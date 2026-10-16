using Editor;
using Effigy;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// What the viewport draws ABOUT the clip rather than of it: onion skin, the motion trail, and
/// keyed cameras at the playhead - plus the one pose operation that needs the whole skeleton at
/// once, mirroring.
///
/// All of it is computed from the document, never read back off the renderer. A ghost three
/// frames ahead has no renderer to read; and the live pose lags a tick behind every write (see
/// ApplyWorldTransform), so anything measured off it would be measured off last frame.
/// </summary>
internal sealed partial class RigViewport
{
	private const string OnionCookie = "marionette.onion";
	private const string TrailCookie = "marionette.trail";

	/// <summary>Frames between onion-skin ghosts.</summary>
	public int OnionStep { get; set; } = 4;

	/// <summary>Ghosts each side of the playhead.</summary>
	public int OnionCount { get; set; } = 2;

	/// <summary>Frames either side of the playhead the motion trail covers.</summary>
	public int TrailFrames { get; set; } = 30;

	private bool _showOnion;
	private bool _showTrail;

	private RigAnimDocument _overlayAnim;
	private float _overlayFrame;
	private bool _overlayDirty = true;

	/// <summary>Each ghost: its offset from the playhead (negative is the past), and every main-model
	/// bone's world transform at that frame.</summary>
	private readonly List<(float Offset, Dictionary<string, Transform> World)> _ghosts = new();

	private readonly List<Vector3> _trail = new();
	private readonly List<Vector3> _trailKeys = new();
	private string _trailBone;
	private int _trailStart;

	/// <summary>Keyed cameras' placement at the playhead. A camera with no keys is not in here and
	/// draws where its fields put it.</summary>
	private readonly Dictionary<RigCamera, (Transform World, float FieldOfView)> _cameraFrame = new();

	public bool ShowOnionSkin
	{
		get => _showOnion;
		set
		{
			_showOnion = value;
			_overlayDirty = true;
			EditorCookie.Set( OnionCookie, value );
		}
	}

	public bool ShowMotionTrail
	{
		get => _showTrail;
		set
		{
			_showTrail = value;
			_overlayDirty = true;
			EditorCookie.Set( TrailCookie, value );
		}
	}

	private void BuildOverlayToggles( Layout bar )
	{
		_showOnion = EditorCookie.Get( OnionCookie, false );
		_showTrail = EditorCookie.Get( TrailCookie, false );

		bar.AddSpacingCell( 12 );

		var onion = new Checkbox( "Onion Skin" )
		{
			Value = _showOnion,
			ToolTip = $"Ghost skeletons of the pose {OnionStep} and {OnionStep * 2} frames either side of the playhead - red behind, green ahead - so spacing reads without scrubbing."
		};
		onion.Toggled = () => ShowOnionSkin = onion.Value;
		bar.Add( onion );

		var trail = new Checkbox( "Motion Trail" )
		{
			Value = _showTrail,
			ToolTip = $"The path the selected bone travels over {TrailFrames} frames either side of the playhead. Dots are its keys; the gaps between frames show its speed."
		};
		trail.Toggled = () => ShowMotionTrail = trail.Value;
		bar.Add( trail );
	}

	/// <summary>The clip and playhead the overlays are drawn for. Called on every scrub; cheap
	/// when both overlays are off.</summary>
	public void UpdateOnionSkin( RigAnimDocument anim, float frame )
	{
		_overlayAnim = anim;
		_overlayFrame = frame;
		_overlayDirty = true;
	}

	/// <summary>The clip changed under the overlays - a key moved, a tangent bent. Recomputed on
	/// the next frame drawn, not now, so a drag that edits every frame pays once per frame.</summary>
	public void InvalidateOverlays() => _overlayDirty = true;

