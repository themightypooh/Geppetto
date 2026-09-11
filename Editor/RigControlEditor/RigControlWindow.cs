using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
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
[EditorApp( "Marionette", "accessibility_new", "Pose and keyframe a skinned model's bones, author IK/Limit rig constraints, and place per-frame prop-attach events" )]
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
	private RigLightsPanel _lights;
	private RigCamerasPanel _cameras;
	private RigInspectorPanel _inspector;
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
	/// first-person animation is the gap in s&amp;box nobody has filled.
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

		// THE POSE HAS TO BE APPLIED HERE OR OPENING A CLIP SHOWS NOTHING IT CONTAINS.
		//
		// Loading fills in the document and moves the playhead to 0, but RigTimeline.Playhead's
		// setter deliberately doesn't raise Scrubbed - that fires on user interaction, so that
		// moving the playhead in code can't recurse. The consequence was that nothing ever drove
		// the model after a load: EvaluatePose was never called, every bone kept its bind pose,
		// and a clip you had just saved opened showing none of its own keyframes.
		//
		// That reads exactly like the save silently failing, which is what it was reported as.
		// The keyframes were on disk and loaded correctly the whole time; nothing asked the
		// viewport to use them.
		OnScrub( _timeline.Playhead );
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
		file.AddOption( "Import OBJ...", "file_upload", ImportObj )
			.StatusTip = "Load a Wavefront OBJ into the viewport, one object per o/g group in the file - each one placeable, poseable and keyable on its own";
		file.AddSeparator();
		file.AddOption( "Export Animation...", "file_download", OpenExport )
			.StatusTip = "Compile this clip into a .vmdl you can drop on a SkinnedModelRenderer and play by name";
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
		edit.AddOption( "Copy Keyframes", "content_copy", CopyKeyframes, "rig.copykeys" )
			.StatusTip = "Copy selected keys, or the pose at the playhead if nothing is selected. Survives switching clips.";
		edit.AddOption( "Cut Keyframes", "content_cut", CutKeyframes, "rig.cutkeys" )
			.StatusTip = "Copy the selected keys and remove them";
		edit.AddOption( "Paste Keyframes", "content_paste", PasteKeyframes, "rig.pastekeys" )
			.StatusTip = "Paste copied keys at the playhead, creating tracks as needed. Works in a different animation.";
		edit.AddOption( "Copy Pose at Playhead", "accessibility_new", CopyPoseAtPlayhead, "rig.copypose" )
			.StatusTip = "Snapshot every keyed bone at the playhead. Idle into fire: copy, open the other clip, paste.";
		edit.AddSeparator();
		edit.AddOption( "Delete Selected Keyframe", "delete", () => _timeline.DeleteSelectedKeyframe() )
			.StatusTip = "Same as pressing Delete with a keyframe selected on the Timeline";
		edit.AddSeparator();
		edit.AddOption( "Clear Keyframes on Selection", "clear_all", ClearSelectedBoneKeyframes )
			.StatusTip = "Remove every keyframe from whichever bone or part is selected in the viewport, so it can be re-posed from scratch";
	}

	/// <summary>Wipes one bone's whole track - the fast way to redo a bone that was posed wrong
	/// throughout, instead of hunting down and deleting each of its keyframes by hand.</summary>
	private void ClearSelectedBoneKeyframes()
	{
		if ( _anim is null )
			return;

		var bone = _viewport.SelectedBone ?? _viewport.SelectedReferencePropName;

		if ( bone is null )
			return;

		var track = _viewport.SelectedBone is not null ? _anim.FindTrack( bone ) : _anim.FindPartTrack( bone );
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

		help.AddOption( "Start Animation Tutorial", "school", () =>
		{
			_tutorial.Restart();

			// Starting it should show it. Restarting a tutorial whose panel is closed, and saying
			// nothing, is the kind of dead menu item this tool has had enough of.
			DockManager.SetDockState( "Tutorial", true );
			DockManager.RaiseDock( "Tutorial" );

			RefreshTutorial();
		} ).StatusTip = "Walks through building a reach-and-flip-a-switch animation, one step at a time";

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

		// Same label and icon every other editor window uses - ShaderGraph, AnimGraph,
		// ActionGraph, Sprite, Sound and Rect all have this identical line. Panels are draggable,
		// so sooner or later everyone lands somewhere they didn't intend and can't remember what
		// the default was; matching the wording means people already know what to look for.
		view.AddOption( "Restore To Default", "settings_backup_restore", ResetLayout );
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
		bar.AddOption( new Option( "Export", "file_download", OpenExport )
		{
			ToolTip = "Export this clip for use in game — compiles a .vmdl you can drop on a model and play by name"
		} );
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

		// Painted, not a font icon - see RigIconButton. Red because this is the one control up here
		// that throws something away.
		bar.AddWidget( new RigIconButton( this, RigIcon.Delete, Theme.Red, "",
			"Remove the currently loaded model from the viewport", ClearModel ) );
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
			if ( KeySelectedPart() )
				return;

			RigStatusBar.Show( "Select a bone or a part in the viewport first - there's nothing to key yet" );
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

	/// <summary>Keys the selected object, part or prop where it stands. Holding something still
	/// across a span is the same one-keypress job as holding a bone still.</summary>
	private bool KeySelectedPart()
	{
		if ( _viewport.SelectedReferencePropName is not { } key || string.IsNullOrWhiteSpace( key ) )
			return false;

		if ( LocalTransformOf( key ) is not { } local )
			return false;

		var frame = (int)MathF.Round( _timeline.Playhead );

		_anim.GetOrAddPartTrack( key ).SetKeyframe( frame, local );

		_timeline.Refresh();
		MarkDirty( $"Key {RigTrackName.Display( key )}" );

		RigStatusBar.Show( $"Keyed {RigTrackName.Display( key )} at frame {frame}" );
		return true;
	}

	/// <summary>Where a named object, part or prop is right now, in the space its keyframes are
	/// stored in - a part inside its object, everything else in the world.</summary>
	private Transform? LocalTransformOf( string key )
	{
		var (owner, part) = _anim.FindPartTarget( key );

		if ( part is not null )
			return part.LocalTransform;

		// Exact name only - FindPartTarget hands back the owner for "door/nonsense" as well, and
		// that must not read as the door.
		if ( owner is not null && owner.Name == key )
			return owner.LocalTransform;

		if ( _anim.ReferenceProps?.FirstOrDefault( p => p?.Name == key ) is { } prop )
			return prop.LocalTransform;

		return null;
	}

	/// <summary>The other half of LocalTransformOf: stores a transform on a named object, part or
	/// prop, in the same space.</summary>
	private void SetLocalTransformOf( string key, Transform local )
	{
		var (owner, part) = _anim.FindPartTarget( key );

		if ( part is not null )
		{
			part.Position = local.Position;
			part.Rotation = local.Rotation.Angles();
			part.Scale = local.Scale.x;
			return;
		}

		if ( owner is not null && owner.Name == key )
		{
			owner.Position = local.Position;
			owner.Rotation = local.Rotation.Angles();
			owner.Scale = local.Scale.x;
			return;
		}

		if ( _anim.ReferenceProps?.FirstOrDefault( p => p?.Name == key ) is { } prop )
		{
			prop.Position = local.Position;
			prop.Rotation = local.Rotation.Angles();
			prop.Scale = local.Scale.x;
		}
	}

	/// <summary>
	/// The Inspector's view of a selected object, part or prop.
	///
	/// Every delegate looks the thing up by name when it runs, rather than closing over the object
	/// found now - undo swaps the document's objects for copies, and a sheet bound to the old one
	/// would go on editing something nothing draws.
	/// </summary>
	private MovableTransform MovableFor( string key )
	{
		if ( _anim is null )
			return null;

		var (owner, part) = _anim.FindPartTarget( key );
		var isObject = part is null && owner is not null && owner.Name == key;
		var isProp = part is null && !isObject && _anim.ReferenceProps?.Any( p => p?.Name == key ) == true;

		if ( part is null && !isObject && !isProp )
			return null;

		ReferenceProp PropNamed() => _anim.ReferenceProps?.FirstOrDefault( p => p?.Name == key );

		return new MovableTransform
		{
			Kind = part is not null ? "Part" : isObject ? "Object" : "Prop",

			ReadLocal = () => LocalTransformOf( key ),

			WriteLocal = local =>
			{
				SetLocalTransformOf( key, local );

				// Same gate as a drag: with Link on, typing a number keys it at the playhead; off,
				// it only poses.
				if ( _viewport.AutoKeyEnabled )
					OnPartPosed( key, local );
			},

			GetFollowBone = part is not null ? null : () => _anim.FindObject( key )?.FollowBone ?? PropNamed()?.FollowBone ?? "",

			SetFollowBone = part is not null ? null : bone =>
			{
				if ( _anim.FindObject( key ) is { } o )
					o.FollowBone = bone;
				else if ( PropNamed() is { } p )
					p.FollowBone = bone;
			},
		};
	}

	/// <summary>
	/// The objects panel has changed the document - added, renamed, hidden, duplicated or deleted
	/// something - and everything that shows objects has to catch up before the undo step lands.
	/// </summary>
	private void OnObjectsChanged( string label )
	{
		if ( _anim is null )
			return;

		UpdateDocumentFolder();

		// Rebuilt, not re-placed: the list itself changed.
		_viewport.SetReferenceProps( _anim.ReferenceProps );
		_viewport.SetObjects( _anim.Objects );

		_bones?.Rebuild();

		// A rename or a delete moves or removes tracks, and the timeline draws tracks.
		if ( _viewport.SelectedBone is null && _viewport.SelectedReferencePropName is null )
			_timeline.SelectedBone = null;

		_timeline.Refresh();

		// The rebuilt objects are placed from their stored fields; anything keyed goes back to
		// where this frame says it is.
		OnScrub( _timeline.Playhead );

		MarkDirty( label );
	}

	[Shortcut( "rig.copykeys", "CTRL+C", ShortcutType.Window )]
	private void CopyKeyframes()
	{
		if ( _timeline is null )
			return;

		var n = _timeline.CopyKeyframes();

		if ( n == 0 )
		{
			RigStatusBar.Show( "Nothing to copy — key some bones, or select keyframes on the timeline" );
			return;
		}

		RigStatusBar.Show( n == 1
			? "Copied 1 keyframe — open another clip and Ctrl+V to paste"
			: $"Copied {n} keyframes — open another clip and Ctrl+V to paste" );
	}

	[Shortcut( "rig.cutkeys", "CTRL+X", ShortcutType.Window )]
	private void CutKeyframes()
	{
		if ( _timeline is null )
			return;

		if ( !_timeline.HasSelectedKeyframe )
		{
			RigStatusBar.Show( "Select keyframes on the timeline to cut" );
			return;
		}

		_timeline.CutKeyframes();
		RigStatusBar.Show( "Cut selected keyframes" );
	}

	[Shortcut( "rig.pastekeys", "CTRL+V", ShortcutType.Window )]
	private void PasteKeyframes()
	{
		if ( _timeline is null )
			return;

		var n = _timeline.PasteKeyframes();

		if ( n == 0 )
		{
			RigStatusBar.Show( "Clipboard is empty — copy keyframes from this clip or another first" );
			return;
		}

		var frame = (int)MathF.Round( _timeline.Playhead );
		RigStatusBar.Show( n == 1
			? $"Pasted 1 keyframe at frame {frame}"
			: $"Pasted {n} keyframes at frame {frame}" );
	}

	[Shortcut( "rig.copypose", "CTRL+SHIFT+C", ShortcutType.Window )]
	private void CopyPoseAtPlayhead()
	{
		if ( _timeline is null )
			return;

		var n = _timeline.CopyPoseAtPlayhead();

		if ( n == 0 )
		{
			RigStatusBar.Show( "No keyed bones to copy a pose from" );
			return;
		}

		RigStatusBar.Show( $"Copied pose ({n} bones) — open another clip and Ctrl+V to paste" );
	}

	private void ToggleDragMode()
	{
		_viewport.DragMode = _viewport.DragMode switch
		{
			BoneDragMode.Rotate => BoneDragMode.Move,
			BoneDragMode.Move => BoneDragMode.Scale,
			_ => BoneDragMode.Rotate
		};

		UpdateDragModeOption();
	}

	private void UpdateDragModeOption()
	{
		if ( _dragModeOption is null ) return;

		_dragModeOption.Icon = _viewport.DragMode switch
		{
			BoneDragMode.Rotate => "3d_rotation",
			BoneDragMode.Move => "open_with",
			_ => "zoom_out_map"
		};

		_dragModeOption.ToolTip = _viewport.DragMode switch
		{
			BoneDragMode.Rotate => "Dragging a bone rotates it. Hold E to move instead. Rotation is what you want for almost all posing - joints pivot, they don't slide.",
			BoneDragMode.Move => "Dragging a bone moves it. Hold E to rotate instead. Moving a bone stretches the skin, so it's mainly for root and IK-target bones.",
			_ => "Dragging a bone scales it uniformly. Scaling stretches the skin - use it for squash-and-stretch, not for normal posing."
		};
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

			_inspector?.Refresh();

			RefreshTutorial();
		};
		_viewport.BonePosed += OnBonePosed;
		_viewport.BoneDragStarted += OnBoneDragStarted;
		_viewport.BoneDragEnded += OnBoneDragEnded;

		// Same three-signal shape as a bone drag, for the same reason: the move fires every frame
		// and would bury the undo stack under hundreds of one-pixel entries if it recorded there.
		_viewport.ReferencePropDragStarted += () =>
		{
			_undoStack.Push( _baseline?.WithLabel( "Move Reference Prop" ) );
			_baseline = RigSnapshot.Capture( _anim, _rig );
			UpdateUndoOptions();
		};

		// NOT a panel rebuild - this fires every frame of a drag, and rebuilding a tree view that
		// often both costs more than the drag does and clears the selection out from under it. The
		// panel catches up when the drag ends.
		_viewport.ReferencePropMoved += MarkDirtyOnly;

		_viewport.ReferencePropDragEnded += () =>
		{
			_bones?.Rebuild();
			ResetBaseline();
		};

		_viewport.ReferencePropPosed += OnPartPosed;

		_viewport.ReferencePropSelected += part =>
		{
			if ( _timeline is not null )
			{
				_timeline.SelectedBone = part;
				_timeline.Refresh();
			}

			// Back the other way too: picking something in the 3D view marks its row in the
			// objects tree and puts its numbers in the Inspector, the same as picking a bone does.
			_bones?.ObjectsPanel?.ShowSelection( part );
			_inspector?.Refresh();
		};

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
			BoneRowSelected = SelectTrackTarget,
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

		_bones.ObjectsPanel.ImportRequested = ImportObj;
		_bones.ObjectsPanel.Changed = OnObjectsChanged;

		_constraints = new RigConstraintsPanel( this )
		{
			Edited = () => MarkDirty( "Edit Constraint" ),
		};

		// The viewport is rebuilt from the list on every edit rather than the panel touching the
		// scene itself - same split as the objects panel, and the reason a light can be undone
		// like anything else.
		_lights = new RigLightsPanel( this, _viewport )
		{
			Changed = label =>
			{
				_viewport.SetLights( _anim?.Lights );
				MarkDirty( label );
			},
		};

		_cameras = new RigCamerasPanel( this, _viewport )
		{
			Changed = label =>
			{
				_viewport.SetCameras( _anim?.Cameras );
				MarkDirty( label );
			},
		};

		// SetLocalTransform fires BonePosed on its way through, so the keyframe and the dirty flag
		// are already handled by the time this runs. All that's left is the undo step - one per
		// field edit, unlike a drag, which is one per drag.
		_inspector = new RigInspectorPanel( this, _viewport )
		{
			Edited = () => MarkDirty( "Edit Transform" ),
			MovableFor = MovableFor,
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
		DockManager.RegisterDock( new() { Title = "Inspector", Icon = "tune", Area = DockArea.Hidden, CreateAction = () => _inspector } );
		DockManager.RegisterDock( new() { Title = "Lights", Icon = "lightbulb", Area = DockArea.Hidden, CreateAction = () => _lights } );
		DockManager.RegisterDock( new() { Title = "Cameras", Icon = "videocam", Area = DockArea.Hidden, CreateAction = () => _cameras } );

		// THE EDITOR'S OWN CONSOLE, not one of ours.
		//
		// Half the tool's diagnostics go to the log - rig_test_pose, rig_debug_drag, the sample
		// and wave builders - and reading them meant leaving the tool for the main editor window,
		// which on a second monitor is fine and on one monitor means losing sight of the thing you
		// are debugging.
		//
		// ConsoleWidget isn't publicly constructible, so it's created by name through the type
		// library. That's not a workaround - it's exactly what ShaderGraph does to dock the same
		// widget (ShaderGraph/Code/MainWindow.cs:1180), and it means this is the real console with
		// real command input, not a log view that reimplements a third of one.
		if ( EditorTypeLibrary.Create( "ConsoleWidget", typeof( Widget ), new object[] { this } ) is Widget console )
			DockManager.RegisterDock( new() { Title = "Console", Icon = "text_snippet", Area = DockArea.Hidden, CreateAction = () => console } );
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
		// Bumped again for the Inspector dock, for the same reason the Tutorial bump was needed:
		// a layout saved under the old cookie has no Inspector in it, and a dock that only exists
		// in BuildDefaultLayout never appears for anyone who has opened the tool before.
		// Bumped again for the Tutorial moving out of the tab group into its own row, and the new
		// splitter proportions. Both live entirely in the saved layout, so a restored Marionette3
		// layout would keep the old arrangement and BuildDefaultLayout would never run again.
		// Bumped again for the taller timeline row, and again for the Console dock.
		StateCookie = "Marionette6";

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
		// THE TUTORIAL IS ITS OWN DOCK, NOT A TAB. Tabbed alongside the property sheets it was
		// either covering them or being covered by them, so following a step meant flipping back
		// and forth between the instruction and the panel the instruction is about. Below them in
		// the same column, both are on screen at once, which is the only arrangement where a
		// written step and the fields it refers to are usable together.
		var bonesDock = DockManager.OpenDock( "BonesObject", DockArea.Right, _centralDock );

		// A quarter of the width. Narrower than the 0.62/0.38 it replaced, which was sized for a
		// column that had to hold the tutorial's prose as well; the tutorial now has its own space
		// underneath, so the sheets only need to fit label-and-field rows.
		DockManager.SetSplitterProportions( bonesDock, 0.75f, 0.25f );

		// Center, not Right - Right would split the space into three columns (the bug that
		// shipped first); Center stacks a dock as a tab alongside whatever's already there.
		DockManager.OpenDock( "Inspector", DockArea.Center, bonesDock );

		// AnimEvents and Constraints are registered but deliberately NOT opened here. Both are
		// for work that comes after you can already pose and key - and four tabs across a quarter
		// width column truncates every one of their titles. They're one click away in View.
		DockManager.RaiseDock( "BonesObject" );

		if ( RigTutorial.OpenOnStartup )
		{
			var tutorialDock = DockManager.OpenDock( "Tutorial", DockArea.Bottom, bonesDock );
			DockManager.SetSplitterProportions( tutorialDock, 0.5f, 0.5f );
		}

		// A third of the height, up from a fifth. The timeline is where the actual work of timing
		// happens and it was the most cramped thing on screen - the viewport had space to spare.
		var timeline = DockManager.OpenDock( "Timeline", DockArea.Bottom, _centralDock );
		DockManager.SetSplitterProportions( timeline, 0.67f, 0.33f );

		// Tabbed behind the timeline rather than beside it. The console is for when something has
		// gone wrong or a builder command has been run - worth one click, not worth permanent
		// space next to the thing you use every second.
		DockManager.OpenDock( "Console", DockArea.Center, timeline );
		DockManager.RaiseDock( "Timeline" );
	}

	private void ApplyRigToPanels()
	{
		_constraints.SetRig( _rig );
		_viewport.Rig = _rig;

		// Before the props are built - a prop drawn from an OBJ beside the clip cannot be found
		// without knowing where the clip is.
		UpdateDocumentFolder();

		_viewport.SetReferenceProps( _anim?.ReferenceProps );
		_viewport.SetObjects( _anim?.Objects );
		_viewport.SetLights( _anim?.Lights );
		_lights?.SetAnim( _anim );
		_viewport.SetCameras( _anim?.Cameras );
		_cameras?.SetAnim( _anim );

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

		// The numbers follow the gizmo. Guarded on its side against writing them back, so a drag
		// updating the fields can't turn into the fields re-posing the bone mid-drag.
		_inspector?.Refresh();

		// No undo step here - this fires every frame of a drag, and the whole drag was already
		// recorded as one step by OnBoneDragStarted.
		MarkDirtyOnly();
	}

	/// <summary>A dragged part is keyed exactly like a dragged bone - same frame, same track shape,
	/// same one-undo-step-per-drag rule.</summary>
	private void OnPartPosed( string part, Transform local )
	{
		if ( _anim is null )
			return;

		_anim.GetOrAddPartTrack( part ).SetKeyframe( (int)MathF.Round( _timeline.Playhead ), local );

		_timeline.Refresh();
		_inspector?.Refresh();

		MarkDirtyOnly();
	}

	/// <summary>A timeline row is a bone or a part, and clicking it has to select the right kind of
	/// thing - a part row that selected a bone of the same name would silently hand the gizmo to
	/// something else.</summary>
	private void SelectTrackTarget( string name )
	{
		if ( _anim?.FindPartTrack( name ) is not null && _viewport.SelectReferenceProp( name ) )
		{
			_inspector?.Refresh();
			return;
		}

		_viewport.Select( name );
	}

	private void OnScrub( float frame )
	{
		if ( _anim is null )
			return;

		// PARTS FIRST, THEN BONES. A part's bones are posed in world space, converted against the
		// object's own transform - so the object has to be where this frame says it is before its
		// skeleton is placed against it. The other order lags a rigged prop's bones one frame
		// behind the prop, which on a scrub looks like the weapon coming apart.
		//
		// Parts are on the same playhead as the bones - a door has to be open at the frame the
		// hand that opened it is on, or the clip is only half scrubbed.
		_viewport.ApplyPartPose( part => _anim.FindPartTrack( part ) is { } track && track.Keyframes.Count > 0 ? track.Evaluate( frame ) : null );

		_viewport.EvaluatePose( bone => _anim.FindTrack( bone ) is { } track && track.Keyframes.Count > 0 ? track.Evaluate( frame ) : null );

		// Scrubbing changes the pose without any drag, so nothing else would update the fields.
		_inspector?.Refresh();
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
		_bones.SetAsset( _asset, _anim );
		_bones.Rebuild();
		_constraints.Rebuild();

		// Undo can add or remove whole objects - an import, a delete - so the viewport's copies
		// have to be rebuilt from the restored lists, not just re-placed.
		_viewport.SetReferenceProps( _anim?.ReferenceProps );
		_viewport.SetObjects( _anim?.Objects );
		_viewport.SetLights( _anim?.Lights );
		_lights?.Rebuild();

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

	private void UpdateTitle() => Title = $"Marionette - {_asset?.Path ?? "nothing open"}{(_dirty ? "*" : "")}";

	/// <summary>
	/// The missing button. There was a way to get a clip into a compiled model — add it to
	/// Effigy's clip list and compile THAT model — but nothing in this window said so, and the
	/// common case (posing the first-person arms) is not an Effigy model at all.
	/// </summary>
	private void OpenExport()
	{
		if ( _anim is null )
			return;

		if ( _anim.SourceModel is null )
		{
			RigStatusBar.Show( "Set a Model in BonesObject first — export has to know which skeleton this clip is for" );
			return;
		}

		if ( _anim.BoneTracks is null || !_anim.BoneTracks.Any( t => t.Keyframes.Count > 0 ) )
		{
			RigStatusBar.Show( "Nothing to export — key some bones first" );
			return;
		}

		if ( _asset is null && !PickSaveLocation() )
			return;

		if ( _dirty )
			Save();

		new RigAnimExportDialog( this, _anim, _asset, _timeline?.Looping ?? true ).Show();
	}

	/// <summary>
	/// Bring in a Wavefront OBJ as one object, with one part per lump of the file.
	///
	/// SPLIT, NOT WELDED. An exporter that kept a door, its handle and its hinge as separate
	/// objects wrote that structure into the file as o/g markers, and it is the only thing that
	/// makes them animatable separately - fused into one mesh they are one object that can only
	/// move as one. Effigy's reader already splits on those markers; this is that reader pointed
	/// at Marionette's prop list.
	/// </summary>
	private void ImportObj()
	{
		if ( _anim is null )
			return;

		var dialog = new FileDialog( null )
		{
			Title = "Import OBJ",
			DefaultSuffix = ".obj",
			Directory = Project.Current?.GetAssetsPath() ?? "",
		};

		dialog.SetFindFile();
		dialog.SetModeOpen();
		dialog.SetNameFilter( "Wavefront OBJ (*.obj)" );

		if ( !dialog.Execute() )
			return;

		ImportObjFile( dialog.SelectedFile );
	}

	/// <summary>Where the clip lives, for resolving a prop's mesh file. Re-read rather than cached
	/// once, because the first save of a blank document gives the clip a home it did not have when
	/// the window opened.</summary>
	private void UpdateDocumentFolder()
	{
		_viewport.DocumentFolder = _asset?.AbsolutePath is { } clipPath
			? Path.GetDirectoryName( clipPath )
			: null;
	}

	private void ImportObjFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
		{
			RigStatusBar.Show( "That file is not there any more" );
			return;
		}

		// Re-read rather than trust this session's cache: importing a file you have just
		// re-exported from Blender has to show the new mesh, not the one we happened to read first.
		RigObjMeshes.Forget( path );

		var pieces = RigObjMeshes.Pieces( path );

		if ( pieces.Count == 0 )
		{
			RigStatusBar.Show( $"{Path.GetFileName( path )} has no geometry in it - see the console for why" );
			return;
		}

		var stored = CopyBesideClip( path ) ?? path;
		var stem = Path.GetFileNameWithoutExtension( path );

		_anim.Objects ??= new List<RigObject>();

		// ONE OBJECT, ONE PART PER LUMP. The file is the thing - a door - and its o/g markers are
		// the pieces of that thing. Importing them as separate top-level objects would throw away
		// the one piece of structure the exporter took the trouble to record: that they belong
		// together and are placed together.
		var owner = new RigObject
		{
			Name = RigObjectsPanel.UniqueObjectName( _anim, stem ),
			ObjSource = stored,
			Visible = true,
		};

		var taken = new HashSet<string>();

		foreach ( var piece in pieces )
		{
			if ( piece.Mesh is null || piece.Mesh.FaceCount == 0 )
				continue;

			// A file with no markers comes back as one unnamed piece - it is the file, so it is
			// named after the file. Names are how a track finds its part, so they are made unique
			// here rather than being allowed to collide silently.
			// The separator is taken out of the part's NAME (ObjPart keeps the file's own), since a
			// slash there would read as an object and a part and the track would be filed wrong.
			var wanted = string.IsNullOrWhiteSpace( piece.Name ) ? stem : piece.Name.Replace( RigTrackName.Separator, '_' );
			var name = RigObjectsPanel.Unique( wanted, taken );

			owner.Parts.Add( new RigObjectPart
			{
				Name = name,
				ObjPart = piece.Name,
				Visible = true,
			} );
		}

		if ( owner.Parts.Count == 0 )
		{
			RigStatusBar.Show( $"{Path.GetFileName( path )} has no faces to import" );
			return;
		}

		_anim.Objects.Add( owner );

		UpdateDocumentFolder();

		_viewport.SetObjects( _anim.Objects );
		_bones?.SetAsset( _asset, _anim );
		_bones?.Rebuild();

		MarkDirty( $"Import {Path.GetFileName( path )}" );

		// Selected on arrival, so its row is marked, its numbers are in the Inspector and the
		// gizmo is on it - the next thing anybody does with a fresh import is put it somewhere.
		_viewport.SelectReferenceProp( owner.Name );

		RigStatusBar.Show( owner.Parts.Count == 1
			? $"Imported {owner.Name} - drag it in the viewport, then press K to key it"
			: $"Imported {owner.Name} with {owner.Parts.Count} parts - each one moves and keys on its own" );
	}

	/// <summary>
	/// A copy of the OBJ beside the .riganim, so the clip does not depend on where the file
	/// happened to be the day it was imported.
	///
	/// The same reasoning as Effigy's ImportSidecar, and the same shape: one folder named after the
	/// document. Returns the path to store - relative to the clip - or null when there is nowhere
	/// to put it yet, in which case the caller falls back to the absolute path and the clip works
	/// on this machine until it is saved and re-imported.
	/// </summary>
	private string CopyBesideClip( string source )
	{
		var clip = _asset?.AbsolutePath;

		if ( string.IsNullOrWhiteSpace( clip ) )
		{
			RigStatusBar.Show( "Save the clip to keep its meshes beside it - until then it points at the file where it is" );
			return null;
		}

		try
		{
			var folder = Path.GetFileNameWithoutExtension( clip ) + ".meshes";
			var absoluteFolder = Path.Combine( Path.GetDirectoryName( clip ) ?? "", folder );

			Directory.CreateDirectory( absoluteFolder );

			var name = Path.GetFileName( source );
			var target = Path.Combine( absoluteFolder, name );

			// A second, different file of the same name must not overwrite the first - some other
			// prop is already drawing from it, and it would silently become a different mesh.
			for ( var n = 2; File.Exists( target ) && !SameFile( source, target ); n++ )
			{
				name = $"{Path.GetFileNameWithoutExtension( source )}_{n}{Path.GetExtension( source )}";
				target = Path.Combine( absoluteFolder, name );
			}

			if ( !SameFile( source, target ) )
				File.Copy( source, target, true );

			return $"{folder}/{name}";
		}
		catch ( Exception e )
		{
			// Not fatal: the import still works off the original path. Worth saying, because the
			// clip is now less portable than it looks.
			Log.Warning( $"[Marionette] could not copy {source} beside the clip: {e.Message}" );
			return null;
		}
	}

	private static bool SameFile( string source, string target )
	{
		if ( !File.Exists( target ) )
			return false;

		var a = new FileInfo( source );
		var b = new FileInfo( target );

		return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
	}

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

		// A first save gives the clip a folder, which is what a prop's relative mesh path is
		// relative to.
		UpdateDocumentFolder();

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
