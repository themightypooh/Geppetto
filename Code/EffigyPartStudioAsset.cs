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
/// IT CANNOT BE ABSTRACT, AND THE CONSOLE WARNING IS THE PRICE. Claiming the extension also tells
/// the engine the file IS a GameResource, so the asset browser calls Asset.LoadResource() on every
/// thumbnail pass and tries to read a line-based part studio as the JSON a GameResource is made of.
/// It fails and logs "Tried to load ... but couldn't load from data". Nothing is waiting on the
/// result, so the message is the entire cost.
///
/// Asset.TryLoadGameResource does have an exit that skips all of that silently - an abstract target
/// type - and it was tried here. It bricks the editor. AssetType.GenerateGlyphs builds this type's
/// browser icon with Activator.CreateInstance, which throws on an abstract class, and it runs
/// inside StartupLoadProject.OpenProject: the failure is not a warning but "Failed to bootstrap
/// engine", on every attempt to open any project the library is installed in. Do not try it again.
///
/// EffigyStudioPreview is what actually helps, and it does not touch the load. A rendered thumbnail
/// gets cached to PNG, and a cached thumbnail is never re-rendered, so the load runs once per save
/// instead of every time the tile scrolls back into view.
/// </summary>
[AssetType( Name = "Effigy Part Studio", Extension = "effigy", Category = "Effigy" )]
public sealed class EffigyPartStudioAsset : GameResource
{
}
