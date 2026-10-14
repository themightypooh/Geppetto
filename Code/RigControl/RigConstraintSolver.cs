using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

/// <summary>
/// Solves the constraints authored on a RigDocument.
///
/// AN IK CONSTRAINT WORKS ONE OF TWO WAYS, chosen per constraint by IkConstraint.Live:
///
///   - LIVE (the default): dragging the target bone keys the constraint's goal - a track of its
///     own on the clip (TrackTarget.IkGoal) - and the chain is solved toward that goal every frame,
///     by RigAnimPlayerComponent in game, by the viewport in the editor, and by the export when it
///     bakes. Change the constraint and every pose authored under it follows.
///   - BAKED: the drag runs the solve once and writes ordinary FK keyframes for the whole chain.
///     Playback needs no solver at all, and a clip plays identically anywhere that can read a
///     keyframe; the cost is that changing a constraint later does not touch poses already made.
///
/// Limits are applied to keyframes as they are read, either way.
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
	/// Solve a constraint from a renderer's current pose: walk ChainLength bones up from
	/// <paramref name="end"/>, solve toward <paramref name="target"/>, and hand back the chain
	/// root-first as world transforms to write. See SolveChain for what the length means.
	///
	/// <paramref name="goalRotation"/> is the end bone's new rotation, or null to keep the one it
	/// has. The pole is in the MODEL'S space and is turned with the renderer here, so a constraint
	/// bends the same way whichever way the character faces.
	/// </summary>
	public static bool TrySolve( SkinnedModelRenderer renderer, BoneCollection.Bone end, IkConstraint ik,
		Vector3 target, Rotation? goalRotation, out List<SolvedBone> solved )
	{
		solved = null;

		if ( !renderer.IsValid() || end is null || ik is null )
			return false;

		var length = Math.Max( ik.ChainLength, 1 );
		var bones = new BoneCollection.Bone[length + 1];
		bones[length] = end;

		for ( var k = length - 1; k >= 0; k-- )
		{
			bones[k] = bones[k + 1].Parent;

			if ( bones[k] is null )
				return false;
		}

		var world = new Transform[bones.Length];

		for ( var k = 0; k < bones.Length; k++ )
		{
			if ( !renderer.TryGetBoneTransform( bones[k], out world[k] ) )
				return false;
		}

		if ( !SolveChain( world, target, goalRotation, renderer.WorldRotation * ik.PoleDirection, ik.Iterations, ik.Weight ) )
			return false;

		solved = new List<SolvedBone>( bones.Length );

		for ( var k = 0; k < bones.Length; k++ )
			solved.Add( new SolvedBone( bones[k], world[k] ) );

		return true;
	}

	/// <summary>
	/// Two-bone IK from a renderer's pose, bending toward a world-space pole. Kept for callers that
	/// have no constraint to hand (the sample builders); a constraint goes through TrySolve.
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

		var chain = new[] { rootTx, midTx, endTx };

		if ( !SolveChain( chain, target, null, poleDirection, 1, 1f ) )
			return false;

		solved = new List<SolvedBone>
		{
			new( root, chain[0] ),
			new( mid, chain[1] ),
			new( end, chain[2] )
		};

		return true;
	}

	/// <summary>
	/// IK on a chain of world transforms, solved in place: <paramref name="chain"/>[0] is the top
	/// bone that turns, the last entry is the end that reaches. Every bone keeps its length.
	///
	/// THE LENGTH PICKS THE SOLVER - and until this existed, IkConstraint.ChainLength and Iterations
	/// were saved in every rig and read by nothing:
	///
	///   - one bone: it turns to aim the end at the target. Exact.
	///   - two bones: closed form. Two bones have an exact answer - the law of cosines gives the
	///     elbow angle directly - so iterating would be a slower way to a worse result.
	///   - three or more: cyclic coordinate descent, <paramref name="iterations"/> passes, each
	///     turning every bone from the end upward to point the end at the target. There is no
	///     closed form, and CCD, like the two-bone solve, works in deltas from where each bone
	///     already points, so it never has to know which local axis a rig treats as "along the
	///     bone" - a convention that varies per rig and is not safe to assume. The pole has no
	///     single middle joint to mean here and is not used.
	///
	/// The end bone keeps its world rotation unless <paramref name="goalRotation"/> is given.
	/// <paramref name="weight"/> blends every chain bone between where it was and the solve.
	///
	/// Effigy's kernel has the same solve on its own types (ChainIk), which is what a headless take
	/// check and the .vmdl export use - keep the two alike.
	/// </summary>
	public static bool SolveChain( Transform[] chain, Vector3 target, Rotation? goalRotation, Vector3 pole, int iterations, float weight )
	{
		if ( chain is null || chain.Length < 2 )
			return false;

		var original = (Transform[])chain.Clone();
		var n = chain.Length - 1;

		if ( n == 2 )
		{
			if ( !SolveTwoBone( chain, target, pole ) )
				return false;
		}
		else
		{
			// One bone is one step, and exact; more are iterated.
			var passes = n == 1 ? 1 : Math.Max( iterations, 1 );

			for ( var pass = 0; pass < passes; pass++ )
			{
				for ( var j = n - 1; j >= 0; j-- )
				{
					var pivot = chain[j].Position;
					var delta = DeltaBetween( chain[n].Position - pivot, target - pivot );

					for ( var k = j; k <= n; k++ )
						chain[k] = new Transform( pivot + delta * (chain[k].Position - pivot), delta * chain[k].Rotation, chain[k].Scale );
				}

				if ( chain[n].Position.Distance( target ) < 0.01f )
					break;
			}

			// The end only moves; the rotation it was carried through is put back.
			chain[n] = new Transform( chain[n].Position, original[n].Rotation, original[n].Scale );
		}

		if ( goalRotation is { } turn )
			chain[n] = new Transform( chain[n].Position, turn, chain[n].Scale );

		weight = weight.Clamp( 0f, 1f );

		if ( weight < 1f )
		{
			for ( var k = 0; k <= n; k++ )
				chain[k] = Transform.Lerp( original[k], chain[k], weight, true );
		}

		return true;
	}

	/// <summary>The closed-form two-bone solve on a three-entry chain, in place. False when the
	/// chain is degenerate (zero-length bones, target on the root).</summary>
	private static bool SolveTwoBone( Transform[] chain, Vector3 target, Vector3 poleDirection )
	{
		var rootTx = chain[0];
		var midTx = chain[1];
		var endTx = chain[2];

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

		chain[0] = new Transform( a, upperDelta * rootTx.Rotation, rootTx.Scale );
		chain[1] = new Transform( newMidPosition, lowerDelta * midTx.Rotation, midTx.Scale );
		chain[2] = new Transform( clampedTarget, endTx.Rotation, endTx.Scale );

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
