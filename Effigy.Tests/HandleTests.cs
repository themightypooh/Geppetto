using System;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The grips that sit on a curve rather than at its ends — see SketchHandles.
///
/// These are here rather than read in the viewport for the usual reason: the editor assembly does
/// not compile outside s&amp;box, so anything verified by eye up there is verified once, by whoever
/// wrote it. The maths that matters is a circumcentre, an axis projection and a sweep, and all
/// three are wrong in ways that still LOOK like an arc on screen — a bulge that quietly takes the
/// long way round the circle is a plausible drawing of the wrong thing.
/// </summary>
public static class HandleTests
{
	public static void Run()
	{
		Report.Section( "curve handles: where the grips are" );
		TestWhereTheGripsAre();

		Report.Section( "curve handles: dragging a line moves the whole line" );
		TestDraggingALine();

		Report.Section( "curve handles: dragging an arc changes its bulge and nothing else" );
		TestDraggingAnArc();

		Report.Section( "curve handles: circles and ellipses" );
		TestCircleAndEllipse();

		Report.Section( "curve handles: what a drag refuses" );
		TestRefusals();
	}

	const float Tolerance = 1e-3f;

	static bool Near( float a, float b, float tolerance = Tolerance ) => MathF.Abs( a - b ) <= tolerance;

	static bool Near( Vec2 a, Vec2 b, float tolerance = Tolerance ) => (a - b).Length <= tolerance;

	static float ToSegment( Vec2 p, Vec2 a, Vec2 b )
	{
		var along = b - a;
		var lengthSquared = along.LengthSquared;

		if ( lengthSquared < 1e-12f )
			return (p - a).Length;

		var t = Math.Clamp( Vec2.Dot( p - a, along ) / lengthSquared, 0f, 1f );

		return (p - (a + along * t)).Length;
	}

