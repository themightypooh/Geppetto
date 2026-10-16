using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Part studies: each of the user's anchor parts (references/picks in the organic-modeling skill)
/// rebuilt alone, so a recipe can be judged against its original before it goes on a creature.
/// Inches, z up, front is +X.
/// </summary>
public static partial class CreatureGen
{
	static Sdf PartStudy( string name, Options o ) => name switch
	{
		"study_sacs" => StudySacs( o ),
		"study_fangneck" => StudyFangNeck( o ),
		"study_veinskin" => StudyVeinSkin( o ),
		"study_gums" => StudyGums( o ),
		_ => throw new ArgumentException( $"No part study called '{name}'" ),
	};

	// --- shared -----------------------------------------------------------------------------

	static Vec3 Gradient( Sdf f, Vec3 p, float h = 0.05f ) => new Vec3(
		f.Eval( p + new Vec3( h, 0, 0 ) ) - f.Eval( p - new Vec3( h, 0, 0 ) ),
		f.Eval( p + new Vec3( 0, h, 0 ) ) - f.Eval( p - new Vec3( 0, h, 0 ) ),
		f.Eval( p + new Vec3( 0, 0, h ) ) - f.Eval( p - new Vec3( 0, 0, h ) ) ).Normal;

	/// <summary>Pull a point onto the zero surface of <paramref name="f"/> (a few Newton steps).</summary>
	static Vec3 ToSurface( Sdf f, Vec3 p )
	{
		for ( var i = 0; i < 6; i++ )
			p -= Gradient( f, p ) * f.Eval( p );
		return p;
	}

	static Vec3 Perp( Vec3 d ) => (MathF.Abs( d.z ) < 0.9f ? Vec3.Cross( d, new Vec3( 0, 0, 1 ) ) : Vec3.Cross( d, new Vec3( 1, 0, 0 ) )).Normal;

	/// <summary>
	/// A tooth or fang as its own small mesh, so it keeps a sharp tip and a gap to its neighbours
	/// (the voxel skin can't hold a 0.05 in tip). Rings of <paramref name="sides"/> from the base
	/// along <paramref name="dir"/>, bending by <paramref name="curl"/> radians toward
	/// <paramref name="bendTo"/> over its length, radius easing from baseR to tipR, then a point.
	/// </summary>
	static void Fang( Vec3 root, Vec3 dir, Vec3 bendTo, float length, float baseR, float tipR, float curl, int material, string tag, int sides = 8, int rings = 7, float flatten = 0.8f, float hookFrom = 0f )
	{
		dir = dir.Normal;
		var pts = new List<Vec3>();
		var faces = new List<Face>();
		var p = root;
		var d = dir;
		var bendAxis = Vec3.Cross( dir, bendTo ).Normal;
		if ( bendAxis.Length < 0.5f ) bendAxis = Perp( dir );
		var step = length / rings;
		for ( var r = 0; r < rings; r++ )
		{
			var t = r / (float)rings;
			// Wider at the root, then a long taper: a cone with a slight belly, not a straight spike.
			var rad = tipR + (baseR - tipR) * MathF.Pow( 1f - t, 1.35f ) * (1f + 0.12f * MathF.Sin( MathF.PI * t ));
			var u = Perp( d );
			var v = Vec3.Cross( d, u ).Normal;
			for ( var s = 0; s < sides; s++ )
			{
				var a = 2 * MathF.PI * s / sides;
				// Slightly flattened side to side, like a real canine.
				pts.Add( p + u * (MathF.Cos( a ) * rad) + v * (MathF.Sin( a ) * rad * flatten) );
			}
			p += d * step;
			// The bend can be held back to the tip (hookFrom 0.6: a straight shank and a hooked tip, not a sickle).
			if ( t >= hookFrom )
				d = Xform.Rotate( bendAxis, curl / MathF.Max( 1f, rings * (1f - hookFrom) ) ).TransformDirection( d ).Normal;
		}
		var tip = pts.Count;
		pts.Add( p );
		var baseCentre = pts.Count;
		pts.Add( root - dir * 0.05f );
		for ( var r = 0; r < rings - 1; r++ )
			for ( var s = 0; s < sides; s++ )
			{
				var a = r * sides + s;
				var b = r * sides + (s + 1) % sides;
				faces.Add( new Face( new[] { a, b, b + sides, a + sides } ) );
			}
		var last = (rings - 1) * sides;
		for ( var s = 0; s < sides; s++ )
		{
			faces.Add( new Face( new[] { last + s, last + (s + 1) % sides, tip } ) );
			faces.Add( new Face( new[] { (s + 1) % sides, s, baseCentre } ) );
		}
		MeshParts.Add( new MeshPart { Mesh = new PolyMesh( pts, faces ), Material = material, Tag = tag } );
	}

	/// <summary>
	/// Space colonization (Runions et al. 2005/2007) on the zero surface of <paramref name="skin"/>:
	/// a vein or root tree grows from <paramref name="roots"/> toward attractor points scattered on
	/// the skin, each attractor pulling its nearest node within <paramref name="influence"/>, and dying
	/// once a node comes within <paramref name="kill"/>. Coverage comes out even and branching natural,
	/// which random walkers can't promise. Radii follow the pipe model: tips are
	/// <paramref name="tipR"/>, and at each fork r^γ of the parent = the sum over children.
	/// Returns the tree cut into chains (a chain runs until a fork), each with per-point radii.
	/// </summary>
	static List<(List<Vec3> P, List<float> R)> SurfaceColonize( Sdf skin, List<Vec3> attractors, List<Vec3> roots, Vec3 bias,
		float step = 0.6f, float influence = 5f, float kill = 1.4f, float tipR = 0.14f, float gamma = 2.6f, int maxIter = 300 )
	{
		var pos = new List<Vec3>();
		var parent = new List<int>();
		// Nodes in a hash grid one influence radius across, so a fine pass with thousands of attractors
		// only looks at the 27 cells around each one.
		var cellSize = MathF.Max( influence, kill );
		var grid = new Dictionary<(int, int, int), List<int>>();
		(int, int, int) Cell( Vec3 p ) => ((int)MathF.Floor( p.x / cellSize ), (int)MathF.Floor( p.y / cellSize ), (int)MathF.Floor( p.z / cellSize ));
		void AddNode( Vec3 p, int par )
		{
			pos.Add( p );
			parent.Add( par );
			var c = Cell( p );
			if ( !grid.TryGetValue( c, out var list ) ) grid[c] = list = new List<int>();
			list.Add( pos.Count - 1 );
		}
		foreach ( var r in roots ) AddNode( r, -1 );
		var alive = attractors.ToList();
		for ( var iter = 0; iter < maxIter && alive.Count > 0; iter++ )
		{
			var pull = new Dictionary<int, Vec3>();
			var dead = new bool[alive.Count];
			for ( var ai = 0; ai < alive.Count; ai++ )
			{
				var a = alive[ai];
				var best = -1;
				var bd = influence * influence;
				var (cx, cy, cz) = Cell( a );
				for ( var dx = -1; dx <= 1; dx++ )
					for ( var dy = -1; dy <= 1; dy++ )
						for ( var dz = -1; dz <= 1; dz++ )
						{
							if ( !grid.TryGetValue( (cx + dx, cy + dy, cz + dz), out var list ) ) continue;
							foreach ( var i in list )
							{
								var d2 = (pos[i] - a).LengthSquared;
								if ( d2 < bd ) { bd = d2; best = i; }
							}
						}
				if ( best < 0 ) continue;
				// Reached: this attractor is used up.
				if ( bd < kill * kill ) { dead[ai] = true; continue; }
				pull[best] = (pull.TryGetValue( best, out var v ) ? v : Vec3.Zero) + (a - pos[best]).Normal;
			}
			alive = alive.Where( ( _, ai ) => !dead[ai] ).ToList();
			if ( pull.Count == 0 && !dead.Any( d => d ) ) break;
			foreach ( var (i, v) in pull )
			{
				var n = Gradient( skin, pos[i] );
				var dir = v.Normal + bias;
				dir -= n * Vec3.Dot( dir, n );
				if ( dir.LengthSquared < 1e-6f ) continue;
				var q = ToSurface( skin, pos[i] + dir.Normal * step );
				if ( float.IsNaN( q.x ) || (q - pos[i]).Length > 2.5f * step ) continue;
				// Two attractors pulling opposite ways can stall a node; don't stack nodes on one spot.
				var dup = false;
				for ( var j = pos.Count - 1; j >= 0 && j >= pos.Count - 400; j-- )
					if ( (pos[j] - q).LengthSquared < step * step * 0.04f ) { dup = true; break; }
				if ( dup ) continue;
				AddNode( q, i );
			}
		}

		// Pipe model radii, leaves up to the roots (children always come after their parent).
		var children = new List<int>[pos.Count];
		for ( var i = 0; i < pos.Count; i++ ) children[i] = new List<int>();
		for ( var i = 0; i < pos.Count; i++ ) if ( parent[i] >= 0 ) children[parent[i]].Add( i );
		var flow = new float[pos.Count];
		for ( var i = pos.Count - 1; i >= 0; i-- )
			flow[i] = children[i].Count == 0 ? MathF.Pow( tipR, gamma ) : children[i].Sum( c => flow[c] );
		float Rad( int i ) => MathF.Pow( flow[i], 1f / gamma );

		// Cut into chains: from each root and each fork child, follow the fattest child until a leaf.
		var chains = new List<(List<Vec3>, List<float>)>();
		var starts = new Stack<int>();
		for ( var i = 0; i < roots.Count; i++ ) starts.Push( i );
		while ( starts.Count > 0 )
		{
			var s = starts.Pop();
			var pts = new List<Vec3>();
			var rads = new List<float>();
			if ( parent[s] >= 0 ) { pts.Add( pos[parent[s]] ); rads.Add( Rad( s ) ); }
			var c = s;
			while ( true )
			{
				pts.Add( pos[c] );
				rads.Add( Rad( c ) );
				if ( children[c].Count == 0 ) break;
				var next = children[c].OrderByDescending( k => flow[k] ).First();
				foreach ( var k in children[c] ) if ( k != next ) starts.Push( k );
				c = next;
			}
			if ( pts.Count >= 3 ) chains.Add( (pts, rads) );
		}
		return chains;
	}

