using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Flat edge bevel (chamfer). Selected edges are cut back by a fixed width on both adjacent faces,
/// which opens a new face along the edge and, where several selected edges meet, a new n-gon cap
/// at the vertex.
///
/// THE COERCION AT THE HEART OF THIS: a face's corner is never split into two points. Instead each
/// (face, vertex) pair gets exactly ONE new point — the intersection, in that face's own plane, of
/// its two boundary edges after sliding the selected ones inward by `width`. That single rule is
/// what makes an edge bevel, a vertex cap and a plain untouched corner all fall out of the same
/// code: an untouched corner is the case where neither incident line moved, so the "intersection"
/// is just the original vertex.
///
/// THE PART THAT ISN'T LOCAL: a corner can still move because of an edge that ISN'T selected,
/// simply because the corner's OTHER edge is. That moved point lands exactly on the unselected
/// edge's own line (see CutCorner) — so the face across THAT edge, even though nothing about it was
/// selected, now disagrees with its neighbour about where their shared edge ends. Reconciling that
/// is what the bridging pass does for every edge, not only the ones the angle threshold picked.
///
/// A rounded fillet (multiple segments) is not built — this is Segments=1 chamfer only. See
/// MODELING-HANDOFF.md.
/// </summary>
public static class Bevel
{
	/// <summary>
	/// Cuts every edge whose two face normals diverge by at least `angleThresholdDegrees`, by
	/// `width` on each side. Boundary edges (one adjacent face) are never selected — bevelling an
	/// open edge needs different handling and is not built.
	///
	/// Skin weights, if the mesh is rigged, come along: every new corner point is a genuine cut of
	/// exactly one original vertex (see the class comment), so it inherits that vertex's weights
	/// unchanged rather than an average of its neighbours — the same "one point, one source" rule
	/// CatmullClark's own corner rule follows, just without the blending, because a bevel corner
	/// doesn't move far enough from its source to warrant one.
	/// </summary>
	public static PolyMesh Apply( PolyMesh mesh, float width, float angleThresholdDegrees )
	{
		if ( width <= 0f )
			return mesh.Clone();

		var edgeFaces = mesh.BuildEdgeFaces();
		var vertexFaces = mesh.BuildVertexFaces();
		var faceNormals = mesh.Faces.Select( mesh.FaceNormal ).ToArray();

		var selected = SelectEdges( mesh, edgeFaces, faceNormals, angleThresholdDegrees );

		// Per (face, vertex) bookkeeping, filled while the corners are cut.
		var cornerPoint = new Dictionary<(int Face, int Vertex), int>();
		var edgeFrom = new Dictionary<(int Face, EdgeKey), int>();
		var nextEdgeAtVertex = new Dictionary<(int Face, int Vertex), EdgeKey>();

		var positions = new List<Vec3>( mesh.Positions );
		var weights = mesh.IsRigged ? new List<BoneWeight[]>( mesh.Skin.Vertices ) : null;

		// Pass 1: work out where every corner of every original face lands. This has to finish for
		// EVERY face before any face's final loop is built (pass 2 below) — a face's own corner can
		// stay put while its neighbour's, across an edge that was never selected, still needs to
		// know that.
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			var n = faceNormals[fi];
			var count = f.Count;

			for ( var i = 0; i < count; i++ )
			{
				var prev = f.Indices[(i - 1 + count) % count];
				var v = f.Indices[i];
				var next = f.Indices[(i + 1) % count];

				var prevKey = new EdgeKey( prev, v );
				var nextKey = new EdgeKey( v, next );

				edgeFrom[(fi, nextKey)] = v;
				nextEdgeAtVertex[(fi, v)] = nextKey;

				var point = CutCorner(
					mesh.Positions[prev], mesh.Positions[v], mesh.Positions[next], n,
					selected.Contains( prevKey ), selected.Contains( nextKey ), width );

				int index;

				if ( point is { } p )
				{
					index = positions.Count;
					positions.Add( p );
					weights?.Add( mesh.Skin[v] );
				}
				else
				{
					index = v;
				}

				cornerPoint[(fi, v)] = index;
			}
		}