	/// <summary>What the overlays currently hold, for the MCP self-check: ghosts drawn, trail
	/// points, keyed cameras placed.</summary>
	internal (int Ghosts, int TrailPoints, int TrailKeys, int KeyedCameras) OverlayCounts()
	{
		RefreshOverlays();
		return (_ghosts.Count, _trail.Count, _trailKeys.Count, _cameraFrame.Count);
	}

	/// <summary>The viewport's own camera, for rendering what it sees to a bitmap.</summary>
	internal CameraComponent ViewCameraComponent => _camera;

	private void RefreshOverlays()
	{
		if ( !_overlayDirty && _trailBone == (_showTrail ? SelectedBone : null) )
			return;

		_overlayDirty = false;
		_ghosts.Clear();
		_trail.Clear();
		_trailKeys.Clear();
		_trailBone = _showTrail ? SelectedBone : null;

		if ( _overlayAnim is null || !_renderer.IsValid() )
			return;

		var last = LastKeyedFrame( _overlayAnim );

		if ( _showOnion )
		{
			for ( var i = -OnionCount; i <= OnionCount; i++ )
			{
				if ( i == 0 )
					continue;

				var at = _overlayFrame + i * OnionStep;

				if ( at < 0f || at > last )
					continue;

				_ghosts.Add( (i * OnionStep, MainPoseAt( _overlayAnim, at )) );
			}
		}

		// The main model's bones only: a prop's bone has no track of its own to trail.
		if ( _trailBone is { } bone && FindBone( bone ) is { Subject: RigTrackName.RootSubject } )
		{
			var from = Math.Max( 0, (int)MathF.Floor( _overlayFrame ) - TrailFrames );
			var to = Math.Min( (int)last, (int)MathF.Ceiling( _overlayFrame ) + TrailFrames );
			_trailStart = from;

			for ( var f = from; f <= to; f++ )
			{
				if ( MainPoseAt( _overlayAnim, f ).TryGetValue( bone, out var world ) )
					_trail.Add( world.Position );
			}

			if ( _overlayAnim.FindTrack( bone ) is { } track )
			{
				foreach ( var key in track.Keyframes.Where( k => k.Frame >= from && k.Frame <= to ) )
				{
					var index = key.Frame - from;

					if ( index >= 0 && index < _trail.Count )
						_trailKeys.Add( _trail[index] );
				}
			}
		}
	}

	private static float LastKeyedFrame( RigAnimDocument anim )
	{
		var last = 0;

		foreach ( var track in anim.BoneTracks )
		{
			foreach ( var key in track.Keyframes )
				last = Math.Max( last, key.Frame );
		}

		return last;
	}

	/// <summary>
	/// Every main-model bone's world transform at <paramref name="frame"/>, the way a scrub would
	/// place it: keyframe or bind pose, limits, then live IK. The same steps as EvaluatePose,
	/// without writing anything, so a ghost is exactly where the arm will be.
	/// </summary>
	private Dictionary<string, Transform> MainPoseAt( RigAnimDocument anim, float frame )
	{
		var world = new Dictionary<string, Transform>();

		if ( _renderer.Model?.Bones is not { } bones )
			return world;

		Transform? Pose( string bone ) => anim.FindTrack( bone ) is { Keyframes.Count: > 0 } track ? track.Evaluate( frame ) : null;
		Transform? Goal( string bone ) => anim.IkGoalAt( bone, frame, _renderer.WorldTransform );

		foreach ( var bone in bones.AllBones )
		{
			var rig = new RigBone { Subject = RigTrackName.RootSubject, Renderer = _renderer, Bone = bone, Radius = _boneHandleRadius };
			var local = RigConstraintSolver.ClampToLimits( Rig, bone.Name, Pose( bone.Name ) ?? BindPoseFor( rig ) );
			var parent = bone.Parent is { } p && world.TryGetValue( p.Name, out var pw ) ? pw : _renderer.WorldTransform;

			world[bone.Name] = parent.ToWorld( local );
		}

		SolveLiveIk( Goal, world, Pose );

		return world;
	}

