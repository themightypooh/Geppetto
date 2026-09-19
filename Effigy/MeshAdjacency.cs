using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Vertex-to-face adjacency in compressed sparse row form: one flat array of face indices, and an
/// offset per vertex saying where its run starts.
///
/// WHY THIS EXISTS ALONGSIDE <see cref="PolyMesh.BuildVertexFaces"/>, which answers the same
/// question. That one returns a <c>List&lt;int&gt;[]</c>, so a mesh with 240k vertices allocates
/// 240k List objects and 240k backing arrays to hold four ints each - about 90 MB of garbage for
/// 4 MB of answer, and the GC pause that goes with it lands in the middle of a viewport rebuild.
/// Two flat arrays hold the same data with two allocations.
///
/// The list version is kept because a dozen callers use it and most of them run once on a shape
/// small enough not to care. This one is for the paths that run per rebuild or per stroke.
///
/// NOT A HALF-EDGE STRUCTURE, and for the reason PolyMesh gives: this is derived on demand and
/// thrown away, so it cannot go stale. Build it, use it, drop it.
/// </summary>
public sealed class VertexFaces
{
	/// <summary>Where vertex v's run starts. Length is VertexCount + 1, so a run is
	/// Offsets[v]..Offsets[v+1] and the last entry needs no special case.</summary>
	public readonly int[] Offsets;

	/// <summary>Face indices, grouped by vertex.</summary>
	public readonly int[] Items;

	VertexFaces( int[] offsets, int[] items )
	{
		Offsets = offsets;
		Items = items;
	}

	public int VertexCount => Offsets.Length - 1;

	/// <summary>How many faces touch this vertex. Its valence, for a manifold interior vertex.</summary>
	public int CountAt( int vertex ) => Offsets[vertex + 1] - Offsets[vertex];

	/// <summary>The faces touching a vertex, as a view into <see cref="Items"/>. No allocation.</summary>
	public ReadOnlySpan<int> this[int vertex] =>
		new( Items, Offsets[vertex], Offsets[vertex + 1] - Offsets[vertex] );

	/// <summary>
	/// Counting sort in two passes: count each vertex's faces, prefix-sum that into offsets, then
	/// scatter. Linear, and allocates exactly the two arrays it returns.
	/// </summary>
	public static VertexFaces Build( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var vertexCount = mesh.Positions.Count;
		var offsets = new int[vertexCount + 1];
		var faces = mesh.Faces;

		// Pass one: how many faces per vertex, counted into offsets[v + 1] so the prefix sum
		// below turns the counts into starts in place.
		for ( var fi = 0; fi < faces.Count; fi++ )
		{
			var indices = faces[fi].Indices;

			for ( var i = 0; i < indices.Length; i++ )
			{
				// A malformed face can list the same vertex twice, and counting it twice would
				// inflate the valence - which is what skews the Catmull-Clark vertex rule and the
				// area-weighted normal. BuildVertexFaces spends a LINQ Distinct() per face on
				// this; a scan of the corners before this one is the same answer without the
				// enumerator, and faces have three or four corners.
				if ( Repeats( indices, i ) )
					continue;

				offsets[indices[i] + 1]++;
			}
		}

		for ( var v = 0; v < vertexCount; v++ )
			offsets[v + 1] += offsets[v];

		// Pass two: scatter. cursor walks a copy of the starts so offsets survives intact.
		var items = new int[offsets[vertexCount]];
		var cursor = new int[vertexCount];
		Array.Copy( offsets, cursor, vertexCount );

		for ( var fi = 0; fi < faces.Count; fi++ )
		{
			var indices = faces[fi].Indices;

			for ( var i = 0; i < indices.Length; i++ )
			{
				if ( Repeats( indices, i ) )
					continue;

				items[cursor[indices[i]]++] = fi;
			}
		}

		return new VertexFaces( offsets, items );
	}

	/// <summary>Whether this corner's vertex already appeared earlier in the same face.</summary>
	static bool Repeats( int[] indices, int corner )
	{
		var v = indices[corner];

		for ( var j = 0; j < corner; j++ )
		{
			if ( indices[j] == v )
				return true;
		}

		return false;
	}
}

/// <summary>One directed half-edge. <see cref="Origin"/> is the vertex it leaves, <see cref="Next"/>
/// and <see cref="Prev"/> walk the face loop, <see cref="Twin"/> is the opposite direction across the
/// same edge (-1 on a boundary), and <see cref="Face"/> is the face it borders.</summary>
public struct HEdge
{
	public int Origin;
	public int Next;
	public int Prev;
	public int Twin;
	public int Face;
	public Vec2 UV;
}

/// <summary>One face loop of an <see cref="EditableMesh"/>: a starting half-edge and a material
/// slot. Corner UVs ride the half-edges themselves, mirroring <see cref="Face"/>'s per-corner UVs.</summary>
public sealed class EFace
{
	public int HalfEdge = -1;
	public int Material;
}

/// <summary>
/// A mutable mesh with explicit half-edge adjacency, for direct polygon editing.
///
/// <see cref="PolyMesh"/> is the durable representation — the one the features, the exporters and the
/// document all talk in, and the one that stays an indexed n-gon list because that is cheap to
/// derive adjacency from and hard to corrupt. This is the interactive representation: a half-edge
/// per directed side of every edge, so the questions an edit session asks a hundred times a frame —
/// which faces touch this edge, what is this vertex's fan, which edges bound this face — are O(1)
/// walks instead of dictionary rebuilds.
///
/// This is the threshold <see cref="PolyMesh"/>'s header says to cross "when interactive per-element
/// editing arrives". The handles are stable indices into <see cref="Positions"/>,
/// <see cref="HalfEdges"/> and <see cref="Faces"/>; they stay valid while elements are only added,
/// which is the contract the first wave of edit operations keeps. Removal, and the tombstoning or
/// compaction that goes with it, is a later step.
/// </summary>
public sealed class EditableMesh
{
	public List<Vec3> Positions = new();
	public List<HEdge> HalfEdges = new();
	public List<EFace> Faces = new();

	/// <summary>One outgoing half-edge per vertex, or -1 for an isolated vertex. This is the entry
	/// point for rotating around a vertex — see <see cref="OutgoingHalfEdges"/>.</summary>
	public int[] VertexHalfEdge = Array.Empty<int>();

	// The attributes PolyMesh carries that a round trip must not drop. Positions are 1:1 with
	// PolyMesh's on conversion, so these parallel arrays transfer directly.
	public SkinWeights Skin;
	public Vec4[] VertexColors;
	public PaintCanvas Paint;

	public int VertexCount => Positions.Count;
	public int FaceCount => Faces.Count;

	public static EditableMesh FromPolyMesh( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var e = new EditableMesh
		{
			Positions = new List<Vec3>( mesh.Positions ),
			Skin = mesh.Skin?.Clone(),
		};

		if ( mesh.VertexColors is not null )
			e.VertexColors = (Vec4[])mesh.VertexColors.Clone();

		if ( mesh.Paint is not null )
			e.Paint = mesh.Paint.Clone();

		var faceVertices = new List<List<int>>( mesh.FaceCount );
		var faceUVs = new List<List<Vec2>>( mesh.FaceCount );
		var materials = new List<int>( mesh.FaceCount );

		foreach ( var face in mesh.Faces )
		{
			faceVertices.Add( new List<int>( face.Indices ) );
			faceUVs.Add( new List<Vec2>( face.UVs ) );
			materials.Add( face.Material );
		}

		e.Rebuild( faceVertices, faceUVs, materials );
		return e;
	}

	/// <summary>
	/// Rebuild the half-edge structure from face loops, keeping <see cref="Positions"/> and the
	/// parallel attribute arrays untouched. Vertex handles are therefore stable across the rebuild;
	/// face and half-edge handles are re-derived and must not be held across a call.
	/// </summary>
	void Rebuild( List<List<int>> faceVertices, List<List<Vec2>> faceUVs, List<int> materials )
	{
		HalfEdges.Clear();
		Faces.Clear();

		VertexHalfEdge = new int[Positions.Count];

		for ( var v = 0; v < VertexHalfEdge.Length; v++ )
			VertexHalfEdge[v] = -1;

		// An undirected edge's first half-edge, waiting for its opposite direction to arrive so the
		// two can be linked as twins. Keyed by the sorted pair, so u->w and w->u share a slot.
		var twins = new Dictionary<EdgeKey, int>();

		for ( var fi = 0; fi < faceVertices.Count; fi++ )
		{
			var indices = faceVertices[fi];
			var uvs = faceUVs[fi];
			var n = indices.Count;
			var start = HalfEdges.Count;

			Faces.Add( new EFace { HalfEdge = start, Material = materials[fi] } );

			for ( var i = 0; i < n; i++ )
			{
				var from = indices[i];
				var to = indices[(i + 1) % n];
				var he = new HEdge
				{
					Origin = from,
					Next = start + (i + 1) % n,
					Prev = start + (i + n - 1) % n,
					Twin = -1,
					Face = fi,
					UV = uvs[i],
				};

				var key = new EdgeKey( from, to );

				if ( twins.TryGetValue( key, out var other ) )
				{
					he.Twin = other;
					twins.Remove( key );

					var twin = HalfEdges[other];
					twin.Twin = start + i;
					HalfEdges[other] = twin;
				}
				else
				{
					twins[key] = start + i;
				}

				HalfEdges.Add( he );
				VertexHalfEdge[from] = start + i;
			}
		}
	}

	/// <summary>
	/// Remove faces, Blender's Delete Faces. The faces' edges become boundary edges in the mesh
	/// around them, so deleting a face off a closed solid opens it and deleting a whole region
	/// leaves the surrounding mesh with a rim. Vertices that end up referenced by nothing stay in
	/// <see cref="Positions"/> as isolated vertices.
	/// </summary>
	public void DeleteFaces( IReadOnlyCollection<int> faces )
	{
		if ( faces is null )
			throw new ArgumentNullException( nameof( faces ) );

		var delete = new bool[Faces.Count];

		foreach ( var f in faces )
		{
			if ( f < 0 || f >= Faces.Count )
				throw new ArgumentOutOfRangeException( nameof( faces ), $"face {f} out of range" );
			delete[f] = true;
		}

		var faceVertices = new List<List<int>>();
		var faceUVs = new List<List<Vec2>>();
		var materials = new List<int>();
		var loop = new List<int>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			if ( delete[fi] )
				continue;

			FaceHalfEdges( fi, loop );

			var verts = new List<int>( loop.Count );
			var uvs = new List<Vec2>( loop.Count );

			foreach ( var he in loop )
			{
				verts.Add( HalfEdges[he].Origin );
				uvs.Add( HalfEdges[he].UV );
			}

			faceVertices.Add( verts );
			faceUVs.Add( uvs );
			materials.Add( Faces[fi].Material );
		}

		Rebuild( faceVertices, faceUVs, materials );
	}

	/// <summary>Rebuild a <see cref="PolyMesh"/>. Positions, face winding, per-corner UVs, material
	/// slots, skin weights, vertex colours and paint are carried over; vertex order is unchanged, so
	/// the parallel attribute arrays stay parallel.</summary>
	public PolyMesh ToPolyMesh()
	{
		var mesh = new PolyMesh
		{
			Positions = new List<Vec3>( Positions ),
			Skin = Skin?.Clone(),
		};

		if ( VertexColors is not null )
			mesh.VertexColors = (Vec4[])VertexColors.Clone();

		if ( Paint is not null )
			mesh.Paint = Paint.Clone();

		var loop = new List<int>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			FaceHalfEdges( fi, loop );

			var indices = new int[loop.Count];
			var uvs = new Vec2[loop.Count];

			for ( var i = 0; i < loop.Count; i++ )
			{
				var he = HalfEdges[loop[i]];
				indices[i] = he.Origin;
				uvs[i] = he.UV;
			}

			mesh.AddFace( indices, uvs, Faces[fi].Material );
		}

		return mesh;
	}

	/// <summary>The half-edges of a face in loop order. Clears and fills <paramref name="results"/>.
	/// A safety counter stops a corrupt loop from spinning forever.</summary>
	public void FaceHalfEdges( int face, List<int> results )
	{
		results.Clear();

		if ( face < 0 || face >= Faces.Count )
			return;

		var start = Faces[face].HalfEdge;

		if ( start < 0 || start >= HalfEdges.Count )
			return;

		var he = start;
		var guard = 0;

		do
		{
			results.Add( he );
			he = HalfEdges[he].Next;

			if ( ++guard > HalfEdges.Count )
				break;
		} while ( he != start );
	}

	/// <summary>The vertices of a face, in loop order.</summary>
	public List<int> FaceVertices( int face )
	{
		var results = new List<int>();
		FaceHalfEdges( face, results );

		for ( var i = 0; i < results.Count; i++ )
			results[i] = HalfEdges[results[i]].Origin;

		return results;
	}

	/// <summary>
	/// The half-edges leaving a vertex, in rotation order. Starting from the vertex's recorded
	/// half-edge, each step is <c>twin(prev)</c> — the half-edge leaving this vertex in the next face
	/// around — so a closed manifold fan returns to its start and a boundary fan stops at -1.
	/// </summary>
	public void OutgoingHalfEdges( int vertex, List<int> results )
	{
		results.Clear();

		if ( vertex < 0 || vertex >= VertexHalfEdge.Length )
			return;

		var start = VertexHalfEdge[vertex];

		if ( start < 0 )
			return;

		var he = start;
		var guard = 0;

		do
		{
			results.Add( he );
			he = TwinAround( he );

			if ( ++guard > HalfEdges.Count )
				break;
		} while ( he != start && he >= 0 );
	}

	int TwinAround( int he )
	{
		var prev = HalfEdges[he].Prev;
		return HalfEdges[prev].Twin;
	}

	/// <summary>
	/// Structural checks, reported as the same <see cref="MeshValidation"/> a PolyMesh gets so a
	/// caller can ask the same questions of either representation. Boundary and non-manifold counts
	/// come from undirected edge valences; twin symmetry and next/prev consistency are checked
	/// directly, which is what catches the corruption an edit is most likely to introduce.
	/// </summary>
	public MeshValidation Validate()
	{
		var result = new MeshValidation();

		for ( var i = 0; i < HalfEdges.Count; i++ )
		{
			var he = HalfEdges[i];

			if ( he.Origin < 0 || he.Origin >= Positions.Count )
				result.Errors.Add( $"half-edge {i} references vertex {he.Origin}, out of range" );

			if ( he.Next < 0 || he.Next >= HalfEdges.Count )
				result.Errors.Add( $"half-edge {i} has out-of-range next {he.Next}" );

			if ( he.Prev < 0 || he.Prev >= HalfEdges.Count )
				result.Errors.Add( $"half-edge {i} has out-of-range prev {he.Prev}" );

			if ( he.Twin < -1 || he.Twin >= HalfEdges.Count )
				result.Errors.Add( $"half-edge {i} has out-of-range twin {he.Twin}" );

			if ( he.Face < 0 || he.Face >= Faces.Count )
				result.Errors.Add( $"half-edge {i} references face {he.Face}, out of range" );

			if ( he.Twin >= 0 && HalfEdges[he.Twin].Twin != i )
				result.Errors.Add( $"half-edge {i} and its twin {he.Twin} do not point back at each other" );

			if ( he.Next >= 0 && he.Next < HalfEdges.Count && HalfEdges[he.Next].Prev != i )
				result.Errors.Add( $"half-edge {i} next does not point prev back" );

			if ( he.Prev >= 0 && he.Prev < HalfEdges.Count && HalfEdges[he.Prev].Next != i )
				result.Errors.Add( $"half-edge {i} prev does not point next back" );
		}

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			var face = Faces[fi];

			if ( face.HalfEdge < 0 || face.HalfEdge >= HalfEdges.Count )
			{
				result.Errors.Add( $"face {fi} has out-of-range half-edge {face.HalfEdge}" );
				continue;
			}

			if ( HalfEdges[face.HalfEdge].Face != fi )
				result.Errors.Add( $"face {fi} half-edge reports a different face" );

			var visited = 0;
			var he = face.HalfEdge;

			do
			{
				if ( HalfEdges[he].Face != fi )
					result.Errors.Add( $"face {fi} loop contains half-edge {he} of face {HalfEdges[he].Face}" );

				visited++;
				he = HalfEdges[he].Next;

				if ( visited > HalfEdges.Count )
				{
					result.Errors.Add( $"face {fi} loop does not close" );
					break;
				}
			} while ( he != face.HalfEdge );

			if ( visited < 3 )
				result.Errors.Add( $"face {fi} has only {visited} corners" );
		}

		for ( var v = 0; v < VertexHalfEdge.Length; v++ )
		{
			var he = VertexHalfEdge[v];

			if ( he < -1 || he >= HalfEdges.Count )
				result.Errors.Add( $"vertex {v} has out-of-range half-edge {he}" );
			else if ( he >= 0 && HalfEdges[he].Origin != v )
				result.Errors.Add( $"vertex {v} half-edge {he} originates elsewhere" );
		}

		var counts = new Dictionary<EdgeKey, int>();

		for ( var i = 0; i < HalfEdges.Count; i++ )
		{
			var he = HalfEdges[i];
			var to = HalfEdges[he.Next].Origin;
			var key = new EdgeKey( he.Origin, to );
			counts.TryGetValue( key, out var c );
			counts[key] = c + 1;
		}

		foreach ( var (key, count) in counts )
		{
			if ( count == 1 )
				result.BoundaryEdges++;
			else if ( count > 2 )
			{
				result.NonManifoldEdges++;
				result.Errors.Add( $"edge {key} is shared by {count} half-edges" );
			}
		}

		return result;
	}

	/// <summary>Append a vertex that carries the source vertex's skin and colour. Positions, the
	/// skin list and the colour array stay parallel; paint rides the mesh and is untouched.</summary>
	int DuplicateVertex( int source, Vec3 position )
	{
		var index = Positions.Count;
		Positions.Add( position );

		Array.Resize( ref VertexHalfEdge, Positions.Count );
		VertexHalfEdge[index] = -1;

		if ( Skin is not null )
			Skin.Vertices.Add( (BoneWeight[])Skin.Vertices[source].Clone() );

		if ( VertexColors is not null )
		{
			Array.Resize( ref VertexColors, Positions.Count );
			VertexColors[index] = VertexColors[source];
		}

		return index;
	}

	/// <summary>
	/// Extrude a region of faces along one vector, Blender's Extrude Region.
	///
	/// Boundary vertices of the region are duplicated and offset; interior vertices move in place.
	/// The region stays connected to the rest of the mesh by a band of side faces around each
	/// boundary loop, so a face extruded off a closed box stays closed and a face extruded off an
	/// open sheet gains side walls and stays open underneath. Side faces inherit the region's
	/// material and re-use the boundary corners' UVs.
	///
	/// The first mutation on <see cref="EditableMesh"/>, and the one the rest of the direct-edit
	/// toolset is built from — solidify is this applied to every face, and it is the reason the
	/// half-edge representation exists rather than the rebuild-on-demand one.
	/// </summary>
	public void ExtrudeRegion( IReadOnlyCollection<int> regionFaces, Vec3 offset )
	{
		BandRegion( regionFaces, v => Positions[v] + offset, true );
	}

	/// <summary>
	/// Extrude a region of faces, moving each vertex to the position returned by <paramref name="targetForVertex"/>.
	/// </summary>
	public void ExtrudeRegion( IReadOnlyCollection<int> regionFaces, Func<int, Vec3> targetForVertex )
	{
		BandRegion( regionFaces, targetForVertex, true );
	}

	/// <summary>
	/// Inset a region of faces, Blender's Inset Faces.
	///
	/// Same band construction as <see cref="ExtrudeRegion"/>, but the duplicated boundary vertices
	/// move inward within the surface instead of being lifted off it, so the region shrinks and a
	/// ring of faces fills the border while the whole thing stays on the same surface. The inset is
	/// the even-thickness solve: each boundary edge ends up exactly <paramref name="distance"/>
	/// inward, measured perpendicular to the edge, using the same <see cref="PlaneOffset"/> corner
	/// solve as shell and solidify. Interior vertices stay put, and the outer boundary stays where
	/// it was, so insetting an open sheet keeps it open.
	/// </summary>
	public void InsetFaces( IReadOnlyCollection<int> regionFaces, float distance )
	{
		if ( regionFaces is null )
			throw new ArgumentNullException( nameof( regionFaces ) );

		if ( !float.IsFinite( distance ) || distance <= 0f )
			throw new ArgumentOutOfRangeException( nameof( distance ) );

		var inRegion = BuildRegionMask( regionFaces );
		var loops = BoundaryLoops( inRegion, out var boundaryOrigin );
		var target = new Dictionary<int, Vec3>();

		foreach ( var loop in loops )
		{
			var k = loop.Count;

			for ( var i = 0; i < k; i++ )
			{
				var he = loop[i];
				var v = boundaryOrigin[he];
				var prev = boundaryOrigin[loop[(i + k - 1) % k]];
				var next = boundaryOrigin[loop[(i + 1) % k]];

				// Each of the two boundary edges meeting here wants to move inward by `distance`;
				// an edge's inward direction is its tangent crossed with the face normal.
				var normal = FaceNormal( HalfEdges[he].Face );
				var inward0 = Vec3.Cross( normal, Positions[v] - Positions[prev] );
				var inward1 = Vec3.Cross( normal, Positions[next] - Positions[v] );

				var planes = PlaneOffset.Distinct( new[] { inward0.Normal, inward1.Normal } );
				PlaneOffset.TrySolve( planes, distance, out var displacement );
				target[v] = Positions[v] + displacement;
			}
		}

		BandRegion( regionFaces, v => target[v], false );
	}

	/// <summary>
	/// The ring of edges a loop cut through <paramref name="seedHalfEdge"/> would cross: from each
	/// half-edge, the opposite edge in its face, then across that edge, until it comes back round.
	///
	/// Split out of <see cref="LoopCut(int, float)"/> so a preview can walk the same ring the cut
	/// will, and refuse for exactly the same reasons — a hover that previewed a loop the cut then
	/// refused would be worse than no preview.
	/// </summary>
	/// <param name="crossedIncoming">For each face the ring passes through, the half-edge that led
	/// into it.</param>
	public List<int> RingHalfEdges( int seedHalfEdge, out Dictionary<int, int> crossedIncoming )
	{
		if ( seedHalfEdge < 0 || seedHalfEdge >= HalfEdges.Count )
			throw new ArgumentOutOfRangeException( nameof( seedHalfEdge ) );

		var ringEdges = new List<int>();
		var visited = new HashSet<EdgeKey>();
		var cornerScratch = new List<int>();

		crossedIncoming = new Dictionary<int, int>();

		var he = seedHalfEdge;
		ringEdges.Add( he );
		visited.Add( EdgeOf( he ) );

		while ( true )
		{
			var face = HalfEdges[he].Face;
			FaceHalfEdges( face, cornerScratch );

			if ( cornerScratch.Count != 4 )
				throw new InvalidOperationException( "loop cut needs a quad mesh; this face is not a quad" );

			crossedIncoming[face] = he;

			var opposite = HalfEdges[HalfEdges[he].Next].Next;
			var twin = HalfEdges[opposite].Twin;

			if ( twin < 0 )
				throw new InvalidOperationException( "the loop runs off the mesh's boundary; open rings are not supported yet" );

			var key = EdgeOf( twin );

			if ( visited.Contains( key ) )
				break;

			visited.Add( key );
			ringEdges.Add( twin );
			he = twin;
		}

		return ringEdges;
	}

	/// <summary>
	/// Where a loop cut through <paramref name="seedHalfEdge"/> would land, in ring order — the
	/// polyline a preview draws. Closed: the last point joins back to the first. Nothing is changed.
	/// </summary>
	public List<Vec3> LoopCutPoints( int seedHalfEdge, float fraction )
	{
		var ring = RingHalfEdges( seedHalfEdge, out _ );
		var points = new List<Vec3>( ring.Count );

		foreach ( var e in ring )
		{
			var a = HalfEdges[e].Origin;
			var b = HalfEdges[HalfEdges[e].Next].Origin;
			points.Add( Positions[a] + (Positions[b] - Positions[a]) * fraction );
		}

		return points;
	}

	/// <summary>
	/// Insert an edge loop, Blender's Loop Cut.
	///
	/// From a seed half-edge, walks the ring of opposite edges around a quad mesh, splits each ring
	/// edge at <paramref name="fraction"/> and cuts every crossed quad into two along the line
	/// joining the two split points. The canonical box-modelling gesture: one cut around a cube turns
	/// it into two stacked cubes without changing its shape.
	///
	/// Restricted to closed rings of quads for now — a face that is not a quad, or a ring that runs
	/// off the mesh's boundary, is refused rather than approximated. New vertices interpolate the
	/// edge endpoints' positions and UVs and inherit the first endpoint's skin and colour.
	/// </summary>
	public void LoopCut( int seedHalfEdge, float fraction )
	{
		if ( seedHalfEdge < 0 || seedHalfEdge >= HalfEdges.Count )
			throw new ArgumentOutOfRangeException( nameof( seedHalfEdge ) );

		if ( !float.IsFinite( fraction ) || fraction <= 0f || fraction >= 1f )
			throw new ArgumentOutOfRangeException( nameof( fraction ), "the cut must lie strictly inside the edges" );

		var ringEdges = RingHalfEdges( seedHalfEdge, out var crossedIncoming );

		// Split each unique ring edge once.
		var splitVertex = new Dictionary<EdgeKey, int>();

		foreach ( var e in ringEdges )
		{
			var a = HalfEdges[e].Origin;
			var b = HalfEdges[HalfEdges[e].Next].Origin;
			var key = new EdgeKey( a, b );

			if ( splitVertex.ContainsKey( key ) )
				continue;

			splitVertex[key] = DuplicateVertex( a, Positions[a] + (Positions[b] - Positions[a]) * fraction );
		}

		// Assemble the new face list: crossed quads split in two, everything else unchanged.
		var faceVertices = new List<List<int>>();
		var faceUVs = new List<List<Vec2>>();
		var materials = new List<int>();
		var loop = new List<int>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			if ( !crossedIncoming.TryGetValue( fi, out var incoming ) )
			{
				FaceHalfEdges( fi, loop );

				var verts = new List<int>( loop.Count );
				var uvs = new List<Vec2>( loop.Count );

				foreach ( var hei in loop )
				{
					verts.Add( HalfEdges[hei].Origin );
					uvs.Add( HalfEdges[hei].UV );
				}

				faceVertices.Add( verts );
				faceUVs.Add( uvs );
				materials.Add( Faces[fi].Material );
				continue;
			}

			// Corners A B C D in the quad's winding; ring edges are (A,B) and (C,D).
			var heA = incoming;
			var heB = HalfEdges[heA].Next;
			var heC = HalfEdges[heB].Next;
			var heD = HalfEdges[heC].Next;

			var a = HalfEdges[heA].Origin;
			var b = HalfEdges[heB].Origin;
			var c = HalfEdges[heC].Origin;
			var d = HalfEdges[heD].Origin;

			var n1 = splitVertex[new EdgeKey( a, b )];
			var n2 = splitVertex[new EdgeKey( c, d )];

			var uvN1 = LerpUV( HalfEdges[heA].UV, HalfEdges[heB].UV, fraction );
			var uvN2 = LerpUV( HalfEdges[heC].UV, HalfEdges[heD].UV, fraction );

			faceVertices.Add( new List<int> { n1, b, c, n2 } );
			faceUVs.Add( new List<Vec2> { uvN1, HalfEdges[heB].UV, HalfEdges[heC].UV, uvN2 } );
			materials.Add( Faces[fi].Material );

			faceVertices.Add( new List<int> { n2, d, a, n1 } );
			faceUVs.Add( new List<Vec2> { uvN2, HalfEdges[heD].UV, HalfEdges[heA].UV, uvN1 } );
			materials.Add( Faces[fi].Material );
		}

		Rebuild( faceVertices, faceUVs, materials );
	}

	EdgeKey EdgeOf( int halfEdge )
	{
		var he = HalfEdges[halfEdge];
		return new EdgeKey( he.Origin, HalfEdges[he.Next].Origin );
	}

	static Vec2 LerpUV( Vec2 a, Vec2 b, float t ) =>
		new( a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t );

	/// <summary>
	/// Bridge two boundary loops with a band of quads, Blender's Bridge Edge Loops.
	///
	/// <paramref name="halfEdgeA"/> and <paramref name="halfEdgeB"/> are boundary half-edges (their
	/// twin is -1), one on each rim; the two rims are paired in cyclic order from those seeds and a
	/// quad connects each edge pair. The band winds with the surrounding surfaces, so bridging the two
	/// rims of an open tube closes it the right way out and bridging two flat surfaces makes a slab.
	/// The seeds set the pairing's rotational offset — pick corresponding corners when the
	/// correspondence matters, as Blender snaps the nearest vertices to choose it.
	///
	/// The two loops must have the same number of edges; unequal counts are refused rather than
	/// interpolated, and a loop that branches, runs open or is the same loop twice is refused. Band
	/// faces take the material of the face on loop A. The existing faces stay exactly as they were.
	/// </summary>
	public void BridgeLoops( int halfEdgeA, int halfEdgeB )
	{
		if ( !IsBoundaryHalfEdge( halfEdgeA ) )
			throw new ArgumentOutOfRangeException( nameof( halfEdgeA ), "the seed must be a boundary half-edge" );

		if ( !IsBoundaryHalfEdge( halfEdgeB ) )
			throw new ArgumentOutOfRangeException( nameof( halfEdgeB ), "the seed must be a boundary half-edge" );

		var loopA = WalkBoundary( halfEdgeA );
		var loopB = WalkBoundary( halfEdgeB );

		if ( loopA.Contains( halfEdgeB ) )
			throw new ArgumentException( "the two seeds are on the same boundary loop" );

		if ( loopA.Count != loopB.Count )
			throw new InvalidOperationException(
				$"bridge needs matching edge counts: the two loops have {loopA.Count} and {loopB.Count} edges" );

		var faceVertices = new List<List<int>>();
		var faceUVs = new List<List<Vec2>>();
		var materials = new List<int>();
		var loop = new List<int>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			FaceHalfEdges( fi, loop );

			var verts = new List<int>( loop.Count );
			var uvs = new List<Vec2>( loop.Count );

			foreach ( var hei in loop )
			{
				verts.Add( HalfEdges[hei].Origin );
				uvs.Add( HalfEdges[hei].UV );
			}

			faceVertices.Add( verts );
			faceUVs.Add( uvs );
			materials.Add( Faces[fi].Material );
		}

		var n = loopA.Count;

		for ( var i = 0; i < n; i++ )
		{
			var hA = loopA[i];
			var hB = loopB[n - 1 - i]; // rim B runs the other way round

			var aO = HalfEdges[hA].Origin;
			var aD = HalfEdges[HalfEdges[hA].Next].Origin;
			var bO = HalfEdges[hB].Origin;
			var bD = HalfEdges[HalfEdges[hB].Next].Origin;

			faceVertices.Add( new List<int> { aO, bD, bO, aD } );
			faceUVs.Add( new List<Vec2>
			{
				HalfEdges[hA].UV,
				HalfEdges[HalfEdges[hB].Next].UV,
				HalfEdges[hB].UV,
				HalfEdges[HalfEdges[hA].Next].UV,
			} );
			materials.Add( Faces[HalfEdges[hA].Face].Material );
		}

		Rebuild( faceVertices, faceUVs, materials );
	}

	bool IsBoundaryHalfEdge( int halfEdge ) =>
		halfEdge >= 0 && halfEdge < HalfEdges.Count && HalfEdges[halfEdge].Twin < 0;

	/// <summary>Walk a closed boundary loop from a boundary half-edge, following the rim in the
	/// half-edge direction. Returns the loop's half-edges in order, refusing a loop that branches or
	/// runs open rather than guessing.</summary>
	List<int> WalkBoundary( int start )
	{
		var result = new List<int>();
		var he = start;

		while ( true )
		{
			result.Add( he );

			var dest = HalfEdges[HalfEdges[he].Next].Origin;
			var next = NextBoundary( he, dest );

			if ( next == start )
				break;

			if ( next < 0 || result.Contains( next ) )
				throw new InvalidOperationException( "the boundary is not a single closed loop at the seed" );

			he = next;
		}

		return result;
	}

	int NextBoundary( int he, int vertex )
	{
		// When the whole face sits on the rim the continuation is simply the next half-edge in it.
		var inFace = HalfEdges[he].Next;

		if ( HalfEdges[inFace].Twin < 0 )
			return inFace;

		// Otherwise the rim turns the corner into the next face: the boundary half-edge leaving the
		// vertex that is not this face's edge.
		var outgoing = new List<int>();
		OutgoingHalfEdges( vertex, outgoing );

		foreach ( var h in outgoing )
		{
			if ( h != inFace && HalfEdges[h].Twin < 0 )
				return h;
		}

		return -1;
	}

	/// <summary>
	/// Dissolve edges, Blender's Dissolve Edges: remove each chosen edge and merge the faces on either
	/// side of it into one n-gon. Unlike <see cref="CoplanarMerge"/> this does not require the faces to
	/// be coplanar — it is the explicit "delete this edge and join the faces across it" gesture.
	///
	/// A chain of dissolved edges merges a whole fan of faces at once, because the faces become
	/// connected through the removals and the group is merged as a unit. The merged boundary has to be
	/// a single loop: a group around a hole (an annulus of faces) is refused, the same as Blender
	/// refuses it, rather than being returned as a face that cannot be triangulated. Every face in a
	/// merged group must sit on the same material slot, and a chosen edge on the boundary (with no
	/// second face to merge with) is refused.
	/// </summary>
	public void DissolveEdges( IReadOnlyCollection<int> halfEdges )
	{
		if ( halfEdges is null )
			throw new ArgumentNullException( nameof( halfEdges ) );

		if ( halfEdges.Count == 0 )
			return;

		var removed = new HashSet<EdgeKey>();

		foreach ( var he in halfEdges )
		{
			if ( he < 0 || he >= HalfEdges.Count )
				throw new ArgumentOutOfRangeException( nameof( halfEdges ), "half-edge out of range" );
			removed.Add( EdgeOf( he ) );
		}

		// Undirected edge -> its half-edges, the same lookup BuildEdgeFaces uses.
		var byEdge = new Dictionary<EdgeKey, List<int>>();

		for ( var hei = 0; hei < HalfEdges.Count; hei++ )
		{
			var key = EdgeOf( hei );

			if ( !byEdge.TryGetValue( key, out var list ) )
			{
				list = new List<int>();
				byEdge[key] = list;
			}

			list.Add( hei );
		}

		// Union-find over faces joined by a removed edge. A group is every face reachable through the
		// removals; each group collapses to one face.
		var parent = new int[Faces.Count];

		for ( var i = 0; i < parent.Length; i++ )
			parent[i] = i;

		int Find( int x )
		{
			while ( parent[x] != x )
			{
				parent[x] = parent[parent[x]];
				x = parent[x];
			}

			return x;
		}

		void Union( int a, int b )
		{
			a = Find( a );
			b = Find( b );

			if ( a != b )
				parent[a] = b;
		}

		foreach ( var key in removed )
		{
			if ( !byEdge.TryGetValue( key, out var list ) || list.Count != 2 )
				throw new InvalidOperationException( "dissolve needs an interior edge with a face on each side" );

			Union( HalfEdges[list[0]].Face, HalfEdges[list[1]].Face );
		}

		var roots = new int[Faces.Count];
		var groups = new Dictionary<int, List<int>>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			roots[fi] = Find( fi );

			if ( !groups.TryGetValue( roots[fi], out var list ) )
			{
				list = new List<int>();
				groups[roots[fi]] = list;
			}

			list.Add( fi );
		}

		bool Internal( int h, int root ) =>
			HalfEdges[h].Twin >= 0 && roots[HalfEdges[HalfEdges[h].Twin].Face] == root;

		var faceVertices = new List<List<int>>();
		var faceUVs = new List<List<Vec2>>();
		var materials = new List<int>();
		var emitted = new HashSet<int>();
		var loop = new List<int>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			var root = roots[fi];

			if ( !emitted.Add( root ) )
				continue;

			var group = groups[root];

			if ( group.Count == 1 )
			{
				FaceHalfEdges( fi, loop );

				var verts = new List<int>( loop.Count );
				var faceUv = new List<Vec2>( loop.Count );

				foreach ( var h in loop )
				{
					verts.Add( HalfEdges[h].Origin );
					faceUv.Add( HalfEdges[h].UV );
				}

				faceVertices.Add( verts );
				faceUVs.Add( faceUv );
				materials.Add( Faces[fi].Material );
				continue;
			}

			var material = Faces[group[0]].Material;

			foreach ( var fj in group )
			{
				if ( Faces[fj].Material != material )
					throw new InvalidOperationException( "dissolve would join faces on different material slots" );
			}

			// The merged boundary is every half-edge of the group whose opposite is outside it. Edges
			// with both halves inside the group are the ones being dissolved away.
			var boundary = new List<int>();
			var boundarySet = new HashSet<int>();

			foreach ( var fj in group )
			{
				FaceHalfEdges( fj, loop );

				foreach ( var h in loop )
				{
					if ( Internal( h, root ) )
						continue;

					boundary.Add( h );
					boundarySet.Add( h );
				}
			}

			if ( boundary.Count < 3 )
				throw new InvalidOperationException( "dissolve would leave too few corners" );

			var start = boundary[0];
			var cur = start;
			var corners = new List<int>();
			var uvs = new List<Vec2>();
			var guard = 0;

			do
			{
				corners.Add( HalfEdges[cur].Origin );
				uvs.Add( HalfEdges[cur].UV );

				var y = HalfEdges[cur].Next;

				while ( Internal( y, root ) )
					y = HalfEdges[HalfEdges[y].Twin].Next;

				cur = y;

				if ( ++guard > HalfEdges.Count )
					throw new InvalidOperationException( "dissolve could not close the merged face" );
			} while ( cur != start );

			if ( corners.Count != boundary.Count )
				throw new InvalidOperationException( "dissolve would make a face with a hole" );

			faceVertices.Add( corners );
			faceUVs.Add( uvs );
			materials.Add( material );
		}

		Rebuild( faceVertices, faceUVs, materials );
	}

	/// <summary>
	/// Dissolve a vertex, Blender's Dissolve Vertices for a single vertex: remove it and merge the
	/// whole fan of faces around it into one n-gon. The vertex must be interior — its fan a closed
	/// cycle — because a boundary vertex has no face on the open side to merge with, and it is
	/// refused. The fan's faces must share a material slot.
	///
	/// The vertex is left in <see cref="Positions"/> as an orphan rather than renumbering the mesh,
	/// matching how <see cref="DeleteFaces"/> treats vertices it strands.
	/// </summary>
	public void DissolveVertex( int vertex )
	{
		if ( vertex < 0 || vertex >= Positions.Count )
			throw new ArgumentOutOfRangeException( nameof( vertex ) );

		var outgoing = new List<int>();
		OutgoingHalfEdges( vertex, outgoing );

		if ( outgoing.Count < 3 )
			throw new InvalidOperationException( "dissolve needs at least three faces around the vertex" );

		var fan = new HashSet<int>();
		var material = -1;

		foreach ( var h in outgoing )
		{
			if ( HalfEdges[HalfEdges[h].Prev].Twin < 0 )
				throw new InvalidOperationException( "dissolve needs an interior vertex" );

			var f = HalfEdges[h].Face;
			fan.Add( f );

			if ( material < 0 )
				material = Faces[f].Material;
			else if ( Faces[f].Material != material )
				throw new InvalidOperationException( "dissolve would join faces on different material slots" );
		}

		// Each face contributes its corners after the vertex; the connector at each seam repeats, so
		// drop consecutive duplicates and the wrap-around one, keeping the first corner's UV.
		var ring = new List<int>();
		var ringUV = new List<Vec2>();

		foreach ( var h in outgoing )
		{
			var x = HalfEdges[h].Next;

			while ( x != h )
			{
				ring.Add( HalfEdges[x].Origin );
				ringUV.Add( HalfEdges[x].UV );
				x = HalfEdges[x].Next;
			}
		}

		for ( var i = ring.Count - 1; i >= 1; i-- )
		{
			if ( ring[i] == ring[i - 1] )
			{
				ring.RemoveAt( i );
				ringUV.RemoveAt( i );
			}
		}

		if ( ring.Count > 1 && ring[0] == ring[^1] )
		{
			ring.RemoveAt( ring.Count - 1 );
			ringUV.RemoveAt( ringUV.Count - 1 );
		}

		if ( ring.Count < 3 )
			throw new InvalidOperationException( "dissolve would leave too few corners" );

		var faceVertices = new List<List<int>>();
		var faceUVs = new List<List<Vec2>>();
		var materials = new List<int>();
		var loop = new List<int>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			if ( fan.Contains( fi ) )
				continue;

			FaceHalfEdges( fi, loop );

			var verts = new List<int>( loop.Count );
			var uvs = new List<Vec2>( loop.Count );

			foreach ( var h in loop )
			{
				verts.Add( HalfEdges[h].Origin );
				uvs.Add( HalfEdges[h].UV );
			}

			faceVertices.Add( verts );
			faceUVs.Add( uvs );
			materials.Add( Faces[fi].Material );
		}

		faceVertices.Add( ring );
		faceUVs.Add( ringUV );
		materials.Add( material );

		Rebuild( faceVertices, faceUVs, materials );
	}

	bool[] BuildRegionMask( IReadOnlyCollection<int> regionFaces )
	{
		var inRegion = new bool[Faces.Count];

		foreach ( var f in regionFaces )
		{
			if ( f < 0 || f >= Faces.Count )
				throw new ArgumentOutOfRangeException( nameof( regionFaces ), $"face {f} out of range" );
			inRegion[f] = true;
		}

		return inRegion;
	}

	/// <summary>The region's boundary half-edges, grouped into loops with the region on the left of
	/// each, plus every half-edge's original origin vertex.</summary>
	List<List<int>> BoundaryLoops( bool[] inRegion, out Dictionary<int, int> boundaryOrigin )
	{
		boundaryOrigin = new Dictionary<int, int>();

		var boundary = new List<int>();

		for ( var he = 0; he < HalfEdges.Count; he++ )
		{
			if ( !inRegion[HalfEdges[he].Face] )
				continue;

			var twin = HalfEdges[he].Twin;
			var isBoundary = twin < 0 || !inRegion[HalfEdges[twin].Face];

			if ( isBoundary )
				boundary.Add( he );
		}

		var visited = new bool[HalfEdges.Count];
		var loops = new List<List<int>>();

		foreach ( var start in boundary )
		{
			if ( visited[start] )
				continue;

			var loop = new List<int>();
			var he = start;

			do
			{
				visited[he] = true;
				loop.Add( he );

				var h = HalfEdges[he].Next;
				var twin = HalfEdges[h].Twin;

				while ( twin >= 0 && inRegion[HalfEdges[twin].Face] )
				{
					h = HalfEdges[twin].Next;
					twin = HalfEdges[h].Twin;
				}

				he = h;
			} while ( he != start );

			loops.Add( loop );
		}

		foreach ( var loop in loops )
		{
			foreach ( var he in loop )
				boundaryOrigin[he] = HalfEdges[he].Origin;
		}

		return loops;
	}

	/// <summary>Newell normal of a face, from its current vertex positions.</summary>
	Vec3 FaceNormal( int face )
	{
		var loop = new List<int>();
		FaceHalfEdges( face, loop );

		var n = Vec3.Zero;

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = Positions[HalfEdges[loop[i]].Origin];
			var b = Positions[HalfEdges[loop[(i + 1) % loop.Count]].Origin];

			n += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}

		return n.Normal;
	}

	/// <summary>
	/// The shared machinery of extrude and inset: duplicate the region's boundary vertices at the
	/// positions <paramref name="targetForVertex"/> supplies, optionally move interior vertices by
	/// <paramref name="interiorOffset"/>, rewire the region onto the copies, and build the band of
	/// faces joining the old boundary to the new one.
	/// </summary>
	void BandRegion( IReadOnlyCollection<int> regionFaces, Func<int, Vec3> targetForVertex, bool moveInterior )
	{
		var inRegion = BuildRegionMask( regionFaces );
		var loops = BoundaryLoops( inRegion, out var boundaryOrigin );

		var boundaryVertices = new HashSet<int>();
		var duplicate = new Dictionary<int, int>();

		foreach ( var loop in loops )
		{
			foreach ( var he in loop )
			{
				var v = boundaryOrigin[he];
				boundaryVertices.Add( v );

				if ( !duplicate.ContainsKey( v ) )
					duplicate[v] = DuplicateVertex( v, targetForVertex( v ) );
			}
		}

		// Interior region vertices move in place, once each, when the operation asks them to.
		if ( moveInterior )
		{
			var moved = new HashSet<int>();
			var scratch = new List<int>();

			foreach ( var f in regionFaces )
			{
				FaceHalfEdges( f, scratch );

				foreach ( var he in scratch )
				{
					var v = HalfEdges[he].Origin;

					if ( boundaryVertices.Contains( v ) || moved.Contains( v ) )
						continue;

					Positions[v] = targetForVertex( v );
					moved.Add( v );
				}
			}
		}

		// Rewire the region's boundary corners onto the duplicated vertices.
		var rewireScratch = new List<int>();

		foreach ( var f in regionFaces )
		{
			FaceHalfEdges( f, rewireScratch );

			foreach ( var he in rewireScratch )
			{
				var v = HalfEdges[he].Origin;

				if ( !duplicate.TryGetValue( v, out var dup ) )
					continue;

				var h = HalfEdges[he];
				h.Origin = dup;
				HalfEdges[he] = h;
			}
		}

		// Build the band around each boundary loop, then link twins in a second pass.
		foreach ( var loop in loops )
		{
			var k = loop.Count;
			var wallStart = new int[k];

			for ( var i = 0; i < k; i++ )
			{
				var he = loop[i];
				var heNext = loop[(i + 1) % k];
				var v = boundaryOrigin[he];
				var vNext = boundaryOrigin[heNext];
				var nv = duplicate[v];
				var nvNext = duplicate[vNext];

				var faceIndex = Faces.Count;
				var w0 = HalfEdges.Count;

				Faces.Add( new EFace { HalfEdge = w0, Material = Faces[HalfEdges[he].Face].Material } );

				var uv0 = HalfEdges[he].UV;
				var uv1 = HalfEdges[heNext].UV;

				HalfEdges.Add( new HEdge { Origin = v, Next = w0 + 1, Prev = w0 + 3, Twin = -1, Face = faceIndex, UV = uv0 } );
				HalfEdges.Add( new HEdge { Origin = vNext, Next = w0 + 2, Prev = w0, Twin = -1, Face = faceIndex, UV = uv1 } );
				HalfEdges.Add( new HEdge { Origin = nvNext, Next = w0 + 3, Prev = w0 + 1, Twin = -1, Face = faceIndex, UV = uv1 } );
				HalfEdges.Add( new HEdge { Origin = nv, Next = w0, Prev = w0 + 2, Twin = -1, Face = faceIndex, UV = uv0 } );

				wallStart[i] = w0;
				VertexHalfEdge[v] = w0;
				VertexHalfEdge[nv] = w0 + 3;
			}

			for ( var i = 0; i < k; i++ )
			{
				var he = loop[i];
				var w0 = wallStart[i];
				var wNext = wallStart[(i + 1) % k];

				// Outer edge links back to the mesh the region was lifted off (or to the boundary).
				var oldTwin = HalfEdges[he].Twin;

				var bottom = HalfEdges[w0];
				bottom.Twin = oldTwin;
				HalfEdges[w0] = bottom;

				if ( oldTwin >= 0 )
				{
					var opposite = HalfEdges[oldTwin];
					opposite.Twin = w0;
					HalfEdges[oldTwin] = opposite;
				}

				// Inner edge links to the re-wired region boundary.
				var region = HalfEdges[he];
				region.Twin = w0 + 2;
				HalfEdges[he] = region;

				var top = HalfEdges[w0 + 2];
				top.Twin = he;
				HalfEdges[w0 + 2] = top;

				// Side edges link each band face to its neighbours around the loop.
				var rising = HalfEdges[w0 + 1];
				rising.Twin = wNext + 3;
				HalfEdges[w0 + 1] = rising;

				var falling = HalfEdges[wNext + 3];
				falling.Twin = w0 + 1;
				HalfEdges[wNext + 3] = falling;
			}
		}
	}
}

