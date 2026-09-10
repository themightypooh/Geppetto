using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Turns the Part Studio's in-memory PolyMesh straight into a runtime Model, so the viewport can
/// show the result of a feature the moment it is added.
///
/// The alternative — the path Export/Compile still take — is OBJ on disk, a generated .vmdl, and
/// a call into the asset compiler. That takes hundreds of milliseconds and writes files, which is
/// fine for producing a placeable prop and hopeless as the response to dragging a slider. This
/// path never touches the disk.
///
/// It is deliberately NOT a second geometry pipeline: it shares MeshNormals with ObjWriter, so
/// what you see here and what the compiler bakes are smoothed by the same rule.
///
/// MATERIALS RENDER HERE NOW. A face carries a slot number and PartStudio.MaterialNames binds the
/// slot to a vmat, so the preview groups faces by the material they resolve to and builds one
/// submesh per material — the same grouping the exporters express as usemtl runs. Faces on slot 0,
/// on a slot nothing is bound to, or on a slot whose vmat will not load fall back to the flat
/// placeholder rather than rendering as nothing. Pass no resolver — the sculpt preview does — and
/// the whole model is the placeholder, which is the old behaviour.
/// </summary>
internal static class EffigyPreview
{
	/// <summary>
	/// FLAT grey, with no pattern on it at all. The fallback for a face whose slot names no
	/// material, or names one that will not load.
	///
	/// It used to be dev/reflectivity_30, and that material actively lies about scale: its texture
	/// is a grid with the number "30" printed in every tile - the material's reflectivity, nothing
	/// to do with size - and caps take plane coordinates straight through as UVs, so it tiles once
	/// per sketch unit. A 30x30 face therefore came out covered in thirty-odd squares each labelled
	/// "30", which reads as a part 900 units across. gray_50's texture is a single flat colour.
	/// </summary>
	private const string PreviewMaterial = "materials/dev/gray_50.vmat";

	/// <summary>
	/// Build a Model from the mesh, optionally resolving each face's material slot to a real vmat.
	/// </summary>
	/// <param name="materialForSlot">Slot number to bound material path, or null / empty for an
	/// unbound slot. Pass null to render everything on the placeholder.</param>
	public static Model Build( PolyMesh mesh, Func<int, string> materialForSlot = null,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees )
	{
		if ( mesh is null || mesh.FaceCount == 0 || mesh.VertexCount == 0 )
			return null;

		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, smoothingAngleDegrees );

		var placeholder = Material.Load( PreviewMaterial );
		var bounds = BoundsOf( mesh );

		// Faces bucketed by the material they render with. One drop of brushed steel onto three
		// faces is one bucket and one submesh; a part nobody assigned is one bucket on the
		// placeholder. The slot is resolved once and cached, not once per face.
		var buckets = new Dictionary<Material, List<int>>();
		var slotCache = new Dictionary<int, Material>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( mesh.Faces[fi].Count < 3 )
				continue;

			var material = ResolveMaterial( mesh.Faces[fi].Material, materialForSlot, placeholder, slotCache );

			if ( !buckets.TryGetValue( material, out var faces ) )
				buckets[material] = faces = new List<int>();

