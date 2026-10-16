using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The sparse solver layer: CSR assembly, the minimum-degree LDLᵀ, CG, the cotangent Laplacian and
/// the pinned solve. Everything downstream — bone heat, ARAP — trusts these numbers, and a solver
/// that is subtly wrong still returns smooth, plausible-looking garbage. So the checks are against
/// a dense reference and against identities the operator must satisfy exactly (linear precision
/// of the cotangent Laplacian on a flat mesh), not against a picture.
/// </summary>
public static class SparseSolverTests
{
	public static void Run()
	{
		Section( "sparse: assembly and the direct solver against a dense one" );
		TestBuilderMergesDuplicates();
		TestLdltMatchesDense();
		TestOrderingDoesNotChangeTheAnswer();
		TestSingularIsReported();
		TestConjugateGradientAgrees();

		Section( "sparse: the cotangent Laplacian" );
		TestLaplacianIdentities();
		TestLinearPrecisionOnAFlatMesh();
		TestObtuseAndDegenerateTriangles();
		TestQuadsSplitLikeTheBake();

		Section( "sparse: pinned solves and a 50k-vertex timing" );
		TestHarmonicInterpolation();
		TestFiftyThousandVertices();
	}

	static void TestBuilderMergesDuplicates()
	{
		var b = new SparseBuilder( 3, 3 );
		b.Add( 1, 2, 1.5 );
		b.Add( 1, 0, 2 );
		b.Add( 1, 2, 0.5 );
		b.Add( 0, 0, 4 );
		var m = b.Build();

		Check( "duplicates are summed and rows sorted",
			m.NonZeros == 3 && m[1, 2] == 2.0 && m[1, 0] == 2.0 && m[0, 0] == 4.0 && m.Col[m.RowStart[1]] == 0,
			m.ToString() );

		var threw = false;
		try { b.Add( 0, 0, double.NaN ); } catch ( ArgumentException ) { threw = true; }
		Check( "a NaN entry is refused at assembly, where its cause is visible", threw );
	}

	/// <summary>A random sparse SPD matrix: a random symmetric pattern made diagonally dominant.</summary>
	static SparseMatrix RandomSpd( int n, int perRow, int seed )
	{
		var rng = new Random( seed );
		var b = new SparseBuilder( n, n );
		var rowAbs = new double[n];

		for ( var i = 0; i < n; i++ )
		{
			for ( var k = 0; k < perRow; k++ )
			{
				var j = rng.Next( n );
				if ( j == i ) continue;
				var v = rng.NextDouble() * 2 - 1;
				b.AddSymmetric( i, j, v );
				rowAbs[i] += Math.Abs( v );
				rowAbs[j] += Math.Abs( v );
			}
		}

		for ( var i = 0; i < n; i++ )
			b.Add( i, i, rowAbs[i] + 0.5 + rng.NextDouble() );

		return b.Build();
	}

	static double[] DenseSolve( double[,] a, double[] rhs )
	{
		var n = rhs.Length;
		var m = (double[,])a.Clone();
		var x = (double[])rhs.Clone();

		for ( var k = 0; k < n; k++ )
		{
			var piv = k;
			for ( var i = k + 1; i < n; i++ ) if ( Math.Abs( m[i, k] ) > Math.Abs( m[piv, k] ) ) piv = i;

			if ( piv != k )
			{
				for ( var j = 0; j < n; j++ ) (m[k, j], m[piv, j]) = (m[piv, j], m[k, j]);
				(x[k], x[piv]) = (x[piv], x[k]);
			}

			for ( var i = k + 1; i < n; i++ )
			{
				var f = m[i, k] / m[k, k];
				for ( var j = k; j < n; j++ ) m[i, j] -= f * m[k, j];
				x[i] -= f * x[k];
			}
		}

		for ( var i = n - 1; i >= 0; i-- )
		{
			var s = x[i];
			for ( var j = i + 1; j < n; j++ ) s -= m[i, j] * x[j];
			x[i] = s / m[i, i];
		}

		return x;
	}

	static double MaxDiff( double[] a, double[] b ) => a.Zip( b, ( x, y ) => Math.Abs( x - y ) ).Max();

