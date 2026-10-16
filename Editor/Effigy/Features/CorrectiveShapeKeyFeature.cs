using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>A feature that opens a shape key End Shape Key closes: Shape Key or Corrective Shape Key.
/// What the Shape Keys panel lists a key by.</summary>
public interface IShapeKeyStart
{
	string KeyName { get; }
	BodySelectionParam KeyBodies { get; }
	StringParam KeyNameParam { get; }

	/// <summary>The driver the key will carry, or null when it is keyed by hand only.</summary>
	MorphDriver Driver { get; }
}

/// <summary>
/// The "Driven by" fields both shape-key features carry, and how they become a <see cref="MorphDriver"/>.
/// Fields, not a nested object, because StudioDocument saves a feature's public param fields by
/// reflection; each feature declares its own and hands them here.
/// </summary>
public static class ShapeKeyDriver
{
	public static readonly string[] Channels = { "Rotation", "Swing", "Distance" };
	public static readonly string[] Axes = { "X", "Y", "Z" };
	public static readonly string[] Eases = { "Smooth", "Linear", "Ease In", "Ease Out", "Stepped" };

	static readonly RigEase[] EaseValues = { RigEase.Smooth, RigEase.Linear, RigEase.EaseIn, RigEase.EaseOut, RigEase.Stepped };

	public static StringParam BoneParam() => new( "Driven by bone" );
	public static ChoiceParam ChannelParam() => new( "Reads", Channels );
	public static ChoiceParam AxisParam() => new( "Axis", Axes, 1 );
	public static StringParam OtherBoneParam() => new( "Other bone" );
	public static FloatParam RestParam() => new( "Rest", 0f, -360f, 360f, "°" );
	public static FloatParam TargetParam( float value = 90f ) => new( "Target", value, -360f, 360f, "°" );
	public static ChoiceParam EaseParam() => new( "Ease", Eases );
	public static BoolParam ClampParam() => new( "Clamp to range", true );

	public static int EaseIndex( RigEase ease ) => Math.Max( 0, Array.IndexOf( EaseValues, ease ) );

	/// <summary>The driver the fields describe, or null when no bone is named.</summary>
	public static MorphDriver From( string morph, StringParam bone, ChoiceParam channel, ChoiceParam axis, StringParam other,
		FloatParam rest, FloatParam target, ChoiceParam ease, BoolParam clamp )
	{
		if ( string.IsNullOrWhiteSpace( bone.Value ) )
			return null;

		return new MorphDriver
		{
			Morph = morph,
			Bone = bone.Value.Trim(),
			Channel = (DriverChannel)Math.Clamp( channel.Index, 0, 2 ),
			Axis = (DriverAxis)Math.Clamp( axis.Index, 0, 2 ),
			OtherBone = other.Value?.Trim() ?? "",
			Rest = rest.Value,
			Target = target.Value,
			Ease = EaseValues[Math.Clamp( ease.Index, 0, EaseValues.Length - 1 )],
			Clamp = clamp.Value,
		};
	}

	/// <summary>The key name rules both features share: Key name, else the feature's name with
	/// underscores, else the fallback.</summary>
	public static string KeyName( string key, string featureName, string fallback ) =>
		!string.IsNullOrWhiteSpace( key ) ? key.Trim()
		: !string.IsNullOrWhiteSpace( featureName ) ? string.Join( "_", featureName.Trim().Replace( "\"", "" ).Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		: fallback;
}

/// <summary>
/// Corrective Shape Key: sculpt a fix for how a joint deforms, in the pose where it goes wrong, and
/// have it fade in as the joint gets there - pose-space deformation.
///
/// It poses the body (its own weights, or a smooth bind to the rig when it has none) with the driver
/// bone turned to Target - or into one of the garment test poses (Sit, Crouch, …), measuring the
/// angle from it - and opens a key. Everything until End Shape Key shapes the POSED body: a Stroke,
/// Surface Detail, a Sculpt, a move. End Shape Key carries every sculpted vertex back to the bind
/// pose through the inverse of its own skinning (<see cref="PoseSpaceCorrective"/>), stores that as the
/// key, and puts the body back at bind. The key goes out with its driver: the DMX bake writes the
/// driven weight on every frame of a clip, and the exported .ctrlrig tells Marionette's player to
/// set it live.
/// </summary>
public sealed class CorrectiveShapeKeyFeature : Feature, IShapeKeyStart
{
	public override string TypeName => "Corrective Shape Key";

