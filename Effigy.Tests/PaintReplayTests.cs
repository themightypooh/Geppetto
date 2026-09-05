using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The vertex-colour paint dab and its replay — the s&amp;box-native path where paint composes over a
/// material. Judged by the same proofs as the texture dab, minus the seams: a vertex dab needs no
/// atlas, so "paint both charts" has no meaning here; what remains is "the dab colours the surface
/// under the brush, the far side of a thin wall is left alone, the falloff is measured in 3D, and
/// replay is deterministic".
/// </summary>
public static class PaintReplayTests
{
	public static void Run()
	{
		Section( "paint: the dab colours the surface under the brush" );
		TestDabColoursTheVerticesUnderTheBrush();
		TestFalloffIsDistanceWeighted();

		Section( "paint: the thin-wall guard" );
		TestThinWallPaintsOneSideOnly();

		Section( "paint: replay is deterministic" );
		TestReplayIsDeterministic();

		Section( "paint: the session's stroke contract" );
		TestSessionLifecycle();
		TestCancelRestoresCommittedStrokes();
		TestSessionReload();
		TestMirrorXPaintsBothSides();

		Section( "paint: the feature replays onto vertex colours" );
		TestFeatureProducesColors();
		TestFeatureColorsPickUpNewStrokes();
		TestFeatureReplaceStrokes();
	}

	// --- fixtures ------------------------------------------------------------------------------

	/// <summary>A 2x2 grid of quads in the XY plane, centred on the origin — enough vertices to say
	/// which ones a dab touched, without depending on any particular primitive's layout.</summary>
	static PolyMesh Grid()
	{
		var m = new PolyMesh();

		const int n = 8;

		for ( var iy = 0; iy <= n; iy++ )
		{
			for ( var ix = 0; ix <= n; ix++ )
				m.AddVertex( new Vec3( ix / (float)n * 2f - 1f, iy / (float)n * 2f - 1f, 0f ) );
		}

		for ( var iy = 0; iy < n; iy++ )
		{
			for ( var ix = 0; ix < n; ix++ )
			{
				var a = iy * (n + 1) + ix;
				m.AddFace( new[] { a, a + 1, a + n + 2, a + n + 1 } );
			}
		}

		return m;
	}

	/// <summary>Two faces at the same position, one wound +Z and one wound -Z, with separate
	/// vertices on each side. A dab on the top must not bleed through to the bottom.</summary>
	static PolyMesh ThinWall()
	{
		var m = new PolyMesh();

		m.AddVertex( new Vec3( 0, 0, 0 ) );
		m.AddVertex( new Vec3( 1, 0, 0 ) );
		m.AddVertex( new Vec3( 1, 1, 0 ) );
		m.AddVertex( new Vec3( 0, 1, 0 ) );

		m.AddFace( new[] { 0, 1, 2, 3 } ); // +Z

		m.AddVertex( new Vec3( 0, 0, 0 ) );
		m.AddVertex( new Vec3( 0, 1, 0 ) );
		m.AddVertex( new Vec3( 1, 1, 0 ) );
		m.AddVertex( new Vec3( 1, 0, 0 ) );

		m.AddFace( new[] { 4, 5, 6, 7 } ); // -Z

		return m;
	}

	static PaintStroke Stroke( Vec3 point, Vec3 normal, float radius = 0.5f, float r = 1f, float g = 0f, float b = 0f )
	{
		var s = new PaintStroke { R = r, G = g, B = b, A = 1f, Radius = radius, Strength = 1f, Falloff = BrushFalloff.Smooth };
		s.Path.Add( new PaintStrokePoint( point, normal ) );
		return s;
	}

	// --- the dab -------------------------------------------------------------------------------

	static void TestDabColoursTheVerticesUnderTheBrush()
	{
		var mesh = Grid();
		var colors = PaintReplay.ReplayColors( mesh, new[] { Stroke( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.4f ) } );

		var painted = 0;

		foreach ( var c in colors )
		{
			if ( c.w > 0f )
				painted++;
		}

		Check( "a dab paints the vertices inside its radius", painted > 0, $"{painted} of {colors.Length}" );

		var outside = true;

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			if ( mesh.Positions[i].Length > 0.9f && colors[i].w > 0f )
				outside = false;
		}

