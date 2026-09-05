using Editor;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

// ============================================================================
//  The workspace bar — which part of the pipeline you are working in.
//
//  WHAT WAS WRONG. Every toolset the editor has grew onto ONE bar. CAD's five
//  stages, sketch's four, sculpt's three and paint's one all took turns in the
//  same strip, and the only thing that ever said which was showing was the word
//  written small at the right of the tab row — a status readout, not a control.
//  Rig did not even get that: its tools lived in a dock panel on the right while
//  every other toolset lived in the bar, so "start rigging" was a different kind
//  of action from "start sculpting" for no reason a user could see.
//
//  Nothing was hidden and nothing was broken. It just kept growing, and a tool
//  that has grown for a while stops having a shape you can hold in your head.
//
//  WHAT THIS DOES. One row above the stage bar, four names, one selected:
//
//    ┌──────┬────────┬───────┬─────┐
//    │ CAD  │ Sculpt │ Paint │ Rig │
//    └──────┴────────┴───────┴─────┘
//      Sketch 2 | Solid 4 | Detail 5 | Repeat 4 | Finish 4        ✔ Finish
//      ▣ Primitive   ⬆ Extrude   ⟳ Revolve   ↝ Sweep   ⌂ Loft
//
//  The pipeline is now stated on screen rather than inferred from which glyphs
//  appeared, and it is a CONTROL: clicking Sculpt gets you sculpting, the way
//  clicking Extrude gets you an extrude. Picking a workspace also lays the docks
//  out for it and decides which of the viewport's eleven input handlers is live
//  — see EffigyWindow.Workspaces.cs, which owns all three of those.
//
//  TEXT ONLY, no icons, unlike every other bar in this editor. The workspaces
//  are a closed set of four that never changes and each has a name everybody
//  arriving already knows, so there is nothing for a glyph to disambiguate — it
//  would only add weight above the two rows that genuinely need it. Same
//  argument the stage TABS make against icons, one level up.
//
//  PAINTED, NOT LAID OUT, for the reason EffigyStageBar's header gives: a button
//  can only size itself to its own label inside a paint pass, so the row measures
//  while drawing, caches the rects, and hit-tests the cache.
// ============================================================================

/// <summary>
/// The four parts of the pipeline, in the order they are usually walked.
///
/// SKETCH IS NOT ONE OF THEM, deliberately. A sketch is something you open inside CAD and finish
/// again — it takes the stage bar over the way sculpt does, but you did not leave CAD to do it,
/// and putting it here would make the switcher answer "what am I doing right now" when the
/// question it exists to answer is "which part of the pipeline am I in". EffigyBarMode still
/// carries the finer distinction; this is the coarse one.
/// </summary>
internal enum EffigyWorkspace
{
	Cad,
	Sculpt,
	Paint,
	Rig,
}

/// <summary>
/// The switcher itself. Owns nothing but the selection and the painting — what a workspace MEANS
/// is entirely EffigyWindow.Workspaces.cs's business, reached through <see cref="Switched"/>.
/// </summary>
internal sealed class EffigyWorkspaceBar : Widget
{
	/// <summary>Height of the row. Shorter than the tool row and slightly taller than the tab row:
	/// it sits above both and wants to read as the outermost ring of chrome rather than as a third
	/// peer to them.</summary>
	public const float RowHeight = 32f;

	/// <summary>A workspace was clicked. Fires for the ALREADY-SELECTED one too — re-clicking Paint
	/// while painting is a reasonable way to ask for "put me back in the paint layout", and the
	/// window's entry paths are all no-ops when there is nothing to change.</summary>
	public Action<EffigyWorkspace> Switched { get; set; }

	public Color ChromeColor
	{
		get => _chrome;
		set { _chrome = value; Update(); }
	}

	/// <summary>Which workspace is lit. Set by the window from the bar mode, never by a click —
	/// the click asks, the window decides whether it happened. A sculpt that refuses to open (no
	/// cage, the feature errored) must not leave the switcher claiming you are in it.</summary>
	public EffigyWorkspace Selected
	{
		get => _selected;
		set
		{
			if ( _selected == value )
				return;

			_selected = value;
			Update();
		}
	}

