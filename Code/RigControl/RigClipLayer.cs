using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

/// <summary>
/// Clip-on-clip layering: one clip summed onto another.
///
/// Two ways in, the same maths. MergeLayer BAKES the sum into the base clip as keys - what the
/// export, the take check and the viewport all read, so a layered haul is checked and shipped as
/// one clip. RigAnimPlayerComponent.LayerOver plays the layer LIVE over another player in game, for
/// when the two have to stay separate (an idle pulse that keeps going whatever the body is doing).
///
/// TIME: base frame f plays the layer at (f - Offset) × Rate. A looping layer repeats over the
/// whole base - its last keyed frame is its first again, as Make Loop leaves it - so five cable
/// loops of 37, 41, 43... frames summed never line up. A one-shot layer only counts inside its own
/// span and adds nothing outside it.
///
/// Kernel twin: Effigy.ClipLayer (the headless sim's layer= option); keep the two in step.
/// </summary>
public static class RigClipLayer
{
	public sealed class Settings
	{
		/// <summary>Overlay or Additive. Exclusive is not a layer and is read as Overlay.</summary>
		public RigAnimMode Mode = RigAnimMode.Additive;
		public float Weight = 1f;
		public int Offset;
		public float Rate = 1f;
		public bool Loop;

		/// <summary>Track names to take from the layer; "cable1_*" matches a prefix. Empty = all.</summary>
		public List<string> Mask = new();

		/// <summary>Copy the layer's marks across, shifted (and repeated, when looping).</summary>
		public bool Marks = true;
	}

	public sealed class Result
	{
		public int Tracks, Keys, Marks;
		public List<string> Notes = new();
		public string Error;
		public bool Ok => Error is null;

		public override string ToString() => Error ?? $"layered {Tracks} track(s), {Keys} key(s), {Marks} mark(s)"
			+ (Notes.Count > 0 ? "; " + string.Join( "; ", Notes ) : "");
	}

	/// <summary>
	/// Sum <paramref name="layer"/> onto <paramref name="clip"/> in place: each layered track becomes
	/// a Monotone key per frame, then reduced. <paramref name="bindOf"/> gives the rest pose of a bone
	/// the base does not key (what an additive layer is added to); without it such bones are skipped.
	/// </summary>
	public static Result MergeLayer( RigAnimDocument clip, RigAnimDocument layer, Settings settings, Func<string, Transform?> bindOf = null )
	{
		var result = new Result();
		settings ??= new Settings();

		if ( clip is null || layer is null ) { result.Error = "no clip"; return result; }
		if ( clip == layer ) { result.Error = "a clip cannot be layered onto itself"; return result; }

		var additive = settings.Mode == RigAnimMode.Additive;
		var layerSpan = LastKey( layer );
		var rate = settings.Rate > 0f ? settings.Rate : 1f;
		var weight = settings.Weight.Clamp( 0f, 1f );

		if ( layerSpan < 1 ) { result.Error = "the layer has no motion - key at least two frames"; return result; }

		var span = Math.Max( LastKey( clip ), 1 );

		if ( !settings.Loop )
			span = Math.Max( span, settings.Offset + (int)MathF.Ceiling( layerSpan / rate ) );

		foreach ( var top in layer.BoneTracks.Where( t => t.Keyframes.Count > 0
			&& t.Target is TrackTarget.Bone or TrackTarget.IkGoal or TrackTarget.IkWeight ).ToList() )
		{
			if ( settings.Mask is { Count: > 0 } && !settings.Mask.Any( m => Glob( top.BoneName, m ) ) )
				continue;

			var under = clip.BoneTracks.FirstOrDefault( t => t.Target == top.Target && t.BoneName == top.BoneName );
			Func<int, Transform> baseAt;

			if ( under is { Keyframes.Count: > 0 } )
			{
				var keyed = under;
				baseAt = f => keyed.Evaluate( f );
			}
			else if ( top.Target == TrackTarget.Bone && bindOf?.Invoke( top.BoneName ) is { } bind )
				baseAt = _ => bind;
			else if ( top.Target == TrackTarget.IkWeight )
				baseAt = _ => Transform.Zero;
			else if ( top.Target == TrackTarget.IkGoal && !additive )
				baseAt = null;
			else
			{
				result.Notes.Add( top.Target == TrackTarget.IkGoal
					? $"{top.BoneName}: additive IK goal with no goal under it, skipped"
					: $"{top.BoneName}: no such bone, skipped" );
				continue;
			}

			if ( top.Target == TrackTarget.IkGoal && SpaceOf( clip, top.BoneName ) != SpaceOf( layer, top.BoneName ) )
			{
				result.Notes.Add( $"{top.BoneName}: IK goal keyed in a different space in each clip, skipped" );
				continue;
			}

			var reference = top.Evaluate( top.Keyframes.Min( k => k.Frame ) );
			var poses = new Transform[span + 1];

			for ( var f = 0; f <= span; f++ )
			{
				var lf = LayerFrame( f, settings.Offset, rate, layerSpan, settings.Loop );

				if ( baseAt is null )
				{
					poses[f] = top.Evaluate( lf ?? ((f - settings.Offset) * rate).Clamp( 0f, layerSpan ) );
					continue;
				}

				var below = baseAt( f );
				poses[f] = lf is { } at ? Compose( below, reference, top.Evaluate( at ), weight, additive ) : below;
			}

			under ??= clip.GetOrAddTrack( top.BoneName, top.Target );
			var written = RigKeyOps.Bake( under, 0, span, f => poses[f] );
			var keys = under.Keyframes.Where( k => k.Frame <= span ).Select( k => (under, k) ).ToList();
			result.Keys += written - RigKeyOps.Reduce( keys, 0.02f, 0.25f );
			result.Tracks++;
		}

		if ( settings.Marks && layer.Marks is { Count: > 0 } )
			result.Marks = CopyMarks( clip, layer, settings, rate, layerSpan, span );

		if ( result.Tracks == 0 && result.Notes.Count == 0 )
			result.Error = settings.Mask is { Count: > 0 } ? "no layer track matches the mask" : "the layer keys nothing to merge";

		return result;
	}

