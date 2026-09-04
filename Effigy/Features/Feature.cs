using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

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

	/// <summary>
	/// Whether dragging this number through its range means anything.
	///
	/// A slider is for a MAGNITUDE — segment counts, subdivision levels, numbers where the values
	/// either side of the one you have are the neighbouring answers. A material slot is an
	/// IDENTIFIER that happens to be stored as a number: slot 7 is not "more" than slot 6, sweeping
	/// through 40 of them on the way says nothing, and the drag bar was the widest control in the
	/// Extrude dialog doing the least. Those get a field and no slider.
	///
	/// The bounds test in the dialog cannot tell the two apart — 0..63 looks exactly as draggable
	/// as 0..6 — so the parameter says which it is.
	/// </summary>
	public bool Slider = true;

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

	/// <summary>
	/// Structured form of Error or Warning. Null when the feature ran clean.
	///
	/// Error and Warning stay as strings because PartStudio, RebuildReport, the feature tree and
	/// several tests already read them. They are set alongside this: Error = Diagnostic.Problem
	/// on a failure, Warning = Diagnostic.Problem on a warning.
	/// </summary>
	public FeatureDiagnostic Diagnostic { get; internal set; }

	public abstract string TypeName { get; }
	public abstract IReadOnlyList<IParam> Parameters { get; }

	/// <summary>
	/// The subset of <see cref="Parameters"/> that belongs behind the dialog's Advanced disclosure
	/// — folded away by default, and still every bit as much a parameter as the ones above it.
	///
	/// It is a SUBSET RATHER THAN A SECOND LIST. Everything generic — the snapshot Cancel restores
	/// from, the document writer, the diagnostics that point at a parameter by label — walks
	/// Parameters, and a parameter that only appeared here would be invisible to all of it. So this
	/// says nothing about what a feature HAS; it only says which of them are answered once and then
	/// left alone. Extrude is the case that asked for it: taper, a second distance and a material
	/// slot are three rows of nothing-to-do-here sitting under the distance that is the whole point
	/// of the feature.
	///
	/// Held by REFERENCE, matched by reference. Naming the parameters by label would tie which rows
	/// fold up to the words on screen.
	/// </summary>
	public virtual IReadOnlyList<IParam> AdvancedParameters => Array.Empty<IParam>();

	/// <summary>
	/// Whether this feature's cached result is out of date even though nobody called MarkDirty.
	///
	/// The convention everywhere else is that whoever edits a feature marks it dirty, and for a
	/// dialog full of numbers that is one call in one place. It does not hold for a feature whose
	/// state is a live object somebody else is mutating — SculptFeature's levels are changed by a
	/// brush, hundreds of times a stroke, nowhere near the code that owns the studio. Relying on
	/// that caller to remember is how "every parameter edit is a silent no-op" happened once
	/// already, and it looked like three unrelated UI faults for a day.
	///
	/// So a feature that owns mutable state outside its parameters answers this instead, and the
	/// rebuild asks. Must be cheap: it is called for every reusable feature on every rebuild.
	/// </summary>
	public virtual bool IsStale => false;

	/// <summary>
	/// Seed this feature from geometry that was already picked, the way Onshape's tools consume
	/// the current selection instead of making you pick again after the button.
	///
	/// Faces go to anything that stores FaceRefs — a sketch's plane, draft, hole, face material,
	/// subdivide. Body ids go onto every BodySelectionParam. A face selection with no explicit
	/// body list still names those faces' bodies, so clicking a face and then Fillet fillets that
	/// part rather than every part.
	///
	/// <paramref name="bodies"/> is only needed for Shell, whose openings are still stored as
	/// indices rather than FaceRefs and have to be resolved against the live mesh.
	/// </summary>
	public void ApplyGeometrySelection( IReadOnlyList<FaceRef> faces, IReadOnlyList<string> bodyIds,
		IEnumerable<Body> bodies = null, IReadOnlyList<EdgeRef> edges = null,
		string sketchFeatureId = null, IReadOnlyList<Vec2> regionSeeds = null )
	{
		faces ??= Array.Empty<FaceRef>();
		bodyIds ??= Array.Empty<string>();
		edges ??= Array.Empty<EdgeRef>();

		if ( this is SketchFeature sketch && faces.Count > 0 )
			sketch.Face = faces[0];

		if ( this is SketchConsumingFeature consumer
			&& !string.IsNullOrEmpty( sketchFeatureId )
			&& sketchFeatureId != SketchConsumingFeature.AwaitingPick )
		{
			consumer.SketchFeatureId = sketchFeatureId;
			consumer.RegionSeeds.Clear();

			if ( regionSeeds is { Count: > 0 } )
				consumer.RegionSeeds.AddRange( regionSeeds );
		}

		var pickedFaces = this switch
		{
			FaceMaterialFeature material => material.Faces,
			DraftFeature draft => draft.Faces,
			HoleFeature hole => hole.Faces,
			SubdivideFeature subdivide => subdivide.Faces,
			_ => null,
		};

		if ( pickedFaces is not null && faces.Count > 0 )
		{
			pickedFaces.Clear();
			pickedFaces.AddRange( faces );
		}

		ApplyBlendEdges( faces, edges, bodies );

		var ids = bodyIds.Count > 0
			? bodyIds
			: (IReadOnlyList<string>)faces.Select( f => f.BodyId )
				.Concat( edges.Select( e => e.BodyId ) )
				.Distinct()
				.ToList();

		if ( ids.Count > 0 )
		{
			foreach ( var param in Parameters )
			{
				if ( param is not BodySelectionParam selection )
					continue;

				selection.BodyIds.Clear();
				selection.BodyIds.AddRange( ids );
			}
		}

		if ( this is not ShellFeature shell || faces.Count == 0 || bodies is null )
			return;

		// OpenFaces is applied to every selected body, so it only means something when the
		// openings all live on one part. Two parts and a mixed index list would punch the
		// wrong faces — or fail — on the second body.
		if ( ids.Count != 1 )
			return;

		shell.OpenFaces.Clear();

		foreach ( var face in faces )
		{
			if ( !FacePlane.TryResolveFace( bodies, face, out _, out var index ) )
				continue;

			if ( !shell.OpenFaces.Contains( index ) )
				shell.OpenFaces.Add( index );
		}
	}

	/// <summary>
	/// Fillet and Chamfer store edges, not faces. A picked edge list is copied across; a picked
	/// FACE becomes that face's boundary, which is what "select the top, then Fillet" means in
	/// Onshape.
	/// </summary>
	void ApplyBlendEdges( IReadOnlyList<FaceRef> faces, IReadOnlyList<EdgeRef> edges, IEnumerable<Body> bodies )
	{
		var dest = this switch
		{
			FilletFeature fillet => fillet.Edges,
			ChamferFeature chamfer => chamfer.Edges,
			_ => null,
		};

		if ( dest is null )
			return;

		dest.Clear();

		if ( edges.Count > 0 )
		{
			dest.AddRange( edges );
			return;
		}

		if ( faces.Count == 0 || bodies is null )
			return;

		var seen = new HashSet<(string BodyId, EdgeKey Key)>();

		foreach ( var face in faces )
		{
			if ( !FacePlane.TryResolveFace( bodies, face, out var body, out var index ) )
				continue;

			foreach ( var edge in FacePlane.CaptureBoundary( body, index ) )
			{
				if ( !FacePlane.TryResolveEdge( bodies, edge, out _, out var key ) )
					continue;

				if ( !seen.Add( (body.Id, key) ) )
					continue;

				dest.Add( edge );
			}
		}
	}

	protected abstract void Execute( FeatureContext ctx );

	internal void Run( FeatureContext ctx )
	{
		Error = null;
		Warning = null;
		Diagnostic = null;

		// Bodies made from here on belong to this feature and are named after it. Set before the
		// Suppressed check is pointless; set here so every path into Execute is covered.
		ctx.BeginFeature( Id );

		if ( Suppressed )
			return;

		try
		{
			Execute( ctx );
		}
		catch ( FeatureException e )
		{
			ApplyDiagnostic( e.Diagnostic );
		}
		catch ( Exception e )
		{
			ApplyDiagnostic( new FeatureDiagnostic( DiagnosticSeverity.Error, e.Message ) );
		}
	}

	/// <summary>Refuse to proceed. The three arguments are the three things the dialog shows.</summary>
	[DoesNotReturn]
	protected static void Fail( string problem, string cause, params string[] remedies ) =>
		throw new FeatureException( new FeatureDiagnostic( DiagnosticSeverity.Error, problem, cause, remedies: remedies ) );

	/// <summary>Refuse, and name the control the dialog should highlight.</summary>
	[DoesNotReturn]
	protected static void FailOn( string parameterLabel, string problem, string cause, params string[] remedies ) =>
		throw new FeatureException( new FeatureDiagnostic( DiagnosticSeverity.Error, problem, cause, parameterLabel, remedies: remedies ) );

	/// <summary>Refuse, highlight the control, and offer a button that writes <paramref name="suggested"/>
	/// into it.</summary>
	[DoesNotReturn]
	protected static void FailOn( string parameterLabel, float suggested, string problem, string cause, params string[] remedies ) =>
		throw new FeatureException( new FeatureDiagnostic( DiagnosticSeverity.Error, problem, cause, parameterLabel, suggested, remedies ) );

	/// <summary>The feature built, but not from what was asked. Geometry stays; the dialog goes yellow.</summary>
	protected void Warn( string problem, string cause, params string[] remedies ) =>
		ApplyDiagnostic( new FeatureDiagnostic( DiagnosticSeverity.Warning, problem, cause, remedies: remedies ) );

	/// <summary>
	/// After a cut, put every piece the cut severed into the part list. Returns how many bodies
	/// were added, so the caller can say so.
	///
	/// A boolean is allowed to sever a part and has no obligation to mention it — "one mesh" is a
	/// fine answer to "subtract this". Whether that mesh is one SOLID is the part list's question,
	/// and this is where it gets asked. See MeshSplit for why connectivity is by shared vertex and
	/// why the order the pieces come back in is a promise rather than an implementation detail.
	///
	/// THE ORIGINAL BODY KEEPS ITS ID, and keeps the largest piece. Everything built on this part is
	/// holding that id — a sketch drawn on one of its faces, a later feature's body selection — and
	/// a cut must not invalidate them. The offcuts are new bodies named after the feature that made
	/// them, which is the same rule every other body in the studio follows.
	/// </summary>
	protected int SeparatePieces( FeatureContext ctx, Body body )
	{
		if ( body?.Mesh is null || MeshSplit.PieceCount( body.Mesh ) < 2 )
			return 0;

		var pieces = MeshSplit.ConnectedPieces( body.Mesh );

		body.Mesh = pieces[0];

		// Straight after the body they came off, so the parts list reads in the order someone would
		// expect rather than collecting offcuts at the bottom. IndexOf can legitimately miss - a
		// feature is allowed to hand us a body it has not published - and appending is right then.
		var at = ctx.Bodies.IndexOf( body );

		for ( var i = 1; i < pieces.Count; i++ )
		{
			var piece = new Body( ctx.NewBodyId(), $"{body.Name} ({i + 1})", pieces[i] )
			{
				Visible = body.Visible,
				FeatureId = Id,
			};

			if ( at < 0 )
				ctx.Bodies.Add( piece );
			else
				ctx.Bodies.Insert( at + i, piece );
		}

		return pieces.Count - 1;
	}

	/// <summary>
	/// The warning a severing cut earns. Never an error: the geometry is right, and the part list
	/// having grown is something to look at rather than something to fix.
	/// </summary>
	protected void WarnSeparated( int added, string bodyName )
	{
		if ( added <= 0 )
			return;

		Warn(
			added == 1
				? $"This cut separated '{bodyName}' into two parts"
				: $"This cut separated '{bodyName}' into {added + 1} parts",
			$"The tool went all the way through, so what was one solid is now {added + 1} that touch nowhere. "
				+ "The largest keeps the original part's name and id; the rest are new parts below it.",
			"Reduce the depth if the cut was meant to be a pocket",
			"Nothing to fix if separating the part was the intent" );
	}

	/// <summary>Names in a sentence: "'A'", "'A' and 'B'", "'A', 'B' and 'C'".</summary>
	protected static string Listed( IReadOnlyList<string> names )
	{
		if ( names is null || names.Count == 0 )
			return "nothing";

		var quoted = new List<string>( names.Count );

		foreach ( var name in names )
			quoted.Add( $"'{name}'" );

		if ( quoted.Count == 1 )
			return quoted[0];

		return string.Join( ", ", quoted.GetRange( 0, quoted.Count - 1 ) ) + " and " + quoted[^1];
	}

	protected void ApplyDiagnostic( FeatureDiagnostic diagnostic )
	{
		if ( diagnostic is null )
			return;

		Diagnostic = diagnostic;

		if ( diagnostic.Severity == DiagnosticSeverity.Error )
			Error = diagnostic.Problem;
		else
			Warning = diagnostic.Problem;
	}

	/// <summary>The bodies this feature acts on, or a refusal if there are none. A feature that
	/// did nothing is never a success.</summary>
	protected List<Body> RequireBodies( FeatureContext ctx, BodySelectionParam selection )
	{
		var bodies = new List<Body>();

		foreach ( var b in ctx.Bodies )
		{
			if ( selection.Matches( b ) )
				bodies.Add( b );
		}

		if ( bodies.Count > 0 )
			return bodies;

		if ( ctx.Bodies.Count == 0 )
		{
			Fail(
				"This studio has no bodies yet",
				"There is nothing to act on — the feature list has not produced a solid.",
				"Add a Primitive, or extrude a sketch first" );
		}

		Fail(
			"No matching body is selected",
			$"The studio has {ctx.Bodies.Count} body/bodies but none match this feature's selection.",
			"Clear the body selection to act on every body",
			"Pick a body that is still in the studio" );

		return bodies;
	}

	/// <summary>
	/// Run a blend on each selected body, using picked edges when any were given.
	///
	/// <paramref name="edges"/> empty means the blend's own angle threshold, which is how Fillet
	/// and Chamfer behaved before edges could be picked. Non-empty means only those edges, and a
	/// body that none of them land on is left alone rather than blended by the threshold — mixing
	/// the two would fillet a part you never pointed at.
	/// </summary>
	protected List<(Body Body, BlendReport Report)> BlendBodies( FeatureContext ctx, List<EdgeRef> edges,
		Func<PolyMesh, HashSet<EdgeKey>, BlendReport> blend )
	{
		var reports = new List<(Body Body, BlendReport Report)>();
		var picked = edges is { Count: > 0 };

		foreach ( var body in RequireBodies( ctx, BodiesOf() ) )
		{
			if ( !picked )
			{
				reports.Add( (body, blend( body.Mesh, null )) );
				continue;
			}

			var keys = FacePlane.ResolveEdges( body, edges );

			if ( keys.Count == 0 )
				continue;

			reports.Add( (body, blend( body.Mesh, keys )) );
		}

		if ( picked && reports.Count == 0 )
		{
			Fail(
				"None of the picked edges are on the selected bodies",
				"The edges were stored as geometry so they could survive a rebuild, and none of them could be re-found.",
				"Pick the edges again",
				"Clear the edge selection to blend every sharp edge" );
		}

		return reports;
	}

	/// <summary>The BodySelectionParam on this feature, or a fresh empty one (meaning every body)
	/// if the feature does not have one. Fillet and Chamfer both do.</summary>
	BodySelectionParam BodiesOf()
	{
		foreach ( var param in Parameters )
		{
			if ( param is BodySelectionParam selection )
				return selection;
		}

		return new BodySelectionParam( "Bodies" );
	}

	/// <summary>Assign blended meshes, or refuse, only after every body has been tried — so a
	/// failure leaves the studio as it was, which is Feature.Run's contract.</summary>
	protected void CommitBlend( List<(Body Body, BlendReport Report)> reports, string sizeLabel )
	{
		foreach ( var ( _, report ) in reports )
		{
			if ( report.Failure is null )
				continue;

			report.Failure.ParameterLabel ??= sizeLabel;

			if ( report.SuggestedSize > 0f )
				report.Failure.SuggestedValue ??= FloorThousandths( report.SuggestedSize );

			throw new FeatureException( report.Failure );
		}

		foreach ( var ( body, report ) in reports )
			body.Mesh = report.Mesh;

		var warnings = new List<FeatureDiagnostic>();

		foreach ( var ( _, report ) in reports )
			warnings.AddRange( report.Warnings );

		if ( warnings.Count == 0 )
			return;

		if ( warnings.Count == 1 )
		{
			ApplyDiagnostic( warnings[0] );
			return;
		}

		Warn(
			warnings[0].Problem,
			string.Join( " ", warnings.Select( w => w.Cause ).Where( c => !string.IsNullOrEmpty( c ) ) ),
			warnings.SelectMany( w => w.Remedies ).Distinct().ToArray() );
	}

	/// <summary>A number the user can type that is never larger than the true fit, so suggesting
	/// it cannot immediately fail again to rounding.</summary>
	protected static float FloorThousandths( float value ) => MathF.Floor( value * 1000f ) / 1000f;

	public override string ToString() => $"{TypeName} '{Name ?? Id}'{(Suppressed ? " (suppressed)" : "")}";
}
