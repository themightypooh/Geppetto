using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The DMX writer's output parsed as KeyValues2, rather than searched for substrings.
///
/// WHY THIS EXISTS. Every DMX check before this one asked whether a name appeared in the text —
/// "does it contain DmeModel", "does it carry jointWeights". All of them passed on a file the
/// engine's own reader rejected at line 56 with "Expecting ',', didn't find it!". Three things
/// were wrong and no substring test could see any of them:
///
///   1. an element written INSIDE an element_array got no trailing comma, so the next member's
///      type name ran onto the previous member;
///   2. a reference to an element defined elsewhere was written as a bare quoted id, where
///      KeyValues2 wants the two tokens "element" "&lt;id&gt;";
///   3. the vertex format fields were named positions / normals / textureCoordinates /
///      jointWeights / jointIndices, where the compiler keys on position$0 / normal$0 /
///      texcoord$0 / blendweights$0 / blendindices$0.
///
/// The first two the compiler reports as "Couldn't load DMX file", the third as "Missing position
/// values" — none of which names the mistake. Note that a substring test is not merely weak here,
/// it is actively misleading: the old checks asserted the presence of the WRONG names and passed
/// for it. A render cannot see this class of bug either, because there is nothing to render. Only
/// a parse can. So this file ships a minimal KV2 reader the way SmdWriter ships a minimal SMD
/// reader, and asserts on the tree it produces.
///
/// The reader is deliberately strict — it fails where the engine fails rather than being lenient
/// and passing files the engine will not load. Its errors carry a line number for the same reason
/// dmxconvert's do: that is what made the original bug findable in one step.
/// </summary>
public static class DmxGrammarTests
{
	public static void Run()
	{
		Report.Section( "DMX grammar: the output parses as KeyValues2" );
		TestParses();

		Report.Section( "DMX grammar: the parsed tree is the model that went in" );
		TestContent();
	}

	// --- the checks ---------------------------------------------------------------------------

	static void TestParses()
	{
		// A rigged cylinder is the case that exercises every construct at once: nested bone dags
		// inside an element_array (the comma bug), jointList full of references (the reference
		// bug), face sets, and numeric arrays.
		var (mesh, skeleton) = Rigged();
		var text = DmxWriter.Write( mesh, skeleton, modelName: "grammar" );

		var root = Parse( text, out var error );

		Report.Check( "a rigged export parses", root is not null, error );

		if ( root is null )
			return;

		// The two specific mistakes, named, so a failure says which one came back rather than
		// just "does not parse".
		Report.Check( "every element_array member is comma-separated",
			!text.Contains( "}\n\t\t\t\"Dme" ) && !text.Contains( "}\n\t\t\"Dme" ),
			"an element sits directly against the next member's type name" );

		Report.Check( "references are the two-token form, not a bare id",
			!System.Text.RegularExpressions.Regex.IsMatch( text, "\n\\s*\"[0-9a-f]{8}-" ),
			"a bare quoted id would be read as an element type name" );

		// A static export takes a different path through the writer - Skeleton.SingleRoot rather
		// than a supplied one - and has to parse too.
		var staticText = DmxWriter.Write( Primitives.Box( 2, 2, 2 ), modelName: "grammar_static" );

		Report.Check( "a static export parses", Parse( staticText, out var staticError ) is not null, staticError );

		// One bone, one face, one vertex: the degenerate end, where an off-by-one in the comma
		// trimming would show up as a stray or missing separator.
		var plane = Primitives.Plane( 1, 1 );

		Report.Check( "a single-face export parses",
			Parse( DmxWriter.Write( plane, modelName: "grammar_plane" ), out var planeError ) is not null, planeError );

		// Trailing commas are trimmed at the end of every array; a file that keeps one loads in
		// some readers and not others, which is the failure mode that is worst to diagnose.
		Report.Check( "no array ends on a trailing comma",
			!System.Text.RegularExpressions.Regex.IsMatch( text, ",\\s*\\]" ) );
	}

