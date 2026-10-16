using System;

namespace Marionette;

/// <summary>
/// The arithmetic of a morph driver (RigMorphDriver): what a bone reads and how the reading becomes a
/// weight.
///
/// ENGINE-FREE ON PURPOSE, like RigCurve. Effigy's kernel has its own copy (MorphDriver) because the
/// kernel cannot reference the game assembly; Effigy.Tests compiles THIS file next to it and checks
/// the two agree, so a corrective fades in the same in the game, the viewport, the headless take and
/// the DMX bake. Rotations are passed as quaternion components and enums as their ordinals for the
/// same reason.
///
/// Channels (RigDriverChannel): 0 Rotation - signed degrees turned about the axis since rest, other
/// turns ignored (the twist half of a swing-twist split); 1 Swing - unsigned degrees the axis has
/// tipped from where it pointed at rest; 2 Distance - passed in as it is. Axes 0 X, 1 Y, 2 Z of the
/// bone's own rest frame. Eases are KeyInterpolation's ordinals.
/// </summary>
public static class RigDriverMath
{
	/// <summary>The reading for a bone whose parent-space rotation is <c>now</c> and was <c>rest</c>
	/// at bind. <paramref name="distance"/> is returned as is for the Distance channel.</summary>
	public static float Reading( int channel, int axis,
		float rx, float ry, float rz, float rw,
		float nx, float ny, float nz, float nw,
		float distance = 0f )
	{
		if ( channel == 2 )
			return distance;

		// delta = rest⁻¹ · now. A unit rest's inverse is its conjugate.
		var (ix, iy, iz, iw) = (-rx, -ry, -rz, rw);
		var dx = iw * nx + ix * nw + iy * nz - iz * ny;
		var dy = iw * ny - ix * nz + iy * nw + iz * nx;
		var dz = iw * nz + ix * ny - iy * nx + iz * nw;
		var dw = iw * nw - ix * nx - iy * ny - iz * nz;

		var (ax, ay, az) = Axis( axis );

		if ( channel == 1 )
		{
			// The axis turned by delta: v' = v + 2w(q×v) + 2 q×(q×v).
			var cx = dy * az - dz * ay;
			var cy = dz * ax - dx * az;
			var cz = dx * ay - dy * ax;
			var mx = ax + 2f * (dw * cx + (dy * cz - dz * cy));
			var my = ay + 2f * (dw * cy + (dz * cx - dx * cz));
			var mz = az + 2f * (dw * cz + (dx * cy - dy * cx));
			var length = MathF.Sqrt( mx * mx + my * my + mz * mz );
			var dot = length > 1e-9f ? (mx * ax + my * ay + mz * az) / length : 1f;
			return MathF.Acos( Math.Clamp( dot, -1f, 1f ) ) * 180f / MathF.PI;
		}

		var p = dx * ax + dy * ay + dz * az;
		var degrees = 2f * MathF.Atan2( p, dw ) * 180f / MathF.PI;

		while ( degrees > 180f ) degrees -= 360f;
		while ( degrees <= -180f ) degrees += 360f;

		return degrees;
	}

	/// <summary>A reading through the two-key curve: 0 at <paramref name="rest"/>,
	/// <paramref name="value"/> at <paramref name="target"/>, eased between; past the keys clamped,
	/// or carried on in a straight line when <paramref name="clamp"/> is off.</summary>
	public static float Map( float reading, float rest, float target, int ease, bool clamp, float value )
	{
		var span = target - rest;
		var t = MathF.Abs( span ) < 1e-6f ? (reading >= target ? 1f : 0f) : (reading - rest) / span;
		float eased;

		if ( t >= 0f && t <= 1f )
			eased = Ease( ease, t );
		else if ( clamp )
			eased = t < 0f ? 0f : 1f;
		else
			eased = t;

		return eased * value;
	}

	/// <summary>
	/// KeyInterpolation's remap of t, written out (Easing.QuadraticInOut / In / Out - RigKey.Apply in
	/// the kernel). Stepped is off until the target is reached. Cubic and Monotone through two keys
	/// are a straight line.
	/// </summary>
	public static float Ease( int ease, float t ) => ease switch
	{
		0 => t < 0.5f ? 2f * t * t : 1f - MathF.Pow( -2f * t + 2f, 2f ) / 2f,
		2 => t >= 1f ? 1f : 0f,
		3 => t * t,
		4 => 1f - (1f - t) * (1f - t),
		_ => t
	};

	static (float, float, float) Axis( int axis ) => axis switch
	{
		0 => (1f, 0f, 0f),
		1 => (0f, 1f, 0f),
		_ => (0f, 0f, 1f),
	};
}
