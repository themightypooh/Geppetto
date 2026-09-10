using Editor;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The citizen switch, at the right-hand end of the tool row.
///
/// WHY IT IS NOT ONLY IN SETTINGS. The stand-in is scenery in the viewport, and when it is in
/// the way of the part the control that hides it has to be next to the part — a dialog three
/// menus away is where you go to turn it on, not where you go to get it out of the shot. Same
/// argument the grid switch already made for paper; this is that argument for the ruler.
///
/// NOT A SECOND COPY OF THE VALUE. Reads and writes <see cref="EffigyViewport.ShowSizeReference"/>,
/// the same bool Settings sets, and re-reads it whenever it is shown so the two cannot disagree.
/// </summary>
internal sealed class EffigySizeReferenceButton : Widget
{
	public const float BarWidth = 96f;
	public const float BarHeight = EffigyToolChrome.ButtonHeight;

	private readonly Button _toggle;
	private readonly EffigyViewport _viewport;
	private bool _syncing;

	/// <summary>Raised after the viewport has been changed, so the window can save the cookie
	/// the same way the settings dialog's callback does.</summary>
	public Action Changed { get; set; }

	public EffigySizeReferenceButton( Widget parent, EffigyViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedHeight = BarHeight;
		FixedWidth = BarWidth;

		Layout = Layout.Row();
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		_toggle = new Button( "Citizen", "person", this )
		{
			ToolTip = "Stand the base citizen at the origin as a size ruler. Scenery only — it takes "
				+ "no clicks, joins no feature and is never exported.",
			FixedWidth = BarWidth,
		};

		_toggle.Clicked = Toggle;
		Layout.Add( _toggle, 1 );

		Refresh();
	}

	/// <summary>The tool row's background, so the button sits in the bar rather than as a panel
	/// on it. Same job <see cref="EffigySketchGridBar.GapColor"/> does for the grid switch.</summary>
	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	public void Refresh()
	{
		if ( !_viewport.IsValid )
			return;

		_syncing = true;

		try
		{
			var on = _viewport.ShowSizeReference;
			_toggle.Tint = on ? Theme.Blue : Theme.TextControl.WithAlpha( 0.5f );
		}
		finally
		{
			_syncing = false;
		}
	}

	private void Toggle()
	{
		if ( _syncing || !_viewport.IsValid )
			return;

		_viewport.ShowSizeReference = !_viewport.ShowSizeReference;
		Refresh();
		Changed?.Invoke();
	}
}
