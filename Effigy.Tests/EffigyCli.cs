using System;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Render;

namespace Effigy.Tests;

/// <summary>
/// The headless Effigy commands: look at a document, measure it, match it to a drawing, run a
/// build script, clean a mesh. `tools/effigy.sh` is the wrapper; this is what it calls.
///
///   effigy render  doc.effigy out.png [view] [size]   sheet (front/side/top/iso) or one view
///   effigy describe doc.effigy                         the model in words
///   effigy measure doc.effigy <landmark|x,y,z>         what is at a place
///   effigy match   doc.effigy <view> ref.png [out.png] silhouette against a drawing
///   effigy script  build.txt [doc.effigy] [outdir]     run a build script, from a document or empty
///   effigy fix     doc.effigy <fixes> [out.effigy]     loose, doubles, holes, normals, all
///   effigy parts   out.png                             the part library on one sheet
///   effigy new     out.effigy                          an empty document
/// </summary>
public static class EffigyCli
{
	public static bool Handles( string verb ) =>
		verb is "render" or "describe" or "measure" or "match" or "script" or "fix" or "parts" or "new" or "crop";

	public static int Run( string[] args )
	{
		try
		{
			return args[0] switch
			{
				"render" => Render( args ),
				"describe" => Describe( args ),
				"measure" => Measure( args ),
				"match" => Match( args ),
				"script" => Script( args ),
				"fix" => Fix( args ),
				"parts" => Parts( args ),
				"new" => New( args ),
				"crop" => Crop( args ),
				_ => Usage(),
			};
		}
		catch ( Exception e )
		{
			Console.Error.WriteLine( $"effigy {args[0]}: {e.Message}" );
			return 2;
		}
	}

	static int Usage()
	{
		Console.WriteLine( "effigy render|describe|measure|match|script|fix|parts|new … — see EffigyCli.cs" );
		return 1;
	}

	static PartStudio Load( string path )
	{
		var studio = StudioDocument.ReadFile( path );
		studio.Rebuild();
		return studio;
	}

