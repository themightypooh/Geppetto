using System;
using System.Collections.Generic;

namespace ModelKernel;

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
			current = SubdivideOnce( current );

		return current;
	}

	static PolyMesh SubdivideOnce( PolyMesh mesh )
	{
		var edgeFaces = mesh.BuildEdgeFaces();
		var vertexFaces = mesh.BuildVertexFaces();
		var vertexEdges = mesh.BuildVertexEdges();

		// Stable index per edge, so edge points can live in a contiguous block of the new vertex
		// list rather than in a dictionary that later lookups have to chase.
		var edgeIndex = new Dictionary<EdgeKey, int>( edgeFaces.Count );
		var edgeList = new List<EdgeKey>( edgeFaces.Count );

		foreach ( var key in edgeFaces.Keys )
		{
			edgeIndex[key] = edgeList.Count;
			edgeList.Add( key );
		}

		var vertCount = mesh.VertexCount;
		var edgeCount = edgeList.Count;
		var faceCount = mesh.FaceCount;

		// Layout of the new vertex list:
		//   [0 .. V)                 updated original vertices
		//   [V .. V+E)               edge points
		//   [V+E .. V+E+F)           face points
		var newPositions = new Vec3[vertCount + edgeCount + faceCount];

		// --- face points -------------------------------------------------------------------
		var facePoints = new Vec3[faceCount];

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			facePoints[fi] = mesh.FaceCentroid( mesh.Faces[fi] );
			newPositions[vertCount + edgeCount + fi] = facePoints[fi];
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
				continue;
			}

			var faceSum = Vec3.Zero;

			foreach ( var fi in faces )
				faceSum += facePoints[fi];

			// (v0 + v1 + average of adjacent face points) weighted as the standard 4-point rule.
			// Written to average over faces.Count rather than assuming 2, so a non-manifold edge
			// degrades to something sane instead of reading past the end.
			newPositions[vertCount + ei] = (a + b + faceSum / faces.Count * 2f) * 0.25f;
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
				continue;
			}

			if ( boundaryNeighbours.Count > 2 )
			{
				// A corner where several borders meet. Nothing sensible to interpolate, so pin it.
				newPositions[vi] = v;
				continue;
			}

			var faces = vertexFaces[vi];
			var n = faces.Count;

			if ( n < 3 )
			{
				// Valence 1 or 2 interior vertices are degenerate; the (n-3)/n term misbehaves.
				// Pinning is the least surprising thing to do with them.
				newPositions[vi] = v;
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
		}

		// --- new faces ---------------------------------------------------------------------
		var result = new PolyMesh { Positions = new List<Vec3>( newPositions ) };

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

		return result;
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
