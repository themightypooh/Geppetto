using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Dragging the bodies an open Transform is holding — move them with the mouse.
///
/// THE SAME BARGAIN AS THE FACE ARROW, one level up. Transform already moves bodies; what it asked
/// for was three numbers typed into a panel, and typing 0.4 into Z to find out whether 0.4 was right
/// is not how anybody places a part. Press Transform, drag the handle, watch the part go.
///
/// THREE ARROWS HERE, NOT ONE. The face handle is deliberately single-axis because a face has a
/// direction of its own and the other two arrows would be inert (see EffigyViewport.FaceDrag). A
/// body has no such direction: every axis moves it, so every axis is honest, and Gizmo.Control.Position
/// is exactly the handle for that.
///
/// ONLY WHILE A TRANSFORM'S DIALOG IS OPEN, for the reason that keeps this parametric: a body does
/// not move because it was shoved, it moves because a FEATURE says it does. The drag writes the open
/// feature's Translate — typing, with the mouse — and nothing is appended to the tree behind anyone's
/// back. The handle lives exactly as long as that dialog.
///
/// THE VIEWPORT RESOLVES THE DRAG AND STOPS THERE. Which parameter the displacement lands in is a
/// question about the feature, so it lives with the dialog that owns it.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>
	/// Set by the dialog of a feature that moves whole bodies, for as long as that dialog is open.
	/// The gate that keeps this parametric.
	///
	/// TURNING IT ON STARTS AT MOVE, whatever the last Transform was left on. Pressing Transform is
	/// what promises arrows; opening one onto a scale nub because the previous dialog ended there
	/// would be a handle nobody asked for.
	/// </summary>
	public bool BodyDragEnabled
	{
		get => _bodyDragEnabled;
		set
		{
			if ( value && !_bodyDragEnabled )
				_bodyDragMode = BodyDragMode.Move;

			_bodyDragEnabled = value;
		}
	}

	private bool _bodyDragEnabled;

	/// <summary>What the body handle does. One handle at a time: arrows, rings and a scale nub on one
	/// origin fight for the same pixels, and a click that lands on the wrong one is a part that jumps.</summary>
	public enum BodyDragMode { Move, Rotate, Scale }

	private BodyDragMode _bodyDragMode = BodyDragMode.Move;

	/// <summary>
	/// Switch the body handle, or do nothing when there is no body handle — W/E/R call this beside
	/// <see cref="SetBoneDragMode"/>, so the three keys mean one thing whichever handle is up.
	///
	/// REFUSED MID-DRAG. Swapping the control under a held button leaves the old one believing it is
	/// still grabbed, and the new one takes the next frame's mouse as the start of a drag.
	/// </summary>
	public void SetBodyDragMode( BodyDragMode mode )
	{
		if ( !BodyDragEnabled || _draggingBody )
			return;

		_bodyDragMode = mode;
	}

	/// <summary>
	/// Raised once when a drag starts, before anything has moved, with where the handle was.
	///
	/// THE ANCHOR IS PASSED because rotate and scale pivot about it, and the consumer — which turns
	/// a drag into Translate — has to keep that point still while the rest turns or grows round it.
	/// </summary>
	public Action<Vec3> BodyDragBegan { get; set; }

	/// <summary>
	/// Raised every frame the handle moves: the displacement accumulated since the drag started,
	/// not the per-frame delta.
	///
	/// The total rather than something to integrate, because the consumer sets a parameter from it
	/// and a parameter is a value, not an increment.
	/// </summary>
	public Action<Vec3> BodyDragMoved { get; set; }

	/// <summary>
	/// Raised every frame the rings turn: the rotation since the grab as an axis and an angle in
	/// degrees, world-aligned, about the handle.
	///
	/// AXIS AND DEGREES because that is what a Transform stores; the viewport takes the quaternion
	/// apart so the dialog is handed the feature's own vocabulary rather than one to unpick.
	/// </summary>
	public Action<Vec3, float> BodyRotateDragged { get; set; }

	/// <summary>Raised every frame the scale nub moves: the uniform factor since the grab, about the
	/// handle, already clamped away from zero.</summary>
	public Action<float> BodyScaleDragged { get; set; }

	/// <summary>
	/// Whether anything is wired to what the handle does NOW. A handle nobody listens to is a hitbox
	/// over the model that eats clicks and does nothing, so without a consumer there is no handle.
	/// </summary>
	private bool HasBodyDragConsumer() => _bodyDragMode switch
	{
		BodyDragMode.Rotate => BodyRotateDragged is not null,
		BodyDragMode.Scale => BodyScaleDragged is not null,
		_ => BodyDragMoved is not null,
	};

	/// <summary>Raised when the button comes up.</summary>
	public Action BodyDragEnded { get; set; }

	/// <summary>True while the handle is being dragged. Idle picking stands down for the duration,
	/// or the click that ends the drag would also re-pick whatever is under the cursor.</summary>
	public bool IsDraggingBody => _draggingBody;

	private bool _draggingBody;
	private Vector3 _bodyDragAnchor;
	private Vector3 _bodyDragDelta;

	/// <summary>
	/// The handle for the bodies the open feature is holding, and the drag it reports.
	///
	/// Stood down while a sketch, the sculpt brush, the paint brush or the bone tool is armed: each
	/// of those has a click of its own, and a set of arrows floating over the model while one of them
	/// is live is an invitation to a click that will not do what it looks like.
	/// </summary>
	private void BodyDragFrame()
	{
		if ( !BodyDragEnabled || !HasBodyDragConsumer() )
		{
			EndBodyDrag();
			return;
		}

		if ( (IsSketching || IsSculpting || IsPainting || IsMaterialBrushing || IsNoting || BoneToolActive) && !_draggingBody )
		{
			EndBodyDrag();
			return;
		}

		// MID-DRAG THE BODIES ARE TRAVELLING. The rebuild moves them by exactly the displacement the
		// handle has reported, so anchor plus displacement is where they are — reading their live
		// centre back would add that movement a second time and the arrows would run away from the
		// cursor.
		//
		// Rotate and scale keep the anchor still — that is what the pivot is for — so their handle
		// stays where it was grabbed.
		if ( _draggingBody )
		{
			DrawBodyDragHandle( _bodyDragMode == BodyDragMode.Move ? _bodyDragAnchor + _bodyDragDelta : _bodyDragAnchor );
			return;
		}

		if ( !TryBodyDragHandle( out var centre ) )
		{
			EndBodyDrag();
			return;
		}

		DrawBodyDragHandle( centre );
	}

	/// <summary>
	/// The handle for the current mode, and the drag it reports.
	///
	/// EVERY CONTROL RETURNS FALSE on a frame the value did not move, so a still frame mid-drag is not
	/// the end of the drag — the mouse button is what says that (RigViewport.cs:2337 makes the same
	/// distinction for the same reason).
	///
	/// THE THREE DISAGREE ABOUT WHAT THEY REPORT. Position hands back a per-frame delta, so it is
	/// accumulated; Rotate and Scale hand back the total since the grab, so they are assigned.
	/// Accumulating those would compound every frame's rotation onto the last.
	/// </summary>
	private void DrawBodyDragHandle( Vector3 origin )
	{
		using var scope = Gizmo.Scope( "body-drag", new Transform( origin ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		switch ( _bodyDragMode )
		{
			case BodyDragMode.Rotate:
			{
				if ( !Gizmo.Control.Rotate( "body-rotate", Rotation.Identity, out var rotation ) )
					break;

				BeginBodyDrag( origin );

				// w is the cosine of half the angle, the vector part the axis times its sine.
				var w = rotation.w.Clamp( -1f, 1f );
				var sin = MathF.Sqrt( MathF.Max( 0f, 1f - w * w ) );

				// No rotation has no axis to name; the grab has happened, nothing has turned yet.
				if ( sin < 1e-6f )
					return;

				var axis = new Vec3( rotation.x, rotation.y, rotation.z ) / sin;
				var degrees = 2f * MathF.Acos( w ).RadianToDegree();

				BodyRotateDragged?.Invoke( axis, degrees );
				return;
			}

			case BodyDragMode.Scale:
			{
				if ( !Gizmo.Control.Scale( "body-scale", 1f, out var scale ) )
					break;

				BeginBodyDrag( origin );

				// Clamped as the rig's scale handle is (RigViewport.cs:2472): a factor reaching zero
				// makes the Transform refuse to rebuild, which reads as the drag breaking the model.
				BodyScaleDragged?.Invoke( scale.Clamp( 0.01f, 100f ) );
				return;
			}

			default:
			{
				if ( !Gizmo.Control.Position( "body-move", Vector3.Zero, out var delta, Rotation.Identity ) )
					break;

				BeginBodyDrag( origin );

				_bodyDragDelta += delta;

				BodyDragMoved?.Invoke( new Vec3( _bodyDragDelta.x, _bodyDragDelta.y, _bodyDragDelta.z ) );
				return;
			}
		}

		// Held through a frame that simply did not move.
		if ( _draggingBody && Gizmo.IsLeftMouseDown )
			return;

		EndBodyDrag();
	}

	private void BeginBodyDrag( Vector3 origin )
	{
		if ( _draggingBody )
			return;

		_draggingBody = true;
		_bodyDragAnchor = origin;
		_bodyDragDelta = Vector3.Zero;
		BodyDragBegan?.Invoke( new Vec3( origin.x, origin.y, origin.z ) );
	}

	private void EndBodyDrag()
	{
		if ( !_draggingBody )
			return;

		_draggingBody = false;
		_bodyDragDelta = Vector3.Zero;
		BodyDragEnded?.Invoke();
	}

	/// <summary>
	/// Where the handle sits: the centre of the bounds of the bodies the feature will move.
	///
	/// THE FEATURE'S SET, NOT THE IDLE ONE — SelectedBodyIds is what the DIALOG is holding, and the
	/// handle answers the dialog. EMPTY MEANS EVERY BODY, because that is what BodySelectionParam
	/// means when nothing has been picked, and a Transform opened on nothing still moves the whole
	/// model; a handle that refused to appear until a body was picked would be lying about that.
	///
	/// BOUNDS CENTRE RATHER THAN A VERTEX AVERAGE, so a dense end of a part does not drag the arrows
	/// off into it. Where a translate handle sits does not change what the drag does — it only has to
	/// be somewhere the user reads as "the thing I am moving".
	/// </summary>
	private bool TryBodyDragHandle( out Vector3 centre )
	{
		centre = Vector3.Zero;

		var wanted = SelectedBodyIds;
		var all = wanted is null || wanted.Count == 0;

		var min = Vec3.Zero;
		var max = Vec3.Zero;
		var found = false;

		foreach ( var body in _displayBodies )
		{
			if ( body?.Mesh is not { } mesh || !body.Visible )
				continue;

			if ( !all && (body.Id is not { } id || !wanted.Contains( id )) )
				continue;

			// Cached per mesh - see TryMeshBounds. This runs every frame something is selected,
			// and it used to walk every vertex of every selected body to do it.
			if ( !TryMeshBounds( mesh, out var bodyMin, out var bodyMax ) )
				continue;

			if ( !found )
			{
				min = bodyMin;
				max = bodyMax;
				found = true;
				continue;
			}

			min = new Vec3( MathF.Min( min.x, bodyMin.x ), MathF.Min( min.y, bodyMin.y ), MathF.Min( min.z, bodyMin.z ) );
			max = new Vec3( MathF.Max( max.x, bodyMax.x ), MathF.Max( max.y, bodyMax.y ), MathF.Max( max.z, bodyMax.z ) );
		}

		if ( !found )
			return false;

		var mid = (min + max) * 0.5f;

		centre = new Vector3( mid.x, mid.y, mid.z );

		return true;
	}
}