	static void TestLdltMatchesDense()
	{
		var worst = 0.0;

		for ( var seed = 1; seed <= 5; seed++ )
		{
			var a = RandomSpd( 40, 3, seed );
			var rng = new Random( seed * 7 );
			var rhs = Enumerable.Range( 0, 40 ).Select( _ => rng.NextDouble() * 10 - 5 ).ToArray();
			var sparse = SparseLdlt.Factor( a ).Solve( rhs );
			var dense = DenseSolve( a.ToDense(), rhs );
			worst = Math.Max( worst, MaxDiff( sparse, dense ) );
		}

		Check( "LDLᵀ matches dense Gaussian elimination on five random 40x40 SPD systems", worst < 1e-10, $"worst {worst:G3}" );

		// Factor once, solve many: two right-hand sides through one factor, each against dense.
		var m = RandomSpd( 25, 4, 11 );
		var f = SparseLdlt.Factor( m );
		var r1 = Enumerable.Range( 0, 25 ).Select( i => (double)i ).ToArray();
		var r2 = Enumerable.Range( 0, 25 ).Select( i => Math.Sin( i ) ).ToArray();
		var e = Math.Max( MaxDiff( f.Solve( r1 ), DenseSolve( m.ToDense(), r1 ) ), MaxDiff( f.Solve( r2 ), DenseSolve( m.ToDense(), r2 ) ) );
		Check( "one factor solves several right-hand sides", e < 1e-10, $"{e:G3}" );
	}

	static void TestOrderingDoesNotChangeTheAnswer()
	{
		var lap = MeshLaplacian.Cotangent( Primitives.QuadSphere( 1f, 8 ) );
		var a = lap.L.AddDiagonal( lap.Mass );
		var rhs = Enumerable.Range( 0, a.Rows ).Select( i => Math.Cos( i * 0.37 ) ).ToArray();
		var md = SparseLdlt.Factor( a );
		var natural = SparseLdlt.Factor( a, SparseOrdering.Identity( a.Rows ) );
		var diff = MaxDiff( md.Solve( rhs ), natural.Solve( rhs ) );

		Check( "minimum-degree and natural orderings give the same answer", diff < 1e-9, $"{diff:G3}" );
		Check( "minimum degree keeps the factor sparser than the natural order",
			md.FactorNonZeros < natural.FactorNonZeros,
			$"min-degree {md.FactorNonZeros} vs natural {natural.FactorNonZeros} non-zeros (matrix {a.NonZeros})" );

		var perm = SparseOrdering.MinimumDegree( a );
		Check( "the ordering is a permutation", perm.OrderBy( x => x ).SequenceEqual( Enumerable.Range( 0, a.Rows ) ) );

		var nd = SparseOrdering.NestedDissection( a, leafSize: 40 );
		var ndFactor = SparseLdlt.Factor( a, nd );
		var ndDiff = MaxDiff( ndFactor.Solve( rhs ), natural.Solve( rhs ) );
		Check( "nested dissection is a permutation, gives the same answer and less fill than natural",
			nd.OrderBy( x => x ).SequenceEqual( Enumerable.Range( 0, a.Rows ) ) && ndDiff < 1e-9 && ndFactor.FactorNonZeros < natural.FactorNonZeros,
			$"{ndFactor.FactorNonZeros} non-zeros, difference {ndDiff:G3}" );

		// Two separate pieces and an isolated vertex: dissection must order every one of them.
		var two = new SparseBuilder( 7, 7 );
		for ( var i = 0; i < 7; i++ ) two.Add( i, i, 4 );
		two.AddSymmetric( 0, 1, -1 ); two.AddSymmetric( 1, 2, -1 ); two.AddSymmetric( 4, 5, -1 ); two.AddSymmetric( 5, 6, -1 );
		var split = SparseOrdering.NestedDissection( two.Build(), leafSize: 1 );
		Check( "dissection orders disconnected pieces and isolated unknowns", split.OrderBy( x => x ).SequenceEqual( Enumerable.Range( 0, 7 ) ) );
	}

	static void TestSingularIsReported()
	{
		// A closed sphere's Laplacian has the constants in its null space: nothing pins it.
		var lap = MeshLaplacian.Cotangent( Primitives.QuadSphere( 1f, 4 ) );
		var ok = SparseLdlt.TryFactor( lap.L, out _, out var why );
		Check( "an unpinned Laplacian is reported singular with a cause", !ok && why is not null && why.Contains( "nothing holding" ), why );

		var pinned = DirichletSolver.TryCreate( lap.L, new[] { 0 }, out _, out var why2 );
		Check( "one pin is enough to make it solvable", pinned, why2 );
	}

	static void TestConjugateGradientAgrees()
	{
		var lap = MeshLaplacian.Cotangent( Primitives.QuadSphere( 1f, 10 ) );
		var a = lap.L.AddDiagonal( lap.Mass.Select( m => m * 3 ).ToArray() );
		var rhs = Enumerable.Range( 0, a.Rows ).Select( i => (i % 7) - 3.0 ).ToArray();
		var direct = SparseLdlt.Factor( a ).Solve( rhs );
		var x = new double[a.Rows];
		var res = ConjugateGradient.Solve( a, rhs, x, 1e-12 );
		var diff = MaxDiff( direct, x ) / Math.Max( 1e-30, direct.Max( Math.Abs ) );

		Check( "Jacobi-preconditioned CG converges to the direct answer", res.Converged && diff < 1e-8, $"{res}; relative difference {diff:G3}" );
	}

