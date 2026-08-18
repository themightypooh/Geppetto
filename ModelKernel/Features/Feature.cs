using System;
using System.Collections.Generic;

namespace ModelKernel;

/// <summary>One solid in the studio. Onshape calls these parts; a Part Studio holds several.</summary>
public sealed class Body
{
	public string Id;
	public string Name;
	public PolyMesh Mesh;

	public Body( string id, string name, PolyMesh mesh )
	{
		Id = id;
		Name = name;
		Mesh = mesh;
	}

	public Body Clone() => new( Id, Name, Mesh.Clone() );
}

// --- parameters -------------------------------------------------------------------------------

/// <summary>
/// Feature parameters describe themselves, so one generic panel can render any feature's dialog
/// rather than each feature needing hand-written UI.
///
/// This is copied from Onshape deliberately. Every feature dialog there has the same shape, and
/// that uniformity is most of why the tool feels coherent with a hundred features in it. Getting
/// it for free is worth the small indirection here.
///
/// The parameter object IS the storage — the feature reads `_length.Value` — so there is no
/// separate copy to keep in sync.
/// </summary>
public interface IParam
{
	string Label { get; }
}

public sealed class FloatParam : IParam
{
	public string Label { get; }
	public float Value;
	public float Min, Max;
	public string Unit;

	public FloatParam( string label, float value, float min = float.MinValue, float max = float.MaxValue, string unit = null )
	{
		Label = label;
		Value = value;
		Min = min;
		Max = max;
		Unit = unit;
	}

	public float Clamped => Math.Clamp( Value, Min, Max );
}

public sealed class IntParam : IParam
{
	public string Label { get; }
	public int Value;
	public int Min, Max;

	public IntParam( string label, int value, int min = int.MinValue, int max = int.MaxValue )
	{
		Label = label;
		Value = value;
		Min = min;
		Max = max;
	}

	public int Clamped => Math.Clamp( Value, Min, Max );
}

public sealed class BoolParam : IParam
{
	public string Label { get; }
	public bool Value;

	public BoolParam( string label, bool value )
	{
		Label = label;
		Value = value;
	}
}

public sealed class Vec3Param : IParam
{
	public string Label { get; }
	public Vec3 Value;

	public Vec3Param( string label, Vec3 value )
	{
		Label = label;
		Value = value;
	}
}

public sealed class ChoiceParam : IParam
{
	public string Label { get; }
	public string[] Options;
	public int Index;

	public ChoiceParam( string label, string[] options, int index = 0 )
	{
		Label = label;
		Options = options;
		Index = index;
	}

	public string Value => Options[Math.Clamp( Index, 0, Options.Length - 1 )];
}

/// <summary>Which bodies a feature acts on. Empty means every body, which is what Onshape's
/// "all" behaves like and is the sane default for a studio holding one part.</summary>
public sealed class BodySelectionParam : IParam
{
	public string Label { get; }
	public List<string> BodyIds = new();

	public BodySelectionParam( string label )
	{
		Label = label;
	}

	public bool Matches( Body b ) => BodyIds.Count == 0 || BodyIds.Contains( b.Id );
}

// --- features ---------------------------------------------------------------------------------

/// <summary>The body list a feature reads and writes as it runs.</summary>
public sealed class FeatureContext
{
	public List<Body> Bodies = new();
	int _nextId = 1;

	public string NewBodyId() => $"body{_nextId++}";

	/// <summary>Kept across rebuilds so ids stay stable — a selection referring to "body3" must
	/// still mean the same body after an unrelated feature is edited upstream.</summary>
	public void SeedIdCounter( int next ) => _nextId = Math.Max( _nextId, next );
}

/// <summary>
/// One step in the history. Features run in order, each seeing what the ones before it produced.
///
/// THE ORDERED TREE IS THE WHOLE POINT. It is what separates a parametric modeller from a stack of
/// bakes: roll back to before feature 3, change a number, roll forward and everything downstream
/// rebuilds against the new value. Every design decision here bends toward keeping that true —
/// features hold parameters rather than results, and nothing caches geometry inside a feature.
/// </summary>
public abstract class Feature
{
	public string Id = Guid.NewGuid().ToString( "N" )[..8];
	public string Name;
	public bool Suppressed;

	/// <summary>Set by Run when Execute threw. A failed feature does not stop the rebuild — the
	/// tree carries on with the bodies as they were, which is what lets you fix an upstream
	/// mistake without every later feature also reporting failure.</summary>
	public string Error { get; internal set; }

	public abstract string TypeName { get; }
	public abstract IReadOnlyList<IParam> Parameters { get; }

	protected abstract void Execute( FeatureContext ctx );

	internal void Run( FeatureContext ctx )
	{
		Error = null;

		if ( Suppressed )
			return;

		try
		{
			Execute( ctx );
		}
		catch ( Exception e )
		{
			Error = e.Message;
		}
	}

	public override string ToString() => $"{TypeName} '{Name ?? Id}'{(Suppressed ? " (suppressed)" : "")}";
}
