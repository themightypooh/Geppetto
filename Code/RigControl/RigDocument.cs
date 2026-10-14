using Sandbox;
using System.Collections.Generic;

namespace Marionette;

// The rig itself: what model it's for, and the IK/Limit constraint stack the compiled skeleton
// solves through. Separate from RigAnimDocument (.riganim) because the same rig is shared by
// every clip authored against it - editing a constraint here should affect every riganim that
// points at this .ctrlrig, not just the one open at the time.
[AssetType( Name = "Control Rig", Extension = "ctrlrig", Category = "Marionette" )]
public sealed class RigDocument : GameResource
{
	[Property, Group( "Source" )] public Model SourceModel { get; set; }

	[Property] public List<IkConstraint> IkConstraints { get; set; } = new();
	[Property] public List<LimitConstraint> LimitConstraints { get; set; } = new();

	/// <summary>
	/// Bones hidden from the viewport - rig plumbing like weapon_root or camera helpers that are
	/// never posed by hand and only crowd the ones that are.
	///
	/// PURELY A VIEW FILTER. A hidden bone still evaluates, still keeps its keyframes, and still
	/// plays back exactly as before - it just stops drawing a handle. Hiding is not deleting, and
	/// a tool where hiding silently dropped animation would be a trap.
	///
	/// Lives on the rig rather than the clip because which bones are worth touching is a property
	/// of the skeleton, so every clip authored against it wants the same answer.
	/// </summary>
	[Property] public List<string> HiddenBones { get; set; } = new();

	public bool IsBoneHidden( string bone ) => HiddenBones.Contains( bone );
}

public sealed class IkConstraint
{
	[Property] public string Name { get; set; } = "IK";
	[Property, Title( "Target Bone" )] public string TargetBone { get; set; } = "";
	[Property] public float Weight { get; set; } = 1f;
	[Property] public bool Enabled { get; set; } = true;

	/// <summary>
	/// How many bones up the chain from TargetBone the solve is allowed to move - 2 reaches the
	/// target's parent and grandparent (wrist -> elbow -> shoulder for an arm).
	///
	/// 1 aims the one bone above the end; 2 is the exact two-bone solve, bending toward the pole;
	/// 3 or more is iterated (see Iterations). These used to be stored and ignored - every chain was
	/// solved as two bones whatever this said. See RigConstraintSolver.SolveChain.
	/// </summary>
	[Property, Title( "Chain Length" ), Range( 1, 8 )] public int ChainLength { get; set; } = 2;

	/// <summary>Which way the middle joint bends, in the model's own space. Two-bone chains only -
	/// a longer chain has no single middle joint for it to mean.</summary>
	[Property, Title( "Pole Direction" )] public Vector3 PoleDirection { get; set; } = Vector3.Up;

	/// <summary>Passes of the solve for a chain of three or more. One- and two-bone chains have an
	/// exact answer and do not iterate.</summary>
	[Property] public int Iterations { get; set; } = 10;

	/// <summary>
	/// Solve at playback from an animated goal, rather than once while dragging.
	///
	/// ON: dragging the target bone keys the constraint's GOAL (an IK goal track on the clip) and
	/// the chain is solved toward it every frame - in the editor, in game, and in the export. Change
	/// the constraint later and every pose authored under it follows.
	///
	/// OFF: the old behaviour. The drag runs the solve once and bakes the chain into ordinary
	/// keyframes; playback needs no solver.
	///
	/// ON FOR EVERY CONSTRAINT ADDED NOW, OFF FOR ONE LOADED FROM A RIG THAT PREDATES IT. A rig made
	/// before this existed was authored by baking, and its clips are full of baked arm keys; turning
	/// it live would make the next drag write a goal that then overrides those keys on every frame,
	/// including the ones already finished. So the property's default is off (what an old file reads
	/// back as) and RigConstraintsPanel switches it on when it adds a constraint. Tick it on an old
	/// rig to opt in.
	/// </summary>
	[Property, Title( "Solve At Playback" )] public bool Live { get; set; }

	/// <summary>Whether the end bone takes the goal's rotation as well as its position - one control
	/// that places the wrist and turns the palm. Off, the end keeps the rotation its keys give it.</summary>
	[Property, Title( "Match Goal Rotation" ), ShowIf( nameof( Live ), true )] public bool GoalRotation { get; set; } = true;
}

public sealed class LimitConstraint
{
	[Property] public string Name { get; set; } = "Limit";
	[Property, Title( "Target Bone" )] public string TargetBone { get; set; } = "";
	[Property] public float Weight { get; set; } = 1f;
	[Property] public bool Enabled { get; set; }

	[Property, Title( "Min Angles" )] public Angles MinAngles { get; set; } = new( -45, -45, -45 );
	[Property, Title( "Max Angles" )] public Angles MaxAngles { get; set; } = new( 45, 45, 45 );
}
