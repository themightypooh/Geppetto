using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Put a face back together after something has cut it into coplanar pieces.
///
/// WHY THIS EXISTS, AND WHY THE EXISTING REPAIR DOES NOT COVER IT. Two things can happen to the
/// face a boolean cuts through, and they need opposite fixes. If the engine returns it as ONE loop
/// that runs out to the hole and back along a seam, that is a bridged face and
/// Triangulate.SplitBridgedFace takes it apart into the n+1 n-gons a face with n holes has to be.
/// If instead the engine returns it as MANY separate faces that happen to share a plane, nothing
/// looked at it at all - and that is the case measured on a real part: an extrude cut into the side
/// of a body came back as 88 triangles and quads lying in a single plane, with `bridged faces: 0`.
///
/// Both produce a mesh that is closed, manifold, Euler-correct and exactly the right volume. The
/// cost is paid somewhere no geometric check looks: a Face is the unit of SELECTION and of material
/// assignment, so clicking that wall to paint it painted one fragment out of 88. Same lesson as the
/// bridged-face work, arriving from the other side.
///
/// So this walks the other direction from Triangulate.SplitBridgedFace. Faces that share an edge,
/// share a plane and share a material slot are one surface that got fragmented, and they are welded
/// back into the largest n-gons that surface can be - one, or n+1 when the surface has n holes in
/// it, which is the same floor the split path lands on and for the same reason: a face is one loop
/// of corners, so a surface with a hole in it is never one face at any price.
///
/// IT REFUSES RATHER THAN GUESSES, exactly like MeshHoleRepair. A group whose boundary does not
/// chain into clean loops, or whose merged area does not add up to the pieces it replaced, is left
/// as it was. A wrong merge is a self-intersecting face that passes every validator, and coarse
/// geometry that selects badly is far better than geometry that is quietly wrong.
///
/// MATERIAL SLOTS ARE PART OF THE IDENTITY, not an afterthought. Two coplanar neighbours painted
/// different colours are two faces because the user made them two faces, and merging them would
/// throw away the assignment being protected here.
/// </summary>
public static class CoplanarMerge
{
	/// <summary>Cosine limit for "these two faces lie in the same plane". Compared on the signed
	/// dot rather than its absolute value: two fragments of one surface face the same way, and a
	/// pair that face opposite ways are the two sides of a zero-thickness sliver, which must never
	/// be welded into one face.</summary>
	const float NormalTolerance = 0.9995f;

	/// <summary>How far apart the two planes may sit and still be one plane. A float-drift
	/// tolerance, not a modelling one - the fragments are exactly coplanar in exact arithmetic and
	/// a few ulps apart after a boolean has recomputed their corners.</summary>
	const float PlaneTolerance = 1e-3f;

	/// <summary>How far the merged area may drift from the area of the pieces it replaces, as a
	/// fraction. The merge is a repartition of the same surface, so the two agree to rounding or
	/// the loops were chained wrong.</summary>
	const float AreaTolerance = 1e-3f;

	/// <summary>
	/// Weld every fragmented surface in the mesh back together, in place. Returns how many faces
	/// were removed - zero when there was nothing to do, so a caller can report having acted
	/// rather than guessing.
	/// </summary>
	public static int Merge( PolyMesh mesh )
	{
		if ( mesh is null || mesh.FaceCount < 2 )
			return 0;

		var groups = Group( mesh );

		if ( groups is null )
			return 0;

		// EVERY GROUP IS DECIDED BEFORE ANY FACE MOVES. Rebuilding the list once at the end keeps
		// face indices - which the edge map and the grouping both hold - valid the whole way
		// through, and means a group that refuses halfway leaves nothing behind.
		var replacements = new Dictionary<int, List<Face>>();

		foreach ( var (root, members) in groups )
		{
			if ( members.Count < 2 )
				continue;

			var merged = TryMerge( mesh, members );

			if ( merged is not null )
				replacements[root] = merged;
		}

		if ( replacements.Count == 0 )
			return 0;

		var before = mesh.FaceCount;
		var replaced = new HashSet<int>();

		foreach ( var (root, _) in replacements )
		{
			foreach ( var index in groups[root] )
				replaced.Add( index );
		}

		var faces = new List<Face>( mesh.FaceCount );

		for ( var i = 0; i < mesh.FaceCount; i++ )
		{
			if ( !replaced.Contains( i ) )
				faces.Add( mesh.Faces[i] );
		}

		foreach ( var (_, merged) in replacements )
			faces.AddRange( merged );

		mesh.Faces = faces;

		return before - mesh.FaceCount;
	}

	// --- grouping -----------------------------------------------------------------------------

