using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// An on/off switch with a knob that slides between the two ends.
///
/// HAND-PAINTED BECAUSE S&amp;BOX HAS NO SWITCH. It ships <c>Checkbox</c>, which is a tick in a square
/// — a different control that reads as "tick this to agree" rather than "this is on". Same reason
/// EffigyIcons draws its own glyphs: the widget wanted here does not exist in the library, so it
/// gets drawn, using the same Paint calls every other custom widget in this file already uses.
///
/// The slide advances a fixed step PER PAINT rather than per second, and that is deliberate. An
/// eased, time-based animation needs a trustworthy frame clock, and a wrong guess about one in the
/// editor gives a knob stuck half way — a visual bug that no compiler catches. Stepping per paint
/// cannot stall: it is bounded at roughly six frames, it always arrives, and OnPaint stops asking
/// for another frame the moment it lands.
/// </summary>
internal sealed class EffigyToggleSwitch : Widget
{
	private const float TrackWidth = 42f;
	private const float TrackHeight = 22f;

	/// <summary>Knob inset from the track edge, so the track reads as a groove around it.</summary>
	private const float KnobInset = 3f;

	/// <summary>How much of the travel one repaint covers. 0.18 is about six frames end to end —
	/// fast enough to feel like a switch, slow enough to read as movement.</summary>
	private const float SlideStep = 0.18f;

	public bool Value { get; private set; }

	/// <summary>Fires only on a real change, and only when the user caused it — SetValue with
	/// notify false is how the window seeds the control without echoing back.</summary>
	public Action<bool> ValueChanged { get; set; }

	private float _slide;
	private bool _pressed;

	public EffigyToggleSwitch( Widget parent, bool value ) : base( parent )
	{
		Value = value;
		_slide = value ? 1f : 0f;

		Cursor = CursorShape.Finger;
		MouseTracking = true;

		// Same reasoning as the tool bar's buttons: a plain Widget paints the system background, which
		// is a pale square sitting behind a rounded control.
		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedSize = new Vector2( TrackWidth, TrackHeight );
	}

	public void SetValue( bool value, bool notify = true )
	{
		if ( Value == value )
			return;

		Value = value;
		Update();

		if ( notify )
			ValueChanged?.Invoke( value );
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		var target = Value ? 1f : 0f;

		if ( MathF.Abs( _slide - target ) <= SlideStep )
		{
			_slide = target;
		}
		else
		{
			_slide += _slide < target ? SlideStep : -SlideStep;

			// Still travelling, so ask for another frame. Once it lands this stops firing and the
			// widget goes quiet.
			Update();
		}

		var track = LocalRect;

		// The track carries the state: dim when off, the theme's accent when on. Colour and
		// position both say the same thing, which is what makes a switch readable at a glance.
		Paint.ClearPen();
		Paint.SetBrush( Color.Lerp( Theme.ControlBackground, Theme.Primary, _slide ) );
		Paint.DrawRect( track, track.Height * 0.5f );

		var diameter = track.Height - KnobInset * 2f;
		var travel = track.Width - KnobInset * 2f - diameter;

		var knob = new Rect( track.Left + KnobInset + travel * _slide, track.Top + KnobInset, diameter, diameter );

		Paint.SetBrush( Theme.TextLight.WithAlpha( _pressed ? 0.75f : 1f ) );
		Paint.DrawRect( knob, diameter * 0.5f );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		_pressed = true;
		Update();
		e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( !_pressed )
			return;

		_pressed = false;

		// Only flips if released over the control — dragging off to cancel, the same as every
		// other button in this editor.
		if ( IsUnderMouse )
			SetValue( !Value );
		else
			Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();

		_pressed = false;
		Update();
	}
}

/// <summary>
/// A titled block whose body folds away when its header is clicked.
///
/// s&amp;box's own collapsible category is internal to the base editor library, so this is drawn here
/// the same way <see cref="EffigyToggleSwitch"/> is — a text triangle and a bold title, nothing that
/// needs an icon asset. The open/closed state is remembered in an EditorCookie, so the window comes
/// back the way it was left.
/// </summary>
internal sealed class CollapsibleSection : Widget
{
	private readonly SectionHeader _header;
	private readonly Widget _body;
	private readonly string _cookie;

