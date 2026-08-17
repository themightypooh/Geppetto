using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Editor;
using Sandbox;

// Phase 1 spike (see plans/tidy-singing-backus.md): prove Reclaimer.Blam can run
// inside the s&box editor process before writing any real conversion code.
//
// s&box tool/editor projects currently have no supported way to add a third-party
// DLL reference -- the .csproj is auto-regenerated from the .sbproj on every project
// load, silently discarding any hand-added <Reference> (confirmed open/unimplemented:
// Facepunch/sbox-public#6826, "Add NuGet package support for tool projects"). So
// instead of a compile-time reference, we Assembly.LoadFrom the DLLs at runtime and
// talk to them via `dynamic` -- runtime binding needs no project reference at all.
//
// Editor-only, deliberately -- Reclaimer.Blam is GPL-3.0 and this must never end up
// in Code/ or get shipped with the published game.
public static class HaloMountSpike
{
	// Hot-reloaded editor assemblies don't live at the project's source path (Assembly.Location
	// resolves into the sbox install itself), so this has to be a hardcoded absolute path rather
	// than derived relative to this assembly.
	const string LibrariesDir = @"C:\Users\po\Documents\s&box projects\marionette\Editor\HaloMount\Libraries";

	const string TestMapPath = @"C:\Program Files (x86)\Steam\steamapps\common\Halo The Master Chief Collection\halo3\maps\010_jungle.map";

	static Assembly reclaimerBlam;

	static Assembly LoadReclaimer()
	{
		if ( reclaimerBlam is not null )
			return reclaimerBlam;

		// Load dependencies first so Reclaimer.Blam's own resolution finds them already loaded.
		Assembly.LoadFrom( Path.Combine( LibrariesDir, "System.Drawing.Common.dll" ) );
		Assembly.LoadFrom( Path.Combine( LibrariesDir, "Reclaimer.Core.dll" ) );
		reclaimerBlam = Assembly.LoadFrom( Path.Combine( LibrariesDir, "Reclaimer.Blam.dll" ) );

		return reclaimerBlam;
	}

	[Menu( "Editor", "Halo Mount/Test Reclaimer Load", "science" )]
	[ConCmd( "halomount_test" )]
	public static void TestLoad()
	{
		Log.Info( $"[HaloMount] Libraries dir: {LibrariesDir}" );
		Log.Info( $"[HaloMount] Opening {TestMapPath}" );

		try
		{
			var asm = LoadReclaimer();
			var cacheFactory = asm.GetType( "Reclaimer.Blam.Common.CacheFactory", throwOnError: true );

			dynamic cache = cacheFactory.InvokeMember(
				"ReadCacheFile",
				BindingFlags.InvokeMethod | BindingFlags.Static | BindingFlags.Public,
				null, null, new object[] { TestMapPath } );

			Log.Info( $"[HaloMount] Engine build: {cache.BuildString}, cache type: {cache.CacheType}, tag count: {cache.TagIndex.TagCount}" );

			var counts = new System.Collections.Generic.Dictionary<string, int>();
			foreach ( dynamic tag in cache.TagIndex )
			{
				string classCode = tag.ClassCode;
				if ( classCode is null )
					continue;

				counts.TryGetValue( classCode, out var n );
				counts[classCode] = n + 1;
			}

			foreach ( var (classCode, count) in counts )
				Log.Info( $"[HaloMount]   {classCode}: {count}" );

			Log.Info( "[HaloMount] Load succeeded." );
		}
		catch ( Exception ex )
		{
			Log.Error( $"[HaloMount] Load failed: {ex}" );
		}
	}

	const string OdstMapsDir = @"C:\Program Files (x86)\Steam\steamapps\common\Halo The Master Chief Collection\halo3odst\maps";

	const string Halo3MapsDir = @"C:\Program Files (x86)\Steam\steamapps\common\Halo The Master Chief Collection\halo3\maps";

