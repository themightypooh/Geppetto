using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The Lights tab - the lighting the viewport renders this clip under, and the one place it is
/// edited.
///
/// WHY LIGHTING IS A FEATURE OF AN ANIMATION TOOL AT ALL. A pose is read off its silhouette and
/// its shadows. Under one fixed sun over the shoulder - which is what this viewport had - an arm
/// in front of a chest is a flat shape, a hand turned palm-up looks the same as palm-down, and
/// the face of anything looking away from that one direction is a grey wall. You cannot judge a
/// pose you cannot see, so lighting is not decoration here, it is the instrument.
///
/// A LIGHT LIVES ON THE CLIP, NOT IN A PREFERENCE. The useful lighting is per-shot: him at the
/// desk wants the desk's key light and the next clip wants something else. Kept as an editor
/// setting it would follow you between clips and be wrong for most of them.
///
/// AND IT IS WORKSPACE UNTIL TICKED OTHERWISE. Export With Clip is off by default, so a light
/// added to see an elbow by stays in this window; ticked, RigAnimPlayerComponent spawns it
/// beside the model when the clip plays. Neither ever reaches an exported .vmdl - that format
/// is bone channels and has nowhere to put a light - and the export dialog says so rather than
/// dropping them quietly.
///
/// Modelled on RigConstraintsPanel: a list of small objects, each a property sheet with a row of
/// buttons under it. Same shape because it is the same job, and a second layout for it would be
/// a second thing to keep in step.
/// </summary>
internal sealed class RigLightsPanel : Widget
{
	private readonly RigViewport _viewport;
	private readonly Widget _list;

	private RigAnimDocument _anim;

	/// <summary>Raised after this panel has changed the document, with the undo label. Same
	/// contract as every other panel in this window: the panel edits the list, the window
	/// rebuilds, marks dirty and records the undo step.</summary>
	public Action<string> Changed { get; set; }

