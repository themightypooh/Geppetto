using Effigy;
using Marionette.EditorTools;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.Tools;

/// <summary>
/// Key from targets on an open clip: where an IK end should be on which frames (model space), as
/// live goal keys (mode goal) or solved now into the chain's FK keys (mode fk). The marionette_key
/// MCP tool and the timeline's Key From Targets menu both call this; the kernel's ClipKeys is the
/// headless twin (effigy.sh key).
/// </summary>
internal static class RigKeyFromTargets
{
	/// <summary>Apply <paramref name="targets"/> ("frame:x,y,z[:p,y,r][:ease][:w=];..") to
	/// <paramref name="doc"/>. Returns what happened; <paramref name="changed"/> is false when it
	/// refused (and nothing was touched).</summary>
	public static string Apply( RigAnimDocument doc, string bone, string targets, string mode, string ease = "Smooth", int chain = 0, string solver = "fabrik" )
		=> Apply( doc, bone, targets, mode, ease, chain, solver, out _ );

	public static string Apply( RigAnimDocument doc, string bone, string targets, string mode, string ease, int chain, string solver, out bool changed )
	{
		changed = true;

		if ( !Enum.TryParse<RigEase>( (ease ?? "").Replace( " ", "" ), true, out var defaultEase ) )
			return Fail( out changed, $"ease '{ease}' is not Smooth, Linear, Stepped, EaseIn, EaseOut, Cubic or Monotone." );

		if ( ClipKeys.Parse( targets, defaultEase, out var parseError ) is not { } list )
			return Fail( out changed, $"targets: {parseError}" );

		var constraint = doc.RigAsset?.IkConstraints?.FirstOrDefault( c => c is not null && c.TargetBone == bone );
		string message;

		switch ( (mode ?? "").Trim().ToLowerInvariant() )
		{
			case "goal":
			{
				if ( doc.SourceModel is not null && RigAnimExport.SkeletonFromModel( doc.SourceModel ).IndexOf( bone ) < 0 )
					return Fail( out changed, $"The model has no bone '{bone}'." );

				foreach ( var t in list )
				{
					var current = doc.IkGoalAt( bone, t.Frame );
					var rotation = t.Rotation is { } q ? new Rotation( q.x, q.y, q.z, q.w ) : current?.Rotation ?? Rotation.Identity;
					var goal = new Transform( new Vector3( t.Position.x, t.Position.y, t.Position.z ), rotation );
					var track = doc.GetOrAddIkGoalTrack( bone );
					track.SetKeyframe( t.Frame, doc.IkGoalKey( bone, goal, t.Frame ) );
					SetEase( track, t.Frame, t.Ease );

					if ( t.Weight is { } w )
					{
						doc.SetIkWeightKey( bone, t.Frame, w );
						SetEase( doc.FindIkWeightTrack( bone ), t.Frame, t.Ease );
					}
				}

				message = $"keyed {list.Count} goal(s) for {bone}"
					+ (list.Any( t => t.Weight is not null ) ? $" and {list.Count( t => t.Weight is not null )} weight(s)" : "")
					+ (constraint is not { Live: true, Enabled: true } ? $" - but no enabled live constraint ends at {bone}, so they do nothing until one does" : "");
				break;
			}

			case "fk":
			{
				if ( doc.SourceModel is null )
					return Fail( out changed, "The clip has no model - fk needs the skeleton." );

				var skeleton = RigAnimExport.SkeletonFromModel( doc.SourceModel );
				var ik = new RigIk
				{
					Name = constraint?.Name ?? "key",
					EndBone = bone,
					ChainLength = chain > 0 ? chain : constraint?.ChainLength ?? 2,
					Pole = constraint is null ? new Vec3( 0, 0, 1 ) : new Vec3( constraint.PoleDirection.x, constraint.PoleDirection.y, constraint.PoleDirection.z ),
					Iterations = Math.Max( constraint?.Iterations ?? 20, 10 ),
					Fabrik = (constraint?.Solver ?? Parse( solver )) == IkSolver.Fabrik,
					Spline = (constraint?.Solver ?? Parse( solver )) == IkSolver.Spline,
					MaxStretch = constraint?.MaxStretch ?? 1f,
				};

				if ( skeleton.IndexOf( bone ) < 0 )
					return Fail( out changed, $"The model has no bone '{bone}'." );

				var keys = 0;
				var notes = new List<string>();

				// One target at a time on the clip as it now stands - an earlier key changes the pose a
				// later target is solved from, the same as keying them by hand in order.
				foreach ( var t in list.OrderBy( t => t.Frame ) )
				{
					var clip = EffigyAnimExport.ToClip( doc, skeleton, "key", false, out _, out _ );
					var local = clip.Frames.Count == 0 ? RigSoftBind( skeleton ) : clip.Frames[Math.Clamp( t.Frame, 0, clip.Frames.Count - 1 )];
					var world = RigAnimFile.ModelSpace( skeleton, local );
					ik.GoalRotation = t.Rotation is not null;

					if ( ChainIk.Apply( skeleton, world, ik, new RigPose( t.Position, t.Rotation ?? Quat.Identity, Vec3.One ), EffigyAnimExport.Limits( doc ) ) is not { } solved )
					{
						notes.Add( $"frame {t.Frame}: could not solve" );
						continue;
					}

					var after = RigAnimFile.LocalSpace( skeleton, solved );
					var miss = (solved[skeleton.IndexOf( bone )].Origin - t.Position).Length;

					if ( miss > 0.25f )
						notes.Add( $"frame {t.Frame}: ends {miss:0.##} from the target, out of reach" );

					var b = skeleton.IndexOf( bone );
					var chainBones = new List<int>();

					if ( t.Rotation is not null )
						chainBones.Add( b );

					for ( var k = 0; k < ik.ChainLength && skeleton.Bones[b].Parent >= 0; k++ )
					{
						b = skeleton.Bones[b].Parent;
						chainBones.Add( b );
					}

					foreach ( var cb in chainBones )
					{
						var name = skeleton.Bones[cb].Name;
						var track = doc.GetOrAddTrack( name );
						var scale = track.Keyframes.Count > 0 ? track.Evaluate( t.Frame ).Scale : Vector3.One;
						track.SetKeyframe( t.Frame, RigSecondaryBake.ToTransform( after[cb], scale ) );
						SetEase( track, t.Frame, t.Ease );
						keys++;
					}
				}

				if ( constraint is { Live: true, Enabled: true } && doc.FindIkGoalTrack( bone ) is { Keyframes.Count: > 0 } )
					notes.Add( $"{constraint.Name} is live with goal keys - at playback it overrides these FK keys" );

				message = $"keyed {keys} FK key(s) on {bone}'s chain" + (notes.Count > 0 ? "; " + string.Join( "; ", notes ) : "");
				break;
			}

			default:
				return Fail( out changed, $"Unknown mode '{mode}'. Use goal or fk." );
		}

		return message;
	}

	static string Fail( out bool changed, string message )
	{
		changed = false;
		return message;
	}

	static IkSolver Parse( string solver ) => (solver ?? "").Trim().ToLowerInvariant() switch
	{
		"ccd" => IkSolver.Ccd,
		"spline" => IkSolver.Spline,
		_ => IkSolver.Fabrik,
	};

	static Xform[] RigSoftBind( Skeleton skeleton ) => skeleton.Bones.Select( b => b.Local ).ToArray();

	static void SetEase( BoneTrack track, int frame, RigEase ease )
	{
		if ( track?.Keyframes.FirstOrDefault( k => k.Frame == frame ) is { } key )
			key.Interpolation = (KeyInterpolation)(int)ease;
	}
}
