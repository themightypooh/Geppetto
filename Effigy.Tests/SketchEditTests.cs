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

		Report.Section( "sketch cut stroke" );
		TestCut();

		Report.Section( "sketch mirror" );
		TestMirror();
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

	/// <summary>
	/// The cut stroke — one segment of a drag, and what it takes with it.
	///
	/// EVERY CASE HERE IS "WHAT DID THE STROKE LEAVE BEHIND", not "did the call return true". The
	/// whole risk in a tool driven by a drag is that it takes slightly more or slightly less than
	/// the thing you swiped through, and a call that succeeded says nothing about which.
	/// </summary>
	static void TestCut()
	{
		// A line crossing nothing has no piece smaller than itself, so a swipe through it takes it.
		var lonely = new Sketch();
		lonely.AddLine( new Vec2( 0f, 0f ), new Vec2( 4f, 0f ) );

		Report.Check( "a stroke through a lone line takes it",
			SketchCut.Cut( lonely, new Vec2( 2f, -1f ), new Vec2( 2f, 1f ) ) == 1 && lonely.Curves.Count == 0,
			$"{lonely.Curves.Count} curves left" );

		// And a stroke that goes nowhere near it takes nothing. An eraser that fires on a miss is
		// worse than one that misses, because the drag is continuous and every frame of it is
		// another chance.
		var missed = new Sketch();
		missed.AddLine( new Vec2( 0f, 0f ), new Vec2( 4f, 0f ) );

		Report.Check( "a stroke that misses cuts nothing",
			SketchCut.Cut( missed, new Vec2( 2f, 1f ), new Vec2( 2f, 3f ) ) == 0 && missed.Curves.Count == 1,
			$"{missed.Curves.Count} curves left" );

		// DRAWN ALONG A LINE RATHER THAN ACROSS IT. Collinear is not a crossing, and the whole
		// difference between the two is one degenerate case in SegmentCross - so it is worth its own
		// check: without it, dragging down an edge to reach the thing past it eats the edge.
		var alongside = new Sketch();
		alongside.AddLine( new Vec2( 0f, 0f ), new Vec2( 4f, 0f ) );

		Report.Check( "a stroke drawn along a line does not eat it",
			SketchCut.Cut( alongside, new Vec2( 1f, 0f ), new Vec2( 3f, 0f ) ) == 0 && alongside.Curves.Count == 1,
			$"{alongside.Curves.Count} curves left" );

		// THE CUT STOPS AT THE NEIGHBOURS, which is the whole difference between this and deleting
		// whatever is under the cursor. A rectangle's edge ends at two corners, so swiping it takes
		// that edge and leaves the other three - the shape opens up rather than vanishing.
		var box = new Sketch();
		box.AddRectangle( new Vec2( 0f, 0f ), new Vec2( 4f, 3f ) );

		Report.Check( "a stroke across one edge of a rectangle takes that edge",
			SketchCut.Cut( box, new Vec2( 2f, -1f ), new Vec2( 2f, 1f ) ) == 1 && box.Curves.Count == 3,
			$"{box.Curves.Count} edges left" );

		Report.Check( "and it is the edge that was crossed",
			!box.Curves.OfType<SketchLine>().Any( l =>
				MathF.Abs( box.Points[l.Start].y ) < 1e-4f && MathF.Abs( box.Points[l.End].y ) < 1e-4f ),
			string.Join( "; ", box.Curves.OfType<SketchLine>()
				.Select( l => $"{box.Points[l.Start]}->{box.Points[l.End]}" ) ) );

		// A cut through the middle of a crossed line leaves the outer pieces standing - the same
		// bite the Trim tool's click takes, because it is the same call underneath.
		var cross = new Sketch();
		cross.AddLine( new Vec2( -4f, 0f ), new Vec2( 4f, 0f ) );
		cross.AddLine( new Vec2( -1f, -2f ), new Vec2( -1f, 2f ) );
		cross.AddLine( new Vec2( 1f, -2f ), new Vec2( 1f, 2f ) );

		Report.Check( "a stroke through a crossed line bites out the middle",
			SketchCut.Cut( cross, new Vec2( 0f, -0.5f ), new Vec2( 0f, 0.5f ) ) == 1 );

		var spans = cross.Curves.OfType<SketchLine>()
			.Where( l => MathF.Abs( cross.Points[l.Start].y ) < 1e-6f && MathF.Abs( cross.Points[l.End].y ) < 1e-6f )
			.ToList();

		Report.Check( "leaving the two outer pieces rather than nothing", spans.Count == 2,
			$"{spans.Count} pieces" );

		Report.Check( "and neither piece covers the bite",
			spans.All( p => MathF.Min( cross.Points[p.Start].x, cross.Points[p.End].x ) >= 1f - 1e-3f ||
				MathF.Max( cross.Points[p.Start].x, cross.Points[p.End].x ) <= -1f + 1e-3f ),
			string.Join( "; ", spans.Select( p => $"{cross.Points[p.Start]}->{cross.Points[p.End]}" ) ) );

		// ONE SEGMENT, SEVERAL CURVES. A drag covers ground between frames, and the piece of path
		// handed down can easily span three lines - so a segment has to cut all of what it went
		// through rather than the first thing it found.
		var fence = new Sketch();

		for ( var i = 0; i < 3; i++ )
			fence.AddLine( new Vec2( i, -1f ), new Vec2( i, 1f ) );

		var crossings = SketchCut.Crossings( fence, new Vec2( -0.5f, 0f ), new Vec2( 2.5f, 0f ) );

		Report.Check( "one segment finds every curve it went through", crossings.Count == 3,
			$"{crossings.Count} crossings" );

		Report.Check( "in the order the stroke reached them",
			crossings.Count == 3 && crossings[0].At.x < crossings[1].At.x && crossings[1].At.x < crossings[2].At.x,
			string.Join( "; ", crossings.Select( c => c.At.ToString() ) ) );

		Report.Check( "and cuts all of them", SketchCut.Apply( fence, crossings ) == 3 && fence.Curves.Count == 0,
			$"{fence.Curves.Count} curves left" );

		// A closed curve crossed twice by ONE segment is reported once. Cutting at both is not a
		// thing that can happen in one pass - the first cut replaces the circle with an arc, and the
		// second crossing then names a curve the sketch no longer has.
		var hoop = new Sketch();
		hoop.AddCircle( new Vec2( 0f, 0f ), 2f );

		Report.Check( "a segment through both sides of a circle reports it once",
			SketchCut.Crossings( hoop, new Vec2( -3f, 1f ), new Vec2( 3f, 1f ) ).Count == 1 );

		Report.Check( "and a circle crossing nothing else goes whole",
			SketchCut.Cut( hoop, new Vec2( -3f, 1f ), new Vec2( 3f, 1f ) ) == 1 && hoop.Curves.Count == 0,
			$"{hoop.Curves.Count} curves left" );

		// The same circle with something to cut against keeps the part away from the stroke, which
		// is Trim's rule and not a second one.
		var capped = new Sketch();
		capped.AddCircle( new Vec2( 0f, 0f ), 2f );
		capped.AddLine( new Vec2( -3f, 1f ), new Vec2( 3f, 1f ) );

		Report.Check( "a circle crossed by a line loses only the piece swiped through",
			SketchCut.Cut( capped, new Vec2( 0f, 1.5f ), new Vec2( 0f, 2.5f ) ) == 1 &&
			capped.Curves.OfType<SketchArc>().Count() == 1 && !capped.Curves.OfType<SketchCircle>().Any(),
			$"{capped.Curves.OfType<SketchCircle>().Count()} circles, {capped.Curves.OfType<SketchArc>().Count()} arcs" );

		var kept = capped.Curves.OfType<SketchArc>().First();

		Report.Check( "and what is kept is the part away from the stroke",
			capped.Points[kept.Start].y < 1f + 1e-3f && capped.Points[kept.End].y < 1f + 1e-3f,
			$"{capped.Points[kept.Start]} to {capped.Points[kept.End]}" );

		// Trim refuses splines, so the cut tool removes them rather than doing nothing - see
		// SketchCut's header for why silence is the worse of the two answers.
		var wiggly = new Sketch();
		wiggly.AddSpline( false, new Vec2( 0f, 0f ), new Vec2( 1f, 2f ), new Vec2( 2f, 0f ) );

		Report.Check( "a spline, which trim will not cut, goes whole",
			SketchCut.Cut( wiggly, new Vec2( 1f, 0f ), new Vec2( 1f, 3f ) ) == 1 && wiggly.Curves.Count == 0,
			$"{wiggly.Curves.Count} curves left" );
	}

	/// <summary>
	/// Mirror, which is the one edit here that ADDS geometry rather than reshaping what is there —
	/// and the only one whose result has to survive being dragged afterwards.
	///
	/// So the checks come in two halves. The first is the reflection itself: the copies land where
	/// the closed form says, an arc's sweep flips, and a point already on the axis is SHARED rather
	/// than duplicated. The second is the part that makes it a mirror instead of a paste — the
	/// Symmetric rules it leaves behind hold, and moving an original drags its reflection after it.
	/// A test that only checked coordinates would pass for a mirror that falls apart on the first
	/// drag, which is the most likely way for this to be broken.
	/// </summary>
	static void TestMirror()
	{
		// A vertical axis on x = 0, and an L drawn entirely to the right of it.
		var sketch = new Sketch();
		var axis = sketch.Add( new SketchLine( sketch.AddPoint( 0f, -5f ), sketch.AddPoint( 0f, 5f ) ) );
		axis.Construction = true;

		var foot = sketch.AddPoint( 1f, 0f );
		var knee = sketch.AddPoint( 3f, 0f );
		var top = sketch.AddPoint( 3f, 2f );

		var shin = sketch.Add( new SketchLine( foot, knee ) );
		var thigh = sketch.Add( new SketchLine( knee, top ) );

		var ok = SketchEdit.Mirror( sketch, new[] { shin, thigh }, null, axis, out var created, out var error );

		Report.Check( "two lines mirror across a vertical axis", ok, error );
		Report.Check( "and produce one copy each", created.Count == 2, $"{created.Count} created" );

		if ( !ok || created.Count != 2 )
			return;

		var copyShin = (SketchLine)created[0];
		var copyThigh = (SketchLine)created[1];

		Report.Check( "the copies land where the closed form says",
			(sketch.Points[copyShin.Start] - new Vec2( -1f, 0f )).Length < 1e-5f &&
			(sketch.Points[copyShin.End] - new Vec2( -3f, 0f )).Length < 1e-5f &&
			(sketch.Points[copyThigh.End] - new Vec2( -3f, 2f )).Length < 1e-5f,
			$"{sketch.Points[copyShin.Start]} {sketch.Points[copyShin.End]} {sketch.Points[copyThigh.End]}" );

		// The copies share their corner with each other exactly the way the originals do. Mirroring
		// each curve into its own fresh points would leave a corner that looks joined and comes
		// apart the moment either half is dragged.
		Report.Check( "and share their own corner by index, not by coordinate",
			copyShin.End == copyThigh.Start, $"{copyShin.End} against {copyThigh.Start}" );

		Report.Check( "the originals were not moved",
			(sketch.Points[foot] - new Vec2( 1f, 0f )).Length < 1e-6f &&
			(sketch.Points[top] - new Vec2( 3f, 2f )).Length < 1e-6f );

		Report.Check( "the axis itself was not copied",
			sketch.Curves.OfType<SketchLine>().Count( l => MathF.Abs( sketch.Points[l.Start].x ) < 1e-5f
				&& MathF.Abs( sketch.Points[l.End].x ) < 1e-5f ) == 1 );

		// THE PART THAT MAKES IT A MIRROR. Every mirrored point carries a Symmetric rule against
		// the axis, so the solver knows the two halves belong to each other.
		var symmetric = sketch.Constraints.Count( c => c.Kind == SketchConstraintKind.Symmetric );

		Report.Check( "each mirrored point is held symmetric to its original",
			symmetric == 3, $"{symmetric} symmetric constraints for 3 mirrored points" );

		// Solving a sketch that is ALREADY symmetric must not move anything. If it does, the
		// constraints disagree with the geometry the mirror just wrote, and every mirror in the
		// editor would jump the moment anything else was constrained.
		sketch.Solve();

		Report.Check( "and the sketch as built already satisfies them, so solving moves nothing",
			(sketch.Points[copyThigh.End] - new Vec2( -3f, 2f )).Length < 1e-3f,
			sketch.Points[copyThigh.End].ToString() );

		// Move the original's top corner and re-solve, pinned on the point that moved - which is
		// what a drag does; see the Solve call in EffigyViewport.Sketching.cs.
		//
		// WHAT IS CHECKED IS THAT THE SYMMETRY STILL HOLDS, not that the copy landed on a
		// coordinate worked out by hand. Nothing in this sketch is fixed, so the solver is free to
		// meet the new position by moving the axis a little as well as the copy - which is
		// ordinary parametric behaviour, and would be the answer Onshape gives for an
		// unconstrained mirror line too. Asserting a hand-computed position would be asserting
		// that the axis never moves, which nothing here promises.
		sketch.Points[top] = new Vec2( 5f, 3f );

		var solved = SketchSolver.Solve( sketch, top );

		var axisA = sketch.Points[axis.Start];
		var axisB = sketch.Points[axis.End];
		var wanted = SketchEdit.Reflect( sketch.Points[top], axisA, axisB );

		Report.Check( "moving an original drags its reflection after it",
			solved.Converged && (sketch.Points[copyThigh.End] - wanted).Length < 1e-2f,
			$"{sketch.Points[copyThigh.End]} against the reflection {wanted}" );

		Report.Check( "and the point that was dragged stayed where it was put",
			(sketch.Points[top] - new Vec2( 5f, 3f )).Length < 1e-4f, sketch.Points[top].ToString() );

		TestMirrorOnAxis();
		TestMirrorArcAndCircle();
		TestMirrorRefusals();
	}

	/// <summary>A half profile drawn against the axis has to come back as ONE closed shape, which
	/// only happens if the points sitting on the axis are shared rather than doubled.</summary>
	static void TestMirrorOnAxis()
	{
		var sketch = new Sketch();
		var axis = sketch.Add( new SketchLine( sketch.AddPoint( 0f, -5f ), sketch.AddPoint( 0f, 5f ) ) );

		// Half a triangle: out from the axis, up, and back to the axis.
		var bottom = sketch.AddPoint( 0f, 0f );
		var side = sketch.AddPoint( 2f, 1f );
		var apex = sketch.AddPoint( 0f, 2f );

		var lower = sketch.Add( new SketchLine( bottom, side ) );
		var upper = sketch.Add( new SketchLine( side, apex ) );

		var before = sketch.Points.Count;

		Report.Check( "a half profile drawn against the axis mirrors",
			SketchEdit.Mirror( sketch, new[] { lower, upper }, null, axis, out var created, out var error ), error );

		Report.Check( "and only the one point off the axis is duplicated",
			sketch.Points.Count == before + 1, $"{sketch.Points.Count - before} points added" );

		var copyLower = (SketchLine)created[0];
		var copyUpper = (SketchLine)created[1];

		Report.Check( "so the two halves meet at the same indices and the loop closes",
			copyLower.Start == bottom && copyUpper.End == apex,
			$"{copyLower.Start}/{bottom} and {copyUpper.End}/{apex}" );

		Report.Check( "and a point on the axis gets no symmetry rule of its own",
			sketch.Constraints.Count( c => c.Kind == SketchConstraintKind.Symmetric ) == 1 );
	}

	static void TestMirrorArcAndCircle()
	{
		var sketch = new Sketch();
		var axis = sketch.Add( new SketchLine( sketch.AddPoint( 0f, -5f ), sketch.AddPoint( 0f, 5f ) ) );

		var arc = sketch.Add( new SketchArc(
			sketch.AddPoint( 2f, 0f ), sketch.AddPoint( 3f, 0f ), sketch.AddPoint( 2f, 1f ) ) );

		var circle = sketch.Add( new SketchCircle( sketch.AddPoint( 4f, 1f ), 0.75f ) );
		circle.Construction = true;

		Report.Check( "an arc and a circle mirror",
			SketchEdit.Mirror( sketch, new SketchCurve[] { arc, circle }, null, axis, out var created, out var error ),
			error );

		var copyArc = created.OfType<SketchArc>().FirstOrDefault();
		var copyCircle = created.OfType<SketchCircle>().FirstOrDefault();

		Report.Check( "the arc's sweep flips, because a reflection reverses handedness",
			copyArc is not null && copyArc.Clockwise != arc.Clockwise );

		if ( copyArc is not null )
		{
			// The reflected arc has to pass through the reflections of the points the original
			// passes through, which is a stronger claim than its ends landing in the right place -
			// and it is the one that catches a sweep left unflipped.
			var original = arc.Tessellate( sketch, 0.001f );
			var copy = copyArc.Tessellate( sketch, 0.001f );

			var matched = original.Count == copy.Count && original.Count > 0;

			for ( var i = 0; matched && i < original.Count; i++ )
				matched = (copy[i] - new Vec2( -original[i].x, original[i].y )).Length < 1e-3f;

			Report.Check( "and the copy traces the reflection of the original along its whole length", matched );
		}

		Report.Check( "a circle keeps its radius and reflects its centre",
			copyCircle is not null && MathF.Abs( copyCircle.Radius - 0.75f ) < 1e-5f &&
			(sketch.Points[copyCircle.Center] - new Vec2( -4f, 1f )).Length < 1e-5f );

		Report.Check( "and construction geometry mirrors as construction geometry",
			copyCircle is not null && copyCircle.Construction );
	}

	static void TestMirrorRefusals()
	{
		var sketch = new Sketch();
		var axis = sketch.Add( new SketchLine( sketch.AddPoint( 0f, -1f ), sketch.AddPoint( 0f, 1f ) ) );
		var line = sketch.Add( new SketchLine( sketch.AddPoint( 1f, 0f ), sketch.AddPoint( 2f, 0f ) ) );

		Report.Check( "mirroring nothing is refused rather than silently doing nothing",
			!SketchEdit.Mirror( sketch, null, null, axis, out _, out var emptyError ), emptyError );

		Report.Check( "and so is a mirror line with no length",
			!SketchEdit.Mirror( sketch, new[] { line }, null,
				new SketchLine( axis.Start, axis.Start ), out _, out var shortError ), shortError );

		// Selecting the axis along with everything else is the ordinary way to work - a box drawn
		// round the whole sketch takes the centreline too - so it has to be dropped rather than
		// reflected onto itself.
		var count = sketch.Curves.Count;

		Report.Check( "the axis in the selection is dropped, not copied onto itself",
			SketchEdit.Mirror( sketch, new[] { axis, line }, null, axis, out var created, out var error )
				&& created.Count == 1 && sketch.Curves.Count == count + 1,
			error ?? $"{created?.Count} created" );

		// A lone point - what the Point tool leaves behind - is geometry too, and mirrors with no
		// curve attached to it.
		var lone = new Sketch();
		var loneAxis = lone.Add( new SketchLine( lone.AddPoint( 0f, -1f ), lone.AddPoint( 0f, 1f ) ) );
		var dot = lone.AddPoint( 3f, 1f );

		Report.Check( "a lone point mirrors on its own",
			SketchEdit.Mirror( lone, null, new[] { dot }, loneAxis, out _, out var loneError ) &&
			lone.Points.Any( p => (p - new Vec2( -3f, 1f )).Length < 1e-5f ), loneError );
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
