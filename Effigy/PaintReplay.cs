using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The texel bounds a dab touched, so the live session can recompose and re-upload only what a dab
/// changed rather than the whole canvas. A value type with no allocation, because a stroke fires
/// thousands of dabs and a class here would be thousands of objects a second.
/// </summary>
public readonly struct TexelBounds
{
	public readonly bool Any;
	public readonly int MinX, MinY, MaxX, MaxY;

	public TexelBounds( int minX, int minY, int maxX, int maxY )
	{
		Any = true;
		MinX = minX;
		MinY = minY;
		MaxX = maxX;
		MaxY = maxY;
	}

	public TexelBounds With( int x, int y ) => Any
		? new TexelBounds( Math.Min( MinX, x ), Math.Min( MinY, y ), Math.Max( MaxX, x ), Math.Max( MaxY, y ) )
		: new TexelBounds( x, y, x, y );
}

/// <summary>
/// Replays paint strokes onto a canvas — the derived-artifact half of the storage decision in
/// docs/dev/PAINTING.md §4.
///
/// Paint is stored as strokes in object space, not as texels, so a rebuilt or re-unwrapped mesh gets
/// its paint re-applied from scratch rather than pasted through an atlas that moved under it. This is
/// the re-applier: for each stroke it drops the brush sphere onto the surface and rasterises whatever
/// faces the sphere touches, weighting every texel by its 3D distance from the dab centre. Neither
/// the brush nor the rasteriser ever learns what a seam is — a stroke crossing a chart boundary
/// paints both charts because both have faces inside the sphere.
///
/// A STROKE IS COMPOSITED ONCE, NOT PER DAB. The vertex-colour path blended every dab straight into
/// the result, so a held brush kept compositing the same coverage over itself and the colour "ticked
/// up" like a sculpt brush instead of laying down like a paint brush. Here each stroke's dabs are
/// first stamped into a per-stroke COVERAGE buffer (the maximum falloff any dab reached at each
/// texel, never the sum), and then that one buffer is composited into the canvas. Holding still
/// re-stamps the same coverage and changes nothing.
///
/// THE DAB IS SHARED, NOT DUPLICATED. <see cref="PaintSession"/> composites a live dab through
/// <see cref="StampDab"/>; a rebuild replays the whole list through <see cref="Replay"/>. Both paths
/// end at the same rasterise-and-stamp, so a stroke painted live and the same stroke replayed later
/// produce identical texels.
///
/// AN ERASE IS A STROKE LIKE ANY OTHER, and takes the identical route: same dabs, same coverage
/// buffer, same once-per-stroke application. Only the last step differs — <see cref="Apply"/> sends a
/// painting stroke to <see cref="Composite"/> and an erasing one to <see cref="Erase"/>. That is why
/// erases and paint interleave correctly without any ordering machinery: they are the same log.
/// </summary>
public static class PaintReplay
{
	/// <summary>How many texels a replayed canvas bleeds outward at its islands' edges, so a shader
	/// filtering across a seam finds colour there instead of the gutter. Same figure the bake uses.</summary>
	const int DilatePasses = 4;

	/// <summary>
	/// Replay every stroke, in order, onto a fresh canvas. The order is the whole point — colour
	/// blending does not commute — and this preserves it exactly, compositing each stroke once from
	/// its own coverage buffer so dabs within a stroke never accumulate.
	/// </summary>
	public static PaintCanvas Replay( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( resolution < 1 )
			throw new ArgumentOutOfRangeException( nameof( resolution ) );

		var canvas = new PaintCanvas( resolution, resolution );

		if ( strokes is { Count: > 0 } )
		{
			var bvh = MeshBVH.Build( mesh );
			var faces = new List<int>();
			var coverage = new float[resolution * resolution];

			// Which texels the log last spoke about with an ERASE, so the dilate below leaves them
			// alone. Without this an erased hole inside an island is indistinguishable from the
			// gutter outside one, and the dilate bleeds the paint straight back into it — see
			// PaintCanvas.Dilate. Allocated only when something actually erases, because the
			// overwhelmingly common document has no erases in it at all.
			bool[] erased = null;

			foreach ( var stroke in strokes )
			{
				Array.Clear( coverage, 0, coverage.Length );
				StampStroke( stroke, mesh, bvh, coverage, resolution, faces );
				Apply( canvas, stroke, coverage );

				if ( stroke.Erase )
					erased ??= new bool[resolution * resolution];

				if ( erased is not null )
					MarkErased( erased, coverage, stroke.Erase );
			}

			canvas.Dilate( DilatePasses, erased );
		}

		return canvas;
	}

	/// <summary>
	/// Apply one stroke's finished coverage to a canvas: paint composites its colour in, an erase
	/// takes coverage back out of the alpha.
	///
	/// THE ONE PLACE THAT BRANCH IS MADE. The replay, the live session's commit and its reload all
	/// end here, so there is no route by which a stroke painted by hand and the same stroke replayed
	/// later could disagree about which of the two it was.
	/// </summary>
	internal static void Apply( PaintCanvas canvas, PaintStroke stroke, float[] coverage )
	{
		if ( stroke.Erase )
			Erase( canvas, coverage );
		else
			Composite( canvas, coverage, ToByte( stroke.R ), ToByte( stroke.G ), ToByte( stroke.B ) );
	}

