using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Mounting;

// Phase 2 (see plans/tidy-singing-backus.md): detection-only mount so Halo MCC shows
// up in the Asset Browser's Mounts panel next to the official Quake/HL2/etc mounts.
// No resources registered yet -- Mount() just proves it can open every Halo 3
// campaign map via Reclaimer.Blam (loaded per HaloMountSpike's Assembly.LoadFrom +
// dynamic workaround, since tool projects can't take a compile-time DLL reference --
// see Facepunch/sbox-public#6826).
//
// Editor-only, deliberately -- Reclaimer.Blam is GPL-3.0 and must never end up in
// Code/ or ship with the published game.
public class HaloMCCMount : BaseGameMount
{
	public override string Ident => "halomcc";
	public override string Title => "Halo: The Master Chief Collection";

	const long AppId = 976730;
	public override long? SteamAppId => AppId;

	const string LibrariesDir = @"C:\Users\po\Documents\s&box projects\marionette\Editor\HaloMount\Libraries";

	// Same "campaign.map/shared.map throw on TagIndex read for this MCC ODST install"
	// limitation found during Phase 3 spiking -- scan only the per-mission maps.
	const string OdstMapsDir = @"C:\Program Files (x86)\Steam\steamapps\common\Halo The Master Chief Collection\halo3odst\maps";
	static readonly string[] OdstMissionMaps =
	[
		"L200", "c100", "c200", "h100", "l300",
		"sc100", "sc110", "sc120", "sc130", "sc140", "sc150"
	];

	static Assembly reclaimerBlam;

	internal static Assembly LoadReclaimer()
	{
		if ( reclaimerBlam is not null )
			return reclaimerBlam;

		Assembly.LoadFrom( Path.Combine( LibrariesDir, "System.Drawing.Common.dll" ) );
		Assembly.LoadFrom( Path.Combine( LibrariesDir, "Reclaimer.Core.dll" ) );
		reclaimerBlam = Assembly.LoadFrom( Path.Combine( LibrariesDir, "Reclaimer.Blam.dll" ) );

		return reclaimerBlam;
	}

	// Lets HaloMountSpike's remount command find this instance's private `_host` field via
	// reflection to force a remount after editing conversion code -- see Remount() there.
	public static HaloMCCMount ActiveInstance { get; private set; }

	List<string> halo3Maps = new();

	protected override void Initialize( InitializeContext context )
	{
		ActiveInstance = this;

		if ( !context.IsAppInstalled( AppId ) )
			return;

		var gameDir = context.GetAppDirectory( AppId );
		if ( gameDir is null )
			return;

		var mapsDir = Path.Combine( gameDir, "halo3", "maps" );
		if ( System.IO.Directory.Exists( mapsDir ) )
			halo3Maps = System.IO.Directory.GetFiles( mapsDir, "*.map" ).ToList();

		IsInstalled = halo3Maps.Count > 0;
	}

	// campaign.map/shared.map throw on TagIndex read for this MCC install (confirmed during
	// Phase 3 spiking) -- everything else parses fine.
	static bool IsScannable( string mapPath )
	{
		var name = System.IO.Path.GetFileName( mapPath );
		return !name.Equals( "campaign.map", StringComparison.OrdinalIgnoreCase )
			&& !name.Equals( "shared.map", StringComparison.OrdinalIgnoreCase );
	}

	// MountContext turned out to be a ref-struct-like type -- the compiler refuses to let it
	// cross into an async method OR be captured by any lambda/closure at all ("Cannot use
	// parameter 'context' that has ref-like type inside an anonymous method..."), so
	// Task.Run/ContinueWith backgrounding is a dead end; Mount() has to stay fully synchronous.
	// The actual fix for the hang: cache the expensive part (opening and tag-scanning ~40 map
	// files) at the process level, so only the FIRST mount in an editor session pays that cost
	// -- every remount after that (which happens constantly while iterating on conversion code
	// via halomount_remount) reuses the cached scan and is near-instant. Cache raw discovery
	// data, not ResourceLoader instances, so loaders always get built against the CURRENT mount
	// instance (`this`) rather than a stale one from a previous Mount() call.
	static List<(string kind, string displayPath, string mapPath, string tagName)> discoveryCache;

	// For HaloMountSpike's diagnostics -- look up the (mapPath, tagName) behind a registered
	// display path (e.g. "characters/civilian_fem.vmdl") so a failing load can be reproduced
	// directly, bypassing the engine's resource-loading pipeline which swallows the real
	// exception detail down to a generic "Exception when loading" with no message/stack.
	public static (string mapPath, string tagName)? FindDiscoveryEntry( string displayPath )
	{
		if ( discoveryCache is null )
			return null;

		foreach ( var entry in discoveryCache )
		{
			if ( entry.displayPath == displayPath )
				return (entry.mapPath, entry.tagName);
		}

		return null;
	}