		Check( "and leaves vertices beyond the radius alone", outside );
	}

	static void TestFalloffIsDistanceWeighted()
	{
		var mesh = Grid();
		var colors = PaintReplay.ReplayColors( mesh, new[] { Stroke( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 0.5f ) } );

		// The vertex nearest the dab centre must be the strongest, and some vertex partway out must
		// carry a fraction rather than the whole — the assertion that pins the falloff to distance.
		var centre = Closest( mesh, new Vec3( 0, 0, 0 ) );

		Check( "the vertex under the centre is fully opaque", colors[centre].w >= 0.99f, $"{colors[centre].w:0.###}" );

		var partial = false;

		foreach ( var c in colors )
		{
			if ( c.w > 0f && c.w < 0.99f )
				partial = true;
		}

		Check( "a vertex partway out is partially transparent", partial );
	}

	static void TestThinWallPaintsOneSideOnly()
	{
		var mesh = ThinWall();
		var colors = PaintReplay.ReplayColors( mesh, new[] { Stroke( new Vec3( 0.5f, 0.5f, 0 ), new Vec3( 0, 0, 1 ), 1f ) } );

		var near = colors[0].w > 0f || colors[1].w > 0f || colors[2].w > 0f || colors[3].w > 0f;
		var far = colors[4].w > 0f || colors[5].w > 0f || colors[6].w > 0f || colors[7].w > 0f;

		Check( "the near side is painted", near );
		Check( "the far side is not", !far );
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

		var a = PaintReplay.ReplayColors( mesh, strokes );
		var b = PaintReplay.ReplayColors( mesh, strokes );

		Check( "replaying the same strokes twice is identical", Equal( a, b ) );
	}

	// --- the session ---------------------------------------------------------------------------

	static void TestSessionLifecycle()
	{
		var mesh = Grid();
		var session = new PaintSession( mesh ) { R = 1f, G = 0f, B = 0f, Radius = 0.5f };

		var began = session.BeginStroke( new Vec3( 0, 0, 2 ), new Vec3( 0, 0, -1 ) );

		Check( "a stroke begins on a hit", began );

		var before = CountColored( session.Colors );

		var samples = session.MoveTo( new Vec3( 0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );

		Check( "a drag far enough earns interpolated samples", samples > 0, $"{samples} samples" );
		Check( "and the drag painted more of the surface", CountColored( session.Colors ) >= before );

		var stroke = session.EndStroke();

		Check( "ending a stroke returns it", stroke is not null );
		Check( "with the path points the stroke recorded", stroke.Path.Count == 1 + samples, $"{stroke.Path.Count}" );
		Check( "and the session is no longer stroking", !session.IsStroking );
	}

	static void TestCancelRestoresCommittedStrokes()
	{
		var mesh = Grid();
		var session = new PaintSession( mesh ) { R = 1f, G = 0f, B = 0f, Radius = 0.3f };

		session.BeginStroke( new Vec3( -0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );
		session.EndStroke();

		var committed = (Vec4[])session.Colors.Clone();

		session.BeginStroke( new Vec3( 0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );
		session.CancelStroke();

		Check( "cancelling a stroke returns the colours to the committed strokes",
			Equal( session.Colors, committed ) );
	}

	static void TestSessionReload()
	{
		// Reload is undo/redo's route into the session: it adopts a new stroke list and replays it,
		// so a document restore that removed a stroke cannot leave the live colours serving the old
		// one. This is the "paint disappears when you undo" failure, caught in the kernel.
		var mesh = Grid();
		var session = new PaintSession( mesh ) { R = 1f, G = 0f, B = 0f, Radius = 0.3f };

		session.BeginStroke( new Vec3( -0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );
		var stroke = session.EndStroke();

		Check( "a stroke leaves paint behind", CountColored( session.Colors ) > 0 );

		session.Reload( Array.Empty<PaintStroke>() );

		Check( "reloading with no strokes clears the colours", CountColored( session.Colors ) == 0 );

		session.Reload( new[] { stroke } );

		Check( "and reloading with the stroke brings them back", CountColored( session.Colors ) > 0 );
	}

	static void TestMirrorXPaintsBothSides()
	{
		// Mirror is recorded INTO the stroke's path rather than applied only live, so a rebuild and an
		// export reproduce it. This is the "mirror vanishes when you reopen" failure, caught here.
		var mesh = Grid();
		var session = new PaintSession( mesh ) { R = 1f, G = 0f, B = 0f, Radius = 0.3f, MirrorX = true };

		session.BeginStroke( new Vec3( 0.5f, 0, 2 ), new Vec3( 0, 0, -1 ) );
		var stroke = session.EndStroke();

		var replayed = PaintReplay.ReplayColors( mesh, new[] { stroke } );

		Check( "the mirrored point is recorded in the path", stroke.Path.Count >= 2, $"{stroke.Path.Count} points" );

		var plusX = Closest( mesh, new Vec3( 0.5f, 0, 0 ) );
		var minusX = Closest( mesh, new Vec3( -0.5f, 0, 0 ) );

		Check( "both the hit side and its mirror got paint",
			session.Colors[plusX].w > 0f && session.Colors[minusX].w > 0f );

		Check( "replay reproduces the mirrored paint exactly", Equal( session.Colors, replayed ) );
	}

	// --- the feature ---------------------------------------------------------------------------

	static void TestFeatureProducesColors()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;

		// Vertex colour needs vertices to carry it — a bare box has eight, so the paint is meant to
		// land after a subdivision, which is what a painter would do. This is that tree.
		var subdiv = studio.Add( new SubdivideFeature() );
		subdiv.Levels.Value = 2;

		var paint = studio.Add( new PaintFeature() );
		paint.AddStroke( Stroke( new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 0.3f ) );

		studio.Rebuild();

		var mesh = studio.Bodies[0].Mesh;

		Check( "a paint feature sets vertex colours on the body", mesh.HasVertexColors,
			paint.Error ?? "no colours" );

		var any = mesh.VertexColors is not null && mesh.VertexColors.Any( c => c.w > 0f );
		Check( "and they carry the stroke's colour", any );
	}

	static void TestFeatureColorsPickUpNewStrokes()
	{
		// The replay cache is keyed on topology + REVISION. A stroke appended between rebuilds
		// changes the revision while the mesh does not move, and colours keyed on the mesh alone
		// would keep serving the pre-stroke result — paint that is saved but never appears.
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;

		var subdiv = studio.Add( new SubdivideFeature() );
		subdiv.Levels.Value = 2;

		var paint = studio.Add( new PaintFeature() );

		paint.AddStroke( Stroke( new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 0.3f ) );
		studio.Rebuild();
		var first = CountColored( studio.Bodies[0].Mesh.VertexColors );

		paint.AddStroke( Stroke( new Vec3( 1, 0, 0 ), new Vec3( 1, 0, 0 ), 0.3f ) );
		studio.Rebuild();
		var second = CountColored( studio.Bodies[0].Mesh.VertexColors );

		Check( "a stroke appended after a rebuild lands in the replayed colours", second > first,
			$"{first} -> {second}" );
	}

	static void TestFeatureReplaceStrokes()
	{
		// ReplaceStrokes is undo/redo's route into the feature. It must bump the revision, or the
		// replay cache — keyed on topology + revision — keeps serving colours the restored list does
		// not describe: undo would remove the stroke from the document and the model would keep it.
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;

		var subdiv = studio.Add( new SubdivideFeature() );
		subdiv.Levels.Value = 2;

		var paint = studio.Add( new PaintFeature() );

		var a = Stroke( new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 0.3f );
		var b = Stroke( new Vec3( 1, 0, 0 ), new Vec3( 1, 0, 0 ), 0.3f );

		paint.AddStroke( a );
		paint.AddStroke( b );
		studio.Rebuild();
		var both = CountColored( studio.Bodies[0].Mesh.VertexColors );

		paint.ReplaceStrokes( new[] { a } );
		studio.Rebuild();
		var one = CountColored( studio.Bodies[0].Mesh.VertexColors );

		Check( "replacing the stroke list re-replays the colours", one < both, $"{both} -> {one}" );
	}

	// --- helpers -------------------------------------------------------------------------------

	static int Closest( PolyMesh mesh, Vec3 point )
	{
		var best = 0;
		var bestD = float.MaxValue;

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var d = (mesh.Positions[i] - point).LengthSquared;

			if ( d < bestD )
			{
				bestD = d;
				best = i;
			}
		}

		return best;
	}

	static int CountColored( Vec4[] colors )
	{
		var n = 0;

		foreach ( var c in colors )
		{
			if ( c.w > 0f )
				n++;
		}

		return n;
	}

	static bool Equal( Vec4[] a, Vec4[] b )
	{
		if ( a.Length != b.Length )
			return false;

		for ( var i = 0; i < a.Length; i++ )
		{
			if ( MathF.Abs( a[i].x - b[i].x ) > 1e-6f
				|| MathF.Abs( a[i].y - b[i].y ) > 1e-6f
				|| MathF.Abs( a[i].z - b[i].z ) > 1e-6f
				|| MathF.Abs( a[i].w - b[i].w ) > 1e-6f )
				return false;
		}

		return true;
	}
}
