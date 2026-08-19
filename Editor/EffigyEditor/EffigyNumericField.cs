using Editor;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// Onshape's numeric field: a box you type a number OR an expression into.
///
/// This replaces the FloatSlider that every dimension used to be, and the replacement is the
/// single biggest workflow fix in the tool. A parametric modeller whose dimensions can only be
/// dragged is not a parametric modeller - you cannot type 4, you cannot type 1/8, and you cannot
/// say "the same as the last one". Worse, most of Effigy's FloatParams declare min 0.0001 and no
/// maximum at all (see BasicFeatures.cs), so the old slider invented a 0..9999 range and gave you
/// a hundred thousand steps of travel to hit a value with.
///
/// Sliders are not gone, they are demoted: EffigyFeatureDialog pairs one alongside this field only
/// when the parameter declares finite bounds narrow enough for dragging to mean something. An
/// unbounded length gets the field alone, which is what Onshape shows.
///
/// WHERE THIS DIVERGES FROM ONSHAPE, DELIBERATELY. Onshape swaps the text between the expression
/// and its result as the field gains and loses focus. That needs focus events on LineEdit which
/// are not proven against this editor's API, so instead the evaluated result is shown in a label
/// beside the field the moment it differs from what you typed: `1/8` sits there reading `= 0.125`.
/// Same information, continuously, and it costs no API surface that RigControlEditor has not
/// already used in anger.
/// </summary>
internal sealed class EffigyNumericField : Widget
{
	private readonly LineEdit _edit;
	private readonly Editor.Label _readout;

	/// <summary>The parameter's unit, passed through to the evaluator - "deg" makes `rad` and `°`
	/// suffixes legal, anything else makes the field a plain number.</summary>
	public string Unit { get; init; }

	public float Min { get; init; } = float.MinValue;
	public float Max { get; init; } = float.MaxValue;

	/// <summary>Round to whole numbers on the way out, for IntParam.</summary>
	public bool Integer { get; init; }

	/// <summary>Fires only when the text parsed. A field mid-keystroke ("1/") holds the last good
	/// value rather than pushing a NaN into the feature and blanking the viewport.</summary>
	public Action<float> ValueEdited { get; set; }

	private float _value;

	public float Value => _value;

	public EffigyNumericField( Widget parent, float value, string unit = null ) : base( parent )
	{
		Unit = unit;
		_value = value;

		Layout = Layout.Row();
		Layout.Spacing = 6;

		_edit = new LineEdit( EffigyExpression.Format( value ), this );
		_edit.TextEdited += OnTextEdited;
		Layout.Add( _edit, 1 );

		_readout = new Editor.Label( "" ) { Color = Theme.TextControl.WithAlpha( 0.55f ) };
		Layout.Add( _readout );
	}

	/// <summary>
	/// Push a value in from outside without firing ValueEdited.
	///
	/// Used when a paired slider moves, and when the dialog reopens on the same feature. It must
	/// not echo back out as an edit or the two controls drive each other in a loop.
	/// </summary>
	public void SetValue( float value )
	{
		_value = Clamp( value );

		if ( _edit.IsValid() )
			_edit.Text = EffigyExpression.Format( _value );

		ShowReadout( null );
	}

	private void OnTextEdited( string text )
	{
		if ( !EffigyExpression.TryEvaluate( text, Unit, out var parsed ) )
		{
			// Half-typed is the common case, not an error worth shouting about - the field just
			// stops agreeing with the model until it makes sense again.
			ShowReadout( "?", invalid: true );
			return;
		}

		var clamped = Clamp( parsed );

		// Show the evaluated number whenever it is not literally what was typed. That covers both
		// `1/8` becoming 0.125 and a value being clamped against the parameter's own limits, which
		// is the case that otherwise looks like the field ignoring you.
		ShowReadout( EffigyExpression.Format( clamped ) == text.Trim()
			? null
			: $"= {EffigyExpression.Format( clamped )}" );

		if ( clamped == _value )
			return;

		_value = clamped;
		ValueEdited?.Invoke( _value );
	}

	private float Clamp( float v )
	{
		if ( Integer )
			v = MathF.Round( v );

		return Math.Clamp( v, Min, Max );
	}

	private void ShowReadout( string text, bool invalid = false )
	{
		if ( !_readout.IsValid() )
			return;

		_readout.Text = text ?? "";
		_readout.Color = invalid ? Theme.Red : Theme.TextControl.WithAlpha( 0.55f );
	}
}
