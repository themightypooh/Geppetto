using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Shape Key: start recording a shape key (morph target, blend shape) on bodies. Every feature
/// between this and <see cref="ShapeKeyEndFeature"/> - a Stroke with any sculpt brush, Surface
/// Detail, a Sculpt, an edit-op move, a Deform - shapes the key instead of the body: End Shape Key
/// turns how far each vertex moved into the key and puts the body back as it was here.
///
/// So a sac that swells, a maw that gapes or a chest that splits is modelled with the tools that
/// model everything else, saved in the history like everything else, and rebuilt when the body
/// under it changes. The key goes out with the model (DMX delta states, the preview's morphs) and
/// Marionette keys it over time (morph curves).
///
/// A KEY CAN ONLY MOVE VERTICES. A feature inside it that adds or removes vertices (extrude,
/// subdivide, remesh) cannot be expressed as a morph; End Shape Key says which body and stops.
/// </summary>
public sealed class ShapeKeyFeature : Feature, IShapeKeyStart
{
	public override string TypeName => "Shape Key";

	public override GeometryKind Accepts => GeometryKind.Body;

	/// <summary>The key's name - the morph's (flex controller's) name in the model, what a
	/// Marionette morph curve and SkinnedModelRenderer.Morphs address. Empty: the feature's name.</summary>
	public readonly StringParam Key = new( "Key name" );

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	// "Driven by": empty bone, keyed by hand only. See ShapeKeyDriver / MorphDriver.
	public readonly StringParam Bone = ShapeKeyDriver.BoneParam();
	public readonly ChoiceParam Channel = ShapeKeyDriver.ChannelParam();
	public readonly ChoiceParam Axis = ShapeKeyDriver.AxisParam();
	public readonly StringParam OtherBone = ShapeKeyDriver.OtherBoneParam();
	public readonly FloatParam RestAngle = ShapeKeyDriver.RestParam();
	public readonly FloatParam TargetAngle = ShapeKeyDriver.TargetParam();
	public readonly ChoiceParam Ease = ShapeKeyDriver.EaseParam();
	public readonly BoolParam Clamp = ShapeKeyDriver.ClampParam();

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Key, Bodies, Bone, Channel, Axis, OtherBone, RestAngle, TargetAngle, Ease, Clamp };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Bone, Channel, Axis, OtherBone, RestAngle, TargetAngle, Ease, Clamp };

	/// <summary>Key name, or the feature's own name with spaces made underscores ("Shape Key 1" →
	/// Shape_Key_1), or "shape_key".</summary>
	public string KeyName => ShapeKeyDriver.KeyName( Key.Value, Name, "shape_key" );

	/// <summary>The bone reading that sets the key's weight, or null when it is keyed by hand.</summary>
	public MorphDriver Driver => ShapeKeyDriver.From( KeyName, Bone, Channel, Axis, OtherBone, RestAngle, TargetAngle, Ease, Clamp );

	BodySelectionParam IShapeKeyStart.KeyBodies => Bodies;
	StringParam IShapeKeyStart.KeyNameParam => Key;

	/// <summary>Called after a rebuild: a body still recording means the last Shape Key was never
	/// ended. Warns on that feature (rolled back past its End is the usual cause, and is fine while
	/// sculpting the key - the warning says what an export would get).</summary>
	/// <param name="all">The whole history. A key whose End Shape Key is past the rollback bar is
	/// being edited (Shape Keys ▸ Edit key, <see cref="ShapeKeyEdit"/>), not left open: no warning.</param>
	internal static void WarnUnclosed( IEnumerable<Feature> evaluated, FeatureContext ctx, RebuildReport report, IReadOnlyList<Feature> all = null )
	{
		// A Shape Key reused from the cache is not re-run, so it would keep the warning an earlier
		// rebuild gave it (open while its key was being recorded) after its End is added. Executing
		// one never warns, so a warning on it is always this one: clear it and decide afresh.
		foreach ( var stale in evaluated.Where( k => k is IShapeKeyStart && WarnedUnclosed.TryGetValue( k, out _ ) ).ToList() )
		{
			report.Warnings.RemoveAll( w => w.FeatureId == stale.Id && w.Message == stale.Warning );
			stale.Warning = null;
			WarnedUnclosed.Remove( stale );
		}

		var open = ctx.Bodies.Where( b => b.Mesh?.RecordingMorph is not null ).ToList();

		if ( open.Count == 0 )
			return;

		var key = open[0].Mesh.RecordingMorph.Name;

		if ( evaluated.LastOrDefault( f => f is IShapeKeyStart && !f.Suppressed && f.Error is null ) is not { } start )
			return;

		if ( all is not null && all.SkipWhile( f => !ReferenceEquals( f, start ) ).Skip( 1 )
			.TakeWhile( f => f is not IShapeKeyStart ).Any( f => f is ShapeKeyEndFeature ) )
		{
			return;
		}

		start.Warning = $"Shape key '{key}' is still open: nothing below it ends it, so {string.Join( ", ", open.Select( b => b.Name ) )} "
			+ "show the shape and the key is not exported. Add End Shape Key after the features that shape it.";
		WarnedUnclosed.AddOrUpdate( start, null );
		report.Warnings.Add( (start.Id, start.Warning) );
	}

	/// <summary>The key starts (plain or corrective) carrying this warning, so a later rebuild can take it back.</summary>
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Feature, object> WarnedUnclosed = new();

	protected override void Execute( FeatureContext ctx )
	{
		var name = KeyName;

		if ( string.IsNullOrWhiteSpace( name ) || name.Any( c => char.IsWhiteSpace( c ) || c == '"' ) )
		{
			Fail(
				"The shape key needs a name",
				string.IsNullOrWhiteSpace( name ) ? "A morph is addressed by name and this one has none." : $"'{name}' has spaces or quotes, which a morph name cannot.",
				"Set Key name, e.g. key=sac_swell" );
		}

		// A driven key whose driver cannot evaluate is still a key - it can be keyed by hand - so this
		// warns, naming what the driver needs, rather than failing the model.
		if ( Driver is { } driver && driver.Problems( ctx.Rig is { Count: > 0 } ? ctx.Rig : null ) is { Count: > 0 } problems )
		{
			Warn(
				$"'{name}' is driven but its driver cannot work yet",
				problems[0],
				ctx.Rig is { Count: > 0 } ? "Fix Driven by in the key's Advanced fields" : "Place bones in the Rig workspace so the driver has a bone to read" );
		}

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			if ( body.Mesh.RecordingMorph is { } open )
			{
				Fail(
					$"Shape key '{open.Name}' is still open on {body.Name}",
					"Keys are recorded one at a time, and the one before this has not been ended.",
					"Add End Shape Key before this one" );
			}

			body.Mesh.Morphs.Add( new MeshMorph { Name = name, Base = body.Mesh.Positions.ToArray(), Deltas = null, Driver = Driver } );
		}
	}
}

