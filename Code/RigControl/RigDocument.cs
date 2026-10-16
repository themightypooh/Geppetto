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

	/// <summary>
	/// Corrective shape keys: morphs whose weight is read off a bone's angle every frame. See
	/// RigMorphDriver. Effigy's export fills these from its Corrective Shape Keys.
	/// </summary>
	[Property, Group( "Correctives" ), Title( "Morph Drivers" )] public List<RigMorphDriver> MorphDrivers { get; set; } = new();

	/// <summary>
	/// The body soft bones collide with: a capsule from each listed bone's head to its tail (first
	/// child), Radius thick. Read by Bake Secondary Motion, the player's live Soft Chains and Effigy's
	/// headless sim alike, so a cable hangs off the same body baked or live. A capsule on a bone that
	/// is itself soft is ignored - a cable does not collide with itself.
	/// </summary>
	[Property, Group( "Secondary Motion" ), Title( "Soft Colliders" )] public List<RigSoftCollider> SoftColliders { get; set; } = new();

	/// <summary>Soft bones stay above a floor at GroundHeight on the model's own Z.</summary>
	[Property, Group( "Secondary Motion" )] public bool Ground { get; set; }

	[Property, Group( "Secondary Motion" ), ShowIf( nameof( Ground ), true )] public float GroundHeight { get; set; }

	/// <summary>The soft bones' own radius: how far a tail keeps off a collider or the floor.</summary>
	[Property, Group( "Secondary Motion" ), Title( "Soft Thickness" )] public float SoftThickness { get; set; } = 1f;
}

/// <summary>A capsule on a body bone that soft bones collide with. See RigDocument.SoftColliders.</summary>
public sealed class RigSoftCollider
{
	[Property] public string Bone { get; set; } = "";
	[Property] public float Radius { get; set; } = 4f;
	[Property] public bool Enabled { get; set; } = true;
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
	[Property, Title( "Chain Length" ), Range( 1, 16 )] public int ChainLength { get; set; } = 2;

	/// <summary>
	/// How a chain of three or more bones is solved. One and two bones have an exact answer and
	/// ignore this.
	///
	/// CCD (the default, and what every rig made before this reads back as) turns each bone from the
	/// end upward to point the end at the goal. It converges fast, but the bones nearest the end do
	/// most of the bending, so a long tentacle or cable curls at its tip and stays straight at the root.
	///
	/// FABRIK drags the joint positions to the goal and back to the root, so the bend spreads along
	/// the whole chain - the shape a rope or a tentacle actually takes. Use it for anything longer than
	/// a limb.
	///
	/// SPLINE lays the chain on one even arc from its root to the goal, every joint bending the same,
	/// bowing the way the keyed pose bows (toward the Pole Direction when the pose is straight) - so a
	/// tentacle or slack cable is one curve, never a kink. With Goal Rotation on, a turned wrist's twist
	/// is spread along the chain instead of all landing on the last joint. CCD and FABRIK ignore the pole.
	/// </summary>
	[Property, ShowIf( nameof( IsLongChain ), true )] public IkSolver Solver { get; set; } = IkSolver.Ccd;

	/// <summary>Whether Solver applies - only to chains of three or more.</summary>
	[Hide] public bool IsLongChain => ChainLength >= 3;

	/// <summary>Which way the middle joint bends, in the model's own space. Two-bone chains, and a
	/// Spline chain whose keyed pose is straight (which way it bows) - CCD and FABRIK chains have no
	/// single middle joint for it to mean.</summary>
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

	/// <summary>
	/// How far past its own length the chain may stretch to reach a goal - 1.5 lets a cable reach
	/// half as far again. 1 (the default, and what every older rig reads back as) never stretches:
	/// a goal out of reach is pointed at, as before.
	///
	/// Only a goal BEYOND the chain stretches it, and only as much as that goal needs. The joints
	/// slide apart along their bones and the skin between them stretches; bones are not scaled,
	/// because Source 2 bone scale is uniform and a scaled bone would fatten as it lengthened. See
	/// RigConstraintSolver.Stretch.
	/// </summary>
	[Property, Title( "Max Stretch" ), Range( 1f, 4f )] public float MaxStretch { get; set; } = 1f;
}

/// <summary>The solver for an IK chain of three or more bones. See IkConstraint.Solver.
/// NEW VALUES ARE APPENDED, NEVER INSERTED - see KeyInterpolation for why.</summary>
public enum IkSolver
{
	/// <summary>Cyclic coordinate descent: fast, bends the end of the chain most.</summary>
	[Title( "CCD" )]
	Ccd,

	/// <summary>Forward-and-backward reaching: spreads the bend along the whole chain.</summary>
	[Title( "FABRIK" )]
	Fabrik,

	/// <summary>Spline IK: the chain lies on one even arc from its root to the goal, bowing the way the
	/// pose bows; with Goal Rotation the twist is spread down the chain. For tentacles and cables. See
	/// RigConstraintSolver.SolveSpline.</summary>
	[Title( "Spline" )]
	Spline
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
