using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// One point of a cord as a knot is drawn: where it is in the knot's own frame (x right, y up,
/// z out of the cloth, inches) and what the relax may do with it.
/// </summary>
public readonly record struct CordPoint( Vec3 P, bool Pin = false, bool Hidden = false, bool Core = false );

/// <summary>A cord after it is sampled: its nodes in world space and, per node, which way is out
/// of the cloth under it (so a flat lace lies flat) and the flags the relax reads.</summary>
public sealed class CordLine
{
	public List<Vec3> P = new();
	public List<Vec3> Up = new();
	public List<bool> Pin = new();
	public List<bool> Hidden = new();
	public List<bool> Core = new();
	/// <summary>The last node is a free end (a tail), so it can carry an aglet or a stopper.</summary>
	public bool FreeEnd;
	public int Count => P.Count;
}

/// <summary>
/// The knots clothing is tied with, drawn as cords in the knot's own frame.
///
/// WHY TEMPLATES AND NOT A KNOT CODE. A garment wears a handful of named knots, and each is drawn
/// the way a knot book draws it: the cord's path with every crossing already lifted over or under
/// (the same signed lift Celtic-knot generators use). The relax that follows only pushes cords
/// apart and draws them snug, so it keeps the crossings it is given; drawing them right is the
/// whole of "which knot this is", and <see cref="KnotCheck"/> reads them back to prove it.
///
/// THE BOW IS A REEF WITH BOTH ENDS SLIPPED (shoelace bow = double-slipped reef knot), so the bow,
/// the square knot and their granny versions share one core: a half-knot, then one lace wrapped
/// round the other's neck. With loops it is a bow; with the loops pulled out it is a square knot.
/// A granny ties the second half the same way round as the first, so it sits turned a quarter -
/// the crooked, along-the-shoe bow that is how anyone spots one.
///
/// Units: d is the cord's thickness (its diameter, or a flat lace's thickness), w its width in the
/// plane of the knot (d again for round cord). Everything in a knot's core scales with them.
/// </summary>
public static class KnotPaths
{
	/// <summary>
	/// The bow / square-knot family. The standing parts arrive from <paramref name="left"/> and
	/// <paramref name="right"/> (points on the cloth, in the knot frame, usually the top eyelets or
	/// where a belt comes round the body) and the knot sits at the origin.
	/// </summary>
	/// <param name="loop">Loop length, centre to tip; 0 ties a square knot (no loops).</param>
	/// <param name="tail">Length of each free end beyond the knot.</param>
	/// <param name="granny">Second half tied the same way as the first.</param>
	public static List<List<CordPoint>> Bow( Vec3 left, Vec3 right, float d, float w, float loop, float tail, bool granny )
	{
		var r = d * 0.5f;
		var u = MathF.Max( d, w );
		// Layers off the cloth: the half-knot lies on it, the neck over that, the wrap over everything.
		var z0 = r;
		var z1 = r + d * 1.1f;
		var z2 = r + d * 2.3f;
		var neckY = u * 0.5f;
		// A granny's loops (or ends) come out turned a quarter: up and down the cloth, not across.
		Vec3 Out( float x, float y, float z ) => granny ? new Vec3( -y * 0.8f + x * 0.25f, x, z ) : new Vec3( x, y, z );
		var tailDrop = MathF.Max( tail, u * 2f );

		// RIGHT LACE: under the left one in the half-knot, then up into the neck that forms loop A
		// (or, for a square knot, the single strand that turns down as its end).
		var a = new List<CordPoint>
		{
			new( right, Pin: true ),
			new( new Vec3( u * 0.7f, -u * 1.5f, z0 ) ),
			new( new Vec3( 0f, -u * 1.0f, z0 ), Core: true ),
			new( new Vec3( -u * 0.9f, -u * 0.6f, z0 + d * 0.3f ), Core: true ),
			new( new Vec3( -u * 0.95f, u * 0.15f, z1 ), Core: true ),
			new( new Vec3( -u * 0.45f, neckY, z1 ), Core: true ),
			new( new Vec3( u * 0.8f, neckY, z1 ), Core: true ),
		};

		if ( loop > 0f )
		{
			a.Add( new( Out( u * 0.8f + loop * 0.45f, neckY + loop * 0.3f, z1 - d * 0.3f ) ) );
			a.Add( new( Out( u * 0.8f + loop, loop * 0.12f, z1 - d * 0.5f ) ) );
			a.Add( new( Out( u * 0.8f + loop * 0.45f, -neckY - loop * 0.2f, z1 - d * 0.3f ) ) );
			a.Add( new( new Vec3( u * 0.8f, -neckY, z1 ), Core: true ) );
			a.Add( new( new Vec3( -u * 0.2f, -neckY, z1 ), Core: true ) );
			// End A leaves from under the wrap and falls to the right.
			a.Add( new( new Vec3( u * 0.2f, -u * 1.4f, z1 - d * 0.2f ) ) );
			a.Add( new( Out( u * 0.9f, -u * 1.2f - tailDrop * 0.5f, z0 + d * 0.2f ) ) );
			a.Add( new( Out( u * 1.3f, -u * 1.2f - tailDrop, z0 ) ) );
		}
		else
		{
			// Square knot: the neck is one strand; it turns down at the right as end A.
			a.Add( new( new Vec3( u * 1.5f, 0f, z1 - d * 0.2f ), Core: true ) );
			a.Add( new( Out( u * 1.7f, -u * 1.0f - tailDrop * 0.4f, z0 + d * 0.3f ) ) );
			a.Add( new( Out( u * 1.9f, -u * 1.0f - tailDrop, z0 ) ) );
		}

		// LEFT LACE: over the right one, round the neck (front, then behind), then pushed through
		// the wrap as loop B (a bow) or straight out as end B (a square knot).
		var flip = granny ? -1f : 1f;
		var b = new List<CordPoint>
		{
			new( left, Pin: true ),
			new( new Vec3( -u * 0.7f, -u * 1.5f, z0 ) ),
			new( new Vec3( 0f, -u * 1.0f, z0 + d * 1.05f ), Core: true ),
			new( new Vec3( u * 0.9f, -u * 0.7f, z1 ), Core: true ),
			// The wrap: up across the front of the neck - the band that is the knot's face.
			new( new Vec3( u * 0.35f, -u * 1.0f, z2 - d * 0.4f ), Core: true ),
			new( new Vec3( 0f, 0f, z2 ), Core: true ),
			new( new Vec3( -u * 0.3f * flip, u * 1.05f, z2 - d * 0.5f ), Core: true ),
			// ...over the top and down behind the neck, between it and the half-knot.
			new( new Vec3( -u * 0.1f * flip, u * 1.15f, z1 ), Core: true ),
			new( new Vec3( u * 0.1f, u * 0.2f, z0 + d * 0.05f ), Core: true ),
			new( new Vec3( u * 0.35f, -u * 0.75f, z0 + d * 0.5f ), Core: true ),
		};

		var zb = z1 + d * 0.15f;

		if ( loop > 0f )
		{
			b.Add( new( new Vec3( u * 0.45f, -neckY * 0.55f, zb ), Core: true ) );
			b.Add( new( new Vec3( -u * 0.8f, -neckY * 0.55f, zb ), Core: true ) );
			b.Add( new( Out( -u * 0.8f - loop * 0.45f, -neckY - loop * 0.2f, zb - d * 0.4f ) ) );
			b.Add( new( Out( -u * 0.8f - loop, loop * 0.12f, zb - d * 0.6f ) ) );
			b.Add( new( Out( -u * 0.8f - loop * 0.45f, neckY + loop * 0.3f, zb - d * 0.4f ) ) );
			b.Add( new( new Vec3( -u * 0.8f, neckY * 0.55f, zb ), Core: true ) );
			b.Add( new( new Vec3( u * 0.4f, neckY * 0.55f, zb ), Core: true ) );
			// End B trails back out of the wrap the way the bight went in, and falls to the left.
			b.Add( new( new Vec3( u * 0.5f, -u * 0.6f, zb + d * 0.3f ), Core: true ) );
			b.Add( new( new Vec3( -u * 0.3f, -u * 1.6f, zb ) ) );
			b.Add( new( Out( -u * 0.9f, -u * 1.4f - tailDrop * 0.5f, z0 + d * 0.6f ) ) );
			b.Add( new( Out( -u * 1.3f, -u * 1.4f - tailDrop, z0 + d * 0.3f ) ) );
		}
		else
		{
			b.Add( new( new Vec3( -u * 0.3f, -neckY * 0.3f, zb ), Core: true ) );
			b.Add( new( new Vec3( -u * 1.5f, -u * 0.1f, zb - d * 0.3f ), Core: true ) );
			b.Add( new( Out( -u * 1.7f, -u * 1.0f - tailDrop * 0.4f, z0 + d * 0.3f ) ) );
			b.Add( new( Out( -u * 1.9f, -u * 1.0f - tailDrop, z0 ) ) );
		}

		return new List<List<CordPoint>> { a, b };
	}

