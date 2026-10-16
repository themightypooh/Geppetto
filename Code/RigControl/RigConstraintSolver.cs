using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

/// <summary>
/// Solves the constraints authored on a RigDocument.
///
/// AN IK CONSTRAINT WORKS ONE OF TWO WAYS, chosen per constraint by IkConstraint.Live:
///
///   - LIVE (the default): dragging the target bone keys the constraint's goal - a track of its
///     own on the clip (TrackTarget.IkGoal) - and the chain is solved toward that goal every frame,
///     by RigAnimPlayerComponent in game, by the viewport in the editor, and by the export when it
///     bakes. Change the constraint and every pose authored under it follows.
///   - BAKED: the drag runs the solve once and writes ordinary FK keyframes for the whole chain.
///     Playback needs no solver at all, and a clip plays identically anywhere that can read a
///     keyframe; the cost is that changing a constraint later does not touch poses already made.
///
/// LIMITS APPLY EVERYWHERE A POSE IS MADE: to keyframes (and bind poses) as they are read, and
/// inside every IK solve (SolveChain's limit argument, see Limit) - in the viewport, the game player,
/// the export and Effigy's headless sim alike (kernel twin: Effigy RigLimit / ChainIk.Limit).
/// </summary>
public static class RigConstraintSolver
{
	/// <summary>Clamp a bone's parent-space rotation to any enabled Limit constraint targeting it.
	/// Returns the transform unchanged when nothing applies.</summary>
	public static Transform ClampToLimits( RigDocument rig, string boneName, Transform local )
	{
		if ( rig is null || string.IsNullOrEmpty( boneName ) )
			return local;

		foreach ( var limit in rig.LimitConstraints )
		{
			if ( !limit.Enabled || limit.TargetBone != boneName )
				continue;

			var angles = local.Rotation.Angles();

			var clamped = new Angles(
				Math.Clamp( angles.pitch, limit.MinAngles.pitch, limit.MaxAngles.pitch ),
				Math.Clamp( angles.yaw, limit.MinAngles.yaw, limit.MaxAngles.yaw ),
				Math.Clamp( angles.roll, limit.MinAngles.roll, limit.MaxAngles.roll ) );

			// Weight blends between the free and the clamped rotation, so a limit can soften a
			// joint rather than only hard-stopping it.
			var weight = limit.Weight.Clamp( 0f, 1f );
			var result = Rotation.Lerp( local.Rotation, clamped.ToRotation(), weight );

			local = new Transform( local.Position, result, local.Scale );
		}

		return local;
	}

	/// <summary>
	/// The clamp SolveChain takes for a chain of <paramref name="bones"/> (top first): bone k's
	/// parent-space transform through ClampToLimits. Null when no enabled limit names any of them, so
	/// an unlimited chain solves exactly as before.
	/// </summary>
	public static Func<int, Transform, Transform> LimitsFor( RigDocument rig, IReadOnlyList<string> bones )
	{
		if ( rig?.LimitConstraints is not { Count: > 0 } limits || bones is null )
			return null;

		if ( !bones.Any( b => limits.Any( l => l.Enabled && l.TargetBone == b ) ) )
			return null;

		return ( k, local ) => ClampToLimits( rig, bones[k], local );
	}

	/// <summary>Whether an enabled Limit constraint names <paramref name="boneName"/>.</summary>
	public static bool IsLimited( RigDocument rig, string boneName ) =>
		rig?.LimitConstraints?.Any( l => l.Enabled && l.TargetBone == boneName ) == true;

	public static IkConstraint FindIkFor( RigDocument rig, string boneName ) =>
		rig?.IkConstraints.FirstOrDefault( c => c.Enabled && c.TargetBone == boneName );

	/// <summary>
	/// One solved bone: the world transform to write.
	/// </summary>
	public readonly record struct SolvedBone( BoneCollection.Bone Bone, Transform World );

