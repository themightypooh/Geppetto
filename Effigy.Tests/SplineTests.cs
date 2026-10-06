using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Splines, judged by what a modeller asks of them: the curve goes through the points you
/// clicked, a tube along it is a closed solid, a closed loop meets itself, and a mesh bent along
/// the curve lands on it.
/// </summary>
public static class SplineTests
{
	public static void Run()
	{
		Section( "splines: through the points, tubes are solid, bends land on the curve" );
		TestTheCurvePassesThroughEveryControlPoint();
		TestAStraightSplineIsThePolyline();
		TestATubeIsAClosedSolid();
		TestAClosedTubeHasNoSeamAndNoCaps();
		TestATaperedTubeIsThinnerAtTheEnd();
		TestBendingABoxAlongACurveFollowsIt();
		TestTheFeaturesSaveAndLoad();
		TestSweepTakesASpline();
	}

	static Spline3 Zigzag() => new()
	{
		Points = new List<Vec3> { new( 0, 0, 0 ), new( 4, 3, 0 ), new( 8, 0, 1 ), new( 12, 3, 2 ) },
	};

	static void TestTheCurvePassesThroughEveryControlPoint()
	{
		var spline = Zigzag();
		var path = spline.Sample( 8 );

		Check( "an open spline of four points samples to 3 × 8 + 1 points", path.Count == 25, $"{path.Count}" );

		var all = spline.Points.All( p => path.Any( q => (q - p).Length < 1e-4f ) );
		Check( "every control point is on the sampled curve", all );

		// No overshoot: a centripetal Catmull-Rom stays inside the hull of its neighbours, so the
		// curve never leaves the box the control points span.
		var inside = path.All( q => q.x >= -1e-3f && q.x <= 12.001f && q.y >= -1e-3f && q.y <= 3.001f );
		Check( "the curve stays inside the box of its control points", inside );
	}

	static void TestAStraightSplineIsThePolyline()
	{
		var spline = Zigzag();
		spline.Smooth = false;

		var path = spline.Sample( 8 );
		Check( "a straight spline is exactly its control points", path.Count == 4 && path.SequenceEqual( spline.Points ) );
	}

	static void TestATubeIsAClosedSolid()
	{
		var path = Zigzag().Sample( 6 );
		var mesh = SplineTube.Build( path, closed: false, radius: 0.5f, endRadius: 0.5f, sides: 8, twistDegrees: 0f, material: 0 );

		var check = MeshValidator.Validate( mesh );
		Check( "an open tube with caps is a closed manifold", check.IsClosed, $"boundary {check.BoundaryEdges}, non-manifold {check.NonManifoldEdges}" );
		Check( "it has a ring per station plus the two caps", mesh.FaceCount == (path.Count - 1) * 8 + 2, $"{mesh.FaceCount}" );

		// Every ring vertex is a radius away from its station.
		var ok = true;
		for ( var i = 0; i < path.Count; i++ )
			for ( var k = 0; k < 8; k++ )
				ok &= MathF.Abs( (mesh.Positions[i * 8 + k] - path[i]).Length - 0.5f ) < 1e-3f;

		Check( "every ring vertex sits one radius from its station", ok );
	}

	static void TestAClosedTubeHasNoSeamAndNoCaps()
	{
		var spline = new Spline3
		{
			Closed = true,
			Points = new List<Vec3> { new( 0, 0, 0 ), new( 5, 0, 1 ), new( 5, 5, 0 ), new( 0, 5, -1 ) },
		};

		var path = spline.Sample( 6 );
		Check( "a closed spline samples with no repeated first point", path.Count == 24, $"{path.Count}" );

		var mesh = SplineTube.Build( path, closed: true, radius: 0.4f, endRadius: 0.4f, sides: 6, twistDegrees: 0f, material: 0 );
		var check = MeshValidator.Validate( mesh );

		Check( "a closed tube is a torus: manifold with no boundary", check.IsClosed, $"boundary {check.BoundaryEdges}, non-manifold {check.NonManifoldEdges}" );
		Check( "and it has no cap faces", mesh.FaceCount == path.Count * 6, $"{mesh.FaceCount}" );

		// The frames meet: the last ring's first vertex sits within a ring's step of the first
		// ring's first vertex, rather than a half turn off. That is the mismatch spread.
		var frames = Spline3.Frames( path, closed: true );
		var lu = frames[^1].U;
		var u0 = frames[0].U;
		var stepAngle = MathF.Acos( Math.Clamp( Vec3.Dot( lu, u0 ), -1f, 1f ) );
		Check( "the frame carried round a closed loop meets its start", stepAngle < MathF.PI * 2f / path.Count + 0.2f, $"{stepAngle:0.###} rad" );
	}