	/// <summary>
	/// A stopper knot on a hanging cord: the cord comes down from <paramref name="top"/>, the knot
	/// sits <paramref name="at"/> below it and <paramref name="tail"/> hangs on past.
	///
	/// Both are closed knots with the loop cut where it bulges furthest: an overhand is a trefoil
	/// cut open, a figure-eight the 4_1 knot cut open. Cutting at the outermost lobe and running the
	/// ends straight away along the cut's own direction adds no crossing, so the path keeps exactly
	/// the knot's crossings (3 and 4). Curves: x=sin t+2 sin 2t, y=cos t-2 cos 2t, z=-sin 3t for the
	/// trefoil; x=(2+cos 2t)cos 3t, y=(2+cos 2t)sin 3t, z=sin 4t for the figure-eight.
	/// </summary>
	public static List<CordPoint> Stopper( Vec3 top, float at, float tail, float d, bool figureEight )
	{
		var r = d * 0.5f;
		var s = d * 1.15f;
		var pts = new List<CordPoint> { new( top, Pin: true ) };
		var knotY = top.y - at;
		const int samples = 36;

		// Each curve's cut is at a lobe whose tangent runs along +X (trefoil) or +Y (figure-eight);
		// map that tangent onto "down the cord" and the rest of the knot off to one side of it.
		Vec3 Map( float along, float side, float depth ) =>
			new( top.x + side * s, knotY - along * s, r + (depth + 1f) * d * 0.8f );

		pts.Add( new( new Vec3( top.x, knotY + 4.5f * s, r ) ) );

		for ( var k = 0; k <= samples; k++ )
		{
			var f = k / (float)samples;

			if ( figureEight )
			{
				// Upper end is just before t = 2pi; walk back to just after 0.
				var t = 2f * MathF.PI - 0.12f - f * (2f * MathF.PI - 0.24f);
				var rad = 2f + MathF.Cos( 2f * t );
				pts.Add( new( Map( rad * MathF.Sin( 3f * t ), 3f - rad * MathF.Cos( 3f * t ), MathF.Sin( 4f * t ) ), Core: true ) );
			}
			else
			{
				// Upper end is just before t = 3pi; walk back to just after pi.
				var t = 3f * MathF.PI - 0.12f - f * (2f * MathF.PI - 0.24f);
				var x = MathF.Sin( t ) + 2f * MathF.Sin( 2f * t );
				var y = MathF.Cos( t ) - 2f * MathF.Cos( 2f * t );
				pts.Add( new( Map( x, y + 3f, -MathF.Sin( 3f * t ) ), Core: true ) );
			}
		}

		pts.Add( new( new Vec3( top.x, knotY - 4.5f * s, r ) ) );
		pts.Add( new( new Vec3( top.x, knotY - 4.5f * s - MathF.Max( tail, d ), r ) ) );
		return pts;
	}

