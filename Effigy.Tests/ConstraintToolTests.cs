using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Turning a selection into a constraint.
///
/// The solver has been able to satisfy eleven kinds of rule for several sessions and there has been
/// no way to add one in the editor, so it only ever ran on what the drawing inference happened to
/// put there. ConstraintTools is the missing half, and it is all rules and no drawing — which is
/// where the mistakes are, and why it can be tested without a viewport.
///
/// These check three things in order: that the right things are offered for a given selection, that
/// applying one and solving actually moves the geometry the way the label promised, and that the
/// same rule cannot be added twice however it is phrased.
/// </summary>
public static class ConstraintToolTests
{
	public static void Run()
	{
		Report.Section( "constraint tools: what a selection allows" );
		TestOffers();

		Report.Section( "constraint tools: applying one does what the label says" );
		TestApplied();

		Report.Section( "constraint tools: dimensions open on the truth" );
		TestMeasured();

		Report.Section( "constraint tools: the same rule is not offered twice" );
		TestDuplicates();

		Report.Section( "constraint tools: what a circle's radius really is" );
		TestCircleRadius();

		Report.Section( "constraint tools: the six kinds that had no way in" );
		TestTheUnreachableSix();

		Report.Section( "constraint tools: a rule that cannot hold is taken back out" );
		TestRefused();

		Report.Section( "constraint tools: marks to draw on the sketch" );
		TestMarkers();

		Report.Section( "constraint tools: finding what holds a point" );
		TestTouching();
	}