	private static readonly (EffigyWorkspace Workspace, string Name, string Tip)[] Items =
	{
		(EffigyWorkspace.Cad, "CAD",
			"Sketches, solids and the feature tree — how the shape gets made"),
		(EffigyWorkspace.Sculpt, "Sculpt",
			"Brush the shape by hand. Opens the sculpt you were last in, or adds one"),
		(EffigyWorkspace.Paint, "Paint",
			"Lay colour onto a body's texture. Opens the paint you were last in, or adds one"),
		(EffigyWorkspace.Rig, "Rig",
			"Place bones and pin bodies to them, ready to export"),
	};

	private EffigyWorkspace _selected = EffigyWorkspace.Cad;
	private Color _chrome = Theme.ControlBackground;

	/// <summary>Where each item was drawn. Same cache-while-painting, hit-test-the-cache pattern as
	/// EffigyStageTabRow — a click before the first paint finds an empty list and does nothing.
	/// </summary>
	private readonly List<Rect> _rects = new();

	private int _hovered = -1;

	private const float Pad = 18f;
	private const float Gap = 3f;

	public EffigyWorkspaceBar( Widget parent ) : base( parent )
	{
		Cursor = CursorShape.Finger;
		MouseTracking = true;

		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedHeight = RowHeight;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		Paint.ClearPen();
		Paint.SetBrush( _chrome );
		Paint.DrawRect( new Rect( 0f, 0f, Width, Height ) );

		PaintItems();

		// A hairline under the row. The stage bar draws its own, so the two together give the
		// chrome a readable stack rather than one undifferentiated slab.
		Paint.ClearBrush();
		Paint.SetPen( Theme.Text.WithAlpha( 0.10f ), 1f );
		Paint.DrawLine( new Vector2( 0f, Height - 0.5f ), new Vector2( Width, Height - 0.5f ) );
	}

	private void PaintItems()
	{
		_rects.Clear();

		var x = Pad * 0.5f;

		for ( var i = 0; i < Items.Length; i++ )
		{
			var (workspace, name, _) = Items[i];
			var selected = workspace == _selected;

			Paint.SetDefaultFont( EffigyToolChrome.TabFontSize + 0.5f, selected ? 700 : 500 );

			var width = Paint.MeasureText( name ).x + Pad;
			var rect = new Rect( x, 4f, width, Height - 8f );

			_rects.Add( rect );

			// A FILLED PILL for the selected one, where the stage tabs below use a 2px underline.
			// The two are meant to look different: they are different kinds of choice — this one
			// changes what the whole window is for, that one changes which five buttons are on a
			// row — and drawing them the same way would make the outer choice look like one more
			// tab in a longer strip.
			if ( selected )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Blue.WithAlpha( 0.20f ) );
				Paint.DrawRect( rect, 4f );

				Paint.ClearBrush();
				Paint.SetPen( Theme.Blue.WithAlpha( 0.55f ), 1f );
				Paint.DrawRect( rect, 4f );
			}
			else if ( _hovered == i )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Text.WithAlpha( 0.06f ) );
				Paint.DrawRect( rect, 4f );
			}

			Paint.ClearBrush();
			Paint.SetDefaultFont( EffigyToolChrome.TabFontSize + 0.5f, selected ? 700 : 500 );
			Paint.SetPen( selected ? Theme.Blue : Theme.TextControl.WithAlpha( 0.8f ) );
			Paint.DrawText( rect, name, TextFlag.Center );

			x += width + Gap;
		}
	}

	private int ItemAt( Vector2 local )
	{
		for ( var i = 0; i < _rects.Count; i++ )
		{
			if ( _rects[i].IsInside( local ) )
				return i;
		}

		return -1;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		var item = ItemAt( e.LocalPosition );

		if ( item == _hovered )
			return;

		_hovered = item;
		ToolTip = item >= 0 ? Items[item].Tip : "";

		Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();

		_hovered = -1;
		Update();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		e.Accepted = true;

		var item = ItemAt( e.LocalPosition );

		if ( item >= 0 )
			Switched?.Invoke( Items[item].Workspace );
	}
}