	/// <summary>
	/// The biggest group of faces that are still one surface pretending to be several. Zero or one
	/// means nothing is fragmented; anything higher is how many clicks it takes to paint the worst
	/// face on the model.
	///
	/// PUBLIC BECAUSE THE DEFECT WAS INVISIBLE. Every existing measure - closed, manifold, Euler,
	/// volume, largest face - passed on a mesh with 88 fragments of one wall in it. This is the
	/// number that would have shown it, so the diagnostics print it.
	/// </summary>
	public static int LargestFragmentedSurface( PolyMesh mesh )
	{
		if ( mesh is null || mesh.FaceCount < 2 )
			return 0;

		var groups = Group( mesh );
		var largest = 0;

		if ( groups is not null )
		{
			foreach ( var (_, members) in groups )
				largest = Math.Max( largest, members.Count );
		}

		return largest;
	}

	/// <summary>
	/// Union faces that share an edge, a plane and a material into connected surfaces.
	///
	/// Adjacency comes off the shared edge rather than off the plane alone, because two fragments
	/// of the same plane on opposite sides of a part are not one face however well their planes
	/// agree - the top of a step and the floor it steps down to can be coplanar and are separate
	/// surfaces with a gap between them.
	/// </summary>
	static Dictionary<int, List<int>> Group( PolyMesh mesh )
	{
		var normals = new Vec3[mesh.FaceCount];
		var offsets = new float[mesh.FaceCount];
		var usable = new bool[mesh.FaceCount];

		for ( var i = 0; i < mesh.FaceCount; i++ )
		{
			var face = mesh.Faces[i];

			if ( face.Count < 3 )
				continue;

			var normal = mesh.FaceNormal( face );

			if ( normal.LengthSquared < 1e-20f )
				continue;

			normals[i] = normal.Normal;
			offsets[i] = Vec3.Dot( normals[i], mesh.FaceCentroid( face ) );
			usable[i] = true;
		}

		var parent = new int[mesh.FaceCount];

		for ( var i = 0; i < parent.Length; i++ )
			parent[i] = i;

		var merged = false;

		foreach ( var (_, faces) in mesh.BuildEdgeFaces() )
		{
			// Only a manifold edge joins two faces into one surface. A boundary edge has nothing on
			// the other side, and a non-manifold one has no single other side to pick.
			if ( faces.Count != 2 )
				continue;

			var a = faces[0];
			var b = faces[1];

			if ( !usable[a] || !usable[b] )
				continue;

			if ( mesh.Faces[a].Material != mesh.Faces[b].Material )
				continue;

			if ( Vec3.Dot( normals[a], normals[b] ) < NormalTolerance )
				continue;

			if ( MathF.Abs( offsets[a] - offsets[b] ) > PlaneTolerance )
				continue;

			if ( Union( parent, a, b ) )
				merged = true;
		}

		if ( !merged )
			return null;

		var groups = new Dictionary<int, List<int>>();

		for ( var i = 0; i < mesh.FaceCount; i++ )
		{
			if ( !usable[i] )
				continue;

			var root = Find( parent, i );

			if ( !groups.TryGetValue( root, out var list ) )
				groups[root] = list = new List<int>();

			list.Add( i );
		}

		return groups;
	}

	static int Find( int[] parent, int i )
	{
		while ( parent[i] != i )
			i = parent[i] = parent[parent[i]];

		return i;
	}

	static bool Union( int[] parent, int a, int b )
	{
		var ra = Find( parent, a );
		var rb = Find( parent, b );

		if ( ra == rb )
			return false;

		parent[rb] = ra;
		return true;
	}

	// --- merging one group --------------------------------------------------------------------

