using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The face query a paint dab depends on, judged against what it has to be: the SAME faces a
/// linear scan over the triangulated surface would find, each once.
///
/// The box test is a pruning step and the triangle test is the verdict. A query that stopped at the
/// box would hand the caller faces the brush never touched — every returned face is rasterised into
/// texels, so a false positive is visible paint, not a loose number. That is why the brute-force
/// agreement below is the check that matters, and everything else is a sharper instance of it.
/// </summary>
public static class BvhFaceTests
{
	public static void Run()
	{
		Section( "bvh faces-in-radius: agrees with a linear scan" );
		TestAgreesWithBruteForce();
		TestFaceCentreFindsOnlyThatFace();
		TestEdgeFindsBothFaces();
		TestWholeModelReturnsEveryFaceOnce();
		TestOutsideReturnsEmpty();
		TestZeroRadiusDoesNotThrow();
	}

	static void TestAgreesWithBruteForce()
	{
		// A subdivided sphere is the fixture that would expose a box-only query: its triangles sit
		// at every angle, so a sphere centred in a valley must not catch the far wall of that valley
		// merely because both bounding boxes overlap. Random points and radii rather than a few
		// hand-picked ones, so a pruning bug has to be exactly as lucky as the box to hide.
		var mesh = CatmullClark.Subdivide( Primitives.QuadSphere( 1f, 4 ), 2 );
		var bvh = MeshBVH.Build( mesh );
		var rng = new Random( 20260904 );

		var points = new Vec3[100];

		for ( var i = 0; i < points.Length; i++ )
		{
			points[i] = new Vec3(
				(float)(rng.NextDouble() * 2.0 - 1.0) * 1.2f,
				(float)(rng.NextDouble() * 2.0 - 1.0) * 1.2f,
				(float)(rng.NextDouble() * 2.0 - 1.0) * 1.2f );
		}

		var radii = new[] { 0.05f, 0.2f, 0.5f, 1.5f };
		var results = new List<int>();

		foreach ( var radius in radii )
		{
			var mismatches = 0;

			foreach ( var point in points )
			{
				bvh.FacesInRadius( mesh, point, radius, results );
				var expected = BruteForceFaces( mesh, point, radius );

				results.Sort();
				expected.Sort();

				if ( !results.SequenceEqual( expected ) )
					mismatches++;
			}

			Check( $"radius {radius:0.##} agrees with a linear scan over {mesh.FaceCount} faces",
				mismatches == 0, $"{mismatches} of {points.Length} points disagreed" );
		}
	}

	static void TestFaceCentreFindsOnlyThatFace()
	{
		// A sphere hugging one face's middle must return that face and nothing else. The opposite
		// face's box never overlaps, but the four side faces' boxes reach within the sphere's box —
		// it is the triangle test, not the box test, that keeps them out.
		var mesh = Primitives.Box( 2, 2, 2 );
		var bvh = MeshBVH.Build( mesh );

		var centre = mesh.FaceCentroid( mesh.Faces[1] );
		var results = new List<int>();
		bvh.FacesInRadius( mesh, centre, 0.1f, results );

		Check( "a small sphere on a face centre returns exactly that face",
			results.Count == 1 && results[0] == 1, $"got [{string.Join( ", ", results )}]" );
	}

	static void TestEdgeFindsBothFaces()
	{
		// The edge between the bottom and the -Y side, shared by exactly two faces. A sphere centred
		// ON the edge holds a sliver of both triangles, so both faces must come back.
		var mesh = Primitives.Box( 2, 2, 2 );
		var bvh = MeshBVH.Build( mesh );

		var edge = (mesh.Positions[0] + mesh.Positions[1]) * 0.5f;
		var results = new List<int>();
		bvh.FacesInRadius( mesh, edge, 0.1f, results );
		results.Sort();

		Check( "a sphere centred on an edge returns both faces sharing it",
			results.Count == 2 && results[0] == 0 && results[1] == 2,
			$"got [{string.Join( ", ", results )}]" );
	}

