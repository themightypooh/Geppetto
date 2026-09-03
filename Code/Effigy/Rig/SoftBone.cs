using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// What makes one bone soft: how hard it snaps back to the pose it was given, how quickly it stops
/// ringing, and how far it is allowed to stray.
///
/// A bone with no SoftBone on it is rigid, which is why this is a separate object hung off
/// <see cref="Bone.Soft"/> rather than four more fields on every bone. Most bones in most rigs are
/// rigid and should cost nothing to say so.
///
/// THE NUMBERS ARE AUTHORING DATA, NOT SOLVER STATE. Nothing here changes while a solve runs - the
/// moving parts live in <see cref="SoftPose"/>. That split is what lets one authored rig drive many
/// instances, and it is why these can be edited live while something is being simulated.
/// </summary>
public sealed class SoftBone
{
	/// <summary>
	/// The spring pulling the bone back to the pose the animation asked for, as an acceleration per
	/// unit of offset. Its natural frequency is sqrt(Stiffness) radians per second, so 40 is about
	/// one swing a second and 400 is a stiff twitch.
	///
	/// A FORCE, NOT A LERP TOWARD THE TARGET. Lerping the position is the obvious way to write this
	/// and it is wrong: Verlet reads the gap between successive positions back as momentum, so the
	/// lerp gets counted twice and how much depends on how many steps the second was cut into. The
	/// frame-rate check in SoftBoneTests failed by most of a bone against exactly that version.
	///
	/// Zero is a dead limb that only gravity and momentum move.
	/// </summary>
	public float Stiffness = 60f;

	/// <summary>
	/// How much of its speed the bone keeps after ONE SECOND, 0 to 1.
	///
	/// Per second, not per step, and that is the whole point: written per step this is a different
	/// material at 30fps than at 240, the rig goes floppy on slow machines, and the bug gets blamed
	/// on the art. See the frame-rate check in SoftBoneTests, which failed against the per-step
	/// version by most of a bone.
	///
	/// At 1 nothing is ever lost and a disturbed bone rings forever; at 0 it has no momentum at all
	/// and stiffness alone drags it about. Limbs live low - a few percent - because a second is a
	/// long time for a swinging arm.
	/// </summary>
	public float Damping = 0.04f;

	/// <summary>
	/// How much gravity pulls on the bone's tail, as a multiple of the solve's gravity.
	///
	/// Per bone rather than global because the whole point of authoring this on a rig is that a
	/// forearm and a coat tail hang differently.
	/// </summary>
	public float Weight = 1f;

	/// <summary>
	/// The furthest the bone may stray from its animated direction, in degrees.
	///
	/// This is the difference between soft and broken. Without a limit, a fast enough swing puts a
	/// forearm through the shoulder it hangs off, and no amount of stiffness prevents it - stiffness
	/// is a rate, and a rate can always be outrun. A cone cannot be outrun; it is a hard clamp
	/// applied after everything else. 180 means unlimited.
	/// </summary>
	public float MaxAngle = 45f;

	public SoftBone Clone() => new()
	{
		Stiffness = Stiffness,
		Damping = Damping,
		Weight = Weight,
		MaxAngle = MaxAngle,
	};
}

/// <summary>
/// The moving half: where each soft bone's tail actually is, and where it was last step.
///
/// Verlet, so the previous position IS the velocity. There is no separate velocity array to fall
/// out of step with the positions, and a bone that is teleported by having both written to the same
/// value is simply at rest - which is exactly what <see cref="Rest"/> wants.
///
/// One of these per INSTANCE. The skeleton and its SoftBones are shared authoring data; this is
/// what makes two characters wearing the same rig not share a wobble.
/// </summary>
public sealed class SoftPose
{
	public Vec3[] Tail;
	public Vec3[] Previous;

	/// <summary>False until the first solve, which places the tails rather than swinging them in
	/// from wherever the array happened to start.</summary>
	public bool Started;

	/// <summary>
	/// How long the previous step was.
	///
	/// Verlet reads velocity out of the gap between two positions, and that gap is only a velocity
	/// if you know how long it took. Without this, a frame-rate change silently rescales every
	/// bone's speed - see the time correction in <see cref="SoftSolver.Solve"/>.
	/// </summary>
	public float LastStep;

	public SoftPose( int bones )
	{
		Tail = new Vec3[bones];
		Previous = new Vec3[bones];
	}

	/// <summary>Forget the motion. The next solve places the bones at the pose instead of easing
	/// toward it - what you want after a teleport, and what you want on the first frame.</summary>
	public void Rest()
	{
		Started = false;
		LastStep = 0f;
	}
}

