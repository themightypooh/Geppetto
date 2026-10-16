using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>What a <see cref="RemedyAction"/> does when its button is pressed.</summary>
public enum RemedyKind
{
	/// <summary>Put a new feature into the history directly above the one that failed: the
	/// prerequisite it was missing (a Subdivide under a stroke finer than the mesh, an Unwrap under a
	/// bake).</summary>
	InsertFeatureAbove,

	/// <summary>Ask for a folder and write it into a text parameter (a bake's Folder).</summary>
	ChooseFolder,

	/// <summary>Switch the editor to a workspace (a garment with no rig opens Rig). The kernel cannot
	/// do this one; it only names it.</summary>
	OpenWorkspace,
}

/// <summary>
/// The one-click version of a remedy. A diagnostic's <see cref="FeatureDiagnostic.Remedies"/> say
/// what to do in words; an action is the same fix as data, so the editor can put a button on the
/// callout and a script can apply it with `remedy`.
///
/// ONLY FOR FIXES THAT ARE UNAMBIGUOUS. "Put a Subdivide above this" has one obvious meaning;
/// "move the points onto the body" does not, and stays words.
/// </summary>
public sealed class RemedyAction
{
	public RemedyKind Kind;

	/// <summary>The button's text: "Subdivide above", "Choose folder".</summary>
	public string Label;

	/// <summary>InsertFeatureAbove: the feature type, as a script names it ("Subdivide", "UVProject").</summary>
	public string FeatureType;

	/// <summary>InsertFeatureAbove: the bodies the new feature acts on, by id. Empty: the feature's
	/// default (usually every body).</summary>
	public readonly List<string> BodyIds = new();

	/// <summary>InsertFeatureAbove: parameters for the new feature, as a script writes them
	/// (levels=2, mode=Organic).</summary>
	public readonly Dictionary<string, string> Params = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>ChooseFolder: the label of the text parameter the folder goes into.</summary>
	public string ParameterLabel;

	/// <summary>OpenWorkspace: the workspace's name ("Rig").</summary>
	public string Workspace;

	public static RemedyAction InsertFeatureAbove( string label, string featureType, IEnumerable<string> bodyIds = null,
		params (string Key, string Value)[] parameters )
	{
		var action = new RemedyAction { Kind = RemedyKind.InsertFeatureAbove, Label = label, FeatureType = featureType };

		if ( bodyIds is not null )
			action.BodyIds.AddRange( bodyIds.Where( id => !string.IsNullOrEmpty( id ) ).Distinct() );

		foreach ( var (key, value) in parameters )
			action.Params[key] = value;

		return action;
	}

	/// <summary>A Subdivide above the failing feature on <paramref name="bodyIds"/>, with enough levels
	/// to bring <paramref name="edge"/>-long edges down to <paramref name="finest"/> (each level halves
	/// them; at most 3, since every level is four times the faces).</summary>
	public static RemedyAction SubdivideAbove( IEnumerable<string> bodyIds, float edge, float finest )
	{
		var levels = finest > 0f && edge > finest ? (int)Math.Ceiling( Math.Log2( edge / finest ) ) : 1;
		levels = Math.Clamp( levels, 1, 3 );

		return InsertFeatureAbove( levels == 1 ? "Subdivide above" : $"Subdivide above ({levels} levels)", "Subdivide", bodyIds,
			("levels", levels.ToString( System.Globalization.CultureInfo.InvariantCulture )) );
	}

	public static RemedyAction ChooseFolder( string label, string parameterLabel ) =>
		new() { Kind = RemedyKind.ChooseFolder, Label = label, ParameterLabel = parameterLabel };

	public static RemedyAction OpenWorkspace( string label, string workspace ) =>
		new() { Kind = RemedyKind.OpenWorkspace, Label = label, Workspace = workspace };

	// --- text form ------------------------------------------------------------------------------
	// One line of key=value tokens, each value escaped, so an action can be written wherever a
	// diagnostic is (a script's log, a saved report) and read back exactly.

	static readonly string[] KindNames = { "insert-above", "choose-folder", "open-workspace" };

	public string ToText()
	{
		var sb = new StringBuilder( "kind=" ).Append( KindNames[(int)Kind] );

		void Put( string key, string value )
		{
			if ( value is not null )
				sb.Append( ' ' ).Append( key ).Append( '=' ).Append( Uri.EscapeDataString( value ) );
		}

		Put( "label", Label );
		Put( "type", FeatureType );
		Put( "param", ParameterLabel );
		Put( "workspace", Workspace );

		if ( BodyIds.Count > 0 )
			Put( "bodies", string.Join( ",", BodyIds ) );

		foreach ( var (key, value) in Params.OrderBy( p => p.Key, StringComparer.Ordinal ) )
			Put( "p." + Uri.EscapeDataString( key ), value );

		return sb.ToString();
	}