	// --- 1. flesh sacs with vein cords and roots (anchor: Living Flesh Blob) ---------------------

	/// <summary>
	/// A 30 in sac cluster. The anchor's read: about 30 sacs at a 4:1 size spread, packed so every
	/// contact is a sharp crease, raised vein cords (0.6–1 in) running over and down between them
	/// and on into the roots, and a splay of roots that differ in length and thickness.
	/// </summary>
	static Sdf StudySacs( Options o )
	{
		var rng = new Random( o.Seed );
		var sacs = new SdfBlend( o.Get( "sacK", 0.6f ) );
		// The same sacs blended very soft. Cords take their heading from this surface, so the big
		// course of a cord ignores single sacs (it doesn't trace their rims); they still sit on the real skin.
		var proxy = new SdfBlend( o.Get( "proxyK", 2.5f ) );
		void Add( Sdf s ) { sacs.Add( s ); proxy.Add( s ); }

		// The core: a wide head (widest about two-thirds up) over a neck about 70% as wide, where the cords
		// gather, flaring out again into a broad, lumpy base that runs into the roots. Sacs are pushed out
		// of it, so it reads as one mass that has grown sacs, not a pile of balls.
		var upper = new SdfEllipsoid( new Vec3( 0, 0, 19.5f ), new Vec3( o.Get( "headR", 9f ), o.Get( "headR", 9f ) * 0.94f, 12f ), Xform.Rotate( new Vec3( 1, 0, 0 ), 0.14f ) ) { Material = FleshMat, Tag = "core" };
		var lower = new SdfEllipsoid( new Vec3( 0, 0, 8f ), new Vec3( o.Get( "neckR", 6.4f ), o.Get( "neckR", 6.4f ) * 0.94f, 8f ) ) { Material = FleshMat, Tag = "core" };
		var core = new SdfBlend( 5f, upper, lower );
		core.Prepare( 0f );
		Add( upper );
		Add( lower );
		// The bottom half draws from its own random stream, so changing it leaves the head as it is.
		var rngLow = new Random( o.Seed + 101 );
		var flare = new SdfEllipsoid( new Vec3( 0, 0, 2f ), new Vec3( o.Get( "flareR", 7.5f ), o.Get( "flareR", 7.5f ) * 0.94f, 3.2f ) ) { Material = FleshMat, Tag = "core" };
		var coreLow = new SdfBlend( 4.2f, upper, lower, flare );
		coreLow.Prepare( 0f );
		Add( flare );
		// One big smooth crown sac on top: the anchor's largest form.
		Add( new SdfEllipsoid( new Vec3( 0.5f, -0.5f, 26f ), new Vec3( 6.5f, 6f, 4.5f ) ) { Material = FleshMat, Tag = "crown" } );
		var placed = new List<(Vec3 C, float R)> { (new Vec3( 0.5f, -0.5f, 26f ), 5.5f) };
		bool PlaceSac( float r, float minZ, float maxZ, float showLo, float showHi, Random rr = null, Sdf on = null )
		{
			rr ??= rng;
			on ??= core;
			for ( var attempt = 0; attempt < 600; attempt++ )
			{
				var dir = RandDir( rr );
				// Low down, probe from inside: the blend is only a distance near its surface.
				var reach = ReferenceEquals( on, core ) ? 9f : 3f;
				var probe = new Vec3( dir.x * reach, dir.y * reach, Rand( rr, minZ, maxZ ) );
				var s0 = ToSurface( on, probe );
				var n = Gradient( on, s0 );
				// Centred under the skin so only part of each sac shows.
				var show = Rand( rr, showLo, showHi );
				var c = s0 - n * (r * (1f - 2f * show) * 0.8f);
				var ok = true;
				foreach ( var (pc, pr) in placed )
				{
					if ( (pc - c).Length < o.Get( "sacGap", 0.62f ) * (pr + r) ) { ok = false; break; }
					// No rows: a same-size neighbour at the same height reads as a band of grapes.
					if ( MathF.Abs( pc.z - c.z ) < 2f && MathF.Abs( pr - r ) < 1f && (pc - c).Length < 1.6f * (pr + r) ) { ok = false; break; }
				}
				if ( !ok ) continue;
				placed.Add( (c, r) );
				var u = Perp( n );
				var rot = new Xform( u, Vec3.Cross( n, u ).Normal, n, Vec3.Zero ) * Xform.Rotate( new Vec3( 0, 0, 1 ), Rand( rr, 0f, 3f ) );
				// Squashed along the normal so neighbours press flat and every contact is a crease.
				Add( new SdfEllipsoid( c, new Vec3( r * Rand( rr, 0.95f, 1.2f ), r * Rand( rr, 0.85f, 1.05f ), r * Rand( rr, 0.62f, 0.8f ) ), rot ) { Material = FleshMat, Tag = "sac" } );
				return true;
			}
			return false;
		}
		// Head: many medium lobes make it lumpy, rather than a few big ones on a smooth egg.
		for ( var i = 0; i < (int)o.Get( "sacs", 16 ); i++ )
		{
			var u = (float)rng.NextDouble();
			PlaceSac( 2.5f + (o.Get( "sacMax", 5f ) - 2.5f) * u, 12f, 28f, 0.22f, 0.4f );
		}
		// Small sub-sacs high up, so the top isn't a bald dome.
		for ( var i = 0; i < (int)o.Get( "topSacs", 5 ); i++ )
			PlaceSac( Rand( rng, 1.2f, 2.2f ), 18f, 29f, 0.3f, 0.5f );
		// Lower body: smaller rounder swellings, 1.6–3 in, down to the roots.
		for ( var i = 0; i < (int)o.Get( "lowSacs", 14 ); i++ )
			PlaceSac( Rand( rng, 1.6f, 3f ), 2f, 12f, 0.2f, 0.38f );
		// Neck and base: more half-buried swellings, so the bottom is as lumpy as the anchor's, down onto
		// the flare where the roots leave.
		for ( var i = 0; i < (int)o.Get( "neckSacs", 14 ); i++ )
			PlaceSac( Rand( rngLow, 1.8f, 2.8f ), 3f, 11f, 0.45f, 0.55f, rngLow, coreLow );
		// A few small lumps on the flare's rim, so it doesn't read as a skirt.
		for ( var i = 0; i < 3; i++ )
			PlaceSac( Rand( rngLow, 1.2f, 1.6f ), 1.5f, 4f, 0.4f, 0.5f, rngLow, coreLow );
		sacs.Prepare( 0f );
		proxy.Prepare( 0f );
		Console.WriteLine( $"  sacs: {placed.Count} placed" );

		// Vein cords: a climbing root system grown by space colonization over the skin, from trunks
		// low on the base toward attractors scattered over the whole body up to the crown. Pipe-model
		// radii make the trunks fat and the tips fine. Each chain is smoothed, laid on the skin
		// half-embedded (dipping under now and then), and pulled taut so it spans creases.
		var cords = new SdfBlend( 0.15f );
		var trunkStarts = new List<(Vec3 P, Vec3 Out, float R)>();
		var trunks = (int)o.Get( "trunks", 10 );
		var seeds = new List<Vec3>();
		for ( var i = 0; i < trunks; i++ )
		{
			var a = 2 * MathF.PI * i / trunks + Rand( rng, -0.25f, 0.25f );
			var outward = new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 );
			// Seed from inside the trunk: the blend is only a distance near its surface.
			seeds.Add( ToSurface( sacs, outward * 4f + new Vec3( 0, 0, Rand( rng, 1.5f, 3f ) ) ) );
		}
		var attract = new List<Vec3>();
		for ( var i = 0; i < (int)o.Get( "attractors", 750 ); i++ )
{
	var dir = RandDir( rng );
	// A quarter on the top so the crown gets its share, a fifth low down where the anchor is densest.
	var band = rng.NextDouble();
	var z = band < 0.25 ? Rand( rng, 20f, 31f ) : band < 0.45 ? Rand( rng, 4f, 14f ) : Rand( rng, 4f, 30f );
			var probe = z > 27f ? new Vec3( 0, 0, 22f ) + dir * 3f : new Vec3( dir.x * 3f, dir.y * 3f, z );
			var q = ToSurface( sacs, probe );
			if ( float.IsNaN( q.x ) || MathF.Abs( sacs.Eval( q ) ) >= 0.1f ) continue;
			// Veins bunch in the creases: keep every attractor in a hollow, half of those on lobe tops.
			var convex = sacs.Eval( q + Gradient( sacs, q ) * 1f ) > 0.95f;
			if ( convex && rng.NextDouble() < 0.5 ) continue;
			attract.Add( q );
		}
		var chains = SurfaceColonize( sacs, attract, seeds, new Vec3( 0, 0, o.Get( "climb", 0.05f ) ),
			step: 0.6f, influence: o.Get( "influence", 5f ), kill: o.Get( "kill", 1.2f ), tipR: o.Get( "tipR", 0.17f ), gamma: o.Get( "gamma", 2.6f ) );
		var rScale = o.Get( "cordScale", 1f );
		var rMax = o.Get( "cordMax", 0.7f );
		var rMin = o.Get( "cordMin", 0.28f );
		// Pass 1: each colonization chain smoothed and given a slow sideways meander.
		var runs = new List<(List<Vec3> P, List<float> R, bool Joined)>();
		foreach ( var (cp, cr) in chains )
		{
			// Stubs read as twigs stuck on: skip short side chains.
			var chainLen = 0f;
			for ( var k = 1; k < cp.Count; k++ ) chainLen += (cp[k] - cp[k - 1]).Length;
			if ( chainLen < o.Get( "minChain", 2.5f ) ) continue;
			var pts = cp.ToList();
			for ( var it = 0; it < 4; it++ )
				for ( var k = 1; k < pts.Count - 1; k++ )
					pts[k] = ToSurface( sacs, pts[k] * 0.5f + (pts[k - 1] + pts[k + 1]) * 0.25f );
			var mPhase = Rand( rng, 0f, 6.3f );
			var mPeriod = Rand( rng, 3f, 6f );
			var mAmp = o.Get( "meander", 0.7f );
			var arc = 0f;
			var bent = pts.ToList();
			for ( var k = 1; k < pts.Count - 1; k++ )
			{
				arc += (pts[k] - pts[k - 1]).Length;
				var along = (pts[k + 1] - pts[k - 1]).Normal;
				var side = Vec3.Cross( Gradient( sacs, pts[k] ), along ).Normal;
				// Pinned at the chain's start so forks stay attached.
				var w = MathF.Min( 1f, arc / 2f );
				bent[k] = ToSurface( sacs, pts[k] + side * (mAmp * w * MathF.Sin( arc / mPeriod * 2f * MathF.PI + mPhase )) );
			}
			runs.Add( (bent, cr.Select( r => Math.Clamp( r * rScale, rMin, rMax ) ).ToList(), false) );
		}

