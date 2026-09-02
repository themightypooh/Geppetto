using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// Generates a wave for a mounted Halo character.
///
/// SOLVED, NOT HAND-AUTHORED, for the same reason RigSampleBuilder is: nobody knows which local
/// axis of r_upperarm points along the bone without testing, so any Euler angles picked by hand
/// are a coin flip. Saying "put the end of the forearm HERE" needs no such knowledge.
///
/// The grunt's arm is exactly three bones - r_clavicle, r_upperarm, r_forearm - and there is no
/// hand or finger below it. That's a two-bone chain with the forearm as the effector, which is
/// precisely what TrySolveTwoBone wants, so the wave is authored by moving the end of the forearm
/// and letting the shoulder and elbow follow.
///
/// TARGETS ARE DERIVED FROM THE SKELETON, NOT TYPED IN. The raised position is measured as a
/// fraction of the arm's own reach from the shoulder, so this produces a sane wave on any of the
/// mounted characters whose arms are named this way - elite, brute, marine - not only the grunt,
/// despite them being wildly different sizes.
/// </summary>
internal static class RigWaveBuilder
{
	private const string DefaultModel = "models/halo/grunt.vmdl";

	private const string EndBone = "r_forearm";

	/// <summary>30fps. Tight enough to read as a greeting rather than a stretch - a real wave is
	/// about two beats per second, and the most common mistake is doing it half that speed.</summary>
	private const int Fps = 30;

	private const int FrameRest = 0;
	private const int FrameWindUp = 6;
	private const int FrameRaised = 16;
	private const int FrameOut1 = 24;
	private const int FrameIn1 = 32;
	private const int FrameOut2 = 40;
	private const int FrameIn2 = 48;
	private const int FrameDown = 62;
	private const int FrameCount = 70;

