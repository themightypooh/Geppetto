using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The controls a paint stroke needs on screen: the colour, the brush radius, and how hard it
/// presses. The same shape as EffigySculptBar and for the same reason — these are values about the
/// STROKE, not tools, so they float near the model rather than living on the stage bar.
///
/// The colour is a swatch that opens s&amp;box's own ColorPicker on click, rather than a second
/// hand-rolled colour wheel: the editor library already ships one, and this tool's rule is to spend
/// its own code only on things the engine does not have.
/// </summary>
internal sealed class EffigyPaintBar : Widget
{
	public const float BarHeight = 28f;

	private readonly PaintSwatch _swatch;
	private readonly EffigyNumericField _radius;
	private readonly EffigyNumericField _strength;

	private PaintSession _session;

	/// <summary>Raised when something here changed a value the viewport should redraw for.</summary>
	public Action Changed { get; set; }

	public EffigyPaintBar( Widget parent ) : base( parent )
	{
		// The same two flags every floating widget in this tool sets — a plain Widget paints the
		// system background, which is a white slab on the 3D view.
		TranslucentBackground = true;
		NoSystemBackground = true;

		Visible = false;
		FixedHeight = BarHeight;
		FixedWidth = 420f;

		Layout = Layout.Row();
		Layout.Spacing = 8;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		_swatch = new PaintSwatch( this ) { Picked = OnColorPicked };
		Layout.Add( _swatch );

		Layout.Add( new Editor.Label( "Radius" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_radius = new EffigyNumericField( this, 0.25f, "u" )
		{
			Min = 1e-4f,
			ValueEdited = OnRadiusEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _radius );

		Layout.Add( new Editor.Label( "Strength" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_strength = new EffigyNumericField( this, 1f )
		{
			ValueEdited = OnStrengthEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _strength );
	}

	/// <summary>The viewport's background, so the gaps between controls disappear into the 3D view.</summary>
	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	public void Bind( PaintSession session )
	{
		_session = session;
		Visible = session is not null;

		if ( session is not null )
			Refresh();
	}

	public void Refresh()
	{
		if ( _session is null )
			return;

		_swatch.Color = new Color( _session.R, _session.G, _session.B, _session.A );
		_swatch.Update();

		_radius.SetValue( _session.Radius );
		_strength.SetValue( _session.Strength );
	}

	private void OnColorPicked( Color color )
	{
		if ( _session is null )
			return;

		_session.R = color.r;
		_session.G = color.g;
		_session.B = color.b;
		_session.A = color.a;

		Changed?.Invoke();
	}

	private void OnRadiusEdited( float value )
	{
		if ( _session is null )
			return;

		// Clamped rather than refused: a zero radius makes BeginStroke throw, and a tool that throws
		// because somebody cleared a box is not a tool.
		_session.Radius = MathF.Max( value, 1e-4f );
		Changed?.Invoke();
	}

	private void OnStrengthEdited( float value )
	{
		if ( _session is null )
			return;

		_session.Strength = Math.Clamp( value, 0f, 1f );
		Changed?.Invoke();
	}
}

/// <summary>
/// A coloured square that opens the colour picker on click. A plain painted widget rather than a
/// Button, because a Button wants a label or an icon and this is neither — it is the colour, and the
/// colour is the whole control.
/// </summary>
internal sealed class PaintSwatch : Widget
{
	public Color Color = Color.White;
	public Action<Color> Picked;

	public PaintSwatch( Widget parent ) : base( parent )
	{
		Cursor = CursorShape.Finger;
		FixedWidth = 28f;
		FixedHeight = 22f;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Color );
		Paint.DrawRect( LocalRect.Shrink( 2f ), 4f );

		// A hairline so a near-white or near-black swatch does not vanish into the viewport.
		Paint.SetPen( Theme.Text.WithAlpha( 0.4f ), 1f );
		Paint.DrawRect( LocalRect.Shrink( 2f ), 4f );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		e.Accepted = true;

		var picker = ColorPicker.OpenColorPopup( Color, c =>
		{
			Color = c;
			Picked?.Invoke( c );
			Update();
		} );

		picker.HasAlpha = true;
		picker.IsHDR = false;
	}
}
