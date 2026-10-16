using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// A painted body's whole texture set: the images its paint exports as, and the .vmat that binds them.
///
/// <see cref="PaintMaterial"/> is the colour-only path and stays as it was; this is the same chain for
/// every channel. Colour bakes over the default grey, roughness and metalness bake over their own base
/// values as grey images, and a painted height becomes a tangent-space normal map — complex.vfx has no
/// height input for an ordinary surface, but it lights a normal map, and a normal map derived from the
/// painted height is how the bumps a player painted actually show.
///
/// The metalness binding is the one the engine's own material generator writes: <c>F_METALNESS_TEXTURE 1</c>
/// with <c>TextureMetalness</c> beside <c>TextureRoughness</c> (Facepunch/sbox-public commit 0a2c0f0,
/// "Create Material"). A channel nobody painted keeps the engine default, exactly as the colour-only
/// material did.
/// </summary>
public static class PaintTextureSet
{
	/// <summary>One image to write: its file suffix (`color`, `rough`, `metal`, `normal`), RGBA bytes
	/// and size.</summary>
	public sealed class Image
	{
		public string Suffix;
		public byte[] Rgba;
		public int Width, Height;
	}

	/// <summary>
	/// How steep the normal map leans per unit of painted height difference across one texel. A full
	/// black-to-white step over one texel leans the normal by about 63°; a soft brush spreads that over
	/// many texels and reads as a gentle bump. A tuning value, not a physical one — the painted height
	/// has no units, the same as any height-to-normal filter's strength.
	/// </summary>
	public const float DefaultBumpStrength = 2f;

	/// <summary>
	/// Every image the paint on <paramref name="paint"/> exports as, in channel order. Colour is
	/// always present when there is paint at all; the others only when painted.
	/// </summary>
	public static List<Image> Images( PaintCanvas paint, float bumpStrength = DefaultBumpStrength )
	{
		var images = new List<Image>();

		if ( paint is null )
			return images;

		images.Add( new Image { Suffix = "color", Rgba = PaintLayers.Opaque( paint, PaintChannel.Color ), Width = paint.Width, Height = paint.Height } );

		if ( paint.RoughnessMap is { } rough )
			images.Add( new Image { Suffix = "rough", Rgba = PaintLayers.Opaque( paint, PaintChannel.Roughness ), Width = rough.Width, Height = rough.Height } );

		if ( paint.MetalnessMap is { } metal )
			images.Add( new Image { Suffix = "metal", Rgba = PaintLayers.Opaque( paint, PaintChannel.Metalness ), Width = metal.Width, Height = metal.Height } );

		if ( paint.HeightMap is { } height )
			images.Add( new Image { Suffix = "normal", Rgba = HeightToNormal( height, bumpStrength ), Width = height.Width, Height = height.Height } );

		return images;
	}

	/// <summary>
	/// A painted height canvas as a tangent-space normal map (RGBA, alpha 255).
	///
	/// The height is first baked opaque over the flat base (0.5), so unpainted texels are level. The
	/// slope is a central difference: along +x of the image is +u, and down the image (+row) is +v,
	/// because the paint canvas is rasterised with row = v·height — the same walk NormalBake uses. So
	/// in the bake's frame (x along dP/du, y along dP/dv, z out) the normal is
	/// normalize(−s·∂h/∂u, −s·∂h/∂v, 1).
	///
	/// GREEN FOLLOWS NormalBake's SETTLED CONVENTION, not a fresh guess: s&amp;box applies +green along
	/// −dP/dv (see <see cref="BakeOptions.FlipGreen"/>), so y is written negated — a painted dent and
	/// a baked dent light the same way.
	/// </summary>
	public static byte[] HeightToNormal( PaintCanvas height, float strength = DefaultBumpStrength )
	{
		if ( height is null )
			throw new ArgumentNullException( nameof( height ) );

		var w = height.Width;
		var h = height.Height;
		var b = (byte)MathF.Round( PaintChannels.Base( PaintChannel.Height ) * 255f );
		var opaque = new byte[w * h * 4];
		height.BakeOpaque( opaque, b, b, b );

		float H( int x, int y ) => opaque[(Math.Clamp( y, 0, h - 1 ) * w + Math.Clamp( x, 0, w - 1 )) * 4] / 255f;

		var result = new byte[w * h * 4];

		for ( var y = 0; y < h; y++ )
		{
			for ( var x = 0; x < w; x++ )
			{
				var du = (H( x + 1, y ) - H( x - 1, y )) * 0.5f;
				var dv = (H( x, y + 1 ) - H( x, y - 1 )) * 0.5f;
				var n = new Vec3( -strength * du, -strength * dv, 1f ).Normal;
				var o = (y * w + x) * 4;

				result[o] = Encode( n.x );
				result[o + 1] = Encode( -n.y );
				result[o + 2] = Encode( n.z );
				result[o + 3] = 255;
			}
		}

		return result;
	}

	static byte Encode( float v ) => (byte)Math.Clamp( MathF.Round( (v * 0.5f + 0.5f) * 255f ), 0f, 255f );

