using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// FaceSurface: deciding where "that face" stops.
///
/// The viewport's face highlight, its edge picker and the sketcher's reference outline all used to
/// answer this separately - the first two by looking at one n-gon, the third by scanning the whole
/// mesh for anything coplanar - so on a boolean-cut wall the highlight lit one triangle, the edge
/// picker offered the triangulation seams, and the grid covered the whole wall. Three answers to
/// one question, all on screen at once.
///
/// What is checked here is the rule they now share, at the sizes and shapes it has to hold at: a
/// primitive face, a fragmented one, a fragmented one whose seam vertices are duplicated, one with
/// a hole through it, two coplanar faces that do not touch, two that touch but are painted
/// differently, and a curved surface that must not merge at all.
/// </summary>
public static class FaceSurfaceTests
{
	public static void Run()
	{
		Report.Section( "face surface: a face nothing has cut is itself" );
		TestWholeFace();

		Report.Section( "face surface: fragments of one wall are one surface" );
		TestFragmentsMerge();

		Report.Section( "face surface: fragments whose seam vertices were duplicated" );
		TestUnweldedSeam();

		Report.Section( "face surface: coplanar faces that do not touch are not one surface" );
		TestDisjointCoplanar();

		Report.Section( "face surface: a different material slot is a different surface" );
		TestMaterialSplit();

		Report.Section( "face surface: a curved surface never merges" );
		TestCurvedSurface();

		Report.Section( "face surface: edge picking offers the outline, never a seam" );
		TestEdgePickSkipsSeams();

		Report.Section( "face surface: a hole through a face keeps its rim" );
		TestHoleRim();

		Report.Section( "face surface: face-to-edges for Fillet follows the surface" );
		TestCaptureBoundary();
	}

	/// <summary>The index of the face whose normal points most nearly along +Z.</summary>
	static int TopFace( PolyMesh mesh )
	{
		var best = -1;
		var bestZ = 0.99f;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			var z = mesh.FaceNormal( mesh.Faces[i] ).z;

			if ( z <= bestZ )
				continue;

			bestZ = z;
			best = i;
		}

