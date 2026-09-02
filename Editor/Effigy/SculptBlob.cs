using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// A sculpt's deltas, as bytes.
///
/// WHY THIS IS NOT IN THE .effigy FILE. Every other piece of a feature is a handful of numbers and
/// goes in the text document, which diffs and which a human can repair. A sculpt is not: at L4 on a
/// 500-face cage it is 128k vertices per level, and written as text it would be tens of megabytes of
/// decimal digits sitting in the middle of a file whose whole virtue is being readable. So the
/// document keeps the feature — its id, its body selection, its place in the history — and the
/// deltas go beside it in a blob keyed by that feature id.
///
/// SIXTEEN BITS PER COMPONENT, AGAINST A PER-LEVEL BOUNDING BOX. Deltas are frame-space coefficients
/// scaled by local edge length, so within one level they occupy a small, similar range and a shared
/// box wastes almost nothing. Six bytes a vertex is the budget the plan was written against: L4 on a
/// 500-face cage is ~750 KB per level. A float32 delta would be twice that for precision far below
/// what a 16-bit step buys — on a bump of one edge length the step is about 3e-5 of an edge, which is
/// nowhere near visible and nowhere near the tolerance anything downstream cares about.
///
/// The box is stored per level rather than globally because levels differ by orders of magnitude:
/// L1 carries the shape, L4 carries pores, and one shared box would spend most of its range on the
/// level that does not need it.
/// </summary>
public static class SculptBlob
{
	/// <summary>Eight bytes so a file can be identified without parsing it.</summary>
	static readonly byte[] Magic = Encoding.ASCII.GetBytes( "EFFIGYSC" );

	/// <summary>Bumped when the layout changes. Refused by name rather than by crash, like the
	/// document format's own version.</summary>
	public const int Version = 1;

	public const string Extension = ".sculpt";

	/// <summary>Bytes per vertex per level: three components, two bytes each.</summary>
	public const int BytesPerVertex = 6;

	const int Steps = 65535;

	/// <summary>
	/// Serialise every level's deltas. Little-endian, via BinaryWriter, which is little-endian on
	/// every runtime this ships to — noted rather than assumed, because a blob is the one thing here
	/// that outlives the process that wrote it.
	/// </summary>
	public static byte[] Write( MultiresSculpt sculpt )
	{
		if ( sculpt is null )
			throw new ArgumentNullException( nameof( sculpt ) );

		using var stream = new MemoryStream();
		using var w = new BinaryWriter( stream, Encoding.ASCII, leaveOpen: true );

		w.Write( Magic );
		w.Write( Version );
		w.Write( MultiresSculpt.TopologyId( sculpt.Cage ) );
		w.Write( sculpt.LevelCount );

		for ( var level = 0; level < sculpt.LevelCount; level++ )
			WriteLayer( w, sculpt.LayerAt( level ) );

		w.Flush();
		return stream.ToArray();
	}

	static void WriteLayer( BinaryWriter w, SculptLayer layer )
	{
		var deltas = layer.Deltas;
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var d in deltas )
		{
			min = new Vec3( MathF.Min( min.x, d.x ), MathF.Min( min.y, d.y ), MathF.Min( min.z, d.z ) );
			max = new Vec3( MathF.Max( max.x, d.x ), MathF.Max( max.y, d.y ), MathF.Max( max.z, d.z ) );
		}

		// An empty level has no box at all. Writing zeroes keeps the record fixed-size, and a zero
		// range reconstructs exactly, which is what an untouched level has to do — a level nobody
		// sculpted must come back bit-identical rather than merely close.
		if ( deltas.Length == 0 )
		{
			min = Vec3.Zero;
			max = Vec3.Zero;
		}

		w.Write( deltas.Length );
		Write( w, min );
		Write( w, max );

		var range = max - min;

