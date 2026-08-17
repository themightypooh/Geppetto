using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>What dragging a bone does. Held E flips to the other one for as long as it's down,
/// so the common case (rotate) needs no modifier and the occasional one is still one key away.</summary>
internal enum BoneDragMode
{
	Rotate,
	Move
}

/// <summary>
/// The 3D panel - a skinned model in its own editor scene, with an FK rotate ring per bone
/// (see the Blue/Yellow circles on the Citizen in the reference screenshot) and a move handle
/// on whichever bone is selected.
///
/// Posing goes through MovieBoneAnimatorSystem.SetParentSpaceBone - the same entry point
/// MovieMaker's own bone dragging uses (Session/InverseKinematics.cs) to actually deform a
/// SkinnedModelRenderer's mesh. This was ruled out early on because the type isn't referenceable
/// without an explicit assembly reference - MovieMaker compiles to its own package assembly
/// (package.local.moviemaker.dll), which midnight_am.sbproj now lists under
/// Metadata.Compiler.AssemblyReferences. Without this, "posing" only ever moved a detached
/// GameObject (CreateBoneObjects, or GetBoneObject's returned proxy) that never fed back into
/// the render - confirmed directly: s&box's own docs call CreateBoneObjects legacy, saying it
/// only ever worked as an intermediate step MovieMaker's *own* internal baking read from, never
/// as a live "move this and the mesh follows" control on its own.
/// </summary>
internal sealed class RigViewport : Widget
{
	private readonly SceneRenderingWidget _canvas;
	private readonly CameraComponent _camera;
	private readonly Gizmo.Instance _gizmoInstance;

	private GameObject _modelObject;
	private SkinnedModelRenderer _renderer;
	private float _boneHandleRadius = 1f;

	public Action<string> BoneSelected { get; set; }
	public string SelectedBone { get; private set; }

	/// <summary>The control rig whose constraints apply while posing. Null poses plain FK.</summary>
	public RigDocument Rig { get; set; }

	/// <summary>What dragging a bone's dot does.
	///
	/// Rotate is the default because skeletal animation is rotation - joints pivot, they don't
	/// slide. Translating a bone stretches the skin and pulls the joint off its parent, which is
	/// the single most common way a first pose ends up looking broken. Move is still here because
	/// it's genuinely needed for root/IK-target bones, but it's the exception.</summary>
	public BoneDragMode DragMode { get; set; } = BoneDragMode.Rotate;

	/// <summary>Fired every frame a bone's pose actually changes from dragging - the current
	/// frame's keyframe is written on every call rather than once at drag-end, since
	/// BoneTrack.SetKeyframe overwriting the same frame repeatedly is harmless and there's no
	/// confirmed "drag finished" signal from Gizmo.Control itself to hook instead.</summary>
	public Action<string, Transform> BonePosed { get; set; }

	/// <summary>Fired once when a bone drag begins, before anything has moved, and once when it
	/// ends. Undo hangs off these: BonePosed fires every frame of a drag, so recording there would
	/// bury the stack under hundreds of one-pixel entries. One drag is one undo step.</summary>
	public Action<string> BoneDragStarted { get; set; }

	public Action BoneDragEnded { get; set; }

	private bool _suppressAutoKey;

	public RigViewport( Widget parent ) : base( parent )
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
			_camera.BackgroundColor = Theme.ControlBackground;
			_camera.ZFar = 4096;
			_camera.Enabled = true;

			var sun = new GameObject( true, "sun" ).GetOrAddComponent<DirectionalLight>( false );
			sun.WorldRotation = Rotation.From( 45, 45, 0 );
			sun.LightColor = Color.White;
			sun.Enabled = true;

			var ambient = new GameObject( true, "ambient" ).GetOrAddComponent<AmbientLight>( false );
			ambient.Color = Theme.ControlBackground * 0.6f;
			ambient.Enabled = true;

