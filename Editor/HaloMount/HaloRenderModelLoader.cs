using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox;
using Sandbox.Mounting;

// Phase 3+4 (see plans/tidy-singing-backus.md): converts a single Halo 3 render_model
// tag into an s&box Model, via Reclaimer.Blam's own high-level Model/Mesh object
// (RenderModelTag.GetContent() -> Scene -> Model) rather than hand-parsing Halo's
// vertex/index buffer formats ourselves. Actual geometry/material conversion lives in
// HaloMeshConverter (shared with HaloBspLoader, which produces the same Model shape from
// ScenarioStructureBspTag) -- this class is just the render_model-specific tag lookup.
public class HaloRenderModelLoader( HaloMCCMount host, string mapPath, string tagName ) : ResourceLoader<HaloMCCMount>
{
	public string MapPath { get; } = mapPath;
	public string TagName { get; } = tagName;

	protected override object Load()
	{
		var asm = HaloMCCMount.LoadReclaimer();
		var cacheFactory = asm.GetType( "Reclaimer.Blam.Common.CacheFactory", throwOnError: true );

		dynamic cache = cacheFactory.InvokeMember(
			"ReadCacheFile",
			BindingFlags.InvokeMethod | BindingFlags.Static | BindingFlags.Public,
			null, null, new object[] { MapPath } );

		object tagItem = null;
		foreach ( dynamic tag in cache.TagIndex )
		{
			string classCode = tag.ClassCode;
			string name = tag.TagName;
			if ( classCode == "mode" && name == TagName )
			{
				tagItem = tag;
				break;
			}
		}

		if ( tagItem is null )
			throw new Exception( $"Could not find render_model tag '{TagName}' in {MapPath}" );

		var renderModelType = asm.GetType( "Reclaimer.Blam.Halo3.RenderModelTag", throwOnError: true );
		var readMetadata = tagItem.GetType().GetMethod( "ReadMetadata" ).MakeGenericMethod( renderModelType );
		dynamic renderModelTag = readMetadata.Invoke( tagItem, null );

		dynamic scene = renderModelTag.GetContent();

		dynamic reclaimerModel = null;
		foreach ( dynamic model in scene.EnumerateModels() )
		{
			reclaimerModel = model;
			break;
		}

		if ( reclaimerModel is null )
			throw new Exception( $"render_model tag '{TagName}' produced no models" );

		// Model.Meshes is a flat list covering every region/permutation. A simple weapon has
		// exactly one region (with the "real" body as its first permutation, plus alternate
		// variants as later permutations -- so within a region, only the first permutation
		// should be used). A biped like the Grunt spreads its actual body across MULTIPLE
		// regions instead (arms/backpack/head/helmet/legs/torso, confirmed via
		// halomount_diag_load -- 6 regions, one mesh each) -- taking only the first region, as
		// this used to, silently dropped 5 of them ("only arms showing"). The general rule that
		// covers both cases: take EVERY region's first permutation. MeshRange is a named
		// ValueTuple (Index, Count) -- those names are compiler sugar only, so through `dynamic`
		// the real fields are Item1/Item2.
		var meshIndices = new SortedSet<int>();
		foreach ( dynamic region in reclaimerModel.Regions )
		{
			dynamic firstPermutation = null;
			foreach ( dynamic permutation in region.Permutations ) { firstPermutation = permutation; break; }

			if ( firstPermutation is null )
				continue;

			dynamic meshRange = firstPermutation.MeshRange;
			int start = (int)meshRange.Item1;
			int count = (int)meshRange.Item2;
			for ( var i = start; i < start + count; i++ )
				meshIndices.Add( i );
		}

		var builder = Model.Builder.WithName( Path );

		var allMeshes = new List<object>();
		foreach ( dynamic m in reclaimerModel.Meshes )
			allMeshes.Add( m );

		var materialIndex = 0;
		foreach ( var i in meshIndices )
		{
			if ( i < 0 || i >= allMeshes.Count )
				continue;

			dynamic reclaimerMesh = allMeshes[i];
			if ( reclaimerMesh is null )
				continue;

			foreach ( var sbMesh in HaloMeshConverter.ConvertMesh( reclaimerModel, reclaimerMesh, ref materialIndex ) )
				builder = builder.AddMesh( sbMesh );
		}

		HaloMeshConverter.BuildSkeleton( builder, reclaimerModel );

		return builder.Create();
	}
}
