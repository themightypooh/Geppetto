using System;
using System.Collections.Generic;

namespace Effigy.Tests;

/// <summary>
/// Delete faces and close the flat opening. The viewport only decides which faces.
/// </summary>
public static class FaceDeleteTests
{
	public static void Run()
	{
		Report.Section( "delete face: a boss comes off and the wall closes" );
		TestBoss();

		Report.Section( "delete face: one face of a box is put back" );
		TestBoxCap();

		Report.Section( "delete face: a bent opening is refused" );
		TestRefusals();

		Report.Section( "delete face: the feature records it" );
		TestFeature();
	}

	static void TestBoss()
	{
		var mesh = Boss();
		var beforeFaces = mesh.FaceCount;
		var before = MeshValidator.Validate( mesh );

		Report.Check( "the boss starts closed", before.IsClosed && before.IsValid, before.ToString() );
		Report.Check( "and its volume includes the boss", Nearly( mesh.SignedVolume(), 208f ),
			$"{mesh.SignedVolume()}" );

		// Walls then the lid. Face order is fixed by Boss().
		var healed = FaceDelete.Remove( mesh, new[] { 9, 10, 11, 12, 13 } );

		Report.Check( "the input mesh is untouched", mesh.FaceCount == beforeFaces, $"{mesh.FaceCount}" );

		var check = MeshValidator.Validate( healed );
		Report.Check( "the wall closes", check.IsClosed && check.IsValid, check.ToString() );
		Report.Check( "one piece", MeshSplit.PieceCount( healed ) == 1, $"{MeshSplit.PieceCount( healed )}" );
		Report.Check( "the boss volume is gone", Nearly( healed.SignedVolume(), 200f ),
			$"{healed.SignedVolume()}" );
		Report.Check( "nothing sticks up past the wall", Highest( healed ) < 2.01f, $"{Highest( healed )}" );

		var top = 0f;

		foreach ( var face in healed.Faces )
		{
			if ( healed.FaceNormal( face ).z > 0.9f )
				top = MathF.Max( top, healed.FaceArea( face ) );
		}

		Report.Check( "the top is one face again", top > 90f, $"{top}" );
	}

	static void TestBoxCap()
	{
		var box = Primitives.Box( 2f, 2f, 2f );
		var healed = FaceDelete.Remove( box, new[] { 1 } );
		var check = MeshValidator.Validate( healed );

		Report.Check( "still closed", check.IsClosed && check.IsValid, check.ToString() );
		Report.Check( "same volume", Nearly( healed.SignedVolume(), box.SignedVolume() ),
			$"{healed.SignedVolume()} vs {box.SignedVolume()}" );
		Report.Check( "still six faces", healed.FaceCount == 6, $"{healed.FaceCount}" );
		Report.Check( "still outward", healed.SignedVolume() > 0f, $"{healed.SignedVolume()}" );
	}

	static void TestRefusals()
	{
		var box = Primitives.Box( 2f, 2f, 2f );
		Report.Check( "three faces around a corner are not a flat mouth",
			Refused( box, new[] { 1, 3, 4 } ), "it closed" );
		Report.Check( "and the box is unchanged", box.FaceCount == 6, $"{box.FaceCount}" );

		Report.Check( "deleting every face is refused",
			Refused( box, new[] { 0, 1, 2, 3, 4, 5 } ), "it removed the part" );

		var open = Primitives.Plane();
		Report.Check( "an open mesh is refused", Refused( open, new[] { 0 } ), "it capped a mesh that was already open" );
		Report.Check( "and left alone", open.FaceCount == 1, $"{open.FaceCount}" );
	}

