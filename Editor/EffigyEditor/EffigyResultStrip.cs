using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The ADD / REMOVE segmented control, floating under the tool strip.
///
/// WHY IT EXISTS. Result was a dropdown in the feature dialog, four rows down, reading "Auto" until
/// something changed it. That was enough to SET the mode and nowhere near enough to KNOW it: an
/// extrude that quietly added instead of cutting looks exactly like a boolean that failed, and the
/// gap between those two readings cost a whole session of blaming the engine's boolean for a
/// feature that was never asked to cut in the first place. A cut is the one operation here that
/// destroys material, and the mode it is in should be readable without looking for it.
///
/// THE DROPDOWN IS GONE NOW. This is the only control for Result — the dialog skips that parameter
/// (EffigyFeatureDialog.Rebuild) because a second, quieter copy of a mode indicator four rows down
/// is not redundancy, it is somewhere else to look.
///
/// FOUR SEGMENTS, NOT TWO. The mockup this is built from showed ADD | REMOVE, and two segments
/// cannot tell the truth: the default is Auto, so the honest answer to "which of these is lit" is
/// neither, and a control that lights ADD while the parameter says Auto is worse than no control.
/// Auto is also not a synonym for either one - it reads the sketch's attachment and can come out as
/// either - so it gets its own segment and says what it will resolve to underneath.
///
/// It is a VIEW of ExtrudeFeature.Result, not a second place that owns the value: clicking a segment
/// sets the one ChoiceParam the kernel reads and then goes through the same Edited path every other
/// parameter edit takes.
/// </summary>
internal sealed class EffigyResultStrip : Widget
{
	/// <summary>Segment size. Wider than it needs to be for the text: this is a mode indicator
	/// first and a button second, and it has to be legible from across the viewport.</summary>
	public const float SegmentWidth = 84f;

	public const float StripHeight = 30f;

	/// <summary>Corner radius. Half the height makes the ends semicircular - the pill in the
	/// mockup, rather than a rounded rectangle.</summary>
	private const float Radius = StripHeight * 0.5f;

	/// <summary>The parameter this is a view of. Null when no feature with a Result is open, which
	/// is also when the strip hides itself.</summary>
	private ChoiceParam _param;

	/// <summary>What Auto would actually do right now, as a Result index, or -1 if it cannot be
	/// worked out. Drawn under the Auto segment so "Auto" is not a shrug.</summary>
	private int _autoResolves = -1;

	private int _hovered = -1;

	/// <summary>Raised after a segment sets the parameter. The window hooks this to the same
	/// handler the dialog's own Edited runs, so a click here rebuilds exactly as one there does.
	/// </summary>
	public Action Changed { get; set; }

	public EffigyResultStrip( Widget parent ) : base( parent )
	{
		// Same two flags every floating widget in this tool sets. A plain Widget paints the system
		// background - a white slab on the 3D view - and without MouseTracking there is no hover.
		TranslucentBackground = true;
		NoSystemBackground = true;
		MouseTracking = true;

		Cursor = CursorShape.Finger;
		Visible = false;

		FixedHeight = StripHeight;
		FixedWidth = SegmentWidth * 4f;
	}

	/// <summary>
	/// Point the strip at whatever the dialog just opened, or at nothing.
	///
	/// Takes the FEATURE rather than the parameter so the decision about which features have a
	/// Result lives in one place, and so a feature type gaining one later needs no change here.
	/// </summary>
	public void Bind( Feature feature, Func<string, string> sketchHost )
	{
		_param = feature is SketchConsumingFeature consumer ? consumer.Result : null;

		FixedWidth = _param is null ? 0f : SegmentWidth * _param.Options.Length;

		_autoResolves = ResolveAuto( feature, sketchHost );

		Visible = _param is not null;
		_hovered = -1;

		Update();
	}

	/// <summary>
	/// What Auto would come out as, following the same rule the kernel does: a sketch on a face of
	/// an existing body adds to that body, a sketch on a global plane starts a new one.
	///
	/// Deliberately a READING of the rule rather than a call into it. The kernel decides this
	/// during a rebuild, with a FeatureContext that does not exist while a dialog is open, and
	/// building a fake one to ask would be a second implementation to drift. If the answer is not
	/// obvious from here it says nothing, which is the correct failure for a hint.
	/// </summary>
	private static int ResolveAuto( Feature feature, Func<string, string> sketchHost )
	{
		if ( feature is not SketchConsumingFeature consumer )
			return -1;

		if ( consumer.Sketch?.Value is not { Length: > 0 } sketchId )
			return -1;

		// A host means the sketch was placed on a face of something, which is the whole of the
		// rule. Auto never removes - see SketchConsumingFeature - so this is Add or New and
		// nothing else.
		return sketchHost?.Invoke( sketchId ) is { Length: > 0 } ? IndexAdd : IndexNew;
	}

	private const int IndexAuto = 0;
	private const int IndexNew = 1;
	private const int IndexAdd = 2;
	private const int IndexRemove = 3;

