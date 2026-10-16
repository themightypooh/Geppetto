using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Blob, Stroke and Surface detail: the implicit kernel, the sculpt brushes and the skin passes as
/// features, so a build script (and so the CLI and the MCP) can reach them. Each is checked through
/// <see cref="BuildScript"/>, the way an agent would drive it, and each is checked to refuse rather
/// than do nothing.
/// </summary>
public static class OrganicFeatureTests
{
	public static void Run()
	{
		Section( "organic features: blob, stroke, surface detail from a script" );
		TestBlobFusesAndCuts();
		TestBlobRefusesBadShapesAndHugeGrids();
		TestStrokeCreasesAlongItsPath();
		TestStrokeRefusesToDoNothing();
		TestDetailPasses();
		TestOrganicFeaturesRoundTrip();
	}

	static void TestBlobFusesAndCuts()
	{
		var studio = new PartStudio();
		var result = BuildScript.Run( studio, "add Blob name=B shapes=\"sphere 0,0,0 4; sphere 6,0,0 3 mat=2; -sphere 0,-4,0 1.5\" blend=2 voxel=0.3" );
		Check( "a blob script runs", result.Ok, result.ToString() );

		var body = ModelMeasure.FindBody( studio, "B" );
		Check( "and makes one fused body", body is not null && MeshSplit.ConnectedPieces( body.Mesh ).Count == 1, body is null ? "no body" : $"{MeshSplit.ConnectedPieces( body.Mesh ).Count} pieces" );

		var (min, max) = ModelMeasure.Bounds( body.Mesh );
		Check( "spanning both spheres", min.x < -3.8f && max.x > 8.8f, $"x {min.x}..{max.x}" );
		Check( "with the second sphere's material slot", body.Mesh.Faces.Any( f => f.Material == 2 ) && body.Mesh.Faces.Any( f => f.Material == 0 ) );

		// The cutter at y = -4 scoops the front: nothing is left at the sphere's own front pole.
		var front = body.Mesh.Positions.Where( p => MathF.Abs( p.x ) < 0.5f && MathF.Abs( p.z ) < 0.5f ).Min( p => p.y );
		Check( "and the cutter scoops it", front > -3.6f, $"front pole at y {front:0.##}" );
	}

	static void TestBlobRefusesBadShapesAndHugeGrids()
	{
		var bad = BuildScript.Run( new PartStudio(), "add Blob shapes=\"sphere 0,0,0\"" );
		Check( "a shape missing its radius says which and how", !bad.Ok && bad.Error.Contains( "Shape 1" ) && bad.Error.Contains( "radius" ), bad.Error );

		var huge = BuildScript.Run( new PartStudio(), "add Blob shapes=\"sphere 0,0,0 50\" voxel=0.01" );
		Check( "a grid too big to sample is refused with a voxel that fits", !huge.Ok && huge.Error.Contains( "Set Voxel to" ), huge.Error );

		var cutsOnly = BuildScript.Run( new PartStudio(), "add Blob shapes=\"-sphere 0,0,0 3\"" );
		Check( "cutters alone are refused", !cutsOnly.Ok, cutsOnly.ToString() );
	}