	/// <summary>The layer frame that base frame <paramref name="f"/> plays, or null where a one-shot
	/// layer is not playing. A loop repeats every <paramref name="span"/> frames both ways.</summary>
	public static float? LayerFrame( float f, float offset, float rate, float span, bool loop )
	{
		var t = (f - offset) * rate;

		if ( loop )
		{
			if ( span <= 0f )
				return 0f;

			t %= span;
			return t < 0f ? t + span : t;
		}

		return t < -1e-4f || t > span + 1e-4f ? null : t.Clamp( 0f, span );
	}

	/// <summary>
	/// One bone with the layer on it. Overlay: lerp toward the layer by weight. Additive: the
	/// layer's motion away from <paramref name="reference"/> (its first key) on top - position adds,
	/// rotation composes in the bone's own frame (a spine kick bends the same way standing or
	/// crouched), scale multiplies, so a 4 % sac pulse layered over a haul still pulses.
	/// </summary>
	public static Transform Compose( Transform below, Transform reference, Transform layer, float weight, bool additive )
	{
		if ( !additive )
			return new Transform(
				Vector3.Lerp( below.Position, layer.Position, weight ),
				Rotation.Slerp( below.Rotation, layer.Rotation, weight ),
				Vector3.Lerp( below.Scale, layer.Scale, weight ) );

		var delta = Rotation.Slerp( Rotation.Identity, reference.Rotation.Inverse * layer.Rotation, weight );
		var ratio = new Vector3( Ratio( layer.Scale.x, reference.Scale.x ), Ratio( layer.Scale.y, reference.Scale.y ), Ratio( layer.Scale.z, reference.Scale.z ) );

		return new Transform(
			below.Position + (layer.Position - reference.Position) * weight,
			below.Rotation * delta,
			below.Scale * (Vector3.One + (ratio - Vector3.One) * weight) );
	}

	/// <summary>"cable1_*" style match: '*' is any run of characters. MotionLint.Glob's rule.</summary>
	public static bool Glob( string name, string pattern )
	{
		if ( string.IsNullOrEmpty( pattern ) ) return false;
		if ( pattern == "*" ) return true;
		if ( !pattern.Contains( '*' ) ) return name == pattern;

		var parts = pattern.Split( '*' );
		var at = 0;

		for ( var i = 0; i < parts.Length; i++ )
		{
			if ( parts[i].Length == 0 ) continue;
			var found = name.IndexOf( parts[i], at, StringComparison.Ordinal );
			if ( found < 0 || (i == 0 && found != 0) ) return false;
			at = found + parts[i].Length;
		}

		return parts[^1].Length == 0 || name.EndsWith( parts[^1], StringComparison.Ordinal );
	}

	/// <summary>A model's rest pose by bone name, in parent space - what a layered bone the base
	/// does not key is added to. Bone.LocalTransform is model space despite its name; the parent's
	/// undoes it.</summary>
	public static Func<string, Transform?> BindOf( Model model ) => name =>
	{
		if ( model?.Bones?.GetBone( name ) is not { } bone )
			return null;

		return bone.Parent is { } parent ? parent.LocalTransform.ToLocal( bone.LocalTransform ) : bone.LocalTransform;
	};

	private static float Ratio( float value, float reference ) => MathF.Abs( reference ) > 1e-6f ? value / reference : 1f;

	private static int LastKey( RigAnimDocument doc ) =>
		doc.BoneTracks.SelectMany( t => t.Keyframes ).Select( k => k.Frame ).DefaultIfEmpty( 0 ).Max();

	private static IkGoalSpace SpaceOf( RigAnimDocument doc, string bone ) => doc.FindIkGoalAnchor( bone )?.Space ?? IkGoalSpace.Model;

	private static int CopyMarks( RigAnimDocument clip, RigAnimDocument layer, Settings s, float rate, int layerSpan, int span )
	{
		clip.Marks ??= new List<RigMark>();
		var added = 0;
		var period = layerSpan / rate;

		foreach ( var mark in layer.Marks.Where( m => m is not null ) )
		{
			var first = s.Offset + mark.Frame / rate;

			if ( s.Loop )
			{
				// Every repeat inside the base, the first one possibly before the offset.
				while ( first - period >= 0f ) first -= period;
				while ( first < 0f ) first += period;
			}

			for ( var at = first; at <= span + 1e-4f; at += period )
			{
				var frame = (int)MathF.Round( at );

				if ( !clip.Marks.Any( m => m is not null && m.Name == mark.Name && m.Frame == frame ) )
				{
					clip.Marks.Add( new RigMark { Name = mark.Name, Frame = frame, Note = mark.Note } );
					added++;
				}

				if ( !s.Loop || period <= 0f )
					break;
			}
		}

		return added;
	}
}
