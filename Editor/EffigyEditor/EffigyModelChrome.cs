using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Model's chrome: the header, the palette and the catalog.
//
//  NOTHING DOCKED. The Model workspace used to run on the same two-row stage
//  bar as CAD — tabs over named buttons — and whatever was put on that bar,
//  it read as CAD, because it WAS the CAD bar. These three widgets replace
//  it in Model and sit on the viewport itself, the way a modeller's do:
//
//    EffigyViewportHeader  a strip across the top: the mode pills, the
//                          element chips, the toggles, the mesh check badge
//                          and the way into the catalog.
//    EffigyToolPalette     a column of icon tiles down the left: the tools
//                          you hold, with the key in the corner.
//    EffigyToolCatalog     every tool in the mode, grouped, with search on
//                          top. Space. This is what replaces a menu bar.
//
//  They draw EffigyStageTool and EffigyModeSegment — the same objects the
//  bar drew — so a tool's enabled state, tick, tip and reason are exactly as
//  live here as they were there, and the window builds tools once.
//
//  PAINTED, NOT LAID OUT, for the reason the stage bar's rows are: the
//  viewport is a rendering surface, and a child that declines to paint its
//  background shows whatever was in the buffer. Every one of these paints
//  its own ground, opaque.
// ============================================================================

/// <summary>The palette these three share. Model's own, a shade warmer than the editor's grey,
/// and one accent for "this is on" (blue, the mode) and one for "this is selected" (orange,
/// the geometry and the armed tool) — the modeller's convention.</summary>
internal static class EffigyModelChrome
{
	public static Color Ground => new( 0.122f, 0.133f, 0.153f );
	public static Color Control => new( 0.165f, 0.180f, 0.208f );
	public static Color ControlHi => new( 0.204f, 0.224f, 0.259f );
	public static Color Line => new( 0.188f, 0.204f, 0.235f );
	public static Color Text => new( 0.906f, 0.914f, 0.933f );
	public static Color Dim => new( 0.604f, 0.635f, 0.682f );
	public static Color Faint => new( 0.388f, 0.420f, 0.471f );
	public static Color Blue => new( 0.357f, 0.576f, 1f );
	public static Color BlueSoft => Blue.WithAlpha( 0.18f );
	public static Color Orange => new( 0.941f, 0.631f, 0.227f );
	public static Color OrangeSoft => Orange.WithAlpha( 0.16f );
	public static Color Green => new( 0.247f, 0.749f, 0.541f );
	public static Color Yellow => new( 0.902f, 0.753f, 0.290f );
	public static Color Red => new( 0.898f, 0.384f, 0.353f );

	public const float HeaderHeight = 36f;
	public const float Margin = 10f;

	/// <summary>A rounded panel with a hairline border, the ground every piece of chrome sits on.</summary>
	public static void PaintPanel( Rect rect, float radius, Color? fill = null )
	{
		Paint.ClearPen();
		Paint.SetBrush( fill ?? Ground );
		Paint.DrawRect( rect, radius );

		Paint.ClearBrush();
		Paint.SetPen( Line, 1f );
		Paint.DrawRect( rect.Shrink( 0.5f ), radius );
	}

	/// <summary>A tool's key, drawn small in a corner. From <see cref="EffigyStageTool.Key"/>.</summary>
	public static void PaintKey( Rect rect, string key, Color color )
	{
		if ( string.IsNullOrEmpty( key ) )
			return;

		Paint.SetDefaultFont( 7f, 500 );
		Paint.SetPen( color );
		Paint.DrawText( rect, key, TextFlag.RightBottom );
	}
}

/// <summary>
/// The strip across the top of the viewport in Model.
///
/// Left to right: the mode pills (Object | Edit | Sculpt | Retopo), the element chips with their
/// counts while editing, then at the right the toggles, the mesh check badge and the search box
/// that opens the catalog. Everything is a hit rect painted here; the window feeds the lists and
/// reads the clicks through the tools' own Clicked.
/// </summary>
internal sealed class EffigyViewportHeader : Widget
{
	public List<EffigyModeSegment> Modes { get; } = new();