	/// <summary>
	/// Solve a constraint from a renderer's current pose: walk ChainLength bones up from
	/// <paramref name="end"/>, solve toward <paramref name="target"/>, and hand back the chain
	/// root-first as world transforms to write. See SolveChain for what the length means.
	///
	/// <paramref name="goalRotation"/> is the end bone's new rotation, or null to keep the one it
	/// has. The pole is in the MODEL'S space and is turned with the renderer here, so a constraint
	/// bends the same way whichever way the character faces.
	/// </summary>
	public static bool TrySolve( SkinnedModelRenderer renderer, BoneCollection.Bone end, IkConstraint ik,
		Vector3 target, Rotation? goalRotation, out List<SolvedBone> solved, RigDocument rig = null )
	{
		solved = null;

		if ( !renderer.IsValid() || end is null || ik is null )
			return false;

		var length = Math.Max( ik.ChainLength, 1 );
		var bones = new BoneCollection.Bone[length + 1];
		bones[length] = end;

		for ( var k = length - 1; k >= 0; k-- )
		{
			bones[k] = bones[k + 1].Parent;

			if ( bones[k] is null )
				return false;
		}

		var world = new Transform[bones.Length];

		for ( var k = 0; k < bones.Length; k++ )
		{
			if ( !renderer.TryGetBoneTransform( bones[k], out world[k] ) )
				return false;
		}

		var limit = LimitsFor( rig, bones.Select( b => b.Name ).ToArray() );
		var above = renderer.WorldTransform;

		if ( limit is not null && bones[0].Parent is { } top && !renderer.TryGetBoneTransform( top, out above ) )
			return false;

		if ( !SolveChain( world, target, goalRotation, renderer.WorldRotation * ik.PoleDirection, ik.Iterations, ik.Weight, ik.Solver, ik.MaxStretch, above, limit ) )
			return false;

		solved = new List<SolvedBone>( bones.Length );

		for ( var k = 0; k < bones.Length; k++ )
			solved.Add( new SolvedBone( bones[k], world[k] ) );

		return true;
	}

	/// <summary>
	/// Two-bone IK from a renderer's pose, bending toward a world-space pole. Kept for callers that
	/// have no constraint to hand (the sample builders); a constraint goes through TrySolve.
	/// </summary>
	public static bool TrySolveTwoBone( SkinnedModelRenderer renderer, BoneCollection.Bone end,
		Vector3 target, Vector3 poleDirection, out List<SolvedBone> solved )
	{
		solved = null;

		if ( !renderer.IsValid() || end?.Parent is not { } mid || mid.Parent is not { } root )
			return false;

		if ( !renderer.TryGetBoneTransform( root, out var rootTx )
			|| !renderer.TryGetBoneTransform( mid, out var midTx )
			|| !renderer.TryGetBoneTransform( end, out var endTx ) )
			return false;

		var chain = new[] { rootTx, midTx, endTx };

		if ( !SolveChain( chain, target, null, poleDirection, 1, 1f ) )
			return false;

		solved = new List<SolvedBone>
		{
			new( root, chain[0] ),
			new( mid, chain[1] ),
			new( end, chain[2] )
		};

		return true;
	}

