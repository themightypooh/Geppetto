using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Effigy;

/// <summary>
/// Reading and writing a Part Studio.
///
/// WHY THIS IS THE MOST IMPORTANT FILE IN THE FOLDER. Everything else here exists to make the model
/// parametric — the ordered history, rollback, incremental rebuild, references that survive an edit
/// — and none of it means anything if the history dies with the window. Without this, every session
/// is a one-shot bake: you keep the OBJ and you lose the model, and the feature tree is decoration.
/// The whole point of a history is coming back to it in a week and changing the 4 to a 6.
///
/// TEXT, HAND-WRITTEN, LIKE EVERY OTHER FORMAT IN HERE. ObjWriter, SmdWriter and DmxWriter are all
/// written by hand and so is the expression evaluator, for the reason the README gives: the kernel
/// has no dependencies, and it has none because it is meant to be dropped into s&box or Godot or a
/// console runner as loose .cs files. A serializer that reached for a library would be the first
/// thing to break that. Text also diffs, which matters more than it sounds for a format holding
/// somebody's model: a corrupt binary is a shrug, a corrupt text file is usually one bad line you
/// can see.
///
/// FIELDS ARE FOUND BY REFLECTION, NOT LISTED. A feature's Parameters property is not usable as the
/// list to save: PrimitiveFeature changes its parameters with the shape dropdown, so a box saved
/// today would not know what to do with the radius it will want tomorrow. The public FIELDS are
/// stable — SizeX is SizeX whatever the dropdown says — and reflecting over them means a new
/// feature is saved the moment it is written. There is no step to forget.
///
/// It also means an unhandled field type is possible, so DocumentTests asserts that every feature
/// type in the assembly round-trips every field it declares. Adding state a save cannot carry fails
/// the suite rather than quietly not saving, which is the failure this design could otherwise have.
/// </summary>
public static class StudioDocument
{
	/// <summary>Bumped when the format changes in a way a reader has to know about. Written on the
	/// first line so a file from the future can be refused by name rather than by crash.</summary>
	public const int Version = 1;

	public const string Extension = ".effigy";

	// --- writing ------------------------------------------------------------------------------

	public static void WriteFile( PartStudio studio, string path ) =>
		File.WriteAllText( path, Write( studio ) );

	public static string Write( PartStudio studio )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		var sb = new StringBuilder();

		sb.Append( "effigy " ).Append( Version ).Append( '\n' );
		sb.Append( "rollback " ).Append( studio.RollbackIndex ).Append( '\n' );

		// Sorted, so two saves of the same document are the same bytes. A dictionary's order is not
		// promised, and a format that reshuffles itself makes every diff useless.
		foreach ( var (slot, name) in studio.MaterialNames.OrderBy( kv => kv.Key ) )
		{
			if ( !string.IsNullOrWhiteSpace( name ) )
				sb.Append( "material " ).Append( slot ).Append( ' ' ).Append( OneLine( name ) ).Append( '\n' );
		}

		foreach ( var feature in studio.Features )
			WriteFeature( sb, feature );

