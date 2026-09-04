using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Sketches where more than two curves meet at a point.
///
/// Drawing a line across a shape to divide it is how anyone splits a profile, and it used to be
/// reported as "branching sketches are not supported yet" — the whole sketch fell back to whatever
/// loops happened not to touch the branch. The fix is the planar face traversal ProfileFinder's own
/// comment had been describing as the upgrade path for a while.
///
/// What these tests are really checking is that the traversal picks the RIGHT face at each branch.
/// A walk that turns the wrong way at a junction still terminates and still produces closed loops —
/// they are just the wrong loops, made of the wrong halves, and they extrude into plausible solids.
/// So every case here asserts on areas that are known in advance, not merely on counts.
/// </summary>
public static class BranchTests
{
	public static void Run()
	{
		Report.Section( "branching: a line across a rectangle makes two regions" );
		TestSplitRectangle();

		Report.Section( "branching: more than one cut, and cuts that meet" );
		TestMultipleCuts();

		Report.Section( "branching: a line across a rectangle that was never split at the edges" );
		TestUnsplitCuts();

		Report.Section( "branching: arcs at a junction" );
		TestArcJunction();

		Report.Section( "branching: what still does not enclose anything" );
		TestOpenGeometry();

		Report.Section( "branching: unbranched sketches are unchanged" );
		TestNoRegression();
	}

	static void TestSplitRectangle()
	{
		// A 4x2 rectangle with a line down the middle. Two 2x2 halves — the case the old finder
		// refused outright.
		var sketch = new Sketch();
		var bottomLeft = sketch.AddPoint( 0, 0 );
		var bottomMid = sketch.AddPoint( 2, 0 );
		var bottomRight = sketch.AddPoint( 4, 0 );
		var topRight = sketch.AddPoint( 4, 2 );
		var topMid = sketch.AddPoint( 2, 2 );
		var topLeft = sketch.AddPoint( 0, 2 );

		sketch.Add( new SketchLine( bottomLeft, bottomMid ) );
		sketch.Add( new SketchLine( bottomMid, bottomRight ) );
		sketch.Add( new SketchLine( bottomRight, topRight ) );
		sketch.Add( new SketchLine( topRight, topMid ) );
		sketch.Add( new SketchLine( topMid, topLeft ) );
		sketch.Add( new SketchLine( topLeft, bottomLeft ) );
		sketch.Add( new SketchLine( bottomMid, topMid ) );

		var found = ProfileFinder.Find( sketch );

		Report.Check( "it finds two regions, not one and a complaint",
			found.Profiles.Count == 2, $"{found.Profiles.Count} regions, warnings: {string.Join( "; ", found.Warnings )}" );

		Report.Check( "and says nothing was skipped", found.Warnings.Count == 0,
			string.Join( "; ", found.Warnings ) );

		if ( found.Profiles.Count != 2 )
			return;

		// EACH HALF IS 4, NOT ONE OF THEM 8. A traversal that turned the wrong way at the junction
		// would happily return the whole rectangle plus a sliver, and both would be closed loops.
		var areas = found.Profiles.Select( p => MathF.Abs( ProfileFinder.SignedArea( p.Outer ) ) ).OrderBy( a => a ).ToList();

		Report.Check( "both halves measure 4",
			MathF.Abs( areas[0] - 4f ) < 1e-3f && MathF.Abs( areas[1] - 4f ) < 1e-3f,
			string.Join( ", ", areas.Select( a => a.ToString( "0.####" ) ) ) );

		Report.Check( "and both come back counter-clockwise",
			found.Profiles.All( p => ProfileFinder.SignedArea( p.Outer ) > 0f ) );

		// End to end: two regions means two bodies, and their volumes are the two halves.
		var studio = new PartStudio();
		var feature = studio.Add( new SketchFeature() );
		feature.Sketch = sketch;

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;
		extrude.Result.Index = 1; // keep them apart so each can be measured

		var report = studio.Rebuild();

		Report.Check( "it extrudes without error", !report.HasErrors, report.ToString() );

		Report.Check( "into two bodies", studio.Bodies.Count == 2, $"{studio.Bodies.Count} bodies" );

		Report.Check( "of 4 units each",
			studio.Bodies.All( b => MathF.Abs( Volume( b.Mesh ) - 4f ) < 1e-3f ),
			string.Join( ", ", studio.Bodies.Select( b => Volume( b.Mesh ).ToString( "0.####" ) ) ) );
	}

