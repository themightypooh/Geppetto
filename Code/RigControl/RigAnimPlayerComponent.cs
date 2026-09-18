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
public sealed class RigAnimPlayerComponent : Component
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
	/// Which GameObject each of the clip's whole-part tracks drives.
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

	public bool IsPlaying { get; private set; }
	public float Frame { get; private set; }

	/// <summary>Fires once when a non-looping clip hits its last keyed frame.</summary>
	public Action Finished { get; set; }

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
		}
	}

	/// <summary>Take them down again, only our own. A camera this component did not create is
	/// somebody else's, and disabling a clip player must not pull the level's camera out.</summary>
	private void DespawnCameras()
	{
		foreach ( var camera in _cameras )
			camera?.Destroy();

		_cameras.Clear();
	}

	protected override void OnUpdate()
	{
		if ( !Target.IsValid() || Anim is null )
			return;

		EnsureRig();

		if ( IsPlaying )
		{
			Frame += Time.Delta * FrameRate;

			if ( Frame > LastFrame )
			{
				if ( Loop )
				{
					Frame %= MathF.Max( LastFrame, 0.0001f );
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
			return;
		}

		var fade = _active ? BlendIn : BlendOut;
		_blend = fade <= 0f
			? (_active ? 1f : 0f)
			: _blend.Approach( _active ? 1f : 0f, Time.Delta / fade );

		if ( !_active && _blend <= 0f )
		{
			ReleaseBones();
			return;
		}

		ApplyFrame( Frame );
	}

	/// <summary>Starts the clip. A finished one-shot restarts from frame 0 — that's what "open
	/// the fridge again" has to mean. Pause() then Play() resumes if you have not hit the end.</summary>
	public void Play()
	{
		if ( Frame >= LastFrame || (Mode != RigAnimMode.Exclusive && !_active && _blend <= 0f) )
			Frame = 0f;

		// Resolve afresh: Parts may have been bound, or the model swapped, since last time.
		ReleaseBones();
		_driven.Clear();

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
		ApplyFrame( Frame );
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

			var clip = track.Evaluate( frame );

			if ( Mode == RigAnimMode.Exclusive )
			{
				driven.Object.LocalTransform = clip;
				continue;
			}

			if ( !driven.InMask )
				continue;

			var weight = (Weight * _blend).Clamp( 0f, 1f );
			var anim = AnimatedLocal( driven );

			driven.Object.LocalTransform = Mode == RigAnimMode.Additive
				? AddOnto( anim, driven.Reference, clip, weight )
				: new Transform(
					Vector3.Lerp( anim.Position, clip.Position, weight ),
					Rotation.Slerp( anim.Rotation, clip.Rotation, weight ),
					Vector3.Lerp( anim.Scale, clip.Scale, weight ) );
		}

		ApplyParts( frame );

		_events?.SetFrame( frame );
	}

	/// <summary>
	/// Additive: the clip's motion AWAY FROM ITS FIRST FRAME, laid on top of whatever the graph
	/// is doing. So author an additive clip starting from a rest pose - the recoil's frame 0 is
	/// "nothing has happened yet" - and every later key is read as a nudge from there.
	///
	/// Rotation composes in the bone's own frame, so a spine kick bends the spine the same way
	/// whether he is standing or crouched. Position adds as a plain offset. Scale is the graph's.
	/// </summary>
	private static Transform AddOnto( Transform anim, Transform reference, Transform clip, float weight )
	{
		var delta = Rotation.Slerp( Rotation.Identity, reference.Rotation.Inverse * clip.Rotation, weight );

		return new Transform(
			anim.Position + (clip.Position - reference.Position) * weight,
			anim.Rotation * delta,
			anim.Scale );
	}

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
