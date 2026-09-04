using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The PNG encoder, taken apart again.
///
/// A written file that no reader accepts is the failure worth guarding, and "it did not throw" says
/// nothing about that. So this decodes what it wrote: the signature, the IHDR fields, every chunk's
/// CRC, and the IDAT inflated back to the scanlines that went in. If any of those is wrong the file
/// opens in nothing, and the symptom is a corrupt-image icon rather than a stack trace.
/// </summary>
public static class PngTests
{
	public static void Run()
	{
		Section( "png: what is written is a file a reader will accept" );
		TestHeaderIsAPng();
		TestPixelsSurviveTheRoundTrip();
		TestEveryChunkCrcIsRight();
		TestABakedMapCanBeFlippedOnTheWayOut();
		TestBadInputIsRefused();
	}

	static byte[] Gradient( int w, int h )
	{
		var rgb = new byte[w * h * 3];

		for ( var y = 0; y < h; y++ )
		{
			for ( var x = 0; x < w; x++ )
			{
				var i = (y * w + x) * 3;
				rgb[i] = (byte)(x * 7 + y);
				rgb[i + 1] = (byte)(y * 3);
				rgb[i + 2] = (byte)(255 - x);
			}
		}

		return rgb;
	}

	static void TestHeaderIsAPng()
	{
		var bytes = PngWriter.ToBytes( Gradient( 9, 5 ), 9, 5 );
		var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
		var ok = bytes.Length > 8;

		for ( var i = 0; ok && i < signature.Length; i++ )
			ok = bytes[i] == signature[i];

		Check( "it starts with the PNG signature", ok );

		var ihdr = Chunk( bytes, "IHDR" );

		Check( "IHDR is thirteen bytes", ihdr is { Length: 13 }, $"{ihdr?.Length}" );
		Check( "and names the size it was given", ReadBe( ihdr, 0 ) == 9 && ReadBe( ihdr, 4 ) == 5,
			$"{ReadBe( ihdr, 0 )}x{ReadBe( ihdr, 4 )}" );
		Check( "8-bit truecolour, no interlace",
			ihdr[8] == 8 && ihdr[9] == 2 && ihdr[10] == 0 && ihdr[11] == 0 && ihdr[12] == 0 );
		Check( "and it ends with IEND", Chunk( bytes, "IEND" ) is { Length: 0 } );
	}

	static void TestPixelsSurviveTheRoundTrip()
	{
		// The check that matters: inflate the IDAT and compare against what went in, scanline filter
		// bytes and all. A file that passes every header check and holds the wrong pixels is still a
		// broken bake.
		const int w = 13;
		const int h = 7;
		var rgb = Gradient( w, h );
		var bytes = PngWriter.ToBytes( rgb, w, h );

		var idat = Chunk( bytes, "IDAT" );
		var raw = Inflate( idat );

		Check( "the IDAT inflates to one filter byte per scanline plus the pixels",
			raw.Length == h * (w * 3 + 1), $"{raw.Length} bytes, expected {h * (w * 3 + 1)}" );

		var same = true;
		var o = 0;

		for ( var y = 0; y < h && same; y++ )
		{
			same &= raw[o++] == 0;

			for ( var x = 0; x < w * 3; x++ )
				same &= raw[o++] == rgb[y * w * 3 + x];
		}

		Check( "and every pixel comes back exactly as it went in", same );
	}

	static void TestEveryChunkCrcIsRight()
	{
		// A wrong CRC is the classic hand-rolled-PNG bug: most viewers refuse the file outright and
		// the ones that do not are the ones you happen to test with.
		var bytes = PngWriter.ToBytes( Gradient( 6, 6 ), 6, 6 );
		var offset = 8;
		var chunks = 0;
		var good = true;

		while ( offset + 12 <= bytes.Length )
		{
			var length = ReadBe( bytes, offset );
			var type = Encoding.ASCII.GetString( bytes, offset + 4, 4 );
			var stated = unchecked((uint)ReadBe( bytes, offset + 8 + length ));

			var actual = Crc32( bytes, offset + 4, 4 + length );
			good &= stated == actual;
			chunks++;

			offset += 12 + length;

			if ( type == "IEND" )
				break;
		}

		Check( $"all {chunks} chunks carry a correct CRC", good && chunks == 3, $"{chunks} chunks" );
		Check( "and nothing is left over after IEND", offset == bytes.Length,
			$"{bytes.Length - offset} trailing bytes" );
	}

