using Sandbox;
using System;

namespace Marionette;

/// <summary>Which part of the body's travel becomes root motion. NEW VALUES ARE APPENDED, NEVER
/// INSERTED - see KeyInterpolation. Kernel twin: Effigy.RootMotionMode.</summary>
public enum RootMotionMode
{
	/// <summary>Travel along the ground (x and y). Bob and turn stay in the body.</summary>
	[Title( "Ground Travel" )]
	XY,

	/// <summary>Ground travel and the turn about up - a walk that curves.</summary>
	[Title( "Ground Travel + Turn" )]
	XYYaw,

	/// <summary>Travel in x, y and z (a jump, a climb) and the turn about up.</summary>
	[Title( "Full Travel" )]
	Full
}

/// <summary>What the in-game player does with a clip's root motion. See
/// RigAnimPlayerComponent.RootMotion.</summary>
public enum RootMotionPlayback
{
	/// <summary>The clip travels in the model's space, as keyed.</summary>
	[Title( "Off" )]
	Off,

	/// <summary>The GameObject is moved by the travel and the body plays in place.</summary>
	[Title( "Move GameObject" )]
	MoveObject,

	/// <summary>The body plays in place and the travel is handed to game code in RootMotionDelta,
	/// for a character controller to move by.</summary>
	[Title( "Report Only" )]
	ReportOnly
}

/// <summary>
/// A clip's root motion: the bone that carries the body's travel (the top bone), the bone the travel
/// was taken from (the hips), and how. Set by the timeline's Extract Root Motion; read by the
/// player, the export (ModelDoc ExtractMotion) and Effigy's headless sim (Effigy.RootMotionSettings).
/// </summary>
public sealed class RigRootMotion
{
	[Property] public bool Enabled { get; set; } = true;

	/// <summary>The bone the travel is keyed on and read from - the top of the skeleton.</summary>
	[Property, Title( "Root Bone" )] public string Bone { get; set; } = "root";

	/// <summary>The bone the travel was taken from - the hips or pelvis.</summary>
	[Property, Title( "Travel From" )] public string Source { get; set; } = "";

	[Property] public RootMotionMode Mode { get; set; } = RootMotionMode.XY;

	/// <summary>An even-speed line from the first frame to the last: the step-to-step surge stays in
	/// the body. Off follows the hips exactly, frame by frame.</summary>
	[Property, Title( "Even Speed" )] public bool Linear { get; set; } = true;
}

/// <summary>
/// The engine half of root motion - Effigy's RootMotion on Transform. Keep the two alike.
/// </summary>
public static class RigRootMotionMath
{
	/// <summary>
	/// The motion between two model-space transforms of one bone: the ground offset (and height for
	/// Full) and the turn about up (XY + Yaw, Full), pivoting on where the bone started. The turn is
	/// the twist of the rotation change about up, so no bone axis is assumed to be forward.
	/// Kernel twin: Effigy.RootMotion.MotionOf.
	/// </summary>
	public static Transform MotionOf( Transform start, Transform now, RootMotionMode mode )
	{
		var d = now.Position - start.Position;

		if ( mode != RootMotionMode.Full )
			d = new Vector3( d.x, d.y, 0f );

		var yaw = mode == RootMotionMode.XY ? 0f : YawBetween( start.Rotation, now.Rotation );
		var turn = Rotation.FromAxis( Vector3.Up, yaw.RadianToDegree() );
		var pivot = start.Position;

		// x -> pivot + d + turn(x - pivot)
		return new Transform( pivot + d - turn * pivot, turn );
	}

	/// <summary>The turn about up (radians, -π..π) from <paramref name="a"/> to <paramref name="b"/>.</summary>
	public static float YawBetween( Rotation a, Rotation b )
	{
		var q = b * a.Inverse;
		var angle = 2f * MathF.Atan2( q.z, q.w );

		while ( angle > MathF.PI ) angle -= 2f * MathF.PI;
		while ( angle < -MathF.PI ) angle += 2f * MathF.PI;

		return angle;
	}
}
