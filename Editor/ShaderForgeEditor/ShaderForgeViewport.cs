using Editor;
using Sandbox;
using System;

namespace Marionette.ShaderForgeEditor;

/// <summary>
/// The 3D preview pane — a live <see cref="SceneRenderingWidget"/> showing the current model
/// (a stock primitive for now; Phase 2 adds loading real project models) under a small pre-lit
/// rig, with editor orbit-camera controls.
///
/// Camera handling, the scene-tick requirement and the framing math are copied from
/// <c>EffigyViewport</c> (see Editor/EffigyEditor) rather than reinvented — both are proven there,
/// and HANDOFF.md is explicit that <c>SceneRenderingWidget</c> renders its scene but never ticks
/// it, so skipping <see cref="Scene.EditorTick"/> in <see cref="OnPreFrame"/> would silently
/// freeze the preview.
/// </summary>
internal sealed class ShaderForgeViewport : Widget
{
	private readonly SceneRenderingWidget _canvas;
	private readonly CameraComponent _camera;
	private readonly Gizmo.Instance _gizmoInstance;

	private GameObject _modelObject;
	private ModelRenderer _renderer;

	/// <summary>Raised whenever the shown model changes, so the status bar can update.</summary>
	public Action<string> ModelInfoChanged { get; set; }

	/// <summary>Current model stats for the status bar.</summary>
	public string ModelInfo { get; private set; } = "";

	public ShaderForgeViewport( Widget parent ) : base( parent )
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
			_camera.BackgroundColor = new Color( 0.18f, 0.19f, 0.21f );
			_camera.ZNear = 0.5f;
			_camera.ZFar = 8192;
			_camera.FieldOfView = 45f;
			_camera.Enabled = true;

			// The pre-lit rig the design doc asks for: key, fill and a subtle ambient — enough to
			// read shape and shading without any of them being a stand-in for the shader under
			// test producing its own light.
			var key = new GameObject( true, "key_light" ).GetOrAddComponent<DirectionalLight>( false );
			key.WorldRotation = Rotation.From( 45, 45, 0 );
			key.LightColor = Color.White;
			key.Enabled = true;

			var point = new GameObject( true, "fill_point" ) { WorldPosition = new Vector3( -40, 60, 60 ) }
				.GetOrAddComponent<PointLight>( false );
			point.LightColor = new Color( 0.75f, 0.8f, 0.9f );
			point.Radius = 400f;
			point.Enabled = true;

			var ambient = new GameObject( true, "ambient" ).GetOrAddComponent<AmbientLight>( false );
			ambient.Color = new Color( 0.35f, 0.35f, 0.38f );
			ambient.Enabled = true;

