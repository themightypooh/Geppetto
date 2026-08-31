using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// The sketch constraint solver.
///
/// Two kinds of check, because a solver fails in two unrelated ways.
///
/// FIRST, EVERY DERIVATIVE AGAINST A FINITE DIFFERENCE. A wrong Jacobian entry does not produce a
/// wrong answer, it produces a slow or unstable one — the residual still drives toward zero, just
/// along a worse path — so it shows up as "the solver feels flaky" and never as a failing assert.
/// Comparing each analytic derivative to a central difference of the residual it claims to
/// differentiate catches a swapped term or a sign the moment it is written.
///
/// SECOND, SOLVES WITH KNOWN ANSWERS. A perfectly differentiable solver can still converge on the
/// wrong shape if the residual itself says the wrong thing. So the sketches here have answers that
/// can be written down in advance: a rectangle whose corner must land at an exact coordinate, a
/// perpendicular that must come out at exactly 90 degrees.
/// </summary>
public static class ConstraintTests
{
	public static void Run()
	{
		Report.Section( "constraints: analytic derivatives match finite differences" );
		TestJacobians();

		Report.Section( "solver: sketches with answers known in advance" );
		TestKnownSolves();

		Report.Section( "solver: degrees of freedom and redundancy" );
		TestDiagnosis();

		Report.Section( "solver: the cases that must not break anything" );
		TestDegenerate();

		Report.Section( "solver: constraints run as part of a rebuild" );
		TestInFeatureTree();

		Report.Section( "solver: the constraints a dimension tool needs" );
		TestDimensionConstraints();

		Report.Section( "solver: an arc's endpoints stay on its own circle" );
		TestArcStaysAnArc();

		Report.Section( "solver: tangency, midpoint, fix, concentric and diameter" );
		TestNewRules();
	}

	static void TestDimensionConstraints()
	{
		// ANGLE. Two lines off a shared corner, told to meet at 60 degrees. Asserted as an angle
		// rather than as coordinates, because the solver is free to reach it by turning either line.
		var angle = new Sketch();
		var pivot = angle.AddPoint( 0f, 0f );
		var armA = angle.AddPoint( 2f, 0f );
		var armB = angle.AddPoint( 1.4f, 1.4f );

		var first = angle.Add( new SketchLine( pivot, armA ) );
		var second = angle.Add( new SketchLine( pivot, armB ) );

		angle.Constraints.Add( new SketchConstraint( SketchConstraintKind.Angle, pivot, armA, pivot, armB ) { Value = 60f } );

		var angleResult = angle.Solve();
		var measured = Angle( angle.Points[armA] - angle.Points[pivot], angle.Points[armB] - angle.Points[pivot] );

		Report.Check( "an angle constraint solves", angleResult.Converged,
			$"residual {angleResult.Residual:0.###e0}" );

		Report.Check( "to exactly the angle asked for", MathF.Abs( measured - 60f ) < 0.01f,
			$"got {measured:0.###}" );

		// A right angle through the same constraint has to agree with the dedicated perpendicular
		// one, or two ways of saying the same thing give two different shapes.
		var right = new Sketch();
		var r0 = right.AddPoint( 0f, 0f );
		var r1 = right.AddPoint( 2f, 0f );
		var r2 = right.AddPoint( 1.6f, 1.2f );
		right.Add( new SketchLine( r0, r1 ) );
		right.Add( new SketchLine( r0, r2 ) );
		right.Constraints.Add( new SketchConstraint( SketchConstraintKind.Angle, r0, r1, r0, r2 ) { Value = 90f } );
		right.Solve();

		Report.Check( "and 90 degrees means perpendicular",
			MathF.Abs( Angle( right.Points[r1] - right.Points[r0], right.Points[r2] - right.Points[r0] ) - 90f ) < 0.01f );

		// POINT ON LINE.
		//
		// WORTH READING BEFORE WRITING A TEST LIKE THIS. The first version asserted that the point
		// moved onto the line and the line stayed put — and it failed, because the solver is under no
		// obligation to move the point. "These three are collinear" is equally satisfied by swinging
		// the LINE onto the point, and with nothing holding the line down that is part of what the
		// least-norm step does. It converged perfectly and to a shape the test did not expect, which
		// is the solver being right and the test being wrong.
		//
		// So: assert the relation, and where a specific answer is wanted, hold the reference geometry
		// down first.
		var onLine = new Sketch();
		var lineA = onLine.AddPoint( 0f, 0f );
		var lineB = onLine.AddPoint( 4f, 0f );
		var loose = onLine.AddPoint( 2f, 1.5f );

		var baseLine = onLine.Add( new SketchLine( lineA, lineB ) );
		onLine.Constraints.Add( new SketchConstraint( SketchConstraintKind.PointOnLine, loose, lineA, lineB, 0f ) );

		var looseResult = onLine.Solve();

		Report.Check( "a point-on-line constraint solves", looseResult.Converged,
			$"residual {looseResult.Residual:0.###e0}" );

		Report.Check( "leaving the three genuinely collinear",
			MathF.Abs( Vec2.Cross( onLine.Points[lineB] - onLine.Points[lineA],
				onLine.Points[loose] - onLine.Points[lineA] ) ) < 1e-3f,
			$"cross {Vec2.Cross( onLine.Points[lineB] - onLine.Points[lineA], onLine.Points[loose] - onLine.Points[lineA] ):0.#####}" );

		// Now hold the line horizontal. Point 0 is pinned at the origin, so the line becomes the x
		// axis and the answer is no longer a choice: the point has to come to y = 0.
		onLine.AddConstraint( baseLine, SketchConstraintKind.Horizontal );
		onLine.Points[loose] = new Vec2( 2f, 1.5f );
		onLine.Solve();

		Report.Check( "and with the line held down, the point is the thing that moves",
			MathF.Abs( onLine.Points[loose].y ) < 1e-3f, $"y = {onLine.Points[loose].y:0.#####}" );

		// SYMMETRIC. Two points mirrored about a line, from a start where neither is.
		var mirror = new Sketch();
		var axisA = mirror.AddPoint( 0f, 0f );
		var axisB = mirror.AddPoint( 0f, 4f );
		var leftPoint = mirror.AddPoint( -2f, 1f );
		var rightPoint = mirror.AddPoint( 3f, 2.5f );

		var axis = mirror.Add( new SketchLine( axisA, axisB ) );

		// The axis is held vertical for the same reason the line above is held horizontal: otherwise
		// the cheapest way to make two points symmetric is to swing the axis between them, and the
		// test would be asserting about an axis that had moved.
		mirror.AddConstraint( axis, SketchConstraintKind.Vertical );
		mirror.Constraints.Add( new SketchConstraint( SketchConstraintKind.Symmetric,
			leftPoint, rightPoint, axisA, axisB ) );

		var mirrorResult = mirror.Solve();

		Report.Check( "a symmetry constraint solves", mirrorResult.Converged,
			$"residual {mirrorResult.Residual:0.###e0}" );

		var l = mirror.Points[leftPoint];
		var r = mirror.Points[rightPoint];

		Report.Check( "the two points end up mirrored across the axis",
			MathF.Abs( l.x + r.x ) < 1e-3f && MathF.Abs( l.y - r.y ) < 1e-3f,
			$"{l} and {r}" );

		// AND NOT BY COLLAPSING ONTO THE AXIS, which satisfies "mirrored" in the most useless way
		// available and is what a single-row version of this constraint would allow.
		Report.Check( "rather than both collapsing onto it",
			MathF.Abs( l.x ) > 0.5f, $"they ended up {MathF.Abs( l.x ):0.###} from the axis" );

		// RADIUS on an arc. Its centre-to-endpoint distance is what a radius IS here, and the
		// implicit arc constraint carries the far end along with it.
		var arc = new Sketch();
		var centre = arc.AddPoint( 0f, 0f );
		var start = arc.AddPoint( 2f, 0f );
		var end = arc.AddPoint( 0f, 2f );

		arc.Add( new SketchArc( centre, start, end ) );
		arc.Constraints.Add( new SketchConstraint( SketchConstraintKind.Radius, centre, start, 3f ) );

		arc.Solve();

		Report.Check( "a radius dimension sets the arc's radius",
			MathF.Abs( (arc.Points[start] - arc.Points[centre]).Length - 3f ) < 1e-3f,
			$"{(arc.Points[start] - arc.Points[centre]).Length:0.####}" );

		Report.Check( "and the other end comes with it",
			MathF.Abs( (arc.Points[end] - arc.Points[centre]).Length - 3f ) < 1e-3f,
			$"{(arc.Points[end] - arc.Points[centre]).Length:0.####}" );
	}

