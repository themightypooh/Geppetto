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

	public bool IsPlaying { get; private set; }
	public float Frame { get; private set; }

	/// <summary>Fires once when a non-looping clip hits its last keyed frame.</summary>
	public Action Finished { get; set; }

	private RigEventPlayerComponent _events;
	private bool _rigReady;
	private bool _notifiedFinish;

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

		if ( PlayOnStart )
			Play();
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
		_rigReady = true;
	}

	private void ApplyFrame( float frame )
	{
		foreach ( var track in Anim.BoneTracks )
		{
			if ( track.Keyframes.Count == 0 )
				continue;

			var bone = FindBone( track.BoneName );
			if ( !bone.IsValid() )
				continue;

			var local = track.Evaluate( frame );
			bone.LocalPosition = local.Position;
			bone.LocalRotation = local.Rotation;
			bone.LocalScale = local.Scale;
		}

		_events?.SetFrame( frame );
	}

	private GameObject FindBone( string name )
	{
		if ( !Target.IsValid() )
			return null;

		var queue = new Queue<GameObject>();
		queue.Enqueue( Target.GameObject );

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
