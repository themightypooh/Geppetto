using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Draft, measured rather than eyeballed.
///
/// The failure this guards is the one every offset-like operation has: leaned too far, a wall does
/// not fail, it turns inside out — and the result is closed, manifold, Euler-correct and wrong. Area
/// alone cannot see it, because area has no sign in three dimensions. Only the face normal reversing
/// says so.
/// </summary>
public static class DraftTests
{
	public static void Run()
	{
		Section( "draft: tapering faces that already exist" );
		TestABoxLeansOneWayEitherSideOfTheNeutralPlane();
		TestTheNeutralPlaneIsWhereNothingMoves();
		TestASignChangeReversesTheTaper();
		TestAFaceLookingAlongThePullCannotBeDrafted();
		TestTooMuchDraftIsRefusedRatherThanInverted();
		TestTheRefusalNamesAnAngleThatWorks();
		TestOnlyPickedFacesMove();
	}

	/// <summary>The four walls of the fixture box — everything a draft along Z can act on.</summary>
	static List<int> Walls( PolyMesh mesh )
	{
		var walls = new List<int>();

		for ( var i = 0; i < mesh.FaceCount; i++ )
		{
			if ( MathF.Abs( mesh.FaceNormal( mesh.Faces[i] ).Normal.z ) < 0.01f )
				walls.Add( i );
		}

		return walls;
	}

	static float WidthAt( PolyMesh mesh, float z )
	{
		var widest = 0f;

		foreach ( var p in mesh.Positions )
		{
			if ( MathF.Abs( p.z - z ) < 1e-3f )
				widest = MathF.Max( widest, MathF.Abs( p.x ) );
		}

		return widest;
	}

	static void TestABoxLeansOneWayEitherSideOfTheNeutralPlane()
	{
		// A 2x2x2 box, neutral at the middle, drafted 10 degrees. The top has to come in and the
		// bottom go out - a taper that moved both ends the same way is a scale, not a draft.
		var box = Primitives.Box( 2, 2, 2 );
		var drafted = DraftOperation.Draft( box, Walls( box ), Vec3.Zero, new Vec3( 0, 0, 1 ), 10f );

		var topBefore = WidthAt( box, 1f );
		var bottomBefore = WidthAt( box, -1f );
		var top = WidthAt( drafted, 1f );
		var bottom = WidthAt( drafted, -1f );

		Check( "the top of the wall moves out with the pull", top > topBefore + 0.05f,
			$"{topBefore:0.###} became {top:0.###}" );
		Check( "and the bottom moves in", bottom < bottomBefore - 0.05f,
			$"{bottomBefore:0.###} became {bottom:0.###}" );

		// tan(10 deg) = 0.176, over one unit of height either side of the neutral plane.
		var expected = MathF.Tan( 10f * MathF.PI / 180f );

		Check( "by the angle it was asked for", MathF.Abs( (top - topBefore) - expected ) < 1e-3f,
			$"moved {top - topBefore:0.####}, tan(10) is {expected:0.####}" );
	}

	static void TestTheNeutralPlaneIsWhereNothingMoves()
	{
		// The parting line. A draft whose neutral plane did not hold still would move the whole part
		// as well as taper it, and every feature built on it downstream would shift.
		var box = Primitives.Box( 2, 2, 4 );
		var drafted = DraftOperation.Draft( box, Walls( box ), new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 8f );

		// The fixture has no vertices at z = 1, so this is checked by where the wall crosses it:
		// the width there must be what it was.
		var atNeutral = 0f;
		var count = 0;

		for ( var i = 0; i < box.VertexCount; i++ )
		{
			var before = box.Positions[i];
			var after = drafted.Positions[i];
			var distance = before.z - 1f;

			// Every vertex must have moved by exactly its distance from the plane times the tangent.
			// Every corner of a box belongs to two drafted walls, so it travels along the diagonal
			// far enough that EACH wall leans by the angle - which is sqrt(2) times the lean itself.
			var moved = (after - before).Length;
			var expected = MathF.Abs( distance ) * MathF.Tan( 8f * MathF.PI / 180f ) * MathF.Sqrt( 2f );

			atNeutral += MathF.Abs( moved - expected );
			count++;
		}

		Check( "every vertex moved in proportion to its distance from the neutral plane",
			atNeutral / count < 1e-3f, $"mean error {atNeutral / count:0.#####}" );

		var onPlane = DraftOperation.Draft( box, Walls( box ), new Vec3( 0, 0, -2 ), new Vec3( 0, 0, 1 ), 8f );
		var bottomMoved = 0f;

		for ( var i = 0; i < box.VertexCount; i++ )
		{
			if ( MathF.Abs( box.Positions[i].z + 2f ) < 1e-3f )
				bottomMoved = MathF.Max( bottomMoved, (onPlane.Positions[i] - box.Positions[i]).Length );
		}

		Check( "and a vertex ON the plane does not move at all", bottomMoved < 1e-5f,
			$"moved {bottomMoved:0.######}" );
	}

