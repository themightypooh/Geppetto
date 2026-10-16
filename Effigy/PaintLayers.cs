using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One paint layer's own content, before it meets the layers below: a straight-alpha canvas per
/// surface channel it touches (null where it touches nothing), and a per-texel mask.
///
/// This is what a <see cref="PaintFeature"/> builds from its strokes and its fill. It is not what
/// the body wears — <see cref="PaintLayers.CompositeSet"/> lays it over the paint already there.
/// </summary>
public sealed class PaintLayerMaps
{
	public PaintCanvas Color, Roughness, Metalness, Height;

	/// <summary>The painted layer mask (black hides, white shows; coverage in alpha), or null when
	/// nobody painted on the mask.</summary>
	public PaintCanvas MaskPaint;

	/// <summary>Texels across (layers are square, like the canvas they replay onto).</summary>
	public int Size;

	public PaintCanvas Get( PaintChannel channel ) => channel switch
	{
		PaintChannel.Color => Color,
		PaintChannel.Roughness => Roughness,
		PaintChannel.Metalness => Metalness,
		PaintChannel.Height => Height,
		PaintChannel.Mask => MaskPaint,
		_ => null,
	};

	public void Set( PaintChannel channel, PaintCanvas canvas )
	{
		switch ( channel )
		{
			case PaintChannel.Color: Color = canvas; break;
			case PaintChannel.Roughness: Roughness = canvas; break;
			case PaintChannel.Metalness: Metalness = canvas; break;
			case PaintChannel.Height: Height = canvas; break;
			case PaintChannel.Mask: MaskPaint = canvas; break;
		}
	}

	/// <summary>Whether any surface channel carries content. A layer with only a mask adds nothing.</summary>
	public bool HasSurface => Color is not null || Roughness is not null || Metalness is not null || Height is not null;
}

/// <summary>
/// Layer compositing for paint: how one layer lands on the paint below it.
///
/// EVERY CANVAS HERE IS STRAIGHT ALPHA, like <see cref="PaintCanvas"/> itself, so a blend mode has to
/// say what happens where the backdrop is partly transparent. The answer is the W3C one (Compositing
/// and Blending Level 1, §5.8 "general formula for compositing and blending"), which every layer-based
/// painter implements:
///
///   αo = αs + αb·(1 − αs)
///   Co = [ αs·(1 − αb)·Cs + αs·αb·B(Cb, Cs) + (1 − αs)·αb·Cb ] / αo
///
/// Where nothing is below (αb = 0) the layer shows as itself whatever its mode — a Multiply layer on a
/// bare surface is its own colour, not black — and where the backdrop is opaque the blend function B
/// decides. Normal is B = Cs, which reduces exactly to <see cref="PaintCanvas.SourceOver"/>, and Normal
/// is routed through that very function so a one-layer document composites bit-for-bit as before.
///
/// αs is the layer's own coverage times its opacity times its mask, per texel: opacity and mask are
/// one more factor on how much of the layer arrives, which is all either of them means.
/// </summary>
public static class PaintLayers
{
	/// <summary>The blend function B(Cb, Cs) for one channel, everything in 0..1.</summary>
	public static float Blend( float cb, float cs, PaintBlendMode mode ) => mode switch
	{
		PaintBlendMode.Multiply => cb * cs,
		PaintBlendMode.Screen => cb + cs - cb * cs,
		// Overlay is HardLight with the layers swapped: the BACKDROP decides multiply or screen.
		PaintBlendMode.Overlay => cb <= 0.5f ? cs * 2f * cb : Screen( cs, 2f * cb - 1f ),
		PaintBlendMode.Add => MathF.Min( 1f, cb + cs ),
		_ => cs,
	};

	static float Screen( float a, float b ) => a + b - a * b;

