using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Rasterises meshes to a PNG.
///
/// The SVG previews are fine in a browser but not everything renders SVG, and a PNG can be looked
/// at anywhere. No image library is available here, so this does the whole job: scanline polygon
/// fill into a framebuffer, 2x supersampling, then a hand-rolled PNG encoder.
///
/// Worth the ~200 lines because a test suite proves the numbers and an image proves the shape.
/// They catch different mistakes.
/// </summary>
public static class PngPreview
{
	const int Supersample = 2;

	public sealed class Tile
	{
		public PolyMesh Mesh;
		public string Label;
		public bool Wireframe;

		public Tile( PolyMesh mesh, string label, bool wireframe = false )
		{
			Mesh = mesh;
			Label = label;
			Wireframe = wireframe;
		}
	}

	/// <summary>A grid of previews in one image — a contact sheet.</summary>
	public static void WriteSheet( IReadOnlyList<Tile> tiles, string path, int columns = 4, int tileSize = 300 )
	{
		var rows = (tiles.Count + columns - 1) / columns;
		var w = columns * tileSize;
		var h = rows * tileSize;

		var ss = Supersample;
		var buffer = new int[w * ss * h * ss];
		var background = Rgb( 18, 20, 24 );

		for ( var i = 0; i < buffer.Length; i++ )
			buffer[i] = background;

		for ( var t = 0; t < tiles.Count; t++ )
		{
			var col = t % columns;
			var row = t / columns;

			DrawTile( buffer, w * ss, h * ss,
				col * tileSize * ss, row * tileSize * ss, tileSize * ss, tiles[t] );
		}

		var downsampled = Downsample( buffer, w * ss, h * ss, ss );

		// Labels are drawn after downsampling so the 3x5 bitmap font stays crisp rather than
		// being blurred by the box filter.
		for ( var t = 0; t < tiles.Count; t++ )
		{
			var col = t % columns;
			var row = t / columns;
			var tile = tiles[t];

			var caption = $"{tile.Label}";
			var stats = $"{tile.Mesh.VertexCount}v {tile.Mesh.FaceCount}f";

			DrawText( downsampled, w, h, col * tileSize + 10, row * tileSize + tileSize - 011 - 12, caption, Rgb( 150, 165, 182 ) );
			DrawText( downsampled, w, h, col * tileSize + 10, row * tileSize + tileSize - 011, stats, Rgb( 96, 108, 122 ) );
		}

		WritePng( path, downsampled, w, h );
	}

