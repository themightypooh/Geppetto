using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Where a paint layer's mask comes from, before any mask painting on top of it.</summary>
public enum PaintMaskGenerator
{
	/// <summary>No generator: the layer shows everywhere (unless mask paint says otherwise).</summary>
	None,

	/// <summary>Convex edges and ridges, from the curvature bake — where a surface rubs and wears.</summary>
	EdgeWear,

	/// <summary>Crevices and shut-in places, from the ambient-occlusion and cavity bakes — where grime settles.</summary>
	Dirt,

	/// <summary>Bottom to top of the body: 0 at its lowest point, 1 at its highest.</summary>
	Gradient,

	/// <summary>The faces on one material slot: 1 on them, 0 everywhere else.</summary>
	MaterialId,
}

/// <summary>
/// Procedural masks for fill layers, per texel on the body's own UV atlas.
///
/// THE CURVATURE AND OCCLUSION ARE BAKED, NOT INTERPOLATED FROM VERTICES. A low box has eight
/// vertices and a per-vertex curvature would be one number smeared across each face; the bake
/// (<see cref="NormalBake.BakeAll"/>, the body baked onto itself) samples every texel, so a box's
/// edges read as edges. The same bake the Bake tool already ships, not a second estimate of it.
///
/// Gradient and Material ID need no rays: the first is the height of the surface point a texel
/// stands for, the second whether the face that owns the texel wears the slot.
///
/// Every generator returns raw values 0..1 at the given size; <see cref="PaintLayers.Shape"/> turns
/// them into a mask with the layer's level and softness. A texel no face owns is 0.
/// </summary>
public static class PaintMasks
{
	/// <summary>
	/// The largest size a ray-traced generator bakes at; above it the bake is upsampled. A mask is
	/// a soft thing and 512 texels already resolve a bevel, while ambient occlusion at 4096² is
	/// minutes of rays for nothing a viewer could see.
	/// </summary>
	public const int MaxBakeSize = 512;

	/// <summary>
	/// The raw values for <paramref name="generator"/> at <paramref name="size"/>², or null for
	/// <see cref="PaintMaskGenerator.None"/>. <paramref name="slot"/> is the material slot Material ID
	/// selects.
	/// </summary>
	public static float[] Generate( PolyMesh mesh, int size, PaintMaskGenerator generator, int slot )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		return generator switch
		{
			PaintMaskGenerator.EdgeWear => Baked( mesh, size, BakeMaps.Curvature, set =>
				i => Math.Clamp( (set.Curvature.Rgb[i * 3] / 255f - 0.5f) * 2f, 0f, 1f ) ),

			// Dirt is wherever either says "shut in": occlusion catches the deep folds, cavity the
			// fine creases occlusion is too coarse to see.
			PaintMaskGenerator.Dirt => Baked( mesh, size, BakeMaps.AmbientOcclusion | BakeMaps.Cavity, set =>
				i => MathF.Max( 1f - set.AmbientOcclusion.Rgb[i * 3] / 255f, 1f - set.Cavity.Rgb[i * 3] / 255f ) ),

			PaintMaskGenerator.Gradient => Gradient( mesh, size ),
			PaintMaskGenerator.MaterialId => FaceValues( mesh, size, face => face.Material == slot ? 1f : 0f ),
			_ => null,
		};
	}

	/// <summary>Whether any face of <paramref name="mesh"/> carries a full set of UVs — without one,
	/// no texel belongs to any face and a generator has nothing to say.</summary>
	public static bool HasUVs( PolyMesh mesh )
	{
		foreach ( var face in mesh.Faces )
		{
			if ( face.Count >= 3 && face.UVs is not null && face.UVs.Length == face.Count )
				return true;
		}

		return false;
	}

	static float[] Baked( PolyMesh mesh, int size, BakeMaps maps, Func<BakeSet, Func<int, float>> read )
	{
		var bakeSize = Math.Min( size, MaxBakeSize );
		var set = NormalBake.BakeAll( mesh, mesh, bakeSize, bakeSize, maps, new BakeOptions { AoRays = 32 } );
		var value = read( set );
		var raw = new float[bakeSize * bakeSize];

		for ( var i = 0; i < raw.Length; i++ )
			raw[i] = value( i );

		// The bake's rows run with v (row 0 at v = 0), the same as the paint canvas's Rasterise walk,
		// so no flip: both go through NormalBake.Rasterise.
		return PaintLayers.Resample( raw, bakeSize, bakeSize, size, size );
	}

	/// <summary>Height up the body (+z), 0 at its lowest vertex and 1 at its highest, per texel.</summary>
	static float[] Gradient( PolyMesh mesh, int size )
	{
		var lo = float.MaxValue;
		var hi = float.MinValue;

		foreach ( var p in mesh.Positions )
		{
			lo = MathF.Min( lo, p.z );
			hi = MathF.Max( hi, p.z );
		}

		var span = hi - lo > 1e-6f ? hi - lo : 1f;

		return Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) =>
		{
			var z = mesh.Positions[face.Indices[a]].z * wa + mesh.Positions[face.Indices[b]].z * wb
				+ mesh.Positions[face.Indices[c]].z * wc;
			return Math.Clamp( (z - lo) / span, 0f, 1f );
		} );
	}

	/// <summary>One value per face, written to every texel the face owns.</summary>
	public static float[] FaceValues( PolyMesh mesh, int size, Func<Face, float> value ) =>
		Rasterised( mesh, size, ( face, a, b, c, wa, wb, wc ) => value( face ) );

	delegate float TexelValue( Face face, int a, int b, int c, float wa, float wb, float wc );

	/// <summary>Walk every UV triangle with the same rasteriser the paint dab and the bake use, so a
	/// mask and a stroke agree about which texel a face owns, then bleed values a few texels past the
	/// islands so filtering at a seam does not reach the empty gutter.</summary>
	static float[] Rasterised( PolyMesh mesh, int size, TexelValue value )
	{
		var result = new float[size * size];
		var filled = new bool[size * size];

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var k = 0; k < face.Count; k++ )
				corners.Add( mesh.Positions[face.Indices[k]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var f = face;
				NormalBake.Rasterise( face.UVs[ia], face.UVs[ib], face.UVs[ic], size, size, ( x, y, wa, wb, wc ) =>
				{
					var i = y * size + x;
					result[i] = value( f, ia, ib, ic, wa, wb, wc );
					filled[i] = true;
				} );
			}
		}

		Bleed( result, filled, size, 4 );
		return result;
	}

	static void Bleed( float[] values, bool[] filled, int size, int passes )
	{
		for ( var pass = 0; pass < passes; pass++ )
		{
			var added = new List<(int, float)>();

			for ( var y = 0; y < size; y++ )
			{
				for ( var x = 0; x < size; x++ )
				{
					var i = y * size + x;

					if ( filled[i] )
						continue;

					float sum = 0;
					var n = 0;

					for ( var dy = -1; dy <= 1; dy++ )
					{
						for ( var dx = -1; dx <= 1; dx++ )
						{
							int nx = x + dx, ny = y + dy;

							if ( nx < 0 || ny < 0 || nx >= size || ny >= size || !filled[ny * size + nx] )
								continue;

							sum += values[ny * size + nx];
							n++;
						}
					}

					if ( n > 0 )
						added.Add( (i, sum / n) );
				}
			}

			if ( added.Count == 0 )
				return;

			foreach ( var (i, v) in added )
			{
				values[i] = v;
				filled[i] = true;
			}
		}
	}
}