	/// <summary>
	/// One texel of a layer over one texel of backdrop, straight RGBA in bytes, with the layer's
	/// effective alpha <paramref name="sa"/> (its coverage × opacity × mask) given as a float.
	/// </summary>
	public static (byte R, byte G, byte B, byte A) Over(
		byte dr, byte dg, byte db, byte da, byte sr, byte sg, byte sb, float sa, PaintBlendMode mode )
	{
		if ( sa <= 0f )
			return (dr, dg, db, da);

		if ( mode == PaintBlendMode.Normal )
			return PaintCanvas.SourceOver( dr, dg, db, da, sr, sg, sb, sa );

		sa = MathF.Min( sa, 1f );
		var ab = da / 255f;
		var ao = sa + ab * (1f - sa);

		float Channel( byte b, byte s )
		{
			var cb = b / 255f;
			var cs = s / 255f;
			var premul = sa * (1f - ab) * cs + sa * ab * Blend( cb, cs, mode ) + (1f - sa) * ab * cb;
			return premul / ao;
		}

		return (ToByte( Channel( dr, sr ) ), ToByte( Channel( dg, sg ) ), ToByte( Channel( db, sb ) ), ToByte( ao ));
	}

	static byte ToByte( float v ) => (byte)Math.Clamp( MathF.Round( v * 255f ), 0f, 255f );

	/// <summary>
	/// <paramref name="layer"/> over <paramref name="below"/>, as a NEW canvas. Neither input is
	/// touched: the backdrop is the layer below's cached result, shared with the rebuild that made it,
	/// and writing into it would composite this layer into that one the next time it is reused.
	///
	/// A null layer hands the backdrop back unchanged (the same object — nothing was composited). A
	/// null backdrop is a transparent one. When the two differ in size the backdrop is resampled to
	/// the layer's, so two layers painted at different resolutions still stack.
	/// </summary>
	public static PaintCanvas Composite( PaintCanvas below, PaintCanvas layer, PaintBlendMode mode,
		float opacity, float[] mask )
	{
		if ( layer is null )
			return below;

		var w = layer.Width;
		var h = layer.Height;
		var result = below is null ? new PaintCanvas( w, h ) : Resample( below, w, h );
		var dst = result.Rgba;
		var src = layer.Rgba;
		opacity = Math.Clamp( opacity, 0f, 1f );

		for ( var i = 0; i < w * h; i++ )
		{
			var o = i * 4;
			var sa = src[o + 3] / 255f * opacity * (mask is null ? 1f : mask[i]);

			if ( sa <= 0f )
				continue;

			var (r, g, b, a) = Over( dst[o], dst[o + 1], dst[o + 2], dst[o + 3], src[o], src[o + 1], src[o + 2], sa, mode );
			dst[o] = r;
			dst[o + 1] = g;
			dst[o + 2] = b;
			dst[o + 3] = a;
		}

		result.Invalidate();
		return result;
	}

	/// <summary>
	/// A whole layer over a body's paint: every surface channel composited on its own, the result's
	/// companions set from the per-channel results. A channel the layer does not touch is carried
	/// through as it was below. Returns the backdrop itself when the layer has no surface content.
	/// </summary>
	public static PaintCanvas CompositeSet( PaintCanvas below, PaintLayerMaps layer, PaintBlendMode mode,
		float opacity, float[] mask )
	{
		if ( layer is null || !layer.HasSurface )
			return below;

		// The colour canvas carries the companions, so it always exists on the result — an
		// all-transparent colour where only roughness was painted reads as "no colour paint here".
		var colour = layer.Color is not null
			? Composite( below, layer.Color, mode, opacity, mask )
			: below is null
				? new PaintCanvas( layer.Size, layer.Size )
				: Strip( below );

		colour.RoughnessMap = Composite( below?.RoughnessMap, layer.Roughness, mode, opacity, mask );
		colour.MetalnessMap = Composite( below?.MetalnessMap, layer.Metalness, mode, opacity, mask );
		colour.HeightMap = Composite( below?.HeightMap, layer.Height, mode, opacity, mask );

		return colour;
	}

	/// <summary>A copy of a colour canvas without its companions, so the result's companions can be
	/// set without reaching back into the backdrop's.</summary>
	static PaintCanvas Strip( PaintCanvas canvas )
	{
		var copy = new PaintCanvas( canvas.Width, canvas.Height );
		Array.Copy( canvas.Rgba, copy.Rgba, canvas.Rgba.Length );
		return copy;
	}

