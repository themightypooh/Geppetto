using Editor;
using Marionette;
using Sandbox;
using System;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The dockable RigControlEditor window - a 3D viewport with per-bone FK gizmos, a per-bone
/// keyframe timeline beneath it, and a tabbed right panel (BonesObject / AnimEvents /
/// Constraints). Edits a RigAnimDocument (.riganim); its Rig Asset Path points at a separate
/// RigDocument (.ctrlrig) that owns the model reference for posing and the IK/Limit constraints,
/// so several clips can share one rig. Built directly on DockWindow/IAssetEditor rather than
/// AnimGraph's own DocumentWindow base (ClipWindow.cs, SkeletonWindow.cs) - that base's undo/
/// dirty machinery lives in the AnimGraph package itself and isn't referenceable from project
/// code, so this re-does the smaller save/dirty part it actually needs.
/// </summary>
[EditorForAssetType( "riganim" )]
[EditorApp( "Rig Control Editor", "accessibility_new", "Pose and keyframe a skinned model's bones, author IK/Limit rig constraints, and place per-frame prop-attach events" )]
public sealed class RigControlWindow : DockWindow, IAssetEditor
{
	public bool CanOpenMultipleAssets => false;

	private Asset _asset;
	private RigAnimDocument _anim;
	private RigDocument _rig;
	private Model _lastModel;
	private bool _dirty;

	private RigViewport _viewport;
	private RigTimeline _timeline;
	private RigEventProperties _events;
	private RigBonesPanel _bones;
	private RigConstraintsPanel _constraints;
	private DockWidget _centralDock;

	private Option _saveOption;

	// Opened from the Tools menu (EditorApp) as well as by double-clicking a .riganim asset
	// (EditorForAssetType) - the docks have to exist before either path can populate them, so
	// they're built here with a blank in-memory document rather than inside AssetOpen, the same
	// shape SpriteEditor's own Window.cs uses.
	/// <summary>
	/// The Citizen first-person arms, from the base citizen addon - so it's present for everyone
	/// rather than being something of mine.
	///
	/// A blank document opens with these already loaded. Opening an animation tool to an empty
	/// black viewport tells you nothing and makes the first move "go find a model", which is the
	/// least interesting decision in the process. Arms are also what most people are here for:
	/// first-person animation is the gap in s&box nobody has filled.
	/// </summary>
	public const string DefaultModelPath = "models/first_person/first_person_arms_preview.vmdl";

	public RigControlWindow()
	{
		DeleteOnClose = true;
		// Roomier by default. The side panels were pinched enough at 1400 that step text and
		// property names clipped, and a tool whose first impression is truncated labels reads as
		// broken before anyone has used it.
		Size = new Vector2( 1760, 1040 );
		SetWindowIcon( "accessibility_new" );

		_anim = new RigAnimDocument
		{
			SourceModel = Model.Load( DefaultModelPath )
		};

		BuildMenuBar();

		// Docks first - BuildToolbar sets its buttons' initial icon state immediately (e.g.
		// UpdateLinkOption reads _viewport.AutoKeyEnabled), so _viewport/_timeline have to exist
		// before it runs. Building it first was the bug: null _viewport, NullReferenceException
		// straight out of the constructor.
		BuildDocks();
		BuildToolbar();
		BuildEditMenu();
		BuildViewMenu();
		BuildHelpMenu();
		BuildStatusBar();

		ResetDirty();
		ResetBaseline();
		Show();

		ShowTutorialIfWanted();
	}

	/// <summary>
	/// Opens the Tutorial dock on startup unless the reader has opted out.
	///
	/// Done HERE rather than only in BuildDefaultLayout, because that method only runs when there
	/// is no saved layout to restore. Anyone who has opened the tool before has one - and if the
	/// Tutorial dock was closed in it, the default layout never runs again and nothing ever
	/// reopens the panel. It simply stops existing, with no way to tell that from it being
	/// broken. SetDockState works against the restored layout too, so this covers both cases.
	/// </summary>
	private void ShowTutorialIfWanted()
	{
		if ( !RigTutorial.OpenOnStartup )
			return;

		DockManager.SetDockState( "Tutorial", true );
		DockManager.RaiseDock( "Tutorial" );

		RefreshTutorial();
	}

	public void AssetOpen( Asset asset )
	{
		if ( asset is null )
			return;

		Raise();
		LoadAsset( asset );
	}

