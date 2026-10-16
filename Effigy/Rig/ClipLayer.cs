using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>How a layer clip lands on the clip under it. Matches Marionette's RigAnimMode minus
/// Exclusive, which is not a layer.</summary>
public enum RigLayerMode
{
	/// <summary>The layer's pose replaces the base's, by <see cref="ClipLayer.Settings.Weight"/>.</summary>
	Overlay,

	/// <summary>The layer's motion away from its first key is added on top of the base: position
	/// adds, rotation composes in the bone's own frame, scale multiplies.</summary>
	Additive
}

/// <summary>
/// Clip-on-clip layering, baked: one clip summed onto another, the result written as keys on the
/// base. Marionette's RigKeyOps.MergeLayer on kernel clips, for the headless sim and generators.
///
/// WHY BAKED. Five cables each on their own short loop, an idle pulse over a haul: the sum is what
/// the export, the take check and the editor have to see, and all of them read one clip. Baking the
/// sum keeps every one of them working unchanged; the in-game player can also layer live (see
/// RigAnimPlayerComponent.LayerOver) when the layers have to stay separate at runtime.
///
/// TIME: base frame f plays the layer at (f - Offset) * Rate. A looping layer repeats over the whole
/// base (its last keyed frame is its first again, as Make Loop leaves it); a one-shot layer only
/// counts inside its own span and adds nothing outside it.
///
/// Every touched bone becomes a Monotone key per frame, then reduced to the keys the curve needs -
/// the same as a secondary bake. Marks, events and morphs stay the base's.
/// </summary>
public static class ClipLayer
{
	public sealed class Settings
	{
		public RigLayerMode Mode = RigLayerMode.Additive;
		public float Weight = 1f;
		public int Offset;
		public float Rate = 1f;
		public bool Loop;

		/// <summary>Track names to take from the layer, MotionLint globs ("cable1_*"); empty = all.</summary>
		public List<string> Mask = new();
	}

	public sealed class Result
	{
		public int Tracks, Keys;
		public List<string> Notes = new();
		public string Error;
		public bool Ok => Error is null;
	}

	/// <summary>
	/// Sum <paramref name="layer"/> onto <paramref name="clip"/> in place. <paramref name="skeleton"/>
	/// gives the bind pose for a bone the base does not key (the pose the layer is added to); without
	/// it such bones are skipped.
	/// </summary>
	public static Result Merge( RigAnimFile clip, RigAnimFile layer, Settings settings, Skeleton skeleton = null )
	{
		var result = new Result();
		settings ??= new Settings();

		if ( layer is null || clip is null ) { result.Error = "no clip"; return result; }

		var layerSpan = layer.LastKeyFrame;
		var rate = settings.Rate > 0f ? settings.Rate : 1f;
		var weight = Math.Clamp( settings.Weight, 0f, 1f );

		if ( layerSpan < 1 && layer.Tracks.All( t => t.Keys.Count <= 1 ) ) { result.Error = "the layer has no motion"; return result; }

		// Base frames to write: the base's length, or as far as a one-shot layer reaches.
		var span = Math.Max( clip.LastKeyFrame, 1 );

		if ( !settings.Loop )
			span = Math.Max( span, settings.Offset + (int)MathF.Ceiling( layerSpan / rate ) );

		foreach ( var top in layer.Tracks.Where( t => t.Keys.Count > 0 && t.Kind is RigTrackKind.Bone or RigTrackKind.IkGoal or RigTrackKind.IkWeight ) )
		{
			if ( settings.Mask.Count > 0 && !MotionLint.Matches( top.Name, settings.Mask ) )
				continue;

			var under = clip.Tracks.FirstOrDefault( t => t.Kind == top.Kind && t.Name == top.Name );
			Func<int, RigPose> baseAt;

			if ( under is { Keys.Count: > 0 } )
			{
				var keyed = under;
				baseAt = f => keyed.Evaluate( f );
			}
			else if ( top.Kind == RigTrackKind.Bone && skeleton?.IndexOf( top.Name ) is >= 0 and var bone )
			{
				var bind = RigPose.FromXform( skeleton.Bones[bone].Local );
				baseAt = _ => bind;
			}
			else if ( top.Kind == RigTrackKind.IkWeight )
				baseAt = _ => RigPose.Identity;
			else if ( top.Kind == RigTrackKind.IkGoal && settings.Mode == RigLayerMode.Overlay )
				baseAt = null;
			else
			{
				result.Notes.Add( top.Kind == RigTrackKind.IkGoal
					? $"{top.Name}: additive IK goal with no goal under it, skipped"
					: $"{top.Name}: no such bone, skipped" );
				continue;
			}

			if ( top.Kind == RigTrackKind.IkGoal && Space( clip, top.Name ) != Space( layer, top.Name ) )
			{
				result.Notes.Add( $"{top.Name}: IK goal keyed in a different space in each clip, skipped" );
				continue;
			}

			var reference = top.Keys[0].Local;
			var poses = new RigPose[span + 1];

			for ( var f = 0; f <= span; f++ )
			{
				var lf = LayerFrame( f, settings.Offset, rate, layerSpan, settings.Loop );

				// A goal the base never keyed is the layer's alone: held at its ends outside the span.
				if ( baseAt is null )
				{
					poses[f] = top.Evaluate( lf ?? Math.Clamp( (f - settings.Offset) * rate, 0f, layerSpan ) );
					continue;
				}

				var below = baseAt( f );
				poses[f] = lf is { } at ? Compose( below, reference, top.Evaluate( at ), weight, settings.Mode ) : below;
			}

			if ( under is null )
			{
				under = new RigTrack { Name = top.Name, Kind = top.Kind };
				clip.Tracks.Add( under );
			}

			under.Keys.RemoveAll( k => k.Frame <= span );
			under.Keys.InsertRange( 0, poses.Select( ( p, f ) => new RigKey { Frame = f, Local = p, Ease = RigEase.Monotone } ) );
			result.Keys += span + 1 - ClipBake.Reduce( under, 0, span );
			result.Tracks++;
		}

		if ( result.Tracks == 0 && result.Notes.Count == 0 )
			result.Error = settings.Mask.Count > 0 ? "no layer track matches the mask" : "the layer keys nothing to merge";

		return result;
	}

