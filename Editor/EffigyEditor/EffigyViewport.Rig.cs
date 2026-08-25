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

	private static readonly Color BoneToolPreviewColor = new( 1f, 0.85f, 0.2f, 0.55f );

	/// <summary>Length of the ghost bone shown before the first click of a chain has a real tail to
	/// aim at — a guess, since nothing here knows the model's scale. Matches DogBone's own knob
	/// math (knobR = boneLen * 0.16) against BoneHandleRadius (0.8) closely enough that the ghost
	/// reads as the same size class as a committed bone rather than conspicuously different.</summary>
	private const float PendingBonePreviewLength = 5f;

	/// <summary>
	/// Highlight the point under the cursor with the actual dog-bone shape the click would commit
	/// — not a placeholder dot, the real DrawDogBone — so what you see before clicking is what you
	/// get after, for BOTH clicks of the gesture:
	///
	/// - Second click of a segment: head is the pending point from the first click, tail is the
	///   cursor. Exact preview — the real head, the real tail, the real orientation.
	/// - First click of a chain: there is no head yet, so the tail is a guess — a fixed length
	///   standing off the surface along its normal. Direction and exact length are not meaningful
	///   yet (only the second click fixes those); what this answers is "a bone will appear roughly
	///   here, roughly this size," which a bare dot didn't.
	///
	/// Drawn depth-ignoring like the committed skeleton (DrawRigSkeleton) so the preview reads
	/// through the mesh the same way placed bones already do, rather than disappearing the moment
	/// it points away from the camera.
	///
	/// Same raycast MeshRaycast/_displayBodies pairing as the face and body picks — bones are
	/// placed ON the model, not in empty space.
	/// </summary>
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
		Gizmo.Draw.IgnoreDepth = true;

		if ( PendingBoneHead is { } head && (point - head).Length > 0.01f )
		{
			var headWorld = new Vector3( head.x, head.y, head.z );
			var (xAxis, zAxis) = PreviewBasis( head, point );
			DrawDogBone( headWorld, world, xAxis, zAxis );
		}
		else if ( PendingBoneHead is null )
		{
			var tailGuess = point + hit.Hit.Normal * PendingBonePreviewLength;

			if ( (tailGuess - point).Length > 0.01f )
			{
				var tailWorld = new Vector3( tailGuess.x, tailGuess.y, tailGuess.z );
				var (xAxis, zAxis) = PreviewBasis( point, tailGuess );
				DrawDogBone( world, tailWorld, xAxis, zAxis );
			}
		}
		else
		{
			Gizmo.Draw.SolidSphere( world, BoneHandleRadius * 0.4f, 8, 8 );
		}

		Gizmo.Draw.IgnoreDepth = false;

		if ( Gizmo.WasLeftMousePressed )
			BonePointPicked?.Invoke( point );
	}

	/// <summary>
	/// The same head→tail aim-and-perpendicular construction Skeleton.LocalFromWorldPoints uses,
	/// kept here only far enough to get two axes for the PREVIEW's cross-section — the kernel
	/// owns the real thing once the click commits. Not shared code because it can't be: the kernel
	/// has no notion of a Vector3/Gizmo and shouldn't grow one for a rendering concern. Copied
	/// exactly, including the same seed-axis threshold, so the preview's roll matches the bone
	/// AddBoneFromPoints actually creates rather than merely resembling it.
	/// </summary>
	private static (Vector3 xAxis, Vector3 zAxis) PreviewBasis( Vec3 head, Vec3 tail )
	{
		var along = tail - head;
		var y = along / along.Length;

		var seed = MathF.Abs( y.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
		var x = Vec3.Cross( seed, y ).Normal;
		var z = Vec3.Cross( x, y );

		return (new Vector3( x.x, x.y, x.z ), new Vector3( z.x, z.y, z.z ));
	}
}
