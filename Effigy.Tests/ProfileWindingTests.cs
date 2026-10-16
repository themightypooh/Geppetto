using System;
using System.Collections.Generic;

namespace Effigy.Tests;

/// <summary>
/// A Profile must wind outward whichever axis it stacks along.
///
/// For a long time only Up was right. The ring is built as (depth, width) about the stacking axis and
/// then placed by swapping coordinates; for Forward and Left that swap is a mirror, so those bodies
/// came out inside out - negative volume, normals pointing in. In s&amp;box that reads as "bad normals":
/// the outside is culled, you see the far inside wall, and it is lit from the wrong side. A podracer
/// built mostly from Forward profiles showed it on every engine nacelle and the whole pod hull.
/// </summary>
public static class ProfileWindingTests
{
	public static void Run()
	{
		Report.Section( "profile winds outward along every axis" );

		var front = new List<Vec2> { new( 0, 2 ), new( 5, 3 ), new( 10, 1 ) };
		var side = new List<Vec2> { new( 0, 1.5f ), new( 5, 2.5f ), new( 10, 1 ) };
		var curve = new List<Vec3> { new( 0, 0, 0 ), new( 10, 1.5f, 0.5f ) };
		var twist = new List<Vec2> { new( 0, 0 ), new( 10, 40 ) };
		var names = new[] { "Forward", "Left", "Up" };

		for ( var axis = 0; axis < 3; axis++ )
		{
			var plain = ProfileBody.Build( front, side, null, null, 16, 0.4f, 0, axis );
			var bent = ProfileBody.Build( front, side, curve, twist, 16, 0.4f, 0, axis );

			Report.Check( $"{names[axis]} profile winds outward", plain.SignedVolume() > 0f, $"{plain.SignedVolume():0.###}" );
			Report.Check( $"{names[axis]} curved, twisted profile winds outward", bent.SignedVolume() > 0f, $"{bent.SignedVolume():0.###}" );
		}

		// the same outline along each axis is the same solid, just turned
		var up = ProfileBody.Build( front, side, null, null, 16, 0.4f, 0, 2 ).SignedVolume();
		var fwd = ProfileBody.Build( front, side, null, null, 16, 0.4f, 0, 0 ).SignedVolume();
		Report.Check( "Forward and Up enclose the same volume", MathF.Abs( up - fwd ) < 1e-3f * MathF.Abs( up ), $"{fwd:0.###} vs {up:0.###}" );

		// mirror=1 makes the other side through MeshTransform.Apply, which already re-winds a
		// handedness flip. Reversing the faces a second time on top of that turned every R leg of the
		// podracer pilot inside out.
		foreach ( var along in names )
		{
			var studio = new PartStudio();
			var result = BuildScript.Run( studio, $"add Profile name=Leg along={along} front=0,2;5,3;10,1 side=0,1.5;5,2.5;10,1 mirror=1 position=0,4,0" );
			var left = ModelMeasure.FindBody( studio, "Leg L" );
			var right = ModelMeasure.FindBody( studio, "Leg R" );

			Report.Check( $"{along} mirror=1 builds both sides", result.Ok && left is not null && right is not null, result.ToString() );

			if ( left is not null && right is not null )
				Report.Check( $"{along} mirrored side winds outward like the original", right.Mesh.SignedVolume() > 0f
					&& MathF.Abs( right.Mesh.SignedVolume() - left.Mesh.SignedVolume() ) < 1e-3f * MathF.Abs( left.Mesh.SignedVolume() ),
					$"{left.Mesh.SignedVolume():0.###} vs {right.Mesh.SignedVolume():0.###}" );
		}
	}
}
