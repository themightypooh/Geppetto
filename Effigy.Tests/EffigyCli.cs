using System.Collections.Generic;
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
///   effigy topology doc.effigy|model.obj [Body] [poles=12]     faces, poles, edges, quads, symmetry, loops per joint
///   effigy section  doc.effigy|model.obj Body|all z=… | along=Bone [at=…]   width, depth, perimeter, area per slice
///   effigy flex     doc.effigy Body|all [bones=] [angles=90,140] [render=dir]   bend each joint: volume kept, new crossings
///   effigy script  build.txt [doc.effigy] [outdir]     run a build script, from a document or empty
///   effigy fix     doc.effigy <fixes> [out.effigy]     loose, doubles, holes, normals, all
///   effigy parts   out.png                             the part library on one sheet
///   effigy new     out.effigy                          an empty document
///   effigy wardrobe outdir [recipe] [size] [nodrape]   every garment recipe, rendered and checked
///   effigy clothing outdir [recipe] [quick]            the whole Clothing bar, then the live cloth stepped
///   effigy take    dump.json clip.riganim [options]    play a clip on a dumped model, count crossings, render stills
///   effigy sim clip.riganim [model=dump.json|tentacle|citizen] [options]   play a clip headlessly and lint its motion
///   effigy simrig tentacle|citizen out.json [cables=5] [bones=12]        a stand-in model dump for take/sim
///   effigy key clip.riganim bone goal|fk "frame:x,y,z;.." [options]    key a clip from targets (MarionetteCli)
///   effigy fbxdump bones.json source.fbx out.json      a model dump from its source FBX, on an engine skeleton
///   effigy fbxskin dump.json source.fbx [out.json]     give a dump's render meshes the FBX's skin weights
/// </summary>
public static class EffigyCli
{
	public static bool Handles( string verb ) =>
		verb is "render" or "describe" or "measure" or "match" or "topology" or "section" or "flex" or "script" or "fix" or "parts" or "new" or "crop" or "wardrobe" or "clothing" or "take" or "sim" or "simrig" or "key" or "fbxdump" or "fbxskin" or "fbxinfo" or "shade";

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
				"topology" or "section" or "flex" => MeshReport( args ),
				"script" => Script( args ),
				"fix" => Fix( args ),
				"parts" => Parts( args ),
				"new" => New( args ),
				"crop" => Crop( args ),
				"wardrobe" => Wardrobe( args ),
				"clothing" => ClothingSim.Run( args ),
				"take" => Take( args ),
				"sim" => Sim( args ),
				"simrig" => SimRigCommand( args ),
				"key" => MarionetteCli.Key( args ),
				"fbxdump" => FbxDump( args ),
				"fbxinfo" => FbxInfo( args ),
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
		Console.WriteLine( "effigy render|shade|describe|measure|match|script|fix|parts|new|wardrobe|clothing|take|fbxdump|fbxskin … — see EffigyCli.cs" );
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

