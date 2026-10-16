using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Keys from targets: say where a hand (or any IK end) should be on which frames, in the model's
/// space, and get keys - instead of hand-picking Euler angles for a rig whose axes nobody measured.
///
/// TWO WAYS TO KEY A TARGET:
///   - <see cref="Goals"/>: IK goal keys (and IK weight keys) for a live constraint, converted into
///     whatever space the clip keys that goal in. The solve happens at playback.
///   - <see cref="Solve"/>: the constraint solved now, on the clip's own pose at each frame, and the
///     chain written as ordinary FK keys - for a rig with no live constraint, or a pose to polish by
///     hand afterwards.
///
/// <see cref="Morph"/> keys a shape key's weight curve (a Morph track) the same way.
///
/// Marionette's marionette_key MCP tool does the same to a RigAnimDocument; the headless
/// <c>effigy.sh key</c> and the clip generators call this.
/// </summary>
public static class ClipKeys
{
	public sealed record Target( int Frame, Vec3 Position, Quat? Rotation, RigEase Ease, float? Weight );

	public sealed class Result
	{
		public int Keys;
		public List<string> Notes = new();
		public string Error;
		public bool Ok => Error is null;

		public override string ToString() => Error ?? $"{Keys} key(s)" + (Notes.Count > 0 ? "; " + string.Join( "; ", Notes ) : "");
	}