	public void SelectMember( string memberName )
	{
	}

	private void LoadAsset( Asset asset )
	{
		_asset = asset;

		if ( !asset.TryLoadResource( out _anim ) || _anim is null )
			_anim = new RigAnimDocument();

		LoadRig();

		_lastModel = _anim.SourceModel;
		_viewport.SetModel( _lastModel );
		ApplyRigToPanels();

		_timeline.SetAsset( _anim );
		_events.SetAsset( _anim );
		_bones.SetAsset( _asset, _anim );
		_bones.Rebuild();

		ResetDirty();

		// History belongs to the document that was open. Carrying it across a load would let
		// Ctrl+Z paste the previous clip's tracks into this one.
		_undoStack.Clear();
		ResetBaseline();
	}

	private void OpenPicker()
	{
		var picker = AssetPicker.Create( null, AssetType.FromType( typeof( RigAnimDocument ) ), new AssetPicker.PickerOptions() );
		picker.Title = "Open Rig Animation";
		picker.OnAssetPicked = assets =>
		{
			if ( assets.FirstOrDefault() is { } asset )
				LoadAsset( asset );
		};
		picker.Show();
	}

	private void LoadRig()
	{
		_rig = _anim.RigAsset;
	}

	private void BuildMenuBar()
	{
		var file = MenuBar.FindOrCreateMenu( "File" );
		file.Clear();
		file.AddOption( "New", "common/new.png", New );
		file.AddOption( "Open...", "folder_open", OpenPicker, "editor.open" );
		file.AddSeparator();
		_saveOption = file.AddOption( "Save", "common/save.png", Save, "editor.save" );
		file.AddSeparator();
		file.AddOption( "Close", "close", Close );
	}

	private void BuildEditMenu()
	{
		var edit = MenuBar.FindOrCreateMenu( "Edit" );

		_undoOption = edit.AddOption( "Undo", "undo", Undo, "editor.undo" );
		_redoOption = edit.AddOption( "Redo", "redo", Redo, "editor.redo" );
		UpdateUndoOptions();

		edit.AddSeparator();
		edit.AddOption( "Delete Selected Keyframe", "delete", () => _timeline.DeleteSelectedKeyframe() )
			.StatusTip = "Same as pressing Delete with a keyframe selected on the Timeline";
		edit.AddSeparator();
		edit.AddOption( "Clear Keyframes on Selected Bone", "clear_all", ClearSelectedBoneKeyframes )
			.StatusTip = "Remove every keyframe from whichever bone is selected in the viewport, so it can be re-posed from scratch";
	}

	/// <summary>Wipes one bone's whole track - the fast way to redo a bone that was posed wrong
	/// throughout, instead of hunting down and deleting each of its keyframes by hand.</summary>
	private void ClearSelectedBoneKeyframes()
	{
		if ( _anim is null || _viewport.SelectedBone is not { } bone )
			return;

		var track = _anim.FindTrack( bone );
		if ( track is null || track.Keyframes.Count == 0 )
			return;

		track.Keyframes.Clear();

		_timeline.Refresh();
		MarkDirty( $"Clear Keyframes on {bone}" );
	}

	// There was no way to get a closed dock back before this - close BonesObject and it was
	// gone for the rest of the session. My first attempt (FindDockWidget/OpenDock by hand) was
	// wrong - the real pattern, confirmed from ShaderGraph's and AnimGraph's own shipped View
	// menus, is DockTypes + IsDockOpen + SetDockState: a checkable option per registered dock
	// that DockManager itself knows how to open/close correctly, including re-attaching one
	// that was fully closed.
	private RigStatusBar _statusBar;
	private RigTutorialPanel _tutorialPanel;
	private readonly RigTutorial _tutorial = new();

	/// <summary>Hover hints only. The tutorial used to live in here too and was unreadable - see
	/// RigTutorialPanel.</summary>
	private void BuildStatusBar()
	{
		// Window.StatusBar, not Layout.Add - a DockWindow has no Layout of its own (the dock
		// manager owns the window's whole client area), so adding to it threw a
		// NullReferenceException straight out of the constructor.
		_statusBar = new RigStatusBar( this );
		StatusBar = _statusBar;

		RefreshTutorial();
	}

