using Editor;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// The view cube in the canvas's top-right corner — Onshape's and Fusion's cube, drawn the way
/// s&amp;box's own scene view draws its corner gizmo (SceneViewportWidget.OrientationGizmo.cs).
///
///   CLICK a face to look straight at it, an edge bevel for the 45° view between two faces, or a
///   corner for that corner's isometric view. Every one of the 26 is a solid piece you can see.
///   ARROWS round it turn the view a step: 15° a click, 90° with Shift, 5° with Ctrl, as Onshape's
///   do. Left/right and up/down turn it, the two curved ones roll it.
///   DRAG it to orbit the camera around the part. The cursor is held still while you drag, as the
///   scene view does, so a long drag never runs off the edge of the viewport.
///   HOME, bottom left of it, goes back to the three-quarter view a fresh document opens in.
///
/// THE CUBE ITSELF — pieces, hit testing, drawing — is EffigyViewCube, engine-free so a headless
/// render (tools/viewcube-sim) draws exactly what this does. This file is the camera and the mouse.
///
/// HOW IT IS DRAWN, AND WHY NOTHING ELSE WORKED. Everything is in screen pixels, through one
/// SceneCustomObject on the overlay layer that replays a list of primitives in order — exactly
/// the scene view's approach. Three earlier attempts each failed on their own:
///
///   A Qt widget on the canvas cannot be see-through: the scene is a native surface Qt never
///   composites under its children, so it was a black square, then a grey panel.
///   Gizmo lines in the world, placed through the camera, slid against text placed by pixel, and
///   flickered while the camera moved.
///
/// The overlay object has no camera in the way at all: a pixel is a pixel. It only knows
/// rectangles, lines and text out of the box; faces are added as triangle fans through the same
/// UI material, so they share its pixel space exactly.
///
/// NAMES COME FROM THE REFERENCE PLANES, not from s&amp;box's axes. Front is the face that looks
/// square at the Front (XZ) plane, Right at the Right (YZ) plane, Top at Top (XY) — so clicking
/// "Front" and then sketching on the Front plane puts you face-on to your sketch.
///
/// THE SWING IS ANIMATED rather than a cut. Jumping straight from a three-quarter view to Top
/// loses you; a quarter second of motion is enough for your eye to follow it round.
/// </summary>
internal sealed partial class EffigyViewport
{
	private const float ViewSwingSeconds = 0.25f;

	/// <summary>Degrees of orbit per pixel dragged.</summary>
	private const float CubeOrbitDegreesPerPixel = 0.4f;

	private bool _swinging;
	private float _swingT;
	private Vector3 _swingFocus;
	private float _swingDistance;
	private Vector3 _swingFromDir, _swingToDir;
	private Vector3 _swingFromUp, _swingToUp;

	private Vector3? _cubeHover;
	private ViewCubeButton _cubeHoverButton;
	private bool _cubeHovered;
	private bool _cubeMouseDown;
	private bool _cubeDragging;
	private bool _cubeLeftWasDown;
	private bool _cubeFlyWasActive;
	private Vector2 _cubePressPos;
	private Vector2 _cubeLockCursor;
	private CubeOverlayObject _cubeOverlay;

	/// <summary>True while the mouse is over the cube or working it. The frame tick treats this like
	/// being over an overlay, so a click on the cube is never also a sketch point or a pick.</summary>
	private bool CubeOwnsMouse => _cubeHovered || _cubeMouseDown || _cubeDragging;

	/// <summary>The camera's rotation, for anything drawing itself from it.</summary>
	public Rotation CameraRotation => _camera.WorldRotation;

	// --- layout, in RENDER pixels (logical times DpiScale), the space the overlay draws in -----

	private Vector2 CubeCentre
	{
		get
		{
			var top = _modelHeader?.IsValid() == true && _modelHeader.Visible ? EffigyModelChrome.HeaderHeight : 0f;
			return new Vector2( _canvas.Width - EffigyViewCube.Inset, top + EffigyViewCube.Inset ) * DpiScale;
		}
	}

