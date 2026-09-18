using Editor;
using Effigy;
using Sandbox;
using System;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Turn a rigged Effigy model into a playermodel — a model that carries citizen's 95 bones in
/// citizen's bind pose and names citizen's animation graph, so the graph drives it the way it
/// drives citizen.
///
///     effigy_playermodel models/effigy/gearhead_rigged.effigy            -> gearhead_rigged_citizen
///     effigy_playermodel models/effigy/gearhead_rigged.effigy gearhead_citizen
///
/// WHAT IT DOES, in order, on the way from the studio to the two files it writes:
///
///   1. Loads the .effigy and rebuilds it, the same way the rigged-export button does.
///   2. Binds each body to the bone the rig panel assigned it (BodyBoneMap), smooths the weights,
///      and applies any painted layer on top.
///   3. Fits <see cref="CitizenSkeleton"/> INTO the mesh through
///      <see cref="CitizenBoneMap.Playermodel"/> — citizen's bones, by citizen's names, standing
///      on the model's own joints. The mesh does not move and keeps its proportions.
///   4. Spreads limb weight onto the fitted twist bones, reorders depth-first, and remaps the
///      weights to match.
///   5. Writes a DMX (the mesh + the fitted bind pose + weights) and a .vmdl that names citizen's
///      animation graph and the citizen prefabs, with body bones marked to ignore the clips'
///      translation so citizen's bone lengths never reach it.
///
/// Steps 2-4 and the .vmdl text are <see cref="Playermodel"/> in the kernel, which also says why
/// the skeleton is fitted into the mesh rather than the mesh snapped onto citizen.
/// </summary>
public static class EffigyPlayermodelExport
{
	[ConCmd( "effigy_playermodel" )]
	public static void Run( string source = "", string outName = "" )
	{
		if ( string.IsNullOrWhiteSpace( source ) )
		{
			Log.Error( "[pm] usage: effigy_playermodel models/effigy/<name>.effigy [outName]" );
			return;
		}

		var root = EffigyAssetFolder.AssetsRoot();

		if ( root is null )
		{
			Log.Error( "[pm] could not resolve the project's Assets folder" );
			return;
		}

		var absSource = Path.GetFullPath( Path.Combine( root, source.Replace( '/', Path.DirectorySeparatorChar ) ) );

		if ( !File.Exists( absSource ) )
		{
			Log.Error( $"[pm] no file at {absSource}" );
			return;
		}

		PartStudio studio;

		try
		{
			studio = StudioDocument.ReadFile( absSource );
		}
		catch ( Exception e )
		{
			Log.Error( $"[pm] could not load {source}: {e.Message}" );
			return;
		}

		var report = studio.Rebuild();

		if ( report.HasErrors || studio.Bodies.Count == 0 )
		{
			Log.Error( $"[pm] the studio does not build - {report}" );
			return;
		}

		var name = string.IsNullOrWhiteSpace( outName )
			? Path.GetFileNameWithoutExtension( absSource ) + "_citizen"
			: outName.Trim();

		Export( studio, name );
	}

	/// <summary>
	/// Write and compile the playermodel for an already-rebuilt studio, and return the compiled
	/// asset path (<c>models/effigy/name.vmdl</c>), or null if nothing usable came out.
	///
	/// TAKES A STUDIO RATHER THAN A PATH, so the two callers can each do their own half. The console
	/// command loads a file; File -> Compile Playermodel hands over the document that is already
	/// open, unsaved edits included. Before the split the menu item would have had to save first,
	/// which is a surprising thing for a compile to do.
	///
	/// NO PIVOT IS APPLIED, unlike <c>CompileVmdl</c>. A playermodel's origin has to be between its
	/// feet, because that is where the engine stands a player and where citizen's clips are authored
	/// from; honouring a pivot the modeller set for some other export would sink or float the whole
	/// character.
	/// </summary>
	public static string Export( PartStudio studio, string name )
	{
		ArgumentNullException.ThrowIfNull( studio );

		if ( string.IsNullOrWhiteSpace( name ) )
		{
			Log.Error( "[pm] no name to write the playermodel under" );
			return null;
		}

		if ( studio.Bodies.Count == 0 )
		{
			Log.Error( "[pm] the studio has no bodies" );
			return null;
		}

		// WARNED, NOT REFUSED. A rigless studio still compiles into a citizen-skeleton model - every
		// vertex lands on one bone and the result is a statue that slides around - and saying so is
		// more use than a refusal to somebody who is halfway through rigging.
		if ( studio.Rig.Count == 0 )
			Log.Warning( "[pm] the studio has no rig - assign bones in the Rig panel, or this will "
				+ "compile into a model that animates as one rigid lump" );

		// Bind, fit citizen INTO the mesh, spread twist weight and order depth-first - the kernel's
		// half, shared with the headless generator so the two cannot drift apart.
		var fit = Playermodel.Build( studio );

		// EVERYTHING THAT MAKES A MODEL FAIL AS A PLAYERMODEL, said before it ships rather than
		// after it is standing wrong in a game. Checked on the FIT result, because binding, fitting
		// and twist spreading all change the weights - a check before them passes models that fail
		// after. Unmapped bones and stranded vertices are among what it reports, so the two hand
		// written warnings that used to be here are now part of one list.
		foreach ( var finding in Playermodel.Check( fit, DmxWriter.MaxInfluences ) )
		{
			var line = $"[pm] {finding.Problem}. {finding.Remedy}";

			if ( finding.Severity == Playermodel.Severity.Note )
				Log.Info( line );
			else
				Log.Warning( line );
		}

		var ordered = fit.Skeleton;

		var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy" );
		Directory.CreateDirectory( folder );

		var dmxPath = Path.Combine( folder, $"{name}.dmx" );
		DmxWriter.WriteFile( fit.Mesh, dmxPath, ordered, materialName: studio.NameForSlot, modelName: name );

		var vmdlPath = Path.Combine( folder, $"{name}.vmdl" );
		File.WriteAllText( vmdlPath, Playermodel.Vmdl( $"models/effigy/{name}.dmx", ordered ) );

		EffigyAssetFolder.Register( folder );

		var assetPath = $"models/effigy/{name}.vmdl";
		var asset = AssetSystem.FindByPath( assetPath );

		if ( asset is null )
		{
			Log.Warning( $"[pm] wrote {name}.dmx and {name}.vmdl but the asset system could not find the .vmdl" );
			return null;
		}

		asset.Compile( true );

		if ( asset.IsCompileFailed )
		{
			Log.Warning( $"[pm] {name}.vmdl compile FAILED - the compiler's output above says why. "
				+ "The .dmx is on disk either way." );
			return null;
		}

		Log.Info( $"[pm] {name}.vmdl compiled - {ordered.Count} citizen bones fitted to {fit.Mesh.VertexCount} vertices" );

		return assetPath;
	}
}
