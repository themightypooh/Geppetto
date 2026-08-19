using Editor;
using Marionette.ShaderForge;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.ShaderForgeEditor;

/// <summary>
/// Every call this tool makes into the engine's shader and material APIs, in one place.
///
/// This exists because of the warning at the top of MODELING-HANDOFF.md: this work was done
/// without the engine source on disk, so the exact shapes of Shader.Load, Material.FromShader,
/// Material.Set and Shader.Schema are LEADS, NOT FACTS. Rather than scatter those guesses through
/// the UI where one wrong signature takes the whole window down, they are funnelled through here
/// and every one is guarded.
///
/// The contract that follows from that: writing the .shad file is the deliverable and must always
/// work — it is plain File.WriteAllText. Live preview is best-effort. If the material APIs are not
/// shaped the way this assumes, the tool still generates and saves correct shaders and the panel
/// says preview is unavailable, rather than the tool being dead.
///
/// Run `shaderforge_probe` in the console to find out which half you are in.
/// </summary>
internal static class ShaderForgeBridge
{
	/// <summary>Logged once per failing operation, so a broken API does not spam the console on
	/// every slider tick.</summary>
	private static readonly HashSet<string> _reported = new();

	private static void ReportOnce( string operation, Exception e )
	{
		if ( !_reported.Add( operation ) )
			return;

		Log.Warning( $"[ShaderForge] {operation} failed — {e.GetType().Name}: {e.Message}. " +
			"Shader generation and saving still work; live preview of this does not. " +
			"Run `shaderforge_probe` for detail." );
	}

	// --- paths ------------------------------------------------------------------------------

	/// <summary>The project's Assets folder, or null if it cannot be resolved.</summary>
	public static string AssetsPath()
	{
		try
		{
			return Project.Current?.GetAssetsPath();
		}
		catch ( Exception e )
		{
			ReportOnce( "resolving the project assets path", e );
			return null;
		}
	}

	/// <summary>Absolute path a generated shader would be written to, or null if unresolvable.</summary>
	public static string OutputPathFor( string fileName )
	{
		var assets = AssetsPath();

		if ( string.IsNullOrEmpty( assets ) )
			return null;

		var safe = ShaderTemplate.SafeFileName( fileName );

		return Path.Combine( assets, ShaderTemplate.OutputFolder.Replace( '/', Path.DirectorySeparatorChar ), $"{safe}.shad" );
	}

	/// <summary>The asset-relative path (shaders/custom/foo.shad) for a given file name.</summary>
	public static string RelativePathFor( string fileName ) =>
		$"{ShaderTemplate.OutputFolder}/{ShaderTemplate.SafeFileName( fileName )}.shad";

	// --- writing ----------------------------------------------------------------------------

