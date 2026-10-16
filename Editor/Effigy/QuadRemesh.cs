using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Effigy;

/// <summary>
/// Automatic quad retopology — the half of Remesh that <see cref="Decimate"/> says it is not.
///
/// THE GAP IT CLOSES. Decimation makes a dense import small and leaves it as triangles, and the
/// assets this tool is trying to stand next to are not triangles: a pro organic model is a
/// ZRemesher-style quad mesh, all quads, a handful of poles, edges all about the same length.
/// That is what subdivision, skinning and a UV unwrap want, and building it by hand with PolyBuild
/// over a sculpt is an afternoon per part. This is the button that does the afternoon.
///
/// INSTANT MESHES, JAKOB ET AL. 2015, written from the paper rather than from a library. Two
/// fields are solved on the dense surface and the mesh is read out of them:
///
/// 1. <b>Orientation</b>, a 4-RoSy field: at every vertex, a tangent direction that only means
///    something up to a quarter turn, which is exactly the symmetry of a quad's edges. It is
///    smoothed so neighbours agree, and pulled toward the principal curvature direction where the
///    surface has one — a cylinder's loops go round it, not along a diagonal.
/// 2. <b>Position</b>, a 4-PoSy field: at every vertex, the nearest point of a square lattice of
///    the target edge length laid in that orientation. Smoothed the same way, so neighbouring
///    vertices agree where the lattice points are.
/// 3. <b>Extraction.</b> Two neighbouring vertices whose lattice points coincide are the same
///    output vertex; two whose points are one lattice step apart are an output edge. Collapse the
///    first, connect the second, walk the faces of the graph that results.
///
/// Both fields are solved on a HIERARCHY — vertices paired up level by level until a few dozen
/// are left — coarsest first. A smoothing pass only talks to neighbours, so on a fine mesh alone a
/// field takes thousands of passes to agree end to end; on the hierarchy it is a few dozen a
/// level, which is the difference between seconds and minutes on a 50k-face import.
///
/// PURE QUADS BY ONE SUBDIVISION. What extraction gives is quad-dominant: mostly quads with a
/// scatter of triangles and pentagons where the field could not decide. Those are paired into
/// quads where a neighbour allows it and split where it does not, and then the whole thing is
/// solved at twice the edge length and split once, every n-gon into n quads — the same trick
/// Instant Meshes' own pure-quad mode uses. Afterwards every face is a quad and every leftover
/// triangle is one valence-3 pole instead of a hole in the grid. The vertices are relaxed along
/// the surface and put back on the source, which is what evens out the edge lengths.
///
/// SYMMETRY IN X is done by remeshing the positive half and mirroring it, with the seam made a
/// border the fields have to respect: the orientation runs along it and a lattice line lies on
/// it. That is exact rather than approximately symmetric, which is the version worth having on a
/// character.
///
/// WHAT IT DOES NOT KEEP: UVs, skin weights, vertex colour and material slots. The topology is new,
/// so there is nothing per-vertex to carry them on; the result is one material and no UVs, and
/// the feature says so.
/// </summary>
public static class QuadRemesh
{
	public sealed class Options
	{
		/// <summary>Quads to aim for. A target, not a promise — the count lands within about ten
		/// percent of it, because the lattice decides where faces fall.</summary>
		public int TargetFaces = 4000;

		/// <summary>Remesh the positive-X half and mirror it, so the result is exactly symmetric.</summary>
		public bool SymmetryX;

		/// <summary>How hard the orientation leans toward principal curvature, 0..1. Zero is the
		/// smoothest field; one lets strongly curved regions overrule their neighbours. LOW BY
		/// DEFAULT because every place the curvature disagrees with the smooth field is a place the
		/// lattice has to bend, and a bend in the lattice is a pole: measured on SDF blobs, 0.15
		/// kept the loops round the limbs at about 3% poles, and 1.0 doubled the poles and tore the
		/// mirror seam.</summary>
		public float CurvatureAlign = 0.15f;

		/// <summary>Tangential smoothing passes over the final mesh, each followed by a
		/// reprojection onto the source.</summary>
		public int RelaxPasses = 10;
	}

	/// <summary>The three numbers a quad mesh is judged by, measured rather than assumed.</summary>
	public readonly struct Stats
	{
		public readonly int Faces, Quads, Vertices, Poles;
		public readonly float EdgeMean, EdgeCv;

		public Stats( int faces, int quads, int vertices, int poles, float edgeMean, float edgeCv )
		{
			Faces = faces;
			Quads = quads;
			Vertices = vertices;
			Poles = poles;
			EdgeMean = edgeMean;
			EdgeCv = edgeCv;
		}

		public float QuadFraction => Faces == 0 ? 0f : (float)Quads / Faces;
		public float PoleFraction => Vertices == 0 ? 0f : (float)Poles / Vertices;

		public override string ToString() =>
			$"{Faces} faces, {QuadFraction:P1} quads, {Poles} poles ({PoleFraction:P1}), edge cv {EdgeCv:0.###}";
	}

	public sealed class Result
	{
		public PolyMesh Mesh;
		public Stats Stats;
		public int FromTriangles;
		public TimeSpan Time;

		public override string ToString() => $"{FromTriangles} triangles -> {Stats} in {Time.TotalSeconds:0.00} s";
	}

	/// <summary>
	/// Quads, poles and edge-length spread. A POLE is a vertex whose valence is not the regular
	/// one: four inside the surface, three on a border. EDGE CV is the standard deviation of the
	/// edge lengths over their mean — 0 is a perfect grid, and a triangulated sculpt sits near 0.5.
	/// </summary>
	public static Stats Measure( PolyMesh mesh )
	{
		var edges = mesh.BuildEdgeFaces();
		var valence = new int[mesh.VertexCount];
		var border = new bool[mesh.VertexCount];
		double sum = 0, sumSq = 0;

		foreach ( var (key, faces) in edges )
		{
			valence[key.A]++;
			valence[key.B]++;

			if ( faces.Count == 1 )
				border[key.A] = border[key.B] = true;

			var length = (double)(mesh.Positions[key.A] - mesh.Positions[key.B]).Length;
			sum += length;
			sumSq += length * length;
		}

		var used = 0;
		var poles = 0;

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			if ( valence[v] == 0 )
				continue;

			used++;

			if ( valence[v] != (border[v] ? 3 : 4) )
				poles++;
		}

		var count = Math.Max( 1, edges.Count );
		var mean = sum / count;
		var cv = mean > 0 ? Math.Sqrt( Math.Max( 0, sumSq / count - mean * mean ) ) / mean : 0;

