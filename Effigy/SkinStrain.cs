using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>How skin answers a deformation: see <see cref="SkinStrain.Respond"/>. Lengths in model units.</summary>
public sealed class SkinStrainOptions
{
	/// <summary>Crest spacing of compression folds (0 = no folds). 0.18–0.22 in on a forehead, 0.3–0.5 in on neck or fat.</summary>
	public float FoldWavelength;
	/// <summary>Folds where the smallest principal stretch drops under this.</summary>
	public float FoldBelow = 0.78f;
	/// <summary>Amplitude cap; 0.06–0.07 in on a face.</summary>
	public float FoldMax = 0.07f;
	/// <summary>Squeezed regions smaller than this many vertices get no folds (specks read as scratches).</summary>
	public int FoldMinVerts = 120;
	/// <summary>Where folds may go at all, by vertex; null = anywhere.</summary>
	public Func<int, bool> FoldAllowed;
	/// <summary>Where striae may go at all, by vertex; null = anywhere.</summary>
	public Func<int, bool> StriaeAllowed;
	/// <summary>Striae spacing (0 = none); 0.12–0.25 in.</summary>
	public float StriaeSpacing;
	/// <summary>Striae where the largest principal stretch passes this.</summary>
	public float StriaeAbove = 1.3f;
	/// <summary>Groove half-width and depth: a flat trough about 0.06 in wide and 0.004–0.012 in deep.</summary>
	public float StriaeWidth = 0.03f, StriaeDepth = 0.012f;
	/// <summary>Across the stretch like real stretch marks (false), or along it for drag streaks (true).</summary>
	public bool StriaeAlong;
	/// <summary>Model units per inch, so the inch-tuned noise frequencies scale with the model.</summary>
	public float Scale = 1f;
	public int Seed = 1;
}

/// <summary>A fold swept along a curve: a narrow groove, a rounded lip on each side, the
/// <see cref="Side"/> one overhanging. The path is in the mesh's space. Sizes in model units.</summary>
public sealed record SweptFold( IReadOnlyList<Vec3> Path, float Depth, float Width, float Overhang = 0f, int Side = 1, float Pinch = 0.25f );

