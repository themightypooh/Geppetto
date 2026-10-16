using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Material studies: anchor reads that live in the texture, not the mesh. The zombie necromorph's
/// vein skin is the first: on the real asset the skin is smooth low-poly in clay, and the cobbled
/// cells, red grooves, veins and grain are all normal map + albedo + roughness. So the study is
/// built the way the artist built it: a dense sculpt (here, a height field) baked onto a plain cage.
///
/// Run: dotnet run -c Release -- --material veinskin OUT [key=value ...]
/// Writes cage.obj (with UVs), sculpt.obj, and veinskin_{normal,albedo,rough,height}.png.
/// Inches. Palette and roughness sampled from the zombie's own maps (see part-studies.md §3).
/// </summary>
public static class MaterialStudies
{
	public static int Run( string name, string outDir, string[] extra )
	{
		var o = new Dictionary<string, float>();
		foreach ( var kv in extra )
		{
			var i = kv.IndexOf( '=' );
			if ( i > 0 ) o[kv[..i]] = float.Parse( kv[(i + 1)..], System.Globalization.CultureInfo.InvariantCulture );
		}
		Directory.CreateDirectory( outDir );
		return name switch
		{
			"veinskin" => VeinSkin( outDir, o ),
			"gums" => Gums( outDir, o ),
			"lollipop" => Lollipop( outDir, o ),
			_ => throw new ArgumentException( $"No material study called '{name}'" ),
		};
	}

	static float Get( Dictionary<string, float> o, string k, float d ) => o.TryGetValue( k, out var v ) ? v : d;
	static float Smooth( float a, float b, float x ) { var t = Math.Clamp( (x - a) / (b - a), 0f, 1f ); return t * t * (3f - 2f * t); }
	static Vec3 Hex( string h ) => new( Convert.ToInt32( h[..2], 16 ) / 255f, Convert.ToInt32( h[2..4], 16 ) / 255f, Convert.ToInt32( h[4..6], 16 ) / 255f );
	static Vec3 Mix( Vec3 a, Vec3 b, float t ) => a + (b - a) * Math.Clamp( t, 0f, 1f );

