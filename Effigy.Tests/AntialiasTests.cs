using System;
using System.Collections.Generic;
using System.Linq;
using Effigy.Render;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// ShadedRender's antialiasing (<see cref="CoverageResolve"/>), its silhouette normals, geometric
/// specular AA and the measuring view's smooth lines. Each check is something a person sees on an
/// edge: steps, a dark ring, a sparkle, a stair-stepped wire.
/// </summary>
public static class AntialiasTests
{
	public static void Run()
	{
		Section( "antialiasing: pattern and filter" );
		TestPattern();
		TestFilter();

		Section( "antialiasing: edges in a render" );
		TestEdgeLevels();
		TestNoDarkRing();
		TestInteriorUnchanged();
		TestSpecularAa();

		Section( "antialiasing: smooth lines" );
		TestSmoothLine();
	}

	static float Lum( int px ) => ((px >> 16) & 255) * 0.2126f + ((px >> 8) & 255) * 0.7152f + (px & 255) * 0.0722f;

	static ShadeCamera Front( float z = 1f, float dist = 5f ) => new( new Vec3( dist, 0, z ), new Vec3( 0, 0, z ), 30f );

	static ShadeScene Plain( float grey )
	{
		var s = new ShadeScene { Floor = false, BackgroundBottom = new Vec3( grey, grey, grey ), BackgroundTop = new Vec3( grey, grey, grey ), AoStrength = 0f };
		s.Lights.Add( new ShadeLight( new Vec3( 1, 0.3f, 0.5f ), 2f ) { Shadows = false } );
		return s;
	}

	static void TestPattern()
	{
		foreach ( var want in new[] { 4, 16, 64 } )
		{
			var p = CoverageResolve.Pattern( want );
			var n = p.Length;
			var cols = p.Select( s => (int)(s.X * n) ).Distinct().Count();
			var rows = p.Select( s => (int)(s.Y * n) ).Distinct().Count();
			var k = (int)MathF.Round( MathF.Sqrt( n ) );
			var strata = p.Select( s => ((int)(s.X * k), (int)(s.Y * k)) ).Distinct().Count();
			Check( $"{want} samples: one per stratum, and every sample in its own row and column of the fine grid",
				n == want && cols == n && rows == n && strata == n, $"{n} samples, {cols} columns, {rows} rows, {strata} strata" );
		}
		Check( "the pattern is the same every time (renders repeat)", CoverageResolve.Pattern( 16 ).SequenceEqual( CoverageResolve.Pattern( 16 ) ) );
	}

	static void TestFilter()
	{
		var min = Enumerable.Range( 0, 151 ).Min( i => CoverageResolve.Filter( i / 100f ) );
		Check( "the pixel filter is never negative (no dark ring round a light silhouette)", min >= 0f, $"min {min}" );
		Check( "and is zero at its edge", CoverageResolve.Filter( CoverageResolve.FilterRadius ) == 0f && CoverageResolve.Filter( 0f ) > 0.9f );
	}

