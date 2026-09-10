using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// An undirected edge, identified by its two endpoints regardless of which way round they were
/// given. Two faces sharing an edge traverse it in opposite directions, so the key has to ignore
/// direction or every shared edge looks like two different edges.
/// </summary>
public readonly struct EdgeKey : IEquatable<EdgeKey>
{
	public readonly int A, B;

	public EdgeKey( int a, int b )
	{
		// Sorted, so (3,7) and (7,3) are the same key.
		A = Math.Min( a, b );
		B = Math.Max( a, b );
	}

	public bool Equals( EdgeKey o ) => A == o.A && B == o.B;
	public override bool Equals( object obj ) => obj is EdgeKey e && Equals( e );
	/// <summary>
	/// The two endpoints packed into a long and mixed by Fibonacci hashing.
	///
	/// This was HashCode.Combine, which is a general-purpose hash doing several rounds of xxHash
	/// over a randomised seed. It is a good hash and it is not cheap, and an edge key is hashed
	/// once per corner of the mesh by every routine that builds edge adjacency - about eight
	/// million times over one SurfaceIndex build on a dense import.
	///
	/// A and B are vertex indices, so they are small non-negative ints with no structure worth
	/// defending against; packing them into the two halves of a long and multiplying by the
	/// golden-ratio constant spreads them across the whole word, which is all a Dictionary bucket
	/// index needs. Taking the HIGH bits is the point of the multiply - the low bits of a product
	/// are barely mixed at all.
	/// </summary>
	public override int GetHashCode()
	{
		var packed = ((ulong)(uint)A << 32) | (uint)B;
		return (int)((packed * 11400714819323198485UL) >> 33);
	}
	public override string ToString() => $"[{A}-{B}]";
}

/// <summary>
/// One n-gon. Indices reference PolyMesh.Positions; UVs are stored PER CORNER rather than per
/// vertex.
///
/// Per-corner UVs are what make seams possible: a cube corner belongs to three faces which each
/// want a different UV for the same position. Storing UVs on the vertex would force one value and
/// smear the texture across the seam. It also makes UV subdivision purely local — see
/// CatmullClark.
/// </summary>
public sealed class Face
{
	public int[] Indices;
	public Vec2[] UVs;

	/// <summary>Material slot. Export groups faces by this, so a model can carry several materials
	/// without the kernel knowing what a material is.</summary>
	public int Material;

	public Face( int[] indices, Vec2[] uvs = null, int material = 0 )
	{
		Indices = indices;
		UVs = uvs ?? new Vec2[indices.Length];
		Material = material;

		if ( UVs.Length != Indices.Length )
			throw new ArgumentException( $"Face has {Indices.Length} corners but {UVs.Length} UVs" );
	}

	public int Count => Indices.Length;
}

/// <summary>
/// A polygon mesh of n-gons. Not a half-edge structure — adjacency is derived on demand instead.
///
/// WHY NOT HALF-EDGE: Catmull-Clark needs edge→faces and vertex→(faces, edges), and nothing else
/// in phase one needs more than that. Deriving those three maps from an indexed face list is a
/// few dozen lines and is hard to get subtly wrong; a half-edge structure is a few hundred and is
/// easy to corrupt during editing. If interactive per-element editing arrives later and profiling
/// says the rebuild hurts, that is the moment to switch, not before.
///
/// QUADS ARE THE POINT. Catmull-Clark degrades badly on triangle soup, so every primitive here is
/// built quad-dominant on purpose. See Primitives.
/// </summary>
public sealed class PolyMesh
{
	public List<Vec3> Positions = new();
	public List<Face> Faces = new();

	/// <summary>
	/// Bone influences, parallel to Positions. Null until something rigs the mesh, which is the
	/// normal state for a prop.
	///
	/// It lives on the mesh rather than beside it because everything that rebuilds a vertex list —
	/// Clone, Append, Catmull-Clark — has to rebuild the weights the same way. Kept alongside, it
	/// would be silently dropped by whichever of those somebody forgot to update, and the symptom
	/// would be a model that only loses its rig after a subdivide.
	/// </summary>
	public SkinWeights Skin;

	public bool IsRigged => Skin is not null && Skin.Count == Positions.Count;

