using Editor;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// An on/off switch with a knob that slides between the two ends.
///
/// HAND-PAINTED BECAUSE S&BOX HAS NO SWITCH. It ships <c>Checkbox</c>, which is a tick in a square
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

		// Same reasoning as EffigyToolButton: a plain Widget paints the system background, which
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
		public int PaletteIndex;
	}

	/// <summary>The spacings the dropdown offers, in sketch units. Zero is Automatic — the adaptive
	/// 1/2/5 step that keeps the grid about a constant size on screen at any zoom.</summary>
	private static readonly float[] Spacings = { 0f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 25f };

	private Values _values;

	private readonly Action<Values> _changed;

	public EffigySettingsWindow( Widget owner, Values values, Action<Values> changed )
	{
		_values = values;
		_changed = changed;

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
		Size = new Vector2( 400, 340 );

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

		Heading( canvas, "Grid" );

		AddSwitch( canvas, "Show plane grid",
			"The lattice inside every plane's outline - the three reference planes and the one you "
			+ "are sketching on.",
			_values.ShowGrid,
			value => { _values.ShowGrid = value; Changed(); } );

		// --- spacing -------------------------------------------------------------------------

		var spacingRow = canvas.Layout.AddRow();

		spacingRow.Add( new Editor.Label( "Grid spacing" ) );
		spacingRow.AddStretchCell();

		var spacing = new ComboBox( canvas )
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

		Heading( canvas, "Snapping" );

		AddSwitch( canvas, "Snap to grid",
			"Round the cursor to the nearest grid intersection. Off draws freehand on the plane.",
			_values.SnapToGrid,
			value => { _values.SnapToGrid = value; Changed(); } );

		AddSwitch( canvas, "Snap to points",
			"Jump the cursor onto existing sketch points. This is what closes a chain - without it "
			+ "two clicks in the same spot leave two points a hair apart and the profile will not "
			+ "extrude.",
			_values.SnapToPoints,
			value => { _values.SnapToPoints = value; Changed(); } );

		// --- the palette ---------------------------------------------------------------------

		Heading( canvas, "Appearance" );

		var paletteRow = canvas.Layout.AddRow();

		paletteRow.Add( new Editor.Label( "Colour palette" ) );
		paletteRow.AddStretchCell();

		var combo = new ComboBox( canvas ) { MinimumWidth = 150 };

		for ( var i = 0; i < EffigyPalette.All.Length; i++ )
		{
			var index = i;

			combo.AddItem( EffigyPalette.All[index].Name,
				onSelected: () => { _values.PaletteIndex = index; Changed(); },
				selected: index == _values.PaletteIndex );
		}

		paletteRow.Add( combo );

		canvas.Layout.AddStretchCell();

		Canvas = canvas;
	}

	private void Changed() => _changed?.Invoke( _values );

	private static void Heading( Widget canvas, string text )
	{
		var label = canvas.Layout.Add( new Editor.Label( text ) );

		label.SetStyles( "font-weight: 600;" );
	}

	/// <summary>A labelled row with the switch pushed out to the right edge, which is the shape
	/// every one of these settings wants.</summary>
	private static void AddSwitch( Widget canvas, string label, string tip, bool value, Action<bool> changed )
	{
		var row = canvas.Layout.AddRow();

		row.Add( new Editor.Label( label ) { ToolTip = tip } );
		row.AddStretchCell();

		var toggle = new EffigyToggleSwitch( canvas, value ) { ToolTip = tip };

		toggle.ValueChanged = changed;

		row.Add( toggle );
	}

	/// <summary>Zero is the adaptive step rather than "no grid", so it has to say so — a dropdown
	/// reading "0" next to a visible lattice is a puzzle.</summary>
	private static string Describe( float step ) => step <= 0f ? "Automatic" : $"{step:0.###} u";
}