	static void TestASignChangeReversesTheTaper()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var walls = Walls( box );

		var positive = DraftOperation.Draft( box, walls, Vec3.Zero, new Vec3( 0, 0, 1 ), 6f );
		var negative = DraftOperation.Draft( box, walls, Vec3.Zero, new Vec3( 0, 0, 1 ), -6f );

		Check( "a negative angle tapers the other way",
			WidthAt( positive, 1f ) > 1f && WidthAt( negative, 1f ) < 1f,
			$"{WidthAt( positive, 1f ):0.###} against {WidthAt( negative, 1f ):0.###}" );
	}

	static void TestAFaceLookingAlongThePullCannotBeDrafted()
	{
		// Not a small effect - not an operation. A face whose normal IS the pull has no horizontal
		// component to lean, and saying so beats silently doing nothing.
		var box = Primitives.Box( 2, 2, 2 );
		var caps = new List<int>();

		for ( var i = 0; i < box.FaceCount; i++ )
		{
			if ( MathF.Abs( box.FaceNormal( box.Faces[i] ).Normal.z ) > 0.99f )
				caps.Add( i );
		}

		Check( "the fixture has a top and a bottom to try it on", caps.Count == 2, $"{caps.Count}" );
		Check( "drafting them along the pull is refused",
			Throws( () => DraftOperation.Draft( box, caps, Vec3.Zero, new Vec3( 0, 0, 1 ), 5f ), out var why ) );
		Check( "and the refusal says why rather than naming an angle",
			why is not null && why.Contains( "straight along the pull" ), why ?? "" );
	}

	static void TestTooMuchDraftIsRefusedRatherThanInverted()
	{
		// THE CHECK THIS WHOLE FILE EXISTS FOR. A wall leaned past vertical folds through itself and
		// comes back closed, manifold and Euler-correct.
		var box = Primitives.Box( 2, 2, 8 );
		var walls = Walls( box );

		Check( "a sane draft on a tall box builds",
			!Throws( () => DraftOperation.Draft( box, walls, Vec3.Zero, new Vec3( 0, 0, 1 ), 5f ), out _ ) );

		Check( "and one that folds the wall through itself is refused",
			Throws( () => DraftOperation.Draft( box, walls, Vec3.Zero, new Vec3( 0, 0, 1 ), 30f ), out var why ) );

		Check( "named as a fold, an inversion or a collapse, not as a vague failure",
			why is not null && (why.Contains( "folds" ) || why.Contains( "inside out" ) || why.Contains( "collapses" )),
			why ?? "" );

		Check( "an angle past vertical is refused outright",
			Throws( () => DraftOperation.Draft( box, walls, Vec3.Zero, new Vec3( 0, 0, 1 ), 91f ), out _ ) );
	}

	static void TestTheRefusalNamesAnAngleThatWorks()
	{
		// A refusal that names a number you can act on is worth ten that only say no - the same trick
		// fillet and shell use.
		var box = Primitives.Box( 2, 2, 8 );
		var walls = Walls( box );
		var largest = DraftOperation.LargestAngle( box, walls, Vec3.Zero, new Vec3( 0, 0, 1 ), 30f );

		Check( "the largest angle it offers is smaller than the one refused", largest > 0f && largest < 30f,
			$"{largest:0.###}" );
		Check( "and that angle actually builds",
			!Throws( () => DraftOperation.Draft( box, walls, Vec3.Zero, new Vec3( 0, 0, 1 ), largest ), out _ ),
			$"{largest:0.###} was offered and does not work" );
	}

	static void TestOnlyPickedFacesMove()
	{
		// A vertex on the boundary of the selection also belongs to faces that are staying put. Its
		// draft has to come from the picked faces alone, or the corner leans by an amount that has
		// nothing to do with what was asked for.
		var box = Primitives.Box( 2, 2, 2 );
		var walls = Walls( box );
		var one = new List<int> { walls[0] };

		var drafted = DraftOperation.Draft( box, one, Vec3.Zero, new Vec3( 0, 0, 1 ), 10f );
		var picked = new HashSet<int>( box.Faces[walls[0]].Indices );
		var strayed = 0;

		for ( var i = 0; i < box.VertexCount; i++ )
		{
			if ( picked.Contains( i ) )
				continue;

			if ( (drafted.Positions[i] - box.Positions[i]).Length > 1e-6f )
				strayed++;
		}

		Check( "drafting one face moves only its own corners", strayed == 0, $"{strayed} others moved" );

		var moved = picked.Count( i => (drafted.Positions[i] - box.Positions[i]).Length > 1e-6f );

		Check( "and it does move them", moved > 0, $"{moved} of {picked.Count}" );
	}

	static bool Throws( Action action, out string message )
	{
		message = null;

		try
		{
			action();
			return false;
		}
		catch ( Exception e )
		{
			message = e.Message;
			return true;
		}
	}
}
