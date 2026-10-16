using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

/// <summary>
/// Whole-selection edits on keyframes: reverse, retime, offset, reduce.
///
/// Each takes the keys it should touch as (track, key) pairs - the timeline's selection, or every
/// key of a track for an agent driving a clip file - and edits them in place. Kept out of the
/// timeline so the MCP tools and the editor do exactly the same thing to a clip.
///
/// A KEY THAT LANDS ON AN OCCUPIED FRAME REPLACES WHAT WAS THERE. Moving a key onto another key of
/// the same track leaves one key, and the one you moved wins - the same rule a paste follows. Keys
/// you did not select are never moved, only replaced when something lands on them.
///
/// Every operation returns how many keys it moved or removed, so a caller can say "nothing to do"
/// rather than appearing to do nothing.
/// </summary>
public static class RigKeyOps
{
	/// <summary>
	/// Play the selected keys backwards: the span from the earliest selected frame to the latest is
	/// mirrored in time, on every track at once, so the relative timing between bones mirrors too.
	///
	/// The Thing's trick: animate a tentacle pulling IN (easy, it follows the body) and reverse it
	/// into a strike. Reversing is more than swapping frames - a key governs the segment LEAVING it,
	/// so each key takes the easing of the key that used to lead into it, ease in and ease out trade
	/// places, and tangents swap sides and change sign.
	/// </summary>
	public static int Reverse( IEnumerable<(BoneTrack Track, BoneKeyframe Key)> keys )
	{
		var groups = Group( keys );

		if ( groups.Count == 0 )
			return 0;

		var first = groups.Min( g => g.Value[0].Frame );
		var last = groups.Max( g => g.Value[^1].Frame );

		if ( first == last )
			return 0;

		var moved = 0;

		foreach ( var (track, selected) in groups )
		{
			var count = selected.Count;
			var easing = selected.Select( k => k.Interpolation ).ToArray();
			var tangentIn = selected.Select( k => k.TangentIn ).ToArray();
			var tangentOut = selected.Select( k => k.TangentOut ).ToArray();

			for ( var i = 0; i < count; i++ )
			{
				var key = selected[i];

				// After reversing, this key leads into the one that used to come before it - the
				// segment the previous key used to govern. The first key (now last) has nothing
				// before it, and keeps the mode the old last key left the range with.
				var from = i > 0 ? i - 1 : count - 1;
				key.Interpolation = Flip( easing[from] );

				key.TangentIn = Negated( tangentOut[i] );
				key.TangentOut = Negated( tangentIn[i] );
				key.Frame = first + last - key.Frame;
				moved++;
			}

			Settle( track, selected );
		}

		return moved;
	}

	/// <summary>
	/// Stretch or squeeze the selected keys in time around <paramref name="pivot"/> (the earliest
	/// selected frame when null). 2 makes the move take twice as long; 0.5 half as long.
	///
	/// Frames are whole numbers, so a squeeze can land two keys on one frame; the later of them is
	/// kept. Tangents are slopes per frame, so they are divided by the factor and the curve keeps
	/// its shape at the new speed.
	/// </summary>
	public static int Retime( IEnumerable<(BoneTrack Track, BoneKeyframe Key)> keys, float factor, int? pivot = null )
	{
		if ( !(factor > 0f) || MathF.Abs( factor - 1f ) < 1e-4f )
			return 0;

		var groups = Group( keys );

		if ( groups.Count == 0 )
			return 0;

		var origin = pivot ?? groups.Min( g => g.Value[0].Frame );
		var moved = 0;

		foreach ( var (track, selected) in groups )
		{
			foreach ( var key in selected )
			{
				var frame = Math.Max( origin + (int)MathF.Round( (key.Frame - origin) * factor ), 0 );

				key.TangentIn = Scaled( key.TangentIn, 1f / factor );
				key.TangentOut = Scaled( key.TangentOut, 1f / factor );

				if ( frame != key.Frame )
					moved++;

				key.Frame = frame;
			}

			Settle( track, selected );
		}

		return moved;
	}

