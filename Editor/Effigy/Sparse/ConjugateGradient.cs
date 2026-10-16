using System;

namespace Effigy;

/// <summary>
/// Jacobi-preconditioned conjugate gradient for symmetric positive-definite systems.
///
/// THE FALLBACK, NOT THE FIRST CHOICE. <see cref="SparseLdlt"/> is exact and, once factored,
/// solves a new right-hand side in milliseconds; CG starts from scratch every time and its
/// iteration count grows with the mesh. What CG has is memory: it never forms a factor, so it
/// works on systems whose fill would not fit, and it is the second opinion the tests check the
/// direct solver against.
/// </summary>
public static class ConjugateGradient
{
	public readonly struct Result
	{
		public readonly int Iterations;
		/// <summary>‖b − A x‖ / ‖b‖ at exit.</summary>
		public readonly double RelativeResidual;
		public readonly bool Converged;

		public Result( int iterations, double residual, bool converged )
		{
			Iterations = iterations;
			RelativeResidual = residual;
			Converged = converged;
		}

		public override string ToString() => $"{(Converged ? "converged" : "stopped")} after {Iterations} iterations, residual {RelativeResidual:G3}";
	}

	/// <summary>
	/// Solve A x = b. <paramref name="x"/> is the starting guess on entry (zeros are fine; the
	/// previous frame's answer is better) and the solution on exit.
	/// </summary>
	public static Result Solve( SparseMatrix a, double[] b, double[] x, double tolerance = 1e-10, int maxIterations = 0 )
	{
		if ( a is null ) throw new ArgumentNullException( nameof( a ) );
		if ( a.Rows != a.Cols ) throw new ArgumentException( "CG needs a square matrix" );

		var n = a.Rows;

		if ( b.Length < n || x.Length < n )
			throw new ArgumentException( $"CG needs vectors of {n}" );

		if ( maxIterations <= 0 )
			maxIterations = Math.Max( 100, 4 * n );

		var inv = a.Diagonal();

		for ( var i = 0; i < n; i++ )
			inv[i] = inv[i] > 0 ? 1.0 / inv[i] : 1.0;

		var r = new double[n];
		var z = new double[n];
		var p = new double[n];
		var ap = new double[n];

		a.Multiply( x, ap );

		var bNorm = 0.0;

		for ( var i = 0; i < n; i++ )
		{
			r[i] = b[i] - ap[i];
			bNorm += b[i] * b[i];
		}

		bNorm = Math.Sqrt( bNorm );

		if ( bNorm == 0.0 )
		{
			Array.Clear( x, 0, n );
			return new Result( 0, 0, true );
		}

		var rz = 0.0;

		for ( var i = 0; i < n; i++ )
		{
			z[i] = inv[i] * r[i];
			p[i] = z[i];
			rz += r[i] * z[i];
		}

		var residual = Norm( r, n ) / bNorm;

		for ( var it = 0; it < maxIterations; it++ )
		{
			if ( residual <= tolerance )
				return new Result( it, residual, true );

			a.Multiply( p, ap );
			var pap = 0.0;

			for ( var i = 0; i < n; i++ )
				pap += p[i] * ap[i];

			// Not positive definite along p: CG's step is undefined. Stop rather than diverge.
			if ( !(pap > 0) )
				return new Result( it, residual, false );

			var alpha = rz / pap;

			for ( var i = 0; i < n; i++ )
			{
				x[i] += alpha * p[i];
				r[i] -= alpha * ap[i];
			}

			var rzNext = 0.0;

			for ( var i = 0; i < n; i++ )
			{
				z[i] = inv[i] * r[i];
				rzNext += r[i] * z[i];
			}

			var beta = rzNext / rz;
			rz = rzNext;

			for ( var i = 0; i < n; i++ )
				p[i] = z[i] + beta * p[i];

			residual = Norm( r, n ) / bNorm;
		}

		return new Result( maxIterations, residual, residual <= tolerance );
	}

	static double Norm( double[] v, int n )
	{
		var s = 0.0;
		for ( var i = 0; i < n; i++ ) s += v[i] * v[i];
		return Math.Sqrt( s );
	}
}