	/// <summary>
	/// Short, upper case, and NOT the parameter's own labels.
	///
	/// ChoiceParam's options are written for a dropdown - "Remove from the body it cuts into" - and
	/// they are the right text there, where there is room and no context. Here the whole point is
	/// that it reads at a glance from across the viewport.
	/// </summary>
	private static string Caption( int index ) => index switch
	{
		IndexAuto => "AUTO",
		IndexNew => "NEW",
		IndexAdd => "ADD",
		IndexRemove => "REMOVE",
		_ => "?"
	};

	/// <summary>
	/// REMOVE IS RED AND NOTHING ELSE IS. It is the only one of the four that destroys material,
	/// and the one whose being wrongly armed is expensive; the others are all additive and a
	/// mistake among them costs an undo. Blue for the rest matches every other selected state in
	/// this editor.
	/// </summary>
	private static Color Accent( int index ) => index == IndexRemove ? Theme.Red : Theme.Blue;

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		// The strip cannot decline to paint - an unpainted rect keeps whatever was in the buffer
		// keeps whatever was in the buffer. The pill's own body IS the background here.
		//
		// EVERYTHING BELOW IS BUILT FROM Width AND Height AND NOTHING ELSE. Rect.Left and
		// Rect.Bottom appear nowhere in this repo's working editor code, and an unproven member
		// name does not fail politely here - it fails the whole editor assembly and takes every
		// other tool with it. A widget's own rect starts at 0,0, so neither is needed: this is the
		// same idiom EffigyFeatureDialog paints with.
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground.WithAlpha( 0.92f ) );
		Paint.DrawRect( new Rect( 0f, 0f, Width, Height ), Radius );

		if ( _param is null )
			return;

		var count = _param.Options.Length;
		var width = Width / count;

		for ( var i = 0; i < count; i++ )
		{
			var left = width * i;
			var rect = new Rect( left, 0f, width, Height );
			var selected = _param.Index == i;

			if ( selected )
			{
				// The selected segment is a filled lozenge inset inside the pill, so the pill's own
				// outline stays continuous behind it and the fill reads as sitting IN the control
				// rather than replacing part of it.
				Paint.ClearPen();
				Paint.SetBrush( Accent( i ).WithAlpha( 0.9f ) );
				Paint.DrawRect( rect.Shrink( 3f ), Radius - 3f );
			}
			else if ( _hovered == i )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Text.WithAlpha( 0.1f ) );
				Paint.DrawRect( rect.Shrink( 3f ), Radius - 3f );
			}

			// A divider between unselected neighbours only. Drawing one against a filled segment
			// puts a line through the accent for no reason - the fill is already the boundary.
			if ( i > 0 && !selected && _param.Index != i - 1 )
			{
				Paint.SetPen( Theme.Text.WithAlpha( 0.18f ), 1f );
				Paint.DrawLine( new Vector2( left, 7f ), new Vector2( left, Height - 7f ) );
			}

			Paint.SetDefaultFont( 11f, selected ? 700 : 500 );
			Paint.SetPen( selected ? Color.White : Theme.TextControl.WithAlpha( 0.75f ) );
			Paint.DrawText( rect, Caption( i ), TextFlag.Center );
		}

		PaintAutoHint( width );
	}

	/// <summary>
	/// A dot under Auto's segment, in the colour of whatever Auto is going to do.
	///
	/// Without it "Auto" is the one setting that does not say what it does, which is exactly the
	/// state that started all this - an extrude reading Auto, adding, and looking like a cut that
	/// silently failed.
	/// </summary>
	private void PaintAutoHint( float width )
	{
		if ( _autoResolves < 0 || _param.Index != IndexAuto )
			return;

		const float radius = 2.5f;

		var centre = new Vector2( width * 0.5f, Height - 6f );

		Paint.ClearPen();
		Paint.SetBrush( Accent( _autoResolves ) );

		// A RECT, not a centre and a radius. Both proven call sites in this repo (RigHelpBox,
		// RigTimeline) pass the bounding box; there is no centre+radius overload to reach for.
		Paint.DrawCircle( new Rect( centre.x - radius, centre.y - radius, radius * 2f, radius * 2f ) );
	}

	private int SegmentAt( Vector2 local )
	{
		if ( _param is null || Width <= 0f )
			return -1;

		var index = (int)(local.x / (Width / _param.Options.Length));

		return index >= 0 && index < _param.Options.Length ? index : -1;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		var was = _hovered;
		_hovered = SegmentAt( e.LocalPosition );

		if ( was != _hovered )
		{
			ToolTip = _hovered >= 0 && _param is not null ? _param.Options[_hovered] : "";
			Update();
		}
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();

		_hovered = -1;
		Update();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton || _param is null )
			return;

		// ACCEPTED WHATEVER HAPPENS, including a click that changes nothing. The strip floats on the
		// 3D canvas, and an unaccepted press there is a camera orbit that also scatters the view -
		// the same reason EffigyViewport excludes the tool strips from its hover test.
		e.Accepted = true;

		var index = SegmentAt( e.LocalPosition );

		if ( index < 0 || index == _param.Index )
			return;

		_param.Index = index;

		Update();

		Changed?.Invoke();
	}
}
