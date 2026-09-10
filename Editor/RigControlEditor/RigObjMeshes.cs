using Effigy;
using Marionette;
using Marionette.EditorTools;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;

namespace Marionette.Tools;

/// <summary>
/// Wavefront OBJ files, turned into viewport models, one per lump of the file.
///
/// WHY MARIONETTE READS OBJ AT ALL. A clip is a scene: the hand, the thing it is opening, and the
/// bits of that thing that move separately. Until now every one of those had to be a compiled
/// .vmdl asset, so animating against a mesh somebody exported from Blender meant compiling a model
/// per piece before you could even see whether the grip lined up. An OBJ is the format every tool
/// writes, it carries its own object/group markers, and Effigy already has a reader that splits on
/// them - so a file that was exported as a door, a handle and a hinge arrives as three objects that
/// can be posed and keyed separately, which is the whole point.
///
/// CACHED BY PATH AND BY PIECE. A prop is rebuilt whenever the list changes, and re-reading and
/// re-triangulating a 60k-triangle file on each of those would make editing the list feel broken.
/// The cache is keyed by the resolved absolute path, so two props out of one file share one parse.
///
/// NOTHING HERE COMPILES AN ASSET. The model is built in memory for the viewport, exactly the way
/// Effigy previews its own meshes. That is deliberate: an import must not write .vmdl files into
/// somebody's project as a side effect of being looked at.
/// </summary>
internal static class RigObjMeshes
{
	private static readonly Dictionary<string, List<ObjReader.ObjPiece>> _files = new();
	private static readonly Dictionary<string, Model> _models = new();

	/// <summary>
	/// Where an OBJ actually is on disk.
	///
	/// A clip stores the path relative to itself when the file was copied in beside it, and an
	/// absolute path when it was not - so both are tried, relative first. Relative first because
	/// that is the copy the clip owns: if a project is moved wholesale, the absolute path is the
	/// one that has gone stale.
	/// </summary>
	public static string Resolve( string source, string documentFolder )
	{
		if ( string.IsNullOrWhiteSpace( source ) )
			return null;

		if ( !string.IsNullOrWhiteSpace( documentFolder ) )
		{
			var beside = Path.GetFullPath( Path.Combine( documentFolder, source ) );

			if ( File.Exists( beside ) )
				return beside;
		}

		return File.Exists( source ) ? Path.GetFullPath( source ) : null;
	}

	/// <summary>Every lump in the file, in the order the exporter wrote them. Empty when the file
	/// is missing or unreadable - the caller reports that, since only it knows which prop asked.</summary>
	public static IReadOnlyList<ObjReader.ObjPiece> Pieces( string absolutePath )
	{
		if ( string.IsNullOrWhiteSpace( absolutePath ) )
			return Array.Empty<ObjReader.ObjPiece>();

		var key = Key( absolutePath );

		if ( _files.TryGetValue( key, out var cached ) )
			return cached;

		List<ObjReader.ObjPiece> pieces;

		try
		{
			pieces = ObjReader.ReadPieces( File.ReadAllText( absolutePath ) );
		}
		catch ( Exception e )
		{
			// A bad file is a thing to say out loud once, not to throw out of a paint loop that
			// would then run again next frame and throw again.
			Log.Warning( $"[Marionette] could not read {absolutePath}: {e.Message}" );
			pieces = new List<ObjReader.ObjPiece>();
		}

		_files[key] = pieces;
		return pieces;
	}

	/// <summary>
	/// One lump of the file as a viewport model.
	///
	/// An empty part name means the whole file - a single-object OBJ, or one an exporter wrote with
	/// no markers at all, which the reader hands back as one unnamed piece. Matching an empty name
	/// against the first piece rather than refusing is what lets those files be imported without
	/// the person doing it having to know whether their exporter bothered.
	/// </summary>
	public static Model Load( string absolutePath, string part )
	{
		if ( string.IsNullOrWhiteSpace( absolutePath ) )
			return null;

		var key = $"{Key( absolutePath )}|{part}";

		if ( _models.TryGetValue( key, out var cached ) )
			return cached;

		var pieces = Pieces( absolutePath );
		PolyMesh mesh = null;

		foreach ( var piece in pieces )
		{
			if ( !string.IsNullOrEmpty( part ) && piece.Name != part )
				continue;

			mesh = piece.Mesh;
			break;
		}

		// Named a piece the file does not have - the exporter's names changed, or the file was
		// replaced with a different one. Nothing to draw; the prop reports it.
		var model = mesh is null ? null : EffigyPreview.Build( mesh );

		_models[key] = model;
		return model;
	}

	/// <summary>Drop a file from the cache so the next look re-reads it. Called after an import,
	/// so re-importing a file you have just re-exported shows the new mesh rather than the one
	/// this session happened to read first.</summary>
	public static void Forget( string absolutePath )
	{
		if ( string.IsNullOrWhiteSpace( absolutePath ) )
			return;

		var key = Key( absolutePath );

		_files.Remove( key );

		foreach ( var modelKey in new List<string>( _models.Keys ) )
		{
			if ( modelKey.StartsWith( key + "|", StringComparison.Ordinal ) )
				_models.Remove( modelKey );
		}
	}

	/// <summary>Windows paths differ by case and by slash without differing at all, and a cache
	/// that missed on that would parse the same file twice and hand out two models for it.</summary>
	private static string Key( string path ) => path.Replace( '\\', '/' ).ToLowerInvariant();
}