	List<(ResourceType type, string path, ResourceLoader loader)> DiscoverResources()
	{
		discoveryCache ??= ScanAllMaps();

		var found = new List<(ResourceType, string, ResourceLoader)>();
		foreach ( var (kind, displayPath, mapPath, tagName) in discoveryCache )
		{
			ResourceLoader loader = kind == "bsp"
				? new HaloBspLoader( this, mapPath, tagName )
				: new HaloRenderModelLoader( this, mapPath, tagName );

			found.Add( (ResourceType.Model, displayPath, loader) );
		}

		return found;
	}

	List<(string kind, string displayPath, string mapPath, string tagName)> ScanAllMaps()
	{
		var found = new List<(string, string, string, string)>();

		var asm = LoadReclaimer();
		var cacheFactory = asm.GetType( "Reclaimer.Blam.Common.CacheFactory", throwOnError: true );

		var mapsToScan = halo3Maps.Where( IsScannable )
			.Concat( OdstMissionMaps.Select( name => System.IO.Path.Combine( OdstMapsDir, $"{name}.map" ) ) );

		var registeredNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var weaponCount = 0;
		var bspCount = 0;
		var bipedCount = 0;

		foreach ( var mapPath in mapsToScan )
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

			// Collect render_model ("mode") tag names present in this map first, so each
			// weapon ("weap") tag can be matched against one by name -- Halo3 objects keep
			// every tag for the same thing (weapon, render_model, physics, etc) under one
			// shared path, e.g. objects\weapons\pistol\needler\needler for both.
			var renderModelNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			var weaponTagNames = new List<string>();
			var bspTagNames = new List<string>();
			var bipedTagNames = new List<string>();

			foreach ( dynamic tag in cache.TagIndex )
			{
				string classCode = tag.ClassCode;
				string tagName = tag.TagName;
				if ( classCode == "mode" )
					renderModelNames.Add( tagName );
				else if ( classCode == "weap" )
					weaponTagNames.Add( tagName );
				else if ( classCode == "sbsp" )
					bspTagNames.Add( tagName );
				else if ( classCode == "bipd" )
					bipedTagNames.Add( tagName );
			}

			foreach ( var tagName in weaponTagNames )
			{
				if ( !renderModelNames.Contains( tagName ) )
					continue;

				var shortName = tagName.Split( '\\' ).Last();
				if ( !registeredNames.Add( shortName ) )
					continue;

				found.Add( ("weapon", $"weapons/{shortName}.vmdl", mapPath, tagName) );
				weaponCount++;
			}

			// Bipeds (Grunts, Elites, Spartans, etc) -- same name-matching trick as weapons,
			// same loader too, since RenderModelTag doesn't care what kind of object
			// references it. This is what actually carries the skeleton (HaloMeshConverter.
			// BuildSkeleton) that makes these posable, unlike weapons which only have 0-1
			// bones.
			foreach ( var tagName in bipedTagNames )
			{
				if ( !renderModelNames.Contains( tagName ) )
					continue;

				var shortName = tagName.Split( '\\' ).Last();
				var registerKey = $"biped:{shortName}";
				if ( !registeredNames.Add( registerKey ) )
					continue;

				found.Add( ("biped", $"characters/{shortName}.vmdl", mapPath, tagName) );
				bipedCount++;
			}

			// Multiplayer maps are almost always one scnr + one sbsp sharing a name like
			// levels\multi\guardian\guardian -- the map filename (guardian.map) is a cleaner
			// display name than that path's last segment, which is identical to the second-
			// to-last for these.
			foreach ( var tagName in bspTagNames )
			{
				var mapShortName = System.IO.Path.GetFileNameWithoutExtension( mapPath );
				var registerKey = $"bsp:{mapShortName}:{tagName}";
				if ( !registeredNames.Add( registerKey ) )
					continue;

				found.Add( ("bsp", $"maps/{mapShortName}.vmdl", mapPath, tagName) );
				bspCount++;
			}
		}

		Log.Info( $"[HaloMount] Scanned: {weaponCount} weapon models, {bspCount} level BSPs, {bipedCount} bipeds." );

		return found;
	}

	protected override Task Mount( MountContext context )
	{
		try
		{
			var toRegister = DiscoverResources();

			foreach ( var (type, path, loader) in toRegister )
				context.Add( type, path, loader );

			Log.Info( $"[HaloMount] Registered {toRegister.Count} resources." );

			IsMounted = true;
		}
		catch ( Exception ex )
		{
			Log.Error( $"[HaloMount] Mount failed: {ex}" );
		}

		return Task.CompletedTask;
	}
}
