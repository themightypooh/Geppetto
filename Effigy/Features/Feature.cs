using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>One solid in the studio. Onshape calls these parts; a Part Studio holds several.</summary>
public sealed class Body
{
	public string Id;
	public string Name;
	public PolyMesh Mesh;

	/// <summary>Whether this body is drawn. Set from the feature that produced it — see
	/// Feature.Visible for why this is not the same thing as suppression.</summary>
	public bool Visible = true;

	/// <summary>Id of the Feature that produced this body, recorded by the rebuild alongside
	/// Visible. Bodies are re-made from scratch every rebuild and have no identity a UI can hold
	/// onto, so a Parts list needs this to get from a row back to the thing that owns it.</summary>
	public string FeatureId;

	public Body( string id, string name, PolyMesh mesh )
	{
		Id = id;
		Name = name;
		Mesh = mesh;
	}

	public Body Clone() => new( Id, Name, Mesh.Clone() ) { Visible = Visible, FeatureId = FeatureId };
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

/// <summary>The state a feature reads and writes as it runs.</summary>
public sealed class FeatureContext
{
	public List<Body> Bodies = new();

	/// <summary>Sketches published by SketchFeature, keyed by that feature's id. Extrude and
	/// Revolve look themselves up here rather than holding a reference, so editing the sketch
	/// upstream and rebuilding feeds the new geometry through without any wiring to keep in sync.</summary>
	public Dictionary<string, Sketch> Sketches = new();

	/// <summary>
	/// For a sketch drawn on a face, the id of the body that face belongs to. Keyed by sketch
	/// feature id, same as Sketches.
	///
	/// This is what lets an extrude know it is growing OUT OF something rather than into thin air.
	/// A Sketch is pure geometry and has no business knowing about bodies, and the consuming
	/// feature never sees the SketchFeature itself — only what it published here — so the
	/// attachment travels the same way the sketch does.
	/// </summary>
	public Dictionary<string, string> SketchHostBodies = new();

	int _nextId = 1;
	string _featureId;
	int _featureBodies;

	/// <summary>
	/// A body id belonging to the feature currently running: its feature id, plus a counter of the
	/// bodies that feature has made this run.
	///
	/// IDS USED TO BE A SINGLE RUNNING COUNTER - body1, body2, body3 in creation order across the
	/// whole rebuild - and that is the topological naming problem all over again, one level up from
	/// the faces FaceRef was written to protect. Add a feature ANYWHERE upstream that produces a
	/// body and every id after it shifts by one, so a sketch attached to "body1" silently lands on
	/// whatever happens to be body1 now. Not an error, not a warning: a boss quietly reattaches
	/// itself to an unrelated block.
	///
	/// Feature ids are assigned once at creation and never reused, so a body named after the
	/// feature that made it cannot be renumbered by anything happening elsewhere in the tree.
	/// </summary>
	public string NewBodyId() =>
		_featureId is null ? $"body{_nextId++}" : $"{_featureId}b{_featureBodies++}";

	/// <summary>Called by Feature.Run before Execute, so bodies are named after their maker. The
	/// per-feature counter restarts, which is what makes a feature that produces three bodies -
	/// a pattern, say - name them the same three ids on every rebuild.</summary>
	public void BeginFeature( string featureId )
	{
		_featureId = featureId;
		_featureBodies = 0;
	}

	/// <summary>Vestigial: only the fallback numbering uses it, for a context nothing has called
	/// BeginFeature on.</summary>
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

	/// <summary>
	/// Suppressed features do not run at all — their effect is removed from the model.
	/// </summary>
	public bool Suppressed;

	/// <summary>
	/// Whether the geometry this feature produced is DRAWN. Deliberately not the same thing as
	/// Suppressed, and conflating the two would be wrong: suppression takes a feature's effect out
	/// of the model, hiding only stops you looking at it. A hidden body is still there, still
	/// exported, and everything downstream still builds on it.
	///
	/// Solvespace keeps the same two flags separately on a group for the same reason.
	/// </summary>
	public bool Visible = true;

	/// <summary>Set by Run when Execute threw. A failed feature does not stop the rebuild — the
	/// tree carries on with the bodies as they were, which is what lets you fix an upstream
	/// mistake without every later feature also reporting failure.</summary>
	public string Error { get; internal set; }

	/// <summary>
	/// Something the feature worked around rather than failed on — it built, but not from
	/// everything it was given.
	///
	/// Distinct from Error because the two need opposite handling. An error means there is no
	/// geometry and the tree below is standing on nothing; a warning means there IS geometry and
	/// you should look at it. Collapsing the second into the first is why one stray branch
	/// anywhere in a sketch used to fail every extrude that read it.
	/// </summary>
	public string Warning { get; internal set; }

	public abstract string TypeName { get; }
	public abstract IReadOnlyList<IParam> Parameters { get; }

	protected abstract void Execute( FeatureContext ctx );

	internal void Run( FeatureContext ctx )
	{
		Error = null;
		Warning = null;

		// Bodies made from here on belong to this feature and are named after it. Set before the
		// Suppressed check is pointless; set here so every path into Execute is covered.
		ctx.BeginFeature( Id );

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
