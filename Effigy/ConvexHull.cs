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

			var visible = new List<int>();

			for ( var f = 0; f < faces.Count; f++ )
			{
				if ( SignedDistance( points, faces[f], points[p] ) > tolerance * scale )
					visible.Add( f );
			}

			if ( visible.Count == 0 )
				continue;

			// The horizon: every edge of the visible region that the invisible region also owns. Those
			// are the edges the new point stitches to; an edge shared by two visible faces is interior
			// to the hole and goes with them.
			var horizon = new List<(int A, int B)>();
			var visibleSet = new HashSet<int>( visible );

			foreach ( var f in visible )
			{
				var face = faces[f];

				AddIfHorizon( faces, visibleSet, points, horizon, face.A, face.B );
				AddIfHorizon( faces, visibleSet, points, horizon, face.B, face.C );
				AddIfHorizon( faces, visibleSet, points, horizon, face.C, face.A );
			}

			if ( horizon.Count == 0 )
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

	static void AddIfHorizon( List<HullFace> faces, HashSet<int> visible, IReadOnlyList<Vec3> points,
		List<(int A, int B)> horizon, int a, int b )
	{
		for ( var f = 0; f < faces.Count; f++ )
		{
			if ( visible.Contains( f ) )
				continue;

			var face = faces[f];

			// The neighbour walks the shared edge the other way round, which is what identifies it.
			if ( (face.A == b && face.B == a) || (face.B == b && face.C == a) || (face.C == b && face.A == a) )
			{
				horizon.Add( (a, b) );
				return;
			}
		}
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

		if ( best <= tolerance * scale )
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

		if ( c < 0 || best <= tolerance * scale )
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

		if ( apex < 0 || best <= tolerance * scale )
			return false;

		seed = new[] { a, b, c, apex };
		return true;
	}
}