/// <summary>What a click picks in Edit mode.</summary>
public enum EditElement
{
	Vertex,
	Edge,
	Face,
}

/// <summary>
/// One Edit-mode session on one body: the working mesh, the selection, and undo.
///
/// ENGINE-FREE ON PURPOSE. The editor owns picking, drawing and the mouse; everything that decides
/// what an operation DOES lives here, so the headless suite can drive a whole session — select,
/// extrude, drag, undo, commit — without s&box.
///
/// THE WORKING MESH IS A <see cref="PolyMesh"/>, and each operation round-trips through
/// <see cref="EditableMesh"/>. Selection is by vertex index, <see cref="EdgeKey"/> and face index,
/// which survive that round trip because EditableMesh keeps vertex order and face order. An
/// operation that re-derives faces (dissolve, delete) says what the selection becomes afterwards.
///
/// UNDO IS ONE STEP PER OPERATION, NOT PER MOUSE SAMPLE. A drag is <see cref="BeginDrag"/> /
/// <see cref="Drag"/> / <see cref="EndDrag"/> and lands as one step; a scrubbed operation is
/// <see cref="Preview"/> repeated, then <see cref="Accept"/> or <see cref="Cancel"/>, and also lands
/// as one step. Undo inside the session is the session's own stack; leaving the session hands the
/// finished mesh to <see cref="MeshEditFeature.Commit"/>, which is one step on the document's stack.
/// </summary>
public sealed class MeshEditSession
{
	readonly struct Snapshot
	{
		public readonly PolyMesh Mesh;
		public readonly EditElement Mode;
		public readonly int[] Vertices;
		public readonly EdgeKey[] Edges;
		public readonly int[] Faces;
		public readonly string Label;
		public readonly List<PolyMesh> Separated;
		public readonly EdgeKey[] Seams;
		public readonly int RetopoStart, RetopoFaceStart;

		/// <summary>The surface snapping was holding — a reference, not a copy: it is replaced, never
		/// edited, so undoing a Finish retopo can put the traced surface back.</summary>
		public readonly PolyMesh SnapTarget;
		public readonly MeshBVH SnapTree;

		/// <summary>
		/// A tail snapshot keeps only what retopology added — the vertices from RetopoStart and the
		/// faces from RetopoFaceStart — and no copy of the body being traced. A click of poly build
		/// on a million-triangle sculpt then costs a few hundred bytes of undo, not the sculpt.
		///
		/// Sound because undo is last-in-first-out: anything that changed the traced body since
		/// this snapshot took a full snapshot of its own, and has been undone before this one is
		/// restored, so the body in front of the tail is the body this tail was taken over.
		/// </summary>
		public readonly bool IsTail;
		public readonly Vec3[] TailPositions;
		public readonly BoneWeight[][] TailSkin;
		public readonly Vec4[] TailColours;
		public readonly Face[] TailFaces;

		public Snapshot( MeshEditSession s, string label, bool tail = false )
		{
			Separated = new List<PolyMesh>( s.Separated );
			Seams = new List<EdgeKey>( s.Seams ).ToArray();
			RetopoStart = s.RetopoStart;
			RetopoFaceStart = s._retopoFaceStart;
			SnapTarget = s._snapTarget;
			SnapTree = s._snapTree;
			IsTail = tail && s.RetopoStart >= 0 && s._retopoFaceStart >= 0;
			Mesh = null;
			TailPositions = null;
			TailSkin = null;
			TailColours = null;
			TailFaces = null;

			if ( IsTail )
			{
				var m = s.Mesh;
				TailPositions = m.Positions.GetRange( RetopoStart, m.VertexCount - RetopoStart ).ToArray();

				if ( m.Skin is not null && m.Skin.Count > RetopoStart )
					TailSkin = m.Skin.Vertices.GetRange( RetopoStart, m.Skin.Count - RetopoStart ).ToArray();

				if ( m.VertexColors is not null && m.VertexColors.Length > RetopoStart )
					TailColours = m.VertexColors[RetopoStart..];

				TailFaces = new Face[m.FaceCount - RetopoFaceStart];
				for ( var f = 0; f < TailFaces.Length; f++ )
				{
					var face = m.Faces[RetopoFaceStart + f];
					TailFaces[f] = new Face( (int[])face.Indices.Clone(), (Vec2[])face.UVs.Clone(), face.Material );
				}
			}
			else
				Mesh = s.Mesh.Clone();

			Mode = s.Mode;
			Vertices = new List<int>( s.SelectedVertices ).ToArray();
			Edges = new List<EdgeKey>( s.SelectedEdges ).ToArray();
			Faces = new List<int>( s.SelectedFaces ).ToArray();
			Label = label;
		}
	}

	readonly List<Snapshot> _undo = new();
	readonly List<Snapshot> _redo = new();

	/// <summary>The body the session started from. What <see cref="MeshEditFeature"/> fingerprints.</summary>
	public PolyMesh Base { get; }

	/// <summary>The mesh being edited. Replaced, not mutated, by most operations — hold the session,
	/// not this.</summary>
	public PolyMesh Mesh { get; private set; }

	public EditElement Mode { get; private set; } = EditElement.Face;

	public HashSet<int> SelectedVertices { get; } = new();
	public HashSet<EdgeKey> SelectedEdges { get; } = new();
	public HashSet<int> SelectedFaces { get; } = new();

	/// <summary>
	/// Edges the unwrapper may not run a chart across — Blender's Mark Seam. Cutting a model open
	/// along seams is how you decide where the texture is allowed to break, and it is the difference
	/// between an automatic unwrap and one you chose.
	///
	/// Held on the session and in its undo history, NOT saved with the edit: the committed mesh
	/// carries the UVs the unwrap produced, so reopening the edit gives you the result, not the cuts
	/// that made it.
	/// </summary>
	public HashSet<EdgeKey> Seams { get; } = new();

	/// <summary>Bumped on every change to the mesh, so a preview knows when to re-upload.</summary>
	public int Revision { get; private set; }

	/// <summary>
	/// Bumped only when the mesh itself is replaced — an operation, an undo, a redo — and NOT while a
	/// drag is moving vertices about. Anything derived from the topology rather than the positions
	/// (the wireframe's edge list, say) can cache against this and survive a whole drag.
	/// </summary>
	public int TopologyRevision { get; private set; }

	/// <summary>Bumped on every change to the selection, so the overlay knows when to redraw.</summary>
	public int SelectionRevision { get; private set; }

	/// <summary>True once anything changed the mesh. A session that changed nothing commits nothing.</summary>
	public bool HasChanges => _undo.Count > 0;

	public int UndoCount => _undo.Count;
	public int RedoCount => _redo.Count;
	public string LastLabel => _undo.Count > 0 ? _undo[^1].Label : null;

	/// <summary>The label of every step that can be undone, oldest first — for a history list.</summary>
	public IReadOnlyList<string> UndoLabels => _undo.ConvertAll( u => u.Label );

	/// <summary>
	/// X-mirror editing: a vertex on the x = 0 plane stays on it when moved, and every vertex's
	/// mirror partner (the vertex at -x within <see cref="MirrorTolerance"/>) moves with it.
	/// </summary>
	public bool MirrorX { get; set; }

	public float MirrorTolerance { get; set; } = 1e-3f;

	/// <summary>Surface snapping. When set, dragged vertices land on this body's nearest surface
	/// plus <see cref="SnapOffset"/>. Must be in the same space as <see cref="Mesh"/>.</summary>
	public PolyMesh SnapTarget
	{
		get => _snapTarget;
		set
		{
			_snapTarget = value;
			_snapTree = value is null ? null : MeshBVH.Build( value );
		}
	}

	public float SnapOffset { get; set; }

	public float SnapMaxDistance { get; set; } = 1000f;

	PolyMesh _snapTarget;
	MeshBVH _snapTree;

	public MeshEditSession( PolyMesh body ) : this( body, body )
	{
	}

	/// <summary>Resume an edit: <paramref name="body"/> is what the feature receives from above (what
	/// the commit is fingerprinted against), <paramref name="start"/> is the edit made so far.</summary>
	public MeshEditSession( PolyMesh body, PolyMesh start )
	{
		if ( body is null )
			throw new ArgumentNullException( nameof( body ) );

		Base = body.Clone();
		Mesh = (start ?? body).Clone();
	}

	// --- selection ---------------------------------------------------------------------------

	/// <summary>
	/// Switch vertex / edge / face picking, converting the selection the way Blender does: whatever
	/// vertices are selected stay selected, and edges or faces are selected when all their vertices
	/// are. Going to Face from a lone vertex therefore selects nothing, which is correct.
	/// </summary>
	public void SetMode( EditElement mode )
	{
		if ( mode == Mode )
			return;

		var verts = AffectedVertices();
		ClearSelection();
		Mode = mode;

		switch ( mode )
		{
			case EditElement.Vertex:
				SelectedVertices.UnionWith( verts );
				break;

			case EditElement.Edge:
				foreach ( var key in Mesh.BuildEdgeFaces().Keys )
				{
					if ( verts.Contains( key.A ) && verts.Contains( key.B ) )
						SelectedEdges.Add( key );
				}
				break;

			case EditElement.Face:
				for ( var f = 0; f < Mesh.FaceCount; f++ )
				{
					if ( Array.TrueForAll( Mesh.Faces[f].Indices, verts.Contains ) )
						SelectedFaces.Add( f );
				}
				break;
		}

		SelectionRevision++;
	}

	public void ClearSelection()
	{
		SelectedVertices.Clear();
		SelectedEdges.Clear();
		SelectedFaces.Clear();
		SelectionRevision++;
	}

	public void SelectAll()
	{
		ClearSelection();

		switch ( Mode )
		{
			case EditElement.Vertex:
				foreach ( var f in Mesh.Faces )
					SelectedVertices.UnionWith( f.Indices );
				break;
			case EditElement.Edge:
				SelectedEdges.UnionWith( Mesh.BuildEdgeFaces().Keys );
				break;
			case EditElement.Face:
				for ( var f = 0; f < Mesh.FaceCount; f++ )
					SelectedFaces.Add( f );
				break;
		}
	}

	/// <summary>How a click combines with what is already selected.</summary>
	public enum Combine { Replace, Add, Remove, Toggle }

	public void SelectVertex( int vertex, Combine how = Combine.Replace ) => Apply( SelectedVertices, vertex, how );
	public void SelectEdge( EdgeKey edge, Combine how = Combine.Replace ) => Apply( SelectedEdges, edge, how );
	public void SelectFace( int face, Combine how = Combine.Replace ) => Apply( SelectedFaces, face, how );

	void Apply<T>( HashSet<T> set, T item, Combine how )
	{
		switch ( how )
		{
			case Combine.Replace:
				ClearSelection();
				set.Add( item );
				break;
			case Combine.Add:
				set.Add( item );
				break;
			case Combine.Remove:
				set.Remove( item );
				break;
			case Combine.Toggle:
				if ( !set.Remove( item ) )
					set.Add( item );
				break;
		}

		SelectionRevision++;
	}

	/// <summary>
	/// Select the edge loop through an edge, Blender's Alt+click: across each vertex to the edge
	/// opposite in a four-edge fan, both ways, until the loop closes, hits a pole or runs off the
	/// boundary.
	/// </summary>
	public void SelectEdgeLoop( EdgeKey seed, Combine how = Combine.Replace )
	{
		var loop = EdgeLoop( Mesh, seed );

		if ( how == Combine.Replace )
			ClearSelection();

		foreach ( var e in loop )
		{
			if ( how == Combine.Remove )
				SelectedEdges.Remove( e );
			else
				SelectedEdges.Add( e );
		}

		SelectionRevision++;
	}

	/// <summary>Every vertex the selection touches, whatever the mode. What Move moves.</summary>
	public HashSet<int> AffectedVertices()
	{
		var set = new HashSet<int>( SelectedVertices );

		foreach ( var e in SelectedEdges )
		{
			set.Add( e.A );
			set.Add( e.B );
		}

		foreach ( var f in SelectedFaces )
		{
			if ( f >= 0 && f < Mesh.FaceCount )
				set.UnionWith( Mesh.Faces[f].Indices );
		}

		return set;
	}

	/// <summary>The selection's centre, for a gizmo. Zero when nothing is selected.</summary>
	public Vec3 SelectionCentre()
	{
		var verts = AffectedVertices();

		if ( verts.Count == 0 )
			return Vec3.Zero;

		var sum = Vec3.Zero;
		foreach ( var v in verts )
			sum += Mesh.Positions[v];

		return sum / verts.Count;
	}

	// --- undo --------------------------------------------------------------------------------

	void Push( string label, bool tail = false )
	{
		_undo.Add( new Snapshot( this, label, tail ) );
		_redo.Clear();

		// A cap, not a leak: a long session on a dense mesh would otherwise hold every step's copy.
		if ( _undo.Count > 100 )
			_undo.RemoveAt( 0 );
	}

	void Restore( Snapshot s )
	{
		Separated = new List<PolyMesh>( s.Separated );
		Seams.Clear();
		Seams.UnionWith( s.Seams );
		RetopoStart = s.RetopoStart;
		_retopoFaceStart = s.RetopoFaceStart;
		// Only a retopology snapshot owns the snap target; anywhere else it belongs to the Snap
		// toggle, and undoing an edit must not quietly switch that.
		if ( s.RetopoStart >= 0 )
		{
			_snapTarget = s.SnapTarget;
			_snapTree = s.SnapTree;
		}

		if ( s.IsTail )
			RestoreTail( s );
		else
			Mesh = s.Mesh.Clone();

		Mode = s.Mode;
		SelectedVertices.Clear();
		SelectedVertices.UnionWith( s.Vertices );
		SelectedEdges.Clear();
		SelectedEdges.UnionWith( s.Edges );
		SelectedFaces.Clear();
		SelectedFaces.UnionWith( s.Faces );
		Changed();
		SelectionRevision++;
	}