	static void TestMultipleCuts()
	{
		// A 6x2 rectangle cut twice: three 2x2 regions, and the middle one is bounded by two cuts
		// rather than by any original edge on either side.
		var sketch = new Sketch();
		var p = new int[8];
		p[0] = sketch.AddPoint( 0, 0 );
		p[1] = sketch.AddPoint( 2, 0 );
		p[2] = sketch.AddPoint( 4, 0 );
		p[3] = sketch.AddPoint( 6, 0 );
		p[4] = sketch.AddPoint( 6, 2 );
		p[5] = sketch.AddPoint( 4, 2 );
		p[6] = sketch.AddPoint( 2, 2 );
		p[7] = sketch.AddPoint( 0, 2 );

		for ( var i = 0; i < 8; i++ )
			sketch.Add( new SketchLine( p[i], p[(i + 1) % 8] ) );

		sketch.Add( new SketchLine( p[1], p[6] ) );
		sketch.Add( new SketchLine( p[2], p[5] ) );

		var found = ProfileFinder.Find( sketch );

		Report.Check( "two cuts make three regions", found.Profiles.Count == 3,
			$"{found.Profiles.Count} regions" );

		if ( found.Profiles.Count == 3 )
		{
			Report.Check( "each measuring 4",
				found.Profiles.All( f => MathF.Abs( MathF.Abs( ProfileFinder.SignedArea( f.Outer ) ) - 4f ) < 1e-3f ),
				string.Join( ", ", found.Profiles.Select( f => ProfileFinder.SignedArea( f.Outer ).ToString( "0.###" ) ) ) );
		}

		// A CROSS: four curves meeting at one interior point, which is the highest-degree junction a
		// sketch normally produces and the one most likely to be ordered wrongly.
		var cross = new Sketch();
		var corner = new int[4];
		corner[0] = cross.AddPoint( 0, 0 );
		corner[1] = cross.AddPoint( 4, 0 );
		corner[2] = cross.AddPoint( 4, 4 );
		corner[3] = cross.AddPoint( 0, 4 );

		var midBottom = cross.AddPoint( 2, 0 );
		var midRight = cross.AddPoint( 4, 2 );
		var midTop = cross.AddPoint( 2, 4 );
		var midLeft = cross.AddPoint( 0, 2 );
		var centre = cross.AddPoint( 2, 2 );

		cross.Add( new SketchLine( corner[0], midBottom ) );
		cross.Add( new SketchLine( midBottom, corner[1] ) );
		cross.Add( new SketchLine( corner[1], midRight ) );
		cross.Add( new SketchLine( midRight, corner[2] ) );
		cross.Add( new SketchLine( corner[2], midTop ) );
		cross.Add( new SketchLine( midTop, corner[3] ) );
		cross.Add( new SketchLine( corner[3], midLeft ) );
		cross.Add( new SketchLine( midLeft, corner[0] ) );

		cross.Add( new SketchLine( midBottom, centre ) );
		cross.Add( new SketchLine( centre, midTop ) );
		cross.Add( new SketchLine( midLeft, centre ) );
		cross.Add( new SketchLine( centre, midRight ) );

		var crossFound = ProfileFinder.Find( cross );

		Report.Check( "a cross through a square makes four quadrants",
			crossFound.Profiles.Count == 4, $"{crossFound.Profiles.Count} regions" );

		if ( crossFound.Profiles.Count == 4 )
		{
			Report.Check( "each a quarter of it",
				crossFound.Profiles.All( f => MathF.Abs( MathF.Abs( ProfileFinder.SignedArea( f.Outer ) ) - 4f ) < 1e-3f ),
				string.Join( ", ", crossFound.Profiles.Select( f => ProfileFinder.SignedArea( f.Outer ).ToString( "0.###" ) ) ) );

			// Four quadrants, four distinct centres — a traversal that returned the same face twice
			// would pass the count and the area and still be wrong.
			var centres = crossFound.Profiles
				.Select( f => (MathF.Round( f.Outer.Average( v => v.x ), 2 ), MathF.Round( f.Outer.Average( v => v.y ), 2 )) )
				.Distinct()
				.Count();

			Report.Check( "and all four are different regions", centres == 4, $"{centres} distinct centres" );
		}
	}

