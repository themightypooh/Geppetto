using Sandbox;

namespace Marionette;

public enum RigEventSide
{
	Client,
	Server,
}

public sealed class RigEvent
{
	[Property] public string Name { get; set; } = "event";

	[Property, Group( "Timing" ), Title( "Start Frame" )] public int StartFrame { get; set; }
	[Property, Group( "Timing" ), Title( "End Frame" )] public int EndFrame { get; set; } = 1;

	// Which realm spawns the prop. Client-side is the common case for a cosmetic hold (nothing
	// else needs to know about it); Server needs to own anything that should hit, be networked,
	// or exist for other players.
	[Property, Group( "Attachment" )] public RigEventSide Side { get; set; } = RigEventSide.Client;

	[Property, Group( "Attachment" ), Title( "Model" )] public Model PropModel { get; set; }

	[Property, Group( "Attachment" ), Title( "Attach Bone" )] public string AttachBone { get; set; } = "hand_L";

	// Re-parent to the bone's live transform every frame rather than snapping once on attach --
	// off for a prop that's meant to be placed and left (e.g. set down mid-animation).
	[Property, Group( "Attachment" ), Title( "Follow Bone" )] public bool FollowBone { get; set; } = true;

	[Property, Group( "Offset" ), Title( "Position" )] public Vector3 PositionOffset { get; set; }
	[Property, Group( "Offset" ), Title( "Rotation" )] public Angles RotationOffset { get; set; }
	[Property, Group( "Offset" ), Title( "Scale" )] public float ScaleOffset { get; set; } = 1f;

	public bool IsActive( float frame ) => frame >= StartFrame && frame <= EndFrame;

	public Transform BoneOffsetTransform => new( PositionOffset, RotationOffset, ScaleOffset );
}
