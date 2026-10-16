using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

public enum MeshFilterKind
{
	/// <summary>Toward the neighbours' average, along the normal only (Laplacian).</summary>
	Smooth,
	/// <summary>Scale about the mesh's centre.</summary>
	Scale,
	/// <summary>Out along the normals by Strength inches.</summary>
	Inflate,
	/// <summary>Toward a sphere about the centre, at the mesh's mean radius.</summary>
	Sphere,
	/// <summary>Even the spacing along the surface without changing its shape.</summary>
	Relax,
	/// <summary>Smooth that keeps volume and shape (HC Laplacian): small bumps go, large forms stay.</summary>
	SurfaceSmooth,
	/// <summary>Sharpen the forms: the opposite of smoothing, steadied so it doesn't explode.</summary>
	Sharpen,
	/// <summary>Exaggerate the fine detail (the difference from a smoothed copy).</summary>
	EnhanceDetails,
}

/// <summary>
/// Whole-mesh sculpt filters (Blender's mesh filters): each moves every vertex at once, scaled by
/// Strength and an optional mask, Iterations times. Measured against Blender's
/// (Effigy.Tests --parity).
/// </summary>
public static class MeshFilter
{
	public static void Apply( PolyMesh mesh, MeshFilterKind kind, float strength, int iterations = 1, float[] mask = null )
	{
		var n = mesh.VertexCount;
		if ( n == 0 ) return;
		var nb = MeshLoops.Neighbours( n, mesh.Faces );
		for ( var it = 0; it < Math.Max( 1, iterations ); it++ )
		{
			var pos = mesh.Positions.ToArray();
			var normals = SurfaceDetail.VertexNormals( mesh );
			var avg = Average( pos, nb );
			var centre = pos.Aggregate( Vec3.Zero, ( a, b ) => a + b ) / n;
			var meanRadius = pos.Average( p => (p - centre).Length );
			Vec3[] smooth = null;
			if ( kind is MeshFilterKind.EnhanceDetails or MeshFilterKind.Sharpen )
				smooth = Average( Average( pos, nb ), nb );
			Vec3[] hc = kind == MeshFilterKind.SurfaceSmooth ? HcStep( pos, avg, nb ) : null;
			for ( var v = 0; v < n; v++ )
			{
				var k = strength * (mask is null ? 1f : mask[v]);
				if ( k == 0f ) continue;
				var p = pos[v];
				mesh.Positions[v] = kind switch
				{
					// Along the normal only (as Blender's smooth filter): bumps settle, nothing slides.
					MeshFilterKind.Smooth => p + normals[v] * (Vec3.Dot( avg[v] - p, normals[v] ) * Math.Clamp( k, -1f, 1f )),
					MeshFilterKind.Scale => p + (p - centre) * k,
					MeshFilterKind.Inflate => p + normals[v] * k,
					MeshFilterKind.Sphere => p + ((p - centre).LengthSquared > 1e-12f ? centre + (p - centre).Normal * meanRadius - p : Vec3.Zero) * Math.Clamp( k, -1f, 1f ),
					MeshFilterKind.Relax => p + Tangent( avg[v] - p, normals[v] ) * Math.Clamp( k, -1f, 1f ),
					MeshFilterKind.SurfaceSmooth => p + (hc[v] - p) * Math.Clamp( k, -1f, 1f ),
					MeshFilterKind.Sharpen => p - (avg[v] - p) * k * 0.5f + (p - smooth[v]) * k * 0.5f,
					MeshFilterKind.EnhanceDetails => p + (p - smooth[v]) * k,
					_ => p,
				};
			}
		}
	}

	static Vec3 Tangent( Vec3 d, Vec3 n ) => d - n * Vec3.Dot( d, n );

	static Vec3[] Average( Vec3[] pos, int[][] nb )
	{
		var avg = new Vec3[pos.Length];
		for ( var v = 0; v < pos.Length; v++ )
		{
			if ( nb[v].Length == 0 ) { avg[v] = pos[v]; continue; }
			var s = Vec3.Zero;
			foreach ( var u in nb[v] ) s += pos[u];
			avg[v] = s / nb[v].Length;
		}
		return avg;
	}

	/// <summary>One HC-Laplacian step (Vollmer et al. 1999): smooth, then push back by the smoothed
	/// difference so the surface doesn't shrink — alpha 0.5, beta 0.5.</summary>
	static Vec3[] HcStep( Vec3[] original, Vec3[] smoothed, int[][] nb )
	{
		const float alpha = 0.5f, beta = 0.5f;
		var b = new Vec3[original.Length];
		for ( var v = 0; v < original.Length; v++ )
			b[v] = smoothed[v] - (original[v] * alpha + original[v] * (1f - alpha));
		var result = new Vec3[original.Length];
		for ( var v = 0; v < original.Length; v++ )
		{
			var nbAvg = Vec3.Zero;
			foreach ( var u in nb[v] ) nbAvg += b[u];
			nbAvg = nb[v].Length > 0 ? nbAvg / nb[v].Length : Vec3.Zero;
			result[v] = smoothed[v] - (b[v] * beta + nbAvg * (1f - beta));
		}
		return result;
	}
}
