using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

/// <summary>
/// Plays a RigAnimDocument on a live SkinnedModelRenderer — the in-game half of Marionette.
///
/// THIS IS THE HOOKUP FOR INTERACTION CLIPS. Exporting a .vmdl is for AnimGraph / sequence
/// playback. Opening a fridge, pulling a lever, reloading: keep the character's existing model,
/// drop this next to the SkinnedModelRenderer, assign the .riganim, uncheck Play On Start and
/// Loop, and call Play() from your interact code. NormalizedTime is 0..1 on the same clock as
/// the clip, which is what you tween the fridge door against.
///
/// Posing writes LocalPosition/LocalRotation on the bone objects, and flags each one it drives
/// ProceduralBone - the engine's "animation, keep your hands off this bone". Without the flag the
/// renderer writes its own pose back over the clip every frame. A sibling RigEventPlayerComponent,
/// if present, gets the same frame so attached props stay in sync.
///
/// THREE MODES, see <see cref="RigAnimMode"/>. Exclusive is the original: the graph goes off and
/// the clip owns the body. Overlay and Additive leave the graph running - he keeps walking - and
/// take only the bones the clip keys, narrowed further by <see cref="Mask"/>. They fade in on
/// Play() and back out on Stop() or when a one-shot ends, and hand the bones back to the graph
/// once faded, so a reload is a thing that happens to the arms and then stops happening.
/// </summary>
public sealed partial class RigAnimPlayerComponent : Component
{
	[Property] public RigAnimDocument Anim { get; set; }
	[Property] public SkinnedModelRenderer Target { get; set; }

	/// <summary>Idle loops. A fridge-open does not — leave this off and call Play() on use.</summary>
	[Property] public bool Loop { get; set; } = true;

	/// <summary>Off for interaction clips. On would play the grab the moment the pawn spawns.</summary>
	[Property] public bool PlayOnStart { get; set; } = true;

	/// <summary>Whether the clip replaces the animgraph or rides on top of it. See RigAnimMode.</summary>
	[Property] public RigAnimMode Mode { get; set; } = RigAnimMode.Exclusive;

	/// <summary>
	/// The bones this clip may take, each WITH EVERYTHING BELOW IT - "arm_upper_R" is the whole
	/// right arm down to the fingertips. Empty means every bone the clip keys.
	///
	/// A mask narrows, it never widens: a bone the clip has no track for is left to the graph
	/// whatever the mask says. It exists for the clip that was authored full-body - a reload keyed
	/// with the legs standing still - played over a character who is running.
	/// </summary>
	[Property, HideIf( nameof( Mode ), RigAnimMode.Exclusive )]
	public List<string> Mask { get; set; } = new();

	/// <summary>How much of the clip reaches the body at full fade-in. 0.5 is half a wave.</summary>
	[Property, Range( 0f, 1f ), HideIf( nameof( Mode ), RigAnimMode.Exclusive )]
	public float Weight { get; set; } = 1f;

	/// <summary>Seconds to fade in on Play(). Zero snaps.</summary>
	[Property, HideIf( nameof( Mode ), RigAnimMode.Exclusive )]
	public float BlendIn { get; set; } = 0.15f;

	/// <summary>Seconds to fade out on Stop() or at the end of a one-shot. Zero snaps.</summary>
	[Property, HideIf( nameof( Mode ), RigAnimMode.Exclusive )]
	public float BlendOut { get; set; } = 0.2f;

	/// <summary>Where the fade is, 0 to 1. Always 1 in Exclusive.</summary>
	public float Blend => Mode == RigAnimMode.Exclusive ? 1f : _blend;

	/// <summary>
	/// Which GameObject each of the clip's whole-part tracks drives - and what an IK goal keyed on an
	/// object clamps to (IkGoalSpace.Object): bind the clip's "pipe" to the level's pipe and the hand
	/// holds that one, wherever it is.
	///
	/// Leave it empty for a clip that only animates the main model. Fill in one row per object the
	/// clip touches - the name has to match the reference prop it was authored against, which is
	/// what the timeline's row is labelled with.
	///
	/// A row does both jobs: it places the object from its whole-part track, AND it is where the
	/// clip's "part/bone" tracks are looked up, so a bound object that carries its own skeleton has
	/// that skeleton posed too. One name, one object, whether the clip moves it as a whole, bends
	/// its bones, or both.
	/// </summary>
	[Property] public List<AnimPartBinding> Parts { get; set; } = new();

	/// <summary>Playback speed: 2 plays twice as fast, 0.5 half. Marks still fire once per crossing.</summary>
	[Property, Range( 0.05f, 4f )] public float Rate { get; set; } = 1f;

	/// <summary>
	/// Play this clip ON TOP OF ANOTHER PLAYER rather than over the animgraph - an idle pulse over a
	/// haul, one cable's short loop over the body's clip. Set Mode to Additive or Overlay; the other
	/// player poses first every frame, then this one layers onto exactly what it wrote, so the two
	/// never fight over order. Each keeps its own clock, Loop and Rate, so loops of different lengths
	/// drift through each other and never line up.
	///
	/// Leave empty to layer over the animgraph as before. To bake the sum into one clip instead (for
	/// export or the take check), use the timeline's Layer Clip menu - RigClipLayer.MergeLayer.
	/// </summary>
	[Property, HideIf( nameof( Mode ), RigAnimMode.Exclusive )]
	public RigAnimPlayerComponent LayerOver { get; set; }

	/// <summary>
	/// Bones sprung LIVE after the clip, its layers and IK: each entry is a bone and everything
	/// below it, with a preset (Loose cable, Heavy flesh, Springy, Stiff) or its own numbers. They
	/// lag and swing behind the clip and behind the GameObject moving through the world - what a
	/// baked secondary pass cannot know about. Bones a live IK chain is holding are left alone. See
	/// RigSoftChain; Bake Secondary Motion is the same springs written into the clip instead.
	/// </summary>
	[Property, Group( "Secondary Motion" )] public List<RigSoftChain> SoftChains { get; set; } = new();