	static void TestWhereTheGripsAre()
	{
		var sketch = new Sketch();

		var line = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 2 ) );
		var circle = sketch.AddCircle( new Vec2( 10, 0 ), 3f );
		var ellipse = sketch.AddEllipse( new Vec2( 0, 10 ), new Vec2( 4, 10 ), 2f );
		var spline = sketch.AddSpline( false, new Vec2( -5, 0 ), new Vec2( -6, 2 ), new Vec2( -7, 0 ) );

		var handles = SketchHandles.For( sketch );

		Report.Check( "one handle per curve that has something to drive", handles.Count == 3,
			$"got {handles.Count}" );

		Report.Check( "a spline gets none - its shape is its own points, and they already drag",
			handles.All( h => h.CurveId != spline.Id ) );

		var lineHandle = handles.Single( h => h.CurveId == line.Id );

		Report.Check( "the line's grip is at its middle", Near( lineHandle.At, new Vec2( 2, 1 ) ),
			$"{lineHandle.At.x}, {lineHandle.At.y}" );

		var circleHandle = handles.Single( h => h.CurveId == circle.Id );

		Report.Check( "the circle's grip is on its rim", Near( circleHandle.At, new Vec2( 13, 0 ) ),
			$"{circleHandle.At.x}, {circleHandle.At.y}" );

		var ellipseHandle = handles.Single( h => h.CurveId == ellipse.Id );

		Report.Check( "the ellipse's grip is a quarter turn from its major axis",
			Near( ellipseHandle.At, new Vec2( 0, 12 ) ), $"{ellipseHandle.At.x}, {ellipseHandle.At.y}" );

		// A ROTATED ELLIPSE IS THE CASE THAT CATCHES A HARD-CODED AXIS. Its major point is up and
		// to the right, so the minor grip must be up and to the LEFT by the minor radius - not
		// straight up, which is what an axis read off the world rather than off the point gives.
		var turned = new Sketch();
		var diagonal = turned.AddEllipse( Vec2.Zero, new Vec2( 3f, 3f ), 2f );
		var turnedHandle = SketchHandles.For( turned ).Single();
		var expected = new Vec2( -MathF.Sqrt( 2f ), MathF.Sqrt( 2f ) );

		Report.Check( "and follows the major axis when the ellipse is turned",
			Near( turnedHandle.At, expected ), $"{turnedHandle.At.x}, {turnedHandle.At.y}" );

		Report.Check( "the turned grip is still a minor radius from the centre",
			Near( turnedHandle.At.Length, diagonal.MinorRadius ) );

		// The arc grip has to be ON the arc, which for a quarter turn from east to north is the
		// 45-degree point - not the middle of the chord, and not the far side of the circle.
		var arcSketch = new Sketch();
		var c = arcSketch.AddPoint( 0, 0 );
		var start = arcSketch.AddPoint( 2, 0 );
		var end = arcSketch.AddPoint( 0, 2 );
		arcSketch.Add( new SketchArc( c, start, end ) );

		var arcHandle = SketchHandles.For( arcSketch ).Single();
		var diagonalPoint = new Vec2( MathF.Sqrt( 2f ), MathF.Sqrt( 2f ) );

		Report.Check( "the arc's grip is the middle of the arc itself",
			Near( arcHandle.At, diagonalPoint ), $"{arcHandle.At.x}, {arcHandle.At.y}" );

		// Same two endpoints, the other way round the circle. The grip must move with it, or the
		// bulge of a clockwise arc is dragged by something sitting on the arc it is not.
		var back = new Sketch();
		var bc = back.AddPoint( 0, 0 );
		var bs = back.AddPoint( 2, 0 );
		var be = back.AddPoint( 0, 2 );
		back.Add( new SketchArc( bc, bs, be, clockwise: true ) );

		var backHandle = SketchHandles.For( back ).Single();

		Report.Check( "and follows the sweep when the arc goes the other way",
			Near( backHandle.At, diagonalPoint * -1f ), $"{backHandle.At.x}, {backHandle.At.y}" );
	}

	static void TestDraggingALine()
	{
		var sketch = new Sketch();
		var line = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );

		var moved = SketchHandles.Drag( sketch, line.Id, CurveHandleKind.LineMiddle, new Vec2( 2, 3 ) );

		Report.Check( "the drag reports a change", moved );

		Report.Check( "both ends moved by the same amount",
			Near( sketch.Points[line.Start], new Vec2( 0, 3 ) ) && Near( sketch.Points[line.End], new Vec2( 4, 3 ) ),
			$"{sketch.Points[line.Start].x},{sketch.Points[line.Start].y} -> {sketch.Points[line.End].x},{sketch.Points[line.End].y}" );

		Report.Check( "the middle landed exactly where it was dragged",
			Near( SketchHandles.For( sketch ).Single().At, new Vec2( 2, 3 ) ) );

		// A WALL OF A CLOSED PROFILE. Its corners are shared with the two walls either side, so
		// dragging it must take their ends with it and leave the loop closed - the whole reason
		// SketchCurve stores point indices rather than copies.
		var square = new Sketch();
		var walls = square.AddRectangle( new Vec2( 0, 0 ), new Vec2( 4, 4 ) );
		var top = walls.First( w => Near( (square.Points[w.Start] + square.Points[w.End]) / 2f, new Vec2( 2, 4 ) ) );

		SketchHandles.Drag( square, top.Id, CurveHandleKind.LineMiddle, new Vec2( 2, 6 ) );

		var found = ProfileFinder.Find( square );

		Report.Check( "dragging one wall of a rectangle keeps the profile closed",
			found.Profiles.Count == 1 && found.OpenChains == 0,
			$"{found.Profiles.Count} regions, {found.OpenChains} open chains" );

		Report.Check( "and the rectangle got taller rather than coming apart",
			square.Points.Count == 4 && Near( square.Points.Max( p => p.y ), 6f ),
			$"{square.Points.Count} points, top at {square.Points.Max( p => p.y )}" );
	}

	static void TestDraggingAnArc()
	{
		var sketch = new Sketch();
		var centre = sketch.AddPoint( 0, 0 );
		var start = sketch.AddPoint( 2, 0 );
		var end = sketch.AddPoint( -2, 0 );
		var arc = sketch.Add( new SketchArc( centre, start, end ) );

		var before = (Start: sketch.Points[start], End: sketch.Points[end]);

		// Flatten it: a bulge of 1 over a chord of 4 is a much bigger circle than the half-circle
		// it starts as.
		var target = new Vec2( 0, 1 );

		Report.Check( "the drag reports a change",
			SketchHandles.Drag( sketch, arc.Id, CurveHandleKind.ArcBulge, target ) );

		Report.Check( "the endpoints did not move",
			Near( sketch.Points[start], before.Start ) && Near( sketch.Points[end], before.End ) );

		var c = sketch.Points[centre];
		var radius = (sketch.Points[start] - c).Length;

		Report.Check( "the arc now passes through the point it was dragged to",
			Near( (target - c).Length, radius ), $"radius {radius}, target at {(target - c).Length}" );

		// The solver adds this as an implicit rule on every arc, so an arc that does not already
		// satisfy it is one the first constraint anyone applies will visibly kink.
		Report.Check( "and both endpoints are still the same distance from the centre",
			Near( (sketch.Points[end] - c).Length, radius ) );

		Report.Check( "a flatter arc has its centre pushed away from the chord", c.y < -1f,
			$"centre at {c.x}, {c.y}" );

		// ACROSS THE CHORD. The same circle through the same two endpoints is two arcs, and
		// dragging the bulge to the other side must give the near one rather than the long way
		// round - which is what happens if the direction flag is left alone.
		var flipped = SketchHandles.Drag( sketch, arc.Id, CurveHandleKind.ArcBulge, new Vec2( 0, -1 ) );

		Report.Check( "dragging the bulge across the chord flips the arc rather than inverting it",
			flipped && arc.Clockwise );

		var tessellated = arc.Tessellate( sketch, 0.001f );

		// AGAINST THE SEGMENTS, NOT THE SAMPLES. A polyline's vertices are as far apart as the
		// tessellation tolerance allows, so the nearest VERTEX to a point exactly on the arc is
		// half a segment away and always will be - measuring that way fails a correct arc and
		// tightening the tolerance only moves the number.
		var nearest = Enumerable.Range( 0, tessellated.Count - 1 )
			.Min( i => ToSegment( new Vec2( 0, -1 ), tessellated[i], tessellated[i + 1] ) );

		Report.Check( "and the drawn arc really does pass through the cursor", Near( nearest, 0f, 0.01f ),
			$"nearest approach {nearest}" );

		Report.Check( "the flipped arc still ends where it started",
			Near( tessellated[0], before.Start ) && Near( tessellated[^1], before.End ) );
	}

	static void TestCircleAndEllipse()
	{
		var sketch = new Sketch();
		var circle = sketch.AddCircle( new Vec2( 1, 1 ), 2f );

		Report.Check( "dragging the rim sets the radius to the distance from the centre",
			SketchHandles.Drag( sketch, circle.Id, CurveHandleKind.CircleRim, new Vec2( 1, 6 ) )
			&& Near( circle.Radius, 5f ), $"radius {circle.Radius}" );

		Report.Check( "the centre stayed put", Near( sketch.Points[circle.Center], new Vec2( 1, 1 ) ) );

		var ellipse = sketch.AddEllipse( new Vec2( 0, 0 ), new Vec2( 4, 0 ), 2f );
		var major = sketch.Points[ellipse.MajorPoint];

		Report.Check( "dragging the minor grip sets the minor radius",
			SketchHandles.Drag( sketch, ellipse.Id, CurveHandleKind.EllipseMinor, new Vec2( 0, 3 ) )
			&& Near( ellipse.MinorRadius, 3f ), $"minor {ellipse.MinorRadius}" );

		Report.Check( "the major axis is untouched by it",
			Near( sketch.Points[ellipse.MajorPoint], major ) );

		// ONLY THE COMPONENT ACROSS THE AXIS COUNTS. Taking the raw distance would read this drag -
		// a long way along the major axis and a whisker off it - as a huge minor radius.
		Report.Check( "sliding the grip along the major axis barely moves the minor radius",
			SketchHandles.Drag( sketch, ellipse.Id, CurveHandleKind.EllipseMinor, new Vec2( 20, 3 ) ) == false
			|| Near( ellipse.MinorRadius, 3f ), $"minor {ellipse.MinorRadius}" );
	}

	static void TestRefusals()
	{
		var sketch = new Sketch();
		var centre = sketch.AddPoint( 0, 0 );
		var start = sketch.AddPoint( 2, 0 );
		var end = sketch.AddPoint( -2, 0 );
		var arc = sketch.Add( new SketchArc( centre, start, end ) );

		var before = sketch.Points[centre];

		Report.Check( "an arc bulged flat onto its own chord is refused",
			!SketchHandles.Drag( sketch, arc.Id, CurveHandleKind.ArcBulge, new Vec2( 0, 0.0000001f ) )
			&& Near( sketch.Points[centre], before ) );

		var circle = sketch.AddCircle( new Vec2( 5, 5 ), 2f );

		Report.Check( "a circle dragged to zero radius is refused",
			!SketchHandles.Drag( sketch, circle.Id, CurveHandleKind.CircleRim, new Vec2( 5, 5 ) )
			&& Near( circle.Radius, 2f ) );

		Report.Check( "a drag against a curve id that is gone is refused",
			!SketchHandles.Drag( sketch, "nosuchid", CurveHandleKind.LineMiddle, new Vec2( 1, 1 ) ) );

		var line = sketch.AddLine( new Vec2( 0, 9 ), new Vec2( 2, 9 ) );

		Report.Check( "a drag that asks for no movement is refused",
			!SketchHandles.Drag( sketch, line.Id, CurveHandleKind.LineMiddle, new Vec2( 1, 9 ) ) );

		// THE PIN IS A POINT THE DRAG IS NOT MOVING. Pinning something the drag moves would fight
		// the hand: the solver holds it still while the cursor pulls it, and the sketch shears.
		Report.Check( "an arc drag pins one of the endpoints it keeps still",
			SketchHandles.Pin( sketch, arc.Id, CurveHandleKind.ArcBulge ) == arc.Start );

		Report.Check( "a circle drag pins its centre",
			SketchHandles.Pin( sketch, circle.Id, CurveHandleKind.CircleRim ) == circle.Center );
	}
}