/// <summary>
/// Runs the soft bones: an animated pose goes in, a pose with lag and swing in it comes out.
///
/// WHAT THIS IS FOR. A tracked hand, a windblown coat, an antenna - anything where the animation
/// system knows where one end is and the rest should follow physically rather than be keyframed.
/// The case that drove it is a VR arm: the controller reports a wrist and everything above it is
/// invention, so the invented part should at least move like a limb.
///
/// WHY IT IS IN THE KERNEL. It is arithmetic on <see cref="Vec3"/> and <see cref="Xform"/> with no
/// engine anywhere near it, so it can be tested the way the rest of this kernel is tested - by
/// measuring what it produced, not by looking at it. A wobble that is subtly wrong still looks like
/// a wobble; what catches it is checking that bone lengths did not change, that a still rig stays
/// still, and that nothing leaves its cone. None of those are visible.
///
/// THE HEADS ARE NEVER SOFT, ONLY THE DIRECTIONS. A bone's head is wherever its parent's tail put
/// it, exactly, every step. If heads were free the chain would come apart, and a limb that
/// stretches is a far worse artefact than one that lags. So each bone solves a direction, and the
/// length is restored afterwards rather than being a spring that might not converge.
/// </summary>
public static class SoftSolver
{
	/// <summary>How hard a solve pulls down, in units per second squared, before per-bone Weight.</summary>
	public static readonly Vec3 DefaultGravity = new( 0f, 0f, -386f );

	/// <summary>
	/// One step.
	///
	/// <paramref name="animated"/> is the pose the rig would have with no softness - one world
	/// transform per bone, in the skeleton's own order. The result is written back into the same
	/// array, so a caller that does not care about the difference does not have to allocate.
	///
	/// TOPOLOGICAL ORDER IS LOad-BEARING. A bone is solved after its parent, so by the time it is
	/// reached its parent's softened transform is already in the array and its head follows from
	/// it. <see cref="Skeleton.AddBone"/> guarantees that order, which is the whole reason it
	/// refuses a parent that does not exist yet.
	/// </summary>
	public static void Solve( Skeleton skeleton, Xform[] animated, SoftPose pose, float dt, Vec3? gravity = null )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( animated is null ) throw new ArgumentNullException( nameof( animated ) );
		if ( pose is null ) throw new ArgumentNullException( nameof( pose ) );

		if ( animated.Length < skeleton.Count )
			throw new ArgumentException(
				$"Need a transform per bone: {skeleton.Count} bones, {animated.Length} transforms", nameof( animated ) );

		if ( pose.Tail.Length < skeleton.Count )
			throw new ArgumentException(
				$"Pose is for {pose.Tail.Length} bones, skeleton has {skeleton.Count}", nameof( pose ) );

		// A zero or backwards step is not an error - a paused editor hands one over every frame -
		// but there is nothing to integrate, and dividing the frame up would produce NaN.
		if ( dt <= 0f )
			return;

		var g = gravity ?? DefaultGravity;

		for ( int i = 0; i < skeleton.Count; i++ )
		{
			var bone = skeleton.Bones[i];

			// The head comes from the parent's SOLVED transform, so softness accumulates down the
			// chain the way it does in a real limb. Rigid bones still pass their parent's softness
			// on, which is why this happens before the rigid test.
			var rest = bone.Parent >= 0
				? animated[bone.Parent] * bone.Local
				: animated[i];

			if ( bone.Soft is null || bone.Length <= 1e-6f )
			{
				animated[i] = rest;
				continue;
			}

			animated[i] = SolveBone( bone, rest, pose, i, dt, g );
		}

