using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// The .vmat that puts a bake on a model: colour, normal, roughness and ambient occlusion maps bound
/// to s&amp;box's complex shader.
///
/// THE PARAMETER NAMES ARE THE ENGINE'S, READ OFF ITS OWN FILES, NOT REMEMBERED. TextureColor,
/// TextureNormal, TextureRoughness and TextureAmbientOcclusion are the inputs complex.shader_c
/// declares; g_flAmbientOcclusionDirectDiffuse and g_flAmbientOcclusionDirectSpecular are the two
/// AO controls it carries, and the engine's `templates/default.vmat` sets both to 0 — AO darkens
/// indirect light only, the usual PBR answer, unless asked otherwise. A material elsewhere on this
/// machine writes `g_flAmbientOcclusionStrength`, which complex.shader_c does not contain at all;
/// a name the shader does not know is dropped without a word, so that one does nothing.
///
/// Height is deliberately not bound. complex reads TextureHeight only under F_PARALLAX_OCCLUSION,
/// which its own validation refuses alongside detail textures, alpha test, translucency and more —
/// switching it on behind the caller's back would change how the material compiles. The height map
/// is written beside the others for whoever wants parallax, with its range in the bake report.
///
/// Same Layer0 form and shader name as <see cref="PaintMaterial.VmatSource"/>, for the same reason:
/// it is the shape the engine's template still ships. Textures are named by their SOURCE image path;
/// the asset system compiles the PNGs itself (see PaintMaterial's note on .vtex).
/// </summary>
public static class BakeMaterial
{
	/// <summary>
	/// The .vmat source. Any texture left null gets the engine's own default for that slot, so a
	/// bake with no colour map still compiles and lights like an untextured surface.
	/// </summary>
	public static string VmatSource( string color = null, string normal = null, string roughness = null, string ambientOcclusion = null,
		float metalness = 0f, float aoDirectDiffuse = 0f, float aoDirectSpecular = 0f )
	{
		var c = CultureInfo.InvariantCulture;
		var sb = new StringBuilder();

		sb.Append( "// THIS FILE IS AUTO-GENERATED\n" );
		sb.Append( "\n" );
		sb.Append( "Layer0\n" );
		sb.Append( "{\n" );
		sb.Append( "\tshader \"complex.vfx\"\n" );
		sb.Append( "\n" );
		sb.Append( "\t//---- Ambient Occlusion ----\n" );
		sb.Append( string.Format( c, "\tg_flAmbientOcclusionDirectDiffuse \"{0:0.000}\"\n", Math.Clamp( aoDirectDiffuse, 0f, 1f ) ) );
		sb.Append( string.Format( c, "\tg_flAmbientOcclusionDirectSpecular \"{0:0.000}\"\n", Math.Clamp( aoDirectSpecular, 0f, 1f ) ) );
		sb.Append( $"\tTextureAmbientOcclusion \"{ambientOcclusion ?? "materials/default/default_ao.tga"}\"\n" );
		sb.Append( "\n" );
		sb.Append( "\t//---- Color ----\n" );
		sb.Append( "\tg_flModelTintAmount \"1.000\"\n" );
		sb.Append( "\tg_vColorTint \"[1.000000 1.000000 1.000000 0.000000]\"\n" );
		sb.Append( $"\tTextureColor \"{color ?? "materials/default/default_color.tga"}\"\n" );
		sb.Append( "\n" );
		sb.Append( "\t//---- Lighting ----\n" );
		sb.Append( "\tg_flDirectionalLightmapMinZ \"0.050\"\n" );
		sb.Append( "\tg_flDirectionalLightmapStrength \"1.000\"\n" );
		sb.Append( "\n" );
		sb.Append( "\t//---- Metalness ----\n" );
		sb.Append( string.Format( c, "\tg_flMetalness \"{0:0.000}\"\n", Math.Clamp( metalness, 0f, 1f ) ) );
		sb.Append( "\n" );
		sb.Append( "\t//---- Normal ----\n" );
		sb.Append( $"\tTextureNormal \"{normal ?? "materials/default/default_normal.tga"}\"\n" );
		sb.Append( "\n" );
		sb.Append( "\t//---- Roughness ----\n" );
		sb.Append( $"\tTextureRoughness \"{roughness ?? "materials/default/default_rough.tga"}\"\n" );
		sb.Append( "\n" );
		sb.Append( "\t//---- Texture Coordinates ----\n" );
		sb.Append( "\tg_nScaleTexCoordUByModelScaleAxis \"0\"\n" );
		sb.Append( "\tg_nScaleTexCoordVByModelScaleAxis \"0\"\n" );
		sb.Append( "\tg_vTexCoordOffset \"[0.000 0.000]\"\n" );
		sb.Append( "\tg_vTexCoordScale \"[1.000 1.000]\"\n" );
		sb.Append( "\tg_vTexCoordScrollSpeed \"[0.000 0.000]\"\n" );
		sb.Append( "}\n" );

		return sb.ToString();
	}

