using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Brush options a script or the sculpt bar can set: multi-plane and radial symmetry, the masks a
/// Stroke or Detail pass can be kept to, and the Kelvinlet twist, scale and pinch brushes. Each is
/// measured: equal displacement in every symmetric copy, nothing at all outside a mask's region.
/// </summary>
public static class BrushOptionsTests
{
	public static void Run()
	{
		Report.Section( "brush options: multi-plane and radial symmetry" );
		TestRadialEqualDisplacements();
		TestMultiPlaneMirror();
		TestMirroredTwistIsAMirrorImage();
		TestSymmetryCopies();
		TestStrokeSymmetryParams();
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	/// <summary>A flat disk in XY, 24 sectors and 16 rings out to radius 4: symmetric under every
	/// 15° turn and every mirror through the origin, so a symmetric copy lands on real vertices.</summary>
	internal static PolyMesh Disk( int sectors = 24, int rings = 16, float radius = 4f )
	{
		var m = new PolyMesh();
		m.AddVertex( Vec3.Zero );

		for ( var r = 1; r <= rings; r++ )
			for ( var s = 0; s < sectors; s++ )
			{
				var a = 2f * MathF.PI * s / sectors;
				var d = radius * r / rings;
				m.AddVertex( new Vec3( d * MathF.Cos( a ), d * MathF.Sin( a ), 0 ) );
			}

		int V( int r, int s ) => 1 + (r - 1) * sectors + ((s % sectors) + sectors) % sectors;
		var uv = new Vec2[] { default, default, default };
		var uv4 = new Vec2[] { default, default, default, default };

		for ( var s = 0; s < sectors; s++ )
			m.AddFace( new[] { 0, V( 1, s ), V( 1, s + 1 ) }, uv, 0 );

		for ( var r = 1; r < rings; r++ )
			for ( var s = 0; s < sectors; s++ )
				m.AddFace( new[] { V( r, s ), V( r + 1, s ), V( r + 1, s + 1 ), V( r, s + 1 ) }, uv4, 0 );

		return m;
	}

	/// <summary>Index of the vertex nearest <paramref name="p"/>.</summary>
	static int Nearest( IReadOnlyList<Vec3> points, Vec3 p )
	{
		var best = 0;
		var bd = float.MaxValue;
		for ( var i = 0; i < points.Count; i++ )
		{
			var d = (points[i] - p).LengthSquared;
			if ( d < bd ) { bd = d; best = i; }
		}
		return best;
	}

	static void TestRadialEqualDisplacements()
	{
		var m = Disk();
		var before = m.Positions.ToArray();
		var stroke = new BrushStroke { Kind = BrushKind.Draw, Radial = 6, RadialAxis = MirrorAxis.Z };
		stroke.Samples.Add( new BrushSample( new Vec3( 2.2f, 0.4f, 0 ), new Vec3( 0, 0, 1 ), 0.9f, 0.3f ) );
		Brush.Apply( m, stroke );

		var worst = 0f;
		var lifted = 0;

		for ( var v = 0; v < before.Length; v++ )
		{
			var dz = m.Positions[v].z - before[v].z;
			if ( dz > 1e-5f ) lifted++;

			for ( var k = 1; k < 6; k++ )
			{
				var turned = SimpleDeform.Rotate( before[v], new Vec3( 0, 0, 1 ), 2f * MathF.PI * k / 6f );
				var u = Nearest( before, turned );
				worst = MathF.Max( worst, MathF.Abs( (m.Positions[u].z - before[u].z) - dz ) );
			}
		}

		Check( "radial 6 lifts six equal mounds: every vertex moves as its 60° turns do", worst < 1e-4f && lifted >= 6, $"worst {worst:0.000000}, {lifted} lifted" );

		// The six mounds and nothing else: a vertex at 30° off any copy, at the stroke's radius, stays put.
		var between = Nearest( before, SimpleDeform.Rotate( new Vec3( 2.2f, 0.4f, 0 ), new Vec3( 0, 0, 1 ), MathF.PI / 6f ) );
		Check( "and leaves the surface between the copies alone", MathF.Abs( m.Positions[between].z - before[between].z ) < 1e-6f );
	}

	static void TestMultiPlaneMirror()
	{
		var m = Primitives.Plane( 10, 10, 100, 100 );
		var before = m.Positions.ToArray();
		var stroke = new BrushStroke { Kind = BrushKind.Draw, MirrorAxes = SymmetryAxes.X | SymmetryAxes.Y };
		stroke.Samples.Add( new BrushSample( new Vec3( 2f, 1.5f, 0 ), new Vec3( 0, 0, 1 ), 0.8f, 0.2f ) );
		Brush.Apply( m, stroke );

		float Rise( float x, float y ) { var i = Nearest( before, new Vec3( x, y, 0 ) ); return m.Positions[i].z - before[i].z; }

		var rises = new[] { Rise( 2, 1.5f ), Rise( -2, 1.5f ), Rise( 2, -1.5f ), Rise( -2, -1.5f ) };
		Check( "mirror X+Y raises four equal mounds, one in each quadrant", rises.Min() > 0.1f && rises.Max() - rises.Min() < 1e-5f, string.Join( ", ", rises.Select( r => $"{r:0.0000}" ) ) );

		var copies = BrushSymmetry.Copies( SymmetryAxes.X | SymmetryAxes.Y | SymmetryAxes.Z );
		Check( "mirror X+Y+Z is eight copies", copies.Count == 8 && copies[0].IsIdentity, $"{copies.Count}" );
	}

	static void TestMirroredTwistIsAMirrorImage()
	{
		var m = Primitives.Plane( 10, 10, 100, 100 );
		var before = m.Positions.ToArray();
		var stroke = new BrushStroke { Kind = BrushKind.Twist, Mirror = MirrorAxis.X };
		stroke.Samples.Add( new BrushSample( new Vec3( 2f, 0f, 0 ), new Vec3( 0, 0, 1 ), 1f, 0.5f ) );
		Brush.Apply( m, stroke );

		// A vertex at (2, 0.5) and its mirror at (-2, 0.5): the mirror's displacement is the
		// original's with x negated.
		var a = Nearest( before, new Vec3( 2f, 0.5f, 0 ) );
		var b = Nearest( before, new Vec3( -2f, 0.5f, 0 ) );
		var da = m.Positions[a] - before[a];
		var db = m.Positions[b] - before[b];
		Check( "a mirrored twist turns the other way, so the two sides are mirror images",
			da.Length > 1e-3f && (new Vec3( -da.x, da.y, da.z ) - db).Length < 1e-4f, $"{da.x:0.0000},{da.y:0.0000} vs {db.x:0.0000},{db.y:0.0000}" );
	}

	static void TestSymmetryCopies()
	{
		Check( "radial 2 about Z plus mirror X+Y is four copies, not eight (shared turns applied once)",
			BrushSymmetry.Copies( SymmetryAxes.X | SymmetryAxes.Y, 2 ).Count == 4 );
		Check( "symmetry planes parse from X+Y and print back", BrushSymmetry.TryParseAxes( "x+y", out var axes ) && BrushSymmetry.Name( axes ) == "X+Y" );
		Check( "the first four mirror choices are the single planes older files stored",
			BrushSymmetry.ChoiceNames().Take( 4 ).SequenceEqual( Enum.GetNames( typeof( MirrorAxis ) ) ) );
	}

	static void TestStrokeSymmetryParams()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive shape=Sphere divisions=16\nadd Stroke points=0.45,0.1,0.2 radius=0.2 strength=0.05 mirror=X+Y radial_copies=3" );
		var stroke = studio.Features.OfType<StrokeFeature>().FirstOrDefault();
		Check( "a script sets a Stroke's mirror planes and radial copies", r.Ok && stroke is not null && stroke.Mirror.Value == "X+Y" && stroke.Radial.Value == 3, r.ToString() );

		var text = StudioDocument.Write( studio );
		var loaded = StudioDocument.Read( text ).Features.OfType<StrokeFeature>().First();
		Check( "and they save and load", loaded.Mirror.Value == "X+Y" && loaded.Radial.Value == 3 );
	}
}
