using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The 3D viewport for Effigy — a live view of the PartStudio's output, with Onshape-style
/// reference planes (Top/Front/Right intersecting at the origin), a selectable origin point,
/// a view cube, and orbit camera.
///
/// PLANES ARE DRAWN AS WIREFRAME RECTANGLES using Gizmo.Draw.Line, oriented to s&box's
/// coordinate system (+x forward, +y left, +z up):
///
///   Top   = XY plane at z=0,  horizontal, normal +Z
///   Front = XZ plane at y=0,  vertical facing camera, normal +Y
///   Right = YZ plane at x=0,  vertical to the right, normal +X
///
/// The origin point can be selected by clicking it, then moved with a position gizmo (three
/// colored arrows for X/Y/Z), matching Onshape's interactable origin. Double-click to reset.
/// </summary>
internal sealed partial class EffigyViewport : Widget
{
	private readonly SceneRenderingWidget _canvas;
	private readonly CameraComponent _camera;
	private readonly Gizmo.Instance _gizmoInstance;

	private GameObject _modelObject;
	private ModelRenderer _renderer;

	/// <summary>Half-width of each reference plane rectangle in world units.</summary>
	private const float PlaneSize = 128f;

	/// <summary>How many subdivisions along each edge of the reference planes.</summary>
	private const int PlaneSubdivisions = 8;

	/// <summary>Radius of the origin handle dot in world units.</summary>
	private const float OriginHandleRadius = 4f;

	// --- origin state -----------------------------------------------------------------------

	/// <summary>The origin's current world position. Reference planes and axis lines are drawn
	/// relative to this, so dragging it shifts the whole coordinate frame.</summary>
	public Vector3 OriginPosition { get; private set; } = Vector3.Zero;

	/// <summary>True while the user is dragging the origin gizmo.</summary>
	private bool _draggingOrigin;

	/// <summary>Position the origin was at when a drag started, so the accumulated delta can be
	/// added to the correct base — same pattern as RigViewport's _propDragStart.</summary>
	private Vector3 _originDragStart;

	/// <summary>Accumulated movement since drag began — the Position gizmo at Vector3.Zero
	/// returns a per-frame displacement, so total drag is the sum.</summary>
	private Vector3 _originDragDelta;

	/// <summary>Whether the origin is selected (showing the position gizmo).</summary>
	public bool OriginSelected { get; private set; }

	/// <summary>Raised when the origin is moved, so the parameter panel can update.</summary>
	public Action OriginMoved { get; set; }

	/// <summary>Raised when the loaded model changes, so the status bar can update.</summary>
	public Action<string> ModelInfoChanged { get; set; }

	/// <summary>Raised when the origin selection state changes.</summary>
	public Action<bool> OriginSelectionChanged { get; set; }

	/// <summary>Current model stats for the status bar.</summary>
	public string ModelInfo { get; private set; } = "";

	private Color _backgroundColor = new( 0.82f, 0.84f, 0.86f, 1f );

	/// <summary>
	/// Viewport background, driven by the active palette.
	///
	/// This was an auto-property, and the camera read it exactly once - in this constructor,
	/// before any palette had been applied. So every palette in the View menu changed this field
	/// and nothing else, and all four themes rendered identically. The setter is the whole fix.
	/// </summary>
	public Color BackgroundColor
	{
		get => _backgroundColor;
		set
		{
			_backgroundColor = value;

			if ( _camera.IsValid() )
				_camera.BackgroundColor = value;
		}
	}

	/// <summary>Grid colour for the reference planes, driven by the active palette. The plane
	/// OUTLINES keep their per-axis hues (Top orange, Front blue, Right green) because that is how
	/// you tell them apart; it is the grid infill that has to stay legible against whatever the
	/// background happens to be.</summary>
	public Color PlaneColor { get; set; } = new( 0.55f, 0.58f, 0.61f, 1f );

