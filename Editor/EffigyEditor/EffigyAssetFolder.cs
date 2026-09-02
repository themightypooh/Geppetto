using Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Where Effigy writes its exports, and how it makes the editor notice them.
///
/// WHY THIS IS HERE RATHER THAN CALLED. Both halves used to live in this repo's private Blender
/// pipeline (Editor/Pipeline/KitConfig + ExternalAssetTools), which the library cannot reference:
/// a library is compiled on its own in whatever project mounts it, and the pipeline is personal
/// content that never ships. The two pieces Effigy actually needs are small and generic, so they
/// are restated here and the tool stands up in any project.
/// </summary>
internal static class EffigyAssetFolder
{
	// Only what Effigy itself writes. Deliberately not "every file": an Assets tree also holds
	// .py, .json and .md, and handing those to the asset system is noise at best.
	private static readonly string[] Extensions =
	{
		".vmdl", ".vmat", ".obj", ".dmx", ".smd", ".fbx", ".png", ".tga",
	};

	/// <summary>The active project's Assets folder, or null if there isn't one.</summary>
	public static string AssetsRoot()
	{
		var project = Sandbox.Project.Current;
		if ( project is null ) return null;

		var root = project.GetAssetsPath();
		return string.IsNullOrWhiteSpace( root ) || !Directory.Exists( root )
			? null
			: Path.GetFullPath( root );
	}

	/// <summary>
	/// Resolve a project-relative folder ("models/effigy") to an absolute path, refusing anything
	/// that escapes the Assets tree.
	/// </summary>
	public static string ResolveAssetFolder( string folder )
	{
		var root = AssetsRoot()
			?? throw new Exception( "Could not resolve the active project's Assets folder." );

		var target = string.IsNullOrWhiteSpace( folder )
			? root
			: Path.GetFullPath( Path.Combine( root, folder.Replace( '/', Path.DirectorySeparatorChar ) ) );

		if ( !target.StartsWith( root, StringComparison.OrdinalIgnoreCase ) )
			throw new Exception( $"'{folder}' resolves outside the Assets folder." );

		return target;
	}

	/// <summary>
	/// Register every asset file under a folder, so a file written with File.WriteAllText becomes
	/// visible to the asset system. Without this, asset_compile fails with "No asset at ..." and
	/// there is no console command that forces a rescan. Safe to re-run: an already-known file is
	/// counted and skipped.
	/// </summary>
	public static RegisterResult Register( string absoluteFolder, bool recursive = true )
	{
		var root = AssetsRoot();
		var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

		var registered = new List<string>();
		var alreadyKnown = 0;
		var failed = new List<string>();

		foreach ( var file in Directory.EnumerateFiles( absoluteFolder, "*.*", option ) )
		{
			var ext = Path.GetExtension( file );
			if ( !Extensions.Contains( ext, StringComparer.OrdinalIgnoreCase ) )
				continue;

			// Compiled output is not a source asset.
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
		public string[] Errors { get; set; }
	}
}
