using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A named value on the document, evaluated before the first feature rebuilds.
///
/// THIS IS WHAT MAKES A PARAMETRIC MODELLER PARAMETRIC IN MORE THAN ONE PLACE. A feature's own
/// parameter is one number; a variable is a number several features can refer to. "The wall is 2
/// units thick" said once, used by the shell, the rib pitch and the hole, and changed once.
///
/// The expression is the source of truth. <see cref="Value"/> is the last successful evaluation,
/// held when the text is mid-keystroke or a cycle, the same rule numeric fields already use.
/// </summary>
public sealed class StudioVariable
{
	public string Name;
	public string Expr;
	public float Value;

	public StudioVariable() { }

	public StudioVariable( string name, string expr, float value = 0f )
	{
		Name = name;
		Expr = expr;
		Value = value;
	}
}

/// <summary>
/// Resolve <c>#name</c> against a list of variables, with cycle detection.
///
/// A CYCLE IS A PARSE FAILURE, not a stack overflow: <c>a = #b + 1</c>, <c>b = #a + 1</c> names
/// both variables and keeps the last good values. Evaluation is recursive through expressions so
/// order in the table does not matter.
/// </summary>
public static class VariableResolver
{
	public static bool IsLegalName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return false;

		if ( !( char.IsLetter( name[0] ) || name[0] == '_' ) )
			return false;

