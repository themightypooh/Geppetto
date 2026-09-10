using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// A body that already exists as a mesh file, brought into the history — one body per object in
/// the file, so a character exported with its brows, lids and hair kept separate arrives as a
/// tidy list of parts rather than one welded lump. See <see cref="ObjReader.ReadPieces"/>.
///
/// EFFIGY IS CONSTRUCTIVE. A body is the output of Primitive / Sketch+Extrude / Loft / Sweep /
/// Boolean / Sculpt — there is no other way in. Paint, weight paint and SkinBinder all work on
/// bodies, so a sculpt made outside the tool (Meshy, Blender, a scan) was unreachable: you could
/// look at it, you could not paint it. This feature is that way in.
///
/// THE MESH DOES NOT GO IN THE DOCUMENT. StudioDocument saves public fields by reflection into
/// a text file whose virtue is being readable, and a 60k-triangle OBJ is megabytes of vertices —
/// the same problem SculptFeature solved with a side-car. The document holds a source path. The
/// triangles live in that file, or in a copy next to the .effigy (see <see cref="ImportSidecar"/>),
/// and they are loaded at rebuild.
/// </summary>
public sealed class ImportFeature : Feature
{
	public override string TypeName => "Import";

	/// <summary>The mesh file this feature reads. Wavefront OBJ; anything else is a refusal with
	/// a remedy, not a crash.</summary>
	public readonly StringParam Source = new( "Source" );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Source, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	// Bytes from a side-car, waiting for the first rebuild. Same reason SculptFeature holds a
	// pending blob: the document has been read, the file on disk may be gone, and the triangles
	// still have to land on a body.
	byte[] _pending;

	// Last successfully parsed pieces, kept so a rebuild that is not re-reading the source does not
	// have to parse again. Cloned on the way out — downstream features must not mutate the cache.
	List<ObjReader.ObjPiece> _pieces;

	// The OBJ bytes that produced _mesh, so a save can write the original rather than a re-export.
	byte[] _objBytes;

	/// <summary>True when a side-car has handed this feature bytes that have not been built yet.</summary>
	public bool HasPendingMesh => _pending is not null;

	/// <summary>True once a rebuild has produced a body from this feature.</summary>
	public bool HasMesh => _pieces is not null;

	/// <summary>How many bodies this import produces — one per <c>o</c>/<c>g</c> lump in the file.
	/// Zero until it has been built once.</summary>
	public int PieceCount => _pieces?.Count ?? 0;

	/// <summary>
	/// Whether the cached result is out of date even though nobody called MarkDirty.
	///
	/// A side-car load mutates _pending without going through the dialog, so the rebuild would
	/// reuse an empty snapshot and the imported body would simply not appear. Same reason
	/// SculptFeature answers this.
	/// </summary>
	public override bool IsStale => _pending is not null;

	/// <summary>The OBJ bytes to persist beside the document, or null if nothing has been loaded.</summary>
	public byte[] SaveMesh() => _objBytes ?? _pending;

	/// <summary>Take bytes from a side-car. They are parsed at the next rebuild, not now.</summary>
	public void LoadMesh( byte[] bytes )
	{
		_pending = bytes;
		_objBytes = bytes;
		_pieces = null;
	}

	/// <summary>
	/// Point this feature at a file and, if the file is there, take its bytes so a rebuild does
	/// not depend on the path still existing later.
	/// </summary>
	public void BindSource( string path )
	{
		Source.Value = path ?? "";
		_pieces = null;

		if ( !string.IsNullOrWhiteSpace( path ) && File.Exists( path ) )
			LoadMesh( File.ReadAllBytes( path ) );
		else
			_pending = null;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var path = Source.Value?.Trim() ?? "";
		byte[] bytes = null;
		string from = null;

		if ( path.Length > 0 && File.Exists( path ) )
		{
			RefuseIfNotObj( path );
			bytes = File.ReadAllBytes( path );
			from = path;
		}
		else if ( _pending is not null )
		{
			bytes = _pending;
			from = "the side-car copy beside this document";
		}
		else if ( _pieces is not null )
		{
			Publish( ctx, _pieces );
			return;
		}
		else if ( path.Length == 0 )
		{
			FailOn( "Source",
				"No mesh file to import",
				"An Import needs a Wavefront OBJ, and this one has no source path.",
				"Pick an .obj file" );
		}
		else
		{
			FailOn( "Source",
				"The mesh file is missing",
				$"Nothing is at '{path}', and this feature has no side-car copy to fall back on.",
				"Pick the file again",
				"Put the OBJ next to this document and point Source at it" );
		}

		List<ObjReader.ObjPiece> pieces;

		try
		{
			pieces = ObjReader.ReadPieces( Encoding.UTF8.GetString( bytes ) );
		}
		catch ( Exception e )
		{
			FailOn( "Source",
				"The mesh file could not be read",
				$"{from} is not a Wavefront OBJ this feature understands: {e.Message}",
				"Export the sculpt as OBJ and pick that" );
			return;
		}

		var vertices = 0;
		var faces = 0;

		foreach ( var piece in pieces )
		{
			vertices += piece.Mesh.VertexCount;
			faces += piece.Mesh.FaceCount;
		}

		if ( vertices == 0 || faces == 0 )
		{
			FailOn( "Source",
				"The mesh file is empty",
				$"{from} has {vertices} vertices and {faces} faces.",
				"Export a mesh that has faces",
				"Pick a different file" );
		}

		_objBytes = bytes;
		_pending = null;
		_pieces = pieces;

		Publish( ctx, pieces );
	}

