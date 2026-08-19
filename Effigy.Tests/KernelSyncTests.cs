using System;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Guards the one piece of this repo that duplication is unavoidable in.
///
/// s&box compiles `Code/` into the game assembly and `Editor/` into the editor assembly, and
/// nothing else - a top-level `Effigy/` is invisible to it. The kernel cannot live in `Code/`
/// either, because ObjWriter and SmdWriter call File.WriteAllText and the game assembly's sandbox
/// whitelist does not allow it. So the editor assembly needs its own copy of the kernel, and
/// `Editor/Effigy/` is that copy.
///
/// `Effigy/` is canonical: it is what this test project compiles, and keeping it out of any engine
/// folder is what keeps the Godot option open (MODELING-HANDOFF-GODOT.md).
///
/// The failure mode this exists to catch is silent: edit the canonical kernel, forget to mirror,
/// and the tests keep passing against source the editor is not running. That already happened once
/// - the mirror was committed with a stray blank line in SolidFeatures.cs, which is a diff of no
/// consequence and proof that nothing was checking. Run tools/sync-kernel.sh to fix a failure here.
/// </summary>
public static class KernelSyncTests
{
	public static void Run()
	{
		Report.Section( "Editor/Effigy mirrors the canonical kernel" );

		if ( FindRepoRoot() is not { } root )
		{
			// Not a failure: the runner is allowed to be pointed at the kernel from somewhere with
			// no repo around it. Saying so beats a green tick that checked nothing.
			Report.Check( "repo root located", true, "skipped - no repo layout around the runner" );
			return;
		}

		var src = Path.Combine( root, "Effigy" );
		var dst = Path.Combine( root, "Editor", "Effigy" );

		if ( !Directory.Exists( dst ) )
		{
			Report.Check( "Editor/Effigy exists", false, $"{dst} is missing - run tools/sync-kernel.sh" );
			return;
		}

		var srcFiles = RelativeCsFiles( src );
		var dstFiles = RelativeCsFiles( dst );

		var missing = srcFiles.Except( dstFiles ).ToList();
		var extra = dstFiles.Except( srcFiles ).ToList();

		Report.Check( "no kernel file missing from the mirror", missing.Count == 0, string.Join( ", ", missing ) );
		Report.Check( "no stale file left in the mirror", extra.Count == 0, string.Join( ", ", extra ) );

		// Byte-for-byte rather than token-for-token. A whitespace-only difference is harmless in
		// itself and is exactly the signal that the two are being maintained by hand.
		foreach ( var file in srcFiles.Intersect( dstFiles ).OrderBy( f => f, StringComparer.Ordinal ) )
		{
			var same = File.ReadAllBytes( Path.Combine( src, file ) )
				.SequenceEqual( File.ReadAllBytes( Path.Combine( dst, file ) ) );

			Report.Check( $"{file} matches", same, same ? null : "run tools/sync-kernel.sh" );
		}
	}

	static string[] RelativeCsFiles( string dir ) =>
		Directory.EnumerateFiles( dir, "*.cs", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( dir, f ).Replace( '\\', '/' ) )
			.OrderBy( f => f, StringComparer.Ordinal )
			.ToArray();

	/// <summary>Walk up from the running binary looking for the layout, so the check works whether
	/// the runner was started from the repo root or from Effigy.Tests.</summary>
	static string FindRepoRoot()
	{
		var dir = new DirectoryInfo( AppContext.BaseDirectory );

		while ( dir is not null )
		{
			if ( Directory.Exists( Path.Combine( dir.FullName, "Effigy" ) )
				&& Directory.Exists( Path.Combine( dir.FullName, "Effigy.Tests" ) ) )
				return dir.FullName;

			dir = dir.Parent;
		}

		return null;
	}
}
