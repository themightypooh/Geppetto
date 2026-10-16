using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// A body over a stick figure typed as text (a skin modifier, or ZSpheres): joints with a thickness each, limbs between them, any
/// number of limbs at a joint. <see cref="SkeletonCage"/> makes the all-quad cage (hull joints,
/// untwisted rings), Catmull-Clark smooths it, and Fit pulls it onto the exact radii.
///
/// THE STICK FIGURE IS CHAINS, one per `;`, each a run of joints separated by `|`, each joint
/// `x,y,z radius`. Joints in the same place (within Weld) are one joint, so a branch is a chain
/// that starts where another has a joint:
///
///   0,0,40 5 | 0,0,62 6 | 0,0,74 4.5        spine and head
///   0,0,40 5 | 6,0,20 2.6 | 6,0,2 2          a leg out of the pelvis
///   0,0,62 6 | 20,0,60 2 | 31,0,58 1.5       an arm out of the chest
///
/// The skin is one surface on one material slot. The result is an ordinary body: edit it, sculpt it, rig it. It has no UVs yet.
/// </summary>
public sealed class SkinFeature : Feature
{
	public override string TypeName => "Skin";

	public readonly StringParam Bones = new( "Bones", "0,0,0 3 | 0,0,20 3" );
	public readonly IntParam Sides = new( "Sides", 8, 4, 32 );
	public readonly IntParam Smooth = new( "Smooth levels", 2, 0, 4 );
	public readonly BoolParam Fit = new( "Fit to radii", true );
	public readonly FloatParam Fillet = new( "Fillet", -1f, -1f, unit: "u" );
	public readonly FloatParam Weld = new( "Weld", 0.05f, 0f, unit: "u" );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bones, Sides, Smooth, Fit, Fillet, Weld, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Fillet, Weld, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var graph = Parse( Bones.Value ?? "", Weld.Clamped );

		if ( graph.Edges.Count == 0 )
		{
			FailOn( "Bones",
				"There is no limb to build on",
				"A chain needs two joints or more, separated by |.",
				"Write a chain: 0,0,0 3 | 0,0,20 3" );
		}

		if ( Sides.Clamped % 2 != 0 )
		{
			FailOn( "Sides", Sides.Clamped + 1,
				"Sides must be even",
				$"A ring of {Sides.Clamped} cannot be capped with quads.",
				$"Set Sides to {Sides.Clamped + 1}" );
		}

		PolyMesh mesh;
		SkeletonCage.Report report;
		try
		{
			mesh = SkeletonCage.Build( graph, new SkeletonCage.Options
			{
				Sides = Sides.Clamped,
				SmoothLevels = Smooth.Clamped,
				Fit = Fit.Value,
				FitBlend = Fillet.Value,
			}, out report );
		}
		catch ( InvalidOperationException e )
		{
			FailOn( "Bones", "The stick figure cannot be skinned", e.Message, "Change the chain it names" );
			return;
		}

		foreach ( var f in mesh.Faces )
			f.Material = Material.Clamped;

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );

		Warning = $"{mesh.FaceCount:N0} quads over {graph.Edges.Count} limb(s)"
			+ (report.BranchJoints > 0 ? $", {report.BranchJoints} branching joint(s) (most limbs at one: {report.MaxBranches})" : "")
			+ ". It has no UVs yet: add a UV project (Unwrap) before baking.";
	}

	/// <summary>The chains as a stick figure. Bad text fails on the Bones field, naming the chain.</summary>
	internal static SkeletonCage.Graph Parse( string text, float weld )
	{
		var graph = new SkeletonCage.Graph();
		var chains = text.Split( ';' ).Select( s => s.Trim() ).Where( s => s.Length > 0 ).ToList();

		for ( var c = 0; c < chains.Count; c++ )
		{
			var prev = -1;
			foreach ( var joint in chains[c].Split( '|' ).Select( s => s.Trim() ) )
			{
				var words = joint.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
				if ( words.Length != 2
					|| !ModelMeasure.TryParseVec3( words[0], out var p )
					|| !float.TryParse( words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var r ) )
				{
					FailOn( "Bones", $"Chain {c + 1} cannot be read",
						$"'{joint}' is not a joint; a joint is a point and a radius, like 0,0,10 2.",
						"Fix that joint, or remove it" );
					return graph;
				}

				if ( !(r > 0f) )
				{
					FailOn( "Bones", $"Chain {c + 1} has a joint with no thickness",
						$"'{joint}' has radius {r}.", "Give every joint a radius above zero" );
				}

				var n = graph.AddNode( p, r, MathF.Max( weld, 1e-4f ) );
				if ( prev >= 0 )
					graph.AddEdge( prev, n );
				prev = n;
			}
		}

		return graph;
	}
}
