using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Which source element a subdivided vertex came from. The layout of SubdivideOnce is
/// a contract: originals, then edge points, then face points, in that order.</summary>
public enum SubdivisionOrigin
{
	Original,
	Edge,
	Face
}

/// <summary>
/// One output vertex of a single Catmull-Clark step, named by the source element it came from.
///
/// Original: A is the source vertex index, B is unused.
/// Edge:     A, B are the source endpoints, already sorted A &lt; B.
/// Face:     A is the source face index, B is unused.
/// </summary>
public readonly struct SubdivisionVertex
{
	public readonly SubdivisionOrigin Origin;
	public readonly int A;
	public readonly int B;

	public SubdivisionVertex( SubdivisionOrigin origin, int a, int b = -1 )
	{
		Origin = origin;
		A = a;
		B = b;
	}
}

/// <summary>
/// Per-vertex correspondence for one subdivision step. A sculpt stores deltas against these
/// indices, so the map has to be identical for the same topology on every rebuild — not merely
/// "the same in practice on this runtime".
/// </summary>
public sealed class SubdivisionMap
{
	public readonly SubdivisionVertex[] Vertices;
	public readonly int SourceVertexCount;
	public readonly int SourceEdgeCount;
	public readonly int SourceFaceCount;

	public SubdivisionMap( SubdivisionVertex[] vertices, int sourceVertexCount, int sourceEdgeCount, int sourceFaceCount )
	{
		Vertices = vertices;
		SourceVertexCount = sourceVertexCount;
		SourceEdgeCount = sourceEdgeCount;
		SourceFaceCount = sourceFaceCount;
	}

	public int OutputVertexCount => Vertices.Length;
}

/// <summary>
/// Catmull-Clark subdivision.
///
/// This is the bridge between the two halves of the tool: the parametric stage produces a coarse
/// quad cage, this turns it into something dense enough to sculpt, and the cage survives
/// underneath as the low-poly the sculpt eventually bakes down onto. That is the whole reason the
/// pipeline starts parametric — the cage IS the retopology, produced as a side effect rather than
/// as a step somebody has to do.
///
/// EVERY OUTPUT FACE IS A QUAD, whatever went in. An n-gon becomes n quads, a triangle becomes 3.
/// So triangles in the input are survivable but not free: they produce valence-3 extraordinary
/// vertices that stay extraordinary at every level and pucker visibly under a sculpt brush. Keep
/// the primitives quad-dominant.
///
/// Boundaries use the cubic B-spline curve rules, so an open mesh keeps its border instead of
/// shrinking away from it.
/// </summary>
public static class CatmullClark
{
	/// <summary>Subdivide `levels` times. Level 0 returns a clone, so callers can always treat the
	/// result as theirs to mutate.</summary>
	public static PolyMesh Subdivide( PolyMesh mesh, int levels = 1 )
	{
		if ( levels < 0 )
			throw new ArgumentOutOfRangeException( nameof( levels ) );

		var current = mesh.Clone();

		for ( var i = 0; i < levels; i++ )
			current = SubdivideOnce( current ).Mesh;

		return current;
	}

	/// <summary>
	/// One subdivision step plus the correspondence map. Sculpt deltas are stored per output
	/// vertex; this is how those vertices name the cage elements they came from.
	/// </summary>
	public static (PolyMesh Mesh, SubdivisionMap Map) SubdivideWithMap( PolyMesh mesh ) =>
		SubdivideOnce( mesh );

