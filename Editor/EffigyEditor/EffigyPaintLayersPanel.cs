using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// The paint layer stack, floating under the paint bar: every Paint on the body being painted, top
/// layer first the way every layer-based painter lists them, each with its blend mode, opacity and
/// order; and below the list, the open layer's fill and mask settings.
///
/// A LAYER IS A PAINT FEATURE. Nothing here owns a layer: the list is read off the feature tree,
/// reordering moves features, deleting removes one, and every setting is a parameter on the
/// feature — so a layer saves, undoes and replays like the rest of the history. This widget only
/// raises what the user asked for; the window does it (see EffigyWindow.PaintLayers.cs).
///
/// Rebuilt wholesale on every refresh rather than patched: a stack is a handful of rows, and a
/// rebuild cannot leave a row showing a layer that has moved.
/// </summary>
internal sealed class EffigyPaintLayersPanel : Widget
{
	public const float PanelWidth = 360f;
	const float RowHeight = 26f;

	/// <summary>Open this layer for painting.</summary>
	public Action<PaintFeature> Selected { get; set; }

	/// <summary>Add a layer above the open one; true for a fill layer.</summary>
	public Action<bool> Added { get; set; }

	public Action<PaintFeature> Removed { get; set; }

	/// <summary>Move a layer up (+1) or down (−1) the stack.</summary>
	public Action<PaintFeature, int> Moved { get; set; }

	/// <summary>A setting on this layer changed; the window rebuilds and refreshes the preview.</summary>
	public Action<PaintFeature> Edited { get; set; }

	private IReadOnlyList<PaintFeature> _layers = Array.Empty<PaintFeature>();
	private PaintFeature _active;

	public Color GapColor { get; set; } = Theme.ControlBackground;

