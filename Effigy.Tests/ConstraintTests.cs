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
	}

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
			("perpendicular", new PerpendicularConstraint( 0, 1, 3, 4 ))
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
}
