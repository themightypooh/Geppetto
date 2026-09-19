using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// The poses a garment is tested in before it ships: the ones that find a shoulder through a
/// sleeve, a thigh through a trouser leg, a hem that rides up when the wearer sits.
///
/// WHY POSES AND NOT AN ANIMATION. A garment fitted in the bind pose fits in the bind pose, and
/// nothing about that says whether it still does with the arms up. Every clothing tool worth
/// the name previews the extremes - Marvelous Designer's "test movement", CLO's pose library -
/// because the extremes are where clipping lives, and a walk cycle never reaches them. Eight
/// still poses, each one an extreme, found the same faults a clip would and are a click each.
///
/// BY REGION, NOT BY BONE NAME. A pose says "raise the upper arms 130 degrees"; which bone that
/// is comes from <see cref="BodyRegions.Classify"/>, so the same eight poses bend Citizen, a
/// Mixamo import and a rig somebody named in English. Only the first bone of a region's chain
/// is turned (a twist bone, a second spine link follow their parent as they would in a graph),
/// except the torso, which spreads its bend along the spine so it curves rather than hinges.
///
/// WORLD AXES, in the body's frame: +x forward, +y left, +z up. A pose is written for a body
/// standing upright and facing +x, which is what every wearer in Effigy is, and the rotation is
/// applied about the bone's own head so the joint stays put.
/// </summary>
public static class GarmentPoses
{
	/// <summary>One joint's turn: which region, which side (+1 left, -1 right, 0 both), about
	/// which world axis, by how many degrees. <see cref="Mirror"/> flips the angle for the right
	/// side, for the turns that are symmetric (raising both arms) rather than shared (leaning).</summary>
	public readonly record struct Turn( BodyRegion Region, int Side, Vec3 Axis, float Degrees, bool Mirror = false );

	public sealed class Pose
	{
		public string Name;
		public string Tip;
		public Turn[] Turns;
	}

	static readonly Vec3 Fwd = new( 1f, 0f, 0f );
	static readonly Vec3 Left = new( 0f, 1f, 0f );
	static readonly Vec3 Up = new( 0f, 0f, 1f );

	// About +x, +degrees takes a hanging left arm (-z) out to the left (+y); mirrored, the right
	// arm goes right. About +y, -degrees takes a hanging limb forward (+x); +degrees takes it back.
	// About +z, +degrees turns the body to its left.
	public static readonly IReadOnlyList<Pose> All = new[]
	{
		new Pose
		{
			Name = "Arms out", Tip = "Arms straight out to the sides. Finds a sleeve that pulls off the shoulder.",
			Turns = new[] { new Turn( BodyRegion.UpperArm, 0, Fwd, 50f, Mirror: true ) },
		},
		new Pose
		{
			Name = "Arms up", Tip = "Both arms straight up. The pose that lifts a hem and splits an armpit.",
			Turns = new[] { new Turn( BodyRegion.UpperArm, 0, Fwd, 130f, Mirror: true ) },
		},
		new Pose
		{
			Name = "Reach", Tip = "Arms forward, elbows bent, like holding a wheel. Finds the back of a shoulder through a sleeve.",
			Turns = new[]
			{
				new Turn( BodyRegion.UpperArm, 0, Left, -70f ),
				new Turn( BodyRegion.LowerArm, 0, Left, -60f ),
			},
		},
		new Pose
		{
			Name = "Bend", Tip = "Bent forward at the waist. The back of a shirt has to stretch; the hem rides up.",
			Turns = new[] { new Turn( BodyRegion.Torso, 0, Left, 45f ) },
		},
		new Pose
		{
			Name = "Twist", Tip = "Upper body turned to the left. Finds a waist that does not follow.",
			Turns = new[] { new Turn( BodyRegion.Torso, 0, Up, 40f ) },
		},
		new Pose
		{
			Name = "Sit", Tip = "Thighs forward, knees bent, as in a chair. Trousers pull at the seat and the knee.",
			Turns = new[]
			{
				new Turn( BodyRegion.UpperLeg, 0, Left, -85f ),
				new Turn( BodyRegion.LowerLeg, 0, Left, 85f ),
			},
		},
		new Pose
		{
			Name = "Stride", Tip = "One leg forward, one back, mid-step. Finds the crotch and the back of the thigh.",
			Turns = new[]
			{
				new Turn( BodyRegion.UpperLeg, +1, Left, -40f ),
				new Turn( BodyRegion.LowerLeg, +1, Left, 30f ),
				new Turn( BodyRegion.UpperLeg, -1, Left, 30f ),
			},
		},
		new Pose
		{
			Name = "Crouch", Tip = "Down on the haunches, arms forward. Everything at once.",
			Turns = new[]
			{
				new Turn( BodyRegion.UpperLeg, 0, Left, -100f ),
				new Turn( BodyRegion.LowerLeg, 0, Left, 120f ),
				new Turn( BodyRegion.Torso, 0, Left, 30f ),
				new Turn( BodyRegion.UpperArm, 0, Left, -60f ),
			},
		},
	};

