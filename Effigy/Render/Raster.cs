using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Effigy.Render;

/// <summary>
/// A framebuffer with a depth buffer, a few drawing calls, a 3×5 bitmap font, and PNG in and out.
///
/// NO IMAGE LIBRARY, ON PURPOSE. The kernel runs in three places — the test harness, the editor
/// assembly and whatever mounts it next — and the one thing they all have is System.IO. Two
/// hundred lines of rasteriser and codec is cheaper than a dependency in every one of them, and
/// it is what makes a picture of the model available anywhere the kernel is.
///
/// This exists so an agent can SEE what it built: <see cref="ModelRender"/> draws the studio into
/// one of these, and the PNG is something a model can read back. Pixels are packed 0xRRGGBB.
/// </summary>
public sealed class Raster
{
	public readonly int Width;
	public readonly int Height;
	public readonly int[] Pixels;
	public readonly float[] Depth;

	public Raster( int width, int height, int background = 0x121418 )
	{
		Width = Math.Max( 1, width );
		Height = Math.Max( 1, height );
		Pixels = new int[Width * Height];
		Depth = new float[Width * Height];

		Clear( background );
	}

	public static int Rgb( int r, int g, int b ) =>
		(Math.Clamp( r, 0, 255 ) << 16) | (Math.Clamp( g, 0, 255 ) << 8) | Math.Clamp( b, 0, 255 );

	public static int Blend( int under, int over, float alpha )
	{
		alpha = Math.Clamp( alpha, 0f, 1f );
		var r = ((under >> 16) & 0xFF) + (((over >> 16) & 0xFF) - ((under >> 16) & 0xFF)) * alpha;
		var g = ((under >> 8) & 0xFF) + (((over >> 8) & 0xFF) - ((under >> 8) & 0xFF)) * alpha;
		var b = (under & 0xFF) + ((over & 0xFF) - (under & 0xFF)) * alpha;
		return Rgb( (int)r, (int)g, (int)b );
	}

	public void Clear( int colour )
	{
		Array.Fill( Pixels, colour );
		Array.Fill( Depth, float.MaxValue );
	}

	public void Plot( int x, int y, int colour, float alpha = 1f )
	{
		if ( x < 0 || y < 0 || x >= Width || y >= Height )
			return;

		var i = y * Width + x;
		Pixels[i] = alpha >= 1f ? colour : Blend( Pixels[i], colour, alpha );
	}

	/// <summary>Bresenham, with an optional depth so a wire hides behind a nearer face.</summary>
	public void Line( float x0, float y0, float x1, float y1, int colour, float alpha = 1f, float z0 = float.MinValue, float z1 = float.MinValue, float bias = 0f )
	{
		var dx = MathF.Abs( x1 - x0 );
		var dy = MathF.Abs( y1 - y0 );
		var steps = (int)MathF.Ceiling( MathF.Max( dx, dy ) );

		if ( steps == 0 )
		{
			PlotZ( (int)MathF.Round( x0 ), (int)MathF.Round( y0 ), colour, alpha, z0, bias );
			return;
		}

		for ( var s = 0; s <= steps; s++ )
		{
			var t = s / (float)steps;
			var x = (int)MathF.Round( x0 + (x1 - x0) * t );
			var y = (int)MathF.Round( y0 + (y1 - y0) * t );
			var z = z0 == float.MinValue ? float.MinValue : z0 + (z1 - z0) * t;
			PlotZ( x, y, colour, alpha, z, bias );
		}
	}

	void PlotZ( int x, int y, int colour, float alpha, float z, float bias )
	{
		if ( x < 0 || y < 0 || x >= Width || y >= Height )
			return;

		var i = y * Width + x;

		if ( z != float.MinValue && z - bias > Depth[i] )
			return;

		Pixels[i] = alpha >= 1f ? colour : Blend( Pixels[i], colour, alpha );
	}

	/// <summary>A depth-tested triangle. Screen x, y and a depth per corner; nearer is smaller.</summary>
	public void Triangle( Vec2 a, Vec2 b, Vec2 c, float za, float zb, float zc, int colour, bool[] mask = null )
	{
		var minX = Math.Max( 0, (int)MathF.Floor( MathF.Min( a.x, MathF.Min( b.x, c.x ) ) ) );
		var maxX = Math.Min( Width - 1, (int)MathF.Ceiling( MathF.Max( a.x, MathF.Max( b.x, c.x ) ) ) );
		var minY = Math.Max( 0, (int)MathF.Floor( MathF.Min( a.y, MathF.Min( b.y, c.y ) ) ) );
		var maxY = Math.Min( Height - 1, (int)MathF.Ceiling( MathF.Max( a.y, MathF.Max( b.y, c.y ) ) ) );

		var area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

		if ( MathF.Abs( area ) < 1e-8f )
			return;

		var inv = 1f / area;

		for ( var y = minY; y <= maxY; y++ )
		{
			var py = y + 0.5f;

			for ( var x = minX; x <= maxX; x++ )
			{
				var px = x + 0.5f;

				var w0 = ((b.x - px) * (c.y - py) - (b.y - py) * (c.x - px)) * inv;
				var w1 = ((c.x - px) * (a.y - py) - (c.y - py) * (a.x - px)) * inv;
				var w2 = 1f - w0 - w1;

				if ( w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f )
					continue;

				var z = w0 * za + w1 * zb + w2 * zc;
				var i = y * Width + x;

				if ( z > Depth[i] )
					continue;

				Depth[i] = z;
				Pixels[i] = colour;

				if ( mask is not null )
					mask[i] = true;
			}
		}
	}

