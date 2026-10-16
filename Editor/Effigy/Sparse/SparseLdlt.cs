using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Sparse LDLᵀ factorisation of a symmetric matrix: factor once, solve as many right-hand sides
/// as you like.
///
/// WHY FACTOR-ONCE MATTERS. Bone heat solves the same matrix once per bone; ARAP solves the same
/// matrix five to twenty times per edit with a new right-hand side each time. A factorisation
/// costs a few hundred milliseconds on a 50k-vertex mesh and a solve costs a few milliseconds, so
/// keeping the factor is the difference between interactive and not.
///
/// THE ALGORITHM is the up-looking simplicial LDLᵀ (Davis, "Direct Methods for Sparse Linear
/// Systems", 2006, ch. 4): an elimination-tree pass finds each column's non-zero count, then each
/// row of L is a sparse triangular solve whose pattern is a path up that tree. No supernodes, no
/// BLAS — a few hundred lines that a C# kernel can carry as loose source.
///
/// THE ORDERING is what keeps it sparse. Eliminating vertices in mesh order fills in a band as
/// wide as the mesh, which on 50k vertices is tens of millions of entries. <see cref="SparseOrdering.Auto"/>
/// picks minimum degree for small systems and nested dissection for large ones; either keeps the
/// factor within a small multiple of the matrix.
///
/// No pivoting, so the matrix must be symmetric positive definite or at least have non-zero
/// leading minors in the chosen order. A zero or negative pivot is reported through
/// <see cref="TryFactor"/> with the row it happened at — on a mesh, almost always a piece with no
/// constraint on it, whose Laplacian is singular.
/// </summary>
public sealed class SparseLdlt
{
	public readonly int Size;

	// perm[k] = original row eliminated k-th; inverse[original] = k.
	readonly int[] _perm;
	readonly int[] _inverse;

	// L, strictly lower, column by column: column j's rows are _li[_lp[j] .. _lp[j+1]-1].
	readonly int[] _lp;
	readonly int[] _li;
	readonly double[] _lx;
	readonly double[] _d;

	SparseLdlt( int n, int[] perm, int[] inverse, int[] lp, int[] li, double[] lx, double[] d )
	{
		Size = n;
		_perm = perm;
		_inverse = inverse;
		_lp = lp;
		_li = li;
		_lx = lx;
		_d = d;
	}

	/// <summary>Non-zeros in L, below the diagonal — the memory the factor holds, in entries.</summary>
	public int FactorNonZeros => _lp[Size];

	/// <summary>The smallest pivot relative to the largest — near zero means nearly singular.</summary>
	public double PivotRatio
	{
		get
		{
			double lo = double.MaxValue, hi = 0;
			foreach ( var x in _d ) { lo = Math.Min( lo, Math.Abs( x ) ); hi = Math.Max( hi, Math.Abs( x ) ); }
			return hi > 0 ? lo / hi : 0;
		}
	}

	/// <summary>Factor, or throw with the cause. See <see cref="TryFactor"/>.</summary>
	public static SparseLdlt Factor( SparseMatrix a, int[] ordering = null )
	{
		if ( TryFactor( a, out var f, out var why, ordering ) )
			return f;

		throw new InvalidOperationException( why );
	}

