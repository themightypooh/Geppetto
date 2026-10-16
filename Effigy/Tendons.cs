using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Tissue stretched across a gap that tore open faster than it could let go: sinew between the
/// lips of a mouth wrenched open, strands across a wound. A tendon is a bundle of two or three
/// fibres twisting half a turn about each other along the span, each flared to about 2.2× where it
/// roots into the surface and thinned to half in the middle; the bundle sags by an eighth of its
/// span. A snapped one hangs from its root and tapers to a point.
/// </summary>
public static class Tendons
{
	/// <summary>
	/// Appends a tendon from <paramref name="a"/> to <paramref name="b"/> (z is down for the sag)
	/// to a vertex and face list. <paramref name="reach"/> under 1 makes it snapped: it runs that
	/// fraction of the way, drops under its weight and ends in a point.
	/// </summary>
	public static void Append( List<Vec3> positions, List<Face> faces, Vec3 a, Vec3 b, float radius, float reach, Random rng, int sides = 7, int steps = 16 )
	{
		var span = (b - a).Length;
		if ( span < 1e-5f || radius <= 0f ) return;
		var axis = (b - a) / span;
		Basis( axis, out var ux, out var uy );
		var fibres = rng.Next( 2, 4 );
		var phase = (float)rng.NextDouble() * MathF.PI * 2f;
		for ( var k = 0; k < fibres; k++ )
		{
			var path = new List<Vec3>();
			var radii = new List<float>();
			var ang0 = phase + MathF.PI * 2f * k / fibres;
			var fr = radius * (fibres == 2 ? 0.75f : 0.65f) * (0.85f + 0.3f * (float)rng.NextDouble());
			for ( var i = 0; i <= steps; i++ )
			{
				var t = reach * i / steps;
				var sag = -span * 0.125f * 4f * t * (1f - t);
				var hang = reach < 1f ? -span * 0.35f * (t / reach) * (t / reach) : 0f;
				var ang = ang0 + MathF.PI * t;
				// Fibres stay bundled (a round cord, not a flat ribbon): at most ~0.45 radii apart.
				var spread = radius * (0.25f * MathF.Sin( MathF.PI * Math.Clamp( t, 0f, 1f ) ) + 0.18f);
				path.Add( a + (b - a) * t + new Vec3( 0, 0, sag + hang ) + (ux * MathF.Cos( ang ) + uy * MathF.Sin( ang )) * spread );
				var root = 1f + 1.2f * (Step( 0.18f, 0f, t ) + (reach >= 1f ? Step( 0.82f, 1f, t ) : 0f));
				var waist = 1f - 0.5f * MathF.Sin( MathF.PI * Math.Clamp( t, 0f, 1f ) );
				var tip = reach < 1f ? Step( reach, reach * 0.6f, t ) * 0.9f + 0.1f : 1f;
				radii.Add( fr * root * waist * tip );
			}
			Tube( positions, faces, path, radii, sides );
		}
	}

	/// <summary>An open tube with a radius per path point, appended to a vertex and face list.</summary>
	public static void Tube( List<Vec3> positions, List<Face> faces, IList<Vec3> path, IList<float> radii, int sides = 7 )
	{
		var start = positions.Count;
		var n = path.Count;
		var prevX = Vec3.Zero;
		for ( var i = 0; i < n; i++ )
		{
			var t = path[Math.Min( i + 1, n - 1 )] - path[Math.Max( i - 1, 0 )];
			t = t.LengthSquared > 1e-12f ? t.Normal : new Vec3( 1, 0, 0 );
			Basis( t, out var x, out var y );
			if ( i > 0 && Vec3.Dot( x, prevX ) < 0f ) { x = -x; y = -y; }
			prevX = x;
			for ( var k = 0; k < sides; k++ )
			{
				var ang = MathF.PI * 2f * k / sides;
				positions.Add( path[i] + (x * MathF.Cos( ang ) + y * MathF.Sin( ang )) * radii[i] );
			}
		}
		for ( var i = 0; i + 1 < n; i++ )
			for ( var k = 0; k < sides; k++ )
			{
				var k2 = (k + 1) % sides;
				faces.Add( new Face( new[] { start + i * sides + k, start + i * sides + k2, start + (i + 1) * sides + k2, start + (i + 1) * sides + k } ) );
			}
	}

	static void Basis( Vec3 d, out Vec3 x, out Vec3 y )
	{
		var helper = MathF.Abs( d.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		x = Vec3.Cross( d, helper ).Normal;
		y = Vec3.Cross( d, x ).Normal;
	}

	static float Step( float a, float b, float x )
	{
		var t = Math.Clamp( (x - a) / (b - a), 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}
