using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The cage-level modelling tools the distorted heads are built with: ripping an edge chain
/// open, growing regions to find a seal, open loops and loop extrusion (MeshLoops); strain folds
/// and striae (SkinStrain); detail projected onto a Multires level and riding a cage edit
/// (DetailProjection); tendons (Tendons).
/// </summary>
public static class CageToolsTests
{
	public static void Run()
	{
		Report.Section( "cage tools: rip, regions, loops, strain folds, detail projection, tendons" );
		TestRip();
		TestGrowRegions();
		TestLoops();
		TestStrainFolds();
		TestStriae();
		TestDetailRidesCage();
		TestTendon();
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	static int OpenEdges( PolyMesh m ) => MeshLoops.EdgeFaces( m ).Count( kv => kv.Value.Count == 1 );

	static void TestRip()
	{
		// A 6 x 4 grid ripped along the middle row's inner edges (x in -2..2 of -3..3).
		var grid = Primitives.Plane( 6, 4, 6, 4 );
		var cen = MeshLoops.Centroids( grid );
		var labels = cen.Select( c => c.y > 0 ? 1 : 2 ).ToArray();
		var seal = MeshLoops.Border( grid, labels, 1, 2, p => MathF.Abs( p.x ) < 2.01f );
		Check( "the border between two regions is the row of edges between them", seal.Count == 4, $"{seal.Count} edges" );
		var before = OpenEdges( grid );
		var ripped = MeshLoops.Rip( grid, seal, fi => labels[fi] == 2, out var twins );
		Check( "a rip duplicates only the chain's inner vertices (its ends stay shared)", twins.Count == 3 && ripped.VertexCount == grid.VertexCount + 3, $"{twins.Count} twins" );
		Check( "a rip deletes nothing", ripped.FaceCount == grid.FaceCount );
		Check( "a rip opens two edges for every edge of the chain", OpenEdges( ripped ) == before + 2 * seal.Count, $"{OpenEdges( ripped )} open, was {before}" );
		Check( "ripped copies start where their originals are", twins.All( kv => (ripped.Positions[kv.Value] - ripped.Positions[kv.Key]).Length < 1e-6f ) );
	}

	static void TestGrowRegions()
	{
		var grid = Primitives.Plane( 8, 2, 8, 2 );
		var cen = MeshLoops.Centroids( grid );
		int At( float x, float y ) => Enumerable.Range( 0, grid.FaceCount ).OrderBy( i => (cen[i] - new Vec3( x, y, 0 )).LengthSquared ).First();
		var labels = MeshLoops.GrowRegions( grid, new[] { (At( -3.5f, 0.5f ), 1), (At( 3.5f, 0.5f ), 2) } );
		var left = Enumerable.Range( 0, grid.FaceCount ).Where( i => cen[i].x < 0 ).All( i => labels[i] == 1 );
		var right = Enumerable.Range( 0, grid.FaceCount ).Where( i => cen[i].x > 0 ).All( i => labels[i] == 2 );
		Check( "two seeds split a strip at the line equally far from both", left && right );
	}

	static void TestLoops()
	{
		var quad = Primitives.Plane( 1, 1, 1, 1 );
		var loops = MeshLoops.OpenLoops( quad );
		Check( "a single quad has one closed open-loop of four", loops.Count == 1 && loops[0].Closed && loops[0].Verts.Count == 4 );
		var ext = MeshLoops.ExtrudeLoop( quad, loops[0].Verts, true, 2, ( i, r ) => quad.Positions[loops[0].Verts[i]] * (1f + 0.5f * r) + new Vec3( 0, 0, -0.3f * r ), 0, out var grid );
		Check( "extruding a loop of four by two rings adds eight quads", ext.FaceCount == quad.FaceCount + 8 && ext.VertexCount == quad.VertexCount + 8 );
		var after = MeshLoops.OpenLoops( ext );
		Check( "the extruded surface's open edge is its last ring", after.Count == 1 && after[0].Verts.All( v => v == grid[0, 2] || v == grid[1, 2] || v == grid[2, 2] || v == grid[3, 2] ) );
		Check( "extruded quads keep the surface's winding (every shared edge runs both ways)", WindingConsistent( ext ) );
	}

	static bool WindingConsistent( PolyMesh m )
	{
		var directed = new HashSet<(int, int)>();
		foreach ( var f in m.Faces )
			for ( var k = 0; k < f.Indices.Length; k++ )
				if ( !directed.Add( (f.Indices[k], f.Indices[(k + 1) % f.Indices.Length]) ) ) return false;
		return true;
	}

	static PolyMesh Sheet( int n, float size ) => Primitives.Plane( size, size, n, n );

	static void TestStrainFolds()
	{
		// A 3 x 3 in sheet squeezed to 60% along x: folds should rise, with crests across x.
		var rest = Sheet( 60, 3f );
		var squeezed = rest.Positions.Select( p => new Vec3( p.x * 0.6f, p.y, p.z ) ).ToArray();
		var positions = (Vec3[])squeezed.Clone();
		var tone = SkinStrain.Respond( rest.Faces, rest.Positions, squeezed, positions, new SkinStrainOptions { FoldWavelength = 0.3f, FoldMinVerts = 10, Seed = 3 } );
		var dz = positions.Select( p => p.z ).ToArray();
		var amp = dz.Max() - dz.Min();
		Check( "a sheet squeezed to 60% gets folds", amp > 0.02f, $"{amp:0.000} in peak to peak" );
		Check( "and they stay under the amplitude cap", dz.Max() < 0.071f && dz.Min() > -0.071f, $"{dz.Min():0.000}..{dz.Max():0.000}" );
		// Variation along x (across the crests) beats variation along y (along them).
		float Var( Func<int, int> next )
		{
			var s = 0f; var c = 0;
			for ( var i = 0; i < positions.Length; i++ ) { var j = next( i ); if ( j < 0 ) continue; s += MathF.Abs( dz[j] - dz[i] ); c++; }
			return s / MathF.Max( c, 1 );
		}
		var stride = 61;
		var alongX = Var( i => i % stride < stride - 1 ? i + 1 : -1 );
		var alongY = Var( i => i + stride < positions.Length ? i + stride : -1 );
		Check( "the fold crests run across the squeeze", alongX > 1.5f * alongY, $"{alongX:0.0000} across, {alongY:0.0000} along" );
		Check( "fold valleys are shaded", tone.Min() < -0.05f );
		var still = (Vec3[])rest.Positions.ToArray().Clone();
		SkinStrain.Respond( rest.Faces, rest.Positions, rest.Positions, still, new SkinStrainOptions { FoldWavelength = 0.3f, StriaeSpacing = 0.17f } );
		Check( "an undeformed sheet is left alone", still.Zip( rest.Positions ).All( t => (t.First - t.Second).Length < 1e-6f ) );
	}

	static void TestStriae()
	{
		var rest = Sheet( 80, 3f );
		var stretched = rest.Positions.Select( p => new Vec3( p.x * 1.7f, p.y, p.z ) ).ToArray();
		var positions = (Vec3[])stretched.Clone();
		var tone = SkinStrain.Respond( rest.Faces, rest.Positions, stretched, positions, new SkinStrainOptions { StriaeSpacing = 0.17f, StriaeDepth = 0.012f, Seed = 5 } );
		var dz = positions.Select( p => p.z ).ToArray();
		Check( "skin stretched past 1.3 gets striae", dz.Min() < -0.004f, $"deepest {dz.Min():0.0000}" );
		Check( "striae are shallow (marks, not cuts)", dz.Min() > -0.013f && dz.Max() < 0.01f );
		Check( "striae are pale", tone.Max() > 0.3f );
	}

	static void TestDetailRidesCage()
	{
		// A target with detail: a subdivided sphere with bumps; the cage is the plain sphere.
		var cage = Primitives.QuadSphere( 1f, 4 );
		var target = CatmullClark.Subdivide( cage, 2 );
		for ( var i = 0; i < target.VertexCount; i++ )
		{
			var p = target.Positions[i];
			target.Positions[i] = p * (1f + 0.04f * MathF.Sin( p.x * 9f ) * MathF.Sin( p.y * 9f ));
		}
		var sculpt = new MultiresSculpt( cage );
		sculpt.AddLevel();
		sculpt.AddLevel();
		var mean = DetailProjection.Capture( sculpt, 2, target, 0.5f );
		var bvh = MeshBVH.Build( target );
		var evaluated = sculpt.Evaluate( 2 );
		var err = evaluated.Positions.Average( p => bvh.NearestSurface( target, p, 1f )?.Distance ?? 1f );
		Check( "projected detail lands on the sculpt it came from", err < 0.01f, $"{err:0.0000} mean off, moved {mean:0.000}" );
		// Move the cage: the detail should come along.
		var moved = new PolyMesh( cage.Positions.Select( p => p + new Vec3( 2, 0, 0 ) ), cage.Faces.Select( f => new Face( (int[])f.Indices.Clone() ) ) );
		sculpt.SetCage( moved );
		var shifted = new PolyMesh( target.Positions.Select( p => p + new Vec3( 2, 0, 0 ) ), target.Faces.Select( f => new Face( (int[])f.Indices.Clone() ) ) );
		var bvh2 = MeshBVH.Build( shifted );
		var err2 = sculpt.Evaluate( 2 ).Positions.Average( p => bvh2.NearestSurface( shifted, p, 1f )?.Distance ?? 1f );
		Check( "and rides a cage edit", err2 < 0.01f, $"{err2:0.0000} mean off" );
	}

	static void TestTendon()
	{
		var positions = new List<Vec3>();
		var faces = new List<Face>();
		Tendons.Append( positions, faces, new Vec3( 0, -1, 0 ), new Vec3( 0, 1, 0 ), 0.05f, 1f, new Random( 4 ) );
		Check( "a tendon is built", positions.Count > 0 && faces.Count > 0 && positions.All( p => float.IsFinite( p.x + p.y + p.z ) ) );
		var mid = positions.Where( p => MathF.Abs( p.y ) < 0.07f ).ToList();
		Check( "it sags in the middle", mid.Count > 0 && mid.Average( p => p.z ) < -0.15f, mid.Count > 0 ? $"{mid.Average( p => p.z ):0.00}" : "no middle" );
		var near = positions.Where( p => p.y < -0.9f ).ToList();
		Check( "it is thickest where it roots", near.Max( p => MathF.Sqrt( p.x * p.x + p.z * p.z ) ) > mid.Max( p => MathF.Abs( p.x ) ) );
		var snapped = new List<Vec3>();
		Tendons.Append( snapped, new List<Face>(), new Vec3( 0, -1, 0 ), new Vec3( 0, 1, 0 ), 0.05f, 0.45f, new Random( 4 ) );
		Check( "a snapped tendon stops short and hangs", snapped.Max( p => p.y ) < 0.2f && snapped.Min( p => p.z ) < -0.2f );
	}
}
