using Editor;
using Editor.Mcp;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Marionette.EditorTools;

/// <summary>
/// The Blender -&gt; s&amp;box bridge: run the kit's generators, deploy them into the project,
/// register and compile the result, and check that what came out is actually usable.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS REPLACES. The loop used to be: run <c>blender -b --python build_city.py</c> in a
/// terminal, run <c>python deploy.py out_city rp_city</c>, come back to the editor, register the
/// files (they're invisible otherwise), compile them one at a time, then spawn one into a scene
/// and look at it to find out whether any of it worked. Four context switches and N compile calls
/// per iteration. <c>kit_build</c> is all of it in one call that reports only what broke.
/// </para>
/// <para>
/// THE FAILURES THIS IS SHAPED AROUND are the silent ones. Geometry that arrives 100x too big
/// blows past the physics coordinate limit and the compiler DISCARDS every physics mesh with a
/// quiet "ignoring mesh" -- the models still render perfectly and you walk through the whole
/// city. A material remap whose <c>from</c> name doesn't match the slot the FBX carries just
/// falls back to default.vmat. Neither shows up as an error anywhere. So the compile step here
/// is followed by <c>kit_validate</c>, which asks the compiled models directly.
/// </para>
/// </remarks>
[McpToolset( "kit", "Blender to s&box: build the kit, deploy it, register, compile, validate" )]
public static class KitPipelineTools
{
	/// <summary>
	/// Build the kit in Blender, deploy it into the project, register the new files and compile
	/// them. Every stage is optional, so this doubles as "just deploy what's already built".
	/// </summary>
	/// <param name="script">Generator to run in Blender, e.g. "build_city.py", "build_block.py", "build.py".</param>
	/// <param name="outDir">Folder the generator writes into, relative to the kit, e.g. "out_city".</param>
	/// <param name="subfolder">Model subfolder inside Assets/models to deploy into, e.g. "rp_city".</param>
	/// <param name="blender">Run the Blender generator. False starts from whatever is already in outDir.</param>
	/// <param name="deploy">Run deploy.py to copy FBX/materials in and author the .vmdl files.</param>
	/// <param name="register">Make the newly written files visible to the asset system.</param>
	/// <param name="compile">Compile the deployed .vmdl and .vmat assets.</param>
	/// <param name="timeoutSeconds">Give up on a stage that runs longer than this.</param>
	[McpTool( "kit_build" )]
	public static async Task<BuildResult> Build(
		string script = "build_city.py",
		string outDir = "out_city",
		string subfolder = "rp_city",
		bool blender = true,
		bool deploy = true,
		bool register = true,
		bool compile = true,
		[Sandbox.Range( 10, 3600 )] int timeoutSeconds = 900 )
	{
		var kit = KitConfig.KitDir;
		if ( !Directory.Exists( kit ) )
			throw new Exception( $"Kit folder not found: {kit}. Set it with kit_paths." );

		var result = new BuildResult { Kit = kit, Script = script, Subfolder = subfolder };
		var sw = Stopwatch.StartNew();

		// --- 1. Blender.
		if ( blender )
		{
			if ( !File.Exists( Path.Combine( kit, script ) ) )
				throw new Exception( $"No such generator: {Path.Combine( kit, script )}" );

			var exe = KitConfig.BlenderExe;
			if ( string.IsNullOrWhiteSpace( exe ) || !File.Exists( exe ) )
				throw new Exception( "blender.exe not found. Set it with kit_paths." );

			var proc = await Run( exe, $"-b --python \"{script}\"", kit, timeoutSeconds );
			result.Stages.Add( Stage( "blender", proc ) );

			// A Blender traceback still exits 0 sometimes, so the output is checked too.
			if ( !proc.Ok || proc.Output.Contains( "Traceback", StringComparison.Ordinal ) )
			{
				result.Note = "Blender failed - later stages skipped.";
				return Finish( result, sw );
			}
		}

		// --- 2. deploy.py: copies FBX + materials into the project and writes the .vmdl files.
		if ( deploy )
		{
			var proc = await Run( PythonExe, $"deploy.py {outDir} {subfolder}", kit, timeoutSeconds );
			result.Stages.Add( Stage( "deploy", proc ) );

			if ( !proc.Ok || proc.Output.Contains( "Traceback", StringComparison.Ordinal ) )
			{
				result.Note = "deploy.py failed - nothing was registered or compiled.";
				return Finish( result, sw );
			}
		}

		var modelFolder = $"models/{subfolder}";

		// --- 3. Register. Without this the editor cannot see a single one of those files.
		if ( register )
		{
			var models = ExternalAssetTools.Register( KitConfig.ResolveAssetFolder( modelFolder ) );
			var mats = ExternalAssetTools.Register( KitConfig.ResolveAssetFolder( "materials/rp_kit" ) );

			result.Registered = models.Registered + mats.Registered;
			result.Stages.Add( new StageResult
			{
				Name = "register",
				Ok = models.Success && mats.Success,
				Summary = $"{result.Registered} new, {models.AlreadyKnown + mats.AlreadyKnown} already known",
				Errors = models.Errors.Concat( mats.Errors ).Take( 10 ).ToArray(),
			} );
		}

		// --- 4. Compile.
		if ( compile )
		{
			var failed = new List<string>();
			int compiled = 0;

			foreach ( var asset in AssetsUnder( modelFolder ).Concat( AssetsUnder( "materials/rp_kit" ) ) )
			{
				try
				{
					asset.Compile( true );
					compiled++;

					if ( asset.IsCompileFailed )
						failed.Add( asset.Path );
				}
				catch ( Exception e )
				{
					failed.Add( $"{asset.Path}: {e.Message}" );
				}
			}

			result.Compiled = compiled;
			result.Stages.Add( new StageResult
			{
				Name = "compile",
				Ok = failed.Count == 0,
				Summary = $"{compiled} assets compiled, {failed.Count} failed",
				Errors = failed.Take( 15 ).ToArray(),
			} );
		}

		result.Note = "Compiled clean is NOT proof of collision or materials - run kit_validate.";
		return Finish( result, sw );
	}

