using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A sculpt in the feature tree.
///
/// It consumes one body the way ShellFeature does and replaces its mesh with the sculpted one, so
/// everything downstream — export, rigging, the boolean — sees the finished surface and does not
/// need to know a sculpt happened.
///
/// WHERE IT GOES IN THE HISTORY: on the cage, in place of a Subdivide. The levels ARE the
/// subdivision, and putting a Subdivide underneath would hand this feature a dense mesh as its cage
/// and give up the thing the whole design is for — a coarse cage you can still edit parametrically.
///
/// ITS PARAMETERS ARE NOT PARAMETERS. Every other feature in this tree is a handful of numbers, and
/// the generic dialog renders them from <see cref="Parameters"/>. This one's state is megabytes of
/// per-vertex deltas: it belongs to a brush, not to a text box, and it goes to a side-car blob
/// rather than into the document. That is why <see cref="_sculpt"/> is private — StudioDocument
/// saves PUBLIC fields by reflection and throws on anything it cannot write, so a public one here
/// would either break every save or quietly serialise a megabyte of decimal digits into a format
/// whose whole virtue is being readable. Persistence goes through
/// <see cref="SaveDeltas"/>/<see cref="LoadDeltas"/> and <see cref="SculptSidecar"/>, and there is a
/// test that the round trip actually carries the sculpt — the reflection sweep in DocumentTests
/// cannot cover this one, so something else has to.
///
/// WHAT IT OUTPUTS is the top level, always. <see cref="MultiresSculpt.ViewLevel"/> is an editing
/// convenience and deliberately does not reach the model: dropping to L1 to work coarsely must not
/// quietly export an L1 model. Blender draws the same line as separate viewport and render levels,
/// and if this ever needs the cheaper preview it should be that pair rather than one level doing
/// both jobs.
/// </summary>
public sealed class SculptFeature : Feature
{
	public override string TypeName => "Sculpt";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Body" );

