using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// AABB tree over faces. Ray hits and radius queries against a dense sculpt mesh, where the
/// linear scan in <see cref="MeshRaycast"/> is no longer viable.
///
/// Built once; <see cref="Refit"/> updates bounds after a stroke sample. Sculpting never
/// changes topology, so the tree structure stays valid and only the boxes move. That is the
/// payoff for refusing dyntopo, and it is the reason this is a refittable BVH rather than a
/// rebuild-every-sample grid.
///
/// Faces are triangulated the same way <see cref="MeshRaycast"/> triangulates them, so a BVH
/// hit and a linear hit name the same face.
/// </summary>
public sealed class MeshBVH
{
	const int LeafSize = 4;
	const float Pad = 1e-5f;

	struct Node
	{
		public Vec3 Min, Max;
		public int Left;       // child index; -1 if leaf
		public int Right;
		public int FaceStart;
		public int FaceCount;
	}

	readonly Node[] _nodes;
	readonly int[] _faces;
	readonly int _faceCount;
	readonly bool[] _seen;

	MeshBVH( Node[] nodes, int[] faces, int faceCount, int vertexCount )
	{
		_nodes = nodes;
		_faces = faces;
		_faceCount = faceCount;
		_seen = new bool[Math.Max( vertexCount, 1 )];
	}

	public int NodeCount => _nodes.Length;
	public int FaceCount => _faceCount;
	public bool IsEmpty => _nodes.Length == 0;

	public static MeshBVH Build( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( mesh.FaceCount == 0 )
			return new MeshBVH( Array.Empty<Node>(), Array.Empty<int>(), 0, mesh.VertexCount );

		var faces = new int[mesh.FaceCount];
		var centroids = new Vec3[mesh.FaceCount];

		// PER-FACE BOXES, COMPUTED ONCE.
		//
		// Every node needs the bounds of its range, and the range at the root is every face. Asking
		// the mesh for them walks each face's vertices again at each of the ~log n levels, so a
		// vertex is read log n times over a build. Boxing each face up front makes a node's bounds
		// a union of count boxes instead - the same total number of unions, but over 6 floats
		// already in a flat array rather than an indirection through Faces[i].Indices into
		// Positions.
		var faceMin = new Vec3[mesh.FaceCount];
		var faceMax = new Vec3[mesh.FaceCount];

		for ( var i = 0; i < mesh.FaceCount; i++ )
		{
			faces[i] = i;

			var f = mesh.Faces[i];
			var sum = Vec3.Zero;
			var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );

			foreach ( var vi in f.Indices )
			{
				var p = mesh.Positions[vi];
				sum += p;
				lo = CMin( lo, p );
				hi = CMax( hi, p );
			}

			centroids[i] = sum / f.Count;
			faceMin[i] = lo;
			faceMax[i] = hi;
		}