	/// <summary>
	/// Per-vertex colour, parallel to <see cref="Positions"/>, in straight (non-premultiplied) RGBA
	/// 0..1. Null unless a caller fills it in.
	///
	/// NOTHING IN THE TOOL WRITES THIS ANY MORE, and that is deliberate rather than an oversight.
	/// This was where paint lived: per-vertex colour, composited by the material's own multiply. It
	/// was only ever as fine as the mesh — a bare box has eight vertices and all of them are corners
	/// — and the shader everything renders with turned out not to read the COLOR stream at all, so
	/// the resolution problem was academic next to the paint being invisible. Paint is a texture now;
	/// see <see cref="Paint"/>.
	///
	/// IT STAYS BECAUSE THE WRITERS TAKE IT. <c>DmxWriter</c> and <c>ObjWriter</c> both emit
	/// per-vertex colour for a mesh that carries it, <c>MeshTransform</c> preserves it across a
	/// merge, and DmxGrammarTests pins the DMX spelling — that is a capability of the mesh writers,
	/// held by a test, and it is independent of how paint happens to be stored this month. What it is
	/// NOT any more is a thing the tool produces: no feature, no brush and no rebuild fills this in,
	/// so any code branching on <see cref="HasVertexColors"/> to decide what a PART looks like is
	/// branching on something that never happens.
	/// </summary>
	public Vec4[] VertexColors;

	public bool HasVertexColors => VertexColors is not null && VertexColors.Length == Positions.Count;

	/// <summary>
	/// The paint atlas a <see cref="PaintFeature"/> replayed onto this body, or null when it
	/// carries none.
	///
	/// WHY IT RIDES THE MESH, the same reason <see cref="VertexColors"/> did before it: the exporters
	/// and the preview read the merged mesh, not the feature tree, so paint has to travel with the
	/// mesh to reach them. It is a derived artifact — the feature replays its strokes onto it every
	/// rebuild — and it is keyed to THIS body's UV layout, which is why a merged mesh can only carry
	/// one body's atlas; the common case is one painted body, and that is the case the merge preserves.
	/// </summary>
	public PaintCanvas Paint;

	public bool HasPaint => Paint is not null;

	public PolyMesh() { }

	public PolyMesh( IEnumerable<Vec3> positions, IEnumerable<Face> faces )
	{
		Positions = positions.ToList();
		Faces = faces.ToList();
	}

	public int VertexCount => Positions.Count;
	public int FaceCount => Faces.Count;

	public int AddVertex( Vec3 p )
	{
		Positions.Add( p );
		return Positions.Count - 1;
	}

	public Face AddFace( int[] indices, Vec2[] uvs = null, int material = 0 )
	{
		var f = new Face( indices, uvs, material );
		Faces.Add( f );
		return f;
	}

	/// <summary>Every distinct undirected edge, with the faces using it. A closed manifold has
	/// exactly two faces per edge; a boundary edge has one; anything else is non-manifold.</summary>
	public Dictionary<EdgeKey, List<int>> BuildEdgeFaces()
	{
		// Presized on Euler's relation: a closed quad mesh has about twice as many edges as faces,
		// a triangle mesh about one and a half times. Guessing high costs one oversized bucket
		// array; guessing low costs a rehash of every entry at each doubling, which on a dense
		// mesh is most of this function's time and all of its garbage.
		var map = new Dictionary<EdgeKey, List<int>>( Faces.Count * 2 );

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			var indices = Faces[fi].Indices;
			var n = indices.Length;

			for ( var i = 0; i < n; i++ )
			{
				// i + 1 == n rather than a modulo: this runs once per corner of the mesh, and a
				// division is not free at that count.
				var key = new EdgeKey( indices[i], indices[i + 1 == n ? 0 : i + 1] );

				if ( !map.TryGetValue( key, out var list ) )
					// Capacity two, because a manifold edge has exactly two faces. The default
					// List starts at four on first Add, so this halves the backing arrays.
					map[key] = list = new List<int>( 2 );

				list.Add( fi );
			}
		}

