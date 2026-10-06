using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Effigy.Render;

/// <summary>
/// How well the model's silhouette from a view matches a reference drawing of the same view.
///
/// BOTH SILHOUETTES ARE FITTED TO THEIR OWN BOUNDS FIRST. A reference is drawn at whatever size
/// the paper was; the model is whatever size it is. What can be compared is SHAPE — the outline
/// scaled to fill the same box — and that is what the overlap measures. Height is compared
/// separately as the box's own aspect, since a fitted comparison would hide "too short".
///
/// The report says where it differs, in words an agent can act on: which bands of height the
/// model is wider or narrower than the drawing, and by how much of the width. That is the thing
/// a picture of two overlaid outlines shows a person, written down.
/// </summary>
public static class ReferenceMatch
{
	public sealed class Result
	{
		/// <summary>Intersection over union of the two fitted silhouettes. 1 is a perfect match; 0.8 is
		/// recognisably the same thing; 0.5 is a different shape.</summary>
		public float Overlap;

		/// <summary>The reference's height ÷ width, and the model's. Equal when the proportions agree.</summary>
		public float ReferenceAspect, ModelAspect;

		/// <summary>Where the model sticks out past the drawing, and where it falls short, as fractions
		/// of the frame.</summary>
		public float Excess, Missing;

		public string Report;
		public Raster Diff;
	}

