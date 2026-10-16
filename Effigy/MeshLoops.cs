using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Topology operations a modeller reaches for on a cage, rather than on a dense sculpt: grow
/// regions over faces, rip an edge chain open, find open edge loops in face order, and extrude a
/// loop into rings. They are what turns a closed-mouth base into an open one along the artist's
/// own loops instead of carving the dense surface (see the organic-modeling skill's tutorials.md).
/// </summary>
public static class MeshLoops
{
	/// <summary>Faces on each edge (key: smaller vertex index first).</summary>
	public static Dictionary<(int, int), List<int>> EdgeFaces( PolyMesh mesh )
	{
		var byEdge = new Dictionary<(int, int), List<int>>();
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var idx = mesh.Faces[fi].Indices;
			for ( var k = 0; k < idx.Length; k++ )
			{
				var key = Key( idx[k], idx[(k + 1) % idx.Length] );
				if ( !byEdge.TryGetValue( key, out var list ) )
					byEdge[key] = list = new List<int>( 2 );
				list.Add( fi );
			}
		}
		return byEdge;
	}

	public static (int, int) Key( int a, int b ) => a < b ? (a, b) : (b, a);

	/// <summary>Face centroids.</summary>
	public static Vec3[] Centroids( PolyMesh mesh )
	{
		var c = new Vec3[mesh.FaceCount];
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var s = Vec3.Zero;
			foreach ( var i in mesh.Faces[fi].Indices ) s += mesh.Positions[i];
			c[fi] = s / mesh.Faces[fi].Indices.Length;
		}
		return c;
	}

	/// <summary>
	/// Grows labelled regions out from seed faces at once, by distance between face centroids,
	/// through faces <paramref name="allow"/> accepts: each face takes the label of whichever seed
	/// reaches it first. Where two regions meet is the line equally far from both — for an upper
	/// and a lower lip seed, the seal at the bottom of the mouth's slit. Unreached faces get 0.
	/// </summary>
	public static int[] GrowRegions( PolyMesh mesh, IEnumerable<(int Face, int Label)> seeds, Func<int, bool> allow = null )
	{
		var byEdge = EdgeFaces( mesh );
		var cen = Centroids( mesh );
		var label = new int[mesh.FaceCount];
		var dist = Enumerable.Repeat( float.MaxValue, mesh.FaceCount ).ToArray();
		var pq = new PriorityQueue<int, float>();
		foreach ( var (f, l) in seeds )
		{
			label[f] = l; dist[f] = 0f; pq.Enqueue( f, 0f );
		}
		while ( pq.TryDequeue( out var fi, out var d ) )
		{
			if ( d > dist[fi] ) continue;
			var idx = mesh.Faces[fi].Indices;
			for ( var k = 0; k < idx.Length; k++ )
				foreach ( var nf in byEdge[Key( idx[k], idx[(k + 1) % idx.Length] )] )
				{
					if ( nf == fi || (allow is not null && !allow( nf )) ) continue;
					var nd = d + (cen[nf] - cen[fi]).Length;
					if ( nd < dist[nf] ) { dist[nf] = nd; label[nf] = label[fi]; pq.Enqueue( nf, nd ); }
				}
		}
		return label;
	}

	/// <summary>
	/// The edges between faces labelled <paramref name="a"/> and <paramref name="b"/>, kept only
	/// where <paramref name="keepEdge"/> accepts both end points.
	/// </summary>
	public static List<(int, int)> Border( PolyMesh mesh, int[] labels, int a, int b, Func<Vec3, bool> keepEdge = null )
	{
		var cut = new List<(int, int)>();
		foreach ( var (key, fs) in EdgeFaces( mesh ) )
		{
			if ( fs.Count != 2 ) continue;
			var la = labels[fs[0]]; var lb = labels[fs[1]];
			if ( !((la == a && lb == b) || (la == b && lb == a)) ) continue;
			if ( keepEdge is not null && (!keepEdge( mesh.Positions[key.Item1] ) || !keepEdge( mesh.Positions[key.Item2] )) ) continue;
			cut.Add( key );
		}
		return cut;
	}

	/// <summary>
	/// Rips a chain of edges open: every vertex inside the chain is duplicated and the faces on
	/// <paramref name="sideB"/> take the copies. The chain's ends (vertices on only one of its
	/// edges) stay shared, so a rip along a lip seal opens the mouth and keeps the corners joined.
	/// Nothing is deleted and the face list keeps its order. <paramref name="twins"/> maps each
	/// ripped vertex to its new copy.
	/// </summary>
	public static PolyMesh Rip( PolyMesh mesh, IEnumerable<(int, int)> edges, Func<int, bool> sideB, out Dictionary<int, int> twins )
	{
		var degree = new Dictionary<int, int>();
		foreach ( var (a, b) in edges )
		{
			degree[a] = degree.GetValueOrDefault( a ) + 1;
			degree[b] = degree.GetValueOrDefault( b ) + 1;
		}
		var positions = mesh.Positions.ToList();
		twins = new Dictionary<int, int>();
		foreach ( var (v, deg) in degree.OrderBy( kv => kv.Key ) )
			if ( deg >= 2 ) { twins[v] = positions.Count; positions.Add( mesh.Positions[v] ); }
		var faces = new List<Face>( mesh.FaceCount );
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var f = mesh.Faces[fi];
			var idx = (int[])f.Indices.Clone();
			if ( sideB( fi ) )
				for ( var k = 0; k < idx.Length; k++ )
					if ( twins.TryGetValue( idx[k], out var t ) ) idx[k] = t;
			faces.Add( new Face( idx, f.UVs, f.Material ) );
		}
		return new PolyMesh( positions, faces );
	}

	/// <summary>
	/// Open edges in face order, chained into loops or chains: each list runs the way the faces
	/// run along it (a to b as some face goes a -> b), so walls built off it keep the winding.
	/// The bool says whether the chain closes.
	/// </summary>
	public static List<(List<int> Verts, bool Closed)> OpenLoops( PolyMesh mesh, Func<int, bool> include = null )
	{
		var byEdge = EdgeFaces( mesh );
		var next = new Dictionary<int, int>();
		foreach ( var f in mesh.Faces )
			for ( var k = 0; k < f.Indices.Length; k++ )
			{
				int a = f.Indices[k], b = f.Indices[(k + 1) % f.Indices.Length];
				if ( byEdge[Key( a, b )].Count != 1 ) continue;
				if ( include is not null && (!include( a ) || !include( b )) ) continue;
				next[a] = b;
			}
		var prev = new Dictionary<int, int>();
		foreach ( var (a, b) in next ) prev[b] = a;
		var seen = new HashSet<int>();
		var result = new List<(List<int>, bool)>();
		// Chains first, from their heads; then what is left is loops.
		foreach ( var head in next.Keys.Where( v => !prev.ContainsKey( v ) ).Concat( next.Keys ).ToList() )
		{
			if ( seen.Contains( head ) ) continue;
			var chain = new List<int> { head };
			seen.Add( head );
			var cur = head;
			var closed = false;
			while ( next.TryGetValue( cur, out var n ) )
			{
				if ( n == head ) { closed = true; break; }
				if ( !seen.Add( n ) ) break;
				chain.Add( n );
				cur = n;
			}
			if ( chain.Count >= 2 ) result.Add( (chain, closed) );
		}
		return result;
	}

	/// <summary>
	/// Extrudes an open loop into <paramref name="rings"/> new rings: ring r of loop vertex i sits
	/// at <paramref name="at"/>(i, r) (r from 1). Quads run ring to ring wound like a wall off the
	/// face edge, so the new surface continues the old one's outside. Returns the new mesh and the
	/// vertex index of every (i, r).
	/// </summary>
	public static PolyMesh ExtrudeLoop( PolyMesh mesh, IList<int> loop, bool closed, int rings, Func<int, int, Vec3> at, int material, out int[,] grid )
	{
		var positions = mesh.Positions.ToList();
		var faces = mesh.Faces.Select( f => new Face( (int[])f.Indices.Clone(), f.UVs, f.Material ) ).ToList();
		var m = loop.Count;
		grid = new int[m, rings + 1];
		for ( var i = 0; i < m; i++ )
		{
			grid[i, 0] = loop[i];
			for ( var r = 1; r <= rings; r++ )
			{
				grid[i, r] = positions.Count;
				positions.Add( at( i, r ) );
			}
		}
		var count = closed ? m : m - 1;
		for ( var i = 0; i < count; i++ )
		{
			var j = (i + 1) % m;
			for ( var r = 0; r < rings; r++ )
				faces.Add( new Face( new[] { grid[j, r], grid[i, r], grid[i, r + 1], grid[j, r + 1] }, null, material ) );
		}
		return new PolyMesh( positions, faces );
	}

	/// <summary>Vertex neighbours over face edges.</summary>
	public static int[][] Neighbours( int vertexCount, IEnumerable<Face> faces )
	{
		var sets = new HashSet<int>[vertexCount];
		foreach ( var f in faces )
			for ( var k = 0; k < f.Indices.Length; k++ )
			{
				int a = f.Indices[k], b = f.Indices[(k + 1) % f.Indices.Length];
				(sets[a] ??= new HashSet<int>()).Add( b );
				(sets[b] ??= new HashSet<int>()).Add( a );
			}
		return sets.Select( s => s?.ToArray() ?? Array.Empty<int>() ).ToArray();
	}

	/// <summary>Shortest distance over the mesh's edges from a set of source vertices, up to <paramref name="limit"/> (beyond: MaxValue).</summary>
	public static float[] EdgeDistance( IList<Vec3> positions, int[][] neighbours, IEnumerable<int> sources, float limit )
	{
		var dist = Enumerable.Repeat( float.MaxValue, positions.Count ).ToArray();
		var pq = new PriorityQueue<int, float>();
		foreach ( var s in sources ) { dist[s] = 0f; pq.Enqueue( s, 0f ); }
		while ( pq.TryDequeue( out var v, out var dv ) )
		{
			if ( dv > dist[v] ) continue;
			foreach ( var j in neighbours[v] )
			{
				var nd = dv + (positions[v] - positions[j]).Length;
				if ( nd < dist[j] && nd <= limit ) { dist[j] = nd; pq.Enqueue( j, nd ); }
			}
		}
		return dist;
	}
}
