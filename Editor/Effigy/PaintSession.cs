using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The paint tool, with no cursor in it.
///
/// WHY THIS IS IN THE KERNEL, the same reason SculptSession is: everything a paint tool does between
/// the pointer and the mesh is arithmetic — project a ray, decide whether the cursor has moved far
/// enough to earn a sample, drop a dab, record it as a stroke. All of it is testable with no engine
/// anywhere, and all of it is where the bugs are. What is left for the editor is genuinely thin: hand
/// this rays, upload <see cref="Canvas"/>, and draw a ring at <see cref="Hover"/>.
///
/// THE SESSION PAINTS TEXELS, NOT VERTICES. A dab stamps coverage into a per-stroke buffer rather
/// than blending straight into the canvas; the canvas the editor reads is recomposed from the
/// committed strokes plus the stroke in flight. That is what makes holding the brush still a no-op
/// instead of a darkening — the in-flight stroke's coverage is the maximum its dabs reached, so the
/// same dab re-stamped over and over recomposes to the same texels. A stroke ends as a
/// <see cref="PaintStroke"/> the caller appends to the feature; undo is the feature tree's undo, so
/// this session carries no undo stack.
///
/// ERASING IS THE SAME STROKE MACHINERY, not a second mode with its own path. <see cref="Erasing"/>
/// decides which kind the next stroke is, the dabs and the coverage buffer are identical either way,
/// and only the final application differs. So an erase costs one undo step like any stroke, saves
/// into the document like any stroke, and replays in its place in the log — which is what stops the
/// paint underneath it from coming back on the next rebuild.
/// </summary>
public sealed class PaintSession
{
	readonly PolyMesh _mesh;
	readonly MeshBVH _bvh;
	readonly List<int> _found = new();
	readonly int _resolution;

	// Two canvases, because the live canvas must be recomposable from a base. _committed holds every
	// finished stroke and never shows the one in flight; Canvas is _committed with the in-flight
	// stroke composited over it, rebuilt over the dab's bounds each time a dab lands. The editor
	// uploads Canvas, not _committed.
	// One per channel, made on first use: a session that only ever paints colour holds one canvas,
	// as it always did.
	readonly PaintCanvas[] _committedByChannel = new PaintCanvas[Enum.GetValues<PaintChannel>().Length];

	PaintCanvas _committed => Committed( _channel );

	PaintCanvas Committed( PaintChannel channel ) =>
		_committedByChannel[(int)channel] ??= new PaintCanvas( _resolution, _resolution );

	PaintChannel _channel = PaintChannel.Color;

	// The in-flight stroke's per-texel coverage — the maximum any dab of this stroke reached. Cleared
	// on BeginStroke, stamped by each dab, composited into _committed once on EndStroke.
	readonly float[] _coverage;

	// Live only between BeginStroke and EndStroke.
	PaintStroke _current;
	Vec3 _lastSample;
	byte _strokeR, _strokeG, _strokeB;

	// Brush settings. Floats in 0..1, the same units PaintStroke stores, so a stroke committed here
	// and one read back from the document describe the same colour.
	public float R = 1f;
	public float G = 1f;
	public float B = 1f;
	public float A = 1f;

	/// <summary>Brush radius in world units, not pixels — the kernel has no screen.</summary>
	public float Radius = 0.1f;

	/// <summary>How hard the stroke presses, 0..1. One is full opacity, half is a lighter dab.</summary>
	public float Strength = 1f;

	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>How far the cursor must travel before it earns another sample, as a fraction of the
	/// radius. A pointer produces events far faster than a brush needs them; this is what keeps a slow
	/// drag from biting far harder than a quick one for the same gesture.</summary>
	public float Spacing = 0.5f;

	/// <summary>Most samples one pointer move may be split into, so a drag across the whole model in
	/// one frame under-samples rather than stalls.</summary>
	public int MaxSamplesPerMove = 64;

	/// <summary>
	/// Whether the next stroke ERASES rather than paints.
	///
	/// A SESSION FLAG RATHER THAN SOMETHING READ PER DAB, the same shape and the same reason as
	/// <see cref="SculptSession.Inverted"/>: the editor sets it from a held modifier at the moment a
	/// stroke begins and leaves it alone for the rest of that stroke, so letting go of the key
	/// halfway through a gesture cannot turn half of one mark into the other kind. The stroke carries
	/// its own copy from here, so changing this afterwards does not rewrite what was already painted.
	/// </summary>
	public bool Erasing;

