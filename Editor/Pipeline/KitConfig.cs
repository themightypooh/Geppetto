using Editor;
using System;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Where the Blender kit lives and which Blender runs it. Shared by every tool in the
/// <c>kit</c> and <c>kit_view</c> toolsets.
/// </summary>
/// <remarks>
/// <para>
/// Both values are stored as editor cookies rather than constants, so a machine with Blender
/// somewhere else -- or a kit that moves -- is a <c>kit_paths</c> call away from working
/// instead of a recompile. The defaults are what this machine actually has, so in practice
/// nobody ever sets them.
/// </para>
/// <para>
/// The kit is deliberately OUTSIDE the project (Documents\sbox_maps\rp_kit). It generates for
/// s&amp;box but it isn't an s&amp;box asset, and having the editor index a folder full of .py
/// and .blend files helps nobody.
/// </para>
/// </remarks>
public static class KitConfig
{
	private const string BlenderCookie = "Marionette.Kit.BlenderExe";
	private const string KitCookie = "Marionette.Kit.Directory";

	private const string DefaultKitDir = @"C:\Users\po\Documents\sbox_maps\rp_kit";

	/// <summary>Absolute path to blender.exe. Auto-detected on first use.</summary>
	public static string BlenderExe
	{
		get
		{
			var stored = EditorCookie.Get( BlenderCookie, "" );
			if ( !string.IsNullOrWhiteSpace( stored ) && File.Exists( stored ) )
				return stored;

			var found = FindBlender();
			if ( !string.IsNullOrWhiteSpace( found ) )
				EditorCookie.Set( BlenderCookie, found );

			return found;
		}
		set => EditorCookie.Set( BlenderCookie, value ?? "" );
	}

	/// <summary>Absolute path to the rp_kit generator folder (build_city.py, deploy.py, ...).</summary>
	public static string KitDir
	{
		get
		{
			var stored = EditorCookie.Get( KitCookie, "" );
			return !string.IsNullOrWhiteSpace( stored ) && Directory.Exists( stored )
				? stored
				: DefaultKitDir;
		}
		set => EditorCookie.Set( KitCookie, value ?? "" );
	}

	/// <summary>
	/// Newest Blender under the standard install root. Sorted by version string descending so a
	/// machine with 4.2 and 5.1 side by side picks 5.1 -- the kit's scripts target the newer API.
	/// </summary>
	private static string FindBlender()
	{
		var roots = new[]
		{
			@"C:\Program Files\Blender Foundation",
			@"C:\Program Files (x86)\Steam\steamapps\common\Blender",
		};

		foreach ( var root in roots )
		{
			if ( !Directory.Exists( root ) ) continue;

			var hit = Directory.EnumerateFiles( root, "blender.exe", SearchOption.AllDirectories )
				.OrderByDescending( p => p, StringComparer.OrdinalIgnoreCase )
				.FirstOrDefault();

			if ( hit is not null ) return hit;
		}

		return "";
	}

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
	/// Resolve a project-relative folder ("models/rp_city") to an absolute path, refusing anything
	/// that escapes the Assets tree. A generator bug that passes "../../.." should not hand the
	/// whole drive to a tool that deletes or recompiles.
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
}