	static int Render( string[] a )
	{
		if ( a.Length < 3 ) { Console.Error.WriteLine( "effigy render doc.effigy out.png [sheet|turn|turn12|front|side|top|iso|back|left] [size] [nowire] [plain] [bones] [focus=Body]" ); return 1; }

		var studio = Load( a[1] );
		var viewName = a.Length > 3 ? a[3] : "sheet";
		var size = a.Length > 4 && int.TryParse( a[4], out var s ) ? s : 512;
		var plain = a.Contains( "plain" );
		var focus = a.FirstOrDefault( x => x.StartsWith( "focus=" ) )?[6..];
		var options = new RenderOptions { Wireframe = !a.Contains( "nowire" ) && !plain, Bones = a.Contains( "bones" ), Grid = !plain, Axes = !plain, Labels = !plain, FocusBodyId = focus is null ? null : ModelMeasure.FindBody( studio, focus )?.Id };

		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( a[2] ) ) ?? "." );

		ModelRender.Named( studio.Bodies, viewName, size, options, studio.Rig ).SavePng( a[2] );

		Console.WriteLine( a[2] );
		Console.WriteLine( ModelMeasure.Describe( studio ) );
		return 0;
	}

	static int Describe( string[] a )
	{
		if ( a.Length < 2 ) { Console.Error.WriteLine( "effigy describe doc.effigy" ); return 1; }
		Console.WriteLine( ModelMeasure.Describe( Load( a[1] ) ) );
		return 0;
	}

	static int Measure( string[] a )
	{
		if ( a.Length < 3 ) { Console.Error.WriteLine( "effigy measure doc.effigy <landmark|x,y,z> [second landmark]" ); return 1; }

		var studio = Load( a[1] );

		if ( !BuildScript.Point( studio, a[2], out var p, out var why ) ) { Console.Error.WriteLine( why ); return 1; }

		if ( a.Length > 3 )
		{
			if ( !BuildScript.Point( studio, a[3], out var q, out var why2 ) ) { Console.Error.WriteLine( why2 ); return 1; }
			var d = q - p;
			Console.WriteLine( $"{a[2]} ({p.x:0.##}, {p.y:0.##}, {p.z:0.##}) to {a[3]} ({q.x:0.##}, {q.y:0.##}, {q.z:0.##}): {d.Length:0.###} (dx {d.x:0.##}, dy {d.y:0.##}, dz {d.z:0.##})" );
			return 0;
		}

		Console.WriteLine( ModelMeasure.At( studio, p ) );
		return 0;
	}

	static int Match( string[] a )
	{
		if ( a.Length < 4 ) { Console.Error.WriteLine( "effigy match doc.effigy <view> ref.png [out.png]" ); return 1; }

		var studio = Load( a[1] );
		var view = RenderView.Named( a[2] );
		if ( view is null ) { Console.Error.WriteLine( $"no view '{a[2]}'" ); return 1; }

		const int size = 256;
		var (w, h, rgb, alpha) = Raster.LoadPng( a[3] );
		var reference = ReferenceMatch.ReferenceSilhouette( w, h, rgb, alpha, size, out var aspect );
		var result = ReferenceMatch.Compare( studio.Bodies, view, reference, aspect, size );

		Console.WriteLine( result.Report );
		Console.WriteLine( "(reference: " + ReferenceMatch.LastRead + ")" );

		if ( a.Length > 4 )
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( a[4] ) ) ?? "." );
			result.Diff.SavePng( a[4] );

			// The reference as it was read, beside the diff, so a bad read is visible rather than guessed at.
			var refRaster = new Raster( size, size );
			for ( var i = 0; i < reference.Length; i++ ) if ( reference[i] ) refRaster.Pixels[i] = 0x5b93ff;
			refRaster.SavePng( Path.ChangeExtension( a[4], ".ref.png" ) );
			Console.WriteLine( a[4] );
		}

		return 0;
	}

	static int Script( string[] a )
	{
		if ( a.Length < 2 ) { Console.Error.WriteLine( "effigy script build.txt [doc.effigy|-] [outdir]" ); return 1; }

		var studio = a.Length > 2 && a[2] != "-" && File.Exists( a[2] ) ? Load( a[2] ) : new PartStudio();
		var outDir = a.Length > 3 ? a[3] : Path.GetDirectoryName( Path.GetFullPath( a[1] ) ) ?? ".";
		var result = BuildScript.Run( studio, File.ReadAllText( a[1] ), outDir );

		Console.WriteLine( result );
		return result.Ok ? 0 : 1;
	}

	static int Fix( string[] a )
	{
		if ( a.Length < 3 ) { Console.Error.WriteLine( "effigy fix doc.effigy loose,doubles,holes,normals|all [out.effigy]" ); return 1; }

		var studio = Load( a[1] );
		var fixes = a[2].Split( ',' );

		// A fix is an edit, so it is recorded as one: a Mesh edit per body, holding the cleaned
		// mesh, which is exactly what Edit mode's own fixes leave in the history.
		foreach ( var body in studio.Bodies.ToList() )
		{
			var edit = new MeshEditFeature();
			edit.Name = $"Fix {body.Name ?? body.Id}";
			edit.Bodies.BodyIds.Add( body.Id );
			var mesh = body.Mesh.Clone();
			foreach ( var line in MeshFix.Apply( mesh, fixes ) )
				Console.WriteLine( $"{body.Name ?? body.Id}: {line}" );

			var applied = new MeshEditSession( body.Mesh, mesh );
			applied.CommitTo( edit );
			studio.Add( edit );
		}

		studio.Rebuild();
		var outPath = a.Length > 3 ? a[3] : a[1];
		StudioDocument.WriteFile( studio, outPath );
		Console.WriteLine( outPath );
		return 0;
	}

	static int Parts( string[] a )
	{
		if ( a.Length < 2 ) { Console.Error.WriteLine( "effigy parts out.png" ); return 1; }

		var bodies = PartLibrary.Names.Select( ( name, i ) =>
		{
			var mesh = PartLibrary.Build( name, 4f, 8f, 1f, 12, 90f );
			return new Body( $"part{i}", name, mesh );
		} ).ToList();

		var tile = 220;
		var columns = 5;
		var rows = (bodies.Count + columns - 1) / columns;
		var sheet = new Raster( tile * columns, tile * rows );

		for ( var i = 0; i < bodies.Count; i++ )
		{
			var one = ModelRender.Render( new[] { bodies[i] }, RenderView.Iso, tile, new RenderOptions { Grid = false, Axes = false } );
			var ox = (i % columns) * tile;
			var oy = (i / columns) * tile;

			for ( var y = 0; y < tile; y++ )
				Array.Copy( one.Pixels, y * tile, sheet.Pixels, (oy + y) * sheet.Width + ox, tile );
		}

		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( a[1] ) ) ?? "." );
		sheet.SavePng( a[1] );
		Console.WriteLine( a[1] );

		foreach ( var name in PartLibrary.Names )
			Console.WriteLine( $"{name}: {PartLibrary.Uses( name )}" );

		return 0;
	}

	/// <summary>Cut a reference down to its subject: effigy crop in.png out.png x y w h (pixels, or
	/// fractions of the image when 1 or less).</summary>
	static int Crop( string[] a )
	{
		if ( a.Length < 7 ) { Console.Error.WriteLine( "effigy crop in.png out.png x y w h" ); return 1; }

		var (w, h, rgb, alpha) = Raster.LoadPng( a[2 - 1] );
		float F( string t ) => float.Parse( t, System.Globalization.CultureInfo.InvariantCulture );
		int P( float v, int total ) => v <= 1f ? (int)MathF.Round( v * total ) : (int)v;
		var x0 = P( F( a[3] ), w ); var y0 = P( F( a[4] ), h ); var cw = P( F( a[5] ), w ); var ch = P( F( a[6] ), h );
		x0 = Math.Clamp( x0, 0, w - 1 ); y0 = Math.Clamp( y0, 0, h - 1 ); cw = Math.Clamp( cw, 1, w - x0 ); ch = Math.Clamp( ch, 1, h - y0 );

		var outRaster = new Raster( cw, ch );
		for ( var y = 0; y < ch; y++ )
			for ( var x = 0; x < cw; x++ )
				outRaster.Pixels[y * cw + x] = rgb[(y0 + y) * w + x0 + x];

		outRaster.SavePng( a[2] );
		Console.WriteLine( $"{a[2]} {cw}x{ch}" );
		return 0;
	}

	static int New( string[] a )
	{
		if ( a.Length < 2 ) { Console.Error.WriteLine( "effigy new out.effigy" ); return 1; }
		StudioDocument.WriteFile( new PartStudio(), a[1] );
		Console.WriteLine( a[1] );
		return 0;
	}
}