	private Vector2 CubeMouse => _canvas.FromScreen( Editor.Application.CursorPosition ) * DpiScale;

	// --- camera -------------------------------------------------------------------------------

	/// <summary>
	/// Look straight down <paramref name="direction"/> — a face, edge or corner of the cube —
	/// from the side it points to, keeping the current distance to the part.
	/// </summary>
	public void LookDown( Vector3 direction )
	{
		var dir = direction.Normal;

		// Straight down or straight up has no "up" of its own. Front at the bottom of the screen
		// for Top, and at the top for Bottom, the way both Onshape and Fusion draw them.
		var up = dir.z > 0.99f ? Vector3.Left
			: dir.z < -0.99f ? Vector3.Right
			: Vector3.Up;

		SwingCameraTo( CurrentFocus(), dir, up );
	}

	/// <summary>Turn the camera around the part by a drag, in pixels.</summary>
	public void OrbitView( Vector2 delta )
	{
		_swinging = false;

		var focus = CurrentFocus();
		var rotation = _camera.WorldRotation;
		var dir = -rotation.Forward;
		var distance = (_camera.WorldPosition - focus).Length;

		if ( distance < 1f )
			distance = PlaneSize * 1.25f;

		dir = Rotation.FromAxis( Vector3.Up, -delta.x * CubeOrbitDegreesPerPixel ) * dir;

		var pitched = Rotation.FromAxis( rotation.Right, -delta.y * CubeOrbitDegreesPerPixel ) * dir;

		// Stop just short of the poles. Over the top, "up" flips and the view spins on its axis.
		if ( MathF.Abs( pitched.Normal.z ) < 0.995f )
			dir = pitched;

		dir = dir.Normal;

		var up = MathF.Abs( dir.z ) > 0.98f ? rotation.Up : Vector3.Up;

		_camera.WorldPosition = focus + dir * distance;
		_camera.WorldRotation = Rotation.LookAt( -dir, up );
	}

	private void SwingCameraTo( Vector3 focus, Vector3 direction, Vector3 up )
	{
		var rotation = _camera.WorldRotation;

		_swingFocus = focus;
		_swingDistance = (_camera.WorldPosition - focus).Length;

		// A camera sitting exactly on the focus has no distance to preserve, which happens before
		// anything has been framed. Fall back to the reference planes' own scale.
		if ( _swingDistance < 1f )
			_swingDistance = PlaneSize * 1.25f;

		_swingFromDir = -rotation.Forward;
		_swingToDir = direction.Normal;
		_swingFromUp = rotation.Up;
		_swingToUp = Rotation.LookAt( -_swingToDir, up ).Up;

		// Straight through to the opposite side would pass the direction through zero. Go round
		// by way of the camera's up instead.
		if ( Vector3.Dot( _swingFromDir, _swingToDir ) < -0.995f )
			_swingFromDir = (_swingFromDir + rotation.Up * 0.05f).Normal;

		_swingT = 0f;
		_swinging = true;
	}

	private void AdvanceSwing( bool flying )
	{
		if ( !_swinging )
			return;

		// Grabbing the camera mid-swing takes it back.
		if ( flying )
		{
			_swinging = false;
			return;
		}

		_swingT = MathF.Min( _swingT + RealTime.Delta / ViewSwingSeconds, 1f );

		var t = 1f - MathF.Pow( 1f - _swingT, 3f );
		var dir = Vector3.Lerp( _swingFromDir, _swingToDir, t ).Normal;
		var up = Vector3.Lerp( _swingFromUp, _swingToUp, t );

		// Keep up square to the direction, or LookAt rolls the view when they near parallel.
		up = (up - dir * Vector3.Dot( up, dir )).Normal;

		_camera.WorldPosition = _swingFocus + dir * _swingDistance;
		_camera.WorldRotation = Rotation.LookAt( -dir, up );

		if ( _swingT >= 1f )
			_swinging = false;
	}

