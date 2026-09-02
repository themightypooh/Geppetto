using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

/// <summary>
/// Solves the constraints authored on a RigDocument.
///
/// CONSTRAINTS ACT WHILE POSING, AND BAKE INTO KEYFRAMES. Dragging a hand runs the IK solve and
/// writes ordinary FK keyframes for the whole chain, rather than storing "there is IK here" and
/// re-solving it at playback. That's a deliberate trade:
///
///   - Playback needs no solver at all, so a clip plays identically in the editor, in game, and
///     anywhere else that can read a keyframe. Nothing to keep in sync.
///   - The cost is that changing a constraint later doesn't retroactively change poses already
///     authored under the old one. Re-drag the effector to re-solve.
///
/// The alternative - live constraints evaluated every frame - needs the IK goal to be its own
/// animatable object, which is a bigger change to the asset format than this pass is making.
/// </summary>
public static class RigConstraintSolver
{
	/// <summary>Clamp a bone's parent-space rotation to any enabled Limit constraint targeting it.
	/// Returns the transform unchanged when nothing applies.</summary>
	public static Transform ClampToLimits( RigDocument rig, string boneName, Transform local )
	{
		if ( rig is null || string.IsNullOrEmpty( boneName ) )
			return local;

		foreach ( var limit in rig.LimitConstraints )
		{
			if ( !limit.Enabled || limit.TargetBone != boneName )
				continue;

			var angles = local.Rotation.Angles();

			var clamped = new Angles(
				Math.Clamp( angles.pitch, limit.MinAngles.pitch, limit.MaxAngles.pitch ),
				Math.Clamp( angles.yaw, limit.MinAngles.yaw, limit.MaxAngles.yaw ),
				Math.Clamp( angles.roll, limit.MinAngles.roll, limit.MaxAngles.roll ) );

			// Weight blends between the free and the clamped rotation, so a limit can soften a
			// joint rather than only hard-stopping it.
			var weight = limit.Weight.Clamp( 0f, 1f );
			var result = Rotation.Lerp( local.Rotation, clamped.ToRotation(), weight );

			local = new Transform( local.Position, result, local.Scale );
		}

		return local;
	}

	public static IkConstraint FindIkFor( RigDocument rig, string boneName ) =>
		rig?.IkConstraints.FirstOrDefault( c => c.Enabled && c.TargetBone == boneName );

	/// <summary>
	/// One solved bone: the world transform to write.
	/// </summary>
	public readonly record struct SolvedBone( BoneCollection.Bone Bone, Transform World );

	/// <summary>
	/// Two-bone IK, closed form. Given a chain end (hand), its parent (elbow) and grandparent
	/// (shoulder), rotate the two upper bones so the end lands on <paramref name="target"/>.
	///
	/// Closed form rather than iterative because two bones have an exact answer - the law of
	/// cosines gives the elbow angle directly. FABRIK and CCD exist for chains longer than this;
	/// they're iterative, and for the arm/leg case they'd be a slower way to get a worse result.
	///
	/// Returns false when the chain isn't three bones deep, or is degenerate (zero-length bones).
	/// </summary>
	public static bool TrySolveTwoBone( SkinnedModelRenderer renderer, BoneCollection.Bone end,
		Vector3 target, Vector3 poleDirection, out List<SolvedBone> solved )
	{
		solved = null;

		if ( !renderer.IsValid() || end?.Parent is not { } mid || mid.Parent is not { } root )
			return false;

		if ( !renderer.TryGetBoneTransform( root, out var rootTx )
			|| !renderer.TryGetBoneTransform( mid, out var midTx )
			|| !renderer.TryGetBoneTransform( end, out var endTx ) )
			return false;

		var a = rootTx.Position;
		var b = midTx.Position;
		var c = endTx.Position;

		var upperLength = (b - a).Length;
		var lowerLength = (c - b).Length;

		if ( upperLength <= 0.001f || lowerLength <= 0.001f )
			return false;

		var toTarget = target - a;
		var distance = toTarget.Length;

		if ( distance <= 0.001f )
			return false;

		// Clamped just inside full extension - at exactly L1+L2 the bend plane is undefined and
		// the elbow snaps to an arbitrary side.
		var reach = upperLength + lowerLength;
		distance = distance.Clamp( MathF.Abs( upperLength - lowerLength ) + 0.001f, reach - 0.001f );

		var dir = toTarget.Normal;
		var clampedTarget = a + dir * distance;

		// Law of cosines: the angle at the root between the chain direction and the upper bone.
		var cosRoot = (upperLength * upperLength + distance * distance - lowerLength * lowerLength)
			/ (2f * upperLength * distance);

		var rootAngle = MathF.Acos( cosRoot.Clamp( -1f, 1f ) ).RadianToDegree();

		// The bend plane is spanned by the chain direction and the pole. Cross gives the axis to
		// swing the upper bone around; if the pole is parallel to the chain that cross collapses,
		// so fall back to the current elbow offset, and then to any perpendicular.
		var bendAxis = Vector3.Cross( dir, poleDirection.Normal );

		if ( bendAxis.Length < 0.001f )
			bendAxis = Vector3.Cross( dir, (b - a).Normal );

		if ( bendAxis.Length < 0.001f )
			bendAxis = Vector3.Cross( dir, MathF.Abs( dir.z ) < 0.9f ? Vector3.Up : Vector3.Forward );

		bendAxis = bendAxis.Normal;

		var newMidPosition = a + Rotation.FromAxis( bendAxis, rootAngle ) * dir * upperLength;

		// Rotations are applied as deltas from where each bone currently points, so this never has
		// to know which local axis the skeleton treats as "along the bone" - a convention that
		// varies per rig and is not safe to assume.
		var upperDelta = DeltaBetween( b - a, newMidPosition - a );
		var lowerDelta = DeltaBetween( c - b, clampedTarget - newMidPosition );

		solved = new List<SolvedBone>
		{
			new( root, new Transform( a, upperDelta * rootTx.Rotation, rootTx.Scale ) ),
			new( mid, new Transform( newMidPosition, lowerDelta * midTx.Rotation, midTx.Scale ) ),
			new( end, new Transform( clampedTarget, endTx.Rotation, endTx.Scale ) )
		};

		return true;
	}

	/// <summary>Shortest rotation taking one direction onto another, built from an explicit
	/// axis and angle - Rotation.FromAxis is the one primitive here that's certain to exist and
	/// behave, and the degenerate cases (parallel, antiparallel) need handling anyway.</summary>
	private static Rotation DeltaBetween( Vector3 from, Vector3 to )
	{
		if ( from.Length < 0.001f || to.Length < 0.001f )
			return Rotation.Identity;

		from = from.Normal;
		to = to.Normal;

		var dot = Vector3.Dot( from, to ).Clamp( -1f, 1f );

		if ( dot > 0.99999f )
			return Rotation.Identity;

		// Exactly opposite: no unique shortest arc, so pick any perpendicular axis and flip.
		if ( dot < -0.99999f )
		{
			var perpendicular = Vector3.Cross( from, MathF.Abs( from.z ) < 0.9f ? Vector3.Up : Vector3.Forward );
			return Rotation.FromAxis( perpendicular.Normal, 180f );
		}

		var axis = Vector3.Cross( from, to ).Normal;
		var angle = MathF.Acos( dot ).RadianToDegree();

		return Rotation.FromAxis( axis, angle );
	}
}
