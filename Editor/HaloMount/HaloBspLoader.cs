using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox;
using Sandbox.Mounting;

// Level geometry, the Guardian-map follow-up to HaloRenderModelLoader. ScenarioStructureBspTag
// produces the exact same Reclaimer Model/Mesh/Segment shape as RenderModelTag (see
// ScenarioStructureBspTag.GetModelContent() in Reclaimer.Blam), so HaloMeshConverter handles
// both -- this class is just the sbsp-specific tag lookup and region selection.
//
// A BSP's first Region is always the "clusters" group (added before any per-instance-group
// regions in Reclaimer's own GetModelContent()) -- that's the static level geometry itself.
// Unlike a weapon's render_model, where only the first permutation's MeshRange is the "real"
// body and the rest are alternate variants, EVERY permutation in the clusters region is a
// real, distinct piece of the level that all need converting -- a level is not one mesh.
// Embedded geometry instances (crates/scenery welded into the BSP, later regions) are not
// converted yet -- v1 is the static shell only.
public class HaloBspLoader( HaloMCCMount host, string mapPath, string tagName ) : ResourceLoader<HaloMCCMount>
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
			if ( classCode == "sbsp" && name == TagName )
			{
				tagItem = tag;
				break;
			}
		}

		if ( tagItem is null )
			throw new Exception( $"Could not find structure_bsp tag '{TagName}' in {MapPath}" );

		var bspType = asm.GetType( "Reclaimer.Blam.Halo3.ScenarioStructureBspTag", throwOnError: true );
		var readMetadata = tagItem.GetType().GetMethod( "ReadMetadata" ).MakeGenericMethod( bspType );
		dynamic bspTag = readMetadata.Invoke( tagItem, null );

		dynamic scene = bspTag.GetContent();

		dynamic reclaimerModel = null;
		foreach ( dynamic model in scene.EnumerateModels() )
		{
			reclaimerModel = model;
			break;
		}

		if ( reclaimerModel is null )
			throw new Exception( $"structure_bsp tag '{TagName}' produced no models" );

		dynamic clusterRegion = null;
		foreach ( dynamic region in reclaimerModel.Regions ) { clusterRegion = region; break; }

		var meshIndices = new SortedSet<int>();
		if ( clusterRegion is not null )
		{
			foreach ( dynamic permutation in clusterRegion.Permutations )
			{
				dynamic meshRange = permutation.MeshRange;
				int start = (int)meshRange.Item1;
				int count = (int)meshRange.Item2;
				for ( var i = start; i < start + count; i++ )
					meshIndices.Add( i );
			}
		}

		var allMeshes = new List<object>();
		foreach ( dynamic m in reclaimerModel.Meshes )
			allMeshes.Add( m );

		Log.Info( $"[HaloMount] BSP '{TagName}': {allMeshes.Count} total meshes, {meshIndices.Count} clusters to convert" );

		var builder = Model.Builder.WithName( Path );
		var materialIndex = 0;
		var meshesAdded = 0;

		foreach ( var i in meshIndices )
		{
			if ( i < 0 || i >= allMeshes.Count )
				continue;

			dynamic reclaimerMesh = allMeshes[i];
			if ( reclaimerMesh is null )
				continue;

			foreach ( var sbMesh in HaloMeshConverter.ConvertMesh( reclaimerMesh, ref materialIndex ) )
			{
				builder = builder.AddMesh( sbMesh );
				meshesAdded++;
			}
		}

		Log.Info( $"[HaloMount] BSP '{TagName}': converted into {meshesAdded} s&box meshes across {materialIndex} materials" );

		return builder.Create();
	}
}
