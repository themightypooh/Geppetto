using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Trim, extend, fillet and offset — the edits that make a sketcher usable rather than
/// write-only. Before these, a line drawn wrong could only be deleted and drawn again.
///
/// Each one is checked by the GEOMETRY IT PROMISES rather than by the curve count it happens to
/// leave behind. A fillet's arc has to be genuinely tangent to both lines, not merely present; an
/// offset has to sit the requested distance away along its whole length, not just at its ends. A
/// count-only test passes for an operation that produces the right number of wrong curves, which is
/// the most likely way for any of this to be broken.
/// </summary>
public static class SketchEditTests
{
	public static void Run()
	{
		Report.Section( "sketch fillet" );
		TestFillet();

		Report.Section( "sketch trim" );
		TestTrim();

		Report.Section( "sketch extend" );
		TestExtend();

		Report.Section( "sketch offset" );
		TestOffset();
	}

	static void TestFillet()
	{
		// A right angle at the origin, arms four units long.
		var sketch = new Sketch();
		var corner = sketch.AddPoint( 0f, 0f );
		var right = sketch.AddPoint( 4f, 0f );
		var up = sketch.AddPoint( 0f, 4f );

		var armA = sketch.Add( new SketchLine( corner, right ) );
		var armB = sketch.Add( new SketchLine( corner, up ) );

		var ok = SketchEdit.Fillet( sketch, corner, 1f, out var error );

		Report.Check( "a right angle rounds", ok, error );

		var arc = sketch.Curves.OfType<SketchArc>().FirstOrDefault();

		Report.Check( "and leaves an arc behind", arc is not null );

		if ( arc is null )
			return;

		var centre = sketch.Points[arc.Center];
		var radius = arc.Radius( sketch );

		Report.Check( "of the radius that was asked for", MathF.Abs( radius - 1f ) < 1e-4f,
			$"{radius:0.#####}" );

		// A right angle rounded by 1 puts the centre at (1,1) and the tangent points at (1,0) and
		// (0,1). Those are worth asserting outright, because they are the closed-form answer.
		Report.Check( "centred where the closed form says", (centre - new Vec2( 1f, 1f )).Length < 1e-4f,
			centre.ToString() );

		// TANGENCY IS THE ACTUAL CLAIM. The centre must be exactly a radius from each arm's line,
		// which is what makes the join smooth rather than merely connected.
		var gapA = DistanceToSegment( centre, sketch.Points[armA.Start], sketch.Points[armA.End] );
		var gapB = DistanceToSegment( centre, sketch.Points[armB.Start], sketch.Points[armB.End] );

		Report.Check( "and genuinely tangent to both arms, not just touching them",
			MathF.Abs( gapA - radius ) < 1e-3f && MathF.Abs( gapB - radius ) < 1e-3f,
			$"arm gaps {gapA:0.#####} and {gapB:0.#####} against radius {radius:0.#####}" );

		// The arms must still reach the arc: its ends are their new ends.
		var arcStart = sketch.Points[arc.Start];
		var arcEnd = sketch.Points[arc.End];

		var armEnds = new[]
		{
			sketch.Points[armA.Start], sketch.Points[armA.End],
			sketch.Points[armB.Start], sketch.Points[armB.End]
		};

		Report.Check( "and the arms were shortened onto its ends, leaving no gap",
			armEnds.Any( p => (p - arcStart).Length < 1e-4f ) && armEnds.Any( p => (p - arcEnd).Length < 1e-4f ) );

		Report.Check( "the far ends of the arms did not move",
			armEnds.Any( p => (p - new Vec2( 4f, 0f )).Length < 1e-5f ) &&
			armEnds.Any( p => (p - new Vec2( 0f, 4f )).Length < 1e-5f ) );

		// A fillet bigger than the lines can carry has to be refused, not clamped: clamping would
		// silently give a different radius than the number typed.
		var tight = new Sketch();
		var tightCorner = tight.AddPoint( 0f, 0f );
		tight.Add( new SketchLine( tightCorner, tight.AddPoint( 1f, 0f ) ) );
		tight.Add( new SketchLine( tightCorner, tight.AddPoint( 0f, 1f ) ) );

		Report.Check( "a radius too big for its corner is refused rather than clamped",
			!SketchEdit.Fillet( tight, tightCorner, 5f, out var tightError ) &&
			tightError.Contains( "available" ), tightError );

		var straight = new Sketch();
		var mid = straight.AddPoint( 0f, 0f );
		straight.Add( new SketchLine( mid, straight.AddPoint( 1f, 0f ) ) );
		straight.Add( new SketchLine( mid, straight.AddPoint( -1f, 0f ) ) );

		Report.Check( "two collinear lines have no corner to round",
			!SketchEdit.Fillet( straight, mid, 0.2f, out var straightError ), straightError );

		var crowded = new Sketch();
		var hub = crowded.AddPoint( 0f, 0f );
		crowded.Add( new SketchLine( hub, crowded.AddPoint( 1f, 0f ) ) );
		crowded.Add( new SketchLine( hub, crowded.AddPoint( 0f, 1f ) ) );
		crowded.Add( new SketchLine( hub, crowded.AddPoint( -1f, 0f ) ) );

		Report.Check( "three lines at a corner is ambiguous and says so",
			!SketchEdit.Fillet( crowded, hub, 0.2f, out var crowdedError ) &&
			crowdedError.Contains( "ambiguous" ), crowdedError );
	}