	/// <summary>Vertex / Edge / Face — checkable tools. <see cref="Count"/> gives each its number.</summary>
	public List<EffigyStageTool> Elements { get; } = new();
	public Func<EffigyStageTool, string> Count { get; set; }

	/// <summary>X-ray, Mirror, Snap, Soft — checkable tools, drawn as chips with their icon.</summary>
	public List<EffigyStageTool> Toggles { get; } = new();

	/// <summary>The mesh check: 0 clean, 1 something to look at, 2 something wrong, and the words.</summary>
	public Func<(int Level, string Text)> Check { get; set; }
	public Action CheckClicked { get; set; }

	public Action SearchClicked { get; set; }

	private readonly List<(Rect Rect, Action Run, string Tip, bool Enabled)> _hits = new();
	private int _hovered = -1;

	public EffigyViewportHeader( Widget parent ) : base( parent )
	{
		MouseTracking = true;
		Cursor = CursorShape.Finger;
		FixedHeight = EffigyModelChrome.HeaderHeight;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		Paint.ClearPen();
		Paint.SetBrush( EffigyModelChrome.Ground );
		Paint.DrawRect( new Rect( 0f, 0f, Width, Height ) );

		Paint.SetPen( EffigyModelChrome.Line, 1f );
		Paint.DrawLine( new Vector2( 0f, Height - 0.5f ), new Vector2( Width, Height - 0.5f ) );

		_hits.Clear();

		var x = EffigyModelChrome.Margin;
		var cy = Height * 0.5f;

		// --- modes: one rounded group, the active one filled blue ---
		if ( Modes.Count > 0 )
		{
			Paint.SetDefaultFont( 11.5f, 600 );

			var widths = Modes.Select( m => Paint.MeasureText( m.Label ).x + 22f ).ToArray();
			var group = new Rect( x, cy - 13f, widths.Sum() + 2f * (Modes.Count + 1), 26f );

			EffigyModelChrome.PaintPanel( group, 6f, EffigyModelChrome.Control );

			var mx = group.Left + 2f;

			for ( var i = 0; i < Modes.Count; i++ )
			{
				var mode = Modes[i];
				var rect = new Rect( mx, group.Top + 2f, widths[i], group.Height - 4f );
				var index = _hits.Count;

				_hits.Add( (rect, mode.Clicked, mode.Tip, true) );

				if ( mode.Active )
				{
					Paint.ClearPen();
					Paint.SetBrush( EffigyModelChrome.Blue );
					Paint.DrawRect( rect, 4f );
				}
				else if ( _hovered == index )
				{
					Paint.ClearPen();
					Paint.SetBrush( EffigyModelChrome.ControlHi );
					Paint.DrawRect( rect, 4f );
				}

				Paint.SetDefaultFont( 11.5f, 600 );
				Paint.SetPen( mode.Active ? Color.White : EffigyModelChrome.Dim );
				Paint.DrawText( rect, mode.Label, TextFlag.Center );

				mx += widths[i] + 2f;
			}

			x = group.Right + 8f;
		}

		// --- elements: a joined segment of chips ---
		if ( Elements.Count > 0 )
		{
			for ( var i = 0; i < Elements.Count; i++ )
			{
				var tool = Elements[i];
				var count = Count?.Invoke( tool ) ?? "";

				Paint.SetDefaultFont( 11f, 500 );
				var w = 18f + Paint.MeasureText( tool.Label ).x + (count.Length > 0 ? 8f + Paint.MeasureText( count ).x : 0f) + 18f;
				var rect = new Rect( x, cy - 13f, w, 26f );

				PaintChip( rect, tool, tool.Label, count, first: i == 0, last: i == Elements.Count - 1 );

				x += w - 1f;
			}

			x += 9f;
		}

		// --- right side, laid out from the right edge back ---
		var rx = Width - EffigyModelChrome.Margin;

		// search box
		{
			var w = 168f;
			var rect = new Rect( rx - w, cy - 13f, w, 26f );
			var index = _hits.Count;
			_hits.Add( (rect, SearchClicked, "Every tool in this mode, with search (Space)", true) );

			Paint.ClearPen();
			Paint.SetBrush( _hovered == index ? EffigyModelChrome.Control : EffigyModelChrome.Ground.Darken( 0.2f ) );
			Paint.DrawRect( rect, 5f );
			Paint.ClearBrush();
			Paint.SetPen( _hovered == index ? EffigyModelChrome.Blue : EffigyModelChrome.Line, 1f );
			Paint.DrawRect( rect.Shrink( 0.5f ), 5f );

			Paint.SetPen( EffigyModelChrome.Faint );
			Paint.DrawIcon( new Rect( rect.Left + 6f, rect.Top, 18f, rect.Height ), "search", 14, TextFlag.Center );

			Paint.SetDefaultFont( 11f, 400 );
			Paint.SetPen( EffigyModelChrome.Faint );
			Paint.DrawText( rect.Shrink( 28f, 0f, 52f, 0f ), "Find a tool", TextFlag.LeftCenter );

			var kbd = new Rect( rect.Right - 48f, rect.Top + 5f, 42f, 16f );
			Paint.ClearPen();
			Paint.SetBrush( EffigyModelChrome.Control );
			Paint.DrawRect( kbd, 3f );
			Paint.SetDefaultFont( 8.5f, 500 );
			Paint.SetPen( EffigyModelChrome.Dim );
			Paint.DrawText( kbd, "Space", TextFlag.Center );

			rx = rect.Left - 6f;
		}

		// check badge
		if ( Check is not null )
		{
			var (level, text) = Check();

			Paint.SetDefaultFont( 11f, 500 );
			var w = 12f + 8f + Paint.MeasureText( text ).x + 10f;
			var rect = new Rect( rx - w, cy - 13f, w, 26f );
			var index = _hits.Count;
			_hits.Add( (rect, CheckClicked, "The mesh check. Click for the list and the fixes", true) );

			EffigyModelChrome.PaintPanel( rect, 5f, _hovered == index ? EffigyModelChrome.ControlHi : EffigyModelChrome.Control );

			var dot = level switch { 0 => EffigyModelChrome.Green, 1 => EffigyModelChrome.Yellow, _ => EffigyModelChrome.Red };
			Paint.ClearPen();
			Paint.SetBrush( dot );
			Paint.DrawCircle( new Vector2( rect.Left + 12f, cy ), new Vector2( 8f, 8f ) );

			Paint.SetDefaultFont( 11f, 500 );
			Paint.SetPen( level == 0 ? EffigyModelChrome.Dim : EffigyModelChrome.Text );
			Paint.DrawText( rect.Shrink( 22f, 0f, 8f, 0f ), text, TextFlag.LeftCenter );

			rx = rect.Left - 6f;
		}

		// toggles, right to left so the order in the list reads left to right
		for ( var i = Toggles.Count - 1; i >= 0; i-- )
		{
			var tool = Toggles[i];

			Paint.SetDefaultFont( 11f, 500 );
			var w = 30f + Paint.MeasureText( tool.Label ).x + 10f;
			var rect = new Rect( rx - w, cy - 13f, w, 26f );

			PaintChip( rect, tool, tool.Label, "", first: true, last: true, icon: true );

			rx = rect.Left - 4f;
		}
	}

