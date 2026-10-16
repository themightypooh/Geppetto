using System;
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
	/// LEGACY, READ BY NOTHING. Tint/Replace from the vertex-colour era, kept as a field only so
	/// documents saved with it still load and round-trip unchanged. A layer's real blend is
	/// <see cref="Mode"/>; this name could not be reused because a ChoiceParam saves its INDEX, and an
	/// old "Replace" (1) would have loaded as Multiply.
	/// </summary>
	public readonly ChoiceParam Blend = new( "Blend", new[] { "Tint", "Replace" } );

	/// <summary>How this layer combines with the paint below it. See <see cref="PaintLayers"/>.</summary>
	public readonly ChoiceParam Mode = new( "Blend Mode", Enum.GetNames<PaintBlendMode>() );

	/// <summary>How much of the layer shows, 0..1, on top of its own coverage and mask.</summary>
	public readonly FloatParam Opacity = new( "Opacity", 1f, 0f, 1f );

	/// <summary>
	/// Whether the layer starts fully covered with the fill values below, rather than empty. A fill
	/// layer is how a whole surface gets one material — rust, dirt, wet sheen — that a mask then
	/// confines; strokes on the layer still paint over the fill.
	/// </summary>
	public readonly BoolParam Fill = new( "Fill Layer", false );

	public readonly BoolParam FillUseColor = new( "Use Color", true );
	public readonly Vec3Param FillColor = new( "Color", new Vec3( 0.42f, 0.32f, 0.24f ) );
	public readonly BoolParam FillUseRoughness = new( "Use Roughness", false );
	public readonly FloatParam FillRoughness = new( "Roughness", 0.8f, 0f, 1f );
	public readonly BoolParam FillUseMetalness = new( "Use Metallic", false );
	public readonly FloatParam FillMetalness = new( "Metallic", 0f, 0f, 1f );
	public readonly BoolParam FillUseHeight = new( "Use Height", false );
	public readonly FloatParam FillHeight = new( "Height", 0.5f, 0f, 1f );

	/// <summary>Where the layer's mask comes from, before mask painting. Option text matches
	/// <see cref="PaintMaskGenerator"/>, spaced for reading.</summary>
	public readonly ChoiceParam MaskGenerator = new( "Mask Generator", MaskOptions );

	public static readonly string[] MaskOptions = { "None", "Edge Wear", "Dirt", "Gradient", "Material ID" };

	/// <summary>Where the generated mask switches from hidden to shown, 0..1.</summary>
	public readonly FloatParam MaskLevel = new( "Mask Level", 0.5f, 0f, 1f );

	/// <summary>How wide the switch is: 0 is a hard edge, 1 a long fade.</summary>
	public readonly FloatParam MaskSoftness = new( "Mask Softness", 0.3f, 0f, 1f );

	/// <summary>Swap what the mask shows and hides.</summary>
	public readonly BoolParam InvertMask = new( "Invert Mask", false );

	/// <summary>The material slot the Material ID generator selects.</summary>
	public readonly IntParam MaskSlot = new( "Mask Material Slot", 0, 0, 63 ) { Slider = false };

	/// <summary>
	/// Every layer setting, for the script and the feature dialog. Blend is carried for old documents
	/// and deliberately absent: showing a combo that changes nothing is a bug report waiting to be filed.
	/// </summary>
	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Bodies, Resolution, Mode, Opacity,
		Fill, FillUseColor, FillColor, FillUseRoughness, FillRoughness, FillUseMetalness, FillMetalness, FillUseHeight, FillHeight,
		MaskGenerator, MaskLevel, MaskSoftness, InvertMask, MaskSlot,
	};

	public PaintBlendMode BlendMode => (PaintBlendMode)Math.Clamp( Mode.Index, 0, Enum.GetValues<PaintBlendMode>().Length - 1 );

	public PaintMaskGenerator Generator => (PaintMaskGenerator)Math.Clamp( MaskGenerator.Index, 0, MaskOptions.Length - 1 );

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

	// The replay cache: the layer's own maps last produced, and the topology + atlas + revision +
	// resolution they were produced from. Keyed on topology (vertex count and face indices,
	// deliberately not positions), the atlas (every corner UV, so a re-unwrap invalidates it), the
	// revision (so a new stroke does) and the resolution (so changing it re-replays rather than serving
	// a stale canvas at the old size). A parametric edit that moves geometry without changing
	// structure, UVs or resolution reuses the maps rather than re-replaying.
	PaintLayerMaps _strokeMaps;
	long _topologyId;
	long _atlasId;
	int _canvasRevision = -1;
	int _canvasResolution = -1;

	// The layer with its fill under the strokes, keyed on the stroke maps and the fill settings.
	PaintLayerMaps _layerMaps;
	PaintLayerMaps _layerFrom;
	string _fillKey;

	// The generated mask, which is the expensive part (a bake), keyed on everything it reads —
	// including positions, because curvature and occlusion move when the surface does.
	float[] _generated;
	string _generatedKey;

	// The composite last put on the body, and what it was composited from. The backdrop is keyed by
	// REFERENCE: the layer below hands back its own cached canvas while nothing below changed, and
	// a new object whenever something did.
	PaintCanvas _cachedCanvas;
	PaintCanvas _compositeBelow;
	PaintLayerMaps _compositeLayer;
	string _compositeKey;

	public override bool IsStale => Revision != _builtRevision;

	/// <summary>The paint this feature put on its body — the layers below with this one composited
	/// over them — or null before a build. The editor shows it after a rebuild.</summary>
	public PaintCanvas Canvas => _cachedCanvas;

	/// <summary>The body's paint from the layers BELOW this one, as the last build found it. The live
	/// paint session shows this under the layer being painted, so painting a layer in the middle of a
	/// stack shows the stack.</summary>
	public PaintCanvas Below { get; private set; }

	/// <summary>This layer's own content (fill and strokes), before compositing, or null before a build.</summary>
	public PaintLayerMaps Layer => _layerMaps;

	/// <summary>The final mask (generator, mask paint, invert) the last build used, or null when
	/// the layer shows everywhere.</summary>
	public float[] Mask { get; private set; }

	/// <summary>The generator's mask after Mask Level and Softness, without mask paint or invert —
	/// what the live session combines with its own mask strokes. Null with no generator.</summary>
	public float[] GeneratorMask { get; private set; }

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

		// ONE BODY, LOUDLY. An empty selection matches every body, so a two-body studio used to
		// walk in here, skip the replay, and leave both canvases untouched with no error — paint
		// that looks like it ran and did nothing. Fail at the door so the editor's "pick one"
		// prompt and the kernel agree.
		if ( targets.Count != 1 )
		{
			Fail(
				"Paint paints one body at a time",
				$"This feature matched {targets.Count} bodies and a paint layer is one stroke list, one atlas.",
				"Pick one body in the Parts list",
				"Add a Paint per body rather than one Paint over all of them" );
		}

		var mesh = targets[0].Mesh;
		var below = mesh.Paint;
		Below = below;

		var hasStrokes = Strokes is { Count: > 0 };
		var fill = Fill.Value && (FillUseColor.Value || FillUseRoughness.Value || FillUseMetalness.Value || FillUseHeight.Value);
		var generator = Generator;

		if ( Fill.Value && !fill )
		{
			Warn( "This fill layer fills nothing",
				"Fill Layer is on but every channel (Use Color, Use Roughness, Use Metallic, Use Height) is off.",
				"Turn on the channels the fill should write",
				"Turn Fill Layer off if you meant to paint strokes only" );
		}

		if ( (fill || generator != PaintMaskGenerator.None) && !PaintMasks.HasUVs( mesh ) )
		{
			FailOn( fill ? "Fill Layer" : "Mask Generator",
				"This paint layer has nowhere to go",
				"The body has no UV layout, so no texel belongs to any face — a fill or a generated mask needs one.",
				"Add a UV Project in Unwrap mode above this layer",
				"Or enter Paint on the body once — it unwraps it for you" );
		}

		var resolution = Resolution.Clamped;
		var topology = MultiresSculpt.TopologyId( mesh );
		var atlas = AtlasId.Of( mesh );

		// The generated mask is made even for a layer with nothing on it yet, so the live session
		// can show it the moment the first stroke lands rather than after the next rebuild.
		var generatedKey = generator == PaintMaskGenerator.None
			? "none"
			: $"{generator}|{MaskSlot.Clamped}|{resolution}|{topology}|{atlas}|{PositionHash( mesh )}";

		if ( _generatedKey != generatedKey )
		{
			_generated = PaintMasks.Generate( mesh, resolution, generator, MaskSlot.Clamped );
			_generatedKey = generatedKey;
		}

		var shaped = _generated is null ? null : PaintLayers.Shape( (float[])_generated.Clone(), MaskLevel.Clamped, MaskSoftness.Clamped );
		GeneratorMask = shaped;

		if ( !hasStrokes && !fill )
		{
			// A fresh layer with nothing on it yet. Not a failure — it is how every layer starts —
			// and the body's paint passes through untouched.
			_cachedCanvas = below;
			Mask = null;
			_builtRevision = Revision;
			return;
		}

		if ( _strokeMaps is null || _topologyId != topology || _atlasId != atlas
			|| _canvasRevision != Revision || _canvasResolution != resolution )
		{
			_strokeMaps = PaintLayers.Replay( mesh, Strokes, resolution );
			_topologyId = topology;
			_atlasId = atlas;
			_canvasRevision = Revision;
			_canvasResolution = resolution;
		}

		var fillKey = fill
			? $"{FillUseColor.Value}|{FillColor.Value.x}|{FillColor.Value.y}|{FillColor.Value.z}|{FillUseRoughness.Value}|{FillRoughness.Clamped}|"
				+ $"{FillUseMetalness.Value}|{FillMetalness.Clamped}|{FillUseHeight.Value}|{FillHeight.Clamped}"
			: "none";

		if ( _layerMaps is null || !ReferenceEquals( _layerFrom, _strokeMaps ) || _fillKey != fillKey )
		{
			_layerMaps = fill ? WithFill( _strokeMaps, resolution ) : _strokeMaps;
			_layerFrom = _strokeMaps;
			_fillKey = fillKey;
		}

		Mask = PaintLayers.FinalMask( shaped, _layerMaps.MaskPaint, InvertMask.Value, resolution, resolution );

		if ( !_layerMaps.HasSurface )
		{
			Warn( "This layer only has mask paint",
				"Every stroke on it was painted on the Layer Mask, so there is nothing for the mask to show.",
				"Switch the channel to Base Color (or another surface channel) and paint",
				"Or turn on Fill Layer so the mask confines a fill" );
		}
		else if ( Opacity.Clamped <= 0f )
		{
			Warn( "This layer is invisible",
				"Its Opacity is 0, so it adds nothing to the surface.",
				"Raise Opacity",
				"Suppress the layer instead if you meant to hide it" );
		}
		else if ( Mask is not null && MaxOf( Mask ) < 1e-3f )
		{
			Warn( "The layer mask hides the whole layer", MaskEmptyCause( mesh ),
				generator == PaintMaskGenerator.MaterialId ? "Pick a Mask Material Slot a face is on" : "Lower Mask Level or raise Mask Softness",
				"Turn Invert Mask off (or on)",
				"Paint white on the Layer Mask where it should show" );
		}

		var compositeKey = $"{BlendMode}|{Opacity.Clamped}|{MaskLevel.Clamped}|{MaskSoftness.Clamped}|{InvertMask.Value}|{generatedKey}";

		if ( _cachedCanvas is null || !ReferenceEquals( _compositeBelow, below ) || !ReferenceEquals( _compositeLayer, _layerMaps )
			|| _compositeKey != compositeKey )
		{
			_cachedCanvas = PaintLayers.CompositeSet( below, _layerMaps, BlendMode, Opacity.Clamped, Mask );
			_compositeBelow = below;
			_compositeLayer = _layerMaps;
			_compositeKey = compositeKey;
		}

		mesh.Paint = _cachedCanvas;

		// Last, so a failure above leaves the feature stale and the next rebuild tries again.
		_builtRevision = Revision;
	}

	/// <summary>The layer's stroke maps over a solid fill for every channel the fill writes. The fill
	/// is the layer's base; strokes on the same channel paint over it in Normal.</summary>
	PaintLayerMaps WithFill( PaintLayerMaps strokes, int size )
	{
		var maps = new PaintLayerMaps { Size = size, MaskPaint = strokes.MaskPaint };

		maps.Color = Over( FillUseColor.Value
			? PaintLayers.Solid( size, size, Byte( FillColor.Value.x ), Byte( FillColor.Value.y ), Byte( FillColor.Value.z ) )
			: null, strokes.Color );
		maps.Roughness = Over( FillUseRoughness.Value ? Grey( FillRoughness.Clamped, size ) : null, strokes.Roughness );
		maps.Metalness = Over( FillUseMetalness.Value ? Grey( FillMetalness.Clamped, size ) : null, strokes.Metalness );
		maps.Height = Over( FillUseHeight.Value ? Grey( FillHeight.Clamped, size ) : null, strokes.Height );

		return maps;
	}

	static PaintCanvas Over( PaintCanvas fill, PaintCanvas strokes ) =>
		fill is null ? strokes : PaintLayers.Composite( fill, strokes, PaintBlendMode.Normal, 1f, null );

	static PaintCanvas Grey( float value, int size )
	{
		var b = Byte( value );
		return PaintLayers.Solid( size, size, b, b, b );
	}

	static byte Byte( float v ) => PaintReplay.ToByte( v );

	static float MaxOf( float[] values )
	{
		var max = 0f;

		foreach ( var v in values )
			max = MathF.Max( max, v );

		return max;
	}

	string MaskEmptyCause( PolyMesh mesh )
	{
		if ( Generator == PaintMaskGenerator.MaterialId )
		{
			foreach ( var face in mesh.Faces )
			{
				if ( face.Material == MaskSlot.Clamped )
					return $"Mask Level {MaskLevel.Clamped:0.##} with Invert Mask on leaves nothing showing on slot {MaskSlot.Clamped}.";
			}

			return $"No face of this body is on material slot {MaskSlot.Clamped}, so Material ID selects nothing.";
		}

		return Generator == PaintMaskGenerator.None
			? "The Layer Mask is painted black everywhere it covers, and Invert Mask hides the rest."
			: $"Mask Level {MaskLevel.Clamped:0.##} is above every value the {MaskOptions[(int)Generator]} generator found on this body.";
	}

	/// <summary>A cheap hash of the vertex positions, so a generated mask follows a sculpt or a move.</summary>
	static long PositionHash( PolyMesh mesh )
	{
		unchecked
		{
			long hash = 1469598103934665603;

			foreach ( var p in mesh.Positions )
			{
				hash = (hash ^ BitConverter.SingleToInt32Bits( p.x )) * 1099511628211;
				hash = (hash ^ BitConverter.SingleToInt32Bits( p.y )) * 1099511628211;
				hash = (hash ^ BitConverter.SingleToInt32Bits( p.z )) * 1099511628211;
			}

			return hash;
		}
	}
}
