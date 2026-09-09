using Effigy;
using System;

namespace Marionette.EditorTools;

// ============================================================================
//  Posing: drag a bone and watch the mesh follow.
//
//  WHY THIS WAS MISSING, AND WHY IT IS THE RIG'S WHOLE FEEDBACK LOOP. The rig
//  panel could place bones and pin bodies to them, and the mesh would only show
//  the result after export — SkinBinder ran in exactly one place, CompileVmdl.
//  So a weight was a number in a panel rather than a crease in a surface, and
//  the only way to find out whether a rig worked was to open it somewhere else.
//
//  The pose is already in hand — the pose gizmo writes bone.Local — it simply
//  was not used for anything but an inspector readout. This uses it: the bind
//  pose is snapshotted when the preview arms, the gizmo's Local is the current
//  pose, and SkinBinder.Deform carries one into the other onto a DISPLAY copy of
//  the mesh. The cage is never touched; the pose is never saved.
//
//  A POSE PREVIEW IS NOT A TIMELINE. One transform per bone, no keys, no
//  playback. Reset snaps back to the bind pose; turning it off leaves the bind
//  pose exactly as it was authored, because the pose never wrote to Local.
// ============================================================================

internal sealed partial class EffigyViewport
{
	/// <summary>Whether the pose preview is armed — a bind snapshot and weights exist.</summary>
	public bool PosePreviewActive { get; private set; }

	// The bind pose (a full clone, so Reset can restore Local AND Length), the weights the mesh is
	// skinned with, and the bind mesh the deformation is applied to. The skeleton is cloned rather
	// than referenced: the pose gizmo mutates RigSkeleton.Bones[i].Local in place, so a reference
	// would already BE the pose by the time anyone tried to reset to it.
	private Skeleton _poseBindSkeleton;
	private SkinWeights _poseWeights;
	private PolyMesh _poseBaseMesh;

	/// <summary>Fired whenever the posed skeleton changed, so the window re-deforms the display
	/// mesh. The pose gizmo fires BonePosed on every drag frame; this is the pose-preview half of
	/// the same signal.</summary>
	public Action PoseChanged { get; set; }

	/// <summary>
	/// Arm the preview against a mesh and its weights. The skeleton is the rig's current state,
	/// which becomes the bind pose the next drag is measured against. Returns false when there is
	/// nothing to pose — no mesh, no weights, or no bones.
	/// </summary>
	public bool ArmPosePreview( PolyMesh baseMesh, SkinWeights weights, Skeleton skeleton )
	{
		if ( baseMesh is null || baseMesh.VertexCount == 0 || weights is null || weights.Count == 0
			|| skeleton is null || skeleton.Count == 0 )
			return false;

		_poseBaseMesh = baseMesh;
		_poseWeights = weights;
		_poseBindSkeleton = skeleton.Clone();
		PosePreviewActive = true;
		return true;
	}

	/// <summary>Stop posing, without touching the skeleton. The bind pose was never modified.</summary>
	public void DisarmPosePreview()
	{
		PosePreviewActive = false;
		_poseBaseMesh = null;
		_poseWeights = null;
		_poseBindSkeleton = null;
	}

	/// <summary>
	/// The bind mesh deformed by the current pose, or null when the preview is off or the skeleton
	/// changed shape under it (a bone placed or deleted) — the caller re-arms rather than guess.
	/// </summary>
	public PolyMesh PoseMesh()
	{
		if ( !PosePreviewActive || _poseBindSkeleton is null || _poseWeights is null || _poseBaseMesh is null )
			return null;

		// A bone added or removed while posing invalidates the weights (bone indices) and the bind
		// snapshot. Refuse rather than index past an array end — the caller arms a fresh preview.
		if ( RigSkeleton is null || RigSkeleton.Count != _poseBindSkeleton.Count )
			return null;

		var bind = new Xform[_poseBindSkeleton.Count];
		for ( var i = 0; i < bind.Length; i++ )
			bind[i] = _poseBindSkeleton.WorldBind( i );

		var pose = new Xform[RigSkeleton.Count];
		for ( var i = 0; i < pose.Length; i++ )
			pose[i] = RigSkeleton.WorldBind( i );

		var deformed = SkinBinder.Deform( _poseBaseMesh.Positions, _poseWeights, bind, pose );

		var result = _poseBaseMesh.Clone();
		for ( var i = 0; i < result.Positions.Count; i++ )
			result.Positions[i] = deformed[i];

		return result;
	}

	/// <summary>
	/// Snap the skeleton back to the bind pose it was armed with. Matches by NAME rather than index
	/// so a bone deleted and re-added under the same name still resets. Local and Length both
	/// restored — the gizmo writes both.
	/// </summary>
	public void ResetPose()
	{
		if ( !PosePreviewActive || _poseBindSkeleton is null || RigSkeleton is null )
			return;

		for ( var i = 0; i < RigSkeleton.Count; i++ )
		{
			var bind = _poseBindSkeleton.IndexOf( RigSkeleton.Bones[i].Name );
			if ( bind < 0 )
				continue;

			RigSkeleton.Bones[i].Local = _poseBindSkeleton.Bones[bind].Local;
			RigSkeleton.Bones[i].Length = _poseBindSkeleton.Bones[bind].Length;
		}
	}
}
