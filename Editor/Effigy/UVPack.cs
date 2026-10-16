using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Pack UV islands into the texture square with a margin given IN PIXELS at a texture size, turning
/// each island to whatever angle boxes it tightest - what a texture artist means by "pack with 4 px
/// padding at 2K", where <see cref="UVIslands.Pack"/> takes a margin as a fraction of the square and
/// only ever turns an island a quarter.
///
/// WHY PIXELS. The gutter exists for the bake's dilation and for mip-mapping: a 4-pixel bleed needs
/// 4 pixels of empty texture between islands at the size the texture is actually authored, and a
/// fraction of the square means a different number of pixels at every size. So the margin is
/// converted at <see cref="Options.TextureSize"/>: every pair of islands is at least that many
/// pixels apart, and every island is at least half that from the edge of the square (so a tiling
/// neighbour's island is a whole margin away too).
///
/// ARBITRARY ROTATION, PER ISLAND. Each island is first turned by the angle that gives its convex
/// hull the smallest bounding rectangle - one of the hull's own edge directions, which is the
/// rotating-calipers result - so a diagonal sleeve stops wasting the corners of its box. Only
/// rotation, never a mirror: a mirrored island bakes with its tangent frame flipped. The packer
/// may then also turn a box a quarter if that sits it lower.
///
/// SKYLINE, BOTTOM-LEFT. Boxes go tallest first onto a skyline, each at the place its top would be
/// lowest. The largest scale that still fits is found by bisection, unless
/// <see cref="Options.KeepScale"/> asks for the islands' present size - which is what texel
/// density needs: after <see cref="UVDensity.Set"/>, packing must place the islands, not resize them.
/// </summary>
public static class UVPack
{
	public sealed class Options
	{
		/// <summary>Pixels along one side of the (square) texture the margin is measured on.</summary>
		public int TextureSize = 1024;

		/// <summary>Empty texture between any two islands, in pixels at <see cref="TextureSize"/>.</summary>
		public float MarginPixels = 4f;

		/// <summary>Turn each island to its tightest angle first. Off, islands keep their angle
		/// (quarter turns are still allowed when <see cref="QuarterTurns"/>).</summary>
		public bool Rotate = true;

		/// <summary>Let the packer stand a box on its side.</summary>
		public bool QuarterTurns = true;

		/// <summary>Place the islands at their present size instead of scaling them to fill the square.</summary>
		public bool KeepScale;
	}

	public sealed class Result
	{
		/// <summary>How much every island was scaled by (1 with <see cref="Options.KeepScale"/>).</summary>
		public float Scale;

		/// <summary>False when the islands do not fit at the asked size and margin. Nothing is written then.</summary>
		public bool Fits;

		public int Islands;

		/// <summary>The margin in UV units: pixels over the texture size.</summary>
		public float Margin;

		/// <summary>The margin as asked, in pixels, and the texture size it was measured on.</summary>
		public float MarginPixels;
		public int TextureSize;

		/// <summary>Share of the square the islands' UV area covers, 0 to 1.</summary>
		public float Coverage;

		public override string ToString() => Fits
			? $"{Islands} island{(Islands == 1 ? "" : "s")} packed with {MarginPixels:0.#} px between them at {TextureSize} px, {Coverage * 100f:0.#}% of the square used"
			: $"{Islands} island{(Islands == 1 ? "" : "s")} do not fit in the square with {MarginPixels:0.#} px margins at {TextureSize} px";
	}

	sealed class Box
	{
		public int Island;
		public float Angle;       // radians, applied first about the origin
		public Vec2 Min;          // of the rotated island, at scale 1
		public Vec2 Size;         // of the rotated island, at scale 1
		public Vec2 At;           // where the box's corner lands, after scaling
		public bool Turned;       // a further quarter turn by the packer
	}