	/// <summary>Steps tick themselves off by watching the document, so this runs after anything
	/// that could have satisfied one.</summary>
	private void RefreshTutorial()
	{
		_tutorial.Evaluate( _anim, _viewport?.SelectedBone );
		_tutorialPanel?.Rebuild();
	}

	private void BuildHelpMenu()
	{
		var help = MenuBar.FindOrCreateMenu( "Help" );

		help.AddOption( "Start Wave Tutorial", "school", () =>
		{
			_tutorial.Restart();

			// Starting it should show it. Restarting a tutorial whose panel is closed, and saying
			// nothing, is the kind of dead menu item this tool has had enough of.
			DockManager.SetDockState( "Tutorial", true );
			DockManager.RaiseDock( "Tutorial" );

			RefreshTutorial();
		} ).StatusTip = "Walks through building a simple wave animation, one step at a time";

		help.AddOption( "Dismiss Tutorial", "close", () =>
		{
			_tutorial.Dismiss();
			_tutorialPanel?.Rebuild();
		} );
	}

	private void BuildViewMenu()
	{
		var view = MenuBar.FindOrCreateMenu( "View" );

		// A floating Asset Browser, not an embedded one - AssetBrowser's bare constructor throws
		// (BuildLocationsPanel NullReferenceException, confirmed from the exception itself, not
		// a guess) because it expects setup MainAssetBrowser's own constructor provides and a
		// plain `new AssetBrowser(this)` skips. CreateFloating goes through that real path instead.
		view.AddOption( "Open Assets Browser", "folder_open", () => MainAssetBrowser.CreateFloating() );
		view.AddSeparator();

		foreach ( var dock in DockManager.DockTypes )
		{
			var option = view.AddOption( dock.Title, dock.Icon );
			option.Checkable = true;
			option.Checked = DockManager.IsDockOpen( dock.Title );
			option.Toggled += b => DockManager.SetDockState( dock.Title, b );
		}
	}

	private Option _linkOption;
	private Option _dragModeOption;

	private void BuildToolbar()
	{
		var bar = new ToolBar( this, "RigControlToolbar" );
		bar.SetIconSize( 24 );
		AddToolBar( bar, ToolbarPosition.Top );

		bar.AddOption( new Option( "New", "common/new.png", New ) { ToolTip = "New" } );
		bar.AddOption( new Option( "Open", "common/open.png", OpenPicker ) { ToolTip = "Open" } );
		bar.AddOption( new Option( "Save", "common/save.png", Save ) { ToolTip = "Save", ShortcutName = "editor.save" } );
		bar.AddSeparator();

		_linkOption = bar.AddOption( new Option( "Link", "link", ToggleAutoKey )
		{
			ToolTip = "Auto-Key - posing a bone writes a keyframe at the playhead. Off: pose without keying."
		} );
		UpdateLinkOption();
		bar.AddSeparator();

		// The explicit "make a keyframe" action. Until this existed the only way to create one was
		// as a side effect of dragging a bone with auto-key on - which works, but is invisible:
		// nothing on screen said keyframes were a thing you could make on purpose.
		// The key button itself lives on the Timeline's transport, not here - keying happens AT a
		// frame, so it belongs beside the controls that choose the frame. The shortcut (K) is
		// still registered on the window so it works wherever focus is.
		//
		// Transport (play, step, loop, speed, frame rate) deliberately does NOT live here - it's
		// all on the Timeline's own bar, next to the thing it moves. This toolbar is for actions
		// on the document and the posing tool itself.
		_dragModeOption = bar.AddOption( new Option( "Drag Mode", "3d_rotation", ToggleDragMode ) );
		UpdateDragModeOption();
		bar.AddSeparator();

		bar.AddOption( new Option( "Clear Model", "delete_forever", ClearModel )
		{
			ToolTip = "Remove the currently loaded model from the viewport"
		} );
	}

	private void ToggleAutoKey()
	{
		_viewport.AutoKeyEnabled = !_viewport.AutoKeyEnabled;
		UpdateLinkOption();
	}

	private void UpdateLinkOption()
	{
		if ( _linkOption is null ) return;
		_linkOption.Icon = _viewport.AutoKeyEnabled ? "link" : "link_off";
	}