	/// <summary>
	/// One body per lump of the file.
	///
	/// A SINGLE-PIECE FILE KEEPS THE FEATURE'S OWN NAME, so the common case — one object, or an
	/// exporter that wrote no markers at all — reads in the Parts list exactly as it did before
	/// imports could be split. Only a file that really carries several objects grows several rows,
	/// and then the exporter's names are the useful half of the label, prefixed so two imports
	/// cannot produce two rows called "head".
	///
	/// The mesh is cloned on the way out because <see cref="_pieces"/> is a cache: a rebuild that
	/// did not re-read the file publishes from it, and a downstream feature that mutated what it
	/// was handed would corrupt every rebuild after.
	/// </summary>
	void Publish( FeatureContext ctx, List<ObjReader.ObjPiece> pieces )
	{
		var slot = Material.Clamped;
		var single = pieces.Count == 1;

		foreach ( var piece in pieces )
		{
			var mesh = piece.Mesh.Clone();

			if ( slot != 0 )
			{
				foreach ( var face in mesh.Faces )
					face.Material = slot;
			}

			var label = single || string.IsNullOrWhiteSpace( piece.Name )
				? Name
				: $"{Name} / {piece.Name}";

			ctx.Bodies.Add( new Body( ctx.NewBodyId(), label, mesh ) );
		}
	}

	static void RefuseIfNotObj( string path )
	{
		var ext = Path.GetExtension( path );

		if ( string.Equals( ext, ".obj", StringComparison.OrdinalIgnoreCase ) )
			return;

		var shown = string.IsNullOrEmpty( ext ) ? "no extension" : ext;
		FailOn( "Source",
			"Import reads Wavefront OBJ",
			$"'{path}' is {shown}. FBX, GLB and the rest are writers in this kernel, not readers.",
			"Export the mesh as OBJ and pick that" );
	}
}

/// <summary>
/// Where an imported mesh lives next to a document.
///
/// One directory beside the .effigy file, one OBJ per import feature, named by that feature's id.
/// Keyed by id rather than by position so re-ordering the history or renaming the feature does not
/// hand somebody else's sculpt to this one.
///
/// Saving does not delete files it did not write — same rule as <see cref="SculptSidecar"/>. A
/// file whose feature is gone is the cheapest undo of "I deleted the import and saved".
/// <see cref="Prune"/> exists for when that is actually wanted.
/// </summary>
public static class ImportSidecar
{
	public const string FolderSuffix = ".import";

	public static string DirectoryFor( string documentPath )
	{
		if ( string.IsNullOrWhiteSpace( documentPath ) )
			throw new ArgumentException( "A document path is needed to find its imported meshes.", nameof( documentPath ) );

		var dir = Path.GetDirectoryName( documentPath ) ?? "";
		return Path.Combine( dir, Path.GetFileNameWithoutExtension( documentPath ) + FolderSuffix );
	}

	public static string PathFor( string documentPath, string featureId ) =>
		Path.Combine( DirectoryFor( documentPath ), featureId + ".obj" );

	/// <summary>Write an OBJ for every import feature that has one. Returns how many it wrote.</summary>
	public static int Save( PartStudio studio, string documentPath )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		var pending = new List<(string Id, byte[] Bytes)>();

		foreach ( var feature in studio.Features )
		{
			if ( feature is not ImportFeature import )
				continue;

			var bytes = import.SaveMesh();

			if ( bytes is not null )
				pending.Add( (feature.Id, bytes) );
		}

		if ( pending.Count == 0 )
			return 0;

		Directory.CreateDirectory( DirectoryFor( documentPath ) );

		foreach ( var (id, bytes) in pending )
			File.WriteAllBytes( PathFor( documentPath, id ), bytes );

		return pending.Count;
	}

	/// <summary>Hand each import feature its OBJ. Parsed at the next rebuild, not now.</summary>
	public static int Load( PartStudio studio, string documentPath )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		var dir = DirectoryFor( documentPath );

		if ( !Directory.Exists( dir ) )
			return 0;

		var loaded = 0;

		foreach ( var feature in studio.Features )
		{
			if ( feature is not ImportFeature import )
				continue;

			var path = PathFor( documentPath, feature.Id );

			if ( !File.Exists( path ) )
				continue;

			import.LoadMesh( File.ReadAllBytes( path ) );
			loaded++;
		}

		return loaded;
	}

	/// <summary>Delete OBJs no feature in this studio claims. Destructive, so it is never part of
	/// saving — see the note on this class.</summary>
	public static int Prune( PartStudio studio, string documentPath )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		var dir = DirectoryFor( documentPath );

		if ( !Directory.Exists( dir ) )
			return 0;

		var keep = new HashSet<string>( StringComparer.Ordinal );

		foreach ( var feature in studio.Features )
		{
			if ( feature is ImportFeature )
				keep.Add( feature.Id + ".obj" );
		}

		var removed = 0;

		foreach ( var path in Directory.GetFiles( dir, "*.obj" ) )
		{
			if ( keep.Contains( Path.GetFileName( path ) ) )
				continue;

			File.Delete( path );
			removed++;
		}

		return removed;
	}
}