	// Only one bipd tag with the exact short name "grunt" ever showed up in the registered
	// catalog (asset_search confirmed just one grunt.vmdl) -- but Halo 3 has several distinct
	// Grunt ranks (minor/major/ultra/spec-ops/heretic) as genuinely separate tags, most likely
	// living in later campaign missions with actual Covenant combat, not the early
	// 010_jungle.map the one registered "grunt" came from. This scans every halo3 campaign map
	// for any bipd tag whose name contains the given substring, regardless of whether a
	// matching render_model was found -- broader than what Mount() registers.
	[ConCmd( "halomount_find_biped" )]
	public static void FindBiped( string nameContains )
	{
		var asm = LoadReclaimer();
		var cacheFactory = asm.GetType( "Reclaimer.Blam.Common.CacheFactory", throwOnError: true );

		var mapPaths = Directory.GetFiles( Halo3MapsDir, "*.map" )
			.Where( p =>
			{
				var name = Path.GetFileName( p );
				return !name.Equals( "campaign.map", StringComparison.OrdinalIgnoreCase )
					&& !name.Equals( "shared.map", StringComparison.OrdinalIgnoreCase );
			} );

		foreach ( var mapPath in mapPaths )
		{
			dynamic cache;
			try
			{
				cache = cacheFactory.InvokeMember(
					"ReadCacheFile",
					BindingFlags.InvokeMethod | BindingFlags.Static | BindingFlags.Public,
					null, null, new object[] { mapPath } );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[HaloMount] Skipping unreadable map {mapPath}: {ex.Message}" );
				continue;
			}

			foreach ( dynamic tag in cache.TagIndex )
			{
				string classCode = tag.ClassCode;
				if ( classCode != "bipd" )
					continue;

				string tagName = tag.TagName;
				if ( tagName.Contains( nameContains, StringComparison.OrdinalIgnoreCase ) )
					Log.Info( $"[HaloMount] bipd in {Path.GetFileName( mapPath )}: {tagName}" );
			}
		}
	}

	[Menu( "Editor", "Halo Mount/Find ODST Pistol", "search" )]
	[ConCmd( "halomount_find_pistol" )]
	public static void FindPistol()
	{
		var asm = LoadReclaimer();
		var cacheFactory = asm.GetType( "Reclaimer.Blam.Common.CacheFactory", throwOnError: true );

		// campaign.map / shared.map are global cache files that Reclaimer's engine-detection
		// misreads as Halo2 for this ODST install (throws in TagIndex.ReadItems) -- the actual
		// per-mission maps parse fine, same as the earlier halo3/010_jungle.map spike.
		var missionMaps = new[] { "L200", "c100", "c200", "h100", "l300", "sc100", "sc110", "sc120", "sc130", "sc140", "sc150" }
			.Select( name => Path.Combine( OdstMapsDir, $"{name}.map" ) );

		foreach ( var mapPath in missionMaps )
		{
			Log.Info( $"[HaloMount] Scanning {mapPath}" );

			try
			{
				dynamic cache = cacheFactory.InvokeMember(
					"ReadCacheFile",
					BindingFlags.InvokeMethod | BindingFlags.Static | BindingFlags.Public,
					null, null, new object[] { mapPath } );

				foreach ( dynamic tag in cache.TagIndex )
				{
					try
					{
						string classCode = tag.ClassCode;
						if ( classCode != "weap" )
							continue;

						string tagName = tag.TagName;
						Log.Info( $"[HaloMount]   weap: {tagName}" );
					}
					catch ( Exception tagEx )
					{
						Log.Warning( $"[HaloMount]   (skipped a weap tag: {tagEx.Message})" );
					}
				}
			}
			catch ( Exception mapEx )
			{
				var real = mapEx is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : mapEx;
				Log.Warning( $"[HaloMount] Failed to scan {mapPath}: {real}" );
			}
		}
	}

	// BaseGameMount.Mount() only runs once when the mount is enabled -- hot-reloading new
	// context.Add() calls (or changed ResourceLoader.Load() logic) into HaloMCCMount doesn't
	// re-trigger it, and resource loaders cache their result after the first Load(). Force a
	// remount via the engine's own MountHost so edited conversion code actually gets re-run.
	[Menu( "Editor", "Halo Mount/Remount", "refresh" )]
	[ConCmd( "halomount_remount" )]
	public static void Remount()
	{
		// MountHost is internal to Sandbox.Mounting.dll and isn't reachable as a singleton from
		// outside -- but every BaseGameMount instance holds a private `_host` reference to it
		// (confirmed via reflection dump), so pull it off our own live mount instance instead.
		var haloMount = HaloMCCMount.ActiveInstance;
		if ( haloMount is null )
		{
			Log.Error( "[HaloMount] No active HaloMCCMount instance -- has it initialized yet?" );
			return;
		}

		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

		var hostField = typeof( Sandbox.Mounting.BaseGameMount ).GetField( "_host", flags );
		var host = hostField?.GetValue( haloMount );
		if ( host is null )
		{
			Log.Error( "[HaloMount] Could not read _host field off the mount instance." );
			return;
		}

		var mountHostType = host.GetType();
		mountHostType.GetMethod( "Unmount", flags )?.Invoke( host, new object[] { "halomcc" } );
		mountHostType.GetMethod( "Mount", flags )?.Invoke( host, new object[] { "halomcc" } );

		Log.Info( "[HaloMount] Remounted halomcc" );
	}

