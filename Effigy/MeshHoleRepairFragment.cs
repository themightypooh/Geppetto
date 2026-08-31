using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Closing a mouth that lies inside a surface which has already been taken apart — cutting a body
/// that has been cut before.
///
/// WHY THE OTHER THREE ALL DECLINE IT. The single-face repair closes a hole by triangulating the
/// face around it, so the surface it fixed is a fan afterwards. Cut into that same surface again
/// and the second mouth lands across a dozen coplanar triangles: no single face contains it, there
/// are far more than two candidates, and the crossings where it passes from one triangle to the
/// next are ordinary points on those triangles' edges rather than vertices — which is the one thing
/// MeshHoleRepairCurved refuses without, because inventing a crossing means splitting a face it was
/// not asked to touch.
///
/// THE ANSWER IS TO STOP TREATING THE FRAGMENTS AS FACES. They are one surface that a previous
/// repair happened to leave in pieces, and the mouth is a hole in that SURFACE. So the whole
/// coplanar group is taken as one region — outer contour, plus whatever holes it already has — the
/// new mouth is added as one more hole, and the group is rebuilt from the loops. No crossing has to
/// be named, because the mouth never crosses anything: it is strictly inside the region.
///
/// THAT MAKES THIS THE LAST RESORT AND IT IS ORDERED LAST FOR A REASON. Rebuilding the group throws
/// away the partition the fragments had. Where the earlier repairs apply, they preserve it — the
/// span repair notches two faces and leaves them two faces — and this one would flatten it. So it
/// only ever sees loops all three of the others walked away from.
///
/// IT REFUSES THE SAME WAY THEY DO. The group must be coplanar with the loop, share one material,
/// have exactly one outer contour, and contain the mouth strictly inside it and outside every hole
/// it already has. And the result is measured before it is kept — see CloseLoopsInFragments.
/// </summary>
public static class MeshHoleRepairFragment
{
	const float PlaneTolerance = 1e-3f;
	const float NormalTolerance = 0.999f;

	/// <summary>
	/// Close every mouth lying inside a fragmented coplanar surface, and return how many.
	///
	/// Run LAST. Each repair is applied to a copy and rolled back unless it strictly reduced the
	/// open boundary without making anything non-manifold — the same guarantee MeshHoleRepairCurved
	/// gives, and for the same reason: rebuilding a group of faces is far too big a hammer to swing
	/// on trust.
	/// </summary>
	public static int CloseLoopsInFragments( PolyMesh mesh )
	{
		if ( mesh is null || mesh.FaceCount == 0 )
			return 0;

		var closed = 0;

		while ( true )
		{
			var loops = BoundaryLoops( mesh );
			var progressed = false;

			foreach ( var loop in loops )
			{
				if ( loop.Count < 3 )
					continue;

				var before = MeshValidator.Validate( mesh );
				var snapshot = mesh.Faces;

				mesh.Faces = CloneFaces( snapshot );

				if ( !TryCloseInGroup( mesh, loop ) )
				{
					mesh.Faces = snapshot;
					continue;
				}

				var after = MeshValidator.Validate( mesh );

				if ( after.BoundaryEdges >= before.BoundaryEdges
					|| after.NonManifoldEdges > before.NonManifoldEdges
					|| !after.IsValid )
				{
					mesh.Faces = snapshot;
					continue;
				}

				closed++;
				progressed = true;
				break;
			}

			if ( !progressed )
				return closed;
		}
	}

	static List<Face> CloneFaces( List<Face> faces )
	{
		var copy = new List<Face>( faces.Count );

		foreach ( var f in faces )
			copy.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );

