using System;
using System.Collections.Generic;
using System.Linq;

namespace ModelKernel;

/// <summary>
/// Bevel every edge of a solid: inset each face, then bridge the gaps left behind.
///
/// This is the third of the three things that make blocking out a room painful in Hammer, after
/// hollowing and joining. There, bevelling an edge means clipping the same edge repeatedly and
/// hoping, or vertex-editing until the brush stops being convex. Here it is a number, and because
/// it lives in the feature tree it stays a number — set it to 2, decide next week it should be 4.
///
/// HOW IT WORKS. Every face is inset within its own plane so each of its edges moves `distance`
/// inward. That leaves a gap along every original edge and a hole at every original vertex, which
/// are then filled:
///
///     one inset face  per original face      (n-gon, same winding, still in the original plane)
///     one quad        per original edge      (the chamfer strip)
///     one n-gon       per original vertex    (the corner cap, one corner per adjacent face)
///
/// A box becomes 6 + 12 + 8 = 26 faces. The count is exact and there is a test for it.
///
/// THE CORNER INSET IS THE SAME SOLVE AS SHELL, one dimension down. Moving a face corner so both
/// its edges end up `distance` away is "find the point at a fixed distance from two planes, moving
/// as little as possible" — with the two edge normals taken inside the face plane rather than the
/// face normals taken in space. Both call PlaneOffset. Doing it any other way — bisector times
/// 1/sin(half-angle), say — is the same formula rediscovered, and gets the sharp-angle cases wrong.
///
/// SINGLE SEGMENT ONLY: this is a chamfer, a flat cut, not a rounded fillet. That is a deliberate
/// stopping point rather than a shortcut. Multi-segment rounding needs the corner caps rebuilt as
/// patches that agree with every edge strip meeting them, which is most of why the engine's own
/// bevel is two thousand lines. It is also largely unnecessary here: a chamfer followed by
/// Catmull-Clark gives a rounded edge whose tightness is controlled by the bevel distance, which is
/// the pipeline this kernel is built around anyway.
/// </summary>
public static class BevelOperation
{
	/// <summary>
	/// Bevel every edge by `distance`. The mesh must be closed — an open mesh has boundary vertices
	/// whose corner caps are not closed rings, and inventing a cap for them would invent geometry.
	/// </summary>
	public static PolyMesh Bevel( PolyMesh mesh, float distance )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( distance <= 0f )
			throw new InvalidOperationException( "Bevel distance must be positive" );

		var validation = MeshValidator.Validate( mesh );

		if ( !validation.IsValid )
			throw new InvalidOperationException( "Cannot bevel an invalid mesh: " + validation );

		if ( !validation.IsClosed )
			throw new InvalidOperationException( "Cannot bevel an open mesh — its boundary vertices have no closed ring of faces to cap" );

		var faceNormals = new Vec3[mesh.FaceCount];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			faceNormals[fi] = mesh.FaceNormal( mesh.Faces[fi] );

		var result = new PolyMesh();

		// insetIndex[fi][i] is the new vertex made from corner i of face fi. Every new vertex in the
		// mesh comes from exactly one face corner, which is what makes the three face types below
		// addressable without any further bookkeeping.
		var insetIndex = new int[mesh.FaceCount][];
		var insetUV = new Vec2[mesh.FaceCount][];

		// Which original vertex each new one came from. Every inset vertex is one face corner, so
		// this is exact rather than a nearest-match — which is what lets the rig come along.
		var sourceVertex = new List<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			InsetFace( mesh, fi, faceNormals[fi], distance, result, out insetIndex[fi], out insetUV[fi] );

