using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Effigy.Render;

/// <summary>
/// Antialiasing for <see cref="ShadedRender"/>: many visibility samples per pixel, shading once.
///
/// WHY NOT JUST SUPERSAMPLE. Supersampling shades every sample, and the shading (lights, shadow maps,
/// occlusion, reflections) is nearly all the cost: 4×4 is sixteen times the work for an edge that is
/// still only sixteen steps deep, on an ordered grid that gives a near-horizontal edge just four of them.
/// Here the frame is shaded on its own grid (one sample per output pixel at <c>ss=1</c>) and the edges
/// come from a cheap visibility-only pass: <see cref="ShadeScene.Coverage"/> samples per output pixel
/// that record which triangle is in front and how far away it is, nothing else. Each sample then takes
/// its colour from the nearest shaded pixel that saw the same surface - the same triangle, or the same
/// mesh at the same depth - so a silhouette pixel half over the backdrop is half backdrop, at a cost of
/// one shade. This is the decoupling MSAA does in hardware (Akeley 1993), done the way Subpixel
/// Reconstruction Antialiasing does it in software (Chajdas, McGuire, Luebke 2011): geometry at high
/// rate, shading at low rate, joined by a depth- and id-aware lookup.
///
/// THE PATTERN is multi-jittered (Chiu, Shirley, Wang 1994): n×n strata, and also every sample in its
/// own row and column of the n² sub-grid, so an edge at any angle - near-horizontal included - steps
/// through n² coverage levels instead of n. It is the same in every pixel, so the filter weights are a
/// table.
///
/// THE FILTER is Blackman-Harris, 1.5 pixels wide each way (Cycles' default pixel filter), in
/// display-linear light after the tone curve (so a bright rim does not swallow its edge, and the ramp
/// is even rather than gamma-bent). Not Mitchell-Netravali (1988): its negative lobes, harmless on a
/// photo, ring as a dark line round every light silhouette on a flat backdrop, which is this
/// renderer's whole subject. Blackman-Harris has none and is nearly as sharp.
///
/// WHAT IT CANNOT DO: a part thinner than the shading grid's spacing (a hair, a wire) is seen by the
/// coverage samples but was never shaded, so it borrows the nearest shade of the same mesh at that
/// depth, or of anything when there is none. Raise <c>ss</c> for scenes made of strands.
/// </summary>
public static class CoverageResolve
{
	/// <summary>Half-width of <see cref="Filter"/>, in output pixels.</summary>
	public const float FilterRadius = 1.5f;

	/// <summary>Blackman-Harris over |x| &lt; <see cref="FilterRadius"/>; positive everywhere, so no ringing.</summary>
	public static float Filter( float x )
	{
		x = MathF.Abs( x );
		if ( x >= FilterRadius ) return 0f;
		var t = 2f * MathF.PI * (x / (2f * FilterRadius) + 0.5f);
		return 0.35875f - 0.48829f * MathF.Cos( t ) + 0.14128f * MathF.Cos( 2f * t ) - 0.01168f * MathF.Cos( 3f * t );
	}