		return map;
	}

	/// <summary>Faces touching each vertex, indexed by vertex.</summary>
	public List<int>[] BuildVertexFaces()
	{
		// Built through the CSR map rather than directly, so the counting happens once and every
		// list here is allocated at exactly its final size. The direct version grew each list from
		// empty - four, eight, sixteen - and threw away every intermediate array, which on a dense
		// mesh was some ninety megabytes of garbage to produce four megabytes of answer. It also
		// spent a LINQ Distinct() per face on the repeated-vertex guard; VertexFaces.Build does
		// that with a scan of the three or four corners instead.
		var csr = VertexFaces.Build( this );
		var map = new List<int>[Positions.Count];

		for ( var v = 0; v < map.Length; v++ )
		{
			var start = csr.Offsets[v];
			var count = csr.Offsets[v + 1] - start;
			var list = new List<int>( count );

			for ( var i = 0; i < count; i++ )
				list.Add( csr.Items[start + i] );

			map[v] = list;
		}

		return map;
	}

	/// <summary>Edges touching each vertex, indexed by vertex.</summary>
	public List<EdgeKey>[] BuildVertexEdges()
	{
		// DEDUPED PER VERTEX, NOT GLOBALLY.
		//
		// This used to hold one HashSet<(int, EdgeKey)> across the whole mesh and hash a 12-byte
		// tuple twice per corner - some two million hashes on a dense body, into a set that grew
		// to two million entries. The duplicate an edge arrives as is always the SECOND face
		// sharing it, so the only list it can already be in is the one being appended to, and that
		// list is the vertex's valence long: four entries, scanned linearly, no hashing anywhere.
		//
		// Quadratic in valence, which is the right trade - valence is four on a quad mesh and a
		// pole with fifty edges is still fifty times fifty against a hash of every corner.
		var map = new List<EdgeKey>[Positions.Count];

		for ( var i = 0; i < map.Length; i++ )
			map[i] = new List<EdgeKey>( 4 );

		foreach ( var f in Faces )
		{
			var indices = f.Indices;
			var n = indices.Length;

			for ( var i = 0; i < n; i++ )
			{
				var a = indices[i];
				var b = indices[i + 1 == n ? 0 : i + 1];
				var key = new EdgeKey( a, b );

				AddOnce( map[a], key );
				AddOnce( map[b], key );
			}
		}

		return map;

		static void AddOnce( List<EdgeKey> list, EdgeKey key )
		{
			for ( var i = 0; i < list.Count; i++ )
			{
				if ( list[i].Equals( key ) )
					return;
			}

			list.Add( key );
		}
	}

	/// <summary>Centroid of a face's corners.</summary>
	public Vec3 FaceCentroid( Face f )
	{
		var sum = Vec3.Zero;

		foreach ( var i in f.Indices )
			sum += Positions[i];

		return sum / f.Count;
	}

	/// <summary>Newell's method, which is correct for non-planar n-gons where a simple
	/// cross-product of the first three corners is not.</summary>
	public Vec3 FaceNormal( Face f )
	{
		var n = Vec3.Zero;

		for ( var i = 0; i < f.Count; i++ )
		{
			var a = Positions[f.Indices[i]];
			var b = Positions[f.Indices[(i + 1) % f.Count]];

			n += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}

		return n.Normal;
	}

	/// <summary>Area-weighted vertex normals. Area weighting rather than a plain average, so a
	/// vertex surrounded by one big face and several slivers leans toward the big one.</summary>
	public Vec3[] ComputeVertexNormals()
	{
		var normals = new Vec3[Positions.Count];

		foreach ( var f in Faces )
		{
			var n = FaceNormal( f );
			var weight = FaceArea( f );

			foreach ( var i in f.Indices )
				normals[i] += n * weight;
		}

		for ( var i = 0; i < normals.Length; i++ )
			normals[i] = normals[i].Normal;

		return normals;
	}

	/// <summary>
	/// Fan triangulation about the centroid, PROJECTED ONTO THE FACE'S OWN NORMAL so the fan's
	/// backward triangles subtract instead of adding.
	///
	/// It used to sum |cross| and so measured a convex face exactly and a CONCAVE one too big -
	/// every fan triangle that wound backwards was counted as material rather than as the notch it
	/// stands for. That was true of nothing this kernel made until a cap with a hole in it started
	/// coming back as two n-gons, each of which wraps around the hole and is concave by
	/// construction. A washer then measured a volume larger than its own solid, which is the sort
	/// of thing that reads as "the cap is broken" when the cap is fine and the ruler is not.
	///
	/// Exact for any planar polygon, concave or not, and identical to the old sum for a convex one:
	/// every fan triangle winds the same way there. For a mildly non-planar face it is the area
	/// projected onto the Newell normal, which is the honest generalisation and is what the
	/// area-weighted vertex normals wanted from it anyway.
	/// </summary>
	public float FaceArea( Face f ) => FaceArea( f, FaceNormal( f ) );

	/// <summary>
	/// <see cref="FaceArea(Face)"/> for a caller that already has the face's Newell normal.
	///
	/// Every caller that wants a face's area wants its normal too - the area is only meaningful as
	/// the area projected onto that normal - so the one-argument form recomputed a normal the
	/// caller was holding. Area-weighted normals over a dense mesh did that once per face and it
	/// was the single biggest cost in the viewport's rebuild.
	/// </summary>
	public float FaceArea( Face f, Vec3 normal )
	{
		var c = FaceCentroid( f );
		var area = 0f;

		for ( var i = 0; i < f.Count; i++ )
		{
			var a = Positions[f.Indices[i]] - c;
			var b = Positions[f.Indices[(i + 1) % f.Count]] - c;
			area += Vec3.Dot( Vec3.Cross( a, b ), normal ) * 0.5f;
		}

		return MathF.Abs( area );
	}

	/// <summary>
	/// Enclosed volume with sign. Positive when face windings put normals outward, negative when
	/// the solid is inside-out.
	///
	/// Divergence theorem: sum over faces of (centroid · normal) * area equals three times the
	/// enclosed volume. A mesh can be closed, manifold, Euler-correct and still inverted — this
	/// is the quantity that sees it. Used as a refusal, so it lives on the mesh rather than as a
	/// private copy in every test file.
	/// </summary>
	public float SignedVolume()
	{
		var acc = 0f;

		foreach ( var f in Faces )
			acc += Vec3.Dot( FaceCentroid( f ), FaceNormal( f ) ) * FaceArea( f );

		return acc / 3f;
	}

	/// <summary>
	/// Diagonal of the axis-aligned bounds - this tool's one answer to "how big is this model".
	///
	/// Effigy's units are dimensionless: a default primitive is one unit across and a room is
	/// hundreds, so anything with a distance in it needs a default that SCALES rather than a
	/// constant. The bake's search range, reprojection's, and the sculpt brush's starting radius are
	/// all a fraction of this, and they were three copies of the same loop until they were not.
	/// </summary>
	public float BoundsDiagonal
	{
		get
		{
			if ( Positions.Count == 0 )
				return 0f;

			var min = Positions[0];
			var max = Positions[0];

			foreach ( var p in Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}

			return (max - min).Length;
		}
	}

	public PolyMesh Clone()
	{
		var m = new PolyMesh { Positions = new List<Vec3>( Positions ), Skin = Skin?.Clone() };

		if ( VertexColors is not null )
			m.VertexColors = (Vec4[])VertexColors.Clone();

		if ( Paint is not null )
			m.Paint = Paint.Clone();

		foreach ( var f in Faces )
			m.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );

		return m;
	}
}