	/// <summary>Put the retopology tail back over the body in front of it — see <see cref="Snapshot.IsTail"/>.</summary>
	void RestoreTail( Snapshot s )
	{
		var m = Mesh;
		m.Positions.RemoveRange( s.RetopoStart, m.VertexCount - s.RetopoStart );
		m.Positions.AddRange( s.TailPositions );

		if ( m.Skin is not null )
		{
			if ( m.Skin.Count > s.RetopoStart )
				m.Skin.Vertices.RemoveRange( s.RetopoStart, m.Skin.Count - s.RetopoStart );

			if ( s.TailSkin is not null )
				m.Skin.Vertices.AddRange( s.TailSkin );
		}

		if ( m.VertexColors is not null )
		{
			var colours = m.VertexColors;
			Array.Resize( ref colours, s.RetopoStart + (s.TailColours?.Length ?? 0) );
			s.TailColours?.CopyTo( colours, s.RetopoStart );
			m.VertexColors = colours;
		}

		m.Faces.RemoveRange( s.RetopoFaceStart, m.FaceCount - s.RetopoFaceStart );

		foreach ( var face in s.TailFaces )
			m.Faces.Add( new Face( (int[])face.Indices.Clone(), (Vec2[])face.UVs.Clone(), face.Material ) );
	}

	public bool Undo()
	{
		if ( _preview is not null )
		{
			Cancel();
			return true;
		}

		if ( _undo.Count == 0 )
			return false;

		_redo.Add( new Snapshot( this, _undo[^1].Label, _undo[^1].IsTail ) );
		Restore( _undo[^1] );
		_undo.RemoveAt( _undo.Count - 1 );
		return true;
	}

	public bool Redo()
	{
		if ( _redo.Count == 0 )
			return false;

		_undo.Add( new Snapshot( this, _redo[^1].Label, _redo[^1].IsTail ) );
		Restore( _redo[^1] );
		_redo.RemoveAt( _redo.Count - 1 );
		return true;
	}

	void Changed()
	{
		Revision++;
		TopologyRevision++;

		// A seam whose edge an operation deleted must go with it, or it steers a later unwrap from
		// an edge that is not there and the overlay draws a vertex that has been renumbered. Undo
		// puts it back, because the snapshot was taken before the operation ran.
		PruneSeams();
		if ( _snapTree is not null && ReferenceEquals( _snapTarget, Mesh ) )
			_snapTree = MeshBVH.Build( Mesh );
	}

	// --- scrubbed operations -----------------------------------------------------------------

	Snapshot? _preview;
	string _previewLabel;

	/// <summary>True between the first <see cref="Preview"/> and <see cref="Accept"/>/<see cref="Cancel"/>.</summary>
	public bool IsPreviewing => _preview is not null;

	public string PreviewLabel => _previewLabel;

	/// <summary>
	/// Run an operation from the state before the first preview, replacing the last preview. The
	/// operations panel calls this on every slider move; nothing reaches undo until Accept. If the
	/// operation throws, the mesh is left as it was before the first preview and the exception
	/// propagates — the panel shows the message and keeps the slider live.
	/// </summary>
	public void Preview( string label, Action<MeshEditSession> operation )
	{
		if ( operation is null )
			throw new ArgumentNullException( nameof( operation ) );

		if ( _preview is null )
		{
			_preview = new Snapshot( this, label );
			_previewLabel = label;
		}
		else
		{
			Restore( _preview.Value );
		}

		try
		{
			_inPreview = true;
			operation( this );
		}
		catch
		{
			Restore( _preview.Value );
			throw;
		}
		finally
		{
			_inPreview = false;
		}
	}

	bool _inPreview;

	public void Accept()
	{
		if ( _preview is null )
			return;

		_undo.Add( _preview.Value );
		_redo.Clear();
		_preview = null;
		_previewLabel = null;
	}

	public void Cancel()
	{
		if ( _preview is null )
			return;

		Restore( _preview.Value );
		_preview = null;
		_previewLabel = null;
	}

	/// <summary>Every operation below goes through this: one undo step, unless it is running inside
	/// a Preview, which owns the step.</summary>
	void Step( string label, Action body, bool tail = false )
	{
		if ( !_inPreview )
			Push( label, tail );

		try
		{
			body();
		}
		catch
		{
			if ( !_inPreview )
			{
				Restore( _undo[^1] );
				_undo.RemoveAt( _undo.Count - 1 );
			}

			throw;
		}

		Changed();
	}

	// --- operations --------------------------------------------------------------------------

	/// <summary>Extrude the selected faces, pushing each face out along the group's average normal. If edges are selected, extrude them into walls.</summary>
	public void Extrude( float distance )
	{
		if ( Mode == EditElement.Edge )
		{
			ExtrudeEdges( distance );
			return;
		}

		var faces = RequireFaces( "Extrude" );

		Step( "Extrude", () =>
		{
			var normal = Vec3.Zero;
			foreach ( var f in faces )
				normal += Mesh.FaceNormal( Mesh.Faces[f] ) * Mesh.FaceArea( Mesh.Faces[f] );

			if ( normal.LengthSquared < 1e-12f )
				throw new InvalidOperationException( "The selected faces point in opposite directions, so there is no one way to extrude them. Extrude them in smaller groups." );

			var e = EditableMesh.FromPolyMesh( Mesh );
			var wallStart = e.Faces.Count;
			e.ExtrudeRegion( faces, normal.Normal * distance );
			Mesh = e.ToPolyMesh();

			// Blender leaves the whole new extrusion selected: cap and side walls. ExtrudeRegion
			// appends the band faces last, so the wall faces are exactly the trailing block. Without
			// this the walls (and nothing else) stay unselected and pressing E again refuses.
			ClearSelection();
			for ( var f = wallStart; f < e.Faces.Count; f++ )
				SelectedFaces.Add( f );
		} );
	}

	private void ExtrudeEdges( float distance )
	{
		var edges = new List<EdgeKey>( SelectedEdges );
		if ( edges.Count == 0 )
			throw new InvalidOperationException( "Select one or more edges to extrude." );

		Step( "Extrude", () =>
		{
			var m = Mesh.Clone();
			var verts = new HashSet<int>();
			foreach ( var e in edges )
			{
				verts.Add( e.A );
				verts.Add( e.B );
			}

			var normals = m.ComputeVertexNormals();
			var averageNormal = Vec3.Zero;
			foreach ( var v in verts )
				averageNormal += normals[v];

			if ( averageNormal.LengthSquared < 1e-12f )
				averageNormal = new Vec3( 0, 0, 1 );

			var offset = averageNormal.Normal * distance;
			var oldToNew = new Dictionary<int, int>();

			foreach ( var v in verts )
			{
				var newPos = m.Positions[v] + offset;
				var newIdx = m.Positions.Count;
				m.Positions.Add( newPos );

				if ( m.Skin is not null )
					m.Skin.Vertices.Add( (BoneWeight[])m.Skin.Vertices[v].Clone() );

				if ( m.VertexColors is not null )
				{
					Array.Resize( ref m.VertexColors, m.Positions.Count );
					m.VertexColors[newIdx] = m.VertexColors[v];
				}

				oldToNew[v] = newIdx;
			}

			var edgeMaterial = new Dictionary<EdgeKey, int>();
			for ( var i = 0; i < m.Faces.Count; i++ )
			{
				var f = m.Faces[i];
				for ( var j = 0; j < f.Indices.Length; j++ )
				{
					var edge = new EdgeKey( f.Indices[j], f.Indices[(j + 1) % f.Indices.Length] );
					if ( edges.Contains( edge ) )
						edgeMaterial[edge] = f.Material;
				}
			}

			var newSelectedEdges = new HashSet<EdgeKey>();

			foreach ( var e in edges )
			{
				var oldA = e.A;
				var oldB = e.B;
				var newA = oldToNew[oldA];
				var newB = oldToNew[oldB];

				var mat = edgeMaterial.TryGetValue( e, out var mVal ) ? mVal : 0;
				m.Faces.Add( new Face( new[] { oldA, oldB, newB, newA }, null, mat ) );
				newSelectedEdges.Add( new EdgeKey( newA, newB ) );
			}

			Mesh = m;
			SelectedEdges.Clear();
			foreach ( var e in newSelectedEdges ) SelectedEdges.Add( e );
			SelectedVertices.Clear();
			foreach ( var v in oldToNew.Values ) SelectedVertices.Add( v );
		} );
	}

	/// <summary>Extrude the selected faces, pushing each vertex along its own normal. The faces stay together.</summary>
	public void ExtrudeAlongNormals( float distance )
	{
		var faces = RequireFaces( "Extrude Along Normals" );

		Step( "Extrude Along Normals", () =>
		{
			var e = EditableMesh.FromPolyMesh( Mesh );
			var normals = Mesh.ComputeVertexNormals();
			e.ExtrudeRegion( faces, v => e.Positions[v] + normals[v] * distance );
			Mesh = e.ToPolyMesh();
		} );
	}

	/// <summary>Flatten the selected vertices to their best-fit plane.</summary>
	public void FlattenFaces()
	{
		var verts = RequireVertices( "Flatten" );

		Step( "Flatten", () =>
		{
			var points = new List<Vec3>( verts.Count );
			foreach ( var v in verts ) points.Add( Mesh.Positions[v] );

			var normal = new Vec3( 0, 0, 0 );
			for ( var i = 0; i < points.Count; i++ )
			{
				var a = points[i];
				var b = points[(i + 1) % points.Count];
				normal += new Vec3(
					(a.y - b.y) * (a.z + b.z),
					(a.z - b.z) * (a.x + b.x),
					(a.x - b.x) * (a.y + b.y)
				);
			}

			if ( normal.LengthSquared > 1e-12f )
			{
				normal = normal.Normal;
				var center = Vec3.Zero;
				foreach ( var p in points ) center += p;
				center /= points.Count;

				var d = Vec3.Dot( center, normal );
				var m = Mesh.Clone();
				foreach ( var v in verts )
				{
					var p = m.Positions[v];
					var dist = Vec3.Dot( p, normal ) - d;
					m.Positions[v] = p - normal * dist;
				}
				Mesh = m;
			}
		} );
	}

	/// <summary>Turn the selected vertices into a regular circle on their best-fit plane — LoopTools Circle.</summary>
	public void LoopCircle()
	{
		var verts = RequireVertices( "Circle" );
		if ( verts.Count < 3 )
			throw new InvalidOperationException( "Circle requires at least 3 vertices." );

		Step( "Circle", () =>
		{
			var m = Mesh.Clone();
			var vertList = new List<int>( verts );

			// Build adjacency map between selected vertices using mesh faces
			var edgeMap = new Dictionary<int, List<int>>();
			foreach ( var v in vertList ) edgeMap[v] = new List<int>();

			foreach ( var face in m.Faces )
			{
				for ( var i = 0; i < face.Indices.Length; i++ )
				{
					var a = face.Indices[i];
					var b = face.Indices[(i + 1) % face.Indices.Length];
					if ( verts.Contains( a ) && verts.Contains( b ) )
					{
						if ( !edgeMap[a].Contains( b ) ) edgeMap[a].Add( b );
						if ( !edgeMap[b].Contains( a ) ) edgeMap[b].Add( a );
					}
				}
			}

			// Try to walk a continuous loop or chain
			var orderedVerts = new List<int>();
			var visited = new HashSet<int>();
			var start = vertList[0];
			foreach ( var v in vertList )
			{
				if ( edgeMap[v].Count == 1 )
				{
					start = v;
					break;
				}
			}

			var current = start;
			while ( current >= 0 && visited.Add( current ) )
			{
				orderedVerts.Add( current );
				var next = -1;
				foreach ( var nbr in edgeMap[current] )
				{
					if ( !visited.Contains( nbr ) )
					{
						next = nbr;
						break;
					}
				}
				current = next;
			}

			if ( orderedVerts.Count < vertList.Count )
			{
				foreach ( var v in vertList )
				{
					if ( !visited.Contains( v ) )
						orderedVerts.Add( v );
				}
			}

			// Calculate center
			var center = Vec3.Zero;
			foreach ( var v in orderedVerts )
				center += m.Positions[v];
			center /= orderedVerts.Count;

			// Normal of loop
			var normal = Vec3.Zero;
			for ( var i = 0; i < orderedVerts.Count; i++ )
			{
				var a = m.Positions[orderedVerts[i]] - center;
				var b = m.Positions[orderedVerts[(i + 1) % orderedVerts.Count]] - center;
				normal += Vec3.Cross( a, b );
			}

			if ( normal.LengthSquared < 1e-12f )
				normal = new Vec3( 0, 0, 1 );
			else
				normal = normal.Normal;

			var seed = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
			var u = Vec3.Cross( seed, normal ).Normal;
			var vAxis = Vec3.Cross( normal, u ).Normal;

			// Average in-plane radius
			var avgRadius = 0f;
			foreach ( var v in orderedVerts )
			{
				var p = m.Positions[v] - center;
				var inPlane = p - normal * Vec3.Dot( p, normal );
				avgRadius += inPlane.Length;
			}
			avgRadius /= orderedVerts.Count;
			if ( avgRadius < 1e-6f ) avgRadius = 1f;

			// If no topological edges connect them, sort by angle
			if ( edgeMap[vertList[0]].Count == 0 )
			{
				orderedVerts.Sort( ( a, b ) =>
				{
					var pa = m.Positions[a] - center;
					var pb = m.Positions[b] - center;
					var angA = MathF.Atan2( Vec3.Dot( pa, vAxis ), Vec3.Dot( pa, u ) );
					var angB = MathF.Atan2( Vec3.Dot( pb, vAxis ), Vec3.Dot( pb, u ) );
					return angA.CompareTo( angB );
				} );
			}

			var firstP = m.Positions[orderedVerts[0]] - center;
			var startAngle = MathF.Atan2( Vec3.Dot( firstP, vAxis ), Vec3.Dot( firstP, u ) );

			var stepDir = 1f;
			if ( orderedVerts.Count > 1 )
			{
				var secondP = m.Positions[orderedVerts[1]] - center;
				var secondAngle = MathF.Atan2( Vec3.Dot( secondP, vAxis ), Vec3.Dot( secondP, u ) );
				var diff = secondAngle - startAngle;
				while ( diff <= -MathF.PI ) diff += MathF.PI * 2;
				while ( diff > MathF.PI ) diff -= MathF.PI * 2;
				stepDir = diff >= 0 ? 1f : -1f;
			}

			var n = orderedVerts.Count;
			for ( var i = 0; i < n; i++ )
			{
				var angle = startAngle + stepDir * (i * MathF.PI * 2 / n);
				m.Positions[orderedVerts[i]] = center + (u * MathF.Cos( angle ) + vAxis * MathF.Sin( angle )) * avgRadius;
			}

			Mesh = m;
		} );
	}

	/// <summary>Distribute the selected vertices evenly along their edge chain or loop — LoopTools Space.</summary>
	public void LoopSpace()
	{
		var verts = RequireVertices( "Space" );
		if ( verts.Count < 3 )
			throw new InvalidOperationException( "Space requires at least 3 vertices." );

		Step( "Space", () =>
		{
			var m = Mesh.Clone();
			var vertList = new List<int>( verts );

			var edgeMap = new Dictionary<int, List<int>>();
			foreach ( var v in vertList ) edgeMap[v] = new List<int>();

			foreach ( var face in m.Faces )
			{
				for ( var i = 0; i < face.Indices.Length; i++ )
				{
					var a = face.Indices[i];
					var b = face.Indices[(i + 1) % face.Indices.Length];
					if ( verts.Contains( a ) && verts.Contains( b ) )
					{
						if ( !edgeMap[a].Contains( b ) ) edgeMap[a].Add( b );
						if ( !edgeMap[b].Contains( a ) ) edgeMap[b].Add( a );
					}
				}
			}

			var orderedVerts = new List<int>();
			var visited = new HashSet<int>();
			var start = vertList[0];
			var isClosed = true;

			foreach ( var v in vertList )
			{
				if ( edgeMap[v].Count == 1 )
				{
					start = v;
					isClosed = false;
					break;
				}
			}

			var current = start;
			while ( current >= 0 && visited.Add( current ) )
			{
				orderedVerts.Add( current );
				var next = -1;
				foreach ( var nbr in edgeMap[current] )
				{
					if ( !visited.Contains( nbr ) )
					{
						next = nbr;
						break;
					}
				}
				current = next;
			}

			if ( orderedVerts.Count < 3 )
				return;

			if ( isClosed && !edgeMap[orderedVerts[^1]].Contains( orderedVerts[0] ) )
				isClosed = false;

			var n = orderedVerts.Count;
			var cumulative = new float[n + (isClosed ? 1 : 0)];
			cumulative[0] = 0f;

			for ( var i = 0; i < n - 1; i++ )
			{
				var d = (m.Positions[orderedVerts[i + 1]] - m.Positions[orderedVerts[i]]).Length;
				cumulative[i + 1] = cumulative[i] + d;
			}

			if ( isClosed )
			{
				var d = (m.Positions[orderedVerts[0]] - m.Positions[orderedVerts[^1]]).Length;
				cumulative[n] = cumulative[n - 1] + d;
			}

			var totalLen = cumulative[^1];
			if ( totalLen < 1e-6f )
				return;

			var newPositions = new Vec3[n];
			newPositions[0] = m.Positions[orderedVerts[0]];

			var stepDist = isClosed ? totalLen / n : totalLen / (n - 1);

			for ( var i = 1; i < (isClosed ? n : n - 1); i++ )
			{
				var targetDist = i * stepDist;

				var seg = 0;
				while ( seg < cumulative.Length - 1 && cumulative[seg + 1] < targetDist )
					seg++;

				var segStartDist = cumulative[seg];
				var segEndDist = cumulative[seg + 1];
				var t = segEndDist > segStartDist ? (targetDist - segStartDist) / (segEndDist - segStartDist) : 0f;

				var pA = m.Positions[orderedVerts[seg % n]];
				var pB = m.Positions[orderedVerts[(seg + 1) % n]];
				newPositions[i] = pA + (pB - pA) * t;
			}

			if ( !isClosed )
				newPositions[n - 1] = m.Positions[orderedVerts[n - 1]];

			for ( var i = 0; i < n; i++ )
				m.Positions[orderedVerts[i]] = newPositions[i];

			Mesh = m;
		} );
	}

	/// <summary>Connect two selected vertices by cutting an edge across their shared face (J).</summary>
	public void ConnectVertices()
	{
		var verts = RequireVertices( "Connect" );
		if ( verts.Count != 2 )
			throw new InvalidOperationException( "Select exactly two vertices across a face to connect them (J)." );

		var v1 = -1;
		var v2 = -1;
		foreach ( var v in verts )
		{
			if ( v1 < 0 ) v1 = v;
			else v2 = v;
		}

		Step( "Connect Vertices", () =>
		{
			var m = Mesh.Clone();
			var faceIdx = -1;
			int idx1 = -1, idx2 = -1;

			for ( var fi = 0; fi < m.Faces.Count; fi++ )
			{
				var f = m.Faces[fi];
				var i1 = Array.IndexOf( f.Indices, v1 );
				var i2 = Array.IndexOf( f.Indices, v2 );

				if ( i1 >= 0 && i2 >= 0 )
				{
					var count = f.Indices.Length;
					if ( Math.Abs( i1 - i2 ) != 1 && Math.Abs( i1 - i2 ) != count - 1 )
					{
						faceIdx = fi;
						idx1 = i1;
						idx2 = i2;
						break;
					}
				}
			}

			if ( faceIdx < 0 )
				throw new InvalidOperationException( "The two selected vertices must share a face (and not already be directly connected)." );

			var oldFace = m.Faces[faceIdx];
			var n = oldFace.Indices.Length;

			if ( idx1 > idx2 )
			{
				var tmp = idx1; idx1 = idx2; idx2 = tmp;
			}

			// Sub-face 1: from idx1 to idx2
			var count1 = idx2 - idx1 + 1;
			var indices1 = new int[count1];
			var uvs1 = new Vec2[count1];
			for ( var i = 0; i < count1; i++ )
			{
				indices1[i] = oldFace.Indices[idx1 + i];
				uvs1[i] = oldFace.UVs[idx1 + i];
			}

			// Sub-face 2: from idx2 wrapping around to idx1
			var count2 = (n - idx2) + idx1 + 1;
			var indices2 = new int[count2];
			var uvs2 = new Vec2[count2];
			for ( var i = 0; i < count2; i++ )
			{
				var cur = (idx2 + i) % n;
				indices2[i] = oldFace.Indices[cur];
				uvs2[i] = oldFace.UVs[cur];
			}

			m.Faces[faceIdx] = new Face( indices1, uvs1, oldFace.Material );
			m.Faces.Add( new Face( indices2, uvs2, oldFace.Material ) );

			Mesh = m;
			SelectedEdges.Clear();
			SelectedEdges.Add( new EdgeKey( v1, v2 ) );
		} );
	}

	/// <summary>
	/// Relax the selected vertices along the surface without collapsing volume — LoopTools Relax.
	/// Vertices move tangential to the surface towards the average of their neighbours.
	/// </summary>
	public void Relax( float strength = 0.5f, int iterations = 4 )
	{
		var moving = AffectedVertices();

		if ( moving.Count == 0 )
		{
			moving = new HashSet<int>();
			for ( var i = 0; i < Mesh.VertexCount; i++ )
				moving.Add( i );
		}

		if ( moving.Count == 0 )
			throw new InvalidOperationException( "There is nothing to relax." );

		strength = Math.Clamp( strength, 0f, 1f );
		iterations = Math.Clamp( iterations, 1, 100 );

		Step( "Relax", () =>
		{
			var neighbours = VertexNeighbours();
			var boundary = BoundaryVertexSet();
			var normals = Mesh.ComputeVertexNormals();
			var positions = Mesh.Positions;
			var scratch = new Vec3[positions.Count];

			for ( var pass = 0; pass < iterations; pass++ )
			{
				for ( var i = 0; i < positions.Count; i++ )
					scratch[i] = positions[i];

				foreach ( var v in moving )
				{
					if ( boundary.Contains( v ) || !neighbours.TryGetValue( v, out var around ) || around.Count == 0 )
						continue;

					var sum = Vec3.Zero;
					foreach ( var n in around )
						sum += scratch[n];

					var avg = sum / around.Count;
					var delta = avg - scratch[v];

					var nrm = normals[v];
					if ( nrm.LengthSquared > 1e-12f )
					{
						nrm = nrm.Normal;
						delta -= nrm * Vec3.Dot( delta, nrm );
					}

					var target = scratch[v] + delta * strength;

					if ( MirrorX && MathF.Abs( scratch[v].x ) <= MirrorTolerance )
						target = new Vec3( 0f, target.y, target.z );

					positions[v] = target;
				}
			}
		} );
	}

	/// <summary>Merge coplanar faces into n-gons across the whole mesh.</summary>
	public void LimitedDissolve()
	{
		Step( "Limited Dissolve", () =>
		{
			var m = Mesh.Clone();
			if ( CoplanarMerge.Merge( m ) > 0 )
			{
				Mesh = m;
				ClearSelection();
			}
		} );
	}

	/// <summary>Extrude each selected face separately along its own normal. The faces pull apart from each other.</summary>
	public void ExtrudeIndividual( float distance )
	{
		var faces = RequireFaces( "Extrude Individual" );

		Step( "Extrude Individual", () =>
		{
			var e = EditableMesh.FromPolyMesh( Mesh );
			foreach ( var f in faces )
				e.ExtrudeRegion( new[] { f }, Mesh.FaceNormal( Mesh.Faces[f] ) * distance );
			Mesh = e.ToPolyMesh();
		} );
	}

	/// <summary>Triangulate the selected faces (split into triangles).</summary>
	public void TriangulateFaces()
	{
		var faces = RequireFaces( "Triangulate" );

		Step( "Triangulate", () =>
		{
			var m = Mesh.Clone();
			var newFaces = new List<Face>( m.Faces.Count );
			var selectionSet = new HashSet<int>( faces );

			for ( var i = 0; i < m.Faces.Count; i++ )
			{
				var f = m.Faces[i];
				if ( selectionSet.Contains( i ) && f.Indices.Length > 3 )
				{
					var corners = new Vec3[f.Indices.Length];
					for ( var j = 0; j < f.Indices.Length; j++ ) corners[j] = m.Positions[f.Indices[j]];

					foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
					{
						newFaces.Add( new Face(
							new[] { f.Indices[ia], f.Indices[ib], f.Indices[ic] },
							new[] { f.UVs[ia], f.UVs[ib], f.UVs[ic] },
							f.Material ) );
					}
				}
				else
				{
					newFaces.Add( f );
				}
			}

			m.Faces = newFaces;
			Mesh = m;
		} );
	}

	/// <summary>Poke the selected faces (add a vertex in the center and triangulate).</summary>
	public void PokeFaces()
	{
		var faces = RequireFaces( "Poke Faces" );

		Step( "Poke Faces", () =>
		{
			var m = Mesh.Clone();
			var newFaces = new List<Face>( m.Faces.Count );
			var selectionSet = new HashSet<int>( faces );

			for ( var i = 0; i < m.Faces.Count; i++ )
			{
				var f = m.Faces[i];
				if ( selectionSet.Contains( i ) )
				{
					var center = Vec3.Zero;
					for ( var j = 0; j < f.Indices.Length; j++ )
						center += m.Positions[f.Indices[j]];
					center /= f.Indices.Length;

					var centerUV = Vec2.Zero;
					for ( var j = 0; j < f.Indices.Length; j++ )
						centerUV += f.UVs[j];
					centerUV /= f.Indices.Length;

					var centerIndex = m.Positions.Count;
					m.Positions.Add( center );

					if ( m.Skin is not null )
						m.Skin.Vertices.Add( (BoneWeight[])m.Skin.Vertices[f.Indices[0]].Clone() );

					if ( m.VertexColors is not null )
					{
						Array.Resize( ref m.VertexColors, m.Positions.Count );
						m.VertexColors[centerIndex] = m.VertexColors[f.Indices[0]];
					}

					for ( var j = 0; j < f.Indices.Length; j++ )
					{
						var next = (j + 1) % f.Indices.Length;
						newFaces.Add( new Face(
							new[] { f.Indices[j], f.Indices[next], centerIndex },
							new[] { f.UVs[j], f.UVs[next], centerUV },
							f.Material ) );
					}
				}
				else
				{
					newFaces.Add( f );
				}
			}

			m.Faces = newFaces;
			Mesh = m;
		} );
	}

	/// <summary>
	/// Join pairs of neighbouring selected triangles into quads, Blender's Tris to Quads (Alt+J).
	/// Two triangles pair when the angle between them is under <paramref name="maxFaceAngle"/> and the
	/// quad's corners stray from 90° by less than <paramref name="maxShapeAngle"/>, both in degrees.
	/// The best-looking pairs go first, so a triangulated grid comes back as the grid it was.
	/// </summary>
	public void TrisToQuads( float maxFaceAngle = 40f, float maxShapeAngle = 40f )
	{
		var faces = RequireFaces( "Tris to Quads" );

		Step( "Tris to Quads", () =>
		{
			var m = Mesh.Clone();
			var selected = new HashSet<int>();
			foreach ( var f in faces )
			{
				if ( m.Faces[f].Indices.Length == 3 )
					selected.Add( f );
			}

			var faceCos = MathF.Cos( maxFaceAngle * MathF.PI / 180f );
			var shapeLimit = maxShapeAngle * MathF.PI / 180f;

			// Every edge shared by two selected triangles of one material is a candidate, scored by
			// how flat the pair is and how square the quad would be. Lower is better.
			var candidates = new List<(float score, int a, int b, int[] quad, Vec2[] uvs)>();
			foreach ( var (key, owners) in m.BuildEdgeFaces() )
			{
				if ( owners.Count != 2 || !selected.Contains( owners[0] ) || !selected.Contains( owners[1] ) )
					continue;

				var fa = m.Faces[owners[0]];
				var fb = m.Faces[owners[1]];
				if ( fa.Material != fb.Material )
					continue;

				var na = m.FaceNormal( fa );
				var nb = m.FaceNormal( fb );
				var cos = Vec3.Dot( na, nb );
				if ( cos < faceCos )
					continue;

				// Walk A, and where its shared edge starts slip in B's far corner. B runs the edge
				// the other way, so the quad keeps A's winding.
				var quad = new int[4];
				var uvs = new Vec2[4];
				var n = 0;
				for ( var i = 0; i < 3; i++ )
				{
					var u = fa.Indices[i];
					var v = fa.Indices[(i + 1) % 3];
					quad[n] = u;
					uvs[n++] = fa.UVs[i];

					if ( !new EdgeKey( u, v ).Equals( key ) )
						continue;

					for ( var j = 0; j < 3; j++ )
					{
						var w = fb.Indices[j];
						if ( w == u || w == v )
							continue;

						quad[n] = w;
						uvs[n++] = fb.UVs[j];
					}
				}

				if ( n != 4 )
					continue;

				var worst = 0f;
				var convex = true;
				var normal = (na + nb).Normal;
				for ( var i = 0; i < 4; i++ )
				{
					var p = m.Positions[quad[i]];
					var before = (m.Positions[quad[(i + 3) % 4]] - p).Normal;
					var after = (m.Positions[quad[(i + 1) % 4]] - p).Normal;
					var angle = MathF.Acos( Math.Clamp( Vec3.Dot( before, after ), -1f, 1f ) );
					worst = MathF.Max( worst, MathF.Abs( angle - MathF.PI / 2f ) );

					// A reflex corner turns the wrong way round the quad's normal.
					if ( Vec3.Dot( Vec3.Cross( after, before ), normal ) < 0f )
						convex = false;
				}

				if ( !convex || worst > shapeLimit )
					continue;

				candidates.Add( (worst + (1f - cos), owners[0], owners[1], quad, uvs) );
			}

			candidates.Sort( ( x, y ) => x.score.CompareTo( y.score ) );

			var consumed = new HashSet<int>();
			var quads = new Dictionary<int, (int[] quad, Vec2[] uvs)>();
			foreach ( var c in candidates )
			{
				if ( consumed.Contains( c.a ) || consumed.Contains( c.b ) )
					continue;

				consumed.Add( c.a );
				consumed.Add( c.b );
				quads[Math.Min( c.a, c.b )] = (c.quad, c.uvs);
			}

			if ( quads.Count == 0 )
				throw new InvalidOperationException( "No two selected triangles could join into a quad. Select neighbouring triangles that lie roughly flat against each other." );

			var newFaces = new List<Face>( m.Faces.Count );
			var newSelection = new HashSet<int>();
			for ( var i = 0; i < m.Faces.Count; i++ )
			{
				if ( quads.TryGetValue( i, out var q ) )
				{
					newSelection.Add( newFaces.Count );
					newFaces.Add( new Face( q.quad, q.uvs, m.Faces[i].Material ) );
				}
				else if ( !consumed.Contains( i ) )
				{
					if ( SelectedFaces.Contains( i ) )
						newSelection.Add( newFaces.Count );
					newFaces.Add( m.Faces[i] );
				}
			}

			m.Faces = newFaces;
			Mesh = m;
			ClearSelection();
			SelectedFaces.UnionWith( newSelection );
		} );
	}