	static void TestTrim()
	{
		// A horizontal line crossed by a vertical one at x = 1. Picking left of the crossing must
		// remove the left piece and leave the right.
		var sketch = new Sketch();
		var line = sketch.Add( new SketchLine( sketch.AddPoint( -2f, 0f ), sketch.AddPoint( 4f, 0f ) ) );
		sketch.Add( new SketchLine( sketch.AddPoint( 1f, -2f ), sketch.AddPoint( 1f, 2f ) ) );

		Report.Check( "a crossed line trims", SketchEdit.Trim( sketch, line, new Vec2( -1f, 0f ), out var error ), error );

		Report.Check( "the piece under the pick is the piece that goes",
			MathF.Abs( sketch.Points[line.Start].x - 1f ) < 1e-4f &&
			MathF.Abs( sketch.Points[line.End].x - 4f ) < 1e-4f,
			$"{sketch.Points[line.Start]} to {sketch.Points[line.End]}" );

		// Picking the other side of the same crossing takes the other piece.
		var mirror = new Sketch();
		var across = mirror.Add( new SketchLine( mirror.AddPoint( -2f, 0f ), mirror.AddPoint( 4f, 0f ) ) );
		mirror.Add( new SketchLine( mirror.AddPoint( 1f, -2f ), mirror.AddPoint( 1f, 2f ) ) );

		SketchEdit.Trim( mirror, across, new Vec2( 3f, 0f ), out _ );

		Report.Check( "and picking the other side takes the other piece",
			MathF.Abs( mirror.Points[across.Start].x + 2f ) < 1e-4f &&
			MathF.Abs( mirror.Points[across.End].x - 1f ) < 1e-4f,
			$"{mirror.Points[across.Start]} to {mirror.Points[across.End]}" );

		// Crossed twice, picked in the middle: the curve splits rather than losing an end.
		var twice = new Sketch();
		var spine = twice.Add( new SketchLine( twice.AddPoint( -4f, 0f ), twice.AddPoint( 4f, 0f ) ) );
		twice.Add( new SketchLine( twice.AddPoint( -1f, -2f ), twice.AddPoint( -1f, 2f ) ) );
		twice.Add( new SketchLine( twice.AddPoint( 1f, -2f ), twice.AddPoint( 1f, 2f ) ) );

		SketchEdit.Trim( twice, spine, new Vec2( 0f, 0f ), out _ );

		var pieces = twice.Curves.OfType<SketchLine>()
			.Where( l => MathF.Abs( twice.Points[l.Start].y ) < 1e-6f && MathF.Abs( twice.Points[l.End].y ) < 1e-6f )
			.ToList();

		Report.Check( "a bite out of the middle leaves two pieces, not one", pieces.Count == 2,
			$"{pieces.Count} pieces" );

		Report.Check( "and neither piece covers the bite",
			pieces.All( p => MathF.Min( twice.Points[p.Start].x, twice.Points[p.End].x ) >= 1f - 1e-3f ||
				MathF.Max( twice.Points[p.Start].x, twice.Points[p.End].x ) <= -1f + 1e-3f ),
			string.Join( "; ", pieces.Select( p => $"{twice.Points[p.Start]}->{twice.Points[p.End]}" ) ) );

		// A curve crossing nothing has no piece to cut, so trimming it removes it outright.
		var lonely = new Sketch();
		var only = lonely.Add( new SketchLine( lonely.AddPoint( 0f, 0f ), lonely.AddPoint( 1f, 1f ) ) );

		SketchEdit.Trim( lonely, only, new Vec2( 0.5f, 0.5f ), out _ );

		Report.Check( "a line crossing nothing is removed outright", lonely.Curves.Count == 0,
			$"{lonely.Curves.Count} curves left" );

		// A circle has no ends, so a trim turns it into an arc rather than shortening it.
		var round = new Sketch();
		var circle = round.AddCircle( new Vec2( 0f, 0f ), 2f );
		round.Add( new SketchLine( round.AddPoint( -3f, 1f ), round.AddPoint( 3f, 1f ) ) );

		var trimmedCircle = SketchEdit.Trim( round, circle, new Vec2( 0f, 2f ), out var circleError );

		Report.Check( "a circle crossed twice trims", trimmedCircle, circleError );

		Report.Check( "and becomes an arc rather than staying a circle",
			!round.Curves.OfType<SketchCircle>().Any() && round.Curves.OfType<SketchArc>().Count() == 1,
			$"{round.Curves.OfType<SketchCircle>().Count()} circles, {round.Curves.OfType<SketchArc>().Count()} arcs" );

		var survivor = round.Curves.OfType<SketchArc>().First();

		Report.Check( "the arc kept is the one away from the pick",
			round.Points[survivor.Start].y < 1f + 1e-3f && round.Points[survivor.End].y < 1f + 1e-3f,
			$"{round.Points[survivor.Start]} to {round.Points[survivor.End]}" );

		var spline = new Sketch();
		var wiggle = spline.AddSpline( false, new Vec2( 0f, 0f ), new Vec2( 1f, 1f ), new Vec2( 2f, 0f ) );

		Report.Check( "trimming a spline is refused with a reason rather than a wrong answer",
			!SketchEdit.Trim( spline, wiggle, new Vec2( 1f, 1f ), out var splineError ) &&
			splineError.Contains( "not supported" ), splineError );
	}

