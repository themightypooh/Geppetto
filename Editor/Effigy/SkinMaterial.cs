using System;
using System.Globalization;
using System.Text;

namespace Effigy;

/// <summary>
/// The .vmat for s&amp;box's skin shader — the only shipped s&amp;box shader with subsurface scattering
/// (Valve's pre-integrated skin: a curvature lookup plus a blurred diffuse normal) and per-texel
/// transmission. complex.vfx, which <see cref="FleshMaterial"/> and <see cref="BakeMaterial"/> write,
/// has neither; flesh on it reads as plastic however good the albedo is.
///
/// EVERY KEY AND NUMBER IS FACEPUNCH'S, COPIED FROM THEIR HUMAN VMATS, NOT REMEMBERED. Source:
/// github.com/Facepunch/sbox-public game/addons/citizen/Assets/models/citizen_human/ —
/// bodies/male/body_mode_dark.vmat (Body), heads/citizen_human_malehead01/citizen_human_malehead01.vmat
/// (Head) and …/mouth.vmat (Mouth). Same shader line ("shaders/skin.shader"), same sections, same
/// order. The human models only: Facepunch dropped the old Citizen.
///
/// `strings core/shaders/skin.shader_c` shows most of the names (F_USE_BENT_NORMALS, F_SSS_MASK,
/// TextureTransmissiveMask, g_flCurvatureScale…) but NOT g_vAmbientOcclusionColorBleed or
/// F_MORPH_SUPPORTED, which Facepunch's own files set — the compiled variants are compressed, so a
/// missing string proves nothing. The vmats are the authority. What still wants a look in the
/// editor (does each key show as set in the material panel?) is listed in
/// docs/progress/effigy-sbox-output.md.
///
/// See ~/.claude/skills/organic-modeling/references/techniques/realtime-flesh-shading.md for why
/// each value is what it is (curvature only bites on radii under ~13 in; transmission is for parts
/// under ~3 mm; the red AO bleed does much of the "flesh" work in creases).
/// </summary>
public static class SkinMaterial
{
	/// <summary>The shader-side numbers of one skin material. Start from a preset and change what differs.</summary>
	public sealed class Params
	{
		public string Name;

		/// <summary>Scales the curvature fed to the SSS lookup. Bodies 0.463, heads 0.279, mouth 0.569.</summary>
		public float CurvatureScale = 0.463f;

		/// <summary>How blurred the diffuse ("SSS") normal is against the detail normal.</summary>
		public float DiffuseNormalBlur = 2f;

		/// <summary>Second GGX lobe mix: 0.008 is dry skin (effectively off), 0.263 the wet mouth.</summary>
		public float SecondLobeRatio = 0.008f;

		public float SecondLobeGlossiness = 0.392f;

		/// <summary>How fast transmission fades: 0.759 head, 0.8 body, 1.858 mouth.</summary>
		public float TransmissionFalloff = 0.8f;

		/// <summary>The colour light comes through as. Human: #BF6302 orange-red.</summary>
		public Vec3 TransmissionColor = new( 0.749020f, 0.388235f, 0.007843f );

		/// <summary>AO tinted red, so creases go red rather than grey. Facepunch's on every skin.</summary>
		public Vec3 AoColorBleed = new( 0.400000f, 0.149020f, 0.129412f );

		/// <summary>Per-channel softening of the ambient normal (R fully soft).</summary>
		public Vec3 AmbientNormalSoftness = new( 1.000000f, 0.301961f, 0f );

		/// <summary>Multiplies the albedo; the mouth darkens to [0.741 0.722 0.682].</summary>
		public Vec3 ColorTint = new( 1f, 1f, 1f );

		/// <summary>One flat roughness when no roughness map is given (the mouth's 0.552). Null = the engine default map.</summary>
		public float? ConstantRoughness;

		/// <summary>Set when the model has morph targets (the human head and mouth do).</summary>
		public bool Morphs;
	}

	/// <summary>The human body (body_mode_dark): dry skin, no transmission of its own.</summary>
	public static Params Body() => new() { Name = "Body" };

	/// <summary>The human head (malehead01): tighter curvature, more blur, transmission at the ears.</summary>
	public static Params Head() => new()
	{
		Name = "Head", CurvatureScale = 0.279f, DiffuseNormalBlur = 1.834f, TransmissionFalloff = 0.759f, Morphs = true,
	};

