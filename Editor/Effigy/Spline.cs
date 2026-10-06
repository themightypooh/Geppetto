using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A smooth curve through control points, in 3D, and the two things the modeller wants from one:
/// a tube along it, and a mesh bent along it.
///
/// CATMULL-ROM, NOT BÉZIER. The curve passes THROUGH every point you click, so drawing one is
/// "click where the cable goes" rather than "click, then find the handle that pulls the curve
/// toward where you meant". Centripetal parameterisation, which is the variant that neither
/// loops back on itself nor cusps when two points are close — the one every modelling package
/// that offers a through-point curve actually uses.
///
/// A STRAIGHT SPLINE IS THE SAME OBJECT WITH SMOOTH OFF: the control points joined by lines.
/// Pipes with elbows, a horn that kinks, a path that has to hit exact corners.
/// </summary>
public sealed class Spline3
{
	public List<Vec3> Points = new();
	public bool Closed;
	public bool Smooth = true;

	/// <summary>
	/// The curve as a polyline: <paramref name="perSegment"/> points between each pair of control
	/// points when smooth, the control points themselves otherwise. A closed spline returns no
	/// repeat of its first point — the caller that needs the loop closed joins last to first,
	/// which is how Skinner's wrap and every path consumer here already read a loop.
	/// </summary>
	public List<Vec3> Sample( int perSegment = 8 )
	{
		var result = new List<Vec3>();
		var n = Points.Count;

		if ( n == 0 )
			return result;

		if ( n == 1 || !Smooth || (n == 2 && !Closed) )
		{
			result.AddRange( Points );
			return result;
		}

		perSegment = Math.Max( 1, perSegment );
		var segments = Closed ? n : n - 1;

		for ( var i = 0; i < segments; i++ )
		{
			var p0 = At( i - 1 );
			var p1 = At( i );
			var p2 = At( i + 1 );
			var p3 = At( i + 2 );

			for ( var k = 0; k < perSegment; k++ )
				result.Add( Centripetal( p0, p1, p2, p3, k / (float)perSegment ) );
		}

		if ( !Closed )
			result.Add( Points[^1] );

		return result;
	}

	/// <summary>The control point at <paramref name="i"/>, wrapping when closed and clamping when
	/// open — the open ends reuse their neighbour, which is the standard way to give a
	/// Catmull-Rom a first and last segment.</summary>
	Vec3 At( int i )
	{
		var n = Points.Count;

		if ( Closed )
			return Points[((i % n) + n) % n];

		return Points[Math.Clamp( i, 0, n - 1 )];
	}

	/// <summary>
	/// Centripetal Catmull-Rom between p1 and p2, with p0 and p3 as the neighbours. The
	/// parameter spacing is the square root of the chord length — that is the "centripetal" —
	/// which is what keeps a tight bend from overshooting into a loop.
	/// </summary>
	static Vec3 Centripetal( Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, float t )
	{
		const float alpha = 0.5f;

		float Knot( Vec3 a, Vec3 b, float prev )
		{
			var d = (b - a).Length;
			return prev + MathF.Max( MathF.Pow( d, alpha ), 1e-5f );
		}

		var t0 = 0f;
		var t1 = Knot( p0, p1, t0 );
		var t2 = Knot( p1, p2, t1 );
		var t3 = Knot( p2, p3, t2 );

		var u = t1 + (t2 - t1) * t;

		var a1 = Blend( p0, p1, t0, t1, u );
		var a2 = Blend( p1, p2, t1, t2, u );
		var a3 = Blend( p2, p3, t2, t3, u );
		var b1 = Blend( a1, a2, t0, t2, u );
		var b2 = Blend( a2, a3, t1, t3, u );

		return Blend( b1, b2, t1, t2, u );
	}

	static Vec3 Blend( Vec3 a, Vec3 b, float ta, float tb, float t )
	{
		var span = tb - ta;

		if ( MathF.Abs( span ) < 1e-8f )
			return a;

		return a * ((tb - t) / span) + b * ((t - ta) / span);
	}

	/// <summary>Total length of the sampled polyline, including the closing edge when closed.</summary>
	public static float Length( IReadOnlyList<Vec3> path, bool closed )
	{
		var total = 0f;

		for ( var i = 0; i + 1 < path.Count; i++ )
			total += (path[i + 1] - path[i]).Length;

		if ( closed && path.Count > 1 )
			total += (path[0] - path[^1]).Length;

		return total;
	}

	/// <summary>
	/// A frame at every station — the tangent and two perpendiculars — carried along the path by
	/// the smallest rotation at each step, so nothing spins where the tangent happens to pass a
	/// world axis. Sweep does the same and says why (SweepFeature's header). A closed path gets
	/// the leftover twist between its last frame and its first spread evenly back along it, so
	/// the loop meets itself.
	/// </summary>
	public static List<(Vec3 T, Vec3 U, Vec3 V)> Frames( IReadOnlyList<Vec3> path, bool closed )
	{
		var frames = new List<(Vec3, Vec3, Vec3)>( path.Count );
		var n = path.Count;

		if ( n == 0 )
			return frames;

		if ( n == 1 )
		{
			frames.Add( (new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 )) );
			return frames;
		}

