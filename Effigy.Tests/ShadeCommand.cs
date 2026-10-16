using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy.Render;

namespace Effigy.Tests;

/// <summary>
/// `effigy shade`: the judging renders that tools/render_creature.py made in Blender, on Effigy's own
/// <see cref="ShadedRender"/>. Same arguments, same file names, so the scripts swap one line.
///
///   effigy shade model.obj outdir [size=1024] [views=front,quarter,side,back,top,close] [turn=8]
///                [cam=x,y,z look=x,y,z lens=85] [clay=1] [silhouette=0] [ground=0] [ss=2] [sheet=0]
///                [tex=0] [assets=dir] [style=studio] [ibl=0|1] [bloom=0.6] [tonemap=legacy|aces|agx|punchy]
///                [vignette=0.3] [floorrefl=0.15] [contact=-1]
///
/// STYLE: style=studio turns on <see cref="ShadeScene.Portfolio"/> - a procedural studio environment for
/// reflections and soft fill, bent-normal AO, contact shadows, bloom, AgX, a vignette, a faint floor
/// reflection. The other keys set one piece each and win over style=.
///
/// TEXTURES: each <c>usemtl</c> name is looked up as an s&amp;box .vmat under the project's Assets folder
/// (found by walking up from the OBJ, or assets=). Its TextureColor / Roughness / Metalness / Normal /
/// AmbientOcclusion PNGs, colour tint and self-illum are drawn on the OBJ's UVs, so the render shows the
/// materials the engine will. A name with no vmat, or a .tga default, falls back to clay. tex=0 or
/// clay=1 turns it off.
///
/// Writes outdir/&lt;name&gt;_&lt;view&gt;.png per view, &lt;name&gt;_sil_&lt;view&gt;.png (black on white, front/side/quarter),
/// &lt;name&gt;_turnNN.png, and &lt;name&gt;_sheet.png with every tile labelled. With cam= it renders the one
/// view &lt;name&gt;_custom.png and no sheet. s&amp;box space: X forward, Y left, Z up, inches.
///
/// The scene copies the Blender one: dark backdrop and floor, the same four suns at the same angles
/// (strength / π, since Blender's sun strength is irradiance and ShadedRender's diffuse already carries
/// the 1/π), 36 mm-sensor lenses, the camera distances, the flesh and clay materials.
/// </summary>
public static class ShadeCommand
{
	public static int Run( string[] a )
	{
		if ( a.Length < 3 ) { Console.Error.WriteLine( "effigy shade model.obj outdir [size=] [views=] [turn=] [cam= look= lens=] [clay=1] [silhouette=0] [ground=0] [ss=2] [sheet=0] [style=studio] [bloom=] [tonemap=agx|punchy|aces] [vignette=] [floorrefl=] [ibl=0|1]" ); return 1; }

		var objPath = a[1]; var outDir = a[2];
		var opts = a.Skip( 3 ).Select( s => s.Split( '=', 2 ) ).Where( p => p.Length == 2 ).ToDictionary( p => p[0], p => p[1] );
		string O( string k, string d ) => opts.TryGetValue( k, out var v ) ? v : d;
		static float F( string s ) => float.Parse( s, CultureInfo.InvariantCulture );
		static Vec3 V( string s ) { var p = s.Split( ',' ); return new Vec3( F( p[0] ), F( p[1] ), F( p[2] ) ); }

		var size = int.Parse( O( "size", "1024" ) );
		var ss = Math.Clamp( int.Parse( O( "ss", "2" ) ), 1, 4 );
		var views = O( "views", "front,quarter,side,back,top,close" ).Split( ',', StringSplitOptions.RemoveEmptyEntries ).ToList();
		var clay = O( "clay", "0" ) == "1";
		var name = Path.GetFileNameWithoutExtension( objPath );
		Directory.CreateDirectory( outDir );

		var mesh = ObjReader.ReadFile( objPath );
		if ( mesh.Positions.Count == 0 || mesh.Faces.Count == 0 ) { Console.Error.WriteLine( $"no mesh in {objPath}" ); return 1; }

		var mn = new Vec3( mesh.Positions.Min( p => p.x ), mesh.Positions.Min( p => p.y ), mesh.Positions.Min( p => p.z ) );
		var mx = new Vec3( mesh.Positions.Max( p => p.x ), mesh.Positions.Max( p => p.y ), mesh.Positions.Max( p => p.z ) );
		var c = (mn + mx) * 0.5f;
		var extent = MathF.Max( mx.x - mn.x, MathF.Max( mx.y - mn.y, mx.z - mn.z ) );
		Console.WriteLine( $"bounds ({mn.x:0.##}, {mn.y:0.##}, {mn.z:0.##}) ({mx.x:0.##}, {mx.y:0.##}, {mx.z:0.##}) extent {extent:0.##}" );

		var material = clay
			? new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.62f, 0.6f, 0.57f ), Roughness = 0.65f }
			: new ShadeMaterial { Roughness = 0.38f, Subsurface = 0.35f, ScatterDistance = 0.15f, Coat = 0.25f, CoatRoughness = 0.2f };

		var dark = new Vec3( 0.08f, 0.085f, 0.1f );
		var scene = new ShadeScene
		{
			BackgroundBottom = dark, BackgroundTop = dark,
			SkyColour = dark * 1.4f, GroundColour = dark * 0.5f,
			Floor = O( "ground", "1" ) != "0", FloorHeight = mn.z,
			FloorColour = new Vec3( 0.12f, 0.12f, 0.125f ), FloorRoughness = 0.9f,
			AoRadius = extent * 0.04f, AoStrength = 1f,
			Exposure = F( O( "exposure", "1" ) ),
		};
		// The finished look (studio IBL, AO, contact shadows, bloom, AgX, vignette); each piece can be set alone too.
		if ( O( "style", "plain" ) == "studio" ) scene.Portfolio();
		if ( opts.ContainsKey( "bloom" ) ) scene.Bloom = F( opts["bloom"] );
		if ( opts.ContainsKey( "vignette" ) ) scene.Vignette = F( opts["vignette"] );
		if ( opts.ContainsKey( "floorrefl" ) ) scene.FloorReflection = F( opts["floorrefl"] );
		if ( opts.ContainsKey( "contact" ) ) scene.ContactShadows = F( opts["contact"] );
		if ( opts.TryGetValue( "tonemap", out var tm ) )
			scene.Tonemap = tm switch { "aces" => ShadeTonemap.Aces, "agx" => ShadeTonemap.AgX, "punchy" => ShadeTonemap.AgXPunchy, _ => ShadeTonemap.Legacy };
		if ( O( "ibl", "" ) == "1" ) scene.Environment ??= ShadeEnvironment.Studio();
		if ( O( "ibl", "" ) == "0" ) scene.Environment = null;

		var parts = clay || O( "tex", "1" ) == "0" ? null : TexturedParts( objPath, mesh, O( "assets", null ) );
		void AddModel()
		{
			if ( parts is null ) { scene.Add( mesh, material ); return; }
			foreach ( var (sub, mat) in parts ) scene.Add( sub, mat );
		}
		AddModel();
		// Blender's suns: XYZ euler degrees, strength, colour, angular size in degrees.
		scene.Lights.Add( Sun( 50, 15, 35, 3.2f, new Vec3( 1f, 0.93f, 0.85f ), 6 ) );
		scene.Lights.Add( Sun( 65, -10, 150, 1.2f, new Vec3( 0.75f, 0.85f, 1f ), 30 ) );
		scene.Lights.Add( Sun( 35, 0, -120, 3.0f, new Vec3( 0.9f, 0.95f, 1f ), 10 ) );
		scene.Lights.Add( Sun( -60, 0, 20, 0.5f, new Vec3( 1f, 0.45f, 0.35f ), 40 ) );

		var dist = extent * 1.75f;
		var cams = new Dictionary<string, (Vec3 Eye, Vec3 Look, float Lens)>
		{
			// the creature's front is +X
			["front"] = (new Vec3( c.x + dist, c.y, c.z + extent * 0.15f ), c, 60),
			["quarter"] = (new Vec3( c.x + dist * 0.75f, c.y - dist * 0.7f, c.z + extent * 0.45f ), c, 60),
			["side"] = (new Vec3( c.x, c.y - dist, c.z + extent * 0.12f ), c, 60),
			["back"] = (new Vec3( c.x - dist, c.y, c.z + extent * 0.15f ), c, 60),
			["top"] = (new Vec3( c.x + 0.01f, c.y, c.z + dist ), c, 60),
			["close"] = (new Vec3( c.x + dist * 0.45f, c.y - dist * 0.35f, c.z + extent * 0.35f ), new Vec3( c.x, c.y, c.z + extent * 0.2f ), 85),
		};
		var custom = opts.ContainsKey( "cam" );
		if ( custom )
		{
			cams["custom"] = (V( opts["cam"] ), opts.ContainsKey( "look" ) ? V( opts["look"] ) : c, F( O( "lens", "85" ) ));
			views = new List<string> { "custom" };
		}

		var clock = System.Diagnostics.Stopwatch.StartNew();
		var tiles = new List<(string Label, Raster Image)>();
		void Shot( string label, string file, (Vec3 Eye, Vec3 Look, float Lens) cam )
		{
			var camera = new ShadeCamera( cam.Eye, cam.Look, LensToFov( cam.Lens ) );
			// Straight down: Blender's to_track_quat('-Z','Y') keeps +Y up the frame.
			if ( MathF.Abs( (cam.Look - cam.Eye).Normal.z ) > 0.99f ) camera.Up = new Vec3( 0, 1, 0 );
			var r = ShadedRender.Render( scene, camera, size, size, ss );
			var path = Path.Combine( outDir, file );
			r.SavePng( path );
			tiles.Add( (label, r) );
			Console.WriteLine( path );
		}

		foreach ( var v in views )
		{
			if ( !cams.TryGetValue( v, out var cam ) ) { Console.Error.WriteLine( $"no view '{v}' (front, quarter, side, back, top, close)" ); continue; }
			Shot( $"{name} {v}", $"{name}_{v}.png", cam );
		}

		// Silhouettes: black on white, flat. Nothing reflects or is lit, the backdrop saturates to white.
		if ( !custom && O( "silhouette", "1" ) == "1" )
		{
			var keep = (scene.Lights.ToList(), scene.Floor, scene.BackgroundBottom, scene.SkyColour, scene.GroundColour, scene.AoStrength);
			scene.Items.Clear();
			scene.Add( mesh, new ShadeMaterial { UseVertexColours = false, Albedo = Vec3.Zero, Specular = 0f, Roughness = 1f } );
			scene.Lights.Clear();
			scene.Floor = false;
			scene.BackgroundBottom = scene.BackgroundTop = new Vec3( 50, 50, 50 );
			scene.SkyColour = scene.GroundColour = Vec3.Zero;
			scene.AoStrength = 0f;
			// Nothing of the finished look either: no reflections, glow or darkened corners in a mask.
			var look = (scene.Environment, scene.Bloom, scene.Vignette);
			(scene.Environment, scene.Bloom, scene.Vignette) = (null, 0f, 0f);
			foreach ( var v in new[] { "front", "side", "quarter" } )
				Shot( $"{name} silhouette {v}", $"{name}_sil_{v}.png", cams[v] with { Lens = 60 } );
			scene.Items.Clear();
			AddModel();
			scene.Lights.AddRange( keep.Item1 );
			(scene.Floor, scene.BackgroundBottom, scene.SkyColour, scene.GroundColour, scene.AoStrength) = (keep.Floor, keep.BackgroundBottom, keep.SkyColour, keep.GroundColour, keep.AoStrength);
			scene.BackgroundTop = keep.BackgroundBottom;
			(scene.Environment, scene.Bloom, scene.Vignette) = look;
		}

		var turn = int.Parse( O( "turn", "0" ) );
		for ( var i = 0; i < turn; i++ )
		{
			var t = 2f * MathF.PI * i / turn;
			Shot( $"{name} turn {i * 360 / turn}", $"{name}_turn{i:00}.png", (new Vec3( c.x + dist * MathF.Cos( t ), c.y + dist * MathF.Sin( t ), c.z + extent * 0.3f ), c, 60) );
		}

		if ( !custom && O( "sheet", "1" ) == "1" && tiles.Count > 0 )
		{
			var sheet = Path.Combine( outDir, $"{name}_sheet.png" );
			Sheet( tiles, 3 ).SavePng( sheet );
			Console.WriteLine( $"sheet {sheet}" );
		}
		Console.WriteLine( $"shaded {mesh.Faces.Count:N0} faces, {tiles.Count} images at {size}² in {clock.Elapsed.TotalSeconds:0.0}s" );
		return 0;
	}

	/// <summary>
	/// The OBJ split by <c>usemtl</c>, each part with a material read from its vmat. Null when no part
	/// found a vmat with a texture (the caller then draws clay). Faces come out of ObjReader in file order,
	/// so counting <c>f</c> lines under each <c>usemtl</c> says which face wears which name.
	/// </summary>
	static List<(PolyMesh Mesh, ShadeMaterial Material)> TexturedParts( string objPath, PolyMesh mesh, string assets )
	{
		var names = new List<string>();
		var current = "";
		foreach ( var line in File.ReadLines( objPath ) )
		{
			if ( line.StartsWith( "usemtl" ) ) current = line.Length > 7 ? line[7..].Trim() : "";
			else if ( line.StartsWith( "f " ) ) names.Add( current );
		}
		if ( names.Count != mesh.Faces.Count ) { Console.Error.WriteLine( $"tex: {names.Count} f lines vs {mesh.Faces.Count} faces, drawing clay" ); return null; }

		assets ??= FindAssets( Path.GetDirectoryName( Path.GetFullPath( objPath ) ) );
		if ( assets is null ) { Console.Error.WriteLine( "tex: no Assets folder above the OBJ (pass assets=), drawing clay" ); return null; }

		var parts = new List<(PolyMesh, ShadeMaterial)>();
		var any = false;
		foreach ( var group in Enumerable.Range( 0, names.Count ).GroupBy( i => names[i] ) )
		{
			var sub = new PolyMesh { Positions = mesh.Positions };
			foreach ( var i in group ) sub.Faces.Add( mesh.Faces[i] );
			var vmat = FindVmat( assets, group.Key );
			var mat = vmat is null ? null : VmatMaterial( assets, vmat );
			Console.WriteLine( $"tex: {group.Key} ({group.Count():N0} faces) -> {(mat is null ? "clay" : Path.GetRelativePath( assets, vmat ))}" );
			any |= mat is not null;
			parts.Add( (sub, mat ?? new ShadeMaterial { UseVertexColours = false, Albedo = new Vec3( 0.62f, 0.6f, 0.57f ), Roughness = 0.65f }) );
		}
		return any ? parts : null;
	}

	static string FindAssets( string dir )
	{
		for ( var d = dir; d is not null; d = Path.GetDirectoryName( d ) )
		{
			if ( Path.GetFileName( d ).Equals( "Assets", StringComparison.OrdinalIgnoreCase ) ) return d;
			var inside = Path.Combine( d, "Assets" );
			if ( Directory.Exists( inside ) ) return inside;
		}
		return null;
	}

	/// <summary>"pod_body", "pod_body.vmat" or "materials/podracer/pod_body.vmat" to a file under Assets.</summary>
	static string FindVmat( string assets, string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return null;
		var rel = name.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) ? name : name + ".vmat";
		var direct = Path.Combine( assets, rel.Replace( '/', Path.DirectorySeparatorChar ) );
		if ( File.Exists( direct ) ) return direct;
		var leaf = Path.GetFileName( rel );
		return Directory.EnumerateFiles( assets, leaf, SearchOption.AllDirectories ).FirstOrDefault();
	}

	/// <summary>The parts of a complex.shader vmat this renderer can draw. Null when it binds no PNG.</summary>
	internal static ShadeMaterial VmatMaterial( string assets, string vmatPath )
	{
		var kv = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var raw in File.ReadLines( vmatPath ) )
		{
			var line = raw.Trim();
			var q = line.IndexOf( '"' );
			if ( q <= 0 || line.StartsWith( "//" ) ) continue;
			var key = line[..q].Trim(); var end = line.IndexOf( '"', q + 1 );
			if ( end > q ) kv[key] = line[(q + 1)..end];
		}
		static Vec3 Vec( string s ) { var p = s.Trim( '[', ']', ' ' ).Split( ' ', StringSplitOptions.RemoveEmptyEntries ); return new Vec3( F( p[0] ), F( p[1] ), F( p[2] ) ); }
		static Vec2 Vec2Of( string s ) { var p = s.Trim( '[', ']', ' ' ).Split( ' ', StringSplitOptions.RemoveEmptyEntries ); return new Vec2( F( p[0] ), F( p[1] ) ); }
		static float F( string s ) => float.Parse( s, CultureInfo.InvariantCulture );

		(byte[] Rgba, int W, int H)? Load( string key )
		{
			if ( !kv.TryGetValue( key, out var rel ) || !rel.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) ) return null;
			var path = Path.Combine( assets, rel.Replace( '/', Path.DirectorySeparatorChar ) );
			if ( !File.Exists( path ) ) { Console.Error.WriteLine( $"tex: {Path.GetFileName( vmatPath )}: {rel} missing" ); return null; }
			try { return PngReader.Read( File.ReadAllBytes( path ) ); }
			catch ( Exception e ) { Console.Error.WriteLine( $"tex: {rel}: {e.Message}" ); return null; }
		}

		var m = new ShadeMaterial { UseVertexColours = false, Albedo = Vec3.One, Roughness = 0.5f };
		var maps = new (string Key, (byte[] Rgba, int W, int H)? Img)[]
		{
			("TextureColor", Load( "TextureColor" )), ("TextureRoughness", Load( "TextureRoughness" )),
			("TextureMetalness", Load( "TextureMetalness" )), ("TextureNormal", Load( "TextureNormal" )),
			("TextureAmbientOcclusion", Load( "TextureAmbientOcclusion" )),
		};
		// Tiny maps are constants (rough_035.png, metal_100.png): read them as numbers. The rest are
		// resampled to the biggest one so they share MapSize.
		var size = maps.Where( x => x.Img is { } i && i.W > 8 ).Select( x => Math.Max( x.Img.Value.W, x.Img.Value.H ) ).DefaultIfEmpty( 0 ).Max();
		byte[] Fit( (byte[] Rgba, int W, int H) img )
		{
			if ( img.W == size && img.H == size ) return img.Rgba;
			var o = new byte[size * size * 4];
			for ( var y = 0; y < size; y++ )
				for ( var x = 0; x < size; x++ )
					Array.Copy( img.Rgba, ((y * img.H / size) * img.W + x * img.W / size) * 4, o, (y * size + x) * 4, 4 );
			return o;
		}
		var tint = kv.TryGetValue( "g_vColorTint", out var t ) ? Vec( t ) : Vec3.One;
		var textured = false;
		foreach ( var (key, img) in maps )
		{
			if ( img is not { } i ) continue;
			if ( i.W <= 8 || size == 0 )
			{
				var v = i.Rgba[0] / 255f;
				if ( key == "TextureColor" ) m.Albedo = new Vec3( ShadedRender.SrgbToLinear( i.Rgba[0] / 255f ), ShadedRender.SrgbToLinear( i.Rgba[1] / 255f ), ShadedRender.SrgbToLinear( i.Rgba[2] / 255f ) );
				else if ( key == "TextureRoughness" ) m.Roughness = v;
				else if ( key == "TextureMetalness" ) m.Metallic = v;
				textured = true;
				continue;
			}
			var data = Fit( i );
			switch ( key )
			{
				case "TextureColor": m.AlbedoMap = data; break;
				case "TextureRoughness": m.RoughnessMap = data; break;
				case "TextureMetalness": m.MetallicMap = data; break;
				case "TextureNormal": m.NormalMap = data; break;
				case "TextureAmbientOcclusion": m.AoMap = data; break;
			}
			textured = true;
		}
		if ( !textured ) return null;
		m.MapSize = size;
		// s&box tiles every map by these; its samplers wrap, so UVs past 0..1 repeat (ClampMaps stays off).
		if ( kv.TryGetValue( "g_vTexCoordScale", out var ts ) ) m.MapScale = Vec2Of( ts );
		if ( kv.TryGetValue( "g_vTexCoordOffset", out var to ) ) m.MapOffset = Vec2Of( to );
		if ( m.AlbedoMap is not null ) m.MapTint = tint; else m.Albedo = new Vec3( m.Albedo.x * tint.x, m.Albedo.y * tint.y, m.Albedo.z * tint.z );
		if ( kv.TryGetValue( "F_SELF_ILLUM", out var si ) && si.Trim() == "1" )
		{
			var st = kv.TryGetValue( "g_vSelfIllumTint", out var s1 ) ? Vec( s1 ) : Vec3.One;
			var sb = kv.TryGetValue( "g_flSelfIllumBrightness", out var s2 ) ? F( s2 ) : 1f;
			m.Emission = st * (sb * 0.35f);
		}
		if ( kv.TryGetValue( "F_TRANSLUCENT", out var tr ) && tr.Trim() == "1" )
		{
			// No see-through here: draw it as the dark glossy sheet it mostly reads as.
			m.Albedo = new Vec3( tint.x * 0.15f, tint.y * 0.15f, tint.z * 0.15f ); m.AlbedoMap = null;
			m.Coat = 1f; m.CoatRoughness = 0.05f;
		}
		return m;
	}

	/// <summary>Blender's default camera: a 36 mm sensor, fit to the wider side (square here).</summary>
	public static float LensToFov( float lensMm ) => 2f * MathF.Atan( 18f / lensMm ) * 180f / MathF.PI;

	/// <summary>A Blender sun from its XYZ euler rotation: it shines down its local −Z, so the direction
	/// toward it is the rotated +Z.</summary>
	static ShadeLight Sun( float rx, float ry, float rz, float strength, Vec3 colour, float angleDeg )
	{
		float R( float d ) => d * MathF.PI / 180f;
		var (x, y, z) = (R( rx ), R( ry ), R( rz ));
		var v = new Vec3( 0, -MathF.Sin( x ), MathF.Cos( x ) );
		v = new Vec3( v.x * MathF.Cos( y ) + v.z * MathF.Sin( y ), v.y, -v.x * MathF.Sin( y ) + v.z * MathF.Cos( y ) );
		v = new Vec3( v.x * MathF.Cos( z ) - v.y * MathF.Sin( z ), v.x * MathF.Sin( z ) + v.y * MathF.Cos( z ), v.z );
		return new ShadeLight( v, strength / MathF.PI, R( angleDeg ), colour );
	}

	/// <summary>Tiles in a grid, each labelled in its corner, on the dark sheet colour.</summary>
	public static Raster Sheet( IReadOnlyList<(string Label, Raster Image)> tiles, int columns, int gap = 4 )
	{
		var w = tiles.Max( t => t.Image.Width ); var h = tiles.Max( t => t.Image.Height );
		columns = Math.Clamp( columns, 1, tiles.Count );
		var rows = (tiles.Count + columns - 1) / columns;
		var sheet = new Raster( columns * (w + gap) + gap, rows * (h + gap) + gap, Raster.Rgb( 0x20, 0x20, 0x20 ) );
		for ( var i = 0; i < tiles.Count; i++ )
		{
			var (label, img) = tiles[i];
			var ox = gap + i % columns * (w + gap); var oy = gap + i / columns * (h + gap);
			for ( var y = 0; y < img.Height; y++ )
				Array.Copy( img.Pixels, y * img.Width, sheet.Pixels, (oy + y) * sheet.Width + ox, img.Width );
			sheet.Rect( ox, oy, Raster.TextWidth( label ) + 12, 22, Raster.Rgb( 0, 0, 0 ), 0.6f );
			sheet.Text( ox + 6, oy + 4, label, Raster.Rgb( 255, 255, 255 ) );
		}
		return sheet;
	}
}