	static void TestStrokeCreasesAlongItsPath()
	{
		var studio = new PartStudio();
		var result = BuildScript.Run( studio, @"
add Blob name=B shapes=""sphere 0,0,0 5"" voxel=0.2
add Stroke bodies=B brush=Crease radius=0.8 strength=0.8 points=-3,-5,0;3,-5,0
" );
		Check( "a crease stroke runs", result.Ok, result.ToString() );

		// Off the path the sphere is untouched; on it the skin is cut in.
		var mesh = ModelMeasure.FindBody( studio, "B" ).Mesh;
		var onPath = mesh.Positions.Where( p => MathF.Abs( p.x ) < 1.5f && MathF.Abs( p.z ) < 0.25f && p.y < 0f ).Min( p => p.Length );
		var offPath = mesh.Positions.Where( p => MathF.Abs( p.x ) < 1.5f && MathF.Abs( p.z ) > 2.5f && MathF.Abs( p.z ) < 3f && p.y < 0f ).Average( p => p.Length );
		Check( "the crease cuts in along the path", onPath < 4.9f, $"min radius on the path {onPath:0.###}" );
		Check( "and leaves the skin off it alone", MathF.Abs( offPath - 5f ) < 0.15f, $"radius off the path {offPath:0.###}" );
	}

	static void TestStrokeRefusesToDoNothing()
	{
		var far = BuildScript.Run( new PartStudio(), "add Primitive name=Box shape=Box\nadd Stroke bodies=Box points=100,100,100;110,100,100" );
		Check( "a stroke nowhere near the body fails", !far.Ok && far.Error.Contains( "moved nothing" ), far.Error );

		var coarse = BuildScript.Run( new PartStudio(), "add Primitive name=Box shape=Box\nadd Stroke bodies=Box points=0,0,0.5;0.2,0,0.5 radius=0.01" );
		Check( "a brush smaller than the mesh's edges says to subdivide", !coarse.Ok && coarse.Error.Contains( "Subdivide" ), coarse.Error );

		var noPath = BuildScript.Run( new PartStudio(), "add Primitive name=Box shape=Box\nadd Stroke bodies=Box" );
		Check( "a stroke with no points says so", !noPath.Ok && noPath.Error.Contains( "no path" ), noPath.Error );
	}

	static void TestDetailPasses()
	{
		foreach ( var pass in new[] { "Grain", "Pustules", "Scatter", "Sag" } )
		{
			var studio = new PartStudio();
			var result = BuildScript.Run( studio, $@"
add Blob name=B shapes=""sphere 0,0,0 6"" voxel=0.25
add Detail bodies=B pass={pass} size=1 depth=0.15 lumps=0.05 spacing=2 busy=1
" );
			Check( $"the {pass} pass runs", result.Ok, result.ToString() );

			var mesh = ModelMeasure.FindBody( studio, "B" ).Mesh;
			var spread = mesh.Positions.Max( p => p.Length ) - mesh.Positions.Min( p => p.Length );
			Check( $"and roughens the sphere", spread > 0.08f, $"radius spread {spread:0.###}" );
		}

		// Confined to a reach around a point, the far side of the body is untouched.
		var local = new PartStudio();
		var ok = BuildScript.Run( local, "add Blob name=B shapes=\"sphere 0,0,0 6\" voxel=0.25\nadd Detail bodies=B pass=Grain size=1 depth=0.2 lumps=0.1 busy=1 near=6,0,0 reach=3" );
		var before = new PartStudio();
		BuildScript.Run( before, "add Blob name=B shapes=\"sphere 0,0,0 6\" voxel=0.25" );
		var a = ModelMeasure.FindBody( local, "B" ).Mesh.Positions;
		var b = ModelMeasure.FindBody( before, "B" ).Mesh.Positions;
		var farMoved = Enumerable.Range( 0, a.Count ).Where( i => b[i].x < 0f ).Max( i => (a[i] - b[i]).Length );
		var nearMoved = Enumerable.Range( 0, a.Count ).Where( i => b[i].x > 5f ).Max( i => (a[i] - b[i]).Length );
		Check( "a pass with a reach stays near its point", ok.Ok && farMoved < 1e-5f && nearMoved > 0.02f, $"far {farMoved:0.#####}, near {nearMoved:0.###}" );

		var nothing = BuildScript.Run( new PartStudio(), "add Primitive name=Box shape=Box\nadd Detail bodies=Box pass=Grain near=100,100,100 reach=1" );
		Check( "a pass whose masks exclude everything fails", !nothing.Ok && nothing.Error.Contains( "moved nothing" ), nothing.Error );
	}

	static void TestOrganicFeaturesRoundTrip()
	{
		var studio = new PartStudio();
		BuildScript.Run( studio, @"
add Blob name=B shapes=""capsule 0,0,0 0,0,8 3; -sphere 0,-3,4 1"" voxel=0.3
add Stroke bodies=B brush=Crease radius=0.9 strength=0.5 points=-2,-3,2;2,-3,6
add Detail bodies=B pass=Pustules size=0.8 clusters=2 percluster=5 spread=2
" );
		var text = StudioDocument.Write( studio );
		var loaded = StudioDocument.Read( text );
		loaded.Rebuild();

		var a = ModelMeasure.FindBody( studio, "B" ).Mesh;
		var b = ModelMeasure.FindBody( loaded, "B" )?.Mesh;
		var same = b is not null && a.VertexCount == b.VertexCount && Enumerable.Range( 0, a.VertexCount ).All( i => (a.Positions[i] - b.Positions[i]).Length < 1e-4f );
		Check( "a saved blob, stroke and detail pass rebuild identically", same, b is null ? "no body after load" : $"{a.VertexCount} vs {b.VertexCount} verts" );
	}
}
