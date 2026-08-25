using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Effigy.Tests;

/// <summary>
/// Saving and loading a Part Studio.
///
/// The bar here is higher than "it wrote a file". A modelling document that loses something on the
/// way through is worse than one that fails to open: a failure sends you to a backup, a silent loss
/// sends you back to work you thought you had. So the tests compare the model that comes back
/// against the one that went in, field by field, and the strongest one is generated rather than
/// written — every feature type in the assembly, every field it declares, set to a value that is not
/// the default, round-tripped and compared.
///
/// That last test is what makes the reflective design safe. StudioDocument saves fields it finds
/// rather than fields someone remembered to list, so the risk it carries is a field of a type the
/// writer has no case for. This turns that from a silent hole into a failing test the day the field
/// is added.
/// </summary>
public static class DocumentTests
{
	public static void Run()
	{
		Report.Section( "document: a studio survives the round trip" );
		TestRoundTrip();

		Report.Section( "document: every feature type carries every field it declares" );
		TestEveryFieldSurvives();

		Report.Section( "document: the geometry is identical after a reload" );
		TestRebuildMatches();

		Report.Section( "document: malformed and unfamiliar files" );
		TestBadInput();

		Report.Section( "document: it goes to disk and comes back" );
		TestFile();
	}

	static void TestRoundTrip()
	{
		var studio = Worked();
		var back = StudioDocument.Read( StudioDocument.Write( studio ) );

		Report.Check( "the same number of features come back",
			back.Features.Count == studio.Features.Count,
			$"{studio.Features.Count} became {back.Features.Count}" );

		Report.Check( "in the same order and of the same types",
			back.Features.Select( f => f.GetType().Name ).SequenceEqual( studio.Features.Select( f => f.GetType().Name ) ),
			string.Join( ", ", back.Features.Select( f => f.GetType().Name ) ) );

		// IDS ARE NOT COSMETIC. Body ids derive from the feature that made them, and a FaceRef holds
		// a body id — so a load that reissued feature ids would break every sketch drawn on a face
		// the moment the file was reopened, which is the single worst thing this format could do.
		Report.Check( "feature ids are preserved exactly",
			back.Features.Select( f => f.Id ).SequenceEqual( studio.Features.Select( f => f.Id ) ),
			string.Join( ", ", back.Features.Select( f => f.Id ) ) );

		Report.Check( "names come back", back.Features[0].Name == studio.Features[0].Name,
			$"'{studio.Features[0].Name}' became '{back.Features[0].Name}'" );

		Report.Check( "the rollback bar comes back", back.RollbackIndex == studio.RollbackIndex,
			$"{studio.RollbackIndex} became {back.RollbackIndex}" );

		Report.Check( "material slot names come back, spaces and all",
			back.NameForSlot( 3 ) == "brushed steel" && back.NameForSlot( 7 ) == "rubber",
			$"{back.NameForSlot( 3 )} / {back.NameForSlot( 7 )}" );

		Report.Check( "and an unnamed slot still falls back to its number",
			back.NameForSlot( 5 ) == "material_5", back.NameForSlot( 5 ) );

		var box = (PrimitiveFeature)back.Features[0];

		Report.Check( "float parameters come back exactly",
			box.SizeX.Value == 4.25f && box.SizeZ.Value == 0.1f,
			$"{box.SizeX.Value} / {box.SizeZ.Value}" );

		Report.Check( "and suppression with them", back.Features.Any( f => f.Suppressed ) );

		var sketch = back.Features.OfType<SketchFeature>().First();

		Report.Check( "the sketch's points come back",
			sketch.Sketch.Points.Count == 6, $"{sketch.Sketch.Points.Count} points" );

		Report.Check( "its curves come back with their ids",
			sketch.Sketch.Curves.Count == 5
			&& sketch.Sketch.Curves.All( c => !string.IsNullOrEmpty( c.Id ) ),
			$"{sketch.Sketch.Curves.Count} curves" );

		Report.Check( "construction geometry stays construction",
			sketch.Sketch.Curves.Count( c => c.Construction ) == 1 );

		Report.Check( "and its constraints come back whole",
			sketch.Sketch.Constraints.Count == 2
			&& sketch.Sketch.Constraints.Any( c => c.Kind == SketchConstraintKind.Distance && c.Value == 3.5f ),
			$"{sketch.Sketch.Constraints.Count} constraints" );

		var attached = back.Features.OfType<SketchFeature>().Last();

		Report.Check( "a sketch attached to a face remembers which face",
			attached.Face is { } face && face.BodyId == "boxb0" && face.Anchored,
			attached.Face?.BodyId ?? "no face" );

		var extrude = back.Features.OfType<ExtrudeFeature>().First();

		Report.Check( "an extrude remembers which sketch it consumes",
			extrude.SketchFeatureId == "sketch1", extrude.SketchFeatureId ?? "none" );

		Report.Check( "and which region of it", extrude.RegionSeed is { } seed
			&& MathF.Abs( seed.x - 1.5f ) < 1e-6f, $"{extrude.RegionSeed}" );

		Report.Check( "the Result choice comes back", extrude.Result.Index == 1,
			$"index {extrude.Result.Index}" );

		var paint = back.Features.OfType<FaceMaterialFeature>().First();

		Report.Check( "a face material keeps every face it was given", paint.Faces.Count == 2,
			$"{paint.Faces.Count} faces" );

		Report.Check( "and the slot it paints them", paint.Material.Value == 3, $"{paint.Material.Value}" );

		var shell = back.Features.OfType<ShellFeature>().First();

		Report.Check( "a body selection comes back", shell.Bodies.BodyIds.SequenceEqual( new[] { "boxb0" } ),
			string.Join( ", ", shell.Bodies.BodyIds ) );

		// Writing what was read must produce the same text. A round trip that is stable only once is
		// a round trip that is losing something slowly.
		Report.Check( "writing it again produces byte-identical text",
			StudioDocument.Write( back ) == StudioDocument.Write( studio ) );
	}

