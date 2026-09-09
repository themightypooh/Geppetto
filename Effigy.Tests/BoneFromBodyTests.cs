using System;
using System.Collections.Generic;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Deriving a bone from the shape of the body it will move.
///
/// The theme here is that the answers must be STABLE, not merely plausible. A derived bone that is
/// a little off is easy to nudge; a derived bone that comes out differently on two identical
/// fingers, or flips end-for-end when the model is rebuilt, is worse than no feature at all,
/// because the modeller has to check every one by eye before trusting any of them. So most of what
/// follows measures repeatability and orientation rather than precision.
/// </summary>
public static class BoneFromBodyTests
{
	public static void Run()
	{
		Section( "a bone spans the body's longest axis" );
		TestLongestAxis();

		Section( "roll follows the widest direction across the bone" );
		TestRoll();

		Section( "an anchor decides which end is the head" );
		TestAnchor();

		Section( "shapes with no answer are refused rather than guessed" );
		TestDegenerate();

		Section( "the same shape always derives the same bone" );
		TestRepeatable();

		Section( "a derived bone feeds Skeleton unchanged" );
		TestFeedsSkeleton();
	}

	/// <summary>The eight corners of an axis-aligned box from the origin to (sx, sy, sz).</summary>
	static List<Vec3> Box( float sx, float sy, float sz )
	{
		var points = new List<Vec3>();

		for ( var i = 0; i < 8; i++ )
		{
			points.Add( new Vec3(
				(i & 1) == 0 ? 0 : sx,
				(i & 2) == 0 ? 0 : sy,
				(i & 4) == 0 ? 0 : sz ) );
		}

		return points;
	}

	static void TestLongestAxis()
	{
		// Four long, two wide, one thick — a finger, near enough, and unambiguous about which axis
		// is which.
		var ok = BoneFromBody.TryDerive( Box( 4, 1, 2 ), out var head, out var tail, out _ );

		Check( "a box derives a bone", ok );
		Check( "the bone aims down the long axis",
			Near( (tail - head).Normal, new Vec3( 1, 0, 0 ) ), (tail - head).Normal.ToString() );
		Check( "it spans the full extent", Near( (tail - head).Length, 4f ), (tail - head).Length.ToString() );
		Check( "it sits on the body's centre line", Near( head, new Vec3( 0, 0.5f, 1 ) ), head.ToString() );

		// Rotated onto a diagonal: nothing about the method is axis-aligned, and a body built on a
		// sketch plane that is not one of the three standard ones is the normal case, not the
		// exotic one.
		var diagonal = new List<Vec3>();
		var along = new Vec3( 1, 1, 0 ).Normal;

		foreach ( var p in Box( 4, 1, 2 ) )
		{
			// Re-express the box about the diagonal: x runs along it, y across it in plane.
			diagonal.Add( along * p.x + new Vec3( -along.y, along.x, 0 ) * p.y + new Vec3( 0, 0, 1 ) * p.z );
		}

		BoneFromBody.TryDerive( diagonal, out var dHead, out var dTail, out _ );

		Check( "a diagonal body aims down its own long axis",
			Near( (dTail - dHead).Normal, along ), (dTail - dHead).Normal.ToString() );
		Check( "and still spans four units", Near( (dTail - dHead).Length, 4f ),
			(dTail - dHead).Length.ToString() );
	}

	static void TestRoll()
	{
		// Long in x, thin in y, wide in z. The roll should name z, because that is the direction
		// the section actually has width in — it is what an animator would call the flat of it.
		BoneFromBody.TryDerive( Box( 4, 1, 2 ), out _, out _, out var up );

		Check( "roll takes the wide direction, not the thin one",
			Near( up, new Vec3( 0, 0, 1 ) ) || Near( up, new Vec3( 0, 0, -1 ) ), up.ToString() );

		// Swap which of the two cross directions is wider and the roll has to follow. This is the
		// check that would fail if the axes were simply being read off in a fixed order.
		BoneFromBody.TryDerive( Box( 4, 2, 1 ), out _, out _, out var swapped );

		Check( "widening the other way rolls the bone",
			Near( swapped, new Vec3( 0, 1, 0 ) ) || Near( swapped, new Vec3( 0, -1, 0 ) ),
			swapped.ToString() );

		BoneFromBody.TryDerive( Box( 4, 1, 2 ), out _, out var tail, out var perpendicular );
		BoneFromBody.TryDerive( Box( 4, 1, 2 ), out var head, out _, out _ );

		Check( "the hint is already perpendicular to the aim",
			MathF.Abs( Vec3.Dot( perpendicular, (tail - head).Normal ) ) < 1e-4f,
			Vec3.Dot( perpendicular, (tail - head).Normal ).ToString() );
		Check( "and is unit length", Near( perpendicular.Length, 1f ), perpendicular.Length.ToString() );
	}

