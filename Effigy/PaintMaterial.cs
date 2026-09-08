using System;

namespace Effigy;

/// <summary>
/// The authoring half of the texture path: turn a painted canvas into the assets a compiled model
/// can bind.
///
/// WHY THIS IS IN THE KERNEL AND NOT THE EDITOR. The editor is where the files get written, but the
/// TEXT — the PNG bytes and the .vmat — is exactly the kind of thing the headless suite can judge:
/// do the braces balance, does the material name the image it was given. Same reason VmdlMaterials
/// and VmdlPhysics live here.
///
/// The chain is canvas → opaque RGBA → PNG → a .vmat naming the PNG, then the .vmat is bound to the
/// painted body's material slot through the existing MaterialNames mechanism. The canvas is baked
/// opaque over white first, because the canvas's straight alpha (0 where unpainted) has no sensible
/// meaning on an ordinary material's colour map — a transparent texel reads as a hole or black — and
/// white is the blank the paint covers, the same trick the old vertex-colour Replace mode used.
///
/// THE .VMAT NAMES THE PNG, NOT A .VTEX. The first cut wrote a .vtex in the chain and pointed the
/// material at it, and the engine's material compiler refused it outright: ".vtex files not yet
/// supported as an input texture type." A material's colour texture is an IMAGE in this engine — the
/// engine's own template binds `TextureColor "materials/default/default_color.tga"` — so the material
/// points straight at the PNG and the asset system compiles the PNG to a texture on its own.
/// </summary>
public static class PaintMaterial
{
	/// <summary>
	/// The canvas as an opaque RGBA buffer, composited over a base colour. Unpainted texels fall back
	/// to the base, painted ones are source-over the base at their coverage, and the alpha is 255
	/// everywhere.
	/// </summary>
	public static byte[] OpaqueRgba( PaintCanvas canvas, byte baseR = 255, byte baseG = 255, byte baseB = 255 )
	{
		if ( canvas is null )
			throw new ArgumentNullException( nameof( canvas ) );

		var opaque = new byte[canvas.Width * canvas.Height * 4];
		canvas.BakeOpaque( opaque, baseR, baseG, baseB );
		return opaque;
	}

	/// <summary>
	/// The .vmat source that binds <paramref name="imagePath"/> as an ordinary lit material's colour.
	///
	/// The Layer0 form rather than the kv3 one, deliberately: it is the shape the engine's own
	/// `templates/default.vmat` still ships, so a material written this way compiles today where a
	/// hand-rolled kv3 material is a guess at a format this kernel has never emitted. The colour
	/// texture is the paint atlas, named by its SOURCE image path — the engine compiles the PNG to a
	/// texture itself, and a .vtex is not accepted as a texture input (see the class comment). The
	/// rest of the attributes are the same defaults the template carries, so the paint lights like
	/// any other surface.
	/// </summary>
	public static string VmatSource( string imagePath )
	{
		return
			"// THIS FILE IS AUTO-GENERATED\n" +
			"\n" +
			"Layer0\n" +
			"{\n" +
			"\tshader \"complex.vfx\"\n" +
			"\n" +
			"\t//---- Color ----\n" +
			"\tg_flModelTintAmount \"1.000\"\n" +
			"\tg_vColorTint \"[1.000000 1.000000 1.000000 0.000000]\"\n" +
			$"\tTextureColor \"{imagePath}\"\n" +
			"\n" +
			"\t//---- Lighting ----\n" +
			"\tg_flDirectionalLightmapMinZ \"0.050\"\n" +
			"\tg_flDirectionalLightmapStrength \"1.000\"\n" +
			"\n" +
			"\t//---- Metalness ----\n" +
			"\tg_flMetalness \"0.000\"\n" +
			"\n" +
			"\t//---- Normal ----\n" +
			"\tTextureNormal \"materials/default/default_normal.tga\"\n" +
			"\n" +
			"\t//---- Roughness ----\n" +
			"\tTextureRoughness \"materials/default/default_rough.tga\"\n" +
			"\n" +
			"\t//---- Texture Coordinates ----\n" +
			"\tg_nScaleTexCoordUByModelScaleAxis \"0\"\n" +
			"\tg_nScaleTexCoordVByModelScaleAxis \"0\"\n" +
			"\tg_vTexCoordOffset \"[0.000 0.000]\"\n" +
			"\tg_vTexCoordScale \"[1.000 1.000]\"\n" +
			"\tg_vTexCoordScrollSpeed \"[0.000 0.000]\"\n" +
			"}\n";
	}
}
