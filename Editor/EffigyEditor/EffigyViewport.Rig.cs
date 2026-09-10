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
	/// <summary>Where along the cursor ray a click puts the joint.</summary>
	internal enum BonePlacement
	{
		/// <summary>On the skin, where the ray first meets the model.</summary>
		Surface,

		/// <summary>Halfway through the material the ray crosses — see MeshRaycast.SolidSpan.</summary>
		Middle,
	}

	/// <summary>While true, left-clicking the model reports the point via BonePointPicked instead
	/// of selecting or posing a bone.</summary>
	public bool BoneToolActive { get; set; }

	/// <summary>
	/// How deep a click places the joint. Owned by the rig panel, which puts the control on screen;
	/// mirrored here because the PREVIEW has to agree with what the click will do, and the preview
	/// is drawn from this file.
	///
	/// MIDDLE IS THE DEFAULT. A bone belongs on the medial line of what it drives, and surface is
	/// the special case — an attachment point, a prop mount — rather than the ordinary one. Surface
	/// was the only option for as long as this tool has existed, which is why a spine placed with it
	/// runs down the front of the chest rather than through it.
	/// </summary>
	public BonePlacement BonePlacementMode { get; set; } = BonePlacement.Middle;

	/// <summary>
	/// Force every placed joint onto y = 0.
	///
	/// Y BECAUSE THAT IS THIS PROJECT'S MIRROR PLANE — EffigyRigPanel.MirrorSelectedBone reflects
	/// across it, and a mirrored rig is only clean if the chain it hangs off sits exactly on it. A
	/// spine joint at y = 0.004 mirrors an arm to 0.008 from where its twin should be, which is
	/// invisible until something animates.
	/// </summary>
	public bool SnapBonesToCentre { get; set; }

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

	/// <summary>The measured run of material, drawn cooler and thinner than the bone itself so it
	/// reads as a measurement rather than as geometry.</summary>
	private static readonly Color BoneSpanColor = new( 0.55f, 0.8f, 1f, 0.7f );

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
	/// IN MIDDLE MODE THE FIRST CLICK LOSES THAT GHOST BONE and gets a knob instead. The guess stood
	/// the bone off the surface along the surface NORMAL, which meant something while joints lived on
	/// the skin and means nothing from a point inside the model — it would draw a bone pushing back
	/// out through the face you were pointing at. A knob at the placement point plus the measured run
	/// is the honest version: it says where, and says nothing about a direction only the second click
	/// decides.
	///
	/// Drawn depth-ignoring like the committed skeleton (DrawRigSkeleton) so the preview reads
	/// through the mesh the same way placed bones already do, rather than disappearing the moment it
	/// points away from the camera — which in Middle mode is every time, since the point is behind
	/// the surface by construction.
	///
	/// Same raycast MeshRaycast/_displayBodies pairing as the face and body picks.
	/// </summary>
	private void BoneToolFrame()
	{
		if ( !BoneToolActive || !_canvasHasCursor )
			return;

		var ray = Gizmo.CurrentRay;
		var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
		var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

		if ( MeshRaycast.Raycast( _displayBodies, origin, direction, PickTreeFor ) is not { } hit )
			return;

		// MEASURED IN THE BODY THE HIT NAMES, not across all of them. Raycast above has already
		// decided which solid the cursor is on — including discarding surfaces buried inside another
		// body — and asking that question twice would both cost a second full scan every frame and
		// leave two answers that could disagree.
		var span = BonePlacementMode == BonePlacement.Middle
			? MeshRaycast.FirstSolidSpan( hit.Body.Mesh, origin, direction )
			: null;

		var point = PlacementPoint( hit.Hit, span );
		var world = new Vector3( point.x, point.y, point.z );

		Gizmo.Draw.IgnoreDepth = true;

		if ( span is { } run )
			DrawBoneSpan( run, world );

		Gizmo.Draw.Color = BoneToolPreviewColor;

		if ( PendingBoneHead is { } head && (point - head).Length > 0.01f )
		{
			var headWorld = new Vector3( head.x, head.y, head.z );
			var (xAxis, zAxis) = PreviewBasis( head, point );
			DrawDogBone( headWorld, world, xAxis, zAxis );
		}
		else if ( PendingBoneHead is null && span is null )
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
	/// Where the click lands: the surface, or the middle of the material behind it, then pulled onto
	/// the mirror plane if that is asked for.
	///
	/// THE SNAP IS APPLIED LAST, and it moves the point off the ray. That is the point of it — an
	/// exact zero is worth more than staying under the cursor, because it is what makes a mirrored
	/// rig line up. The preview draws the result rather than the ray, so the jump is visible before
	/// the click rather than discovered in the inspector afterwards.
	///
	/// A run that could not be measured falls back to the surface silently. It means the ray found no
	/// way out of the material — an open mesh, a single-sided surface — and refusing the click there
	/// would leave the tool dead on such a model with nothing said about why.
	/// </summary>
	private Vec3 PlacementPoint( MeshHit hit, SolidSpan? span )
	{
		var point = span is { } run ? run.Midpoint : hit.Point;

		return SnapBonesToCentre ? new Vec3( point.x, 0f, point.z ) : point;
	}

	/// <summary>
	/// The run of material the placement was measured through: a line from the near face to the far
	/// one, capped at both ends, with its thickness written beside the point it produced.
	///
	/// WITHOUT THIS THE TOOL LOOKS BROKEN. A click in Middle mode puts the joint somewhere the cursor
	/// is not, behind a surface you cannot see through — so the first honest question is "why did it
	/// go there?" The line answers it by showing the two faces the depth was measured between, and it
	/// makes the fallback legible too: no line means no run was found and the click will land on the
	/// skin.
	/// </summary>
	private static void DrawBoneSpan( SolidSpan span, Vector3 placement )
	{
		var entry = new Vector3( span.Entry.x, span.Entry.y, span.Entry.z );
		var exit = new Vector3( span.Exit.x, span.Exit.y, span.Exit.z );

		Gizmo.Draw.Color = BoneSpanColor;
		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.Line( entry, exit );

		// Ends marked, or the line reads as a bone shaft rather than as a measurement.
		var tick = BoneHandleRadius * 0.25f;
		Gizmo.Draw.SolidSphere( entry, tick, 6, 6 );
		Gizmo.Draw.SolidSphere( exit, tick, 6, 6 );

		// The thickness, beside the point it produced. That number is the whole argument for the mode:
		// it says how far off the surface the joint is about to sit.
		Gizmo.Draw.WorldText( $"{span.Thickness:0.##}",
			new Transform( placement + Vector3.Up * (BoneHandleRadius * 1.2f) ), "Roboto", 9f, TextFlag.Center );
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
