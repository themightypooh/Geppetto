using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// A cross-section measured: where a plane cuts a mesh, chained into outlines, with each
/// outline's width, depth, perimeter and area.
///
/// THE NUMBER ANATOMY IS WRITTEN IN. A reference says a thigh is 18 in round at the crotch and
/// a waist 11 in across; a render only says "looks thin". A slice answers in the same terms, so
/// a body can be held to the measurements instead of to an impression. <see cref="MeshSection"/>
/// finds the chords; this joins them into loops and measures them.
///
/// SEVERAL LOOPS PER SLICE, kept apart. A cut at knee height through a whole figure meets two
/// legs, and the useful answer is two circumferences, not their sum. A loop inside another is a
/// hole (a hollow tube, a sleeve over an arm) and is taken off the outer loop's area.
/// </summary>
public static class SectionReport
{
	public sealed class Loop
	{
		public List<Vec3> Points = new();
		public bool Closed;
		public float Perimeter;

		/// <summary>Enclosed area; zero for an open chain (the mesh has a hole there).</summary>
		public float Area;

		/// <summary>Extent along the slice's <see cref="Slice.U"/> and <see cref="Slice.V"/>.</summary>
		public float Width, Depth;

		public Vec3 Centre;

		/// <summary>Inside another loop of the same slice: a hole, not a second limb.</summary>
		public bool IsHole;
	}

	public sealed class Slice
	{
		public string Label = "";
		public Vec3 Origin, Normal, U, V;
		public List<Loop> Loops = new();

		public bool Hit => Loops.Count > 0;

		/// <summary>Outer loops' areas less their holes.</summary>
		public float Area => Loops.Sum( l => l.IsHole ? -l.Area : l.Area );
		public float Perimeter => Loops.Sum( l => l.Perimeter );
		public float Width, Depth;

		public string Describe()
		{
			if ( !Hit )
				return $"{Label}: misses";

			var sb = new StringBuilder();
			var outer = Loops.Count( l => !l.IsHole );
			sb.Append( Invariant( $"{Label}: {outer} outline{(outer == 1 ? "" : "s")}" ) );
			if ( Loops.Count > outer ) sb.Append( Invariant( $" + {Loops.Count - outer} hole{(Loops.Count - outer == 1 ? "" : "s")}" ) );
			sb.Append( Invariant( $", width {N( Width )} ({AxisName( U )}), depth {N( Depth )} ({AxisName( V )}), perimeter {N( Perimeter )}, area {N( Area )}" ) );

			if ( Loops.Count > 1 || Loops.Any( l => !l.Closed ) )
			{
				for ( var i = 0; i < Loops.Count; i++ )
				{
					var l = Loops[i];
					sb.Append( Invariant( $"\n  {(l.IsHole ? "hole" : "outline")} {i + 1}: width {N( l.Width )}, depth {N( l.Depth )}, perimeter {N( l.Perimeter )}, " ) );
					sb.Append( l.Closed ? Invariant( $"area {N( l.Area )}" ) : "OPEN (the mesh has a hole or a loose edge here; no area)" );
					sb.Append( Invariant( $", centre {P( l.Centre )}" ) );
				}
			}
			else
			{
				sb.Append( Invariant( $", centre {P( Loops[0].Centre )}" ) );
			}

			return sb.ToString();
		}
	}

	/// <summary>
	/// Cut <paramref name="meshes"/> with the plane through <paramref name="origin"/> facing
	/// <paramref name="normal"/>. <paramref name="uHint"/> says which way "width" runs; it is
	/// projected into the plane, and when it lies along the normal the first of world y, z, x that
	/// does not is used instead.
	/// </summary>
	public static Slice Cut( IEnumerable<PolyMesh> meshes, Vec3 origin, Vec3 normal, Vec3? uHint = null, string label = null )
	{
		var n = normal.Normal;
		// Width and depth read as positive axes: x rather than -x.
		var u = Positive( InPlane( n, uHint ) );
		var v = Positive( Vec3.Cross( n, u ).Normal );
		var slice = new Slice { Label = label ?? "", Origin = origin, Normal = n, U = u, V = v };

		float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;

		foreach ( var mesh in meshes )
		{
			if ( mesh is null || mesh.FaceCount == 0 )
				continue;

			var weld = MathF.Max( mesh.BoundsDiagonal * 1e-5f, 1e-6f );
			foreach ( var chain in Chain( MeshSection.CrossSection( mesh, OffVertices( mesh, origin, n ), n, 1e-7f ), weld ) )
			{
				var loop = Measure( chain.Points, chain.Closed, origin, u, v );
				slice.Loops.Add( loop );

				foreach ( var p in loop.Points )
				{
					var a = Vec3.Dot( p - origin, u );
					var b = Vec3.Dot( p - origin, v );
					minU = MathF.Min( minU, a ); maxU = MathF.Max( maxU, a );
					minV = MathF.Min( minV, b ); maxV = MathF.Max( maxV, b );
				}
			}
		}

		if ( slice.Hit )
		{
			slice.Width = maxU - minU;
			slice.Depth = maxV - minV;
			MarkHoles( slice );
			slice.Loops.Sort( ( a, b ) => b.Area.CompareTo( a.Area ) );
		}

		return slice;
	}

