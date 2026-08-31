using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Multiresolution sculpt levels over one cage.
///
/// THE ONE RULE THE WHOLE THING RESTS ON: level N+1's rest surface is the subdivision of level N
/// *displaced*, not of level N at rest. Everything else here is bookkeeping around that sentence.
///
/// It is what makes a low-level edit carry the high-level detail. Sculpt a pore at L3, then go
/// back to L1 and pull the jaw out: L1's deltas move the surface L2 and L3 are subdivided from,
/// so the pore rides the jaw. Subdivide the cage a fixed number of times instead and the jaw edit
/// either flattens the pore or leaves it floating off the surface — which is the failure this
/// design exists to avoid, and the reason a sculpt is a stack of levels rather than one dense mesh.
///
/// Level 0 is the cage itself and always exists; it carries deltas like any other level, so a
/// coarse edit is available without adding a level first. <see cref="ViewLevel"/> is display only —
/// dropping it shows fewer levels and DISCARDS NOTHING. Only <see cref="RemoveTopLevel"/> destroys
/// deltas, and it hands them back so the caller can undo it.
///
/// Deltas themselves are <see cref="SculptLayer"/>s in derived <see cref="SculptFrames"/>, so every
/// level rides a cage edit for the same reason a single level does — see <see cref="SetCage"/>.
/// </summary>
public sealed class MultiresSculpt
{
	PolyMesh _cage;
	int _viewLevel;

	readonly List<SculptLayer> _layers = new();

	// Rest mesh and frames per level. Cache ONLY: every entry is a pure function of the cage and
	// the layers below it, so throwing the whole thing away costs time and changes no answer.
	// Both are needed together — a layer is captured and applied through the frames of its own
	// rest mesh, and pairing a layer with anyone else's frames silently bends the sculpt.
	readonly List<PolyMesh> _rest = new();
	readonly List<SculptFrames> _frames = new();

	public MultiresSculpt( PolyMesh cage )
	{
		if ( cage is null )
			throw new ArgumentNullException( nameof( cage ) );

		_cage = cage.Clone();
		_layers.Add( Zero( _cage.VertexCount ) );
	}

	/// <summary>
	/// Bumped by every change that alters the model: adding or removing a level, recording deltas,
	/// setting a layer, re-basing the cage. A feature compares it against the revision it last built
	/// at to know its cached geometry is stale — see Feature.IsStale.
	///
	/// ViewLevel deliberately does NOT bump it. The view is not the model, and a preview level that
	/// forced a rebuild would be both wrong and slow.
	/// </summary>
	public int Revision { get; private set; }

	/// <summary>The highest level that exists. 0 means the cage alone.</summary>
	public int TopLevel => _layers.Count - 1;

	/// <summary>How many levels exist, cage included. Always at least 1.</summary>
	public int LevelCount => _layers.Count;

	/// <summary>Which level is displayed. Purely a view: the levels above it keep their deltas.</summary>
	public int ViewLevel
	{
		get => _viewLevel;
		set
		{
			if ( value < 0 || value > TopLevel )
				throw new ArgumentOutOfRangeException( nameof( value ),
					$"View level {value} does not exist; this sculpt has levels 0 to {TopLevel}." );

			_viewLevel = value;
		}
	}

	/// <summary>The cage, as given. A copy — edit it through <see cref="SetCage"/>.</summary>
	public PolyMesh Cage => _cage.Clone();

	/// <summary>The stored deltas at one level, for persistence and for undo.</summary>
	public SculptLayer LayerAt( int level )
	{
		Validate( level );
		return _layers[level];
	}

