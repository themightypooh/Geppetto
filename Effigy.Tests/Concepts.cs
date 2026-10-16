using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The three concepts, round 2. Each is built in three layers that get different treatment:
/// FLESH (the grown mass, guts, tendrils) takes heavy noise; SKIN (the human torso and limbs,
/// ribs, folds) takes light noise so the anatomy survives it; HARD (heads, hands, boots, hats,
/// teeth, rods) takes almost none so the parts that make it a person stay crisp. Cavities are
/// cut out of flesh+skin together, then the hard parts go on top.
/// </summary>
public static partial class CreatureGen
{
	sealed class Layers
	{
		public readonly SdfBlend Flesh, Skin, Hard, Teeth;
		public readonly List<Sdf> Cuts = new();

		public Layers( float fleshK, float skinK )
		{
			Flesh = new SdfBlend( fleshK );
			Skin = new SdfBlend( skinK );
			Hard = new SdfBlend( 0.3f );
			Teeth = new SdfBlend( 0.25f );
		}

		public Sdf Assemble( int seed, float fleshAmp = 1.4f, float joinK = 2.2f, float cutK = 1.4f )
		{
			// Big slow lobes, and only a whisper of fine noise: taut skin, not cauliflower.
			var flesh = new SdfDisplace( new SdfDisplace( Flesh, fleshAmp, 0.065f, 2, seed, warp: 0.45f ), fleshAmp * 0.08f, 0.35f, 1, seed + 9 );
			var skin = new SdfDisplace( Skin, 0.22f, 0.45f, 2, seed + 3 ) { Stretch = new Vec3( 1f, 1f, 1f ) };
			var body = new SdfBlend( joinK, flesh, skin );
			var cut = new SdfSubtract( body, cutK );
			foreach ( var c in Cuts )
				cut.Add( c );
			var withTeeth = new SdfBlend( 0.35f, cut, Teeth );
			return new SdfBlend( 0.7f, withTeeth, new SdfDisplace( Hard, 0.05f, 0.8f, 1, seed + 1 ) );
		}
	}

	// --- shared anatomy helpers -------------------------------------------------------------

	/// <summary>A rib: an arc around <paramref name="axis"/> through <paramref name="center"/>,
	/// from angle a0 to a1 measured from <paramref name="zero"/>, radius stretched by ry/rz.</summary>
	static void RibArc( SdfBlend into, Vec3 center, Vec3 axis, Vec3 zero, float radiusA, float radiusB, float a0, float a1, float r, int material, string tag, int steps = 9 )
	{
		axis = axis.Normal;
		zero = (zero - axis * Vec3.Dot( zero, axis )).Normal;
		var other = Vec3.Cross( axis, zero ).Normal;
		var pts = new List<Vec3>();
		var radii = new List<float>();

		for ( var i = 0; i <= steps; i++ )
		{
			var a = (a0 + (a1 - a0) * i / steps) * MathF.PI / 180f;
			pts.Add( center + zero * MathF.Cos( a ) * radiusA + other * MathF.Sin( a ) * radiusB );
			radii.Add( r * (0.8f + 0.2f * MathF.Sin( MathF.PI * i / steps )) );
		}

		into.Add( new SdfTube( pts, radii ) { Material = material, Tag = tag } );
	}

	/// <summary>Vertebral knobs along a polyline.</summary>
	static void Spine( SdfBlend into, IReadOnlyList<Vec3> path, float spacing, float r, string tag, int material = SkinMat )
	{
		for ( var i = 0; i + 1 < path.Count; i++ )
		{
			var a = path[i];
			var b = path[i + 1];
			var len = (b - a).Length;
			var n = Math.Max( 1, (int)(len / spacing) );
			for ( var k = 0; k < n; k++ )
				into.Add( new SdfSphere( Vec3.Lerp( a, b, (float)k / n ), r ) { Material = material, Tag = tag } );
		}
	}

	/// <summary>A ring of skin, a fold or a collar, around an axis.</summary>
	static void Ring( SdfBlend into, Vec3 center, Vec3 axis, float radius, float r, int material, string tag, float radiusB = -1f )
	{
		Orthonormal( axis, out var x, out var y );
		if ( radiusB < 0f ) radiusB = radius;
		var pts = new List<Vec3>();
		var radii = new List<float>();
		for ( var i = 0; i <= 16; i++ )
		{
			var a = i / 16f * MathF.PI * 2f;
			pts.Add( center + x * MathF.Cos( a ) * radius + y * MathF.Sin( a ) * radiusB );
			radii.Add( r );
		}
		into.Add( new SdfTube( pts, radii ) { Material = material, Tag = tag } );
	}

	/// <summary>Skin folds: short ridges laid over a surface region, where bodies were pulled together.</summary>
	static void Folds( SdfBlend into, Random rng, Vec3 center, Vec3 radii, int count, float length, float r, string tag, int material = SkinMat )
	{
		for ( var i = 0; i < count; i++ )
		{
			var dir = RandDir( rng );
			var onSurface = center + new Vec3( dir.x * radii.x, dir.y * radii.y, dir.z * radii.z ) * 0.97f;
			Orthonormal( dir, out var t, out _ );
			t = Rotate( t, dir, Rand( rng, 0, 180 ) );
			var pts = new[] { onSurface - t * length * 0.5f, onSurface + dir * r * 0.4f, onSurface + t * length * 0.5f };
			into.Add( new SdfTube( pts, new[] { r * 0.4f, r, r * 0.4f } ) { Material = material, Tag = tag } );
		}
	}

	/// <summary>A gut rope: a segmented tube along a path that wanders over the floor.</summary>
	static void GutRope( SdfBlend into, Random rng, Vec3 start, Vec3 dir, float length, float radius, string tag )
	{
		var pts = new List<Vec3>();
		var radii = new List<float>();
		var p = start;
		var d = dir.Normal;
		var step = radius * 1.6f;
		var n = (int)(length / step);
		for ( var i = 0; i <= n; i++ )
		{
			pts.Add( p );
			radii.Add( radius * ((i & 1) == 0 ? 1.12f : 0.85f) );
			d = (d + new Vec3( Rand( rng, -0.35f, 0.35f ), Rand( rng, -0.35f, 0.35f ), 0 )).Normal;
			p += d * step;
			p = new Vec3( p.x, p.y, MathF.Max( radius * 0.75f, p.z - step * 0.4f ) );
		}
		into.Add( new SdfTube( pts, radii ) { Material = FleshMat, Tag = tag } );
	}

	/// <summary>
	/// Ribs lying under the skin of an ellipsoid torso: each rib follows the torso's own cross
	/// section at its station along the long (local x) axis, proud by <paramref name="proud"/>,
	/// over the angle range a0..a1 (0 = local +z, 90 = local +y).
	/// </summary>
	static void RibsOn( SdfBlend into, Vec3 center, Vec3 radii, Xform rotation, int count, float x0, float x1,
		float a0, float a1, float r, float proud, string tag, int material = SkinMat )
	{
		for ( var k = 0; k < count; k++ )
		{
			var x = count == 1 ? x0 : x0 + (x1 - x0) * k / (count - 1);
			var f = 1f - (x / radii.x) * (x / radii.x);
			if ( f <= 0.05f ) continue;
			var sc = MathF.Sqrt( f );
			var pts = new List<Vec3>();
			var rad = new List<float>();
			for ( var i = 0; i <= 10; i++ )
			{
				var a = (a0 + (a1 - a0) * i / 10f) * MathF.PI / 180f;
				var local = new Vec3( x, MathF.Sin( a ) * (radii.y * sc + proud - r * 0.5f), MathF.Cos( a ) * (radii.z * sc + proud - r * 0.5f) );
				pts.Add( center + rotation.TransformDirection( local ) );
				rad.Add( r * (0.75f + 0.25f * MathF.Sin( MathF.PI * i / 10f )) );
			}
			into.Add( new SdfTube( pts, rad ) { Material = material, Tag = tag } );
		}
	}

	/// <summary>A gut loop hanging from a point on the underside to the floor and pooling.</summary>
	static void HangingGut( SdfBlend into, Random rng, Vec3 from, Vec3 pool, float radius, string tag )
	{
		var pts = new List<Vec3>();
		var rad = new List<float>();
		var n = 10;
		for ( var i = 0; i <= n; i++ )
		{
			var t = (float)i / n;
			var p = Vec3.Lerp( from, pool, t ) + new Vec3( MathF.Sin( t * 5f ) * 1.5f, MathF.Cos( t * 4f ) * 1.5f, 0 ) + RandDir( rng ) * 0.6f;
			pts.Add( new Vec3( p.x, p.y, MathF.Max( radius * 0.8f, p.z ) ) );
			rad.Add( radius * ((i & 1) == 0 ? 1.12f : 0.86f) );
		}
		into.Add( new SdfTube( pts, rad ) { Material = FleshMat, Tag = tag } );
	}