	/// <summary>
	/// IK on a chain of world transforms, solved in place: <paramref name="chain"/>[0] is the top
	/// bone that turns, the last entry is the end that reaches. Every bone keeps its length unless
	/// <paramref name="maxStretch"/> says otherwise.
	///
	/// THE LENGTH PICKS THE SOLVER - and until this existed, IkConstraint.ChainLength and Iterations
	/// were saved in every rig and read by nothing:
	///
	///   - one bone: it turns to aim the end at the target. Exact.
	///   - two bones: closed form. Two bones have an exact answer - the law of cosines gives the
	///     elbow angle directly - so iterating would be a slower way to a worse result.
	///   - three or more: cyclic coordinate descent, <paramref name="iterations"/> passes, each
	///     turning every bone from the end upward to point the end at the target. There is no
	///     closed form, and CCD, like the two-bone solve, works in deltas from where each bone
	///     already points, so it never has to know which local axis a rig treats as "along the
	///     bone" - a convention that varies per rig and is not safe to assume. The pole has no
	///     single middle joint to mean here and is not used.
	///   - three or more with <paramref name="solver"/> FABRIK: the joint positions are reached to
	///     the target and back to the root, then each bone is turned (as a delta, the same way) to
	///     point at its solved child. Spreads the bend along the chain instead of curling the tip.
	///   - three or more with Spline: laid on one even arc from root to goal, bowing the way the pose
	///     bows (or toward the pole); with a goal rotation its twist is spread down the chain.
	///
	/// <paramref name="maxStretch"/> above 1 lets the chain lengthen, by up to that factor, toward a
	/// goal it cannot reach - see Stretch. At 1 every bone keeps its length, as above.
	///
	/// The end bone keeps its world rotation unless <paramref name="goalRotation"/> is given.
	/// <paramref name="weight"/> blends every chain bone between where it was and the solve.
	///
	/// <paramref name="limit"/> (LimitsFor) keeps the solve inside the rig's Limit constraints - see
	/// Limit; <paramref name="above"/> is the world transform of the top bone's parent, which a
	/// parent-space limit is measured against.
	///
	/// Effigy's kernel has the same solve on its own types (ChainIk), which is what a headless take
	/// check and the .vmdl export use - keep the two alike.
	/// </summary>
	public static bool SolveChain( Transform[] chain, Vector3 target, Rotation? goalRotation, Vector3 pole, int iterations, float weight,
		IkSolver solver = IkSolver.Ccd, float maxStretch = 1f, Transform above = default, Func<int, Transform, Transform> limit = null )
	{
		if ( chain is null || chain.Length < 2 )
			return false;

		var original = chain.ToArray();      // not chain.Clone(): Array.Clone is off s&box's whitelist (SB1000)
		var n = chain.Length - 1;

		// A stretched chain is one the goal is at or past the end of, so its answer is a straight
		// line - which FABRIK lays exactly and CCD only creeps toward. Either solver gets it.
		var stretched = Stretch( chain, target, maxStretch );

		if ( n == 2 )
		{
			if ( !SolveTwoBone( chain, target, pole ) )
				return false;
		}
		else if ( n >= 3 && solver == IkSolver.Spline && !stretched )
		{
			SolveSpline( chain, target, pole, Math.Max( iterations, 1 ) );

			// The end only moves; the rotation it was carried through is put back.
			chain[n] = new Transform( chain[n].Position, original[n].Rotation, original[n].Scale );

			if ( goalRotation is { } twistTo )
				Twist( chain, original, twistTo );
		}
		else if ( n >= 3 && (solver == IkSolver.Fabrik || stretched) )
		{
			SolveFabrik( chain, target, Math.Max( iterations, 1 ) );

			// The end only moves; the rotation it was carried through is put back.
			chain[n] = new Transform( chain[n].Position, original[n].Rotation, original[n].Scale );
		}
		else
		{
			// One bone is one step, and exact; more are iterated.
			var passes = n == 1 ? 1 : Math.Max( iterations, 1 );

			for ( var pass = 0; pass < passes; pass++ )
			{
				for ( var j = n - 1; j >= 0; j-- )
				{
					var pivot = chain[j].Position;
					var delta = DeltaBetween( chain[n].Position - pivot, target - pivot );

					for ( var k = j; k <= n; k++ )
						chain[k] = new Transform( pivot + delta * (chain[k].Position - pivot), delta * chain[k].Rotation, chain[k].Scale );
				}

				if ( chain[n].Position.Distance( target ) < 0.01f )
					break;
			}

			// The end only moves; the rotation it was carried through is put back.
			chain[n] = new Transform( chain[n].Position, original[n].Rotation, original[n].Scale );
		}

		if ( goalRotation is { } turn )
			chain[n] = new Transform( chain[n].Position, turn, chain[n].Scale );

		if ( limit is not null )
			Limit( chain, above, limit, target, iterations );

		weight = weight.Clamp( 0f, 1f );

		if ( weight < 1f )
		{
			for ( var k = 0; k <= n; k++ )
				chain[k] = Transform.Lerp( original[k], chain[k], weight, true );
		}

		return true;
	}

