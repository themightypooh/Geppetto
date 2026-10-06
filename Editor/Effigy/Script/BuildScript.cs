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
///   set Torso sizez=32
///   fix Torso all
///   render out/torso.png view=sheet
///   describe
///   match front reference.png out/match.png
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
public static class BuildScript
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

				if ( !Assign( studio, feature, pairs, r ) )
					return false;

				studio.Add( feature );
				return RebuildAndCheck( studio, feature, r, $"added {feature.TypeName} '{feature.Name ?? feature.Id}'" );
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
					r.Log.Add( $"{body.Name ?? body.Id}: left-right symmetry error {ModelMeasure.SymmetryError( body.Mesh ) * 100f:0.#}% of width" );
				}

				return true;
			}

			case "match":
			{
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
				if ( args.Count < 2 )
					return Fail( r, "pivot needs a body and a point: pivot Arm @Hub.centre" );

				if ( ModelMeasure.FindBody( studio, args[0] ) is null )
					return Fail( r, $"No body called '{args[0]}'." );

				if ( !Point( studio, args[1], out var p, out var why ) )
					return Fail( r, why );

				r.Pivots[args[0]] = p;
				return true;
			}

			case "export":
			{
				// export models/civic_art/orrery split=1 assets=C:/proj/Assets name=orrery
				if ( args.Count < 1 )
					return Fail( r, "export needs a folder under the assets root: export models/civic_art/orrery [split=1] [assets=root] [name=x]" );

				var folder = args[0].Replace( '\\', '/' ).Trim( '/' );
				var opts = Pairs( args.Skip( 1 ) );
				var root = opts.TryGetValue( "assets", out var assets ) ? assets : baseDir;
				var split = Truthy( opts.GetValueOrDefault( "split", "0" ) );
				var name = opts.GetValueOrDefault( "name", folder.Contains( '/' ) ? folder[(folder.LastIndexOf( '/' ) + 1)..] : folder );
				var dir = Path.Combine( root, folder );
				Directory.CreateDirectory( dir );

				var layout = new StringBuilder();
				layout.AppendLine( "# part\tmodel\tpivot x\ty\tz\tsize x\ty\tz" );

				var bodies = studio.Bodies.Where( b => b?.Mesh is not null && b.Mesh.Faces.Count > 0 && b.Visible ).ToList();

				if ( bodies.Count == 0 )
					return Fail( r, "Nothing to export: no visible bodies." );

				if ( split )
				{
					foreach ( var body in bodies )
					{
						var partName = Safe( body.Name ?? body.Id );
						var mesh = body.Mesh.Clone();
						var (min, max) = ModelMeasure.Bounds( mesh );
						var pivot = r.Pivots.TryGetValue( body.Name ?? "", out var p ) ? p : new Vec3( (min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, min.z );

						MeshTransform.Apply( mesh, Xform.Translate( -pivot ) );
						WriteModel( studio, mesh, dir, folder, partName );
						layout.AppendLine( $"{body.Name ?? body.Id}\t{folder}/{partName}.vmdl\t{N( pivot.x )}\t{N( pivot.y )}\t{N( pivot.z )}\t{N( max.x - min.x )}\t{N( max.y - min.y )}\t{N( max.z - min.z )}" );
					}
				}
				else
				{
					var mesh = new PolyMesh();
					foreach ( var body in bodies )
						MeshTransform.Append( mesh, body.Mesh );

					var (min, max) = ModelMeasure.Bounds( mesh );
					var pivot = r.Pivots.TryGetValue( name, out var p ) ? p : new Vec3( (min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, min.z );
					MeshTransform.Apply( mesh, Xform.Translate( -pivot ) );
					WriteModel( studio, mesh, dir, folder, Safe( name ) );
					layout.AppendLine( $"{name}\t{folder}/{Safe( name )}.vmdl\t{N( pivot.x )}\t{N( pivot.y )}\t{N( pivot.z )}\t{N( max.x - min.x )}\t{N( max.y - min.y )}\t{N( max.z - min.z )}" );
				}

				var layoutPath = Path.Combine( dir, "layout.txt" );
				File.WriteAllText( layoutPath, layout.ToString() );
				r.Written.Add( layoutPath );
				r.Log.Add( $"exported {(split ? bodies.Count : 1)} model(s) to {dir}; layout in layout.txt" );
				return true;
			}

			default:
				return Fail( r, $"Unknown command '{verb}'. Commands: add, set, remove, rebuild, fix, render, describe, measure, distance, symmetry, match, material, pivot, export, save, echo." );
		}
	}

	/// <summary>One .obj and the .vmdl that loads it, materials bound from the studio's slots.</summary>
	static void WriteModel( PartStudio studio, PolyMesh mesh, string dir, string folder, string name )
	{
		var objPath = Path.Combine( dir, name + ".obj" );
		ObjWriter.WriteFile( mesh, objPath, name, materialName: studio.NameForSlot );

		var materials = VmdlMaterials.GroupList( studio, mesh );
		File.WriteAllText( Path.Combine( dir, name + ".vmdl" ), VmdlDocument.Static( $"{folder}/{name}.obj", "", materials ) );
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
