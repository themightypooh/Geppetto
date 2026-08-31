using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Chamfer and fillet — the two ways to take a sharp edge off a solid, and one implementation,
/// because they differ in exactly one place.
///
/// TWO NAMES BECAUSE THEY ARE TWO OPERATIONS TO THE PERSON USING THEM. Onshape has a Chamfer tool
/// and a Fillet tool, sized in a distance and a radius respectively, and nobody reaching for a
/// rounded corner thinks of it as a chamfer with a segment count turned up. This file used to be
/// called Bevel and offered the flat cut only; the rounding arrived as a parameter on it, which
/// put the two operations behind one name and one control that meant a different thing at each
/// end of its range.
///
/// THE COERCION AT THE HEART OF THIS: a face's corner is never split into two points. Instead each
/// (face, vertex) pair gets exactly ONE new point — the intersection, in that face's own plane, of
/// its two boundary edges after sliding the selected ones inward by that edge's setback. That
/// single rule is what makes an edge cut, a vertex cap and a plain untouched corner all fall out of
/// the same code: an untouched corner is the case where neither incident line moved, so the
/// "intersection" is just the original vertex.
///
/// THE PART THAT ISN'T LOCAL: a corner can still move because of an edge that ISN'T selected,
/// simply because the corner's OTHER edge is. That moved point lands exactly on the unselected
/// edge's own line (see CutCorner) — so the face across THAT edge, even though nothing about it was
/// selected, now disagrees with its neighbour about where their shared edge ends. Reconciling that
/// is what the bridging pass does for every edge, not only the ones the angle threshold picked.
///
/// WHERE A FILLET DIFFERS FROM A CHAMFER, AND IT IS ONLY THESE TWO THINGS:
///
/// 1. The setback is derived rather than given. A chamfer's distance IS the setback. A fillet's
///    radius is not: the tangent points of a circle of radius r inscribed against two faces meeting
///    at an opening angle φ sit r/tan(φ/2) back from the edge, so a fillet computes a setback PER
///    EDGE from that edge's own angle. On a cube every edge opens at 90° and the two coincide,
///    which is why one global width was enough as long as only chamfers existed.
///
/// 2. The single bridging quad along an edge becomes n quads across an arc — and every point on
///    that arc has to be threaded into the vertex cap at each end, in the cap's own cyclic order.
///    That is the part that made rounding not a local change. Arc points added to the bridge and
///    not to the cap leave T-junctions, which pass closed, manifold and Euler while rendering
///    wrong. See ArcRails and the cap pass.
/// </summary>
public static class EdgeBlend
{
	/// <summary>
	/// Flat cut. `distance` is how far back from the edge the cut starts on each adjacent face,
	/// which is what Onshape's chamfer means by distance.
	/// </summary>
	public static PolyMesh Chamfer( PolyMesh mesh, float distance, float angleThresholdDegrees )
		=> Apply( mesh, distance, angleThresholdDegrees, 1, rounded: false );

	/// <summary>
	/// Rounded cut. `radius` is the radius of the arc, not the setback — see the class comment for
	/// why those are the same number on a cube and different everywhere else.
	///
	/// `segments` is how many faces the arc is made of. Four is a reasonable default for a
	/// polygonal kernel: enough to read as round at model scale, few enough not to bury the quad
	/// cage Catmull-Clark wants under a strip of slivers. One is legal and produces exactly the
	/// chamfer, which is the right answer to asking for a one-segment arc.
	/// </summary>
	public static PolyMesh Fillet( PolyMesh mesh, float radius, float angleThresholdDegrees, int segments = 4 )
		=> Apply( mesh, radius, angleThresholdDegrees, Math.Max( 1, segments ), rounded: true );