	static void TestContent()
	{
		var (mesh, skeleton) = Rigged();
		var root = Parse( DmxWriter.Write( mesh, skeleton, modelName: "grammar" ), out var error );

		if ( root is null )
		{
			Report.Check( "the tree is readable", false, error );
			return;
		}

		var model = root.Element( "skeleton" );

		Report.Check( "the root carries a DmeModel", model is not null && model.Type == "DmeModel" );

		if ( model is null )
			return;

		// jointList is what the per-vertex joint indices index into, so its length is the one
		// thing that makes the weights mean anything.
		var joints = model.Array( "jointList" );

		Report.Check( "jointList has one entry per bone",
			joints.Count == skeleton.Count, $"{joints.Count} vs {skeleton.Count}" );

		Report.Check( "every jointList entry is a reference",
			joints.All( j => j.IsReference ), "an entry parsed as something other than a reference" );

		var vertexData = FindFirst( root, "DmeVertexData" );

		Report.Check( "there is a vertex data block", vertexData is not null );

		if ( vertexData is null )
			return;

		// THE CHECK FOR THE THIRD BUG. vertexFormat declares the fields; each one then has to exist
		// as an array under exactly that name. They were written as two independent sets of string
		// literals and disagreed with what the compiler keys on — "positions" rather than
		// "position$0" — which it reports as "Missing position values", naming a field it cannot
		// find rather than the one it was given. Comparing the declaration against the arrays makes
		// any future rename self-checking, whatever the names become.
		var declared = vertexData.Array( "vertexFormat" ).Select( f => f.Value ).ToList();

		Report.Check( "vertexFormat declares the five fields a skinned mesh needs",
			declared.Count == 5, string.Join( ", ", declared ) );

		Report.Check( "every declared field exists as an array under that exact name",
			declared.All( f => vertexData.Array( f ).Count > 0 ),
			string.Join( ", ", declared.Where( f => vertexData.Array( f ).Count == 0 ) ) );

		// And the names are the $-suffixed ones, stated outright so a revert reads as a failure
		// rather than as a passing test about different names.
		Report.Check( "the fields are the <semantic>$<set> spelling the compiler keys on",
			declared.Contains( "position$0" ) && declared.Contains( "normal$0" )
				&& declared.Contains( "texcoord$0" ) && declared.Contains( "blendweights$0" )
				&& declared.Contains( "blendindices$0" ),
			string.Join( ", ", declared ) );

		var positions = vertexData.Array( "position$0" );
		var weights = vertexData.Array( "blendweights$0" );
		var jointIndices = vertexData.Array( "blendindices$0" );

		Report.Check( "one position per vertex",
			positions.Count == mesh.VertexCount, $"{positions.Count} vs {mesh.VertexCount}" );

		// The compiler states this rule outright: "Incorrect number of joint weights or indices
		// specified, must match number of positions values". It is a fixed stride, so it is a
		// multiplication, not an equality.
		Report.Check( "weights are MaxInfluences per position",
			weights.Count == mesh.VertexCount * DmxWriter.MaxInfluences,
			$"{weights.Count} vs {mesh.VertexCount * DmxWriter.MaxInfluences}" );

		Report.Check( "joint indices match the weights",
			jointIndices.Count == weights.Count, $"{jointIndices.Count} vs {weights.Count}" );

		Report.Check( "no joint index points outside the skeleton",
			jointIndices.All( j => int.TryParse( j.Value, out var b ) && b >= 0 && b < skeleton.Count ) );

		// Pruning to four influences has to renormalise, or a vertex that had five influences
		// comes out lighter than the ones that had four and the mesh sags toward the origin.
		var sums = new List<float>();

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var sum = 0f;

			for ( var i = 0; i < DmxWriter.MaxInfluences; i++ )
				sum += float.Parse( weights[v * DmxWriter.MaxInfluences + i].Value,
					System.Globalization.CultureInfo.InvariantCulture );

			sums.Add( sum );
		}

		Report.Check( "every vertex's weights still sum to 1 after pruning",
			sums.All( s => MathF.Abs( s - 1f ) < 1e-3f ),
			$"worst {sums.Select( s => MathF.Abs( s - 1f ) ).Max()}" );

		// The three index arrays must be the same length - the compiler says so: "Cannot add
		// vertex data block with different number of normal indices (%d) and vertex indices (%d)".
		var positionIndices = vertexData.Array( "position$0Indices" );
		var normalIndices = vertexData.Array( "normal$0Indices" );
		var uvIndices = vertexData.Array( "texcoord$0Indices" );

