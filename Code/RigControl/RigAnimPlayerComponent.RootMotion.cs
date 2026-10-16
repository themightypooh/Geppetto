using Sandbox;

namespace Marionette;

/// <summary>
/// Root motion in game: the clip's travel (RigAnimDocument.RootMotion, set by Extract Root Motion)
/// taken off the root bone each frame - the body plays in place - and either moved onto the
/// GameObject or handed to game code. A loop adds one whole cycle's travel at the wrap, so a walk
/// keeps walking instead of snapping back. Same maths as Effigy's RootMotion (the export and the
/// headless sim): RigRootMotionMath.MotionOf.
/// </summary>
public sealed partial class RigAnimPlayerComponent
{
	/// <summary>
	/// What to do with the clip's root motion. Off plays the travel as keyed. Move GameObject moves
	/// this character by it while the body plays in place. Report Only plays in place and puts each
	/// frame's travel in RootMotionDelta for a character controller to move by. Needs a clip with
	/// root motion (timeline: right-click the hips row ▸ Extract Root Motion).
	/// </summary>
	[Property, Group( "Root Motion" ), Title( "Root Motion" )] public RootMotionPlayback RootMotion { get; set; } = RootMotionPlayback.Off;

	/// <summary>This frame's travel, in the model's own space (position and turn) - what Move
	/// GameObject applied, or what Report Only leaves for game code. Identity when nothing moved.</summary>
	public Transform RootMotionDelta { get; private set; } = global::Transform.Zero;

	/// <summary>The clip's travel at the last frame applied, measured from frame 0. Null right
	/// after Play or Seek, so a jump never moves the character.</summary>
	private Transform? _rootPrev;

	/// <summary>Set when the clock wrapped this tick, so the cycle's remaining travel is added.</summary>
	private bool _rootWrapped;

	private bool _rootWarned;

	private void ResetRootMotion()
	{
		_rootPrev = null;
		_rootWrapped = false;
		RootMotionDelta = global::Transform.Zero;
	}

	private void ApplyRootMotion()
	{
		var wrapped = _rootWrapped;
		_rootWrapped = false;
		RootMotionDelta = global::Transform.Zero;

		if ( RootMotion == RootMotionPlayback.Off || Anim?.RootMotion is not { Enabled: true } settings || !Target.IsValid() )
			return;

		var root = FindBone( Target.GameObject, settings.Bone );

		if ( !root.IsValid() || Anim.FindTrack( settings.Bone ) is not { Keyframes.Count: > 0 } track )
		{
			if ( !_rootWarned )
				Log.Warning( $"[Marionette] {GameObject.Name}: Root Motion is on, but '{settings.Bone}' is not a keyed bone of this model - extract root motion again for this model, or turn Root Motion off." );

			_rootWarned = true;
			return;
		}

		// A layered player only moves the character when it actually wrote the root this frame.
		if ( Mode != RigAnimMode.Exclusive && !Wrote( root ) )
			return;

		var parentModel = Target.Model?.Bones.GetBone( settings.Bone )?.Parent is { } parent ? parent.LocalTransform : global::Transform.Zero;
		Transform ModelAt( float frame ) => parentModel.ToWorld( track.Evaluate( frame ) );

		var start = ModelAt( 0f );
		var now = Target.WorldTransform.ToLocal( root.WorldTransform );
		var motion = RigRootMotionMath.MotionOf( start, now, settings.Mode );

		var delta = _rootPrev is not { } prev
			? global::Transform.Zero
			: wrapped
				? prev.ToLocal( RigRootMotionMath.MotionOf( start, ModelAt( LastFrame ), settings.Mode ) ).ToWorld( motion )
				: prev.ToLocal( motion );

		_rootPrev = motion;
		RootMotionDelta = delta;

		if ( RootMotion == RootMotionPlayback.MoveObject )
			Target.GameObject.WorldTransform = Target.GameObject.WorldTransform.ToWorld( delta );

		// The body in place: the root's travel taken back out, everything under it carried along.
		root.WorldTransform = Target.WorldTransform.ToWorld( motion.ToLocal( now ) );
	}
}
