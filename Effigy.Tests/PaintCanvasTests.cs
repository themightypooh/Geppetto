using System;
using System.Text;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The paint canvas and the RGBA path it needs, taken apart the way the bake was.
///
/// The canvas is the piece §4 of docs/dev/PAINTING.md replays onto, and the whole reason it exists
/// rather than a raw `byte[]` is the dirty rect: the old paint project re-uploaded a 1024^2 texture
/// on every dab. The blending checks here pin down the source-over math that a stroke depends on, and
/// the PNG checks pin down that teaching the writer alpha did not move the RGB path one byte.
/// </summary>
public static class PaintCanvasTests
{
	public static void Run()
	{
		Section( "paint canvas: source-over blend and the dirty rect" );
		TestOpaqueRedOntoEmpty();
		TestHalfRedOverOpaqueWhite();
		TestHalfTwiceIsNotFull();
		TestNoOpsLeaveEverythingAlone();
		TestDirtyRectIsTheUnionAndClears();
		TestClearMarksTheWholeCanvas();

		Section( "paint canvas: RGBA PNG without moving RGB" );
		TestRgbaPngRoundTrips();
		TestRgbOutputIsByteIdentical();
	}

	static void TestOpaqueRedOntoEmpty()
	{
		var canvas = new PaintCanvas( 1, 1 );
		canvas.Blend( 0, 0, 255, 0, 0, 1f );

		Check( "opaque red onto empty is exactly opaque red",
			canvas.Rgba[0] == 255 && canvas.Rgba[1] == 0 && canvas.Rgba[2] == 0 && canvas.Rgba[3] == 255,
			$"({canvas.Rgba[0]},{canvas.Rgba[1]},{canvas.Rgba[2]},{canvas.Rgba[3]})" );
	}

	static void TestHalfRedOverOpaqueWhite()
	{
		var canvas = new PaintCanvas( 1, 1 );

		// Opaque white, set directly so the assertion below is about the blend alone, not about how
		// the background got there.
		canvas.Rgba[0] = canvas.Rgba[1] = canvas.Rgba[2] = canvas.Rgba[3] = 255;

		canvas.Blend( 0, 0, 255, 0, 0, 0.5f );

		// Hand-computed, deliberately not re-derived the way the code computes it: half coverage of
		// red over opaque white leaves red at 1, green and blue at half, and the surface still opaque.
		Check( "half-weight red over opaque white is (255, 128, 128, 255)",
			canvas.Rgba[0] == 255 && canvas.Rgba[1] == 128 && canvas.Rgba[2] == 128 && canvas.Rgba[3] == 255,
			$"({canvas.Rgba[0]},{canvas.Rgba[1]},{canvas.Rgba[2]},{canvas.Rgba[3]})" );
	}

	static void TestHalfTwiceIsNotFull()
	{
		var twice = new PaintCanvas( 1, 1 );
		twice.Blend( 0, 0, 255, 0, 0, 0.5f );
		var firstAlpha = twice.Rgba[3];
		twice.Blend( 0, 0, 255, 0, 0, 0.5f );

		var once = new PaintCanvas( 1, 1 );
		once.Blend( 0, 0, 255, 0, 0, 1f );

		// Source-over is not additive: a second half-weight dab raises the alpha again rather than
		// saturating at the first, which is exactly why two half dabs must differ from one full dab.
		Check( "the first half-weight dab painted with some alpha", firstAlpha > 0, $"{firstAlpha}" );
		Check( "and the second raised it further", twice.Rgba[3] > firstAlpha,
			$"{firstAlpha} -> {twice.Rgba[3]}" );
		Check( "two half-weight dabs are not one full-weight dab", !Equal( twice.Rgba, once.Rgba ),
			$"({twice.Rgba[0]},{twice.Rgba[1]},{twice.Rgba[2]},{twice.Rgba[3]}) vs "
			+ $"({once.Rgba[0]},{once.Rgba[1]},{once.Rgba[2]},{once.Rgba[3]})" );
	}

