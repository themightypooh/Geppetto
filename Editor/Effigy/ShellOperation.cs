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
	static void RefuseIfFolded( PolyMesh mesh, Vec3[] inner, HashSet<int> open, float thickness, float[] outerAreas, float sliver )
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

			// A face that was a sliver outside is a sliver inside too, and that is not a collapse:
			// there was never any surface there to lose. Its neighbours are checked like any other.
			if ( outerAreas[fi] <= sliver )
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
		Vec3[] positionsArray = null;

		foreach ( var fi in open )
		{
			if ( fi < 0 || fi >= mesh.FaceCount )
				throw new ArgumentOutOfRangeException( nameof( openFaces ), $"face {fi} does not exist" );
		}

		if ( open.Count == mesh.FaceCount )
			throw new InvalidOperationException( "Cannot open every face — there would be nothing left" );

		var faceNormals = new Vec3[mesh.FaceCount];
		var faceAreas = new float[mesh.FaceCount];
		var totalArea = 0f;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			faceNormals[fi] = mesh.FaceNormal( mesh.Faces[fi] );
			faceAreas[fi] = NewellNormal( mesh.Faces[fi], positionsArray ??= mesh.Positions.ToArray() ).Length * 0.5f;
			totalArea += faceAreas[fi];
		}

		// A SLIVER HAS NO PLANE WORTH OBEYING. A voxel skin (Blob, Voxel remesh, the voxel boolean)
		// is full of quads squeezed to almost nothing where the surface crosses a cell corner, and the
		// normal of one of those is noise. Asking a vertex to stand `thickness` clear of a random plane
		// is what threw vertices across the cavity and made a 0.2 wall "collapse" an 8 in ellipsoid.
		var sliver = totalArea / Math.Max( mesh.FaceCount, 1 ) * 1e-3f;
		bool Planar( int fi ) => faceAreas[fi] > sliver && faceNormals[fi].LengthSquared > 0.5f;

		Vec3 AreaNormal( List<int> faces )
		{
			var sum = Vec3.Zero;
			foreach ( var fi in faces ) sum += faceNormals[fi] * MathF.Max( faceAreas[fi], 1e-12f );
			return sum.LengthSquared > 1e-24f ? sum.Normal : faceNormals[faces[0]];
		}

		var edgeFaces = mesh.BuildEdgeFaces();
		var rimEdges = FindRimEdges( edgeFaces, open );

		RejectPinchedOpenings( rimEdges );

		var vertexFaces = mesh.BuildVertexFaces();

		// --- inner surface positions -------------------------------------------------------
		var inner = new Vec3[mesh.VertexCount];
		approximatedVertices = 0;
		var unplaced = new List<int>();
		var unplacedSet = new HashSet<int>();

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			// AN OPENED FACE MUST NOT CONSTRAIN THE SOLVE. It is being deleted, so requiring the
			// inner surface to stand `thickness` clear of it pulls the wall back from the opening
			// and turns the rim into a 45-degree chamfer instead of a flat band of wall.
			var kept = vertexFaces[vi].Where( fi => !open.Contains( fi ) ).ToList();

			if ( kept.Count == 0 )
			{
				// Every face here was opened, so this vertex survives in neither surface. Its
				// position is never referenced; leaving it put is tidier than inventing one.
				inner[vi] = mesh.Positions[vi];
				continue;
			}

			var solid = kept.Where( Planar ).ToList();
			var planes = PlaneOffset.Distinct( (solid.Count > 0 ? solid : kept).Select( fi => faceNormals[fi] ) );
			var exact = PlaneOffset.TrySolve( planes, thickness, out var displacement );

			// AN ILL-CONDITIONED SOLVE IS NOT AN ANSWER. Several almost-coplanar planes - every vertex
			// of a smooth organic surface - make the normal equations nearly singular, and their
			// least-squares fit can be many walls deep in the one direction they barely constrain. A
			// real corner never needs that: a box corner is t*sqrt(3), a 30-degree knife edge about
			// 4t. Past that, or pointing outward, take the even-thickness offset along the vertex's
			// area-weighted normal instead (Blender's Solidify "Even Thickness"), and count it.
			if ( !exact && (displacement.Length > thickness * 4f || Vec3.Dot( displacement, AreaNormal( solid.Count > 0 ? solid : kept ) ) <= 0f) )
			{
				var n = AreaNormal( solid.Count > 0 ? solid : kept );
				var worst = 1f;

				foreach ( var fi in solid.Count > 0 ? solid : kept )
					worst = MathF.Min( worst, Vec3.Dot( faceNormals[fi], n ) );

				displacement = n * (thickness / MathF.Max( worst, 0.25f ));
			}

			if ( !exact )
				approximatedVertices++;

			inner[vi] = mesh.Positions[vi] - displacement;

			if ( solid.Count == 0 )
			{
				unplaced.Add( vi );
				unplacedSet.Add( vi );
			}
		}

		// A vertex touching nothing but slivers has no plane of its own to stand clear of, so it
		// takes its neighbours' step - otherwise it stays on the outer skin and the wall is zero there.
		for ( var pass = 0; pass < 4 && unplaced.Count > 0; pass++ )
		{
			var still = new List<int>();

			foreach ( var vi in unplaced )
			{
				var sum = Vec3.Zero;
				var count = 0;

				foreach ( var fi in vertexFaces[vi] )
					foreach ( var other in mesh.Faces[fi].Indices )
					{
						if ( other == vi || unplacedSet.Contains( other ) )
							continue;

						sum += mesh.Positions[other] - inner[other];
						count++;
					}

				if ( count == 0 ) { still.Add( vi ); continue; }
				inner[vi] = mesh.Positions[vi] - sum / count;
			}

			foreach ( var vi in unplaced )
				if ( !still.Contains( vi ) ) unplacedSet.Remove( vi );

			unplaced = still;
		}

		RefuseIfFolded( mesh, inner, open, thickness, faceAreas, sliver );

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