	public override GeometryKind Accepts => GeometryKind.Body;

	public static readonly string[] PoseChoices = new[] { "Bone at target" }.Concat( GarmentPoses.All.Select( p => p.Name ) ).ToArray();

	public readonly StringParam Key = new( "Key name" );
	public readonly BodySelectionParam Bodies = new( "Bodies" );

	public readonly StringParam Bone = ShapeKeyDriver.BoneParam();
	public readonly ChoiceParam Channel = ShapeKeyDriver.ChannelParam();
	public readonly ChoiceParam Axis = ShapeKeyDriver.AxisParam();
	public readonly StringParam OtherBone = ShapeKeyDriver.OtherBoneParam();
	public readonly FloatParam RestAngle = ShapeKeyDriver.RestParam();
	public readonly FloatParam TargetAngle = ShapeKeyDriver.TargetParam();
	public readonly ChoiceParam Ease = ShapeKeyDriver.EaseParam();
	public readonly BoolParam Clamp = ShapeKeyDriver.ClampParam();

	/// <summary>Which pose the key is sculpted in: the driver bone at Target, or a test pose whose
	/// angle on the driver bone becomes Target (Axis picked as the one it turns about most).</summary>
	public readonly ChoiceParam Pose = new( "Sculpt in pose", PoseChoices );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Key, Bodies, Bone, Channel, Axis, OtherBone, RestAngle, TargetAngle, Ease, Clamp, Pose };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { OtherBone, Clamp };

	public string KeyName => ShapeKeyDriver.KeyName( Key.Value, Name, "corrective" );

	BodySelectionParam IShapeKeyStart.KeyBodies => Bodies;
	StringParam IShapeKeyStart.KeyNameParam => Key;

	public MorphDriver Driver => ShapeKeyDriver.From( KeyName, Bone, Channel, Axis, OtherBone, RestAngle, TargetAngle, Ease, Clamp );

	/// <summary>The driver as it is after a test pose measured it (Axis and Target from the pose).
	/// Null until the feature has run.</summary>
	public MorphDriver Measured { get; private set; }

