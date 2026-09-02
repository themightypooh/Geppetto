using Effigy;
using Sandbox;

namespace Marionette.EditorTools;

/// <summary>
/// How big a material wants to be, asked of the material itself.
///
/// WHY A DROP MUST GUESS AT ALL. Extrude caps take sketch coordinates straight through as UVs, so a
/// dropped material starts out repeating once per unit — once per INCH. On a diner floor that is
/// two hundred repeats across the room, which does not read as tile; it reads as noise, and the
/// first thing anyone does is go looking for a scale field. A default that is merely in the right
/// order of magnitude turns that from a rescue into a preference.
///
/// THE RULE IS THE EDITOR'S OWN, not one invented here. s&amp;box's mesh editor answers exactly this
/// question in FaceTool.UI.Texture.CalculateTextureSize, and it asks the material first:
///
///   1. `WorldMappingWidth` / `WorldMappingHeight` on the material, when it declares them. This is
///      the material saying "one repeat of me is 48 inches", which is the only source of the number
///      that is actually correct rather than plausible — the diner tile's own vmat comment says 48,
///      and putting it in the attribute is how that stops being a comment.
///   2. Otherwise the texture's pixel size times 0.25, which is the Source convention of a texel
///      per quarter unit: a 1024 map becomes 256 units across.
///   3. Otherwise 512, for a material with neither.
///
/// Agreeing with the mesh editor matters more than being clever: the same material dropped in
/// Effigy and painted onto a block in the scene should come out the same size, or one of the two is
/// wrong and there is no way to tell which.
///
/// EFFIGY MEASURES IN UNITS PER TILE, which is what this returns — the world size of one full
/// repeat. s&amp;box stores the reciprocal-ish quantity (units per texel, default 0.25) on the face;
/// CalculateTextureSize is the point where its own code converts, so this borrows the converted
/// answer rather than the storage format.
/// </summary>
internal static class EffigyMaterialSize
{
	/// <summary>What a material with nothing to say is assumed to be, matching the mesh editor's
	/// own fallback.</summary>
	private const float Fallback = 512f;

	/// <summary>
	/// The size one repeat of this material should cover, or <see cref="MaterialScale.Unscaled"/>
	/// when there is no material to ask.
	///
	/// Unscaled rather than the 512 fallback for a missing path, because "no material" is not a
	/// material with no opinion — it is a slot nobody has bound, and quietly resizing one would move
	/// the texture of whatever gets bound to it later.
	/// </summary>
	public static Vec2 For( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return MaterialScale.Unscaled;

		return For( Material.Load( path ) );
	}

	/// <summary>The same question of a loaded material.</summary>
	public static Vec2 For( Material material )
	{
		if ( material is null )
			return MaterialScale.Unscaled;

		// GetInt returns 0 for an attribute the material does not carry, which is how the engine's
		// own copy of this tells "not declared" from a real width.
		var width = material.Attributes?.GetInt( "WorldMappingWidth" ) ?? 0;
		var height = material.Attributes?.GetInt( "WorldMappingHeight" ) ?? 0;

		var texture = material.FirstTexture;

		var x = width > 0 ? width : texture is not null ? texture.Size.x * 0.25f : Fallback;
		var y = height > 0 ? height : texture is not null ? texture.Size.y * 0.25f : Fallback;

		return MaterialScale.Sanitise( new Vec2( x, y ) );
	}
}

