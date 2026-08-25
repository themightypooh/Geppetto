using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>What a solve did, and what it found out about the sketch on the way.</summary>
public sealed class SolveResult
{
	/// <summary>Every constraint is satisfied to tolerance.</summary>
	public bool Converged;

	public int Iterations;

	/// <summary>Norm of the residual vector at the final state. Zero is a satisfied sketch.</summary>
	public double Residual;

	/// <summary>
	/// Degrees of freedom left: how many independent ways the sketch can still be moved without
	/// breaking a constraint. Onshape's "under defined" is this being greater than zero.
	///
	/// Counted as free variables minus the RANK of the Jacobian, not minus the number of
	/// constraints — the whole point is that two constraints saying the same thing only remove one
	/// freedom, and counting rows would claim otherwise.
	///
	/// Read it knowing what the pin leaves behind: pinning one point kills translation but not
	/// rotation, so a rectangle with all four sides dimensioned still reports 1 until something
	/// (a horizontal constraint, usually) fixes its orientation.
	/// </summary>
	public int DegreesOfFreedom;

	/// <summary>Constraint rows beyond the rank — rules that repeat something already implied.
	/// Harmless when consistent, and the reason a solve can be redundant and still converge; the
	/// diagnosis a user wants when adding one more dimension does nothing.</summary>
	public int RedundantConstraints;
}

/// <summary>
/// The sketch constraint solver: Levenberg-Marquardt over the constraint residuals.
///
/// The shape of the problem. Every point is two unknowns, every constraint is one or more equations
/// that should read zero, and the answer is the point positions that make them all zero. That is a
/// nonlinear least squares problem, and LM is the standard way to take it: a Gauss-Newton step
/// where it behaves, damped toward gradient descent where it does not, with the damping (λ) raised
/// on a step that made things worse and lowered on one that helped.
///
/// WHY A POINT IS PINNED. The equations only ever mention differences between points, so the whole
/// sketch can slide anywhere without changing a single residual. JᵀJ is singular in that direction
/// and the step is not unique. Pinning one point removes the slide and leaves the rest free. It
/// does not remove rotation, which is why SolveResult.DegreesOfFreedom bottoms out at 1 for an
/// otherwise fully dimensioned sketch — the honest answer, since such a sketch really can be spun.
/// The editor should pin whichever point the user is dragging, so the shape resolves around their
/// hand rather than around point 0.
///
/// WHY IT SOLVES IN DOUBLE AND STORES IN FLOAT. Sketch points are float, and JᵀJ squares the
/// condition number of J — a right angle between near-parallel lines loses far more digits than
/// float has to give. The solve runs in double and the answer is narrowed on the way out, which is
/// why the convergence floor here is 1e-4 rather than the tolerance: past that, the residual is
/// measuring the storage type, not the sketch.
/// </summary>
public static class SketchSolver
{
	const double Tolerance = 1e-6;
	const int MaxIterations = 40;
	const double LambdaInit = 1e-3;
	const double LambdaMax = 1e12;

	/// <summary>The residual below which a non-converged solve is still called solved. Float
	/// coordinates cannot express better, so demanding Tolerance of them would report failure on a
	/// sketch that is as correct as its storage allows.</summary>
	const double FloatFloor = 1e-4;