	/// <summary>
	/// Every feature, every field, set to something that is not its default.
	///
	/// The point is coverage without a list. StudioDocument finds fields by reflection, so this
	/// finds them the same way: anything it cannot save throws while writing, and anything it saves
	/// but does not restore comes back unequal. A feature added next year is covered the day it is
	/// written, which is the only way this stays true.
	/// </summary>
	static void TestEveryFieldSurvives()
	{
		foreach ( var type in FeatureTypes() )
		{
			var feature = (Feature)Activator.CreateInstance( type );
			var fields = SaveableFields( type );

			foreach ( var field in fields )
				Disturb( feature, field );

			var studio = new PartStudio();
			studio.Add( feature );

			string text;

			try
			{
				text = StudioDocument.Write( studio );
			}
			catch ( Exception e )
			{
				// The failure this whole design is exposed to: a field of a type the writer has no
				// case for. Better here than as a model that saves and comes back missing a piece.
				Report.Check( $"{type.Name} can be written", false, e.Message );
				continue;
			}

			var back = StudioDocument.Read( text ).Features.Single();
			var mismatch = fields.FirstOrDefault( f => !Same( field: f, a: feature, b: back ) );

			Report.Check( $"{type.Name} round-trips all {fields.Count} of its fields",
				mismatch is null,
				mismatch is null ? "" : $"{mismatch.Name} ({mismatch.FieldType.Name}) came back different" );
		}
	}

