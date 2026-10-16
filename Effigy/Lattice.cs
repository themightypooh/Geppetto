using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// A free-form deformation cage (Sederberg and Parry 1986, "Free-form deformation of solid geometric
/// models"): a box of U × V × W control points around a shape, and moving a point drags the shape
/// near it with it.
///
/// OFFSETS, NOT POSITIONS. Each control point stores how far it has been moved from its rest place in
/// the even grid, and a vertex moves by the weighted sum of those offsets. The weights sum to one and
/// an untouched cage has every offset zero, so an undeformed lattice is EXACTLY the identity whatever
/// the interpolation — no reliance on a basis reproducing straight lines through rounding.
///
/// Two weightings. Smooth is the classic Bernstein tensor product: every control point reaches the
/// whole cage, falling off smoothly, so a pull reads as a broad sculptural change. Linear is
/// trilinear within the cell a vertex is in: a pull stays local to its neighbouring cells and keeps
/// the creases a cage edit sometimes wants.
/// </summary>
public sealed class LatticeCage
{
	public Vec3 Min, Max;
	public int U, V, W;
	public Vec3[] Offsets;

	public LatticeCage( Vec3 min, Vec3 max, int u, int v, int w )
	{
		if ( u < 2 || v < 2 || w < 2 )
			throw new ArgumentOutOfRangeException( nameof( u ), "a lattice needs at least two points along each side" );

		Min = min;
		Max = max;
		U = u;
		V = v;
		W = w;
		Offsets = new Vec3[u * v * w];
	}

	public int Index( int i, int j, int k ) => i + U * (j + V * k);

	public int Count => Offsets.Length;

	/// <summary>Where control point (i, j, k) sits before it is moved.</summary>
	public Vec3 RestPoint( int i, int j, int k ) => new(
		Lerp( Min.x, Max.x, i / (float)(U - 1) ),
		Lerp( Min.y, Max.y, j / (float)(V - 1) ),
		Lerp( Min.z, Max.z, k / (float)(W - 1) ) );

	/// <summary>Where control point (i, j, k) is now.</summary>
	public Vec3 Point( int i, int j, int k ) => RestPoint( i, j, k ) + Offsets[Index( i, j, k )];

	public bool IsIdentity => Offsets.All( o => o.LengthSquared == 0f );

	/// <summary>Where <paramref name="p"/> goes. Points outside the cage are weighted as at its nearest face.</summary>
	public Vec3 Deform( Vec3 p, bool smooth )
	{
		var s = Param( p.x, Min.x, Max.x );
		var t = Param( p.y, Min.y, Max.y );
		var r = Param( p.z, Min.z, Max.z );

		var wu = smooth ? Bernstein( U - 1, s ) : Hat( U, s );
		var wv = smooth ? Bernstein( V - 1, t ) : Hat( V, t );
		var ww = smooth ? Bernstein( W - 1, r ) : Hat( W, r );

		var shift = Vec3.Zero;

		for ( var k = 0; k < W; k++ )
		{
			if ( ww[k] == 0f ) continue;

			for ( var j = 0; j < V; j++ )
			{
				var wjk = wv[j] * ww[k];
				if ( wjk == 0f ) continue;

				for ( var i = 0; i < U; i++ )
				{
					var w = wu[i] * wjk;
					if ( w == 0f ) continue;
					shift += Offsets[Index( i, j, k )] * w;
				}
			}
		}

		return p + shift;
	}

	/// <summary>Deform every vertex in place. Returns how many moved.</summary>
	public int Apply( PolyMesh mesh, bool smooth )
	{
		var moved = 0;

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var before = mesh.Positions[i];
			var after = Deform( before, smooth );

			if ( (after - before).LengthSquared > 1e-14f )
				moved++;

			mesh.Positions[i] = after;
		}

		return moved;
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;

	static float Param( float x, float lo, float hi ) =>
		hi - lo <= 1e-12f ? 0.5f : Math.Clamp( (x - lo) / (hi - lo), 0f, 1f );

	static float[] Bernstein( int n, float t )
	{
		var w = new float[n + 1];

		for ( var i = 0; i <= n; i++ )
			w[i] = (float)(Binomial( n, i ) * Math.Pow( t, i ) * Math.Pow( 1 - t, n - i ));

		return w;
	}

