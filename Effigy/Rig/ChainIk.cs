using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// One IK constraint as Marionette's .ctrlrig stores it - the subset a pose needs. See
/// <see cref="ChainIk"/>.
/// </summary>
public sealed class RigIk
{
	public string Name = "IK";

	/// <summary>The chain's end - the bone that reaches. Also the name its goal track is keyed under.</summary>
	public string EndBone = "";

	/// <summary>How many bones above the end the solve may turn. 2 is an arm or a leg.</summary>
	public int ChainLength = 2;

	/// <summary>Which way the middle joint bends, in model space. Two-bone chains only.</summary>
	public Vec3 Pole = new( 0, 0, 1 );

	public float Weight = 1f;

	/// <summary>Passes of the longer-chain solve. Two-bone and one-bone chains are exact and ignore it.</summary>
	public int Iterations = 10;

	public bool Enabled = true;

	/// <summary>Whether the constraint is solved at playback from a keyed goal. Off means it only
	/// acts while dragging in the editor and bakes into keyframes, so a clip has nothing to solve.
	/// A constraint built in code is live; one read from a .ctrlrig without the field is not, as in
	/// Marionette, where a rig that predates live IK stays baked.</summary>
	public bool Live = true;

	/// <summary>Whether the end bone takes the goal's rotation as well as its position.</summary>
	public bool GoalRotation = true;

	/// <summary>The constraints of a .ctrlrig file, in file order - the order they are solved in.</summary>
	public static List<RigIk> ReadRig( string json )
	{
		var list = new List<RigIk>();
		using var doc = JsonDocument.Parse( json );

		if ( !doc.RootElement.TryGetProperty( "IkConstraints", out var array ) || array.ValueKind != JsonValueKind.Array )
			return list;

		foreach ( var e in array.EnumerateArray() )
		{
			var ik = new RigIk
			{
				Name = Str( e, "Name" ) ?? "IK",
				EndBone = Str( e, "TargetBone" ) ?? "",
				ChainLength = Int( e, "ChainLength" ) ?? 2,
				Weight = Num( e, "Weight" ) ?? 1f,
				Iterations = Int( e, "Iterations" ) ?? 10,
				Enabled = Bool( e, "Enabled" ) ?? true,
				Live = Bool( e, "Live" ) ?? false,
				GoalRotation = Bool( e, "GoalRotation" ) ?? true,
			};

			if ( Str( e, "PoleDirection" ) is { } pole )
			{
				var p = pole.Split( ',' );

				if ( p.Length >= 3 )
					ik.Pole = new Vec3( F( p[0] ), F( p[1] ), F( p[2] ) );
			}

			list.Add( ik );
		}

		return list;
	}

	static float F( string s ) => float.Parse( s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture );

	static string Str( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	static int? Int( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

	static float? Num( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;

	static bool? Bool( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

/// <summary>
/// IK on a chain of any length, on a pose in model space - Marionette's
/// RigConstraintSolver.SolveChain on <see cref="Xform"/>, so a clip with live IK goals samples the
/// same headlessly (a take check, a .vmdl export) as it plays in the editor and in game.
///
/// THE CHAIN LENGTH PICKS THE SOLVER:
///
///   - 1: the one bone above the end turns to aim it at the goal. Exact.
///   - 2: <see cref="TwoBoneIk"/> - the law of cosines, bending toward the pole. Exact.
///   - 3 or more: cyclic coordinate descent, <see cref="RigIk.Iterations"/> passes from the end
///     upward. Longer chains have no closed form, and CCD is the one that never needs a bone's
///     "along" axis, the same reason the two-bone solve works in deltas. The pole does not apply.
///
/// THE END BONE KEEPS ITS ROTATION unless the constraint asks for the goal's (GoalRotation), and
/// <see cref="RigIk.Weight"/> blends every chain bone between the pose and the solve.
/// </summary>
public static class ChainIk
{
	/// <summary>
	/// Solve one constraint toward a goal in model space. Returns the whole pose with the chain
	/// moved and everything below it carried along, or null when the chain is shorter than asked or
	/// degenerate - in which case the pose stands as it was.
	/// </summary>
	public static Xform[] Apply( Skeleton skeleton, IReadOnlyList<Xform> world, RigIk ik, RigPose goal )
	{
		if ( ik is null || !ik.Enabled || ik.ChainLength < 1 )
			return null;

		var end = skeleton.IndexOf( ik.EndBone );

		if ( end < 0 )
			return null;

		// chain[0] is the top bone that turns, chain[^1] the end.
		var chain = new int[ik.ChainLength + 1];
		chain[^1] = end;

		for ( var k = chain.Length - 2; k >= 0; k-- )
		{
			chain[k] = skeleton.Bones[chain[k + 1]].Parent;

			if ( chain[k] < 0 )
				return null;
		}

		var target = goal.Position;
		var w = chain.Select( b => world[b] ).ToArray();
		var n = chain.Length - 1;

		if ( n == 2 )
		{
			var a = w[0].Origin;
			var b = w[1].Origin;
			var c = w[2].Origin;

			if ( !TwoBoneIk.TrySolve( a, b, c, target, ik.Pole, 0f, out var s ) )
				return null;

			w[0] = TwoBoneIk.About( a, s.UpperDelta ) * w[0];
			w[1] = Xform.Translate( s.Middle - b ) * TwoBoneIk.About( b, s.LowerDelta ) * w[1];
			w[2] = Xform.Translate( s.End - c ) * w[2];
		}
		else
		{
			// One bone is one CCD step, and exact; more are iterated.
			var passes = n == 1 ? 1 : Math.Max( ik.Iterations, 1 );

			for ( var pass = 0; pass < passes; pass++ )
			{
				for ( var j = n - 1; j >= 0; j-- )
				{
					var pivot = w[j].Origin;
					var delta = TwoBoneIk.DeltaBetween( w[n].Origin - pivot, target - pivot );

					for ( var k = j; k <= n; k++ )
						w[k] = TwoBoneIk.About( pivot, delta ) * w[k];
				}

				if ( (w[n].Origin - target).Length < 0.01f )
					break;
			}

			// The end only moves; its carried rotation is put back.
			w[n] = Xform.Translate( w[n].Origin - world[end].Origin ) * world[end];
		}

		if ( ik.GoalRotation )
			w[n] = goal.Rotation.ToXform( w[n].Origin, Quat.ScaleOf( world[end] ) );

		var weight = Math.Clamp( ik.Weight, 0f, 1f );

		if ( weight < 1f )
		{
			for ( var k = 0; k <= n; k++ )
			{
				var from = world[chain[k]];
				var rotation = Quat.Slerp( Quat.FromXform( from ), Quat.FromXform( w[k] ), weight );
				w[k] = rotation.ToXform( Vec3.Lerp( from.Origin, w[k].Origin, weight ), Quat.ScaleOf( from ) );
			}
		}

		// In local terms so children follow: the chain's new locals, then the pose run forward.
		var local = RigAnimFile.LocalSpace( skeleton, world );

		for ( var k = 0; k <= n; k++ )
		{
			var parent = skeleton.Bones[chain[k]].Parent;
			var parentWorld = k > 0 ? w[k - 1] : parent >= 0 ? world[parent] : Xform.Identity;
			local[chain[k]] = parent >= 0 ? parentWorld.Inverse * w[k] : w[k];
		}

		return RigAnimFile.ModelSpace( skeleton, local );
	}
}