/// <summary>
/// End Shape Key: finish the key the last <see cref="ShapeKeyFeature"/> opened. Each recording
/// body's movement since then becomes the key's per-vertex offsets, and the body goes back to the
/// shape it had when the key opened. A key of the same name recorded earlier is replaced.
/// </summary>
public sealed class ShapeKeyEndFeature : Feature
{
	public override string TypeName => "End Shape Key";

	public override GeometryKind Accepts => GeometryKind.None;

	public override IReadOnlyList<IParam> Parameters => Array.Empty<IParam>();

	protected override void Execute( FeatureContext ctx )
	{
		var open = ctx.Bodies.Where( b => b.Mesh?.RecordingMorph is not null ).ToList();

		if ( open.Count == 0 )
		{
			Warn(
				"No shape key is open",
				"End Shape Key closes the key a Shape Key feature above it started, and there is none, so it does nothing.",
				"Add a Shape Key feature before the features that shape it" );
			return;
		}

		var still = new List<string>();

		foreach ( var body in open )
		{
			var mesh = body.Mesh;
			var morph = mesh.RecordingMorph;

			if ( morph.Base.Length != mesh.Positions.Count )
			{
				Fail(
					$"Shape key '{morph.Name}' changed {body.Name}'s vertex count",
					$"It had {morph.Base.Length} vertices when the key opened and has {mesh.Positions.Count} now. A shape key can only move vertices; a feature inside it added or removed some (extrude, subdivide, remesh, a boolean).",
					"Move that feature above the Shape Key, or below End Shape Key" );
			}

			if ( morph.IsCorrective )
			{
				// Sculpted posed: carry each vertex back through its own skinning to bind space, and
				// put the body back at bind rather than at the pose it was shown in. PoseSpaceCorrective.
				morph.Deltas = PoseSpaceCorrective.Deltas( morph.BindBase, morph.Unskin, mesh.Positions );

				for ( var i = 0; i < morph.BindBase.Length; i++ )
					mesh.Positions[i] = morph.BindBase[i];

				morph.BindBase = null;
				morph.Unskin = null;
			}
			else
			{
				morph.Deltas = mesh.Positions.Select( ( p, i ) => p - morph.Base[i] ).ToArray();

				for ( var i = 0; i < morph.Base.Length; i++ )
					mesh.Positions[i] = morph.Base[i];
			}

			morph.Base = null;
			mesh.Morphs.RemoveAll( m => m != morph && m?.Name == morph.Name );

			if ( morph.Deltas.All( d => d.LengthSquared < 1e-12f ) )
				still.Add( body.Name );
		}

		if ( still.Count > 0 )
		{
			Warn(
				$"Nothing moved on {string.Join( ", ", still )}",
				"No feature between Shape Key and End Shape Key moved those bodies, so their key is empty.",
				"Put a Stroke, Detail, Sculpt or move between them" );
		}
	}
}

