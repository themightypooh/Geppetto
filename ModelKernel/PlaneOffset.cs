using System;
using System.Collections.Generic;

namespace ModelKernel;

/// <summary>
/// The offset solve shared by shell and bevel: find the displacement that puts a point exactly
/// `distance` from every one of a set of planes, moving as little as possible.
///
///     for each plane i:   dot( n_i, d ) = distance,     minimising |d|
///
/// Two operations need this and they look nothing alike until you write them down. Shell moves a
/// vertex inward so every face it touches ends up `thickness` away. Bevel insets a face corner so
/// both edges meeting there end up `distance` away — the same problem, with the two edge normals
/// taken inside the face plane instead of the face normals taken in space.
///
/// MINIMISING |d| IS NOT A DETAIL. The system is underdetermined wherever fewer than three planes
/// meet, which is the normal case: every bevel corner has two, and so does every vertex on the rim
/// of a shell opening. Any solution satisfies the distances; only the least-norm one refuses to
/// slide the point sideways along the edge, which is what keeps a rim flush and a bevel even.
///
/// Solved in closed form by plane count. No matrix machinery for the two common cases, and the
/// closed forms are exact rather than iterated:
///
///     one plane      d = t * n
///     two planes     d = t / (1 + n0·n1) * (n0 + n1)
///     three or more  normal equations, in double precision
/// </summary>
public static class PlaneOffset
{
	/// <summary>Planes whose normals differ by less than this count as one. Duplicated constraints
	/// bias a least-squares fit toward whichever plane happens to be split into more polygons — and
	/// subdivision splits every one of them.</summary>
	public const double DistinctTolerance = 1e-4;

	/// <summary>
	/// The displacement satisfying every plane's distance constraint, with the smallest magnitude.
	///
	/// Returns false when no exact solution exists: anti-parallel planes, which face away from each
	/// other so no single step clears both, or three-plus planes whose normals neither span three
	/// dimensions nor agree. `displacement` still receives the best available answer, so a caller
	/// that wants to carry on can, but it has been told.
	/// </summary>
	public static bool TrySolve( IReadOnlyList<Vec3> planeNormals, float distance, out Vec3 displacement )
	{
		switch ( planeNormals.Count )
		{
			case 0:
				displacement = Vec3.Zero;
				return false;

			// A flat region: straight along the one normal.
			case 1:
				displacement = planeNormals[0] * distance;
				return true;

			// An edge. Two constraints pin two directions and leave the third free; the least-norm
			// solution takes no step at all along the edge.
			//
			//   d = a*n0 + b*n1, and symmetry gives a = b = t / (1 + n0·n1)
			case 2:
				return TrySolvePair( planeNormals[0], planeNormals[1], distance, out displacement );

			default:
			{
				if ( TrySolveNormalEquations( planeNormals, distance, out displacement ) )
					return true;

				// Rank-deficient with three or more: the normals lie in a plane or a line, so this
				// corner is not really a corner. Fall back to the two furthest from parallel, which
				// is exact when the rest are redundant and approximate when they are not.
				var (a, b) = MostIndependentPair( planeNormals );
				TrySolvePair( planeNormals[a], planeNormals[b], distance, out displacement );
				return false;
			}
		}
	}

	static bool TrySolvePair( Vec3 n0, Vec3 n1, float distance, out Vec3 displacement )
	{
		var c = Vec3.Dot( n0, n1 );

		// Anti-parallel planes face away from each other, so nothing is `distance` clear of both.
		// That is a zero-thickness sheet, not a solid.
		if ( 1f + c < 1e-6f )
		{
			displacement = n0 * distance;
			return false;
		}

		displacement = (n0 + n1) * (distance / (1f + c));
		return true;
	}

	/// <summary>
	/// Least squares through the normal equations (A^T A) d = A^T b, by Cramer's rule.
	///
	/// Double precision on purpose. The kernel is float everywhere else, but this is the one place
	/// the answer comes from a difference of similar quantities, and a 3x3 determinant of
	/// near-parallel normals loses far too many digits in single.
	/// </summary>
	static bool TrySolveNormalEquations( IReadOnlyList<Vec3> planes, float distance, out Vec3 displacement )
	{
		displacement = Vec3.Zero;

		var m = new double[3, 3];
		var rhs = new double[3];

		foreach ( var f in planes )
		{
			double fx = f.x, fy = f.y, fz = f.z;

			m[0, 0] += fx * fx; m[0, 1] += fx * fy; m[0, 2] += fx * fz;
			m[1, 0] += fy * fx; m[1, 1] += fy * fy; m[1, 2] += fy * fz;
			m[2, 0] += fz * fx; m[2, 1] += fz * fy; m[2, 2] += fz * fz;

			rhs[0] += fx * distance;
			rhs[1] += fy * distance;
			rhs[2] += fz * distance;
		}

		var det = Determinant( m );

		if ( Math.Abs( det ) < 1e-9 * planes.Count )
			return false;

		var x = Determinant( Replace( m, 0, rhs ) ) / det;
		var y = Determinant( Replace( m, 1, rhs ) ) / det;
		var z = Determinant( Replace( m, 2, rhs ) ) / det;

		if ( double.IsNaN( x ) || double.IsNaN( y ) || double.IsNaN( z ) )
			return false;

		displacement = new Vec3( (float)x, (float)y, (float)z );
		return true;
	}

	static (int, int) MostIndependentPair( IReadOnlyList<Vec3> planes )
	{
		var bestA = 0;
		var bestB = 1;
		var lowest = float.MaxValue;

		for ( var i = 0; i < planes.Count; i++ )
		{
			for ( var j = i + 1; j < planes.Count; j++ )
			{
				var dot = MathF.Abs( Vec3.Dot( planes[i], planes[j] ) );

				if ( dot < lowest )
				{
					lowest = dot;
					bestA = i;
					bestB = j;
				}
			}
		}

		return (bestA, bestB);
	}

	/// <summary>Collapse near-duplicate normals, so one flat surface split into several polygons
	/// contributes one constraint rather than several identical ones.</summary>
	public static List<Vec3> Distinct( IEnumerable<Vec3> normals )
	{
		var distinct = new List<Vec3>( 4 );

		foreach ( var n in normals )
		{
			var seen = false;

			foreach ( var existing in distinct )
			{
				if ( 1.0 - Vec3.Dot( existing, n ) < DistinctTolerance )
				{
					seen = true;
					break;
				}
			}

			if ( !seen )
				distinct.Add( n );
		}

		return distinct;
	}

	static double Determinant( double[,] m ) =>
		m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
		- m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
		+ m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

	static double[,] Replace( double[,] m, int column, double[] with )
	{
		var copy = (double[,])m.Clone();

		for ( var r = 0; r < 3; r++ )
			copy[r, column] = with[r];

		return copy;
	}
}