	/// <summary>
	/// Factor a symmetric matrix. Only the lower triangle (row ≥ col) is read, so a matrix that is
	/// symmetric in pattern and value is the expectation; it is not checked here because the check
	/// costs as much as a solve. <paramref name="ordering"/> overrides the minimum-degree ordering:
	/// pass an identity to factor in the natural order.
	///
	/// False with <paramref name="why"/> when a pivot vanishes: the matrix is singular in that order,
	/// which for a mesh operator means a connected piece with nothing pinning it.
	/// </summary>
	public static bool TryFactor( SparseMatrix a, out SparseLdlt factor, out string why, int[] ordering = null )
	{
		if ( a is null )
			throw new ArgumentNullException( nameof( a ) );

		if ( a.Rows != a.Cols )
			throw new ArgumentException( $"LDLᵀ needs a square matrix, not {a.Rows}x{a.Cols}" );

		var n = a.Rows;
		var perm = ordering ?? SparseOrdering.Auto( a );

		if ( perm.Length != n )
			throw new ArgumentException( $"The ordering has {perm.Length} entries for a {n}-row matrix" );

		var inverse = new int[n];
		Array.Fill( inverse, -1 );

		for ( var k = 0; k < n; k++ )
		{
			if ( (uint)perm[k] >= (uint)n || inverse[perm[k]] >= 0 )
				throw new ArgumentException( "The ordering is not a permutation" );

			inverse[perm[k]] = k;
		}

		// C = upper triangle of P A Pᵀ, by column. Column k holds the rows i ≤ k it couples to.
		// Read from A's lower triangle — row r ≥ col c — and placed at (min, max) in new numbering.
		var cp = new int[n + 1];

		for ( var r = 0; r < n; r++ )
		{
			for ( var p = a.RowStart[r]; p < a.RowStart[r + 1]; p++ )
			{
				var c = a.Col[p];
				if ( c > r ) continue;
				cp[Math.Max( inverse[r], inverse[c] ) + 1]++;
			}
		}

		for ( var k = 0; k < n; k++ )
			cp[k + 1] += cp[k];

		var ci = new int[cp[n]];
		var cx = new double[cp[n]];
		var fill = (int[])cp.Clone();

		for ( var r = 0; r < n; r++ )
		{
			for ( var p = a.RowStart[r]; p < a.RowStart[r + 1]; p++ )
			{
				var c = a.Col[p];
				if ( c > r ) continue;
				int i = inverse[r], j = inverse[c];
				var col = Math.Max( i, j );
				var at = fill[col]++;
				ci[at] = Math.Min( i, j );
				cx[at] = a.Val[p];
			}
		}

		// Symbolic: elimination tree and the count of each column of L.
		var parent = new int[n];
		var flag = new int[n];
		var lnz = new int[n];

		for ( var k = 0; k < n; k++ )
		{
			parent[k] = -1;
			flag[k] = k;

			for ( var p = cp[k]; p < cp[k + 1]; p++ )
			{
				var i = ci[p];

				if ( i >= k )
					continue;

				for ( ; flag[i] != k; i = parent[i] )
				{
					if ( parent[i] == -1 )
						parent[i] = k;

					lnz[i]++;
					flag[i] = k;
				}
			}
		}

		var lp = new int[n + 1];

		for ( var k = 0; k < n; k++ )
		{
			lp[k + 1] = lp[k] + lnz[k];

			if ( lp[k + 1] < 0 )
				throw new InvalidOperationException( "The factor would hold more than two billion entries; this system is too large to factor directly" );
		}

		var li = new int[lp[n]];
		var lx = new double[lp[n]];
		var d = new double[n];
		var y = new double[n];
		var pattern = new int[n];

		// Scale for the singularity test: a pivot this much smaller than the diagonal it started
		// from has cancelled to roundoff, which is what a singular block looks like in floating point.
		var scale = 0.0;
		for ( var p = 0; p < cx.Length; p++ ) scale = Math.Max( scale, Math.Abs( cx[p] ) );
		var tiny = 1e-13 * Math.Max( scale, 1e-300 );

		Array.Clear( lnz );

		for ( var k = 0; k < n; k++ )
		{
			y[k] = 0.0;
			var top = n;
			flag[k] = k;

			for ( var p = cp[k]; p < cp[k + 1]; p++ )
			{
				var i = ci[p];
				y[i] += cx[p];
				var len = 0;

				for ( ; flag[i] != k; i = parent[i] )
				{
					pattern[len++] = i;
					flag[i] = k;
				}

				while ( len > 0 )
					pattern[--top] = pattern[--len];
			}

			d[k] = y[k];
			y[k] = 0.0;

			for ( ; top < n; top++ )
			{
				var i = pattern[top];
				var yi = y[i];
				y[i] = 0.0;
				var p2 = lp[i] + lnz[i];

				for ( var p = lp[i]; p < p2; p++ )
					y[li[p]] -= lx[p] * yi;

				var lki = yi / d[i];
				d[k] -= lki * yi;
				li[p2] = k;
				lx[p2] = lki;
				lnz[i]++;
			}

			if ( !(Math.Abs( d[k] ) > tiny) )
			{
				factor = null;
				why = $"The system is singular: pivot {k} (row {perm[k]}) is {d[k]:G3}. On a mesh this "
					+ "almost always means a separate piece with nothing holding it in place.";
				return false;
			}
		}

		factor = new SparseLdlt( n, perm, inverse, lp, li, lx, d );
		why = null;
		return true;
	}