	public EffigyViewport( Widget parent ) : base( parent )
	{
		MinimumSize = 200;
		Layout = Layout.Column();

		_canvas = new SceneRenderingWidget( this );
		_canvas.OnPreFrame += OnPreFrame;
		_canvas.FocusMode = FocusMode.Click;
		_canvas.Scene = Scene.CreateEditorScene();

		using ( _canvas.Scene.Push() )
		{
			_camera = new GameObject( true, "camera" ).GetOrAddComponent<CameraComponent>( false );
			_camera.BackgroundColor = BackgroundColor;
			_camera.ZNear = 0.5f;
			_camera.ZFar = 8192;
			_camera.FieldOfView = 45f;
			_camera.Enabled = true;

			var sun = new GameObject( true, "sun" ).GetOrAddComponent<DirectionalLight>( false );
			sun.WorldRotation = Rotation.From( 45, 45, 0 );
			sun.LightColor = Color.White;
			sun.Enabled = true;

			var fill = new GameObject( true, "fill" ).GetOrAddComponent<DirectionalLight>( false );
			fill.WorldRotation = Rotation.From( -30, -120, 0 );
			fill.LightColor = new Color( 0.6f, 0.6f, 0.6f, 1f );
			fill.Enabled = true;

			var ambient = new GameObject( true, "ambient" ).GetOrAddComponent<AmbientLight>( false );
			ambient.Color = new Color( 0.6f, 0.6f, 0.6f, 1f );
			ambient.Enabled = true;

			_canvas.Camera = _camera;
		}

		_gizmoInstance = _canvas.GizmoInstance;

		// The canvas is NOT added to the layout here - the tool strip has to go above it and does
		// not exist yet. BuildToolbar calls CompleteLayout to fill this widget's existing column
		// layout in the right order.

		FrameCamera();
	}

	// --- layout helpers ---------------------------------------------------------------------

	/// <summary>The 3D canvas, exposed so the window can parent floating overlays (the tool strip)
	/// onto it rather than into the layout.</summary>
	public Widget Canvas => _canvas;

	/// <summary>
	/// Give the canvas the whole viewport and float <paramref name="overlay"/> on top of it at the
	/// top-left. Called once from BuildToolbar, after the tool strip is built.
	///
	/// The overlay is deliberately NOT a layout row above the canvas. A row takes a band off the
	/// top of the viewport and paints window chrome across it; parenting to the canvas instead
	/// lets the 3D scene fill the widget with the buttons sitting on it, which is what the tool
	/// strip was always described as doing.
	///
	/// Note this fills the layout the constructor already made rather than assigning a fresh one.
	/// It runs after DockManager.SetCentralWidget has sized the viewport, and replacing the layout
	/// at that point orphans the canvas: it keeps whatever tiny geometry it had and renders the
	/// whole 3D scene into a sliver, leaving the rest of the viewport black.
	/// </summary>
	public void CompleteLayout( Widget featureOverlay, Widget sketchOverlay )
	{
		Layout.Add( _canvas, 1 );

		// Both strips sit in the SAME spot and only one is ever visible at a time - entering a
		// sketch is supposed to REPLACE the feature strip with the sketch one, not add a second
		// row alongside it. They used to be two unrelated widget systems (this floating strip and
		// a window-docked ToolBar that showed and hid itself independently), which is exactly why
		// the feature strip stayed on screen through the whole time anyone was sketching - nothing
		// ever told it to get out of the way.
		_overlay = featureOverlay;
		_overlay.Position = OverlayMargin;

		sketchOverlay.Position = OverlayMargin;
		sketchOverlay.Visible = false;
	}

	/// <summary>Inset of the floating tool strip from the canvas's top-left corner.</summary>
	private static readonly Vector2 OverlayMargin = new( 10f, 10f );

	/// <summary>The floating tool strip, so the frame loop can keep camera drags out of it.</summary>
	private Widget _overlay;

	// --- model management -------------------------------------------------------------------

