using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The viewport side of the radial menu (layout and drawing in EffigyRadialMenu): opening it at
/// the cursor on a TAP of the right button, reading the mouse while it is up, and running what is
/// picked.
///
/// A TAP, BECAUSE THE RIGHT BUTTON IS ALSO THE CAMERA. Held and dragged it flies the view; pressed
/// and let go without the view moving it is a click — the distinction OnContextMenu already makes
/// (see NoteCameraFlight). So flying is untouched, and a click that used to open a list, or say
/// "no menu here", opens the pie instead. There is no longer anywhere a right-click does nothing.
///
/// WHILE IT IS UP IT OWNS THE MOUSE, like the view cube: a click picks a slot, never places a sketch
/// point or picks a face behind it. A click in the dead zone, a right-click, or Escape closes it.
/// </summary>
internal sealed partial class EffigyViewport
{
	private readonly List<EffigyRadialMenu.Item> _radialItems = new();
	private readonly List<Action> _radialActions = new();
	private bool _radialOpen;
	private Vector2 _radialCentre;
	private string _radialTitle;
	private int _radialHover = -1;
	private bool _radialLeftWasDown;
	private float _radialClosedAt = float.NegativeInfinity;
	private CubeOverlayObject _radialOverlay;

	/// <summary>The window's half: what a right-click on a face, or on nothing, offers. Given the
	/// face under the cursor, or null for empty space.</summary>
	public Action<EffigyFaceHit?> ContextRadialRequested { get; set; }

	/// <summary>The window's half inside a sketch, when nothing is half-drawn.</summary>
	public Action SketchRadialRequested { get; set; }

	public bool RadialOpen => _radialOpen;

	/// <summary>True while the menu is up — the frame treats it like an overlay under the mouse.</summary>
	private bool RadialOwnsMouse => _radialOpen;

	/// <summary>Open the menu at the cursor with these slots. Slots past eight are dropped — the
	/// caller is expected to end its list with "More…".</summary>
	public void OpenRadial( string title, IEnumerable<(EffigyRadialMenu.Item Item, Action Run)> slots )
	{
		_radialItems.Clear();
		_radialActions.Clear();

		foreach ( var (item, run) in slots.Take( EffigyRadialMenu.MaxItems ) )
		{
			_radialItems.Add( item );
			_radialActions.Add( run );
		}

		if ( _radialItems.Count == 0 )
			return;

		var canvas = _canvas.Size * DpiScale;
		_radialCentre = EffigyRadialMenu.ClampCentre( _canvas.FromScreen( Editor.Application.CursorPosition ) * DpiScale, canvas, DpiScale );
		_radialTitle = title;
		_radialHover = -1;

		// The button that opened it is the right one; a left button already down must come up and
		// go down again to count as a pick.
		_radialLeftWasDown = Editor.Application.MouseButtons.HasFlag( MouseButtons.Left );
		_radialOpen = true;
	}

	public void CloseRadial()
	{
		if ( !_radialOpen )
			return;

		_radialOpen = false;
		_radialHover = -1;
		_radialClosedAt = RealTime.Now;
	}

	// --- telling a tap from a look ----------------------------------------------------------

	/// <summary>Longest a press can be held and still be a tap, seconds.</summary>
	private const float RightTapSeconds = 0.35f;

	/// <summary>How far the view may turn during a press and it still be a tap, degrees. The fly
	/// camera locks the cursor while the button is down, so the cursor cannot be what tells a look
	/// from a tap — whether the VIEW moved can.</summary>
	private const float RightTapDegrees = 0.5f;

	private bool _rightWasDown;
	private bool _rightPressOnCanvas;
	private float _rightPressedAt;
	private Vector3 _rightPressForward;
	private Vector3 _rightPressPosition;
	private Vector2 _rightPressCursor;
	private bool _rightPressClosedMenu;

