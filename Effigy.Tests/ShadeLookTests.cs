using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy.Render;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// ShadedRender's finished look: the studio environment (split-sum IBL), bloom, AgX, tiled and
/// triplanar maps with mips, normal maps, per-face materials. Each check is a property a person
/// judging a render would notice, measured on small renders.
/// </summary>
public static class ShadeLookTests
{
	public static void Run( string outDir )
	{
		var dir = Path.Combine( outDir, "shade_look" );
		Directory.CreateDirectory( dir );

		Section( "shaded render: image-based lighting" );
		TestChromeReflects( dir );

		Section( "shaded render: bloom and tone" );
		TestBloom( dir );
		TestAgX();

		Section( "shaded render: maps" );
		TestTiling();
		TestTriplanar();
		TestNormalMap();

		Section( "shaded render: materials per face" );
		TestFaceMaterials();
	}

	static PolyMesh Sphere( float r = 1f, int seg = 24 )
	{
		var s = Primitives.QuadSphere( r, seg );
		for ( var i = 0; i < s.Positions.Count; i++ ) s.Positions[i] += new Vec3( 0, 0, r );
		return s;
	}

	static ShadeScene Dark() => new() { Floor = false, BackgroundBottom = Vec3.Zero, BackgroundTop = Vec3.Zero };

	static ShadeCamera Front( float z = 1f, float dist = 5f ) => new( new Vec3( dist, 0, z ), new Vec3( 0, 0, z ), 30f );

	static float Lum( int px ) => ((px >> 16) & 255) * 0.2126f + ((px >> 8) & 255) * 0.7152f + (px & 255) * 0.0722f;

	/// <summary>Luminance of pixels brighter than the backdrop: count, mean, spread.</summary>
	static (int N, float Mean, float Std) Covered( Raster r, float floor = 2f )
	{
		var l = r.Pixels.Select( Lum ).Where( v => v > floor ).ToArray();
		if ( l.Length == 0 ) return (0, 0, 0);
		var m = l.Average();
		return (l.Length, m, MathF.Sqrt( l.Select( v => (v - m) * (v - m) ).Average() ));
	}

