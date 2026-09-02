using Editor;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

// ============================================================================
//  The stage bar — the tool chrome that replaced three floating icon strips.
//
//  WHAT WAS WRONG WITH THE STRIPS. There were fifty buttons across the feature,
//  sketch and sculpt strips, every one a 54px monochrome square, every one in
//  the SAME top-left spot on the canvas, and the three swapped through that
//  spot without ever saying which was showing. One button in fifty carried a
//  text label, so finding a tool meant parking the cursor on each square in
//  turn and waiting for a tooltip. The strip also could not decline to paint
//  (see the old EffigyToolStrip.OnPaint), so it was not floating chrome at all
//  — it was an opaque band covering the top-left of the part.
//
//  WHAT THIS DOES INSTEAD. Two docked rows above the viewport:
//
//    Sketch 2 | Solid 4 | Detail 5 | Repeat 4 | Finish 4      SKETCH 1  ✔ Finish
//    ▣ Primitive   ⬆ Extrude   ⟳ Revolve   ↝ Sweep   ⌂ Loft
//
//  A stage is a named handful of tools. Only one stage's tools are on screen,
//  so the widest row is six buttons rather than nineteen, which buys enough
//  room to put the NAME on every button. Mode stops being a thing you infer
//  from which glyphs appeared: entering a sketch swaps the whole stage set and
//  writes the mode on the right of the tab row, next to the one control that
//  leaves it.
//
//  BOTH ROWS ARE ONE PAINTED WIDGET EACH, not a layout full of button widgets.
//  That is what lets a button size itself to its own label: Paint.MeasureText
//  only works inside a paint pass, so each row measures its items while drawing
//  and caches the rects it drew them in, and the mouse handlers hit-test that
//  cache. Same pattern EffigyResultStrip uses for its segments. The old strips
//  had to guess (EffigyToolStrip carried a hand-counted _contentWidth and the
//  one labelled button was hardcoded to 132px) and a guess that came out short
//  simply cut the last buttons off.
// ============================================================================

/// <summary>Which set of stages the bar is showing. The modes are exclusive by construction now —
/// one bar holding one list — where three strips with independent Visible flags could and did show
/// two at once.</summary>
internal enum EffigyBarMode
{
	Part,
	Sketch,
	Sculpt,
}

/// <summary>
/// The chrome constants and edge-decoration painting shared by everything that draws a tool
/// button, kept in one place so the bar, the result strip and the sculpt bar agree.
///
/// This is what is left of EffigyToolStrip. That class was a widget carrying a pile of statics
/// that had nothing to do with being a widget; the widget half is gone and the statics are all
/// that anything else was ever reaching for.
/// </summary>
internal static class EffigyToolChrome
{
	/// <summary>Height of the tab row — the strip of stage names.</summary>
	public const float TabRowHeight = 30f;

	/// <summary>Height of the tool row under it. 42 leaves a 30px button with 6px of air above and
	/// below, which is enough for the hover glow to read as hugging the button rather than as a
	/// line between rows.</summary>
	public const float ToolRowHeight = 42f;

	/// <summary>The bar as a whole. Named because the viewport prices its overlays against it.</summary>
	public const float BarHeight = TabRowHeight + ToolRowHeight;

	/// <summary>Height of a tool button inside the tool row.</summary>
	public const float ButtonHeight = 30f;

	/// <summary>
	/// Scale for the hand-painted glyphs, which are authored in a nominal 18x18 box.
	///
	/// 1.05 puts a 19px glyph next to an 11.5pt label. The old strip ran at 1.8 for a 32px glyph,
	/// and the reason that number was right is gone with the strip it was measured for: at 54px
	/// square with no label, the glyph WAS the button and had to carry it alone. Here the label
	/// carries the naming and the glyph is the thing your eye returns to once you know the name,
	/// so it wants to sit at text weight rather than shout over it.
	/// </summary>
	public const float IconScale = 1.05f;

	/// <summary>Point size of a tool button's label.</summary>
	public const float LabelFontSize = 11.5f;