	/// <summary>
	/// Turn the view one step for an arrow: 15° a click, 90° with Shift, 5° with Ctrl — Onshape's
	/// increments. Left and right turn about the screen's up, up and down about its right, and the
	/// two curved arrows roll the view about the line of sight.
	/// </summary>
	private void StepView( ViewCubeButton button )
	{
		var modifiers = Editor.Application.KeyboardModifiers;
		var degrees = modifiers.HasFlag( KeyboardModifiers.Shift ) ? 90f
			: modifiers.HasFlag( KeyboardModifiers.Ctrl ) ? 5f
			: 15f;

		var rotation = _swinging ? Rotation.LookAt( -_swingToDir, _swingToUp ) : _camera.WorldRotation;

		var turn = button switch
		{
			ViewCubeButton.Left => Rotation.FromAxis( rotation.Up, -degrees ),
			ViewCubeButton.Right => Rotation.FromAxis( rotation.Up, degrees ),
			ViewCubeButton.Up => Rotation.FromAxis( rotation.Right, degrees ),
			ViewCubeButton.Down => Rotation.FromAxis( rotation.Right, -degrees ),
			ViewCubeButton.RollLeft => Rotation.FromAxis( rotation.Forward, degrees ),
			ViewCubeButton.RollRight => Rotation.FromAxis( rotation.Forward, -degrees ),
			_ => Rotation.Identity,
		};

		SwingCameraTo( CurrentFocus(), turn * -rotation.Forward, turn * rotation.Up );
	}

	// --- input, once a frame ------------------------------------------------------------------

	/// <summary>
	/// Read the mouse over the cube and advance any swing. Early in the frame, before anything
	/// else decides whether the canvas has the cursor. Same shape as the scene view's
	/// UpdateOrientationGizmo: a press only starts on the cube, a drag locks the cursor, and a
	/// release that never dragged is a click.
	/// </summary>
	private void ViewCubeFrame( bool flying )
	{
		AdvanceSwing( flying );

		var rotation = _camera.WorldRotation;
		var mouse = CubeMouse;
		var centre = CubeCentre;

		var overBody = EffigyViewCube.OverBody( mouse, centre, DpiScale );
		var overButton = EffigyViewCube.ButtonAt( mouse, centre, DpiScale );
		var interacting = _cubeMouseDown || _cubeDragging;

		// A fly that has just ended is not a hover — the right button coming up over the cube
		// would otherwise light it.
		var suppress = flying || _cubeFlyWasActive;
		_cubeFlyWasActive = flying;

		_cubeHovered = IsActiveWindow && !suppress
			&& (interacting || _canvas.IsUnderMouse && (overBody || overButton != ViewCubeButton.None));

		if ( !_cubeDragging )
		{
			_cubeHoverButton = _cubeHovered ? overButton : ViewCubeButton.None;
			_cubeHover = _cubeHovered && overButton == ViewCubeButton.None ? EffigyViewCube.DirectionAt( mouse, rotation, centre, DpiScale ) : null;
		}

		var leftDown = Editor.Application.MouseButtons.HasFlag( MouseButtons.Left );

		if ( leftDown && !_cubeLeftWasDown && !_cubeMouseDown && _cubeHovered )
		{
			_cubeMouseDown = true;
			_cubeDragging = false;
			_cubePressPos = mouse;
			_cubeLockCursor = Editor.Application.UnscaledCursorPosition;
		}

		_cubeLeftWasDown = leftDown;

		if ( !_cubeMouseDown )
			return;

		if ( leftDown )
		{
			if ( !_cubeDragging && (mouse - _cubePressPos).Length > 4f * DpiScale )
			{
				_cubeDragging = true;
				_cubeHover = null;
				_cubeHoverButton = ViewCubeButton.None;
			}

			if ( _cubeDragging )
			{
				OrbitView( Editor.Application.CursorDelta );

				// Hold the cursor where the drag began, as the scene view does.
				_canvas.Cursor = CursorShape.Blank;
				Editor.Application.UnscaledCursorPosition = _cubeLockCursor;
			}

			return;
		}

		if ( _cubeDragging )
		{
			Editor.Application.UnscaledCursorPosition = _cubeLockCursor;
			_canvas.Cursor = CursorShape.None;
		}
		else if ( _cubeHoverButton == ViewCubeButton.Home )
		{
			SetStandardView( StandardView.Isometric );
		}
		else if ( _cubeHoverButton != ViewCubeButton.None )
		{
			StepView( _cubeHoverButton );
		}
		else if ( _cubeHover is { } direction )
		{
			LookDown( direction );
		}

		_cubeMouseDown = false;
		_cubeDragging = false;
	}

