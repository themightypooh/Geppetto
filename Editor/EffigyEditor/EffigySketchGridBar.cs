using Editor;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The grid switch and its spacing, floating in the corner of the viewport while a sketch is open.
///
/// WHY IT IS NOT ONLY IN SETTINGS. Both controls already live in Edit &gt; Settings, and that is the
/// right home for them when you are setting up how the tool behaves. It is the wrong home for them
/// while you are drawing: the grid is the paper, changing paper is part of drawing, and a dialog
/// three menus away is not somewhere you go mid-sketch. Every sketcher worth using puts these two
/// on the canvas.
///
/// NOT A SECOND COPY OF THE VALUE. This reads and writes the viewport's own properties - the same
/// two the settings window sets - and re-reads them every time it is shown, so the two controls can
/// never disagree about what is true. The spacing list itself is EffigySettingsWindow's, for the
/// same reason: two lists drift, and a value that exists on one dropdown and not the other reads as
/// the setting having been lost.
///
/// AT THE END OF THE TOOL ROW, not floating on the model. It belongs to the mode you are in, the
/// way the Line and Rectangle buttons beside it do, and chrome is where a mode's controls go - a
/// panel sitting on the part is one more thing between you and the thing you are drawing. Flush
/// right, so the tools growing and shrinking as you change stage never move it.
/// </summary>
internal sealed class EffigySketchGridBar : Widget
{
	/// <summary>The tool buttons' own height, so the two read as one row of controls rather than
	/// as a panel parked next to them.</summary>
	public const float BarHeight = EffigyToolChrome.ButtonHeight;
	public const float BarWidth = 210f;

	private readonly Button _toggle;
	private readonly ComboBox _spacing;

	/// <summary>Set while Refresh writes the controls. Both of them fire their change callbacks on
	/// assignment exactly as a click does, and without this a refresh would turn straight round and
	/// write the value it had only just read - harmless for the toggle, and for the dropdown a way
	/// to reset the spacing to whatever happened to be first.</summary>
	private bool _syncing;

	/// <summary>Raised after either control has changed the viewport, so the window can save the
	/// setting the same way the settings dialog's own callback does. The viewport is already
	/// updated by the time this fires.</summary>
	public Action Changed { get; set; }

	private readonly EffigyViewport _viewport;

	public EffigySketchGridBar( Widget parent, EffigyViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		// The same two flags every floating widget in this tool sets — a plain Widget paints the
		// system background, which is a white slab on the 3D view.
		TranslucentBackground = true;
		NoSystemBackground = true;

		Visible = false;
		FixedHeight = BarHeight;
		FixedWidth = BarWidth;

		Layout = Layout.Row();
		Layout.Spacing = 6;
		Layout.Margin = new Sandbox.UI.Margin( 6, 2, 6, 2 );

		_toggle = new Button( "Grid", "grid_on", this )
		{
			ToolTip = "Draw the grid on the face you are sketching on. The lines are the intervals "
				+ "the cursor snaps to.",
			FixedWidth = 74f,
		};

		_toggle.Clicked = ToggleGrid;

		Layout.Add( _toggle );

		_spacing = new ComboBox( this )
		{
			ToolTip = "How far apart the grid lines sit, in sketch units. Automatic fits the step "
				+ "to the face you are on and to how close the camera is.",
		};

		foreach ( var value in EffigySettingsWindow.Spacings )
		{
			var step = value;

			_spacing.AddItem( EffigySettingsWindow.Describe( step ), onSelected: () => SetSpacing( step ) );
		}

		Layout.Add( _spacing, 1 );

		Refresh();
	}

	/// <summary>The tool row's background, so the gaps between the two controls disappear into the
	/// bar rather than showing as a panel laid on it. A widget cannot simply decline to paint: a
	/// rect it leaves alone keeps whatever the previous frame put there.</summary>
	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	/// <summary>Point both controls at what the viewport actually holds. Called on every show, and
	/// again whenever the settings window has been the one to change it.</summary>
	public void Refresh()
	{
		if ( !_viewport.IsValid )
			return;

		_syncing = true;

		try
		{
			var on = _viewport.ShowPlaneGrid;

			_toggle.Icon = on ? "grid_on" : "grid_off";

			// Tinted rather than labelled on and off. The button says "Grid" either way, so the
			// only thing that has to change is whether it reads as engaged - and a label that
			// alternates between two words is a control you have to read to use.
			_toggle.Tint = on ? Theme.Blue : Theme.TextControl.WithAlpha( 0.5f );

			var spacing = _viewport.GridSpacing;
			var index = 0;

			for ( var i = 0; i < EffigySettingsWindow.Spacings.Length; i++ )
			{
				if ( MathF.Abs( EffigySettingsWindow.Spacings[i] - spacing ) < 0.0001f )
				{
					index = i;
					break;
				}
			}

			_spacing.CurrentIndex = index;
		}
		finally
		{
			_syncing = false;
		}
	}

	private void ToggleGrid()
	{
		if ( _syncing || !_viewport.IsValid )
			return;

		_viewport.ShowPlaneGrid = !_viewport.ShowPlaneGrid;

		Refresh();
		Changed?.Invoke();
	}

	private void SetSpacing( float step )
	{
		if ( _syncing || !_viewport.IsValid )
			return;

		_viewport.GridSpacing = step;

		// Choosing a spacing while the grid is off is asking to see it. Turning it on is what was
		// meant, and leaving it off would make the dropdown look broken.
		if ( !_viewport.ShowPlaneGrid )
			_viewport.ShowPlaneGrid = true;

		Refresh();
		Changed?.Invoke();
	}
}