/// <summary>
/// Strain-driven skin detail, after the recipes in the organic-modeling skill's tutorials.md
/// (Polywink's compression-shape wrinkle map, the Blender Studio tension-map wrinkles, Abney's
/// Dam Standard + Inflate + Pinch folds):
///
/// - principal strain per vertex from sign-free tensors of edge growth, smoothed so the
///   directions are coherent (averaging raw edge directions cancels opposite edges and flickers,
///   which is where faceted "crumpled" folds come from);
/// - a phase field integrated along the compression direction, whose isolines are the swept fold
///   crests, evened out so crests keep their spacing where the field twists;
/// - folds with a wide round roll and a narrow creased valley (about 5:1), amplitude from the lost
///   length a = W/π·√(1/λ − 1), broken into staggered segments, fading at the region's edge;
/// - striae across the stretch past a threshold: narrow flat grooves with a raised shoulder,
///   broken along their length, plus a pale tone so they read by colour as well as shape;
/// - explicit folds swept along curves (<see cref="SweptFolds"/>).
///
/// Self-contained on purpose (its own noise and neighbours): it is mirrored into the editor.
/// </summary>
public static class SkinStrain
{
	/// <summary>
	/// Adds folds and striae to <paramref name="positions"/> in place, from how
	/// <paramref name="deformed"/> stretched relative to <paramref name="rest"/> (same topology).
	/// Returns a tone per vertex: positive pale (striae), negative shadowed (fold valleys).
	/// </summary>
	public static float[] Respond( IList<Face> faces, IList<Vec3> rest, IList<Vec3> deformed, Vec3[] positions, SkinStrainOptions o )
	{
		var n = positions.Length;
		var nb = MeshLoops.Neighbours( n, faces );
		var (stretch, sdir, squeeze, cdir) = StrainField( faces, rest, deformed, nb, 6, out var anisotropy );
		var normals = VertexNormals( positions, faces );
		var noise = new HashNoise( o.Seed + 17 );
		var s = o.Scale;
		var delta = new float[n];
		var tone = new float[n];

		if ( o.FoldWavelength > 0f )
		{
			var amount = new float[n];
			for ( var v = 0; v < n; v++ )
				amount[v] = Smoothstep( o.FoldBelow + 0.04f, o.FoldBelow - 0.12f, squeeze[v] );
			// Not where the skin has already folded over itself (normals disagree): displacing
			// along them turns a crumple into shards. Nor where the caller says folds don't belong.
			for ( var v = 0; v < n; v++ )
			{
				if ( amount[v] <= 0f ) continue;
				if ( o.FoldAllowed is not null && !o.FoldAllowed( v ) ) { amount[v] = 0f; continue; }
				var c = 0f;
				foreach ( var j in nb[v] ) c += Vec3.Dot( normals[v], normals[j] );
				c = nb[v].Length > 0 ? c / nb[v].Length : 1f;
				amount[v] *= Smoothstep( 0.82f, 0.96f, c );
				// Squeezed from all round there is no fold direction (skin just thickens): fade out.
				amount[v] *= Smoothstep( 0.6f, 0.78f, anisotropy[v] );
			}
			amount = Islands( amount, nb, o.FoldMinVerts );
			// Erode twice, then feather: a strip too narrow to hold a wavelength gets no folds.
			for ( var it = 0; it < 2; it++ )
			{
				var eroded = (float[])amount.Clone();
				for ( var v = 0; v < n; v++ )
					foreach ( var j in nb[v] ) eroded[v] = MathF.Min( eroded[v], amount[j] );
				amount = eroded;
			}
			for ( var it = 0; it < 4; it++ ) amount = Diffuse( amount, nb, 1 );
			var mask = new bool[n];
			var dirs = new Vec3[n];
			for ( var v = 0; v < n; v++ )
			{
				mask[v] = amount[v] > 0.02f && cdir[v].LengthSquared > 0f && normals[v].LengthSquared > 0f;
				if ( !mask[v] ) continue;
				var t = cdir[v] - normals[v] * Vec3.Dot( cdir[v], normals[v] );
				dirs[v] = t.LengthSquared > 1e-8f ? t.Normal : Vec3.Zero;
			}
			var phi = Phase( positions, dirs, mask, nb );
			EvenPhase( phi, positions, mask, nb );
			var w = o.FoldWavelength;
			// A crease is only as sharp as the mesh under it: the valley (where the profile is under
			// half height) is kept at least 2.2 edges wide, or it aliases into jagged facets. With
			// enough resolution the profile reaches the sculpted 5:1 roll-to-valley shape (power 0.35).
			var h = MeanEdge( positions, faces );
			var target = MathF.Min( 2.2f * h / w, 0.9f );
			var power = Math.Clamp( MathF.Log( 0.5f ) / MathF.Log( MathF.Max( MathF.Sin( MathF.PI * target / 2f ), 1e-3f ) ), 0.35f, 1.6f );
			var mean = MeanCrest( power );
			for ( var v = 0; v < n; v++ )
			{
				if ( !mask[v] ) continue;
				var l = MathF.Max( squeeze[v], 0.35f );
				var a = MathF.Min( o.FoldWavelength / MathF.PI * MathF.Sqrt( 1f / l - 1f ), o.FoldMax );
				var p = positions[v] / s;
				var x = phi[v] / w + 0.18f * noise.Sample( p * 1.3f );
				var crest = MathF.Pow( MathF.Abs( MathF.Sin( MathF.PI * x ) ), power );
				var along = Smoothstep( -0.45f, 0.25f, noise.Sample( p * 1.4f + new Vec3( 3.1f, 0, 0 ) ) );
				var k = amount[v] * along;
				// Zero mean over a wavelength, normalised so the valley bottom is exactly −a.
				delta[v] += k * a * (crest - mean) / mean;
				tone[v] -= k * 0.35f * Smoothstep( 0.6f, 0.2f, crest );
			}
			// Nothing finer than the mesh can carry: two passes of smoothing on the fold height.
			for ( var it = 0; it < 2; it++ )
			{
				var next = (float[])delta.Clone();
				for ( var v = 0; v < n; v++ )
				{
					if ( !mask[v] || nb[v].Length == 0 ) continue;
					var sum = 0f;
					foreach ( var j in nb[v] ) sum += delta[j];
					next[v] = 0.5f * delta[v] + 0.5f * sum / nb[v].Length;
				}
				delta = next;
			}
		}

		if ( o.StriaeSpacing > 0f )
		{
			var mask = new bool[n];
			var amount = new float[n];
			var dirs = new Vec3[n];
			for ( var v = 0; v < n; v++ )
			{
				amount[v] = o.StriaeAllowed is not null && !o.StriaeAllowed( v ) ? 0f : Smoothstep( o.StriaeAbove, o.StriaeAbove + 0.45f, stretch[v] );
				mask[v] = amount[v] > 0f && sdir[v].LengthSquared > 0f && normals[v].LengthSquared > 0f;
				if ( !mask[v] ) continue;
				var t = sdir[v] - normals[v] * Vec3.Dot( sdir[v], normals[v] );
				if ( o.StriaeAlong ) t = Vec3.Cross( normals[v], t );
				dirs[v] = t.LengthSquared > 1e-8f ? t.Normal : Vec3.Zero;
			}
			var phi = Phase( positions, dirs, mask, nb );
			EvenPhase( phi, positions, mask, nb );
			var w = o.StriaeSpacing;
			var half = o.StriaeWidth / o.StriaeSpacing;
			for ( var v = 0; v < n; v++ )
			{
				if ( !mask[v] ) continue;
				var p = positions[v] / s;
				var x = phi[v] / w + 0.3f * (noise.Sample( p * 1.7f ) + 0.5f * noise.Sample( p * 3.4f ));
				var dist = MathF.Abs( x - MathF.Round( x ) ) / half;
				if ( dist >= 3f ) continue;
				var broken = Smoothstep( -0.15f, 0.2f, noise.Sample( p * 1.6f + new Vec3( 0, 7.7f, 0 ) ) );
				var depth = o.StriaeDepth * amount[v] * broken;
				// Weiss's compact wrinkle: −d at the centre, zero at the edge, a raised shoulder past it.
				delta[v] += depth * (dist - 3f) * (dist - 3f) * (dist - 1f) / 9f;
				tone[v] += amount[v] * broken * Smoothstep( 1.6f, 0.4f, dist );
			}
		}

		for ( var v = 0; v < n; v++ )
			if ( delta[v] != 0f ) positions[v] += normals[v] * delta[v];
		return tone;
	}