	/// <summary>
	/// Run a python snippet inside Blender with the kit importable, and get the printed result
	/// back. For asking questions about geometry without a full rebuild-deploy-compile round trip:
	/// how big is this piece really, what materials does it carry, how many verts.
	/// </summary>
	/// <remarks>
	/// The snippet runs with the kit folder on sys.path and <c>bpy</c> imported, in an EMPTY scene
	/// unless you build or open something yourself. Print whatever you want back; everything
	/// between the markers this wraps around your code is returned, so Blender's own console noise
	/// is filtered out.
	/// </remarks>
	/// <param name="code">Python to run. Multi-line is fine. Use print() for anything you want returned.</param>
	/// <param name="setup">Optional kit module to import first, e.g. "kitlib" or "street".</param>
	/// <param name="timeoutSeconds">Give up after this long.</param>
	[McpTool( "blender_eval" )]
	public static async Task<EvalResult> BlenderEval(
		string code,
		string setup = "",
		[Sandbox.Range( 5, 600 )] int timeoutSeconds = 120 )
	{
		if ( string.IsNullOrWhiteSpace( code ) )
			throw new Exception( "Give some python to run." );

		var kit = KitConfig.KitDir;
		var exe = KitConfig.BlenderExe;

		if ( !Directory.Exists( kit ) ) throw new Exception( $"Kit folder not found: {kit}" );
		if ( string.IsNullOrWhiteSpace( exe ) || !File.Exists( exe ) )
			throw new Exception( "blender.exe not found. Set it with kit_paths." );

		const string begin = "<<<KIT_EVAL_BEGIN>>>";
		const string end = "<<<KIT_EVAL_END>>>";

		// Written into the kit folder so relative imports and paths behave exactly as they do for
		// the real generators. Deleted afterwards, and named so a leftover is obviously temporary.
		var temp = Path.Combine( kit, "_mcp_eval.py" );

		var sb = new StringBuilder();
		sb.AppendLine( "import os, sys, traceback" );
		sb.AppendLine( "import bpy" );
		sb.AppendLine( "HERE = os.path.dirname(os.path.abspath(__file__))" );
		sb.AppendLine( "if HERE not in sys.path: sys.path.insert(0, HERE)" );

		if ( !string.IsNullOrWhiteSpace( setup ) )
			sb.AppendLine( $"import {setup}" );

		sb.AppendLine( $"print('{begin}')" );
		sb.AppendLine( "try:" );

		// Indent the caller's code into the try block. Tabs would fight python's indentation
		// rules, so they're expanded first.
		foreach ( var line in code.Replace( "\r\n", "\n" ).Replace( "\t", "    " ).Split( '\n' ) )
			sb.AppendLine( "    " + line );

		sb.AppendLine( "except Exception:" );
		sb.AppendLine( "    traceback.print_exc()" );
		sb.AppendLine( $"print('{end}')" );

		try
		{
			File.WriteAllText( temp, sb.ToString() );

			var proc = await Run( exe, "-b --python \"_mcp_eval.py\"", kit, timeoutSeconds );

			var output = Between( proc.Output, begin, end );

			return new EvalResult
			{
				Ok = proc.Ok && !output.Contains( "Traceback", StringComparison.Ordinal ),
				Output = output,
				ExitCode = proc.ExitCode,
				Seconds = proc.Seconds,
				// Only worth reading when something went wrong before the markers were reached.
				RawTail = string.IsNullOrWhiteSpace( output ) ? Tail( proc.Output, 20 ) : null,
			};
		}
		finally
		{
			try { if ( File.Exists( temp ) ) File.Delete( temp ); } catch { /* leftover temp is harmless */ }
		}
	}

