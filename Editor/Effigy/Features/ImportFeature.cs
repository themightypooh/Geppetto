using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// A body that already exists as a mesh file, brought into the history — one body per object in
/// the file, so a character exported with its brows, lids and hair kept separate arrives as a
/// tidy list of parts rather than one welded lump. See <see cref="ObjReader.ReadPieces"/>.
///
/// OBJ OR FBX, decided by what the bytes are rather than what the file is called. An FBX brings
/// one body per mesh, placed by its node transforms, scaled from its declared units to inches and
/// turned from its declared up axis to Z, with its UVs, vertex colours and material slots - see
/// <see cref="FbxImport"/>. When it is skinned, the weights ride along by bone name and land on
/// the body whenever the document's rig has those bones; <see cref="InstallSkeleton"/> is how the
/// file's own skeleton becomes the rig when the document has none.
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
public class ImportFeature : Feature
{
	public override string TypeName => "Import";

	/// <summary>Whether the bodies this feature publishes are reference only — see Body.IsReference.
	/// False here and true for WearerFeature, which is otherwise the same import.</summary>
	protected virtual bool ReferenceBodies => false;

	/// <summary>The mesh file this feature reads: Wavefront OBJ or FBX (binary, or ASCII 7.x).
	/// Anything else is a refusal with a remedy, not a crash.</summary>
	public readonly StringParam Source = new( "Source" );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Source, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	// Bytes from a side-car, waiting for the first rebuild. Same reason SculptFeature holds a
	// pending blob: the document has been read, the file on disk may be gone, and the triangles
	// still have to land on a body.
	byte[] _pending;

	// Last successfully parsed pieces, deleted ones included, so a rebuild whose source has not
	// changed does not parse again. Cloned on the way out — downstream features must not mutate
	// the cache.
	List<ObjReader.ObjPiece> _pieces;

	// The source file _pieces was read from, as path, size and write time; null when they came from
	// side-car bytes. See the note in Execute.
	string _piecesStamp;

	// The source bytes (OBJ or FBX) that produced _pieces, so a save can write the original rather
	// than a re-export.
	byte[] _objBytes;

	// What an FBX source carried besides geometry, parallel to _pieces: named skin weights per piece
	// (null entries for unskinned pieces, null list for an OBJ), the file's material names by slot,
	// its skeleton, and what the conversion had to say. Cached with _pieces for the same reason.
	List<List<(string Bone, float Weight)>[]> _weights;
	SortedDictionary<int, string> _sourceMaterials;
	Skeleton _sourceSkeleton;
	FbxImport.Result _fbx;

	// Piece indices the user has deleted. The source OBJ keeps every piece — deleting one must
	// not rewrite the file — so this records which of the parsed pieces Publish should skip.
	// Public so StudioDocument saves it (see StateFields); cleared when a new source is bound.
	public readonly List<int> RemovedPieces = new();

	/// <summary>True when a side-car has handed this feature bytes that have not been built yet.</summary>
	public bool HasPendingMesh => _pending is not null;

	/// <summary>True once a rebuild has produced a body from this feature.</summary>
	public bool HasMesh => _pieces is not null;

	/// <summary>True when the source bytes are FBX rather than OBJ.</summary>
	public bool IsFbxSource => FbxImport.IsFbx( _objBytes ?? _pending );

	/// <summary>The material names an FBX source gave its slots, slot → name. Empty for an OBJ or
	/// before the first build. Read-only: binding a slot to a real material is still a drop.</summary>
	public IReadOnlyDictionary<int, string> SourceMaterialNames =>
		_sourceMaterials ?? (IReadOnlyDictionary<int, string>)new SortedDictionary<int, string>();

	/// <summary>The FBX source's skeleton in Effigy space, or null for an OBJ, a file with no
	/// bones, or before the first build.</summary>
	public Skeleton SourceSkeleton => _sourceSkeleton;

	/// <summary>
	/// Make the source file's skeleton this document's rig, when the document has none.
	///
	/// ONLY AN EMPTY RIG IS FILLED - the same rule the wearer import follows. A rig somebody drew
	/// carries bindings, weight paint and soft-bone settings keyed on its names, and an import that
	/// replaced it would break all of those without being asked. Returns how many bones went in;
	/// <paramref name="message"/> always says what happened, including why nothing did.
	///
	/// The feature does not do this itself at rebuild because a rebuild only makes bodies - the
	/// rig is document state, changed by an explicit action (picking the file), never as a side
	/// effect of re-running history.
	/// </summary>
	public static int InstallSkeleton( PartStudio studio, ImportFeature import, out string message )
	{
		message = "";

		if ( studio is null || import is null )
			return 0;

		var bytes = import._objBytes ?? import._pending;

		if ( !FbxImport.IsFbx( bytes ) )
			return 0;

		Skeleton skeleton;

		try
		{
			skeleton = FbxImport.Read( bytes ).Skeleton;
		}
		catch ( Exception e )
		{
			message = $"The file's skeleton could not be read: {e.Message}";
			return 0;
		}

		if ( skeleton is not { Count: > 0 } )
			return 0;

		if ( studio.Rig.Count > 0 )
		{
			message = $"The file's {skeleton.Count} bones were not installed - this document already has a rig with "
				+ $"{studio.Rig.Count} bones. Delete those bones and pick the file again to take the file's skeleton.";
			return 0;
		}

		studio.Rig.Bones.Clear();
		studio.Rig.Bones.AddRange( skeleton.Bones );
		studio.MarkDirty( import );
		message = $"The file's skeleton is now the rig: {skeleton.Count} bones.";
		return skeleton.Count;
	}

