using System;
using System.Collections.Generic;

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