	/// <summary>
	/// The reference's silhouette, fitted to its own bounds into a square of <paramref name="size"/>.
	///
	/// THREE KINDS OF PICTURE, ONE ANSWER. A cut-out on transparency: the alpha is the answer. A
	/// photo or a render on a plain ground: the ground's colour is read off the border, and the
	/// subject is whatever is far from it in colour — which also reads a pencil drawing on paper,
	/// since paper is the border colour and graphite is far from it. A picture with no plain
	/// border — a drawing cropped tight — falls back to dark-versus-light. In every case only the
	/// largest connected blob is kept, so labels, a caption, a watermark or a second object do
	/// not stretch the frame, and an outline drawing is filled in.
	/// </summary>
	public static bool[] ReferenceSilhouette( int width, int height, int[] rgb, byte[] alpha, int size, out float aspect )
	{
		var mask = new bool[width * height];
		var transparent = alpha.Any( a => a < 250 );

		if ( transparent )
		{
			for ( var i = 0; i < mask.Length; i++ )
				mask[i] = alpha[i] >= 128;
		}
		else
		{
			// The ground: the commonest colour in the WHOLE image (quantised, so a gradient still has a
			// mode) — a backdrop is the biggest thing in a reference, and reading it off the border
			// alone fails the moment a crop touches the subject or a caption bar. The border ring then
			// says how plain the ground is: how much of the edge is far from that colour.
			var ring = Math.Max( 1, Math.Min( width, height ) / 40 );
			var counts = new Dictionary<int, int>();
			var borderPixels = new List<int>();

			for ( var y = 0; y < height; y++ )
				for ( var x = 0; x < width; x++ )
				{
					if ( x >= ring && x < width - ring && y >= ring && y < height - ring )
						continue;
					borderPixels.Add( rgb[y * width + x] );
				}

			foreach ( var p in rgb )
			{
				var q = ((p >> 19) << 10) | (((p >> 11) & 0x1F) << 5) | ((p >> 3) & 0x1F);
				counts[q] = counts.GetValueOrDefault( q ) + 1;
			}

			var mode = counts.OrderByDescending( kv => kv.Value ).First().Key;
			float r = ((mode >> 10) & 0x1F) * 255f / 31f, g = ((mode >> 5) & 0x1F) * 255f / 31f, b = (mode & 0x1F) * 255f / 31f;
			var spread = borderPixels.Count( p => Distance( p, r, g, b ) > 0.15f ) / (float)borderPixels.Count;

			var distance = new float[width * height];

			if ( spread < 0.35f )
			{
				// A plain ground: the subject is what is far from it in colour — or in brightness, which
				// is weighted up so a black visor on a slate backdrop still counts as subject.
				// Per row, the ground is what the row's own left and right edges show, when they agree —
				// a backdrop is rarely flat, and a top-to-bottom gradient crossing one fixed cut reads as
				// a band of subject across the whole picture. Rows whose edges disagree (the subject
				// touches an edge) fall back to the overall ground.
				for ( var y = 0; y < height; y++ )
				{
					float lr = 0, lg = 0, lb = 0, rr = 0, rg = 0, rb = 0;

					for ( var x = 0; x < ring; x++ )
					{
						var l = rgb[y * width + x]; var rt = rgb[y * width + width - 1 - x];
						lr += (l >> 16) & 0xFF; lg += (l >> 8) & 0xFF; lb += l & 0xFF;
						rr += (rt >> 16) & 0xFF; rg += (rt >> 8) & 0xFF; rb += rt & 0xFF;
					}

					lr /= ring; lg /= ring; lb /= ring; rr /= ring; rg /= ring; rb /= ring;

					var edgesAgree = MathF.Sqrt( (lr - rr) * (lr - rr) + (lg - rg) * (lg - rg) + (lb - rb) * (lb - rb) ) / 255f / 1.732f < 0.08f;
					var leftNearGround = Distance( Raster.Rgb( (int)lr, (int)lg, (int)lb ), r, g, b ) < 0.25f;
					var gr = edgesAgree && leftNearGround ? (lr + rr) * 0.5f : r;
					var gg = edgesAgree && leftNearGround ? (lg + rg) * 0.5f : g;
					var gb = edgesAgree && leftNearGround ? (lb + rb) * 0.5f : b;
					var groundLuminance = (gr * 0.299f + gg * 0.587f + gb * 0.114f) / 255f;

					for ( var x = 0; x < width; x++ )
					{
						var i = y * width + x;
						distance[i] = MathF.Max( Distance( rgb[i], gr, gg, gb ), MathF.Abs( Luminance( rgb[i] ) - groundLuminance ) * 1.6f );
					}
				}
			}
			else
			{
				// No plain ground to read: dark versus light, the darker side taken as ink, then
				// flipped if the ink runs to the border more than the paper does.
				for ( var i = 0; i < rgb.Length; i++ )
					distance[i] = 1f - Luminance( rgb[i] );
			}

			// With a plain ground the question is only "is this the ground?", so the cut is a fixed
			// small distance — Otsu would split a white head from a black visor and keep one of them.
			// Without a plain ground there are two populations, ink and paper, and Otsu finds them.
			var cut = spread < 0.35f ? 0.12f : Otsu( distance );

			for ( var i = 0; i < mask.Length; i++ )
				mask[i] = distance[i] > cut;

			LastRead = $"ground rgb({r:0},{g:0},{b:0}), {spread * 100f:0}% of the border far from it, cut {cut:0.00}, {mask.Count( m => m )} of {mask.Length} px subject before cleaning";

			if ( spread >= 0.35f )
			{
				var borderInk = 0; var border = 0;
				for ( var x = 0; x < width; x++ ) { border += 2; if ( mask[x] ) borderInk++; if ( mask[(height - 1) * width + x] ) borderInk++; }
				for ( var y = 0; y < height; y++ ) { border += 2; if ( mask[y * width] ) borderInk++; if ( mask[y * width + width - 1] ) borderInk++; }

				if ( borderInk * 2 > border )
					for ( var i = 0; i < mask.Length; i++ )
						mask[i] = !mask[i];
			}
		}

		// Thin lines — a grid, hatching, a caption's strokes — are not the subject, however far they
		// are from the ground. Eroding a pixel removes anything one pixel wide; the largest blob is
		// then a solid shape; dilating puts its edge back.
		// EFFIGY_DEBUG_MASK=path writes the raw classification, for a match that read wrong.
		if ( Environment.GetEnvironmentVariable( "EFFIGY_DEBUG_MASK" ) is { Length: > 0 } dump )
		{
			var dbg = new Raster( width, height );
			for ( var i = 0; i < mask.Length; i++ ) if ( mask[i] ) dbg.Pixels[i] = 0xffffff;
			dbg.SavePng( dump );
		}

		// First a closing, so hairlines drawn OVER the subject — a wireframe, pencil construction
		// lines — do not cut it into confetti before the blob is picked.
		Dilate( mask, width, height );
		Erode( mask, width, height );

		Erode( mask, width, height );
		KeepLargestBlob( mask, width, height );
		Dilate( mask, width, height );
		FillInside( mask, width, height );
		LastRead += $", {mask.Count( m => m )} after cleaning";

		return Fit( mask, width, height, size, out aspect );
	}