		Vec3 TangentAt( int i )
		{
			Vec3 t;

			if ( closed )
				t = path[(i + 1) % n] - path[((i - 1) % n + n) % n];
			else if ( i == 0 )
				t = path[1] - path[0];
			else if ( i == n - 1 )
				t = path[n - 1] - path[n - 2];
			else
				t = path[i + 1] - path[i - 1];

			return t.LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : t.Normal;
		}

		var tangent = TangentAt( 0 );

		// Any perpendicular will do for the first frame; the least surprising is the one closest
		// to world up, so a level cable's profile starts upright.
		var up = MathF.Abs( tangent.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var u = Vec3.Cross( up, tangent ).Normal;
		var v = Vec3.Cross( tangent, u );

		frames.Add( (tangent, u, v) );

		for ( var i = 1; i < n; i++ )
		{
			var next = TangentAt( i );
			Rotate( ref u, ref v, tangent, next );
			tangent = next;
			frames.Add( (tangent, u, v) );
		}

		if ( closed && n > 2 )
		{
			// Carry the last frame one more step, onto the first tangent, and measure how far it
			// has turned from where the first frame started. That angle is the loop's mismatch.
			var lu = frames[^1].Item2;
			var lv = frames[^1].Item3;
			Rotate( ref lu, ref lv, frames[^1].Item1, frames[0].Item1 );

			var u0 = frames[0].Item2;
			var v0 = frames[0].Item3;
			var mismatch = MathF.Atan2( Vec3.Dot( lu, v0 ), Vec3.Dot( lu, u0 ) );

			if ( MathF.Abs( mismatch ) > 1e-5f )
			{
				for ( var i = 1; i < n; i++ )
				{
					var angle = -mismatch * i / n;
					var (t, fu, fv) = frames[i];
					var cos = MathF.Cos( angle );
					var sin = MathF.Sin( angle );
					frames[i] = (t, fu * cos + fv * sin, fv * cos - fu * sin);
				}
			}
		}

		return frames;
	}

	/// <summary>Turn two axes by the smallest rotation carrying <paramref name="from"/> onto
	/// <paramref name="to"/>. Rodrigues, with the degenerate cases taken out first.</summary>
	static void Rotate( ref Vec3 u, ref Vec3 v, Vec3 from, Vec3 to )
	{
		var axis = Vec3.Cross( from, to );
		var sin = axis.Length;
		var cos = Vec3.Dot( from, to );

		if ( sin < 1e-9f )
		{
			if ( cos > 0f )
				return;

			u = -u;
			v = -v;
			return;
		}

		axis = axis / sin;
		u = Turn( u, axis, cos, sin );
		v = Turn( v, axis, cos, sin );
	}

	static Vec3 Turn( Vec3 x, Vec3 axis, float cos, float sin ) =>
		x * cos + Vec3.Cross( axis, x ) * sin + axis * (Vec3.Dot( axis, x ) * (1f - cos));
}

/// <summary>A tube along a path: one ring per station, skinned. Cables, pipes, horns, tails.</summary>
public static class SplineTube
{
	/// <param name="radius">Radius at the start.</param>
	/// <param name="endRadius">Radius at the end; equal to <paramref name="radius"/> for a plain
	/// tube, smaller for a horn or a tail, zero for a point.</param>
	/// <param name="sides">Points round each ring. 3 is the least that encloses anything.</param>
	/// <param name="twistDegrees">How far the profile turns over the whole length. Ignored on a
	/// closed path, which has to meet itself.</param>
	public static PolyMesh Build( IReadOnlyList<Vec3> path, bool closed, float radius, float endRadius,
		int sides, float twistDegrees, int material, bool capEnds = true )
	{
		if ( path is null || path.Count < 2 )
			throw new ArgumentException( "A tube needs a path of at least two points.", nameof( path ) );

		sides = Math.Max( 3, sides );

		var frames = Spline3.Frames( path, closed );
		var rings = new List<List<Vec3>>( path.Count );
		var n = path.Count;

		// Radius and twist are spread by arc length rather than by station, so a taper stays
		// straight where the stations bunch up on a tight bend.
		var total = Spline3.Length( path, closed );
		var along = 0f;

		for ( var i = 0; i < n; i++ )
		{
			if ( i > 0 )
				along += (path[i] - path[i - 1]).Length;

			var f = total > 1e-8f ? along / total : 0f;
			var r = closed ? radius : radius + (endRadius - radius) * f;
			var twist = closed ? 0f : twistDegrees * MathF.PI / 180f * f;

			var (_, u, v) = frames[i];
			var ring = new List<Vec3>( sides );

			for ( var k = 0; k < sides; k++ )
			{
				var a = twist + k * MathF.PI * 2f / sides;
				ring.Add( path[i] + u * (MathF.Cos( a ) * r) + v * (MathF.Sin( a ) * r) );
			}

			rings.Add( ring );
		}

		return Skinner.Skin( rings, capEnds && !closed, material, wrap: closed );
	}
}