	/// <summary>
	/// A lacing between two rows of eyelets <paramref name="gap"/> apart, <paramref name="rows"/>
	/// pairs <paramref name="spacing"/> apart going up from the origin.
	///
	/// CRISS-CROSS: each end comes up out of a bottom eyelet and crosses to the next row on the
	/// other side, going in under the edge and up through the eyelet each time - the little loop
	/// round the edge every sneaker shows. The ends swap which is on top every row, as a careful
	/// lacer does. STRAIGHT BAR: only the bars show; the runs between them are under the cloth.
	/// Both ends come out of the top pair and, when <paramref name="bow"/>, tie a bow above it.
	/// </summary>
	public static List<List<CordPoint>> Lacing( float gap, int rows, float spacing, float d, float w, bool straightBar,
		bool bow, float loop, float tail, out List<Vec3> eyelets )
	{
		var r = d * 0.5f;
		var half = gap * 0.5f;
		rows = Math.Max( 2, rows );
		eyelets = new List<Vec3>();

		for ( var k = 0; k < rows; k++ )
		{
			eyelets.Add( new Vec3( -half, k * spacing, 0f ) );
			eyelets.Add( new Vec3( half, k * spacing, 0f ) );
		}

		var cords = new List<List<CordPoint>>();
		var under = -d * 1.5f;
		var top = (rows - 1) * spacing;

		// One eyelet pass: in under the edge on the inside, up through the hole, back over the edge.
		void Through( List<CordPoint> c, float x, float y, float side )
		{
			var inward = -side;
			c.Add( new( new Vec3( x + inward * w * 1.6f, y, r ) ) );
			c.Add( new( new Vec3( x + inward * w * 0.9f, y, under ), Hidden: true ) );
			c.Add( new( new Vec3( x, y, under ), Hidden: true ) );
			c.Add( new( new Vec3( x, y, r * 0.6f ), Pin: true ) );
			c.Add( new( new Vec3( x + inward * w * 0.6f, y + d * 0.4f, r + d * 0.9f ) ) );
		}

		if ( straightBar )
		{
			// Bars, bottom to second-from-top; each runs out of one eyelet and into the other.
			for ( var k = 0; k < rows - 1; k++ )
			{
				var y = k * spacing;
				cords.Add( new List<CordPoint>
				{
					new( new Vec3( -half, y, under ), Hidden: true, Pin: true ),
					new( new Vec3( -half, y, r * 0.6f ), Pin: true ),
					new( new Vec3( 0f, y, r + d * 0.2f ) ),
					new( new Vec3( half, y, r * 0.6f ), Pin: true ),
					new( new Vec3( half, y, under ), Hidden: true, Pin: true ),
				} );
			}

			var endA = new List<CordPoint> { new( new Vec3( -half, top, under ), Hidden: true, Pin: true ), new( new Vec3( -half, top, r * 0.6f ), Pin: true ) };
			var endB = new List<CordPoint> { new( new Vec3( half, top, under ), Hidden: true, Pin: true ), new( new Vec3( half, top, r * 0.6f ), Pin: true ) };
			TieOff( cords, endA, endB );
			return cords;
		}

		var ends = new[] { new List<CordPoint>(), new List<CordPoint>() };

		for ( var e = 0; e < 2; e++ )
		{
			var side = e == 0 ? -1f : 1f;
			ends[e].Add( new( new Vec3( side * half, 0f, under ), Hidden: true, Pin: true ) );
			ends[e].Add( new( new Vec3( side * half, 0f, r * 0.6f ), Pin: true ) );

			for ( var k = 1; k < rows; k++ )
			{
				var from = side * (k % 2 == 1 ? 1f : -1f);
				var to = -from;
				// End 0 is on top on odd rows, end 1 on even ones: they take turns.
				var onTop = (k % 2 == 1) == (e == 0);
				var midY = (k - 0.5f) * spacing;
				ends[e].Add( new( new Vec3( 0f, midY, onTop ? r + d * 1.1f : r ) ) );
				Through( ends[e], to * half, k * spacing, to );
			}
		}

		TieOff( cords, ends[0], ends[1] );
		return cords;

		void TieOff( List<List<CordPoint>> into, List<CordPoint> l, List<CordPoint> rgt )
		{
			// Which end finishes on which side depends on the row count; the bow takes them as they come.
			var lx = l[^1].P.x;
			var rx = rgt[^1].P.x;

			if ( lx > rx )
				(l, rgt) = (rgt, l);

			if ( bow )
			{
				var centre = new Vec3( 0f, top + MathF.Max( spacing * 0.8f, d * 4f ), 0f );
				var knot = Bow( l[^1].P - centre, rgt[^1].P - centre, d, w, loop, tail, false );
				// Bow() pins its first point - that is the eyelet these ends already reached.
				rgt.AddRange( knot[0].Skip( 1 ).Select( p => p with { P = p.P + centre } ) );
				l.AddRange( knot[1].Skip( 1 ).Select( p => p with { P = p.P + centre } ) );
			}
			else
			{
				l.Add( new( l[^1].P + new Vec3( -d * 2f, -tail * 0.5f, r ) ) );
				l.Add( new( l[^1].P + new Vec3( -d * 2f, -tail * 0.5f, 0f ) ) );
				rgt.Add( new( rgt[^1].P + new Vec3( d * 2f, -tail * 0.5f, r ) ) );
				rgt.Add( new( rgt[^1].P + new Vec3( d * 2f, -tail * 0.5f, 0f ) ) );
			}

			into.Add( l );
			into.Add( rgt );
		}
	}