	/// <summary>
	/// A canvas at another size, bilinear in premultiplied space (so a half-covered texel does not drag
	/// its transparent neighbour's black into the colour). Same size hands back a plain copy.
	/// </summary>
	public static PaintCanvas Resample( PaintCanvas source, int width, int height )
	{
		var result = new PaintCanvas( width, height );

		if ( source.Width == width && source.Height == height )
		{
			Array.Copy( source.Rgba, result.Rgba, source.Rgba.Length );
			return result;
		}

		var s = source.Rgba;
		var d = result.Rgba;

		for ( var y = 0; y < height; y++ )
		{
			var fy = (y + 0.5f) * source.Height / height - 0.5f;
			var y0 = Math.Clamp( (int)MathF.Floor( fy ), 0, source.Height - 1 );
			var y1 = Math.Min( y0 + 1, source.Height - 1 );
			var ty = Math.Clamp( fy - y0, 0f, 1f );

			for ( var x = 0; x < width; x++ )
			{
				var fx = (x + 0.5f) * source.Width / width - 0.5f;
				var x0 = Math.Clamp( (int)MathF.Floor( fx ), 0, source.Width - 1 );
				var x1 = Math.Min( x0 + 1, source.Width - 1 );
				var tx = Math.Clamp( fx - x0, 0f, 1f );

				float r = 0, g = 0, b = 0, a = 0;

				void Tap( int xx, int yy, float wgt )
				{
					var o = (yy * source.Width + xx) * 4;
					var al = s[o + 3] / 255f * wgt;
					r += s[o] * al;
					g += s[o + 1] * al;
					b += s[o + 2] * al;
					a += al;
				}

				Tap( x0, y0, (1 - tx) * (1 - ty) );
				Tap( x1, y0, tx * (1 - ty) );
				Tap( x0, y1, (1 - tx) * ty );
				Tap( x1, y1, tx * ty );

				var od = (y * width + x) * 4;

				if ( a > 0f )
				{
					d[od] = (byte)Math.Clamp( MathF.Round( r / a ), 0f, 255f );
					d[od + 1] = (byte)Math.Clamp( MathF.Round( g / a ), 0f, 255f );
					d[od + 2] = (byte)Math.Clamp( MathF.Round( b / a ), 0f, 255f );
					d[od + 3] = ToByte( a );
				}
			}
		}

		return result;
	}

	/// <summary>A float mask at another size, bilinear. Null stays null.</summary>
	public static float[] Resample( float[] mask, int sw, int sh, int width, int height )
	{
		if ( mask is null || (sw == width && sh == height) )
			return mask;

		var result = new float[width * height];

		for ( var y = 0; y < height; y++ )
		{
			var fy = (y + 0.5f) * sh / height - 0.5f;
			var y0 = Math.Clamp( (int)MathF.Floor( fy ), 0, sh - 1 );
			var y1 = Math.Min( y0 + 1, sh - 1 );
			var ty = Math.Clamp( fy - y0, 0f, 1f );

			for ( var x = 0; x < width; x++ )
			{
				var fx = (x + 0.5f) * sw / width - 0.5f;
				var x0 = Math.Clamp( (int)MathF.Floor( fx ), 0, sw - 1 );
				var x1 = Math.Min( x0 + 1, sw - 1 );
				var tx = Math.Clamp( fx - x0, 0f, 1f );

				result[y * width + x] =
					mask[y0 * sw + x0] * (1 - tx) * (1 - ty) + mask[y0 * sw + x1] * tx * (1 - ty)
					+ mask[y1 * sw + x0] * (1 - tx) * ty + mask[y1 * sw + x1] * tx * ty;
			}
		}

		return result;
	}