	/// <summary>
	/// The human mouth interior: wet tissue in s&amp;box is a flat roughness of 0.552 plus a real second
	/// lobe (0.263 at gloss 0.3), not a near-mirror; no transmission.
	/// </summary>
	public static Params Mouth() => new()
	{
		Name = "Mouth", CurvatureScale = 0.569f, DiffuseNormalBlur = 0.435f,
		SecondLobeRatio = 0.263f, SecondLobeGlossiness = 0.3f,
		TransmissionFalloff = 1.858f, TransmissionColor = new Vec3( 0, 0, 0 ),
		ColorTint = new Vec3( 0.741176f, 0.721569f, 0.682353f ), ConstantRoughness = 0.552f, Morphs = true,
	};

	/// <summary>
	/// Wet creature flesh (sacs, exposed meat): the body's SSS with the mouth's wet second lobe.
	/// realtime-flesh-shading.md §5.1. Curvature is left at the body's 0.463; big masses may want
	/// 1–3, which is one of the in-engine checks still open.
	/// </summary>
	public static Params WetFlesh() => new()
	{
		Name = "Wet flesh", SecondLobeRatio = 0.263f, SecondLobeGlossiness = 0.3f,
	};

	public static readonly string[] Presets = { "Body", "Head", "Mouth", "Wet flesh" };

	public static Params Preset( string name ) => name?.Trim().ToLowerInvariant().Replace( '_', ' ' ) switch
	{
		"head" => Head(),
		"mouth" => Mouth(),
		"wet flesh" or "wet" or "flesh" => WetFlesh(),
		_ => Body(),
	};

	/// <summary>
	/// The .vmat source. Texture paths are asset-relative source images, the way every Effigy vmat
	/// names them; a null map gets the default Facepunch's files use for that slot (black for the
	/// transmission mask, i.e. none). A bent-normal map switches F_USE_BENT_NORMALS on; without one
	/// the flag stays off rather than pointing the shader at nothing.
	/// </summary>
	public static string VmatSource( Params p, string color = null, string normal = null, string roughness = null,
		string ambientOcclusion = null, string transmissiveMask = null, string bentNormal = null )
	{
		p ??= Body();
		var sb = new StringBuilder();

		sb.Append( "// THIS FILE IS AUTO-GENERATED by Effigy (SkinMaterial). Values from Facepunch's citizen_human vmats.\n" );
		sb.Append( "\n" );
		sb.Append( "Layer0\n" );
		sb.Append( "{\n" );
		sb.Append( "\tshader \"shaders/skin.shader\"\n" );

		if ( p.Morphs )
		{
			sb.Append( "\n\t//---- Animation ----\n" );
			sb.Append( "\tF_MORPH_SUPPORTED 1\n" );
		}

		if ( bentNormal is not null )
		{
			sb.Append( "\n\t//---- Skin ----\n" );
			sb.Append( "\tF_USE_BENT_NORMALS 1\n" );
		}

		sb.Append( "\n\t//---- Ambient Occlusion ----\n" );
		sb.Append( "\tg_flAmbientOcclusionDirectDiffuse \"1.000\"\n" );
		sb.Append( "\tg_flAmbientOcclusionDirectSpecular \"1.000\"\n" );
		sb.Append( $"\tg_vAmbientOcclusionColorBleed \"{Vector( p.AoColorBleed, 0f )}\"\n" );
		sb.Append( $"\tTextureAmbientOcclusion \"{ambientOcclusion ?? "materials/default/default_ao.tga"}\"\n" );

		sb.Append( "\n\t//---- Color ----\n" );
		sb.Append( "\tg_flModelTintAmount \"1.000\"\n" );
		sb.Append( $"\tg_vColorTint \"{Vector( p.ColorTint, 0f )}\"\n" );
		sb.Append( $"\tTextureColor \"{color ?? "materials/default/default_color.tga"}\"\n" );

		sb.Append( "\n\t//---- Curvature ----\n" );
		sb.Append( $"\tg_flCurvatureScale \"{F( p.CurvatureScale )}\"\n" );

		sb.Append( "\n\t//---- Fog ----\n" );
		sb.Append( "\tg_bFogEnabled \"1\"\n" );

		sb.Append( "\n\t//---- Lookup Textures ----\n" );
		sb.Append( $"\tg_vAmbientNormalSoftness \"{Vector( p.AmbientNormalSoftness, 0f )}\"\n" );
		sb.Append( "\tTextureDiffuseFalloff \"materials/default/default_skin_diffusewarp.tga\"\n" );

		sb.Append( "\n\t//---- Normal ----\n" );
		sb.Append( $"\tg_flDiffuseNormalBlur \"{F( p.DiffuseNormalBlur )}\"\n" );

		if ( bentNormal is not null )
			sb.Append( $"\tTextureBentNormal \"{bentNormal}\"\n" );

		sb.Append( $"\tTextureNormal \"{normal ?? "materials/default/default_normal.tga"}\"\n" );

		sb.Append( "\n\t//---- Roughness ----\n" );

		if ( roughness is not null )
			sb.Append( $"\tTextureRoughness \"{roughness}\"\n" );
		else if ( p.ConstantRoughness is float r )
			sb.Append( $"\tTextureRoughness \"{Vector( new Vec3( r, r, r ), 0f )}\"\n" );
		else
			sb.Append( "\tTextureRoughness \"materials/default/default_rough.tga\"\n" );

		sb.Append( "\n\t//---- Specular ----\n" );
		sb.Append( $"\tg_flSecondSpecularLobeGlossiness \"{F( p.SecondLobeGlossiness )}\"\n" );
		sb.Append( $"\tg_flSecondSpecularLobeRatio \"{F( p.SecondLobeRatio )}\"\n" );

		sb.Append( "\n\t//---- Texture Coordinates ----\n" );
		sb.Append( "\tg_vTexCoordOffset \"[0.000 0.000]\"\n" );
		sb.Append( "\tg_vTexCoordScale \"[1.000 1.000]\"\n" );

		sb.Append( "\n\t//---- Transmission ----\n" );
		sb.Append( $"\tg_flTransmissionFalloff \"{F( p.TransmissionFalloff )}\"\n" );
		sb.Append( $"\tg_vTransmissionColor \"{Vector( p.TransmissionColor, 0f )}\"\n" );
		sb.Append( $"\tTextureTransmissiveMask \"{transmissiveMask ?? "[0.000000 0.000000 0.000000 0.000000]"}\"\n" );
		sb.Append( "}\n" );

		return sb.ToString();
	}

