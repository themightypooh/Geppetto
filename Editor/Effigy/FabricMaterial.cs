using System;
using System.Globalization;

namespace Effigy;

/// <summary>
/// Real fabric for a garment: a tiling colour, normal and roughness set computed from a weave,
/// and a Complex .vmat with the switches the store's own items use for that kind of cloth.
///
/// WHY PROCEDURAL. A garment with the default grey material is not a shirt yet, and asking for a
/// texture is asking for a second tool and a week of learning it. Every fabric people actually
/// put on a character is a small repeating structure - a knit's loops, a twill's diagonal, cord's
/// ridges, plaid's stripes - and small repeating structures are what a few lines of maths make
/// well. A 512 tile at a foot to a UV unit (see GarmentFit.LimbUVs) is sharp up close and repeats
/// without a seam, which is the whole job of a fabric texture.
///
/// THE SWITCHES MATTER MORE THAN THE PIXELS. Complex's cloth shading is what makes fleece and
/// velvet look soft, and anisotropic gloss is what makes satin sheen along the weave; without
/// them a perfect texture still reads as painted plastic. See CLOTHING.md for which shipped item
/// uses which. Everything here is a starting point to recolour and rescale, not a claim about any
/// real mill's cloth.
/// </summary>
public static class FabricMaterial
{
	public const int Size = 512;

	public static readonly string[] Presets =
	{
		"Jersey", "Rib knit", "Fleece", "Denim", "Corduroy", "Leather", "Satin", "Canvas", "Plaid", "Gingham", "Quilted",
	};

	/// <summary>Which Complex feature a preset leans on: none, cloth shading, or anisotropic gloss.</summary>
	public static string ShadingFor( string preset ) => preset switch
	{
		"Fleece" or "Corduroy" or "Rib knit" or "Jersey" => "cloth",
		"Satin" => "anisotropic",
		_ => "",
	};

	/// <summary>Default roughness, 0 glossy to 1 matte. Leather and satin shine; knits do not.</summary>
	public static float RoughnessFor( string preset ) => preset switch
	{
		"Leather" => 0.45f,
		"Satin" => 0.35f,
		"Denim" or "Canvas" => 0.85f,
		_ => 0.92f,
	};