	/// <summary>
	/// The invariant nothing used to enforce.
	///
	/// An arc reads its radius off the centre-to-START distance, and Tessellate snaps its last sample
	/// onto End wherever End is. So an End that drifts off the circle does not produce an error — it
	/// produces an arc at the wrong radius with a kink in its final segment, which reads as a
	/// rendering glitch. Nothing moved points apart while coordinates were only typed; a solver moves
	/// points for a living.
	/// </summary>
	static void TestArcStaysAnArc()
	{
		var sketch = new Sketch();
		var centre = sketch.AddPoint( 0f, 0f );
		var start = sketch.AddPoint( 2f, 0f );
		var end = sketch.AddPoint( 0f, 2f );
		var far = sketch.AddPoint( 5f, 5f );

		sketch.Add( new SketchArc( centre, start, end ) );
		var pull = sketch.Add( new SketchLine( end, far ) );

		// A constraint that has every reason to drag End off its circle: it says nothing about the
		// arc, only about the line hanging off its end.
		sketch.Constraints.Add( new SketchConstraint( SketchConstraintKind.Distance, end, far, 6f ) );
		sketch.AddConstraint( pull, SketchConstraintKind.Horizontal );

		var result = sketch.Solve();

		Report.Check( "the sketch solves", result.Converged, $"residual {result.Residual:0.###e0}" );

		var startRadius = (sketch.Points[start] - sketch.Points[centre]).Length;
		var endRadius = (sketch.Points[end] - sketch.Points[centre]).Length;

		Report.Check( "and the arc's two ends are still the same distance from its centre",
			MathF.Abs( startRadius - endRadius ) < 1e-3f,
			$"start {startRadius:0.#####}, end {endRadius:0.#####}" );

		// The tessellation is where the damage would show: its last sample is snapped onto End, so a
		// broken invariant leaves a step between the second-to-last point and the last one.
		var points = sketch.Curves.OfType<SketchArc>().First().Tessellate( sketch, sketch.Tolerance );
		var lastStep = (points[^1] - points[^2]).Length;
		var typicalStep = (points[1] - points[0]).Length;

		Report.Check( "so the arc has no kink in its last segment",
			lastStep < typicalStep * 3f + 1e-4f,
			$"last step {lastStep:0.#####} against a typical {typicalStep:0.#####}" );

		// An arc with no constraints anywhere in the sketch is still untouched: the implicit rule
		// exists to stop a solve breaking an arc, not to move arcs nobody asked about.
		var untouched = new Sketch();
		var uc = untouched.AddPoint( 0f, 0f );
		var us = untouched.AddPoint( 2f, 0f );
		var ue = untouched.AddPoint( 0f, 3f ); // deliberately NOT on the same circle

		untouched.Add( new SketchArc( uc, us, ue ) );
		untouched.Solve();

		Report.Check( "an arc in a sketch with no constraints is left exactly as drawn",
			untouched.Points[ue].y == 3f, $"{untouched.Points[ue]}" );
	}

	static float Angle( Vec2 a, Vec2 b ) =>
		MathF.Acos( Math.Clamp( Vec2.Dot( a.Normal, b.Normal ), -1f, 1f ) ) * 180f / MathF.PI;