	// --- drawing, once a frame ----------------------------------------------------------------

	/// <summary>Build this frame's primitives and hand them to the overlay object.</summary>
	private void DrawViewCube()
	{
		_cubeOverlay ??= new CubeOverlayObject( _gizmoInstance.World );
		_cubeOverlay.Begin();

		// Too narrow to spare the corner: draw nothing rather than sit on the part.
		if ( _camera.IsValid() && _canvas.Width >= EffigyViewCube.Inset * 4f )
		{
			EffigyViewCube.Draw( _cubeOverlay, new EffigyViewCube.State(
				_camera.WorldRotation, CubeCentre, DpiScale, _cubeHover, _cubeHoverButton, _cubeHovered, Theme.Blue ) );
		}

		_cubeOverlay.Push();
	}
}

/// <summary>
/// The cube's renderer: one scene object on the overlay layer that replays a list of screen-space
/// primitives in order. Lifted from s&amp;box's OrientationGizmoSceneObject, with an icon added for
/// the home button.
/// </summary>
internal sealed class CubeOverlayObject : SceneCustomObject, IOverlayCanvas
{
	private enum PrimitiveKind { Circle, Line, Text, Icon, Polygon, RoundedRect }

	private struct Primitive
	{
		public PrimitiveKind Kind;
		public Vector2 A;
		public Vector2 B;
		public float Size;
		public Color Color;
		public Color Border;
		public float BorderSize;
		public string Text;
		public Rect Rect;
		public Vector2[] Points;
	}

	private List<Primitive> _build = new();
	private List<Primitive> _render = new();
	private readonly RenderAttributes _lineAttributes = new();

	public CubeOverlayObject( SceneWorld world ) : base( world )
	{
		RenderLayer = SceneRenderLayer.OverlayWithoutDepth;
		Bounds = BBox.FromPositionAndSize( 0, float.MaxValue ); // never frustum-cull a screen-space overlay
	}

	public void Begin() => _build.Clear();
	public void Push() => (_render, _build) = (_build, _render);

	public void Circle( Vector2 center, float radius, Color fill, Color border = default, float borderSize = 0f )
		=> _build.Add( new Primitive { Kind = PrimitiveKind.Circle, A = center, Size = radius, Color = fill, Border = border, BorderSize = borderSize } );

	public void RoundedRect( Rect rect, float radius, Color fill, Color border = default, float borderSize = 0f )
		=> _build.Add( new Primitive { Kind = PrimitiveKind.RoundedRect, Rect = rect, Size = radius, Color = fill, Border = border, BorderSize = borderSize } );

	public void Line( Vector2 start, Vector2 end, float thickness, Color color )
		=> _build.Add( new Primitive { Kind = PrimitiveKind.Line, A = start, B = end, Size = thickness, Color = color } );

	public void Text( string text, Vector2 pos, float size, Color color )
		=> _build.Add( new Primitive { Kind = PrimitiveKind.Text, A = pos, Size = size, Color = color, Text = text } );

