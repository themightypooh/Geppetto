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
/// what you see here and what the compiler bakes are smoothed by the same rule. What it skips is
/// materials (one dev material for everything) and per-face material slots, neither of which the
/// preview has anything to say about yet.
/// </summary>
internal static class EffigyPreview
{
	/// <summary>Plain grey dev material. Effigy's material slots are integers with no binding to
	/// real materials yet, so there is nothing better to pick per face.</summary>
	private const string PreviewMaterial = "materials/dev/reflectivity_30.vmat";

	public static Model Build( PolyMesh mesh, float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees )
	{
		if ( mesh is null || mesh.FaceCount == 0 || mesh.VertexCount == 0 )
			return null;

		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, smoothingAngleDegrees );

		// One vertex per face corner rather than per position. Corner normals are the whole point
		// of MeshNormals - sharing a vertex between two faces that disagree about the normal is
		// exactly what rounds off a box's edges.
		var vertices = new List<SimpleVertex>( mesh.FaceCount * 4 );
		var indices = new List<int>( mesh.FaceCount * 6 );

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			var corners = cornerNormals[fi];

			if ( face.Count < 3 )
				continue;

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

			// Fan from corner 0. Effigy's faces are convex - every one of them comes out of a
			// primitive, an extrude cap or a subdivision step, all of which produce convex
			// polygons - so a fan is a valid triangulation and no ear clipping is needed.
			for ( var c = 2; c < face.Count; c++ )
			{
				indices.Add( first );
				indices.Add( first + c - 1 );
				indices.Add( first + c );
			}
		}

		if ( indices.Count == 0 )
			return null;

		var sbMesh = new Mesh( Material.Load( PreviewMaterial ) );
		sbMesh.CreateVertexBuffer<SimpleVertex>( vertices.Count, vertices );
		sbMesh.CreateIndexBuffer( indices.Count, indices );
		sbMesh.Bounds = BoundsOf( mesh );

		return Model.Builder
			.AddMesh( sbMesh )
			.Create();
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