	/// <summary>Solve A x = b. <paramref name="b"/> is left alone.</summary>
	public double[] Solve( double[] b )
	{
		var x = new double[Size];
		Solve( b, x );
		return x;
	}

	/// <summary>Solve A x = b into <paramref name="x"/>; b and x may be the same array.</summary>
	public void Solve( double[] b, double[] x )
	{
		if ( b.Length < Size || x.Length < Size )
			throw new ArgumentException( $"Solve needs vectors of {Size}" );

		var w = new double[Size];

		for ( var k = 0; k < Size; k++ )
			w[k] = b[_perm[k]];

		// L w = b
		for ( var j = 0; j < Size; j++ )
		{
			var wj = w[j];

			if ( wj == 0.0 )
				continue;

			for ( var p = _lp[j]; p < _lp[j + 1]; p++ )
				w[_li[p]] -= _lx[p] * wj;
		}

		// D
		for ( var j = 0; j < Size; j++ )
			w[j] /= _d[j];

		// Lᵀ x = w
		for ( var j = Size - 1; j >= 0; j-- )
		{
			var s = w[j];

			for ( var p = _lp[j]; p < _lp[j + 1]; p++ )
				s -= _lx[p] * w[_li[p]];

			w[j] = s;
		}

		for ( var k = 0; k < Size; k++ )
			x[_perm[k]] = w[k];
	}
}

/// <summary>Fill-reducing orderings for <see cref="SparseLdlt"/>.</summary>
public static class SparseOrdering
{
	/// <summary>Below this many unknowns, minimum degree on the explicit graph; above, nested dissection.</summary>
	public const int DissectionThreshold = 3000;

	/// <summary>The ordering <see cref="SparseLdlt.Factor"/> uses when none is given.</summary>
	public static int[] Auto( SparseMatrix a ) =>
		a.Rows > DissectionThreshold ? NestedDissection( a ) : MinimumDegree( a );

	/// <summary>The natural order 0..n-1 — what not ordering at all looks like, for comparison.</summary>
	public static int[] Identity( int n )
	{
		var p = new int[n];
		for ( var i = 0; i < n; i++ ) p[i] = i;
		return p;
	}

	/// <summary>
	/// Minimum-degree ordering on the explicit elimination graph.
	///
	/// Repeatedly eliminate the vertex with the fewest remaining neighbours, joining its neighbours
	/// into a clique — exactly the fill the factorisation will create. This is the classic
	/// algorithm (Tinney &amp; Walker 1967, as surveyed by George &amp; Liu 1989), not the quotient-graph
	/// approximation AMD uses (Amestoy, Davis &amp; Duff 1996): it holds the filled graph explicitly
	/// and computes exact degrees. That is cheap while the graph is small and gets slow as cliques
	/// grow, which is why large systems go through <see cref="NestedDissection"/> and only its
	/// small leaves come here.
	///
	/// Ties break on the lower index, so the ordering — and every result built on it — is
	/// deterministic run to run.
	/// </summary>
	public static int[] MinimumDegree( SparseMatrix a )
	{
		if ( a.Rows != a.Cols )
			throw new ArgumentException( "An ordering needs a square matrix" );

		var graph = Graph.From( a );
		var all = Identity( a.Rows );
		var order = new List<int>( a.Rows );
		MinimumDegreeOn( graph, all, order );
		return order.ToArray();
	}