	public bool IsPlaying { get; private set; }
	public float Frame { get; private set; }

	/// <summary>Fires once when a non-looping clip hits its last keyed frame.</summary>
	public Action Finished { get; set; }

	/// <summary>
	/// Fires as playback crosses one of the clip's marks (RigAnimDocument.Marks) - the cue for a
	/// footstep, a latch clicking, a line of dialogue. Once per crossing, in frame order, including
	/// across a loop's wrap. Seeking does not fire it: a jump is not the moment arriving.
	/// </summary>
	public Action<RigMark> MarkReached { get; set; }

	private RigEventPlayerComponent _events;
	private bool _rigReady;
	private bool _notifiedFinish;

	/// <summary>Overlay/Additive: fading toward full (true) or toward nothing (false).</summary>
	private bool _active;
	private float _blend;

	/// <summary>Each skeleton track's bone, resolved once rather than searched for every frame.
	/// Null values are remembered too - a track for a part nobody bound. Cleared on Play().</summary>
	private readonly Dictionary<string, DrivenBone> _driven = new();

	/// <summary>The light objects this component put up, so it can take down its own and only
	/// its own. See SpawnLights.</summary>
	private readonly List<GameObject> _lights = new();

	/// <summary>The camera objects this component put up, so it can take down its own and only
	/// its own. See SpawnCameras.</summary>
	private readonly List<GameObject> _cameras = new();

	/// <summary>Which shot each spawned camera is, so a keyed one can follow its track.</summary>
	private readonly Dictionary<GameObject, RigCamera> _cameraShots = new();

	/// <summary>
	/// Last keyed frame, not FrameCount.
	///
	/// FrameCount defaults to 900 (the timeline canvas). Playing to that would hold a two-second
	/// grab for twenty-eight seconds of nothing, and any tween using Duration would last thirty
	/// seconds. Same rule as the editor transport: you watch what you authored.
	/// </summary>
	public float LastFrame
	{
		get
		{
			if ( Anim?.BoneTracks is null )
				return 0f;

			var last = 0f;

			foreach ( var track in Anim.BoneTracks )
			{
				foreach ( var key in track.Keyframes )
					last = MathF.Max( last, key.Frame );
			}

			return last > 0f ? last : MathF.Max( (Anim.FrameCount) - 1, 1f );
		}
	}

	public float FrameRate => Anim is { AnimationSpeed: > 0 } ? Anim.AnimationSpeed : 30f;

	/// <summary>Length in seconds of the authored clip — start the fridge tween with this duration.</summary>
	public float Duration => LastFrame / MathF.Max( FrameRate, 0.0001f );

	/// <summary>0 at the first frame, 1 at the last. Drive a door/drawer/lever with this, not a
	/// second timer, so pausing or scrubbing the clip cannot desync the prop.</summary>
	public float NormalizedTime => LastFrame <= 0f ? 1f : (Frame / LastFrame).Clamp( 0f, 1f );

	protected override void OnEnabled()
	{
		Target ??= GetComponent<SkinnedModelRenderer>();
		_events ??= GetComponent<RigEventPlayerComponent>();

		SpawnLights();
		SpawnCameras();

		if ( PlayOnStart )
			Play();
	}

	protected override void OnDisabled()
	{
		DespawnLights();
		DespawnCameras();
		ReleaseBones();
		_active = false;
		_blend = 0f;
	}

	/// <summary>
	/// Put up the clip's exported lights, as children of this object.
	///
	/// ONLY THE ONES TICKED FOR EXPORT. A clip's light list is mostly workspace lighting - what
	/// the animator needed to see the pose by - and spawning that into a game would be a window
	/// tool redecorating somebody's scene. See RigLight.Export.
	///
	/// CHILDREN, so they travel with the model. The positions were authored in the model's own
	/// space against the pose they light, which is only meaningful relative to the model: a lamp
	/// over his desk is over his desk, not at some world coordinate the clip could not have
	/// known. That also means they inherit the object's rotation, which is what you want and
	/// what makes a directional light in a clip a fill FOR THE CHARACTER rather than a sun.
	/// </summary>
	private void SpawnLights()
	{
		DespawnLights();

		if ( Anim?.Lights is null )
			return;

		foreach ( var light in Anim.Lights )
		{
			if ( light is null || !light.Enabled || !light.Export )
				continue;

			var go = new GameObject( true, string.IsNullOrWhiteSpace( light.Name ) ? "light" : light.Name );
			go.Parent = GameObject;
			go.LocalPosition = light.Position;
			go.LocalRotation = light.Rotation.ToRotation();

			switch ( light.Kind )
			{
				case RigLightKind.Ambient:
					var ambient = go.AddComponent<AmbientLight>();
					ambient.Color = light.Tint();
					break;

				case RigLightKind.Point:
					var point = go.AddComponent<PointLight>();
					point.LightColor = light.Tint();
					point.Radius = light.Range;
					point.Shadows = light.Shadows;
					break;

				case RigLightKind.Spot:
					var spot = go.AddComponent<SpotLight>();
					spot.LightColor = light.Tint();
					spot.Radius = light.Range;
					spot.ConeInner = MathF.Min( light.ConeInner, light.ConeOuter );
					spot.ConeOuter = MathF.Max( light.ConeInner, light.ConeOuter );
					spot.Shadows = light.Shadows;
					break;

				default:
					var sun = go.AddComponent<DirectionalLight>();
					sun.LightColor = light.Tint();
					sun.Shadows = light.Shadows;
					break;
			}

			_lights.Add( go );
		}
	}

	/// <summary>Take them down again. Tracked in a list rather than found by name on the way out:
	/// a light this component did not create is somebody else's, and disabling a clip player must
	/// not turn off the room.</summary>
	private void DespawnLights()
	{
		foreach ( var light in _lights )
			light?.Destroy();

		_lights.Clear();
	}

