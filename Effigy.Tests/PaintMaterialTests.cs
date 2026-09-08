using System;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The authoring half of the paint path: what the painted canvas bakes to, and the .vmat text that
/// compiles it. A headless suite cannot compile an asset, but it can say whether the file the editor
/// will write is structurally sound and names the right image — the same bargain VmdlMaterialsTests
/// makes for the remap node.
/// </summary>
public static class PaintMaterialTests
{
	public static void Run()
	{
		Section( "paint material: the canvas bakes opaque over white" );
		TestOpaqueBake();

		Section( "paint material: the vmat names its image" );
		TestVmatNamesTheImage();
	}

	static void TestOpaqueBake()
	{
		var canvas = new PaintCanvas( 2, 1 );

		// One texel painted red, one left transparent.
		canvas.Blend( 0, 0, 255, 0, 0, 1f );

		var opaque = PaintMaterial.OpaqueRgba( canvas );

		Check( "the bake is the right size", opaque.Length == 2 * 1 * 4, $"{opaque.Length}" );

		Check( "a painted texel stays its colour and goes opaque",
			opaque[0] == 255 && opaque[1] == 0 && opaque[2] == 0 && opaque[3] == 255,
			$"({opaque[0]},{opaque[1]},{opaque[2]},{opaque[3]})" );

		Check( "an unpainted texel falls back to opaque white",
			opaque[4] == 255 && opaque[5] == 255 && opaque[6] == 255 && opaque[7] == 255,
			$"({opaque[4]},{opaque[5]},{opaque[6]},{opaque[7]})" );
	}

	static void TestVmatNamesTheImage()
	{
		var vmat = PaintMaterial.VmatSource( "models/effigy/box_paint.png" );

		Check( "the vmat binds the PNG as its colour",
			vmat.Contains( "TextureColor \"models/effigy/box_paint.png\"" ) );

		Check( "braces balance", CountOf( vmat, "{" ) == CountOf( vmat, "}" ),
			$"{CountOf( vmat, "{" )} open, {CountOf( vmat, "}" )} close" );

		Check( "it is the Layer0 form the engine's own templates ship",
			vmat.Contains( "Layer0" ) && vmat.Contains( "shader \"complex.vfx\"" ) );

		// A .vtex is not a valid texture input — the engine's compiler says so in its own words.
		// The material must name the image, not a texture wrapper.
		Check( "and it never names a .vtex",
			!vmat.Contains( ".vtex" ) );
	}

	static int CountOf( string text, string needle )
	{
		var count = 0;
		var at = 0;

		while ( (at = text.IndexOf( needle, at, StringComparison.Ordinal )) >= 0 )
		{
			count++;
			at += needle.Length;
		}

		return count;
	}
}