	/// <summary>Keys whichever bone is selected at the playhead, from its pose right now.
	///
	/// Deliberately keys the CURRENT pose rather than requiring you to nudge the bone first, so
	/// holding a pose across a span - the ordinary way you stop a limb drifting between two other
	/// keys - is one keypress rather than a fake drag.</summary>
	[Shortcut( "rig.keybone", "K", ShortcutType.Window )]
	private void KeySelectedBone()
	{
		if ( _anim is null )
			return;

		if ( _viewport.SelectedBone is not { } bone )
		{
			RigStatusBar.Show( "Select a bone in the viewport first - there's nothing to key yet" );
			return;
		}

		if ( !_viewport.TryGetLocalTransform( bone, out var local ) )
			return;

		var frame = (int)MathF.Round( _timeline.Playhead );

		_anim.GetOrAddTrack( bone ).SetKeyframe( frame, local );

		_timeline.Refresh();
		MarkDirty( $"Key {bone}" );

		RigStatusBar.Show( $"Keyed {bone} at frame {frame}" );
	}

	private void ToggleDragMode()
	{
		_viewport.DragMode = _viewport.DragMode == BoneDragMode.Rotate ? BoneDragMode.Move : BoneDragMode.Rotate;
		UpdateDragModeOption();
	}

	private void UpdateDragModeOption()
	{
		if ( _dragModeOption is null ) return;

		var rotate = _viewport.DragMode == BoneDragMode.Rotate;

		_dragModeOption.Icon = rotate ? "3d_rotation" : "open_with";
		_dragModeOption.ToolTip = rotate
			? "Dragging a bone rotates it. Hold E to move instead. Rotation is what you want for almost all posing - joints pivot, they don't slide."
			: "Dragging a bone moves it. Hold E to rotate instead. Moving a bone stretches the skin, so it's mainly for root and IK-target bones.";
	}

	private void New()
	{
		void Proceed()
		{
			_asset = null;

			// Same default as opening the tool: a new clip starts with something to pose.
			_anim = new RigAnimDocument
			{
				SourceModel = Model.Load( DefaultModelPath )
			};

			_rig = null;
			_lastModel = null;

			ApplyRigToPanels();
			_timeline.SetAsset( _anim );
			_events.SetAsset( _anim );
			_bones.SetAsset( _asset, _anim );
			_bones.Rebuild();

			ResetDirty();

			_undoStack.Clear();
			ResetBaseline();
		}

		if ( !_dirty )
		{
			Proceed();
			return;
		}

		var confirm = new PopupWindow( "Unsaved Changes",
			$"\"{_asset?.Name ?? "untitled"}\" has unsaved changes. Would you like to save now?", "Cancel",
			new System.Collections.Generic.Dictionary<string, System.Action>
			{
				{ "Don't Save", Proceed },
				{ "Save", () => { Save(); Proceed(); } }
			} );

		confirm.Show();
	}