	/// <summary>
	/// Shift each track's selected keys by its own number of frames - <paramref name="framesFor"/>
	/// asked once per track. Frames below zero are clamped to zero.
	///
	/// This is overlap: give a chain the same pose on every bone, then offset each bone by its depth
	/// below the first one, and the motion rolls down the chain root first, tip last.
	/// </summary>
	public static int Offset( IEnumerable<(BoneTrack Track, BoneKeyframe Key)> keys, Func<BoneTrack, int> framesFor )
	{
		var moved = 0;

		foreach ( var (track, selected) in Group( keys ) )
		{
			var by = framesFor?.Invoke( track ) ?? 0;

			if ( by == 0 )
				continue;

			foreach ( var key in selected )
			{
				key.Frame = Math.Max( key.Frame + by, 0 );
				moved++;
			}

			Settle( track, selected );
		}

		return moved;
	}

	/// <summary>
	/// Remove the selected keys the curve does not need: a key goes when the track, without it,
	/// still passes within <paramref name="positionTolerance"/> units and
	/// <paramref name="angleTolerance"/> degrees of where it was on every frame of the span. The
	/// first and last key of each track's selection always stay.
	///
	/// For a dense take - a baked secondary pass, a captured pose every frame - that should become
	/// something a person can edit. Removed keys are not selected any more; the caller drops them.
	/// </summary>
	public static int Reduce( IEnumerable<(BoneTrack Track, BoneKeyframe Key)> keys,
		float positionTolerance = 0.05f, float angleTolerance = 0.5f, float scaleTolerance = 0.005f )
	{
		var removed = 0;

		foreach ( var (track, selected) in Group( keys ) )
		{
			if ( selected.Count < 3 )
				continue;

			var from = selected[0].Frame;
			var to = selected[^1].Frame;
			var original = new Transform[to - from + 1];

			for ( var f = from; f <= to; f++ )
				original[f - from] = track.Evaluate( f );

			for ( var i = 1; i < selected.Count - 1; i++ )
			{
				var key = selected[i];
				var at = track.Keyframes.IndexOf( key );

				if ( at <= 0 || at >= track.Keyframes.Count - 1 )
					continue;

				track.Keyframes.RemoveAt( at );

				// The gap changes, and so do the segments either side of it: Cubic and Monotone take
				// their tangents from the neighbouring keys.
				var lo = Math.Max( track.Keyframes[Math.Max( at - 2, 0 )].Frame, from );
				var hi = Math.Min( track.Keyframes[Math.Min( at + 1, track.Keyframes.Count - 1 )].Frame, to );
				var fits = true;

				for ( var f = lo; f <= hi && fits; f++ )
					fits = Near( track.Evaluate( f ), original[f - from], positionTolerance, angleTolerance, scaleTolerance );

				if ( fits )
				{
					removed++;
					continue;
				}

				track.Keyframes.Insert( at, key );
			}
		}

		return removed;
	}

	/// <summary>
	/// Close a loop: every track in <paramref name="tracks"/> that has keys gets a key at
	/// <paramref name="lastFrame"/> equal to its first key (same easing), so the clip ends in the pose
	/// it starts in and plays around without a jump. A track keyed past <paramref name="lastFrame"/>
	/// loses those keys - they are outside the loop. Returns how many tracks were closed.
	/// </summary>
	public static int MakeLoop( IEnumerable<BoneTrack> tracks, int lastFrame )
	{
		if ( tracks is null || lastFrame < 1 )
			return 0;

		var closed = 0;

		foreach ( var track in tracks )
		{
			if ( track?.Keyframes is not { Count: > 0 } keys )
				continue;

			keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
			var first = keys[0];

			if ( first.Frame >= lastFrame )
				continue;

			keys.RemoveAll( k => k.Frame > lastFrame );
			track.SetKeyframe( lastFrame, first.Local );

			if ( keys.FirstOrDefault( k => k.Frame == lastFrame ) is { } end )
			{
				end.Interpolation = first.Interpolation;
				end.TangentIn = first.TangentIn is null ? null : new List<float>( first.TangentIn );
				end.TangentOut = first.TangentOut is null ? null : new List<float>( first.TangentOut );
			}

			closed++;
		}

		return closed;
	}