	public static Slice Cut( PolyMesh mesh, Vec3 origin, Vec3 normal, Vec3? uHint = null, string label = null ) =>
		Cut( new[] { mesh }, origin, normal, uHint, label );

	/// <summary>
	/// The plane's origin, moved a hair along the normal when a vertex lies on the plane.
	///
	/// A plane exactly through vertices is the common case, not the rare one — a cut at a box's
	/// floor, or through a cylinder's axis where its seam vertices sit. <see cref="MeshSection"/> is
	/// built for footprints and skips an edge lying in the plane between faces on either side of it,
	/// so the outline would come back with its sides missing. A shift of a hundred-thousandth of the
	/// model's size changes no measurement anyone reads, and puts every vertex clearly on one side.
	/// </summary>
	static Vec3 OffVertices( PolyMesh mesh, Vec3 origin, Vec3 n )
	{
		var step = MathF.Max( mesh.BoundsDiagonal * 1e-5f, 1e-5f );

		// A cut exactly at a box's top must step down into it, not up off it: prefer a shift that
		// leaves vertices on both sides.
		Vec3? firstClear = null;

		foreach ( var k in new[] { 0f, 1f, -1f, 2.3f, -3.7f, 5.9f } )
		{
			var o = origin + n * (step * k);
			bool clear = true, above = false, below = false;

			foreach ( var p in mesh.Positions )
			{
				var d = Vec3.Dot( p - o, n );
				if ( MathF.Abs( d ) <= step * 0.1f ) { clear = false; break; }
				if ( d > 0f ) above = true; else below = true;
			}

			if ( !clear ) continue;
			if ( above && below ) return o;
			firstClear ??= o;
		}

		return firstClear ?? origin + n * (step * 0.5f);
	}

	/// <summary>The loop of <paramref name="slice"/> whose outline contains <paramref name="point"/>
	/// (projected into the plane), smallest first; null when none does.</summary>
	public static Loop Containing( Slice slice, Vec3 point )
	{
		var (px, py) = (Vec3.Dot( point - slice.Origin, slice.U ), Vec3.Dot( point - slice.Origin, slice.V ));
		Loop best = null;

		foreach ( var loop in slice.Loops )
		{
			if ( !loop.Closed || loop.IsHole )
				continue;

			if ( Inside( loop, slice.Origin, slice.U, slice.V, px, py ) && (best is null || loop.Area < best.Area) )
				best = loop;
		}

		return best;
	}

	/// <summary>A unit vector in the plane: the hint projected in, or the first world axis of y,
	/// z, x that is not too close to the normal.</summary>
	public static Vec3 InPlane( Vec3 normal, Vec3? hint )
	{
		var n = normal.Normal;

		foreach ( var candidate in (hint is Vec3 h ? new[] { h } : Array.Empty<Vec3>()).Concat( new[] { new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 ) } ) )
		{
			var p = candidate - n * Vec3.Dot( candidate, n );
			if ( p.Length > 0.5f * MathF.Max( candidate.Length, 1e-6f ) )
				return p.Normal;
		}

