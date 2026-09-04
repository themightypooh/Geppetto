using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Closing a mouth that lies across TWO faces rather than inside one.
///
/// WHAT WAS DECLINED AND WHY. `MeshHoleRepair.FindContainingFace` wants one coplanar face that
/// contains the whole loop, uniquely — and it is right to, because a guess there seals a surface the
/// wrong way and the result is closed, manifold and wrong. But a cut that lands where two coplanar
/// faces meet has a mouth in both of them and no single containing face exists, so the repair walked
/// away and the opening stayed open. A cut meeting an edge so the mouth spans two faces
/// needs the loop split where it crosses that edge.
///
/// THE FIX IS A DETOUR, NOT A PATCH. Each face keeps its own boundary; where that boundary runs
/// along the shared edge, it detours around the half of the mouth on its side. A left quad whose
/// edge runs (0,-2) to (0,2) with a mouth crossing at (0,-1) and (0,1) becomes:
///
///     ... (0,-2) -> (0,-1) -> [the arc through the left half of the mouth] -> (0,1) -> (0,2) ...
///
/// which is one notched face, not a face plus a patch. Nothing is added, nothing is triangulated,
/// and the two faces still meet along what is left of their shared edge.
///
/// WHAT IT STILL DECLINES, and deliberately:
///
/// - a loop crossing a face boundary somewhere that is not a vertex of the loop. The crossing point
///   has to already exist as a vertex, because inventing one means splitting a face this repair was
///   not asked to touch and cannot see the consequences of.
/// - a loop entering and leaving one face more than once. That is a face with two notches or a
///   notch and a hole, and telling those apart needs the containment test the single-face path
///   already does properly.
/// - anything non-planar. A mouth on a curved surface is a different problem and is still open.
/// </summary>
public static class MeshHoleRepairSpan
{
	const float PlaneTolerance = 1e-4f;
	const float NormalTolerance = 0.999f;

	/// <summary>
	/// Close every boundary loop that straddles exactly two coplanar faces. Returns how many.
	///
	/// Run AFTER the single-face repair, so it only ever sees what that one declined.
	/// </summary>
	public static int CloseLoopsSpanningFaces( PolyMesh mesh )
	{
		if ( mesh is null || mesh.FaceCount == 0 )
			return 0;

		var closed = 0;

		// Re-derived each time round: closing one loop changes the faces, and a stale list would
		// splice the next loop into a face that no longer looks like that.
		while ( true )
		{
			var loops = BoundaryLoops( mesh );
			var progressed = false;

			foreach ( var loop in loops )
			{
				if ( loop.Count < 3 || !TryCloseAcross( mesh, loop ) )
					continue;

				closed++;
				progressed = true;
				break;
			}

			if ( !progressed )
				return closed;
		}
	}

	static bool TryCloseAcross( PolyMesh mesh, List<int> loop )
	{
		var normal = LoopNormal( mesh, loop );

		if ( normal.LengthSquared < 1e-20f )
			return false;

		normal = normal.Normal;

		Basis( normal, out var u, out var v );

		var plane = Vec3.Dot( mesh.Positions[loop[0]], normal );
		var candidates = new List<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 )
				continue;

			var faceNormal = mesh.FaceNormal( face );

			if ( faceNormal.LengthSquared < 1e-20f )
				continue;

			if ( MathF.Abs( Vec3.Dot( faceNormal.Normal, normal ) ) < NormalTolerance )
				continue;

			if ( MathF.Abs( Vec3.Dot( mesh.Positions[face.Indices[0]], normal ) - plane ) > PlaneTolerance )
				continue;

			// A face already using a loop vertex as one of its own corners is the wall the loop came
			// from, not the surface it is a mouth in.
			if ( SharesAnyVertex( face, loop ) )
				continue;

