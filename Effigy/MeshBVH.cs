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

		for ( var i = 0; i < mesh.FaceCount; i++ )
		{
			faces[i] = i;
			centroids[i] = mesh.FaceCentroid( mesh.Faces[i] );
		}

		var nodes = new List<Node>( mesh.FaceCount * 2 );
		BuildNode( mesh, faces, centroids, 0, mesh.FaceCount, nodes );
		return new MeshBVH( nodes.ToArray(), faces, mesh.FaceCount, mesh.VertexCount );
	}

	static int BuildNode( PolyMesh mesh, int[] faces, Vec3[] centroids, int start, int count, List<Node> nodes )
	{
		var index = nodes.Count;
		nodes.Add( default );

		BoundsOfFaces( mesh, faces, start, count, out var min, out var max );

		if ( count <= LeafSize || AllCentroidsEqual( centroids, faces, start, count ) )
		{
			nodes[index] = new Node
			{
				Min = min, Max = max,
				Left = -1, Right = -1,
				FaceStart = start, FaceCount = count
			};
			return index;
		}

		var axis = LongestAxis( min, max );
		Array.Sort( faces, start, count, Comparer<int>.Create( ( a, b ) =>
		{
			var ca = Component( centroids[a], axis );
			var cb = Component( centroids[b], axis );
			var cmp = ca.CompareTo( cb );
			return cmp != 0 ? cmp : a.CompareTo( b );
		} ) );

		var mid = count / 2;

		if ( mid == 0 || mid == count )
		{
			nodes[index] = new Node
			{
				Min = min, Max = max,
				Left = -1, Right = -1,
				FaceStart = start, FaceCount = count
			};
			return index;
		}

		var left = BuildNode( mesh, faces, centroids, start, mid, nodes );
		var right = BuildNode( mesh, faces, centroids, start + mid, count - mid, nodes );
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

		RefitNode( mesh, 0 );
	}

	void RefitNode( PolyMesh mesh, int index )
	{
		ref var node = ref _nodes[index];

		if ( node.Left < 0 )
		{
			BoundsOfFaces( mesh, _faces, node.FaceStart, node.FaceCount, out node.Min, out node.Max );
			return;
		}

		RefitNode( mesh, node.Left );
		RefitNode( mesh, node.Right );
		node.Min = CMin( _nodes[node.Left].Min, _nodes[node.Right].Min );
		node.Max = CMax( _nodes[node.Left].Max, _nodes[node.Right].Max );
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
