using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Fewer triangles, same shape. <see cref="SubdivideFeature"/>'s opposite number.
///
/// THE FEATURE THE IMPORTER CREATED THE NEED FOR. Everything the tool built itself arrived at a
/// sensible density because a primitive is a handful of quads and you add detail deliberately. A
/// mesh that came from somewhere else did not: a Meshy generation, a scan, a sculpt exported by
/// whoever made it, all land somewhere between a hundred thousand and a million triangles because
/// nothing upstream had a reason to stop. That mesh is the right SHAPE and the wrong SIZE, and at
/// that size it is not a model you can work on — the pick tree keeps hover usable and nothing keeps
/// subdivision, weight painting, the boolean or the compiler usable. Remesh is the step that turns
/// an import into a part.
///
/// IT COMES BACK AS TRIANGLES. Always, including where quads went in — see <see cref="Decimate"/>
/// for why, and for why the answer is not to pretend otherwise. So this belongs on an import and
/// not on a CAD body: rolling a Remesh over a quad cage you built with sketches and extrudes throws
/// away the topology that made it a cage, and the feature says so rather than letting you find out
/// two workspaces later. The refusal is a warning rather than an error because there are real
/// reasons to want it anyway — a collision hull, a distant LOD — and being told is enough.
///
/// THE TARGET IS IN TRIANGLES, NOT FACES, and that is a distinction the dialog has to keep making
/// because the tree everywhere else counts faces. A 500-face quad cage is 1000 triangles; typing
/// 500 into a triangle budget and getting half of what you meant is the kind of confusion that
/// looks like the algorithm being bad at its job.
/// </summary>
public sealed class RemeshFeature : Feature
{
	public override string TypeName => "Remesh";

	/// <summary>A part, and nothing finer. There is no per-face form of this: the quadrics are
	/// summed over the whole surface and a boundary drawn through the middle of one would be
	/// preserved as a border, which is the opposite of what picking a region would mean.</summary>
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>
	/// Which of the two numbers below is the one being read.
	///
	/// PERCENTAGE FIRST because it is the one you can answer without knowing anything. An import
	/// whose size you have not looked at has no obvious triangle budget, and "keep a tenth" is a
	/// decision you can make on the spot and correct once. A count is what you switch to when the
	/// budget is coming from somewhere else — a platform limit, an LOD ladder, a collision hull.
	/// </summary>
	public readonly ChoiceParam Target = new( "Target", new[] { "Percentage", "Triangle count" } );

	/// <summary>Fraction of the incoming triangles to keep, as a percentage. Read when
	/// <see cref="Target"/> is Percentage.</summary>
	public readonly FloatParam Percent = new( "Keep %", 10f, 0.1f, 100f );

	/// <summary>
	/// Triangles to stop at, per body. PER BODY rather than for the selection as a whole: splitting
	/// one budget across parts means deciding what a part is worth, and the honest version of that
	/// decision is to run the feature twice with two numbers rather than to have it guessed.
	/// </summary>
	public readonly IntParam Triangles = new( "Triangles", 5000, 4, 4_000_000 ) { Slider = false };

	/// <summary>Hold open borders and the lines between materials. See
	/// <see cref="Decimate.Options.PreserveBoundary"/> — off, a shell's opening goes ragged.</summary>
	public readonly BoolParam HoldBorders = new( "Hold borders", true );

	/// <summary>Merge vertices that share a position first. See
	/// <see cref="Decimate.Options.Weld"/> — off, an unwelded export cannot be reduced at all.</summary>
	public readonly BoolParam Weld = new( "Weld first", true );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Target, Percent, Triangles, HoldBorders, Weld };

	/// <summary>The two protections are answered once, if ever. The target is the whole
	/// question.</summary>
	public override IReadOnlyList<IParam> AdvancedParameters =>
		new IParam[] { HoldBorders, Weld };

	/// <summary>
	/// What this will cost, for a dialog that can say "412,908 → 41,290" before you press anything.
	///
	/// The same question <see cref="SubdivideFeature.PredictCost"/> answers, and needed more here:
	/// subdivision's cost is arithmetic anybody can do in their head, and a percentage of a number
	/// you have never seen is not.
	/// </summary>
	public (int From, int To) PredictCost( IEnumerable<Body> bodies )
	{
		var from = 0;
		var to = 0;

		foreach ( var body in bodies.Where( Bodies.Matches ) )
		{
			var count = Decimate.TriangleCount( body.Mesh );

			from += count;
			to += Math.Clamp( TargetFor( count ), 4, count );
		}

		return (from, to);
	}

	int TargetFor( int triangles ) =>
		Target.Index == 1
			? Triangles.Clamped
			: (int)MathF.Round( triangles * Math.Clamp( Percent.Clamped, 0f, 100f ) / 100f );

	protected override void Execute( FeatureContext ctx )
	{
		var targets = ctx.Bodies.Where( Bodies.Matches ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				"Nothing to remesh",
				"The bodies this feature names are not in the model at this point in the tree.",
				"Pick a part and try again",
				"Or clear the selection to remesh every part" );
		}

		var missed = new List<string>();
		var quads = 0;

		foreach ( var body in targets )
		{
			var before = Decimate.TriangleCount( body.Mesh );

			if ( before <= 4 )
				continue;

			// Counted before the reduce, since afterwards everything is triangles and the question
			// cannot be asked any more.
			if ( body.Mesh.Faces.Any( f => f.Count > 3 ) )
				quads++;

			var result = Decimate.Run( body.Mesh, new Decimate.Options
			{
				TargetTriangles = TargetFor( before ),
				Weld = Weld.Value,
				PreserveBoundary = HoldBorders.Value,
			} );

			body.Mesh = result.Mesh;

			if ( !result.ReachedTarget )
				missed.Add( $"{body.Name} stopped at {result.ToTriangles:N0}" );
		}

		// BOTH OF THESE ARE WARNINGS, and the model is built either way. See the class note: a
		// remesh that could not reach its budget still produced a smaller mesh, and a remesh over a
		// quad cage is a thing somebody may have meant.
		if ( missed.Count > 0 )
		{
			Warn(
				missed.Count == 1
					? "The target was not reached"
					: $"{missed.Count} parts did not reach the target",
				"There were no collapses left that keep the surface manifold and the right way out: "
					+ string.Join( ", ", missed ) + ".",
				"Take what it gave you — this is as small as the shape goes without tearing",
				"Or turn off Hold borders, if the part is open and its opening does not matter" );
		}
		else if ( quads > 0 )
		{
			Warn(
				quads == 1 ? "The part came back as triangles" : $"{quads} parts came back as triangles",
				"Remesh reduces a triangle mesh, and the part it ran on had quads in it. Quads are "
					+ "what subdivision and skinning want, so a cage that had them has lost them.",
				"Suppress this feature if the part was a cage rather than an import",
				"Keep it if the triangles are what you wanted — a collision hull, or a distant LOD" );
		}
	}
}
