using Marionette;
using Sandbox;
using System.Collections.Generic;

namespace Marionette.Tools;

/// <summary>
/// The rig's morph drivers (correctives) on a scrub: each driven morph's weight read off the pose
/// EvaluatePose just resolved, so the viewport shows the fix fading in exactly as the player and the
/// export do.
///
/// Read from the pose this pass computed, never off the renderer: bone writes land a tick late (see
/// ApplyWorldTransform), and a driver read off last frame's knee would lag the knee it fixes.
/// </summary>
internal sealed partial class RigViewport
{
	/// <summary>World transforms by bone key from the last EvaluatePose.</summary>
	private Dictionary<string, Transform> _poseWorlds;

	/// <summary>
	/// Add every driven morph's weight to <paramref name="weights"/> - unless the clip already sets
	/// that morph this frame, which wins, as in game.
	/// </summary>
	public void AddDrivenMorphs( Dictionary<string, float> weights )
	{
		if ( Rig?.MorphDrivers is not { Count: > 0 } || !_renderer.IsValid() || _renderer.Model is null )
			return;

		var driven = new Dictionary<string, float>();
		RigMorphDriver.WeightsAt( Rig, DriverPose, driven );

		foreach ( var (morph, weight) in driven )
			weights.TryAdd( morph, weight );
	}

	/// <summary>A main-model bone's bind and current parent-space transforms and its model-space
	/// position this frame; null for a bone the model does not have.</summary>
	private (Transform Bind, Transform Local, Vector3 Model)? DriverPose( string name )
	{
		if ( string.IsNullOrEmpty( name ) || _renderer.Model?.Bones.GetBone( name ) is not { } bone )
			return null;

		if ( !TryPoseWorld( bone, out var world ) )
			return null;

		var parentWorld = bone.Parent is { } parent && TryPoseWorld( parent, out var pw ) ? pw : _renderer.WorldTransform;

		// Bone.LocalTransform is MODEL space despite its name; the parent's undoes it.
		var bind = bone.Parent is { } p ? p.LocalTransform.ToLocal( bone.LocalTransform ) : bone.LocalTransform;

		return (bind, parentWorld.ToLocal( world ), _renderer.WorldTransform.PointToLocal( world.Position ));
	}

	private bool TryPoseWorld( BoneCollection.Bone bone, out Transform world )
	{
		// The main model's bones are keyed by their bare name (RigTrackName.RootSubject).
		if ( _poseWorlds is not null && _poseWorlds.TryGetValue( bone.Name, out world ) )
			return true;

		return _renderer.TryGetBoneTransform( bone, out world );
	}
}