	private void PaintChip( Rect rect, EffigyStageTool tool, string label, string count, bool first, bool last, bool icon = false )
	{
		var index = _hits.Count;
		_hits.Add( (rect, () => { if ( tool.Checkable ) tool.Checked = !tool.Checked; tool.Run(); }, tool.Enabled ? tool.Tip : tool.DisabledReason ?? tool.Tip, tool.Enabled) );

		var on = tool.Checked;
		var hovered = _hovered == index;

		Paint.ClearPen();
		Paint.SetBrush( on ? EffigyModelChrome.BlueSoft : hovered ? EffigyModelChrome.ControlHi : EffigyModelChrome.Control );
		Paint.DrawRect( rect, first && last ? 5f : 0f );

		Paint.ClearBrush();
		Paint.SetPen( on ? EffigyModelChrome.Blue : EffigyModelChrome.Line, 1f );
		Paint.DrawRect( rect.Shrink( 0.5f ), first && last ? 5f : 0f );

		var textColor = !tool.Enabled ? EffigyModelChrome.Faint : on ? EffigyModelChrome.Text : EffigyModelChrome.Dim;
		var tx = rect.Left + 9f;

		if ( icon )
		{
			EffigyIcons.Draw( tool.FaceIcon, new Vector2( rect.Left + 15f, rect.Top + rect.Height * 0.5f ), textColor, 0.72f );
			tx = rect.Left + 26f;
		}

		Paint.SetDefaultFont( 11f, 500 );
		Paint.SetPen( textColor );
		Paint.DrawText( new Rect( tx, rect.Top, rect.Width, rect.Height ), label, TextFlag.LeftCenter );

		if ( count.Length > 0 )
		{
			Paint.SetDefaultFont( 10f, 500 );
			Paint.SetPen( on ? EffigyModelChrome.Blue : EffigyModelChrome.Faint );
			Paint.DrawText( rect.Shrink( 0f, 0f, 9f, 0f ), count, TextFlag.RightCenter );
		}
	}

