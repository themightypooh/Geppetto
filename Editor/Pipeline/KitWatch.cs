using Editor;
using Editor.Mcp;
using Sandbox;
using System;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Watches the kit's FBX output folder and pulls changes into the project automatically:
/// deploy, register, compile. Save in Blender, and the editor just has it.
/// </summary>
/// <remarks>
/// <para>
/// This is <c>kit_build</c> with the Blender stage switched off, fired by a file timestamp
/// instead of a tool call. It exists for the iteration where you're re-running a generator by
/// hand and only want the s&amp;box half to keep up.
/// </para>
/// <para>
/// DEBOUNCED, because a build writes 60+ FBX files over several seconds and each one bumps the
/// folder's newest timestamp. Deploying on the first write would copy a half-finished set, so
/// the watcher waits for the writes to STOP for <see cref="QuietSeconds"/> before doing
/// anything.
/// </para>
/// <para>
/// DEFAULTS OFF. It runs an external python process and recompiles assets; that shouldn't start
/// happening to somebody who just opened the project. Turn it on from Editor &gt; Marionette &gt;
/// Watch Kit Output, or with the <c>kit_watch</c> tool.
/// </para>
/// </remarks>
[McpToolset( "kit_watch", "Auto-deploy the kit into the project whenever Blender writes new FBX" )]
public static class KitWatch
{
	private const string EnabledCookie = "Marionette.Kit.Watch";
	private const string OutCookie = "Marionette.Kit.WatchOut";
	private const string SubCookie = "Marionette.Kit.WatchSub";

	private const float PollSeconds = 1.0f;

	/// <summary>How long the output folder has to stay unchanged before a deploy fires.</summary>
	private const float QuietSeconds = 2.0f;

	/// <summary>A deploy that hasn't finished in this long is assumed dead, so the watcher recovers.</summary>
	private const float StuckSeconds = 300f;

	public static bool Enabled
	{
		get => EditorCookie.Get( EnabledCookie, false );
		set => EditorCookie.Set( EnabledCookie, value );
	}

	/// <summary>Generator output folder inside the kit, e.g. "out_city".</summary>
	public static string OutDir
	{
		get => EditorCookie.Get( OutCookie, "out_city" );
		set => EditorCookie.Set( OutCookie, value );
	}

	/// <summary>Model subfolder to deploy into, e.g. "rp_city".</summary>
	public static string Subfolder
	{
		get => EditorCookie.Get( SubCookie, "rp_city" );
		set => EditorCookie.Set( SubCookie, value );
	}

	// Newest write time already dealt with. Seeded on the first poll rather than treated as a
	// change, so enabling the watcher doesn't immediately deploy whatever is already sitting there.
	private static DateTime _seen = DateTime.MinValue;
	private static bool _seeded;

	// When the folder last changed, for the quiet-period check.
	private static DateTime _lastChange = DateTime.MinValue;

	private static bool _running;
	private static RealTimeSince _sinceStarted;
	private static RealTimeSince _sincePoll;

	[EditorEvent.Frame]
	public static void Poll()
	{
		if ( !Enabled ) return;

		// Statics survive hotloads and play-stop, so a flag left true by a reload would disable the
		// watcher permanently. Time-bound it instead of trusting it.
		if ( _running && _sinceStarted > StuckSeconds )
		{
			Log.Warning( "[KitWatch] previous deploy never reported back - resuming." );
			_running = false;
		}

		if ( _running ) return;
		if ( _sincePoll < PollSeconds ) return;

		_sincePoll = 0f;

		var fbxDir = Path.Combine( KitConfig.KitDir, OutDir, "fbx" );
		if ( !Directory.Exists( fbxDir ) ) return;

		DateTime newest;

		try
		{
			var files = Directory.EnumerateFiles( fbxDir, "*.fbx" ).ToArray();
			if ( files.Length == 0 ) return;

			newest = files.Max( File.GetLastWriteTimeUtc );
		}
		catch ( IOException )
		{
			// Mid-write. Try again next poll.
			return;
		}

		if ( !_seeded )
		{
			_seen = newest;
			_seeded = true;
			return;
		}

		if ( newest > _seen )
		{
			_seen = newest;
			_lastChange = DateTime.UtcNow;
			return;
		}

		// Nothing new. Fire once the writes have been quiet long enough.
		if ( _lastChange == DateTime.MinValue ) return;
		if ( (DateTime.UtcNow - _lastChange).TotalSeconds < QuietSeconds ) return;

		_lastChange = DateTime.MinValue;
		Deploy();
	}

	private static async void Deploy()
	{
		_running = true;
		_sinceStarted = 0f;

		try
		{
			Log.Info( $"[KitWatch] {OutDir} changed - deploying to models/{Subfolder}" );

			var result = await KitPipelineTools.Build(
				outDir: OutDir,
				subfolder: Subfolder,
				blender: false );

			Log.Info( $"[KitWatch] {(result.Success ? "OK" : "FAILED")} - " +
				$"{result.Registered} registered, {result.Compiled} compiled in {result.Seconds:0.#}s" );

			if ( !result.Success )
			{
				foreach ( var stage in result.Stages.Where( s => !s.Ok ) )
					Log.Warning( $"[KitWatch] {stage.Name}: {stage.Summary}\n{string.Join( "\n", stage.Errors ?? Array.Empty<string>() )}" );
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[KitWatch] deploy failed: {e.Message}" );
		}
		finally
		{
			_running = false;
		}
	}

	/// <summary>
	/// Turn the kit output watcher on or off, and see what it's watching. While on, re-exporting
	/// from Blender deploys, registers and compiles into the project by itself.
	/// </summary>
	/// <param name="enabled">-1 leaves it as is, 0 turns it off, 1 turns it on.</param>
	/// <param name="outDir">Generator output folder inside the kit, e.g. "out_city". Empty leaves it alone.</param>
	/// <param name="subfolder">Model subfolder to deploy into, e.g. "rp_city". Empty leaves it alone.</param>
	[McpTool( "kit_watch" )]
	public static object Watch( int enabled = -1, string outDir = "", string subfolder = "" )
	{
		if ( !string.IsNullOrWhiteSpace( outDir ) ) OutDir = outDir;
		if ( !string.IsNullOrWhiteSpace( subfolder ) ) Subfolder = subfolder;

		if ( enabled >= 0 )
		{
			Enabled = enabled > 0;

			// Re-seed so flipping it on doesn't count everything already on disk as a change.
			_seeded = false;
			_lastChange = DateTime.MinValue;

			Log.Info( $"[KitWatch] {(Enabled ? "ENABLED" : "DISABLED")}" );
		}

		var watching = Path.Combine( KitConfig.KitDir, OutDir, "fbx" );

		return new
		{
			Enabled,
			Watching = watching,
			Exists = Directory.Exists( watching ),
			DeployingTo = $"models/{Subfolder}",
			QuietSeconds,
			Note = "Deploy fires once writes have been quiet, so a part-written build is never copied.",
		};
	}

	[Menu( "Editor", "Marionette/Watch Kit Output", "sync" )]
	public static void ToggleMenu() => Watch( Enabled ? 0 : 1 );
}