	/// <summary>
	/// The drawing the user actually produces: a rectangle of four lines, then two more lines
	/// whose ends sit on those edges and whose interiors cross, without sharing a point index
	/// with anything. Coincidence is identity, so the integer walk prunes them as dangling.
	/// Region finding has to recover the four panes anyway — that is the whole of this test.
	/// </summary>
	static void TestUnsplitCuts()
	{
		var chord = UnsplitRectangleWith( vertical: true, horizontal: false );
		var pointsBefore = chord.Points.Count;
		var curvesBefore = chord.Curves.Count;
		var chordFound = ProfileFinder.Find( chord );

		Report.Check( "finding regions does not rewrite the sketch",
			chord.Points.Count == pointsBefore && chord.Curves.Count == curvesBefore,
			$"{chord.Points.Count} pts / {chord.Curves.Count} curves, were {pointsBefore}/{curvesBefore}" );

		Report.Check( "one unsplit chord makes two regions",
			chordFound.Profiles.Count == 2, $"{chordFound.Profiles.Count} regions, warnings: {string.Join( "; ", chordFound.Warnings )}" );

		if ( chordFound.Profiles.Count == 2 )
		{
			Report.Check( "each a half of the rectangle",
				chordFound.Profiles.All( f => MathF.Abs( f.Area - 8f ) < 1e-3f ),
				string.Join( ", ", chordFound.Profiles.Select( f => f.Area.ToString( "0.###" ) ) ) );
		}

		var cross = UnsplitRectangleWith( vertical: true, horizontal: true );
		var crossFound = ProfileFinder.Find( cross );

		Report.Check( "two unsplit lines through a rectangle make four faces",
			crossFound.Profiles.Count == 4, $"{crossFound.Profiles.Count} regions, warnings: {string.Join( "; ", crossFound.Warnings )}" );

		if ( crossFound.Profiles.Count == 4 )
		{
			Report.Check( "each a quarter of it",
				crossFound.Profiles.All( f => MathF.Abs( f.Area - 4f ) < 1e-3f ),
				string.Join( ", ", crossFound.Profiles.Select( f => f.Area.ToString( "0.###" ) ) ) );

			var centres = crossFound.Profiles
				.Select( f => (MathF.Round( f.Outer.Average( v => v.x ), 2 ), MathF.Round( f.Outer.Average( v => v.y ), 2 )) )
				.Distinct()
				.Count();

			Report.Check( "and all four are different regions", centres == 4, $"{centres} distinct centres" );
		}

		Report.Check( "the recovered faces are not overlap lenses",
			crossFound.Profiles.Count( p => p.FromOverlap ) == 0,
			$"{crossFound.Profiles.Count( p => p.FromOverlap )} overlap faces" );

		// End to end: four regions, four bodies.
		var studio = new PartStudio();
		var feature = studio.Add( new SketchFeature() );
		feature.Sketch = UnsplitRectangleWith( vertical: true, horizontal: true );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;
		extrude.Result.Index = 1;

		var report = studio.Rebuild();

		Report.Check( "the unsplit cross extrudes without error", !report.HasErrors, report.ToString() );
		Report.Check( "into four bodies", studio.Bodies.Count == 4, $"{studio.Bodies.Count} bodies" );
		Report.Check( "of 4 units each",
			studio.Bodies.All( b => MathF.Abs( Volume( b.Mesh ) - 4f ) < 1e-3f ),
			string.Join( ", ", studio.Bodies.Select( b => Volume( b.Mesh ).ToString( "0.####" ) ) ) );
	}