	const string GuardianMap = @"C:\Program Files (x86)\Steam\steamapps\common\Halo The Master Chief Collection\halo3\maps\guardian.map";

	[Menu( "Editor", "Halo Mount/Inspect Guardian", "map" )]
	[ConCmd( "halomount_inspect_guardian" )]
	public static void InspectGuardian()
	{
		var asm = LoadReclaimer();
		var cacheFactory = asm.GetType( "Reclaimer.Blam.Common.CacheFactory", throwOnError: true );

		dynamic cache = cacheFactory.InvokeMember(
			"ReadCacheFile",
			BindingFlags.InvokeMethod | BindingFlags.Static | BindingFlags.Public,
			null, null, new object[] { GuardianMap } );

		Log.Info( $"[HaloMount] guardian.map: build={cache.BuildString}, cacheType={cache.CacheType}, tags={cache.TagIndex.TagCount}" );

		dynamic scenarioTag = null;
		foreach ( dynamic tag in cache.TagIndex )
		{
			string classCode = tag.ClassCode;
			if ( classCode == "sbsp" )
			{
				string bspName = tag.TagName;
				Log.Info( $"[HaloMount]   sbsp (structure_bsp): {bspName}" );
			}
			else if ( classCode == "scnr" )
			{
				scenarioTag = tag;
				string scnrName = tag.TagName;
				Log.Info( $"[HaloMount]   scnr (scenario): {scnrName}" );
			}
		}

		if ( scenarioTag is null )
		{
			Log.Warning( "[HaloMount] No scnr tag found." );
			return;
		}

		var scenarioType = asm.GetType( "Reclaimer.Blam.Halo3.ScenarioTag", throwOnError: true );
		var readMetadata = scenarioTag.GetType().GetMethod( "ReadMetadata" ).MakeGenericMethod( scenarioType );
		dynamic scenario = readMetadata.Invoke( scenarioTag, null );

		var scenarioProps = scenarioType.GetProperties( BindingFlags.Public | BindingFlags.Instance );
		foreach ( var p in scenarioProps )
			Log.Info( $"[HaloMount]   ScenarioTag prop: {p.PropertyType.Name} {p.Name}" );
	}

	[ConCmd( "halomount_inspect_mounthost" )]
	public static void InspectMountHost()
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
		var mountHostType = typeof( Sandbox.Mounting.BaseGameMount ).Assembly.GetType( "Sandbox.Mounting.MountHost" );

		foreach ( var f in mountHostType.GetFields( flags ) )
			Log.Info( $"[HaloMount] field: {f.FieldType} {f.Name} (static={f.IsStatic})" );
		foreach ( var p in mountHostType.GetProperties( flags ) )
			Log.Info( $"[HaloMount] prop: {p.PropertyType} {p.Name} (static={p.GetMethod?.IsStatic})" );
		foreach ( var m in mountHostType.GetMethods( flags ) )
			Log.Info( $"[HaloMount] method: {m.Name} (static={m.IsStatic})" );

