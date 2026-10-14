using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The Grip tab - authoring a .grip: where the hand sits on a prop, and what the fingers do.
///
/// A GRIP IS ITS OWN ASSET, NOT PART OF THE CLIP. One grip is reused by every clip and every game
/// object that holds that prop, so it opens, saves and creates itself here the way the window does
/// a .riganim, and the clip never stores it. The clip is only the stage: its reference props are
/// what you author against, and its pose is what Capture reads.
///
/// CAPTURE, DON'T TYPE. Every number in a grip is a pose you can already make in the viewport -
/// pose the hand on the prop, click Capture Hand; curl the fingers, click Capture Fingers. The
/// sheets below are for nudging what was captured, not for building it.
///
/// PREVIEW DOES NOT KEY. Scrubbing the parameter poses the fingers through EvaluatePose, the same
/// non-keying path a timeline scrub uses, so looking at a grip never writes the clip. Key Fingers
/// is the one button that does, and says so. Scrubbing the timeline puts the clip's own pose back.
///
/// BLENDING IS THE RUNTIME'S. The preview calls GripAsset.Evaluate and the contact close calls
/// RigGripSolver - the exact code RigGripComponent runs - so what you see here is what the game
/// does, not a second implementation that agrees with it most of the time.
/// </summary>
internal sealed class RigGripPanel : Widget
{
	private readonly RigViewport _viewport;
	private readonly Widget _body;
	private readonly Editor.Label _title;
	private readonly Editor.Label _status;
	private readonly FloatSlider _slider;
	private readonly Editor.Label _sliderValue;

	private RigAnimDocument _anim;
	private GripAsset _grip;
	private Asset _asset;
	private bool _dirty;
	private string _propName;
	private GripPose _selected;

	/// <summary>Raised after any edit, with the undo label. Grip edits change the .grip, not the
	/// clip; only "Key Grip Fingers" writes keyframes into the clip.</summary>
	public Action<string> Changed { get; set; }

	public RigGripPanel( Widget parent, RigViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		Name = "Grip";
		WindowTitle = "Grip";
		SetWindowIcon( "pan_tool" );

		Layout = Layout.Column();

		Layout.Add( RigHelpBox.Create( this,
			"How one hand holds one prop, saved as its own .grip asset: the hand's place on the " +
			"prop, and finger poses along a 0..1 parameter. RigGripComponent plays it in game.",
			new[]
			{
				RigHelpBox.S( "Capture Hand",
					"Pose the hand where it belongs on the prop, pick that prop under Author " +
					"Against, click Capture Hand. The hand is stored in the prop's space, so the " +
					"game can put it on the prop wherever the prop is." ),

				RigHelpBox.S( "Poses",
					"Each pose is every finger's rotation at one place on the parameter; between " +
					"two the fingers blend. One pose is a static grip. Several are a grip that " +
					"changes - a trigger pulling, a wallet folding. Curl the fingers, then Capture " +
					"Fingers on the pose." ),

				RigHelpBox.S( "Preview and Key Fingers",
					"The slider poses the fingers from the grip without touching the clip. Key " +
					"Fingers writes what you see as keyframes at the playhead." ),

				RigHelpBox.S( "Solve Contact",
					"Closes the previewed fingers onto the prop's mesh, skin first, the way live " +
					"contact does in game. Capture Fingers afterwards to keep the result." ),
			} ) );

		var bar = Layout.AddRow();
		bar.Margin = 4;
		bar.Spacing = 4;
		_title = bar.Add( new Editor.Label( "No grip" ), 1 );
		bar.Add( new Button( "New", "note_add" ) { Clicked = () => Guard( New ), ToolTip = "Start a new, unsaved grip" } );
		bar.Add( new Button( "Open", "folder_open" ) { Clicked = () => Guard( Open ), ToolTip = "Open a .grip asset" } );
		bar.Add( new Button( "Save", "save" ) { Clicked = () => Save(), ToolTip = "Save the grip to its .grip asset" } );

		var preview = Layout.AddRow();
		preview.Margin = 4;
		preview.Spacing = 4;
		preview.Add( new Editor.Label( "Preview" ) { FixedWidth = 52 } );

		_slider = new FloatSlider( this )
		{
			Minimum = 0f,
			Maximum = 1f,
			Step = 0.01f,
			Value = 0f,
			ToolTip = "Pose the fingers from the grip at this parameter - not keyed"
		};

		_slider.OnValueEdited = () => Preview();
		preview.Add( _slider, 1 );

		_sliderValue = preview.Add( new Editor.Label( "0.00" ) { FixedWidth = 32 } );

		var actions = Layout.AddRow();
		actions.Margin = 4;
		actions.Spacing = 4;
		actions.Add( new Button( "Key Fingers", "key" )
		{
			ToolTip = "Write the previewed finger pose into the clip as keyframes at the playhead",
			Clicked = KeyFingers
		} );
		actions.Add( new Button( "Solve Contact", "front_hand" )
		{
			ToolTip = "Close the previewed fingers onto the prop's mesh, as live contact does",
			Clicked = SolveContact
		} );
		actions.AddStretchCell();

		_status = Layout.Add( new Editor.Label( "" ) { Enabled = false } );
		_status.Margin = 4;
		_status.WordWrap = true;

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		_body = new Widget( this ) { Layout = Layout.Column() };
		_body.Layout.Margin = 4;
		_body.Layout.Spacing = 4;
		scroll.Canvas = _body;

		Rebuild();
	}

