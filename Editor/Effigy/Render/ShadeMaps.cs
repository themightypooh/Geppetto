using System;
using System.Runtime.CompilerServices;

namespace Effigy.Render;

/// <summary>How a material's maps land on the mesh.</summary>
public enum ShadeMapping
{
	/// <summary>The face UVs where a triangle has them; triplanar where it does not and
	/// <see cref="ShadeMaterial.TriplanarScale"/> is set; otherwise one texel, as before maps had tiling.</summary>
	Auto,
	/// <summary>Always the face UVs.</summary>
	UV,
	/// <summary>Always projected from the three axes in model space, <see cref="ShadeMaterial.TriplanarScale"/> units per tile.</summary>
	Triplanar,
}

/// <summary>
/// A material map decoded to floats with a box-filtered mip chain, built once per byte array and kept
/// while the array lives. Mips are what let a leather grain tiled forty times over a glove fade to its
/// average instead of sparkling, and the shortening of averaged normals feeds roughness (Toksvig) so
/// bumpy surfaces seen from afar go satin rather than glittering.
/// </summary>
sealed class MapMips
{
	readonly float[][] _lv; readonly int[] _size;
	public int Levels => _lv.Length;

	static readonly ConditionalWeakTable<byte[], MapMips> Srgb = new(), Linear = new(), Normal = new();

	public static MapMips Get( byte[] map, int size, MapKind kind )
	{
		var table = kind switch { MapKind.Srgb => Srgb, MapKind.Normal => Normal, _ => Linear };
		lock ( table )
		{
			if ( table.TryGetValue( map, out var m ) && m._size[0] == size ) return m;
			m = new MapMips( map, size, kind );
			table.AddOrUpdate( map, m );
			return m;
		}
	}

	MapMips( byte[] map, int size, MapKind kind )
	{
		var count = 1; for ( var s = size; s > 1; s /= 2 ) count++;
		_lv = new float[count][]; _size = new int[count];
		var top = new float[size * size * 3];
		for ( var i = 0; i < size * size && i * 4 + 2 < map.Length; i++ )
			for ( var c = 0; c < 3; c++ )
			{
				var v = map[i * 4 + c] / 255f;
				top[i * 3 + c] = kind switch { MapKind.Srgb => ShadedRender.SrgbToLinear( v ), MapKind.Normal => v * 2f - 1f, _ => v };
			}
		_lv[0] = top; _size[0] = size;
		for ( var l = 1; l < count; l++ )
		{
			var ps = _size[l - 1]; var s = Math.Max( 1, ps / 2 ); var src = _lv[l - 1];
			var dst = new float[s * s * 3];
			for ( var y = 0; y < s; y++ )
				for ( var x = 0; x < s; x++ )
					for ( var c = 0; c < 3; c++ )
					{
						int x0 = Math.Min( 2 * x, ps - 1 ), x1 = Math.Min( 2 * x + 1, ps - 1 ), y0 = Math.Min( 2 * y, ps - 1 ), y1 = Math.Min( 2 * y + 1, ps - 1 );
						dst[(y * s + x) * 3 + c] = (src[(y0 * ps + x0) * 3 + c] + src[(y0 * ps + x1) * 3 + c] + src[(y1 * ps + x0) * 3 + c] + src[(y1 * ps + x1) * 3 + c]) * 0.25f;
					}
			_lv[l] = dst; _size[l] = s;
		}
	}

	/// <summary>Trilinear read. <paramref name="lod"/> 0 is full size; <paramref name="wrapV"/> off clamps v
	/// (an atlas), on repeats it (a tiling texture).</summary>
	public Vec3 Sample( Vec2 uv, float lod, bool wrapV )
	{
		lod = Math.Clamp( lod, 0f, Levels - 1 );
		var l = (int)lod; var f = lod - l;
		var c = Bilinear( l, uv, wrapV );
		return f > 0.001f && l + 1 < Levels ? Vec3.Lerp( c, Bilinear( l + 1, uv, wrapV ), f ) : c;
	}

	Vec3 Bilinear( int l, Vec2 uv, bool wrapV )
	{
		var size = _size[l]; var px = _lv[l];
		var fx = (uv.x - MathF.Floor( uv.x )) * size - 0.5f;
		var fy = (wrapV ? uv.y - MathF.Floor( uv.y ) : Math.Clamp( uv.y, 0f, 1f )) * size - 0.5f;
		var x0 = (int)MathF.Floor( fx ); var y0 = (int)MathF.Floor( fy );
		var tx = fx - x0; var ty = fy - y0;
		Vec3 At( int x, int y )
		{
			x = ((x % size) + size) % size;
			y = wrapV ? ((y % size) + size) % size : Math.Clamp( y, 0, size - 1 );
			var o = (y * size + x) * 3;
			return new Vec3( px[o], px[o + 1], px[o + 2] );
		}
		return Vec3.Lerp( Vec3.Lerp( At( x0, y0 ), At( x0 + 1, y0 ), tx ), Vec3.Lerp( At( x0, y0 + 1 ), At( x0 + 1, y0 + 1 ), tx ), ty );
	}
}

enum MapKind { Srgb, Linear, Normal }