		var nodes = new List<Node>( mesh.FaceCount * 2 );
		BuildNode( faces, centroids, faceMin, faceMax, 0, mesh.FaceCount, nodes );
		return new MeshBVH( nodes.ToArray(), faces, mesh.FaceCount, mesh.VertexCount );
	}

	static int BuildNode( int[] faces, Vec3[] centroids, Vec3[] faceMin, Vec3[] faceMax, int start, int count, List<Node> nodes )
	{
		var index = nodes.Count;
		nodes.Add( default );

		BoundsOfBoxes( faces, faceMin, faceMax, start, count, out var min, out var max );

		if ( count <= LeafSize || AllCentroidsEqual( centroids, faces, start, count ) )
		{
			SortLeaf( faces, start, count );
			nodes[index] = new Node
			{
				Min = min, Max = max,
				Left = -1, Right = -1,
				FaceStart = start, FaceCount = count
			};
			return index;
		}

		var axis = LongestAxis( min, max );
		var mid = count / 2;

		// PARTITION AROUND THE MEDIAN, DO NOT SORT.
		//
		// This used to be Array.Sort over the range with a Comparer<int>.Create closure, which is
		// the obvious way to write "split at the median" and is quietly the most expensive line in
		// the kernel. Two separate costs, both paid at EVERY node:
		//
		//   - Sorting is O(k log k) where selecting the median is O(k). Summed over a whole tree
		//     that is the difference between O(n log n) and O(n log^2 n).
		//   - Comparer<int>.Create allocates a comparer per node (~2n of them) and turns every
		//     comparison into an interface call through a delegate, which does not inline.
		//
		// Nothing downstream wants the range sorted - BuildNode only ever asks which half a face
		// falls in. Select is the operation that was actually meant.
		//
		// The ordering is the same total order the comparer used, centroid along the split axis
		// with the face index breaking ties, so each child gets exactly the same SET of faces the
		// sorting version gave it and the tree shape is unchanged. Only the order within a range
		// differs, and the leaf sort below puts that back for the ranges anyone can observe.
		SelectNth( faces, centroids, axis, start, count, mid );

		if ( mid == 0 || mid == count )
		{
			SortLeaf( faces, start, count );
			nodes[index] = new Node
			{
				Min = min, Max = max,
				Left = -1, Right = -1,
				FaceStart = start, FaceCount = count
			};
			return index;
		}

		var left = BuildNode( faces, centroids, faceMin, faceMax, start, mid, nodes );
		var right = BuildNode( faces, centroids, faceMin, faceMax, start + mid, count - mid, nodes );
		nodes[index] = new Node
		{
			Min = min, Max = max,
			Left = left, Right = right,
			FaceStart = start, FaceCount = 0
		};
		return index;
	}

	/// <summary>
	/// Recompute every box from the mesh's current positions. Face membership does not change.
	/// Call after vertices move; do not call after topology changes — build a new tree.
	/// </summary>
	public void Refit( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( mesh.FaceCount != _faceCount )
			throw new ArgumentException(
				$"Refit needs the same topology (built on {_faceCount} faces, mesh has {mesh.FaceCount})" );

		if ( _nodes.Length == 0 )
			return;

		// FLAT AND BACKWARDS, NOT RECURSIVE.
		//
		// BuildNode appends a node before recursing into its children, so a parent's index is
		// always lower than either child's. Walking the array from the end therefore visits every
		// child before its parent, which is exactly the order a refit needs - and it does so as a
		// straight sequential pass over the node array instead of two million nested calls, each
		// touching the array at an index the prefetcher cannot guess.
		for ( var i = _nodes.Length - 1; i >= 0; i-- )
		{
			ref var node = ref _nodes[i];

			if ( node.Left < 0 )
			{
				BoundsOfFaces( mesh, _faces, node.FaceStart, node.FaceCount, out node.Min, out node.Max );
				continue;
			}

			node.Min = CMin( _nodes[node.Left].Min, _nodes[node.Right].Min );
			node.Max = CMax( _nodes[node.Left].Max, _nodes[node.Right].Max );
		}
	}

	/// <summary>
	/// Refit only the part of the tree a brush could have touched: the sphere at
	/// <paramref name="center"/> of <paramref name="radius"/>, the region the caller just moved
	/// vertices inside.
	///
	/// WHY THIS IS SOUND. A node's current box was computed from where its vertices were BEFORE
	/// the edit, and every vertex that moved was inside the sphere at that point. So a node whose
	/// box misses the sphere cannot contain a moved vertex, and its bounds - and those of
	/// everything below it - are still correct. Descending only into nodes that DO intersect
	/// visits the handful of leaves under the brush plus their ancestors, and skips the rest.
	///
	/// A vertex is free to land outside the sphere; the leaf holding it is recomputed from its new
	/// position and the boxes grow on the way back up. What must not happen is MISSING a node that
	/// needed recomputing, and the argument above is about the old positions, which is the side
	/// that decides that.
	///
	/// The full <see cref="Refit"/> is for when the whole mesh moved - a transform, a pose, an
	/// undo. This is the interactive case, where a stroke touches a thousand vertices of a million
	/// and rebuilding every box costs the frame.
	/// </summary>
	public void RefitRegion( PolyMesh mesh, Vec3 center, float radius )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( mesh.FaceCount != _faceCount )
			throw new ArgumentException(
				$"RefitRegion needs the same topology (built on {_faceCount} faces, mesh has {mesh.FaceCount})" );

		if ( _nodes.Length == 0 || radius <= 0f )
			return;

		RefitRegionNode( mesh, 0, center, radius );
	}

	/// <summary>Returns whether anything under this node was touched, so a parent only recombines
	/// when a child below it actually changed.</summary>
	bool RefitRegionNode( PolyMesh mesh, int index, Vec3 center, float radius )
	{
		ref var node = ref _nodes[index];

		if ( !SphereHitsBox( node.Min, node.Max, center, radius ) )
			return false;

		if ( node.Left < 0 )
		{
			BoundsOfFaces( mesh, _faces, node.FaceStart, node.FaceCount, out node.Min, out node.Max );
			return true;
		}

		// Both sides, deliberately not short-circuited - a stroke straddling the split touches each.
		var left = RefitRegionNode( mesh, node.Left, center, radius );
		var right = RefitRegionNode( mesh, node.Right, center, radius );

		if ( !left && !right )
			return false;

		node.Min = CMin( _nodes[node.Left].Min, _nodes[node.Right].Min );
		node.Max = CMax( _nodes[node.Left].Max, _nodes[node.Right].Max );
		return true;
	}

	static bool SphereHitsBox( Vec3 min, Vec3 max, Vec3 center, float radius )
	{
		var dx = center.x < min.x ? min.x - center.x : center.x > max.x ? center.x - max.x : 0f;
		var dy = center.y < min.y ? min.y - center.y : center.y > max.y ? center.y - max.y : 0f;
		var dz = center.z < min.z ? min.z - center.z : center.z > max.z ? center.z - max.z : 0f;

		return dx * dx + dy * dy + dz * dz <= radius * radius;
	}

	/// <summary>
	/// Nearest face hit, same contract as <see cref="MeshRaycast.Raycast(PolyMesh, Vec3, Vec3)"/>.
	/// </summary>
	public MeshHit? Raycast( PolyMesh mesh, Vec3 origin, Vec3 direction )
	{
		if ( mesh is null || _nodes.Length == 0 )
			return null;

		if ( mesh.FaceCount != _faceCount )
			throw new ArgumentException(
				$"Raycast needs the same topology (built on {_faceCount} faces, mesh has {mesh.FaceCount})" );

		var dir = direction.Normal;

		if ( dir.LengthSquared < 0.5f )
			return null;

		var inv = new Vec3( SafeInv( dir.x ), SafeInv( dir.y ), SafeInv( dir.z ) );
		MeshHit? best = null;
		RaycastNode( mesh, 0, origin, dir, inv, ref best );
		return best;
	}

	void RaycastNode( PolyMesh mesh, int index, Vec3 origin, Vec3 dir, Vec3 inv, ref MeshHit? best )
	{
		var node = _nodes[index];
		var tMax = best is { } current ? current.Distance : float.MaxValue;

		if ( !RayHitsBounds( origin, inv, 0f, tMax, node.Min, node.Max ) )
			return;

		if ( node.Left < 0 )
		{
			for ( var i = 0; i < node.FaceCount; i++ )
			{
				var fi = _faces[node.FaceStart + i];

				if ( !MeshRaycast.HitFace( mesh, fi, origin, dir, out var t, out var point ) )
					continue;

				if ( best is { } held && t >= held.Distance )
					continue;

				best = new MeshHit( point, fi, mesh.FaceNormal( mesh.Faces[fi] ), t );
			}

			return;
		}

		RaycastNode( mesh, node.Left, origin, dir, inv, ref best );
		RaycastNode( mesh, node.Right, origin, dir, inv, ref best );
	}

	/// <summary>
	/// Vertices whose positions lie inside the sphere. The tree prunes faces whose boxes miss
	/// the sphere; the returned set is then filtered by actual distance, so it matches a
	/// brute-force scan of every vertex.
	/// </summary>
	public void VerticesInRadius( PolyMesh mesh, Vec3 point, float radius, List<int> results )
	{
		if ( results is null )
			throw new ArgumentNullException( nameof( results ) );

		results.Clear();

		if ( mesh is null || _nodes.Length == 0 || radius < 0f )
			return;

		if ( mesh.FaceCount != _faceCount )
			throw new ArgumentException(
				$"Query needs the same topology (built on {_faceCount} faces, mesh has {mesh.FaceCount})" );

		if ( _seen.Length < mesh.VertexCount )
			throw new ArgumentException(
				$"Query needs the same vertex count (built for {_seen.Length}, mesh has {mesh.VertexCount})" );

		Array.Clear( _seen, 0, mesh.VertexCount );
		var r2 = radius * radius;
		Collect( mesh, 0, point, radius, r2, results );
	}

	void Collect( PolyMesh mesh, int index, Vec3 point, float radius, float r2, List<int> results )
	{
		var node = _nodes[index];

		if ( !SphereHitsBounds( point, radius, node.Min, node.Max ) )
			return;

		if ( node.Left < 0 )
		{
			for ( var i = 0; i < node.FaceCount; i++ )
			{
				var face = mesh.Faces[_faces[node.FaceStart + i]];

				foreach ( var vi in face.Indices )
				{
					if ( _seen[vi] )
						continue;

					_seen[vi] = true;
					var d = mesh.Positions[vi] - point;

					if ( d.LengthSquared <= r2 )
						results.Add( vi );
				}
			}

			return;
		}

		Collect( mesh, node.Left, point, radius, r2, results );
		Collect( mesh, node.Right, point, radius, r2, results );
	}

	/// <summary>
	/// Faces any part of which lies inside the sphere, named the way a raycast names them.
	///
	/// The box test is only the pruning step; the verdict is the closest point on the triangulated
	/// face, using the same triangulation <see cref="MeshRaycast"/> does so a hit here and a hit
	/// there agree. A face whose box overlaps the sphere but whose surface does not is not just a
	/// loose result — the caller turns every returned face into rasterised texels, so a false
	/// positive is visible paint where the brush never touched.
	/// </summary>
	public void FacesInRadius( PolyMesh mesh, Vec3 point, float radius, List<int> results )
	{
		if ( results is null )
			throw new ArgumentNullException( nameof( results ) );

		results.Clear();

		if ( mesh is null || _nodes.Length == 0 || radius < 0f )
			return;

		if ( mesh.FaceCount != _faceCount )
			throw new ArgumentException(
				$"Query needs the same topology (built on {_faceCount} faces, mesh has {mesh.FaceCount})" );

		var r2 = radius * radius;
		CollectFaces( mesh, 0, point, radius, r2, results );
	}

	void CollectFaces( PolyMesh mesh, int index, Vec3 point, float radius, float r2, List<int> results )
	{
		var node = _nodes[index];

		if ( !SphereHitsBounds( point, radius, node.Min, node.Max ) )
			return;

		if ( node.Left < 0 )
		{
			for ( var i = 0; i < node.FaceCount; i++ )
			{
				var fi = _faces[node.FaceStart + i];

				if ( FaceTouchesSphere( mesh, mesh.Faces[fi], point, r2 ) )
					results.Add( fi );
			}

			return;
		}

		CollectFaces( mesh, node.Left, point, radius, r2, results );
		CollectFaces( mesh, node.Right, point, radius, r2, results );
	}

	static bool FaceTouchesSphere( PolyMesh mesh, Face face, Vec3 point, float r2 )
	{
		if ( face.Count < 3 )
			return false;

		var corners = new List<Vec3>( face.Count );

		for ( var c = 0; c < face.Count; c++ )
			corners.Add( mesh.Positions[face.Indices[c]] );

		foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
		{
			if ( SphereTouchesTriangle( point, r2, corners[ia], corners[ib], corners[ic] ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Whether the sphere reaches a triangle, by clamping the closest point on the triangle to the
	/// vertex, then edge, then face regions in turn. The box pass above is a loose gate; this is the
	/// distance that actually decides, so a sphere grazing a box corner far from the triangle it
	/// contains does not drag a face in.
	/// </summary>
	static bool SphereTouchesTriangle( Vec3 p, float r2, Vec3 a, Vec3 b, Vec3 c )
	{
		var ab = b - a;
		var ac = c - a;
		var ap = p - a;

		var d1 = Vec3.Dot( ab, ap );
		var d2 = Vec3.Dot( ac, ap );

		if ( d1 <= 0f && d2 <= 0f )
			return ap.LengthSquared <= r2;

		var bp = p - b;
		var d3 = Vec3.Dot( ab, bp );
		var d4 = Vec3.Dot( ac, bp );

		if ( d3 >= 0f && d4 <= d3 )
			return bp.LengthSquared <= r2;

		var vc = d1 * d4 - d3 * d2;

		if ( vc <= 0f && d1 >= 0f && d3 <= 0f )
		{
			var v = d1 / (d1 - d3);
			return (a + ab * v - p).LengthSquared <= r2;
		}

		var cp = p - c;
		var d5 = Vec3.Dot( ab, cp );
		var d6 = Vec3.Dot( ac, cp );

		if ( d6 >= 0f && d5 <= d6 )
			return cp.LengthSquared <= r2;

		var vb = d5 * d2 - d1 * d6;

		if ( vb <= 0f && d2 >= 0f && d6 <= 0f )
		{
			var w = d2 / (d2 - d6);
			return (a + ac * w - p).LengthSquared <= r2;
		}

		var va = d3 * d6 - d5 * d4;

		if ( va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f )
		{
			var w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
			return (b + (c - b) * w - p).LengthSquared <= r2;
		}

		var denom = 1f / (va + vb + vc);
		return (a + ab * (vb * denom) + ac * (vc * denom) - p).LengthSquared <= r2;
	}

	/// <summary>
	/// Reorder faces[start..start+count) so the element that would land at <paramref name="n"/>
	/// under a full sort is at n, everything ordered before it is left of it, and everything
	/// ordered after it is right of it. Quickselect - average O(count), no allocation.
	///
	/// The order is (centroid along axis, then face index), which is a strict total order because
	/// face indices are distinct. That matters: it means the split never depends on which equal
	/// element the partition happened to land on, so a build is reproducible.
	/// </summary>
	static void SelectNth( int[] faces, Vec3[] centroids, int axis, int start, int count, int n )
	{
		var lo = start;
		var hi = start + count - 1;
		var target = start + n;

		while ( lo < hi )
		{
			// Median of three, so the already-sorted runs a mesh generator produces constantly do
			// not hit quickselect's quadratic case.
			var mid = lo + ((hi - lo) >> 1);

			if ( Before( faces[mid], faces[lo], centroids, axis ) ) Swap( faces, lo, mid );
			if ( Before( faces[hi], faces[lo], centroids, axis ) ) Swap( faces, lo, hi );
			if ( Before( faces[hi], faces[mid], centroids, axis ) ) Swap( faces, mid, hi );

			// The median of the three is now at mid; park it at hi as the pivot.
			Swap( faces, mid, hi );
			var pivot = faces[hi];
			var store = lo;

			for ( var i = lo; i < hi; i++ )
			{
				if ( Before( faces[i], pivot, centroids, axis ) )
				{
					Swap( faces, i, store );
					store++;
				}
			}

			Swap( faces, store, hi );

			// Before is a STRICT TOTAL order - the face index breaks every centroid tie - so no
			// two elements compare equal and the pivot always lands strictly between the two
			// halves. That is what rules out the all-equal input that makes Lomuto quadratic.
			if ( store == target ) return;
			if ( store < target ) lo = store + 1;
			else hi = store - 1;
		}
	}

	/// <summary>
	/// Leaves are at most <see cref="LeafSize"/> faces, so an insertion sort by face index is
	/// nothing. It exists for determinism rather than speed: a radius query returns faces in leaf
	/// order, and without this that order would depend on how quickselect happened to partition.
	/// </summary>
	static void SortLeaf( int[] faces, int start, int count )
	{
		for ( var i = start + 1; i < start + count; i++ )
		{
			var v = faces[i];
			var j = i - 1;

			while ( j >= start && faces[j] > v )
			{
				faces[j + 1] = faces[j];
				j--;
			}

			faces[j + 1] = v;
		}
	}

	static bool Before( int a, int b, Vec3[] centroids, int axis )
	{
		var ca = Component( centroids[a], axis );
		var cb = Component( centroids[b], axis );
		return ca != cb ? ca < cb : a < b;
	}

	static void Swap( int[] a, int i, int j )
	{
		(a[i], a[j]) = (a[j], a[i]);
	}

	/// <summary>
	/// Union of precomputed per-face boxes over a range. The build's version of
	/// <see cref="BoundsOfFaces"/>; the padding matches, because the minimum of (v - Pad) over a
	/// set is the same as (minimum of v) - Pad.
	/// </summary>
	static void BoundsOfBoxes( int[] faces, Vec3[] faceMin, Vec3[] faceMax, int start, int count, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		for ( var i = 0; i < count; i++ )
		{
			var f = faces[start + i];
			min = CMin( min, faceMin[f] );
			max = CMax( max, faceMax[f] );
		}

		min = new Vec3( min.x - Pad, min.y - Pad, min.z - Pad );
		max = new Vec3( max.x + Pad, max.y + Pad, max.z + Pad );
	}

	/// <summary>Bounds straight from the mesh. Refit's version - positions have moved, so the
	/// boxes the build cached are stale and the vertices are the only truth.</summary>
	static void BoundsOfFaces( PolyMesh mesh, int[] faces, int start, int count, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		for ( var i = 0; i < count; i++ )
		{
			var face = mesh.Faces[faces[start + i]];

			foreach ( var vi in face.Indices )
			{
				var p = mesh.Positions[vi];
				min = CMin( min, p );
				max = CMax( max, p );
			}
		}

		min = new Vec3( min.x - Pad, min.y - Pad, min.z - Pad );
		max = new Vec3( max.x + Pad, max.y + Pad, max.z + Pad );
	}

	static bool AllCentroidsEqual( Vec3[] centroids, int[] faces, int start, int count )
	{
		var first = centroids[faces[start]];

		for ( var i = 1; i < count; i++ )
		{
			if ( !centroids[faces[start + i]].AlmostEquals( first, 1e-8f ) )
				return false;
		}

		return true;
	}

	static int LongestAxis( Vec3 min, Vec3 max )
	{
		var e = max - min;

		if ( e.x >= e.y && e.x >= e.z )
			return 0;

		return e.y >= e.z ? 1 : 2;
	}

	static float Component( Vec3 v, int axis ) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;

	static Vec3 CMin( Vec3 a, Vec3 b ) =>
		new( MathF.Min( a.x, b.x ), MathF.Min( a.y, b.y ), MathF.Min( a.z, b.z ) );

	static Vec3 CMax( Vec3 a, Vec3 b ) =>
		new( MathF.Max( a.x, b.x ), MathF.Max( a.y, b.y ), MathF.Max( a.z, b.z ) );

	static float SafeInv( float d )
	{
		if ( d > 1e-12f || d < -1e-12f )
			return 1f / d;

		return d >= 0f ? 1e12f : -1e12f;
	}

	static bool RayHitsBounds( Vec3 origin, Vec3 inv, float tMin, float tMax, Vec3 bmin, Vec3 bmax )
	{
		var t0 = (bmin.x - origin.x) * inv.x;
		var t1 = (bmax.x - origin.x) * inv.x;

		if ( t0 > t1 )
			(t0, t1) = (t1, t0);

		tMin = MathF.Max( tMin, t0 );
		tMax = MathF.Min( tMax, t1 );

		if ( tMin > tMax )
			return false;

		t0 = (bmin.y - origin.y) * inv.y;
		t1 = (bmax.y - origin.y) * inv.y;

		if ( t0 > t1 )
			(t0, t1) = (t1, t0);

		tMin = MathF.Max( tMin, t0 );
		tMax = MathF.Min( tMax, t1 );

		if ( tMin > tMax )
			return false;

		t0 = (bmin.z - origin.z) * inv.z;
		t1 = (bmax.z - origin.z) * inv.z;

		if ( t0 > t1 )
			(t0, t1) = (t1, t0);

		tMin = MathF.Max( tMin, t0 );
		tMax = MathF.Min( tMax, t1 );
		return tMin <= tMax;
	}

	static bool SphereHitsBounds( Vec3 point, float radius, Vec3 bmin, Vec3 bmax )
	{
		var dx = point.x < bmin.x ? bmin.x - point.x : point.x > bmax.x ? point.x - bmax.x : 0f;
		var dy = point.y < bmin.y ? bmin.y - point.y : point.y > bmax.y ? point.y - bmax.y : 0f;
		var dz = point.z < bmin.z ? bmin.z - point.z : point.z > bmax.z ? point.z - bmax.z : 0f;
		return dx * dx + dy * dy + dz * dz <= radius * radius;
	}
}