	/// <summary>
	/// Lays folds along curves into <paramref name="positions"/>: every vertex within three widths
	/// of a fold's path gets its profile by signed distance across the path (groove, overhanging
	/// lip on the fold's side, a small lip on the other) and a pinch toward the centreline; depth
	/// and width taper to nothing over the last quarter at each end.
	/// </summary>
	public static void SweptFolds( IList<Face> faces, Vec3[] positions, IEnumerable<SweptFold> folds )
	{
		var normals = VertexNormals( positions, faces );
		var delta = new Vec3[positions.Length];
		foreach ( var fold in folds )
		{
			if ( fold.Path.Count < 2 ) continue;
			var w = fold.Width;
			var path = CatmullRom( fold.Path, MathF.Max( w * 0.25f, 1e-3f ) );
			var reach = 3f * w;
			var cum = new float[path.Count];
			for ( var i = 1; i < path.Count; i++ ) cum[i] = cum[i - 1] + (path[i] - path[i - 1]).Length;
			var total = MathF.Max( cum[^1], 1e-5f );
			var lo = path.Aggregate( ( a, b ) => new Vec3( MathF.Min( a.x, b.x ), MathF.Min( a.y, b.y ), MathF.Min( a.z, b.z ) ) ) - new Vec3( reach, reach, reach );
			var hi = path.Aggregate( ( a, b ) => new Vec3( MathF.Max( a.x, b.x ), MathF.Max( a.y, b.y ), MathF.Max( a.z, b.z ) ) ) + new Vec3( reach, reach, reach );
			for ( var v = 0; v < positions.Length; v++ )
			{
				var p = positions[v];
				if ( p.x < lo.x || p.y < lo.y || p.z < lo.z || p.x > hi.x || p.y > hi.y || p.z > hi.z || normals[v].LengthSquared == 0f ) continue;
				var best = float.MaxValue; var c = Vec3.Zero; var tan = Vec3.Zero; var at = 0f;
				for ( var i = 0; i + 1 < path.Count; i++ )
				{
					var ab = path[i + 1] - path[i];
					var len2 = ab.LengthSquared;
					var t = len2 > 1e-12f ? Math.Clamp( Vec3.Dot( p - path[i], ab ) / len2, 0f, 1f ) : 0f;
					var q = path[i] + ab * t;
					var d2 = (p - q).LengthSquared;
					if ( d2 < best ) { best = d2; c = q; tan = ab; at = cum[i] + t * MathF.Sqrt( len2 ); }
				}
				if ( best > reach * reach || tan.LengthSquared < 1e-12f ) continue;
				var side = Vec3.Cross( normals[v], tan.Normal );
				if ( side.LengthSquared < 1e-8f ) continue;
				side = side.Normal;
				var l = Vec3.Dot( p - c, side ) * fold.Side;
				var u = at / total;
				var taper = Smoothstep( 0f, 0.25f, u ) * Smoothstep( 1f, 0.75f, u );
				var ww = w * (0.4f + 0.6f * taper);
				var g = l / (0.35f * ww);
				var h = (l - 0.55f * ww) / (0.45f * ww);
				var e = (l + 0.4f * ww) / (0.25f * ww);
				var height = -fold.Depth * taper * MathF.Exp( -g * g )
					+ fold.Overhang * taper * MathF.Exp( -h * h )
					+ 0.3f * fold.Overhang * taper * MathF.Exp( -e * e );
				var pinch = -side * fold.Side * l * fold.Pinch * taper * MathF.Exp( -(l / ww) * (l / ww) );
				delta[v] += normals[v] * height + pinch;
			}
		}
		for ( var v = 0; v < positions.Length; v++ ) positions[v] += delta[v];
	}