	/// <summary>How the last reference was read — the ground colour, how plain the border was, the
	/// cut — for a match that came out wrong.</summary>
	public static string LastRead = "";

	static float Luminance( int p ) =>
		(((p >> 16) & 0xFF) * 0.299f + ((p >> 8) & 0xFF) * 0.587f + (p & 0xFF) * 0.114f) / 255f;

	/// <summary>Colour distance from a mean, 0..1, weighted so a hue change counts as much as a
	/// brightness change — a red toy on a grey table is far even when they are equally bright.</summary>
	static float Distance( int p, float r, float g, float b )
	{
		var dr = (((p >> 16) & 0xFF) - r) / 255f;
		var dg = (((p >> 8) & 0xFF) - g) / 255f;
		var db = ((p & 0xFF) - b) / 255f;
		return MathF.Sqrt( dr * dr + dg * dg + db * db ) / 1.732f;
	}

	static void Erode( bool[] mask, int width, int height )
	{
		var src = (bool[])mask.Clone();
		for ( var y = 0; y < height; y++ )
			for ( var x = 0; x < width; x++ )
			{
				var i = y * width + x;
				mask[i] = src[i] && x > 0 && src[i - 1] && x + 1 < width && src[i + 1] && y > 0 && src[i - width] && y + 1 < height && src[i + width];
			}
	}

	static void Dilate( bool[] mask, int width, int height )
	{
		var src = (bool[])mask.Clone();
		for ( var y = 0; y < height; y++ )
			for ( var x = 0; x < width; x++ )
			{
				var i = y * width + x;
				mask[i] = src[i] || (x > 0 && src[i - 1]) || (x + 1 < width && src[i + 1]) || (y > 0 && src[i - width]) || (y + 1 < height && src[i + width]);
			}
	}

	/// <summary>Keep only the largest 4-connected true region.</summary>
	static void KeepLargestBlob( bool[] mask, int width, int height )
	{
		var label = new int[width * height];
		var sizes = new List<int> { 0 };
		var stack = new Stack<int>();

		for ( var start = 0; start < mask.Length; start++ )
		{
			if ( !mask[start] || label[start] != 0 )
				continue;

			var id = sizes.Count;
			sizes.Add( 0 );
			label[start] = id;
			stack.Push( start );

			while ( stack.Count > 0 )
			{
				var i = stack.Pop();
				sizes[id]++;
				var x = i % width; var y = i / width;

				void Try( int j ) { if ( mask[j] && label[j] == 0 ) { label[j] = id; stack.Push( j ); } }
				if ( x > 0 ) Try( i - 1 );
				if ( x + 1 < width ) Try( i + 1 );
				if ( y > 0 ) Try( i - width );
				if ( y + 1 < height ) Try( i + width );
			}
		}

		if ( sizes.Count <= 2 )
			return;

		var best = 1;
		for ( var id = 2; id < sizes.Count; id++ )
			if ( sizes[id] > sizes[best] ) best = id;

		for ( var i = 0; i < mask.Length; i++ )
			mask[i] = label[i] == best;
	}

