using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// Placing bones by clicking the model — separate from EffigyViewport.Sketching.cs's picking
/// modes because this one writes new geometry into a skeleton rather than selecting existing
/// geometry, and separate from the pose gizmo in EffigyViewport.cs because that drags a bone
/// that already exists.
///
/// The Skeleton itself is owned by EffigyRigPanel, not the viewport — same division as
/// BodyPickMode, where the viewport only ever reports what was clicked and the owner decides
/// what that means. Chaining (each click extending the previous point into a new bone, parented
/// to it) is Blender's armature-extrude gesture, and the reason the panel — not the viewport —
/// tracks the pending head and parent: it is the one deciding when a chain resets.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>While true, left-clicking the model reports the point via BonePointPicked instead
	/// of selecting or posing a bone.</summary>
	public bool BoneToolActive { get; set; }

	/// <summary>World point of the chain's last placed joint, for the preview line. Set by the
	/// panel — null means the next click starts a fresh chain rather than extending one.</summary>
	public Vec3? PendingBoneHead { get; set; }

	/// <summary>Fires with the world-space point clicked while the tool is active.</summary>
	public Action<Vec3> BonePointPicked { get; set; }

	/// <summary>Escape while the tool is active. The panel decides whether that closes the current
	/// chain (if one is open) or turns the tool off entirely (if not) — the viewport has no notion
	/// of which, since PendingBoneHead is the panel's state mirrored here for drawing only.</summary>
	public Action BoneToolEscape { get; set; }

	private static readonly Color BoneToolPreviewColor = new( 1f, 0.85f, 0.2f, 0.9f );

	/// <summary>Highlight the point under the cursor, draw a line back to the pending head if a
	/// chain is open, and report a click. Same raycast MeshRaycast/_displayBodies pairing as the
	/// face and body picks — bones are placed ON the model, not in empty space.</summary>
	private void BoneToolFrame()
	{
		if ( !BoneToolActive || !_canvasHasCursor )
			return;

		var ray = Gizmo.CurrentRay;
		var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
		var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

		if ( MeshRaycast.Raycast( _displayBodies, origin, direction ) is not { } hit )
			return;

		var point = hit.Hit.Point;
		var world = new Vector3( point.x, point.y, point.z );

		Gizmo.Draw.Color = BoneToolPreviewColor;
		Gizmo.Draw.SolidSphere( world, BoneHandleRadius * 0.4f, 8, 8 );

		if ( PendingBoneHead is { } head )
			Gizmo.Draw.Line( new Vector3( head.x, head.y, head.z ), world );

		if ( Gizmo.WasLeftMousePressed )
			BonePointPicked?.Invoke( point );
	}
}