	// --- derivatives --------------------------------------------------------------------------

	/// <summary>Central difference, which is second-order accurate and so agrees to far more digits
	/// than a forward difference would. See Differentiate for why the step it divides by is the one
	/// the float array actually took rather than the one that was asked for.</summary>
	static void TestJacobians()
	{
		// Positions chosen to be irregular on purpose. Anything symmetric — a unit square, points
		// on an axis — makes whole families of derivative mistakes cancel out and read as correct.
		var points = new List<Vec2>
		{
			new( 0.3f, -0.7f ),
			new( 2.1f, 0.4f ),
			new( 1.2f, 2.6f ),
			new( -0.9f, 1.1f ),
			new( 3.4f, -1.8f )
		};

		var cases = new (string Name, IConstraint Constraint)[]
		{
			("coincident", new CoincidentConstraint( 0, 1 )),
			("distance", new DistanceConstraint( 0, 1, 2.5 )),
			("horizontal", new HorizontalConstraint( 1, 2 )),
			("vertical", new VerticalConstraint( 2, 3 )),
			("equal length", new EqualLengthConstraint( 0, 1, 2, 3 )),
			("parallel", new ParallelConstraint( 0, 1, 2, 3 )),
			("perpendicular", new PerpendicularConstraint( 0, 1, 3, 4 )),
			("angle", new AngleConstraint( 0, 1, 2, 3, 37.5 )),
			("point on line", new PointOnLineConstraint( 4, 0, 2 )),
			("symmetric", new SymmetricConstraint( 0, 1, 2, 3 )),
			("midpoint", new MidpointConstraint( 4, 0, 2 )),
			("fixed", new FixedConstraint( 3, 1.25, -0.4 )),
			("tangent line-arc", new TangentLineArcConstraint( 0, 1, 2, 3 )),
			("tangent arc-arc, external", new TangentArcArcConstraint( 0, 1, 2, 3 )),
			("tangent arc-arc, internal", new TangentArcArcConstraint( 0, 4, 2, 3, internalTangency: true ))
		};

		foreach ( var (name, constraint) in cases )
		{
			var worst = 0.0;
			var where = "";

			var rows = new ConstraintResult[constraint.ResidualCount];
			constraint.Evaluate( points.ToArray(), rows );

			for ( var row = 0; row < rows.Length; row++ )
			{
				foreach ( var (point, gx, gy) in rows[row].Jacobian )
				{
					var numericX = Differentiate( constraint, points, point, axisX: true, row );
					var numericY = Differentiate( constraint, points, point, axisX: false, row );

					if ( Math.Abs( numericX - gx ) > worst )
					{
						worst = Math.Abs( numericX - gx );
						where = $"row {row}, point {point}, ∂x: analytic {gx:0.######}, numeric {numericX:0.######}";
					}

					if ( Math.Abs( numericY - gy ) > worst )
					{
						worst = Math.Abs( numericY - gy );
						where = $"row {row}, point {point}, ∂y: analytic {gy:0.######}, numeric {numericY:0.######}";
					}
				}
			}

			Report.Check( $"{name}: every derivative matches a finite difference", worst < 1e-4, where );
		}
	}

	/// <summary>
	/// The step is measured, not assumed, and that is not a detail.
	///
	/// Sketch points are float. Ask for a step of h and what actually lands in the array is the
	/// nearest float to x+h, which for a coordinate of a few units is off by around 1e-7 — a tenth
	/// of a percent of a 1e-4 step. Dividing by the REQUESTED h then reports every derivative wrong
	/// by about 1e-3, which looks exactly like a genuine sign-or-term bug and is nothing of the
	/// kind. Dividing by the step the float array actually took removes it completely, and leaves
	/// the residual difference itself as the only error left.
	/// </summary>
	static double Differentiate( IConstraint constraint, List<Vec2> points, int point, bool axisX, int row )
	{
		const double h = 1e-3;

		var moved = new Vec2[points.Count];
		var rows = new ConstraintResult[constraint.ResidualCount];

		(double Residual, double At) Sample( double delta )
		{
			for ( var i = 0; i < points.Count; i++ )
				moved[i] = points[i];

			moved[point] = axisX
				? new Vec2( (float)(points[point].x + delta), points[point].y )
				: new Vec2( points[point].x, (float)(points[point].y + delta) );

			constraint.Evaluate( moved, rows );

			return (rows[row].Residual, axisX ? moved[point].x : moved[point].y);
		}

		var plus = Sample( h );
		var minus = Sample( -h );

		return (plus.Residual - minus.Residual) / (plus.At - minus.At);
	}

	// --- solves with known answers ------------------------------------------------------------

