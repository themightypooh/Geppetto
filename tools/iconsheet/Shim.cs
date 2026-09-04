// Stand-ins for the four engine types EffigyIcons.cs touches, and nothing else.
//
// EffigyIcons is the one editor file whose entire dependency on s&box is eight Paint calls and
// three value types - Vector2, Color and Rect - so it can be compiled and RUN outside the engine
// as long as those exist. That is what makes a contact sheet possible at all.
//
// These are deliberately minimal. They are not an s&box emulation layer and must not grow into
// one: the moment a glyph needs an engine facility that is not here, the honest answer is to look
// at it in the editor rather than to widen the shim until the sheet lies about what will be drawn.

using System;
using System.Collections.Generic;

namespace Sandbox;

public struct Vector2
{
	public float x;
	public float y;

	public Vector2( float x, float y ) { this.x = x; this.y = y; }

	public static readonly Vector2 Zero = new( 0, 0 );

	public float Length => MathF.Sqrt( x * x + y * y );

	/// <summary>Unit vector, and zero stays zero rather than becoming NaN - ArrowHead normalises a
	/// direction it is handed and a degenerate one should draw nothing, not poison the sheet.</summary>
	public Vector2 Normal
	{
		get
		{
			var length = Length;
			return length <= 0.0001f ? Zero : new Vector2( x / length, y / length );
		}
	}

	public static Vector2 operator +( Vector2 a, Vector2 b ) => new( a.x + b.x, a.y + b.y );
	public static Vector2 operator -( Vector2 a, Vector2 b ) => new( a.x - b.x, a.y - b.y );
	public static Vector2 operator -( Vector2 a ) => new( -a.x, -a.y );
	public static Vector2 operator *( Vector2 a, float f ) => new( a.x * f, a.y * f );
	public static Vector2 operator *( float f, Vector2 a ) => new( a.x * f, a.y * f );
	public static Vector2 operator /( Vector2 a, float f ) => new( a.x / f, a.y / f );
}

public struct Color
{
	public float r, g, b, a;

	public Color( float r, float g, float b, float a = 1f ) { this.r = r; this.g = g; this.b = b; this.a = a; }

	public Color WithAlpha( float alpha ) => new( r, g, b, alpha );
}

public struct Rect
{
	public float x, y, width, height;

	public Rect( float x, float y, float width, float height )
	{
		this.x = x; this.y = y; this.width = width; this.height = height;
	}
}
