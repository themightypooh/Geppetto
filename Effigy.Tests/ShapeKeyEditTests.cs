using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Sculpting into a shape key that is already finished (Shape Keys ▸ Edit key, `add … inkey=`):
/// the new work is added to the key's offsets, the body's base shape does not move, and it all
/// saves and reads back.
/// </summary>
public static class ShapeKeyEditTests
{
	const string Ball = "add Primitive name=Sac shape=Sphere radius=10 divisions=10\n";
	const string Key = "add ShapeKey name=Swell key=sac_swell bodies=Sac\n"
		+ "add Stroke name=Top bodies=Sac brush=Inflate radius=6 strength=1 points=0,0,10;0,0,9\n"
		+ "add ShapeKeyEnd\n";
	const string Second = "add Stroke name=Side bodies=Sac brush=Inflate radius=4 strength=1 points=10,0,0;9,0,0\n";

	public static void Run()
	{
		Section( "shape keys: edit a finished key" );
		TestReopenAndAdd();
		TestScriptInKey();
		TestRefusals();
		TestRoundTrip();
	}

	static PartStudio Build( string script )
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, script );
		Check( "(setup) the script runs", r.Ok, r.ToString() );
		return studio;
	}

	/// <summary>The second stroke as a feature, built the way a script's `add` builds one.</summary>
	static Feature SideStroke( PartStudio studio )
	{
		var stroke = BuildScript.CreateFeature( "Stroke" );
		var ok = BuildScript.Configure( studio, stroke, new System.Collections.Generic.Dictionary<string, string>
		{
			["name"] = "Side", ["bodies"] = "Sac", ["brush"] = "Inflate", ["radius"] = "4", ["strength"] = "1", ["points"] = "10,0,0;9,0,0",
		}, out var why );
		Check( "(setup) the stroke is configured", ok, why );
		return stroke;
	}

	static MeshMorph Morph( PartStudio studio ) => studio.ToMesh().ValidMorphs.Single( m => m.Name == "sac_swell" );

	static void TestReopenAndAdd()
	{
		var studio = Build( Ball + Key + "add Transform name=After bodies=Sac\n" );
		var start = studio.Features.OfType<ShapeKeyFeature>().Single();
		var basePositions = studio.ToMesh().Positions.ToArray();
		var before = Morph( studio ).Deltas.ToArray();

		Check( "a key built line by line (open, then ended) keeps no stale 'still open' warning",
			start.Warning is null && studio.Rebuild().Warnings.Count == 0, start.Warning );
		Check( "a finished key is not being edited", ShapeKeyEdit.Editing( studio ) is null );
		Check( "Reopen moves the bar to just above End Shape Key",
			ShapeKeyEdit.Reopen( studio, start, out var why ) && studio.RollbackIndex == studio.Features.IndexOf( studio.Features.OfType<ShapeKeyEndFeature>().Single() ), why );
		Check( "and the key reads as being edited", ShapeKeyEdit.Editing( studio )?.Start == start );

		studio.Rebuild();
		var shown = studio.Bodies[0].Mesh.Positions;
		var showsKey = shown.Select( ( p, i ) => (p - (basePositions[i] + before[i])).Length ).Max();
		Check( "while editing, the model shows the key fully on (sculpt on what you are changing)", showsKey < 1e-4f, $"{showsKey:0.#####}" );
		Check( "and the key is not warned about as left open: its End is just past the bar", start.Warning is null, start.Warning );

		// What the editor's Sculpt does at the bar: a feature inserted there lands inside the key.
		var stroke = SideStroke( studio );
		Check( "a feature added while editing goes in above End", ShapeKeyEdit.AddToKey( studio, start, stroke, out why ), why );
		Check( "and the bar moves with it, so it is evaluated", studio.RollbackIndex == studio.Features.IndexOf( stroke ) + 1 );

		Check( "Close ends the edit and puts the bar back at the end", ShapeKeyEdit.Close( studio ) == start && studio.RollbackIndex == int.MaxValue );
		studio.Rebuild();

		var after = Morph( studio ).Deltas;
		var top = Enumerable.Range( 0, after.Length ).OrderByDescending( i => basePositions[i].z ).First();
		var east = Enumerable.Range( 0, after.Length ).OrderByDescending( i => basePositions[i].x ).First();

		Check( "the key's offsets changed", after.Zip( before ).Max( d => (d.First - d.Second).Length ) > 0.2f );
		Check( "the new stroke's swelling is in the key", after[east].Length > 0.2f && before[east].Length < 1e-4f, $"{before[east].Length:0.###} → {after[east].Length:0.###}" );
		Check( "and the first stroke's is still there", after[top].Length > 0.2f, $"{after[top].Length:0.###}" );

		var drift = studio.ToMesh().Positions.Zip( basePositions ).Max( p => (p.First - p.Second).Length );
		Check( "the body's base shape did not move", drift < 1e-4f, $"{drift:0.#####}" );
		Check( "and the features after the key still build", studio.Features.All( f => f.Error is null ) );
	}

	static void TestScriptInKey()
	{
		var viaScript = Build( Ball + Key + Second.TrimEnd( '\n' ) + " inkey=sac_swell\n" );
		var stroke = viaScript.Features.OfType<StrokeFeature>().Single( s => s.Name == "Side" );
		var end = viaScript.Features.OfType<ShapeKeyEndFeature>().Single();

		Check( "`add … inkey=sac_swell` puts the feature above that key's End", viaScript.Features.IndexOf( stroke ) == viaScript.Features.IndexOf( end ) - 1 );

		var written = Build( Ball + "add ShapeKey name=Swell key=sac_swell bodies=Sac\n"
			+ "add Stroke name=Top bodies=Sac brush=Inflate radius=6 strength=1 points=0,0,10;0,0,9\n" + Second + "add ShapeKeyEnd\n" );
		var a = Morph( viaScript ).Deltas;
		var b = Morph( written ).Deltas;
		Check( "and makes the same key as writing it there in the first place", a.Zip( b ).Max( d => (d.First - d.Second).Length ) < 1e-4f );

		var missing = BuildScript.Run( Build( Ball + Key ), Second.TrimEnd( '\n' ) + " inkey=nope\n" );
		Check( "an unknown key is refused, naming the keys there are", !missing.Ok && missing.Error.Contains( "sac_swell" ), missing.ToString() );
	}

	static void TestRefusals()
	{
		var open = Build( Ball + "add ShapeKey key=open bodies=Sac\n" + Second );
		var start = open.Features.OfType<ShapeKeyFeature>().Single();
		Check( "a key still recording cannot be reopened, and says why", !ShapeKeyEdit.Reopen( open, start, out var why ) && why.Contains( "still recording" ), why );

		var two = Build( Ball + Key + "add ShapeKey key=b bodies=Sac\n" + Second );
		var first = two.Features.OfType<ShapeKeyFeature>().First();
		Check( "a finished key cannot be reopened while another records", !ShapeKeyEdit.Reopen( two, first, out why ) && why.Contains( "'b'" ), why );

		var done = Build( Ball + Key );
		var key = done.Features.OfType<ShapeKeyFeature>().Single();
		Check( "a key cannot hold another Shape Key", !ShapeKeyEdit.AddToKey( done, key, new ShapeKeyFeature(), out why ) && why.Length > 0, why );
		Check( "Close with nothing being edited changes nothing", ShapeKeyEdit.Close( done ) is null && done.RollbackIndex == int.MaxValue );
	}

	static void TestRoundTrip()
	{
		var studio = Build( Ball + Key );
		var start = studio.Features.OfType<ShapeKeyFeature>().Single();
		ShapeKeyEdit.Reopen( studio, start, out _ );
		ShapeKeyEdit.AddToKey( studio, start, SideStroke( studio ), out _ );
		studio.Rebuild();

		var reread = StudioDocument.Read( StudioDocument.Write( studio ) );
		Check( "a document saved mid-edit reopens mid-edit", ShapeKeyEdit.Editing( reread )?.Start.KeyName == "sac_swell" );

		ShapeKeyEdit.Close( studio );
		studio.Rebuild();
		ShapeKeyEdit.Close( reread );
		reread.Rebuild();

		var a = Morph( studio ).Deltas;
		var b = Morph( reread ).Deltas;
		Check( "and the finished key reads back identical", a.Length == b.Length && a.Zip( b ).Max( d => (d.First - d.Second).Length ) < 1e-5f );
	}
}