	/// <summary>
	/// Put up the clip's exported cameras, as children of this object.
	///
	/// ONLY THE ONES TICKED FOR EXPORT. A clip's camera list is mostly workspace framing - what the
	/// animator needed to see the pose by - and spawning that into a game would be a window tool
	/// redecorating somebody's scene. See RigCamera.Export.
	///
	/// CHILDREN, so they travel with the model, the same as the lights: the transform was authored
	/// in the model's own space, which is only meaningful relative to the model.
	///
	/// The cameras are made available but NOT activated - which shot is live is the game's call,
	/// not the clip's. Read them from Camera.GetComponent&lt;CameraComponent&gt;() and set
	/// Active / WorldTransform when the shot should cut.
	/// </summary>
	private void SpawnCameras()
	{
		DespawnCameras();

		if ( Anim?.Cameras is null )
			return;

		foreach ( var camera in Anim.Cameras )
		{
			if ( camera is null || !camera.Enabled || !camera.Export )
				continue;

			var go = new GameObject( true, string.IsNullOrWhiteSpace( camera.Name ) ? "camera" : camera.Name );
			go.Parent = GameObject;
			go.LocalPosition = camera.Position;
			go.LocalRotation = camera.Rotation.ToRotation();

			var cam = go.AddComponent<CameraComponent>();
			cam.FieldOfView = camera.FieldOfView;
			cam.ZNear = camera.ZNear;
			cam.ZFar = camera.ZFar;

			_cameras.Add( go );
			_cameraShots[go] = camera;
		}
	}

	/// <summary>Take them down again, only our own. A camera this component did not create is
	/// somebody else's, and disabling a clip player must not pull the level's camera out.</summary>
	private void DespawnCameras()
	{
		foreach ( var camera in _cameras )
			camera?.Destroy();

		_cameras.Clear();
		_cameraShots.Clear();
	}

	/// <summary>The player this one layers over, when it is live; see LayerOver.</summary>
	private RigAnimPlayerComponent Under =>
		Mode != RigAnimMode.Exclusive && LayerOver.IsValid() && LayerOver != this && LayerOver.Enabled && LayerOver.LayerOver != this
			? LayerOver : null;

	/// <summary>Players layered over this one, ticked right after it poses. They sign themselves up
	/// from their own OnUpdate, so a layer added or retargeted at runtime is picked up a frame later.</summary>
	private readonly List<RigAnimPlayerComponent> _layers = new();

	/// <summary>Every bone object this player wrote on its last frame - what a layer over it reads
	/// as "the pose below" instead of the animgraph's.</summary>
	private readonly HashSet<GameObject> _written = new();

	/// <summary>Bones a live IK chain solved this frame with some weight - the soft pass leaves them.</summary>
	private readonly HashSet<GameObject> _ikHeld = new();

	private readonly RigSoftPass _soft = new();

	protected override void OnUpdate()
	{
		// A layer is posed by the player under it, straight after that one's own pose.
		if ( Under is { } under )
		{
			if ( !under._layers.Contains( this ) )
				under._layers.Add( this );

			return;
		}

		Tick( Time.Delta );
	}

	private void Tick( float delta )
	{
		if ( !Target.IsValid() || Anim is null )
			return;

		EnsureRig();
		_written.Clear();
		_ikHeld.Clear();

		if ( IsPlaying )
		{
			// A clip that has just started from 0 has not "passed" frame 0 yet, so a mark there fires
			// on this first tick - the cue that goes with the clip starting.
			var before = _fromStart ? -1f : Frame;
			_fromStart = false;
			Frame += delta * FrameRate * MathF.Max( Rate, 0f );

			FireMarks( before, MathF.Min( Frame, LastFrame ) );

			if ( Frame > LastFrame )
			{
				if ( Loop )
				{
					Frame %= MathF.Max( LastFrame, 0.0001f );

					// A cycle plants its World-space goals afresh from wherever the body has got
					// to - a haul that loops grips the next stretch of pipe, not the first one.
					_worldAnchor = Target.WorldTransform;
					_rootWrapped = true;

					// The wrap starts the clip again: a mark on frame 0 has arrived, and so has
					// everything up to where this tick landed.
					FireMarks( -1f, Frame );
				}
				else
				{
					Frame = LastFrame;
					IsPlaying = false;

					// A layered one-shot hands the body back when it ends; an exclusive one holds
					// its last pose, which is what a cutscene's final frame wants.
					_active = false;

					if ( !_notifiedFinish )
					{
						_notifiedFinish = true;
						Finished?.Invoke();
					}
				}
			}
		}

		if ( Mode == RigAnimMode.Exclusive )
		{
			ApplyFrame( Frame );
			ApplyRootMotion();
			TickLayers( delta );
			ApplySoft( delta, 1f );
			return;
		}

		var fade = _active ? BlendIn : BlendOut;
		_blend = fade <= 0f
			? (_active ? 1f : 0f)
			: _blend.Approach( _active ? 1f : 0f, delta / fade );

		if ( !_active && _blend <= 0f )
		{
			ReleaseBones();
			TickLayers( delta );
			return;
		}

		ApplyFrame( Frame );
		ApplyRootMotion();
		TickLayers( delta );
		ApplySoft( delta, (Weight * _blend).Clamp( 0f, 1f ) );
	}

	/// <summary>
	/// The live springs (SoftChains), last: keys, then layers, then IK, then springs - the order the
	/// bake reacts in too. Built on first use and after Play(); a GameObject that teleports resets
	/// its tails rather than whipping them across the map.
	/// </summary>
	private void ApplySoft( float delta, float amount )
	{
		if ( SoftChains is not { Count: > 0 } )
			return;

		if ( !_soft.Built )
			_soft.Build( SoftChains, name => FindBone( Target.GameObject, name ), IsBone, Target, Anim.RigAsset );

		_soft.Step( delta,
			( go, bone ) => Wrote( go ) ? go.LocalTransform : AnimatedLocal( new DrivenBone { Object = go, Renderer = Target, Bone = bone } ),
			go => _ikHeld.Contains( go ) || _layers.Any( l => l._ikHeld.Contains( go ) ),
			amount );
	}