			foreach ( var original in mesh.Faces[fi].Indices )
				sourceVertex.Add( original );
		}

		// A bevelled body keeps its rig. Shell had to be taught this after shipping without it; the
		// rule is that anything rebuilding a vertex list rebuilds the weights beside it.
		if ( mesh.IsRigged )
		{
			var skin = new SkinWeights();

			foreach ( var original in sourceVertex )
				skin.Vertices.Add( (BoneWeight[])mesh.Skin[original].Clone() );

			result.Skin = skin;
		}

		// --- one face per original face ----------------------------------------------------
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			// The inset face keeps the original winding, so it keeps the original normal — and it
			// stays exactly in the original plane, which is the property the tests check.
			result.AddFace( (int[])insetIndex[fi].Clone(), (Vec2[])insetUV[fi].Clone(), mesh.Faces[fi].Material );
		}

		var edgeFaces = mesh.BuildEdgeFaces();

		// --- one quad per original edge ----------------------------------------------------
		foreach ( var (edge, faces) in edgeFaces )
		{
			var f1 = faces[0];
			var f2 = faces[1];

			var a1 = CornerOf( mesh, f1, edge.A );
			var b1 = CornerOf( mesh, f1, edge.B );
			var a2 = CornerOf( mesh, f2, edge.A );
			var b2 = CornerOf( mesh, f2, edge.B );

			var quad = new[] { insetIndex[f1][a1], insetIndex[f1][b1], insetIndex[f2][b2], insetIndex[f2][a2] };
			var uvs = new[] { insetUV[f1][a1], insetUV[f1][b1], insetUV[f2][b2], insetUV[f2][a2] };

			// A chamfer strip faces outward, roughly along the average of the two faces it joins.
			// Measuring and flipping beats deriving the winding from traversal order, which depends
			// on which of the two faces the edge map happened to list first.
			Orient( result, quad, uvs, (faceNormals[f1] + faceNormals[f2]).Normal );

			result.AddFace( quad, uvs, mesh.Faces[f1].Material );
		}

		// --- one n-gon per original vertex -------------------------------------------------
		var vertexFaces = mesh.BuildVertexFaces();
		var vertexNormals = mesh.ComputeVertexNormals();

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			var ring = OrderFacesAroundVertex( mesh, vi, vertexFaces[vi], edgeFaces );

			var cap = new int[ring.Count];
			var capUVs = new Vec2[ring.Count];

			for ( var i = 0; i < ring.Count; i++ )
			{
				var corner = CornerOf( mesh, ring[i], vi );
				cap[i] = insetIndex[ring[i]][corner];
				capUVs[i] = insetUV[ring[i]][corner];
			}

			Orient( result, cap, capUVs, vertexNormals[vi] );

			result.AddFace( cap, capUVs, mesh.Faces[ring[0]].Material );
		}

		// Shell shipped a non-manifold result because it validated its input and never its output.
		// Not making that mistake twice: this is the same order of work as the operation itself.
		var outcome = MeshValidator.Validate( result );

		if ( !outcome.IsValid || !outcome.IsClosed )
			throw new InvalidOperationException( $"Bevel produced an invalid mesh ({outcome}). This usually means the distance is too large for some feature of the model." );

		return result;
	}

	/// <summary>
	/// Move every corner of one face inward within its own plane, so each edge of the face ends up
	/// `distance` from where it was.
	/// </summary>
	static void InsetFace( PolyMesh mesh, int fi, Vec3 normal, float distance, PolyMesh result, out int[] indices, out Vec2[] uvs )
	{
		var face = mesh.Faces[fi];
		var n = face.Count;

		indices = new int[n];
		uvs = new Vec2[n];

		for ( var i = 0; i < n; i++ )
		{
			var prevCorner = (i - 1 + n) % n;
			var nextCorner = (i + 1) % n;

			var v = mesh.Positions[face.Indices[i]];
			var prev = mesh.Positions[face.Indices[prevCorner]];
			var next = mesh.Positions[face.Indices[nextCorner]];

			var toPrev = prev - v;
			var toNext = next - v;

			// The inward normals of the two edges meeting here, taken inside the face plane. For a
			// counter-clockwise face wound against `normal`, cross(normal, edgeDirection) points
			// into the face.
			var inEdge = (v - prev).Normal;
			var outEdge = (next - v).Normal;

			var n1 = Vec3.Cross( normal, inEdge );
			var n2 = Vec3.Cross( normal, outEdge );

			if ( !PlaneOffset.TrySolve( new[] { n1, n2 }, distance, out var displacement ) )
				throw new InvalidOperationException(
					$"Face {fi} corner {i} folds back on itself — its two edges are almost collinear, so there is no inset point." );

			var position = v + displacement;

			indices[i] = result.AddVertex( position );
			uvs[i] = InterpolateUV( face, i, prevCorner, nextCorner, toPrev, toNext, displacement );
		}

		// An inset that overshoots collapses the face through itself — the classic "bevel bigger
		// than the feature" failure. Caught here, naming the face, rather than surfacing later as a
		// mesh that renders with holes in it.
		//
		// CHECKING THE FACE NORMAL IS NOT ENOUGH, which is worth spelling out because it is the
		// obvious test and it silently misses the commonest case. Over-insetting a centrally
		// symmetric face — a rectangle, say — point-reflects it through its centre, and a 180-degree
		// rotation PRESERVES orientation. The normal comes back unchanged and the check passes on a
		// face that has turned itself inside out.
		//
		// What does catch it is per-edge: every inset edge has to still run the same way as the edge
		// it came from. An edge that has reversed direction means its two endpoints have crossed
		// over each other, which is exactly the failure, symmetric face or not.
		for ( var i = 0; i < n; i++ )
		{
			var next = (i + 1) % n;

			var original = mesh.Positions[face.Indices[next]] - mesh.Positions[face.Indices[i]];
			var inset = result.Positions[indices[next]] - result.Positions[indices[i]];

			if ( Vec3.Dot( original.Normal, inset.Normal ) <= 0f )
				throw new InvalidOperationException(
					$"A bevel of {distance} is too large for face {fi} — edge {i} collapses through itself. Try a smaller distance." );
		}

		var insetNormal = NewellNormal( result, indices );

		if ( Vec3.Dot( insetNormal, normal ) <= 0f )
			throw new InvalidOperationException(
				$"A bevel of {distance} is too large for face {fi} — insetting by that much turns it inside out. Try a smaller distance." );
	}

	/// <summary>
	/// The UV for an inset corner, by expressing its displacement in terms of the two edges leaving
	/// that corner and applying the same proportions to the corner's UVs.
	///
	/// Correct rather than approximate, because UVs vary linearly across a planar face: a point some
	/// fraction along two edges has exactly that fraction of the UV change along each.
	/// </summary>
	static Vec2 InterpolateUV( Face face, int corner, int prevCorner, int nextCorner, Vec3 toPrev, Vec3 toNext, Vec3 displacement )
	{
		var prevLength = toPrev.Length;
		var nextLength = toNext.Length;

		if ( prevLength < 1e-9f || nextLength < 1e-9f )
			return face.UVs[corner];

		var u = toPrev / prevLength;
		var w = toNext / nextLength;

		var c = Vec3.Dot( u, w );
		var det = 1f - c * c;

		// Collinear edges: the corner is a straight line, so there is no meaningful decomposition.
		// The corner's own UV is the honest answer.
		if ( MathF.Abs( det ) < 1e-9f )
			return face.UVs[corner];

		var du = Vec3.Dot( displacement, u );
		var dw = Vec3.Dot( displacement, w );

		var alpha = (du - c * dw) / det;
		var beta = (dw - c * du) / det;

		var uvHere = face.UVs[corner];

		return uvHere
			+ (face.UVs[prevCorner] - uvHere) * (alpha / prevLength)
			+ (face.UVs[nextCorner] - uvHere) * (beta / nextLength);
	}

	/// <summary>
	/// The faces around a vertex, in the order they touch each other — which is the order their
	/// inset corners have to be listed in for the cap to be a simple polygon rather than a
	/// self-crossing star.
	///
	/// Walks face to face across the edges meeting at the vertex. On a closed manifold this returns
	/// to where it started having visited each face once; anything else means the vertex is not
	/// manifold and is reported rather than silently producing a tangled cap.
	/// </summary>
	static List<int> OrderFacesAroundVertex( PolyMesh mesh, int vertex, List<int> faces, Dictionary<EdgeKey, List<int>> edgeFaces )
	{
		var ring = new List<int>( faces.Count );
		var start = faces[0];
		var current = start;
		var (currentEdge, _) = EdgesAtVertex( mesh, current, vertex );

		for ( var step = 0; step < faces.Count; step++ )
		{
			ring.Add( current );

			var adjacent = edgeFaces[currentEdge];

			if ( adjacent.Count != 2 )
				throw new InvalidOperationException( $"Vertex {vertex} sits on a non-manifold edge and cannot be capped" );

			var next = adjacent[0] == current ? adjacent[1] : adjacent[0];
			var (edgeA, edgeB) = EdgesAtVertex( mesh, next, vertex );

			currentEdge = edgeA.Equals( currentEdge ) ? edgeB : edgeA;
			current = next;

			if ( current == start )
				break;
		}

		if ( ring.Count != faces.Count )
			throw new InvalidOperationException(
				$"The faces around vertex {vertex} do not form one closed ring ({ring.Count} of {faces.Count} reachable), so it cannot be capped" );

		return ring;
	}

	/// <summary>The two edges of `face` that meet at `vertex`.</summary>
	static (EdgeKey, EdgeKey) EdgesAtVertex( PolyMesh mesh, int face, int vertex )
	{
		var f = mesh.Faces[face];
		var n = f.Count;
		var corner = CornerOf( mesh, face, vertex );

		return (
			new EdgeKey( f.Indices[(corner - 1 + n) % n], vertex ),
			new EdgeKey( vertex, f.Indices[(corner + 1) % n] ));
	}

	static int CornerOf( PolyMesh mesh, int face, int vertex )
	{
		var indices = mesh.Faces[face].Indices;

		for ( var i = 0; i < indices.Length; i++ )
		{
			if ( indices[i] == vertex )
				return i;
		}

		throw new InvalidOperationException( $"Face {face} does not use vertex {vertex}" );
	}

	/// <summary>Reverse a face's corners — and its UVs alongside them — if it is facing the wrong
	/// way. Reversing one without the other mirrors the texture, which is a bug this codebase has
	/// already shipped once.</summary>
	static void Orient( PolyMesh mesh, int[] indices, Vec2[] uvs, Vec3 desired )
	{
		if ( Vec3.Dot( NewellNormal( mesh, indices ), desired ) >= 0f )
			return;

		Array.Reverse( indices );
		Array.Reverse( uvs );
	}

	static Vec3 NewellNormal( PolyMesh mesh, int[] indices )
	{
		var n = Vec3.Zero;

		for ( var i = 0; i < indices.Length; i++ )
		{
			var a = mesh.Positions[indices[i]];
			var b = mesh.Positions[indices[(i + 1) % indices.Length]];

			n += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}

		return n.Normal;
	}
}
