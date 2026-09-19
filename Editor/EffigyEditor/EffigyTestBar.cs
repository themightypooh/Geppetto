using Editor;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The scrub for a clothing test pose: how far into it the wearer is. Floats near the model like
/// the sculpt and weight bars, and only while a pose is on.
///
/// WHY A SCRUB AND NOT JUST THE EXTREME. The extreme says whether a sleeve clips with the arm
/// all the way up; halfway says where it first catches, which is the shoulder seam you actually
/// have to loosen. It is the same one number every pose library has under its thumbnails.
/// </summary>
internal sealed class EffigyTestBar : Widget
{
	public const float BarHeight = 28f;

	private readonly Editor.Label _name;
	private readonly FloatSlider _amount;
	private readonly Editor.Label _readout;
	private bool _refreshing;

	/// <summary>The amount changed, 0..1.</summary>
	public Action<float> Scrubbed { get; set; }

	public float Amount => _amount.Value;

	public EffigyTestBar( Widget parent ) : base( parent )
	{
		TranslucentBackground = true;
		NoSystemBackground = true;
		Visible = false;
		FixedHeight = BarHeight;
		FixedWidth = 420f;

		Layout = Layout.Row();
		Layout.Spacing = 8;

		_name = new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.75f ) };
		Layout.Add( _name );

		Layout.Add( new Editor.Label( "Into it" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );
		_amount = new FloatSlider( this ) { Minimum = 0f, Maximum = 1f, Step = 0.01f, Value = 1f, MinimumWidth = 200f };
		_amount.OnValueEdited = () =>
		{
			if ( _refreshing )
				return;

			_readout.Text = $"{_amount.Value:P0}";
			Scrubbed?.Invoke( _amount.Value );
		};
		Layout.Add( _amount, 1 );

		_readout = new Editor.Label( "100%" ) { Color = Theme.TextControl.WithAlpha( 0.75f ), FixedWidth = 40f };
		Layout.Add( _readout );
	}

	public Color GapColor { get; set; } = Theme.ControlBackground;

	/// <summary>Show for a pose, at the amount it was just applied at.</summary>
	public void Show( string poseName, float amount )
	{
		_refreshing = true;
		_name.Text = poseName;
		_amount.Value = amount;
		_readout.Text = $"{amount:P0}";
		_refreshing = false;
		Visible = true;
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}
}