	/// <summary>
	/// Cut the corner off each selected vertex, Blender's Bevel Vertices (Ctrl+Shift+B). Every edge
	/// into the vertex is shortened by <paramref name="width"/> and the notch is capped with one
	/// flat face. Vertices on an open boundary are left alone. The caps become the selection.
	/// </summary>
	public void BevelVertices( float width )
	{
		var verts = AffectedVertices();
		if ( verts.Count == 0 )
			throw new InvalidOperationException( "Bevel Vertices needs vertices selected. Switch to Vertex mode (1) and pick the corners to cut." );

		if ( width <= 0f )
			throw new InvalidOperationException( "Bevel Vertices needs a width above zero." );

		Step( "Bevel Vertices", () =>
		{
			var m = Mesh.Clone();
			var edgeFaces = m.BuildEdgeFaces();
			var normals = m.ComputeVertexNormals();

			// Every edge leaving each vertex, its material, and whether the vertex sits on a boundary.
			var spokes = new Dictionary<int, HashSet<int>>();
			var materials = new Dictionary<int, int>();
			var boundary = new HashSet<int>();
			foreach ( var (key, owners) in edgeFaces )
			{
				foreach ( var v in new[] { key.A, key.B } )
				{
					if ( !verts.Contains( v ) )
						continue;

					if ( !spokes.TryGetValue( v, out var set ) )
						spokes[v] = set = new HashSet<int>();
					set.Add( key.A == v ? key.B : key.A );
					materials[v] = m.Faces[owners[0]].Material;

					if ( owners.Count != 2 )
						boundary.Add( v );
				}
			}

			// One new vertex per spoke, pulled along it. Never past the middle, or two bevelled ends
			// of one edge would cross.
			var cuts = new Dictionary<(int v, int w), int>();
			var cutT = new Dictionary<(int v, int w), float>();
			var capped = new List<int>();
			foreach ( var (v, others) in spokes )
			{
				if ( boundary.Contains( v ) || others.Count < 3 )
					continue;

				capped.Add( v );
				var p = m.Positions[v];
				foreach ( var w in others )
				{
					var len = (m.Positions[w] - p).Length;
					if ( len < 1e-6f )
						continue;

					var t = MathF.Min( width / len, 0.49f );
					cuts[(v, w)] = m.Positions.Count;
					cutT[(v, w)] = t;
					m.Positions.Add( Vec3.Lerp( p, m.Positions[w], t ) );
					m.Skin?.Vertices.Add( (BoneWeight[])m.Skin.Vertices[v].Clone() );
				}
			}

			if ( capped.Count == 0 )
				throw new InvalidOperationException( "None of the selected vertices can be bevelled: each needs three or more edges and no open boundary." );

			if ( m.VertexColors is not null )
			{
				var was = m.VertexColors.Length;
				Array.Resize( ref m.VertexColors, m.Positions.Count );
				foreach ( var ((v, _), i) in cuts )
				{
					if ( i >= was )
						m.VertexColors[i] = m.VertexColors[v];
				}
			}

			// Each face round a cut vertex trades that corner for the two cut points on its edges.
			var newFaces = new List<Face>( m.Faces.Count + capped.Count );
			foreach ( var f in m.Faces )
			{
				var idx = new List<int>( f.Indices.Length + 2 );
				var uvs = new List<Vec2>( f.Indices.Length + 2 );
				var count = f.Indices.Length;
				for ( var i = 0; i < count; i++ )
				{
					var v = f.Indices[i];
					var prevAt = (i + count - 1) % count;
					var nextAt = (i + 1) % count;

					if ( cuts.TryGetValue( (v, f.Indices[prevAt]), out var a ) && cuts.TryGetValue( (v, f.Indices[nextAt]), out var b ) )
					{
						idx.Add( a );
						uvs.Add( f.UVs[i] + (f.UVs[prevAt] - f.UVs[i]) * cutT[(v, f.Indices[prevAt])] );
						idx.Add( b );
						uvs.Add( f.UVs[i] + (f.UVs[nextAt] - f.UVs[i]) * cutT[(v, f.Indices[nextAt])] );
					}
					else
					{
						idx.Add( v );
						uvs.Add( f.UVs[i] );
					}
				}

				newFaces.Add( new Face( idx.ToArray(), uvs.ToArray(), f.Material ) );
			}

			// The cap: the cut points in order round the vertex normal, wound to face outward.
			var capFaces = new List<int>();
			foreach ( var v in capped )
			{
				var n = normals[v];
				var p = m.Positions[v];
				var ring = new List<int>();
				foreach ( var w in spokes[v] )
				{
					if ( cuts.TryGetValue( (v, w), out var i ) )
						ring.Add( i );
				}

				if ( ring.Count < 3 )
					continue;

				var refDir = m.Positions[ring[0]] - p;
				refDir -= n * Vec3.Dot( refDir, n );
				refDir = refDir.Normal;
				var side = Vec3.Cross( n, refDir );

				ring.Sort( ( x, y ) =>
				{
					var dx = m.Positions[x] - p;
					var dy = m.Positions[y] - p;
					return MathF.Atan2( Vec3.Dot( dx, side ), Vec3.Dot( dx, refDir ) )
						.CompareTo( MathF.Atan2( Vec3.Dot( dy, side ), Vec3.Dot( dy, refDir ) ) );
				} );

				var capUVs = new Vec2[ring.Count];
				capFaces.Add( newFaces.Count );
				newFaces.Add( new Face( ring.ToArray(), capUVs, materials[v] ) );
			}

			m.Faces = newFaces;

			// Compaction only drops the bevelled vertices, so every face keeps its index.
			Mesh = RemoveUnusedVertices( m );
			ClearSelection();
			if ( Mode == EditElement.Vertex )
			{
				foreach ( var f in capFaces )
					SelectedVertices.UnionWith( Mesh.Faces[f].Indices );
			}
			else
			{
				SelectedFaces.UnionWith( capFaces );
			}
		} );
	}

	/// <summary>Inset the selected faces as one region. The inner faces stay selected.</summary>
	public void Inset( float distance )
	{
		var faces = RequireFaces( "Inset" );

		Step( "Inset", () =>
		{
			var e = EditableMesh.FromPolyMesh( Mesh );
			e.InsetFaces( faces, distance );
			Mesh = e.ToPolyMesh();
		} );
	}

	/// <summary>
	/// Where a loop cut across <paramref name="edge"/> would land, for a hover preview. Returns null
	/// rather than throwing when the ring is not cuttable — a non-quad face, an open rim, an edge
	/// that is gone — because a preview runs on every mouse move and a refusal there is an ordinary
	/// answer, not an error. <see cref="LoopCut"/> still throws, so the click that commits still
	/// says why.
	/// </summary>
	public List<Vec3> LoopCutPreview( EdgeKey edge, float fraction )
	{
		try
		{
			// Held between frames: a hover preview runs on every mouse move, and rebuilding the
			// half-edge mesh each time is the same cost the overlay's edge cache exists to avoid.
			// Keyed on Revision, not TopologyRevision, because the preview reads POSITIONS out of
			// the cached mesh and a drag moves those without changing the topology.
			if ( _previewMesh is null || _previewMeshRevision != Revision )
			{
				_previewMesh = EditableMesh.FromPolyMesh( Mesh );
				_previewMeshRevision = Revision;
			}

			var he = FindHalfEdge( _previewMesh, edge.A, edge.B );

			return he < 0 ? null : _previewMesh.LoopCutPoints( he, Math.Clamp( fraction, 0.02f, 0.98f ) );
		}
		catch ( Exception ex ) when ( ex is InvalidOperationException or ArgumentException )
		{
			return null;
		}
	}

	EditableMesh _previewMesh;
	int _previewMeshRevision = -1;

	/// <summary>Cut a loop across the ring through <paramref name="edge"/>. The new loop becomes the
	/// selection, in Edge mode, so it can be slid or scaled straight away.</summary>
	public void LoopCut( EdgeKey edge, float fraction )
	{
		Step( "Loop cut", () =>
		{
			var before = Mesh.VertexCount;
			var e = EditableMesh.FromPolyMesh( Mesh );
			var he = FindHalfEdge( e, edge.A, edge.B );

			if ( he < 0 )
				throw new InvalidOperationException( "That edge is not part of this mesh any more." );

			e.LoopCut( he, fraction );
			Mesh = e.ToPolyMesh();

			SelectedVertices.Clear();
			SelectedEdges.Clear();
			SelectedFaces.Clear();
			Mode = EditElement.Edge;

			foreach ( var key in Mesh.BuildEdgeFaces().Keys )
			{
				if ( key.A >= before && key.B >= before )
					SelectedEdges.Add( key );
			}

			SelectionRevision++;
		} );
	}

	/// <summary>Delete what is selected: faces in Face mode, and every face touching a selected
	/// vertex or edge otherwise. Vertices nothing uses any more are removed too.</summary>
	public void Delete()
	{
		var doomed = new HashSet<int>( SelectedFaces );

		for ( var f = 0; f < Mesh.FaceCount; f++ )
		{
			var idx = Mesh.Faces[f].Indices;

			for ( var i = 0; i < idx.Length; i++ )
			{
				if ( SelectedVertices.Contains( idx[i] ) || SelectedEdges.Contains( new EdgeKey( idx[i], idx[(i + 1) % idx.Length] ) ) )
				{
					doomed.Add( f );
					break;
				}
			}
		}

		if ( doomed.Count == 0 )
			throw new InvalidOperationException( "Nothing is selected to delete." );

		Step( "Delete", () =>
		{
			var e = EditableMesh.FromPolyMesh( Mesh );
			e.DeleteFaces( doomed );
			Mesh = RemoveUnusedVertices( e.ToPolyMesh() );
			ClearSelection();
		} );
	}

	/// <summary>Dissolve the selected edges (Edge mode) or vertices (Vertex mode) — they go, and the
	/// faces around them merge into one, unlike Delete, which leaves a hole.</summary>
	public void Dissolve()
	{
		if ( Mode == EditElement.Face )
			throw new InvalidOperationException( "Dissolve works on edges or vertices. Switch to Edge or Vertex mode." );

		if ( SelectedEdges.Count == 0 && SelectedVertices.Count == 0 )
			throw new InvalidOperationException( "Nothing is selected to dissolve." );

		Step( "Dissolve", () =>
		{
			var e = EditableMesh.FromPolyMesh( Mesh );

			if ( SelectedEdges.Count > 0 )
			{
				var hes = new List<int>();
				foreach ( var key in SelectedEdges )
				{
					var he = FindHalfEdge( e, key.A, key.B );
					if ( he < 0 )
						he = FindHalfEdge( e, key.B, key.A );
					if ( he >= 0 )
						hes.Add( he );
				}

				e.DissolveEdges( hes );
			}
			else
			{
				// One at a time, highest index first is irrelevant here — DissolveVertex leaves the
				// vertex list alone — but the half-edges are re-derived each call, so it must be a
				// fresh lookup per vertex, never a list built up front.
				foreach ( var v in SelectedVertices )
					e.DissolveVertex( v );
			}

			Mesh = RemoveUnusedVertices( e.ToPolyMesh() );
			ClearSelection();
		} );
	}

	/// <summary>Merge the selected vertices to their centre, Blender's Merge at Center. Faces the
	/// merge squeezes flat are dropped.</summary>
	public void MergeAtCentre()
	{
		var verts = AffectedVertices();

		if ( verts.Count < 2 )
			throw new InvalidOperationException( "Merging needs two or more vertices selected." );

		Step( "Merge", () =>
		{
			var centre = SelectionCentre();
			var keep = int.MaxValue;
			foreach ( var v in verts )
				keep = Math.Min( keep, v );

			Mesh.Positions[keep] = centre;
			var result = new PolyMesh { Positions = new List<Vec3>( Mesh.Positions ), Skin = Mesh.Skin?.Clone(), VertexColors = (Vec4[])Mesh.VertexColors?.Clone() };

			foreach ( var face in Mesh.Faces )
			{
				var idx = new List<int>();
				var uvs = new List<Vec2>();

				for ( var i = 0; i < face.Indices.Length; i++ )
				{
					var v = verts.Contains( face.Indices[i] ) ? keep : face.Indices[i];
					if ( idx.Count > 0 && idx[^1] == v )
						continue;
					idx.Add( v );
					uvs.Add( face.UVs[i] );
				}

				if ( idx.Count > 1 && idx[0] == idx[^1] )
				{
					idx.RemoveAt( idx.Count - 1 );
					uvs.RemoveAt( uvs.Count - 1 );
				}

				if ( idx.Count >= 3 )
					result.AddFace( idx.ToArray(), uvs.ToArray(), face.Material );
			}

			Mesh = RemoveUnusedVertices( result );
			ClearSelection();
		} );
	}

	/// <summary>Merge vertices closer than <paramref name="tolerance"/> anywhere in the mesh.</summary>
	public void MergeByDistance( float tolerance )
	{
		Step( "Merge by distance", () =>
		{
			Mesh = RemoveUnusedVertices( MeshWeld.Weld( Mesh, tolerance ) );
			ClearSelection();
		} );
	}

	/// <summary>Give the whole mesh thickness — the last step of a garment.</summary>
	public void Solidify( float thickness )
	{
		Step( "Solidify", () =>
		{
			Mesh = MeshSolidify.Solidify( Mesh, thickness );
			ClearSelection();
		} );
	}

	/// <summary>Wrap the selection (or the whole mesh, if nothing is selected) onto
	/// <see cref="SnapTarget"/>, keeping <paramref name="offset"/> off it.</summary>
	public void Shrinkwrap( float offset, float maxDistance )
	{
		if ( _snapTarget is null )
			throw new InvalidOperationException( "Pick a body to wrap onto first." );

		var verts = AffectedVertices();
		float[] weights = null;

		if ( verts.Count > 0 )
		{
			weights = new float[Mesh.VertexCount];
			foreach ( var v in verts )
				weights[v] = 1f;
		}

		Step( "Shrinkwrap", () =>
		{
			Mesh = MeshShrinkwrap.Apply( Mesh, _snapTarget, maxDistance, offset, out _, weights );
		} );
	}

	// --- dragging ----------------------------------------------------------------------------

	int[] _dragVerts;
	Vec3[] _dragFrom;
	int[] _dragMirror;
	float[] _dragWeight;

	public bool IsDragging => _dragVerts is not null;

	/// <summary>Start moving the selection. Snapshots for one undo step.</summary>
	public bool BeginDrag()
	{
		var verts = AffectedVertices();

		if ( verts.Count == 0 )
			return false;

		Push( "Move" );

		var list = new List<int>( verts );
		var weights = new List<float>();
		foreach ( var _ in verts )
			weights.Add( 1f );

		// Soft falloff: every other vertex near the selection joins with a weight.
		if ( SoftRadius > 0f )
		{
			foreach ( var (i, d) in SoftConnected ? ConnectedDistances( verts, SoftRadius ) : StraightDistances( verts, SoftRadius ) )
			{
				list.Add( i );
				weights.Add( SoftWeight( 1f - d / SoftRadius ) );
			}
		}

		_dragVerts = list.ToArray();
		_dragWeight = weights.ToArray();
		_dragFrom = new Vec3[_dragVerts.Length];
		_dragMirror = new int[_dragVerts.Length];

		for ( var i = 0; i < _dragVerts.Length; i++ )
		{
			_dragFrom[i] = Mesh.Positions[_dragVerts[i]];
			_dragMirror[i] = MirrorX ? MirrorPartner( _dragVerts[i] ) : -1;
		}

		return true;
	}

	/// <summary>Move the dragged vertices to where they started plus <paramref name="offset"/>.
	/// Absolute, not accumulated, so a drag never drifts.</summary>
	public void Drag( Vec3 offset )
	{
		if ( _dragVerts is null )
			return;

		for ( var i = 0; i < _dragVerts.Length; i++ )
			PlaceDragged( i, _dragFrom[i] + offset );

		Revision++;
	}

	/// <summary>Put dragged vertex <paramref name="i"/> at <paramref name="to"/>, keeping it on the
	/// mirror plane if it started there, snapping it, and moving its mirror partner.</summary>
	void PlaceDragged( int i, Vec3 to )
	{
		{
			var from = _dragFrom[i];
			var weight = _dragWeight[i];

			if ( weight < 1f )
				to = Vec3.Lerp( from, to, weight );
			var onPlane = MirrorX && (MathF.Abs( from.x ) <= MirrorTolerance || MathF.Abs( to.x ) <= MirrorTolerance || (from.x > 0f && to.x < 0f) || (from.x < 0f && to.x > 0f));

			if ( onPlane )
				to = new Vec3( 0f, to.y, to.z );

			if ( _snapTree is not null && weight >= 1f && !ReferenceEquals( _snapTarget, Mesh ) )
			{
				var hit = _snapTree.NearestSurface( _snapTarget, to, SnapMaxDistance );
				if ( hit is { } h )
					to = h.Point + h.Normal * SnapOffset;
			}

			Mesh.Positions[_dragVerts[i]] = to;

			var partner = _dragMirror[i];
			if ( partner >= 0 && Array.IndexOf( _dragVerts, partner ) < 0 )
				Mesh.Positions[partner] = new Vec3( -to.x, to.y, to.z );
		}
	}

	/// <summary>Finish the drag. False cancels it — every vertex goes back and the step is dropped.</summary>
	public void EndDrag( bool keep = true )
	{
		if ( _dragVerts is null )
			return;

		if ( !keep )
		{
			Restore( _undo[^1] );
			_undo.RemoveAt( _undo.Count - 1 );
		}
		else
		{
			Changed();
		}

		_dragVerts = null;
		_dragFrom = null;
		_dragMirror = null;
	}

	/// <summary>The vertex mirrored across x = 0, or -1. Vertices on the plane are their own
	/// partner and report -1 — they are handled by clamping instead.</summary>
	public int MirrorPartner( int vertex )
	{
		var p = Mesh.Positions[vertex];

		if ( MathF.Abs( p.x ) <= MirrorTolerance )
			return -1;

		var want = new Vec3( -p.x, p.y, p.z );
		var best = -1;
		var bestDistance = MirrorTolerance;

		for ( var i = 0; i < Mesh.VertexCount; i++ )
		{
			var d = (Mesh.Positions[i] - want).Length;
			if ( d <= bestDistance )
			{
				bestDistance = d;
				best = i;
			}
		}

		return best;
	}

	/// <summary>Hand the result to the feature. False (and nothing committed) when nothing changed.</summary>
	public bool CommitTo( MeshEditFeature feature )
	{
		if ( feature is null )
			throw new ArgumentNullException( nameof( feature ) );

		if ( _preview is not null )
			Accept();

		if ( _dragVerts is not null )
			EndDrag();

		if ( !HasChanges )
			return false;

		feature.Commit( Mesh, Base, Separated );
		return true;
	}

	// --- bevel, slide, rotate and scale ---------------------------------------------------------

	/// <summary>
	/// Round off or cut the selected edges (in Face mode, the edges around the selected faces).
	/// One segment is a chamfer, more is a fillet. Selection is cleared: the edges are gone.
	/// </summary>
	public void Bevel( float width, int segments )
	{
		var edges = new HashSet<EdgeKey>( SelectedEdges );

		if ( Mode == EditElement.Face )
		{
			foreach ( var (key, faces) in Mesh.BuildEdgeFaces() )
			{
				var inside = 0;
				foreach ( var f in faces )
				{
					if ( SelectedFaces.Contains( f ) )
						inside++;
				}

				// A boundary of the selection: one side selected, one not.
				if ( inside == 1 && faces.Count == 2 )
					edges.Add( key );
			}
		}

		if ( edges.Count == 0 )
			throw new InvalidOperationException( "Bevel needs edges selected. Switch to Edge mode (2) and pick the edges to round off." );

		Step( "Bevel", () =>
		{
			var report = segments <= 1
				? EdgeBlend.ChamferReport( Mesh, width, edges )
				: EdgeBlend.FilletReport( Mesh, width, segments, edges );

			if ( report.Failure is { } failure )
				throw new InvalidOperationException( failure.Cause is null ? failure.Problem : $"{failure.Problem}. {failure.Cause}" );

			Mesh = report.Mesh;
			ClearSelection();
		} );
	}

	/// <summary>
	/// Slide the selected edges along the edges running across them, Blender's Edge Slide.
	/// <paramref name="amount"/> is -1..1: the fraction of the way to the neighbouring loop on one
	/// side (positive) or the other (negative). Topology is unchanged — only positions move — so the
	/// selection survives.
	/// </summary>
	public void EdgeSlide( float amount )
	{
		if ( SelectedEdges.Count == 0 )
			throw new InvalidOperationException( "Edge slide needs an edge loop selected. Alt+click an edge in Edge mode (2)." );

		amount = Math.Clamp( amount, -1f, 1f );

		var rails = SlideRails( amount >= 0f );

		if ( rails.Count == 0 )
			throw new InvalidOperationException( "These edges have no faces beside them to slide along." );

		var t = MathF.Abs( amount );

		Step( "Edge slide", () =>
		{
			var from = new Dictionary<int, Vec3>();
			foreach ( var v in rails.Keys )
				from[v] = Mesh.Positions[v];

			foreach ( var (v, towards) in rails )
				Mesh.Positions[v] = Vec3.Lerp( from[v], Mesh.Positions[towards], t );
		} );
	}

	/// <summary>
	/// Slide each selected vertex along one of the edges leaving it, Blender's Vertex Slide.
	/// <paramref name="amount"/> is -1..1: the fraction of the way to the chosen neighbour, or to the
	/// opposite one when negative. Only positions move, so the selection survives.
	///
	/// WHICH EDGE. Blender resolves this against live mouse movement; there is no mouse in the
	/// kernel, so the rail is the edge leaving the vertex that lies closest to
	/// <paramref name="direction"/> — the editor passes the way the view is facing, so the edge that
	/// runs across the screen is the one that slides. Every selected vertex is resolved separately,
	/// so a row of them slides the same way across the model rather than fanning out.
	/// </summary>
	public void VertexSlide( float amount, Vec3 direction )
	{
		var moving = AffectedVertices();

		if ( moving.Count == 0 )
			throw new InvalidOperationException( "Vertex slide needs vertices selected. Switch to Vertex mode (1) and pick the ones to slide." );

		if ( direction.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "Vertex slide needs a direction to choose an edge along." );

		amount = Math.Clamp( amount, -1f, 1f );
		direction = direction / direction.Length;

		var neighbours = VertexNeighbours();
		var rails = new Dictionary<int, int>();

		foreach ( var v in moving )
		{
			if ( !neighbours.TryGetValue( v, out var around ) || around.Count == 0 )
				continue;

			var best = -1;
			var bestScore = float.NegativeInfinity;

			foreach ( var n in around )
			{
				var along = Mesh.Positions[n] - Mesh.Positions[v];

				if ( along.LengthSquared < 1e-12f )
					continue;

				// Signed, so amount's sign picks between the two ends of a run of edges: the best
				// match forward and the best match backward are the two rails a vertex has.
				var score = Vec3.Dot( along / along.Length, direction ) * MathF.Sign( amount == 0f ? 1f : amount );

				// Ties go to the lower index, so the same model always slides the same way.
				if ( score > bestScore || (score == bestScore && best >= 0 && n < best) )
				{
					bestScore = score;
					best = n;
				}
			}

			if ( best >= 0 )
				rails[v] = best;
		}

		if ( rails.Count == 0 )
			throw new InvalidOperationException( "These vertices have no edges to slide along." );

		var t = MathF.Abs( amount );

		Step( "Vertex slide", () =>
		{
			var from = new Dictionary<int, Vec3>();
			foreach ( var v in rails.Keys )
				from[v] = Mesh.Positions[v];

			// Read every start position first: two selected vertices can be each other's rail, and
			// moving one before reading the other would slide the second towards a moved target.
			foreach ( var (v, towards) in rails )
			{
				var target = from.TryGetValue( towards, out var held ) ? held : Mesh.Positions[towards];
				Mesh.Positions[v] = Vec3.Lerp( from[v], target, t );
			}
		} );
	}

	/// <summary>Every vertex's neighbours along the mesh's edges.</summary>
	Dictionary<int, List<int>> VertexNeighbours()
	{
		var result = new Dictionary<int, List<int>>();

		void Link( int a, int b )
		{
			if ( !result.TryGetValue( a, out var list ) )
				result[a] = list = new List<int>();

			if ( !list.Contains( b ) )
				list.Add( b );
		}

		foreach ( var face in Mesh.Faces )
		{
			var idx = face.Indices;

			for ( var i = 0; i < idx.Length; i++ )
			{
				var a = idx[i];
				var b = idx[(i + 1) % idx.Length];
				Link( a, b );
				Link( b, a );
			}
		}

		return result;
	}

	/// <summary>
	/// For every vertex on the selected edges, the neighbour it slides towards on one side.
	///
	/// A side is a face's winding: for a selected edge a→b, the face that runs a→b is the LEFT face,
	/// and in it a's rail is the corner before a and b's is the corner after b. Choosing by winding
	/// rather than by position keeps the side consistent all the way round a loop, however it turns.
	/// </summary>
	Dictionary<int, int> SlideRails( bool left )
	{
		var rails = new Dictionary<int, int>();
		var directed = OrientSelectedEdges();

		foreach ( var face in Mesh.Faces )
		{
			var idx = face.Indices;
			var n = idx.Length;

			for ( var i = 0; i < n; i++ )
			{
				var a = idx[i];
				var b = idx[(i + 1) % n];

				if ( !SelectedEdges.Contains( new EdgeKey( a, b ) ) )
					continue;

				var before = idx[(i + n - 1) % n];
				var after = idx[(i + 2) % n];

				// Left of the loop's own walking direction, not of the sorted EdgeKey — the keys flip
				// wherever vertex numbering runs backwards along the loop.
				var forward = directed.Contains( (a, b) );

				if ( forward == left )
				{
					if ( !SelectedEdgeTouches( before ) )
						rails.TryAdd( a, before );
					if ( !SelectedEdgeTouches( after ) )
						rails.TryAdd( b, after );
				}
			}
		}

		return rails;
	}

	/// <summary>Walk every chain of selected edges and give each edge the direction of its walk, so
	/// "left" means the same side all the way along.</summary>
	HashSet<(int, int)> OrientSelectedEdges()
	{
		var neighbours = new Dictionary<int, List<int>>();

		foreach ( var e in SelectedEdges )
		{
			if ( !neighbours.TryGetValue( e.A, out var na ) )
				neighbours[e.A] = na = new List<int>();
			if ( !neighbours.TryGetValue( e.B, out var nb ) )
				neighbours[e.B] = nb = new List<int>();
			na.Add( e.B );
			nb.Add( e.A );
		}

		var directed = new HashSet<(int, int)>();
		var done = new HashSet<EdgeKey>();

		// Start open chains at an end so the walk covers them in one pass; closed loops anywhere.
		var starts = new List<int>();
		foreach ( var (v, n) in neighbours )
			if ( n.Count == 1 )
				starts.Add( v );
		starts.AddRange( neighbours.Keys );

		foreach ( var start in starts )
		{
			var at = start;

			while ( true )
			{
				var moved = false;

				foreach ( var next in neighbours[at] )
				{
					var key = new EdgeKey( at, next );
					if ( !done.Add( key ) )
						continue;

					directed.Add( (at, next) );
					at = next;
					moved = true;
					break;
				}

				if ( !moved )
					break;
			}
		}

		return directed;
	}

	bool SelectedEdgeTouches( int vertex )
	{
		foreach ( var e in SelectedEdges )
		{
			if ( e.A == vertex || e.B == vertex )
				return true;
		}

		return false;
	}

	/// <summary>Drag with any transform of the starting positions — what the rotate and scale
	/// handles use. Same rules as <see cref="Drag"/>: absolute, mirror and snap applied.</summary>
	public void DragTransform( Func<Vec3, Vec3> transform )
	{
		if ( _dragVerts is null || transform is null )
			return;

		for ( var i = 0; i < _dragVerts.Length; i++ )
			PlaceDragged( i, transform( _dragFrom[i] ) );

		Revision++;
	}

	/// <summary>Rotate the dragged vertices about <paramref name="pivot"/>.</summary>
	public void DragRotate( Vec3 pivot, Vec3 axis, float degrees )
	{
		var n = axis.Normal;
		var r = degrees * MathF.PI / 180f;
		var c = MathF.Cos( r );
		var s = MathF.Sin( r );

		DragTransform( p =>
		{
			// Rodrigues: v cosθ + (k×v) sinθ + k(k·v)(1−cosθ).
			var v = p - pivot;
			return pivot + v * c + Vec3.Cross( n, v ) * s + n * (Vec3.Dot( n, v ) * (1f - c));
		} );
	}

	/// <summary>Scale the dragged vertices about <paramref name="pivot"/>, per axis.</summary>
	public void DragScale( Vec3 pivot, Vec3 factor ) =>
		DragTransform( p => pivot + new Vec3( (p.x - pivot.x) * factor.x, (p.y - pivot.y) * factor.y, (p.z - pivot.z) * factor.z ) );

	// --- knife and bisect -------------------------------------------------------------------------

	/// <summary>
	/// Cut the mesh with a plane, Blender's Bisect: every face the plane crosses is split along it,
	/// and the new edges are selected. Only the selected faces are cut when there are any.
	/// </summary>
	public void Bisect( Vec3 point, Vec3 normal )
	{
		var faces = SelectedFaces.Count > 0 ? new HashSet<int>( SelectedFaces ) : null;
		Step( "Bisect", () => CutPlane( point, normal.Normal, faces, null ) );
	}

	/// <summary>
	/// Knife: cut along the line from <paramref name="a"/> to <paramref name="b"/> as seen along
	/// <paramref name="view"/>, only as far as the stroke reaches. Only faces turned towards the
	/// viewer are cut unless <paramref name="throughAll"/> — X-ray — as in Blender, where a knife
	/// stroke on the front of a head does not also slice the back of it.
	/// </summary>
	public void Knife( Vec3 a, Vec3 b, Vec3 view, bool throughAll = false )
	{
		var along = b - a;

		if ( along.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "The knife line has no length. Click two different points." );

		var normal = Vec3.Cross( along, view );

		if ( normal.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "The knife line points straight into the screen. Turn the view and cut again." );

		// Within the stroke: the part of the plane between the two ends, measured along the line.
		var len2 = along.LengthSquared;
		bool Within( Vec3 p )
		{
			var t = Vec3.Dot( p - a, along ) / len2;
			return t >= -1e-4f && t <= 1f + 1e-4f;
		}

		HashSet<int> facing = null;

		if ( !throughAll )
		{
			facing = new HashSet<int>();
			for ( var f = 0; f < Mesh.FaceCount; f++ )
				if ( Vec3.Dot( Mesh.FaceNormal( Mesh.Faces[f] ), view ) < 0f )
					facing.Add( f );
		}

		Step( "Knife", () => CutPlane( a, normal.Normal, facing, Within ) );
	}

