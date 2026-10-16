using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// The UV toolset's Edit-mode operations: cylinder and sphere projection, texel density, packing
/// at a texture size, pins, minimize stretch, stitch, align and the second UV set. Each is one
/// undo step and acts on the selection, or on everything when nothing is selected.
/// </summary>
public sealed partial class MeshEditSession
{
	/// <summary>The selected faces in Face mode, else every face.</summary>
	List<int> UVFacesOrAll() => Mode == EditElement.Face && SelectedFaces.Count > 0
		? SelectedFaces.Where( f => f >= 0 && f < Mesh.FaceCount ).OrderBy( f => f ).ToList()
		: AllFaces( Mesh.FaceCount );

	/// <summary>
	/// Wrap the selected faces (or all) round an axis: a pipe, a limb, a can. U goes once round,
	/// V runs along the axis with square texels at the average radius. Null axis: the direction
	/// the selection is longest along.
	/// </summary>
	public void ProjectCylinder( Vec3? axis = null )
	{
		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "There is nothing to project." );

		var faces = UVFacesOrAll();
		var a = axis ?? UVProjection.LongestAxis( Mesh, faces );
		Step( "Cylinder projection", () => UVProjection.CylinderProject( Mesh, a, null, faces ) );
	}

	/// <summary>
	/// Map the selected faces (or all) by longitude and latitude about an axis (up, by default):
	/// a head, an eye, a ball. U goes once round, V from the bottom pole to the top.
	/// </summary>
	public void ProjectSphere( Vec3? axis = null )
	{
		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "There is nothing to project." );

		var faces = UVFacesOrAll();
		var a = axis ?? new Vec3( 0, 0, 1 );
		Step( "Sphere projection", () => UVProjection.SphereProject( Mesh, a, null, faces ) );
	}
}