	static void TestFeature()
	{
		var studio = new PartStudio();
		var primitive = studio.Add( new PrimitiveFeature() );
		primitive.SizeX.Value = 2f;
		primitive.SizeY.Value = 2f;
		primitive.SizeZ.Value = 2f;
		studio.Rebuild();

		var empty = studio.Add( new DeleteFaceFeature() );
		studio.Rebuild();
		Report.Check( "no faces is a refusal", empty.Error is not null, empty.Error ?? "no error" );

		studio.Features.Remove( empty );
		var body = studio.Bodies[0];
		var delete = studio.Add( new DeleteFaceFeature() );

		for ( var i = 0; i < body.Mesh.Faces.Count; i++ )
		{
			if ( body.Mesh.FaceNormal( body.Mesh.Faces[i] ).z > 0.99f )
			{
				delete.Faces.Add( FacePlane.Capture( body, i, body.Mesh.FaceCentroid( body.Mesh.Faces[i] ) ) );
				break;
			}
		}

		studio.Rebuild();
		body = studio.Bodies[0];
		var check = MeshValidator.Validate( body.Mesh );

		Report.Check( "a picked top face builds", delete.Error is null, delete.Error ?? "" );
		Report.Check( "and the box is still a box",
			check.IsClosed && Nearly( body.Mesh.SignedVolume(), 8f ),
			delete.Error ?? $"{body.Mesh.SignedVolume()} {check}" );
	}

	static bool Refused( PolyMesh mesh, IReadOnlyList<int> faces )
	{
		try
		{
			FaceDelete.Remove( mesh, faces );
			return false;
		}
		catch ( InvalidOperationException )
		{
			return true;
		}
	}

	static float Highest( PolyMesh mesh )
	{
		var highest = float.NegativeInfinity;

		foreach ( var face in mesh.Faces )
		{
			foreach ( var index in face.Indices )
				highest = MathF.Max( highest, mesh.Positions[index].z );
		}

		return highest;
	}

	static bool Nearly( float value, float expected ) => MathF.Abs( value - expected ) < 0.05f;

	/// <summary>A 10×10×2 slab with a 2×2×2 boss on top. Faces 9–12 are the boss walls, 13 the lid.
	/// Volume 208 while the boss is on.</summary>
	static PolyMesh Boss()
	{
		var mesh = new PolyMesh();

		mesh.AddVertex( new Vec3( -5, -5, 0 ) );
		mesh.AddVertex( new Vec3( 5, -5, 0 ) );
		mesh.AddVertex( new Vec3( 5, 5, 0 ) );
		mesh.AddVertex( new Vec3( -5, 5, 0 ) );
		mesh.AddVertex( new Vec3( -5, -5, 2 ) );
		mesh.AddVertex( new Vec3( 5, -5, 2 ) );
		mesh.AddVertex( new Vec3( 5, 5, 2 ) );
		mesh.AddVertex( new Vec3( -5, 5, 2 ) );
		mesh.AddVertex( new Vec3( -1, -1, 2 ) );
		mesh.AddVertex( new Vec3( 1, -1, 2 ) );
		mesh.AddVertex( new Vec3( 1, 1, 2 ) );
		mesh.AddVertex( new Vec3( -1, 1, 2 ) );
		mesh.AddVertex( new Vec3( -1, -1, 4 ) );
		mesh.AddVertex( new Vec3( 1, -1, 4 ) );
		mesh.AddVertex( new Vec3( 1, 1, 4 ) );
		mesh.AddVertex( new Vec3( -1, 1, 4 ) );

		mesh.AddFace( new[] { 0, 3, 2, 1 } );
		mesh.AddFace( new[] { 0, 1, 5, 4 } );
		mesh.AddFace( new[] { 1, 2, 6, 5 } );
		mesh.AddFace( new[] { 2, 3, 7, 6 } );
		mesh.AddFace( new[] { 3, 0, 4, 7 } );
		mesh.AddFace( new[] { 4, 5, 9, 8 } );
		mesh.AddFace( new[] { 5, 6, 10, 9 } );
		mesh.AddFace( new[] { 6, 7, 11, 10 } );
		mesh.AddFace( new[] { 7, 4, 8, 11 } );
		mesh.AddFace( new[] { 8, 9, 13, 12 } );
		mesh.AddFace( new[] { 9, 10, 14, 13 } );
		mesh.AddFace( new[] { 10, 11, 15, 14 } );
		mesh.AddFace( new[] { 11, 8, 12, 15 } );
		mesh.AddFace( new[] { 12, 13, 14, 15 } );

		return mesh;
	}
}
