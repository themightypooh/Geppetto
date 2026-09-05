using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The paint pipeline, end to end, without s&amp;box.
///
/// WHY THIS EXISTS SEPARATELY FROM PaintReplayTests. Those test the DAB — one brush, one hand-built
/// mesh, the colour maths. Nothing tested the CHAIN: a PaintFeature sitting in a real feature tree,
/// above a real primitive, surviving a rebuild, surviving an UPSTREAM EDIT, surviving a save and a
/// reload, and reaching the DMX with its colours attached. That chain is the actual product claim —
/// "paint follows the part through later edits instead of smearing" — and it was the one thing no
/// test made. A dab that works on a fixture and a paint feature that works in a document are two
/// different statements.
///
/// IT ALSO RENDERS THE RESULT. The remaining Known Issue is that nobody has LOOKED at a painted
/// model; the preview sheet here is the closest a headless run can get, and it is close enough to
/// answer "did the brush go where I aimed it" and "is a bare box really too coarse to paint". What
/// it cannot answer is how s&amp;box's own shader composites the vertex colours — that needs the
/// editor open, and this deliberately does not pretend otherwise.
///
/// Invoked as: Effigy.Tests.exe --paint [outDir]
/// </summary>
public static class PaintGen
{
	static int _passed;
	static int _failed;

	// A 2-unit box centred on the origin, so the top face sits at z = +1 and a brush aimed at
	// (x, y, 1) with normal +Z lands on it. Radius is in the same units — see the memory of every
	// dab that missed because it was aimed at a point in space rather than at the surface.
	const float Half = 1f;