	/// <summary>A light card tilted 4° on a dark backdrop: its top edge is nearly horizontal, the worst case for steps.</summary>
	static Raster Card( int coverage, int ss = 1 )
	{
		var card = Primitives.Box( 0.05f, 2f, 1.2f );
		var a = 4f * MathF.PI / 180f;
		for ( var i = 0; i < card.Positions.Count; i++ )
		{
			var q = card.Positions[i];
			card.Positions[i] = new Vec3( q.x, q.y * MathF.Cos( a ) - q.z * MathF.Sin( a ), q.y * MathF.Sin( a ) + q.z * MathF.Cos( a ) + 1f );
		}
		var s = Plain( 0.02f ).Add( card, new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.8f, 0.8f, 0.8f ), Roughness = 0.9f } );
		s.Coverage = coverage;
		return ShadedRender.Render( s, Front(), 96, 96, ss );
	}

	static int EdgeLevels( Raster r )
	{
		var bg = Lum( r.Pixels[0] );
		var inside = r.Pixels.Select( Lum ).Max();
		return r.Pixels.Select( Lum ).Where( v => v > bg + 4f && v < inside - 4f ).Select( v => (int)(v / 4f) ).Distinct().Count();
	}

	static void TestEdgeLevels()
	{
		var hard = EdgeLevels( Card( 1 ) );
		var ordered = EdgeLevels( Card( 1, 2 ) );
		var soft = EdgeLevels( Card( 16 ) );
		var fine = EdgeLevels( Card( 64 ) );
		Check( "with no AA a near-flat edge has no in-between shades", hard <= 2, $"{hard}" );
		Check( "16 coverage samples step a near-flat edge through many more shades than a 2×2 grid", soft >= 8 && soft > ordered * 2, $"aa=16: {soft}, ss=2 grid: {ordered}" );
		Check( "64 give at least as many as 16", fine >= soft, $"aa=64: {fine}, aa=16: {soft}" );
	}

	static void TestNoDarkRing()
	{
		var sphere = Primitives.QuadSphere( 1f, 16 );
		for ( var i = 0; i < sphere.Positions.Count; i++ ) sphere.Positions[i] += new Vec3( 0, 0, 1 );
		foreach ( var aa in new[] { 1, 16 } )
		{
			var s = Plain( 0.35f ).Add( sphere, new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.85f, 0.85f, 0.85f ), Roughness = 0.4f } );
			s.Lights.Clear();
			s.Lights.Add( new ShadeLight( new Vec3( 1, 0, 0 ), 2.5f ) { Shadows = false } ); // from the camera: the whole rim is lit
			s.Coverage = aa;
			var r = ShadedRender.Render( s, Front(), 120, 120, 1 );
			var bg = Lum( r.Pixels[0] );
			var darkest = r.Pixels.Select( Lum ).Min();
			Check( $"aa={aa}: no pixel round a lit sphere is darker than the backdrop (no black rim)", darkest >= bg - 3f, $"darkest {darkest:0.#}, backdrop {bg:0.#}" );
		}
	}

	static void TestInteriorUnchanged()
	{
		var sphere = Primitives.QuadSphere( 1f, 16 );
		for ( var i = 0; i < sphere.Positions.Count; i++ ) sphere.Positions[i] += new Vec3( 0, 0, 1 );
		Raster R( int aa ) { var s = Plain( 0.1f ).Add( sphere, ShadeMaterial.Clay() ); s.Coverage = aa; s.SpecularAa = false; return ShadedRender.Render( s, Front(), 80, 80, 1 ); }
		var off = R( 1 ); var on = R( 16 );
		var worst = 0;
		for ( var y = 30; y < 50; y++ )
			for ( var x = 30; x < 50; x++ )
				worst = Math.Max( worst, (int)MathF.Abs( Lum( off.Pixels[y * 80 + x] ) - Lum( on.Pixels[y * 80 + x] ) ) );
		Check( "inside a surface the antialiased render matches the plain one (only edges change)", worst <= 6, $"worst {worst}/255" );
	}

	static void TestSpecularAa()
	{
		var gloss = new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.3f, 0.3f, 0.3f ), Roughness = 0.08f };
		Raster R( PolyMesh m, bool on, float shift = 0f )
		{
			var s = Plain( 0.05f ).Add( m, gloss ); s.SpecularAa = on;
			var cam = new ShadeCamera( new Vec3( 5f, shift, 1f ), new Vec3( 0, shift, 1f ), 30f );
			return ShadedRender.Render( s, cam, 64, 64, 1 );
		}

		// A truly flat card (a plane: a box's corners would smooth its normals into a curve).
		var plane = Primitives.Plane( 2f, 2f );
		for ( var i = 0; i < plane.Positions.Count; i++ ) { var q = plane.Positions[i]; plane.Positions[i] = new Vec3( 0f, q.x, q.y + 1f ); }
		var on = R( plane, true ); var off = R( plane, false );
		Check( "on a flat face specular AA changes nothing", on.Pixels.SequenceEqual( off.Pixels ) );

		// A small glossy ball nudged by quarter pixels, as in a turntable: without it the highlight's peak
		// jumps as it falls on a pixel or between them (the sparkle); with it the peak holds steady.
		var ball = Primitives.QuadSphere( 0.25f, 12 );
		for ( var i = 0; i < ball.Positions.Count; i++ ) ball.Positions[i] += new Vec3( 0, 0, 1 );
		var px = 2f * 5f * MathF.Tan( 15f * MathF.PI / 180f ) / 64f;
		float Flicker( bool aa )
		{
			var peaks = Enumerable.Range( 0, 6 ).Select( k => R( ball, aa, k * 0.25f * px ).Pixels.Max( Lum ) ).ToArray();
			return peaks.Max() - peaks.Min();
		}
		var withAa = Flicker( true ); var without = Flicker( false );
		Check( "on a tiny glossy ball moving by quarter pixels the highlight flickers less", withAa < without, $"peak swing {withAa:0.#} with, {without:0.#} without" );
	}

	static void TestSmoothLine()
	{
		var r = new Raster( 64, 64, 0xffffff );
		r.LineSmooth( 4, 10, 60, 30, 0x000000 );
		var partial = r.Pixels.Count( p => Lum( p ) > 8f && Lum( p ) < 247f );
		var touched = r.Pixels.Count( p => Lum( p ) < 247f );
		Check( "a smooth line has soft edges (pixels part-covered)", partial > 40, $"{partial} partial of {touched}" );
		Check( "and stays about a pixel wide (not a smear)", touched < 60 * 3.2f, $"{touched} pixels for a 60 px line" );

		var hidden = new Raster( 32, 32, 0xffffff );
		Array.Fill( hidden.Depth, 1f );
		hidden.LineSmooth( 2, 16, 30, 16, 0x000000, 1f, 1f, 5f, 5f );
		Check( "a line behind the depth buffer is hidden", hidden.Pixels.All( p => p == 0xffffff ) );
	}
}