	private bool _expanded;

	/// <summary>The controls for this section. Add to this, not to the section itself.</summary>
	public Widget Body => _body;

	public CollapsibleSection( Widget parent, string title, string cookie, bool defaultExpanded = true ) : base( parent )
	{
		_cookie = cookie;
		_expanded = cookie is null ? defaultExpanded : EditorCookie.Get( cookie, defaultExpanded );

		Layout = Layout.Column();
		Layout.Spacing = 4;

		_header = Layout.Add( new SectionHeader( this, title, _expanded ) );
		_header.Clicked = Toggle;

		_body = Layout.Add( new Widget( this ) );
		_body.Layout = Layout.Column();
		_body.Layout.Spacing = 10;
		_body.Layout.Margin = new Sandbox.UI.Margin( 12, 0, 0, 0 );
		_body.Visible = _expanded;
	}

	private void Toggle()
	{
		_expanded = !_expanded;
		_header.SetExpanded( _expanded );
		_body.Visible = _expanded;

		if ( _cookie is not null )
			EditorCookie.Set( _cookie, _expanded );
	}

	/// <summary>The clickable bar at the top of a <see cref="CollapsibleSection"/>.</summary>
	private sealed class SectionHeader : Widget
	{
		private readonly Editor.Label _arrow;

		public Action Clicked { get; set; }

		public SectionHeader( Widget parent, string title, bool expanded ) : base( parent )
		{
			Cursor = CursorShape.Finger;
			MouseTracking = true;

			TranslucentBackground = true;
			NoSystemBackground = true;

			Layout = Layout.Row();
			Layout.Spacing = 6;

			_arrow = Layout.Add( new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

			var label = Layout.Add( new Editor.Label( title ) );
			label.SetStyles( "font-weight: 600;" );

			Layout.AddStretchCell();

			SetExpanded( expanded );
		}

		public void SetExpanded( bool expanded ) => _arrow.Text = expanded ? "▾" : "▸";

		protected override void OnMousePress( MouseEvent e )
		{
			if ( !e.LeftMouseButton )
				return;

			e.Accepted = true;
			Clicked?.Invoke();
		}
	}
}

/// <summary>
/// A key-capture box for rebinding a shortcut. Click it, press the new key, and the box shows the
/// result and hands it back through <see cref="KeyBind.ValueChanged"/>.
///
/// Built on the editor's own KeyBind, with one override: the shortcut system stores keys by
/// <see cref="KeyEvent.Name"/> (what the editor's keybind page captures), while the stock KeyBind
/// reads the button-code name — two spellings that disagree for the same key.
/// </summary>
internal sealed class EffigyKeyBind : KeyBind
{
	public EffigyKeyBind( Widget parent ) : base( parent )
	{
		MinimumWidth = 110;
	}

	protected override string GetKeyName( KeyEvent e ) => e.Name.ToUpperInvariant();
}

/// <summary>
/// Effigy's settings, in their own window off the Edit menu.
///
/// A WINDOW RATHER THAN MORE MENU. Both of these lived in the View menu as checkable options, which
/// is where a setting goes when there are two of them and nowhere else to put them. There are two
/// now and there will be more — this is where they land instead of the menu growing a tail.
///
/// It knows nothing about EffigyWindow. Values come in as plain arguments and changes go out as
/// callbacks, so the window that owns the viewport stays the one place that decides what a setting
/// actually does. Built the way FastTextureWindow builds: a Canvas, with the toolbar, menu and
/// status bars turned off, since none of the three has anything to show here.
/// </summary>
internal sealed class EffigySettingsWindow : Window
{
	/// <summary>
	/// Everything the window shows, in and out in one lump.
	///
	/// A struct rather than eight constructor arguments and eight callbacks. Every one of these is
	/// read once when the window opens and written back the moment a control moves, so passing them
	/// separately meant a parameter list that grew every time a setting was added — which was
	/// already twice.
	/// </summary>
	internal struct Values
	{
		public bool ShowGrid;
		public float GridSpacing;
		public bool SnapToGrid;
		public bool SnapToPoints;
		public bool SnapToFaceEdges;
		public int PaletteIndex;
		public bool ShowSizeReference;

