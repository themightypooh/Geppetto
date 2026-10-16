using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// Binary FBX 7.4, made from the ASCII <see cref="FbxWriter"/> writes.
///
/// WHY BOTH. The engine's fbx2dmx reads ASCII happily and ASCII is the one you can read and diff, so
/// it stays what the compile path and the tests use. Blender's importer refuses ASCII FBX outright
/// ("ASCII FBX files are not supported"), and Blender is where an exported model is most likely
/// going, so File ▸ Export FBX writes this.
///
/// WHY A CONVERTER AND NOT A SECOND WRITER. The ASCII writer is the one that knows what an FBX
/// scene has to contain, and it is tested and checked against fbx2dmx. Re-serialising its output
/// means the binary file says exactly what the ASCII one says, and there is one place to fix a
/// scene rather than two that drift apart.
///
/// WHAT ASCII LOSES AND THIS PUTS BACK: types. Binary values are typed (int32, int64, double,
/// string, typed arrays) and importers check them — Blender asserts that a `P` integer is an int32
/// and a `P` number a double. So the type comes from where a value sits: object ids and
/// connection ends are int64, a `P`'s values follow the type the `P` declares, a geometry array is
/// doubles or int32 by its name, and anything else is an int32 unless it has a decimal point.
/// Object names change shape too: ASCII's `Geometry::mesh` is `mesh\0\x01Geometry` in binary.
///
/// Only the subset FbxText writes is understood — one node or one array per line, `;` comments.
/// It is not a general ASCII FBX reader and is not meant to be.
/// </summary>
public static class FbxBinary
{
	const int Version = 7400;

	static readonly byte[] HeadMagic = Encoding.ASCII.GetBytes( "Kaydara FBX Binary  \0\x1a\0" );

	static readonly byte[] FootId =
	{
		0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e
	};

	static readonly byte[] FootMagic =
	{
		0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b
	};

	/// <summary>Arrays of doubles. Every other array FbxWriter writes is int32 indices.</summary>
	static readonly HashSet<string> DoubleArrays = new()
	{
		"Vertices", "Normals", "UV", "Weights", "Transform", "TransformLink", "Matrix", "Binormals", "Tangents", "Colors", "FullWeights",
	};

	/// <summary>`P` types whose values are int32, per the FBX SDK's property types.</summary>
	static readonly HashSet<string> IntProperties = new( StringComparer.OrdinalIgnoreCase )
	{
		"int", "Integer", "enum", "bool", "Bool", "Visibility Inheritance",
	};

	sealed class Node
	{
		public string Name;
		public readonly List<object> Props = new();
		public readonly List<Node> Children = new();
		public bool HasBlock;
	}

	/// <summary>A single raw char, the binary type 'C' — ASCII's bare `T` and `F`.</summary>
	readonly struct Flag
	{
		public readonly bool Value;
		public Flag( bool value ) => Value = value;
	}

	public static void WriteFile( string ascii, string path ) => File.WriteAllBytes( path, FromAscii( ascii ) );

	public static byte[] FromAscii( string ascii )
	{
		var root = Parse( ascii );

		using var stream = new MemoryStream();
		using var w = new BinaryWriter( stream, Encoding.UTF8 );

		w.Write( HeadMagic );
		w.Write( (uint)Version );

		foreach ( var node in root.Children )
			WriteNode( w, node );

		w.Write( new byte[13] );

		// The footer, the way Blender's own writer lays it out. Readers find the end of the nodes
		// by the null record above, so this is courtesy rather than structure.
		w.Write( FootId );
		w.Write( new byte[4] );
		var pad = (int)(((stream.Position + 15) & ~15) - stream.Position);
		w.Write( new byte[pad == 0 ? 16 : pad] );
		w.Write( (uint)Version );
		w.Write( new byte[120] );
		w.Write( FootMagic );

		w.Flush();
		return stream.ToArray();
	}

	// --- reading the ASCII --------------------------------------------------------------------

