using Sandbox;

namespace Marionette;

// The knobs materials/pixel_arms.vmat reads, in one place, so something other than the in-game
// viewmodel can wear the same look -- the rig editor's viewport uses it to pose against the arms
// as they actually appear rather than against a white mannequin.
//
// TWO THINGS ABOUT THIS MATERIAL ARE NOT OPTIONAL, both learned the hard way in the game this
// shader came from:
//
//   - It goes on as a WHOLE-MODEL MaterialOverride, not per-slot. That's what keeps the pixelation
//     scoped to this one renderer instead of the whole screen.
//
//   - A MaterialOverride cannot reach the model's own textures. Set it and nothing else and the
//     arms come out as flat-colour mannequin, which reads as a broken shader rather than as pixel
//     art. The albedo has to be handed over by hand as an attribute -- that's what ColorTexture is
//     for, and ResolveSkinTexture finds the usual one.
//
// This is sample content - the rig editor uses it so the included arms can be posed as they
// actually look rather than as a white mannequin. Nothing in the tool requires it.
public sealed class PixelArmsStyle
{
	// The generated albedo the stock first-person arms use. The path is ugly because the content
	// compiler mangles the source png name with a hash, but it's stable for a given package
	// version. Find the current one by searching "first_person" in the asset browser -- it's the
	// one ending _color.
	public const string DefaultSkinTexturePath =
		"models/first_person/materials/v_first_person_arms_human_color_png_5762368.generated.vtex";

	public const string DefaultSkinMaterialPath =
		"models/first_person/materials/v_first_person_arms_human_light.vmat";

	public Texture ColorTexture { get; init; }

	public float VertexSnap { get; init; } = 6f;
	public float TexelGrid { get; init; } = 180f;
	public float PixelBlock { get; init; } = 0f;
	public float ObjectSnap { get; init; } = 0f;
	public float FlatShade { get; init; } = 0f;
	public float ColorSteps { get; init; } = 0f;
	public float ColorDepth { get; init; } = 0f;
	public float MipLevel { get; init; } = 5f;
	public Color PixelColor { get; init; } = new( 0.76f, 0.66f, 0.58f );
	public float Roughness { get; init; } = 0.85f;
	public float Metalness { get; init; } = 0f;

	// Self-lit by default, which is what makes this usable in a bare editor scene - the look
	// doesn't depend on whatever lighting happens to be set up around it.
	public bool SelfLit { get; init; } = true;
	public Vector3 LightDirection { get; init; } = new( -0.4f, -0.6f, 0.7f );
	public float Ambient { get; init; } = 0.55f;
	public float Diffuse { get; init; } = 0.75f;

	public bool DebugShowTexture { get; init; }

	/// <summary>Which sign of object-space y to discard, for hiding one arm. 0 keeps both, which
	/// is what the rig editor wants - it's posing the whole skeleton, not framing a shot.</summary>
	public float HideSide { get; init; }

	/// <summary>The vertex snap works in screen pixels, so the shader needs the resolution in the
	/// vertex stage where the engine's viewport globals aren't dependable. In a tool window this is
	/// the widget's size, not Screen.Size.</summary>
	public Vector2 ScreenSize { get; init; } = new( 1920, 1080 );

	public static Material LoadMaterial() => Material.Load( "materials/pixel_arms.vmat" );

	/// <summary>The arms' albedo, so the override samples real skin instead of rendering a
	/// mannequin. Named outright rather than discovered - FirstTexture returns the wrong map on
	/// this material, and flat-coloured arms are a much better failure than red ones.</summary>
	public static Texture ResolveSkinTexture(
		string texturePath = DefaultSkinTexturePath,
		string materialPath = DefaultSkinMaterialPath )
	{
		if ( !string.IsNullOrWhiteSpace( texturePath ) && Texture.Load( texturePath, false ) is { } direct )
			return direct;

		if ( string.IsNullOrWhiteSpace( materialPath ) || Material.Load( materialPath ) is not { } material )
			return null;

		return material.GetTexture( "g_tColor" )
			?? material.GetTexture( "Color" )
			?? material.GetTexture( "g_tAlbedo" );
	}

	public void ApplyTo( SceneObject so )
	{
		if ( !so.IsValid() )
			return;

		if ( ColorTexture is not null )
			so.Attributes.Set( "ArmColorTex", ColorTexture );

		so.Attributes.Set( "ArmHasTex", ColorTexture is not null ? 1f : 0f );
		so.Attributes.Set( "ArmPixelColor", new Vector4( PixelColor.r, PixelColor.g, PixelColor.b, PixelColor.a ) );
		so.Attributes.Set( "ArmPixelBlock", PixelBlock );
		so.Attributes.Set( "ArmTexelGrid", TexelGrid );
		so.Attributes.Set( "ArmVertSnap", VertexSnap );
		so.Attributes.Set( "ArmPixelSize", ObjectSnap );
		so.Attributes.Set( "ArmScreenSize", ScreenSize );
		so.Attributes.Set( "ArmFlatShade", FlatShade );
		so.Attributes.Set( "ArmColorSteps", ColorSteps );
		so.Attributes.Set( "ArmRoughness", Roughness );
		so.Attributes.Set( "ArmMetalness", Metalness );
		so.Attributes.Set( "ArmHideSide", HideSide );
		so.Attributes.Set( "ArmMip", MipLevel );
		so.Attributes.Set( "ArmColorDepth", ColorDepth );
		so.Attributes.Set( "ArmDebug", DebugShowTexture ? 1f : 0f );
		so.Attributes.Set( "ArmSelfLit", SelfLit ? 1f : 0f );
		so.Attributes.Set( "ArmLightDir", LightDirection.Normal );
		so.Attributes.Set( "ArmAmbient", Ambient );
		so.Attributes.Set( "ArmDiffuse", Diffuse );
	}
}