	public EffigyPaintLayersPanel( Widget parent ) : base( parent )
	{
		TranslucentBackground = true;
		NoSystemBackground = true;
		Visible = false;
		FixedWidth = PanelWidth;

		Layout = Layout.Column();
		Layout.Spacing = 2;
		Layout.Margin = new Sandbox.UI.Margin( 6 );
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor.WithAlpha( 0.92f ) );
		Paint.DrawRect( LocalRect, 4f );
	}

	/// <summary>Show <paramref name="layers"/> (bottom first, tree order) with <paramref name="active"/>
	/// open; null hides the panel.</summary>
	public void Bind( IReadOnlyList<PaintFeature> layers, PaintFeature active )
	{
		_layers = layers ?? Array.Empty<PaintFeature>();
		_active = active;
		Visible = layers is not null && active is not null;

		// Next frame, not now: Bind is usually reached from one of this panel's own controls (a
		// blend combo, an opacity box), and clearing the layout here would delete that control
		// inside its own callback.
		_rebuildPending = Visible;
	}

	private bool _rebuildPending;

	/// <summary>Once a frame: rebuild the rows if a Bind asked for it. The same frame-hook shape
	/// EffigyRigPanel uses to settle an edit after the field that raised it is done.</summary>
	[EditorEvent.Frame]
	public void SettleRebuild()
	{
		if ( !_rebuildPending || !IsValid )
			return;

		_rebuildPending = false;

		try
		{
			Rebuild();
		}
		catch ( Exception )
		{
			// The event can reach a panel whose widgets are already gone, as RigTimeline.Frame notes.
		}
	}

	private void Rebuild()
	{
		Layout.Clear( true );
		var rows = 0;

		Layout.Add( new Editor.Label( "Layers" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );
		rows++;

		// Top layer first: the one painted last sits over the rest, so it is listed on top.
		for ( var i = _layers.Count - 1; i >= 0; i-- )
		{
			Layout.Add( LayerRow( _layers[i], i ) );
			rows++;
		}

		var add = new Widget( this ) { Layout = Layout.Row(), FixedHeight = RowHeight };
		add.Layout.Spacing = 4;

		var addLayer = new Button( "New Layer", add )
		{
			ToolTip = "Add an empty paint layer above this one. Each layer has its own strokes, blend mode and opacity.",
		};
		addLayer.Clicked = () => Added?.Invoke( false );
		add.Layout.Add( addLayer );

		var addFill = new Button( "Fill Layer", add )
		{
			ToolTip = "Add a layer that covers the whole surface with one color or material value. Give it a mask to confine it — edge wear, dirt, a gradient.",
		};
		addFill.Clicked = () => Added?.Invoke( true );
		add.Layout.Add( addFill );

		Layout.Add( add );
		rows++;

		if ( _active is not null )
			rows += AddLayerSettings( _active );

		FixedHeight = rows * (RowHeight + Layout.Spacing) + 14f;
	}

	private Widget LayerRow( PaintFeature layer, int index )
	{
		var row = new Widget( this ) { Layout = Layout.Row(), FixedHeight = RowHeight };
		row.Layout.Spacing = 4;

		var active = ReferenceEquals( layer, _active );
		var name = new Button( (active ? "● " : "") + (layer.Name ?? "Paint"), row )
		{
			ToolTip = active ? "The layer you are painting." : "Paint on this layer instead.",
			MinimumWidth = 110f,
		};
		name.Clicked = () => Selected?.Invoke( layer );
		row.Layout.Add( name, 1 );

		var blend = new ComboBox( row )
		{
			MinimumWidth = 84f,
			ToolTip = "Blend Mode — how this layer mixes with the layers below. Normal covers; Multiply darkens; Screen lightens; Overlay adds contrast; Add brightens.",
		};

		var names = Enum.GetNames<PaintBlendMode>();

		for ( var m = 0; m < names.Length; m++ )
		{
			var mode = m;
			blend.AddItem( names[m], onSelected: () =>
			{
				if ( layer.Mode.Index == mode )
					return;

				layer.Mode.Index = mode;
				Edited?.Invoke( layer );
			}, selected: layer.Mode.Index == m );
		}

		row.Layout.Add( blend );

		var opacity = new EffigyNumericField( row, layer.Opacity.Value )
		{
			Label = "Layer opacity",
			Min = 0f,
			Max = 1f,
			FixedWidth = 56f,
			ToolTip = "Opacity — how much of this layer shows, from 0 (none) to 1 (all of it).",
			ValueEdited = v =>
			{
				layer.Opacity.Value = Math.Clamp( v, 0f, 1f );
				Edited?.Invoke( layer );
			},
		};

		row.Layout.Add( opacity );

		var up = new IconButton( "arrow_upward", () => Moved?.Invoke( layer, +1 ) )
		{
			ToolTip = index == _layers.Count - 1 ? "Already the top layer." : "Move this layer up, over the one above it.",
			Enabled = index < _layers.Count - 1,
		};
		row.Layout.Add( up );

		var down = new IconButton( "arrow_downward", () => Moved?.Invoke( layer, -1 ) )
		{
			ToolTip = index == 0 ? "Already the bottom layer." : "Move this layer down, under the one below it.",
			Enabled = index > 0,
		};
		row.Layout.Add( down );

		row.Layout.Add( new IconButton( "delete", () => Removed?.Invoke( layer ) )
		{
			ToolTip = "Delete this layer and its strokes. Ctrl+Z brings it back.",
		} );

		return row;
	}

	/// <summary>The open layer's fill and mask settings. Returns how many rows it added.</summary>
	private int AddLayerSettings( PaintFeature layer )
	{
		var rows = 0;

		Layout.Add( new Editor.Label( $"{layer.Name ?? "Paint"} settings" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );
		rows++;

		var fillRow = Row();
		fillRow.Layout.Add( Toggle( fillRow, layer.Fill, layer,
			"Fill the whole layer with the values below, under any strokes on it. A mask confines it." ) );

		var swatch = new PaintSwatch( fillRow )
		{
			Color = new Color( layer.FillColor.Value.x, layer.FillColor.Value.y, layer.FillColor.Value.z ),
			ToolTip = "The fill's color.",
		};
		swatch.Picked = c =>
		{
			layer.FillColor.Value = new Vec3( c.r, c.g, c.b );
			Edited?.Invoke( layer );
		};

		fillRow.Layout.Add( Toggle( fillRow, layer.FillUseColor, layer, "Let the fill write its color." ) );
		fillRow.Layout.Add( swatch );
		Layout.Add( fillRow );
		rows++;

		if ( layer.Fill.Value )
		{
			Layout.Add( ValueRow( layer, layer.FillUseRoughness, layer.FillRoughness,
				"Let the fill write roughness: 0 is mirror-smooth, 1 fully matte." ) );
			Layout.Add( ValueRow( layer, layer.FillUseMetalness, layer.FillMetalness,
				"Let the fill write metallic: 0 for paint, skin and cloth, 1 for bare metal." ) );
			Layout.Add( ValueRow( layer, layer.FillUseHeight, layer.FillHeight,
				"Let the fill write height: 0.5 is level, higher raises, lower sinks. Exported as a normal map." ) );
			rows += 3;
		}

		var maskRow = Row();
		maskRow.Layout.Add( new Editor.Label( "Mask" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		var generator = new ComboBox( maskRow )
		{
			MinimumWidth = 110f,
			ToolTip = "Mask Generator — where this layer shows. Edge Wear: worn convex edges. Dirt: crevices and shut-in places. "
				+ "Gradient: more toward the top. Material ID: only faces on one material slot. Paint on the Layer Mask channel to adjust it by hand.",
		};

		for ( var g = 0; g < PaintFeature.MaskOptions.Length; g++ )
		{
			var index = g;
			generator.AddItem( PaintFeature.MaskOptions[g], onSelected: () =>
			{
				if ( layer.MaskGenerator.Index == index )
					return;

				layer.MaskGenerator.Index = index;
				Edited?.Invoke( layer );
			}, selected: layer.MaskGenerator.Index == g );
		}

		maskRow.Layout.Add( generator );
		maskRow.Layout.Add( Toggle( maskRow, layer.InvertMask, layer, "Swap where the mask shows and hides the layer." ) );
		Layout.Add( maskRow );
		rows++;

		if ( layer.Generator != PaintMaskGenerator.None )
		{
			var shape = Row();
			shape.Layout.Add( Field( shape, layer, layer.MaskLevel, "Mask Level — where the mask switches from hidden to shown. Raise it to show less." ) );
			shape.Layout.Add( Field( shape, layer, layer.MaskSoftness, "Mask Softness — 0 is a hard edge, 1 a long fade." ) );

			if ( layer.Generator == PaintMaskGenerator.MaterialId )
			{
				shape.Layout.Add( Field( shape, layer, layer.MaskSlot, "The material slot whose faces this layer shows on." ) );
			}

			Layout.Add( shape );
			rows++;
		}

		return rows;
	}

	private Widget Row()
	{
		var row = new Widget( this ) { Layout = Layout.Row(), FixedHeight = RowHeight };
		row.Layout.Spacing = 6;
		return row;
	}

	private Widget ValueRow( PaintFeature layer, BoolParam use, FloatParam value, string tip )
	{
		var row = Row();
		row.Layout.Add( Toggle( row, use, layer, tip ) );
		row.Layout.Add( Field( row, layer, value, tip ) );
		return row;
	}

	private Checkbox Toggle( Widget parent, BoolParam param, PaintFeature layer, string tip )
	{
		var toggle = new Checkbox( param.Label ) { Value = param.Value, ToolTip = tip };

		toggle.Toggled = () =>
		{
			param.Value = toggle.Value;
			Edited?.Invoke( layer );
		};

		return toggle;
	}

	private Widget Field( Widget parent, PaintFeature layer, FloatParam param, string tip )
	{
		var box = new Widget( parent ) { Layout = Layout.Row() };
		box.Layout.Spacing = 4;
		box.Layout.Add( new Editor.Label( param.Label ) { Color = Theme.TextControl.WithAlpha( 0.6f ), ToolTip = tip } );
		box.Layout.Add( new EffigyNumericField( box, param.Value )
		{
			Label = param.Label,
			Min = param.Min,
			Max = param.Max,
			FixedWidth = 56f,
			ToolTip = tip,
			ValueEdited = v =>
			{
				param.Value = Math.Clamp( v, param.Min, param.Max );
				param.Expr = null;
				Edited?.Invoke( layer );
			},
		} );
		return box;
	}

	private Widget Field( Widget parent, PaintFeature layer, IntParam param, string tip )
	{
		var box = new Widget( parent ) { Layout = Layout.Row() };
		box.Layout.Spacing = 4;
		box.Layout.Add( new Editor.Label( "Slot" ) { Color = Theme.TextControl.WithAlpha( 0.6f ), ToolTip = tip } );
		box.Layout.Add( new EffigyNumericField( box, param.Value )
		{
			Label = param.Label,
			Min = param.Min,
			Max = param.Max,
			Integer = true,
			FixedWidth = 44f,
			ToolTip = tip,
			ValueEdited = v =>
			{
				param.Value = Math.Clamp( (int)MathF.Round( v ), param.Min, param.Max );
				Edited?.Invoke( layer );
			},
		} );
		return box;
	}
}