	/// <summary>The clip whose reference props and objects the grip is authored against.</summary>
	public void SetAnim( RigAnimDocument anim )
	{
		_anim = anim;

		if ( PropNames().All( n => n != _propName ) )
			_propName = PropNames().Contains( _viewport?.SelectedReferencePropName ) ? _viewport.SelectedReferencePropName : PropNames().FirstOrDefault();

		Rebuild();
	}

	// ---- the asset ---------------------------------------------------------------------------

	private void New()
	{
		_grip = new GripAsset { SourceModel = _viewport?.Renderer?.Model };
		_asset = null;
		_selected = null;
		_dirty = true;
		AdoptProp();
		Rebuild();
		Changed?.Invoke( "New Grip" );
	}

	private void Open()
	{
		var fd = new FileDialog( null )
		{
			Title = "Open Grip",
			DefaultSuffix = ".grip",
			Directory = Project.Current?.GetAssetsPath() ?? "",
		};

		fd.SetFindFile();
		fd.SetModeOpen();
		fd.SetNameFilter( "Marionette Grip (*.grip)" );

		if ( !fd.Execute() )
			return;

		var asset = AssetSystem.FindByPath( fd.SelectedFile );

		if ( asset is null || !asset.TryLoadResource( out GripAsset grip ) || grip is null )
		{
			Say( "That file is not a grip the asset system knows about." );
			return;
		}

		_grip = grip;
		_asset = asset;
		_selected = _grip.Poses.FirstOrDefault();
		_dirty = false;
		Rebuild();
	}

	/// <summary>Same shape as the window's Save: a grip with no file yet is given one first, and
	/// every save after that is a plain overwrite.</summary>
	private bool Save()
	{
		if ( _grip is null )
			return false;

		if ( _asset is null )
		{
			var fd = new FileDialog( null )
			{
				Title = "Save Grip As...",
				DefaultSuffix = ".grip",
				Directory = Project.Current?.GetAssetsPath() ?? "",
			};

			fd.SelectFile( "untitled.grip" );
			fd.SetFindFile();
			fd.SetModeSave();
			fd.SetNameFilter( "Marionette Grip (*.grip)" );

			if ( !fd.Execute() )
				return false;

			_asset = AssetSystem.CreateResource( "grip", fd.SelectedFile );

			if ( _asset is null )
				return false;
		}

		_asset.SaveToDisk( _grip );
		_dirty = false;
		UpdateTitle();

		MainAssetBrowser.Instance?.Local.UpdateAssetList();
		return true;
	}