	/// <summary>Fill the interior of an outline: anything not reachable from the border is inside.</summary>
	static void FillInside( bool[] mask, int width, int height )
	{
		var outside = new bool[width * height];
		var stack = new Stack<int>();

		void Push( int y, int x )
		{
			var i = y * width + x;
			if ( !mask[i] && !outside[i] ) { outside[i] = true; stack.Push( i ); }
		}

		for ( var x = 0; x < width; x++ ) { Push( 0, x ); Push( height - 1, x ); }
		for ( var y = 0; y < height; y++ ) { Push( y, 0 ); Push( y, width - 1 ); }

		while ( stack.Count > 0 )
		{
			var i = stack.Pop();
			var x = i % width; var y = i / width;
			if ( x > 0 ) Push( y, x - 1 );
			if ( x + 1 < width ) Push( y, x + 1 );
			if ( y > 0 ) Push( y - 1, x );
			if ( y + 1 < height ) Push( y + 1, x );
		}

		for ( var i = 0; i < mask.Length; i++ )
			mask[i] = !outside[i];
	}

	/// <summary>Otsu's threshold: the cut that best separates the two brightness populations, so a
	/// grey model on a dark ground splits from the ground and not from a few white labels.</summary>
	static float Otsu( float[] values )
	{
		const int bins = 64;
		var histogram = new int[bins];

		foreach ( var v in values )
			histogram[Math.Clamp( (int)(v * (bins - 1)), 0, bins - 1 )]++;

		var total = values.Length;
		var sum = 0f;
		for ( var i = 0; i < bins; i++ ) sum += i * histogram[i];

		var sumB = 0f; var wB = 0; var best = 0f; var threshold = bins / 2;

		for ( var i = 0; i < bins; i++ )
		{
			wB += histogram[i];
			if ( wB == 0 ) continue;
			var wF = total - wB;
			if ( wF == 0 ) break;
			sumB += i * histogram[i];
			var mB = sumB / wB;
			var mF = (sum - sumB) / wF;
			var between = wB * (float)wF * (mB - mF) * (mB - mF);
			if ( between > best ) { best = between; threshold = i; }
		}

		return (threshold + 0.5f) / (bins - 1);
	}

	/// <summary>Crop a mask to its bounds and scale it into a square, keeping its aspect (centred).</summary>
	public static bool[] Fit( bool[] mask, int width, int height, int size, out float aspect )
	{
		int minX = width, minY = height, maxX = -1, maxY = -1;

		for ( var y = 0; y < height; y++ )
			for ( var x = 0; x < width; x++ )
				if ( mask[y * width + x] )
				{
					minX = Math.Min( minX, x ); maxX = Math.Max( maxX, x );
					minY = Math.Min( minY, y ); maxY = Math.Max( maxY, y );
				}

		var result = new bool[size * size];

		if ( maxX < 0 )
		{
			aspect = 1f;
			return result;
		}

		var w = maxX - minX + 1;
		var h = maxY - minY + 1;
		aspect = h / (float)w;

		var scale = MathF.Max( w, h ) / (float)size;
		var ox = (size - w / scale) * 0.5f;
		var oy = (size - h / scale) * 0.5f;

		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
				var sx = (int)((x - ox) * scale) + minX;
				var sy = (int)((y - oy) * scale) + minY;

				if ( sx < minX || sy < minY || sx > maxX || sy > maxY )
					continue;

				result[y * size + x] = mask[sy * width + sx];
			}