	/// <summary>
	/// Move the sketch's points to satisfy its constraints, in place.
	///
	/// A sketch with no constraints is a no-op and reports converged — every sketch drawn before the
	/// solver existed goes down that path, which is what makes this safe to call unconditionally
	/// from the rebuild.
	/// </summary>
	/// <param name="pinnedPoint">The point held fixed to give the sketch an absolute frame. Pass
	/// the point being dragged, when there is one.</param>
	public static SolveResult Solve( Sketch sketch, int pinnedPoint = 0 )
	{
		var result = new SolveResult { Converged = true };

		if ( sketch is null || sketch.Points.Count == 0 )
			return result;

		var constraints = new List<IConstraint>( sketch.Constraints.Count );

		foreach ( var stored in sketch.Constraints )
		{
			if ( stored.Build( sketch ) is { } c )
				constraints.Add( c );
		}

		// IMPLICIT, AND NOT OPTIONAL. An arc is a centre and two endpoints, and its radius is read
		// off the centre-to-START distance — Tessellate then snaps its last sample onto End wherever
		// End happens to be. Nothing has ever required the two endpoints to be the same distance from
		// the centre, and while coordinates were only ever typed, nothing moved them apart.
		//
		// A solver moves points. Constrain anything touching one end of an arc and the other end
		// drifts off its own circle, and what comes back is not a bad arc that complains — it is an
		// arc drawn at the wrong radius with a kink in the last segment, which looks like a rendering
		// glitch and is nothing of the kind.
		//
		// So every arc contributes "both endpoints are equidistant from my centre" whether the user
		// asked for it or not. It is not design intent, it is what an arc IS, and a user who never
		// adds a constraint never pays for it because the whole solve is skipped below.
		foreach ( var curve in sketch.Curves.OfType<SketchArc>() )
		{
			if ( curve.Center != curve.Start && curve.Center != curve.End )
				constraints.Add( new EqualLengthConstraint( curve.Center, curve.Start, curve.Center, curve.End ) );
		}

		// Only the STORED constraints decide whether there is anything to do. A sketch with arcs and
		// no constraints is one nobody has asked anything of, and solving it would move points that
		// were placed deliberately.
		if ( sketch.Constraints.Count == 0 || constraints.Count == 0 )
			return result;

		var nPts = sketch.Points.Count;

		// Column map: each point's slot among the free variables, or -1 for the pinned one. Doing it
		// as a map rather than "index minus one" is what lets any point be the pin.
		var column = new int[nPts];
		var free = 0;

		for ( var i = 0; i < nPts; i++ )
			column[i] = i == pinnedPoint ? -1 : free++;

		var n = free * 2;

		if ( n == 0 )
			return result;

		var points = new Vec2[nPts];

		for ( var i = 0; i < nPts; i++ )
			points[i] = sketch.Points[i];

		var rows = 0;
		var widest = 1;

		foreach ( var c in constraints )
		{
			rows += c.ResidualCount;
			widest = Math.Max( widest, c.ResidualCount );
		}

		var residual = new double[rows];
		var rowBuf = new ConstraintResult[widest];

		var J = new double[rows * n];
		var g = new double[n];
		var H = new double[n * n];
		var dx = new double[n];

		var lambda = LambdaInit;

		for ( var iter = 0; iter < MaxIterations; iter++ )
		{
			result.Iterations = iter + 1;

			Array.Clear( residual, 0, residual.Length );
			Array.Clear( J, 0, J.Length );

			var row = 0;

			foreach ( var c in constraints )
			{
				var needed = c.ResidualCount;
				c.Evaluate( points, rowBuf.AsSpan( 0, needed ) );

				for ( var r = 0; r < needed; r++ )
				{
					residual[row] = rowBuf[r].Residual;

					foreach ( var (point, gx, gy) in rowBuf[r].Jacobian )
					{
						if ( point < 0 || point >= nPts || column[point] < 0 )
							continue;

						var col = column[point] * 2;
						J[row * n + col] += gx;
						J[row * n + col + 1] += gy;
					}

					row++;
				}
			}

			var residualSq = 0.0;

			for ( var i = 0; i < rows; i++ )
				residualSq += residual[i] * residual[i];

			result.Residual = Math.Sqrt( residualSq );

			if ( result.Residual < Tolerance )
			{
				Finish( sketch, points, J, rows, n, result, converged: true );
				return result;
			}

			// g = Jᵀr
			Array.Clear( g, 0, n );

			for ( var i = 0; i < rows; i++ )
			{
				var ri = residual[i];

				for ( var j = 0; j < n; j++ )
					g[j] += J[i * n + j] * ri;
			}

			// H = JᵀJ + λI. Cholesky needs it positive definite, and the λ on the diagonal is
			// exactly what guarantees that however rank-deficient JᵀJ is.
			Array.Clear( H, 0, H.Length );

			for ( var i = 0; i < rows; i++ )
			{
				for ( var j = 0; j < n; j++ )
				{
					var jij = J[i * n + j];

					if ( jij == 0 )
						continue;

					for ( var k = 0; k < n; k++ )
						H[j * n + k] += jij * J[i * n + k];
				}
			}

			for ( var j = 0; j < n; j++ )
				H[j * n + j] += lambda;

			// CholeskySolve overwrites H with its factorization, so J is the only thing left holding
			// the Jacobian by the time the analysis wants it. That is why the analysis reads J.
			if ( !CholeskySolve( H, n, g, dx ) )
			{
				lambda = Math.Min( lambda * 10, LambdaMax );

				if ( lambda >= LambdaMax )
				{
					Finish( sketch, points, J, rows, n, result, result.Residual < FloatFloor );
					return result;
				}

				continue;
			}

			var saved = (Vec2[])points.Clone();

			for ( var i = 0; i < nPts; i++ )
			{
				if ( column[i] < 0 )
					continue;

				var col = column[i] * 2;
				points[i] = new Vec2( (float)(points[i].x - dx[col]), (float)(points[i].y - dx[col + 1]) );
			}

			// Did the step help? Measured against THIS iteration's residual, not the last accepted
			// one. Those differ only on the first pass — where the last-accepted value is infinity
			// and every step, including a disastrous one, would be taken.
			var steppedSq = 0.0;

			foreach ( var c in constraints )
			{
				var needed = c.ResidualCount;
				c.Evaluate( points, rowBuf.AsSpan( 0, needed ) );

				for ( var r = 0; r < needed; r++ )
					steppedSq += rowBuf[r].Residual * rowBuf[r].Residual;
			}

			if ( steppedSq < residualSq )
			{
				lambda = Math.Max( lambda * 0.25, 1e-12 );
				continue;
			}

			points = saved;
			lambda = Math.Min( lambda * 4, LambdaMax );

			if ( lambda >= LambdaMax )
			{
				Finish( sketch, points, J, rows, n, result, result.Residual < FloatFloor );
				return result;
			}
		}

		Finish( sketch, points, J, rows, n, result, result.Residual < FloatFloor );
		return result;
	}