	/// <summary>Swapping grips with unsaved work asks first - the grip is not on the clip, so the
	/// window's own unsaved-changes prompt never sees it.</summary>
	private void Guard( Action then )
	{
		if ( !_dirty || _grip is null )
		{
			then();
			return;
		}

		new PopupWindow( "Unsaved Grip", "This grip has unsaved changes.", "Cancel",
			new Dictionary<string, Action>
			{
				{ "Discard", then },
				{ "Save", () => { if ( Save() ) then(); } },
			} ).Show();
	}

	private void Edit( string label, bool rebuild = false )
	{
		_dirty = true;
		UpdateTitle();

		if ( rebuild )
			Rebuild();

		Changed?.Invoke( label );
	}

	private void UpdateTitle() =>
		_title.Text = _grip is null ? "No grip" : $"{_asset?.Name ?? "untitled"}.grip{(_dirty ? " *" : "")}";

	private void Say( string text ) => _status.Text = text;

	// ---- the sheet ---------------------------------------------------------------------------

	public void Rebuild()
	{
		_body.Layout.Clear( true );
		UpdateTitle();

		if ( _grip is null )
		{
			_body.Layout.Add( new Editor.Label( "Open a .grip, or start a new one." ) { Enabled = false } );
			return;
		}

		_body.Layout.Add( BuildProp() );

		var serialized = EditorTypeLibrary.GetSerializedObject( _grip );
		serialized.OnPropertyChanged += _ => Edit( "Edit Grip" );

		var sheet = new ControlSheet();
		sheet.AddObject( serialized, p => p.Name is nameof( GripAsset.SourceModel ) or nameof( GripAsset.Side )
			or nameof( GripAsset.Prop ) or nameof( GripAsset.ExtraModels ) or nameof( GripAsset.HandInProp ) );
		_body.Layout.Add( sheet );

		_body.Layout.Add( Heading( "Contact" ) );

		var contact = EditorTypeLibrary.GetSerializedObject( _grip.Contact ??= new GripContact() );
		contact.OnPropertyChanged += _ => Edit( "Edit Grip Contact" );

		var contactSheet = new ControlSheet();
		contactSheet.AddObject( contact );
		_body.Layout.Add( contactSheet );

		var poses = _body.Layout.AddRow();
		poses.Spacing = 4;
		poses.Add( Heading( "Poses" ), 1 );
		poses.Add( new Button( "Add Pose", "add" ) { Clicked = AddPose, ToolTip = "A new pose at the preview parameter" } );

		foreach ( var pose in _grip.Poses.Where( p => p is not null ).OrderBy( p => p.Parameter ).ToList() )
			_body.Layout.Add( BuildPose( pose ) );

		if ( _grip.Poses.Count == 0 )
			_body.Layout.Add( new Editor.Label( "No poses. Curl the fingers, Add Pose, Capture Fingers." ) { Enabled = false } );

		_body.Layout.AddStretchCell();
	}

	private static Editor.Label Heading( string text )
	{
		var label = new Editor.Label( text );
		label.SetStyles( "font-weight: bold;" );
		return label;
	}

	/// <summary>Which prop in the viewport the hand is captured against, and the button that
	/// captures it.</summary>
	private Widget BuildProp()
	{
		var row = new Widget( _body ) { Layout = Layout.Row() };
		row.Layout.Spacing = 4;

		row.Layout.Add( new Editor.Label( "Author Against" ) { FixedWidth = 90 } );

		var pick = new Button( _propName ?? "(no props in clip)", "category" )
		{
			ToolTip = "The reference prop or object in the viewport the hand is placed on"
		};

		pick.Clicked = () =>
		{
			var menu = new Editor.Menu( this );

			foreach ( var name in PropNames() )
			{
				menu.AddOption( name, name == _propName ? "check" : null, () =>
				{
					_propName = name;
					AdoptProp();
					Edit( "Grip Prop", rebuild: true );
				} );
			}

			if ( !PropNames().Any() )
				menu.AddOption( "Add a reference prop to the clip first", null, null ).Enabled = false;

			menu.OpenAtCursor();
		};

		row.Layout.Add( pick, 1 );
		row.Layout.Add( new Button( "Capture Hand", "back_hand" )
		{
			ToolTip = "Store the viewport's hand bone, relative to this prop, as Hand In Prop",
			Clicked = CaptureHand
		} );

		return row;
	}