	private void DrawOnionSkin()
	{
		if ( !_showOnion )
			return;

		RefreshOverlays();

		if ( _ghosts.Count == 0 || _renderer.Model?.Bones is not { } bones )
			return;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1.5f;

		foreach ( var (offset, world) in _ghosts )
		{
			// Nearer ghosts are stronger; the past is warm and the future cool, the convention every
			// onion skin since paper has used.
			var strength = 1f - (MathF.Abs( offset ) / MathF.Max( OnionStep * (OnionCount + 1), 1f ));
			Gizmo.Draw.Color = (offset < 0 ? Theme.Red : Theme.Green).WithAlpha( 0.25f + 0.45f * strength );

			foreach ( var bone in bones.AllBones )
			{
				if ( bone.Parent is not { } parent || IsHidden( bone.Name ) || (!ShowTwistBones && IsTwistBone( bone.Name )) )
					continue;

				if ( world.TryGetValue( bone.Name, out var a ) && world.TryGetValue( parent.Name, out var b ) )
					Gizmo.Draw.Line( b.Position, a.Position );
			}
		}

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
	}

	private void DrawMotionTrail()
	{
		if ( !_showTrail )
			return;

		RefreshOverlays();

		if ( _trail.Count < 2 )
			return;

		Gizmo.Draw.IgnoreDepth = true;

		var now = (int)MathF.Round( _overlayFrame ) - _trailStart;

		for ( var i = 1; i < _trail.Count; i++ )
		{
			// Behind the playhead warm, ahead cool - the same reading as the onion skin.
			Gizmo.Draw.Color = (i <= now ? Theme.Red : Theme.Green).WithAlpha( 0.8f );
			Gizmo.Draw.Line( _trail[i - 1], _trail[i] );
		}

		// A tick per frame, so spacing - which is speed - can be read off the path itself.
		Gizmo.Draw.Color = Color.White.WithAlpha( 0.5f );

		foreach ( var point in _trail )
			Gizmo.Draw.SolidSphere( point, HandleRadius * 0.12f, 4, 4 );

		Gizmo.Draw.Color = Theme.Yellow;

		foreach ( var key in _trailKeys )
			Gizmo.Draw.SolidSphere( key, HandleRadius * 0.3f, 6, 6 );

		Gizmo.Draw.IgnoreDepth = false;
	}

	// --- cameras ---------------------------------------------------------------------------------

	/// <summary>Place the clip's keyed cameras where their tracks say they are at this frame.</summary>
	public void ShowCameraFrame( RigAnimDocument anim, float frame )
	{
		_cameraFrame.Clear();

		if ( anim?.Cameras is null )
			return;

		foreach ( var camera in anim.Cameras )
		{
			if ( camera is not null && anim.FindCameraTrack( camera.Name ) is { Keyframes.Count: > 0 } )
				_cameraFrame[camera] = anim.CameraAt( camera, frame );
		}
	}

	private (Transform World, float FieldOfView) CameraPlacement( RigCamera camera ) =>
		_cameraFrame.TryGetValue( camera, out var keyed ) ? keyed : (camera.World, camera.FieldOfView);

	/// <summary>Put the viewport's own camera where a shot is, with its lens - the way to judge a
	/// keyed move frame by frame. Fly off again as normal.</summary>
	public void LookThrough( RigCamera camera )
	{
		if ( camera is null || !_camera.IsValid() )
			return;

		var (world, fov) = CameraPlacement( camera );
		_camera.WorldPosition = world.Position;
		_camera.WorldRotation = world.Rotation;
		_camera.FieldOfView = fov;
	}

	// --- mirror ----------------------------------------------------------------------------------