	/// <summary>Whether anything has actually been sculpted at this level.</summary>
	public bool HasDetail( int level )
	{
		Validate( level );

		foreach ( var d in _layers[level].Deltas )
		{
			if ( d.LengthSquared > 1e-20f )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Vertex and face count at a level, without building it — what a level slider warns with
	/// before the user asks for something that costs 400k vertices.
	/// </summary>
	public (int Vertices, int Faces) Cost( int level ) => CatmullClark.PredictCost( _cage, level );

	/// <summary>
	/// Add a level above the current top and make it the view. Its deltas start at zero, so the
	/// model does not change shape — the new level is a finer surface over the same shape, which is
	/// the only sane thing for "add a level" to mean.
	/// </summary>
	public int AddLevel()
	{
		var level = _layers.Count;
		EnsureBuilt( level );
		_layers.Add( Zero( _rest[level].VertexCount ) );
		_viewLevel = level;
		Revision++;
		return level;
	}

	/// <summary>
	/// Drop the top level, returning its deltas so the caller can put it back. The only call here
	/// that destroys anything, which is why it hands the evidence over rather than swallowing it.
	/// </summary>
	public SculptLayer RemoveTopLevel()
	{
		if ( _layers.Count == 1 )
			throw new InvalidOperationException(
				"Level 0 is the cage, not a sculpt level, so it cannot be removed." );

		var dropped = _layers[^1];
		_layers.RemoveAt( _layers.Count - 1 );
		Trim( _layers.Count );

		if ( _viewLevel > TopLevel )
			_viewLevel = TopLevel;

		Revision++;
		return dropped;
	}

	/// <summary>
	/// The surface a level is sculpted against: every layer below it applied, then subdivided, but
	/// WITHOUT this level's own deltas. Deltas are measured from here, so this is also what their
	/// frames are built on.
	/// </summary>
	public PolyMesh Rest( int level ) => RestMesh( level ).Clone();

	/// <summary>The level as it looks: <see cref="Rest"/> plus this level's own deltas.</summary>
	public PolyMesh Evaluate( int level )
	{
		Validate( level );
		var mesh = RestMesh( level ).Clone();
		_layers[level].Apply( mesh, FramesFor( level ) );
		return mesh;
	}

	/// <summary>What the viewport shows.</summary>
	public PolyMesh Display() => Evaluate( _viewLevel );

	/// <summary>
	/// Frames of a level's REST surface — what <see cref="Record"/> measures deltas through. A brush
	/// wants the frames of the displaced mesh instead, which is what <see cref="Stroke"/> is for.
	/// </summary>
	public SculptFrames FramesFor( int level )
	{
		Validate( level );
		EnsureBuilt( level );
		return _frames[level];
	}

	/// <summary>
	/// Store a sculpted mesh as this level's deltas. The mesh must be <see cref="Evaluate"/> of that
	/// level after a brush has moved its vertices — same vertices, moved, never added or removed.
	///
	/// Levels above are left exactly as they are and simply re-evaluate against the new surface.
	/// That is the whole feature: this call is what a low-level edit is, and it does nothing to the
	/// detail above it.
	/// </summary>
	public void Record( int level, PolyMesh sculpted )
	{
		Validate( level );

		if ( sculpted is null )
			throw new ArgumentNullException( nameof( sculpted ) );

		var rest = RestMesh( level );

		if ( sculpted.VertexCount != rest.VertexCount )
			throw new ArgumentException(
				$"Level {level} has {rest.VertexCount} vertices and the sculpted mesh has "
				+ $"{sculpted.VertexCount}. A brush moves vertices; it must not add or remove them." );

		_layers[level] = SculptLayer.Capture( rest, sculpted, FramesFor( level ) );
		Trim( level + 1 );
		Revision++;
	}

	/// <summary>
	/// Put deltas straight into a level, without a mesh to measure them from. Persistence reads them
	/// back this way, and so does an undo that kept a whole layer rather than a stroke's diff.
	///
	/// The count has to match what the cage produces at that level, which is why it is checked here
	/// rather than trusted: deltas from the wrong level land on real vertex indices and produce a
	/// mesh that is merely wrong rather than obviously broken.
	/// </summary>
	public void SetLayer( int level, SculptLayer layer )
	{
		Validate( level );

		if ( layer is null )
			throw new ArgumentNullException( nameof( layer ) );

		var expected = RestMesh( level ).VertexCount;

		if ( layer.Count != expected )
			throw new ArgumentException(
				$"Level {level} has {expected} vertices and this layer has {layer.Count}." );

		_layers[level] = layer;
		Trim( level + 1 );
		Revision++;
	}

	/// <summary>
	/// Run a stroke at one level and store the result there. Returns the sparse undo from
	/// <see cref="Brush"/>, which <see cref="Undo"/> takes back.
	///
	/// THIS EXISTS TO GET THE TWO SETS OF FRAMES THE RIGHT WAY ROUND. A brush works on the surface
	/// the user can see, so it takes the frames of the DISPLACED mesh; a delta is measured from the
	/// rest surface, so <see cref="Record"/> captures through the frames of the REST mesh. Both are
	/// correct in their own place, and swapping them is invisible on a fresh level — the two are the
	/// same mesh until something has been sculpted there — and wrong on every level after that.
	///
	/// Of the six brushes only Inflate reads the frames at all; Draw, Grab, Flatten and Pinch take
	/// their direction from the stroke sample, and Smooth from the neighbours. So the bug this
	/// prevents is narrow, and correspondingly easy to leave in.
	/// </summary>
	public BrushUndo Stroke( int level, BrushStroke stroke, float[] mask = null )
	{
		Validate( level );

		if ( stroke is null )
			throw new ArgumentNullException( nameof( stroke ) );

		var displaced = Evaluate( level );
		var undo = Brush.Apply( displaced, stroke, SculptFrames.Build( displaced ), mask );
		Record( level, displaced );

		return undo;
	}

	/// <summary>
	/// Put back what one stroke moved. Sparse, like the undo itself — the alternative is a copy of
	/// every delta per stroke, which at L4 is most of a megabyte for a flick of the wrist.
	/// </summary>
	public void Undo( int level, BrushUndo undo )
	{
		Validate( level );

		if ( undo is null )
			throw new ArgumentNullException( nameof( undo ) );

		var displaced = Evaluate( level );
		undo.Restore( displaced );
		Record( level, displaced );
	}

	/// <summary>
	/// Swap in a rebuilt cage. Every level re-derives its frames from the new surface and every
	/// delta rides the edit — the parametric half of the tool staying editable is the entire point
	/// of storing deltas in a derived frame.
	///
	/// Refuses a cage of different topology rather than misapplying the deltas or silently dropping
	/// them. Reprojection (step 10) is what will eventually make that case work; until it exists a
	/// clear refusal is the honest answer, and the deltas are untouched by it.
	/// </summary>
	public void SetCage( PolyMesh cage )
	{
		if ( cage is null )
			throw new ArgumentNullException( nameof( cage ) );

		if ( !CanRebase( cage, out var why ) )
			throw new InvalidOperationException( why );

		_cage = cage.Clone();
		Trim( 0 );
		Revision++;
	}

	/// <summary>
	/// Whether <see cref="SetCage"/> would accept this cage, and if not, why — in the shape a
	/// diagnostic wants: what stopped it, with both models' numbers, and what would work.
	/// </summary>
	public bool CanRebase( PolyMesh cage, out string why )
	{
		if ( cage is null )
			throw new ArgumentNullException( nameof( cage ) );

		if ( cage.VertexCount != _cage.VertexCount || cage.FaceCount != _cage.FaceCount )
		{
			why = $"The rebuilt cage has {cage.VertexCount} vertices and {cage.FaceCount} faces; this "
				+ $"sculpt was made on {_cage.VertexCount} vertices and {_cage.FaceCount} faces. Deltas "
				+ "are stored per vertex, so they cannot be placed on it. Undo the feature edit that "
				+ "changed the cage's topology, or re-sculpt on the new cage.";
			return false;
		}

		for ( var f = 0; f < cage.FaceCount; f++ )
		{
			var a = _cage.Faces[f].Indices;
			var b = cage.Faces[f].Indices;

			if ( a.Length != b.Length || !SameIndices( a, b ) )
			{
				why = $"The rebuilt cage has the same vertex and face counts but face {f} joins "
					+ "different vertices, so it is a different surface wearing the same numbers. "
					+ "Applying the deltas would put every detail somewhere arbitrary. Undo the feature "
					+ "edit that changed the cage's topology, or re-sculpt on the new cage.";
				return false;
			}
		}

		why = null;
		return true;
	}

	/// <summary>
	/// A stable id for the cage's topology — counts and face indices, deliberately NOT positions,
	/// because positions are exactly what a parametric edit is expected to change. Persistence
	/// (step 6) stores this beside the deltas so a reopened document can tell a moved cage from a
	/// rebuilt one without keeping a copy of the old mesh.
	/// </summary>
	public static long TopologyId( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		// FNV-1a, written out rather than leaning on GetHashCode: this value goes in a file and has
		// to mean the same thing in the next process and on the next runtime.
		const long prime = 0x100000001b3;
		var hash = unchecked((long)0xcbf29ce484222325);

		void Mix( int value )
		{
			for ( var b = 0; b < 4; b++ )
			{
				hash ^= (value >> (b * 8)) & 0xff;
				hash = unchecked(hash * prime);
			}
		}

		Mix( mesh.VertexCount );
		Mix( mesh.FaceCount );

		foreach ( var face in mesh.Faces )
		{
			Mix( face.Count );

			foreach ( var i in face.Indices )
				Mix( i );
		}

		return hash;
	}

	static bool SameIndices( int[] a, int[] b )
	{
		for ( var i = 0; i < a.Length; i++ )
		{
			if ( a[i] != b[i] )
				return false;
		}

		return true;
	}

	static SculptLayer Zero( int vertexCount ) => new( new Vec3[vertexCount] );

	void Validate( int level )
	{
		if ( level < 0 || level > TopLevel )
			throw new ArgumentOutOfRangeException( nameof( level ),
				$"Level {level} does not exist; this sculpt has levels 0 to {TopLevel}." );
	}

	PolyMesh RestMesh( int level )
	{
		Validate( level );
		EnsureBuilt( level );
		return _rest[level];
	}

	/// <summary>Build rest surfaces and frames up to `level`, reusing whatever is still valid.</summary>
	void EnsureBuilt( int level )
	{
		if ( _rest.Count == 0 )
		{
			var cage = _cage.Clone();
			_rest.Add( cage );
			_frames.Add( SculptFrames.Build( cage ) );
		}

		for ( var l = _rest.Count; l <= level; l++ )
		{
			// The one rule, in code: displace the level below, THEN subdivide.
			var below = _rest[l - 1].Clone();
			_layers[l - 1].Apply( below, _frames[l - 1] );

			var rest = CatmullClark.Subdivide( below, 1 );
			_rest.Add( rest );
			_frames.Add( SculptFrames.Build( rest ) );
		}
	}

	/// <summary>Drop the cached surfaces from `level` up; they sit downstream of something that moved.</summary>
	void Trim( int level )
	{
		if ( _rest.Count > level )
			_rest.RemoveRange( level, _rest.Count - level );

		if ( _frames.Count > level )
			_frames.RemoveRange( level, _frames.Count - level );
	}
}