	private Widget BuildPose( GripPose pose )
	{
		var panel = new Widget( _body ) { Layout = Layout.Column() };
		panel.Layout.Margin = 6;
		panel.Layout.Spacing = 4;

		var header = panel.Layout.AddRow();
		header.Spacing = 6;

		var chip = new Editor.Label( "[pose]" ) { Color = pose == _selected ? Theme.Green : Theme.Blue };
		chip.SetStyles( "font-weight: bold;" );
		header.Add( chip );

		var title = header.Add( new Editor.Label( $"{pose.Name}  @ {pose.Parameter:0.00}" ) { Enabled = false }, 1 );
		header.Add( new Editor.Label( $"{pose.Bones.Count} bones" ) { Enabled = false } );

		var serialized = EditorTypeLibrary.GetSerializedObject( pose );

		// Not rebuilt per change: rebuilding mid-drag of the Parameter slider would tear the slider
		// out from under the mouse. The header is the only thing showing the edited values.
		serialized.OnPropertyChanged += _ =>
		{
			title.Text = $"{pose.Name}  @ {pose.Parameter:0.00}";
			Edit( "Edit Grip Pose" );
		};

		var sheet = new ControlSheet();
		sheet.AddObject( serialized, p => p.Name != nameof( GripPose.Bones ) );
		panel.Layout.Add( sheet );

		var row = panel.Layout.AddRow();
		row.Spacing = 4;

		row.Add( new Button( "Preview", "visibility" )
		{
			ToolTip = "Pose the fingers exactly as this pose has them - not keyed",
			Clicked = () =>
			{
				_selected = pose;
				_slider.Value = pose.Parameter;
				ShowPose( pose );
			}
		} );

		row.Add( new Button( "Capture Fingers", "pan_tool" )
		{
			ToolTip = "Store the viewport's current finger rotations into this pose",
			Clicked = () => CaptureFingers( pose )
		} );

		row.AddStretchCell();

		row.Add( new Button( "Delete", "delete" )
		{
			Clicked = () =>
			{
				_grip.Poses.Remove( pose );

				if ( _selected == pose )
					_selected = null;

				Edit( "Delete Grip Pose", rebuild: true );
			}
		} );

		return panel;
	}

	private void AddPose()
	{
		var existing = _grip.Poses.Select( p => p?.Name ).ToHashSet();
		var index = 0;

		while ( existing.Contains( $"pose {index}" ) )
			index++;

		var pose = new GripPose { Name = $"pose {index}", Parameter = _slider.Value };
		_grip.Poses.Add( pose );
		_selected = pose;
		Edit( "Add Grip Pose", rebuild: true );
	}

	// ---- the prop ----------------------------------------------------------------------------

	private IEnumerable<string> PropNames()
	{
		if ( _anim is null )
			return Enumerable.Empty<string>();

		var props = _anim.ReferenceProps?.Where( p => p is not null && !string.IsNullOrWhiteSpace( p.Name ) ).Select( p => p.Name ) ?? Enumerable.Empty<string>();
		var objects = _anim.Objects?.Where( o => o is not null && !string.IsNullOrWhiteSpace( o.Name ) ).Select( o => o.Name ) ?? Enumerable.Empty<string>();

		return props.Concat( objects ).Distinct();
	}

	private ReferenceProp ChosenProp => _anim?.ReferenceProps?.FirstOrDefault( p => p?.Name == _propName );

	/// <summary>A reference prop's models become the grip's Prop, so the asset remembers what it
	/// was authored on. An object is OBJ meshes with no Model to name, so it leaves them alone.</summary>
	private void AdoptProp()
	{
		if ( _grip is null || ChosenProp is not { } prop || prop.Model is null )
			return;

		_grip.Prop = prop.Model;
		_grip.ExtraModels = prop.ExtraModels?.Where( m => m is not null ).ToList() ?? new();
	}