	static void TestKnownSolves()
	{
		// A quadrilateral dragged out of square, told to be a rectangle 4 x 2 with its base
		// horizontal. Point 0 is pinned, so the answer is fully determined and can be written down:
		// exactly (0,0), (4,0), (4,2), (0,2).
		var sketch = new Sketch();
		var p0 = sketch.AddPoint( 0f, 0f );
		var p1 = sketch.AddPoint( 3.6f, 0.35f );
		var p2 = sketch.AddPoint( 4.3f, 1.7f );
		var p3 = sketch.AddPoint( -0.2f, 2.4f );

		var bottom = sketch.Add( new SketchLine( p0, p1 ) );
		var right = sketch.Add( new SketchLine( p1, p2 ) );
		var top = sketch.Add( new SketchLine( p2, p3 ) );
		var left = sketch.Add( new SketchLine( p3, p0 ) );

		sketch.AddConstraint( bottom, SketchConstraintKind.Horizontal );
		sketch.AddConstraint( top, SketchConstraintKind.Horizontal );
		sketch.AddConstraint( left, SketchConstraintKind.Vertical );
		sketch.AddConstraint( right, SketchConstraintKind.Vertical );
		sketch.AddConstraint( SketchConstraintKind.Distance, p0, p1, 4f );
		sketch.AddConstraint( SketchConstraintKind.Distance, p1, p2, 2f );

		var result = sketch.Solve();

		Report.Check( "a dragged quad solves to a rectangle", result.Converged,
			$"residual {result.Residual:0.###e0} after {result.Iterations} iterations" );

		Report.Check( "the pinned corner did not move", Near( sketch.Points[p0], 0f, 0f ),
			$"{sketch.Points[p0]}" );

		Report.Check( "and every other corner landed where the constraints say",
			Near( sketch.Points[p1], 4f, 0f ) && Near( sketch.Points[p2], 4f, 2f ) && Near( sketch.Points[p3], 0f, 2f ),
			$"{sketch.Points[p1]}, {sketch.Points[p2]}, {sketch.Points[p3]}" );

		// The old curve-id storage still has to solve. Sketches drawn before the solver existed
		// carry Horizontal and Vertical against a CURVE, and if Build stopped resolving those, every
		// one of them would quietly lose its constraints instead of failing.
		var legacy = new Sketch();
		var a = legacy.AddPoint( 0f, 0f );
		var b = legacy.AddPoint( 3f, 0.9f );
		var line = legacy.Add( new SketchLine( a, b ) );
		legacy.AddConstraint( line, SketchConstraintKind.Horizontal );

		var legacyResult = legacy.Solve();

		Report.Check( "a constraint stored the old way, against a curve, still solves",
			legacyResult.Converged && Math.Abs( legacy.Points[b].y ) < 1e-3f,
			$"y = {legacy.Points[b].y}" );

		// Perpendicular, checked as an angle rather than as coordinates: the solver is free to
		// rotate the second line either way, so the assert is on what was actually asked for.
		var corner = new Sketch();
		var c0 = corner.AddPoint( 0f, 0f );
		var c1 = corner.AddPoint( 2f, 0f );
		var c2 = corner.AddPoint( 2.4f, 1.6f );
		var armA = corner.Add( new SketchLine( c0, c1 ) );
		var armB = corner.Add( new SketchLine( c1, c2 ) );
		corner.AddConstraint( SketchConstraintKind.Perpendicular, armA, armB );

		corner.Solve();

		var u = corner.Points[c1] - corner.Points[c0];
		var v = corner.Points[c2] - corner.Points[c1];
		var angle = MathF.Acos( Math.Clamp( Vec2.Dot( u.Normal, v.Normal ), -1f, 1f ) ) * 180f / MathF.PI;

		Report.Check( "perpendicular comes out at 90 degrees", MathF.Abs( angle - 90f ) < 0.01f,
			$"got {angle:0.###}" );

		// Equal length says nothing about what the length IS, only that they match. So the assert
		// is that they match, and that the answer sits between the two starting lengths rather than
		// collapsing both to zero — which also satisfies "equal" and is the failure worth catching.
		var equal = new Sketch();
		var e0 = equal.AddPoint( 0f, 0f );
		var e1 = equal.AddPoint( 4f, 0f );
		var e2 = equal.AddPoint( 0f, 3f );
		var e3 = equal.AddPoint( 1f, 3f );
		var longer = equal.Add( new SketchLine( e0, e1 ) );
		var shorter = equal.Add( new SketchLine( e2, e3 ) );
		equal.AddConstraint( SketchConstraintKind.EqualLength, longer, shorter );

		equal.Solve();

		var lenA = (equal.Points[e1] - equal.Points[e0]).Length;
		var lenB = (equal.Points[e3] - equal.Points[e2]).Length;

		Report.Check( "equal length makes two segments the same length",
			MathF.Abs( lenA - lenB ) < 1e-3f, $"{lenA:0.####} vs {lenB:0.####}" );

		Report.Check( "and does not satisfy itself by collapsing both to nothing",
			lenA > 0.5f && lenA < 4f, $"settled at {lenA:0.####}" );

		// Parallel is sign-blind by construction, so antiparallel must count as solved. Starting
		// the second line pointing roughly backwards is the case that would fail if the residual
		// were an angle difference instead of a cross product.
		var parallel = new Sketch();
		var q0 = parallel.AddPoint( 0f, 0f );
		var q1 = parallel.AddPoint( 2f, 0f );
		var q2 = parallel.AddPoint( 3f, 1f );
		var q3 = parallel.AddPoint( 1.2f, 1.4f );
		var refLine = parallel.Add( new SketchLine( q0, q1 ) );
		var swung = parallel.Add( new SketchLine( q2, q3 ) );
		parallel.AddConstraint( SketchConstraintKind.Parallel, refLine, swung );

		var parallelResult = parallel.Solve();
		var cross = Vec2.Cross( (parallel.Points[q1] - parallel.Points[q0]).Normal,
			(parallel.Points[q3] - parallel.Points[q2]).Normal );

		Report.Check( "an antiparallel line counts as parallel and solves",
			parallelResult.Converged && MathF.Abs( cross ) < 1e-3f, $"cross {cross:0.#####}" );

		// Coincident is the one that has to move two points onto each other in both axes at once.
		var joined = new Sketch();
		var j0 = joined.AddPoint( 0f, 0f );
		var j1 = joined.AddPoint( 1f, 1f );
		var j2 = joined.AddPoint( 2.5f, -0.5f );
		joined.AddConstraint( SketchConstraintKind.Coincident, j1, j2 );

		joined.Solve();

		Report.Check( "coincident brings two points together",
			(joined.Points[j2] - joined.Points[j1]).Length < 1e-4f,
			$"{joined.Points[j1]} vs {joined.Points[j2]}" );

		Report.Check( "and leaves the pinned point alone", Near( joined.Points[j0], 0f, 0f ) );
	}

	// --- diagnosis ----------------------------------------------------------------------------