	/// <summary>Point size of a stage tab's name.</summary>
	public const float TabFontSize = 11.5f;

	/// <summary>
	/// The colour of every CONFIRM action in this editor - accept a feature, finish a sketch,
	/// validate a binding.
	///
	/// A tick drawn in the same grey as everything else is a shape you have to go looking for, and
	/// the two on screen at once - accept the feature, finish the sketch - are the two most
	/// consequential buttons in the tool. Green for commit is one of the few colour conventions
	/// everyone already reads without being taught. Anything new that commits something should use
	/// this rather than picking its own green.
	/// </summary>
	public static Color ConfirmColor => Theme.Green;

	/// <summary>
	/// The colour the sketcher paints the outline of the face being sketched on, so a button that
	/// acts on that outline wears it too.
	///
	/// NOT ConfirmColor, even though both are green. That one means "this commits what you have
	/// been doing" and is spent carefully - two on screen at once is already the most it should be.
	/// This one means "this is about the part underneath", and it matches SketchReferenceColor in
	/// the viewport rather than the theme.
	/// </summary>
	public static Color ReferenceColor => new( 0.45f, 1f, 0.6f, 1f );

	/// <summary>
	/// Hover and press: a faint halo hugging the item's outer edge, and nothing else.
	///
	/// Drawn as concentric rounded rects fading INWARD from the edge. On the old floating strip
	/// that was forced — a widget clips its own painting, so a halo outside its rect was simply
	/// cut off — and it is kept here because it is also the right answer: the rows sit against
	/// chrome now, and a filled hover box behind every button would turn a row of five names into
	/// a row of five boxes.
	/// </summary>
	public static void PaintEdgeGlow( Rect rect, float strength )
	{
		const int Rings = 4;

		Paint.ClearBrush();

		for ( var i = 0; i < Rings; i++ )
		{
			var falloff = 1f - i / (float)Rings;

			Paint.SetPen( Theme.Text.WithAlpha( strength * falloff * falloff * 0.5f ), 1f );
			Paint.DrawRect( rect.Shrink( 0.5f + i ), 4f );
		}
	}

	/// <summary>A crisp ring at the edge, for a mode that is armed and has to stay visibly armed
	/// with the cursor somewhere else. A ring rather than a tint - same no-colour-change rule as
	/// PaintEdgeGlow, so armed reads as a different SHAPE, not a different colour.</summary>
	public static void PaintEdgeRing( Rect rect )
	{
		Paint.ClearBrush();
		Paint.SetPen( Theme.Text.WithAlpha( 0.75f ), 1.5f );
		Paint.DrawRect( rect.Shrink( 1f ), 4f );
	}

	/// <summary>
	/// "The tutorial means this one." A filled wash plus a solid ring, in the same yellow the
	/// tutorial panel uses for its current step and its bullets.
	///
	/// LOUDER THAN THE ARMED RING ON PURPOSE. Armed is a state you put the tool in and already
	/// know about; this is aimed at someone who has been told to press a button they have never
	/// seen. It matters less than it did — the buttons carry their names now, so the reader has
	/// something to search for besides a shape — but a tutorial that says "press Extrude" should
	/// still be able to point at Extrude.
	/// </summary>
	public static void PaintAttentionRing( Rect rect )
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.Yellow.WithAlpha( 0.22f ) );
		Paint.DrawRect( rect, 4f );

		Paint.ClearBrush();
		Paint.SetPen( Theme.Yellow.WithAlpha( 0.9f ), 2f );
		Paint.DrawRect( rect.Shrink( 1f ), 4f );
	}
}

/// <summary>One entry behind a tool's chevron: the same idea, done a different way. A corner
/// rectangle and a centre rectangle are one button with two variants, not two buttons — the same
/// arrangement Onshape uses, and the reason the sketch row is nineteen buttons for
/// twenty-four tools.</summary>
internal sealed class EffigyStageVariant
{
	public EffigyIcon Icon;
	public string Label;
	public string Tip;