	static (PolyMesh Mesh, SubdivisionMap Map) SubdivideOnce( PolyMesh mesh )
	{
		var edgeFaces = mesh.BuildEdgeFaces();
		var vertexFaces = mesh.BuildVertexFaces();
		var vertexEdges = mesh.BuildVertexEdges();

		// Edge-point indices are a persisted contract, not an implementation accident. Dictionary
		// enumeration is insertion order in practice and is not promised across a rebuild or a
		// runtime. Sort by the already-canonical (A, B) so the same topology always produces the
		// same edge block.
		var edgeList = new List<EdgeKey>( edgeFaces.Keys );
		edgeList.Sort( ( a, b ) =>
		{
			var cmp = a.A.CompareTo( b.A );
			return cmp != 0 ? cmp : a.B.CompareTo( b.B );
		} );

		var edgeIndex = new Dictionary<EdgeKey, int>( edgeList.Count );

		for ( var i = 0; i < edgeList.Count; i++ )
			edgeIndex[edgeList[i]] = i;

		var vertCount = mesh.VertexCount;
		var edgeCount = edgeList.Count;
		var faceCount = mesh.FaceCount;

		// Layout of the new vertex list — this IS the correspondence:
		//   [0 .. V)                 updated original vertices
		//   [V .. V+E)               edge points, sorted by (A, B)
		//   [V+E .. V+E+F)           face points, in source face order
		var newPositions = new Vec3[vertCount + edgeCount + faceCount];
		var mapVertices = new SubdivisionVertex[newPositions.Length];

		for ( var vi = 0; vi < vertCount; vi++ )
			mapVertices[vi] = new SubdivisionVertex( SubdivisionOrigin.Original, vi );

		for ( var ei = 0; ei < edgeCount; ei++ )
		{
			var key = edgeList[ei];
			mapVertices[vertCount + ei] = new SubdivisionVertex( SubdivisionOrigin.Edge, key.A, key.B );
		}

		for ( var fi = 0; fi < faceCount; fi++ )
			mapVertices[vertCount + edgeCount + fi] = new SubdivisionVertex( SubdivisionOrigin.Face, fi );

		// Skin weights ride along through every rule below, using THE SAME COEFFICIENTS as the
		// positions. That is not a nicety: every Catmull-Clark rule is an affine combination — its
		// coefficients sum to 1 — so applying it to weights preserves "non-negative and sums to 1"
		// automatically. A rigged cage stays correctly rigged at level 4 with no renormalisation
		// step and no special cases. There is a test asserting exactly that.
		var rigged = mesh.IsRigged;
		var newWeights = rigged ? new BoneWeight[newPositions.Length][] : null;
		var facePointWeights = rigged ? new BoneWeight[faceCount][] : null;

		// --- face points -------------------------------------------------------------------
		var facePoints = new Vec3[faceCount];

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			facePoints[fi] = mesh.FaceCentroid( mesh.Faces[fi] );
			newPositions[vertCount + edgeCount + fi] = facePoints[fi];

			if ( !rigged )
				continue;

			var face = mesh.Faces[fi];
			var share = 1f / face.Count;
			var corners = new List<(BoneWeight[], float)>( face.Count );

			foreach ( var ci in face.Indices )
				corners.Add( (mesh.Skin[ci], share) );

			facePointWeights[fi] = SkinWeights.Blend( corners );
			newWeights[vertCount + edgeCount + fi] = facePointWeights[fi];
		}

		// --- edge points -------------------------------------------------------------------
		for ( var ei = 0; ei < edgeCount; ei++ )
		{
			var key = edgeList[ei];
			var a = mesh.Positions[key.A];
			var b = mesh.Positions[key.B];
			var faces = edgeFaces[key];

			if ( faces.Count == 1 )
			{
				// Boundary edge: plain midpoint. Pulling it toward the single adjacent face point
				// would drag the border inward, which is exactly the "my open mesh shrank" bug.
				newPositions[vertCount + ei] = (a + b) * 0.5f;

				if ( rigged )
					newWeights[vertCount + ei] = SkinWeights.Blend(
						(mesh.Skin[key.A], 0.5f), (mesh.Skin[key.B], 0.5f) );

				continue;
			}

			var faceSum = Vec3.Zero;

			foreach ( var fi in faces )
				faceSum += facePoints[fi];

			// (v0 + v1 + average of adjacent face points) weighted as the standard 4-point rule.
			// Written to average over faces.Count rather than assuming 2, so a non-manifold edge
			// degrades to something sane instead of reading past the end.
			newPositions[vertCount + ei] = (a + b + faceSum / faces.Count * 2f) * 0.25f;

			if ( !rigged )
				continue;

			var edgeTerms = new List<(BoneWeight[], float)>( faces.Count + 2 )
			{
				(mesh.Skin[key.A], 0.25f),
				(mesh.Skin[key.B], 0.25f)
			};

			var facePointShare = 0.5f / faces.Count;

			foreach ( var fi in faces )
				edgeTerms.Add( (facePointWeights[fi], facePointShare) );

			newWeights[vertCount + ei] = SkinWeights.Blend( edgeTerms );
		}

