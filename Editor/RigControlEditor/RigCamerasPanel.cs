using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The Cameras tab - the shots this clip was framed with, and the one place they are edited.
///
/// WHY CAMERAS ARE A FEATURE OF AN ANIMATION TOOL AT ALL. The framing you find by flying the
/// viewport is the framing the final shot should have, and the only way to get back to it - now,
/// after a reload, or in the game - is to write the numbers down. A camera is that write-down: a
/// name, a transform, a lens.
///
/// A CAMERA LIVES ON THE CLIP, NOT IN A PREFERENCE. The useful framing is per-shot: him at the
/// desk wants the shot over the desk's shoulder and the next clip wants something else. Kept as an
/// editor setting it would follow you between clips and be wrong for most of them.
///
/// AND IT IS WORKSPACE UNTIL TICKED OTHERWISE. Export With Clip is off by default, so a camera
/// dropped to look at an elbow stays in this window; ticked, RigAnimPlayerComponent spawns it when
/// the clip plays and the export bakes it into the shot file.
///
/// Modelled on RigLightsPanel: a list of small objects, each a property sheet with a row of buttons
/// under it. Same shape because it is the same job, and a second layout for it would be a second
/// thing to keep in step.
/// </summary>
internal sealed class RigCamerasPanel : Widget
{
	private readonly RigViewport _viewport;
	private readonly Widget _list;

	private RigAnimDocument _anim;

	/// <summary>Raised after this panel has changed the document, with the undo label. Same
	/// contract as every other panel in this window: the panel edits the list, the window
	/// rebuilds, marks dirty and records the undo step.</summary>
	public Action<string> Changed { get; set; }

	/// <summary>The frame a Key button keys at - the timeline's playhead.</summary>
	public Func<int> Playhead { get; set; }

	public RigCamerasPanel( Widget parent, RigViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		Name = "Cameras";
		WindowTitle = "Cameras";
		SetWindowIcon( "videocam" );

		Layout = Layout.Column();

		Layout.Add( RigHelpBox.Create( this,
			"Shots for this clip, saved with it. A camera records where you looked from when a " +
			"moment read right, so you can get back to that framing - now, after a reload, or " +
			"in the game.",
			new[]
			{
				RigHelpBox.S( "Place At Camera",
					"Puts the selected camera where you are looking from, aimed the way you are " +
					"looking, with the viewport's own field of view. Fly to where the shot " +
					"should be, click the button. That is the whole workflow - find the framing, " +
					"then save it." ),

				RigHelpBox.S( "Key / Key From View",
					"A camera can move. Key stores its placement and lens at the playhead; Key From " +
					"View places it at the viewport camera first. Keys show on the Timeline as the " +
					"camera's own row, and ease, curve and copy like any other. Look Through puts you " +
					"at the shot as it is on this frame." ),

				RigHelpBox.S( "Export With Clip",
					"Off means the camera is workspace - this window only, never in a game. On " +
					"means RigAnimPlayerComponent spawns a camera at it when the clip plays, and " +
					"the export bakes it into the shot file. Either way the framing is saved on " +
					"the clip." ),
			} ) );

		var bar = Layout.AddRow();
		bar.Margin = 4;
		bar.Add( new Editor.Label( "Cameras" ), 1 );
		bar.Add( new Button( "Add Camera", "add" ) { Clicked = Add } );

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		_list = new Widget( this ) { Layout = Layout.Column() };
		_list.Layout.Margin = 4;
		_list.Layout.Spacing = 4;
		scroll.Canvas = _list;

		Rebuild();
	}

	public void SetAnim( RigAnimDocument anim )
	{
		_anim = anim;
		Rebuild();
	}

	private List<RigCamera> Cameras => _anim?.Cameras ??= new List<RigCamera>();