/// <summary>What Validate found. Kept as data rather than thrown, because a mesh mid-edit is
/// allowed to be briefly invalid and the caller decides whether it matters.</summary>
public sealed class MeshValidation
{
	public List<string> Errors = new();
	public int BoundaryEdges;
	public int NonManifoldEdges;
	public bool IsClosed => BoundaryEdges == 0 && NonManifoldEdges == 0;
	public bool IsValid => Errors.Count == 0;

	public override string ToString() =>
		IsValid
			? $"valid, {(IsClosed ? "closed" : $"{BoundaryEdges} boundary edges")}"
			: string.Join( "; ", Errors );
}

public static class MeshValidator
{
	public static MeshValidation Validate( PolyMesh mesh )
	{
		var r = new MeshValidation();

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];

			if ( f.Count < 3 )
				r.Errors.Add( $"face {fi} has {f.Count} corners" );

			if ( f.UVs.Length != f.Count )
				r.Errors.Add( $"face {fi} has {f.Count} corners but {f.UVs.Length} UVs" );

			foreach ( var i in f.Indices )
			{
				if ( i < 0 || i >= mesh.Positions.Count )
					r.Errors.Add( $"face {fi} references vertex {i}, out of range" );
			}

			// A scan rather than Distinct().Count(): this runs over every face of the mesh, and
			// the LINQ form allocates an enumerator and a set per face to compare three or four
			// integers.
			if ( RepeatsAVertex( f.Indices ) )
				r.Errors.Add( $"face {fi} repeats a vertex" );
		}

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count == 1 ) r.BoundaryEdges++;
			else if ( faces.Count > 2 )
			{
				r.NonManifoldEdges++;
				r.Errors.Add( $"edge {key} is shared by {faces.Count} faces" );
			}
		}

		return r;
	}

	static bool RepeatsAVertex( int[] indices )
	{
		for ( var i = 1; i < indices.Length; i++ )
		{
			for ( var j = 0; j < i; j++ )
			{
				if ( indices[i] == indices[j] )
					return true;
			}
		}

		return false;
	}

	/// <summary>V - E + F. Equals 2 for a closed genus-0 surface, and is the cheapest single check
	/// that a subdivision pass did not quietly corrupt the topology.</summary>
	public static int EulerCharacteristic( PolyMesh mesh ) =>
		mesh.VertexCount - mesh.BuildEdgeFaces().Count + mesh.FaceCount;
}