	/// <summary>How many bodies this import produces — one per <c>o</c>/<c>g</c> lump in the file.
	/// Zero until it has been built once.</summary>
	public int PieceCount => _pieces?.Count ?? 0;

	/// <summary>The piece index a body id names, or -1 when the id is not one of this import's
	/// pieces. Body ids are <c>{featureId}b{N}</c>, where N is the position in the parsed file.</summary>
	public int PieceIndexFor( string bodyId )
	{
		if ( _pieces is null || string.IsNullOrWhiteSpace( bodyId ) )
			return -1;

		var prefix = Id + "b";

		if ( !bodyId.StartsWith( prefix, StringComparison.Ordinal ) )
			return -1;

		if ( !int.TryParse( bodyId[prefix.Length..], out var index ) )
			return -1;

		return index >= 0 && index < _pieces.Count ? index : -1;
	}

	/// <summary>How many pieces still publish, after the deleted ones are skipped.</summary>
	public int RemainingPieceCount()
	{
		if ( _pieces is null )
			return 0;

		var remaining = _pieces.Count;

		foreach ( var removed in RemovedPieces )
		{
			if ( removed >= 0 && removed < _pieces.Count )
				remaining--;
		}

		return remaining;
	}

	/// <summary>
	/// Whether deleting this body removes just its own piece. False for a single-piece import —
	/// removing the last piece would leave an empty import, so the caller removes the whole
	/// feature instead.
	/// </summary>
	public bool CanRemovePiece( string bodyId ) =>
		PieceIndexFor( bodyId ) >= 0 && RemainingPieceCount() > 1;