	/// <summary>
	/// <paramref name="count"/> sample positions in the unit square, multi-jittered: rounded to a square
	/// n×n (n = 1..8), one per stratum and one per row and column of the n²×n² sub-grid. Deterministic.
	/// </summary>
	public static (float X, float Y)[] Pattern( int count )
	{
		var n = Math.Clamp( (int)MathF.Round( MathF.Sqrt( Math.Max( 1, count ) ) ), 1, 8 );
		// Stratum (i, j) is column i, row j. Its sub-column (x within the stratum) must differ down each
		// stratum column and its sub-row across each stratum row: start from the canonical arrangement,
		// sub-column = j and sub-row = i, then shuffle each column's sub-columns and each row's sub-rows.
		var cols = new int[n * n]; var rows = new int[n * n];
		for ( var j = 0; j < n; j++ )
			for ( var i = 0; i < n; i++ )
			{
				cols[j * n + i] = j;
				rows[j * n + i] = i;
			}
		var rng = new Random( 0x5eed + n );
		for ( var i = 0; i < n; i++ )
			for ( var j = n - 1; j > 0; j-- )
			{
				var k = rng.Next( j + 1 );
				(cols[j * n + i], cols[k * n + i]) = (cols[k * n + i], cols[j * n + i]);
			}
		for ( var j = 0; j < n; j++ )
			for ( var i = n - 1; i > 0; i-- )
			{
				var k = rng.Next( i + 1 );
				(rows[j * n + i], rows[j * n + k]) = (rows[j * n + k], rows[j * n + i]);
			}
		var pts = new (float, float)[n * n];
		for ( var j = 0; j < n; j++ )
			for ( var i = 0; i < n; i++ )
			{
				var x = (i + (cols[j * n + i] + 0.5f) / n) / n;
				var y = (j + (rows[j * n + i] + 0.5f) / n) / n;
				pts[j * n + i] = (x, y);
			}
		return pts;
	}

	/// <summary>Everything the resolve reads from the shaded frame.</summary>
	public sealed class Frame
	{
		/// <summary>The shading grid: <see cref="Ss"/> × the output size, in display-linear colour after the curve.</summary>
		public Vec3[] Toned;
		public int W, H, Ss;
		/// <summary>Per shading pixel: 0 backdrop, 1 mesh, 2 floor; the triangle (or -1); view depth.</summary>
		public byte[] Kind;
		public int[] TriId;
		public float[] Depth;
		/// <summary>Per triangle, which scene item (mesh) it came from.</summary>
		public int[] TriItem;
		public List<(int A, int B, int C)> Tris;
		/// <summary>Vertex screen position on the shading grid, and view depth.</summary>
		public float[] Sx, Sy, Sz;
		public float Near;
	}

