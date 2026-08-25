using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Hollow a solid to a wall thickness, optionally opening chosen faces.
///
/// This is the operation that makes blocking out a room painless. Hammer's answer is a "hollow"
/// button that emits six overlapping brushes which leak at the seams; the answer here is one
/// number, and the walls meet exactly.
///
/// THE MATHS, BECAUSE THE OBVIOUS VERSION IS WRONG. The tempting implementation moves every vertex
/// along its own normal by the thickness. That is incorrect at any corner, and quietly so. A box
/// corner's area-weighted normal is (1,1,1)/sqrt(3); moving it 0.1 along that leaves each wall
/// 0.1/sqrt(3) = 0.058 thick. The model looks right and measures wrong, which is the worst kind of
/// bug to ship.
///
/// Thickness is a property of PLANES, not of vertices. So for each vertex this solves for the
/// displacement d that puts the vertex exactly `thickness` from every face plane meeting there:
///
///     for each adjacent face i:   dot( f_i, d ) = thickness,     minimising |d|
///
/// Minimising |d| matters as much as the constraints do. The system is underdetermined wherever
/// fewer than three distinct planes meet — which is the normal case along the rim of an opening,
/// where only the two side walls constrain the point. The least-norm solution slides the vertex
/// straight in and not along the rim, which is what keeps an opening's edge flush instead of
/// chamfered.
///
/// The solve itself lives in PlaneOffset, because bevel needs the identical thing one dimension
/// down — insetting a face corner is the same problem with the two edge normals taken inside the
/// face plane. At a box corner it returns exactly (0.1, 0.1, 0.1): the vertex travels t*sqrt(3),
/// not t.
///
/// SELF-INTERSECTION IS REFUSED, NOT HANDLED. Shell a shape by more than its own thinnest feature
/// and the inner surface passes through itself — the two walls of a plate shelled by more than half
/// its thickness cross over, and what comes back is a closed mesh that measures negative volume in
/// places and looks entirely normal from outside.
///
/// Building the offset surface properly, so that the overlap is trimmed away instead, is a much
/// larger algorithm and is still not here. What IS here is a check that catches every practical
/// case: a face whose inner copy has FLIPPED relative to its outer one has been pushed through
/// itself, and that is a local signature no amount of symmetry can hide. It is the same reasoning
/// LoopOffset uses one dimension down, where a reversed edge means the profile folded.
///
/// A shape can still self-intersect somewhere no single face flipped — two distant walls closing on
/// each other rather than one wall inverting. That case is not caught and is named here so the next
/// person knows the difference between "checked" and "not possible".
/// </summary>
public static class ShellOperation
{
	/// <summary>
	/// Refuse a thickness that pushes the inner surface through itself.
	///
	/// Each inner face is the outer one displaced inward, so as long as the wall is thinner than the
	/// shape it keeps its orientation. Push past that and the face turns over: its normal reverses,
	/// its area passes through zero on the way, and the solid it bounds starts enclosing negative
	/// space. Comparing each face's inner normal against its outer one catches that exactly, and
	/// costs one cross product per face.
	///
	/// The message names the thickness and the face, because "too thick" without a number is not
	/// something anyone can act on — the useful question is always "thicker than what".
	/// </summary>
	static void RefuseIfFolded( PolyMesh mesh, Vec3[] inner, HashSet<int> open, float thickness )
	{
		// THE CHECK THAT CATCHES THE COMMON CASE, and the reason the per-face one below is not
		// enough on its own. Shell a 1-thick plate by 0.6 and its top and bottom walls pass straight
		// through each other — but neither FACE inverts, because each is simply translated inward and
		// a translation preserves a normal. Nothing local goes wrong; the surface as a whole turns
		// inside out.
		//
		// Enclosed volume sees it immediately: the inner surface ends up with its top below its
		// bottom and measures negative. Exact only while the surface is closed, which is why it is
		// skipped once faces are opened — there the inner surface is genuinely not a closed volume,
		// and the per-face check is what remains.
		//
		// Opening a face also makes this failure much less likely in that direction, since an opened
		// face does not constrain the solve and so cannot push the opposite wall through anything.
		if ( open.Count == 0 )
		{
			var innerVolume = EnclosedVolume( mesh, inner );

			if ( innerVolume <= 0f )
			{
				throw new InvalidOperationException(
					$"A wall thickness of {thickness:0.###} is more than this shape is thick — the inner "
					+ "surface turns inside out, leaving no cavity. Use a smaller thickness." );
			}
		}

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( open.Contains( fi ) )
				continue;

			var face = mesh.Faces[fi];

			if ( face.Count < 3 )
				continue;

			var outerNormal = mesh.FaceNormal( face );
			var innerNormal = NewellNormal( face, inner );

			// Zero area means the face has been squeezed to nothing — the exact boundary of the fold
			// rather than past it, and just as unusable.
			if ( innerNormal.LengthSquared < 1e-16f )
			{
				throw new InvalidOperationException(
					$"A wall thickness of {thickness:0.###} collapses this shape: face {fi} has no inner surface left. "
					+ "The shape is that thin somewhere — use a smaller thickness." );
			}

			if ( Vec3.Dot( outerNormal, innerNormal.Normal ) < 0f )
			{
				throw new InvalidOperationException(
					$"A wall thickness of {thickness:0.###} is more than this shape is thick: the inner surface "
					+ $"passes through itself at face {fi}. Use a smaller thickness, or open that face." );
			}
		}
	}

	/// <summary>Divergence theorem over a face list read against a different position array: the
	/// volume the surface would enclose. Negative means it is inside out.</summary>
	static float EnclosedVolume( PolyMesh mesh, Vec3[] positions )
	{
		var total = 0f;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var normal = NewellNormal( face, positions );

			if ( normal.LengthSquared < 1e-20f )
				continue;

			// Newell's vector has twice the polygon's area as its length, so the area falls out of
			// it rather than needing a second pass.
			var area = normal.Length * 0.5f;
			var centroid = Vec3.Zero;

			foreach ( var index in face.Indices )
				centroid += positions[index];

			centroid /= face.Count;

			total += Vec3.Dot( centroid, normal.Normal ) * area;
		}

		return total / 3f;
	}

	/// <summary>Newell's method, over a face read against a different position array. The same
	/// reasoning as Triangulate's: three consecutive corners of a real face are often nearly
	/// collinear, and their cross product is then noise pointing anywhere.</summary>
	static Vec3 NewellNormal( Face face, Vec3[] positions )
	{
		var n = Vec3.Zero;

		for ( var i = 0; i < face.Count; i++ )
		{
			var a = positions[face.Indices[i]];
			var b = positions[face.Indices[(i + 1) % face.Count]];

			n = new Vec3(
				n.x + (a.y - b.y) * (a.z + b.z),
				n.y + (a.z - b.z) * (a.x + b.x),
				n.z + (a.x - b.x) * (a.y + b.y) );
		}

		return n;
	}

	public static PolyMesh Shell( PolyMesh mesh, float thickness, IEnumerable<int> openFaces = null )
		=> Shell( mesh, thickness, openFaces, out _ );

	/// <summary>As Shell, and reports how many vertices could not be solved exactly. Zero on every
	/// shape this kernel generates; worth surfacing if it ever is not.</summary>
	public static PolyMesh Shell( PolyMesh mesh, float thickness, IEnumerable<int> openFaces, out int approximatedVertices )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( thickness <= 0f )
			throw new InvalidOperationException( "Shell thickness must be positive" );

		var validation = MeshValidator.Validate( mesh );

		if ( !validation.IsValid )
			throw new InvalidOperationException( "Cannot shell an invalid mesh: " + validation );

		if ( !validation.IsClosed )
			throw new InvalidOperationException( "Cannot shell an open mesh — it has no inside to hollow" );

		var open = new HashSet<int>( openFaces ?? Enumerable.Empty<int>() );

		foreach ( var fi in open )
		{
			if ( fi < 0 || fi >= mesh.FaceCount )
				throw new ArgumentOutOfRangeException( nameof( openFaces ), $"face {fi} does not exist" );
		}

		if ( open.Count == mesh.FaceCount )
			throw new InvalidOperationException( "Cannot open every face — there would be nothing left" );

		var faceNormals = new Vec3[mesh.FaceCount];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			faceNormals[fi] = mesh.FaceNormal( mesh.Faces[fi] );

		var edgeFaces = mesh.BuildEdgeFaces();
		var rimEdges = FindRimEdges( edgeFaces, open );

		RejectPinchedOpenings( rimEdges );

		var vertexFaces = mesh.BuildVertexFaces();

		// --- inner surface positions -------------------------------------------------------
		var inner = new Vec3[mesh.VertexCount];
		approximatedVertices = 0;

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			// AN OPENED FACE MUST NOT CONSTRAIN THE SOLVE. It is being deleted, so requiring the
			// inner surface to stand `thickness` clear of it pulls the wall back from the opening
			// and turns the rim into a 45-degree chamfer instead of a flat band of wall.
			var planes = PlaneOffset.Distinct(
				vertexFaces[vi].Where( fi => !open.Contains( fi ) ).Select( fi => faceNormals[fi] ) );

			if ( planes.Count == 0 )
			{
				// Every face here was opened, so this vertex survives in neither surface. Its
				// position is never referenced; leaving it put is tidier than inventing one.
				inner[vi] = mesh.Positions[vi];
				continue;
			}

			if ( !PlaneOffset.TrySolve( planes, thickness, out var displacement ) )
				approximatedVertices++;

			inner[vi] = mesh.Positions[vi] - displacement;
		}

		RefuseIfFolded( mesh, inner, open, thickness );

		// --- assemble ----------------------------------------------------------------------
		// Layout: [0 .. V) the outer surface, [V .. 2V) the inner one.
		var result = new PolyMesh();

		foreach ( var p in mesh.Positions )
			result.AddVertex( p );

		foreach ( var p in inner )
			result.AddVertex( p );

		// The inner surface is the outer one displaced, so a rigged mesh keeps its rig: each inner
		// vertex carries the weights of the outer vertex it came from. Without this, shelling a
		// rigged body silently unrigs it — which is exactly what PolyMesh.Skin's own comment warns
		// about anything that rebuilds a vertex list.
		if ( mesh.IsRigged )
		{
			var skin = new SkinWeights();

			foreach ( var w in mesh.Skin.Vertices )
				skin.Vertices.Add( (BoneWeight[])w.Clone() );

			foreach ( var w in mesh.Skin.Vertices )
				skin.Vertices.Add( (BoneWeight[])w.Clone() );

			result.Skin = skin;
		}

		var offset = mesh.VertexCount;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( open.Contains( fi ) )
				continue;

			var f = mesh.Faces[fi];

			result.AddFace( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material );

			// The inner surface is the same sheet turned inside out: same connectivity, reversed
			// winding, so its normals face the cavity instead of the world.
			var n = f.Count;
			var innerIndices = new int[n];
			var innerUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				innerIndices[i] = f.Indices[n - 1 - i] + offset;
				innerUVs[i] = f.UVs[n - 1 - i];
			}

			result.AddFace( innerIndices, innerUVs, f.Material );
		}

		foreach ( var (edge, removed) in rimEdges )
			AddRimQuad( result, mesh, edge, removed, faceNormals, offset );

		return result;
	}

	/// <summary>
	/// Edges where exactly one of the two adjacent faces was opened — the border of the hole, and
	/// the only place a rim is needed. An edge between two opened faces is interior to the opening
	/// and simply disappears.
	/// </summary>
	static List<(EdgeKey Edge, int RemovedFace)> FindRimEdges( Dictionary<EdgeKey, List<int>> edgeFaces, HashSet<int> open )
	{
		var rim = new List<(EdgeKey, int)>();

		if ( open.Count == 0 )
			return rim;

		foreach ( var (edge, faces) in edgeFaces )
		{
			if ( faces.Count != 2 )
				continue;

			var openCount = faces.Count( open.Contains );

			if ( openCount == 1 )
				rim.Add( (edge, faces.First( open.Contains )) );
		}

		return rim;
	}

	/// <summary>
	/// Refuse an opening that pinches to a point.
	///
	/// If two opened faces meet at only a VERTEX — diagonally opposite quads of a subdivided face,
	/// say — then four rim edges meet there, and every rim quad built on them contains the same
	/// outer-to-inner edge. The result is an edge shared by four faces: non-manifold, and something
	/// no exporter or physics system will accept.
	///
	/// Splitting the vertex to separate the two rims is the proper fix and a much larger change.
	/// Until then this is refused loudly rather than returned broken, because a silently
	/// non-manifold mesh fails much later and somewhere else.
	/// </summary>
	static void RejectPinchedOpenings( List<(EdgeKey Edge, int RemovedFace)> rimEdges )
	{
		if ( rimEdges.Count == 0 )
			return;

		var incident = new Dictionary<int, int>();

		foreach ( var (edge, _) in rimEdges )
		{
			incident.TryGetValue( edge.A, out var a );
			incident[edge.A] = a + 1;
			incident.TryGetValue( edge.B, out var b );
			incident[edge.B] = b + 1;
		}

		foreach ( var (vertex, count) in incident )
		{
			// A vertex on a simple boundary loop has exactly two rim edges. More means two separate
			// stretches of rim meet there.
			if ( count > 2 )
				throw new InvalidOperationException(
					$"The opened faces pinch to a point at vertex {vertex}: {count} rim edges meet there, "
					+ "which would produce a non-manifold mesh. Open faces that share an edge, or leave a "
					+ "face between them." );
		}
	}

	static void AddRimQuad( PolyMesh result, PolyMesh source, EdgeKey edge, int removedFace, Vec3[] faceNormals, int offset )
	{
		var quad = new[] { edge.A, edge.B, edge.B + offset, edge.A + offset };
		var uvs = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };

		// The rim faces the way the removed face did. Rather than deriving the winding from the kept
		// face's traversal order — which depends on the mesh's own conventions and is easy to get
		// backwards — build it, measure it, and flip it if it points inward. Same posture as the
		// revolve sweep check, and for the same reason: an inverted face is invisible in wireframe.
		if ( Vec3.Dot( NewellNormal( result, quad ), faceNormals[removedFace] ) < 0f )
		{
			// Reverse the UVs alongside the indices, or a flipped quad ends up mirrored relative to
			// its unflipped neighbours along the same rim.
			Array.Reverse( quad );
			Array.Reverse( uvs );
		}

		result.AddFace( quad, uvs, source.Faces[removedFace].Material );
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