	static void DrawTile( int[] buffer, int bufW, int bufH, int ox, int oy, int size, Tile tile )
	{
		var mesh = tile.Mesh;

		if ( mesh.VertexCount == 0 )
			return;

		var yaw = 35f * MathF.PI / 180f;
		var pitch = 24f * MathF.PI / 180f;
		var view = Xform.Rotate( new Vec3( 1, 0, 0 ), -pitch ) * Xform.Rotate( new Vec3( 0, 0, 1 ), yaw );

		// Same convention as SvgPreview: positions go into VIEW space, the screen reads x and z,
		// so view-space Y is depth and the viewer sits at -Y. camera is a constant view-space axis.
		var camera = new Vec3( 0, -1, 0 );
		var light = new Vec3( -0.4f, -0.75f, 0.53f ).Normal;

		var projected = mesh.Positions.Select( p => view.TransformPoint( p ) ).ToList();

		var minX = projected.Min( p => p.x );
		var maxX = projected.Max( p => p.x );
		var minZ = projected.Min( p => p.z );
		var maxZ = projected.Max( p => p.z );

		var span = MathF.Max( maxX - minX, maxZ - minZ );

		if ( span < 1e-6f )
			span = 1f;

		var margin = size * 0.16f;
		var scale = (size - margin * 2f) / span;
		var cx = (minX + maxX) * 0.5f;
		var cz = (minZ + maxZ) * 0.5f;

		Vec2 Screen( Vec3 p ) => new(
			ox + size * 0.5f + (p.x - cx) * scale,
			oy + size * 0.46f - (p.z - cz) * scale );

		var order = Enumerable.Range( 0, mesh.FaceCount )
			.Select( fi => (Index: fi, Depth: mesh.Faces[fi].Indices.Average( i => Vec3.Dot( projected[i], camera ) )) )
			.OrderBy( x => x.Depth )
			.ToList();

		foreach ( var (fi, _) in order )
		{
			var face = mesh.Faces[fi];
			var viewNormal = view.TransformDirection( mesh.FaceNormal( face ) );

			// Backface cull. An inside-out solid renders as a hole, which makes this a visual
			// double-check on the winding tests.
			if ( Vec3.Dot( viewNormal, camera ) <= 0.01f )
				continue;

			var lambert = MathF.Max( 0f, Vec3.Dot( mesh.FaceNormal( face ), light ) );
			var shade = 0.22f + 0.78f * lambert * lambert;

			// The unpainted surface, and the thing paint composes OVER. Kept as the base rather
			// than as an else-branch so a half-opaque dab reads as a tint of this rather than as a
			// colour that arrived from nowhere - which is what the engine's standard material does
			// with vertex colours, and therefore what this preview has to do to be worth trusting.
			float br = shade * 118 + 26;
			float bg = shade * 168 + 30;
			float bb = shade * 208 + 38;

			// VERTEX COLOURS, AVERAGED ACROSS THE FACE. A PolyMesh face is flat-shaded here, so
			// there is no per-pixel interpolation to carry a gradient; the average is the honest
			// summary of what the four corners say. It means a dab smaller than one face shows up
			// as a weak tint of the whole face rather than as a spot, which is the truth about
			// vertex paint on a coarse mesh and exactly the thing the Known Issue warns about.
			if ( mesh.HasVertexColors )
			{
				float pr = 0, pg = 0, pb = 0, pa = 0;

				foreach ( var i in face.Indices )
				{
					var c = mesh.VertexColors[i];
					pr += c.x; pg += c.y; pb += c.z; pa += c.w;
				}

				var n = face.Indices.Length;
				pa /= n;

				if ( pa > 0.001f )
				{
					// Source-over, with the paint lit by the same lambert the base gets. Painting
					// a face does not make it stop facing away from the light.
					br += (pr / n * 255f * shade - br) * pa;
					bg += (pg / n * 255f * shade - bg) * pa;
					bb += (pb / n * 255f * shade - bb) * pa;
				}
			}

			var colour = Rgb( (int)br, (int)bg, (int)bb );

			var poly = face.Indices.Select( i => Screen( projected[i] ) ).ToArray();

			if ( !tile.Wireframe )
				FillPolygon( buffer, bufW, bufH, poly, colour );

			var edge = tile.Wireframe ? Rgb( 126, 196, 235 ) : Rgb( 12, 14, 17 );

			for ( var i = 0; i < poly.Length; i++ )
				DrawLine( buffer, bufW, bufH, poly[i], poly[(i + 1) % poly.Length], edge );
		}
	}

	static int Rgb( int r, int g, int b ) =>
		(Math.Clamp( r, 0, 255 ) << 16) | (Math.Clamp( g, 0, 255 ) << 8) | Math.Clamp( b, 0, 255 );

	/// <summary>Scanline fill with the even-odd rule, which is correct for the convex and simple
	/// faces the kernel produces.</summary>
	static void FillPolygon( int[] buffer, int w, int h, Vec2[] poly, int colour )
	{
		var minY = Math.Max( 0, (int)MathF.Floor( poly.Min( p => p.y ) ) );
		var maxY = Math.Min( h - 1, (int)MathF.Ceiling( poly.Max( p => p.y ) ) );

		var crossings = new List<float>( 8 );

		for ( var y = minY; y <= maxY; y++ )
		{
			crossings.Clear();
			var scan = y + 0.5f;

			for ( var i = 0; i < poly.Length; i++ )
			{
				var a = poly[i];
				var b = poly[(i + 1) % poly.Length];

				if ( a.y > scan == b.y > scan )
					continue;

				crossings.Add( a.x + (scan - a.y) / (b.y - a.y) * (b.x - a.x) );
			}

			if ( crossings.Count < 2 )
				continue;

			crossings.Sort();

			for ( var c = 0; c + 1 < crossings.Count; c += 2 )
			{
				var x0 = Math.Max( 0, (int)MathF.Ceiling( crossings[c] - 0.5f ) );
				var x1 = Math.Min( w - 1, (int)MathF.Floor( crossings[c + 1] - 0.5f ) );

				for ( var x = x0; x <= x1; x++ )
					buffer[y * w + x] = colour;
			}
		}
	}