	/// <summary>
	/// Write <paramref name="sample"/> into <paramref name="track"/> as a key on every
	/// <paramref name="step"/>th frame from <paramref name="from"/> to <paramref name="to"/> (both
	/// ends always), replacing any keys already in the span. Monotone by default: dense keys from a
	/// solver must not overshoot between samples. Returns how many keys were written.
	/// </summary>
	public static int Bake( BoneTrack track, int from, int to, Func<int, Transform> sample,
		int step = 1, KeyInterpolation mode = KeyInterpolation.Monotone )
	{
		if ( track is null || sample is null || to < from )
			return 0;

		step = Math.Max( step, 1 );
		track.Keyframes.RemoveAll( k => k.Frame >= from && k.Frame <= to );

		var written = 0;

		for ( var f = from; ; f = Math.Min( f + step, to ) )
		{
			track.SetKeyframe( f, sample( f ) );

			if ( track.Keyframes.FirstOrDefault( k => k.Frame == f ) is { } key )
			{
				key.Interpolation = mode;
				key.TangentIn = null;
				key.TangentOut = null;
			}

			written++;

			if ( f >= to )
				break;
		}

		return written;
	}

	// --- helpers ---------------------------------------------------------------------------------

	/// <summary>Selected keys by track, each list in frame order, keys that are no longer in their
	/// track dropped (an undo replaced the clip under the selection).</summary>
	private static List<KeyValuePair<BoneTrack, List<BoneKeyframe>>> Group( IEnumerable<(BoneTrack Track, BoneKeyframe Key)> keys )
	{
		if ( keys is null )
			return new();

		return keys
			.Where( k => k.Track is not null && k.Key is not null && k.Track.Keyframes.Contains( k.Key ) )
			.GroupBy( k => k.Track )
			.Select( g => new KeyValuePair<BoneTrack, List<BoneKeyframe>>( g.Key,
				g.Select( k => k.Key ).Distinct().OrderBy( k => k.Frame ).ToList() ) )
			.ToList();
	}

	/// <summary>After moving <paramref name="moved"/>: any other key sharing a frame with one of them
	/// goes, then the track is put back in frame order. Among moved keys that collide, the one
	/// latest in <paramref name="moved"/> stays.</summary>
	private static void Settle( BoneTrack track, List<BoneKeyframe> moved )
	{
		var winner = new Dictionary<int, BoneKeyframe>();

		foreach ( var key in moved )
			winner[key.Frame] = key;

		track.Keyframes.RemoveAll( k => winner.TryGetValue( k.Frame, out var kept ) && kept != k );
		track.Keyframes.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
	}

	private static KeyInterpolation Flip( KeyInterpolation mode ) => mode switch
	{
		KeyInterpolation.EaseIn => KeyInterpolation.EaseOut,
		KeyInterpolation.EaseOut => KeyInterpolation.EaseIn,
		_ => mode
	};

	private static List<float> Negated( List<float> tangent ) =>
		tangent?.Select( v => -v ).ToList();

	private static List<float> Scaled( List<float> tangent, float by ) =>
		tangent?.Select( v => v * by ).ToList();

	private static bool Near( Transform a, Transform b, float position, float degrees, float scale ) =>
		a.Position.Distance( b.Position ) <= position
		&& Rotation.Difference( a.Rotation, b.Rotation ).Angle() <= degrees
		&& (a.Scale - b.Scale).Length <= scale;
}