	/// <summary>Resolve <paramref name="f"/> to <paramref name="width"/>×<paramref name="height"/> display-linear pixels.</summary>
	public static Vec3[] Resolve( Frame f, int width, int height, int samples )
	{
		var pat = Pattern( samples );
		var K = pat.Length;
		const int R = 1; // pixels each way a sample can reach (the filter is 1.5 wide; a sample sits within its pixel)
		var taps = 2 * R + 1;

		// Weight of sample k of the pixel (dx, dy) away, for the pixel being filled.
		var wt = new float[taps * taps * K];
		for ( var dy = -R; dy <= R; dy++ )
			for ( var dx = -R; dx <= R; dx++ )
				for ( var k = 0; k < K; k++ )
					wt[((dy + R) * taps + dx + R) * K + k] = Filter( dx + pat[k].X - 0.5f ) * Filter( dy + pat[k].Y - 0.5f );

		// Vertex positions in output pixels.
		var inv = 1f / f.Ss;
		var vx = new float[f.Sx.Length]; var vy = new float[f.Sy.Length];
		for ( var i = 0; i < vx.Length; i++ ) { vx[i] = f.Sx[i] * inv; vy[i] = f.Sy[i] * inv; }

		const int band = 16;
		var bands = (height + band - 1) / band;
		var bins = new List<int>[bands];
		for ( var b = 0; b < bands; b++ ) bins[b] = new List<int>();
		for ( var t = 0; t < f.Tris.Count; t++ )
		{
			var (a, b2, c) = f.Tris[t];
			if ( f.Sz[a] < f.Near || f.Sz[b2] < f.Near || f.Sz[c] < f.Near ) continue;
			var y0 = MathF.Min( vy[a], MathF.Min( vy[b2], vy[c] ) ); var y1 = MathF.Max( vy[a], MathF.Max( vy[b2], vy[c] ) );
			var x0 = MathF.Min( vx[a], MathF.Min( vx[b2], vx[c] ) ); var x1 = MathF.Max( vx[a], MathF.Max( vx[b2], vx[c] ) );
			if ( y1 < -R || y0 >= height + R || x1 < 0 || x0 >= width ) continue;
			var lo = Math.Max( 0, ((int)MathF.Floor( y0 ) - R) / band ); var hi = Math.Min( bands - 1, ((int)MathF.Floor( y1 ) + R) / band );
			for ( var bi = lo; bi <= hi; bi++ ) bins[bi].Add( t );
		}

		// Each tap's total weight, for a neighbour whose samples all read the same shaded pixel.
		var tapSum = new float[taps * taps];
		for ( var tp = 0; tp < tapSum.Length; tp++ )
			for ( var k = 0; k < K; k++ ) tapSum[tp] += wt[tp * K + k];

		var outPx = new Vec3[width * height];
		Parallel.For( 0, bands, bi =>
		{
			// The band's rows plus the filter's reach above and below.
			var r0 = bi * band; var r1 = Math.Min( height, r0 + band );
			var s0 = Math.Max( 0, r0 - R ); var s1 = Math.Min( height, r1 + R );
			var rows = s1 - s0;
			var n = rows * width * K;
			// Per sample: the triangle in front, and its 1/z (0 = nothing; nearer is larger, no division).
			var tri = new int[n]; var iz = new float[n];
			Array.Fill( tri, -1 );
			var o0 = new float[K]; var o1 = new float[K]; var oz = new float[K];

			// --- visibility at every sample: edge functions are linear, so a sample is three adds ---
			foreach ( var t in bins[bi] )
			{
				var (a, b, c) = f.Tris[t];
				float ax = vx[a], ay = vy[a], bx = vx[b], by = vy[b], cx = vx[c], cy = vy[c];
				var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
				if ( MathF.Abs( area ) < 1e-12f ) continue;
				var ia = 1f / area;
				float A0 = (by - cy) * ia, B0 = (cx - bx) * ia, C0 = (bx * cy - by * cx) * ia;
				float A1 = (cy - ay) * ia, B1 = (ax - cx) * ia, C1 = (cx * ay - cy * ax) * ia;
				float iza = 1f / f.Sz[a], izb = 1f / f.Sz[b], izc = 1f / f.Sz[c];
				float Az = A0 * (iza - izc) + A1 * (izb - izc), Bz = B0 * (iza - izc) + B1 * (izb - izc), Cz = izc + C0 * (iza - izc) + C1 * (izb - izc);
				for ( var k = 0; k < K; k++ )
				{
					o0[k] = A0 * pat[k].X + B0 * pat[k].Y;
					o1[k] = A1 * pat[k].X + B1 * pat[k].Y;
					oz[k] = Az * pat[k].X + Bz * pat[k].Y;
				}
				var minX = Math.Max( 0, (int)MathF.Floor( MathF.Min( ax, MathF.Min( bx, cx ) ) ) );
				var maxX = Math.Min( width - 1, (int)MathF.Floor( MathF.Max( ax, MathF.Max( bx, cx ) ) ) );
				var minY = Math.Max( s0, (int)MathF.Floor( MathF.Min( ay, MathF.Min( by, cy ) ) ) );
				var maxY = Math.Min( s1 - 1, (int)MathF.Floor( MathF.Max( ay, MathF.Max( by, cy ) ) ) );
				for ( var y = minY; y <= maxY; y++ )
					for ( var x = minX; x <= maxX; x++ )
					{
						var e0 = A0 * x + B0 * y + C0; var e1 = A1 * x + B1 * y + C1; var ez = Az * x + Bz * y + Cz;
						var bse = ((y - s0) * width + x) * K;
						for ( var k = 0; k < K; k++ )
						{
							var w0 = e0 + o0[k]; var w1 = e1 + o1[k];
							if ( w0 < 0f || w1 < 0f || w0 + w1 > 1f ) continue;
							var d = ez + oz[k];
							if ( d <= iz[bse + k] ) continue;
							iz[bse + k] = d; tri[bse + k] = t;
						}
					}
			}

			// --- each sample's colour: the shaded pixel nearby that saw the same surface ---
			// (tri[] is reused to hold the shading-grid index the sample reads; uniform[] marks the pixels
			// whose samples all read one, which is nearly all of them.)
			var uniform = new int[rows * width];
			for ( var y = s0; y < s1; y++ )
				for ( var x = 0; x < width; x++ )
				{
					var bse = ((y - s0) * width + x) * K;
					var same = -2;
					for ( var k = 0; k < K; k++ )
					{
						var d = iz[bse + k];
						var src = Source( f, (x + pat[k].X) * f.Ss, (y + pat[k].Y) * f.Ss, tri[bse + k], d > 0f ? 1f / d : float.MaxValue );
						tri[bse + k] = src;
						same = same == -2 ? src : same == src ? same : -1;
					}
					uniform[(y - s0) * width + x] = same;
				}

			// --- filter ---
			for ( var y = r0; y < r1; y++ )
				for ( var x = 0; x < width; x++ )
				{
					var acc = Vec3.Zero; var wsum = 0f;
					for ( var dy = -R; dy <= R; dy++ )
					{
						var yy = y + dy; if ( yy < s0 || yy >= s1 ) continue;
						for ( var dx = -R; dx <= R; dx++ )
						{
							var xx = x + dx; if ( xx < 0 || xx >= width ) continue;
							var tp = (dy + R) * taps + dx + R;
							var u = uniform[(yy - s0) * width + xx];
							if ( u >= 0 )
							{
								acc += f.Toned[u] * tapSum[tp];
								wsum += tapSum[tp];
								continue;
							}
							var bse = ((yy - s0) * width + xx) * K;
							var wb = tp * K;
							for ( var k = 0; k < K; k++ )
							{
								var w = wt[wb + k];
								acc += f.Toned[tri[bse + k]] * w;
								wsum += w;
							}
						}
					}
					outPx[y * width + x] = acc / wsum;
				}
		} );
		return outPx;
	}

