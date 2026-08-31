using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Welding a surface that arrived in pieces back into as few faces as it can be.
///
/// WHAT THESE ARE ACTUALLY PROTECTING. Not the geometry - a fragmented face and a welded one
/// enclose the same volume, pass the same validator and have the same Euler characteristic, which
/// is exactly why the defect survived the whole cut effort unnoticed. What changes is the number of
/// things a person has to click to paint one flat wall, and the only way to test that is to count
/// faces and insist the count is the smallest legal one.
///
/// So every test here asserts a face COUNT, and then asserts that the mesh is still correct - in
/// that order, because the second half was never in doubt and the first is the whole point.
/// </summary>
public static class CoplanarMergeTests
{
	public static void Run()
	{
		Report.Section( "coplanar merge: fragments of one surface become one face" );
		TestGridBecomesOneFace();
		TestSplitQuadBecomesOneQuad();

		Report.Section( "coplanar merge: what it refuses to weld" );
		TestBoxIsLeftAlone();
		TestMaterialsAreNotWeldedAcross();
		TestOppositeFacingsAreNotWelded();

		Report.Section( "coplanar merge: a surface with a hole is n+1 faces" );
		TestAnnulusBecomesTwoFaces();

		Report.Section( "coplanar merge: the solid survives it" );
		TestSolidIsUnchanged();

		Report.Section( "coplanar merge: the measure that would have caught it" );
		TestFragmentationMeasure();
	}

	/// <summary>
	/// The diagnostic number, checked against cases whose answer is known by hand. It exists
	/// because every other measure - closed, manifold, Euler, volume, largest face - reported a
	/// perfect mesh while one wall of it was 88 separate faces.
	/// </summary>
	static void TestFragmentationMeasure()
	{
		var box = Primitives.Box( 2, 2, 2 );
		Check( "a box has no fragmented surface", CoplanarMerge.LargestFragmentedSurface( box ) == 0,
			$"got {CoplanarMerge.LargestFragmentedSurface( box )}" );

		var plane = Primitives.Plane( 4f, 4f, 4, 4 );
		Check( "a 4x4 grid reads as 16", CoplanarMerge.LargestFragmentedSurface( plane ) == 16,
			$"got {CoplanarMerge.LargestFragmentedSurface( plane )}" );

		Fragment( box, 0 );
		Check( "a shattered box face reads as its piece count",
			CoplanarMerge.LargestFragmentedSurface( box ) == 4,
			$"got {CoplanarMerge.LargestFragmentedSurface( box )}" );

		CoplanarMerge.Merge( box );
		Check( "and reads as nothing once welded", CoplanarMerge.LargestFragmentedSurface( box ) == 0,
			$"got {CoplanarMerge.LargestFragmentedSurface( box )}" );
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );

	// --- the merges that should happen ----------------------------------------------------------

	/// <summary>
	/// The measured case, in miniature. A 4x4 grid of quads in one plane is sixteen faces that a
	/// person sees as one wall, and after the weld it is one 16-gon: four corner vertices plus
	/// three between each pair, all kept, because a vertex on the rim is shared with whatever lies
	/// beyond it and dropping it would open a crack.
	/// </summary>
	static void TestGridBecomesOneFace()
	{
		var plane = Primitives.Plane( 4f, 4f, 4, 4 );

		Check( "the grid starts fragmented", plane.FaceCount == 16, $"{plane.FaceCount} faces" );

		var removed = CoplanarMerge.Merge( plane );

		Check( "welds down to one face", plane.FaceCount == 1, $"{plane.FaceCount} faces" );
		Check( "reports what it removed", removed == 15, $"reported {removed}" );
		Check( "keeps every boundary vertex", plane.Faces[0].Count == 16,
			$"{plane.Faces[0].Count} corners" );

		var validation = MeshValidator.Validate( plane );
		Check( "still a valid mesh", validation.IsValid, validation.ToString() );
		Check( "area is unchanged", Close( plane.FaceArea( plane.Faces[0] ), 16f ),
			$"{plane.FaceArea( plane.Faces[0] )}" );
	}