	/// <summary>Pose every live layer over this player, in the order they signed up, each on what
	/// the one before it left. A layer that has gone, or now layers elsewhere, drops out.</summary>
	private void TickLayers( float delta )
	{
		_layers.RemoveAll( l => !l.IsValid() || !l.Enabled || l.Under != this );

		foreach ( var layer in _layers.ToList() )
		{
			layer.Tick( delta );
			_written.UnionWith( layer._written );
		}
	}

	/// <summary>True when this player (or a layer over it) wrote <paramref name="bone"/> this frame.</summary>
	private bool Wrote( GameObject bone ) => _written.Contains( bone );

	/// <summary>Where the model stood when the clip (or this cycle of a loop) started - what a
	/// World-space IK goal is planted against. See IkGoalSpace.World.</summary>
	private Transform? _worldAnchor;

	/// <summary>Set by Play() from frame 0, so the first tick includes a mark ON frame 0.</summary>
	private bool _fromStart;

	/// <summary>Marks in (from, to]. Ordered, so two cues a frame apart arrive in the right order
	/// even on a long tick.</summary>
	private void FireMarks( float from, float to )
	{
		if ( MarkReached is null || Anim?.Marks is not { Count: > 0 } || to <= from )
			return;

		foreach ( var mark in Anim.MarksInOrder )
		{
			if ( mark.Frame > from && mark.Frame <= to )
				MarkReached( mark );
		}
	}

	/// <summary>Starts the clip. A finished one-shot restarts from frame 0 — that's what "open
	/// the fridge again" has to mean. Pause() then Play() resumes if you have not hit the end.</summary>
	public void Play()
	{
		if ( Frame >= LastFrame || (Mode != RigAnimMode.Exclusive && !_active && _blend <= 0f) )
			Frame = 0f;

		_fromStart = Frame <= 0f;

		if ( _fromStart || _worldAnchor is null )
			_worldAnchor = Target.IsValid() ? Target.WorldTransform : null;

		// Resolve afresh: Parts may have been bound, or the model swapped, since last time.
		ReleaseBones();
		_driven.Clear();
		_ikChains.Clear();
		_soft.Rest();
		ResetRootMotion();

		_notifiedFinish = false;
		IsPlaying = true;
		_active = true;
	}

	/// <summary>Holds the current frame. In Overlay/Additive the clip stays faded in, frozen.</summary>
	public void Pause() => IsPlaying = false;

	/// <summary>Exclusive: back to frame 0. Overlay/Additive: fades out from wherever it is, and
	/// the next Play() starts over.</summary>
	public void Stop()
	{
		IsPlaying = false;
		_notifiedFinish = false;

		if ( Mode == RigAnimMode.Exclusive )
			Frame = 0f;
		else
			_active = false;
	}

	public void Seek( float frame )
	{
		Frame = frame.Clamp( 0f, LastFrame );
		_notifiedFinish = false;
		_soft.Rest();
		ResetRootMotion();
		ApplyFrame( Frame );
		ApplyRootMotion();
	}

	private void EnsureRig()
	{
		if ( _rigReady )
			return;

		// Layered modes need nothing switched off - the graph running underneath is the point -
		// only the bone objects to write to.
		var exclusive = Mode == RigAnimMode.Exclusive;

		Target.CreateBoneObjects = true;

		if ( exclusive )
			Target.UseAnimGraph = false;

		// Every bound part that carries its own skeleton needs the same preparation - bone objects
		// to write to, and the graph off so it stops fighting those writes. A part with no skinned
		// renderer is a plain object moved as a whole, and needs neither.
		if ( Parts is not null )
		{
			foreach ( var part in Parts )
			{
				if ( part?.Target.IsValid() != true )
					continue;

				// Every skinned renderer under the bound object, not just the first - an object can
				// be several rigged parts, and each one has its own skeleton to write to.
				foreach ( var renderer in part.Target.GetComponentsInChildren<SkinnedModelRenderer>( true ) )
				{
					renderer.CreateBoneObjects = true;

					if ( exclusive )
						renderer.UseAnimGraph = false;
				}
			}
		}

		_rigReady = true;
	}

	private void ApplyFrame( float frame )
	{
		foreach ( var track in Anim.SkeletonTracks )
		{
			if ( track.Keyframes.Count == 0 )
				continue;

			if ( Driven( track ) is not { } driven )
				continue;

			// Limits as the keys are read - the viewport, the export and the headless sim do the same.
			var clip = RigConstraintSolver.ClampToLimits( Anim.RigAsset, track.BoneName, track.Evaluate( frame ) );

			if ( Mode == RigAnimMode.Exclusive )
			{
				driven.Object.LocalTransform = clip;
				_written.Add( driven.Object );
				continue;
			}

			if ( !driven.InMask )
				continue;

			var weight = (Weight * _blend).Clamp( 0f, 1f );
			var anim = UnderLocal( driven );
			_written.Add( driven.Object );

			driven.Object.LocalTransform = Mode == RigAnimMode.Additive
				? AddOnto( anim, driven.Reference, clip, weight )
				: new Transform(
					Vector3.Lerp( anim.Position, clip.Position, weight ),
					Rotation.Slerp( anim.Rotation, clip.Rotation, weight ),
					Vector3.Lerp( anim.Scale, clip.Scale, weight ) );
		}

		if ( Mode == RigAnimMode.Exclusive )
			ApplyLimitedBinds();

		ApplyIk( frame );
		ApplyParts( frame );
		ApplyMorphs( frame );
		ApplyCameras( frame );

		_events?.SetFrame( frame );
	}