	/// <summary>The layer frame base frame <paramref name="f"/> plays, or null where a one-shot layer
	/// is not playing. A loop repeats every <paramref name="span"/> frames in both directions.</summary>
	public static float? LayerFrame( int f, int offset, float rate, int span, bool loop )
	{
		var t = (f - offset) * rate;

		if ( loop )
		{
			if ( span <= 0 )
				return 0f;

			t %= span;
			return t < 0f ? t + span : t;
		}

		return t < -1e-4f || t > span + 1e-4f ? null : Math.Clamp( t, 0f, span );
	}

	/// <summary>One bone's pose with the layer on it - RigAnimPlayerComponent's AddOnto and Overlay
	/// lerp, so a baked layer and a live one agree.</summary>
	public static RigPose Compose( RigPose below, RigPose reference, RigPose layer, float weight, RigLayerMode mode )
	{
		if ( mode == RigLayerMode.Overlay )
			return RigPose.Lerp( below, layer, weight );

		var delta = Quat.Slerp( Quat.Identity, (reference.Rotation.Inverse * layer.Rotation).Normalized, weight );
		var ratio = new Vec3( Ratio( layer.Scale.x, reference.Scale.x ), Ratio( layer.Scale.y, reference.Scale.y ), Ratio( layer.Scale.z, reference.Scale.z ) );

		return new RigPose(
			below.Position + (layer.Position - reference.Position) * weight,
			(below.Rotation * delta).Normalized,
			new Vec3( below.Scale.x * Lerp1( ratio.x, weight ), below.Scale.y * Lerp1( ratio.y, weight ), below.Scale.z * Lerp1( ratio.z, weight ) ) );
	}

	static float Ratio( float value, float reference ) => MathF.Abs( reference ) > 1e-6f ? value / reference : 1f;

	static float Lerp1( float ratio, float weight ) => 1f + (ratio - 1f) * weight;

	static RigGoalSpace Space( RigAnimFile clip, string bone ) =>
		clip.GoalSpaces.TryGetValue( bone, out var anchor ) ? anchor.Space : RigGoalSpace.Model;
}
