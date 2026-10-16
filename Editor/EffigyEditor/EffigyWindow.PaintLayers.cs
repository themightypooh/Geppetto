using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Paint layers and channels in the window: the Channel and Layers stages on the paint bar, the
/// floating layer stack, and the texture-set export.
///
/// A LAYER IS A PAINT FEATURE and the stack is the feature tree's Paints on one body, in tree order.
/// So adding a layer inserts a Paint after the open one, reordering moves features, deleting removes
/// one, and every setting is an ordinary parameter — undo, save and rebuild need nothing new. The
/// arithmetic (blend, mask, fill, channels) lives in the kernel: PaintLayers, PaintMasks,
/// PaintTextureSet; this file only moves features and refreshes what is on screen.
/// </summary>
public sealed partial class EffigyWindow
{
	private EffigyPaintLayersPanel _paintLayersPanel;

	private readonly Dictionary<PaintChannel, EffigyStageTool> _paintChannelTools = new();

	/// <summary>The floating layer stack, under the paint bar. Called once, beside the bar's own setup.</summary>
	private void CreatePaintLayersPanel()
	{
		if ( _viewport is null || _paintBar is null )
			return;

		_paintLayersPanel = new EffigyPaintLayersPanel( _viewport.Canvas )
		{
			Selected = SelectPaintLayer,
			Added = AddPaintLayer,
			Removed = RemovePaintLayer,
			Moved = MovePaintLayer,
			Edited = OnPaintLayerEdited,
		};

		_viewport.AddPaintOverlay( _paintLayersPanel );
		_paintLayersPanel.Position = _paintBar.Position + new Vector2( 0f, EffigyPaintBar.BarHeight + 8f );

		_paintBar.ChannelChanged = OnPaintChannelPicked;
	}