	/// <summary>
	/// The one cutting routine both tools share. Classify vertices against the plane, put a new
	/// vertex on every crossed edge (once, shared by both faces on it so the mesh stays welded),
	/// thread it into every face that uses that edge, then split each cut face between its two cut
	/// points. Faces the plane meets at a single point, or along one of their own edges, are left
	/// alone — there is nothing to split.
	/// </summary>
	void CutPlane( Vec3 point, Vec3 normal, HashSet<int> onlyFaces, Func<Vec3, bool> within )
	{
		var mesh = Mesh;
		var eps = MathF.Max( mesh.BoundsDiagonal, 1e-3f ) * 1e-5f;
		var side = new float[mesh.VertexCount];

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var d = Vec3.Dot( mesh.Positions[i] - point, normal );
			side[i] = MathF.Abs( d ) <= eps ? 0f : d;
		}

		var positions = new List<Vec3>( mesh.Positions );
		var skin = mesh.IsRigged ? new List<BoneWeight[]>( mesh.Skin.Vertices ) : null;
		var colours = mesh.HasVertexColors ? new List<Vec4>( mesh.VertexColors ) : null;
		var splits = new Dictionary<EdgeKey, (int Vertex, int From, float T)>();

		bool Cuttable( int f ) => onlyFaces is null || onlyFaces.Contains( f );

		// 1. A vertex on every crossed edge of a face being cut.
		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			if ( !Cuttable( f ) )
				continue;

			var idx = mesh.Faces[f].Indices;