	/// <summary>
	/// Cuts every edge whose two face normals diverge by at least `angleThresholdDegrees`.
	/// Boundary edges (one adjacent face) are never selected — cutting an open edge needs different
	/// handling and is not built.
	///
	/// Skin weights, if the mesh is rigged, come along: every new point is a genuine cut of exactly
	/// one original vertex (see the class comment), so it inherits that vertex's weights unchanged
	/// rather than an average of its neighbours — the same "one point, one source" rule
	/// CatmullClark's own corner rule follows, just without the blending, because a cut corner
	/// doesn't move far enough from its source to warrant one. An arc point between two corners cut
	/// from the same vertex is that vertex's cut too, so it takes the same weights.
	/// </summary>
	static PolyMesh Apply( PolyMesh mesh, float size, float angleThresholdDegrees, int segments, bool rounded )
	{
		if ( size <= 0f )
			return mesh.Clone();

		var edgeFaces = mesh.BuildEdgeFaces();
		var vertexFaces = mesh.BuildVertexFaces();
		var faceNormals = mesh.Faces.Select( mesh.FaceNormal ).ToArray();

		var selected = SelectEdges( mesh, edgeFaces, faceNormals, angleThresholdDegrees );

		// How far back from each selected edge its two faces get cut. One number per edge rather
		// than one for the whole mesh, because a fillet's setback depends on the angle the edge
		// opens at. A chamfer puts the same distance on every edge and this is a flat lookup.
		var setback = Setbacks( selected, edgeFaces, faceNormals, size, rounded );

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
					Setback( setback, prevKey ), Setback( setback, nextKey ) );

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

		// Pass 2.5: the arc, for every selected edge, at both of its ends.
		//
		// Built before pass 3 and before the caps because both consume it and they have to consume
		// the SAME points — one that exists in the bridge and not in the cap is a T-junction. A
		// chamfer skips this entirely and every rail below is two points long, which is the single
		// quad it always emitted.
		var rails = rounded
			? ArcRails( mesh, result, selected, edgeFaces, edgeFrom, cornerPoint, faceNormals, size, segments )
			: new Dictionary<EdgeKey, Rails>();

		// Pass 3: a bridging face for every edge whose two sides disagree at either end — not just
		// the selected ones. An edge can end up needing this even though it was never selected
		// itself: cut a face's corner because of one of its OTHER edges, and the corner slides along
		// THIS edge's own line (see CutCorner) — while the face on the far side of this same edge,
		// having no selected edge of its own here, never moves. Left alone that is a T-junction: two
		// faces disagreeing about where their shared edge ends, which shows up as an open boundary
		// rather than a crash. Skipping unselected edges here was the original design and the bug
		// both — an edge cut is not local to the edge you selected, it is local to every corner
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

			// The two rails this strip runs between: each end of the edge, from its AB-side point to
			// its BA-side point, through whatever arc points pass 2.5 put between them. No arc means
			// two points, and the loop below emits the one quad it always did.
			var hasArc = rails.TryGetValue( key, out var arc );

			var railA = hasArc ? arc.AtA : new[] { pAonAB, pAonBA };
			var railB = hasArc ? arc.AtB : new[] { pBonAB, pBonBA };