	private void BuildDocks()
	{
		_viewport = new RigViewport( this );
		_viewport.BoneSelected += bone =>
		{
			_bones?.Rebuild();

			// Selection is one shared idea, not one per panel - picking a bone anywhere highlights
			// its row on the timeline too, and clicking a timeline row selects it in the viewport.
			if ( _timeline is not null )
			{
				_timeline.SelectedBone = bone;
				_timeline.Refresh();
			}

			RefreshTutorial();
		};
		_viewport.BonePosed += OnBonePosed;
		_viewport.BoneDragStarted += OnBoneDragStarted;
		_viewport.BoneDragEnded += OnBoneDragEnded;

		// Hiding edits the .ctrlrig, so it's a real document change - dirty, saved, and undoable
		// like any other. The bones panel rebuilds so its tree can show what's hidden.
		_viewport.BoneVisibilityChanged += () =>
		{
			_bones?.Rebuild();
			MarkDirty( "Change Bone Visibility" );
		};

		_centralDock = DockManager.SetCentralWidget( _viewport );

		_timeline = new RigTimeline( this )
		{
			Scrubbed = OnScrub,
			Edited = () => MarkDirty( "Timeline Edit" ),
			KeyRequested = KeySelectedBone,
			BoneRowSelected = bone => _viewport.Select( bone ),
		};

		_events = new RigEventProperties( this )
		{
			Edited = () => MarkDirty( "Edit Anim Event" ),
		};

		_bones = new RigBonesPanel( this, _viewport )
		{
			Edited = () =>
			{
				LoadRig();

				// ApplyRigToPanels respawns the reference props, which is what picks up a model
				// being assigned to one.
				ApplyRigToPanels();
				MarkDirty( "Edit Rig Source" );
			},
		};

		_constraints = new RigConstraintsPanel( this )
		{
			Edited = () => MarkDirty( "Edit Constraint" ),
		};

		_tutorialPanel = new RigTutorialPanel( this )
		{
			Tutorial = _tutorial,
			Changed = RefreshTutorial,

			// Opening the dock the step is talking about, rather than naming it and hoping.
			RevealPanel = title =>
			{
				DockManager.SetDockState( title, true );
				DockManager.RaiseDock( title );
			},
		};

		DockManager.RegisterDock( new() { Title = "Tutorial", Icon = "school", Area = DockArea.Hidden, CreateAction = () => _tutorialPanel } );
		DockManager.RegisterDock( new() { Title = "AnimEvents", Icon = "bolt", Area = DockArea.Hidden, CreateAction = () => _events } );
		DockManager.RegisterDock( new() { Title = "BonesObject", Icon = "polyline", Area = DockArea.Hidden, CreateAction = () => _bones } );
		DockManager.RegisterDock( new() { Title = "Constraints", Icon = "link", Area = DockArea.Hidden, CreateAction = () => _constraints } );
		DockManager.RegisterDock( new() { Title = "Timeline", Icon = "view_timeline", Area = DockArea.Hidden, CreateAction = () => _timeline } );

		// Bumped from "RigControlEditor" - the blank-window bug shipped its first broken layout
		// (nothing opened) under that cookie, and would otherwise keep restoring that empty state
		// forever instead of ever calling BuildDefaultLayout again.
		// Bumped for the Tutorial dock. A saved layout under the old cookie has no Tutorial in it,
		// and DockWindow only calls BuildDefaultLayout when there's nothing to restore - so
		// without this, the new panel would silently never appear for anyone who had opened the
		// tool before.
		// Bumped again for the wider default split. Splitter proportions live in the saved layout,
		// so without a new cookie anyone who has already opened the tool keeps the narrow columns
		// forever and never sees the change.
		StateCookie = "Marionette2";

		_lastModel = _anim.SourceModel;
		_viewport.SetModel( _lastModel );

		ApplyRigToPanels();

		_timeline.SetAsset( _anim );
		_events.SetAsset( _anim );
		_bones.SetAsset( _asset, _anim );
		_bones.Rebuild();
	}

	// Registering a dock (RegisterDock) only tells the DockManager it exists - actually placing
	// it on screen has to happen here. DockWindow calls this itself once there's no saved layout
	// for StateCookie to restore instead; calling OpenDock inline in the constructor (the bug
	// that shipped first) registers docks nobody ever tells to open.
	protected override void BuildDefaultLayout()
	{
		var bonesDock = DockManager.OpenDock( "BonesObject", DockArea.Right, _centralDock );

		// 0.62/0.38 rather than 0.72/0.28. The right column holds the tutorial and the property
		// sheets, all of which are text - and text is what suffers first when a panel is narrow.
		// The viewport loses a little width and cares far less.
		DockManager.SetSplitterProportions( bonesDock, 0.62f, 0.38f );

		// Center, not Right - Right would split the space into three columns (the bug that
		// shipped first); Center stacks a dock as a tab alongside whatever's already there.
		DockManager.OpenDock( "AnimEvents", DockArea.Center, bonesDock );
		DockManager.OpenDock( "Constraints", DockArea.Center, bonesDock );

		// Tabbed alongside the others, and raised only if the reader hasn't opted out. It's the
		// one panel that's useless if nobody notices it - and equally, the one that becomes an
		// irritant if it insists after being told no.
		DockManager.OpenDock( "Tutorial", DockArea.Center, bonesDock );

		if ( RigTutorial.OpenOnStartup )
			DockManager.RaiseDock( "Tutorial" );
		else
			DockManager.RaiseDock( "BonesObject" );

		var timeline = DockManager.OpenDock( "Timeline", DockArea.Bottom, _centralDock );
		DockManager.SetSplitterProportions( timeline, 0.65f, 0.35f );
	}

