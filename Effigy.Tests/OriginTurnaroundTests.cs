using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Render;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Usability plan step 7: Set Origin (saved, and what export subtracts), Render Turnaround (the
/// shaded sheet the editor saves), and the symmetry reading the Model check badge shows. Each is
/// checked by number — where the origin lands, what the files say, which pixels the model covers.
/// </summary>
public static class OriginTurnaroundTests
{
	public static void Run()
	{
		Section( "set origin: where it lands" );
		TestOriginTargets();
		TestOriginFromSelection();
		TestOriginRefusals();

		Section( "set origin: saved with the document" );
		TestOriginRoundTrip();
		TestOriginOldBytesUnchanged();
		TestOriginHandEditedLine();

		Section( "set origin: the script's pivot writes it, export reads it" );
		TestScriptPivotSaved();
		TestScriptPivotModel();
		TestScriptPivotRefusals();

		Section( "symmetry badge reading" );
		TestSymmetryPlanes();
		TestSymmetryMatchesBruteForce();
		TestSymmetrySampled();

		Section( "render turnaround" );
		TestTurnaroundRender();
		TestTurnaroundViewsDiffer();
		TestTurnaroundWrite();
		TestTurnaroundRefusals();
		TestTurnaroundScript();
	}

	const string Box = "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=0,0,5\n";

	static PartStudio Studio( string script )
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, script );
		if ( !r.Ok ) throw new InvalidOperationException( r.ToString() );
		return studio;
	}

	static bool Near( Vec3 a, Vec3 b, float tol = 1e-3f ) => (a - b).Length < tol;

	static string Dir( string what )
	{
		var dir = Path.Combine( Path.GetTempPath(), "effigy-origin-turnaround", what + "-" + Guid.NewGuid().ToString( "N" )[..6] );
		Directory.CreateDirectory( dir );
		return dir;
	}

	static void TestOriginTargets()
	{
		var studio = Studio( "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=20,4,5\n" );

		var ok = StudioOrigin.SetModel( studio, OriginTarget.Geometry, null, default, out var said );
		Check( "Origin to Geometry puts it at the middle of the model", ok && Near( studio.Origin, new Vec3( 20, 4, 5 ) ), $"{studio.Origin} {said}" );
		Check( "and says where it went and where it was", said.Contains( "(20, 4, 5)" ) && said.Contains( "was (0, 0, 0)" ), said );

		StudioOrigin.SetModel( studio, OriginTarget.Bottom, null, default, out _ );
		Check( "Origin to Bottom puts it under the middle, on the underside", Near( studio.Origin, new Vec3( 20, 4, 0 ) ), $"{studio.Origin}" );

		StudioOrigin.SetModel( studio, OriginTarget.Point, null, new Vec3( 1, 2, 3 ), out _ );
		Check( "Origin to a clicked point puts it at that point", Near( studio.Origin, new Vec3( 1, 2, 3 ) ), $"{studio.Origin}" );

		StudioOrigin.SetModel( studio, OriginTarget.World, null, default, out said );
		Check( "Origin to World Centre puts it back at zero", Near( studio.Origin, Vec3.Zero ) && !StudioOrigin.HasModelOrigin( studio ), said );

		var before = studio.Bodies[0].Mesh.Positions.ToList();
		StudioOrigin.SetModel( studio, OriginTarget.Geometry, null, default, out _ );
		studio.Rebuild();
		Check( "moving the origin moves no geometry", studio.Bodies[0].Mesh.Positions.SequenceEqual( before ) );
	}

	static void TestOriginFromSelection()
	{
		var studio = Studio( Box + "add Primitive name=Far shape=Box sizex=4 sizey=4 sizez=4 position=30,0,2\n" );
		var far = ModelMeasure.FindBody( studio, "Far" );

		StudioOrigin.SetModel( studio, OriginTarget.Geometry, new[] { far.Id }, default, out var said );
		Check( "with a body selected, Origin to Geometry measures that body", Near( studio.Origin, new Vec3( 30, 0, 2 ) ), $"{studio.Origin}" );
		Check( "and names it", said.Contains( "Far" ), said );

		StudioOrigin.SetModel( studio, OriginTarget.Geometry, null, default, out _ );
		Check( "with nothing selected it measures the whole model", Near( studio.Origin, new Vec3( 13.5f, 0, 5 ) ), $"{studio.Origin}" );

		far.Visible = false;
		StudioOrigin.SetModel( studio, OriginTarget.Geometry, null, default, out _ );
		Check( "a hidden body does not count (it does not export)", Near( studio.Origin, new Vec3( 0, 0, 5 ) ), $"{studio.Origin}" );

		var ok = StudioOrigin.SetBody( studio, far.Id, OriginTarget.Bottom, default, out said );
		Check( "a body can have its own origin", ok && StudioOrigin.TryBody( studio, far, out var own ) && Near( own, new Vec3( 30, 0, 0 ) ), said );
	}

	static void TestOriginRefusals()
	{
		var empty = new PartStudio();
		var ok = StudioOrigin.SetModel( empty, OriginTarget.Geometry, null, default, out var said );
		Check( "Origin to Geometry on an empty model refuses", !ok && empty.Origin.Length == 0f );
		Check( "and says to add a body", said.Contains( "add a body" ), said );

		var studio = Studio( Box );
		ok = StudioOrigin.SetModel( studio, OriginTarget.Point, null, new Vec3( float.NaN, 0, 0 ), out said );
		Check( "a point that is not a number is refused", !ok && studio.Origin.Length == 0f, said );

		ok = StudioOrigin.SetBody( studio, "no-such-body", OriginTarget.Geometry, default, out said );
		Check( "a body origin for a missing body is refused, by name", !ok && said.Contains( "no-such-body" ), said );

		Check( "Origin to World Centre works with nothing to measure", StudioOrigin.SetModel( empty, OriginTarget.World, null, default, out _ ) );
	}

	static void TestOriginRoundTrip()
	{
		var studio = Studio( Box );
		var box = studio.Bodies[0];
		studio.Origin = new Vec3( 1.5f, -2.25f, 3f );
		studio.BodyOrigins[box.Id] = new Vec3( 0, 0, -0.5f );
		studio.BodyOrigins["gone"] = new Vec3( 9, 8, 7 );

		var text = StudioDocument.Write( studio );
		var back = StudioDocument.Read( text );

		Check( "the model origin survives a save", Near( back.Origin, studio.Origin ), $"{back.Origin}" );
		Check( "each body origin survives a save", back.BodyOrigins.TryGetValue( box.Id, out var o ) && Near( o, new Vec3( 0, 0, -0.5f ) ), string.Join( ";", back.BodyOrigins ) );
		Check( "an origin for a body that went away is kept, not lost", back.BodyOrigins.ContainsKey( "gone" ) );
		Check( "two saves of the same document are the same bytes", StudioDocument.Write( back ) == text );
	}

	static void TestOriginOldBytesUnchanged()
	{
		var text = StudioDocument.Write( Studio( Box ) );
		Check( "a document with no origins set writes no origin lines", !text.Contains( "bodyorigin" ) && !text.Contains( "\norigin " ), text.Split( '\n' ).Take( 4 ).Aggregate( ( a, b ) => a + " | " + b ) );
	}

	static void TestOriginHandEditedLine()
	{
		var text = StudioDocument.Write( Studio( Box ) ).Replace( "rollback ", "bodyorigin abc 1 2\nrollback " );
		PartStudio back = null;
		var threw = false;
		try { back = StudioDocument.Read( text ); } catch ( Exception ) { threw = true; }
		Check( "a bodyorigin line short a coordinate is dropped, not fatal", !threw && back.BodyOrigins.Count == 0 );
	}

	static string Layout( string dir ) => File.ReadAllLines( Path.Combine( dir, "layout.txt" ) ).Skip( 1 ).First();

	static void TestScriptPivotSaved()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, Box + "pivot Box @Box.top" );
		var box = studio.Bodies[0];
		Check( "pivot Body writes the body's saved origin", r.Ok && StudioOrigin.TryBody( studio, box, out var o ) && Near( o, new Vec3( 0, 0, 10 ) ), r.ToString() );

		// A later run with no pivot line — a reopened document — exports about the saved origin.
		var reopened = StudioDocument.Read( StudioDocument.Write( studio ) );
		reopened.Rebuild();
		var dir = Dir( "split" );
		r = BuildScript.Run( reopened, $"export {dir.Replace( '\\', '/' )} split=1 assets=/" );
		var row = r.Ok ? Layout( dir ).Split( '\t' ) : Array.Empty<string>();
		Check( "a reopened document's split export uses the saved body origin", r.Ok && row.Length > 4 && row[2] == "0" && row[3] == "0" && row[4] == "10", r.Ok ? Layout( dir ) : r.ToString() );

		var obj = File.ReadAllLines( Directory.GetFiles( dir, "*.obj" ).First() ).Where( l => l.StartsWith( "v " ) )
			.Select( l => float.Parse( l.Split( ' ', StringSplitOptions.RemoveEmptyEntries )[3], System.Globalization.CultureInfo.InvariantCulture ) ).ToList();
		Check( "and the exported mesh hangs below that origin", obj.Count > 0 && MathF.Abs( obj.Max() ) < 1e-3f && MathF.Abs( obj.Min() + 10f ) < 1e-3f, $"z {obj.DefaultIfEmpty().Min()}..{obj.DefaultIfEmpty().Max()}" );
	}

	static void TestScriptPivotModel()
	{
		var studio = new PartStudio();
		var dir = Dir( "whole" );
		var r = BuildScript.Run( studio, Box + $"pivot model 1,2,3\nexport {dir.Replace( '\\', '/' )} assets=/ name=whole" );
		Check( "pivot model sets the model's origin", r.Ok && Near( studio.Origin, new Vec3( 1, 2, 3 ) ), r.ToString() );
		var row = r.Ok ? Layout( dir ).Split( '\t' ) : Array.Empty<string>();
		Check( "and the whole-model export uses it", row.Length > 4 && row[2] == "1" && row[3] == "2" && row[4] == "3", r.Ok ? Layout( dir ) : "" );

		// The editor's Set Origin writes the same field, so a script export of a document the editor
		// gave an origin lands the same way the editor's own export does.
		var edited = Studio( Box );
		StudioOrigin.SetModel( edited, OriginTarget.Geometry, null, default, out _ );
		var dir2 = Dir( "edited" );
		r = BuildScript.Run( edited, $"export {dir2.Replace( '\\', '/' )} assets=/ name=edited" );
		row = r.Ok ? Layout( dir2 ).Split( '\t' ) : Array.Empty<string>();
		Check( "a document's saved origin is what script export subtracts", row.Length > 4 && row[4] == "5", r.Ok ? Layout( dir2 ) : r.ToString() );

		var plain = Studio( Box );
		var dir3 = Dir( "plain" );
		r = BuildScript.Run( plain, $"export {dir3.Replace( '\\', '/' )} assets=/ name=plain" );
		row = r.Ok ? Layout( dir3 ).Split( '\t' ) : Array.Empty<string>();
		Check( "with no origin set export still stands the model on its bottom centre", row.Length > 4 && row[2] == "0" && row[4] == "0", r.Ok ? Layout( dir3 ) : r.ToString() );
	}

	static void TestScriptPivotRefusals()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, Box + "pivot Nope @Box.top" );
		Check( "pivot on a missing body fails, naming it and the way out", !r.Ok && r.Error.Contains( "Nope" ) && r.Error.Contains( "model" ), r.ToString() );

		r = BuildScript.Run( studio, "pivot model" );
		Check( "pivot with no point fails with its usage", !r.Ok && r.Error.Contains( "pivot model @ground" ), r.ToString() );
	}

	static void TestSymmetryPlanes()
	{
		var centred = Studio( Box ).Bodies[0].Mesh;
		var reading = SymmetryCheck.Measure( centred );
		Check( "a box on the centre reads symmetric", reading.Level == 0 && reading.Error < 1e-4f, reading.Describe() );

		var alongX = Studio( "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=12,0,5\n" ).Bodies[0].Mesh;
		reading = SymmetryCheck.Measure( alongX );
		Check( "a box moved along X is symmetric across Y, and says so", reading.Level == 0 && reading.Axis == 'Y' && reading.ErrorX > SymmetryCheck.Lopsided, reading.Describe() );
		Check( "its description names the plane and the editor's own mirror axis", reading.Describe().Contains( "across Y = 0" ), reading.Describe() );

		var off = Studio( "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=3,3,5\n" ).Bodies[0].Mesh;
		reading = SymmetryCheck.Measure( off );
		Check( "a box off both planes reads lopsided", reading.Level == 2 && reading.Short.StartsWith( "Off " ), $"{reading.Short} {reading.Describe()}" );
		Check( "the script measure is the Y plane of the same reading", MathF.Abs( ModelMeasure.SymmetryError( off ) - SymmetryCheck.Error( off, 1 ) ) < 1e-7f );
	}

	static void TestSymmetryMatchesBruteForce()
	{
		// Nearly centred, so every mirrored vertex has its partner within a grid cell and the grid's
		// answer must be the brute-force one exactly.
		var mesh = Studio( "add Primitive name=Ball shape=Sphere radius=5 divisions=10 position=0.12,-0.07,5\n" ).Bodies[0].Mesh;
		var (min, max) = ModelMeasure.Bounds( mesh );

		float Brute( int axis )
		{
			var width = axis == 0 ? max.x - min.x : max.y - min.y;
			var total = 0f;
			foreach ( var p in mesh.Positions )
			{
				var m = axis == 0 ? new Vec3( -p.x, p.y, p.z ) : new Vec3( p.x, -p.y, p.z );
				total += mesh.Positions.Min( q => (q - m).Length );
			}
			return total / mesh.Positions.Count / width;
		}

		var bx = Brute( 0 ); var by = Brute( 1 );
		var gx = SymmetryCheck.Error( mesh, 0 ); var gy = SymmetryCheck.Error( mesh, 1 );
		Check( "the X-plane error matches a brute-force nearest-vertex measure", MathF.Abs( gx - bx ) < 1e-5f && bx > 0f, $"grid {gx:0.######} brute {bx:0.######}" );
		Check( "the Y-plane error matches a brute-force nearest-vertex measure", MathF.Abs( gy - by ) < 1e-5f && by > 0f, $"grid {gy:0.######} brute {by:0.######}" );
		Check( "the further off-centre plane reads further off", gx > gy, $"{gx} {gy}" );

		// A vertex with nothing near its mirror is charged two grid cells (1/12 of the width): the
		// script measure always capped it there, which is still well past lopsided.
		var away = Studio( "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=40,0,5\n" ).Bodies[0].Mesh;
		Check( "a body nowhere near its mirror reads the capped 1/12, past lopsided", MathF.Abs( SymmetryCheck.Error( away, 0 ) - 1f / 12f ) < 1e-4f && 1f / 12f > SymmetryCheck.Lopsided );
	}

	static void TestSymmetrySampled()
	{
		var mesh = Studio( "add Primitive name=Ball shape=Sphere radius=5 divisions=48 position=0.4,0,5\n" ).Bodies[0].Mesh;
		var full = SymmetryCheck.Error( mesh, 0 );
		var sampled = SymmetryCheck.Error( mesh, 0, 200 );
		Check( "sampling a dense mesh for the badge stays within a fifth of the full measure", MathF.Abs( sampled - full ) <= full * 0.2f + 1e-4f, $"full {full:0.####} sampled {sampled:0.####} of {mesh.Positions.Count} vertices" );
	}

	/// <summary>How many columns of the middle row differ from the row's left edge: the model's width on screen.</summary>
	static int Covered( Raster image )
	{
		var y = image.Height / 2;
		var edge = image.Pixels[y * image.Width];
		var count = 0;
		for ( var x = 0; x < image.Width; x++ )
		{
			var p = image.Pixels[y * image.Width + x];
			var d = Math.Abs( ((p >> 16) & 255) - ((edge >> 16) & 255) ) + Math.Abs( ((p >> 8) & 255) - ((edge >> 8) & 255) ) + Math.Abs( (p & 255) - (edge & 255) );
			if ( d > 24 ) count++;
		}
		return count;
	}

	static void TestTurnaroundRender()
	{
		var studio = Studio( Box );
		var r = Turnaround.Render( studio, new TurnaroundOptions { Size = 64, Supersample = 1 } );
		Check( "a turnaround renders", r.Ok, r.Error );
		Check( "front, quarter, side and back, in that order", r.Views.Select( v => v.View ).SequenceEqual( new[] { "front", "quarter", "side", "back" } ), string.Join( ",", r.Views.Select( v => v.View ) ) );
		Check( "each view is the size asked for", r.Views.All( v => v.Image.Width == 64 && v.Image.Height == 64 ) );
		Check( "the sheet lays them out in a row with gaps", r.Sheet.Width == 4 * 68 + 4 && r.Sheet.Height == 72, $"{r.Sheet.Width}x{r.Sheet.Height}" );
		Check( "the model is in the middle of the front view", Covered( r.Views[0].Image ) > 8, $"{Covered( r.Views[0].Image )} columns" );
	}

	static void TestTurnaroundViewsDiffer()
	{
		// Long in X, thin in Y: narrow from the front, wide from the side.
		var studio = Studio( "add Primitive name=Plank shape=Box sizex=30 sizey=4 sizez=6 position=0,0,3\n" );
		var r = Turnaround.Render( studio, new TurnaroundOptions { Size = 96, Supersample = 1 } );
		var front = Covered( r.Views[0].Image );
		var side = Covered( r.Views[2].Image );
		var back = Covered( r.Views[3].Image );
		Check( "the side view looks along the length the front view looks down", side > front * 2, $"front {front} side {side}" );
		Check( "front and back see about the same width", Math.Abs( front - back ) <= Math.Max( 3, front / 6 ), $"front {front} back {back}" );
	}

	static void TestTurnaroundWrite()
	{
		var studio = Studio( Box );
		var dir = Path.Combine( Dir( "write" ), "made-for-it" );
		var r = Turnaround.Write( studio, dir, "My Box", new TurnaroundOptions { Size = 48, Supersample = 1, Clay = false } );
		Check( "a turnaround writes into a new folder", r.Ok && Directory.Exists( dir ), r.Error );
		var names = r.Written.Select( Path.GetFileName ).ToList();
		Check( "the sheet first, then one picture per view, named after the model", names.SequenceEqual( new[] { "My_Box_turnaround.png", "My_Box_front.png", "My_Box_quarter.png", "My_Box_side.png", "My_Box_back.png" } ), string.Join( ",", names ) );
		var (w, h, _, _) = Raster.LoadPng( r.Written[0] );
		Check( "the saved sheet reads back at its size", w == 4 * 52 + 4 && h == 56, $"{w}x{h}" );
		Check( "material mode says what each body was drawn in", r.Log.Any( l => l.StartsWith( "Box: clay" ) ), string.Join( " | ", r.Log ) );
	}

	static void TestTurnaroundRefusals()
	{
		var r = Turnaround.Render( new PartStudio() );
		Check( "an empty model refuses, saying to add a body", !r.Ok && r.Error.Contains( "Add a body" ), r.Error );

		r = Turnaround.Render( Studio( Box ), new TurnaroundOptions { Views = new[] { "front", "underneath" } } );
		Check( "an unknown view is named", !r.Ok && r.Error.Contains( "underneath" ), r.Error );

		r = Turnaround.Render( Studio( Box ), new TurnaroundOptions { Size = 4 } );
		Check( "a size too small to render is refused", !r.Ok && r.Error.Contains( "16 to 4096" ), r.Error );

		var file = Path.Combine( Dir( "blocked" ), "a-file" );
		File.WriteAllText( file, "not a folder" );
		r = Turnaround.Write( Studio( Box ), file, "box", new TurnaroundOptions { Size = 32, Supersample = 1 } );
		Check( "a folder that can't be made says it couldn't save and what to do", !r.Ok && r.Error.Contains( "couldn't be saved" ) && r.Error.Contains( "Choose a folder" ), r.Error );

		r = Turnaround.Write( Studio( Box ), " ", "box" );
		Check( "no folder at all is refused", !r.Ok && r.Error.Contains( "Choose one" ), r.Error );
	}

	static void TestTurnaroundScript()
	{
		var dir = Dir( "script" );
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, Box + $"turnaround {dir.Replace( '\\', '/' )}/turn size=32 ss=1 name=crate views=front,side" );
		Check( "the script's turnaround writes the sheet and its views", r.Ok && r.Written.Count == 3 && File.Exists( Path.Combine( dir, "turn", "crate_turnaround.png" ) ), r.ToString() );

		r = BuildScript.Run( studio, $"turnaround {dir.Replace( '\\', '/' )} spin=1" );
		Check( "an unknown option is refused with the list", !r.Ok && r.Error.Contains( "spin" ) && r.Error.Contains( "shade" ), r.ToString() );

		r = BuildScript.Run( studio, $"turnaround {dir.Replace( '\\', '/' )} shade=wet" );
		Check( "an unknown look is refused", !r.Ok && r.Error.Contains( "shade=clay" ), r.ToString() );

		var shaded = Path.Combine( dir, "sheet.png" );
		r = BuildScript.Run( studio, $"render {shaded.Replace( '\\', '/' )} shade=clay views=2 size=32" );
		Check( "render shade= still renders through the shared scene", r.Ok && File.Exists( shaded ), r.ToString() );
	}
}
