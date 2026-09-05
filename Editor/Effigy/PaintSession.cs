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
/// this rays, show <see cref="Colors"/>, and draw a ring at <see cref="Hover"/>.
///
/// THE SESSION PAINTS VERTEX COLOURS DIRECTLY. A new dab composites straight onto the colour array,
/// no replay, which is exactly as cheap as painting should be. Replaying the whole stroke list is the
/// REBUILD path (<see cref="PaintReplay.ReplayColors"/>), not the stroke path. A stroke ends as a
/// <see cref="PaintStroke"/> the caller appends to the feature; undo is the feature tree's undo, so
/// this session carries no undo stack.
/// </summary>
public sealed class PaintSession
{
	readonly PolyMesh _mesh;
	readonly MeshBVH _bvh;
	readonly Vec3[] _normals;
	readonly List<int> _found = new();

	// Live only between BeginStroke and EndStroke.
	PaintStroke _current;
	Vec3 _lastSample;

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
	/// Mirror every sample across X — the cheap symmetry that covers most of what symmetry is for,
	/// and the same flag SculptSession carries for the same reason. Recorded INTO the stroke's path
	/// (the mirrored point joins the real one), so a mirrored stroke survives replay and export the
	/// way a live-only mirror would not.
	/// </summary>
	public bool MirrorX;

	/// <summary>
	/// The strokes committed so far, in order. The caller mirrors each <see cref="EndStroke"/> result
	/// into its feature; this list is the session's own copy, used to rebuild the colours when a stroke
	/// is cancelled mid-flight.
	/// </summary>
	public readonly List<PaintStroke> Strokes = new();

	/// <summary>The per-vertex colours, straight RGBA in 0..1, parallel to <see cref="Mesh"/>'s
	/// positions. The editor reads this to colour the model.</summary>
	public Vec4[] Colors { get; }

	public PaintSession( PolyMesh mesh, IReadOnlyList<PaintStroke> existing = null )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );

		Colors = new Vec4[mesh.VertexCount];

		// Built once and never refitted: unlike a sculpt stroke, a paint stroke moves no geometry, so
		// the tree stays valid for the life of the session. Paint is strictly cheaper than sculpt here.
		_bvh = MeshBVH.Build( mesh );
		_normals = mesh.ComputeVertexNormals();

		if ( existing is { Count: > 0 } )
		{
			foreach ( var stroke in existing )
			{
				Strokes.Add( stroke );
				PaintReplay.PaintStrokeColors( stroke, _mesh, _bvh, _normals, Colors, _found );
			}
		}
	}

	public bool IsStroking => _current is not null;

	/// <summary>
	/// A starting radius that suits this model: a twelfth of the diagonal, the same argument
	/// SculptSession makes — Effigy's units are dimensionless, so a fixed default is the whole model
	/// on one part and invisible on the next.
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

	/// <summary>Where the cursor sits on the surface, or null if the ray missed. The editor draws its
	/// ring here; nothing about it changes the colours.</summary>
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
		};

		_lastSample = hit.Value.Point;

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
			return null;

		Strokes.Add( stroke );
		return stroke;
	}

	/// <summary>
	/// Abandon the stroke in flight. The colours have already absorbed its dabs, so abandoning is a
	/// rebuild from the committed strokes rather than a removal — cheaper than tracking per-vertex
	/// undo, and the same answer the document itself would give.
	/// </summary>
	public void CancelStroke()
	{
		_current = null;

		Array.Clear( Colors, 0, Colors.Length );

		foreach ( var stroke in Strokes )
			PaintReplay.PaintStrokeColors( stroke, _mesh, _bvh, _normals, Colors, _found );
	}

	/// <summary>
	/// Reset the session to a different stroke list — undo/redo's route in.
	///
	/// The document restore only rewrites the feature's stroke list; the session's colour array is its
	/// own copy and does not change with it. Leaving it would make the next stroke resurrect colours
	/// the undo just removed. So this drops any stroke in flight, adopts the new list, and replays it
	/// from scratch — the same path <see cref="CancelStroke"/> walks.
	/// </summary>
	public void Reload( IReadOnlyList<PaintStroke> strokes )
	{
		_current = null;

		Strokes.Clear();

		if ( strokes is not null )
			Strokes.AddRange( strokes );

		Array.Clear( Colors, 0, Colors.Length );

		foreach ( var stroke in Strokes )
			PaintReplay.PaintStrokeColors( stroke, _mesh, _bvh, _normals, Colors, _found );
	}

	/// <summary>One sample onto the stroke and the colours, mirrored across X when
	/// <see cref="MirrorX"/> is on. The mirrored point is written into the path alongside the real one,
	/// so the mirror is part of the stroke's own record rather than a live-only effect that a rebuild
	/// would drop.</summary>
	void AddSample( Vec3 point, Vec3 normal )
	{
		_current.Path.Add( new PaintStrokePoint( point, normal ) );
		Dab( point, normal );

		if ( !MirrorX )
			return;

		var mirroredPoint = new Vec3( -point.x, point.y, point.z );
		var mirroredNormal = new Vec3( -normal.x, normal.y, normal.z );

		_current.Path.Add( new PaintStrokePoint( mirroredPoint, mirroredNormal ) );
		Dab( mirroredPoint, mirroredNormal );
	}

	void Dab( Vec3 point, Vec3 normal )
	{
		PaintReplay.DabColors( _mesh, _bvh, _normals, Colors, point, normal,
			Radius, Strength * A, Falloff, R, G, B, _found );
	}
}