	// --- the pieces ------------------------------------------------------------------------------

	/// <summary>
	/// Largest stretch and smallest squeeze ratio per vertex with their directions, from sign-free
	/// tensors Σ w·e·eᵀ over the deformed edge directions (w = how much the edge grew or shrank),
	/// smoothed <paramref name="smooth"/> times over the mesh before the eigenvectors are taken.
	/// </summary>
	public static (float[] Stretch, Vec3[] StretchDir, float[] Squeeze, Vec3[] SqueezeDir) StrainField( IList<Face> faces, IList<Vec3> rest, IList<Vec3> deformed, int[][] nb, int smooth ) =>
		StrainField( faces, rest, deformed, nb, smooth, out _ );

	/// <summary>As above, also giving how one-directional the squeeze is per vertex: the principal
	/// share of the compression tensor's trace (1 = squeezed along one line, 0.5 = from all round).</summary>
	public static (float[] Stretch, Vec3[] StretchDir, float[] Squeeze, Vec3[] SqueezeDir) StrainField( IList<Face> faces, IList<Vec3> rest, IList<Vec3> deformed, int[][] nb, int smooth, out float[] squeezeAnisotropy )
	{
		var n = rest.Count;
		var ts = new float[n * 6];
		var tc = new float[n * 6];
		var maxR = new float[n];
		var minR = Enumerable.Repeat( 1f, n ).ToArray();
		static void Add( float[] t, int v, Vec3 e, float w )
		{
			var o = v * 6;
			t[o] += w * e.x * e.x; t[o + 1] += w * e.y * e.y; t[o + 2] += w * e.z * e.z;
			t[o + 3] += w * e.x * e.y; t[o + 4] += w * e.x * e.z; t[o + 5] += w * e.y * e.z;
		}
		foreach ( var f in faces )
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				int a = f.Indices[i], b = f.Indices[(i + 1) % f.Indices.Length];
				var r = (rest[a] - rest[b]).Length;
				var w = deformed[a] - deformed[b];
				if ( r < 1e-6f || w.LengthSquared < 1e-12f ) continue;
				var ratio = w.Length / r;
				var e = w.Normal;
				if ( ratio > 1f ) { Add( ts, a, e, ratio - 1f ); Add( ts, b, e, ratio - 1f ); }
				else { Add( tc, a, e, 1f - ratio ); Add( tc, b, e, 1f - ratio ); }
				maxR[a] = MathF.Max( maxR[a], ratio ); maxR[b] = MathF.Max( maxR[b], ratio );
				minR[a] = MathF.Min( minR[a], ratio ); minR[b] = MathF.Min( minR[b], ratio );
			}
		for ( var it = 0; it < smooth; it++ )
		{
			ts = Diffuse( ts, nb, 6 ); tc = Diffuse( tc, nb, 6 );
			maxR = Diffuse( maxR, nb, 1 ); minR = Diffuse( minR, nb, 1 );
		}
		var sd = new Vec3[n];
		var cd = new Vec3[n];
		squeezeAnisotropy = new float[n];
		for ( var v = 0; v < n; v++ )
		{
			sd[v] = Principal( ts, v );
			cd[v] = Principal( tc, v );
			var o = v * 6;
			var trace = tc[o] + tc[o + 1] + tc[o + 2];
			if ( trace > 1e-9f )
			{
				var d = cd[v];
				var along = tc[o] * d.x * d.x + tc[o + 1] * d.y * d.y + tc[o + 2] * d.z * d.z
					+ 2f * (tc[o + 3] * d.x * d.y + tc[o + 4] * d.x * d.z + tc[o + 5] * d.y * d.z);
				squeezeAnisotropy[v] = along / trace;
			}
		}
		return (maxR, sd, minR, cd);
	}

	/// <summary>
	/// A scalar whose gradient follows a direction field over the masked vertices: integrated out
	/// from a seed per region (directions flipped to agree with their parent, so a sign-free field
	/// becomes oriented), then relaxed so it is consistent around loops.
	/// </summary>
	public static float[] Phase( IList<Vec3> pos, Vec3[] dir, bool[] mask, int[][] nb, int relax = 40 )
	{
		var n = pos.Count;
		var phi = new float[n];
		var seen = new bool[n];
		var d = (Vec3[])dir.Clone();
		var queue = new Queue<int>();
		for ( var s = 0; s < n; s++ )
		{
			if ( !mask[s] || seen[s] || d[s].LengthSquared < 1e-9f ) continue;
			seen[s] = true;
			queue.Enqueue( s );
			while ( queue.Count > 0 )
			{
				var v = queue.Dequeue();
				foreach ( var j in nb[v] )
				{
					if ( !mask[j] || seen[j] ) continue;
					if ( d[j].LengthSquared < 1e-9f ) d[j] = d[v];
					if ( Vec3.Dot( d[j], d[v] ) < 0f ) d[j] = -d[j];
					phi[j] = phi[v] + Vec3.Dot( pos[j] - pos[v], (d[v] + d[j]).Normal );
					seen[j] = true;
					queue.Enqueue( j );
				}
			}
		}
		for ( var it = 0; it < relax; it++ )
			for ( var v = 0; v < n; v++ )
			{
				if ( !mask[v] ) continue;
				var s = 0f; var c = 0;
				foreach ( var j in nb[v] )
				{
					if ( !mask[j] ) continue;
					var dj = Vec3.Dot( d[j], d[v] ) < 0f ? -d[j] : d[j];
					s += phi[j] + Vec3.Dot( pos[v] - pos[j], (d[v] + dj).Normal );
					c++;
				}
				if ( c > 0 ) phi[v] = s / c;
			}
		return phi;
	}

	/// <summary>The phase divided by its own smoothed local rate, so crests keep their spacing where the field twists.</summary>
	public static void EvenPhase( float[] phi, IList<Vec3> pos, bool[] mask, int[][] nb )
	{
		var n = phi.Length;
		var rate = new float[n];
		for ( var v = 0; v < n; v++ )
		{
			if ( !mask[v] ) { rate[v] = 1f; continue; }
			var s = 0f; var c = 0;
			foreach ( var j in nb[v] )
			{
				if ( !mask[j] ) continue;
				var dl = (pos[j] - pos[v]).Length;
				if ( dl > 1e-5f ) { s += MathF.Abs( phi[j] - phi[v] ) / dl; c++; }
			}
			rate[v] = c > 0 ? s / c : 1f;
		}
		for ( var it = 0; it < 6; it++ ) rate = Diffuse( rate, nb, 1 );
		// Neighbour differences of a unit-rate phase average 2/π of the full rate.
		for ( var v = 0; v < n; v++ )
			if ( mask[v] ) phi[v] /= Math.Clamp( rate[v] * MathF.PI / 2f, 0.6f, 3f );
	}

	static float MeanEdge( IList<Vec3> pos, IList<Face> faces )
	{
		var sum = 0.0; var count = 0;
		foreach ( var f in faces )
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				sum += (pos[f.Indices[i]] - pos[f.Indices[(i + 1) % f.Indices.Length]]).Length;
				count++;
			}
		return count > 0 ? (float)(sum / count) : 0f;
	}

	/// <summary>Mean of |sin(πx)|^p over a period.</summary>
	static float MeanCrest( float p )
	{
		var s = 0f;
		for ( var i = 0; i < 64; i++ ) s += MathF.Pow( MathF.Sin( MathF.PI * (i + 0.5f) / 64f ), p );
		return s / 64f;
	}

	/// <summary>Zeroes connected regions of positive values with fewer than <paramref name="min"/> vertices.</summary>
	public static float[] Islands( float[] a, int[][] nb, int min )
	{
		var o = (float[])a.Clone();
		var seen = new bool[a.Length];
		var stack = new Stack<int>();
		for ( var s = 0; s < a.Length; s++ )
		{
			if ( seen[s] || a[s] <= 0f ) continue;
			var region = new List<int>();
			seen[s] = true; stack.Push( s );
			while ( stack.Count > 0 )
			{
				var v = stack.Pop(); region.Add( v );
				foreach ( var j in nb[v] )
					if ( !seen[j] && a[j] > 0f ) { seen[j] = true; stack.Push( j ); }
			}
			if ( region.Count < min ) foreach ( var v in region ) o[v] = 0f;
		}
		return o;
	}

	/// <summary>One step of half-and-half neighbour averaging over interleaved values.</summary>
	public static float[] Diffuse( float[] a, int[][] nb, int stride )
	{
		var o = (float[])a.Clone();
		for ( var v = 0; v < nb.Length; v++ )
		{
			if ( nb[v].Length == 0 ) continue;
			for ( var k = 0; k < stride; k++ )
			{
				var s = 0f;
				foreach ( var j in nb[v] ) s += a[j * stride + k];
				o[v * stride + k] = 0.5f * a[v * stride + k] + 0.5f * s / nb[v].Length;
			}
		}
		return o;
	}

	static Vec3 Principal( float[] t, int v )
	{
		var o = v * 6;
		float xx = t[o], yy = t[o + 1], zz = t[o + 2], xy = t[o + 3], xz = t[o + 4], yz = t[o + 5];
		if ( xx + yy + zz < 1e-9f ) return Vec3.Zero;
		var x = xx >= yy && xx >= zz ? new Vec3( 1, 0.1f, 0.1f ) : yy >= zz ? new Vec3( 0.1f, 1, 0.1f ) : new Vec3( 0.1f, 0.1f, 1 );
		for ( var i = 0; i < 16; i++ )
		{
			var y = new Vec3( xx * x.x + xy * x.y + xz * x.z, xy * x.x + yy * x.y + yz * x.z, xz * x.x + yz * x.y + zz * x.z );
			if ( y.LengthSquared < 1e-18f ) return Vec3.Zero;
			x = y.Normal;
		}
		return x;
	}

	/// <summary>Area-weighted unit vertex normals of a face list (zero where undefined).</summary>
	public static Vec3[] VertexNormals( IList<Vec3> pos, IEnumerable<Face> faces )
	{
		var normals = new Vec3[pos.Count];
		foreach ( var f in faces )
		{
			var idx = f.Indices;
			var fn = Vec3.Zero;
			for ( var i = 0; i < idx.Length; i++ )
			{
				var a = pos[idx[i]]; var b = pos[idx[(i + 1) % idx.Length]];
				fn += new Vec3( (a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y) );
			}
			foreach ( var i in idx ) normals[i] += fn;
		}
		for ( var i = 0; i < normals.Length; i++ )
			normals[i] = normals[i].LengthSquared > 1e-12f ? normals[i].Normal : Vec3.Zero;
		return normals;
	}

	/// <summary>A polyline through the points as a Catmull-Rom curve, sampled about every <paramref name="step"/>.</summary>
	public static List<Vec3> CatmullRom( IReadOnlyList<Vec3> pts, float step )
	{
		var outp = new List<Vec3>();
		if ( pts.Count < 2 ) { outp.AddRange( pts ); return outp; }
		for ( var i = 0; i + 1 < pts.Count; i++ )
		{
			var p0 = pts[Math.Max( i - 1, 0 )]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min( i + 2, pts.Count - 1 )];
			var k = Math.Max( 1, (int)MathF.Ceiling( (p2 - p1).Length / step ) );
			for ( var j = 0; j < k; j++ )
			{
				var t = (float)j / k;
				var t2 = t * t; var t3 = t2 * t;
				outp.Add( 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3) );
			}
		}
		outp.Add( pts[^1] );
		return outp;
	}

	public static float Smoothstep( float a, float b, float x )
	{
		var t = Math.Clamp( (x - a) / (b - a), 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	/// <summary>Smooth value noise in [-1, 1], hashed rather than tabled.</summary>
	sealed class HashNoise
	{
		readonly uint _seed;
		public HashNoise( int seed ) { _seed = (uint)seed * 0x9E3779B9u; }

		float Hash( int x, int y, int z )
		{
			var h = _seed ^ (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ (uint)z * 0xCB1AB31Fu;
			h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
			return (h & 0xFFFFFF) / (float)0x7FFFFF - 1f;
		}

		public float Sample( Vec3 p )
		{
			int x0 = (int)MathF.Floor( p.x ), y0 = (int)MathF.Floor( p.y ), z0 = (int)MathF.Floor( p.z );
			float fx = p.x - x0, fy = p.y - y0, fz = p.z - z0;
			static float F( float t ) => t * t * t * (t * (t * 6f - 15f) + 10f);
			float u = F( fx ), w = F( fy ), t = F( fz );
			static float L( float a, float b, float k ) => a + (b - a) * k;
			var x00 = L( Hash( x0, y0, z0 ), Hash( x0 + 1, y0, z0 ), u );
			var x10 = L( Hash( x0, y0 + 1, z0 ), Hash( x0 + 1, y0 + 1, z0 ), u );
			var x01 = L( Hash( x0, y0, z0 + 1 ), Hash( x0 + 1, y0, z0 + 1 ), u );
			var x11 = L( Hash( x0, y0 + 1, z0 + 1 ), Hash( x0 + 1, y0 + 1, z0 + 1 ), u );
			return L( L( x00, x10, w ), L( x01, x11, w ), t );
		}
	}
}
