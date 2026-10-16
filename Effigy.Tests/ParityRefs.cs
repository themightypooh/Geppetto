using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Effigy.Tests;

/// <summary>
/// The Blender parity references, cached in the repository (Effigy.Tests/parity-refs) so the
/// scoreboard and its regression tests run without Blender installed. Each file holds one case's
/// measured displacement field: only the vertices Blender moved, in millionths of an inch, gzipped.
///
/// A REFERENCE IS ONLY GOOD FOR THE JOB THAT MADE IT. Its first line carries a signature of
/// everything that went into the Blender run — brush, strength, radius, stroke points, auto-mask
/// settings, filter, test mesh — and a cached file whose signature no longer matches the case is
/// ignored rather than trusted, so editing a case can never silently score against a stale field.
/// Refresh them with Blender on PATH: <c>dotnet run -c Release -- --parity</c> (missing or stale
/// references are re-measured in one Blender launch and written back here).
/// </summary>
public static class ParityRefs
{
	public static string Dir( [CallerFilePath] string thisFile = "" ) =>
		Path.Combine( Path.GetDirectoryName( thisFile ) ?? ".", "parity-refs" );

	static string FileFor( Parity.Case c ) => Path.Combine( Dir(), c.File + ".ref.gz" );

	/// <summary>Everything that decides what Blender produces for a case, hashed.</summary>
	public static string Signature( Parity.Case c, PolyMesh surface )
	{
		var path = Parity.StrokePath( c );
		var sb = new StringBuilder();
		sb.Append( c.Blender ).Append( '|' ).Append( c.Kind ).Append( '|' ).Append( c.Mesh ).Append( '|' )
			.Append( Num( c.Radius ) ).Append( '|' ).Append( Num( c.Strength ) ).Append( '|' ).Append( c.Invert ).Append( '|' )
			.Append( c.Alpha ).Append( '|' ).Append( c.BlenderFilter ).Append( '|' )
			.Append( JsonSerializer.Serialize( c.Auto ?? new Dictionary<string, object>() ) ).Append( '|' );
		foreach ( var p in path ) sb.Append( Num( p.x ) ).Append( ',' ).Append( Num( p.y ) ).Append( ',' ).Append( Num( p.z ) ).Append( ';' );
		sb.Append( '|' ).Append( surface.VertexCount ).Append( '|' );
		foreach ( var p in surface.Positions ) sb.Append( Num( p.x ) ).Append( Num( p.y ) ).Append( Num( p.z ) );
		var hash = SHA256.HashData( Encoding.UTF8.GetBytes( sb.ToString() ) );
		return Convert.ToHexString( hash, 0, 8 ).ToLowerInvariant();
	}

	static string Num( float v ) => v.ToString( "0.#####", CultureInfo.InvariantCulture );

	/// <summary>The cached reference positions for a case, or null when there is none or it is stale.</summary>
	public static Vec3[] Load( Parity.Case c, PolyMesh surface ) => Load( c, surface, out _ );

	public static Vec3[] Load( Parity.Case c, PolyMesh surface, out string source )
	{
		source = null;
		var file = FileFor( c );
		if ( !File.Exists( file ) ) return null;
		using var gz = new GZipStream( File.OpenRead( file ), CompressionMode.Decompress );
		using var reader = new StreamReader( gz );
		var head = (reader.ReadLine() ?? "").Split( ' ' );
		// parity-ref 1 <signature> <vertex count> <made by…>
		if ( head.Length < 4 || head[0] != "parity-ref" || head[2] != Signature( c, surface ) || head[3] != surface.VertexCount.ToString( CultureInfo.InvariantCulture ) )
			return null;
		source = string.Join( ' ', head.Skip( 4 ) );
		var result = surface.Positions.ToArray();
		string line;
		while ( (line = reader.ReadLine()) is not null )
		{
			var s = line.Split( ' ' );
			if ( s.Length < 4 ) continue;
			var i = int.Parse( s[0], CultureInfo.InvariantCulture );
			result[i] += new Vec3( int.Parse( s[1], CultureInfo.InvariantCulture ), int.Parse( s[2], CultureInfo.InvariantCulture ), int.Parse( s[3], CultureInfo.InvariantCulture ) ) * 1e-6f;
		}
		return result;
	}

	/// <summary>Cache a freshly measured reference.</summary>
	public static void Save( Parity.Case c, PolyMesh surface, Vec3[] reference, string madeBy )
	{
		Directory.CreateDirectory( Dir() );
		using var gz = new GZipStream( File.Create( FileFor( c ) ), CompressionLevel.SmallestSize );
		using var writer = new StreamWriter( gz ) { NewLine = "\n" };
		writer.WriteLine( $"parity-ref 1 {Signature( c, surface )} {surface.VertexCount} {madeBy}" );
		for ( var i = 0; i < reference.Length; i++ )
		{
			var d = (reference[i] - surface.Positions[i]) * 1e6f;
			var (x, y, z) = ((int)MathF.Round( d.x ), (int)MathF.Round( d.y ), (int)MathF.Round( d.z ));
			if ( x == 0 && y == 0 && z == 0 ) continue;
			writer.WriteLine( FormattableString.Invariant( $"{i} {x} {y} {z}" ) );
		}
	}
}