		/// <summary>OUT ONLY — the height of the loaded stand-in, in world units, which only the
		/// viewport can know because only it has the model. Whatever is set on the way in is
		/// ignored; the applied values coming back carry the real number, and the caption under
		/// the switch prints it.</summary>
		public float SizeReferenceHeight;

		/// <summary>Even light from every side. Off is the studio sun, plus any lamps you have
		/// placed in the viewport.</summary>
		public bool FullBright;

		/// <summary>OUT ONLY — how many point lights are currently in the viewport, so the caption
		/// under the switch can say so.</summary>
		public int PlacedLightCount;

		/// <summary>Draw the model with a checker instead of its own materials, so UV stretching
		/// and seams are visible on the surface. A diagnostic, not how the part looks.</summary>
		public bool ShowUVChecker;

		/// <summary>Normal-map bake: DirectX green (-Y) rather than OpenGL (+Y), the row order, and
		/// the square size in texels. Read by the Bake button, not by the viewport.</summary>
		public bool BakeDirectXGreen;
		public bool BakeFlipV;
		public int BakeSize;
	}

	/// <summary>The sizes the bake dropdown offers. A square map, so one number.</summary>
	private static readonly int[] BakeSizes = { 256, 512, 1024, 2048, 4096 };

	/// <summary>The spacings the dropdown offers, in sketch units. Zero is Automatic — the adaptive
	/// 1/2/5 step that keeps the grid about a constant size on screen at any zoom.</summary>
	///
	/// Internal because the sketch grid overlay offers the same list. Two lists would be two lists
	/// that drift: a value on one dropdown and not on the other reads as the setting having been
	/// lost when you switch between them.
	internal static readonly float[] Spacings = { 0f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 25f };

	private Values _values;

	/// <summary>
	/// Apply, and hand back what was actually applied.
	///
	/// A one-way callback was enough while every setting was a value this window already had.
	/// The size reference is not: its height is a property of a model this window never loads, so
	/// the only way to print it is to ask for it after the switch has been flipped and the viewport
	/// has done the loading.
	/// </summary>
	private readonly Func<Values, Values> _changed;

	/// <summary>The line under the size-reference switch, kept so a flip can rewrite it.</summary>
	private Editor.Label _referenceNote;

	/// <summary>The size-reference switch itself, kept for the one case where the answer comes back
	/// different from the question: the citizen would not load, so the viewport is off and the
	/// switch has to go back to off with it rather than sit on over an empty floor.</summary>
	private EffigyToggleSwitch _referenceToggle;

	/// <summary>Full-bright switch, kept so adding a light can flip it off from outside without
	/// treating that as a new click.</summary>
	private EffigyToggleSwitch _brightToggle;

	/// <summary>The line under the lighting switch, kept so adding or clearing a light can rewrite
	/// it.</summary>
	private Editor.Label _lightsNote;

	private readonly Action _addPointLight;
	private readonly Action _clearLights;

	public EffigySettingsWindow( Widget owner, Values values, Func<Values, Values> changed,
		Action addPointLight = null, Action clearLights = null )
	{
		_values = values;
		_changed = changed;
		_addPointLight = addPointLight;
		_clearLights = clearLights;

		// PARENTED TO EFFIGY, NOT TO THE MAIN EDITOR WINDOW.
		//
		// ProjectSettingsWindow uses `Parent = EditorWindow` and that is right for it — it is the
		// main editor's own dialog. Copying it here was not: Effigy is a separate top-level window,
		// so owning this dialog to a DIFFERENT top-level window handed focus to the editor's window
		// group and dropped Effigy behind it. Opening settings appeared to minimise the tool.
		//
		// Owned by the window whose settings these are, the dialog floats over Effigy and Effigy
		// stays exactly where it was.
		Parent = owner;

		WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.CloseButton
			| WindowFlags.WindowSystemMenuHint | WindowFlags.WindowTitle;

		WindowTitle = "Effigy Settings";
		Size = new Vector2( 400, 540 );

		SetWindowIcon( "settings" );

		Build();
	}

