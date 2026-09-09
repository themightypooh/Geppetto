using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// An RGBA canvas a paint stroke composites into, held CPU-side.
///
/// THIS IS THE LIVE PAINT TARGET NOW. Paint stores strokes and replays them onto this canvas, so
/// resolution is independent of mesh density - a bare box paints at the same texel resolution a
/// sculpted one does, which is the whole reason the vertex-colour path was replaced. The editor
/// uploads the canvas to a texture and renders it as an ordinary material; on export the same
/// canvas is baked to a PNG and authored into a .vmat.
///
/// DELIBERATELY THE SAME SHAPE AS BakedMap. A baked normal map is a `byte[] Rgb` plus a `bool[]`
/// mask, written straight out by PngWriter; this is a `byte[] Rgba`, four bytes a texel with the
/// alpha the normal map has no use for. Matching that shape means the PNG writer grows one colour
/// type instead of a second writer, and the editor's texture upload is a memcpy either way.
///
/// The one thing BakedMap does not have is what this exists to carry: a DIRTY RECT. The old paint
/// project stamped a dab into a CPU buffer and then shipped the entire 1024^2 image back to the GPU
/// on every mouse-move — four megabytes a frame — see docs/dev/PAINTING.md section 2 cause 5. The
/// rect below is the fix: the smallest bounds a dab touched, so the editor re-uploads only that.
/// </summary>
public sealed class PaintCanvas
{
	public readonly int Width;
	public readonly int Height;

	/// <summary>
	/// Four bytes per texel, row-major, row 0 at the TOP of the image. Straight (non-premultiplied)
	/// alpha, because that is what a PNG colour type 6 stores and what a texture upload consumes; a
	/// premultiplied buffer would round-trip through the writer wrong.
	/// </summary>
	public readonly byte[] Rgba;

	bool _dirty;
	int _minX, _minY, _maxX, _maxY;

	public PaintCanvas( int width, int height )
	{
		Width = width;
		Height = height;
		Rgba = new byte[width * height * 4];
	}

	/// <summary>A deep copy, for <see cref="PolyMesh.Clone"/>. The dirty flag is NOT copied — a clone
	/// is a snapshot, not a live canvas, and its upload state is its new owner's business.</summary>
	public PaintCanvas Clone()
	{
		var copy = new PaintCanvas( Width, Height );
		Array.Copy( Rgba, copy.Rgba, Rgba.Length );
		return copy;
	}

	/// <summary>
	/// Source-over composite of a solid colour at <paramref name="weight"/> coverage onto one texel.
	///
	/// THE COLOUR HAS NO ALPHA OF ITS OWN. A stroke is one RGBA colour carried once for the whole
	/// stroke, and per-texel coverage comes from the falloff curve; so the incoming source is the
	/// colour with alpha equal to the coverage. Two half-weight dabs of red are therefore a lighter
	/// red than one full-weight dab — the alpha rises both times, and the result is not the same.
	///
	/// weight &lt;= 0 is a no-op and does not touch the dirty rect: the caller rasterises a triangle's
	/// bounding box, so it hands back plenty of texels that take no paint at all. Out-of-bounds
	/// coordinates are ignored rather than thrown for the same reason — the caller already clamps its
	/// loop, and a second clamp here is one more place to get it wrong.
	/// </summary>
	public void Blend( int x, int y, byte r, byte g, byte b, float weight )
	{
		if ( weight <= 0f )
			return;

		if ( x < 0 || y < 0 || x >= Width || y >= Height )
			return;

		var i = (y * Width + x) * 4;

		var (or, og, ob, oa) = SourceOver( Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3], r, g, b, weight );

		Rgba[i] = or;
		Rgba[i + 1] = og;
		Rgba[i + 2] = ob;
		Rgba[i + 3] = oa;