	/// <summary>
	/// A limited bone the clip never keys sits at its bind pose, clamped - what the viewport shows and
	/// the export and headless sim bake. Exclusive only: in a layered mode an unkeyed bone is the
	/// graph's, not the clip's.
	/// </summary>
	private void ApplyLimitedBinds()
	{
		if ( Anim.RigAsset?.LimitConstraints is not { Count: > 0 } limits )
			return;

		foreach ( var limit in limits )
		{
			var name = limit?.TargetBone;

			if ( limit is null || !limit.Enabled || string.IsNullOrEmpty( name ) || Anim.FindTrack( name ) is { Keyframes.Count: > 0 } )
				continue;

			if ( !_driven.TryGetValue( name, out var driven ) || (driven is not null && !driven.Object.IsValid()) )
			{
				var go = FindBoneFor( name );
				var renderer = go.IsValid() ? go.GetComponentInParent<SkinnedModelRenderer>( true, false ) : null;

				driven = go.IsValid()
					? new DrivenBone { Object = go, Renderer = renderer, Bone = renderer?.Model?.Bones.GetBone( go.Name ), InMask = true }
					: null;

				_driven[name] = driven;
			}

			if ( driven?.Bone is not { } bone )
				continue;

			if ( !driven.Object.Flags.Contains( GameObjectFlags.ProceduralBone ) )
			{
				driven.Object.Flags |= GameObjectFlags.ProceduralBone;
				driven.Flagged = true;
			}

			// Bone.LocalTransform is MODEL space despite its name; the parent's undoes it.
			var bind = bone.Parent is { } parent ? parent.LocalTransform.ToLocal( bone.LocalTransform ) : bone.LocalTransform;
			driven.Object.LocalTransform = RigConstraintSolver.ClampToLimits( Anim.RigAsset, name, bind );
			_written.Add( driven.Object );
		}
	}

	/// <summary>
	/// Keyed cameras follow their tracks: a dolly, a push-in, a rack to a tighter lens. A camera with
	/// no keys stays where SpawnCameras put it. Still the game's call which one is live - this only
	/// moves them.
	/// </summary>
	private void ApplyCameras( float frame )
	{
		foreach ( var (go, camera) in _cameraShots )
		{
			if ( !go.IsValid() || Anim.FindCameraTrack( camera.Name ) is not { Keyframes.Count: > 0 } )
				continue;

			var (world, fov) = Anim.CameraAt( camera, frame );
			go.LocalPosition = world.Position;
			go.LocalRotation = world.Rotation;

			if ( go.GetComponent<CameraComponent>() is { } cam )
				cam.FieldOfView = fov;
		}
	}

	/// <summary>Each live constraint's chain, resolved once. Null values are remembered too - a
	/// constraint whose bones this model does not have. Cleared on Play().</summary>
	private readonly Dictionary<IkConstraint, IkChain> _ikChains = new();

	/// <summary>
	/// The rig's live IK constraints, solved toward the clip's keyed goals - after the keyframes,
	/// so the chain starts from the pose the clip gives it.
	///
	/// EVERY CHAIN BONE IS PUT BACK FIRST: to its keyframe if the clip keys it (already done by the
	/// loop above), otherwise to its bind pose, or in a layered mode to what the graph has. Solving
	/// from wherever last frame's solve left the arm would make the result depend on its own
	/// history - a longer chain's iterated solve would creep - and a clip has to look the same on
	/// every frame it is played, scrubbed or exported.
	///
	/// A goal is keyed in the model's space by default (see TrackTarget.IkGoal), so it travels with
	/// the character. The clip can plant it instead - see GoalWorld.
	/// </summary>
	private void ApplyIk( float frame )
	{
		if ( Anim.RigAsset?.IkConstraints is not { Count: > 0 } constraints || !Target.IsValid() )
			return;

		var layered = Mode != RigAnimMode.Exclusive;

		foreach ( var ik in constraints )
		{
			if ( ik is null || !ik.Enabled || !ik.Live )
				continue;

			if ( Anim.FindIkGoalTrack( ik.TargetBone ) is not { Keyframes.Count: > 0 } goalTrack )
				continue;

			if ( !_ikChains.TryGetValue( ik, out var chain ) )
				_ikChains[ik] = chain = ResolveChain( ik );

			if ( chain is null || !chain.Objects.All( o => o.IsValid() ) )
				continue;

			if ( layered && !InMask( chain.Objects[^1] ) )
				continue;

			var world = new Transform[chain.Objects.Length];

			for ( var k = 0; k < world.Length; k++ )
			{
				var bone = chain.Objects[k];

				if ( !bone.Flags.Contains( GameObjectFlags.ProceduralBone ) )
				{
					bone.Flags |= GameObjectFlags.ProceduralBone;
					chain.Flagged[k] = true;
				}

				if ( !chain.Keyed[k] )
				{
					bone.LocalTransform = layered
						? UnderLocal( new DrivenBone { Object = bone, Renderer = Target, Bone = chain.Bones[k] } )
						: chain.Bind[k];
				}

				world[k] = bone.WorldTransform;
			}

			if ( GoalWorld( ik.TargetBone, goalTrack.Evaluate( frame ), frame ) is not { } goal )
				continue;

			// The clip's keyed IK weight (TrackTarget.IkWeight) on top: 0 leaves the chain on its keys.
			var weight = ik.Weight * Anim.IkWeightAt( ik.TargetBone, frame ) * (layered ? (Weight * _blend).Clamp( 0f, 1f ) : 1f);

			if ( weight <= 0f )
				continue;

			// Inside the rig's Limit constraints, measured against the top bone's parent.
			var limit = RigConstraintSolver.LimitsFor( Anim.RigAsset, chain.Objects.Select( o => o.Name ).ToArray() );
			var above = chain.Objects[0].Parent is { } top && top.IsValid() ? top.WorldTransform : Target.WorldTransform;

			if ( !RigConstraintSolver.SolveChain( world, goal.Position, ik.GoalRotation ? goal.Rotation : null,
				Target.WorldRotation * ik.PoleDirection, ik.Iterations, weight, ik.Solver, ik.MaxStretch, above, limit ) )
				continue;

			// Top-down, so each bone is written under a parent that is already where it belongs.
			for ( var k = 0; k < world.Length; k++ )
			{
				chain.Objects[k].WorldTransform = world[k];
				_written.Add( chain.Objects[k] );
			}

			// The bones the solve moves (the top one only turns; the end only when the goal turns it).
			for ( var k = 0; k < world.Length - (ik.GoalRotation ? 0 : 1); k++ )
				_ikHeld.Add( chain.Objects[k] );
		}
	}

