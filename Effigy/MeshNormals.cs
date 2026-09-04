using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Shared normal generation. Lives here rather than inside a writer because every export format
/// needs the same answer — OBJ and SMD differ in how they spell a normal, not in what it should
/// be. Two copies of this would eventually disagree, and the symptom would be a model that shades
/// differently depending on which format it was exported through.
/// </summary>
public static class MeshNormals
{
	/// <summary>Faces meeting at a sharper angle than this keep a hard edge; anything softer gets
	/// averaged. Without this a box comes out with rounded-looking corners and a cylinder comes out
	/// faceted — one threshold fixes both.</summary>
	public const float DefaultSmoothingAngleDegrees = 40f;

	/// <summary>
	/// One normal per face corner, averaged only across faces that agree to within the threshold.
	///
	/// The naive version — one averaged normal per vertex — rounds off every hard edge, so a box
	/// renders like a pillow. The other naive version — one normal per face — facets every curve,
	/// so a 32-segment cylinder looks like a nut. Thresholding gets both right from the same code
	/// and means the parametric stage never has to hand-author smoothing groups.
	/// </summary>
	public static (int[][] CornerNormals, List<Vec3> Normals) ComputeCornerNormals( PolyMesh mesh, float angleDegrees )
	{
		var cosLimit = MathF.Cos( angleDegrees * MathF.PI / 180f );
		var faceNormals = new Vec3[mesh.FaceCount];
		var faceAreas = new float[mesh.FaceCount];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			faceNormals[fi] = mesh.FaceNormal( mesh.Faces[fi] );
			faceAreas[fi] = mesh.FaceArea( mesh.Faces[fi] );
		}

		var vertexFaces = mesh.BuildVertexFaces();
		var normals = new List<Vec3>();
		var dedupe = new Dictionary<(long, long, long), int>();
		var cornerNormals = new int[mesh.FaceCount][];

		int Intern( Vec3 n )
		{
			var key = (
				(long)MathF.Round( n.x * 1e4f ),
				(long)MathF.Round( n.y * 1e4f ),
				(long)MathF.Round( n.z * 1e4f ));

			if ( dedupe.TryGetValue( key, out var idx ) )
				return idx;

			idx = normals.Count;
			normals.Add( n );
			dedupe[key] = idx;
			return idx;
		}

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var f = mesh.Faces[fi];
			cornerNormals[fi] = new int[f.Count];

			for ( var i = 0; i < f.Count; i++ )
			{
				var sum = Vec3.Zero;

				foreach ( var other in vertexFaces[f.Indices[i]] )
				{
					if ( Vec3.Dot( faceNormals[fi], faceNormals[other] ) >= cosLimit )
						sum += faceNormals[other] * faceAreas[other];
				}

				var n = sum.Normal;

				// A degenerate cluster can cancel to zero; fall back to the face's own normal
				// rather than emitting (0,0,0), which some importers reject outright.
				if ( n.LengthSquared < 0.5f )
					n = faceNormals[fi];

				cornerNormals[fi][i] = Intern( n );
			}
		}

		return (cornerNormals, normals);
	}
}