		for ( var i = 1; i < name.Length; i++ )
		{
			if ( !( char.IsLetterOrDigit( name[i] ) || name[i] == '_' ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Evaluate every variable in the table, writing each successful result back onto
	/// <see cref="StudioVariable.Value"/>. Failures leave the previous value.
	/// </summary>
	public static void EvaluateAll( IReadOnlyList<StudioVariable> variables )
	{
		if ( variables is null || variables.Count == 0 )
			return;

		foreach ( var variable in variables )
		{
			if ( variable is null || string.IsNullOrWhiteSpace( variable.Name ) )
				continue;

			var visiting = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

			if ( TryResolve( variables, variable.Name, visiting, out var value ) )
				variable.Value = value;
		}
	}

	public static bool TryResolve( IReadOnlyList<StudioVariable> variables, string name, out float value ) =>
		TryResolve( variables, name, new HashSet<string>( StringComparer.OrdinalIgnoreCase ), out value );

	public static bool TryResolve( IReadOnlyList<StudioVariable> variables, string name,
		HashSet<string> visiting, out float value )
	{
		value = 0f;

		if ( variables is null || string.IsNullOrWhiteSpace( name ) )
			return false;

		StudioVariable match = null;

		foreach ( var variable in variables )
		{
			if ( variable?.Name is null )
				continue;

			if ( string.Equals( variable.Name, name, StringComparison.OrdinalIgnoreCase ) )
			{
				match = variable;
				break;
			}
		}

		if ( match is null )
			return false;

		if ( !visiting.Add( match.Name ) )
			return false;

		if ( string.IsNullOrWhiteSpace( match.Expr ) )
		{
			value = match.Value;
			visiting.Remove( match.Name );
			return true;
		}

		float? Resolve( string asked )
		{
			if ( !TryResolve( variables, asked, visiting, out var found ) )
				return null;

			return found;
		}

		var ok = Expression.TryEvaluate( match.Expr, null, Resolve, out value );
		visiting.Remove( match.Name );
		return ok;
	}

	/// <summary>
	/// Every <c>#name</c> an expression refers to, in order, once each. A '#' with no name after
	/// it is skipped here; the parser is what calls that malformed.
	/// </summary>
	public static List<string> ReferencedNames( string expr )
	{
		var names = new List<string>();

		if ( string.IsNullOrEmpty( expr ) )
			return names;

		for ( var i = 0; i < expr.Length; i++ )
		{
			if ( expr[i] != '#' )
				continue;

			var start = i + 1;

			while ( start < expr.Length && char.IsWhiteSpace( expr[start] ) )
				start++;

			var end = start;

			if ( end >= expr.Length || !( char.IsLetter( expr[end] ) || expr[end] == '_' ) )
				continue;

			while ( end < expr.Length && ( char.IsLetterOrDigit( expr[end] ) || expr[end] == '_' ) )
				end++;

			var name = expr.Substring( start, end - start );

			if ( !names.Exists( n => string.Equals( n, name, StringComparison.OrdinalIgnoreCase ) ) )
				names.Add( name );

			i = end - 1;
		}

		return names;
	}

	/// <summary>
	/// Rewrite every <c>#oldName</c> in an expression to <c>#newName</c>, whole names only and
	/// ignoring case the way resolution does. <c>#wall</c> renamed to <c>#side</c> leaves
	/// <c>#wall2</c> alone. Returns the text unchanged when nothing matched.
	/// </summary>
	public static string RenameReferences( string expr, string oldName, string newName )
	{
		if ( string.IsNullOrEmpty( expr ) || string.IsNullOrEmpty( oldName ) || string.IsNullOrEmpty( newName ) )
			return expr;

		var sb = new System.Text.StringBuilder( expr.Length );

		for ( var i = 0; i < expr.Length; i++ )
		{
			sb.Append( expr[i] );

			if ( expr[i] != '#' )
				continue;

			var start = i + 1;

			while ( start < expr.Length && char.IsWhiteSpace( expr[start] ) )
				start++;

			var end = start;

			while ( end < expr.Length && ( char.IsLetterOrDigit( expr[end] ) || expr[end] == '_' ) )
				end++;

			if ( end == start )
				continue;

			var name = expr.Substring( start, end - start );

			sb.Append( expr, i + 1, start - i - 1 );
			sb.Append( string.Equals( name, oldName, StringComparison.OrdinalIgnoreCase ) ? newName : name );
			i = end - 1;
		}

		return sb.ToString();
	}

	/// <summary>
	/// Why <paramref name="expr"/> does not evaluate against the table, in a sentence, or null
	/// when it does. <paramref name="self"/> is the variable that owns the expression, so a loop
	/// back to it is called a loop rather than an unknown name.
	///
	/// ORDER OF THE ANSWERS IS ORDER OF USEFULNESS: a name that does not exist, then a loop
	/// (named the whole way round, so the user can see which link to cut), then a name that
	/// exists but is itself broken, then plain bad syntax.
	/// </summary>
	public static string Explain( IReadOnlyList<StudioVariable> variables, string expr, string self = null )
	{
		if ( string.IsNullOrWhiteSpace( expr ) )
			return "it's empty";

		var names = ReferencedNames( expr );

		foreach ( var name in names )
		{
			if ( Find( variables, name ) is null )
				return $"there's no variable called '#{name}'";
		}

		if ( !string.IsNullOrWhiteSpace( self ) )
		{
			var path = CyclePath( variables, self, expr );

			if ( path is not null )
				return $"it goes round in a circle ({string.Join( " → ", path )})";
		}

		var visiting = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		if ( !string.IsNullOrWhiteSpace( self ) )
			visiting.Add( self );

		foreach ( var name in names )
		{
			if ( !TryResolve( variables, name, visiting, out _ ) )
				return $"'#{name}' doesn't evaluate itself";
		}

		float? Resolve( string asked ) => TryResolve( variables, asked, visiting, out var found ) ? (float?)found : null;

		if ( Expression.TryEvaluate( expr, null, Resolve, out _ ) )
			return null;

		return "it isn't a number or an expression I can read";
	}

	/// <summary>
	/// The loop <paramref name="self"/> would sit in if its expression were <paramref name="expr"/>,
	/// as "#a", "#b", "#a" - or null when there is none.
	/// </summary>
	public static List<string> CyclePath( IReadOnlyList<StudioVariable> variables, string self, string expr )
	{
		var path = new List<string> { $"#{self}" };
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		bool Walk( string text )
		{
			foreach ( var name in ReferencedNames( text ) )
			{
				if ( string.Equals( name, self, StringComparison.OrdinalIgnoreCase ) )
				{
					path.Add( $"#{self}" );
					return true;
				}

				if ( !seen.Add( name ) )
					continue;

				var next = Find( variables, name );

				if ( next is null )
					continue;

				path.Add( $"#{next.Name}" );

				if ( Walk( next.Expr ) )
					return true;

				path.RemoveAt( path.Count - 1 );
			}

			return false;
		}

		return Walk( expr ) ? path : null;
	}

	public static StudioVariable Find( IReadOnlyList<StudioVariable> variables, string name )
	{
		if ( variables is null || string.IsNullOrWhiteSpace( name ) )
			return null;

		foreach ( var variable in variables )
		{
			if ( variable?.Name is not null && string.Equals( variable.Name, name, StringComparison.OrdinalIgnoreCase ) )
				return variable;
		}

		return null;
	}

	/// <summary>A resolver a numeric field or a rebuild can hand to <see cref="Expression"/>.</summary>
	public static Func<string, float?> Bind( IReadOnlyList<StudioVariable> variables ) =>
		name => TryResolve( variables, name, out var value ) ? value : null;
}
