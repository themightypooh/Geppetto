using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// How cloth sits on a body that is not shaped like cloth: over its hollows, not into them.
///
/// A GARMENT IS NOT AN OFFSET OF THE SKIN. Lifting every point of the skin out by the same amount
/// keeps every hollow the skin has - the groove of the spine, the dip under the chest, the waist,
/// the small of the back - and a shirt made that way reads as paint. Real cloth rests on a few
/// high points (shoulders, chest, shoulder blades), spans the hollows between them, and below the
/// chest falls more or less straight under its own weight. Pattern makers draft for exactly that:
/// a T-shirt is a tube sized by the chest, not by the waist.
///
/// SO THE BODY IS SLICED along an axis (down the torso, down an arm), each slice is replaced by
/// its convex outline - which is what a taut cloth across it does - and from the place the cloth
/// starts to hang, every slice is made at least as big as the one above it: the straight fall.
/// Garment points are then moved OUT ONLY, onto that outline plus the ease. A point already
/// further out (a puff, a flare) is left where it is, so this composes with everything after it.
/// </summary>
public static class GarmentHang
{
	public sealed class Slices
	{
		public Vec3 Origin, Axis, U, V;
		public float Step;

		/// <summary>One convex outline per slice, in (U, V) coordinates, top first.</summary>
		public List<Vec2[]> Hulls = new();

		public int Count => Hulls.Count;

		Vec2[] Centres;

		internal void Seal() => Centres = Hulls.Select( Centre ).ToArray();

		/// <summary>Where along the axis a point is, in slices (fractional).</summary>
		public float SliceOf( Vec3 p ) => Vec3.Dot( p - Origin, Axis ) / Step;

		/// <summary>
		/// <paramref name="p"/> moved straight out from the slice centre onto the outline plus
		/// <paramref name="ease"/>, if it is inside that; otherwise unchanged. Between two slices
		/// the outlines are blended, so the surface is smooth along the axis too.
		/// </summary>
		public Vec3 OutTo( Vec3 p, float ease )
		{
			if ( Count == 0 )
				return p;

			var along = Vec3.Dot( p - Origin, Axis );
			var q = new Vec2( Vec3.Dot( p - Origin, U ), Vec3.Dot( p - Origin, V ) );
			var f = Math.Clamp( along / Step, 0f, Count - 1 );
			var k = Math.Min( (int)f, Count - 1 );
			var k1 = Math.Min( k + 1, Count - 1 );
			var w = f - k;

			var c = Centres[k] * (1f - w) + Centres[k1] * w;
			var d = q - c;

			if ( d.LengthSquared < 1e-10f )
				return p;

			var dir = d / d.Length;
			var reach = Reach( Hulls[k], Centres[k], dir ) * (1f - w) + Reach( Hulls[k1], Centres[k1], dir ) * w;
			var want = reach + ease;

			if ( d.Length >= want )
				return p;

			var r = c + dir * want;
			return Origin + Axis * along + U * r.x + V * r.y;
		}
	}

