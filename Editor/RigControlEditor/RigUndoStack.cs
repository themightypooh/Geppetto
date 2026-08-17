using Marionette;
using Sandbox;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// A full-state snapshot of everything the editor can change: the clip's tracks/events and the
/// rig's constraints.
///
/// Deep clones rather than JSON. The data is small (a clip is a few hundred keyframes at most, and
/// snapshots only happen per user action, not per frame), and cloning by hand can't fail on a
/// missing serializer for Transform/Angles the way a round-trip through Json could. An undo system
/// that silently corrupts a pose is worse than no undo system.
/// </summary>
internal sealed class RigSnapshot
{
	public string Label { get; private set; } = "Edit";

	private List<BoneTrack> _tracks;
	private List<RigEvent> _events;
	private List<MorphEvent> _morphs;
	private int _frameCount;
	private int _animationSpeed;

	private List<IkConstraint> _ik;
	private List<LimitConstraint> _limits;
	private bool _hasRig;

	public static RigSnapshot Capture( RigAnimDocument anim, RigDocument rig )
	{
		var snap = new RigSnapshot();

		if ( anim is not null )
		{
			snap._tracks = anim.BoneTracks.Select( Clone ).ToList();
			snap._events = anim.Events.Select( Clone ).ToList();
			snap._morphs = anim.MorphEvents.Select( Clone ).ToList();
			snap._frameCount = anim.FrameCount;
			snap._animationSpeed = anim.AnimationSpeed;
		}

		if ( rig is not null )
		{
			snap._hasRig = true;
			snap._ik = rig.IkConstraints.Select( Clone ).ToList();
			snap._limits = rig.LimitConstraints.Select( Clone ).ToList();
		}

		return snap;
	}

	public RigSnapshot WithLabel( string label )
	{
		Label = label;
		return this;
	}

	public void RestoreTo( RigAnimDocument anim, RigDocument rig )
	{
		if ( anim is not null && _tracks is not null )
		{
			anim.BoneTracks = _tracks.Select( Clone ).ToList();
			anim.Events = _events.Select( Clone ).ToList();
			anim.MorphEvents = _morphs.Select( Clone ).ToList();
			anim.FrameCount = _frameCount;
			anim.AnimationSpeed = _animationSpeed;
		}

		// Only touch the rig if this snapshot actually captured one - a clip opened without a
		// .ctrlrig must not wipe the constraints of one attached later.
		if ( rig is not null && _hasRig )
		{
			rig.IkConstraints = _ik.Select( Clone ).ToList();
			rig.LimitConstraints = _limits.Select( Clone ).ToList();
		}
	}

	private static BoneTrack Clone( BoneTrack t ) => new()
	{
		BoneName = t.BoneName,
		Keyframes = t.Keyframes.Select( k => new BoneKeyframe
		{
			Frame = k.Frame,
			Local = k.Local,
			Interpolation = k.Interpolation
		} ).ToList()
	};

	private static RigEvent Clone( RigEvent e ) => new()
	{
		Name = e.Name,
		StartFrame = e.StartFrame,
		EndFrame = e.EndFrame,
		Side = e.Side,
		PropModel = e.PropModel,
		AttachBone = e.AttachBone,
		FollowBone = e.FollowBone,
		PositionOffset = e.PositionOffset,
		RotationOffset = e.RotationOffset,
		ScaleOffset = e.ScaleOffset
	};

	private static MorphEvent Clone( MorphEvent m ) => new()
	{
		Name = m.Name,
		StartFrame = m.StartFrame,
		EndFrame = m.EndFrame,
		MorphName = m.MorphName,
		Value = m.Value
	};

	private static IkConstraint Clone( IkConstraint c ) => new()
	{
		Name = c.Name,
		TargetBone = c.TargetBone,
		Weight = c.Weight,
		Enabled = c.Enabled,
		ChainLength = c.ChainLength,
		PoleDirection = c.PoleDirection,
		Iterations = c.Iterations
	};

	private static LimitConstraint Clone( LimitConstraint c ) => new()
	{
		Name = c.Name,
		TargetBone = c.TargetBone,
		Weight = c.Weight,
		Enabled = c.Enabled,
		MinAngles = c.MinAngles,
		MaxAngles = c.MaxAngles
	};
}

/// <summary>
/// Undo/redo over RigSnapshots.
///
/// The window keeps a rolling "baseline" snapshot of the last committed state and pushes THAT
/// here when an edit lands, because the panels mutate the document and then report it - by the
/// time anything is notified, the pre-edit state only exists in the baseline. Snapshotting on
/// notification would record the result of the edit, which undoes to nothing.
/// </summary>
internal sealed class RigUndoStack
{
	private const int MaxDepth = 128;

	private readonly List<RigSnapshot> _undo = new();
	private readonly List<RigSnapshot> _redo = new();

	public bool CanUndo => _undo.Count > 0;
	public bool CanRedo => _redo.Count > 0;

	public string UndoLabel => CanUndo ? _undo[^1].Label : null;
	public string RedoLabel => CanRedo ? _redo[^1].Label : null;

	public void Clear()
	{
		_undo.Clear();
		_redo.Clear();
	}

	/// <summary>Record a pre-edit state. Doing anything new invalidates the redo branch.</summary>
	public void Push( RigSnapshot before )
	{
		if ( before is null )
			return;

		_undo.Add( before );
		_redo.Clear();

		// Oldest-first trim, so a long session costs bounded memory rather than growing forever.
		if ( _undo.Count > MaxDepth )
			_undo.RemoveAt( 0 );
	}

	public RigSnapshot Undo( RigSnapshot current )
	{
		if ( !CanUndo )
			return null;

		var state = _undo[^1];
		_undo.RemoveAt( _undo.Count - 1 );

		_redo.Add( current.WithLabel( state.Label ) );

		return state;
	}

	public RigSnapshot Redo( RigSnapshot current )
	{
		if ( !CanRedo )
			return null;

		var state = _redo[^1];
		_redo.RemoveAt( _redo.Count - 1 );

		_undo.Add( current.WithLabel( state.Label ) );

		return state;
	}
}
