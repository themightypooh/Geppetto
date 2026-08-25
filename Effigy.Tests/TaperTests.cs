using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Draft angle, and a second distance the other way.
///
/// Both are Onshape parameters that needed no boolean and had simply never been built. The second
/// distance is bookkeeping; the taper is the one with something to get wrong, and what it can get
/// wrong is subtle: push each vertex along its own bisector and every corner that is not a right
/// angle ends up a different distance from its own edges, so the draft varies around the profile
/// while looking entirely plausible in a render.
///
/// The test that catches that is not the volume — it is measuring the LEAN OF EVERY WALL on a shape
/// whose corners are not 90 degrees. Edge-based offset gives them all the same angle; the bisector
/// shortcut does not, and no picture would tell you.
/// </summary>
public static class TaperTests
{
	public static void Run()
	{
		Report.Section( "taper: the solid is the frustum it should be" );
		TestVolume();

		Report.Section( "taper: every wall leans by the angle asked for" );
		TestWallAngles();

		Report.Section( "taper: refusing what will not fit" );
		TestTooSteep();

		Report.Section( "taper: holes open up as the part narrows" );
		TestWithHoles();

		Report.Section( "extrude: a second distance the other way" );
		TestSecondDistance();
	}

	static void TestVolume()
	{
		// A 2x2 square, 1 deep, drafted so the top insets by 0.5 on each edge — atan(0.5), which is
		// where tan comes out at exactly a half.
		var angle = MathF.Atan( 0.5f ) * 180f / MathF.PI;
		var studio = Square( 2f, 1f, angle, out var extrude );

		var report = studio.Rebuild();

		Report.Check( "a tapered extrude builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		var mesh = studio.Bodies.Single().Mesh;

		// Frustum: h/3 × (A + a + sqrt(Aa)), with a 2x2 bottom and a 1x1 top. 1/3 × (4 + 1 + 2).
		var expected = (4f + 1f + 2f) / 3f;

		Report.Check( "and measures as the frustum, not the prism",
			MathF.Abs( Volume( mesh ) - expected ) < 1e-3f,
			$"{Volume( mesh ):0.#####}, expected {expected:0.#####} (a prism would be 4)" );

		Report.Check( "it winds outward", Volume( mesh ) > 0f );
		Report.Check( "and is closed", MeshValidator.Validate( mesh ) is { IsValid: true, IsClosed: true } );

		// The top really is 1x1, which is what makes the volume the right answer for the right
		// reason rather than a coincidence of two errors.
		var top = mesh.Positions.Where( p => p.z > 0.5f ).ToList();

		Report.Check( "the far cap is inset by the draft",
			MathF.Abs( top.Max( p => p.x ) - 0.5f ) < 1e-3f && MathF.Abs( top.Min( p => p.x ) + 0.5f ) < 1e-3f,
			$"x spans {top.Min( p => p.x ):0.###} to {top.Max( p => p.x ):0.###}, expected -0.5 to 0.5" );

		var bottom = mesh.Positions.Where( p => p.z < 0.5f ).ToList();

		Report.Check( "while the near cap is untouched",
			MathF.Abs( bottom.Max( p => p.x ) - 1f ) < 1e-3f,
			$"x reaches {bottom.Max( p => p.x ):0.###}, expected 1" );

		// A negative angle drafts the other way, which is what you want for a part that is wider at
		// the top than the bottom.
		var wide = Square( 2f, 1f, -angle, out _ );
		wide.Rebuild();

		var wideVolume = Volume( wide.Bodies.Single().Mesh );
		var wideExpected = (4f + 9f + 6f) / 3f;

		Report.Check( "a negative taper widens instead",
			MathF.Abs( wideVolume - wideExpected ) < 1e-3f,
			$"{wideVolume:0.#####}, expected {wideExpected:0.#####}" );

		// Zero has to be exactly the old behaviour, since every existing model relies on it.
		var straight = Square( 2f, 1f, 0f, out _ );
		straight.Rebuild();

		Report.Check( "zero taper is still a plain prism",
			MathF.Abs( Volume( straight.Bodies.Single().Mesh ) - 4f ) < 1e-4f
			&& straight.Bodies.Single().Mesh.FaceCount == 6,
			$"{Volume( straight.Bodies.Single().Mesh ):0.####}, {straight.Bodies.Single().Mesh.FaceCount} faces" );
	}

	static void TestWallAngles()
	{
		// A HEXAGON, because its corners are 120 degrees rather than 90. This is the shape that
		// separates an edge-based offset from a vertex-bisector one: offset along the bisectors and
		// every wall still leans, but not all by the same amount, and the part measures wrong while
		// looking right.
		const float taper = 12f;

		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		var corners = new Vec2[6];

		for ( var i = 0; i < 6; i++ )
		{
			var a = i * MathF.PI / 3f;
			corners[i] = new Vec2( MathF.Cos( a ) * 3f, MathF.Sin( a ) * 3f );
		}

		sketch.Sketch.AddPolygon( corners );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 2f;
		extrude.Taper.Value = taper;

		var report = studio.Rebuild();

		Report.Check( "a tapered hexagon builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		var mesh = studio.Bodies.Single().Mesh;
		var up = new Vec3( 0, 0, 1 );
		var leans = new List<float>();

		foreach ( var face in mesh.Faces )
		{
			var normal = mesh.FaceNormal( face );

			// Caps point along Z; anything else is a wall.
			if ( MathF.Abs( Vec3.Dot( normal, up ) ) > 0.9f )
				continue;

			// A narrowing extrude tips each wall's outward normal upward by exactly the draft angle,
			// so the sine of the lean is the normal's own Z.
			leans.Add( MathF.Asin( Math.Clamp( Vec3.Dot( normal, up ), -1f, 1f ) ) * 180f / MathF.PI );
		}

		Report.Check( "the hexagon has six walls", leans.Count == 6, $"{leans.Count} walls" );

		var worst = leans.Count == 0 ? 0f : leans.Max( l => MathF.Abs( l - taper ) );

		Report.Check( $"and every one leans exactly {taper} degrees", worst < 0.05f,
			leans.Count == 0 ? "no walls" : $"worst was {leans.OrderByDescending( l => MathF.Abs( l - taper ) ).First():0.###}" );

		// AN L-SHAPE, which adds a reflex corner — 270 degrees inside. A bisector offset gets a
		// reflex corner wrong in the other direction from a sharp one, so this catches the sign as
		// well as the magnitude.
		var lStudio = new PartStudio();
		var ls = lStudio.Add( new SketchFeature() );
		ls.Sketch.AddPolygon(
			new Vec2( 0, 0 ), new Vec2( 6, 0 ), new Vec2( 6, 2 ),
			new Vec2( 2, 2 ), new Vec2( 2, 6 ), new Vec2( 0, 6 ) );

		var lExtrude = lStudio.Add( new ExtrudeFeature() );
		lExtrude.Distance.Value = 1f;
		lExtrude.Taper.Value = taper;

		var lReport = lStudio.Rebuild();

		Report.Check( "an L with a reflex corner tapers too", !lReport.HasErrors, lReport.ToString() );

		if ( lReport.HasErrors )
			return;

		var lMesh = lStudio.Bodies.Single().Mesh;
		var lLeans = lMesh.Faces
			.Select( f => mesh.FaceNormal( f ) )
			.ToList();

		var lWorst = 0f;

		foreach ( var face in lMesh.Faces )
		{
			var normal = lMesh.FaceNormal( face );

			if ( MathF.Abs( Vec3.Dot( normal, up ) ) > 0.9f )
				continue;

			var lean = MathF.Asin( Math.Clamp( Vec3.Dot( normal, up ), -1f, 1f ) ) * 180f / MathF.PI;
			lWorst = MathF.Max( lWorst, MathF.Abs( lean - taper ) );
		}

		Report.Check( "including across the reflex corner", lWorst < 0.05f, $"worst off by {lWorst:0.###}" );
	}

	static void TestTooSteep()
	{
		// 45 degrees over a distance of 1 insets a 2x2 square by exactly 1 on each edge, which
		// leaves nothing. A solid with a zero-area cap is not a solid, and building one would put a
		// degenerate body into the tree that every later feature would trip over.
		var studio = Square( 2f, 1f, 45f, out var extrude );
		var report = studio.Rebuild();

		Report.Check( "a taper that collapses the profile is refused", report.HasErrors, "it built something" );

		Report.Check( "and the error says what to change",
			extrude.Error is not null && extrude.Error.Contains( "taper" ) && extrude.Error.Contains( "shallower" ),
			extrude.Error ?? "no error" );

		// Past collapse, where a naive offset turns the loop inside out and produces a solid with
		// negative volume that looks perfectly normal in wireframe.
		var inverted = Square( 2f, 2f, 60f, out var invertedExtrude );
		inverted.Rebuild();

		Report.Check( "so is one that would turn the profile inside out",
			invertedExtrude.Error is not null, "it built something" );

		// Just inside the limit still has to work, or the guard is too eager and the parameter is
		// unusable near the angles anyone would actually pick.
		var tight = Square( 2f, 1f, 43f, out var tightExtrude );
		tight.Rebuild();

		Report.Check( "a steep but survivable taper still builds", tightExtrude.Error is null,
			tightExtrude.Error );

		if ( tightExtrude.Error is null )
		{
			Report.Check( "with a small but real top cap",
				Volume( tight.Bodies.Single().Mesh ) > 0.1f,
				$"{Volume( tight.Bodies.Single().Mesh ):0.####}" );
		}
	}

	static void TestWithHoles()
	{
		// Draft shrinks the SECTION, so a hole in a narrowing part gets wider going up — the
		// material between the outer wall and the hole thins from both sides. That falls out of the
		// offset being winding-relative, since a hole is wound the other way, and it is worth
		// pinning because getting it backwards produces a part that looks drafted and has an
		// undercut in it.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -4, -4 ), new Vec2( 4, 4 ) );
		sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 1.5f );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 2f;
		extrude.Taper.Value = 10f;

		var report = studio.Rebuild();

		Report.Check( "a tapered plate with a hole builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "it is still genus 1", MeshValidator.EulerCharacteristic( mesh ) == 0,
			$"X = {MeshValidator.EulerCharacteristic( mesh )}" );

		Report.Check( "and still closed and valid",
			MeshValidator.Validate( mesh ) is { IsValid: true, IsClosed: true } );

		// The hole's radius at each end, measured from the points near the axis.
		var lowHole = mesh.Positions.Where( p => p.z < 0.5f )
			.Select( p => new Vec2( p.x, p.y ).Length ).Where( r => r < 3f ).ToList();

		var highHole = mesh.Positions.Where( p => p.z > 1.5f )
			.Select( p => new Vec2( p.x, p.y ).Length ).Where( r => r < 3f ).ToList();

		Report.Check( "both ends of the hole are there",
			lowHole.Count > 8 && highHole.Count > 8, $"{lowHole.Count} / {highHole.Count}" );

		if ( lowHole.Count > 0 && highHole.Count > 0 )
		{
			var expected = 1.5f + 2f * MathF.Tan( 10f * MathF.PI / 180f );

			Report.Check( "and the hole opens out as the part narrows",
				MathF.Abs( highHole.Average() - expected ) < 0.02f,
				$"{lowHole.Average():0.####} at the bottom, {highHole.Average():0.####} at the top, expected {expected:0.####}" );
		}

		// The outer wall goes the other way over the same distance.
		var lowOuter = mesh.Positions.Where( p => p.z < 0.5f ).Max( p => p.x );
		var highOuter = mesh.Positions.Where( p => p.z > 1.5f ).Max( p => p.x );

		Report.Check( "while the outside comes in", highOuter < lowOuter - 0.3f,
			$"{lowOuter:0.####} to {highOuter:0.####}" );
	}

	static void TestSecondDistance()
	{
		// 2 up and 1 down from the sketch plane: a 3-tall solid spanning -1 to 2, which is a thing
		// a symmetric checkbox cannot express at all.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 2f;
		extrude.SecondDistance.Value = 1f;

		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "it spans both sides of the sketch plane",
			MathF.Abs( mesh.Positions.Max( p => p.z ) - 2f ) < 1e-4f
			&& MathF.Abs( mesh.Positions.Min( p => p.z ) + 1f ) < 1e-4f,
			$"{mesh.Positions.Min( p => p.z ):0.###} to {mesh.Positions.Max( p => p.z ):0.###}" );

		Report.Check( "with the volume of all three units of height",
			MathF.Abs( Volume( mesh ) - 12f ) < 1e-3f, $"{Volume( mesh ):0.####}" );

		// Symmetric is the simpler intent, so it wins rather than the two silently compounding.
		extrude.Symmetric.Value = true;
		studio.MarkDirty( extrude );
		studio.Rebuild();

		var symmetric = studio.Bodies.Single().Mesh;

		Report.Check( "symmetric overrides it rather than adding to it",
			MathF.Abs( symmetric.Positions.Max( p => p.z ) - 1f ) < 1e-4f
			&& MathF.Abs( symmetric.Positions.Min( p => p.z ) + 1f ) < 1e-4f,
			$"{symmetric.Positions.Min( p => p.z ):0.###} to {symmetric.Positions.Max( p => p.z ):0.###}" );

		// Flip mirrors the whole arrangement, second distance included, rather than only the first.
		extrude.Symmetric.Value = false;
		extrude.Flip.Value = true;
		studio.MarkDirty( extrude );
		studio.Rebuild();

		var flipped = studio.Bodies.Single().Mesh;

		Report.Check( "flip mirrors both ends",
			MathF.Abs( flipped.Positions.Min( p => p.z ) + 2f ) < 1e-4f
			&& MathF.Abs( flipped.Positions.Max( p => p.z ) - 1f ) < 1e-4f,
			$"{flipped.Positions.Min( p => p.z ):0.###} to {flipped.Positions.Max( p => p.z ):0.###}" );

		// Zero is off, which is what every model built before this parameter existed relies on.
		extrude.Flip.Value = false;
		extrude.SecondDistance.Value = 0f;
		studio.MarkDirty( extrude );
		studio.Rebuild();

		Report.Check( "zero means one-sided, as before",
			MathF.Abs( studio.Bodies.Single().Mesh.Positions.Min( p => p.z ) ) < 1e-4f );
	}

	// --- helpers ------------------------------------------------------------------------------

	static PartStudio Square( float size, float distance, float taper, out ExtrudeFeature extrude )
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		var half = size * 0.5f;

		sketch.Sketch.AddRectangle( new Vec2( -half, -half ), new Vec2( half, half ) );

		extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = distance;
		extrude.Taper.Value = taper;

		return studio;
	}

	static float Volume( PolyMesh mesh )
	{
		var acc = 0f;

		foreach ( var f in mesh.Faces )
			acc += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		return acc / 3f;
	}
}
