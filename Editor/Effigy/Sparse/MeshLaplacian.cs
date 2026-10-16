using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The cotangent Laplacian of a polygon mesh and its lumped mass matrix — the operator under
/// bone heat, ARAP, fairing and every other "smooth over the surface" solve.
///
/// SIGN. <see cref="L"/> is positive semi-definite: L_ii = Σ w_ij and L_ij = −w_ij, i.e. it is
/// −Δ integrated over each vertex's area. That is the form a Cholesky-style solver wants.
/// Multiply by <see cref="Mass"/>⁻¹ to get the pointwise Laplacian.
///
/// TRIANGLES. Polygons are split with <see cref="NormalBake.FaceTriangles"/> — the one
/// triangulation the bake and the exporters share — so a quad's diagonal here is the diagonal the
/// engine will draw, and the operator agrees with the surface that ships.
///
/// ROBUSTNESS, because sculpted and imported meshes are never clean:
/// <list type="bullet">
/// <item>An obtuse triangle gives a negative cotangent, and an edge with two of them gets a
/// negative weight, which breaks the maximum principle (heat can go below zero) and can make ARAP
/// unstable. With <c>clamp</c> on, edge weights are clamped up to a small positive floor, a
/// fraction of the mean weight — the standard fix (Sorkine &amp; Alexa 2007 §4 mention it). The
/// intrinsic Delaunay Laplacian is the exact fix; this is the cheap one.</item>
/// <item>A sliver or zero-area triangle has an unbounded cotangent. It contributes nothing to
/// the weights (and is counted in <see cref="DegenerateTriangles"/>); its edges keep the floor,
/// so the mesh stays connected and the system stays solvable.</item>
/// <item>Faces with a repeated corner, and vertices no face uses, are tolerated: an unused vertex
/// gets an empty row, which a caller must pin or drop (<see cref="Isolated"/> lists them).</item>
/// </list>
/// </summary>
public sealed class MeshLaplacian
{
	/// <summary>The stiffness matrix, n x n, symmetric positive semi-definite.</summary>
	public SparseMatrix L;

	/// <summary>Lumped (barycentric) vertex areas: a third of each touching triangle.</summary>
	public double[] Mass;

	/// <summary>Every triangle the operator was built on, as vertex indices.</summary>
	public List<(int A, int B, int C)> Triangles;

	/// <summary>Edges whose summed cotangent weight was negative or tiny and was clamped to the floor.</summary>
	public int ClampedEdges;

	/// <summary>Triangles too thin to have a meaningful angle; they added no cotangent weight.</summary>
	public int DegenerateTriangles;

	/// <summary>Vertices no triangle touches. Their rows are empty.</summary>
	public List<int> Isolated = new();

	public int VertexCount => Mass.Length;

	/// <summary>The triangles of every face, as vertex indices, in the bake/export triangulation.</summary>
	public static List<(int A, int B, int C)> TrianglesOf( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var tris = new List<(int, int, int)>( mesh.FaceCount * 2 );

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var idx = mesh.Faces[fi].Indices;

			if ( idx.Length == 3 )
			{
				Push( idx[0], idx[1], idx[2] );
				continue;
			}

			foreach ( var (a, b, c) in NormalBake.FaceTriangles( mesh, fi ) )
				Push( idx[a], idx[b], idx[c] );
		}

		return tris;

