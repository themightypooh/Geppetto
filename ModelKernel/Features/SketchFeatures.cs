using System;
using System.Collections.Generic;
using System.Linq;

namespace ModelKernel;

/// <summary>
/// A sketch parameter. The generic dialog can't render this as a number box — it needs the
/// viewport, because you draw it. It implements IParam anyway so a panel can lay out a row for it
/// with an "Edit sketch" button, exactly the way Onshape's own sketch references behave.
/// </summary>
public sealed class SketchParam : IParam
{
	public string Label { get; }
	public Sketch Value;

	public SketchParam( string label, Sketch value = null )
	{
		Label = label;
		Value = value ?? new Sketch();
	}
}

/// <summary>
/// Extrude a sketch into a solid. The other way into the modeller, next to PrimitiveFeature.
///
/// Onshape's Extrude carries the sketch by reference and re-reads it on every rebuild, which is
/// what makes "edit the sketch, watch the solid follow" work. Same here: the feature holds the
/// sketch and rebuilds from its points, so nothing is baked at the moment of creation.
/// </summary>
public sealed class ExtrudeFeature : Feature
{
	public override string TypeName => "Extrude";

	public readonly SketchParam Sketch = new( "Sketch" );
	public readonly FloatParam Distance = new( "Distance", 1f, unit: "u" );
	public readonly BoolParam Symmetric = new( "Symmetric about the plane", false );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Sketch, Distance, Symmetric, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var mesh = SketchSolids.Extrude( Sketch.Value, Distance.Value, Symmetric.Value, Material.Clamped );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}
}

/// <summary>
/// Spin a sketch around an axis. Everything round that isn't a cylinder — bottles, lamp bases,
/// chess pieces, pipe fittings.
/// </summary>
public sealed class RevolveFeature : Feature
{
	public override string TypeName => "Revolve";

	public readonly SketchParam Sketch = new( "Sketch" );
	public readonly Vec3Param Axis = new( "Axis (point x, point y, direction angle°)", new Vec3( 0, 0, 90f ) );
	public readonly FloatParam Degrees = new( "Angle", 360f, -360f, 360f, "deg" );
	public readonly IntParam Segments = new( "Segments", 16, 3, 512 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Sketch, Axis, Degrees, Segments, Material };

	protected override void Execute( FeatureContext ctx )
	{
		// The axis is packed into one Vec3Param as (point x, point y, direction in degrees) so the
		// generic dialog can show it today. A real sketch UI would pick the axis by clicking a line,
		// and this parameter becomes a reference instead — the maths below does not change.
		var point = new Vec2( Axis.Value.x, Axis.Value.y );
		var radians = Axis.Value.z * MathF.PI / 180f;
		var direction = new Vec2( MathF.Cos( radians ), MathF.Sin( radians ) );

		var mesh = SketchSolids.Revolve( Sketch.Value, point, direction, Degrees.Value, Segments.Clamped, Material.Clamped );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}
}

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
/// Bevel every edge of the selected bodies. "Round off", in the language a non-modeller uses.
///
/// Every edge, not a selection, because the kernel has no edge selection yet — that arrives with a
/// viewport. Bevelling everything is the common case for a prop anyway: it is what stops a model
/// reading as programmer art, because nothing real has a perfectly sharp edge.
/// </summary>
public sealed class BevelFeature : Feature
{
	public override string TypeName => "Bevel";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Distance = new( "Distance", 0.1f, 0.0001f, unit: "u" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Distance };

	protected override void Execute( FeatureContext ctx )
	{
		var targets = ctx.Bodies.Where( Bodies.Matches ).ToList();

		// Build everything before assigning anything, so a failure partway leaves the bodies as they
		// were — the same contract ShellFeature had to be fixed to honour.
		var bevelled = new List<PolyMesh>( targets.Count );

		foreach ( var body in targets )
			bevelled.Add( BevelOperation.Bevel( body.Mesh, Distance.Clamped ) );

		for ( var i = 0; i < targets.Count; i++ )
			targets[i].Mesh = bevelled[i];
	}
}