	static void TestDiagnosis()
	{
		// A single free point with nothing said about it: two degrees of freedom, plus the rotation
		// the pin cannot remove. One constraint on it removes exactly one.
		var sketch = new Sketch();
		var p0 = sketch.AddPoint( 0f, 0f );
		var p1 = sketch.AddPoint( 1f, 1f );
		var p2 = sketch.AddPoint( 2f, 0.5f );
		var line = sketch.Add( new SketchLine( p0, p1 ) );
		sketch.Add( new SketchLine( p1, p2 ) );

		sketch.AddConstraint( line, SketchConstraintKind.Horizontal );

		var one = sketch.Solve();

		// Four free variables (two unpinned points), one independent constraint.
		Report.Check( "one constraint on four free variables leaves three degrees of freedom",
			one.DegreesOfFreedom == 3, $"got {one.DegreesOfFreedom}" );

		Report.Check( "and nothing is redundant", one.RedundantConstraints == 0,
			$"got {one.RedundantConstraints}" );

		// THE CASE COUNTING ROWS CANNOT TELL APART. Saying a line is horizontal twice is two
		// constraint rows and one actual restriction. A solver that counted rows would report a
		// freedom removed that is still there, and would call a fine sketch over-constrained.
		sketch.AddConstraint( SketchConstraintKind.Horizontal, p0, p1 );

		var twice = sketch.Solve();

		Report.Check( "saying the same thing twice does not remove a second freedom",
			twice.DegreesOfFreedom == 3, $"got {twice.DegreesOfFreedom}" );

		Report.Check( "and the repeat is reported as redundant", twice.RedundantConstraints == 1,
			$"got {twice.RedundantConstraints}" );

		Report.Check( "a redundant but consistent sketch still solves", twice.Converged );

		// Fully dimensioned: the rectangle from the solve tests bottoms out at 1, the rotation the
		// pin leaves behind. Documented on SolveResult, and asserted here so it stays true.
		var rect = new Sketch();
		var r0 = rect.AddPoint( 0f, 0f );
		var r1 = rect.AddPoint( 3.6f, 0.35f );
		var r2 = rect.AddPoint( 4.3f, 1.7f );
		var r3 = rect.AddPoint( -0.2f, 2.4f );
		var rb = rect.Add( new SketchLine( r0, r1 ) );
		var rr = rect.Add( new SketchLine( r1, r2 ) );
		var rt = rect.Add( new SketchLine( r2, r3 ) );
		var rl = rect.Add( new SketchLine( r3, r0 ) );

		rect.AddConstraint( rb, SketchConstraintKind.Horizontal );
		rect.AddConstraint( rt, SketchConstraintKind.Horizontal );
		rect.AddConstraint( rl, SketchConstraintKind.Vertical );
		rect.AddConstraint( rr, SketchConstraintKind.Vertical );
		rect.AddConstraint( SketchConstraintKind.Distance, r0, r1, 4f );
		rect.AddConstraint( SketchConstraintKind.Distance, r1, r2, 2f );

		var solved = rect.Solve();

		Report.Check( "a fully dimensioned rectangle has no freedom left but its orientation",
			solved.DegreesOfFreedom == 0, $"got {solved.DegreesOfFreedom}" );

		// Contradiction: 4 across and also 5 across. It cannot converge, and the important part is
		// that it says so rather than reporting success at a compromise.
		var impossible = new Sketch();
		var i0 = impossible.AddPoint( 0f, 0f );
		var i1 = impossible.AddPoint( 4f, 0f );
		impossible.AddConstraint( SketchConstraintKind.Distance, i0, i1, 4f );
		impossible.AddConstraint( SketchConstraintKind.Distance, i0, i1, 5f );
		impossible.AddConstraint( SketchConstraintKind.Horizontal, i0, i1 );

		var contradiction = impossible.Solve();

		Report.Check( "a contradiction is reported as not converged", !contradiction.Converged,
			$"residual {contradiction.Residual:0.###}" );

		Report.Check( "and the geometry is left somewhere between the two demands",
			(impossible.Points[i1] - impossible.Points[i0]).Length is > 4f and < 5f,
			$"length {(impossible.Points[i1] - impossible.Points[i0]).Length:0.####}" );
	}

	// --- the cases that must not break anything -----------------------------------------------