	/// <summary>
	/// A goal's key in the world, through the space the clip keyed it in (IkGoalSpace):
	///
	///   - Model: where the character is now. The goal travels with it.
	///   - World: where the character was when the clip, or this cycle of it, started - so the hand
	///     stays planted while game code moves the GameObject.
	///   - Object: on the object it follows. Bound in Parts, that is the real object in the scene,
	///     wherever it is - the hand clamps the pipe the level actually has. Unbound, it is where the
	///     clip placed the object around the model, so the clip still plays as authored.
	///
	/// Null when the clip's object is gone and nothing is bound in its place.
	/// </summary>
	private Transform? GoalWorld( string bone, Transform key, float frame )
	{
		var anchor = Anim.FindIkGoalAnchor( bone );

		switch ( anchor?.Space ?? IkGoalSpace.Model )
		{
			case IkGoalSpace.World:
				return (_worldAnchor ?? Target.WorldTransform).ToWorld( key );

			case IkGoalSpace.Object:
				if ( Resolve( anchor.Object ) is { } bound && bound.IsValid() && bound != Target.GameObject )
					return bound.WorldTransform.ToWorld( key );

				return Anim.PlacementAt( anchor.Object, frame ) is { } placed
					? Target.WorldTransform.ToWorld( placed.ToWorld( key ) )
					: null;

			default:
				return Target.WorldTransform.ToWorld( key );
		}
	}

	private IkChain ResolveChain( IkConstraint ik )
	{
		var length = Math.Max( ik.ChainLength, 1 );
		var end = FindBone( Target.GameObject, ik.TargetBone );

		if ( !end.IsValid() )
			return null;

		var chain = new IkChain
		{
			Objects = new GameObject[length + 1],
			Bones = new BoneCollection.Bone[length + 1],
			Bind = new Transform[length + 1],
			Keyed = new bool[length + 1],
			Flagged = new bool[length + 1],
		};

		var go = end;

		for ( var k = length; k >= 0; k-- )
		{
			if ( !go.IsValid() || !IsBone( go ) )
				return null;

			var bone = Target.Model?.Bones.GetBone( go.Name );

			chain.Objects[k] = go;
			chain.Bones[k] = bone;
			chain.Keyed[k] = Anim.FindTrack( go.Name ) is { Keyframes.Count: > 0 };

			// Bone.LocalTransform is MODEL space despite its name; the parent's undoes it.
			chain.Bind[k] = bone is null ? go.LocalTransform
				: bone.Parent is { } parent ? parent.LocalTransform.ToLocal( bone.LocalTransform )
				: bone.LocalTransform;

			go = go.Parent;
		}

		return chain;
	}

	private sealed class IkChain
	{
		/// <summary>Top bone first, the constraint's end last.</summary>
		public GameObject[] Objects;
		public BoneCollection.Bone[] Bones;
		public Transform[] Bind;
		public bool[] Keyed;
		public bool[] Flagged;
	}

	/// <summary>The morphs this player has an override on right now, so a morph whose event has
	/// ended is handed back - and only ours are, never one the game set itself.</summary>
	private readonly HashSet<string> _morphsDriven = new();
	private readonly Dictionary<string, float> _morphScratch = new();

	/// <summary>
	/// The clip's morph events, on the same clock as the bones.
	///
	/// These were authored in the clip and saved with it, and then nothing ever read them: an
	/// "eat" morph keyed over frames 10-30 never moved a vertex in game. A morph is held at its
	/// value for its frame range and released outside it, so the face goes back to whatever the
	/// game (or a viseme) had it at.
	///
	/// Overlay and Additive scale by the fade like everything else, so a layered clip's mouth
	/// eases in with its arms instead of snapping.
	/// </summary>
	private void ApplyMorphs( float frame )
	{
		if ( !Target.IsValid() )
			return;

		Anim.MorphWeightsAt( frame, _morphScratch );

		var weight = Mode == RigAnimMode.Exclusive ? 1f : (Weight * _blend).Clamp( 0f, 1f );

		foreach ( var (name, value) in _morphScratch )
		{
			Target.Morphs.Set( name, value * weight );
			_morphsDriven.Add( name );
		}

		_morphsDriven.RemoveWhere( name =>
		{
			if ( _morphScratch.ContainsKey( name ) )
				return false;

			Target.Morphs.Clear( name );
			return true;
		} );
	}

	/// <summary>Drop every morph override this player set.</summary>
	private void ReleaseMorphs()
	{
		if ( Target.IsValid() )
		{
			foreach ( var name in _morphsDriven )
				Target.Morphs.Clear( name );
		}

		_morphsDriven.Clear();
	}

	/// <summary>
	/// Additive: the clip's motion AWAY FROM ITS FIRST FRAME, laid on top of whatever the graph
	/// is doing. So author an additive clip starting from a rest pose - the recoil's frame 0 is
	/// "nothing has happened yet" - and every later key is read as a nudge from there.
	///
	/// Rotation composes in the bone's own frame, so a spine kick bends the spine the same way
	/// whether he is standing or crouched. Position adds as a plain offset. Scale multiplies, so a
	/// keyed 4 % swell swells whatever is under it (it used to be dropped). RigClipLayer.Compose.
	/// </summary>
	private static Transform AddOnto( Transform anim, Transform reference, Transform clip, float weight ) =>
		RigClipLayer.Compose( anim, reference, clip, weight, additive: true );

	/// <summary>The pose this player blends from: what the player it layers over wrote this frame,
	/// when it wrote this bone (see LayerOver); otherwise the animgraph's, as before.</summary>
	private Transform UnderLocal( DrivenBone driven ) =>
		Under is { } under && under.Wrote( driven.Object ) ? driven.Object.LocalTransform : AnimatedLocal( driven );