	private void Add()
	{
		if ( _anim is null )
			return;

		var camera = new RigCamera { Name = UniqueName() };

		// A new camera lands where you are looking from, with the viewport's own lens, rather than
		// at the origin inside the model - the first thing you do after adding a camera should not
		// have to be digging it out of somebody's chest.
		PlaceAtCamera( camera );

		Cameras.Add( camera );
		Rebuild();
		Changed?.Invoke( "Add Camera" );
	}

	private string UniqueName()
	{
		var existing = Cameras.Select( c => c?.Name ).ToHashSet();
		var index = 0;

		while ( existing.Contains( $"camera {index}" ) )
			index++;

		return $"camera {index}";
	}

	public void Rebuild()
	{
		_list.Layout.Clear( true );

		if ( _anim is null )
		{
			_list.Layout.Add( new Editor.Label( "Open a Rig Animation asset to add cameras." ) { Enabled = false } );
			return;
		}

		foreach ( var camera in Cameras.Where( c => c is not null ).ToList() )
			_list.Layout.Add( Build( camera ) );

		if ( Cameras.Count == 0 )
		{
			_list.Layout.Add( new Editor.Label(
				"No cameras. Add one and place it where the shot should be." )
			{ Enabled = false } );
		}
	}

	private Widget Build( RigCamera camera )
	{
		var panel = new Widget( _list ) { Layout = Layout.Column() };
		panel.Layout.Margin = 6;
		panel.Layout.Spacing = 4;

		var header = panel.Layout.AddRow();
		header.Spacing = 6;

		var chip = new Editor.Label( "[cam]" ) { Color = Theme.Blue };
		chip.SetStyles( "font-weight: bold;" );
		header.Add( chip );
		header.Add( new Editor.Label( camera.Name ) { Enabled = false }, 1 );

		// A keyed camera's fields are where it sits with no keys; on screen it is where its track
		// puts it. Saying so is the difference between "my edit did nothing" and knowing why.
		if ( _anim?.FindCameraTrack( camera.Name ) is { Keyframes.Count: > 0 } keyed )
		{
			var badge = new Editor.Label( $"{keyed.Keyframes.Count} key(s)" )
			{
				Color = Theme.Yellow,
				ToolTip = "This camera moves: its track places it, so the fields below only matter where it has no keys"
			};
			badge.SetStyles( "font-weight: bold;" );
			header.Add( badge );
		}

		// The one badge worth having on the header: whether this camera leaves the window. It is
		// the difference between a working shot and something that will show up in a game.
		if ( camera.Export )
		{
			var exported = new Editor.Label( "exports" ) { Color = Theme.Green };
			exported.SetStyles( "font-weight: bold;" );
			header.Add( exported );
		}

		var serialized = EditorTypeLibrary.GetSerializedObject( camera );
		var nameBefore = camera.Name;

		serialized.OnPropertyChanged += _ =>
		{
			// A camera's keys are stored under its name, so a rename takes them along rather than
			// leaving them behind on a track nothing answers to.
			if ( camera.Name != nameBefore && _anim?.FindCameraTrack( nameBefore ) is { } track && _anim.FindCameraTrack( camera.Name ) is null )
				track.BoneName = camera.Name;

			nameBefore = camera.Name;
			Changed?.Invoke( "Edit Camera" );
			Rebuild();
		};

		var sheet = new ControlSheet();
		sheet.AddObject( serialized );
		panel.Layout.Add( sheet );

		panel.Layout.Add( Buttons( camera ) );

		return panel;
	}