			faces.Add( fi );
		}

		var builder = Model.Builder;
		var any = false;

		foreach ( var (material, faces) in buckets )
		{
			if ( BuildSubmesh( mesh, faces, cornerNormals, normals, material, bounds ) is { } sub )
			{
				builder.AddMesh( sub );
				any = true;
			}
		}

		return any ? builder.Create() : null;
	}

	/// <summary>
	/// Build a Model from the mesh wearing ONE material on every face, slot resolution ignored.
	///
	/// THIS IS THE PAINT-PREVIEW PATH. While painting, the whole part wears the painted atlas — a
	/// <see cref="Material.CreateCopy"/> with the live canvas texture bound — so the slot bucketing
	/// above is the wrong answer there. One material, one submesh, the same smoothing as the ordinary
	/// preview, and the caller owns the material's lifetime across rebuilds.
	/// </summary>
	public static Model Build( PolyMesh mesh, Material material,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees )
	{
		if ( mesh is null || mesh.FaceCount == 0 || mesh.VertexCount == 0 || material is null )
			return null;

		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, smoothingAngleDegrees );
		var bounds = BoundsOf( mesh );

		var faces = new List<int>( mesh.FaceCount );

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( mesh.Faces[fi].Count >= 3 )
				faces.Add( fi );
		}

		var sub = BuildSubmesh( mesh, faces, cornerNormals, normals, material, bounds );

		if ( sub is null )
			return null;

		var builder = Model.Builder;
		builder.AddMesh( sub );

		return builder.Create();
	}

	/// <summary>
	/// A preview Model that can be rewritten in place when only vertex positions change — a body
	/// being dragged by the Transform handle. Rebuilding the Model, its buffers and its GameObject
	/// every frame is what made dragging a dense import sluggish, so this retains the meshes and
	/// overwrites their vertex buffers instead. Buckets, triangulation and buffer allocation are
	/// done once; a drag only recomputes normals and re-uploads the vertex data.
	/// </summary>
	internal sealed class LivePreview
	{
		public Model Model { get; private set; }

		private readonly List<Mesh> _meshes = new();
		private readonly List<List<SimpleVertex>> _vertices = new();
		private readonly Dictionary<Material, int> _bucketIndex = new();
		private int[] _faceMesh;
		private int[] _faceCorners;
		private int _faceCount;

		private LivePreview() { }

		public static LivePreview Build( PolyMesh mesh, Func<int, string> materialForSlot,
			float smoothingAngleDegrees )
		{
			var live = new LivePreview { _faceCount = mesh.FaceCount };
			live.Rebuild( mesh, materialForSlot, smoothingAngleDegrees );
			return live;
		}

		/// <summary>
		/// Rewrite the vertex buffers from a mesh whose topology matches the one this was built
		/// from. Returns false — the caller must rebuild — when the face count, material layout or
		/// corner count changed, because any of those moves the bucketing and the index buffers.
		/// </summary>
		public bool TryUpdate( PolyMesh mesh, Func<int, string> materialForSlot, float smoothingAngleDegrees )
		{
			if ( Model is null || mesh.FaceCount != _faceCount )
				return false;

			var placeholder = Material.Load( PreviewMaterial );
			var slotCache = new Dictionary<int, Material>();
			var faceLists = new List<List<int>>( _meshes.Count );

			for ( var i = 0; i < _meshes.Count; i++ )
				faceLists.Add( new List<int>() );

			for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			{
				// The index buffers are laid out corner by corner, so two faces swapping corner
				// counts keeps the per-bucket vertex total the same while scrambling the triangles.
				if ( mesh.Faces[fi].Count != _faceCorners[fi] )
					return false;

				if ( mesh.Faces[fi].Count < 3 )
					continue;

				var material = ResolveMaterial( mesh.Faces[fi].Material, materialForSlot, placeholder, slotCache );

				// A face on a bucket that was not there at build time means the material layout
				// changed, which an in-place update cannot express.
				if ( !_bucketIndex.TryGetValue( material, out var bi ) || bi != _faceMesh[fi] )
					return false;

				faceLists[bi].Add( fi );
			}

			var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, smoothingAngleDegrees );
			var bounds = BoundsOf( mesh );

			for ( var i = 0; i < _meshes.Count; i++ )
			{
				var vertices = BuildVertices( mesh, faceLists[i], cornerNormals, normals );

				if ( vertices.Count != _vertices[i].Count )
					return false;

				_meshes[i].SetVertexBufferData( vertices, 0 );
				_meshes[i].Bounds = bounds;
			}

			return true;
		}

		void Rebuild( PolyMesh mesh, Func<int, string> materialForSlot, float smoothingAngleDegrees )
		{
			_meshes.Clear();
			_vertices.Clear();
			_bucketIndex.Clear();

			var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, smoothingAngleDegrees );
			var bounds = BoundsOf( mesh );
			var placeholder = Material.Load( PreviewMaterial );
			var slotCache = new Dictionary<int, Material>();

			var buckets = new List<(Material Material, List<int> Faces)>();
			_faceMesh = new int[mesh.FaceCount];
			_faceCorners = new int[mesh.FaceCount];

			for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			{
				_faceCorners[fi] = mesh.Faces[fi].Count;

				if ( mesh.Faces[fi].Count < 3 )
					continue;

				var material = ResolveMaterial( mesh.Faces[fi].Material, materialForSlot, placeholder, slotCache );

				if ( !_bucketIndex.TryGetValue( material, out var bi ) )
				{
					bi = buckets.Count;
					_bucketIndex[material] = bi;
					buckets.Add( (material, new List<int>()) );
				}

				buckets[bi].Faces.Add( fi );
				_faceMesh[fi] = bi;
			}

			var builder = Model.Builder;

			foreach ( var (material, faces) in buckets )
			{
				var vertices = BuildVertices( mesh, faces, cornerNormals, normals );

				if ( vertices.Count == 0 )
					continue;

				var indices = TriangulateIndices( mesh, faces );
				var sbMesh = new Mesh( material );
				sbMesh.CreateVertexBuffer<SimpleVertex>( vertices.Count, vertices );
				sbMesh.CreateIndexBuffer( indices.Count, indices );
				sbMesh.Bounds = bounds;

				builder.AddMesh( sbMesh );
				_meshes.Add( sbMesh );
				_vertices.Add( vertices );
			}

			Model = _meshes.Count > 0 ? builder.Create() : null;
		}
	}

	/// <summary>
	/// The material a slot renders with: the vmat bound to it, or the flat placeholder.
	///
	/// Slot 0 is the slot every face starts on and never carries a material, so it is the
	/// placeholder without a lookup. A named slot whose vmat will not load — a path into a package
	/// that is not installed, a material deleted since it was assigned — also falls back rather
	/// than rendering the part as nothing, which is the failure the single-material preview used
	/// to avoid wholesale.
	/// </summary>
	private static Material ResolveMaterial( int slot, Func<int, string> materialForSlot,
		Material placeholder, Dictionary<int, Material> cache )
	{
		if ( slot <= 0 || materialForSlot is null )
			return placeholder;

		if ( cache.TryGetValue( slot, out var cached ) )
			return cached;

		var name = materialForSlot( slot );
		var material = string.IsNullOrWhiteSpace( name ) ? null : Material.Load( name );

		// A named slot whose vmat would not load is the one case the fallback hides — the face
		// renders as placeholder grey and nothing says why. Say why, once per slot per build.
		if ( material is null && !string.IsNullOrWhiteSpace( name ) )
			Log.Warning( $"[effigy-preview] slot {slot}: material '{name}' failed to load — using placeholder" );

		return cache[slot] = material ?? placeholder;
	}

	/// <summary>
	/// One Mesh over a subset of the faces, all sharing one material.
	///
	/// ONE VERTEX FORMAT, since paint stopped being vertex colour. There was a second path here that
	/// built the richer <c>Vertex</c> so the engine could multiply a material by a per-vertex colour;
	/// it was chosen by <c>PolyMesh.HasVertexColors</c>, which nothing sets any more, so it rendered
	/// nothing that this path does not. Paint reaches the viewport as the painted body's material
	/// now, the same way a dropped material does.
	/// </summary>
	private static Mesh BuildSubmesh( PolyMesh mesh, List<int> faceIndices, int[][] cornerNormals,
		List<Vec3> normals, Material material, BBox bounds )
	{
		var vertices = BuildVertices( mesh, faceIndices, cornerNormals, normals );
		var indices = TriangulateIndices( mesh, faceIndices );

		if ( indices.Count == 0 )
			return null;

		var sbMesh = new Mesh( material );
		sbMesh.CreateVertexBuffer<SimpleVertex>( vertices.Count, vertices );
		sbMesh.CreateIndexBuffer( indices.Count, indices );
		sbMesh.Bounds = bounds;

		return sbMesh;
	}

	/// <summary>
	/// One vertex per face corner rather than per position, in the same order
	/// <see cref="TriangulateIndices"/> indexes them. Corner normals are the whole point of
	/// MeshNormals - sharing a vertex between two faces that disagree about the normal is exactly
	/// what rounds off a box's edges.
	/// </summary>
	private static List<SimpleVertex> BuildVertices( PolyMesh mesh, List<int> faceIndices,
		int[][] cornerNormals, List<Vec3> normals )
	{
		var vertices = new List<SimpleVertex>( faceIndices.Count * 4 );

		foreach ( var fi in faceIndices )
		{
			var face = mesh.Faces[fi];
			var corners = cornerNormals[fi];

			for ( var c = 0; c < face.Count; c++ )
			{
				var p = mesh.Positions[face.Indices[c]];
				var n = normals[corners[c]];
				var uv = face.UVs is not null && c < face.UVs.Length ? face.UVs[c] : default;

				var position = new Vector3( p.x, p.y, p.z );
				var normal = new Vector3( n.x, n.y, n.z );

				vertices.Add( new SimpleVertex( position, normal, TangentFor( normal ), new Vector2( uv.x, uv.y ) ) );
			}
		}

		return vertices;
	}

	/// <summary>
	/// Ear-clipping, not a fan. This used to fan from corner 0 on the grounds that every face the
	/// kernel produces is convex. Extrude caps are not: they are whatever closed region was drawn,
	/// and fanning a concave one fills its notches in — draw a dart and the solid came back as a
	/// quadrilateral with the concave corner swallowed.
	/// </summary>
	private static List<int> TriangulateIndices( PolyMesh mesh, List<int> faceIndices )
	{
		var indices = new List<int>( faceIndices.Count * 6 );
		var first = 0;

		foreach ( var fi in faceIndices )
		{
			var face = mesh.Faces[fi];
			var polygon = new List<Vec3>( face.Count );

			for ( var k = 0; k < face.Count; k++ )
				polygon.Add( mesh.Positions[face.Indices[k]] );

			foreach ( var (a, b, cc) in Triangulate.Face( polygon ) )
			{
				indices.Add( first + a );
				indices.Add( first + b );
				indices.Add( first + cc );
			}

			first += face.Count;
		}

		return indices;
	}

	/// <summary>
	/// Any unit vector perpendicular to the normal will do. Effigy has no tangent basis of its own
	/// - UVs come from box or planar projection, not from an unwrap - so there is nothing to
	/// derive a real tangent from, and the preview material does not read one.
	/// </summary>
	private static Vector3 TangentFor( Vector3 normal )
	{
		// Cross with whichever axis the normal is least aligned to, so the result never collapses.
		var axis = MathF.Abs( normal.z ) < 0.9f ? Vector3.Up : Vector3.Forward;
		var tangent = Vector3.Cross( normal, axis );

		return tangent.IsNearZeroLength ? Vector3.Forward : tangent.Normal;
	}

	private static BBox BoundsOf( PolyMesh mesh )
	{
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			var v = new Vector3( p.x, p.y, p.z );
			min = Vector3.Min( min, v );
			max = Vector3.Max( max, v );
		}

		return new BBox( min, max );
	}
}