	/// <summary>Where the chosen prop is in the viewport, by the same rule RigViewport places it:
	/// its own transform, carried by its follow bone when it has one.</summary>
	private bool TryPropWorld( out Transform world )
	{
		world = Transform.Zero;

		Transform local;
		string follow;

		if ( ChosenProp is { } prop )
		{
			local = prop.LocalTransform;
			follow = prop.FollowBone;
		}
		else if ( _propName is not null && _anim?.FindObject( _propName ) is { } obj )
		{
			local = obj.LocalTransform;
			follow = obj.FollowBone;
		}
		else
		{
			return false;
		}

		world = !string.IsNullOrWhiteSpace( follow ) && _viewport.TryGetWorldTransform( follow, out var bone )
			? bone.ToWorld( local )
			: local;

		return true;
	}

	// ---- capture -----------------------------------------------------------------------------

	private GripHand Hand()
	{
		if ( _grip is null )
		{
			Say( "Open or create a grip first." );
			return null;
		}

		var hand = RigGripSolver.Hand( _viewport?.Renderer, _grip.Side );

		if ( hand is null )
			Say( $"The viewport's model has no hand_{_grip.BoneSuffix} with finger bones." );

		return hand;
	}

	private void CaptureHand()
	{
		if ( Hand() is not { } hand )
			return;

		if ( !TryPropWorld( out var propWorld ) )
		{
			Say( "Pick the prop the hand is on under Author Against." );
			return;
		}

		if ( !_viewport.TryGetWorldTransform( hand.HandBone, out var handWorld ) )
		{
			Say( $"Could not read {hand.HandBone} off the viewport." );
			return;
		}

		_grip.HandInProp = propWorld.ToLocal( handWorld );
		_grip.SourceModel ??= _viewport.Renderer?.Model;
		Say( $"Captured {hand.HandBone} on {_propName}." );
		Edit( "Capture Grip Hand", rebuild: true );
	}

	/// <summary>Every finger bone's rotation in its GripHand parent - the previous joint, or the
	/// hand - which is the space GripPose stores and GripAsset.Evaluate hands back.</summary>
	private void CaptureFingers( GripPose pose )
	{
		if ( Hand() is not { } hand )
			return;

		var count = 0;

		for ( var i = 0; i < hand.Count; i++ )
		{
			var parent = hand.Joints[i].Parent;
			var parentName = parent < 0 ? hand.HandBone : hand.Bones[parent];

			if ( !_viewport.TryGetWorldTransform( hand.Bones[i], out var world )
				|| !_viewport.TryGetWorldTransform( parentName, out var parentWorld ) )
				continue;

			pose.Set( hand.Bones[i], (parentWorld.Rotation.Inverse * world.Rotation).Normal );
			count++;
		}

		_selected = pose;
		Say( $"Captured {count} finger bones into \"{pose.Name}\"." );
		Edit( "Capture Grip Fingers", rebuild: true );
	}

	// ---- preview -----------------------------------------------------------------------------

	/// <summary>The finger rotations the preview is showing, in GripHand bone order. Null until
	/// something has been previewed.</summary>
	private GripQuat[] _shown;

	private void Preview()
	{
		_sliderValue.Text = $"{_slider.Value:0.00}";

		if ( Hand() is not { } hand )
			return;

		var local = new GripQuat[hand.Count];

		if ( _grip.Evaluate( _slider.Value, hand, local ) is null )
		{
			Say( "No poses to preview yet." );
			return;
		}

		Show( hand, local );
	}

	private void ShowPose( GripPose pose )
	{
		_sliderValue.Text = $"{_slider.Value:0.00}";

		if ( Hand() is not { } hand )
			return;

		var local = new GripQuat[hand.Count];
		_grip.Evaluate( pose, hand, local );
		Show( hand, local );
		Rebuild();
	}