	/// <summary>
	/// Replay a layer's stroke log into one canvas per channel. Strokes on different channels never
	/// meet, so each channel replays only its own strokes, in log order, through the same
	/// <see cref="PaintReplay"/> the single-colour path always used. A channel nobody painted stays null.
	/// </summary>
	public static PaintLayerMaps Replay( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution )
	{
		var maps = new PaintLayerMaps { Size = resolution };

		if ( strokes is not { Count: > 0 } )
			return maps;

		var byChannel = new Dictionary<PaintChannel, List<PaintStroke>>();

		foreach ( var stroke in strokes )
		{
			if ( !byChannel.TryGetValue( stroke.Channel, out var list ) )
				byChannel[stroke.Channel] = list = new List<PaintStroke>();

			list.Add( stroke );
		}

		var bvh = MeshBVH.Build( mesh );

		foreach ( var (channel, list) in byChannel )
			maps.Set( channel, PaintReplay.Replay( mesh, list, resolution, bvh ) );

		return maps;
	}

	/// <summary>A canvas fully covered with one straight colour — a fill layer's content for one channel.</summary>
	public static PaintCanvas Solid( int width, int height, byte r, byte g, byte b )
	{
		var canvas = new PaintCanvas( width, height );
		var d = canvas.Rgba;

		for ( var i = 0; i < d.Length; i += 4 )
		{
			d[i] = r;
			d[i + 1] = g;
			d[i + 2] = b;
			d[i + 3] = 255;
		}

		return canvas;
	}

	/// <summary>
	/// The layer's final mask: the generator's mask (or white when there is none), with the painted
	/// mask laid over it — a painted texel pulls the mask toward its own grey by its coverage, so black
	/// paint hides and white paint shows, and erasing the mask paint lets the generator back through.
	/// Inverted last, when asked. Null means "show everywhere" (no generator, no paint, no invert).
	/// </summary>
	public static float[] FinalMask( float[] generator, PaintCanvas painted, bool invert, int width, int height )
	{
		if ( generator is null && painted is null && !invert )
			return null;

		var n = width * height;
		var mask = new float[n];

		var p = painted is not null && (painted.Width != width || painted.Height != height)
			? Resample( painted, width, height )
			: painted;

		for ( var i = 0; i < n; i++ )
		{
			var m = generator is null ? 1f : generator[i];

			if ( p is not null )
			{
				var a = p.Rgba[i * 4 + 3] / 255f;
				m = m * (1f - a) + p.Rgba[i * 4] / 255f * a;
			}

			mask[i] = invert ? 1f - m : m;
		}

		return mask;
	}

	/// <summary>
	/// Shape a raw generator value into a mask: 0 below <paramref name="level"/>, 1 above it, with a
	/// smooth ramp <paramref name="softness"/> wide centred on the level. Softness 0 is a hard edge.
	/// Done in place and returned.
	/// </summary>
	public static float[] Shape( float[] values, float level, float softness )
	{
		if ( values is null )
			return null;

		level = Math.Clamp( level, 0f, 1f );
		var half = Math.Clamp( softness, 0f, 1f ) * 0.5f;

		for ( var i = 0; i < values.Length; i++ )
		{
			var v = values[i];

			if ( half < 1e-4f )
			{
				values[i] = v >= level ? 1f : 0f;
				continue;
			}

			var t = Math.Clamp( (v - (level - half)) / (2f * half), 0f, 1f );
			values[i] = t * t * (3f - 2f * t);
		}

		return values;
	}

	/// <summary>
	/// One channel of the body's paint as an opaque buffer over that channel's base value — the image
	/// that exports for it. Colour bakes over <paramref name="colourBase"/>; a scalar channel over its
	/// own base (<see cref="PaintChannels.Base"/>). Null when the channel was never painted.
	/// </summary>
	public static byte[] Opaque( PaintCanvas paint, PaintChannel channel )
	{
		var canvas = paint?.Channel( channel );

		if ( canvas is null )
			return null;

		var b = channel == PaintChannel.Color
			? PaintMaterial.DefaultBaseR
			: ToByte( PaintChannels.Base( channel ) );

		var opaque = new byte[canvas.Width * canvas.Height * 4];

		if ( channel == PaintChannel.Color )
			canvas.BakeOpaque( opaque, PaintMaterial.DefaultBaseR, PaintMaterial.DefaultBaseG, PaintMaterial.DefaultBaseB );
		else
			canvas.BakeOpaque( opaque, b, b, b );

		return opaque;
	}
}