	public RigLightsPanel( Widget parent, RigViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		Name = "Lights";
		WindowTitle = "Lights";
		SetWindowIcon( "lightbulb" );

		Layout = Layout.Column();

		Layout.Add( RigHelpBox.Create( this,
			"Lighting for the viewport, saved on this clip. A clip with no lights uses the " +
			"built-in sun and ambient; add one and these replace them.",
			new[]
			{
				RigHelpBox.S( "Why bother",
					"A pose reads by its shadows. Under one fixed light an arm in front of a " +
					"chest is a flat shape and a hand turned over looks identical either way - " +
					"so a second light from another angle is often the difference between " +
					"judging a pose and guessing at it." ),

				RigHelpBox.S( "Place At Camera",
					"Puts the selected light where you are looking from, aimed the way you are " +
					"looking. Fly the viewport to where you want the light, click the button. " +
					"That is faster than any three numbers, and it is how a key light gets " +
					"placed on a real set." ),

				RigHelpBox.S( "Export With Clip",
					"Off means the light is workspace - this window only, never in a game. On " +
					"means RigAnimPlayerComponent spawns it beside the model when the clip " +
					"plays, which is how a lamp authored with the pose it lights travels with " +
					"it. Either way it cannot go into an exported .vmdl: that file is bone " +
					"channels and has nowhere to put a light." ),

				RigHelpBox.S( "Start from the defaults",
					"Add ▸ Copy Default Lighting drops the built-in sun and ambient in as two " +
					"ordinary entries. Adjusting those is usually quicker than lighting from " +
					"black, and it makes it obvious what you changed." ),
			} ) );

		var bar = Layout.AddRow();
		bar.Margin = 4;
		bar.Add( new Editor.Label( "Lights" ), 1 );
		bar.Add( new Button( "Add Light", "add" ) { Clicked = OpenAddMenu } );

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

	private void OpenAddMenu()
	{
		if ( _anim is null )
			return;

		var menu = new Menu( this );

		menu.AddOption( "Directional", "wb_sunny", () => Add( RigLightKind.Directional ) );
		menu.AddOption( "Point", "lightbulb", () => Add( RigLightKind.Point ) );
		menu.AddOption( "Spot", "flashlight_on", () => Add( RigLightKind.Spot ) );
		menu.AddOption( "Ambient", "wb_twilight", () => Add( RigLightKind.Ambient ) );

		// Only worth offering while the defaults are still what you are looking at - once the
		// list has entries the defaults are already off, and this would read as "put them back"
		// when what it does is add two more lights.
		if ( Lights.Count == 0 )
		{
			menu.AddSeparator();
			menu.AddOption( "Copy Default Lighting", "content_copy", CopyDefaults );
		}

		menu.OpenAtCursor();
	}

	private List<RigLight> Lights => _anim?.Lights ??= new List<RigLight>();

	private void Add( RigLightKind kind )
	{
		var light = new RigLight { Kind = kind, Name = UniqueName( kind.ToString() ) };

		// A new point or spot lands where you are looking from rather than at the origin, which
		// for a light is inside the model. Nobody wants their first act after adding a light to
		// be dragging it out of somebody's chest.
		if ( kind is RigLightKind.Point or RigLightKind.Spot )
			PlaceAtCamera( light );

		Lights.Add( light );
		Rebuild();
		Changed?.Invoke( "Add Light" );
	}

	/// <summary>The built-in lighting as two ordinary entries, so it can be adjusted rather than
	/// replaced. The numbers are RigViewport.DefaultLighting's, and the ambient is resolved from
	/// the theme HERE rather than left as a live reference - a clip that stores its lighting has
	/// to store a colour, not a promise to ask the editor's theme again later.</summary>
	private void CopyDefaults()
	{
		Lights.Add( new RigLight
		{
			Name = "sun",
			Kind = RigLightKind.Directional,
			Rotation = new Angles( 45f, 45f, 0f ),
			Color = Color.White,
		} );

		Lights.Add( new RigLight
		{
			Name = "ambient",
			Kind = RigLightKind.Ambient,
			Color = Theme.ControlBackground * 0.6f,
		} );

		Rebuild();
		Changed?.Invoke( "Copy Default Lighting" );
	}

	private string UniqueName( string prefix )
	{
		var existing = Lights.Select( l => l?.Name ).ToHashSet();
		var index = 0;

		while ( existing.Contains( $"{prefix} {index}" ) )
			index++;

		return $"{prefix} {index}";
	}

	public void Rebuild()
	{
		_list.Layout.Clear( true );

		if ( _anim is null )
		{
			_list.Layout.Add( new Editor.Label( "Open a Rig Animation asset to light it." ) { Enabled = false } );
			return;
		}

		foreach ( var light in Lights.Where( l => l is not null ).ToList() )
			_list.Layout.Add( Build( light ) );

		if ( Lights.Count == 0 )
		{
			_list.Layout.Add( new Editor.Label(
				"No lights - this clip is using the built-in sun and ambient. Add one and these replace them." )
			{ Enabled = false } );
		}
	}

	private Widget Build( RigLight light )
	{
		var panel = new Widget( _list ) { Layout = Layout.Column() };
		panel.Layout.Margin = 6;
		panel.Layout.Spacing = 4;

		var header = panel.Layout.AddRow();
		header.Spacing = 6;

		var chip = new Editor.Label( $"[{light.Kind}]" ) { Color = ChipColor( light.Kind ) };
		chip.SetStyles( "font-weight: bold;" );
		header.Add( chip );
		header.Add( new Editor.Label( light.Name ) { Enabled = false }, 1 );

		// The one badge worth having on the header: whether this light leaves the window. It is
		// the difference between a working light and something that will show up in a game, and
		// it is not worth scrolling a property sheet to find out.
		if ( light.Export )
		{
			var exported = new Editor.Label( "exports" ) { Color = Theme.Green };
			exported.SetStyles( "font-weight: bold;" );
			header.Add( exported );
		}

		var serialized = EditorTypeLibrary.GetSerializedObject( light );

		// Rebuild, not just refresh: Kind decides which fields the sheet shows, and the name and
		// the export badge are drawn in the header above it. Any of the three changing means
		// this row is out of date, and a light whose sheet disagrees with its own header is
		// worse than a moment's flicker.
		serialized.OnPropertyChanged += _ =>
		{
			Changed?.Invoke( "Edit Light" );
			Rebuild();
		};

		var sheet = new ControlSheet();
		sheet.AddObject( serialized );
		panel.Layout.Add( sheet );

		panel.Layout.Add( Buttons( light ) );

		return panel;
	}

	private Widget Buttons( RigLight light )
	{
		var row = new Widget { Layout = Layout.Row() };
		row.Layout.Spacing = 4;

		var toggle = new Button( light.Enabled ? "On" : "Off", light.Enabled ? "toggle_on" : "toggle_off" )
		{
			ToolTip = "Turn this light off without losing it - the way to see what one light is doing"
		};

		toggle.Clicked = () =>
		{
			light.Enabled = !light.Enabled;
			toggle.Text = light.Enabled ? "On" : "Off";
			toggle.Icon = light.Enabled ? "toggle_on" : "toggle_off";
			Changed?.Invoke( "Toggle Light" );
		};

		row.Layout.Add( toggle );

		// Not offered for the two kinds that have nowhere to put a camera position: a directional
		// light has no position, and an ambient has neither position nor direction. A button that
		// does nothing on half the rows teaches people to distrust it.
		if ( light.Kind is RigLightKind.Point or RigLightKind.Spot or RigLightKind.Directional )
		{
			row.Layout.Add( new Button( "Place At Camera", "photo_camera" )
			{
				ToolTip = "Move this light to the viewport camera, aimed where you are looking",
				Clicked = () =>
				{
					PlaceAtCamera( light );
					Rebuild();
					Changed?.Invoke( "Place Light" );
				}
			} );
		}

		row.Layout.AddStretchCell();

		row.Layout.Add( new Button( "Delete", "delete" )
		{
			Clicked = () =>
			{
				Lights.Remove( light );
				Rebuild();
				Changed?.Invoke( "Delete Light" );
			}
		} );

		return row;
	}

	/// <summary>Where you are looking from, aimed where you are looking. A directional light
	/// takes only the angle - it has no position to take.</summary>
	private void PlaceAtCamera( RigLight light )
	{
		var view = _viewport?.ViewCamera ?? Transform.Zero;

		light.Rotation = view.Rotation.Angles();

		if ( light.Kind != RigLightKind.Directional )
			light.Position = view.Position;
	}

	private static Color ChipColor( RigLightKind kind ) => kind switch
	{
		RigLightKind.Directional => Theme.Yellow,
		RigLightKind.Point => Theme.Blue,
		RigLightKind.Spot => Theme.Green,
		_ => Theme.TextControl,
	};
}