		return copy;
	}

	// --- the repair -------------------------------------------------------------------------------

	static bool TryCloseInGroup( PolyMesh mesh, List<int> loop )
	{
		var normal = LoopNormal( mesh, loop );

		// A mouth with no plane of its own is the curved case, and belongs to the repair that reads
		// its answer off the wall rather than off a plane.
		if ( normal.LengthSquared < 1e-20f )
			return false;

		normal = normal.Normal;

		Basis( normal, out var u, out var v );

		var plane = Vec3.Dot( mesh.Positions[loop[0]], normal );
		var group = CoplanarGroup( mesh, loop, normal, plane, out var material );

		// One face is the single-face repair's own case, and it does it better: it splices the hole
		// into that face and leaves the rest of the mesh alone.
		if ( group.Count < 2 )
			return false;

		if ( !GroupBoundary( mesh, group, out var rings ) )
			return false;

		// Flattened once, here, so every containment test below is asking about the same numbers.
		var flat = new List<List<Vec2>>( rings.Count );

		foreach ( var ring in rings )
		{
			var polygon = new List<Vec2>( ring.Count );

			foreach ( var index in ring )
				polygon.Add( Flatten( mesh.Positions[index], u, v ) );

			flat.Add( polygon );
		}

		// The biggest ring is the outer contour and the rest are holes the surface already had. A
		// group with no ring big enough to hold the others is not one surface.
		var outer = 0;

		for ( var i = 1; i < flat.Count; i++ )
		{
			if ( MathF.Abs( SignedArea( flat[i] ) ) > MathF.Abs( SignedArea( flat[outer] ) ) )
				outer = i;
		}

		var mouth = new List<Vec2>( loop.Count );

		foreach ( var index in loop )
			mouth.Add( Flatten( mesh.Positions[index], u, v ) );

		// STRICTLY INSIDE THE CONTOUR AND OUTSIDE EVERY EXISTING HOLE. A mouth failing either is not
		// a hole in this surface, and closing it would seal something that belongs elsewhere.
		foreach ( var p in mouth )
		{
			if ( !PointInPolygon( flat[outer], p ) )
				return false;
		}

		for ( var i = 0; i < flat.Count; i++ )
		{
			if ( i == outer )
				continue;

			foreach ( var p in mouth )
			{
				if ( PointInPolygon( flat[i], p ) )
					return false;
			}
		}

		// The loops, in the order the triangulator indexes them: outer first, then every hole the
		// surface already had, then the new mouth.
		var holeRings = new List<List<int>>();
		var holePolygons = new List<IReadOnlyList<Vec2>>();

		for ( var i = 0; i < flat.Count; i++ )
		{
			if ( i == outer )
				continue;

			holeRings.Add( rings[i] );
			holePolygons.Add( flat[i] );
		}

		holeRings.Add( loop );
		holePolygons.Add( mouth );

		var combined = new List<int>( rings[outer] );

		foreach ( var ring in holeRings )
			combined.AddRange( ring );

		// N-GONS RATHER THAN A FAN WHERE THE SHAPE ALLOWS IT, for the reason Triangulate.SplitWithHoles
		// gives: a Face is the unit of selection and of material assignment, so a surface returned as
		// thirty triangles is a surface you paint a thirtieth of per click. Falling back to the ear
		// clipper when it will not split is right - coarse and correct beats refusing.
		var faces = new List<int[]>();
		var split = Triangulate.SplitWithHoles( flat[outer], holePolygons );

		if ( split is { Count: > 0 } )
		{
			foreach ( var ring in split )
				faces.Add( Map( ring, combined ) );
		}
		else
		{
			var triangles = Triangulate.WithHoles( flat[outer], holePolygons );

			if ( triangles.Count == 0 )
				return false;

			foreach ( var (a, b, c) in triangles )
				faces.Add( Map( new List<int> { a, b, c }, combined ) );
		}

		var uvs = KnownUVs( mesh, group );

		Basis( normal, out var mapU, out var mapV );

		if ( !UVMap( mesh, group, mapU, mapV, out var origin, out var originUv, out var scaleU, out var scaleV ) )
			return false;

		var rebuilt = new List<Face>( mesh.FaceCount + faces.Count );
		var members = new HashSet<int>( group );
		var written = false;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( !members.Contains( fi ) )
			{
				rebuilt.Add( mesh.Faces[fi] );
				continue;
			}

			if ( written )
				continue;

			written = true;

			foreach ( var indices in faces )
			{
				if ( indices.Length < 3 || !Distinct( indices ) )
					return false;

				var corners = new Vec2[indices.Length];

				for ( var i = 0; i < indices.Length; i++ )
				{
					if ( uvs.TryGetValue( indices[i], out var existing ) )
					{
						corners[i] = existing;
						continue;
					}

					var p = Flatten( mesh.Positions[indices[i]], mapU, mapV ) - origin;
					corners[i] = new Vec2( originUv.x + p.x * scaleU, originUv.y + p.y * scaleV );
				}

				rebuilt.Add( new Face( indices, corners, material ) );
			}
		}

		mesh.Faces = rebuilt;
		return true;
	}

	static int[] Map( List<int> ring, List<int> combined )
	{
		var mapped = new int[ring.Count];

		for ( var i = 0; i < ring.Count; i++ )
			mapped[i] = combined[ring[i]];

		return mapped;
	}

	static bool Distinct( int[] indices )
	{
		var seen = new HashSet<int>();

		foreach ( var i in indices )
		{
			if ( !seen.Add( i ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// The connected run of faces sharing the loop's plane and one material slot.
	///
	/// Connected THROUGH SHARED EDGES, because that is what makes it one surface rather than two
	/// that happen to be level with each other — a lid and a pocket floor two units below it are
	/// coplanar in normal and nothing else, and welding them would be nonsense. Material is part of
	/// the identity for the reason CoplanarMerge gives: two coplanar neighbours painted differently
	/// are two faces because somebody made them two faces.
	/// </summary>
	static List<int> CoplanarGroup( PolyMesh mesh, List<int> loop, Vec3 normal, float plane, out int material )
	{
		material = 0;

		var loopEdges = new HashSet<EdgeKey>();

		for ( var i = 0; i < loop.Count; i++ )
			loopEdges.Add( new EdgeKey( loop[i], loop[(i + 1) % loop.Count] ) );

		var eligible = new List<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 )
				continue;

			var faceNormal = mesh.FaceNormal( face );

			// The SIGNED dot, not its magnitude: a face pointing the other way is the far side of a
			// zero-thickness sliver, never part of this surface.
			if ( Vec3.Dot( faceNormal, normal ) < NormalTolerance )
				continue;

			if ( MathF.Abs( Vec3.Dot( mesh.Positions[face.Indices[0]], normal ) - plane ) > PlaneTolerance )
				continue;

			var isWall = false;

			for ( var i = 0; i < face.Count; i++ )
			{
				if ( loopEdges.Contains( new EdgeKey( face.Indices[i], face.Indices[(i + 1) % face.Count] ) ) )
				{
					isWall = true;
					break;
				}
			}

			if ( !isWall )
				eligible.Add( fi );
		}

		if ( eligible.Count == 0 )
			return eligible;

		// Which of the eligible faces the mouth actually sits in decides the group, so the walk
		// starts from the one containing the loop's first vertex and spreads through shared edges.
		Basis( normal, out var u, out var v );

		var seed = -1;
		var point = Flatten( mesh.Positions[loop[0]], u, v );

		foreach ( var fi in eligible )
		{
			var polygon = Polygon( mesh, mesh.Faces[fi], u, v );

			if ( PointInPolygon( polygon, point ) || OnBoundary( polygon, point ) )
			{
				seed = fi;
				break;
			}
		}

		if ( seed < 0 )
			return new List<int>();

		material = mesh.Faces[seed].Material;

		var pool = new HashSet<int>();

		foreach ( var fi in eligible )
		{
			if ( mesh.Faces[fi].Material == material )
				pool.Add( fi );
		}

		var edgeFaces = mesh.BuildEdgeFaces();
		var group = new List<int>();
		var visited = new HashSet<int> { seed };
		var queue = new Queue<int>();

		queue.Enqueue( seed );

		while ( queue.Count > 0 )
		{
			var fi = queue.Dequeue();
			group.Add( fi );

			var face = mesh.Faces[fi];

			for ( var i = 0; i < face.Count; i++ )
			{
				var key = new EdgeKey( face.Indices[i], face.Indices[(i + 1) % face.Count] );

				if ( !edgeFaces.TryGetValue( key, out var users ) )
					continue;

				foreach ( var other in users )
				{
					if ( other == fi || !pool.Contains( other ) || !visited.Add( other ) )
						continue;

					queue.Enqueue( other );
				}
			}
		}

		group.Sort();
		return group;
	}

	/// <summary>
	/// The group's own boundary: the edges exactly one of its faces uses, chained into loops.
	///
	/// Internal edges — the ones two group faces share — are what the fragmentation is made of and
	/// are exactly what this discards. What is left is the surface's real outline plus the holes it
	/// already had. The mouth is not among them: its edges belong to the tunnel wall, which is not
	/// in the group.
	/// </summary>
	static bool GroupBoundary( PolyMesh mesh, List<int> group, out List<List<int>> rings )
	{
		rings = new List<List<int>>();

		var count = new Dictionary<EdgeKey, int>();
		var directed = new Dictionary<int, List<int>>();

		foreach ( var fi in group )
		{
			var face = mesh.Faces[fi];

			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[(i + 1) % face.Count];
				var key = new EdgeKey( a, b );

				count[key] = count.TryGetValue( key, out var n ) ? n + 1 : 1;
			}
		}

		// Kept DIRECTED, so the rings come out wound the way the surface is rather than whichever
		// way the walk happened to go. A hole wound like its contour triangulates inside out.
		foreach ( var fi in group )
		{
			var face = mesh.Faces[fi];

			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[(i + 1) % face.Count];

				if ( count[new EdgeKey( a, b )] != 1 )
					continue;

				if ( !directed.TryGetValue( a, out var list ) )
					directed[a] = list = new List<int>();

				list.Add( b );
			}
		}

		var used = new HashSet<(int, int)>();

		foreach ( var start in directed.Keys )
		{
			foreach ( var first in directed[start] )
			{
				if ( used.Contains( (start, first) ) )
					continue;

				var ring = new List<int> { start };
				var current = first;

				used.Add( (start, current) );

				while ( current != start )
				{
					ring.Add( current );

					if ( !directed.TryGetValue( current, out var outgoing ) )
						return false;

					var next = -1;

					foreach ( var candidate in outgoing )
					{
						if ( used.Contains( (current, candidate) ) )
							continue;

						next = candidate;
						break;
					}

					if ( next < 0 )
						return false;

					used.Add( (current, next) );
					current = next;

					// A walk longer than the boundary itself is a bookkeeping fault, not a shape.
					if ( ring.Count > count.Count + 1 )
						return false;
				}

				if ( ring.Count >= 3 )
					rings.Add( ring );
			}
		}

		return rings.Count >= 1;
	}

	/// <summary>The UV every vertex of the group already carries, taken from the first face that
	/// names it. A vertex with two different UVs across the group is a seam, and a surface with a
	/// seam through it is not one this should be welding into a single region — but keeping the
	/// first is the same thing the group's own faces were already showing.</summary>
	static Dictionary<int, Vec2> KnownUVs( PolyMesh mesh, List<int> group )
	{
		var known = new Dictionary<int, Vec2>();

		foreach ( var fi in group )
		{
			var face = mesh.Faces[fi];

			for ( var i = 0; i < face.Count; i++ )
				known.TryAdd( face.Indices[i], face.UVs[i] );
		}

		return known;
	}

	/// <summary>The linear plane-to-UV map the group's own corners already follow, for the mouth's
	/// vertices which have no UV of their own. Same rule as the other repairs.</summary>
	static bool UVMap( PolyMesh mesh, List<int> group, Vec3 u, Vec3 v,
		out Vec2 origin, out Vec2 originUv, out float scaleU, out float scaleV )
	{
		origin = default;
		originUv = default;
		scaleU = 0f;
		scaleV = 0f;

		var face = mesh.Faces[group[0]];

		origin = Flatten( mesh.Positions[face.Indices[0]], u, v );
		originUv = face.UVs[0];

		foreach ( var fi in group )
		{
			var f = mesh.Faces[fi];

			for ( var i = 0; i < f.Count; i++ )
			{
				var p = Flatten( mesh.Positions[f.Indices[i]], u, v ) - origin;
				var duv = new Vec2( f.UVs[i].x - originUv.x, f.UVs[i].y - originUv.y );

				if ( MathF.Abs( p.x ) > 1e-6f && scaleU == 0f )
					scaleU = duv.x / p.x;

				if ( MathF.Abs( p.y ) > 1e-6f && scaleV == 0f )
					scaleV = duv.y / p.y;
			}
		}

		return true;
	}

	// --- shared geometry, kept here so this file stands alone -------------------------------------

	static List<List<int>> BoundaryLoops( PolyMesh mesh )
	{
		var atVertex = new Dictionary<int, List<int>>();

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 1 )
				continue;

			Link( key.A, key.B );
			Link( key.B, key.A );
		}

		var loops = new List<List<int>>();
		var used = new HashSet<EdgeKey>();

		foreach ( var start in atVertex.Keys )
		{
			if ( atVertex[start].Count != 2 )
				continue;

			var loop = new List<int>();
			var current = start;
			var previous = -1;

			while ( true )
			{
				loop.Add( current );

				var next = -1;

				foreach ( var candidate in atVertex[current] )
				{
					if ( candidate == previous || used.Contains( new EdgeKey( current, candidate ) ) )
						continue;

					next = candidate;
					break;
				}

				if ( next < 0 )
					break;

				used.Add( new EdgeKey( current, next ) );
				previous = current;
				current = next;

				if ( current == start )
					break;
			}

			if ( loop.Count >= 3 && current == start )
				loops.Add( loop );
		}

		return loops;

		void Link( int from, int to )
		{
			if ( !atVertex.TryGetValue( from, out var list ) )
			{
				list = new List<int>( 2 );
				atVertex[from] = list;
			}

			list.Add( to );
		}
	}

	static Vec3 LoopNormal( PolyMesh mesh, List<int> loop )
	{
		var n = new Vec3( 0, 0, 0 );

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = mesh.Positions[loop[i]];
			var b = mesh.Positions[loop[(i + 1) % loop.Count]];

			n += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}

		// A loop bounding a hole is wound against the face it is a hole in, so its own normal points
		// the other way. Everything here is expressed in the SURFACE's direction.
		return n;
	}

	static void Basis( Vec3 normal, out Vec3 u, out Vec3 v )
	{
		var n = normal.Normal;
		var seed = MathF.Abs( n.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );

		u = Vec3.Cross( seed, n ).Normal;
		v = Vec3.Cross( n, u ).Normal;
	}

	static Vec2 Flatten( Vec3 p, Vec3 u, Vec3 v ) => new( Vec3.Dot( p, u ), Vec3.Dot( p, v ) );

	static List<Vec2> Polygon( PolyMesh mesh, Face face, Vec3 u, Vec3 v )
	{
		var polygon = new List<Vec2>( face.Count );

		foreach ( var index in face.Indices )
			polygon.Add( Flatten( mesh.Positions[index], u, v ) );

		return polygon;
	}

	static float SignedArea( List<Vec2> polygon )
	{
		var area = 0f;

		for ( int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++ )
			area += (polygon[j].x + polygon[i].x) * (polygon[j].y - polygon[i].y);

		return area * 0.5f;
	}

	static bool OnBoundary( List<Vec2> polygon, Vec2 p )
	{
		for ( var i = 0; i < polygon.Count; i++ )
		{
			var a = polygon[i];
			var b = polygon[(i + 1) % polygon.Count];
			var along = b - a;
			var length = along.Length;

			if ( length < 1e-9f )
				continue;

			var cross = along.x * (p.y - a.y) - along.y * (p.x - a.x);

			if ( MathF.Abs( cross ) / length > 1e-4f )
				continue;

			var t = ((p.x - a.x) * along.x + (p.y - a.y) * along.y) / (length * length);

			if ( t >= -1e-5f && t <= 1f + 1e-5f )
				return true;
		}

		return false;
	}

	static bool PointInPolygon( List<Vec2> polygon, Vec2 point )
	{
		var inside = false;

		for ( int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++ )
		{
			var a = polygon[i];
			var b = polygon[j];

			if ( a.y > point.y != b.y > point.y
				&& point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x )
			{
				inside = !inside;
			}
		}

		return inside;
	}
}
