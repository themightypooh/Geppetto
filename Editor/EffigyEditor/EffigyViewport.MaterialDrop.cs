using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Dropping a material onto a face.
///
/// The other half of <see cref="EffigyMaterialBrowser"/>, and the reason the browser is worth
/// having: a material list you can only look at is a material list you may as well have opened in
/// the asset browser. The gesture is the feature.
///
/// It is deliberately NOT a mode. Nothing has to be armed, no tool has to be selected, and the
/// viewport does not care what it was doing a moment ago — the same reasoning as the right-click
/// face menu in EffigyViewport.FaceMenu.cs, which this shares its raycast with. You point at a
/// face, you let go, that face wears the material.
///
/// WHERE THE SLOT COMES FROM. Nowhere on screen. A face carries a slot number and the drop names
/// only a material, so something has to choose the number; Effigy.MaterialDrop does, and does it in
/// the kernel where MaterialDropTests can hold the choice to account. This file's job stops at
/// turning a drop position into a face.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>Raised when a material is dropped on a face: the face, and the material's relative
	/// path. As with the context menu, the viewport resolves what was pointed at and stops — what
	/// it MEANS for the document is the window's business, because the window owns the studio, the
	/// undo stack and the rebuild.</summary>
	public Action<EffigyFaceHit, string> MaterialDropped { get; set; }

	/// <summary>The face the drag is currently over, held between hover events so the frame loop
	/// can light it up. Null when the drag is over nothing pickable, or over nothing at all.</summary>
	private EffigyFaceHit? _dropHit;

	/// <summary>The material the current drag carries, or null when it carries something that is
	/// not one. Kept only so the highlight can be dropped the moment the drag leaves.</summary>
	private string _dropMaterial;

	/// <summary>Green rather than the pick blue or the selected amber, because it is neither: those
	/// two say "this is what your click will choose", and this says "letting go here changes the
	/// model". A third meaning gets a third colour.</summary>
	private static readonly Color MaterialDropColor = new( 0.35f, 0.9f, 0.5f, 1f );

	/// <summary>Turn drops on. Called from the constructor — a widget that has not asked for them
	/// never sees a drag at all, and the failure is silent in exactly the way that costs an hour.
	/// </summary>
	private void EnableMaterialDrops() => AcceptDrops = true;

	/// <summary>Light up the face the drag is over. Called from OnPreFrame with the rest of the
	/// per-frame drawing, because Gizmo.Draw only means anything inside the scene's own frame and a
	/// drag event is nowhere near one.</summary>
	private void MaterialDropFrame()
	{
		if ( _dropHit is { } hit )
			DrawFace( hit.Body, hit.FaceIndex, MaterialDropColor );
	}

	public override void OnDragHover( DragEvent ev )
	{
		base.OnDragHover( ev );

		_dropHit = null;
		_dropMaterial = MaterialFromDrag( ev.Data );

		if ( _dropMaterial is null || !DropsAllowed )
		{
			ev.Action = DropAction.Ignore;
			return;
		}

		// Copy whether or not a face is under the cursor. Refusing the drag over empty space would
		// flick the cursor between "yes" and "no" as you cross the model's silhouette on the way to
		// the face you are aiming at, which reads as the window rejecting the material rather than
		// as you not being on a face yet. What actually stops a miss is the hit test in OnDragDrop.
		ev.Action = DropAction.Copy;

		if ( TryPickFaceAt( ev.LocalPosition, out var hit ) )
			_dropHit = hit;
	}

	public override void OnDragDrop( DragEvent ev )
	{
		base.OnDragDrop( ev );

		var material = MaterialFromDrag( ev.Data );

		_dropHit = null;
		_dropMaterial = null;

		if ( material is null || !DropsAllowed )
			return;

		// Picked again from the drop position rather than trusting the last hover. The two are
		// usually the same face, but a drop can arrive without a hover in front of it — dropping
		// straight in from another window is one way — and acting on a face resolved from a stale
		// position would paint whatever the cursor last passed over.
		if ( !TryPickFaceAt( ev.LocalPosition, out var hit ) )
			return;

		MaterialDropped?.Invoke( hit, material );
	}

	public override void OnDragLeave()
	{
		base.OnDragLeave();

		_dropHit = null;
		_dropMaterial = null;
	}

	/// <summary>
	/// Whether a drop can land right now.
	///
	/// The same list the right-click menu refuses on, and for the same reason: while a sketch is
	/// open or a dialog has armed a pick, the bodies are not what you are pointing at, and painting
	/// one mid-pick would edit a body the feature being configured may not even be allowed to
	/// touch. Sculpting is in the list too — a drop there would land on the cage.
	/// </summary>
	private bool DropsAllowed =>
		!IsSketching && !PlanePickMode && !SketchPickMode && !FacePickMode && !BodyPickMode && !BoneToolActive;

	/// <summary>
	/// The face under a point in THIS widget, for a drag that has no cursor ray to borrow.
	///
	/// Everywhere else in the viewport aims with Gizmo.CurrentRay, which is built once a frame from
	/// where the mouse is hovering. A drag is not a hover: the canvas does not report itself under
	/// the mouse while one is in progress, so CaptureCursorRay holds whatever ray was current when
	/// the drag began — the far side of the screen, usually the browser you dragged out of. The ray
	/// has to be built from the position the drag event carries instead.
	///
	/// Local to the widget, so the canvas's own offset comes off first (the tool strip sits above
	/// it), and scaled by DpiScale, because the camera renders at physical pixels while Qt reports
	/// logical ones. Same conversion the scene viewport does for the same reason.
	/// </summary>
	private bool TryPickFaceAt( Vector2 position, out EffigyFaceHit hit )
	{
		hit = default;

		if ( !_canvas.IsValid() || !_camera.IsValid() )
			return false;

		var local = position - _canvas.Position;

		// Outside the 3D canvas entirely — over the tool strip, or past an edge. A ray built from
		// here still resolves to something, which is exactly the problem: it would paint a face
		// while you were letting go over a button.
		if ( local.x < 0 || local.y < 0 || local.x > _canvas.Size.x || local.y > _canvas.Size.y )
			return false;

		var ray = _camera.ScreenPixelToRay( local * DpiScale );

		// Vector3 -> Vec3 is a straight re-type, not a transform: the kernel's axes and s&box's
		// line up exactly. Same conversion CaptureCursorRay does.
		return TryPickFace(
			new Vec3( ray.Position.x, ray.Position.y, ray.Position.z ),
			new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z ),
			out hit );
	}

	/// <summary>
	/// Turn a dragged payload into a material path, or null.
	///
	/// Text first, because that is where both the editor's own asset list and
	/// <see cref="EffigyMaterialBrowser"/> put the relative path, and because a multi-selection
	/// arrives as one line per asset — the first is the one under the cursor.
	///
	/// Then RESOLVED THROUGH THE ASSET SYSTEM rather than believed. Two things fall out of that.
	/// A model or a sound dragged onto a face is refused instead of being written into MaterialNames
	/// as a material that will never load, which is a document you cannot tell is broken by looking
	/// at it. And the path stored is the asset's own RelativePath, so a drag and a pick through the
	/// browse button in EffigyMaterialSlot write the same spelling for the same file — which is what
	/// lets MaterialDrop.SlotFor recognise a material it has already given a slot to.
	/// </summary>
	private static string MaterialFromDrag( DragData data )
	{
		if ( data is null )
			return null;

		foreach ( var candidate in DraggedPaths( data ) )
		{
			var path = candidate?.Trim();

			if ( string.IsNullOrWhiteSpace( path ) )
				continue;

			if ( AssetSystem.FindByPath( path ) is { } asset )
			{
				if ( asset.AssetType == AssetType.Material )
					return asset.RelativePath;

				continue;
			}

			// Not something the asset system knows. Accepted only when it is plainly a relative
			// material path — a document that arrived from another machine and refers to a material
			// this one has not compiled yet is a real case, and refusing it would make the drop
			// silently do nothing. An ABSOLUTE path is refused whatever it ends in: it is true on
			// one machine, and storing it would export a material nobody else can resolve.
			if ( !Path.IsPathRooted( path ) && path.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
				return path.Replace( '\\', '/' );
		}

		return null;
	}

	private static IEnumerable<string> DraggedPaths( DragData data )
	{
		if ( !string.IsNullOrWhiteSpace( data.Text ) )
		{
			foreach ( var line in data.Text.Split( '\n' ) )
				yield return line;
		}

		if ( data.Files is { } files )
		{
			foreach ( var file in files )
				yield return file;
		}
	}
}