	/// <summary>
	/// Mark one piece as deleted. The source OBJ keeps every piece — deleting one must not rewrite
	/// the file — and <see cref="Publish"/> skips it from here on. The caller marks the studio dirty
	/// and rebuilds.
	/// </summary>
	public bool RemovePiece( string bodyId )
	{
		var index = PieceIndexFor( bodyId );

		if ( index < 0 )
			return false;

		if ( !RemovedPieces.Contains( index ) )
			RemovedPieces.Add( index );

		return true;
	}

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
		_weights = null;
		_sourceMaterials = null;
		_sourceSkeleton = null;
		_fbx = null;
	}

	/// <summary>
	/// Point this feature at a file and, if the file is there, take its bytes so a rebuild does
	/// not depend on the path still existing later.
	/// </summary>
	public void BindSource( string path )
	{
		Source.Value = path ?? "";
		_pieces = null;
		_weights = null;
		_sourceMaterials = null;
		_sourceSkeleton = null;
		_fbx = null;
		RemovedPieces.Clear();

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
		string stamp = null;

		if ( path.Length > 0 && File.Exists( path ) )
		{
			RefuseIfUnsupported( path );

			// THE FILE IS ONLY PARSED AGAIN WHEN IT HAS CHANGED. Deleting a piece, undo and a variable
			// edit all re-run this feature, and re-reading the source each time made every one of them
			// cost the whole file however little of it was left: ten deletes that took a 900k-face
			// Meshy import down to 20k faces still cost 1.3s apiece, where a file holding only the
			// survivors rebuilds in 40ms. Size and write time are the key, so exporting over the same
			// path is still picked up on the next rebuild.
			var info = new FileInfo( path );
			stamp = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

			if ( _pieces is not null && stamp == _piecesStamp )
			{
				Publish( ctx, _pieces );
				return;
			}

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
				"An Import needs a Wavefront OBJ or an FBX, and this one has no source path.",
				"Pick an .obj or .fbx file" );
		}
		else
		{
			FailOn( "Source",
				"The mesh file is missing",
				$"Nothing is at '{path}', and this feature has no side-car copy to fall back on.",
				"Pick the file again",
				"Put the mesh file next to this document and point Source at it" );
		}

		List<ObjReader.ObjPiece> pieces;
		FbxImport.Result fbx = null;
		var isFbx = FbxImport.IsFbx( bytes );

		// A file named .fbx that is not one would otherwise fall through to the OBJ reader, which
		// skips every line it does not know and reports an empty mesh - true, and no help at all.
		if ( !isFbx && string.Equals( Path.GetExtension( path ), ".fbx", StringComparison.OrdinalIgnoreCase ) && from == path )
		{
			FailOn( "Source",
				"The FBX file could not be read",
				$"{from} is named .fbx but has neither a binary FBX header nor an ASCII FBX header.",
				"Export it again as FBX (binary)",
				"Or export it as OBJ and pick that" );
		}

		try
		{
			if ( isFbx )
			{
				fbx = FbxImport.Read( bytes );
				pieces = fbx.Pieces;
			}
			else
			{
				pieces = ObjReader.ReadPieces( Encoding.UTF8.GetString( bytes ) );
			}
		}
		catch ( Exception e )
		{
			if ( isFbx )
			{
				FailOn( "Source",
					"The FBX file could not be read",
					$"{from} is an FBX this feature could not read: {e.Message}",
					"Export it again as binary FBX 7.x",
					"Or export it as OBJ and pick that" );
			}

			FailOn( "Source",
				"The mesh file could not be read",
				$"{from} is not a Wavefront OBJ this feature understands: {e.Message}",
				"Export the sculpt as OBJ or FBX and pick that" );
			return;
		}

		var vertices = 0;
		var faces = 0;

		foreach ( var piece in pieces )
		{
			vertices += piece.Mesh.VertexCount;
			faces += piece.Mesh.FaceCount;
		}

		if ( isFbx && (vertices == 0 || faces == 0) )
		{
			var bones = fbx.Skeleton?.Count ?? 0;
			FailOn( "Source",
				"The FBX has no mesh",
				$"{from} holds no polygon meshes" + (bones > 0 ? $", only a skeleton of {bones} bones." : "."),
				"Export the mesh objects too, not just the armature or animation",
				"Pick a different file" );
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
		_piecesStamp = stamp;
		_fbx = fbx;
		_weights = fbx?.Weights;
		_sourceMaterials = fbx?.MaterialSlots;
		_sourceSkeleton = fbx?.Skeleton;

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

		// How many pieces will actually publish, after the deleted ones are skipped. A single
		// SURVIVOR keeps the feature's own name, the same way a single-piece file does.
		var remaining = pieces.Count;

		foreach ( var removed in RemovedPieces )
		{
			if ( removed >= 0 && removed < pieces.Count )
				remaining--;
		}

		var single = remaining == 1;

		// Skin from the file, resolved by bone name against the rig as it is NOW - the rig can
		// change without the file changing, so this happens on every publish, not at parse.
		var missingBones = new SortedSet<string>( StringComparer.Ordinal );
		var skinnedNoRig = 0;
		var skinLeftOff = 0;
		var skinned = 0;

		for ( var i = 0; i < pieces.Count; i++ )
		{
			var piece = pieces[i];

			// The id is claimed for a removed piece too, so deleting one never renumbers the
			// survivors — BodyNames, rig bindings and downstream body selections all key on
			// {featureId}b{N} and must keep meaning the same piece.
			var id = ctx.NewBodyId();

			if ( RemovedPieces.Contains( i ) )
				continue;

			var mesh = piece.Mesh.Clone();

			if ( slot != 0 )
			{
				foreach ( var face in mesh.Faces )
					face.Material = slot;
			}

			if ( _weights is not null && i < _weights.Count && _weights[i] is { } named
				&& named.Length == mesh.VertexCount )
			{
				if ( ctx.Rig is not { Count: > 0 } )
				{
					skinnedNoRig++;
				}
				else if ( FbxImport.Resolve( named, ctx.Rig, missingBones, out _ ) is { } skin )
				{
					mesh.Skin = skin;
					skinned++;
				}
				else
				{
					skinLeftOff++;
				}
			}

			var label = single || string.IsNullOrWhiteSpace( piece.Name )
				? Name
				: $"{Name} / {piece.Name}";

			ctx.Bodies.Add( new Body( id, label, mesh ) { IsReference = ReferenceBodies } );
		}

		WarnAboutSkin( skinnedNoRig, skinLeftOff, skinned, missingBones );
	}

	/// <summary>
	/// What happened to the file's skin, when it did not simply land. One warning, the most
	/// useful one: weights that could not reach their bones first, then weights with no rig at all,
	/// then faces the file says point the other way.
	/// </summary>
	void WarnAboutSkin( int skinnedNoRig, int skinLeftOff, int skinned, SortedSet<string> missing )
	{
		var named = string.Join( ", ", missing.Take( 6 ) ) + (missing.Count > 6 ? $" and {missing.Count - 6} more" : "");

		if ( skinLeftOff > 0 )
		{
			Warn( "Some parts' skin weights were left off",
				$"The rig has no bone called {named}, and {skinLeftOff} part(s) had vertices weighted only to those, "
					+ "so those parts were left for the rig to bind instead of keeping the file's weights.",
				"Delete this document's bones and pick the file again to take its skeleton",
				"Or rename the rig's bones to match the file" );
			return;
		}

		if ( missing.Count > 0 )
		{
			Warn( "Some skin influences were dropped",
				$"The rig has no bone called {named}. Their share of each vertex went to the file's other bones.",
				"Delete this document's bones and pick the file again to take its skeleton",
				"Or rename the rig's bones to match the file" );
			return;
		}

		if ( skinnedNoRig > 0 )
		{
			var bones = _sourceSkeleton?.Count ?? 0;
			Warn( "The file's skin weights were left off",
				$"{skinnedNoRig} part(s) of this FBX are skinned to {bones} bones, but this document has no rig to hold them.",
				"Pick the file again from Source's browse button - with no bones here, the file's skeleton comes in with it",
				"Or draw bones in Rig and bind the part there" );
			return;
		}

		if ( _fbx is { InwardPieces: > 0 } fbx )
		{
			Warn( "Some imported faces point inward",
				$"{fbx.InwardPieces} part(s) of this FBX have normals in the file that point against their faces, "
					+ "so they will light inside out.",
				"Re-export with the normals facing out (recalculate outside before exporting)" );
		}
	}

	/// <summary>The extensions Import reads. Anything else is refused by name before any bytes are
	/// parsed.</summary>
	public static readonly string[] Extensions = { ".obj", ".fbx" };

	static void RefuseIfUnsupported( string path )
	{
		var ext = Path.GetExtension( path );

		if ( Array.Exists( Extensions, e => string.Equals( e, ext, StringComparison.OrdinalIgnoreCase ) ) )
			return;

		var shown = string.IsNullOrEmpty( ext ) ? "no extension" : ext;
		var gltf = string.Equals( ext, ".glb", StringComparison.OrdinalIgnoreCase ) || string.Equals( ext, ".gltf", StringComparison.OrdinalIgnoreCase );

		FailOn( "Source",
			"Import reads OBJ and FBX",
			$"'{path}' is {shown}." + (gltf ? " glTF/GLB is not read yet." : ""),
			"Export the mesh as FBX or OBJ and pick that" );
	}
}