		return best;
	}

	static void TestWholeFace()
	{
		var mesh = Primitives.Box( 4f, 6f, 2f );
		var surface = FaceSurface.FromFace( mesh, TopFace( mesh ) );

		Report.Check( "the top of a box is one face",
			surface.Faces.Count == 1, $"{surface.Faces.Count} faces" );

		Report.Check( "with four boundary edges",
			surface.Boundary.Count == 4, $"{surface.Boundary.Count} edges" );

		// The whole point of the type is that it never widens past the surface it was asked about.
		// A rule slightly too loose would pull in the four sides here and nothing would say so.
		Report.Check( "and it does not reach round onto the sides",
			surface.Faces.All( f => mesh.FaceNormal( mesh.Faces[f] ).z > 0.99f ) );
	}

	/// <summary>
	/// Cut the top of a box into two triangles the way a boolean does, and the two must come back
	/// as one surface with the seam gone. This is the case CoplanarMerge's header measured at 88
	/// fragments on a real part.
	/// </summary>
	static void TestFragmentsMerge()
	{
		var mesh = Primitives.Box( 4f, 4f, 2f );
		var top = TopFace( mesh );
		var corners = mesh.Faces[top].Indices;

		mesh.Faces.RemoveAt( top );
		mesh.AddFace( new[] { corners[0], corners[1], corners[2] } );
		mesh.AddFace( new[] { corners[0], corners[2], corners[3] } );

		var surface = FaceSurface.FromFace( mesh, TopFace( mesh ) );

		Report.Check( "both triangles are in the surface",
			surface.Faces.Count == 2, $"{surface.Faces.Count} faces" );

		// Five edges would mean the diagonal survived - which is exactly what the old per-face
		// highlight drew across the middle of the wall.
		Report.Check( "and the seam between them is not in the outline",
			surface.Boundary.Count == 4, $"{surface.Boundary.Count} edges" );

		// Seeded from EITHER fragment. An answer that depends on which triangle the ray happened to
		// hit is an answer that changes as the cursor moves across an unbroken-looking wall.
		var tops = Enumerable.Range( 0, mesh.Faces.Count )
			.Where( i => mesh.FaceNormal( mesh.Faces[i] ).z > 0.99f )
			.ToList();

		var same = tops.All( i =>
		{
			var s = FaceSurface.FromFace( mesh, i );
			return s.Faces.Count == 2 && s.Boundary.Count == 4;
		} );

		Report.Check( "seeded from either fragment, the surface is the same", same,
			$"{tops.Count} fragments tried" );
	}

	/// <summary>
	/// The same split, but with the seam described by a SECOND pair of vertices at the same
	/// positions - which is what a boolean leaves behind when it does not weld. Indices alone say
	/// the two triangles share nothing; positions say they share an edge.
	/// </summary>
	static void TestUnweldedSeam()
	{
		var mesh = Primitives.Box( 4f, 4f, 2f );
		var top = TopFace( mesh );
		var corners = mesh.Faces[top].Indices;

		// Duplicates of the two corners the diagonal runs between.
		var a = mesh.AddVertex( mesh.Positions[corners[0]] );
		var c = mesh.AddVertex( mesh.Positions[corners[2]] );

		mesh.Faces.RemoveAt( top );
		mesh.AddFace( new[] { corners[0], corners[1], corners[2] } );
		mesh.AddFace( new[] { a, c, corners[3] } );

		var surface = FaceSurface.FromFace( mesh, TopFace( mesh ) );

		Report.Check( "a seam described by coincident vertices still joins the two",
			surface.Faces.Count == 2, $"{surface.Faces.Count} faces" );

		Report.Check( "and still drops out of the outline",
			surface.Boundary.Count == 4, $"{surface.Boundary.Count} edges" );
	}

	/// <summary>
	/// Two boxes side by side have coplanar tops that never touch. A plane test alone calls them
	/// one surface and outlines a face the cursor is nowhere near; reachability is what makes the
	/// answer local to what was clicked.
	/// </summary>
	static void TestDisjointCoplanar()
	{
		var mesh = Primitives.Box( 2f, 2f, 2f );
		var other = Primitives.Box( 2f, 2f, 2f );

		MeshTransform.Apply( other, Xform.Translate( new Vec3( 10f, 0f, 0f ) ) );
		MeshTransform.Append( mesh, other );

		var surface = FaceSurface.FromFace( mesh, TopFace( mesh ) );

		Report.Check( "the far box's top does not join this one",
			surface.Faces.Count == 1, $"{surface.Faces.Count} faces" );

		Report.Check( "so the outline is one square rather than two",
			surface.Boundary.Count == 4, $"{surface.Boundary.Count} edges" );
	}

	/// <summary>
	/// CoplanarMerge refuses to weld two coplanar neighbours painted different colours, because the
	/// user made them two faces. Highlighting has to agree, or clicking a painted face lights up
	/// its neighbour as well and the click paints less than the highlight promised.
	/// </summary>
	static void TestMaterialSplit()
	{
		var mesh = Primitives.Box( 4f, 4f, 2f );
		var top = TopFace( mesh );
		var corners = mesh.Faces[top].Indices;

		mesh.Faces.RemoveAt( top );
		mesh.AddFace( new[] { corners[0], corners[1], corners[2] }, null, 1 );
		mesh.AddFace( new[] { corners[0], corners[2], corners[3] }, null, 2 );

		var surface = FaceSurface.FromFace( mesh, mesh.Faces.Count - 1 );

		Report.Check( "a coplanar neighbour on another slot stays out",
			surface.Faces.Count == 1, $"{surface.Faces.Count} faces" );

		Report.Check( "so a painted triangle outlines as a triangle",
			surface.Boundary.Count == 3, $"{surface.Boundary.Count} edges" );
	}

	/// <summary>A cylinder's side is many quads that are nowhere near coplanar. Merging any of them
	/// would put the sketch grid on a curved surface, which is the one place it cannot mean
	/// anything.</summary>
	static void TestCurvedSurface()
	{
		var mesh = Primitives.Cylinder( 1f, 2f, 24 );

		var side = Enumerable.Range( 0, mesh.Faces.Count )
			.First( i => MathF.Abs( mesh.FaceNormal( mesh.Faces[i] ).z ) < 0.1f );

		var surface = FaceSurface.FromFace( mesh, side );

		Report.Check( "one quad of a cylinder wall is one surface",
			surface.Faces.Count == 1, $"{surface.Faces.Count} faces" );

		// The flat cap above it, on the other hand, is a single n-gon and must come back whole.
		var cap = FaceSurface.FromFace( mesh, TopFace( mesh ) );

		Report.Check( "and its flat cap is still one face with a rim all the way round",
			cap.Faces.Count == 1 && cap.Boundary.Count == 24,
			$"{cap.Faces.Count} faces, {cap.Boundary.Count} edges" );
	}

	/// <summary>
	/// The edge picker's contract: what it offers is an edge of the PART. A seam is an edge of the
	/// mesh only - filleting along one does nothing - and on a wall returned as dozens of triangles
	/// a seam was always within a few pixels of the cursor, so the face underneath could not be
	/// picked at all.
	/// </summary>
	static void TestEdgePickSkipsSeams()
	{
		var mesh = Primitives.Box( 4f, 4f, 2f );
		var top = TopFace( mesh );
		var corners = mesh.Faces[top].Indices;

		mesh.Faces.RemoveAt( top );
		mesh.AddFace( new[] { corners[0], corners[1], corners[2] } );
		mesh.AddFace( new[] { corners[0], corners[2], corners[3] } );

		var surface = FaceSurface.FromFace( mesh, TopFace( mesh ) );

		// A point sitting right on the diagonal, which is where the old code was at its worst.
		var onSeam = (mesh.Positions[corners[0]] + mesh.Positions[corners[2]]) * 0.5f;

		Report.Check( "a point on the seam still finds an edge",
			surface.TryClosestEdge( onSeam, out var key, out var distance ) );

		var seam = new EdgeKey( corners[0], corners[2] );

		Report.Check( "and it is not the seam", !key.Equals( seam ), key.ToString() );

		// Half the width of the square: the centre of a 4x4 face is 2 from every real edge, and
		// zero from the seam. A picker that had offered the seam would report 0 here.
		Report.Check( "reported at the distance of a real edge",
			MathF.Abs( distance - 2f ) < 1e-3f, $"{distance}" );

		// And a point near a genuine edge still gets that edge, or the change would have traded one
		// broken picker for another.
		var nearEdge = (mesh.Positions[corners[0]] + mesh.Positions[corners[1]]) * 0.5f;

		Report.Check( "a point on a real edge gets that edge",
			surface.TryClosestEdge( nearEdge, out var edge, out var near )
			&& edge.Equals( new EdgeKey( corners[0], corners[1] ) ) && near < 1e-3f,
			$"{edge} at {near}" );
	}

	/// <summary>
	/// A surface with a hole through it. The rim is used once like any other boundary edge, so it
	/// survives - which is what lets the sketch grid stop at the hole rather than paint over it.
	/// </summary>
	static void TestHoleRim()
	{
		// A square annulus: an outer 4x4 ring and an inner 2x2 one, joined by four quads. Built by
		// hand rather than cut, so the test says what it means without depending on the boolean.
		var mesh = new PolyMesh();

		var outer = new List<int>();
		var inner = new List<int>();

		foreach ( var (x, y) in new[] { (-2f, -2f), (2f, -2f), (2f, 2f), (-2f, 2f) } )
			outer.Add( mesh.AddVertex( new Vec3( x, y, 0f ) ) );

		foreach ( var (x, y) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) } )
			inner.Add( mesh.AddVertex( new Vec3( x, y, 0f ) ) );

		for ( var i = 0; i < 4; i++ )
		{
			var j = (i + 1) % 4;
			mesh.AddFace( new[] { outer[i], outer[j], inner[j], inner[i] } );
		}

		var surface = FaceSurface.FromFace( mesh, 0 );

		Report.Check( "all four quads of the ring are one surface",
			surface.Faces.Count == 4, $"{surface.Faces.Count} faces" );

		// Four outside and four round the hole. The spokes between the quads are each used twice
		// and drop out, exactly as the seams of a fragmented wall do.
		Report.Check( "and the outline is the outer square plus the hole's rim",
			surface.Boundary.Count == 8, $"{surface.Boundary.Count} edges" );

		var atRim = new Vec3( 1f, 0f, 0f );

		Report.Check( "the rim is offered to an edge pick",
			surface.TryClosestEdge( atRim, out _, out var distance ) && distance < 1e-3f,
			$"{distance}" );
	}

	/// <summary>
	/// "Select the top, then Fillet" goes through FacePlane.CaptureBoundary, and on a fragmented
	/// wall that used to mean one triangle's three sides - two of which are seams lying flat in the
	/// middle of the wall. Blending along those does nothing, and the third edge is a third of one
	/// real edge, so the fillet came out as a stub.
	/// </summary>
	static void TestCaptureBoundary()
	{
		var mesh = Primitives.Box( 4f, 4f, 2f );
		var top = TopFace( mesh );
		var corners = mesh.Faces[top].Indices;

		mesh.Faces.RemoveAt( top );
		mesh.AddFace( new[] { corners[0], corners[1], corners[2] } );
		mesh.AddFace( new[] { corners[0], corners[2], corners[3] } );

		var body = new Body( "split", "Split", mesh );
		var edges = FacePlane.CaptureBoundary( body, TopFace( mesh ) );

		Report.Check( "a fragmented top gives the four edges of the square",
			edges.Count == 4, $"{edges.Count} edges" );

		// Every one of them a unit from the middle of the top face in plan, which the diagonal's
		// midpoint - sitting right on the centre - is not.
		var square = edges.All( e =>
			MathF.Abs( MathF.Max( MathF.Abs( e.Point.x ), MathF.Abs( e.Point.y ) ) - 2f ) < 1e-3f );

		Report.Check( "and none of them is the seam across it", square );

		// The unfragmented case is the one everything already depended on, so it has to be
		// unchanged rather than merely still reasonable.
		var whole = Primitives.Box( 4f, 4f, 2f );
		var wholeEdges = FacePlane.CaptureBoundary( new Body( "whole", "Whole", whole ), TopFace( whole ) );

		Report.Check( "an uncut top still gives its own four edges",
			wholeEdges.Count == 4, $"{wholeEdges.Count} edges" );
	}
}
