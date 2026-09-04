using Editor;
using Editor.Assets;
using Effigy;
using Sandbox;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Marionette.EditorTools;

/// <summary>
/// The asset browser's preview of a .effigy part studio: the model the document actually builds,
/// turning on the spot, rather than the generic document icon every unrecognised file gets.
///
/// WHY A PREVIEW AND NOT A THUMBNAIL RENDERER. The engine's thumbnail path is
/// AssetThumbnail.RenderAssetThumb, and it walks [Asset.ThumbnailRenderer] methods in priority
/// order. One of those already exists — AssetPreview.RenderAssetThumbnail in the tools addon —
/// and all it does is look for an [AssetPreview] claiming the extension, build its scene and
/// photograph it. Registering here rather than writing another ThumbnailRenderer means the SAME
/// class also fills the inspector's preview panel, which a thumbnail renderer would not, and the
/// scene, camera, lighting and turntable come from AssetPreview instead of being written again.
///
/// IT IS ALSO WHAT STOPS THE BROWSER RETRYING. RenderAssetThumb calls Asset.LoadResource() before
/// it reaches any of this, and a part studio is line-based text rather than a serialised
/// GameResource, so that load has nothing to return. The console message it used to leave is gone
/// for a different reason — EffigyPartStudioAsset is abstract, and that is the one exit in
/// Asset.TryLoadGameResource with no Log.Warning attached — but the RETRYING was this: a null
/// thumbnail is never written to the on-disk cache, so the browser re-rendered every time the tile
/// came back into view. A real bitmap gets cached as a PNG, and the whole path runs once per save.
///
/// EVERYTHING IS BEST-EFFORT. This runs against whatever is on disk, including a document saved
/// by a newer build, one whose sculpt blobs have been deleted, or one with a broken feature
/// halfway down the tree. None of that is worth an error in somebody's console while they are
/// scrolling a folder, so a failure here just leaves the icon alone.
/// </summary>
[AssetPreview( "effigy" )]
internal sealed class EffigyStudioPreview : AssetPreview
{
	public override bool IsAnimatedPreview => true;

	public override float PreviewWidgetCycleSpeed => 0.2f;

	public EffigyStudioPreview( Asset asset ) : base( asset )
	{
	}

	public override async Task InitializeAsset()
	{
		await Task.Yield();

		var path = Asset?.AbsolutePath;

		if ( string.IsNullOrEmpty( path ) || !File.Exists( path ) )
			return;

		PartStudio studio;

		try
		{
			studio = StudioDocument.ReadFile( path );
		}
		catch ( Exception )
		{
			return;
		}

		// Before the rebuild, for the reason LoadDocument gives: a sculpt feature is handed its
		// bytes here and only turns them into geometry on the rebuild below, once the cage it
		// sculpts has been built by the features above it. Missing blobs are not fatal — the
		// feature rebuilds as its unsculpted cage, which is still worth a picture.
		try
		{
			SculptSidecar.Load( studio, path );
		}
		catch ( Exception )
		{
		}

		using ( Scene.Push() )
		using ( EditorUtility.DisableTextureStreaming() )
		{
			Model model;

			try
			{
				// The report is deliberately ignored. A document with a failing feature still
				// builds everything above it, and that partial part is a better thumbnail than
				// no thumbnail — the same call the window makes for the same reason.
				studio.Rebuild();

				// Visible bodies only, and wearing their real vmats, so the browser shows what
				// the window shows rather than a flat grey stand-in.
				model = EffigyPreview.Build( studio.ToVisibleMesh(),
					slot => studio.MaterialNames.TryGetValue( slot, out var name ) ? name : null );
			}
			catch ( Exception )
			{
				return;
			}

			if ( model is null || model.MeshCount == 0 )
				return;

			PrimaryObject = new GameObject( true, "effigy part studio" );
			PrimaryObject.WorldTransform = Transform.Zero;

			var renderer = PrimaryObject.AddComponent<ModelRenderer>();
			renderer.Model = model;

			SceneSize = model.RenderBounds.Size;
			SceneCenter = renderer.WorldRotation * model.RenderBounds.Center;
		}
	}
}