		return new Stats( mesh.FaceCount, mesh.Faces.Count( f => f.Count == 4 ), used, poles, (float)mean, (float)cv );
	}

	public static Result Run( PolyMesh source, Options options = null )
	{
		if ( source is null )
			throw new ArgumentNullException( nameof( source ) );

		options ??= new Options();
		var clock = Stopwatch.StartNew();
		var from = Decimate.TriangleCount( source );

		var (min, max) = Bounds( source.Positions );
		var diagonal = MathF.Max( (max - min).Length, 1e-6f );

		var input = MeshWeld.Weld( source, diagonal * 1e-6f );
		var faces = Math.Max( 8, options.TargetFaces );

		if ( options.SymmetryX )
		{
			input = MeshWeld.Weld( MeshClip.Keep( input, Vec3.Zero, new Vec3( 1, 0, 0 ), diagonal * 1e-6f ), diagonal * 1e-6f );
			faces = Math.Max( 4, faces / 2 );
		}

		var mesh = input.FaceCount == 0
			? new PolyMesh()
			: Solve( input, faces, options, options.SymmetryX ? diagonal * 1e-5f : -1f );

		if ( options.SymmetryX && mesh.FaceCount > 0 )
			mesh = MeshModifiers.MirrorX( mesh, diagonal * 1e-5f );

		clock.Stop();

		return new Result { Mesh = mesh, Stats = Measure( mesh ), FromTriangles = from, Time = clock.Elapsed };
	}

	// --- the pipeline ------------------------------------------------------------------------

	/// <summary>One remesh of one (possibly half) surface. <paramref name="seam"/> &gt;= 0 means
	/// the x = 0 border is a mirror seam and its vertices are held on the plane.</summary>
	static PolyMesh Solve( PolyMesh input, int targetFaces, Options options, float seam )
	{
		var tris = Triangulate( input );
		var positions = input.Positions.ToArray();
		var area = TotalArea( positions, tris );

		if ( area <= 0f )
			return new PolyMesh();

		// Solved at TWICE the final edge length, because the one subdivision at the end halves it:
		// a quarter of the faces now is all of them afterwards.
		var scale = MathF.Sqrt( 4f * area / targetFaces );

		// THE INPUT HAS TO BE FINER THAN THE LATTICE. A vertex carries one lattice point, so a
		// surface sampled more coarsely than the output has places with no vertex to say where a
		// lattice point is. Splitting every triangle in four until edges are under half a lattice
		// step costs nothing on a dense import, which never needs it, and is what lets a decimated
		// mesh go in at all.
		while ( MeanEdge( positions, tris ) > scale * 0.4f && tris.Length / 3 < 2_000_000 )
			(positions, tris) = Split( positions, tris );

		var reference = new PolyMesh( positions, Enumerable.Range( 0, tris.Length / 3 )
			.Select( t => new Face( new[] { tris[t * 3], tris[t * 3 + 1], tris[t * 3 + 2] } ) ) );
		var tree = MeshBVH.Build( reference );

		var fine = Level.FromTriangles( positions, tris, seam );
		var levels = new List<Level> { fine };

		while ( levels[^1].Count > 64 )
		{
			var coarse = levels[^1].Coarsen();

			if ( coarse.Count > levels[^1].Count * 0.9f )
				break;

			levels.Add( coarse );
		}

		foreach ( var level in levels )
			level.Colour();

		Curvature( levels, scale, options.CurvatureAlign );
		SolveOrientation( levels );
		SolvePosition( levels, scale );

		var extracted = Extract( fine, scale, seam );
		Cleanup( extracted );

		var quads = Subdivide( extracted );
		Relax( quads, reference, tree, scale, options.RelaxPasses, seam );

		return quads.ToMesh();
	}

	// --- input -------------------------------------------------------------------------------

	static (Vec3 Min, Vec3 Max) Bounds( IReadOnlyList<Vec3> points )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in points )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return points.Count == 0 ? (Vec3.Zero, Vec3.Zero) : (min, max);
	}

	static int[] Triangulate( PolyMesh mesh )
	{
		var tris = new List<int>( mesh.FaceCount * 6 );

		foreach ( var f in mesh.Faces )
		{
			for ( var i = 1; i + 1 < f.Count; i++ )
			{
				var a = f.Indices[0];
				var b = f.Indices[i];
				var c = f.Indices[i + 1];

				if ( a == b || b == c || a == c )
					continue;

				tris.Add( a );
				tris.Add( b );
				tris.Add( c );
			}
		}

		return tris.ToArray();
	}

	static float TotalArea( Vec3[] v, int[] tris )
	{
		double sum = 0;

		for ( var t = 0; t < tris.Length; t += 3 )
			sum += Vec3.Cross( v[tris[t + 1]] - v[tris[t]], v[tris[t + 2]] - v[tris[t]] ).Length * 0.5;

		return (float)sum;
	}

	static float MeanEdge( Vec3[] v, int[] tris )
	{
		if ( tris.Length == 0 )
			return 0f;

		double sum = 0;

		for ( var t = 0; t < tris.Length; t += 3 )
		{
			sum += (v[tris[t]] - v[tris[t + 1]]).Length;
			sum += (v[tris[t + 1]] - v[tris[t + 2]]).Length;
			sum += (v[tris[t + 2]] - v[tris[t]]).Length;
		}

		return (float)(sum / tris.Length);
	}

	/// <summary>Every triangle into four at its edge midpoints. Flat, so the surface is exactly
	/// the one that went in.</summary>
	static (Vec3[], int[]) Split( Vec3[] v, int[] tris )
	{
		var positions = new List<Vec3>( v );
		var mids = new Dictionary<EdgeKey, int>( tris.Length );
		var result = new int[tris.Length * 4];

		int Mid( int a, int b )
		{
			var key = new EdgeKey( a, b );

			if ( !mids.TryGetValue( key, out var m ) )
			{
				m = positions.Count;
				positions.Add( (v[a] + v[b]) * 0.5f );
				mids[key] = m;
			}

			return m;
		}

		for ( var t = 0; t < tris.Length; t += 3 )
		{
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			int ab = Mid( a, b ), bc = Mid( b, c ), ca = Mid( c, a );
			var o = t * 4;

			result[o] = a; result[o + 1] = ab; result[o + 2] = ca;
			result[o + 3] = ab; result[o + 4] = b; result[o + 5] = bc;
			result[o + 6] = ca; result[o + 7] = bc; result[o + 8] = c;
			result[o + 9] = ab; result[o + 10] = bc; result[o + 11] = ca;
		}

		return (positions.ToArray(), result);
	}

	// --- the hierarchy -----------------------------------------------------------------------

	/// <summary>One level of the multiresolution graph the fields are solved on.</summary>
	sealed class Level
	{
		public int Count;
		public Vec3[] V, N;
		public float[] A;

		// Adjacency, compressed: the neighbours of i are Adj[Start[i] .. Start[i + 1]).
		public int[] Start, Adj;

		/// <summary>Into the next coarser level. Null on the coarsest.</summary>
		public int[] Parent;

		/// <summary>Into the next finer level; the second is -1 for a vertex that had no
		/// partner. Null on the finest.</summary>
		public int[] Child0, Child1;

		/// <summary>Border tangent, or zero away from a border. A border vertex's orientation is
		/// held along it rather than solved.</summary>
		public Vec3[] Border;

		/// <summary>Held on the mirror plane.</summary>
		public bool[] Seam;

		/// <summary>Vertices grouped so no two in a group are neighbours: each group is smoothed
		/// in parallel and the result is the same every run.</summary>
		public int[][] Phases;

		public Vec3[] Q, O;

		/// <summary>Principal curvature direction and how much it should count, 0..1.</summary>
		public Vec3[] Dir;
		public float[] Pull;

		// The curvature tensor, summed over the vertices this one stands for.
		public Sym[] T;

		public static Level FromTriangles( Vec3[] v, int[] tris, float seam )
		{
			var n = v.Length;
			var level = new Level { Count = n, V = v, N = new Vec3[n], A = new float[n] };
			var pairs = new List<long>( tris.Length * 2 );
			var edgeUse = new Dictionary<EdgeKey, int>( tris.Length );

			for ( var t = 0; t < tris.Length; t += 3 )
			{
				int a = tris[t], b = tris[t + 1], c = tris[t + 2];
				var cross = Vec3.Cross( v[b] - v[a], v[c] - v[a] );
				var third = cross.Length / 6f;

				level.N[a] += cross;
				level.N[b] += cross;
				level.N[c] += cross;
				level.A[a] += third;
				level.A[b] += third;
				level.A[c] += third;

				for ( var k = 0; k < 3; k++ )
				{
					var i = tris[t + k];
					var j = tris[t + (k + 1) % 3];
					pairs.Add( ((long)i << 32) | (uint)j );
					pairs.Add( ((long)j << 32) | (uint)i );

					var key = new EdgeKey( i, j );
					edgeUse[key] = edgeUse.TryGetValue( key, out var used ) ? used + 1 : 1;
				}
			}

			for ( var i = 0; i < n; i++ )
				level.N[i] = level.N[i].Normal;

			(level.Start, level.Adj) = Compress( n, pairs );

			level.Border = new Vec3[n];
			level.Seam = new bool[n];

			foreach ( var (key, used) in edgeUse )
			{
				if ( used != 1 )
					continue;

				// Summed with a consistent sign so the two border edges at a vertex add up rather
				// than cancel.
				var d = v[key.B] - v[key.A];

				if ( Vec3.Dot( d, level.Border[key.A] ) < 0 ) level.Border[key.A] -= d; else level.Border[key.A] += d;
				if ( Vec3.Dot( d, level.Border[key.B] ) < 0 ) level.Border[key.B] -= d; else level.Border[key.B] += d;
			}

			for ( var i = 0; i < n; i++ )
			{
				var b = level.Border[i];
				level.Border[i] = (b - level.N[i] * Vec3.Dot( level.N[i], b )).Normal;
				level.Seam[i] = seam >= 0f && MathF.Abs( v[i].x ) <= seam && level.Border[i].LengthSquared > 0f;
			}

			return level;
		}

		static (int[], int[]) Compress( int n, List<long> pairs )
		{
			pairs.Sort();
			var start = new int[n + 1];
			var adj = new List<int>( pairs.Count / 2 );
			var last = -1L;

			foreach ( var p in pairs )
			{
				if ( p == last )
					continue;

				last = p;
				var i = (int)(p >> 32);
				var j = (int)(p & 0xffffffff);

				if ( i == j )
					continue;

				start[i + 1]++;
				adj.Add( j );
			}

			for ( var i = 0; i < n; i++ )
				start[i + 1] += start[i];

			return (start, adj.ToArray());
		}

		/// <summary>
		/// Pair each vertex with its best neighbour — similar normal, similar area — and merge the
		/// pairs. Instant Meshes' rule: the normal term keeps creases from being averaged across,
		/// and the area term keeps the clusters about the same size, which is what keeps the
		/// coarse levels a fair picture of the fine one.
		/// </summary>
		public Level Coarsen()
		{
			var scores = new List<float>();
			var edges = new List<long>();

			for ( var i = 0; i < Count; i++ )
			{
				for ( var k = Start[i]; k < Start[i + 1]; k++ )
				{
					var j = Adj[k];

					if ( j <= i )
						continue;

					var ratio = A[i] > A[j] ? A[j] / MathF.Max( A[i], 1e-20f ) : A[i] / MathF.Max( A[j], 1e-20f );
					scores.Add( -Vec3.Dot( N[i], N[j] ) * ratio );
					edges.Add( ((long)i << 32) | (uint)j );
				}
			}

			var keys = scores.ToArray();
			var order = edges.ToArray();
			Array.Sort( keys, order );

			var parent = new int[Count];
			Array.Fill( parent, -1 );
			var child0 = new List<int>();
			var child1 = new List<int>();

			foreach ( var e in order )
			{
				var i = (int)(e >> 32);
				var j = (int)(e & 0xffffffff);

				if ( parent[i] >= 0 || parent[j] >= 0 )
					continue;

				parent[i] = parent[j] = child0.Count;
				child0.Add( i );
				child1.Add( j );
			}

			for ( var i = 0; i < Count; i++ )
			{
				if ( parent[i] >= 0 )
					continue;

				parent[i] = child0.Count;
				child0.Add( i );
				child1.Add( -1 );
			}

			var m = child0.Count;
			var c = new Level
			{
				Count = m,
				V = new Vec3[m],
				N = new Vec3[m],
				A = new float[m],
				Border = new Vec3[m],
				Seam = new bool[m],
				Child0 = child0.ToArray(),
				Child1 = child1.ToArray(),
			};

			for ( var p = 0; p < m; p++ )
			{
				var a = c.Child0[p];
				var b = c.Child1[p];

				if ( b < 0 )
				{
					c.V[p] = V[a];
					c.N[p] = N[a];
					c.A[p] = A[a];
					c.Border[p] = Border[a];
					c.Seam[p] = Seam[a];
					continue;
				}

				var w = A[a] + A[b];
				c.A[p] = w;
				c.V[p] = w > 0 ? (V[a] * A[a] + V[b] * A[b]) / w : (V[a] + V[b]) * 0.5f;
				c.N[p] = (N[a] * A[a] + N[b] * A[b]).Normal;

				if ( c.N[p].LengthSquared == 0f )
					c.N[p] = N[a];

				// A seam vertex outranks a plain border one: the seam is the constraint that has to
				// come out exact.
				var pick = Seam[a] || !Seam[b] && Border[a].LengthSquared > 0f ? a : b;
				c.Border[p] = Border[pick].LengthSquared > 0f ? Tangent( Border[pick], c.N[p] ) : Vec3.Zero;
				c.Seam[p] = Seam[a] || Seam[b];
			}

			var pairs = new List<long>( Adj.Length );

			for ( var i = 0; i < Count; i++ )
			{
				for ( var k = Start[i]; k < Start[i + 1]; k++ )
				{
					var pi = parent[i];
					var pj = parent[Adj[k]];

					if ( pi != pj )
						pairs.Add( ((long)pi << 32) | (uint)pj );
				}
			}

			(c.Start, c.Adj) = Compress( m, pairs );
			Parent = parent;
			return c;
		}

		/// <summary>Greedy graph colouring, so a Gauss-Seidel pass can run a colour at a time in
		/// parallel and still give the same answer every run.</summary>
		public void Colour()
		{
			var colour = new int[Count];
			var groups = new List<List<int>>();
			var taken = new List<bool>();

			for ( var i = 0; i < Count; i++ )
			{
				taken.Clear();

				for ( var k = Start[i]; k < Start[i + 1]; k++ )
				{
					var j = Adj[k];

					if ( j >= i )
						continue;

					while ( taken.Count <= colour[j] )
						taken.Add( false );

					taken[colour[j]] = true;
				}

				var pick = 0;

				while ( pick < taken.Count && taken[pick] )
					pick++;

				colour[i] = pick;

				while ( groups.Count <= pick )
					groups.Add( new List<int>() );

				groups[pick].Add( i );
			}

			Phases = groups.Select( g => g.ToArray() ).ToArray();
		}

		public void ForEachPhase( Action<int> body )
		{
			foreach ( var phase in Phases )
			{
				if ( phase.Length < 256 )
				{
					foreach ( var i in phase )
						body( i );
				}
				else
				{
					Parallel.For( 0, phase.Length, k => body( phase[k] ) );
				}
			}
		}
	}

	/// <summary>A symmetric 3x3 matrix, enough for a curvature tensor.</summary>
	struct Sym
	{
		public float XX, XY, XZ, YY, YZ, ZZ;

		public static Sym operator +( Sym a, Sym b ) => new()
		{
			XX = a.XX + b.XX, XY = a.XY + b.XY, XZ = a.XZ + b.XZ,
			YY = a.YY + b.YY, YZ = a.YZ + b.YZ, ZZ = a.ZZ + b.ZZ,
		};

		public static Sym Outer( Vec3 t, float w ) => new()
		{
			XX = t.x * t.x * w, XY = t.x * t.y * w, XZ = t.x * t.z * w,
			YY = t.y * t.y * w, YZ = t.y * t.z * w, ZZ = t.z * t.z * w,
		};

		public float Quad( Vec3 a, Vec3 b ) =>
			a.x * (XX * b.x + XY * b.y + XZ * b.z) +
			a.y * (XY * b.x + YY * b.y + YZ * b.z) +
			a.z * (XZ * b.x + YZ * b.y + ZZ * b.z);
	}

	static Vec3 Perpendicular( Vec3 n )
	{
		var axis = MathF.Abs( n.x ) < 0.6f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		return Vec3.Cross( n, axis ).Normal;
	}

	static Vec3 Tangent( Vec3 d, Vec3 n )
	{
		var t = (d - n * Vec3.Dot( n, d )).Normal;
		return t.LengthSquared > 0f ? t : Perpendicular( n );
	}

	// --- curvature ---------------------------------------------------------------------------

	/// <summary>
	/// Where the orientation should point, and how much that should count.
	///
	/// Taubin's tensor at each fine vertex — every edge's normal curvature laid along its tangent
	/// direction — then SUMMED UP THE HIERARCHY rather than smoothed. A SurfaceNets surface is
	/// stair-stepped at the voxel and its per-vertex curvature is mostly noise; summed over a
	/// cluster about one lattice step across, the noise cancels and the shape's own curvature is
	/// what is left. That level's answer is then handed back down to every finer vertex it stands
	/// for, so the fine levels lean on the same smooth direction the coarse ones did.
	///
	/// THE PULL is anisotropy times how curved it is at the lattice's scale. A sphere is curved and
	/// has no preferred direction; a nearly flat patch has one but it means nothing. Only a
	/// surface that is curved one way and not the other should be allowed to steer the field.
	/// </summary>
	static void Curvature( List<Level> levels, float scale, float strength )
	{
		var fine = levels[0];
		fine.T = new Sym[fine.Count];

		Parallel.For( 0, fine.Count, i =>
		{
			var sum = default( Sym );
			var n = fine.N[i];
			var count = 0;

			for ( var k = fine.Start[i]; k < fine.Start[i + 1]; k++ )
			{
				var e = fine.V[fine.Adj[k]] - fine.V[i];
				var lsq = e.LengthSquared;

				if ( lsq <= 0f )
					continue;

				var kappa = -2f * Vec3.Dot( n, e ) / lsq;
				var t = (e - n * Vec3.Dot( n, e )).Normal;
				sum += Sym.Outer( t, kappa );
				count++;
			}

			var w = count > 0 ? fine.A[i] / count : 0f;
			fine.T[i] = new Sym
			{
				XX = sum.XX * w, XY = sum.XY * w, XZ = sum.XZ * w,
				YY = sum.YY * w, YZ = sum.YZ * w, ZZ = sum.ZZ * w,
			};
		} );

		for ( var l = 1; l < levels.Count; l++ )
		{
			var c = levels[l];
			var f = levels[l - 1];
			c.T = new Sym[c.Count];

			for ( var p = 0; p < c.Count; p++ )
				c.T[p] = c.Child1[p] < 0 ? f.T[c.Child0[p]] : f.T[c.Child0[p]] + f.T[c.Child1[p]];
		}

		// The level whose clusters are about one lattice cell.
		var cell = 0;

		while ( cell + 1 < levels.Count && levels[cell].A.Average() < scale * scale * 0.5f )
			cell++;

		for ( var l = 0; l < levels.Count; l++ )
		{
			var level = levels[l];
			level.Dir = new Vec3[level.Count];
			level.Pull = new float[level.Count];
		}

		for ( var l = levels.Count - 1; l >= 0; l-- )
		{
			var level = levels[l];

			for ( var i = 0; i < level.Count; i++ )
			{
				if ( l < cell )
				{
					// Inherited: the parent's direction, laid in this vertex's tangent plane.
					var p = level.Parent[i];
					level.Dir[i] = Tangent( levels[l + 1].Dir[p], level.N[i] );
					level.Pull[i] = levels[l + 1].Pull[p];
					continue;
				}

				var n = level.N[i];
				var b1 = Perpendicular( n );
				var b2 = Vec3.Cross( n, b1 );
				var t = level.T[i];
				var area = MathF.Max( level.A[i], 1e-20f );
				var m11 = t.Quad( b1, b1 ) / area;
				var m12 = t.Quad( b1, b2 ) / area;
				var m22 = t.Quad( b2, b2 ) / area;
				var theta = 0.5f * MathF.Atan2( 2f * m12, m11 - m22 );
				var diff = MathF.Sqrt( (m11 - m22) * (m11 - m22) + 4f * m12 * m12 );
				var mid = 0.5f * (m11 + m22);
				var magnitude = MathF.Abs( mid + 0.5f * diff ) + MathF.Abs( mid - 0.5f * diff );
				var aniso = magnitude > 1e-12f ? diff / magnitude : 0f;
				var curved = Math.Clamp( magnitude * scale / 0.15f, 0f, 1f );

				level.Dir[i] = (b1 * MathF.Cos( theta ) + b2 * MathF.Sin( theta )).Normal;
				level.Pull[i] = strength * aniso * aniso * curved;
			}
		}
	}

	// --- the orientation field ---------------------------------------------------------------

	/// <summary>
	/// Of the four ways q1 can be turned by quarter turns, the one closest to q0 — and q0 itself.
	/// Comparing in 3D rather than in a shared tangent plane is the "extrinsic" in Instant Meshes:
	/// no parallel transport to compute, and it behaves the same on any surface.
	/// </summary>
	static (Vec3, Vec3) CompatOrientation( Vec3 q0, Vec3 n0, Vec3 q1, Vec3 n1 )
	{
		var a1 = Vec3.Cross( n0, q0 );
		var b1 = Vec3.Cross( n1, q1 );

		var s00 = Vec3.Dot( q0, q1 );
		var s01 = Vec3.Dot( q0, b1 );
		var s10 = Vec3.Dot( a1, q1 );
		var s11 = Vec3.Dot( a1, b1 );

		var best = MathF.Abs( s00 );
		Vec3 a = q0, b = q1;
		var dot = s00;

		if ( MathF.Abs( s01 ) > best ) { best = MathF.Abs( s01 ); a = q0; b = b1; dot = s01; }
		if ( MathF.Abs( s10 ) > best ) { best = MathF.Abs( s10 ); a = a1; b = q1; dot = s10; }
		if ( MathF.Abs( s11 ) > best ) { a = a1; b = b1; dot = s11; }

		return (a, dot < 0f ? -b : b);
	}

	static void SolveOrientation( List<Level> levels )
	{
		for ( var l = levels.Count - 1; l >= 0; l-- )
		{
			var level = levels[l];
			level.Q = new Vec3[level.Count];

			for ( var i = 0; i < level.Count; i++ )
			{
				if ( level.Border[i].LengthSquared > 0f )
					level.Q[i] = level.Border[i];
				else if ( l == levels.Count - 1 )
					level.Q[i] = level.Pull[i] > 0f ? level.Dir[i] : Perpendicular( level.N[i] );
				else
					level.Q[i] = Tangent( levels[l + 1].Q[level.Parent[i]], level.N[i] );
			}

			// The coarsest level starts from nothing and gets all the passes it wants; every other
			// level starts from its parent's answer. The finest is half of all the vertices and
			// is already nearly settled when it arrives, so it gets the fewest.
			var passes = l == levels.Count - 1 ? 200 : l == 0 ? 12 : 30;

			for ( var pass = 0; pass < passes; pass++ )
				level.ForEachPhase( i => SmoothOrientation( level, i ) );
		}
	}

	static void SmoothOrientation( Level level, int i )
	{
		if ( level.Border[i].LengthSquared > 0f )
			return;

		var n = level.N[i];
		var sum = level.Q[i];
		var weight = 0f;

		for ( var k = level.Start[i]; k < level.Start[i + 1]; k++ )
		{
			var j = level.Adj[k];
			var (a, b) = CompatOrientation( sum, n, level.Q[j], level.N[j] );
			sum = a * weight + b;
			sum -= n * Vec3.Dot( n, sum );
			weight += 1f;
			sum = sum.Normal;
		}

		var pull = level.Pull[i] * weight;

		if ( pull > 0f )
		{
			var (a, b) = CompatOrientation( sum, n, level.Dir[i], n );
			sum = a * weight + b * pull;
			sum -= n * Vec3.Dot( n, sum );
		}

		var q = sum.Normal;
		level.Q[i] = q.LengthSquared > 0f ? q : Perpendicular( n );
	}

	// --- the position field ------------------------------------------------------------------

	/// <summary>The point that minimises its distance to both vertices while lying on both
	/// tangent planes — where two neighbours' lattices are compared. Instant Meshes' formula,
	/// with its small epsilon for the case of two parallel planes.</summary>
	static Vec3 Middle( Vec3 p0, Vec3 n0, Vec3 p1, Vec3 n1 )
	{
		var n0p0 = Vec3.Dot( n0, p0 );
		var n0p1 = Vec3.Dot( n0, p1 );
		var n1p0 = Vec3.Dot( n1, p0 );
		var n1p1 = Vec3.Dot( n1, p1 );
		var n0n1 = Vec3.Dot( n0, n1 );
		var denom = 1f / (1f - n0n1 * n0n1 + 1e-4f);
		var l0 = 2f * (n0p1 - n0p0 - n0n1 * (n1p0 - n1p1)) * denom;
		var l1 = 2f * (n1p0 - n1p1 - n0n1 * (n0p1 - n0p0)) * denom;
		return (p0 + p1) * 0.5f - (n0 * l0 + n1 * l1) * 0.25f;
	}

	static Vec3 Floor( Vec3 o, Vec3 q, Vec3 t, Vec3 p, float scale, float inv )
	{
		var d = p - o;
		return o + q * (MathF.Floor( Vec3.Dot( q, d ) * inv ) * scale) + t * (MathF.Floor( Vec3.Dot( t, d ) * inv ) * scale);
	}

	static Vec3 Round( Vec3 o, Vec3 q, Vec3 n, Vec3 p, float scale, float inv )
	{
		var t = Vec3.Cross( n, q );
		var d = p - o;
		return o + q * (MathF.Round( Vec3.Dot( q, d ) * inv ) * scale) + t * (MathF.Round( Vec3.Dot( t, d ) * inv ) * scale);
	}

	/// <summary>The lattice points of the two vertices' lattices that are closest to each other,
	/// near the point between them.</summary>
	static (Vec3, Vec3) CompatPosition( Vec3 p0, Vec3 n0, Vec3 q0, Vec3 o0,
		Vec3 p1, Vec3 n1, Vec3 q1, Vec3 o1, float scale, float inv )
	{
		var t0 = Vec3.Cross( n0, q0 );
		var t1 = Vec3.Cross( n1, q1 );
		var middle = Middle( p0, n0, p1, n1 );
		var f0 = Floor( o0, q0, t0, middle, scale, inv );
		var f1 = Floor( o1, q1, t1, middle, scale, inv );
		var best = float.MaxValue;
		Vec3 a = f0, b = f1;

		for ( var i = 0; i < 4; i++ )
		{
			var x0 = f0 + (q0 * (i & 1) + t0 * (i >> 1)) * scale;

			for ( var j = 0; j < 4; j++ )
			{
				var x1 = f1 + (q1 * (j & 1) + t1 * (j >> 1)) * scale;
				var cost = (x0 - x1).LengthSquared;

				if ( cost < best )
				{
					best = cost;
					a = x0;
					b = x1;
				}
			}
		}

		return (a, b);
	}

	/// <summary>The same comparison, answered as how many lattice steps vertex 1's point is from
	/// vertex 0's, in vertex 0's frame. (0,0) is the same output vertex; a single unit step is an
	/// output edge.</summary>
	static (int, int) Shift( Vec3 p0, Vec3 n0, Vec3 q0, Vec3 o0,
		Vec3 p1, Vec3 n1, Vec3 q1, Vec3 o1, float scale, float inv )
	{
		var t0 = Vec3.Cross( n0, q0 );
		var t1 = Vec3.Cross( n1, q1 );
		var middle = Middle( p0, n0, p1, n1 );
		var d0 = middle - o0;
		var d1 = middle - o1;
		var i0 = (int)MathF.Floor( Vec3.Dot( q0, d0 ) * inv );
		var j0 = (int)MathF.Floor( Vec3.Dot( t0, d0 ) * inv );
		var i1 = (int)MathF.Floor( Vec3.Dot( q1, d1 ) * inv );
		var j1 = (int)MathF.Floor( Vec3.Dot( t1, d1 ) * inv );
		var best = float.MaxValue;
		var result = (0, 0);

		for ( var a = 0; a < 4; a++ )
		{
			int ia = i0 + (a & 1), ja = j0 + (a >> 1);
			var x0 = o0 + (q0 * ia + t0 * ja) * scale;

			for ( var b = 0; b < 4; b++ )
			{
				int ib = i1 + (b & 1), jb = j1 + (b >> 1);
				var x1 = o1 + (q1 * ib + t1 * jb) * scale;
				var cost = (x0 - x1).LengthSquared;

				if ( cost < best )
				{
					best = cost;
					result = (ia - ib, ja - jb);
				}
			}
		}

		return result;
	}

	static void SolvePosition( List<Level> levels, float scale )
	{
		var inv = 1f / scale;

		for ( var l = levels.Count - 1; l >= 0; l-- )
		{
			var level = levels[l];
			level.O = new Vec3[level.Count];

			for ( var i = 0; i < level.Count; i++ )
			{
				var o = l == levels.Count - 1 ? level.V[i] : levels[l + 1].O[level.Parent[i]];
				o -= level.N[i] * Vec3.Dot( level.N[i], o - level.V[i] );
				level.O[i] = Constrain( level, i, Round( o, level.Q[i], level.N[i], level.V[i], scale, inv ) );
			}

			var passes = l == levels.Count - 1 ? 200 : l == 0 ? 12 : 30;

			for ( var pass = 0; pass < passes; pass++ )
				level.ForEachPhase( i => SmoothPosition( level, i, scale, inv ) );
		}
	}

	static void SmoothPosition( Level level, int i, float scale, float inv )
	{
		var n = level.N[i];
		var v = level.V[i];
		var q = level.Q[i];
		var sum = level.O[i];
		var weight = 0f;

		for ( var k = level.Start[i]; k < level.Start[i + 1]; k++ )
		{
			var j = level.Adj[k];
			var (a, b) = CompatPosition( v, n, q, sum, level.V[j], level.N[j], level.Q[j], level.O[j], scale, inv );
			sum = (a * weight + b) / (weight + 1f);
			weight += 1f;
			sum -= n * Vec3.Dot( n, sum - v );
		}

		level.O[i] = Constrain( level, i, Round( sum, q, n, v, scale, inv ) );
	}

	/// <summary>A border vertex's lattice has a line along the border: its point is slid across
	/// the border direction until it lies on the line through the vertex.</summary>
	static Vec3 Constrain( Level level, int i, Vec3 o )
	{
		if ( level.Border[i].LengthSquared == 0f )
			return o;

		var t = Vec3.Cross( level.N[i], level.Q[i] );
		return o - t * Vec3.Dot( t, o - level.V[i] );
	}

	// --- extraction --------------------------------------------------------------------------

	/// <summary>A small editable polygon mesh: the extraction's output and the cleanup's input.</summary>
	sealed class Poly
	{
		public List<Vec3> P = new();
		public List<Vec3> N = new();
		public List<bool> Seam = new();
		public List<bool> Border = new();
		public List<int[]> Faces = new();

		public PolyMesh ToMesh()
		{
			var used = new int[P.Count];
			Array.Fill( used, -1 );
			var mesh = new PolyMesh();

			foreach ( var f in Faces )
			{
				var idx = new int[f.Length];

				for ( var k = 0; k < f.Length; k++ )
				{
					if ( used[f[k]] < 0 )
						used[f[k]] = mesh.AddVertex( P[f[k]] );

					idx[k] = used[f[k]];
				}

				mesh.AddFace( idx );
			}

			return mesh;
		}
	}

	static int Find( int[] parent, int i )
	{
		while ( parent[i] != i )
		{
			parent[i] = parent[parent[i]];
			i = parent[i];
		}

		return i;
	}

	/// <summary>
	/// Read the mesh out of the two fields. Every fine edge is asked how far apart its two lattice
	/// points are: no step and the two vertices are one output vertex, one step and they are an
	/// output edge, anything else and the edge says nothing. The output graph is then cleaned of
	/// the edges a noisy field adds — dangling ends, and pairs of edges leaving a vertex in nearly
	/// the same direction — and its faces are walked by turning the same way at every vertex.
	/// </summary>
	static Poly Extract( Level fine, float scale, float seam )
	{
		var inv = 1f / scale;
		var n = fine.Count;
		var parent = Enumerable.Range( 0, n ).ToArray();
		var links = new List<(int, int)>();

		for ( var i = 0; i < n; i++ )
		{
			for ( var k = fine.Start[i]; k < fine.Start[i + 1]; k++ )
			{
				var j = fine.Adj[k];

				if ( j <= i )
					continue;

				var (qi, qj) = CompatOrientation( fine.Q[i], fine.N[i], fine.Q[j], fine.N[j] );
				var (dx, dy) = Shift( fine.V[i], fine.N[i], qi, fine.O[i], fine.V[j], fine.N[j], qj, fine.O[j], scale, inv );
				var steps = Math.Abs( dx ) + Math.Abs( dy );

				if ( steps == 0 )
				{
					var a = Find( parent, i );
					var b = Find( parent, j );

					if ( a != b )
						parent[Math.Max( a, b )] = Math.Min( a, b );
				}
				else if ( steps == 1 )
				{
					links.Add( (i, j) );
				}
			}
		}

		// Clusters, numbered in order of their lowest member so the result is the same every run.
		var cluster = new int[n];
		Array.Fill( cluster, -1 );
		var poly = new Poly();
		var weights = new List<float>();

		for ( var i = 0; i < n; i++ )
		{
			var root = Find( parent, i );

			if ( cluster[root] < 0 )
			{
				cluster[root] = poly.P.Count;
				poly.P.Add( Vec3.Zero );
				poly.N.Add( Vec3.Zero );
				poly.Seam.Add( false );
				poly.Border.Add( false );
				weights.Add( 0f );
			}

			var c = cluster[i] = cluster[root];
			var w = MathF.Max( fine.A[i], 1e-20f );
			poly.P[c] += fine.O[i] * w;
			poly.N[c] += fine.N[i] * w;
			weights[c] += w;
			poly.Seam[c] |= fine.Seam[i];
			poly.Border[c] |= fine.Border[i].LengthSquared > 0f;
		}

		for ( var c = 0; c < poly.P.Count; c++ )
		{
			poly.P[c] /= weights[c];
			poly.N[c] = poly.N[c].Normal;

			if ( poly.Seam[c] )
				poly.P[c] = new Vec3( 0f, poly.P[c].y, poly.P[c].z );
		}

		var count = poly.P.Count;
		var adjacency = new HashSet<int>[count];

		for ( var c = 0; c < count; c++ )
			adjacency[c] = new HashSet<int>();

		foreach ( var (i, j) in links )
		{
			var a = cluster[i];
			var b = cluster[j];

			if ( a == b )
				continue;

			adjacency[a].Add( b );
			adjacency[b].Add( a );
		}

		CleanGraph( poly, adjacency );
		poly.Faces = TraceFaces( poly, adjacency );

		// THE BORDER COMES BACK AS ONE BIG FACE — the walk goes round the outside of an open
		// surface the same way it goes round a quad. A face whose corners are all on the border
		// spans the opening, not the surface, so it goes.
		poly.Faces.RemoveAll( f => f.All( c => poly.Border[c] ) );

		return poly;
	}

	static float Angle( Vec3 d, Vec3 b1, Vec3 b2 ) => MathF.Atan2( Vec3.Dot( d, b2 ), Vec3.Dot( d, b1 ) );

	static void CleanGraph( Poly poly, HashSet<int>[] adjacency )
	{
		void Prune()
		{
			var queue = new Queue<int>( Enumerable.Range( 0, adjacency.Length ).Where( c => adjacency[c].Count == 1 ) );

			while ( queue.Count > 0 )
			{
				var c = queue.Dequeue();

				if ( adjacency[c].Count != 1 )
					continue;

				var other = adjacency[c].First();
				adjacency[c].Clear();
				adjacency[other].Remove( c );

				if ( adjacency[other].Count == 1 )
					queue.Enqueue( other );
			}
		}

		Prune();

		// Two edges leaving a vertex less than 30 degrees apart are one real edge and one the
		// field invented. The longer one is the invention: the real neighbour is a lattice step
		// away and the false one is a diagonal or worse.
		for ( var round = 0; round < 2; round++ )
		{
			var drop = new List<(int, int)>();

			for ( var c = 0; c < adjacency.Length; c++ )
			{
				if ( adjacency[c].Count < 3 )
					continue;

				var n = poly.N[c];
				var b1 = Perpendicular( n );
				var b2 = Vec3.Cross( n, b1 );
				var around = adjacency[c].OrderBy( o => Angle( poly.P[o] - poly.P[c], b1, b2 ) ).ToArray();

				for ( var k = 0; k < around.Length; k++ )
				{
					var a = around[k];
					var b = around[(k + 1) % around.Length];
					var gap = Angle( poly.P[b] - poly.P[c], b1, b2 ) - Angle( poly.P[a] - poly.P[c], b1, b2 );

					if ( gap < 0f )
						gap += MathF.PI * 2f;

					if ( gap < MathF.PI / 6f )
					{
						var far = (poly.P[a] - poly.P[c]).LengthSquared > (poly.P[b] - poly.P[c]).LengthSquared ? a : b;
						drop.Add( (c, far) );
					}
				}
			}

			if ( drop.Count == 0 )
				break;

			foreach ( var (a, b) in drop )
			{
				adjacency[a].Remove( b );
				adjacency[b].Remove( a );
			}

			Prune();
		}
	}

	/// <summary>Walk every face of the graph: arrive at a vertex, leave along the next edge
	/// clockwise from the one you came in on. With each vertex's edges sorted counter-clockwise
	/// about its normal, that keeps the face on the left, so every face comes out wound outward.</summary>
	static List<int[]> TraceFaces( Poly poly, HashSet<int>[] adjacency )
	{
		var count = adjacency.Length;
		var around = new int[count][];
		var used = new bool[count][];

		for ( var c = 0; c < count; c++ )
		{
			var n = poly.N[c];
			var b1 = Perpendicular( n );
			var b2 = Vec3.Cross( n, b1 );
			around[c] = adjacency[c].OrderBy( o => Angle( poly.P[o] - poly.P[c], b1, b2 ) ).ToArray();
			used[c] = new bool[around[c].Length];
		}

		var faces = new List<int[]>();
		var face = new List<int>();

		for ( var c = 0; c < count; c++ )
		{
			for ( var k = 0; k < around[c].Length; k++ )
			{
				if ( used[c][k] )
					continue;

				face.Clear();
				int u = c, slot = k;

				while ( !used[u][slot] && face.Count < 4096 )
				{
					used[u][slot] = true;
					face.Add( u );

					var w = around[u][slot];
					var back = Array.IndexOf( around[w], u );
					slot = (back - 1 + around[w].Length) % around[w].Length;
					u = w;
				}

				if ( face.Count >= 3 )
					faces.AddRange( Simple( face ) );
			}
		}

		return faces;
	}

	/// <summary>A walk that passes through a vertex twice — round a bridge edge — split into
	/// the simple loops it is made of.</summary>
	static IEnumerable<int[]> Simple( List<int> walk )
	{
		var stack = new List<int>();
		var seen = new Dictionary<int, int>();

		foreach ( var v in walk )
		{
			if ( seen.TryGetValue( v, out var at ) )
			{
				var loop = stack.GetRange( at, stack.Count - at );

				if ( loop.Count >= 3 )
					yield return loop.ToArray();

				for ( var k = at + 1; k < stack.Count; k++ )
					seen.Remove( stack[k] );

				stack.RemoveRange( at + 1, stack.Count - at - 1 );
				continue;
			}

			seen[v] = stack.Count;
			stack.Add( v );
		}

		if ( stack.Count >= 3 )
			yield return stack.ToArray();
	}

	// --- cleanup -----------------------------------------------------------------------------

	/// <summary>
	/// From quad-dominant to as close to all-quads as the topology allows, before the subdivision
	/// makes it all-quads for certain.
	///
	/// In order: dissolve the valence-2 vertices a face walk leaves where two faces fold over each
	/// other, cut every face with more than four sides into quads (and one triangle when the count
	/// is odd), pair the triangles into quads — directly or across a straight strip of quads — and
	/// turn edges wherever that evens out the valences. Every triangle left over becomes one
	/// valence-3 pole after subdivision, and every pole here survives it, so this is where the pole
	/// count is decided.
	/// </summary>
	static void Cleanup( Poly poly )
	{
		DissolveValenceTwo( poly );
		SplitLarge( poly );
		PairTriangles( poly );
		DissolveValenceTwo( poly );
		SplitLarge( poly );
		RotateEdges( poly );
	}

	/// <summary>
	/// Turn the edge between two quads a step round the hexagon they make, wherever that brings
	/// the valences nearer four. The two endpoints lose an edge and two corners of the hexagon
	/// gain one, so a pair of valence-5 vertices facing a pair of valence-3 ones becomes four
	/// regular vertices in one move. Only moves that lower the total are taken, so it ends.
	/// </summary>
	static void RotateEdges( Poly poly )
	{
		for ( var round = 0; round < 10; round++ )
		{
			var owner = new Dictionary<(int, int), int>();
			var valence = new int[poly.P.Count];
			var edges = new HashSet<EdgeKey>();
			var border = new bool[poly.P.Count];

			for ( var f = 0; f < poly.Faces.Count; f++ )
			{
				var face = poly.Faces[f];

				for ( var k = 0; k < face.Length; k++ )
				{
					var a = face[k];
					var b = face[(k + 1) % face.Length];
					owner[(a, b)] = f;

					if ( edges.Add( new EdgeKey( a, b ) ) )
					{
						valence[a]++;
						valence[b]++;
					}
				}
			}

			foreach ( var (a, b) in owner.Keys )
			{
				if ( !owner.ContainsKey( (b, a) ) )
					border[a] = border[b] = true;
			}

			int Cost( int v, int change )
			{
				var off = valence[v] + change - (border[v] ? 3 : 4);
				return off * off;
			}

			var touched = new HashSet<int>();
			var changed = 0;

			for ( var f = 0; f < poly.Faces.Count; f++ )
			{
				var q1 = poly.Faces[f];

				if ( q1.Length != 4 || touched.Contains( f ) )
					continue;

				for ( var k = 0; k < 4; k++ )
				{
					var u = q1[k];
					var v = q1[(k + 1) % 4];

					if ( !owner.TryGetValue( (v, u), out var g ) || g == f || touched.Contains( g ) || poly.Faces[g].Length != 4 )
						continue;

					if ( border[u] || border[v] )
						continue;

					var q2 = poly.Faces[g];
					var i = Array.IndexOf( q2, v );

					// q1 runs u, v, a, b and q2 runs v, u, c, d: the hexagon is v, a, b, u, c, d.
					var ring = new[] { v, q1[(k + 2) % 4], q1[(k + 3) % 4], u, q2[(i + 2) % 4], q2[(i + 3) % 4] };

					if ( ring.Distinct().Count() != 6 )
						continue;

					var before = Cost( u, 0 ) + Cost( v, 0 );
					var best = 0;
					var pick = -1;

					for ( var s = 1; s <= 2; s++ )
					{
						int p = ring[s], q = ring[s + 3];

						if ( edges.Contains( new EdgeKey( p, q ) ) )
							continue;

						var gain = before + Cost( p, 0 ) + Cost( q, 0 ) - Cost( u, -1 ) - Cost( v, -1 ) - Cost( p, 1 ) - Cost( q, 1 );

						if ( gain > best )
						{
							best = gain;
							pick = s;
						}
					}

					if ( pick < 0 )
						continue;

					poly.Faces[f] = new[] { ring[pick], ring[pick + 1], ring[pick + 2], ring[pick + 3] };
					poly.Faces[g] = new[] { ring[pick + 3], ring[(pick + 4) % 6], ring[(pick + 5) % 6], ring[pick] };
					touched.Add( f );
					touched.Add( g );
					changed++;
					break;
				}
			}

			if ( changed == 0 )
				return;
		}
	}

	static void DissolveValenceTwo( Poly poly )
	{
		for ( var round = 0; round < 4; round++ )
		{
			var incident = new List<int>[poly.P.Count];
			var degree = new int[poly.P.Count];

			for ( var f = 0; f < poly.Faces.Count; f++ )
			{
				foreach ( var v in poly.Faces[f] )
					(incident[v] ??= new List<int>()).Add( f );
			}

			foreach ( var key in Edges( poly.Faces ) )
			{
				degree[key.A]++;
				degree[key.B]++;
			}

			var dead = new HashSet<int>();
			var changed = false;

			for ( var v = 0; v < poly.P.Count; v++ )
			{
				if ( degree[v] != 2 || incident[v] is not { Count: 2 } pair || poly.Border[v] )
					continue;

				int f = pair[0], g = pair[1];

				if ( dead.Contains( f ) || dead.Contains( g ) )
					continue;

				var merged = Merge( poly.Faces[f], poly.Faces[g], v );

				if ( merged is null )
					continue;

				poly.Faces[f] = merged;
				dead.Add( g );
				changed = true;
			}

			if ( !changed )
				return;

			poly.Faces = poly.Faces.Where( ( _, i ) => !dead.Contains( i ) ).ToList();
		}
	}

	/// <summary>Two faces that meet only at the two edges of vertex v, as one face without it.</summary>
	static int[] Merge( int[] f, int[] g, int v )
	{
		var fi = Array.IndexOf( f, v );
		var gi = Array.IndexOf( g, v );

		// f runs a -> v -> b, so g runs b -> v -> a.
		var b = f[(fi + 1) % f.Length];
		var a = f[(fi - 1 + f.Length) % f.Length];

		if ( g[(gi + 1) % g.Length] != a || g[(gi - 1 + g.Length) % g.Length] != b )
			return null;

		var result = new List<int>();

		for ( var k = 1; k < f.Length; k++ )
			result.Add( f[(fi + k) % f.Length] );

		for ( var k = 2; k < g.Length - 1; k++ )
			result.Add( g[(gi + k) % g.Length] );

		if ( result.Count < 3 || result.Distinct().Count() != result.Count )
			return null;

		return result.ToArray();
	}

	static IEnumerable<EdgeKey> Edges( List<int[]> faces )
	{
		var seen = new HashSet<EdgeKey>();

		foreach ( var f in faces )
		{
			for ( var k = 0; k < f.Length; k++ )
			{
				var key = new EdgeKey( f[k], f[(k + 1) % f.Length] );

				if ( seen.Add( key ) )
					yield return key;
			}
		}
	}

	static void SplitLarge( Poly poly )
	{
		var result = new List<int[]>( poly.Faces.Count );

		foreach ( var f in poly.Faces )
		{
			if ( f.Length <= 4 )
			{
				result.Add( f );
				continue;
			}

			if ( f.Length <= 6 )
			{
				// One diagonal: a pentagon into a quad and a triangle, a hexagon into two quads.
				// The shortest of the candidates, which is the one least likely to cut across
				// the face's bulge.
				var best = 0;
				var shortest = float.MaxValue;

				for ( var s = 0; s < f.Length; s++ )
				{
					var length = (poly.P[f[s]] - poly.P[f[(s + 3) % f.Length]]).LengthSquared;

					if ( length < shortest )
					{
						shortest = length;
						best = s;
					}
				}

				var quad = new int[4];
				var rest = new List<int>();

				for ( var k = 0; k < 4; k++ )
					quad[k] = f[(best + k) % f.Length];

				for ( var k = 3; k <= f.Length; k++ )
					rest.Add( f[(best + k) % f.Length] );

				result.Add( quad );
				result.Add( rest.ToArray() );
				continue;
			}

			// Seven or more: a new vertex in the middle and a quad to every other corner. An
			// even count closes up exactly; an odd one leaves the last wedge a triangle.
			var centre = Vec3.Zero;
			var normal = Vec3.Zero;

			foreach ( var c in f )
			{
				centre += poly.P[c];
				normal += poly.N[c];
			}

			var mid = poly.P.Count;
			poly.P.Add( centre / f.Length );
			poly.N.Add( normal.Normal );
			poly.Seam.Add( false );
			poly.Border.Add( false );

			var k0 = 0;

			for ( ; k0 + 2 < f.Length; k0 += 2 )
				result.Add( new[] { mid, f[k0], f[k0 + 1], f[k0 + 2] } );

			result.Add( k0 + 1 < f.Length
				? new[] { mid, f[k0], f[k0 + 1], f[0] }
				: new[] { mid, f[k0], f[0] } );
		}

		poly.Faces = result;
	}

	/// <summary>
	/// Pair triangles into quads, including across a strip of quads between them.
	///
	/// Two triangles that share an edge are one quad with a diagonal in it. The more common case
	/// is two triangles with a straight row of quads between them — where a lattice row ends on
	/// one side and starts on the other — and that strip, triangle, m quads, triangle, has 2m + 4
	/// sides, which is exactly m + 1 quads. Re-cutting it with every rung shifted one step along
	/// removes both triangles and moves the row ending to nowhere. Of the two ways to stagger the
	/// rungs, the one taken is the one that lowers the valence of the busier end, so a valence-5
	/// vertex beside the triangle is fixed by the same move.
	///
	/// Shortest strips first, so a pair of neighbours never loses its partner to a long detour.
	/// </summary>
	static void PairTriangles( Poly poly )
	{
		const int MaxStrip = 8;

		for ( var round = 0; round < 8; round++ )
		{
			var owner = new Dictionary<(int, int), int>();
			var edges = new HashSet<EdgeKey>();
			var valence = new Dictionary<int, int>();

			for ( var f = 0; f < poly.Faces.Count; f++ )
			{
				var face = poly.Faces[f];

				for ( var k = 0; k < face.Length; k++ )
				{
					var a = face[k];
					var b = face[(k + 1) % face.Length];
					owner[(a, b)] = f;

					if ( edges.Add( new EdgeKey( a, b ) ) )
					{
						valence[a] = valence.GetValueOrDefault( a ) + 1;
						valence[b] = valence.GetValueOrDefault( b ) + 1;
					}
				}
			}

			var candidates = new List<(int Length, float Cost, int[] Strip, int[][] Quads)>();

			for ( var t = 0; t < poly.Faces.Count; t++ )
			{
				var tri = poly.Faces[t];

				if ( tri.Length != 3 )
					continue;

				for ( var k = 0; k < 3; k++ )
				{
					var xs = new List<int> { tri[k] };
					var ys = new List<int> { tri[(k + 1) % 3] };
					var apex = tri[(k + 2) % 3];
					var strip = new List<int> { t };

					while ( strip.Count <= MaxStrip )
					{
						// Across the last rung, which the face on the far side runs y -> x.
						if ( !owner.TryGetValue( (ys[^1], xs[^1]), out var g ) || strip.Contains( g ) )
							break;

						var face = poly.Faces[g];

						if ( face.Length == 3 )
						{
							if ( g > t )
							{
								var other = face.First( c => c != xs[^1] && c != ys[^1] );
								var quads = Restagger( poly, apex, xs, ys, other, edges, valence, out var cost );

								if ( quads is not null )
									candidates.Add( (strip.Count - 1, cost, strip.Append( g ).ToArray(), quads) );
							}

							break;
						}

						if ( face.Length != 4 )
							break;

						var i = Array.IndexOf( face, ys[^1] );

						if ( face[(i + 1) % 4] != xs[^1] )
							break;

						xs.Add( face[(i + 2) % 4] );
						ys.Add( face[(i + 3) % 4] );
						strip.Add( g );
					}
				}
			}

			var used = new HashSet<int>();
			var added = new HashSet<EdgeKey>();
			var replaced = new Dictionary<int, int[][]>();

			foreach ( var (_, _, strip, quads) in candidates.OrderBy( c => c.Length ).ThenBy( c => c.Cost ) )
			{
				if ( strip.Any( used.Contains ) )
					continue;

				var fresh = new List<EdgeKey>();

				foreach ( var q in quads )
				{
					for ( var k = 0; k < 4; k++ )
					{
						var key = new EdgeKey( q[k], q[(k + 1) % 4] );

						if ( !edges.Contains( key ) )
							fresh.Add( key );
					}
				}

				if ( fresh.Any( added.Contains ) )
					continue;

				foreach ( var key in fresh )
					added.Add( key );

				foreach ( var f in strip )
					used.Add( f );

				replaced[strip[0]] = quads;
			}

			if ( replaced.Count == 0 )
				return;

			var result = new List<int[]>( poly.Faces.Count );

			for ( var f = 0; f < poly.Faces.Count; f++ )
			{
				if ( replaced.TryGetValue( f, out var quads ) )
					result.AddRange( quads );
				else if ( !used.Contains( f ) )
					result.Add( poly.Faces[f] );
			}

			poly.Faces = result;
		}
	}

	/// <summary>
	/// The m + 1 quads that replace a triangle, m quads and a triangle. The strip's rungs run
	/// x[k] -> y[k]; its outline is apex1, x[0..m], apex2, y[m..0]. Null when the cut would reuse
	/// a vertex or duplicate an edge that already exists elsewhere.
	/// </summary>
	static int[][] Restagger( Poly poly, int apex1, List<int> xs, List<int> ys, int apex2,
		HashSet<EdgeKey> edges, Dictionary<int, int> valence, out float cost )
	{
		cost = 0f;
		var m = xs.Count - 1;
		var outline = new List<int> { apex1, apex2 };
		outline.AddRange( xs );
		outline.AddRange( ys );

		if ( outline.Distinct().Count() != outline.Count )
			return null;

		int[][] quads;

		if ( m == 0 )
		{
			quads = new[] { new[] { apex1, xs[0], apex2, ys[0] } };
		}
		else
		{
			// Staggering one way takes an edge from x[m] and y[0], the other way from x[0] and
			// y[m]. Take it from whichever pair has more to give.
			int Over( int v ) => valence.GetValueOrDefault( v ) - 4;
			var forward = Over( xs[m] ) + Over( ys[0] ) >= Over( xs[0] ) + Over( ys[m] );
			quads = new int[m + 1][];

			if ( forward )
			{
				quads[0] = new[] { apex1, xs[0], ys[1], ys[0] };

				for ( var k = 1; k < m; k++ )
					quads[k] = new[] { xs[k - 1], xs[k], ys[k + 1], ys[k] };

				quads[m] = new[] { xs[m - 1], xs[m], apex2, ys[m] };
			}
			else
			{
				quads[0] = new[] { apex1, xs[0], xs[1], ys[0] };

				for ( var k = 1; k < m; k++ )
					quads[k] = new[] { ys[k - 1], xs[k], xs[k + 1], ys[k] };

				quads[m] = new[] { ys[m - 1], xs[m], apex2, ys[m] };
			}

			// The new rungs are inside the outline, so one that already exists is an edge some
			// other face uses, and adding it again would make the mesh non-manifold.
			foreach ( var q in quads )
			{
				for ( var k = 0; k < 4; k++ )
				{
					var key = new EdgeKey( q[k], q[(k + 1) % 4] );
					var boundary = outline.Contains( key.A ) && outline.Contains( key.B ) && IsOutlineEdge( outline, xs, ys, apex1, apex2, key );

					if ( !boundary && edges.Contains( key ) )
						return null;
				}
			}
		}

		foreach ( var q in quads )
			cost = MathF.Max( cost, Skew( poly, q ) );

		return m == 0 && cost > 55f ? null : quads;
	}

	static bool IsOutlineEdge( List<int> outline, List<int> xs, List<int> ys, int apex1, int apex2, EdgeKey key )
	{
		var ring = new List<int> { apex1 };
		ring.AddRange( xs );
		ring.Add( apex2 );

		for ( var k = ys.Count - 1; k >= 0; k-- )
			ring.Add( ys[k] );

		for ( var k = 0; k < ring.Count; k++ )
		{
			if ( new EdgeKey( ring[k], ring[(k + 1) % ring.Count] ).Equals( key ) )
				return true;
		}

		return false;
	}


	/// <summary>The worst corner's distance from a right angle, in degrees.</summary>
	static float Skew( Poly poly, int[] quad )
	{
		var worst = 0f;

		for ( var k = 0; k < 4; k++ )
		{
			var c = poly.P[quad[k]];
			var a = (poly.P[quad[(k + 3) % 4]] - c).Normal;
			var b = (poly.P[quad[(k + 1) % 4]] - c).Normal;
			var angle = MathF.Acos( Math.Clamp( Vec3.Dot( a, b ), -1f, 1f ) ) * 180f / MathF.PI;
			worst = MathF.Max( worst, MathF.Abs( angle - 90f ) );
		}

		return worst;
	}

	// --- subdivision and relaxation ----------------------------------------------------------

	/// <summary>Every n-gon into n quads at its edge midpoints and centre. Flat — the relaxation
	/// and the reprojection afterwards are what put the new vertices on the surface.</summary>
	static Poly Subdivide( Poly poly )
	{
		var result = new Poly { P = new List<Vec3>( poly.P ), N = new List<Vec3>( poly.N ), Seam = new List<bool>( poly.Seam ), Border = new List<bool>( poly.Border ) };
		var mids = new Dictionary<EdgeKey, int>();

		int Mid( int a, int b )
		{
			var key = new EdgeKey( a, b );

			if ( !mids.TryGetValue( key, out var m ) )
			{
				m = result.P.Count;
				result.P.Add( (poly.P[a] + poly.P[b]) * 0.5f );
				result.N.Add( (poly.N[a] + poly.N[b]).Normal );
				result.Seam.Add( poly.Seam[a] && poly.Seam[b] );
				result.Border.Add( poly.Border[a] && poly.Border[b] );
				mids[key] = m;
			}

			return m;
		}

		foreach ( var f in poly.Faces )
		{
			var centre = Vec3.Zero;
			var normal = Vec3.Zero;

			foreach ( var c in f )
			{
				centre += poly.P[c];
				normal += poly.N[c];
			}

			var mid = result.P.Count;
			result.P.Add( centre / f.Length );
			result.N.Add( normal.Normal );
			result.Seam.Add( false );
			result.Border.Add( false );

			for ( var k = 0; k < f.Length; k++ )
			{
				var prev = f[(k - 1 + f.Length) % f.Length];
				var next = f[(k + 1) % f.Length];
				result.Faces.Add( new[] { f[k], Mid( f[k], next ), mid, Mid( prev, f[k] ) } );
			}
		}

		return result;
	}

	/// <summary>
	/// Even the mesh out and put it on the source. Each pass moves every vertex part of the way to
	/// the mean of its neighbours — only along the surface, so the shape does not shrink the way a
	/// plain Laplacian shrinks it — and then onto the nearest point of the source.
	///
	/// A real border vertex only slides along the border: it averages its border neighbours and
	/// nothing else. A seam vertex is put back on x = 0 after every step, so the mirror can weld it.
	/// </summary>
	static void Relax( Poly poly, PolyMesh reference, MeshBVH tree, float scale, int passes, float seam )
	{
		var count = poly.P.Count;
		var neighbours = new List<int>[count];
		var edgeUse = new Dictionary<EdgeKey, int>();

		foreach ( var f in poly.Faces )
		{
			for ( var k = 0; k < f.Length; k++ )
			{
				var key = new EdgeKey( f[k], f[(k + 1) % f.Length] );
				edgeUse[key] = edgeUse.TryGetValue( key, out var u ) ? u + 1 : 1;
			}
		}

		var onBorder = new bool[count];

		foreach ( var (key, used) in edgeUse )
		{
			if ( used == 1 )
				onBorder[key.A] = onBorder[key.B] = true;
		}

		foreach ( var (key, used) in edgeUse )
		{
			// A border vertex hears only from the border, so it slides along it rather than in.
			if ( !onBorder[key.A] || used == 1 )
				(neighbours[key.A] ??= new List<int>()).Add( key.B );

			if ( !onBorder[key.B] || used == 1 )
				(neighbours[key.B] ??= new List<int>()).Add( key.A );
		}

		var positions = poly.P.ToArray();
		var next = new Vec3[count];
		var reach = scale * 4f;

		Vec3 Snap( int i, Vec3 p )
		{
			var hit = tree.NearestSurface( reference, p, reach );
			var q = hit is { } h ? h.Point : p;
			return seam >= 0f && poly.Seam[i] ? new Vec3( 0f, q.y, q.z ) : q;
		}

		Parallel.For( 0, count, i => positions[i] = Snap( i, positions[i] ) );

		for ( var pass = 0; pass < passes; pass++ )
		{
			var normals = VertexNormals( positions, poly.Faces );

			Parallel.For( 0, count, i =>
			{
				var list = neighbours[i];

				if ( list is null || list.Count == 0 )
				{
					next[i] = positions[i];
					return;
				}

				var mean = Vec3.Zero;

				foreach ( var j in list )
					mean += positions[j];

				var d = mean / list.Count - positions[i];
				var n = normals[i];

				if ( !onBorder[i] )
					d -= n * Vec3.Dot( n, d );

				next[i] = Snap( i, positions[i] + d * 0.6f );
			} );

			(positions, next) = (next, positions);
		}

		poly.P = positions.ToList();
	}

	static Vec3[] VertexNormals( Vec3[] positions, List<int[]> faces )
	{
		var normals = new Vec3[positions.Length];

		foreach ( var f in faces )
		{
			var n = Vec3.Zero;

			for ( var k = 0; k < f.Length; k++ )
			{
				var a = positions[f[k]];
				var b = positions[f[(k + 1) % f.Length]];
				n += Vec3.Cross( a, b );
			}

			foreach ( var c in f )
				normals[c] += n;
		}

		for ( var i = 0; i < normals.Length; i++ )
			normals[i] = normals[i].Normal;

		return normals;
	}
}