		void Push( int a, int b, int c )
		{
			if ( a != b && b != c && a != c )
				tris.Add( (a, b, c) );
		}
	}

	/// <summary>
	/// Build the cotangent Laplacian and mass matrix. <paramref name="clamp"/> keeps every edge
	/// weight positive (see the class notes); turn it off only to inspect the raw operator.
	/// </summary>
	public static MeshLaplacian Cotangent( PolyMesh mesh, bool clamp = true )
	{
		var tris = TrianglesOf( mesh );
		var n = mesh.VertexCount;
		var weights = new Dictionary<long, double>( tris.Count * 2, EdgeKeyComparer.Instance );
		var mass = new double[n];
		var degenerate = 0;
		var p = mesh.Positions;

		foreach ( var (a, b, c) in tris )
		{
			var pa = D( p[a] );
			var pb = D( p[b] );
			var pc = D( p[c] );

			var area2 = Len( Cross( Sub( pb, pa ), Sub( pc, pa ) ) );
			var perimeter = Len( Sub( pb, pa ) ) + Len( Sub( pc, pb ) ) + Len( Sub( pa, pc ) );

			// Area relative to the perimeter squared is scale-free; an equilateral triangle sits near
			// 0.1. Below 1e-6 the angles are numerical noise.
			var thin = !(area2 > 1e-6 * perimeter * perimeter);

			if ( thin )
				degenerate++;
			else
			{
				// The cotangent at each corner weights the opposite edge.
				AddEdge( b, c, Cot( pb, pa, pc ) );
				AddEdge( c, a, Cot( pc, pb, pa ) );
				AddEdge( a, b, Cot( pa, pc, pb ) );
			}

			// Even a thin triangle's edges are real mesh edges; register them so clamping can keep
			// the vertices connected.
			Touch( b, c );
			Touch( c, a );
			Touch( a, b );

			var share = area2 / 6.0;
			mass[a] += share;
			mass[b] += share;
			mass[c] += share;
		}

		// Floor: a fraction of the mean positive weight. Small enough not to change a clean mesh's
		// answer measurably, large enough that the matrix keeps every mesh edge.
		var sum = 0.0;
		var count = 0;

		foreach ( var w in weights.Values )
		{
			if ( w > 0 ) { sum += w; count++; }
		}

		var floor = count > 0 ? 1e-3 * sum / count : 1e-3;
		var clamped = 0;
		var builder = new SparseBuilder( n, n );
		var used = new bool[n];

		foreach ( var (key, raw) in weights )
		{
			var i = (int)(key >> 32);
			var j = (int)(key & 0xffffffff);
			var w = raw;

			if ( clamp && w < floor )
			{
				w = floor;
				clamped++;
			}

			builder.AddSymmetric( i, j, -w );
			builder.Add( i, i, w );
			builder.Add( j, j, w );
			used[i] = used[j] = true;
		}

		var totalMass = 0.0;
		var massed = 0;
		foreach ( var m in mass ) if ( m > 0 ) { totalMass += m; massed++; }
		var massFloor = massed > 0 ? 1e-6 * totalMass / massed : 1.0;

		var isolated = new List<int>();

		for ( var v = 0; v < n; v++ )
		{
			if ( !used[v] )
				isolated.Add( v );

			if ( mass[v] < massFloor )
				mass[v] = massFloor;
		}

		return new MeshLaplacian
		{
			L = builder.Build(),
			Mass = mass,
			Triangles = tris,
			ClampedEdges = clamped,
			DegenerateTriangles = degenerate,
			Isolated = isolated,
		};

		void AddEdge( int i, int j, double cot )
		{
			var key = Key( i, j );
			weights.TryGetValue( key, out var w );
			weights[key] = w + 0.5 * cot;
		}

		void Touch( int i, int j )
		{
			var key = Key( i, j );
			if ( !weights.ContainsKey( key ) ) weights[key] = 0.0;
		}
	}

	/// <summary>
	/// The graph Laplacian: weight 1 on every edge, mass 1 on every vertex. Ignores geometry, so it
	/// is wrong for anything metric, but it is never negative and never degenerate.
	/// </summary>
	public static MeshLaplacian Uniform( PolyMesh mesh )
	{
		var tris = TrianglesOf( mesh );
		var n = mesh.VertexCount;
		var seen = new HashSet<long>( EdgeKeyComparer.Instance );
		var builder = new SparseBuilder( n, n );
		var used = new bool[n];

		foreach ( var (a, b, c) in tris )
		{
			Edge( a, b );
			Edge( b, c );
			Edge( c, a );
		}

		var mass = new double[n];
		Array.Fill( mass, 1.0 );
		var isolated = new List<int>();
		for ( var v = 0; v < n; v++ ) if ( !used[v] ) isolated.Add( v );

		return new MeshLaplacian { L = builder.Build(), Mass = mass, Triangles = tris, Isolated = isolated };

		void Edge( int i, int j )
		{
			if ( !seen.Add( Key( i, j ) ) )
				return;

			builder.AddSymmetric( i, j, -1.0 );
			builder.Add( i, i, 1.0 );
			builder.Add( j, j, 1.0 );
			used[i] = used[j] = true;
		}
	}

	/// <summary>The edge weight w_ij (the negated off-diagonal), zero when i and j share no edge.</summary>
	public double Weight( int i, int j ) => -L[i, j];

	/// <summary>Connected piece per vertex through the operator's edges, numbered from 0; and how many pieces.</summary>
	public int[] Components( out int count )
	{
		var n = VertexCount;
		var label = new int[n];
		Array.Fill( label, -1 );
		count = 0;
		var stack = new Stack<int>();

		for ( var s = 0; s < n; s++ )
		{
			if ( label[s] >= 0 )
				continue;

			label[s] = count;
			stack.Push( s );

			while ( stack.Count > 0 )
			{
				var v = stack.Pop();

				for ( var p = L.RowStart[v]; p < L.RowStart[v + 1]; p++ )
				{
					var u = L.Col[p];

					if ( label[u] >= 0 )
						continue;

					label[u] = count;
					stack.Push( u );
				}
			}

			count++;
		}

		return label;
	}

	/// <summary>
	/// Hashes a packed edge key. The default long hash folds the halves together with XOR, so the
	/// key of edge (i, j) hashes to i ^ j — and neighbouring vertices have neighbouring indices, so
	/// a whole mesh lands in a few hundred buckets. That made a 50k-vertex build take twelve seconds.
	/// </summary>
	sealed class EdgeKeyComparer : IEqualityComparer<long>
	{
		public static readonly EdgeKeyComparer Instance = new();
		public bool Equals( long a, long b ) => a == b;

		public int GetHashCode( long k )
		{
			var x = (ulong)k * 0x9E3779B97F4A7C15UL;
			return (int)(x >> 32) ^ (int)x;
		}
	}

	static long Key( int i, int j ) => i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;

	// cot of the angle at `apex` between the edges to `u` and `v`.
	static double Cot( (double, double, double) u, (double, double, double) apex, (double, double, double) v )
	{
		var e1 = Sub( u, apex );
		var e2 = Sub( v, apex );
		var cross = Len( Cross( e1, e2 ) );
		return cross > 0 ? Dot( e1, e2 ) / cross : 0.0;
	}

	static (double, double, double) D( Vec3 v ) => (v.x, v.y, v.z);
	static (double, double, double) Sub( (double x, double y, double z) a, (double x, double y, double z) b ) => (a.x - b.x, a.y - b.y, a.z - b.z);
	static double Dot( (double x, double y, double z) a, (double x, double y, double z) b ) => a.x * b.x + a.y * b.y + a.z * b.z;
	static (double, double, double) Cross( (double x, double y, double z) a, (double x, double y, double z) b ) =>
		(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
	static double Len( (double x, double y, double z) a ) => Math.Sqrt( a.x * a.x + a.y * a.y + a.z * a.z );
}