	/// <summary>A coiled gut rope on the floor that loops back on itself.</summary>
	static void GutLoop( SdfBlend into, Random rng, Vec3 start, Vec3 heading, float length, float radius, string tag )
	{
		var pts = new List<Vec3>();
		var rad = new List<float>();
		var p = start;
		var a = MathF.Atan2( heading.y, heading.x );
		var step = radius * 1.4f;
		var n = (int)(length / step);
		var turn = Rand( rng, 0.18f, 0.28f ) * (rng.Next( 2 ) == 0 ? 1 : -1);
		for ( var i = 0; i <= n; i++ )
		{
			pts.Add( new Vec3( p.x, p.y, radius * 0.85f ) );
			rad.Add( radius * ((i & 1) == 0 ? 1.12f : 0.86f) );
			// Straight-ish, then a loop back.
			a += i > n / 3 && i < n * 2 / 3 ? turn * 2.2f : Rand( rng, -0.12f, 0.12f );
			p += new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 ) * step;
		}
		into.Add( new SdfTube( pts, rad ) { Material = FleshMat, Tag = tag } );
	}

	/// <summary>Stretch folds: long shallow ridges radiating from a point over a surface.</summary>
	static void StretchFolds( SdfBlend into, Random rng, Vec3 center, Vec3 radii, Vec3 from, int count, float length, float r, string tag, int material = SkinMat )
	{
		for ( var i = 0; i < count; i++ )
		{
			var a = Rand( rng, 0, MathF.PI * 2 );
			var start = (from - center).Normal;
			Orthonormal( start, out var u, out var v );
			var pts = new List<Vec3>();
			var rad = new List<float>();
			for ( var k = 0; k <= 6; k++ )
			{
				var t = k / 6f;
				var dir = (start + (u * MathF.Cos( a ) + v * MathF.Sin( a )) * t * (length / radii.x)).Normal;
				var onSurface = center + new Vec3( dir.x * radii.x, dir.y * radii.y, dir.z * radii.z );
				pts.Add( onSurface );
				rad.Add( r * MathF.Sin( MathF.PI * (0.1f + 0.8f * t) ) );
			}
			into.Add( new SdfTube( pts, rad ) { Material = material, Tag = tag } );
		}
	}

	// --- CHOIR: three crewmen, one sack, six borrowed limbs ----------------------------------

	/// <summary>
	/// A torso torn open down its midline: a sunk split <paramref name="length"/> tall, two ragged
	/// flaps of skin and muscle peeled outward, five broken ribs a side jutting forward and down
	/// through the flaps as uneven shards (they are the teeth), gullet folds inside, and a limp
	/// tongue hanging from the bottom of the split to the floor.
	/// </summary>
	static void TornSplit( Layers L, Random rng, Vec3 c, Vec3 normal, Vec3 along, float length, string tag, Vec3 tongueSide = default )
	{
		var n = normal.Normal;
		var a = along.Normal;
		var s = Vec3.Cross( n, a ).Normal;
		var half = length * 0.5f;
		var basis = new Xform( n, s, a, Vec3.Zero );
		// The opening, deep: 9 in wide at the middle, cut in from the surface.
		L.Cuts.Add( new SdfEllipsoid( c - n * 2.6f, new Vec3( 7f, 4.5f, half ), basis ) { Material = CavityMat, Tag = tag } );
		// Gullet: three rings stepping back 6 in into a throat.
		for ( var k = 0; k < 3; k++ )
		{
			var ringC = c - n * (2.2f + 2.0f * k);
			var ry = 3.6f - 0.9f * k; var rz = half * (0.8f - 0.18f * k);
			var pts = new List<Vec3>(); var rad = new List<float>();
			for ( var q = 0; q <= 20; q++ )
			{
				var ang = q / 20f * MathF.PI * 2f;
				pts.Add( ringC + s * (MathF.Sin( ang ) * ry) + a * (MathF.Cos( ang ) * rz) );
				rad.Add( 0.55f - 0.1f * k );
			}
			// In the layer added after the cut: in the flesh layer the opening's own cut carved them away.
			L.Teeth.Add( new SdfTube( pts.ToArray(), rad.Select( r => r * 1.4f ).ToArray() ) { Material = GumMat, Tag = tag } );
		}
		// Strands of flesh across the opening, sagging.
		for ( var k = 0; k < 3; k++ )
		{
			var t = Rand( rng, -0.5f, 0.5f );
			var p0 = c + a * (t * half) + s * 3.6f - n * 0.6f;
			var p1 = c + a * ((t + Rand( rng, -0.2f, 0.2f )) * half) - s * 3.6f - n * 0.6f;
			L.Teeth.Add( new SdfTube( new[] { p0, Vec3.Lerp( p0, p1, 0.5f ) - a * 1.6f + n * 0.4f, p1 }, new[] { 0.4f, 0.24f, 0.38f } ) { Material = GumMat, Tag = tag } );
		}
		foreach ( var side in new[] { 1f, -1f } )
		{
			// The flap: a wedge of skin and muscle peeled out from the edge, thick at the hinge and
			// thin at the torn lip. The left one is peeled 5 in and folded down over itself, the right 2 in.
			var peel = side > 0 ? 5f : 2f;
			for ( var i = 0; i <= 9; i++ )
			{
				var t = -0.9f + 1.8f * i / 9f;
				var open = 1f - t * t;
				var hinge = c + a * (t * half) + s * side * (4.2f * MathF.Sqrt( open ) + 0.3f);
				var outDir = (s * side * 0.55f + n * 0.83f).Normal;
				var droop = side > 0 ? -a * (1.6f * open) : Vec3.Zero;
				var mid = hinge + outDir * (peel * 0.5f * open) + droop * 0.5f;
				var thick = 0.75f * (0.6f + 0.4f * open);
				var fx = new Xform( outDir, Vec3.Cross( a, outDir ).Normal, a, Vec3.Zero );
				L.Flesh.Add( new SdfEllipsoid( mid, new Vec3( peel * 0.55f * open + 0.6f, thick, half * 0.13f * Rand( rng, 0.9f, 1.25f ) ), fx ) { Material = FleshMat, Tag = tag } );
			}
			// Tears notched into the lip.
			for ( var k = 0; k < 3; k++ )
			{
				var t = Rand( rng, -0.7f, 0.7f );
				var open = 1f - t * t;
				var at = c + a * (t * half) + s * side * (4.2f * MathF.Sqrt( open ) + 0.3f) + (s * side * 0.55f + n * 0.83f).Normal * (peel * open);
				L.Cuts.Add( new SdfEllipsoid( at, new Vec3( 1.4f, 1.0f, 0.3f ), basis ) { Material = FleshMat, Tag = tag } );
			}
			// Ribs broken out through the flaps, four a side 4 in apart, 1.2 in thick at the root: out of
			// the wall, forward, then curving back in toward the middle like a ribcage closing.
			foreach ( var t in new[] { 0.6f, 0.2f, -0.2f, -0.6f } )
			{
				var root = c + a * (t * half + Rand( rng, -0.3f, 0.3f )) + s * side * 4.4f - n * 1.6f;
				var reachOut = Rand( rng, 0.85f, 1.15f );
				var mid = root + n * (3.2f * reachOut) + s * side * 1.2f - a * 0.4f;
				// Curving back in, but stopping well short of the middle: a ribcage, not a grille.
				var tip = mid + n * (2.4f * reachOut) - s * side * (1.1f * reachOut) - a * 0.9f;
				L.Teeth.Add( new SdfTube( new[] { root, mid, tip }, new[] { 0.9f, 0.7f, 0.32f } ) { Material = BoneMat, Tag = tag } );
			}
		}
		// The tongue: flat (4 x 1.2 in), out of the bottom of the split, off-centre, hanging 14 in and
		// lapping onto the floor.
		var bottom = c - a * (half * 0.85f) - n * 1.5f;
		var off = tongueSide.LengthSquared > 0f ? tongueSide.Normal * 3f : Vec3.Zero;
		var path = new[] { bottom, bottom + n * 5f - a * 0.6f + off * 0.4f, bottom + n * 7.5f - a * 4f + off, new Vec3( bottom.x + 9f, bottom.y, 1.6f ) + off, new Vec3( bottom.x + 13f, bottom.y + 1.5f, 0.75f ) + off };
		for ( var i = 0; i < path.Length - 1; i++ )
			for ( var k = 0; k < 4; k++ )
			{
				var p = Vec3.Lerp( path[i], path[i + 1], k / 4f );
				var dir = (path[i + 1] - path[i]).Normal;
				var flatUp = Vec3.Cross( dir, Vec3.Cross( new Vec3( 0, 0, 1 ), dir ).LengthSquared > 1e-4f ? Vec3.Cross( new Vec3( 0, 0, 1 ), dir ).Normal : s ).Normal;
				var w = 2f - 0.45f * (i + k / 4f) / path.Length;
				L.Teeth.Add( new SdfEllipsoid( p, new Vec3( 1.3f, w, 0.6f ), new Xform( dir, Vec3.Cross( flatUp, dir ).Normal, flatUp, Vec3.Zero ) ) { Material = GumMat, Tag = tag + "_tongue" } );
			}
	}

	/// <summary>
	/// A chest torn open, not a cut: an irregular hole about 14 x 20 in, its rim peeled outward 3-4 in
	/// in ragged flaps, the ribs snapped outward through the rim (1.5 in thick, staggered, splintered),
	/// a throat falling away into the dark behind. The tongue lolls out of the bottom.
	/// </summary>
	static void TornChest( Layers L, Random rng, Vec3 c, Vec3 normal, Vec3 along, float length, string tag, Vec3 tongueSide = default )
	{
		var n = normal.Normal;
		var a = along.Normal;
		var s = Vec3.Cross( n, a ).Normal;
		var half = length * 0.5f;
		var basis = new Xform( n, s, a, Vec3.Zero );
		// The hole's half-width at height t (-1..1 along it), ragged: a few cut blobs, each off-centre.
		var widths = new float[9];
		for ( var i = 0; i < widths.Length; i++ ) widths[i] = Rand( rng, 0.75f, 1.1f );
		float Width( float t ) { var f = (t + 1f) * 0.5f * (widths.Length - 1); var i = Math.Clamp( (int)f, 0, widths.Length - 2 ); var w = widths[i] + (widths[i + 1] - widths[i]) * (f - i); return 7f * MathF.Sqrt( MathF.Max( 0f, 1f - t * t ) ) * w + 0.6f; }
		for ( var i = 0; i < 7; i++ )
		{
			var t = -0.8f + 1.6f * i / 6f;
			L.Cuts.Add( new SdfEllipsoid( c + a * (t * half) + s * Rand( rng, -1f, 1f ) - n * 3f, new Vec3( 6.5f, Width( t ) * 0.95f, half * 0.24f ), basis ) { Material = CavityMat, Tag = tag } );
		}
		// The throat: falling away 8 in back, narrowing.
		L.Cuts.Add( new SdfEllipsoid( c - n * 8f + a * 1f, new Vec3( 5f, 3.6f, half * 0.45f ), basis ) { Material = CavityMat, Tag = tag } );
		// Two strands of tissue still spanning it, sagging.
		foreach ( var t in new[] { 0.35f, -0.2f } )
		{
			var p0 = c + a * (t * half) + s * (Width( t ) - 0.4f) - n * 1.2f;
			var p1 = c + a * ((t - 0.12f) * half) - s * (Width( t - 0.12f ) - 0.4f) - n * 1.6f;
			L.Teeth.Add( new SdfTube( new[] { p0, Vec3.Lerp( p0, p1, 0.5f ) - a * 2.4f + n * 0.3f, p1 }, new[] { 0.55f, 0.32f, 0.5f } ) { Material = GumMat, Tag = tag } );
		}
		foreach ( var side in new[] { 1f, -1f } )
		{
			// The flaps: skin and muscle peeled outward from the rim, thick at the hinge, thin and
			// uneven at the torn lip; the right side curls further and droops.
			var peel = side > 0 ? 4f : 3f;
			for ( var i = 0; i <= 12; i++ )
			{
				var t = -0.88f + 1.76f * i / 12f;
				var open = 1f - t * t;
				var hinge = c + a * (t * half) + s * side * Width( t );
				var outDir = (s * side * 0.7f + n * 0.7f).Normal;
				var reach = peel * MathF.Sqrt( open ) * Rand( rng, 0.6f, 1.3f );
				var droop = side > 0 ? -a * (1.2f * open) : Vec3.Zero;
				var mid = hinge + outDir * (reach * 0.5f) + droop * 0.5f;
				var fx = new Xform( outDir, Vec3.Cross( a, outDir ).Normal, a, Vec3.Zero );
				L.Flesh.Add( new SdfEllipsoid( mid, new Vec3( reach * 0.6f + 0.7f, Rand( rng, 0.55f, 0.85f ), half * 0.1f * Rand( rng, 0.8f, 1.3f ) ), fx ) { Material = FleshMat, Tag = tag } );
			}
			// Tears notched into the lip.
			for ( var k = 0; k < 3; k++ )
			{
				var t = Rand( rng, -0.7f, 0.7f );
				var at = c + a * (t * half) + s * side * (Width( t ) + peel * 0.6f) + n * (peel * 0.5f);
				L.Cuts.Add( new SdfEllipsoid( at, new Vec3( 1.4f, 1.1f, 0.35f ), basis ) { Material = FleshMat, Tag = tag } );
			}
			// The ribs, snapped outward like a cracked-open chest: four a side, 1.5 in thick, rooted in
			// the hole's wall and bent out over the flaps, each a different length, ending in a splinter.
			foreach ( var t0 in new[] { 0.58f, 0.22f, -0.14f, -0.5f } )
			{
				var t = t0 + Rand( rng, -0.04f, 0.04f );
				var root = c + a * (t * half) + s * side * (Width( t ) - 1.2f) - n * 2.6f;
				var len = Rand( rng, 4f, 7f );
				var dir = (s * side * Rand( rng, 0.35f, 0.6f ) + n * 0.85f + a * Rand( rng, -0.15f, 0.2f )).Normal;
				var mid = root + dir * (len * 0.55f);
				var tip = mid + (dir + s * side * 0.35f).Normal * (len * 0.45f);
				L.Teeth.Add( new SdfTube( new[] { root, mid, tip }, new[] { 0.8f, 0.7f, 0.5f } ) { Material = BoneMat, Tag = tag } );
				// The splinter: the break leaves a sharp sliver past the blunt end, off to one side.
				var sliver = (dir + a * Rand( rng, -0.5f, 0.5f ) + s * side * 0.3f).Normal;
				L.Teeth.Add( new SdfRoundCone( tip - dir * 0.3f + a * 0.2f, 0.42f, tip + sliver * Rand( rng, 1.2f, 2.2f ), 0.08f ) { Material = BoneMat, Tag = tag } );
			}
		}
		// The tongue: flat (4 x 1.2 in), out of the bottom of the hole, off-centre, hanging and
		// lapping onto the floor.
		var bottom = c - a * (half * 0.8f) - n * 1.5f;
		var off = tongueSide.LengthSquared > 0f ? tongueSide.Normal * 3f : Vec3.Zero;
		var path = new[] { bottom, bottom + n * 5f - a * 0.6f + off * 0.4f, bottom + n * 7.5f - a * 4f + off, new Vec3( bottom.x + 9f, bottom.y, 1.6f ) + off, new Vec3( bottom.x + 13f, bottom.y + 1.5f, 0.75f ) + off };
		for ( var i = 0; i < path.Length - 1; i++ )
			for ( var k = 0; k < 4; k++ )
			{
				var p = Vec3.Lerp( path[i], path[i + 1], k / 4f );
				var dir = (path[i + 1] - path[i]).Normal;
				var flatUp = Vec3.Cross( dir, Vec3.Cross( new Vec3( 0, 0, 1 ), dir ).LengthSquared > 1e-4f ? Vec3.Cross( new Vec3( 0, 0, 1 ), dir ).Normal : s ).Normal;
				var w = 2f - 0.45f * (i + k / 4f) / path.Length;
				L.Teeth.Add( new SdfEllipsoid( p, new Vec3( 1.3f, w, 0.6f ), new Xform( dir, Vec3.Cross( flatUp, dir ).Normal, flatUp, Vec3.Zero ) ) { Material = GumMat, Tag = tag + "_tongue" } );
			}
	}

	/// <summary>
	/// One gut, laid by hand rather than scattered: out of <paramref name="from"/>, down over the lip,
	/// onto the floor and round in three overlapping loops near <paramref name="floorCentre"/>, each
	/// lying on the last. Segmented every 2.2 in, with thin membrane strands between the loops.
	/// In a crisp layer, so the tube keeps its diameter instead of blending into a pillow.
	/// </summary>
	static void LaidGut( SdfBlend into, Random rng, Vec3 from, Vec3 outward, Vec3 floorCentre, float radius, string tag )
	{
		var key = new List<Vec3> { from, from + outward * 3.5f + new Vec3( 0, 0, -1.5f ) };
		var drop = from + outward * 5.5f;
		key.Add( new Vec3( drop.x, drop.y, MathF.Max( radius * 3f, from.z * 0.45f ) ) );
		var loopR = 4.2f;
		var startA = MathF.Atan2( drop.y - floorCentre.y, drop.x - floorCentre.x );
		for ( var q = 0; q <= 40; q++ )
		{
			var th = q / 40f * MathF.PI * 2f * 2.3f;
			var drift = floorCentre + new Vec3( th * 0.45f, -th * 0.55f, 0 );
			var r = loopR * (1f + 0.12f * MathF.Sin( th * 1.7f ));
			// Each pass lies on the one before where they cross.
			var z = radius + 2f * radius * Math.Clamp( (th - MathF.PI * 1.7f) / (MathF.PI * 0.5f), 0f, 1f ) * (0.6f + 0.4f * MathF.Sin( th ));
			key.Add( new Vec3( drift.x + r * MathF.Cos( startA + th ), drift.y + r * MathF.Sin( startA + th ), MathF.Max( z, radius * 0.85f ) ) );
		}
		// Resampled at a segment's length, alternating bulge and pinch.
		var pts = new List<Vec3> { key[0] };
		for ( var i = 1; i < key.Count; i++ )
		{
			var seg = key[i] - key[i - 1];
			var steps = Math.Max( 1, (int)MathF.Ceiling( seg.Length / (radius * 0.8f) ) );
			for ( var k = 1; k <= steps; k++ ) pts.Add( key[i - 1] + seg * (k / (float)steps) );
		}
		var rad = new List<float>();
		var along = 0f;
		for ( var i = 0; i < pts.Count; i++ )
		{
			if ( i > 0 ) along += (pts[i] - pts[i - 1]).Length;
			rad.Add( radius * (1f + 0.13f * MathF.Cos( along / 2.2f * MathF.PI * 2f )) );
		}
		into.Add( new SdfTube( pts, rad ) { Material = FleshMat, Tag = tag } );
		// Mesentery: thin strands between loops that lie near each other.
		for ( var k = 0; k < 5; k++ )
		{
			var i = rng.Next( pts.Count / 3, pts.Count - 8 );
			var best = -1; var bd = float.MaxValue;
			for ( var j = 0; j < pts.Count; j++ )
			{
				if ( Math.Abs( j - i ) < 18 ) continue;
				var d = (pts[j] - pts[i]).Length;
				if ( d > radius * 2.4f && d < bd ) { bd = d; best = j; }
			}
			if ( best < 0 || bd > radius * 5f ) continue;
			var mid = Vec3.Lerp( pts[i], pts[best], 0.5f ) + new Vec3( 0, 0, -0.4f );
			into.Add( new SdfTube( new[] { pts[i], mid, pts[best] }, new[] { 0.32f, 0.27f, 0.32f } ) { Material = GumMat, Tag = tag } );
		}
	}

	/// <summary>
	/// Choir's sculpt pass, done the way a sculptor would rather than as noise:
	/// - hard parts locked;
	/// - grown seams where the bodies fuse;
	/// - the loose flesh (belly, sag, core) and the skin along every seam buckled into real folds by
	///   the Cloth Expand brush (fabric grown in place folds itself);
	/// - each torso's anatomy creased in (pec undercurves, collarbones);
	/// - grain only on the busy 40%.
	/// </summary>
	static void ChoirSculpt( PolyMesh mesh, Sdf[] nearest, int seed, Frame[] torsos, (Vec3 Centre, Vec3 Radii)[] lobes )
	{
		bool Soft( Sdf leaf ) => leaf is not null && leaf.Material is SkinMat or FleshMat or ClothMat or FatMat or GumMat;
		string Tag( int v ) => nearest[v]?.Tag ?? "";
		var sc = new Sculptor( mesh ) { Mask = nearest.Select( l => Soft( l ) ? 1f : 0f ).ToArray() };

		// Seams between separate bodies (not a hand on its wrist or a head on its neck).
		var ids = new Dictionary<string, int>();
		var part = nearest.Select( l => Soft( l ) ? (ids.TryGetValue( l.Tag ?? "?", out var k ) ? k : ids[l.Tag ?? "?"] = ids.Count) : -1 ).ToArray();
		var tagOf = ids.OrderBy( kv => kv.Value ).Select( kv => kv.Key ).ToArray();
		bool Joint( string t ) => t.Contains( "hand", StringComparison.OrdinalIgnoreCase ) || t.StartsWith( "head", StringComparison.OrdinalIgnoreCase ) || t.EndsWith( "_collar" ) || t.EndsWith( "_spur" );
		var seams = SurfaceDetail.Seams( mesh, part, 9f, ( a, b ) => !Joint( tagOf[a] ) && !Joint( tagOf[b] ) );
		SurfaceDetail.GrownSeam( mesh, seams, new SeamOptions { Depth = 0.5f, Width = 0.6f, Roll = 0.45f, RollOffset = 1.2f, RollWidth = 1.0f, Sag = 0.6f, Seed = seed } );

		// Folds drawn the way a sculptor draws them: a crease, and a roll of flesh built up just above
		// it so it hangs over. A few per sag lobe, across its lower outward side, each one different.
		var rng = new Random( seed + 1 );
		var away = new Vec3( -5, 0, 0 );
		foreach ( var (c, r) in lobes )
		{
			var facing = MathF.Atan2( c.y - away.y, c.x - away.x );
			var count = 2 + rng.Next( 2 );
			for ( var k = 0; k < count; k++ )
			{
				var dz = -r.z * (0.15f + 0.3f * k + Rand( rng, -0.06f, 0.06f ));
				var shrink = MathF.Sqrt( MathF.Max( 0.05f, 1f - (dz / r.z) * (dz / r.z) ) );
				var half = Rand( rng, 0.7f, 1.2f );
				var mid = facing + Rand( rng, -0.4f, 0.4f );
				var line = new List<Vec3>();
				for ( var q = 0; q <= 8; q++ )
				{
					var a = mid - half + 2f * half * q / 8f;
					// Folds sag in the middle and wander.
					var sag = -0.35f * MathF.Sin( MathF.PI * q / 8f ) + Rand( rng, -0.1f, 0.1f );
					line.Add( c + new Vec3( r.x * MathF.Cos( a ) * shrink, r.y * MathF.Sin( a ) * shrink, dz + sag ) );
				}
				var depth = Rand( rng, 0.012f, 0.022f );
				sc.Stroke( BrushKind.Crease, line, 1.0f, depth * 3f );
				// A rounded roll (Blob, not a plane-filling clay: that makes shelves), dragged down over
				// the crease so it overhangs and shadows it, softened only along its top.
				var roll = line.Select( p => p + new Vec3( 0, 0, 0.7f ) ).ToList();
				sc.Stroke( BrushKind.Blob, roll, 1.1f, 0.05f );
				sc.Stroke( BrushKind.Grab, roll, 1.0f, 0.12f );
				foreach ( var p in roll ) sc.Dab( BrushKind.Grab, p, 1.2f, 0.6f, new Vec3( 0, 0, -0.6f ) );
				sc.Stroke( BrushKind.Smooth, roll.Select( p => p + new Vec3( 0, 0, 0.7f ) ).ToList(), 1.0f, 0.3f );
			}
		}
		// The crewman's belly hanging over his belt: a crease along the belt's top, the roll above it.
		{
			var t = torsos[0];
			var belt = Enumerable.Range( 0, 11 ).Select( q => { var a = -1.3f + 2.6f * q / 10f; return t.P( 6.6f * MathF.Cos( a ), 5.2f * MathF.Sin( a ), -14.1f ); } ).ToList();
			sc.Stroke( BrushKind.Crease, belt, 0.9f * t.Scale, 0.02f );
			var roll = belt.Select( p => p + t.Up * 0.9f ).ToList();
			sc.Stroke( BrushKind.Blob, roll, 1.1f * t.Scale, 0.035f );
			sc.Stroke( BrushKind.Smooth, roll, 1.4f * t.Scale, 0.35f );
		}

		// Anatomy creased in on each torso (torso frames: chest along Forward, neck along Up; body inches).
		foreach ( var t in torsos )
			foreach ( var side in new[] { 1f, -1f } )
			{
				// Under the pec, curving up into the armpit.
				sc.Stroke( BrushKind.Crease, new[] { t.P( 4.4f, 0.8f * side, 4.4f ), t.P( 4.2f, 2.8f * side, 3.8f ), t.P( 3.7f, 4.8f * side, 4.4f ), t.P( 2.9f, 6.2f * side, 5.9f ) }, 0.9f * t.Scale, 0.012f );
				// Along the collarbone.
				sc.Stroke( BrushKind.Crease, new[] { t.P( 3.2f, 0.9f * side, 12.4f ), t.P( 2.6f, 3.5f * side, 12.6f ), t.P( 1.4f, 6.2f * side, 12.2f ) }, 0.7f * t.Scale, 0.008f );
			}

		// A spine down the top of the rear mass (a fourth man's back, buried): vertebrae 1.5 in apart
		// along the ridge, and a shoulder blade either side at the top, so the back has a person in it.
		{
			var pos = mesh.Positions;
			var ridge = new List<Vec3>();
			for ( var x = -5f; x >= -25f; x -= 1.5f )
			{
				var y0 = 2f + (x + 5f) * 0.05f;
				var bestZ = float.MinValue; var best = Vec3.Zero;
				for ( var v = 0; v < pos.Count; v++ )
				{
					var q = pos[v];
					if ( MathF.Abs( q.x - x ) < 0.6f && MathF.Abs( q.y - y0 ) < 1.2f && q.z > bestZ && sc.Mask[v] > 0f && !Tag( v ).StartsWith( "head" ) && !Tag( v ).StartsWith( "spare" ) )
					{ bestZ = q.z; best = q; }
				}
				if ( bestZ > 4f ) ridge.Add( best );
			}
			sc.Stroke( BrushKind.Crease, ridge.Select( p => p + new Vec3( 0, 1.4f, 0 ) ).ToList(), 0.8f, 0.03f );
			sc.Stroke( BrushKind.Crease, ridge.Select( p => p + new Vec3( 0, -1.4f, 0 ) ).ToList(), 0.8f, 0.03f );
			for ( var i = 0; i < ridge.Count; i++ )
				sc.Dab( BrushKind.Blob, ridge[i], 1.0f + 0.25f * (i % 2), 0.55f );
			if ( ridge.Count > 4 )
			{
				var top = ridge[2];
				foreach ( var side in new[] { 1f, -1f } )
				{
					var blade = new[] { top + new Vec3( 1f, 4.5f * side, 0 ), top + new Vec3( -3f, 5.5f * side, 0 ), top + new Vec3( -6.5f, 4f * side, 0 ) };
					sc.Stroke( BrushKind.Blob, blade, 2.2f, 0.06f );
					sc.Stroke( BrushKind.Crease, blade.Select( p => p + new Vec3( -1.5f, 2f * side, 0 ) ).ToList(), 0.9f, 0.02f );
				}
			}
		}
		// Pustules: a cluster of small hard swellings on the rear mass, 1-3 in, not an even scatter.
		{
			var cand = Enumerable.Range( 0, mesh.VertexCount ).Where( v => Tag( v ) is "sag" or "lumps" && mesh.Positions[v].z > 6f && mesh.Positions[v].y > -2f ).ToList();
			if ( cand.Count > 0 )
			{
				var seedV = mesh.Positions[cand[rng.Next( cand.Count )]];
				var near = cand.Where( v => (mesh.Positions[v] - seedV).Length < 7f ).ToList();
				for ( var k = 0; k < 8 && near.Count > 0; k++ )
				{
					var r = Rand( rng, 0.5f, 1.4f );
					sc.Dab( BrushKind.Blob, mesh.Positions[near[rng.Next( near.Count )]], r, r * 0.7f );
				}
			}
		}

		var busy = SurfaceDetail.BusyMask( mesh, 0.4f, 0.06f, seed + 5 );
		SurfaceDetail.Grain( mesh, 0.05f, 0.35f, 1.2f, 0.03f, seed + 4, v => Soft( nearest[v] ) ? busy[v] : 0f );
	}

	static Sdf Choir( Options o )
	{
		var rng = new Random( o.Seed );
		var L = new Layers( 2.6f, 1.6f );

		// Three real men's torsos (the CC0 body's own sculpt: pecs, clavicles, abs, navel), fused
		// at the seams by the skin blend. A stands facing the player and carries the maw down its
		// sternum and the crewman on its own neck; B lies chest-up across the top-rear, howling;
		// C lies on its side along the right flank, its face smeared into the shoulder.
		var tA = new Frame( new Vec3( 13, -1, 26 ), new Vec3( 1, 0, -0.12f ), new Vec3( 0.12f, 0, 1 ), 1.3f );
		var tB = new Frame( new Vec3( -9, 5, 30 ), new Vec3( 0.5f, 0.05f, 0.86f ), new Vec3( -0.86f, 0.15f, 0.5f ), 1.2f );
		var tC = new Frame( new Vec3( 0, -10, 24 ), new Vec3( 0.15f, -0.95f, -0.1f ), new Vec3( 0.75f, -0.25f, 0.6f ), 1.15f );
		L.Skin.Add( HumanParts.RealTorso( tA, "torsoA" ) );
		L.Skin.Add( HumanParts.RealTorso( tB, "torsoB" ) );
		L.Skin.Add( HumanParts.RealTorso( tC, "torsoC", neck: 1.5f ) );
		// The weight: a sag of fused flesh under the rear, hanging to 5 in off the floor.
		// in drooping lobes, the lowest resting on the floor on a squashed pad, strings of tissue down.
		var lobes = new[] { (new Vec3( -14, 4, 9 ), new Vec3( 9, 7.5f, 4.8f )), (new Vec3( -20, -2, 7 ), new Vec3( 7.5f, 7, 4.5f )), (new Vec3( -9, -5, 10 ), new Vec3( 7, 6.5f, 4.2f )), (new Vec3( -17, 8, 6 ), new Vec3( 6.5f, 6, 4f )) };
		foreach ( var (c, r) in lobes )
			L.Flesh.Add( new SdfEllipsoid( c, r ) { Material = FleshMat, Tag = "sag" } );
		L.Flesh.Add( new SdfEllipsoid( new Vec3( -17, 2, 1.4f ), new Vec3( 9, 7, 1.6f ) ) { Material = FleshMat, Tag = "sag" } );
		foreach ( var (a0, b0) in new[] { (new Vec3( -11, 3, 7 ), new Vec3( -9.5f, 4, 0.4f )), (new Vec3( -23, -1, 5 ), new Vec3( -25, -2, 0.4f )), (new Vec3( -13, -7, 9 ), new Vec3( -12, -9.5f, 0.4f )) } )
			L.Flesh.Add( new SdfTube( new[] { a0, Vec3.Lerp( a0, b0, 0.5f ) + new Vec3( 0.4f, 0, 0 ), b0 }, new[] { 0.7f, 0.35f, 0.6f } ) { Material = FleshMat, Tag = "sag" } );
		L.Flesh.Add( new SdfEllipsoid( new Vec3( -2, 0, 20 ), new Vec3( 12, 11, 9 ) ) { Material = FleshMat, Tag = "core" } );

		Lumps( L.Flesh, rng, new Vec3( -16, -4, 26 ), 4, 6, 2.5f, 4.5f, FleshMat, -1, "lumps" );

		// Four arms and two legs, each a different length, two elbows bent the wrong way.
		var limbs = new (Vec3 root, float yaw, bool arm, float upLen, float lift, bool wrong, string name)[]
		{
			(tA.P( -0.5f, 8.3f, 10.5f ), 38f, true, 1.0f, 0f, false, "leg1"),
			(tA.P( -0.5f, -8.3f, 10.5f ), -44f, true, 0.88f, 6f, true, "leg2"),
			(new Vec3( 3, 14, 27 ), 95f, true, 1.12f, 0f, false, "leg3"),
			(new Vec3( 2, -14, 26 ), -86f, true, 0.95f, 0f, true, "leg4"),
		};

		foreach ( var (root, yaw, arm, upLen, lift, wrong, name) in limbs )
		{
			var outward = Rotate( new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), yaw );
			L.Skin.Add( new SdfSphere( root - outward * 2f, 3.4f ) { Material = SkinMat, Tag = name } );       // buried shoulder
			// Upper arm out and a little down, forearm down to the floor: the elbow about halfway along
			// the limb, so neither bone is stretched much past twice its length and the arm keeps its
			// biceps, its elbow and its forearm taper instead of becoming a pipe.
			var thin = name is "leg1" or "leg3";
			var girth = thin ? 1.15f : 1.6f;
			var up = (outward * 0.92f + new Vec3( 0, 0, wrong ? -0.15f : -0.35f )).Normal;
			var elbow = root + up * 21f * upLen * (thin ? 1.08f : 1f);
			var down = wrong ? (outward * 0.45f + new Vec3( 0, 0, -1f )).Normal : (outward * 0.15f + new Vec3( 0, 0, -1f )).Normal;
			var wrist = elbow + down * ((elbow.z - (2.6f + lift)) / MathF.Max( -down.z, 0.2f ));
			var isLeft = outward.y > 0f;
			L.Skin.Add( HumanParts.RealArmSegment( true, isLeft, root, elbow, girth, name ) );
			L.Skin.Add( HumanParts.RealArmSegment( false, isLeft, elbow, wrist, girth, name, roll: wrong ? 180f : 0f ) );
			var end = new Frame( wrist, down, new Vec3( 0, 0, 1 ) );
			var handDir = Rotate( outward, new Vec3( 0, 0, 1 ), Rand( rng, -30f, 30f ) );
			var hf = new Frame( new Vec3( end.Origin.x, end.Origin.y, 1.9f + lift ), handDir, new Vec3( 0, 0, 1 ), 1.5f );
			L.Skin.Add( HandAtWrist( rng, hf, name + "_hand", fingerStretch: 1.1f, stub: false ) );
			// The olecranon: a 2 in bump out the back of the bend, and one bone spur grown out of it —
			// solid, 4 in, flattened, swept back and up, through a torn collar. One piece, nothing floating.
			var outBack = ((elbow - root).Normal + (elbow - wrist).Normal).Normal;
			L.Skin.Add( new SdfEllipsoid( elbow + outBack * 0.8f * girth, new Vec3( 1.6f, 1.6f, 1.9f ) * girth ) { Material = SkinMat, Tag = name } );
			var spurDir = (outBack * 0.7f + new Vec3( 0, 0, 0.45f ) - up * 0.3f).Normal;
			var spurRoot = elbow + outBack * 1.6f * girth;
			L.Hard.Add( new SdfRoundCone( spurRoot, 0.75f * MathF.Max( girth, 1.1f ), spurRoot + spurDir * 4f, 0.2f ) { Material = BoneMat, Tag = name + "_spur" } );
			var flatAxis = Vec3.Cross( spurDir, outBack ).LengthSquared > 1e-6f ? Vec3.Cross( spurDir, outBack ).Normal : Vec3.Cross( spurDir, new Vec3( 0, 0, 1 ) ).Normal;
			L.Flesh.Add( new SdfEllipsoid( spurRoot + spurDir * 0.3f, new Vec3( 1.4f, 1.4f, 0.6f ) * MathF.Max( girth, 1.1f ), new Xform( Vec3.Cross( spurDir, flatAxis ).Normal, flatAxis, spurDir, Vec3.Zero ) ) { Material = FleshMat, Tag = name + "_collar" } );
			// A tendon ridge down the back of the forearm.
			var tendonSide = Vec3.Cross( (wrist - elbow).Normal, flatAxis ).Normal;
			L.Skin.Add( new SdfTube( new[] { elbow + tendonSide * 1.3f * girth, Vec3.Lerp( elbow, wrist, 0.5f ) + tendonSide * 1.15f * girth, wrist + tendonSide * 0.8f * girth }, new[] { 0.45f * girth, 0.35f * girth, 0.25f * girth } ) { Material = SkinMat, Tag = name } );
			// The wrist: forearm end down into the planted hand.
			L.Skin.Add( new SdfRoundCone( wrist + (elbow - wrist).Normal * 2.5f, 1.25f * girth, hf.Origin + new Vec3( 0, 0, 0.6f ), 1.05f * girth ) { Material = SkinMat, Tag = name } );
		}

		// A man's legs under the rear, in his work trousers, crouched deep under the weight: thighs
		// forward under the belly, knees jutting, shins back down to boots planted flat.
		foreach ( var (side, name) in new[] { (1f, "leg5"), (-1f, "leg6") } )
		{
			var hip = new Vec3( -18, 8.5f * side, 22 );
			var knee = hip + new Vec3( 0.8f, 0.12f * side, -0.55f ).Normal * 15.5f;
			var ankle = new Vec3( -17.5f, 9.5f * side, 5.2f );
			L.Skin.Add( HumanParts.RealLegSegment( true, side > 0, hip, knee, 1.15f, name, trousers: 0.45f, material: ClothMat ) );
			L.Skin.Add( HumanParts.RealLegSegment( false, side > 0, knee, ankle, 1.15f, name, trousers: 0.4f, material: ClothMat ) );
			L.Skin.Add( new SdfSphere( hip, 4.4f ) { Material = ClothMat, Tag = name } );
			L.Hard.Add( Boot( new Frame( new Vec3( ankle.x + 1.2f, ankle.y, 0 ), new Vec3( 1, 0.1f * side, 0 ), new Vec3( 0, 0, 1 ), 1.1f ), name + "_boot" ) );
		}

		// The heads sit on their own torsos' necks.
		var headA = HumanParts.SeatHead( tA.P( 0.9f, 0, 16f ), new Vec3( 1, -0.05f, -0.3f ), new Vec3( 0.3f, 0, 1 ), 1.05f );
		L.Skin.Add( FatedHead( rng, headA, "headA", HeadFate.Intact, neckLength: 0.4f, jawDegrees: 18f ) );
		L.Hard.Add( HardHat( new Frame( headA.P( 0, 0, 0.3f ), headA.Forward, Rotate( headA.Up, headA.Forward, 18f ), 1.05f ), "hat" ) );
		// B howls at the ceiling, jaw open to the rear so it reads from behind as well.
		var headB = HumanParts.SeatHead( tB.P( 0.9f, 0, 16f ), new Vec3( -0.2f, 0.1f, 1 ), new Vec3( -1, 0, -0.2f ), 1.05f );
		L.Skin.Add( FatedHead( rng, headB, "headB", HeadFate.Howl, neckLength: 0.4f ) );
		// C half-sunk: seated 2.5 in down into its shoulder.
		// Turned 30° outward so the face reads from the side.
		var headC = HumanParts.SeatHead( tC.P( 0.9f, 0, 14.5f ) - tC.Up * 3.5f, Rotate( new Vec3( 0.75f, -0.65f, 0.05f ), new Vec3( 0.1f, 0.3f, 1 ), -30f ), new Vec3( 0.1f, 0.3f, 1 ), 1.0f );
		L.Skin.Add( FatedHead( rng, headC, "headC", HeadFate.Smear, neckLength: 0.4f, jawDegrees: 16f ) );

		// The spare arm with a clenched fist out of the top.
		// The spare arm out of the top, reaching: the body's own arm, the hand clawing open.
		{
			var sRoot = new Vec3( -2, -3, 42 );
			var sElbow = sRoot + new Vec3( -0.2f, -0.3f, 1f ).Normal * 12f;
			var sWrist = sElbow + new Vec3( 0.55f, -0.15f, 0.8f ).Normal * 11f;
			L.Skin.Add( HumanParts.RealArmSegment( true, false, sRoot, sElbow, 1.2f, "spareArm" ) );
			L.Skin.Add( HumanParts.RealArmSegment( false, false, sElbow, sWrist, 1.2f, "spareArm" ) );
			L.Skin.Add( new SdfSphere( sRoot, 3.2f ) { Material = SkinMat, Tag = "spareArm" } );
			var sDir = (sWrist - sElbow).Normal;
			L.Skin.Add( HandAtWrist( rng, new Frame( sWrist + sDir * 1.0f, sDir, new Vec3( -0.6f, 0.1f, 0.4f ), 1.3f ), "spareHand", fingerStretch: 1.25f, spread: 1.35f, left: false ) );
		}

		// The maw: down the front of torso A, under headA.
		// The maw: torsoA split from sternum to navel.
		var mawC = tA.P( 4.6f, 0, -1.5f );
		TornChest( L, rng, mawC, tA.Forward, tA.Up, 20f, "maw", tongueSide: new Vec3( 0, -1, 0 ) );

		// The medium tier, between the torsos and the small detail: a gut spilling from the bottom of
		// the split into a pile on the floor, and two tumorous lumps where the torsos fuse.
		LaidGut( L.Teeth, rng, mawC - tA.Up * 6.5f - tA.Forward * 1.5f, tA.Forward, new Vec3( mawC.x + 9f, mawC.y + 3f, 0 ), 1.4f, "mawGut" );
		L.Flesh.Add( new SdfEllipsoid( new Vec3( 6.5f, -9.5f, 29f ), new Vec3( 4.2f, 3.6f, 3.3f ) ) { Material = FleshMat, Tag = "tumour" } );
		L.Flesh.Add( new SdfEllipsoid( new Vec3( 2f, 6f, 37f ), new Vec3( 3.6f, 3.2f, 3.4f ) ) { Material = FleshMat, Tag = "tumour" } );

		// Under the split: his belly sags over his belt, and one trouser leg is folded under him,
		// knee on the floor and the boot sole-up behind it. The front stands on something now.
		L.Skin.Add( new SdfEllipsoid( tA.P( 2.2f, 0, -12.5f ), new Vec3( 6.5f, 9.5f, 5f ), tA.Rotation ) { Material = SkinMat, Tag = "belly" } );
		RibArc( L.Skin, tA.P( 0.2f, 0, -15.2f ), tA.Up, tA.Forward, 6.6f * 1.3f, 5.2f * 1.3f, 0, 360, 1.1f, ClothMat, "belt", 28 );
		L.Skin.Add( new SdfEllipsoid( tA.P( 0.4f, 0, -18f ), new Vec3( 6.2f, 8.6f, 4.2f ) * 1.3f, tA.Rotation ) { Material = ClothMat, Tag = "trousers" } );
		{
			var hipK = tA.P( 1f, -4.5f, -18.5f );
			var kneeK = new Vec3( hipK.x + 11f, hipK.y - 1.5f, 3.4f );
			var ankleK = new Vec3( hipK.x - 3f, hipK.y - 2.5f, 3.2f );
			L.Skin.Add( HumanParts.RealLegSegment( true, false, hipK, kneeK, 1.15f, "kneelLeg", trousers: 0.5f, material: ClothMat ) );
			L.Skin.Add( HumanParts.RealLegSegment( false, false, kneeK, ankleK, 1.15f, "kneelLeg", trousers: 0.45f, material: ClothMat ) );
			// The boot lies on its side behind, sole toward the viewer's back.
			// The boot on its side behind, its sole turned to the front so it reads as a boot, a trouser
			// cuff bunched over the ankle.
			L.Hard.Add( Boot( new Frame( ankleK + new Vec3( -3.0f, -1.5f, -0.6f ), new Vec3( 0, -1, 0.1f ), new Vec3( -1, 0, 0.25f ), 1.1f ), "kneelBoot" ) );
			RibArc( L.Skin, ankleK + new Vec3( -1.2f, -0.6f, 0 ), new Vec3( -1, -0.3f, 0 ).Normal, new Vec3( 0, 0, 1 ), 2.6f, 2.6f, 0, 360, 0.9f, ClothMat, "kneelLeg", 16 );
		}

		// Guts: hanging out of the rear underside to a spill on the floor, offset to one side,
		// and one rope out behind between the legs.
		var spill = new Vec3( -14, 6, 0 );
		L.Flesh.Add( new SdfEllipsoid( spill + new Vec3( 0, 0, 1.5f ), new Vec3( 13, 9, 2.6f ) ) { Material = FleshMat, Tag = "spill" } );
		GutCoils( L.Flesh, rng, spill + new Vec3( 0, 0, 4 ), 7f, 9, 1.5f, "spill" );
		HangingGut( L.Flesh, rng, new Vec3( -10, 3, 12 ), spill + new Vec3( 4, -2, 2 ), 1.5f, "hang1" );
		HangingGut( L.Flesh, rng, new Vec3( -15, 8, 11 ), spill + new Vec3( -3, 3, 2 ), 1.4f, "hang2" );
		HangingGut( L.Flesh, rng, new Vec3( -5, -4, 14 ), spill + new Vec3( 6, -8, 2 ), 1.3f, "hang3" );

		// Vertebrae torn up through the skin of torsoA's back: three spurs of bone, a raw collar each.
		foreach ( var (z, len) in new[] { (2f, 7f), (6.5f, 9f), (11f, 6f) } )
		{
			var root0 = tA.P( -5.2f, 0, z );
			var d = (-tA.Forward * 0.8f + tA.Up * 0.6f).Normal;
			L.Hard.Add( new SdfRoundCone( root0, 1.6f, root0 + d * len * 1.15f + tA.Left * 0.4f, 0.3f ) { Material = BoneMat, Tag = "spurs" } );
			L.Flesh.Add( new SdfEllipsoid( root0 + d * 1.0f, new Vec3( 3.2f, 3.2f, 1.6f ) ) { Material = FleshMat, Tag = "spurs" } );
		}

		// The surface pass: seams grown where the men meet, the belly and sag hanging in folds.
		// The sculpt pass, with the kernel's own brushes on the voxel skin (see ChoirSculpt).
		Detail = ( mesh, nearest ) => ChoirSculpt( mesh, nearest, o.Seed, new[] { tA, tB, tC }, lobes );

		return L.Assemble( o.Seed, fleshAmp: 1.1f );
	}

	// --- STALK: a man on all fours with his head four feet away --------------------------------

	static Sdf Stalk( Options o )
	{
		var rng = new Random( o.Seed );
		var L = new Layers( 2.4f, 1.4f );

		// The man, crawling, shoulders raised: ribcage clearly between the forelegs.
		var chestC = new Vec3( 9, 0, 33 );
		var chestR = new Vec3( 12.5f, 8.4f, 7.6f );
		var chestRot = Xform.Rotate( new Vec3( 0, 1, 0 ), 0.16f );
		L.Skin.Add( new SdfEllipsoid( chestC, chestR, chestRot ) { Material = SkinMat, Tag = "torso" } );
		L.Skin.Add( new SdfEllipsoid( new Vec3( 19, 0, 35 ), new Vec3( 5, 9, 5.6f ) ) { Material = SkinMat, Tag = "shoulders" } );
		L.Skin.Add( new SdfRoundCone( new Vec3( 0, 0, 31 ), 6.6f, new Vec3( -7, 0, 28 ), 6.2f ) { Material = SkinMat, Tag = "waist" } );
		RibsOn( L.Skin, chestC, chestR, chestRot, 8, -7f, 8f, 35f, 150f, 0.6f, 0.5f, "ribs" );
		RibsOn( L.Skin, chestC, chestR, chestRot, 8, -7f, 8f, -150f, -35f, 0.6f, 0.5f, "ribs" );
		L.Skin.Add( new SdfCapsule( chestC + new Vec3( 8, 0, -7.3f ), chestC + new Vec3( -4, 0, -7.6f ), 0.7f ) { Material = SkinMat, Tag = "sternum" } );
		foreach ( var side in new[] { 1f, -1f } )
			L.Skin.Add( new SdfEllipsoid( new Vec3( 14, 4.8f * side, 40.4f ), new Vec3( 4.0f, 3.0f, 1.2f ), Xform.Rotate( new Vec3( 1, 0, 0 ), 0.28f * side ) ) { Material = SkinMat, Tag = "scapula" } );
		Spine( L.Skin, new[] { new Vec3( 23, 0, 39 ), new Vec3( 11, 0, 41.2f ), new Vec3( -1, 0, 39.5f ), new Vec3( -10, 0, 33 ) }, 1.9f, 1.0f, "spine" );
		L.Skin.Add( new SdfEllipsoid( new Vec3( -8, 0, 28 ), new Vec3( 6, 8, 6 ) ) { Material = ClothMat, Tag = "pelvis" } );

		// The sack: 15% bigger, taut, sitting on the floor on a flattened patch behind the pelvis.
		var sack = new Vec3( -27, 0, 16 );
		var sackR = new Vec3( 19, 15.5f, 15.5f );
		L.Flesh.Add( new SdfEllipsoid( sack, sackR ) { Material = FleshMat, Tag = "sack" } );
		L.Flesh.Add( new SdfEllipsoid( sack + new Vec3( 0, 0, -12 ), new Vec3( 15, 12, 4 ) ) { Material = FleshMat, Tag = "sack" } );   // contact patch
		L.Flesh.Add( new SdfEllipsoid( sack + new Vec3( -9, 6, 7 ), new Vec3( 10, 8, 8 ) ) { Material = FleshMat, Tag = "sack" } );
		StretchFolds( L.Skin, rng, sack, sackR, new Vec3( -12, 0, 28 ), 5, 14f, 1.0f, "sackFolds" );
		for ( var i = 0; i < 7; i++ )
		{
			var a = Rand( rng, -1.2f, 1.2f );
			var p0 = new Vec3( -12, MathF.Sin( a ) * 6, 26 + MathF.Cos( a ) * 3 );
			var mid = sack + new Vec3( Rand( rng, -4, 4 ), MathF.Sin( a ) * sackR.y * 0.95f, MathF.Cos( a ) * sackR.z * 0.95f );
			var end = sack + new Vec3( -sackR.x * 0.85f, MathF.Sin( a * 1.3f ) * sackR.y * 0.6f, MathF.Cos( a * 1.3f ) * sackR.z * 0.6f );
			L.Skin.Add( new SdfTube( new[] { p0, mid, end }, new[] { 0.6f, 0.5f, 0.3f } ) { Material = GumMat, Tag = "veins" } );
		}

		// The tear and its guts: two short loops hanging out, one rope coiling back on the floor.
		L.Cuts.Add( new SdfEllipsoid( sack + new Vec3( 10, -6, -9 ), new Vec3( 5, 3.5f, 3 ) ) { Material = CavityMat, Tag = "tear" } );
		HangingGut( L.Flesh, rng, sack + new Vec3( 10, -6, -9 ), new Vec3( -10, -14, 2 ), 1.5f, "loop1" );
		HangingGut( L.Flesh, rng, sack + new Vec3( 9, -4, -10 ), new Vec3( -6, -10, 2 ), 1.4f, "loop2" );
		GutLoop( L.Flesh, rng, new Vec3( -10, -14, 0 ), new Vec3( -1, -0.4f, 0 ), 40f, 1.5f, "rope" );

		// Hind legs kneeling, knees on the floor, boots soles-up and visible behind.
		foreach ( var side in new[] { 1f, -1f } )
		{
			var hip = new Vec3( -8, 6.8f * side, 26 );
			var knee = AnatomicalLimb( L.Skin, hip, new List<(Vec3, float, float, float)> { (new Vec3( 0.55f, 0.2f * side, -1f ), 21f, 4.2f, 2.8f) }, ClothMat, side > 0 ? "hindL" : "hindR", endZ: 3f );
			var ankle = AnatomicalLimb( L.Skin, knee.Origin, new List<(Vec3, float, float, float)> { (new Vec3( -1f, 0.25f * side, 0.05f ), 18f, 3.1f, 2.0f) }, ClothMat, side > 0 ? "hindL" : "hindR" );
			L.Hard.Add( Boot( new Frame( ankle.Origin + new Vec3( 0, 0, 0.5f ), new Vec3( -0.25f, 0.1f * side, 1 ), new Vec3( 1, 0, 0.25f ), 1.0f ), side > 0 ? "bootL" : "bootR" ) );
		}

		// Forelegs: mantis arms, short upper arm, elbow high above the shoulder, long forearm,
		// an extra backward hinge 10 in above the wrist. Angled out, asymmetric, one mid-step.
		foreach ( var side in new[] { 1f, -1f } )
		{
			var left = side > 0;
			var root = new Vec3( 20, 8.6f * side, 35 );
			L.Skin.Add( new SdfSphere( root - new Vec3( 0, 1.5f * side, 0 ), 3.4f ) { Material = SkinMat, Tag = "deltoid" } );
			var segs = new List<(Vec3, float, float, float)>
			{
				(new Vec3( 0.15f, 0.45f * side, 1f ), left ? 16f : 13f, 3.2f, 2.2f),
				(new Vec3( 0.45f, 0.35f * side, -1f ), 30f, 2.9f, 1.6f),
				(new Vec3( 0.5f, 0.1f * side, -1f ), 12f, 1.9f, 1.25f),
			};
			var lift = left ? 0f : 6f;
			var end = AnatomicalLimb( L.Skin, root, segs, SkinMat, left ? "foreL" : "foreR", endZ: 2.6f + lift, sag: 0.3f );
			var wrist = lift > 0f ? end.Origin - new Vec3( 0, 0, 0.6f ) : new Vec3( end.Origin.x, end.Origin.y, 1.4f );
			var handFwd = lift > 0f ? (end.Forward + new Vec3( 0.4f, 0, 0 )).Normal : new Vec3( 1, 0.3f * side, 0 ).Normal;
			L.Skin.Add( HandAtWrist( rng, new Frame( wrist, handFwd, new Vec3( 0, 0, 1 ), 1.15f ), left ? "handL" : "handR", fingerStretch: 1.35f, left: left ) );
		}

		// The neck: 7.5 in at the shoulders tapering to 4.5 in, rising 30 degrees for 20 in
		// then drooping, the face hanging 20 in off the floor looking at the player.
		var neckPts = new List<Vec3>();
		var neckR = new List<float>();
		var np = new Vec3( 24, 0, 37 );
		var nd = Rotate( new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), -30f );
		for ( var i = 0; i <= 14; i++ )
		{
			var t = i / 14f;
			neckPts.Add( np );
			neckR.Add( 3.75f - 1.5f * t );
			if ( i >= 5 ) nd = Rotate( nd, new Vec3( 0, 1, 0 ), 8.5f );
			np += nd * 3.0f;
		}
		L.Skin.Add( new SdfTube( neckPts, neckR ) { Material = SkinMat, Tag = "neck" } );
		var sideN = new Vec3( 0, 1, 0 );
		for ( var sgn = -1; sgn <= 1; sgn += 2 )
		{
			var cord = neckPts.Select( ( q, i ) =>
			{
				var tan = (neckPts[Math.Min( i + 1, neckPts.Count - 1 )] - neckPts[Math.Max( i - 1, 0 )]).Normal;
				var down = Vec3.Cross( sideN, tan ).Normal;
				var twist = (float)i / neckPts.Count * MathF.PI * 0.5f;
				return q + (sideN * MathF.Cos( twist ) * sgn + down * MathF.Sin( twist ) * 0.5f) * neckR[i] * 0.72f;
			} ).ToArray();
			L.Skin.Add( new SdfTube( cord, neckR.Select( r => 0.62f ).ToArray() ) { Material = SkinMat, Tag = "neckCord" } );
		}
		for ( var i = 1; i < neckPts.Count - 1; i++ )
		{
			var tan = (neckPts[i + 1] - neckPts[i - 1]).Normal;
			var down = Vec3.Cross( sideN, tan ).Normal;
			L.Skin.Add( new SdfSphere( neckPts[i] + down * neckR[i] * 0.85f, 0.55f ) { Material = SkinMat, Tag = "trachea" } );
		}

		var headDir = (neckPts[^1] - neckPts[^2]).Normal;
		var faceDir = new Vec3( 1, 0, 0.35f ).Normal;   // up toward the player, whatever the neck is doing
		// Seated so the head's own neck root (-1.25, 0, -4.7 in head inches) lands on the tube's end,
		// or the tube's end cap shows under the chin as a ball.
		var hfUp = new Vec3( -0.35f, 0, 1 ).Normal;
		var hf = new Frame( neckPts[^1] - (faceDir * -1.25f + hfUp * -4.7f) * 1.1f, faceDir, hfUp, 1.1f );
		L.Skin.Add( FatedHead( rng, hf, "head", HeadFate.Howl, neckLength: 0.3f ) );

		// head2: top-front of the right shoulder, visible from the front, eyes fused shut.
		var h2 = new Frame( new Vec3( 22, -9.5f, 41 ), new Vec3( 1, -0.45f, 0.4f ), new Vec3( -0.3f, 0, 1 ), 0.75f );
		L.Skin.Add( FatedHead( rng, h2, "head2", HeadFate.Droop ) );
		RibArc( L.Skin, h2.P( -1.8f, 0, 0 ), h2.Forward, h2.Up, 4.0f, 4.3f, 0, 360, 0.7f, SkinMat, "head2_collar", 16 );

		return L.Assemble( o.Seed, fleshAmp: 1.0f );
	}

	// --- BLOOM: a man still standing, his chest opened like a flower --------------------------

	static Sdf Bloom( Options o )
	{
		var rng = new Random( o.Seed );
		var L = new Layers( 2.0f, 1.3f );

		var hips = new Vec3( 0, 0, 38 );

		// Legs, kneecaps under the cloth, diagonal trouser folds, boots with the cuff tucked in.
		foreach ( var side in new[] { 1f, -1f } )
		{
			var root = hips + new Vec3( 0, 4.6f * side, -1 );
			var end = AnatomicalLimb( L.Skin, root, LegSegments( new Vec3( 0.05f, 0.08f * side, -1f ), new Vec3( -0.06f, 0.02f * side, -1f ), 1.0f ), ClothMat, side > 0 ? "legL" : "legR", endZ: 7.5f );
			L.Skin.Add( new SdfEllipsoid( root + new Vec3( 2.6f, 0.5f * side, -17.8f ), new Vec3( 1.0f, 1.9f, 2.2f ) ) { Material = ClothMat, Tag = "kneecap" } );
			for ( var f = 0; f < 4; f++ )
			{
				var z = -15f - f * 2.2f;
				var a = root + new Vec3( 2.2f, -1.8f * side, z );
				var b = root + new Vec3( 0.5f, 3.2f * side, z + 4f );
				L.Skin.Add( new SdfCapsule( a, b, 0.42f ) { Material = ClothMat, Tag = "trouserFold" } );
			}
			for ( var f = 0; f < 3; f++ )
			{
				var z = 9.8f + f * 1.4f;
				L.Skin.Add( new SdfCapsule( new Vec3( end.Origin.x + 2.4f, end.Origin.y - 1.5f * side, z ), new Vec3( end.Origin.x - 1.5f, end.Origin.y + 2.2f * side, z + 0.6f ), 0.4f ) { Material = ClothMat, Tag = "bunch" } );
			}
			L.Hard.Add( Boot( new Frame( new Vec3( end.Origin.x - 1.6f, end.Origin.y, 0 ), new Vec3( 1, 0.08f * side, 0 ), new Vec3( 0, 0, 1 ), 1.0f ), side > 0 ? "bootL" : "bootR" ) );
		}

		// Pelvis, buttocks, a belt flush with the waist, the belly over it.
		L.Skin.Add( new SdfEllipsoid( hips + new Vec3( 0, 0, 1 ), new Vec3( 6.5f, 8.2f, 5.5f ) ) { Material = ClothMat, Tag = "pelvis" } );
		foreach ( var side in new[] { 1f, -1f } )
			L.Skin.Add( new SdfSphere( hips + new Vec3( -3.8f, 3.6f * side, -0.5f ), 4.6f ) { Material = ClothMat, Tag = "buttock" } );
		Ring( L.Hard, hips + new Vec3( 0.2f, 0, 4.2f ), new Vec3( 0.08f, 0, 1 ), 6.35f, 0.55f, BootMat, "belt", 7.8f );
		L.Hard.Add( new SdfEllipsoid( hips + new Vec3( 6.6f, 0, 4.2f ), new Vec3( 0.4f, 1.2f, 0.9f ) ) { Material = BoneMat, Tag = "buckle" } );
		var belly = hips + new Vec3( 4.0f, 0, 7.5f );
		L.Skin.Add( new SdfEllipsoid( belly, new Vec3( 6.5f, 8f, 6.2f ) ) { Material = SkinMat, Tag = "belly" } );
		L.Cuts.Add( new SdfSphere( belly + new Vec3( 6.3f, 0, -1.2f ), 0.5f ) { Material = CavityMat, Tag = "navel" } );

		// The chest: intact shoulders and collarbones above, the front split open below them.
		var chest = hips + new Vec3( 1, 0, 17 );
		var chestR = new Vec3( 7.2f, 9.2f, 8.5f );
		L.Skin.Add( new SdfEllipsoid( chest, chestR ) { Material = SkinMat, Tag = "chest" } );
		var shoulderL = hips + new Vec3( -0.5f, 10.5f, 23 );
		var shoulderR = hips + new Vec3( -0.5f, -10.5f, 23 );
		L.Skin.Add( new SdfRoundCone( shoulderL, 4.0f, shoulderR, 4.0f ) { Material = SkinMat, Tag = "shoulders" } );
		foreach ( var side in new[] { 1f, -1f } )
			L.Skin.Add( new SdfCapsule( hips + new Vec3( 5.6f, 1.2f * side, 24.5f ), hips + new Vec3( 2.5f, 9.5f * side, 24.8f ), 0.75f ) { Material = SkinMat, Tag = "clavicle" } );
		Spine( L.Skin, new[] { hips + new Vec3( -6.8f, 0, 27 ), hips + new Vec3( -7.2f, 0, 12 ) }, 1.9f, 0.85f, "spine" );
		foreach ( var side in new[] { 1f, -1f } )
			L.Skin.Add( new SdfEllipsoid( hips + new Vec3( -6.2f, 5.2f * side, 21 ), new Vec3( 1.3f, 3.6f, 4.6f ), Xform.Rotate( new Vec3( 0, 0, 1 ), 0.3f * side ) ) { Material = SkinMat, Tag = "scapula" } );

		// The opening: 16 in tall, 10 wide, on the front of the chest, a hole in shadow with a
		// spine column at the back wall, lung lobes and coils spilling over the lower lip.
		var O = chest + new Vec3( 6.4f, 0, -0.5f );
		L.Cuts.Add( new SdfEllipsoid( O + new Vec3( -1.5f, 0, 0 ), new Vec3( 6.5f, 5.6f, 8.2f ) ) { Material = CavityMat, Tag = "cavity" } );
		Spine( L.Teeth, new[] { O + new Vec3( -7.2f, 0, -7 ), O + new Vec3( -7.6f, 0, 7 ) }, 1.8f, 1.2f, "vertebrae", BoneMat );
		foreach ( var side in new[] { 1f, -1f } )
			L.Teeth.Add( new SdfEllipsoid( O + new Vec3( -4.5f, 3.1f * side, 0.5f ), new Vec3( 2.4f, 1.6f, 4.4f ) ) { Material = FleshMat, Tag = "lung" } );
		GutCoils( L.Teeth, rng, O + new Vec3( -2.5f, 0, -4.5f ), 3.0f, 6, 1.0f, "innards" );
		HangingGut( L.Flesh, rng, O + new Vec3( 0.5f, 1.5f, -6.5f ), hips + new Vec3( 7.5f, 2.5f, 5.5f ), 1.0f, "spill" );

		// The split: the ribcage broken down the sternum and both halves swung open on the
		// flanks like doors, ribs and intercostal muscle intact, teeth along the free edges
		// where the sternum was. The cavity behind is in plain view.
		foreach ( var side in new[] { 1f, -1f } )
		{
			var open = side > 0 ? 112f : 98f;          // not a mirror
			var ribLen = side > 0 ? 1.0f : 0.9f;
			var ribs = new List<List<Vec3>>();
			for ( var k = 0; k < 6; k++ )
			{
				var z = -6.5f + k * 2.5f;
				var lenK = (9.5f + 3.2f * MathF.Sin( (k + 0.5f) / 6f * MathF.PI )) * ribLen;
				var hinge = O + new Vec3( -3.2f, 4.6f * side, z * 1.0f );
				var d = Rotate( new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), open * side * 0.55f );
				var pts = new List<Vec3>();
				var p = hinge;
				for ( var i = 0; i <= 8; i++ )
				{
					pts.Add( p );
					d = Rotate( d, new Vec3( 0, 0, 1 ), open * side * 0.055f );   // keeps curling back
					d = (d + new Vec3( 0, 0, (k - 2.5f) * 0.03f )).Normal;          // fan open a little
					p += d * lenK / 8f;
				}
				ribs.Add( pts );
				L.Teeth.Add( new SdfTube( pts, pts.Select( ( _, i ) => 0.72f - i * 0.03f ).ToArray() ) { Material = BoneMat, Tag = side > 0 ? "ribsL" : "ribsR" } );
			}

			// Intercostal muscle between neighbouring ribs, thick and raw.
			for ( var k = 0; k + 1 < ribs.Count; k++ )
				for ( var i = 0; i < 8; i++ )
				{
					var a0 = ribs[k][i];
					var a1 = ribs[k][i + 1];
					var b0 = ribs[k + 1][i];
					var b1 = ribs[k + 1][i + 1];
					var c = (a0 + a1 + b0 + b1) * 0.25f;
					var along = ((a1 - a0) + (b1 - b0)).Normal;
					var across = (b0 - a0).Normal;
					var nrm = Vec3.Cross( along, across ).Normal;
					across = Vec3.Cross( nrm, along ).Normal;
					var len = ((a1 - a0).Length + (b1 - b0).Length) * 0.5f;
					var gap = (b0 - a0).Length;
					L.Flesh.Add( new SdfEllipsoid( c, new Vec3( len * 0.62f, gap * 0.55f, 0.75f - i * 0.04f ), new Xform( along, across, nrm, Vec3.Zero ) ) { Material = FleshMat, Tag = side > 0 ? "doorL" : "doorR" } );
				}

			// Teeth along the free edge, curving in toward the cavity, plus between the rib tips.
			var edge = ribs.Select( r => r[^1] ).ToList();
			for ( var k = 0; k < edge.Count; k++ )
			{
				for ( var j = 0; j < 2; j++ )
				{
					var at = j == 0 || k + 1 >= edge.Count ? edge[k] : Vec3.Lerp( edge[k], edge[k + 1], 0.5f );
					var toward = (O + new Vec3( 2, 0, 0 ) - at).Normal;
					var dirT = (toward * 0.8f + new Vec3( 0.25f, 0, 0 ) + RandDir( rng ) * 0.15f).Normal;
					var molar = rng.NextDouble() < 0.25;
					var tl = molar ? Rand( rng, 1.4f, 1.9f ) : Rand( rng, 2.8f, 4.6f );
					L.Teeth.Add( new SdfRoundCone( at - dirT * 0.5f, molar ? 0.8f : 0.62f, at + dirT * tl, molar ? 0.5f : 0.12f ) { Material = BoneMat, Tag = side > 0 ? "teethL" : "teethR" } );
				}
			}

			// The skin that covered it, peeled with the door, a lip along the hinge.
			var hingeLine = ribs.Select( r => r[1] + new Vec3( -0.5f, 0.6f * side, 0 ) ).ToArray();
			L.Skin.Add( new SdfTube( hingeLine, hingeLine.Select( _ => 1.3f ).ToArray() ) { Material = SkinMat, Tag = side > 0 ? "doorL" : "doorR" } );
			// A tusk at the top and bottom of each door.
			L.Teeth.Add( new SdfRoundCone( edge[^1], 0.95f, edge[^1] + (new Vec3( 0.4f, -0.5f * side, 1f )).Normal * 6f, 0.15f ) { Material = BoneMat, Tag = "tusk" } );
			L.Teeth.Add( new SdfRoundCone( edge[0], 0.9f, edge[0] + (new Vec3( 0.5f, -0.4f * side, -1f )).Normal * 5f, 0.15f ) { Material = BoneMat, Tag = "tusk" } );
		}

		// A flap of sternum and skin hanging down from the top of the split.
		L.Skin.Add( new SdfTube( new[] { O + new Vec3( 0, 0, 7.5f ), O + new Vec3( 3, 0, 6 ), O + new Vec3( 4, 0.5f, 2 ), O + new Vec3( 3.5f, 0.3f, -1.5f ) }, new[] { 2.2f, 1.8f, 1.4f, 0.9f } ) { Material = FleshMat, Tag = "sternumFlap" } );
		L.Teeth.Add( new SdfCapsule( O + new Vec3( 0.5f, 0, 7.3f ), O + new Vec3( 3.8f, 0.4f, 0 ), 0.75f ) { Material = BoneMat, Tag = "sternum" } );

		// Three tongues with gesture: one reaching at the viewer, one draped over the lower-left
		// petal, one curling down to the belt.
		Vec3 TongueStart( float y, float z ) => O + new Vec3( -1.5f, y, z );
		L.Flesh.Add( new SdfTube( new[] { TongueStart( 0, 1 ), O + new Vec3( 4, 0.5f, 3 ), O + new Vec3( 10, -1, 1.5f ), O + new Vec3( 16, 0.5f, 4 ), O + new Vec3( 21, 2, 2.5f ), O + new Vec3( 25, 1, 4 ) }, new[] { 1.5f, 1.35f, 1.1f, 0.85f, 0.6f, 0.38f } ) { Material = GumMat, Tag = "tongue1" } );
		L.Flesh.Add( new SdfTube( new[] { TongueStart( 1.5f, -2 ), O + new Vec3( 3, 3, -3 ), O + new Vec3( 6, 7, -6 ), O + new Vec3( 6.5f, 10, -11 ), O + new Vec3( 5, 11.5f, -15 ) }, new[] { 1.3f, 1.15f, 0.9f, 0.6f, 0.38f } ) { Material = GumMat, Tag = "tongue2" } );
		L.Flesh.Add( new SdfTube( new[] { TongueStart( -1, -3 ), O + new Vec3( 3, -2, -6 ), O + new Vec3( 5, -1, -10 ), O + new Vec3( 4, 1, -12.5f ), O + new Vec3( 2.5f, 0, -13.5f ) }, new[] { 1.1f, 0.95f, 0.75f, 0.5f, 0.32f } ) { Material = GumMat, Tag = "tongue3" } );

		// The head: on its own neck, broken over to the left and hanging upside down beside the
		// left shoulder, face turned forward, jaw hanging open toward the sky.
		var nk = hips + new Vec3( 0.5f, 0, 26 );
		var neckPts = new[] { nk, nk + new Vec3( 0.5f, 1.5f, 4 ), nk + new Vec3( 0.5f, 6, 6 ), nk + new Vec3( 1.5f, 11, 3 ), nk + new Vec3( 2.5f, 13, -2.5f ) };
		L.Skin.Add( new SdfTube( neckPts, new[] { 3.0f, 2.6f, 2.3f, 2.2f, 2.3f } ) { Material = SkinMat, Tag = "neck" } );
		var hf = new Frame( neckPts[^1] + new Vec3( 2, 1.5f, -6 ), new Vec3( 0.85f, 0.3f, 0.1f ), new Vec3( 0.1f, 0.15f, -1f ), 1.0f );
		L.Skin.Add( FatedHead( rng, hf, "head", HeadFate.Droop ) );

		// Arms: dead weight, straight and a little hyperextended, hands planted 20 in outside the boots.
		foreach ( var side in new[] { 1f, -1f } )
		{
			var root = side > 0 ? shoulderL : shoulderR;
			L.Skin.Add( new SdfSphere( root, 3.6f ) { Material = SkinMat, Tag = "deltoid" } );
			var segs = new List<(Vec3, float, float, float)>
			{
				(new Vec3( 0.08f, 0.32f * side, -1f ), 24f, 2.9f, 1.9f),
				(new Vec3( 0.18f, 0.28f * side, -1f ), 30f, 2.5f, 1.15f),
			};
			var end = AnatomicalLimb( L.Skin, root, segs, SkinMat, side > 0 ? "armL" : "armR", endZ: 2.6f, sag: 0.35f );
			L.Skin.Add( HandAtWrist( rng, new Frame( new Vec3( end.Origin.x, end.Origin.y, 1.4f ), new Vec3( 1, 0.3f * side, 0 ), new Vec3( 0, 0, 1 ), 1.1f ), side > 0 ? "handL" : "handR", fingerStretch: 1.25f ) );
		}

		return L.Assemble( o.Seed, fleshAmp: 0.8f );
	}
}