			candidates.Add( fi );
		}

		// Exactly two. One is the single-face case, which the other repair does properly; three or
		// more is a mouth crossing a corner, and which face owns which arc stops being obvious.
		if ( candidates.Count != 2 )
			return false;

		var first = candidates[0];
		var second = candidates[1];

		if ( !SplitAcross( mesh, loop, first, second, u, v, out var arcs ) )
			return false;

		// Both notches built before either is written. A half-applied repair leaves a mesh that is
		// worse than the one that came in, and this is exactly where that could happen.
		if ( !BuildNotched( mesh, first, arcs.First, u, v, out var firstFace ) )
			return false;

		if ( !BuildNotched( mesh, second, arcs.Second, u, v, out var secondFace ) )
			return false;

		mesh.Faces[first] = firstFace;
		mesh.Faces[second] = secondFace;

		return true;
	}

	/// <summary>
	/// Cut the loop into the two arcs that belong to the two faces.
	///
	/// The joins are the loop vertices that sit on BOTH faces' boundaries — the points where the
	/// mouth crosses the shared edge. There have to be exactly two of them; one means the loop only
	/// touches the edge, and more means it weaves back and forth, which is the case this declines.
	/// </summary>
	static bool SplitAcross( PolyMesh mesh, List<int> loop, int a, int b, Vec3 u, Vec3 v,
		out (List<int> First, List<int> Second) arcs )
	{
		arcs = (null, null);

		var polygonA = Polygon( mesh, mesh.Faces[a], u, v );
		var polygonB = Polygon( mesh, mesh.Faces[b], u, v );

		var joins = new List<int>();
		var side = new int[loop.Count];

		for ( var i = 0; i < loop.Count; i++ )
		{
			var p = Flatten( mesh.Positions[loop[i]], u, v );
			var onA = OnBoundary( polygonA, p );
			var onB = OnBoundary( polygonB, p );

			if ( onA && onB )
			{
				joins.Add( i );
				side[i] = 0;
				continue;
			}

			var inA = PointInPolygon( polygonA, p );
			var inB = PointInPolygon( polygonB, p );

			// A vertex in neither face means the loop leaves the two faces entirely, and a vertex in
			// both that is not on a shared boundary means they overlap - neither is this case.
			if ( inA == inB )
				return false;

			side[i] = inA ? 1 : 2;
		}

		if ( joins.Count != 2 )
			return false;

		var firstArc = Arc( loop, side, joins[0], joins[1] );
		var secondArc = Arc( loop, side, joins[1], joins[0] );

		if ( firstArc is null || secondArc is null )
			return false;

		// One arc per face, sorted by which side its interior vertices sat on.
		var firstSide = InteriorSide( side, loop.Count, joins[0], joins[1] );
		var secondSide = InteriorSide( side, loop.Count, joins[1], joins[0] );

		if ( firstSide == 0 || secondSide == 0 || firstSide == secondSide )
			return false;

		arcs = firstSide == 1 ? (firstArc, secondArc) : (secondArc, firstArc);
		return true;
	}

	/// <summary>The run of loop vertices from one join to the next, inclusive of both.</summary>
	static List<int> Arc( List<int> loop, int[] side, int from, int to )
	{
		var arc = new List<int>();
		var i = from;

		for ( var guard = 0; guard <= loop.Count; guard++ )
		{
			arc.Add( loop[i] );

			if ( i == to && arc.Count > 1 )
				return arc;

			i = (i + 1) % loop.Count;
		}

		return null;
	}

	/// <summary>Which face the vertices strictly between two joins belong to, or 0 if they disagree.</summary>
	static int InteriorSide( int[] side, int count, int from, int to )
	{
		var found = 0;

		for ( var i = (from + 1) % count; i != to; i = (i + 1) % count )
		{
			if ( side[i] == 0 )
				continue;

			if ( found == 0 )
				found = side[i];
			else if ( found != side[i] )
				return 0;
		}

		return found;
	}

	/// <summary>
	/// The face, re-walked with the arc spliced in where its boundary passes the mouth.
	///
	/// The arc's two ends lie ON one of the face's edges, so the new boundary follows the face until
	/// it reaches that edge, detours along the arc, and picks the face up again. The arc is taken in
	/// whichever direction leaves the face wound the way it already was — a notch that reverses the
	/// winding is a face pointing into the solid, which renders black and looks fine in wireframe.
	/// </summary>
	static bool BuildNotched( PolyMesh mesh, int faceIndex, List<int> arc, Vec3 u, Vec3 v, out Face result )
	{
		result = null;

		var face = mesh.Faces[faceIndex];
		var start = arc[0];
		var end = arc[^1];

		var startEdge = EdgeCarrying( mesh, face, start, u, v );
		var endEdge = EdgeCarrying( mesh, face, end, u, v );

		if ( startEdge < 0 || endEdge < 0 || startEdge != endEdge )
			return false;

		var a = Flatten( mesh.Positions[face.Indices[startEdge]], u, v );
		var from = Flatten( mesh.Positions[start], u, v );
		var to = Flatten( mesh.Positions[end], u, v );

		// Which end of the arc the face's own winding reaches first along that edge.
		var startFirst = (from - a).LengthSquared <= (to - a).LengthSquared;
		var walk = new List<int>( face.Count + arc.Count );

		for ( var i = 0; i < face.Count; i++ )
		{
			walk.Add( face.Indices[i] );

			if ( i != startEdge )
				continue;

			if ( startFirst )
			{
				walk.AddRange( arc );
			}
			else
			{
				for ( var j = arc.Count - 1; j >= 0; j-- )
					walk.Add( arc[j] );
			}
		}

		// A repeated index means the arc met the face at a corner it already owns, and the polygon
		// would pinch there. Refuse rather than emit a face that touches itself.
		var seen = new HashSet<int>();

		foreach ( var index in walk )
		{
			if ( !seen.Add( index ) )
				return false;
		}

		result = new Face( walk.ToArray(), NewUVs( mesh, face, walk, u, v ), face.Material );
		return true;
	}

	/// <summary>
	/// UVs for the rebuilt face, carried across rather than reset.
	///
	/// The face's own corners keep the UVs they had. The arc's vertices are new to this face and get
	/// theirs by the same planar mapping the original corners already follow, so a notch does not
	/// smear the texture across the surface it was cut into.
	/// </summary>
	static Vec2[] NewUVs( PolyMesh mesh, Face face, List<int> walk, Vec3 u, Vec3 v )
	{
		var known = new Dictionary<int, Vec2>();

		for ( var i = 0; i < face.Count; i++ )
			known[face.Indices[i]] = face.UVs[i];

		// Two corners are enough to fix a linear map from plane coordinates to UV, provided they are
		// not the same point. Anything less and the face was degenerate before this touched it.
		var uvs = new Vec2[walk.Count];
		var origin = Flatten( mesh.Positions[face.Indices[0]], u, v );
		var originUv = face.UVs[0];

		var scaleU = 0f;
		var scaleV = 0f;

		for ( var i = 1; i < face.Count; i++ )
		{
			var p = Flatten( mesh.Positions[face.Indices[i]], u, v ) - origin;
			var duv = new Vec2( face.UVs[i].x - originUv.x, face.UVs[i].y - originUv.y );

			if ( MathF.Abs( p.x ) > 1e-6f && scaleU == 0f )
				scaleU = duv.x / p.x;

			if ( MathF.Abs( p.y ) > 1e-6f && scaleV == 0f )
				scaleV = duv.y / p.y;
		}

		for ( var i = 0; i < walk.Count; i++ )
		{
			if ( known.TryGetValue( walk[i], out var existing ) )
			{
				uvs[i] = existing;
				continue;
			}

			var p = Flatten( mesh.Positions[walk[i]], u, v ) - origin;
			uvs[i] = new Vec2( originUv.x + p.x * scaleU, originUv.y + p.y * scaleV );
		}

		return uvs;
	}

	/// <summary>Index of the face edge this point lies on, or -1.</summary>
	static int EdgeCarrying( PolyMesh mesh, Face face, int vertex, Vec3 u, Vec3 v )
	{
		var p = Flatten( mesh.Positions[vertex], u, v );

		for ( var i = 0; i < face.Count; i++ )
		{
			var a = Flatten( mesh.Positions[face.Indices[i]], u, v );
			var b = Flatten( mesh.Positions[face.Indices[(i + 1) % face.Count]], u, v );

			if ( OnSegment( a, b, p ) )
				return i;
		}

		return -1;
	}

	static List<Vec2> Polygon( PolyMesh mesh, Face face, Vec3 u, Vec3 v )
	{
		var polygon = new List<Vec2>( face.Count );

		foreach ( var index in face.Indices )
			polygon.Add( Flatten( mesh.Positions[index], u, v ) );

		return polygon;
	}

	static bool OnBoundary( List<Vec2> polygon, Vec2 p )
	{
		for ( var i = 0; i < polygon.Count; i++ )
		{
			if ( OnSegment( polygon[i], polygon[(i + 1) % polygon.Count], p ) )
				return true;
		}

		return false;
	}

	static bool OnSegment( Vec2 a, Vec2 b, Vec2 p )
	{
		var along = b - a;
		var length = along.Length;

		if ( length < 1e-9f )
			return (p - a).Length < 1e-5f;

		var cross = along.x * (p.y - a.y) - along.y * (p.x - a.x);

		if ( MathF.Abs( cross ) / length > 1e-4f )
			return false;

		var t = ((p.x - a.x) * along.x + (p.y - a.y) * along.y) / (length * length);
		return t >= -1e-5f && t <= 1f + 1e-5f;
	}

	// --- shared with MeshHoleRepair, kept here so this file stands alone -------------------------

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

	static bool SharesAnyVertex( Face face, List<int> loop )
	{
		foreach ( var index in face.Indices )
		{
			if ( loop.Contains( index ) )
				return true;
		}

		return false;
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

		return n;
	}

	static void Basis( Vec3 normal, out Vec3 u, out Vec3 v )
	{
		var seed = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );

		u = Vec3.Cross( seed, normal ).Normal;
		v = Vec3.Cross( normal, u ).Normal;
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
			{
				inside = !inside;
			}
		}

		return inside;
	}
}
