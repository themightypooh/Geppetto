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
/// and a fly camera.
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

	/// <summary>Half-width a reference plane starts at, in world units. Each plane can be dragged
	/// to its own size from there — see <see cref="_planeHalfSize"/>.</summary>
	private const float PlaneSize = 128f;

	/// <summary>
	/// Half-width of each reference plane — Top, Front, Right — in world units.
	///
	/// PER PLANE rather than the single shared constant this used to be. A plane is a drawing
	/// surface, and the one you are about to sketch on wants to be big enough to work on while the
	/// other two want to be out of the way. One number could not do both.
	/// </summary>
	private readonly float[] _planeHalfSize = { PlaneSize, PlaneSize, PlaneSize };

	/// <summary>How small a plane may be dragged. Below this the corner handles land on top of
	/// each other and there is no way to grab one to make it big again.</summary>
	private const float MinPlaneHalfSize = 8f;

	/// <summary>
	/// Radius of the origin handle dot, in SCREEN PIXELS.
	///
	/// It was four WORLD units, which is a different object at every scale: a boulder sitting in
	/// the middle of a thirty-unit part, and invisible on a thousand-unit one. Onshape's origin is
	/// a few pixels across at any zoom and that is what makes it a marker rather than geometry.
	/// Same reasoning, and the same conversion, as the sketch snapping tolerances.
	/// </summary>
	private const float OriginHandlePixels = 2.5f;

	/// <summary>The dot's radius in world units at its current distance from the camera.</summary>
	private float OriginHandleRadius() => WorldRadiusAt( OriginPosition, OriginHandlePixels );

	/// <summary>What a screen-pixel radius is worth in world units at some point in the scene. The
	/// origin dot wanted this first; the plane corner handles want it at twelve more places, each
	/// at its own distance from the camera.</summary>
	private float WorldRadiusAt( Vector3 point, float pixels )
	{
		var distance = MathF.Max( (point - _camera.WorldPosition).Length, 0.01f );
		var halfHeight = MathF.Tan( _camera.FieldOfView.DegreeToRadian() * 0.5f ) * distance;

		return halfHeight / MathF.Max( _canvas.Size.y * 0.5f, 1f ) * pixels;
	}

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

	// --- bone selection / drag state --------------------------------------------------------

	/// <summary>Index of the currently selected bone in the rig skeleton, or -1 for none.</summary>
	private int _selectedBoneIndex = -1;

	/// <summary>What dragging the selected bone does. Rotate is the default because skeletal
	/// animation rotates joints. Move translates, Scale adjusts bone length.</summary>
	public enum BoneDragMode { Rotate, Move, Scale }

	/// <summary>Current drag mode — E flips to the other mode while held.</summary>
	private BoneDragMode _boneDragMode = BoneDragMode.Rotate;

	/// <summary>True while a drag is in progress (mouse down and control reporting).</summary>
	private bool _boneDragging;

	/// <summary>The bone's world pose when the drag started — position, rotation, and length
	/// are all captured here so live values are never fed back into themselves.</summary>
	private Vector3 _dragStartPos;
	private Rotation _dragStartRot;
	private float _dragStartLength;

	/// <summary>Accumulated position delta since drag began (for Move mode).</summary>
	private Vector3 _moveDelta;

	/// <summary>Raised when the selected bone changes from viewport interaction, so the rig
	/// panel can sync its tree selection. The int is the bone index, or -1 for deselected.</summary>
	public Action<int> BoneSelectionChanged { get; set; }

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

	/// <summary>
	/// Chrome colour drawn over the viewport, driven by the active palette so it stays legible
	/// against whatever the background happens to be.
	///
	/// This used to be the reference planes' grid colour, which is where the name comes from. The
	/// planes are outlines only now and their outlines keep their per-axis hues — Top orange, Front
	/// blue, Right green — because that is how you tell them apart. What is left on this is the
	/// faded interior grid.
	/// </summary>
	public Color PlaneColor { get; set; } = new( 0.55f, 0.58f, 0.61f, 1f );
	public bool OriginVisible { get; set; } = true;
	public bool TopPlaneVisible { get; set; } = true;
	public bool FrontPlaneVisible { get; set; } = true;
	public bool RightPlaneVisible { get; set; } = true;
	public Effigy.Skeleton RigSkeleton { get; set; }

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
	public void CompleteLayout( Widget featureOverlay, Widget sketchOverlay, Widget resultOverlay = null )
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

		_sketchOverlay = sketchOverlay;
		sketchOverlay.Position = OverlayMargin;
		sketchOverlay.Visible = false;

		// A SECOND ROW, not a third thing sharing the first spot. The two strips above swap with
		// each other because only one can be relevant at a time; this one is about the feature
		// being edited and is orthogonal to both, so it sits under whichever is showing.
		if ( resultOverlay is not null )
		{
			_resultOverlay = resultOverlay;
			resultOverlay.Position = OverlayMargin + new Vector2( 0f, EffigyToolStrip.ButtonSize + 8f );
		}
	}

	/// <summary>Inset of the floating tool strip from the canvas's top-left corner.</summary>
	private static readonly Vector2 OverlayMargin = new( 10f, 10f );

	/// <summary>The floating tool strip, so the frame loop can keep camera drags out of it.</summary>
	private Widget _overlay;
	private Widget _sketchOverlay;
	private Widget _resultOverlay;

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
		// Say "units" outright and keep the fractions. This is the only place the part's real
		// size is stated, so it is what settles an argument with whatever the surface happens
		// to look like it is.
		ModelInfo = $"{meshCount} mesh{(meshCount != 1 ? "es" : "")} · "
			+ $"{size.x:0.##} × {size.y:0.##} × {size.z:0.##} units";
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
		if ( !OriginVisible )
			return;

		var radius = OriginHandleRadius();

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
			Gizmo.Draw.SolidSphere( 0f, radius * 1.4f, 12, 12 );
			Gizmo.Draw.IgnoreDepth = false;

			return;
		}

		// --- not selected: draw the dot and check for click ---

		// Draw origin dot — Onshape-style small circle
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 0.85f ); // warm yellow
		Gizmo.Draw.SolidSphere( 0f, radius, 10, 10 );
		Gizmo.Draw.IgnoreDepth = false;

		// Hitbox for selection — slightly larger than the visual dot for easier clicking
		Gizmo.Hitbox.DepthBias = 0.01f;
		Gizmo.Hitbox.Sphere( new Sphere( Vector3.Zero, radius * 2.8f ) );

		if ( Gizmo.IsHovered )
		{
			// Highlight on hover
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 0.35f );
			Gizmo.Draw.SolidSphere( 0f, radius * 2.8f, 10, 10 );
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
		// Before anything is drawn, so a plane being dragged this frame is drawn at the size the
		// cursor is asking for rather than one frame behind it.
		UpdatePlaneResize();

		var center = OriginPosition;
		var s = PlaneSize;

		// DEPTH-TESTED. The reference planes are 128 units across and were drawn straight through
		// whatever the part is, so a finished solid had a grid laid over it and read as a glass
		// box rather than as material. A plane behind the part now goes behind the part.
		Gizmo.Draw.IgnoreDepth = false;

		// OUTLINES BY DEFAULT, GRID ON REQUEST. Each plane used to be filled with an 8x8 lattice
		// unconditionally, and three of those overlapping at the origin was most of what you saw on
		// opening the editor: the part sat inside a wire cage. The outline alone says where a plane
		// is and how big it is, which is all it has to say most of the time — but the lattice is a
		// ruler when you want one, so it is a setting rather than a deletion. Edit > Settings.
		var grid = PlaneColor.WithAlpha( PlaneColor.a * 0.5f );

		for ( var index = 0; index < 3; index++ )
		{
			if ( !PlaneVisible( index ) )
				continue;

			var (right, up, colour) = PlaneAxes( index );
			var half = _planeHalfSize[index];

			DrawPlaneOutline( center, right, up, half, colour );

			if ( !ShowPlaneGrid )
				continue;

			// A plane seen edge-on is a line, and its grid collapses into that line as a bright
			// smear across everything behind it. Three planes meet at right angles, so from any
			// camera angle at least one of them is close to edge-on and it was always the one
			// making the middle of the view unreadable. Fading it out by how square-on it is means
			// you only ever see the grids you are actually looking at.
			var facing = MathF.Abs( Vector3.Dot( Vector3.Cross( right, up ), _camera.WorldRotation.Forward ) );
			var viewFade = MathF.Min( facing / EdgeOnFade, 1f );

			if ( viewFade <= 0.01f )
				continue;

			DrawPlaneGrid( center, right, up, half, DrawnGridStep( center, half ),
				grid.WithAlpha( grid.a * viewFade ) );
		}

		// An offset sketch lives on a parallel plane, not on the origin reference plane. Keep the
		// normal reference planes visible, but draw the active sketch plane where the sketch math
		// actually places its geometry so the user never has to infer why it appears to float.
		if ( IsSketching && ActiveSketch?.Plane is { } sketchPlane )
		{
			// The one plane that still draws through everything: you are working on it, and a
			// sketch plane you cannot see because a body is in front of it is not usable.
			Gizmo.Draw.IgnoreDepth = true;

			var sketchCenter = center + new Vector3( sketchPlane.Origin.x, sketchPlane.Origin.y, sketchPlane.Origin.z );
			var sketchX = new Vector3( sketchPlane.XAxis.x, sketchPlane.XAxis.y, sketchPlane.XAxis.z );
			var sketchY = new Vector3( sketchPlane.YAxis.x, sketchPlane.YAxis.y, sketchPlane.YAxis.z );
			var sketchColor = new Color( 0.95f, 0.82f, 0.25f, 0.65f );

			DrawPlaneOutline( sketchCenter, sketchX, sketchY, s, sketchColor );

			if ( ShowPlaneGrid )
			{
				DrawPlaneGrid( sketchCenter, sketchX, sketchY, s, DrawnGridStep( sketchCenter, s ),
					sketchColor.WithAlpha( 0.3f ) );
			}

			Gizmo.Draw.IgnoreDepth = false;
		}

		DrawPlaneCornerHandles();

		if ( !OriginVisible )
		{
			Gizmo.Draw.IgnoreDepth = false;
			DrawPlaneHitboxes();
			DrawHoveredPlaneHighlight();
			return;
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

	/// <summary>
	/// The most grid lines a plane may draw across itself in one direction.
	///
	/// A CAP, NOT A DENSITY. Spacing is now a real distance in units rather than a count of
	/// subdivisions, which means a fine grid on a plane dragged out to a thousand units asks for
	/// tens of thousands of lines and takes the frame rate with it. Past this the step is widened
	/// until it fits, so a grid that would be an unreadable smear is drawn coarse instead.
	/// </summary>
	private const int MaxGridLines = 160;

	/// <summary>How square-on a plane has to be before its grid is at full strength — the cosine of
	/// the angle between its normal and the view. Below this it fades out proportionally, reaching
	/// nothing when exactly edge-on. 0.35 is about twenty degrees of tilt.</summary>
	private const float EdgeOnFade = 0.35f;

	/// <summary>
	/// The step to draw a plane's lattice at — the same one the cursor snaps to, so the lines mean
	/// something, widened if that would put more than <see cref="MaxGridLines"/> across the plane.
	///
	/// On Automatic the step comes from the camera: WorldRadiusAt with a one-pixel radius IS the
	/// units-per-pixel at that point, which is exactly what AutoGridStep wants. That is why the
	/// reference planes can have an adaptive grid outside a sketch, where there is no sketch plane
	/// to measure against.
	/// </summary>
	private float DrawnGridStep( Vector3 center, float halfSize )
	{
		var step = GridStep( WorldRadiusAt( center, 1f ) );

		if ( step <= 0f )
			step = halfSize * 0.25f;

		return MathF.Max( step, halfSize * 2f / MaxGridLines );
	}

	/// <summary>
	/// Whether planes draw a grid inside their outline — the three reference planes AND the active
	/// sketch plane, together.
	///
	/// ONE SWITCH FOR ALL FOUR. It governed only the sketch plane at first, which made it look
	/// broken: the sketch plane is drawn only while a sketch is open, so flipping the setting
	/// anywhere else changed nothing on screen and there was no way to tell that from a dead
	/// control.
	///
	/// Snapping is unaffected either way — SketchSnapper rounds to a step it works out for itself
	/// and never consults this — so turning the grid off means drawing against an invisible ruler.
	/// </summary>
	public bool ShowPlaneGrid { get; set; }

	/// <summary>
	/// Draw a plane's lattice, stepping OUT FROM THE CENTRE rather than in from one edge.
	///
	/// That is not cosmetic. Starting at -halfSize put the lines at whatever the plane's width
	/// happened to leave over, so with a 1-unit spacing on a 128.5-unit plane none of them landed on
	/// a whole number — the grid was half a unit off the coordinates the cursor was snapping to.
	/// Walking out from zero puts every line on an exact multiple of the step, which is what makes
	/// it the same grid the snap uses.
	///
	/// Two things keep three overlapping planes from reading as a wire cage. The centre lines are
	/// skipped, because those are the origin axes and they are already drawn in their own colours —
	/// three planes meeting at the origin were putting six coincident grey lines over three
	/// coloured ones. And the lines fade as they get further out, so the lattice thins toward the
	/// edge instead of ending in a hard grid to the last row.
	/// </summary>
	private static void DrawPlaneGrid( Vector3 center, Vector3 right, Vector3 up,
		float halfSize, float step, Color color )
	{
		if ( step <= 0f || halfSize <= 0f )
			return;

		var count = (int)(halfSize / step);

		for ( var i = 1; i <= count; i++ )
		{
			var offset = i * step;

			// Quadratic rather than linear: a linear ramp still reads as a solid sheet most of the
			// way out and then stops. This is near full weight around the origin, where the work
			// happens, and a quarter of it at the rim.
			var t = offset / halfSize;
			var faded = color.WithAlpha( color.a * (1f - 0.75f * t * t) );

			Gizmo.Draw.Color = faded;

			foreach ( var sign in Signs )
			{
				var d = offset * sign;

				Gizmo.Draw.Line( center + up * d - right * halfSize, center + up * d + right * halfSize );
				Gizmo.Draw.Line( center + right * d - up * halfSize, center + right * d + up * halfSize );
			}
		}
	}

	/// <summary>Both sides of the centre line, walked together so each pair shares one alpha.
	/// </summary>
	private static readonly float[] Signs = { 1f, -1f };

	/// <summary>Whether a plane index — 0 Top, 1 Front, 2 Right — is currently shown.</summary>
	private bool PlaneVisible( int index ) => index switch
	{
		0 => TopPlaneVisible,
		1 => FrontPlaneVisible,
		_ => RightPlaneVisible,
	};

	/// <summary>
	/// The two in-plane axes and the edge colour for a plane index, in Onshape's convention:
	/// Top (XY) orange, Front (XZ) blue, Right (YZ) green.
	///
	/// One definition rather than the three switch statements the outline, the hover wash and the
	/// corner handles each used to carry. They disagreed once already — the hover wash is the
	/// reason DrawPlaneHitboxes lives beside the wireframe rather than apart from it.
	/// </summary>
	private static (Vector3 Right, Vector3 Up, Color Colour) PlaneAxes( int index ) => index switch
	{
		0 => (Vector3.Forward, Vector3.Left, new Color( 0.85f, 0.55f, 0.25f, 0.55f )),
		1 => (Vector3.Forward, Vector3.Up, new Color( 0.25f, 0.5f, 0.85f, 0.55f )),
		_ => (Vector3.Left, Vector3.Up, new Color( 0.25f, 0.78f, 0.45f, 0.55f )),
	};

	// --- resizing a plane by its corners --------------------------------------------------------

	/// <summary>Radius of a plane's corner handle in SCREEN PIXELS, for the same reason the origin
	/// dot is measured that way: a world-unit handle is a boulder on a small part and invisible on
	/// a large one.</summary>
	private const float PlaneCornerPixels = 5f;

	/// <summary>Which plane is being resized right now, or -1. Held across frames because a drag
	/// is a gesture, not an event — the cursor leaves the handle the moment it starts moving.
	/// </summary>
	private int _resizingPlane = -1;

	/// <summary>
	/// A grab handle at each corner of each plane, shown only when the cursor is on it.
	///
	/// HOVER-ONLY because twelve permanent dots around the origin is the clutter the grid was just
	/// taken out for. The hitbox is always registered — it has to be, or there would be nothing to
	/// hover — but nothing is drawn until the cursor finds it, and then the corner being dragged
	/// stays lit for as long as the drag lasts.
	///
	/// Not while sketching or while a plane is armed for picking: in both of those a click on a
	/// plane already means something, and a handle sitting on the corner would eat it.
	/// </summary>
	private void DrawPlaneCornerHandles()
	{
		if ( IsSketching || PlanePickMode )
			return;

		var center = OriginPosition;

		for ( var index = 0; index < 3; index++ )
		{
			if ( !PlaneVisible( index ) )
				continue;

			var (right, up, colour) = PlaneAxes( index );
			var half = _planeHalfSize[index];

			for ( var corner = 0; corner < 4; corner++ )
			{
				// 0 (+,+), 1 (-,+), 2 (-,-), 3 (+,-) — the same walk around the rectangle
				// DrawPlaneOutline makes, so a handle always sits on a drawn corner.
				var x = corner is 0 or 3 ? half : -half;
				var y = corner is 0 or 1 ? half : -half;

				var position = center + right * x + up * y;
				var radius = WorldRadiusAt( position, PlaneCornerPixels );

				using var scope = Gizmo.Scope( $"plane-corner-{index}-{corner}", new Transform( position ) );

				Gizmo.Hitbox.DepthBias = 0.01f;
				Gizmo.Hitbox.Sphere( new Sphere( Vector3.Zero, radius * 2f ) );

				var dragging = _resizingPlane == index;

				if ( !Gizmo.IsHovered && !dragging )
					continue;

				// Through everything, including the part. A handle you cannot see because the solid
				// you are building sits in front of it is a handle you cannot grab.
				Gizmo.Draw.IgnoreDepth = true;
				Gizmo.Draw.Color = colour.WithAlpha( dragging ? 0.9f : 0.5f );
				Gizmo.Draw.SolidSphere( 0f, radius, 10, 10 );
				Gizmo.Draw.IgnoreDepth = false;

				if ( Gizmo.IsHovered && Gizmo.WasLeftMousePressed )
					_resizingPlane = index;
			}
		}
	}

	/// <summary>
	/// Carry a corner drag, sizing the plane to wherever the cursor is on it.
	///
	/// The cursor is put back on the plane rather than tracked in screen space, so the corner stays
	/// under the pointer at any camera angle — the same ray-into-plane projection sketching uses to
	/// place a point (CursorToPlane). The new half-size is the LARGER of the two in-plane distances,
	/// which keeps the plane square the way it has always been drawn; the corner therefore tracks
	/// the cursor exactly along a diagonal and approximately elsewhere, which is what a square
	/// constraint costs.
	/// </summary>
	private void UpdatePlaneResize()
	{
		if ( _resizingPlane < 0 )
			return;

		// Released anywhere, over the handle or not. A drag that only ended when the cursor
		// happened to be back on the corner would never end.
		if ( !Gizmo.IsLeftMouseDown )
		{
			_resizingPlane = -1;
			return;
		}

		var (right, up, _) = PlaneAxes( _resizingPlane );
		var normal = Vector3.Cross( right, up );

		var ray = Gizmo.CurrentRay;
		var denom = Vector3.Dot( ray.Forward, normal );

		// Edge-on: the plane is a line from here and there is no meaningful hit. Hold the size it
		// had rather than snapping it to something arbitrary.
		if ( MathF.Abs( denom ) < 1e-5f )
			return;

		var t = Vector3.Dot( OriginPosition - ray.Position, normal ) / denom;

		if ( t <= 0f )
			return;

		var offset = ray.Position + ray.Forward * t - OriginPosition;

		var half = MathF.Max( MathF.Abs( Vector3.Dot( offset, right ) ), MathF.Abs( Vector3.Dot( offset, up ) ) );

		_planeHalfSize[_resizingPlane] = MathF.Max( half, MinPlaneHalfSize );
	}

	// --- standard views ----------------------------------------------------------------------

	/// <summary>Named camera poses, reachable from the View menu. A fly camera does not need a
	/// corner cube to stay oriented, but snapping to a plane is still useful.</summary>
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

		// The floating tool strips sit inside the canvas, so "cursor over the canvas" is true while
		// you are aiming at a button. Without excluding them, pressing a tool also grabs the orbit
		// camera, the click drags the view, and sketch tools place points on the plane.
		var overAnyOverlay = (_overlay?.IsUnderMouse ?? false)
			|| (_sketchOverlay?.IsUnderMouse ?? false)
			|| (_resultOverlay?.IsUnderMouse ?? false);
		var overCanvas = _canvas.IsUnderMouse && !overAnyOverlay;

		_gizmoInstance.Input.IsHovered = IsActiveWindow && overCanvas;

		var flying = _gizmoInstance.FirstPersonCamera( _camera, _canvas );

		if ( flying )
			_gizmoInstance.Input.IsHovered = false;

		// Whether this right-press has actually moved the view yet — see EffigyViewport.FaceMenu.cs,
		// which uses it to tell a right-click apart from the end of an orbit.
		NoteCameraFlight( flying );

		// After FirstPersonCamera has had its say this means "the cursor is over the canvas and we
		// are not flying the camera" - which is exactly the condition a sketch click needs. Without
		// it, left-dragging to orbit scatters points across the plane.
		_canvasHasCursor = _gizmoInstance.Input.IsHovered;

		_canvas.UpdateGizmoInputs( _gizmoInstance.Input.IsHovered );

		// Held for the right-click menu, which has no frame of its own to build a ray in.
		CaptureCursorRay();

		// BEFORE the planes, not with the rest of the picking below. The planes decide whether to
		// take this click by comparing against the face under the cursor, so the face has to be
		// known by the time they ask — see ResolveFacePick.
		ResolveFacePick();

		// Draw planes first (behind everything else)
		DrawReferencePlanes();
		DrawCommittedSketches();
		ShadeMaterialSlotsFrame();
		SketchPickFrame();
		FacePickFrame();
		BodyPickFrame();
		DrawRigSkeleton();
		BoneToolFrame();

		SketchFrame();

		// Origin on top of the planes. Hidden while sketching or picking anything - it sits at the
		// exact spot most first clicks land, and stealing them was the first thing that broke.
		if ( !IsSketching && !PlanePickMode && !SketchPickMode && !BodyPickMode && !BoneToolActive )
		{
			DrawOrigin();

			// Click empty space to deselect origin
			if ( Gizmo.WasLeftMousePressed && !Gizmo.IsHovered && OriginSelected )
				DeselectOrigin();
		}

		// BoneToolActive and BodyPickMode: the same "you can click here" signal every other live
		// pick mode already gets from Gizmo.HasHovered/_hoveredSketchId/_hoveredFaceBodyId. Without
		// it, placing a bone or assigning a body was the only click-to-act mode in the whole tool
		// that left the cursor a plain arrow the entire time.
		Cursor = Gizmo.HasHovered || IsSketching || _hoveredSketchId is not null || _hoveredFaceBodyId is not null
			|| BoneToolActive || BodyPickMode
			? CursorShape.Finger : CursorShape.Arrow;
	}

	/// <summary>Draw all bones as dog-bone shapes with selection and pose gizmo.</summary>
	private void DrawRigSkeleton()
	{
		if ( RigSkeleton is null || RigSkeleton.Count == 0 )
			return;

		// The selected bone's gizmo needs IgnoreDepth=false to match normal editor gizmos.
		Gizmo.Draw.IgnoreDepth = true;

		for ( var i = 0; i < RigSkeleton.Count; i++ )
			DrawBoneHandle( i );

		// The selected bone's gizmo runs after the loop, in its own scope, so its hitboxes
		// do not fight with the bone hitboxes.
		if ( _selectedBoneIndex >= 0 && _selectedBoneIndex < RigSkeleton.Count )
			DrawSelectedBoneGizmo();

		Gizmo.Draw.IgnoreDepth = false;

		// Click empty space to deselect — AFTER the gizmo so Gizmo.HasHovered covers both
		// our bone hitboxes AND the gizmo control's own hitboxes. Using !Gizmo.IsHovered
		// here was the bug: IsHovered only sees Hitbox.Sphere calls, not Control hitboxes,
		// so clicking the gizmo counted as empty space and deselected immediately.
		if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered && _selectedBoneIndex >= 0 && !_boneDragging )
		{
			_selectedBoneIndex = -1;
			BoneSelectionChanged?.Invoke( -1 );
		}
	}

	/// <summary>Base radius of a bone's head sphere in world units.</summary>
	private const float BoneHandleRadius = 0.8f;

	/// <summary>Draw one bone as a dog-bone: a knobby ball at the head, a knobby ball at the
	/// tail, and a thin shaft between them.</summary>
	private void DrawBoneHandle( int index )
	{
		var world = RigSkeleton.WorldBind( index );
		var bone = RigSkeleton.Bones[index];

		var head = new Vector3( world.Origin.x, world.Origin.y, world.Origin.z );
		var tailVec = world.TransformPoint( new Vec3( 0, bone.Length, 0 ) );
		var tail = new Vector3( tailVec.x, tailVec.y, tailVec.z );

		// Cross-section axes from the Xform basis — shows roll of the bone.
		var xAxis = new Vector3( world.X.x, world.X.y, world.X.z );
		var zAxis = new Vector3( world.Z.x, world.Z.y, world.Z.z );

		var isSelected = index == _selectedBoneIndex;

		Gizmo.Draw.Color = isSelected
			? new Color( 1f, 0.85f, 0.2f, 1f )
			: new Color( 0.95f, 0.35f, 0.2f, 0.8f );

		DrawDogBone( head, tail, xAxis, zAxis );

		// No hitbox on the selected bone — its own gizmo handles registration.
		if ( isSelected )
			return;

		// While placing new bones, an existing bone's hitbox would steal the click instead of
		// letting it land on the mesh underneath.
		if ( BoneToolActive )
			return;

		Gizmo.Hitbox.DepthBias = 0.01f;
		Gizmo.Hitbox.Sphere( new Sphere( head, BoneHandleRadius * 2.5f ) );

		if ( Gizmo.IsHovered )
		{
			Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 0.35f );
			Gizmo.Draw.SolidSphere( head, BoneHandleRadius * 2.5f, 8, 8 );

			if ( Gizmo.WasLeftMousePressed )
			{
				_selectedBoneIndex = index;
				BoneSelectionChanged?.Invoke( index );
			}
		}
	}

	/// <summary>
	/// Pose gizmo for the selected bone. Mode is set by W (move), E (rotate), R (scale).
	/// Follows RigViewport's pattern: Position gives per-frame delta (accumulate), Rotate
	/// gives cumulative-since-grab (assign). The start pose is captured once and everything
	/// is applied to that — the live transform is never fed back.
	/// </summary>
	private void DrawSelectedBoneGizmo()
	{
		var world = RigSkeleton.WorldBind( _selectedBoneIndex );
		var bone = RigSkeleton.Bones[_selectedBoneIndex];
		var head = new Vector3( world.Origin.x, world.Origin.y, world.Origin.z );
		var headRot = ExtractRotation( world );

		var startPos = _boneDragging ? _dragStartPos : head;
		var startRot = _boneDragging ? _dragStartRot : headRot;

		using var scope = Gizmo.Scope( $"BoneCtrl{_selectedBoneIndex}", new Transform( startPos, startRot ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		switch ( _boneDragMode )
		{
			case BoneDragMode.Rotate:
			{
				if ( !Gizmo.Control.Rotate( $"bone{_selectedBoneIndex}-rot", Rotation.Identity, out var rotation ) )
				{
					EndBoneDragIfReleased();
					return;
				}

				BeginBoneDrag( head, headRot, bone.Length );
				_boneDragging = true;

				// Rotate is CUMULATIVE since the grab — assign, don't accumulate.
				var newRot = rotation * _dragStartRot;

				ApplyBoneTransform( _selectedBoneIndex, _dragStartPos, newRot, _dragStartLength );
				break;
			}

			case BoneDragMode.Move:
			{
				if ( !Gizmo.Control.Position( $"bone{_selectedBoneIndex}-pos", Vector3.Zero, out var delta, Rotation.Identity ) )
				{
					EndBoneDragIfReleased();
					return;
				}

				BeginBoneDrag( head, headRot, bone.Length );
				_boneDragging = true;

				// Position is PER-FRAME DELTA — accumulate.
				_moveDelta += delta;

				ApplyBoneTransform( _selectedBoneIndex, _dragStartPos + _moveDelta, _dragStartRot, _dragStartLength );
				break;
			}

			case BoneDragMode.Scale:
			{
				// Scale adjusts bone length via a vertical drag.
				if ( !Gizmo.Control.Position( $"bone{_selectedBoneIndex}-scl", Vector3.Zero, out var delta, Rotation.Identity ) )
				{
					EndBoneDragIfReleased();
					return;
				}

				BeginBoneDrag( head, headRot, bone.Length );
				_boneDragging = true;

				// Use the vertical (Y) component of the drag as the length change.
				var localDelta = _dragStartRot.Inverse * delta;
				var newLength = MathF.Max( _dragStartLength + localDelta.y, 0.5f );

				ApplyBoneTransform( _selectedBoneIndex, _dragStartPos, _dragStartRot, newLength );
				break;
			}
		}
	}

	private void BeginBoneDrag( Vector3 head, Rotation headRot, float length )
	{
		if ( _boneDragging )
			return;

		_boneDragging = true;
		_dragStartPos = head;
		_dragStartRot = headRot;
		_dragStartLength = length;
		_moveDelta = Vector3.Zero;
	}

	private void EndBoneDragIfReleased()
	{
		if ( Gizmo.IsLeftMouseDown )
			return;

		_boneDragging = false;
	}

	/// <summary>
	/// A literal dog-bone: a knobby ball at each end, joined by a thin shaft. This is the
	/// shape a "bone" reads as at a glance, unlike Blender's tapering-diamond convention
	/// which this replaces.
	/// </summary>
	private static void DrawDogBone( Vector3 head, Vector3 tail, Vector3 xAxis, Vector3 zAxis )
	{
		var boneDir = tail - head;
		var boneLen = boneDir.Length;
		if ( boneLen < 0.01f )
			return;

		var axis = boneDir / boneLen;

		// The knobs are wider than the shaft — that contrast is what makes the shape read
		// as a bone rather than a dumbbell bar. Inset the shaft so it disappears inside the
		// knobs rather than poking out past them.
		var knobR = boneLen * 0.16f;
		var shaftR = knobR * 0.35f;
		var inset = knobR * 0.6f;

		Gizmo.Draw.SolidSphere( head, knobR, 8, 8 );
		Gizmo.Draw.SolidSphere( tail, knobR, 8, 8 );

		var shaftStart = head + axis * inset;
		var shaftEnd = tail - axis * inset;

		if ( (shaftEnd - shaftStart).Length > 0.01f )
			DrawShaft( shaftStart, shaftEnd, xAxis, zAxis, shaftR );
	}

	/// <summary>A thin cylinder between two points, wound both ways per face so it reads
	/// solid from either side — same trick the old diamond body used.</summary>
	private static void DrawShaft( Vector3 a, Vector3 b, Vector3 xAxis, Vector3 zAxis, float radius, int segments = 8 )
	{
		for ( var i = 0; i < segments; i++ )
		{
			var t0 = i / (float)segments * MathF.Tau;
			var t1 = (i + 1) / (float)segments * MathF.Tau;

			var o0 = xAxis * (MathF.Cos( t0 ) * radius) + zAxis * (MathF.Sin( t0 ) * radius);
			var o1 = xAxis * (MathF.Cos( t1 ) * radius) + zAxis * (MathF.Sin( t1 ) * radius);

			var a0 = a + o0;
			var a1 = a + o1;
			var b0 = b + o0;
			var b1 = b + o1;

			Gizmo.Draw.SolidTriangle( a0, b0, a1 );
			Gizmo.Draw.SolidTriangle( a1, b0, b1 );

			Gizmo.Draw.SolidTriangle( a0, a1, b0 );
			Gizmo.Draw.SolidTriangle( a1, b1, b0 );
		}
	}

	/// <summary>Extract an s&box Rotation from an Effigy Xform's basis columns.
	/// Xform.Y is bone forward (+Y convention), Xform.Z is bone up.</summary>
	private static Rotation ExtractRotation( Xform xform )
	{
		var forward = new Vector3( xform.Y.x, xform.Y.y, xform.Y.z );
		var up = new Vector3( xform.Z.x, xform.Z.y, xform.Z.z );
		return Rotation.LookAt( forward, up );
	}

	/// <summary>
	/// Write a world-space pose back into the skeleton. Updates position, orientation, and
	/// optionally length of the bone, converting back to parent-local space. Children follow
	/// automatically because their Local transforms are relative.
	/// </summary>
	private void ApplyBoneTransform( int index, Vector3 newHeadWorld, Rotation newWorldRot, float newLength )
	{
		var bone = RigSkeleton.Bones[index];

		// Decompose the new world rotation into the Xform basis columns.
		var fwd = newWorldRot.Forward;
		var right = newWorldRot.Right;
		var up = newWorldRot.Up;

		var newX = new Vec3( right.x, right.y, right.z );
		var newY = new Vec3( fwd.x, fwd.y, fwd.z );
		var newZ = new Vec3( up.x, up.y, up.z );
		var newOrigin = new Vec3( newHeadWorld.x, newHeadWorld.y, newHeadWorld.z );

		if ( bone.Parent < 0 )
		{
			bone.Local = new Xform( newX, newY, newZ, newOrigin );
		}
		else
		{
			var parentWorld = RigSkeleton.WorldBind( bone.Parent );
			var inv = parentWorld.Inverse;
			bone.Local = new Xform(
				inv.TransformDirection( newX ),
				inv.TransformDirection( newY ),
				inv.TransformDirection( newZ ),
				inv.TransformPoint( newOrigin ) );
		}

		bone.Length = newLength;

		// Fires every frame of a drag, not just on release — a numeric inspector reading these
		// same bones live is the reason: without this it goes stale the instant a drag starts and
		// stays wrong until the bone is reselected, which is worse than not showing numbers at all.
		BonePosed?.Invoke( index );
	}

	/// <summary>Raised whenever the pose gizmo writes a new transform into a bone — see
	/// ApplyBoneTransform. Carries the bone's index so a listener only watching one bone (an
	/// inspector panel, say) can ignore edits to any other.</summary>
	public Action<int> BonePosed { get; set; }

	/// <summary>Deselect the bone — called from the rig panel or Escape key.</summary>
	public void DeselectBone()
	{
		if ( _selectedBoneIndex < 0 )
			return;

		_selectedBoneIndex = -1;
		_boneDragging = false;
		BoneSelectionChanged?.Invoke( -1 );
	}

	/// <summary>Select a bone by index — called from the rig panel's tree view. Does not
	/// invoke the BoneSelectionChanged callback to avoid feedback loops.</summary>
	public void SelectBone( int index )
	{
		_selectedBoneIndex = index >= 0 && index < RigSkeleton?.Count ? index : -1;
	}

	/// <summary>Escape backs out of the half-drawn entity, then out of the tool - the same two
	/// stages every CAD sketcher uses. W/E/R switch bone drag modes when a bone is selected.</summary>
	protected override void OnKeyPress( KeyEvent e )
	{
		// A dimension box up on screen owns the keyboard first - digits, Enter and its own Escape.
		// It has to come before the Escape branch below or dismissing the number would also back
		// out of the tool you are drawing with.
		if ( HandleDimensionKey( e ) )
			return;

		// W/E/R switch bone drag mode while a bone is selected.
		if ( _selectedBoneIndex >= 0 )
		{
			switch ( e.Key )
			{
				case KeyCode.W:
					_boneDragMode = BoneDragMode.Move;
					e.Accepted = true;
					return;
				case KeyCode.E:
					_boneDragMode = BoneDragMode.Rotate;
					e.Accepted = true;
					return;
				case KeyCode.R:
					_boneDragMode = BoneDragMode.Scale;
					e.Accepted = true;
					return;
			}
		}

		if ( e.Key != KeyCode.Escape )
		{
			base.OnKeyPress( e );
			return;
		}

		if ( IsSketching )
		{
			// A selection is the shallower thing to back out of, so Escape drops it first and only
			// cancels the tool on a second press. Otherwise picking three things and hitting Escape
			// to undo the third would abandon the tool as well.
			if ( HasSketchSelection )
				ClearSketchSelection();
			else
				CancelSketchTool();

			e.Accepted = true;
			return;
		}

		// Escape stands down an armed selection box. The viewport owns the key press; the dialog
		// owns the boxes' painted state, so it is told through PickModeCancelled. Sketch picking
		// itself stays live while a consumer dialog is open — the dialog turns it off.
		if ( PlanePickMode || SketchPickMode || BodyPickMode )
		{
			PlanePickMode = false;
			BodyPickMode = false;
			PickModeCancelled?.Invoke();
			e.Accepted = true;
			return;
		}

		// Same two-stage back-out as the sketch tools: the panel owns which stage it is, since it
		// is the one holding whether a chain is currently open.
		if ( BoneToolActive )
		{
			BoneToolEscape?.Invoke();
			e.Accepted = true;
			return;
		}

		if ( _selectedBoneIndex >= 0 )
		{
			DeselectBone();
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
