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
		Section( "paint material: the canvas bakes opaque over the base colour" );
		TestOpaqueBake();

		Section( "paint bind: the atlas goes on the slots the faces actually wear" );
		TestBindUsesThePaintedSlots();
		TestMergeDropIsNamed();

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

		Check( "an unpainted texel falls back to the default base, not white",
			opaque[4] == PaintMaterial.DefaultBaseR
			&& opaque[5] == PaintMaterial.DefaultBaseG
			&& opaque[6] == PaintMaterial.DefaultBaseB
			&& opaque[7] == 255,
			$"({opaque[4]},{opaque[5]},{opaque[6]},{opaque[7]})" );

		var overRed = PaintMaterial.OpaqueRgba( canvas, 200, 10, 10 );

		Check( "a caller can pass the bound material's colour as the base",
			overRed[4] == 200 && overRed[5] == 10 && overRed[6] == 10 && overRed[7] == 255 );
	}

	static void TestBindUsesThePaintedSlots()
	{
		var mesh = Primitives.Box( 2f, 2f, 2f );

		foreach ( var face in mesh.Faces )
			face.Material = 2;

		var slots = PaintBind.SlotsToBind( mesh );

		Check( "faces on slot 2 bind slot 2, not slot 0",
			slots.Count == 1 && slots[0] == 2,
			string.Join( ",", slots ) );

		Check( "slot 0 is not invented", !slots.Contains( 0 ) );
	}

	static void TestMergeDropIsNamed()
	{
		var a = Primitives.Box( 2f, 2f, 2f );
		var b = Primitives.Box( 2f, 2f, 2f );
		a.Paint = new PaintCanvas( 8, 8 );
		b.Paint = new PaintCanvas( 8, 8 );

		Check( "two painted bodies merging is a drop, said rather than silent",
			PaintBind.MergeDropsPaint( a, b ) );

		var plain = Primitives.Box( 1f, 1f, 1f );

		Check( "a painted body into an unpainted rest is not a drop",
			!PaintBind.MergeDropsPaint( plain, a ) );
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
