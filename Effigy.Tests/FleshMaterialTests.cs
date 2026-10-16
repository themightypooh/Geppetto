using System;
using System.IO;
using System.Linq;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The layered flesh material: a cavity-masked layer lands in the crease and not on the dome, a
/// per-texel bake plugs in where a per-vertex signal was, every preset writes its PNGs and .vmat on
/// a creased test blob, and a build script can apply it.
/// </summary>
public static class FleshMaterialTests
{
	public static void Run( string outDir )
	{
		Section( "flesh material: masks land where they say" );
		TestCavityLandsInTheCrease();
		TestTexelSignalPlugsIn();

		Section( "flesh material: every preset writes its maps and a vmat" );
		TestPresets( Path.Combine( outDir, "flesh" ) );

		Section( "flesh material: a build script applies it" );
		TestScript( Path.Combine( outDir, "flesh-script" ) );
	}

	/// <summary>A sphere 4 in across with a groove 0.6 in deep round its equator, unwrapped.</summary>
	static PolyMesh CreasedBlob()
	{
		var mesh = Primitives.QuadSphere( 2f, 16 );
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var groove = MathF.Max( 0f, 1f - MathF.Abs( p.z ) / 0.5f );
			if ( groove > 0f ) mesh.Positions[i] = p - new Vec3( p.x, p.y, 0f ).Normal * (0.6f * groove * groove);
		}
		UVUnwrap.Unwrap( mesh );
		return mesh;
	}

	static void TestCavityLandsInTheCrease()
	{
		var mesh = CreasedBlob();
		var surface = new FleshSurface( mesh, 256 );
		var stack = new FleshStack
		{
			BaseColour = new Vec3( 0.5f, 0.5f, 0.5f ),
			BaseRoughness = 0.5f,
			Layers = { new FleshLayer { Name = "Crease", Colour = new Vec3( 0f, 1f, 0f ), Roughness = 0.1f, Mask = Mask.Cavity( 0.02f, 0.1f ) } },
		};
		var maps = stack.Render( surface );

		double creaseG = 0, domeG = 0, creaseR = 0, domeR = 0;
		int nc = 0, nd = 0;
		for ( var t = 0; t < surface.Covered.Length; t++ )
		{
			if ( !surface.Covered[t] ) continue;
			var z = MathF.Abs( surface.PositionAt( t ).z );
			if ( z < 0.12f ) { creaseG += maps.Albedo[t * 4 + 1]; creaseR += maps.Roughness[t * 4]; nc++; }
			else if ( z > 1f ) { domeG += maps.Albedo[t * 4 + 1]; domeR += maps.Roughness[t * 4]; nd++; }
		}
		creaseG /= Math.Max( 1, nc ); domeG /= Math.Max( 1, nd ); creaseR /= Math.Max( 1, nc ); domeR /= Math.Max( 1, nd );

		Check( "the blob covers its UV square", surface.CoveredCount > 256 * 256 / 4, $"{surface.CoveredCount} texels" );
		Check( "a cavity-masked layer lands in the crease", creaseG > 200, $"crease green {creaseG:0}" );
		Check( "and stays off the dome", domeG < 140, $"dome green {domeG:0}" );
		Check( "its roughness rides the same mask", creaseR < 60 && domeR > 110, $"crease {creaseR:0}, dome {domeR:0}" );

		var wet = FleshMaterial.Bake( mesh, FleshMaterial.Preset( "wet_flesh" ), 256 );
		double cl = 0, dl = 0; nc = nd = 0;
		for ( var t = 0; t < surface.Covered.Length; t++ )
		{
			if ( !surface.Covered[t] ) continue;
			var z = MathF.Abs( surface.PositionAt( t ).z );
			var l = wet.Albedo[t * 4] + wet.Albedo[t * 4 + 1] + wet.Albedo[t * 4 + 2];
			if ( z < 0.12f ) { cl += l; nc++; } else if ( z > 1f ) { dl += l; nd++; }
		}
		Check( "wet flesh: the crease is darker than the dome", cl / Math.Max( 1, nc ) < dl / Math.Max( 1, nd ) * 0.85,
			$"crease {cl / Math.Max( 1, nc ):0}, dome {dl / Math.Max( 1, nd ):0}" );
	}

	static void TestTexelSignalPlugsIn()
	{
		var mesh = CreasedBlob();

		// A per-texel AO map that is shut on the left half of the square and open on the right: the
		// layer has to follow the map, not the per-vertex AO it replaced.
		var map = new BakedMap( 64, 64 );
		for ( var y = 0; y < 64; y++ )
			for ( var x = 0; x < 64; x++ )
				map.Rgb[(y * 64 + x) * 3] = (byte)(x < 32 ? 255 : 0);

		var surface = new FleshSurface( mesh, 128 ) { Ao = new TexelSignal( "test-ao", map ) };
		var stack = new FleshStack { BaseColour = Vec3.Zero, Layers = { new FleshLayer { Colour = new Vec3( 1, 1, 1 ), Mask = Mask.Ao( 0.2f, 0.8f ) } } };
		var maps = stack.Render( surface );
		int left = 0, right = 0, nl = 0, nr = 0;
		for ( var y = 0; y < 128; y++ )
			for ( var x = 0; x < 128; x++ )
			{
				var t = y * 128 + x;
				if ( !surface.Covered[t] ) continue;
				if ( x < 60 ) { left += maps.Albedo[t * 4]; nl++; } else if ( x > 68 ) { right += maps.Albedo[t * 4]; nr++; }
			}
		Check( "a per-texel bake plugs in where the per-vertex AO was", left / Math.Max( 1, nl ) > 250 && right / Math.Max( 1, nr ) < 5 );
	}

	static void TestPresets( string dir )
	{
		Directory.CreateDirectory( dir );
		var mesh = CreasedBlob();
		var n = 256 * 256;

		foreach ( var name in FleshMaterial.Presets )
		{
			var stack = FleshMaterial.Preset( name );
			var maps = FleshMaterial.Bake( mesh, stack, 256, 0.5f );
			var tag = name.ToLowerInvariant().Replace( ' ', '_' );
			var color = Path.Combine( dir, $"flesh_{tag}_color.png" );
			var rough = Path.Combine( dir, $"flesh_{tag}_rough.png" );
			var vmatPath = Path.Combine( dir, $"flesh_{tag}.vmat" );
			PngWriter.WriteFileRgba( color, maps.Albedo, 256, 256 );
			PngWriter.WriteFileRgba( rough, maps.Roughness, 256, 256 );
			var vmat = FleshMaterial.VmatSource( $"materials/flesh/flesh_{tag}_color.png", $"materials/flesh/flesh_{tag}_rough.png", stack.Metalness );
			File.WriteAllText( vmatPath, vmat );

			var shades = Enumerable.Range( 0, n ).Select( i => (maps.Albedo[i * 4] << 16) | (maps.Albedo[i * 4 + 1] << 8) | maps.Albedo[i * 4 + 2] ).Distinct().Count();
			var roughs = Enumerable.Range( 0, n ).Where( i => maps.Covered[i] ).Select( i => maps.Roughness[i * 4] / 255f ).ToArray();
			var noBlack = Enumerable.Range( 0, n ).All( i => maps.Albedo[i * 4] + maps.Albedo[i * 4 + 1] + maps.Albedo[i * 4 + 2] > 0 );
			Check( $"{name}: full maps with layered variation", maps.Albedo.Length == n * 4 && maps.Roughness.Length == n * 4 && shades > 12 && noBlack,
				$"{shades} shades, rough {roughs.Min():0.00}-{roughs.Max():0.00} (median {roughs.OrderBy( r => r ).ElementAt( roughs.Length / 2 ):0.00})" );
			Check( $"{name}: PNGs and vmat written", File.Exists( color ) && File.Exists( rough ) && vmat.Contains( "complex.vfx" )
				&& vmat.Contains( $"TextureColor \"materials/flesh/flesh_{tag}_color.png\"" ) && vmat.Contains( "TextureRoughness" )
				&& vmat.Count( c => c == '{' ) == vmat.Count( c => c == '}' ) );
		}

		var a = FleshMaterial.Bake( mesh, FleshMaterial.Preset( "Raw meat" ), 128 );
		var b = FleshMaterial.Bake( mesh, FleshMaterial.Preset( "raw_meat" ), 128 );
		Check( "the same settings bake the same maps", a.Albedo.SequenceEqual( b.Albedo ) && a.Roughness.SequenceEqual( b.Roughness ) );

		// Rule 6: on raw meat the pale fat is matte and the red lean is glossy.
		var fat = Enumerable.Range( 0, 128 * 128 ).Where( i => a.Covered[i] && a.Albedo[i * 4 + 2] > 150 ).Select( i => (int)a.Roughness[i * 4] ).DefaultIfEmpty( 0 ).Average();
		var lean = Enumerable.Range( 0, 128 * 128 ).Where( i => a.Covered[i] && a.Albedo[i * 4 + 2] < 90 ).Select( i => (int)a.Roughness[i * 4] ).DefaultIfEmpty( 255 ).Average();
		Check( "raw meat: fat is rougher than lean", fat > lean + 40, $"fat {fat / 255:0.00}, lean {lean / 255:0.00}" );
	}

	static void TestScript( string dir )
	{
		if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
		var folder = dir.Replace( '\\', '/' );
		var studio = new PartStudio();
		var result = BuildScript.Run( studio,
			"add Primitive name=Sac shape=Sphere radius=3 segments=24\n" +
			$"add FleshMaterial bodies=Sac preset=raw_meat resolution=128 folder={folder}\n", dir );

		var feature = studio.Features.OfType<FleshMaterialFeature>().FirstOrDefault();
		Check( "the script adds it", result.Ok && feature is not null, result.ToString() );
		Check( "and it writes the maps and the vmat", feature is not null && feature.Written.Count == 3 && feature.Written.All( File.Exists ),
			feature is null ? "" : string.Join( ", ", feature.Written.Select( Path.GetFileName ) ) );
		Check( "and shows on the model", studio.Bodies.Any( b => b.Mesh?.Paint is { Width: 128 } ) );
	}
}