		foreach ( var d in deltas )
		{
			w.Write( Quantise( d.x, min.x, range.x ) );
			w.Write( Quantise( d.y, min.y, range.y ) );
			w.Write( Quantise( d.z, min.z, range.z ) );
		}
	}

	static ushort Quantise( float value, float min, float range )
	{
		if ( range <= 0f )
			return 0;

		var t = (value - min) / range;
		return (ushort)Math.Clamp( MathF.Round( t * Steps ), 0f, Steps );
	}

	static float Dequantise( ushort q, float min, float range ) => range <= 0f ? min : min + range * q / Steps;

	/// <summary>
	/// Rebuild a sculpt from its bytes onto a cage.
	///
	/// The cage is required because everything except the deltas is derived — rest surfaces, frames,
	/// the lot — so the blob does not store any of it and cannot be read without one. That is the
	/// same decision as storing deltas in a derived frame, one level up: what is derivable is never
	/// written, so it can never be stale.
	/// </summary>
	public static MultiresSculpt Read( byte[] bytes, PolyMesh cage )
	{
		if ( bytes is null )
			throw new ArgumentNullException( nameof( bytes ) );

		if ( cage is null )
			throw new ArgumentNullException( nameof( cage ) );

		using var stream = new MemoryStream( bytes, writable: false );
		using var r = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );

		if ( bytes.Length < Magic.Length + 16 )
			throw new InvalidOperationException( "This is not a sculpt blob — it is too short to hold a header." );

		var magic = r.ReadBytes( Magic.Length );

		for ( var i = 0; i < Magic.Length; i++ )
		{
			if ( magic[i] != Magic[i] )
				throw new InvalidOperationException( "This is not a sculpt blob — it does not start with EFFIGYSC." );
		}

		var version = r.ReadInt32();

		if ( version > Version )
			throw new InvalidOperationException(
				$"This sculpt was written by a newer build (format {version}; this one reads {Version})." );

		var topology = r.ReadInt64();
		var cageTopology = MultiresSculpt.TopologyId( cage );

		if ( topology != cageTopology )
			throw new InvalidOperationException(
				"This sculpt was made on a different cage. Deltas are stored per vertex, so they cannot "
				+ "be placed on this one. Undo the feature edit that changed the cage's topology, or "
				+ "re-sculpt on the new cage." );

		var levels = r.ReadInt32();

		if ( levels < 1 )
			throw new InvalidOperationException( $"A sculpt has at least the cage level; this blob claims {levels}." );

		var sculpt = new MultiresSculpt( cage );

		for ( var level = 0; level < levels; level++ )
		{
			// Add the level BEFORE reading into it: a level's vertex count depends on the levels
			// below it being displaced first, which is only true once their deltas are in place.
			if ( level > 0 )
				sculpt.AddLevel();

			var expected = sculpt.LayerAt( level ).Count;
			var layer = ReadLayer( r, level, expected );
			sculpt.SetLayer( level, layer );
		}

		sculpt.ViewLevel = sculpt.TopLevel;
		return sculpt;
	}

	static SculptLayer ReadLayer( BinaryReader r, int level, int expected )
	{
		var count = r.ReadInt32();

		if ( count != expected )
			throw new InvalidOperationException(
				$"Level {level} of this sculpt has {count} vertices but the cage produces {expected} there. "
				+ "The blob and the cage do not belong to each other." );

		var min = ReadVec( r );
		var max = ReadVec( r );
		var range = max - min;
		var deltas = new Vec3[count];

		for ( var i = 0; i < count; i++ )
		{
			deltas[i] = new Vec3(
				Dequantise( r.ReadUInt16(), min.x, range.x ),
				Dequantise( r.ReadUInt16(), min.y, range.y ),
				Dequantise( r.ReadUInt16(), min.z, range.z ) );
		}

		return new SculptLayer( deltas );
	}

	static void Write( BinaryWriter w, Vec3 v )
	{
		w.Write( v.x );
		w.Write( v.y );
		w.Write( v.z );
	}

	static Vec3 ReadVec( BinaryReader r ) => new( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() );

	/// <summary>What <see cref="Write"/> will produce, without producing it — for a UI that wants to
	/// say what a level costs on disk before the user commits to it.</summary>
	public static int PredictBytes( MultiresSculpt sculpt )
	{
		if ( sculpt is null )
			throw new ArgumentNullException( nameof( sculpt ) );

		var total = Magic.Length + sizeof( int ) + sizeof( long ) + sizeof( int );

		for ( var level = 0; level < sculpt.LevelCount; level++ )
			total += sizeof( int ) + 6 * sizeof( float ) + sculpt.LayerAt( level ).Count * BytesPerVertex;

		return total;
	}
}