	static void DrawLine( int[] buffer, int w, int h, Vec2 a, Vec2 b, int colour )
	{
		var steps = (int)MathF.Max( MathF.Abs( b.x - a.x ), MathF.Abs( b.y - a.y ) ) + 1;

		for ( var i = 0; i <= steps; i++ )
		{
			var t = i / (float)steps;
			var x = (int)MathF.Round( a.x + (b.x - a.x) * t );
			var y = (int)MathF.Round( a.y + (b.y - a.y) * t );

			if ( x >= 0 && x < w && y >= 0 && y < h )
				buffer[y * w + x] = colour;
		}
	}

	static int[] Downsample( int[] src, int w, int h, int factor )
	{
		var dw = w / factor;
		var dh = h / factor;
		var dst = new int[dw * dh];

		for ( var y = 0; y < dh; y++ )
		{
			for ( var x = 0; x < dw; x++ )
			{
				int r = 0, g = 0, b = 0;

				for ( var sy = 0; sy < factor; sy++ )
				{
					for ( var sx = 0; sx < factor; sx++ )
					{
						var p = src[(y * factor + sy) * w + x * factor + sx];
						r += (p >> 16) & 0xFF;
						g += (p >> 8) & 0xFF;
						b += p & 0xFF;
					}
				}

				var n = factor * factor;
				dst[y * dw + x] = Rgb( r / n, g / n, b / n );
			}
		}

		return dst;
	}

	// --- a 3x5 bitmap font, enough for labels -------------------------------------------------

	static readonly Dictionary<char, string[]> Glyphs = BuildFont();

	static Dictionary<char, string[]> BuildFont()
	{
		var f = new Dictionary<char, string[]>
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
			['('] = new[] { " ##", "#  ", "#  ", "#  ", " ##" },
			[')'] = new[] { "## ", "  #", "  #", "  #", "## " },
			['.'] = new[] { "   ", "   ", "   ", "   ", " # " },
			[' '] = new[] { "   ", "   ", "   ", "   ", "   " },
		};

		return f;
	}

	static void DrawText( int[] buffer, int w, int h, int x, int y, string text, int colour, int scale = 2 )
	{
		var cursor = x;

		foreach ( var raw in text.ToLowerInvariant() )
		{
			if ( !Glyphs.TryGetValue( raw, out var glyph ) )
				glyph = Glyphs[' '];

			for ( var gy = 0; gy < 5; gy++ )
			{
				for ( var gx = 0; gx < 3; gx++ )
				{
					if ( glyph[gy][gx] != '#' )
						continue;

					for ( var sy = 0; sy < scale; sy++ )
					{
						for ( var sx = 0; sx < scale; sx++ )
						{
							var px = cursor + gx * scale + sx;
							var py = y + gy * scale + sy;

							if ( px >= 0 && px < w && py >= 0 && py < h )
								buffer[py * w + px] = colour;
						}
					}
				}
			}

			cursor += 4 * scale;
		}
	}

	// --- PNG encoding -------------------------------------------------------------------------

	internal static void WritePng( string path, int[] pixels, int w, int h )
	{
		// Raw scanlines, each prefixed with filter type 0 (None). Simplest valid encoding.
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

		using var fs = File.Create( path );

		fs.Write( new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } );

		var ihdr = new byte[13];
		WriteBe( ihdr, 0, w );
		WriteBe( ihdr, 4, h );
		ihdr[8] = 8;  // bit depth
		ihdr[9] = 2;  // colour type 2 = truecolour RGB
		Chunk( fs, "IHDR", ihdr );

		Chunk( fs, "IDAT", ZlibCompress( raw ) );
		Chunk( fs, "IEND", Array.Empty<byte>() );
	}

	static void WriteBe( byte[] b, int offset, int value )
	{
		b[offset] = (byte)(value >> 24);
		b[offset + 1] = (byte)(value >> 16);
		b[offset + 2] = (byte)(value >> 8);
		b[offset + 3] = (byte)value;
	}

	static void Chunk( Stream s, string type, byte[] data )
	{
		var length = new byte[4];
		WriteBe( length, 0, data.Length );
		s.Write( length );

		var typeBytes = System.Text.Encoding.ASCII.GetBytes( type );
		s.Write( typeBytes );
		s.Write( data );

		var crc = Crc32( typeBytes, data );
		var crcBytes = new byte[4];
		WriteBe( crcBytes, 0, unchecked((int)crc) );
		s.Write( crcBytes );
	}

	/// <summary>DeflateStream emits a raw deflate stream; zlib wants a 2-byte header in front and
	/// an Adler-32 of the UNCOMPRESSED data on the end.</summary>
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

		foreach ( var x in a )
			c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);

		foreach ( var x in b )
			c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);

		return c ^ 0xFFFFFFFFu;
	}
}
