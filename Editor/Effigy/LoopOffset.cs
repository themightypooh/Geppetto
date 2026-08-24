using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Move a closed 2D loop in or out by a fixed distance, measured from its EDGES.
///
/// This is what a draft angle needs: the far cap of a tapered extrude is the near cap offset by
/// distance × tan(angle), and every wall then leans by exactly that angle because both ends of it
/// are that far apart in plane.
///
/// FROM THE EDGES, NOT FROM THE VERTICES, and it is the same distinction shell is built around. Push
/// each vertex along its own bisector by d and the walls come out at the wrong angle everywhere two
/// edges do not meet at 90 degrees — a 60-degree corner ends up a different distance from its own
/// edges than a square one, so the taper varies around the profile and measures wrong while looking
/// plausible. Offsetting the EDGE LINES and intersecting the results is exact for any corner angle,
/// and it is the same reasoning PlaneOffset carries for three dimensions.
///
/// SELF-INTERSECTION IS NOT HANDLED, stated rather than discovered. Offset a loop inward by more
/// than its narrowest half-width and the result folds through itself — the classic case is a thin
/// tab that vanishes and comes back inside out. Detecting a fold in general is the same problem as a
/// 2D boolean; what this does instead is check three cheap things that between them catch every
/// practical case: the signed area keeps its sign, it has not collapsed to nothing, and no edge has
/// reversed direction. That third one is not redundant — see TryOffset for the symmetric profile
/// that passes the first two while being inside out. All three mean the same thing to a user, that
/// the taper is too steep for this profile at this distance, and saying so beats building a solid
/// that is wrong in a way nothing downstream will notice.
/// </summary>
public static class LoopOffset
{
	/// <summary>
	/// Offset a loop by <paramref name="distance"/>, positive being INWARD — toward the material,
	/// whichever way the loop is wound.
	///
	/// The loop's winding is what makes that work, and it needs no branch: see Inward. An outer loop
	/// shrinks and a hole in it widens, from one rule, which is what a draft angle does in reality —
	/// the whole section gets smaller, so the metal between an outer wall and a hole thins from both
	/// sides at once.
	/// </summary>
	public static bool TryOffset( IReadOnlyList<Vec2> loop, float distance, out List<Vec2> result, out string error )
	{
		result = null;
		error = null;

		if ( loop is null || loop.Count < 3 )
		{
			error = "a loop needs at least three points";
			return false;
		}

		var area = SignedArea( loop );

		if ( MathF.Abs( area ) < 1e-9f )
		{
			error = "the profile has no area";
			return false;
		}

		// Zero is not a special case worth guarding — it falls out as a copy — but it is the common
		// one, so it skips the work.
		if ( MathF.Abs( distance ) < 1e-9f )
		{
			result = new List<Vec2>( loop );
			return true;
		}

		var counterClockwise = area > 0f;
		var offset = new List<Vec2>( loop.Count );

		for ( var i = 0; i < loop.Count; i++ )
		{
			var previous = loop[(i - 1 + loop.Count) % loop.Count];
			var current = loop[i];
			var next = loop[(i + 1) % loop.Count];

			// The two edges meeting at this corner, each slid inward by `distance` along its own
			// normal. Intersecting the slid LINES is what makes the result exactly `distance` from
			// both edges rather than approximately from either.
			var intoA = Inward( current - previous ) * distance;
			var intoB = Inward( next - current ) * distance;

			if ( TryIntersect( previous + intoA, current + intoA, current + intoB, next + intoB, out var corner ) )
			{
				offset.Add( corner );
				continue;
			}

			// Parallel edges - a straight-through vertex, or a doubled point. There is no
			// intersection to take and none is needed: sliding the point along the shared normal is
			// already exactly right.
			offset.Add( current + intoA );
		}

		var newArea = SignedArea( offset );

		// Turned inside out, or collapsed to nothing. Both are "too far", and both produce a solid
		// that looks like geometry and measures like nonsense.
		if ( newArea > 0f != counterClockwise || MathF.Abs( newArea ) < MathF.Abs( area ) * 1e-4f )
		{
			error = $"offsetting by {distance:0.###} collapses this profile — it is narrower than that somewhere";
			return false;
		}

		// AND THE CHECK THE AREA CANNOT MAKE. Push a symmetric profile past its own centre and every
		// vertex crosses to the far side — which in two dimensions is a rotation by half a turn, and
		// rotations PRESERVE orientation. The signed area comes back the same sign and a healthy
		// size, describing a solid that is inside out and measures fine. A square drafted at 60
		// degrees over its own width sailed through the test above.
		//
		// An edge that has reversed direction is the local signature of that fold, and it cannot be
		// hidden by symmetry.
		for ( var i = 0; i < loop.Count; i++ )
		{
			var j = (i + 1) % loop.Count;
			var before = loop[j] - loop[i];
			var after = offset[j] - offset[i];

			if ( Vec2.Dot( before, after ) > 0f )
				continue;

			error = $"offsetting by {distance:0.###} folds this profile through itself";
			return false;
		}

		result = offset;
		return true;
	}

	/// <summary>
	/// The unit normal of an edge pointing into the material: always the left of travel.
	///
	/// NO WINDING BRANCH, and the first version had one, which made holes shrink instead of widen.
	/// The winding already carries the answer. An outer loop runs counter-clockwise and its material
	/// is inside it, so left of travel points inward. A hole runs clockwise and its material is
	/// OUTSIDE it, so left of travel points outward — away from the void, into the metal. Same rule,
	/// opposite result, exactly because the two are wound opposite ways. Branching on the winding
	/// applies the correction twice and sends holes the wrong way.
	/// </summary>
	static Vec2 Inward( Vec2 edge )
	{
		var direction = edge.Normal;

		return new Vec2( -direction.y, direction.x );
	}

	/// <summary>Where two infinite lines cross, each given by two points on it. False when they are
	/// parallel to within a tolerance that scales with the segments, so a long nearly-straight
	/// corner is treated as straight rather than throwing its intersection out to infinity.</summary>
	static bool TryIntersect( Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1, out Vec2 point )
	{
		point = Vec2.Zero;

		var da = a1 - a0;
		var db = b1 - b0;
		var denominator = Vec2.Cross( da, db );

		// Relative rather than absolute: the cross product carries the product of both lengths, so a
		// fixed epsilon calls large geometry parallel and small geometry crossing.
		if ( MathF.Abs( denominator ) < 1e-7f * MathF.Sqrt( da.LengthSquared * db.LengthSquared ) )
			return false;

		point = a0 + da * (Vec2.Cross( b0 - a0, db ) / denominator);
		return true;
	}

	static float SignedArea( IReadOnlyList<Vec2> loop )
	{
		var sum = 0f;

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % loop.Count];
			sum += a.x * b.y - b.x * a.y;
		}

		return sum * 0.5f;
	}
}