	static void TestLaplacianIdentities()
	{
		var mesh = Primitives.QuadSphere( 2f, 6 );
		var lap = MeshLaplacian.Cotangent( mesh );
		var rowSum = 0.0;

		for ( var r = 0; r < lap.L.Rows; r++ )
		{
			var s = 0.0;
			for ( var p = lap.L.RowStart[r]; p < lap.L.RowStart[r + 1]; p++ ) s += lap.L.Val[p];
			rowSum = Math.Max( rowSum, Math.Abs( s ) );
		}

		Check( "every row sums to zero (constants are harmonic)", rowSum < 1e-12, $"{rowSum:G3}" );
		Check( "the operator is symmetric", lap.L.IsSymmetric() );

		var area = lap.Mass.Sum();
		var triArea = lap.Triangles.Sum( t =>
		{
			var p = mesh.Positions;
			return 0.5 * Vec3.Cross( p[t.B] - p[t.A], p[t.C] - p[t.A] ).Length;
		} );
		Check( "the lumped masses sum to the surface area", Math.Abs( area - triArea ) < 1e-4 * triArea, $"{area:F4} vs {triArea:F4}" );
		Check( "a sphere is under 4πr² but close (it is a polyhedron)", area < 4 * Math.PI * 4 && area > 0.95 * 4 * Math.PI * 4, $"{area:F3}" );

		var offDiag = true;
		for ( var r = 0; r < lap.L.Rows; r++ )
			for ( var p = lap.L.RowStart[r]; p < lap.L.RowStart[r + 1]; p++ )
				if ( lap.L.Col[p] != r && lap.L.Val[p] > 0 ) offDiag = false;
		Check( "clamped weights are all positive (off-diagonals ≤ 0)", offDiag );
	}

	/// <summary>A jittered flat grid of triangles and quads: the cotangent Laplacian of a planar mesh
	/// annihilates linear functions at interior vertices, exactly. Uniform weights would not.</summary>
	static PolyMesh JitteredGrid( int n, float jitter, int seed, bool quads )
	{
		var rng = new Random( seed );
		var m = new PolyMesh();

		for ( var y = 0; y <= n; y++ )
			for ( var x = 0; x <= n; x++ )
			{
				var edge = x == 0 || y == 0 || x == n || y == n;
				var jx = edge ? 0 : (float)(rng.NextDouble() - 0.5) * jitter;
				var jy = edge ? 0 : (float)(rng.NextDouble() - 0.5) * jitter;
				m.AddVertex( new Vec3( x + jx, y + jy, 0 ) );
			}

		for ( var y = 0; y < n; y++ )
			for ( var x = 0; x < n; x++ )
			{
				int a = y * (n + 1) + x, b = a + 1, c = a + n + 2, d = a + n + 1;

				if ( quads ) m.AddFace( new[] { a, b, c, d } );
				else { m.AddFace( new[] { a, b, c } ); m.AddFace( new[] { a, c, d } ); }
			}

		return m;
	}

	static void TestLinearPrecisionOnAFlatMesh()
	{
		foreach ( var quads in new[] { false, true } )
		{
			var mesh = JitteredGrid( 12, 0.5f, 3, quads );
			var lap = MeshLaplacian.Cotangent( mesh, clamp: false );
			var f = mesh.Positions.Select( p => 2.0 * p.x - 0.7 * p.y + 1 ).ToArray();
			var lf = lap.L.Multiply( f );
			var worst = 0.0;

			for ( var v = 0; v < mesh.VertexCount; v++ )
			{
				var p = mesh.Positions[v];
				if ( p.x <= 0 || p.y <= 0 || p.x >= 12 || p.y >= 12 ) continue;
				worst = Math.Max( worst, Math.Abs( lf[v] ) );
			}

			Check( $"linear functions are harmonic inside a jittered flat {(quads ? "quad" : "triangle")} grid", worst < 1e-4, $"{worst:G3}" );
		}
	}