	/// <summary>
	/// Slice <paramref name="points"/> along <paramref name="axis"/> from <paramref name="origin"/>
	/// every <paramref name="step"/>. From <paramref name="fallFrom"/> to <paramref name="fallTo"/>
	/// (distances along the axis) each outline also contains the one before it: the cloth hangs
	/// over that stretch. Past <paramref name="fallTo"/> each slice is its own again.
	/// </summary>
	public static Slices Build( IEnumerable<Vec3> points, Vec3 origin, Vec3 axis, float step, float fallFrom = float.MaxValue,
		float fallTo = float.MaxValue )
	{
		axis = axis.Normal;
		var reference = MathF.Abs( axis.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var u = Vec3.Cross( reference, axis ).Normal;
		var v = Vec3.Cross( axis, u );

		var s = new Slices { Origin = origin, Axis = axis, U = u, V = v, Step = step };
		var bins = new SortedDictionary<int, List<Vec2>>();

		foreach ( var p in points )
		{
			var along = Vec3.Dot( p - origin, axis );

			if ( along < -step * 0.5f )
				continue;

			var k = (int)MathF.Round( along / step );

			if ( !bins.TryGetValue( k, out var list ) )
				bins[k] = list = new List<Vec2>();

			list.Add( new Vec2( Vec3.Dot( p - origin, u ), Vec3.Dot( p - origin, v ) ) );
		}

		if ( bins.Count == 0 )
			return s;

		var last = bins.Keys.Max();
		Vec2[] previous = null;
		var widest = 0f;

		for ( var k = 0; k <= last; k++ )
		{
			var pts = bins.TryGetValue( k, out var list ) && list.Count >= 3 ? list : null;

			// A slice with too little of the body in it (a thin band between two rings of
			// vertices) borrows its neighbour's outline rather than collapsing to a point.
			var hull = pts is null ? previous : Hull( pts );

			// THE FALL KEEPS THE CLOTH'S ROUND, NOT ITS SHAPE. A straight-cut shirt has a fixed
			// width of fabric round it: hanging from a deep chest over wide hips it changes shape,
			// it does not grow. Taking the union of every outline above did grow it - a deep chest
			// and wide hips make a hull bigger round than either, and a size M T-shirt's hem came
			// out 44 inches round on a body of 40, where the real one is 40.6. So each slice keeps
			// its own outline, scaled up until it is at least as far round as the widest above it.
			if ( hull is not null && k * step >= fallFrom && k * step <= fallTo )
			{
				var round = Perimeter( hull );

				if ( round < widest && round > 1e-4f )
				{
					var c = Centre( hull );
					var grow = widest / round;
					hull = hull.Select( p => c + (p - c) * grow ).ToArray();
				}

				widest = MathF.Max( widest, Perimeter( hull ) );
			}
			else if ( hull is not null && k * step < fallFrom )
				widest = 0f;

			s.Hulls.Add( hull ?? Array.Empty<Vec2>() );

			if ( hull is not null )
				previous = hull;
		}

		// Leading empties (nothing at the very top) take the first real outline.
		var first = s.Hulls.FirstOrDefault( h => h.Length >= 3 );

		if ( first is null )
		{
			s.Hulls.Clear();
			return s;
		}

		for ( var i = 0; i < s.Hulls.Count && s.Hulls[i].Length < 3; i++ )
			s.Hulls[i] = first;

		s.Seal();

		return s;
	}

	/// <summary>
	/// Hang the chosen vertices of a garment on the slices, and carry the movement a few rings
	/// into the vertices next to them so there is no step where hung cloth meets the rest.
	/// </summary>
	/// <param name="weight">How much of the move each vertex takes, 0..1. Null is all of it.</param>
	/// <param name="blendInto">Which of the other vertices may take a share of the move. Null is
	/// all of them. A part hung on its own (a sleeve) must not take the body's: the hang only ever
	/// moves cloth out, so whatever it is given it keeps.</param>
	public static void Hang( PolyMesh garment, IReadOnlyList<int> vertices, Slices slices, float ease, int blendRings = 6,
		Func<int, float> weight = null, Func<int, bool> blendInto = null )
	{
		if ( slices.Count == 0 || vertices.Count == 0 )
			return;

		var moved = new Vec3[garment.VertexCount];
		var fixedHere = new bool[garment.VertexCount];

		foreach ( var vi in vertices )
		{
			moved[vi] = (slices.OutTo( garment.Positions[vi], ease ) - garment.Positions[vi]) * (weight?.Invoke( vi ) ?? 1f);
			fixedHere[vi] = true;
		}

		// The neighbours share in it, fading: each ring takes the average of what is next to it.
		var neighbours = new List<int>[garment.VertexCount];

		for ( var i = 0; i < neighbours.Length; i++ )
			neighbours[i] = new List<int>();

		foreach ( var face in garment.Faces )
			for ( var c = 0; c < face.Count; c++ )
			{
				var a = face.Indices[c];
				var b = face.Indices[(c + 1) % face.Count];
				neighbours[a].Add( b );
				neighbours[b].Add( a );
			}

		for ( var ring = 0; ring < blendRings; ring++ )
		{
			var next = (Vec3[])moved.Clone();

			for ( var i = 0; i < moved.Length; i++ )
			{
				if ( fixedHere[i] || neighbours[i].Count == 0 || (blendInto is not null && !blendInto( i )) )
					continue;

				var sum = Vec3.Zero;

				foreach ( var n in neighbours[i] )
					sum += moved[n];

				next[i] = sum / neighbours[i].Count;
			}

			moved = next;
		}

		for ( var i = 0; i < moved.Length; i++ )
			garment.Positions[i] += moved[i];
	}

	static float Perimeter( Vec2[] hull )
	{
		var len = 0f;

		for ( var i = 0; i < hull.Length; i++ )
			len += (hull[(i + 1) % hull.Length] - hull[i]).Length;

		return len;
	}

	static Vec2 Centre( Vec2[] hull )
	{
		if ( hull.Length == 0 )
			return default;

		var c = new Vec2( 0, 0 );

		foreach ( var p in hull )
			c += p;

		return c / hull.Length;
	}

	/// <summary>How far from <paramref name="c"/> along <paramref name="dir"/> the outline is.</summary>
	static float Reach( Vec2[] hull, Vec2 c, Vec2 dir )
	{
		var best = 0f;

		for ( var i = 0; i < hull.Length; i++ )
		{
			var a = hull[i] - c;
			var b = hull[(i + 1) % hull.Length] - c;
			var e = b - a;
			var den = Vec2.Cross( dir, e );

			if ( MathF.Abs( den ) < 1e-9f )
				continue;

			// c + dir t = a + e s
			var t = Vec2.Cross( a, e ) / den;
			var s = Vec2.Cross( a, dir ) / den;

			if ( s >= -1e-4f && s <= 1f + 1e-4f && t > best )
				best = t;
		}

		return best;
	}

	/// <summary>Andrew's monotone chain.</summary>
	static Vec2[] Hull( List<Vec2> points )
	{
		var pts = points.Distinct().OrderBy( p => p.x ).ThenBy( p => p.y ).ToList();

		if ( pts.Count < 3 )
			return pts.ToArray();

		var hull = new List<Vec2>();

		for ( var pass = 0; pass < 2; pass++ )
		{
			var start = hull.Count;

			foreach ( var p in pass == 0 ? pts : Enumerable.Reverse( pts ) )
			{
				while ( hull.Count >= start + 2 && Vec2.Cross( hull[^1] - hull[^2], p - hull[^2] ) <= 0f )
					hull.RemoveAt( hull.Count - 1 );

				hull.Add( p );
			}

			hull.RemoveAt( hull.Count - 1 );
		}

		return hull.ToArray();
	}
}
