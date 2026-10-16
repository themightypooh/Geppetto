using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Solve A x = b with some unknowns held at given values — the shape of nearly every mesh solve:
/// ARAP pins its handles and anchors, fairing pins the boundary of the hole, a harmonic field pins
/// its sources.
///
/// The pinned unknowns are eliminated rather than penalised: the free-free block A_ff is factored
/// once, and each solve moves the pinned columns to the right-hand side,
/// A_ff x_f = b_f − A_fc x_c. Exact, and it keeps A_ff symmetric positive definite whenever every
/// connected piece of the mesh has at least one pin.
/// </summary>
public sealed class DirichletSolver
{
	readonly SparseMatrix _a;
	readonly int[] _free;
	readonly int[] _freeIndex;
	readonly bool[] _pinned;
	readonly SparseLdlt _factor;

	public int Size => _a.Rows;
	public int FreeCount => _free.Length;

	/// <summary>The free unknowns' original indices, in solve order.</summary>
	public IReadOnlyList<int> Free => _free;

	DirichletSolver( SparseMatrix a, int[] free, int[] freeIndex, bool[] pinned, SparseLdlt factor )
	{
		_a = a;
		_free = free;
		_freeIndex = freeIndex;
		_pinned = pinned;
		_factor = factor;
	}

	public bool IsPinned( int i ) => _pinned[i];

	/// <summary>Factor, or throw with the cause. See <see cref="TryCreate"/>.</summary>
	public static DirichletSolver Create( SparseMatrix a, IEnumerable<int> pinned )
	{
		if ( TryCreate( a, pinned, out var s, out var why ) )
			return s;

		throw new InvalidOperationException( why );
	}

	/// <summary>
	/// Factor A's free block. False when it is singular — a connected piece of the mesh with no pin,
	/// which can slide anywhere and so has no single answer.
	/// </summary>
	public static bool TryCreate( SparseMatrix a, IEnumerable<int> pinned, out DirichletSolver solver, out string why )
	{
		if ( a is null ) throw new ArgumentNullException( nameof( a ) );
		if ( a.Rows != a.Cols ) throw new ArgumentException( "A constrained solve needs a square matrix" );

		var n = a.Rows;
		var isPinned = new bool[n];

		if ( pinned is not null )
		{
			foreach ( var i in pinned )
			{
				if ( (uint)i >= (uint)n )
					throw new ArgumentOutOfRangeException( nameof( pinned ), $"Pinned index {i} is outside 0..{n - 1}" );

				isPinned[i] = true;
			}
		}

		var freeIndex = new int[n];
		var free = new List<int>( n );

		for ( var i = 0; i < n; i++ )
		{
			freeIndex[i] = isPinned[i] ? -1 : free.Count;
			if ( !isPinned[i] ) free.Add( i );
		}

		var freeArr = free.ToArray();
		SparseLdlt factor = null;

		if ( freeArr.Length > 0 && !SparseLdlt.TryFactor( a.Submatrix( freeArr, freeIndex ), out factor, out why ) )
		{
			solver = null;
			return false;
		}

		solver = new DirichletSolver( a, freeArr, freeIndex, isPinned, factor );
		why = null;
		return true;
	}

	/// <summary>
	/// Solve with right-hand side <paramref name="b"/> (full length; pinned rows ignored) and the
	/// pinned values taken from <paramref name="values"/> (full length; free rows ignored). The
	/// answer is full length, pinned entries copied through.
	/// </summary>
	public double[] Solve( double[] b, double[] values )
	{
		var n = Size;
		var x = new double[n];

		if ( values is not null )
			for ( var i = 0; i < n; i++ )
				if ( _pinned[i] ) x[i] = values[i];

		if ( _free.Length == 0 )
			return x;

		var rhs = new double[_free.Length];

		for ( var k = 0; k < _free.Length; k++ )
		{
			var r = _free[k];
			var s = b is null ? 0.0 : b[r];

			for ( var p = _a.RowStart[r]; p < _a.RowStart[r + 1]; p++ )
			{
				var c = _a.Col[p];
				if ( _pinned[c] ) s -= _a.Val[p] * x[c];
			}

			rhs[k] = s;
		}

		var y = _factor.Solve( rhs );

		for ( var k = 0; k < _free.Length; k++ )
			x[_free[k]] = y[k];

		return x;
	}
}