	/// <summary>
	/// The faces one fragmented surface should have become, or null to leave it alone.
	///
	/// Null at the first thing that cannot be established rather than at the first thing that looks
	/// wrong - everything here comes out of a boolean, and the difference between "this is not a
	/// simple planar patch" and "this is a simple planar patch I have mis-walked" is not something
	/// the code can tell from the inside.
	/// </summary>
	static List<Face> TryMerge( PolyMesh mesh, List<int> members )
	{
		var loops = BoundaryLoops( mesh, members );

		if ( loops is null || loops.Count == 0 )
			return null;

		var material = mesh.Faces[members[0]].Material;
		var normal = GroupNormal( mesh, members );

		if ( normal.LengthSquared < 1e-20f )
			return null;

		normal = normal.Normal;

		Basis( normal, out var u, out var v );

		var flat = new List<List<Vec2>>( loops.Count );

		foreach ( var loop in loops )
		{
			var points = new List<Vec2>( loop.Count );

			foreach ( var index in loop )
				points.Add( Flatten( mesh.Positions[index], u, v ) );

			flat.Add( points );
		}

		// The surface's own area, measured off the pieces, to check the answer against.
		var target = 0f;

		foreach ( var index in members )
		{
			var face = mesh.Faces[index];
			var points = new List<Vec2>( face.Count );

			foreach ( var corner in face.Indices )
				points.Add( Flatten( mesh.Positions[corner], u, v ) );

			target += MathF.Abs( SignedArea( points ) );
		}

		var uvs = CornerUVs( mesh, members );

		// ONE LOOP: a plain patch with no hole in it, which is one n-gon.
		if ( loops.Count == 1 )
		{
			var loop = loops[0];

			if ( SignedArea( flat[0] ) < 0f )
			{
				loop = new List<int>( loop );
				loop.Reverse();
			}

			var face = BuildFace( loop, uvs, material );

			return face is not null && Agrees( AreaOf( mesh, loop, u, v ), target )
				? new List<Face> { face }
				: null;
		}

		// SEVERAL LOOPS: an outer boundary and n holes, which is n+1 faces. Same floor
		// Triangulate.SplitBridgedFace lands on, reached from the other direction.
		var outer = OuterLoop( flat );

		if ( outer < 0 )
			return null;

		var outerPoints = flat[outer];
		var holePoints = new List<IReadOnlyList<Vec2>>( flat.Count - 1 );
		var holeLoops = new List<List<int>>( flat.Count - 1 );

		for ( var i = 0; i < flat.Count; i++ )
		{
			if ( i == outer )
				continue;

			holePoints.Add( flat[i] );
			holeLoops.Add( loops[i] );
		}

		var split = Triangulate.SplitWithHoles( outerPoints, holePoints );

		if ( split is null || split.Count == 0 )
			return null;

		// SplitWithHoles indexes the concatenated list it was handed - outer first, then each hole
		// in order - so the map back is positional, the same contract MeshHoleRepair relies on.
		var combined = new List<int>( loops[outer] );

		foreach ( var hole in holeLoops )
			combined.AddRange( hole );

		var built = new List<Face>( split.Count );
		var area = 0f;

		foreach ( var piece in split )
		{
			var loop = new List<int>( piece.Count );

			foreach ( var i in piece )
				loop.Add( combined[i] );

			var face = BuildFace( loop, uvs, material );

			if ( face is null )
				return null;

			built.Add( face );
			area += AreaOf( mesh, loop, u, v );
		}

		return Agrees( area, target ) ? built : null;
	}

	/// <summary>Null for anything PolyMesh would be right to reject - too few corners, or a corner
	/// visited twice, which is the pinched face this whole path exists to avoid producing.</summary>
	static Face BuildFace( List<int> loop, Dictionary<int, Vec2> uvs, int material )
	{
		if ( loop.Count < 3 || loop.Distinct().Count() != loop.Count )
			return null;

		var indices = loop.ToArray();
		var corners = new Vec2[indices.Length];

		for ( var i = 0; i < indices.Length; i++ )
			corners[i] = uvs.TryGetValue( indices[i], out var uv ) ? uv : Vec2.Zero;

		return new Face( indices, corners, material );
	}

	/// <summary>
	/// Chain the group's boundary into closed loops, or null if it is not a clean planar patch.
	///
	/// A boundary edge is one used by exactly one face OF THE GROUP: an edge shared by two members
	/// is interior and disappears into the merged face, and an edge shared with a face outside the
	/// group is the surface's own rim. Anything used more than twice is non-manifold within the
	/// patch and there is no single boundary to walk.
	/// </summary>
	static List<List<int>> BoundaryLoops( PolyMesh mesh, List<int> members )
	{
		var counts = new Dictionary<EdgeKey, int>();

		foreach ( var index in members )
		{
			var face = mesh.Faces[index];

			for ( var i = 0; i < face.Count; i++ )
			{
				var key = new EdgeKey( face.Indices[i], face.Indices[(i + 1) % face.Count] );

				counts.TryGetValue( key, out var count );

				if ( count >= 2 )
					return null;

				counts[key] = count + 1;
			}
		}

		var atVertex = new Dictionary<int, List<int>>();

		foreach ( var (key, count) in counts )
		{
			if ( count != 1 )
				continue;

			Link( key.A, key.B );
			Link( key.B, key.A );
		}

		// Every boundary vertex needs exactly two boundary edges or the walk has a choice to make,
		// and a choice here is a guess. Same refusal MeshHoleRepair makes for the same reason.
		foreach ( var (_, neighbours) in atVertex )
		{
			if ( neighbours.Count != 2 )
				return null;
		}

		var loops = new List<List<int>>();
		var visited = new HashSet<int>();

		foreach ( var start in atVertex.Keys )
		{
			if ( !visited.Add( start ) )
				continue;

			var loop = new List<int> { start };
			var current = start;
			var previous = -1;

			while ( true )
			{
				var neighbours = atVertex[current];
				var next = neighbours[0] == previous ? neighbours[1] : neighbours[0];

				if ( next == start )
					break;

				if ( !visited.Add( next ) )
					return null;

				loop.Add( next );

				previous = current;
				current = next;

				// A patch cannot have a boundary longer than every boundary vertex it owns.
				if ( loop.Count > atVertex.Count )
					return null;
			}

			if ( loop.Count < 3 )
				return null;

			loops.Add( loop );
		}

		return loops;

		void Link( int from, int to )
		{
			if ( !atVertex.TryGetValue( from, out var list ) )
				atVertex[from] = list = new List<int>( 2 );

			if ( !list.Contains( to ) )
				list.Add( to );
		}
	}

