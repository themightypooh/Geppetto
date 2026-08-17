using Sandbox;
using System.Collections.Generic;

namespace Marionette;

// The rig itself: what model it's for, and the IK/Limit constraint stack the compiled skeleton
// solves through. Separate from RigAnimDocument (.riganim) because the same rig is shared by
// every clip authored against it - editing a constraint here should affect every riganim that
// points at this .ctrlrig, not just the one open at the time.
[AssetType( Name = "Control Rig", Extension = "ctrlrig", Category = "Midnight AM" )]
public sealed class RigDocument : GameResource
{
	[Property, Group( "Source" )] public Model SourceModel { get; set; }

	[Property] public List<IkConstraint> IkConstraints { get; set; } = new();
	[Property] public List<LimitConstraint> LimitConstraints { get; set; } = new();
}

public sealed class IkConstraint
{
	[Property] public string Name { get; set; } = "IK";
	[Property, Title( "Target Bone" )] public string TargetBone { get; set; } = "";
	[Property] public float Weight { get; set; } = 1f;
	[Property] public bool Enabled { get; set; } = true;

	// How many bones up the chain from TargetBone the solve is allowed to move - 2 reaches the
	// target's parent and grandparent (e.g. wrist -> elbow -> shoulder for an arm IK).
	[Property, Title( "Chain Length" )] public int ChainLength { get; set; } = 2;

	[Property, Title( "Pole Direction" )] public Vector3 PoleDirection { get; set; } = Vector3.Up;
	[Property] public int Iterations { get; set; } = 10;
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
