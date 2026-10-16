using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

/// <summary>The spring presets of a live soft chain - the same four Bake Secondary Motion offers, so
/// "loose" means one thing baked or live. Custom uses the chain's own numbers.</summary>
public enum RigSoftPreset
{
	Loose,
	Heavy,
	Springy,
	Stiff,
	Custom,

	/// <summary>Keeps its length and hangs: a slack cable drapes, sags and piles (Effigy ChainSpring.Rope).
	/// Appended after Custom - new values are never inserted.</summary>
	Rope
}

/// <summary>
/// One bone and everything below it, sprung live by RigAnimPlayerComponent: lag, swing and settle
/// behind the clip and behind the GameObject moving through the world - a cable trailing its hand, a
/// sac sloshing as the heap lurches, a dangling boot.
///
/// LIVE, NOT BAKED. Bake Secondary Motion writes the same springs into the clip, which is what an
/// export or a vmdl sequence needs. Live reacts to what only the game knows: the body being dragged,
/// knocked, turned by the player. The physics is the bake's (Effigy ChainSpring): time-corrected
/// Verlet on each bone's tail, a spring back to where the animation puts it, gravity by weight,
/// length restored, a cone around the animated direction, softness accumulating down the chain.
/// Axis-agnostic: a bone's tail is its first child, so it works on any compiled model.
/// </summary>
public sealed class RigSoftChain
{
	/// <summary>The top bone; it and every bone below it swing.</summary>
	[Property] public string Bone { get; set; } = "";

	[Property] public bool Enabled { get; set; } = true;

	[Property] public RigSoftPreset Preset { get; set; } = RigSoftPreset.Loose;

	/// <summary>Spring back toward the animated pose, per unit of offset; sqrt is radians/second.</summary>
	[Property, ShowIf( nameof( Preset ), RigSoftPreset.Custom )] public float Stiffness { get; set; } = 120f;

	/// <summary>Fraction of its speed a tail keeps after one second - smaller is MORE damped.</summary>
	[Property, ShowIf( nameof( Preset ), RigSoftPreset.Custom ), Range( 0f, 1f )] public float Damping { get; set; } = 0.001f;

	/// <summary>Gravity on the tail, as a multiple of 386 in/s².</summary>
	[Property, ShowIf( nameof( Preset ), RigSoftPreset.Custom )] public float GravityWeight { get; set; } = 0.3f;

	/// <summary>The furthest a bone may stray from its animated direction, degrees.</summary>
	[Property, ShowIf( nameof( Preset ), RigSoftPreset.Custom ), Range( 0f, 180f )] public float MaxAngle { get; set; } = 60f;

	/// <summary>Custom only: run the line as a rope (keep length, drape) with the numbers above;
	/// Stiffness is then a pull back toward the animated joints, 0 a pure rope.</summary>
	[Property, ShowIf( nameof( Preset ), RigSoftPreset.Custom )] public bool Rope { get; set; }

	/// <summary>
	/// Rope only: a bone on this bone's first-child line whose head is held where the clip puts it -
	/// the cable's IK hand, so the rope sags between the body and the clamped hand instead of
	/// leaving it. Empty: the rope stops above the first bone a live IK chain holds and pins there,
	/// or hangs free.
	/// </summary>
	[Property, Title( "Pin Bone" )] public string PinBone { get; set; } = "";

	/// <summary>The numbers this chain runs with.</summary>
	public RigSpringPresets.Values Settings => Preset switch
	{
		RigSoftPreset.Custom => new RigSpringPresets.Values( Stiffness, Damping, GravityWeight, MaxAngle, Rope ),
		RigSoftPreset.Rope => RigSpringPresets.All[RigSpringPresets.All.Length - 1],
		_ => RigSpringPresets.All[(int)Preset]
	};
}

/// <summary>
/// The live spring pass for one player: per-bone tails in world space, stepped after the clip's
/// keys, layers and IK. Not serialized - a pose of the moment, reset on Play and Seek.
///
/// Same physics as the bake (Effigy ChainSpring), including what it reacts to: the rig's Soft
/// Colliders and floor (tails pushed out, SoftThickness off them), rope chains (particles at the
/// joints keeping their spacing, root pinned to the body, end pinned to the clip's hand), and the
/// rig's Limit constraints on every swung bone.
/// </summary>
internal sealed class RigSoftPass
{
	private sealed class Node
	{
		public GameObject Object;
		public BoneCollection.Bone Bone;
		public int Parent = -1;
		public int FirstChild = -1;
		public RigSpringPresets.Values Settings;
		public Vector3 Tail, Previous;
		public bool Started;
		public bool Flagged;
		public bool Limited;

