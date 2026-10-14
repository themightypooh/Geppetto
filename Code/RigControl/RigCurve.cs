using System;

namespace Marionette;

/// <summary>
/// The cubic half of a track: Hermite segments through the keys, for the Cubic and Monotone
/// interpolation modes.
///
/// WHY CUBIC IS NOT JUST ANOTHER EASE. Every older mode remaps time inside one segment and then
/// blends the two keys either side of it, so motion always comes to a dead stop - or a dead turn -
/// at every key. That is fine for a handful of hand-set poses and wrong for a dense take: a
/// solver-written clip with a key every few frames reads as a string of little jolts, one per key.
/// A Hermite segment knows the keys beyond its own two, so velocity carries THROUGH a key instead
/// of resetting at it.
///
/// THE CHANNELS ARE A TRANSFORM AS TEN NUMBERS: position x y z, rotation x y z w, scale x y z.
/// Every channel is curved on its own and the rotation is renormalised afterwards. Neighbouring
/// rotations are flipped onto the same hemisphere first, because q and -q are the same rotation
/// and a curve through both would swing the long way round.
///
/// A TANGENT IS A SLOPE IN VALUE PER FRAME, per channel. A key with no tangent of its own (the
/// default) gets one from its neighbours:
///
///   - Cubic: Catmull-Rom - the slope of the line between the keys either side. Smooth, and free
///     to overshoot: an arm swinging through a key carries a little past it.
///   - Monotone: Fritsch-Carlson. Zero at a key that is a peak or a valley in that channel, and
///     otherwise the mean of the two neighbouring slopes held to three times the smaller one. That
///     box is the sufficient half of Fritsch and Carlson's condition, and it is what guarantees
///     the curve never leaves the range of the two keys it joins - no overshoot, no wobble on a
///     hold. The local form, so a key's tangent depends only on its neighbours and any segment can
///     be evaluated alone.
///
/// The first and last key take the slope of their one segment.
///
/// ENGINE-FREE ON PURPOSE. Effigy's kernel has its own copy of this (RigTrack in RigAnimFile.cs),
/// because the kernel cannot reference the game assembly. Effigy.Tests compiles THIS file next to
/// the kernel and checks the two agree, so a clip evaluates the same in the editor, in game, and
/// in a headless take check.
/// </summary>
public static class RigCurve
{
	/// <summary>Position (3), rotation (4), scale (3).</summary>
	public const int Channels = 10;

	const int RotationStart = 3;

	/// <summary>
	/// One point on the segment from key A to key B.
	///
	/// <paramref name="outA"/> and <paramref name="inB"/> are the keys' own tangents - empty for
	/// automatic. The previous and next keys are the ones beyond A and B; null frames mean there is
	/// none. Values are in each key's stored sign; hemisphere alignment happens here.
	/// </summary>
	public static void Evaluate( bool monotone, float frame,
		float fa, ReadOnlySpan<float> va, ReadOnlySpan<float> outA,
		float fb, ReadOnlySpan<float> vb, ReadOnlySpan<float> inB,
		float? fp, ReadOnlySpan<float> vp,
		float? fn, ReadOnlySpan<float> vn,
		Span<float> result )
	{
		Span<float> a = stackalloc float[Channels];
		Span<float> b = stackalloc float[Channels];
		Span<float> p = stackalloc float[Channels];
		Span<float> n = stackalloc float[Channels];

		va.CopyTo( a );
		vb.CopyTo( b );

		// B onto A's hemisphere, the previous key onto A's, the next onto B's as it now stands.
		var signB = Align( a, b );

		if ( fp is not null )
		{
			vp.CopyTo( p );
			Align( a, p );
		}

		if ( fn is not null )
		{
			vn.CopyTo( n );
			Align( b, n );
		}

		var span = fb - fa;
		var t = span > 0f ? ((frame - fa) / span).Clamp01() : 0f;

		var t2 = t * t;
		var t3 = t2 * t;
		var h00 = 2f * t3 - 3f * t2 + 1f;
		var h10 = t3 - 2f * t2 + t;
		var h01 = -2f * t3 + 3f * t2;
		var h11 = t3 - t2;

		for ( var c = 0; c < Channels; c++ )
		{
			// A flipped key's own tangent flips with it - it was drawn against the stored sign.
			var rotation = c >= RotationStart && c < RotationStart + 4;

			var mA = outA.Length == Channels
				? outA[c]
				: Slope( monotone, fp, fp is null ? 0f : p[c], fa, a[c], fb, b[c] );

			var mB = inB.Length == Channels
				? inB[c] * (rotation ? signB : 1f)
				: Slope( monotone, fa, a[c], fb, b[c], fn, fn is null ? 0f : n[c] );

			result[c] = h00 * a[c] + h10 * span * mA + h01 * b[c] + h11 * span * mB;
		}

		Normalise( result );
	}

	/// <summary>
	/// The automatic slope at the middle key of three, in value per frame. Either neighbour may be
	/// missing (a null frame), in which case the slope is that of the one segment there is.
	/// </summary>
	public static float Slope( bool monotone, float? f0, float v0, float f1, float v1, float? f2, float v2 )
	{
		float? before = f0 is { } a && f1 - a > 0f ? (v1 - v0) / (f1 - a) : null;
		float? after = f2 is { } b && b - f1 > 0f ? (v2 - v1) / (b - f1) : null;

		if ( before is null && after is null )
			return 0f;

		if ( before is null )
			return after.Value;

		if ( after is null )
			return before.Value;

		if ( !monotone )
			return (v2 - v0) / (f2.Value - f0.Value);

		var d0 = before.Value;
		var d1 = after.Value;

		// A peak, a valley or a flat: the curve must turn here, so it arrives level.
		if ( d0 * d1 <= 0f )
			return 0f;

		var m = (d0 + d1) * 0.5f;
		var limit = 3f * MathF.Min( MathF.Abs( d0 ), MathF.Abs( d1 ) );

		return MathF.Sign( m ) * MathF.Min( MathF.Abs( m ), limit );
	}

	/// <summary>Flip <paramref name="v"/>'s rotation onto <paramref name="reference"/>'s
	/// hemisphere. Returns -1 when it flipped.</summary>
	public static float Align( ReadOnlySpan<float> reference, Span<float> v )
	{
		var dot = 0f;

		for ( var c = RotationStart; c < RotationStart + 4; c++ )
			dot += reference[c] * v[c];

		if ( dot >= 0f )
			return 1f;

		for ( var c = RotationStart; c < RotationStart + 4; c++ )
			v[c] = -v[c];

		return -1f;
	}

	/// <summary>Renormalise the rotation channels. A curve between two unit quaternions is not
	/// itself unit length, and a rotation that is not unit length scales the bone.</summary>
	public static void Normalise( Span<float> v )
	{
		var length = 0f;

		for ( var c = RotationStart; c < RotationStart + 4; c++ )
			length += v[c] * v[c];

		length = MathF.Sqrt( length );

		if ( length < 1e-6f )
		{
			v[RotationStart] = v[RotationStart + 1] = v[RotationStart + 2] = 0f;
			v[RotationStart + 3] = 1f;
			return;
		}

		for ( var c = RotationStart; c < RotationStart + 4; c++ )
			v[c] /= length;
	}

	static float Clamp01( this float value ) => value < 0f ? 0f : value > 1f ? 1f : value;
}