	/// <summary>
	/// Nested dissection by breadth-first level structures (George 1973; George &amp; Liu 1978).
	///
	/// Find a vertex at one end of the graph, sweep breadth-first from it, and cut at the middle
	/// level: that level separates the graph into two halves no edge crosses. Order the halves
	/// first, recursively, and the separator last — so eliminating either half never fills in
	/// the other. On a mesh a level is a ring a few edges wide, about √n vertices, which is
	/// what gives the n log n fill a 2D-like graph allows. Pieces below
	/// <paramref name="leafSize"/> are finished by <see cref="MinimumDegree"/>.
	///
	/// Deterministic: every choice breaks ties on the lower index.
	/// </summary>
	public static int[] NestedDissection( SparseMatrix a, int leafSize = 200 )
	{
		if ( a.Rows != a.Cols )
			throw new ArgumentException( "An ordering needs a square matrix" );

		var n = a.Rows;
		var graph = Graph.From( a );
		var order = new List<int>( n );
		var region = new int[n];   // which piece a vertex currently belongs to; stamps, never reset
		var level = new int[n];
		var stamp = 0;

		// Explicit work stack rather than recursion: a long chain graph can dissect thousands deep.
		// Each frame is either a piece to split or a separator to emit after its halves.
		var work = new Stack<(int[] Vertices, bool Emit)>();
		work.Push( (Identity( n ), false) );

		while ( work.Count > 0 )
		{
			var (verts, emit) = work.Pop();

			if ( emit )
			{
				order.AddRange( verts );
				continue;
			}

			if ( verts.Length <= leafSize )
			{
				MinimumDegreeOn( graph, verts, order );
				continue;
			}

			stamp++;
			foreach ( var v in verts ) region[v] = stamp;

			// Separate connected components first: each is dissected on its own.
			var components = Components( graph, verts, region, ref stamp );

			if ( components.Count > 1 )
			{
				for ( var c = components.Count - 1; c >= 0; c-- )
					work.Push( (components[c], false) );

				continue;
			}

			var piece = components[0];
			var mine = region[piece[0]];

			// Pseudo-peripheral start: sweep, restart from the farthest, twice.
			var start = piece[0]; // sorted, so the lowest index
			int depth;

			for ( var pass = 0; pass < 3; pass++ )
			{
				Sweep( graph, start, region, mine, level, out _, out var far );

				if ( far == start )
					break;

				start = far;
			}

			Sweep( graph, start, region, mine, level, out depth, out _ );

			if ( depth < 2 )
			{
				// Too shallow to cut (a near-clique): order it directly.
				MinimumDegreeOn( graph, piece, order );
				continue;
			}

			// The level where the running count passes half.
			var counts = new int[depth + 1];
			foreach ( var v in piece ) counts[level[v]]++;

			var cut = 1;
			var running = 0;

			for ( var l = 0; l <= depth; l++ )
			{
				running += counts[l];

				if ( running * 2 >= piece.Length )
				{
					cut = Math.Clamp( l, 1, depth - 1 );
					break;
				}
			}

			// Only the cut-level vertices that actually touch the far side separate anything; the
			// rest join the near half.
			var near = new List<int>();
			var far2 = new List<int>();
			var sep = new List<int>();

			foreach ( var v in piece )
			{
				var lv = level[v];

				if ( lv < cut ) near.Add( v );
				else if ( lv > cut ) far2.Add( v );
				else
				{
					var touches = false;

					for ( var p = graph.Start[v]; p < graph.Start[v + 1]; p++ )
					{
						var u = graph.Adj[p];
						if ( region[u] == mine && level[u] == cut + 1 ) { touches = true; break; }
					}

					(touches ? sep : near).Add( v );
				}
			}

			if ( near.Count == 0 || far2.Count == 0 )
			{
				MinimumDegreeOn( graph, piece, order );
				continue;
			}

			near.Sort();
			far2.Sort();
			sep.Sort();

			// Popped in reverse: near, then far, then the separator after both.
			work.Push( (sep.ToArray(), true) );
			work.Push( (far2.ToArray(), false) );
			work.Push( (near.ToArray(), false) );
		}

		return order.ToArray();
	}

	// --- helpers -----------------------------------------------------------------------------

	/// <summary>Symmetric adjacency of a matrix's off-diagonal pattern, in CSR.</summary>
	sealed class Graph
	{
		public int[] Start;
		public int[] Adj;

		public static Graph From( SparseMatrix a )
		{
			var n = a.Rows;
			var deg = new int[n + 1];

			// Union of A's pattern and its transpose, so a non-symmetric pattern still gives a
			// symmetric graph. Counted per (row, col) once each way, then de-duplicated.
			for ( var r = 0; r < n; r++ )
				for ( var p = a.RowStart[r]; p < a.RowStart[r + 1]; p++ )
				{
					var c = a.Col[p];
					if ( c == r || a.Val[p] == 0.0 ) continue;
					deg[r + 1]++;
					deg[c + 1]++;
				}

			for ( var i = 0; i < n; i++ ) deg[i + 1] += deg[i];

			var adj = new int[deg[n]];
			var fill = (int[])deg.Clone();

			for ( var r = 0; r < n; r++ )
				for ( var p = a.RowStart[r]; p < a.RowStart[r + 1]; p++ )
				{
					var c = a.Col[p];
					if ( c == r || a.Val[p] == 0.0 ) continue;
					adj[fill[r]++] = c;
					adj[fill[c]++] = r;
				}

			var start = new int[n + 1];
			var write = 0;

			for ( var v = 0; v < n; v++ )
			{
				Array.Sort( adj, deg[v], deg[v + 1] - deg[v] );
				start[v] = write;

				for ( var p = deg[v]; p < deg[v + 1]; p++ )
				{
					if ( write > start[v] && adj[write - 1] == adj[p] )
						continue;

					adj[write++] = adj[p];
				}
			}

			start[n] = write;
			Array.Resize( ref adj, write );
			return new Graph { Start = start, Adj = adj };
		}
	}

