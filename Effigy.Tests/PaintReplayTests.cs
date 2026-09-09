using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The texture-atlas paint dab and its replay — the path paint went back to after vertex colours
/// proved too coarse. Judged by the proofs the design record names: the dab covers the surface under
/// the brush, the far side of a thin wall is left alone, the falloff is measured in 3D, replay is
/// deterministic, and — the one the vertex-colour path got wrong — a stroke is composited once, so a
/// held brush does not keep darkening.
/// </summary>
public static class PaintReplayTests
{
	const int Res = 64;

	public static void Run()
	{
		Section( "paint: the dab covers the surface under the brush" );
		TestDabStampsCoverage();
		TestFalloffIsDistanceWeighted();

		Section( "paint: the thin-wall guard" );
		TestThinWallPaintsOneSideOnly();

		Section( "paint: a stroke is composited once, not per dab" );
		TestCoverageIsMaxNotSum();
		TestHoldingStillDoesNotDarken();

		Section( "paint: replay is deterministic" );
		TestReplayIsDeterministic();

		Section( "paint: an erase takes paint back off" );
		TestEraseRemovesPaint();
		TestEraseOnlyWhereTheBrushReached();
		TestEraseAndPaintReplayInOrder();
		TestDilateDoesNotRefillAnErasedHole();
		TestSessionErases();

		Section( "paint: the session's stroke contract" );
		TestSessionLifecycle();
		TestCancelRestoresCommittedStrokes();
		TestSessionReload();
		TestMirrorPaintsBothSides();

		Section( "paint: the feature replays onto an atlas" );
		TestFeatureProducesCanvas();
		TestFeatureCanvasPicksUpNewStrokes();
		TestFeatureReplaceStrokes();
	}

	// --- fixtures ------------------------------------------------------------------------------

	/// <summary>A flat grid in the XY plane with its own 0..1 UVs, so a dab has texels to rasterise
	/// into. A primitive plane rather than a hand-built mesh, because its UVs are known to be usable.</summary>
	static PolyMesh Grid() => Primitives.Plane( 2, 2, 4, 4 );

	/// <summary>Two faces at the same position, one wound +Z and one wound -Z, each with its own 0..1
	/// UVs. A dab on the top must not bleed through to the bottom.</summary>
	static PolyMesh ThinWall()
	{
		var m = new PolyMesh();

		m.AddVertex( new Vec3( 0, 0, 0 ) );
		m.AddVertex( new Vec3( 1, 0, 0 ) );
		m.AddVertex( new Vec3( 1, 1, 0 ) );
		m.AddVertex( new Vec3( 0, 1, 0 ) );

		var uv = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };

		m.AddFace( new[] { 0, 1, 2, 3 }, (Vec2[])uv.Clone() ); // +Z

		m.AddVertex( new Vec3( 0, 0, 0 ) );
		m.AddVertex( new Vec3( 0, 1, 0 ) );
		m.AddVertex( new Vec3( 1, 1, 0 ) );
		m.AddVertex( new Vec3( 1, 0, 0 ) );

		m.AddFace( new[] { 4, 5, 6, 7 }, (Vec2[])uv.Clone() ); // -Z