	/// <summary>A 4x4 rectangle as four lines, plus optional full-span dividers whose endpoints
	/// sit on the edges without splitting them.</summary>
	static Sketch UnsplitRectangleWith( bool vertical, bool horizontal )
	{
		var sketch = new Sketch();
		sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 4, 4 ) );

		if ( vertical )
			sketch.Add( new SketchLine( sketch.AddPoint( 2, 0 ), sketch.AddPoint( 2, 4 ) ) );

		if ( horizontal )
			sketch.Add( new SketchLine( sketch.AddPoint( 0, 2 ), sketch.AddPoint( 4, 2 ) ) );

		return sketch;
	}

	static void TestArcJunction()
	{
		// A half disc split down the middle: two quarter arcs meeting the vertical radius at the top.
		//
		// The arcs are what make this worth testing. Order the junction by the straight-line
		// direction to each curve's far endpoint rather than by the TANGENT it actually leaves in,
		// and the two arcs sort onto the wrong sides of the vertical line — the walk then leaves
		// along the wrong curve and returns two closed loops that are not these two.
		//
		// NOTE THE ARC IS SPLIT AT THE TOP POINT, and it has to be. A single semicircle from right to
		// left would pass THROUGH (0,2) geometrically while sharing no point index with the line that
		// ends there, and in this kernel coincidence is identity: touching is not joining. The
		// vertical line would have a free end and be pruned, correctly, leaving one half disc. That
		// is the model working as designed, and the first version of this test got it wrong.
		var sketch = new Sketch();
		var left = sketch.AddPoint( -2, 0 );
		var right = sketch.AddPoint( 2, 0 );
		var centre = sketch.AddPoint( 0, 0 );
		var top = sketch.AddPoint( 0, 2 );

		sketch.Add( new SketchArc( centre, right, top ) );
		sketch.Add( new SketchArc( centre, top, left ) );
		sketch.Add( new SketchLine( left, centre ) );
		sketch.Add( new SketchLine( centre, right ) );
		sketch.Add( new SketchLine( centre, top ) );

		var found = ProfileFinder.Find( sketch );

		Report.Check( "an arc meeting lines at a junction splits into two quarters",
			found.Profiles.Count == 2, $"{found.Profiles.Count} regions" );

		if ( found.Profiles.Count != 2 )
			return;

		// Two quarter-discs of radius 2: pi r^2 / 4 each, tessellated so a little under.
		var areas = found.Profiles.Select( f => MathF.Abs( ProfileFinder.SignedArea( f.Outer ) ) ).ToList();
		var quarter = MathF.PI * 4f / 4f;

		Report.Check( "each about a quarter of the disc",
			areas.All( a => MathF.Abs( a - quarter ) < 0.05f ),
			string.Join( ", ", areas.Select( a => a.ToString( "0.####" ) ) ) + $", expected about {quarter:0.####} each" );

		Report.Check( "and together they are the half disc",
			MathF.Abs( areas.Sum() - MathF.PI * 2f ) < 0.05f, $"{areas.Sum():0.####}" );
	}

	static void TestOpenGeometry()
	{
		// Pruning has to repeat. A tail of three curves retracts one at a time: removing the last
		// leaves the second dangling, and so on. A single pass would leave two of them in the graph
		// and put a zero-width spur through the middle of the region.
		var sketch = new Sketch();
		var a = sketch.AddPoint( 0, 0 );
		var b = sketch.AddPoint( 2, 0 );
		var c = sketch.AddPoint( 2, 2 );
		var d = sketch.AddPoint( 0, 2 );

		sketch.Add( new SketchLine( a, b ) );
		sketch.Add( new SketchLine( b, c ) );
		sketch.Add( new SketchLine( c, d ) );
		sketch.Add( new SketchLine( d, a ) );

		var t1 = sketch.AddPoint( 4, 0 );
		var t2 = sketch.AddPoint( 6, 0 );
		var t3 = sketch.AddPoint( 8, 0 );

		sketch.Add( new SketchLine( b, t1 ) );
		sketch.Add( new SketchLine( t1, t2 ) );
		sketch.Add( new SketchLine( t2, t3 ) );

		var found = ProfileFinder.Find( sketch );

		Report.Check( "a tail hanging off a square is pruned entirely",
			found.Profiles.Count == 1, $"{found.Profiles.Count} regions" );

		Report.Check( "leaving the square intact",
			found.Profiles.Count == 1 && MathF.Abs( MathF.Abs( ProfileFinder.SignedArea( found.Profiles[0].Outer ) ) - 4f ) < 1e-3f,
			found.Profiles.Count == 1 ? $"{ProfileFinder.SignedArea( found.Profiles[0].Outer ):0.####}" : "" );

		Report.Check( "and the tail is reported rather than silently dropped",
			found.Warnings.Count > 0 && found.OpenChains == 1,
			$"{found.OpenChains} chains, warnings: {string.Join( "; ", found.Warnings )}" );

		// Two separate tails are two chains, so the message can say the right number.
		var two = new Sketch();
		var x0 = two.AddPoint( 0, 0 );
		var x1 = two.AddPoint( 1, 0 );
		var y0 = two.AddPoint( 5, 0 );
		var y1 = two.AddPoint( 6, 0 );

		two.Add( new SketchLine( x0, x1 ) );
		two.Add( new SketchLine( y0, y1 ) );

		var twoFound = ProfileFinder.Find( two );

		Report.Check( "two disconnected open chains count as two",
			twoFound.OpenChains == 2, $"{twoFound.OpenChains}" );

		Report.Check( "and no regions come out of them", twoFound.Profiles.Count == 0 );
	}

	static void TestNoRegression()
	{
		// Every ordinary sketch has to come out exactly as before. A rectangle is one region and NOT
		// two — the outer infinite face has to be the one that gets dropped, and dropping the wrong
		// one is the failure that would show up here first.
		var rect = new Sketch();
		rect.AddRectangle( new Vec2( 0, 0 ), new Vec2( 3, 2 ) );

		var found = ProfileFinder.Find( rect );

		Report.Check( "a plain rectangle is still exactly one region", found.Profiles.Count == 1,
			$"{found.Profiles.Count} regions" );

		Report.Check( "of the right area and winding",
			found.Profiles.Count == 1 && MathF.Abs( ProfileFinder.SignedArea( found.Profiles[0].Outer ) - 6f ) < 1e-3f,
			found.Profiles.Count == 1 ? $"{ProfileFinder.SignedArea( found.Profiles[0].Outer ):0.####}" : "" );

		// Two separate rectangles are two regions, each with its own outer face to discard.
		var pair = new Sketch();
		pair.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );
		pair.AddRectangle( new Vec2( 5, 0 ), new Vec2( 6, 1 ) );

		Report.Check( "two disconnected rectangles are two regions",
			ProfileFinder.Find( pair ).Profiles.Count == 2 );

		// Nesting still classifies: a square inside a square is a region with a hole, not two
		// regions. The face traversal finds both loops; the depth pass decides what they mean.
		var nested = new Sketch();
		nested.AddRectangle( new Vec2( 0, 0 ), new Vec2( 6, 6 ) );
		nested.AddRectangle( new Vec2( 2, 2 ), new Vec2( 4, 4 ) );

		var nestedFound = ProfileFinder.Find( nested );

		Report.Check( "a square inside a square is one region with a hole",
			nestedFound.Profiles.Count == 1 && nestedFound.Profiles[0].Holes.Count == 1,
			$"{nestedFound.Profiles.Count} regions, {nestedFound.Profiles.FirstOrDefault()?.Holes.Count ?? 0} holes" );

		Report.Check( "and the hole is wound the other way",
			nestedFound.Profiles.Count == 1 && nestedFound.Profiles[0].Holes.Count == 1
			&& ProfileFinder.SignedArea( nestedFound.Profiles[0].Holes[0] ) < 0f );

		// Construction geometry stays out of it, branch or no branch: a construction line across a
		// rectangle must not split anything.
		var construction = new Sketch();
		var c0 = construction.AddPoint( 0, 0 );
		var c1 = construction.AddPoint( 2, 0 );
		var c2 = construction.AddPoint( 2, 2 );
		var c3 = construction.AddPoint( 0, 2 );

		construction.Add( new SketchLine( c0, c1 ) );
		construction.Add( new SketchLine( c1, c2 ) );
		construction.Add( new SketchLine( c2, c3 ) );
		construction.Add( new SketchLine( c3, c0 ) );
		construction.Add( new SketchLine( c0, c2 ) ).Construction = true;

		var constructionFound = ProfileFinder.Find( construction );

		Report.Check( "a construction line across a square does not divide it",
			constructionFound.Profiles.Count == 1
			&& MathF.Abs( ProfileFinder.SignedArea( constructionFound.Profiles[0].Outer ) - 4f ) < 1e-3f,
			$"{constructionFound.Profiles.Count} regions" );
	}

	// --- helpers ------------------------------------------------------------------------------

	static float Volume( PolyMesh mesh ) => mesh.SignedVolume();
}