	/// <summary>
	/// The main model's pose mirrored left for right, as the local transforms to key.
	///
	/// BY BIND-RELATIVE DELTA, NOT BY COPYING ROTATIONS. A left and a right bone rarely share local
	/// axes - one side's frames are usually the other's turned half a turn - so copying hand_L's
	/// rotation onto hand_R points the hand into the wrist. What is mirrored instead is how far each
	/// bone has moved from its bind pose, in model space, reflected across the model's left-right
	/// plane (Y): that only asks the BIND POSE to be symmetric, which a humanoid's is, and works
	/// whatever axes each side was rigged with.
	///
	/// Pairs come from Skeleton.FlipSideSuffix (hand_L / hand_R, finger_index_0_L / _R). A bone with
	/// no side - the spine, the head - is mirrored onto itself.
	///
	/// <paramref name="flip"/> swaps the two sides (and turns the middle). Otherwise it COPIES: each
	/// bone in <paramref name="bones"/> is left alone and its partner takes its mirror - pose one arm,
	/// get the other. Null bones means the whole skeleton.
	/// </summary>
	public Dictionary<string, Transform> MirroredLocals( IEnumerable<string> bones, bool flip )
	{
		var result = new Dictionary<string, Transform>();

		if ( !_renderer.IsValid() || _renderer.Model?.Bones is not { } skeleton )
			return result;

		var all = skeleton.AllBones.ToList();
		var byName = all.ToDictionary( b => b.Name );

		// Current pose in model space - the renderer's live pose, which after a scrub is the clip's.
		var model = new Dictionary<string, Transform>();

		foreach ( var bone in all )
		{
			if ( _renderer.TryGetBoneTransform( bone, out var world ) )
				model[bone.Name] = _renderer.WorldTransform.ToLocal( world );
		}

		string Partner( string name ) => Skeleton.FlipSideSuffix( name ) is { } other && byName.ContainsKey( other ) ? other : null;

		var scope = new HashSet<string>( bones ?? all.Select( b => b.Name ) );
		var targets = new Dictionary<string, string>();

		foreach ( var name in scope )
		{
			if ( !byName.ContainsKey( name ) )
				continue;

			var partner = Partner( name );

			if ( flip )
			{
				targets[name] = partner ?? name;

				if ( partner is not null )
					targets[partner] = name;
			}
			else if ( partner is not null )
			{
				targets[partner] = name;
			}
		}

		var mirrored = new Dictionary<string, Transform>( model );

		foreach ( var (target, source) in targets )
		{
			if ( !model.TryGetValue( source, out var current ) )
				continue;

			// Bone.LocalTransform is the bind pose in MODEL space - see BindPoseFor.
			mirrored[target] = MirrorModelTransform( current, byName[source].LocalTransform, byName[target].LocalTransform );
		}

		foreach ( var target in targets.Keys )
		{
			var bone = byName[target];
			var parent = bone.Parent is { } p && mirrored.TryGetValue( p.Name, out var pw ) ? pw : Transform.Zero;
			result[target] = bone.Parent is null ? mirrored[target] : parent.ToLocal( mirrored[target] );
		}

		return result;
	}

	/// <summary>
	/// One model-space transform mirrored across the model's left-right plane (Y), by its delta from
	/// <paramref name="sourceBind"/> laid onto <paramref name="targetBind"/>. See MirroredLocals; also
	/// what mirrors an IK goal, which is a model-space transform for its end bone.
	/// </summary>
	public static Transform MirrorModelTransform( Transform current, Transform sourceBind, Transform targetBind )
	{
		// How far the source has moved from its bind...
		var deltaRotation = current.Rotation * sourceBind.Rotation.Inverse;
		var deltaPosition = current.Position - deltaRotation * sourceBind.Position;

		// ...reflected across Y (a reflection conjugating a rotation keeps the Y part of its axis
		// and flips the rest), and laid onto the target's bind.
		var r = new Rotation( -deltaRotation.x, deltaRotation.y, -deltaRotation.z, deltaRotation.w );
		var t = new Vector3( deltaPosition.x, -deltaPosition.y, deltaPosition.z );

		return new Transform( r * targetBind.Position + t, r * targetBind.Rotation, current.Scale );
	}
}
