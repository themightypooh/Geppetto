using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Effigy;

/// <summary>
/// A document's built geometry, kept in a file so opening one does not rebuild it.
///
/// WHY THIS EXISTS. <see cref="StudioDocument.Read"/> is fast — it is a text parse — and
/// <see cref="PartStudio.Rebuild"/> is not. Measured on a real document (the podracer engine, 325
/// features), the parse is milliseconds and the rebuild is 12.7 seconds, and 10.6 of those seconds
/// are three features at the top of the tree: a Blob, the Remesh that decimates it, and the UV
/// project that unwraps it. Every launch paying that is the difference between a tool that feels
/// instant and one that feels broken, and it is the single thing that has to be solved before a
/// viewport can be built on top.
///
/// WHAT IT STORES, AND WHAT IT DOES NOT. The cache is the state a rebuild ENDS with —
/// <c>PartStudio.Bodies</c> and <c>Planes</c> — written after
/// <c>MaterialScale.Apply</c> has run, so the UVs in it are the scaled ones the viewport and the
/// exporters see. That is why <see cref="PartStudio.AdoptCachedState"/> must not re-run the
/// presentation and material-scale passes: doing so would divide the UVs a second time. Nothing
/// derivable is stored, because a derivable copy is a copy that can disagree — the feature tree,
/// the material names, the rig, the variables and the body names all come back from the text.
///
/// THE KEY IS EVERYTHING THE BUILD READS. The source text, plus a stamp for each file the document
/// reaches for on disk (an import, a sculpt blob, an alpha map), plus the identity of the compiled
/// kernel — because a rebuilt assembly with a changed algorithm would otherwise hand back geometry
/// the old one made. A stamp is path, length and write time, which is the rule ImportFeature
/// already uses on its own source, so the two agree about what "changed" means rather than each
/// having their own opinion.
///
/// A STALE KEY IS NOT AN ERROR. It means rebuild, and the answer is written back over the top. A
/// key that does not match never loads; the reader does not try to merge or repair, because there is
/// no such thing as half a rebuilt model.
///
/// WHAT IT REFUSES. Shape keys and a paint atlas are state that lives on a mesh and is not written
/// here — a morph carries drivers and corrective bind data, and a <see cref="PaintCanvas"/>'s dirty
/// flag is upload state a byte copy cannot honestly reconstruct. A document with either is not
/// cached at all, and says so, rather than being cached minus the part that would have been lost.
/// That is the same call <see cref="SculptBlob"/> makes about writing deltas it cannot represent,
/// and the honest version of a cache that is allowed to be incomplete is one that declines.
///
/// WHERE IT LIVES. Not beside the document. A cache is a build artifact, not something the user
/// authored, and putting a multi-megabyte binary next to somebody's .effigy file means every repo
/// holding one grows an untracked file that has to be explained to git. See <see cref="Directory"/>.
/// </summary>
public static class StudioCache
{
	/// <summary>Eight bytes so a file can be identified without parsing it.</summary>
	static readonly byte[] Magic = Encoding.ASCII.GetBytes( "EFFIGYPC" );

	/// <summary>Bumped when the layout changes. Refused by name rather than by crash, like every
	/// other format in here.
	///
	/// 2 added the leading "this body has a mesh" byte. 1 wrote a body with no mesh as five zeroed
	/// fields, which read back as a body holding an EMPTY mesh instead of none — a difference no
	/// feature can currently produce, which is exactly why it would have gone unnoticed until it
	/// mattered. There is no 1 left in the wild to be compatible with.</summary>
	public const int Version = 2;

	public const string Suffix = ".cache";

	/// <summary>
	/// Off for a caller that needs a guaranteed cold build — a benchmark, a test, or a tool that
	/// wants to report what a rebuild actually costs. A switch rather than an honour system: the
	/// kernel should not be reading the environment to decide how it behaves.
	/// </summary>
	public static bool Enabled = true;

