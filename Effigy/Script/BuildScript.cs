using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// A build script: what to add to the studio, in order, in one text file that runs headlessly
/// and in the editor alike.
///
///   # a comment
///   add Primitive name=Torso shape=Box sizex=20 sizey=12 sizez=30 position=@ground+0,0,15
///   add Part name=Buckle part=Buckle size=6 length=4 position=@Torso.front+1,0,-6
///   add Spline name=Cable points=@Torso.back;-10,4,20;-14,0,8 radius=0.8
///   add CurveDeform bodies=Tail
///   add Import name=Scan source=scan.fbx
///   set Torso sizez=32
///   fix Torso all
///   remedy Stroke1                       (press a failing feature's fix button: Subdivide above, …)
///   add Stroke bodies=Sac … inkey=sac_swell   (sculpt into a finished shape key)
///   render out/torso.png view=sheet
///   describe
///   match front reference.png out/match.png
///   bake Cage Sculpt out/bake size=2048 maps=normal,ao,curvature,cavity,height ss=2 vmat=1
///
/// `add TYPE` takes a feature type (Primitive, Spline, Part, Profile, Transform, Mirror,
/// Subdivide…) and `key=value` pairs naming its parameters by label or field. Anywhere a point
/// goes, `@landmark` or `@landmark+dx,dy,dz` names a place on the model — a body's side, a
/// bone, the ground — resolved by <see cref="ModelMeasure.TryLandmark"/> against the model AS
/// BUILT SO FAR, so a part can sit on the body the line before it made.
///
/// Every command runs a rebuild when it changes the model, and a feature that fails stops the
/// script with the feature's own diagnostic — its cause and its remedies — so what comes back
/// is what to change, not a stack trace.
/// </summary>
public static partial class BuildScript
{
	public sealed class Result
	{
		public bool Ok = true;
		public List<string> Log = new();
		public List<string> Written = new();
		public string Error;
		public int ErrorLine;

		/// <summary>Where each body turns or stands, by body name, set with `pivot`. Export moves the
		/// body so this point is its model origin, and the layout says where that point was.</summary>
		public Dictionary<string, Vec3> Pivots = new( StringComparer.OrdinalIgnoreCase );

		public override string ToString()
		{
			var sb = new StringBuilder();
			foreach ( var line in Log ) sb.AppendLine( line );
			if ( Error is not null ) sb.AppendLine( $"ERROR line {ErrorLine}: {Error}" );
			return sb.ToString().TrimEnd();
		}
	}

	/// <summary>Run <paramref name="text"/> against <paramref name="studio"/>. Files a `render`,
	/// `match` or `save` writes go under <paramref name="baseDir"/> when their path is relative.</summary>
	public static Result Run( PartStudio studio, string text, string baseDir = null )
	{
		var result = new Result();
		baseDir ??= Directory.GetCurrentDirectory();

		if ( studio is null )
		{
			result.Ok = false;
			result.Error = "No studio.";
			return result;
		}

		var lines = (text ?? "").Replace( "\r\n", "\n" ).Split( '\n' );

		for ( var i = 0; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 || line.StartsWith( '#' ) )
				continue;

			try
			{
				if ( !Command( studio, Tokenise( line ), baseDir, result ) )
				{
					result.Ok = false;
					result.ErrorLine = i + 1;
					return result;
				}
			}
			catch ( Exception e )
			{
				result.Ok = false;
				result.ErrorLine = i + 1;
				result.Error = e.Message;
				return result;
			}
		}