	protected override void Execute( FeatureContext ctx )
	{
		Measured = null;
		var name = KeyName;

		if ( string.IsNullOrWhiteSpace( name ) || name.Any( c => char.IsWhiteSpace( c ) || c == '"' ) )
		{
			Fail(
				"The corrective key needs a name",
				string.IsNullOrWhiteSpace( name ) ? "A morph is addressed by name and this one has none." : $"'{name}' has spaces or quotes, which a morph name cannot.",
				"Set Key name, e.g. key=knee_fix" );
		}

		var rig = ctx.Rig;

		if ( rig is not { Count: > 0 } )
		{
			Fail(
				"A corrective shape key needs a rig",
				"It is sculpted in a pose and driven by a bone, and this part has no bones.",
				"Place bones in the Rig workspace, or import a rigged model first" );
		}

		if ( string.IsNullOrWhiteSpace( Bone.Value ) )
			FailOn( Bone.Label, "Pick the bone that drives the key", "The key fades in as this bone turns, and none is named.", "Set Driven by bone, e.g. bone=calf_L" );

		if ( rig.IndexOf( Bone.Value.Trim() ) < 0 )
			FailOn( Bone.Label, $"Bone '{Bone.Value.Trim()}' is not in the rig", "The driver reads this bone's turn.", $"Use one of: {string.Join( ", ", rig.Bones.Take( 8 ).Select( b => b.Name ) )}{(rig.Count > 8 ? ", …" : "")}" );

		var driver = Driver;
		Skeleton posed;

		if ( Pose.Index <= 0 )
		{
			if ( driver.Channel == DriverChannel.Distance )
			{
				FailOn( Channel.Label,
					"A distance has no pose to sculpt in",
					"Corrective Shape Key turns the bone to Target to pose the body; a distance between two bones does not say how.",
					"Read Rotation or Swing, or pick a test pose in Sculpt in pose, or drive a plain Shape Key by Distance" );
			}

			if ( MathF.Abs( driver.Target - driver.Rest ) < 1e-3f )
				FailOn( TargetAngle.Label, "Rest and Target are the same angle", "The key is sculpted at Target and is off at Rest, so they must differ.", "Set Target to the angle where the joint looks wrong, e.g. 90" );

			posed = PoseSpaceCorrective.PosedFor( rig, driver );
		}
		else
		{
			var pose = GarmentPoses.All[Math.Clamp( Pose.Index - 1, 0, GarmentPoses.All.Count - 1 )];
			posed = rig.Clone();

			if ( GarmentPoses.Apply( posed, pose ) == 0 )
				FailOn( Pose.Label, $"The {pose.Name} pose moved no bones", "It turns bones by body region (arms, legs, torso), and none of this rig's bone names was recognised.", "Name the bones the usual way (thigh_L, upperarm_R, spine_1…), or use Bone at target" );

			if ( driver.Channel == DriverChannel.Distance )
			{
				var bind = PoseSpaceCorrective.World( rig );
				var world = PoseSpaceCorrective.World( posed );
				var other = rig.IndexOf( driver.OtherBone );

				if ( other < 0 )
					FailOn( OtherBone.Label, "A Distance driver needs a second bone", $"'{driver.OtherBone}' is not in the rig.", "Set Other bone" );

				var b = rig.IndexOf( driver.Bone );
				driver.Rest = (bind[b].Origin - bind[other].Origin).Length;
				driver.Target = (world[b].Origin - world[other].Origin).Length;
			}
			else
			{
				var measured = PoseSpaceCorrective.Measure( rig, posed, driver.Bone, name );

				if ( measured is null )
					FailOn( Bone.Label, $"{driver.Bone} does not turn in the {pose.Name} pose", "The key's weight is read off this bone, so it must move between bind and the pose.", "Pick a bone the pose bends (a thigh for Sit), or use Bone at target" );

				driver.Channel = DriverChannel.Rotation;
				driver.Axis = measured.Axis;
				driver.Rest = 0f;
				driver.Target = measured.Target;
			}
		}

		Measured = driver;
		var bindWorld = PoseSpaceCorrective.World( rig );
		var poseWorld = PoseSpaceCorrective.World( posed );

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;

			if ( mesh.RecordingMorph is { } open )
			{
				Fail(
					$"Shape key '{open.Name}' is still open on {body.Name}",
					"Keys are recorded one at a time, and the one before this has not been ended.",
					"Add End Shape Key before this one" );
			}

			// Its own weights when it has them; otherwise the smooth bind the export and Test start
			// from. Weights painted later than this are not seen by the un-skin.
			var weights = mesh.IsRigged ? mesh.Skin : SkinBinder.BindSmooth( mesh, rig );
			var result = PoseSpaceCorrective.Pose( mesh.Positions, weights, bindWorld, poseWorld );
			var bindPositions = mesh.Positions.ToArray();

			for ( var i = 0; i < result.Posed.Length; i++ )
				mesh.Positions[i] = result.Posed[i];

			mesh.Morphs.Add( new MeshMorph
			{
				Name = name,
				Base = result.Posed.ToArray(),
				BindBase = bindPositions,
				Unskin = result.Unskin,
				Driver = driver.Clone(),
				Deltas = null,
			} );

			if ( result.Singular > 0 )
			{
				Warn(
					$"{result.Singular} vertices of {body.Name} cannot be un-skinned exactly",
					"Their bones pull in opposite directions at equal weight in this pose, so their skinning has no inverse; they follow their heaviest bone.",
					"Smooth the weights at that joint, or pose it less far" );
			}
		}
	}
}