	static void TestATaperedTubeIsThinnerAtTheEnd()
	{
		var path = Zigzag().Sample( 4 );
		var mesh = SplineTube.Build( path, closed: false, radius: 1f, endRadius: 0.25f, sides: 8, twistDegrees: 0f, material: 0 );

		var first = (mesh.Positions[0] - path[0]).Length;
		var last = (mesh.Positions[(path.Count - 1) * 8] - path[^1]).Length;

		Check( "the first ring has the start radius", MathF.Abs( first - 1f ) < 1e-3f, $"{first:0.###}" );
		Check( "the last ring has the end radius", MathF.Abs( last - 0.25f ) < 1e-3f, $"{last:0.###}" );
	}

	static void TestBendingABoxAlongACurveFollowsIt()
	{
		// A long thin box along X, bent along a quarter circle in the XY plane: its far end
		// should land at the curve's far end, turned to face along Y.
		var box = Primitives.Box( 10f, 1f, 1f );
		var path = new List<Vec3>();
		for ( var i = 0; i <= 16; i++ )
		{
			var a = i / 16f * MathF.PI * 0.5f;
			path.Add( new Vec3( MathF.Sin( a ) * 10f, 10f - MathF.Cos( a ) * 10f, 0f ) );
		}

		CurveDeform.Apply( box, path, closed: false, axis: 0, stretch: true );

		var maxY = box.Positions.Max( p => p.y );
		var maxX = box.Positions.Max( p => p.x );

		Check( "the bent box reaches the end of the arc", maxY > 9.5f && maxX > 9.5f && maxX < 11f, $"max x {maxX:0.##}, max y {maxY:0.##}" );

		// Faces still face outward: a bend is a smooth map, so no face should have flipped.
		var check = MeshValidator.Validate( box );
		Check( "the bent box is still a closed manifold", check.IsClosed );
	}

	static void TestTheFeaturesSaveAndLoad()
	{
		var studio = new PartStudio();
		var spline = studio.Add( new SplineFeature() );
		spline.Points.AddRange( Zigzag().Points );
		spline.Radius.Value = 0.75f;
		spline.Sides.Value = 10;

		var box = studio.Add( new PrimitiveFeature() );
		var bend = studio.Add( new CurveDeformFeature() );
		bend.Bodies.BodyIds.Clear();
		studio.Rebuild();

		Check( "a spline feature with points builds a tube", spline.Error is null && studio.Bodies.Count >= 1, spline.Error ?? "" );
		Check( "bend along curve finds the spline above it", bend.Error is null, bend.Error ?? "" );

		var text = StudioDocument.Write( studio );
		var back = StudioDocument.Read( text );
		var loaded = back.Features.OfType<SplineFeature>().First();

		Check( "the spline's points survive a save and load", loaded.Points.Count == 4 && (loaded.Points[1] - new Vec3( 4, 3, 0 )).Length < 1e-4f, $"{loaded.Points.Count} points" );
		Check( "and so do its tube settings", MathF.Abs( loaded.Radius.Value - 0.75f ) < 1e-5f && loaded.Sides.Value == 10 );

		back.Rebuild();
		Check( "the loaded document builds the same number of bodies", back.Bodies.Count == studio.Bodies.Count, $"{back.Bodies.Count} vs {studio.Bodies.Count}" );
	}

	static void TestSweepTakesASpline()
	{
		var studio = new PartStudio();
		var profile = studio.Add( new SketchFeature() );
		profile.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var path = studio.Add( new SplineFeature() );
		path.Tube.Value = false;
		path.Points.AddRange( new[] { new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 4 ), new Vec3( 3, 0, 7 ), new Vec3( 6, 0, 7 ) } );

		var sweep = studio.Add( new SweepFeature() );
		studio.Rebuild();

		Check( "a sweep with one sketch and a spline uses the spline as its path", sweep.Error is null, sweep.Error ?? "" );
		Check( "and it builds a solid", studio.Bodies.Count == 1 && MeshValidator.Validate( studio.Bodies[0].Mesh ).IsClosed );
	}
}