	/// <summary>
	/// Load a compiled .vmdl model into the viewport. Null clears the viewport.
	///
	/// Uses Model.Load on an asset path, same pattern as RigControlWindow's LoadAsset and
	/// EffigyTool's own export path. The ModelRenderer (not SkinnedModelRenderer) is correct
	/// here because Effigy produces static meshes — no bones, no animation.
	/// </summary>
	/// <param name="model">The model to show, or null to clear the viewport.</param>
	/// <param name="frameCamera">Reframe to fit the new model. Off for a live rebuild: the
	/// preview is regenerated on every slider tick, and snapping the camera back mid-drag makes
	/// the part impossible to look at while you adjust it.</param>
	public void SetModel( Model model, bool frameCamera = true )
	{
		using var scope = _canvas.Scene.Push();

		_modelObject?.Destroy();
		_modelObject = null;
		_renderer = null;

		if ( model is null )
		{
			ModelInfo = "";
			ModelInfoChanged?.Invoke( ModelInfo );
			return;
		}

		_modelObject = new GameObject( true, "effigy_model" );
		_renderer = _modelObject.GetOrAddComponent<ModelRenderer>( false );
		_renderer.Model = model;
		_renderer.Enabled = true;

		var meshCount = model.MeshCount;
		var bounds = model.Bounds;
		var size = bounds.Size;
		ModelInfo = $"{model.ResourceName ?? model.Name ?? "model"} \u00b7 {meshCount} mesh{(meshCount != 1 ? "es" : "")} \u00b7 {size.x:F0}\u00d7{size.y:F0}\u00d7{size.z:F0}";
		ModelInfoChanged?.Invoke( ModelInfo );

		if ( frameCamera )
			FrameCamera();
	}

	/// <summary>
	/// Frame whatever is on screen from an isometric-ish front-right-top angle, like a fresh
	/// Onshape document.
	///
	/// It has to FIT the model rather than sit at a fixed distance. Effigy's units are
	/// dimensionless — a default Box is one unit on a side, next to reference planes 128 units
	/// wide — so a fixed 320-unit pullback renders a freshly added primitive as a speck, which
	/// reads as the button having done nothing.
	/// </summary>
	public void FrameCamera()
	{
		var dir = new Vector3( 1f, -1f, 0.65f ).Normal;
		var center = Vector3.Zero;

		// No model: frame the reference planes, which is all there is to look at.
		var radius = PlaneSize * 1.25f;

		if ( _renderer.IsValid() && _renderer.Model is { } model )
		{
			var bounds = model.Bounds;
			center = bounds.Center;

			// Half the diagonal, so the part fits from any angle. Floored because a zero-size
			// body (a degenerate feature) would otherwise put the camera inside it.
			radius = MathF.Max( bounds.Size.Length * 0.5f, 1f );
		}

		// Fit the bounding sphere in the vertical FOV, with a margin so it is not edge to edge.
		var distance = radius / MathF.Tan( _camera.FieldOfView.DegreeToRadian() * 0.5f ) * 1.4f;

		_camera.WorldPosition = center + dir * distance;
		_camera.WorldRotation = Rotation.LookAt( -dir, Vector3.Up );

		// A one-unit part needs to be able to get closer than the 0.5 near plane the planes want.
		_camera.ZNear = Math.Clamp( distance * 0.01f, 0.01f, 8f );
	}

	// --- origin interaction -----------------------------------------------------------------

	/// <summary>Reset the origin back to (0,0,0). Called from double-click or parameter panel.</summary>
	public void ResetOrigin()
	{
		OriginPosition = Vector3.Zero;
		OriginMoved?.Invoke();
	}

	/// <summary>Set origin programmatically (from parameter panel number fields).</summary>
	public void SetOrigin( Vector3 position )
	{
		OriginPosition = position;
		OriginMoved?.Invoke();
	}