	/// <summary>
	/// Pose the viewport's fingers WITHOUT keying. EvaluatePose is the scrub path - it suppresses
	/// auto-key - but it drives every bone, so every other bone is handed back exactly the local
	/// pose it is showing now and only the fingers change. Positions stay as they are; fingers do
	/// not stretch, the same rule RigGripComponent writes by.
	/// </summary>
	private void Show( GripHand hand, GripQuat[] local )
	{
		var pose = Snapshot();

		for ( var i = 0; i < hand.Count; i++ )
		{
			if ( pose.TryGetValue( hand.Bones[i], out var current ) )
				pose[hand.Bones[i]] = current.WithRotation( RigGripSolver.Q( local[i] ) );
		}

		_viewport.EvaluatePose( bone => pose.TryGetValue( bone, out var t ) ? t : null );
		_shown = local;
		_lastPose = pose;
	}

	private Dictionary<string, Transform> _lastPose;

	private Dictionary<string, Transform> Snapshot()
	{
		var pose = new Dictionary<string, Transform>();

		foreach ( var bone in _viewport.BoneNames() )
		{
			if ( _viewport.TryGetLocalTransform( bone, out var local ) )
				pose[bone] = local;
		}

		return pose;
	}

	/// <summary>
	/// The previewed fingers as keyframes at the playhead. SetLocalTransform is the inspector's
	/// keying path; Link is forced on for the duration so the button always does what it says,
	/// then put back. The value keyed is exactly the local handed in whatever the renderer's
	/// one-tick lag shows, and the preview is re-shown after so the screen agrees with it.
	/// </summary>
	private void KeyFingers()
	{
		if ( Hand() is not { } hand )
			return;

		if ( _shown is null || _shown.Length != hand.Count || _lastPose is null )
		{
			Say( "Preview a pose first - Key Fingers keys what the preview shows." );
			return;
		}

		// Fresh, so a bone moved since the preview is not put back where it was then.
		Show( hand, _shown );

		var link = _viewport.AutoKeyEnabled;
		_viewport.AutoKeyEnabled = true;

		try
		{
			for ( var i = 0; i < hand.Count; i++ )
			{
				if ( _lastPose.TryGetValue( hand.Bones[i], out var local ) )
					_viewport.SetLocalTransform( hand.Bones[i], local );
			}
		}
		finally
		{
			_viewport.AutoKeyEnabled = link;
		}

		var pose = _lastPose;
		_viewport.EvaluatePose( bone => pose.TryGetValue( bone, out var t ) ? t : null );

		Say( $"Keyed {hand.Count} finger bones at the playhead." );
		Changed?.Invoke( "Key Grip Fingers" );
	}

	/// <summary>
	/// The live-contact close, in the editor: the previewed pose under the viewport's hand, closed
	/// onto the chosen reference prop's render mesh by RigGripSolver - the call RigGripComponent
	/// makes in LiveContact, with the prop's models standing in for the held object. Objects
	/// built from OBJ files have no Model to hand the solver, so only reference props can do this.
	/// </summary>
	private void SolveContact()
	{
		if ( Hand() is not { } hand )
			return;

		if ( ChosenProp is not { } prop || !prop.AllModels.Any() || !TryPropWorld( out var propWorld ) )
		{
			Say( "Solve Contact needs a reference prop with a model under Author Against." );
			return;
		}

		var local = _shown is { } shown && shown.Length == hand.Count ? shown : null;

		if ( local is null )
		{
			local = new GripQuat[hand.Count];

			if ( _grip.Evaluate( _slider.Value, hand, local ) is null )
				_grip.Evaluate( (GripPose)null, hand, local );
		}

		if ( !RigGripSolver.PoseAuthored( _viewport.Renderer, hand, local ) )
		{
			Say( "The viewport cannot report the hand yet." );
			return;
		}

		var target = RigGripSolver.TargetFor( prop.AllModels.Select( m => (m, propWorld) ) );
		var settings = _grip.Settings();

		hand.Solve( target, settings );

		var solved = new GripQuat[hand.Count];

		for ( var i = 0; i < hand.Count; i++ )
			solved[i] = hand.LocalRotation( i );

		Show( hand, solved );

		var depth = hand.Penetration( target );
		Say( depth > 0f
			? $"Closed on {_propName}; deepest skin intrusion {depth:0.000} in. Capture Fingers to keep it."
			: $"Closed on {_propName}, no intrusion. Capture Fingers to keep it." );
	}
}
