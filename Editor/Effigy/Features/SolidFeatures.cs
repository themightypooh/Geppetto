using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

// Features that reshape solids after they exist, whatever made them - a primitive, an extrude, a
// revolve. Kept apart from SketchFeatures deliberately: these care about meshes, not about where
// the mesh came from, and separating them keeps two people working on the two halves out of each
// other's way.

/// <summary>
/// Hollow the selected bodies to a wall thickness. The "make a room" feature.
///
/// OpenFaces is an index list because there is no face selection in the kernel yet — a viewport
/// would set it by clicking. Left empty, the result is a sealed hollow solid, which is what you
/// want for something that only needs to be light rather than enterable.
/// </summary>
public sealed class ShellFeature : Feature
{
	public override string TypeName => "Shell";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Thickness = new( "Wall thickness", 0.1f, 0.0001f, unit: "u" );

	/// <summary>
	/// Face indices to leave open. Not an IParam — a viewport sets this by picking, and a numeric
	/// list in a dialog would be unusable.
	///
	/// These indices are applied to EVERY selected body, which only makes sense when one body is
	/// selected. That is the normal case for a room, and the alternative — per-body face sets —
	/// needs a selection model the kernel does not have yet.
	/// </summary>
	public readonly List<int> OpenFaces = new();

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Thickness };

	protected override void Execute( FeatureContext ctx )
	{
		var targets = ctx.Bodies.Where( Bodies.Matches ).ToList();

		// Shell everything before assigning anything. Feature.Run promises that a failed feature
		// leaves the bodies as they were, and mutating in place breaks that promise the moment the
		// third body of four throws — you get a half-shelled model and an error message.
		var shelled = new List<PolyMesh>( targets.Count );

		foreach ( var body in targets )
			shelled.Add( ShellOperation.Shell( body.Mesh, Thickness.Clamped, OpenFaces ) );

		for ( var i = 0; i < targets.Count; i++ )
			targets[i].Mesh = shelled[i];
	}
}

/// <summary>
/// Flat chamfer along every edge sharper than the angle threshold. Onshape's Fillet, minus the
/// round — this is the Segments=1 case, a straight cut rather than an arc. See Bevel.cs for why
/// that is the whole algorithm rather than a special case of one.
/// </summary>
public sealed class BevelFeature : Feature
{
	public override string TypeName => "Bevel";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Width = new( "Width", 0.1f, 0.0001f, unit: "u" );
	public readonly FloatParam AngleThreshold = new( "Angle threshold", 15f, 0f, 180f, unit: "deg" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Width, AngleThreshold };

	protected override void Execute( FeatureContext ctx )
	{
		foreach ( var body in ctx.Bodies.Where( Bodies.Matches ) )
			body.Mesh = Bevel.Apply( body.Mesh, Width.Clamped, AngleThreshold.Clamped );
	}
}

/// <summary>
/// Re-project UVs across the selected bodies. Onshape has no equivalent because it does not care
/// about textures; every game-facing modeller needs one.
///
/// Placed as a feature rather than an export option on purpose: where it sits in the tree decides
/// what it sees. Before a bevel it projects the sharp cage and the chamfer strips inherit
/// interpolated UVs; after a bevel it projects the chamfers as their own faces.
/// </summary>
public sealed class UVProjectFeature : Feature
{
	public override string TypeName => "UV project";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Mode = new( "Mode", new[] { "Box", "Planar" } );
	public readonly Vec3Param Direction = new( "Direction", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam Scale = new( "Units per tile", 1f, 0.0001f, unit: "u" );

	public override IReadOnlyList<IParam> Parameters => Mode.Value == "Planar"
		? new IParam[] { Bodies, Mode, Direction, Scale }
		: new IParam[] { Bodies, Mode, Scale };

	protected override void Execute( FeatureContext ctx )
	{
		foreach ( var body in ctx.Bodies.Where( Bodies.Matches ) )
		{
			if ( Mode.Value == "Planar" )
				UVProjection.PlanarProject( body.Mesh, Direction.Value, Scale.Clamped );
			else
				UVProjection.BoxProject( body.Mesh, Scale.Clamped );
		}
	}
}