	/// <summary>What arming this variant does. Carried per variant rather than the button holding
	/// an index into somebody's table, so the bar never needs to know what a tool IS.</summary>
	public Action Chosen;
}

/// <summary>
/// One tool on the bar.
///
/// A DATA OBJECT THE CALLER OWNS AND KEEPS, not a widget. The rows are repainted wholesale on
/// every stage change, so a button held from before a switch would belong to nothing; state that
/// has to survive — which variant is on the face, whether the tool is armed — lives here instead.
/// That is also what lets the window arm a tool that is sitting on a stage nobody is looking at.
/// </summary>
internal sealed class EffigyStageTool
{
	public EffigyIcon Icon;
	public string Label;
	public string Tip;

	/// <summary>A mode you turn on and leave on, rather than a command that happens once. Armed
	/// tools wear the edge ring.</summary>
	public bool Checkable;

	/// <summary>Overrides the glyph colour for a button that means something in particular — the
	/// finish tick is green like every other confirm, Use is the reference green. Null leaves it
	/// as ordinary chrome.</summary>
	public Color? IconColor;

	/// <summary>What a plain click does. Ignored when there are variants: those carry their own.</summary>
	public Action Clicked;

	public EffigyStageVariant[] Variants;

	// --- live state, owned here so a stage switch cannot lose it ---------------------------

	public bool Checked;
	public bool Attention;

	/// <summary>Which variant is on the face of the button. Onshape keeps the last one you picked
	/// there, so the second use of a centre rectangle is a single click.</summary>
	public int Current;

	public bool HasVariants => Variants is { Length: > 1 };

	private EffigyStageVariant Face =>
		Variants is { Length: > 0 } ? Variants[Math.Clamp( Current, 0, Variants.Length - 1 )] : null;

	public EffigyIcon FaceIcon => Face?.Icon ?? Icon;
	public string FaceLabel => Face?.Label ?? Label;
	public string FaceTip => Face?.Tip ?? Tip;

	/// <summary>Run whatever this button does right now — the current variant, or the plain
	/// action.</summary>
	public void Run()
	{
		if ( Face is { } variant )
			variant.Chosen?.Invoke();
		else
			Clicked?.Invoke();
	}
}

/// <summary>A named handful of tools, and whether they can be used yet.</summary>
internal sealed class EffigyStage
{
	public string Name;

	/// <summary>
	/// Why this stage cannot be entered, or null when it can.
	///
	/// A REASON RATHER THAN A BOOL, and shown rather than hidden. The strip this replaced dropped
	/// seventeen of nineteen buttons on an empty studio so nobody could add an extrude with
	/// nothing to extrude — correct, and it meant the toolbar silently changed shape halfway
	/// through step one of the tutorial, and never said why it had been holding things back. A
	/// dimmed tab that says "draw a sketch first" teaches the same rule and stays still.
	/// </summary>
	public string LockedReason;

	public readonly List<EffigyStageTool> Tools = new();

	public bool Locked => !string.IsNullOrEmpty( LockedReason );

	public EffigyStage Add( EffigyStageTool tool )
	{
		Tools.Add( tool );
		return this;
	}
}

/// <summary>
/// The bar itself: a tab row over a tool row, docked above the viewport rather than floating on
/// it. Docked because it no longer has to hide — five or six named buttons is a piece of chrome
/// you read, where nineteen anonymous squares was a wall you wanted off your model.
/// </summary>
internal sealed class EffigyStageBar : Widget
{
	private readonly EffigyStageTabRow _tabs;
	private readonly EffigyStageToolRow _tools;

	private readonly List<EffigyStage> _stages = new();
	private int _selected;

	/// <summary>Raised after the visible stage changes, so the owner can re-apply anything that
	/// lives on the buttons — armed states, the tutorial's highlight.</summary>
	public Action StageChanged { get; set; }

