using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Secondary motion baked over a whole clip: chosen bones lag, swing and settle behind the keyed
/// animation as if on springs, and the result comes back as ordinary per-frame poses for
/// Marionette to write as keys.
///
/// WHY NOT <see cref="SoftSolver"/>: that one runs live in Effigy's viewport and assumes the
/// skeleton's +Y is along every bone. Marionette clips are authored on compiled models whose bone
/// axes are whatever the source rig used, so this never asks: a bone's TAIL is where its first child
/// sits (or, for a leaf, as far past the head as the head is from its parent, the same way), and
/// every turn is a shortest-arc delta from the animated tail to the simulated one - the trick the IK
/// solvers use for the same reason.
///
/// The physics is SoftSolver's: time-corrected Verlet on the tail, a spring pulling it to where the
/// animation puts it, gravity scaled by the bone's weight, length restored rather than sprung, and a
/// cone around the animated direction. Settings are <see cref="SoftBone"/>, so a bone tuned in
/// Effigy feels the same baked in Marionette.
///
/// SOFTNESS ACCUMULATES DOWN A CHAIN. Each bone's head comes from its parent's SIMULATED transform,
/// so a cable of ten soft bones whips, not ten bones each wobbling on a rigid parent.
/// </summary>
public static class ChainSpring
{
	/// <summary>A named spring setting for a kind of body part. Damping is <see cref="SoftBone"/>'s:
	/// the fraction of its speed a tail keeps after one second, so smaller is MORE damped.</summary>
	public sealed record Preset( string Key, string Name, string Tip, float Stiffness, float Damping, float Weight, float MaxAngle, bool Rope = false )
	{
		public SoftBone ToSoftBone() => new() { Stiffness = Stiffness, Damping = Damping, Weight = Weight, MaxAngle = MaxAngle };
	}

	/// <summary>
	/// Tuned on a four-bone cable in ChainSpringTests: damping 0.02 flails, 0.001 swings twice and
	/// settles in about two seconds. Shared by Marionette's Bake Secondary Motion menu, its MCP tool
	/// and the headless sim, so all three mean the same thing by "loose".
	/// </summary>
	public static readonly Preset[] Presets =
	{
		new( "loose", "Loose (cable, tentacle, tail)", "Trails and whips behind the move, settles in about two seconds", 120f, 0.001f, 0.3f, 60f ),
		new( "heavy", "Heavy (flesh, sac, belly)", "Slow, sags under its own weight, sloshes rather than whips", 50f, 0.0005f, 1f, 25f ),
		new( "springy", "Springy (antenna, ear, jiggle)", "Quick wobble that dies fast", 300f, 0.003f, 0.1f, 30f ),
		new( "stiff", "Stiff (fingers, small tips)", "A frame or two of lag and almost no swing", 500f, 0.0001f, 0.05f, 15f ),
		new( "rope", "Rope (slack cable: drapes, sags, piles)", "Keeps its length and hangs under its own weight; no spring back to the pose. Pinned at an IK hand it sags between root and hand",
			0f, 0.05f, 1f, 180f, Rope: true ),
	};

	/// <summary>A capsule a soft bone collides with: from <paramref name="Bone"/>'s head to its tail
	/// (first child, or a leaf's own offset carried on), <paramref name="Radius"/> thick.</summary>
	public readonly record struct Capsule( int Bone, float Radius );

	/// <summary>A rope: <paramref name="Top"/> and its first-child line, simulated as a chain of
	/// particles that keep their spacing. <paramref name="Pin"/> is a bone on that line whose head is
	/// held where the animation puts it (an IK hand); -1 leaves the end free.</summary>
	public readonly record struct Rope( int Top, int Pin = -1 );