	/// <summary>Pack every island of <paramref name="mesh"/>. Writes nothing when they do not fit.</summary>
	public static Result Pack( PolyMesh mesh, Options options = null, UVIslands.Layout layout = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		options ??= new Options();
		if ( options.TextureSize <= 0 )
			throw new InvalidOperationException( "The texture size has to be above zero pixels." );
		if ( options.MarginPixels < 0f || !float.IsFinite( options.MarginPixels ) )
			throw new InvalidOperationException( "The margin can't be negative: it is how many empty pixels to leave between islands." );

		var margin = options.MarginPixels / options.TextureSize;
		if ( margin >= 0.5f )
			throw new InvalidOperationException( $"A {options.MarginPixels:0.#} px margin is half of a {options.TextureSize} px texture or more, which leaves no room for any island." );

		layout ??= UVIslands.Find( mesh );
		var result = new Result { Islands = layout.Count, Margin = margin, Scale = 1f, MarginPixels = options.MarginPixels, TextureSize = options.TextureSize };
		if ( layout.Count == 0 )
		{
			result.Fits = true;
			return result;
		}

		var boxes = new List<Box>();
		for ( var i = 0; i < layout.Count; i++ )
		{
			var points = Points( mesh, layout.Islands[i] );
			var angle = options.Rotate ? TightestAngle( points ) : 0f;
			var (min, max) = RotatedBounds( points, angle );
			boxes.Add( new Box { Island = i, Angle = angle, Min = min, Size = max - min } );
		}

		float scale;
		if ( options.KeepScale )
		{
			scale = 1f;
			if ( !TryPack( boxes, scale, margin, options.QuarterTurns, commit: true ) )
				return result;
		}
		else
		{
			var largest = boxes.Max( b => MathF.Max( b.Size.x, b.Size.y ) );
			if ( largest <= 1e-12f )
				return result;

			var low = 0f;
			var high = 1f / largest;
			while ( TryPack( boxes, high, margin, options.QuarterTurns, commit: false ) && high < 1e8f )
				high *= 2f;

			for ( var i = 0; i < 40; i++ )
			{
				var mid = (low + high) * 0.5f;
				if ( TryPack( boxes, mid, margin, options.QuarterTurns, commit: false ) )
					low = mid;
				else
					high = mid;
			}

			scale = low;
			if ( scale <= 0f || !TryPack( boxes, scale, margin, options.QuarterTurns, commit: true ) )
				return result;
		}

		// Write: rotate by the island's angle, drop its rotated bounds to the origin, scale, turn
		// a quarter if the packer stood it up (counter-clockwise, which keeps the winding), place.
		foreach ( var box in boxes )
		{
			var cos = MathF.Cos( box.Angle );
			var sin = MathF.Sin( box.Angle );

			foreach ( var f in layout.Islands[box.Island] )
			{
				var uvs = mesh.Faces[f].UVs;
				for ( var c = 0; c < uvs.Length; c++ )
				{
					var p = uvs[c];
					var r = new Vec2( p.x * cos - p.y * sin, p.x * sin + p.y * cos );
					var local = (r - box.Min) * scale;
					if ( box.Turned )
						local = new Vec2( -local.y, local.x ) + new Vec2( box.Size.y * scale, 0f );
					uvs[c] = box.At + local;
				}
			}
		}

		result.Fits = true;
		result.Scale = scale;
		result.Coverage = Coverage( mesh );
		return result;
	}

	/// <summary>Skyline bottom-left at one scale. Boxes are inflated by the margin, and the bin is
	/// the square less half a margin all round, so inflated boxes that do not overlap leave a whole
	/// margin between the islands and half of one at the edge.</summary>
	static bool TryPack( List<Box> boxes, float scale, float margin, bool quarterTurns, bool commit )
	{
		var order = Enumerable.Range( 0, boxes.Count ).OrderByDescending( i => MathF.Max( boxes[i].Size.x, boxes[i].Size.y ) )
			.ThenByDescending( i => boxes[i].Size.x * boxes[i].Size.y ).ThenBy( i => boxes[i].Island ).ToList();

		// Skyline segments: x, width, height. The usable square runs from 0 to 1 in inflated space.
		var sky = new List<(float X, float W, float Y)> { (0f, 1f, 0f) };

		foreach ( var index in order )
		{
			var box = boxes[index];
			var bestTop = float.MaxValue;
			var bestX = 0f;
			var bestY = 0f;
			var bestTurned = false;
			var bestW = 0f;

			for ( var turn = 0; turn < (quarterTurns ? 2 : 1); turn++ )
			{
				var w = (turn == 0 ? box.Size.x : box.Size.y) * scale + margin;
				var h = (turn == 0 ? box.Size.y : box.Size.x) * scale + margin;
				if ( w > 1f + 1e-6f || h > 1f + 1e-6f )
					continue;

				for ( var s = 0; s < sky.Count; s++ )
				{
					var x = sky[s].X;
					if ( x + w > 1f + 1e-6f )
						break;

					// The floor under [x, x + w) is the highest skyline segment it spans.
					var y = 0f;
					var reach = x + w;
					for ( var t = s; t < sky.Count && sky[t].X < reach - 1e-7f; t++ )
						y = MathF.Max( y, sky[t].Y );

					var top = y + h;
					if ( top > 1f + 1e-6f )
						continue;

					if ( top < bestTop - 1e-7f || (MathF.Abs( top - bestTop ) <= 1e-7f && x < bestX) )
					{
						bestTop = top;
						bestX = x;
						bestY = y;
						bestTurned = turn == 1;
						bestW = w;
					}
				}
			}

			if ( bestTop == float.MaxValue )
				return false;

			if ( commit )
			{
				box.Turned = bestTurned;
				box.At = new Vec2( bestX + margin * 0.5f, bestY + margin * 0.5f );
			}

			Raise( sky, bestX, bestW, bestTop );
		}

		return true;
	}