	[ConCmd( "rig_build_wave" )]
	public static void Build( string modelPath = DefaultModel )
	{
		var path = string.IsNullOrWhiteSpace( modelPath ) ? DefaultModel : modelPath;
		var model = Model.Load( path );

		if ( model?.Bones is null )
		{
			Log.Error( $"[wave] couldn't load {path}" );
			return;
		}

		var scene = Scene.CreateEditorScene();

		try
		{
			using var scope = scene.Push();

			var renderer = new GameObject( true, "wave" ).GetOrAddComponent<SkinnedModelRenderer>( false );
			renderer.Model = model;
			renderer.UseAnimGraph = false;
			renderer.Enabled = true;

			// Bone writes only land on a tick - see RigViewport.ApplyWorldTransform. Reading the
			// rest pose before this returns nothing useful.
			//
			// ONE TICK IS NOT ALWAYS ENOUGH. RigSampleBuilder gets away with a single tick on the
			// first-person arms, and the same single tick returns nothing for the mounted Halo
			// models - the bone array isn't populated until the renderer has been through setup,
			// and how many ticks that takes is not ours to know. So it ticks until the bones read
			// back, rather than ticking a fixed number of times and hoping.
			scene.EditorTick( 0f, 1f / 60f );

			if ( model.Bones.GetBone( EndBone ) is not { } end )
			{
				Log.Error( $"[wave] no {EndBone} on this model - bones are: " +
					string.Join( ", ", model.Bones.AllBones.Select( b => b.Name ) ) );
				return;
			}

			if ( end.Parent is not { } mid || mid.Parent is not { } root )
			{
				Log.Error( $"[wave] {EndBone} isn't three bones deep - can't solve a chain" );
				return;
			}

			if ( !WaitForBones( scene, renderer, end, mid, root, out var endRest, out var midRest, out var rootRest ) )
			{
				Log.Error( $"[wave] couldn't read the rest pose of {root.Name} / {mid.Name} / {end.Name} " +
					$"after {MaxSetupTicks} ticks. The model loaded and the bones exist, so this is the " +
					$"renderer not having built its bone array - not a naming problem." );
				return;
			}

			var shoulder = rootRest.Position;
			var reach = (midRest.Position - rootRest.Position).Length
				+ (endRest.Position - midRest.Position).Length;

			if ( reach <= 0.01f )
			{
				Log.Error( "[wave] arm has no length" );
				return;
			}

			// Source convention: +x forward, +y left, +z up. This is the RIGHT arm, so "out" is -y.
			//
			// 0.92 of full reach, never 1.0 - at full extension the elbow's bend plane is undefined
			// and it snaps to an arbitrary side. The solver clamps this itself, but asking for a
			// pose it has to rescue is a good way to get one you didn't design.
			Vector3 Raised( float outward, float lift ) =>
				shoulder + new Vector3( 0.30f, outward, lift ).Normal * (reach * 0.92f);

			// The two ends of the wave. Same height, different splay - a wave is a rotation at the
			// elbow, so the hand swings sideways rather than up and down.
			var waveOut = Raised( -0.62f, 0.72f );
			var waveIn = Raised( -0.14f, 0.86f );

			// Anticipation: the arm dips before it lifts. Small - this is a greeting, not a throw.
			var windUp = endRest.Position + new Vector3( -0.04f, -0.04f, -0.10f ) * reach;

			var poses = new (int Frame, Vector3 Target, string Label)[]
			{
				(FrameRest, endRest.Position, "rest - arm at the side"),
				(FrameWindUp, windUp, "anticipation - dip before the lift"),
				(FrameRaised, waveOut, "raised - arm up and out"),
				(FrameOut1, waveIn, "wave in"),
				(FrameIn1, waveOut, "wave out"),
				(FrameOut2, waveIn, "wave in"),
				(FrameIn2, waveOut, "wave out"),
				(FrameDown, endRest.Position, "back to rest")
			};

			var doc = new RigAnimDocument
			{
				SourceModel = model,
				AnimationSpeed = Fps,
				FrameCount = FrameCount
			};

			foreach ( var (frame, target, label) in poses )
			{
				// The pole spans the bend plane with the chain direction. Forward, not up or down:
				// the arm is raised, so the chain direction is close to vertical, and a pole that's
				// also vertical makes the cross product collapse and the elbow pick a side at
				// random. Forward stays perpendicular through the whole motion.
				if ( !RigConstraintSolver.TrySolveTwoBone( renderer, end, target, Vector3.Forward, out var chain ) )
				{
					Log.Warning( $"[wave] frame {frame} ({label}): solve failed" );
					continue;
				}

				// The chain's own solved transforms are the parents for the bones below them.
				// Reading a parent back off the renderer gives its REST pose, not its solved one -
				// which shows up as a bone's parent-space position drifting between keyframes.
				// Bones don't change length; if position moves, the parent was wrong.
				var solved = chain.ToDictionary( c => c.Bone.Name, c => c.World );

				foreach ( var (bone, world) in chain )
				{
					var parentWorld = bone.Parent is { } parent
						? (solved.TryGetValue( parent.Name, out var solvedParent )
							? solvedParent
							: renderer.TryGetBoneTransform( parent, out var p ) ? p : renderer.WorldTransform)
						: renderer.WorldTransform;

					doc.GetOrAddTrack( bone.Name ).SetKeyframe( frame, parentWorld.ToLocal( world ) );
				}

				Log.Info( $"[wave] frame {frame,2}: {label}" );
			}

			// CreateResource takes an ABSOLUTE filename - handed a relative one it resolves against
			// the sbox install directory and throws.
			if ( Project.Current?.GetAssetsPath() is not { } assetsPath )
			{
				Log.Error( "[wave] no current project" );
				return;
			}

			var name = System.IO.Path.GetFileNameWithoutExtension( path );
			var output = $"animations/{name}_wave.riganim";
			var absolute = System.IO.Path.Combine( assetsPath, output.Replace( '/', System.IO.Path.DirectorySeparatorChar ) );

			System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( absolute ) );

			var asset = AssetSystem.CreateResource( "riganim", absolute );

			if ( asset is null )
			{
				Log.Error( $"[wave] couldn't create {absolute}" );
				return;
			}

			asset.SaveToDisk( doc );

			Log.Info( $"[wave] wrote {output} - {doc.BoneTracks.Count} tracks, " +
				$"{doc.BoneTracks.Sum( t => t.Keyframes.Count )} keyframes" );
		}
		catch ( Exception e )
		{
			Log.Error( $"[wave] threw: {e}" );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>Generous - these are cheap, and the cost of being one tick short is a command that
	/// looks broken.</summary>
	private const int MaxSetupTicks = 32;

	/// <summary>
	/// Ticks the scene until all three chain bones report a transform.
	///
	/// Polling rather than a fixed count because the number of ticks a renderer needs before its
	/// bone array exists is an engine detail, and one that clearly differs per model - the same
	/// single tick that works for the first-person arms returns nothing for the mounted Halo
	/// characters.
	/// </summary>
	private static bool WaitForBones( Scene scene, SkinnedModelRenderer renderer,
		BoneCollection.Bone end, BoneCollection.Bone mid, BoneCollection.Bone root,
		out Transform endRest, out Transform midRest, out Transform rootRest )
	{
		endRest = midRest = rootRest = default;

		for ( var tick = 0; tick < MaxSetupTicks; tick++ )
		{
			if ( renderer.TryGetBoneTransform( end, out endRest )
				&& renderer.TryGetBoneTransform( mid, out midRest )
				&& renderer.TryGetBoneTransform( root, out rootRest ) )
			{
				// A bone that reports an identity transform hasn't been posed yet, it's just been
				// allocated - and every bone of a real skeleton sits somewhere other than the
				// origin. Waiting for a non-degenerate chain avoids solving against zeros.
				if ( (midRest.Position - rootRest.Position).Length > 0.001f
					&& (endRest.Position - midRest.Position).Length > 0.001f )
				{
					if ( tick > 0 )
						Log.Info( $"[wave] bones became readable after {tick + 1} ticks" );

					return true;
				}
			}

			scene.EditorTick( (tick + 1) / 60f, 1f / 60f );
		}

		return false;
	}

	[Menu( "Editor", "Marionette/Build Grunt Wave", "waving_hand" )]
	public static void BuildDefault() => Build( DefaultModel );
}