	public void Rect( int x, int y, int w, int h, int colour, float alpha = 1f )
	{
		for ( var yy = y; yy < y + h; yy++ )
			for ( var xx = x; xx < x + w; xx++ )
				Plot( xx, yy, colour, alpha );
	}

	// --- text ---------------------------------------------------------------------------------

	public static int TextWidth( string text, int scale = 2 ) => (text?.Length ?? 0) * 4 * scale;

	public void Text( int x, int y, string text, int colour, int scale = 2 )
	{
		if ( string.IsNullOrEmpty( text ) )
			return;

		var cursor = x;

		foreach ( var raw in text.ToLowerInvariant() )
		{
			if ( !Glyphs.TryGetValue( raw, out var glyph ) )
				glyph = Glyphs[' '];

			for ( var gy = 0; gy < 5; gy++ )
				for ( var gx = 0; gx < 3; gx++ )
				{
					if ( glyph[gy][gx] != '#' )
						continue;

					for ( var sy = 0; sy < scale; sy++ )
						for ( var sx = 0; sx < scale; sx++ )
							Plot( cursor + gx * scale + sx, y + gy * scale + sy, colour );
				}

			cursor += 4 * scale;
		}
	}

	static readonly Dictionary<char, string[]> Glyphs = new()
	{
		['a'] = new[] { "###", "# #", "###", "# #", "# #" },
		['b'] = new[] { "## ", "# #", "## ", "# #", "## " },
		['c'] = new[] { "###", "#  ", "#  ", "#  ", "###" },
		['d'] = new[] { "## ", "# #", "# #", "# #", "## " },
		['e'] = new[] { "###", "#  ", "###", "#  ", "###" },
		['f'] = new[] { "###", "#  ", "###", "#  ", "#  " },
		['g'] = new[] { "###", "#  ", "# #", "# #", "###" },
		['h'] = new[] { "# #", "# #", "###", "# #", "# #" },
		['i'] = new[] { "###", " # ", " # ", " # ", "###" },
		['j'] = new[] { "  #", "  #", "  #", "# #", "###" },
		['k'] = new[] { "# #", "# #", "## ", "# #", "# #" },
		['l'] = new[] { "#  ", "#  ", "#  ", "#  ", "###" },
		['m'] = new[] { "# #", "###", "###", "# #", "# #" },
		['n'] = new[] { "## ", "# #", "# #", "# #", "# #" },
		['o'] = new[] { "###", "# #", "# #", "# #", "###" },
		['p'] = new[] { "###", "# #", "###", "#  ", "#  " },
		['q'] = new[] { "###", "# #", "# #", "###", "  #" },
		['r'] = new[] { "###", "# #", "## ", "# #", "# #" },
		['s'] = new[] { "###", "#  ", "###", "  #", "###" },
		['t'] = new[] { "###", " # ", " # ", " # ", " # " },
		['u'] = new[] { "# #", "# #", "# #", "# #", "###" },
		['v'] = new[] { "# #", "# #", "# #", "# #", " # " },
		['w'] = new[] { "# #", "# #", "###", "###", "# #" },
		['x'] = new[] { "# #", "# #", " # ", "# #", "# #" },
		['y'] = new[] { "# #", "# #", "###", " # ", " # " },
		['z'] = new[] { "###", "  #", " # ", "#  ", "###" },
		['0'] = new[] { "###", "# #", "# #", "# #", "###" },
		['1'] = new[] { " # ", "## ", " # ", " # ", "###" },
		['2'] = new[] { "###", "  #", "###", "#  ", "###" },
		['3'] = new[] { "###", "  #", "###", "  #", "###" },
		['4'] = new[] { "# #", "# #", "###", "  #", "  #" },
		['5'] = new[] { "###", "#  ", "###", "  #", "###" },
		['6'] = new[] { "###", "#  ", "###", "# #", "###" },
		['7'] = new[] { "###", "  #", "  #", "  #", "  #" },
		['8'] = new[] { "###", "# #", "###", "# #", "###" },
		['9'] = new[] { "###", "# #", "###", "  #", "###" },
		['_'] = new[] { "   ", "   ", "   ", "   ", "###" },
		['-'] = new[] { "   ", "   ", "###", "   ", "   " },
		['+'] = new[] { "   ", " # ", "###", " # ", "   " },
		['('] = new[] { " ##", "#  ", "#  ", "#  ", " ##" },
		[')'] = new[] { "## ", "  #", "  #", "  #", "## " },
		['.'] = new[] { "   ", "   ", "   ", "   ", " # " },
		[','] = new[] { "   ", "   ", "   ", " # ", "#  " },
		[':'] = new[] { "   ", " # ", "   ", " # ", "   " },
		['/'] = new[] { "  #", "  #", " # ", "#  ", "#  " },
		['%'] = new[] { "# #", "  #", " # ", "#  ", "# #" },
		[' '] = new[] { "   ", "   ", "   ", "   ", "   " },
	};

