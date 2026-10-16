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
///   effigy shade   model.obj outdir [options]          lit judging renders + labelled sheet, no Blender (ShadeCommand)
///   effigy describe doc.effigy                         the model in words
///   effigy measure doc.effigy <landmark|x,y,z>         what is at a place
///   effigy match   doc.effigy <view> ref.png [out.png] silhouette against a drawing
///   effigy script  build.txt [doc.effigy] [outdir]     run a build script, from a document or empty
///   effigy fix     doc.effigy <fixes> [out.effigy]     loose, doubles, holes, normals, all
///   effigy parts   out.png                             the part library on one sheet
///   effigy new     out.effigy                          an empty document
///   effigy wardrobe outdir [recipe] [size] [nodrape]   every garment recipe, rendered and checked
///   effigy take    dump.json clip.riganim [options]    play a clip on a dumped model, count crossings, render stills
///   effigy fbxdump bones.json source.fbx out.json      a model dump from its source FBX, on an engine skeleton
///   effigy fbxskin dump.json source.fbx [out.json]     give a dump's render meshes the FBX's skin weights
/// </summary>
public static class EffigyCli
{
	public static bool Handles( string verb ) =>
		verb is "render" or "describe" or "measure" or "match" or "script" or "fix" or "parts" or "new" or "crop" or "wardrobe" or "take" or "fbxdump" or "fbxskin" or "shade";

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
				"wardrobe" => Wardrobe( args ),
				"take" => Take( args ),
				"fbxdump" => FbxDump( args ),
				"fbxskin" => FbxSkin( args ),
				"shade" => ShadeCommand.Run( args ),
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
		Console.WriteLine( "effigy render|shade|describe|measure|match|script|fix|parts|new|wardrobe|take|fbxdump|fbxskin … — see EffigyCli.cs" );
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

	/// <summary>
	/// Every garment recipe at its defaults, on a smooth Citizen-proportioned body (the Citizen
	/// skeleton under <see cref="SkinBlockout"/>), one sheet each plus a front-view overview, with
	/// the Check report and build time per recipe. The way to look at the whole clothing kernel
	/// without the editor: effigy wardrobe outdir [recipe] [size] [nodrape]
	/// </summary>
	static int Wardrobe( string[] a )
	{
		if ( a.Length < 2 ) { Console.Error.WriteLine( "effigy wardrobe outdir [recipe name] [size] [nodrape]" ); return 1; }

		var outDir = Path.GetFullPath( a[1] );
		Directory.CreateDirectory( outDir );

		var only = a.Length > 2 && !int.TryParse( a[2], out _ ) && a[2] != "nodrape" ? a[2] : null;
		var size = a.Skip( 2 ).Select( x => int.TryParse( x, out var n ) ? n : 0 ).FirstOrDefault( n => n > 0 );
		if ( size == 0 ) size = 512;
		var drape = !a.Contains( "nodrape" );

		var rig = CitizenSkeleton.Build();
		var skin = SkinBlockout.Build( rig );
		var objBytes = System.Text.Encoding.UTF8.GetBytes( ObjWriter.Write( skin, "wearer" ) );

		var tile = 260;
		var names = GarmentRecipe.Names.Where( n => only is null || n.Equals( only, StringComparison.OrdinalIgnoreCase ) ).ToList();
		var overview = new Raster( tile * Math.Min( 5, names.Count ), tile * ((names.Count + 4) / 5) );

		for ( var k = 0; k < names.Count; k++ )
		{
			var index = Array.IndexOf( GarmentRecipe.Names, names[k] );
			var studio = new PartStudio { Rig = rig.Clone() };
			var import = studio.Add( new ImportFeature { Name = "Wearer" } );
			import.LoadMesh( objBytes );

			var garment = studio.Add( new GarmentFeature() );
			garment.Recipe.Index = index;
			var (length, sleeve) = GarmentRecipe.Defaults( index );
			garment.Length.Value = length;
			garment.Sleeve.Value = sleeve;
			garment.Drape.Value = drape;

			var clock = System.Diagnostics.Stopwatch.StartNew();
			studio.Rebuild();
			clock.Stop();

			var made = studio.Bodies.FirstOrDefault( b => b.IsGarment );
			var problem = garment.Diagnostic?.Problem;

			if ( made is null )
			{
				Console.WriteLine( $"{names[k],-12} NOTHING BUILT {problem}" );
				continue;
			}

			var wornOver = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
			var report = GarmentCheck.Run( made.Mesh, wornOver );
			Console.WriteLine( $"{names[k],-12} {clock.ElapsedMilliseconds,6} ms  {made.Mesh.VertexCount,6} v  {report}{(problem is null ? "" : "  [" + problem + "]")}" );

			var file = names[k].ToLowerInvariant().Replace( ' ', '_' ).Replace( "-", "" );
			ModelRender.Named( studio.Bodies, "sheet", size, new RenderOptions { Wireframe = true, Labels = false }, null ).SavePng( Path.Combine( outDir, file + ".png" ) );

			var front = ModelRender.Render( studio.Bodies, RenderView.Turn( 25f, 10f ), tile, new RenderOptions { Grid = false, Axes = false, Labels = false } );
			var ox = (k % 5) * tile;
			var oy = (k / 5) * tile;
			for ( var y = 0; y < tile; y++ )
				Array.Copy( front.Pixels, y * tile, overview.Pixels, (oy + y) * overview.Width + ox, tile );
		}

		var overviewPath = Path.Combine( outDir, "wardrobe.png" );
		overview.SavePng( overviewPath );
		Console.WriteLine( overviewPath );
		return 0;
	}