	/// <summary>
	/// Which loop contains all the others. Area alone is not enough - a loop can be larger than
	/// another and beside it rather than around it - so containment is tested outright, and a set
	/// of loops with no single container is refused.
	/// </summary>
	static int OuterLoop( List<List<Vec2>> loops )
	{
		for ( var i = 0; i < loops.Count; i++ )
		{
			var contains = true;

			for ( var j = 0; j < loops.Count && contains; j++ )
			{
				if ( i == j )
					continue;

				foreach ( var point in loops[j] )
				{
					if ( PointInPolygon( loops[i], point ) )
						continue;

					contains = false;
					break;
				}
			}

			if ( contains )
				return i;
		}

		return -1;
	}

	/// <summary>Area-weighted normal of the whole group, so one tiny fragment with a noisy normal
	/// cannot tilt the plane every corner is about to be flattened onto.</summary>
	static Vec3 GroupNormal( PolyMesh mesh, List<int> members )
	{
		var sum = Vec3.Zero;

		foreach ( var index in members )
		{
			var face = mesh.Faces[index];

			if ( face.Count < 3 )
				continue;

			// FaceNormal normalises before returning, so the weighting has to be applied here -
			// summing them raw would let a sliver count as much as the face it sits beside.
			sum += mesh.FaceNormal( face ) * mesh.FaceArea( face );
		}

		return sum;
	}

	/// <summary>
	/// One UV per vertex, taken from the group's own corners.
	///
	/// A fragmented surface is one planar patch that the boolean gave a single projected mapping,
	/// so every fragment agrees about the UV at a shared corner and the first answer found is the
	/// answer. Per-corner UVs exist for seams between faces that DISAGREE, and a seam inside one
	/// flat surface is exactly what is being removed here.
	/// </summary>
	static Dictionary<int, Vec2> CornerUVs( PolyMesh mesh, List<int> members )
	{
		var uvs = new Dictionary<int, Vec2>();

		foreach ( var index in members )
		{
			var face = mesh.Faces[index];

			for ( var i = 0; i < face.Count; i++ )
			{
				var vertex = face.Indices[i];

				if ( !uvs.ContainsKey( vertex ) && face.UVs is not null && i < face.UVs.Length )
					uvs[vertex] = face.UVs[i];
			}
		}

		return uvs;
	}

	static float AreaOf( PolyMesh mesh, List<int> loop, Vec3 u, Vec3 v )
	{
		var points = new List<Vec2>( loop.Count );

		foreach ( var index in loop )
			points.Add( Flatten( mesh.Positions[index], u, v ) );

		return MathF.Abs( SignedArea( points ) );
	}

	static bool Agrees( float area, float target ) =>
		MathF.Abs( area - target ) <= AreaTolerance * MathF.Max( target, 1f );

	static float SignedArea( List<Vec2> points )
	{
		var sum = 0f;

		for ( int i = 0, j = points.Count - 1; i < points.Count; j = i++ )
			sum += (points[j].x - points[i].x) * (points[j].y + points[i].y);

		return sum * 0.5f;
	}

	/// <summary>Right-handed, so u cross v is the normal and a counter-clockwise loop in this basis
	/// is one wound the way the surface faces. Same construction as MeshHoleRepair's.</summary>
	static void Basis( Vec3 normal, out Vec3 u, out Vec3 v )
	{
		var n = normal.Normal;
		var seed = MathF.Abs( n.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );

		u = Vec3.Cross( seed, n ).Normal;
		v = Vec3.Cross( n, u );
	}

	static Vec2 Flatten( Vec3 p, Vec3 u, Vec3 v ) => new( Vec3.Dot( p, u ), Vec3.Dot( p, v ) );

	static bool PointInPolygon( List<Vec2> polygon, Vec2 point )
	{
		var inside = false;

		for ( int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++ )
		{
			var a = polygon[i];
			var b = polygon[j];

			if ( a.y > point.y != b.y > point.y
				&& point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x )
				inside = !inside;
		}

		return inside;
	}
}
