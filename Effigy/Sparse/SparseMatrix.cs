using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A sparse matrix in compressed rows (CSR), in doubles.
///
/// THE ONE STORAGE THE SOLVERS SHARE. Every mesh operator in the kernel that needs a linear solve —
/// the cotangent Laplacian under bone heat, ARAP and fairing — is a few non-zeros per row out of
/// tens of thousands of columns. Dense storage for a 50k-vertex mesh would be twenty gigabytes;
/// this is a few megabytes.
///
/// Doubles, not floats, although the mesh is floats: the factorisation subtracts long sums of
/// nearly cancelling products, and in single precision a 50k Laplacian loses most of its digits
/// before the last pivot. Positions go in and come out as floats; only the arithmetic is wide.
///
/// Build one with <see cref="SparseBuilder"/>; rows are sorted and duplicate entries summed, so a
/// matrix assembled triangle by triangle comes out the same as one written row by row.
/// </summary>
public sealed class SparseMatrix
{
	public readonly int Rows;
	public readonly int Cols;

	/// <summary>Row r's entries are RowStart[r] .. RowStart[r+1]-1 in <see cref="Col"/> and <see cref="Val"/>, columns ascending.</summary>
	public readonly int[] RowStart;
	public readonly int[] Col;
	public readonly double[] Val;

	public SparseMatrix( int rows, int cols, int[] rowStart, int[] col, double[] val )
	{
		Rows = rows;
		Cols = cols;
		RowStart = rowStart ?? throw new ArgumentNullException( nameof( rowStart ) );
		Col = col ?? throw new ArgumentNullException( nameof( col ) );
		Val = val ?? throw new ArgumentNullException( nameof( val ) );
	}

	public int NonZeros => RowStart[Rows];

	/// <summary>The entry at (row, col), zero when not stored. Binary search: for checks and tests, not inner loops.</summary>
	public double this[int row, int col]
	{
		get
		{
			int lo = RowStart[row], hi = RowStart[row + 1] - 1;

			while ( lo <= hi )
			{
				var mid = (lo + hi) >> 1;
				var c = Col[mid];

				if ( c == col ) return Val[mid];
				if ( c < col ) lo = mid + 1;
				else hi = mid - 1;
			}

			return 0.0;
		}
	}

	/// <summary>y = A x.</summary>
	public void Multiply( double[] x, double[] y )
	{
		if ( x.Length < Cols || y.Length < Rows )
			throw new ArgumentException( $"Multiply needs x of {Cols} and y of {Rows}, got {x.Length} and {y.Length}" );

		for ( var r = 0; r < Rows; r++ )
		{
			var sum = 0.0;

			for ( var p = RowStart[r]; p < RowStart[r + 1]; p++ )
				sum += Val[p] * x[Col[p]];

			y[r] = sum;
		}
	}

	public double[] Multiply( double[] x )
	{
		var y = new double[Rows];
		Multiply( x, y );
		return y;
	}

	public double[] Diagonal()
	{
		var d = new double[Math.Min( Rows, Cols )];

		for ( var r = 0; r < d.Length; r++ )
			d[r] = this[r, r];

		return d;
	}

	/// <summary>Whether A equals its transpose to within <paramref name="tolerance"/> of the largest entry.</summary>
	public bool IsSymmetric( double tolerance = 1e-9 )
	{
		if ( Rows != Cols )
			return false;

		var scale = 0.0;
		foreach ( var v in Val ) scale = Math.Max( scale, Math.Abs( v ) );
		var limit = tolerance * Math.Max( scale, 1e-300 );

		for ( var r = 0; r < Rows; r++ )
			for ( var p = RowStart[r]; p < RowStart[r + 1]; p++ )
				if ( Math.Abs( Val[p] - this[Col[p], r] ) > limit )
					return false;

		return true;
	}

	/// <summary>This plus a diagonal: A + diag(d). Used for (L + M·H) in bone heat and similar shifts.</summary>
	public SparseMatrix AddDiagonal( double[] d )
	{
		var b = new SparseBuilder( Rows, Cols );

		for ( var r = 0; r < Rows; r++ )
		{
			for ( var p = RowStart[r]; p < RowStart[r + 1]; p++ )
				b.Add( r, Col[p], Val[p] );

			if ( r < d.Length && d[r] != 0.0 )
				b.Add( r, r, d[r] );
		}

		return b.Build();
	}