	/// <summary>
	/// A take's sign-off without Blender or the editor: a model dumped by the editor's
	/// effigy_model_dump, a Marionette clip, and <see cref="TakeAudit"/> between them.
	///
	///   effigy take dump.json clip.riganim [substeps=4] [from=F] [to=F] [noself] [nopairs] [noprops]
	///                [depth=0] [allow=glob:glob@depth,...] [gap=8] [stills=N | frames=a,b,c]
	///                [camera=bone | eye=x,y,z [look=x,y,z] | eyetrack=take.json [eyekey=Eye]]
	///                [fov=80] [size=640x360] [out=dir] [rig=arms.ctrlrig]
	///
	/// rig= is the clip's Control Rig, for a clip posed with live IK goals: its constraints say how
	/// each goal is solved (chain length, pole, weight). Without it a goal is solved as a two-bone
	/// chain bending up, and the report says so.
	///
	/// Prints the report and exits 0 when the take is clear, 3 when anything crosses. Stills go to
	/// out/take_sheet.png (and one PNG per frame), from the model's origin looking down +x - the
	/// first-person eye of a view model - unless a camera bone, an eye point, or a take file's eye
	/// track (followed frame by frame, see <see cref="EyeTrack"/>) is given. A still
	/// with nothing in it says so, rather than leaving an empty sheet to puzzle over.
	/// </summary>
	static int Take( string[] a )
	{
		if ( a.Length < 3 ) { Console.Error.WriteLine( "effigy take dump.json clip.riganim [substeps=4] [from=F] [to=F] [noself] [nopairs] [noprops] [depth=0] [allow=glob:glob@depth,...] [gap=8] [stills=N|frames=a,b,c] [camera=bone | eye=x,y,z [look=x,y,z] | eyetrack=take.json [eyekey=Eye]] [fov=80] [size=640x360] [out=dir] [rig=arms.ctrlrig]" ); return 1; }

		string Opt( string key ) => a.Skip( 3 ).FirstOrDefault( x => x.StartsWith( key + "=" ) )?[(key.Length + 1)..];
		float? Num( string key ) => Opt( key ) is { } v ? float.Parse( v, System.Globalization.CultureInfo.InvariantCulture ) : null;

		var dump = ModelDump.ReadFile( a[1] );
		var clip = RigAnimFile.ReadFile( a[2] );

		if ( Opt( "rig" ) is { } rigPath )
			clip.Ik = RigIk.ReadRig( File.ReadAllText( rigPath ) );
		else if ( clip.IkGoalTracks.Any() )
			Console.WriteLine( "clip keys IK goals but no rig= was given - solving them as two-bone chains bending up" );

		var audit = new TakeAudit( dump, clip );
		var options = new TakeAudit.Options
		{
			Substeps = (int)(Num( "substeps" ) ?? 4),
			From = Num( "from" ),
			To = Num( "to" ),
			Self = !a.Contains( "noself" ),
			PartPairs = !a.Contains( "nopairs" ),
			Props = !a.Contains( "noprops" ),
			GapRange = Num( "gap" ) ?? 8f,
			DepthTolerance = Num( "depth" ) ?? 0f,
			// allow=glove_*:hard_*/hab_rubber@0.5,... or allow=@rules.txt (one rule per line, # comments).
			Allow = Opt( "allow" ) is { } rules
				? TakeAudit.Allowance.ParseList( rules.StartsWith( '@' )
					? string.Join( ",", File.ReadAllLines( rules[1..] ).Select( r => r.Split( '#' )[0].Trim() ).Where( r => r.Length > 0 ) )
					: rules )
				: new System.Collections.Generic.List<TakeAudit.Allowance>(),
		};

		Console.WriteLine( $"model {dump.Model}: {dump.Skeleton.Count} bones, {dump.Meshes.Count} part(s), {dump.Meshes.Sum( m => m.TriangleCount )} triangles, {dump.Props.Count} prop(s)" );
		Console.WriteLine( $"clip {Path.GetFileName( a[2] )}: {clip.BoneTracks.Count()} bone tracks, frames 0..{clip.LastFrame} at {clip.FrameRate} fps (made on {clip.SourceModel})" );

		var watch = System.Diagnostics.Stopwatch.StartNew();
		var result = audit.Run( options );
		Console.WriteLine( result );
		Console.WriteLine( $"checked in {watch.ElapsedMilliseconds} ms" );

		// Stills: every listed frame, or N spread evenly over the clip.
		var frames = Opt( "frames" ) is { } list
			? list.Split( ',' ).Select( f => float.Parse( f, System.Globalization.CultureInfo.InvariantCulture ) ).ToList()
			: Num( "stills" ) is { } n && n > 0
				? Enumerable.Range( 0, (int)n ).Select( i => n <= 1 ? 0f : MathF.Round( i * clip.LastFrame / (n - 1f) * 4f ) / 4f ).ToList()
				: new System.Collections.Generic.List<float>();

		if ( frames.Count > 0 )
		{
			var outDir = Opt( "out" ) ?? Path.GetDirectoryName( Path.GetFullPath( a[2] ) );
			Directory.CreateDirectory( outDir );

			var size = (Opt( "size" ) ?? "640x360").Split( 'x' );
			var width = int.Parse( size[0] );
			var height = size.Length > 1 ? int.Parse( size[1] ) : width * 9 / 16;
			var fov = Num( "fov" ) ?? 80f;
			var bone = Opt( "camera" );
			Vec3? eye = Opt( "eye" ) is { } e && ModelMeasure.TryParseVec3( e, out var ev ) ? ev : null;
			Vec3? look = Opt( "look" ) is { } l && ModelMeasure.TryParseVec3( l, out var lv ) ? lv : null;
			var track = Opt( "eyetrack" ) is { } trackPath ? EyeTrack.ReadFile( trackPath, Opt( "eyekey" ) ?? "Eye" ) : null;
			var tiles = new System.Collections.Generic.List<Raster>();
			var empty = new System.Collections.Generic.List<float>();

			foreach ( var f in frames )
			{
				var camera = bone is not null ? audit.BoneCamera( bone, f, fov )
					: eye is { } at ? EyeCamera.LookAt( at, look ?? at + new Vec3( 1, 0, 0 ), fov )
					: track is not null ? track.At( f / Math.Max( 1, clip.FrameRate ), fov )
					: EyeCamera.At( Xform.Identity, fov );

				if ( camera is null ) { Console.Error.WriteLine( $"no bone called '{bone}' in the model" ); return 1; }

				var still = audit.Still( f, camera, width, height );
				if ( still.Pixels.All( p => p == still.Pixels[0] ) )
					empty.Add( f );
				var name = Path.Combine( outDir, $"take_{f.ToString( "0.##", System.Globalization.CultureInfo.InvariantCulture )}.png" );
				still.SavePng( name );
				tiles.Add( still );
			}

			var sheet = EyeRender.Sheet( tiles, Math.Min( 4, tiles.Count ), frames.Select( f => $"frame {f:0.##}" ).ToList() );
			var sheetPath = Path.Combine( outDir, "take_sheet.png" );
			sheet.SavePng( sheetPath );
			Console.WriteLine( $"stills: {frames.Count} frame(s), sheet {sheetPath}" );

			if ( empty.Count > 0 )
				Console.WriteLine( $"  nothing in view at frame(s) {string.Join( ", ", empty )} - the model is outside the camera. Name a camera bone, or place the eye with eye=x,y,z look=x,y,z" );
		}

		return result.Clear ? 0 : 3;
	}

	/// <summary>
	/// A model dump with no editor: the skeleton (and any poses) from a dump that has bones -
	/// arms_pose_dump's output is enough - and the meshes and skin weights from the FBX the model
	/// was compiled from, lined up with the engine's model space by the bones the two share.
	/// </summary>
	static int FbxDump( string[] a )
	{
		if ( a.Length < 4 ) { Console.Error.WriteLine( "effigy fbxdump bones.json source.fbx out.json" ); return 1; }

		var bones = ModelDump.ReadFile( a[1] );
		var fbx = FbxScene.ReadFile( a[2] );
		var dump = ModelDump.FromFbx( bones, fbx, out var fit );

		dump.WriteFile( a[3] );
		Console.WriteLine( $"fbx {Path.GetFileName( a[2] )}: {fbx.Meshes.Count} mesh(es), {fbx.BoneBind.Count} bone(s); {fit}" );
		foreach ( var part in dump.Meshes )
			Console.WriteLine( $"  {part.Name}: {part.Positions.Length} vertices, {part.TriangleCount} triangles{(part.Weights is null ? ", not skinned" : part.Weights.Vertices.Count( w => w.Length == 0 ) is > 0 and var bare ? $", {bare} vertices on no bone the model has" : "")}" );
		Console.WriteLine( a[3] );
		return fit.BoneError < 0.05f ? 0 : 3;
	}

	/// <summary>Skin weights for a dump the editor wrote, from the model's source FBX.</summary>
	static int FbxSkin( string[] a )
	{
		if ( a.Length < 3 ) { Console.Error.WriteLine( "effigy fbxskin dump.json source.fbx [out.json]" ); return 1; }

		var dump = ModelDump.ReadFile( a[1] );
		var fit = dump.AttachWeights( FbxScene.ReadFile( a[2] ) );
		var output = a.Length > 3 ? a[3] : a[1];

		dump.WriteFile( output );
		Console.WriteLine( fit );
		Console.WriteLine( output );
		return fit.BoneError < 0.05f && fit.WorstVertex < 0.05f ? 0 : 3;
	}

	static int New( string[] a )
	{
		if ( a.Length < 2 ) { Console.Error.WriteLine( "effigy new out.effigy" ); return 1; }
		StudioDocument.WriteFile( new PartStudio(), a[1] );
		Console.WriteLine( a[1] );
		return 0;
	}
}