	static void TestDegenerate()
	{
		// The path every sketch drawn before the solver existed goes down.
		var plain = new Sketch();
		plain.AddRectangle( new Vec2( 0, 0 ), new Vec2( 4, 2 ) );
		var before = plain.Points.ToList();

		var result = plain.Solve();

		Report.Check( "a sketch with no constraints is a converged no-op",
			result.Converged && result.Iterations == 0 );

		Report.Check( "and not one point moved",
			plain.Points.Select( ( p, i ) => (p - before[i]).Length ).All( d => d == 0f ) );

		// A constraint left behind by a deleted curve. Dropping it beats failing the whole solve:
		// one deleted line would otherwise wedge every other constraint in the sketch.
		var stale = new Sketch();
		var s0 = stale.AddPoint( 0f, 0f );
		var s1 = stale.AddPoint( 2f, 0.6f );
		stale.Add( new SketchLine( s0, s1 ) );
		stale.Constraints.Add( new SketchConstraint( SketchConstraintKind.Horizontal, "curve-that-is-gone" ) );
		stale.AddConstraint( SketchConstraintKind.Horizontal, s0, s1 );

		var staleResult = stale.Solve();

		Report.Check( "a constraint whose curve was deleted is dropped, not fatal",
			staleResult.Converged && MathF.Abs( stale.Points[s1].y ) < 1e-3f );

		// Same for point indices that no longer exist, which is what deleting a point leaves.
		var dangling = new Sketch();
		var d0 = dangling.AddPoint( 0f, 0f );
		var d1 = dangling.AddPoint( 1f, 1f );
		dangling.AddConstraint( SketchConstraintKind.Distance, d0, 99, 3f );
		dangling.AddConstraint( SketchConstraintKind.Horizontal, d0, d1 );

		Report.Check( "a constraint pointing at a deleted point is dropped too",
			dangling.Solve().Converged && MathF.Abs( dangling.Points[d1].y ) < 1e-3f );

		// Coincident points with a distance between them: the derivative is genuinely undefined
		// there, and the guard exists so it produces a step instead of a NaN.
		var stacked = new Sketch();
		var t0 = stacked.AddPoint( 1f, 1f );
		var t1 = stacked.AddPoint( 1f, 1f );
		stacked.AddConstraint( SketchConstraintKind.Distance, t0, t1, 2f );

		var stackedResult = stacked.Solve();
		var separated = (stacked.Points[t1] - stacked.Points[t0]).Length;

		Report.Check( "two points on top of each other can still be pushed apart",
			!float.IsNaN( separated ) && MathF.Abs( separated - 2f ) < 1e-3f,
			$"got {separated}" );

		Report.Check( "and no coordinate came out NaN",
			stacked.Points.All( p => !float.IsNaN( p.x ) && !float.IsNaN( p.y ) ),
			stackedResult.Residual.ToString( "0.###" ) );

		// Any point can be the pin, which is what lets the editor solve around the point being
		// dragged instead of around point 0.
		var pinned = new Sketch();
		var f0 = pinned.AddPoint( 0f, 0f );
		var f1 = pinned.AddPoint( 3f, 0.8f );
		pinned.AddConstraint( SketchConstraintKind.Horizontal, f0, f1 );

		SketchSolver.Solve( pinned, pinnedPoint: f1 );

		Report.Check( "pinning a different point holds THAT one still",
			Near( pinned.Points[f1], 3f, 0.8f ) && MathF.Abs( pinned.Points[f0].y - 0.8f ) < 1e-3f,
			$"{pinned.Points[f0]} / {pinned.Points[f1]}" );

		// Clone has to carry the point indices. It rebuilt constraints from (Kind, CurveId) alone,
		// which silently emptied every point-based constraint — and undo is built on Clone.
		var original = new Sketch();
		var o0 = original.AddPoint( 0f, 0f );
		var o1 = original.AddPoint( 2f, 1f );
		original.AddConstraint( SketchConstraintKind.Distance, o0, o1, 5f );

		var copy = original.Clone();
		var copied = copy.Constraints[0];

		Report.Check( "cloning a sketch carries its constraints whole",
			copied.PointA == o0 && copied.PointB == o1 && copied.Value == 5f,
			$"A={copied.PointA} B={copied.PointB} value={copied.Value}" );

		Report.Check( "so a cloned sketch solves to the same answer",
			copy.Solve().Converged && MathF.Abs( (copy.Points[o1] - copy.Points[o0]).Length - 5f ) < 1e-3f );
	}

	// --- inside the feature tree ---------------------------------------------------------------

	static void TestInFeatureTree()
	{
		// The solve has to happen during the rebuild, before anything reads the sketch. This is the
		// whole integration: draw a rough rectangle, constrain it to 4 x 2, extrude 1, and the
		// solid must enclose exactly 8 — which it only can if the points were moved before the
		// profile was found.
		var studio = new PartStudio();
		var sketchFeature = studio.Add( new SketchFeature() );
		var sketch = sketchFeature.Sketch;

		var p0 = sketch.AddPoint( 0f, 0f );
		var p1 = sketch.AddPoint( 3.7f, 0.2f );
		var p2 = sketch.AddPoint( 4.1f, 1.8f );
		var p3 = sketch.AddPoint( -0.3f, 2.2f );

		var bottom = sketch.Add( new SketchLine( p0, p1 ) );
		var right = sketch.Add( new SketchLine( p1, p2 ) );
		var top = sketch.Add( new SketchLine( p2, p3 ) );
		var left = sketch.Add( new SketchLine( p3, p0 ) );

		sketch.AddConstraint( bottom, SketchConstraintKind.Horizontal );
		sketch.AddConstraint( top, SketchConstraintKind.Horizontal );
		sketch.AddConstraint( left, SketchConstraintKind.Vertical );
		sketch.AddConstraint( right, SketchConstraintKind.Vertical );
		sketch.AddConstraint( SketchConstraintKind.Distance, p0, p1, 4f );
		sketch.AddConstraint( SketchConstraintKind.Distance, p1, p2, 2f );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;

		var report = studio.Rebuild();

		Report.Check( "a constrained sketch rebuilds without error", !report.HasErrors, report.ToString() );

		var volume = EnclosedVolume( studio.ToMesh() );

		Report.Check( "the extrude measures 4 x 2 x 1 because the solve ran first",
			MathF.Abs( volume - 8f ) < 1e-2f, $"enclosed volume {volume:0.####}" );

		// Editing a dimension has to flow through: change the 4 to a 6 and the solid follows.
		sketch.Constraints.First( c => c.Kind == SketchConstraintKind.Distance && c.Value == 4f ).Value = 6f;
		studio.MarkDirty( 0 );
		studio.Rebuild();

		var widened = EnclosedVolume( studio.ToMesh() );

		Report.Check( "editing a dimension rebuilds the solid to match",
			MathF.Abs( widened - 12f ) < 1e-2f, $"enclosed volume {widened:0.####}" );

		// A sketch that cannot solve must warn and still produce geometry. Blanking the model
		// mid-edit, every time a sketch passes through an over-constrained state, would be worse
		// than carrying on with the closest fit.
		var broken = new PartStudio();
		var brokenSketch = broken.Add( new SketchFeature() );
		var b0 = brokenSketch.Sketch.AddPoint( 0f, 0f );
		var b1 = brokenSketch.Sketch.AddPoint( 4f, 0f );
		var b2 = brokenSketch.Sketch.AddPoint( 4f, 2f );
		var b3 = brokenSketch.Sketch.AddPoint( 0f, 2f );
		brokenSketch.Sketch.Add( new SketchLine( b0, b1 ) );
		brokenSketch.Sketch.Add( new SketchLine( b1, b2 ) );
		brokenSketch.Sketch.Add( new SketchLine( b2, b3 ) );
		brokenSketch.Sketch.Add( new SketchLine( b3, b0 ) );
		brokenSketch.Sketch.AddConstraint( SketchConstraintKind.Distance, b0, b1, 4f );
		brokenSketch.Sketch.AddConstraint( SketchConstraintKind.Distance, b0, b1, 7f );

		broken.Add( new ExtrudeFeature() ).Distance.Value = 1f;

		var brokenReport = broken.Rebuild();

		Report.Check( "an unsolvable sketch warns rather than failing the feature",
			!brokenReport.HasErrors && !string.IsNullOrEmpty( brokenSketch.Warning ),
			brokenSketch.Warning ?? "no warning" );

		Report.Check( "and the model still builds", broken.Bodies.Count == 1,
			$"{broken.Bodies.Count} bodies" );
	}

