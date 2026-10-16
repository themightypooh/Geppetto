using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The convex hull of a point cloud, by incremental insertion.
///
/// WHY THE KERNEL NEEDS ONE AT ALL: collision. A physics engine wants convex shapes, and a body that
/// has been through a boolean or a subdivide is not a primitive any more and cannot be described as
/// one. Its hull is the honest fallback — bigger than the part where the part is concave, never
/// smaller, and always convex.
///
/// INCREMENTAL RATHER THAN QUICKHULL, deliberately. Quickhull is faster on large clouds and this is
/// never given one: a CAD body is hundreds of vertices, not millions, and the incremental algorithm
/// is short enough to read in one sitting. Start with a tetrahedron, then for each remaining point,
/// delete every face it can see and stitch the resulting hole back to it.
///
/// It is exact for points in general position and degrades gracefully where it is not: a cloud that
/// is flat, collinear or a single point has no volume to enclose, and rather than emit a broken hull
/// this says so by returning null.
///
/// A point looking at a flat run of faces is outside some of them by less than the tolerance. Leaving
/// those faces out of the cap punches holes in it, the horizon becomes two loops, and stitching both
/// to the new point pinches the surface — after that every later horizon is nonsense and the face
/// count explodes. Every face the point is actually in front of belongs to the cap, the horizon is
/// one undirected loop or it is not stitched, and a stitch that would cut off an earlier vertex is
/// refused.
/// </summary>
public static class ConvexHull
{
	/// <summary>One face of the hull, as indices into the original point list.</summary>
	public readonly struct HullFace
	{
		public readonly int A, B, C;

		public HullFace( int a, int b, int c )
		{
			A = a;
			B = b;
			C = c;
		}
	}