	/// <summary>
	/// Which shaded pixel a coverage sample at (<paramref name="X"/>, <paramref name="Y"/>) on the
	/// shading grid takes its colour from. <paramref name="t"/> is the triangle it saw (-1: backdrop or
	/// floor) at view depth <paramref name="z"/>.
	/// </summary>
	static int Source( Frame f, float X, float Y, int t, float z )
	{
		var cx = Math.Clamp( (int)X, 0, f.W - 1 ); var cy = Math.Clamp( (int)Y, 0, f.H - 1 );
		var own = cy * f.W + cx;

		// The common case, nearly every sample: the pixel it sits in saw the same thing.
		if ( t < 0 ? f.Kind[own] != 1 : f.TriId[own] == t && f.Kind[own] == 1 )
			return own;

		var item = t >= 0 ? f.TriItem[t] : -1;
		var best = -1; var bestScore = float.MaxValue;
		for ( var r = 1; r <= 2 && best < 0; r++ )
			for ( var jy = cy - r; jy <= cy + r; jy++ )
			{
				if ( jy < 0 || jy >= f.H ) continue;
				for ( var jx = cx - r; jx <= cx + r; jx++ )
				{
					if ( jx < 0 || jx >= f.W ) continue;
					var j = jy * f.W + jx;
					var ddx = jx + 0.5f - X; var ddy = jy + 0.5f - Y;
					var dist2 = ddx * ddx + ddy * ddy;
					float score;
					if ( t < 0 )
					{
						if ( f.Kind[j] == 1 ) continue;
						score = dist2;
					}
					else
					{
						if ( f.Kind[j] != 1 ) continue;
						var tj = f.TriId[j];
						if ( tj == t ) score = dist2 * 1e-3f; // the same triangle wins outright
						else
						{
							if ( f.TriItem[tj] != item ) continue;
							var rel = MathF.Abs( f.Depth[j] - z ) / MathF.Max( z, 1e-4f );
							if ( rel > 0.03f ) continue; // the same mesh, but another layer of it (an arm over the chest)
							score = dist2 + rel * 400f;
						}
					}
					if ( score < bestScore ) { bestScore = score; best = j; }
				}
			}
		return best >= 0 ? best : own;
	}
}
