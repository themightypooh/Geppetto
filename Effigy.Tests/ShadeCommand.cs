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
		if ( a.Length < 3 ) { Console.Error.WriteLine( "effigy shade model.obj outdir [size=] [views=] [turn=] [cam= look= lens=] [clay=1] [silhouette=0] [ground=0] [ss=2] [sheet=0]" ); return 1; }

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
		scene.Add( mesh, material );
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
			foreach ( var v in new[] { "front", "side", "quarter" } )
				Shot( $"{name} silhouette {v}", $"{name}_sil_{v}.png", cams[v] with { Lens = 60 } );
			scene.Items.Clear();
			scene.Add( mesh, material );
			scene.Lights.AddRange( keep.Item1 );
			(scene.Floor, scene.BackgroundBottom, scene.SkyColour, scene.GroundColour, scene.AoStrength) = (keep.Floor, keep.BackgroundBottom, keep.SkyColour, keep.GroundColour, keep.AoStrength);
			scene.BackgroundTop = keep.BackgroundBottom;
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
