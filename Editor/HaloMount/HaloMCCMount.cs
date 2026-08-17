using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

	protected override Task Mount( MountContext context )
	{
		try
		{
			var asm = LoadReclaimer();
			var cacheFactory = asm.GetType( "Reclaimer.Blam.Common.CacheFactory", throwOnError: true );

			var mapsToScan = halo3Maps.Where( IsScannable )
				.Concat( OdstMissionMaps.Select( name => System.IO.Path.Combine( OdstMapsDir, $"{name}.map" ) ) );

			var registeredNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			var weaponCount = 0;
			var bspCount = 0;

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
				}

				foreach ( var tagName in weaponTagNames )
				{
					if ( !renderModelNames.Contains( tagName ) )
						continue;

					var shortName = tagName.Split( '\\' ).Last();
					if ( !registeredNames.Add( shortName ) )
						continue;

					context.Add( ResourceType.Model, $"weapons/{shortName}.vmdl", new HaloRenderModelLoader( this, mapPath, tagName ) );
					weaponCount++;
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

					context.Add( ResourceType.Model, $"maps/{mapShortName}.vmdl", new HaloBspLoader( this, mapPath, tagName ) );
					bspCount++;
				}
			}

			Log.Info( $"[HaloMount] Registered {weaponCount} weapon models, {bspCount} level BSPs." );

			IsMounted = true;
		}
		catch ( Exception ex )
		{
			Log.Error( $"[HaloMount] Mount failed: {ex}" );
		}

		return Task.CompletedTask;
	}
}