	/// <summary>Note which texels this stroke touched, and whether it was an erase that touched them.
	/// LAST WRITER WINS, because the strokes are a log: painting over an erased texel makes it
	/// painted again, and erasing a painted one makes it deliberately empty.</summary>
	static void MarkErased( bool[] erased, float[] coverage, bool erasing )
	{
		for ( var i = 0; i < erased.Length; i++ )
		{
			if ( coverage[i] > 0f )
				erased[i] = erasing;
		}
	}

	/// <summary>Stamp one stroke's dabs into a coverage buffer, taking the maximum. The live session
	/// uses this to seed its coverage from the stroke in flight, so it must not composite anything —
	/// the composite is the caller's step, once, at the end.</summary>
	internal static void StampStroke( PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces )
	{
		if ( stroke is null || stroke.Path.Count == 0 )
			return;

		// Coverage folds the stroke's own opacity into its strength, so the alpha the composite sees
		// is "how much paint arrives here" and the colour has no alpha of its own to double-count.
		var coverageScale = stroke.Strength * stroke.A;

		foreach ( var point in stroke.Path )
			StampDab( mesh, bvh, coverage, resolution, point.Position, point.Normal,
				stroke.Radius, coverageScale, stroke.Falloff, faces );
	}

	/// <summary>
	/// One dab into a coverage buffer: the brush sphere at <paramref name="point"/>, rasterised into
	/// the faces it reaches, each texel's coverage raised to the dab's weight where that is higher.
	/// Returns the texel bounds it actually touched, so the live session can recompose and re-upload
	/// only that region.
	///
	/// THE 3D FOOTPRINT IS THE WHOLE DESIGN. A 2D disc in UV space cannot know a seam exists; a sphere
	/// in object space touches faces on both sides of one and paints them both for free. The normal
	/// gate is the other half of the same idea — a dab on one side of a thin wall must not bleed
	/// through to the far face, which is rejected by comparing its normal to the recorded surface
	/// normal.
	///
	/// MAX, NOT SUM, is what makes a stroke lay down like paint. Two dabs of one stroke overlapping
	/// must not darken each other — the coverage is the strongest the brush pressed anywhere, and the
	/// stroke is composited from that once.
	/// </summary>
	internal static TexelBounds StampDab( PolyMesh mesh, MeshBVH bvh, float[] coverage, int resolution,
		Vec3 point, Vec3 normal, float radius, float strength, BrushFalloff falloff, List<int> faces )
	{
		if ( radius <= 0f || strength <= 0f )
			return default;

		bvh.FacesInRadius( mesh, point, radius, faces );

		var n = normal.LengthSquared >= 0.5f ? normal.Normal : new Vec3( 0, 0, 1 );
		var bounds = default( TexelBounds );

		foreach ( var fi in faces )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			// The far side of a thin wall points away from the brush; a face exactly perpendicular to
			// it belongs to the neighbouring face's own dab, not this one.
			if ( Vec3.Dot( mesh.FaceNormal( face ), n ) <= 0f )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var c = 0; c < face.Count; c++ )
				corners.Add( mesh.Positions[face.Indices[c]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var p0 = corners[ia];
				var p1 = corners[ib];
				var p2 = corners[ic];

				var u0 = face.UVs[ia];
				var u1 = face.UVs[ib];
				var u2 = face.UVs[ic];

				NormalBake.Rasterise( u0, u1, u2, resolution, resolution, ( x, y, wa, wb, wc ) =>
				{
					// The barycentrics Rasterise hands back let a texel's 3D position be rebuilt from
					// its three corners, so the falloff can be measured in object space rather than in
					// UV space — the same distance that made the brush one consistent size everywhere.
					var p = p0 * wa + p1 * wb + p2 * wc;
					var t = (p - point).Length / radius;
					var weight = Brush.Falloff( t, falloff ) * strength;

					if ( weight <= 0f )
						return;

					var index = y * resolution + x;

					if ( weight > coverage[index] )
					{
						coverage[index] = weight;
						bounds = bounds.With( x, y );
					}
				} );
			}
		}

		return bounds;
	}

	/// <summary>Composite a coverage buffer into a canvas once, at the stroke's colour. The coverage
	/// is the stroke's per-texel alpha, already capped at the maximum any of its dabs reached.</summary>
	internal static void Composite( PaintCanvas canvas, float[] coverage, byte r, byte g, byte b )
	{
		for ( var y = 0; y < canvas.Height; y++ )
		{
			for ( var x = 0; x < canvas.Width; x++ )
			{
				var weight = coverage[y * canvas.Width + x];

				if ( weight > 0f )
					canvas.Blend( x, y, r, g, b, weight );
			}
		}
	}

	/// <summary>Take a coverage buffer back out of a canvas's alpha, once — the mirror of
	/// <see cref="Composite"/>. Colour is left alone; see PaintCanvas.Erase for why straight alpha
	/// means an erase is entirely a statement about how much paint is there.</summary>
	internal static void Erase( PaintCanvas canvas, float[] coverage )
	{
		for ( var y = 0; y < canvas.Height; y++ )
		{
			for ( var x = 0; x < canvas.Width; x++ )
			{
				var weight = coverage[y * canvas.Width + x];

				if ( weight > 0f )
					canvas.Erase( x, y, weight );
			}
		}
	}

	/// <summary>A float colour channel to a byte, the same rounding the canvas blend uses.</summary>
	internal static byte ToByte( float v ) => (byte)Math.Clamp( MathF.Round( v * 255f ), 0f, 255f );
}