	/// <summary>
	/// The maps: colour and normal as RGBA, roughness as RGBA grey. <paramref name="colour"/> is
	/// the cloth, <paramref name="accent"/> the second thread of a plaid or gingham. <paramref
	/// name="scale"/> is how many repeats across the tile - more is a finer weave.
	/// </summary>
	public static (byte[] Color, byte[] Normal, byte[] Roughness) Maps( string preset, Vec3 colour, Vec3 accent,
		float scale, float wear, int seed = 1 )
	{
		var size = Size;
		var color = new byte[size * size * 4];
		var normal = new byte[size * size * 4];
		var rough = new byte[size * size * 4];
		var height = new float[size * size];
		var tint = new float[size * size * 3];
		var baseRough = RoughnessFor( preset );

		// A whole number of repeats, or the tile would not wrap.
		var repeats = Math.Max( 1, (int)MathF.Round( scale ) );
		scale = repeats;

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				// Periodic in [0,1) so the tile repeats without a seam whatever the scale.
				var u = x / (float)size * scale;
				var v = y / (float)size * scale;
				var i = y * size + x;
				var (h, mix) = Weave( preset, u, v, seed, repeats );

				// Wear: a slow blotch of lighter, rougher cloth, the way knees and elbows go.
				var w = wear * (Noise( x / 64f, y / 64f, seed + 11, size / 64 ) * 0.5f + 0.5f);
				height[i] = h;

				var c = colour * (1f - mix) + accent * mix;
				c = c * (1f - w * 0.25f) + new Vec3( 1f, 1f, 1f ) * (w * 0.25f);

				// Thread-level shade: deeper in the weave is darker.
				var shade = 0.82f + 0.18f * h;
				tint[i * 3] = c.x * shade;
				tint[i * 3 + 1] = c.y * shade;
				tint[i * 3 + 2] = c.z * shade;

				var r = Math.Clamp( baseRough + (0.5f - h) * 0.15f + w * 0.1f, 0.05f, 1f );
				var rb = Byte( r );
				rough[i * 4] = rb;
				rough[i * 4 + 1] = rb;
				rough[i * 4 + 2] = rb;
				rough[i * 4 + 3] = 255;
			}
		}

		// Normals from the height, wrapped, so the tile's edges match.
		var strength = preset is "Leather" or "Satin" ? 1.5f : 4f;

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var i = y * size + x;
				var dx = height[y * size + (x + 1) % size] - height[y * size + (x + size - 1) % size];
				var dy = height[((y + 1) % size) * size + x] - height[((y + size - 1) % size) * size + x];
				var n = new Vec3( -dx * strength, -dy * strength, 1f ).Normal;

				normal[i * 4] = Byte( n.x * 0.5f + 0.5f );
				normal[i * 4 + 1] = Byte( n.y * 0.5f + 0.5f );
				normal[i * 4 + 2] = Byte( n.z * 0.5f + 0.5f );
				normal[i * 4 + 3] = 255;

				color[i * 4] = Byte( tint[i * 3] );
				color[i * 4 + 1] = Byte( tint[i * 3 + 1] );
				color[i * 4 + 2] = Byte( tint[i * 3 + 2] );
				color[i * 4 + 3] = 255;
			}
		}

		return (color, normal, rough);
	}

	/// <summary>Height 0..1 of the weave at a periodic (u, v), and how much of the accent colour
	/// is there. Each preset is a different small structure.</summary>
	static (float Height, float Mix) Weave( string preset, float u, float v, int seed, int repeats )
	{
		var fu = u - MathF.Floor( u );
		var fv = v - MathF.Floor( v );

		switch ( preset )
		{
			case "Jersey":
			{
				// Interlocking V loops: columns of stitches, each row offset.
				var col = MathF.Floor( u * 2f );
				var row = fv + (((int)col & 1) == 0 ? 0f : 0.5f);
				row -= MathF.Floor( row );
				var arc = MathF.Sin( row * MathF.PI );
				var side = MathF.Abs( (u * 2f - MathF.Floor( u * 2f )) - 0.5f ) * 2f;
				return (Math.Clamp( arc * (1f - side * 0.6f), 0f, 1f ), 0f);
			}

			case "Rib knit":
			{
				// Alternating raised wales, twice the loop pitch.
				var wale = 0.5f + 0.5f * MathF.Cos( u * MathF.PI * 2f );
				var loop = 0.5f + 0.5f * MathF.Cos( v * MathF.PI * 4f );
				return (wale * 0.75f + loop * 0.25f, 0f);
			}

			case "Fleece":
			{
				// Fine matted pile: high-frequency noise, no structure to speak of.
				var n = Noise( u * 8f, v * 8f, seed, repeats * 8 ) * 0.6f + Noise( u * 32f, v * 32f, seed + 3, repeats * 32 ) * 0.4f;
				return (n * 0.5f + 0.5f, 0f);
			}

			case "Denim":
			{
				// 3/1 twill: the diagonal that makes denim denim, with the white weft showing in
				// the troughs.
				var d = (u + v) * 4f;
				var t = d - MathF.Floor( d );
				var ridge = t < 0.75f ? 1f - MathF.Abs( t / 0.75f - 0.5f ) * 2f : 0f;
				var thread = 0.5f + 0.5f * MathF.Cos( u * MathF.PI * 16f );
				return (ridge * 0.8f + thread * 0.2f, (1f - ridge) * 0.35f);
			}

			case "Corduroy":
			{
				// Wide soft ridges along v, with a hint of the pile's fuzz.
				var ridge = 0.5f + 0.5f * MathF.Cos( u * MathF.PI * 2f );
				return (MathF.Pow( ridge, 0.7f ) * 0.9f + Noise( u * 30f, v * 30f, seed, repeats * 30 ) * 0.1f, 0f);
			}

			case "Leather":
			{
				// Grain: cells of noise with creases between them.
				var cell = Noise( u * 6f, v * 6f, seed, repeats * 6 ) * 0.5f + Noise( u * 24f, v * 24f, seed + 5, repeats * 24 ) * 0.5f;
				var crease = MathF.Abs( Noise( u * 3f, v * 3f, seed + 9, repeats * 3 ) );
				return (Math.Clamp( 0.55f + cell * 0.3f - crease * 0.25f, 0f, 1f ), 0f);
			}

			case "Satin":
			{
				// Almost flat; long floats along u give the anisotropic gloss its direction.
				var f = 0.5f + 0.5f * MathF.Cos( v * MathF.PI * 24f );
				return (0.5f + f * 0.08f, 0f);
			}

			case "Canvas":
			{
				// Plain weave, coarse: over-under both ways.
				var a = MathF.Floor( u * 4f ) + MathF.Floor( v * 4f );
				var over = ((int)a & 1) == 0;
				var warp = 0.5f + 0.5f * MathF.Cos( (u * 4f - MathF.Floor( u * 4f ) - 0.5f) * MathF.PI * 2f );
				var weft = 0.5f + 0.5f * MathF.Cos( (v * 4f - MathF.Floor( v * 4f ) - 0.5f) * MathF.PI * 2f );
				return (over ? 0.5f + warp * 0.5f : 0.5f + weft * 0.5f, 0f);
			}

			case "Plaid":
			{
				// Two colours in wide bands both ways, with a plain weave under them.
				var bandU = fu < 0.5f ? 1f : 0f;
				var bandV = fv < 0.5f ? 1f : 0f;
				var mix = bandU + bandV > 1.5f ? 1f : bandU + bandV > 0.5f ? 0.5f : 0f;
				var a = MathF.Floor( u * 16f ) + MathF.Floor( v * 16f );
				var weave = ((int)a & 1) == 0 ? 0.6f : 0.4f;
				return (weave, mix);
			}

			case "Gingham":
			{
				// Checks: the overlap of a stripe both ways is the full colour, one is half.
				var bandU = fu < 0.5f ? 1f : 0f;
				var bandV = fv < 0.5f ? 1f : 0f;
				var mix = (bandU + bandV) * 0.5f;
				var a = MathF.Floor( u * 24f ) + MathF.Floor( v * 24f );
				var weave = ((int)a & 1) == 0 ? 0.6f : 0.4f;
				return (weave, mix);
			}

			case "Quilted":
			{
				// Diamond puffs with stitched seams.
				var du = MathF.Abs( fu - 0.5f );
				var dv = MathF.Abs( fv - 0.5f );
				var puff = 1f - Math.Clamp( (du + dv) * 2f, 0f, 1f );
				return (MathF.Sqrt( puff ) * 0.9f + Noise( u * 20f, v * 20f, seed, repeats * 20 ) * 0.1f, 0f);
			}
		}

		return (0.5f, 0f);
	}

	/// <summary>The .vmat, with the switch the preset's shading asks for.</summary>
	public static string VmatSource( string preset, string colorPng, string normalPng, string roughPng )
	{
		static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

		var shading = ShadingFor( preset );
		var extra = shading switch
		{
			"cloth" => "\tF_CLOTH_SHADING 1\n\tg_flClothFuzz \"0.500\"\n",
			"anisotropic" => "\tF_ANISOTROPIC_GLOSS 1\n\tg_flAnisotropicGloss \"0.700\"\n",
			_ => "",
		};

		return
			"// THIS FILE IS AUTO-GENERATED by an Effigy Fabric feature. Edit the feature, not this file.\n" +
			"Layer0\n" +
			"{\n" +
			"\tshader \"complex.vfx\"\n" +
			extra +
			"\n" +
			"\t//---- Color ----\n" +
			"\tg_flModelTintAmount \"1.000\"\n" +
			"\tg_vColorTint \"[1.000000 1.000000 1.000000 0.000000]\"\n" +
			$"\tTextureColor \"{colorPng}\"\n" +
			"\n" +
			"\t//---- Metalness ----\n" +
			"\tg_flMetalness \"0.000\"\n" +
			"\n" +
			"\t//---- Normal ----\n" +
			$"\tTextureNormal \"{normalPng}\"\n" +
			"\n" +
			"\t//---- Roughness ----\n" +
			$"\tTextureRoughness \"{roughPng}\"\n" +
			$"\tg_flRoughnessScaleFactor \"{F( 1f )}\"\n" +
			"\n" +
			"\t//---- Texture Coordinates ----\n" +
			"\tg_nScaleTexCoordUByModelScaleAxis \"0\"\n" +
			"\tg_nScaleTexCoordVByModelScaleAxis \"0\"\n" +
			"\tg_vTexCoordOffset \"[0.000 0.000]\"\n" +
			"\tg_vTexCoordScale \"[1.000 1.000]\"\n" +
			"\tg_vTexCoordScrollSpeed \"[0.000 0.000]\"\n" +
			"}\n";
	}

	static byte Byte( float v ) => (byte)Math.Clamp( (int)MathF.Round( v * 255f ), 0, 255 );

	/// <summary>Smooth value noise in -1..1 on an integer lattice that repeats every
	/// <paramref name="period"/> units, so a tile sampled over a whole number of periods wraps.</summary>
	static float Noise( float x, float y, int seed, int period )
	{
		var x0 = (int)MathF.Floor( x );
		var y0 = (int)MathF.Floor( y );
		period = Math.Max( 1, period );
		var fx = x - x0;
		var fy = y - y0;
		var sx = fx * fx * (3f - 2f * fx);
		var sy = fy * fy * (3f - 2f * fy);

		float H( int a, int b )
		{
			a = ((a % period) + period) % period;
			b = ((b % period) + period) % period;

			unchecked
			{
				var h = a * 374761393 + b * 668265263 + seed * 1274126177;
				h = (h ^ (h >> 13)) * 1274126177;
				return ((h ^ (h >> 16)) & 0xffff) / 32767.5f - 1f;
			}
		}

		var a = H( x0, y0 );
		var b = H( x0 + 1, y0 );
		var c = H( x0, y0 + 1 );
		var d = H( x0 + 1, y0 + 1 );

		return (a + (b - a) * sx) * (1f - sy) + (c + (d - c) * sx) * sy;
	}
}