	/// <summary>
	/// What the graph wants this bone to be, in its parent's space - the pose the clip blends
	/// away from.
	///
	/// ONE FRAME BEHIND. Components update before the renderer animates, so this is the graph's
	/// pose from the frame just gone. At full weight in Overlay that is invisible, because the
	/// clip ignores it; during a fade, or in Additive, it is a sixtieth of a second of lag on the
	/// bones the clip owns, against a body a sixtieth of a second ahead.
	///
	/// When there is no animated pose to read - the graph has not ticked yet - the bone's current
	/// transform stands in, which at worst means the first frame fades from wherever it was.
	/// </summary>
	private static Transform AnimatedLocal( DrivenBone driven )
	{
		var renderer = driven.Renderer;

		if ( !renderer.IsValid() || driven.Bone is null || !renderer.TryGetBoneTransformAnimation( driven.Bone, out var world ) )
			return driven.Object.LocalTransform;

		var parent = driven.Bone.Parent;

		if ( parent is null )
			return renderer.WorldTransform.ToLocal( world );

		return renderer.TryGetBoneTransformAnimation( parent, out var parentWorld )
			? parentWorld.ToLocal( world )
			: driven.Object.LocalTransform;
	}

	/// <summary>
	/// The bone a skeleton track drives, resolved and flagged on first use.
	///
	/// FLAGGED PROCEDURALBONE, which is the whole mechanism: the renderer reads a flagged bone's
	/// local transform INTO the pose and leaves every other bone to the graph. That per-bone
	/// switch is what lets a clip own an arm while the legs keep walking. Only bones this player
	/// flagged are remembered as its own, so releasing never unflags somebody else's.
	/// </summary>
	private DrivenBone Driven( BoneTrack track )
	{
		// A track name says which object as well as which bone - see RigTrackName. Bare names are
		// the main model's, which is every bone track written before clips could hold more than
		// one object.
		if ( !_driven.TryGetValue( track.BoneName, out var driven ) || (driven is not null && !driven.Object.IsValid()) )
		{
			driven = Resolve( track );
			_driven[track.BoneName] = driven;
		}

		if ( driven is null )
			return null;

		if ( Mode != RigAnimMode.Exclusive && !driven.InMask )
			return driven;

		if ( !driven.Object.Flags.Contains( GameObjectFlags.ProceduralBone ) )
		{
			driven.Object.Flags |= GameObjectFlags.ProceduralBone;
			driven.Flagged = true;
		}

		return driven;
	}

	private DrivenBone Resolve( BoneTrack track )
	{
		var bone = FindBoneFor( track.BoneName );

		if ( !bone.IsValid() )
			return null;

		var renderer = bone.GetComponentInParent<SkinnedModelRenderer>( true, false );
		var first = track.Keyframes.Count > 0 ? track.Keyframes.Min( k => k.Frame ) : 0f;

		return new DrivenBone
		{
			Object = bone,
			Renderer = renderer,
			Bone = renderer?.Model?.Bones.GetBone( bone.Name ),
			Reference = track.Evaluate( first ),
			InMask = InMask( bone ),
		};
	}

	/// <summary>The bone or anything above it is named in Mask. An empty mask takes everything.</summary>
	private bool InMask( GameObject bone )
	{
		if ( Mask is null || Mask.Count == 0 )
			return true;

		for ( var go = bone; go.IsValid() && IsBone( go ); go = go.Parent )
		{
			if ( Mask.Contains( go.Name ) )
				return true;
		}

		return false;
	}

	/// <summary>Give every bone this player flagged back to the graph.</summary>
	private void ReleaseBones()
	{
		ReleaseMorphs();
		_soft.Release();

		foreach ( var chain in _ikChains.Values )
		{
			if ( chain is null )
				continue;

			for ( var k = 0; k < chain.Objects.Length; k++ )
			{
				if ( chain.Flagged[k] && chain.Objects[k].IsValid() )
					chain.Objects[k].Flags &= ~GameObjectFlags.ProceduralBone;

				chain.Flagged[k] = false;
			}
		}

		foreach ( var driven in _driven.Values )
		{
			if ( driven is null || !driven.Flagged )
				continue;

			if ( driven.Object.IsValid() )
				driven.Object.Flags &= ~GameObjectFlags.ProceduralBone;

			driven.Flagged = false;
		}
	}

	private static bool IsBone( GameObject go ) =>
		go.Flags.Contains( GameObjectFlags.Bone ) || go.Flags.Contains( GameObjectFlags.ProceduralBone );

	private sealed class DrivenBone
	{
		public GameObject Object;
		public SkinnedModelRenderer Renderer;
		public BoneCollection.Bone Bone;

		/// <summary>The clip's first keyed pose, which Additive measures every later key against.</summary>
		public Transform Reference;

		public bool InMask;

		/// <summary>This player set the ProceduralBone flag, so this player takes it off.</summary>
		public bool Flagged;
	}

	/// <summary>
	/// Drives the whole-part tracks - the door, the lever, the magazine - from the same clip and
	/// the same clock as the bones.
	///
	/// The clip stores a part by NAME, because the editor's props are a preview and the real
	/// object at runtime is whatever the scene happens to have built. Parts is that hookup: name
	/// on the left, the GameObject it means on the right. An unbound name is simply not driven,
	/// so a clip that animates three parts still plays its bones in a scene that only wired one.
	/// </summary>
	private void ApplyParts( float frame )
	{
		if ( Parts is null || Parts.Count == 0 )
			return;

		foreach ( var track in Anim.PartTracks )
		{
			if ( track.Keyframes.Count == 0 )
				continue;

			// A part that follows another is placed below, through its whole chain - its keys are
			// relative to the part it follows, which the scene's hierarchy need not mirror.
			var (owner, part) = Anim.FindPartTarget( track.BoneName );

			if ( part is not null && owner.ParentOf( part ) is not null )
				continue;

			var target = Resolve( track.BoneName );

			if ( !target.IsValid() )
				continue;

			// LOCAL, ALWAYS. A part is keyed inside the object it belongs to, so driving its local
			// transform is what keeps the handle on the door while the door swings.
			var local = track.Evaluate( frame );

			target.LocalPosition = local.Position;
			target.LocalRotation = local.Rotation;
			target.LocalScale = local.Scale;
		}

		ApplyFollowers( frame );
	}