	private int HitAt( Vector2 p )
	{
		for ( var i = 0; i < _hits.Count; i++ )
			if ( _hits[i].Rect.IsInside( p ) )
				return i;
		return -1;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		var index = HitAt( e.LocalPosition );
		if ( index == _hovered )
			return;

		_hovered = index;
		ToolTip = index >= 0 ? _hits[index].Tip ?? "" : "";
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
		e.Accepted = true;

		if ( !e.LeftMouseButton )
			return;

		var index = HitAt( e.LocalPosition );
		if ( index < 0 || !_hits[index].Enabled )
			return;

		_hits[index].Run?.Invoke();
		Update();
	}
}

/// <summary>
/// The column of tool tiles down the left of the viewport: what you hold, per mode.
///
/// Icon tiles, the key in the corner, the name on hover, groups split by a hairline. Orange
/// when the tool is armed — the same orange as selected geometry, because an armed tool is the
/// thing your next click will do to that geometry.
/// </summary>
internal sealed class EffigyToolPalette : Widget
{
	private readonly List<EffigyStage> _groups = new();
	private readonly List<(Rect Rect, EffigyStageTool Tool)> _tiles = new();
	private int _hovered = -1;

	private const float Tile = 36f;
	private const float TileH = 34f;
	private const float Pad = 5f;
	private const float Gap = 2f;
	private const float Sep = 7f;

	public EffigyToolPalette( Widget parent ) : base( parent )
	{
		MouseTracking = true;
		Cursor = CursorShape.Finger;
	}

	/// <summary>Replace the groups. Null entries in a stage's tools are skipped; each stage is one group.</summary>
	public void SetGroups( IEnumerable<EffigyStage> groups )
	{
		_groups.Clear();

		foreach ( var g in groups ?? Array.Empty<EffigyStage>() )
		{
			if ( g is null || g.Tools.All( t => t is null ) )
				continue;

			_groups.Add( g );
		}

		var tiles = _groups.Sum( g => g.Tools.Count( t => t is not null ) );
		var height = Pad * 2f + tiles * TileH + Math.Max( 0, tiles - 1 ) * Gap + Math.Max( 0, _groups.Count - 1 ) * Sep;

		FixedWidth = Pad * 2f + Tile;
		FixedHeight = height;
		Update();
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		EffigyModelChrome.PaintPanel( new Rect( 0f, 0f, Width, Height ), 8f );

		_tiles.Clear();

		var y = Pad;

		for ( var g = 0; g < _groups.Count; g++ )
		{
			if ( g > 0 )
			{
				Paint.SetPen( EffigyModelChrome.Line, 1f );
				Paint.DrawLine( new Vector2( Pad + 3f, y + Sep * 0.5f ), new Vector2( Width - Pad - 3f, y + Sep * 0.5f ) );
				y += Sep;
			}

			foreach ( var tool in _groups[g].Tools )
			{
				if ( tool is null )
					continue;

				var rect = new Rect( Pad, y, Tile, TileH );
				var index = _tiles.Count;
				_tiles.Add( (rect, tool) );

				var on = tool.Checked && tool.Enabled;
				var hovered = _hovered == index && tool.Enabled;

				if ( on || hovered )
				{
					Paint.ClearPen();
					Paint.SetBrush( on ? EffigyModelChrome.OrangeSoft : EffigyModelChrome.Control );
					Paint.DrawRect( rect, 6f );

					if ( on )
					{
						Paint.ClearBrush();
						Paint.SetPen( EffigyModelChrome.Orange, 1f );
						Paint.DrawRect( rect.Shrink( 0.5f ), 6f );
					}
				}

				if ( tool.Attention )
					EffigyToolChrome.PaintAttentionRing( rect );

				var color = !tool.Enabled ? EffigyModelChrome.Faint.WithAlpha( 0.6f ) : on || hovered ? EffigyModelChrome.Text : EffigyModelChrome.Dim;

				EffigyIcons.Draw( tool.FaceIcon, new Vector2( rect.Left + Tile * 0.5f, rect.Top + TileH * 0.5f - 1f ), color, 0.95f );
				EffigyModelChrome.PaintKey( rect.Shrink( 0f, 0f, 3f, 1f ), tool.Key, on ? EffigyModelChrome.Orange : EffigyModelChrome.Faint );

				y += TileH + Gap;
			}
		}
	}