	/// <summary>
	/// The square block of rows and columns <paramref name="keep"/>, renumbered 0..keep.Length-1 in
	/// that order — the free-free block of a system with some unknowns held fixed.
	/// <paramref name="newIndex"/> maps an old index to its new one, or -1 when dropped.
	/// </summary>
	public SparseMatrix Submatrix( int[] keep, int[] newIndex )
	{
		var b = new SparseBuilder( keep.Length, keep.Length );

		for ( var k = 0; k < keep.Length; k++ )
		{
			var r = keep[k];

			for ( var p = RowStart[r]; p < RowStart[r + 1]; p++ )
			{
				var c = newIndex[Col[p]];

				if ( c >= 0 )
					b.Add( k, c, Val[p] );
			}
		}

		return b.Build();
	}

	/// <summary>Dense copy, for tests on small systems.</summary>
	public double[,] ToDense()
	{
		var d = new double[Rows, Cols];

		for ( var r = 0; r < Rows; r++ )
			for ( var p = RowStart[r]; p < RowStart[r + 1]; p++ )
				d[r, Col[p]] += Val[p];

		return d;
	}

	public override string ToString() => $"SparseMatrix {Rows}x{Cols}, {NonZeros} non-zeros";
}

/// <summary>
/// Assembles a <see cref="SparseMatrix"/> from (row, col, value) triplets in any order.
/// Duplicates are summed — which is what finite-element assembly wants: each triangle adds its
/// share to the edges it touches, and an interior edge is touched twice.
/// </summary>
public sealed class SparseBuilder
{
	readonly int _rows, _cols;
	readonly List<int> _r = new();
	readonly List<int> _c = new();
	readonly List<double> _v = new();

	public SparseBuilder( int rows, int cols )
	{
		if ( rows < 0 || cols < 0 )
			throw new ArgumentOutOfRangeException( nameof( rows ), "A matrix cannot have a negative size" );

		_rows = rows;
		_cols = cols;
	}

	public int Count => _r.Count;

	public void Add( int row, int col, double value )
	{
		if ( (uint)row >= (uint)_rows || (uint)col >= (uint)_cols )
			throw new ArgumentOutOfRangeException( nameof( row ), $"({row}, {col}) is outside a {_rows}x{_cols} matrix" );

		if ( double.IsNaN( value ) || double.IsInfinity( value ) )
			throw new ArgumentException( $"Entry ({row}, {col}) is {value}; a solver cannot recover from that, so it is refused here where the cause is still visible" );

		_r.Add( row );
		_c.Add( col );
		_v.Add( value );
	}

	/// <summary>Add value at (i, j) and (j, i) — once on the diagonal when i == j.</summary>
	public void AddSymmetric( int i, int j, double value )
	{
		Add( i, j, value );

		if ( i != j )
			Add( j, i, value );
	}

	public SparseMatrix Build()
	{
		var count = new int[_rows + 1];

		foreach ( var r in _r )
			count[r + 1]++;

		for ( var r = 0; r < _rows; r++ )
			count[r + 1] += count[r];

		var cursor = (int[])count.Clone();
		var cols = new int[_r.Count];
		var vals = new double[_r.Count];

		for ( var k = 0; k < _r.Count; k++ )
		{
			var at = cursor[_r[k]]++;
			cols[at] = _c[k];
			vals[at] = _v[k];
		}

		// Sort each row by column and merge duplicates in place.
		var start = new int[_rows + 1];
		var write = 0;

		for ( var r = 0; r < _rows; r++ )
		{
			int lo = count[r], hi = count[r + 1];
			Array.Sort( cols, vals, lo, hi - lo );
			start[r] = write;

			for ( var p = lo; p < hi; p++ )
			{
				if ( write > start[r] && cols[write - 1] == cols[p] )
				{
					vals[write - 1] += vals[p];
					continue;
				}

				cols[write] = cols[p];
				vals[write] = vals[p];
				write++;
			}
		}

		start[_rows] = write;
		Array.Resize( ref cols, write );
		Array.Resize( ref vals, write );
		return new SparseMatrix( _rows, _cols, start, cols, vals );
	}
}