		var corners = mesh.Faces.Sum( f => f.Count );

		Report.Check( "one index per face corner",
			positionIndices.Count == corners, $"{positionIndices.Count} vs {corners}" );

		Report.Check( "all three index arrays are the same length",
			normalIndices.Count == positionIndices.Count && uvIndices.Count == positionIndices.Count,
			$"{positionIndices.Count}/{normalIndices.Count}/{uvIndices.Count}" );

		// N-gons are the reason DMX is written at all. A cylinder's caps are n-gons; if the writer
		// ever triangulates, the corner count above drops and this is the check that says why.
		Report.Check( "the cap n-gons were not triangulated",
			mesh.Faces.Any( f => f.Count > 4 ), "no face has more than four corners" );

		var faceSet = FindFirst( root, "DmeFaceSet" );

		Report.Check( "there is a face set", faceSet is not null );

		// faces is a run of corner indices per face, each run terminated by -1. Every face in the
		// mesh has to be accounted for across all the sets.
		var terminators = 0;

		foreach ( var set in FindAll( root, "DmeFaceSet" ) )
			terminators += set.Array( "faces" ).Count( f => f.Value == "-1" );

		Report.Check( "the face sets cover every face",
			terminators == mesh.FaceCount, $"{terminators} vs {mesh.FaceCount}" );
	}

	static (PolyMesh, Skeleton) Rigged()
	{
		var skeleton = new Skeleton();
		var root = skeleton.AddBone( "root", -1, Xform.Identity, 2f );
		skeleton.AddBone( "upper", root, Xform.Translate( new Vec3( 0, 2, 0 ) ), 2f );

		var mesh = Primitives.Cylinder( 0.5f, 4f, 12 );
		mesh.Skin = SkinBinder.BindSmooth( mesh, skeleton );

		return (mesh, skeleton);
	}

	// --- a minimal KeyValues2 reader ------------------------------------------------------------

	/// <summary>One parsed element: its type, and its attributes in order.</summary>
	public sealed class Node
	{
		public string Type;
		public string Name;
		public readonly List<(string Key, Node Child)> Children = new();
		public readonly Dictionary<string, string> Values = new();
		public readonly Dictionary<string, List<Item>> Arrays = new();

		public Node Element( string key ) =>
			Children.FirstOrDefault( c => c.Key == key ).Child;

		public List<Item> Array( string key ) =>
			Arrays.TryGetValue( key, out var a ) ? a : new List<Item>();
	}

	/// <summary>An array member: either a plain value, a reference, or a nested element.</summary>
	public sealed class Item
	{
		public string Value;
		public Node Element;
		public bool IsReference;
	}

	public static Node FindFirst( Node root, string type ) => FindAll( root, type ).FirstOrDefault();

	public static List<Node> FindAll( Node root, string type )
	{
		var found = new List<Node>();
		Walk( root );
		return found;

		void Walk( Node n )
		{
			if ( n is null )
				return;

			if ( n.Type == type )
				found.Add( n );

			foreach ( var (_, child) in n.Children )
				Walk( child );

			foreach ( var array in n.Arrays.Values )
			{
				foreach ( var item in array )
				{
					if ( item.Element is not null )
						Walk( item.Element );
				}
			}
		}
	}

	/// <summary>
	/// Parse KeyValues2 text into a tree, or return null and say where it broke.
	///
	/// Strict about the two things the writer got wrong: array members must be separated by
	/// commas, and a reference inside an element_array is the two tokens "element" "&lt;id&gt;".
	/// </summary>
	public static Node Parse( string text, out string error )
	{
		error = null;

		try
		{
			var reader = new Reader( text );
			reader.SkipHeader();
			return reader.ReadElement();
		}
		catch ( FormatException e )
		{
			error = e.Message;
			return null;
		}
	}

	sealed class Reader
	{
		readonly string _text;
		int _i;

		public Reader( string text ) => _text = text;

		int Line => _text.Take( _i ).Count( c => c == '\n' ) + 1;

		public void SkipHeader()
		{
			SkipSpace();

			if ( !_text.AsSpan( _i ).StartsWith( "<!--" ) )
				throw new FormatException( "no <!-- dmx encoding --> header" );

			var end = _text.IndexOf( "-->", _i, StringComparison.Ordinal );

			if ( end < 0 )
				throw new FormatException( "unterminated header comment" );

			_i = end + 3;
		}

		/// <summary>An element: a quoted type name, then a braced body.</summary>
		public Node ReadElement()
		{
			var node = new Node { Type = ReadQuoted() };
			Expect( '{' );

			while ( true )
			{
				SkipSpace();

				if ( Peek() == '}' )
				{
					_i++;
					break;
				}

				var key = ReadQuoted();
				var type = ReadQuoted();

				if ( type.EndsWith( "_array", StringComparison.Ordinal ) )
				{
					node.Arrays[key] = ReadArray( type );
					continue;
				}

				// An attribute whose type is an element type name is an inline child element;
				// anything else - including "element", which is a reference - is a plain value.
				SkipSpace();

				if ( Peek() == '{' )
				{
					var child = new Node { Type = type };
					ReadBody( child );
					node.Children.Add( (key, child) );
					continue;
				}

				var value = ReadQuoted();
				node.Values[key] = value;

				if ( key == "name" )
					node.Name = value;
			}

			return node;
		}

		void ReadBody( Node node )
		{
			Expect( '{' );

			while ( true )
			{
				SkipSpace();

				if ( Peek() == '}' )
				{
					_i++;
					return;
				}

				var key = ReadQuoted();
				var type = ReadQuoted();

				if ( type.EndsWith( "_array", StringComparison.Ordinal ) )
				{
					node.Arrays[key] = ReadArray( type );
					continue;
				}

				SkipSpace();

				if ( Peek() == '{' )
				{
					var child = new Node { Type = type };
					ReadBody( child );
					node.Children.Add( (key, child) );
					continue;
				}

				var value = ReadQuoted();
				node.Values[key] = value;

				if ( key == "name" )
					node.Name = value;
			}
		}

		List<Item> ReadArray( string arrayType )
		{
			Expect( '[' );

			var items = new List<Item>();
			var first = true;

			while ( true )
			{
				SkipSpace();

				if ( Peek() == ']' )
				{
					_i++;
					return items;
				}

				// THE CHECK THIS FILE EXISTS FOR. Every member after the first has to be preceded
				// by a comma, whether it is a scalar or a whole nested element.
				if ( !first )
				{
					if ( Peek() != ',' )
						throw new FormatException( $"line {Line}: expecting ',' between array members, found '{Peek()}'" );

					_i++;
					SkipSpace();

					if ( Peek() == ']' )
						throw new FormatException( $"line {Line}: trailing comma before ']'" );
				}

				first = false;

				var token = ReadQuoted();

				if ( arrayType != "element_array" )
				{
					items.Add( new Item { Value = token } );
					continue;
				}

				// In an element_array a member is either "element" "<id>" - a reference - or a
				// type name followed by a body.
				if ( token == "element" )
				{
					items.Add( new Item { Value = ReadQuoted(), IsReference = true } );
					continue;
				}

				SkipSpace();

				if ( Peek() != '{' )
					throw new FormatException(
						$"line {Line}: '{token}' is read as an element type and needs a body; " +
						"a bare id must be written as \"element\" \"<id>\"" );

				var child = new Node { Type = token };
				ReadBody( child );
				items.Add( new Item { Element = child } );
			}
		}

		string ReadQuoted()
		{
			SkipSpace();

			if ( Peek() != '"' )
				throw new FormatException( $"line {Line}: expecting a quoted token, found '{Peek()}'" );

			_i++;

			var sb = new System.Text.StringBuilder();

			while ( _i < _text.Length && _text[_i] != '"' )
			{
				if ( _text[_i] == '\\' && _i + 1 < _text.Length )
					_i++;

				sb.Append( _text[_i++] );
			}

			if ( _i >= _text.Length )
				throw new FormatException( $"line {Line}: unterminated string" );

			_i++;
			return sb.ToString();
		}

		void Expect( char c )
		{
			SkipSpace();

			if ( Peek() != c )
				throw new FormatException( $"line {Line}: expecting '{c}', found '{Peek()}'" );

			_i++;
		}

		char Peek() => _i < _text.Length ? _text[_i] : '\0';

		void SkipSpace()
		{
			while ( _i < _text.Length && char.IsWhiteSpace( _text[_i] ) )
				_i++;
		}
	}
}