	/// <summary>
	/// Which side of the body a bone is on: +1 left, -1 right, 0 for the middle (a spine, a neck)
	/// or a name that does not say. Reads Citizen's <c>_L</c>/<c>_R</c>, Mixamo's <c>Left</c>/
	/// <c>Right</c>, Unreal's <c>_l</c>/<c>_r</c> and the words themselves.
	/// </summary>
	public static int SideOf( string boneName )
	{
		var n = (boneName ?? "").ToLowerInvariant();

		if ( n.Contains( "left" ) ) return +1;
		if ( n.Contains( "right" ) ) return -1;

		// A side letter is its own token: arm_upper_L, arm_upper_L_twist, upperarm_l, L_arm.
		var parts = n.Split( new[] { '_', '.', ':', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries );

		if ( parts.Any( p => p == "l" ) ) return +1;
		if ( parts.Any( p => p == "r" ) ) return -1;

		return 0;
	}

	/// <summary>
	/// Bend a skeleton into a pose, in place. Call it on a skeleton at its bind pose; calling it
	/// twice bends twice. Returns how many bones were turned, so a rig Effigy cannot read (no
	/// arm it recognises) reports "nothing moved" rather than showing the bind pose as the answer.
	/// </summary>
	/// <param name="amount">How far into the pose, 0 the bind pose to 1 the whole thing. A scrub
	/// through the middle finds where a sleeve first catches, which the extreme alone does not.</param>
	public static int Apply( Skeleton skeleton, Pose pose, float amount = 1f )
	{
		if ( skeleton is null || pose?.Turns is null )
			return 0;

		amount = Math.Clamp( amount, 0f, 1f );

		var turned = 0;

		foreach ( var turn in pose.Turns )
		{
			// The chains of this region on the wanted side: each starts at a bone whose parent is
			// not in the region, and runs down through children that are.
			var roots = new List<int>();

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				var bone = skeleton.Bones[i];

				if ( BodyRegions.Classify( bone.Name ) != turn.Region )
					continue;

				if ( bone.Parent >= 0 && BodyRegions.Classify( skeleton.Bones[bone.Parent].Name ) == turn.Region )
					continue;

				var side = SideOf( bone.Name );

				if ( turn.Side != 0 && side != turn.Side )
					continue;

				roots.Add( i );
			}

			foreach ( var root in roots )
			{
				var side = SideOf( skeleton.Bones[root].Name );
				var degrees = (turn.Mirror && side < 0 ? -turn.Degrees : turn.Degrees) * amount;

				// The torso is a chain that should curve: spread the bend down the links. A limb
				// hinges at its root and its twist bones ride along.
				var chain = turn.Region is BodyRegion.Torso ? Chain( skeleton, root, turn.Region ) : new List<int> { root };
				var each = degrees / chain.Count;

				foreach ( var index in chain )
				{
					Rotate( skeleton, index, turn.Axis, each );
					turned++;
				}
			}
		}

		return turned;
	}

	/// <summary>Rotate a bone about a world axis through its own head, keeping its children with
	/// it: only this bone's Local changes, and WorldBind walks the chain, so everything below
	/// follows the way a posed joint's do.</summary>
	static void Rotate( Skeleton skeleton, int index, Vec3 axis, float degrees )
	{
		var bone = skeleton.Bones[index];
		var world = skeleton.WorldBind( index );
		var spun = Xform.RotateAbout( world.Origin, axis, degrees * MathF.PI / 180f ) * world;

		bone.Local = bone.Parent < 0 ? spun : skeleton.WorldBind( bone.Parent ).Inverse * spun;
	}

	/// <summary>A bone that classifies as its parent's region but is not the next link of the
	/// chain: a clavicle off the spine, a twist bone beside the arm.</summary>
	static bool Offshoot( string name ) =>
		name.Contains( "twist", StringComparison.OrdinalIgnoreCase )
		|| name.Contains( "clavicle", StringComparison.OrdinalIgnoreCase )
		|| name.Contains( "shoulder", StringComparison.OrdinalIgnoreCase )
		|| name.Contains( "collar", StringComparison.OrdinalIgnoreCase );

	/// <summary>The bone and the line of same-region descendants under it, top down, one per
	/// level: a spine's links, not every rib hanging off them.</summary>
	static List<int> Chain( Skeleton skeleton, int root, BodyRegion region )
	{
		var chain = new List<int> { root };
		var at = root;

		while ( true )
		{
			var next = skeleton.Children( at )
				.Where( c => BodyRegions.Classify( skeleton.Bones[c].Name ) == region && !Offshoot( skeleton.Bones[c].Name ) )
				.Cast<int?>()
				.FirstOrDefault();

			if ( next is not { } n )
				return chain;

			chain.Add( n );
			at = n;
		}
	}
}
