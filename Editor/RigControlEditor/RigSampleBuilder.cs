using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// Generates the bundled example clip: a first-person reach out and flip a switch.
///
/// WHY THIS IS GENERATED RATHER THAN HAND-AUTHORED. Poses are produced by running the same
/// two-bone IK solver the tool uses, against world-space targets for the hand. That sidesteps the
/// thing that makes writing an animation in code unreliable - nobody knows which local axis of
/// arm_upper_R is "forward" without testing, so any hand-picked Euler angles are a coin flip.
/// Saying "put the hand HERE" needs no such knowledge, and it's how an animator thinks anyway.
///
/// The four beats are laid out on the timeline exactly as the tutorial teaches them, so the
/// example and the lesson agree.
/// </summary>
internal static class RigSampleBuilder
{
	private const string ModelPath = "models/first_person/first_person_arms_preview.vmdl";
	private const string SwitchPath = "models/lightswitch/lightswitch_plate.vmdl";
	private const string OutputPath = "animations/reach_and_flip_switch.riganim";

	/// <summary>Frames for each beat. Deliberately tight - the whole action is under a second at
	/// 30fps, which is what a real reach-and-press takes.</summary>
	private const int FrameRest = 0;
	private const int FrameAnticipate = 6;
	private const int FrameReach = 14;
	private const int FrameContact = 19;
	private const int FrameSettle = 22;
	private const int FrameReturn = 28;

	// CONSOLE ONLY, NO [Menu]. This regenerates a clip that ships as an example, which is a
	// thing this repo does to itself and not a thing a person who installed Geppetto has any use
	// for — it was putting a "Marionette" menu in their editor whose every entry was scaffolding
	// for someone else's tests.
	[ConCmd( "rig_build_sample" )]
	public static void Build()
	{
		var model = Model.Load( ModelPath );

		if ( model?.Bones is null )
		{
			Log.Error( $"[sample] couldn't load {ModelPath}" );
			return;
		}

		var scene = Scene.CreateEditorScene();

		try
		{
			using var scope = scene.Push();

			var renderer = new GameObject( true, "sample" ).GetOrAddComponent<SkinnedModelRenderer>( false );
			renderer.Model = model;
			renderer.UseAnimGraph = false;
			renderer.Enabled = true;

			scene.EditorTick( 0f, 1f / 60f );

			if ( model.Bones.GetBone( "hand_R" ) is not { } hand )
			{
				Log.Error( "[sample] no hand_R on this model" );
				return;
			}

			if ( !renderer.TryGetBoneTransform( hand, out var handRest ) )
			{
				Log.Error( "[sample] couldn't read the rest pose" );
				return;
			}

			// Everything is placed RELATIVE TO THE REST POSE, not at absolute coordinates, so the
			// numbers still mean something if the model changes.
			//
			// Source convention: +x forward, +y left, +z up. The right hand starts low and close,
			// then reaches forward-up-and-left toward a switch in front of the eye.
			var rest = handRest.Position + new Vector3( 0f, 0f, -6f );
			var switchAt = handRest.Position + new Vector3( 14f, 6f, 10f );

			var poses = new (int Frame, Vector3 Target, string Label)[]
			{
				(FrameRest, rest, "rest - hand low, just out of frame"),
				(FrameAnticipate, rest + new Vector3( -2f, -1f, -2f ), "anticipation - wind back and down"),
				(FrameReach, switchAt, "extreme - hand at the switch"),
				(FrameContact, switchAt, "contact - held"),
				(FrameSettle, switchAt + new Vector3( 2f, 0f, 1f ), "settle - drift just past"),
				(FrameReturn, rest, "return - back to rest")
			};

			var doc = new RigAnimDocument
			{
				SourceModel = model,
				AnimationSpeed = 30,
				FrameCount = 34,
				ReferenceProps = new List<ReferenceProp>
				{
					new()
					{
						Name = "light switch",
						Model = Model.Load( SwitchPath ),
						Position = switchAt,
						Rotation = new Angles( 0f, 180f, 0f ),
						Scale = 1f
					}
				}
			};

			foreach ( var (frame, target, label) in poses )
			{
				if ( !RigConstraintSolver.TrySolveTwoBone( renderer, hand, target, Vector3.Up, out var chain ) )
				{
					Log.Warning( $"[sample] frame {frame} ({label}): solve failed" );
					continue;
				}

				// The chain's own solved transforms are the parents for the bones below it.
				// Reading the parent back off the renderer gives its BIND pose, not its solved
				// one, so each bone would be measured against a parent that hadn't moved - which
				// shows up as a bone's parent-space POSITION drifting between keyframes. Bones
				// don't change length; if position moves, the parent is wrong.
				var solved = new Dictionary<string, Transform>();

				foreach ( var (bone, world) in chain )
					solved[bone.Name] = world;

				foreach ( var (bone, world) in chain )
				{
					// Keyframes are parent-space; the solver works in world.
					var parentWorld = bone.Parent is { } parent
						? (solved.TryGetValue( parent.Name, out var solvedParent )
							? solvedParent
							: renderer.TryGetBoneTransform( parent, out var p ) ? p : renderer.WorldTransform)
						: renderer.WorldTransform;

					var track = doc.GetOrAddTrack( bone.Name );
					track.SetKeyframe( frame, parentWorld.ToLocal( world ) );

					// Contact snaps rather than eases - it's a switch, not a wave.
					if ( frame == FrameContact && track.Keyframes.FirstOrDefault( k => k.Frame == frame ) is { } key )
						key.Interpolation = KeyInterpolation.Stepped;
				}

				Log.Info( $"[sample] frame {frame,2}: {label}" );
			}

			// CreateResource takes an ABSOLUTE filename, not an asset-relative path - handed a
			// relative one it resolves against the sbox install directory and throws.
			if ( Project.Current?.GetAssetsPath() is not { } assetsPath )
			{
				Log.Error( "[sample] no current project" );
				return;
			}

			var absolute = System.IO.Path.Combine( assetsPath, OutputPath.Replace( '/', System.IO.Path.DirectorySeparatorChar ) );

			System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( absolute ) );

			var asset = AssetSystem.CreateResource( "riganim", absolute );

			if ( asset is null )
			{
				Log.Error( $"[sample] couldn't create {absolute}" );
				return;
			}

			asset.SaveToDisk( doc );

			Log.Info( $"[sample] wrote {OutputPath} - {doc.BoneTracks.Count} tracks, " +
				$"{doc.BoneTracks.Sum( t => t.Keyframes.Count )} keyframes" );
		}
		catch ( Exception e )
		{
			Log.Error( $"[sample] threw: {e}" );
		}
		finally
		{
			scene.Destroy();
		}
	}
}