/// <summary>
/// Bend a mesh along a path.
///
/// The mesh's own X axis becomes distance along the curve; Y and Z become offsets in the curve's
/// frame at that distance. So a straight tail modelled along X, dropped on a curved path, becomes
/// a curved tail — the way a curve modifier works in every modeller, and the reason it wants a
/// straight source: the path supplies the shape, the mesh supplies the cross-section along it.
/// </summary>
public static class CurveDeform
{
	/// <param name="axis">Which of the mesh's axes runs along the curve: 0 X, 1 Y, 2 Z.</param>
	/// <param name="stretch">Fit the mesh's whole length to the whole path. Off, one unit of mesh
	/// is one unit of path, and a mesh longer than the path carries straight on past its end.</param>
	public static void Apply( PolyMesh mesh, IReadOnlyList<Vec3> path, bool closed, int axis = 0, bool stretch = true )
	{
		if ( mesh is null || mesh.Positions.Count == 0 || path is null || path.Count < 2 )
			return;

		axis = Math.Clamp( axis, 0, 2 );

		var min = float.MaxValue;
		var max = float.MinValue;
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			var a = Along( p, axis );
			min = MathF.Min( min, a );
			max = MathF.Max( max, a );
			lo = new Vec3( MathF.Min( lo.x, p.x ), MathF.Min( lo.y, p.y ), MathF.Min( lo.z, p.z ) );
			hi = new Vec3( MathF.Max( hi.x, p.x ), MathF.Max( hi.y, p.y ), MathF.Max( hi.z, p.z ) );
		}

		// The cross-section rides the curve about the mesh's OWN centre line, not the world's: a
		// tail modelled at z = 30 is a tail, not a tail thirty units off its path.
		var centre = (lo + hi) * 0.5f;

		var extent = MathF.Max( max - min, 1e-6f );
		var total = Spline3.Length( path, closed );
		var frames = Spline3.Frames( path, closed );

		// Cumulative distance to each station, plus the closing edge so a closed path wraps.
		var n = path.Count;
		var cum = new float[n + 1];

		for ( var i = 1; i < n; i++ )
			cum[i] = cum[i - 1] + (path[i] - path[i - 1]).Length;

		cum[n] = closed ? cum[n - 1] + (path[0] - path[n - 1]).Length : cum[n - 1];

		var scale = stretch ? total / extent : 1f;

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var s = (Along( p, axis ) - min) * scale;
			var (a, b) = Across( p - centre, axis );

			if ( closed && total > 1e-8f )
				s = ((s % total) + total) % total;

			mesh.Positions[i] = Place( path, frames, cum, closed, s, a, b );
		}

	}

	static float Along( Vec3 p, int axis ) => axis switch { 0 => p.x, 1 => p.y, _ => p.z };

	/// <summary>The two offsets across the curve, in the order that keeps the mesh right-handed.</summary>
	static (float, float) Across( Vec3 p, int axis ) => axis switch
	{
		0 => (p.y, p.z),
		1 => (p.z, p.x),
		_ => (p.x, p.y),
	};

	static Vec3 Place( IReadOnlyList<Vec3> path, List<(Vec3 T, Vec3 U, Vec3 V)> frames, float[] cum, bool closed, float s, float a, float b )
	{
		var n = path.Count;

		// Before the start or past the end of an open path: carry straight on along the end tangent.
		if ( !closed && s <= 0f )
		{
			var (t, u, v) = frames[0];
			return path[0] + t * s + u * a + v * b;
		}

		if ( !closed && s >= cum[n - 1] )
		{
			var (t, u, v) = frames[n - 1];
			return path[n - 1] + t * (s - cum[n - 1]) + u * a + v * b;
		}

		// Find the segment holding s.
		var seg = 0;
		while ( seg + 1 < n + (closed ? 1 : 0) && cum[seg + 1] < s )
			seg++;

		var i0 = seg % n;
		var i1 = (seg + 1) % n;
		var span = cum[seg + 1] - cum[seg];
		var f = span > 1e-8f ? (s - cum[seg]) / span : 0f;

		var pos = Vec3.Lerp( path[i0], path[i1], f );
		var u0 = frames[i0].U; var v0 = frames[i0].V;
		var u1 = frames[i1].U; var v1 = frames[i1].V;

		// Lerping the frame axes and renormalising is fine between neighbouring stations, whose
		// frames differ by a small rotation; it would not be across a large one.
		var uu = Vec3.Lerp( u0, u1, f );
		var vv = Vec3.Lerp( v0, v1, f );

		if ( uu.LengthSquared > 1e-12f ) uu = uu.Normal;
		if ( vv.LengthSquared > 1e-12f ) vv = vv.Normal;

		return pos + uu * a + vv * b;
	}
}