	/// <summary>
	/// Targets as text, ';' between them: <c>frame:x,y,z[:pitch,yaw,roll][:ease][:w=0..1]</c> -
	/// position in model space (inches), rotation as s&amp;box Angles in degrees, ease a
	/// KeyInterpolation name (Smooth, Linear, Stepped, EaseIn, EaseOut, Cubic, Monotone), w an IK
	/// weight to key on the same frame. Null and an error on anything it cannot read.
	/// </summary>
	public static List<Target> Parse( string text, RigEase defaultEase, out string error )
	{
		error = null;
		var list = new List<Target>();

		foreach ( var item in (text ?? "").Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var parts = item.Split( ':' );

			if ( parts.Length < 2 || !int.TryParse( parts[0], out var frame ) || Floats( parts[1], 3 ) is not { } p )
			{
				error = $"'{item}' is not frame:x,y,z";
				return null;
			}

			Quat? rotation = null;
			var ease = defaultEase;
			float? weight = null;

			foreach ( var part in parts.Skip( 2 ) )
			{
				if ( part.StartsWith( "w=" ) && float.TryParse( part[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var w ) )
					weight = Math.Clamp( w, 0f, 1f );
				else if ( Floats( part, 3 ) is { } a )
					rotation = Quat.FromAngles( a[0], a[1], a[2] );
				else if ( Enum.TryParse<RigEase>( part.Replace( " ", "" ), true, out var e ) )
					ease = e;
				else
				{
					error = $"'{part}' in '{item}' is not a rotation, an ease or w=";
					return null;
				}
			}

			list.Add( new Target( frame, new Vec3( p[0], p[1], p[2] ), rotation, ease, weight ) );
		}

		if ( list.Count == 0 )
			error = "no targets";

		return list.Count == 0 ? null : list.OrderBy( t => t.Frame ).ToList();
	}

	/// <summary>
	/// IK goal keys for <paramref name="bone"/>'s constraint at each target (rotation kept from the
	/// goal's current value where a target gives none), in the goal's own space, plus IK weight keys
	/// where a target gives one.
	/// </summary>
	public static Result Goals( RigAnimFile clip, string bone, IEnumerable<Target> targets )
	{
		var result = new Result();

		if ( string.IsNullOrWhiteSpace( bone ) ) { result.Error = "no bone"; return result; }

		var goal = Track( clip, bone, RigTrackKind.IkGoal );

		foreach ( var t in targets )
		{
			var current = clip.IkGoalAt( bone, t.Frame ) ?? RigPose.Identity;
			var model = new RigPose( t.Position, t.Rotation ?? current.Rotation, Vec3.One );
			Set( goal, t.Frame, ToGoalSpace( clip, bone, model, t.Frame ), t.Ease );
			result.Keys++;

			if ( t.Weight is { } w )
			{
				Set( Track( clip, bone, RigTrackKind.IkWeight ), t.Frame, new RigPose( Vec3.Zero, Quat.Identity, new Vec3( w, w, w ) ), t.Ease );
				result.Keys++;
			}
		}

		if ( clip.Ik is not null && !clip.Ik.Any( c => c.Enabled && c.Live && c.EndBone == bone ) )
			result.Notes.Add( $"no live IK constraint ends at {bone} in the rig - the goal keys do nothing until one does" );

		return result;
	}

	/// <summary>
	/// <paramref name="ik"/> solved toward each target on the clip's own pose at that frame (its keys
	/// and live IK as they play), the chain written as FK keys - the end bone too when a target turns
	/// it. Keys after the first change the pose the next target is solved from, so targets go in
	/// frame order.
	/// </summary>
	public static Result Solve( RigAnimFile clip, Skeleton skeleton, RigIk ik, IEnumerable<Target> targets )
	{
		var result = new Result();

		if ( ik is null || skeleton.IndexOf( ik.EndBone ) < 0 ) { result.Error = $"the model has no bone '{ik?.EndBone}'"; return result; }

		var chain = new List<int> { skeleton.IndexOf( ik.EndBone ) };

		for ( var k = 0; k < Math.Max( ik.ChainLength, 1 ); k++ )
		{
			var parent = skeleton.Bones[chain[0]].Parent;

			if ( parent < 0 ) { result.Error = $"{ik.EndBone} has fewer than {ik.ChainLength} bones above it"; return result; }

			chain.Insert( 0, parent );
		}

		var solver = ik.Weighted( 1f );
		solver.Weight = 1f;
		solver.Enabled = true;

		foreach ( var t in targets.OrderBy( t => t.Frame ) )
		{
			solver.GoalRotation = t.Rotation is not null;
			var local = clip.SampleLocal( skeleton, t.Frame );
			var world = RigAnimFile.ModelSpace( skeleton, local );

			if ( ChainIk.Apply( skeleton, world, solver, new RigPose( t.Position, t.Rotation ?? Quat.Identity, Vec3.One ), clip.Limits ) is not { } solved )
			{
				result.Notes.Add( $"frame {t.Frame}: the chain could not be solved" );
				continue;
			}

			var after = RigAnimFile.LocalSpace( skeleton, solved );
			var miss = (solved[chain[^1]].Origin - t.Position).Length;

			if ( miss > 0.25f )
				result.Notes.Add( $"frame {t.Frame}: {ik.EndBone} ends {miss:0.##} from the target - out of reach" );

			foreach ( var b in t.Rotation is null ? chain.Take( chain.Count - 1 ) : chain )
			{
				var track = clip.Find( skeleton.Bones[b].Name ) ?? Track( clip, skeleton.Bones[b].Name, RigTrackKind.Bone );
				var scale = track.Keys.Count > 0 ? track.Evaluate( t.Frame ).Scale : Vec3.One;
				var pose = RigPose.FromXform( after[b] );
				Set( track, t.Frame, new RigPose( pose.Position, pose.Rotation, scale ), t.Ease );
				result.Keys++;
			}
		}

		if ( ClipBake.LiveIkBones( clip, skeleton ).Overlaps( chain.Select( b => skeleton.Bones[b].Name ) ) )
			result.Notes.Add( "a live IK constraint also poses this chain - it overrides these FK keys at playback" );

		return result;
	}

	/// <summary>A morph (shape key) weight on a frame: 0 the base shape, 1 the full key.</summary>
	public sealed record MorphTarget( int Frame, float Weight, RigEase Ease );

	/// <summary>
	/// Morph keys as text, ';' between them: <c>frame:weight[:ease]</c> - weight 0 (base shape) to 1
	/// (the full shape key), ease a KeyInterpolation name. Null and an error on anything it cannot read.
	/// </summary>
	public static List<MorphTarget> ParseMorph( string text, RigEase defaultEase, out string error )
	{
		error = null;
		var list = new List<MorphTarget>();

		foreach ( var item in (text ?? "").Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var parts = item.Split( ':' );

			if ( parts.Length < 2 || parts.Length > 3 || !int.TryParse( parts[0], out var frame ) || frame < 0
				|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight ) || !float.IsFinite( weight ) )
			{
				error = $"'{item}' is not frame:weight[:ease] (frame a whole number from 0, weight a number such as 0.5)";
				return null;
			}

			var ease = defaultEase;

			if ( parts.Length == 3 && !Enum.TryParse( parts[2].Replace( " ", "" ), true, out ease ) )
			{
				error = $"'{parts[2]}' in '{item}' is not an ease: Smooth, Linear, Stepped, EaseIn, EaseOut, Cubic or Monotone";
				return null;
			}

			list.Add( new MorphTarget( frame, weight, ease ) );
		}

		if ( list.Count == 0 )
			error = "no keys: give frame:weight[:ease], ';' between them, e.g. \"0:0;12:1:EaseOut;24:0\"";

		return list.Count == 0 ? null : list.OrderBy( t => t.Frame ).ToList();
	}