/// <summary>
/// Where an imported mesh lives next to a document.
///
/// One directory beside the .effigy file, one mesh file per import feature (<c>{id}.obj</c>, or
/// <c>{id}.fbx</c> for an FBX source), named by that feature's id.
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

	/// <summary>The side-car for an import whose bytes are OBJ. An FBX source is kept as FBX - see
	/// <see cref="PathFor(string, string, byte[])"/>.</summary>
	public static string PathFor( string documentPath, string featureId ) =>
		Path.Combine( DirectoryFor( documentPath ), featureId + ".obj" );

	/// <summary>The side-car for these bytes: <c>{id}.fbx</c> when they are FBX, <c>{id}.obj</c>
	/// otherwise. Kept in the source's own format so a skinned FBX keeps its skeleton and weights
	/// across a save, which a re-export to OBJ would lose.</summary>
	public static string PathFor( string documentPath, string featureId, byte[] bytes ) =>
		Path.Combine( DirectoryFor( documentPath ), featureId + (FbxImport.IsFbx( bytes ) ? ".fbx" : ".obj") );

	static readonly string[] SidecarExtensions = { ".obj", ".fbx" };

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
		{
			var path = PathFor( documentPath, id, bytes );
			File.WriteAllBytes( path, bytes );

			// THIS FEATURE'S OWN PREVIOUS COPY in the other format goes, so a load cannot pick up
			// the stale one: re-pointing an import from an OBJ to an FBX would otherwise leave
			// both, and which won would depend on the order Load checks them in. Only that exact
			// file - {id} in the other extension - is touched; nothing another feature wrote is.
			foreach ( var ext in SidecarExtensions )
			{
				var other = Path.ChangeExtension( path, ext );

				if ( other != path && File.Exists( other ) )
					File.Delete( other );
			}
		}

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

			var path = SidecarExtensions
				.Select( ext => Path.Combine( dir, feature.Id + ext ) )
				.FirstOrDefault( File.Exists );

			if ( path is null )
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
			{
				foreach ( var ext in SidecarExtensions )
					keep.Add( feature.Id + ext );
			}
		}

		var removed = 0;

		foreach ( var path in SidecarExtensions.SelectMany( ext => Directory.GetFiles( dir, "*" + ext ) ) )
		{
			if ( keep.Contains( Path.GetFileName( path ) ) )
				continue;

			File.Delete( path );
			removed++;
		}

		return removed;
	}
}
