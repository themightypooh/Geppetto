using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// One Limit constraint as Marionette's .ctrlrig stores it (LimitConstraint): a bone's parent-space
/// rotation clamped per Euler axis, blended by weight - Weight below 1 is a soft stop.
///
/// APPLIED EVERYWHERE A POSE IS MADE, the same way: to every keyed local as it is read, and inside
/// every IK solve (<see cref="ChainIk.Apply"/>). It used to be honoured by the editor viewport only,
/// so once keys came from a bake, a wave, a layer or an MCP tool the game and the export played a
/// pose the viewport never showed.
///
/// THE ANGLES ARE s&amp;box's <c>Rotation.Angles()</c>, formula for formula (read out of
/// Sandbox.System.dll), and the blend is <c>Rotation.Lerp</c>'s Quaternion.Lerp - so a clamp here
/// lands on the floats RigConstraintSolver.ClampToLimits does. Pinned by a test.
/// </summary>
public sealed class RigLimit
{
	public string Name = "Limit";
	public string Bone = "";
	public float Weight = 1f;

	/// <summary>Off for one read from a file without the field, as in Marionette (a new
	/// LimitConstraint starts disabled).</summary>
	public bool Enabled;

	/// <summary>Pitch, yaw, roll in degrees.</summary>
	public Vec3 Min = new( -45, -45, -45 ), Max = new( 45, 45, 45 );

	/// <summary>The Limit constraints of a .ctrlrig file, in file order - the order they apply in.</summary>
	public static List<RigLimit> ReadRig( string json )
	{
		var list = new List<RigLimit>();
		using var doc = JsonDocument.Parse( json );

		if ( !doc.RootElement.TryGetProperty( "LimitConstraints", out var array ) || array.ValueKind != JsonValueKind.Array )
			return list;

		foreach ( var e in array.EnumerateArray() )
		{
			var limit = new RigLimit
			{
				Name = Str( e, "Name" ) ?? "Limit",
				Bone = Str( e, "TargetBone" ) ?? "",
				Weight = e.TryGetProperty( "Weight", out var w ) && w.ValueKind == JsonValueKind.Number ? w.GetSingle() : 1f,
				Enabled = e.TryGetProperty( "Enabled", out var en ) && en.ValueKind == JsonValueKind.True,
			};

			if ( Angles( Str( e, "MinAngles" ) ) is { } min ) limit.Min = min;
			if ( Angles( Str( e, "MaxAngles" ) ) is { } max ) limit.Max = max;

			list.Add( limit );
		}

		return list;
	}

	/// <summary>Whether any enabled limit in <paramref name="limits"/> targets <paramref name="bone"/>.</summary>
	public static bool Applies( IReadOnlyList<RigLimit> limits, string bone ) =>
		limits is { Count: > 0 } && !string.IsNullOrEmpty( bone ) && limits.Any( l => l.Enabled && l.Bone == bone );

	/// <summary>A parent-space rotation clamped by every enabled limit on <paramref name="bone"/>, in
	/// order. Unchanged when none applies. RigConstraintSolver.ClampToLimits on the kernel's types.</summary>
	public static Quat Clamp( IReadOnlyList<RigLimit> limits, string bone, Quat rotation )
	{
		if ( limits is not { Count: > 0 } || string.IsNullOrEmpty( bone ) )
			return rotation;

		foreach ( var limit in limits )
		{
			if ( !limit.Enabled || limit.Bone != bone )
				continue;

			var (pitch, yaw, roll) = ToAngles( rotation );
			var clamped = Quat.FromAngles(
				Math.Clamp( pitch, limit.Min.x, limit.Max.x ),
				Math.Clamp( yaw, limit.Min.y, limit.Max.y ),
				Math.Clamp( roll, limit.Min.z, limit.Max.z ) );

			rotation = Lerp( rotation, clamped, Math.Clamp( limit.Weight, 0f, 1f ) );
		}

		return rotation;
	}

	/// <summary>A local transform with its rotation clamped; position and scale kept.</summary>
	public static Xform Clamp( IReadOnlyList<RigLimit> limits, string bone, Xform local )
	{
		if ( !Applies( limits, bone ) )
			return local;

		return Clamp( limits, bone, Quat.FromXform( local ) ).ToXform( local.Origin, Quat.ScaleOf( local ) );
	}

	/// <summary>A local pose with its rotation clamped.</summary>
	public static RigPose Clamp( IReadOnlyList<RigLimit> limits, string bone, RigPose local ) =>
		Applies( limits, bone ) ? local with { Rotation = Clamp( limits, bone, local.Rotation ) } : local;

	/// <summary>s&amp;box's Rotation.Angles(), in degrees - pitch about y, yaw about z, roll about x -
	/// copied from the engine, gimbal branch and all.</summary>
	public static (float Pitch, float Yaw, float Roll) ToAngles( Quat q )
	{
		const float deg = 180f / MathF.PI;
		var (x, y, z, w) = (q.x, q.y, q.z, q.w);
		var s = 2f * x * z - 2f * w * y;
		var pitch = MathF.Asin( Math.Clamp( -s, -1f, 1f ) ) * deg;

		if ( MathF.Abs( MathF.Abs( s ) - 1f ) <= 0.0001f )
		{
			var a = 2f * (w * z - x * y);
			var b = 2f * (x * z + w * y);
			var sign = -Math.Sign( s );
			return (MathF.PI / 2f * sign * deg, sign * MathF.Atan2( a * sign, b * sign ) * deg, 0f);
		}

		var yaw = MathF.Atan2( 2f * (x * y + w * z), 2f * (w * w + x * x) - 1f ) * deg;
		var roll = MathF.Atan2( 2f * (y * z + w * x), 2f * (w * w + z * z) - 1f ) * deg;

		return (pitch, yaw, roll);
	}

	/// <summary>Rotation.Lerp: System.Numerics' normalised quaternion lerp.</summary>
	static Quat Lerp( Quat a, Quat b, float t )
	{
		var r = System.Numerics.Quaternion.Lerp( new( a.x, a.y, a.z, a.w ), new( b.x, b.y, b.z, b.w ), t );
		return new Quat( r.X, r.Y, r.Z, r.W );
	}

	static Vec3? Angles( string text )
	{
		if ( string.IsNullOrEmpty( text ) )
			return null;

		var p = text.Split( ',' );

		if ( p.Length < 3 )
			return null;

		return new Vec3( F( p[0] ), F( p[1] ), F( p[2] ) );
	}

	static float F( string s ) => float.Parse( s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture );

	static string Str( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