		// Pass 2: make it a net, not a tree. The anchor's veins fuse back into each other (a tip runs on
		// into the nearest cord ahead of it) and braid (a thinner companion splits off, runs alongside
		// and rejoins), so the read is loops and lenses, not clean Y-forks fanning upward.
		List<Vec3> SurfacePath( Vec3 a, Vec3 b )
		{
			var n = Math.Max( 2, (int)((b - a).Length / 0.5f) );
			var path = new List<Vec3>();
			for ( var s = 1; s <= n; s++ ) path.Add( ToSurface( sacs, a + (b - a) * (s / (float)n) ) );
			return path;
		}
		var joins = 0;
		var tipRuns = runs.Count;
		for ( var i = 0; i < tipRuns; i++ )
		{
			if ( rng.NextDouble() > o.Get( "fuse", 0.75f ) ) continue;
			var (pts, rads, _) = runs[i];
			if ( pts.Count < 4 ) continue;
			var tip = pts[^1];
			var ahead = (pts[^1] - pts[^4]).Normal;
			Vec3? best = null;
			var bd = o.Get( "fuseReach", 3.5f );
			for ( var j = 0; j < tipRuns; j++ )
			{
				if ( j == i ) continue;
				foreach ( var q in runs[j].P )
				{
					var dq = q - tip;
					var dl = dq.Length;
					// Ahead of the tip (within ~70°), not back onto the cord it forked from.
					if ( dl < 0.8f || dl >= bd || Vec3.Dot( dq / dl, ahead ) < 0.35f ) continue;
					if ( (q - pts[0]).Length < 1.5f ) continue;
					bd = dl;
					best = q;
				}
			}
			if ( best is not Vec3 target ) continue;
			var r0 = rads[^1];
			foreach ( var q in SurfacePath( tip, target ) )
			{
				pts.Add( q );
				rads.Add( r0 );
			}
			runs[i] = (pts, rads, true);
			joins++;
		}
		var braids = 0;
		for ( var i = 0; i < tipRuns; i++ )
		{
			var (pts, rads, _) = runs[i];
			if ( pts.Count < 12 || rng.NextDouble() > o.Get( "braid", 0.3f ) ) continue;
			var len = (int)(Rand( rng, 3f, 6.5f ) / 0.6f);
			if ( len >= pts.Count - 2 ) continue;
			var s0 = rng.Next( 1, pts.Count - len - 1 );
			var width = Rand( rng, 0.3f, 0.7f ) * (rng.Next( 2 ) == 0 ? 1 : -1);
			// Some companions cross over their parent once instead of running beside it.
			var crosses = rng.NextDouble() < 0.3;
			var comp = new List<Vec3>();
			var compR = new List<float>();
			for ( var k = s0; k <= s0 + len; k++ )
			{
				var u = (k - s0) / (float)len;
				var along = (pts[Math.Min( k + 1, pts.Count - 1 )] - pts[k - 1]).Normal;
				var side = Vec3.Cross( Gradient( sacs, pts[k] ), along ).Normal;
				comp.Add( ToSurface( sacs, pts[k] + side * (width * MathF.Sin( (crosses ? 2f : 1f) * MathF.PI * u )) ) );
				compR.Add( MathF.Max( rMin * 0.8f, rads[k] * 0.5f ) );
			}
			runs.Add( (comp, compR, true) );
			braids++;
		}

		// Fine tier: capillaries, 0.06–0.16 in, grown by a second, fine colonization out of the cords
		// toward attractors in broad patches (about half the skin stays quiet: detail spread evenly
		// reads as noise). Only when the mesh can hold them (voxel 0.07 or finer, ~3 voxels across).
		var caps = new SdfBlend( 0.05f );
		var fine = o.Get( "fine", o.Voxel <= 0.07f ? 1f : 0f ) > 0f;
		// Capillaries off by default: as geometry they read as loose strokes stuck on the skin (judge r7,
		// and the user). Kept behind caps=1 for a normal-map bake later.
		var withCaps = fine && o.Get( "caps", 0f ) > 0f;
		if ( withCaps )
		{
			var patch = new Noise3( o.Seed + 11 );
			var capSeeds = new List<Vec3>();
			foreach ( var (rp, _, _) in runs )
				for ( var k = 2; k < rp.Count; k += 2 ) capSeeds.Add( rp[k] );
			var capAttract = new List<Vec3>();
			for ( var i = 0; i < (int)o.Get( "capAttractors", 4500 ); i++ )
			{
				var dir = RandDir( rng );
				var z = Rand( rng, 3f, 31f );
				var probe = z > 27f ? new Vec3( 0, 0, 22f ) + dir * 3f : new Vec3( dir.x * 3f, dir.y * 3f, z );
				var q = ToSurface( sacs, probe );
				if ( float.IsNaN( q.x ) || MathF.Abs( sacs.Eval( q ) ) >= 0.05f ) continue;
				if ( patch.Fbm( q * 0.09f, 2 ) < o.Get( "capQuiet", -0.15f ) ) continue;
				// Fine veins grow off the cords: only within 2.5 in of one, and mostly off the lobe tops.
				var nearCord = false;
				foreach ( var sp in capSeeds ) if ( (sp - q).LengthSquared < 6.25f ) { nearCord = true; break; }
				if ( !nearCord ) continue;
				if ( sacs.Eval( q + Gradient( sacs, q ) * 1f ) > 0.95f && rng.NextDouble() < 0.6 ) continue;
				capAttract.Add( q );
			}
			var capChains = SurfaceColonize( sacs, capAttract, capSeeds, Vec3.Zero, step: 0.3f, influence: 2.2f, kill: 0.4f, tipR: 0.05f, gamma: 2.4f );
			var capCount = 0;
			foreach ( var (cp, cr) in capChains )
			{
				if ( cp.Count < 4 ) continue;
				var pts = cp.ToList();
				for ( var it = 0; it < 3; it++ )
					for ( var k = 1; k < pts.Count - 1; k++ )
						pts[k] = ToSurface( sacs, pts[k] * 0.5f + (pts[k - 1] + pts[k + 1]) * 0.25f );
				// Starts as a branch of its cord and tapers out to a thread that fades into the skin.
				var rads = cr.Select( ( r, k ) => Math.Clamp( r * 1.2f, 0.06f, 0.16f ) * (0.3f + 0.7f * MathF.Min( 1f, (cr.Count - 1 - k) * 0.3f / 1.2f )) ).ToList();
				for ( var k = 0; k < pts.Count; k++ )
				{
					var fromEnd = (pts.Count - 1 - k) * 0.3f;
					pts[k] += Gradient( sacs, pts[k] ) * (rads[k] * (0.25f - 0.5f * MathF.Max( 0f, 1f - fromEnd / 0.8f )));
				}
				caps.Add( new SdfTube( pts, rads ) { Material = GumMat, Tag = "capillary" } );
				capCount++;
			}
			Console.WriteLine( $"  capillaries: {capCount} from {capSeeds.Count} seeds, {capAttract.Count} attractors" );
		}