	/// <summary>
	/// One cache root for the machine, honouring XDG_CACHE_HOME. NOT beside the document, on
	/// purpose: see the class note. Deleting this whole directory is always safe, and is the
	/// answer to every question about a cache that looks wrong.
	/// </summary>
	public static string Directory
	{
		get
		{
			var custom = Environment.GetEnvironmentVariable( "EFFIGY_CACHE_DIR" );

			if ( !string.IsNullOrWhiteSpace( custom ) )
				return custom;

			var xdg = Environment.GetEnvironmentVariable( "XDG_CACHE_HOME" );

			var root = !string.IsNullOrWhiteSpace( xdg )
				? xdg
				: Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".cache" );

			return Path.Combine( root, "effigy" );
		}
	}

	// --- loading ------------------------------------------------------------------------------

	/// <summary>
	/// Read a document and hand back a built one, from the cache when there is a valid entry for it.
	///
	/// The whole reason this is one call rather than three: every caller that gets the order wrong —
	/// restore after rebuild, save before rebuild — is a caller that did the expensive thing anyway.
	/// </summary>
	public static PartStudio Load( string documentPath ) => Load( documentPath, out _ );

	/// <summary>
	/// The same, and also which way the document arrived.
	///
	/// A caller with a window on screen has a question this answers and the geometry does not: once
	/// the model is up, 0.09 s and 13 s look identical, and only one of them means the cache is
	/// working. A loader that reports "loaded" without it is how a cache that quietly stopped
	/// hitting goes unnoticed for a week.
	/// </summary>
	public static PartStudio Load( string documentPath, out string note )
	{
		var studio = StudioDocument.ReadFile( documentPath );

		if ( TryRestore( studio, documentPath, out note ) )
			return studio;

		studio.Rebuild();
		TrySave( studio, documentPath, out var saved );

		note = $"{note}; {saved}";
		return studio;
	}

	/// <summary>
	/// Put the cached geometry into <paramref name="studio"/> in place of a rebuild.
	///
	/// Returns false with a reason in <paramref name="note"/> for every way this can decline —
	/// no entry, stale, a format this build does not read, or a file that turned out to be
	/// truncated. The caller rebuilds; the note is for a UI or a log, not a decision.
	/// </summary>
	public static bool TryRestore( PartStudio studio, string documentPath, out string note )
	{
		note = null;

		if ( !Enabled ) { note = "the cache is off"; return false; }

		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		if ( string.IsNullOrWhiteSpace( documentPath ) )
		{
			note = "no document path, so no cache to find";
			return false;
		}

		var path = PathFor( documentPath );

		if ( !File.Exists( path ) )
		{
			note = "nothing cached yet";
			return false;
		}

		try
		{
			using var stream = File.OpenRead( path );
			using var reader = new BinaryReader( stream, Encoding.UTF8 );

			if ( !reader.ReadBytes( Magic.Length ).SequenceEqual( Magic ) )
			{
				note = Drop( path, "not an Effigy geometry cache" );
				return false;
			}

			var version = reader.ReadInt32();

			if ( version != Version )
			{
				note = Drop( path, $"cache format {version}, this build reads {Version}" );
				return false;
			}

			if ( reader.ReadString() != Key( studio, documentPath ) )
			{
				// Stale rather than broken, so the file is left alone: the rebuild that follows
				// writes over it. Named as "the key does not match" rather than "the document
				// changed" because the key covers the kernel build and any file the document reads
				// as well, and the honest answer to "why did this stop hitting" is which string it
				// is filed under — printed by `effigy cache <doc>`.
				note = "the key does not match: the document, a file it reads, or the kernel build has changed";
				return false;
			}

			var bodies = ReadBodies( reader );
			var planes = ReadPlanes( reader );
			var warnings = ReadNotes( reader );
			var errors = ReadNotes( reader );

			// Nothing past the last note is read, so trailing bytes are ignored rather than
			// refused: a later format can append a section and this build still opens the file.
			studio.AdoptCachedState( bodies, planes );
			ApplyNotes( studio, warnings, errors );

			note = $"restored {bodies.Count} body(ies)";
			return true;
		}
		catch ( Exception e )
		{
			// A truncated or hand-mangled file. Deleting it is the right answer rather than leaving
			// something that fails every single launch from here on.
			note = Drop( path, e.Message );
			return false;
		}
	}

	/// <summary>
	/// Write what a rebuild produced, so the next launch can skip it.
	///
	/// Called after <see cref="PartStudio.Rebuild"/> and never instead of it. Returns false with a
	/// reason when the document holds something this format cannot carry — see the class note.
	/// </summary>
	public static bool TrySave( PartStudio studio, string documentPath, out string note )
	{
		note = null;

		if ( !Enabled ) { note = "the cache is off"; return false; }

		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		if ( string.IsNullOrWhiteSpace( documentPath ) )
		{
			note = "a cache needs a document path to be found by";
			return false;
		}

		if ( studio.Features.Count == 0 )
		{
			note = "an empty document has nothing worth caching";
			return false;
		}

		if ( !Savable( studio, out note ) )
			return false;

		try
		{
			var path = PathFor( documentPath );
			var directory = Path.GetDirectoryName( path );

			if ( !string.IsNullOrWhiteSpace( directory ) )
				System.IO.Directory.CreateDirectory( directory );

			// Written beside the target and moved over it, so a crash mid-write leaves the previous
			// entry intact rather than half a file that fails to load from then on.
			var temporary = path + ".tmp";

			using ( var stream = File.Create( temporary ) )
			using ( var writer = new BinaryWriter( stream, Encoding.UTF8 ) )
			{
				writer.Write( Magic );
				writer.Write( Version );
				writer.Write( Key( studio, documentPath ) );

				WriteBodies( writer, studio.Bodies );
				WritePlanes( writer, studio.Planes );
				WriteNotes( writer, WarningsFor( studio ) );
				WriteNotes( writer, ErrorsFor( studio ) );
			}

			File.Move( temporary, path, overwrite: true );

			note = $"wrote {studio.Bodies.Count} body(ies)";
			return true;
		}
		catch ( Exception e )
		{
			note = e.Message;
			return false;
		}
	}

	/// <summary>Forget one document's entry. The answer to "this cache is wrong and I want it gone"
	/// without deleting everybody else's.
	///
	/// NOT gated on <see cref="Enabled"/>. Turning the cache off says "do not read or write entries";
	/// it does not say "you may not tidy up", and a clear that quietly did nothing while a file sat
	/// there would report the same thing as a clear that worked.</summary>
	public static bool Delete( string documentPath )
	{
		if ( string.IsNullOrWhiteSpace( documentPath ) )
			return false;

		var path = PathFor( documentPath );

		if ( !File.Exists( path ) )
			return false;

		File.Delete( path );
		return true;
	}

	/// <summary>Where a document's entry lives. Named after the document so a directory listing is
	/// readable, and keyed by its full path so two documents with the same name do not collide.</summary>
	public static string PathFor( string documentPath )
	{
		var full = Path.GetFullPath( documentPath );
		var stem = Path.GetFileNameWithoutExtension( full );
		var key = Hash( "path:" + full ).ToString( "x16" );

		return Path.Combine( Directory, $"{stem}-{key}{Suffix}" );
	}

	// --- what the key is made of --------------------------------------------------------------

	/// <summary>
	/// Everything a rebuild of this document reads.
	///
	/// FNV-1a rather than a framework hash, for the reason <see cref="MultiresSculpt.TopologyId"/>
	/// gives: this value goes in a file and has to mean the same thing in the next process.
	/// </summary>
	public static string Key( PartStudio studio, string documentPath )
	{
		var hash = Hash( "StudioCache" + Version );
		hash = Mix( hash, KernelStamp() );
		hash = Mix( hash, Read( documentPath ) ?? "" );

		foreach ( var path in ExternalFiles( studio, documentPath ) )
			hash = Mix( hash, path + "|" + Stamp( path ) );

		return hash.ToString( "x16" );
	}

	/// <summary>
	/// Which build of the kernel wrote an entry.
	///
	/// THE ASSEMBLY'S MODULE VERSION ID, which .NET derives from the compilation itself — so an
	/// unchanged kernel keeps the same id across a rebuild and a changed one does not. That matters
	/// more than it sounds: stamping the file's write time instead invalidates every entry on the
	/// machine every time the app is rebuilt, which is a 13 second cold load per build, for no
	/// reason at all. A rebuild that changes nothing produces a byte-identical assembly here, and
	/// this is what notices that.
	///
	/// The failure modes are both safe, which is why it is this rather than something cleverer: if
	/// the id ever turned out to be per-compilation rather than per-content, the result is an entry
	/// that is thrown away too often, never one that is kept too long. <see cref="Version"/> above
	/// is the manual override for a host that has no assembly at all.
	/// </summary>
	public static string KernelStamp()
	{
		try
		{
			var assembly = typeof( PartStudio ).Assembly;
			var module = assembly.ManifestModule.ModuleVersionId;

			if ( module != Guid.Empty )
				return $"mvid:{module:N}";

			return "asm:" + (assembly.GetName().Version?.ToString() ?? "unknown");
		}
		catch ( Exception )
		{
			// Not being able to describe the build is not a reason to refuse to load a document; it
			// only makes an entry that little bit more likely to be used when it should not be, and
			// the source text and the file stamps below still cover the document itself.
			return "asm:unknown";
		}
	}

	/// <summary>
	/// Every file a rebuild will reach for, so its stamps can go in the key.
	///
	/// TWO SOURCES, and both are needed. A feature that reads a path holds it in a parameter — an
	/// Import's source OBJ, a Stroke's alpha map — and those are found by looking at every string a
	/// feature carries rather than by a list of which features read files, which would be a list to
	/// forget to update. The sidecar directories beside the document are the other source: an
	/// import whose bytes were saved next to the document is read from there, not from the original
	/// path, and a sculpt's deltas live there too.
	///
	/// OVER-INCLUSIVE ON PURPOSE. A string that happens to name a file but is not an input would
	/// cost a needless rebuild. A file that is an input and is missed would hand back geometry the
	/// document no longer describes, which is the failure this whole file exists to avoid.
	/// </summary>
	static IEnumerable<string> ExternalFiles( PartStudio studio, string documentPath )
	{
		var found = new List<string>();
		var seen = new HashSet<string>( StringComparer.Ordinal );

		var directory = Path.GetDirectoryName( Path.GetFullPath( documentPath ) ) ?? ".";

		foreach ( var feature in studio.Features )
		{
			if ( feature is null )
				continue;

			foreach ( var field in feature.GetType().GetFields( BindingFlags.Public | BindingFlags.Instance ) )
			{
				var text = field.GetValue( feature ) switch
				{
					StringParam parameter => parameter.Value,
					string literal => literal,
					_ => null,
				};

				if ( string.IsNullOrWhiteSpace( text ) )
					continue;

				string path;

				try { path = Path.GetFullPath( text, directory ); }
				catch ( Exception ) { continue; }

				if ( File.Exists( path ) && seen.Add( path ) )
					found.Add( path );
			}
		}

		var stem = Path.GetFileNameWithoutExtension( documentPath );

		foreach ( var suffix in new[] { ImportSidecar.FolderSuffix, SculptBlob.Extension, MeshEditBlob.Extension } )
		{
			var folder = Path.Combine( directory, stem + suffix );

			if ( !System.IO.Directory.Exists( folder ) )
				continue;

			foreach ( var path in System.IO.Directory.GetFiles( folder ).OrderBy( p => p, StringComparer.Ordinal ) )
				if ( seen.Add( path ) )
					found.Add( path );
		}

		found.Sort( StringComparer.Ordinal );
		return found;
	}

	/// <summary>The same stamp ImportFeature puts on its own source: path, length and write time.
	/// Size alone misses an edit that keeps the byte count, and a write time alone misses a file
	/// copied over one that happens to share it.</summary>
	static string Stamp( string path )
	{
		try
		{
			var file = new FileInfo( path );
			return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
		}
		catch ( Exception )
		{
			return "unreadable";
		}
	}

	/// <summary>
	/// Whether this document can be cached at all, and if not, what it carries that this format
	/// does not write.
	///
	/// PUBLIC BECAUSE IT IS THE ANSWER TO A QUESTION A USER ACTUALLY ASKS. A document that
	/// rebuilds on every launch is either uncacheable or has a bug, and without this the two look
	/// identical from the outside. A UI can say which.
	///
	/// Answered before anything is serialised, so the refusal is a clean "no cache for this
	/// document" rather than a file that loaded back missing a shape key.
	/// </summary>
	public static bool Savable( PartStudio studio, out string note )
	{
		note = null;

		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		foreach ( var body in studio.Bodies )
		{
			var mesh = body?.Mesh;

			if ( mesh is null )
				continue;

			if ( mesh.Morphs.Count > 0 )
			{
				note = $"{body.Name ?? body.Id} carries {mesh.Morphs.Count} shape key(s), which a cache cannot yet carry";
				return false;
			}

			if ( mesh.Paint is not null )
			{
				note = $"{body.Name ?? body.Id} carries a paint atlas, which a cache cannot yet carry";
				return false;
			}
		}

		return true;
	}

	// --- writing ------------------------------------------------------------------------------

	static void WriteBodies( BinaryWriter w, List<Body> bodies )
	{
		w.Write( bodies.Count );

		foreach ( var body in bodies )
		{
			// THE READ ORDER IS THIS ORDER. ReadBodies reads the same fields in the same sequence,
			// and the two are meant to be read side by side.
			w.Write( body.Id ?? "" );
			w.Write( body.Name ?? "" );
			WriteMesh( w, body.Mesh );

			w.Write( body.Visible );
			w.Write( body.IsGarment );
			w.Write( body.IsGarmentDetail );
			w.Write( body.IsReference );
			w.Write( body.Layer );
			w.Write( body.LayerAllowance );
			w.Write( body.FeatureId ?? "" );
		}
	}

	static void WriteMesh( BinaryWriter w, PolyMesh mesh )
	{
		// Whether there is a mesh at all, before anything about it. Written as a flag rather than
		// inferred from a zero vertex count, because the two mean different things: a body with no
		// geometry yet is not a body holding an empty mesh.
		w.Write( mesh is not null );

		if ( mesh is null )
			return;

		w.Write( mesh.Positions.Count );

		foreach ( var p in mesh.Positions )
		{
			w.Write( p.x );
			w.Write( p.y );
			w.Write( p.z );
		}

		// Per-vertex colour is written whole or not at all: a mesh whose colour array is the wrong
		// length is the same as one with none, which is what PolyMesh.HasVertexColors says.
		var colors = mesh.HasVertexColors ? mesh.VertexColors : null;
		w.Write( colors is not null );

		if ( colors is not null )
			foreach ( var c in colors )
			{
				w.Write( c.x );
				w.Write( c.y );
				w.Write( c.z );
				w.Write( c.w );
			}

		w.Write( mesh.Faces.Count );

		foreach ( var face in mesh.Faces )
		{
			w.Write( face.Material );
			w.Write( face.Count );

			foreach ( var index in face.Indices )
				w.Write( index );

			foreach ( var uv in face.UVs )
			{
				w.Write( uv.x );
				w.Write( uv.y );
			}
		}

		// IsRigged rather than "Skin is not null": a weight list that does not line up with the
		// positions is not a rig, and writing one would load back as a body that silently lost it.
		var skin = mesh.IsRigged ? mesh.Skin : null;
		w.Write( skin is not null );

		if ( skin is not null )
			foreach ( var weights in skin.Vertices )
			{
				w.Write( weights?.Length ?? 0 );

				foreach ( var weight in weights ?? Array.Empty<BoneWeight>() )
				{
					w.Write( weight.Bone );
					w.Write( weight.Weight );
				}
			}

		w.Write( mesh.Seams.Count );

		foreach ( var seam in mesh.Seams )
		{
			w.Write( seam.A );
			w.Write( seam.B );
		}
	}

	static void WritePlanes( BinaryWriter w, Dictionary<string, SketchPlane> planes )
	{
		var ordered = planes?.Where( p => p.Value is not null )
			.OrderBy( p => p.Key, StringComparer.Ordinal )
			.ToList() ?? new List<KeyValuePair<string, SketchPlane>>();

		w.Write( ordered.Count );

		foreach ( var (key, plane) in ordered )
		{
			w.Write( key );
			Write( w, plane.Origin );
			Write( w, plane.XAxis );
			Write( w, plane.YAxis );
		}
	}

	static void WriteNotes( BinaryWriter w, List<(string FeatureId, string Message)> notes )
	{
		w.Write( notes.Count );

		foreach ( var (featureId, message) in notes )
		{
			w.Write( featureId ?? "" );
			w.Write( message ?? "" );
		}
	}

	static void Write( BinaryWriter w, Vec3 v )
	{
		w.Write( v.x );
		w.Write( v.y );
		w.Write( v.z );
	}

	// --- reading ------------------------------------------------------------------------------

	static List<Body> ReadBodies( BinaryReader r )
	{
		var count = ReadCount( r, "bodies", 17 );
		var bodies = new List<Body>( count );

		for ( var i = 0; i < count; i++ )
		{
			var id = r.ReadString();
			var name = r.ReadString();
			var mesh = ReadMesh( r );

			var visible = r.ReadBoolean();
			var garment = r.ReadBoolean();
			var detail = r.ReadBoolean();
			var reference = r.ReadBoolean();
			var layer = r.ReadInt32();
			var allowance = r.ReadSingle();
			var featureId = r.ReadString();

			bodies.Add( new Body( id, name, mesh )
			{
				Visible = visible,
				IsGarment = garment,
				IsGarmentDetail = detail,
				IsReference = reference,
				Layer = layer,
				LayerAllowance = allowance,
				FeatureId = featureId,
			} );
		}

		return bodies;
	}

	static PolyMesh ReadMesh( BinaryReader r )
	{
		// A body with no mesh comes back with no mesh, rather than with an empty one.
		if ( !r.ReadBoolean() )
			return null;

		var mesh = new PolyMesh();

		var vertices = ReadCount( r, "vertices", 12 );
		mesh.Positions.Capacity = vertices;

		for ( var i = 0; i < vertices; i++ )
			mesh.Positions.Add( new Vec3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ) );

		if ( r.ReadBoolean() )
		{
			var colors = new Vec4[vertices];

			for ( var i = 0; i < vertices; i++ )
				colors[i] = new Vec4( r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() );

			mesh.VertexColors = colors;
		}

		var faces = ReadCount( r, "faces", 8 );
		mesh.Faces.Capacity = faces;

		for ( var i = 0; i < faces; i++ )
		{
			var material = r.ReadInt32();
			var corners = ReadCount( r, "face corners", 4 );
			var indices = new int[corners];

			for ( var c = 0; c < corners; c++ )
			{
				indices[c] = r.ReadInt32();

				// A face pointing outside the vertex list is a corrupt file, and the mesh it would
				// build is one that throws somewhere else entirely, much later, with no clue why.
				if ( indices[c] < 0 || indices[c] >= vertices )
					throw new InvalidDataException( $"face {i} points at vertex {indices[c]} of {vertices}" );
			}

			var uvs = new Vec2[corners];

			for ( var c = 0; c < corners; c++ )
				uvs[c] = new Vec2( r.ReadSingle(), r.ReadSingle() );

			mesh.Faces.Add( new Face( indices, uvs, material ) );
		}

		if ( r.ReadBoolean() )
		{
			var skin = new SkinWeights();

			for ( var i = 0; i < vertices; i++ )
			{
				var influences = ReadCount( r, "influences", 8 );
				var weights = new BoneWeight[influences];

				for ( var w = 0; w < influences; w++ )
				{
					var bone = r.ReadInt32();
					weights[w] = new BoneWeight( bone, r.ReadSingle() );
				}

				skin.Vertices.Add( weights );
			}

			mesh.Skin = skin;
		}

		var seams = ReadCount( r, "seams", 8 );

		for ( var i = 0; i < seams; i++ )
			mesh.Seams.Add( new EdgeKey( r.ReadInt32(), r.ReadInt32() ) );

		return mesh;
	}

	static Dictionary<string, SketchPlane> ReadPlanes( BinaryReader r )
	{
		var count = ReadCount( r, "planes", 22 );
		var planes = new Dictionary<string, SketchPlane>( count, StringComparer.Ordinal );

		for ( var i = 0; i < count; i++ )
		{
			var key = r.ReadString();

			// Through the constructor, which normalises the axes — the only way a plane is ever
			// built in here, and why a stored one comes back identical rather than merely close.
			planes[key] = new SketchPlane(
				new Vec3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
				new Vec3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
				new Vec3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ) );
		}

		return planes;
	}

	static List<(string FeatureId, string Message)> ReadNotes( BinaryReader r )
	{
		var count = ReadCount( r, "notes", 8 );
		var notes = new List<(string, string)>( count );

		for ( var i = 0; i < count; i++ )
			notes.Add( (r.ReadString(), r.ReadString()) );

		return notes;
	}

	/// <summary>
	/// A count off the wire, checked against what is actually left in the file.
	///
	/// A CORRUPT LENGTH MUST NOT BE AN ALLOCATION. The reader would otherwise be asked for a list of
	/// two billion entries by a file of ninety bytes, and the failure would be an out-of-memory
	/// rather than the catchable "this cache is bad, rebuild". Every count in here is followed by at
	/// least <paramref name="bytesEach"/> bytes per entry, so the file length is a hard upper bound
	/// on all of them.
	/// </summary>
	static int ReadCount( BinaryReader r, string what, int bytesEach )
	{
		var count = r.ReadInt32();
		var remaining = r.BaseStream.Length - r.BaseStream.Position;

		if ( count < 0 || (long)count * bytesEach > remaining )
			throw new InvalidDataException( $"{what}: {count} does not fit in the {remaining} byte(s) left" );

		return count;
	}

	// --- notes --------------------------------------------------------------------------------

	/// <summary>
	/// A rebuilt document reports what happened as state ON THE FEATURES, not as a value anybody
	/// keeps — see <see cref="Feature.Warning"/>, which is where ModelMeasure.Describe reads it
	/// from. A restore that skipped the rebuild would otherwise hand back a model with no warnings
	/// on it: the same geometry, and "Hull has no UVs yet" quietly gone.
	/// </summary>
	static void ApplyNotes( PartStudio studio, List<(string FeatureId, string Message)> warnings,
		List<(string FeatureId, string Message)> errors )
	{
		foreach ( var (featureId, message) in errors )
			if ( Find( studio, featureId ) is { } feature )
				feature.Error = message;

		foreach ( var (featureId, message) in warnings )
			if ( Find( studio, featureId ) is { } feature && feature.Error is null )
				feature.Warning = message;
	}

	static Feature Find( PartStudio studio, string featureId ) =>
		string.IsNullOrWhiteSpace( featureId ) ? null : studio.Features.FirstOrDefault( f => f.Id == featureId );

	static List<(string FeatureId, string Message)> WarningsFor( PartStudio studio ) =>
		studio.Features.Where( f => f.Error is null && f.Warning is not null )
			.Select( f => (f.Id, f.Warning) ).ToList();

	static List<(string FeatureId, string Message)> ErrorsFor( PartStudio studio ) =>
		studio.Features.Where( f => f.Error is not null )
			.Select( f => (f.Id, f.Error) ).ToList();

	// --- shared -------------------------------------------------------------------------------

	/// <summary>Remove a file that cannot be read, and hand back what to tell the caller about it.
	/// A cache that fails to load every launch is worse than no cache at all.</summary>
	static string Drop( string path, string why )
	{
		try { File.Delete( path ); }
		catch ( Exception ) { }

		return why;
	}

	static string Read( string path )
	{
		try { return File.Exists( path ) ? File.ReadAllText( path ) : null; }
		catch ( Exception ) { return null; }
	}

	static long Hash( string text ) => Mix( unchecked((long)0xcbf29ce484222325), text );

	/// <summary>FNV-1a over a string's bytes, written out rather than leaning on GetHashCode: this
	/// value is compared against one in a file, in another process, on another runtime.</summary>
	static long Mix( long hash, string text )
	{
		const long prime = 0x100000001b3;

		foreach ( var b in Encoding.UTF8.GetBytes( text ?? "" ) )
		{
			hash ^= b;
			hash = unchecked(hash * prime);
		}

		// A separator, so ("ab","c") and ("a","bc") do not hash the same.
		hash ^= 0x1f;
		return unchecked(hash * prime);
	}
}