		return result;
	}

	public static Result Compare( IReadOnlyList<Body> bodies, RenderView view, bool[] reference, float referenceAspect, int size )
	{
		var raw = ModelRender.Silhouette( bodies, view, size );
		var model = Fit( raw, size, size, size, out var modelAspect );

		var both = 0; var either = 0; var excess = 0; var missing = 0;

		for ( var i = 0; i < model.Length; i++ )
		{
			if ( model[i] && reference[i] ) both++;
			if ( model[i] || reference[i] ) either++;
			if ( model[i] && !reference[i] ) excess++;
			if ( !model[i] && reference[i] ) missing++;
		}

		var result = new Result
		{
			Overlap = either == 0 ? 1f : both / (float)either,
			ReferenceAspect = referenceAspect,
			ModelAspect = modelAspect,
			Excess = excess / (float)(size * size),
			Missing = missing / (float)(size * size),
		};

		// The diff picture: reference in blue, model in orange, the overlap where both agree.
		var diff = new Raster( size, size, 0x121418 );

		for ( var i = 0; i < model.Length; i++ )
		{
			if ( model[i] && reference[i] ) diff.Pixels[i] = 0x6f7d8c;
			else if ( model[i] ) diff.Pixels[i] = 0xf0a13a;
			else if ( reference[i] ) diff.Pixels[i] = 0x5b93ff;
		}

		diff.Text( 6, 6, $"overlap {result.Overlap * 100f:0}%", 0xe7e9ee, 2 );
		diff.Text( 6, 20, "orange: model only  blue: drawing only", 0x9aa2ae, 1 );
		result.Diff = diff;

		result.Report = Words( model, reference, size, result, view );
		return result;
	}

	/// <summary>The differences in bands of height, in words.</summary>
	static string Words( bool[] model, bool[] reference, int size, Result r, RenderView view )
	{
		var sb = new StringBuilder();
		sb.AppendLine( $"{view.Name}: silhouette overlap {r.Overlap * 100f:0}%. Drawing is {r.ReferenceAspect:0.00} tall per unit wide, model is {r.ModelAspect:0.00}"
			+ (MathF.Abs( r.ReferenceAspect - r.ModelAspect ) / MathF.Max( r.ReferenceAspect, 1e-3f ) > 0.08f
				? (r.ModelAspect < r.ReferenceAspect ? " — the model is too wide for its height (or too short)." : " — the model is too tall for its width (or too narrow).")
				: " — proportions agree.") );

		const int bands = 8;
		var names = new[] { "top", "upper", "upper-middle", "middle", "middle", "lower-middle", "lower", "bottom" };

		for ( var b = 0; b < bands; b++ )
		{
			var y0 = b * size / bands;
			var y1 = (b + 1) * size / bands;

			float modelWidth = 0, refWidth = 0, modelLeft = 0, refLeft = 0, modelRight = 0, refRight = 0;
			var rows = 0;

			for ( var y = y0; y < y1; y++ )
			{
				int mMin = size, mMax = -1, rMin = size, rMax = -1;

				for ( var x = 0; x < size; x++ )
				{
					if ( model[y * size + x] ) { mMin = Math.Min( mMin, x ); mMax = Math.Max( mMax, x ); }
					if ( reference[y * size + x] ) { rMin = Math.Min( rMin, x ); rMax = Math.Max( rMax, x ); }
				}

				if ( mMax < 0 && rMax < 0 )
					continue;

				rows++;
				if ( mMax >= 0 ) { modelWidth += mMax - mMin + 1; modelLeft += mMin; modelRight += mMax; }
				if ( rMax >= 0 ) { refWidth += rMax - rMin + 1; refLeft += rMin; refRight += rMax; }
			}

			if ( rows == 0 )
				continue;

			modelWidth /= rows; refWidth /= rows;
			var delta = (modelWidth - refWidth) / size;

			if ( MathF.Abs( delta ) < 0.04f )
				continue;

			var band = names[b];
			var pct = MathF.Abs( delta ) * 100f;
			sb.AppendLine( delta > 0
				? $"- {band}: model is wider than the drawing by {pct:0}% of the frame."
				: $"- {band}: model is narrower than the drawing by {pct:0}% of the frame." );
		}

		if ( r.Excess > 0.02f && r.Missing < 0.01f )
			sb.AppendLine( "Mostly the model sticks out past the drawing: trim rather than add." );
		else if ( r.Missing > 0.02f && r.Excess < 0.01f )
			sb.AppendLine( "Mostly the drawing reaches where the model does not: add rather than trim." );

		return sb.ToString().TrimEnd();
	}
}