	private void ApplyRigToPanels()
	{
		_constraints.SetRig( _rig );
		_viewport.Rig = _rig;
		_viewport.SetReferenceProps( _anim?.ReferenceProps );

		// SourceModel on the clip itself wins; the rig's own model is only a fallback for a clip
		// that hasn't set one yet. Previously this only ever updated the viewport in the fallback
		// case (SourceModel is null) - setting Model directly in BonesObject never reached the
		// viewport at all, which is why it stayed blank after picking a model there.
		var wanted = _anim.SourceModel ?? _rig?.SourceModel;

		if ( wanted != _lastModel )
		{
			_lastModel = wanted;
			_viewport.SetModel( _lastModel );
		}
	}

	/// <summary>Toolbar's Clear Model - drops the loaded model (and everything the viewport
	/// spawned for it) without touching keyframes/events already authored, so a broken or wrong
	/// test model can be backed out of instead of being stuck staring at it.</summary>
	private void ClearModel()
	{
		if ( _anim is not null )
			_anim.SourceModel = null;

		_lastModel = null;
		_viewport.SetModel( null );

		_bones.SetAsset( _asset, _anim );
		_bones.Rebuild();
		MarkDirty( "Clear Model" );
	}

	private void OnBonePosed( string bone, Transform local )
	{
		if ( _anim is null )
			return;

		var track = _anim.GetOrAddTrack( bone );
		track.SetKeyframe( (int)MathF.Round( _timeline.Playhead ), local );

		_timeline.Refresh();

		// No undo step here - this fires every frame of a drag, and the whole drag was already
		// recorded as one step by OnBoneDragStarted.
		MarkDirtyOnly();
	}

	private void OnScrub( float frame )
	{
		if ( _anim is null )
			return;

		_viewport.EvaluatePose( bone => _anim.FindTrack( bone ) is { } track && track.Keyframes.Count > 0 ? track.Evaluate( frame ) : null );
	}

	private void Scrub( float frame ) => _timeline.Playhead = frame;

	// UNDO
	//
	// The panels mutate the document and then tell us about it, so by the time MarkDirty runs the
	// pre-edit state is already gone. _baseline is that missing piece: a snapshot of the last
	// committed state, kept up to date, pushed onto the stack when an edit lands and immediately
	// re-taken. Snapshotting inside MarkDirty instead would record the RESULT of the edit, and
	// undo would restore the thing you were trying to undo.
	private readonly RigUndoStack _undoStack = new();
	private RigSnapshot _baseline;

	private Option _undoOption;
	private Option _redoOption;

	private void ResetBaseline()
	{
		_baseline = RigSnapshot.Capture( _anim, _rig );
		UpdateUndoOptions();
	}

	/// <summary>A bone drag fires BonePosed every frame, so undo is recorded once here at the
	/// start and suppressed until the drag ends - one drag, one undo step.</summary>
	private void OnBoneDragStarted( string bone )
	{
		_undoStack.Push( _baseline?.WithLabel( $"Pose {bone}" ) );
		_baseline = RigSnapshot.Capture( _anim, _rig );
		UpdateUndoOptions();
	}

	/// <summary>The drag's result becomes the new baseline, so the next edit undoes back to the
	/// posed state rather than to before the drag.</summary>
	private void OnBoneDragEnded() => ResetBaseline();

	// ShortcutType.Window, matching ShaderGraph's MainWindow. Without it the shortcut registers at
	// the wrong scope and never reaches this window - which is its own reason Ctrl+Z appeared to
	// do nothing, entirely separate from whether undo itself worked.
	[Shortcut( "editor.undo", "CTRL+Z", ShortcutType.Window )]
	private void Undo()
	{
		var restored = _undoStack.Undo( RigSnapshot.Capture( _anim, _rig ) );
		if ( restored is null )
			return;

		ApplyRestoredSnapshot( restored );
	}

	// CTRL+Y, which is what the editor's own asset editors bind redo to. CTRL+SHIFT+Z was my
	// habit, not this editor's convention.
	[Shortcut( "editor.redo", "CTRL+Y", ShortcutType.Window )]
	private void Redo()
	{
		var restored = _undoStack.Redo( RigSnapshot.Capture( _anim, _rig ) );
		if ( restored is null )
			return;

		ApplyRestoredSnapshot( restored );
	}