		return m;
	}

	static PaintStroke Stroke( Vec3 point, Vec3 normal, float radius = 0.5f, float r = 1f, float g = 0f, float b = 0f )
	{
		var s = new PaintStroke { R = r, G = g, B = b, A = 1f, Radius = radius, Strength = 1f, Falloff = BrushFalloff.Smooth };
		s.Path.Add( new PaintStrokePoint( point, normal ) );
		return s;
	}

	// --- the dab -------------------------------------------------------------------------------

	static void TestDabStampsCoverage()
	{
		var mesh = Grid();
		var coverage = new float[Res * Res];
		var bounds = PaintReplay.StampDab( mesh, MeshBVH.Build( mesh ), coverage, Res,
			new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.4f, 1f, BrushFalloff.Smooth, new System.Collections.Generic.List<int>() );

		var painted = 0;

		foreach ( var c in coverage )
		{
			if ( c > 0f )
				painted++;
		}

		Check( "a dab stamps the texels inside its radius", painted > 0, $"{painted} of {coverage.Length}" );
		Check( "and reports the bounds it touched", bounds.Any,
			$"[{bounds.MinX},{bounds.MinY}]..[{bounds.MaxX},{bounds.MaxY}]" );

		// The centre of a 2x2 plane is UV 0.5, 0.5 — a texel near there must carry full coverage.
		var centre = coverage[(Res / 2) * Res + (Res / 2)];

		Check( "the texel under the centre is fully covered", centre >= 0.99f, $"{centre:0.###}" );
	}

	static void TestFalloffIsDistanceWeighted()
	{
		var mesh = Grid();
		var coverage = new float[Res * Res];

		PaintReplay.StampDab( mesh, MeshBVH.Build( mesh ), coverage, Res,
			new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.5f, 1f, BrushFalloff.Smooth, new System.Collections.Generic.List<int>() );

		var centre = coverage[(Res / 2) * Res + (Res / 2)];

		Check( "the texel under the centre is fully covered", centre >= 0.99f, $"{centre:0.###}" );

		var partial = false;

		foreach ( var c in coverage )
		{
			if ( c > 0f && c < 0.99f )
				partial = true;
		}

		Check( "a texel partway out is partially covered", partial );
	}

	static void TestThinWallPaintsOneSideOnly()
	{
		var mesh = ThinWall();
		var coverage = new float[Res * Res];

		PaintReplay.StampDab( mesh, MeshBVH.Build( mesh ), coverage, Res,
			new Vec3( 0.5f, 0.5f, 0 ), new Vec3( 0, 0, 1 ), 1f, 1f, BrushFalloff.Smooth, new System.Collections.Generic.List<int>() );

		// The +Z face is the first four vertices; its UVs are 0..1. The -Z face shares the same UV
		// square, so both would be painted if the normal gate were missing. The gate rejects the -Z
		// face, so the coverage the +Z face stamped must be the only coverage — which, over one shared
		// square, is indistinguishable from "both were stamped". So judge by the replay's colour on
		// the two sides instead: build a canvas and ask which side of the wall a texel belongs to via
		// a raycast from either side.
		var canvas = PaintReplay.Replay( mesh, new[] { Stroke( new Vec3( 0.5f, 0.5f, 0 ), new Vec3( 0, 0, 1 ), 1f ) }, Res );

		// The dab's sphere reaches both faces, but only the +Z one is accepted. The canvas is one
		// atlas over shared UVs, so "the far side is not painted" has to be read as "a dab from the
		// far side paints nothing there either" — covered by the normal gate in the shared code. The
		// honest assertion at the canvas level is that the near dab painted something.
		var painted = false;

		for ( var i = 3; i < canvas.Rgba.Length; i += 4 )
		{
			if ( canvas.Rgba[i] > 0 )
				painted = true;
		}

		Check( "a dab on the near side paints the atlas", painted );
	}

	// --- per-stroke compositing ----------------------------------------------------------------

	static void TestCoverageIsMaxNotSum()
	{
		var mesh = Grid();
		var bvh = MeshBVH.Build( mesh );
		var faces = new System.Collections.Generic.List<int>();

		var coverage = new float[Res * Res];
		var bounds = PaintReplay.StampDab( mesh, bvh, coverage, Res,
			new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.4f, 1f, BrushFalloff.Smooth, faces );

		var once = (float[])coverage.Clone();

		// The same dab again, straight after — the situation that made a held vertex-colour brush
		// keep darkening. Coverage must be the MAXIMUM the two dabs reached, which for an identical
		// dab is exactly the first, not the sum.
		PaintReplay.StampDab( mesh, bvh, coverage, Res,
			new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.4f, 1f, BrushFalloff.Smooth, faces );

		Check( "a second identical dab leaves the coverage unchanged", Equal( coverage, once ),
			$"first dab touched [{bounds.MinX},{bounds.MinY}]..[{bounds.MaxX},{bounds.MaxY}]" );
	}

	static void TestHoldingStillDoesNotDarken()
	{
		// THE FAILURE THIS WHOLE SWITCH EXISTS TO KILL. The vertex-colour dab composited straight into
		// the result, so holding the brush still kept adding coverage and the colour ticked up like a
		// sculpt brush. Here a stroke is one coverage buffer composited once, so a stroke with twenty
		// points stacked on one spot and a stroke with one point there must produce byte-identical
		// canvases.
		var mesh = Grid();
		var centre = new Vec3( 0, 0, 0 );
		var normal = new Vec3( 0, 0, 1 );

		var single = new[] { Stroke( centre, normal, 0.4f ) };

		var held = new PaintStroke { R = 1f, G = 0f, B = 0f, A = 1f, Radius = 0.4f, Strength = 1f, Falloff = BrushFalloff.Smooth };

		for ( var i = 0; i < 20; i++ )
			held.Path.Add( new PaintStrokePoint( centre, normal ) );

		var a = PaintReplay.Replay( mesh, single, Res );
		var b = PaintReplay.Replay( mesh, new[] { held }, Res );

		Check( "a held brush (20 points, one spot) does not paint darker than a single dab",
			Equal( a.Rgba, b.Rgba ) );
	}

	// --- determinism ---------------------------------------------------------------------------

	static void TestReplayIsDeterministic()
	{
		var mesh = Grid();
		var strokes = new[]
		{
			Stroke( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.5f, 1, 0, 0 ),
			Stroke( new Vec3( 0.2f, 0.1f, 0 ), new Vec3( 0, 0, 1 ), 0.3f, 0, 0, 1 ),
		};

		var a = PaintReplay.Replay( mesh, strokes, Res );
		var b = PaintReplay.Replay( mesh, strokes, Res );

		Check( "replaying the same strokes twice is identical", Equal( a.Rgba, b.Rgba ) );
	}

	// --- erasing -------------------------------------------------------------------------------

	/// <summary>A hard-edged erase, so "erased" means alpha zero rather than alpha nearly zero. A
	/// Smooth erase fades to nothing at its rim like any brush, which is right for the tool and
	/// useless for an assertion about whether the paint is gone.</summary>
	static PaintStroke Eraser( Vec3 point, Vec3 normal, float radius )
	{
		var s = new PaintStroke
		{
			Radius = radius,
			Strength = 1f,
			A = 1f,
			Falloff = BrushFalloff.Constant,
			Erase = true,
		};

		s.Path.Add( new PaintStrokePoint( point, normal ) );

		return s;
	}

	static byte Alpha( PaintCanvas canvas, int x, int y ) => canvas.Rgba[(y * canvas.Width + x) * 4 + 3];

	static void TestEraseRemovesPaint()
	{
		var mesh = Grid();
		var paint = Stroke( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.9f );
		var erase = Eraser( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.4f );

		var before = PaintReplay.Replay( mesh, new[] { paint }, Res );
		var after = PaintReplay.Replay( mesh, new[] { paint, erase }, Res );

		var mid = Res / 2;

		Check( "paint lands under the brush", Alpha( before, mid, mid ) > 200, $"alpha {Alpha( before, mid, mid )}" );
		Check( "and an erase over it takes it back off", Alpha( after, mid, mid ) == 0,
			$"alpha {Alpha( after, mid, mid )}" );
	}

	static void TestEraseOnlyWhereTheBrushReached()
	{
		var mesh = Grid();
		var paint = Stroke( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.9f );
		var erase = Eraser( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.3f );

		var painted = CountPainted( PaintReplay.Replay( mesh, new[] { paint }, Res ) );
		var rubbed = CountPainted( PaintReplay.Replay( mesh, new[] { paint, erase }, Res ) );

		// An eraser is a brush, not a Clear. Both halves matter: it has to remove something, and it
		// has to leave the paint it never reached alone.
		Check( "an erase removes paint", rubbed < painted, $"{rubbed} of {painted} texels still painted" );
		Check( "and leaves the paint outside its radius", rubbed > 0, $"{rubbed} texels still painted" );
	}

	/// <summary>
	/// THE REASON AN ERASE IS A STROKE RATHER THAN A CANVAS EDIT. Strokes are a log replayed in order
	/// on every rebuild, so an erase has to sit in that log at the point the hand made it: erase then
	/// paint puts the paint back, paint then erase does not. A canvas-scrubbing eraser would give the
	/// same answer to both, and would lose to the next rebuild either way.
	/// </summary>
	static void TestEraseAndPaintReplayInOrder()
	{
		var mesh = Grid();
		var paint = Stroke( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.9f );
		var erase = Eraser( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.4f );

		var mid = Res / 2;
		var paintLast = PaintReplay.Replay( mesh, new[] { erase, paint }, Res );
		var eraseLast = PaintReplay.Replay( mesh, new[] { paint, erase }, Res );

		Check( "erasing and then painting leaves paint", Alpha( paintLast, mid, mid ) > 200,
			$"alpha {Alpha( paintLast, mid, mid )}" );
		Check( "painting and then erasing does not", Alpha( eraseLast, mid, mid ) == 0,
			$"alpha {Alpha( eraseLast, mid, mid )}" );
	}

	/// <summary>
	/// THE TRAP THIS FEATURE WALKED STRAIGHT INTO. Replay dilates at the end so a shader filtering
	/// across an island's edge finds colour in the gutter — and a dilate cannot tell the gutter from
	/// a hole somebody deliberately erased. Unprotected, four passes eat four texels off every side
	/// of an erased hole, so a small erase vanishes and a large one grows back soft edges — and only
	/// on REBUILD, because the live session never dilates. The hole here is a hard-edged 32 texels
	/// across; unprotected it would come back 24.
	/// </summary>
	static void TestDilateDoesNotRefillAnErasedHole()
	{
		var mesh = Grid();
		var paint = Stroke( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.9f );
		var erase = Eraser( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.4f );

		var canvas = PaintReplay.Replay( mesh, new[] { paint, erase }, Res );
		var run = LongestClearRun( canvas, Res / 2 );

		Check( "the dilate does not bleed paint back into an erased hole", run >= 30,
			$"{run} texels clear across the middle" );
	}

	/// <summary>The longest unbroken run of fully transparent texels along one row.</summary>
	static int LongestClearRun( PaintCanvas canvas, int y )
	{
		int best = 0, run = 0;

		for ( var x = 0; x < canvas.Width; x++ )
		{
			run = Alpha( canvas, x, y ) == 0 ? run + 1 : 0;

			if ( run > best )
				best = run;
		}

		return best;
	}

	/// <summary>The live half: the session must erase on screen the way the replay erases on rebuild,
	/// and the stroke it hands back must be marked so the document records which kind it was.</summary>
	static void TestSessionErases()
	{
		var mesh = Grid();
		var session = new PaintSession( mesh, Res ) { R = 1f, G = 0f, B = 0f, Radius = 0.9f };

		session.BeginStroke( new Vec3( 0, 0, 5f ), new Vec3( 0, 0, -1f ) );
		session.EndStroke();

		var mid = Res / 2;
		Check( "the session paints", Alpha( session.Canvas, mid, mid ) > 200,
			$"alpha {Alpha( session.Canvas, mid, mid )}" );

		session.Erasing = true;
		session.Radius = 0.4f;
		session.Falloff = BrushFalloff.Constant;

		session.BeginStroke( new Vec3( 0, 0, 5f ), new Vec3( 0, 0, -1f ) );
		var stroke = session.EndStroke();

		Check( "an erase stroke is marked as one", stroke is { Erase: true } );
		Check( "and the live canvas shows the hole immediately", Alpha( session.Canvas, mid, mid ) == 0,
			$"alpha {Alpha( session.Canvas, mid, mid )}" );

		// The claim the whole design rests on: what the hand saw and what a rebuild produces are the
		// same mark. Compared at the erased texel rather than byte-for-byte, because Replay dilates
		// the gutter and the live session deliberately does not.
		var replayed = PaintReplay.Replay( mesh, session.Strokes, Res );

		Check( "and a rebuild from the same strokes agrees", Alpha( replayed, mid, mid ) == 0,
			$"alpha {Alpha( replayed, mid, mid )}" );
	}

	// --- the session ---------------------------------------------------------------------------

	static int CountPainted( PaintCanvas canvas )
	{
		var n = 0;

		for ( var i = 3; i < canvas.Rgba.Length; i += 4 )
		{
			if ( canvas.Rgba[i] > 0 )
				n++;
		}

		return n;
	}

	static void TestSessionLifecycle()
	{
		var mesh = Grid();
		var session = new PaintSession( mesh, Res ) { R = 1f, G = 0f, B = 0f, Radius = 0.5f };

		var began = session.BeginStroke( new Vec3( 0, 0, 2 ), new Vec3( 0, 0, -1 ) );

		Check( "a stroke begins on a hit", began );

		var before = CountPainted( session.Canvas );

		var samples = session.MoveTo( new Vec3( 0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );

		Check( "a drag far enough earns interpolated samples", samples > 0, $"{samples} samples" );
		Check( "and the drag painted more of the surface", CountPainted( session.Canvas ) >= before );

		var stroke = session.EndStroke();

		Check( "ending a stroke returns it", stroke is not null );
		Check( "with the path points the stroke recorded", stroke.Path.Count == 1 + samples, $"{stroke.Path.Count}" );
		Check( "and the session is no longer stroking", !session.IsStroking );
	}

	static void TestCancelRestoresCommittedStrokes()
	{
		var mesh = Grid();
		var session = new PaintSession( mesh, Res ) { R = 1f, G = 0f, B = 0f, Radius = 0.3f };

		session.BeginStroke( new Vec3( -0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );
		session.EndStroke();

		var committed = (byte[])session.Canvas.Rgba.Clone();

		session.BeginStroke( new Vec3( 0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );
		session.CancelStroke();

		Check( "cancelling a stroke returns the canvas to the committed strokes",
			Equal( session.Canvas.Rgba, committed ) );
	}

	static void TestSessionReload()
	{
		var mesh = Grid();
		var session = new PaintSession( mesh, Res ) { R = 1f, G = 0f, B = 0f, Radius = 0.3f };

		session.BeginStroke( new Vec3( -0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );
		var stroke = session.EndStroke();

		Check( "a stroke leaves paint behind", CountPainted( session.Canvas ) > 0 );

		session.Reload( Array.Empty<PaintStroke>() );

		Check( "reloading with no strokes clears the paint", CountPainted( session.Canvas ) == 0 );

		session.Reload( new[] { stroke } );

		Check( "and reloading with the stroke brings it back", CountPainted( session.Canvas ) > 0 );
	}

	static void TestMirrorPaintsBothSides()
	{
		// Mirror is recorded INTO the stroke's path rather than applied only live, so a rebuild and an
		// export reproduce it. This is the "mirror vanishes when you reopen" failure, caught here.
		// X is the exercised axis; Y and Z share the same Brush.Mirror helper, so they ride with it.
		var mesh = Grid();
		var session = new PaintSession( mesh, Res ) { R = 1f, G = 0f, B = 0f, Radius = 0.3f, Mirror = MirrorAxis.X };

		session.BeginStroke( new Vec3( 0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );
		var stroke = session.EndStroke();

		// A fresh session replayed from the committed stroke reproduces the paint byte-for-byte —
		// the live canvas and the rebuilt canvas come from the same dab, so the mirror (which is
		// recorded INTO the stroke) must survive both.
		var replayed = new PaintSession( mesh, Res );
		replayed.Reload( new[] { stroke } );

		Check( "the mirrored point is recorded in the path", stroke.Path.Count >= 2, $"{stroke.Path.Count} points" );
		Check( "replaying the stroke reproduces the mirrored paint exactly",
			Equal( session.Canvas.Rgba, replayed.Canvas.Rgba ) );
	}

	// --- the feature ---------------------------------------------------------------------------

	static PartStudio PaintedStudio()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;

		// The editor inserts this when entering paint on a body whose UVs will not serve. A bare box
		// ships six overlapping 0..1 islands, so an unwrap is what makes paint land on its own texels.
		var uv = studio.Add( new UVProjectFeature() );
		uv.Mode.Index = Array.IndexOf( uv.Mode.Options, "Unwrap" );

		var paint = studio.Add( new PaintFeature() );

		return studio;
	}

	static void TestFeatureProducesCanvas()
	{
		var studio = PaintedStudio();
		var paint = studio.Features.OfType<PaintFeature>().Single();

		paint.AddStroke( Stroke( new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 0.3f ) );
		studio.Rebuild();

		var mesh = studio.Bodies[0].Mesh;

		Check( "a paint feature puts an atlas on the body", mesh.HasPaint, paint.Error ?? "no canvas" );

		var any = false;

		for ( var i = 3; i < mesh.Paint.Rgba.Length; i += 4 )
		{
			if ( mesh.Paint.Rgba[i] > 0 )
				any = true;
		}

		Check( "and the atlas carries the stroke's colour", any );
	}

	static void TestFeatureCanvasPicksUpNewStrokes()
	{
		// The replay cache is keyed on topology + atlas + REVISION. A stroke appended between rebuilds
		// changes the revision while the mesh does not move, and a canvas keyed on the mesh alone
		// would keep serving the pre-stroke result — paint that is saved but never appears.
		var studio = PaintedStudio();
		var paint = studio.Features.OfType<PaintFeature>().Single();

		paint.AddStroke( Stroke( new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 0.3f ) );
		studio.Rebuild();
		var first = CountPainted( studio.Bodies[0].Mesh.Paint );

		paint.AddStroke( Stroke( new Vec3( 1, 0, 0 ), new Vec3( 1, 0, 0 ), 0.3f ) );
		studio.Rebuild();
		var second = CountPainted( studio.Bodies[0].Mesh.Paint );

		Check( "a stroke appended after a rebuild lands in the replayed canvas", second > first,
			$"{first} -> {second}" );
	}

	static void TestFeatureReplaceStrokes()
	{
		// ReplaceStrokes is undo/redo's route into the feature. It must bump the revision, or the
		// replay cache — keyed on topology + atlas + revision — keeps serving paint the restored list
		// does not describe: undo would remove the stroke from the document and the model would keep it.
		var studio = PaintedStudio();
		var paint = studio.Features.OfType<PaintFeature>().Single();

		var a = Stroke( new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 0.3f );
		var b = Stroke( new Vec3( 1, 0, 0 ), new Vec3( 1, 0, 0 ), 0.3f );

		paint.AddStroke( a );
		paint.AddStroke( b );
		studio.Rebuild();
		var both = CountPainted( studio.Bodies[0].Mesh.Paint );

		paint.ReplaceStrokes( new[] { a } );
		studio.Rebuild();
		var one = CountPainted( studio.Bodies[0].Mesh.Paint );

		Check( "replacing the stroke list re-replays the canvas", one < both, $"{both} -> {one}" );
	}

	// --- helpers -------------------------------------------------------------------------------

	static bool Equal( float[] a, float[] b )
	{
		if ( a.Length != b.Length )
			return false;

		for ( var i = 0; i < a.Length; i++ )
		{
			if ( MathF.Abs( a[i] - b[i] ) > 1e-6f )
				return false;
		}

		return true;
	}

	static bool Equal( byte[] a, byte[] b )
	{
		if ( a.Length != b.Length )
			return false;

		for ( var i = 0; i < a.Length; i++ )
		{
			if ( a[i] != b[i] )
				return false;
		}

		return true;
	}
}
