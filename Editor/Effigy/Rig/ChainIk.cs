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

	/// <summary>Solve a chain of three or more with FABRIK rather than CCD - Marionette's
	/// IkConstraint.Solver. Spreads the bend along the chain instead of curling the tip.</summary>
	public bool Fabrik;

	/// <summary>Spline IK - Marionette's IkSolver.Spline: the chain is laid on one even arc from its
	/// root to the goal, bowing the way the pose bows, and a goal's twist is spread down the chain.
	/// See <see cref="ChainIk.Spline"/>.</summary>
	public bool Spline;

	/// <summary>How far past its length the chain may stretch to reach a goal - Marionette's
	/// IkConstraint.MaxStretch. 1 (the default, and what a rig without the field reads back as)
	/// never stretches; 1.5 lets a cable reach half as far again. See <see cref="ChainIk.Stretch"/>.</summary>
	public float MaxStretch = 1f;

	/// <summary>A copy with its weight multiplied by <paramref name="by"/> - a clip's keyed IK weight
	/// (<see cref="RigAnimFile.IkWeightAt"/>) on top of the constraint's own.</summary>
	public RigIk Weighted( float by )
	{
		var copy = (RigIk)MemberwiseClone();
		copy.Weight = Weight * Math.Clamp( by, 0f, 1f );
		return copy;
	}

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
				Fabrik = SolverIs( e, "Fabrik", 1 ),
				Spline = SolverIs( e, "Spline", 2 ),
				MaxStretch = Num( e, "MaxStretch" ) ?? 1f,
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

	/// <summary>IkConstraint.Solver as saved: by name ("Fabrik", "Spline"), or by ordinal (1, 2) from
	/// an older serializer. Absent is CCD.</summary>
	static bool SolverIs( JsonElement e, string name, int ordinal )
	{
		if ( !e.TryGetProperty( "Solver", out var v ) )
			return false;

		return v.ValueKind switch
		{
			JsonValueKind.String => string.Equals( v.GetString(), name, StringComparison.OrdinalIgnoreCase ),
			JsonValueKind.Number => v.TryGetInt32( out var i ) && i == ordinal,
			_ => false
		};
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
///   - 3 or more with <see cref="RigIk.Fabrik"/>: FABRIK on the joint positions, then each bone
///     turned by a delta onto its solved child - the bend spreads down the chain.
///
/// STRETCH (<see cref="RigIk.MaxStretch"/> above 1): a goal past the chain's length spreads the
/// joints apart along their bones first, by up to that factor, and the solve then runs on the longer
/// chain. See <see cref="Stretch"/>.
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
	public static Xform[] Apply( Skeleton skeleton, IReadOnlyList<Xform> world, RigIk ik, RigPose goal, IReadOnlyList<RigLimit> limits = null )
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

		// A stretched chain is one the goal is at or past the end of, so its answer is a straight
		// line - which FABRIK lays exactly and CCD only creeps toward. Either solver gets it.
		var stretched = Stretch( w, target, ik.MaxStretch );

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
		else if ( n >= 3 && ik.Spline && !stretched )
		{
			Spline( w, target, ik.Pole, Math.Max( ik.Iterations, 1 ) );

			// The end only moves; its carried rotation is put back (the goal's, below, if asked).
			w[n] = Xform.Translate( w[n].Origin - world[end].Origin ) * world[end];

			if ( ik.GoalRotation )
				Twist( w, chain.Select( b => world[b] ).ToArray(), goal.Rotation );
		}
		else if ( n >= 3 && (ik.Fabrik || stretched) )
		{
			Fabrik( w, target, Math.Max( ik.Iterations, 1 ) );

			// The end only moves; its carried rotation is put back.
			w[n] = Xform.Translate( w[n].Origin - world[end].Origin ) * world[end];
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

		if ( limits is { Count: > 0 } )
		{
			var top = skeleton.Bones[chain[0]].Parent;
			Limit( w, top >= 0 ? world[top] : Xform.Identity, chain.Select( b => skeleton.Bones[b].Name ).ToArray(), limits, target, ik.Iterations );
		}

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

	/// <summary>
	/// Limit constraints inside the solve: every chain bone's parent-space rotation clamped
	/// (<see cref="RigLimit.Clamp(IReadOnlyList{RigLimit}, string, Xform)"/>), root first, each clamp
	/// carrying the bones below it. When a clamp bit, the end is no longer on the goal, so CCD passes
	/// win back what reach the limits allow - each turn clamped as it is made, the standard way to
	/// solve a limited chain. A chain no limit touches, or one the solve kept inside its limits, is
	/// left exactly as solved. The end bone keeps the rotation the solve gave it (the goal's, or its
	/// own), clamped last. Engine twin: RigConstraintSolver.Limit.
	/// </summary>
	public static void Limit( Xform[] w, Xform above, string[] names, IReadOnlyList<RigLimit> limits, Vec3 target, int iterations )
	{
		var n = w.Length - 1;
		var limited = names.Select( b => RigLimit.Applies( limits, b ) ).ToArray();

		if ( !limited.Any( x => x ) )
			return;

		Xform Parent( int k ) => k == 0 ? above : w[k - 1];

		bool ClampAt( int k )
		{
			if ( !limited[k] )
				return false;

			var parent = Parent( k );
			var local = parent.Inverse * w[k];
			var from = Quat.FromXform( local );
			var to = RigLimit.Clamp( limits, names[k], from );

			if ( MathF.Abs( from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w ) > 0.9999999f )
				return false;

			var placed = parent * to.ToXform( local.Origin, Quat.ScaleOf( local ) );
			var carry = placed * w[k].Inverse;
			w[k] = placed;

			for ( var j = k + 1; j <= n; j++ )
				w[j] = carry * w[j];

			return true;
		}

		var endRotation = Quat.FromXform( w[n] );
		var bit = false;

		for ( var k = 0; k < n; k++ )
			bit |= ClampAt( k );

		if ( bit )
		{
			for ( var pass = 0; pass < Math.Max( iterations, 10 ); pass++ )
			{
				for ( var j = n - 1; j >= 0; j-- )
				{
					var pivot = w[j].Origin;
					var delta = TwoBoneIk.DeltaBetween( w[n].Origin - pivot, target - pivot );

					for ( var k = j; k <= n; k++ )
						w[k] = TwoBoneIk.About( pivot, delta ) * w[k];

					ClampAt( j );
				}

				if ( (w[n].Origin - target).Length < 0.01f )
					break;
			}

			w[n] = endRotation.ToXform( w[n].Origin, Quat.ScaleOf( w[n] ) );
		}

		ClampAt( n );
	}

	/// <summary>
	/// Lengthen the chain toward a goal it cannot reach: each joint below the root slides out along
	/// its own bone, every bone by the same factor - the goal's distance over the chain's length,
	/// capped at <paramref name="maxStretch"/>. Rotations and scales are untouched. True when it
	/// stretched - the goal is then at or past the end of the chain. Engine twin:
	/// RigConstraintSolver.Stretch.
	///
	/// TRANSLATION, NOT SCALE. A bone scaled along its length (thickness kept at 1/sqrt of the
	/// stretch) would need non-uniform bone scale, and Source 2 has none - a DMX bone's scale is one
	/// float, the export already averages three axes into it. So the joints move apart and the skin
	/// between them stretches; every bone keeps its own thickness.
	/// </summary>
	public static bool Stretch( Xform[] w, Vec3 target, float maxStretch )
	{
		if ( !(maxStretch > 1f) || w.Length < 2 )
			return false;

		var n = w.Length - 1;
		var total = 0f;

		for ( var k = 0; k < n; k++ )
			total += (w[k + 1].Origin - w[k].Origin).Length;

		var distance = (target - w[0].Origin).Length;

		if ( total < 1e-4f || distance <= total )
			return false;

		var grow = MathF.Min( distance / total, maxStretch ) - 1f;
		var shift = Vec3.Zero;
		var above = w[0].Origin;

		for ( var k = 1; k <= n; k++ )
		{
			var origin = w[k].Origin;
			shift += (origin - above) * grow;
			above = origin;
			w[k] = Xform.Translate( shift ) * w[k];
		}

		return true;
	}

	/// <summary>FABRIK on the chain's joints, then each bone turned onto its solved child about its
	/// own head. Engine twin: RigConstraintSolver.SolveFabrik.</summary>
	static void Fabrik( Xform[] w, Vec3 target, int iterations )
	{
		var n = w.Length - 1;
		var p = new Vec3[n + 1];
		var d = new float[n];
		var total = 0f;

		for ( var k = 0; k <= n; k++ )
			p[k] = w[k].Origin;

		for ( var k = 0; k < n; k++ )
		{
			d[k] = (p[k + 1] - p[k]).Length;
			total += d[k];
		}

		var root = p[0];

		if ( (target - root).Length >= total )
		{
			var dir = (target - root).Normal;

			for ( var k = 0; k < n; k++ )
				p[k + 1] = p[k] + dir * d[k];
		}
		else
		{
			for ( var pass = 0; pass < iterations; pass++ )
			{
				p[n] = target;

				for ( var k = n - 1; k >= 0; k-- )
					p[k] = p[k + 1] + Toward( p[k] - p[k + 1] ) * d[k];

				p[0] = root;

				for ( var k = 0; k < n; k++ )
					p[k + 1] = p[k] + Toward( p[k + 1] - p[k] ) * d[k];

				if ( (p[n] - target).Length < 0.01f )
					break;
			}
		}

		for ( var j = 0; j < n; j++ )
		{
			var pivot = w[j].Origin;
			var delta = TwoBoneIk.DeltaBetween( w[j + 1].Origin - pivot, p[j + 1] - pivot );

			for ( var k = j; k <= n; k++ )
				w[k] = TwoBoneIk.About( pivot, delta ) * w[k];
		}
	}

	/// <summary>
	/// Spline IK on the chain's joints: the chain is laid on the one circular arc from its root to the
	/// goal that is exactly as long as the chain - constant curvature, so every joint bends the same
	/// amount and the slack goes into one smooth bow instead of a kink. The arc bows the way the posed
	/// chain already bows (its joints' offset from the root-goal line), or toward the pole when the
	/// pose is straight. Bones are turned onto it, then FABRIK passes settle any rounding. Out of reach
	/// it is FABRIK's straight line. Engine twin: RigConstraintSolver.SolveSpline.
	///
	/// Why: CCD curls the tip and FABRIK bends wherever the pose happens to; a tentacle or a slack
	/// cable reads as one even curve (a Bezier fitted to length was tried first and folded back on
	/// itself whenever the goal came in close).
	/// </summary>
	public static void Spline( Xform[] w, Vec3 target, Vec3 pole, int iterations )
	{
		var n = w.Length - 1;
		var p = w.Select( x => x.Origin ).ToArray();
		var d = new float[n];

		for ( var k = 0; k < n; k++ )
			d[k] = (p[k + 1] - p[k]).Length;

		var bow = Vec3.Zero;

		for ( var k = 1; k < n; k++ )
			bow += p[k] - p[0];

		if ( ArcJoints( p[0], target, bow, pole, d ) is { } q )
		{
			for ( var j = 0; j < n; j++ )
			{
				var pivot = w[j].Origin;
				var delta = TwoBoneIk.DeltaBetween( w[j + 1].Origin - pivot, q[j + 1] - pivot );

				for ( var k = j; k <= n; k++ )
					w[k] = TwoBoneIk.About( pivot, delta ) * w[k];
			}
		}

		Fabrik( w, target, iterations );
	}

	/// <summary>
	/// The joints of a chain <paramref name="lengths"/> long on the circular arc from
	/// <paramref name="root"/> to <paramref name="goal"/> whose length is the chain's: the arc's angle
	/// θ solves chord/length = sin(θ/2)/(θ/2), and it bows toward the part of <paramref name="bow"/>
	/// square to the chord (then <paramref name="pole"/>'s, then any side). Null when the goal is out
	/// of reach or on the root.
	/// </summary>
	public static Vec3[] ArcJoints( Vec3 root, Vec3 goal, Vec3 bow, Vec3 pole, float[] lengths )
	{
		var total = lengths.Sum();
		var chord = (goal - root).Length;

		if ( chord < 1e-4f || chord >= total * 0.9999f )
			return null;

		var u = (goal - root) / chord;
		var side = Square( bow, u );

		if ( side.Length < 1e-3f * total )
			side = Square( pole, u );

		if ( side.Length < 1e-6f )
			side = Square( MathF.Abs( u.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ), u );

		var v = side.Normal;

		// sin(x)/x falls from 1 at 0 to 0 at π (x = θ/2); bisect for chord/total.
		var ratio = chord / total;
		var lo = 1e-5f;
		var hi = MathF.PI - 1e-5f;

		for ( var i = 0; i < 50; i++ )
		{
			var mid = (lo + hi) * 0.5f;

			if ( MathF.Sin( mid ) / mid > ratio ) lo = mid; else hi = mid;
		}

		var theta = (lo + hi);
		var radius = total / theta;
		var centre = root + u * (chord * 0.5f) - v * (radius * MathF.Cos( theta * 0.5f ));

		var joints = new Vec3[lengths.Length + 1];
		joints[0] = root;
		var along = 0f;

		for ( var k = 0; k < lengths.Length; k++ )
		{
			along += lengths[k];
			var a = MathF.PI * 0.5f + theta * 0.5f - along / radius;
			joints[k + 1] = centre + u * (radius * MathF.Cos( a )) + v * (radius * MathF.Sin( a ));
		}

		return joints;
	}

	static Vec3 Square( Vec3 v, Vec3 unit ) => v - unit * Vec3.Dot( v, unit );

	/// <summary>
	/// Spread the goal's twist down the chain: the end's turn about the last bone, measured against
	/// where its parent carried it, shared out so each bone takes (k+1)/(n+1) of it about its own
	/// length and the end the rest - instead of the whole twist landing on the wrist. Positions do
	/// not move (each bone turns about its own axis). Engine twin: RigConstraintSolver.Twist.
	/// </summary>
	public static void Twist( Xform[] w, Xform[] original, Quat goalRotation )
	{
		var n = w.Length - 1;
		var carried = Quat.FromXform( w[n - 1] ) * Quat.FromXform( original[n - 1] ).Inverse * Quat.FromXform( original[n] );
		var q = (goalRotation * carried.Inverse).Normalized;
		var axis = Toward( w[n].Origin - w[n - 1].Origin );
		var angle = 2f * MathF.Atan2( q.x * axis.x + q.y * axis.y + q.z * axis.z, q.w );

		if ( angle > MathF.PI ) angle -= 2f * MathF.PI;
		if ( angle < -MathF.PI ) angle += 2f * MathF.PI;

		if ( MathF.Abs( angle ) < 1e-4f )
			return;

		for ( var k = 0; k < n; k++ )
		{
			var along = w[k + 1].Origin - w[k].Origin;

			if ( along.Length < 1e-6f )
				continue;

			w[k] = Xform.RotateAbout( w[k].Origin, along.Normal, angle * (k + 1) / (n + 1) ) * w[k];
		}
	}

	static Vec3 Toward( Vec3 v ) => v.Length > 1e-6f ? v.Normal : new Vec3( 0, 0, 1 );
}