		Mark( x, y );
	}

	/// <summary>
	/// Destination-out of <paramref name="weight"/> coverage from one texel: take that much of the
	/// paint away and leave the colour where it is.
	///
	/// STRAIGHT ALPHA IS WHY THE COLOUR DOES NOT MOVE. This buffer is non-premultiplied, so a texel's
	/// RGB is what the paint looks like and its alpha is how much of it is there. Erasing is entirely
	/// a statement about how much; fading RGB toward the base here would be premultiplying by hand,
	/// and <see cref="BakeOpaque(byte[], byte, byte, byte)"/> would then composite over the base a
	/// second time — the paint would come out half-erased twice, and a fully erased texel would still
	/// be tinted.
	///
	/// Same two guards as <see cref="Blend"/> and for the same reasons: weight &lt;= 0 does not touch
	/// the dirty rect, and out-of-bounds is ignored rather than thrown.
	/// </summary>
	public void Erase( int x, int y, float weight )
	{
		if ( weight <= 0f )
			return;

		if ( x < 0 || y < 0 || x >= Width || y >= Height )
			return;

		var i = (y * Width + x) * 4;

		Rgba[i + 3] = DestinationOut( Rgba[i + 3], weight );

		Mark( x, y );
	}

	/// <summary>
	/// Destination-out over straight alpha, in byte space — the one copy of the erase math, kept a
	/// static for the same reason <see cref="SourceOver"/> is one. The live session recomposes an
	/// erasing texel from the pre-stroke base while the replay applies the finished stroke to the
	/// canvas; two hand-written copies of this is how the live canvas and the rebuilt one would come
	/// to disagree about what an erase did.
	/// </summary>
	internal static byte DestinationOut( byte da, float weight )
	{
		var sa = Math.Min( weight, 1f );

		return (byte)MathF.Round( da * (1f - sa) );
	}

	/// <summary>
	/// Source-over over straight-alpha RGBA, in byte space — the one copy of the colour math.
	///
	/// WHY THIS IS A STATIC AND NOT PRIVATE TO <see cref="Blend"/>. A paint stroke is composited
	/// once rather than per dab (see PaintReplay), and the live session recomposes a texel from a
	/// pre-stroke base rather than from the canvas's current value. Both need the same source-over,
	/// and a second hand-written copy of this arithmetic is exactly the thing that drifts into
	/// "two half dabs are one full dab" (see PaintCanvasTests). One function, used everywhere.
	/// </summary>
	internal static (byte R, byte G, byte B, byte A) SourceOver(
		byte dr, byte dg, byte db, byte da, byte sr, byte sg, byte sb, float weight )
	{
		// Source-over, computed in premultiplied form and divided back out to straight alpha: the
		// incoming colour scaled by its own coverage, the colour already there by what of it the new
		// coverage does not cover. oa is never zero here — sa > 0 implies oa >= sa.
		var sa = Math.Min( weight, 1f );
		var daF = da / 255f;
		var oa = sa + daF * (1f - sa);

		var or = (sr / 255f) * sa + (dr / 255f) * daF * (1f - sa);
		var og = (sg / 255f) * sa + (dg / 255f) * daF * (1f - sa);
		var ob = (sb / 255f) * sa + (db / 255f) * daF * (1f - sa);

		or /= oa;
		og /= oa;
		ob /= oa;

		return (
			(byte)MathF.Round( or * 255f ),
			(byte)MathF.Round( og * 255f ),
			(byte)MathF.Round( ob * 255f ),
			(byte)MathF.Round( oa * 255f ) );
	}

	/// <summary>Write one texel straight, bypassing the blend. The live session uses this to lay a
	/// recomposed texel back onto the canvas; it is not a general-purpose API and skips every guard
	/// <see cref="Blend"/> makes, because the caller has already resolved them.</summary>
	internal void Write( int x, int y, byte r, byte g, byte b, byte a )
	{
		var i = (y * Width + x) * 4;

		Rgba[i] = r;
		Rgba[i + 1] = g;
		Rgba[i + 2] = b;
		Rgba[i + 3] = a;

		Mark( x, y );
	}

	/// <summary>
	/// Bake a rectangular region of the canvas over an opaque base colour, into a dense RGBA
	/// buffer <paramref name="dest"/> sized <c>w * h * 4</c>.
	///
	/// WHY THE CANVAS IS BAKED OPAQUE AT ALL. The canvas holds straight RGBA with the paint's
	/// coverage in its alpha, so an unpainted texel is fully transparent. A texture bound as an
	/// ordinary material's colour map has nothing sensible to show in a transparent texel - it
	/// reads as a hole, or black, depending on the shader - so the canvas is composited over a
	/// base before it reaches a texture. The base is the "surface the paint covers": white, the
	/// same trick the vertex-colour Replace mode used, so an unpainted region is a flat blank
	/// rather than a see-through one.
	///
	/// Rectangular because the editor only re-uploads the dirty rect while painting - baking the
	/// whole 1024² for a single dab is the same four-megabyte-per-mouse-move cost the dirty rect
	/// exists to avoid. The whole-canvas form below is the one-shot for the initial upload and for
	/// export.
	/// </summary>
	internal void BakeOpaque( byte[] dest, byte br, byte bg, byte bb, int x, int y, int w, int h )
	{
		var o = 0;

		for ( var yy = y; yy < y + h; yy++ )
		{
			for ( var xx = x; xx < x + w; xx++ )
			{
				var i = (yy * Width + xx) * 4;
				var coverage = Rgba[i + 3] / 255f;

				// A transparent texel needs no blend - source-over of nothing over the base IS the
				// base. Skirting the blend also keeps the byte math off the hot path for a canvas
				// that is mostly empty.
				if ( coverage <= 0f )
				{
					dest[o] = br;
					dest[o + 1] = bg;
					dest[o + 2] = bb;
					dest[o + 3] = 255;
				}
				else
				{
					var (r, g, b, _) = SourceOver( br, bg, bb, 255, Rgba[i], Rgba[i + 1], Rgba[i + 2], coverage );

					dest[o] = r;
					dest[o + 1] = g;
					dest[o + 2] = b;
					dest[o + 3] = 255;
				}

				o += 4;
			}
		}
	}

	/// <summary>The whole canvas baked over the base colour, for the initial upload and for export.</summary>
	internal void BakeOpaque( byte[] dest, byte br, byte bg, byte bb ) =>
		BakeOpaque( dest, br, bg, bb, 0, 0, Width, Height );

	/// <summary>Whether anything has touched the canvas since the last <see cref="ClearDirty"/>.</summary>
	public bool HasDirty => _dirty;

	/// <summary>The leftmost column touched, when <see cref="HasDirty"/> is true.</summary>
	public int MinX => _minX;

	/// <summary>The topmost row touched, when <see cref="HasDirty"/> is true.</summary>
	public int MinY => _minY;

	/// <summary>The rightmost column touched, when <see cref="HasDirty"/> is true.</summary>
	public int MaxX => _maxX;

	/// <summary>The bottommost row touched, when <see cref="HasDirty"/> is true.</summary>
	public int MaxY => _maxY;

	public void ClearDirty() => _dirty = false;

	/// <summary>Mark the whole canvas dirty without touching its pixels, so the next upload repaints
	/// everything. The live session calls this when the visible canvas is rebuilt wholesale from the
	/// committed one, where the pixels changed everywhere but were not written through <see cref="Blend"/>.</summary>
	internal void Invalidate()
	{
		_dirty = true;
		_minX = 0;
		_minY = 0;
		_maxX = Width - 1;
		_maxY = Height - 1;
	}

	/// <summary>Back to transparent, the whole canvas marked dirty so the next upload repaints it all.</summary>
	public void Clear()
	{
		Array.Clear( Rgba, 0, Rgba.Length );

		_dirty = true;
		_minX = 0;
		_minY = 0;
		_maxX = Width - 1;
		_maxY = Height - 1;
	}

	/// <summary>
	/// Bleed filled texels outward, so a shader filtering across an island's edge finds colour there
	/// instead of the transparent gutter. Same idea as <see cref="NormalBake.Dilate"/> — each pass
	/// averages the filled neighbours into the unfilled ones — but over RGBA, where "filled" is
	/// simply a non-zero alpha and the alpha itself is one of the channels being averaged.
	///
	/// WHAT <paramref name="protect"/> IS FOR, AND WHY IT IS NOT OPTIONAL ONCE ERASING EXISTS. This
	/// fills any transparent texel that has a filled neighbour, and it cannot tell the gutter outside
	/// an island from a hole somebody deliberately erased in the middle of one. Without the mask, an
	/// erased hole is bled back in from its own edges — four passes eat four texels off every side of
	/// it, so a small erase disappears entirely and a large one comes back with soft edges. That
	/// reads as "the eraser does not work", and it would only happen on REBUILD, because the live
	/// session never dilates. A texel flagged here is one the stroke log last spoke about with an
	/// erase, and it is left alone. It can still be a source for its neighbours when it holds paint:
	/// a half-erased texel is filled, and the gutter beside it should find that half.
	///
	/// Marks the whole canvas dirty: dilation spreads outward and could touch anywhere, so there is
	/// no smaller rect worth tracking.
	/// </summary>
	internal void Dilate( int passes, bool[] protect = null )
	{
		if ( passes <= 0 )
			return;

		var filled = new bool[Width * Height];

		for ( var i = 0; i < filled.Length; i++ )
			filled[i] = Rgba[i * 4 + 3] > 0;

		for ( var pass = 0; pass < passes; pass++ )
		{
			var added = new List<(int Index, int R, int G, int B, int A)>();

			for ( var y = 0; y < Height; y++ )
			{
				for ( var x = 0; x < Width; x++ )
				{
					var index = y * Width + x;

					if ( filled[index] )
						continue;

					// Deliberately empty, not gutter. Never filled, and never marked filled, so it
					// stays empty for every remaining pass too.
					if ( protect is not null && protect[index] )
						continue;

					int r = 0, g = 0, b = 0, a = 0, n = 0;

					for ( var dy = -1; dy <= 1; dy++ )
					{
						for ( var dx = -1; dx <= 1; dx++ )
						{
							var nx = x + dx;
							var ny = y + dy;

							if ( nx < 0 || ny < 0 || nx >= Width || ny >= Height )
								continue;

							var other = ny * Width + nx;

							if ( !filled[other] )
								continue;

							var o = other * 4;
							r += Rgba[o];
							g += Rgba[o + 1];
							b += Rgba[o + 2];
							a += Rgba[o + 3];
							n++;
						}
					}

					if ( n > 0 )
						added.Add( (index, r / n, g / n, b / n, a / n) );
				}
			}

			if ( added.Count == 0 )
				return;

			foreach ( var (index, r, g, b, a) in added )
			{
				var o = index * 4;
				Rgba[o] = (byte)r;
				Rgba[o + 1] = (byte)g;
				Rgba[o + 2] = (byte)b;
				Rgba[o + 3] = (byte)a;
				filled[index] = true;
			}
		}

		_dirty = true;
		_minX = 0;
		_minY = 0;
		_maxX = Width - 1;
		_maxY = Height - 1;
	}

	void Mark( int x, int y )
	{
		if ( !_dirty )
		{
			_dirty = true;
			_minX = _maxX = x;
			_minY = _maxY = y;
			return;
		}

		if ( x < _minX ) _minX = x;
		if ( x > _maxX ) _maxX = x;
		if ( y < _minY ) _minY = y;
		if ( y > _maxY ) _maxY = y;
	}
}
