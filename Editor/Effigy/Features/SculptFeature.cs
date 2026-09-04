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
