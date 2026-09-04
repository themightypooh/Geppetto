using System;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// How big a material is, per slot.
///
/// The number is easy; WHEN it is applied is the whole risk. It divides UVs the features produced,
/// and PartStudio caches a clone of the bodies after every feature so that editing feature 7 of 20
/// reuses the snapshot from 6. Get the ordering wrong and the divide lands inside that cache: the
/// model looks right on the rebuild that set the scale and shrinks by the same factor on every
/// rebuild after it, which is a bug you find by dragging an unrelated slider twice.
///
/// So most of what is checked here is idempotence across rebuilds — a full one, an incremental one,
/// and one that re-evaluates nothing at all — rather than the arithmetic.
/// </summary>
public static class MaterialScaleTests
{
	public static void Run()
	{
		Report.Section( "material scale: a slot nobody resized is left exactly alone" );
		TestUnscaledIsUntouched();

		Report.Section( "material scale: the divide lands on the faces wearing the slot" );
		TestScaleDivides();

		Report.Section( "material scale: rebuilding does not scale it again" );
		TestIdempotentAcrossRebuilds();

		Report.Section( "material scale: a cached rebuild scales what it restored" );
		TestIncrementalRebuild();

		Report.Section( "material scale: back to 1:1 leaves no entry behind" );
		TestResetRemovesEntry();

		Report.Section( "material scale: a zero cannot divide the model into infinity" );
		TestSanitised();

		Report.Section( "material scale: fit puts a whole number of repeats on a face" );
		TestFit();

		Report.Section( "material scale: fitting twice lands on the same number" );
		TestFitIsStable();

		Report.Section( "material scale: it survives a save and a load" );
		TestRoundTrip();

		Report.Section( "material scale: a retired slot takes its size with it" );
		TestReleasedSlotLosesScale();

		Report.Section( "material scale: on an extrude cap the number really is units" );
		TestExtrudeCapIsInUnits();
	}