		// Also check BaseGameMount itself for a Host/MountHost-typed member -- mounts likely
		// hold a reference to whatever hosts them.
		var baseGameMountType = typeof( Sandbox.Mounting.BaseGameMount );
		foreach ( var f in baseGameMountType.GetFields( flags ) )
			Log.Info( $"[HaloMount] BaseGameMount field: {f.FieldType} {f.Name}" );
		foreach ( var p in baseGameMountType.GetProperties( flags ) )
			Log.Info( $"[HaloMount] BaseGameMount prop: {p.PropertyType} {p.Name}" );
	}

	// Reproduces a resource load directly, outside the engine's resource pipeline (which
	// swallows the real exception down to a generic "Exception when loading" with no message
	// or stack trace -- confirmed on characters/civilian_fem.vmdl, which returns null with
	// nothing useful in the console). Also dumps per-mesh BoneIndex/HasBlendIndices/
	// HasBlendWeights so we can tell rigid-bone meshes (handled) apart from smooth-skinned
	// ones (not handled yet -- likely why bipeds render exploded).
	[ConCmd( "halomount_diag_load" )]
	public static void DiagnoseLoad( string displayPath )
	{
		var entry = HaloMCCMount.FindDiscoveryEntry( displayPath );
		if ( entry is null )
		{
			Log.Error( $"[HaloMount] No cached discovery entry for '{displayPath}' -- has the mount scanned yet (halomount_remount)?" );
			return;
		}

		var (mapPath, tagName) = entry.Value;
		Log.Info( $"[HaloMount] Reproducing load: map={mapPath} tag={tagName}" );

		try
		{
			var asm = LoadReclaimer();
			var cacheFactory = asm.GetType( "Reclaimer.Blam.Common.CacheFactory", throwOnError: true );

			dynamic cache = cacheFactory.InvokeMember(
				"ReadCacheFile",
				BindingFlags.InvokeMethod | BindingFlags.Static | BindingFlags.Public,
				null, null, new object[] { mapPath } );

			object tagItem = null;
			foreach ( dynamic tag in cache.TagIndex )
			{
				string classCode = tag.ClassCode;
				string name = tag.TagName;
				if ( classCode == "mode" && name == tagName )
				{
					tagItem = tag;
					break;
				}
			}

			if ( tagItem is null )
			{
				Log.Error( $"[HaloMount] No render_model tag found for '{tagName}'" );
				return;
			}

			var renderModelType = asm.GetType( "Reclaimer.Blam.Halo3.RenderModelTag", throwOnError: true );
			var readMetadata = tagItem.GetType().GetMethod( "ReadMetadata" ).MakeGenericMethod( renderModelType );
			dynamic renderModelTag = readMetadata.Invoke( tagItem, null );

			dynamic scene = renderModelTag.GetContent();

			dynamic reclaimerModel = null;
			foreach ( dynamic model in scene.EnumerateModels() ) { reclaimerModel = model; break; }

			if ( reclaimerModel is null )
			{
				Log.Error( "[HaloMount] GetContent() produced no models." );
				return;
			}

			Log.Info( $"[HaloMount] Model: {reclaimerModel.Bones.Count} bones, {reclaimerModel.Meshes.Count} meshes" );

			foreach ( dynamic region in reclaimerModel.Regions )
			{
				foreach ( dynamic permutation in region.Permutations )
				{
					dynamic range = permutation.MeshRange;
					Log.Info( $"[HaloMount]   region '{region.Name}' permutation '{permutation.Name}' -> meshes [{(int)range.Item1}, {(int)range.Item1 + (int)range.Item2})" );
				}
			}

			var meshIndex = 0;
			foreach ( dynamic mesh in reclaimerModel.Meshes )
			{
				meshIndex++;
				if ( mesh is null )
				{
					Log.Info( $"[HaloMount]   mesh {meshIndex}: null" );
					continue;
				}

				object boneIndexObj = mesh.BoneIndex;
				dynamic vb = mesh.VertexBuffer;
				bool hasBlendIndices = vb is not null && (bool)vb.HasBlendIndices;
				bool hasBlendWeights = vb is not null && (bool)vb.HasBlendWeights;
				int vertexCount = vb is null ? 0 : (int)vb.Count;

				Log.Info( $"[HaloMount]   mesh {meshIndex}: BoneIndex={boneIndexObj}, HasBlendIndices={hasBlendIndices}, "
					+ $"HasBlendWeights={hasBlendWeights}, VertexCount={vertexCount}" );

				try
				{
					var materialIndex = 0;
					HaloMeshConverter.ConvertMesh( reclaimerModel, mesh, ref materialIndex );
				}
				catch ( Exception meshEx )
				{
					Log.Error( $"[HaloMount]   mesh {meshIndex} FAILED TO CONVERT: {meshEx}" );
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Error( $"[HaloMount] Diagnostic failed: {ex}" );
		}
	}
}
