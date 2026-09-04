using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Whether the feature produced geometry. An error means it did not; a warning means it
/// did, and you should look at it.</summary>
public enum DiagnosticSeverity
{
	Warning,
	Error
}

/// <summary>
/// Why a feature failed or degraded, in the three parts a person can act on: what is wrong, why
/// with this model's numbers, and what to do instead.
///
/// A single string cannot do this. The UI styles the three parts differently, ParameterLabel puts
/// the red ring on the control that is actually wrong, and remedies want to be a list — and
/// sometimes a button that does the thing.
/// </summary>
public sealed class FeatureDiagnostic
{
	public DiagnosticSeverity Severity;
	public string Problem;
	public string Cause;
	public List<string> Remedies = new();

	/// <summary>Which parameter's control to highlight, matching <see cref="IParam.Label"/>, or
	/// null when the failure is not a single field.</summary>
	public string ParameterLabel;

	/// <summary>When set, the dialog can offer a button that writes this into the named
	/// parameter — handing the user the number rather than only telling them it.</summary>
	public float? SuggestedValue;

	public FeatureDiagnostic() { }

	public FeatureDiagnostic( DiagnosticSeverity severity, string problem, string cause = null,
		string parameterLabel = null, float? suggestedValue = null, params string[] remedies )
	{
		Severity = severity;
		Problem = problem;
		Cause = cause;
		ParameterLabel = parameterLabel;
		SuggestedValue = suggestedValue;

		if ( remedies is { Length: > 0 } )
			Remedies.AddRange( remedies );
	}

	/// <summary>Problem plus cause, for a tooltip that has no room for the list.</summary>
	public string Tooltip
	{
		get
		{
			if ( string.IsNullOrEmpty( Cause ) )
				return Problem;

			return string.IsNullOrEmpty( Problem ) ? Cause : $"{Problem}\n{Cause}";
		}
	}
}

/// <summary>Thrown by a feature that knows exactly why it cannot proceed.</summary>
public sealed class FeatureException : Exception
{
	public readonly FeatureDiagnostic Diagnostic;

	public FeatureException( FeatureDiagnostic diagnostic )
		: base( diagnostic?.Problem ?? "Feature failed" )
	{
		Diagnostic = diagnostic ?? throw new ArgumentNullException( nameof( diagnostic ) );
	}
}

/// <summary>What EdgeBlend did, including the silent degradations that used to vanish.</summary>
public sealed class BlendReport
{
	public PolyMesh Mesh;
	public FeatureDiagnostic Failure;
	public readonly List<FeatureDiagnostic> Warnings = new();
	public float SuggestedSize;
	public int SelectedEdges;
	public float SharpestDegrees;
	public float OriginalVolume;
	public float ResultVolume;
}