		// Pass 3: lay each run on the skin, half-embedded and dipping under now and then; a free end
		// tapers and sinks, a fused one doesn't. Then pull it taut so it spans creases.
		foreach ( var (pts, rads0, joined) in runs )
		{
			var rads = rads0.Select( ( r, k ) => joined ? r : r * (0.45f + 0.55f * MathF.Min( 1f, (rads0.Count - 1 - k) * 0.6f / 1.5f )) ).ToList();
			var depthPhase = Rand( rng, 0f, 6.3f );
			var lies = new List<float>();
			for ( var k = 0; k < pts.Count; k++ )
			{
				var dip = MathF.Sin( k * 0.6f / 2.2f + depthPhase );
				var fromEnd = (pts.Count - 1 - k) * 0.6f;
				var sink = joined ? 0f : MathF.Max( 0f, 1f - fromEnd / 1f );
				var depth = (dip < -0.7f ? 0.4f - 0.8f * ((-dip - 0.7f) / 0.3f) : 0.4f) - 0.7f * sink * sink;
				var n = Gradient( sacs, pts[k] );
				lies.Add( rads[k] * depth );
				pts[k] += n * lies[k];
			}
			// Taut string, projected both ways: never into the skin, never more than 0.3 r off it.
			for ( var it = 0; it < (int)o.Get( "taut", 6 ); it++ )
				for ( var k = 1; k < pts.Count - 1; k++ )
				{
					var q = pts[k] + ((pts[k - 1] + pts[k + 1]) * 0.5f - pts[k]) * 0.5f;
					var g = sacs.Eval( q ) - lies[k];
					var gc = Math.Clamp( g, 0f, 0.5f * rads[k] );
					q -= Gradient( sacs, q ) * (g - gc);
					if ( !float.IsNaN( q.x ) ) pts[k] = q;
					}
					cords.Add( new SdfTube( pts, rads ) { Material = GumMat, Tag = "cord" } );
		}
		for ( var i = 0; i < seeds.Count; i++ )
		{
			var trunk = chains.FirstOrDefault( c => (c.P[0] - seeds[i]).LengthSquared < 1e-4f );
			var r = trunk.R is null ? 0.4f : MathF.Min( rMax, trunk.R[0] * rScale );
			trunkStarts.Add( (seeds[i], new Vec3( seeds[i].x, seeds[i].y, 0 ).Normal, r) );
		}
		Console.WriteLine( $"  cords: {chains.Count} chains, {runs.Count} runs, {joins} fused tips, {braids} braids, {attract.Count} attractors" );

