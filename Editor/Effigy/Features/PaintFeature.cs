using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A paint layer in the feature tree.
///
/// WHERE THE PAINT LIVES: strokes in object space, replayed onto whatever the mesh currently is.
/// The texture atlas is a derived artifact — the same bet the rest of the kernel already made by
/// keeping the mesh a function of the feature history. Nothing else in the document holds paint, undo
/// is the feature tree's undo, and a stroke is one entry in the list.
///
/// EXECUTE REPLAYS THE STROKES ONTO A TEXTURE ATLAS. The dab — faces in radius, reject the far side
/// by its normal, falloff-weighted coverage — lives in PaintReplay, shared with the live session so a
/// stroke painted by hand and the same stroke rebuilt later produce identical texels. A texture atlas
/// rather than vertex colours because paint resolution must not equal mesh density: a bare box paints
/// at the same texel resolution a sculpted part does, which is the whole reason the vertex-colour
/// path was replaced.
///
/// THE ATLAS IS KEYED TO THE UV LAYOUT, which is the trap the vertex-colour cache never had. A
/// re-unwrap keeps the topology and moves every island, so a canvas cached on topology alone would be
/// handed back against a rearranged atlas — paint scattered onto unrelated faces, silently. The cache
/// is keyed on both the topology id and <see cref="AtlasId"/>.
///
/// ITS STALENESS GUARD IS COPIED FROM SculptFeature FOR THE SAME REASON. A paint session appends
/// strokes nowhere near the studio, so nothing calls MarkDirty and the rebuild would happily reuse
/// the cached body from before the stroke — the paint would stop following the brush, which reads as
/// "the paint tool does nothing" rather than as a caching bug. A revision counter bumped when the
/// stroke list changes, compared here to what the last rebuild built from, is the guard.
/// </summary>
public sealed class PaintFeature : Feature
{
	/// <summary>
	/// How many texels across the replayed canvas is.
	///
	/// A PARAMETER RATHER THAN A CONSTANT because it belongs to the document, the same way a sculpt
	/// cage's level does: a part with one small painted detail and one big painted wall wants two
	/// answers, and 1024 texels across a matchbox is enormous while across a 4000-unit part it is
	/// four texels per inch. Strokes are resolution-independent — points and radii, replayed — so a
	/// change re-replays and loses nothing.
	/// </summary>
	public readonly IntParam Resolution = new( "Resolution", 1024, 64, 4096 );

	public override string TypeName => "Paint";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Body" );

	/// <summary>
	/// Whether the paint tints what is underneath it or stands in for it.
	///
	/// CARRIED FOR THE DOCUMENT FORMAT, NOT READ BY THE ATLAS. The vertex-colour path used it to pick
	/// which material an unbound slot compiled to (tint keeps default.vmat, replace binds white.vmat);
	/// a texture atlas is the surface colour itself, and telling tint from replace through a texture
	/// needs a shader that combines the base material with the atlas, which nothing shipped provides.
	/// The atlas covers. The choice stays on the feature so documents saved before the switch still
	/// load and round-trip unchanged.
	/// </summary>
	public readonly ChoiceParam Blend = new( "Blend", new[] { "Tint", "Replace" } );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Resolution, Blend };

	/// <summary>
	/// The strokes, in the order they were painted.
	///
	/// NULL UNTIL THE FIRST STROKE LANDS, the same "not yet populated" idiom SculptFeature uses for
	/// its levels. A never-painted feature serialises to nothing at all: StudioDocument writes a null
	/// field as absent, and the reflection sweep in DocumentTests round-trips a null list as null.
	/// </summary>
	public List<PaintStroke> Strokes;

	/// <summary>Bumped each time the stroke list changes, so <see cref="IsStale"/> can notice
	/// without anyone remembering to call MarkDirty.</summary>
	public int Revision { get; private set; }

	// The revision this feature last built from. See the class comment — a stroke lands nowhere near
	// the studio, so nothing calls MarkDirty and this is what catches it.
	int _builtRevision = -1;

	// The replay cache: the canvas last produced, and the topology + atlas + revision + resolution it
	// was produced from. Keyed on topology (vertex count and face indices, deliberately not
	// positions), the atlas (every corner UV, so a re-unwrap invalidates it), the revision (so a new
	// stroke does) and the resolution (so changing it re-replays rather than serving a stale canvas
	// at the old size). A parametric edit that moves geometry without changing structure, UVs or
	// resolution reuses the canvas rather than re-replaying.
	PaintCanvas _cachedCanvas;
	long _topologyId;
	long _atlasId;
	int _canvasRevision = -1;
	int _canvasResolution = -1;

	public override bool IsStale => Revision != _builtRevision;

	/// <summary>The canvas last replayed by this feature, or null before a build. The editor reads it
	/// to show paint after a rebuild — it is the same canvas <see cref="Execute"/> put on the body.</summary>
	public PaintCanvas Canvas => _cachedCanvas;

	/// <summary>Append a stroke and mark the feature stale, so the next rebuild replays it. The list
	/// is lazily created here so a fresh feature never has to check for null before painting.</summary>
	public void AddStroke( PaintStroke stroke )
	{
		(Strokes ??= new()).Add( stroke );
		Revision++;
	}

	/// <summary>
	/// Replace the whole stroke list — undo/redo's route in.
	///
	/// The revision is bumped, not merely the list swapped, because the replay cache is keyed on it: a
	/// plain assignment would leave <see cref="Revision"/> unchanged, the cache would see no reason to
	/// re-render, and the model would keep serving paint the restored strokes do not describe. The
	/// strokes themselves are copied by reference — they are immutable once painted, so sharing them
	/// across undo snapshots is the correct and cheapest read.
	/// </summary>
	public void ReplaceStrokes( IReadOnlyList<PaintStroke> strokes )
	{
		Strokes = strokes is null ? null : new List<PaintStroke>( strokes );
		Revision++;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );

		// Paint paints ONE body at a time — one stroke list, one atlas. A studio with several bodies
		// needs a picked body, which is exactly what the editor's door gate asks for before a session
		// starts.
		if ( Strokes is { Count: > 0 } && targets.Count == 1 )
		{
			var mesh = targets[0].Mesh;
			var topology = MultiresSculpt.TopologyId( mesh );
			var atlas = AtlasId.Of( mesh );
			var resolution = Resolution.Clamped;

			if ( _cachedCanvas is null || _topologyId != topology || _atlasId != atlas
				|| _canvasRevision != Revision || _canvasResolution != resolution )
			{
				_cachedCanvas = PaintReplay.Replay( mesh, Strokes, resolution );
				_topologyId = topology;
				_atlasId = atlas;
				_canvasRevision = Revision;
				_canvasResolution = resolution;
			}

			mesh.Paint = _cachedCanvas;
		}

		// Last, so a failure above leaves the feature stale and the next rebuild tries again.
		_builtRevision = Revision;
	}
}
