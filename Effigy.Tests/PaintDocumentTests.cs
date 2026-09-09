using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Paint strokes survive the document round trip.
///
/// WHY THIS MATTERS MORE THAN THE OTHER DOCUMENT TESTS. Strokes are a LOG — colour blending does not
/// commute, so a round trip that reordered them would be silent corruption rather than a diff. The
/// order assertion below is the whole reason paint is stored as strokes rather than dabs, and it is
/// the one thing a field-by-field comparison cannot catch.
/// </summary>
public static class PaintDocumentTests
{
	public static void Run()
	{
				Report.Section( "paint: a bare box is reachable" );
		TestBareBoxIsReachable();

Section( "paint: strokes survive the document round trip" );
		TestStrokesRoundTrip();
		TestOrderIsPreserved();
		TestEmptyPathRoundTrips();
		TestSavingTwiceIsIdentical();
		TestStaleness();

		Section( "paint: erases round trip, and older documents have none" );
		TestErasesRoundTripInPlace();
		TestDocumentWithoutErasesReadsAsPaint();

		Section( "paint: resolution is a parameter, not a constant" );
		TestResolutionIsAParameter();
	}


	/// <summary>
	/// THE CASE NOTHING COVERED: a bare box, which is the first thing anybody paints.
	///
	/// The vertex-colour brush could not reach a single vertex on an unsubdivided part — it painted
	/// nothing, silently, and looked broken. A texel dab needs no vertices, so a bare box must paint
	/// at brush resolution the moment it has usable UVs, which is what the auto-unwrap provides. This
	/// is the acceptance case for the whole move back to a texture atlas.
	/// </summary>
	static void TestBareBoxIsReachable()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 1f;

		// The unwrap the editor inserts on entering paint — a bare box ships six overlapping islands,
		// so without it the paint would scramble onto every face at once.
		var uv = studio.Add( new UVProjectFeature() );
		uv.Mode.Index = Array.IndexOf( uv.Mode.Options, "Unwrap" );
		studio.Rebuild();

		var mesh = studio.Bodies[0].Mesh;

		Report.Check( "a 1-unit box really does have only its corners",
			mesh.Positions.Count == 8, $"{mesh.Positions.Count} vertices" );

		var session = new PaintSession( mesh, 1024 );
		session.Radius = session.SuggestedRadius;

		var hit = session.Hover( new Vec3( 0, 0, 10f ), new Vec3( 0, 0, -1f ) );

		Report.Check( "a ray straight down finds the top face", hit is not null );