	/// <summary>Put a segment of height <paramref name="top"/> over [x, x + w) into the skyline.</summary>
	static void Raise( List<(float X, float W, float Y)> sky, float x, float w, float top )
	{
		var end = x + w;
		var next = new List<(float X, float W, float Y)>();
		foreach ( var seg in sky )
		{
			var segEnd = seg.X + seg.W;
			if ( segEnd <= x + 1e-7f || seg.X >= end - 1e-7f )
			{
				next.Add( seg );
				continue;
			}

			if ( seg.X < x )
				next.Add( (seg.X, x - seg.X, seg.Y) );
			if ( segEnd > end )
				next.Add( (end, segEnd - end, seg.Y) );
		}

		next.Add( (x, w, top) );
		next.Sort( ( a, b ) => a.X.CompareTo( b.X ) );

		// Merge neighbours at one height, so the skyline does not fragment into slivers.
		sky.Clear();
		foreach ( var seg in next )
		{
			if ( sky.Count > 0 && MathF.Abs( sky[^1].Y - seg.Y ) < 1e-7f && MathF.Abs( sky[^1].X + sky[^1].W - seg.X ) < 1e-6f )
				sky[^1] = (sky[^1].X, sky[^1].W + seg.W, seg.Y);
			else
				sky.Add( seg );
		}
	}

	static List<Vec2> Points( PolyMesh mesh, List<int> faces )
	{
		var points = new List<Vec2>();
		foreach ( var f in faces )
			points.AddRange( mesh.Faces[f].UVs );
		return points;
	}

	static (Vec2 Min, Vec2 Max) RotatedBounds( List<Vec2> points, float angle )
	{
		var cos = MathF.Cos( angle );
		var sin = MathF.Sin( angle );
		var min = new Vec2( float.MaxValue, float.MaxValue );
		var max = new Vec2( float.MinValue, float.MinValue );
		foreach ( var p in points )
		{
			var r = new Vec2( p.x * cos - p.y * sin, p.x * sin + p.y * cos );
			min = new Vec2( MathF.Min( min.x, r.x ), MathF.Min( min.y, r.y ) );
			max = new Vec2( MathF.Max( max.x, r.x ), MathF.Max( max.y, r.y ) );
		}

		return (min, max);
	}

	/// <summary>
	/// The angle to turn a point set by so its bounding box has the least area: minus the angle of
	/// one of its convex hull's edges. A box already as tight as it gets keeps angle zero, so an
	/// axis-aligned island is not turned for nothing.
	/// </summary>
	public static float TightestAngle( IReadOnlyList<Vec2> points )
	{
		var hull = Hull( points );
		if ( hull.Count < 3 )
			return 0f;

		var (m0, x0) = RotatedBounds( hull, 0f );
		var bestArea = (x0.x - m0.x) * (x0.y - m0.y);
		var best = 0f;

		for ( var i = 0; i < hull.Count; i++ )
		{
			var e = hull[(i + 1) % hull.Count] - hull[i];
			if ( e.LengthSquared < 1e-20f )
				continue;

			var angle = -MathF.Atan2( e.y, e.x );
			var (min, max) = RotatedBounds( hull, angle );
			var area = (max.x - min.x) * (max.y - min.y);
			if ( area < bestArea * (1f - 1e-4f) )
			{
				bestArea = area;
				best = angle;
			}
		}

		return best;
	}

	/// <summary>Andrew's monotone chain, counter-clockwise, no collinear points.</summary>
	static List<Vec2> Hull( IReadOnlyList<Vec2> input )
	{
		var pts = input.Distinct().OrderBy( p => p.x ).ThenBy( p => p.y ).ToList();
		if ( pts.Count < 3 )
			return pts;

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

		return hull;
	}

	/// <summary>Share of the unit square the faces' UV area covers (overlaps counted twice).</summary>
	static float Coverage( PolyMesh mesh )
	{
		var area = 0.0;
		foreach ( var face in mesh.Faces )
			for ( var i = 1; i + 1 < face.UVs.Length; i++ )
				area += 0.5 * Math.Abs( Vec2.Cross( face.UVs[i] - face.UVs[0], face.UVs[i + 1] - face.UVs[0] ) );
		return (float)Math.Min( 1.0, area );
	}
}