		/// <summary>The top of the chain (no soft parent): it hangs against the body, so its tail does
		/// not collide.</summary>
		public bool Top;
	}

	/// <summary>A rope chain: its top node's first-child line, to an explicit pin if it has one.</summary>
	private sealed class Rope
	{
		public List<int> Line = new();
		public int Pin = -1;
		public Vector3[] P, Previous;
		public bool Started;
	}

	private readonly List<Node> _nodes = new();
	private readonly Dictionary<int, Rope> _ropes = new();
	private readonly List<(GameObject Bone, float Radius)> _colliders = new();
	private RigDocument _rig;
	private SkinnedModelRenderer _renderer;
	private float _lastStep;

	/// <summary>Gravity in s&amp;box units (inches), down - SoftSolver.DefaultGravity.</summary>
	private static readonly Vector3 Gravity = new( 0f, 0f, -386f );

	/// <summary>A tail that jumps further than this in a frame was teleported, not swung.</summary>
	private const float Teleport = 200f;

	/// <summary>Distance-constraint passes per rope step - ChainSpring.Options.Iterations.</summary>
	private const int RopeIterations = 12;

	public bool Built { get; private set; }

	public void Build( IEnumerable<RigSoftChain> chains, Func<string, GameObject> find, Func<GameObject, bool> isBone, SkinnedModelRenderer renderer,
		RigDocument rig = null )
	{
		Release();
		_nodes.Clear();
		_ropes.Clear();
		_colliders.Clear();
		_rig = rig;
		_renderer = renderer;
		var index = new Dictionary<GameObject, int>();

		void Add( GameObject go, int parent, RigSpringPresets.Values settings )
		{
			if ( index.ContainsKey( go ) )
				return;

			var i = _nodes.Count;
			index[go] = i;
			_nodes.Add( new Node
			{
				Object = go, Bone = renderer?.Model?.Bones.GetBone( go.Name ), Parent = parent, Settings = settings, Top = parent < 0,
				Limited = RigConstraintSolver.IsLimited( rig, go.Name ),
			} );

			if ( parent >= 0 && _nodes[parent].FirstChild < 0 )
				_nodes[parent].FirstChild = i;

			foreach ( var child in go.Children.Where( isBone ) )
				Add( child, i, settings );
		}

		foreach ( var chain in chains ?? Enumerable.Empty<RigSoftChain>() )
		{
			if ( chain is not { Enabled: true } || string.IsNullOrWhiteSpace( chain.Bone ) || find( chain.Bone ) is not { } top || !top.IsValid() )
				continue;

			var first = _nodes.Count;
			Add( top, -1, chain.Settings );

			if ( !chain.Settings.Rope || _nodes.Count == first || _ropes.ContainsKey( first ) )
				continue;

			// The line, down to the pin when one is named; without one it is cut each step at the
			// first bone a live IK chain holds.
			var rope = new Rope();

			for ( var i = first; i >= 0; i = _nodes[i].FirstChild )
			{
				if ( !string.IsNullOrEmpty( chain.PinBone ) && _nodes[i].Object.Name == chain.PinBone )
				{
					rope.Pin = i;
					break;
				}

				rope.Line.Add( i );
			}

			_ropes[first] = rope;
		}

		foreach ( var collider in rig?.SoftColliders ?? new List<RigSoftCollider>() )
		{
			if ( collider is not { Enabled: true } || string.IsNullOrEmpty( collider.Bone ) || find( collider.Bone ) is not { } bone || !bone.IsValid() )
				continue;

			// A cable does not collide with itself.
			if ( !index.ContainsKey( bone ) )
				_colliders.Add( (bone, collider.Radius) );
		}

		Built = true;
	}

	/// <summary>Forget the motion: the next step places every tail at the pose.</summary>
	public void Rest()
	{
		foreach ( var node in _nodes )
			node.Started = false;

		foreach ( var rope in _ropes.Values )
			rope.Started = false;
	}

	/// <summary>Give the bones back.</summary>
	public void Release()
	{
		foreach ( var node in _nodes )
		{
			if ( node.Flagged && node.Object.IsValid() )
				node.Object.Flags &= ~GameObjectFlags.ProceduralBone;

			node.Flagged = false;
		}

		Built = false;
	}