	static List<int[]> Components( Graph g, int[] verts, int[] region, ref int stamp )
	{
		var mine = region[verts[0]];
		var result = new List<int[]>();
		var queue = new Queue<int>();
		var piece = new List<int>();

		foreach ( var s in verts )
		{
			if ( region[s] != mine )
				continue;

			var label = ++stamp;
			region[s] = label;
			queue.Enqueue( s );
			piece.Clear();

			while ( queue.Count > 0 )
			{
				var v = queue.Dequeue();
				piece.Add( v );

				for ( var p = g.Start[v]; p < g.Start[v + 1]; p++ )
				{
					var u = g.Adj[p];

					if ( region[u] != mine )
						continue;

					region[u] = label;
					queue.Enqueue( u );
				}
			}

			piece.Sort();
			result.Add( piece.ToArray() );
		}

		return result;
	}

	/// <summary>Breadth-first levels from <paramref name="start"/> within one region, into
	/// <paramref name="level"/>; <paramref name="far"/> is the lowest-indexed vertex of the deepest level.</summary>
	static void Sweep( Graph g, int start, int[] region, int mine, int[] level, out int depth, out int far )
	{
		var queue = new Queue<int>();
		var seen = new HashSet<int> { start };
		level[start] = 0;
		queue.Enqueue( start );
		depth = 0;
		far = start;

		while ( queue.Count > 0 )
		{
			var v = queue.Dequeue();

			if ( level[v] > depth || (level[v] == depth && v < far) )
			{
				depth = level[v];
				far = v;
			}

			for ( var p = g.Start[v]; p < g.Start[v + 1]; p++ )
			{
				var u = g.Adj[p];

				if ( region[u] != mine || !seen.Add( u ) )
					continue;

				level[u] = level[v] + 1;
				queue.Enqueue( u );
			}
		}
	}

	/// <summary>Minimum degree on the subgraph induced by <paramref name="verts"/>, appended to <paramref name="order"/>.</summary>
	static void MinimumDegreeOn( Graph g, int[] verts, List<int> order )
	{
		var m = verts.Length;

		if ( m == 0 )
			return;

		var member = new Dictionary<int, int>( m );
		for ( var k = 0; k < m; k++ ) member[verts[k]] = k;

		var adj = new HashSet<int>[m];

		for ( var k = 0; k < m; k++ )
		{
			adj[k] = new HashSet<int>();
			var v = verts[k];

			for ( var p = g.Start[v]; p < g.Start[v + 1]; p++ )
				if ( member.TryGetValue( g.Adj[p], out var j ) )
					adj[k].Add( j );
		}

		var done = new bool[m];
		var queue = new PriorityQueue<int, long>( m );

		for ( var k = 0; k < m; k++ )
			queue.Enqueue( k, Key( adj[k].Count, k ) );

		var nbrs = new List<int>();

		while ( queue.TryDequeue( out var v, out var key ) )
		{
			// Stale entry: eliminated already, or its degree changed after it was queued.
			if ( done[v] || key != Key( adj[v].Count, v ) )
				continue;

			done[v] = true;
			order.Add( verts[v] );

			nbrs.Clear();
			nbrs.AddRange( adj[v] );
			adj[v] = null;

			foreach ( var u in nbrs )
			{
				var set = adj[u];
				set.Remove( v );

				foreach ( var w in nbrs )
					if ( w != u )
						set.Add( w );

				queue.Enqueue( u, Key( set.Count, u ) );
			}
		}

		static long Key( int degree, int vertex ) => ((long)degree << 32) | (uint)vertex;
	}
}
