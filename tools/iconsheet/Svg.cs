// The Paint recorder. Every call EffigyIcons makes lands here as an SVG element instead of on a
// widget, so the glyph that gets drawn is the glyph the editor draws - same code, same numbers,
// different back end.
//
// WHAT THIS CANNOT TELL YOU, stated so the sheet is not over-trusted. It reproduces geometry
// exactly, because geometry is all EffigyIcons computes. It does NOT reproduce s&box's
// rasteriser: pen cap and join style are the notable guess (Qt's QPen defaults to a square cap
// and a bevel join; round is used here because it is what icon strokes usually are), and at a
// 1.6px stroke the difference is sub-pixel. Judge whether a glyph READS as its operation, and
// whether it sits right in its button. Do not judge hairline antialiasing from this.

using System;
using System.Globalization;
using System.Text;
using Sandbox;

namespace Editor;

public static class Paint
{
	/// <summary>Set by EffigyIcons and ignored - SVG is antialiased regardless.</summary>
	public static bool Antialiasing { get; set; }

	private static Color? _pen;
	private static float _penWidth;
	private static Color? _brush;

	private static readonly StringBuilder Elements = new();

	// --- the drawn extent ----------------------------------------------------------------------
	//
	// Tracked because "the glyphs look a bit small in the button" is not a finding anybody can act
	// on and "the median glyph covers 46% of a 54px button, and the spread is 2:1" is. Every point
	// that reaches a draw call is folded in, so this is the INK's own bounds rather than the
	// nominal box the glyph claims to be authored against - which is the whole point, since the
	// first run showed four glyphs well outside it.

	private static float _minX, _minY, _maxX, _maxY;
	private static bool _anyInk;

	private static void Saw( float x, float y )
	{
		if ( !_anyInk )
		{
			_minX = _maxX = x;
			_minY = _maxY = y;
			_anyInk = true;
			return;
		}

		_minX = MathF.Min( _minX, x );
		_maxX = MathF.Max( _maxX, x );
		_minY = MathF.Min( _minY, y );
		_maxY = MathF.Max( _maxY, y );
	}

	/// <summary>Width and height of everything drawn since Begin, in button pixels.</summary>
	public static (float Width, float Height) Extent
		=> _anyInk ? (_maxX - _minX, _maxY - _minY) : (0f, 0f);

	/// <summary>Start recording a fresh glyph. Pen and brush are cleared too, so one icon can
	/// never inherit the last one's state - the editor gets a fresh paint pass per widget and the
	/// sheet has to match, or a glyph that forgets to set its own pen would look fine here and
	/// blank there.</summary>
	public static void Begin()
	{
		Elements.Clear();
		_pen = null;
		_brush = null;
		_penWidth = 1f;
		_anyInk = false;
	}

	public static string End() => Elements.ToString();

	public static void SetPen( Color color, float width )
	{
		_pen = color;
		_penWidth = width;
	}

	public static void ClearPen() => _pen = null;

	public static void SetBrush( Color color ) => _brush = color;

	public static void ClearBrush() => _brush = null;

	public static void DrawLine( Vector2 a, Vector2 b )
	{
		if ( _pen is null ) return;

		Saw( a.x, a.y );
		Saw( b.x, b.y );

		Elements.Append( $"<line x1=\"{N( a.x )}\" y1=\"{N( a.y )}\" x2=\"{N( b.x )}\" y2=\"{N( b.y )}\" {Stroke()} stroke-linecap=\"round\" />\n" );
	}

	public static void DrawPolygon( params Vector2[] points )
	{
		if ( points is null || points.Length < 2 ) return;

		var path = new StringBuilder();

		for ( var i = 0; i < points.Length; i++ )
		{
			Saw( points[i].x, points[i].y );
			path.Append( i == 0 ? $"M {N( points[i].x )} {N( points[i].y )} " : $"L {N( points[i].x )} {N( points[i].y )} " );
		}

		path.Append( 'Z' );

		Elements.Append( $"<path d=\"{path}\" {Fill()} {Stroke()} stroke-linejoin=\"round\" />\n" );
	}

	public static void DrawRect( Rect rect, float cornerRadius = 0f )
	{
		Saw( rect.x, rect.y );
		Saw( rect.x + rect.width, rect.y + rect.height );

		var radius = cornerRadius > 0 ? $" rx=\"{N( cornerRadius )}\"" : "";

		Elements.Append( $"<rect x=\"{N( rect.x )}\" y=\"{N( rect.y )}\" width=\"{N( rect.width )}\" height=\"{N( rect.height )}\"{radius} {Fill()} {Stroke()} />\n" );
	}

	private static string Fill()
		=> _brush is Color brush ? $"fill=\"{Hex( brush )}\" fill-opacity=\"{N( brush.a )}\"" : "fill=\"none\"";

	private static string Stroke()
		=> _pen is Color pen
			? $"stroke=\"{Hex( pen )}\" stroke-opacity=\"{N( pen.a )}\" stroke-width=\"{N( _penWidth )}\""
			: "stroke=\"none\"";

	private static string Hex( Color c )
		=> $"#{Channel( c.r )}{Channel( c.g )}{Channel( c.b )}";

	private static string Channel( float v )
		=> ((int)MathF.Round( Math.Clamp( v, 0f, 1f ) * 255f )).ToString( "x2", CultureInfo.InvariantCulture );

	private static string N( float v ) => MathF.Round( v, 3 ).ToString( CultureInfo.InvariantCulture );
}