		if ( hit is { } h )
		{
			session.BeginStroke( new Vec3( 0, 0, 10f ), new Vec3( 0, 0, -1f ) );
			session.EndStroke();

			Report.Check( "a single dab on a bare box paints texels",
				CountPainted( session.Canvas ) > 0, $"{CountPainted( session.Canvas )} texels" );
		}
	}

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

	static PaintStroke MakeStroke( float r, float g, float b, float a, float radius, float strength,
		BrushFalloff falloff, float spacing, int points )
	{
		var stroke = new PaintStroke
		{
			R = r,
			G = g,
			B = b,
			A = a,
			Radius = radius,
			Strength = strength,
			Falloff = falloff,
			Spacing = spacing,
		};

		for ( var i = 0; i < points; i++ )
		{
			stroke.Path.Add( new PaintStrokePoint(
				new Vec3( i, i * 0.5f, -i * 0.25f ),
				new Vec3( 0f, 0f, 1f ) ) );
		}

		return stroke;
	}

	/// <summary>A studio with a paint feature carrying two strokes — red then green, several points
	/// each, deliberately distinct so an order swap would show up as the wrong colour.</summary>
	static PartStudio Painted()
	{
		var studio = new PartStudio();
		var paint = studio.Add( new PaintFeature() );

		paint.AddStroke( MakeStroke( 0.9f, 0.1f, 0.2f, 1f, 0.5f, 0.8f, BrushFalloff.Sharp, 0.25f, 3 ) );
		paint.AddStroke( MakeStroke( 0.1f, 0.9f, 0.3f, 0.5f, 0.2f, 0.4f, BrushFalloff.Linear, 0.5f, 4 ) );

		return studio;
	}

	static void TestStrokesRoundTrip()
	{
		var original = Painted();
		var originalPaint = original.Features.OfType<PaintFeature>().Single();

		var back = StudioDocument.Read( StudioDocument.Write( original ) ).Features.OfType<PaintFeature>().Single();

		Check( "the same number of strokes come back",
			back.Strokes.Count == originalPaint.Strokes.Count,
			$"{originalPaint.Strokes.Count} became {back.Strokes.Count}" );

		for ( var s = 0; s < originalPaint.Strokes.Count && s < back.Strokes.Count; s++ )
		{
			var a = originalPaint.Strokes[s];
			var b = back.Strokes[s];

			Check( $"stroke {s} keeps its point count", b.Path.Count == a.Path.Count,
				$"{a.Path.Count} became {b.Path.Count}" );

			Check( $"stroke {s} keeps its colour", b.R == a.R && b.G == a.G && b.B == a.B && b.A == a.A );
			Check( $"stroke {s} keeps its radius", b.Radius == a.Radius );
			Check( $"stroke {s} keeps its strength", b.Strength == a.Strength );
			Check( $"stroke {s} keeps its falloff", b.Falloff == a.Falloff );
			Check( $"stroke {s} keeps its spacing", b.Spacing == a.Spacing );

			for ( var p = 0; p < a.Path.Count && p < b.Path.Count; p++ )
			{
				Check( $"stroke {s} point {p} keeps its position", Close( a.Path[p].Position, b.Path[p].Position ) );
				Check( $"stroke {s} point {p} keeps its normal", Close( a.Path[p].Normal, b.Path[p].Normal ) );
			}
		}
	}

	/// <summary>
	/// An erase is a different LINE KIND in the file, and the thing that has to survive is not just
	/// the flag but its POSITION. Paint, erase, paint is three entries in one log and the middle one
	/// only means anything where it sits — a round trip that grouped the erases at the end, or that
	/// dropped them into a second list, would replay as a part with a hole in the wrong place.
	/// </summary>
	static void TestErasesRoundTripInPlace()
	{
		var studio = new PartStudio();
		var paint = studio.Add( new PaintFeature() );

		paint.AddStroke( MakeStroke( 0.9f, 0.1f, 0.2f, 1f, 0.5f, 0.8f, BrushFalloff.Sharp, 0.25f, 3 ) );

		var rubbed = MakeStroke( 0.1f, 0.9f, 0.3f, 0.5f, 0.2f, 0.4f, BrushFalloff.Constant, 0.5f, 4 );
		rubbed.Erase = true;
		paint.AddStroke( rubbed );

		paint.AddStroke( MakeStroke( 0.2f, 0.3f, 0.9f, 1f, 0.7f, 0.6f, BrushFalloff.Linear, 0.5f, 2 ) );

		var text = StudioDocument.Write( studio );
		var back = StudioDocument.Read( text ).Features.OfType<PaintFeature>().Single();

		Check( "the file writes an erase as its own line kind", text.Contains( "\terase " ),
			text.Contains( "\tstroke " ) ? "only stroke lines were written" : "no stroke lines at all" );

		Check( "all three entries come back", back.Strokes?.Count == 3, $"{back.Strokes?.Count ?? 0}" );

		if ( back.Strokes?.Count != 3 )
			return;

		Check( "the erase comes back in the middle, where it was made",
			!back.Strokes[0].Erase && back.Strokes[1].Erase && !back.Strokes[2].Erase,
			$"{back.Strokes[0].Erase}, {back.Strokes[1].Erase}, {back.Strokes[2].Erase}" );

		// The erase carries a full brush header like any stroke, and replay reads every field of it.
		Check( "and keeps the brush it was made with",
			back.Strokes[1].Radius == rubbed.Radius && back.Strokes[1].Falloff == rubbed.Falloff
			&& back.Strokes[1].Path.Count == rubbed.Path.Count );
	}

	/// <summary>
	/// FORWARD COMPATIBILITY, WITHOUT A VERSION FIELD. Every .effigy written before erasing existed
	/// has only "stroke" lines, and the absence of an "erase" line has to read as "nothing here is an
	/// erase" — which it does, because the flag lives in the keyword rather than in the header. Had it
	/// been a ninth header number instead, every one of those documents would read its first path
	/// point as the flag and shift the whole path by one.
	/// </summary>
	static void TestDocumentWithoutErasesReadsAsPaint()
	{
		var text = StudioDocument.Write( Painted() );

		Check( "a document with no erases writes none", !text.Contains( "\terase " ) );

		var back = StudioDocument.Read( text ).Features.OfType<PaintFeature>().Single();
		var allPaint = back.Strokes is { Count: 2 } && back.Strokes.TrueForAll( s => !s.Erase );

		Check( "and reads back as paint, every stroke of it", allPaint );
	}

	static void TestOrderIsPreserved()
	{
		var original = Painted();
		var originalPaint = original.Features.OfType<PaintFeature>().Single();
		var back = StudioDocument.Read( StudioDocument.Write( original ) ).Features.OfType<PaintFeature>().Single();

		// Red then green, deliberately. Blending does not commute, so the exact sequence is the thing
		// a reorder destroys — assert the colours come back in the painted order, not sorted.
		var orderKept = back.Strokes.Count == originalPaint.Strokes.Count;

		for ( var s = 0; orderKept && s < originalPaint.Strokes.Count; s++ )
		{
			orderKept = back.Strokes[s].R == originalPaint.Strokes[s].R
				&& back.Strokes[s].G == originalPaint.Strokes[s].G
				&& back.Strokes[s].B == originalPaint.Strokes[s].B;
		}

		Check( "strokes come back in the order they were painted", orderKept );
	}

	static void TestEmptyPathRoundTrips()
	{
		var studio = new PartStudio();
		var paint = studio.Add( new PaintFeature() );
		paint.AddStroke( MakeStroke( 1f, 1f, 1f, 1f, 0.1f, 1f, BrushFalloff.Smooth, 0.5f, 0 ) );

		// Reaching the assertions at all is the "does not throw" half — a write that threw would
		// crash the runner rather than report a failed check.
		var back = StudioDocument.Read( StudioDocument.Write( studio ) ).Features.OfType<PaintFeature>().Single();

		Check( "a stroke with no points survives the round trip", back.Strokes.Count == 1,
			$"{back.Strokes.Count} strokes" );
		Check( "and keeps its empty path", back.Strokes[0].Path.Count == 0,
			$"{back.Strokes[0].Path.Count} points" );
	}

	static void TestSavingTwiceIsIdentical()
	{
		var studio = Painted();

		var first = StudioDocument.Write( studio );
		var second = StudioDocument.Write( studio );

		Check( "saving the same document twice gives the same bytes", first == second );
	}

	static void TestStaleness()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );

		var paint = studio.Add( new PaintFeature() );
		studio.Rebuild();

		Check( "a rebuilt paint feature is not stale", !paint.IsStale );

		paint.AddStroke( MakeStroke( 1f, 0f, 0f, 1f, 0.1f, 1f, BrushFalloff.Smooth, 0.5f, 2 ) );

		Check( "appending a stroke marks it stale", paint.IsStale );

		studio.Rebuild();

		Check( "and a rebuild clears it", !paint.IsStale );
	}

	static bool Close( Vec3 a, Vec3 b ) =>
		MathF.Abs( a.x - b.x ) < 1e-5f && MathF.Abs( a.y - b.y ) < 1e-5f && MathF.Abs( a.z - b.z ) < 1e-5f;

	/// <summary>
	/// Resolution used to be a const, so every part painted at 1024 whether that was a matchbox or a
	/// stadium. It is a parameter now, and the three things that used to break when it was a const
	/// have to hold: the replay cache misses when it changes (no stale canvas served at the old
	/// size), the strokes re-replay rather than scale or clear, and the setting survives a save.
	/// </summary>
	static void TestResolutionIsAParameter()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 1f;

		var uv = studio.Add( new UVProjectFeature() );
		uv.Mode.Index = Array.IndexOf( uv.Mode.Options, "Unwrap" );

		var paint = studio.Add( new PaintFeature() );
		paint.AddStroke( TopFaceStroke() );

		studio.Rebuild();

		var first = paint.Canvas;
		Check( "a fresh paint feature replays at the 1024 default",
			first is not null && first.Width == 1024, first is null ? "no canvas" : $"{first.Width}" );

		var paintedAtDefault = CountPainted( first );
		Check( "and the box is painted", paintedAtDefault > 0, $"{paintedAtDefault} texels" );

		// The editor marks the feature dirty when a parameter changes; done by hand here.
		paint.Resolution.Value = 256;
		studio.MarkDirty( paint );
		studio.Rebuild();

		var second = paint.Canvas;
		Check( "changing the resolution replays at the new size",
			second.Width == 256 && second.Height == 256, $"{second.Width}x{second.Height}" );
		Check( "and is not the stale canvas", !ReferenceEquals( first, second ) );
		Check( "and the paint is still there, not cleared", CountPainted( second ) > 0,
			$"{CountPainted( second )} texels" );

		var back = StudioDocument.Read( StudioDocument.Write( studio ) ).Features.OfType<PaintFeature>().Single();
		Check( "save and reopen keeps the resolution", back.Resolution.Value == 256, $"{back.Resolution.Value}" );
	}

	/// <summary>One dab flat on the top of a 1-unit box, where the replay can actually land.</summary>
	static PaintStroke TopFaceStroke()
	{
		var stroke = new PaintStroke
		{
			R = 1f, G = 0f, B = 0f, A = 1f,
			Radius = 0.3f, Strength = 1f, Falloff = BrushFalloff.Sharp, Spacing = 0.5f,
		};

		stroke.Path.Add( new PaintStrokePoint( new Vec3( 0, 0, 0.5f ), new Vec3( 0, 0, 1 ) ) );
		return stroke;
	}
}