	// --- helpers ------------------------------------------------------------------------------

	static bool Near( Vec2 p, float x, float y ) =>
		MathF.Abs( p.x - x ) < 1e-3f && MathF.Abs( p.y - y ) < 1e-3f;

	static float EnclosedVolume( PolyMesh mesh )
	{
		var acc = 0f;

		foreach ( var f in mesh.Faces )
			acc += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		return acc / 3f;
	}

	/// <summary>
	/// The rules added after the first solver landed: tangency, midpoint, fix, concentric and
	/// diameter.
	///
	/// Same two-part discipline as everything above — the derivatives are checked against finite
	/// differences in TestJacobians, and these are the known-answer solves. Tangency gets the most
	/// attention because it is the only rule here whose residual is not linear in the coordinates,
	/// and because "nearly tangent" is a thing a wrong residual can converge to while looking fine.
	/// </summary>
	static void TestNewRules()
	{
		// TANGENT, LINE TO CIRCLE. A horizontal line three units above a circle of radius two,
		// told to touch it. The radius is held by its own dimension, so the only way to satisfy
		// the tangency is to bring the line down to y = 2.
		var tangent = new Sketch();
		var centre = tangent.AddPoint( 0f, 0f );
		var rim = tangent.AddPoint( 2f, 0f );
		var top = tangent.AddPoint( 0f, 2f );
		var left = tangent.AddPoint( -5f, 3f );
		var right = tangent.AddPoint( 5f, 3f );

		var edge = tangent.Add( new SketchLine( left, right ) );
		tangent.Add( new SketchArc( centre, rim, top ) );

		tangent.Constraints.Add( new SketchConstraint( SketchConstraintKind.Radius, centre, rim, 2f ) );
		tangent.AddConstraint( edge, SketchConstraintKind.Horizontal );
		tangent.Constraints.Add( new SketchConstraint( SketchConstraintKind.Tangent, left, right, centre, rim ) );

		var tangentResult = tangent.Solve();

		Report.Check( "a line-to-circle tangency solves", tangentResult.Converged,
			$"residual {tangentResult.Residual:0.###e0}" );

		var gap = DistanceToLine( tangent.Points[centre], tangent.Points[left], tangent.Points[right] );
		var radius = (tangent.Points[rim] - tangent.Points[centre]).Length;

		Report.Check( "and the line ends up exactly a radius from the centre",
			MathF.Abs( gap - radius ) < 1e-3f, $"gap {gap:0.#####} against radius {radius:0.#####}" );

		Report.Check( "without the dimension being sacrificed to get there",
			MathF.Abs( radius - 2f ) < 1e-3f, $"radius {radius:0.#####}" );

		// TANGENT, CIRCLE TO CIRCLE, TOUCHING OUTSIDE. Both radii are dimensioned, so the centres
		// have to end up exactly their sum apart.
		var pair = new Sketch();
		var centreA = pair.AddPoint( 0f, 0f );
		var rimA = pair.AddPoint( 1f, 0f );
		var centreB = pair.AddPoint( 5f, 0f );
		var rimB = pair.AddPoint( 5.5f, 0f );

		pair.Constraints.Add( new SketchConstraint( SketchConstraintKind.Radius, centreA, rimA, 1f ) );
		pair.Constraints.Add( new SketchConstraint( SketchConstraintKind.Radius, centreB, rimB, 0.5f ) );
		pair.Constraints.Add( new SketchConstraint( SketchConstraintKind.TangentArcs, centreA, rimA, centreB, rimB ) );

		var pairResult = pair.Solve();
		var centres = (pair.Points[centreB] - pair.Points[centreA]).Length;

		Report.Check( "two circles tangent on the outside solve", pairResult.Converged,
			$"residual {pairResult.Residual:0.###e0}" );

		Report.Check( "to centres exactly the sum of the radii apart",
			MathF.Abs( centres - 1.5f ) < 1e-3f, $"centres {centres:0.#####}, wanted 1.5" );

		// TANGENT, ONE CIRCLE INSIDE THE OTHER. Same two circles, told to nestle instead: the
		// centres now belong at the DIFFERENCE of the radii, which is the case a residual that
		// quietly assumed "sum" would get wrong while still converging.
		var nested = new Sketch();
		var innerCentre = nested.AddPoint( 0f, 0f );
		var innerRim = nested.AddPoint( 1f, 0f );
		var outerCentre = nested.AddPoint( 0.2f, 0f );
		var outerRim = nested.AddPoint( 3.2f, 0f );

		nested.Constraints.Add( new SketchConstraint( SketchConstraintKind.Radius, innerCentre, innerRim, 1f ) );
		nested.Constraints.Add( new SketchConstraint( SketchConstraintKind.Radius, outerCentre, outerRim, 3f ) );
		nested.Constraints.Add( new SketchConstraint( SketchConstraintKind.TangentArcs,
			innerCentre, innerRim, outerCentre, outerRim ) { Value = 1f } );

		var nestedResult = nested.Solve();
		var nestedCentres = (nested.Points[outerCentre] - nested.Points[innerCentre]).Length;

		Report.Check( "a circle tangent inside another solves", nestedResult.Converged,
			$"residual {nestedResult.Residual:0.###e0}" );

		Report.Check( "to centres the DIFFERENCE of the radii apart, not the sum",
			MathF.Abs( nestedCentres - 2f ) < 1e-3f, $"centres {nestedCentres:0.#####}, wanted 2" );

		// MIDPOINT AND FIX TOGETHER. Two fixed ends and a point told to sit between them has
		// exactly one answer, so this checks both rules at once and neither can hide.
		var middle = new Sketch();
		middle.AddPoint( 9f, 9f );                      // the solver's pin, deliberately elsewhere
		var endA = middle.AddPoint( -3f, 4f );
		var endB = middle.AddPoint( 1f, -1f );
		var centrePoint = middle.AddPoint( 0f, 0f );

		middle.Constraints.Add( new SketchConstraint( SketchConstraintKind.Fixed, endA, -1 ) { Value = 0f, ValueY = 0f } );
		middle.Constraints.Add( new SketchConstraint( SketchConstraintKind.Fixed, endB, -1 ) { Value = 4f, ValueY = 2f } );
		middle.Constraints.Add( new SketchConstraint( SketchConstraintKind.Midpoint, centrePoint, endA, endB ) );

		var middleResult = middle.Solve();

		Report.Check( "fix and midpoint solve together", middleResult.Converged,
			$"residual {middleResult.Residual:0.###e0}" );

		Report.Check( "the fixed ends land on their absolute coordinates",
			(middle.Points[endA] - new Vec2( 0f, 0f )).Length < 1e-3f &&
			(middle.Points[endB] - new Vec2( 4f, 2f )).Length < 1e-3f,
			$"{middle.Points[endA]} and {middle.Points[endB]}" );

		Report.Check( "and the midpoint lands exactly between them",
			(middle.Points[centrePoint] - new Vec2( 2f, 1f )).Length < 1e-3f,
			$"{middle.Points[centrePoint]}, wanted (2, 1)" );

		// CONCENTRIC. Stored as its own kind, solved as a coincidence of the two centres — so what
		// this really checks is that the kind is wired to an evaluator at all.
		var shared = new Sketch();
		shared.AddPoint( 9f, 9f );
		var hubA = shared.AddPoint( 0f, 0f );
		var hubB = shared.AddPoint( 3f, 1.5f );

		shared.Constraints.Add( new SketchConstraint( SketchConstraintKind.Concentric, hubA, hubB ) );

		var sharedResult = shared.Solve();

		Report.Check( "concentric solves", sharedResult.Converged, $"residual {sharedResult.Residual:0.###e0}" );

		Report.Check( "to two centres in the same place",
			(shared.Points[hubA] - shared.Points[hubB]).Length < 1e-3f,
			$"{shared.Points[hubA]} against {shared.Points[hubB]}" );

		// DIAMETER IS RADIUS AT HALF THE NUMBER. The one thing that could go wrong here is the
		// factor going the wrong way, which doubles every hole in a part rather than failing.
		var across = new Sketch();
		var hub = across.AddPoint( 0f, 0f );
		var edgePoint = across.AddPoint( 1f, 0f );

		across.Constraints.Add( new SketchConstraint( SketchConstraintKind.Diameter, hub, edgePoint, 6f ) );
		across.Solve();

		var acrossRadius = (across.Points[edgePoint] - across.Points[hub]).Length;

		Report.Check( "a diameter of 6 gives a radius of 3, not 6 or 12",
			MathF.Abs( acrossRadius - 3f ) < 1e-3f, $"radius {acrossRadius:0.#####}" );

		TestFixRoundTrips();
	}