	static void TestRebuildMatches()
	{
		// The real test of a document format for a modeller: the model it describes has to build
		// into the same geometry. Comparing the tree field by field can pass while the model comes
		// out different, if something the tree depends on was not carried.
		var studio = Worked();
		var originalReport = studio.Rebuild();

		Report.Check( "the fixture builds cleanly to begin with", !originalReport.HasErrors,
			string.Join( "; ", studio.Features.Where( f => f.Error is not null ).Select( f => $"{f.TypeName}: {f.Error}" ) ) );

		var before = studio.ToMesh();

		var back = StudioDocument.Read( StudioDocument.Write( studio ) );
		var report = back.Rebuild();

		// Compared against the original rather than demanded to be clean: what this test is about is
		// the format carrying the model, and a fixture that legitimately errored should error the
		// same way on both sides rather than being unrepresentable.
		Report.Check( "the reloaded studio builds exactly as well as the original",
			report.Errors.Count == originalReport.Errors.Count,
			$"{originalReport.Errors.Count} errors became {report.Errors.Count}: {report}" );

		var after = back.ToMesh();

		Report.Check( "same vertex count", after.VertexCount == before.VertexCount,
			$"{before.VertexCount} became {after.VertexCount}" );

		Report.Check( "same face count", after.FaceCount == before.FaceCount,
			$"{before.FaceCount} became {after.FaceCount}" );

		Report.Check( "same enclosed volume", MathF.Abs( Volume( after ) - Volume( before ) ) < 1e-3f,
			$"{Volume( before ):0.####} became {Volume( after ):0.####}" );

		Report.Check( "and the same body ids, so anything referring to one still resolves",
			back.Bodies.Select( b => b.Id ).SequenceEqual( studio.Bodies.Select( b => b.Id ) ),
			string.Join( ", ", back.Bodies.Select( b => b.Id ) ) );

		// Material assignments are carried by faces rather than by bodies, so they are worth their
		// own check: a document that lost them would still pass every count above.
		Report.Check( "painted faces are still painted",
			after.Faces.Count( f => f.Material == 3 ) == before.Faces.Count( f => f.Material == 3 ),
			$"{before.Faces.Count( f => f.Material == 3 )} became {after.Faces.Count( f => f.Material == 3 )}" );
	}

	static void TestBadInput()
	{
		Report.Check( "something that is not a document is refused",
			Refused( "hello\nworld\n", out var notADoc ), "it parsed" );

		Report.Check( "and says what it wanted instead",
			notADoc is not null && notADoc.Contains( "effigy" ), notADoc ?? "" );

		Report.Check( "a file from a newer format is refused by version",
			Refused( $"effigy {StudioDocument.Version + 9}\n", out var newer ), "it parsed" );

		Report.Check( "naming both versions so it is obvious what to do",
			newer is not null && newer.Contains( "newer" ), newer ?? "" );

		Report.Check( "a feature type this build does not have is named",
			Refused( "effigy 1\nfeature SomethingFromTheFuture\n\tid x\nend\n", out var unknown ), "it parsed" );

		Report.Check( "rather than throwing something unreadable",
			unknown is not null && unknown.Contains( "SomethingFromTheFuture" ), unknown ?? "" );

		Report.Check( "a document that stops mid-feature is refused",
			Refused( "effigy 1\nfeature PrimitiveFeature\n\tid x\n", out _ ), "it parsed" );

		Report.Check( "and one that stops mid-sketch",
			Refused( "effigy 1\nfeature SketchFeature\n\tid x\n\tsketch Sketch\n\t\tpoint 0 0\n", out _ ), "it parsed" );

		// FORWARD COMPATIBILITY, which is the difference between a format that can grow and one
		// that strands files. A parameter this build has never heard of is skipped, and everything
		// around it still loads.
		var withExtra = "effigy 1\nfeature PrimitiveFeature\n\tid keep\n\tparam SizeX 7\n"
			+ "\tparam SomeFutureThing 42\n\tparam SizeY 3\nend\n";

		var loaded = StudioDocument.Read( withExtra ).Features.Single() as PrimitiveFeature;

		Report.Check( "an unfamiliar parameter is skipped rather than fatal",
			loaded is not null && loaded.SizeX.Value == 7f && loaded.SizeY.Value == 3f,
			loaded is null ? "did not load" : $"{loaded.SizeX.Value} / {loaded.SizeY.Value}" );

		// An empty studio is a real thing to save — a new document someone hits Ctrl+S in.
		var empty = StudioDocument.Read( StudioDocument.Write( new PartStudio() ) );

		Report.Check( "an empty studio round-trips", empty.Features.Count == 0 );
	}

