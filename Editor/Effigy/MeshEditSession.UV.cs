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

	// --- packing ---------------------------------------------------------------------------

	/// <summary>
	/// Lay every island into the texture square as large as it goes, at least
	/// <paramref name="marginPixels"/> apart at <paramref name="textureSize"/>, each turned to the
	/// angle that boxes it tightest when <paramref name="rotate"/> (see <see cref="UVPack"/>).
	/// </summary>
	public UVPack.Result PackIslands( float marginPixels, int textureSize, bool rotate = true, bool keepScale = false )
	{
		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "There is nothing to pack." );

		UVPack.Result result = null;
		Step( "Pack islands", () =>
		{
			result = UVPack.Pack( Mesh, new UVPack.Options { MarginPixels = marginPixels, TextureSize = textureSize, Rotate = rotate, KeepScale = keepScale } );
			if ( !result.Fits )
				throw new InvalidOperationException( keepScale
					? $"The islands don't fit in the square at their present size with {marginPixels:0.#} px margins. Lower the texel density, use a bigger texture size, or pack without keeping the size."
					: $"The islands don't fit in the square with {marginPixels:0.#} px margins at {textureSize} px. Use a smaller margin or a bigger texture size." );
			Report( result.ToString() );
		} );
		return result;
	}

	// --- texel density ---------------------------------------------------------------------

	/// <summary>The texel density of the selected faces' islands (or every island) at
	/// <paramref name="textureSize"/> pixels square. Reads only; changes nothing.</summary>
	public UVDensity.Report TexelDensity( int textureSize ) => UVDensity.Measure( Mesh, textureSize, UVFacesOrAll() );

	/// <summary>
	/// Scale the selected faces' islands (or every island) so each gets <paramref name="pixelsPerUnit"/>
	/// at <paramref name="textureSize"/>: the way to give a hand the same sharpness as the arm it is
	/// on. Each island is scaled about its own centre, so pack afterwards if they now overlap.
	/// </summary>
	public int SetTexelDensity( float pixelsPerUnit, int textureSize )
	{
		if ( Mesh.FaceCount == 0 )
			throw new InvalidOperationException( "There are no faces with UVs to size." );

		var faces = UVFacesOrAll();
		var scaled = 0;
		Step( "Set texel density", () =>
		{
			scaled = UVDensity.Set( Mesh, pixelsPerUnit, textureSize, faces );
			if ( scaled == 0 )
				NoChange( "None of these islands has any UV area to scale. Unwrap them first." );
			else
				Report( $"{scaled} island{(scaled == 1 ? "" : "s")} set to {pixelsPerUnit:0.##} px per unit at {textureSize} px." );
		} );
		return scaled;
	}
}