		return result;
	}

	static bool Command( PartStudio studio, List<string> tokens, string baseDir, Result r )
	{
		if ( tokens.Count == 0 )
			return true;

		var verb = tokens[0].ToLowerInvariant();
		var args = tokens.Skip( 1 ).ToList();

		switch ( verb )
		{
			case "add":
			{
				if ( args.Count == 0 )
					return Fail( r, "add needs a feature type: add Primitive shape=Box …" );

				var feature = Create( args[0] );

				if ( feature is null )
					return Fail( r, $"No feature type '{args[0]}'. Types: {string.Join( ", ", FeatureTypeNames() )}." );

				var pairs = Pairs( args.Skip( 1 ) );

				// inkey=sac_swell: into that finished shape key, just above its End Shape Key.
				pairs.Remove( "inkey", out var intoKey );

				if ( !Assign( studio, feature, pairs, r ) )
					return false;

				if ( intoKey is not null )
				{
					if ( !ShapeKeyEdit.AddToKey( studio, intoKey, feature, out var whyKey ) )
						return Fail( r, whyKey );
				}
				else
				{
					studio.Add( feature );
				}

				// add Import source=leg.fbx - the path is the script's, like save and export, and an
				// FBX's skeleton becomes the rig when there is none yet, as picking it in the editor does.
				if ( feature is ImportFeature import && !string.IsNullOrWhiteSpace( import.Source.Value ) )
				{
					import.BindSource( Resolve( baseDir, import.Source.Value.Trim() ) );

					if ( ImportFeature.InstallSkeleton( studio, import, out var rigMessage ) > 0 || rigMessage.Length > 0 )
						r.Log.Add( rigMessage );
				}

				return RebuildAndCheck( studio, feature, r, $"added {feature.TypeName} '{feature.Name ?? feature.Id}'" + (intoKey is null ? "" : $" into shape key {intoKey}") );
			}

			case "edit":
			{
				// edit Torso extrude select=normal:up distance=4 — an EditOp feature, replayed on every rebuild.
				if ( args.Count < 2 )
					return Fail( r, $"edit needs a body and an operation: edit Torso extrude select=normal:up distance=4. Operations: {string.Join( ", ", EditOpFeature.Operations.Select( o => Normalise( o ) ) )}." );

				if ( ModelMeasure.FindBody( studio, args[0] ) is null )
					return Fail( r, $"No body called '{args[0]}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );

				var op = Array.FindIndex( EditOpFeature.Operations, o => Normalise( o ) == Normalise( args[1] ) );

				if ( op < 0 )
					return Fail( r, $"No edit operation '{args[1]}'. Operations: {string.Join( ", ", EditOpFeature.Operations.Select( o => Normalise( o ) ) )}." );

				var feature = new EditOpFeature();
				feature.Op.Index = op;
				var pairs = Pairs( args.Skip( 2 ) );
				pairs["bodies"] = args[0];
				pairs.TryAdd( "name", $"{args[0]} {Normalise( EditOpFeature.Operations[op] )}" );

				if ( !Assign( studio, feature, pairs, r ) )
					return false;

				studio.Add( feature );
				return RebuildAndCheck( studio, feature, r, $"{Normalise( EditOpFeature.Operations[op] )} on {args[0]}" );
			}

			case "set":
			{
				if ( args.Count < 2 )
					return Fail( r, "set needs a feature name and key=value pairs." );

				var feature = FindFeature( studio, args[0] );

				if ( feature is null )
					return Fail( r, $"No feature called '{args[0]}'. Features: {string.Join( ", ", studio.Features.Select( f => f.Name ?? f.TypeName ) )}." );

				if ( !Assign( studio, feature, Pairs( args.Skip( 1 ) ), r ) )
					return false;

				studio.MarkDirty( feature );
				return RebuildAndCheck( studio, feature, r, $"set {feature.Name ?? feature.TypeName}" );
			}

			case "remove":
			{
				if ( args.Count < 1 )
					return Fail( r, "remove needs a feature name." );

				var feature = FindFeature( studio, args[0] );

				if ( feature is null )
					return Fail( r, $"No feature called '{args[0]}'." );

				studio.Remove( feature );
				studio.Rebuild();
				r.Log.Add( $"removed {feature.Name ?? feature.TypeName}" );
				return true;
			}

			case "remedy":
			{
				// remedy Stroke1 [2] [folder=out/tex]: press the fix button on a feature's diagnostic.
				if ( args.Count < 1 )
					return Fail( r, "remedy needs a feature name: remedy Stroke1 (the first fix), remedy Stroke1 2, remedy Flesh folder=out/tex" );

				var feature = FindFeature( studio, args[0] );

				if ( feature is null )
					return Fail( r, $"No feature called '{args[0]}'." );

				studio.Rebuild();
				var actions = feature.Diagnostic?.Actions ?? new List<RemedyAction>();

				if ( actions.Count == 0 )
					return Fail( r, feature.Diagnostic is null
						? $"{feature.Name} has no problem to fix."
						: $"{feature.Name}: {feature.Diagnostic.Problem}. It has no one-click fix; its advice is: {string.Join( "; ", feature.Diagnostic.Remedies )}." );

				var options = Pairs( args.Skip( 1 ).Where( a => a.Contains( '=' ) ) );
				var pick = args.Skip( 1 ).FirstOrDefault( a => !a.Contains( '=' ) );
				var n = pick is not null && int.TryParse( pick, out var parsed ) ? parsed : 1;

				if ( n < 1 || n > actions.Count )
					return Fail( r, $"{feature.Name} has {actions.Count} fix(es): {string.Join( ", ", actions.Select( ( a, i ) => $"{i + 1} {a.Label}" ) )}." );

				var outcome = FeatureRemedy.Apply( studio, feature, actions[n - 1], options.GetValueOrDefault( "folder" ) );

				if ( !outcome.Ok )
					return Fail( r, outcome.Message );

				studio.Rebuild();
				r.Log.Add( outcome.Message + (feature.Diagnostic is null ? $" {feature.Name} builds cleanly now."
					: $" {feature.Name} still says: {feature.Diagnostic.Problem}.") );
				return true;
			}

			case "rebuild":
				studio.Rebuild();
				r.Log.Add( "rebuilt" );
				return true;

			case "fix":
			{
				if ( args.Count < 2 )
					return Fail( r, "fix needs a body (or all) and the fixes: fix Torso loose holes normals" );

				var targets = args[0].Equals( "all", StringComparison.OrdinalIgnoreCase )
					? studio.Bodies.ToList()
					: new List<Body> { ModelMeasure.FindBody( studio, args[0] ) };

				if ( targets.Count == 0 || targets[0] is null )
					return Fail( r, $"No body called '{args[0]}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );

				foreach ( var body in targets )
					foreach ( var line in MeshFix.Apply( body.Mesh, args.Skip( 1 ) ) )
						r.Log.Add( $"{body.Name ?? body.Id} {line}" );

				return true;
			}

			case "render":
			{
				if ( args.Count < 1 )
					return Fail( r, "render needs an output path: render out/model.png view=sheet" );

				var path = Resolve( baseDir, args[0] );
				var opts = Pairs( args.Skip( 1 ) );

				if ( opts.TryGetValue( "shade", out var shade ) )
					return RenderShaded( studio, path, opts, shade, r );

				var viewName = opts.GetValueOrDefault( "view", "sheet" );
				var size = (int)Number( opts.GetValueOrDefault( "size", "512" ) );
				var options = new RenderOptions
				{
					Wireframe = Truthy( opts.GetValueOrDefault( "wire", "1" ) ),
					Bones = Truthy( opts.GetValueOrDefault( "bones", "0" ) ),
					Labels = Truthy( opts.GetValueOrDefault( "labels", "1" ) ),
					Grid = Truthy( opts.GetValueOrDefault( "grid", "1" ) ),
					FocusBodyId = opts.TryGetValue( "focus", out var focus ) ? ModelMeasure.FindBody( studio, focus )?.Id : null,
				};

				Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) ) ?? "." );

				ModelRender.Named( studio.Bodies, viewName, size, options, studio.Rig ).SavePng( path );

				r.Written.Add( path );
				r.Log.Add( $"rendered {viewName} to {path}" );
				return true;
			}

			case "turnaround":
			{
				// turnaround out/turn [shade=clay|material] [size=512] [views=front,quarter,side,back] [name=x] [focus=Body]
				// File ▸ Render Turnaround, from a script: the sheet and one picture per view.
				if ( args.Count < 1 )
					return Fail( r, "turnaround needs a folder: turnaround out/turn [shade=clay|material] [size=512] [views=front,quarter,side,back] [name=model] [focus=Body]" );

				var folder = Resolve( baseDir, args[0] );
				var opts = Pairs( args.Skip( 1 ) );

				foreach ( var key in opts.Keys )
					if ( key is not ("shade" or "size" or "views" or "name" or "focus" or "elevation" or "ss" or "aa" or "preset") )
						return Fail( r, $"turnaround has no option '{key}'. Options: shade, size, views, name, focus, elevation, ss, preset." );

				var look = opts.GetValueOrDefault( "shade", "clay" ).ToLowerInvariant();
				if ( look is not ("clay" or "material") )
					return Fail( r, $"shade={look}: use shade=clay (form only) or shade=material (what the materials make it look like)." );

				var options = new TurnaroundOptions
				{
					Clay = look == "clay",
					Size = (int)Number( opts.GetValueOrDefault( "size", "512" ) ),
					Elevation = Number( opts.GetValueOrDefault( "elevation", "12" ) ),
					Supersample = (int)Number( opts.GetValueOrDefault( "ss", "1" ) ),
					Coverage = (int)Number( opts.GetValueOrDefault( "aa", "16" ) ),
				};

				if ( opts.TryGetValue( "views", out var viewList ) )
					options.Views = viewList.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

				if ( opts.TryGetValue( "focus", out var focusName ) )
				{
					var focusBody = ModelMeasure.FindBody( studio, focusName );
					if ( focusBody is null )
						return Fail( r, $"focus={focusName}: no body called that." );
					options.FocusBodyId = focusBody.Id;
				}

				if ( opts.TryGetValue( "preset", out var preset ) && (options.Fallback = FleshMaterial.Preset( preset )) is null )
					return Fail( r, $"No material preset '{preset}'. Presets: {string.Join( ", ", FleshMaterial.Presets )}." );

				var turned = Turnaround.Write( studio, folder, opts.GetValueOrDefault( "name", "model" ), options );
				r.Log.AddRange( turned.Log );

				if ( !turned.Ok )
					return Fail( r, turned.Error );

				r.Written.AddRange( turned.Written );
				return true;
			}

			case "describe":
				r.Log.Add( ModelMeasure.Describe( studio ) );
				return true;

			case "measure":
			case "at":
			{
				if ( args.Count < 1 )
					return Fail( r, "measure needs a landmark or a point: measure Torso.top" );

				if ( !Point( studio, args[0], out var p, out var why ) )
					return Fail( r, why );

				r.Log.Add( ModelMeasure.At( studio, p ) );
				return true;
			}

			case "distance":
			{
				if ( args.Count < 2 )
					return Fail( r, "distance needs two landmarks: distance Hand_L Hand_R" );

				if ( !Point( studio, args[0], out var a, out var whyA ) ) return Fail( r, whyA );
				if ( !Point( studio, args[1], out var b, out var whyB ) ) return Fail( r, whyB );

				var d = b - a;
				r.Log.Add( $"{args[0]} to {args[1]}: {d.Length:0.###} (dx {d.x:0.###}, dy {d.y:0.###}, dz {d.z:0.###})" );
				return true;
			}

			case "symmetry":
			{
				foreach ( var body in args.Count > 0 ? new[] { ModelMeasure.FindBody( studio, args[0] ) } : studio.Bodies.ToArray() )
				{
					if ( body?.Mesh is null ) continue;
					var reading = SymmetryCheck.Measure( body.Mesh, int.MaxValue );
					r.Log.Add( $"{body.Name ?? body.Id}: left-right symmetry error {reading.ErrorY * 100f:0.#}% of width (across X = 0: {reading.ErrorX * 100f:0.#}%)" );
				}

				return true;
			}

			case "topology":
				return Topology( studio, args, r );

			case "section":
				return Section( studio, args, r );

			case "flex":
				return Flex( studio, args, baseDir, r );

			case "match":
			{
				// match front=ref_front.png side=ref_side.png [out=dir]: several views, one combined score.
				if ( args.Count > 0 && args[0].Contains( '=' ) )
					return MatchViews( studio, args, baseDir, r );

				if ( args.Count < 2 )
					return Fail( r, "match needs a view and a reference PNG: match front ref.png [out.png]" );

				var view = RenderView.Named( args[0] );

				if ( view is null )
					return Fail( r, $"No view called '{args[0]}'." );

				var refPath = Resolve( baseDir, args[1] );

				if ( !File.Exists( refPath ) )
					return Fail( r, $"No reference image at {refPath}." );

				const int size = 256;
				var (w, h, rgb, alpha) = Raster.LoadPng( refPath );
				var reference = ReferenceMatch.ReferenceSilhouette( w, h, rgb, alpha, size, out var aspect );
				var match = ReferenceMatch.Compare( studio.Bodies, view, reference, aspect, size );

				r.Log.Add( match.Report );

				if ( args.Count > 2 )
				{
					var outPath = Resolve( baseDir, args[2] );
					Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( outPath ) ) ?? "." );
					match.Diff.SavePng( outPath );
					r.Written.Add( outPath );
				}

				return true;
			}

			case "save":
			{
				if ( args.Count < 1 )
					return Fail( r, "save needs a path." );

				var path = Resolve( baseDir, args[0] );
				Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) ) ?? "." );
				StudioDocument.WriteFile( studio, path );
				r.Written.Add( path );
				r.Log.Add( $"saved {path}" );
				return true;
			}

			case "echo":
				r.Log.Add( string.Join( " ", args ) );
				return true;

			case "material":
			{
				// material 1 materials/alley/brass.vmat — what slot 1 is exported as.
				if ( args.Count < 2 || !int.TryParse( args[0], out var slot ) )
					return Fail( r, "material needs a slot number and a .vmat path: material 1 materials/alley/brass.vmat" );

				studio.MaterialNames[slot] = args[1];
				studio.MarkDirty( 0 );
				r.Log.Add( $"slot {slot} is {args[1]}" );
				return true;
			}

			case "pivot":
			{
				// pivot Bob3 @Beam.bottom — the point this part turns about, which export makes its origin.
				// pivot model @ground — the whole model's origin (Object ▸ Set Origin in the editor).
				// Both are SAVED with the document now (PartStudio.BodyOrigins / Origin), not only kept
				// for this run's export; r.Pivots still carries them so a run's export reads the same.
				if ( args.Count < 2 )
					return Fail( r, "pivot needs a body (or model) and a point: pivot Arm @Hub.centre, pivot model @ground" );

				var pivotBody = ModelMeasure.FindBody( studio, args[0] );
				var whole = pivotBody is null && args[0].Equals( "model", StringComparison.OrdinalIgnoreCase );

				if ( pivotBody is null && !whole )
					return Fail( r, $"No body called '{args[0]}'. Name a body, or say model for the whole model's origin." );

				if ( !Point( studio, args[1], out var p, out var why ) )
					return Fail( r, why );

				if ( whole )
				{
					StudioOrigin.SetModel( studio, OriginTarget.Point, null, p, out var said );
					r.Log.Add( said );
					return true;
				}

				r.Pivots[args[0]] = p;
				studio.BodyOrigins[pivotBody.Id] = p;
				return true;
			}

			case "export":
				return Export( studio, args, baseDir, r );

			case "corrective":
				return Corrective( studio, args, r );

			case "bake":
				return Bake( studio, args, baseDir, r );

			case "paintmaps":
			{
				// paintmaps Torso out/tex [name=torso] [assets=root] [bump=2] — the painted texture set.
				if ( args.Count < 2 )
					return Fail( r, "paintmaps needs a painted body and a folder: paintmaps Torso out/tex [name=x] [assets=root] [bump=2]" );

				var body = ModelMeasure.FindBody( studio, args[0] );

				if ( body?.Mesh is null )
					return Fail( r, $"No body called '{args[0]}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );

				if ( body.Mesh.Paint is null )
					return Fail( r, $"{args[0]} has no paint to write. Add a paint layer first: add Paint bodies={args[0]} fill=1 color=0.4,0.3,0.2" );

				var opts = Pairs( args.Skip( 2 ) );
				var dir = Resolve( baseDir, args[1] );
				var root = opts.TryGetValue( "assets", out var assets ) ? assets : baseDir;
				var bump = opts.TryGetValue( "bump", out var bumpText ) ? Number( bumpText ) : PaintTextureSet.DefaultBumpStrength;
				var name = Safe( opts.GetValueOrDefault( "name", (body.Name ?? body.Id) + "_paint" ) );

				foreach ( var path in PaintTextureSet.Write( body.Mesh.Paint, dir, name,
					p => Path.GetRelativePath( root, p ).Replace( '\\', '/' ), bump ) )
				{
					r.Written.Add( path );
					r.Log.Add( $"wrote {path}" );
				}

				return true;
			}

			default:
				// Rig verbs (bone, chain, rig, bind, skin, twist, mirror bones, subdivide) - BuildScriptRig.cs.
				if ( RigCommand( studio, verb, args, baseDir, r, out var handled ) )
					return handled;

				return Fail( r, $"Unknown command '{verb}'. Commands: add, edit, set, remove, rebuild, fix, render, turnaround, describe, measure, distance, symmetry, topology, section, flex, match, material, pivot, export, corrective, bake, paintmaps, save, echo, {RigVerbs}." );
		}
	}

	const string ExportUsage = "export folder [split=1] [assets=root] [name=x] [lods=0|1|0.5,0.25] [loddistance=a,b] "
		+ "[collision=none|auto|hulls|render] [surface=default] [skin=auto|0|1] [fbx=0|1] [wrinkles=0|1]";

	/// <summary>
	/// `export`: the .vmdl and the mesh files it loads, the same set the editor's Compile writes.
	///
	///   lods=      0 (default) none; 1 = half and a quarter detail (the editor's set); or a list of
	///              triangle ratios, 0.5,0.25. loddistance= the switch distances in units; by default
	///              6, 15, 30… times the model's diagonal. Static levels are triangulated .obj files;
	///              a skinned model gets skinned .dmx/.smd levels on the same skeleton, shape keys kept.
	///              Every level holds UV seams and material borders.
	///   collision= none (default, what export always wrote); auto = the editor's choice (exact shapes
	///              from the history, else a hull per body; on a skinned model a hull per body on the
	///              bone that drives it); hulls = one hull per body; render = physics from the render mesh.
	///   skin=      auto (default): skinned when the studio has bones and the export is not split.
	///              Skinned writes .dmx (what the vmdl loads) and .smd, bound like the editor's export.
	///   wrinkles=1 wrinkle maps (skinned only): each shape key's squash/stretch per vertex in the .dmx,
	///              the squish normal/AO maps as {name}_squish_normal.png/_squish_ao.png, and F_WRINKLE on
	///              each Effigy .vmat the model uses (found under the assets root).
	///   fbx=1      also a binary .fbx of each model, skinned when the model is, for Blender and other DCCs.
	///              The .vmdl still loads the .obj/.dmx, whose import ModelDoc has been measured on.
	/// </summary>
	static bool Export( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		// export models/civic_art/orrery split=1 assets=C:/proj/Assets name=orrery
		if ( args.Count < 1 )
			return Fail( r, $"export needs a folder under the assets root: {ExportUsage}" );

		var folder = args[0].Replace( '\\', '/' ).Trim( '/' );
		var opts = Pairs( args.Skip( 1 ) );

		foreach ( var key in opts.Keys )
			if ( key is not ("split" or "assets" or "name" or "lods" or "loddistance" or "collision" or "surface" or "skin" or "fbx" or "wrinkles") )
				return Fail( r, $"export has no option '{key}'. {ExportUsage}" );

		var root = opts.TryGetValue( "assets", out var assets ) ? assets : baseDir;
		var split = Truthy( opts.GetValueOrDefault( "split", "0" ) );
		var name = opts.GetValueOrDefault( "name", folder.Contains( '/' ) ? folder[(folder.LastIndexOf( '/' ) + 1)..] : folder );
		var collision = opts.GetValueOrDefault( "collision", "none" ).ToLowerInvariant();
		var surface = opts.GetValueOrDefault( "surface", "default" );
		var fbx = Truthy( opts.GetValueOrDefault( "fbx", "0" ) );
		var hasRig = studio.Rig is { Count: > 0 };

		if ( collision is not ("none" or "auto" or "hulls" or "render") )
			return Fail( r, $"collision={collision}: use none, auto, hulls or render." );

		var skinText = opts.GetValueOrDefault( "skin", "auto" ).ToLowerInvariant();
		bool skin;

		if ( skinText == "auto" )
			skin = hasRig && !split;
		else if ( skinText is "1" or "0" or "true" or "false" or "on" or "off" or "yes" or "no" )
			skin = Truthy( skinText );
		else
			return Fail( r, $"skin={skinText}: use auto, 1 or 0." );

		if ( skin && !hasRig )
			return Fail( r, "skin=1, but the studio has no bones to skin to. Build a rig first, or export without skin=." );

		if ( skin && split )
			return Fail( r, "skin=1 with split=1: a skinned model is one mesh on one skeleton. Export it whole, or split it as static parts (skin=0)." );

		var wrinkles = Truthy( opts.GetValueOrDefault( "wrinkles", "0" ) );

		if ( wrinkles && !skin )
			return Fail( r, "wrinkles=1 needs a skinned export: shape keys, and the wrinkle data that rides on them, only go out in the skinned .dmx. Build a rig (or pass skin=1), and do not split." );

		if ( !TryLods( opts, out var ratios, out var lodDistances, out var whyLods ) )
			return Fail( r, whyLods );

		var bodies = studio.Bodies.Where( b => b?.Mesh is not null && b.Mesh.Faces.Count > 0 && b.Visible && !b.IsReference ).ToList();

		if ( bodies.Count == 0 )
			return Fail( r, "Nothing to export: no visible bodies." );

		var dir = Path.Combine( root, folder );
		Directory.CreateDirectory( dir );

		var layout = new StringBuilder();
		layout.AppendLine( "# part\tmodel\tpivot x\ty\tz\tsize x\ty\tz" );

		Vec3 DefaultPivot( Vec3 min, Vec3 max ) => new( (min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, min.z );

		// The model's saved origin (Object ▸ Set Origin, or `pivot model`) when it has been moved off
		// the world centre; otherwise the bottom centre, which is what export always did. A model
		// whose origin was put back at the world centre therefore still exports bottom-centred here.
		Vec3 ModelPivot( PartStudio s, Vec3 min, Vec3 max ) => StudioOrigin.HasModelOrigin( s ) ? s.Origin : DefaultPivot( min, max );

		if ( split )
		{
			foreach ( var body in bodies )
			{
				var partName = Safe( body.Name ?? body.Id );
				var mesh = body.Mesh.Clone();
				var (min, max) = ModelMeasure.Bounds( mesh );
				var pivot = r.Pivots.TryGetValue( body.Name ?? "", out var p ) ? p
					: StudioOrigin.TryBody( studio, body, out var saved ) ? saved
					: DefaultPivot( min, max );

				MeshTransform.Apply( mesh, Xform.Translate( -pivot ) );
				var physics = StaticPhysics( studio, collision, surface, pivot, b => b.Id == body.Id, r, partName );
				WriteStatic( studio, mesh, dir, folder, partName, physics, ratios, lodDistances, fbx, r );
				layout.AppendLine( $"{body.Name ?? body.Id}\t{folder}/{partName}.vmdl\t{N( pivot.x )}\t{N( pivot.y )}\t{N( pivot.z )}\t{N( max.x - min.x )}\t{N( max.y - min.y )}\t{N( max.z - min.z )}" );
			}
		}
		else if ( skin )
		{
			// The editor's bind, in the editor's order: weights from the unpivoted mesh and bones,
			// then both shifted together, so the weights still say what they said.
			var (mesh, ranges) = studio.ToMeshWithBodies( b => bodies.Contains( b ) );
			var skeleton = studio.Rig;
			var weights = SkinBinder.BindBodies( mesh, ranges, studio.BodyBoneMap, skeleton );
			weights = SkinBinder.SmoothWeights( mesh, weights );

			if ( studio.WeightPaint is { Count: > 0 } paint )
			{
				paint.Apply( mesh, weights, skeleton, out var missing );
				if ( paint.IsStale ) r.Log.Add( $"export: painted weights not used - the mesh changed since they were painted ({paint.Count} vertices)" );
				else if ( missing.Count > 0 ) r.Log.Add( $"export: painted weights for missing bone(s) {string.Join( ", ", missing.Take( 4 ) )} were not used" );
			}

			mesh.Skin = weights;

			// The writers keep 4 bones a vertex (s&box's GPU skinning reads no more); say when that costs something.
			if ( weights.CapWarning( DmxWriter.MaxInfluences ) is { } capped )
				r.Log.Add( "export: " + capped );

			// Corrective shapes from the weights just bound, before the pivot moves mesh and bones together.
			foreach ( var line in CorrectiveMorph.BuildAll( mesh, skeleton, studio.Correctives, DmxWriter.MaxInfluences ) )
				r.Log.Add( "export: " + line );

			// Morphs only play on materials that turn them on. Effigy's own flesh vmats on these bodies get the switch.
			if ( mesh.HasMorphs )
				foreach ( var vmatPath in studio.Features.OfType<FleshMaterialFeature>().Where( f => !f.Suppressed )
					.SelectMany( f => f.Written ).Where( w => w.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) && File.Exists( w ) ) )
				{
					var text = File.ReadAllText( vmatPath );
					if ( !VmatMorph.IsOn( text ) && VmatMorph.Enable( text ) is { } on )
					{
						File.WriteAllText( vmatPath, on );
						r.Log.Add( $"export: {Path.GetFileName( vmatPath )}: F_MORPH_SUPPORTED on, for the morphs" );
					}
				}

			var (min, max) = ModelMeasure.Bounds( mesh );
			var pivot = r.Pivots.TryGetValue( name, out var p ) ? p : ModelPivot( studio, min, max );
			var shapes = collision is "auto" or "hulls" ? BoneHulls( studio, skeleton ) : null;

			MeshTransform.Apply( mesh, Xform.Translate( -pivot ) );
			skeleton = Pivoted( skeleton, pivot );

			if ( shapes is not null )
				foreach ( var shape in shapes ) Shift( shape, -pivot );

			var physics = collision switch
			{
				"render" => VmdlPhysics.MeshFromRender( surface ),
				"none" => "",
				_ => VmdlPhysics.ShapeList( shapes, surface ),
			};

			if ( collision is "auto" or "hulls" && physics.Length == 0 )
			{
				physics = VmdlPhysics.MeshFromRender( surface );
				r.Log.Add( "export: no body made a hull, so collision is the render mesh (static: it will not follow the bones)" );
			}

			var safe = Safe( name );
			var dmxPath = Path.Combine( dir, safe + ".dmx" );
			var smdPath = Path.Combine( dir, safe + ".smd" );
			Dictionary<string, float[]> wrinkleValues = null;

			if ( wrinkles )
			{
				var made = WrinkleMaps.WriteForExport( mesh, dir, folder, safe, slot =>
				{
					var material = studio.NameForSlot( slot );
					return material.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) ? Path.Combine( root, material ) : null;
				} );

				wrinkleValues = made.Values.Count > 0 ? made.Values : null;
				r.Written.AddRange( made.Written );
				r.Log.Add( $"export: wrinkle maps for {made.Values.Count} shape key(s), on {made.MaterialsOn.Count} material(s)" );
				r.Log.AddRange( made.Notes.Select( n => $"export: wrinkle maps: {n}" ) );

				if ( made.Values.Count == 0 )
					return Fail( r, $"wrinkles=1: {made.Notes.FirstOrDefault()}" );
			}

			DmxWriter.WriteFile( mesh, dmxPath, skeleton, materialName: studio.NameForSlot, modelName: safe, wrinkles: wrinkleValues );
			SmdWriter.WriteFile( mesh, smdPath, skeleton, materialName: studio.NameForSlot );

			// Driven shape keys: their drivers into the model's .ctrlrig, for Marionette and the player.
			var drivers = MorphDriver.FromMesh( mesh );

			if ( MorphDriver.WriteRigFile( Path.Combine( dir, safe + ".ctrlrig" ), drivers, $"{folder}/{safe}.vmdl" ) )
				r.Log.Add( $"export: {drivers.Count} morph driver(s) written to {safe}.ctrlrig" );
			var vmdlPath = Path.Combine( dir, safe + ".vmdl" );
			var skinMaterials = VmdlMaterials.GroupList( studio, mesh );
			r.Written.AddRange( new[] { dmxPath, smdPath } );

			if ( ratios.Count > 0 )
			{
				// Skinned levels: each decimated with its weights and shape keys carried, on the
				// same pivoted skeleton as the full mesh.
				var levels = LodExport.WriteSkinned( mesh, skeleton, dir, folder, safe, studio.NameForSlot, ratios, lodDistances, wrinkles: wrinkles );
				var (files, switchAt) = LodExport.VmdlLists( $"{folder}/{safe}.dmx", levels );
				File.WriteAllText( vmdlPath, VmdlDocument.SkinnedWithLods( files, switchAt, skeleton, physics, skinMaterials ) );
				levels.ForEach( l => r.Written.AddRange( l.Files ) );
				r.Log.Add( $"export: {safe} skinned LODs: {Decimate.TriangleCount( mesh )} tris, then {LodExport.Describe( levels )}" );

				if ( Decimate.TriangleCount( mesh ) < LodExport.MinimumTriangles )
					r.Log.Add( $"export: note: {safe} is only {Decimate.TriangleCount( mesh )} triangles, so its LODs save next to nothing" );
			}
			else
				File.WriteAllText( vmdlPath, VmdlDocument.Skinned( $"{folder}/{safe}.dmx", skeleton, physics, skinMaterials ) );

			r.Written.Add( vmdlPath );

			// Shape keys went into the DMX (DmxMorphs); their materials need F_MORPH_SUPPORTED or the
			// GPU never applies them - the editor's Compile does the same (EnableMorphMaterials).
			if ( mesh.HasMorphs )
			{
				r.Log.Add( $"export: {mesh.ValidMorphs.Count()} shape key(s) written to the .dmx as flex controls" );

				foreach ( var line in VmatMorph.EnableFiles( mesh, studio.NameForSlot, n => n.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) ? Path.Combine( root, n ) : null ) )
					r.Log.Add( $"export: {line}" );

				if ( fbx )
					r.Log.Add( "export: note: the .fbx carries no shape keys; the .dmx the .vmdl loads does" );
			}

			if ( fbx )
			{
				var fbxPath = Path.Combine( dir, safe + ".fbx" );
				FbxWriter.WriteBinaryFile( mesh, fbxPath, skeleton, studio.NameForSlot, safe );
				r.Written.Add( fbxPath );
			}

			r.Log.Add( $"export: {safe} skinned to {skeleton.Count} bone(s), {mesh.VertexCount} vertices; collision {Said( collision, physics )}{(fbx ? "; .fbx too" : "")}" );
			layout.AppendLine( $"{name}\t{folder}/{safe}.vmdl\t{N( pivot.x )}\t{N( pivot.y )}\t{N( pivot.z )}\t{N( max.x - min.x )}\t{N( max.y - min.y )}\t{N( max.z - min.z )}" );
		}
		else
		{
			var mesh = new PolyMesh();
			foreach ( var body in bodies )
				MeshTransform.Append( mesh, body.Mesh );

			var (min, max) = ModelMeasure.Bounds( mesh );
			var pivot = r.Pivots.TryGetValue( name, out var p ) ? p : ModelPivot( studio, min, max );
			MeshTransform.Apply( mesh, Xform.Translate( -pivot ) );
			var physics = StaticPhysics( studio, collision, surface, pivot, b => bodies.Contains( b ), r, Safe( name ) );
			WriteStatic( studio, mesh, dir, folder, Safe( name ), physics, ratios, lodDistances, fbx, r );
			layout.AppendLine( $"{name}\t{folder}/{Safe( name )}.vmdl\t{N( pivot.x )}\t{N( pivot.y )}\t{N( pivot.z )}\t{N( max.x - min.x )}\t{N( max.y - min.y )}\t{N( max.z - min.z )}" );
		}

		var layoutPath = Path.Combine( dir, "layout.txt" );
		File.WriteAllText( layoutPath, layout.ToString() );
		r.Written.Add( layoutPath );
		r.Log.Add( $"exported {(split ? bodies.Count : 1)} model(s) to {dir}; layout in layout.txt" );
		return true;
	}

	/// <summary>`lods=1` is the editor's pair; `lods=0.5,0.25` names the ratios; `loddistance=` the
	/// switch distances (default multiples of the model's diagonal, filled in by WriteStatic).</summary>
	static bool TryLods( Dictionary<string, string> opts, out List<float> ratios, out List<float> distances, out string why )
	{
		ratios = new List<float>();
		distances = null;
		why = null;
		var text = opts.GetValueOrDefault( "lods", "0" ).Trim();

		if ( text is "0" or "" or "off" or "no" or "false" )
		{
			if ( opts.ContainsKey( "loddistance" ) ) { why = "loddistance= without lods=: say which levels to make, e.g. lods=0.5,0.25."; return false; }
			return true;
		}

		if ( text is "1" or "on" or "yes" or "true" )
			ratios.AddRange( new[] { 0.5f, 0.25f } );
		else
		{
			foreach ( var part in text.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
			{
				if ( !TryNumber( part, out var ratio ) || ratio <= 0f || ratio >= 1f )
				{
					why = $"lods={text}: each level is a share of the triangles between 0 and 1, e.g. lods=0.5,0.25 (or lods=1 for that pair).";
					return false;
				}

				if ( ratios.Count > 0 && ratio >= ratios[^1] )
				{
					why = $"lods={text}: each level must be coarser than the one before it.";
					return false;
				}

				ratios.Add( ratio );
			}
		}

		if ( opts.TryGetValue( "loddistance", out var distText ) )
		{
			distances = new List<float>();

			foreach ( var part in distText.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
			{
				if ( !TryNumber( part, out var d ) || d <= 0f || (distances.Count > 0 && d <= distances[^1]) )
				{
					why = $"loddistance={distText}: rising distances in units, one per level.";
					return false;
				}

				distances.Add( d );
			}

			if ( distances.Count != ratios.Count )
			{
				why = $"loddistance= has {distances.Count} distance(s) for {ratios.Count} level(s): give one per level.";
				return false;
			}
		}

		return true;
	}

	/// <summary>One static model: its mesh, any LOD meshes, the .vmdl that loads them, and an .fbx
	/// when asked. Materials bound from the studio's slots.</summary>
	static void WriteStatic( PartStudio studio, PolyMesh mesh, string dir, string folder, string name, string physics,
		List<float> ratios, List<float> distances, bool fbx, Result r )
	{
		// Triangulated the bake's way (NormalBake.FaceTriangles), as the editor's static compile is.
		var objPath = Path.Combine( dir, name + ".obj" );

		// OBJ has no shape keys. Say so rather than drop them without a word, as the editor does.
		if ( mesh.HasMorphs )
			r.Log.Add( $"export: warning: {mesh.ValidMorphs.Count()} shape key(s) on {name} were left out: a static model exports as OBJ, which has no morphs. "
				+ "Export it skinned (add a bone, and no skin=0 or split=1) to keep them in the .dmx." );

		ObjWriter.WriteFile( mesh, objPath, name, materialName: studio.NameForSlot, triangulate: true );
		r.Written.Add( objPath );

		var materials = VmdlMaterials.GroupList( studio, mesh );
		var vmdlPath = Path.Combine( dir, name + ".vmdl" );

		if ( ratios.Count > 0 )
		{
			var full = Decimate.TriangleCount( mesh );
			var levels = LodExport.WriteStatic( mesh, dir, folder, name, studio.NameForSlot, ratios, distances );
			var (files, switchAt) = LodExport.VmdlLists( $"{folder}/{name}.obj", levels );
			levels.ForEach( l => r.Written.AddRange( l.Files ) );

			File.WriteAllText( vmdlPath, VmdlDocument.StaticWithLods( files, switchAt, physics, materials ) );
			r.Log.Add( $"export: {name} LODs: {full} tris, then {LodExport.Describe( levels )}" );

			if ( full < LodExport.MinimumTriangles )
				r.Log.Add( $"export: note: {name} is only {full} triangles, so its LODs save next to nothing" );
		}
		else
			File.WriteAllText( vmdlPath, VmdlDocument.Static( $"{folder}/{name}.obj", physics, materials ) );

		r.Written.Add( vmdlPath );

		if ( fbx )
		{
			var fbxPath = Path.Combine( dir, name + ".fbx" );
			FbxWriter.WriteBinaryFile( mesh, fbxPath, materialName: studio.NameForSlot, modelName: name );
			r.Written.Add( fbxPath );
		}
	}

	/// <summary>The PhysicsShapeList for a static model, already shifted onto its pivot.</summary>
	static string StaticPhysics( PartStudio studio, string collision, string surface, Vec3 pivot, Func<Body, bool> include, Result r, string name )
	{
		if ( collision == "none" )
			return "";

		if ( collision == "render" )
		{
			r.Log.Add( $"export: {name} collision from the render mesh" );
			return VmdlPhysics.MeshFromRender( surface );
		}

		List<CollisionShape> shapes;
		string how;

		if ( collision == "auto" )
		{
			var report = CollisionBuilder.Build( studio );
			shapes = report.Shapes.Where( s => s.BodyId is null || include( studio.Bodies.FirstOrDefault( b => b.Id == s.BodyId ) ?? new Body( s.BodyId, null, null ) ) ).ToList();
			how = report.ToString();
		}
		else
		{
			shapes = CollisionBuilder.HullsPerBody( studio ).Where( s => s.BodyId is null || include( studio.Bodies.First( b => b.Id == s.BodyId ) ) ).ToList();
			how = $"{shapes.Count} hull(s), one per body";
		}

		foreach ( var shape in shapes )
			Shift( shape, -pivot );

		var node = VmdlPhysics.ShapeList( shapes, surface );

		if ( node.Length == 0 )
		{
			r.Log.Add( $"export: {name}: no collision shape could be built ({how}), so collision is the render mesh" );
			return VmdlPhysics.MeshFromRender( surface );
		}

		r.Log.Add( $"export: {name} collision {how}" );
		return node;
	}

	/// <summary>A skinned model's collision: one hull per body on the bone that drives it - the
	/// assigned bone, else the nearest, as the editor's export does.</summary>
	static List<CollisionShape> BoneHulls( PartStudio studio, Skeleton skeleton )
	{
		var shapes = CollisionBuilder.HullsPerBody( studio );

		foreach ( var shape in shapes )
		{
			if ( shape.BodyId is not null && studio.BodyBoneMap.TryGetValue( shape.BodyId, out var named ) && skeleton.IndexOf( named ) >= 0 )
			{
				shape.Bone = named;
				continue;
			}

			var centre = shape.Position;

			if ( shape.Points is { Count: > 0 } points )
			{
				centre = Vec3.Zero;
				foreach ( var q in points ) centre += q;
				centre /= points.Count;
			}

			shape.Bone = skeleton.Bones[SkinBinder.NearestBone( centre, skeleton )].Name;
		}

		return shapes;
	}

	static void Shift( CollisionShape shape, Vec3 offset )
	{
		shape.Position += offset;

		if ( shape.Points is null )
			return;

		for ( var i = 0; i < shape.Points.Count; i++ )
			shape.Points[i] += offset;
	}

	/// <summary>The skeleton moved with the mesh onto the pivot: root bones only, children follow.</summary>
	static Skeleton Pivoted( Skeleton skeleton, Vec3 pivot )
	{
		var copy = skeleton.Clone();
		var shift = Xform.Translate( -pivot );

		foreach ( var bone in copy.Bones )
			if ( bone.Parent < 0 )
				bone.Local = shift * bone.Local;

		return copy;
	}

	static string Said( string collision, string physics ) =>
		collision == "none" ? "none" : physics.Contains( "PhysicsMeshFromRender" ) ? "from the render mesh"
			: $"{System.Text.RegularExpressions.Regex.Matches( physics, "PhysicsShape(Box|Sphere|Cylinder|Hull)\"" ).Count} hull(s) on their bones";

	/// <summary>`maps=normal,ao,curvature,cavity,height,thickness` or `maps=all`.</summary>
	static bool BakeMapList( string text, out BakeMaps maps, out string why )
	{
		maps = BakeMaps.None;
		why = null;

		foreach ( var part in text.Split( new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			var map = part.Trim().ToLowerInvariant() switch
			{
				"all" => BakeMaps.All,
				"normal" or "normals" => BakeMaps.Normal,
				"ao" or "occlusion" or "ambientocclusion" => BakeMaps.AmbientOcclusion,
				"curvature" or "curv" => BakeMaps.Curvature,
				"cavity" => BakeMaps.Cavity,
				"height" => BakeMaps.Height,
				"thickness" => BakeMaps.Thickness,
				"bentnormal" or "bent" => BakeMaps.BentNormal,
				"objectnormal" or "worldnormal" or "object" => BakeMaps.ObjectNormal,
				"position" or "pos" => BakeMaps.Position,
				"id" or "materialid" or "matid" => BakeMaps.MaterialId,
				"everything" => BakeMaps.Everything,
				_ => BakeMaps.None,
			};

			if ( map == BakeMaps.None )
			{
				why = $"maps: '{part.Trim()}' is not a bake. Maps: normal, ao, curvature, cavity, height, thickness, bentnormal, objectnormal, position, id, all (the first six), everything.";
				return false;
			}

			maps |= map;
		}

		if ( maps == BakeMaps.None )
		{
			why = "maps: name at least one of normal, ao, curvature, cavity, height, thickness.";
			return false;
		}

		return true;
	}

	static string Safe( string name )
	{
		var sb = new StringBuilder();
		foreach ( var c in (name ?? "part").Trim().ToLowerInvariant() )
			sb.Append( char.IsLetterOrDigit( c ) ? c : '_' );
		return sb.Length == 0 ? "part" : sb.ToString();
	}

	static string N( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );

	static bool RebuildAndCheck( PartStudio studio, Feature feature, Result r, string did )
	{
		studio.Rebuild();

		if ( feature.Error is not null )
		{
			var d = feature.Diagnostic;
			var sb = new StringBuilder();
			sb.Append( $"{feature.TypeName} '{feature.Name ?? feature.Id}' failed: {feature.Error}" );

			if ( d is not null )
			{
				if ( !string.IsNullOrEmpty( d.Cause ) ) sb.Append( $" Because: {d.Cause}" );
				foreach ( var remedy in d.Remedies ) sb.Append( $" Try: {remedy}." );
			}

			return Fail( r, sb.ToString() );
		}

		r.Log.Add( did + (feature.Warning is not null ? $" (note: {feature.Warning})" : "" ) );
		return true;
	}

	static bool Fail( Result r, string message )
	{
		r.Error = message;
		return false;
	}

	// --- assigning parameters ---------------------------------------------------------------------

	static bool Assign( PartStudio studio, Feature feature, Dictionary<string, string> pairs, Result r )
	{
		foreach ( var (key, value) in pairs )
		{
			if ( key == "name" )
			{
				feature.Name = value;
				continue;
			}

			if ( feature is IScriptValueReader reader && reader.HandlesScriptValue( key, value, out var readError ) )
			{
				if ( readError is not null )
					return Fail( r, $"{key}: {readError}" );

				continue;
			}

			var target = FindParam( feature, key, out var field );

			if ( target is null && field is null )
				return Fail( r, $"{feature.TypeName} has no parameter '{key}'. It has: {string.Join( ", ", ParamNames( feature ) )}." );

			try
			{
				if ( target is not null && !AssignParam( studio, target, value, out var why ) )
					return Fail( r, $"{key}: {why}" );

				if ( target is null && !AssignField( studio, feature, field, value, out var whyField ) )
					return Fail( r, $"{key}: {whyField}" );
			}
			catch ( Exception e )
			{
				return Fail( r, $"{key}: {e.Message}" );
			}
		}

		return true;
	}

	static bool AssignParam( PartStudio studio, IParam param, string value, out string why )
	{
		why = null;

		switch ( param )
		{
			case FloatParam f:
				if ( !TryNumber( value, out var fv ) ) { why = $"'{value}' is not a number."; return false; }
				f.Value = fv;
				f.Expr = null;
				return true;

			case IntParam i:
				if ( !TryNumber( value, out var iv ) ) { why = $"'{value}' is not a number."; return false; }
				i.Value = (int)MathF.Round( iv );
				return true;

			case BoolParam b:
				b.Value = Truthy( value );
				return true;

			case ChoiceParam c:
			{
				var index = Array.FindIndex( c.Options, o => string.Equals( o, value, StringComparison.OrdinalIgnoreCase )
					|| string.Equals( o.Replace( ' ', '_' ), value, StringComparison.OrdinalIgnoreCase ) );

				// "Left" for "Left (y)", "box" for "Box": the start of an option, then anywhere in it.
				if ( index < 0 )
					index = Array.FindIndex( c.Options, o => o.StartsWith( value, StringComparison.OrdinalIgnoreCase ) );

				if ( index < 0 )
					index = Array.FindIndex( c.Options, o => o.Contains( value, StringComparison.OrdinalIgnoreCase ) );

				if ( index < 0 && int.TryParse( value, out var n ) && n >= 0 && n < c.Options.Length )
					index = n;

				if ( index < 0 ) { why = $"'{value}' is not one of {string.Join( ", ", c.Options )}."; return false; }
				c.Index = index;
				return true;
			}

			case Vec3Param v:
				if ( !Point( studio, value, out var p, out why ) ) return false;
				v.Value = p;
				return true;

			case StringParam s:
				// A selection rule names places by landmark; those resolve now, against the model as
				// built so far, like every other point in a script, so the feature stores numbers.
				if ( s.Label == "Select" && value.Contains( '@' ) )
				{
					if ( !ResolveLandmarks( studio, value, out var resolved, out why ) ) return false;
					value = resolved;
				}

				s.Value = value;
				return true;

			case BodySelectionParam bodies:
			{
				bodies.BodyIds.Clear();

				foreach ( var name in value.Split( new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries ) )
				{
					var body = ModelMeasure.FindBody( studio, name.Trim() );
					if ( body is null ) { why = $"No body called '{name.Trim()}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}."; return false; }
					bodies.BodyIds.Add( body.Id );
				}

				return true;
			}
		}

		why = $"a {param.GetType().Name} cannot be set from a script.";
		return false;
	}

	static bool AssignField( PartStudio studio, Feature feature, FieldInfo field, string value, out string why )
	{
		why = null;
		var current = field.GetValue( feature );

		switch ( current )
		{
			case List<Vec3> points:
			{
				points.Clear();

				foreach ( var part in value.Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
				{
					if ( !Point( studio, part.Trim(), out var p, out why ) ) return false;
					points.Add( p );
				}

				return true;
			}

			case List<Vec2> pairs:
			{
				pairs.Clear();

				foreach ( var part in value.Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
				{
					var xy = part.Split( ',' );
					if ( xy.Length != 2 || !TryNumber( xy[0], out var x ) || !TryNumber( xy[1], out var y ) ) { why = $"'{part}' is not a pair a,b."; return false; }
					pairs.Add( new Vec2( x, y ) );
				}

				return true;
			}

			case string:
			{
				// A field holding another feature's id — a path sketch, a spline — takes its name.
				var other = FindFeature( studio, value );
				field.SetValue( feature, other?.Id ?? value );
				return true;
			}

			case List<string> texts:
			{
				texts.Clear();
				foreach ( var part in value.Split( new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries ) )
					texts.Add( FindFeature( studio, part.Trim() )?.Id ?? part.Trim() );
				return true;
			}
		}

		why = $"a {field.FieldType.Name} cannot be set from a script.";
		return false;
	}

	static IParam FindParam( Feature feature, string key, out FieldInfo field )
	{
		field = null;
		var wanted = Normalise( key );

		foreach ( var param in feature.Parameters.Concat( feature.AdvancedParameters ) )
			if ( Normalise( param.Label ) == wanted )
				return param;

		foreach ( var f in feature.GetType().GetFields( BindingFlags.Public | BindingFlags.Instance ) )
		{
			if ( Normalise( f.Name ) != wanted )
				continue;

			if ( f.GetValue( feature ) is IParam p )
				return p;

			field = f;
			return null;
		}

		return null;
	}

	static IEnumerable<string> ParamNames( Feature feature )
	{
		foreach ( var p in feature.Parameters.Concat( feature.AdvancedParameters ).Distinct() )
			yield return p.Label;

		foreach ( var f in feature.GetType().GetFields( BindingFlags.Public | BindingFlags.Instance ) )
			if ( f.GetValue( feature ) is not IParam && f.Name is not ("Id" or "Name" or "Suppressed" or "Visible") )
				yield return f.Name;
	}

	static string Normalise( string s ) => (s ?? "").ToLowerInvariant().Replace( " ", "" ).Replace( "_", "" ).Replace( "'", "" );

	// --- points and landmarks ---------------------------------------------------------------------

	/// <summary>`x,y,z`, `@landmark`, or `@landmark+dx,dy,dz` / `@landmark-dx,dy,dz`.</summary>
	public static bool Point( PartStudio studio, string text, out Vec3 point, out string why )
	{
		why = null;
		point = Vec3.Zero;
		text = (text ?? "").Trim();

		if ( !text.StartsWith( '@' ) )
		{
			if ( ModelMeasure.TryParseVec3( text, out point ) )
				return true;

			// A bare name is a landmark too.
			return ModelMeasure.TryLandmark( studio, text, out point, out why );
		}

		var body = text[1..];
		var offset = Vec3.Zero;
		var plus = body.IndexOfAny( new[] { '+', '-' }, 1 );

		if ( plus > 0 )
		{
			var sign = body[plus] == '-' ? -1f : 1f;
			var offsetText = body[(plus + 1)..];
			body = body[..plus];

			if ( !ModelMeasure.TryParseVec3( offsetText, out offset ) )
			{
				why = $"'{offsetText}' after the landmark is not an offset dx,dy,dz.";
				return false;
			}

			offset *= sign;
		}

		if ( !ModelMeasure.TryLandmark( studio, body, out point, out why ) )
			return false;

		point += offset;
		return true;
	}

	/// <summary>Every `@landmark[+dx,dy,dz]` in a selection rule, as `x,y,z`. A landmark runs to the
	/// next `~`, `&amp;`, `|` or the end.</summary>
	static bool ResolveLandmarks( PartStudio studio, string text, out string resolved, out string why )
	{
		why = null;
		var sb = new StringBuilder();
		var i = 0;

		while ( i < text.Length )
		{
			if ( text[i] != '@' )
			{
				sb.Append( text[i++] );
				continue;
			}

			var end = text.IndexOfAny( new[] { '~', '&', '|' }, i );
			if ( end < 0 ) end = text.Length;

			if ( !Point( studio, text[i..end], out var p, out why ) )
			{
				resolved = text;
				return false;
			}

			sb.Append( $"{N( p.x )},{N( p.y )},{N( p.z )}" );
			i = end;
		}

		resolved = sb.ToString();
		return true;
	}

	// --- features by type and name ----------------------------------------------------------------

	static Feature Create( string typeName )
	{
		var wanted = Normalise( typeName );

		foreach ( var type in FeatureTypes() )
		{
			var name = Normalise( type.Name );
			var bare = name.EndsWith( "feature" ) ? name[..^7] : name;

			if ( name == wanted || bare == wanted )
				return (Feature)Activator.CreateInstance( type );
		}

		return null;
	}

	/// <summary>A new feature by the name a script uses ("Subdivide", "UVProject", "UV project"), or null.</summary>
	public static Feature CreateFeature( string typeName ) => string.IsNullOrWhiteSpace( typeName ) ? null : Create( typeName );

	/// <summary>Set a feature's parameters from script-style key=value pairs, as `add` does.</summary>
	public static bool Configure( PartStudio studio, Feature feature, IEnumerable<KeyValuePair<string, string>> pairs, out string why )
	{
		var r = new Result();
		var ok = Assign( studio, feature, pairs.ToDictionary( p => p.Key, p => p.Value ), r );
		why = r.Error;
		return ok;
	}

	static IEnumerable<Type> FeatureTypes() => typeof( Feature ).Assembly.GetTypes()
		.Where( t => t.IsSubclassOf( typeof( Feature ) ) && !t.IsAbstract )
		.OrderBy( t => t.Name, StringComparer.Ordinal );

	/// <summary>A feature type's parameters, one per line, with what each takes — the help a script
	/// author reads before writing `add`.</summary>
	public static string Describe( string typeName )
	{
		var feature = Create( typeName );

		if ( feature is null )
			return $"No feature type '{typeName}'. Types: {string.Join( ", ", FeatureTypeNames() )}.";

		var sb = new StringBuilder();
		sb.AppendLine( $"{feature.TypeName} (add {typeName})" );

		foreach ( var param in feature.Parameters.Concat( feature.AdvancedParameters ).Distinct() )
		{
			var key = Normalise( param.Label );
			var kind = param switch
			{
				FloatParam f => $"number, default {f.Value}" + (f.Min > float.MinValue ? $", min {f.Min}" : "") + (f.Max < float.MaxValue ? $", max {f.Max}" : ""),
				IntParam i => $"whole number, default {i.Value}, {i.Min}..{i.Max}",
				BoolParam b => $"on/off, default {(b.Value ? "on" : "off")}",
				ChoiceParam c => $"one of {string.Join( "|", c.Options )}, default {c.Value}",
				Vec3Param v => $"point x,y,z or @landmark, default {v.Value.x},{v.Value.y},{v.Value.z}",
				StringParam => "text",
				BodySelectionParam => "body names, comma-separated (empty = every body)",
				_ => param.GetType().Name,
			};
			sb.AppendLine( $"  {key} = {kind}" );
		}

		foreach ( var f in feature.GetType().GetFields( BindingFlags.Public | BindingFlags.Instance ) )
		{
			if ( f.GetValue( feature ) is IParam || f.Name is "Id" or "Name" or "Suppressed" or "Visible" )
				continue;

			var kind = f.FieldType == typeof( List<Vec3> ) ? "points x,y,z;x,y,z;… (landmarks allowed)"
				: f.FieldType == typeof( List<Vec2> ) ? "pairs a,b;a,b;…"
				: f.FieldType == typeof( string ) ? "a feature name"
				: f.FieldType == typeof( List<string> ) ? "feature names, comma-separated"
				: null;

			if ( kind is not null )
				sb.AppendLine( $"  {Normalise( f.Name )} = {kind}" );
		}

		sb.AppendLine( "  name = what to call it" );
		return sb.ToString().TrimEnd();
	}

	public static IEnumerable<string> FeatureTypeNames() =>
		FeatureTypes().Select( t => t.Name.EndsWith( "Feature" ) ? t.Name[..^7] : t.Name );

	static Feature FindFeature( PartStudio studio, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return null;

		if ( name.StartsWith( '#' ) && int.TryParse( name[1..], out var index ) && index >= 1 && index <= studio.Features.Count )
			return studio.Features[index - 1];

		return studio.Features.FirstOrDefault( f => f.Id == name )
			?? studio.Features.LastOrDefault( f => string.Equals( f.Name, name, StringComparison.OrdinalIgnoreCase ) )
			?? studio.Features.LastOrDefault( f => string.Equals( f.Name?.Replace( ' ', '_' ), name, StringComparison.OrdinalIgnoreCase ) )
			?? studio.Features.LastOrDefault( f => string.Equals( f.TypeName, name, StringComparison.OrdinalIgnoreCase ) );
	}

	// --- lexing -------------------------------------------------------------------------------------

	static List<string> Tokenise( string line )
	{
		var tokens = new List<string>();
		var sb = new StringBuilder();
		var quoted = false;

		foreach ( var c in line )
		{
			if ( c == '"' ) { quoted = !quoted; continue; }

			if ( char.IsWhiteSpace( c ) && !quoted )
			{
				if ( sb.Length > 0 ) { tokens.Add( sb.ToString() ); sb.Clear(); }
				continue;
			}

			sb.Append( c );
		}

		if ( sb.Length > 0 )
			tokens.Add( sb.ToString() );

		return tokens;
	}

	/// <summary>
	/// `render out.png shade=material|clay [views=4] [size=512] [elevation=12] [preset=Wet gore] [focus=Body] [label=OURS]`:
	/// a lit turnaround through ShadedRender, for judging how a thing LOOKS (wet, glossy, translucent)
	/// rather than measuring it. shade=material shows each body as its Flesh Material feature baked it
	/// (maps or per-vertex); a body with none shows its paint layers if it has any, else takes
	/// `preset=` evaluated per vertex, else its vertex colours, else clay.
	/// </summary>
	static bool RenderShaded( PartStudio studio, string path, Dictionary<string, string> opts, string shade, Result r )
	{
		var mode = shade.Trim().ToLowerInvariant();
		if ( mode is not ("clay" or "material" or "flesh" or "wet") )
			return Fail( r, $"shade={shade}: use shade=material (what the materials make it look like) or shade=clay (form only)." );

		var views = (int)Number( opts.GetValueOrDefault( "views", "4" ) );
		var size = (int)Number( opts.GetValueOrDefault( "size", "512" ) );
		var elevation = Number( opts.GetValueOrDefault( "elevation", "12" ) );
		var focus = opts.TryGetValue( "focus", out var f ) ? ModelMeasure.FindBody( studio, f ) : null;
		FleshStack fallback = null;
		if ( opts.TryGetValue( "preset", out var presetName ) && (fallback = FleshMaterial.Preset( presetName )) is null )
			return Fail( r, $"No material preset '{presetName}'. Presets: {string.Join( ", ", FleshMaterial.Presets )}." );

		// The scene Render Turnaround draws, so the script's sheet and the editor's match.
		var scene = Turnaround.Scene( studio, mode == "clay", focus?.Id, fallback, r.Log );

		if ( scene.Items.Count == 0 )
			return Fail( r, focus is null ? "Nothing to render: no visible body has faces." : $"{focus.Name ?? focus.Id} has no faces to render." );

		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) ) ?? "." );
		scene.Coverage = (int)Number( opts.GetValueOrDefault( "aa", "16" ) );
		ShadedRender.Sheet( scene, views, size, elevation, (int)Number( opts.GetValueOrDefault( "ss", "1" ) ), opts.GetValueOrDefault( "label", "OURS" ) ).SavePng( path );
		r.Written.Add( path );
		r.Log.Add( $"rendered shade={mode}, {Math.Clamp( views, 1, 12 )} views, to {path}" );
		return true;
	}

	/// <summary>
	///   corrective bone=elbow_L angle=90 [axis=x,y,z]   add (or replace) a corrective shape on a bone
	///   corrective remove bone=elbow_L [angle=90]        take it (or every one on that bone) off
	///   corrective                                       list them
	/// Built into the model at export (CorrectiveMorph) from the export's own weights.
	/// </summary>
	static bool Corrective( PartStudio studio, List<string> args, Result r )
	{
		var remove = args.Count > 0 && args[0].Equals( "remove", StringComparison.OrdinalIgnoreCase );
		var opts = Pairs( remove ? args.Skip( 1 ) : args );

		if ( opts.Count == 0 )
		{
			r.Log.Add( studio.Correctives.Count == 0 ? "no corrective shapes"
				: string.Join( "\n", studio.Correctives.Select( c => $"corrective {c.MorphName}: bone {c.Bone}, {c.Angle:0}°" ) ) );
			return true;
		}

		if ( !opts.TryGetValue( "bone", out var bone ) || string.IsNullOrWhiteSpace( bone ) )
			return Fail( r, "corrective needs bone=: corrective bone=elbow_L angle=90" );

		float? angle = null;
		if ( opts.TryGetValue( "angle", out var angleText ) )
		{
			if ( !float.TryParse( angleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var a ) || a <= 0f || a > 170f )
				return Fail( r, $"angle={angleText}: a bend in degrees, above 0 and up to 170." );
			angle = a;
		}

		if ( remove )
		{
			var n = studio.Correctives.RemoveAll( c => c.Bone == bone && (angle is null || MathF.Abs( c.Angle - angle.Value ) < 0.5f) );
			if ( n == 0 ) return Fail( r, $"no corrective shape on '{bone}'{(angle is null ? "" : $" at {angle:0}°")} to remove." );
			r.Log.Add( $"removed {n} corrective shape(s) from {bone}" );
			return true;
		}

		if ( studio.Rig.IndexOf( bone ) < 0 )
			return Fail( r, $"No bone '{bone}'. Bones: {string.Join( ", ", studio.Rig.Bones.Select( b => b.Name ).Take( 20 ) )}." );

		var shape = new CorrectiveShape { Bone = bone, Angle = angle ?? 90f };
		if ( opts.TryGetValue( "axis", out var axisText ) )
		{
			var c = axisText.Split( ',' );
			if ( c.Length != 3 || !c.All( x => float.TryParse( x, NumberStyles.Float, CultureInfo.InvariantCulture, out _ ) ) )
				return Fail( r, $"axis={axisText}: a direction x,y,z (the joint's hinge), or leave it out to pick one." );
			shape.Axis = new Vec3( float.Parse( c[0], CultureInfo.InvariantCulture ), float.Parse( c[1], CultureInfo.InvariantCulture ), float.Parse( c[2], CultureInfo.InvariantCulture ) );
		}

		studio.Correctives.RemoveAll( c => c.MorphName == shape.MorphName );
		studio.Correctives.Add( shape );
		r.Log.Add( $"corrective {shape.MorphName}: built into the model at export" );
		return true;
	}

	static Dictionary<string, string> Pairs( IEnumerable<string> tokens )
	{
		var pairs = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var token in tokens )
		{
			var eq = token.IndexOf( '=' );

			if ( eq <= 0 )
				throw new ArgumentException( $"'{token}' is not key=value." );

			pairs[token[..eq].Trim().ToLowerInvariant()] = token[(eq + 1)..].Trim();
		}

		return pairs;
	}

	static bool TryNumber( string text, out float value ) =>
		float.TryParse( (text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value );

	static float Number( string text ) => TryNumber( text, out var v ) ? v : throw new ArgumentException( $"'{text}' is not a number." );

	static bool Truthy( string text ) => (text ?? "").Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

	static string Resolve( string baseDir, string path ) => Path.IsPathRooted( path ) ? path : Path.Combine( baseDir, path );
}