	static void TestChromeReflects( string dir )
	{
		var chrome = new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.95f, 0.93f, 0.9f ), Metallic = 1f, Roughness = 0.05f };
		ShadeScene Scene() { var s = Dark().Add( Sphere(), chrome ); s.Lights.Add( new ShadeLight( new Vec3( 1, 0.5f, 1 ), 2f ) ); return s; }
		var flat = ShadedRender.Render( Scene(), Front(), 160, 160, 1 );
		var lit = Scene(); lit.Environment = ShadeEnvironment.Studio();
		var ibl = ShadedRender.Render( lit, Front(), 160, 160, 1 );
		flat.SavePng( Path.Combine( dir, "chrome_plain.png" ) ); ibl.SavePng( Path.Combine( dir, "chrome_ibl.png" ) );
		var (_, _, sf) = Covered( flat ); var (_, _, si) = Covered( ibl );
		Check( "chrome mirrors the studio: far more contrast across the ball than the sky gradient gave", si > sf * 1.5f && si > 30f, $"luminance spread {si:0} with the studio, {sf:0} without" );

		// Rough metal keeps its colour and brightness (multiple scattering), rather than going dark.
		var gold = new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 1f, 0.75f, 0.3f ), Metallic = 1f, Roughness = 0.8f };
		var gs = Dark().Add( Sphere(), gold ); gs.Environment = ShadeEnvironment.Studio();
		var (_, gm, _) = Covered( ShadedRender.Render( gs, Front(), 120, 120, 1 ) );
		Check( "rough gold under the studio is not black", gm > 35f, $"mean luminance {gm:0}" );

		var e = ShadeEnvironment.Studio();
		var before = e.Signature(); e.Softboxes[0].Intensity *= 2f;
		Check( "editing a softbox invalidates the baked environment", e.Signature() != before );
	}

	static PolyMesh Ring()
	{
		var m = new PolyMesh(); const int seg = 64, side = 10;
		for ( var i = 0; i < seg; i++ )
			for ( var j = 0; j < side; j++ )
			{
				var a = i * MathF.PI * 2 / seg; var b = j * MathF.PI * 2 / side;
				m.Positions.Add( new Vec3( 0.1f * MathF.Sin( b ), (0.8f + 0.1f * MathF.Cos( b )) * MathF.Cos( a ), 1f + (0.8f + 0.1f * MathF.Cos( b )) * MathF.Sin( a ) ) );
			}
		for ( var i = 0; i < seg; i++ )
			for ( var j = 0; j < side; j++ )
			{
				int I( int a, int b ) => (a % seg) * side + (b % side);
				m.Faces.Add( new Face( new[] { I( i, j ), I( i + 1, j ), I( i + 1, j + 1 ), I( i, j + 1 ) } ) );
			}
		return m;
	}

	static void TestBloom( string dir )
	{
		var glow = new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.02f, 0.05f, 0.06f ), Emission = new Vec3( 0.3f, 1.6f, 2f ) };
		var plain = ShadedRender.Render( Dark().Add( Ring(), glow ), Front(), 160, 160, 1 );
		var s = Dark().Add( Ring(), glow ); s.Bloom = 0.8f;
		var bloomed = ShadedRender.Render( s, Front(), 160, 160, 1 );
		bloomed.SavePng( Path.Combine( dir, "ring_bloom.png" ) );
		// The hole in the ring and the space round it: dark without bloom, lit by the halo with it.
		int Halo( Raster r ) => r.Pixels.Count( p => Lum( p ) > 12f );
		Check( "an emissive ring glows past its own edge", Halo( bloomed ) > Halo( plain ) * 1.3f, $"{Halo( bloomed )} lit px with bloom, {Halo( plain )} without" );
	}

	static void TestAgX()
	{
		var hot = new ShadeMaterial { UseVertexColours = false, Albedo = Vec3.Zero, Specular = 0f, Emission = new Vec3( 0.05f, 3f, 4f ) };
		int Centre( ShadeTonemap t ) { var s = Dark().Add( Sphere(), hot ); s.Tonemap = t; var r = ShadedRender.Render( s, Front(), 40, 40, 1 ); return r.Pixels[20 * 40 + 20]; }
		var legacy = Centre( ShadeTonemap.Legacy ); var agx = Centre( ShadeTonemap.AgX );
		int R( int p ) => (p >> 16) & 255;
		Check( "AgX takes a blinding cyan toward white, as film does; the old curve keeps it pure cyan", R( agx ) > R( legacy ) + 40, $"red channel {R( agx )} AgX, {R( legacy )} legacy" );
	}

	/// <summary>A two-colour checker, <paramref name="cells"/> per side, RGBA.</summary>
	static byte[] Checker( int size, int cells )
	{
		var m = new byte[size * size * 4];
		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
				var on = ((x * cells / size) + (y * cells / size)) % 2 == 0;
				var o = (y * size + x) * 4;
				m[o] = (byte)(on ? 230 : 15); m[o + 1] = (byte)(on ? 30 : 15); m[o + 2] = 15; m[o + 3] = 255;
			}
		return m;
	}

	static int Transitions( Raster r, int row )
	{
		var n = 0; bool? last = null;
		for ( var x = 0; x < r.Width; x++ )
		{
			var red = ((r.Pixels[row * r.Width + x] >> 16) & 255) > 90;
			if ( last is bool l && l != red ) n++;
			last = red;
		}
		return n;
	}

	/// <summary>A square card facing +x, 2 units across, UVs 0..1.</summary>
	static PolyMesh Card()
	{
		var p = Primitives.Plane( 2f, 2f, 4, 4 );
		for ( var i = 0; i < p.Positions.Count; i++ ) p.Positions[i] = new Vec3( 0, p.Positions[i].x, 1f + p.Positions[i].y );
		return p;
	}

	static ShadeScene Lit( ShadeScene s ) { s.Lights.Add( new ShadeLight( new Vec3( 1, 0.2f, 0.4f ), 2f ) { Shadows = false } ); return s; }

	static void TestTiling()
	{
		var map = Checker( 64, 2 );
		var once = ShadedRender.Render( Lit( Dark().Add( Card(), new ShadeMaterial { UseVertexColours = false, MapSize = 64, AlbedoMap = map } ) ), Front( 1f, 6f ), 160, 160, 1 );
		var tiled = ShadedRender.Render( Lit( Dark().Add( Card(), new ShadeMaterial { UseVertexColours = false, MapSize = 64, AlbedoMap = map, MapScale = new Vec2( 4, 4 ) } ) ), Front( 1f, 6f ), 160, 160, 1 );
		var a = Transitions( once, 66 ); var b = Transitions( tiled, 66 );
		Check( "MapScale tiles the maps: many more repeats across the card", b >= a * 2 && b >= 7, $"{a} colour changes across a row at 1×, {b} at 4×" );

		// Far away, a fine tiling averages out instead of aliasing into stripes.
		var fine = Lit( Dark().Add( Card(), new ShadeMaterial { UseVertexColours = false, MapSize = 64, AlbedoMap = Checker( 64, 16 ), MapScale = new Vec2( 16, 16 ) } ) );
		var far = ShadedRender.Render( fine, Front( 1f, 60f ), 80, 80, 1 );
		Check( "a texture far finer than a pixel is filtered (mips), not aliased", Transitions( far, 40 ) <= 2, $"{Transitions( far, 40 )} colour changes across the far card" );
	}

	static void TestTriplanar()
	{
		var box = Primitives.Box( 2f, 2f, 2f );
		for ( var i = 0; i < box.Positions.Count; i++ ) box.Positions[i] += new Vec3( 0, 0, 1f );
		foreach ( var f in box.Faces ) f.UVs = new Vec2[f.Indices.Length];   // no UVs at all
		var m = new ShadeMaterial { UseVertexColours = false, MapSize = 64, AlbedoMap = Checker( 64, 2 ), TriplanarScale = 0.5f };
		var r = ShadedRender.Render( Lit( Dark().Add( box, m ) ), new ShadeCamera( new Vec3( 6, 3, 4 ), new Vec3( 0, 0, 1 ), 30f ), 160, 160, 1 );
		int reds = 0, darks = 0;
		foreach ( var px in r.Pixels )
		{
			int R = (px >> 16) & 255, G = (px >> 8) & 255;
			if ( R > 60 && R > G * 2 ) reds++; else if ( R > 2 ) darks++;
		}
		Check( "a mesh without UVs takes a tiling map through the triplanar projection", reds > 300 && darks > 300, $"{reds} red px, {darks} dark px" );
	}

	static void TestNormalMap()
	{
		// Stripes of normals tilted ±x across the card.
		const int size = 32;
		var nm = new byte[size * size * 4];
		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
				var o = (y * size + x) * 4; var left = x * 4 / size % 2 == 0;
				nm[o] = (byte)(left ? 60 : 196); nm[o + 1] = 128; nm[o + 2] = 230; nm[o + 3] = 255;
			}
		ShadeMaterial M( bool map ) => new() { UseVertexColours = false, Albedo = new Vec3( 0.5f, 0.5f, 0.5f ), Roughness = 0.7f, MapSize = map ? size : 0, NormalMap = map ? nm : null };
		// Light raking across the card, so a tilt along it shows.
		ShadeScene Raked( ShadeMaterial m ) { var s = Dark().Add( Card(), m ); s.Lights.Add( new ShadeLight( new Vec3( 0.5f, 1f, 0f ), 2f ) { Shadows = false } ); return s; }
		var flat = ShadedRender.Render( Raked( M( false ) ), Front( 1f, 6f ), 120, 120, 1 );
		var bumped = ShadedRender.Render( Raked( M( true ) ), Front( 1f, 6f ), 120, 120, 1 );
		float RowSpread( Raster r ) { var l = Enumerable.Range( 30, 60 ).Select( x => Lum( r.Pixels[60 * 120 + x] ) ).ToArray(); return l.Max() - l.Min(); }
		Check( "a tangent-space normal map bands the light across a flat card", RowSpread( bumped ) > RowSpread( flat ) + 20f, $"spread {RowSpread( bumped ):0} with the map, {RowSpread( flat ):0} without" );
	}

	static void TestFaceMaterials()
	{
		var a = new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.5f, 0.5f, 0.5f ), Roughness = 0.5f };
		var twin = new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.5f, 0.5f, 0.5f ), Roughness = 0.5f };
		var sphere = Sphere();
		for ( var i = 0; i < sphere.Faces.Count; i++ ) sphere.Faces[i].Material = sphere.Positions[sphere.Faces[i].Indices[0]].y > 0 ? 1 : 0;
		ShadeScene S() => Lit( Dark() );
		var one = ShadedRender.Render( S().Add( sphere, a ), Front(), 120, 120, 1 );
		var byFace = ShadedRender.Render( S().Add( sphere, new Dictionary<int, ShadeMaterial> { [0] = a, [1] = twin }, a ), Front(), 120, 120, 1 );
		var worst = 0;
		for ( var i = 0; i < one.Pixels.Length; i++ )
			for ( var c = 0; c < 24; c += 8 )
				worst = Math.Max( worst, Math.Abs( ((one.Pixels[i] >> c) & 255) - ((byFace.Pixels[i] >> c) & 255) ) );
		Check( "two identical materials by face render exactly like one: no seam at the boundary", worst <= 2, $"largest channel difference {worst}" );

		var red = new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.8f, 0.05f, 0.05f ) };
		var mixed = ShadedRender.Render( S().Add( sphere, new Dictionary<int, ShadeMaterial> { [1] = red }, a ), Front(), 120, 120, 1 );
		int reds = 0, greys = 0;
		foreach ( var px in mixed.Pixels )
		{
			int R = (px >> 16) & 255, G = (px >> 8) & 255;
			if ( R > G * 2 + 20 ) reds++; else if ( G > 20 ) greys++;
		}
		Check( "each face wears the material its id names, the rest the fallback", reds > 500 && greys > 500, $"{reds} red px, {greys} grey px" );
	}
}