		// --- updated original vertices -----------------------------------------------------
		for ( var vi = 0; vi < vertCount; vi++ )
		{
			var v = mesh.Positions[vi];
			var edges = vertexEdges[vi];

			// Collect boundary edges at this vertex. A boundary vertex has exactly two; it follows
			// the curve rule and ignores the surface entirely, which is what keeps a border crisp.
			var boundaryNeighbours = new List<int>( 2 );

			foreach ( var key in edges )
			{
				if ( edgeFaces[key].Count == 1 )
					boundaryNeighbours.Add( key.A == vi ? key.B : key.A );
			}

			if ( boundaryNeighbours.Count == 2 )
			{
				// Cubic B-spline: (p_prev + 6v + p_next) / 8.
				newPositions[vi] =
					(mesh.Positions[boundaryNeighbours[0]]
					 + v * 6f
					 + mesh.Positions[boundaryNeighbours[1]]) / 8f;

				if ( rigged )
					newWeights[vi] = SkinWeights.Blend(
						(mesh.Skin[boundaryNeighbours[0]], 0.125f),
						(mesh.Skin[vi], 0.75f),
						(mesh.Skin[boundaryNeighbours[1]], 0.125f) );

				continue;
			}

			if ( boundaryNeighbours.Count > 2 )
			{
				// A corner where several borders meet. Nothing sensible to interpolate, so pin it.
				newPositions[vi] = v;

				if ( rigged )
					newWeights[vi] = mesh.Skin[vi];

				continue;
			}

			var faces = vertexFaces[vi];
			var n = faces.Count;

			if ( n < 3 )
			{
				// Valence 1 or 2 interior vertices are degenerate; the (n-3)/n term misbehaves.
				// Pinning is the least surprising thing to do with them.
				newPositions[vi] = v;

				if ( rigged )
					newWeights[vi] = mesh.Skin[vi];

				continue;
			}

			// F: average of adjacent face points.
			var f = Vec3.Zero;

			foreach ( var fi in faces )
				f += facePoints[fi];

			f /= n;

			// R: average of adjacent EDGE MIDPOINTS — not of the edge points computed above. This
			// is the single most commonly mis-implemented line in Catmull-Clark, and using edge
			// points here produces a surface that looks nearly right and shrinks slightly wrong.
			var r = Vec3.Zero;

			foreach ( var key in edges )
				r += (mesh.Positions[key.A] + mesh.Positions[key.B]) * 0.5f;

			r /= edges.Count;

			newPositions[vi] = (f + r * 2f + v * (n - 3)) / n;

			if ( !rigged )
				continue;

			// The same rule, expanded to per-input coefficients:
			//   each adjacent face point   1/n^2        (F is itself an average over n faces)
			//   each adjacent edge endpoint 1/(n*E)     (R averages midpoints, each half its ends)
			//   the vertex itself           (n-3)/n
			// which sums to (1 + 2 + n-3)/n = 1, as it must.
			var vertexTerms = new List<(BoneWeight[], float)>( n + edges.Count * 2 + 1 );

			foreach ( var fi in faces )
				vertexTerms.Add( (facePointWeights[fi], 1f / (n * n)) );

			var endpointShare = 1f / (n * edges.Count);

			foreach ( var key in edges )
			{
				vertexTerms.Add( (mesh.Skin[key.A], endpointShare) );
				vertexTerms.Add( (mesh.Skin[key.B], endpointShare) );
			}

			vertexTerms.Add( (mesh.Skin[vi], (n - 3f) / n) );
			newWeights[vi] = SkinWeights.Blend( vertexTerms );
		}

		// --- new faces ---------------------------------------------------------------------
		var result = new PolyMesh { Positions = new List<Vec3>( newPositions ) };

		if ( rigged )
			result.Skin = new SkinWeights { Vertices = new List<BoneWeight[]>( newWeights ) };

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			var n = face.Count;
			var facePointIdx = vertCount + edgeCount + fi;

			// UVs are subdivided using only this face's own corner UVs. Because nothing outside
			// the face is consulted, a seam — where two faces disagree about the UV at a shared
			// position — survives subdivision automatically. That is why UVs live per corner.
			var faceUV = Vec2.Zero;

			foreach ( var uv in face.UVs )
				faceUV += uv;

			faceUV /= n;