/// <summary>
/// Give a surface a uniform thickness, the inverse of <see cref="ShellOperation"/>.
///
/// Shell hollows a closed solid to a wall thickness; this turns a surface into a slab of that
/// thickness. The clothing case is an open garment mesh with boundary edges and no inside — solidify
/// produces two walls (the original surface and one offset along its normals, facing opposite ways)
/// joined by a rim around the boundary, so the garment becomes a closed thin solid ready to export.
/// A closed mesh is inflated into a hollow shell instead.
///
/// The offset uses the same plane solve as <see cref="ShellOperation"/> rather than a naive
/// vertex-normal push, for the same reason that solve exists: thickness is a property of planes, and
/// at a corner only the plane-constrained solve puts every wall exactly `thickness` away.
/// Self-intersection is not trimmed away here — solidifying a surface past a concave feature can
/// still fold the offset surface through itself, and that is left to a caller who checks, rather
/// than silently repaired.
/// </summary>
public static class MeshSolidify
{
	/// <param name="smooth">Offset along the averaged vertex normal instead of solving the face
	/// planes. For cloth: the plane solve keeps a hard corner exact, and on a draped crease - two
	/// faces nearly facing each other - it sent the outer wall under the inner one, which showed
	/// through as dark specks all over a shirt. A smooth surface wants a smooth offset.</param>
	public static PolyMesh Solidify( PolyMesh mesh, float thickness, bool smooth = false )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var validation = MeshValidator.Validate( mesh );

		if ( !validation.IsValid )
			throw new InvalidOperationException( "Cannot solidify an invalid mesh: " + validation );

		if ( thickness == 0f )
			return mesh.Clone();

		// A boundary vertex where two openings only touch — the ankle, where the foot hull and the
		// shin hull meet at a point — has four boundary edges. Every rim quad then contains the
		// same outer-to-inner edge, and that edge is shared by four faces. Split the fans first so
		// each opening gets its own copy of the vertex. The copies stay on the same point. The
		// caller's mesh is left alone: the split is on a clone.
		mesh = mesh.Clone();
		SplitPinchedBoundary( mesh );

		var faceNormals = new Vec3[mesh.FaceCount];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			faceNormals[fi] = mesh.FaceNormal( mesh.Faces[fi] );

		var vertexFaces = mesh.BuildVertexFaces();
		var smoothNormals = smooth ? mesh.ComputeVertexNormals() : null;

		// Offset every vertex so each face plane it touches stands `thickness` away along its normal.
		// A positive thickness offsets along the face normal (outward), a negative one against it.
		var offsetPositions = new Vec3[mesh.VertexCount];

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			if ( smoothNormals is not null )
			{
				offsetPositions[vi] = mesh.Positions[vi] + smoothNormals[vi] * thickness;
				continue;
			}

			var planes = PlaneOffset.Distinct( vertexFaces[vi].Select( fi => faceNormals[fi] ) );

			if ( planes.Count == 0 )
			{
				offsetPositions[vi] = mesh.Positions[vi];
				continue;
			}

			PlaneOffset.TrySolve( planes, thickness, out var displacement );