	static void TestNoOpsLeaveEverythingAlone()
	{
		var canvas = new PaintCanvas( 4, 4 );
		canvas.Blend( 1, 1, 255, 0, 0, 1f );
		var before = (byte[])canvas.Rgba.Clone();

		canvas.Blend( 1, 1, 0, 255, 0, 0f );    // weight 0
		canvas.Blend( 1, 1, 0, 255, 0, -1f );   // negative weight
		canvas.Blend( -1, 1, 0, 255, 0, 1f );   // x below the canvas
		canvas.Blend( 1, -1, 0, 255, 0, 1f );   // y below the canvas
		canvas.Blend( 4, 0, 0, 255, 0, 1f );    // x at the right edge
		canvas.Blend( 0, 4, 0, 255, 0, 1f );    // y at the bottom edge

		Check( "weight 0, negative weight and out-of-bounds leave Rgba untouched",
			Equal( canvas.Rgba, before ) );
		Check( "and none of them moved the dirty rect",
			canvas.HasDirty && canvas.MinX == 1 && canvas.MinY == 1 && canvas.MaxX == 1 && canvas.MaxY == 1,
			$"[{canvas.MinX},{canvas.MinY}]..[{canvas.MaxX},{canvas.MaxY}]" );
	}

	static void TestDirtyRectIsTheUnionAndClears()
	{
		var canvas = new PaintCanvas( 16, 16 );

		Check( "a fresh canvas reports no dirty rect", !canvas.HasDirty );

		canvas.Blend( 2, 3, 255, 0, 0, 1f );
		canvas.Blend( 10, 5, 0, 0, 255, 1f );

		Check( "two scattered writes produce their union",
			canvas.HasDirty && canvas.MinX == 2 && canvas.MinY == 3 && canvas.MaxX == 10 && canvas.MaxY == 5,
			$"[{canvas.MinX},{canvas.MinY}]..[{canvas.MaxX},{canvas.MaxY}]" );

		canvas.ClearDirty();

		Check( "and ClearDirty empties it", !canvas.HasDirty );
	}

	static void TestClearMarksTheWholeCanvas()
	{
		var canvas = new PaintCanvas( 8, 6 );
		canvas.Blend( 1, 1, 255, 0, 0, 1f );
		canvas.Clear();

		var allZero = true;

		for ( var i = 0; i < canvas.Rgba.Length; i++ )
			allZero &= canvas.Rgba[i] == 0;

		Check( "Clear returns the canvas to fully transparent", allZero );
		Check( "and marks the whole canvas dirty",
			canvas.HasDirty && canvas.MinX == 0 && canvas.MinY == 0 && canvas.MaxX == 7 && canvas.MaxY == 5,
			$"[{canvas.MinX},{canvas.MinY}]..[{canvas.MaxX},{canvas.MaxY}]" );
	}

	static void TestRgbaPngRoundTrips()
	{
		var canvas = new PaintCanvas( 5, 3 );
		canvas.Blend( 1, 1, 255, 0, 0, 1f );
		canvas.Blend( 3, 2, 0, 128, 255, 0.5f );

		var png = PngWriter.ToBytesRgba( canvas.Rgba, canvas.Width, canvas.Height );

		var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
		var ok = png.Length > signature.Length;

		for ( var i = 0; ok && i < signature.Length; i++ )
			ok = png[i] == signature[i];

		Check( "an RGBA write still starts with the PNG signature", ok );

		var ihdr = Chunk( png, "IHDR" );

		Check( "its IHDR names the size it was given",
			ihdr is { Length: 13 } && ReadBe( ihdr, 0 ) == 5 && ReadBe( ihdr, 4 ) == 3,
			ihdr is { Length: 13 } ? $"{ReadBe( ihdr, 0 )}x{ReadBe( ihdr, 4 )}" : "no IHDR" );
		Check( "and declares colour type 6, truecolour with alpha", ihdr is { Length: 13 } && ihdr[9] == 6,
			ihdr is { Length: 13 } ? $"colour type {ihdr[9]}" : "no IHDR" );
	}

	static void TestRgbOutputIsByteIdentical()
	{
		// The same deterministic bake the reference bytes were captured from, before the writer grew
		// alpha. If teaching it RGBA changed the RGB path by so much as a byte, this fails.
		var cage = Primitives.Plane( 2, 2, 4, 4 );
		var sculpted = Primitives.Plane( 2, 2, 8, 8 );

		for ( var i = 0; i < sculpted.VertexCount; i++ )
		{
			var p = sculpted.Positions[i];
			var r = MathF.Sqrt( p.x * p.x + p.y * p.y );

			if ( r >= 0.6f )
				continue;

			var t = 1f - r / 0.6f;
			sculpted.Positions[i] = new Vec3( p.x, p.y, p.z + 0.2f * t * t * (3f - 2f * t) );
		}

		var map = NormalBake.Bake( cage, sculpted, 32 );
		var now = PngWriter.ToBytes( map.Rgb, map.Width, map.Height );
		var before = Convert.FromBase64String( ReferenceRgbPng );

		Check( "an RGB bake is byte-identical to before the RGBA change",
			now.Length == before.Length && Equal( now, before ),
			$"{now.Length} bytes vs {before.Length} reference bytes" );
	}