			_canvas.Camera = _camera;
		}

		_gizmoInstance = _canvas.GizmoInstance;

		Layout.Add( _canvas, 1 );
	}

	public void SetModel( Model model )
	{
		using var scope = _canvas.Scene.Push();

		if ( _renderer.IsValid() )
		{
			_renderer.Enabled = false;
			_renderer.Model = null;
		}

		_modelObject?.Destroy();
		_modelObject = null;
		_renderer = null;
		_pixelMaterial = null;
		Select( null );

		if ( model is null )
			return;

		_modelObject = new GameObject( true, "rig" );
		_renderer = _modelObject.GetOrAddComponent<SkinnedModelRenderer>( false );

		_renderer.Model = model;
		_renderer.UseAnimGraph = false;
		_renderer.Enabled = true;

		_boneHandleRadius = (model.Bounds.Size.Length * 0.012f).Clamp( 0.15f, 3f );

		FrameCamera();
	}

	public SkinnedModelRenderer Renderer => _renderer;

	public void Select( string bone )
	{
		SelectedBone = bone;
		BoneSelected?.Invoke( bone );
	}

	private void FrameCamera()
	{
		if ( !_renderer.IsValid() || _renderer.Model is null )
			return;

		var bounds = _renderer.Model.Bounds;
		var distance = MathX.SphereCameraDistance( MathF.Max( bounds.Size.Length * 0.6f, 32f ), _camera.FieldOfView );

		_camera.WorldPosition = bounds.Center + new Vector3( -1, -1, 0.5f ).Normal * distance;
		_camera.WorldRotation = Rotation.LookAt( bounds.Center - _camera.WorldPosition, Vector3.Up );
	}

	/// <summary>Every bone in the model's own skeleton, with its current world transform - read
	/// via TryGetBoneTransform, not a GameObject. No GameObject proxy is involved anywhere in this
	/// file anymore; posing writes go straight through MovieBoneAnimatorSystem instead.</summary>
	private IEnumerable<(BoneCollection.Bone Bone, Transform World)> LiveBones()
	{
		if ( !_renderer.IsValid() || _renderer.Model?.Bones is not { } bones )
			yield break;

		foreach ( var bone in bones.AllBones )
		{
			if ( _renderer.TryGetBoneTransform( bone, out var world ) )
				yield return (bone, world);
		}
	}

	public IEnumerable<string> BoneNames() => LiveBones().Select( x => x.Bone.Name );

	/// <summary>Bones with no parent in the skeleton itself - the citizen_human rig has more than
	/// one of these (pelvis, and a separate root_IK utility chain), so this is a list.</summary>
	public IEnumerable<string> RootBoneNames() =>
		LiveBones().Where( x => x.Bone.Parent is null ).Select( x => x.Bone.Name );

	public IEnumerable<string> ChildBoneNames( string parentName ) =>
		LiveBones().Where( x => x.Bone.Parent?.Name == parentName ).Select( x => x.Bone.Name );

	private BoneCollection.Bone FindBoneData( string name ) =>
		_renderer.IsValid() && _renderer.Model?.Bones is { } bones ? bones.GetBone( name ) : null;

	public bool TryGetWorldTransform( string name, out Transform world )
	{
		world = default;

		if ( !_renderer.IsValid() || FindBoneData( name ) is not { } bone )
			return false;

		return _renderer.TryGetBoneTransform( bone, out world );
	}

	/// <summary>A bone's current pose in parent space - the form keyframes are stored in. This is
	/// what "key this bone where it is right now" needs, independent of any drag.</summary>
	public bool TryGetLocalTransform( string name, out Transform local )
	{
		local = default;

		if ( !_renderer.IsValid() || FindBoneData( name ) is not { } bone )
			return false;

		if ( !_renderer.TryGetBoneTransform( bone, out var world ) )
			return false;

		local = ParentWorld( bone ).ToLocal( world );
		return true;
	}

	/// <summary>Write a bone's new world-space transform.
	///
	/// SetBoneTransform is the right call and it does work - verified headlessly (rig_test_pose):
	/// it registers a bone override that is still in place after a scene tick. What it does NOT do
	/// is take effect immediately - reading the bone back in the same frame you wrote it still
	/// returns the old value, because overrides are only folded into the pose during the scene's
	/// bone update. That one-frame lag is expected here, not a bug: the drag re-reads the bone
	/// fresh each frame, so it picks up the previous frame's write.
	///
	/// SceneModel.SetBoneWorldTransform is the trap to avoid - it's the one that reads back
	/// instantly, which makes it look correct, but it sets no override and the very next tick
	/// stomps it back to the bind pose.</summary>
	private void ApplyWorldTransform( BoneCollection.Bone bone, Transform world )
	{
		_renderer.SetBoneTransform( bone, world );
	}

	/// <summary>A bone's parent's world transform, falling back to the model's own for roots.</summary>
	private Transform ParentWorld( BoneCollection.Bone bone ) =>
		bone.Parent is { } parent && _renderer.TryGetBoneTransform( parent, out var parentTx )
			? parentTx
			: _renderer.WorldTransform;

	/// <summary>Run a world-space pose through any Limit constraints on that bone. Limits are
	/// authored in parent space (that's the only space a joint angle means anything in), so this
	/// converts down, clamps, and converts back.</summary>
	private Transform ApplyLimits( BoneCollection.Bone bone, Transform world )
	{
		if ( Rig is null )
			return world;

		var parentWorld = ParentWorld( bone );
		var local = RigConstraintSolver.ClampToLimits( Rig, bone.Name, parentWorld.ToLocal( world ) );

		return parentWorld.ToWorld( local );
	}

	/// <summary>Push a keyed pose onto every bone that has one for this frame, skipping whichever
	/// bone is being actively dragged so playback can't fight the hand doing the dragging.
	/// _draggingBone (set inside DrawSelectedBoneControls, the only place it's safe to read
	/// Gizmo.IsLeftMouseDown) stands in for the raw Gizmo property - EvaluatePose is called from
	/// RigControlWindow.OnScrub, outside any active Gizmo context, and Gizmo.IsLeftMouseDown
	/// throws a NullReferenceException unconditionally when read from there. That exception fired
	/// on every single scrub and every playback tick, silently, the entire time.</summary>
	public void EvaluatePose( Func<string, Transform?> poseForBone )
	{
		_suppressAutoKey = true;

		try
		{
			// REBUILD THE POSE FROM THE DOCUMENT, don't layer onto whatever the renderer still
			// holds. Posing a bone leaves a bone override on the renderer, and the loop below
			// skips bones that have no keyframe - so without clearing first, a bone keeps the
			// override from the last time it was dragged even after its keyframes are gone.
			//
			// That is what made undo look broken. Undo was restoring the document correctly the
			// whole time; the viewport just went on displaying the posed bone, because nothing
			// ever told the renderer to let go of it.
			//
			// Skipped mid-drag, where clearing would fight the hand doing the dragging.
			if ( !_draggingBone && _renderer.IsValid() && _renderer.SceneModel is { } sceneModel )
				sceneModel.ClearBoneOverrides();

			foreach ( var (bone, world) in LiveBones() )
			{
				if ( bone.Name == SelectedBone && _draggingBone )
					continue;

				if ( poseForBone( bone.Name ) is not { } local )
					continue;

				// Keyframes are stored parent-space (BoneTrack.Evaluate) - world space is what
				// ApplyWorldTransform's writes expect (matching TryGetBoneTransform's convention).
				// Limits are clamped here too, so scrubbing shows the same pose posing produced
				// rather than only constraining at author time.
				var parentWorld = ParentWorld( bone );
				var clamped = RigConstraintSolver.ClampToLimits( Rig, bone.Name, local );

				ApplyWorldTransform( bone, parentWorld.ToWorld( clamped ) );
			}
		}
		finally
		{
			_suppressAutoKey = false;
		}
	}

	private readonly System.Diagnostics.Stopwatch _sceneClock = System.Diagnostics.Stopwatch.StartNew();
	private float _lastTickTime;

	/// <summary>SceneRenderingWidget renders its scene but never updates it - it has PreFrame,
	/// Render and RenderScene, and no tick of any kind. Bone overrides are only folded into the
	/// render pose during the scene's bone update (rig_test_pose: a write reads back stale in the
	/// same frame and correct only after a tick), so without this the model sat frozen and no
	/// amount of correct posing could ever have shown up. Nothing moved because nothing updated.</summary>
	private void TickScene()
	{
		if ( _canvas.Scene is not { } scene )
			return;

		var now = (float)_sceneClock.Elapsed.TotalSeconds;
		var delta = (now - _lastTickTime).Clamp( 0f, 0.1f );
		_lastTickTime = now;

		scene.EditorTick( now, delta );
	}

	/// <summary>Wear the game's pixel-arms look instead of whatever the model ships with. The
	/// preview arms model resolves to materials/dev/gray_25.vmat - a dev placeholder - which is
	/// why it renders as a white mannequin, indistinguishable from a missing material.</summary>
	public bool PixelStyle { get; set; } = true;

	private Material _pixelMaterial;
	private Texture _pixelSkin;
	private bool _pixelSkinResolved;

	private void ApplyPixelStyle()
	{
		if ( !_renderer.IsValid() )
			return;

		if ( !PixelStyle )
		{
			// Only clear an override we put there.
			if ( _pixelMaterial is not null && _renderer.MaterialOverride == _pixelMaterial )
				_renderer.MaterialOverride = null;

			return;
		}

		_pixelMaterial ??= PixelArmsStyle.LoadMaterial();

		if ( _pixelMaterial is null )
			return;

		if ( _renderer.MaterialOverride != _pixelMaterial )
			_renderer.MaterialOverride = _pixelMaterial;

		if ( !_pixelSkinResolved )
		{
			_pixelSkinResolved = true;
			_pixelSkin = PixelArmsStyle.ResolveSkinTexture();
		}

		// Read the scene object AFTER setting the override - swapping materials rebuilds it, and
		// it's null until the renderer has been drawn once anyway.
		new PixelArmsStyle
		{
			ColorTexture = _pixelSkin,
			// Both arms: this is a rig editor, not a shot.
			HideSide = 0f,
			// The vertex snap is in screen pixels, and in a tool window that's this widget.
			ScreenSize = _canvas.Size
		}.ApplyTo( _renderer.SceneObject );
	}

	private void OnPreFrame()
	{
		TickScene();
		ApplyPixelStyle();

		_gizmoInstance.Input.IsHovered = IsActiveWindow && _canvas.IsUnderMouse;

		if ( _gizmoInstance.FirstPersonCamera( _camera, _canvas ) )
			_gizmoInstance.Input.IsHovered = false;

		_canvas.UpdateGizmoInputs( _gizmoInstance.Input.IsHovered );

		Gizmo.Draw.Grid( 0, Gizmo.GridAxis.XY );

		DrawBoneHandles();
		DrawSelectedBoneReadout();

		Cursor = Gizmo.HasHovered ? CursorShape.Finger : CursorShape.Arrow;
	}

	/// <summary>The mesh doesn't visually bend when you pose it (see the class header) - this is
	/// the compensating feedback: the selected bone's actual numbers, live, so posing isn't done
	/// fully blind.</summary>
	private void DrawSelectedBoneReadout()
	{
		if ( SelectedBone is null )
			return;

		if ( !TryGetWorldTransform( SelectedBone, out var world ) )
			return;

		var bone = FindBoneData( SelectedBone );
		var parentWorld = bone?.Parent is { } parent && _renderer.TryGetBoneTransform( parent, out var parentTx )
			? parentTx
			: _renderer.WorldTransform;

		var local = parentWorld.ToLocal( world );

		// Quiet, and only the numbers that change while posing. The bone name is the one thing
		// worth reading at a glance, so it keeps full contrast; the values sit back.
		var angles = local.Rotation.Angles();

		Gizmo.Draw.Color = Color.White.WithAlpha( 0.85f );
		Gizmo.Draw.ScreenText( SelectedBone, new Vector2( 12, 12 ), size: 13, flags: TextFlag.LeftTop );

		Gizmo.Draw.Color = Color.White.WithAlpha( 0.4f );
		Gizmo.Draw.ScreenText(
			$"rot  {angles.pitch:0.#}  {angles.yaw:0.#}  {angles.roll:0.#}\n" +
			$"pos  {local.Position.x:0.#}  {local.Position.y:0.#}  {local.Position.z:0.#}",
			new Vector2( 12, 30 ), size: 11, flags: TextFlag.LeftTop );
	}

	private bool _draggingBone;

	/// <summary>One dot per bone, click to select, click-and-drag to move it directly - hold E
	/// while dragging to rotate in place instead. This replaces an earlier select-then-a-separate-
	/// ring-appears-elsewhere design: MovieMaker's own docs describe bone posing as "click and
	/// drag from a joint... hold E to rotate", a single unified control per bone, not two.
	/// Unselected bones get a plain click-to-select hitbox. The selected bone gets no hitbox of
	/// ours at all - only Gizmo.Control, which brings its own. Registering both is what broke
	/// dragging for so long; see the comment on the selected branch below.</summary>
	private void DrawBoneHandles()
	{
		// The selected bone's control is run after the loop, in its own top-level scope, so it
		// isn't nested inside this bone's rotated drawing scope.
		(BoneCollection.Bone Bone, Transform World)? selected = null;
		string hovered = null;

		// X-ray the skeleton. Most of a rig sits inside the mesh, so depth-tested dots are both
		// invisible and unclickable - the hitboxes are already depth-biased to the front, so
		// without this the clickable spot and the visible dot disagree.
		Gizmo.Draw.IgnoreDepth = true;

		foreach ( var (bone, world) in LiveBones() )
		{
			using var boneScope = Gizmo.Scope( $"Bone{bone.Index}", world );

			var radius = _boneHandleRadius;
			var isSelected = bone.Name == SelectedBone;

			if ( bone.Parent is { } parentBone && _renderer.TryGetBoneTransform( parentBone, out var parentWorld ) )
			{
				Gizmo.Draw.Color = Theme.Blue.WithAlpha( 0.8f );
				Gizmo.Draw.Line( 0f, world.PointToLocal( parentWorld.Position ) );
			}

			// Solid dot, not a hollow ring - the reference draws bones as filled white dots.
			// The selected one is drawn fatter as well as yellow, so it's still findable in a
			// dense area of the rig where a colour change alone is easy to lose.
			Gizmo.Draw.Color = isSelected ? Theme.Yellow : (Gizmo.IsHovered ? Theme.Green : Color.White);
			Gizmo.Draw.SolidSphere( 0f, radius * (isSelected ? 0.5f : 0.35f), 8, 8 );

			// No hitbox of our own on the selected bone. Gizmo.Control registers its own hitboxes
			// for its handles, and a sphere sitting at the same scope origin - depth-biased in
			// front, no less - wins the hover test against them, so the control never sees the
			// press and the drag never starts. That was the actual reason dragging did nothing.
			if ( isSelected )
			{
				selected = (bone, world);
				continue;
			}

			Gizmo.Hitbox.DepthBias = 0.01f;
			Gizmo.Hitbox.Sphere( new Sphere( 0f, radius ) );

			if ( Gizmo.IsHovered )
			{
				hovered = bone.Name;

				if ( Gizmo.WasLeftMousePressed )
					Select( bone.Name );
			}
		}

		// Named after the loop so the hint reflects this frame, not last frame's hover.
		if ( hovered is not null )
		{
			RigStatusBar.Show( DragMode == BoneDragMode.Rotate
				? $"{hovered}  -  click to select, then drag to rotate. Hold E to move instead."
				: $"{hovered}  -  click to select, then drag to move. Hold E to rotate instead." );
		}
		else if ( SelectedBone is not null )
		{
			RigStatusBar.Show( $"{SelectedBone} selected  -  drag the gizmo to pose it" );
		}
		else
		{
			RigStatusBar.Clear();
		}

		// Hand the control back normal depth handling so it looks and behaves like the scene
		// editor's own move gizmo.
		Gizmo.Draw.IgnoreDepth = false;

		if ( selected is { } sel )
			DragSelectedBone( sel.Bone, sel.World );
	}

	private string _dragBoneName;
	private Transform _dragAnchor;

	private void EndDrag()
	{
		var was = _dragBoneName;

		_dragBoneName = null;
		_draggingBone = false;

		// Only fire on an actual drag ending - this runs every frame the control isn't active.
		if ( was is not null )
			BoneDragEnded?.Invoke();
	}

	/// <summary>Drag the selected bone with a normal editor-style gizmo.
	///
	/// Gizmo.Control.Position/Rotate are absolute in, absolute out - the out params are named
	/// newPos/newValue, not deltas. They hand back the value the handle has been dragged *to*,
	/// measured from whatever basis you fed in, for the whole drag. Feeding them the bone's live
	/// transform each frame and adding the result to it re-applies the entire drag offset on top
	/// of the already-moved bone every frame, which compounds - that was the "one pixel of mouse
	/// movement and the arm swings around" sensitivity.
	///
	/// So the basis is frozen: the bone's transform is captured once when the drag starts and the
	/// moved result never feeds back into it. The scope is positioned at the bone but deliberately
	/// unrotated, so the arrows are world-aligned like the scene editor's own gizmo rather than
	/// tumbling with the bone.</summary>
	private void DragSelectedBone( BoneCollection.Bone bone, Transform world )
	{
		var dragging = _dragBoneName == bone.Name;
		var basis = dragging ? _dragAnchor : world;

		using var scope = Gizmo.Scope( $"BoneControl{bone.Index}", new Transform( basis.Position ) );

		// E is a hold-to-flip, not a toggle - it borrows the other mode for as long as it's down
		// and springs back, so you can nudge a bone's position mid-rotation-pass without losing
		// your place.
		var rotating = (DragMode == BoneDragMode.Rotate) != Editor.Application.IsKeyDown( KeyCode.E );

		Transform newWorld;

		if ( rotating )
		{
			if ( !Gizmo.Control.Rotate( "bone-rotate", Rotation.Identity, out var rotation ) )
			{
				EndDrag();
				return;
			}

			// Left-multiply: the control's value is a world-space rotation about the bone's
			// own origin, applied on top of the frozen starting orientation.
			newWorld = new Transform( basis.Position, rotation * basis.Rotation, basis.Scale );
		}
		else
		{
			if ( !Gizmo.Control.Position( "bone-move", Vector3.Zero, out var offset ) )
			{
				EndDrag();
				return;
			}

			// Scope is unrotated and unscaled, so its space is world-aligned and this offset can
			// be added straight onto the world position.
			newWorld = new Transform( basis.Position + offset, basis.Rotation, basis.Scale );
		}

		if ( !dragging )
		{
			// Announced before the first write lands, so whatever is listening can record the
			// pre-drag pose.
			BoneDragStarted?.Invoke( bone.Name );

			_dragBoneName = bone.Name;
			_dragAnchor = world;
		}

		_draggingBone = true;

		// IK first: if this bone is an enabled IK target, dragging it should bend the chain
		// behind it rather than tear the effector off its parent.
		if ( RigConstraintSolver.FindIkFor( Rig, bone.Name ) is { } ik
			&& RigConstraintSolver.TrySolveTwoBone( _renderer, bone, newWorld.Position, ik.PoleDirection, out var chain ) )
		{
			var weight = ik.Weight.Clamp( 0f, 1f );

			foreach ( var (chainBone, chainWorld) in chain )
			{
				var blended = chainWorld;

				// Weight blends the solve against where the bone already was, so an IK constraint
				// can be dialled in rather than being all-or-nothing.
				if ( weight < 1f && _renderer.TryGetBoneTransform( chainBone, out var currentWorld ) )
					blended = Transform.Lerp( currentWorld, chainWorld, weight, true );

				ApplyWorldTransform( chainBone, blended );
				NotifyPosed( chainBone, blended );
			}

			return;
		}

		newWorld = ApplyLimits( bone, newWorld );

		ApplyWorldTransform( bone, newWorld );
		NotifyPosed( bone, newWorld );
	}

	/// <summary>The toolbar's Link icon - off, dragging a bone poses it live without writing a
	/// keyframe, same as scrubbing between existing keys does.</summary>
	public bool AutoKeyEnabled { get; set; } = true;

	private void NotifyPosed( BoneCollection.Bone bone, Transform newWorld )
	{
		if ( _suppressAutoKey || !AutoKeyEnabled )
			return;

		var parentWorld = bone.Parent is { } parent && _renderer.TryGetBoneTransform( parent, out var parentTx )
			? parentTx
			: _renderer.WorldTransform;

		BonePosed?.Invoke( bone.Name, parentWorld.ToLocal( newWorld ) );
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		_canvas.Scene?.Destroy();
	}
}