			for ( var i = 0; i < idx.Length; i++ )
			{
				var u = idx[i];
				var w = idx[(i + 1) % idx.Length];

				if ( side[u] * side[w] >= 0f )
					continue;

				var key = new EdgeKey( u, w );
				if ( splits.ContainsKey( key ) )
					continue;

				var t = side[u] / (side[u] - side[w]);
				var p = Vec3.Lerp( mesh.Positions[u], mesh.Positions[w], t );

				if ( within is not null && !within( p ) )
					continue;

				splits[key] = (positions.Count, u, t);
				positions.Add( p );
				skin?.Add( t < 0.5f ? mesh.Skin[u] : mesh.Skin[w] );
				colours?.Add( Vec4Lerp( mesh.VertexColors[u], mesh.VertexColors[w], t ) );
			}
		}

		if ( splits.Count == 0 )
			throw new InvalidOperationException( "The cut does not cross the model. Draw it across the faces you want to split." );

		var result = new PolyMesh { Positions = positions, Paint = mesh.Paint?.Clone() };
		if ( skin is not null )
			result.Skin = new SkinWeights { Vertices = skin };
		if ( colours is not null )
			result.VertexColors = colours.ToArray();

		var newEdges = new List<EdgeKey>();

		// 2. Thread the new vertices into every face (cut or not — a neighbour that is not cut still
		//    shares the edge and must carry the vertex to stay welded), then 3. split the cut faces.
		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			var face = mesh.Faces[f];
			var idx = face.Indices;
			var n = idx.Length;
			var outIdx = new List<int>( n + 2 );
			var outUv = new List<Vec2>( n + 2 );
			var cuts = new List<int>();

			for ( var i = 0; i < n; i++ )
			{
				var u = idx[i];
				var w = idx[(i + 1) % n];

				outIdx.Add( u );
				outUv.Add( face.UVs[i] );

				if ( side[u] == 0f && Cuttable( f ) && (within is null || within( mesh.Positions[u] )) )
					cuts.Add( outIdx.Count - 1 );

				if ( splits.TryGetValue( new EdgeKey( u, w ), out var s ) )
				{
					// T was measured from s.From; this face may walk the edge the other way.
					var t = s.From == u ? s.T : 1f - s.T;
					outIdx.Add( s.Vertex );
					outUv.Add( new Vec2( face.UVs[i].x + (face.UVs[(i + 1) % n].x - face.UVs[i].x) * t, face.UVs[i].y + (face.UVs[(i + 1) % n].y - face.UVs[i].y) * t ) );

					if ( Cuttable( f ) )
						cuts.Add( outIdx.Count - 1 );
				}
			}

			var m = outIdx.Count;

			if ( cuts.Count == 2 && Cuttable( f ) )
			{
				var c0 = cuts[0];
				var c1 = cuts[1];
				var adjacent = (c1 - c0 + m) % m == 1 || (c0 - c1 + m) % m == 1;

				if ( !adjacent )
				{
					result.AddFace( Arc( outIdx, c0, c1 ), Arc( outUv, c0, c1 ), face.Material );
					result.AddFace( Arc( outIdx, c1, c0 ), Arc( outUv, c1, c0 ), face.Material );
					newEdges.Add( new EdgeKey( outIdx[c0], outIdx[c1] ) );
					continue;
				}
			}

			result.AddFace( outIdx.ToArray(), outUv.ToArray(), face.Material );
		}

		Mesh = result;
		ClearSelection();
		Mode = EditElement.Edge;
		SelectedEdges.UnionWith( newEdges );
		SelectionRevision++;
	}

	/// <summary>Corners from <paramref name="from"/> round to <paramref name="to"/>, both included.</summary>
	static T[] Arc<T>( List<T> ring, int from, int to )
	{
		var result = new List<T>();
		var n = ring.Count;

		for ( var i = from; ; i = (i + 1) % n )
		{
			result.Add( ring[i] );
			if ( i == to )
				break;
		}

		return result.ToArray();
	}

	static Vec4 Vec4Lerp( Vec4 a, Vec4 b, float t ) =>
		new( a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t );

	// --- proportional editing ---------------------------------------------------------------------

	/// <summary>
	/// Soft falloff for moves, rotates and scales: vertices within this distance of the selection
	/// come along too, less the further away they are (smoothstep). Zero is off. Blender's
	/// Proportional Editing, and what makes a dragged vertex pull a curve rather than a spike.
	/// </summary>
	public float SoftRadius { get; set; }

	/// <summary>The shape of the soft falloff's curve, Blender's falloff types. Smooth by default.</summary>
	public SoftFalloff SoftShape { get; set; } = SoftFalloff.Smooth;

	/// <summary>
	/// Measure the falloff along the surface instead of through space (Blender's Connected Only,
	/// Alt+O). Moving a lip then leaves the other lip alone even though it is close by, and a finger
	/// can be bent without pulling the one beside it.
	/// </summary>
	public bool SoftConnected { get; set; }

	public enum SoftFalloff
	{
		Smooth,
		Sphere,
		Root,
		InverseSquare,
		Sharp,
		Linear,
		Constant,
	}

	/// <summary>The weight a vertex gets at <paramref name="t"/>, 1 at the selection and 0 at the
	/// edge of the radius. Blender's curves.</summary>
	public float SoftWeight( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return SoftShape switch
		{
			SoftFalloff.Sphere => MathF.Sqrt( 2f * t - t * t ),
			SoftFalloff.Root => MathF.Sqrt( t ),
			SoftFalloff.InverseSquare => t * (2f - t),
			SoftFalloff.Sharp => t * t,
			SoftFalloff.Linear => t,
			SoftFalloff.Constant => 1f,
			_ => t * t * (3f - 2f * t),
		};
	}

	/// <summary>Every unselected vertex within <paramref name="radius"/> of the selection through
	/// space, with its distance. Brute force over the selection per vertex — fine at game-character
	/// sizes, and a drag only pays it once.</summary>
	IEnumerable<(int vertex, float distance)> StraightDistances( HashSet<int> verts, float radius )
	{
		var chosen = new List<Vec3>();
		foreach ( var v in verts )
			chosen.Add( Mesh.Positions[v] );

		for ( var i = 0; i < Mesh.VertexCount; i++ )
		{
			if ( verts.Contains( i ) )
				continue;

			var nearest = float.MaxValue;
			foreach ( var c in chosen )
				nearest = MathF.Min( nearest, (Mesh.Positions[i] - c).LengthSquared );

			var d = MathF.Sqrt( nearest );
			if ( d < radius )
				yield return (i, d);
		}
	}

	/// <summary>Every unselected vertex within <paramref name="radius"/> of the selection walking
	/// along edges, with the length of the shortest walk. Dijkstra out from the whole selection at
	/// once, stopping at the radius, so only the reachable neighbourhood is visited.</summary>
	IEnumerable<(int vertex, float distance)> ConnectedDistances( HashSet<int> verts, float radius )
	{
		var neighbours = new List<int>[Mesh.VertexCount];
		foreach ( var f in Mesh.Faces )
		{
			var n = f.Indices.Length;
			for ( var i = 0; i < n; i++ )
			{
				var a = f.Indices[i];
				var b = f.Indices[(i + 1) % n];
				(neighbours[a] ??= new List<int>()).Add( b );
				(neighbours[b] ??= new List<int>()).Add( a );
			}
		}

		var best = new Dictionary<int, float>();
		var queue = new PriorityQueue<int, float>();
		foreach ( var v in verts )
		{
			best[v] = 0f;
			queue.Enqueue( v, 0f );
		}

		while ( queue.TryDequeue( out var v, out var d ) )
		{
			if ( d > best[v] || neighbours[v] is null )
				continue;

			foreach ( var w in neighbours[v] )
			{
				var next = d + (Mesh.Positions[w] - Mesh.Positions[v]).Length;
				if ( next >= radius || (best.TryGetValue( w, out var known ) && known <= next) )
					continue;

				best[w] = next;
				queue.Enqueue( w, next );
			}
		}

		foreach ( var (v, d) in best )
		{
			if ( !verts.Contains( v ) )
				yield return (v, d);
		}
	}

	// --- duplicate, separate, extract ------------------------------------------------------------

	/// <summary>
	/// Pieces split off this body by <see cref="Separate"/> or <see cref="ExtractGarment"/>. They
	/// leave the edit as bodies of their own — a garment has to be a separate body to be fitted,
	/// painted and exported on its own.
	/// </summary>
	public List<PolyMesh> Separated { get; private set; } = new();

	/// <summary>Copy the selected faces in place (Shift+D). The copies are selected, ready to move.</summary>
	public void Duplicate()
	{
		var faces = RequireFaces( "Duplicate" );

		Step( "Duplicate", () =>
		{
			var copies = CopyFaces( faces, out _ );
			ClearSelection();
			SelectedFaces.UnionWith( copies );
		} );
	}

	/// <summary>Move the selected faces out of this body into a body of their own (P).</summary>
	public void Separate()
	{
		var faces = RequireFaces( "Separate" );

		if ( faces.Count == Mesh.FaceCount )
			throw new InvalidOperationException( "Every face is selected, so there would be nothing left of this body. Leave some faces unselected." );

		Step( "Separate", () =>
		{
			Separated.Add( TakeFaces( faces ) );
			ClearSelection();
		} );
	}

	/// <summary>
	/// The clothing start: copy the selected faces of the body, lift the copy off the skin by
	/// <paramref name="gap"/> along the surface normals, and separate it as a body of its own. What
	/// comes out already fits — it is the body's own shape, a gap away — and is ready for Solidify.
	/// The body is not changed.
	/// </summary>
	public void ExtractGarment( float gap )
	{
		var faces = RequireFaces( "Extract" );

		Step( "Extract garment", () =>
		{
			var normals = Mesh.ComputeVertexNormals();
			var copies = CopyFaces( faces, out var map );

			foreach ( var (from, to) in map )
				Mesh.Positions[to] = Mesh.Positions[from] + normals[from] * gap;

			Separated.Add( TakeFaces( copies ) );
			ClearSelection();
		} );
	}

	/// <summary>Append copies of <paramref name="faces"/> on new vertices. Returns the new faces'
	/// indices and the old-to-new vertex map.</summary>
	List<int> CopyFaces( List<int> faces, out Dictionary<int, int> map )
	{
		map = new Dictionary<int, int>();
		var result = new List<int>();

		foreach ( var f in faces )
		{
			var face = Mesh.Faces[f];
			var idx = new int[face.Indices.Length];

			for ( var i = 0; i < idx.Length; i++ )
			{
				var v = face.Indices[i];

				if ( !map.TryGetValue( v, out var copy ) )
				{
					copy = Mesh.Positions.Count;
					Mesh.Positions.Add( Mesh.Positions[v] );

					if ( Mesh.Skin is not null && Mesh.Skin.Count == copy )
						Mesh.Skin.Vertices.Add( Mesh.Skin[v] );

					if ( Mesh.VertexColors is not null && Mesh.VertexColors.Length == copy )
					{
						var colours = Mesh.VertexColors;
						Array.Resize( ref colours, copy + 1 );
						colours[copy] = colours[v];
						Mesh.VertexColors = colours;
					}

					map[v] = copy;
				}

				idx[i] = copy;
			}

			result.Add( Mesh.FaceCount );
			Mesh.AddFace( idx, (Vec2[])face.UVs.Clone(), face.Material );
		}

		return result;
	}

	/// <summary>Remove <paramref name="faces"/> from the mesh and return them as a mesh of their own.</summary>
	PolyMesh TakeFaces( List<int> faces )
	{
		var set = new HashSet<int>( faces );
		var piece = new PolyMesh { Positions = new List<Vec3>( Mesh.Positions ), Skin = Mesh.Skin?.Clone(), VertexColors = (Vec4[])Mesh.VertexColors?.Clone() };
		var rest = new PolyMesh { Positions = new List<Vec3>( Mesh.Positions ), Skin = Mesh.Skin?.Clone(), VertexColors = (Vec4[])Mesh.VertexColors?.Clone(), Paint = Mesh.Paint?.Clone() };

		for ( var f = 0; f < Mesh.FaceCount; f++ )
		{
			var face = Mesh.Faces[f];
			(set.Contains( f ) ? piece : rest).AddFace( (int[])face.Indices.Clone(), (Vec2[])face.UVs.Clone(), face.Material );
		}

		Mesh = RemoveUnusedVertices( rest );
		return RemoveUnusedVertices( piece );
	}

	/// <summary>Give the whole mesh the skin weights of the nearest point on
	/// <paramref name="source"/> — Blender's Data Transfer, the step that makes a garment move with
	/// the body under it.</summary>
	public int TransferWeights( PolyMesh source )
	{
		var reached = 0;
		Step( "Copy weights", () => reached = SkinBinder.TransferFrom( Mesh, source ) );
		return reached;
	}

	/// <summary>
	/// Drop the mesh under gravity for <paramref name="seconds"/>, onto <paramref name="collider"/>
	/// — or onto <see cref="SnapTarget"/> when that is null. The selected vertices are pins — the
	/// collar and shoulders of a shirt, the waistband of a skirt — and stay exactly where they are.
	/// One undo step; re-running from the same start gives the same drape, so the seconds can be
	/// scrubbed.
	/// </summary>
	/// <exception cref="InvalidOperationException">Nothing holds the mesh up and nothing stops it:
	/// no pins and no collider means the whole mesh just falls, which is never what was wanted.
	/// Everything pinned is the same mistake the other way round.</exception>
	public void Drape( float seconds, Fabric fabric = Fabric.Cotton, float thickness = -1f,
		PolyMesh collider = null )
	{
		var pins = AffectedVertices();
		var floor = collider ?? _snapTarget;

		if ( pins.Count == 0 && floor is null )
			throw new InvalidOperationException( "Drape needs something to hold the cloth up: select the vertices that pin it — a shirt's collar and shoulders, a skirt's waistband — or give it a body to land on with Snap." );

		if ( pins.Count >= Mesh.VertexCount )
			throw new InvalidOperationException( "Everything is pinned, so nothing can fall. Select only the part that holds the cloth up." );

		Step( "Drape", () =>
		{
			var sim = ClothSim.WithFabric( Mesh, pins, fabric );

			if ( thickness > 0f )
				sim.Thickness = thickness;
			else
				sim.Thickness = MathF.Max( Mesh.BoundsDiagonal, 1e-3f ) * 0.004f;

			sim.SetCollider( floor );
			sim.Run( seconds );
			Mesh = sim.Bake( Mesh );
		} );
	}

	// --- ring, linked and similar ----------------------------------------------------------------

	/// <summary>
	/// Select the ring of edges a loop cut through <paramref name="seed"/> would cross — Blender's
	/// Select Edge Ring, and the perpendicular partner to <see cref="SelectEdgeLoop"/>. A loop runs
	/// ALONG a limb; a ring runs AROUND it, which is what you pick to scale a wrist or slide a whole
	/// band of geometry.
	///
	/// Walks the same ring <see cref="EditableMesh.RingHalfEdges"/> gives the loop cut, so what gets
	/// selected and what a cut would cross are the same set by construction. A ring that runs off a
	/// boundary or through a non-quad selects as far as it got rather than refusing — a selection is
	/// not a destructive operation and half a ring is still useful.
	/// </summary>
	public void SelectEdgeRing( EdgeKey seed, Combine how = Combine.Replace )
	{
		var ring = new List<EdgeKey>();

		try
		{
			var e = EditableMesh.FromPolyMesh( Mesh );
			var he = FindHalfEdge( e, seed.A, seed.B );

			if ( he >= 0 )
			{
				foreach ( var edge in e.RingHalfEdges( he, out _ ) )
				{
					var a = e.HalfEdges[edge].Origin;
					var b = e.HalfEdges[e.HalfEdges[edge].Next].Origin;
					ring.Add( new EdgeKey( a, b ) );
				}
			}
		}
		catch ( Exception ex ) when ( ex is InvalidOperationException or ArgumentException )
		{
			// As far as it got. The seed itself is always a sane answer.
		}

		if ( ring.Count == 0 )
			ring.Add( seed );

		if ( how == Combine.Replace )
			ClearSelection();

		Mode = EditElement.Edge;

		foreach ( var edge in ring )
			Apply( SelectedEdges, edge, how == Combine.Replace ? Combine.Add : how );
	}

	/// <summary>
	/// Grow the selection over everything connected to it — Blender's Select Linked (L). One click
	/// on an ear takes the whole ear when it is a separate piece, which is how you pick one part of
	/// a character out of a mesh that has several.
	///
	/// Connected means sharing a vertex, so two pieces that merely touch at a point count as one.
	/// That is the same rule <see cref="MeshSplit.ConnectedPieces"/> uses, and disagreeing with it
	/// would mean Select Linked and Separate picked different things.
	/// </summary>
	public void SelectLinked( Combine how = Combine.Add )
	{
		var seeds = AffectedVertices();

		if ( seeds.Count == 0 )
			throw new InvalidOperationException( "Select something first — Linked grows from what is already picked." );

		var neighbours = VertexNeighbours();
		var reached = new HashSet<int>( seeds );
		var queue = new Queue<int>( seeds );

		while ( queue.Count > 0 )
		{
			if ( !neighbours.TryGetValue( queue.Dequeue(), out var around ) )
				continue;

			foreach ( var n in around )
				if ( reached.Add( n ) )
					queue.Enqueue( n );
		}

		if ( how == Combine.Replace )
			ClearSelection();

		SelectByVertices( reached );
	}

	/// <summary>What <see cref="SelectSimilar"/> compares.</summary>
	public enum Similarity
	{
		/// <summary>Faces pointing the same way, within the angle. Picks out every flat panel on a
		/// hard-surface part, or the whole top of a shoulder.</summary>
		Normal,

		/// <summary>Faces on the same material slot.</summary>
		Material,

		/// <summary>Faces of about the same area, within the angle read as a percentage. Finds the
		/// dense region left by a local subdivide.</summary>
		Area,
	}

	/// <summary>
	/// Extend a face selection to every face resembling what is already picked — Blender's Select
	/// Similar. Face mode only, because that is where it means something: a vertex has no normal of
	/// its own to compare and no material at all.
	/// </summary>
	/// <param name="tolerance">Degrees for <see cref="Similarity.Normal"/>, a fraction for
	/// <see cref="Similarity.Area"/>, ignored for <see cref="Similarity.Material"/>.</param>
	public void SelectSimilar( Similarity by, float tolerance = 10f )
	{
		if ( SelectedFaces.Count == 0 )
			throw new InvalidOperationException( "Select at least one face (3) to match against." );

		var picked = new List<int>( SelectedFaces );
		var added = new HashSet<int>( SelectedFaces );

		switch ( by )
		{
			case Similarity.Material:
			{
				var slots = new HashSet<int>();

				foreach ( var f in picked )
					slots.Add( Mesh.Faces[f].Material );

				for ( var f = 0; f < Mesh.FaceCount; f++ )
					if ( slots.Contains( Mesh.Faces[f].Material ) )
						added.Add( f );

				break;
			}

			case Similarity.Area:
			{
				var reference = new List<float>();

				foreach ( var f in picked )
					reference.Add( Mesh.FaceArea( Mesh.Faces[f], Mesh.FaceNormal( Mesh.Faces[f] ) ) );

				var slack = MathF.Max( tolerance, 1e-4f );

				for ( var f = 0; f < Mesh.FaceCount; f++ )
				{
					var area = Mesh.FaceArea( Mesh.Faces[f], Mesh.FaceNormal( Mesh.Faces[f] ) );

					foreach ( var r in reference )
					{
						// Relative, not absolute: "about the same size" means a ratio, and an
						// absolute epsilon would behave differently on a hand and on a torso.
						if ( MathF.Abs( area - r ) <= slack * MathF.Max( r, 1e-6f ) )
						{
							added.Add( f );
							break;
						}
					}
				}

				break;
			}

			default:
			{
				var limit = MathF.Cos( Math.Clamp( tolerance, 0f, 180f ) * MathF.PI / 180f );
				var reference = new List<Vec3>();

				foreach ( var f in picked )
				{
					var n = Mesh.FaceNormal( Mesh.Faces[f] );

					if ( n.LengthSquared > 1e-16f )
						reference.Add( n / n.Length );
				}

				for ( var f = 0; f < Mesh.FaceCount; f++ )
				{
					var n = Mesh.FaceNormal( Mesh.Faces[f] );

					if ( n.LengthSquared < 1e-16f )
						continue;

					n /= n.Length;

					foreach ( var r in reference )
					{
						if ( Vec3.Dot( n, r ) >= limit )
						{
							added.Add( f );
							break;
						}
					}
				}

				break;
			}
		}

		Mode = EditElement.Face;
		SelectedVertices.Clear();
		SelectedEdges.Clear();
		SelectedFaces.Clear();
		SelectedFaces.UnionWith( added );
		SelectionRevision++;
	}

	// --- invert, path, normals, fill, to sphere --------------------------------------------------

	/// <summary>
	/// Select everything that is not selected, and nothing that is — Blender's Invert (Ctrl+I).
	/// Works in the current mode: invert three faces and you get every other face, not every other
	/// vertex.
	/// </summary>
	public void InvertSelection()
	{
		switch ( Mode )
		{
			case EditElement.Vertex:
			{
				var used = new HashSet<int>();
				foreach ( var f in Mesh.Faces )
					used.UnionWith( f.Indices );

				used.ExceptWith( SelectedVertices );
				SelectedVertices.Clear();
				SelectedVertices.UnionWith( used );
				break;
			}

			case EditElement.Edge:
			{
				var all = new HashSet<EdgeKey>( Mesh.BuildEdgeFaces().Keys );
				all.ExceptWith( SelectedEdges );
				SelectedEdges.Clear();
				SelectedEdges.UnionWith( all );
				break;
			}

			default:
			{
				var rest = new List<int>();
				for ( var f = 0; f < Mesh.FaceCount; f++ )
					if ( !SelectedFaces.Contains( f ) )
						rest.Add( f );

				SelectedFaces.Clear();
				SelectedFaces.UnionWith( rest );
				break;
			}
		}

		SelectionRevision++;
	}

	/// <summary>
	/// Select the shortest path between two picked vertices (along edges, by length) or two picked
	/// faces (across shared edges, centre to centre) — Blender's Shortest Path. Pick where a seam or
	/// a waistline starts and ends and the whole line is taken, without clicking every edge of it.
	/// </summary>
	public void SelectShortestPath()
	{
		if ( Mode == EditElement.Face )
		{
			if ( SelectedFaces.Count != 2 )
				throw new InvalidOperationException( "Pick exactly two faces — the path runs from one to the other." );

			var ends = new List<int>( SelectedFaces );
			var faceNeighbours = new Dictionary<int, List<int>>();

			foreach ( var pair in Mesh.BuildEdgeFaces() )
			{
				var around = pair.Value;

				for ( var i = 0; i < around.Count; i++ )
				{
					if ( !faceNeighbours.TryGetValue( around[i], out var list ) )
						faceNeighbours[around[i]] = list = new List<int>();

					for ( var j = 0; j < around.Count; j++ )
						if ( i != j )
							list.Add( around[j] );
				}
			}

			var centres = new Vec3[Mesh.FaceCount];
			for ( var f = 0; f < Mesh.FaceCount; f++ )
				centres[f] = Mesh.FaceCentroid( Mesh.Faces[f] );

			var path = Dijkstra( ends[0], ends[1], faceNeighbours, ( a, b ) => (centres[a] - centres[b]).Length );

			SelectedFaces.UnionWith( path );
			SelectionRevision++;
			return;
		}

		if ( Mode != EditElement.Vertex )
			throw new InvalidOperationException( "Shortest path runs between two vertices (1) or two faces (3)." );

		if ( SelectedVertices.Count != 2 )
			throw new InvalidOperationException( "Pick exactly two vertices — the path runs from one to the other." );

		var picked = new List<int>( SelectedVertices );
		var vertexPath = Dijkstra( picked[0], picked[1], VertexNeighbours(), ( a, b ) => (Mesh.Positions[a] - Mesh.Positions[b]).Length );

		SelectedVertices.UnionWith( vertexPath );
		SelectionRevision++;
	}

	static List<int> Dijkstra( int from, int to, Dictionary<int, List<int>> neighbours, Func<int, int, float> cost )
	{
		var distance = new Dictionary<int, float> { [from] = 0f };
		var previous = new Dictionary<int, int>();
		var queue = new PriorityQueue<int, float>();
		queue.Enqueue( from, 0f );

		while ( queue.TryDequeue( out var at, out var d ) )
		{
			if ( at == to )
				break;

			if ( d > distance[at] || !neighbours.TryGetValue( at, out var around ) )
				continue;

			foreach ( var n in around )
			{
				var next = d + cost( at, n );

				if ( distance.TryGetValue( n, out var known ) && known <= next )
					continue;

				distance[n] = next;
				previous[n] = at;
				queue.Enqueue( n, next );
			}
		}

		if ( !distance.ContainsKey( to ) )
			throw new InvalidOperationException( "Those two are on separate pieces, so there is no path between them." );

		var path = new List<int> { to };
		for ( var at = to; at != from; at = previous[at] )
			path.Add( previous[at] );

		return path;
	}

	/// <summary>
	/// Turn the selected faces inside out (all of them when nothing is selected) — Blender's Flip.
	/// Reverses each face's winding; the per-corner UVs travel with their corners, so the texture
	/// does not move.
	/// </summary>
	public void FlipNormals()
	{
		var faces = FacesOrAll();

		Step( "Flip Normals", () =>
		{
			foreach ( var f in faces )
				Reverse( Mesh.Faces[f] );
		} );
	}

	static void Reverse( Face face )
	{
		Array.Reverse( face.Indices );
		Array.Reverse( face.UVs );
	}

	/// <summary>
	/// Make the selected faces (all of them when nothing is selected) agree with their neighbours
	/// and face outward — Blender's Recalculate Outside (Shift+N). The fix for an import or a
	/// boolean that left a few faces black in the preview.
	///
	/// Each connected piece is made consistent from one face across shared edges; an edge with more
	/// than two faces is not crossed, because there is no agreeing with three neighbours. Then the
	/// piece is flipped as a whole if its faces, on balance, point toward its middle — "inside out"
	/// for a closed shape, and a sensible guess for an open one.
	/// </summary>
	public void RecalculateNormals()
	{
		var faces = FacesOrAll();

		Step( "Recalculate Normals", () =>
		{
			var inScope = new HashSet<int>( faces );
			var edgeFaces = Mesh.BuildEdgeFaces();
			var visited = new HashSet<int>();

			foreach ( var start in faces )
			{
				if ( !visited.Add( start ) )
					continue;

				var piece = new List<int> { start };
				var queue = new Queue<int>();
				queue.Enqueue( start );

				while ( queue.Count > 0 )
				{
					var idx = Mesh.Faces[queue.Dequeue()].Indices;

					for ( var i = 0; i < idx.Length; i++ )
					{
						var a = idx[i];
						var b = idx[(i + 1) % idx.Length];

						if ( !edgeFaces.TryGetValue( new EdgeKey( a, b ), out var around ) || around.Count != 2 )
							continue;

						foreach ( var n in around )
						{
							if ( !inScope.Contains( n ) || !visited.Add( n ) )
								continue;

							// Agreeing neighbours run a shared edge the opposite way round.
							if ( RunsForward( Mesh.Faces[n], a, b ) )
								Reverse( Mesh.Faces[n] );

							piece.Add( n );
							queue.Enqueue( n );
						}
					}
				}

				var middle = Vec3.Zero;
				var totalArea = 0f;

				foreach ( var f in piece )
				{
					var area = Mesh.FaceArea( Mesh.Faces[f] );
					middle += Mesh.FaceCentroid( Mesh.Faces[f] ) * area;
					totalArea += area;
				}

				if ( totalArea <= 1e-12f )
					continue;

				middle /= totalArea;

				var outward = 0f;
				foreach ( var f in piece )
				{
					var face = Mesh.Faces[f];
					outward += Vec3.Dot( Mesh.FaceNormal( face ) * Mesh.FaceArea( face ), Mesh.FaceCentroid( face ) - middle );
				}

				if ( outward < 0f )
					foreach ( var f in piece )
						Reverse( Mesh.Faces[f] );
			}
		} );
	}

	static bool RunsForward( Face face, int a, int b )
	{
		var idx = face.Indices;
		for ( var i = 0; i < idx.Length; i++ )
			if ( idx[i] == a && idx[(i + 1) % idx.Length] == b )
				return true;

		return false;
	}

	List<int> FacesOrAll()
	{
		if ( SelectedFaces.Count > 0 )
			return new List<int>( SelectedFaces );

		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "There are no faces." );

		var all = new List<int>( Mesh.FaceCount );
		for ( var f = 0; f < Mesh.FaceCount; f++ )
			all.Add( f );

		return all;
	}

	/// <summary>
	/// Make a face — Blender's Fill (F). Select the vertices or edges round a hole and the hole is
	/// capped with one n-gon, wound to match the faces around it. Three or four loose vertices that
	/// are not round a hole get a face too, ordered around their middle.
	/// </summary>
	public void Fill()
	{
		var picked = AffectedVertices();

		if ( picked.Count < 3 )
			throw new InvalidOperationException( "Select at least three vertices, or the edges round a hole." );

		var ring = HoleRing( picked, out var material );

		if ( ring is null )
		{
			if ( picked.Count > 4 )
				throw new InvalidOperationException( "Those vertices are not round a single hole. Select the edges of one open border, or up to four loose vertices." );

			ring = OrderAroundMiddle( picked );
		}

		Step( "Fill", () =>
		{
			Mesh.Faces.Add( new Face( ring.ToArray(), null, material ) );
			ClearSelection();
			Mode = EditElement.Face;
			SelectedFaces.Add( Mesh.FaceCount - 1 );
		} );
	}

	/// <summary>
	/// The open border running through <paramref name="picked"/>, in the winding a face capping it
	/// should have — each open edge belongs to one face, and the cap has to run it the other way.
	/// Null when the picked vertices are not round a hole. Throws when they are round several.
	/// </summary>
	List<int> HoleRing( HashSet<int> picked, out int material )
	{
		var nextOf = new Dictionary<int, int>();
		material = 0;

		foreach ( var pair in Mesh.BuildEdgeFaces() )
		{
			if ( pair.Value.Count != 1 || !picked.Contains( pair.Key.A ) || !picked.Contains( pair.Key.B ) )
				continue;

			var owner = Mesh.Faces[pair.Value[0]];
			material = owner.Material;

			if ( RunsForward( owner, pair.Key.A, pair.Key.B ) )
				nextOf[pair.Key.B] = pair.Key.A;
			else
				nextOf[pair.Key.A] = pair.Key.B;
		}

		if ( nextOf.Count < 3 )
			return null;

		var ring = new List<int>();
		var start = int.MaxValue;
		foreach ( var key in nextOf.Keys )
			start = Math.Min( start, key );
		var at = start;

		do
		{
			ring.Add( at );

			if ( !nextOf.TryGetValue( at, out at ) || ring.Count > nextOf.Count )
				return null;
		}
		while ( at != start );

		if ( ring.Count != nextOf.Count )
			throw new InvalidOperationException( "The selection runs round more than one hole. Do them one at a time." );

		return ring;
	}

	/// <summary>
	/// Cap a hole with a grid of quads instead of one n-gon — Blender's Grid Fill. The border is
	/// read as four sides, two of <c>a</c> edges and two of <c>b</c>, and the inside is filled by
	/// blending between opposite sides, so the quads flow with the rim: what an eye socket, a neck
	/// or a sleeve end needs to subdivide and deform cleanly.
	///
	/// The border needs an even number of edges, at least four. <paramref name="offset"/> turns
	/// which vertex is the grid's first corner, which turns the grid on the rim; <paramref name="span"/>
	/// sets how many edges the first side takes (0 picks the squarest split).
	/// </summary>
	public void GridFill( int offset = 0, int span = 0 )
	{
		var picked = AffectedVertices();
		var material = 0;
		var ring = picked.Count >= 4 ? HoleRing( picked, out material ) : null;

		if ( ring is null )
			throw new InvalidOperationException( "Select the edges round one hole — an open border with an even number of edges." );

		var n = ring.Count;

		if ( n % 2 != 0 )
			throw new InvalidOperationException( $"That hole has {n} edges. Grid fill needs an even number — loop cut or dissolve one edge of the rim first." );

		var half = n / 2;
		var a = span > 0 ? Math.Clamp( span, 1, half - 1 ) : Math.Max( 1, half / 2 );
		var b = half - a;

		offset = ((offset % n) + n) % n;
		var rim = new int[n];
		for ( var i = 0; i < n; i++ )
			rim[i] = ring[(i + offset) % n];

		Step( "Grid fill", () =>
		{
			var grid = new int[a + 1, b + 1];

			for ( var i = 0; i <= a; i++ )
			{
				grid[i, 0] = rim[i];
				grid[i, b] = rim[2 * a + b - i];
			}

			for ( var j = 0; j <= b; j++ )
			{
				grid[a, j] = rim[a + j];
				grid[0, j] = rim[(n - j) % n];
			}

			// Coons patch: blend the two pairs of opposite sides, minus the corners counted twice.
			Vec3 P( int i, int j ) => Mesh.Positions[grid[i, j]];

			for ( var i = 1; i < a; i++ )
			{
				for ( var j = 1; j < b; j++ )
				{
					var u = (float)i / a;
					var v = (float)j / b;

					var sides = P( i, 0 ) * (1 - v) + P( i, b ) * v + P( 0, j ) * (1 - u) + P( a, j ) * u;
					var corners = P( 0, 0 ) * ((1 - u) * (1 - v)) + P( a, 0 ) * (u * (1 - v)) + P( 0, b ) * ((1 - u) * v) + P( a, b ) * (u * v);

					// Carries the skin and colour of the nearest rim corner, so a filled hole on a
					// rigged body still moves with it.
					var nearest = u + v < 1f ? grid[0, 0] : grid[a, b];
					var created = CopyVertex( nearest );
					Mesh.Positions[created] = sides - corners;
					grid[i, j] = created;
				}
			}

			var made = new List<int>();

			for ( var i = 0; i < a; i++ )
			{
				for ( var j = 0; j < b; j++ )
				{
					made.Add( Mesh.FaceCount );
					Mesh.AddFace( new[] { grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1] }, null, material );
				}
			}

			ClearSelection();
			Mode = EditElement.Face;
			SelectedFaces.UnionWith( made );
		} );
	}

	/// <summary>
	/// Tear the mesh open along the selected edges — Blender's Rip (V). Each edge becomes two, one
	/// on each side, so moving the selection opens a slit: a mouth, a pocket, the cut down the front
	/// of a jacket. The ends of a path that stop inside the mesh stay joined, so the slit closes at
	/// its corners like a real cut. The new side is left selected, ready to move.
	/// </summary>
	public void Rip()
	{
		if ( SelectedEdges.Count == 0 )
			throw new InvalidOperationException( "Rip tears along edges. Switch to Edge mode (2) and pick the line to cut." );

		var edgeFaces = Mesh.BuildEdgeFaces();
		var cut = new HashSet<EdgeKey>( SelectedEdges );

		foreach ( var e in cut )
			if ( !edgeFaces.TryGetValue( e, out var owners ) || owners.Count != 2 )
				throw new InvalidOperationException( "Every edge to rip needs a face on both sides. An open border is already torn." );

		// Walk the cut into chains so each edge knows which way along it runs; which face is on
		// the "moving" side follows from that.
		var around = new Dictionary<int, List<EdgeKey>>();
		foreach ( var e in cut )
		{
			foreach ( var v in new[] { e.A, e.B } )
			{
				if ( !around.TryGetValue( v, out var list ) )
					around[v] = list = new List<EdgeKey>();
				list.Add( e );
			}
		}

		foreach ( var pair in around )
			if ( pair.Value.Count > 2 )
				throw new InvalidOperationException( "The cut branches. Rip one line at a time." );

		var moving = new HashSet<int>();
		var walked = new HashSet<EdgeKey>();

		foreach ( var seed in cut )
		{
			if ( walked.Contains( seed ) )
				continue;

			// Start the walk at an end of this chain, or anywhere on it if it is a closed loop.
			var chain = new HashSet<int>();
			var stack = new Stack<int>();
			stack.Push( seed.A );

			while ( stack.Count > 0 )
			{
				var v = stack.Pop();
				if ( !chain.Add( v ) )
					continue;

				foreach ( var e in around[v] )
					stack.Push( e.A == v ? e.B : e.A );
			}

			var at = seed.A;
			foreach ( var v in chain )
				if ( around[v].Count == 1 ) { at = v; break; }

			// Walk it, taking the face each edge runs forward in as the moving side.
			while ( true )
			{
				var edge = around[at].Find( x => !walked.Contains( x ) );
				if ( !around[at].Exists( x => !walked.Contains( x ) ) )
					break;

				walked.Add( edge );
				var next = edge.A == at ? edge.B : edge.A;

				foreach ( var f in edgeFaces[edge] )
					if ( RunsForward( Mesh.Faces[f], at, next ) )
						moving.Add( f );

				at = next;
			}
		}

		Step( "Rip", () =>
		{
			var faceAround = new Dictionary<int, List<int>>();
			for ( var f = 0; f < Mesh.FaceCount; f++ )
			{
				foreach ( var v in Mesh.Faces[f].Indices )
				{
					if ( !around.ContainsKey( v ) )
						continue;
					if ( !faceAround.TryGetValue( v, out var list ) )
						faceAround[v] = list = new List<int>();
					list.Add( f );
				}
			}

			var copies = new Dictionary<int, int>();

			foreach ( var pair in faceAround )
			{
				var v = pair.Key;
				var fan = pair.Value;

				// Group the faces round v that stay connected without crossing the cut.
				var group = new Dictionary<int, int>();
				var groups = 0;

				foreach ( var start in fan )
				{
					if ( group.ContainsKey( start ) )
						continue;

					var queue = new Queue<int>();
					queue.Enqueue( start );
					group[start] = groups;

					while ( queue.Count > 0 )
					{
						var f = queue.Dequeue();
						var idx = Mesh.Faces[f].Indices;
						var k = System.Array.IndexOf( idx, v );

						foreach ( var w in new[] { idx[(k + 1) % idx.Length], idx[(k + idx.Length - 1) % idx.Length] } )
						{
							var key = new EdgeKey( v, w );
							if ( cut.Contains( key ) || !edgeFaces.TryGetValue( key, out var owners ) )
								continue;

							foreach ( var g in owners )
							{
								if ( group.ContainsKey( g ) )
									continue;
								group[g] = groups;
								queue.Enqueue( g );
							}
						}
					}

					groups++;
				}

				// Still in one piece round v: an end of the cut inside the mesh. It stays joined.
				if ( groups < 2 )
					continue;

				var movingGroup = -1;
				foreach ( var f in fan )
					if ( moving.Contains( f ) ) { movingGroup = group[f]; break; }

				if ( movingGroup < 0 )
					continue;

				var copy = CopyVertex( v );
				copies[v] = copy;

				foreach ( var f in fan )
				{
					if ( group[f] != movingGroup )
						continue;

					var idx = Mesh.Faces[f].Indices;
					idx[System.Array.IndexOf( idx, v )] = copy;
				}
			}

			if ( copies.Count == 0 )
				throw new InvalidOperationException( "Nothing to tear: a single edge with both ends inside the mesh has nowhere to open. Select a longer line, or one that reaches a border." );

			ClearSelection();
			Mode = EditElement.Edge;

			foreach ( var e in cut )
				SelectedEdges.Add( new EdgeKey( copies.TryGetValue( e.A, out var ca ) ? ca : e.A, copies.TryGetValue( e.B, out var cb ) ? cb : e.B ) );
		} );
	}

	/// <summary>
	/// The point Spin and Screw turn round (on the up axis through it). World origin until moved —
	/// Blender's 3D cursor, for the one thing here that needs it.
	/// </summary>
	public Vec3 Pivot { get; set; }

	/// <summary>Put the <see cref="Pivot"/> at the middle of the selection.</summary>
	public void PivotToSelection()
	{
		if ( AffectedVertices().Count == 0 )
			throw new InvalidOperationException( "Select something to put the pivot at." );

		Pivot = SelectionCentre();
	}

	List<int> OrderAroundMiddle( HashSet<int> vertices )
	{
		var list = new List<int>( vertices );
		var middle = Vec3.Zero;
		foreach ( var v in list )
			middle += Mesh.Positions[v];
		middle /= list.Count;

		// The plane to sort in: the biggest cross product of spokes from the middle.
		var normal = Vec3.Zero;
		for ( var i = 0; i < list.Count; i++ )
		for ( var j = i + 1; j < list.Count; j++ )
		{
			var c = Vec3.Cross( Mesh.Positions[list[i]] - middle, Mesh.Positions[list[j]] - middle );
			if ( c.LengthSquared > normal.LengthSquared )
				normal = c;
		}

		if ( normal.LengthSquared < 1e-16f )
			throw new InvalidOperationException( "Those vertices lie in a line, so there is no face to make." );

		normal /= normal.Length;
		var u = Mesh.Positions[list[0]] - middle;
		u -= normal * Vec3.Dot( u, normal );
		u /= MathF.Max( u.Length, 1e-12f );
		var w = Vec3.Cross( normal, u );

		float Angle( int v )
		{
			var d = Mesh.Positions[v] - middle;
			return MathF.Atan2( Vec3.Dot( d, w ), Vec3.Dot( d, u ) );
		}

		list.Sort( ( a, b ) => Angle( a ).CompareTo( Angle( b ) ) );
		return list;
	}

	/// <summary>
	/// Push the selected vertices toward a sphere round their middle — Blender's To Sphere.
	/// <paramref name="factor"/> 1 is a full sphere at their average radius, 0 is no change. The
	/// quick way to round off a head, an eye or a knuckle blocked out as a cube.
	/// </summary>
	public void ToSphere( float factor = 1f )
	{
		var moving = AffectedVertices();

		if ( moving.Count < 2 )
			throw new InvalidOperationException( "Select the vertices to round off." );

		factor = Math.Clamp( factor, 0f, 1f );

		Step( "To Sphere", () =>
		{
			var middle = Vec3.Zero;
			foreach ( var v in moving )
				middle += Mesh.Positions[v];
			middle /= moving.Count;

			var radius = 0f;
			foreach ( var v in moving )
				radius += (Mesh.Positions[v] - middle).Length;
			radius /= moving.Count;

			foreach ( var v in moving )
			{
				var offset = Mesh.Positions[v] - middle;
				var length = offset.Length;

				if ( length < 1e-9f )
					continue;

				Mesh.Positions[v] = Vec3.Lerp( Mesh.Positions[v], middle + offset * (radius / length), factor );
			}
		} );
	}

	// --- split, spin, shear, bend, array, trait selection -----------------------------------------

	/// <summary>
	/// Tear the selected faces loose from the rest without moving them — Blender's Split (Y). They
	/// keep their place, but now have vertices of their own along the border, so moving them opens
	/// a gap instead of stretching the faces around. How you cut a collar or a cuff free of a shirt.
	/// </summary>
	public void SplitFaces()
	{
		var faces = RequireFaces( "Split" );

		if ( faces.Count == Mesh.FaceCount )
			throw new InvalidOperationException( "Every face is selected, so there is nothing to split them from." );

		Step( "Split", () =>
		{
			var picked = new HashSet<int>( faces );
			var shared = new HashSet<int>();
			var inside = new HashSet<int>();

			for ( var f = 0; f < Mesh.FaceCount; f++ )
				if ( !picked.Contains( f ) )
					shared.UnionWith( Mesh.Faces[f].Indices );

			foreach ( var f in faces )
				inside.UnionWith( Mesh.Faces[f].Indices );

			shared.IntersectWith( inside );

			if ( shared.Count == 0 )
				throw new InvalidOperationException( "Those faces are already loose from the rest." );

			var copies = new Dictionary<int, int>();
			foreach ( var v in shared )
				copies[v] = CopyVertex( v );

			foreach ( var f in faces )
			{
				var idx = Mesh.Faces[f].Indices;
				for ( var i = 0; i < idx.Length; i++ )
					if ( copies.TryGetValue( idx[i], out var c ) )
						idx[i] = c;
			}
		} );
	}

	/// <summary>A new vertex on top of <paramref name="v"/>, carrying its skin and colour.</summary>
	int CopyVertex( int v )
	{
		var copy = Mesh.Positions.Count;
		Mesh.Positions.Add( Mesh.Positions[v] );

		if ( Mesh.Skin is not null && Mesh.Skin.Count == copy )
			Mesh.Skin.Vertices.Add( Mesh.Skin[v] );

		if ( Mesh.VertexColors is not null && Mesh.VertexColors.Length == copy )
		{
			var colours = Mesh.VertexColors;
			Array.Resize( ref colours, copy + 1 );
			colours[copy] = colours[v];
			Mesh.VertexColors = colours;
		}

		return copy;
	}

	/// <summary>
	/// Sweep the selected edges round the up axis (Z) through the <see cref="Pivot"/> and build faces
	/// behind them — Blender's Spin, and with <paramref name="rise"/> its Screw. Select an open rim
	/// and spin it for a lathe: a bowl from a lip, a button from an edge, a horn from a ring with rise.
	/// A full turn with no rise closes on itself instead of leaving a doubled seam.
	/// </summary>
	/// <param name="degrees">How far round to go. 360 is a full turn.</param>
	/// <param name="steps">How many bands of faces to split the turn into.</param>
	/// <param name="rise">How far up Z the far end ends up — the screw's pitch per turn.</param>
	public void Spin( float degrees, int steps, float rise = 0f )
	{
		if ( SelectedEdges.Count == 0 )
			throw new InvalidOperationException( "Spin sweeps edges. Switch to Edge mode (2) and pick the profile to sweep." );

		if ( MathF.Abs( degrees ) < 1e-3f )
			throw new InvalidOperationException( "Spin needs an angle." );

		steps = Math.Clamp( steps, 1, 512 );
		var edges = new List<EdgeKey>( SelectedEdges );

		Step( rise == 0f ? "Spin" : "Screw", () =>
		{
			var profile = new List<int>();
			foreach ( var e in edges )
			{
				if ( !profile.Contains( e.A ) ) profile.Add( e.A );
				if ( !profile.Contains( e.B ) ) profile.Add( e.B );
			}

			var closes = MathF.Abs( MathF.Abs( degrees ) - 360f ) < 1e-3f && MathF.Abs( rise ) < 1e-6f;
			var rings = new List<Dictionary<int, int>>();
			var first = new Dictionary<int, int>();
			foreach ( var v in profile )
				first[v] = closes ? CopyVertex( v ) : v;
			rings.Add( first );

			for ( var k = 1; k <= steps; k++ )
			{
				if ( closes && k == steps )
				{
					rings.Add( first );
					break;
				}

				var t = (float)k / steps;
				var angle = degrees * t * MathF.PI / 180f;
				var cos = MathF.Cos( angle );
				var sin = MathF.Sin( angle );
				var ring = new Dictionary<int, int>();

				foreach ( var v in profile )
				{
					var copy = CopyVertex( v );
					var p = Mesh.Positions[v] - Pivot;
					Mesh.Positions[copy] = Pivot + new Vec3( p.x * cos - p.y * sin, p.x * sin + p.y * cos, p.z + rise * t );
					ring[v] = copy;
				}

				rings.Add( ring );
			}

			var edgeFaces = Mesh.BuildEdgeFaces();
			var made = new List<int>();

			foreach ( var e in edges )
			{
				// Wind against an existing face on the edge, so the new band agrees with it.
				var forward = edgeFaces.TryGetValue( e, out var owners ) && owners.Count > 0
					&& RunsForward( Mesh.Faces[owners[0]], e.A, e.B );

				var material = owners is { Count: > 0 } ? Mesh.Faces[owners[0]].Material : 0;

				for ( var k = 1; k < rings.Count; k++ )
				{
					var a0 = rings[k - 1][e.A];
					var b0 = rings[k - 1][e.B];
					var a1 = rings[k][e.A];
					var b1 = rings[k][e.B];

					made.Add( Mesh.FaceCount );
					Mesh.AddFace( forward ? new[] { b0, a0, a1, b1 } : new[] { a0, b0, b1, a1 }, null, material );
				}
			}

			ClearSelection();
			Mode = EditElement.Face;
			SelectedFaces.UnionWith( made );
		} );
	}

	/// <summary>
	/// Lean the selection over: every vertex slides along X in proportion to its height above the
	/// selection's middle — Blender's Shear. <paramref name="factor"/> 1 is a 45 degree lean. For a
	/// slanted heel, a swept-back fin, a leaning hat.
	/// </summary>
	public void Shear( float factor )
	{
		var moving = RequireVertices( "Shear" );

		Step( "Shear", () =>
		{
			var middle = 0f;
			foreach ( var v in moving )
				middle += Mesh.Positions[v].z;
			middle /= moving.Count;

			foreach ( var v in moving )
			{
				var p = Mesh.Positions[v];
				Mesh.Positions[v] = new Vec3( p.x + factor * (p.z - middle), p.y, p.z );
			}
		} );
	}

	/// <summary>
	/// Curl the selection up through <paramref name="degrees"/> along its length in X — Blender's
	/// Bend. The -X end stays put and the rest wraps round an arc of the same length, so nothing
	/// stretches. A tail, a curled toe, a bent horn from a straight one.
	/// </summary>
	public void Bend( float degrees )
	{
		var moving = RequireVertices( "Bend" );

		if ( MathF.Abs( degrees ) < 1e-3f )
			return;

		Step( "Bend", () =>
		{
			var minX = float.MaxValue;
			var maxX = float.MinValue;
			var middle = 0f;

			foreach ( var v in moving )
			{
				var p = Mesh.Positions[v];
				minX = MathF.Min( minX, p.x );
				maxX = MathF.Max( maxX, p.x );
				middle += p.z;
			}

			middle /= moving.Count;
			var length = maxX - minX;

			if ( length < 1e-6f )
				throw new InvalidOperationException( "The selection has no length along X to bend along." );

			var total = degrees * MathF.PI / 180f;
			var radius = length / total;

			foreach ( var v in moving )
			{
				var p = Mesh.Positions[v];
				var angle = total * (p.x - minX) / length;
				var arm = radius - (p.z - middle);
				Mesh.Positions[v] = new Vec3( minX + arm * MathF.Sin( angle ), p.y, middle + radius - arm * MathF.Cos( angle ) );
			}
		} );
	}

	HashSet<int> RequireVertices( string operation )
	{
		var moving = AffectedVertices();

		if ( moving.Count < 2 )
			throw new InvalidOperationException( $"{operation} needs a selection to work on." );

		return moving;
	}

	/// <summary>
	/// Repeat the selected faces in a row along X, each copy one selection-width further on —
	/// Blender's Array, done once rather than as a modifier. Teeth, rivets, buttons, fence posts.
	/// <paramref name="count"/> includes the original. Every copy ends up selected.
	/// </summary>
	public void ArrayFaces( int count, float gap = 0f )
	{
		var faces = RequireFaces( "Array" );
		count = Math.Clamp( count, 2, 256 );

		Step( "Array", () =>
		{
			var minX = float.MaxValue;
			var maxX = float.MinValue;

			foreach ( var f in faces )
				foreach ( var v in Mesh.Faces[f].Indices )
				{
					minX = MathF.Min( minX, Mesh.Positions[v].x );
					maxX = MathF.Max( maxX, Mesh.Positions[v].x );
				}

			var stride = maxX - minX + gap;

			if ( stride <= 1e-6f )
				throw new InvalidOperationException( "The selection has no width along X, so the copies would sit on top of each other. Give it a gap." );

			var all = new List<int>( faces );

			for ( var k = 1; k < count; k++ )
			{
				all.AddRange( CopyFaces( faces, out var map ) );

				foreach ( var copy in map.Values )
					Mesh.Positions[copy] += new Vec3( stride * k, 0f, 0f );
			}

			ClearSelection();
			SelectedFaces.UnionWith( all );
		} );
	}

	/// <summary>
	/// Select every edge that is not shared by exactly two faces — the open borders and the
	/// three-way junctions. Blender's Select Non-Manifold (Shift+Ctrl+Alt+M). Run it before export:
	/// anything it lights up on something meant to be solid is a hole or a bad join.
	/// </summary>
	public void SelectNonManifold()
	{
		ClearSelection();
		Mode = EditElement.Edge;

		foreach ( var pair in Mesh.BuildEdgeFaces() )
			if ( pair.Value.Count != 2 )
				SelectedEdges.Add( pair.Key );

		SelectionRevision++;
	}

	/// <summary>
	/// Swap a face selection for the loop of edges round its border — Blender's Select Boundary
	/// Loop. Select a sleeve, take its rim: ready to bridge, extrude or mark as a seam.
	/// </summary>
	public void SelectBoundaryLoop()
	{
		var faces = RequireFaces( "Boundary loop" );
		var picked = new HashSet<int>( faces );
		var rim = new List<EdgeKey>();

		foreach ( var pair in Mesh.BuildEdgeFaces() )
		{
			var inside = 0;
			foreach ( var f in pair.Value )
				if ( picked.Contains( f ) )
					inside++;

			if ( inside > 0 && inside < pair.Value.Count || inside == 1 && pair.Value.Count == 1 )
				rim.Add( pair.Key );
		}

		ClearSelection();
		Mode = EditElement.Edge;
		SelectedEdges.UnionWith( rim );
		SelectionRevision++;
	}

	/// <summary>
	/// Drop every other element of the selection, in a checker across its surface — Blender's
	/// Checker Deselect. On a grid of faces it gives an actual checkerboard: for inset-and-extrude
	/// panelling, alternate studs, a quilted pattern.
	/// </summary>
	public void CheckerDeselect()
	{
		if ( Mode == EditElement.Edge )
			throw new InvalidOperationException( "Checker works on vertices (1) or faces (3)." );

		var set = Mode == EditElement.Face ? SelectedFaces : SelectedVertices;

		if ( set.Count < 2 )
			throw new InvalidOperationException( "Select a patch to thin out first." );

		Dictionary<int, List<int>> neighbours;

		if ( Mode == EditElement.Vertex )
			neighbours = VertexNeighbours();
		else
		{
			// Faces that share a corner, not just an edge: a checker is diagonal neighbours kept.
			neighbours = new Dictionary<int, List<int>>();
			var byVertex = new Dictionary<int, List<int>>();

			foreach ( var f in set )
				foreach ( var v in Mesh.Faces[f].Indices )
				{
					if ( !byVertex.TryGetValue( v, out var list ) )
						byVertex[v] = list = new List<int>();
					list.Add( f );
				}

			foreach ( var f in set )
			{
				var around = neighbours[f] = new List<int>();
				var edges = Mesh.Faces[f].Indices;

				for ( var i = 0; i < edges.Length; i++ )
				{
					var a = edges[i];
					var b = edges[(i + 1) % edges.Length];

					foreach ( var g in byVertex[a] )
						if ( g != f && Array.IndexOf( Mesh.Faces[g].Indices, b ) >= 0 && !around.Contains( g ) )
							around.Add( g );
				}
			}
		}

		var depth = new Dictionary<int, int>();
		var ordered = new List<int>( set );
		ordered.Sort();

		foreach ( var start in ordered )
		{
			if ( depth.ContainsKey( start ) )
				continue;

			depth[start] = 0;
			var queue = new Queue<int>();
			queue.Enqueue( start );

			while ( queue.Count > 0 )
			{
				var at = queue.Dequeue();
				if ( !neighbours.TryGetValue( at, out var around ) )
					continue;

				foreach ( var n in around )
				{
					if ( !set.Contains( n ) || depth.ContainsKey( n ) )
						continue;

					depth[n] = depth[at] + 1;
					queue.Enqueue( n );
				}
			}
		}

		foreach ( var pair in depth )
			if ( pair.Value % 2 == 1 )
				set.Remove( pair.Key );

		SelectionRevision++;
	}

	/// <summary>
	/// Remove vertices no face uses — what a delete or a dissolve can leave behind, and what an
	/// import sometimes arrives with. Blender's Delete Loose. They are invisible but still exported.
	/// </summary>
	public void DeleteLoose()
	{
		var used = new HashSet<int>();
		foreach ( var f in Mesh.Faces )
			used.UnionWith( f.Indices );

		if ( used.Count == Mesh.VertexCount )
			throw new InvalidOperationException( "There are no loose vertices." );

		Step( "Delete loose", () =>
		{
			Mesh = RemoveUnusedVertices( Mesh );
			ClearSelection();
		} );
	}

	// --- retopology ------------------------------------------------------------------------------

	/// <summary>
	/// Where retopology started: every vertex at or past this index is new topology drawn on the
	/// body, every vertex before it is the body being traced. -1 when not retopologizing.
	///
	/// New geometry is only ever appended, so the line holds for everything retopology itself does.
	/// Deleting part of the body being traced while retopologizing moves it; don't.
	/// </summary>
	public int RetopoStart { get; private set; } = -1;

	public bool IsRetopologizing => RetopoStart >= 0;

	int _retopoFaceStart = -1;

	/// <summary>
	/// Start drawing new, clean topology over this body — Blender's Poly Build over a snapped
	/// target. The body as it stands becomes the surface everything snaps to, and new geometry
	/// floats <paramref name="offset"/> above it so it can be seen and picked. Finish with
	/// <see cref="SeparateRetopo"/>, which lifts the new mesh out as a body of its own and leaves
	/// the original untouched.
	/// </summary>
	public void BeginRetopo( float offset )
	{
		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "Retopology draws over a surface, and this body has none." );

		RetopoStart = Mesh.VertexCount;
		_retopoFaceStart = Mesh.FaceCount;
		SnapTarget = Mesh.Clone();
		SnapOffset = offset;
		ClearSelection();
		Mode = EditElement.Vertex;
		SelectionRevision++;
	}

	/// <summary>Stop retopologizing without separating anything — what is drawn stays in this body.</summary>
	public void EndRetopo()
	{
		RetopoStart = -1;
		SnapTarget = null;
	}

	/// <summary>A point dropped onto the traced surface, lifted by the snap offset.</summary>
	Vec3 OnSurface( Vec3 p )
	{
		if ( _snapTree is not null && _snapTree.NearestSurface( _snapTarget, p, SnapMaxDistance ) is { } hit )
			return hit.Point + hit.Normal * SnapOffset;

		return p;
	}

	/// <summary>
	/// What the next Poly Build click would do, worked out without doing it — so the viewport can
	/// draw the quad as a ghost under the cursor, ring the vertices it would weld to, and say why a
	/// click would be refused before it is made.
	/// </summary>
	public sealed class PolyBuildPlan
	{
		/// <summary>True: extends the selected open edge into a quad. False: drops a vertex.</summary>
		public bool Extends;

		/// <summary>True when this click would close a face (an extension always does).</summary>
		public bool ClosesFace;

		/// <summary>The face the click would make, or for a lone vertex drop just that vertex — in
		/// winding order. Existing corners included, so the ghost can be drawn whole.</summary>
		public Vec3[] Corners;

		/// <summary>Parallel to <see cref="Corners"/>: the existing vertex a corner is (or welds to),
		/// or -1 for a vertex the click would create.</summary>
		public int[] Vertices;

		/// <summary>Which corner is the one this click places — the rest were placed before.</summary>
		public int ClickedCorner = -1;

		/// <summary>Why the click would be refused, or null when it is fine.</summary>
		public string Problem;

		public bool IsValid => Problem is null;
	}

	/// <summary>
	/// Make every extension a square: the new quad's side is as long as the edge it grows from,
	/// in the direction of the click, however far away the click was. Keeps strips even without
	/// having to judge the distance each time. Off, the quad reaches exactly to the click.
	/// </summary>
	public bool EvenQuads { get; set; } = true;

	/// <summary>
	/// Plan one click of Poly Build at <paramref name="point"/> on the traced surface. See
	/// <see cref="PolyBuild"/> for what a click does; this is the same decision, made read-only.
	/// </summary>
	/// <param name="weld">A corner this close to a vertex of the new mesh uses that vertex instead
	/// of making another, which is how strips join up into one mesh rather than lying side by side.</param>
	public PolyBuildPlan PlanPolyBuild( Vec3 point, float weld = 0f, bool closeTriangle = false )
	{
		if ( !IsRetopologizing )
			return new PolyBuildPlan { Corners = new Vec3[0], Vertices = new int[0], Problem = "Start retopology first, over the body you want to trace." };

		var at = OnSeam( OnSurface( point ), weld );

		if ( ExtendableEdge( out var edge, out var owner ) )
			return PlanExtend( edge, owner, at, weld );

		var existing = NearestRetopoVertex( at, weld, -1, -1 );
		// The corners placed so far are simply the selected vertices of the new mesh — so they
		// survive an undo, and a vertex you click to select counts as a corner too.
		var pending = new List<int>();

		if ( Mode == EditElement.Vertex )
			foreach ( var p in SelectedVertices )
				if ( p >= RetopoStart && p < Mesh.VertexCount )
					pending.Add( p );

		if ( pending.Count > 3 )
			return new PolyBuildPlan { Corners = new[] { at }, Vertices = new[] { -1 }, Problem = "Four corners are already selected. Press F to make them a face, or click empty space to start again." };

		if ( existing >= 0 && pending.Contains( existing ) )
			return new PolyBuildPlan { Corners = new[] { Mesh.Positions[existing] }, Vertices = new[] { existing }, Problem = "That vertex is already one of the corners you are placing." };

		var plan = new PolyBuildPlan();
		var closes = pending.Count == 3 || closeTriangle && pending.Count == 2;

		if ( !closes )
		{
			plan.Corners = new[] { existing >= 0 ? Mesh.Positions[existing] : at };
			plan.Vertices = new[] { existing };
			return plan;
		}

		// The closing click: order the corners round their middle and face them the way the
		// surface does, so the new mesh is right way out from the start.
		var corners = new List<(Vec3 P, int V)>();
		foreach ( var p in pending )
			corners.Add( (Mesh.Positions[p], p) );
		corners.Add( (existing >= 0 ? Mesh.Positions[existing] : at, existing) );

		var middle = Vec3.Zero;
		foreach ( var c in corners )
			middle += c.P;
		middle /= corners.Count;

		var surfaceNormal = SurfaceNormalAt( middle );
		var u = corners[0].P - middle;
		u -= surfaceNormal * Vec3.Dot( u, surfaceNormal );
		u /= MathF.Max( u.Length, 1e-12f );
		var w = Vec3.Cross( surfaceNormal, u );

		corners.Sort( ( x, y ) =>
		{
			var dx = x.P - middle;
			var dy = y.P - middle;
			return MathF.Atan2( Vec3.Dot( dx, w ), Vec3.Dot( dx, u ) ).CompareTo( MathF.Atan2( Vec3.Dot( dy, w ), Vec3.Dot( dy, u ) ) );
		} );

		plan.ClosesFace = true;
		plan.Corners = new Vec3[corners.Count];
		plan.Vertices = new int[corners.Count];

		for ( var i = 0; i < corners.Count; i++ )
		{
			plan.Corners[i] = corners[i].P;
			plan.Vertices[i] = corners[i].V;

			if ( corners[i].V == existing )
				plan.ClickedCorner = i;
		}

		plan.Problem = FaceProblem( plan.Corners, surfaceNormal );
		return plan;
	}

	PolyBuildPlan PlanExtend( EdgeKey edge, int owner, Vec3 at, float weld )
	{
		var a = edge.A;
		var b = edge.B;

		// Wound against the face that owns the edge, so the strip agrees with what it grew from.
		var forward = RunsForward( Mesh.Faces[owner], a, b );
		var first = forward ? b : a;
		var second = forward ? a : b;

		var pa = Mesh.Positions[first];
		var pb = Mesh.Positions[second];
		var middle = (pa + pb) * 0.5f;
		var shift = at - middle;

		if ( EvenQuads )
		{
			// Across the edge only: sliding along it is what shears a strip into slivers.
			var along = pb - pa;
			var length = along.Length;
			along /= MathF.Max( length, 1e-12f );
			shift -= along * Vec3.Dot( shift, along );

			if ( shift.LengthSquared > 1e-12f )
				shift = shift / shift.Length * length;
		}

		var plan = new PolyBuildPlan { Extends = true, ClosesFace = true };

		(Vec3 P, int V) Corner( Vec3 from, int avoid )
		{
			var p = OnSeam( OnSurface( from + shift ), weld );
			var near = NearestRetopoVertex( p, weld, a, b, avoid );
			return near >= 0 ? (Mesh.Positions[near], near) : (p, -1);
		}

		// first -> second is the new face's shared side; the far side runs second' -> first'.
		var farSecond = Corner( pb, -1 );
		var farFirst = Corner( pa, farSecond.V );

		plan.Corners = new[] { pa, pb, farSecond.P, farFirst.P };
		plan.Vertices = new[] { first, second, farSecond.V, farFirst.V };

		if ( farSecond.V >= 0 && farSecond.V == farFirst.V )
			plan.Problem = "Both new corners would weld to the same vertex. Click further away, or weld less.";
		else
			plan.Problem = FaceProblem( plan.Corners, SurfaceNormalAt( middle + shift * 0.5f ) );

		return plan;
	}

	/// <summary>
	/// Why a face with these corners would be bad retopology, or null. Checks the three things
	/// that ruin a hand-drawn mesh: a quad twisted into a bow-tie, one facing into the surface,
	/// and one lying on top of a face already drawn.
	/// </summary>
	string FaceProblem( Vec3[] corners, Vec3 surfaceNormal )
	{
		var n = corners.Length;
		var normal = Vec3.Zero;

		for ( var i = 0; i < n; i++ )
			normal += Vec3.Cross( corners[i], corners[(i + 1) % n] );

		var area = normal.Length * 0.5f;

		if ( area < 1e-10f )
			return "That face would have no area — the corners are in a line.";

		// Every corner turns the same way round, or the face is a bow-tie or folds back on itself.
		for ( var i = 0; i < n; i++ )
		{
			var turn = Vec3.Cross( corners[(i + 1) % n] - corners[i], corners[(i + 2) % n] - corners[(i + 1) % n] );
			if ( Vec3.Dot( turn, normal ) < 0f )
				return "That would twist or fold the face. Click on the other side, or closer in.";
		}

		if ( surfaceNormal.LengthSquared > 0f && Vec3.Dot( normal, surfaceNormal ) < 0f )
			return "That face would point into the body. Click on the side the strip should grow towards.";

		// On top of a face already drawn: the new face's middle falls inside one of them.
		var middle = Vec3.Zero;
		foreach ( var c in corners )
			middle += c;
		middle /= n;

		var reach = MathF.Sqrt( area ) * 0.5f;

		// Only the faces drawn since retopology began: they are all appended after the body's own,
		// so a dense sculpt costs nothing here.
		for ( var f = Math.Max( _retopoFaceStart, 0 ); f < Mesh.FaceCount; f++ )
		{
			var face = Mesh.Faces[f];

			var fn = Mesh.FaceNormal( face );
			if ( fn.LengthSquared < 1e-16f )
				continue;
			fn /= fn.Length;

			var centre = Mesh.FaceCentroid( face );
			if ( MathF.Abs( Vec3.Dot( middle - centre, fn ) ) > reach )
				continue;

			if ( InsideFace( face, middle, fn ) )
				return "That face would lie on top of one you already drew.";
		}

		return null;
	}

	bool InsideFace( Face face, Vec3 p, Vec3 normal )
	{
		var idx = face.Indices;
		for ( var i = 0; i < idx.Length; i++ )
		{
			var a = Mesh.Positions[idx[i]];
			var b = Mesh.Positions[idx[(i + 1) % idx.Length]];
			if ( Vec3.Dot( Vec3.Cross( b - a, p - a ), normal ) < 0f )
				return false;
		}

		return true;
	}

	Vec3 SurfaceNormalAt( Vec3 p )
	{
		if ( _snapTree?.NearestSurface( _snapTarget, p, SnapMaxDistance ) is { } hit && hit.Normal.LengthSquared > 1e-16f )
			return hit.Normal / hit.Normal.Length;

		return Vec3.Zero;
	}

	bool ExtendableEdge( out EdgeKey edge, out int owner )
	{
		edge = default;
		owner = -1;

		if ( Mode != EditElement.Edge || SelectedEdges.Count != 1 )
			return false;

		foreach ( var only in SelectedEdges )
			edge = only;

		if ( edge.A < RetopoStart || edge.B < RetopoStart )
			return false;

		// Both ends are new, so only new faces can hold the edge — and this runs every frame the
		// ghost is drawn, over a sculpt that may have a million edges.
		var count = 0;

		for ( var f = Math.Max( _retopoFaceStart, 0 ); f < Mesh.FaceCount; f++ )
		{
			var idx = Mesh.Faces[f].Indices;
			for ( var i = 0; i < idx.Length; i++ )
			{
				if ( new EdgeKey( idx[i], idx[(i + 1) % idx.Length] ).Equals( edge ) )
				{
					owner = f;
					count++;
				}
			}
		}

		return count == 1;
	}

	/// <summary>
	/// One click of Poly Build, at <paramref name="point"/> on the surface being traced:
	///
	/// - With one open edge of the new mesh selected, the edge is extended to the click as a quad,
	///   and the far edge of that quad is selected — so clicking along a limb lays a strip, one click
	///   a face. This is most of retopology.
	/// - Otherwise a new vertex is dropped there and added to the selection. The fourth one closes a
	///   quad (the third, a triangle, only with <paramref name="closeTriangle"/>), and its last edge
	///   is selected, ready to extend.
	///
	/// A click that would make a twisted, inward-facing or overlapping face is refused with the
	/// reason, and changes nothing. <see cref="PlanPolyBuild"/> says so before the click.
	/// </summary>
	public void PolyBuild( Vec3 point, bool closeTriangle = false, float weld = 0f )
	{
		var plan = PlanPolyBuild( point, weld, closeTriangle );

		if ( !plan.IsValid )
			throw new InvalidOperationException( plan.Problem );

		Step( "Poly build", () =>
		{
			var ids = new int[plan.Corners.Length];

			for ( var i = 0; i < ids.Length; i++ )
				ids[i] = plan.Vertices[i] >= 0 ? plan.Vertices[i] : NewRetopoVertex( plan.Corners[i] );

			if ( !plan.ClosesFace )
			{
				if ( Mode != EditElement.Vertex )
				{
					ClearSelection();
					Mode = EditElement.Vertex;
				}

				SelectedVertices.Add( ids[0] );
				return;
			}

			var material = 0;
			if ( plan.Extends && Mesh.BuildEdgeFaces().TryGetValue( new EdgeKey( ids[0], ids[1] ), out var owners ) && owners.Count > 0 )
				material = Mesh.Faces[owners[0]].Material;

			Mesh.AddFace( ids, null, material );

			if ( MirrorX )
				MirrorRetopoFace( ids, material, weld );

			// Select the edge to carry on from: the far side of an extension, or on a fresh quad
			// the side made by the last click — where the hand already is.
			EdgeKey next;

			if ( plan.Extends )
				next = new EdgeKey( ids[2], ids[3] );
			else
			{
				var at = Math.Max( plan.ClickedCorner, 0 );
				next = new EdgeKey( ids[at], ids[(at + ids.Length - 1) % ids.Length] );
			}

				ClearSelection();
			Mode = EditElement.Edge;
			SelectedEdges.Add( next );
		}, tail: true );
	}

	/// <summary>With <see cref="MirrorX"/> on, a point this close to x = 0 goes onto it, so the
	/// two halves share the centre line instead of leaving a seam up the middle.</summary>
	Vec3 OnSeam( Vec3 p, float weld )
	{
		if ( MirrorX && MathF.Abs( p.x ) <= MathF.Max( weld, MirrorTolerance ) )
			return new Vec3( 0f, p.y, p.z );

		return p;
	}

	/// <summary>
	/// Build the mirror image of a face just drawn across x = 0. Corners on the centre line are
	/// shared; the others reuse a vertex already at the mirrored spot (from an earlier mirrored
	/// face), or get a new one dropped onto the surface there — which matters on a sculpt that is
	/// not quite symmetric. A face that straddles the centre line mirrors onto itself, so it is left
	/// alone, as is one whose mirror image is already there.
	/// </summary>
	void MirrorRetopoFace( int[] ids, int material, float weld )
	{
		var seam = MathF.Max( weld, MirrorTolerance );
		var left = false;
		var right = false;

		foreach ( var v in ids )
		{
			var x = Mesh.Positions[v].x;
			left |= x < -seam;
			right |= x > seam;
		}

		if ( left == right )
			return;

		var mirrored = new int[ids.Length];

		for ( var i = 0; i < ids.Length; i++ )
		{
			var p = Mesh.Positions[ids[i]];

			if ( MathF.Abs( p.x ) <= seam )
			{
				mirrored[i] = ids[i];
				continue;
			}

			var target = new Vec3( -p.x, p.y, p.z );
			var near = NearestRetopoVertex( target, seam, -1, -1 );

			if ( near < 0 )
			{
				var landed = OnSurface( target );
				near = NewRetopoVertex( new Vec3( MathF.Abs( landed.x ) <= seam ? 0f : landed.x, landed.y, landed.z ) );
			}

			mirrored[i] = near;
		}

		// Mirroring flips handedness, so the winding reverses to keep the face pointing outward.
		System.Array.Reverse( mirrored );

		var key = new List<int>( mirrored );
		key.Sort();

		for ( var f = Math.Max( _retopoFaceStart, 0 ); f < Mesh.FaceCount; f++ )
		{
			var other = new List<int>( Mesh.Faces[f].Indices );
			other.Sort();

			if ( other.Count != key.Count )
				continue;

			var same = true;
			for ( var i = 0; i < key.Count && same; i++ )
				same = other[i] == key[i];

			if ( same )
				return;
		}

		Mesh.AddFace( mirrored, null, material );
	}

	int NewRetopoVertex( Vec3 at )
	{
		var v = Mesh.AddVertex( at );

		if ( Mesh.Skin is not null && Mesh.Skin.Count == v )
			Mesh.Skin.Vertices.Add( new BoneWeight[0] ); // Copy weights gives it real ones.

		if ( Mesh.VertexColors is not null && Mesh.VertexColors.Length == v )
		{
			var colours = Mesh.VertexColors;
			System.Array.Resize( ref colours, v + 1 );
			colours[v] = new Vec4( 1f, 1f, 1f, 1f );
			Mesh.VertexColors = colours;
		}

		return v;
	}

	/// <summary>The vertex of the new topology nearest <paramref name="p"/> within
	/// <paramref name="radius"/>, skipping the ones given; -1 if none.</summary>
	int NearestRetopoVertex( Vec3 p, float radius, int skipA, int skipB, int skipC = -1 )
	{
		if ( radius <= 0f || !IsRetopologizing )
			return -1;

		var best = -1;
		var bestD = radius * radius;

		for ( var v = RetopoStart; v < Mesh.VertexCount; v++ )
		{
			if ( v == skipA || v == skipB || v == skipC )
				continue;

			var d = (Mesh.Positions[v] - p).LengthSquared;
			if ( d <= bestD )
			{
				bestD = d;
				best = v;
			}
		}

		return best;
	}

	/// <summary>
	/// Lay a whole strip of quads along a stroke across the surface — the drag version of
	/// <see cref="PolyBuild"/>, like the strips tool in Blender's retopology add-ons. The stroke is
	/// resampled every <paramref name="width"/>, so the quads come out square.
	///
	/// When the stroke starts on the open edge that is selected, the strip carries on from it (and
	/// takes that edge's length as its width when <paramref name="width"/> is 0). Its last edge is
	/// left selected, so the next stroke or click continues where this one ended. The whole strip is
	/// one undo step, and is refused as a whole — with the reason — if any quad of it would be
	/// twisted, point into the body, or cover a face already drawn.
	/// </summary>
	public void PolyBuildStroke( IReadOnlyList<Vec3> points, float width, float weld = 0f )
	{
		if ( !IsRetopologizing )
			throw new InvalidOperationException( "Start retopology first, over the body you want to trace." );

		if ( points is null || points.Count < 2 )
			throw new InvalidOperationException( "Drag a longer stroke — a strip needs somewhere to go." );

		var path = new List<Vec3>();
		foreach ( var p in points )
			path.Add( OnSurface( p ) );

		var continuing = ExtendableEdge( out var edge, out var owner );
		int first = -1, second = -1;

		if ( continuing )
		{
			var a = Mesh.Positions[edge.A];
			var b = Mesh.Positions[edge.B];
			var length = (a - b).Length;

			if ( ((a + b) * 0.5f - path[0]).Length > MathF.Max( length, width ) )
				continuing = false;
			else
			{
				if ( width <= 0f )
					width = length;

				// The owner runs the edge one way; the strip's first face must run it the other.
				var forward = RunsForward( Mesh.Faces[owner], edge.A, edge.B );
				first = forward ? edge.B : edge.A;
				second = forward ? edge.A : edge.B;
				path[0] = (a + b) * 0.5f;
			}
		}

		if ( width <= 0f )
			throw new InvalidOperationException( "The strip needs a width. Start it on a selected open edge, or give it one." );

		var samples = Resample( path, width );

		if ( samples.Count < 2 )
			throw new InvalidOperationException( "That stroke is shorter than one quad. Drag further." );

		// Left and right rails, square to the stroke in the surface's tangent plane.
		var left = new Vec3[samples.Count];
		var right = new Vec3[samples.Count];

		for ( var i = 0; i < samples.Count; i++ )
		{
			var ahead = samples[Math.Min( i + 1, samples.Count - 1 )] - samples[Math.Max( i - 1, 0 )];
			var normal = SurfaceNormalAt( samples[i] );
			var side = Vec3.Cross( normal, ahead );

			if ( side.LengthSquared < 1e-16f )
				throw new InvalidOperationException( "The stroke doubles back on itself. Drag in one direction." );

			side = side / side.Length * (width * 0.5f);
			left[i] = OnSeam( OnSurface( samples[i] + side ), weld );
			right[i] = OnSeam( OnSurface( samples[i] - side ), weld );
		}

		if ( continuing )
		{
			// Whichever way round the edge sits, the strip's first face has to share it.
			var a = Mesh.Positions[first];
			var b = Mesh.Positions[second];
			var swap = (a - left[0]).LengthSquared + (b - right[0]).LengthSquared > (a - right[0]).LengthSquared + (b - left[0]).LengthSquared;

			if ( swap )
			{
				// The edge is wound against the stroke: the strip would face into the body.
				throw new InvalidOperationException( "That strip would face into the body. Start it on the other side of the edge, or drag the other way." );
			}

			left[0] = a;
			right[0] = b;
		}

		// Check every quad before building any of them.
		for ( var i = 0; i + 1 < samples.Count; i++ )
		{
			var problem = FaceProblem( new[] { left[i], right[i], right[i + 1], left[i + 1] }, SurfaceNormalAt( samples[i] ) );
			if ( problem is not null )
				throw new InvalidOperationException( $"Quad {i + 1} of the strip: {problem}" );
		}

		Step( "Poly build strip", () =>
		{
			var l = new int[samples.Count];
			var r = new int[samples.Count];

			int Place( Vec3 p )
			{
				var near = NearestRetopoVertex( p, weld, -1, -1 );
				return near >= 0 ? near : NewRetopoVertex( p );
			}

			for ( var i = 0; i < samples.Count; i++ )
			{
				if ( i == 0 && continuing )
				{
					l[0] = first;
					r[0] = second;
					continue;
				}

				l[i] = Place( left[i] );
				r[i] = Place( right[i] );
			}

			var material = continuing ? Mesh.Faces[owner].Material : 0;

			for ( var i = 0; i + 1 < samples.Count; i++ )
			{
				var face = new[] { l[i], r[i], r[i + 1], l[i + 1] };

				// A weld that folded two corners together would make a degenerate face; drop it.
				if ( face[0] == face[3] || face[1] == face[2] || face[0] == face[1] || face[2] == face[3] )
					continue;

				Mesh.AddFace( face, null, material );

				if ( MirrorX )
					MirrorRetopoFace( face, material, weld );
			}

			ClearSelection();
			Mode = EditElement.Edge;
			SelectedEdges.Add( new EdgeKey( l[^1], r[^1] ) );
		}, tail: true );
	}

	/// <summary>Points along a polyline every <paramref name="spacing"/>, from its start; the end
	/// is kept only if the last stretch is at least half a spacing long.</summary>
	static List<Vec3> Resample( List<Vec3> path, float spacing )
	{
		var result = new List<Vec3> { path[0] };
		var carried = 0f;

		for ( var i = 1; i < path.Count; i++ )
		{
			var from = path[i - 1];
			var to = path[i];
			var length = (to - from).Length;

			while ( carried + length >= spacing && length > 1e-9f )
			{
				var t = (spacing - carried) / length;
				from = Vec3.Lerp( from, to, t );
				result.Add( from );
				length = (to - from).Length;
				carried = 0f;
			}

			carried += length;
		}

		if ( carried >= spacing * 0.5f )
			result.Add( path[^1] );

		return result;
	}

	/// <summary>
	/// Even out the new topology and put it back on the surface — Blender's Relax with snapping
	/// on. Retopology drawn by hand is lumpy; this spreads the vertices evenly while keeping every
	/// one of them on the body. Works on the selection, or on all the new topology when nothing is
	/// selected. Open rims are held, as <see cref="Smooth"/> holds them.
	/// </summary>
	public void RelaxRetopo( float strength = 0.5f, int iterations = 4 )
	{
		if ( !IsRetopologizing )
			throw new InvalidOperationException( "Relax works on retopology. Start retopology first." );

		var moving = AffectedVertices();
		moving.RemoveWhere( v => v < RetopoStart );

		if ( moving.Count == 0 )
			for ( var v = RetopoStart; v < Mesh.VertexCount; v++ )
				moving.Add( v );

		if ( moving.Count == 0 )
			throw new InvalidOperationException( "There is no new topology to relax yet." );

		strength = Math.Clamp( strength, 0f, 1f );
		iterations = Math.Clamp( iterations, 1, 100 );

		Step( "Relax", () =>
		{
			var neighbours = VertexNeighbours();
			var boundary = BoundaryVertexSet();
			var scratch = new Vec3[Mesh.VertexCount];

			for ( var pass = 0; pass < iterations; pass++ )
			{
				Mesh.Positions.CopyTo( scratch );

				foreach ( var v in moving )
				{
					if ( boundary.Contains( v ) || !neighbours.TryGetValue( v, out var around ) || around.Count == 0 )
						continue;

					var sum = Vec3.Zero;
					foreach ( var n in around )
						sum += scratch[n];

					Mesh.Positions[v] = OnSurface( Vec3.Lerp( scratch[v], sum / around.Count, strength ) );
				}
			}
		}, tail: true );
	}

	/// <summary>
	/// Finish retopology: lift the new topology out as a body of its own (it appears on Finish,
	/// like Separate) and leave the traced body exactly as it was. Loose vertices that never became
	/// a face are dropped.
	/// </summary>
	public void SeparateRetopo()
	{
		if ( !IsRetopologizing )
			throw new InvalidOperationException( "Start retopology first." );

		var faces = new List<int>();
		for ( var f = 0; f < Mesh.FaceCount; f++ )
		{
			var idx = Mesh.Faces[f].Indices;
			var fresh = true;

			foreach ( var v in idx )
				if ( v < RetopoStart ) { fresh = false; break; }

			if ( fresh )
				faces.Add( f );
		}

		if ( faces.Count == 0 )
			throw new InvalidOperationException( "No new faces yet — click on the body to lay some down." );

		Step( "Separate retopology", () =>
		{
			// TakeFaces compacts what is left, which drops any vertex that was drawn and never
			// closed into a face — it was never part of the body.
			Separated.Add( TakeFaces( faces ) );

			ClearSelection();
			} );

		EndRetopo();
	}

	// --- hard edges ------------------------------------------------------------------------------

	/// <summary>
	/// Make edges shade hard, by splitting the mesh along them — Blender's Edge Split.
	///
	/// HOW SHADING ACTUALLY WORKS HERE, because it decides the design. Nothing stores "this edge is
	/// sharp": <see cref="MeshNormals.ComputeCornerNormals"/> averages a vertex's faces only while
	/// they agree to within the smoothing angle, and the exporters do the same on the way out. So an
	/// edge is hard when the faces across it either turn sharply enough, or DO NOT SHARE VERTICES.
	/// Splitting is therefore not a workaround for a missing flag — it is the representation, and it
	/// survives save, export and every other operation with nothing to plumb.
	///
	/// The cost is honest and worth saying: vertex count goes up, and the mesh is no longer welded
	/// along those edges, so it will read as open there. Do this as a finishing step, the way Blender
	/// makes it a modifier. <see cref="MergeByDistance"/> puts it back.
	/// </summary>
	/// <returns>How many edges were split.</returns>
	public int SplitEdges()
	{
		if ( SelectedEdges.Count == 0 )
			throw new InvalidOperationException( "Split needs edges selected. Switch to Edge mode (2) and pick the edges that should shade hard." );

		return SplitEdgeSet( new HashSet<EdgeKey>( SelectedEdges ), "Split edges" );
	}

	/// <summary>
	/// Split every edge whose two faces turn more than <paramref name="angleDegrees"/> apart — the
	/// one-click version of <see cref="SplitEdges"/> for a model that should read as hard-surface
	/// everywhere it creases. Below the angle nothing happens, so a smooth limb is left alone.
	/// </summary>
	/// <returns>How many edges were split.</returns>
	public int SplitEdgesByAngle( float angleDegrees )
	{
		var limit = MathF.Cos( Math.Clamp( angleDegrees, 0f, 180f ) * MathF.PI / 180f );
		var sharp = new HashSet<EdgeKey>();

		foreach ( var (key, faces) in Mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 2 )
				continue;

			var a = Mesh.FaceNormal( Mesh.Faces[faces[0]] );
			var b = Mesh.FaceNormal( Mesh.Faces[faces[1]] );

			if ( a.LengthSquared < 1e-16f || b.LengthSquared < 1e-16f )
				continue;

			if ( Vec3.Dot( a / a.Length, b / b.Length ) < limit )
				sharp.Add( key );
		}

		if ( sharp.Count == 0 )
			throw new InvalidOperationException( $"No edge on this body turns more than {angleDegrees:0.#}°. Lower the angle to catch softer creases." );

		return SplitEdgeSet( sharp, "Split by angle" );
	}

	/// <summary>
	/// Unweld a set of edges: every face keeps its own copy of the vertices along them, so the two
	/// sides no longer share a normal.
	///
	/// A vertex is duplicated per CONNECTED GROUP of faces around it — the faces you can still walk
	/// between without crossing a split edge. A vertex in the middle of a long sharp crease has two
	/// such groups and becomes two vertices; a vertex where four sharp edges meet becomes four. A
	/// vertex touched by a split edge but whose fan is still connected the long way round is left
	/// alone, because splitting it would change nothing and only cost a vertex.
	/// </summary>
	int SplitEdgeSet( HashSet<EdgeKey> edges, string label )
	{
		var split = 0;

		Step( label, () =>
		{
			var source = Mesh;
			var faces = VertexFaces.Build( source );
			var result = source.Clone();
			var rigged = source.IsRigged;

			// face -> (old vertex -> new vertex), filled in only where a copy was needed.
			var rewrite = new Dictionary<int, Dictionary<int, int>>();

			for ( var v = 0; v < source.VertexCount; v++ )
			{
				if ( faces.CountAt( v ) < 2 )
					continue;

				// Copied out of the span: FanGroups is an ordinary method and a span cannot cross
				// into one that also allocates.
				var fan = new List<int>( faces.CountAt( v ) );
				foreach ( var f in faces[v] )
					fan.Add( f );

				var groups = FanGroups( source, v, fan, edges );

				if ( groups.Count < 2 )
					continue;

				// The first group keeps the original vertex; the rest get copies.
				for ( var g = 1; g < groups.Count; g++ )
				{
					var copy = result.VertexCount;
					result.AddVertex( source.Positions[v] );

					if ( rigged )
						result.Skin.Vertices.Add( source.Skin[v] );

					foreach ( var f in groups[g] )
					{
						if ( !rewrite.TryGetValue( f, out var map ) )
							rewrite[f] = map = new Dictionary<int, int>();

						map[v] = copy;
					}
				}
			}

			foreach ( var (f, map) in rewrite )
			{
				var indices = result.Faces[f].Indices;

				for ( var i = 0; i < indices.Length; i++ )
					if ( map.TryGetValue( indices[i], out var moved ) )
						indices[i] = moved;
			}

			// An edge counts as split only if the two faces across it no longer name the same pair of
			// vertices. Asking whether the old EdgeKey survived does not answer that: one side keeps
			// the original vertices, so the key is still there whether or not the other side moved.
			var edgeFaces = source.BuildEdgeFaces();

			foreach ( var key in edges )
			{
				if ( !edgeFaces.TryGetValue( key, out var across ) || across.Count != 2 )
					continue;

				int Moved( int face, int vertex ) =>
					rewrite.TryGetValue( face, out var map ) && map.TryGetValue( vertex, out var to ) ? to : vertex;

				var a0 = Moved( across[0], key.A );
				var b0 = Moved( across[0], key.B );
				var a1 = Moved( across[1], key.A );
				var b1 = Moved( across[1], key.B );

				if ( a0 != a1 || b0 != b1 )
					split++;
			}

			Mesh = result;
			ClearSelection();
		} );

		return split;
	}

	/// <summary>
	/// The faces around a vertex, grouped by which ones you can still walk between without crossing
	/// one of <paramref name="edges"/>. Two faces are neighbours here when they share an edge that
	/// uses this vertex and is not being split.
	/// </summary>
	static List<List<int>> FanGroups( PolyMesh mesh, int vertex, IReadOnlyList<int> fan, HashSet<EdgeKey> edges )
	{
		// The other end of each of this vertex's edges, per face, so faces can be linked by them.
		var linkedBy = new Dictionary<EdgeKey, List<int>>();

		foreach ( var f in fan )
		{
			var idx = mesh.Faces[f].Indices;

			for ( var i = 0; i < idx.Length; i++ )
			{
				var a = idx[i];
				var b = idx[(i + 1) % idx.Length];

				if ( a != vertex && b != vertex )
					continue;

				var key = new EdgeKey( a, b );

				if ( edges.Contains( key ) )
					continue;

				if ( !linkedBy.TryGetValue( key, out var list ) )
					linkedBy[key] = list = new List<int>();

				list.Add( f );
			}
		}

		var parent = new Dictionary<int, int>();

		int Find( int f )
		{
			while ( parent[f] != f )
				f = parent[f] = parent[parent[f]];
			return f;
		}

		foreach ( var f in fan )
			parent[f] = f;

		foreach ( var (_, sharing) in linkedBy )
			for ( var i = 1; i < sharing.Count; i++ )
			{
				var a = Find( sharing[0] );
				var b = Find( sharing[i] );

				if ( a != b )
					parent[b] = a;
			}

		var byRoot = new Dictionary<int, List<int>>();

		// Ordered by face index so the same mesh always splits the same way.
		foreach ( var f in fan )
		{
			var root = Find( f );

			if ( !byRoot.TryGetValue( root, out var list ) )
				byRoot[root] = list = new List<int>();

			list.Add( f );
		}

		return new List<List<int>>( byRoot.Values );
	}

	// --- shrink/fatten and selection growing -----------------------------------------------------

	/// <summary>
	/// Move the selected vertices along their own normals — Blender's Shrink/Fatten, and the way you
	/// thicken a limb or pull a surface in without touching its shape. Positive fattens, negative
	/// shrinks.
	///
	/// The normal is the vertex normal of the WHOLE mesh, not of the selection, so fattening part of
	/// an arm pushes it out along the arm's real surface rather than along the average of the few
	/// faces that happened to be picked. Mirror and snap apply, as they do to a drag.
	/// </summary>
	public void ShrinkFatten( float distance )
	{
		var moving = AffectedVertices();

		if ( moving.Count == 0 )
			throw new InvalidOperationException( "Shrink/Fatten needs a selection. Pick the vertices, edges or faces to move along their normals." );

		Step( "Shrink/Fatten", () =>
		{
			var normals = Mesh.ComputeVertexNormals();
			var from = new Dictionary<int, Vec3>();

			foreach ( var v in moving )
				from[v] = Mesh.Positions[v];

			foreach ( var v in moving )
			{
				if ( v >= normals.Length )
					continue;

				var n = normals[v];

				if ( n.LengthSquared < 1e-12f )
					continue;

				PlaceMoved( v, from[v] + n / n.Length * distance, from );
			}
		} );
	}

	/// <summary>
	/// Put a vertex somewhere, honouring mirror and snap the way a drag does. Shared by
	/// <see cref="ShrinkFatten"/> so a tool that moves vertices behaves like every other tool that
	/// moves vertices.
	/// </summary>
	void PlaceMoved( int vertex, Vec3 to, Dictionary<int, Vec3> from )
	{
		if ( _snapTree is not null && _snapTarget is not null )
		{
			if ( _snapTree.NearestSurface( _snapTarget, to, SnapMaxDistance ) is { } hit )
				to = hit.Point + hit.Normal * SnapOffset;
		}

		if ( MirrorX )
		{
			var startX = from.TryGetValue( vertex, out var start ) ? start.x : Mesh.Positions[vertex].x;
			if ( MathF.Abs( startX ) <= MirrorTolerance || MathF.Abs( to.x ) <= MirrorTolerance || (startX > 0f && to.x < 0f) || (startX < 0f && to.x > 0f) )
				to = new Vec3( 0f, to.y, to.z );
		}

		Mesh.Positions[vertex] = to;

		if ( !MirrorX )
			return;

		var partner = MirrorPartner( vertex );

		if ( partner >= 0 && partner != vertex )
			Mesh.Positions[partner] = new Vec3( -to.x, to.y, to.z );
	}

	/// <summary>
	/// Grow the selection by one ring — Blender's Select More. Every element touching what is
	/// already selected joins it, which is how you go from one clicked face to a whole region
	/// without dragging a box over geometry you cannot see.
	/// </summary>
	public void GrowSelection()
	{
		var touched = AffectedVertices();

		if ( touched.Count == 0 )
			throw new InvalidOperationException( "Select something first, then grow it." );

		var neighbours = VertexNeighbours();
		var wider = new HashSet<int>( touched );

		foreach ( var v in touched )
			if ( neighbours.TryGetValue( v, out var around ) )
				wider.UnionWith( around );

		SelectByVertices( wider );
	}

	/// <summary>
	/// Shrink the selection by one ring — Blender's Select Less. Anything on the selection's border
	/// drops out, leaving its interior.
	/// </summary>
	public void ShrinkSelection()
	{
		var touched = AffectedVertices();

		if ( touched.Count == 0 )
			throw new InvalidOperationException( "There is no selection to shrink." );

		var neighbours = VertexNeighbours();
		var inner = new HashSet<int>();

		foreach ( var v in touched )
		{
			// A vertex stays only if everything around it was selected too - that is what "not on
			// the border" means.
			if ( !neighbours.TryGetValue( v, out var around ) )
				continue;

			var interior = true;

			foreach ( var n in around )
				if ( !touched.Contains( n ) ) { interior = false; break; }

			if ( interior )
				inner.Add( v );
		}

		SelectByVertices( inner );
	}

	/// <summary>
	/// Re-express a set of vertices as a selection in the current mode: the vertices themselves, the
	/// edges with both ends in the set, or the faces with every corner in it.
	/// </summary>
	void SelectByVertices( HashSet<int> vertices )
	{
		SelectedVertices.Clear();
		SelectedEdges.Clear();
		SelectedFaces.Clear();

		switch ( Mode )
		{
			case EditElement.Vertex:
				SelectedVertices.UnionWith( vertices );
				break;

			case EditElement.Edge:
				foreach ( var key in Mesh.BuildEdgeFaces().Keys )
					if ( vertices.Contains( key.A ) && vertices.Contains( key.B ) )
						SelectedEdges.Add( key );
				break;

			default:
				for ( var f = 0; f < Mesh.FaceCount; f++ )
				{
					var whole = true;

					foreach ( var v in Mesh.Faces[f].Indices )
						if ( !vertices.Contains( v ) ) { whole = false; break; }

					if ( whole )
						SelectedFaces.Add( f );
				}
				break;
		}

		SelectionRevision++;
	}

	// --- skin weights ----------------------------------------------------------------------------

	/// <summary>
	/// Make the weights what the exporter will actually use: at most
	/// <paramref name="maxInfluences"/> bones per vertex, strongest first, summing to one.
	///
	/// WHY THIS IS A TOOL AND NOT A DETAIL. `DmxWriter` and `FbxWriter` both cap at four influences
	/// on the way out, silently. A vertex carrying six therefore deforms one way in the editor and
	/// another way in the game, and nothing tells you. Running this makes the two agree, and the
	/// count it returns is how many vertices were being exported differently from what you saw.
	/// </summary>
	/// <returns>How many vertices changed.</returns>
	public int NormalizeWeights( int maxInfluences = 4 )
	{
		if ( !Mesh.IsRigged )
			throw new InvalidOperationException( "This body has no skin weights. Bind it to a rig first." );

		if ( maxInfluences < 1 )
			throw new ArgumentOutOfRangeException( nameof( maxInfluences ) );

		var changed = 0;

		Step( "Normalize weights", () =>
		{
			var skin = Mesh.Skin;

			for ( var v = 0; v < Mesh.VertexCount && v < skin.Count; v++ )
			{
				var before = skin.Vertices[v];
				var after = SkinWeights.Prune( before, maxInfluences );

				if ( !SameWeights( before, after ) )
					changed++;

				skin.Vertices[v] = after;
			}
		} );

		return changed;
	}

	static bool SameWeights( BoneWeight[] a, BoneWeight[] b )
	{
		if ( a is null || b is null || a.Length != b.Length )
			return false;

		for ( var i = 0; i < a.Length; i++ )
			if ( a[i].Bone != b[i].Bone || MathF.Abs( a[i].Weight - b[i].Weight ) > 1e-6f )
				return false;

		return true;
	}

	/// <summary>
	/// Relax skin weights across the mesh's edges — the fix for the hard crease a bound elbow gets
	/// where one ring of vertices is all upper arm and the next is all forearm. Each pass blends a
	/// vertex's weights toward the average of its neighbours' by <paramref name="strength"/>, then
	/// everything is pruned back to <paramref name="maxInfluences"/> so smoothing cannot quietly
	/// push a vertex past what the exporter will carry.
	///
	/// Only the selected vertices are touched, so a bad shoulder can be fixed without disturbing a
	/// hand that was painted deliberately. With nothing selected it smooths the whole body.
	/// </summary>
	public void SmoothWeights( float strength = 0.5f, int iterations = 3, int maxInfluences = 4 )
	{
		if ( !Mesh.IsRigged )
			throw new InvalidOperationException( "This body has no skin weights. Bind it to a rig first." );

		var moving = AffectedVertices();

		if ( moving.Count == 0 )
		{
			moving = new HashSet<int>();
			for ( var i = 0; i < Mesh.VertexCount; i++ )
				moving.Add( i );
		}

		strength = Math.Clamp( strength, 0f, 1f );
		iterations = Math.Clamp( iterations, 1, 50 );

		Step( "Smooth weights", () =>
		{
			var neighbours = VertexNeighbours();
			var skin = Mesh.Skin;

			for ( var pass = 0; pass < iterations; pass++ )
			{
				// Same rule as geometry smoothing: read the whole frame before writing any of it, or
				// the result depends on the order vertices happen to be numbered in.
				var frame = new BoneWeight[skin.Count][];
				for ( var i = 0; i < skin.Count; i++ )
					frame[i] = skin.Vertices[i];

				foreach ( var v in moving )
				{
					if ( v >= skin.Count || !neighbours.TryGetValue( v, out var around ) || around.Count == 0 )
						continue;

					var terms = new List<(BoneWeight[], float)>( around.Count + 1 )
					{
						(frame[v], 1f - strength),
					};

					var share = strength / around.Count;

					foreach ( var n in around )
						if ( n < frame.Length )
							terms.Add( (frame[n], share) );

					skin.Vertices[v] = SkinWeights.Prune( SkinWeights.Blend( terms ), maxInfluences );
				}
			}
		} );
	}

	/// <summary>
	/// Copy skin weights from one side of the model to the other, swapping left and right bones —
	/// the step that follows <see cref="Symmetrize"/>, which makes the geometry symmetric but leaves
	/// the weights as they were.
	///
	/// <paramref name="bonePartner"/> maps a bone to the one it mirrors onto, and is the caller's
	/// job because only the caller knows the rig's naming: on the Citizen that is the L/R pairs, and
	/// a bone with no partner (a spine, the head) maps to itself.
	///
	/// A vertex is matched to the one at its mirrored position within <see cref="MirrorTolerance"/>.
	/// Vertices on the plane keep their own weights, with the bones swapped — a vertex on the
	/// centre line is weighted to spine bones, which are their own partners, so in practice that
	/// leaves them alone.
	/// </summary>
	/// <returns>How many vertices received mirrored weights.</returns>
	public int MirrorWeights( Func<int, int> bonePartner, bool fromPositive = true )
	{
		if ( !Mesh.IsRigged )
			throw new InvalidOperationException( "This body has no skin weights. Bind it to a rig first." );

		if ( bonePartner is null )
			throw new ArgumentNullException( nameof( bonePartner ) );

		var copied = 0;

		Step( "Mirror weights", () =>
		{
			var skin = Mesh.Skin;
			var sign = fromPositive ? 1f : -1f;

			// Position -> vertex, so a mirrored position can be looked up rather than searched for.
			// Quantised to the tolerance, which is what "the same point" means here anyway.
			var scale = 1f / MathF.Max( MirrorTolerance, 1e-6f );
			var byPosition = new Dictionary<(int, int, int), int>();

			(int, int, int) Key( Vec3 p ) => (
				(int)MathF.Round( p.x * scale ),
				(int)MathF.Round( p.y * scale ),
				(int)MathF.Round( p.z * scale ));

			for ( var v = 0; v < Mesh.VertexCount; v++ )
				byPosition[Key( Mesh.Positions[v] )] = v;

			var updated = new BoneWeight[skin.Count][];
			for ( var i = 0; i < skin.Count; i++ )
				updated[i] = skin.Vertices[i];

			for ( var v = 0; v < Mesh.VertexCount && v < skin.Count; v++ )
			{
				var p = Mesh.Positions[v];

				// Only the receiving side is written.
				if ( p.x * sign >= -MirrorTolerance )
					continue;

				if ( !byPosition.TryGetValue( Key( new Vec3( -p.x, p.y, p.z ) ), out var source ) || source >= skin.Count )
					continue;

				var from = skin.Vertices[source];
				var swapped = new BoneWeight[from.Length];

				for ( var i = 0; i < from.Length; i++ )
					swapped[i] = new BoneWeight( bonePartner( from[i].Bone ), from[i].Weight );

				updated[v] = swapped;
				copied++;
			}

			for ( var i = 0; i < skin.Count; i++ )
				skin.Vertices[i] = updated[i];
		} );

		return copied;
	}

	// --- smoothing and symmetry ------------------------------------------------------------------

	/// <summary>
	/// Relax the selected vertices toward the average of their neighbours — Blender's Smooth
	/// Vertices, and what you reach for straight after a subdivide, when the new density is there
	/// but the surface still has the old faceting in it.
	///
	/// Laplacian, applied <paramref name="iterations"/> times at <paramref name="strength"/> each:
	/// several gentle passes relax a surface much more evenly than one hard one, and a hard one
	/// collapses detail rather than smoothing it. With nothing selected it relaxes the whole mesh.
	///
	/// BOUNDARY VERTICES DO NOT MOVE. On an open mesh — a garment panel, a face that has been split
	/// off — the rim is the silhouette, and averaging it pulls the shape in. Vertices on the
	/// mirror plane stay on it when <see cref="MirrorX"/> is on, for the same reason.
	/// </summary>
	public void Smooth( float strength = 0.5f, int iterations = 4 )
	{
		var moving = AffectedVertices();

		if ( moving.Count == 0 )
		{
			moving = new HashSet<int>();
			for ( var i = 0; i < Mesh.VertexCount; i++ )
				moving.Add( i );
		}

		if ( moving.Count == 0 )
			throw new InvalidOperationException( "There is nothing to smooth." );

		strength = Math.Clamp( strength, 0f, 1f );
		iterations = Math.Clamp( iterations, 1, 100 );

		Step( "Smooth", () =>
		{
			var neighbours = VertexNeighbours();
			var boundary = BoundaryVertexSet();
			var positions = Mesh.Positions;
			var scratch = new Vec3[positions.Count];

			for ( var pass = 0; pass < iterations; pass++ )
			{
				// Read the whole frame before writing any of it, or a vertex relaxes toward
				// neighbours that have already moved this pass and the result depends on index order.
				for ( var i = 0; i < positions.Count; i++ )
					scratch[i] = positions[i];

				foreach ( var v in moving )
				{
					if ( boundary.Contains( v ) || !neighbours.TryGetValue( v, out var around ) || around.Count == 0 )
						continue;

					var sum = Vec3.Zero;
					foreach ( var n in around )
						sum += scratch[n];

					var target = Vec3.Lerp( scratch[v], sum / around.Count, strength );

					// On the mirror plane a vertex may relax along the plane but never off it,
					// or smoothing opens a seam up the middle of the model.
					if ( MirrorX && MathF.Abs( scratch[v].x ) <= MirrorTolerance )
						target = new Vec3( 0f, target.y, target.z );

					positions[v] = target;
				}
			}
		} );
	}

	/// <summary>Vertices on an open rim: an edge used by only one face.</summary>
	HashSet<int> BoundaryVertexSet()
	{
		var result = new HashSet<int>();

		foreach ( var (key, faces) in Mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 1 )
				continue;

			result.Add( key.A );
			result.Add( key.B );
		}

		return result;
	}

	/// <summary>
	/// Make the model symmetric across x = 0 by throwing one half away and replacing it with a
	/// mirror of the other. Characters are symmetric, and an hour of modelling one side is worth
	/// more than an hour of matching the other side to it by hand.
	///
	/// This is not <see cref="MirrorX"/>, which edits both halves at once and needs them to already
	/// match. This makes them match.
	///
	/// Vertices within <see cref="MirrorTolerance"/> of the plane are shared by both halves rather
	/// than duplicated, and are snapped exactly onto it — so the result is one welded mesh, not two
	/// halves touching. Skin weights come across with their bone indices unchanged, which is right
	/// for a symmetric rig and wrong for a rig whose left and right bones differ; those need their
	/// weights re-bound afterwards.
	/// </summary>
	/// <param name="keepPositive">Keep the +x half and mirror it onto -x, or the other way round.</param>
	public void Symmetrize( bool keepPositive = true )
	{
		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "There is nothing to symmetrize." );

		Step( "Symmetrize", () =>
		{
			// Cut along the mirror plane FIRST. Without this, any face straddling x = 0 — four of a
			// plain box's six — belongs to neither half and is simply dropped, which is how a
			// symmetric 2x2x2 box came out with a third of its volume. Cutting puts vertices exactly
			// on the plane, so every face afterwards lies wholly on one side.
			CutPlane( Vec3.Zero, new Vec3( 1f, 0f, 0f ), null, null );

			var source = Mesh;
			var sign = keepPositive ? 1f : -1f;
			var result = new PolyMesh();
			var rigged = source.IsRigged;

			if ( rigged )
				result.Skin = new SkinWeights();

			// Kept vertices: everything on the kept side, plus everything on the plane.
			var map = new Dictionary<int, int>();
			var mirrored = new Dictionary<int, int>();

			int Emit( int v, bool flip )
			{
				var p = source.Positions[v];
				var index = result.VertexCount;

				result.AddVertex( flip ? new Vec3( -p.x, p.y, p.z ) : p );

				if ( rigged )
					result.Skin.Vertices.Add( source.Skin[v] );

				return index;
			}

			// The cut leaves vertices sitting exactly on the plane; anything within the tolerance of
			// it counts as on it.
			var plane = MathF.Max( MirrorTolerance, MathF.Max( source.BoundsDiagonal, 1e-3f ) * 1e-4f );

			for ( var v = 0; v < source.VertexCount; v++ )
			{
				var x = source.Positions[v].x;

				if ( MathF.Abs( x ) <= plane )
				{
					// On the plane: one vertex, shared, snapped exactly onto it.
					var index = result.VertexCount;
					var p = source.Positions[v];
					result.AddVertex( new Vec3( 0f, p.y, p.z ) );

					if ( rigged )
						result.Skin.Vertices.Add( source.Skin[v] );

					map[v] = index;
					mirrored[v] = index;
					continue;
				}

				if ( x * sign > 0f )
				{
					map[v] = Emit( v, false );
					mirrored[v] = Emit( v, true );
				}
			}

			var kept = 0;

			foreach ( var face in source.Faces )
			{
				var complete = true;
				foreach ( var v in face.Indices )
					if ( !map.ContainsKey( v ) ) { complete = false; break; }

				if ( !complete )
					continue;

				kept++;

				var indices = new int[face.Indices.Length];
				for ( var i = 0; i < indices.Length; i++ )
					indices[i] = map[face.Indices[i]];

				result.AddFace( indices, (Vec2[])face.UVs.Clone(), face.Material );

				// The mirror image, wound backwards so it faces out rather than in.
				var flipped = new int[face.Indices.Length];
				var flippedUVs = new Vec2[face.UVs.Length];

				for ( var i = 0; i < flipped.Length; i++ )
				{
					var from = face.Indices.Length - 1 - i;
					flipped[i] = mirrored[face.Indices[from]];
					flippedUVs[i] = face.UVs[from];
				}

				result.AddFace( flipped, flippedUVs, face.Material );
			}

			if ( kept == 0 )
				throw new InvalidOperationException( $"Nothing lies on the {(keepPositive ? "+X" : "-X")} side to mirror. Move the model so the half you want to keep is there, or symmetrize the other way." );

			Mesh = RemoveUnusedVertices( result );
			ClearSelection();
		} );
	}

	// --- subdivision -----------------------------------------------------------------------------

	/// <summary>
	/// Add density where the detail is going. With faces selected this splits only those — linearly,
	/// at midpoints and centroids, so the shape you were looking at does not move and the neighbours
	/// are stitched rather than left with T-junctions. With NOTHING selected it runs the full
	/// Catmull-Clark on the whole body, smoothing included, which is the other thing "subdivide"
	/// reasonably means.
	///
	/// Skin weights blend through both, so subdividing a rigged body keeps it rigged — which is the
	/// point of doing this to a playermodel rather than to a prop.
	///
	/// The new faces become the selection, so a subdivide can be followed straight away by another,
	/// or by a sculpt, without re-picking the region.
	/// </summary>
	public void Subdivide( int levels = 1 )
	{
		if ( levels < 1 )
			throw new ArgumentOutOfRangeException( nameof( levels ), "subdivide at least once" );

		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "There is nothing to subdivide." );

		var region = new List<int>( SelectedFaces );

		Step( region.Count > 0 ? "Subdivide" : "Subdivide all", () =>
		{
			if ( region.Count == 0 )
			{
				Mesh = CatmullClark.Subdivide( Mesh, levels );
				ClearSelection();
				return;
			}

			Mesh = CatmullClark.SubdivideFaces( Mesh, region, levels, out var became );

			// The subdivided faces are emitted in place of their originals, not appended, so the
			// region has to come back from the operation rather than be guessed from a face count.
			SelectedVertices.Clear();
			SelectedEdges.Clear();
			SelectedFaces.Clear();
			SelectedFaces.UnionWith( became );
			Mode = EditElement.Face;
			SelectionRevision++;
		} );
	}

	/// <summary>
	/// What <see cref="Subdivide"/> would cost, so the editor can say so before it happens. A
	/// subdivide is the one operation in the tool that can turn a workable mesh into an unworkable
	/// one in a single click, and a count is cheaper to read than an undo.
	/// </summary>
	public (int Vertices, int Faces) PredictSubdivide( int levels = 1 )
	{
		if ( levels < 1 )
			return (Mesh.VertexCount, Mesh.FaceCount);

		return SelectedFaces.Count > 0
			? CatmullClark.PredictLocalCost( Mesh, SelectedFaces, levels )
			: CatmullClark.PredictCost( Mesh, levels );
	}

	// --- seams and unwrapping --------------------------------------------------------------------

	/// <summary>
	/// Mark the selected edges as seams, or unmark them. Seams do not move a vertex or change a
	/// face, so this is one undo step over the marks alone — the mesh is untouched until
	/// <see cref="Unwrap"/> runs.
	/// </summary>
	public void MarkSeam( bool marked = true )
	{
		if ( SelectedEdges.Count == 0 )
			throw new InvalidOperationException( "Marking a seam needs edges selected. Switch to Edge mode (2) and pick the edges to cut along — Alt+click takes a whole loop." );

		Step( marked ? "Mark seam" : "Clear seam", () =>
		{
			foreach ( var edge in SelectedEdges )
			{
				if ( marked )
					Seams.Add( edge );
				else
					Seams.Remove( edge );
			}
		} );
	}

	/// <summary>Forget every seam, without touching the mesh.</summary>
	public void ClearSeams()
	{
		if ( Seams.Count == 0 )
			return;

		Step( "Clear seams", Seams.Clear );
	}

	/// <summary>
	/// Lay the mesh out flat in the unit square so it can be painted or baked, cutting it at
	/// <see cref="Seams"/> and wherever the surface turns more than <paramref name="angleDegrees"/>.
	/// Only the UVs change — no vertex moves and no face is added — so the selection survives.
	/// </summary>
	/// <returns>What the unwrap produced: charts, faces laid out, and any it had to skip.</returns>
	public UnwrapReport Unwrap( float angleDegrees = 66f, float margin = 0.01f )
	{
		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "There is nothing to unwrap." );

		UnwrapReport report = null;
		Step( "Unwrap", () => report = UVUnwrap.Unwrap( Mesh, angleDegrees, margin, Seams ) );
		return report;
	}

	/// <summary>
	/// The seams that are actually on the mesh right now. An operation that removes faces can strand
	/// a mark on an edge that no longer exists; those are dropped rather than quietly steering a
	/// later unwrap from nowhere.
	/// </summary>
	public int PruneSeams()
	{
		if ( Seams.Count == 0 )
			return 0;

		var live = new HashSet<EdgeKey>( Mesh.BuildEdgeFaces().Keys );
		var dead = new List<EdgeKey>();

		foreach ( var seam in Seams )
			if ( !live.Contains( seam ) )
				dead.Add( seam );

		foreach ( var seam in dead )
			Seams.Remove( seam );

		return dead.Count;
	}

	// --- bridge ---------------------------------------------------------------------------------

	/// <summary>
	/// Join two open edge loops with a band of faces, Blender's Bridge Edge Loops — including loops
	/// with different numbers of edges, which the equal-count kernel bridge refuses. The two rims
	/// are walked together by how far round each one they are, so a quad goes where both advance
	/// together and a triangle takes up the difference where one runs ahead.
	/// </summary>
	public void BridgeSelectedLoops()
	{
		var loops = SelectedBoundaryLoops();

		if ( loops.Count != 2 )
			throw new InvalidOperationException( $"Bridge needs exactly two open loops selected, and found {loops.Count}. Alt+click an edge on each open rim." );

		Step( "Bridge", () =>
		{
			var a = loops[0];
			var c = new List<int>( loops[1] );
			c.Reverse();

			// Start the second rim at the vertex nearest the first rim's start, so the band does
			// not twist.
			var best = 0;
			var bestD = float.MaxValue;
			for ( var j = 0; j < c.Count; j++ )
			{
				var d = (Mesh.Positions[c[j]] - Mesh.Positions[a[0]]).LengthSquared;
				if ( d < bestD )
				{
					bestD = d;
					best = j;
				}
			}

			var rotated = new List<int>();
			for ( var j = 0; j < c.Count; j++ )
				rotated.Add( c[(best + j) % c.Count] );
			c = rotated;

			var sa = ArcParams( a );
			var sc = ArcParams( c );
			var n = a.Count;
			var m = c.Count;
			int A( int i ) => a[i % n];
			int C( int k ) => c[k % m];
			var tolerance = 0.5f / Math.Max( n, m );

			var i = 0;
			var k = 0;

			while ( i < n || k < m )
			{
				var nextA = i < n ? sa[i + 1] : float.MaxValue;
				var nextC = k < m ? sc[k + 1] : float.MaxValue;

				if ( i < n && k < m && MathF.Abs( nextA - nextC ) < tolerance )
				{
					Mesh.AddFace( new[] { A( i + 1 ), A( i ), C( k ), C( k + 1 ) } );
					i++;
					k++;
				}
				else if ( nextA <= nextC )
				{
					Mesh.AddFace( new[] { A( i + 1 ), A( i ), C( k ) } );
					i++;
				}
				else
				{
					Mesh.AddFace( new[] { A( i ), C( k ), C( k + 1 ) } );
					k++;
				}
			}

			ClearSelection();
		} );
	}

	/// <summary>Cumulative length round a closed loop, 0 at the start and 1 back at it.</summary>
	List<float> ArcParams( List<int> loop )
	{
		var result = new List<float> { 0f };
		var total = 0f;

		for ( var i = 0; i < loop.Count; i++ )
		{
			total += (Mesh.Positions[loop[(i + 1) % loop.Count]] - Mesh.Positions[loop[i]]).Length;
			result.Add( total );
		}

		for ( var i = 0; i < result.Count; i++ )
			result[i] = total > 0f ? result[i] / total : i / (float)loop.Count;

		return result;
	}

	/// <summary>
	/// The selected edges that lie on the mesh's boundary, gathered into closed loops and each put
	/// in the order its own faces run — the order a bridge face has to walk backwards.
	/// </summary>
	List<List<int>> SelectedBoundaryLoops()
	{
		var next = new Dictionary<int, int>();

		foreach ( var face in Mesh.Faces )
		{
			var idx = face.Indices;
			for ( var i = 0; i < idx.Length; i++ )
			{
				var u = idx[i];
				var w = idx[(i + 1) % idx.Length];
				if ( SelectedEdges.Contains( new EdgeKey( u, w ) ) )
					next.TryAdd( u, w );
			}
		}

		// An edge is on the boundary when only one face uses it; an interior edge shows up both
		// ways round and cannot be part of a rim.
		var edgeFaces = Mesh.BuildEdgeFaces();
		foreach ( var key in SelectedEdges )
		{
			if ( !edgeFaces.TryGetValue( key, out var owners ) || owners.Count != 1 )
				throw new InvalidOperationException( "Bridge joins open rims, and one of the selected edges has faces on both sides. Select only edges on the open edge of a hole." );
		}

		var loops = new List<List<int>>();
		var used = new HashSet<int>();

		foreach ( var start in next.Keys )
		{
			if ( used.Contains( start ) )
				continue;

			var loop = new List<int>();
			var at = start;

			while ( used.Add( at ) )
			{
				loop.Add( at );

				if ( !next.TryGetValue( at, out at ) )
					throw new InvalidOperationException( "One of the selected rims is not a closed loop. Select every edge round each hole." );
			}

			if ( at != start )
				throw new InvalidOperationException( "The selected rims branch. Select two separate loops." );

			loops.Add( loop );
		}

		return loops;
	}

	// --- helpers -----------------------------------------------------------------------------

	List<int> RequireFaces( string operation )
	{
		if ( SelectedFaces.Count == 0 )
			throw new InvalidOperationException( $"{operation} needs faces selected. Switch to Face mode (3) and pick some." );

		return new List<int>( SelectedFaces );
	}

	/// <summary>The half-edge running from <paramref name="a"/> to <paramref name="b"/>, or -1.</summary>
	public static int FindHalfEdge( EditableMesh mesh, int a, int b )
	{
		for ( var i = 0; i < mesh.HalfEdges.Count; i++ )
		{
			var he = mesh.HalfEdges[i];
			if ( he.Face >= 0 && he.Origin == a && mesh.HalfEdges[he.Next].Origin == b )
				return i;
		}

		return -1;
	}

	/// <summary>
	/// The edge loop through <paramref name="seed"/>: at each end, continue to the edge opposite in a
	/// vertex with exactly four edges (the "straight on" edge of a quad grid). Stops at poles,
	/// boundaries and when the loop closes.
	/// </summary>
	public static HashSet<EdgeKey> EdgeLoop( PolyMesh mesh, EdgeKey seed )
	{
		var edgeFaces = mesh.BuildEdgeFaces();
		var loop = new HashSet<EdgeKey> { seed };

		if ( !edgeFaces.ContainsKey( seed ) )
			return loop;

		var vertexEdges = mesh.BuildVertexEdges();

		void Walk( int from, int at )
		{
			var prev = new EdgeKey( from, at );

			for ( var guard = 0; guard < mesh.VertexCount; guard++ )
			{
				var edges = vertexEdges[at];
				if ( edges.Count != 4 )
					return;

				// The straight-on edge is the one sharing no face with the edge we arrived by.
				var arrivedFaces = edgeFaces[prev];
				EdgeKey? next = null;

				foreach ( var e in edges )
				{
					if ( e.Equals( prev ) )
						continue;

					var shares = false;
					foreach ( var f in edgeFaces[e] )
					{
						if ( arrivedFaces.Contains( f ) )
						{
							shares = true;
							break;
						}
					}

					if ( !shares )
					{
						next = e;
						break;
					}
				}

				if ( next is not { } n || !loop.Add( n ) )
					return;

				var other = n.A == at ? n.B : n.A;
				prev = n;
				from = at;
				at = other;
			}
		}

		Walk( seed.A, seed.B );
		Walk( seed.B, seed.A );
		return loop;
	}

	/// <summary>Drop vertices no face uses, keeping skin and colour parallel.</summary>
	public static PolyMesh RemoveUnusedVertices( PolyMesh mesh )
	{
		var remap = new int[mesh.VertexCount];
		Array.Fill( remap, -1 );

		var result = new PolyMesh();
		var skin = mesh.IsRigged ? new SkinWeights() : null;
		var colours = mesh.HasVertexColors ? new List<Vec4>() : null;

		var used = new bool[mesh.VertexCount];
		foreach ( var f in mesh.Faces )
		{
			foreach ( var i in f.Indices )
				used[i] = true;
		}

		// Walk the vertices in their original order so a surviving vertex keeps its index.
		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			if ( !used[v] )
				continue;

			remap[v] = result.Positions.Count;
			result.Positions.Add( mesh.Positions[v] );
			skin?.Vertices.Add( mesh.Skin[v] );
			colours?.Add( mesh.VertexColors[v] );
		}

		result.Skin = skin;
		result.VertexColors = colours?.ToArray();
		result.Paint = mesh.Paint?.Clone();

		foreach ( var f in mesh.Faces )
		{
			var idx = new int[f.Indices.Length];
			for ( var i = 0; i < idx.Length; i++ )
				idx[i] = remap[f.Indices[i]];

			result.AddFace( idx, (Vec2[])f.UVs.Clone(), f.Material );
		}

		return result;
	}
}
