using System;

namespace Effigy.Tests;

/// <summary>
/// The face across a wall. The viewport only decides when to ask.
/// </summary>
public static class FacingPairTests
{
	public static void Run()
	{
		Report.Section( "facing pair: the far side of a solid" );
		TestSolidBox();

		Report.Section( "facing pair: the inner face of a shell, not the far side" );
		TestShell();

		Report.Section( "facing pair: a fragment of the same wall is not across from it" );
		TestSplitFace();
	}

	static int FaceAlong( PolyMesh mesh, Vec3 normal, bool outer )
	{
		var best = -1;
		var bestDot = outer ? float.MinValue : float.MaxValue;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			var n = mesh.FaceNormal( mesh.Faces[i] );

			if ( Vec3.Dot( n, normal ) < 0.9f )
				continue;

			var along = Vec3.Dot( mesh.FaceCentroid( mesh.Faces[i] ), normal );

			if ( outer ? along > bestDot : along < bestDot )
			{
				bestDot = along;
				best = i;
			}
		}

		return best;
	}

	static void TestSolidBox()
	{
		var mesh = Primitives.Box( 10f, 4f, 6f );
		var front = FaceAlong( mesh, new Vec3( 1, 0, 0 ), outer: true );
		var ok = MeshRaycast.TryOpposite( mesh, front, out var other, out _ );
		var back = FaceAlong( mesh, new Vec3( -1, 0, 0 ), outer: true );

		Report.Check( "the +X face finds the -X face", ok && other == back, $"got {other}, wanted {back}" );

		var gap = ok ? MathF.Abs( mesh.FaceCentroid( mesh.Faces[front] ).x - mesh.FaceCentroid( mesh.Faces[other] ).x ) : -1f;
		Report.Check( "and they are the full 10 in apart", MathF.Abs( gap - 10f ) < 1e-3f, $"{gap}" );
	}

	static void TestShell()
	{
		var mesh = ShellOperation.Shell( Primitives.Box( 10f, 4f, 6f ), 0.25f );
		var front = FaceAlong( mesh, new Vec3( 1, 0, 0 ), outer: true );
		var ok = MeshRaycast.TryOpposite( mesh, front, out var other, out _ );
		var gap = ok
			? (mesh.FaceCentroid( mesh.Faces[front] ) - mesh.FaceCentroid( mesh.Faces[other] )).Length
			: -1f;

		Report.Check( "the outer face finds a face", ok, "none" );
		Report.Check( "a wall's thickness away, not the far side of the part", ok && MathF.Abs( gap - 0.25f ) < 0.02f, $"{gap}" );
		Report.Check( "that face points back", ok && Vec3.Dot( mesh.FaceNormal( mesh.Faces[other] ), new Vec3( -1, 0, 0 ) ) > 0.9f );
	}

	static void TestSplitFace()
	{
		// A box whose +X face is two coplanar quads sharing an edge. Those two are one wall.
		// The face across from either half is the -X face, not the other half.
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( -1, -1, -1 ) ); // 0
		mesh.AddVertex( new Vec3( 1, -1, -1 ) );  // 1
		mesh.AddVertex( new Vec3( 1, 1, -1 ) );   // 2
		mesh.AddVertex( new Vec3( -1, 1, -1 ) );  // 3
		mesh.AddVertex( new Vec3( -1, -1, 1 ) );  // 4
		mesh.AddVertex( new Vec3( 1, -1, 1 ) );   // 5
		mesh.AddVertex( new Vec3( 1, 1, 1 ) );    // 6
		mesh.AddVertex( new Vec3( -1, 1, 1 ) );   // 7
		mesh.AddVertex( new Vec3( 1, 0, -1 ) );   // 8  mid of 1-2
		mesh.AddVertex( new Vec3( 1, 0, 1 ) );    // 9  mid of 5-6

		mesh.AddFace( new[] { 0, 3, 2, 8, 1 } ); // 0 -Z
		mesh.AddFace( new[] { 4, 5, 9, 6, 7 } ); // 1 +Z
		mesh.AddFace( new[] { 0, 1, 5, 4 } );    // 2 -Y
		mesh.AddFace( new[] { 1, 8, 9, 5 } );    // 3 +X lower
		mesh.AddFace( new[] { 8, 2, 6, 9 } );    // 4 +X upper
		mesh.AddFace( new[] { 2, 3, 7, 6 } );    // 5 +Y
		mesh.AddFace( new[] { 3, 0, 4, 7 } );    // 6 -X

		var ok = MeshRaycast.TryOpposite( mesh, 3, out var other, out _ );

		Report.Check( "the lower half finds something", ok );
		Report.Check( "and it is not the upper half of the same wall", ok && other != 4, $"got {other}" );
		Report.Check( "it is the -X face", ok && other == 6, $"got {other}" );
	}
}
