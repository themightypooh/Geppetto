using System;
using System.Collections.Generic;
using System.Text;

namespace Effigy;

/// <summary>
/// The MaterialGroupList node a .vmdl needs so faces keep the materials they were given.
///
/// WHAT THIS CLOSES. Faces carry a slot number and PartStudio.MaterialNames binds that number to a
/// vmat. Every exporter writes the name — OBJ as <c>usemtl</c>, DMX as <c>mtlName</c>, SMD as the
/// triangle's material line — and that was as far as it went. The .vmdl Compile writes had no
/// MaterialGroupList at all. ModelDoc fills a missing one in with
/// <c>use_global_default = true</c> and <c>materials/default.vmat</c>, which is why a part that
/// rendered in brushed steel in the viewport compiled to a blank grey prop.
///
/// COPIED, NOT GUESSED. The remap shape is the one this project's own lightswitch and first-person
/// arms models already ship:
///
/// <code>
/// from = "sw_plate.vmat"
/// to   = "materials/lightswitch/sw_plate.vmat"
/// </code>
///
/// <c>from</c> is the name the mesh file carries. <c>to</c> is the asset the compiler should bind.
/// <c>use_global_default</c> stays false: true is the switch that replaces every slot with default,
/// which is the failure this exists to stop.
///
/// WHY SEVERAL <c>from</c> SPELLINGS. The mesh writers emit whatever NameForSlot returns, which for
/// a bound slot is the full vmat path. ModelDoc's importer is documented as dropping everything
/// after a period in a material name (the Blender <c>.001</c> rule), and an OBJ <c>usemtl</c> with
/// slashes is sometimes taken as the last segment only. The lightswitch files remap from the
/// filename. Emitting the path, the filename, and both with <c>.vmat</c> stripped means whichever
/// spelling the importer keeps still hits a remap.
///
/// WHY THE KERNEL AND NOT THE EDITOR. Same reason as VmdlPhysics: it is text, it has no engine
/// types, and a headless test can say whether the node names the slots the mesh actually uses.
/// </summary>
public static class VmdlMaterials
{
	/// <summary>
	/// The MaterialGroupList node, indented to sit among a RootNode's children.
	///
	/// ALWAYS A NODE, even when nothing is bound. An omitted list is what ModelDoc replaces with
	/// the global default; an empty remap list with <c>use_global_default = false</c> leaves the
	/// mesh names in place, which is the honest answer for a part nobody has painted.
	/// </summary>
	public static string GroupList( PartStudio studio, PolyMesh mesh )
	{
		if ( studio is null )
			return GroupList( mesh, null, null );

		return GroupList( mesh, studio.NameForSlot, studio.MaterialNames );
	}

	/// <summary>
	/// The same node, from the two facts export already has: what the mesh writers will call each
	/// slot, and which slots have a vmat bound.
	/// </summary>
	public static string GroupList( PolyMesh mesh, Func<int, string> nameForSlot,
		IReadOnlyDictionary<int, string> materialNames )
	{
		var remaps = Remaps( mesh, nameForSlot, materialNames );
		var sb = new StringBuilder();

		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"MaterialGroupList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );
		sb.Append( "\t\t\t\t\t{\n" );
		sb.Append( "\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"\n" );
		sb.Append( "\t\t\t\t\t\tremaps = \n" );
		sb.Append( "\t\t\t\t\t\t[\n" );

		foreach ( var (from, to) in remaps )
		{
			sb.Append( "\t\t\t\t\t\t\t{\n" );
			sb.Append( $"\t\t\t\t\t\t\t\tfrom = {Quote( from )}\n" );
			sb.Append( $"\t\t\t\t\t\t\t\tto = {Quote( to )}\n" );
			sb.Append( "\t\t\t\t\t\t\t},\n" );
		}

		sb.Append( "\t\t\t\t\t\t]\n" );
		sb.Append( "\t\t\t\t\t\tuse_global_default = false\n" );
		sb.Append( "\t\t\t\t\t\tglobal_default_material = \"\"\n" );
		sb.Append( "\t\t\t\t\t},\n" );
		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );

		return sb.ToString();
	}

	/// <summary>
	/// Every <c>from → to</c> pair the node will write, in the order they appear.
	///
	/// Public so a test can count remaps without scraping KV3, and so the editor can log how many
	/// slots actually went out.
	/// </summary>
	public static List<(string From, string To)> Remaps( PolyMesh mesh, Func<int, string> nameForSlot,
		IReadOnlyDictionary<int, string> materialNames )
	{
		var remaps = new List<(string, string)>();
		var seenFrom = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var slot in SlotsOn( mesh ) )
		{
			if ( !TryBoundVmat( slot, materialNames, out var target ) )
				continue;

			var written = nameForSlot is not null ? nameForSlot( slot ) : ObjWriter.DefaultMaterialName( slot );

			foreach ( var from in FromAliases( written, target ) )
			{
				if ( !seenFrom.Add( from ) )
					continue;

				remaps.Add( (from, target) );
			}
		}

		return remaps;
	}

	/// <summary>Slots the mesh actually paints. A name sitting on a slot no face wears does not
	/// reach the compiled model, so it is not a remap.</summary>
	static SortedSet<int> SlotsOn( PolyMesh mesh )
	{
		var slots = new SortedSet<int>();

		if ( mesh?.Faces is null )
			return slots;

		foreach ( var face in mesh.Faces )
			slots.Add( face.Material );

		return slots;
	}

	/// <summary>
	/// The vmat a slot should compile to, or nothing.
	///
	/// A hand-typed display name — "anodised", "brushed steel" — is what the mesh writers already
	/// emit, and there is no asset to point <c>to</c> at. Only a path that looks like a material
	/// asset is remappable.
	/// </summary>
	static bool TryBoundVmat( int slot, IReadOnlyDictionary<int, string> materialNames, out string path )
	{
		path = null;

		if ( materialNames is null || !materialNames.TryGetValue( slot, out var name )
			|| string.IsNullOrWhiteSpace( name ) )
			return false;

		var n = name.Trim().Replace( '\\', '/' );

		if ( !n.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase )
			&& !n.StartsWith( "materials/", StringComparison.OrdinalIgnoreCase ) )
			return false;

		path = n;
		return true;
	}

	/// <summary>
	/// Every spelling of <paramref name="written"/> the importer might keep, plus the filename of
	/// <paramref name="target"/> — see the class comment for why there is more than one.
	/// </summary>
	static List<string> FromAliases( string written, string target )
	{
		var names = new List<string>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		void Add( string value )
		{
			if ( string.IsNullOrWhiteSpace( value ) )
				return;

			var n = value.Trim().Replace( '\\', '/' );

			if ( seen.Add( n ) )
				names.Add( n );
		}

		Add( written );
		Add( target );
		Add( FileName( target ) );
		Add( StripVmat( FileName( target ) ) );
		Add( StripVmat( target ) );

		return names;
	}

	static string FileName( string path )
	{
		if ( string.IsNullOrEmpty( path ) )
			return path;

		var n = path.Replace( '\\', '/' );
		var cut = n.LastIndexOf( '/' );

		return cut >= 0 && cut < n.Length - 1 ? n[(cut + 1)..] : n;
	}

	static string StripVmat( string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return name;

		return name.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase )
			? name[..^5]
			: name;
	}

	static string Quote( string value )
	{
		if ( value is null )
			return "\"\"";

		return "\"" + value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ) + "\"";
	}
}
