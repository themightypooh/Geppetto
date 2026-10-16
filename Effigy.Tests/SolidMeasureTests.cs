using System;

namespace Effigy.Tests;

/// <summary>
/// Distances and angles on a finished solid. The viewport only draws what this returns.
/// </summary>
public static class SolidMeasureTests
{
	public static void Run()
	{
		Report.Section( "measure: opposite faces of a box are its depth" );
		TestParallelFaces();

		Report.Section( "measure: a box corner is 90 degrees" );
		TestFaceAngle();

		Report.Section( "measure: a 45 degree ramp over a floor" );
		TestRamp();

		Report.Section( "measure: an edge, a corner, and two parallel edges" );
		TestEdges();

		Report.Section( "measure: a point against a face" );
		TestPoint();

		Report.Section( "measure: the same face twice is not a measurement" );
		TestSameFace();
	}

	static int FaceAlong( PolyMesh mesh, Vec3 normal )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[i] ), normal ) > 0.9f )
				return i;
		}

		return -1;
	}

	static void TestParallelFaces()
	{
		var mesh = Primitives.Box( 10f, 4f, 6f );
		var top = FaceAlong( mesh, new Vec3( 0, 0, 1 ) );
		var bottom = FaceAlong( mesh, new Vec3( 0, 0, -1 ) );

		var ok = SolidMeasure.Try( mesh, SolidMeasure.Target.Face( bottom ), mesh, SolidMeasure.Target.Face( top ), out var reading );

		Report.Check( "the two caps are 6 in apart", ok && !reading.Degrees && MathF.Abs( reading.Value - 6f ) < 1e-3f, $"{reading.Value}" );
		Report.Check( "the leader runs from one cap to the other", ok && MathF.Abs( (reading.To - reading.From).Length - 6f ) < 1e-3f, $"{reading.From} -> {reading.To}" );
	}

	static void TestFaceAngle()
	{
		var mesh = Primitives.Box( 10f, 4f, 6f );
		var top = FaceAlong( mesh, new Vec3( 0, 0, 1 ) );
		var side = FaceAlong( mesh, new Vec3( 1, 0, 0 ) );

		var ok = SolidMeasure.Try( mesh, SolidMeasure.Target.Face( top ), mesh, SolidMeasure.Target.Face( side ), out var reading );

		Report.Check( "adjacent faces meet at 90", ok && reading.Degrees && MathF.Abs( reading.Value - 90f ) < 0.05f, $"{reading.Value}" );
	}

	static void TestRamp()
	{
		// Floor in the XY plane, and a quad rising 1 in over 1 in of X. The fold is 45°.
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( 0, 0, 0 ) );
		mesh.AddVertex( new Vec3( 0, 1, 0 ) );
		mesh.AddVertex( new Vec3( 1, 1, 0 ) );
		mesh.AddVertex( new Vec3( 1, 0, 0 ) );
		mesh.AddVertex( new Vec3( 1, 1, 1 ) );
		mesh.AddVertex( new Vec3( 1, 0, 1 ) );

		mesh.AddFace( new[] { 0, 3, 2, 1 } );
		mesh.AddFace( new[] { 0, 1, 4, 5 } );

		var ok = SolidMeasure.Try( mesh, SolidMeasure.Target.Face( 0 ), mesh, SolidMeasure.Target.Face( 1 ), out var reading );

		Report.Check( "the ramp meets the floor at 45", ok && reading.Degrees && MathF.Abs( reading.Value - 45f ) < 0.1f, $"{reading.Value}" );
	}

	static void TestEdges()
	{
		var mesh = Primitives.Box( 10f, 4f, 6f );
		var along = SolidMeasure.Target.Edge( 0, 1 );
		var across = SolidMeasure.Target.Edge( 1, 2 );
		var above = SolidMeasure.Target.Edge( 4, 5 );

		var length = SolidMeasure.Try( mesh, along, out var edge );
		Report.Check( "the front bottom edge is 10 in", length && !edge.Degrees && MathF.Abs( edge.Value - 10f ) < 1e-3f, $"{edge.Value}" );

		var corner = SolidMeasure.Try( mesh, along, mesh, across, out var angle );
		Report.Check( "those two edges meet at 90", corner && angle.Degrees && MathF.Abs( angle.Value - 90f ) < 0.05f, $"{angle.Value}" );

		var gap = SolidMeasure.Try( mesh, along, mesh, above, out var distance );
		Report.Check( "the edge above it is 6 in away", gap && !distance.Degrees && MathF.Abs( distance.Value - 6f ) < 1e-3f, $"{distance.Value}" );
	}

	static void TestPoint()
	{
		var mesh = Primitives.Box( 10f, 4f, 6f );
		var top = FaceAlong( mesh, new Vec3( 0, 0, 1 ) );
		var ok = SolidMeasure.Try( mesh, SolidMeasure.Target.Vertex( 0 ), mesh, SolidMeasure.Target.Face( top ), out var reading );

		Report.Check( "a bottom corner is 6 in from the top", ok && !reading.Degrees && MathF.Abs( reading.Value - 6f ) < 1e-3f, $"{reading.Value}" );

		var span = SolidMeasure.Try( mesh, SolidMeasure.Target.Vertex( 0 ), mesh, SolidMeasure.Target.Vertex( 6 ), out var diagonal );
		var expect = MathF.Sqrt( 10f * 10f + 4f * 4f + 6f * 6f );
		Report.Check( "opposite corners are the space diagonal", span && MathF.Abs( diagonal.Value - expect ) < 1e-3f, $"{diagonal.Value} vs {expect}" );
	}

	static void TestSameFace()
	{
		var mesh = Primitives.Box( 2f, 2f, 2f );
		var top = FaceAlong( mesh, new Vec3( 0, 0, 1 ) );
		var ok = SolidMeasure.Try( mesh, SolidMeasure.Target.Face( top ), mesh, SolidMeasure.Target.Face( top ), out _ );

		Report.Check( "clicking one face twice refuses", !ok );
		Report.Check( "a face on its own is not a length", !SolidMeasure.Try( mesh, SolidMeasure.Target.Face( top ), out _ ) );
	}
}