	/// <summary>
	/// What a bake reacts to besides its springs - shared with the live player (RigSoftPass), so the
	/// two collide and drape the same.
	///
	/// COLLISION: every soft tail (and every rope particle) is pushed out of the capsules and above the
	/// ground plane, <see cref="Thickness"/> being the soft bones' own radius. A capsule on a soft bone
	/// is ignored (a cable does not collide with itself), and so is the first bone of each chain - its
	/// tail sits against the body it hangs from.
	///
	/// ROPE: a chain's line is particles at its joints with fixed spacing (Gauss-Seidel distance
	/// constraints, <see cref="Iterations"/> passes), gravity by weight, damping per second, and
	/// Stiffness as a pull back toward the animated joints (0 = a pure rope). The root is pinned to the
	/// body; a pinned end follows the animation. The line stops at a bone a live IK chain poses (not
	/// in the soft set): that bone's head becomes the pin, so a rope never drags a clamped hand off
	/// its goal. Bones are then turned onto the particles, so lengths hold exactly.
	///
	/// LIMITS: a soft bone's swung local stays inside the rig's Limit constraints.
	/// </summary>
	public sealed class Options
	{
		public List<Capsule> Capsules = new();

		/// <summary>The floor's height on the model's Z; null for no floor.</summary>
		public float? Ground;

		public float Thickness = 1f;
		public List<Rope> Ropes = new();
		public IReadOnlyList<RigLimit> Limits;
		public int Iterations = 12;
	}

	/// <summary>A rope's line and its particles between steps.</summary>
	sealed class RopeState
	{
		public List<int> Line = new();
		public int Pin = -1;
		public Vec3[] P, Previous;
		public bool Started;
	}