	/// <summary>
	/// When the cage's topology changes, resample the sculpt onto the new one instead of refusing.
	///
	/// OFF BY DEFAULT, AND THAT IS THE IMPORTANT PART. Refusing is right nearly always: the usual
	/// cause of a changed cage is an edit somebody did not mean, and the refusal keeps the deltas
	/// so undoing it brings the sculpt back exactly. Reprojection is lossy and cannot be undone by
	/// undoing the upstream edit — the original deltas are gone once it has run. So it is a thing
	/// you turn on having decided the edit was deliberate, not a thing that quietly happens.
	/// </summary>
	public readonly BoolParam Reproject = new( "Reproject if the cage changes", false );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Reproject };

	MultiresSculpt _sculpt;

	// Bytes read from a side-car, waiting for a cage. A blob cannot become a sculpt without one, and
	// the cage does not exist until the features above this have run, so loading is finished by the
	// first rebuild rather than at load time.
	byte[] _pending;

	/// <summary>The levels and their deltas, once a rebuild has given them a cage. Null before that.</summary>
	public MultiresSculpt Sculpt => _sculpt;

	/// <summary>Whether this feature is carrying deltas that have not been placed on a cage yet.</summary>
	public bool HasPendingDeltas => _pending is not null;

	// The sculpt revision this feature last built geometry from. A brush mutates the levels through
	// Sculpt, nowhere near the studio, so nothing calls MarkDirty and the rebuild would happily reuse
	// the cached body from before the stroke - the model would stop following the brush, which reads
	// as "the sculpt tool does nothing" rather than as a caching bug.
	int _builtRevision = -1;

	/// <summary>True once the levels have been changed since the last rebuild. See Feature.IsStale.</summary>
	public override bool IsStale => _pending is not null || (_sculpt is not null && _sculpt.Revision != _builtRevision);

	/// <summary>This sculpt as bytes, or null if there is nothing to save yet.</summary>
	public byte[] SaveDeltas() => _sculpt is null ? _pending : SculptBlob.Write( _sculpt );

	/// <summary>Take bytes from a side-car. They are read at the next rebuild, not now.</summary>
	public void LoadDeltas( byte[] blob ) => _pending = blob;

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );

		if ( targets.Count != 1 )
		{
			Fail(
				"A sculpt works on one body at a time",
				$"This feature's selection matches {targets.Count} bodies. Deltas are stored per vertex "
				+ "against one cage, so there is no meaning to spreading them over several.",
				"Pick a single body in the selection" );
		}

		var body = targets[0];

		if ( _pending is not null )
		{
			// Kept on failure, never dropped. A cage that stopped matching is usually one edit
			// upstream from matching again, and throwing the deltas away would make that unrecoverable.
			MultiresSculpt loaded;

			try
			{
				loaded = SculptBlob.Read( _pending, body.Mesh );
			}
			catch ( System.Exception e )
			{
				Fail(
					"This sculpt does not fit the body underneath it",
					e.Message,
					"Undo the edit that changed the cage's topology",
					"Delete this feature to start a new sculpt on the current cage" );
				return;
			}

			_sculpt = loaded;
			_pending = null;
		}
		else if ( _sculpt is null )
		{
			_sculpt = new MultiresSculpt( body.Mesh );
		}
		else if ( !_sculpt.CanRebase( body.Mesh, out var why ) )
		{
			if ( !Reproject.Value )
			{
				// The deltas are untouched by this — SetCage is never reached, so undoing the upstream
				// edit brings the sculpt back exactly.
				Fail(
					"The cage under this sculpt changed shape",
					why,
					"Undo the edit that changed the cage's topology",
					"Turn on \"Reproject if the cage changes\" to resample the sculpt onto it, losing detail",
					"Delete this feature to start a new sculpt on the current cage" );
			}

			_sculpt = SculptReprojection.Reproject( _sculpt, body.Mesh, out var report );

			// A WARNING, NOT SILENCE. The model still built, so this is not an error — but what came
			// out is an approximation of what was there, the original deltas are gone, and undoing
			// the upstream edit will not bring them back. Saying nothing here would make a lossy
			// step indistinguishable from a lossless one.
			Warn(
				"The sculpt was resampled onto a new cage",
				$"{why} It was reprojected instead: {report}.",
				"Detail finer than the new cage cannot be recovered",
				"The level structure is gone — everything landed in the top level",
				"Undo now if this was not the intention; the original deltas are no longer held" );
		}
		else
		{
			_sculpt.SetCage( body.Mesh );
		}

		body.Mesh = _sculpt.Evaluate( _sculpt.TopLevel );

		// Last, so that a failure above leaves the feature stale and the next rebuild tries again.
		_builtRevision = _sculpt.Revision;
	}
}

/// <summary>
/// Direct polygon edits in the feature tree — what the Modeling workspace's Edit mode commits.
///
/// Same shape as <see cref="SculptFeature"/>: it consumes one body and replaces its mesh, and its
/// real state is a mesh rather than a handful of numbers, so it lives in a side-car
/// (<see cref="MeshEditSidecar"/>) and not in the readable document.
///
/// WHAT IS STORED IS THE RESULT, NOT THE STEPS. Replaying extrudes and loop cuts by element index
/// would be a parametric history on top of a parametric history, and it breaks the moment the body
/// underneath gains a face. So the edited mesh is kept whole, together with a fingerprint of the
/// body it was made from.
///
/// WHEN THE BODY ABOVE CHANGES, IT REFUSES AND KEEPS THE EDIT, exactly as Sculpt refuses a changed
/// cage: the usual cause is an upstream edit somebody did not mean, and undoing it brings this
/// back untouched. <see cref="KeepIfChanged"/> is the deliberate way past that — it outputs the
/// edit anyway and says so, because an edit made on the old body no longer follows the new one.
/// </summary>
public sealed class MeshEditFeature : Feature
{
	public override string TypeName => "Mesh edit";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Body" );

	public readonly BoolParam KeepIfChanged = new( "Keep this edit if the body above changes", false );