	/// <summary>
	/// The channel the next stroke paints, and the one <see cref="Canvas"/> shows. Switching rebuilds
	/// the visible canvas from that channel's committed strokes; switching mid-stroke is refused,
	/// for the same reason <see cref="Erasing"/> is copied at the press.
	/// </summary>
	public PaintChannel Channel
	{
		get => _channel;
		set
		{
			if ( value == _channel )
				return;

			if ( IsStroking )
				throw new InvalidOperationException( "Finish the stroke before switching channel." );

			_channel = value;
			RefreshCanvas();
		}
	}

	/// <summary>The value a scalar channel (roughness, metallic, height, mask) paints, 0..1. Separate
	/// from the colour so switching to roughness and back does not lose the colour you picked.</summary>
	public float Value = 1f;

	/// <summary>The body's paint from the layers below this one, shown under the layer while painting.
	/// Null for the bottom layer.</summary>
	public PaintCanvas Below;

	/// <summary>The layer's blend mode and opacity, for the preview — the same ones the rebuild uses.</summary>
	public PaintBlendMode BlendMode = PaintBlendMode.Normal;

	public float Opacity = 1f;

	/// <summary>The layer's generated, shaped mask (null for none) and whether it is inverted. The
	/// painted part of the mask is this session's own Layer Mask strokes.</summary>
	public float[] GeneratedMask;

	public bool InvertMask;

	/// <summary>
	/// Which origin plane every sample mirrors across, or none. Recorded INTO the stroke's path
	/// (the mirrored point joins the real one), so a mirrored stroke survives replay and export the
	/// way a live-only mirror would not. Shared enum — see <see cref="MirrorAxis"/>.
	/// </summary>
	public MirrorAxis Mirror;

	/// <summary>
	/// The strokes committed so far, in order. The caller mirrors each <see cref="EndStroke"/> result
	/// into its feature; this list is the session's own copy, used to rebuild the canvas when a stroke
	/// is cancelled mid-flight.
	/// </summary>
	public readonly List<PaintStroke> Strokes = new();

	/// <summary>The live canvas — committed strokes with the stroke in flight composited over them.
	/// The editor uploads this to a texture; its dirty rect is exactly what a dab touched.</summary>
	public PaintCanvas Canvas { get; }