	/// <summary>A preset by key ("loose") or index ("0"); null for neither.</summary>
	public static Preset FindPreset( string keyOrIndex )
	{
		if ( int.TryParse( keyOrIndex, out var i ) )
			return i >= 0 && i < Presets.Length ? Presets[i] : null;

		return Array.Find( Presets, p => string.Equals( p.Key, keyOrIndex, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>
	/// Run the springs over <paramref name="local"/> (one parent-space pose per frame, at
	/// <paramref name="fps"/>) and return the same frames with every bone in <paramref name="soft"/>
	/// replaced by its simulated local. Bones not in the map are untouched, though they still move
	/// with a soft parent.
	///
	/// <paramref name="substeps"/> integrate between frames for stability with stiff springs.
	/// <paramref name="loop"/> runs the clip once to warm up and records the second pass, so the last
	/// frame flows into the first; otherwise the springs start at rest on the first frame.
	/// </summary>
	public static List<Xform[]> Bake( Skeleton skeleton, IReadOnlyList<Xform[]> local, IReadOnlyDictionary<int, SoftBone> soft,
		float fps, Vec3? gravity = null, int substeps = 4, bool loop = false, Options options = null )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( local is null ) throw new ArgumentNullException( nameof( local ) );

		var result = new List<Xform[]>( local.Count );

		if ( local.Count == 0 || soft is null || soft.Count == 0 || fps <= 0f )
		{
			foreach ( var frame in local )
				result.Add( (Xform[])frame.Clone() );

			return result;
		}

		var g = gravity ?? SoftSolver.DefaultGravity;
		var n = skeleton.Count;
		var firstChild = new int[n];

		for ( var i = 0; i < n; i++ )
			firstChild[i] = -1;

		for ( var i = n - 1; i >= 0; i-- )
		{
			var parent = skeleton.Bones[i].Parent;

			if ( parent >= 0 )
				firstChild[parent] = i;
		}

		var tail = new Vec3[n];
		var previous = new Vec3[n];
		var started = new bool[n];
		var ropes = Ropes( skeleton, soft, firstChild, options );
		var held = new HashSet<int>( ropes.Values.Where( r => r.Pin >= 0 && !soft.ContainsKey( r.Pin ) ).Select( r => r.Pin ) );
		var steps = Math.Max( substeps, 1 );
		var dt = 1f / fps / steps;

		for ( var pass = loop ? 0 : 1; pass < 2; pass++ )
		{
			for ( var f = 0; f < local.Count; f++ )
			{
				var pose = local[f];
				Xform[] world = null;

				// The substeps walk from the previous frame's pose to this one rather than holding this
				// one: held, a bone that moves a long way in one frame teleports its head, and the
				// tail's catch-up is read as a huge velocity. Frame 0 comes from the last frame when
				// looping (that is the seam it plays across) and from itself otherwise.
				var before = f > 0 ? local[f - 1] : loop ? local[^1] : pose;

				for ( var s = 0; s < steps; s++ )
				{
					var t = (s + 1f) / steps;
					world = Step( skeleton, t >= 1f ? pose : Blend( before, pose, t ), soft, firstChild, tail, previous, started, dt, g, options, ropes );
				}

				if ( pass == 1 )
					result.Add( LocalOf( skeleton, pose, world, soft.Keys.Concat( held ) ) );
			}
		}

		return result;
	}

	/// <summary>Each rope's line: its top and first-child line while the bones are soft, stopping at
	/// its pin, or at the first bone not in the soft set (a live IK bone), which becomes the pin.</summary>
	static Dictionary<int, RopeState> Ropes( Skeleton skeleton, IReadOnlyDictionary<int, SoftBone> soft, int[] firstChild, Options options )
	{
		var ropes = new Dictionary<int, RopeState>();

		foreach ( var rope in options?.Ropes ?? new List<Rope>() )
		{
			if ( rope.Top < 0 || rope.Top >= skeleton.Count || !soft.ContainsKey( rope.Top ) || ropes.ContainsKey( rope.Top ) )
				continue;

			var state = new RopeState();
			state.Line.Add( rope.Top );

			for ( var b = firstChild[rope.Top]; b >= 0; b = firstChild[b] )
			{
				if ( b == rope.Pin || !soft.ContainsKey( b ) )
				{
					state.Pin = b;
					break;
				}

				state.Line.Add( b );
			}

			ropes[rope.Top] = state;
		}

		return ropes;
	}

	static Xform[] Step( Skeleton skeleton, Xform[] local, IReadOnlyDictionary<int, SoftBone> soft, int[] firstChild,
		Vec3[] tail, Vec3[] previous, bool[] started, float dt, Vec3 gravity, Options options = null, Dictionary<int, RopeState> ropes = null )
	{
		var n = skeleton.Count;
		var world = new Xform[n];

		// The pose before any spring - what a pin holds to and a rope's stiffness pulls toward - and
		// the capsules on the bones that are not soft, placed on it.
		var needAnimated = ropes is { Count: > 0 } || options?.Capsules is { Count: > 0 };
		var animated = needAnimated ? RigAnimFile.ModelSpace( skeleton, local ) : null;
		var capsules = new List<(Vec3 A, Vec3 B, float R)>();

		foreach ( var c in options?.Capsules ?? new List<Capsule>() )
		{
			if ( c.Bone < 0 || c.Bone >= n || soft.ContainsKey( c.Bone ) )
				continue;

			var a = animated[c.Bone].Origin;
			capsules.Add( (a, animated[c.Bone].TransformPoint( TailOffset( local, c.Bone, firstChild[c.Bone] ) ), c.Radius) );
		}

		var thickness = options?.Thickness ?? 0f;
		var collide = capsules.Count > 0 || options?.Ground is not null;

		Vec3 Collide( Vec3 p ) => collide ? Push( p, capsules, options.Ground, thickness ) : p;

		var ropeTail = new Dictionary<int, Vec3>();
		var pins = new HashSet<int>();

		for ( var i = 0; i < n; i++ )
		{
			var parent = skeleton.Bones[i].Parent;
			var rest = parent >= 0 ? world[parent] * local[i] : local[i];

			world[i] = rest;

			// A rope's pinned bone that the springs do not own (an IK hand): its head is wherever the
			// rope left it, its rotation the animation's.
			if ( pins.Contains( i ) && !soft.ContainsKey( i ) )
			{
				world[i] = Quat.FromXform( animated[i] ).ToXform( rest.Origin, Quat.ScaleOf( rest ) );
				continue;
			}

			if ( !soft.TryGetValue( i, out var settings ) || settings is null )
				continue;

			if ( ropes is not null && ropes.TryGetValue( i, out var rope ) )
			{
				StepRope( rope, rest.Origin, local, animated, firstChild, soft, dt, gravity, options, Collide );

				for ( var k = 0; k < rope.Line.Count; k++ )
					ropeTail[rope.Line[k]] = rope.P[k + 1];

				if ( rope.Pin >= 0 )
					pins.Add( rope.Pin );
			}

			var head = rest.Origin;
			var offset = TailOffset( local, i, firstChild[i] );

			if ( offset.LengthSquared < 1e-10f )
				continue;

			var target = rest.TransformPoint( offset );
			var length = (target - head).Length;
			var restDirection = (target - head) / length;

			if ( ropeTail.TryGetValue( i, out var onRope ) )
			{
				var along = onRope - head;

				if ( along.LengthSquared > 1e-12f )
					world[i] = TwoBoneIk.About( head, TwoBoneIk.DeltaBetween( restDirection, along ) ) * rest;

				tail[i] = previous[i] = head + along.Normal * length;
				started[i] = true;
				world[i] = Limit( skeleton, options, i, parent >= 0 ? world[parent] : Xform.Identity, world[i] );
				continue;
			}

			// The top bone of a chain sits against what it hangs from; the rest collide.
			var top = parent < 0 || !soft.ContainsKey( parent );
			Vec3 next;

			if ( !started[i] )
			{
				// At rest on the pose - pushed out at once if the pose itself is inside the body.
				var start = !top && collide ? Collide( target ) : target;
				tail[i] = start;
				previous[i] = start;
				started[i] = true;

				if ( (start - target).LengthSquared < 1e-12f )
					continue;

				next = start;
			}
			else
			{
				var carried = (tail[i] - previous[i]) * MathF.Pow( Math.Clamp( settings.Damping, 0f, 1f ), dt );
				var accel = (target - tail[i]) * MathF.Max( settings.Stiffness, 0f ) + gravity * settings.Weight;
				next = tail[i] + carried + accel * (dt * dt);

				if ( !top )
					next = Collide( next );
			}

			var direction = (next - head).Normal;

			if ( direction.LengthSquared < 1e-12f )
				direction = restDirection;

			direction = SoftSolver.Cone( direction, restDirection, settings.MaxAngle );

			// Collision beats the cone: a few rounds of push-out and back onto the bone's length
			// slide the tail round the body instead of through it.
			for ( var round = 0; !top && collide && round < 4; round++ )
			{
				var pushed = Collide( head + direction * length ) - head;

				if ( pushed.LengthSquared > 1e-12f )
					direction = pushed.Normal;
			}

			previous[i] = tail[i];
			tail[i] = head + direction * length;

			world[i] = TwoBoneIk.About( head, TwoBoneIk.DeltaBetween( restDirection, direction ) ) * rest;
			world[i] = Limit( skeleton, options, i, parent >= 0 ? world[parent] : Xform.Identity, world[i] );
		}

		return world;
	}

	/// <summary>A soft bone's world transform with its local held inside the rig's limits.</summary>
	static Xform Limit( Skeleton skeleton, Options options, int bone, Xform parentWorld, Xform world )
	{
		if ( !RigLimit.Applies( options?.Limits, skeleton.Bones[bone].Name ) )
			return world;

		return parentWorld * RigLimit.Clamp( options.Limits, skeleton.Bones[bone].Name, parentWorld.Inverse * world );
	}

	/// <summary>
	/// One step of a rope: Verlet on every free particle (damping, gravity by weight, stiffness toward
	/// the animated joint), then distance constraints and collision, root pinned to
	/// <paramref name="root"/> and a pinned end to the animation. Engine twin: RigSoftPass.StepRope.
	/// </summary>
	static void StepRope( RopeState rope, Vec3 root, Xform[] local, Xform[] animated, int[] firstChild, IReadOnlyDictionary<int, SoftBone> soft,
		float dt, Vec3 gravity, Options options, Func<Vec3, Vec3> collide )
	{
		var m = rope.Line.Count;
		var anim = new Vec3[m + 1];
		anim[0] = animated[rope.Line[0]].Origin;

		for ( var k = 0; k < m; k++ )
		{
			var b = rope.Line[k];
			anim[k + 1] = k + 1 < m ? animated[rope.Line[k + 1]].Origin
				: rope.Pin >= 0 ? animated[rope.Pin].Origin
				: animated[b].TransformPoint( TailOffset( local, b, firstChild[b] ) );
		}

		var lengths = new float[m];

		for ( var k = 0; k < m; k++ )
			lengths[k] = (anim[k + 1] - anim[k]).Length;

		// The animated chain hangs off the simulated root, so the targets ride with it.
		var shift = root - anim[0];

		for ( var k = 0; k <= m; k++ )
			anim[k] += shift;

		if ( rope.Pin >= 0 )
			anim[m] -= shift;

		var pinned = rope.Pin >= 0;

		if ( !rope.Started || rope.P is null || rope.P.Length != m + 1 )
		{
			rope.P = (Vec3[])anim.Clone();
			rope.Previous = (Vec3[])anim.Clone();
			rope.Started = true;
			return;
		}

		var p = rope.P;
		p[0] = root;
		rope.Previous[0] = root;

		for ( var k = 1; k <= m; k++ )
		{
			if ( k == m && pinned )
			{
				p[k] = anim[k];
				rope.Previous[k] = anim[k];
				continue;
			}

			var settings = soft[rope.Line[k - 1]];
			var carried = (p[k] - rope.Previous[k]) * MathF.Pow( Math.Clamp( settings.Damping, 0f, 1f ), dt );
			var accel = (anim[k] - p[k]) * MathF.Max( settings.Stiffness, 0f ) + gravity * settings.Weight;
			rope.Previous[k] = p[k];
			p[k] = p[k] + carried + accel * (dt * dt);
		}

		for ( var pass = 0; pass < Math.Max( options?.Iterations ?? 12, 1 ); pass++ )
		{
			for ( var k = 0; k < m; k++ )
			{
				var wa = k == 0 ? 0f : 1f;
				var wb = k + 1 == m && pinned ? 0f : 1f;

				if ( wa + wb <= 0f )
					continue;

				var delta = p[k + 1] - p[k];
				var length = delta.Length;

				if ( length < 1e-6f )
					continue;

				var fix = delta * ((length - lengths[k]) / length / (wa + wb));
				p[k] += fix * wa;
				p[k + 1] -= fix * wb;
			}

			for ( var k = 2; k <= m; k++ )
			{
				if ( !(k == m && pinned) )
					p[k] = collide( p[k] );
			}
		}
	}

	/// <summary>A point of radius <paramref name="thickness"/> pushed out of every capsule and above
	/// the ground. Engine twin: RigSoftPass.Push.</summary>
	public static Vec3 Push( Vec3 p, IReadOnlyList<(Vec3 A, Vec3 B, float R)> capsules, float? ground, float thickness )
	{
		foreach ( var (a, b, r) in capsules )
		{
			var ab = b - a;
			var t = ab.LengthSquared > 1e-12f ? Math.Clamp( Vec3.Dot( p - a, ab ) / ab.LengthSquared, 0f, 1f ) : 0f;
			var closest = a + ab * t;
			var away = p - closest;
			var reach = r + thickness;

			if ( away.LengthSquared >= reach * reach )
				continue;

			var length = away.Length;
			var direction = length > 1e-6f ? away / length : Vec3.Cross( ab.LengthSquared > 1e-12f ? ab.Normal : new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ) ).Normal;

			if ( direction.LengthSquared < 0.5f )
				direction = new Vec3( 0, 0, 1 );

			p = closest + direction * reach;
		}

		if ( ground is { } floor && p.z < floor + thickness )
			p = new Vec3( p.x, p.y, floor + thickness );

		return p;
	}

	/// <summary>Two poses mixed bone by bone: origins lerped, rotations slerped, scale lerped.</summary>
	static Xform[] Blend( Xform[] a, Xform[] b, float t )
	{
		var result = new Xform[b.Length];

		for ( var i = 0; i < b.Length; i++ )
		{
			var rotation = Quat.Slerp( Quat.FromXform( a[i] ), Quat.FromXform( b[i] ), t );
			var scale = Vec3.Lerp( Quat.ScaleOf( a[i] ), Quat.ScaleOf( b[i] ), t );
			result[i] = rotation.ToXform( Vec3.Lerp( a[i].Origin, b[i].Origin, t ), scale );
		}

		return result;
	}

	/// <summary>Where the bone's tail sits in its own space: its first child's offset, or for a leaf
	/// its own offset from its parent carried on past the head.</summary>
	static Vec3 TailOffset( Xform[] local, int bone, int child )
	{
		if ( child >= 0 )
			return local[child].Origin;

		return local[bone].Inverse.TransformDirection( local[bone].Origin );
	}

	static Xform[] LocalOf( Skeleton skeleton, Xform[] animated, Xform[] world, IEnumerable<int> soft )
	{
		var result = (Xform[])animated.Clone();

		foreach ( var i in soft )
		{
			if ( i < 0 || i >= skeleton.Count )
				continue;

			var parent = skeleton.Bones[i].Parent;
			result[i] = parent >= 0 ? world[parent].Inverse * world[i] : world[i];
		}

		return result;
	}
}
