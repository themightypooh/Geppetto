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

	/// <summary>One Mesh over a subset of the faces, all sharing one material.</summary>
	private static Mesh BuildSubmesh( PolyMesh mesh, List<int> faceIndices, int[][] cornerNormals,
		List<Vec3> normals, Material material, BBox bounds )
	{
		// One vertex per face corner rather than per position. Corner normals are the whole point
		// of MeshNormals - sharing a vertex between two faces that disagree about the normal is
		// exactly what rounds off a box's edges.
		var vertices = new List<SimpleVertex>( faceIndices.Count * 4 );
		var indices = new List<int>( faceIndices.Count * 6 );

		foreach ( var fi in faceIndices )
		{
			var face = mesh.Faces[fi];
			var corners = cornerNormals[fi];

			var first = vertices.Count;

			for ( var c = 0; c < face.Count; c++ )
			{
				var p = mesh.Positions[face.Indices[c]];
				var n = normals[corners[c]];
				var uv = face.UVs is not null && c < face.UVs.Length ? face.UVs[c] : default;

				var position = new Vector3( p.x, p.y, p.z );
				var normal = new Vector3( n.x, n.y, n.z );

				vertices.Add( new SimpleVertex( position, normal, TangentFor( normal ), new Vector2( uv.x, uv.y ) ) );
			}

			// EAR CLIPPING, NOT A FAN. This used to fan from corner 0 on the grounds that every
			// face the kernel produces is convex. Extrude caps are not: they are whatever closed
			// region was drawn, and fanning a concave one fills its notches in - draw a dart and
			// the solid came back as a quadrilateral with the concave corner swallowed.
			var polygon = new List<Vec3>( face.Count );

			for ( var k = 0; k < face.Count; k++ )
				polygon.Add( mesh.Positions[face.Indices[k]] );

			foreach ( var (a, b, cc) in Triangulate.Face( polygon ) )
			{
				indices.Add( first + a );
				indices.Add( first + b );
				indices.Add( first + cc );
			}
		}

		if ( indices.Count == 0 )
			return null;

		var sbMesh = new Mesh( material );
		sbMesh.CreateVertexBuffer<SimpleVertex>( vertices.Count, vertices );
		sbMesh.CreateIndexBuffer( indices.Count, indices );
		sbMesh.Bounds = bounds;

		return sbMesh;
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
