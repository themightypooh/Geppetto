using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette;

/// <summary>
/// Plays a RigAnimDocument back on a live in-game SkinnedModelRenderer - the runtime half of the
/// RigControlEditor recorder. The editor tool only ever plays a clip back inside its own preview
/// scene; this is what makes a recorded clip actually visible in the real game.
///
/// Posing works the same way the editor viewport's own EvaluatePose does - direct
/// LocalPosition/LocalRotation writes on the per-bone GameObjects SkinnedModelRenderer.
/// CreateBoneObjects spawns - except BreakProceduralBone() isn't called, because that extension
/// method lives in the editor-only tools addon and can't be referenced from game code. UseAnimGraph
/// = false is what actually stops anything else from fighting these writes at runtime; nothing
/// else in this project drives the ProceduralBone flag once that's off, so there's nothing to
/// break free from the way there is in the scene editor.
///
/// Also forwards the current frame to a sibling RigEventPlayerComponent if one is on the same
/// GameObject, so a single Anim assignment drives both bone poses and AnimEvents together.
/// </summary>
public sealed class RigAnimPlayerComponent : Component
{
	[Property] public RigAnimDocument Anim { get; set; }
	[Property] public SkinnedModelRenderer Target { get; set; }
	[Property] public bool Loop { get; set; } = true;
	[Property] public bool PlayOnStart { get; set; } = true;

	public bool IsPlaying { get; private set; }
	public float Frame { get; private set; }

	private RigEventPlayerComponent _events;
	private bool _rigReady;

	private float LastFrame => Anim is null ? 0f : MathF.Max( Anim.FrameCount - 1, 1f );
	private float FrameRate => Anim is { AnimationSpeed: > 0 } ? Anim.AnimationSpeed : 30f;

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
					Frame %= MathF.Max( LastFrame, 0.0001f );
				else
				{
					Frame = LastFrame;
					IsPlaying = false;
				}
			}
		}

		ApplyFrame( Frame );
	}

	public void Play() => IsPlaying = true;
	public void Pause() => IsPlaying = false;

	public void Stop()
	{
		IsPlaying = false;
		Frame = 0f;
	}

	public void Seek( float frame )
	{
		Frame = frame.Clamp( 0f, LastFrame );
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