	private int TileAt( Vector2 p )
	{
		for ( var i = 0; i < _tiles.Count; i++ )
			if ( _tiles[i].Rect.IsInside( p ) )
				return i;
		return -1;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		var index = TileAt( e.LocalPosition );
		if ( index == _hovered )
			return;

		_hovered = index;

		if ( index >= 0 )
		{
			var tool = _tiles[index].Tool;
			var key = string.IsNullOrEmpty( tool.Key ) ? "" : $"   {tool.Key}";
			ToolTip = tool.Enabled ? $"{tool.Label}{key}\n{tool.FaceTip}" : $"{tool.Label}\n{tool.DisabledReason ?? tool.FaceTip}";
		}
		else
		{
			ToolTip = "";
		}

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
		e.Accepted = true;

		if ( !e.LeftMouseButton )
			return;

		var index = TileAt( e.LocalPosition );
		if ( index < 0 )
			return;

		var tool = _tiles[index].Tool;
		if ( !tool.Enabled )
			return;

		if ( tool.Checkable )
			tool.Checked = !tool.Checked;

		tool.Run();
		Update();
	}
}

/// <summary>
/// Every tool in the mode, grouped, behind one box: the catalog. Space opens it over the
/// viewport; typing filters; Enter runs the first match; Escape or a click on empty ground
/// closes it. It is the replacement for a menu bar, and it also teaches where things are —
/// each tool is shown under its group.
/// </summary>
internal sealed class EffigyToolCatalog : Widget
{
	private readonly LineEdit _search;
	private readonly List<EffigyStage> _groups = new();
	private readonly List<(Rect Rect, EffigyStageTool Tool)> _items = new();
	private EffigyStageTool _first;
	private int _hovered = -1;

	/// <summary>Called just before the catalog shows, so the owner can refresh enabled states.</summary>
	public Action Opening { get; set; }

	private const float ColumnWidth = 176f;
	private const float ItemHeight = 22f;
	private const float Inset = 24f;

	public EffigyToolCatalog( Widget parent ) : base( parent )
	{
		MouseTracking = true;
		Visible = false;

		_search = new LineEdit( this ) { PlaceholderText = "Type a tool: rip, bevel, grid fill…" };
		_search.Position = new Vector2( Inset, Inset );
		_search.FixedWidth = 380f;
		_search.FixedHeight = 30f;
		_search.TextChanged += _ => Update();
		_search.ReturnPressed += () =>
		{
			var first = _first;
			Close();
			first?.Run();
		};
	}

	public void SetGroups( IEnumerable<EffigyStage> groups )
	{
		_groups.Clear();
		_groups.AddRange( (groups ?? Array.Empty<EffigyStage>()).Where( g => g is not null ) );
	}

	public void Open()
	{
		Opening?.Invoke();
		_search.Text = "";
		Visible = true;
		Raise();
		_search.Focus();
		Update();
	}