	/// <summary>
	/// The hull of <paramref name="points"/>, or null when they enclose no volume.
	///
	/// Null rather than a degenerate hull: a caller with a flat cloud has a real decision to make
	/// (a box? a plane? refuse?) and handing it three coincident triangles makes that decision look
	/// like it has already been taken.
	/// </summary>
	public static (List<Vec3> Points, List<HullFace> Faces)? Build( IReadOnlyList<Vec3> points, float tolerance = 1e-6f )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );

		if ( points.Count < 4 )
			return null;

		if ( !StartingTetrahedron( points, tolerance, out var seed ) )
			return null;

		var faces = new List<HullFace>
		{
			new( seed[0], seed[1], seed[2] ),
			new( seed[0], seed[2], seed[3] ),
			new( seed[0], seed[3], seed[1] ),
			new( seed[1], seed[3], seed[2] ),
		};

		// Wound outward, so "can this point see this face" is one dot product with a consistent sign.
		var centre = (points[seed[0]] + points[seed[1]] + points[seed[2]] + points[seed[3]]) / 4f;

		for ( var i = 0; i < faces.Count; i++ )
		{
			if ( SignedDistance( points, faces[i], centre ) > 0f )
				faces[i] = new HullFace( faces[i].A, faces[i].C, faces[i].B );
		}

		var used = new HashSet<int>( seed );
		var scale = Extent( points );

		for ( var p = 0; p < points.Count; p++ )
		{
			if ( used.Contains( p ) )
				continue;

			var distances = new float[faces.Count];

			for ( var f = 0; f < faces.Count; f++ )
				distances[f] = SignedDistance( points, faces[f], points[p] );

			// A face counts as visible when the point is in front of it. The old test used
			// +tolerance, so on a flat run — a blockout limb is mostly flat — faces the point was
			// only just in front of dropped out of the cap, the cap split, and stitching the two
			// loops pinched the hull. The band below zero is only float noise: a face the point is
			// actually behind is not visible, and deleting it dents the hull.
			//
			// If that cap's boundary is not one loop, neighbours out to the caller's tolerance are
			// flooded in and the loop is rebuilt. A stitch that would leave an earlier vertex
			// outside the new faces is thrown away; the point is within tolerance, and a bad
			// stitch is permanent.
			var band = tolerance * scale;
			var numeric = MathF.Max( scale * 1e-6f, 1e-6f );
			var edgeFaces = FaceEdges( faces );
			List<int> visible = null;
			List<(int A, int B)> horizon = null;

			for ( var pass = 0; pass < 2; pass++ )
			{
				var flood = pass == 0 ? numeric : band;
				var cap = VisibleCap( faces, edgeFaces, distances, numeric, flood );

				if ( cap.Count == 0 )
					break;

				var loop = HorizonLoop( faces, cap );

				if ( loop is null )
					continue;

				if ( !ConeKeepsUsed( points, used, loop, p, MathF.Max( band, numeric * 50f ) ) )
					continue;

				visible = cap;
				horizon = loop;
				break;
			}

			if ( horizon is null )
				continue;

			visible.Sort();

			for ( var v = visible.Count - 1; v >= 0; v-- )
				faces.RemoveAt( visible[v] );

			foreach ( var (a, b) in horizon )
				faces.Add( new HullFace( a, b, p ) );

			used.Add( p );
		}

		// Only the points that ended up on the hull. A caller storing this as a collision shape does
		// not want the interior ones, and a physics engine will discard them anyway.
		var keep = new List<int>();
		var remap = new Dictionary<int, int>();

		foreach ( var face in faces )
		{
			foreach ( var index in new[] { face.A, face.B, face.C } )
			{
				if ( remap.ContainsKey( index ) )
					continue;

				remap[index] = keep.Count;
				keep.Add( index );
			}
		}

		var hullPoints = new List<Vec3>( keep.Count );

		foreach ( var index in keep )
			hullPoints.Add( points[index] );

		var hullFaces = new List<HullFace>( faces.Count );

		foreach ( var face in faces )
			hullFaces.Add( new HullFace( remap[face.A], remap[face.B], remap[face.C] ) );

		return (hullPoints, hullFaces);
	}

	/// <summary>The hull as a mesh, for looking at and for measuring.</summary>
	public static PolyMesh ToMesh( IReadOnlyList<Vec3> points, float tolerance = 1e-6f )
	{
		if ( Build( points, tolerance ) is not { } hull )
			return null;

		var mesh = new PolyMesh();

		foreach ( var p in hull.Points )
			mesh.AddVertex( p );

		foreach ( var face in hull.Faces )
			mesh.AddFace( new[] { face.A, face.B, face.C } );

		return mesh;
	}

	/// <summary>
	/// Faces the point is clearly outside of, plus the near-coplanar neighbours that keep that set
	/// one disk. <paramref name="seedBand"/> is the "clearly outside" line; <paramref name="floodBand"/>
	/// is how far past the plane a neighbour may sit and still be part of the same cap.
	/// </summary>
	static List<int> VisibleCap( List<HullFace> faces, Dictionary<EdgeKey, List<int>> edgeFaces, float[] distances,
		float seedBand, float floodBand )
	{
		var seen = new bool[faces.Count];
		var queue = new Queue<int>();

		for ( var f = 0; f < faces.Count; f++ )
		{
			if ( distances[f] > seedBand )
			{
				seen[f] = true;
				queue.Enqueue( f );
			}
		}

		var cap = new List<int>();

		while ( queue.Count > 0 )
		{
			var f = queue.Dequeue();
			cap.Add( f );
			var face = faces[f];

			GrowCap( edgeFaces, distances, floodBand, seen, queue, face.A, face.B, f );
			GrowCap( edgeFaces, distances, floodBand, seen, queue, face.B, face.C, f );
			GrowCap( edgeFaces, distances, floodBand, seen, queue, face.C, face.A, f );
		}

		return cap;
	}

	static void GrowCap( Dictionary<EdgeKey, List<int>> edgeFaces, float[] distances, float floodBand, bool[] seen,
		Queue<int> queue, int a, int b, int from )
	{
		if ( !edgeFaces.TryGetValue( new EdgeKey( a, b ), out var share ) )
			return;

		foreach ( var other in share )
		{
			if ( other == from || seen[other] )
				continue;

			if ( distances[other] <= -floodBand )
				continue;

			seen[other] = true;
			queue.Enqueue( other );
		}
	}

	static Dictionary<EdgeKey, List<int>> FaceEdges( List<HullFace> faces )
	{
		var map = new Dictionary<EdgeKey, List<int>>();

		for ( var f = 0; f < faces.Count; f++ )
		{
			var face = faces[f];

			AddEdgeFace( map, face.A, face.B, f );
			AddEdgeFace( map, face.B, face.C, f );
			AddEdgeFace( map, face.C, face.A, f );
		}

		return map;
	}

	static void AddEdgeFace( Dictionary<EdgeKey, List<int>> map, int a, int b, int face )
	{
		var key = new EdgeKey( a, b );

		if ( !map.TryGetValue( key, out var list ) )
		{
			list = new List<int>( 2 );
			map[key] = list;
		}

		list.Add( face );
	}

	/// <summary>
	/// The boundary of the visible cap, one directed edge per undirected edge. Two visible faces
	/// walk a shared edge in opposite directions, so it cancels; the same direction twice is a
	/// duplicated face and is kept once. Returns null when that boundary is not exactly one loop
	/// — every vertex once as a start and once as an end, and one component.
	/// </summary>
	static List<(int A, int B)> HorizonLoop( List<HullFace> faces, List<int> visible )
	{
		var edges = new Dictionary<EdgeKey, (int A, int B)>();

		foreach ( var index in visible )
		{
			var face = faces[index];

			TakeEdge( edges, face.A, face.B );
			TakeEdge( edges, face.B, face.C );
			TakeEdge( edges, face.C, face.A );
		}

		if ( edges.Count < 3 )
			return null;

		var next = new Dictionary<int, int>( edges.Count );
		var incoming = new HashSet<int>();

		foreach ( var (a, b) in edges.Values )
		{
			if ( a == b || !next.TryAdd( a, b ) || !incoming.Add( b ) )
				return null;
		}

		if ( incoming.Count != next.Count )
			return null;

		var start = 0;
		var haveStart = false;

		foreach ( var (a, _) in edges.Values )
		{
			start = a;
			haveStart = true;
			break;
		}

		if ( !haveStart )
			return null;

		var cursor = start;
		var walked = 0;

		do
		{
			if ( !next.TryGetValue( cursor, out cursor ) )
				return null;

			walked++;

			if ( walked > edges.Count )
				return null;
		}
		while ( cursor != start );

		if ( walked != edges.Count )
			return null;

		var loop = new List<(int A, int B)>( edges.Count );

		foreach ( var edge in edges.Values )
			loop.Add( edge );

		return loop;
	}

	/// <summary>Every vertex already on the hull has to stay behind the new fan, to within
	/// <paramref name="slack"/>. A silhouette that isn't the one the point can see cuts the hull.</summary>
	static bool ConeKeepsUsed( IReadOnlyList<Vec3> points, HashSet<int> used, List<(int A, int B)> horizon, int p, float slack )
	{
		foreach ( var (a, b) in horizon )
		{
			var face = new HullFace( a, b, p );

			foreach ( var u in used )
			{
				if ( SignedDistance( points, face, points[u] ) > slack )
					return false;
			}
		}

		return true;
	}

	static void TakeEdge( Dictionary<EdgeKey, (int A, int B)> edges, int a, int b )
	{
		var key = new EdgeKey( a, b );

		if ( !edges.TryGetValue( key, out var existing ) )
		{
			edges[key] = (a, b);
			return;
		}

		// Opposite direction: interior to the cap. Same direction: a second copy of one face.
		if ( existing.A == b && existing.B == a )
			edges.Remove( key );
	}

	static float SignedDistance( IReadOnlyList<Vec3> points, HullFace face, Vec3 p )
	{
		var a = points[face.A];
		var normal = Vec3.Cross( points[face.B] - a, points[face.C] - a );

		return normal.LengthSquared < 1e-20f ? 0f : Vec3.Dot( normal.Normal, p - a );
	}

	static float Extent( IReadOnlyList<Vec3> points )
	{
		var min = points[0];
		var max = points[0];

		foreach ( var p in points )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		var size = (max - min).Length;
		return size > 1e-9f ? size : 1f;
	}

	/// <summary>
	/// Four points that actually enclose a volume: the two furthest apart, the one furthest from the
	/// line between them, then the one furthest from that plane.
	///
	/// Picked by extent rather than by taking the first four, because the first four points of a CAD
	/// mesh are routinely one face of it — coplanar, and no tetrahedron at all.
	/// </summary>
	static bool StartingTetrahedron( IReadOnlyList<Vec3> points, float tolerance, out int[] seed )
	{
		seed = null;

		var scale = Extent( points );
		// The first two scores are squared lengths. Comparing them to the linear tolerance refuses
		// a perfectly good tetrahedron whose longest edge is shorter than the square root of it.
		var tol = tolerance * scale;
		var tol2 = tol * tol;
		int a = 0, b = 0;
		var best = -1f;

		for ( var i = 0; i < points.Count; i++ )
		{
			for ( var j = i + 1; j < points.Count; j++ )
			{
				var d = (points[i] - points[j]).LengthSquared;

				if ( d > best )
				{
					best = d;
					a = i;
					b = j;
				}
			}
		}

		if ( best <= tol2 )
			return false;

		var axis = (points[b] - points[a]).Normal;
		var c = -1;
		best = -1f;

		for ( var i = 0; i < points.Count; i++ )
		{
			var offset = points[i] - points[a];
			var away = (offset - axis * Vec3.Dot( offset, axis )).LengthSquared;

			if ( away > best )
			{
				best = away;
				c = i;
			}
		}

		if ( c < 0 || best <= tol2 )
			return false;

		var normal = Vec3.Cross( points[b] - points[a], points[c] - points[a] );

		if ( normal.LengthSquared < 1e-20f )
			return false;

		normal = normal.Normal;

		var apex = -1;
		best = -1f;

		for ( var i = 0; i < points.Count; i++ )
		{
			var height = MathF.Abs( Vec3.Dot( points[i] - points[a], normal ) );

			if ( height > best )
			{
				best = height;
				apex = i;
			}
		}

		if ( apex < 0 || best <= tol )
			return false;

		seed = new[] { a, b, c, apex };
		return true;
	}
}