/// <summary>
/// Where the blobs live next to a document.
///
/// One directory beside the .effigy file, one file per sculpt feature, named by that feature's id.
/// Keyed by id rather than by position so re-ordering the history, renaming the feature or deleting
/// the one above it does not shuffle anybody's sculpt onto the wrong feature.
///
/// Saving does not delete blobs it did not write. A file whose feature is gone from the document is
/// the cheapest possible undo of "I deleted the sculpt feature and saved", and the cost of keeping
/// it is a stale file rather than somebody's afternoon. <see cref="Prune"/> exists for when that is
/// actually wanted, and has to be asked for by name.
/// </summary>
public static class SculptSidecar
{
	/// <summary>`model.effigy` keeps its blobs in `model.sculpt/`.</summary>
	public static string DirectoryFor( string documentPath )
	{
		if ( string.IsNullOrWhiteSpace( documentPath ) )
			throw new ArgumentException( "A document path is needed to find its sculpt blobs.", nameof( documentPath ) );

		var dir = Path.GetDirectoryName( documentPath ) ?? "";
		return Path.Combine( dir, Path.GetFileNameWithoutExtension( documentPath ) + SculptBlob.Extension );
	}

	public static string PathFor( string documentPath, string featureId ) =>
		Path.Combine( DirectoryFor( documentPath ), featureId + ".bin" );

	/// <summary>Write a blob for every sculpt feature that has one. Returns how many it wrote.</summary>
	public static int Save( PartStudio studio, string documentPath )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		var pending = new List<(string Id, byte[] Bytes)>();

		foreach ( var feature in studio.Features )
		{
			if ( feature is not SculptFeature sculpt )
				continue;

			var bytes = sculpt.SaveDeltas();

			if ( bytes is not null )
				pending.Add( (feature.Id, bytes) );
		}

		if ( pending.Count == 0 )
			return 0;

		var dir = DirectoryFor( documentPath );
		Directory.CreateDirectory( dir );

		foreach ( var (id, bytes) in pending )
			File.WriteAllBytes( PathFor( documentPath, id ), bytes );

		return pending.Count;
	}

	/// <summary>
	/// Hand each sculpt feature its blob. The bytes are held unread until the next rebuild, because
	/// a blob cannot be turned into a sculpt without the cage and the cage does not exist until the
	/// features above have run.
	/// </summary>
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
			if ( feature is not SculptFeature sculpt )
				continue;

			var path = PathFor( documentPath, feature.Id );

			if ( !File.Exists( path ) )
				continue;

			sculpt.LoadDeltas( File.ReadAllBytes( path ) );
			loaded++;
		}

		return loaded;
	}

	/// <summary>Delete blobs no feature in this studio claims. Destructive, so it is never part of
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
			if ( feature is SculptFeature )
				keep.Add( feature.Id + ".bin" );
		}

		var removed = 0;

		foreach ( var path in Directory.GetFiles( dir, "*.bin" ) )
		{
			if ( keep.Contains( Path.GetFileName( path ) ) )
				continue;

			File.Delete( path );
			removed++;
		}

		return removed;
	}
}