	public static int Run( string outDir )
	{
		Directory.CreateDirectory( outDir );

		Console.WriteLine( "painting a model, headless" );
		Console.WriteLine();

		// --- the part ------------------------------------------------------------------------

		var studio = new PartStudio();
		studio.MaterialNames[0] = "models/effigy/paint_base.vmat";

		var box = studio.Add( new PrimitiveFeature() );
		box.Name = "block";
		box.Shape.Index = 0; // Box
		box.SizeX.Value = Half * 2f;
		box.SizeY.Value = Half * 2f;
		box.SizeZ.Value = Half * 2f;
		box.Material.Value = 0;

		// SUBDIVIDE BEFORE PAINTING, which is the whole of the Known Issue said as a build step.
		// Vertex colours live one per vertex, so the eight corners of a bare box can express eight
		// colours and no more. Three levels is 6 -> 1536 faces, which is enough for a brush to read
		// as a brush.
		var dense = studio.Add( new SubdivideFeature() );
		dense.Name = "density";
		dense.Bodies.BodyIds.Add( box.Id + "b0" );
		dense.Levels.Value = 3;

		// REBUILD BEFORE PAINTING, because the strokes have to be aimed at the surface that
		// actually exists. Catmull-Clark pulls a box a long way toward a sphere - the top face
		// stops being at z = 1 - and a stroke aimed at where the box USED to be reaches nothing.
		// This is the same order the editor works in: the mesh exists, then a ray off the cursor
		// finds it. Aiming at remembered coordinates is the trap.
		var pre = studio.Rebuild();

		if ( pre.HasErrors )
		{
			foreach ( var e in pre.Errors )
				Console.WriteLine( "  ERROR " + e );

			return 1;
		}

		var canvas = studio.ToMesh();

		var paint = studio.Add( new PaintFeature() );
		paint.Name = "paint";
		paint.Bodies.BodyIds.Add( box.Id + "b0" );

		// Three strokes in three colours, laid across the top. Adjacent stripes overlap
		// deliberately, so the source-over blend has something to prove.
		paint.AddStroke( Stripe( canvas, 1f, 0.25f, 0.15f, y: -0.42f ) );
		paint.AddStroke( Stripe( canvas, 0.15f, 0.85f, 0.35f, y: 0f ) );
		paint.AddStroke( Stripe( canvas, 0.25f, 0.45f, 1f, y: 0.42f ) );

		var report = studio.Rebuild();

		if ( report.HasErrors )
		{
			foreach ( var e in report.Errors )
				Console.WriteLine( "  ERROR " + e );

			return 1;
		}

		var mesh = studio.ToMesh();
		Console.WriteLine( $"  {mesh.VertexCount} verts, {mesh.FaceCount} faces" );
		Console.WriteLine();

		// --- what the chain has to prove ---------------------------------------------------

		Check( "the rebuilt mesh carries vertex colours", mesh.HasVertexColors );

		var painted = CountPainted( mesh );
		Check( "the brush painted some of it", painted > 0, $"{painted} of {mesh.VertexCount} verts" );
		Check( "and not all of it - three stripes are not a coat of paint",
			painted < mesh.VertexCount, $"{painted} of {mesh.VertexCount} verts" );

		// The underside must be clean. It is the same test the dab already passes on a fixture,
		// asked here of a real solid where the far side is a whole face away rather than a
		// double-sided quad.
		var underside = mesh.Positions
			.Select( ( p, i ) => (p, i) )
			.Where( t => t.p.z < -Half + 0.01f )
			.Count( t => mesh.VertexColors[t.i].w > 0.01f );

		Check( "the bottom of the box is unpainted", underside == 0, $"{underside} painted verts" );

		// THE HEADLINE CLAIM. Change the box UNDER the paint and rebuild: the strokes are in object
		// space and replayed, so they must still be there. If paint were baked into the mesh this
		// is where it would smear or vanish.
		box.SizeX.Value = Half * 3f;

		var report2 = studio.Rebuild();
		Check( "the part still rebuilds after an upstream edit", !report2.HasErrors );

		var edited = studio.ToMesh();
		var paintedAfter = CountPainted( edited );

		Check( "paint survives an edit to the feature below it", paintedAfter > 0,
			$"{paintedAfter} verts still painted" );

		box.SizeX.Value = Half * 2f;
		studio.Rebuild();
		mesh = studio.ToMesh();

		// --- save, reload, repaint ------------------------------------------------------------

		var docPath = Path.Combine( outDir, "painted.effigy" );
		StudioDocument.WriteFile( studio, docPath );

		var reloaded = StudioDocument.ReadFile( docPath );
		var reloadedPaint = reloaded.Features.OfType<PaintFeature>().FirstOrDefault();

		Check( "the .effigy file carries the paint feature", reloadedPaint is not null );
		Check( "with all three strokes", reloadedPaint?.Strokes?.Count == 3,
			$"{reloadedPaint?.Strokes?.Count ?? 0} strokes" );

		reloaded.Rebuild();
		var reloadedMesh = reloaded.ToMesh();

		Check( "and reopening the file paints the same model",
			reloadedMesh.HasVertexColors && SameColors( mesh, reloadedMesh ),
			$"{CountPainted( reloadedMesh )} verts painted" );

		// --- the export the engine actually reads ----------------------------------------------

		var obj = Path.Combine( outDir, "painted.obj" );
		var dmx = Path.Combine( outDir, "painted.dmx" );

		ObjWriter.WriteFile( mesh, obj, "painted" );
		DmxWriter.WriteFile( mesh, dmx, modelName: "painted", materialName: studio.NameForSlot );

		var dmxText = File.ReadAllText( dmx );

		// The field names were read out of the compiler's own binary, so the thing worth asserting
		// is that they are PRESENT and that the stream is the right length — not that the engine
		// likes them, which only the engine can say.
		Check( "the DMX declares a vertex colour stream", dmxText.Contains( "VertexPaintBlendParams" )
			|| dmxText.Contains( "$color" ) || dmxText.Contains( "color$0" ),
			FirstColorField( dmxText ) );

		Console.WriteLine();
		Console.WriteLine( "  OBJ    " + obj );
		Console.WriteLine( "  DMX    " + dmx );
		Console.WriteLine( "  EFFIGY " + docPath );

		// --- the picture ------------------------------------------------------------------------

		// A COARSE MODEL BESIDE THE DENSE ONE, because the Known Issue is a claim about mesh
		// density and the only honest way to show it is both. One level rather than none: at
		// zero the box's eight corners are all further from the stroke than the brush is wide,
		// so the same brush paints literally nothing and the tile is a picture of an unpainted
		// box, which reads as a broken renderer rather than as the point being made.
		var coarse = CoarsePainted( 1 );

		PngPreview.WriteSheet( new[]
		{
			new PngPreview.Tile( mesh, "box + Subdivide x3" ),
			new PngPreview.Tile( coarse, "same brush, Subdivide x1" ),
			new PngPreview.Tile( mesh, "wireframe", wireframe: true ),
			new PngPreview.Tile( reloadedMesh, "reopened from .effigy" ),
		}, Path.Combine( outDir, "painted_preview.png" ), columns: 2, tileSize: 460 );

		Console.WriteLine( "  PNG    " + Path.Combine( outDir, "painted_preview.png" ) );
		Console.WriteLine();
		Console.WriteLine( "------------------------------------------------------------" );
		Console.WriteLine( $"  {_passed} passed, {_failed} failed" );
		Console.WriteLine( "------------------------------------------------------------" );
		Console.WriteLine();
		Console.WriteLine( "  NOT PROVEN HERE: how s&box composites these colours over the" );
		Console.WriteLine( "  material. That needs the editor open - compile painted.dmx and look." );

		return _failed == 0 ? 0 : 1;
	}