	/// <summary>
	/// Keys on <paramref name="morph"/>'s Morph track (TrackKind Morph: the weight rides in the key's
	/// scale, as Marionette's RigAnimDocument.MorphKey stores it), replacing keys already on those
	/// frames. <paramref name="modelMorphs"/>, when known, are the model's shape key names: a name
	/// not among them is keyed anyway (the model may gain it) but said, since until then the keys
	/// drive nothing. Marionette's marionette_key_morph does the same to a RigAnimDocument.
	/// </summary>
	public static Result Morph( RigAnimFile clip, string morph, IEnumerable<MorphTarget> keys, ICollection<string> modelMorphs = null )
	{
		var result = new Result();
		morph = morph?.Trim();

		if ( string.IsNullOrWhiteSpace( morph ) ) { result.Error = "no morph name: say which shape key, e.g. morph=sac_swell"; return result; }
		if ( morph.Any( char.IsWhiteSpace ) ) { result.Error = $"'{morph}' has spaces; a shape key name has none, so nothing could match it"; return result; }

		var list = keys?.ToList() ?? new List<MorphTarget>();

		if ( list.Count == 0 ) { result.Error = "no keys"; return result; }

		var track = Track( clip, morph, RigTrackKind.Morph );

		foreach ( var k in list )
		{
			Set( track, k.Frame, new RigPose( Vec3.Zero, Quat.Identity, new Vec3( k.Weight, k.Weight, k.Weight ) ), k.Ease );
			result.Keys++;
		}

		if ( list.Where( k => k.Weight < 0f || k.Weight > 1f ) is var outside && outside.Any() )
			result.Notes.Add( $"weight(s) {string.Join( ", ", outside.Select( k => k.Weight.ToString( "0.###", CultureInfo.InvariantCulture ) ) )} are outside 0-1; a shape key is authored for 0 (base) to 1 (full), so check them in the engine" );

		foreach ( var e in clip.MorphEvents.Where( e => e.Morph == morph && list.Any( k => k.Frame >= e.Start && k.Frame <= e.End ) ) )
			result.Notes.Add( $"a morph event holds {morph} over frames {e.Start:0}-{e.End:0} and overrides these keys there" );

		if ( modelMorphs is not null && !modelMorphs.Contains( morph ) )
			result.Notes.Add( $"the model has no shape key '{morph}' ({(modelMorphs.Count == 0 ? "it has none" : "it has " + string.Join( ", ", modelMorphs.Take( 8 ) ))}) - these keys do nothing until it does" );

		return result;
	}

	/// <summary>A model-space goal as the key its track stores: through the goal's object when it is
	/// keyed on one. RigAnimDocument.IkGoalKey on a kernel clip.</summary>
	public static RigPose ToGoalSpace( RigAnimFile clip, string bone, RigPose model, float frame )
	{
		if ( !clip.GoalSpaces.TryGetValue( bone, out var anchor ) || anchor.Space != RigGoalSpace.Object || clip.PlacementAt( anchor.Object, frame ) is not { } placed )
			return model;

		var key = placed.ToXform().Inverse * model.ToXform();
		return new RigPose( key.Origin, Quat.FromXform( key ), Quat.ScaleOf( key ) );
	}

	static RigTrack Track( RigAnimFile clip, string name, RigTrackKind kind )
	{
		var track = clip.Tracks.FirstOrDefault( t => t.Kind == kind && t.Name == name );

		if ( track is null )
		{
			track = new RigTrack { Name = name, Kind = kind };
			clip.Tracks.Add( track );
		}

		return track;
	}

	/// <summary>A key on a frame, replacing one already there; the track stays in frame order.</summary>
	public static void Set( RigTrack track, int frame, RigPose pose, RigEase ease )
	{
		track.Keys.RemoveAll( k => k.Frame == frame );
		var at = track.Keys.FindIndex( k => k.Frame > frame );
		var key = new RigKey { Frame = frame, Local = pose, Ease = ease };

		if ( at < 0 ) track.Keys.Add( key ); else track.Keys.Insert( at, key );
	}

	static float[] Floats( string text, int count )
	{
		var parts = text.Split( ',' );

		if ( parts.Length != count )
			return null;

		var values = new float[count];

		for ( var i = 0; i < count; i++ )
		{
			if ( !float.TryParse( parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i] ) )
				return null;
		}

		return values;
	}
}
