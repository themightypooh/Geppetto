using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A blocky humanoid robot, one box per bone, for the "Make a Playermodel" tutorial to start from.
///
/// WHY A SAMPLE AT ALL. The playermodel lesson is about NAMES AND PARENTS - which bone hangs off
/// which, spelled the way citizen's animations expect. Nothing about it is about modelling, and
/// making the reader build twenty boxes first buries the one idea under forty minutes of typing
/// numbers into a primitive dialog. Offered alongside "I'll use my own model", so the lesson works
/// either way and neither route is the consolation prize.
///
/// WHY SEPARATE BOXES AND NOT ONE MESH. Each part is its own body, so Bone from Part can measure a
/// bone along it and pin it, which is the whole loop the lesson teaches. A merged humanoid is one
/// body with one bone and needs weight painting before anything bends.
///
/// WHY EVERY PART IS NAMED AFTER ITS BONE. A body takes its name from the feature that made it, and
/// Bone from Part names the bone after the body - so a part called `upperarm_L` becomes a bone
/// called `upperarm_L`, which <see cref="CitizenBoneMap.Playermodel"/> already knows how to place.
/// The reader never has to rename anything, and the one thing that can silently ruin a playermodel -
/// a misspelled bone - cannot happen on this route.
///
/// WHY EVERY PART IS LONGEST ALONG ITS BONE. <see cref="BoneFromBody.TryDerive"/> measures the
/// direction the vertices spread out along most, so a part that is wider than it is long yields a
/// bone across the limb rather than down it. That is why the torso boxes are taller than they are
/// wide and the robot reads as slightly slim: the shape is constrained by what the measurement can
/// see, not by taste. A cube has no longest axis at all and would be skipped outright.
///
/// UNITS ARE INCHES, the same as the rest of Effigy and of s&box. It stands in a T-pose facing +X
/// with the soles of its feet at z=0 and its hips at 31.5 - citizen's own pelvis height, because
/// citizen's walk decides how high the hips ride and a model built far from it floats or sinks.
/// </summary>
public static class HumanoidSample
{
	/// <summary>One box: the bone name it carries, its size, and where its centre sits.</summary>
	public readonly struct Part
	{
		public readonly string Name;
		public readonly Vec3 Size;
		public readonly Vec3 Centre;

		public Part( string name, Vec3 size, Vec3 centre )
		{
			Name = name;
			Size = size;
			Centre = centre;
		}
	}

	/// <summary>
	/// The left side and the spine. The right side is this mirrored, built by <see cref="Parts"/>.
	///
	/// Written out rather than derived from a proportion table, because these numbers were tuned
	/// against two things at once - the measurement above, and citizen's pelvis height - and a
	/// formula that produced them would be a formula fitted to thirteen answers.
	/// </summary>
	static readonly Part[] Half =
	{
		new( "pelvis", new Vec3( 5f, 6.5f, 7f ), new Vec3( 0f, 0f, 31.5f ) ),
		new( "spine_01", new Vec3( 4.5f, 6.5f, 7f ), new Vec3( 0f, 0f, 38.5f ) ),
		new( "spine_02", new Vec3( 5f, 7f, 7.5f ), new Vec3( 0f, 0f, 45.75f ) ),
		new( "chest", new Vec3( 6f, 7.5f, 8f ), new Vec3( 0f, 0f, 53.5f ) ),
		new( "neck", new Vec3( 2.5f, 2.5f, 3.5f ), new Vec3( 0f, 0f, 59.25f ) ),
		new( "head", new Vec3( 7f, 6.5f, 8.5f ), new Vec3( 0f, 0f, 65.25f ) ),

		new( "clavicle_L", new Vec3( 3f, 5.5f, 2.5f ), new Vec3( 0f, 5.25f, 56f ) ),
		new( "upperarm_L", new Vec3( 3f, 9.5f, 3f ), new Vec3( 0f, 12.75f, 56f ) ),
		new( "forearm_L", new Vec3( 2.6f, 8.5f, 2.6f ), new Vec3( 0f, 21.75f, 56f ) ),
		new( "hand_L", new Vec3( 1.5f, 5f, 3.2f ), new Vec3( 0f, 28.5f, 56f ) ),

		new( "thigh_L", new Vec3( 4f, 4f, 13f ), new Vec3( 0f, 3.2f, 23.5f ) ),
		new( "calf_L", new Vec3( 3.5f, 3.5f, 12.5f ), new Vec3( 0f, 3.2f, 10.75f ) ),
		new( "foot_L", new Vec3( 8f, 3.5f, 3f ), new Vec3( 2.5f, 3.2f, 1.5f ) ),
	};

	/// <summary>
	/// Every box, left side then right. `_L` is the MODEL's left, which is +Y - the side on your
	/// left when you stand behind it, and the side citizen's `_L` bones are on.
	/// </summary>
	public static IReadOnlyList<Part> Parts
	{
		get
		{
			var parts = new List<Part>( Half.Length * 2 );

			foreach ( var part in Half )
			{
				parts.Add( part );

				if ( !part.Name.EndsWith( "_L", StringComparison.Ordinal ) )
					continue;

				var mirrored = part.Name[..^2] + "_R";

				parts.Add( new Part( mirrored, part.Size,
					new Vec3( part.Centre.x, -part.Centre.y, part.Centre.z ) ) );
			}

			return parts;
		}
	}

	/// <summary>
	/// The chains the lesson builds, each one a parent followed by its descendants in order.
	///
	/// ONE LIST, SHARED. The tutorial's IsDone checks read this, the headless test walks it to build
	/// the rig the reader would build by hand, and the step text is written from it. Three copies of
	/// a parent order is three chances for the document to teach something the test does not check.
	///
	/// The first name in each chain already exists by the time that chain is built - `chest` and
	/// `pelvis` come from the spine - so a builder walking these must skip a bone it already made
	/// rather than making a second one.
	/// </summary>
	public static IReadOnlyList<string[]> Chains { get; } = new[]
	{
		new[] { "pelvis", "spine_01", "spine_02", "chest", "neck", "head" },
		new[] { "chest", "clavicle_L", "upperarm_L", "forearm_L", "hand_L" },
		new[] { "chest", "clavicle_R", "upperarm_R", "forearm_R", "hand_R" },
		new[] { "pelvis", "thigh_L", "calf_L", "foot_L" },
		new[] { "pelvis", "thigh_R", "calf_R", "foot_R" },
	};

	/// <summary>
	/// A studio holding the robot, already rebuilt.
	///
	/// BodyNames is written as well as Feature.Name. A body is named after its feature today, and
	/// the names are the one thing this sample exists to get right - belt and braces is cheap here
	/// and a silently renamed body would send the reader hunting a misspelling they never made.
	/// </summary>
	public static PartStudio Build()
	{
		var studio = new PartStudio();

		foreach ( var part in Parts )
		{
			var primitive = studio.Add( new PrimitiveFeature { Name = part.Name } );

			primitive.SizeX.Value = part.Size.x;
			primitive.SizeY.Value = part.Size.y;
			primitive.SizeZ.Value = part.Size.z;
			primitive.Position.Value = part.Centre;
		}

		studio.Rebuild();

		foreach ( var body in studio.Bodies )
		{
			var feature = studio.Features.Find( f => f.Id == body.FeatureId );

			if ( feature?.Name is { Length: > 0 } name )
				studio.BodyNames[body.Id] = name;
		}

		// Names are applied on the NEXT rebuild, so a caller that only ever reads Bodies would see
		// the feature names it already had. Cheap, and it means Build returns a studio whose bodies
		// and BodyNames agree.
		studio.Rebuild();

		return studio;
	}
}