	/// <summary>A filled convex polygon, drawn as a fan of real triangles — solid to its edges.</summary>
	public void Polygon( Vector2[] points, Color color )
		=> _build.Add( new Primitive { Kind = PrimitiveKind.Polygon, Points = points, Color = color } );

	public void Icon( string icon, Rect rect, float size, Color color )
		=> _build.Add( new Primitive { Kind = PrimitiveKind.Icon, Rect = rect, Size = size, Color = color, Text = icon } );

	public override void RenderSceneObject()
	{
		var primitives = _render;

		foreach ( var p in primitives )
		{
			switch ( p.Kind )
			{
				case PrimitiveKind.Circle:
					var rect = new Rect( p.A.x - p.Size, p.A.y - p.Size, p.Size * 2f, p.Size * 2f );
					var borderWidth = p.BorderSize > 0f ? new Vector4( p.BorderSize ) : default;
					Graphics.DrawRoundedRectangle( rect, p.Color, new Vector4( p.Size ), borderWidth, p.Border );
					break;

				case PrimitiveKind.Line:
					DrawLine( p.A, p.B, p.Size, p.Color );
					break;

				case PrimitiveKind.Text:
					Graphics.DrawText( new Rect( p.A, 0f ), p.Text, p.Color, "Roboto", p.Size, flags: TextFlag.Center );
					break;

				case PrimitiveKind.RoundedRect:
					var edge = p.BorderSize > 0f ? new Vector4( p.BorderSize ) : default;
					Graphics.DrawRoundedRectangle( p.Rect, p.Color, new Vector4( p.Size ), edge, p.Border );
					break;

				case PrimitiveKind.Polygon:
					DrawPolygon( p.Points, p.Color );
					break;

				case PrimitiveKind.Icon:
					Graphics.DrawIcon( p.Rect, p.Text, p.Color, p.Size, TextFlag.Center );
					break;
			}
		}
	}

	private readonly RenderAttributes _polygonAttributes = new();
	private Vertex[] _fan = new Vertex[64];

	/// <summary>
	/// A convex polygon as a triangle fan, through the same UI material and pixel space DrawQuad
	/// uses — so it lands exactly where the lines and text do, with no rows and no seams.
	/// </summary>
	private void DrawPolygon( Vector2[] points, Color color )
	{
		if ( points is null || points.Length < 3 )
			return;

		var count = (points.Length - 2) * 3;

		if ( _fan.Length < count )
			_fan = new Vertex[count];

		var n = 0;

		for ( var i = 1; i + 1 < points.Length; i++ )
		{
			_fan[n++] = new Vertex( new Vector3( points[0].x, points[0].y, 0f ), color );
			_fan[n++] = new Vertex( new Vector3( points[i].x, points[i].y, 0f ), color );
			_fan[n++] = new Vertex( new Vector3( points[i + 1].x, points[i + 1].y, 0f ), color );
		}

		_polygonAttributes.Set( "Texture", Texture.White );
		_polygonAttributes.Set( "TransformMat", Matrix.CreateRotationZ( 0f, Vector3.Zero ) );
		Graphics.Draw( _fan.AsSpan( 0, n ), n, Material.UI.Basic, _polygonAttributes, Graphics.PrimitiveType.Triangles );
	}

	private void DrawLine( Vector2 start, Vector2 end, float thickness, Color color )
	{
		var delta = end - start;
		var length = delta.Length;

		if ( length <= 0f )
			return;

		var angle = MathF.Atan2( delta.y, delta.x ).RadianToDegree();
		var rect = new Rect( start.x, start.y - thickness * 0.5f, length, thickness );

		_lineAttributes.Set( "Texture", Texture.White );
		_lineAttributes.Set( "TransformMat", Matrix.CreateRotationZ( angle, new Vector3( start.x, start.y, 0f ) ) );
		Graphics.DrawQuad( rect, Material.UI.Basic, color, _lineAttributes );
	}
}
