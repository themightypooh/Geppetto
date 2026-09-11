using Sandbox;
using System;
using System.Collections.Generic;

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
/// Posing writes LocalPosition/LocalRotation on the procedural bone objects. UseAnimGraph = false
/// stops the graph fighting those writes. A sibling RigEventPlayerComponent, if present, gets
/// the same frame so attached props stay in sync.
/// </summary>
public sealed class RigAnimPlayerComponent : Component
{
	[Property] public RigAnimDocument Anim { get; set; }
	[Property] public SkinnedModelRenderer Target { get; set; }

	/// <summary>Idle loops. A fridge-open does not — leave this off and call Play() on use.</summary>
	[Property] public bool Loop { get; set; } = true;

	/// <summary>Off for interaction clips. On would play the grab the moment the pawn spawns.</summary>
	[Property] public bool PlayOnStart { get; set; } = true;

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

					if ( !_notifiedFinish )
					{
						_notifiedFinish = true;
						Finished?.Invoke();
					}
				}
			}
		}

		ApplyFrame( Frame );
	}

	/// <summary>Starts the clip. A finished one-shot restarts from frame 0 — that's what "open
	/// the fridge again" has to mean. Pause() then Play() resumes if you have not hit the end.</summary>
	public void Play()
	{
		if ( Frame >= LastFrame )
			Frame = 0f;

		_notifiedFinish = false;
		IsPlaying = true;
	}

	public void Pause() => IsPlaying = false;

	public void Stop()
	{
		IsPlaying = false;
		Frame = 0f;
		_notifiedFinish = false;
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

		Target.CreateBoneObjects = true;
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

			// A track name says which object as well as which bone - see RigTrackName. Bare names
			// are the main model's, which is every bone track written before clips could hold more
			// than one object.
			var bone = FindBoneFor( track.BoneName );

			if ( !bone.IsValid() )
				continue;

			var local = track.Evaluate( frame );
			bone.LocalPosition = local.Position;
			bone.LocalRotation = local.Rotation;
			bone.LocalScale = local.Scale;
		}

		ApplyParts( frame );

		_events?.SetFrame( frame );
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
				if ( child.Name == name && !child.Flags.HasFlag( GameObjectFlags.ProceduralBone ) )
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
				if ( child.Name == name && child.Flags.HasFlag( GameObjectFlags.ProceduralBone ) )
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