	/// <summary>
	/// Parts that follow another part - the eyes following the head.
	///
	/// PLACED IN THE WORLD, THROUGH THE OBJECT, rather than as a local transform. The clip stores
	/// the eyes relative to the head, but the scene may have them under the head or beside it under
	/// the object; composing the chain and writing a world transform puts them in the same place
	/// either way.
	///
	/// Driven whenever anything in its chain is keyed, not only when it is - eyes with no keys of
	/// their own still have to follow a head that has some. A chain with no keys anywhere is left
	/// where the scene put it, the same rule every other part plays by.
	/// </summary>
	private void ApplyFollowers( float frame )
	{
		if ( Anim.Objects is null )
			return;

		foreach ( var owner in Anim.Objects )
		{
			if ( owner?.Parts is null )
				continue;

			GameObject ownerObject = null;

			foreach ( var part in owner.Parts )
			{
				if ( part is null || owner.ParentOf( part ) is null || !ChainKeyed( owner, part ) )
					continue;

				var target = Resolve( RigTrackName.Qualify( owner.Name, part.Name ) );

				if ( !target.IsValid() )
					continue;

				ownerObject ??= Resolve( owner.Name );

				if ( !ownerObject.IsValid() )
					continue;

				target.WorldTransform = ownerObject.WorldTransform.ToWorld( Anim.PartInObject( owner, part, frame ) );
			}
		}
	}

	private bool ChainKeyed( RigObject owner, RigObjectPart part )
	{
		for ( var p = part; p is not null; p = owner.ParentOf( p ) )
		{
			if ( Anim.FindPartTrack( RigTrackName.Qualify( owner.Name, p.Name ) ) is { Keyframes.Count: > 0 } )
				return true;
		}

		return false;
	}

	/// <summary>
	/// The object a track name means: a bound object, or one of its parts by name underneath it.
	///
	/// An unbound name has no answer, which is how a clip still plays everything else in a scene
	/// that only wired some of it up.
	/// </summary>
	private GameObject Resolve( string key )
	{
		if ( string.IsNullOrEmpty( key ) )
			return Target.IsValid() ? Target.GameObject : null;

		if ( FindPart( key ) is { } exact )
			return exact.Target;

		// "door/handle" - the object is bound, the part is a child of it named by the exporter.
		// Walked segment by segment so an object nested deeper than one level still resolves.
		var segments = key.Split( RigTrackName.Separator );

		for ( var split = segments.Length - 1; split >= 1; split-- )
		{
			var ownerName = string.Join( RigTrackName.Separator, segments, 0, split );

			if ( FindPart( ownerName )?.Target is not { } owner || !owner.IsValid() )
				continue;

			var found = owner;

			for ( var i = split; i < segments.Length && found.IsValid(); i++ )
				found = FindChild( found, segments[i] );

			if ( found.IsValid() )
				return found;
		}

		return null;
	}

	/// <summary>Anywhere underneath, nearest first - a part that follows another may well be parented
	/// under it in the scene rather than directly under the object. Bones are skipped: they are
	/// found by FindBone, and a bone that shares a part's name must not be moved as the part.</summary>
	private static GameObject FindChild( GameObject parent, string name )
	{
		var queue = new Queue<GameObject>();
		queue.Enqueue( parent );

		while ( queue.Count > 0 )
		{
			foreach ( var child in queue.Dequeue().Children )
			{
				if ( child.Name == name && !IsBone( child ) )
					return child;

				queue.Enqueue( child );
			}
		}

		return null;
	}

	/// <summary>
	/// The bone object a bone track means, wherever it lives.
	///
	/// The name is an object and a bone joined by a slash, and either half can itself contain one -
	/// "door/handle/screw" is a bone in a part of an object. So the split is tried from the right,
	/// longest object first, and the whole name against the main model last. That final fallback is
	/// also what keeps a bone whose OWN name contains a slash working: nothing claims a prefix of
	/// it, so it arrives at the main model intact.
	/// </summary>
	private GameObject FindBoneFor( string key )
	{
		var segments = key.Split( RigTrackName.Separator );

		for ( var split = segments.Length - 1; split >= 1; split-- )
		{
			var ownerName = string.Join( RigTrackName.Separator, segments, 0, split );
			var boneName = string.Join( RigTrackName.Separator, segments, split, segments.Length - split );

			if ( Resolve( ownerName ) is not { } owner || !owner.IsValid() )
				continue;

			if ( FindBone( owner, boneName ) is { } found && found.IsValid() )
				return found;
		}

		return FindBone( Target.IsValid() ? Target.GameObject : null, key );
	}

	private AnimPartBinding FindPart( string name )
	{
		foreach ( var part in Parts )
		{
			if ( part is not null && part.Name == name )
				return part;
		}

		return null;
	}

	private GameObject FindBone( GameObject root, string name )
	{
		if ( !root.IsValid() )
			return null;

		var queue = new Queue<GameObject>();
		queue.Enqueue( root );

		while ( queue.Count > 0 )
		{
			var go = queue.Dequeue();

			foreach ( var child in go.Children )
			{
				if ( child.Name == name && IsBone( child ) )
					return child;

				queue.Enqueue( child );
			}
		}

		return null;
	}
}

/// <summary>One whole-part track wired to the object it moves. See RigAnimPlayerComponent.Parts.</summary>
public sealed class AnimPartBinding
{
	/// <summary>The part's name in the clip - the same name the timeline row shows.</summary>
	[Property] public string Name { get; set; } = "";

	[Property] public GameObject Target { get; set; }
}

/// <summary>How a RigAnimPlayerComponent shares the body with the animgraph.</summary>
public enum RigAnimMode
{
	/// <summary>The graph goes off and the clip owns every bone it keys, holding the last frame at
	/// the end. Cutscenes, sitting down, anything the whole body does.</summary>
	Exclusive,

	/// <summary>The graph keeps running; the clip replaces the bones it keys (narrowed by Mask),
	/// faded by Weight. A reload or a wave while he walks.</summary>
	Overlay,

	/// <summary>The graph keeps running; the clip's motion away from its first frame is added on
	/// top. Recoil, breathing, a flinch.</summary>
	Additive,
}
