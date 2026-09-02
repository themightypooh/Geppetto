using Sandbox;
using System.Collections.Generic;

namespace Marionette;

/// <summary>
/// Reads a RigAnimDocument's Events against this GameObject's SkinnedModelRenderer and spawns/
/// attaches each event's prop while playback is inside its [StartFrame, EndFrame] window.
///
/// There's no confirmed public API for reading "what frame is AnimGraph currently on" from
/// outside it, so this doesn't try to guess the frame itself - whatever drives the animation
/// (an AnimGraph event, a state machine callback, a cutscene timeline) calls SetFrame each tick
/// with the frame it already knows it's on. That's also why Side is a spawn-authority gate
/// rather than full replication: Server events only spawn on the host (so they're the ones that
/// should exist for everyone / be hittable), Client events spawn locally on whichever realm
/// calls SetFrame (cheap cosmetic holds, no networking overhead).
/// </summary>
public sealed class RigEventPlayerComponent : Component
{
	[Property] public RigAnimDocument Anim { get; set; }
	[Property] public SkinnedModelRenderer Target { get; set; }

	private readonly Dictionary<RigEvent, GameObject> _active = new();
	private float _frame;

	protected override void OnEnabled()
	{
		Target ??= GetComponent<SkinnedModelRenderer>();
	}

	protected override void OnDisabled()
	{
		ClearAll();
	}

	/// <summary>Call every tick with the current playback frame, however the caller knows it.</summary>
	public void SetFrame( float frame )
	{
		_frame = frame;

		if ( Anim is null || !Target.IsValid() )
			return;

		foreach ( var evt in Anim.Events )
		{
			var isActive = evt.IsActive( frame );
			var isSpawned = _active.ContainsKey( evt );

			if ( isActive && !isSpawned )
				Spawn( evt );
			else if ( !isActive && isSpawned )
				Despawn( evt );
			else if ( isActive && isSpawned && evt.FollowBone )
				UpdateFollow( evt );
		}
	}

	private bool HasAuthority( RigEvent evt ) => evt.Side switch
	{
		RigEventSide.Server => Networking.IsHost,
		_ => true,
	};

	private void Spawn( RigEvent evt )
	{
		if ( evt.PropModel is null || string.IsNullOrWhiteSpace( evt.AttachBone ) )
			return;

		if ( !HasAuthority( evt ) )
			return;

		if ( !Target.TryGetBoneTransform( evt.AttachBone, out var boneTransform ) )
			return;

		var prop = new GameObject( true, evt.Name )
		{
			Parent = GameObject,
			WorldTransform = boneTransform.ToWorld( evt.BoneOffsetTransform ),
		};

		var renderer = prop.GetOrAddComponent<SkinnedModelRenderer>( false );
		renderer.Model = evt.PropModel;
		renderer.Enabled = true;

		_active[evt] = prop;
	}

	private void UpdateFollow( RigEvent evt )
	{
		if ( !_active.TryGetValue( evt, out var prop ) || !prop.IsValid() )
			return;

		if ( !Target.TryGetBoneTransform( evt.AttachBone, out var boneTransform ) )
			return;

		prop.WorldTransform = boneTransform.ToWorld( evt.BoneOffsetTransform );
	}

	private void Despawn( RigEvent evt )
	{
		if ( _active.Remove( evt, out var prop ) && prop.IsValid() )
			prop.Destroy();
	}

	private void ClearAll()
	{
		foreach ( var prop in _active.Values )
		{
			if ( prop.IsValid() )
				prop.Destroy();
		}

		_active.Clear();
	}
}