	/// <summary>
	/// Watch the right button and open the radial on a tap. Early in the frame, before the fly
	/// camera reads the button, so the press is seen on the canvas it landed on.
	///
	/// A TAP is a press and release inside RightTapSeconds during which the view did not turn or
	/// move and the cursor did not travel. Anything else was the camera being used, and opens
	/// nothing — exactly as right-dragging always behaved.
	/// </summary>
	private void RightTapFrame()
	{
		var down = Editor.Application.MouseButtons.HasFlag( MouseButtons.Right );
		var cursor = Editor.Application.CursorPosition;

		if ( down && !_rightWasDown )
		{
			_rightPressOnCanvas = IsActiveWindow && _canvas.IsUnderMouse && !CubeOwnsMouse && !CalloutsUnderMouse;
			_rightPressedAt = RealTime.Now;
			_rightPressForward = _camera.WorldRotation.Forward;
			_rightPressPosition = _camera.WorldPosition;
			_rightPressCursor = cursor;

			// Seen before RadialFrame closes the menu on this same press, so it is still known that
			// this click was the one that closed it — its release must not open the next one.
			_rightPressClosedMenu = _radialOpen;
		}
		else if ( !down && _rightWasDown && _rightPressOnCanvas )
		{
			_rightPressOnCanvas = false;

			var quick = RealTime.Now - _rightPressedAt <= RightTapSeconds;
			var turned = MathF.Acos( Math.Clamp( Vector3.Dot( _rightPressForward, _camera.WorldRotation.Forward ), -1f, 1f ) )
				* (180f / MathF.PI);
			var moved = (_camera.WorldPosition - _rightPressPosition).Length > 1e-3f;
			var travelled = (cursor - _rightPressCursor).Length > 4f;

			if ( quick && turned < RightTapDegrees && !moved && !travelled && !_rightPressClosedMenu )
				OnRightTap();
		}

		_rightWasDown = down;
	}

	/// <summary>Read the mouse while the menu is up. Early in the frame, with the other overlays.</summary>
	private void RadialFrame()
	{
		RightTapFrame();

		if ( !_radialOpen )
			return;

		// Losing the window or the canvas takes the menu with it; a pie left floating over a view
		// you have tabbed away from would be picked by the next click on your return.
		if ( !IsActiveWindow )
		{
			CloseRadial();
			return;
		}

		var mouse = _canvas.FromScreen( Editor.Application.CursorPosition ) * DpiScale;
		_radialHover = EffigyRadialMenu.HitTest( mouse, _radialCentre, DpiScale, _radialItems.Count );

		var buttons = Editor.Application.MouseButtons;
		var left = buttons.HasFlag( MouseButtons.Left );
		var pressed = left && !_radialLeftWasDown;
		_radialLeftWasDown = left;

		if ( buttons.HasFlag( MouseButtons.Right ) )
		{
			CloseRadial();
			return;
		}

		if ( !pressed )
			return;

		var chosen = _radialHover;
		CloseRadial();

		// The dead zone is the way out without choosing.
		if ( chosen < 0 )
			return;

		var item = _radialItems[chosen];

		// A greyed slot says why rather than closing on nothing — the same rule as a greyed tool.
		if ( !item.Enabled )
		{
			Refuse( item.DisabledReason ?? $"{item.Label} isn't available right now." );
			return;
		}

		_radialActions[chosen]?.Invoke();
	}

	/// <summary>Draw the menu, or nothing. Last in the frame, over everything.</summary>
	private void DrawRadial()
	{
		_radialOverlay ??= new CubeOverlayObject( _gizmoInstance.World );
		_radialOverlay.Begin();

		if ( _radialOpen )
		{
			EffigyRadialMenu.Draw( _radialOverlay, new EffigyRadialMenu.State(
				_radialItems, _radialCentre, DpiScale, _radialHover, _radialTitle, Theme.Blue ) );
		}

		_radialOverlay.Push();
	}

	/// <summary>
	/// What a right-click means while picking for a feature: getting out of the pick, and the views
	/// that help you find what you were picking. Viewport-only, so it needs nothing from the window.
	/// </summary>
	private void OpenPickRadial()
	{
		OpenRadial( BoneToolActive ? "Placing bones" : "Picking", new (EffigyRadialMenu.Item, Action)[]
		{
			(new( BoneToolActive ? "Stop placing bones" : "Cancel picking", "close", Hint: "same as Escape" ), CancelPickFromRadial),
			(new( "Fit view", "fit_screen", Hint: "frame the whole part" ), FrameCamera),
			(new( "Front", "crop_portrait", Hint: "look from the front" ), () => SetStandardView( StandardView.Front )),
			(new( "Right", "crop_portrait", Hint: "look from the right" ), () => SetStandardView( StandardView.Right )),
			(new( "Home view", "home", Hint: "the starting angle" ), () => SetStandardView( StandardView.Isometric )),
			(new( "Top", "crop_square", Hint: "look straight down" ), () => SetStandardView( StandardView.Top )),
		} );
	}

	/// <summary>The same way out of a pick that Escape is.</summary>
	private void CancelPickFromRadial()
	{
		if ( BoneToolActive )
		{
			BoneToolEscape?.Invoke();
			return;
		}

		PlanePickMode = false;
		BodyPickMode = false;
		FacePickMode = false;
		EdgePickMode = false;
		PickModeCancelled?.Invoke();
	}
}
