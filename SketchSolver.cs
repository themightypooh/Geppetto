using System;
using System.Collections.Generic;

namespace Effigy;

public sealed class SolveResult
{
	public bool Converged;
	public int Iterations;
	public double Residual;
}

/// <summary>
/// Levenberg-Marquardt / damped Gauss-Newton sketch constraint solver.
///
/// Point 0 is pinned (fixed) so the system has an absolute frame. All other points are free.
/// Constraints are evaluated through <see cref="IConstraint"/>; the solver never switches on
/// constraint kinds.
///
/// Storage type is <see cref="SketchConstraint"/>; each call builds the runtime evaluators once
/// via <see cref="SketchConstraint.Build"/>.
/// </summary>
public static class SketchSolver
{
	const double Tolerance = 1e-6;       // float coords → ~1e-6 is the useful floor
	const int MaxIterations = 40;
	const double LambdaInit = 1e-3;
	const double LambdaMax = 1e12;

	public static SolveResult Solve( Sketch sketch )
	{
		var result = new SolveResult();
		if ( sketch.Points.Count == 0 )
		{
			result.Converged = true;
			return result;
		}

		var constraints = new List<IConstraint>( sketch.Constraints.Count );
		foreach ( var sc in sketch.Constraints )
		{
			var c = sc.Build( sketch );
			if ( c is not null )
				constraints.Add( c );
		}

		if ( constraints.Count == 0 )
		{
			result.Converged = true;
			return result;
		}

		var nPts = sketch.Points.Count;
		// Free variables: every point except index 0, two DOF each.
		var freeCount = Math.Max( 0, nPts - 1 );
		var n = freeCount * 2;
		if ( n == 0 )
		{
			result.Converged = true;
			return result;
		}

		var points = new Vec2[nPts];
		for ( var i = 0; i < nPts; i++ )
			points[i] = sketch.Points[i];

		var totalResiduals = 0;
		foreach ( var c in constraints )
			totalResiduals += c.ResidualCount;

		var residual = new double[totalResiduals];
		var rowBuf = new ConstraintResult[8]; // max residual rows any single constraint needs

		// Dense J: totalResiduals × n. Built each iteration.
		var J = new double[totalResiduals * n];
		var g = new double[n];
		var H = new double[n * n];
		var dx = new double[n];

		double lambda = LambdaInit;
		double currentResidualSq = double.PositiveInfinity;

		for ( var iter = 0; iter < MaxIterations; iter++ )
		{
			result.Iterations = iter + 1;

			// Evaluate residuals + Jacobian at current points.
			Array.Clear( residual, 0, residual.Length );
			Array.Clear( J, 0, J.Length );
			var rowIndex = 0;
			foreach ( var c in constraints )
			{
				var needed = c.ResidualCount;
				c.Evaluate( points, rowBuf.AsSpan( 0, needed ) );
				for ( var r = 0; r < needed; r++ )
				{
					var cr = rowBuf[r];
					residual[rowIndex] = cr.Residual;
					foreach ( var (pt, gx, gy) in cr.Jacobian )
					{
						if ( pt <= 0 || pt >= nPts ) continue; // pinned or invalid
						var col = (pt - 1) * 2;
						J[rowIndex * n + col]     += gx;
						J[rowIndex * n + col + 1] += gy;
					}
					rowIndex++;
				}
			}

			double residualSq = 0;
			for ( var i = 0; i < totalResiduals; i++ )
				residualSq += residual[i] * residual[i];
			result.Residual = Math.Sqrt( residualSq );

			if ( result.Residual < Tolerance )
			{
				WriteBack( sketch, points );
				result.Converged = true;
				return result;
			}

			// g = Jᵀ r
			Array.Clear( g, 0, n );
			for ( var i = 0; i < totalResiduals; i++ )
			{
				var ri = residual[i];
				for ( var j = 0; j < n; j++ )
					g[j] += J[i * n + j] * ri;
			}

			// H = Jᵀ J + λ I
			Array.Clear( H, 0, H.Length );
			for ( var i = 0; i < totalResiduals; i++ )
			{
				for ( var j = 0; j < n; j++ )
				{
					var jij = J[i * n + j];
					if ( jij == 0 ) continue;
					for ( var k = 0; k < n; k++ )
						H[j * n + k] += jij * J[i * n + k];
				}
			}
			for ( var j = 0; j < n; j++ )
				H[j * n + j] += lambda;

			// Solve H dx = −g  (Cholesky). On failure, increase λ and retry.
			if ( !CholeskySolve( H, n, g, dx ) )
			{
				lambda = Math.Min( lambda * 10, LambdaMax );
				if ( lambda >= LambdaMax )
				{
					WriteBack( sketch, points );
					result.Converged = result.Residual < 1e-4;
					return result;
				}
				continue;
			}

			// Negate: we solved H dx = g, want H dx = −g.
			for ( var j = 0; j < n; j++ )
				dx[j] = -dx[j];

			// Tentative step.
			var saved = (Vec2[])points.Clone();
			for ( var i = 0; i < freeCount; i++ )
			{
				var pt = i + 1;
				points[pt] = new Vec2(
					(float)( points[pt].x + dx[i * 2] ),
					(float)( points[pt].y + dx[i * 2 + 1] ) );
			}

			// Re-evaluate residual at the new state.
			double newResidualSq = 0;
			foreach ( var c in constraints )
			{
				var needed = c.ResidualCount;
				c.Evaluate( points, rowBuf.AsSpan( 0, needed ) );
				for ( var r = 0; r < needed; r++ )
					newResidualSq += rowBuf[r].Residual * rowBuf[r].Residual;
			}

			if ( newResidualSq < currentResidualSq )
			{
				currentResidualSq = newResidualSq;
				lambda = Math.Max( lambda * 0.25, 1e-12 );
			}
			else
			{
				points = saved;
				lambda = Math.Min( lambda * 4, LambdaMax );
				if ( lambda >= LambdaMax )
				{
					WriteBack( sketch, points );
					// Float-precision floor: residual may stall around 1e-7–1e-8 even when "solved".
					result.Converged = result.Residual < 1e-4;
					return result;
				}
			}
		}

		WriteBack( sketch, points );
		result.Converged = result.Residual < 1e-4;
		return result;
	}

	static void WriteBack( Sketch sketch, Vec2[] points )
	{
		for ( var i = 0; i < points.Length; i++ )
			sketch.Points[i] = points[i];
	}

	/// <summary>
	/// In-place Cholesky factorization of symmetric positive-definite H (n×n, row-major),
	/// then forward/back-sub to solve H x = b. Result written into x. Returns false if a pivot
	/// is non-positive (not SPD — usually means λ is still too small or the system is singular).
	/// </summary>
	static bool CholeskySolve( double[] H, int n, double[] b, double[] x )
	{
		// Factor H → L (lower triangular stored in H's lower triangle; diagonal in place).
		for ( var i = 0; i < n; i++ )
		{
			for ( var j = 0; j <= i; j++ )
			{
				double sum = H[i * n + j];
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

		// Forward: L y = b
		for ( var i = 0; i < n; i++ )
		{
			double sum = b[i];
			for ( var k = 0; k < i; k++ )
				sum -= H[i * n + k] * x[k];
			x[i] = sum / H[i * n + i];
		}

		// Back: Lᵀ x = y
		for ( var i = n - 1; i >= 0; i-- )
		{
			double sum = x[i];
			for ( var k = i + 1; k < n; k++ )
				sum -= H[k * n + i] * x[k];
			x[i] = sum / H[i * n + i];
		}

		return true;
	}
}
