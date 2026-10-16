using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette;

/// <summary>What a morph driver reads off its bone. NEW VALUES ARE APPENDED, NEVER INSERTED - see
/// KeyInterpolation. Effigy's DriverChannel is the same list.</summary>
public enum RigDriverChannel
{
	/// <summary>Degrees the bone has turned about Axis since its rest pose, turns about the other
	/// axes ignored. A knee or elbow bend about its hinge axis, or a twist about the bone's length.</summary>
	Rotation,

	/// <summary>Degrees the bone's Axis has swung away from its rest direction, in any direction.
	/// For a shoulder or hip that lifts every way.</summary>
	Swing,

	/// <summary>Distance from the bone to Other Bone, in model units.</summary>
	Distance
}

/// <summary>One of the bone's own rest axes.</summary>
public enum RigDriverAxis { X, Y, Z }

/// <summary>
/// A driver on the rig: one morph's weight read off one bone's pose every frame - a corrective shape
/// key that fades in as a knee bends, the seat of a pair of trousers that holds its shape as the
/// thigh comes up. Pose-space deformation.
///
/// ON THE RIG, NOT THE CLIP, because a corrective belongs to the body: every clip played on it bends
/// the knee and wants the fix. Read every frame by RigAnimPlayerComponent (after the keys and the
/// IK), by Marionette's viewport on a scrub, and by the export, which bakes the weight into each
/// clip's flex channels so the compiled model plays it without this component too. A morph the clip
/// keys or events by hand is the clip's on those frames.
///
/// Several drivers on one morph multiply - a "thigh up AND out" corrective fires only when both do.
/// Effigy's Corrective Shape Key writes these into the model's .ctrlrig when it exports.
/// </summary>
public sealed class RigMorphDriver
{
	[Property, Title( "Morph" )] public string Morph { get; set; } = "";
	[Property] public string Bone { get; set; } = "";
	[Property, Title( "Reads" )] public RigDriverChannel Channel { get; set; } = RigDriverChannel.Rotation;
	[Property, HideIf( nameof( Channel ), RigDriverChannel.Distance )] public RigDriverAxis Axis { get; set; } = RigDriverAxis.Y;
	[Property, Title( "Other Bone" ), ShowIf( nameof( Channel ), RigDriverChannel.Distance )] public string OtherBone { get; set; } = "";

	/// <summary>The reading (degrees, or units for Distance) where the morph is 0.</summary>
	[Property] public float Rest { get; set; }

	/// <summary>The reading where the morph reaches Value - the pose it was sculpted in.</summary>
	[Property] public float Target { get; set; } = 90f;

	[Property] public KeyInterpolation Interpolation { get; set; } = KeyInterpolation.Smooth;

	/// <summary>Hold the weight between 0 and Value outside Rest..Target. Off, it carries on in a line.</summary>
	[Property] public bool Clamp { get; set; } = true;

	[Property] public float Value { get; set; } = 1f;
	[Property] public bool Enabled { get; set; } = true;

	/// <summary>"thigh_L rotation about Y, 0° → -85°" - the read-only line Marionette shows.</summary>
	public string Describe()
	{
		var unit = Channel == RigDriverChannel.Distance ? "" : "°";
		var what = Channel switch
		{
			RigDriverChannel.Rotation => $"{Bone} rotation about {Axis}",
			RigDriverChannel.Swing => $"{Bone} swing of {Axis}",
			_ => $"distance {Bone}–{OtherBone}",
		};

		return $"{what}, {Rest:0.##}{unit} → {Target:0.##}{unit}";
	}

	/// <summary>The weight for a bone whose parent-space rotation is <paramref name="local"/> and was
	/// <paramref name="bind"/> at rest; <paramref name="distance"/> for the Distance channel.</summary>
	public float Evaluate( Rotation bind, Rotation local, float distance = 0f )
	{
		var reading = RigDriverMath.Reading( (int)Channel, (int)Axis,
			bind.x, bind.y, bind.z, bind.w,
			local.x, local.y, local.z, local.w,
			distance );

		return RigDriverMath.Map( reading, Rest, Target, (int)Interpolation, Clamp, Value );
	}

	/// <summary>
	/// Every enabled driver's weight into <paramref name="into"/> (not cleared - the caller layers),
	/// drivers on one morph multiplied. <paramref name="pose"/> answers a bone's (bind, current)
	/// parent-space transforms and model-space position, or null when the model has no such bone -
	/// that driver is skipped.
	/// </summary>
	public static void WeightsAt( RigDocument rig, Func<string, (Transform Bind, Transform Local, Vector3 Model)?> pose, Dictionary<string, float> into )
	{
		if ( rig?.MorphDrivers is not { Count: > 0 } drivers || pose is null )
			return;

		var mine = new Dictionary<string, float>();

		foreach ( var d in drivers )
		{
			if ( d is null || !d.Enabled || string.IsNullOrWhiteSpace( d.Morph ) || pose( d.Bone ) is not { } bone )
				continue;

			var distance = 0f;

			if ( d.Channel == RigDriverChannel.Distance )
			{
				if ( pose( d.OtherBone ) is not { } other )
					continue;

				distance = bone.Model.Distance( other.Model );
			}

			var w = d.Evaluate( bone.Bind.Rotation, bone.Local.Rotation, distance );
			mine[d.Morph] = mine.TryGetValue( d.Morph, out var before ) ? before * w : w;
		}

		foreach ( var (morph, w) in mine )
			into[morph] = w;
	}
}