	/// <summary>The 32x32 RGB bake captured before the writer learned alpha.</summary>
	const string ReferenceRgbPng =
		"iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAErUlEQVR4Aa3We3OaTBTH8b7RNKmJSihqUFGEFcEFUVS8lXpJbNK8TM6esw/mXqtt0nlmvuN//j6g68gn15UH63QO576nNzufPrT+/wBH151d7kv/Bvxheh/4A3MM2Nt1nOfYc7Z03+Qx6bWfOnY3r8DedLv9FLOkbUr2kNOQrvFUtyl9U/LWrm4mOU8dBn5fZ2yX3ZJWg1p1surEquRckVvZ1dUpqFGvTj2DdowtOdt1GDi4btvSMmXLILNKzSuyysQ0dFR0Vex+Rb9EYYX6VxRmTEP6lvTtI8De5/6ybpqyaVCjSkaFDA2bKloKsoLwCoIrGKjY13BYxqhKfYNCUwa29LObaMuucxx4uXzTkkaT6nWq6aSXUFdFTRFGQbBz4Z0LnhdBUYwuxUTDuILjKkVNGrRkaMseewfQsqXRomqD9BpVKqipQitCJQ/GObAz8M6A5yC6ELMiLFSYl8RUx7FBI5OiFv0dsJhsWFQ1qWJQSUdVE4oCSj6t5FLjNGUnqXeShqcQ5yC5SFfFNNFgcYWzOk6aNGxRdhM8O0sHAacjmSNNW9ZbdNUkzUBVF0UN8kpaKKSlC6h/Afsz8DOIcjDLw1JJr9V0VYbvNbFo4tSkkUX9tvQ7bwD3d4DJukVli9QmKjWRr0Cukl6UQFVBV4StIL/ESMNZGTdl2OpwbYiVJRIL5zbFbYo6T8DOePwlv6y/AFVGGkPFwoIhzg04NdIvdVB0oeto68RrFBm0MHBrwJ0JWwYbJpYMv7Vx2nkDuIcA25ENR145pDpYYOKciTMGJyz9bINio26T/XDYR4y+M7xlcO+ktw7cOGLtiKTzBnDfAtkPzZXOQ7YrDVdWXFJdLHji3BNnHpx46RmHS441ToxL35exT0sff/pwz9NbDjdcrLlIPJx4NPCk770D+NpFhWOeixyH0xByEWgRGhGxEfkxxTEuY/FjCHdRehPBOhLLABccx5xCLvlfAb1LJZ/UAIuBuIhELob8DMozNBbIvqO/xHgpkgSu57CdppspLEci6eMsoGHwCrh7QOcZaHZlLaBKSFofL0eiOIOLJFWWaXkDxhbYLfg/YfgD5tewXqebZbpaQBKLRYSTPg17FPp/BJgnTV/We3TVp/IQtQmqCyiuUvU61bdg3oFzn/L7NLpLp9t0uUmXS0gWYh7jNKJxSIOAAi4979c/nBcgq+3JFpdGj2ohVSOqxFiaCy2B8gqMa8G24NwCv4XoBqYbWKxgkYjpFOMRjQcU9ajny273HUB2E42eNPpUHZM+xasF1r4LayXYRjg3gt+IaC1GSxEnIp7jKKbhkKI+hYHk/B0A60qbS8uXZiibERljqs+wuUArQbZEZy34WgRL7CcYLTCa0mBM/YjCcHd8H4G9P/lfgN03kRlc2r60A9kaSHNEzQmZU7Ln1P6GnQS9BPkCgxn1JxSOqRdR8GZ97/KPArsyI5StiFpDskbUjqkz3eVNiI8pGFJvSMGA/N5u+tj6YeCxdmb0dmVMuy870VPeQPJwl5+9Brvdxw6uHwBepe5zXHb817ys7Cw+lh35h449f/72XHQw7zV3r+O7HwGemY9Ofwx4/+K/AP+8nr3xP6qD9ZjYHaC6AAAAAElFTkSuQmCC";

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

	static int ReadBe( byte[] b, int o ) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

	static bool Equal( byte[] a, byte[] b )
	{
		if ( a.Length != b.Length )
			return false;

		for ( var i = 0; i < a.Length; i++ )
		{
			if ( a[i] != b[i] )
				return false;
		}

		return true;
	}
}