	/// <summary>The smallest possible case, and the one a triangulating boolean produces most: a
	/// quad handed back as its two triangles.</summary>
	static void TestSplitQuadBecomesOneQuad()
	{
		var mesh = new PolyMesh();

		mesh.AddVertex( new Vec3( 0, 0, 0 ) );
		mesh.AddVertex( new Vec3( 1, 0, 0 ) );
		mesh.AddVertex( new Vec3( 1, 1, 0 ) );
		mesh.AddVertex( new Vec3( 0, 1, 0 ) );

		mesh.AddFace( new[] { 0, 1, 2 } );
		mesh.AddFace( new[] { 0, 2, 3 } );

		CoplanarMerge.Merge( mesh );

		Check( "two triangles become one face", mesh.FaceCount == 1, $"{mesh.FaceCount} faces" );
		Check( "and it is a quad", mesh.FaceCount == 1 && mesh.Faces[0].Count == 4,
			mesh.FaceCount == 1 ? $"{mesh.Faces[0].Count} corners" : null );
		Check( "wound the way the pieces faced",
			mesh.FaceCount == 1 && Vec3.Dot( mesh.FaceNormal( mesh.Faces[0] ), new Vec3( 0, 0, 1 ) ) > 0.99f );
	}

	// --- the merges that should not ------------------------------------------------------------

	/// <summary>A box has no two adjacent faces in one plane, so nothing may move. This is the test
	/// that fails loudly if the plane comparison is ever loosened into meaninglessness.</summary>
	static void TestBoxIsLeftAlone()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var removed = CoplanarMerge.Merge( box );

