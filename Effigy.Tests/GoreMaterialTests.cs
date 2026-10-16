using System;
using System.IO;
using System.Linq;
using Effigy.Render;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The body-horror half of the flesh material: per-vertex layering for meshes with no UVs,
/// vertex-colour and corruption masks, triplanar pictures, the gore/oil/spike/ribbon families and
/// their .vmat extras, and the lit render a judge reads wet flesh from (ShadedRender with maps,
/// per-vertex roughness, glow and the oil film, reached from a build script as `render shade=`).
/// </summary>
public static class GoreMaterialTests
{
	public static void Run( string outDir )
	{
		var dir = Path.Combine( outDir, "gore" );
		Directory.CreateDirectory( dir );

		Section( "gore material: per vertex, no UVs" );
		TestPerVertex();
		TestVertexColourMask();

		Section( "gore material: corruption from seeds" );
		TestCorruption();
		TestInfectedSkin();

		Section( "gore material: triplanar pictures" );
		TestTriplanar();

		Section( "gore material: the families" );
		TestFamilies( dir );

		Section( "gore material: a judge can read wet" );
		TestWetRender( dir );

		Section( "gore material: from a build script" );
		TestScript( Path.Combine( dir, "script" ) );
	}

	/// <summary>The creased blob from FleshMaterialTests, with every UV zeroed: nothing to bake onto.</summary>
	static PolyMesh CreasedBlobNoUvs( int segments = 16 )
	{
		var mesh = Primitives.QuadSphere( 2f, segments );
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var groove = MathF.Max( 0f, 1f - MathF.Abs( p.z ) / 0.5f );
			if ( groove > 0f ) mesh.Positions[i] = p - new Vec3( p.x, p.y, 0f ).Normal * (0.6f * groove * groove);
		}
		foreach ( var f in mesh.Faces ) f.UVs = new Vec2[f.Count];
		return mesh;
	}

	static void TestPerVertex()
	{
		var mesh = CreasedBlobNoUvs();
		var stack = new FleshStack
		{
			BaseColour = new Vec3( 0.5f, 0.5f, 0.5f ),
			BaseRoughness = 0.5f,
			Layers = { new FleshLayer { Colour = new Vec3( 0f, 1f, 0f ), Roughness = 0.1f, Mask = Mask.Cavity( 0.02f, 0.1f ) } },
		};
		var v = FleshMaterial.BakeVertices( mesh, stack );
		Check( "one colour and one roughness per vertex", v.Colour.Length == mesh.Positions.Count && v.Roughness.Length == mesh.Positions.Count && v.Emission is null );

		var crease = Enumerable.Range( 0, mesh.Positions.Count ).Where( i => MathF.Abs( mesh.Positions[i].z ) < 0.12f ).ToArray();
		var dome = Enumerable.Range( 0, mesh.Positions.Count ).Where( i => MathF.Abs( mesh.Positions[i].z ) > 1f ).ToArray();
		var cg = crease.Average( i => v.Colour[i].y ); var dg = dome.Average( i => v.Colour[i].y );
		var cr = crease.Average( i => v.Roughness[i] ); var dr = dome.Average( i => v.Roughness[i] );
		Check( "a cavity layer lands in the crease with no UVs at all", cg > 0.8 && dg < 0.6, $"crease green {cg:0.00}, dome {dg:0.00}" );
		Check( "and its roughness rides the same mask", cr < 0.25 && dr > 0.4, $"crease {cr:0.00}, dome {dr:0.00}" );

		var threw = false;
		try { stack.Render( new FleshSurface( mesh ) ); } catch ( ArgumentException ) { threw = true; }
		Check( "a per-vertex surface refuses to pretend it has maps", threw );

		var a = FleshMaterial.BakeVertices( mesh, FleshMaterial.Preset( "Wet gore" ) );
		var b = FleshMaterial.BakeVertices( mesh, FleshMaterial.Preset( "wet_gore" ) );
		Check( "per-vertex bakes are repeatable", a.Colour.SequenceEqual( b.Colour ) && a.Roughness.SequenceEqual( b.Roughness ) );
	}

	static void TestVertexColourMask()
	{
		var mesh = CreasedBlobNoUvs();
		mesh.VertexColors = mesh.Positions.Select( p => new Vec4( p.x > 0 ? 1f : 0f, 0, 0, 1 ) ).ToArray();
		var stack = new FleshStack { BaseColour = Vec3.Zero, Layers = { new FleshLayer { Colour = new Vec3( 1, 1, 1 ), Mask = Mask.VertexColour( 0, 0.2f, 0.8f ) } } };
		var v = FleshMaterial.BakeVertices( mesh, stack );
		var pos = Enumerable.Range( 0, mesh.Positions.Count ).Where( i => mesh.Positions[i].x > 0.3f ).Average( i => v.Colour[i].x );
		var neg = Enumerable.Range( 0, mesh.Positions.Count ).Where( i => mesh.Positions[i].x < -0.3f ).Average( i => v.Colour[i].x );
		Check( "a vertex-colour channel masks a layer", pos > 0.99 && neg < 0.01, $"painted side {pos:0.00}, other {neg:0.00}" );

		// The same mask on a texel bake: interpolated from the vertices.
		var uv = CreasedBlobNoUvs();
		UVUnwrap.Unwrap( uv );
		uv.VertexColors = uv.Positions.Select( p => new Vec4( p.x > 0 ? 1f : 0f, 0, 0, 1 ) ).ToArray();
		var surface = new FleshSurface( uv, 128 );
		var maps = stack.Render( surface );
		double tp = 0, tn = 0; int np = 0, nn = 0;
		for ( var t = 0; t < surface.Covered.Length; t++ )
		{
			if ( !surface.Covered[t] ) continue;
			var x = surface.PositionAt( t ).x;
			if ( x > 0.4f ) { tp += maps.Albedo[t * 4]; np++; } else if ( x < -0.4f ) { tn += maps.Albedo[t * 4]; nn++; }
		}
		Check( "and the same mask works on a texel bake", tp / Math.Max( 1, np ) > 240 && tn / Math.Max( 1, nn ) < 15, $"{tp / Math.Max( 1, np ):0} vs {tn / Math.Max( 1, nn ):0}" );
	}

	static void TestCorruption()
	{
		// A 4 in sphere: seed on top, reach 4 in over the surface, 1 in front, no fingering.
		var mesh = Primitives.QuadSphere( 2f, 24 );
		var c = FleshMaterial.Corruption( mesh, new[] { new Vec3( 0, 0, 2 ) }, 4f, 1f, finger: 0f );
		var top = Enumerable.Range( 0, c.Length ).Where( i => mesh.Positions[i].z > 1.9f ).Average( i => c[i] );
		var bottom = Enumerable.Range( 0, c.Length ).Where( i => mesh.Positions[i].z < -1f ).Max( i => c[i] );
		// Surface distance from the pole is 2·θ; 4 in reach ends at θ = 2 rad (z ≈ −0.83), the front starts 1 in earlier.
		var inside = Enumerable.Range( 0, c.Length ).Where( i => MathF.Acos( Math.Clamp( mesh.Positions[i].z / 2f, -1f, 1f ) ) * 2f < 2.6f ).Min( i => c[i] );
		Check( "corruption is 1 at the seed and 0 past its reach", top > 0.99 && bottom < 0.01, $"top {top:0.00}, bottom max {bottom:0.00}" );
		Check( "and fully on inside the front", inside > 0.95, $"min inside {inside:0.00}" );

		var fingered = FleshMaterial.Corruption( mesh, new[] { new Vec3( 0, 0, 2 ) }, 4f, 1f );
		var ring = Enumerable.Range( 0, c.Length ).Where( i => MathF.Abs( mesh.Positions[i].z + 0.3f ) < 0.15f ).Select( i => fingered[i] ).ToArray();
		Check( "the default front is ragged, not a clean ring", ring.Max() - ring.Min() > 0.3f, $"{ring.Min():0.00}-{ring.Max():0.00} round one latitude" );

		var shielded = FleshMaterial.Corruption( mesh, new[] { new Vec3( 0, 0, 2 ) }, 4f, 1f, finger: 0f,
			protect: new[] { (new Vec3( 2, 0, 0.5f ), 0.8f, 0.3f) } );
		var face = Enumerable.Range( 0, c.Length ).Where( i => (mesh.Positions[i] - new Vec3( 2, 0, 0.5f )).Length < 0.6f ).Max( i => shielded[i] );
		Check( "a protected region stays capped (the face stays human)", face <= 0.31f, $"max {face:0.00} under a cap of 0.3" );

		FleshMaterial.PackMasks( mesh, c );
		Check( "packed masks: R is corruption, G wet only where corrupted, A occlusion",
			mesh.HasVertexColors && Enumerable.Range( 0, c.Length ).All( i => MathF.Abs( mesh.VertexColors[i].x - c[i] ) < 1e-6f
				&& mesh.VertexColors[i].y <= c[i] + 1e-6f && mesh.VertexColors[i].w is >= 0f and <= 1f ) );
	}

	static void TestInfectedSkin()
	{
		var mesh = Primitives.QuadSphere( 8f, 24 );
		var c = FleshMaterial.Corruption( mesh, new[] { new Vec3( 0, 0, 8 ) }, 14f, 3f, finger: 0f );
		var v = FleshMaterial.BakeVertices( mesh, FleshMaterial.Preset( "Infected skin" ), corruption: c );
		var turned = Enumerable.Range( 0, c.Length ).Where( i => c[i] > 0.9f && c[i] < 0.92f || (c[i] > 0.8f && c[i] < 0.9f) ).ToArray();
		var clean = Enumerable.Range( 0, c.Length ).Where( i => c[i] < 0.01f ).ToArray();
		double Ratio( int[] set ) => set.Average( i => v.Colour[i].x / Math.Max( 0.02f, v.Colour[i].y ) );
		var rt = Ratio( turned ); var rc = Ratio( clean );
		var gt = turned.Average( i => v.Roughness[i] ); var gc = clean.Average( i => v.Roughness[i] );
		Check( "infected skin: turned flesh is redder than the clean skin", turned.Length > 5 && rt > rc * 1.4, $"R/G {rt:0.00} vs {rc:0.00}" );
		Check( "and wetter", gt < gc - 0.15, $"roughness {gt:0.00} vs {gc:0.00}" );
		var source = Enumerable.Range( 0, c.Length ).Where( i => c[i] > 0.995f ).ToArray();
		var oil = source.Average( i => v.Colour[i].x + v.Colour[i].y + v.Colour[i].z );
		Check( "and oil-black at the source", oil < 0.3, $"rgb sum {oil:0.00}" );
	}

	static void TestTriplanar()
	{
		// 2×2 checker, black and white: one repeat every 4 in.
		var img = new TriplanarImage( 2, 2, new[] { Vec3.Zero, Vec3.One, Vec3.One, Vec3.Zero }, "checker" );
		var up = new Vec3( 0, 0, 1 );
		var a = img.Sample( new Vec3( 1f, 1f, 0 ), up, 4f ).x;
		var b = img.Sample( new Vec3( 3f, 1f, 0 ), up, 4f ).x;
		var c = img.Sample( new Vec3( 1f, 1f, 0 ) + new Vec3( 4f, 4f, 0 ), up, 4f ).x;
		Check( "a picture projects down z onto an upward face, and tiles", a < 0.01f && b > 0.99f && MathF.Abs( a - c ) < 1e-5f, $"{a:0.00} {b:0.00} {c:0.00}" );
		var side = img.Sample( new Vec3( 9f, 3f, 1f ), new Vec3( 1, 0, 0 ), 4f ).x;
		Check( "and along x onto a face facing x (y,z picks the texel)", side > 0.99f, $"{side:0.00}" );

		// A layer from a picture on a mesh with no UVs: both colours appear.
		var mesh = Primitives.QuadSphere( 4f, 24 );
		foreach ( var f in mesh.Faces ) f.UVs = new Vec2[f.Count];
		var stack = new FleshStack { Layers = { new FleshLayer { Image = img, ImageTile = 2f, Colour = new Vec3( 1, 0.5f, 0.5f ) } } };
		var v = FleshMaterial.BakeVertices( mesh, stack );
		var dark = v.Colour.Count( q => q.x < 0.2f ); var light = v.Colour.Count( q => q.x > 0.8f && MathF.Abs( q.y - q.x * 0.5f ) < 0.05f );
		Check( "a triplanar layer colours a mesh with no UVs, tinted by its colour", dark > 20 && light > 20, $"{dark} dark, {light} light" );

		var mask = new FleshStack { BaseColour = Vec3.Zero, Layers = { new FleshLayer { Colour = Vec3.One, Mask = Mask.Triplanar( img, 2f, 0.4f, 0.6f ) } } };
		var m = FleshMaterial.BakeVertices( mesh, mask );
		Check( "and a picture works as a mask", m.Colour.Count( q => q.x > 0.9f ) > 20 && m.Colour.Count( q => q.x < 0.1f ) > 20 );

		var png = Path.Combine( Path.GetTempPath(), $"effigy-tri-{Environment.ProcessId}.png" );
		PngWriter.WriteFileRgba( png, new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255 }, 2, 2 );
		var loaded = TriplanarImage.FromPng( png );
		File.Delete( png );
		Check( "a PNG loads as a triplanar picture", loaded.Width == 2 && MathF.Abs( loaded.Sample( new Vec3( 1, 1, 0 ), up, 4f ).x - a ) < 0.01f );
	}

	static PolyMesh CreasedBlob()
	{
		var mesh = CreasedBlobNoUvs();
		UVUnwrap.Unwrap( mesh );
		return mesh;
	}

	static void TestFamilies( string dir )
	{
		var mesh = CreasedBlob();
		var surfaceCrease = new FleshSurface( mesh, 256 );
		bool Crease( int t ) => MathF.Abs( surfaceCrease.PositionAt( t ).z ) < 0.12f;
		bool Dome( int t ) => MathF.Abs( surfaceCrease.PositionAt( t ).z ) > 1f;
		var covered = Enumerable.Range( 0, 256 * 256 ).Where( t => surfaceCrease.Covered[t] ).ToArray();

		var gore = FleshMaterial.Bake( mesh, FleshMaterial.Preset( "Wet gore" ), 256, 0.5f );
		double Lum( FleshMaps m, int t ) => m.Albedo[t * 4] * 0.3 + m.Albedo[t * 4 + 1] * 0.59 + m.Albedo[t * 4 + 2] * 0.11;
		var cl = covered.Where( Crease ).Average( t => Lum( gore, t ) ); var dl = covered.Where( Dome ).Average( t => Lum( gore, t ) );
		var rough = covered.Select( t => gore.Roughness[t * 4] / 255f ).OrderBy( x => x ).ToArray();
		var med = rough[rough.Length / 2];
		var fat = covered.Count( t => gore.Albedo[t * 4 + 2] > 120 ) / (float)covered.Length;
		Check( "wet gore: cavities darker than domes", cl < dl * 0.8, $"crease {cl:0}, dome {dl:0}" );
		Check( "wet gore: glossy (median roughness 0.08-0.22)", med is >= 0.08f and <= 0.22f, $"median {med:0.00}" );
		Check( "wet gore: fat marbling in a minority of the surface", fat is > 0.01f and < 0.3f, $"{fat:P0} pale" );

		var oil = FleshMaterial.Bake( mesh, FleshMaterial.Preset( "Oil black" ), 128 );
		var oilLum = Enumerable.Range( 0, 128 * 128 ).Where( t => oil.Covered[t] ).Select( t => Lum( oil, t ) ).OrderBy( x => x ).ToArray();
		var oilRough = Enumerable.Range( 0, 128 * 128 ).Where( t => oil.Covered[t] ).Select( t => oil.Roughness[t * 4] / 255f ).OrderBy( x => x ).ToArray();
		Check( "oil black: near black, near mirror", oilLum[oilLum.Length / 2] < 30 && oilRough[oilRough.Length / 2] <= 0.1f,
			$"median luminance {oilLum[oilLum.Length / 2]:0}, roughness {oilRough[oilRough.Length / 2]:0.00}" );

		var ribbon = FleshMaterial.Bake( mesh, FleshMaterial.Preset( "Entity ribbon" ), 128 );
		var glowing = ribbon.Emission is null ? 0 : Enumerable.Range( 0, 128 * 128 ).Count( t => ribbon.Covered[t] && Math.Max( ribbon.Emission[t * 4], ribbon.Emission[t * 4 + 2] ) > 25 );
		var cov = ribbon.Covered.Count( x => x );
		Check( "entity ribbon: glows, but only along veins and growths", ribbon.Emission is not null && glowing > cov / 50 && glowing < cov * 0.4, $"{glowing} of {cov} texels" );
		Check( "families without a glow bake no glow map", gore.Emission is null && oil.Emission is null );

		var vRibbon = FleshMaterial.VmatSource( "a.png", "b.png", FleshMaterial.Preset( "Entity ribbon" ), 0f, "c.png" );
		var vBone = FleshMaterial.VmatSource( "a.png", "b.png", FleshMaterial.Preset( "Bone" ) );
		var vGore = FleshMaterial.VmatSource( "a.png", "b.png", FleshMaterial.Preset( "Wet gore" ) );
		Check( "ribbon vmat: glow, see-through, wrapped", vRibbon.Contains( "F_SELF_ILLUM 1" ) && vRibbon.Contains( "TextureSelfIllumMask \"c.png\"" )
			&& vRibbon.Contains( "F_TRANSLUCENT 1" ) && vRibbon.Contains( "g_flOpacityScale" ) && vRibbon.Contains( "F_DIFFUSE_WRAP 1" )
			&& vRibbon.Count( ch => ch == '{' ) == vRibbon.Count( ch => ch == '}' ) );
		Check( "gore vmat: opaque, wrapped red; bone vmat: none of the extras",
			!vGore.Contains( "F_TRANSLUCENT" ) && vGore.Contains( "g_vDiffuseWrapColor" ) && !vBone.Contains( "F_DIFFUSE_WRAP" ) && !vBone.Contains( "F_SELF_ILLUM" ) );
		Check( "the old three-argument vmat is unchanged in form", FleshMaterial.VmatSource( "a.png", "b.png" ).Contains( "TextureRoughness \"b.png\"" )
			&& !FleshMaterial.VmatSource( "a.png", "b.png" ).Contains( "F_" ) );

		PngWriter.WriteFileRgba( Path.Combine( dir, "wet_gore_color.png" ), gore.Albedo, 256, 256 );
		PngWriter.WriteFileRgba( Path.Combine( dir, "ribbon_glow.png" ), ribbon.Emission ?? new byte[128 * 128 * 4], 128, 128 );
	}

	/// <summary>Below the label strip: how many pixels are highlight-bright (luminance over 200) and the
	/// mean chroma.</summary>
	static (int Bright, float Chroma) Stats( Raster r )
	{
		var bright = 0; float chroma = 0; var n = 0;
		for ( var y = 26; y < r.Height; y++ )
			for ( var x = 0; x < r.Width; x++ )
			{
				var px = r.Pixels[y * r.Width + x];
				int R = (px >> 16) & 255, G = (px >> 8) & 255, B = px & 255;
				if ( R * 0.3f + G * 0.59f + B * 0.11f > 200f ) bright++;
				chroma += Math.Max( R, Math.Max( G, B ) ) - Math.Min( R, Math.Min( G, B ) ); n++;
			}
		return (bright, chroma / Math.Max( 1, n ));
	}

	static void TestWetRender( string dir )
	{
		var sphere = Primitives.QuadSphere( 4f, 24 );
		for ( var i = 0; i < sphere.Positions.Count; i++ ) sphere.Positions[i] += new Vec3( 0, 0, 4 );
		var albedo = new Vec3( 0.25f, 0.04f, 0.04f );

		Raster Shot( ShadeMaterial m )
		{
			// Black backdrop, so the only bright pixels are on the sphere.
			var scene = new ShadeScene { Floor = false, BackgroundBottom = Vec3.Zero, BackgroundTop = Vec3.Zero }.Add( sphere, m );
			return ShadedRender.Sheet( scene, 1, 160, 12f, 1 );
		}

		var dry = Shot( new ShadeMaterial { UseVertexColours = false, Albedo = albedo, Roughness = 0.6f } );
		var wet = Shot( FleshMaterial.Shade( new FleshStack { BaseColour = new Vec3( 0.55f, 0.2f, 0.2f ), BaseRoughness = 0.12f, Wet = 0.9f } ) );
		var (pd, _) = Stats( dry ); var (pw, _) = Stats( wet );
		Check( "wet flesh throws a hard highlight that dry flesh does not", pw > 15 && pw > pd * 3, $"{pw} highlight px wet vs {pd} dry" );

		var plain = Shot( new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.006f, 0.005f, 0.007f ), Roughness = 0.05f, Coat = 1f } );
		var oil = Shot( new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.006f, 0.005f, 0.007f ), Roughness = 0.05f, Coat = 1f, Iridescence = 1f } );
		var (_, cp) = Stats( plain ); var (_, co) = Stats( oil );
		Check( "the oil film puts colour into black reflections", co > cp + 2f, $"mean chroma {co:0.0} with, {cp:0.0} without" );

		// Maps through the UVs: left half of the map red, right half green.
		var mapped = Primitives.QuadSphere( 4f, 24 );
		UVUnwrap.Unwrap( mapped );
		// Split at the median corner u, so half the surface is each colour whatever the packing did.
		var us = mapped.Faces.SelectMany( f => f.UVs ).Select( uv => uv.x ).OrderBy( u => u ).ToArray();
		var cut = us[us.Length / 2];
		var size = 64; var map = new byte[size * size * 4];
		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
				var o = (y * size + x) * 4;
				var red = (x + 0.5f) / size < cut;
				map[o] = (byte)(red ? 230 : 10); map[o + 1] = (byte)(red ? 10 : 230); map[o + 2] = 10; map[o + 3] = 255;
			}
		var shot = ShadedRender.Sheet( new ShadeScene().Add( mapped, new ShadeMaterial { UseVertexColours = false, MapSize = size, AlbedoMap = map } ), 1, 160, 12f, 1 );
		int reds = 0, greens = 0;
		foreach ( var px in shot.Pixels )
		{
			int R = (px >> 16) & 255, G = (px >> 8) & 255;
			if ( R > G * 2 + 30 ) reds++;
			if ( G > R * 2 + 30 ) greens++;
		}
		Check( "a baked albedo map is read through the UVs", reds > 200 && greens > 200, $"{reds} red px, {greens} green px" );

		// Per-vertex roughness: glossy top half, matte bottom half; the top's highlight is the sharper.
		var vr = sphere.Positions.Select( p => p.z > 4f ? 0.08f : 0.9f ).ToArray();
		var split = Shot( new ShadeMaterial { UseVertexColours = false, Albedo = albedo, VertexRoughness = vr } );
		var matte = Shot( new ShadeMaterial { UseVertexColours = false, Albedo = albedo, VertexRoughness = sphere.Positions.Select( _ => 0.9f ).ToArray() } );
		var (ps, _) = Stats( split ); var (pm, _) = Stats( matte );
		Check( "per-vertex roughness reaches the shader", ps > pm + 5, $"{ps} highlight px half-glossy vs {pm} all-matte" );

		wet.SavePng( Path.Combine( dir, "wet_vs_dry_wet.png" ) );
		dry.SavePng( Path.Combine( dir, "wet_vs_dry_dry.png" ) );
		oil.SavePng( Path.Combine( dir, "oil_film.png" ) );
		Check( "the sheet is views × tile wide", ShadedRender.Sheet( new ShadeScene().Add( sphere, ShadeMaterial.Clay() ), 3, 64, 12f, 1 ).Width == 192 );
	}

	static void TestScript( string dir )
	{
		if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
		Directory.CreateDirectory( dir );

		var studio = new PartStudio();
		var result = BuildScript.Run( studio,
			"add Primitive name=Sac shape=Sphere radius=3 segments=24\n" +
			"add FleshMaterial bodies=Sac preset=infected_skin pervertex=1 seeds=0,0,3 reach=6 front=2\n" +
			"render sac.png shade=material views=2 size=96\n" +
			"render clay.png shade=clay views=1 size=64\n", dir );
		var feature = studio.Features.OfType<FleshMaterialFeature>().FirstOrDefault();
		var body = studio.Bodies.FirstOrDefault();
		Check( "a script runs Flesh Material per vertex with corruption seeds", result.Ok && feature is not null && feature.VertexMaterials.Count == 1
			&& feature.Written.Count == 0, result.ToString() );
		Check( "and colours the vertices (no unwrap, no files)", body?.Mesh is { HasVertexColors: true } m
			&& m.VertexColors.Any( c => c.x > c.y * 2f ) && m.VertexColors.Any( c => c.y > 0.45f ) );
		var sac = Path.Combine( dir, "sac.png" );
		Check( "render shade=material writes a lit turnaround", File.Exists( sac ) && Raster.LoadPng( sac ).Width == 192
			&& result.Log.Any( l => l.Contains( "per vertex" ) ), string.Join( " | ", result.Log ) );
		Check( "render shade=clay too", File.Exists( Path.Combine( dir, "clay.png" ) ) );

		var bad = BuildScript.Run( new PartStudio(), "add Primitive name=A shape=Sphere radius=1\nrender x.png shade=chrome\n", dir );
		Check( "an unknown shade says what to use instead", !bad.Ok && bad.ToString().Contains( "shade=material" ) );
		var nopreset = BuildScript.Run( new PartStudio(), "add Primitive name=A shape=Sphere radius=1\nrender x.png shade=material preset=jelly\n", dir );
		Check( "an unknown preset lists the real ones", !nopreset.Ok && nopreset.ToString().Contains( "Wet gore" ) );

		var fallback = BuildScript.Run( new PartStudio(), "add Primitive name=A shape=Sphere radius=1 segments=12\nrender y.png shade=material preset=oil views=1 size=64\n", dir );
		Check( "preset= shades a body that has no Flesh Material", fallback.Ok && fallback.Log.Any( l => l.Contains( "Oil black" ) ), string.Join( " | ", fallback.Log ) );
	}
}
