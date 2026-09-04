using Sandbox;

namespace Effigy;

/// <summary>
/// Registers <c>.effigy</c> with the asset system, so a part studio is a thing the asset browser
/// lists and can open.
///
/// WHY THIS EXISTS AT ALL, GIVEN IT HAS NO CONTENT. The editor attribute that routes a double-click
/// to a window - <c>[EditorForAssetType]</c> on EffigyWindow - only says WHICH editor opens an
/// asset. It cannot make one. The browser dispatches a double-click through Asset.OpenInEditor(),
/// which means the file has to have been indexed as an Asset first, and s&amp;box only indexes
/// extensions it knows.
///
/// There are exactly two ways an extension becomes known. The engine's own <c>bin/assettypes.txt</c>
/// carries the built-in ones - that is how ShaderGraph claims .shdrgrph - and a library cannot add
/// to it, since it ships with the install and is overwritten on update. The other way is this: an
/// [AssetType] on a GameResource, declared in the game assembly. That is how Marionette's .riganim
/// works, and .riganim appears nowhere in assettypes.txt, which is the proof that the C# route is
/// enough on its own.
///
/// THE TYPE IS DELIBERATELY EMPTY. A part studio is a custom line-based text format written and
/// read by StudioDocument, not a serialised GameResource, and it stays that way - one line per
/// thing is what makes it diff properly in git, which a JSON blob would not. Nothing here needs to
/// understand the file: the double-click hands EffigyWindow an Asset, and the window reads the path
/// with StudioDocument exactly as File > Open does. This class only claims the extension.
/// </summary>
[AssetType( Name = "Effigy Part Studio", Extension = "effigy", Category = "Effigy" )]
public sealed class EffigyPartStudioAsset : GameResource
{
}