			_canvas.Camera = _camera;
		}

		_gizmoInstance = _canvas.GizmoInstance;

		Layout.Add( _canvas, 1 );

		SetPrimitive( ShaderForgePrimitiveKind.Sphere );
	}

	// --- model management -------------------------------------------------------------------

	/// <summary>Swap the preview to one of the stock primitives.</summary>
	public void SetPrimitive( ShaderForgePrimitiveKind kind )
	{
		SetModel( ShaderForgePrimitives.Build( kind ), ShaderForgePrimitives.Label( kind ) );
	}

	/// <summary>Show a model built elsewhere (a stock primitive today; a project asset once Phase 2
	/// lands). <paramref name="displayName"/> overrides what the status bar calls it — a stock
	/// primitive has no <see cref="Model.ResourceName"/> worth showing.</summary>
	public void SetModel( Model model, string displayName = null, bool frameCamera = true )
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

		_modelObject = new GameObject( true, "shaderforge_model" );
		_renderer = _modelObject.GetOrAddComponent<ModelRenderer>( false );
		_renderer.Model = model;
		_renderer.Enabled = true;

		var meshCount = model.MeshCount;
		var size = model.Bounds.Size;
		var name = displayName ?? model.ResourceName ?? model.Name ?? "model";
		ModelInfo = $"{name} · {meshCount} mesh{(meshCount != 1 ? "es" : "")} · {size.x:F0}×{size.y:F0}×{size.z:F0}";
		ModelInfoChanged?.Invoke( ModelInfo );

		if ( frameCamera )
			FrameCamera();
	}

	/// <summary>Apply (or clear, with null) a whole-model material override — how the manual
	/// shader preview and the generator both put a material on screen.</summary>
	public void SetMaterialOverride( Material material )
	{
		if ( _renderer.IsValid() )
			_renderer.MaterialOverride = material;
	}

	/// <summary>
	/// Try to override one material slot rather than the whole model, so a character can preview a
	/// skin shader on the body and glass on the eyes at once.
	///
	/// Guarded and reported rather than assumed: whole-model MaterialOverride is proven in this
	/// repo (RigViewport uses it), per-slot targeting through the SceneObject is not. False means
	/// the caller should fall back to the whole model and TELL the user it did — silently painting
	/// every slot when one was asked for is worse than saying it cannot.
	/// </summary>
	public bool TrySetSlotOverride( int slotIndex, Material material )
	{
		if ( !_renderer.IsValid() || slotIndex < 0 )
			return false;

		try
		{
			var sceneObject = _renderer.SceneObject;

			if ( sceneObject is null )
				return false;

			// SetMaterialOverride( material, attributeName, attributeValue ) targets the subset of
			// the model whose material carries that attribute. Slot index is the addressing the
			// model itself exposes, so that is what gets passed through.
			var method = sceneObject.GetType().GetMethod( "SetMaterialOverride",
				new[] { typeof( Material ), typeof( string ), typeof( int ) } );

			if ( method is null )
				return false;

			method.Invoke( sceneObject, new object[] { material, "materialIndex", slotIndex } );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[ShaderForge] per-slot material override unavailable — " +
				$"{e.GetType().Name}: {e.Message}. Falling back to a whole-model override." );
			return false;
		}
	}

	/// <summary>Whether anything is currently loaded to shade.</summary>
	public bool HasModel => _renderer.IsValid() && _renderer.Model is not null;

	/// <summary>The model on screen, so the slot panel can read its material slots.</summary>
	public Model CurrentModel => _renderer.IsValid() ? _renderer.Model : null;

	/// <summary>
	/// Frame whatever is on screen from an isometric-ish front-right-top angle.
	///
	/// Fits the bounding sphere in the vertical FOV rather than sitting at a fixed distance, the
	/// same approach EffigyViewport uses and for the same reason: primitives here range from a
	/// hand-sized sphere to a project model many times larger, and a fixed pullback would frame
	/// one fine and reduce the other to a speck.
	/// </summary>
	public void FrameCamera()
	{
		var dir = new Vector3( 1f, -1f, 0.65f ).Normal;
		var center = Vector3.Zero;
		var radius = 32f;

		if ( _renderer.IsValid() && _renderer.Model is { } model )
		{
			var bounds = model.Bounds;
			center = bounds.Center;
			radius = MathF.Max( bounds.Size.Length * 0.5f, 1f );
		}

		var distance = radius / MathF.Tan( _camera.FieldOfView.DegreeToRadian() * 0.5f ) * 1.6f;

		_camera.WorldPosition = center + dir * distance;
		_camera.WorldRotation = Rotation.LookAt( -dir, Vector3.Up );
		_camera.ZNear = Math.Clamp( distance * 0.01f, 0.01f, 8f );
	}

	// --- per-frame tick ---------------------------------------------------------------------

	private void OnPreFrame()
	{
		if ( _canvas.Scene is { } scene )
			scene.EditorTick( RealTime.Now, RealTime.Delta );

		_gizmoInstance.Input.IsHovered = IsActiveWindow && _canvas.IsUnderMouse;

		// Drag to rotate, scroll to zoom, right-drag to pan — the editor's standard orbit camera,
		// same call EffigyViewport and RigViewport drive their viewports with.
		if ( _gizmoInstance.FirstPersonCamera( _camera, _canvas ) )
			_gizmoInstance.Input.IsHovered = false;

		_canvas.UpdateGizmoInputs( _gizmoInstance.Input.IsHovered );

		Cursor = Gizmo.HasHovered ? CursorShape.Finger : CursorShape.Arrow;
	}
}