	/// <summary>The mode this bar is in - "SKETCH 1", "SCULPT" - or null in the ordinary part
	/// studio. Painted at the right of the tab row next to the control that leaves it.</summary>
	public string Mode
	{
		get => _tabs.Mode;
		set { _tabs.Mode = value; _tabs.Update(); }
	}

	/// <summary>The green "leave this mode" control, or null for no mode. Label and action.</summary>
	public void SetFinish( string label, Action clicked )
	{
		_tabs.FinishLabel = label;
		_tabs.FinishClicked = clicked;
		_tabs.Update();
	}

	/// <summary>The bar's own background, kept in step with the palette by ApplyPalette. The bar
	/// is chrome, so it takes the chrome colour rather than the viewport's.</summary>
	public Color ChromeColor
	{
		get => _tabs.ChromeColor;
		set
		{
			_tabs.ChromeColor = value;
			_tools.ChromeColor = value;
			Refresh();
		}
	}

	public EffigyStageBar( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Spacing = 0;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		_tabs = new EffigyStageTabRow( this ) { Bar = this };
		_tools = new EffigyStageToolRow( this ) { Bar = this };

		Layout.Add( _tabs );
		Layout.Add( _tools );

		FixedHeight = EffigyToolChrome.BarHeight;
	}

	public IReadOnlyList<EffigyStage> Stages => _stages;

	public EffigyStage Current =>
		_selected >= 0 && _selected < _stages.Count ? _stages[_selected] : null;

	public int SelectedIndex => _selected;

	/// <summary>
	/// Replace the whole stage set — which is what entering a sketch or a sculpt does.
	///
	/// The selection is re-derived rather than kept: the incoming stages are a different set of
	/// things, so an index into the old one means nothing. Landing on the first stage that is not
	/// locked is what stops a fresh studio opening on a tab whose tools all refuse to run.
	/// </summary>
	public void SetStages( IEnumerable<EffigyStage> stages, int select = -1 )
	{
		_stages.Clear();
		_stages.AddRange( stages );

		_selected = select >= 0 && select < _stages.Count && !_stages[select].Locked
			? select
			: FirstUsable();

		Refresh();
		StageChanged?.Invoke();
	}

	private int FirstUsable()
	{
		for ( var i = 0; i < _stages.Count; i++ )
		{
			if ( !_stages[i].Locked )
				return i;
		}

		return _stages.Count > 0 ? 0 : -1;
	}

	/// <summary>
	/// Point the bar at a stage. A locked one is refused rather than shown empty-handed — there is
	/// nothing on it that would run, and a tab that opens onto dead buttons teaches less than one
	/// that says why it will not open.
	/// </summary>
	public void Select( int index )
	{
		if ( index < 0 || index >= _stages.Count || index == _selected )
			return;

		if ( _stages[index].Locked )
			return;

		_selected = index;

		Refresh();
		StageChanged?.Invoke();
	}

	/// <summary>
	/// Bring the stage holding this tool to the front, and say whether it worked.
	///
	/// THE PIECE THAT MAKES STAGES SURVIVABLE. A tool armed from somewhere else — the L and C
	/// shortcuts, Escape falling back to Select, the tutorial pointing at Extrude — may live on a
	/// stage nobody is looking at, and a bar that showed one thing while the viewport did another
	/// would be worse than the strip it replaced.
	/// </summary>
	public bool Reveal( EffigyStageTool tool )
	{
		if ( tool is null )
			return false;

		// THE STAGE IN FRONT OF THE READER WINS. A tool can appear on more than one stage - Select
		// is on all four sketch stages, because falling back to it should never cost a tab change -
		// and without this the first stage holding it would drag the bar away from wherever the
		// reader actually is every time Escape dropped the tool back to Select.
		if ( Current is { } current && current.Tools.Contains( tool ) )
			return true;

		for ( var i = 0; i < _stages.Count; i++ )
		{
			if ( !_stages[i].Tools.Contains( tool ) || _stages[i].Locked )
				continue;

			_selected = i;

			Refresh();
			StageChanged?.Invoke();

			return true;
		}

		return false;
	}