	/// <summary>Two cords coming out of a pair of grommets <paramref name="gap"/> apart and hanging
	/// <paramref name="length"/> down the cloth - a hood or waist drawstring left untied.</summary>
	public static List<List<CordPoint>> Drawstring( float gap, float length, float d )
	{
		var r = d * 0.5f;
		var cords = new List<List<CordPoint>>();

		foreach ( var side in new[] { -1f, 1f } )
		{
			var x = side * gap * 0.5f;
			cords.Add( new List<CordPoint>
			{
				new( new Vec3( x, 0f, -d * 1.5f ), Hidden: true, Pin: true ),
				new( new Vec3( x, 0f, r * 0.6f ), Pin: true ),
				new( new Vec3( x + side * d, -d * 2f, r + d * 0.6f ) ),
				new( new Vec3( x + side * d * 0.5f, -length * 0.5f, r ) ),
				new( new Vec3( x, -length, r ) ),
			} );
		}

		return cords;
	}
}

/// <summary>
/// Draws a knot snug and keeps every cord out of every other and out of the cloth: a quasi-static
/// position-based rod. Per sweep: a little gravity on free nodes, segment lengths (shortened inside
/// the knot's core by <c>snug</c>, which is the "pull the ends" of tightening), a bend term toward
/// the neighbours' midpoint, sphere-sphere contact between nodes that are not near neighbours along
/// a cord (the capsule rule every rope-knot sim uses: closer than two radii means touching), and
/// every few sweeps, contact with the cloth (centre at least a radius above it).
///
/// Nodes are spaced half a radius apart so the spheres overlap along a cord and no strand can slip
/// between two nodes of another. Pinned nodes (where a cord comes out of an eyelet) and hidden ones
/// (the runs under the cloth) never move.
/// </summary>
public static class CordRelax
{
	public static void Run( IList<CordLine> cords, float radius, Func<Vec3, (Vec3 Point, Vec3 Normal)?> nearestCloth,
		Vec3 down, int iterations = 160, float snug = 0.5f, float gravity = 0.05f, float bend = 0.25f )
	{
		var nodes = new List<(int C, int I)>();

		for ( var c = 0; c < cords.Count; c++ )
			for ( var i = 0; i < cords[c].Count; i++ )
				nodes.Add( (c, i) );

		var rest = cords.Select( c => Enumerable.Range( 0, Math.Max( 0, c.Count - 1 ) )
			.Select( i => (c.P[i + 1] - c.P[i]).Length * (c.Core[i] && c.Core[i + 1] ? 1f - 0.12f * Math.Clamp( snug, 0f, 1f ) : 1f) ).ToArray() ).ToList();

		var minGap = radius * 2f;
		var spacing = cords.SelectMany( ( c, ci ) => rest[ci] ).DefaultIfEmpty( radius ).Average();
		var skip = Math.Max( 2, (int)MathF.Ceiling( minGap * 1.4f / MathF.Max( spacing, 1e-4f ) ) );
		var cell = minGap;
		var grid = new Dictionary<(int, int, int), List<int>>();

		bool Free( int c, int i ) => !cords[c].Pin[i] && !cords[c].Hidden[i];

		for ( var it = 0; it < iterations; it++ )
		{
			// Gravity: strongest on loose ends, none in the knot (it is held by its own wraps).
			if ( gravity > 0f )
				foreach ( var cord in cords )
					for ( var i = 0; i < cord.Count; i++ )
						if ( !cord.Pin[i] && !cord.Hidden[i] && !cord.Core[i] )
							cord.P[i] += down * (gravity * spacing);

			for ( var c = 0; c < cords.Count; c++ )
			{
				var cord = cords[c];

				for ( var i = 0; i < cord.Count - 1; i++ )
				{
					var wa = Free( c, i ) ? 1f : 0f;
					var wb = Free( c, i + 1 ) ? 1f : 0f;

					if ( wa + wb == 0f )
						continue;

					var delta = cord.P[i + 1] - cord.P[i];
					var len = delta.Length;

					if ( len < 1e-7f )
						continue;

					var corr = delta * ((len - rest[c][i]) / len / (wa + wb));
					cord.P[i] += corr * wa;
					cord.P[i + 1] -= corr * wb;
				}

				for ( var i = 1; i < cord.Count - 1; i++ )
					if ( Free( c, i ) )
						cord.P[i] += ((cord.P[i - 1] + cord.P[i + 1]) * 0.5f - cord.P[i]) * bend;
			}

			// Self and cord-cord contact through a hash grid of node spheres.
			grid.Clear();

			for ( var n = 0; n < nodes.Count; n++ )
			{
				var (c, i) = nodes[n];

				if ( cords[c].Hidden[i] )
					continue;

				var key = Key( cords[c].P[i] );

				if ( !grid.TryGetValue( key, out var list ) )
					grid[key] = list = new List<int>();

				list.Add( n );
			}

			for ( var n = 0; n < nodes.Count; n++ )
			{
				var (c, i) = nodes[n];

				if ( cords[c].Hidden[i] )
					continue;

				var (kx, ky, kz) = Key( cords[c].P[i] );

				for ( var dx = -1; dx <= 1; dx++ )
					for ( var dy = -1; dy <= 1; dy++ )
						for ( var dz = -1; dz <= 1; dz++ )
						{
							if ( !grid.TryGetValue( (kx + dx, ky + dy, kz + dz), out var list ) )
								continue;

							foreach ( var m in list )
							{
								if ( m <= n )
									continue;

								var (c2, j) = nodes[m];

								if ( c2 == c && Math.Abs( i - j ) <= skip )
									continue;

								var delta = cords[c2].P[j] - cords[c].P[i];
								var dist = delta.Length;

								if ( dist >= minGap || dist < 1e-7f )
									continue;

								var wa = Free( c, i ) ? 1f : 0f;
								var wb = Free( c2, j ) ? 1f : 0f;

								if ( wa + wb == 0f )
									continue;

								var push = delta * ((minGap - dist) / dist / (wa + wb));
								cords[c].P[i] -= push * wa;
								cords[c2].P[j] += push * wb;
							}
						}
			}

			if ( nearestCloth is not null && (it % 4 == 3 || it == iterations - 1) )
				foreach ( var cord in cords )
					for ( var i = 0; i < cord.Count; i++ )
					{
						if ( cord.Hidden[i] || cord.Pin[i] )
							continue;

						if ( nearestCloth( cord.P[i] ) is not { } hit )
							continue;

						var n = Vec3.Dot( hit.Normal, cord.Up[i] ) < 0f ? -hit.Normal : hit.Normal;
						cord.Up[i] = n;
						var height = Vec3.Dot( cord.P[i] - hit.Point, n );

						if ( height < radius )
							cord.P[i] += n * (radius - height);
					}
		}

		(int, int, int) Key( Vec3 p ) => ((int)MathF.Floor( p.x / cell ), (int)MathF.Floor( p.y / cell ), (int)MathF.Floor( p.z / cell ));
	}
}