	static void TestABakedMapCanBeFlippedOnTheWayOut()
	{
		// Row order is a convention, not a fact, and getting it wrong lights a model exactly as
		// wrongly as an inverted green channel. Both directions have to be available and the switch
		// has to do only that.
		var map = new BakedMap( 4, 3 );

		for ( var i = 0; i < map.Width * map.Height; i++ )
			map.Rgb[i * 3] = (byte)(i / map.Width);   // red = row number

		var dir = Path.Combine( Path.GetTempPath(), $"effigy-png-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( dir );

		try
		{
			var upright = Path.Combine( dir, "upright.png" );
			var flipped = Path.Combine( dir, "flipped.png" );

			PngWriter.WriteFile( upright, map );
			PngWriter.WriteFile( flipped, map, flipVertically: true );

			var a = Rows( File.ReadAllBytes( upright ), 4, 3 );
			var b = Rows( File.ReadAllBytes( flipped ), 4, 3 );

			Check( "written as-is, row 0 is first", a[0] == 0 && a[2] == 2, $"{a[0]},{a[1]},{a[2]}" );
			Check( "flipped, row 0 is last", b[0] == 2 && b[2] == 0, $"{b[0]},{b[1]},{b[2]}" );
			Check( "and the file is otherwise the same size", new FileInfo( upright ).Length > 0
				&& File.ReadAllBytes( upright ).Length == File.ReadAllBytes( flipped ).Length );
		}
		finally
		{
			Directory.Delete( dir, recursive: true );
		}
	}

	static void TestBadInputIsRefused()
	{
		Check( "a buffer too small for the stated size is refused",
			Throws( () => PngWriter.ToBytes( new byte[10], 8, 8 ) ) );
		Check( "and a zero-sized image is refused",
			Throws( () => PngWriter.ToBytes( new byte[3], 0, 1 ) ) );
	}

	/// <summary>The red channel of each row, read back out of a written file.</summary>
	static int[] Rows( byte[] png, int w, int h )
	{
		var raw = Inflate( Chunk( png, "IDAT" ) );
		var rows = new int[h];

		for ( var y = 0; y < h; y++ )
			rows[y] = raw[y * (w * 3 + 1) + 1];

		return rows;
	}

	static byte[] Chunk( byte[] png, string want )
	{
		var offset = 8;

		while ( offset + 12 <= png.Length )
		{
			var length = ReadBe( png, offset );
			var type = Encoding.ASCII.GetString( png, offset + 4, 4 );

			if ( type == want )
			{
				var data = new byte[length];
				Array.Copy( png, offset + 8, data, 0, length );
				return data;
			}

			offset += 12 + length;
		}

		return null;
	}

	/// <summary>Strip the two-byte zlib header and the Adler-32, and inflate what is between.</summary>
	static byte[] Inflate( byte[] zlib )
	{
		using var input = new MemoryStream( zlib, 2, zlib.Length - 6 );
		using var deflate = new DeflateStream( input, CompressionMode.Decompress );
		using var output = new MemoryStream();

		deflate.CopyTo( output );
		return output.ToArray();
	}

	static int ReadBe( byte[] b, int o ) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

	static uint Crc32( byte[] data, int offset, int length )
	{
		var c = 0xFFFFFFFFu;

		for ( var i = 0; i < length; i++ )
		{
			var x = data[offset + i];
			c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
		}

		return c ^ 0xFFFFFFFFu;
	}

	static readonly uint[] Table = BuildTable();

	static uint[] BuildTable()
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

	static bool Throws( Action action )
	{
		try
		{
			action();
			return false;
		}
		catch
		{
			return true;
		}
	}
}