		return sb.ToString();
	}

	static void WriteFeature( StringBuilder sb, Feature feature )
	{
		sb.Append( "feature " ).Append( feature.GetType().Name ).Append( '\n' );
		sb.Append( "\tid " ).Append( feature.Id ).Append( '\n' );

		// A name can be anything the user typed, so it takes the rest of the line and newlines are
		// stripped rather than escaped — a name spanning two lines is not worth a quoting scheme.
		if ( !string.IsNullOrEmpty( feature.Name ) )
			sb.Append( "\tname " ).Append( OneLine( feature.Name ) ).Append( '\n' );

		sb.Append( "\tsuppressed " ).Append( feature.Suppressed ? 1 : 0 ).Append( '\n' );
		sb.Append( "\tvisible " ).Append( feature.Visible ? 1 : 0 ).Append( '\n' );

		foreach ( var field in StateFields( feature.GetType() ) )
			WriteField( sb, feature, field );

		sb.Append( "end\n" );
	}

	static void WriteField( StringBuilder sb, Feature feature, FieldInfo field )
	{
		var value = field.GetValue( feature );

		switch ( value )
		{
			case FloatParam p:
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( Num( p.Value ) ).Append( '\n' );
				return;

			case IntParam p:
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( p.Value ).Append( '\n' );
				return;

			case BoolParam p:
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( p.Value ? 1 : 0 ).Append( '\n' );
				return;

			case ChoiceParam p:
				// The INDEX, not the label. Labels are user-facing text and get reworded; an index
				// survives that. It does not survive the options being reordered, which is why
				// ResultRemove exists as a named constant rather than a bare 3.
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( p.Index ).Append( '\n' );
				return;

			case Vec3Param p:
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( Vec( p.Value ) ).Append( '\n' );
				return;

			case BodySelectionParam p:
				sb.Append( "\tbodies " ).Append( field.Name );

				foreach ( var id in p.BodyIds )
					sb.Append( ' ' ).Append( id );

				sb.Append( '\n' );
				return;

			case Sketch sketch:
				WriteSketch( sb, field.Name, sketch );
				return;

			case FaceRef face:
				sb.Append( "\tface " ).Append( field.Name ).Append( ' ' ).Append( Face( face ) ).Append( '\n' );
				return;

			case List<int> ints:
				// Shell's OpenFaces, and anything like it. Written even when empty, unlike a null
				// nullable: an empty list and an unmentioned one are the same on load, and writing
				// the line keeps a diff between two saves readable.
				sb.Append( "\tints " ).Append( field.Name );

				foreach ( var n in ints )
					sb.Append( ' ' ).Append( n );

				sb.Append( '\n' );
				return;

			case List<string> texts:
				// Loft's Sections, and anything like it. One line with every entry on it, like
				// ints rather than like facelist, because these are short ids and a line each
				// would bury the rest of the feature.
				sb.Append( "\ttexts " ).Append( field.Name );

				foreach ( var text in texts )
					sb.Append( ' ' ).Append( text );

				sb.Append( '\n' );
				return;

			case List<FaceRef> faces:
				foreach ( var f in faces )
					sb.Append( "\tfacelist " ).Append( field.Name ).Append( ' ' ).Append( Face( f ) ).Append( '\n' );

				return;

			case Vec2 v:
				sb.Append( "\tvec2 " ).Append( field.Name ).Append( ' ' )
					.Append( Num( v.x ) ).Append( ' ' ).Append( Num( v.y ) ).Append( '\n' );
				return;

			case string s:
				if ( s.Length > 0 )
					sb.Append( "\ttext " ).Append( field.Name ).Append( ' ' ).Append( OneLine( s ) ).Append( '\n' );

				return;

			case null:
				// A null nullable — no Face, no RegionSeed — is written as nothing at all. Absence
				// IS the value, and a reader starting from a fresh feature already has it.
				return;
		}

		// Unreachable while DocumentTests passes: it asserts every field of every feature type is a
		// type this switch handles. Throwing rather than skipping is what makes that test able to
		// fail — a silent skip would save a file that quietly lost half a feature.
		throw new InvalidOperationException(
			$"{feature.GetType().Name}.{field.Name} is a {field.FieldType.Name}, which StudioDocument cannot save. "
			+ "Add a case for it here and in ReadField." );
	}

	static void WriteSketch( StringBuilder sb, string fieldName, Sketch sketch )
	{
		sb.Append( "\tsketch " ).Append( fieldName ).Append( '\n' );
		sb.Append( "\t\ttolerance " ).Append( Num( sketch.Tolerance ) ).Append( '\n' );
		sb.Append( "\t\tplane " ).Append( Vec( sketch.Plane.Origin ) ).Append( ' ' )
			.Append( Vec( sketch.Plane.XAxis ) ).Append( ' ' ).Append( Vec( sketch.Plane.YAxis ) ).Append( '\n' );

		foreach ( var p in sketch.Points )
			sb.Append( "\t\tpoint " ).Append( Num( p.x ) ).Append( ' ' ).Append( Num( p.y ) ).Append( '\n' );

		foreach ( var curve in sketch.Curves )
		{
			switch ( curve )
			{
				case SketchLine line:
					sb.Append( "\t\tline " ).Append( line.Start ).Append( ' ' ).Append( line.End );
					break;

				case SketchArc arc:
					sb.Append( "\t\tarc " ).Append( arc.Center ).Append( ' ' ).Append( arc.Start ).Append( ' ' )
						.Append( arc.End ).Append( ' ' ).Append( arc.Clockwise ? 1 : 0 );
					break;

				case SketchCircle circle:
					sb.Append( "\t\tcircle " ).Append( circle.Center ).Append( ' ' ).Append( Num( circle.Radius ) );
					break;

				case SketchEllipse ellipse:
					sb.Append( "\t\tellipse " ).Append( ellipse.Center ).Append( ' ' )
						.Append( ellipse.MajorPoint ).Append( ' ' ).Append( Num( ellipse.MinorRadius ) );
					break;

				// The point COUNT is written before the points, because everything else in this
				// format has a fixed field count and the reader finds a curve's id and construction
				// flag at a known offset. A variable-length record without a count would make that
				// offset unknowable without counting backwards from the end, which works right up
				// until a field is added.
				case SketchSpline spline:
					sb.Append( "\t\tspline " ).Append( spline.Closed ? 1 : 0 ).Append( ' ' )
						.Append( spline.Points.Count );

					foreach ( var index in spline.Points )
						sb.Append( ' ' ).Append( index );

					break;

				default:
					throw new InvalidOperationException( $"StudioDocument cannot save a {curve.GetType().Name}" );
			}

			// Id and construction come last and in the same order for every curve type, so the
			// reader can strip them before it looks at what kind of curve it has.
			sb.Append( ' ' ).Append( curve.Id ).Append( ' ' ).Append( curve.Construction ? 1 : 0 ).Append( '\n' );
		}

		foreach ( var c in sketch.Constraints )
		{
			sb.Append( "\t\tconstraint " ).Append( (int)c.Kind ).Append( ' ' )
				.Append( c.PointA ).Append( ' ' ).Append( c.PointB ).Append( ' ' )
				.Append( c.PointC ).Append( ' ' ).Append( c.PointD ).Append( ' ' )
				.Append( Num( c.Value ) ).Append( ' ' )
				.Append( string.IsNullOrEmpty( c.CurveId ) ? "-" : c.CurveId ).Append( ' ' )
				.Append( Num( c.ValueY ) ).Append( '\n' );
		}

		sb.Append( "\tendsketch\n" );
	}

	// --- reading ------------------------------------------------------------------------------

	public static PartStudio ReadFile( string path ) => Read( File.ReadAllText( path ) );

	/// <summary>
	/// Parse a document back into a studio. Throws with the line number on anything malformed.
	///
	/// The studio comes back NOT rebuilt. Loading is about restoring the tree; running it is the
	/// caller's business and its errors are the model's errors, not the file's — an editor wants to
	/// show a file that loads and fails to build, because that is exactly the state you opened it to
	/// fix.
	/// </summary>
	public static PartStudio Read( string text )
	{
		var studio = new PartStudio();
		var lines = (text ?? "").Replace( "\r\n", "\n" ).Split( '\n' );
		var rollback = int.MaxValue;
		var i = 0;

		string Line() => lines[i];

		if ( lines.Length == 0 || !Line().StartsWith( "effigy " ) )
			throw new InvalidDataException( "Not an Effigy document — the first line should read 'effigy <version>'." );

		var version = ParseInt( Line()[7..].Trim(), 1 );

		if ( version > Version )
		{
			throw new InvalidDataException(
				$"This file was written by a newer Effigy (format {version}; this build reads {Version})." );
		}

		i++;

		for ( ; i < lines.Length; i++ )
		{
			var line = Line().Trim();

			if ( line.Length == 0 )
				continue;

			if ( line.StartsWith( "rollback " ) )
			{
				rollback = ParseInt( line[9..], int.MaxValue );
				continue;
			}

			if ( line.StartsWith( "material " ) )
			{
				var (slot, name) = Split( line[9..] );
				studio.MaterialNames[ParseInt( slot, 0 )] = name;
				continue;
			}

			if ( !line.StartsWith( "feature " ) )
				throw new InvalidDataException( $"Line {i + 1}: expected a feature, found '{line}'" );

			studio.Add( ReadFeature( lines, ref i ) );
		}

		// After the features, so it can be clamped against a tree that actually exists. A rollback
		// index past the end is not corruption — deleting the last feature of a rolled-back tree
		// leaves exactly that — and PartStudio treats it as "roll to end".
		studio.RollbackIndex = Math.Min( rollback, studio.Features.Count );

		return studio;
	}

	static Feature ReadFeature( string[] lines, ref int i )
	{
		var typeName = lines[i].Trim()[8..].Trim();
		var feature = Create( typeName )
			?? throw new InvalidDataException( $"Line {i + 1}: no feature type named '{typeName}' in this build." );

		var fields = StateFields( feature.GetType() ).ToDictionary( f => f.Name, f => f );

		// A list field accumulates across lines, so it is cleared the first time one is seen rather
		// than up front — otherwise loading would wipe a default that the file simply does not
		// mention.
		var clearedLists = new HashSet<string>();

		for ( i++; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 )
				continue;

			if ( line == "end" )
				return feature;

			var (key, rest) = Split( line );

			switch ( key )
			{
				case "id": feature.Id = rest; continue;
				case "name": feature.Name = rest; continue;
				case "suppressed": feature.Suppressed = rest == "1"; continue;
				case "visible": feature.Visible = rest == "1"; continue;
			}

			var (fieldName, value) = Split( rest );

			if ( !fields.TryGetValue( fieldName, out var field ) )
			{
				// A field this build does not have. Ignored on purpose: a file written by a version
				// with an extra parameter should still open, minus that parameter, rather than
				// refusing outright.
				if ( key == "sketch" )
					SkipSketch( lines, ref i );

				continue;
			}

			ReadField( feature, field, key, value, lines, ref i, clearedLists );
		}

		throw new InvalidDataException( $"The document ends inside a {typeName} — no 'end' line." );
	}

	static void ReadField( Feature feature, FieldInfo field, string key, string value, string[] lines, ref int i,
		HashSet<string> clearedLists )
	{
		var current = field.GetValue( feature );

		switch ( key )
		{
			case "param":
				switch ( current )
				{
					case FloatParam p: p.Value = ParseFloat( value ); return;
					case IntParam p: p.Value = ParseInt( value, p.Value ); return;
					case BoolParam p: p.Value = value == "1"; return;
					case ChoiceParam p: p.Index = ParseInt( value, p.Index ); return;
					case Vec3Param p: p.Value = ParseVec3( value ); return;
				}

				return;

			case "bodies":
				if ( current is BodySelectionParam bodies )
				{
					bodies.BodyIds.Clear();
					bodies.BodyIds.AddRange( value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) );
				}

				return;

			case "text":
				field.SetValue( feature, value );
				return;

			case "vec2":
			{
				var parts = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
				field.SetValue( feature, new Vec2( ParseFloat( parts[0] ), ParseFloat( parts[1] ) ) );
				return;
			}

			case "face":
				field.SetValue( feature, ParseFace( value ) );
				return;

			case "texts":
			{
				if ( current is not List<string> texts )
					return;

				texts.Clear();

				foreach ( var part in value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
					texts.Add( part );

				return;
			}

			case "ints":
			{
				if ( current is not List<int> ints )
					return;

				ints.Clear();

				foreach ( var part in value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
					ints.Add( ParseInt( part, 0 ) );

				return;
			}

			case "facelist":
			{
				if ( field.GetValue( feature ) is not List<FaceRef> list )
					return;

				if ( clearedLists.Add( field.Name ) )
					list.Clear();

				list.Add( ParseFace( value ) );
				return;
			}

			case "sketch":
				field.SetValue( feature, ReadSketch( lines, ref i ) );
				return;
		}
	}

	static Sketch ReadSketch( string[] lines, ref int i )
	{
		var sketch = new Sketch();

		for ( i++; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 )
				continue;

			if ( line == "endsketch" )
				return sketch;

			var (key, rest) = Split( line );
			var parts = rest.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

			switch ( key )
			{
				case "tolerance":
					sketch.Tolerance = ParseFloat( rest );
					break;

				case "plane":
					sketch.Plane = new SketchPlane(
						new Vec3( ParseFloat( parts[0] ), ParseFloat( parts[1] ), ParseFloat( parts[2] ) ),
						new Vec3( ParseFloat( parts[3] ), ParseFloat( parts[4] ), ParseFloat( parts[5] ) ),
						new Vec3( ParseFloat( parts[6] ), ParseFloat( parts[7] ), ParseFloat( parts[8] ) ) );
					break;

				case "point":
					sketch.AddPoint( ParseFloat( parts[0] ), ParseFloat( parts[1] ) );
					break;

				case "line":
					sketch.Add( Tagged( new SketchLine( ParseInt( parts[0], 0 ), ParseInt( parts[1], 0 ) ), parts, 2 ) );
					break;

				case "arc":
					sketch.Add( Tagged( new SketchArc(
						ParseInt( parts[0], 0 ), ParseInt( parts[1], 0 ), ParseInt( parts[2], 0 ),
						parts[3] == "1" ), parts, 4 ) );
					break;

				case "circle":
					sketch.Add( Tagged( new SketchCircle( ParseInt( parts[0], 0 ), ParseFloat( parts[1] ) ), parts, 2 ) );
					break;

				case "ellipse":
					sketch.Add( Tagged( new SketchEllipse(
						ParseInt( parts[0], 0 ), ParseInt( parts[1], 0 ), ParseFloat( parts[2] ) ), parts, 3 ) );
					break;

				case "spline":
				{
					var count = ParseInt( parts[1], 0 );
					var indices = new List<int>( count );

					for ( var k = 0; k < count && 2 + k < parts.Length; k++ )
						indices.Add( ParseInt( parts[2 + k], 0 ) );

					sketch.Add( Tagged( new SketchSpline( indices, parts[0] == "1" ), parts, 2 + count ) );
					break;
				}

				case "constraint":
				{
					var constraint = new SketchConstraint( (SketchConstraintKind)ParseInt( parts[0], 0 ),
						ParseInt( parts[1], -1 ), ParseInt( parts[2], -1 ) )
					{
						PointC = ParseInt( parts[3], -1 ),
						PointD = ParseInt( parts[4], -1 ),
						Value = ParseFloat( parts[5] ),
						CurveId = parts[6] == "-" ? null : parts[6],

						// Appended after the CurveId rather than beside Value, so every index before it
						// keeps its meaning and a document written before Fixed existed still reads.
						// Absent means zero, which is what those documents meant.
						ValueY = parts.Length > 7 ? ParseFloat( parts[7] ) : 0f
					};

					sketch.Constraints.Add( constraint );
					break;
				}
			}
		}

		throw new InvalidDataException( "The document ends inside a sketch — no 'endsketch' line." );
	}

	/// <summary>Attach the id and construction flag every curve line ends with.</summary>
	static T Tagged<T>( T curve, string[] parts, int at ) where T : SketchCurve
	{
		if ( parts.Length > at )
			curve.Id = parts[at];

		if ( parts.Length > at + 1 )
			curve.Construction = parts[at + 1] == "1";

		return curve;
	}

	/// <summary>Walk past a sketch belonging to a field this build does not know about, so its
	/// contents are not read as feature lines.</summary>
	static void SkipSketch( string[] lines, ref int i )
	{
		for ( i++; i < lines.Length; i++ )
		{
			if ( lines[i].Trim() == "endsketch" )
				return;
		}
	}

	// --- shared -------------------------------------------------------------------------------

	/// <summary>
	/// The fields a feature's state lives in.
	///
	/// Public instance fields, minus the four the writer handles by name. Declared-only would miss
	/// what a feature inherits — SketchFeatureId and RegionSeed live on SketchConsumingFeature, and
	/// forgetting them would lose which sketch an extrude consumes.
	/// </summary>
	static IEnumerable<FieldInfo> StateFields( Type type ) => type
		.GetFields( BindingFlags.Public | BindingFlags.Instance )
		.Where( f => f.Name is not ("Id" or "Name" or "Suppressed" or "Visible") )
		.OrderBy( f => f.Name, StringComparer.Ordinal );

	/// <summary>
	/// What a feature type used to be called, for documents written before it was renamed.
	///
	/// A SAVED FILE IS A PROMISE. The type token in it is a C# class name, so renaming a class is a
	/// breaking change to every document already on disk unless the old name keeps resolving.
	/// `BevelFeature` became `ChamferFeature` when the flat cut and the rounded one were split into
	/// the two operations Onshape names — the parameters are unchanged, so an old bevel loads as
	/// the chamfer it always was, with its width and angle intact.
	///
	/// Entries are never removed. The cost of one line is nothing next to a document that opens
	/// with a line number and a type name nobody recognises.
	/// </summary>
	static readonly Dictionary<string, string> RenamedFeatures = new()
	{
		["BevelFeature"] = "ChamferFeature",
	};

	/// <summary>Find a feature type by name, in whatever assembly the kernel ended up in.</summary>
	static Feature Create( string typeName )
	{
		if ( RenamedFeatures.TryGetValue( typeName, out var current ) )
			typeName = current;

		var type = typeof( Feature ).Assembly.GetTypes()
			.FirstOrDefault( t => t.Name == typeName && !t.IsAbstract && typeof( Feature ).IsAssignableFrom( t ) );

		return type is null ? null : (Feature)Activator.CreateInstance( type );
	}

	/// <summary>Round-trip float formatting. "R" rather than a fixed number of decimals: a
	/// dimension typed as 0.1 has to come back as 0.1, and a rounded one comes back as a model that
	/// has moved very slightly every time it is opened and saved.</summary>
	static string Num( float f ) => f.ToString( "R", CultureInfo.InvariantCulture );

	static string Vec( Vec3 v ) => $"{Num( v.x )} {Num( v.y )} {Num( v.z )}";

	static string Face( FaceRef f ) =>
		$"{f.BodyId} {Vec( f.Point )} {Vec( f.Normal )} {Num( f.Anchor.x )} {Num( f.Anchor.y )} "
		+ $"{(f.AnchorFromMaxX ? 1 : 0)} {(f.AnchorFromMaxY ? 1 : 0)} {(f.Anchored ? 1 : 0)}";

	static FaceRef ParseFace( string value )
	{
		var p = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		var point = new Vec3( ParseFloat( p[1] ), ParseFloat( p[2] ), ParseFloat( p[3] ) );
		var normal = new Vec3( ParseFloat( p[4] ), ParseFloat( p[5] ), ParseFloat( p[6] ) );

		// Anchored is the last flag, and it decides which constructor is right: the unanchored one
		// leaves Anchored false, which means "sit at the centre of whatever face this resolves to".
		// Reading an anchor into a reference that never had one would move every old sketch.
		if ( p.Length < 12 || p[11] != "1" )
			return new FaceRef( p[0], point, normal );

		return new FaceRef( p[0], point, normal,
			new Vec2( ParseFloat( p[7] ), ParseFloat( p[8] ) ), p[9] == "1", p[10] == "1" );
	}

	static Vec3 ParseVec3( string value )
	{
		var p = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		return new Vec3( ParseFloat( p[0] ), ParseFloat( p[1] ), ParseFloat( p[2] ) );
	}

	static (string Key, string Value) Split( string line )
	{
		var space = line.IndexOf( ' ' );

		return space < 0 ? (line, "") : (line[..space], line[(space + 1)..].Trim());
	}

	static float ParseFloat( string s ) =>
		float.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) ? f : 0f;

	static int ParseInt( string s, int fallback ) =>
		int.TryParse( s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i ) ? i : fallback;

	static string OneLine( string s ) => s.Replace( '\n', ' ' ).Replace( '\r', ' ' ).Trim();
}