	// --- helpers ---------------------------------------------------------------------------------

	/// <summary>
	/// One stroke drawn across the top of whatever mesh it is given, left to right at a fixed y.
	///
	/// EVERY POINT IS A RAYCAST, straight down from well above the part, exactly as the editor
	/// gets its points from a ray off the cursor. That is not ceremony: a dab aims at a point in
	/// SPACE and colours the vertices within its radius of that point, so a stroke aimed at
	/// remembered coordinates lands in mid-air the moment anything upstream moves the surface —
	/// which is precisely what a Subdivide above a box does. A sample that hits nothing is
	/// dropped rather than guessed at.
	/// </summary>
	static PaintStroke Stripe( PolyMesh mesh, float r, float g, float b, float y )
	{
		var stroke = new PaintStroke { R = r, G = g, B = b, A = 1f, Radius = 0.34f, Strength = 1f };

		for ( var i = 0; i <= 24; i++ )
		{
			var x = -Half * 0.75f + (Half * 1.5f) * (i / 24f);
			var hit = MeshRaycast.Raycast( mesh, new Vec3( x, y, Half * 10f ), new Vec3( 0, 0, -1 ) );

			if ( hit is { } h )
				stroke.Path.Add( new PaintStrokePoint( h.Point, h.Normal ) );
		}

		return stroke;
	}

	/// <summary>The same strokes replayed onto an unsubdivided box — the Known Issue, drawn.</summary>
	static PolyMesh CoarsePainted( int levels )
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.Shape.Index = 0;
		box.SizeX.Value = Half * 2f;
		box.SizeY.Value = Half * 2f;
		box.SizeZ.Value = Half * 2f;

		if ( levels > 0 )
		{
			var dense = studio.Add( new SubdivideFeature() );
			dense.Bodies.BodyIds.Add( box.Id + "b0" );
			dense.Levels.Value = levels;
		}

		studio.Rebuild();
		var bare = studio.ToMesh();

		var paint = studio.Add( new PaintFeature() );
		paint.Bodies.BodyIds.Add( box.Id + "b0" );

		// RE-AIMED at the box rather than replayed from the dense model's strokes, for the same
		// reason as above — the two surfaces are not in the same place, and a comparison of
		// density has to be a comparison of the same brush landing, not of one brush missing.
		foreach ( var c in new[] { (1f, 0.25f, 0.15f, -0.42f), (0.15f, 0.85f, 0.35f, 0f), (0.25f, 0.45f, 1f, 0.42f) } )
			paint.AddStroke( Stripe( bare, c.Item1, c.Item2, c.Item3, c.Item4 ) );

		studio.Rebuild();
		return studio.ToMesh();
	}

	static int CountPainted( PolyMesh mesh ) =>
		!mesh.HasVertexColors ? 0 : mesh.VertexColors.Count( c => c.w > 0.01f );

	static bool SameColors( PolyMesh a, PolyMesh b )
	{
		if ( !a.HasVertexColors || !b.HasVertexColors ) return false;
		if ( a.VertexColors.Length != b.VertexColors.Length ) return false;

		for ( var i = 0; i < a.VertexColors.Length; i++ )
		{
			var x = a.VertexColors[i];
			var y = b.VertexColors[i];

			if ( MathF.Abs( x.x - y.x ) > 1e-4f || MathF.Abs( x.y - y.y ) > 1e-4f
				|| MathF.Abs( x.z - y.z ) > 1e-4f || MathF.Abs( x.w - y.w ) > 1e-4f )
				return false;
		}

		return true;
	}

	/// <summary>Whatever the DMX calls its colour field, quoted back — so a failure says what the
	/// writer actually emitted rather than only that the guess was wrong.</summary>
	static string FirstColorField( string dmx )
	{
		var line = dmx.Split( '\n' ).FirstOrDefault( l => l.Contains( "color", StringComparison.OrdinalIgnoreCase ) );
		return line?.Trim() ?? "no line mentioning colour";
	}

	static void Check( string what, bool ok, string detail = null )
	{
		if ( ok ) _passed++; else _failed++;

		var mark = ok ? "  ok  " : "  FAIL";
		Console.WriteLine( detail is null ? $"{mark}  {what}" : $"{mark}  {what}  ({detail})" );
	}
}