	static void TestOffers()
	{
		var sketch = new Sketch();
		var line = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 1 ) );

		var one = Labels( sketch, new SketchSelection( null, new[] { line.Id } ) );

		Report.Check( "one line offers horizontal, vertical and a length",
			one.SequenceEqual( new[] { "Horizontal", "Length", "Vertical" } ), string.Join( ", ", one ) );

		var second = sketch.AddLine( new Vec2( 0, 3 ), new Vec2( 4, 5 ) );

		var two = Labels( sketch, new SketchSelection( null, new[] { line.Id, second.Id } ) );

		Report.Check( "two lines offer the pair rules",
			two.SequenceEqual( new[] { "Angle", "Equal length", "Parallel", "Perpendicular" } ),
			string.Join( ", ", two ) );

		var points = new SketchSelection( new[] { line.Start, second.Start } );
		var pair = Labels( sketch, points );

		Report.Check( "two points offer coincident, distance and the two alignments",
			pair.SequenceEqual( new[] { "Coincident", "Distance", "Horizontal", "Vertical" } ),
			string.Join( ", ", pair ) );

		// A POINT AND A LINE, not three loose points. Three points also describe "one lies on the
		// other two", and then which one is the point is a guess dressed up as a convention.
		//
		// Midpoint takes the same selection, being point-on-line said more exactly.
		var onLine = Labels( sketch, new SketchSelection( new[] { second.Start }, new[] { line.Id } ) );

		Report.Check( "a point and a line offer point-on-line and midpoint",
			onLine.SequenceEqual( new[] { "Midpoint", "Point on line" } ), string.Join( ", ", onLine ) );

		var mirror = Labels( sketch,
			new SketchSelection( new[] { second.Start, second.End }, new[] { line.Id } ) );

		Report.Check( "two points and a line offer symmetric",
			mirror.SequenceEqual( new[] { "Symmetric" } ), string.Join( ", ", mirror ) );

		Report.Check( "nothing selected offers nothing",
			ConstraintTools.Offers( sketch, new SketchSelection() ).Count == 0 );

		Report.Check( "a selection that means nothing offers nothing",
			ConstraintTools.Offers( sketch,
				new SketchSelection( new[] { line.Start, line.End, second.Start } ) ).Count == 0 );
	}

	/// <summary>
	/// The part that matters: apply the offer, solve, and check the geometry obeys.
	///
	/// A test that only checked which constraint object came out would pass just as well if the
	/// point indices in it were wrong, and wrong indices are the single most likely mistake in the
	/// whole file.
	/// </summary>
	static void TestApplied()
	{
		// HORIZONTAL on a line that is not.
		var sketch = new Sketch();
		var line = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 2 ) );

		Apply( sketch, new SketchSelection( null, new[] { line.Id } ), "Horizontal" );

		var solved = SketchSolver.Solve( sketch );

		Report.Check( "horizontal converges", solved.Converged, $"residual {solved.Residual:0.000000}" );

		Report.Check( "and the line comes out level",
			MathF.Abs( sketch.Points[line.Start].y - sketch.Points[line.End].y ) < 1e-3f,
			$"ends at y {sketch.Points[line.Start].y:0.####} and {sketch.Points[line.End].y:0.####}" );

		// PERPENDICULAR between two lines that are not.
		var corner = new Sketch();
		var a = corner.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );
		var b = corner.AddLine( new Vec2( 0, 0 ), new Vec2( 3, 1 ) );

		Apply( corner, new SketchSelection( null, new[] { a.Id, b.Id } ), "Perpendicular" );
		SketchSolver.Solve( corner );

		var u = corner.Points[a.End] - corner.Points[a.Start];
		var v = corner.Points[b.End] - corner.Points[b.Start];

		Report.Check( "perpendicular gives a right angle",
			MathF.Abs( Vec2.Dot( u.Normal, v.Normal ) ) < 1e-3f,
			$"dot {Vec2.Dot( u.Normal, v.Normal ):0.#####}" );

		// DISTANCE, driven to a number the sketch was not.
		var span = new Sketch();
		var line2 = span.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );

		var offer = ConstraintTools.Offers( span, new SketchSelection( null, new[] { line2.Id } ) )
			.Single( o => o.Label == "Length" );

		offer.Value = 10f;
		ConstraintTools.Apply( span, offer );
		SketchSolver.Solve( span );

		Report.Check( "a driven length reaches the number it was given",
			MathF.Abs( (span.Points[line2.End] - span.Points[line2.Start]).Length - 10f ) < 1e-3f,
			$"came out {(span.Points[line2.End] - span.Points[line2.Start]).Length:0.####}" );

		// POINT ON LINE, with the right point of the three.
		var onto = new Sketch();
		var rail = onto.AddLine( new Vec2( 0, 0 ), new Vec2( 10, 0 ) );
		var loose = onto.AddPoint( new Vec2( 5, 4 ) );

		Apply( onto, new SketchSelection( new[] { loose }, new[] { rail.Id } ), "Point on line" );

		var landed = SketchSolver.Solve( onto );

		Report.Check( "point on line converges", landed.Converged, $"residual {landed.Residual:0.000000}" );

		// AGAINST THE LINE WHERE IT ENDED UP, not against where it started. The first version of this
		// checked that the point's y reached zero and the rail's y stayed there, and both failed: an
		// under-constrained sketch is free to meet a new rule by moving EITHER side of it, and least
		// squares moves both. That is correct — the rule says the point is on the line, and says
		// nothing about which of them has to give — so the oracle is the relation, not the fixture's
		// assumption about who moves.
		Report.Check( "the point ends up on the line",
			DistanceToLine( onto, loose, rail.Start, rail.End ) < 1e-3f,
			$"{DistanceToLine( onto, loose, rail.Start, rail.End ):0.######} off it" );

		// It did have to move: a test that passed on an untouched sketch would prove nothing.
		Report.Check( "and something actually moved to get there",
			(onto.Points[loose] - new Vec2( 5, 4 )).Length > 0.1f
			|| (onto.Points[rail.End] - new Vec2( 10, 0 )).Length > 0.1f );

		// SYMMETRIC about a line.
		var mirror = new Sketch();
		var axis = mirror.AddLine( new Vec2( 0, -5 ), new Vec2( 0, 5 ) );
		var left = mirror.AddPoint( new Vec2( -3, 1 ) );
		var right = mirror.AddPoint( new Vec2( 4, 2 ) );

		Apply( mirror, new SketchSelection( new[] { left, right }, new[] { axis.Id } ), "Symmetric" );

		var mirrored = SketchSolver.Solve( mirror );

		Report.Check( "symmetric converges", mirrored.Converged, $"residual {mirrored.Residual:0.000000}" );

		// Same lesson as above, and it bit harder here: the AXIS is free to rotate too, so checking
		// that the two x values cancel assumes an axis that stayed vertical. What symmetry actually
		// means is two things — their midpoint lies on the axis, and the line between them crosses it
		// square — and both are true wherever the axis ended up.
		var midpoint = (mirror.Points[left] + mirror.Points[right]) * 0.5f;
		var toAxis = DistanceToLine( midpoint, mirror.Points[axis.Start], mirror.Points[axis.End] );

		Report.Check( "the pair's midpoint sits on the axis", toAxis < 1e-3f, $"{toAxis:0.######} off it" );

		var across = (mirror.Points[right] - mirror.Points[left]).Normal;
		var along = (mirror.Points[axis.End] - mirror.Points[axis.Start]).Normal;

		Report.Check( "and the line between them crosses it square",
			MathF.Abs( Vec2.Dot( across, along ) ) < 1e-3f,
			$"dot {Vec2.Dot( across, along ):0.#####}" );

		// EQUAL RADIUS on two arcs.
		var arcs = new Sketch();
		var c1 = arcs.AddPoint( new Vec2( 0, 0 ) );
		var s1 = arcs.AddPoint( new Vec2( 2, 0 ) );
		var e1 = arcs.AddPoint( new Vec2( 0, 2 ) );
		var c2 = arcs.AddPoint( new Vec2( 10, 0 ) );
		var s2 = arcs.AddPoint( new Vec2( 15, 0 ) );
		var e2 = arcs.AddPoint( new Vec2( 10, 5 ) );

		var arcA = arcs.Add( new SketchArc( c1, s1, e1 ) );
		var arcB = arcs.Add( new SketchArc( c2, s2, e2 ) );

		Apply( arcs, new SketchSelection( null, new[] { arcA.Id, arcB.Id } ), "Equal radius" );
		SketchSolver.Solve( arcs );

		var r1 = (arcs.Points[s1] - arcs.Points[c1]).Length;
		var r2 = (arcs.Points[s2] - arcs.Points[c2]).Length;

		Report.Check( "equal radius makes two arcs the same size",
			MathF.Abs( r1 - r2 ) < 1e-3f, $"{r1:0.####} and {r2:0.####}" );
	}

	static void TestMeasured()
	{
		var sketch = new Sketch();
		var line = sketch.AddLine( new Vec2( 1, 1 ), new Vec2( 4, 5 ) );

		var length = ConstraintTools.Offers( sketch, new SketchSelection( null, new[] { line.Id } ) )
			.Single( o => o.Label == "Length" );

		// A DIMENSION OPENS ON THE TRUTH. Showing zero and making the user type what the sketch
		// already is turns "lock this where it is" — which is most dimensions — into measuring by
		// hand, and any rounding they do silently moves the geometry.
		Report.Check( "a length dimension is pre-filled with the current length",
			MathF.Abs( length.Value - 5f ) < 1e-4f, $"{length.Value:0.#####}" );

		Report.Check( "and is marked as taking a number", length.NeedsValue );

		Report.Check( "with no unit, since it is a length", length.Unit == "" );

		var second = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 0, 3 ) );

		var angle = ConstraintTools.Offers( sketch, new SketchSelection( null, new[] { line.Id, second.Id } ) )
			.Single( o => o.Label == "Angle" );

		// (3,4) against (0,1) is 90 - 53.13 = 36.87 degrees.
		Report.Check( "an angle dimension is pre-filled with the current angle",
			MathF.Abs( angle.Value - 36.8699f ) < 1e-2f, $"{angle.Value:0.####}" );

		Report.Check( "and says it is in degrees", angle.Unit == "deg" );

		Report.Check( "a plain rule carries no value",
			ConstraintTools.Offers( sketch, new SketchSelection( null, new[] { line.Id, second.Id } ) )
				.Single( o => o.Label == "Parallel" ).NeedsValue == false );

		// Applying without touching the value locks what is there — the sketch must not move.
		var before = sketch.Points[line.End];
		ConstraintTools.Apply( sketch, length );
		SketchSolver.Solve( sketch );

		Report.Check( "applying a measured dimension unchanged leaves the sketch where it was",
			(sketch.Points[line.End] - before).Length < 1e-3f );
	}

	static void TestDuplicates()
	{
		var sketch = new Sketch();
		var a = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );
		var b = sketch.AddLine( new Vec2( 0, 2 ), new Vec2( 4, 3 ) );

		var selection = new SketchSelection( null, new[] { a.Id, b.Id } );

		Apply( sketch, selection, "Parallel" );

		Report.Check( "the rule is on the sketch", sketch.Constraints.Count == 1 );

		Report.Check( "and is no longer offered",
			!Labels( sketch, selection ).Contains( "Parallel" ),
			string.Join( ", ", Labels( sketch, selection ) ) );

		// ORDER-INSENSITIVE. "A parallel to B" and "B parallel to A" are one rule, and offering the
		// second is how a sketch quietly acquires the redundancy that makes the NEXT dimension
		// appear to do nothing.
		var swapped = new SketchSelection( null, new[] { b.Id, a.Id } );

		Report.Check( "nor is it offered with the two lines the other way round",
			!Labels( sketch, swapped ).Contains( "Parallel" ),
			string.Join( ", ", Labels( sketch, swapped ) ) );

		Report.Check( "applying it again is refused",
			!ConstraintTools.Apply( sketch, new ConstraintOffer
			{
				Kind = SketchConstraintKind.Parallel,
				Constraint = new SketchConstraint( SketchConstraintKind.Parallel, b.Start, b.End, a.Start, a.End ),
			} ) );

		Report.Check( "so the sketch still holds one", sketch.Constraints.Count == 1 );

		// Reversing one line's own endpoints is the same segment too.
		Report.Check( "nor with one line's ends reversed",
			ConstraintTools.Has( sketch,
				new SketchConstraint( SketchConstraintKind.Parallel, a.End, a.Start, b.Start, b.End ) ) );

		// SYMMETRIC IS THE EXCEPTION, and has to be. Its first pair is what gets mirrored and its
		// second is the mirror; swapping them is a different rule, not the same one phrased twice.
		var mirror = new Sketch();
		var axis = mirror.AddLine( new Vec2( 0, -5 ), new Vec2( 0, 5 ) );
		var p = mirror.AddPoint( new Vec2( -3, 0 ) );
		var q = mirror.AddPoint( new Vec2( 3, 0 ) );

		mirror.Constraints.Add( new SketchConstraint( SketchConstraintKind.Symmetric, p, q, axis.Start, axis.End ) );

		Report.Check( "swapping a symmetric constraint's pairs is a different rule",
			!ConstraintTools.Has( mirror,
				new SketchConstraint( SketchConstraintKind.Symmetric, axis.Start, axis.End, p, q ) ) );

		Report.Check( "but the same rule is still recognised",
			ConstraintTools.Has( mirror,
				new SketchConstraint( SketchConstraintKind.Symmetric, q, p, axis.End, axis.Start ) ) );
	}

	/// <summary>
	/// A circle's radius is a stored float, not two points, so the solver has nothing to act on and
	/// no radius constraint is offered for one. That is a real limitation and the honest thing is to
	/// say so here rather than to offer a control that silently does nothing.
	/// </summary>
	static void TestCircleRadius()
	{
		var sketch = new Sketch();
		var circle = sketch.AddCircle( new Vec2( 0, 0 ), 3f );

		Report.Check( "a circle offers no radius constraint",
			ConstraintTools.Offers( sketch, new SketchSelection( null, new[] { circle.Id } ) ).Count == 0 );

		// An ARC does, because its radius is the distance between two of its points.
		var arcSketch = new Sketch();
		var centre = arcSketch.AddPoint( new Vec2( 0, 0 ) );
		var start = arcSketch.AddPoint( new Vec2( 3, 0 ) );
		var end = arcSketch.AddPoint( new Vec2( 0, 3 ) );
		var arc = arcSketch.Add( new SketchArc( centre, start, end ) );

		var offers = ConstraintTools.Offers( arcSketch, new SketchSelection( null, new[] { arc.Id } ) );

		Report.Check( "an arc does", offers.Count == 2 && offers[0].Label == "Radius",
			string.Join( ", ", offers.Select( o => o.Label ) ) );

		Report.Check( "pre-filled with the radius it has", MathF.Abs( offers[0].Value - 3f ) < 1e-4f );

		// RADIUS AND DIAMETER ARE ONE RULE WRITTEN TWO WAYS. Both are offered while the arc is
		// undimensioned, and the number each opens on is its own.
		Report.Check( "and a diameter alongside it", offers[1].Label == "Diameter" );

		Report.Check( "opening on twice the radius", MathF.Abs( offers[1].Value - 6f ) < 1e-4f,
			$"{offers[1].Value:0.###}" );

		offers[0].Value = 5f;
		ConstraintTools.Apply( arcSketch, offers[0] );
		SketchSolver.Solve( arcSketch );

		Report.Check( "and driving it moves the arc",
			MathF.Abs( (arcSketch.Points[start] - arcSketch.Points[centre]).Length - 5f ) < 1e-3f,
			$"{(arcSketch.Points[start] - arcSketch.Points[centre]).Length:0.####}" );

		// The implicit arc invariant keeps the far end with it — an arc whose ends sit at different
		// distances from its centre is not an arc.
		Report.Check( "taking the other end with it",
			MathF.Abs( (arcSketch.Points[end] - arcSketch.Points[centre]).Length - 5f ) < 1e-3f,
			$"{(arcSketch.Points[end] - arcSketch.Points[centre]).Length:0.####}" );

		// AND NOW NEITHER IS OFFERED. A sketch carrying both would solve and then report
		// redundancy, which is a puzzling way to be told the arc was already dimensioned.
		var after = Labels( arcSketch, new SketchSelection( null, new[] { arc.Id } ) );

		Report.Check( "a dimensioned arc is offered neither of them again",
			after.Count == 0, string.Join( ", ", after ) );
	}

	/// <summary>
	/// The six kinds that solved, marked up and round-tripped through the file with no selection
	/// anywhere that produced one.
	///
	/// OFFERED AND THEN APPLIED, in that order and never only the first. What a selection allows is
	/// a table, and a table reads as correct while the point indices inside it are wrong; only the
	/// geometry moving proves each constraint's slots were filled in the order it reads them.
	/// </summary>
	static void TestTheUnreachableSix()
	{
		// --- fix ------------------------------------------------------------------------------
		var fixSketch = new Sketch();
		var bar = fixSketch.AddLine( new Vec2( 2, 3 ), new Vec2( 6, 3 ) );

		var single = Labels( fixSketch, new SketchSelection( new[] { bar.Start } ) );

		Report.Check( "one point on its own offers a fix",
			single.SequenceEqual( new[] { "Fix" } ), string.Join( ", ", single ) );

		Apply( fixSketch, new SketchSelection( new[] { bar.Start } ), "Fix" );

		// A FIX IS THE ONE RULE WHOSE VALUE IS A POSITION. Carrying only the x through the offer
		// would nail the point to the right column of a sketch and to y = 0.
		Report.Check( "a fix remembers both coordinates",
			MathF.Abs( fixSketch.Constraints[0].Value - 2f ) < 1e-4f
			&& MathF.Abs( fixSketch.Constraints[0].ValueY - 3f ) < 1e-4f,
			$"{fixSketch.Constraints[0].Value:0.##}, {fixSketch.Constraints[0].ValueY:0.##}" );

		fixSketch.Points[bar.Start] = new Vec2( 9, 9 );
		SketchSolver.Solve( fixSketch, bar.End );

		Report.Check( "and drags the point back where it was fixed",
			(fixSketch.Points[bar.Start] - new Vec2( 2, 3 )).Length < 1e-3f,
			$"({fixSketch.Points[bar.Start].x:0.##},{fixSketch.Points[bar.Start].y:0.##})" );

		// --- midpoint -------------------------------------------------------------------------
		var mid = new Sketch();
		var rail = mid.AddLine( new Vec2( 0, 0 ), new Vec2( 10, 0 ) );
		var free = mid.AddPoint( new Vec2( 1, 4 ) );

		Apply( mid, new SketchSelection( new[] { free }, new[] { rail.Id } ), "Midpoint" );
		SketchSolver.Solve( mid, rail.Start );

		// Against the line WHEREVER IT ENDED UP, not against (5,0): the solver is free to move the
		// line as well as the point, and the rule is about the relation rather than the place.
		Report.Check( "midpoint puts the point half way along the line",
			(mid.Points[free] - (mid.Points[rail.Start] + mid.Points[rail.End]) * 0.5f).Length < 1e-3f,
			$"({mid.Points[free].x:0.##},{mid.Points[free].y:0.##})" );

		// --- concentric -----------------------------------------------------------------------
		//
		// The one new rule a CIRCLE can take part in: it contributes its centre to the solve and
		// nothing else, which is all a concentricity needs.
		var conc = new Sketch();
		var outer = conc.AddCircle( new Vec2( 0, 0 ), 3f );
		var inner = conc.AddCircle( new Vec2( 4, 1 ), 1.5f );

		var both = Labels( conc, new SketchSelection( null, new[] { outer.Id, inner.Id } ) );

		Report.Check( "two circles offer concentric and nothing else",
			both.SequenceEqual( new[] { "Concentric" } ), string.Join( ", ", both ) );

		Apply( conc, new SketchSelection( null, new[] { outer.Id, inner.Id } ), "Concentric" );
		SketchSolver.Solve( conc, outer.Center );

		Report.Check( "and applying it brings their centres together",
			(conc.Points[inner.Center] - conc.Points[outer.Center]).Length < 1e-3f,
			$"{(conc.Points[inner.Center] - conc.Points[outer.Center]).Length:0.####}" );

		// --- a line tangent to an arc -----------------------------------------------------------
		var tangent = new Sketch();
		var hub = tangent.AddPoint( new Vec2( 0, 0 ) );
		var rim = tangent.AddPoint( new Vec2( 2, 0 ) );
		var tail = tangent.AddPoint( new Vec2( 0, 2 ) );
		var curve = tangent.Add( new SketchArc( hub, rim, tail ) );
		var edge = tangent.AddLine( new Vec2( -4, 3 ), new Vec2( 4, 3 ) );

		var lineAndArc = Labels( tangent, new SketchSelection( null, new[] { edge.Id, curve.Id } ) );

		Report.Check( "a line and an arc offer a tangency",
			lineAndArc.SequenceEqual( new[] { "Tangent" } ), string.Join( ", ", lineAndArc ) );

		Apply( tangent, new SketchSelection( null, new[] { edge.Id, curve.Id } ), "Tangent" );
		SketchSolver.Solve( tangent, hub );

		var reach = (tangent.Points[rim] - tangent.Points[hub]).Length;
		var standoff = DistanceToLine( tangent, hub, edge.Start, edge.End );

		Report.Check( "and the line ends up exactly a radius from the centre",
			MathF.Abs( standoff - reach ) < 1e-3f, $"{standoff:0.####} against a radius of {reach:0.####}" );

		// --- two arcs tangent to each other -----------------------------------------------------
		var pairSketch = new Sketch();
		var leftHub = pairSketch.AddPoint( new Vec2( 0, 0 ) );
		var leftRim = pairSketch.AddPoint( new Vec2( 2, 0 ) );
		var leftTail = pairSketch.AddPoint( new Vec2( 0, 2 ) );
		var left = pairSketch.Add( new SketchArc( leftHub, leftRim, leftTail ) );

		var rightHub = pairSketch.AddPoint( new Vec2( 6, 0 ) );
		var rightRim = pairSketch.AddPoint( new Vec2( 7, 0 ) );
		var rightTail = pairSketch.AddPoint( new Vec2( 6, 1 ) );
		var right = pairSketch.Add( new SketchArc( rightHub, rightRim, rightTail ) );

		var twoArcs = Labels( pairSketch, new SketchSelection( null, new[] { left.Id, right.Id } ) );

		Report.Check( "two arcs offer equal radius, concentric and a tangency",
			twoArcs.SequenceEqual( new[] { "Concentric", "Equal radius", "Tangent" } ),
			string.Join( ", ", twoArcs ) );

		Apply( pairSketch, new SketchSelection( null, new[] { left.Id, right.Id } ), "Tangent" );
		SketchSolver.Solve( pairSketch, leftHub );

		var apart = (pairSketch.Points[rightHub] - pairSketch.Points[leftHub]).Length;
		var leftRadius = (pairSketch.Points[leftRim] - pairSketch.Points[leftHub]).Length;
		var rightRadius = (pairSketch.Points[rightRim] - pairSketch.Points[rightHub]).Length;

		Report.Check( "two arcs drawn apart are made to touch on the outside",
			MathF.Abs( apart - (leftRadius + rightRadius) ) < 1e-3f,
			$"{apart:0.###} apart against radii of {leftRadius:0.###} and {rightRadius:0.###}" );

		// WHICH TANGENCY IS READ OFF THE SKETCH. A small arc drawn inside a big one is asking for
		// the internal kind, and offering the external one would shove it out through the wall.
		var nested = new Sketch();
		var bigHub = nested.AddPoint( new Vec2( 0, 0 ) );
		var bigRim = nested.AddPoint( new Vec2( 5, 0 ) );
		var bigTail = nested.AddPoint( new Vec2( 0, 5 ) );
		var big = nested.Add( new SketchArc( bigHub, bigRim, bigTail ) );

		var smallHub = nested.AddPoint( new Vec2( 1, 0 ) );
		var smallRim = nested.AddPoint( new Vec2( 2, 0 ) );
		var smallTail = nested.AddPoint( new Vec2( 1, 1 ) );
		var small = nested.Add( new SketchArc( smallHub, smallRim, smallTail ) );

		var selection = new SketchSelection( null, new[] { big.Id, small.Id } );
		var internalOffer = ConstraintTools.Offers( nested, selection ).Single( o => o.Label == "Tangent" );

		Report.Check( "an arc drawn inside another is offered the internal tangency",
			internalOffer.Value != 0f, $"{internalOffer.Value}" );

		ConstraintTools.Apply( nested, internalOffer );

		// The flag lives in Value, which Apply writes from the offer - so this is also the check
		// that a plain rule's value survives the trip through the offer rather than being zeroed.
		Report.Check( "and the rule keeps that flag when it lands on the sketch",
			nested.Constraints.Single().Value != 0f );

		SketchSolver.Solve( nested, bigHub );

		var gap = (nested.Points[smallHub] - nested.Points[bigHub]).Length;
		var bigRadius = (nested.Points[bigRim] - nested.Points[bigHub]).Length;
		var smallRadius = (nested.Points[smallRim] - nested.Points[smallHub]).Length;

		Report.Check( "so it touches from the inside rather than being pushed out",
			MathF.Abs( gap - MathF.Abs( bigRadius - smallRadius ) ) < 1e-3f,
			$"{gap:0.###} apart against radii of {bigRadius:0.###} and {smallRadius:0.###}" );
	}

	static void TestTouching()
	{
		var sketch = new Sketch();
		var a = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );
		var b = sketch.AddLine( new Vec2( 4, 0 ), new Vec2( 4, 3 ) );

		Apply( sketch, new SketchSelection( null, new[] { a.Id } ), "Horizontal" );
		Apply( sketch, new SketchSelection( null, new[] { b.Id } ), "Vertical" );
		Apply( sketch, new SketchSelection( new[] { a.End, b.Start } ), "Coincident" );

		Report.Check( "three rules on the sketch", sketch.Constraints.Count == 3 );

		var atCorner = ConstraintTools.Touching( sketch, a.End );

		Report.Check( "the corner point is held by the horizontal and the coincidence",
			atCorner.Count == 2, $"{atCorner.Count}" );

		var onFirst = ConstraintTools.Touching( sketch, a );

		Report.Check( "the first line is held by two rules", onFirst.Count == 2, $"{onFirst.Count}" );

		Report.Check( "a point nothing holds comes back empty",
			ConstraintTools.Touching( sketch, b.End ).Count == 1,
			$"{ConstraintTools.Touching( sketch, b.End ).Count}" );

		// Removing one is how a UI undoes a rule, and the sketch has to keep solving after it.
		sketch.Constraints.Remove( atCorner[0] );

		Report.Check( "the sketch still solves with one taken away",
			SketchSolver.Solve( sketch ).Converged );
	}

	/// <summary>
	/// A contradictory constraint must leave NOTHING behind — not the rule, and not the positions
	/// the failed solve dragged the sketch through on its way to giving up.
	///
	/// The second half is the part that is easy to get wrong. Removing the rule and solving again
	/// converges to *a* valid answer, which need not be the one that was on screen a moment ago, so
	/// the user watches their geometry shift in response to an operation that reported failure.
	/// </summary>
	static void TestRefused()
	{
		var sketch = new Sketch();
		var line = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );

		var length = ConstraintTools.Offers( sketch, new SketchSelection( null, new[] { line.Id } ) )
			.Single( o => o.Label == "Length" );

		length.Value = 6f;

		Report.Check( "a first dimension applies", ConstraintTools.ApplyAndSolve( sketch, length ).Applied );

		var settled = sketch.Points.Select( p => p ).ToList();

		// A second length on the same line, saying something else. Both cannot be true.
		var contradiction = new ConstraintOffer
		{
			Kind = SketchConstraintKind.Distance,
			Label = "Length",
			NeedsValue = true,
			Value = 9f,
			Constraint = new SketchConstraint( SketchConstraintKind.Distance, line.Start, line.End ),
		};

		var refused = ConstraintTools.ApplyAndSolve( sketch, contradiction );

		Report.Check( "a contradictory dimension is refused", !refused.Applied );

		Report.Check( "and says why", refused.Message is not null, refused.Message ?? "no message" );

		Report.Check( "the rule is not left on the sketch", sketch.Constraints.Count == 1,
			$"{sketch.Constraints.Count} constraints" );

		Report.Check( "and the geometry is exactly where it was",
			sketch.Points.Zip( settled, ( a, b ) => (a - b).Length ).All( d => d < 1e-6f ),
			$"line is now {(sketch.Points[line.End] - sketch.Points[line.Start]).Length:0.####} long" );

		Report.Check( "so the first dimension still holds",
			MathF.Abs( (sketch.Points[line.End] - sketch.Points[line.Start]).Length - 6f ) < 1e-3f );

		// A duplicate is refused too, and differently — it is not a contradiction, it is already true.
		var duplicate = new ConstraintOffer
		{
			Kind = SketchConstraintKind.Distance,
			Label = "Length",
			Value = 6f,
			Constraint = new SketchConstraint( SketchConstraintKind.Distance, line.End, line.Start ),
		};

		var already = ConstraintTools.ApplyAndSolve( sketch, duplicate );

		Report.Check( "a duplicate is refused", !already.Applied );

		Report.Check( "and says the sketch already has it",
			already.Message is not null && already.Message.Contains( "already" ), already.Message ?? "" );

		Report.Check( "without adding a second copy", sketch.Constraints.Count == 1 );

		// A successful apply reports the freedom left, which is what a UI shows next.
		var fresh = new Sketch();
		var only = fresh.AddLine( new Vec2( 0, 0 ), new Vec2( 3, 1 ) );

		var horizontal = ConstraintTools.Offers( fresh, new SketchSelection( null, new[] { only.Id } ) )
			.Single( o => o.Label == "Horizontal" );

		var applied = ConstraintTools.ApplyAndSolve( fresh, horizontal );

		Report.Check( "a successful apply carries the solve with it",
			applied.Applied && applied.Solve is not null );

		Report.Check( "with the degrees of freedom left to report",
			applied.Solve.DegreesOfFreedom > 0, $"{applied.Solve.DegreesOfFreedom}" );
	}

	/// <summary>
	/// Where each rule's glyph goes.
	///
	/// This is geometry, so it is testable, and it is worth testing because getting it slightly
	/// wrong produces a sketch covered in marks that are all in nearly the right place — which is
	/// the kind of wrong nobody reports as a bug and everybody works around.
	/// </summary>
	static void TestMarkers()
	{
		var sketch = new Sketch();
		var line = sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );

		Apply( sketch, new SketchSelection( null, new[] { line.Id } ), "Horizontal" );

		var marks = ConstraintTools.Markers( sketch );

		Report.Check( "one rule on one line makes one mark", marks.Count == 1 );

		Report.Check( "at the middle of the line it holds",
			(marks[0].Anchor - new Vec2( 2, 0 )).Length < 1e-4f,
			$"({marks[0].Anchor.x:0.##},{marks[0].Anchor.y:0.##})" );

		Report.Check( "labelled for what it is", marks[0].Label == "H", marks[0].Label );

		Report.Check( "and it points off the line rather than along it",
			MathF.Abs( Vec2.Dot( marks[0].Away, new Vec2( 1, 0 ) ) ) < 1e-4f
			&& MathF.Abs( marks[0].Away.Length - 1f ) < 1e-4f,
			$"({marks[0].Away.x:0.##},{marks[0].Away.y:0.##})" );

		Report.Check( "and carries the rule itself, so clicking it can delete it",
			ReferenceEquals( marks[0].Constraint, sketch.Constraints[0] ) );

		Report.Check( "a plain rule is not a dimension", !marks[0].IsDimension );

		// A RULE ABOUT TWO SEGMENTS MARKS BOTH. One glyph between them would leave you guessing
		// which pair it meant on a sketch with six lines in it.
		var second = sketch.AddLine( new Vec2( 0, 3 ), new Vec2( 4, 3 ) );

		Apply( sketch, new SketchSelection( null, new[] { line.Id, second.Id } ), "Equal length" );

		var equals = ConstraintTools.Markers( sketch ).Where( m => m.Label == "=" ).ToList();

		Report.Check( "equal length marks both lines", equals.Count == 2 );

		Report.Check( "one on each, at their middles",
			equals.Any( m => (m.Anchor - new Vec2( 2, 0 )).Length < 1e-4f )
			&& equals.Any( m => (m.Anchor - new Vec2( 2, 3 )).Length < 1e-4f ) );

		// A DIMENSION READS ITS VALUE.
		var dim = new Sketch();
		var bar = dim.AddLine( new Vec2( 0, 0 ), new Vec2( 7.5f, 0 ) );

		Apply( dim, new SketchSelection( null, new[] { bar.Id } ), "Length" );

		var length = ConstraintTools.Markers( dim ).Single();

		Report.Check( "a dimension is labelled with its number",
			length.Label == "7.500", length.Label );

		Report.Check( "and says it is a dimension", length.IsDimension );

		// AN ANGLE IS MARKED WHERE ITS LINES CROSS, which is where a person looks for it.
		var corner = new Sketch();
		var across = corner.AddLine( new Vec2( -4, 0 ), new Vec2( 4, 0 ) );
		var up = corner.AddLine( new Vec2( 0, -3 ), new Vec2( 0, 5 ) );

		Apply( corner, new SketchSelection( null, new[] { across.Id, up.Id } ), "Angle" );

		var angle = ConstraintTools.Markers( corner ).Single();

		Report.Check( "an angle sits at the crossing, not between the midpoints",
			angle.Anchor.Length < 1e-4f, $"({angle.Anchor.x:0.###},{angle.Anchor.y:0.###})" );

		Report.Check( "labelled in degrees", angle.Label.EndsWith( "\u00b0" ), angle.Label );

		// Two lines that never touch still have an angle, out where the extended lines would meet —
		// which is what the angle between them means.
		var apart = new Sketch();
		var flat = apart.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );
		var slanted = apart.AddLine( new Vec2( 6, 2 ), new Vec2( 10, 6 ) );

		Apply( apart, new SketchSelection( null, new[] { flat.Id, slanted.Id } ), "Angle" );

		var extended = ConstraintTools.Markers( apart ).Single();

		Report.Check( "two lines that do not touch are marked where they would meet",
			(extended.Anchor - new Vec2( 4, 0 )).Length < 1e-3f,
			$"({extended.Anchor.x:0.###},{extended.Anchor.y:0.###})" );

		// Parallel lines have no crossing at all, and must not produce a NaN.
		var parallel = new Sketch();
		var one = parallel.AddLine( new Vec2( 0, 0 ), new Vec2( 4, 0 ) );
		var two = parallel.AddLine( new Vec2( 0, 2 ), new Vec2( 4, 2 ) );

		parallel.Constraints.Add( new SketchConstraint( SketchConstraintKind.Angle,
			one.Start, one.End, two.Start, two.End ) { Value = 0f } );

		var noCrossing = ConstraintTools.Markers( parallel ).Single();

		Report.Check( "parallel lines fall back to a real position rather than a NaN",
			float.IsFinite( noCrossing.Anchor.x ) && float.IsFinite( noCrossing.Anchor.y ),
			$"({noCrossing.Anchor.x},{noCrossing.Anchor.y})" );

		// A RULE LEFT BEHIND BY A DELETED CURVE MARKS NOTHING, and must not throw. This is ordinary
		// rather than exceptional — deleting a curve leaves its rules pointing at points that went
		// with it, which is why the solver drops them too.
		var stale = new Sketch();
		stale.AddPoint( new Vec2( 0, 0 ) );
		stale.Constraints.Add( new SketchConstraint( SketchConstraintKind.Distance, 0, 99, 5f ) );

		Report.Check( "a rule pointing at a point that is gone marks nothing",
			ConstraintTools.Markers( stale ).Count == 0 );

		Report.Check( "an empty sketch marks nothing",
			ConstraintTools.Markers( new Sketch() ).Count == 0 );

		Report.Check( "and a null one does not throw",
			ConstraintTools.Markers( null ).Count == 0 );
	}

	// --- helpers ------------------------------------------------------------------------------

	/// <summary>Perpendicular distance from a point to the infinite line through two others — the
	/// relation "on the line" actually asserts, wherever the line has ended up.</summary>
	static float DistanceToLine( Sketch sketch, int point, int a, int b ) =>
		DistanceToLine( sketch.Points[point], sketch.Points[a], sketch.Points[b] );

	static float DistanceToLine( Vec2 p, Vec2 a, Vec2 b )
	{
		var along = b - a;

		if ( along.Length < 1e-9f )
			return (p - a).Length;

		return MathF.Abs( Vec2.Cross( along, p - a ) ) / along.Length;
	}

	static List<string> Labels( Sketch sketch, SketchSelection selection ) =>
		ConstraintTools.Offers( sketch, selection ).Select( o => o.Label ).OrderBy( s => s ).ToList();

	static void Apply( Sketch sketch, SketchSelection selection, string label )
	{
		var offer = ConstraintTools.Offers( sketch, selection ).SingleOrDefault( o => o.Label == label );

		if ( offer is null )
			throw new InvalidOperationException( $"no offer labelled '{label}' for that selection" );

		ConstraintTools.Apply( sketch, offer );
	}
}