	// The live modifiers — see MeshModifiers. Saved like any other parameter; the edit itself
	// stays the cage, and these run on the way out.
	public readonly BoolParam MirrorX = new( "Mirror across X", false );
	public readonly IntParam ArrayCount = new( "Copies along X", 1, 1, 64 );
	public readonly FloatParam ArrayGap = new( "Gap between copies", 0f, 0f );
	public readonly IntParam SubdivideLevels = new( "Subdivide", 0, 0, 3 );
	public readonly FloatParam SolidifyThickness = new( "Thickness", 0f, 0f );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, KeepIfChanged };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { MirrorX, ArrayCount, ArrayGap, SubdivideLevels, SolidifyThickness };

	/// <summary>True when any live modifier would change the mesh.</summary>
	public bool HasModifiers => MirrorX.Value || ArrayCount.Clamped > 1 || SubdivideLevels.Clamped > 0 || SolidifyThickness.Value > 0f;

	/// <summary>Edges the Subdivide modifier keeps sharp, with their sharpness — set in the edit,
	/// saved with it. See <see cref="MeshEditSession.Creases"/>.</summary>
	public Dictionary<EdgeKey, float> Creases { get; private set; } = new();

	/// <summary>The live modifiers applied to <paramref name="mesh"/> — what this feature outputs
	/// for it, and what the editor shows over the cage while you edit.</summary>
	public PolyMesh ApplyModifiers( PolyMesh mesh ) => ApplyModifiers( mesh, Creases );

	/// <summary>The modifiers with creases from elsewhere — the live edit's, which are ahead of
	/// the ones committed here until the edit finishes.</summary>
	public PolyMesh ApplyModifiers( PolyMesh mesh, IReadOnlyDictionary<EdgeKey, float> creases ) => HasModifiers
		? MeshModifiers.Apply( mesh, MirrorX.Value, ArrayCount.Clamped, ArrayGap.Value, SubdivideLevels.Clamped, SolidifyThickness.Value, creases )
		: mesh.Clone();

	PolyMesh _edited;
	List<PolyMesh> _pieces = new();
	long _baseFingerprint;
	byte[] _pending;
	int _revision;
	int _builtRevision = -1;
	PolyMesh _lastInput;

	/// <summary>The mesh this feature outputs, or null before anything was committed.</summary>
	public PolyMesh Edited => _edited;

	/// <summary>Faces separated off during the edit, each output as a body of its own after this one.</summary>
	public IReadOnlyList<PolyMesh> Pieces => _pieces;

	/// <summary>The body as it arrived at the last rebuild — what an edit session starts from when
	/// there is no edit yet. Null before the first rebuild.</summary>
	public PolyMesh LastInput => _lastInput;

	/// <summary>The id of the body this edit replaced at the last rebuild, so the editor can tell
	/// it apart from the others (snapping targets everything else). A property, not a field:
	/// StudioDocument serialises public fields, and this is derived.</summary>
	public string LastBodyId { get; private set; }

	public bool HasEdit => _edited is not null || _pending is not null;

	/// <summary>Bumped by every <see cref="Commit"/>, so the studio rebuilds without a MarkDirty —
	/// the same trick SculptFeature plays with its revision.</summary>
	public int Revision => _revision;

	public override bool IsStale => _pending is not null || _revision != _builtRevision;

	/// <summary>
	/// Hand the feature an edited mesh. <paramref name="basedOn"/> is the body the session started
	/// from — normally <see cref="LastInput"/>. The mesh is cloned: the session keeps editing its own.
	/// </summary>
	public void Commit( PolyMesh edited, PolyMesh basedOn, IEnumerable<PolyMesh> pieces = null, IEnumerable<KeyValuePair<EdgeKey, float>> creases = null )
	{
		if ( edited is null )
			throw new System.ArgumentNullException( nameof( edited ) );
		if ( basedOn is null )
			throw new System.ArgumentNullException( nameof( basedOn ) );

		_edited = edited.Clone();
		Creases = new Dictionary<EdgeKey, float>();
		if ( creases is not null )
			foreach ( var (key, weight) in creases )
				Creases[key] = weight;
		_pieces = new List<PolyMesh>();
		if ( pieces is not null )
			foreach ( var piece in pieces )
				_pieces.Add( piece.Clone() );
		_baseFingerprint = Fingerprint( basedOn );
		_pending = null;
		_revision++;
	}

	/// <summary>Forget the edit, so the body passes through unchanged.</summary>
	public void Clear()
	{
		_edited = null;
		Creases = new Dictionary<EdgeKey, float>();
		_pieces = new List<PolyMesh>();
		_pending = null;
		_revision++;
	}

	public byte[] SaveMesh() => _edited is null ? _pending : MeshEditBlob.Write( _edited, _baseFingerprint, _pieces, Creases );

	public void LoadMesh( byte[] blob )
	{
		_pending = blob;
		_revision++;
	}

	/// <summary>
	/// Topology AND positions. Topology alone would miss a resized box, and an edit made on a 2-inch
	/// box output unchanged on top of a 4-inch one is exactly the silent wrong answer this exists to
	/// refuse. Positions are quantised to 1/1024 inch so float noise from an identical rebuild does
	/// not count as a change.
	/// </summary>
	public static long Fingerprint( PolyMesh mesh )
	{
		const long prime = 0x100000001b3;
		var hash = MultiresSculpt.TopologyId( mesh );

		void Mix( int value )
		{
			for ( var b = 0; b < 4; b++ )
			{
				hash ^= (value >> (b * 8)) & 0xff;
				hash = unchecked(hash * prime);
			}
		}

		foreach ( var p in mesh.Positions )
		{
			Mix( (int)System.MathF.Round( p.x * 1024f ) );
			Mix( (int)System.MathF.Round( p.y * 1024f ) );
			Mix( (int)System.MathF.Round( p.z * 1024f ) );
		}

		return hash;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );

		if ( targets.Count != 1 )
		{
			Fail(
				"A mesh edit works on one body at a time",
				$"This feature's selection matches {targets.Count} bodies. The edit is one mesh, made from one body.",
				"Pick a single body in the selection" );
		}

		var body = targets[0];
		_lastInput = body.Mesh.Clone();
		LastBodyId = body.Id;

		if ( _pending is not null )
		{
			try
			{
				_edited = MeshEditBlob.Read( _pending, out _baseFingerprint, out var pieces, out var creases );
				_pieces = pieces;
				Creases = creases;
			}
			catch ( System.Exception e )
			{
				// Kept, never dropped: a blob this build cannot read is still somebody's work.
				Fail(
					"This mesh edit could not be read",
					e.Message,
					"Delete this feature to start a new edit on the current body" );
				return;
			}

			_pending = null;
		}

		if ( _edited is null )
		{
			// Nothing committed yet: pass the body through. A freshly added edit must not change
			// anything until somebody actually edits — beyond the modifiers somebody switched on.
			if ( HasModifiers )
				body.Mesh = ApplyModifiers( body.Mesh );

			_builtRevision = _revision;
			return;
		}

		if ( Fingerprint( body.Mesh ) != _baseFingerprint )
		{
			if ( !KeepIfChanged.Value )
			{
				Fail(
					"The body under this mesh edit changed",
					"The edit was made on a different version of this body, so it no longer lines up with it.",
					"Undo the change above this feature",
					"Turn on \"Keep this edit if the body above changes\" to use the edit as it is",
					"Delete this feature to edit the current body from scratch" );
			}

			Warn(
				"This mesh edit ignores changes above it",
				"The body above changed after the edit was made. The edit is used as it was, so those changes do not show.",
				"Delete this feature and edit again to pick the changes up" );
		}

		body.Mesh = ApplyModifiers( _edited );

		// Separated pieces come out as bodies of their own, named after the one they came from.
		// Ids come from the context, so they are the same ids on every rebuild.
		for ( var i = 0; i < _pieces.Count; i++ )
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{body.Name} piece {i + 1}", _pieces[i].Clone() ) );

		_builtRevision = _revision;
	}
}
