using Editor;
using Editor.Mcp;
using Effigy;
using Marionette.EditorTools;
using Sandbox;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.Tools;

public static partial class MarionetteMcpTools
{
	/// <summary>
	/// Key a saved clip from TARGETS instead of angles: where an IK end (a hand, a foot, a cable tip)
	/// should be on which frames, in the model's space. mode=goal writes the live constraint's goal
	/// keys (converted into the space the clip keys that goal in - an object, the world) and IK weight
	/// keys; mode=fk solves the constraint now on the clip's own pose at each frame and writes the chain
	/// as FK keys. Same maths as the kernel's ClipKeys, which the headless `effigy.sh key` runs.
	/// </summary>
	[McpTool( "marionette_key" )]
	public static McpResult Key(
		[Description( "Asset path of the clip" )] string path,
		[Description( "The IK constraint's end bone (its Target Bone), e.g. cable1_hand" )] string bone,
		[Description( "Targets, ';' between them: frame:x,y,z[:pitch,yaw,roll][:ease][:w=0..1] - model space, inches; rotation as Angles in degrees; ease Smooth/Linear/Stepped/EaseIn/EaseOut/Cubic/Monotone; w = IK weight to key on that frame (goal mode)" )] string targets,
		[Description( "goal (live IK goal keys + weights) or fk (solve now, write the chain's FK keys)" )] string mode = "goal",
		[Description( "Ease for targets that do not name one" )] string ease = "Smooth",
		[Description( "fk: chain length when the rig has no constraint ending at bone (0 = the constraint's)" )] int chain = 0,
		[Description( "fk: solver when the rig has none for bone - ccd, fabrik or spline" )] string solver = "fabrik",
		[Description( "Write the result to disk; false only reports what would change" )] bool save = true )
	{
		if ( Load( path, out var asset, out var doc ) is { } error )
			return McpResult.Text( error );

		var message = RigKeyFromTargets.Apply( doc, bone, targets, mode, ease, chain, solver, out var changed );

		if ( !changed )
			return McpResult.Text( message );

		if ( save )
			asset.SaveToDisk( doc );

		return McpResult.Text( $"{message}{(save ? $"; saved {asset.Path}" : " (not saved)")}" );
	}
}