	private void ApplyRestoredSnapshot( RigSnapshot snapshot )
	{
		snapshot.RestoreTo( _anim, _rig );

		// The document changed underneath every panel, so all of them are stale.
		_timeline.Refresh();
		_events.SetAsset( _anim );
		_bones.Rebuild();
		_constraints.Rebuild();

		// And the viewport is still showing the pose from before the undo.
		OnScrub( _timeline.Playhead );

		_baseline = RigSnapshot.Capture( _anim, _rig );

		_dirty = true;
		UpdateTitle();

		if ( _saveOption is not null )
			_saveOption.Enabled = true;

		UpdateUndoOptions();
	}

	private void UpdateUndoOptions()
	{
		if ( _undoOption is not null )
		{
			_undoOption.Enabled = _undoStack.CanUndo;
			_undoOption.Text = _undoStack.CanUndo ? $"Undo {_undoStack.UndoLabel}" : "Undo";
		}

		if ( _redoOption is not null )
		{
			_redoOption.Enabled = _undoStack.CanRedo;
			_redoOption.Text = _undoStack.CanRedo ? $"Redo {_undoStack.RedoLabel}" : "Redo";
		}
	}

	/// <summary>Records an undo step, then marks the document dirty.</summary>
	private void MarkDirty( string undoLabel = "Edit" )
	{
		_undoStack.Push( _baseline?.WithLabel( undoLabel ) );
		_baseline = RigSnapshot.Capture( _anim, _rig );
		UpdateUndoOptions();

		MarkDirtyOnly();
	}

	/// <summary>Marks dirty WITHOUT recording an undo step - for edits already covered by one.
	///
	/// This replaced a _suppressUndo flag that MarkDirty checked. The flag was set when a bone
	/// drag started and cleared when it ended, and any path where a drag stopped without its end
	/// firing left it stuck on, silently disabling undo for the rest of the session. A flag whose
	/// failure mode is "undo quietly stops existing" is the wrong mechanism; whether an edit is
	/// part of a larger action is known at the call site, so the call site picks.</summary>
	private void MarkDirtyOnly()
	{
		_dirty = true;
		UpdateTitle();

		if ( _saveOption is not null )
			_saveOption.Enabled = true;

		RefreshTutorial();
	}

	private void ResetDirty()
	{
		_dirty = false;
		UpdateTitle();

		if ( _saveOption is not null )
			_saveOption.Enabled = false;
	}

	private void UpdateTitle() => Title = $"Rig Control - {_asset?.Path ?? "nothing open"}{(_dirty ? "*" : "")}";

	[Shortcut( "editor.save", "CTRL+S", ShortcutType.Window )]
	private void Save()
	{
		if ( _anim is null )
			return;

		// A window opened blank (Tools menu, or New) has no backing file - Save silently did
		// nothing here before, since _asset is only ever set inside LoadAsset. This is the
		// "saving doesn't actually function" bug: there was no Save As for that case at all.
		if ( _asset is null && !PickSaveLocation() )
			return;

		_asset.SaveToDisk( _anim );

		if ( _rig is not null )
		{
			var rigAsset = AssetSystem.FindByPath( _rig.ResourcePath );
			rigAsset?.SaveToDisk( _rig );
		}

		ResetDirty();

		MainAssetBrowser.Instance?.Local.UpdateAssetList();
	}

	/// <summary>First save of a blank document - prompts for where to put it, creates the
	/// .riganim there, and points _asset at it so every save after this is a normal overwrite.</summary>
	private bool PickSaveLocation()
	{
		var fd = new FileDialog( null )
		{
			Title = "Save Rig Animation As...",
			DefaultSuffix = ".riganim",
			Directory = Project.Current?.GetAssetsPath() ?? "",
		};

		fd.SelectFile( "untitled.riganim" );
		fd.SetFindFile();
		fd.SetModeSave();
		fd.SetNameFilter( "Rig Animation (*.riganim)" );

		if ( !fd.Execute() )
			return false;

		var created = AssetSystem.CreateResource( "riganim", fd.SelectedFile );
		if ( created is null )
			return false;

		_asset = created;
		UpdateTitle();

		return true;
	}

	protected override bool OnClose()
	{
		if ( !_dirty )
			return true;

		var confirm = new PopupWindow( "Unsaved Changes",
			$"\"{_asset?.Name ?? "untitled"}\" has unsaved changes. Would you like to save now?", "Cancel",
			new System.Collections.Generic.Dictionary<string, System.Action>
			{
				{ "Don't Save", () => { _dirty = false; Close(); } },
				{ "Save", () => { Save(); Close(); } }
			} );

		confirm.Show();
		return false;
	}
}
