using Editor;
using Effigy;
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

		_edit = new LineEdit( Expression.Format( value ), this );
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
			_edit.Text = Expression.Format( _value );

		ShowReadout( null );
	}

	private void OnTextEdited( string text )
	{
		if ( !Expression.TryEvaluate( text, Unit, out var parsed ) )
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
		ShowReadout( Expression.Format( clamped ) == text.Trim()
			? null
			: $"= {Expression.Format( clamped )}" );

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

/// <summary>
/// Drag-to-scrub, shared by the coloured axis letters and the plain row labels.
///
/// The value is read at the START of the drag and every later position is an offset from it, never
/// an accumulation of per-frame deltas. Accumulating drifts: each step gets clamped and rounded on
/// its way through the parameter, and those roundings add up until the number no longer tracks the
/// cursor. Anchoring to where the drag began means letting go and starting again always lands on
/// the same value for the same travel.
///
/// A subclass supplies only what it paints. Min/Max default to unbounded, so a handle that does
/// not set them scrubs freely — which is exactly what an extrude Distance wants.
/// </summary>
internal abstract class EffigyScrub : Widget
{
	/// <summary>Units per pixel of travel. A shade under a hundredth, so a whole unit is about the
	/// width of the dialog and the common case — nudging a part a few tenths — is a short drag.</summary>
	public float Sensitivity { get; init; } = 0.008f;

	/// <summary>The range the scrub is held to. Unbounded by default, so a length with no declared
	/// maximum drags to any distance; a min-bounded parameter (a positive-only thickness) stops at
	/// its floor instead of pushing a value the feature will reject.</summary>
	public float Min { get; init; } = float.MinValue;
	public float Max { get; init; } = float.MaxValue;

	/// <summary>Reads the value as it is now, so a drag starts from wherever typing left it.</summary>
	public Func<float> Value { get; set; }

	/// <summary>Called on every move with the new value.</summary>
	public Action<float> Dragged { get; set; }

	protected bool Dragging;
	private float _startValue;
	private float _startX;

	protected EffigyScrub( Widget parent ) : base( parent )
	{
		// SizeH says "this scrubs" before the first drag, which is the only hint the control can
		// give that it does anything at all.
		Cursor = CursorShape.SizeH;
		MouseTracking = true;

		TranslucentBackground = true;
		NoSystemBackground = true;
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton || Value is null )
			return;

		Dragging = true;
		_startValue = Value();
		_startX = e.LocalPosition.x;

		Update();
		e.Accepted = true;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		if ( !Dragging )
			return;

		// The button coming up somewhere this widget never heard about — over the viewport, off the
		// window — still ends the drag. Without this the field keeps scrubbing on a dead button.
		if ( !e.ButtonState.HasFlag( MouseButtons.Left ) )
		{
			EndDrag();
			return;
		}

		var step = Sensitivity;

		if ( e.HasShift )
			step *= 0.1f;
		else if ( e.HasCtrl )
			step *= 10f;

		var v = _startValue + (e.LocalPosition.x - _startX) * step;
		Dragged?.Invoke( Math.Clamp( v, Min, Max ) );

		e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( Dragging )
			EndDrag();
	}

	private void EndDrag()
	{
		Dragging = false;
		Update();
	}
}

/// <summary>
/// The coloured axis letter beside a Vec3 field, dragged sideways to scrub the number.
///
/// THE LETTER RATHER THAN THE FIELD. Dragging on the number itself is what Blender does, but the
/// number here is a real LineEdit you type expressions into, and a horizontal drag there already
/// means selecting text. Unity puts the scrub on the label for the same reason. The letter was
/// sitting there as decoration anyway, and it is already colour-coded to the axis it drives.
/// </summary>
internal sealed class EffigyAxisHandle : EffigyScrub
{
	private readonly string _label;
	private readonly Color _colour;

	public EffigyAxisHandle( Widget parent, string label, Color colour ) : base( parent )
	{
		_label = label;
		_colour = colour;

		ToolTip = $"Drag to change {label} — hold Shift for fine, Ctrl for coarse";

		FixedWidth = 16f;
	}

	protected override void OnPaint()
	{
		// Brighter while it is being used or pointed at, so a scrub in progress is visible even
		// though the cursor has usually left the letter by then.
		var strength = Dragging ? 1f : IsUnderMouse ? 0.85f : 0.65f;

		Paint.SetDefaultFont( 8f, 600 );
		Paint.SetPen( _colour.WithAlpha( strength ) );
		Paint.DrawText( LocalRect, _label, TextFlag.Center );
	}
}

/// <summary>
/// A parameter row's label that also scrubs its number.
///
/// The scalar rows carried a dead label while a Vec3's coloured letters could be dragged, so a
/// part's position could be nudged by hand but an extrude's Distance — the number you most want to
/// feel your way to — could only be typed, or dragged on a slider that exists only when the
/// parameter declares finite bounds, which Distance does not. This puts the scrub every Vec3 axis
/// already had onto the label of any scalar row, which is where Onshape and Fusion both put "drag
/// to any distance". Left-aligned and full width because it is still the row's label; the SizeH
/// cursor and the tint under the mouse are the tell that it does more than name the row.
/// </summary>
internal sealed class EffigyScrubLabel : EffigyScrub
{
	private readonly string _label;

	public EffigyScrubLabel( Widget parent, string label ) : base( parent )
	{
		_label = label;

		ToolTip = $"Drag to change {label} — hold Shift for fine, Ctrl for coarse";

		FixedWidth = 110f;
	}

	protected override void OnPaint()
	{
		var active = Dragging || IsUnderMouse;

		Paint.SetDefaultFont( 8f );
		Paint.SetPen( active ? Theme.Blue : Theme.TextControl.WithAlpha( 0.9f ) );
		Paint.DrawText( LocalRect, _label, TextFlag.LeftCenter );
	}
}