	static void TestFile()
	{
		var path = Path.Combine( Path.GetTempPath(), $"effigy-doc-{Guid.NewGuid():N}{StudioDocument.Extension}" );

		try
		{
			var studio = Worked();
			StudioDocument.WriteFile( studio, path );

			Report.Check( "the file is written", File.Exists( path ) );

			var back = StudioDocument.ReadFile( path );

			Report.Check( "and reads back as the same tree",
				back.Features.Count == studio.Features.Count
				&& back.Features.Select( f => f.Id ).SequenceEqual( studio.Features.Select( f => f.Id ) ) );

			// Text, and diffable. The format is meant to be readable when something goes wrong with
			// it, which a binary would not be.
			var text = File.ReadAllText( path );

			Report.Check( "it is text a person can read", text.StartsWith( "effigy " ) && text.Contains( "feature " ) );

			Report.Check( "with no culture-dependent decimal commas in it",
				!System.Text.RegularExpressions.Regex.IsMatch( text, @"\d,\d" ) );
		}
		finally
		{
			if ( File.Exists( path ) )
				File.Delete( path );
		}
	}

	// --- helpers ------------------------------------------------------------------------------

	/// <summary>A studio with something of everything in it: a primitive, two sketches (one on a
	/// face), an extrude that names its sketch and region, a face material, a shell with a body
	/// selection, a suppressed feature and a rollback bar.</summary>
	static PartStudio Worked()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.Id = "box";
		box.Name = "Base block";
		box.SizeX.Value = 4.25f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 0.1f;

		var sketch = studio.Add( new SketchFeature() );
		sketch.Id = "sketch1";
		sketch.Sketch.AddRectangle( new Vec2( 1f, 1f ), new Vec2( 2f, 2f ) );