/// <summary>
/// Reads a knot back: crossings of its picture seen from one side, and how close any two parts
/// of the cords come. A generated knot is checked with these, the way knot tables check a curve
/// (project it, find where segments cross, say which is on top).
/// </summary>
public static class KnotCheck
{
	/// <summary>Crossings of the polylines' projection along <paramref name="view"/>, each with
	/// which polyline/segment is over (nearer the viewer).</summary>
	public static List<(int OverCord, int OverSeg, int UnderCord, int UnderSeg)> Crossings( IList<IList<Vec3>> cords, Vec3 view )
	{
		view = view.Normal;
		var any = MathF.Abs( view.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var ax = Vec3.Cross( any, view ).Normal;
		var ay = Vec3.Cross( view, ax );
		Vec2 Flat( Vec3 p ) => new( Vec3.Dot( p, ax ), Vec3.Dot( p, ay ) );
		var result = new List<(int, int, int, int)>();

		for ( var c1 = 0; c1 < cords.Count; c1++ )
			for ( var i = 0; i < cords[c1].Count - 1; i++ )
				for ( var c2 = c1; c2 < cords.Count; c2++ )
					for ( var j = c2 == c1 ? i + 2 : 0; j < cords[c2].Count - 1; j++ )
					{
						var a0 = Flat( cords[c1][i] );
						var a1 = Flat( cords[c1][i + 1] );
						var b0 = Flat( cords[c2][j] );
						var b1 = Flat( cords[c2][j + 1] );
						var da = a1 - a0;
						var db = b1 - b0;
						var den = Vec2.Cross( da, db );

						if ( MathF.Abs( den ) < 1e-12f )
							continue;

						var t = Vec2.Cross( b0 - a0, db ) / den;
						var s = Vec2.Cross( b0 - a0, da ) / den;

						if ( t < 0f || t >= 1f || s < 0f || s >= 1f )
							continue;

						var za = Vec3.Dot( Vec3.Lerp( cords[c1][i], cords[c1][i + 1], t ), view );
						var zb = Vec3.Dot( Vec3.Lerp( cords[c2][j], cords[c2][j + 1], s ), view );
						result.Add( za >= zb ? (c1, i, c2, j) : (c2, j, c1, i) );
					}

		return result;
	}

	/// <summary>The smallest distance between nodes of the cords that are not within
	/// <paramref name="skip"/> of each other along the same cord.</summary>
	public static float MinClearance( IList<IList<Vec3>> cords, int skip )
	{
		var best = float.MaxValue;

		for ( var c1 = 0; c1 < cords.Count; c1++ )
			for ( var i = 0; i < cords[c1].Count; i++ )
				for ( var c2 = c1; c2 < cords.Count; c2++ )
					for ( var j = c2 == c1 ? i + skip + 1 : 0; j < cords[c2].Count; j++ )
						best = MathF.Min( best, (cords[c1][i] - cords[c2][j]).Length );

		return best;
	}
}

public enum CordShape { Round, Flat, Square, Laid }

/// <summary>
/// The tube along a relaxed cord. A round lace is a ring; a flat lace or a belt a rounded slab
/// kept lying flat against the cloth (its wide side turned to the cloth's normal, the way a flat
/// lace or a robe belt settles); a leather thong a square; a twisted rope three strands on a helix
/// round the centreline. UVs: U round the profile, V along the cord in profile-perimeters, so a
/// texel stays square on a thin lace and a thick belt alike.
/// </summary>
public static class CordSweep
{
	/// <param name="width">Across the flat side; ignored for round and laid cord.</param>
	public static void Add( PolyMesh into, IReadOnlyList<Vec3> path, IReadOnlyList<Vec3> ups, CordShape shape,
		float thickness, float width, int slot, bool caps = true, int sides = 8 )
	{
		if ( path.Count < 2 )
			return;

		var frames = Frames( path, ups );

		if ( shape == CordShape.Laid )
		{
			// Three strands, each a third of the way round, laid at about three diameters a turn
			// (zero-twist 3-strand rope lies at ~43 degrees), touching at the centre.
			var r = thickness * 0.5f;
			var lay = thickness * 3.2f;

			for ( var k = 0; k < 3; k++ )
			{
				var strand = new List<Vec3>( path.Count );
				var along = 0f;

				for ( var i = 0; i < path.Count; i++ )
				{
					if ( i > 0 ) along += (path[i] - path[i - 1]).Length;
					var a = k * MathF.PI * 2f / 3f + along / lay * MathF.PI * 2f;
					var (_, side, up) = frames[i];
					strand.Add( path[i] + (side * MathF.Cos( a ) + up * MathF.Sin( a )) * (r * 0.5f) );
				}

				Ring( into, strand, Frames( strand, ups ), Profile( CordShape.Round, r * 1.1f, r * 1.1f, 6 ), slot, caps );
			}

			return;
		}

		Ring( into, path, frames, Profile( shape, thickness, width, sides ), slot, caps );
	}

