using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A paint layer in the feature tree.
///
/// WHERE THE PAINT LIVES: strokes in object space, replayed onto whatever the mesh currently is.
/// The texture is a derived artifact — the same bet the rest of the kernel already made by keeping
/// the mesh a function of the feature history. Nothing else in the document holds paint, undo is the
/// feature tree's undo, and a stroke is one entry in the list.
///
/// EXECUTE DOES NOT REPLAY YET. The dab — faces in radius, rasterise, falloff-weighted blend — is a
/// later piece. For now this feature only guards the door: it checks that the body's UVs can carry
/// paint at all, and warns rather than fails when they cannot, because the model still built and the
/// warning is the thing a user acts on.
///
/// ITS STALENESS GUARD IS COPIED FROM SculptFeature FOR THE SAME REASON. A paint session appends
/// strokes nowhere near the studio, so nothing calls MarkDirty and the rebuild would happily reuse
/// the cached body from before the stroke — the paint would stop following the brush, which reads as
/// "the paint tool does nothing" rather than as a caching bug. A revision counter bumped when the
/// stroke list changes, compared here to what the last rebuild built from, is the guard.
/// </summary>
public sealed class PaintFeature : Feature
{
	public override string TypeName => "Paint";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Body" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies };

	/// <summary>
	/// The strokes, in the order they were painted.
	///
	/// NULL UNTIL THE FIRST STROKE LANDS, the same "not yet populated" idiom SculptFeature uses for
	/// its levels. A never-painted feature serialises to nothing at all: StudioDocument writes a null
	/// field as absent, and the reflection sweep in DocumentTests round-trips a null list as null.
	/// </summary>
	public List<PaintStroke> Strokes;

	/// <summary>Bumped each time the stroke list changes, so <see cref="IsStale"/> can notice
	/// without anyone remembering to call MarkDirty.</summary>
	public int Revision { get; private set; }

	// The revision this feature last built from. See the class comment — a stroke lands nowhere near
	// the studio, so nothing calls MarkDirty and this is what catches it.
	int _builtRevision = -1;

	public override bool IsStale => Revision != _builtRevision;

	/// <summary>Append a stroke and mark the feature stale, so the next rebuild replays it. The list
	/// is lazily created here so a fresh feature never has to check for null before painting.</summary>
	public void AddStroke( PaintStroke stroke )
	{
		(Strokes ??= new()).Add( stroke );
		Revision++;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );

		// A WARNING, NOT A REFUSAL. The paint is fine; the UVs are the problem. A body whose UVs
		// overlap or escape the square cannot carry the paint, and it will scramble rather than fail
		// once the dab exists. Telling the user now is the difference between a model that says what
		// is wrong and one that quietly paints both islands at once.
		foreach ( var body in targets )
		{
			var coverage = NormalBake.Measure( body.Mesh );

			if ( coverage.CanBake )
				continue;

			Warn(
				"This body's UVs cannot carry paint",
				coverage.Problem,
				"Insert a UV project feature in Unwrap mode above this one",
				"Repaint after the UVs are fixed" );
		}

		// Last, so a failure above leaves the feature stale and the next rebuild tries again.
		_builtRevision = Revision;
	}
}
