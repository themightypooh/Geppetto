using Effigy;
using Sandbox;
using System;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Marionette.EditorTools;

/// <summary>
/// Finds out what s&box's mesh boolean actually looks like, so the adapter can be written against
/// it rather than at it.
///
/// WHY THIS IS A PROBE AND NOT THE ADAPTER. Remove is wired end to end in the kernel — the Result
/// dropdown, MeshBoolean, the errors, all tested — and the single missing piece is the translation
/// between PolyMesh and whatever type the engine's boolean takes. That piece cannot be written from
/// here: there is no s&box in this environment, and guessing an API is how the toolbar ended up
/// full of Material Symbols names that silently rendered as nothing. Worse, a guessed member name
/// is a COMPILE error, which would take the whole editor assembly down rather than failing politely
/// at the one feature that needed it.
///
/// So: `effigy_probe_boolean` in the console dumps the real shape of PolygonMesh — its constructors,
/// how vertices and faces go in, how they come back out, and every method with "boolean" in the
/// name. That output is what the adapter gets written from, in one pass, with no guessing at all.
/// It is the same technique HANDOFF.md records for BoneCollection: a throwaway command that
/// reflection-dumps the type beats reasoning about it from a distance.
/// </summary>
public static class EffigyBooleanProbe
{
	[ConCmd( "effigy_probe_boolean" )]
	public static void Probe()
	{
		// By name rather than by a direct reference, so this file compiles whether or not the type
		// is where it is expected — which is the entire point of a probe.
		var type = FindType( "PolygonMesh" );

		if ( type is null )
		{
			Log.Error( "[effigy] no type named PolygonMesh in any loaded assembly." );
			Log.Info( "[effigy] types with 'mesh' in the name, in case it is called something else:" );

			foreach ( var candidate in AllTypes().Where( t => t.Name.Contains( "Mesh", StringComparison.OrdinalIgnoreCase ) ).Take( 40 ) )
				Log.Info( $"[effigy]   {candidate.FullName}" );

			return;
		}

		var report = new StringBuilder();
		report.AppendLine( $"[effigy] {type.FullName}, from {type.Assembly.GetName().Name}" );

		report.AppendLine( "[effigy] constructors:" );

		foreach ( var c in type.GetConstructors() )
			report.AppendLine( $"[effigy]   new {type.Name}({Parameters( c )})" );

		// The four questions the adapter has to answer: how a vertex goes in, how a face goes in,
		// how they come back out, and what the boolean itself is called.
		Section( report, "vertices in / out", type, m =>
			m.Name.Contains( "Vertex", StringComparison.OrdinalIgnoreCase )
			|| m.Name.Contains( "Position", StringComparison.OrdinalIgnoreCase ) );

		Section( report, "faces in / out", type, m => m.Name.Contains( "Face", StringComparison.OrdinalIgnoreCase ) );

		Section( report, "the boolean itself", type, m =>
			m.Name.Contains( "Boolean", StringComparison.OrdinalIgnoreCase )
			|| m.Name.Contains( "Union", StringComparison.OrdinalIgnoreCase )
			|| m.Name.Contains( "Subtract", StringComparison.OrdinalIgnoreCase ) );

		report.AppendLine( "[effigy] properties:" );

		foreach ( var p in type.GetProperties( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static ) )
			report.AppendLine( $"[effigy]   {Short( p.PropertyType )} {p.Name}" );

		// A boolean almost certainly takes an enum saying which operation. Whatever it is called,
		// its values are what the adapter has to map BooleanOp onto.
		foreach ( var enumType in AllTypes().Where( t => t.IsEnum && t.Name.Contains( "Boolean", StringComparison.OrdinalIgnoreCase ) ) )
			report.AppendLine( $"[effigy] enum {enumType.FullName}: {string.Join( ", ", Enum.GetNames( enumType ) )}" );

		Log.Info( report.ToString() );
		Log.Info( "[effigy] paste that output back and the adapter can be written against it exactly." );
	}

	static void Section( StringBuilder report, string title, Type type, Func<MethodInfo, bool> wanted )
	{
		report.AppendLine( $"[effigy] {title}:" );

		var methods = type
			.GetMethods( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly )
			.Where( m => !m.IsSpecialName && wanted( m ) )
			.OrderBy( m => m.Name )
			.ToList();

		if ( methods.Count == 0 )
		{
			report.AppendLine( "[effigy]   (none)" );
			return;
		}

		foreach ( var m in methods )
			report.AppendLine( $"[effigy]   {Short( m.ReturnType )} {m.Name}({Parameters( m )})" );
	}

	static string Parameters( MethodBase m ) =>
		string.Join( ", ", m.GetParameters().Select( p => $"{Short( p.ParameterType )} {p.Name}" ) );

	/// <summary>Type names without the namespace noise, so a dumped signature reads like the
	/// declaration it is meant to become.</summary>
	static string Short( Type t )
	{
		if ( !t.IsGenericType )
			return t.Name;

		var name = t.Name[..t.Name.IndexOf( '`' )];

		return $"{name}<{string.Join( ", ", t.GetGenericArguments().Select( Short ) )}>";
	}

	static Type FindType( string name ) =>
		AllTypes().FirstOrDefault( t => t.Name == name && t.IsPublic );

	static System.Collections.Generic.IEnumerable<Type> AllTypes()
	{
		foreach ( var assembly in AppDomain.CurrentDomain.GetAssemblies() )
		{
			Type[] types;

			try
			{
				types = assembly.GetTypes();
			}
			catch ( ReflectionTypeLoadException e )
			{
				// A half-loaded assembly still lists the types it managed, and one of them may be
				// the answer. Throwing here would let an unrelated broken addon hide it.
				types = e.Types.Where( t => t is not null ).ToArray();
			}
			catch ( Exception )
			{
				continue;
			}

			foreach ( var t in types )
				yield return t;
		}
	}
}