	/// <summary>
	/// Draw the origin handle: a colored dot at the origin with a hitbox for selection, and a
	/// position gizmo (three axis arrows) when selected.
	///
	/// Clicking the dot selects the origin, showing the gizmo. Dragging an arrow moves the origin
	/// along that axis. The reference planes follow. Click empty space or press Escape to deselect.
	/// </summary>
	private void DrawOrigin()
	{
		using var scope = Gizmo.Scope( "origin", new Transform( OriginPosition ) );

		// --- when selected: position gizmo first, so its handles take priority over the dot ---
		if ( OriginSelected )
		{
			// Position gizmo: three colored arrows for X/Y/Z, world-aligned.
			// Same pattern as RigViewport's DragReferenceProp — gizmo at Vector3.Zero,
			// accumulate the per-frame displacement, add to the drag-start base position.
			using var ctrlScope = Gizmo.Scope( "origin-control", new Transform( Vector3.Zero ) );

			Gizmo.Hitbox.DepthBias = 0.01f;

			if ( Gizmo.Control.Position( "origin-move", Vector3.Zero, out var displacement, Rotation.Identity ) )
			{
				if ( !_draggingOrigin )
				{
					_draggingOrigin = true;
					_originDragStart = OriginPosition;
					_originDragDelta = Vector3.Zero;
				}

				_originDragDelta += displacement;
				OriginPosition = _originDragStart + _originDragDelta;
				OriginMoved?.Invoke();
			}
			else if ( _draggingOrigin )
			{
				// Drag ended — the position is already final from the last frame's update
				_draggingOrigin = false;
			}

			// Draw the dot larger and brighter when selected
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 1f ); // bright yellow
			Gizmo.Draw.SolidSphere( 0f, OriginHandleRadius * 1.4f, 12, 12 );
			Gizmo.Draw.IgnoreDepth = false;

			return;
		}

		// --- not selected: draw the dot and check for click ---