			// UVs are a placeholder here — planar projection isn't built yet (see
			// WHAT-IS-BUILT.md, "UV projection"), and this face's true UV depends on it.
			//
			// Order is [A-side, A-side-next, B-side-next, B-side], NOT the more obvious "walk A's
			// side then B's side" — that ordering puts the normal in the plane but pointing inward,
			// an exact instance of the trap MeshTransform.Apply's own comment warns about: it
			// renders black and looks fine in wireframe. Caught by Newell's method on a cube edge by
			// hand, cross-checked against the volume test.
			//
			// An end whose two points already agree (both equal the plain vertex, or by symmetry
			// equal each other) collapses away in the dedupe below rather than needing special-
			// casing — including the "untouched third face" case: routing THROUGH that vertex here
			// was tried and reliably added spurious volume (a hand-measured, reproducible excess,
			// not a rounding artefact) because it double-counts the sliver the cap below already
			// covers. The vertex's own cap face is what closes that gap instead — every distinct
			// point converging on a vertex belongs to exactly one place, the cap, not smeared across
			// every bridge that happens to end there too.
			for ( var s = 0; s < railA.Length - 1; s++ )
			{
				var corners = new List<int> { railA[s], railA[s + 1], railB[s + 1], railB[s] };

				for ( var i = corners.Count - 1; i > 0; i-- )
				{
					if ( corners[i] == corners[i - 1] )
						corners.RemoveAt( i );
				}

				if ( corners.Count > 2 && corners[0] == corners[^1] )
					corners.RemoveAt( corners.Count - 1 );

				if ( corners.Count < 3 )
					continue; // neither end actually cut — nothing to do on this edge after all.

				result.AddFace( corners.ToArray(), material: mesh.Faces[fAtoB].Material );
			}
		}

		foreach ( var (v, faces) in vertexFaces.Select( ( fs, v ) => (v, fs) ) )
		{
			var loop = WalkFacesAroundVertex( v, faces, edgeFaces, nextEdgeAtVertex );

			if ( loop is null )
				continue; // boundary or non-manifold vertex — left untouched, a known limitation.

			// THE CAP CARRIES THE ARC TOO. Between one face's corner point and the next lies the
			// edge they share, and if that edge was rounded then the cap's boundary follows the arc
			// across it rather than cutting the corner off. Take the same points the bridge used,
			// in the direction this walk is going.
			var points = new List<int>();

			foreach ( var f in loop )
			{
				points.Add( cornerPoint[(f, v)] );

				var edge = nextEdgeAtVertex[(f, v)];

				if ( !rails.TryGetValue( edge, out var arc ) )
					continue;

				var rail = v == edge.A ? arc.AtA : arc.AtB;

				// The rail runs from the AB-side face to the BA-side face. This walk leaves `f`
				// across this edge, so it is going that way only if `f` IS the AB-side face.
				if ( f == arc.FaceAtoB )
				{
					for ( var i = 1; i < rail.Length - 1; i++ )
						points.Add( rail[i] );
				}
				else
				{
					for ( var i = rail.Length - 2; i > 0; i-- )
						points.Add( rail[i] );
				}
			}

			// Two consecutive entries can still land on the very same point — e.g. by symmetry, or
			// an uncut end whose whole rail is one index — so collapse repeats.
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

		// A fully-cut solid never has a single face left referencing an original vertex — every
		// corner got cut on both sides. Left in, those positions would still count toward
		// VertexCount and quietly break any Euler-characteristic check downstream.
		return RemoveUnusedVertices( result );
	}

	// --- the arc --------------------------------------------------------------------------------

	/// <summary>One rounded edge: the strip's two rails, and which of its faces the rails run FROM
	/// so a cap walking the other way knows to read them backwards.</summary>
	readonly struct Rails
	{
		public readonly int FaceAtoB;
		public readonly int[] AtA, AtB;

		public Rails( int faceAtoB, int[] atA, int[] atB )
		{
			FaceAtoB = faceAtoB;
			AtA = atA;
			AtB = atB;
		}
	}

	/// <summary>How far the two ends may disagree about where the arc's centre is, as a fraction of
	/// the radius. Exactly zero on any edge whose corners were cut by a true fillet; non-zero means
	/// the corner has been pulled somewhere the arc cannot follow, and that edge stays flat.</summary>
	const float CentreAgreement = 0.05f;

	/// <summary>
	/// The arc points along every selected edge, at both ends.
	///
	/// ALL OR NOTHING PER EDGE. An arc that could be built at one end and not the other would leave
	/// the strip's two rails different lengths, and there is no honest way to pair them up. So an
	/// end that cannot be established fails the whole edge back to a flat quad — which is a
	/// chamfer on that one edge, visibly coarser and never wrong.
	/// </summary>
	static Dictionary<EdgeKey, Rails> ArcRails(
		PolyMesh mesh, PolyMesh result, HashSet<EdgeKey> selected,
		Dictionary<EdgeKey, List<int>> edgeFaces,
		Dictionary<(int Face, EdgeKey Edge), int> edgeFrom,
		Dictionary<(int Face, int Vertex), int> cornerPoint,
		Vec3[] faceNormals, float radius, int segments )
	{
		var rails = new Dictionary<EdgeKey, Rails>();

		if ( segments < 2 )
			return rails; // a one-segment arc IS the chamfer, and it is already what pass 3 emits.

		foreach ( var key in selected )
		{
			var faces = edgeFaces[key];

			if ( faces.Count != 2 )
				continue;

			var (fAtoB, fBtoA) = edgeFrom[(faces[0], key)] == key.A
				? (faces[0], faces[1])
				: (faces[1], faces[0]);

			var nA = faceNormals[fAtoB];
			var nB = faceNormals[fBtoA];

			// WHICH SIDE THE CENTRE SITS ON. A convex edge is rounded from inside the solid and a
			// concave one from outside, and the normals alone cannot tell them apart — the angle
			// between them is the same for an edge that opens at 90° and one that opens at 270°.
			// The direction the AB-side face traverses the edge is what breaks the tie.
			var along = (mesh.Positions[key.B] - mesh.Positions[key.A]).Normal;
			var convex = Vec3.Dot( Vec3.Cross( nA, nB ), along ) > 0f;
			var signed = convex ? radius : -radius;

			var atA = Arc( result, cornerPoint[(fAtoB, key.A)], cornerPoint[(fBtoA, key.A)],
				nA, nB, signed, segments, key.A, mesh );

			if ( atA is null )
				continue;

			var atB = Arc( result, cornerPoint[(fAtoB, key.B)], cornerPoint[(fBtoA, key.B)],
				nA, nB, signed, segments, key.B, mesh );

			if ( atB is null )
				continue;

			rails[key] = new Rails( fAtoB, atA, atB );
		}

		return rails;
	}

	/// <summary>
	/// One end's rail: the two tangent points with `segments - 1` arc points between them, or null
	/// if this end cannot carry an arc.
	///
	/// The centre is found twice — once from each tangent point, stepping off its own face by the
	/// radius — and the two answers agreeing is the check that this really is a fillet's corner
	/// rather than a corner some other edge has dragged elsewhere.
	/// </summary>
	static int[] Arc( PolyMesh result, int i0, int i1, Vec3 nA, Vec3 nB, float signedRadius,
		int segments, int vertex, PolyMesh source )
	{
		var rail = new int[segments + 1];

		// An end where both faces kept the same point is an uncut corner: the rail is that one
		// point repeated, so the strip's quads collapse to triangles converging on it. That is the
		// shape a fillet running out onto an unbevelled corner actually has.
		if ( i0 == i1 )
		{
			Array.Fill( rail, i0 );
			return rail;
		}

		var a0 = result.Positions[i0];
		var a1 = result.Positions[i1];

		var c0 = a0 - nA * signedRadius;
		var c1 = a1 - nB * signedRadius;

		if ( (c0 - c1).Length > CentreAgreement * MathF.Max( MathF.Abs( signedRadius ), 1f ) )
			return null;

		var centre = (c0 + c1) * 0.5f;

		var u = a0 - centre;
		var w = a1 - centre;

		if ( u.LengthSquared < 1e-12f || w.LengthSquared < 1e-12f )
			return null;

		rail[0] = i0;
		rail[segments] = i1;

		for ( var k = 1; k < segments; k++ )
		{
			var point = centre + Slerp( u, w, (float)k / segments );

			rail[k] = result.AddVertex( point );

			// Same "one point, one source" rule as a cut corner: this point is a cut of `vertex`
			// and nothing else, so it takes that vertex's weights rather than a blend.
			result.Skin?.Vertices.Add( source.IsRigged ? source.Skin[vertex] : Array.Empty<BoneWeight>() );
		}

		return rail;
	}

	/// <summary>
	/// Interpolate between two vectors along the arc between them rather than the chord — which is
	/// the whole difference between a fillet and a chamfer subdivided.
	///
	/// Falls back to a straight lerp when the two are nearly parallel, where the arc and the chord
	/// are the same thing to well inside float precision and the division is not.
	/// </summary>
	static Vec3 Slerp( Vec3 u, Vec3 w, float t )
	{
		var lengths = u.Length * w.Length;

		if ( lengths < 1e-12f )
			return Vec3.Lerp( u, w, t );

		var cos = Math.Clamp( Vec3.Dot( u, w ) / lengths, -1f, 1f );
		var omega = MathF.Acos( cos );
		var sin = MathF.Sin( omega );

		if ( sin < 1e-5f )
			return Vec3.Lerp( u, w, t );

		return u * (MathF.Sin( (1f - t) * omega ) / sin) + w * (MathF.Sin( t * omega ) / sin);
	}

	// --- setbacks -------------------------------------------------------------------------------

	/// <summary>A fillet setback may not exceed this multiple of its radius. r/tan(φ/2) runs away
	/// as an edge flattens out, and past here the "corner" being rounded is a gentle bend whose
	/// fillet would eat the faces either side of it.</summary>
	const float MaxSetback = 12f;

	/// <summary>
	/// How far back from each selected edge to cut.
	///
	/// A chamfer's distance is the setback outright. A fillet's radius is not: the tangent points
	/// of a circle of radius r against two faces opening at φ sit r/tan(φ/2) back from the edge.
	/// φ comes from the two normals — acos(-dot) — and needs no convexity test, because a 90° edge
	/// and a 270° edge set back by the same amount and only the arc's centre differs.
	/// </summary>
	static Dictionary<EdgeKey, float> Setbacks(
		HashSet<EdgeKey> selected, Dictionary<EdgeKey, List<int>> edgeFaces, Vec3[] faceNormals,
		float size, bool rounded )
	{
		var setbacks = new Dictionary<EdgeKey, float>( selected.Count );

		foreach ( var key in selected )
		{
			if ( !rounded )
			{
				setbacks[key] = size;
				continue;
			}

			var faces = edgeFaces[key];

			if ( faces.Count != 2 )
				continue;

			var cos = Math.Clamp( -Vec3.Dot( faceNormals[faces[0]], faceNormals[faces[1]] ), -1f, 1f );
			var half = MathF.Acos( cos ) * 0.5f;
			var tan = MathF.Tan( half );

			setbacks[key] = tan < 1f / MaxSetback ? size * MaxSetback : size / tan;
		}

		return setbacks;
	}

	/// <summary>Zero for an edge that was never selected, which CutCorner reads as "this line did
	/// not move".</summary>
	static float Setback( Dictionary<EdgeKey, float> setbacks, EdgeKey key ) =>
		setbacks.TryGetValue( key, out var width ) ? width : 0f;

	// --- unchanged from the flat-chamfer original -----------------------------------------------

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
	/// The new position for one face's corner at `v`, or null if neither incident edge was cut —
	/// meaning the corner is untouched and the caller should keep using `v` itself.
	///
	/// Each cut edge slides its supporting line inward, within the face's own plane, by that edge's
	/// own setback; the corner becomes the intersection of its two (possibly-slid) boundary lines.
	/// </summary>
	static Vec3? CutCorner( Vec3 prev, Vec3 v, Vec3 next, Vec3 faceNormal, float prevWidth, float nextWidth )
	{
		if ( prevWidth <= 0f && nextWidth <= 0f )
			return null;

		// Direction as the FACE traverses each edge — (prev,v) ending at v, (v,next) starting at
		// v — not "away from v". Cross(normal, direction) then points consistently inward for
		// every edge of a CCW polygon; get the sign backwards and every cut face turns inside
		// out while still looking fine in wireframe, the exact trap CatmullClark's own notes warn
		// about elsewhere in this kernel.
		var dirPrev = (v - prev).Normal;
		var dirNext = (next - v).Normal;

		var p1 = v;
		var d1 = dirPrev;

		if ( prevWidth > 0f )
			p1 += Vec3.Cross( faceNormal, dirPrev ).Normal * prevWidth;

		var p2 = v;
		var d2 = dirNext;

		if ( nextWidth > 0f )
			p2 += Vec3.Cross( faceNormal, dirNext ).Normal * nextWidth;

		// WHERE THE CORNER GOES WHEN THE TWO LINES WILL NOT INTERSECT USEFULLY.
		//
		// At a straight corner the two boundary lines are the same line, slid inward by the same
		// width in the same direction, so their "intersection" is undefined while the corner
		// itself is not: it is just the original vertex moved inward by width. That is this
		// fallback, and it is the right answer for the clamp below too — both are the case where
		// the intersection has stopped meaning anything.
		//
		// Taken from whichever edge was actually cut; when both are, they agree to within the
		// angle that made the intersection useless in the first place.
		var width = prevWidth > 0f ? prevWidth : nextWidth;

		var inward = prevWidth > 0f
			? Vec3.Cross( faceNormal, dirPrev ).Normal
			: Vec3.Cross( faceNormal, dirNext ).Normal;

		var fallback = v + inward * width;

		if ( IntersectCoplanarLines( p1, d1, p2, d2, faceNormal ) is not { } hit )
			return fallback;

		// A LOCAL CUT, SO A CORNER THAT TRAVELS MILES IS THE DEGENERATE CASE.
		//
		// The offset point sits at roughly width/sin(turn) from the vertex, so a corner that is
		// nearly straight throws it arbitrarily far. Ear clipping a thin annulus produces exactly
		// that: collinear corners (turn 180°, sin 1.5e-5) that put the point 15000 units away on a
		// model 20 units across. Those vertices are finite and the mesh still validates as closed
		// and manifold, which is why only a render ever showed it — the model collapses to a
		// speck because the view has to fit a stray vertex a thousand diameters out.
		//
		// Every honest cut lands within a few multiples of width: a square corner is about 1.4x,
		// and even a 5° needle only reaches ~11x. Past this it is degeneracy, not sharpness.
		return (hit - v).Length > width * MaxCornerOffset ? fallback : hit;
	}

	/// <summary>How far a cut corner may travel from its original vertex, as a multiple of the
	/// setback. See CutCorner for why a cap is needed at all and why this value is generous.
	/// </summary>
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