	/// <summary>Which stage this tool sits on, or -1. Used to decide whether a repaint is enough
	/// or the bar has to move.</summary>
	public int StageOf( EffigyStageTool tool )
	{
		for ( var i = 0; i < _stages.Count; i++ )
		{
			if ( _stages[i].Tools.Contains( tool ) )
				return i;
		}

		return -1;
	}

	/// <summary>Repaint both rows. Cheap — each is one widget — and the only way to push a change
	/// made to a tool's data onto what is on screen.</summary>
	public void Refresh()
	{
		_tabs.Update();
		_tools.Update();
	}
}

/// <summary>
/// The stage names, the mode, and the way out of it.
///
/// One painted widget for every tab, hit-tested against the rects it drew last frame. See the
/// header of this file for why the rows are painted rather than composed of button widgets.
/// </summary>
internal sealed class EffigyStageTabRow : Widget
{
	public EffigyStageBar Bar { get; set; }
	public string Mode { get; set; }
	public string FinishLabel { get; set; }
	public Action FinishClicked { get; set; }
	public Color ChromeColor { get; set; } = Theme.ControlBackground;

	/// <summary>Where each tab was drawn, by stage index. Filled during OnPaint and read by the
	/// mouse handlers - a click before the first paint finds an empty list and does nothing, which
	/// is the correct answer for a bar nobody has seen yet.</summary>
	private readonly List<Rect> _tabRects = new();

	private Rect _finishRect;
	private int _hovered = -1;
	private bool _hoveredFinish;

	private const float Pad = 14f;
	private const float Gap = 2f;

	public EffigyStageTabRow( Widget parent ) : base( parent )
	{
		Cursor = CursorShape.Finger;
		MouseTracking = true;

		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedHeight = EffigyToolChrome.TabRowHeight;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		// The row is opaque chrome and says so. This is the one place in the tool where painting a
		// solid band is right rather than a bug to work around: the bar is docked above the
		// viewport now, so there is no 3D view behind it to show through.
		Paint.ClearPen();
		Paint.SetBrush( ChromeColor );
		Paint.DrawRect( new Rect( 0f, 0f, Width, Height ) );

		// A hairline under the row, so the bar has a bottom edge against the viewport instead of
		// bleeding into it.
		Paint.SetPen( Theme.Text.WithAlpha( 0.10f ), 1f );
		Paint.DrawLine( new Vector2( 0f, Height - 0.5f ), new Vector2( Width, Height - 0.5f ) );

		PaintTabs();
		PaintMode();
	}

