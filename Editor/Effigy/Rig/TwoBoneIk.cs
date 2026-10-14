using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Two-bone IK in closed form, on a pose in model space: rotate a chain's root and middle bones
/// so its end lands on a target, bending towards an explicit pole.
///
/// A port of Marionette's RigConstraintSolver.TrySolveTwoBone onto <see cref="Xform"/>, so a take
/// can be solved and checked outside the editor and land where Marionette would put it. The same
/// choices, for the same reasons:
///
/// - LAW OF COSINES, not an iterative solver: two bones have an exact answer.
/// - ROTATIONS ARE DELTAS from where each bone already points, so the solver never assumes which
///   local axis a rig treats as "along the bone".
/// - THE END BONE KEEPS ITS ROTATION and only moves. A hand that should also turn is keyed or
///   solved separately; IK says where the wrist is, not which way the palm faces.
///
/// One addition: <c>softness</c>. A hard-clamped chain snaps straight as the target approaches
/// full reach - the elbow angle's derivative blows up right before the arm locks out, and a
/// slow reach pops. With softness s, the last s units of reach are approached exponentially, so
/// the arm straightens smoothly and never quite locks. Zero (the default) is Marionette's clamp
/// exactly.
/// </summary>
public static class TwoBoneIk
{
	public readonly record struct Solution( Vec3 Root, Vec3 Middle, Vec3 End, Xform UpperDelta, Xform LowerDelta, bool Reached );

	/// <summary>
	/// Solve on three joint positions. <paramref name="root"/>, <paramref name="middle"/> and
	/// <paramref name="end"/> are the current shoulder, elbow and wrist. The deltas rotate about
	/// the origin; apply them about the joint they belong to.
	/// </summary>
	public static bool TrySolve( Vec3 root, Vec3 middle, Vec3 end, Vec3 target, Vec3 pole, float softness, out Solution solution )
	{
		solution = default;

		var upperLength = (middle - root).Length;
		var lowerLength = (end - middle).Length;

		if ( upperLength <= 0.001f || lowerLength <= 0.001f )
			return false;

		var toTarget = target - root;
		var distance = toTarget.Length;

		if ( distance <= 0.001f )
			return false;

		var reach = upperLength + lowerLength;
		var asked = distance;

		if ( softness > 0f && distance > reach - softness )
			distance = reach - softness * MathF.Exp( -(distance - (reach - softness)) / softness );

		// Clamped just inside full extension - at exactly L1+L2 the bend plane is undefined and
		// the elbow snaps to an arbitrary side.
		distance = Math.Clamp( distance, MathF.Abs( upperLength - lowerLength ) + 0.001f, reach - 0.001f );

		var dir = toTarget.Normal;
		var clampedTarget = root + dir * distance;

		var cosRoot = (upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2f * upperLength * distance);
		var rootAngle = MathF.Acos( Math.Clamp( cosRoot, -1f, 1f ) );

		// The bend plane is spanned by the chain direction and the pole. If the pole is parallel to
		// the chain, fall back to the current elbow, and then to any perpendicular.
		var bendAxis = Vec3.Cross( dir, pole.Normal );

		if ( bendAxis.Length < 0.001f )
			bendAxis = Vec3.Cross( dir, (middle - root).Normal );

		if ( bendAxis.Length < 0.001f )
			bendAxis = Vec3.Cross( dir, MathF.Abs( dir.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) );

		bendAxis = bendAxis.Normal;

		var newMiddle = root + Xform.Rotate( bendAxis, rootAngle ).TransformDirection( dir ) * upperLength;

		solution = new Solution(
			root, newMiddle, clampedTarget,
			DeltaBetween( middle - root, newMiddle - root ),
			DeltaBetween( end - middle, clampedTarget - newMiddle ),
			MathF.Abs( asked - distance ) < 0.01f );

		return true;
	}

	/// <summary>
	/// Solve a chain inside a model-space pose: <paramref name="endBone"/>, its parent and its
	/// grandparent. Returns the pose with those three moved and every bone below them carried
	/// along, or null when the chain is not three bones deep or is degenerate.
	/// </summary>
	public static Xform[] Apply( Skeleton skeleton, IReadOnlyList<Xform> world, int endBone, Vec3 target, Vec3 pole, float softness = 0f )
	{
		if ( endBone < 0 || endBone >= skeleton.Count )
			return null;

		var mid = skeleton.Bones[endBone].Parent;
		var top = mid >= 0 ? skeleton.Bones[mid].Parent : -1;

		if ( mid < 0 || top < 0 )
			return null;

		var a = world[top].Origin;
		var b = world[mid].Origin;
		var c = world[endBone].Origin;

		if ( !TrySolve( a, b, c, target, pole, softness, out var s ) )
			return null;

		// Solve in local terms so children follow: replace the three bones' model transforms,
		// convert the whole pose back to local, and run it forward again.
		var solved = new Xform[world.Count];
		for ( var i = 0; i < world.Count; i++ )
			solved[i] = world[i];

		solved[top] = About( a, s.UpperDelta ) * world[top];
		solved[mid] = Xform.Translate( s.Middle - b ) * About( b, s.LowerDelta ) * world[mid];
		solved[endBone] = Xform.Translate( s.End - c ) * world[endBone];

		var local = RigAnimFile.LocalSpace( skeleton, world );
		var parentOf = skeleton.Bones;
		local[top] = parentOf[top].Parent >= 0 ? solved[parentOf[top].Parent].Inverse * solved[top] : solved[top];
		local[mid] = solved[top].Inverse * solved[mid];
		local[endBone] = solved[mid].Inverse * solved[endBone];

		return RigAnimFile.ModelSpace( skeleton, local );
	}

	/// <summary>A rotation applied about a point rather than the origin.</summary>
	internal static Xform About( Vec3 point, Xform rotation ) =>
		Xform.Translate( point ) * rotation * Xform.Translate( -point );

	/// <summary>Shortest rotation taking one direction onto another.</summary>
	internal static Xform DeltaBetween( Vec3 from, Vec3 to )
	{
		if ( from.Length < 0.001f || to.Length < 0.001f )
			return Xform.Identity;

		from = from.Normal;
		to = to.Normal;

		var dot = Math.Clamp( Vec3.Dot( from, to ), -1f, 1f );

		if ( dot > 0.99999f )
			return Xform.Identity;

		if ( dot < -0.99999f )
		{
			var perpendicular = Vec3.Cross( from, MathF.Abs( from.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) );
			return Xform.Rotate( perpendicular.Normal, MathF.PI );
		}

		return Xform.Rotate( Vec3.Cross( from, to ).Normal, MathF.Acos( dot ) );
	}
}