	static Node Parse( string ascii )
	{
		var root = new Node { Name = "" };
		var stack = new Stack<Node>();
		stack.Push( root );

		Node arrayNode = null;
		StringBuilder arrayText = null;

		foreach ( var raw in ascii.Split( '\n' ) )
		{
			var line = StripComment( raw ).Trim();

			if ( line.Length == 0 )
				continue;

			if ( line == "}" )
			{
				if ( arrayNode is not null )
				{
					if ( arrayText is not null )
						arrayNode.Props.Add( ParseArray( arrayNode.Name, arrayText.ToString() ) );

					arrayNode = null;
					arrayText = null;
					continue;
				}

				if ( stack.Count <= 1 )
					throw new FormatException( "FBX has a closing brace with no block open" );

				stack.Pop();
				continue;
			}

			if ( arrayNode is not null )
			{
				// The `a: v,v,v` inside `Name: *N {`. FbxText writes it on one line; other
				// exporters wrap a long array over many, so continuation lines are gathered until
				// the closing brace and parsed as one.
				if ( line.StartsWith( "a:" ) )
					arrayText = new StringBuilder( line[2..] );
				else if ( arrayText is not null )
					arrayText.Append( arrayText.Length > 0 && arrayText[^1] != ',' && line[0] != ',' ? "," : "" ).Append( line );

				continue;
			}

			var colon = line.IndexOf( ':' );
			if ( colon <= 0 )
				throw new FormatException( $"FBX line without a name: {line}" );

			var node = new Node { Name = line[..colon] };
			var rest = line[(colon + 1)..].Trim();
			var opens = rest.EndsWith( "{" );

			if ( opens )
				rest = rest[..^1].TrimEnd().TrimEnd( ',' ).TrimEnd();

			var parent = stack.Peek();
			parent.Children.Add( node );

			if ( rest.StartsWith( "*" ) && opens )
			{
				arrayNode = node;
				continue;
			}

			ParseProps( node, parent, rest );

			if ( opens )
			{
				node.HasBlock = true;
				stack.Push( node );
			}
		}

		return root;
	}

	static string StripComment( string line )
	{
		var quoted = false;

		for ( var i = 0; i < line.Length; i++ )
		{
			if ( line[i] == '"' )
				quoted = !quoted;
			else if ( line[i] == ';' && !quoted )
				return line[..i];
		}

		return line;
	}

	static List<string> SplitProps( string text )
	{
		var tokens = new List<string>();
		var sb = new StringBuilder();
		var quoted = false;

		foreach ( var c in text )
		{
			if ( c == '"' )
				quoted = !quoted;

			if ( c == ',' && !quoted )
			{
				tokens.Add( sb.ToString().Trim() );
				sb.Clear();
				continue;
			}

			sb.Append( c );
		}

		if ( sb.Length > 0 || tokens.Count > 0 )
			tokens.Add( sb.ToString().Trim() );

		return tokens;
	}

	static void ParseProps( Node node, Node parent, string text )
	{
		if ( text.Length == 0 )
			return;

		var tokens = SplitProps( text );
		var underObjects = parent.Name == "Objects";

		for ( var i = 0; i < tokens.Count; i++ )
		{
			var t = tokens[i];

			if ( t.Length >= 2 && t[0] == '"' && t[^1] == '"' )
			{
				var s = t[1..^1];

				// ASCII `Class::name` is binary `name\0\x01Class`, on an object's name.
				if ( underObjects && i == 1 && s.IndexOf( "::", StringComparison.Ordinal ) is var sep and > 0 )
					s = s[(sep + 2)..] + "\0\x01" + s[..sep];

				node.Props.Add( s );
				continue;
			}

			if ( t is "T" or "Y" ) { node.Props.Add( new Flag( true ) ); continue; }
			if ( t is "F" or "N" ) { node.Props.Add( new Flag( false ) ); continue; }

			node.Props.Add( Number( node, underObjects, i, t ) );
		}
	}

	static object Number( Node node, bool underObjects, int index, string t )
	{
		var hasPoint = t.IndexOfAny( new[] { '.', 'e', 'E' } ) >= 0;

		// A P's values follow the type it declares in its second field.
		if ( node.Name == "P" && index >= 4 && node.Props.Count > 1 && node.Props[1] is string type )
		{
			if ( IntProperties.Contains( type ) )
				return (int)double.Parse( t, CultureInfo.InvariantCulture );

			if ( type is "KTime" or "ULongLong" )
				return long.Parse( t, CultureInfo.InvariantCulture );

			return double.Parse( t, CultureInfo.InvariantCulture );
		}