/// <summary>
/// Keeps shape keys with their body across a feature that replaces the body's mesh object rather
/// than editing it in place (many do: a material pass, a remesh, a boolean, an edit op). Called by
/// PartStudio around every feature.
///
/// Same vertex count: the keys are carried over as they were - offsets per vertex index still mean
/// the same thing. A different count: an open key is carried with its now-wrong base so End Shape
/// Key names the problem; finished keys cannot be mapped onto new vertices, so they are dropped and
/// the feature that changed the count says so.
/// </summary>
internal static class ShapeKeyCarry
{
	public static Dictionary<string, (PolyMesh Mesh, List<MeshMorph> Morphs)> Before( FeatureContext ctx )
	{
		Dictionary<string, (PolyMesh, List<MeshMorph>)> keys = null;

		foreach ( var body in ctx.Bodies )
		{
			if ( body?.Mesh is { Morphs.Count: > 0 } mesh && body.Id is not null )
				(keys ??= new())[body.Id] = (mesh, mesh.Morphs.ToList());
		}

		return keys;
	}

	public static void After( Dictionary<string, (PolyMesh Mesh, List<MeshMorph> Morphs)> before, FeatureContext ctx, Feature feature )
	{
		if ( before is null )
			return;

		var dropped = new List<string>();

		foreach ( var body in ctx.Bodies )
		{
			if ( body?.Mesh is not { } mesh || body.Id is null || !before.TryGetValue( body.Id, out var was ) )
				continue;

			if ( ReferenceEquals( mesh, was.Mesh ) || mesh.Morphs.Count > 0 )
				continue;

			var count = mesh.Positions.Count;

			foreach ( var morph in was.Morphs )
			{
				if ( morph is null )
					continue;

				if ( morph.Recording || morph.Deltas?.Length == count )
					mesh.Morphs.Add( morph.Clone() );
				else
					dropped.Add( $"{morph.Name} on {body.Name}" );
			}
		}

		if ( dropped.Count > 0 && feature.Warning is null && feature.Error is null )
		{
			feature.Warning = $"Dropped shape key(s) {string.Join( ", ", dropped )}: this feature changed the vertex count, and a key's offsets "
				+ "are per vertex. Move it above the Shape Key, or record the keys after it.";
		}
	}
}