		// Unreachable: three orthogonal axes cannot all lie within 60 degrees of one normal.
		return new Vec3( 0, 1, 0 );
	}

	static Vec3 Positive( Vec3 d )
	{
		var lead = MathF.Abs( d.x ) >= MathF.Abs( d.y ) && MathF.Abs( d.x ) >= MathF.Abs( d.z ) ? d.x : MathF.Abs( d.y ) >= MathF.Abs( d.z ) ? d.y : d.z;
		return lead < 0f ? -d : d;
	}

	/// <summary>"y", "-z", or the vector itself when it is not near an axis.</summary>
	public static string AxisName( Vec3 d )
	{
		var axes = new[] { (new Vec3( 1, 0, 0 ), "x"), (new Vec3( 0, 1, 0 ), "y"), (new Vec3( 0, 0, 1 ), "z") };
		foreach ( var (axis, name) in axes )
		{
			var dot = Vec3.Dot( d.Normal, axis );
			if ( dot > 0.98f ) return name;
			if ( dot < -0.98f ) return "-" + name;
		}

		return Invariant( $"{d.x:0.##},{d.y:0.##},{d.z:0.##}" );
	}

	// --- chaining ------------------------------------------------------------------------------

	/// <summary>Join chords end to end. A chord's ends are shared with its neighbours' (both
	/// computed from the same mesh edge), so welding at a small tolerance finds the joins.</summary>
	internal static List<(List<Vec3> Points, bool Closed)> Chain( List<(Vec3 A, Vec3 B)> segments, float weld )
	{
		var chains = new List<(List<Vec3>, bool)>();
		if ( segments.Count == 0 )
			return chains;

		var ids = new Dictionary<(long, long, long), int>();
		var points = new List<Vec3>();
		int Id( Vec3 p )
		{
			var key = ((long)MathF.Round( p.x / weld ), (long)MathF.Round( p.y / weld ), (long)MathF.Round( p.z / weld ));
			if ( ids.TryGetValue( key, out var id ) ) return id;
			ids[key] = points.Count;
			points.Add( p );
			return points.Count - 1;
		}

		var ends = new (int A, int B)[segments.Count];
		var at = new Dictionary<int, List<int>>();
		for ( var i = 0; i < segments.Count; i++ )
		{
			var a = Id( segments[i].A );
			var b = Id( segments[i].B );
			ends[i] = (a, b);
			if ( a == b ) continue;
			if ( !at.TryGetValue( a, out var la ) ) at[a] = la = new List<int>();
			if ( !at.TryGetValue( b, out var lb ) ) at[b] = lb = new List<int>();
			la.Add( i );
			lb.Add( i );
		}

		var used = new bool[segments.Count];

		int Next( int point )
		{
			if ( !at.TryGetValue( point, out var list ) ) return -1;
			foreach ( var s in list )
				if ( !used[s] ) return s;
			return -1;
		}

		for ( var start = 0; start < segments.Count; start++ )
		{
			if ( used[start] || ends[start].A == ends[start].B )
				continue;

			used[start] = true;
			var chain = new List<int> { ends[start].A, ends[start].B };

			// Forward from the far end, then backward from the near one, so a chain that starts
			// mid-way along an open outline still collects all of it.
			for ( var s = Next( chain[^1] ); s >= 0; s = Next( chain[^1] ) )
			{
				used[s] = true;
				chain.Add( ends[s].A == chain[^1] ? ends[s].B : ends[s].A );
				if ( chain[^1] == chain[0] ) break;
			}

			var closed = chain.Count > 3 && chain[^1] == chain[0];

			if ( !closed )
			{
				for ( var s = Next( chain[0] ); s >= 0; s = Next( chain[0] ) )
				{
					used[s] = true;
					chain.Insert( 0, ends[s].A == chain[0] ? ends[s].B : ends[s].A );
				}
			}

			if ( closed ) chain.RemoveAt( chain.Count - 1 );
			chains.Add( (chain.Select( i => points[i] ).ToList(), closed) );
		}

		return chains;
	}

	static Loop Measure( List<Vec3> points, bool closed, Vec3 origin, Vec3 u, Vec3 v )
	{
		var loop = new Loop { Points = points, Closed = closed };
		var count = points.Count;
		float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
		var area2 = 0f;
		var centre = Vec3.Zero;

		for ( var i = 0; i < count; i++ )
		{
			var p = points[i];
			centre += p;
			var a = Vec3.Dot( p - origin, u );
			var b = Vec3.Dot( p - origin, v );
			minU = MathF.Min( minU, a ); maxU = MathF.Max( maxU, a );
			minV = MathF.Min( minV, b ); maxV = MathF.Max( maxV, b );

			if ( i + 1 < count || closed )
			{
				var q = points[(i + 1) % count];
				loop.Perimeter += (q - p).Length;
				area2 += a * Vec3.Dot( q - origin, v ) - Vec3.Dot( q - origin, u ) * b;
			}
		}

		loop.Area = closed ? MathF.Abs( area2 ) * 0.5f : 0f;
		loop.Width = maxU - minU;
		loop.Depth = maxV - minV;
		loop.Centre = count > 0 ? centre / count : origin;
		return loop;
	}

	static void MarkHoles( Slice slice )
	{
		foreach ( var loop in slice.Loops )
		{
			if ( !loop.Closed || loop.Points.Count == 0 )
				continue;

			var p = loop.Points[0];
			var (px, py) = (Vec3.Dot( p - slice.Origin, slice.U ), Vec3.Dot( p - slice.Origin, slice.V ));
			var depth = slice.Loops.Count( other => !ReferenceEquals( other, loop ) && other.Closed && other.Area > loop.Area
				&& Inside( other, slice.Origin, slice.U, slice.V, px, py ) );
			loop.IsHole = depth % 2 == 1;
		}
	}

	static bool Inside( Loop loop, Vec3 origin, Vec3 u, Vec3 v, float px, float py )
	{
		var inside = false;
		var pts = loop.Points;

		for ( int i = 0, j = pts.Count - 1; i < pts.Count; j = i++ )
		{
			var (xi, yi) = (Vec3.Dot( pts[i] - origin, u ), Vec3.Dot( pts[i] - origin, v ));
			var (xj, yj) = (Vec3.Dot( pts[j] - origin, u ), Vec3.Dot( pts[j] - origin, v ));

			if ( (yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi )
				inside = !inside;
		}

		return inside;
	}

	internal static string N( float v ) => v.ToString( MathF.Abs( v ) >= 100f ? "0.#" : "0.###", CultureInfo.InvariantCulture );
	internal static string P( Vec3 p ) => $"({N( p.x )}, {N( p.y )}, {N( p.z )})";
	static string Invariant( FormattableString s ) => s.ToString( CultureInfo.InvariantCulture );
}