	static void TestObtuseAndDegenerateTriangles()
	{
		// A fan of very flat obtuse triangles around a vertex, plus one zero-area sliver.
		var m = new PolyMesh();
		m.AddVertex( new Vec3( 0, 0, 0 ) );     // 0
		m.AddVertex( new Vec3( 10, 0, 0 ) );    // 1
		m.AddVertex( new Vec3( 5, 0.2f, 0 ) );  // 2 — apex of an obtuse triangle (0,1,2)
		m.AddVertex( new Vec3( 5, -0.2f, 0 ) ); // 3 — and of (1,0,3)
		m.AddVertex( new Vec3( 20, 0, 0 ) );    // 4 — collinear with 0,1: zero-area (0,1,4)? use (1,4,x)
		m.AddVertex( new Vec3( 15, 0, 0 ) );    // 5 — exactly between 1 and 4: (1,5,4) has zero area
		m.AddFace( new[] { 0, 1, 2 } );
		m.AddFace( new[] { 1, 0, 3 } );
		m.AddFace( new[] { 1, 4, 2 } );
		m.AddFace( new[] { 1, 5, 4 } );

		var raw = MeshLaplacian.Cotangent( m, clamp: false );
		var neg = raw.Weight( 0, 1 ) < 0;
		Check( "an edge between two obtuse angles has a negative raw cotangent weight", neg, $"{raw.Weight( 0, 1 ):G3}" );

		var lap = MeshLaplacian.Cotangent( m );
		Check( "clamping makes it positive and counts it", lap.Weight( 0, 1 ) > 0 && lap.ClampedEdges > 0, $"w01 {lap.Weight( 0, 1 ):G3}, clamped {lap.ClampedEdges}" );
		Check( "a zero-area triangle is counted, not divided by", lap.DegenerateTriangles == 1 && lap.Weight( 1, 5 ) > 0 && lap.Weight( 5, 4 ) > 0,
			$"degenerate {lap.DegenerateTriangles}" );
		Check( "every finite", lap.L.Val.All( double.IsFinite ) && lap.Mass.All( double.IsFinite ) );

		var pinned = DirichletSolver.TryCreate( lap.L, new[] { 0 }, out _, out var why );
		Check( "the degenerate mesh still factors with one pin", pinned, why );
	}

	static void TestQuadsSplitLikeTheBake()
	{
		var mesh = JitteredGrid( 4, 0.6f, 9, true );
		// Make the quads non-planar so the diagonal matters.
		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var p = mesh.Positions[v];
			mesh.Positions[v] = new Vec3( p.x, p.y, (float)Math.Sin( v * 1.3 ) * 0.4f );
		}

		var expected = new List<(int, int, int)>();
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			foreach ( var (a, b, c) in NormalBake.FaceTriangles( mesh, fi ) )
				expected.Add( (mesh.Faces[fi].Indices[a], mesh.Faces[fi].Indices[b], mesh.Faces[fi].Indices[c]) );

		Check( "quads are split along the same diagonals as the bake and export", MeshLaplacian.TrianglesOf( mesh ).SequenceEqual( expected ) );
	}

	static void TestHarmonicInterpolation()
	{
		var mesh = JitteredGrid( 10, 0.4f, 5, false );
		var lap = MeshLaplacian.Cotangent( mesh, clamp: false );
		var boundary = Enumerable.Range( 0, mesh.VertexCount ).Where( v =>
		{
			var p = mesh.Positions[v];
			return p.x <= 0 || p.y <= 0 || p.x >= 10 || p.y >= 10;
		} ).ToList();

		var values = mesh.Positions.Select( p => 0.3 * p.x + 1.1 * p.y - 2 ).ToArray();
		var solver = DirichletSolver.Create( lap.L, boundary );
		var x = solver.Solve( null, values );
		var err = MaxDiff( x, values );

		Check( "a pinned harmonic solve reproduces a linear field exactly", err < 1e-6, $"{err:G3}; {solver.FreeCount} free of {solver.Size}" );
	}

	static void TestFiftyThousandVertices()
	{
		var mesh = Primitives.QuadSphere( 10f, 92 );
		var sw = Stopwatch.StartNew();
		var lap = MeshLaplacian.Cotangent( mesh );
		var build = sw.Elapsed.TotalMilliseconds;

		sw.Restart();
		var a = lap.L.AddDiagonal( lap.Mass );
		var order = SparseOrdering.Auto( a );
		var ordering = sw.Elapsed.TotalMilliseconds;

		sw.Restart();
		var f = SparseLdlt.Factor( a, order );
		var factor = sw.Elapsed.TotalMilliseconds;

		var rhs = mesh.Positions.Select( p => (double)p.z ).ToArray();
		sw.Restart();
		var x = f.Solve( rhs );
		var solve = sw.Elapsed.TotalMilliseconds;

		var ax = a.Multiply( x );
		var res = Math.Sqrt( ax.Zip( rhs, ( u, v ) => (u - v) * (u - v) ).Sum() / rhs.Sum( v => v * v ) );

		Console.WriteLine( $"  note  {mesh.VertexCount} vertices: Laplacian {build:F0} ms, ordering {ordering:F0} ms, "
			+ $"factor {factor:F0} ms ({f.FactorNonZeros} non-zeros in L, {a.NonZeros} in A), solve {solve:F1} ms" );

		Check( "a 50k-vertex system factors and solves to a tiny residual", mesh.VertexCount >= 50000 && res < 1e-10, $"{mesh.VertexCount} vertices, residual {res:G3}" );
	}
}