	/// <summary>
	/// One frame of springs. <paramref name="animatedLocal"/> is each bone's pose before the springs
	/// (what the clip, its layers and IK wrote, or the graph's), <paramref name="skip"/> says which
	/// bones a live IK chain holds (springing those would pull a clamped hand off its goal - a rope
	/// pinned at that hand is the exception: it takes the chain's shape and holds the hand where IK
	/// put it), and <paramref name="amount"/> fades the swing in and out with an Overlay/Additive player.
	/// </summary>
	public void Step( float dt, Func<GameObject, BoneCollection.Bone, Transform> animatedLocal, Func<GameObject, bool> skip, float amount )
	{
		if ( dt <= 0f || _nodes.Count == 0 )
			return;

		// Stiff springs need small steps; the target holds still across them, which at game frame
		// rates is a few milliseconds of hold.
		var steps = Math.Clamp( (int)MathF.Ceiling( dt / (1f / 120f) ), 1, 8 );
		var h = dt / steps;
		var count = _nodes.Count;
		var locals = new Transform[count];
		var world = new Transform[count];
		var animated = new Transform[count];
		var skipped = new bool[count];

		for ( var i = 0; i < count; i++ )
		{
			var node = _nodes[i];
			locals[i] = node.Object.IsValid() ? animatedLocal( node.Object, node.Bone ) : Transform.Zero;
			skipped[i] = node.Object.IsValid() && skip( node.Object );
			var parentWorld = node.Parent >= 0 ? animated[node.Parent] : node.Object.Parent?.WorldTransform ?? Transform.Zero;
			animated[i] = parentWorld.ToWorld( locals[i] );
		}

		// Each rope's line this step: to its pin, or cut at the first held bone, which pins it. Bones
		// on a line to an explicit pin are the rope's even if IK holds them; the held pin keeps the
		// rotation the clip gave it.
		var ropeOwned = new HashSet<int>();
		var heldPins = new HashSet<int>();
		var lines = new Dictionary<int, (List<int> Line, int Pin)>();

		foreach ( var (top, rope) in _ropes )
		{
			var line = new List<int>();
			var pin = rope.Pin;

			foreach ( var i in rope.Line )
			{
				if ( rope.Pin < 0 && skipped[i] )
				{
					pin = i;
					break;
				}

				line.Add( i );
			}

			if ( line.Count == 0 )
				continue;

			lines[top] = (line, pin);

			if ( rope.Pin >= 0 )
				ropeOwned.UnionWith( line );

			if ( pin >= 0 && skipped[pin] )
				heldPins.Add( pin );
		}

		// The body, on the pose before the springs: capsules and the floor.
		var capsules = new List<(Vector3 A, Vector3 B, float R)>();

		foreach ( var (bone, radius) in _colliders )
		{
			if ( !bone.IsValid() )
				continue;

			var child = bone.Children.FirstOrDefault( c => _renderer?.Model?.Bones.HasBone( c.Name ) == true );
			var local = bone.LocalTransform;
			var b = child.IsValid() ? child.WorldPosition : bone.WorldTransform.PointToWorld( local.Rotation.Inverse * local.Position );
			capsules.Add( (bone.WorldPosition, b, radius) );
		}

		var thickness = _rig?.SoftThickness ?? 0f;
		var floor = _rig is { Ground: true } && _renderer.IsValid()
			? (Point: _renderer.WorldTransform.PointToWorld( new Vector3( 0, 0, _rig.GroundHeight ) ), Normal: _renderer.WorldRotation.Up)
			: ((Vector3 Point, Vector3 Normal)?)null;
		var collide = capsules.Count > 0 || floor is not null;

		Vector3 Collide( Vector3 p ) => collide ? Push( p, capsules, floor, thickness ) : p;

		for ( var s = 0; s < steps; s++ )
		{
			var ropeTail = new Dictionary<int, Vector3>();

			for ( var i = 0; i < count; i++ )
			{
				var node = _nodes[i];

				if ( !node.Object.IsValid() )
					continue;

				var parentWorld = node.Parent >= 0 ? world[node.Parent] : node.Object.Parent?.WorldTransform ?? Transform.Zero;
				var rest = parentWorld.ToWorld( locals[i] );
				world[i] = rest;

				if ( heldPins.Contains( i ) )
				{
					world[i] = new Transform( rest.Position, animated[i].Rotation, rest.Scale );
					continue;
				}

				if ( skipped[i] && !ropeOwned.Contains( i ) )
				{
					node.Started = false;
					continue;
				}

				if ( lines.TryGetValue( i, out var line ) )
				{
					var rope = _ropes[i];
					StepRope( rope, line.Line, line.Pin, rest.Position, locals, animated, h, Collide );

					for ( var k = 0; k < line.Line.Count; k++ )
						ropeTail[line.Line[k]] = rope.P[k + 1];
				}

				// The tail: the first child, or a leaf's own offset carried on past its head.
				var offset = node.FirstChild >= 0 ? locals[node.FirstChild].Position : locals[i].Rotation.Inverse * locals[i].Position;

				if ( offset.LengthSquared < 1e-10f )
					continue;

				var head = rest.Position;
				var target = rest.PointToWorld( offset );
				var length = (target - head).Length;

				if ( length < 1e-5f )
					continue;

				var restDirection = (target - head) / length;
				var settings = node.Settings;
				Vector3 direction;

				if ( ropeTail.TryGetValue( i, out var onRope ) )
				{
					direction = (onRope - head).LengthSquared > 1e-12f ? (onRope - head).Normal : restDirection;
					node.Tail = node.Previous = head + direction * length;
					node.Started = true;
				}
				else
				{
					Vector3 next;

					if ( !node.Started || (node.Tail - target).Length > Teleport )
					{
						// At rest on the pose - pushed out at once if the pose itself is inside the body.
						var start = !node.Top && collide ? Collide( target ) : target;
						node.Tail = start;
						node.Previous = start;
						node.Started = true;

						if ( (start - target).LengthSquared < 1e-12f )
							continue;

						next = start;
					}
					else
					{
						var ratio = _lastStep > 1e-9f ? h / _lastStep : 1f;
						var carried = (node.Tail - node.Previous) * ratio * MathF.Pow( settings.Damping.Clamp( 0f, 1f ), h );
						var accel = (target - node.Tail) * MathF.Max( settings.Stiffness, 0f ) + Gravity * settings.Weight;
						next = node.Tail + carried + accel * (h * h);

						if ( !node.Top )
							next = Collide( next );
					}

					direction = (next - head).Normal;

					if ( direction.LengthSquared < 1e-12f )
						direction = restDirection;

					direction = Cone( direction, restDirection, settings.MaxAngle );

					// Collision beats the cone: a few rounds of push-out and back onto the bone's length
					// slide the tail round the body instead of through it.
					for ( var round = 0; !node.Top && collide && round < 4; round++ )
					{
						var pushed = Collide( head + direction * length ) - head;

						if ( pushed.LengthSquared > 1e-12f )
							direction = pushed.Normal;
					}

					node.Previous = node.Tail;
					node.Tail = head + direction * length;
				}

				var swing = Rotation.Slerp( Rotation.Identity, RigConstraintSolver.DeltaBetween( restDirection, direction ), amount.Clamp( 0f, 1f ) );
				world[i] = new Transform( head, swing * rest.Rotation, rest.Scale );

				// Inside the rig's limits, as every pose is.
				if ( node.Limited )
					world[i] = parentWorld.ToWorld( RigConstraintSolver.ClampToLimits( _rig, node.Object.Name, parentWorld.ToLocal( world[i] ) ) );
			}

			_lastStep = h;
		}

		// Top-down, so each bone is written under a parent already where it belongs.
		for ( var i = 0; i < count; i++ )
		{
			var node = _nodes[i];

			if ( !node.Object.IsValid() || (skipped[i] && !ropeOwned.Contains( i ) && !heldPins.Contains( i )) )
				continue;

			if ( !node.Object.Flags.Contains( GameObjectFlags.ProceduralBone ) )
			{
				node.Object.Flags |= GameObjectFlags.ProceduralBone;
				node.Flagged = true;
			}

			node.Object.WorldTransform = world[i];
		}
	}