	/// <summary>
	/// Check the compiled models in a folder for the failures that don't announce themselves:
	/// missing collision, material slots that fell back to default, absurd bounds, models that
	/// failed to load at all.
	/// </summary>
	/// <param name="folder">Folder relative to Assets, e.g. "models/rp_city".</param>
	/// <param name="maxExtent">Flag any model whose bounds exceed this many inches on an axis. The physics coordinate limit is what kills oversized geometry.</param>
	/// <param name="onlyProblems">Return only the models with something wrong. False lists everything.</param>
	[McpTool( "kit_validate" )]
	public static ValidateResult Validate(
		string folder = "models/rp_city",
		float maxExtent = 16384f,
		bool onlyProblems = true )
	{
		var absolute = KitConfig.ResolveAssetFolder( folder );
		if ( !Directory.Exists( absolute ) )
			throw new Exception( $"No such folder: {absolute}" );

		var rows = new List<ModelReport>();

		foreach ( var asset in AssetsUnder( folder ).Where( a => a.Path.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) )
		{
			var row = new ModelReport { Model = asset.Path };
			var problems = new List<string>();

			if ( asset.IsCompileFailed )
				problems.Add( "compile failed" );

			var model = Model.Load( asset.Path );

			if ( model is null || model.IsError )
			{
				problems.Add( "model failed to load (error model)" );
				row.Problems = problems.ToArray();
				rows.Add( row );
				continue;
			}

			row.Meshes = model.MeshCount;

			var bounds = model.Bounds;
			row.Bounds = $"{bounds.Size.x:0.#} x {bounds.Size.y:0.#} x {bounds.Size.z:0.#}";

			if ( bounds.Size.Length <= 0.001f )
				problems.Add( "empty render bounds - no geometry" );
			else if ( bounds.Size.x > maxExtent || bounds.Size.y > maxExtent || bounds.Size.z > maxExtent )
				problems.Add( $"bounds exceed {maxExtent} inches - check import_scale, physics will be discarded" );

			// THE BIG ONE. PhysicsMeshFromRender silently produces nothing when the source geometry
			// is too large or degenerate, and the model still renders fine, so this is the only
			// place the failure is visible short of walking into it in game.
			var physics = model.PhysicsBounds;
			row.HasCollision = physics.Size.Length > 0.001f;

			if ( !row.HasCollision )
				problems.Add( "NO COLLISION - physics mesh was discarded or never authored" );
			else
			{
				row.PhysicsBounds = $"{physics.Size.x:0.#} x {physics.Size.y:0.#} x {physics.Size.z:0.#}";

				// Collision that doesn't match the render mesh is the other half of the same bug --
				// it compiled, but only part of it survived.
				var ratio = physics.Size.Length / MathF.Max( bounds.Size.Length, 0.001f );
				if ( ratio < 0.5f )
					problems.Add( $"collision covers only {ratio * 100f:0}% of the render bounds" );
			}

			// Material slots. A remap whose "from" doesn't match the slot name the FBX carries
			// falls through to default.vmat without complaint.
			try
			{
				var materials = model.Materials.ToArray();
				row.Materials = materials.Length;

				var defaults = materials
					.Where( m => m is not null && m.Name is not null
						&& m.Name.Contains( "default", StringComparison.OrdinalIgnoreCase ) )
					.Select( m => m.Name )
					.Distinct()
					.ToArray();

				if ( defaults.Length > 0 )
					problems.Add( $"{defaults.Length} slot(s) fell back to default material" );
			}
			catch ( Exception e )
			{
				problems.Add( $"material read failed: {e.Message}" );
			}

			// Surface property, read from the source .vmdl -- deploy.py guesses it by name prefix
			// and falls through to "concrete", so wood floors quietly sound like pavement.
			try
			{
				var source = File.ReadAllText( asset.AbsolutePath );
				var marker = "surface_prop = \"";
				var at = source.IndexOf( marker, StringComparison.Ordinal );
				if ( at >= 0 )
				{
					var from = at + marker.Length;
					var to = source.IndexOf( '"', from );
					if ( to > from ) row.SurfaceProp = source[from..to];
				}
			}
			catch { /* source read is a nicety, not a reason to fail the row */ }

			row.Problems = problems.ToArray();
			rows.Add( row );
		}

		var bad = rows.Where( r => r.Problems.Length > 0 ).ToList();

		Log.Info( $"[Kit] validate '{folder}': {rows.Count} models, {bad.Count} with problems" );

		return new ValidateResult
		{
			Folder = folder,
			Total = rows.Count,
			Ok = rows.Count - bad.Count,
			Problems = bad.Count,
			NoCollision = rows.Count( r => !r.HasCollision ),
			Models = (onlyProblems ? bad : rows).Take( 100 ).ToArray(),
		};
	}

