using System;

// Stand-ins for the handful of s&box types EffigyViewCube.cs touches, with the same names,
// conventions (+x forward, +y left, +z up) and the members it uses. Nothing more.

public struct Vector2
{
	public float x, y;
	public Vector2( float x, float y ) { this.x = x; this.y = y; }
	public float Length => MathF.Sqrt( x * x + y * y );
	public static Vector2 operator +( Vector2 a, Vector2 b ) => new( a.x + b.x, a.y + b.y );
	public static Vector2 operator -( Vector2 a, Vector2 b ) => new( a.x - b.x, a.y - b.y );
	public static Vector2 operator *( Vector2 a, float s ) => new( a.x * s, a.y * s );
}

public struct Vector3
{
	public float x, y, z;
	public Vector3( float x, float y, float z ) { this.x = x; this.y = y; this.z = z; }
	public float Length => MathF.Sqrt( x * x + y * y + z * z );
	public Vector3 Normal { get { var l = Length; return l > 1e-9f ? this * (1f / l) : this; } }
	public float Distance( Vector3 o ) => (this - o).Length;
	public static float Dot( Vector3 a, Vector3 b ) => a.x * b.x + a.y * b.y + a.z * b.z;
	public static Vector3 Cross( Vector3 a, Vector3 b ) => new( a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x );
	public static Vector3 operator +( Vector3 a, Vector3 b ) => new( a.x + b.x, a.y + b.y, a.z + b.z );
	public static Vector3 operator -( Vector3 a, Vector3 b ) => new( a.x - b.x, a.y - b.y, a.z - b.z );
	public static Vector3 operator -( Vector3 a ) => new( -a.x, -a.y, -a.z );
	public static Vector3 operator *( Vector3 a, float s ) => new( a.x * s, a.y * s, a.z * s );
	public static Vector3 Up => new( 0, 0, 1 );
	public static Vector3 Left => new( 0, 1, 0 );
	public static Vector3 Right => new( 0, -1, 0 );
}

/// <summary>A camera orientation as its three axes, the way s&amp;box's Rotation reports them.</summary>
public struct Rotation
{
	public Vector3 Forward, Left, Up;
	public Vector3 Right => -Left;

	public static Rotation LookAt( Vector3 forward, Vector3 up )
	{
		var f = forward.Normal;
		var l = Vector3.Cross( up, f ).Normal;
		return new Rotation { Forward = f, Left = l, Up = Vector3.Cross( f, l ) };
	}
}

public struct Color
{
	public float r, g, b, a;
	public Color( float r, float g, float b, float a = 1f ) { this.r = r; this.g = g; this.b = b; this.a = a; }
	public Color WithAlpha( float alpha ) => new( r, g, b, alpha );
	public static Color Lerp( Color x, Color y, float t ) => new( x.r + (y.r - x.r) * t, x.g + (y.g - x.g) * t, x.b + (y.b - x.b) * t, x.a + (y.a - x.a) * t );
	public static Color FromBytes( int r, int g, int b, int a = 255 ) => new( r / 255f, g / 255f, b / 255f, a / 255f );
	public static Color White => new( 1, 1, 1 );
	public static Color Black => new( 0, 0, 0 );
}

namespace Sandbox
{
	public struct Rect
	{
		public float Left, Top, Width, Height;
		public Rect( float x, float y, float w, float h ) { Left = x; Top = y; Width = w; Height = h; }
		public Vector2 Center => new( Left + Width * 0.5f, Top + Height * 0.5f );
		public bool IsInside( Vector2 p ) => p.x >= Left && p.y >= Top && p.x <= Left + Width && p.y <= Top + Height;
	}
}