	/// <summary>
	/// One step of a rope: Verlet on every free particle, then distance constraints and collision,
	/// root pinned to <paramref name="root"/> and a pinned end to the clip. Kernel twin: Effigy
	/// ChainSpring.StepRope - keep the two alike.
	/// </summary>
	private void StepRope( Rope rope, List<int> line, int pin, Vector3 root, Transform[] locals, Transform[] animated, float h, Func<Vector3, Vector3> collide )
	{
		var m = line.Count;
		var anim = new Vector3[m + 1];
		anim[0] = animated[line[0]].Position;

		for ( var k = 0; k < m; k++ )
		{
			var i = line[k];
			var node = _nodes[i];
			anim[k + 1] = k + 1 < m ? animated[line[k + 1]].Position
				: pin >= 0 ? animated[pin].Position
				: node.FirstChild >= 0 ? animated[node.FirstChild].Position
				: animated[i].PointToWorld( locals[i].Rotation.Inverse * locals[i].Position );
		}

		var lengths = new float[m];

		for ( var k = 0; k < m; k++ )
			lengths[k] = (anim[k + 1] - anim[k]).Length;

		var shift = root - anim[0];

		for ( var k = 0; k <= m; k++ )
			anim[k] += shift;

		var pinned = pin >= 0;

		if ( pinned )
			anim[m] -= shift;

		if ( !rope.Started || rope.P is null || rope.P.Length != m + 1 || (rope.P[0] - root).Length > Teleport )
		{
			rope.P = anim.ToArray();
			rope.Previous = anim.ToArray();
			rope.Started = true;
			return;
		}

		var p = rope.P;
		p[0] = root;
		rope.Previous[0] = root;
		var ratio = _lastStep > 1e-9f ? h / _lastStep : 1f;

		for ( var k = 1; k <= m; k++ )
		{
			if ( k == m && pinned )
			{
				p[k] = anim[k];
				rope.Previous[k] = anim[k];
				continue;
			}

			var settings = _nodes[line[k - 1]].Settings;
			var carried = (p[k] - rope.Previous[k]) * ratio * MathF.Pow( settings.Damping.Clamp( 0f, 1f ), h );
			var accel = (anim[k] - p[k]) * MathF.Max( settings.Stiffness, 0f ) + Gravity * settings.Weight;
			rope.Previous[k] = p[k];
			p[k] = p[k] + carried + accel * (h * h);
		}

		for ( var pass = 0; pass < RopeIterations; pass++ )
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

	/// <summary>A point of radius <paramref name="thickness"/> pushed out of every capsule and onto
	/// the floor's side. Kernel twin: Effigy ChainSpring.Push (floor there is the model's Z).</summary>
	private static Vector3 Push( Vector3 p, List<(Vector3 A, Vector3 B, float R)> capsules, (Vector3 Point, Vector3 Normal)? floor, float thickness )
	{
		foreach ( var (a, b, r) in capsules )
		{
			var ab = b - a;
			var t = ab.LengthSquared > 1e-12f ? (Vector3.Dot( p - a, ab ) / ab.LengthSquared).Clamp( 0f, 1f ) : 0f;
			var closest = a + ab * t;
			var away = p - closest;
			var reach = r + thickness;

			if ( away.LengthSquared >= reach * reach )
				continue;

			var length = away.Length;
			var direction = length > 1e-6f ? away / length : Vector3.Cross( ab.LengthSquared > 1e-12f ? ab.Normal : Vector3.Up, new Vector3( 0, 1, 0 ) ).Normal;

			if ( direction.LengthSquared < 0.5f )
				direction = Vector3.Up;

			p = closest + direction * reach;
		}

		if ( floor is { } f )
		{
			var height = Vector3.Dot( p - f.Point, f.Normal );

			if ( height < thickness )
				p += f.Normal * (thickness - height);
		}

		return p;
	}

	/// <summary>Effigy SoftSolver.Cone: hold a direction within <paramref name="degrees"/> of an axis.</summary>
	private static Vector3 Cone( Vector3 direction, Vector3 axis, float degrees )
	{
		if ( degrees >= 180f )
			return direction;

		var limit = MathF.Max( degrees, 0f ) * MathF.PI / 180f;
		var cos = Vector3.Dot( direction, axis ).Clamp( -1f, 1f );

		if ( MathF.Acos( cos ) <= limit )
			return direction;

		var perpendicular = (direction - axis * cos).Normal;

		if ( perpendicular.LengthSquared < 1e-12f )
			perpendicular = Vector3.Cross( axis, MathF.Abs( axis.x ) < 0.9f ? new Vector3( 1, 0, 0 ) : new Vector3( 0, 1, 0 ) ).Normal;

		return (axis * MathF.Cos( limit ) + perpendicular * MathF.Sin( limit )).Normal;
	}
}