	/// <summary>The profile as (across, out) points, counter-clockwise.</summary>
	public static List<Vec2> Profile( CordShape shape, float thickness, float width, int sides )
	{
		var p = new List<Vec2>();
		var t = thickness * 0.5f;

		switch ( shape )
		{
			case CordShape.Flat:
			{
				// A slab with half-round edges: a pressed tubular braid.
				var w = MathF.Max( width * 0.5f - t, 0f );

				for ( var k = 0; k <= 4; k++ )
				{
					var a = -MathF.PI * 0.5f + k * MathF.PI / 4f;
					p.Add( new Vec2( w + MathF.Cos( a ) * t, MathF.Sin( a ) * t ) );
				}

				for ( var k = 0; k <= 4; k++ )
				{
					var a = MathF.PI * 0.5f + k * MathF.PI / 4f;
					p.Add( new Vec2( -w + MathF.Cos( a ) * t, MathF.Sin( a ) * t ) );
				}

				break;
			}
			case CordShape.Square:
			{
				// A cut thong: square, corners eased.
				var c = t * 0.75f;
				p.AddRange( new[] { new Vec2( t, -c ), new Vec2( t, c ), new Vec2( c, t ), new Vec2( -c, t ),
					new Vec2( -t, c ), new Vec2( -t, -c ), new Vec2( -c, -t ), new Vec2( c, -t ) } );
				break;
			}
			default:
				for ( var k = 0; k < Math.Max( 3, sides ); k++ )
				{
					var a = k * MathF.PI * 2f / Math.Max( 3, sides );
					p.Add( new Vec2( MathF.Cos( a ) * t, MathF.Sin( a ) * t ) );
				}

				break;
		}

		return p;
	}