		// Roots: each cord trunk runs on down into a floor root, plus a ring of thin roots and three
		// thick buttresses. Flat on the floor, gnarled, forking, blunt ends.
		var roots = new SdfBlend( 0.6f );
		void Root( Vec3 start, Vec3 dir, float len, float r0, int i, float rStart = -1f )
		{
			// A cord running on into a root starts at the cord's own radius and swells to the root's over
			// the first quarter, so the two read as one system instead of a root plugged into the trunk.
			var swell = rStart > 0f ? rStart : r0;
			// About a third arch up over their neighbours partway along, so they don't all lie flat as spokes.
			var arch = rng.NextDouble() < 0.3 ? Rand( rng, 0.4f, 0.7f ) * r0 : 0f;
			var pts = new List<Vec3>();
			var rads = new List<float>();
			var segs = 14;
			var bendSide = Rand( rng, -0.8f, 0.8f );
			// About a third of the roots curl their tip up off the floor.
			// No curled-up tips: at this scale any rise at the end reads as a peg standing on the floor.
			var upturn = o.Get( "upturn", 0f ) * r0;
			var a = MathF.Atan2( dir.y, dir.x );
			for ( var s = 0; s <= segs; s++ )
			{
				var t = s / (float)segs;
				var outward = dir * (len * t) + Perp( dir ) * (bendSide * len * t * t);
				var z = MathF.Max( r0 * 0.35f, start.z * (1f - MathF.Pow( t, 0.45f )) );
				var kink = Perp( dir ) * (0.35f * r0 * MathF.Sin( 9f * t + a * 3f )) + new Vec3( 0, 0, 0.25f * r0 * MathF.Sin( 13f * t + a ) * (1f - t) );
				var archZ = arch * MathF.Max( 0f, MathF.Sin( MathF.PI * (t - 0.25f) / 0.5f ) ) * (t > 0.25f && t < 0.75f ? 1f : 0f);
				pts.Add( new Vec3( start.x, start.y, 0 ) + outward + kink + new Vec3( 0, 0, z + archZ + upturn * MathF.Max( 0f, (t - 0.7f) / 0.3f ) * MathF.Max( 0f, (t - 0.7f) / 0.3f ) ) );
				// Gnarled: slow swellings along the length, tapering to a point (0.15 r0), not a blunt sausage.
				var body = r0 * (0.15f + 0.85f * MathF.Pow( 1f - t, 1.15f )) * (1f + 0.14f * MathF.Sin( 7f * t + i * 1.7f ) + 0.06f * MathF.Sin( 19f * t + i ));
				var grow = t < 0.25f ? swell + (body - swell) * (t / 0.25f) * (t / 0.25f) * (3f - 2f * (t / 0.25f)) : body;
				rads.Add( MathF.Max( 0.12f, grow ) );
			}
			roots.Add( new SdfTube( pts, rads ) { Material = SkinMat, Tag = "root" } );
			// A side root partway along, not a fork at the tip.
			if ( len > 7f && rng.NextDouble() < o.Get( "rootSide", 0.4f ) )
			{
				var at = (int)(segs * Rand( rng, 0.3f, 0.6f ));
				var forkAt = pts[at];
				var fd = Xform.Rotate( new Vec3( 0, 0, 1 ), Rand( rng, 0.6f, 0.96f ) * (rng.Next( 2 ) == 0 ? 1 : -1) ).TransformDirection( dir );
				var fl = len * Rand( rng, 0.3f, 0.5f );
				roots.Add( new SdfTube( new[] { forkAt, forkAt + fd * (fl * 0.5f) + new Vec3( 0, 0, -forkAt.z * 0.5f ), forkAt + fd * fl - new Vec3( 0, 0, forkAt.z - rads[at] * 0.3f ) },
					new[] { rads[at] * 0.5f, MathF.Max( 0.15f, rads[at] * 0.35f ), 0.12f } ) { Material = SkinMat, Tag = "root" } );
			}
		}
		var e = 0;
		// Every trunk cord runs on down into a floor root, swelling from its own radius.
		foreach ( var (tp, tout, tr) in trunkStarts )
			Root( tp, (tout + Perp( tout ) * Rand( rng, -0.3f, 0.3f )).Normal, Rand( rng, 11f, 18f ), Rand( rng, 1.1f, 1.8f ), e++, tr );
		// The flare's own roots: thick where they leave the base, spread all round it.
		var ring = (int)o.Get( "roots", 12 );
		for ( var i = 0; i < ring; i++ )
		{
			// Jittered ±15° and in length, so they don't read as an even ring of spokes.
			var a = 2 * MathF.PI * (i + 0.5f) / ring + Rand( rng, -0.26f, 0.26f );
			var dir = new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 );
			Root( ToSurface( sacs, new Vec3( 0, 0, Rand( rng, 3f, 6f ) ) + dir * 4f ), dir, Rand( rng, o.Get( "rootMin", 7f ), o.Get( "rootMax", 18f ) ), Rand( rng, 1.1f, 1.9f ), 30 + i );
		}
		for ( var i = 0; i < 3; i++ )
		{
			var a = 2 * MathF.PI * i / 3 + Rand( rng, -0.4f, 0.4f ) + 0.5f;
			var dir = new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 );
			Root( ToSurface( sacs, new Vec3( 0, 0, Rand( rng, 3f, 5f ) ) + dir * 4f ), dir, Rand( rng, 8f, 12f ), 2.1f, 50 + i );
		}

		var body = withCaps
			? new SdfBlend( o.Get( "cordK", 0.18f ), new SdfDisplace( sacs, 0.12f, 0.6f, 2, o.Seed ), cords, caps )
			: new SdfBlend( o.Get( "cordK", 0.18f ), new SdfDisplace( sacs, 0.12f, 0.6f, 2, o.Seed ), cords );

		// Surface pass on the mesh: each sac is its own part, so seams land in the creases between them.
		// Not against the core: those seams sit in deep crevices, and the roll folds over itself there.
		// Each crease gets a pinch (a grown seam: groove plus a small roll of the upper sac's skin) and
		// two or three compression wrinkles running along it; then fine grain on about half the skin.
		if ( fine && o.Get( "surface", 1f ) > 0f )
			Detail = ( mesh, nearest ) =>
			{
				var ids = new Dictionary<Sdf, int>( ReferenceEqualityComparer.Instance );
				var part = nearest.Select( l => l is not null && l.Tag is "sac" or "crown" ? (ids.TryGetValue( l, out var k ) ? k : ids[l] = ids.Count) : -1 ).ToArray();
				var nb = new HashSet<int>[mesh.VertexCount];
				for ( var v = 0; v < nb.Length; v++ ) nb[v] = new HashSet<int>();
				foreach ( var f in mesh.Faces )
					for ( var c = 0; c < f.Count; c++ )
					{
						nb[f.Indices[c]].Add( f.Indices[(c + 1) % f.Count] );
						nb[f.Indices[(c + 1) % f.Count]].Add( f.Indices[c] );
					}
				// Where two leaves are nearly tied the label flickers vertex to vertex, and every flicker becomes a
				// tiny seam that tears. A majority filter makes the part boundaries clean lines.
				for ( var pass = 0; pass < 3; pass++ )
					part = part.Select( ( l, v ) => nb[v].Count == 0 ? l : nb[v].Append( v ).GroupBy( u => part[u] ).OrderByDescending( g => g.Count() ).First().Key ).ToArray();
				var seams = SurfaceDetail.Seams( mesh, part, 3f );
				var busy = SurfaceDetail.BusyMask( mesh, 0.65f, 0.08f, o.Seed + 23 );
				// Only where there is a real crease: a part boundary on smooth skin (two sacs blended soft) would
				// get a seam carved into nothing, which reads as a cut.
				var cavity = SurfaceDetail.Cavity( mesh, 10 );
				float InCrease( int v ) => Math.Clamp( (cavity[v] - 0.02f) / 0.08f, 0f, 1f );
				SurfaceDetail.GrownSeam( mesh, seams, new SeamOptions { Depth = o.Get( "seamDepth", 0.1f ), Width = 0.3f, Roll = 0.12f, RollOffset = 0.5f, RollWidth = 0.35f, Sag = 0.5f, Variation = 0.7f, Seed = o.Seed + 21 }, InCrease );
				// Compression wrinkles only where the skin is busy, and broken: a field of them reads machined.
				if ( o.Get( "wrinkle", 0.06f ) > 0f )
					SurfaceDetail.AnchoredFolds( mesh, seams.Distance, v => seams.Other[v] >= 0 && part[v] >= 0 && busy[v] > 0.5f && InCrease( v ) > 0f,
						new AnchoredFoldOptions { Count = 2, First = 0.6f, Spacing = 0.5f, Growth = 0.4f, Decay = 0.6f, Amplitude = o.Get( "wrinkle", 0.06f ), Sag = 0.2f, Variation = 0.9f, Seed = o.Seed + 22 } );
				// Relax the seam band so nothing narrower than two voxels survives as a slit.
				for ( var it = 0; it < 2; it++ )
				{
					var next = mesh.Positions.ToArray();
					for ( var v = 0; v < next.Length; v++ )
					{
						if ( seams.Distance[v] > 0.35f || nb[v].Count == 0 ) continue;
						var m = Vec3.Zero;
						foreach ( var u in nb[v] ) m += mesh.Positions[u];
						next[v] = mesh.Positions[v] + (m / nb[v].Count - mesh.Positions[v]) * 0.5f;
					}
					for ( var v = 0; v < next.Length; v++ ) mesh.Positions[v] = next[v];
				}
				SurfaceDetail.Grain( mesh, 0.02f, 0.8f, 0.6f, o.Get( "grain", 0.007f ), o.Seed + 24, v => 0.5f + 0.5f * busy[v] );
			};
		return new SdfBlend( o.Get( "rootK", 0.45f ), body, roots );
	}

	// --- 2. skull on a fang-split neck (anchor: ManThing, shoulders up) -------------------------

	/// <summary>
	/// The ManThing from the shoulders up (anchor: picks/manthing; renders in
	/// ~/claude-handoff/studies/manthing_ref). A bare skull with a big egg cranium and a small face,
	/// no lower jaw, drooping forward and down on a long, thin neck that arcs forward from the
	/// shoulders. The whole front of the neck is split, lined with hooked teeth that point forward and
	/// down, opening into a toothed pit at the collar. A thin tongue hangs from under the skull.
	/// Inches, z up, front is +X.
	/// </summary>
	static Sdf StudyFangNeck( Options o )
	{
		var rng = new Random( o.Seed );
		// Low k: a big smooth-min inflates thin tubes (k 2.2 made the neck a sausage).
		var skin = new SdfBlend( o.Get( "skinK", 0.6f ) );

		// Neck up only (the user's scope): no shoulders. The neck simply ends at its base.

		// The neck: about 12 in along its arc, leaning forward about 40° from the shoulders and curling
		// over at the top, thin (r 1.45 → 1.05; the skull is about 3× as wide).
		var neck = new List<Vec3>();
		var neckR = new List<float>();
		var lean = o.Get( "lean", 1f );
		for ( var i = 0; i <= 16; i++ )
		{
			var t = i / 16f;
			var a = (0.42f + 0.6f * t * t) * lean;
			// Integrate a direction that tips forward as it climbs.
			if ( i == 0 ) neck.Add( new Vec3( -0.6f, 0, 54.2f ) );
			else neck.Add( neck[^1] + new Vec3( MathF.Sin( a ), 0, MathF.Cos( a ) ) * (o.Get( "neckLen", 10.6f ) / 16f) );
			// Thin all the way: about a third of the skull's width.
			neckR.Add( o.Get( "neckR", 1.15f ) - 0.12f * t );
		}
		skin.Add( new SdfTube( neck, neckR ) { Material = SkinMat, Tag = "neck" } );
		// A spine ridge down the back of the neck, so it reads as a column, not a hose.
		skin.Add( new SdfTube( neck.Select( ( p, k ) => p + Back( neck, k ) * (neckR[k] * 0.75f) ).ToList(), neckR.Select( r => r * 0.4f ).ToList() ) { Material = SkinMat, Tag = "spine" } );

		// Two tendons either side of the split, so the neck isn't a smooth sausage.
		foreach ( var side in new[] { 1f, -1f } )
			skin.Add( new SdfTube( neck.Select( ( p, k ) => p - Back( neck, k ) * (neckR[k] * 0.55f) + new Vec3( 0, side * neckR[k] * 0.72f, 0 ) ).ToList(), neckR.Select( r => 0.2f ).ToList() ) { Material = SkinMat, Tag = "tendon" } );

		// Neck frame at arc parameter s (0 base … 1 top): point, front normal, tangent.
		(Vec3 P, Vec3 N, Vec3 T, float R) NeckAt( float s )
		{
			var f = Math.Clamp( s, 0f, 1f ) * (neck.Count - 1);
			var k = Math.Min( (int)f, neck.Count - 2 );
			var u = f - k;
			var p = Vec3.Lerp( neck[k], neck[k + 1], u );
			var tan = (neck[k + 1] - neck[k]).Normal;
			var n = Vec3.Cross( new Vec3( 0, 1, 0 ), tan ).Normal;
			if ( n.x < 0 ) n = -n;
			return (p, n, tan, neckR[k] + (neckR[k + 1] - neckR[k]) * u);
		}

		// The skull, built in its own frame (+x face, z up) and pitched down about 26° at the top of the
		// neck. A big egg cranium with an occipital bulge, a small face: brow, deep horizontal eye
		// sockets, cheekbones, a nasal hole and an upper jaw with its tooth row. No mandible.
		var top = NeckAt( 1f );
		var pitch = Xform.Rotate( new Vec3( 0, 1, 0 ), o.Get( "headPitch", 0.5f ) );
		// The neck meets the back of the skull's underside, under a ledge, not its centre.
		var head = top.P + top.T * 0.9f + new Vec3( 2.0f, 0, 2.0f );
		Vec3 H( float x, float y, float z ) => head + pitch.TransformDirection( new Vec3( x, y, z ) );
		// The skull (anchor face-on: a human skull face under an oversized, smooth cranium; no mandible).
		// Built in its own frame, +x the face, z up, origin at the cranium's centre. No brow bar: the
		// forehead runs smoothly down to the orbit rims.
		var bone = new SdfBlend( o.Get( "boneK", 0.45f ) );
		bone.Add( new SdfEllipsoid( H( -0.6f, 0, 0.9f ), new Vec3( 4.3f, 3.1f, 3.4f ), pitch ) { Material = BoneMat, Tag = "cranium" } );
		// The face plate under the forehead: about 0.75 of the cranium's width.
		bone.Add( new SdfEllipsoid( H( 2.0f, 0, -2.0f ), new Vec3( 2.0f, 2.45f, 2.75f ), pitch ) { Material = BoneMat, Tag = "face" } );
		foreach ( var side in new[] { 1f, -1f } )
		{
			// Cheekbone body at the outer-lower corner of each orbit, and its arch running back toward
			// where the neck joins, standing free over a hollow.
			bone.Add( new SdfEllipsoid( H( 2.95f, 1.85f * side, -2.3f ), new Vec3( 0.7f, 0.55f, 0.6f ), pitch ) { Material = BoneMat, Tag = "cheek" } );
			// No separate arch: an arch plus the hollow under it reads as an ear from the side. The temple
			// hollow above gives the cheekbone line on its own.
		}
		// Upper jaw: a small block with a flat lower edge for the tooth row.
		// Set back under the face, not poking out as a muzzle.
		bone.Add( new SdfEllipsoid( H( 3.05f, 0, -3.85f ), new Vec3( 1.05f, 1.5f, 0.85f ), pitch ) { Material = BoneMat, Tag = "maxilla" } );
		var skullCuts = new List<Sdf>();
		foreach ( var side in new[] { 1f, -1f } )
		{
			// Big, round, deep orbits: each about a third of the face's width, dark from the front and
			// open enough from the side to see into.
			skullCuts.Add( new SdfEllipsoid( H( 3.95f, 1.2f * side, -1.0f ), new Vec3( 1.8f, o.Get( "orbitW", 1.1f ), o.Get( "orbitH", 1.05f ) ), pitch ) );
			// The orbit's outer wall cut back, so it reads as a hole from the side as well.
			skullCuts.Add( new SdfEllipsoid( H( 3.35f, 2.0f * side, -0.95f ), new Vec3( 0.95f, 0.5f, 0.62f ), pitch ) );
			// Temporal hollow above the arch, and the hollow under it, so the arch reads as a bar.
			skullCuts.Add( new SdfEllipsoid( H( 0.3f, 3.2f * side, -0.3f ), new Vec3( 1.6f, 0.6f, 1.0f ), pitch ) );

		}
		// The nasal opening: a small upside-down teardrop between and below the orbits.
		skullCuts.Add( new SdfEllipsoid( H( 4.05f, 0, -2.65f ), new Vec3( 0.75f, 0.38f, 0.6f ), pitch ) );
		// Flat underside: no mandible, the skull ends at the upper tooth line.
		skullCuts.Add( new SdfEllipsoid( H( 2.4f, 0, -6.15f ), new Vec3( 3.2f, 3.4f, 1.55f ), pitch ) );
		var skull = new SdfSubtract( bone, 0.2f, skullCuts.ToArray() );
		// The neck runs on up under the ledge into the back of the skull's underside.
		skin.Add( new SdfRoundCone( top.P, top.R, H( -1.8f, 0, -1.6f ), top.R * 1.25f ) { Material = SkinMat, Tag = "neck" } );
		// Upper teeth along the jaw's arc, pointing down and a little forward.
		for ( var i = 0; i < 10; i++ )
		{
			var a = (i + 0.5f) / 10f * MathF.PI - MathF.PI / 2;
			var root = H( 2.95f + 0.95f * MathF.Cos( a ), 1.2f * MathF.Sin( a ), -4.5f );
			var down = pitch.TransformDirection( new Vec3( 0.25f, 0, -1f ) );
			Fang( root, down, pitch.TransformDirection( new Vec3( -1, 0, 0 ) ), Rand( rng, 0.3f, 0.45f ), 0.14f, 0.04f, 0.2f, BoneMat, "tooth", sides: 8, rings: 6, flatten: 0.85f );
			}
			// Skull-only build, for iterating the head without the neck in the way.
			if ( o.Get( "skullOnly", 0f ) > 0f ) return skull;

		// The split: one continuous opening from the jaw (behind the upper teeth) down the whole front
		// of the neck. t runs down it: below 0 it climbs from the top of the neck to the jaw, so the
		// mouth and the neck's split are one feature. Open about 0.40 of the neck radius at the top,
		// narrowing to 0.25 at the base.
		var jaw = H( 2.4f, 0, -2.75f );
		var tJaw = -0.18f;
		(Vec3 Front, Vec3 N, float R) Split( float t )
		{
			if ( t >= 0f )
			{
				var f = NeckAt( 0.95f - 0.91f * t );
				return (f.P + f.N * f.R, f.N, f.R);
			}
			var f0 = NeckAt( 0.95f );
			var u = t / tJaw;
			return (Vec3.Lerp( f0.P + f0.N * f0.R, jaw, u ), f0.N, f0.R);
		}
		float Width( float t ) => Split( t ).R * (0.40f - 0.15f * Math.Clamp( t, 0f, 1f ));
		var cut = new List<Vec3>();
		var cutR = new List<float>();
		for ( var i = 0; i <= 24; i++ )
		{
			var t = tJaw + (1f - tJaw) * i / 24f;
			var f = Split( t );
			cutR.Add( Width( t ) );
			cut.Add( f.Front - f.N * (f.R * o.Get( "slitDepth", 0.55f )) );
		}
		var body = new SdfSubtract( skin, 0.2f, new SdfTube( cut, cutR ) );

		// A thin lip ridge along each edge, a little torn.
		var lips = new SdfBlend( 0.3f );
		foreach ( var side in new[] { 1f, -1f } )
		{
			var lp = new List<Vec3>();
			var lr = new List<float>();
			for ( var i = 0; i <= 24; i++ )
			{
				var t = tJaw + (1f - tJaw) * i / 24f;
				var f = Split( t );
				lp.Add( f.Front - f.N * 0.12f + new Vec3( 0, (Width( t ) + 0.08f + Rand( rng, -0.05f, 0.05f )) * side, 0 ) );
				lr.Add( 0.17f + 0.03f * MathF.Sin( 2f * MathF.PI * 4f * t + side ) );
			}
			lips.Add( new SdfTube( lp, lr ) { Material = GumMat, Tag = "slitLip" } );
		}

		// Teeth: one row of long hooks per lip, from the jaw down (about 11 per lip, 1.3–2.2 in),
		// standing off the front and bent about 25° in toward the midline so the two rows interlock;
		// the hook at the tip does the drooping. Sideways splay capped near 15°.
		var fangs = 0;
		void Tooth( Vec3 root, Vec3 dir, float len, float curl )
		{
			var jitter = Xform.Rotate( new Vec3( 1, 0, 0 ), Rand( rng, -0.2f, 0.2f ) ) * Xform.Rotate( new Vec3( 0, 1, 0 ), Rand( rng, -0.2f, 0.2f ) );
			dir = jitter.TransformDirection( dir ).Normal;
			root -= dir * 0.1f;
			Fang( root, dir, new Vec3( 0, 0, -1 ), len, len * Rand( rng, 0.17f, 0.21f ), 0.02f, curl, BoneMat, "fang", sides: 10, rings: 9, flatten: 1f, hookFrom: 0.6f );
			fangs++;
		}
		var perLip = (int)o.Get( "teethPerLip", 11 );
		foreach ( var side in new[] { 1f, -1f } )
			for ( var i = 0; i < perLip; i++ )
			{
				var t = -0.1f + (i + 0.5f + Rand( rng, -0.15f, 0.15f )) / perLip * 0.95f;
				var f = Split( t );
				var root = f.Front - f.N * 0.1f + new Vec3( 0, (Width( t ) + 0.02f) * side, 0 );
				var big = 0.55f + 0.45f * MathF.Sin( MathF.PI * Math.Clamp( (t + 0.1f) / 0.95f, 0f, 1f ) );
				var len = (0.75f + 0.75f * big) * Rand( rng, 0.85f, 1.15f ) * o.Get( "toothScale", 1.45f );
				var dir = f.N + new Vec3( 0.1f, -0.45f * side, -0.18f );
				Tooth( root, dir, len, 0.7f );
			}
		Console.WriteLine( $"  fangs: {fangs} (+10 skull teeth)" );

		// The tongue: from the top of the split under the skull, hanging about 9 in with a slight S,
		// thin and tapering to a blunt end.
		var tp = new List<Vec3>();
		var tr = new List<float>();
		// From where the split meets the jaw.
		var tf = Split( -0.05f );
		var tRoot = tf.Front - tf.N * 0.4f;
		for ( var i = 0; i <= 24; i++ )
		{
			var t = i / 24f;
			tp.Add( tRoot + new Vec3( 1.2f * MathF.Sin( MathF.PI * 0.5f * MathF.Min( 1f, t * 3f ) ) + 0.5f * MathF.Sin( 2f * MathF.PI * t ), 0.3f * MathF.Sin( 3f * t ), -o.Get( "tongueLen", 9f ) * t ) );
			tr.Add( 0.42f * (0.4f + 0.6f * MathF.Pow( 1f - t, 0.8f )) );
		}
		var tongue = new SdfTube( tp, tr ) { Material = GumMat, Tag = "tongue" };

		return new SdfBlend( 0.25f, new SdfBlend( 0.3f, body, lips ), new SdfBlend( 0.6f, skull, tongue ), new SdfBlend( 0.8f, skull, NeckTopBlend( neck, neckR ) ) );
	}

	/// <summary>The top of the neck again, so the skull fuses onto it with a soft fillet.</summary>
	static Sdf NeckTopBlend( List<Vec3> neck, List<float> neckR ) =>
		new SdfTube( neck.Skip( neck.Count - 4 ).ToList(), neckR.Skip( neckR.Count - 4 ).ToList() ) { Material = SkinMat, Tag = "neck" };

	/// <summary>The back of the neck at point k (−X side, perpendicular to the neck).</summary>
	static Vec3 Back( List<Vec3> neck, int k )
	{
		var tan = (neck[Math.Min( k + 1, neck.Count - 1 )] - neck[Math.Max( k - 1, 0 )]).Normal;
		var n = Vec3.Cross( new Vec3( 0, 1, 0 ), tan ).Normal;
		return n.x < 0 ? n : -n;
	}

	// --- 3. bruised cobblestone vein skin (anchor: zombie necromorph skin) ----------------------

	/// <summary>
	/// A 20 × 16 in curved slab of skin (a chest section). In a ragged 9 in patch: raised cells
	/// 0.8–1.6 in across with 0.1–0.25 in grooves between them, getting smaller and sparser toward the
	/// edge with a few strays outside. Thin branching veins (Y-forks) wander over the plain skin.
	/// Build at voxel 0.08 or finer.
	/// </summary>
	static Sdf StudyVeinSkin( Options o )
	{
		var rng = new Random( o.Seed );
		// The slab: the front of a big ellipsoid, so the skin is gently curved like a chest.
		var chest = new SdfEllipsoid( new Vec3( -14, 0, 0 ), new Vec3( 16f, 13f, 11f ) ) { Material = SkinMat, Tag = "skin" };
		var keep = new SdfEllipsoid( new Vec3( 2, 0, 0 ), new Vec3( 4f, 10f, 8f ) );
		var slab = new SdfIntersect( chest, keep );
		slab.Prepare( 0f );

		// Patch outline: noisy radius around a centre on the skin.
		var centre = new Vec3( 2, 0.5f, 0.5f );
		float PatchR( float ang ) => 4.4f + 0.9f * MathF.Sin( 3 * ang + 0.7f ) + 0.5f * MathF.Sin( 7 * ang + 2.1f );

		var cells = new SdfBlend( o.Get( "cellK", 0.05f ) );
		var placed = new List<(Vec3 P, float R)>();
		// Packed, not scattered: a jittered hex grid so every cell touches its neighbours across a
		// narrow groove (the cobblestone read). Cells shrink and drop out toward the ragged edge,
		// and a few strays sit outside it.
		var groove = o.Get( "groove", 0.1f );
		var pitch = o.Get( "pitch", 1.2f );
		// Clusters, not a uniform pack: a slow (about 3 in) field picks islands of 3–8 cells with
		// plain crackled skin between them.
		var clusterCentres = new List<(float Y, float Z, float R)>();
		for ( var k = 0; k < 400 && clusterCentres.Count < (int)o.Get( "clusters", 9 ); k++ )
		{
			var cy = centre.y + Rand( rng, -5f, 5f );
			var cz = centre.z + Rand( rng, -4.5f, 4.5f );
			var ang = MathF.Atan2( cz - centre.z, cy - centre.y );
			if ( MathF.Sqrt( (cy - centre.y) * (cy - centre.y) + (cz - centre.z) * (cz - centre.z) ) > PatchR( ang ) ) continue;
			if ( clusterCentres.Any( c => MathF.Sqrt( (c.Y - cy) * (c.Y - cy) + (c.Z - cz) * (c.Z - cz) ) < 2.6f ) ) continue;
			clusterCentres.Add( (cy, cz, Rand( rng, 1.3f, 2.0f )) );
		}
		bool InCluster( float y, float z ) => clusterCentres.Any( c => MathF.Sqrt( (c.Y - y) * (c.Y - y) + (c.Z - z) * (c.Z - z) ) < c.R );
		for ( var row = -8; row <= 8; row++ )
			for ( var col = -9; col <= 9; col++ )
			{
				var y = centre.y + (col + (row & 1) * 0.5f) * pitch + Rand( rng, -0.22f, 0.22f ) * pitch;
				var z = centre.z + row * pitch * 0.866f + Rand( rng, -0.22f, 0.22f ) * pitch;
				var ang = MathF.Atan2( z - centre.z, y - centre.y );
				var edge = MathF.Sqrt( (y - centre.y) * (y - centre.y) + (z - centre.z) * (z - centre.z) ) / PatchR( ang );
				if ( edge > 1.45f ) continue;
				if ( edge > 0.8f && rng.NextDouble() < (edge - 0.8f) * 1.4f ) continue;
				if ( !InCluster( y, z ) && rng.NextDouble() > 0.08 ) continue;
				// Half the pitch less half the groove, smaller toward the edge, never two alike.
				var cellR = (pitch * 0.5f - groove * 0.5f) * (edge < 0.6f ? 1f : 1f - 0.45f * (edge - 0.6f)) * Rand( rng, 0.88f, 1.12f );
				var p = ToSurface( slab, new Vec3( 6, y, z ) );
				placed.Add( (p, cellR) );
				var n = Gradient( slab, p );
				var lift = o.Get( "cellLift", 0.2f ) * (edge > 1f ? 0.6f : 1f);
				var u = Perp( n );
				var rot = new Xform( u, Vec3.Cross( n, u ).Normal, n, Vec3.Zero ) * Xform.Rotate( new Vec3( 0, 0, 1 ), Rand( rng, 0f, 3.1f ) );
				// Flat-topped cobble: wide and low, sunk so only its cap shows.
				cells.Add( new SdfEllipsoid( p - n * (cellR * 0.55f - lift), new Vec3( cellR * 1.08f, cellR * Rand( rng, 0.85f, 1.05f ), cellR * 0.55f ), rot ) { Material = FleshMat, Tag = "cell" } );
			}
		Console.WriteLine( $"  cells: {placed.Count}" );

		// Branching veins outside the patch: 0.1 in, 2–6 in long, forking.
		var veins = new SdfBlend( 0.05f );
		var veinStarts = (int)o.Get( "veins", 9 );
		var walkers = new List<(Vec3 P, Vec3 D, float L, int Gen)>();
		for ( var i = 0; i < veinStarts; i++ )
		{
			var ang = Rand( rng, 0f, 2 * MathF.PI );
			var start = centre + new Vec3( 0, MathF.Cos( ang ), MathF.Sin( ang ) ) * (PatchR( ang ) * 0.95f);
			walkers.Add( (ToSurface( slab, start + new Vec3( 4, 0, 0 ) ), new Vec3( 0, MathF.Cos( ang ), MathF.Sin( ang ) ), Rand( rng, 3f, 6f ), 0) );
		}
		while ( walkers.Count > 0 )
		{
			var (p, d, len, gen) = walkers[^1];
			walkers.RemoveAt( walkers.Count - 1 );
			var pts = new List<Vec3>();
			var rads = new List<float>();
			var r0 = gen == 0 ? 0.08f : 0.06f;
			for ( var s = 0f; s < len; s += 0.25f )
			{
				var n = Gradient( slab, p );
				pts.Add( p + n * 0.02f );
				rads.Add( MathF.Max( 0.045f, r0 * (1f - 0.6f * s / len) ) );
				var turned = Xform.Rotate( n, Rand( rng, -0.35f, 0.35f ) ).TransformDirection( d );
				d = (turned - n * Vec3.Dot( turned, n )).Normal;
				p = ToSurface( slab, p + d * 0.25f );
				if ( gen < 3 && s > 0.6f && rng.NextDouble() < 0.15 )
					walkers.Add( (p, Xform.Rotate( n, Rand( rng, 0.4f, 0.7f ) * (rng.Next( 2 ) == 0 ? 1 : -1) ).TransformDirection( d ), gen == 0 ? (len - s) * 0.7f : Rand( rng, 0.5f, 1.5f ), gen + 1) );
			}
			if ( pts.Count >= 3 )
				veins.Add( new SdfTube( pts, rads ) { Material = GumMat, Tag = "vein" } );
		}

		// Surface pass: fine crackle on all the skin, and inside the patch a coarse Worley crease
		// network on top of the cobbles, so they read as polygonal cells, not bubbles.
		var useGrain = o.Get( "grain", 1f ) > 0f;
		Detail = !useGrain ? null : ( mesh, nearest ) =>
		{
			float Patch( int v )
			{
				var q = mesh.Positions[v];
				var ang = MathF.Atan2( q.z - centre.z, q.y - centre.y );
				var e = MathF.Sqrt( (q.y - centre.y) * (q.y - centre.y) + (q.z - centre.z) * (q.z - centre.z) ) / PatchR( ang );
				return Math.Clamp( (1.25f - e) / 0.45f, 0f, 1f );
			}
			SurfaceDetail.Grain( mesh, 0.02f, 1.6f, o.Get( "fineCell", 0.32f ), o.Get( "fineCrease", 0.03f ), o.Seed + 3, v => 1f - 0.6f * Patch( v ) );
			SurfaceDetail.Grain( mesh, 0.05f, 0.6f, o.Get( "cell", 1.25f ), o.Get( "crease", 0.07f ), o.Seed + 4, Patch );
		};
		return new SdfBlend( 0.06f, new SdfBlend( o.Get( "cellBlend", 0.05f ), new SdfDisplace( slab, 0.03f, 1.2f, 2, o.Seed ), cells ), veins );
	}

	// --- 4. teeth in rolled gums (anchor: Maw of the Void) -----------------------------------

	/// <summary>
	/// A torso-sized mass with a wide mouth: about 22 × 14 in, a cavity falling back over 10 in.
	/// Round the rim, a thick rolled gum ridge; each tooth sits in its own gum bulb so the gum
	/// creases between teeth. Fangs 3–12 in (outer row long, inner row short), conical and curved,
	/// leaning out of the mouth, staggered so they don't line up.
	/// </summary>
	static Sdf StudyGums( Options o )
	{
		var rng = new Random( o.Seed );
		var mass = new SdfBlend( 3f );
		mass.Add( new SdfEllipsoid( new Vec3( -2, 0, 40 ), new Vec3( 16f, 20f, 22f ) ) { Material = SkinMat, Tag = "mass" } );
		mass.Add( new SdfEllipsoid( new Vec3( -4, 0, 18 ), new Vec3( 13f, 16f, 14f ) ) { Material = SkinMat, Tag = "belly" } );

		// The mouth: an oval on the front, centre at z 40, half-sizes 11 × 7, and a throat behind it.
		var mc = new Vec3( 13.5f, 0, 40 );
		var ha = 11f;
		var hb = 9f;
		// The rim is lopsided: wider low on one side, the top lip sagging, so it never reads as an oval.
		float Wob( float ang ) => 1f + 0.12f * MathF.Sin( 2 * ang + 0.8f ) + 0.07f * MathF.Sin( 5 * ang + 2f );
		Vec3 Rim( float ang, float grow = 0f ) => mc + new Vec3( -2.2f * (MathF.Cos( ang ) * MathF.Cos( ang )), (ha * Wob( ang ) + grow) * MathF.Cos( ang ), (hb * Wob( ang + 1f ) + grow) * MathF.Sin( ang ) - 1.2f * MathF.Max( 0f, MathF.Sin( ang ) ) * MathF.Cos( ang ) * MathF.Cos( ang ) );
		var cavity = new SdfBlend( 2f,
			new SdfEllipsoid( mc + new Vec3( -3, 0, 0 ), new Vec3( 7f, ha * 0.95f, hb * 0.95f ) ),
			new SdfEllipsoid( mc + new Vec3( -11, 0, -1 ), new Vec3( 8f, ha * 0.7f, hb * 0.75f ) ) );
		var body = new SdfSubtract( new SdfDisplace( mass, 0.35f, 0.15f, 2, o.Seed ), 1.0f, cavity );

		// Gum roll right round the rim: a closed tube, thick and uneven.
		var gums = new SdfBlend( o.Get( "gumK", 0.6f ) );
		var rp = new List<Vec3>();
		var rr = new List<float>();
		for ( var i = 0; i <= 48; i++ )
		{
			var ang = 2 * MathF.PI * i / 48;
			rp.Add( Rim( ang, 0.4f ) );
			rr.Add( 0.95f + 0.3f * MathF.Sin( 5 * ang + 1 ) + 0.15f * MathF.Sin( 11 * ang ) );
		}
		gums.Add( new SdfTube( rp, rr ) { Material = GumMat, Tag = "gumRoll" } );

		// Teeth, outer row: 14 round the rim, 4–12 in, longest at the corners of the top lip and the
		// middle of the bottom; inner row: 10, 2.5–5 in, set back and between them.
		var teeth = 0;
		foreach ( var row in new[] { 0, 1 } )
		{
			var n = row == 0 ? (int)o.Get( "outerTeeth", 14 ) : (int)o.Get( "innerTeeth", 10 );
			for ( var i = 0; i < n; i++ )
			{
				var ang = 2 * MathF.PI * (i + (row == 1 ? 0.5f : 0f) + Rand( rng, -0.18f, 0.18f )) / n;
				var upper = MathF.Sin( ang ) > 0;
				var basePos = Rim( ang, row == 0 ? -0.2f : -1.8f ) + new Vec3( row == 0 ? 0.2f : -1.6f, 0, 0 );
				var inward = (mc - basePos);
				inward = new Vec3( 0, inward.y, inward.z ).Normal;
				// Size: corners of the mouth small, mid-top and mid-bottom long; random 3:1 spread.
				var mid = MathF.Abs( MathF.Sin( ang ) );
				// The corners of the mouth carry no long teeth: skip most of them there.
				if ( mid < 0.35f && rng.NextDouble() < 0.6 ) continue;
				var len = row == 0 ? Rand( rng, 3f, 5f ) + 7f * mid * Rand( rng, 0.35f, 1f ) : Rand( rng, 2.5f, 4.5f );
				var baseR = len * Rand( rng, 0.2f, 0.27f ) + 0.25f;
				// Upper teeth hang down, lower ones stand up, all leaning out of the mouth (+X) and a
				// little toward its middle, tips hooking back in: jaws, not a radial urchin.
				var vertical = new Vec3( 0, 0, upper ? -1f : 1f );
				var dir = (vertical * 0.8f + new Vec3( row == 0 ? 0.45f : 0.15f, -0.25f * MathF.Cos( ang ), 0 ) + RandDir( rng ) * 0.15f).Normal;
				var hookTo = new Vec3( -1, 0, 0 );
				Fang( basePos - dir * (baseR * 0.5f), dir, hookTo, len, baseR, 0.04f, Rand( rng, 0.35f, 0.75f ), BoneMat, "tooth", sides: 10 );
				// The tooth's own gum bulb, so the gum wraps its base and creases to the next.
				var bulbR = baseR * Rand( rng, 1.15f, 1.4f );
				gums.Add( new SdfEllipsoid( basePos, new Vec3( bulbR * 1.2f, bulbR, bulbR * 0.85f ), Xform.Rotate( new Vec3( 1, 0, 0 ), ang ) ) { Material = GumMat, Tag = "gumBulb" } );
				teeth++;
			}
		}
		Console.WriteLine( $"  teeth: {teeth}" );

		// Gum fused to the body softly at its back, so it reads grown into the lip, not glued on.
		return new SdfBlend( o.Get( "gumJoinK", 1.2f ), body, new SdfDisplace( gums, 0.15f, 0.6f, 2, o.Seed + 7 ) );
	}
}