	private Widget Buttons( RigCamera camera )
	{
		var row = new Widget { Layout = Layout.Row() };
		row.Layout.Spacing = 4;

		var toggle = new Button( camera.Enabled ? "On" : "Off", camera.Enabled ? "toggle_on" : "toggle_off" )
		{
			ToolTip = "Turn this camera off without losing it - the way to see what one shot is doing"
		};

		toggle.Clicked = () =>
		{
			camera.Enabled = !camera.Enabled;
			toggle.Text = camera.Enabled ? "On" : "Off";
			toggle.Icon = camera.Enabled ? "toggle_on" : "toggle_off";
			Changed?.Invoke( "Toggle Camera" );
		};

		row.Layout.Add( toggle );

		row.Layout.Add( new Button( "Place At Camera", "photo_camera" )
		{
			ToolTip = "Move this camera to the viewport camera, aimed where you are looking",
			Clicked = () =>
			{
				PlaceAtCamera( camera );
				Rebuild();
				Changed?.Invoke( "Place Camera" );
			}
		} );

		row.Layout.Add( new Button( "Look Through", "visibility" )
		{
			ToolTip = "Put the viewport camera at this shot as it is on the current frame",
			Clicked = () => _viewport?.LookThrough( camera )
		} );

		row.Layout.AddStretchCell();

		row.Layout.Add( new Button( "Delete", "delete" )
		{
			Clicked = () =>
			{
				Cameras.Remove( camera );

				// Its keys go with it - a track for a camera that no longer exists is a row on the
				// timeline nobody can explain.
				_anim?.BoneTracks.RemoveAll( t => t.Target == TrackTarget.Camera && t.BoneName == camera.Name );

				Rebuild();
				Changed?.Invoke( "Delete Camera" );
			}
		} );

		var keys = new Widget { Layout = Layout.Column() };
		keys.Layout.Spacing = 4;
		keys.Layout.Add( row );

		var keyRow = keys.Layout.AddRow();
		keyRow.Spacing = 4;

		keyRow.Add( new Button( "Key", "key" )
		{
			ToolTip = "Key this camera's placement and lens at the playhead",
			Clicked = () => Key( camera, false )
		} );

		keyRow.Add( new Button( "Key From View", "photo_camera" )
		{
			ToolTip = "Place this camera at the viewport camera, then key it at the playhead - fly, key, scrub, fly, key",
			Clicked = () => Key( camera, true )
		} );

		keyRow.AddStretchCell();

		if ( _anim?.FindCameraTrack( camera.Name ) is { Keyframes.Count: > 0 } )
		{
			keyRow.Add( new Button( "Clear Keys", "clear_all" )
			{
				ToolTip = "Remove every key from this camera, so it sits still where its fields put it",
				Clicked = () =>
				{
					_anim.BoneTracks.RemoveAll( t => t.Target == TrackTarget.Camera && t.BoneName == camera.Name );
					Rebuild();
					Changed?.Invoke( "Clear Camera Keys" );
				}
			} );
		}

		return keys;
	}

	/// <summary>
	/// Key a camera at the playhead. The fields are updated as well, so a camera keyed once and then
	/// cleared stays where it was last keyed rather than jumping back.
	/// </summary>
	private void Key( RigCamera camera, bool fromView )
	{
		if ( _anim is null )
			return;

		var frame = Playhead?.Invoke() ?? 0;

		if ( fromView )
		{
			PlaceAtCamera( camera );
		}
		else
		{
			// Where the shot IS on this frame - between two keys, that is the move, not wherever the
			// fields were last left. Keying the fields here would put the last key's framing mid-move.
			var (world, fov) = _anim.CameraAt( camera, frame );
			camera.Position = world.Position;
			camera.Rotation = world.Rotation.Angles();
			camera.FieldOfView = fov;
		}

		_anim.GetOrAddCameraTrack( camera.Name ).SetKeyframe( frame,
			RigAnimDocument.CameraKey( camera.Position, camera.Rotation.ToRotation(), camera.FieldOfView ) );

		Rebuild();
		Changed?.Invoke( "Key Camera" );
	}

	/// <summary>Where you are looking from, aimed where you are looking, with the viewport's own
	/// field of view - so the shot reads back exactly as it looked.</summary>
	private void PlaceAtCamera( RigCamera camera )
	{
		var view = _viewport?.ViewCamera ?? Transform.Zero;

		camera.Position = view.Position;
		camera.Rotation = view.Rotation.Angles();
		camera.FieldOfView = _viewport?.ViewFov ?? 80f;
	}
}