	static double Binomial( int n, int k )
	{
		double r = 1;
		for ( var i = 1; i <= k; i++ )
			r = r * (n - k + i) / i;
		return r;
	}

	static float[] Hat( int count, float t )
	{
		var w = new float[count];
		var x = t * (count - 1);
		var i0 = Math.Min( (int)MathF.Floor( x ), count - 2 );
		var f = x - i0;
		w[i0] = 1f - f;
		w[i0 + 1] = f;
		return w;
	}

	/// <summary>
	/// Read control-point offsets written as text, the way a script or the dialog stores them:
	///
	///   i,j,k: dx,dy,dz ; i,j,k: dx,dy,dz ; ...
	///
	/// Indices count from 0. Any index can be * (every point along that side) or a range a..b, so
	/// <c>*,*,2: 0,0,1</c> lifts the whole top layer of a 3-high cage. Entries naming the same point add
	/// up. Blank text is an untouched cage.
	/// </summary>
	public bool TryParseOffsets( string text, out string why )
	{
		why = null;
		var offsets = new Vec3[Offsets.Length];

		foreach ( var raw in (text ?? "").Split( new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			var entry = raw.Trim();

			if ( entry.Length == 0 )
				continue;

			var colon = entry.IndexOf( ':' );

			if ( colon < 0 )
			{
				why = $"'{entry}' needs a colon between the point and its offset, e.g. 1,1,2: 0,0,0.5.";
				return false;
			}

			var at = entry[..colon].Split( ',' );

			if ( at.Length != 3 )
			{
				why = $"'{entry[..colon].Trim()}' is not a point i,j,k (three indices; * means all).";
				return false;
			}

			if ( !TryIndices( at[0], U, "first", out var iList, out why )
				|| !TryIndices( at[1], V, "second", out var jList, out why )
				|| !TryIndices( at[2], W, "third", out var kList, out why ) )
			{
				why = $"'{entry}': {why}";
				return false;
			}

			if ( !ModelMeasure.TryParseVec3( entry[(colon + 1)..].Trim(), out var offset ) )
			{
				why = $"'{entry[(colon + 1)..].Trim()}' is not an offset dx,dy,dz.";
				return false;
			}

			foreach ( var k in kList )
				foreach ( var j in jList )
					foreach ( var i in iList )
						offsets[Index( i, j, k )] += offset;
		}

		Offsets = offsets;
		return true;
	}

	/// <summary>The offsets as text TryParseOffsets reads back: one entry per moved point.</summary>
	public string FormatOffsets()
	{
		var parts = new List<string>();

		for ( var k = 0; k < W; k++ )
			for ( var j = 0; j < V; j++ )
				for ( var i = 0; i < U; i++ )
				{
					var o = Offsets[Index( i, j, k )];
					if ( o.LengthSquared == 0f ) continue;
					parts.Add( string.Format( CultureInfo.InvariantCulture, "{0},{1},{2}: {3:0.####},{4:0.####},{5:0.####}", i, j, k, o.x, o.y, o.z ) );
				}

		return string.Join( "; ", parts );
	}

	static bool TryIndices( string text, int count, string which, out List<int> list, out string why )
	{
		list = new List<int>();
		why = null;
		text = text.Trim();

		if ( text == "*" )
		{
			for ( var i = 0; i < count; i++ ) list.Add( i );
			return true;
		}

		var range = text.Split( "..", StringSplitOptions.None );

		if ( range.Length == 2 && int.TryParse( range[0], out var lo ) && int.TryParse( range[1], out var hi ) )
		{
			if ( lo < 0 || hi >= count || hi < lo )
			{
				why = $"the {which} index range {text} is outside 0..{count - 1}.";
				return false;
			}

			for ( var i = lo; i <= hi; i++ ) list.Add( i );
			return true;
		}

		if ( !int.TryParse( text, out var n ) )
		{
			why = $"the {which} index '{text}' is not a whole number, a range a..b or *.";
			return false;
		}

		if ( n < 0 || n >= count )
		{
			why = $"the {which} index {n} is outside 0..{count - 1} (this side has {count} points).";
			return false;
		}

		list.Add( n );
		return true;
	}
}