	private void PaintTabs()
	{
		_tabRects.Clear();

		if ( Bar is null )
			return;

		var x = Pad * 0.5f;

		for ( var i = 0; i < Bar.Stages.Count; i++ )
		{
			var stage = Bar.Stages[i];
			var selected = i == Bar.SelectedIndex;

			Paint.SetDefaultFont( EffigyToolChrome.TabFontSize, selected ? 600 : 450 );

			var nameWidth = Paint.MeasureText( stage.Name ).x;
			var count = stage.Tools.Count.ToString();

			Paint.SetDefaultFont( EffigyToolChrome.TabFontSize - 2f, 500 );
			var countWidth = stage.Locked ? 0f : Paint.MeasureText( count ).x + 5f;

			var rect = new Rect( x, 0f, nameWidth + countWidth + Pad, Height );
			_tabRects.Add( rect );

			// The selected tab is an accent underline rather than a filled box. A fill would need
			// the row to be tall enough to carry it, and the underline is the one tab idiom that
			// reads at this height without stealing attention from the tools underneath.
			if ( selected )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Blue );
				Paint.DrawRect( new Rect( rect.Position.x, Height - 2f, rect.Size.x, 2f ) );
			}
			else if ( _hovered == i && !stage.Locked )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Text.WithAlpha( 0.06f ) );
				Paint.DrawRect( rect );
			}

			var textColor = stage.Locked
				? Theme.TextControl.WithAlpha( 0.35f )
				: selected ? Theme.Text : Theme.TextControl.WithAlpha( 0.8f );

			Paint.SetDefaultFont( EffigyToolChrome.TabFontSize, selected ? 600 : 450 );
			Paint.SetPen( textColor );
			Paint.DrawText( new Rect( rect.Position.x + Pad * 0.5f, 0f, nameWidth, Height ),
				stage.Name, TextFlag.LeftCenter );

			// The count is what makes the tabs worth having a number on: it says how much is behind
			// each one, so "where is Shell" has five candidates to check rather than nineteen.
			if ( !stage.Locked )
			{
				Paint.SetDefaultFont( EffigyToolChrome.TabFontSize - 2f, 500 );
				Paint.SetPen( selected ? Theme.Blue : Theme.TextControl.WithAlpha( 0.45f ) );
				Paint.DrawText( new Rect( rect.Position.x + Pad * 0.5f + nameWidth + 5f, 0f, countWidth, Height ),
					count, TextFlag.LeftCenter );
			}

			x += rect.Size.x + Gap;
		}
	}

	/// <summary>
	/// The mode, and the way out of it, at the right-hand end.
	///
	/// TOGETHER, AND ON PURPOSE. "Which mode am I in" and "how do I leave it" are one question
	/// asked twice, and the old strips answered neither: sketch mode was a different set of
	/// glyphs in the same rectangle, and the way out was a green tick at the end of nineteen
	/// squares.
	/// </summary>
	private void PaintMode()
	{
		_finishRect = default;

		if ( string.IsNullOrEmpty( Mode ) )
			return;

		var x = Width - Pad * 0.5f;

		if ( !string.IsNullOrEmpty( FinishLabel ) )
		{
			Paint.SetDefaultFont( EffigyToolChrome.TabFontSize, 600 );

			var textWidth = Paint.MeasureText( FinishLabel ).x;
			var width = textWidth + 34f;

			_finishRect = new Rect( x - width, 4f, width, Height - 9f );

			Paint.ClearPen();
			Paint.SetBrush( EffigyToolChrome.ConfirmColor.WithAlpha( _hoveredFinish ? 0.30f : 0.18f ) );
			Paint.DrawRect( _finishRect, 3f );

			Paint.SetPen( EffigyToolChrome.ConfirmColor );
			EffigyIcons.Draw( EffigyIcon.FinishSketchTool,
				new Vector2( _finishRect.Position.x + 13f, Height * 0.5f - 2f ),
				EffigyToolChrome.ConfirmColor, 0.85f );

			Paint.SetDefaultFont( EffigyToolChrome.TabFontSize, 600 );
			Paint.SetPen( EffigyToolChrome.ConfirmColor );
			Paint.DrawText( new Rect( _finishRect.Position.x + 24f, _finishRect.Position.y, textWidth + 6f, _finishRect.Size.y ),
				FinishLabel, TextFlag.LeftCenter );

			x -= width + 10f;
		}

		Paint.SetDefaultFont( EffigyToolChrome.TabFontSize - 1f, 600 );

		var modeWidth = Paint.MeasureText( Mode ).x;

		Paint.SetPen( Theme.Blue.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( x - modeWidth, 0f, modeWidth, Height ), Mode, TextFlag.LeftCenter );
	}

	private int TabAt( Vector2 local )
	{
		for ( var i = 0; i < _tabRects.Count; i++ )
		{
			if ( _tabRects[i].IsInside( local ) )
				return i;
		}

		return -1;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		var tab = TabAt( e.LocalPosition );
		var finish = _finishRect.Size.x > 0f && _finishRect.IsInside( e.LocalPosition );

		if ( tab == _hovered && finish == _hoveredFinish )
			return;

		_hovered = tab;
		_hoveredFinish = finish;

		// The locked reason IS the tooltip. It is the only place the rule gets explained, and a
		// dimmed tab with no explanation is the strip's silent disappearing act with extra steps.
		ToolTip = tab >= 0 && Bar is not null && tab < Bar.Stages.Count
			? Bar.Stages[tab].LockedReason ?? Bar.Stages[tab].Name
			: "";

		Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();

		_hovered = -1;
		_hoveredFinish = false;

		Update();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		e.Accepted = true;

		if ( _finishRect.Size.x > 0f && _finishRect.IsInside( e.LocalPosition ) )
		{
			FinishClicked?.Invoke();
			return;
		}

		var tab = TabAt( e.LocalPosition );

		if ( tab >= 0 )
			Bar?.Select( tab );
	}
}

