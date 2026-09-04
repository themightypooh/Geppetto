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
///
/// AND IT IS ABSTRACT, WHICH IS THE WHOLE REASON THE CONSOLE IS QUIET. Claiming the extension with
/// a GameResource also tells the engine the file IS one, and the asset browser believes it: every
/// time it wants a thumbnail it calls Asset.LoadResource(), which reads the compiled file and tries
/// to parse it as the JSON a GameResource is made of. A part studio is not JSON, so that parse
/// failed and logged "Tried to load ... but couldn't load from data" - at no cost beyond the noise,
/// since nothing was waiting on the result, but on repeat, because a failed thumbnail is never
/// cached and so is attempted again every time the tile comes back into view.
///
/// Asset.TryLoadGameResource gives up BEFORE any of that on an abstract target type, and gives up
/// silently - it is the one early exit in that method with no Log.Warning attached. Abstract is
/// therefore not a description of this class so much as the switch that turns the message off, and
/// it costs nothing real: the type was never instantiated, because there is nothing to instantiate.
///
/// What it does mean is that the extension registration now rides on the type library listing
/// abstract types, which is worth knowing if .effigy ever stops opening on a double-click after an
/// engine update. That, and not the empty body, is the thing to look at first.
/// </summary>
[AssetType( Name = "Effigy Part Studio", Extension = "effigy", Category = "Effigy" )]
public abstract class EffigyPartStudioAsset : GameResource
{
}
