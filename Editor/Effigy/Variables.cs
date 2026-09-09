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

	/// <summary>A resolver a numeric field or a rebuild can hand to <see cref="Expression"/>.</summary>
	public static Func<string, float?> Bind( IReadOnlyList<StudioVariable> variables ) =>
		name => TryResolve( variables, name, out var value ) ? value : null;
}