		Check( "a box is already minimal", box.FaceCount == 6, $"{box.FaceCount} faces" );
		Check( "and nothing is reported", removed == 0, $"reported {removed}" );
	}

	/// <summary>
	/// Two coplanar neighbours painted different colours are two faces because a person made them
	/// two faces. Welding them would throw away the very assignment this whole change exists to
	/// make clickable.
	/// </summary>
	static void TestMaterialsAreNotWeldedAcross()
	{
		var plane = Primitives.Plane( 2f, 1f, 2, 1 );

		Check( "two quads to start with", plane.FaceCount == 2, $"{plane.FaceCount} faces" );

		plane.Faces[1].Material = 3;

		var removed = CoplanarMerge.Merge( plane );

		Check( "different slots stay apart", plane.FaceCount == 2, $"{plane.FaceCount} faces" );
		Check( "and nothing is reported", removed == 0, $"reported {removed}" );

		// Same geometry, one slot: now it welds. Without this half the test above would pass just
		// as well on a merge that never fires at all.
		var same = Primitives.Plane( 2f, 1f, 2, 1 );
		CoplanarMerge.Merge( same );

		Check( "the same pair on one slot does weld", same.FaceCount == 1, $"{same.FaceCount} faces" );
	}

	/// <summary>
	/// Two faces in one plane pointing opposite ways are the two sides of a zero-thickness sliver,
	/// not one surface. Welding them makes a face that is its own back.
	/// </summary>
	static void TestOppositeFacingsAreNotWelded()
	{
		var mesh = new PolyMesh();

		mesh.AddVertex( new Vec3( 0, 0, 0 ) );
		mesh.AddVertex( new Vec3( 1, 0, 0 ) );
		mesh.AddVertex( new Vec3( 1, 1, 0 ) );
		mesh.AddVertex( new Vec3( 0, 1, 0 ) );

		// The same quad twice, wound opposite ways: every edge is shared, every plane agrees, and
		// the normals are exactly reversed.
		mesh.AddFace( new[] { 0, 1, 2, 3 } );
		mesh.AddFace( new[] { 3, 2, 1, 0 } );

		var removed = CoplanarMerge.Merge( mesh );

		Check( "back-to-back faces stay two", mesh.FaceCount == 2, $"{mesh.FaceCount} faces" );
		Check( "and nothing is reported", removed == 0, $"reported {removed}" );
	}

	// --- holes ----------------------------------------------------------------------------------

	/// <summary>
	/// A square patch with a square hole, fragmented into the eight quads a ring like that splits
	/// into. It cannot come back as one face - a face is one loop of corners and this surface has
	/// two boundaries - so the floor is two, which is exactly the floor SplitBridgedFace lands on
	/// coming the other way.
	/// </summary>
	static void TestAnnulusBecomesTwoFaces()
	{
		// A 3x3 grid of quads with the middle one missing: outer ring 0..15 in a 4x4 lattice.
		var mesh = new PolyMesh();
		var index = new int[4, 4];

		for ( var y = 0; y < 4; y++ )
		for ( var x = 0; x < 4; x++ )
			index[x, y] = mesh.AddVertex( new Vec3( x, y, 0 ) );

		for ( var y = 0; y < 3; y++ )
		for ( var x = 0; x < 3; x++ )
		{
			if ( x == 1 && y == 1 )
				continue;

			mesh.AddFace( new[] { index[x, y], index[x + 1, y], index[x + 1, y + 1], index[x, y + 1] } );
		}

		Check( "eight quads around a hole", mesh.FaceCount == 8, $"{mesh.FaceCount} faces" );

		CoplanarMerge.Merge( mesh );

		Check( "welds to two faces, not one and not eight", mesh.FaceCount == 2,
			$"{mesh.FaceCount} faces" );

		var validation = MeshValidator.Validate( mesh );
		Check( "still a valid mesh", validation.IsValid, validation.ToString() );

		var area = mesh.Faces.Sum( f => mesh.FaceArea( f ) );
		Check( "area is the ring's, so the hole stayed open", Close( area, 8f ), $"{area}" );
	}

	// --- the solid ------------------------------------------------------------------------------

	/// <summary>
	/// The check that matters for a real part: take a closed box, shatter one face into triangles
	/// the way a boolean does, weld it back, and confirm the solid is exactly what it was.
	///
	/// Volume rather than vertex positions, because the merge is allowed to leave a vertex nothing
	/// references any more - what it is not allowed to do is change the shape.
	/// </summary>
	static void TestSolidIsUnchanged()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var before = Volume( box );

		Fragment( box, 0 );

		Check( "the face is fragmented", box.FaceCount > 6, $"{box.FaceCount} faces" );

		var fragmented = MeshValidator.Validate( box );
		Check( "and the fragmented box is still closed", fragmented.IsClosed, fragmented.ToString() );

		CoplanarMerge.Merge( box );

		Check( "back to six faces", box.FaceCount == 6, $"{box.FaceCount} faces" );

		var validation = MeshValidator.Validate( box );
		Check( "valid", validation.IsValid, validation.ToString() );
		Check( "closed", validation.IsClosed, validation.ToString() );
		Check( "volume unchanged", Close( Volume( box ), before ),
			$"{Volume( box )} vs {before}" );

		// EULER OVER THE VERTICES STILL IN USE, not over the list.
		//
		// The fan centre this test added is interior to the welded face, so nothing references it
		// any more - and the merge deliberately leaves it in Positions rather than compacting the
		// list. Renumbering vertices is not a face pass's business: PolyMesh.Skin is parallel to
		// Positions and callers hold indices into it, so a silent compaction here would be a rig
		// quietly landing on the wrong vertices somewhere far away. An orphan costs a vertex in the
		// export and nothing else.
		//
		// So the shape is genus 0 and the raw count is one high, which is the honest answer rather
		// than the tidy one.
		var used = box.Faces.SelectMany( f => f.Indices ).Distinct().Count();
		var euler = used - box.BuildEdgeFaces().Count + box.FaceCount;

		Check( "Euler characteristic of the used vertices is 2", euler == 2, $"got {euler}" );
		Check( "exactly one vertex was orphaned", box.VertexCount - used == 1,
			$"{box.VertexCount - used} orphaned" );
	}

	/// <summary>Replace one face with a fan of triangles about its centroid — the same shape of
	/// damage a boolean does, made deliberately so the test does not need an engine.</summary>
	static void Fragment( PolyMesh mesh, int faceIndex )
	{
		var face = mesh.Faces[faceIndex];
		var centre = mesh.AddVertex( mesh.FaceCentroid( face ) );
		var material = face.Material;
		var corners = face.Indices;

		mesh.Faces.RemoveAt( faceIndex );

		for ( var i = 0; i < corners.Length; i++ )
			mesh.AddFace( new[] { corners[i], corners[(i + 1) % corners.Length], centre }, null, material );
	}

	static float Volume( PolyMesh m ) => m.SignedVolume();

	static bool Close( float a, float b, float eps = 1e-3f ) => MathF.Abs( a - b ) <= eps;
}