		pose.Started = true;
		pose.LastStep = dt;
	}

	static Xform SolveBone( Bone bone, Xform rest, SoftPose pose, int i, float dt, Vec3 gravity )
	{
		var soft = bone.Soft;

		var head = rest.Origin;

		// +Y is the bone axis - the convention Skeleton is built on, see Bone.Length.
		var restDirection = rest.Y.Normal;
		var target = head + restDirection * bone.Length;

		if ( !pose.Started )
		{
			pose.Tail[i] = target;
			pose.Previous[i] = target;
			return rest;
		}

		// Time-corrected Verlet. The gap between the last two positions is a DISTANCE; turning it
		// back into a step of the current length is what makes the result the same at any frame
		// rate, and damping is raised to the power of dt for the same reason - both are per-second
		// quantities being asked what they did in this particular fraction of one.
		float ratio = pose.LastStep > 1e-9f ? dt / pose.LastStep : 1f;

		var carried = (pose.Tail[i] - pose.Previous[i]) * ratio * MathF.Pow( Clamp01( soft.Damping ), dt );

		// Everything acting on the bone arrives as an acceleration, integrated once. That is the
		// part that makes the whole thing frame-rate honest: accelerations compose over a second
		// the same way however many pieces the second is cut into, and a position lerp does not.
		var spring = (target - pose.Tail[i]) * MathF.Max( soft.Stiffness, 0f );
		var accel = spring + gravity * soft.Weight;

		var tail = pose.Tail[i] + carried + accel * (dt * dt);

		// Length is restored rather than sprung, so the bone cannot stretch no matter what the
		// numbers above did.
		// Normal returns Zero rather than NaN for a degenerate vector, so this is the "tail landed
		// exactly on the head" case rather than an impossible one.
		var direction = (tail - head).Normal;
		if ( direction.LengthSquared < 1e-12f )
			direction = restDirection;

		direction = Cone( direction, restDirection, soft.MaxAngle );

		pose.Previous[i] = pose.Tail[i];
		pose.Tail[i] = head + direction * bone.Length;

		return Swing( rest, restDirection, direction );
	}

	/// <summary>
	/// Clamp <paramref name="direction"/> to within <paramref name="degrees"/> of
	/// <paramref name="axis"/>.
	///
	/// Applied last and as a hard clamp, because a limit that is itself a spring can be exceeded by
	/// anything moving fast enough - and the whole reason for having one is the fast case.
	/// </summary>
	public static Vec3 Cone( Vec3 direction, Vec3 axis, float degrees )
	{
		if ( degrees >= 180f ) return direction;

		float limit = MathF.Max( degrees, 0f ) * MathF.PI / 180f;
		float cos = Math.Clamp( Vec3.Dot( direction, axis ), -1f, 1f );
		float angle = MathF.Acos( cos );

		if ( angle <= limit ) return direction;

		// Rotate the axis toward the direction by exactly the limit. The perpendicular component of
		// the direction gives the plane the two share; when they are opposed there is no such plane
		// and any perpendicular will do, which is the degenerate case below.
		var perpendicular = (direction - axis * cos).Normal;

		if ( perpendicular.LengthSquared < 1e-12f )
			perpendicular = AnyPerpendicular( axis );

		return (axis * MathF.Cos( limit ) + perpendicular * MathF.Sin( limit )).Normal;
	}

	/// <summary>
	/// The rest transform, turned so its +Y points along <paramref name="to"/>.
	///
	/// Only the swing is applied - the bone is not twisted about its own axis, because nothing in
	/// this solver has an opinion about roll and inventing one would make a hanging limb slowly
	/// spiral.
	/// </summary>
	static Xform Swing( Xform rest, Vec3 from, Vec3 to )
	{
		var axis = Vec3.Cross( from, to );
		float sin = axis.Length;
		float cos = Math.Clamp( Vec3.Dot( from, to ), -1f, 1f );

		// Already there, or exactly opposed. Opposed needs an arbitrary axis; parallel needs none.
		if ( sin < 1e-7f )
		{
			if ( cos > 0f ) return rest;

			axis = AnyPerpendicular( from );
			return Xform.RotateAbout( rest.Origin, axis, MathF.PI ) * rest;
		}

		return Xform.RotateAbout( rest.Origin, axis / sin, MathF.Atan2( sin, cos ) ) * rest;
	}

	static Vec3 AnyPerpendicular( Vec3 v )
	{
		// Cross with whichever axis this vector is least aligned to, so the result is never near
		// zero length.
		var other = MathF.Abs( v.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		return Vec3.Cross( v, other ).Normal;
	}

	static float Clamp01( float v ) => Math.Clamp( v, 0f, 1f );

	/// <summary>
	/// The pose a skeleton has with nothing animating it - every bone at its bind transform.
	///
	/// Handy on its own, and it is what the tests drive: a solver that cannot leave a rig alone
	/// when the rig is not moving has nothing else worth checking.
	/// </summary>
	public static Xform[] BindPose( Skeleton skeleton )
	{
		var world = new Xform[skeleton.Count];

		for ( int i = 0; i < skeleton.Count; i++ )
		{
			var bone = skeleton.Bones[i];
			world[i] = bone.Parent >= 0 ? world[bone.Parent] * bone.Local : bone.Local;
		}

		return world;
	}

	/// <summary>Every bone that has softness on it, for a panel or a diagnostic to list.</summary>
	public static IEnumerable<int> SoftBones( Skeleton skeleton )
	{
		for ( int i = 0; i < skeleton.Count; i++ )
		{
			if ( skeleton.Bones[i].Soft is not null )
				yield return i;
		}
	}
}
