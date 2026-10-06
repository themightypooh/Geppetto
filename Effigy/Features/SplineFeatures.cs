using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A curve drawn in the viewport — click the points, the curve runs through them — and, when
/// asked, the tube along it.
///
/// ONE FEATURE, TWO JOBS, on purpose. A spline that only ever existed as a path would need a
/// second feature to become a cable, and "draw a cable" is the thing people come here for; a
/// tube that could not also be used as a bare path would need the curve drawn twice for a
/// sweep or a bend. So this publishes the curve for anything downstream (Bend along curve,
/// Sweep) AND builds the tube when Tube is on — off, it is a path and nothing more.
///
/// The points are NOT parameters: they are drawn, not typed, and the dialog shows the radius
/// and the sides while the viewport owns the curve. Saved as a vec3s line like a sketch's
/// region seeds.
/// </summary>
public sealed class SplineFeature : Feature
{
	public override string TypeName => "Spline";

	public List<Vec3> Points = new();

	public readonly BoolParam Closed = new( "Closed loop", false );
	public readonly BoolParam Smooth = new( "Smooth", true );
	public readonly BoolParam Tube = new( "Make a tube", true );
	public readonly FloatParam Radius = new( "Radius", 2f, 0.0001f, unit: "u" );
	public readonly FloatParam Taper = new( "End radius ×", 1f, 0f, 8f );
	public readonly IntParam Sides = new( "Sides", 12, 3, 64 );
	public readonly FloatParam Twist = new( "Twist", 0f, unit: "deg" );
	public readonly IntParam Resolution = new( "Points per segment", 8, 1, 64 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Closed, Smooth, Tube, Radius, Taper, Sides, Twist, Resolution, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Resolution, Material };

	/// <summary>The curve as this feature would publish it, for the viewport to draw while it is
	/// being edited without waiting on a rebuild.</summary>
	public Spline3 ToSpline() => new() { Points = Points, Closed = Closed.Value, Smooth = Smooth.Value };

	protected override void Execute( FeatureContext ctx )
	{
		var spline = new Spline3
		{
			Points = new List<Vec3>( Points ),
			Closed = Closed.Value,
			Smooth = Smooth.Value,
		};

		ctx.Splines[Id] = spline;
		ctx.LastSplineId = Id;

		// Fewer than two points is a spline being drawn, not a broken one. Nothing is built and
		// nothing goes red; the warning says what the next click does.
		if ( Points.Count < 2 )
		{
			Warning = "Click two or more points in the viewport to draw the curve.";
			return;
		}

		if ( !Tube.Value )
			return;

		var path = spline.Sample( Resolution.Clamped );

		if ( Closed.Value && path.Count < 3 )
		{
			Warning = "A closed loop needs three or more points.";
			return;
		}

		var radius = Radius.Clamped;
		var mesh = SplineTube.Build( path, Closed.Value, radius, radius * Taper.Clamped, Sides.Clamped, Twist.Value, Material.Clamped );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}
}

/// <summary>
/// Bend a body along a spline: the body's X (or Y, or Z) runs along the curve, and its other two
/// axes ride in the curve's frame. A straight tail becomes a curled one; a straight rope
/// segment follows the path it was drawn over. The curve modifier, as a feature in the history.
/// </summary>
public sealed class CurveDeformFeature : Feature
{
	public override string TypeName => "Bend along curve";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bend" );

	/// <summary>The spline to follow. Empty means the one published last — the usual case, where
	/// the curve was drawn just before this was added.</summary>
	public string SplineId = "";

	public readonly ChoiceParam Axis = new( "Along the body's", new[] { "X", "Y", "Z" } );
	public readonly BoolParam Stretch = new( "Fit the length to the curve", true );
	public readonly IntParam Resolution = new( "Points per segment", 8, 1, 64 );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Axis, Stretch, Resolution };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Resolution };

	protected override void Execute( FeatureContext ctx )
	{
		var spline = ResolveSpline( ctx );
		var path = spline.Sample( Resolution.Clamped );

		if ( path.Count < 2 )
		{
			Fail(
				"The spline has fewer than two points.",
				"A curve needs two points before anything can bend along it.",
				"Click more points on the spline above this" );
		}

		foreach ( var body in RequireBodies( ctx, Bodies ) )
			CurveDeform.Apply( body.Mesh, path, spline.Closed, Axis.Index, Stretch.Value );
	}

	Spline3 ResolveSpline( FeatureContext ctx )
	{
		if ( !string.IsNullOrEmpty( SplineId ) && ctx.Splines.TryGetValue( SplineId, out var named ) )
			return named;

		if ( ctx.LastSplineId is not null && ctx.Splines.TryGetValue( ctx.LastSplineId, out var last ) )
			return last;

		Fail(
			"There is no spline above this to bend along.",
			"Bend along curve follows a Spline feature earlier in the history, and none has run yet.",
			"Add a Spline first and draw the path",
			"Move this feature below the spline" );

		return null;
	}
}