			for ( var i = 0; i < n; i++ )
			{
				var prev = (i - 1 + n) % n;
				var next = (i + 1) % n;

				var vCur = face.Indices[i];
				var eNext = vertCount + edgeIndex[new EdgeKey( vCur, face.Indices[next] )];
				var ePrev = vertCount + edgeIndex[new EdgeKey( face.Indices[prev], vCur )];

				// Winding runs corner → forward edge → centre → backward edge, which preserves the
				// original face's orientation.
				result.AddFace(
					new[] { vCur, eNext, facePointIdx, ePrev },
					new[]
					{
						face.UVs[i],
						(face.UVs[i] + face.UVs[next]) * 0.5f,
						faceUV,
						(face.UVs[prev] + face.UVs[i]) * 0.5f
					},
					face.Material );
			}
		}

		return (result, new SubdivisionMap( mapVertices, vertCount, edgeCount, faceCount ));
	}

	/// <summary>
	/// Subdivide only the faces named, leaving the rest of the mesh alone.
	///
	/// LINEAR, NOT SMOOTH, and that is the whole difference from <see cref="Subdivide"/>. The
	/// Catmull-Clark limit rules move every original vertex, so applying them to part of a mesh
	/// would drag the border of the selection — and with it the untouched faces sharing those
	/// vertices — off the shape you were looking at. Splitting at midpoints and centroids adds
	/// density and changes nothing else, which is the only behaviour that makes "these faces, not
	/// that ear" a sane thing to ask for. Want the smoothing too? Subdivide the whole body: the
	/// unselected form of this feature still runs the full Catmull-Clark.
	///
	/// NEIGHBOURS GET STITCHED, NOT SUBDIVIDED. A face next to the selection shares edges that have
	/// just gained a midpoint. Leaving it as it was would put that midpoint in the middle of its
	/// straight edge — a T-junction, which is a crack the moment anything moves either vertex, and
	/// a triangulation ambiguity before that. So the new point is inserted into the neighbour's own
	/// loop. It becomes an n-gon of exactly the same shape (the point is ON the edge) and the two
	/// faces still agree about every vertex along the seam.
	/// </summary>
	public static PolyMesh SubdivideFaces( PolyMesh mesh, IEnumerable<int> faces, int levels = 1 )
	{
		if ( levels < 0 )
			throw new ArgumentOutOfRangeException( nameof( levels ) );

		var selected = new HashSet<int>();

		foreach ( var fi in faces )
		{
			if ( fi >= 0 && fi < mesh.FaceCount )
				selected.Add( fi );
		}

		var current = mesh.Clone();

		for ( var i = 0; i < levels && selected.Count > 0; i++ )
			current = SubdivideFacesOnce( current, selected, out selected );

		return current;
	}

	/// <summary>One local step. <paramref name="nextSelected"/> comes back holding the quads the
	/// selection became, so a second level subdivides the same region rather than spreading into
	/// the neighbours that were only stitched.</summary>
	static PolyMesh SubdivideFacesOnce( PolyMesh mesh, HashSet<int> selected, out HashSet<int> nextSelected )
	{
		var edgeFaces = mesh.BuildEdgeFaces();

		// Every edge of a selected face splits — including the ones shared with a face that is not
		// selected, which is what the stitching below then has to account for.
		var splitList = new List<EdgeKey>();

		foreach ( var (key, users) in edgeFaces )
		{
			foreach ( var fi in users )
			{
				if ( !selected.Contains( fi ) )
					continue;

				splitList.Add( key );
				break;
			}
		}

		// Sorted for the same reason SubdivideOnce sorts: dictionary order is not a promise, and
		// the vertex block this produces has to be the same on every rebuild.
		splitList.Sort( ( a, b ) =>
		{
			var cmp = a.A.CompareTo( b.A );
			return cmp != 0 ? cmp : a.B.CompareTo( b.B );
		} );

		var rigged = mesh.IsRigged;

		// Originals are COPIED UNMOVED. See the summary: this is the property the whole operation
		// is for, and it is also what lets the untouched half of the mesh keep its exact positions.
		var result = new PolyMesh { Positions = new List<Vec3>( mesh.Positions ) };

		if ( rigged )
			result.Skin = new SkinWeights { Vertices = new List<BoneWeight[]>( mesh.Skin.Vertices ) };

		var splitIndex = new Dictionary<EdgeKey, int>( splitList.Count );

		foreach ( var key in splitList )
		{
			splitIndex[key] = result.Positions.Count;
			result.Positions.Add( (mesh.Positions[key.A] + mesh.Positions[key.B]) * 0.5f );

			if ( rigged )
				result.Skin.Vertices.Add( SkinWeights.Blend(
					(mesh.Skin[key.A], 0.5f), (mesh.Skin[key.B], 0.5f) ) );
		}

		nextSelected = new HashSet<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			var n = face.Count;

			if ( !selected.Contains( fi ) )
			{
				StitchNeighbour( result, face, splitIndex );
				continue;
			}

			var centre = result.Positions.Count;
			result.Positions.Add( mesh.FaceCentroid( face ) );

			if ( rigged )
			{
				var share = 1f / n;
				var corners = new List<(BoneWeight[], float)>( n );

				foreach ( var ci in face.Indices )
					corners.Add( (mesh.Skin[ci], share) );

				result.Skin.Vertices.Add( SkinWeights.Blend( corners ) );
			}

			// As in SubdivideOnce, UVs come only from this face's own corners, so a seam survives.
			var faceUV = Vec2.Zero;

			foreach ( var uv in face.UVs )
				faceUV += uv;

			faceUV /= n;

			for ( var i = 0; i < n; i++ )
			{
				var prev = (i - 1 + n) % n;
				var next = (i + 1) % n;

				var vCur = face.Indices[i];
				var eNext = splitIndex[new EdgeKey( vCur, face.Indices[next] )];
				var ePrev = splitIndex[new EdgeKey( face.Indices[prev], vCur )];

				result.AddFace(
					new[] { vCur, eNext, centre, ePrev },
					new[]
					{
						face.UVs[i],
						(face.UVs[i] + face.UVs[next]) * 0.5f,
						faceUV,
						(face.UVs[prev] + face.UVs[i]) * 0.5f
					},
					face.Material );

				nextSelected.Add( result.FaceCount - 1 );
			}
		}

		return result;
	}

	/// <summary>Copy a face that was not selected, threading in any midpoint a selected neighbour
	/// put on one of its edges.</summary>
	static void StitchNeighbour( PolyMesh result, Face face, Dictionary<EdgeKey, int> splitIndex )
	{
		var n = face.Count;
		var indices = new List<int>( n * 2 );
		var uvs = new List<Vec2>( n * 2 );

		for ( var i = 0; i < n; i++ )
		{
			var next = (i + 1) % n;

			indices.Add( face.Indices[i] );
			uvs.Add( face.UVs[i] );

			if ( !splitIndex.TryGetValue( new EdgeKey( face.Indices[i], face.Indices[next] ), out var ep ) )
				continue;

			indices.Add( ep );
			uvs.Add( (face.UVs[i] + face.UVs[next]) * 0.5f );
		}

		result.AddFace( indices.ToArray(), uvs.ToArray(), face.Material );
	}

	/// <summary>
	/// What <see cref="SubdivideFaces"/> will cost. Exact rather than a rule of thumb: after the
	/// first level the selected region is all quads, so its face and edge counts are known.
	/// </summary>
	public static (int Vertices, int Faces) PredictLocalCost( PolyMesh mesh, IEnumerable<int> faces, int levels )
	{
		var selected = new HashSet<int>();

		foreach ( var fi in faces )
		{
			if ( fi >= 0 && fi < mesh.FaceCount )
				selected.Add( fi );
		}

		var v = mesh.VertexCount;
		var f = mesh.FaceCount;

		if ( selected.Count == 0 )
			return (v, f);

		// Region counts: faces selected, distinct edges around and inside them, corners.
		var regionEdges = new HashSet<EdgeKey>();
		var corners = 0;

		foreach ( var fi in selected )
		{
			var face = mesh.Faces[fi];
			corners += face.Count;

			for ( var i = 0; i < face.Count; i++ )
				regionEdges.Add( new EdgeKey( face.Indices[i], face.Indices[(i + 1) % face.Count] ) );
		}

		var regionFaces = selected.Count;
		var edges = regionEdges.Count;

		for ( var i = 0; i < levels; i++ )
		{
			// A midpoint per split edge and a centre per selected face; every other vertex, and
			// every face outside the region, is carried over untouched.
			v += edges + regionFaces;
			f += corners - regionFaces;

			// The region afterwards: one quad per corner, each old edge halved plus one spoke to
			// each centre.
			edges = edges * 2 + corners;
			regionFaces = corners;
			corners = regionFaces * 4;
		}

		return (v, f);
	}

	/// <summary>
	/// What Subdivide will cost, without running it. Cheap enough to call on every parameter change
	/// so a level slider can warn before it makes eight million vertices rather than after.
	///
	/// For a closed mesh each level gives V' = V+E+F, F' = 2E, E' = 4E.
	/// </summary>
	public static (int Vertices, int Faces) PredictCost( PolyMesh mesh, int levels )
	{
		var v = mesh.VertexCount;
		var e = mesh.BuildEdgeFaces().Count;
		var f = mesh.FaceCount;

		for ( var i = 0; i < levels; i++ )
		{
			var corners = 0;

			// Only exact for the first level; afterwards everything is quads, so 4 per face.
			if ( i == 0 )
			{
				foreach ( var face in mesh.Faces )
					corners += face.Count;
			}
			else
			{
				corners = f * 4;
			}

			var nv = v + e + f;
			var nf = corners;
			var ne = e * 2 + corners;

			v = nv;
			f = nf;
			e = ne;
		}

		return (v, f);
	}
}