	public static RemedyAction Parse( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			throw new FormatException( "An empty remedy action." );

		var action = new RemedyAction();
		var kind = -1;

		foreach ( var token in text.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var eq = token.IndexOf( '=' );

			if ( eq <= 0 )
				throw new FormatException( $"'{token}' in a remedy action is not key=value." );

			var key = Uri.UnescapeDataString( token[..eq] );
			var value = Uri.UnescapeDataString( token[(eq + 1)..] );

			switch ( key )
			{
				case "kind": kind = Array.IndexOf( KindNames, value ); break;
				case "label": action.Label = value; break;
				case "type": action.FeatureType = value; break;
				case "param": action.ParameterLabel = value; break;
				case "workspace": action.Workspace = value; break;
				case "bodies": action.BodyIds.AddRange( value.Split( ',', StringSplitOptions.RemoveEmptyEntries ) ); break;
				default:
					if ( key.StartsWith( "p.", StringComparison.Ordinal ) && key.Length > 2 )
						action.Params[key[2..]] = value;
					break;
			}
		}

		if ( kind < 0 )
			throw new FormatException( $"A remedy action needs kind=, one of {string.Join( ", ", KindNames )}." );

		action.Kind = (RemedyKind)kind;
		return action;
	}

	public override string ToString() => ToText();
}

/// <summary>
/// Applies a <see cref="RemedyAction"/> to a studio without the editor: what the callout's button
/// does, for scripts and tests. It edits the history and marks it dirty; the caller rebuilds (the
/// editor records undo first and rebuilds its own way).
/// </summary>
public static class FeatureRemedy
{
	public readonly record struct Outcome( bool Ok, string Message, Feature Inserted );

	/// <param name="folder">ChooseFolder: the folder that was chosen.</param>
	public static Outcome Apply( PartStudio studio, Feature failing, RemedyAction action, string folder = null )
	{
		if ( studio is null || failing is null || action is null )
			return new( false, "Nothing to fix: no studio, feature or action.", null );

		var index = studio.Features.IndexOf( failing );

		if ( index < 0 )
			return new( false, $"'{failing.Name}' is no longer in the history, so there is nothing to fix.", null );

		switch ( action.Kind )
		{
			case RemedyKind.InsertFeatureAbove:
			{
				var feature = BuildScript.CreateFeature( action.FeatureType );

				if ( feature is null )
					return new( false, $"This build has no '{action.FeatureType}' feature to insert.", null );

				if ( action.BodyIds.Count > 0 && feature.Parameters.OfType<BodySelectionParam>().FirstOrDefault() is { } bodies )
				{
					bodies.BodyIds.Clear();
					bodies.BodyIds.AddRange( action.BodyIds );
				}

				if ( action.Params.Count > 0 && !BuildScript.Configure( studio, feature, action.Params, out var why ) )
					return new( false, $"Could not set up the {feature.TypeName}: {why}", null );

				studio.Insert( index, feature );

				// The bar is a real position below the failing feature: keep it there, one further
				// down, so the fix is evaluated along with what it fixes.
				if ( studio.RollbackIndex > index && studio.RollbackIndex < int.MaxValue )
					studio.RollbackIndex++;

				return new( true, $"Added {feature.Name} above {failing.Name}.", feature );
			}

			case RemedyKind.ChooseFolder:
			{
				if ( string.IsNullOrWhiteSpace( folder ) )
					return new( false, "No folder was chosen, so nothing changed. Choose a folder to write the files into.", null );

				if ( failing.Parameters.Concat( failing.AdvancedParameters ).OfType<StringParam>()
					.FirstOrDefault( p => p.Label == action.ParameterLabel ) is not { } param )
				{
					return new( false, $"{failing.TypeName} has no '{action.ParameterLabel}' setting to put the folder in.", null );
				}

				param.Value = folder.Trim();
				studio.MarkDirty( failing );
				return new( true, $"{failing.Name} writes its files to {param.Value}.", null );
			}

			case RemedyKind.OpenWorkspace:
				return new( false, $"Open the {action.Workspace} workspace in the editor: a script cannot do this one.", null );
		}

		return new( false, $"Unknown remedy kind {action.Kind}.", null );
	}
}