		// A construction line ALONGSIDE the rectangle, not one of its edges. Marking an edge as
		// construction takes it out of profile finding, which leaves the loop open and the extrude
		// with nothing to build — correct behaviour, and not what this fixture is for.
		sketch.Sketch.AddLine( new Vec2( 1f, 1.5f ), new Vec2( 2f, 1.5f ) ).Construction = true;
		sketch.Sketch.AddConstraint( sketch.Sketch.Curves[0], SketchConstraintKind.Horizontal );
		sketch.Sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 3.5f );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Id = "extrude1";
		extrude.SketchFeatureId = "sketch1";
		extrude.RegionSeed = new Vec2( 1.5f, 1.5f );
		extrude.Distance.Value = 1.25f;
		extrude.Result.Index = 1;

		var onFace = studio.Add( new SketchFeature() );
		onFace.Id = "sketch2";
		onFace.Face = new FaceRef( "boxb0", new Vec3( 0, 0, 0.05f ), new Vec3( 0, 0, 1 ),
			new Vec2( 0.25f, 0.5f ), fromMaxX: true, fromMaxY: false );
		onFace.Sketch.AddCircle( new Vec2( 0, 0 ), 0.4f );

		var paint = studio.Add( new FaceMaterialFeature() );
		paint.Id = "paint1";
		paint.Material.Value = 3;
		paint.Faces.Add( new FaceRef( "boxb0", new Vec3( 0, 0, 0.05f ), new Vec3( 0, 0, 1 ) ) );
		paint.Faces.Add( new FaceRef( "boxb0", new Vec3( 0, 0, -0.05f ), new Vec3( 0, 0, -1 ) ) );

		var shell = studio.Add( new ShellFeature() );
		shell.Id = "shell1";
		shell.Bodies.BodyIds.Add( "boxb0" );
		shell.Suppressed = true;

		studio.RollbackIndex = 6;

		studio.MaterialNames[3] = "brushed steel";
		studio.MaterialNames[7] = "rubber";

		return studio;
	}

	static IEnumerable<Type> FeatureTypes() => typeof( Feature ).Assembly.GetTypes()
		.Where( t => !t.IsAbstract && typeof( Feature ).IsAssignableFrom( t ) )
		.OrderBy( t => t.Name, StringComparer.Ordinal );

	static List<FieldInfo> SaveableFields( Type type ) => type
		.GetFields( BindingFlags.Public | BindingFlags.Instance )
		.Where( f => f.Name is not ("Id" or "Name" or "Suppressed" or "Visible") )
		.ToList();

	/// <summary>Move a field off its default, so a round trip that drops it is visible. A field left
	/// at its default would come back "correct" from a writer that never wrote it.</summary>
	static void Disturb( Feature feature, FieldInfo field )
	{
		switch ( field.GetValue( feature ) )
		{
			case FloatParam p: p.Value = 2.375f; break;
			case IntParam p: p.Value = Math.Clamp( 3, p.Min, p.Max ); break;
			case BoolParam p: p.Value = !p.Value; break;
			case ChoiceParam p: p.Index = p.Options.Length - 1; break;
			case Vec3Param p: p.Value = new Vec3( 0.5f, -1.25f, 2f ); break;
			case BodySelectionParam p: p.BodyIds.Add( "someb0" ); break;
			case List<int> ints: ints.Add( 4 ); ints.Add( 7 ); break;
			case List<FaceRef> list: list.Add( new FaceRef( "someb0", new Vec3( 1, 2, 3 ), new Vec3( 0, 0, 1 ) ) ); break;

			case Sketch sketch:
				sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1.5f, 2.5f ) );
				sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 1.5f );
				break;

			case null when field.FieldType == typeof( FaceRef? ):
				field.SetValue( feature, new FaceRef( "someb0", new Vec3( 1, 2, 3 ), new Vec3( 0, 1, 0 ),
					new Vec2( 0.25f, 0.75f ), fromMaxX: true, fromMaxY: true ) );
				break;

			case null when field.FieldType == typeof( Vec2? ):
				field.SetValue( feature, new Vec2( 0.75f, -0.25f ) );
				break;

			case string:
				field.SetValue( feature, "some-id" );
				break;
		}
	}

	static bool Same( FieldInfo field, Feature a, Feature b )
	{
		var x = field.GetValue( a );
		var y = field.GetValue( b );

		return (x, y) switch
		{
			(FloatParam p, FloatParam q) => p.Value == q.Value,
			(IntParam p, IntParam q) => p.Value == q.Value,
			(BoolParam p, BoolParam q) => p.Value == q.Value,
			(ChoiceParam p, ChoiceParam q) => p.Index == q.Index,
			(Vec3Param p, Vec3Param q) => p.Value.x == q.Value.x && p.Value.y == q.Value.y && p.Value.z == q.Value.z,
			(BodySelectionParam p, BodySelectionParam q) => p.BodyIds.SequenceEqual( q.BodyIds ),
			(List<int> p, List<int> q) => p.SequenceEqual( q ),
			(List<FaceRef> p, List<FaceRef> q) => p.Count == q.Count && p.Zip( q ).All( pair => SameFace( pair.First, pair.Second ) ),
			(Sketch p, Sketch q) => SameSketch( p, q ),
			(FaceRef p, FaceRef q) => SameFace( p, q ),
			(Vec2 p, Vec2 q) => p.x == q.x && p.y == q.y,
			(string p, string q) => p == q,
			(null, null) => true,
			_ => Equals( x, y )
		};
	}

	static bool SameFace( FaceRef a, FaceRef b ) =>
		a.BodyId == b.BodyId && a.Anchored == b.Anchored
		&& a.AnchorFromMaxX == b.AnchorFromMaxX && a.AnchorFromMaxY == b.AnchorFromMaxY
		&& a.Point.x == b.Point.x && a.Point.y == b.Point.y && a.Point.z == b.Point.z
		&& a.Anchor.x == b.Anchor.x && a.Anchor.y == b.Anchor.y;

	static bool SameSketch( Sketch a, Sketch b ) =>
		a.Points.Count == b.Points.Count
		&& a.Curves.Count == b.Curves.Count
		&& a.Constraints.Count == b.Constraints.Count
		&& a.Points.Zip( b.Points ).All( p => p.First.x == p.Second.x && p.First.y == p.Second.y )
		&& a.Curves.Zip( b.Curves ).All( c => c.First.Id == c.Second.Id
			&& c.First.GetType() == c.Second.GetType()
			&& c.First.Construction == c.Second.Construction )
		&& a.Constraints.Zip( b.Constraints ).All( c => c.First.Kind == c.Second.Kind
			&& c.First.PointA == c.Second.PointA && c.First.Value == c.Second.Value );

	static bool Refused( string text, out string message )
	{
		message = null;

		try
		{
			StudioDocument.Read( text );
			return false;
		}
		catch ( Exception e )
		{
			message = e.Message;
			return true;
		}
	}

	static float Volume( PolyMesh mesh )
	{
		var acc = 0f;

		foreach ( var f in mesh.Faces )
			acc += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		return acc / 3f;
	}
}
