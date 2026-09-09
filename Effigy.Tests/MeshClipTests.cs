using System;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

public static class MeshClipTests
{
	public static void Run()
	{
		Section( "mesh clip: a cutaway keeps the positive half" );
		TestKeepsThePositiveSide();
		TestStraddlingFaceIsCut();
		TestWholeMeshOnTheKeptSideIsUnchanged();
	}

	static void TestKeepsThePositiveSide()
	{
		var box = Primitives.Box( 2f, 2f, 2f );
		var clipped = MeshClip.Keep( box, Vec3.Zero, new Vec3( 0, 0, 1 ) );

		Check( "the cut keeps some faces", clipped.FaceCount > 0, $"{clipped.FaceCount}" );

		var below = 0;

		foreach ( var p in clipped.Positions )
		{
			if ( p.z < -1e-4f )
				below++;
		}

		Check( "no vertex sits strictly below the plane", below == 0, $"{below} below" );
	}

	static void TestStraddlingFaceIsCut()
	{
		var box = Primitives.Box( 2f, 2f, 2f );
		var clipped = MeshClip.Keep( box, Vec3.Zero, new Vec3( 0, 0, 1 ) );

		var onPlane = 0;

		foreach ( var p in clipped.Positions )
		{
			if ( MathF.Abs( p.z ) <= 1e-4f )
				onPlane++;
		}

		Check( "faces that straddled the plane emitted vertices on it", onPlane > 0, $"{onPlane} on plane" );
	}

	static void TestWholeMeshOnTheKeptSideIsUnchanged()
	{
		var box = Primitives.Box( 2f, 2f, 2f );
		var clipped = MeshClip.Keep( box, new Vec3( 0, 0, -10f ), new Vec3( 0, 0, 1 ) );

		Check( "a plane that misses keeps every face",
			clipped.FaceCount == box.FaceCount, $"{clipped.FaceCount} vs {box.FaceCount}" );
	}
}