	/// <summary>
	/// THE CASE THAT STARTED THIS. An extrude cap takes sketch coordinates straight through as UVs,
	/// so a floor drawn 240 inches across arrives with 240 repeats on it. This is the one face where
	/// "units per tile" is literally true, and the one worth pinning: 48 has to put a repeat every 48
	/// inches, which is what the diner tile's own vmat says it wants.
	///
	/// Note what this does NOT claim. The extrude's SIDES are normalised 0..1 around the perimeter
	/// and 0..1 up the height, and a primitive box gives every face 0..1 whatever its size, so the
	/// number is only units-per-tile on faces whose UVs were in units to begin with. Three
	/// conventions in one kernel is the real bug underneath; a per-slot divide cannot fix it and
	/// should not pretend to.
	/// </summary>
	static void TestExtrudeCapIsInUnits()
	{
		var studio = new PartStudio();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Id = "floor";
		sketch.Sketch.AddRectangle( new Vec2( 0f, 0f ), new Vec2( 240f, 120f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Id = "slab";
		extrude.SketchFeatureId = "floor";
		extrude.RegionSeed = new Vec2( 120f, 60f );
		extrude.Distance.Value = 4f;

		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;
		var top = TopFace( studio );

		Report.Check( "the cap arrives mapped one repeat per unit",
			Close( SpanOf( mesh, top ).x, 240f ) && Close( SpanOf( mesh, top ).y, 120f ),
			$"{SpanOf( mesh, top )}" );

		// And the side of the same solid, to have the disagreement written down somewhere. It spans
		// its share of the perimeter by its full height: 240 of 720 units around, one tile tall,
		// whatever the extrude distance was. The same slot scale divides both, so the number can be
		// units on one face of a body and repeats-across-the-whole-thing on the next one.
		var side = FaceIndexFacing( mesh, new Vec3( 0, -1, 0 ) );

		Report.Check( "while a side of the same solid is normalised instead",
			Close( SpanOf( mesh, side ).x, 240f / 720f ) && Close( SpanOf( mesh, side ).y, 1f ),
			$"{SpanOf( mesh, side )}" );

		Paint( studio, top, 1 );
		MaterialScale.SetScale( studio, 1, new Vec2( 48f, 48f ) );
		studio.Rebuild();

		var span = Span( studio, 1 );

		Report.Check( "48 units per tile puts a repeat every 48 inches",
			Close( span.x, 240f / 48f ) && Close( span.y, 120f / 48f ),
			$"{span}, wanted 5 x 2.5" );
	}

	/// <summary>
	/// The baseline every existing document depends on. A studio with no scales set must produce
	/// byte-identical UVs to one that has never heard of the idea, or opening an old file and saving
	/// it re-textures the model.
	/// </summary>
	static void TestUnscaledIsUntouched()
	{
		var studio = Boxed();
		var before = UVs( studio );

		studio.Rebuild();

		Report.Check( "no scales, no change", Same( before, UVs( studio ) ) );

		// A scale on a slot NO face is wearing must also change nothing. Reserving a slot in the
		// Materials panel and giving it a size before painting anything is allowed.
		MaterialScale.SetScale( studio, 9, new Vec2( 48f, 48f ) );
		studio.Rebuild();

		Report.Check( "a scale on an unworn slot changes nothing", Same( before, UVs( studio ) ) );
	}

	static void TestScaleDivides()
	{
		var studio = Boxed();
		var top = TopFace( studio );

		Paint( studio, top, 1 );
		studio.Rebuild();

		var unscaled = Span( studio, 1 );
		var untouched = Span( studio, 0 );

		MaterialScale.SetScale( studio, 1, new Vec2( 4f, 4f ) );
		studio.Rebuild();

		var scaled = Span( studio, 1 );

		Report.Check( "four units per tile is a quarter of the repeats",
			Close( scaled.x, unscaled.x / 4f ) && Close( scaled.y, unscaled.y / 4f ),
			$"{unscaled} -> {scaled}" );

		// The other five faces are on slot 0 and must not have moved. This is the check that fails
		// if the divide is applied per body rather than per face.
		//
		// Compared against the span this same box HAD, not against its world size: a primitive box
		// gives every face 0..1 whatever its dimensions, which is a third UV convention alongside
		// the extrude cap's plane coordinates and the extrude side's normalised perimeter. Asserting
		// a world size here would be asserting which convention the primitive happens to use.
		var others = Span( studio, 0 );

		Report.Check( "and the faces on other slots are untouched",
			Close( others.x, untouched.x ) && Close( others.y, untouched.y ),
			$"{untouched} -> {others}" );
	}

	/// <summary>
	/// THE ONE THIS WAS ADDED FOR. Three rebuilds in a row with nothing else changing have to
	/// produce the same UVs, or the scale is compounding into the cache.
	/// </summary>
	static void TestIdempotentAcrossRebuilds()
	{
		var studio = Boxed();

		Paint( studio, TopFace( studio ), 1 );
		MaterialScale.SetScale( studio, 1, new Vec2( 8f, 8f ) );

		studio.Rebuild();
		var first = UVs( studio );

		studio.Rebuild();
		var second = UVs( studio );

		studio.Rebuild();
		var third = UVs( studio );

		Report.Check( "the second rebuild matches the first", Same( first, second ) );
		Report.Check( "and so does the third", Same( first, third ) );
	}

	/// <summary>
	/// The nastier half: a rebuild that re-evaluates NOTHING. Toggling a body's visibility or
	/// dragging the pivot restores the whole model from the snapshot cache without running a single
	/// feature, and the scale still has to be on it — the cache holds the UVs as the features made
	/// them, so a divide that only ran inside the feature loop would come back undone.
	/// </summary>
	static void TestIncrementalRebuild()
	{
		var studio = Boxed();

		Paint( studio, TopFace( studio ), 1 );
		MaterialScale.SetScale( studio, 1, new Vec2( 8f, 8f ) );
		studio.Rebuild();

		var scaled = Span( studio, 1 );

		// Nothing is dirty now, so this restores from the cache rather than running the features.
		var report = studio.Rebuild();

		Report.Check( "the rebuild reused everything", report.FeaturesEvaluated == 0,
			report.ToString() );

		Report.Check( "and the restored model is still scaled",
			Close( Span( studio, 1 ).x, scaled.x ),
			$"{scaled} -> {Span( studio, 1 )}" );

		// And an edit ABOVE the painted face, which re-runs part of the tree and restores the rest.
		var box = studio.Features.OfType<PrimitiveFeature>().Single();
		box.SizeZ.Value = 5f;
		studio.MarkDirty( box );
		studio.Rebuild();

		Report.Check( "an incremental rebuild scales the faces it rebuilt",
			Close( Span( studio, 1 ).x, scaled.x ),
			$"{Span( studio, 1 )}" );
	}

	static void TestResetRemovesEntry()
	{
		var studio = new PartStudio();

		Report.Check( "setting a scale stores it",
			MaterialScale.SetScale( studio, 1, new Vec2( 48f, 48f ) )
			&& studio.MaterialScales.Count == 1 );

		Report.Check( "setting the same scale again changes nothing",
			!MaterialScale.SetScale( studio, 1, new Vec2( 48f, 48f ) ) );

		Report.Check( "and 1:1 removes it rather than storing a no-op",
			MaterialScale.SetScale( studio, 1, MaterialScale.Unscaled )
			&& studio.MaterialScales.Count == 0 );
	}

	static void TestSanitised()
	{
		var studio = Boxed();

		Paint( studio, TopFace( studio ), 1 );
		studio.Rebuild();

		var before = Span( studio, 1 );

		// Straight into the dictionary, which is what a hand-edited document does — SetScale would
		// have caught it.
		studio.MaterialScales[1] = new Vec2( 0f, float.NaN );
		studio.Rebuild();

		var span = Span( studio, 1 );

		Report.Check( "a zero and a NaN fall back to 1:1 rather than to infinity",
			float.IsFinite( span.x ) && float.IsFinite( span.y )
			&& Close( span.x, before.x ) && Close( span.y, before.y ),
			$"{span}" );
	}

	static void TestFit()
	{
		var studio = Boxed();
		var top = TopFace( studio );

		Paint( studio, top, 1 );
		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;
		var face = FaceOn( mesh, 1 );
		var reach = Span( studio, 1 );

		// Against the face's OWN UV reach, because that is all Fit can see and all it claims to use.
		// What "one repeat" costs in world units depends on which convention made the UVs — a
		// primitive's 0..1, an extrude cap's plane coordinates — and Fit is right in both precisely
		// because it never assumes.
		var fit = MaterialScale.Fit( mesh, face, MaterialScale.ScaleFor( studio, 1 ), 1f );

		Report.Check( "one repeat across the face is the face's own reach",
			Close( fit.x, reach.x ) && Close( fit.y, reach.y ), $"{fit} against {reach}" );

		var twice = MaterialScale.Fit( mesh, face, MaterialScale.ScaleFor( studio, 1 ), 2f );

		Report.Check( "two repeats is half of it",
			Close( twice.x, reach.x / 2f ) && Close( twice.y, reach.y / 2f ), $"{twice}" );

		MaterialScale.SetScale( studio, 1, fit );
		studio.Rebuild();

		var span = Span( studio, 1 );

		Report.Check( "and applying it really does put one repeat on the face",
			Close( span.x, 1f ) && Close( span.y, 1f ), $"{span}" );
	}

	/// <summary>
	/// Fit measures the built mesh, and the built mesh has already had the current scale divided out
	/// of it. Without multiplying that back, fitting a face at 4 units per tile would answer 1 —
	/// the scale would walk down by a factor of itself on every press of the button.
	/// </summary>
	static void TestFitIsStable()
	{
		var studio = Boxed();

		Paint( studio, TopFace( studio ), 1 );
		MaterialScale.SetScale( studio, 1, new Vec2( 4f, 4f ) );
		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;
		var first = MaterialScale.Fit( mesh, FaceOn( mesh, 1 ), MaterialScale.ScaleFor( studio, 1 ), 1f );

		MaterialScale.SetScale( studio, 1, first );
		studio.Rebuild();

		mesh = studio.Bodies.Single().Mesh;
		var second = MaterialScale.Fit( mesh, FaceOn( mesh, 1 ), MaterialScale.ScaleFor( studio, 1 ), 1f );

		Report.Check( "fitting a fitted face is a no-op",
			Close( first.x, second.x ) && Close( first.y, second.y ),
			$"{first} then {second}" );
	}

	static void TestRoundTrip()
	{
		var studio = Boxed();

		studio.MaterialNames[1] = "materials/diner/diner_tile_floor.vmat";
		MaterialScale.SetScale( studio, 1, new Vec2( 48f, 36f ) );

		var text = StudioDocument.Write( studio );
		var back = StudioDocument.Read( text );
		var scale = MaterialScale.ScaleFor( back, 1 );

		Report.Check( "the scale comes back", Close( scale.x, 48f ) && Close( scale.y, 36f ),
			$"{scale}" );

		Report.Check( "a document with no scales writes no line for them",
			!StudioDocument.Write( new PartStudio() ).Contains( "materialscale" ) );

		// A hand-written 1:1 must not come back as a stored entry, or a saved file grows a line
		// every time it is opened and written again.
		var unscaled = StudioDocument.Read( "effigy 1\nmaterialscale 3 1 1\n" );

		Report.Check( "and a 1:1 in the file leaves no entry",
			unscaled.MaterialScales.Count == 0 );
	}

	/// <summary>
	/// A drop that empties a slot retires its name; the size has to go with it. Otherwise the next
	/// material handed that slot number inherits a size chosen for a material that is no longer
	/// there — a wall texture arriving pre-set to a floor tile's 48 units, with nothing on screen
	/// saying why.
	/// </summary>
	static void TestReleasedSlotLosesScale()
	{
		var studio = Boxed();
		var body = studio.Bodies.Single();
		var top = TopFace( studio );
		var reference = FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) );

		MaterialDrop.Drop( studio, body.Id, top, reference, "materials/a.vmat", out var first );
		MaterialScale.SetScale( studio, first, new Vec2( 48f, 48f ) );

		studio.Rebuild();
		body = studio.Bodies.Single();
		top = TopFace( studio );
		reference = FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) );

		MaterialDrop.Drop( studio, body.Id, top, reference, "materials/b.vmat", out var second,
			out var released );

		Report.Check( "the first slot was retired", released == first, $"released {released}" );

		Report.Check( "and its size went with it",
			!studio.MaterialScales.ContainsKey( first ),
			string.Join( ", ", studio.MaterialScales.Select( kv => $"{kv.Key}={kv.Value}" ) ) );

		Report.Check( "the material that landed is at its own default",
			Close( MaterialScale.ScaleFor( studio, second ).x, 1f ) );
	}

	// --- helpers ---------------------------------------------------------------------------------

	/// <summary>A 4 x 3 x 2 box, so the top face's UVs span 4 by 3 and nothing is square enough to
	/// hide an axis swap.</summary>
	static PartStudio Boxed()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 2f;

		studio.Rebuild();

		return studio;
	}

	static int TopFace( PartStudio studio ) =>
		FaceIndexFacing( studio.Bodies.Single().Mesh, new Vec3( 0, 0, 1 ) );

	static int FaceIndexFacing( PolyMesh mesh, Vec3 direction )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[i] ), direction.Normal ) > 0.99f )
				return i;
		}

		return -1;
	}

	/// <summary>Put a face on a slot through the feature tree, the way the editor does, so the
	/// assignment survives the rebuilds these tests lean on.</summary>
	static void Paint( PartStudio studio, int faceIndex, int slot )
	{
		var body = studio.Bodies.Single();

		FaceMaterialEdit.Assign( studio, body.Id, faceIndex,
			FacePlane.Capture( body, faceIndex, body.Mesh.FaceCentroid( body.Mesh.Faces[faceIndex] ) ),
			slot );
	}

	static int FaceOn( PolyMesh mesh, int slot )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( mesh.Faces[i].Material == slot )
				return i;
		}

		return -1;
	}

	/// <summary>How far the UVs of the first face on a slot reach, which is the number a scale
	/// changes and the one a texture's apparent size is.</summary>
	static Vec2 Span( PartStudio studio, int slot )
	{
		var mesh = studio.Bodies.Single().Mesh;

		return SpanOf( mesh, FaceOn( mesh, slot ) );
	}

	static Vec2 SpanOf( PolyMesh mesh, int faceIndex )
	{
		var face = mesh.Faces[faceIndex];

		var minU = float.MaxValue;
		var minV = float.MaxValue;
		var maxU = float.MinValue;
		var maxV = float.MinValue;

		foreach ( var uv in face.UVs )
		{
			minU = MathF.Min( minU, uv.x );
			minV = MathF.Min( minV, uv.y );
			maxU = MathF.Max( maxU, uv.x );
			maxV = MathF.Max( maxV, uv.y );
		}

		return new Vec2( maxU - minU, maxV - minV );
	}

	static Vec2[] UVs( PartStudio studio ) =>
		studio.Bodies.SelectMany( b => b.Mesh.Faces ).SelectMany( f => f.UVs ).ToArray();

	static bool Same( Vec2[] a, Vec2[] b ) =>
		a.Length == b.Length && a.Zip( b ).All( p => Close( p.First.x, p.Second.x ) && Close( p.First.y, p.Second.y ) );

	static bool Close( float a, float b ) => MathF.Abs( a - b ) < 1e-4f;
}