	static void TestWholeModelReturnsEveryFaceOnce()
	{
		// A sphere big enough to swallow the whole solid must return every face, and none twice —
		// each face's two triangles both qualify and the query has to name the face once, not once
		// per triangle.
		var mesh = Primitives.Box( 2, 2, 2 );
		var bvh = MeshBVH.Build( mesh );

		var results = new List<int>();
		bvh.FacesInRadius( mesh, Vec3.Zero, 2f, results );

		Check( "a sphere containing the model returns every face", results.Count == mesh.FaceCount,
			$"got {results.Count} of {mesh.FaceCount}" );
		Check( "and each face exactly once", results.Distinct().Count() == results.Count,
			$"duplicates: [{string.Join( ", ", results )}]" );
	}

	static void TestOutsideReturnsEmpty()
	{
		var mesh = Primitives.Box( 2, 2, 2 );
		var bvh = MeshBVH.Build( mesh );

		var results = new List<int>();
		bvh.FacesInRadius( mesh, new Vec3( 10, 10, 10 ), 0.5f, results );

		Check( "a sphere entirely outside returns nothing", results.Count == 0,
			$"got [{string.Join( ", ", results )}]" );
	}

	static void TestZeroRadiusDoesNotThrow()
	{
		// A zero-radius dab is a legal click: it must not divide by anything, and it must still find
		// the face the point sits exactly on.
		var mesh = Primitives.Box( 2, 2, 2 );
		var bvh = MeshBVH.Build( mesh );

		var centre = mesh.FaceCentroid( mesh.Faces[1] );
		var results = new List<int>();
		var ok = true;

		try
		{
			bvh.FacesInRadius( mesh, centre, 0f, results );
		}
		catch
		{
			ok = false;
		}

		Check( "a radius of zero does not throw", ok );
		Check( "and still finds the face the point sits on", results.Count == 1 && results[0] == 1,
			$"got [{string.Join( ", ", results )}]" );
	}

	/// <summary>Reference implementation: every face, triangulated, tested by the closest point on
	/// each triangle. Independent of the BVH's traversal, so any pruning error shows up as a set
	/// that disagrees with this one.</summary>
	static List<int> BruteForceFaces( PolyMesh mesh, Vec3 point, float radius )
	{
		var r2 = radius * radius;
		var found = new List<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var c = 0; c < face.Count; c++ )
				corners.Add( mesh.Positions[face.Indices[c]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				if ( ClosestPointSq( point, corners[ia], corners[ib], corners[ic] ) <= r2 )
				{
					found.Add( fi );
					break;
				}
			}
		}

		return found;
	}

	/// <summary>Squared distance from a point to a triangle, via the closest point clamped to the
	/// vertex, edge and face regions in turn.</summary>
	static float ClosestPointSq( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		var ab = b - a;
		var ac = c - a;
		var ap = p - a;

		var d1 = Vec3.Dot( ab, ap );
		var d2 = Vec3.Dot( ac, ap );

		if ( d1 <= 0f && d2 <= 0f )
			return ap.LengthSquared;

		var bp = p - b;
		var d3 = Vec3.Dot( ab, bp );
		var d4 = Vec3.Dot( ac, bp );

		if ( d3 >= 0f && d4 <= d3 )
			return bp.LengthSquared;

		var vc = d1 * d4 - d3 * d2;

		if ( vc <= 0f && d1 >= 0f && d3 <= 0f )
			return (a + ab * (d1 / (d1 - d3)) - p).LengthSquared;

		var cp = p - c;
		var d5 = Vec3.Dot( ab, cp );
		var d6 = Vec3.Dot( ac, cp );

		if ( d6 >= 0f && d5 <= d6 )
			return cp.LengthSquared;

		var vb = d5 * d2 - d1 * d6;

		if ( vb <= 0f && d2 >= 0f && d6 <= 0f )
			return (a + ac * (d2 / (d2 - d6)) - p).LengthSquared;

		var va = d3 * d6 - d5 * d4;

		if ( va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f )
			return (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))) - p).LengthSquared;

		var denom = 1f / (va + vb + vc);
		return (a + ab * (vb * denom) + ac * (vc * denom) - p).LengthSquared;
	}
}