			// A MITER LIMIT. Where the faces round a vertex nearly face each other - a crease in
			// draped cloth, a fold - the offset that keeps every plane at the thickness is huge,
			// and a shirt grew spikes a foot long out of its wrinkles. A cube's corner needs
			// sqrt(3) of the thickness, so four leaves every hard-surface corner exact.
			var limit = MathF.Abs( thickness ) * 4f;

			if ( displacement.Length > limit )
				displacement = displacement.Normal * limit;

			offsetPositions[vi] = mesh.Positions[vi] + displacement;
		}

		// Layout: [0 .. V) the near wall, [V .. 2V) the far wall.
		var result = new PolyMesh();

		foreach ( var p in mesh.Positions )
			result.AddVertex( p );

		foreach ( var p in offsetPositions )
			result.AddVertex( p );

		// Each far vertex carries the weights of the near vertex it came from, so a rigged garment
		// keeps its rig through the thickness pass.
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
			var f = mesh.Faces[fi];
			var n = f.Count;

			// Far wall: the same winding, on the offset vertices, so its normals still face outward.
			var far = new int[n];

			for ( var i = 0; i < n; i++ )
				far[i] = f.Indices[i] + offset;

			result.AddFace( far, (Vec2[])f.UVs.Clone(), f.Material );

			// Near wall: the same sheet turned inside out — reversed winding on the original
			// vertices — so its normals face the cavity.
			var near = new int[n];
			var nearUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				near[i] = f.Indices[n - 1 - i];
				nearUVs[i] = f.UVs[n - 1 - i];
			}

			result.AddFace( near, nearUVs, f.Material );
		}

		// Rim quads around the boundary, one per boundary edge, wound outward (the face is on the
		// left of its own traversal, so [a, b, b', a'] points away from the surface).
		var edgeFaces = mesh.BuildEdgeFaces();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var f = mesh.Faces[fi];
			var n = f.Count;

			for ( var i = 0; i < n; i++ )
			{
				var a = f.Indices[i];
				var b = f.Indices[(i + 1) % n];
				var key = new EdgeKey( a, b );

				if ( !edgeFaces.TryGetValue( key, out var faces ) || faces.Count != 1 )
					continue;

				result.AddFace(
					new[] { a, b, b + offset, a + offset },
					new[] { f.UVs[i], f.UVs[(i + 1) % n], f.UVs[(i + 1) % n], f.UVs[i] },
					f.Material );
			}
		}

		return result;
	}

	/// <summary>
	/// The outer wall of a <see cref="Solidify"/> result, as the open sheet it was made from: the
	/// same faces, UVs, slots and weights, on the offset vertices. Null when the mesh is not laid
	/// out the way Solidify lays it out.
	///
	/// A SOLIDIFIED GARMENT HAS NO OPENINGS. The rim closes every hem, cuff and collar, so anything
	/// that walks a garment's boundary - a frill at the hem, fur along the cuffs, the opening count
	/// in Check, live cloth that wants one sheet rather than two walls - found nothing on a garment
	/// at its default Thickness. The openings are still there in the layout: Solidify writes the
	/// near wall and then the far wall face by face, so the far wall alone is the sheet, offset.
	///
	/// READ OFF THE FACE ORDER, NOT THE VERTEX COUNT. Fur grows its shells onto the garment after
	/// the fact and appends vertices and faces, so "half the vertices" stops being the offset the
	/// moment a garment has fur. The first two faces give the offset (the far face is the near face
	/// reversed, shifted by it), and every pair after them has to agree.
	/// </summary>
	public static PolyMesh OuterSheet( PolyMesh mesh, out int offset )
	{
		offset = 0;

		if ( mesh is null || mesh.FaceCount < 2 )
			return null;

		bool Pair( Face far, Face near, int shift )
		{
			var n = far.Count;

			if ( near.Count != n || n < 3 )
				return false;

			for ( var i = 0; i < n; i++ )
				if ( far.Indices[i] != near.Indices[n - 1 - i] + shift )
					return false;

			return true;
		}

		var f0 = mesh.Faces[0];
		var f1 = mesh.Faces[1];
		var v = f0.Indices[0] - f1.Indices[f1.Count - 1];

		if ( v <= 0 || 2 * v > mesh.VertexCount || !Pair( f0, f1, v ) )
			return null;

		var pairs = 0;

		while ( 2 * pairs + 1 < mesh.FaceCount && Pair( mesh.Faces[2 * pairs], mesh.Faces[2 * pairs + 1], v ) )
			pairs++;

		// Every near-wall vertex has to be a corner of some pair, or this is a mesh that only
		// happens to start the same way.
		var used = new bool[v];

		for ( var k = 0; k < pairs; k++ )
			foreach ( var vi in mesh.Faces[2 * k + 1].Indices )
			{
				if ( vi < 0 || vi >= v )
					return null;

				used[vi] = true;
			}

		if ( used.Any( u => !u ) )
			return null;

		var sheet = new PolyMesh();

		for ( var i = 0; i < v; i++ )
			sheet.AddVertex( mesh.Positions[i + v] );

		for ( var k = 0; k < pairs; k++ )
		{
			var far = mesh.Faces[2 * k];
			sheet.AddFace( far.Indices.Select( i => i - v ).ToArray(), (Vec2[])far.UVs?.Clone(), far.Material );
		}

		if ( mesh.IsRigged && mesh.Skin.Count >= 2 * v )
		{
			var skin = new SkinWeights();

			for ( var i = 0; i < v; i++ )
				skin.Vertices.Add( (BoneWeight[])mesh.Skin[i + v].Clone() );

			sheet.Skin = skin;
		}

		offset = v;
		return sheet;
	}

	/// <summary>
	/// Give each fan of faces around a pinched boundary vertex its own copy of that vertex.
	///
	/// One fan is the ordinary case: two boundary edges, one loop. Two fans meeting at one vertex
	/// is two loops pinched together, and the rim pass cannot tell them apart. The first fan keeps
	/// the vertex; every later fan is retargeted onto a new one at the same position.
	/// </summary>
	static void SplitPinchedBoundary( PolyMesh mesh )
	{
		var count = mesh.VertexCount;
		var vertexFaces = mesh.BuildVertexFaces();
		var edgeFaces = mesh.BuildEdgeFaces();
		var rigged = mesh.IsRigged;

		bool Boundary( int a, int b ) =>
			edgeFaces.TryGetValue( new EdgeKey( a, b ), out var users ) && users.Count == 1;

		// The maps above are of the mesh as it came in, and a split retargets faces onto copies as
		// it goes. A neighbour split earlier shows up in a face as its copy, which the maps have
		// never heard of - and a pinch next to a pinch (both armpits of a T-shirt cut from a
		// triangulated Citizen) then split a fan that was whole, leaving an edge on four faces.
		// So every index is read back to the vertex it was a copy of before it is looked up.
		var original = new Dictionary<int, int>();
		int Original( int v ) => original.TryGetValue( v, out var o ) ? o : v;

		for ( var vi = 0; vi < count; vi++ )
		{
			var incident = vertexFaces[vi];

			if ( incident is null || incident.Count < 2 )
				continue;

			var parent = new Dictionary<int, int>( incident.Count );

			int Find( int x )
			{
				if ( !parent.TryGetValue( x, out var p ) )
					return parent[x] = x;

				if ( p != x )
					parent[x] = Find( p );

				return parent[x];
			}

			void Union( int a, int b )
			{
				a = Find( a );
				b = Find( b );

				if ( a != b )
					parent[a] = b;
			}

			foreach ( var fi in incident )
				Find( fi );

			foreach ( var fi in incident )
			{
				var idx = mesh.Faces[fi].Indices;
				var n = idx.Length;

				for ( var c = 0; c < n; c++ )
				{
					if ( idx[c] != vi )
						continue;

					var next = idx[c + 1 == n ? 0 : c + 1];
					var prev = idx[c == 0 ? n - 1 : c - 1];

					void Join( int other )
					{
						other = Original( other );
						var key = new EdgeKey( vi, other );

						if ( Boundary( vi, other ) || !edgeFaces.TryGetValue( key, out var users ) )
							return;

						foreach ( var neighbour in users )
						{
							if ( neighbour != fi )
								Union( fi, neighbour );
						}
					}

					Join( next );
					Join( prev );
				}
			}

			var first = true;

			foreach ( var fan in incident.GroupBy( Find ) )
			{
				if ( first )
				{
					first = false;
					continue;
				}

				var copy = mesh.AddVertex( mesh.Positions[vi] );
				original[copy] = vi;

				if ( rigged )
					mesh.Skin.Vertices.Add( (BoneWeight[])mesh.Skin.Vertices[vi].Clone() );

				if ( mesh.VertexColors is { } colors && colors.Length == copy )
				{
					Array.Resize( ref mesh.VertexColors, copy + 1 );
					mesh.VertexColors[copy] = colors[vi];
				}

				foreach ( var fi in fan )
				{
					var idx = mesh.Faces[fi].Indices;

					for ( var c = 0; c < idx.Length; c++ )
					{
						if ( idx[c] == vi )
							idx[c] = copy;
					}
				}
			}
		}
	}
}
