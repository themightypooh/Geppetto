using Editor;
using Editor.Mcp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Registers asset files created OUTSIDE the editor so the asset system can see them.
/// </summary>
/// <remarks>
/// <para>
/// THE PROBLEM THIS SOLVES. This project's world is generated: Blender writes .fbx, deploy.py
/// writes .vmdl and .vmat. Files written that way are invisible to the editor until something
/// registers them -- <c>asset_search</c> returns nothing, <c>asset_compile</c> fails with "No
/// asset at ...", and there is no console command or built-in MCP tool that forces a rescan.
/// The only way in is <see cref="AssetSystem.RegisterFile"/>, which is C#-only, which is why
/// this file exists.
/// </para>
/// <para>
/// Ported from midnight_am, where it was written after a generator run produced 67 .fbx files
/// that were visible and the .vmdl/.vmat written moments later that were not, and stayed
/// invisible indefinitely. It is not a timing problem that waiting solves.
/// </para>
/// <para>
/// SAFE TO RE-RUN. Registering a file that is already known is a no-op, so the sensible usage
/// is to point it at a whole folder after a generator run and not think about which files are
/// new. <c>kit_build</c> calls it for you.
/// </para>
/// </remarks>
[McpToolset( "external", "Register asset files created outside the editor by generator scripts" )]
public static class ExternalAssetTools
{
	// Only the source types this project's generators actually write. Deliberately NOT a
	// blanket "every file": the assets folder also holds .py, .json, .md and Blender temp
	// files, and handing those to the asset system is noise at best.
	private static readonly string[] Extensions =
	{
		".vmdl", ".vmat", ".fbx", ".png", ".tga", ".prefab", ".scene",
		".sound", ".sndevt", ".shader", ".wav", ".mp3",
	};

	/// <summary>
	/// Register every asset file under a folder, so files written by an external script become
	/// visible to asset_search and asset_compile.
	/// </summary>
	/// <param name="folder">Folder relative to the project's Assets directory, e.g. "models/rp_city". Empty does the whole Assets tree.</param>
	/// <param name="recursive">Include subfolders.</param>
	[McpTool( "register_external_assets" )]
	public static object RegisterExternalAssets( string folder = "", bool recursive = true )
	{
		string target;
		try
		{
			target = KitConfig.ResolveAssetFolder( folder );
		}
		catch ( Exception e )
		{
			return new { Success = false, Error = e.Message };
		}

		if ( !Directory.Exists( target ) )
			return new { Success = false, Error = $"No such folder: {target}" };

		var result = Register( target, recursive );

		Log.Info( $"[ExternalAssets] {result.Registered} registered, {result.AlreadyKnown} already known, " +
			$"{result.Failed} failed, under '{(string.IsNullOrWhiteSpace( folder ) ? "Assets" : folder)}'" );

		return result;
	}

	/// <summary>
	/// The registration itself, callable from other tools (kit_build, the watcher) without going
	/// back through the MCP layer.
	/// </summary>
	public static RegisterResult Register( string absoluteFolder, bool recursive = true )
	{
		var root = KitConfig.AssetsRoot();
		var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

		var registered = new List<string>();
		var alreadyKnown = 0;
		var failed = new List<string>();

		foreach ( var file in Directory.EnumerateFiles( absoluteFolder, "*.*", option ) )
		{
			var ext = Path.GetExtension( file );
			if ( !Extensions.Contains( ext, StringComparer.OrdinalIgnoreCase ) )
				continue;

			// Compiled output and Blender's own leftovers are not source assets.
			if ( file.Contains( ".generated.", StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( file.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) ) continue;

			try
			{
				var relative = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
				if ( AssetSystem.FindByPath( relative ) is not null )
				{
					alreadyKnown++;
					continue;
				}

				var asset = AssetSystem.RegisterFile( file );
				if ( asset is not null )
					registered.Add( asset.Path );
				else
					failed.Add( relative );
			}
			catch ( Exception e )
			{
				failed.Add( $"{Path.GetFileName( file )}: {e.Message}" );
			}
		}

		return new RegisterResult
		{
			Success = failed.Count == 0,
			Folder = absoluteFolder,
			Registered = registered.Count,
			AlreadyKnown = alreadyKnown,
			Failed = failed.Count,
			// Capped: registering a whole tree can return hundreds and the caller only ever
			// needs to see enough to confirm it did the right thing.
			Sample = registered.Take( 25 ).ToArray(),
			Errors = failed.Take( 10 ).ToArray(),
		};
	}

	/// <summary>What a registration pass found.</summary>
	public class RegisterResult
	{
		public bool Success { get; set; }
		public string Folder { get; set; }
		public int Registered { get; set; }
		public int AlreadyKnown { get; set; }
		public int Failed { get; set; }
		public string[] Sample { get; set; }
		public string[] Errors { get; set; }
	}

	[Menu( "Editor", "Marionette/Register External Assets", "cloud_sync" )]
	public static void RegisterMenu()
	{
		var result = RegisterExternalAssets();
		Log.Info( $"[ExternalAssets] {result}" );
	}
}