	static void TestExtend()
	{
		// A stub pointing at a wall it does not reach.
		var sketch = new Sketch();
		var stub = sketch.Add( new SketchLine( sketch.AddPoint( 0f, 0f ), sketch.AddPoint( 1f, 0f ) ) );
		sketch.Add( new SketchLine( sketch.AddPoint( 3f, -2f ), sketch.AddPoint( 3f, 2f ) ) );

		Report.Check( "a line extends to what it points at",
			SketchEdit.Extend( sketch, stub, atStart: false, out var error ), error );

		Report.Check( "landing exactly on the crossing",
			(sketch.Points[stub.End] - new Vec2( 3f, 0f )).Length < 1e-4f,
			sketch.Points[stub.End].ToString() );

		Report.Check( "and leaving the other end alone",
			(sketch.Points[stub.Start] - new Vec2( 0f, 0f )).Length < 1e-6f );

		// TWO WALLS: the extend must stop at the NEARER one, or repeated extends jump the sketch.
		var far = new Sketch();
		var probe = far.Add( new SketchLine( far.AddPoint( 0f, 0f ), far.AddPoint( 1f, 0f ) ) );
		far.Add( new SketchLine( far.AddPoint( 6f, -2f ), far.AddPoint( 6f, 2f ) ) );
		far.Add( new SketchLine( far.AddPoint( 3f, -2f ), far.AddPoint( 3f, 2f ) ) );

		SketchEdit.Extend( far, probe, atStart: false, out _ );

		Report.Check( "stopping at the nearer of two walls, not the further",
			MathF.Abs( far.Points[probe.End].x - 3f ) < 1e-4f, far.Points[probe.End].ToString() );

		// Extending the START goes the other way.
		var backwards = new Sketch();
		var tail = backwards.Add( new SketchLine( backwards.AddPoint( 0f, 0f ), backwards.AddPoint( 1f, 0f ) ) );
		backwards.Add( new SketchLine( backwards.AddPoint( -2f, -2f ), backwards.AddPoint( -2f, 2f ) ) );

		SketchEdit.Extend( backwards, tail, atStart: true, out _ );

		Report.Check( "extending the start goes the other way",
			MathF.Abs( backwards.Points[tail.Start].x + 2f ) < 1e-4f,
			backwards.Points[tail.Start].ToString() );

		var nowhere = new Sketch();
		var alone = nowhere.Add( new SketchLine( nowhere.AddPoint( 0f, 0f ), nowhere.AddPoint( 1f, 0f ) ) );

		Report.Check( "a line pointing at nothing says so rather than flying off",
			!SketchEdit.Extend( nowhere, alone, atStart: false, out var nowhereError ), nowhereError );
	}