	// --- PNG out ------------------------------------------------------------------------------

	public void SavePng( string path ) => WritePng( path, Pixels, Width, Height );

	public byte[] ToPng() => EncodePng( Pixels, Width, Height );

	public static void WritePng( string path, int[] pixels, int w, int h ) =>
		File.WriteAllBytes( path, EncodePng( pixels, w, h ) );

	public static byte[] EncodePng( int[] pixels, int w, int h )
	{
		var raw = new byte[h * (w * 3 + 1)];
		var o = 0;

		for ( var y = 0; y < h; y++ )
		{
			raw[o++] = 0;

			for ( var x = 0; x < w; x++ )
			{
				var p = pixels[y * w + x];
				raw[o++] = (byte)((p >> 16) & 0xFF);
				raw[o++] = (byte)((p >> 8) & 0xFF);
				raw[o++] = (byte)(p & 0xFF);
			}
		}

		using var ms = new MemoryStream();
		ms.Write( new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } );

		var ihdr = new byte[13];
		WriteBe( ihdr, 0, w );
		WriteBe( ihdr, 4, h );
		ihdr[8] = 8;
		ihdr[9] = 2;
		Chunk( ms, "IHDR", ihdr );
		Chunk( ms, "IDAT", ZlibCompress( raw ) );
		Chunk( ms, "IEND", Array.Empty<byte>() );