		// Draw origin dot — Onshape-style small circle
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 0.85f ); // warm yellow
		Gizmo.Draw.SolidSphere( 0f, OriginHandleRadius, 10, 10 );
		Gizmo.Draw.IgnoreDepth = false;

		// Hitbox for selection — slightly larger than the visual dot for easier clicking
		Gizmo.Hitbox.DepthBias = 0.01f;
		Gizmo.Hitbox.Sphere( new Sphere( Vector3.Zero, OriginHandleRadius * 2.5f ) );

		if ( Gizmo.IsHovered )
		{
			// Highlight on hover
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 0.35f );
			Gizmo.Draw.SolidSphere( 0f, OriginHandleRadius * 2.5f, 10, 10 );
			Gizmo.Draw.IgnoreDepth = false;

			if ( Gizmo.WasLeftMousePressed )
			{
				OriginSelected = true;
				OriginSelectionChanged?.Invoke( true );
			}
		}
	}

	/// <summary>Deselect the origin — called from the window when clicking empty space or pressing
	/// Escape.</summary>
	public void DeselectOrigin()
	{
		if ( !OriginSelected )
			return;

		OriginSelected = false;
		OriginSelectionChanged?.Invoke( false );
	}

	// --- reference planes -------------------------------------------------------------------

	/// <summary>
	/// Draws the three Onshape-style reference planes as wireframe rectangles intersecting at
	/// the origin. Each plane gets its own faint color so you can tell them apart at a glance,
	/// matching Onshape's convention:
	///
	///   Top   (XY) — orange tint
	///   Front (XZ) — blue tint
	///   Right (YZ) — green tint
	///
	/// All three are drawn as outlined rectangles with edge subdivisions, like Onshape's
	/// default plane visualization — faint enough not to compete with the model.
	///
	/// Planes follow OriginPosition — they are drawn relative to it, not at the world origin,
	/// so dragging the origin shifts the entire coordinate frame.
	/// </summary>
	private void DrawReferencePlanes()
	{
		var center = OriginPosition;
		var s = PlaneSize;
		var step = (s * 2f) / PlaneSubdivisions;

		Gizmo.Draw.IgnoreDepth = true;

		// Grid infill comes from the palette so it stays readable on a light or a dark background;
		// outlines keep their per-axis hue so the three planes remain tellable apart.
		var grid = PlaneColor.WithAlpha( PlaneColor.a * 0.5f );

		// --- Top plane (XY at z=0) — orange edge ---
		DrawPlaneOutline( center, Vector3.Forward, Vector3.Left, s, new Color( 0.85f, 0.55f, 0.25f, 0.55f ) );
		DrawPlaneGrid( center, Vector3.Forward, Vector3.Left, s, step, grid );

		// --- Front plane (XZ at y=0) — blue edge ---
		DrawPlaneOutline( center, Vector3.Forward, Vector3.Up, s, new Color( 0.25f, 0.5f, 0.85f, 0.55f ) );
		DrawPlaneGrid( center, Vector3.Forward, Vector3.Up, s, step, grid );

		// --- Right plane (YZ at x=0) — green edge ---
		DrawPlaneOutline( center, Vector3.Left, Vector3.Up, s, new Color( 0.25f, 0.78f, 0.45f, 0.55f ) );
		DrawPlaneGrid( center, Vector3.Left, Vector3.Up, s, step, grid );

		// An offset sketch lives on a parallel plane, not on the origin reference plane. Keep the
		// normal reference planes visible, but draw the active sketch plane where the sketch math
		// actually places its geometry so the user never has to infer why it appears to float.
		if ( IsSketching && ActiveSketch?.Plane is { } sketchPlane )
		{
			var sketchCenter = center + new Vector3( sketchPlane.Origin.x, sketchPlane.Origin.y, sketchPlane.Origin.z );
			var sketchX = new Vector3( sketchPlane.XAxis.x, sketchPlane.XAxis.y, sketchPlane.XAxis.z );
			var sketchY = new Vector3( sketchPlane.YAxis.x, sketchPlane.YAxis.y, sketchPlane.YAxis.z );
			var sketchColor = new Color( 0.95f, 0.82f, 0.25f, 0.65f );

			DrawPlaneOutline( sketchCenter, sketchX, sketchY, s, sketchColor );
			DrawPlaneGrid( sketchCenter, sketchX, sketchY, s, step, sketchColor.WithAlpha( 0.3f ) );
		}

		// --- Origin axes (colored lines) ---
		var axisLen = s * 0.35f;
		Gizmo.Draw.LineThickness = 2f;

		// X axis — red (forward)
		Gizmo.Draw.Color = new Color( 0.9f, 0.25f, 0.25f, 0.7f );
		Gizmo.Draw.Line( center, center + Vector3.Forward * axisLen );

		// Y axis — green (left)
		Gizmo.Draw.Color = new Color( 0.25f, 0.8f, 0.35f, 0.7f );
		Gizmo.Draw.Line( center, center + Vector3.Left * axisLen );

		// Z axis — blue (up)
		Gizmo.Draw.Color = new Color( 0.3f, 0.45f, 0.9f, 0.7f );
		Gizmo.Draw.Line( center, center + Vector3.Up * axisLen );

		// Axis labels at the ends — using WorldText for 3D placement
		Gizmo.Draw.Color = new Color( 0.9f, 0.25f, 0.25f, 0.8f );
		Gizmo.Draw.WorldText( "X", new Transform( center + Vector3.Forward * (axisLen + 8f) ), "Roboto", 10f, TextFlag.Center );

		Gizmo.Draw.Color = new Color( 0.25f, 0.8f, 0.35f, 0.8f );
		Gizmo.Draw.WorldText( "Y", new Transform( center + Vector3.Left * (axisLen + 8f) ), "Roboto", 10f, TextFlag.Center );

		Gizmo.Draw.Color = new Color( 0.3f, 0.45f, 0.9f, 0.8f );
		Gizmo.Draw.WorldText( "Z", new Transform( center + Vector3.Up * (axisLen + 8f) ), "Roboto", 10f, TextFlag.Center );

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;

		DrawPlaneHitboxes();
		DrawHoveredPlaneHighlight();
	}

	/// <summary>Draw the four edges of a plane rectangle as a wireframe outline.</summary>
	private static void DrawPlaneOutline( Vector3 center, Vector3 right, Vector3 up, float halfSize, Color color )
	{
		Gizmo.Draw.Color = color;

		var a = center + right * halfSize + up * halfSize;
		var b = center - right * halfSize + up * halfSize;
		var c = center - right * halfSize - up * halfSize;
		var d = center + right * halfSize - up * halfSize;

		Gizmo.Draw.Line( a, b );
		Gizmo.Draw.Line( b, c );
		Gizmo.Draw.Line( c, d );
		Gizmo.Draw.Line( d, a );
	}

	/// <summary>Draw subdivision lines across a plane rectangle, like Onshape's faint grid on
	/// reference planes.</summary>
	private static void DrawPlaneGrid( Vector3 center, Vector3 right, Vector3 up,
		float halfSize, float step, Color color )
	{
		Gizmo.Draw.Color = color;

		var half = halfSize;
		var count = (int)(half * 2f / step);

		for ( var i = 0; i <= count; i++ )
		{
			var offset = -half + i * step;
			var start = center + up * offset - right * half;
			var end = center + up * offset + right * half;
			Gizmo.Draw.Line( start, end );
		}

		for ( var i = 0; i <= count; i++ )
		{
			var offset = -half + i * step;
			var start = center + right * offset - up * half;
			var end = center + right * offset + up * half;
			Gizmo.Draw.Line( start, end );
		}
	}

	// --- view cube (top-right orientation indicator) ----------------------------------------

	/// <summary>
	/// A small orientation label in the viewport's top-right corner, like Onshape's view cube.
	/// Shows the current camera direction as text (FRONT / BACK / LEFT / RIGHT / TOP / BOTTOM).
	/// </summary>
	private void DrawViewCube()
	{
		var forward = -_camera.WorldRotation.Forward;
		var absX = MathF.Abs( forward.x );
		var absY = MathF.Abs( forward.y );
		var absZ = MathF.Abs( forward.z );

		string label;

		if ( absZ > absX && absZ > absY )
			label = forward.z > 0 ? "TOP" : "BOTTOM";
		else if ( absX > absY )
			label = forward.x > 0 ? "FRONT" : "BACK";
		else
			label = forward.y > 0 ? "LEFT" : "RIGHT";

		// Set the colour explicitly. Gizmo.Draw.Color is sticky, so without this the label came out
		// in whatever the last thing drawn happened to leave behind - the Z axis blue on one frame,
		// the origin handle's yellow on the next.
		Gizmo.Draw.Color = PlaneColor.WithAlpha( 0.9f );

		Gizmo.Draw.ScreenText( label, new Vector2( _canvas.Size.x - 52f, 18f ),
			"Roboto", 11f, TextFlag.Center );
	}

	// --- standard views ----------------------------------------------------------------------

	/// <summary>The orientations Onshape's view cube offers when you click it.</summary>
	public enum StandardView
	{
		Top,
		Bottom,
		Front,
		Back,
		Left,
		Right,
		Isometric,
	}

	/// <summary>
	/// Point the camera down a named axis, keeping whatever the current framing distance is.
	///
	/// s&box is +x forward, +y left, +z up, so "Front" looks along -x at the XZ plane and "Right"
	/// looks along +y at the YZ plane — matching how DrawReferencePlanes names the same three.
	/// </summary>
	public void SetStandardView( StandardView view )
	{
		var dir = view switch
		{
			StandardView.Top => new Vector3( 0f, 0f, 1f ),
			StandardView.Bottom => new Vector3( 0f, 0f, -1f ),
			StandardView.Front => new Vector3( 1f, 0f, 0f ),
			StandardView.Back => new Vector3( -1f, 0f, 0f ),
			StandardView.Left => new Vector3( 0f, 1f, 0f ),
			StandardView.Right => new Vector3( 0f, -1f, 0f ),
			_ => new Vector3( 1f, -1f, 0.65f ).Normal,
		};

		// Looking straight down needs an up vector that is not also straight down.
		var up = MathF.Abs( dir.z ) > 0.99f ? Vector3.Forward : Vector3.Up;

		PointCameraAt( CurrentFocus(), dir, up );
	}

	/// <summary>
	/// Look square at the active sketch plane — Onshape's N.
	///
	/// This is bound to a key rather than fired on sketch entry on purpose. Onshape does NOT
	/// rotate the view when you pick a plane (automating it is a standing request on their forum,
	/// not shipped behaviour), and taking the camera away from someone who deliberately set up a
	/// three-quarter view to sketch against existing geometry is worse than one keypress.
	/// </summary>
	public void ViewNormalToSketchPlane()
	{
		if ( ActiveSketch?.Plane is not { } plane )
			return;

		var normal = ToWorldDir( plane.Normal );
		var up = ToWorldDir( plane.YAxis );
		var centre = OriginPosition + ToWorldDir( plane.Origin );

		// Second press flips to the far side, the way Onshape's N does.
		if ( Vector3.Dot( _camera.WorldPosition - centre, normal ) > 0f )
			normal = -normal;

		PointCameraAt( centre, normal, up );
	}

	/// <summary>What the camera is currently looking at, so a view change rotates around the part
	/// rather than throwing it off screen.</summary>
	private Vector3 CurrentFocus()
	{
		if ( _renderer.IsValid() && _renderer.Model is { } model )
			return model.Bounds.Center;

		return Vector3.Zero;
	}

	private void PointCameraAt( Vector3 focus, Vector3 direction, Vector3 up )
	{
		var distance = (_camera.WorldPosition - focus).Length;

		// A camera sitting exactly on the focus has no distance to preserve, which happens before
		// anything has been framed. Fall back to the reference planes' own scale.
		if ( distance < 1f )
			distance = PlaneSize * 1.25f;

		_camera.WorldPosition = focus + direction.Normal * distance;
		_camera.WorldRotation = Rotation.LookAt( -direction.Normal, up );
	}

	// --- per-frame tick ---------------------------------------------------------------------

	/// <summary>Whether the cursor is over the 3D canvas and not driving the camera. Read by the
	/// sketch pass, which has no hitbox of its own to hover.</summary>
	private bool _canvasHasCursor;

	private void OnPreFrame()
	{
		if ( _canvas.Scene is { } scene )
			scene.EditorTick( RealTime.Now, RealTime.Delta );

		// The floating tool strip sits inside the canvas, so "cursor over the canvas" is true while
		// you are aiming at a button. Without excluding it, pressing a tool also grabs the orbit
		// camera and the click drags the view.
		var overCanvas = _canvas.IsUnderMouse && !(_overlay?.IsUnderMouse ?? false);

		_gizmoInstance.Input.IsHovered = IsActiveWindow && overCanvas;

		if ( _gizmoInstance.FirstPersonCamera( _camera, _canvas ) )
			_gizmoInstance.Input.IsHovered = false;

		// After FirstPersonCamera has had its say this means "the cursor is over the canvas and we
		// are not flying the camera" - which is exactly the condition a sketch click needs. Without
		// it, left-dragging to orbit scatters points across the plane.
		_canvasHasCursor = _gizmoInstance.Input.IsHovered;

		_canvas.UpdateGizmoInputs( _gizmoInstance.Input.IsHovered );

		// Draw planes first (behind everything else)
		DrawReferencePlanes();
		DrawCommittedSketches();
		SketchPickFrame();
		FacePickFrame();

		SketchFrame();

		// Origin on top of the planes. Hidden while sketching or picking anything - it sits at the
		// exact spot most first clicks land, and stealing them was the first thing that broke.
		if ( !IsSketching && !PlanePickMode && !SketchPickMode )
		{
			DrawOrigin();

			// Click empty space to deselect origin
			if ( Gizmo.WasLeftMousePressed && !Gizmo.IsHovered && OriginSelected )
				DeselectOrigin();
		}

		DrawViewCube();

		Cursor = Gizmo.HasHovered || IsSketching || _hoveredSketchId is not null || _hoveredFaceBodyId is not null
			? CursorShape.Finger : CursorShape.Arrow;
	}

	/// <summary>Escape backs out of the half-drawn entity, then out of the tool - the same two
	/// stages every CAD sketcher uses.</summary>
	protected override void OnKeyPress( KeyEvent e )
	{
		if ( e.Key != KeyCode.Escape )
		{
			base.OnKeyPress( e );
			return;
		}

		if ( IsSketching )
		{
			CancelSketchTool();
			e.Accepted = true;
			return;
		}

		// Escape stands down an armed selection box. The viewport owns the key press; the dialog
		// owns the boxes' painted state, so it is told through PickModeCancelled. Sketch picking
		// itself stays live while a consumer dialog is open — the dialog turns it off.
		if ( PlanePickMode || SketchPickMode )
		{
			PlanePickMode = false;
			PickModeCancelled?.Invoke();
			e.Accepted = true;
			return;
		}

		if ( OriginSelected )
		{
			DeselectOrigin();
			e.Accepted = true;
		}
	}
}
