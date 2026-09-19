using System;
using System.Linq;

namespace Effigy.Tests;

/// <summary>The fabric tiles: every preset makes a full set, the normals point out, plaid shows
/// its second colour, and the same settings make the same pixels every time.</summary>
public static class FabricMaterialTests
{
	public static void Run()
	{
		Report.Section( "fabric: every preset writes a usable tile" );

		var blue = new Vec3( 0.2f, 0.3f, 0.6f );
		var cream = new Vec3( 0.9f, 0.88f, 0.8f );
		var n = FabricMaterial.Size * FabricMaterial.Size;

		foreach ( var preset in FabricMaterial.Presets )
		{
			var (color, normal, rough) = FabricMaterial.Maps( preset, blue, cream, 6f, 0.2f );

			var full = color.Length == n * 4 && normal.Length == n * 4 && rough.Length == n * 4;
			var outward = Enumerable.Range( 0, n ).All( i => normal[i * 4 + 2] > 100 );
			var unit = Enumerable.Range( 0, n ).Take( 4096 ).All( i =>
			{
				float x = normal[i * 4] / 127.5f - 1f, y = normal[i * 4 + 1] / 127.5f - 1f, z = normal[i * 4 + 2] / 127.5f - 1f;
				return MathF.Abs( x * x + y * y + z * z - 1f ) < 0.05f;
			} );
			var textured = Enumerable.Range( 0, n ).Select( i => (int)color[i * 4 + 2] ).Distinct().Count() > 4;

			Report.Check( $"{preset}: full maps, outward unit normals, some texture", full && outward && unit && textured );
		}

		var (plaid, _, _) = FabricMaterial.Maps( "Plaid", blue, cream, 4f, 0f );
		var pale = Enumerable.Range( 0, n ).Count( i => plaid[i * 4] > 180 && plaid[i * 4 + 2] > 160 );
		Report.Check( "plaid shows the second colour over about a quarter of the tile", pale > n / 6 && pale < n / 2, $"{pale * 100 / n}%" );

		var (a, _, _) = FabricMaterial.Maps( "Denim", blue, cream, 8f, 0.3f, 5 );
		var (b, _, _) = FabricMaterial.Maps( "Denim", blue, cream, 8f, 0.3f, 5 );
		Report.Check( "the same settings make the same tile", a.SequenceEqual( b ) );

		// The tile wraps: the column at x = 0 continues from x = size - 1, so a row's jump across
		// the edge is no bigger than a typical jump inside it.
		var (canvas, _, _) = FabricMaterial.Maps( "Canvas", blue, cream, 4f, 0f );
		var size = FabricMaterial.Size;
		var edge = 0; var inner = 0;
		for ( var y = 0; y < size; y++ )
		{
			edge += Math.Abs( canvas[(y * size) * 4] - canvas[(y * size + size - 1) * 4] );
			inner += Math.Abs( canvas[(y * size + size / 2) * 4] - canvas[(y * size + size / 2 - 1) * 4] );
		}
		Report.Check( "the tile wraps at its edge", edge <= inner * 2 + size, $"edge {edge}, inner {inner}" );

		var vmat = FabricMaterial.VmatSource( "Fleece", "c.png", "n.png", "r.png" );
		Report.Check( "fleece asks Complex for cloth shading", vmat.Contains( "F_CLOTH_SHADING 1" ) && vmat.Contains( "complex.vfx" ) );
		Report.Check( "satin asks for anisotropic gloss", FabricMaterial.VmatSource( "Satin", "c", "n", "r" ).Contains( "F_ANISOTROPIC_GLOSS 1" ) );
		Report.Check( "denim asks for neither", !FabricMaterial.VmatSource( "Denim", "c", "n", "r" ).Contains( "F_" ) );
	}
}