/// <summary>
/// The current stage's tools, as icon-and-name buttons.
///
/// THE NAMES ARE THE WHOLE POINT. One button in fifty carried a label on the strips this replaced,
/// and the tooltip that stood in for the other forty-nine cost a cursor park and half a second
/// each. Showing only one stage at a time is what makes the room for them.
/// </summary>
internal sealed class EffigyStageToolRow : Widget
{
	public EffigyStageBar Bar { get; set; }
	public Color ChromeColor { get; set; } = Theme.ControlBackground;

	/// <summary>Where each tool was drawn. Same cache-what-was-painted arrangement as the tab row.
	/// Parallel to the current stage's Tools list.</summary>
	private readonly List<Rect> _rects = new();

	private int _hovered = -1;
	private int _pressed = -1;
	private bool _pressedChevron;

	private const float Pad = 9f;
	private const float IconWidth = 21f;
	private const float IconGap = 6f;
	private const float ChevronWidth = 16f;
	private const float Gap = 3f;

	public EffigyStageToolRow( Widget parent ) : base( parent )
	{
		Cursor = CursorShape.Finger;
		MouseTracking = true;

		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedHeight = EffigyToolChrome.ToolRowHeight;
	}

	private IReadOnlyList<EffigyStageTool> Tools
	{
		get
		{
			var stage = Bar?.Current;

			return stage is null ? Array.Empty<EffigyStageTool>() : stage.Tools;
		}
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		Paint.ClearPen();
		Paint.SetBrush( ChromeColor );
		Paint.DrawRect( new Rect( 0f, 0f, Width, Height ) );

		Paint.SetPen( Theme.Text.WithAlpha( 0.10f ), 1f );
		Paint.DrawLine( new Vector2( 0f, Height - 0.5f ), new Vector2( Width, Height - 0.5f ) );

		_rects.Clear();

		var tools = Tools;
		var x = 7f;
		var top = (Height - EffigyToolChrome.ButtonHeight) * 0.5f;

		for ( var i = 0; i < tools.Count; i++ )
		{
			var tool = tools[i];

			Paint.SetDefaultFont( EffigyToolChrome.LabelFontSize, 450 );

			var label = tool.FaceLabel ?? "";
			var textWidth = Paint.MeasureText( label ).x;
			var width = Pad + IconWidth + IconGap + textWidth + Pad
				+ (tool.HasVariants ? ChevronWidth : 0f);

			var rect = new Rect( x, top, width, EffigyToolChrome.ButtonHeight );
			_rects.Add( rect );

			PaintTool( tool, rect, i );

			x += width + Gap;
		}
	}