	/// <summary>
	/// topology / section / flex: the build-script verb of the same name, run on a document or on
	/// a bare .obj (imported as one body named after the file), printed.
	/// </summary>
	static int MeshReport( string[] a )
	{
		if ( a.Length < 2 )
		{
			Console.Error.WriteLine( a[0] switch
			{
				"topology" => "effigy topology doc.effigy|model.obj [Body|all] [poles=12]",
				"section" => "effigy section doc.effigy|model.obj Body|all z=10,20 | x=… | y=… | along=Bone [at=0,0.5,1]   (a list may be a range: z=0..60:7)",
				_ => "effigy flex doc.effigy Body|all [bones=a,b] [angles=90,140] [axis=auto|x|y|z] [render=outdir] [views=4] [size=320]",
			} );
			return 1;
		}

		PartStudio studio;
		if ( a[1].EndsWith( ".obj", StringComparison.OrdinalIgnoreCase ) )
		{
			studio = new PartStudio();
			var name = Path.GetFileNameWithoutExtension( a[1] ).Replace( ' ', '_' );
			var import = BuildScript.Run( studio, $"add Import name={name} source={Path.GetFullPath( a[1] )}" );
			if ( !import.Ok ) { Console.Error.WriteLine( import ); return 1; }
		}
		else
		{
			studio = Load( a[1] );
		}

		var result = BuildScript.Run( studio, string.Join( " ", a.Where( ( _, i ) => i != 1 ) ), Directory.GetCurrentDirectory() );
		Console.WriteLine( result );
		return result.Ok ? 0 : 1;
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
	/// <summary>What is in a skinned FBX: meshes, bounds, bones. effigy fbxinfo file.fbx</summary>
	static int FbxInfo( string[] a )
	{
		var scene = FbxScene.ReadFile( a[1] );

		foreach ( var m in scene.Meshes )
		{
			var lo = new Vec3( m.Points.Min( p => p.x ), m.Points.Min( p => p.y ), m.Points.Min( p => p.z ) );
			var hi = new Vec3( m.Points.Max( p => p.x ), m.Points.Max( p => p.y ), m.Points.Max( p => p.z ) );
			Console.WriteLine( $"mesh {m.Name}: {m.Points.Length} points, {m.Polygons.Count} polys, skin {(m.Weights is null ? "no" : "yes")}, {lo} .. {hi}, materials {string.Join( ",", m.Materials )}" );
		}

		Console.WriteLine( $"{scene.BoneBind.Count} bones" );
		foreach ( var (name, x) in scene.BoneBind.Take( 200 ) )
			Console.WriteLine( $"  {name} <- {scene.BoneParent.GetValueOrDefault( name ) ?? "-"} at {x.Origin}" );
		return 0;
	}

	static int Wardrobe( string[] a )
	{
		if ( a.Length < 2 ) { Console.Error.WriteLine( "effigy wardrobe outdir [recipe name] [size] [nodrape]" ); return 1; }

		var outDir = Path.GetFullPath( a[1] );
		Directory.CreateDirectory( outDir );

		var only = a.Length > 2 && !int.TryParse( a[2], out _ ) && a[2] != "nodrape" ? a[2] : null;
		var size = a.Skip( 2 ).Select( x => int.TryParse( x, out var n ) ? n : 0 ).FirstOrDefault( n => n > 0 );
		if ( only is "openings" or "human" or "female" or "measure" ) only = null;
		if ( size == 0 ) size = 512;
		var drape = !a.Contains( "nodrape" );

		var rig = CitizenSkeleton.Build();
		var skin = SkinBlockout.Build( rig );

		// "human" / "female": the s&box human from the citizen addon's source FBX instead of a
		// body grown from the Citizen skeleton. See SboxHuman.
		if ( a.Contains( "human" ) || a.Contains( "female" ) )
		{
			var fbx = SboxHuman.Find( female: a.Contains( "female" ) );

			if ( fbx is null )
			{
				Console.Error.WriteLine( "No s&box install found - set SBOX_PATH to the sbox folder." );
				return 1;
			}

			(skin, rig) = SboxHuman.Load( fbx );
			Console.WriteLine( $"wearer: {Path.GetFileName( fbx )}, {skin.VertexCount} v, {rig.Count} bones" );
		}

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

			if ( a.Contains( "measure" ) && names[k] == "T-shirt" )
			{
				var m = TeeMeasure.Measure( made.Mesh, skin, rig );
				Console.WriteLine( $"    wearer chest round {m.BodyChest:0.0} in - size M is for 38-40 (Gildan 5000); widest between chest and hem {m.BodyWidest:0.0}, body under the hem {m.BodyHem:0.0}, bare arm at the sleeve end {m.BodyArm:0.0}" );
				Console.WriteLine( $"    {"point of measure",-34} {"Effigy",7} {"real M",7}  diff" );
				foreach ( var (name, ours, real) in TeeMeasure.Compare( m ) )
					Console.WriteLine( $"    {name,-34} {ours,7:0.0} {real,7:0.0}  {ours - real:+0.0;-0.0}" );
			}

			if ( a.Contains( "openings" ) )
			{
				var sheet = MeshSolidify.OuterSheet( made.Mesh, out _ ) ?? made.Mesh;

				var bdeg = new System.Collections.Generic.Dictionary<int, int>();
				foreach ( var (ek, f) in sheet.BuildEdgeFaces() )
					if ( f.Count == 1 ) { bdeg[ek.A] = bdeg.GetValueOrDefault( ek.A ) + 1; bdeg[ek.B] = bdeg.GetValueOrDefault( ek.B ) + 1; }
				Console.WriteLine( $"    boundary verts {bdeg.Count}, pinched {bdeg.Count( kv => kv.Value != 2 )}, nonmanifold edges {sheet.BuildEdgeFaces().Count( kv => kv.Value.Count > 2 )}" );
				foreach ( var kv in bdeg.Where( kv => kv.Value != 2 ).Take( 8 ) )
					Console.WriteLine( $"      pinch {kv.Key} x{kv.Value} at {sheet.Positions[kv.Key]}" );

				foreach ( var loop in GarmentTrim.OrderedBoundaryLoops( sheet ) )
				{
					var c = loop.Aggregate( Vec3.Zero, ( acc, vi ) => acc + sheet.Positions[vi] ) / loop.Count;
					var round = 0f;
					for ( var i = 0; i < loop.Count; i++ ) round += (sheet.Positions[loop[(i + 1) % loop.Count]] - sheet.Positions[loop[i]]).Length;
					Console.WriteLine( $"    opening: {loop.Count} verts, {round:0.#} in round, centre {c}  x {loop.Min( vi => sheet.Positions[vi].x ):0.0}..{loop.Max( vi => sheet.Positions[vi].x ):0.0} y {loop.Min( vi => sheet.Positions[vi].y ):0.0}..{loop.Max( vi => sheet.Positions[vi].y ):0.0} z {loop.Min( vi => sheet.Positions[vi].z ):0.0}..{loop.Max( vi => sheet.Positions[vi].z ):0.0}" );
				}
			}

			var wornOver = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
			var report = GarmentCheck.Run( made.Mesh, wornOver );
			Console.WriteLine( $"{names[k],-12} {clock.ElapsedMilliseconds,6} ms  {made.Mesh.VertexCount,6} v  {report}{(problem is null ? "" : "  [" + problem + "]")}" );

			var file = names[k].ToLowerInvariant().Replace( ' ', '_' ).Replace( "-", "" );
			ModelRender.Named( studio.Bodies, "sheet", size, new RenderOptions { Wireframe = true, Labels = false }, null ).SavePng( Path.Combine( outDir, file + ".png" ) );

			// Shaded, no wire, front / side / back / three-quarter in a row: the views a garment is
			// judged from, at a size where the silhouette can be read.
			var cell = size / 2;
			var look = new Raster( cell * 4, cell );
			var views = new[] { RenderView.Named( "front" ), RenderView.Named( "left" ), RenderView.Named( "back" ), RenderView.Turn( 35f, 12f ) };
			for ( var v = 0; v < views.Length; v++ )
			{
				var shot = ModelRender.Render( studio.Bodies, views[v], cell, new RenderOptions { Wireframe = false, Grid = false, Axes = false, Labels = false, Margin = 0.04f } );
				for ( var y = 0; y < cell; y++ )
					Array.Copy( shot.Pixels, y * cell, look.Pixels, y * look.Width + v * cell, cell );
			}
			look.SavePng( Path.Combine( outDir, file + "_look.png" ) );

			// The same four, framed on the garment, for the seams and the collar.
			var close = new Raster( cell * 4, cell );
			for ( var v = 0; v < views.Length; v++ )
			{
				var shot = ModelRender.Render( studio.Bodies, views[v], cell, new RenderOptions { Wireframe = true, Grid = false, Axes = false, Labels = false, Margin = 0.06f, FocusBodyId = made.Id } );
				for ( var y = 0; y < cell; y++ )
					Array.Copy( shot.Pixels, y * cell, close.Pixels, y * close.Width + v * cell, cell );
			}
			close.SavePng( Path.Combine( outDir, file + "_close.png" ) );

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
			clip.UseRig( File.ReadAllText( rigPath ) );
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

	/// <summary>
	/// A stand-in model, written as a dump: <c>tentacle</c> (a heap with cable arms and hands, the
	/// Rigging shape) or <c>citizen</c> (Citizen's skeleton in tubes). Feeds <c>take</c> and <c>sim</c>
	/// when there is no editor to dump a real model from.
	/// </summary>
	static int SimRigCommand( string[] a )
	{
		if ( a.Length < 3 ) { Console.Error.WriteLine( "effigy simrig tentacle|citizen out.json [cables=5] [bones=12] [length=90] [nofingers] [pipe=x,y,z:x,y,z:radius]" ); return 1; }

		string Opt( string key ) => a.Skip( 3 ).FirstOrDefault( x => x.StartsWith( key + "=" ) )?[(key.Length + 1)..];
		var dump = BuildSimRig( a[1], Opt, a.Contains( "nofingers" ) );

		if ( dump is null ) { Console.Error.WriteLine( $"no stand-in called '{a[1]}' - tentacle or citizen" ); return 1; }

		if ( Opt( "pipe" ) is { } pipe )
		{
			var p = pipe.Split( ':' );
			if ( p.Length == 3 && ModelMeasure.TryParseVec3( p[0], out var from ) && ModelMeasure.TryParseVec3( p[1], out var to ) )
				dump.Props.Add( SimRig.Pipe( "pipe", from, to, float.Parse( p[2], System.Globalization.CultureInfo.InvariantCulture ) ) );
		}

		dump.WriteFile( a[2] );
		Console.WriteLine( $"{dump.Model}: {dump.Skeleton.Count} bones, {dump.Meshes.Sum( m => m.TriangleCount )} triangles, {dump.Props.Count} prop(s) -> {a[2]}" );
		return 0;
	}

	static ModelDump BuildSimRig( string kind, Func<string, string> opt, bool noFingers )
	{
		int Int( string key, int fallback ) => opt( key ) is { } v ? int.Parse( v ) : fallback;
		float Flt( string key, float fallback ) => opt( key ) is { } v ? float.Parse( v, System.Globalization.CultureInfo.InvariantCulture ) : fallback;

		return kind switch
		{
			"tentacle" => SimRig.Tentacle( Int( "cables", 5 ), Int( "bones", 12 ), Flt( "length", 90f ), !noFingers ),
			"citizen" => SimRig.Citizen(),
			_ => null
		};
	}

	/// <summary>
	/// Play a Marionette clip headlessly and lint its motion (MotionLint): pops, sliding contacts,
	/// unreached goals, stretch, drift in holds, the loop seam, overlap order. Optionally bake
	/// secondary motion first and write the result, trace the motion to a picture, and run the take
	/// check's clipping on the same model. Exit 0 clean, 3 when anything failed.
	/// </summary>
	static int Sim( string[] a )
	{
		if ( a.Length < 2 )
		{
			Console.Error.WriteLine( "effigy sim clip.riganim [model=dump.json|tentacle|citizen] [rig=x.ctrlrig] [substeps=2] [from=F] [to=F] "
				+ "[contacts=glob,..] [chain=prefix,..] [loop] [pop=25] [slide=0.25] [reach=0.25] [ignore=glob,..|none] "
				+ "[layer=clip.riganim[:additive|overlay][:loop][:offset=F][:rate=R][:weight=W][:mask=glob;glob],..] "
				+ "[bake=root:preset[:loop][:rope][:pin=bone],..] [collide=bone:radius,..] [ground=z] [thickness=t] [wave=root:preset[:loop][:amp=][:period=][:wavelength=],..] [rootmotion=root:hips[:xy|xyyaw|full][:exact]] [write=out.riganim] [trail=out.png] [trace=bone,..] [take]" );
			return 1;
		}

		string Opt( string key ) => a.Skip( 2 ).FirstOrDefault( x => x.StartsWith( key + "=" ) )?[(key.Length + 1)..];
		float? Num( string key ) => Opt( key ) is { } v ? float.Parse( v, System.Globalization.CultureInfo.InvariantCulture ) : null;
		List<string> List( string key ) => (Opt( key ) ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).ToList();

		var json = File.ReadAllText( a[1] );
		var clip = RigAnimFile.Read( json );

		if ( Opt( "rig" ) is { } rigPath )
			clip.UseRig( File.ReadAllText( rigPath ) );
		else if ( clip.IkGoalTracks.Any() )
			Console.WriteLine( "clip keys IK goals but no rig= was given - solving them as two-bone chains bending up" );

		// The model: a dump, a named stand-in, or a guess from the clip.
		var modelArg = Opt( "model" )
			?? (clip.SourceModel.Contains( "citizen", StringComparison.OrdinalIgnoreCase ) ? "citizen"
				: clip.Tracks.Any( t => t.Name.StartsWith( "cable" ) ) ? "tentacle" : null);

		if ( modelArg is null ) { Console.Error.WriteLine( "no model= given and none can be guessed from the clip - pass a dump, or model=tentacle / model=citizen" ); return 1; }

		var dump = File.Exists( modelArg ) ? ModelDump.ReadFile( modelArg ) : BuildSimRig( modelArg, Opt, false );

		if ( dump is null ) { Console.Error.WriteLine( $"model '{modelArg}' is neither a dump file nor a stand-in (tentacle, citizen)" ); return 1; }

		var skeleton = dump.Skeleton;
		var missing = clip.BoneTracks.Where( t => t.Keys.Count > 0 && skeleton.IndexOf( t.Name ) < 0 ).Select( t => t.Name ).ToList();

		Console.WriteLine( $"model {dump.Model}: {skeleton.Count} bones; clip {Path.GetFileName( a[1] )}: {clip.BoneTracks.Count()} bone tracks, {clip.IkGoalTracks.Count()} IK goals, frames 0..{clip.LastFrame} at {clip.FrameRate} fps" );

		if ( missing.Count > 0 )
			Console.WriteLine( $"  {missing.Count} track(s) name bones the model lacks and are ignored: {string.Join( ", ", missing.Take( 8 ) )}{(missing.Count > 8 ? " ..." : "")}" );

		// Layers first - they are keys, and the bakes react to the sum: path[:additive|overlay][:loop][:offset=][:rate=][:weight=][:mask=a;b].
		foreach ( var spec in List( "layer" ) )
		{
			var p = spec.Split( ':' );

			if ( !File.Exists( p[0] ) ) { Console.Error.WriteLine( $"layer '{spec}': no file {p[0]}" ); return 1; }

			var settings = new ClipLayer.Settings
			{
				Mode = p.Contains( "overlay" ) ? RigLayerMode.Overlay : RigLayerMode.Additive,
				Loop = p.Contains( "loop" ),
			};

			foreach ( var kv in p.Skip( 1 ).Where( x => x.Contains( '=' ) ).Select( x => x.Split( '=', 2 ) ) )
			{
				if ( kv[0] == "mask" ) { settings.Mask = kv[1].Split( ';', StringSplitOptions.RemoveEmptyEntries ).ToList(); continue; }

				var v = float.Parse( kv[1], System.Globalization.CultureInfo.InvariantCulture );
				if ( kv[0] == "offset" ) settings.Offset = (int)MathF.Round( v );
				else if ( kv[0] == "rate" ) settings.Rate = v;
				else if ( kv[0] == "weight" ) settings.Weight = v;
			}

			var layered = ClipLayer.Merge( clip, RigAnimFile.ReadFile( p[0] ), settings, skeleton );

			if ( !layered.Ok ) { Console.Error.WriteLine( $"layer '{spec}': {layered.Error}" ); return 1; }

			Console.WriteLine( $"layered {Path.GetFileName( p[0] )} ({settings.Mode}{(settings.Loop ? ", loop" : "")}): {layered.Tracks} track(s), {layered.Keys} key(s)"
				+ (layered.Notes.Count > 0 ? "; " + string.Join( "; ", layered.Notes ) : "") );
		}

		// What soft bones collide with: the rig's (rig=), plus collide=bone:radius,.. ground=z thickness=t.
		if ( Opt( "collide" ) is { } || Opt( "ground" ) is { } || Opt( "thickness" ) is { } )
		{
			clip.Collision ??= new RigSoftCollision();
			clip.Collision.Capsules.AddRange( RigSoftCollision.Parse( Opt( "collide" ) ) );

			if ( Num( "ground" ) is { } floor ) clip.Collision.Ground = floor;
			if ( Num( "thickness" ) is { } thick ) clip.Collision.Thickness = thick;
		}

		// Secondary bakes, in order: root:preset[:loop][:rope][:pin=bone]. Presets by key or index; or
		// root:stiffness/damping/weight/angle. The rope preset (or :rope) keeps length and drapes.
		foreach ( var spec in List( "bake" ) )
		{
			var p = spec.Split( ':' );
			var preset = ChainSpring.FindPreset( p.Length > 1 ? p[1] : "loose" );
			SoftBone soft;

			if ( preset is not null )
				soft = preset.ToSoftBone();
			else if ( p.Length > 1 && p[1].Split( '/' ) is { Length: 4 } n )
			{
				var f = n.Select( x => float.Parse( x, System.Globalization.CultureInfo.InvariantCulture ) ).ToArray();
				soft = new SoftBone { Stiffness = f[0], Damping = f[1], Weight = f[2], MaxAngle = f[3] };
			}
			else { Console.Error.WriteLine( $"bake '{spec}': preset is loose, heavy, springy, stiff, rope, 0-4, or stiffness/damping/weight/angle" ); return 1; }

			var rope = preset?.Rope == true || p.Contains( "rope" );
			var pin = p.FirstOrDefault( x => x.StartsWith( "pin=" ) )?[4..];
			var baked = ClipBake.Secondary( clip, skeleton, p[0], soft, p.Contains( "loop" ), rope: rope, pin: pin );

			if ( !baked.Ok ) { Console.Error.WriteLine( $"bake '{spec}': {baked.Error}" ); return 1; }

			Console.WriteLine( $"baked {p[0]} ({preset?.Key ?? "custom"}{(rope ? ", rope" : "")}{(pin is null ? "" : $", pinned at {pin}")}): {baked.Bones} bone(s), {baked.Keys} key(s)"
				+ (baked.SkippedForIk.Count > 0 ? $", skipped {baked.SkippedForIk.Count} posed by live IK" : "")
				+ (clip.Collision?.Any == true ? $", colliding with {clip.Collision.Capsules.Count} capsule(s){(clip.Collision.Ground is { } g ? $" and the floor at z={g}" : "")}" : "")
				+ (baked.Notes.Count > 0 ? "; " + string.Join( "; ", baked.Notes ) : "") );
		}

		// Waves: root:preset[:loop][:amp=12][:period=40][:wavelength=8], each chain its own phase.
		foreach ( var spec in List( "wave" ) )
		{
			var p = spec.Split( ':' );
			var settings = ChainWave.FindPreset( p.Length > 1 && !p[1].Contains( '=' ) && p[1] != "loop" ? p[1] : "writhe" );

			if ( settings is null ) { Console.Error.WriteLine( $"wave '{spec}': preset is sway, writhe, wriggle or undulate" ); return 1; }

			foreach ( var kv in p.Skip( 1 ).Where( x => x.Contains( '=' ) ).Select( x => x.Split( '=' ) ) )
			{
				var v = float.Parse( kv[1], System.Globalization.CultureInfo.InvariantCulture );
				if ( kv[0] == "amp" ) settings.Amplitude = v;
				else if ( kv[0] == "period" ) settings.Period = v;
				else if ( kv[0] == "wavelength" ) settings.Wavelength = v;
				else if ( kv[0] == "taper" ) settings.Taper = v;
			}

			settings.Offset = ChainWave.OffsetFor( p[0], settings.Period );
			var waved = ClipBake.Wave( clip, skeleton, p[0], settings, p.Contains( "loop" ) );

			if ( !waved.Ok ) { Console.Error.WriteLine( $"wave '{spec}': {waved.Error}" ); return 1; }

			Console.WriteLine( $"waved {p[0]}: {waved.Bones} bone(s), {waved.Keys} key(s)" + (waved.SkippedForIk.Count > 0 ? $", skipped {waved.SkippedForIk.Count} posed by live IK" : "") );
		}

		// Root motion: root:hips[:xy|xyyaw|full][:exact] moves the hips' travel onto the root; the
		// clip's own (RootMotion in the file) is used as it is. Either way the lint then judges the
		// clip in place - as it plays on a character the game moves - and feet are reported both ways.
		if ( Opt( "rootmotion" ) is { } rm )
		{
			var p = rm.Split( ':' );
			var settings = new RootMotionSettings
			{
				Bone = p[0],
				Source = p.Length > 1 ? p[1] : "",
				Mode = p.Contains( "full" ) ? RootMotionMode.Full : p.Contains( "xyyaw" ) ? RootMotionMode.XYYaw : RootMotionMode.XY,
				Linear = !p.Contains( "exact" ),
			};
			var extracted = RootMotion.Extract( clip, skeleton, settings );

			if ( !extracted.Ok ) { Console.Error.WriteLine( $"rootmotion '{rm}': {extracted.Error}" ); return 1; }

			Console.WriteLine( extracted );
		}

		if ( clip.RootMotion is { Enabled: true } )
		{
			foreach ( var foot in RootMotion.SlideReport( clip, skeleton, List( "contacts" ) ) )
				Console.WriteLine( string.Create( System.Globalization.CultureInfo.InvariantCulture,
					$"  foot slide {foot.Bone}: {foot.InPlace:0.##} units in place without root motion, {foot.WithRootMotion:0.##} with it ({foot.Spans} planted span(s))" ) );

			clip.PlayInPlace = true;
		}

		if ( Opt( "write" ) is { } writePath )
		{
			File.WriteAllText( writePath, RigAnimWriter.Write( clip, json ) );
			Console.WriteLine( $"wrote {writePath}" );
		}

		var lint = new MotionLint( skeleton, clip );
		var options = new MotionLint.Options
		{
			Substeps = (int)(Num( "substeps" ) ?? 2),
			From = Num( "from" ),
			To = Num( "to" ),
			Contacts = List( "contacts" ),
			Chains = List( "chain" ),
			Loop = a.Contains( "loop" ),
		};

		if ( Opt( "ignore" ) is { } ignore ) options.Ignore = ignore == "none" ? new List<string>() : List( "ignore" );
		if ( Num( "pop" ) is { } pop ) options.PopDegrees = pop;
		if ( Num( "slide" ) is { } slide ) options.SlideTolerance = slide;
		if ( Num( "reach" ) is { } reach ) options.ReachTolerance = reach;

		var result = lint.Run( options );
		Console.WriteLine( result );
		var clean = result.Clear;

		if ( Opt( "trail" ) is { } trailPath )
		{
			var traced = List( "trace" ).Select( skeleton.IndexOf ).Where( i => i >= 0 ).ToList();

			if ( traced.Count == 0 )
				traced = MotionTrail.DefaultTraced( skeleton, clip );

			var from = options.From ?? 0f;
			var to = options.To ?? clip.LastFrame;
			MotionTrail.Render( skeleton, lint.Pose, from, to, traced, title: Path.GetFileName( a[1] ) ).SavePng( trailPath );
			Console.WriteLine( $"trail: {traced.Count} bone(s) traced -> {trailPath}" );
		}

		if ( a.Contains( "take" ) )
		{
			// A stand-in's tubes overlap at every joint by construction, so its self-crossings are
			// noise; its props (a pipe to clamp) are still worth checking.
			var standIn = !File.Exists( modelArg );

			if ( standIn )
				Console.WriteLine( "take: the stand-in's tube skin overlaps itself at the joints, so only props are checked" );

			var take = new TakeAudit( dump, clip ).Run( new TakeAudit.Options { From = options.From, To = options.To, Self = !standIn, PartPairs = !standIn } );
			Console.WriteLine( take );
			clean &= take.Clear;
		}

		return clean ? 0 : 3;
	}
}