	/// <summary>Write the solved positions back and fill in the diagnosis.</summary>
	static void Finish( Sketch sketch, Vec2[] points, double[] J, int rows, int n, SolveResult result, bool converged )
	{
		for ( var i = 0; i < points.Length; i++ )
			sketch.Points[i] = points[i];

		result.Converged = converged;

		var rank = Rank( J, rows, n );
		result.DegreesOfFreedom = n - rank;
		result.RedundantConstraints = rows - rank;
	}

	/// <summary>
	/// Rank of the Jacobian, by Gaussian elimination with partial pivoting on a copy.
	///
	/// This is what separates "under defined by two" from "you added four constraints that between
	/// them say three things". Counting constraint rows cannot tell those apart; counting pivots
	/// can. The threshold is relative to the largest entry, because J's entries carry the scale of
	/// the sketch and an absolute epsilon would call a large sketch full-rank and a small one
	/// singular.
	/// </summary>
	static int Rank( double[] J, int rows, int n )
	{
		if ( rows == 0 || n == 0 )
			return 0;

		var m = (double[])J.Clone();
		var largest = 0.0;

		foreach ( var v in m )
			largest = Math.Max( largest, Math.Abs( v ) );

		if ( largest == 0.0 )
			return 0;

		var epsilon = largest * 1e-9;
		var rank = 0;

		for ( var col = 0; col < n && rank < rows; col++ )
		{
			var pivot = -1;
			var best = epsilon;

			for ( var r = rank; r < rows; r++ )
			{
				var v = Math.Abs( m[r * n + col] );

				if ( v > best )
				{
					best = v;
					pivot = r;
				}
			}

			if ( pivot < 0 )
				continue;

			if ( pivot != rank )
			{
				for ( var c = 0; c < n; c++ )
					(m[rank * n + c], m[pivot * n + c]) = (m[pivot * n + c], m[rank * n + c]);
			}

			var inv = 1.0 / m[rank * n + col];

			for ( var r = rank + 1; r < rows; r++ )
			{
				var factor = m[r * n + col] * inv;

				if ( factor == 0 )
					continue;

				for ( var c = col; c < n; c++ )
					m[r * n + c] -= factor * m[rank * n + c];
			}

			rank++;
		}

		return rank;
	}

	/// <summary>
	/// In-place Cholesky factorization of symmetric positive-definite H (n×n, row-major), then
	/// forward and back substitution to solve H x = b.
	///
	/// False when a pivot comes out non-positive, which means H is not positive definite after all —
	/// λ is still too small for how singular JᵀJ is. The caller's answer to that is to raise λ and
	/// try again, which is LM working as intended rather than an error.
	/// </summary>
	static bool CholeskySolve( double[] H, int n, double[] b, double[] x )
	{
		for ( var i = 0; i < n; i++ )
		{
			for ( var j = 0; j <= i; j++ )
			{
				var sum = H[i * n + j];

				for ( var k = 0; k < j; k++ )
					sum -= H[i * n + k] * H[j * n + k];

				if ( i == j )
				{
					if ( sum <= 1e-18 )
						return false;

					H[i * n + j] = Math.Sqrt( sum );
				}
				else
				{
					H[i * n + j] = sum / H[j * n + j];
				}
			}
		}

		// L y = b
		for ( var i = 0; i < n; i++ )
		{
			var sum = b[i];

			for ( var k = 0; k < i; k++ )
				sum -= H[i * n + k] * x[k];

			x[i] = sum / H[i * n + i];
		}

		// Lᵀ x = y
		for ( var i = n - 1; i >= 0; i-- )
		{
			var sum = x[i];

			for ( var k = i + 1; k < n; k++ )
				sum -= H[k * n + i] * x[k];

			x[i] = sum / H[i * n + i];
		}

		return true;
	}
}