	/// <summary>
	/// Force a model to re-read its source FBX and recompile. Re-exporting the FBX on its own does
	/// NOT do this -- the editor keeps serving the geometry it compiled the first time, which reads
	/// as "my Blender change did nothing" and sends you hunting in the wrong place.
	/// </summary>
	/// <param name="target">A model path ("models/rp_city/park_bench_0.vmdl") or a folder ("models/rp_city") to do all of them.</param>
	[McpTool( "model_reimport" )]
	public static ReimportResult Reimport( string target )
	{
		if ( string.IsNullOrWhiteSpace( target ) )
			throw new Exception( "Give a model path or a folder." );

		var assets = target.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase )
			? new[] { AssetSystem.FindByPath( target ) ?? throw new Exception( $"No asset at '{target}'" ) }.ToList()
			: AssetsUnder( target ).Where( a => a.Path.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ).ToList();

		var done = new List<string>();
		var failed = new List<string>();

		foreach ( var asset in assets )
		{
			try
			{
				// Touching the .vmdl is what makes the compiler consider its inputs stale; without
				// it a recompile is a no-op because the .vmdl itself hasn't changed, only the FBX
				// it points at.
				if ( File.Exists( asset.AbsolutePath ) )
					File.SetLastWriteTimeUtc( asset.AbsolutePath, DateTime.UtcNow );

				asset.Compile( true );

				if ( asset.IsCompileFailed ) failed.Add( asset.Path );
				else done.Add( asset.Path );
			}
			catch ( Exception e )
			{
				failed.Add( $"{asset.Path}: {e.Message}" );
			}
		}