	/// <summary>
	/// Limit constraints inside the solve: every chain bone's parent-space transform clamped by
	/// <paramref name="limit"/>, root first, each clamp carrying the bones below it. When a clamp
	/// bit, the end is off the goal, so CCD passes win back what reach the limits allow, each turn
	/// clamped as it is made. A chain the solve kept inside its limits is left exactly as solved; the
	/// end keeps the rotation the solve gave it, clamped last. Kernel twin: Effigy ChainIk.Limit.
	/// </summary>
	public static void Limit( Transform[] chain, Transform above, Func<int, Transform, Transform> limit, Vector3 target, int iterations )
	{
		var n = chain.Length - 1;

		Transform Parent( int k ) => k == 0 ? above : chain[k - 1];

		bool ClampAt( int k )
		{
			var parent = Parent( k );
			var local = parent.ToLocal( chain[k] );
			var clamped = limit( k, local );

			var a = local.Rotation;
			var b = clamped.Rotation;

			if ( MathF.Abs( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w ) > 0.9999999f )
				return false;

			var placed = parent.ToWorld( new Transform( local.Position, clamped.Rotation, local.Scale ) );
			var old = chain[k];
			chain[k] = placed;

			for ( var j = k + 1; j <= n; j++ )
				chain[j] = placed.ToWorld( old.ToLocal( chain[j] ) );

			return true;
		}

		var endRotation = chain[n].Rotation;
		var bit = false;

		for ( var k = 0; k < n; k++ )
			bit |= ClampAt( k );

		if ( bit )
		{
			for ( var pass = 0; pass < Math.Max( iterations, 10 ); pass++ )
			{
				for ( var j = n - 1; j >= 0; j-- )
				{
					var pivot = chain[j].Position;
					var delta = DeltaBetween( chain[n].Position - pivot, target - pivot );

					for ( var k = j; k <= n; k++ )
						chain[k] = new Transform( pivot + delta * (chain[k].Position - pivot), delta * chain[k].Rotation, chain[k].Scale );

					ClampAt( j );
				}

				if ( chain[n].Position.Distance( target ) < 0.01f )
					break;
			}

			chain[n] = new Transform( chain[n].Position, endRotation, chain[n].Scale );
		}

		ClampAt( n );
	}

	/// <summary>
	/// Lengthen a chain toward a goal it cannot reach: each joint below the root slides out along its
	/// own bone, every bone by the same factor - the goal's distance over the chain's length, capped
	/// at <paramref name="maxStretch"/>. Rotations and scales are untouched, and the solve that
	/// follows runs on the longer chain. True when it stretched - the goal is then at or past the end
	/// of the chain. Kernel twin: Effigy ChainIk.Stretch.
	///
	/// TRANSLATION, NOT SCALE. Scaling each bone along its length with the thickness at 1/sqrt of
	/// the stretch would need non-uniform bone scale, and Source 2 has none - a bone's scale is one
	/// float in a DMX, and the export already averages three axes into it. So the joints move apart
	/// and the skin between them stretches; every bone keeps its own thickness.
	/// </summary>
	public static bool Stretch( Transform[] chain, Vector3 target, float maxStretch )
	{
		if ( !(maxStretch > 1f) || chain is null || chain.Length < 2 )
			return false;

		var n = chain.Length - 1;
		var total = 0f;

		for ( var k = 0; k < n; k++ )
			total += (chain[k + 1].Position - chain[k].Position).Length;

		var distance = (target - chain[0].Position).Length;

		if ( total < 1e-4f || distance <= total )
			return false;

		var grow = MathF.Min( distance / total, maxStretch ) - 1f;
		var shift = Vector3.Zero;
		var above = chain[0].Position;

		for ( var k = 1; k <= n; k++ )
		{
			var position = chain[k].Position;
			shift += (position - above) * grow;
			above = position;
			chain[k] = new Transform( position + shift, chain[k].Rotation, chain[k].Scale );
		}

		return true;
	}

	/// <summary>
	/// FABRIK on joint positions, then the chain turned bone by bone onto them. Each turn is the
	/// shortest-arc delta from where the child is to where it was solved, applied about the bone's
	/// own head to it and everything below - lengths are kept by construction, and no bone's
	/// "along" axis is ever assumed. Kernel twin: Effigy ChainIk.Fabrik.
	/// </summary>
	private static void SolveFabrik( Transform[] chain, Vector3 target, int iterations )
	{
		var n = chain.Length - 1;
		var p = new Vector3[n + 1];
		var d = new float[n];
		var total = 0f;

		for ( var k = 0; k <= n; k++ )
			p[k] = chain[k].Position;

		for ( var k = 0; k < n; k++ )
		{
			d[k] = (p[k + 1] - p[k]).Length;
			total += d[k];
		}

		var root = p[0];

		if ( (target - root).Length >= total )
		{
			// Out of reach: the chain lies straight along the line to the target.
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

				if ( p[n].Distance( target ) < 0.01f )
					break;
			}
		}

