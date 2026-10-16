using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>What a right-click landed on: the body, which face of it, a FaceRef captured at the
/// click point, and the material slot that face is on right now. The slot is carried along because
/// the menu has to tick the current one, and by the time the menu is built the raycast is gone.</summary>
internal readonly struct EffigyFaceHit
{
	public EffigyFaceHit( Body body, int faceIndex, FaceRef reference, int material )
	{
		Body = body;
		FaceIndex = faceIndex;
		Reference = reference;
		Material = material;
	}

	public Body Body { get; }
	public int FaceIndex { get; }
	public FaceRef Reference { get; }
	public int Material { get; }
}

/// <summary>
/// Right-clicking a face of the model.
///
/// Separate from the face picking in EffigyViewport.Sketching.cs, and deliberately so: that pass
/// only runs while FacePickMode is armed by a dialog, and is scoped to the bodies THAT feature may
/// act on. This one is always live and works on whatever is on screen, because "right-click the
/// thing you can see" is not a mode you enter.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>Raised with the face under the cursor when the right button opens a menu over one.
	/// The viewport resolves the face and stops there — what the menu OFFERS is a question about
	/// the studio, which lives in the window.</summary>
	public Action<EffigyFaceHit> FaceContextMenuRequested { get; set; }

	/// <summary>Right-click while editing a mesh. The window opens the menu for the selection.</summary>
	public Action MeshEditContextMenuRequested { get; set; }

	// --- the cursor ray -------------------------------------------------------------------------
	//
	// Kept from the last frame rather than built on demand. Gizmo.CurrentRay only means anything
	// inside the scene's frame — a menu callback is nowhere near one — and a click does not move
	// the mouse, so last frame's ray is aimed exactly where the click landed.

	private Vec3 _cursorRayOrigin;
	private Vec3 _cursorRayDirection;
	private bool _cursorRayValid;

	private void CaptureCursorRay()
	{
		// HELD, not cleared, when the cursor leaves the canvas. Right-dragging turns _canvasHasCursor
		// off (that is how the fly camera stops sketch clicks landing), so clearing here would throw
		// the ray away on the very press that wants it.
		if ( !_canvasHasCursor )
			return;

		var ray = Gizmo.CurrentRay;

		// Vector3 -> Vec3 is a straight re-type, not a transform: the kernel's axes and s&box's line
		// up exactly. Same conversion FacePickFrame does.
		_cursorRayOrigin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
		_cursorRayDirection = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );
		_cursorRayValid = true;
	}

	// --- telling a right-click apart from a right-drag -------------------------------------------
	//
	// Right-drag flies the camera, and the context-menu event arrives on the button RELEASE. Without
	// this, every orbit that ended over the model — which is most of them, the model is what you are
	// orbiting around — would pop a menu over the thing you were trying to look at.
	//
	// The test is whether the camera actually MOVED during the press, not whether the button was
	// held: a press that flew nowhere is a click, however long it was held down.

	private bool _flyingCamera;

	/// <summary>The right button is held and steering the camera. W and S fly then, so the handle
	/// keys stand aside.</summary>
	public bool IsFlyingCamera => _flyingCamera;
	private Vector3 _flightPosition;
	private Vector3 _flightForward;
	private float _cameraMovedAt = float.NegativeInfinity;

	private void NoteCameraFlight( bool flying )
	{
		if ( flying && !_flyingCamera )
		{
			// First frame of this press — remember where it started from, so the comparison below is
			// against this press and not against wherever the last one left off.
			_flightPosition = _camera.WorldPosition;
			_flightForward = _camera.WorldRotation.Forward;
		}
		else if ( flying )
		{
			if ( Moved( _camera.WorldPosition, _flightPosition, 0.01f )
				|| Moved( _camera.WorldRotation.Forward, _flightForward, 0.002f ) )
				_cameraMovedAt = RealTime.Now;
		}

		_flyingCamera = flying;
	}

	private new static bool Moved( Vector3 a, Vector3 b, float epsilon ) =>
		MathF.Abs( a.x - b.x ) > epsilon
		|| MathF.Abs( a.y - b.y ) > epsilon
		|| MathF.Abs( a.z - b.z ) > epsilon;

	// --- the pick ---------------------------------------------------------------------------------

	/// <summary>The face under the cursor, against everything currently on screen.</summary>
	public bool TryPickFaceUnderCursor( out EffigyFaceHit hit )
	{
		hit = default;

		return _cursorRayValid && TryPickFace( _cursorRayOrigin, _cursorRayDirection, out hit );
	}

	/// <summary>
	/// The face some ray hits, against everything currently on screen.
	///
	/// Split from the cursor version for the material drop, which aims with a ray built from the
	/// drop position rather than from the hover — see EffigyViewport.MaterialDrop.cs for why it
	/// cannot use the cursor ray. Everything below the ray is identical for both and was worth
	/// having in one place rather than two that drift.
	///
	/// Visible bodies only. A hidden body is not something you can point at, and letting a pick land
	/// on one would put a material on a face nobody can see and cannot click again to change.
	/// </summary>
	private bool TryPickFace( Vec3 origin, Vec3 direction, out EffigyFaceHit hit )
	{
		hit = default;

		var visible = _displayBodies.Where( b => b?.Mesh is not null && b.Visible ).ToList();

		if ( visible.Count == 0 )
			return false;

		if ( MeshRaycast.Raycast( visible, origin, direction, PickTreeFor ) is not { } result )
			return false;

		var mesh = result.Body.Mesh;
		var index = result.Hit.FaceIndex;

		if ( index < 0 || index >= mesh.Faces.Count )
			return false;

		// Capture rather than the raw constructor, same as the sketch-plane pick: it records where on
		// the face the click landed, which is what lets the reference survive the face being remade
		// at a different size.
		hit = new EffigyFaceHit( result.Body, index,
			FacePlane.Capture( result.Body, index, result.Hit.Point ),
			mesh.Faces[index].Material );

		return true;
	}

	/// <summary>
	/// Qt's context-menu event, deliberately unused. The right button is also s&amp;box's fly camera,
	/// and the camera takes the press before this event arrives — so on the canvas it never fired and
	/// the radial never opened. The tap is detected in the frame loop instead; see RightTapFrame.
	/// </summary>
	protected override void OnContextMenu( ContextMenuEvent e )
	{
	}

	/// <summary>
	/// What a TAP of the right button opens, wherever it lands. Called by RightTapFrame once it has
	/// decided a right press was a tap rather than a look-around.
	/// </summary>
	private void OnRightTap()
	{
		// In a sketch, backing out of what is being drawn still comes first — right-click to stop
		// the line you are dragging out is the reflex every CAD sketcher has. Otherwise the pie:
		// the sketch tools, Constrain for a selection, and Finish.
		if ( IsSketching )
		{
			if ( CancelHalfDrawnSketchEntity() )
				return;

			SketchRadialRequested?.Invoke();
			return;
		}

		// Picking for a feature: the way out of the pick, and the views that help find the thing.
		if ( PlanePickMode || SketchPickMode || FacePickMode || EdgePickMode || BodyPickMode || BoneToolActive )
		{
			OpenPickRadial();
			return;
		}

		// Editing a mesh, the right button is the menu for what is selected — Blender's context
		// menu, and the quickest way to the tool you want without knowing which menu holds it.
		if ( IsMeshEditing )
		{
			MeshEditContextMenuRequested?.Invoke();
			return;
		}

		// A face, or empty space — both open the pie now. Empty space used to say there was no menu.
		ContextRadialRequested?.Invoke( TryPickFaceUnderCursor( out var hit ) ? hit : (EffigyFaceHit?)null );
	}
}