	/// <summary>
	/// A frame per node: tangent, across and out. Out follows <paramref name="ups"/> (the cloth's
	/// normal under each node) once it is turned square to the tangent; where the cord runs straight
	/// along that normal (into an eyelet) the last good frame is carried on instead, so it never spins.
	/// </summary>
	public static List<(Vec3 T, Vec3 Side, Vec3 Up)> Frames( IReadOnlyList<Vec3> path, IReadOnlyList<Vec3> ups )
	{
		var n = path.Count;
		var result = new List<(Vec3, Vec3, Vec3)>( n );
		var prevUp = Vec3.Zero;

		for ( var i = 0; i < n; i++ )
		{
			var t = (path[Math.Min( n - 1, i + 1 )] - path[Math.Max( 0, i - 1 )]);
			t = t.LengthSquared > 1e-14f ? t.Normal : new Vec3( 1, 0, 0 );
			var want = ups is not null && ups.Count > 0 ? ups[Math.Min( i, ups.Count - 1 )] : new Vec3( 0, 0, 1 );
			var up = want - t * Vec3.Dot( want, t );

			if ( up.LengthSquared < 0.04f )
			{
				up = prevUp - t * Vec3.Dot( prevUp, t );

				if ( up.LengthSquared < 1e-8f )
				{
					var any = MathF.Abs( t.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
					up = any - t * Vec3.Dot( any, t );
				}
			}

			up = up.Normal;
			prevUp = up;
			result.Add( (t, Vec3.Cross( up, t ).Normal, up) );
		}

		return result;
	}

	static void Ring( PolyMesh into, IReadOnlyList<Vec3> path, List<(Vec3 T, Vec3 Side, Vec3 Up)> frames,
		List<Vec2> profile, int slot, bool caps )
	{
		var n = path.Count;
		var m = profile.Count;
		var perimeter = 0f;

		for ( var k = 0; k < m; k++ )
			perimeter += (profile[(k + 1) % m] - profile[k]).Length;

		perimeter = MathF.Max( perimeter, 1e-5f );
		var rings = new int[n][];

		for ( var i = 0; i < n; i++ )
		{
			var (_, side, up) = frames[i];
			rings[i] = new int[m];

			for ( var k = 0; k < m; k++ )
				rings[i][k] = into.AddVertex( path[i] + side * profile[k].x + up * profile[k].y );
		}

		var around = new float[m + 1];

		for ( var k = 0; k < m; k++ )
			around[k + 1] = around[k] + (profile[(k + 1) % m] - profile[k]).Length;

		var v = 0f;

		for ( var i = 0; i < n - 1; i++ )
		{
			var v1 = v + (path[i + 1] - path[i]).Length / perimeter;

			for ( var k = 0; k < m; k++ )
			{
				var k1 = (k + 1) % m;
				var mid = (into.Positions[rings[i][k]] + into.Positions[rings[i][k1]]) * 0.5f;
				var uA = around[k] / perimeter;
				var uB = around[k + 1] / perimeter;
				DetailShapes.Face( into, new[] { rings[i][k], rings[i][k1], rings[i + 1][k1], rings[i + 1][k] }, mid - path[i], slot,
					new[] { new Vec2( uA, v ), new Vec2( uB, v ), new Vec2( uB, v1 ), new Vec2( uA, v1 ) } );
			}

			v = v1;
		}

		if ( !caps )
			return;

		DetailShapes.Face( into, rings[0], -frames[0].T, slot );
		DetailShapes.Face( into, rings[n - 1], frames[n - 1].T, slot );
	}
}

/// <summary>Turns a knot's drawn points into evenly spaced cord nodes.</summary>
public static class CordSampler
{
	/// <summary>
	/// Smooth through the points (centripetal Catmull-Rom, as Effigy's splines) and resample at
	/// <paramref name="spacing"/>. A node between two hidden points is hidden; a node in the core
	/// if either neighbour point is; a pin only lands exactly on a pinned point.
	/// </summary>
	public static CordLine Sample( IReadOnlyList<CordPoint> pts, Func<Vec3, Vec3> toWorld, Vec3 up, float spacing )
	{
		const int per = 10;
		var world = pts.Select( p => toWorld( p.P ) ).ToList();
		var dense = new Spline3 { Points = world, Smooth = true }.Sample( per );
		var segOf = new List<float>( dense.Count );

		for ( var k = 0; k < dense.Count; k++ )
			segOf.Add( Math.Min( k / (float)per, pts.Count - 1 ) );

		var line = new CordLine();
		var travelled = 0f;
		var next = 0f;

		for ( var k = 0; k < dense.Count; k++ )
		{
			var seg = segOf[k];
			var i = Math.Min( (int)seg, pts.Count - 2 );
			var exact = MathF.Abs( seg - MathF.Round( seg ) ) < 1e-4f ? (int)MathF.Round( seg ) : -1;

			if ( k > 0 )
				travelled += (dense[k] - dense[k - 1]).Length;

			// Always keep the drawn points themselves (pins must land exactly), and evenly spaced
			// nodes between them.
			if ( exact < 0 && travelled < next && k != dense.Count - 1 )
				continue;

			next = travelled + spacing;
			var hidden = exact >= 0 ? pts[exact].Hidden : pts[i].Hidden && pts[i + 1].Hidden;
			var pin = exact >= 0 && pts[exact].Pin;
			var core = exact >= 0 ? pts[exact].Core : pts[i].Core || pts[i + 1].Core;

			if ( line.Count > 0 && (dense[k] - line.P[^1]).LengthSquared < spacing * spacing * 0.04f )
			{
				// Too close to the last node: keep the stronger flags on it rather than doubling up.
				line.Pin[^1] |= pin;
				line.Hidden[^1] |= hidden;
				line.Core[^1] |= core;
				continue;
			}

			line.P.Add( dense[k] );
			line.Up.Add( up );
			line.Pin.Add( pin || hidden );
			line.Hidden.Add( hidden );
			line.Core.Add( core );
		}

		return line;
	}
}
