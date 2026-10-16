using System;
using System.IO;
using System.IO.Compression;

namespace Effigy;

/// <summary>
/// Reads an 8- or 16-bit PNG (greyscale, greyscale + alpha, RGB or RGBA; not interlaced, not
/// paletted) into RGBA, row 0 at the top. Enough for brush alphas, reference images and reading a
/// 16-bit bake back; the inflate is the BCL's DeflateStream, the same one PngWriter uses.
/// </summary>
public static class PngReader
{
	/// <summary>RGBA bytes. A 16-bit file keeps each sample's high byte.</summary>
	public static (byte[] Rgba, int Width, int Height) Read( byte[] png )
	{
		var (wide, width, height) = Read16( png );
		var rgba = new byte[wide.Length];

		for ( var i = 0; i < wide.Length; i++ )
			rgba[i] = (byte)(wide[i] >> 8);

		return (rgba, width, height);
	}

	/// <summary>
	/// RGBA at 16 bits a sample. An 8-bit file is widened by ×257, so 255 reads 65535 and a byte
	/// round-trips exactly through <c>value &gt;&gt; 8</c>.
	/// </summary>
	public static (ushort[] Rgba, int Width, int Height) Read16( byte[] png )
	{
		if ( png is null || png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G' )
			throw new InvalidDataException( "Not a PNG." );
		int width = 0, height = 0, depth = 0, colour = 0, interlace = 0;
		var idat = new MemoryStream();
		var at = 8;
		while ( at + 8 <= png.Length )
		{
			var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
			var type = System.Text.Encoding.ASCII.GetString( png, at + 4, 4 );
			var data = at + 8;
			if ( length < 0 || data + length > png.Length ) throw new InvalidDataException( "Truncated PNG chunk." );
			if ( type == "IHDR" )
			{
				width = (png[data] << 24) | (png[data + 1] << 16) | (png[data + 2] << 8) | png[data + 3];
				height = (png[data + 4] << 24) | (png[data + 5] << 16) | (png[data + 6] << 8) | png[data + 7];
				depth = png[data + 8]; colour = png[data + 9]; interlace = png[data + 12];
			}
			else if ( type == "IDAT" ) idat.Write( png, data, length );
			else if ( type == "IEND" ) break;
			at = data + length + 4;
		}
		if ( depth != 8 && depth != 16 ) throw new NotSupportedException( $"PNG bit depth {depth}: only 8- and 16-bit are read." );
		if ( interlace != 0 ) throw new NotSupportedException( "Interlaced PNG." );
		var channels = colour switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new NotSupportedException( $"PNG colour type {colour} (paletted?)." ) };
		var sampleBytes = depth / 8;
		// The filters work on BYTES, with "the pixel to the left" a whole pixel's bytes back.
		var bpp = channels * sampleBytes;
		// Skip the 2-byte zlib header; DeflateStream reads raw deflate.
		var compressed = idat.ToArray();
		using var inflate = new DeflateStream( new MemoryStream( compressed, 2, compressed.Length - 2 ), CompressionMode.Decompress );
		var stride = width * bpp;
		var raw = new byte[(stride + 1) * height];
		var read = 0;
		while ( read < raw.Length )
		{
			var n = inflate.Read( raw, read, raw.Length - read );
			if ( n <= 0 ) break;
			read += n;
		}
		var pixels = new byte[stride * height];
		for ( var y = 0; y < height; y++ )
		{
			var filter = raw[y * (stride + 1)];
			var src = y * (stride + 1) + 1;
			var dst = y * stride;
			for ( var x = 0; x < stride; x++ )
			{
				int a = x >= bpp ? pixels[dst + x - bpp] : 0;
				int b = y > 0 ? pixels[dst - stride + x] : 0;
				int c = x >= bpp && y > 0 ? pixels[dst - stride + x - bpp] : 0;
				int v = raw[src + x];
				pixels[dst + x] = (byte)(filter switch
				{
					0 => v,
					1 => v + a,
					2 => v + b,
					3 => v + ((a + b) >> 1),
					4 => v + Paeth( a, b, c ),
					_ => throw new InvalidDataException( $"PNG filter {filter}." ),
				});
			}
		}
		// Sample k of pixel i, widened to 16 bits (big-endian in the file).
		ushort S( int i, int k )
		{
			var p = i * bpp + k * sampleBytes;
			return sampleBytes == 2 ? (ushort)((pixels[p] << 8) | pixels[p + 1]) : (ushort)(pixels[p] * 257);
		}
		var rgba = new ushort[width * height * 4];
		for ( var i = 0; i < width * height; i++ )
		{
			var o = i * 4;
			switch ( channels )
			{
				case 1: rgba[o] = rgba[o + 1] = rgba[o + 2] = S( i, 0 ); rgba[o + 3] = 65535; break;
				case 2: rgba[o] = rgba[o + 1] = rgba[o + 2] = S( i, 0 ); rgba[o + 3] = S( i, 1 ); break;
				case 3: rgba[o] = S( i, 0 ); rgba[o + 1] = S( i, 1 ); rgba[o + 2] = S( i, 2 ); rgba[o + 3] = 65535; break;
				default: for ( var k = 0; k < 4; k++ ) rgba[o + k] = S( i, k ); break;
			}
		}
		return (rgba, width, height);
	}

	static int Paeth( int a, int b, int c )
	{
		var p = a + b - c;
		int pa = Math.Abs( p - a ), pb = Math.Abs( p - b ), pc = Math.Abs( p - c );
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}
}