	static void TestOffset()
	{
		// A single line offset to its left. The sign convention is the thing being pinned here.
		var one = new Sketch();
		var flat = one.Add( new SketchLine( one.AddPoint( 0f, 0f ), one.AddPoint( 4f, 0f ) ) );

		Report.Check( "a single line offsets",
			SketchEdit.Offset( one, new[] { (SketchCurve)flat }, 1f, out var made, out var error ), error );

		var copy = (SketchLine)made[0];

		Report.Check( "to the left of its direction of travel, by the distance asked for",
			MathF.Abs( one.Points[copy.Start].y - 1f ) < 1e-4f &&
			MathF.Abs( one.Points[copy.End].y - 1f ) < 1e-4f,
			$"{one.Points[copy.Start]} to {one.Points[copy.End]}" );

		Report.Check( "and a negative distance goes the other way",
			SketchEdit.Offset( one, new[] { (SketchCurve)flat }, -1f, out var below, out _ ) &&
			MathF.Abs( one.Points[((SketchLine)below[0]).Start].y + 1f ) < 1e-4f );

		// AN OUTSIDE CORNER IS THE INTERESTING CASE: the two offset lines fall short of each other
		// and the joint has to be closed by extending them to their crossing.
		var elbow = new Sketch();
		var a = elbow.AddPoint( 0f, 0f );
		var b = elbow.AddPoint( 4f, 0f );
		var c = elbow.AddPoint( 4f, 4f );

		var first = elbow.Add( new SketchLine( a, b ) );
		var second = elbow.Add( new SketchLine( b, c ) );

		Report.Check( "a corner offsets",
			SketchEdit.Offset( elbow, new SketchCurve[] { first, second }, 1f, out var elbowMade, out var elbowError ),
			elbowError );

		var outA = (SketchLine)elbowMade[0];
		var outB = (SketchLine)elbowMade[1];

		Report.Check( "and its corner is closed rather than left as two loose ends",
			(elbow.Points[outA.End] - elbow.Points[outB.Start]).Length < 1e-4f,
			$"{elbow.Points[outA.End]} against {elbow.Points[outB.Start]}" );

		// Offsetting this corner one unit to the left puts the new corner at (3, 1), which is the
		// closed-form answer and catches a joint stitched to the wrong crossing.
		Report.Check( "at the point the closed form says",
			(elbow.Points[outA.End] - new Vec2( 3f, 1f )).Length < 1e-3f,
			elbow.Points[outA.End].ToString() );

		// An arc offsets by changing radius, and which way depends on its sweep.
		var curved = new Sketch();
		var centre = curved.AddPoint( 0f, 0f );
		var arcStart = curved.AddPoint( 2f, 0f );
		var arcEnd = curved.AddPoint( 0f, 2f );
		var quarter = curved.Add( new SketchArc( centre, arcStart, arcEnd ) );

		Report.Check( "an arc offsets",
			SketchEdit.Offset( curved, new SketchCurve[] { quarter }, 0.5f, out var arcMade, out var arcError ),
			arcError );

		var offsetArc = (SketchArc)arcMade[0];

		Report.Check( "by changing its radius, toward the centre when it turns counter-clockwise",
			MathF.Abs( offsetArc.Radius( curved ) - 1.5f ) < 1e-4f,
			$"{offsetArc.Radius( curved ):0.#####}, wanted 1.5" );

		Report.Check( "and an offset that would collapse an arc is refused",
			!SketchEdit.Offset( curved, new SketchCurve[] { quarter }, 5f, out _, out var collapseError ) &&
			collapseError.Contains( "collapses" ), collapseError );

		var unsupported = new Sketch();
		var wiggle = unsupported.AddSpline( false, new Vec2( 0f, 0f ), new Vec2( 1f, 1f ), new Vec2( 2f, 0f ) );

		Report.Check( "offsetting a spline is refused with a reason",
			!SketchEdit.Offset( unsupported, new SketchCurve[] { wiggle }, 1f, out _, out var wiggleError ) &&
			wiggleError.Contains( "not supported" ), wiggleError );
	}

	static float DistanceToSegment( Vec2 p, Vec2 a, Vec2 b )
	{
		var d = b - a;

		if ( d.LengthSquared < 1e-12f )
			return (p - a).Length;

		var t = Math.Clamp( Vec2.Dot( p - a, d ) / d.LengthSquared, 0f, 1f );

		return (p - (a + d * t)).Length;
	}
}