	/// <summary>The material choices <see cref="WriteSet"/> takes: no vmat, complex, or a skin preset.</summary>
	public static readonly string[] MaterialChoices = { "None", "Complex", "Skin: Body", "Skin: Head", "Skin: Mouth", "Skin: Wet flesh" };

	/// <summary>
	/// Write a bake set the way both the editor and the script want it: one PNG per map as
	/// <c>{name}_{map}.png</c> in <paramref name="folder"/>, a <c>{name}_transmission.png</c> when the
	/// set has thickness (<see cref="SkinMaterial.TransmissionMask"/>), and optionally the .vmat that
	/// binds them — complex (<see cref="VmatSource"/>) or s&amp;box's skin shader
	/// (<see cref="SkinMaterial.VmatSource"/>, with the bent normal bound when the set has one), picked
	/// by one of <see cref="MaterialChoices"/>.
	///
	/// Texture paths in the vmat are relative to <paramref name="assetsRoot"/> (an asset path is what a
	/// vmat names); null writes bare file names. Returns every file written, PNGs first.
	/// </summary>
	public static List<string> WriteSet( BakeSet set, string folder, string name, bool flipV = false,
		string assetsRoot = null, string material = "None", float translucency = 1f, string color = null, string roughness = null )
	{
		if ( set is null ) throw new ArgumentNullException( nameof( set ) );
		Directory.CreateDirectory( folder );
		var written = new List<string>();
		var byMap = new Dictionary<string, string>();

		void Png( string map, BakedMap image )
		{
			var path = Path.Combine( folder, $"{name}_{map}.png" );
			PngWriter.WriteFile( path, image, flipV );
			written.Add( path );
			byMap[map] = path;
		}

		foreach ( var (map, image) in set.Maps )
			Png( map, image );

		if ( set.ThicknessUnits is not null )
			Png( "transmission", SkinMaterial.TransmissionMask( set, translucency ) );

		string Asset( string path ) => path is null ? null
			: assetsRoot is null ? Path.GetFileName( path )
			: Path.GetRelativePath( assetsRoot, path ).Replace( '\\', '/' );

		string Map( string map ) => byMap.TryGetValue( map, out var path ) ? Asset( path ) : null;

		var choice = (material ?? "None").Trim();

		if ( choice.Length == 0 || choice.Equals( "None", StringComparison.OrdinalIgnoreCase ) )
			return written;

		var vmat = Path.Combine( folder, name + ".vmat" );

		if ( choice.Equals( "Complex", StringComparison.OrdinalIgnoreCase ) )
		{
			File.WriteAllText( vmat, VmatSource( color, Map( "normal" ), roughness, Map( "ao" ) ) );
		}
		else
		{
			var preset = choice.StartsWith( "Skin", StringComparison.OrdinalIgnoreCase ) ? choice[(choice.IndexOf( ':' ) + 1)..] : choice;
			var p = SkinMaterial.Preset( preset );

			// Transmission only where the preset transmits: Facepunch's mouth has none, and a black
			// transmission colour with a bright mask would be a mask that does nothing.
			var trans = p.TransmissionColor.x + p.TransmissionColor.y + p.TransmissionColor.z > 0f ? Map( "transmission" ) : null;
			// A baked bent normal switches the skin shader's F_USE_BENT_NORMALS on; without one it stays off.
			File.WriteAllText( vmat, SkinMaterial.VmatSource( p, color, Map( "normal" ), roughness, Map( "ao" ), trans, Map( "bentnormal" ) ) );
		}

		written.Add( vmat );
		return written;
	}
}
