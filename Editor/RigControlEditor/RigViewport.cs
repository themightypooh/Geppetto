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

	// VIEWMODEL MODE
	//
	// First-person arms are not posed in world space - in game they hang off the camera, so the
	// only framing that tells you whether a pose reads is the one the player actually gets. These
	// defaults mirror ViewArmsComponent, which parents the arms to the camera object and sets
	// LocalPosition = (0,0,-8): the camera sits at the origin looking forward and the model hangs
	// below it. They are the numbers to tune, not laws - the correct offset depends on the FOV and
	// the model, which is exactly why they're editable while you watch.

	/// <summary>Frames the model as a viewmodel hanging off the camera instead of as a world prop.</summary>
	public bool ViewmodelMode { get; set; }

	/// <summary>Pins the camera to the player's eye instead of free-flying it. Only meaningful in
	/// viewmodel mode, and it takes the mouse away from camera control - which is the point.</summary>
	public bool LockCameraToView { get; set; }

	/// <summary>Zero by default. The model places itself - its camera bone says where the eye
	/// goes - so nothing needs shifting unless you deliberately want the arms sitting off from
	/// where the rig thinks they should.</summary>
	public Vector3 ViewmodelOffset { get; set; }
	public Angles ViewmodelRotation { get; set; }
	public float ViewmodelFov { get; set; } = 90f;

	// What ApplyViewmodelFraming last wrote, so it can skip writing an identical value.
	private Vector3 _appliedOffset = new( float.NaN, float.NaN, float.NaN );
	private Angles _appliedRotation = new( float.NaN, float.NaN, float.NaN );

	/// <summary>Hides the per-bone dots. A first-person rig puts a hundred handles between you and
	/// the two you're actually moving; this is the way out of that.</summary>
	public bool ShowBoneHandles { get; set; } = true;

	/// <summary>One gizmo on the model root instead of a handle per bone - for placing the whole
	/// model, which in viewmodel mode is how you set where the arms sit relative to the eye.</summary>
	public bool MoveWholeModel { get; set; }

	public Action ViewmodelChanged { get; set; }

	/// <summary>Reveals hidden bones without unhiding them - the way back when you've hidden
	/// something you now need.</summary>
	public bool ShowHiddenBones { get; set; }

	private string _hoveredBone;

	public bool IsHidden( string bone ) => Rig is not null && Rig.IsBoneHidden( bone );

	/// <summary>Right-click a bone dot to hide it. Hiding a whole chain in one go matters more
	/// than it sounds: the useless bones on a rig are almost always a whole subtree - a weapon
	/// root and everything parented under it - and hiding thirty of them one at a time is enough
	/// friction that nobody bothers.</summary>
	protected override void OnContextMenu( ContextMenuEvent e )
	{
		if ( Rig is null )
		{
			RigStatusBar.Show( "Hiding bones needs a Control Rig - set one in the BonesObject tab first" );
			return;
		}

		var menu = new Menu( this );
		var target = _hoveredBone ?? SelectedBone;

		if ( target is not null )
		{
			menu.AddHeading( target );

			if ( IsHidden( target ) )
			{
				menu.AddOption( "Unhide", "visibility", () => SetHidden( target, false, false ) );
			}
			else
			{
				menu.AddOption( "Hide Bone", "visibility_off", () => SetHidden( target, true, false ) );
				menu.AddOption( "Hide Bone And Children", "layers_clear", () => SetHidden( target, true, true ) )
					.StatusTip = "Hides this bone and everything parented under it";
			}

			menu.AddSeparator();
		}

		var showHidden = menu.AddOption( "Show Hidden Bones", "visibility", () =>
		{
			ShowHiddenBones = !ShowHiddenBones;
			ViewmodelChanged?.Invoke();
		} );

		showHidden.Checkable = true;
		showHidden.Checked = ShowHiddenBones;

		var unhideAll = menu.AddOption( "Unhide All", "restart_alt", () =>
		{
			Rig.HiddenBones.Clear();
			BoneVisibilityChanged?.Invoke();
		} );

		unhideAll.Enabled = Rig.HiddenBones.Count > 0;
		unhideAll.Text = $"Unhide All ({Rig.HiddenBones.Count})";

		menu.OpenAtCursor();
	}

	public Action BoneVisibilityChanged { get; set; }

	private void SetHidden( string bone, bool hidden, bool includeChildren )
	{
		Apply( bone );

		if ( includeChildren )
		{
			foreach ( var child in DescendantsOf( bone ) )
				Apply( child );
		}

		// Hiding the selected bone would otherwise leave its control floating with nothing under
		// it - the dot is gone but the gizmo isn't.
		if ( hidden && !ShowHiddenBones && SelectedBone is { } selected && IsHidden( selected ) )
			Select( null );

		BoneVisibilityChanged?.Invoke();

		void Apply( string name )
		{
			if ( hidden )
			{
				if ( !Rig.HiddenBones.Contains( name ) )
					Rig.HiddenBones.Add( name );

				return;
			}

			Rig.HiddenBones.Remove( name );
		}
	}

	/// <summary>Every bone under this one, at any depth.</summary>
	private IEnumerable<string> DescendantsOf( string parent )
	{
		foreach ( var child in ChildBoneNames( parent ).ToList() )
		{
			yield return child;

			foreach ( var deeper in DescendantsOf( child ) )
				yield return deeper;
		}
	}

	/// <summary>Toggled by the rig_debug_drag console command. Off by default; this logs once per
	/// frame of a drag, which is far too noisy to leave on.</summary>
	public static bool DebugDrag { get; private set; }

	[ConCmd( "rig_debug_drag" )]
	public static void SetDebugDrag( int enabled )
	{
		DebugDrag = enabled != 0;
		Log.Info( $"[rigdrag] drag logging {(DebugDrag ? "ON - drag a bone, then paste the log" : "off")}" );
	}

	public RigViewport( Widget parent ) : base( parent )
	{
		MinimumSize = 200;
		Layout = Layout.Column();

		BuildOverlayBar();

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

	private Button _viewButton;

	/// <summary>A single button at the top-right of the viewport, styled like the scene editor's
	/// own overlay controls, opening a menu of view options. One button rather than a row of
	/// toggles, because all of this is setup you touch once per session and then forget - it
	/// shouldn't cost permanent screen space next to the thing you're actually looking at.</summary>
	private void BuildOverlayBar()
	{
		var bar = Layout.AddRow();
		bar.Margin = new Sandbox.UI.Margin( 6, 4 );
		bar.AddStretchCell();

		_viewButton = bar.Add( new Button( "", "videocam" )
		{
			Clicked = OpenViewMenu,
			ToolTip = "View options - viewmodel framing, camera lock, and what handles are shown"
		} );
	}

	private void OpenViewMenu()
	{
		var menu = new Menu( this );

		menu.AddHeading( "First Person" );

		var usingArms = menu.AddOption( "Using Arms (viewmodel)", "back_hand", () =>
		{
			ViewmodelMode = !ViewmodelMode;

			// Turning it off hands the camera back, otherwise the view stays pinned with no
			// visible reason why.
			if ( !ViewmodelMode )
				LockCameraToView = false;

			ViewmodelChanged?.Invoke();
		} );

		usingArms.Checkable = true;
		usingArms.Checked = ViewmodelMode;
		usingArms.StatusTip = "Frame the model the way it hangs off the camera in game, rather than as a prop in a world";

		var lockCamera = menu.AddOption( "Lock Camera To Player View", "lock", () =>
		{
			LockCameraToView = !LockCameraToView;
			ViewmodelChanged?.Invoke();
		} );

		lockCamera.Checkable = true;
		lockCamera.Checked = LockCameraToView;

		// Gated, as asked: the camera lock is meaningless without something to frame, and an
		// enabled toggle that silently does nothing is worse than a disabled one.
		lockCamera.Enabled = ViewmodelMode;
		lockCamera.StatusTip = ViewmodelMode
			? "Pin the camera to the player's eye. You lose free camera control while this is on."
			: "Turn on Using Arms first";

		menu.AddSeparator();
		menu.AddHeading( "Handles" );

		var wholeModel = menu.AddOption( "Move Whole Model", "open_with", () =>
		{
			MoveWholeModel = !MoveWholeModel;
			ViewmodelChanged?.Invoke();
		} );

		wholeModel.Checkable = true;
		wholeModel.Checked = MoveWholeModel;
		wholeModel.StatusTip = "One gizmo on the model root instead of a handle per bone - for placing the arms relative to the eye";

		var showBones = menu.AddOption( "Show Bone Handles", "scatter_plot", () =>
		{
			ShowBoneHandles = !ShowBoneHandles;
			ViewmodelChanged?.Invoke();
		} );

		showBones.Checkable = true;
		showBones.Checked = ShowBoneHandles;
		showBones.StatusTip = "Hide the per-bone dots when the rig is dense enough that they're in the way";

		menu.AddSeparator();
		menu.AddOption( "Reset Camera", "restart_alt", () =>
		{
			LockCameraToView = false;
			FrameCamera();
			ViewmodelChanged?.Invoke();
		} );

		// OpenAtCursor rather than positioning off the button's rect - the cursor is on the button
		// when this runs, and it's the call every other menu in the tool already uses.
		menu.OpenAtCursor();
	}

	/// <summary>Puts the model where the game puts it: hanging off a camera at the origin. Applied
	/// every frame rather than once, so dragging the root or editing the offset shows immediately.</summary>
	private void ApplyViewmodelFraming()
	{
		if ( !ViewmodelMode )
			return;

		// ONLY WRITTEN WHEN IT ACTUALLY CHANGES. Assigning the renderer's transform every frame
		// re-drives the whole model, which is a plausible way to lose the bone overrides posing
		// depends on - and is pointless work regardless, since the value is usually identical to
		// what's already there.
		// Compared against what WE last applied, rather than against the object's current
		// transform - reading a rotation back and comparing it needs an equality test on
		// quaternions, and Angles is a plain struct that compares exactly.
		if ( _modelObject.IsValid() && (_appliedOffset != ViewmodelOffset || _appliedRotation != ViewmodelRotation) )
		{
			_modelObject.WorldPosition = ViewmodelOffset;
			_modelObject.WorldRotation = ViewmodelRotation.ToRotation();

			_appliedOffset = ViewmodelOffset;
			_appliedRotation = ViewmodelRotation;
		}

		if ( !LockCameraToView )
			return;

		// THE EYE COMES FROM THE MODEL'S OWN CAMERA BONE, not from a number.
		//
		// Viewmodels carry a bone marking where the player's eye sits - first_person_arms_preview
		// has one called "camera", at the model's origin. Reading it means the framing is right
		// for any viewmodel, including ones whose eye isn't at their origin, with nothing to tune.
		//
		// This replaced parking the camera at the world origin and shoving the model down by 8
		// units, a figure lifted from ViewArmsComponent - where it means something quite different,
		// because there the arms hang off the camera object rather than the camera being placed
		// against the arms. The result was the eye sitting 8 units above the actual viewpoint and
		// the arms hidden below the bottom of frame, which reads as the lock doing nothing at all.
		var eye = FindBoneData( "camera" ) is { } cameraBone && _renderer.TryGetBoneTransform( cameraBone, out var boneWorld )
			? boneWorld
			: new Transform( _modelObject.IsValid() ? _modelObject.WorldPosition : Vector3.Zero );

		_camera.WorldPosition = eye.Position;
		_camera.WorldRotation = eye.Rotation;
		_camera.FieldOfView = ViewmodelFov;
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

		// Loading a model while the camera is pinned to the eye must not yank it away - the lock
		// is the more specific intent, so it wins.
		if ( LockCameraToView )
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

	/// <summary>
	/// The bone's pose with no animation on it, read straight off the model.
	///
	/// This is the "no keyframe" value - what a bone looks like when the clip says nothing about
	/// it - and it comes from BoneCollection.Bone.LocalTransform, which is model data and cannot
	/// be affected by anything the renderer is currently showing.
	///
	/// It used to be snapshotted from the live renderer on the first frame after a model loaded,
	/// and that was subtly, badly wrong: opening a clip that already HAS keyframes applies them
	/// before that first frame, so the snapshot captured the posed arm and called it the rest
	/// pose. Undo then restored to a "default" with the forearm already bent, because that WAS
	/// the recorded default. Reading the model has no such timing to get wrong.
	/// </summary>
	private static Transform BindPoseFor( BoneCollection.Bone bone ) => bone.LocalTransform;

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
	private readonly List<GameObject> _referenceObjects = new();
	private List<ReferenceProp> _referenceProps;

	/// <summary>
	/// Rebuilds the static reference models - the switch, the weapon, whatever the hands are
	/// working against.
	///
	/// Rebuilt wholesale when the list changes and only transform-updated otherwise, because
	/// destroying and respawning a model every frame would thrash the scene for no reason. A prop
	/// following a bone is re-read every frame, since the bone moves.
	/// </summary>
	public void SetReferenceProps( List<ReferenceProp> props )
	{
		_referenceProps = props;

		using var scope = _canvas.Scene.Push();

		foreach ( var existing in _referenceObjects )
			existing?.Destroy();

		_referenceObjects.Clear();

		if ( props is null )
			return;

		foreach ( var prop in props )
		{
			if ( prop?.Model is null )
			{
				// A placeholder keeps indices lined up with the list, so the transform pass can
				// pair them up without re-searching.
				_referenceObjects.Add( null );
				continue;
			}

			var go = new GameObject( true, string.IsNullOrWhiteSpace( prop.Name ) ? "reference" : prop.Name );
			var renderer = go.GetOrAddComponent<ModelRenderer>( false );

			renderer.Model = prop.Model;
			renderer.Enabled = true;

			_referenceObjects.Add( go );
		}
	}

	/// <summary>Placement, every frame, so dragging a number in the panel moves the prop while
	/// you watch rather than on some later refresh.</summary>
	private void ApplyReferenceProps()
	{
		if ( _referenceProps is null )
			return;

		for ( var i = 0; i < _referenceProps.Count && i < _referenceObjects.Count; i++ )
		{
			var prop = _referenceProps[i];
			var go = _referenceObjects[i];

			if ( prop is null || !go.IsValid() )
				continue;

			go.Enabled = prop.Visible;

			if ( !prop.Visible )
				continue;

			var local = prop.LocalTransform;

			// Following a bone puts the prop in the hand and keeps it there while the hand moves,
			// which is what you want for a weapon that's already held.
			if ( !string.IsNullOrWhiteSpace( prop.FollowBone ) && TryGetWorldTransform( prop.FollowBone, out var boneWorld ) )
			{
				var followed = boneWorld.ToWorld( local );

				go.WorldPosition = followed.Position;
				go.WorldRotation = followed.Rotation;
				go.WorldScale = followed.Scale;
				continue;
			}

			go.WorldPosition = local.Position;
			go.WorldRotation = local.Rotation;
			go.WorldScale = local.Scale;
		}
	}

	/// <summary>The last pose lookup this viewport was given, kept so a drag can re-resolve the
	/// dragged bone's descendants without the window having to hand it over again.</summary>
	private Func<string, Transform?> _poseLookup;

	/// <summary>
	/// Rewrites every bone under <paramref name="root"/> from its own local pose and the parent's
	/// new world transform.
	///
	/// NEEDED BECAUSE EVERY BONE IS PINNED IN WORLD SPACE. EvaluatePose writes a world-space
	/// override for each bone, and a bone with a world override no longer inherits anything from
	/// its parent - so rotating a shoulder moved the shoulder and left the arm behind. It worked
	/// exactly once, before the children had been pinned, which is what made it look intermittent.
	///
	/// Parent-space writes would avoid this entirely, but SceneModel.SetParentSpaceBone is
	/// internal, so world space is the only way in - and the cost of that is having to carry the
	/// hierarchy ourselves, here.
	/// </summary>
	private void PropagateToDescendants( BoneCollection.Bone root, Transform rootWorld )
	{
		if ( !_renderer.IsValid() )
			return;

		var resolved = new Dictionary<string, Transform> { [root.Name] = rootWorld };

		foreach ( var (bone, world) in LiveBones() )
		{
			if ( bone.Parent is not { } parent || !resolved.TryGetValue( parent.Name, out var parentWorld ) )
				continue;

			// Its own pose is unchanged - only where its parent is has moved. There's always an
			// answer now: a keyframe if the clip has one, the model's bind pose if it doesn't.
			var local = _poseLookup?.Invoke( bone.Name ) ?? BindPoseFor( bone );

			var clamped = RigConstraintSolver.ClampToLimits( Rig, bone.Name, local );
			var worldPose = parentWorld.ToWorld( clamped );

			resolved[bone.Name] = worldPose;
			ApplyWorldTransform( bone, worldPose );
		}
	}

	public void EvaluatePose( Func<string, Transform?> poseForBone )
	{
		_poseLookup = poseForBone;
		_suppressAutoKey = true;

		try
		{
			// EVERY BONE IS DRIVEN EXPLICITLY, from its keyframe if it has one and from the bind
			// pose if it doesn't. Nothing is left to whatever the renderer happens to be holding.
			//
			// Two bugs live here, and the fix for the first caused the second:
			//
			// Posing a bone leaves an override on the renderer. Originally this loop skipped bones
			// with no keyframe, so a bone kept the override from the last time it was dragged even
			// after undo removed its keyframes - the document changed and the screen didn't, which
			// is why undo looked broken.
			//
			// Clearing all overrides first fixed that and broke dragging: the clear also wiped the
			// bone being dragged, which this loop then deliberately skips, so it snapped to bind
			// pose and the drag re-applied it the next frame - flickering between two poses.
			// Clearing was too blunt an instrument. Driving each bone to a known value has neither
			// hole: nothing is ever undefined, so nothing needs wiping.
			// THE HIERARCHY IS RESOLVED HERE, not read back off the renderer.
			//
			// Keyframes are parent-space and the write API takes world space, so each bone needs
			// its parent's world transform to convert. Reading that back from the renderer is
			// wrong: bone writes only land on the next tick, so mid-loop the parent still reports
			// its PREVIOUS pose. Every child was being placed against a stale parent and the
			// error compounded down each chain - which is why undo appeared to mangle the model
			// rather than simply restore the wrong pose.
			//
			// It was survivable while this loop only touched keyframed bones, because a stale
			// parent that isn't itself moving converts correctly. Driving every bone made it
			// obvious.
			//
			// So world transforms are accumulated in a local map as the loop walks down: a bone's
			// parent is whatever THIS pass computed, not whatever the renderer last drew.
			var resolved = new Dictionary<string, Transform>();

			foreach ( var (bone, world) in LiveBones() )
			{
				// The dragged bone keeps its live transform, and must still be resolvable as a
				// parent for anything under it.
				if ( bone.Name == SelectedBone && _draggingBone )
				{
					resolved[bone.Name] = world;
					continue;
				}

				// Keyframe if the clip has one, the model's bind pose if it doesn't. Never
				// undefined, so nothing is left holding a stale override.
				var local = poseForBone( bone.Name ) ?? BindPoseFor( bone );

				var clamped = RigConstraintSolver.ClampToLimits( Rig, bone.Name, local );

				// Skeletons list parents before children, so the parent is normally already
				// resolved. The readback fallback only covers a rig that doesn't, where a stale
				// parent is still better than none.
				var parentWorld = bone.Parent is { } parent
					? (resolved.TryGetValue( parent.Name, out var computed ) ? computed : ParentWorld( bone ))
					: _renderer.WorldTransform;

				var worldPose = parentWorld.ToWorld( clamped );

				resolved[bone.Name] = worldPose;
				ApplyWorldTransform( bone, worldPose );
			}
		}
		finally
		{
			_suppressAutoKey = false;
		}
	}

	/// <summary>SceneRenderingWidget renders its scene but never updates it, so a tool hosting its
	/// own editor scene has to tick it. Bone overrides are only folded into the render pose during
	/// that tick (rig_test_pose: a write reads back stale in the same frame and correct only after
	/// a tick), so without this the model sat frozen and no amount of correct posing could have
	/// shown up.
	///
	/// RealTime.Now/RealTime.Delta, which is what ShaderGraph's Preview passes. This used to run
	/// off a hand-rolled Stopwatch - unnecessary, and a second clock to drift out of step with the
	/// one the rest of the editor animates against.</summary>
	private void TickScene()
	{
		if ( _canvas.Scene is not { } scene )
			return;

		scene.EditorTick( RealTime.Now, RealTime.Delta );
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
		ApplyViewmodelFraming();
		ApplyReferenceProps();

		_gizmoInstance.Input.IsHovered = IsActiveWindow && _canvas.IsUnderMouse;

		// Free-fly is skipped entirely while the camera is locked - otherwise dragging in the
		// viewport fights ApplyViewmodelFraming for the camera every frame and the view judders.
		if ( !LockCameraToView && _gizmoInstance.FirstPersonCamera( _camera, _canvas ) )
			_gizmoInstance.Input.IsHovered = false;

		_canvas.UpdateGizmoInputs( _gizmoInstance.Input.IsHovered );

		// The ground grid is a world-space cue and actively misleading on a viewmodel, where
		// nothing is standing on anything.
		if ( !ViewmodelMode )
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

		// Placing the whole model is its own mode with its own single handle. Bone dots are
		// suppressed while it's on, so there's exactly one thing on screen to grab - the whole
		// reason this mode exists.
		if ( MoveWholeModel )
		{
			DragWholeModel();
			return;
		}

		if ( !ShowBoneHandles )
		{
			// Handles hidden, but the selected bone still gets its control - otherwise turning
			// dots off would silently take away the ability to pose.
			if ( SelectedBone is { } stillSelected && FindBoneData( stillSelected ) is { } stillBone
				&& _renderer.TryGetBoneTransform( stillBone, out var stillWorld ) )
				DragSelectedBone( stillBone, stillWorld );

			return;
		}

		// X-ray the skeleton. Most of a rig sits inside the mesh, so depth-tested dots are both
		// invisible and unclickable - the hitboxes are already depth-biased to the front, so
		// without this the clickable spot and the visible dot disagree.
		Gizmo.Draw.IgnoreDepth = true;

		foreach ( var (bone, world) in LiveBones() )
		{
			// Hidden bones are skipped HERE and nowhere else - EvaluatePose still drives them and
			// their keyframes still play. This is the only place hiding is allowed to mean
			// anything, so it can never cost you animation.
			if ( IsHidden( bone.Name ) && !ShowHiddenBones )
				continue;

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

		// Kept for the right-click menu, which has no gizmo context of its own to hit-test in.
		_hoveredBone = hovered;

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

	private bool _draggingModel;
	private Vector3 _modelDragStart;
	private Vector3 _modelMoveDelta;

	/// <summary>The whole-model handle. Same contract as bone dragging, and the same as
	/// PositionEditorTool: zero in, per-frame delta out, accumulated onto the position captured
	/// when the drag began, with the handle basis passed explicitly.</summary>
	private void DragWholeModel()
	{
		if ( !_modelObject.IsValid() )
			return;

		var start = _draggingModel ? _modelDragStart : _modelObject.WorldPosition;

		using var scope = Gizmo.Scope( "ModelRoot", new Transform( start ) );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = Theme.Green;
		Gizmo.Draw.SolidSphere( 0f, _boneHandleRadius * 0.7f, 8, 8 );
		Gizmo.Draw.IgnoreDepth = false;

		Gizmo.Hitbox.DepthBias = 0.01f;

		if ( !Gizmo.Control.Position( "model-move", Vector3.Zero, out var delta, Rotation.Identity ) )
		{
			if ( !Gizmo.IsLeftMouseDown )
				_draggingModel = false;

			return;
		}

		if ( !_draggingModel )
		{
			_draggingModel = true;
			_modelDragStart = _modelObject.WorldPosition;
			_modelMoveDelta = Vector3.Zero;
			start = _modelDragStart;
		}

		_modelMoveDelta += delta;

		var moved = start + _modelMoveDelta;

		// In viewmodel mode the offset IS the authored value - the model's position is derived
		// from it every frame, so writing the object directly would be overwritten instantly.
		if ( ViewmodelMode )
		{
			ViewmodelOffset = moved;
			ViewmodelChanged?.Invoke();
		}
		else
		{
			_modelObject.WorldPosition = moved;
		}
	}

	private string _dragBoneName;

	// The pose the drag started from, and the accumulated position delta - the same two pieces of
	// state PositionEditorTool keeps (startPoints and moveDelta). Both are reset when a drag
	// begins, and the live transform is never fed back into either.
	private Transform _dragStart;
	private Vector3 _moveDelta;

	/// <summary>Latches the drag's starting pose on its first frame. Called only after a control
	/// has reported movement, so a hover never counts as a drag.</summary>
	private void BeginDrag( BoneCollection.Bone bone, Transform world, ref Transform start )
	{
		if ( _dragBoneName == bone.Name )
			return;

		// Announced before the first write lands, so whatever is listening can record the
		// pre-drag pose.
		BoneDragStarted?.Invoke( bone.Name );

		_dragBoneName = bone.Name;
		_dragStart = world;
		_moveDelta = Vector3.Zero;

		start = world;
	}

	/// <summary>
	/// THE DRAG ENDS WHEN THE BUTTON IS RELEASED, not when the control stops reporting.
	///
	/// Gizmo.Control.Position/Rotate returns false on any frame its value didn't change - which
	/// includes frames where the mouse is still held down but hasn't moved. Treating that as the
	/// end of the drag was the rubberbanding: the anchor was dropped mid-drag and re-taken from
	/// the already-moved position, while the control went on reporting the TOTAL offset since
	/// mouse-down, so the very next frame added that whole offset again. Hold still for one frame
	/// and the bone jumped; keep moving and it behaved. That is exactly the "inconsistent and
	/// rubberbandy" symptom, and it applies to the model-root handle for the same reason.
	///
	/// Safe to read Gizmo.IsLeftMouseDown here: this runs inside an active Gizmo context. Reading
	/// it from outside one - which EvaluatePose used to do - throws.
	/// </summary>
	private void EndDragIfReleased()
	{
		if ( Gizmo.IsLeftMouseDown )
			return;

		EndDrag();
	}

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
	/// THE TWO CONTROLS DO NOT AGREE WITH EACH OTHER, which is the thing to know here. Taken from
	/// the engine's own shipped examples rather than from the parameter names, which mislead:
	///
	///   Position (WidgetGallery PositionTest) is ABSOLUTE IN, ABSOLUTE OUT. You pass the current
	///   position and assign what comes back:  Control.Position( n, pos, out var newPos ).
	///
	///   Rotate (WidgetGallery RotationTest, and the scene editor's RotationEditorTool) hands back
	///   a PER-FRAME DELTA that you accumulate:  rotation *= delta.
	///
	/// Every sensitivity bug in this function came from getting that wrong. Passing Vector3.Zero
	/// for position made the control work against the world origin instead of the bone, so a huge
	/// mouse movement produced a tiny one - "can't move it unless I drag crazy far". Treating the
	/// rotation delta as cumulative against a frozen anchor threw away all but one frame of it.
	/// An earlier version had it the other way round and compounded the whole drag every frame.
	///
	/// There is no frozen anchor any more: absolute-out feeds from the live value, and delta-out
	/// accumulates onto it. That is what the engine does, and it is self-correcting rather than
	/// dependent on state we maintain.</summary>
	private void DragSelectedBone( BoneCollection.Bone bone, Transform world )
	{
		var dragging = _dragBoneName == bone.Name;

		// The start pose is captured on the first frame of the drag and everything is applied to
		// THAT, exactly as PositionEditorTool applies its accumulated delta to startPoints. The
		// live transform is never fed back in, so nothing compounds.
		var start = dragging ? _dragStart : world;

		// Handle basis. Identity means world-aligned arrows, like the scene editor in global
		// space - and it is passed EXPLICITLY, because Control.Position takes the basis as a
		// fourth argument and leaving it out is what made every axis drag along the same one.
		var handleRotation = Rotation.Identity;

		using var scope = Gizmo.Scope( $"BoneControl{bone.Index}", new Transform( start.Position ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		// E is a hold-to-flip, not a toggle - it borrows the other mode for as long as it's down
		// and springs back, so you can nudge a bone's position mid-rotation-pass without losing
		// your place.
		var rotating = (DragMode == BoneDragMode.Rotate) != Editor.Application.IsKeyDown( KeyCode.E );

		Transform newWorld;

		if ( rotating )
		{
			// Rotate's value is CUMULATIVE since the grab - RotationEditorTool assigns it rather
			// than accumulating, and applies it to the start rotation. Position's is per-frame.
			// The two controls genuinely differ; this is not a typo.
			if ( !Gizmo.Control.Rotate( "bone-rotate", Rotation.Identity, out var rotation ) )
			{
				EndDragIfReleased();
				return;
			}

			BeginDrag( bone, world, ref start );

			var basis = handleRotation;
			var applied = basis * rotation * basis.Inverse;

			newWorld = new Transform( start.Position, applied * start.Rotation, start.Scale );
		}
		else
		{
			if ( !Gizmo.Control.Position( "bone-move", Vector3.Zero, out var delta, handleRotation ) )
			{
				EndDragIfReleased();
				return;
			}

			BeginDrag( bone, world, ref start );

			// Per-frame delta, accumulated - "moveDelta += delta" - then applied to the start.
			_moveDelta += delta;

			newWorld = new Transform( start.Position + _moveDelta, start.Rotation, start.Scale );
		}

		_draggingBone = true;

		// rig_debug_drag 1. Answers the one question staring at the code can't: does the write
		// land and stay, or is something else putting the bone back? "wrote" is what this frame
		// asked for; "readback" is what the renderer actually had at the START of this frame,
		// i.e. the result of last frame's write. If readback tracks wrote one frame behind,
		// posing is fine and the problem is elsewhere. If readback never moves, something is
		// overwriting it every frame.
		if ( DebugDrag )
		{
			Log.Info( $"[rigdrag] {bone.Name} mode={(rotating ? "rot" : "pos")} " +
				$"readback={world.Position} wrote={newWorld.Position} " +
				$"viewmodel={ViewmodelMode} locked={LockCameraToView}" );
		}

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

				// Same reason as the plain drag: each solved bone has to carry whatever hangs off
				// it. For an arm IK that's the hand and every finger under the wrist.
				PropagateToDescendants( chainBone, blended );

				NotifyPosed( chainBone, blended );
			}

			return;
		}

		newWorld = ApplyLimits( bone, newWorld );

		ApplyWorldTransform( bone, newWorld );

		// Everything below this bone has to be carried with it - see PropagateToDescendants.
		// Without it, rotating a shoulder rotates only the shoulder.
		PropagateToDescendants( bone, newWorld );

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