		for ( var j = 0; j < n; j++ )
		{
			var pivot = chain[j].Position;
			var delta = DeltaBetween( chain[j + 1].Position - pivot, p[j + 1] - pivot );

			for ( var k = j; k <= n; k++ )
				chain[k] = new Transform( pivot + delta * (chain[k].Position - pivot), delta * chain[k].Rotation, chain[k].Scale );
		}
	}

	/// <summary>
	/// Spline IK: the chain laid on the one circular arc from its root to the goal that is exactly as
	/// long as the chain - constant curvature, so every joint bends the same and slack goes into one
	/// smooth bow, never a kink. It bows the way the posed chain bows, or toward the pole when the pose
	/// is straight. Bones are turned onto it, then FABRIK passes settle any rounding. Out of reach it
	/// is FABRIK's straight line. Kernel twin: Effigy ChainIk.Spline.
	///
	/// CCD curls the tip and FABRIK bends wherever the pose happens to; a tentacle or a slack cable
	/// reads as one even curve. (A Bezier fitted to length was tried first and folded back on itself
	/// whenever the goal came in close.)
	/// </summary>
	private static void SolveSpline( Transform[] chain, Vector3 target, Vector3 pole, int iterations )
	{
		var n = chain.Length - 1;
		var p = chain.Select( t => t.Position ).ToArray();
		var d = new float[n];

		for ( var k = 0; k < n; k++ )
			d[k] = (p[k + 1] - p[k]).Length;

		var bow = Vector3.Zero;

		for ( var k = 1; k < n; k++ )
			bow += p[k] - p[0];

		if ( ArcJoints( p[0], target, bow, pole, d ) is { } q )
		{
			for ( var j = 0; j < n; j++ )
			{
				var pivot = chain[j].Position;
				var delta = DeltaBetween( chain[j + 1].Position - pivot, q[j + 1] - pivot );

				for ( var k = j; k <= n; k++ )
					chain[k] = new Transform( pivot + delta * (chain[k].Position - pivot), delta * chain[k].Rotation, chain[k].Scale );
			}
		}

		SolveFabrik( chain, target, iterations );
	}

	/// <summary>Joints of a chain <paramref name="lengths"/> long on the root-to-goal circular arc of
	/// the chain's length (θ solves chord/length = sin(θ/2)/(θ/2)), bowing toward
	/// <paramref name="bow"/> square to the chord, then the pole's, then any side. Null out of reach.
	/// Kernel twin: Effigy ChainIk.ArcJoints - keep the two alike.</summary>
	public static Vector3[] ArcJoints( Vector3 root, Vector3 goal, Vector3 bow, Vector3 pole, float[] lengths )
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
			side = Square( MathF.Abs( u.z ) < 0.9f ? Vector3.Up : Vector3.Forward, u );

		var v = side.Normal;
		var ratio = chord / total;
		var lo = 1e-5f;
		var hi = MathF.PI - 1e-5f;

		for ( var i = 0; i < 50; i++ )
		{
			var mid = (lo + hi) * 0.5f;

			if ( MathF.Sin( mid ) / mid > ratio ) lo = mid; else hi = mid;
		}

		var theta = lo + hi;
		var radius = total / theta;
		var centre = root + u * (chord * 0.5f) - v * (radius * MathF.Cos( theta * 0.5f ));

		var joints = new Vector3[lengths.Length + 1];
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

	private static Vector3 Square( Vector3 v, Vector3 unit ) => v - unit * Vector3.Dot( v, unit );

	/// <summary>Spread a goal's twist down the chain: the end's turn about the last bone, against
	/// where its parent carried it, shared so bone k takes (k+1)/(n+1) of it about its own length.
	/// Positions do not move. Kernel twin: Effigy ChainIk.Twist.</summary>
	private static void Twist( Transform[] chain, Transform[] original, Rotation goalRotation )
	{
		var n = chain.Length - 1;
		var carried = chain[n - 1].Rotation * original[n - 1].Rotation.Inverse * original[n].Rotation;
		var q = goalRotation * carried.Inverse;
		var axis = Toward( chain[n].Position - chain[n - 1].Position );
		var angle = 2f * MathF.Atan2( q.x * axis.x + q.y * axis.y + q.z * axis.z, q.w );

		if ( angle > MathF.PI ) angle -= 2f * MathF.PI;
		if ( angle < -MathF.PI ) angle += 2f * MathF.PI;

		if ( MathF.Abs( angle ) < 1e-4f )
			return;

		for ( var k = 0; k < n; k++ )
		{
			var along = chain[k + 1].Position - chain[k].Position;

			if ( along.Length < 1e-6f )
				continue;

			var turn = Rotation.FromAxis( along.Normal, (angle * (k + 1) / (n + 1)).RadianToDegree() );
			chain[k] = new Transform( chain[k].Position, turn * chain[k].Rotation, chain[k].Scale );
		}
	}

	/// <summary>A unit vector along <paramref name="v"/>, or up when two joints landed on each
	/// other and v has no direction left.</summary>
	private static Vector3 Toward( Vector3 v ) => v.Length > 1e-6f ? v.Normal : Vector3.Up;

	/// <summary>The closed-form two-bone solve on a three-entry chain, in place. False when the
	/// chain is degenerate (zero-length bones, target on the root).</summary>
	private static bool SolveTwoBone( Transform[] chain, Vector3 target, Vector3 poleDirection )
	{
		var rootTx = chain[0];
		var midTx = chain[1];
		var endTx = chain[2];

		var a = rootTx.Position;
		var b = midTx.Position;
		var c = endTx.Position;

		var upperLength = (b - a).Length;
		var lowerLength = (c - b).Length;

		if ( upperLength <= 0.001f || lowerLength <= 0.001f )
			return false;

		var toTarget = target - a;
		var distance = toTarget.Length;

		if ( distance <= 0.001f )
			return false;

		// Clamped just inside full extension - at exactly L1+L2 the bend plane is undefined and
		// the elbow snaps to an arbitrary side.
		var reach = upperLength + lowerLength;
		distance = distance.Clamp( MathF.Abs( upperLength - lowerLength ) + 0.001f, reach - 0.001f );

		var dir = toTarget.Normal;
		var clampedTarget = a + dir * distance;

		// Law of cosines: the angle at the root between the chain direction and the upper bone.
		var cosRoot = (upperLength * upperLength + distance * distance - lowerLength * lowerLength)
			/ (2f * upperLength * distance);

		var rootAngle = MathF.Acos( cosRoot.Clamp( -1f, 1f ) ).RadianToDegree();

		// The bend plane is spanned by the chain direction and the pole. Cross gives the axis to
		// swing the upper bone around; if the pole is parallel to the chain that cross collapses,
		// so fall back to the current elbow offset, and then to any perpendicular.
		var bendAxis = Vector3.Cross( dir, poleDirection.Normal );

		if ( bendAxis.Length < 0.001f )
			bendAxis = Vector3.Cross( dir, (b - a).Normal );

		if ( bendAxis.Length < 0.001f )
			bendAxis = Vector3.Cross( dir, MathF.Abs( dir.z ) < 0.9f ? Vector3.Up : Vector3.Forward );

		bendAxis = bendAxis.Normal;

		var newMidPosition = a + Rotation.FromAxis( bendAxis, rootAngle ) * dir * upperLength;

		// Rotations are applied as deltas from where each bone currently points, so this never has
		// to know which local axis the skeleton treats as "along the bone" - a convention that
		// varies per rig and is not safe to assume.
		var upperDelta = DeltaBetween( b - a, newMidPosition - a );
		var lowerDelta = DeltaBetween( c - b, clampedTarget - newMidPosition );

		chain[0] = new Transform( a, upperDelta * rootTx.Rotation, rootTx.Scale );
		chain[1] = new Transform( newMidPosition, lowerDelta * midTx.Rotation, midTx.Scale );
		chain[2] = new Transform( clampedTarget, endTx.Rotation, endTx.Scale );

		return true;
	}

	/// <summary>Shortest rotation taking one direction onto another, built from an explicit
	/// axis and angle - Rotation.FromAxis is the one primitive here that's certain to exist and
	/// behave, and the degenerate cases (parallel, antiparallel) need handling anyway.</summary>
	internal static Rotation DeltaBetween( Vector3 from, Vector3 to )
	{
		if ( from.Length < 0.001f || to.Length < 0.001f )
			return Rotation.Identity;

		from = from.Normal;
		to = to.Normal;

		var dot = Vector3.Dot( from, to ).Clamp( -1f, 1f );

		if ( dot > 0.99999f )
			return Rotation.Identity;

		// Exactly opposite: no unique shortest arc, so pick any perpendicular axis and flip.
		if ( dot < -0.99999f )
		{
			var perpendicular = Vector3.Cross( from, MathF.Abs( from.z ) < 0.9f ? Vector3.Up : Vector3.Forward );
			return Rotation.FromAxis( perpendicular.Normal, 180f );
		}

		var axis = Vector3.Cross( from, to ).Normal;
		var angle = MathF.Acos( dot ).RadianToDegree();

		return Rotation.FromAxis( axis, angle );
	}
}