	static void TestAnchor()
	{
		var box = Box( 4, 1, 2 );

		// No anchor: canonical, and the canon is +x, so the head is the low end.
		BoneFromBody.TryDerive( box, out var head, out var tail, out _ );
		Check( "unanchored, the head is the canonical end", head.x < tail.x, $"{head} -> {tail}" );

		// Anchored past the far end, the bone has to turn around — that end is where it joins the
		// rest of the model, and a bone growing back toward its own parent is the bug this stops.
		BoneFromBody.TryDerive( box, out var flipped, out var flippedTail, out _, new Vec3( 9, 0, 0 ) );

		Check( "an anchor at the far end swaps the ends", flipped.x > flippedTail.x,
			$"{flipped} -> {flippedTail}" );
		Check( "swapping moves neither point", Near( flipped, tail ) && Near( flippedTail, head ),
			$"{flipped} / {flippedTail}" );

		// An anchor on the side it already pointed at must change nothing at all.
		BoneFromBody.TryDerive( box, out var kept, out var keptTail, out _, new Vec3( -9, 0, 0 ) );

		Check( "an anchor at the near end leaves it alone",
			Near( kept, head ) && Near( keptTail, tail ), $"{kept} -> {keptTail}" );
	}

	static void TestDegenerate()
	{
		Check( "no points is refused",
			!BoneFromBody.TryDerive( new List<Vec3>(), out _, out _, out _ ) );
		Check( "one point is refused",
			!BoneFromBody.TryDerive( new List<Vec3> { Vec3.Zero }, out _, out _, out _ ) );
		Check( "a null list is refused",
			!BoneFromBody.TryDerive( (IReadOnlyList<Vec3>)null, out _, out _, out _ ) );
		Check( "a null mesh is refused",
			!BoneFromBody.TryDerive( (PolyMesh)null, out _, out _, out _ ) );

		// Every point in the same place has no axis and no length. Refusing beats returning a
		// zero-length bone, which Skeleton would throw on anyway, further from the cause.
		var stacked = new List<Vec3> { new( 1, 1, 1 ), new( 1, 1, 1 ), new( 1, 1, 1 ) };
		Check( "a body with no extent is refused",
			!BoneFromBody.TryDerive( stacked, out _, out _, out _ ) );

		// A CUBE HAS NO LONGEST AXIS and that is not an error — every direction is a legitimate
		// principal axis, so there is nothing to be right about. It must still produce a usable
		// bone rather than a NaN or a refusal.
		var cube = BoneFromBody.TryDerive( Box( 2, 2, 2 ), out var head, out var tail, out var up );

		Check( "a cube still derives a bone", cube );
		Check( "the cube's bone has real length",
			cube && (tail - head).Length > 1e-3f && !float.IsNaN( (tail - head).Length ),
			cube ? (tail - head).Length.ToString() : "refused" );
		Check( "and a real up-hint", cube && Near( up.Length, 1f ), up.Length.ToString() );
	}

	static void TestRepeatable()
	{
		// The property the whole feature leans on: a patterned row of eight fingers has to come out
		// as eight bones pointing the same way. Same shape in, same bone out, every time.
		BoneFromBody.TryDerive( Box( 4, 1, 2 ), out var headA, out var tailA, out var upA );
		BoneFromBody.TryDerive( Box( 4, 1, 2 ), out var headB, out var tailB, out var upB );

		Check( "two identical bodies derive identical bones",
			Near( headA, headB ) && Near( tailA, tailB ) && Near( upA, upB ) );

		// Order of the vertices is not part of the shape, and a rebuild is free to change it.
		var shuffled = Box( 4, 1, 2 );
		shuffled.Reverse();

		BoneFromBody.TryDerive( shuffled, out var headC, out var tailC, out var upC );

		Check( "vertex order does not change the answer",
			Near( headA, headC ) && Near( tailA, tailC ) && Near( upA, upC ),
			$"{headC} -> {tailC}, up {upC}" );

		// Translated bodies are the same shape somewhere else, so the bone must simply move with
		// them — a pattern is a translation, and this is that case.
		var moved = new List<Vec3>();
		var shift = new Vec3( 10, -3, 7 );

		foreach ( var p in Box( 4, 1, 2 ) )
			moved.Add( p + shift );

		BoneFromBody.TryDerive( moved, out var headD, out var tailD, out var upD );

		Check( "a translated body derives a translated bone",
			Near( headD, headA + shift ) && Near( tailD, tailA + shift ) && Near( upD, upA ),
			$"{headD} -> {tailD}" );
	}

	static void TestFeedsSkeleton()
	{
		// The output exists to be handed straight to AddBoneFromPoints, up-hint and all, so check
		// the bone that actually lands rather than only the numbers on the way in.
		BoneFromBody.TryDerive( Box( 4, 1, 2 ), out var head, out var tail, out var up );

		var skeleton = new Skeleton();
		skeleton.AddBoneFromPoints( "finger", -1, head, tail, up );

		var world = skeleton.WorldBind( 0 );

		Check( "the bone's +Y runs down the body", Near( world.Y, new Vec3( 1, 0, 0 ) ), world.Y.ToString() );
		Check( "the bone's +Z took the derived roll", Near( world.Z, up ), world.Z.ToString() );
		Check( "the bone starts at the derived head", Near( world.Origin, head ), world.Origin.ToString() );
		Check( "its length is the body's extent", Near( skeleton.Bones[0].Length, 4f ),
			skeleton.Bones[0].Length.ToString() );
	}

	static bool Near( Vec3 a, Vec3 b, float tolerance = 1e-4f ) => (a - b).Length < tolerance;

	static bool Near( float a, float b, float tolerance = 1e-4f ) => MathF.Abs( a - b ) < tolerance;

	static void Section( string title ) => Report.Section( title );

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