	/// <summary>
	/// The .vmat for a painted texture set. <paramref name="images"/> maps a suffix from
	/// <see cref="Images"/> to the image path the material should name; a channel missing from it
	/// keeps the engine default. Same Layer0 form and complex.vfx as <see cref="PaintMaterial.VmatSource"/>.
	/// </summary>
	public static string VmatSource( IReadOnlyDictionary<string, string> images )
	{
		if ( images is null || !images.TryGetValue( "color", out var color ) )
			throw new ArgumentException( "A painted texture set always has a colour image.", nameof( images ) );

		var sb = new StringBuilder();
		sb.Append( "// THIS FILE IS AUTO-GENERATED\n\nLayer0\n{\n\tshader \"complex.vfx\"\n" );

		if ( images.ContainsKey( "metal" ) )
			sb.Append( "\tF_METALNESS_TEXTURE 1\n" );

		sb.Append( "\n\t//---- Color ----\n" );
		sb.Append( "\tg_flModelTintAmount \"1.000\"\n" );
		sb.Append( "\tg_vColorTint \"[1.000000 1.000000 1.000000 0.000000]\"\n" );
		sb.Append( $"\tTextureColor \"{color}\"\n" );

		sb.Append( "\n\t//---- Lighting ----\n" );
		sb.Append( "\tg_flDirectionalLightmapMinZ \"0.050\"\n" );
		sb.Append( "\tg_flDirectionalLightmapStrength \"1.000\"\n" );

		sb.Append( "\n\t//---- Metalness ----\n" );

		if ( images.TryGetValue( "metal", out var metal ) )
			sb.Append( $"\tTextureMetalness \"{metal}\"\n" );
		else
			sb.Append( "\tg_flMetalness \"0.000\"\n" );

		sb.Append( "\n\t//---- Normal ----\n" );
		sb.Append( $"\tTextureNormal \"{(images.TryGetValue( "normal", out var normal ) ? normal : "materials/default/default_normal.tga")}\"\n" );

		sb.Append( "\n\t//---- Roughness ----\n" );
		sb.Append( $"\tTextureRoughness \"{(images.TryGetValue( "rough", out var rough ) ? rough : "materials/default/default_rough.tga")}\"\n" );

		sb.Append( "\n\t//---- Texture Coordinates ----\n" );
		sb.Append( "\tg_nScaleTexCoordUByModelScaleAxis \"0\"\n" );
		sb.Append( "\tg_nScaleTexCoordVByModelScaleAxis \"0\"\n" );
		sb.Append( "\tg_vTexCoordOffset \"[0.000 0.000]\"\n" );
		sb.Append( "\tg_vTexCoordScale \"[1.000 1.000]\"\n" );
		sb.Append( "\tg_vTexCoordScrollSpeed \"[0.000 0.000]\"\n" );
		sb.Append( "}\n" );

		return sb.ToString();
	}

	/// <summary>
	/// Write the whole set into <paramref name="folder"/>: <c>{baseName}.png</c> for colour,
	/// <c>{baseName}_rough/_metal/_normal.png</c> for the rest, and <c>{baseName}.vmat</c> naming them
	/// by <paramref name="assetPath"/> (file path → the path the material should name). Returns every
	/// file written, the .vmat last. The editor's compile and the `paintmaps` script verb both call
	/// this, so a painted body exports the same files from either.
	/// </summary>
	public static List<string> Write( PaintCanvas paint, string folder, string baseName, Func<string, string> assetPath,
		float bumpStrength = DefaultBumpStrength )
	{
		if ( paint is null )
			throw new ArgumentNullException( nameof( paint ) );

		Directory.CreateDirectory( folder );
		var written = new List<string>();
		var named = new Dictionary<string, string>();

		foreach ( var image in Images( paint, bumpStrength ) )
		{
			var path = Path.Combine( folder, image.Suffix == "color" ? $"{baseName}.png" : $"{baseName}_{image.Suffix}.png" );
			PngWriter.WriteFileRgba( path, image.Rgba, image.Width, image.Height );
			written.Add( path );
			named[image.Suffix] = assetPath( path );
		}

		var vmat = Path.Combine( folder, $"{baseName}.vmat" );
		File.WriteAllText( vmat, VmatSource( named ) );
		written.Add( vmat );
		return written;
	}

	/// <summary>
	/// A material for Effigy's own renderer showing this paint the way the exported set will: the
	/// colour, roughness and metallic images and the normal map from height, all at the colour
	/// canvas's size (a channel painted at another resolution is resampled to it).
	/// </summary>
	public static Effigy.Render.ShadeMaterial Shade( PaintCanvas paint, float bumpStrength = DefaultBumpStrength )
	{
		if ( paint is null )
			throw new ArgumentNullException( nameof( paint ) );

		var size = paint.Width;

		byte[] At( PaintCanvas canvas, PaintChannel channel )
		{
			if ( canvas is null )
				return null;

			var holder = new PaintCanvas( size, size );
			var resized = canvas.Width == size && canvas.Height == size ? canvas : PaintLayers.Resample( canvas, size, size );

			switch ( channel )
			{
				case PaintChannel.Roughness: holder.RoughnessMap = resized; break;
				case PaintChannel.Metalness: holder.MetalnessMap = resized; break;
				default: return PaintLayers.Opaque( resized, PaintChannel.Color );
			}

			return PaintLayers.Opaque( holder, channel );
		}

		var height = paint.HeightMap is null ? null
			: paint.HeightMap.Width == size ? paint.HeightMap : PaintLayers.Resample( paint.HeightMap, size, size );

		return new Effigy.Render.ShadeMaterial
		{
			MapSize = size,
			AlbedoMap = PaintLayers.Opaque( paint, PaintChannel.Color ),
			Roughness = PaintChannels.Base( PaintChannel.Roughness ),
			RoughnessMap = At( paint.RoughnessMap, PaintChannel.Roughness ),
			MetallicMap = At( paint.MetalnessMap, PaintChannel.Metalness ),
			NormalMap = height is null ? null : HeightToNormal( height, bumpStrength ),
		};
	}
}