	/// <summary>
	/// A 20 × 16 in patch of chest skin, bent round a 12 in radius. One ragged 9 in patch of cobbled
	/// cells (clusters of 3–8, 0.8–1.6 in, domed, 0.15–0.25 in soft grooves), strays past its edge,
	/// dendritic red veins running out of it, and granular crackle over all the skin.
	/// </summary>
	static int VeinSkin( string outDir, Dictionary<string, float> o )
	{
		const float W = 20f, Hh = 16f, Bend = 12f;
		var seed = (int)Get( o, "seed", 1 );
		var rng = new Random( seed );
		var noise = new Noise3( seed );
		var res = (int)Get( o, "res", 1024 );

		// --- the fields, all in patch-plane inches (x across, y up) ----------------------------
		var centre = new Vec3( 0.5f, 0.3f, 0 );
		float PatchR( float ang ) => 4.5f + 0.9f * MathF.Sin( 3 * ang + 0.7f ) + 0.55f * MathF.Sin( 7 * ang + 2.1f ) + 0.3f * MathF.Sin( 11 * ang + 4f );
		// 1 deep inside the patch, 0 outside, with a noisy, ragged edge.
		float Patch( float x, float y )
		{
			var d = new Vec3( x, y, 0 ) - centre;
			var e = d.Length / PatchR( MathF.Atan2( d.y, d.x ) ) + 0.18f * noise.Fbm( new Vec3( x, y, 0 ) * 0.6f, 2 );
			return Smooth( 1.15f, 0.75f, e );
		}

		var cell = Get( o, "cell", 1.25f );
		// Cells: Worley on a slightly warped plane. Clusters: a blob mask about 3 in across keeps some
		// cells and drops others, so the patch is islands of 3–8 cells with plainer skin between.
		(float Height, float Cap, float Groove, float Centre) Cells( float x, float y, float patch )
		{
			if ( patch <= 0.01f ) return (0, 0, 0, 0);
			var p = new Vec3( x, y, 0 );
			var q = p / cell + noise.Vector( p * 0.35f ) * 0.25f;
			var (f1, f2) = SurfaceDetail.Worley( q, seed + 7 );
			var edge = (f2 - f1) * cell;                       // inches to the nearest cell border
			// Islands: a 2.5 in blob mask with a hard-ish threshold, so clusters of 3–8 cells sit apart.
			var cluster = Smooth( 0.05f, 0.2f, noise.Fbm( p * (1f / 2.5f) + new Vec3( 9, 2, 0 ), 2 ) + 0.35f * patch - 0.3f );
			var keep = cluster * patch;
			if ( keep <= 0.01f ) return (0, 0, 0, 0);
			// Round mound from the distance to the cell's centre (F1), not from the border distance
			// (F2 − F1 makes faceted tents). Cauliflower sub-lumps on the cap, crisp groove at the border.
			// Uneven grooves: width varies ±35% along them, and some stretches close up entirely.
			var gw = Get( o, "groove", 0.08f ) * (1f + 0.35f * noise.Sample( p * 2.5f + new Vec3( 0, 5, 0 ) ));
			var open = Smooth( -0.35f, -0.1f, noise.Sample( p * 1.8f + new Vec3( 6, 0, 0 ) ) );
			var round = MathF.Sqrt( MathF.Max( 0f, 1f - MathF.Pow( f1 * cell / (0.62f * cell), 2f ) ) );
			var dome = round * Smooth( 0f, gw * 1.5f, edge ) * (1f + 0.35f * noise.Fbm( p * 4.5f + new Vec3( 7, 3, 0 ), 2 ));
			var lift = Get( o, "lift", 0.3f ) * keep;
			var groove = (1f - Smooth( 0f, gw, edge )) * open;
			return (lift * dome - Get( o, "grooveDepth", 0.06f ) * groove * keep, keep * Smooth( gw, gw * 2.5f, edge ), groove * keep, round);
		}

		// Veins: dendritic 2D walkers out of the patch edge, Y-forking, thinning; rasterised later as
		// a distance field. 0.05–0.1 in wide, 2–6 in long.
		var veins = new List<(Vec3 A, Vec3 B, float R)>();
		var walkers = new List<(Vec3 P, Vec3 D, float R, float Len, int Gen)>();
		for ( var i = 0; i < (int)Get( o, "veins", 9 ); i++ )
		{
			var ang = (float)(rng.NextDouble() * Math.PI * 2);
			var dir = new Vec3( MathF.Cos( ang ), MathF.Sin( ang ), 0 );
			var start = centre + dir * PatchR( ang ) * 0.85f;
			walkers.Add( (start, dir, Get( o, "veinR", 0.11f ), 2f + 4f * (float)rng.NextDouble(), 0) );
		}
		while ( walkers.Count > 0 )
		{
			var (p, d, r, len, gen) = walkers[^1];
			walkers.RemoveAt( walkers.Count - 1 );
			var step = 0.12f;
			for ( var s = 0f; s < len; s += step )
			{
				d = (d + new Vec3( (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, 0 ) * 0.5f).Normal;
				var next = p + d * step;
				// No crossings: a vein that runs into another (not its own recent trail) stops there.
				var hit = false;
				for ( var k = 0; k < veins.Count - 12; k++ )
				{
					var (va, vb, _) = veins[k];
					if ( (va - next).Length < 0.25f ) { hit = true; break; }
				}
				if ( hit && s > 0.5f ) break;
				var rr = r * (1f - 0.7f * s / len);
				veins.Add( (p, next, rr) );
				p = next;
				if ( gen < 1 && s > 0.8f && rng.NextDouble() < 0.04 )
				{
					var a = (rng.Next( 2 ) == 0 ? 1 : -1) * (0.5f + 0.4f * (float)rng.NextDouble());
					var bd = new Vec3( d.x * MathF.Cos( a ) - d.y * MathF.Sin( a ), d.x * MathF.Sin( a ) + d.y * MathF.Cos( a ), 0 );
					walkers.Add( (p, bd, rr * 0.7f, (len - s) * (0.4f + 0.4f * (float)rng.NextDouble()), gen + 1) );
				}
			}
		}
		// Vein lookup grid (0.5 in buckets).
		var vgrid = new Dictionary<(int, int), List<int>>();
		for ( var i = 0; i < veins.Count; i++ )
		{
			var (a, b, _) = veins[i];
			foreach ( var q in new[] { a, b } )
			{
				var key = ((int)MathF.Floor( q.x / 0.5f ), (int)MathF.Floor( q.y / 0.5f ));
				if ( !vgrid.TryGetValue( key, out var l ) ) vgrid[key] = l = new List<int>();
				if ( l.Count == 0 || l[^1] != i ) l.Add( i );
			}
		}
		float Vein( float x, float y )
		{
			var p = new Vec3( x, y, 0 );
			var best = 0f;
			var cx = (int)MathF.Floor( x / 0.5f );
			var cy = (int)MathF.Floor( y / 0.5f );
			for ( var i = -1; i <= 1; i++ )
				for ( var j = -1; j <= 1; j++ )
				{
					if ( !vgrid.TryGetValue( (cx + i, cy + j), out var l ) ) continue;
					foreach ( var k in l )
					{
						var (a, b, r) = veins[k];
						var ab = b - a;
						var t = Math.Clamp( Vec3.Dot( p - a, ab ) / MathF.Max( 1e-6f, Vec3.Dot( ab, ab ) ), 0f, 1f );
						var dist = (a + ab * t - p).Length;
						best = MathF.Max( best, 1f - Smooth( r * 0.6f, r * 1.6f, dist ) );
					}
				}
			return best;
		}

		// Crumpled hide: ridged noise stretched 1.5:1 across the body (wrinkles run sideways), two octaves.
		float Crumple( float x, float y )
		{
			var p = new Vec3( x, y, 0 );
			var w = new Vec3( p.x * 1.0f, p.y * 1.5f, 0 ) + noise.Vector( p * 0.7f ) * 0.4f;
			var ridged = (1f - MathF.Abs( noise.Sample( w * 1.6f ) )) * 0.65f + (1f - MathF.Abs( noise.Sample( w * 3.7f + new Vec3( 3, 1, 0 ) ) )) * 0.35f;
			return Get( o, "crumple", 0.22f ) * (ridged * ridged - 0.45f);
		}

		// Fine granular crackle over all the skin (the anchor's normal map is mostly this).
		float Grain( float x, float y )
		{
			var p = new Vec3( x, y, 0 );
			// The anchor's normal map is dominated by a coarse granular texture, like dried mud or hide:
			// lumps about 0.15 in across, a broken crack network, and fine pores.
			var (f1, f2) = SurfaceDetail.Worley( p / Get( o, "fineCell", 0.18f ) + noise.Vector( p * 2f ) * 0.35f, seed + 3 );
			var crack = 1f - Smooth( 0f, 0.15f, f2 - f1 );
			var broken = Smooth( -0.2f, 0.3f, noise.Fbm( p * 0.8f + new Vec3( 1, 9, 0 ), 2 ) );
			var lumps = noise.Fbm( p * 6.5f, 3 );
			var pores = MathF.Max( 0f, noise.Sample( p * 14f ) - 0.55f );
			var speckle = noise.Sample( p * 22f ) * 0.5f + noise.Sample( p * 40f ) * 0.3f;
			var crumple = Crumple( x, y );
			return crumple + Get( o, "grain", 0.042f ) * lumps - Get( o, "crackle", 0.03f ) * crack * broken - 0.05f * pores + Get( o, "speckle", 0.025f ) * speckle;
		}

		// --- palette (sampled from the zombie's BaseColor and Roughness maps) ---------------------
		var skin = Hex( "b59e89" ); var mid = Hex( "91715b" ); var red = Hex( "c8352a" ); var dark = Hex( "2d2117" ); var olive = Hex( "89835b" );

		(float H, Vec3 Albedo, float Rough) Sample( float x, float y )
		{
			var patch = Patch( x, y );
			var (ch, cap, groove, centre) = Cells( x, y, patch );
			var vein = Vein( x, y ) * (1f - 0.7f * patch) * (0.6f + 0.4f * Smooth( 0f, 0.4f, patch + 0.3f ));
			var p = new Vec3( x, y, 0 );
			// Mid-scale soft lumps (about 1.7 in), strongest in and around the patch.
			var midLumps = Get( o, "midLumps", 0.18f ) * noise.Fbm( p * 0.6f + new Vec3( 11, 3, 0 ), 2 ) * (0.4f + 0.6f * patch);
			// Veins sit in shallow grooves, not on ridges.
			var grain = Grain( x, y );
			var h = ch + grain + midLumps + vein * Get( o, "veinRaise", 0.1f );
			// Skin: pale with broad mid-tone mottling; olive-green bruising soaks into and around the patch.
			var mott = noise.Fbm( p * 0.45f + new Vec3( 3, 7, 0 ), 3 );
			var grime = noise.Fbm( p * 2.2f + new Vec3( 5, 1, 0 ), 3 );
			var col = Mix( Mix( skin, Hex( "b8a99a" ), 0.25f ), mid, 0.6f + 0.45f * mott + 0.45f * grime );
			// The olive bruise bleeds out in streaks, not a clean blob.
			var streak = noise.Fbm( p * 0.8f + new Vec3( 2, 2, 0 ), 2 );
			col = Mix( col, olive, Smooth( 0.1f, 0.9f, patch * 1.1f + 0.5f * streak + 0.25f * noise.Fbm( p * 0.9f, 2 ) ) * 0.7f );
			var rough = 0.44f + 0.19f * Math.Clamp( 0.45f + 0.9f * mott, 0f, 1f );
			rough = rough + (0.32f - rough) * patch * 0.6f;
			// Cell caps go dark and dry; grooves and veins run red.
			// Caps vary: some near-black, some olive-brown, darkest in their middle.
			var capTone = 0.55f + 0.45f * Smooth( -0.3f, 0.3f, noise.Fbm( p * 0.9f + new Vec3( 2, 8, 0 ), 2 ) );
			// Each cap shades from olive at its rim to near-black in its middle.
			var capCol = Mix( Hex( "6f6a45" ), dark, Smooth( 0.15f, 0.75f, centre ) * capTone + 0.15f );
			col = Mix( col, capCol, cap * 0.92f );
			rough = rough + (0.85f - rough) * cap;
			// Red sits in the grooves and as a thin rim where each cap starts, plus blotches in the patch.
			var rim = Smooth( 0.02f, 0.35f, cap ) * (1f - Smooth( 0.45f, 0.95f, cap ));
			// Red bleeds unevenly: strong in some grooves, gone in others.
			var bleed = Smooth( -0.3f, 0.35f, noise.Fbm( p * 1.6f + new Vec3( 8, 8, 0 ), 2 ) );
			var redness = MathF.Max( MathF.Max( groove * (0.45f + 0.55f * bleed), vein ), rim * 0.7f * bleed ) + 0.35f * patch * Smooth( 0.45f, 0.8f, noise.Fbm( p * 1.3f + new Vec3( 4, 4, 0 ), 2 ) );
			col = Mix( col, Mix( Mix( red, Hex( "a4574e" ), 0.2f * vein ), Hex( "8a2a22" ), 1f - bleed ), Math.Clamp( redness, 0f, 1f ) );
			// Dirt settles in the low spots: grooves, cracks, the bottom of the grain.
			// Dirt in the cavities of the big forms (crumple valleys, lump hollows), not in the fine crackle,
			// which in the albedo read as a cracked-paint web.
			var low = Math.Clamp( -(midLumps + 0.6f * ch + 0.5f * (grain - Get( o, "grain", 0.042f ) * noise.Fbm( p * 6.5f, 3 ))) * 5f, 0f, 1f );
			col = col * (1f - 0.22f * low);
			rough = rough + (0.64f - rough) * Math.Clamp( redness, 0f, 1f );
			// Albedo and roughness follow the relief (a high-pass of the height): cavities darker and a
			// little olive, peaks a touch lighter; matte hide (0.72) that is rougher on peaks and wetter in
			// cavities; cell interiors wet (0.35), their rims 0.6; the halo no glossier than the skin.
			// Dirt follows the crumple valleys and lump hollows (smooth forms), not the pinpoint grain.
			var hp = Crumple( x, y ) + 0.5f * midLumps;
			var cav = Smooth( 0f, 0.08f, -hp );
			var peak = Smooth( 0f, 0.08f, hp );
			// Dark olive-grey hide: 20% darker, 25% toward #6f6f52, with an olive / brown-red mottle about 2.5 in across.
			col = Mix( col * 0.88f * 0.8f, Hex( "6f6f52" ), 0.25f );
			var mm = noise.Fbm( p * 0.4f + new Vec3( 13, 5, 0 ), 2 );
			col = Mix( col, mm > 0 ? Hex( "6b6a45" ) : Hex( "7a4a3c" ), MathF.Abs( mm ) * 0.25f );
			col = Mix( col * (1f - 0.15f * cav), olive * 0.7f, 0.12f * cav ) * (1f + 0.06f * peak);
			// Veins: a dark core with a softer, more saturated edge.
			var veinCore = Smooth( 0.55f, 0.95f, vein );
			col = Mix( col, Hex( "5a1414" ), veinCore * 0.6f );
			var skinRough = 0.72f + 0.08f * peak - 0.10f * cav;
			var cellRough = 0.35f + (0.6f - 0.35f) * (1f - Smooth( 0.2f, 0.7f, centre ));
			rough = Mix( new Vec3( skinRough, 0, 0 ), new Vec3( 0.65f, 0, 0 ), patch * 0.5f ).x;
			rough = rough + (cellRough - rough) * cap;
			rough = rough + (0.6f - rough) * Math.Clamp( redness, 0f, 1f ) * 0.5f;
			return (h, col, rough);
		}

		// --- meshes: a cage (40 × 32 quads, UVs) and the sculpt (0.04 in grid), both bent ---------
		Vec3 Bent( float x, float y, float h )
		{
			var a = x / Bend;
			var r = Bend + h;
			return new Vec3( r * MathF.Sin( a ), y, r * MathF.Cos( a ) - Bend );
		}
		var cage = Primitives.Plane( W, Hh, 40, 32 );
		for ( var i = 0; i < cage.VertexCount; i++ ) { var p = cage.Positions[i]; cage.Positions[i] = Bent( p.x, p.y, 0 ); }
		var segX = (int)(W / Get( o, "step", 0.04f )); var segY = (int)(Hh / Get( o, "step", 0.04f ));
		var sculpt = Primitives.Plane( W, Hh, segX, segY );
		var albedo = new Vec4[sculpt.VertexCount];
		var rough = new float[sculpt.VertexCount];
		var height = new float[sculpt.VertexCount];
		var clock = System.Diagnostics.Stopwatch.StartNew();
		System.Threading.Tasks.Parallel.For( 0, sculpt.VertexCount, i =>
		{
			var p = sculpt.Positions[i];
			var (h, c, r) = Sample( p.x, p.y );
			height[i] = h; albedo[i] = new Vec4( c.x, c.y, c.z, 1 ); rough[i] = r;
		} );
		for ( var i = 0; i < sculpt.VertexCount; i++ ) { var p = sculpt.Positions[i]; sculpt.Positions[i] = Bent( p.x, p.y, height[i] ); }
		sculpt.VertexColors = albedo;
		Console.WriteLine( $"veinskin: sculpt {sculpt.VertexCount:N0} verts, {veins.Count} vein segments, fields {clock.Elapsed.TotalSeconds:0.0}s" );

		var opts = new BakeOptions { MaxDistance = 0.6f };
		clock.Restart();
		var nmap = NormalBake.Bake( cage, sculpt, res, opts );
		var amap = NormalBake.BakeVertexColours( cage, sculpt, res, res, opts );
		var rmap = NormalBake.BakeScalar( cage, sculpt, res, res, rough, 0f, 1f, opts );
		var hmap = NormalBake.BakeScalar( cage, sculpt, res, res, height, -0.1f, 0.3f, opts );
		Console.WriteLine( $"  baked {res}² in {clock.Elapsed.TotalSeconds:0.0}s, filled {nmap.FilledCount * 100f / (res * res):0}%" );
		PngWriter.WriteFile( Path.Combine( outDir, "veinskin_normal.png" ), nmap, flipVertically: true );
		PngWriter.WriteFile( Path.Combine( outDir, "veinskin_albedo.png" ), amap, flipVertically: true );
		PngWriter.WriteFile( Path.Combine( outDir, "veinskin_rough.png" ), rmap, flipVertically: true );
		PngWriter.WriteFile( Path.Combine( outDir, "veinskin_height.png" ), hmap, flipVertically: true );
		ObjWriter.WriteFile( cage, Path.Combine( outDir, "cage.obj" ), "cage" );
		if ( Get( o, "sculptObj", 0f ) > 0f ) ObjWriter.WriteFile( sculpt, Path.Combine( outDir, "sculpt.obj" ), "sculpt" );
		return 0;
	}

	// --- gums (anchor: Maw of the Void's mouth) ------------------------------------------------

	/// <summary>
	/// The Maw's lower mouth, split as the real asset is. MESH: a V-shaped jaw with a chin plate, a
	/// gum shelf the teeth stand on (a UV'd strip), big ivory cones (2 side fangs, 5 front teeth),
	/// 6 upper teeth hanging from an upper rim, a tongue ball and a deep throat. MATERIAL (baked onto
	/// the shelf strip): festoon collars round each tooth, scalloped lobes, creases and pleats, with
	/// colour driven by AO (hot pink lobes, near-black pleats), as measured in materials.md §4.
	/// Inches, 90 in creature, x toward the viewer, z up. Writes gum_cage.obj + gum_{normal,albedo,
	/// rough}.png, teeth.obj (vertex colours), body.obj, tongue.obj.
	/// </summary>
	static int Gums( string outDir, Dictionary<string, float> o )
	{
		var seed = (int)Get( o, "seed", 1 );
		var rng = new Random( seed );
		var noise = new Noise3( seed );
		float R( float a, float b ) => a + (b - a) * (float)rng.NextDouble();

		// The lower jaw arc: a U in plan, its front lowest (the chin plate's V), rising to the sides.
		const float A0 = -1.25f, A1 = 1.25f;
		Vec3 Arc( float a, float shrink, float dz ) =>
			new( (6.5f * MathF.Cos( a ) - 1.5f) * shrink, 11.5f * MathF.Sin( a ) * shrink, 30f + 9f * MathF.Pow( MathF.Sin( a ) * MathF.Sin( a ) + 0.004f, 0.7f ) + dz );
		// The gum wall: faces the viewer, rising from the chin plate (t = 0, low and outside) up and a
		// little back to the tooth line (t ≈ 0.8) and over into the mouth (t = 1).
		// About 8 in tall: on the anchor the gum band is a third of the mouth's height.
		Vec3 Shelf( float a, float t ) => Vec3.Lerp( Arc( a, 1.04f, -7f ), Arc( a, 0.86f, 1.2f ), t ) + new Vec3( 0, 0, 0.6f * MathF.Sin( MathF.PI * t ) );
		var arcLen = 0f;
		for ( var i = 0; i < 200; i++ ) arcLen += (Arc( A0 + (A1 - A0) * (i + 1) / 200f, 1, 0 ) - Arc( A0 + (A1 - A0) * i / 200f, 1, 0 )).Length;
		Vec3 ShelfNormal( float a, float t )
		{
			var da = Shelf( a + 0.002f, t ) - Shelf( a - 0.002f, t );
			var dt = Shelf( a, t + 0.002f ) - Shelf( a, t - 0.002f );
			var n = Vec3.Cross( da, dt ).Normal;
			var outward = Arc( a, 1f, 0f ) - Arc( a, 0.5f, 0f );
			return Vec3.Dot( n, outward ) < 0 ? -n : n;
		}

		// --- teeth: positions on the shelf (a, t), length, base radius -----------------------------
		var teeth = new List<(float A, float T, float Len, float Rb, Vec3 Dir)>();
		foreach ( var s in new[] { -1f, 1f } )
			// Roots on the wall's face (t 0.6), where they can plug into their lobes.
			teeth.Add( (0.95f * s, 0.62f, R( 13.5f, 15f ), 2.75f, new Vec3( 0.3f, -0.35f * s, 1f )) );
		// Three uneven front teeth, set a little back on the shelf, shorter than the fangs.
		foreach ( var (a, len, rb) in new[] { (-0.42f, 8.4f, 1.9f), (0.05f, 7.2f, 1.5f), (0.47f, 9.0f, 2.1f) } )
			teeth.Add( (a + R( -0.04f, 0.04f ), 0.6f, len * R( 0.95f, 1.05f ), rb, new Vec3( 0.15f, -0.25f * MathF.Sin( a ), 1f )) );
		var shelfAt = teeth.Select( t => (Pos: Shelf( t.A, t.T ), t) ).ToList();

		// --- the gum strip: cage + dense sculpt, both parametric with UVs ------------------------
		PolyMesh Strip( int nu, int nv, Func<float, float, float> height )
		{
			var m = new PolyMesh();
			for ( var j = 0; j <= nv; j++ )
				for ( var i = 0; i <= nu; i++ )
				{
					var a = A0 + (A1 - A0) * i / nu; var t = j / (float)nv;
					m.AddVertex( Shelf( a, t ) + ShelfNormal( a, t ) * height( a, t ) );
				}
			var stride = nu + 1;
			for ( var j = 0; j < nv; j++ )
				for ( var i = 0; i < nu; i++ )
				{
					var q = j * stride + i;
					m.AddFace( new[] { q, q + 1, q + stride + 1, q + stride },
						new[] { new Vec2( i / (float)nu, j / (float)nv ), new Vec2( (i + 1f) / nu, j / (float)nv ), new Vec2( (i + 1f) / nu, (j + 1f) / nv ), new Vec2( i / (float)nu, (j + 1f) / nv ) } );
				}
			return m;
		}
		// Gum detail, in inches along the arc (s) and across the band (w), as a height above the shelf.
		var bandW = 8f;
		float GumHeight( float a, float t )
		{
			var s = (a - A0) / (A1 - A0) * arcLen;
			var w = t * bandW;
			var p = Shelf( a, 0 );
			var h = 0f;
			// One lobe per tooth (the anchor's festoon): a U-shaped mound around the tooth's root, open
			// at the top (fading out over its top 30%), wrapping a little up the tooth's sides, with a dark
			// crease only along the U's outer edge. Overlapping lobes take the higher, so no rings.
			var lobe = 0f; var crease = 0f;
			foreach ( var (tp, tt) in shelfAt )
			{
				var ts = (tt.A - A0) / (A1 - A0) * arcLen; var tw = tt.T * bandW;
				var lw = tt.Rb * 1.45f + 0.3f; var lh = Get( o, "lobeH", 2.6f );
				var lx = (s - ts) / lw; var ly = (w - (tw - lh * 0.35f)) / (lh * 0.65f);
				var ld = MathF.Sqrt( lx * lx + MathF.Min( ly, 0f ) * MathF.Min( ly, 0f ) + MathF.Max( ly, 0f ) * MathF.Max( ly, 0f ) * 0.3f );
				var open = Smooth( tw + lh * 0.45f, tw - lh * 0.05f, w );
				lobe = MathF.Max( lobe, MathF.Sqrt( MathF.Max( 0f, 1f - ld * ld ) ) * open );
				var outer = MathF.Exp( -(ld - 1.04f) * (ld - 1.04f) / 0.01f ) * Smooth( tw, tw - lh * 0.2f, w );
				crease = MathF.Max( crease, outer );
				// The socket: a dark ring right at the root.
				var d = MathF.Sqrt( (s - ts) * (s - ts) + (w - tw) * (w - tw) );
				crease = MathF.Max( crease, 0.7f * MathF.Exp( -(d - tt.Rb * 1.05f) * (d - tt.Rb * 1.05f) / 0.05f ) );
			}
			if ( Get( o, "sculpt", 1f ) <= 0f ) h += Get( o, "festoon", 1.0f ) * lobe - 0.4f * crease;
			var tl = 0.66f * bandW;
			// Vertical pleats down the wall below the lobes, every 0.6 in, broken.
			var pleat = MathF.Pow( 1f - MathF.Abs( MathF.Sin( MathF.PI * (s + 0.4f * noise.Sample( new Vec3( s, w, 0 ) * 0.4f )) / 0.6f ) ), 3f );
			h -= (Get( o, "sculpt", 1f ) > 0f ? 0f : Get( o, "pleat", 0.3f )) * pleat * Smooth( tl - 3.2f, tl - 4.4f, w ) * Smooth( 0.1f, 0.6f, w ) * Smooth( -0.2f, 0.3f, noise.Fbm( new Vec3( s * 0.3f, w * 0.6f, 0 ), 2 ) );
			// Fine fleshy grain.
			h += 0.04f * noise.Fbm( new Vec3( s, w, 0 ) * 3f, 3 );
			return h;
		}
		var cage = Strip( 60, 12, ( a, t ) => 0f );
		var sculpt = Strip( (int)(arcLen / Get( o, "step", 0.05f )), (int)(bandW / Get( o, "step", 0.05f )), GumHeight );
		var clock = System.Diagnostics.Stopwatch.StartNew();
		// --- sculpt the festoons with the brush kernel (sculpt=1): the analytic lobes stop at about
		// 6, because they can't lean, overhang or pinch. Same strokes a sculptor would make. ----------
		var useSculpt = Get( o, "sculpt", 1f ) > 0f;
		(Vec3 P, Vec3 N) At( float s, float w )
		{
			var a = A0 + s / arcLen * (A1 - A0); var t = w / bandW;
			return (Shelf( a, t ), ShelfNormal( a, t ));
		}
		if ( useSculpt )
		{
			clock.Restart();
			var strokes = 0;
			void Stroke( BrushKind kind, IEnumerable<(float S, float W)> path, float radius, float strength, BrushFalloff fall = BrushFalloff.Smooth )
			{
				var st = new BrushStroke { Kind = kind, Falloff = fall };
				var pts = path.ToList();
				for ( var i = 0; i < pts.Count; i++ )
				{
					var (p, n) = At( pts[i].S, pts[i].W );
					var (q, _) = At( pts[Math.Min( i + 1, pts.Count - 1 )].S, pts[Math.Min( i + 1, pts.Count - 1 )].W );
					var dir = (q - p).LengthSquared > 1e-8f ? (q - p).Normal : Vec3.Zero;
					st.Samples.Add( new BrushSample( p, n, radius, strength, dir ) );
				}
				Brush.Apply( sculpt, st );
				strokes++;
			}
			IEnumerable<(float, float)> U( float cs, float cw, float halfW, float depth, float step )
			{
				// Up one side, round under the root, up the other: the festoon's path.
				var n = (int)(MathF.PI * (halfW + depth) / step);
				for ( var i = 0; i <= n; i++ )
				{
					var ang = MathF.PI * i / n;
					yield return (cs - halfW * MathF.Cos( ang ), cw - depth * MathF.Sin( ang ) + 0.25f * depth * (1f - MathF.Sin( ang )));
				}
			}
			foreach ( var (tp, tt) in shelfAt )
			{
				var ts = (tt.A - A0) / (A1 - A0) * arcLen; var tw = tt.T * bandW;
				var hw = tt.Rb * 1.25f + 0.2f; var dp = Get( o, "lobeDepth", 1.9f );
				// The U's arms reach up past the root, so the roll wraps the tooth's base.
				tw += Get( o, "lobeUp", 0.9f );
				// 1. The roll: Blob dabs round the U, two passes, a little inside then on the line.
				Stroke( BrushKind.Blob, U( ts, tw, hw * 0.8f, dp * 0.7f, 0.25f ), 0.9f, Get( o, "roll", 0.09f ) );
				Stroke( BrushKind.Blob, U( ts, tw, hw, dp, 0.25f ), 0.75f, Get( o, "roll", 0.09f ) );
				// 2. Fill the lobe's belly so it reads as one swollen roll, not a ring.
				Stroke( BrushKind.Inflate, new[] { (ts, tw - dp * 0.5f), (ts - hw * 0.4f, tw - dp * 0.45f), (ts + hw * 0.4f, tw - dp * 0.45f) }, hw * 0.9f, 0.18f );
				// 3. The crease under the roll's outer edge: Dam Standard along a slightly bigger U.
				Stroke( BrushKind.Crease, U( ts, tw + 0.15f, hw + 0.45f, dp + 0.4f, 0.12f ), 0.3f, Get( o, "creaseD", 0.05f ) );
				// 4. The roll hangs: drag its lower edge down a little so it overhangs the crease.
				Stroke( BrushKind.Grab, new[] { (ts, tw - dp * 0.9f), (ts, tw - dp * 1.05f) }, hw * 0.7f, 0.25f );
				// 5. The socket: pinch the gum in tight round the root.
				Stroke( BrushKind.Crease, U( ts, tw + 0.35f, tt.Rb * 1.05f, tt.Rb * 0.9f, 0.1f ), 0.25f, 0.04f );
			}
			// 6. Pleats below the lobes: short Crease strokes down the wall, every ~0.6 in, broken.
			for ( var s0 = 0.3f; s0 < arcLen - 0.3f; s0 += Get( o, "pleatGap", 0.6f ) )
			{
				if ( noise.Sample( new Vec3( s0 * 0.7f, 3, 0 ) ) < -0.35f ) continue;
				var top = 0.62f * bandW - 2.3f + R( -0.4f, 0.3f ); var len = R( 1.6f, 3f );
				var path = new List<(float, float)>();
				for ( var w0 = top; w0 > top - len; w0 -= 0.1f ) path.Add( (s0 + 0.15f * MathF.Sin( w0 * 2f ), w0) );
				Stroke( BrushKind.Crease, path, 0.18f, 0.025f );
			}
			// 7. A light smooth to knit the dabs, then let the detail grain back in.
			Stroke( BrushKind.Smooth, Enumerable.Range( 0, (int)(arcLen / 1.2f) ).SelectMany( i => new[] { (i * 1.2f, 2f), (i * 1.2f, 4.5f) } ), 1.4f, 0.15f );
			Console.WriteLine( $"  sculpted: {strokes} strokes in {clock.Elapsed.TotalSeconds:0.0}s" );
		}
		clock.Restart();
				var ao = SurfaceDetail.AmbientOcclusion( sculpt, (int)Get( o, "aoRays", 32 ), Get( o, "aoReach", 2f ), seed );
				// Cavity (how far each vertex sits below its smoothed surroundings) finds the brush creases,
				// which are too narrow for AO to see.
				var cavity = SurfaceDetail.Cavity( sculpt, 8 );
		Console.WriteLine( $"gums: sculpt {sculpt.VertexCount:N0} verts, AO {clock.Elapsed.TotalSeconds:0.0}s" );
		// Colour: lobes hot pink-red, pleats and creases near-black, by AO (the Maw's gum is the most
		// AO-driven zone on any pick); a dark socket collar right at each tooth root.
		var lobeCol = Hex( "b14f5b" ); var creaseCol = Hex( "100604" ); var gumMid = Hex( "431d1a" );
		var cols = new Vec4[sculpt.VertexCount];
		var rough = new float[sculpt.VertexCount];
		var nu = (int)(arcLen / Get( o, "step", 0.05f )); var nvv = (int)(bandW / Get( o, "step", 0.05f ));
		for ( var v = 0; v < sculpt.VertexCount; v++ )
		{
			var i = v % (nu + 1); var j = v / (nu + 1);
			var a = A0 + (A1 - A0) * i / nu; var t = j / (float)nvv;
			// Height read off the sculpt itself, so colour follows what the brushes made.
			var h = Vec3.Dot( sculpt.Positions[v] - Shelf( a, t ), ShelfNormal( a, t ) );
			// Lobe tops hot pink, everything recessed falls hard to near-black (the anchor's gum is the most
			// AO-driven zone on any pick).
			var occl = Math.Clamp( Smooth( 0.2f, 0.6f, ao[v] ) * 0.6f + Smooth( 0.05f, -0.2f, h ) * 0.6f + Smooth( 0.008f, 0.04f, cavity[v] ) * 0.95f, 0f, 1f );
			var lift = Smooth( 0.05f, 0.4f, h );
			var c = Mix( Mix( gumMid, lobeCol, lift ), creaseCol, occl );
			// Past the tooth line, over into the mouth, it falls into shadow.
			c = Mix( c, Hex( "110705" ), Smooth( 0.88f, 1f, t ) * 0.8f );
			cols[v] = new Vec4( c.x, c.y, c.z, 1 );
			rough[v] = 0.5f - 0.12f * occl;
		}
		sculpt.VertexColors = cols;
		var res = (int)Get( o, "res", 2048 );
		var opts = new BakeOptions { MaxDistance = 3f };
		clock.Restart();
		PngWriter.WriteFile( Path.Combine( outDir, "gum_normal.png" ), NormalBake.Bake( cage, sculpt, res, res / 4, opts ), flipVertically: true );
		PngWriter.WriteFile( Path.Combine( outDir, "gum_albedo.png" ), NormalBake.BakeVertexColours( cage, sculpt, res, res / 4, opts ), flipVertically: true );
		PngWriter.WriteFile( Path.Combine( outDir, "gum_rough.png" ), NormalBake.BakeScalar( cage, sculpt, res, res / 4, rough, 0f, 1f, opts ), flipVertically: true );
		Console.WriteLine( $"  baked in {clock.Elapsed.TotalSeconds:0.0}s" );
		ObjWriter.WriteFile( cage, Path.Combine( outDir, "gum_cage.obj" ), "gum" );

		// --- teeth: separate meshes, ivory with a dark root collar ------------------------------
		var tmesh = new PolyMesh();
		var tcol = new List<Vec4>();
		void Tooth( Vec3 root, Vec3 dir, float len, float rb, Vec3 bendTo, float curl )
		{
			dir = dir.Normal;
			const int sides = 12, rings = 14;
			var p = root - dir * (rb * 0.6f); var d = dir;
			var axis = Vec3.Cross( dir, bendTo ).Normal;
			var baseIdx = tmesh.VertexCount;
			var u = (MathF.Abs( d.z ) < 0.9f ? Vec3.Cross( d, new Vec3( 0, 0, 1 ) ) : Vec3.Cross( d, new Vec3( 1, 0, 0 ) )).Normal;
			for ( var r = 0; r <= rings; r++ )
			{
				var t = r / (float)rings;
				var rad = rb * MathF.Pow( 1f - t, 0.85f ) * (1f + 0.08f * MathF.Sin( MathF.PI * t ));
				u = (u - d * Vec3.Dot( u, d )).Normal; var w = Vec3.Cross( d, u ).Normal;
				var along = t * (len + rb * 0.6f);
				var fromGum = along - rb * 0.6f - 0.45f;   // roots are sunk 0.4 in below the gum line
				// Root collar #784f3d for the first 0.5 in out of the gum, recovering to ivory by 1 in.
				// A gradient out of the flesh, not a band: dark root to ivory by about 1 in.
				var c = Mix( Hex( "784f3d" ), Hex( "cfa783" ), Smooth( -0.1f, 1.1f, fromGum ) );
				// Baked occlusion: the root half of the tooth is darker, the tip catches light.
				c = c * (0.7f + 0.3f * Smooth( 0f, 0.5f, t ));
				for ( var k = 0; k < sides; k++ )
				{
					var ang = 2f * MathF.PI * k / sides;
					tmesh.AddVertex( p + u * (MathF.Cos( ang ) * rad) + w * (MathF.Sin( ang ) * rad * 0.92f) );
					var speck = noise.Sample( (p + u * MathF.Cos( ang )) * 4f ) > 0.55f ? 0.85f : 1f;
					tcol.Add( new Vec4( c.x * speck, c.y * speck, c.z * speck, 1 ) );
				}
				p += d * ((len + rb * 0.6f) / rings);
				if ( t > 0.35f ) d = Xform.Rotate( axis, curl / (rings * 0.65f) ).TransformDirection( d ).Normal;
			}
			for ( var r = 0; r < rings; r++ )
				for ( var k = 0; k < sides; k++ )
				{
					var q = baseIdx + r * sides + k; var q2 = baseIdx + r * sides + (k + 1) % sides;
					tmesh.AddFace( new[] { q, q2, q2 + sides, q + sides } );
				}
		}
		var mouthCentre = new Vec3( -1f, 0, 44f );
		foreach ( var (tp, tt) in shelfAt )
		{
			// Sunk 0.4 in so no cone bottom shows at the gum line.
			// Into the wall by most of the root radius, so the cone's base is buried in the gum, not
			// standing in front of it with its open end showing.
			var root = tp - ShelfNormal( tt.A, tt.T ) * (0.4f + 0.6f * tt.Rb);
			Tooth( root, tt.Dir, tt.Len, tt.Rb, mouthCentre - root, Get( o, "curl", 0.75f ) );
		}
		// Upper teeth hang from the upper rim, shorter, pointing down and a little in.
		Vec3 Upper( float a ) => new( 6.0f * MathF.Cos( a ) - 1.5f, 10.5f * MathF.Sin( a ), 57f - 4f * MathF.Pow( MathF.Abs( MathF.Sin( a ) ), 1.2f ) );
		for ( var i = 0; i < 6; i++ )
		{
			var a = -0.75f + 1.5f * i / 5f + R( -0.05f, 0.05f );
			var root = Upper( a );
			Tooth( root, new Vec3( 0.25f, -0.15f * MathF.Sin( a ), -1f ), R( 5.4f, 6.9f ), R( 0.6f, 1.0f ), mouthCentre - root, 0.25f );
		}
		tmesh.VertexColors = tcol.ToArray();
		ObjWriter.WriteFile( tmesh, Path.Combine( outDir, "teeth.obj" ), "teeth" );

		// --- body: jaw mass, chin plate, upper rim, cavity; tongue ball -------------------------
		var jaw = new SdfBlend( 1.2f );
		var chinPts = new List<Vec3>(); var chinR = new List<float>();
		for ( var i = 0; i <= 30; i++ ) { var a = A0 - 0.05f + (A1 - A0 + 0.1f) * i / 30f; // The chin plate runs along under the gum wall's bottom edge.
chinPts.Add( Arc( a, 1.1f, -8.6f ) ); chinR.Add( 1.9f ); }
		jaw.Add( new SdfTube( chinPts, chinR ) );
		var shelfPts = new List<Vec3>(); var shelfR = new List<float>();
		for ( var i = 0; i <= 30; i++ ) { var a = A0 + (A1 - A0) * i / 30f; shelfPts.Add( Shelf( a, 0.5f ) - ShelfNormal( a, 0.5f ) * 3.2f ); shelfR.Add( 2.9f ); }
		jaw.Add( new SdfTube( shelfPts, shelfR ) );
		var rimPts = new List<Vec3>(); var rimR = new List<float>();
		for ( var i = 0; i <= 30; i++ ) { var a = -1.2f + 2.4f * i / 30f; rimPts.Add( Upper( a ) + new Vec3( -0.5f, 0, 1.6f ) ); rimR.Add( 2.3f ); }
		jaw.Add( new SdfTube( rimPts, rimR ) );
		// The head-chest mass behind and around the mouth, with the throat hollowed out of it.
		var mass = new SdfEllipsoid( new Vec3( -9f, 0, 44f ), new Vec3( 10f, 17f, 18f ) );
		var throat = new SdfEllipsoid( new Vec3( -6f, 0, 44f ), new Vec3( 11f, 10f, 11f ) );
		var body = new SdfBlend( 2f, new SdfSubtract( mass, 1.5f, throat ), jaw );
		var bmesh = SurfaceNets.Mesh( body, Get( o, "voxel", 0.3f ) ).Mesh;
		MeshSmooth.Taubin( bmesh, 3 );
		ObjWriter.WriteFile( bmesh, Path.Combine( outDir, "body.obj" ), "body" );
		var tongue = SurfaceNets.Mesh( new SdfEllipsoid( new Vec3( -4f, 0, 37.5f ), new Vec3( 6.5f, 7.5f, 6f ) ), 0.25f ).Mesh;
		MeshSmooth.Taubin( tongue, 3 );
		ObjWriter.WriteFile( tongue, Path.Combine( outDir, "tongue.obj" ), "tongue" );
		Console.WriteLine( $"  teeth {tmesh.VertexCount:N0} verts, body {bmesh.VertexCount:N0}" );
		return 0;
	}

	// --- swirl lollipop (anchor: the user's rainbow swirl photo) ---------------------------------

	/// <summary>
	/// A rainbow swirl lollipop, pristine (melt=0) or melting (melt=1): a rope of 7 coloured sugar
	/// strands twisted helically, coiled in an Archimedean spiral into a 5.2 in disc on a white stick.
	/// Melting is gravity: the disc sags into a teardrop (the stripes stretch with it, since colour
	/// follows the source leaf through the warp), thick drips run off the bottom and down the stick,
	/// and a smeared rainbow puddle spreads round the stick's foot. Hard candy is glass, so it flows.
	/// Inches; disc faces +X; writes lollipop.obj with vertex colours. Run: --material lollipop OUT melt=1
	/// </summary>
	static int Lollipop( string outDir, Dictionary<string, float> o )
	{
		var seed = (int)Get( o, "seed", 1 );
		var rng = new Random( seed );
		float R( float a, float b ) => a + (b - a) * (float)rng.NextDouble();
		var melt = Get( o, "melt", 0f );
		// Rainbow order as in the reference, outermost strand first round the rope.
		var cols = new[] { "e8243c", "ff7a1a", "ffd21f", "3cc24a", "1fa4e8", "7a3cc8", "ff5aa8" }.Select( Hex ).ToArray();
		var strands = cols.Length;

		// The coil: Archimedean spiral in the disc plane (y right, z up), centre slightly domed forward.
		var discC = new Vec3( 0, 0, Get( o, "discZ", 9f ) );
		// The photo's rope is about 0.16 of the disc's radius across, coiled about 3.5 times.
		var ropeR = Get( o, "ropeR", 0.4f );
		var pitch = ropeR * 2f * 0.96f;
		var discR = Get( o, "discR", 2.6f );
		var turns = (discR - ropeR) / pitch;
		var coil = new List<Vec3>();
		var thEnd = turns * 2f * MathF.PI;
		for ( var th = 0.35f; th < thEnd + MathF.PI; th += 0.03f )
		{
			// Starts in a tight curl at the centre; the last half turn stops growing, so the rim stays
			// round and the rope's end tucks in instead of sticking out as a tail.
			var r = ropeR * 0.35f + pitch * MathF.Min( th, thEnd ) / (2f * MathF.PI) - (th > thEnd ? 0.35f * ropeR * (th - thEnd) / MathF.PI : 0f);
			coil.Add( discC + new Vec3( 0.25f * (1f - (r / discR) * (r / discR)), r * MathF.Cos( th ), r * MathF.Sin( th ) ) );
		}
		// Strands: helices round the coil, a full twist every ~1.3 in.
		var parts = new List<Sdf>();
		// Long bands along the rope, as in the photo: a full twist every ~5 in.
		var twist = 2f * MathF.PI / Get( o, "twistLen", 3f );
		for ( var k = 0; k < strands; k++ )
		{
			var pts = new List<Vec3>(); var rads = new List<float>();
			var s = 0f;
			for ( var i = 0; i < coil.Count; i++ )
			{
				if ( i > 0 ) s += (coil[i] - coil[i - 1]).Length;
				var tan = (coil[Math.Min( i + 1, coil.Count - 1 )] - coil[Math.Max( i - 1, 0 )]).Normal;
				var nrm = new Vec3( 1, 0, 0 ); // the disc's facing
				var bin = Vec3.Cross( tan, nrm ).Normal; nrm = Vec3.Cross( bin, tan ).Normal;
				var phi = 2f * MathF.PI * k / strands + twist * s;
				// The rope tapers into its tail at the centre.
				var taper = MathF.Min( 1f, s / 1.2f ) * 0.55f + 0.45f;
				// The tucked-in end also tapers away.
				var left = (coil.Count - 1 - i) * 0.03f * 2.6f;
				taper *= 0.55f + 0.45f * MathF.Min( 1f, left / 1.2f );
				pts.Add( coil[i] + (nrm * MathF.Cos( phi ) + bin * MathF.Sin( phi )) * (ropeR * 0.55f * taper) );
				rads.Add( ropeR * 0.52f * taper );
				// Chunk the strand so the blend can cull by bounds.
				if ( pts.Count == 40 || i == coil.Count - 1 )
				{
					parts.Add( new SdfTube( pts.ToList(), rads.ToList() ) { Tag = "strand" + k } );
					pts = new List<Vec3> { pts[^1] }; rads = new List<float> { rads[^1] };
				}
			}
		}
		// Real coils fuse where they touch: a thin plate behind the coil closes any gap between turns.
		parts.Add( new SdfEllipsoid( discC + new Vec3( -0.12f, 0, 0 ), new Vec3( ropeR * 0.55f, discR - ropeR * 0.6f, discR - ropeR * 0.6f ) ) { Tag = "strand" + 0 } );
		// The centre of the curl, so the stick doesn't show through.
		parts.Add( new SdfEllipsoid( coil[0] + new Vec3( -0.05f, 0, 0 ), new Vec3( ropeR * 0.7f, ropeR * 0.8f, ropeR * 0.8f ) ) { Tag = "strand" + 5 } );
		Sdf candy = new SdfBlend( Get( o, "strandK", 0.04f ), parts.ToArray() );
		var stick = new SdfCapsule( new Vec3( 0, 0, 0.2f * (1f - MathF.Min( 1f, melt / 0.15f )) ), discC, 0.17f ) { Tag = "stick" };

		// sim=1: melt it for real (MeltSim) instead of posing a melt; writes f000.obj... per frame.
		if ( Get( o, "sim", 0f ) > 0f )
		{
			o.Remove( "melt" ); // MeltSim's own "melt" is the liquid temperature
			candy.Prepare( 0f );
			return MeltSim.Run( outDir, o, new MeltSim.Setup
			{
				Body = candy,
				Support = new SdfCapsule( Vec3.Zero, discC, 0.17f ) { Tag = "stick" },
				ColourAt = p => candy.Nearest( p, 1.5f )?.Tag is string t && t.StartsWith( "strand" ) ? cols[int.Parse( t[6..] )] : cols[0],
				Floor = 0f,
			} );
		}

		var pieces = new List<Sdf>();
		// Where to look up colour: each source field and how to map a world point into it. A warped
		// SdfInstance reports itself as the leaf, so colour has to be read from its child, through the warp.
		var colourSources = new List<(Sdf Field, Func<Vec3, Vec3> ToSource)> { (stick, p => p) };
		if ( melt <= 0f )
		{
			pieces.Add( candy );
			colourSources.Add( (candy, p => p) );
			}
		else
		{
			// Gravity sag: every point is pulled down by more the lower it sits on the disc, so the disc
			// becomes a teardrop; the low part also thickens front-to-back as material piles up.
			var top = discC.z + discR;
			var sag = new WarpMelt( top, discC, discR, melt * Get( o, "sag", 0.6f ), melt * Get( o, "belly", 0.9f ) );
			pieces.Add( new SdfInstance( candy, Xform.Identity ).Warp( sag ) );
			colourSources.Add( (candy, sag.Inverse) );
			var bottom = sag.Forward( discC - new Vec3( 0, 0, discR ) );
			// Drips: off the teardrop's bottom and lower rim, each the colour of the strand it left from;
			// thick necks, rounded fronts, some running down the stick to the puddle.
			var drips = (int)Get( o, "drips", 7 );
			for ( var i = 0; i < drips; i++ )
			{
				var ang = -MathF.PI / 2f + R( -1.1f, 1.1f );
				var src = sag.Forward( discC + new Vec3( 0, MathF.Cos( ang ) * discR * 0.9f, MathF.Sin( ang ) * discR * 0.9f ) );
				var k = rng.Next( strands );
				var len = i < 2 ? src.z - 0.15f : R( 0.8f, Math.Min( 4.5f, src.z - 0.5f ) );
				var pts = new List<Vec3>(); var rads = new List<float>();
				var r0 = R( 0.13f, 0.24f );
				var toward = i < 2 ? new Vec3( 0, -src.y, 0 ) * 0.9f : Vec3.Zero; // the first two run down to the stick
				// Animating melt 0 -> 1: each drip starts at its own time (the stick runners first) and its
				// front travels its full path, easing out; g is how far along it is (1 at melt=1).
				var t0 = i < 2 ? 0.1f + 0.05f * i : 0.2f + 0.08f * (i - 2);
				var g = Math.Clamp( (melt - t0) / (1f - t0), 0f, 1f ); g = 1f - (1f - g) * (1f - g);
				if ( g < 0.02f ) continue;
				r0 *= 0.4f + 0.6f * MathF.Min( 1f, g * 3f ); // swells from a bulge into a full drip
				for ( var t = 0f; t <= 1.001f; t += 0.05f )
				{
					var u = t * g; // the drip so far is the first g of its full path
					var p = src + new Vec3( 0.12f * MathF.Sin( 5 * u + i ), 0, -len * u ) + toward * MathF.Min( 1f, u * 3f );
					pts.Add( p );
					// Neck thins as it runs, front swells into a bead.
					rads.Add( r0 * (1f - 0.45f * u) + r0 * 0.6f * MathF.Exp( -(t - 1f) * (t - 1f) / 0.004f ) );
				}
				var drip = new SdfTube( pts, rads ) { Tag = "strand" + k };
				pieces.Add( drip );
				colourSources.Add( (drip, p => p) );
				}
			// Puddle round the stick's foot: smeared rainbow swirl, a thin glassy spread with a meniscus rim.
			var pc = new Vec3( 0, 0, 0 );
			// Starts once the stick runners reach the foot, then spreads.
			var pr = Get( o, "puddleR", 2.6f ) * Smooth( 0.2f, 1f, melt );
			var ribbons = new List<Sdf>();
			// The seven stripes interleaved in one spiral that fills the pool, as if the disc's coil had
			// slumped flat and spread: band width 0.3, so a turn steps out by all seven bands.
			var bandW = 0.3f; var stepPerTurn = bandW * strands;
			for ( var k = 0; k < strands; k++ )
			{
				var pts = new List<Vec3>(); var rads = new List<float>();
				for ( var th = 0f; th < 2f * MathF.PI * (pr / stepPerTurn + 1f); th += 0.06f )
				{
					var r = stepPerTurn * th / (2f * MathF.PI) + bandW * k;
					if ( r > pr ) break;
					// Flowed, not drawn: the bands wander and the pool's edge is lobed.
					var wob = 1f + 0.1f * MathF.Sin( 3 * th + 0.5f ) + 0.05f * MathF.Sin( 7 * th + k );
					pts.Add( pc + new Vec3( r * wob * MathF.Cos( th ), r * wob * MathF.Sin( th ), 0.08f ) );
					rads.Add( bandW * 0.72f );
				}
				if ( pts.Count > 2 ) ribbons.Add( new SdfTube( pts, rads ) { Tag = "strand" + k } );
			}
			// Squash the ribbons flat: a puddle is about 0.15 in thick, with a rounded rim.
			if ( ribbons.Count > 0 )
			{
			var ribbonField = new SdfBlend( 0.1f, ribbons.ToArray() );
			var squash = new WarpStretch( Vec3.Zero, new Vec3( 0, 0, 1 ), 0.35f );
			var flat = new SdfInstance( ribbonField, Xform.Identity ).Warp( squash );
			pieces.Add( flat );
			colourSources.Add( (ribbonField, squash.Inverse) );
			}
		}
		var field = new SdfBlend( Get( o, "meltK", 0.02f + 0.23f * MathF.Min( 1f, melt / 0.3f ) ), pieces.ToArray() );
		var all = new SdfBlend( 0.06f, field, stick );
		all.Prepare( 0f );
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var res = SurfaceNets.Mesh( all, Get( o, "voxel", 0.03f ) );
		var mesh = res.Mesh;
		MeshSmooth.Taubin( mesh, 2 );
		var white = new Vec3( 0.93f, 0.93f, 0.9f );
		// Colour blended over every strand near the vertex, weighted by distance, so stripe edges are
		// smooth lines instead of voxel stairs (nearest-leaf labels alias at the voxel size).
		Vec3 ColOf( Sdf l ) => l?.Tag is string t && t.StartsWith( "strand" ) ? cols[int.Parse( t[6..] )] : white;
		var vc = new Vec4[mesh.VertexCount];
		System.Threading.Tasks.Parallel.For( 0, mesh.VertexCount, v =>
		{
			var near = new List<(Sdf Leaf, float Distance)>();
			foreach ( var (field, toSource) in colourSources ) field.Collect( toSource( mesh.Positions[v] ), 0.25f, near );
			var acc = Vec3.Zero; var wsum = 0f; var stickW = 0f;
			var best = near.Count > 0 ? near.Min( q => q.Distance ) : 0f;
			foreach ( var (leaf, d) in near )
			{
				var w = MathF.Exp( -(d - best) / Get( o, "colourSoft", 0.012f ) );
				acc += ColOf( leaf ) * w; wsum += w;
				if ( leaf.Tag == "stick" ) stickW += w;
			}
			var c = wsum > 0 ? acc / wsum : ColOf( res.Nearest[v] );
			vc[v] = new Vec4( c.x, c.y, c.z, wsum > 0 ? 1f - stickW / wsum : 1f );
		} );
		mesh.VertexColors = vc;
		ObjWriter.WriteFile( mesh, Path.Combine( outDir, "lollipop.obj" ), "lollipop" );
		Console.WriteLine( $"lollipop melt {melt}: {mesh.VertexCount:N0} verts in {clock.Elapsed.TotalSeconds:0.0}s" );
		return 0;
	}

	/// <summary>Gravity sag for melting candy on a stick: source points lower on the disc slide further
	/// down (stretching the disc into a teardrop) and spread front-to-back as material piles up low.</summary>
	sealed class WarpMelt : SdfWarp
	{
		readonly float _top, _sag, _belly, _r; readonly Vec3 _c;
		public WarpMelt( float top, Vec3 centre, float radius, float sag, float belly ) { _top = top; _c = centre; _r = radius; _sag = sag; _belly = belly; }
		float Drop( float z ) { var d = MathF.Max( 0f, _top - z ) / (2f * _r); return _sag * 2f * _r * d * d; }
		float Low( float z ) => Math.Clamp( (_c.z - z) / _r, 0f, 1.6f );
		public override Vec3 Forward( Vec3 p )
		{
			var low = Low( p.z );
			return new Vec3( _c.x + (p.x - _c.x) * (1f + _belly * low * low), p.y * (1f - 0.12f * low * _sag), p.z - Drop( p.z ) );
		}
		public override Vec3 Inverse( Vec3 p )
		{
			// Solve z_src - Drop(z_src) = p.z. The map only stretches downward, so it is monotonic and
			// bisection finds the one answer; fixed-point iteration diverges once the sag is strong.
			var lo = p.z; var hi = p.z + _sag * 2f * _r + 1e-3f;
			for ( var i = 0; i < 32; i++ )
			{
				var mid = 0.5f * (lo + hi);
				if ( mid - Drop( mid ) < p.z ) lo = mid; else hi = mid;
			}
			var z = 0.5f * (lo + hi);
			var low = Low( z );
			return new Vec3( _c.x + (p.x - _c.x) / (1f + _belly * low * low), p.y / (1f - 0.12f * low * _sag), z );
		}
		public override float Reach => _sag * 2f * _r + _belly * _r;
	}
}

/// <summary>
/// Shaded render of an OBJ through Effigy's own <see cref="Effigy.Render.ShadedRender"/>.
/// `--shade in.obj out.png eye=x,y,z target=x,y,z fov=30 size=900 floor=0 preset=candy`.
/// With preset=candy, faces whose vertices carry alpha 0 (a stick) render as matte paper and the rest
/// as hard candy.
/// </summary>
public static class ShadeCli
{
	public static int Run( string[] args )
	{
		var objPath = args[0]; var outPath = args[1];
		var kv = args.Skip( 2 ).Select( a => a.Split( '=', 2 ) ).Where( p => p.Length == 2 ).ToDictionary( p => p[0], p => p[1] );
		Vec3 V( string k, Vec3 d ) => kv.TryGetValue( k, out var s ) ? new Vec3( F( s.Split( ',' )[0] ), F( s.Split( ',' )[1] ), F( s.Split( ',' )[2] ) ) : d;
		float F( string s ) => float.Parse( s, System.Globalization.CultureInfo.InvariantCulture );
		float G( string k, float d ) => kv.TryGetValue( k, out var s ) ? F( s ) : d;

		var mesh = ObjReader.ReadFile( objPath );
		var scene = new Effigy.Render.ShadeScene { FloorHeight = G( "floor", 0f ), Floor = G( "floor", 0f ) > -999f, Exposure = G( "exposure", 1f ) };
		if ( kv.GetValueOrDefault( "preset" ) == "candy" && mesh.VertexColors is { } vc )
		{
			var (candy, stick) = Split( mesh, f => f.Indices.Average( i => vc[i].w ) > 0.5f );
			scene.Add( candy, Effigy.Render.ShadeMaterial.HardCandy() );
			scene.Add( stick, new Effigy.Render.ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.85f, 0.85f, 0.82f ), Roughness = 0.7f, Subsurface = 0.2f } );
		}
		else scene.Add( mesh, new Effigy.Render.ShadeMaterial() );
		var cam = new Effigy.Render.ShadeCamera( V( "eye", new Vec3( 20, 0, 8 ) ), V( "target", new Vec3( 0, 0, 7 ) ), G( "fov", 30f ) );
		scene.StudioLights( cam.Target - cam.Eye );
		var size = (int)G( "size", 900 );
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var r = Effigy.Render.ShadedRender.Render( scene, cam, size, size, (int)G( "ss", 2 ) );
		r.SavePng( outPath );
		Console.WriteLine( $"shaded {mesh.Faces.Count:N0} faces at {size}² in {clock.Elapsed.TotalSeconds:0.0}s -> {outPath}" );
		return 0;
	}

	/// <summary>Two meshes from one: faces that pass the test, and the rest, each with its own vertices.</summary>
	static (PolyMesh Yes, PolyMesh No) Split( PolyMesh mesh, Func<Face, bool> test )
	{
		PolyMesh Part( bool want )
		{
			var map = new Dictionary<int, int>();
			var m = new PolyMesh();
			var colours = new List<Vec4>();
			foreach ( var f in mesh.Faces.Where( f => test( f ) == want ) )
			{
				var idx = f.Indices.Select( i =>
				{
					if ( !map.TryGetValue( i, out var j ) ) { j = m.AddVertex( mesh.Positions[i] ); map[i] = j; if ( mesh.VertexColors is { } c ) colours.Add( c[i] ); }
					return j;
				} ).ToArray();
				m.AddFace( idx );
			}
			if ( mesh.VertexColors is not null ) m.VertexColors = colours.ToArray();
			return m;
		}
		return (Part( true ), Part( false ));
	}
}