	/// <summary>
	/// A fix is the only rule whose value is a position rather than a magnitude, so it is the only
	/// one that needed a second number in the file. That second number was appended AFTER the
	/// CurveId rather than next to the first, so older documents still read — this checks the new
	/// half survives a write and a read, and that a line written without it still loads.
	/// </summary>
	static void TestFixRoundTrips()
	{
		var studio = new PartStudio();
		var feature = studio.Add( new SketchFeature() );

		feature.Sketch.AddPoint( 0f, 0f );
		var pinned = feature.Sketch.AddPoint( 1f, 1f );

		feature.Sketch.Constraints.Add(
			new SketchConstraint( SketchConstraintKind.Fixed, pinned, -1 ) { Value = 1.5f, ValueY = -2.5f } );

		var back = StudioDocument.Read( StudioDocument.Write( studio ) );
		var reloaded = ((SketchFeature)back.Features[0]).Sketch.Constraints[0];

		Report.Check( "a fix comes back as a fix", reloaded.Kind == SketchConstraintKind.Fixed,
			reloaded.Kind.ToString() );

		Report.Check( "with both halves of its coordinate intact",
			reloaded.Value == 1.5f && reloaded.ValueY == -2.5f,
			$"({reloaded.Value}, {reloaded.ValueY}), wanted (1.5, -2.5)" );

		// A constraint line from before ValueY existed has seven fields, not eight.
		var older = StudioDocument.Read( StudioDocument.Write( studio )
			.Replace( " 1.5 - -2.5", " 1.5 -" ) );

		Report.Check( "and a document written before ValueY existed still loads",
			((SketchFeature)older.Features[0]).Sketch.Constraints[0].ValueY == 0f );
	}

	/// <summary>Perpendicular distance from a point to the infinite line through two others.</summary>
	static float DistanceToLine( Vec2 p, Vec2 a, Vec2 b )
	{
		var d = b - a;
		var length = d.Length;

		if ( length < 1e-9f )
			return (p - a).Length;

		return MathF.Abs( d.x * (p.y - a.y) - d.y * (p.x - a.x) ) / length;
	}

}
