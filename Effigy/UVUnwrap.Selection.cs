using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

public static partial class UVUnwrap
{
	/// <summary>
	/// Unwrap only <paramref name="faces"/>, leaving every other face's UVs exactly as they are -
	/// the fix for one bad sleeve without re-laying the whole body.
	///
	/// The picked faces are cut off from the rest (the selection's border acts as a seam), charted
	/// and flattened as <see cref="Unwrap"/> would, then laid out WHERE THEY WERE: scaled to fit
	/// the box their old UVs filled, centred in it. That keeps the rest of the layout's packing
	/// valid when the old UVs were a packed layout. When the old UVs were not in the unit square
	/// (a tiling projection) or had no area, the new islands fill the unit square instead.
	/// </summary>
	public static UnwrapReport UnwrapFaces( PolyMesh mesh, IReadOnlyCollection<int> faces, float angleDegrees = 66f,
		float margin = 0.01f, IReadOnlySet<EdgeKey> seams = null, UnwrapMode mode = UnwrapMode.Angle )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var chosen = (faces ?? Array.Empty<int>()).Where( f => f >= 0 && f < mesh.FaceCount ).Distinct().OrderBy( f => f ).ToList();
		if ( chosen.Count == 0 )
			throw new InvalidOperationException( "There are no faces to unwrap. Select the faces (3) to lay out, or select nothing to unwrap the whole mesh." );

		if ( chosen.Count == mesh.FaceCount )
			return Unwrap( mesh, angleDegrees, margin, seams ?? mesh.LiveSeams(), mode );

		var (oldMin, oldMax) = UVIslands.Bounds( mesh, chosen );

		// The sub-mesh shares vertex numbering, so the seams' keys still name its edges.
		var piece = new PolyMesh { Positions = mesh.Positions };
		foreach ( var f in chosen )
		{
			var face = mesh.Faces[f];
			piece.Faces.Add( new Face( (int[])face.Indices.Clone(), (Vec2[])face.UVs.Clone(), face.Material ) );
		}

		var report = Unwrap( piece, angleDegrees, margin, seams ?? mesh.LiveSeams(), mode );

		var (newMin, newMax) = UVIslands.Bounds( piece, Enumerable.Range( 0, piece.FaceCount ) );
		var newSize = newMax - newMin;
		var oldSize = oldMax - oldMin;
		var inSquare = oldMin.x >= -0.01f && oldMin.y >= -0.01f && oldMax.x <= 1.01f && oldMax.y <= 1.01f;
		var fit = 1f;
		var target = Vec2.Zero;

		if ( inSquare && oldSize.x > 1e-5f && oldSize.y > 1e-5f && newSize.x > 1e-9f && newSize.y > 1e-9f )
		{
			fit = MathF.Min( oldSize.x / newSize.x, oldSize.y / newSize.y );
			var placed = newSize * fit;
			target = oldMin + (oldSize - placed) * 0.5f;
		}
		else
		{
			target = newMin;
		}

		for ( var i = 0; i < chosen.Count; i++ )
		{
			var uvs = piece.Faces[i].UVs;
			for ( var c = 0; c < uvs.Length; c++ )
				uvs[c] = target + (uvs[c] - newMin) * fit;

			mesh.Faces[chosen[i]].UVs = uvs;
		}

		return new UnwrapReport( report.Charts, report.Faces, report.SkippedFaces, report.Scale * fit, report.MeanStretch, report.MaxStretch );
	}
}