	// --- transmission ------------------------------------------------------------------------------

	/// <summary>
	/// How much red light gets through <paramref name="mm"/> millimetres of skin: the red channel of
	/// d'Eon, Luebke &amp; Enderton's six-Gaussian skin profile with Jimenez's constants
	/// (SeparableSSS.h, SSSSTransmittance). 0.64 at 0.25 mm, 0.31 at 1 mm, 0.09 at 2 mm, 0.03 at 3 mm.
	/// </summary>
	public static float SkinTransmittanceRed( float mm )
	{
		var dd = -mm * mm;
		return 0.233f * MathF.Exp( dd / 0.0064f )
			+ 0.100f * MathF.Exp( dd / 0.0484f )
			+ 0.118f * MathF.Exp( dd / 0.187f )
			+ 0.113f * MathF.Exp( dd / 0.567f )
			+ 0.358f * MathF.Exp( dd / 1.99f )
			+ 0.078f * MathF.Exp( dd / 7.41f );
	}

	/// <summary>
	/// The transmission mask for TextureTransmissiveMask, from a thickness bake
	/// (<see cref="BakeMaps.Thickness"/>, read from <see cref="BakeSet.ThicknessUnits"/>, never from the
	/// 8-bit map): mask = skin's red transmittance at thickness × mm-per-unit ÷ <paramref name="translucency"/>.
	///
	/// <paramref name="translucency"/> is the k of realtime-flesh-shading.md §3.3: 1 is real skin,
	/// 3–10 gel-like creature flesh. Sanity check from the human head: the mask is non-zero on a few
	/// percent of texels, median about 0.1, max about 0.35. If half the creature glows, k is too big.
	///
	/// <paramref name="millimetresPerUnit"/> is 25.4 for s&amp;box (a unit is an inch). Unhit texels
	/// are black (no transmission), then padded like every other map.
	/// </summary>
	public static BakedMap TransmissionMask( BakeSet set, float translucency = 1f, float millimetresPerUnit = 25.4f, int padding = 4 )
	{
		if ( set?.Thickness is null || set.ThicknessUnits is null )
			throw new ArgumentException( "Needs a bake with BakeMaps.Thickness in it.", nameof( set ) );

		var k = MathF.Max( translucency, 1e-3f );
		var map = new BakedMap( set.Thickness.Width, set.Thickness.Height );

		for ( var i = 0; i < set.ThicknessUnits.Length; i++ )
		{
			var t = set.ThicknessUnits[i];
			var g = t < 0f ? 0f : Math.Clamp( SkinTransmittanceRed( t * millimetresPerUnit / k ), 0f, 1f );
			var b = (byte)Math.Clamp( (int)MathF.Round( g * 255f ), 0, 255 );
			map.Rgb[i * 3] = map.Rgb[i * 3 + 1] = map.Rgb[i * 3 + 2] = b;
			map.Filled[i] = t >= 0f;
		}

		NormalBake.Dilate( map, padding );
		return map;
	}

	static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

	static string Vector( Vec3 v, float w ) => string.Format( CultureInfo.InvariantCulture,
		"[{0:0.000000} {1:0.000000} {2:0.000000} {3:0.000000}]", v.x, v.y, v.z, w );
}