		Log.Info( $"[Kit] reimport '{target}': {done.Count} recompiled, {failed.Count} failed" );

		return new ReimportResult
		{
			Success = failed.Count == 0,
			Recompiled = done.Count,
			Failed = failed.Count,
			Errors = failed.Take( 10 ).ToArray(),
			Note = "Re-read of the FBX is forced by touching the .vmdl - a plain recompile skips it.",
		};
	}

	/// <summary>
	/// Show or change where the pipeline looks for Blender and the kit. Call with no arguments to
	/// see what's currently resolved.
	/// </summary>
	/// <param name="blenderExe">New absolute path to blender.exe. Empty leaves it alone.</param>
	/// <param name="kitDir">New absolute path to the rp_kit folder. Empty leaves it alone.</param>
	[McpTool( "kit_paths" )]
	public static object Paths( string blenderExe = "", string kitDir = "" )
	{
		if ( !string.IsNullOrWhiteSpace( blenderExe ) ) KitConfig.BlenderExe = blenderExe;
		if ( !string.IsNullOrWhiteSpace( kitDir ) ) KitConfig.KitDir = kitDir;

		var exe = KitConfig.BlenderExe;
		var kit = KitConfig.KitDir;

		return new
		{
			BlenderExe = exe,
			BlenderFound = !string.IsNullOrWhiteSpace( exe ) && File.Exists( exe ),
			KitDir = kit,
			KitFound = Directory.Exists( kit ),
			Generators = Directory.Exists( kit )
				? Directory.GetFiles( kit, "build*.py" ).Select( Path.GetFileName ).ToArray()
				: Array.Empty<string>(),
			AssetsRoot = KitConfig.AssetsRoot(),
			Python = PythonExe,
		};
	}

	// deploy.py is stdlib-only, so any python on PATH runs it. Blender's bundled interpreter is
	// deliberately NOT used for it: under `blender --python`, sys.argv carries Blender's own
	// arguments and deploy.py's positional args land in the wrong place.
	private const string PythonExe = "python";

	/// <summary>Every registered asset under a project-relative folder.</summary>
	private static List<Asset> AssetsUnder( string folder )
	{
		var prefix = folder.Replace( '\\', '/' ).TrimEnd( '/' ) + "/";

		return AssetSystem.All
			.Where( a => a?.Path is not null
				&& a.Path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
			.ToList();
	}

	/// <summary>
	/// Run a process to completion, capturing both streams. Output is captured through events
	/// rather than a blocking read at the end, because Blender produces enough output on a city
	/// build to fill the pipe buffer and deadlock a naive ReadToEnd.
	/// </summary>
	private static async Task<ProcResult> Run( string exe, string args, string workingDir, int timeoutSeconds )
	{
		var info = new ProcessStartInfo
		{
			FileName = exe,
			Arguments = args,
			WorkingDirectory = workingDir,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		var output = new StringBuilder();
		var sw = Stopwatch.StartNew();

		using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

		process.OutputDataReceived += ( _, e ) => { if ( e.Data is not null ) lock ( output ) output.AppendLine( e.Data ); };
		process.ErrorDataReceived += ( _, e ) => { if ( e.Data is not null ) lock ( output ) output.AppendLine( e.Data ); };

		Log.Info( $"[Kit] {Path.GetFileName( exe )} {args}" );

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		var timedOut = false;

		try
		{
			using var cts = new CancellationTokenSource( TimeSpan.FromSeconds( timeoutSeconds ) );
			await process.WaitForExitAsync( cts.Token );
		}
		catch ( OperationCanceledException )
		{
			timedOut = true;
			try { process.Kill( true ); } catch { /* already gone */ }
		}

		string text;
		lock ( output ) text = output.ToString();

		return new ProcResult
		{
			Ok = !timedOut && process.ExitCode == 0,
			ExitCode = timedOut ? -1 : process.ExitCode,
			Output = text,
			Seconds = (float)sw.Elapsed.TotalSeconds,
			TimedOut = timedOut,
		};
	}

	private static StageResult Stage( string name, ProcResult proc ) => new()
	{
		Name = name,
		Ok = proc.Ok,
		Summary = proc.TimedOut
			? $"TIMED OUT after {proc.Seconds:0}s"
			: $"exit {proc.ExitCode} in {proc.Seconds:0.#}s",
		// The tail is where a python traceback and deploy.py's own summary both live. The rest of
		// Blender's output is per-object chatter nobody needs.
		Errors = new[] { Tail( proc.Output, proc.Ok ? 8 : 30 ) },
	};

	private static BuildResult Finish( BuildResult result, Stopwatch sw )
	{
		result.Seconds = (float)sw.Elapsed.TotalSeconds;
		result.Success = result.Stages.All( s => s.Ok );
		return result;
	}

	private static string Tail( string text, int lines )
	{
		if ( string.IsNullOrEmpty( text ) ) return "";

		var all = text.Replace( "\r\n", "\n" ).TrimEnd( '\n' ).Split( '\n' );
		return string.Join( "\n", all.Skip( Math.Max( 0, all.Length - lines ) ) );
	}

	private static string Between( string text, string begin, string end )
	{
		if ( string.IsNullOrEmpty( text ) ) return "";

		var from = text.IndexOf( begin, StringComparison.Ordinal );
		if ( from < 0 ) return "";

		from += begin.Length;

		var to = text.IndexOf( end, from, StringComparison.Ordinal );
		return (to < 0 ? text[from..] : text[from..to]).Trim( '\r', '\n', ' ' );
	}

	private class ProcResult
	{
		public bool Ok { get; set; }
		public int ExitCode { get; set; }
		public string Output { get; set; }
		public float Seconds { get; set; }
		public bool TimedOut { get; set; }
	}

	/// <summary>One stage of a build.</summary>
	public class StageResult
	{
		public string Name { get; set; }
		public bool Ok { get; set; }
		public string Summary { get; set; }
		public string[] Errors { get; set; }
	}

	/// <summary>What a whole build did.</summary>
	public class BuildResult
	{
		public bool Success { get; set; }
		public string Kit { get; set; }
		public string Script { get; set; }
		public string Subfolder { get; set; }
		public int Registered { get; set; }
		public int Compiled { get; set; }
		public float Seconds { get; set; }
		public string Note { get; set; }
		public List<StageResult> Stages { get; set; } = new();
	}

	/// <summary>What a blender_eval snippet printed.</summary>
	public class EvalResult
	{
		public bool Ok { get; set; }
		public string Output { get; set; }
		public int ExitCode { get; set; }
		public float Seconds { get; set; }
		/// <summary>Last lines of raw Blender output, only when the snippet never reached its markers.</summary>
		public string RawTail { get; set; }
	}

	/// <summary>One model's health.</summary>
	public class ModelReport
	{
		public string Model { get; set; }
		public bool HasCollision { get; set; }
		public string Bounds { get; set; }
		public string PhysicsBounds { get; set; }
		public int Meshes { get; set; }
		public int Materials { get; set; }
		public string SurfaceProp { get; set; }
		public string[] Problems { get; set; } = Array.Empty<string>();
	}

	/// <summary>Health of every model in a folder.</summary>
	public class ValidateResult
	{
		public string Folder { get; set; }
		public int Total { get; set; }
		public int Ok { get; set; }
		public int Problems { get; set; }
		public int NoCollision { get; set; }
		public ModelReport[] Models { get; set; }
	}

	/// <summary>What a forced reimport did.</summary>
	public class ReimportResult
	{
		public bool Success { get; set; }
		public int Recompiled { get; set; }
		public int Failed { get; set; }
		public string[] Errors { get; set; }
		public string Note { get; set; }
	}
}