	public PaintSession( PolyMesh mesh, int resolution, IReadOnlyList<PaintStroke> existing = null )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );

		if ( resolution < 1 )
			throw new ArgumentOutOfRangeException( nameof( resolution ) );

		_resolution = resolution;
		_coverage = new float[resolution * resolution];
		Canvas = new PaintCanvas( resolution, resolution );

		// Built once and never refitted: unlike a sculpt stroke, a paint stroke moves no geometry, so
		// the tree stays valid for the life of the session. Paint is strictly cheaper than sculpt here.
		_bvh = MeshBVH.Build( mesh );

		if ( existing is { Count: > 0 } )
		{
			foreach ( var stroke in existing )
			{
				Strokes.Add( stroke );
				CommitStroke( stroke );
			}

			// Open on the channel last painted, so re-entering a roughness layer shows roughness.
			_channel = existing[^1].Channel;
		}

		RefreshCanvas();
	}

	public bool IsStroking => _current is not null;

	/// <summary>
	/// A starting radius that suits this model: a twelfth of the diagonal, the same argument
	/// SculptSession makes — Effigy's units are dimensionless, so a fixed default is the whole model
	/// on one part and invisible on the next. Unlike the vertex-colour brush there is no spacing floor
	/// here: a texel dab needs no vertices to reach, so a bare box paints fine at any radius.
	/// </summary>
	public float SuggestedRadius
	{
		get
		{
			var diagonal = _mesh.BoundsDiagonal;
			return diagonal > 1e-6f ? diagonal / 12f : 0.25f;
		}
	}

	/// <summary>The mesh the strokes land on, exposed so the editor can build a preview from it —
	/// the same surface the brush works on, which is the one the user is looking at.</summary>
	public PolyMesh Mesh => _mesh;

	/// <summary>The resolution the canvas is rasterised at, for an editor that must size a texture.</summary>
	public int Resolution => _resolution;

	/// <summary>Where the cursor sits on the surface, or null if the ray missed. The editor draws its
	/// ring here; nothing about it changes the canvas.</summary>
	public MeshHit? Hover( Vec3 origin, Vec3 direction )
	{
		var dir = direction.Normal;

		if ( dir.LengthSquared < 0.5f )
			return null;

		return _bvh.Raycast( _mesh, origin, dir );
	}

	/// <summary>
	/// Start a stroke. Returns false if the ray missed — clicking past the model deselects and must
	/// not begin a stroke that lands somewhere surprising. The first dab is applied here, so a single
	/// click leaves a mark rather than nothing.
	/// </summary>
	public bool BeginStroke( Vec3 origin, Vec3 direction )
	{
		if ( IsStroking )
			throw new InvalidOperationException( "A stroke is already running; end it before starting another." );

		if ( Radius <= 0f )
			throw new InvalidOperationException( $"A brush needs a radius; this one is {Radius}." );

		var dir = direction.Normal;
		var hit = _bvh.Raycast( _mesh, origin, dir );

		if ( hit is null )
			return false;

		_current = new PaintStroke
		{
			R = R,
			G = G,
			B = B,
			A = A,
			Radius = Radius,
			Strength = Strength,
			Falloff = Falloff,
			Spacing = Spacing,

			// Copied at the press and never re-read, so a modifier released mid-gesture does not
			// turn the back half of one mark into the other kind. See Erasing.
			Erase = Erasing,

			Channel = _channel,
		};

		// A scalar channel paints its Value as a grey, carried in R/G/B so the one replay path
		// serves every channel. See PaintStroke.Channel.
		if ( _channel != PaintChannel.Color )
			_current.R = _current.G = _current.B = Math.Clamp( Value, 0f, 1f );

		(_strokeR, _strokeG, _strokeB) = PaintReplay.StrokeBytes( _current );

		_lastSample = hit.Value.Point;

		Array.Clear( _coverage, 0, _coverage.Length );

		AddSample( hit.Value.Point, hit.Value.Normal );

		return true;
	}

	/// <summary>
	/// Carry the stroke to a new pointer position. Returns how many samples it produced: zero when the
	/// cursor has not travelled far enough, and several when it travelled far enough that one would
	/// leave a gap. A ray that misses the model does NOT end the stroke — dragging off the silhouette
	/// and back on is ordinary.
	/// </summary>
	public int MoveTo( Vec3 origin, Vec3 direction )
	{
		if ( !IsStroking )
			throw new InvalidOperationException( "No stroke is running." );

		var dir = direction.Normal;
		var hit = _bvh.Raycast( _mesh, origin, dir );

		if ( hit is null )
			return 0;

		var target = hit.Value.Point;
		var travelled = (target - _lastSample).Length;
		var spacing = MathF.Max( Radius * Spacing, 1e-6f );

		if ( travelled < spacing )
			return 0;

		// Fill the gap. The pointer's real path between two events is unknowable, so this walks the
		// straight line between them — what the gesture looked like at this sampling rate. The normal
		// is the current hit's for the whole segment, the same choice SculptSession makes.
		var steps = Math.Min( (int)(travelled / spacing), MaxSamplesPerMove );

		for ( var i = 1; i <= steps; i++ )
		{
			var t = (float)i / steps;
			var point = _lastSample + (target - _lastSample) * t;

			AddSample( point, hit.Value.Normal );
		}

		_lastSample = target;

		return steps;
	}

	/// <summary>Finish the stroke and commit it to the session's list, returning it so the caller can
	/// add it to the feature. Null if the stroke had no points.</summary>
	public PaintStroke EndStroke()
	{
		if ( !IsStroking )
			throw new InvalidOperationException( "No stroke is running." );

		var stroke = _current;
		_current = null;

		if ( stroke.Path.Count == 0 )
		{
			Array.Clear( _coverage, 0, _coverage.Length );
			return null;
		}

		Strokes.Add( stroke );

		// Apply the stroke ONCE, as whichever kind it is. The live canvas already shows it (it was
		// recomposed from this very coverage over the committed base), so applying the same coverage
		// to _committed leaves the two canvases in agreement and nothing to re-upload.
		PaintReplay.Apply( _committed, stroke, _coverage );
		Array.Clear( _coverage, 0, _coverage.Length );

		return stroke;
	}

	/// <summary>
	/// Abandon the stroke in flight. The committed canvas never absorbed its dabs, so abandoning is a
	/// rebuild of the visible canvas from the committed one — cheaper than tracking per-texel undo,
	/// and the same answer the document itself would give.
	/// </summary>
	public void CancelStroke()
	{
		_current = null;
		Array.Clear( _coverage, 0, _coverage.Length );
		RefreshCanvas();
	}

	/// <summary>
	/// Reset the session to a different stroke list — undo/redo's route in.
	///
	/// The document restore only rewrites the feature's stroke list; the session's canvas is its
	/// own copy and does not change with it. Leaving it would make the next stroke resurrect paint
	/// the undo just removed. So this drops any stroke in flight, adopts the new list, and replays it
	/// from scratch — the same path <see cref="CancelStroke"/> walks.
	/// </summary>
	public void Reload( IReadOnlyList<PaintStroke> strokes )
	{
		_current = null;

		Strokes.Clear();

		if ( strokes is not null )
			Strokes.AddRange( strokes );

		foreach ( var canvas in _committedByChannel )
			canvas?.Clear();

		foreach ( var stroke in Strokes )
			CommitStroke( stroke );

		Array.Clear( _coverage, 0, _coverage.Length );
		RefreshCanvas();
	}

	/// <summary>Replay one committed stroke into the committed canvas. Uses the live coverage buffer
	/// as its scratch — BeginStroke clears it, and this only ever runs between strokes.</summary>
	void CommitStroke( PaintStroke stroke )
	{
		Array.Clear( _coverage, 0, _coverage.Length );
		PaintReplay.StampStroke( stroke, _mesh, _bvh, _coverage, _resolution, _found );
		PaintReplay.Apply( Committed( stroke.Channel ), stroke, _coverage );
	}

	/// <summary>Copy the committed canvas into the visible one and mark the whole thing dirty, so the
	/// next upload sends everything. Used when the canvas is rebuilt wholesale — a cancel or a reload.</summary>
	void RefreshCanvas()
	{
		Array.Copy( _committed.Rgba, Canvas.Rgba, _committed.Rgba.Length );
		Canvas.Invalidate();
	}

	/// <summary>One sample onto the stroke and the coverage, mirrored across the chosen plane when
	/// <see cref="Mirror"/> is set. The mirrored point is written into the path alongside the real one,
	/// so the mirror is part of the stroke's own record rather than a live-only effect that a rebuild
	/// would drop.</summary>
	void AddSample( Vec3 point, Vec3 normal )
	{
		_current.Path.Add( new PaintStrokePoint( point, normal ) );
		Recompose( Stamp( point, normal ) );

		if ( Mirror == MirrorAxis.None )
			return;

		var mirroredPoint = Brush.Mirror( point, Mirror );
		var mirroredNormal = Brush.Mirror( normal, Mirror );

		_current.Path.Add( new PaintStrokePoint( mirroredPoint, mirroredNormal ) );
		Recompose( Stamp( mirroredPoint, mirroredNormal ) );
	}

	/// <summary>Stamp one dab's coverage and return the bounds it touched, so the caller recomposes
	/// the visible canvas over exactly that region.</summary>
	TexelBounds Stamp( Vec3 point, Vec3 normal ) =>
		PaintReplay.StampDab( _mesh, _bvh, _coverage, _resolution,
			point, normal, Radius, Strength * A, Falloff, _found );

	/// <summary>
	/// Rebuild the visible canvas over a texel region: each texel becomes the committed colour with
	/// the in-flight stroke applied over it at its running coverage. Reading the base from _committed
	/// rather than from the canvas's current value is what stops overlapping dabs within one stroke
	/// from stacking — and it is what lets an erase preview at all, since the texels it is taking
	/// away have to come from somewhere once they are gone from the visible canvas.
	/// </summary>
	void Recompose( TexelBounds bounds )
	{
		if ( !bounds.Any )
			return;

		// Read once rather than per texel. Both branches end at the same arithmetic the replay uses,
		// so the mark on screen and the mark after a rebuild are the same mark.
		var erasing = _current is { Erase: true };
		var committed = _committed;

		for ( var y = bounds.MinY; y <= bounds.MaxY; y++ )
		{
			for ( var x = bounds.MinX; x <= bounds.MaxX; x++ )
			{
				var i = (y * _resolution + x) * 4;
				var coverage = _coverage[y * _resolution + x];

				if ( erasing )
				{
					Canvas.Write( x, y,
						committed.Rgba[i], committed.Rgba[i + 1], committed.Rgba[i + 2],
						PaintCanvas.DestinationOut( committed.Rgba[i + 3], coverage ) );

					continue;
				}

				var (r, g, b, a) = PaintCanvas.SourceOver(
					committed.Rgba[i], committed.Rgba[i + 1], committed.Rgba[i + 2], committed.Rgba[i + 3],
					_strokeR, _strokeG, _strokeB, coverage );

				Canvas.Write( x, y, r, g, b, a );
			}
		}
	}

	/// <summary>
	/// What the viewport should show for a texel region, as opaque RGBA into <paramref name="dest"/>
	/// (<c>w * h * 4</c>): the active channel of the layers below, this layer over them with its blend
	/// mode, opacity and mask — the same arithmetic the rebuild uses, so what you see while painting
	/// is what the layer will be. Painting the Layer Mask shows the mask itself, white shown and
	/// black hidden, because a mask you cannot see is a mask you cannot paint.
	/// </summary>
	public void BakePreview( byte[] dest, int x, int y, int w, int h )
	{
		var maskCanvas = _channel == PaintChannel.Mask ? Canvas : _committedByChannel[(int)PaintChannel.Mask];
		var below = _channel == PaintChannel.Mask ? null : Below?.Channel( _channel );

		if ( below is not null && (below.Width != _resolution || below.Height != _resolution) )
			below = null;

		var baseByte = PaintReplay.ToByte( PaintChannels.Base( _channel ) );
		byte br = baseByte, bg = baseByte, bb = baseByte;

		if ( _channel == PaintChannel.Color )
			(br, bg, bb) = (PaintMaterial.DefaultBaseR, PaintMaterial.DefaultBaseG, PaintMaterial.DefaultBaseB);

		var o = 0;

		for ( var yy = y; yy < y + h; yy++ )
		{
			for ( var xx = x; xx < x + w; xx++ )
			{
				var t = yy * _resolution + xx;
				var i = t * 4;

				var m = GeneratedMask is not null && GeneratedMask.Length == _resolution * _resolution ? GeneratedMask[t] : 1f;

				if ( maskCanvas is not null )
				{
					var a = maskCanvas.Rgba[i + 3] / 255f;
					m = m * (1f - a) + maskCanvas.Rgba[i] / 255f * a;
				}

				if ( InvertMask )
					m = 1f - m;

				if ( _channel == PaintChannel.Mask )
				{
					var g = PaintReplay.ToByte( m );
					dest[o] = g;
					dest[o + 1] = g;
					dest[o + 2] = g;
					dest[o + 3] = 255;
					o += 4;
					continue;
				}

				byte r = br, gg = bg, b = bb;

				if ( below is not null && below.Rgba[i + 3] > 0 )
					(r, gg, b, _) = PaintCanvas.SourceOver( r, gg, b, 255, below.Rgba[i], below.Rgba[i + 1], below.Rgba[i + 2], below.Rgba[i + 3] / 255f );

				var sa = Canvas.Rgba[i + 3] / 255f * Math.Clamp( Opacity, 0f, 1f ) * m;
				(r, gg, b, _) = PaintLayers.Over( r, gg, b, 255, Canvas.Rgba[i], Canvas.Rgba[i + 1], Canvas.Rgba[i + 2], sa, BlendMode );

				dest[o] = r;
				dest[o + 1] = gg;
				dest[o + 2] = b;
				dest[o + 3] = 255;
				o += 4;
			}
		}
	}
}
