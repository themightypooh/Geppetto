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

	/// <summary>
	/// The two assumptions PropagateToDescendants stands on, checked against a real model.
	///
	/// Children do not follow a posed parent by themselves - SetBoneTransform pins each bone in
	/// WORLD space, so a bone with an override stops inheriting. RigViewport carries the hierarchy
	/// by hand instead, and that code is only correct if:
	///
	///   1. AllBones lists every parent before its children. PropagateToDescendants makes a single
	///      pass and skips any bone whose parent it hasn't resolved yet, with no second pass - so
	///      one out-of-order bone silently drops its whole subtree, and those bones stay put while
	///      everything around them moves.
	///
	///   2. BindPoseFor returns a PARENT-SPACE transform. It is built as
	///      parent.LocalTransform.ToLocal( bone.LocalTransform ), which is only right if
	///      Bone.LocalTransform is model-space despite the name. If it is already parent-relative,
	///      every un-keyframed descendant gets its parent's transform subtracted twice and lands
	///      somewhere arbitrary - following, but to the wrong place.
	///
	/// Prints one line per failure and a PASS line if neither is broken.
	/// </summary>
	[ConCmd( "rig_test_hierarchy" )]
	public static void TestHierarchy( string modelPath = "models/citizen/citizen.vmdl" )
	{
		var model = Model.Load( modelPath );

		if ( model?.Bones is null )
		{
			Log.Error( $"[rigprobe] could not load '{modelPath}' or it has no bones" );
			return;
		}

		var order = model.Bones.AllBones.ToList();
		var seen = new System.Collections.Generic.HashSet<string>();
		var outOfOrder = new System.Collections.Generic.List<string>();

		foreach ( var bone in order )
		{
			if ( bone.Parent is { } parent && !seen.Contains( parent.Name ) )
				outOfOrder.Add( $"{bone.Name} (parent {parent.Name})" );

			seen.Add( bone.Name );
		}

		Log.Info( $"[rigprobe] {modelPath}: {order.Count} bones, " +
			$"{order.Count( b => b.Parent is null )} roots, parent-first={outOfOrder.Count == 0}" );

		if ( outOfOrder.Count > 0 )
			Log.Error( $"[rigprobe] {outOfOrder.Count} bones listed BEFORE their parent - " +
				$"PropagateToDescendants drops these and everything under them: " +
				$"{string.Join( ", ", outOfOrder.Take( 12 ) )}" );

		var scene = Scene.CreateEditorScene();

		try
		{
			using var scope = scene.Push();

			var renderer = new GameObject( true, "probe" ).GetOrAddComponent<SkinnedModelRenderer>( false );
			renderer.Model = model;
			renderer.UseAnimGraph = false;
			renderer.Enabled = true;

			Tick( scene );

			// The renderer at rest IS the bind pose, so its parent-space transforms are the answer
			// BindPoseFor is trying to compute. Any bone where the two disagree is a bone that will
			// be misplaced the moment its parent moves.
			var worst = 0f;
			var worstBone = "";
			var checked_ = 0;

			foreach ( var bone in order )
			{
				if ( bone.Parent is not { } parent )
					continue;

				if ( !renderer.TryGetBoneTransform( bone, out var world )
					|| !renderer.TryGetBoneTransform( parent, out var parentWorld ) )
					continue;

				var actual = parentWorld.ToLocal( world );
				var computed = parent.LocalTransform.ToLocal( bone.LocalTransform );
				var error = (actual.Position - computed.Position).Length;

				checked_++;

				if ( error > worst )
				{
					worst = error;
					worstBone = bone.Name;
				}
			}

			Log.Info( $"[rigprobe] BindPoseFor vs renderer rest pose over {checked_} bones: " +
				$"worst position error {worst:0.###} on '{worstBone}'" );

			if ( worst > 0.1f )
				Log.Error( "[rigprobe] BindPoseFor does NOT match the rest pose - Bone.LocalTransform is " +
					"parent-space already, so the extra ToLocal is subtracting the parent twice. " +
					"Un-keyframed children will follow a posed parent to the wrong place." );

			if ( outOfOrder.Count == 0 && worst <= 0.1f )
				Log.Info( "[rigprobe] PASS - hierarchy order and bind-pose conversion are both sound." );
		}
		catch ( Exception e )
		{
			Log.Error( $"[rigprobe] hierarchy probe threw: {e}" );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>
	/// Does posing a bone actually carry the bones under it? Rotates one bone, runs the same
	/// carry-the-hierarchy pass RigViewport.PropagateToDescendants runs, ticks, and reports how far
	/// each descendant actually moved.
	///
	/// A descendant that reports moved=0 did not follow. That is the whole question this answers,
	/// and it answers it without a window open.
	/// </summary>
	[ConCmd( "rig_test_follow" )]
	public static void TestFollow( string modelPath = "models/citizen/citizen.vmdl", string boneName = "arm_upper_R" )
	{
		var model = Model.Load( modelPath );

		if ( model?.Bones is null )
		{
			Log.Error( $"[rigprobe] could not load '{modelPath}' or it has no bones" );
			return;
		}

		if ( model.Bones.GetBone( boneName ) is not { } bone )
		{
			Log.Error( $"[rigprobe] no bone '{boneName}'. Bones: " +
				string.Join( ", ", model.Bones.AllBones.Take( 80 ).Select( b => b.Name ) ) );
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

			var before = new System.Collections.Generic.Dictionary<string, Transform>();

			foreach ( var b in model.Bones.AllBones )
			{
				if ( renderer.TryGetBoneTransform( b, out var w ) )
					before[b.Name] = w;
			}

			if ( !before.TryGetValue( boneName, out var start ) )
			{
				Log.Error( $"[rigprobe] could not read '{boneName}'" );
				return;
			}

			// A 45 degree yaw is large enough that no descendant can sit still by rounding.
			var posed = new Transform( start.Position, Rotation.FromYaw( 45f ) * start.Rotation, start.Scale );

			renderer.SetBoneTransform( bone, posed );

			// PropagateToDescendants, verbatim in shape: one pass, parent's NEW world from the map,
			// child's own local pose from the bind pose, skip anything whose parent isn't resolved.
			var resolved = new System.Collections.Generic.Dictionary<string, Transform> { [bone.Name] = posed };

			foreach ( var b in model.Bones.AllBones )
			{
				if ( b.Parent is not { } parent || !resolved.TryGetValue( parent.Name, out var parentWorld ) )
					continue;

				var local = b.Parent is { } p ? p.LocalTransform.ToLocal( b.LocalTransform ) : b.LocalTransform;
				var worldPose = parentWorld.ToWorld( local );

				resolved[b.Name] = worldPose;
				renderer.SetBoneTransform( b, worldPose );
			}

			Tick( scene );

			var descendants = resolved.Keys.Where( n => n != boneName ).ToList();
			var stuck = new System.Collections.Generic.List<string>();
			var moved = new System.Collections.Generic.List<string>();

			foreach ( var name in descendants )
			{
				if ( model.Bones.GetBone( name ) is not { } d || !renderer.TryGetBoneTransform( d, out var after ) )
					continue;

				var delta = (after.Position - before[name].Position).Length;

				(delta < 0.01f ? stuck : moved).Add( $"{name}={delta:0.##}" );
			}

			Log.Info( $"[rigprobe] posed '{boneName}' by 45deg on {modelPath}\n" +
				$"    resolved {descendants.Count} descendants, {moved.Count} moved, {stuck.Count} stuck" );

			if ( moved.Count > 0 )
				Log.Info( $"[rigprobe]   moved: {string.Join( ", ", moved.Take( 12 ) )}" );

			if ( stuck.Count > 0 )
				Log.Error( $"[rigprobe]   DID NOT FOLLOW: {string.Join( ", ", stuck.Take( 12 ) )}" );

			// Anything genuinely under this bone that the single pass never reached is the other
			// half of the failure - it is not stuck, it was never written at all.
			var expected = model.Bones.AllBones.Where( b => IsUnder( b, boneName ) ).Select( b => b.Name ).ToList();
			var missed = expected.Where( n => !resolved.ContainsKey( n ) ).ToList();

			if ( missed.Count > 0 )
				Log.Error( $"[rigprobe]   NEVER REACHED by the pass ({missed.Count}): {string.Join( ", ", missed.Take( 12 ) )}" );
			else
				Log.Info( $"[rigprobe]   the pass reached all {expected.Count} bones under '{boneName}'" );
		}
		catch ( Exception e )
		{
			Log.Error( $"[rigprobe] follow probe threw: {e}" );
		}
		finally
		{
			scene.Destroy();
		}
	}

	private static bool IsUnder( BoneCollection.Bone bone, string ancestor )
	{
		for ( var p = bone.Parent; p is not null; p = p.Parent )
		{
			if ( p.Name == ancestor )
				return true;
		}

		return false;
	}

	private static float _probeTime;

	private static void Tick( Scene scene )
	{
		_probeTime += 1f / 60f;
		scene.EditorTick( _probeTime, 1f / 60f );
	}
}