	/// <summary>
	/// Built here rather than in an override of BuildDock.
	///
	/// BuildDock is not visible to override from this assembly — FastTextureWindow and
	/// ProjectSettingsWindow both live inside the editor's own assembly, where it is. From out
	/// here the constructor is the hook, which is fine: everything this window shows is known by
	/// the time it is constructed.
	/// </summary>
	private void Build()
	{
		var canvas = new Widget( this );

		canvas.Layout = Layout.Column();
		canvas.Layout.Margin = 16;
		canvas.Layout.Spacing = 12;

		// The settings now run long enough to need scrolling — six folded sections plus the hotkeys
		// list would otherwise push the bottom ones off the fixed-size window.
		var scroll = canvas.Layout.Add( new ScrollArea( canvas ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		var body = new Widget( canvas );
		body.Layout = Layout.Column();
		body.Layout.Spacing = 12;
		scroll.Canvas = body;

		// --- grid ----------------------------------------------------------------------------

		var grid = Section( body, "Grid", "Effigy.Settings.Grid" );

		AddSwitch( grid, "Show plane grid",
			"The lattice inside every plane's outline - the three reference planes and the one you "
			+ "are sketching on.",
			_values.ShowGrid,
			value => { _values.ShowGrid = value; Changed(); } );

		// --- spacing -------------------------------------------------------------------------

		var spacingRow = grid.Layout.AddRow();

		spacingRow.Add( new Editor.Label( "Grid spacing" ) );
		spacingRow.AddStretchCell();

		var spacing = new ComboBox( grid )
		{
			MinimumWidth = 150,
			ToolTip = "How far apart the grid lines sit, in sketch units. This is the same step the "
				+ "cursor snaps to - the lines are the intervals you land on.",
		};

		foreach ( var value in Spacings )
		{
			var step = value;

			spacing.AddItem( Describe( step ),
				onSelected: () => { _values.GridSpacing = step; Changed(); },
				selected: MathF.Abs( step - _values.GridSpacing ) < 0.0001f );
		}

		spacingRow.Add( spacing );

		// --- snapping ------------------------------------------------------------------------

		var snapping = Section( body, "Snapping", "Effigy.Settings.Snapping" );

		AddSwitch( snapping, "Snap to grid",
			"Round the cursor to the nearest grid intersection. Off draws freehand on the plane.",
			_values.SnapToGrid,
			value => { _values.SnapToGrid = value; Changed(); } );

		AddSwitch( snapping, "Snap to points",
			"Jump the cursor onto existing sketch points. This is what closes a chain - without it "
			+ "two clicks in the same spot leave two points a hair apart and the profile will not "
			+ "extrude.",
			_values.SnapToPoints,
			value => { _values.SnapToPoints = value; Changed(); } );

		AddSwitch( snapping, "Snap to the face underneath",
			"While sketching on the face of a part, jump the cursor onto that face's own corners "
			+ "and slide it along its edges. Off leaves the outline drawn but inert - useful when "
			+ "you want to draw across a face rather than measure from it.",
			_values.SnapToFaceEdges,
			value => { _values.SnapToFaceEdges = value; Changed(); } );

		// --- the size reference ---------------------------------------------------------------

		var reference = Section( body, "Reference", "Effigy.Settings.Reference" );

		_referenceToggle = AddSwitch( reference, "Show citizen",
			"Stand the base citizen at the origin, to build against. It is scenery only - it takes "
			+ "no clicks, joins no feature and is never exported.",
			_values.ShowSizeReference,
			value => { _values.ShowSizeReference = value; Changed(); } );

		_referenceNote = reference.Layout.Add( new Editor.Label( ReferenceNote( _values ) ) );

		// Dim and small, because it is a readout rather than a control - it sits under the switch
		// the way a hint does, not in the column of things you can change.
		_referenceNote.SetStyles( "color: #808080; font-size: 11px;" );

		// --- lighting ------------------------------------------------------------------------

		var lighting = Section( body, "Lighting", "Effigy.Settings.Lighting" );

		_brightToggle = AddSwitch( lighting, "Full bright",
			"Even light from every side, so a face is never in shadow while you model. Off is a "
			+ "sun like a game scene, plus any lights you have placed.",
			_values.FullBright,
			value => { _values.FullBright = value; Changed(); } );

		_lightsNote = lighting.Layout.Add( new Editor.Label( LightsNote( _values ) ) );
		_lightsNote.SetStyles( "color: #808080; font-size: 11px;" );

		var lightsRow = lighting.Layout.AddRow();
		lightsRow.Spacing = 8;

		var addLight = new Button( "Add point light", "wb_incandescent" )
		{
			ToolTip = "Drop a lamp in the viewport and drag it. Full bright turns off so you can "
				+ "see what it does. Delete removes the selected one.",
			Clicked = OnAddPointLight,
		};

		var clearLights = new Button( "Clear lights" )
		{
			ToolTip = "Remove every lamp you have placed. The studio sun stays if full bright is off.",
			Clicked = OnClearLights,
		};

		lightsRow.Add( addLight );
		lightsRow.Add( clearLights );

		// --- view ----------------------------------------------------------------------------

		var view = Section( body, "View", "Effigy.Settings.View" );

		AddSwitch( view, "UV checker",
			"Draw the model with a checker pattern instead of its own materials, so stretching and "
			+ "seams in the UVs are visible on the surface. A toggle because a checker is a "
			+ "diagnostic, not how the part looks.",
			_values.ShowUVChecker,
			value => { _values.ShowUVChecker = value; Changed(); } );

		// --- the palette ---------------------------------------------------------------------

		var appearance = Section( body, "Appearance", "Effigy.Settings.Appearance" );

		var paletteRow = appearance.Layout.AddRow();

		paletteRow.Add( new Editor.Label( "Colour palette" ) );
		paletteRow.AddStretchCell();

		var combo = new ComboBox( appearance ) { MinimumWidth = 150 };

		for ( var i = 0; i < EffigyPalette.All.Length; i++ )
		{
			var index = i;

			combo.AddItem( EffigyPalette.All[index].Name,
				onSelected: () => { _values.PaletteIndex = index; Changed(); },
				selected: index == _values.PaletteIndex );
		}

		paletteRow.Add( combo );

		// --- normal map bake ----------------------------------------------------------------

		var bake = Section( body, "Normal map bake", "Effigy.Settings.Bake" );

		AddSwitch( bake, "DirectX green channel",
			"Which way the green channel points. On is DirectX-style (-Y), off is OpenGL-style "
			+ "(+Y). If a baked map looks inverted where a surface curves, this is the switch. Only "
			+ "the Bake button in a Sculpt feature reads it.",
			_values.BakeDirectXGreen,
			value => { _values.BakeDirectXGreen = value; Changed(); } );

		AddSwitch( bake, "Flip vertically",
			"Where row zero of the image sits. Off puts v = 0 at the top, on puts it at the bottom "
			+ "- flip this if the bake comes out mirrored top to bottom.",
			_values.BakeFlipV,
			value => { _values.BakeFlipV = value; Changed(); } );

		var bakeSizeRow = bake.Layout.AddRow();

		bakeSizeRow.Add( new Editor.Label( "Bake size" ) );
		bakeSizeRow.AddStretchCell();

		var bakeSize = new ComboBox( bake )
		{
			MinimumWidth = 150,
			ToolTip = "The side length of the baked normal map, in texels. The map is square.",
		};

		foreach ( var size in BakeSizes )
		{
			var value = size;

			bakeSize.AddItem( $"{value} x {value}",
				onSelected: () => { _values.BakeSize = value; Changed(); },
				selected: value == _values.BakeSize );
		}

		bakeSizeRow.Add( bakeSize );

		// --- hotkeys -------------------------------------------------------------------------

		BuildHotkeys( body );

		Canvas = canvas;
	}

	/// <summary>Apply, then take the applied values back - the viewport fills in what it alone
	/// knows, and the caption is rewritten from that rather than from a guess made here.</summary>
	private void Changed()
	{
		if ( _changed is null )
			return;

		_values = _changed( _values );

		if ( _referenceNote.IsValid() )
			_referenceNote.Text = ReferenceNote( _values );

		if ( _lightsNote.IsValid() )
			_lightsNote.Text = LightsNote( _values );

		// notify false: this is the applied value coming home, not a new request. Notifying would
		// hand it straight back to Changed and round the loop again.
		_referenceToggle?.SetValue( _values.ShowSizeReference, notify: false );
		_brightToggle?.SetValue( _values.FullBright, notify: false );
	}

	/// <summary>Rewrite the controls from values the viewport already applied — adding a light
	/// from the View menu, or from the button below, both land here so the switch and the caption
	/// match what is on screen.</summary>
	public void Sync( Values values )
	{
		_values = values;

		if ( _lightsNote.IsValid() )
			_lightsNote.Text = LightsNote( _values );

		if ( _referenceNote.IsValid() )
			_referenceNote.Text = ReferenceNote( _values );

		_brightToggle?.SetValue( _values.FullBright, notify: false );
		_referenceToggle?.SetValue( _values.ShowSizeReference, notify: false );
	}

	private void OnAddPointLight()
	{
		_addPointLight?.Invoke();
	}

	private void OnClearLights()
	{
		_clearLights?.Invoke();
	}

	/// <summary>
	/// What the stand-in is worth as a ruler: its height, in the units every other number in Effigy
	/// is in.
	///
	/// A height of zero with the switch on means the model did not load - the citizen addon is not
	/// mounted. Saying so here is the only place that failure is visible, since the viewport's own
	/// answer to a missing model is an empty patch of floor.
	/// </summary>
	private static string ReferenceNote( Values values )
	{
		if ( !values.ShowSizeReference )
			return "The citizen from the base addon, standing at the origin.";

		return values.SizeReferenceHeight > 0f
			? $"The citizen stands {values.SizeReferenceHeight:0.#} units tall."
			: "The citizen could not be loaded - is the base citizen addon mounted?";
	}

	/// <summary>What the lighting switch is doing right now, in one line, including how many lamps
	/// are in the scene so "Add point light" has somewhere to report back.</summary>
	private static string LightsNote( Values values )
	{
		var lamps = values.PlacedLightCount == 0
			? "No lamps placed."
			: values.PlacedLightCount == 1
				? "1 lamp in the viewport — drag the bulb to move it, Delete to remove it."
				: $"{values.PlacedLightCount} lamps in the viewport — drag a bulb to move it, Delete to remove it.";

		return values.FullBright
			? $"Even light from every side. {lamps}"
			: $"Studio sun, like a game scene. {lamps}";
	}

	/// <summary>A collapsible section, added to the settings column and returned as its body — the
	/// widget every control in that section should be added to.</summary>
	private static Widget Section( Widget parent, string title, string cookie, bool defaultExpanded = true )
	{
		var section = parent.Layout.Add( new CollapsibleSection( parent, title, cookie, defaultExpanded ) );

		return section.Body;
	}

	/// <summary>A labelled row with the switch pushed out to the right edge, which is the shape
	/// every one of these settings wants.</summary>
	private static EffigyToggleSwitch AddSwitch( Widget container, string label, string tip, bool value, Action<bool> changed )
	{
		var row = container.Layout.AddRow();

		row.Add( new Editor.Label( label ) { ToolTip = tip } );
		row.AddStretchCell();

		var toggle = new EffigyToggleSwitch( container, value ) { ToolTip = tip };

		toggle.ValueChanged = changed;

		row.Add( toggle );

		return toggle;
	}

	/// <summary>
	/// The shortcut rebinding section.
	///
	/// Every tool key Effigy registers with the engine's own <c>[Shortcut]</c> system appears here,
	/// each on a row with a key box. Reassigning writes into the engine's ShortcutOverrides store
	/// and re-registers the keys immediately, which is the same mechanism the editor's own keybind
	/// page uses — so a key changed here shows up in Edit &gt; Preferences &gt; Editor Keybinds too,
	/// and vice versa.
	/// </summary>
	private void BuildHotkeys( Widget body )
	{
		var section = Section( body, "Hotkeys", "Effigy.Settings.Hotkeys" );

		var note = section.Layout.Add( new Editor.Label(
			"Click a key, then press the new one. Reset puts a key back to its default." ) );
		note.SetStyles( "color: #808080; font-size: 11px;" );

		var shown = 0;

		foreach ( var entry in EffigyShortcuts() )
		{
			var ident = entry.Identifier;

			var row = section.Layout.AddRow();
			row.Spacing = 8;

			row.Add( new Editor.Label( FriendlyName( entry ) ), 1 );

			var keys = EditorShortcuts.GetKeys( ident );

			var bind = new EffigyKeyBind( section )
			{
				Value = string.IsNullOrEmpty( keys ) ? "None" : keys,
				ToolTip = "Click, then press the new key. Esc cancels.",
			};
			bind.ValueChanged = key => OnKeyBound( ident, key );

			row.Add( bind );

			row.Add( new Button( "Reset" ) { Clicked = () => OnKeyReset( ident, bind ) } );

			shown++;
		}

		if ( shown == 0 )
		{
			section.Layout.Add( new Editor.Label(
				"No rebindable shortcuts are registered — open Effigy once and reopen this window." )
			{
				Color = Theme.TextControl.WithAlpha( 0.6f ),
			} );
		}
	}

	/// <summary>The shortcuts this package owns, in a stable order: everything under the
	/// <c>effigy.</c> and <c>rig.</c> identifiers, plus the shared <c>editor.</c> commands it binds
	/// (undo, redo, save). Duplicate identifiers are collapsed to one row.</summary>
	private static List<EditorShortcuts.Entry> EffigyShortcuts()
	{
		var entries = EditorShortcuts.Entries ?? new List<EditorShortcuts.Entry>();

		return entries
			.Where( e => e.Identifier.StartsWith( "effigy." )
				|| e.Identifier.StartsWith( "rig." )
				|| e.Identifier.StartsWith( "editor." ) )
			.GroupBy( e => e.Identifier )
			.Select( g => g.First() )
			.OrderBy( e => e.Identifier, StringComparer.Ordinal )
			.ToList();
	}

	/// <summary>The editor's own name for a shortcut, with the raw identifier as the fallback — it
	/// is not always humanised, and a bare identifier still reads better than an empty row.</summary>
	private static string FriendlyName( EditorShortcuts.Entry entry ) =>
		string.IsNullOrWhiteSpace( entry.Name ) ? entry.Identifier : entry.Name;

	private void OnKeyBound( string ident, string key )
	{
		var overrides = EditorPreferences.ShortcutOverrides;
		overrides[ident] = key;
		EditorPreferences.ShortcutOverrides = overrides;

		// Re-register every window's keybinds so the new key is live immediately, not next session.
		EditorEvent.Run( "keybinds.update" );
	}

	private void OnKeyReset( string ident, KeyBind bind )
	{
		var overrides = EditorPreferences.ShortcutOverrides;
		overrides.Remove( ident );
		EditorPreferences.ShortcutOverrides = overrides;

		EditorEvent.Run( "keybinds.update" );

		var keys = EditorShortcuts.GetDefaultKeys( ident );
		bind.Value = string.IsNullOrEmpty( keys ) ? "None" : keys;
	}

	/// <summary>Zero is the adaptive step rather than "no grid", so it has to say so — a dropdown
	/// reading "0" next to a visible lattice is a puzzle.</summary>
	internal static string Describe( float step ) => step <= 0f ? "Automatic" : $"{step:0.###} u";
}