		return ms.ToArray();
	}

	static void WriteBe( byte[] b, int offset, int value )
	{
		b[offset] = (byte)(value >> 24);
		b[offset + 1] = (byte)(value >> 16);
		b[offset + 2] = (byte)(value >> 8);
		b[offset + 3] = (byte)value;
	}

	static int ReadBe( byte[] b, int offset ) =>
		(b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

	static void Chunk( Stream s, string type, byte[] data )
	{
		var length = new byte[4];
		WriteBe( length, 0, data.Length );
		s.Write( length );

		var typeBytes = Encoding.ASCII.GetBytes( type );
		s.Write( typeBytes );
		s.Write( data );

		var crc = Crc32( typeBytes, data );
		var crcBytes = new byte[4];
		WriteBe( crcBytes, 0, unchecked((int)crc) );
		s.Write( crcBytes );
	}

	static byte[] ZlibCompress( byte[] data )
	{
		using var ms = new MemoryStream();
		ms.WriteByte( 0x78 );
		ms.WriteByte( 0x01 );

		using ( var deflate = new DeflateStream( ms, CompressionLevel.Optimal, leaveOpen: true ) )
			deflate.Write( data );

		uint a = 1, b = 0;

		foreach ( var x in data )
		{
			a = (a + x) % 65521;
			b = (b + a) % 65521;
		}

		var adler = (b << 16) | a;
		ms.WriteByte( (byte)(adler >> 24) );
		ms.WriteByte( (byte)(adler >> 16) );
		ms.WriteByte( (byte)(adler >> 8) );
		ms.WriteByte( (byte)adler );

		return ms.ToArray();
	}

	static readonly uint[] CrcTable = BuildCrcTable();

	static uint[] BuildCrcTable()
	{
		var table = new uint[256];

		for ( uint n = 0; n < 256; n++ )
		{
			var c = n;
			for ( var k = 0; k < 8; k++ )
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			table[n] = c;
		}

		return table;
	}

	static uint Crc32( byte[] a, byte[] b )
	{
		var c = 0xFFFFFFFFu;
		foreach ( var x in a ) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
		foreach ( var x in b ) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
		return c ^ 0xFFFFFFFFu;
	}

	// --- PNG in -------------------------------------------------------------------------------

	/// <summary>
	/// Read a PNG: 8-bit greyscale, grey+alpha, RGB, RGBA or palette, non-interlaced — which is
	/// every PNG a paint program or a screenshot tool writes. Returns packed RGB and a separate
	/// alpha (255 where the file has none), so a reference cut out on transparency and one drawn
	/// dark on white both read the same way to <see cref="ReferenceMatch"/>.
	/// </summary>
	public static (int Width, int Height, int[] Rgb, byte[] Alpha) LoadPng( string path ) => DecodePng( File.ReadAllBytes( path ) );

	public static (int Width, int Height, int[] Rgb, byte[] Alpha) DecodePng( byte[] file )
	{
		if ( file.Length < 8 || file[0] != 0x89 || file[1] != 0x50 || file[2] != 0x4E || file[3] != 0x47 )
			throw new InvalidDataException( "Not a PNG file." );

		var pos = 8;
		int width = 0, height = 0, depth = 0, colourType = 0, interlace = 0;
		var idat = new MemoryStream();
		byte[] palette = null;
		byte[] paletteAlpha = null;

		while ( pos + 8 <= file.Length )
		{
			var length = ReadBe( file, pos );
			var type = Encoding.ASCII.GetString( file, pos + 4, 4 );
			var data = pos + 8;

			switch ( type )
			{
				case "IHDR":
					width = ReadBe( file, data );
					height = ReadBe( file, data + 4 );
					depth = file[data + 8];
					colourType = file[data + 9];
					interlace = file[data + 12];
					break;
				case "PLTE":
					palette = new byte[length];
					Array.Copy( file, data, palette, 0, length );
					break;
				case "tRNS":
					paletteAlpha = new byte[length];
					Array.Copy( file, data, paletteAlpha, 0, length );
					break;
				case "IDAT":
					idat.Write( file, data, length );
					break;
			}

			if ( type == "IEND" )
				break;

			pos = data + length + 4;
		}

		if ( width <= 0 || height <= 0 )
			throw new InvalidDataException( "PNG has no IHDR." );

		if ( depth != 8 )
			throw new InvalidDataException( $"PNG bit depth {depth} is not supported — save it as 8-bit." );

		if ( interlace != 0 )
			throw new InvalidDataException( "Interlaced PNGs are not supported — save it without interlacing." );

		var channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException( $"PNG colour type {colourType} is not supported." ) };

		idat.Position = 0;
		var stride = width * channels;
		var raw = new byte[(stride + 1) * height];

		using ( var z = new ZLibStream( idat, CompressionMode.Decompress ) )
		{
			var read = 0;
			while ( read < raw.Length )
			{
				var n = z.Read( raw, read, raw.Length - read );
				if ( n <= 0 ) break;
				read += n;
			}
		}

		// Unfilter, in place, one scanline at a time.
		var bpp = channels;
		var prev = new byte[stride];
		var line = new byte[stride];
		var rgb = new int[width * height];
		var alpha = new byte[width * height];

		for ( var y = 0; y < height; y++ )
		{
			var filter = raw[y * (stride + 1)];
			Array.Copy( raw, y * (stride + 1) + 1, line, 0, stride );

			for ( var i = 0; i < stride; i++ )
			{
				var a = i >= bpp ? line[i - bpp] : 0;
				var b = prev[i];
				var c = i >= bpp ? prev[i - bpp] : 0;

				line[i] = filter switch
				{
					1 => (byte)(line[i] + a),
					2 => (byte)(line[i] + b),
					3 => (byte)(line[i] + ((a + b) >> 1)),
					4 => (byte)(line[i] + Paeth( a, b, c )),
					_ => line[i],
				};
			}

			for ( var x = 0; x < width; x++ )
			{
				var o = x * channels;
				int r, g, bl, al = 255;

				switch ( colourType )
				{
					case 0: r = g = bl = line[o]; break;
					case 2: r = line[o]; g = line[o + 1]; bl = line[o + 2]; break;
					case 3:
						var idx = line[o];
						r = palette is not null && idx * 3 + 2 < palette.Length ? palette[idx * 3] : 0;
						g = palette is not null && idx * 3 + 2 < palette.Length ? palette[idx * 3 + 1] : 0;
						bl = palette is not null && idx * 3 + 2 < palette.Length ? palette[idx * 3 + 2] : 0;
						al = paletteAlpha is not null && idx < paletteAlpha.Length ? paletteAlpha[idx] : 255;
						break;
					case 4: r = g = bl = line[o]; al = line[o + 1]; break;
					default: r = line[o]; g = line[o + 1]; bl = line[o + 2]; al = line[o + 3]; break;
				}

				rgb[y * width + x] = Rgb( r, g, bl );
				alpha[y * width + x] = (byte)al;
			}

			(prev, line) = (line, prev);
		}

		return (width, height, rgb, alpha);
	}

	static int Paeth( int a, int b, int c )
	{
		var p = a + b - c;
		var pa = Math.Abs( p - a );
		var pb = Math.Abs( p - b );
		var pc = Math.Abs( p - c );
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}
}