	private void PaintTool( EffigyStageTool tool, Rect rect, int index )
	{
		// Order matters: attention under the armed ring under the hover glow, so hovering a
		// highlighted armed button still reads as hovering it rather than as a fourth state.
		if ( tool.Attention )
			EffigyToolChrome.PaintAttentionRing( rect );

		if ( tool.Checked )
			EffigyToolChrome.PaintEdgeRing( rect );

		if ( _pressed == index || _hovered == index )
			EffigyToolChrome.PaintEdgeGlow( rect, _pressed == index ? 1.4f : 1f );

		var color = tool.IconColor ?? Theme.Text;

		Paint.SetPen( color );
		EffigyIcons.Draw( tool.FaceIcon,
			new Vector2( rect.Position.x + Pad + IconWidth * 0.5f, rect.Position.y + rect.Size.y * 0.5f ),
			color, EffigyToolChrome.IconScale );

		Paint.SetDefaultFont( EffigyToolChrome.LabelFontSize, 450 );
		Paint.SetPen( tool.IconColor ?? Theme.TextControl );
		Paint.DrawText(
			new Rect( rect.Position.x + Pad + IconWidth + IconGap, rect.Position.y,
				rect.Size.x - Pad * 2f - IconWidth - IconGap, rect.Size.y ),
			tool.FaceLabel ?? "", TextFlag.LeftCenter );

		if ( !tool.HasVariants )
			return;

		Paint.SetPen( Theme.TextLight.WithAlpha( _hovered == index ? 0.9f : 0.5f ) );
		Paint.DrawIcon( ChevronRect( rect ), "arrow_drop_down", 15, TextFlag.Center );
	}

	private static Rect ChevronRect( Rect rect ) =>
		new( rect.Position.x + rect.Size.x - ChevronWidth - 2f, rect.Position.y, ChevronWidth, rect.Size.y );

	private int ToolAt( Vector2 local )
	{
		for ( var i = 0; i < _rects.Count; i++ )
		{
			if ( _rects[i].IsInside( local ) )
				return i;
		}

		return -1;
	}

	/// <summary>
	/// The variant list, opened from the chevron.
	///
	/// NO ICONS ON THE OPTIONS. EffigyIcon is a DRAWN glyph — EffigyIcons.Draw paints into a
	/// widget's paint context — while a Menu option takes a Material Icon NAME, which is the very
	/// lookup these icons exist to get away from. The label and the check mark carry the variant.
	/// </summary>
	private void OpenVariantMenu( EffigyStageTool tool )
	{
		var menu = new Menu( this );

		for ( var i = 0; i < tool.Variants.Length; i++ )
		{
			var index = i;
			var variant = tool.Variants[i];

			var option = menu.AddOption( variant.Label, null, () =>
			{
				tool.Current = index;
				variant.Chosen?.Invoke();

				Update();
			} );

			option.Checkable = true;
			option.Checked = i == tool.Current;
		}

		menu.OpenAtCursor();
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		var index = ToolAt( e.LocalPosition );

		if ( index == _hovered )
			return;

		_hovered = index;

		var tools = Tools;
		ToolTip = index >= 0 && index < tools.Count ? tools[index].FaceTip ?? "" : "";

		Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();

		_hovered = -1;
		_pressed = -1;
		_pressedChevron = false;

		Update();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		e.Accepted = true;

		var index = ToolAt( e.LocalPosition );

		if ( index < 0 )
			return;

		var tools = Tools;

		if ( index >= tools.Count )
			return;

		// Which half was pressed decides what the release does. Recorded on PRESS so a drag that
		// starts on the chevron and ends over the glyph cannot arm a tool you did not ask for.
		_pressedChevron = tools[index].HasVariants && ChevronRect( _rects[index] ).IsInside( e.LocalPosition );
		_pressed = index;

		Update();
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		var index = _pressed;
		var chevron = _pressedChevron;

		_pressed = -1;
		_pressedChevron = false;

		Update();

		if ( index < 0 )
			return;

		var tools = Tools;

		// The row may have been repainted with a different stage between the press and the
		// release — a tool that swaps the stage set does exactly that — so the index is checked
		// against BOTH lists rather than trusted from the press.
		if ( index >= tools.Count || index >= _rects.Count || !_rects[index].IsInside( e.LocalPosition ) )
			return;

		var tool = tools[index];

		if ( chevron )
		{
			OpenVariantMenu( tool );
			return;
		}

		if ( tool.Checkable )
			tool.Checked = !tool.Checked;

		tool.Run();

		// The run may have swapped the whole stage set out from under us — finishing a sketch
		// does exactly that — so this is a repaint of whatever is there NOW, not of what was
		// clicked. Update on a widget whose row has been rebuilt is harmless.
		Update();
	}
}
