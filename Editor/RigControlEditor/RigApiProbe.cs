using Marionette;
using Sandbox;
using System;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// A canary for the one engine behaviour the whole tool stands on: that writing a bone transform
/// on a SkinnedModelRenderer actually sticks.
///
/// This exists because "posing does nothing" was, for a long time, indistinguishable from "the
/// write API is broken" - and the write API was fine. The real causes were that a
/// SceneRenderingWidget never ticks its scene, and that bone overrides only fold into the pose
/// during that tick. If an engine update breaks either, `rig_test_pose` says so in one command
/// instead of costing another session of guessing.
/// </summary>
internal static class RigApiProbe
{
	/// <summary>
	/// Writes a bone, reads it back immediately, ticks, reads again. Expected output is
	/// landed=False survived=True: the write is invisible in the same frame and correct after a
	/// tick. survived=False means bone overrides no longer stick and RigViewport's posing is dead.
	/// </summary>
	[ConCmd( "rig_test_pose" )]
	public static void TestPose( string modelPath = "models/citizen/citizen.vmdl" )
	{
		var model = Model.Load( modelPath );

		if ( model?.Bones is null )
		{
			Log.Error( $"[rigprobe] could not load '{modelPath}' or it has no bones" );
			return;
		}

		var scene = Scene.CreateEditorScene();

		try
		{
			using var scope = scene.Push();

			var go = new GameObject( true, "probe" );
			var renderer = go.GetOrAddComponent<SkinnedModelRenderer>( false );
			renderer.Model = model;
			renderer.UseAnimGraph = false;
			renderer.Enabled = true;

			var bone = model.Bones.GetBone( "head" )
				?? model.Bones.AllBones.Skip( 3 ).FirstOrDefault()
				?? model.Bones.AllBones.First();

			Tick( scene );

			if ( !renderer.TryGetBoneTransform( bone, out var before ) )
			{
				Log.Error( "[rigprobe] TryGetBoneTransform failed - the read API is broken" );
				return;
			}

			var target = new Transform( before.Position + Vector3.Up * 20f, before.Rotation, before.Scale );

			renderer.SetBoneTransform( bone, target );
			renderer.TryGetBoneTransform( bone, out var immediate );

			Tick( scene );
			renderer.TryGetBoneTransform( bone, out var afterTick );

			var landed = immediate.Position.AlmostEqual( target.Position, 0.01f );
			var survived = afterTick.Position.AlmostEqual( target.Position, 0.01f );

			Log.Info( $"[rigprobe] bone '{bone.Name}' on {modelPath}\n" +
				$"    before={before.Position} target={target.Position}\n" +
				$"    immediate={immediate.Position} landed={landed} (expected False - writes lag a tick)\n" +
				$"    afterTick={afterTick.Position} survived={survived} (expected True - posing works)" );

			if ( !survived )
				Log.Error( "[rigprobe] bone overrides no longer survive a tick - RigViewport posing is broken" );
		}
		catch ( Exception e )
		{
			Log.Error( $"[rigprobe] threw: {e}" );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>
	/// Verifies the two-bone IK actually puts the effector on the target, across a spread of
	/// reachable and unreachable goals. Unreachable ones should clamp to full extension rather
	/// than blow up or flip.
	/// </summary>
	[ConCmd( "rig_test_ik" )]
	public static void TestIk( string modelPath = "models/citizen/citizen.vmdl", string endBone = "hand_R" )
	{
		var model = Model.Load( modelPath );

		if ( model?.Bones is null )
		{
			Log.Error( $"[rigprobe] could not load '{modelPath}'" );
			return;
		}

		var scene = Scene.CreateEditorScene();

		try
		{
			using var scope = scene.Push();

			var renderer = new GameObject( true, "probe" ).GetOrAddComponent<SkinnedModelRenderer>( false );
			renderer.Model = model;
			renderer.UseAnimGraph = false;
			renderer.Enabled = true;

			Tick( scene );

			if ( model.Bones.GetBone( endBone ) is not { } end )
			{
				Log.Error( $"[rigprobe] no bone '{endBone}'. Bones: {string.Join( ", ", model.Bones.AllBones.Take( 40 ).Select( b => b.Name ) )}" );
				return;
			}

			if ( end.Parent?.Parent is null )
			{
				Log.Error( $"[rigprobe] '{endBone}' is not three bones deep - IK needs end/mid/root" );
				return;
			}

			renderer.TryGetBoneTransform( end.Parent.Parent, out var rootTx );
			renderer.TryGetBoneTransform( end, out var startTx );

			var reach = (end.Parent.Parent.Name, end.Parent.Name, end.Name);
			Log.Info( $"[rigprobe] IK chain: {reach.Item1} -> {reach.Item2} -> {reach.Item3}, effector at {startTx.Position}" );

			// Targets are placed as a fraction of the chain's reach, not as a blind offset from
			// the current pose - the Citizen's arm sits nearly straight in bind pose (~99% of
			// full extension), so "a bit further out" is already unreachable and clamps, which
			// reads as a solver failure when it isn't one.
			renderer.TryGetBoneTransform( end.Parent, out var midTx0 );

			var totalReach = (midTx0.Position - rootTx.Position).Length + (startTx.Position - midTx0.Position).Length;

			var targets = new[]
			{
				("bent (50% reach)", rootTx.Position + (startTx.Position - rootTx.Position).Normal * (totalReach * 0.5f)),
				("down (68% reach)", rootTx.Position + Vector3.Down * (totalReach * 0.68f)),
				("unreachable", rootTx.Position + Vector3.Forward * 500f)
			};

			foreach ( var (label, target) in targets )
			{
				if ( !RigConstraintSolver.TrySolveTwoBone( renderer, end, target, Vector3.Up, out var chain ) )
				{
					Log.Error( $"[rigprobe] IK '{label}': solve returned false" );
					continue;
				}

				var solvedEnd = chain[^1].World.Position;
				var error = (solvedEnd - target).Length;

				// Bone lengths must be preserved - an IK solve that stretches the limb is wrong
				// even when the effector lands exactly on target.
				var upper = (chain[1].World.Position - chain[0].World.Position).Length;
				var lower = (chain[2].World.Position - chain[1].World.Position).Length;

				Log.Info( $"[rigprobe] IK '{label}': target={target} solved={solvedEnd}\n" +
					$"    error={error:0.###} upperLen={upper:0.##} lowerLen={lower:0.##}" );
			}

			renderer.TryGetBoneTransform( end.Parent.Parent, out var r2 );
			renderer.TryGetBoneTransform( end.Parent, out var m2 );
			renderer.TryGetBoneTransform( end, out var e2 );

			Log.Info( $"[rigprobe] original lengths: upper={(m2.Position - r2.Position).Length:0.##} lower={(e2.Position - m2.Position).Length:0.##}" );
		}
		catch ( Exception e )
		{
			Log.Error( $"[rigprobe] IK threw: {e}" );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>Does BoneCollection.Bone carry its own bind/reference transform? If so the bind
	/// pose can be read from the model rather than snapshotted off a renderer that may already
	/// have been posed.</summary>
	[ConCmd( "rig_dump_boneinfo" )]
	public static void DumpBoneInfo()
	{
		var members = typeof( BoneCollection.Bone )
			.GetMembers( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance )
			.Select( m => $"{m.MemberType} {m.Name}" )
			.Distinct()
			.OrderBy( x => x );

		Log.Info( $"[rigprobe] BoneCollection.Bone: {string.Join( ", ", members )}" );
	}

	private static float _probeTime;

	private static void Tick( Scene scene )
	{
		_probeTime += 1f / 60f;
		scene.EditorTick( _probeTime, 1f / 60f );
	}
}