		var result = new PolyMesh { Positions = positions, Skin = weights is null ? null : new SkinWeights { Vertices = weights } };

		// Pass 2: each face keeps its own corner points, one per original corner — no splitting,
		// no routing through a neighbour. Whatever gap that leaves along an edge whose two sides
		// disagree is entirely pass 3's job, for every edge alike, not just the selected ones.
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			var newIndices = new int[f.Count];

			for ( var i = 0; i < f.Count; i++ )
				newIndices[i] = cornerPoint[(fi, f.Indices[i])];

			result.AddFace( newIndices, (Vec2[])f.UVs.Clone(), f.Material );
		}

		// Pass 3: a bridging face for every edge whose two sides disagree at either end — not just
		// the selected ones. An edge can end up needing this even though it was never selected
		// itself: cut a face's corner because of one of its OTHER edges, and the corner slides along
		// THIS edge's own line (see CutCorner) — while the face on the far side of this same edge,
		// having no selected edge of its own here, never moves. Left alone that is a T-junction: two
		// faces disagreeing about where their shared edge ends, which shows up as an open boundary
		// rather than a crash. Skipping unselected edges here was the original design and the bug
		// both — an edge bevel is not local to the edge you selected, it is local to every corner
		// that edge touches.
		foreach ( var key in edgeFaces.Keys )
		{
			var faces = edgeFaces[key];

			if ( faces.Count != 2 )
				continue; // boundary edge — nothing on the other side to reconcile with.

			var (fAtoB, fBtoA) = edgeFrom[(faces[0], key)] == key.A
				? (faces[0], faces[1])
				: (faces[1], faces[0]);

			var pAonAB = cornerPoint[(fAtoB, key.A)];
			var pBonAB = cornerPoint[(fAtoB, key.B)];
			var pBonBA = cornerPoint[(fBtoA, key.B)];
			var pAonBA = cornerPoint[(fBtoA, key.A)];

			// UVs are a placeholder here — planar projection isn't built yet (see
			// MODELING-HANDOFF.md), and this face's true UV depends on it.
			//
			// Order is [A-on-AB, A-on-BA, B-on-BA, B-on-AB], NOT the more obvious "walk A's side
			// then B's side" — that ordering puts the normal in the plane but pointing inward, an
			// exact instance of the trap MeshTransform.Apply's own comment warns about: it renders
			// black and looks fine in wireframe. Caught here by Newell's method on a cube edge by
			// hand, cross-checked against the volume test below.
			//
			// An end whose two points already agree (both equal the plain vertex, or by symmetry
			// equal each other) collapses away in the dedupe below rather than needing special-
			// casing — including the "untouched third face" case: routing THROUGH that vertex here
			// was tried and reliably added spurious volume (a hand-measured, reproducible excess,
			// not a rounding artefact) because it double-counts the sliver the cap below already
			// covers. The vertex's own cap face is what closes that gap instead — every distinct
			// point converging on a vertex belongs to exactly one place, the cap, not smeared across
			// every bridge that happens to end there too.
			var corners = new List<int> { pAonAB, pAonBA, pBonBA, pBonAB };

			for ( var i = corners.Count - 1; i > 0; i-- )
			{
				if ( corners[i] == corners[i - 1] )
					corners.RemoveAt( i );
			}

			if ( corners.Count > 2 && corners[0] == corners[^1] )
				corners.RemoveAt( corners.Count - 1 );

			if ( corners.Count < 3 )
				continue; // neither end actually cut — nothing to bevel on this edge after all.

			result.AddFace( corners.ToArray(), material: mesh.Faces[fAtoB].Material );
		}

		foreach ( var (v, faces) in vertexFaces.Select( ( fs, v ) => (v, fs) ) )
		{
			var loop = WalkFacesAroundVertex( v, faces, edgeFaces, nextEdgeAtVertex );

			if ( loop is null )
				continue; // boundary or non-manifold vertex — left untouched, a known limitation.

			var points = loop.Select( f => cornerPoint[(f, v)] ).ToList();

			// Two consecutive faces can still land on the very same cut point — e.g. by symmetry —
			// so collapse repeats the same way the untouched-vertex case above used to.
			var distinct = new List<int>();

			foreach ( var p in points )
			{
				if ( distinct.Count == 0 || distinct[^1] != p )
					distinct.Add( p );
			}

			if ( distinct.Count > 1 && distinct[0] == distinct[^1] )
				distinct.RemoveAt( distinct.Count - 1 );

			if ( distinct.Count < 3 )
				continue; // nothing left to cap.

			result.AddFace( distinct.ToArray(), material: mesh.Faces[loop[0]].Material );
		}

		// A fully-beveled solid never has a single face left referencing an original vertex —
		// every corner got cut on both sides. Left in, those positions would still count toward
		// VertexCount and quietly break any Euler-characteristic check downstream.
		return RemoveUnusedVertices( result );
	}

	static PolyMesh RemoveUnusedVertices( PolyMesh mesh )
	{
		var remap = new int[mesh.Positions.Count];
		Array.Fill( remap, -1 );

		var positions = new List<Vec3>();
		var weights = mesh.Skin is not null ? new List<BoneWeight[]>() : null;

		foreach ( var f in mesh.Faces )
		{
			foreach ( var i in f.Indices )
			{
				if ( remap[i] < 0 )
				{
					remap[i] = positions.Count;
					positions.Add( mesh.Positions[i] );
					weights?.Add( mesh.Skin[i] );
				}
			}
		}

		var result = new PolyMesh { Positions = positions, Skin = weights is null ? null : new SkinWeights { Vertices = weights } };

		foreach ( var f in mesh.Faces )
			result.AddFace( f.Indices.Select( i => remap[i] ).ToArray(), (Vec2[])f.UVs.Clone(), f.Material );

		return result;
	}

	static HashSet<EdgeKey> SelectEdges(
		PolyMesh mesh, Dictionary<EdgeKey, List<int>> edgeFaces, Vec3[] faceNormals, float angleThresholdDegrees )
	{
		var cosThreshold = MathF.Cos( angleThresholdDegrees * MathF.PI / 180f );
		var selected = new HashSet<EdgeKey>();

		foreach ( var (key, faces) in edgeFaces )
		{
			if ( faces.Count != 2 )
				continue; // boundary or non-manifold — never selected.

			var dot = Vec3.Dot( faceNormals[faces[0]], faceNormals[faces[1]] );

			if ( dot < cosThreshold )
				selected.Add( key );
		}

		return selected;
	}

	/// <summary>
	/// The new position for one face's corner at `v`, or null if neither incident edge is
	/// selected — meaning the corner is untouched and the caller should keep using `v` itself.
	///
	/// Each selected edge slides its supporting line inward, within the face's own plane, by
	/// `width`; the corner becomes the intersection of its two (possibly-slid) boundary lines.
	/// </summary>
	static Vec3? CutCorner( Vec3 prev, Vec3 v, Vec3 next, Vec3 faceNormal, bool prevSelected, bool nextSelected, float width )
	{
		if ( !prevSelected && !nextSelected )
			return null;

		// Direction as the FACE traverses each edge — (prev,v) ending at v, (v,next) starting at
		// v — not "away from v". Cross(normal, direction) then points consistently inward for
		// every edge of a CCW polygon; get the sign backwards and every bevel face turns inside
		// out while still looking fine in wireframe, the exact trap CatmullClark's own notes warn
		// about elsewhere in this kernel.
		var dirPrev = (v - prev).Normal;
		var dirNext = (next - v).Normal;

		var p1 = v;
		var d1 = dirPrev;

		if ( prevSelected )
			p1 += Vec3.Cross( faceNormal, dirPrev ).Normal * width;

		var p2 = v;
		var d2 = dirNext;

		if ( nextSelected )
			p2 += Vec3.Cross( faceNormal, dirNext ).Normal * width;

		// WHERE THE CORNER GOES WHEN THE TWO LINES WILL NOT INTERSECT USEFULLY.
		//
		// At a straight corner the two boundary lines are the same line, slid inward by the same
		// width in the same direction, so their "intersection" is undefined while the corner
		// itself is not: it is just the original vertex moved inward by width. That is this
		// fallback, and it is the right answer for the clamp below too — both are the case where
		// the intersection has stopped meaning anything.
		//
		// Taken from whichever edge was actually selected; when both are, they agree to within
		// the angle that made the intersection useless in the first place.
		var inward = prevSelected
			? Vec3.Cross( faceNormal, dirPrev ).Normal
			: Vec3.Cross( faceNormal, dirNext ).Normal;

		var fallback = v + inward * width;

		if ( IntersectCoplanarLines( p1, d1, p2, d2, faceNormal ) is not { } hit )
			return fallback;

		// A CHAMFER IS A LOCAL CUT, SO A CORNER THAT TRAVELS MILES IS THE DEGENERATE CASE.
		//
		// The offset point sits at roughly width/sin(turn) from the vertex, so a corner that is
		// nearly straight throws it arbitrarily far. Ear clipping a thin annulus produces exactly
		// that: collinear corners (turn 180°, sin 1.5e-5) that put the point 15000 units away on a
		// model 20 units across. Those vertices are finite and the mesh still validates as closed
		// and manifold, which is why only a render ever showed it — the model collapses to a
		// speck because the view has to fit a stray vertex a thousand diameters out.
		//
		// Every honest chamfer lands within a few multiples of width: a square corner is about
		// 1.4x, and even a 5° needle only reaches ~11x. Past this it is degeneracy, not sharpness.
		return (hit - v).Length > width * MaxCornerOffset ? fallback : hit;
	}

	/// <summary>How far a bevelled corner may travel from its original vertex, as a multiple of
	/// width. See CutCorner for why a cap is needed at all and why this value is generous.</summary>
	const float MaxCornerOffset = 20f;

	/// <summary>
	/// Where two lines meet, given they both lie in the plane with this normal. Null if they are
	/// (near) parallel — a straight 180° corner, which ear clipping a thin ring produces routinely.
	///
	/// `denom` IS sin(angle between the two directions): d1, d2 and planeNormal are all unit
	/// vectors, so the triple product reduces to it exactly. That is worth stating because the
	/// epsilon is otherwise impossible to reason about — the previous 1e-9 read as a floating
	/// point guard but actually meant "only reject corners straighter than 6e-8 degrees", which no
	/// mesh ever is, so it never once fired. CutCorner's clamp is what makes the result robust;
	/// this threshold only avoids handing it a division that has already lost all its precision.
	/// </summary>
	static Vec3? IntersectCoplanarLines( Vec3 p1, Vec3 d1, Vec3 p2, Vec3 d2, Vec3 planeNormal )
	{
		var denom = Vec3.Dot( Vec3.Cross( d1, d2 ), planeNormal );

		if ( MathF.Abs( denom ) < 1e-6f )
			return null;

		var w = p2 - p1;
		var t = Vec3.Dot( Vec3.Cross( w, d2 ), planeNormal ) / denom;

		return p1 + d1 * t;
	}

	/// <summary>
	/// The faces touching `v`, in the cyclic order they actually wind around it — found by hopping
	/// from each face across the edge it shares with its neighbour, using the SAME edge every face
	/// calls "next" at this vertex. Returns null for a boundary or non-manifold vertex, where the
	/// faces around v do not form one closed loop.
	/// </summary>
	static List<int> WalkFacesAroundVertex(
		int v, List<int> facesAtV, Dictionary<EdgeKey, List<int>> edgeFaces,
		Dictionary<(int Face, int Vertex), EdgeKey> nextEdgeAtVertex )
	{
		if ( facesAtV.Count < 2 )
			return null;

		var order = new List<int>( facesAtV.Count );
		var current = facesAtV[0];

		for ( var step = 0; step < facesAtV.Count; step++ )
		{
			order.Add( current );

			var edge = nextEdgeAtVertex[(current, v)];
			var onEdge = edgeFaces[edge];

			if ( onEdge.Count != 2 )
				return null; // this vertex touches a boundary edge.

			current = onEdge[0] == current ? onEdge[1] : onEdge[0];
		}

		return current == order[0] && order.Count == facesAtV.Count ? order : null;
	}
}
