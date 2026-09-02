using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The numbers a sculpt needs on screen: brush radius, strength, and which level is being worked.
///
/// WHY A SECOND ROW RATHER THAN MORE BUTTONS. The strips above are icons, and radius and strength
/// are values you type as often as you nudge — the same argument EffigyNumericField was built on for
/// feature dialogs. They also want a readout that says what the level costs, because levels are
/// exponential and the jump from 4 to 6 is sixteen times the vertices; a level control with no
/// number beside it is a way to hang the editor politely.
///
/// It sits under the sculpt strip the way the result strip sits under the feature strip: orthogonal
/// to the tool buttons rather than competing with them for the same spot.
/// </summary>
internal sealed class EffigySculptBar : Widget
{
	public const float BarHeight = 28f;

	private readonly EffigyNumericField _radius;
	private readonly EffigyNumericField _strength;
	private readonly Editor.Label _level;

	private SculptSession _session;

	/// <summary>Raised when something here changed a value the viewport should redraw for.</summary>
	public Action Changed { get; set; }

	public EffigySculptBar( Widget parent ) : base( parent )
	{
		// The same two flags every floating widget in this tool sets — a plain Widget paints the
		// system background, which is a white slab on the 3D view.
		TranslucentBackground = true;
		NoSystemBackground = true;

		Visible = false;
		FixedHeight = BarHeight;
		FixedWidth = 460f;

		Layout = Layout.Row();
		Layout.Spacing = 8;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		Layout.Add( new Editor.Label( "Radius" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_radius = new EffigyNumericField( this, 0.25f, "u" )
		{
			Min = 1e-4f,
			ValueEdited = OnRadiusEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _radius );

		Layout.Add( new Editor.Label( "Strength" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_strength = new EffigyNumericField( this, 0.05f )
		{
			ValueEdited = OnStrengthEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _strength );

		_level = new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.75f ) };
		Layout.Add( _level, 1 );
	}

	/// <summary>The viewport's background, so the gaps between controls disappear into the 3D view.
	/// A floating strip cannot simply decline to paint: a rect a widget leaves alone keeps
	/// whatever the previous frame put there.</summary>
	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	public void Bind( SculptSession session )
	{
		_session = session;
		Visible = session is not null;

		if ( session is not null )
			Refresh();
	}

	/// <summary>Pull the readouts back into step after something else changed them — the X and M
	/// shortcuts, or a level button.</summary>
	public void Refresh()
	{
		if ( _session is null )
			return;

		_radius.SetValue( _session.Radius );
		_strength.SetValue( _session.Strength );

		var (vertices, faces) = _session.Cost( _session.Level );
		var top = _session.Sculpt.TopLevel;

		// The cost is stated outright, in the same spirit as the viewport's own "N units" readout:
		// this is the only place the price of the next level is visible before it is paid.
		var text = $"Level {_session.Level} of {top} · {vertices:N0} verts / {faces:N0} faces";

		// The view is not the model — see SculptFeature. Saying so here is what stops "I dropped to
		// L1 to work coarsely" turning into "my export lost the detail".
		if ( _session.Level < top )
			text += $" · showing {_session.Level}, model builds at {top}";

		if ( _session.MirrorX )
			text += " · mirrored";

		if ( _session.Masking )
			text += _session.Erasing ? " · unmasking" : " · masking";
		else if ( _session.ActiveMask is not null )
			text += $" · {_session.ActiveMask.ProtectedFraction:P0} held";

		_level.Text = text;
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

		_session.Strength = value;
		Changed?.Invoke();
	}
}