	/// <summary>
	/// The Channel and Layers stages, appended to the paint brush's stage set. On the bar so they
	/// are where the brush is, and in Space search because search reads the bar.
	/// </summary>
	private IEnumerable<EffigyStage> BuildPaintLayerStages()
	{
		var channels = new EffigyStage { Name = "Channel" };

		var tips = new Dictionary<PaintChannel, string>
		{
			[PaintChannel.Color] = "Paint the surface color.",
			[PaintChannel.Roughness] = "Paint how rough or glossy the surface is: dark is shiny, light is matte.",
			[PaintChannel.Metalness] = "Paint which parts are bare metal: white is metal, black is not.",
			[PaintChannel.Height] = "Paint bumps and dents: light raises, dark sinks. Exported as a normal map.",
			[PaintChannel.Mask] = "Paint where this layer shows: white shows it, black hides it.",
		};

		foreach ( var channel in Enum.GetValues<PaintChannel>() )
		{
			var ch = channel;
			var tool = new EffigyStageTool
			{
				Icon = EffigyIcon.PaintBrush,
				Label = PaintChannels.Label( ch ),
				Tip = tips[ch],
				Checkable = true,
				Checked = ch == PaintChannel.Color,
				Clicked = () => SetPaintChannel( ch ),
			};

			_paintChannelTools[ch] = tool;
			channels.Add( tool );
		}

		var layers = new EffigyStage { Name = "Layers" };

		layers.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Paint,
			Label = "New Layer",
			Tip = "Add an empty paint layer above this one. Layers stack: each has its own strokes, blend mode and opacity.",
			Clicked = () => AddPaintLayer( false ),
		} );

		layers.Add( NewFillLayerTool() );

		return new[] { channels, layers };
	}

	/// <summary>"Fill Layer", for both the paint bar and the Paint workspace's landing bar.</summary>
	private EffigyStageTool NewFillLayerTool() => new()
	{
		Icon = EffigyIcon.Paint,
		Label = "Fill Layer",
		Tip = "Add a layer that covers the whole surface with one color or material value. Give it a mask (edge wear, dirt, gradient, material) to confine it.",
		Clicked = () => AddPaintLayer( true ),
	};

	/// <summary>Called by EnterPaint once the session exists: hand the session this layer's place in
	/// the stack (the paint below, the blend, the mask) and show the stack.</summary>
	private void BindPaintLayers( PaintSession session, PaintFeature feature )
	{
		if ( session is null || feature is null )
			return;

		ApplyLayerToSession( session, feature );
		_paintLayersPanel?.Bind( PaintLayersOf( feature ), feature );
		_paintBar?.Refresh();
		UpdatePaintChannelChecks();
	}

	private void UnbindPaintLayers() => _paintLayersPanel?.Bind( null, null );

	private static void ApplyLayerToSession( PaintSession session, PaintFeature feature )
	{
		session.Below = feature.Below;
		session.BlendMode = feature.BlendMode;
		session.Opacity = feature.Opacity.Clamped;
		session.GeneratedMask = feature.GeneratorMask;
		session.InvertMask = feature.InvertMask.Value;
	}

	/// <summary>Every Paint on the same body as <paramref name="feature"/>, in tree (bottom-up) order.</summary>
	private List<PaintFeature> PaintLayersOf( PaintFeature feature )
	{
		var body = _studio.Bodies.FirstOrDefault( b => feature.Bodies.Matches( b ) );

		if ( body is null )
			return new List<PaintFeature> { feature };

		return _studio.Features.OfType<PaintFeature>()
			.Where( p => ReferenceEquals( p, feature ) || p.Bodies.BodyIds.Count == 0 || p.Bodies.BodyIds.Contains( body.Id ) )
			.ToList();
	}

	private void SetPaintChannel( PaintChannel channel )
	{
		if ( _viewport?.PaintSession is not { } session )
		{
			SetPrompt( "Open a Paint layer first — the channel is what the brush paints on it." );
			return;
		}

		if ( session.IsStroking )
			return;

		session.Channel = channel;
		_paintBar?.Refresh();
		OnPaintChannelPicked();
	}

	/// <summary>The channel changed (from a stage tool or the bar's own combo): tick the matching tool,
	/// re-upload the preview and say what is being painted. Does not refresh the bar — when the bar's
	/// combo raised this, rebuilding the combo inside its own callback is the thing to avoid.</summary>
	private void OnPaintChannelPicked()
	{
		UpdatePaintChannelChecks();
		_viewport?.RefreshPaintPreview();

		if ( _viewport?.PaintSession is { } session )
		{
			SetPrompt( session.Channel switch
			{
				PaintChannel.Color => "Painting Base Color.",
				PaintChannel.Mask => "Painting the Layer Mask: white shows this layer, black hides it. The model shows the mask while you paint it.",
				_ => $"Painting {PaintChannels.Label( session.Channel )}: the model shows it as grey. Set the value on the bar.",
			} );
		}
	}

	private void UpdatePaintChannelChecks()
	{
		var current = _viewport?.PaintSession?.Channel ?? PaintChannel.Color;

		foreach ( var (channel, tool) in _paintChannelTools )
			tool.Checked = channel == current;

		_stageBar?.Refresh();
	}

	/// <summary>Add a layer above the open one (or at the rollback bar with none open) and paint it.</summary>
	private void AddPaintLayer( bool fill )
	{
		if ( _viewport is null )
			return;

		var current = _paintFeature ?? _lastPaintFeature;

		if ( current is not null && !_studio.Features.Contains( current ) )
			current = null;

		LeaveCurrentWorkspace();
		RecordUndo();

		var layer = new PaintFeature();
		layer.Fill.Value = fill;

		if ( current is not null )
		{
			layer.Bodies.BodyIds = new List<string>( current.Bodies.BodyIds );
			layer.Resolution.Value = current.Resolution.Value;

			var at = _studio.Features.IndexOf( current ) + 1;

			if ( _studio.RollbackIndex < _studio.Features.Count && _studio.RollbackIndex >= at )
				_studio.RollbackIndex++;

			_studio.Insert( at, layer );
		}
		else
		{
			ApplyIdleGeometrySelection( layer );
			InsertAtRollback( layer );
		}

		RebuildStudio();
		EnterPaint( layer );

		if ( fill )
			SetPrompt( "Fill layer added: it covers the whole surface. Pick a Mask (Edge Wear, Dirt, Gradient, Material ID) in the layer settings to confine it." );
	}

	private void SelectPaintLayer( PaintFeature layer )
	{
		if ( layer is null || ReferenceEquals( layer, _paintFeature ) )
			return;

		EnterPaint( layer );
	}

	private void RemovePaintLayer( PaintFeature layer )
	{
		if ( layer is null || !_studio.Features.Contains( layer ) )
			return;

		var stack = PaintLayersOf( layer );
		var index = stack.IndexOf( layer );
		var next = ReferenceEquals( layer, _paintFeature )
			? (index > 0 ? stack[index - 1] : stack.Count > 1 ? stack[1] : null)
			: _paintFeature;

		LeaveCurrentWorkspace();
		RecordUndo();
		_studio.Remove( layer );
		RebuildStudio();

		if ( next is not null && _studio.Features.Contains( next ) )
			EnterPaint( next );
		else
			ShowPaintHome();

		SetPrompt( $"Deleted {layer.Name ?? "the layer"}. Ctrl+Z brings it back." );
	}

	/// <summary>Swap a layer with its neighbour in the stack: +1 up (later in the tree, painted over),
	/// −1 down. Moves the feature in the tree, so the order saves and undoes like any reorder.</summary>
	private void MovePaintLayer( PaintFeature layer, int direction )
	{
		if ( layer is null )
			return;

		var stack = PaintLayersOf( layer );
		var index = stack.IndexOf( layer );
		var neighbour = index + Math.Sign( direction );

		if ( index < 0 || neighbour < 0 || neighbour >= stack.Count )
		{
			SetPrompt( direction > 0 ? "That layer is already on top." : "That layer is already at the bottom." );
			return;
		}

		var open = _paintFeature;

		LeaveCurrentWorkspace();
		RecordUndo();
		_studio.Move( _studio.Features.IndexOf( layer ), _studio.Features.IndexOf( stack[neighbour] ) );
		RebuildStudio();

		if ( open is not null && _studio.Features.Contains( open ) )
			EnterPaint( open );
	}

	/// <summary>A layer setting changed in the stack panel: rebuild, then show the open layer with the
	/// new blend, opacity and mask.</summary>
	private void OnPaintLayerEdited( PaintFeature layer )
	{
		if ( layer is null )
			return;

		RecordUndo();
		_studio.MarkDirty( layer );
		RebuildStudio();

		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}

		if ( _viewport?.PaintSession is { } session && _paintFeature is not null )
		{
			ApplyLayerToSession( session, _paintFeature );
			session.Canvas.Invalidate();
			_viewport.RefreshPaintPreview();
			_paintLayersPanel?.Bind( PaintLayersOf( _paintFeature ), _paintFeature );
		}

		if ( layer.Diagnostic is { } problem )
			SetPrompt( $"{layer.Name}: {problem.Problem}. {problem.Cause}" );
	}

	/// <summary>
	/// Write one painted body's texture set and bind its .vmat — colour always, and roughness,
	/// metallic and a normal map from painted height when those were painted. Returns the material
	/// path. The colour-only material this replaces wrote exactly the colour image of this set.
	/// </summary>
	private static string WritePaintTextureSet( PaintCanvas paint, string name, string tag, string folder )
	{
		var written = PaintTextureSet.Write( paint, folder, $"{name}_{tag}_paint", path => $"models/effigy/{Path.GetFileName( path )}" );
		var relVmat = $"models/effigy/{Path.GetFileName( written[^1] )}";

		Log.Info( $"[Effigy] authored paint texture set {relVmat}: {string.Join( ", ", written.Select( Path.GetFileName ) )}" );
		return relVmat;
	}
}