	public void Close()
	{
		Visible = false;
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		if ( e.Key == KeyCode.Escape )
		{
			Close();
			e.Accepted = true;
			return;
		}

		base.OnKeyPress( e );
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		Paint.ClearPen();
		Paint.SetBrush( EffigyModelChrome.Ground.Darken( 0.15f ) );
		Paint.DrawRect( new Rect( 0f, 0f, Width, Height ) );

		Paint.SetDefaultFont( 10.5f, 400 );
		Paint.SetPen( EffigyModelChrome.Faint );
		Paint.DrawText( new Rect( Inset + 392f, Inset, Width - Inset * 2f - 392f, 30f ), "Enter runs the first match  ·  Esc closes", TextFlag.LeftCenter );

		_items.Clear();
		_first = null;

		var filter = (_search.Text ?? "").Trim().ToLowerInvariant();
		var seen = new HashSet<EffigyStageTool>();

		var x = Inset;
		var y = Inset + 46f;
		var rowBottom = y;

		foreach ( var group in _groups )
		{
			var tools = group.Tools.Where( t => t is not null && seen.Add( t )
				&& (filter.Length == 0 || t.Label.ToLowerInvariant().Contains( filter )) ).ToList();

			if ( tools.Count == 0 )
				continue;

			var height = 18f + tools.Count * ItemHeight;

			if ( x + ColumnWidth > Width - Inset && x > Inset )
			{
				x = Inset;
				y = rowBottom + 14f;
			}

			Paint.SetDefaultFont( 8.5f, 600 );
			Paint.SetPen( EffigyModelChrome.Faint );
			Paint.DrawText( new Rect( x + 6f, y, ColumnWidth, 16f ), group.Name.ToUpperInvariant(), TextFlag.LeftCenter );

			var iy = y + 18f;

			foreach ( var tool in tools )
			{
				var rect = new Rect( x, iy, ColumnWidth - 12f, ItemHeight );
				var index = _items.Count;
				_items.Add( (rect, tool) );

				var hit = filter.Length > 0 && _first is null && tool.Enabled;
				if ( hit )
					_first = tool;

				if ( hit || _hovered == index )
				{
					Paint.ClearPen();
					Paint.SetBrush( EffigyModelChrome.Control );
					Paint.DrawRect( rect, 4f );
				}

				Paint.SetDefaultFont( 11f, 500 );
				Paint.SetPen( !tool.Enabled ? EffigyModelChrome.Faint.WithAlpha( 0.7f ) : hit || _hovered == index ? EffigyModelChrome.Text : EffigyModelChrome.Dim );
				Paint.DrawText( rect.Shrink( 6f, 0f, 0f, 0f ), tool.Label, TextFlag.LeftCenter );

				if ( tool.Checked )
					Paint.DrawIcon( new Rect( rect.Right - 22f, rect.Top, 18f, rect.Height ), "check", 13, TextFlag.Center );
				else
					EffigyModelChrome.PaintKey( rect.Shrink( 0f, 0f, 6f, 5f ), tool.Key, EffigyModelChrome.Faint );

				iy += ItemHeight;
			}

			rowBottom = MathF.Max( rowBottom, y + height );
			x += ColumnWidth;
		}

		if ( _items.Count == 0 )
		{
			Paint.SetDefaultFont( 11f, 400 );
			Paint.SetPen( EffigyModelChrome.Faint );
			Paint.DrawText( new Rect( Inset, Inset + 50f, Width, 20f ), $"Nothing called \"{_search.Text}\" in this mode.", TextFlag.LeftCenter );
		}
	}

	private int ItemAt( Vector2 p )
	{
		for ( var i = 0; i < _items.Count; i++ )
			if ( _items[i].Rect.IsInside( p ) )
				return i;
		return -1;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		var index = ItemAt( e.LocalPosition );
		if ( index == _hovered )
			return;

		_hovered = index;
		ToolTip = index >= 0 ? (_items[index].Tool.Enabled ? _items[index].Tool.FaceTip : _items[index].Tool.DisabledReason) ?? "" : "";
		Update();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		e.Accepted = true;

		if ( !e.LeftMouseButton )
			return;

		var index = ItemAt( e.LocalPosition );

		if ( index < 0 )
		{
			// Empty ground: the catalog is in the way, so it goes.
			if ( e.LocalPosition.y > Inset + 40f )
				Close();
			return;
		}

		var tool = _items[index].Tool;
		if ( !tool.Enabled )
			return;

		Close();

		if ( tool.Checkable )
			tool.Checked = !tool.Checked;

		tool.Run();
	}
}