	/// <summary>
	/// Write a generated shader to the project. Plain file IO on purpose — this is the one
	/// operation that must not depend on an API shape being guessed right.
	/// </summary>
	/// <returns>The absolute path written, or null with the reason in <paramref name="error"/>.</returns>
	public static string Write( string fileName, string source, out string error )
	{
		error = null;

		var path = OutputPathFor( fileName );

		if ( string.IsNullOrEmpty( path ) )
		{
			error = "Could not resolve the project's Assets folder. Is a project open?";
			return null;
		}

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path ) );
			File.WriteAllText( path, source );
		}
		catch ( Exception e )
		{
			error = $"{e.GetType().Name}: {e.Message}";
			return null;
		}

		// Best-effort nudge for the asset system. Hot-reload normally notices a new .shad on its
		// own; if this call is wrong the file is still on disk and correct.
		try
		{
			AssetSystem.RegisterFile( path );
		}
		catch ( Exception e )
		{
			ReportOnce( "registering the written shader with the asset system", e );
		}

		return path;
	}

	// --- materials --------------------------------------------------------------------------

	/// <summary>
	/// Build a preview material from a shader on disk. Returns null — never throws — if the
	/// shader has not compiled yet or the API is not shaped as assumed.
	/// </summary>
	public static Material MaterialFromShader( string assetRelativePath )
	{
		if ( string.IsNullOrWhiteSpace( assetRelativePath ) )
			return null;

		try
		{
			return Material.FromShader( assetRelativePath );
		}
		catch ( Exception e )
		{
			ReportOnce( "Material.FromShader", e );
			return null;
		}
	}

	/// <summary>Load an existing material asset (used by the manual-preview path).</summary>
	public static Material LoadMaterial( string assetRelativePath )
	{
		try
		{
			return Material.Load( assetRelativePath );
		}
		catch ( Exception e )
		{
			ReportOnce( "Material.Load", e );
			return null;
		}
	}

	/// <summary>Push one edited parameter at the live preview material.</summary>
	public static void SetParam( Material material, ShaderParam param, float value, Color color, bool flag )
	{
		if ( !material.IsValid() || param is null )
			return;

		try
		{
			switch ( param.Kind )
			{
				case ShaderParamKind.Color:
					material.Set( param.Name, color );
					break;

				case ShaderParamKind.Bool:
					material.Set( param.Name, flag ? 1f : 0f );
					break;

				default:
					material.Set( param.Name, value );
					break;
			}
		}
		catch ( Exception e )
		{
			ReportOnce( $"Material.Set for {param.Kind}", e );
		}
	}

	// --- schema -----------------------------------------------------------------------------

	/// <summary>One parameter discovered on a shader that this tool did not generate.</summary>
	public sealed class SchemaEntry
	{
		public string Name;
		public string Type;
	}

	/// <summary>
	/// Read the variables a compiled shader exposes.
	///
	/// Reflection rather than a direct property read, deliberately. Shader.Schema is the one API
	/// in this tool whose shape was never confirmed against engine source, and it is only needed
	/// for the secondary path — inspecting somebody else's hand-written .shad. Generated shaders
	/// never need it: the generator already knows every parameter it declared, and the tweak panel
	/// builds from that. So a wrong guess here costs one panel's contents, not the tool.
	/// </summary>
	public static List<SchemaEntry> ReadSchema( object shader )
	{
		var entries = new List<SchemaEntry>();

		if ( shader is null )
			return entries;

		try
		{
			var schema = shader.GetType()
				.GetProperty( "Schema" )
				?.GetValue( shader );

			if ( schema is null )
				return entries;

			// Look for anything enumerable hanging off the schema and describe what it holds.
			foreach ( var property in schema.GetType().GetProperties() )
			{
				if ( property.GetValue( schema ) is not System.Collections.IEnumerable items )
					continue;

				if ( items is string )
					continue;

				foreach ( var item in items )
				{
					if ( item is null )
						continue;

					var name = item.GetType().GetProperty( "Name" )?.GetValue( item )?.ToString()
						?? item.ToString();

					entries.Add( new SchemaEntry { Name = name, Type = property.Name } );
				}
			}
		}
		catch ( Exception e )
		{
			ReportOnce( "reading Shader.Schema by reflection", e );
		}

		return entries;
	}

	/// <summary>Load a compiled shader by asset path, for the manual-preview path.</summary>
	public static object LoadShader( string assetRelativePath )
	{
		try
		{
			return Shader.Load( assetRelativePath );
		}
		catch ( Exception e )
		{
			ReportOnce( "Shader.Load", e );
			return null;
		}
	}

	// --- probe ------------------------------------------------------------------------------

	/// <summary>
	/// Reports which of the assumed shader APIs actually exist, in one command.
	///
	/// Same idea as RigApiProbe (see Editor/RigControlEditor): when a tool stands on an API whose
	/// shape was inferred rather than read, one command that answers "is this still true" beats
	/// another session of guessing. Run it first if anything about the preview misbehaves.
	/// </summary>
	[ConCmd( "shaderforge_probe" )]
	public static void Probe()
	{
		Log.Info( "[ShaderForge] --- API probe ---" );

		Report( "Project.Current.GetAssetsPath()", () => AssetsPath() ?? "(null)" );

		Report( "Material.FromShader(string)", () =>
		{
			var method = typeof( Material ).GetMethod( "FromShader", new[] { typeof( string ) } );
			return method is null ? "MISSING" : $"present -> {method.ReturnType.Name}";
		} );

		Report( "Material.Set(string, float)", () =>
		{
			var method = typeof( Material ).GetMethod( "Set", new[] { typeof( string ), typeof( float ) } );
			return method is null ? "MISSING" : "present";
		} );

		Report( "Material.Set(string, Color)", () =>
		{
			var method = typeof( Material ).GetMethod( "Set", new[] { typeof( string ), typeof( Color ) } );
			return method is null ? "MISSING" : "present";
		} );

		Report( "Shader.Load(string)", () =>
		{
			var method = typeof( Shader ).GetMethod( "Load", new[] { typeof( string ) } );
			return method is null ? "MISSING" : $"present -> {method.ReturnType.Name}";
		} );

		Report( "Shader.Schema", () =>
		{
			var property = typeof( Shader ).GetProperty( "Schema" );

			if ( property is null )
				return "MISSING — the schema panel will be empty for hand-written shaders";

			var members = string.Join( ", ", property.PropertyType.GetProperties().Select( p => p.Name ) );
			return $"present -> {property.PropertyType.Name} {{ {members} }}";
		} );

		Log.Info( "[ShaderForge] --- end probe ---" );
	}

	private static void Report( string what, Func<string> check )
	{
		try
		{
			Log.Info( $"[ShaderForge]   {what}: {check()}" );
		}
		catch ( Exception e )
		{
			Log.Info( $"[ShaderForge]   {what}: THREW {e.GetType().Name} — {e.Message}" );
		}
	}
}