		var isId = (underObjects && index == 0)
			|| (node.Name == "C" && index is 1 or 2)
			|| node.Name is "RootNode" or "Node"
			|| (node.Name == "Document" && index == 0);

		if ( isId )
			return long.Parse( t, CultureInfo.InvariantCulture );

		if ( hasPoint || node.Name == "Link_DeformAcuracy" )
			return double.Parse( t, CultureInfo.InvariantCulture );

		var value = long.Parse( t, CultureInfo.InvariantCulture );
		return value is >= int.MinValue and <= int.MaxValue ? (int)value : value;
	}

	static object ParseArray( string name, string values )
	{
		var parts = values.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

		// An array of a name this does not know is doubles when it is written as decimals - a
		// NormalsW or a BinormalsW from another exporter - rather than a parse failure.
		if ( DoubleArrays.Contains( name ) || Array.Exists( parts, p => p.IndexOfAny( new[] { '.', 'e', 'E' } ) >= 0 ) )
		{
			var d = new double[parts.Length];
			for ( var i = 0; i < parts.Length; i++ )
				d[i] = double.Parse( parts[i], CultureInfo.InvariantCulture );
			return d;
		}

		var n = new int[parts.Length];
		for ( var i = 0; i < parts.Length; i++ )
			n[i] = int.Parse( parts[i], CultureInfo.InvariantCulture );
		return n;
	}

	// --- writing the binary -------------------------------------------------------------------

	static void WriteNode( BinaryWriter w, Node node )
	{
		var stream = w.BaseStream;
		var start = stream.Position;

		w.Write( 0u );                       // end offset, patched below
		w.Write( (uint)node.Props.Count );
		w.Write( 0u );                       // property list length, patched below

		var name = Encoding.ASCII.GetBytes( node.Name );
		w.Write( (byte)name.Length );
		w.Write( name );

		var propsStart = stream.Position;
		foreach ( var prop in node.Props )
			WriteProp( w, prop );
		var propsLength = stream.Position - propsStart;

		// Children end with a 13-byte null record. So does a node with nothing at all, the way the
		// SDK writes `References: { }` — readers accept it either way, and it matches what they expect.
		if ( node.Children.Count > 0 )
		{
			foreach ( var child in node.Children )
				WriteNode( w, child );

			w.Write( new byte[13] );
		}
		else if ( node.Props.Count == 0 )
		{
			w.Write( new byte[13] );
		}

		var end = stream.Position;
		stream.Position = start;
		w.Write( (uint)end );
		w.Write( (uint)node.Props.Count );
		w.Write( (uint)propsLength );
		stream.Position = end;
	}

	static void WriteProp( BinaryWriter w, object prop )
	{
		switch ( prop )
		{
			case Flag f:
				w.Write( (byte)'C' );
				w.Write( (byte)(f.Value ? 1 : 0) );
				return;

			case int i:
				w.Write( (byte)'I' );
				w.Write( i );
				return;

			case long l:
				w.Write( (byte)'L' );
				w.Write( l );
				return;

			case double d:
				w.Write( (byte)'D' );
				w.Write( d );
				return;

			case string s:
				var bytes = Encoding.UTF8.GetBytes( s );
				w.Write( (byte)'S' );
				w.Write( (uint)bytes.Length );
				w.Write( bytes );
				return;

			case double[] da:
				w.Write( (byte)'d' );
				w.Write( (uint)da.Length );
				w.Write( 0u );
				w.Write( (uint)(da.Length * 8) );
				foreach ( var v in da )
					w.Write( v );
				return;

			case int[] ia:
				w.Write( (byte)'i' );
				w.Write( (uint)ia.Length );
				w.Write( 0u );
				w.Write( (uint)(ia.Length * 4) );
				foreach ( var v in ia )
					w.Write( v );
				return;

			default:
				throw new InvalidOperationException( $"No binary FBX type for {prop?.GetType().Name ?? "null"}" );
		}
	}
}
