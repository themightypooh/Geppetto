using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Marionette's Bake Secondary Motion on a <see cref="RigAnimFile"/>, for the headless sim: the
/// clip is sampled as it plays (live IK included), <see cref="ChainSpring"/> runs over it, and the
/// soft bones' tracks are replaced with a key per frame, then reduced to the keys the curve needs.
/// The editor's RigSecondaryBake does the same to a RigAnimDocument; the two share the springs and
/// the presets, and skip the same bones.
/// </summary>
public static class ClipBake
{
	public sealed class Result
	{
		public int Bones, Keys;
		public List<string> SkippedForIk = new();
		public List<string> Notes = new();
		public string Error;
		public bool Ok => Error is null;
	}

	/// <summary>
	/// Bake <paramref name="rootBone"/>'s subtree with <paramref name="settings"/>, colliding with the
	/// rig's body (<see cref="RigAnimFile.Collision"/>) and kept in its limits.
	///
	/// <paramref name="rope"/> simulates the root's first-child line as a rope (ChainSpring.Rope).
	/// <paramref name="pin"/> names a bone on that line whose head holds to the animation: with a pin,
	/// the line's bones a live IK chain poses are baked too (the rope takes the chain's shape, the pin
	/// keeps the hand on its goal) - at playback a CCD/FABRIK solve finds its end already on the goal
	/// and leaves them, while a Spline or two-bone solve re-lays its chain (noted).
	/// </summary>
	public static Result Secondary( RigAnimFile clip, Skeleton skeleton, string rootBone, SoftBone settings, bool loop, bool reduce = true,
		bool rope = false, string pin = null )
	{
		var result = new Result();
		var root = skeleton.IndexOf( rootBone ?? "" );

		if ( root < 0 ) { result.Error = $"the model has no bone '{rootBone}'"; return result; }

		var span = clip.LastFrame;

		if ( span < 1 ) { result.Error = "the clip has no motion to react to"; return result; }

		var pinned = string.IsNullOrEmpty( pin ) ? -1 : skeleton.IndexOf( pin );

		if ( !string.IsNullOrEmpty( pin ) && (pinned < 0 || !RopeLine( skeleton, root, pinned ).Any()) )
		{
			result.Error = $"pin '{pin}' is not on {rootBone}'s first-child line";
			return result;
		}

		var driven = LiveIkBones( clip, skeleton );
		var line = rope && pinned >= 0 ? RopeLine( skeleton, root, pinned ).ToHashSet() : new HashSet<int>();
		var soft = new Dictionary<int, SoftBone>();

		foreach ( var bone in Subtree( skeleton, root ) )
		{
			if ( driven.Contains( skeleton.Bones[bone].Name ) && !line.Contains( bone ) )
				result.SkippedForIk.Add( skeleton.Bones[bone].Name );
			else
				soft[bone] = settings.Clone();
		}

		if ( soft.Count == 0 ) { result.Error = $"every bone under '{rootBone}' is posed by live IK"; return result; }

		if ( line.Any( b => driven.Contains( skeleton.Bones[b].Name ) ) && clip.Ik?.Any( c => c.Live && (c.Spline || c.ChainLength == 2) && skeleton.IndexOf( c.EndBone ) == pinned ) == true )
			result.Notes.Add( $"{pin}'s live IK is Spline or two-bone: it re-lays the chain at playback over the rope - use FABRIK or CCD" );

		var options = (clip.Collision ?? new RigSoftCollision()).ToOptions( skeleton, clip.Limits );

		if ( rope )
			options.Ropes.Add( new ChainSpring.Rope( root, pinned ) );

		var frames = Enumerable.Range( 0, span + 1 ).Select( f => clip.SampleLocal( skeleton, f ) ).ToList();
		var baked = ChainSpring.Bake( skeleton, frames, soft, clip.FrameRate, loop: loop, options: options );

		Write( clip, skeleton, soft.Keys, baked, span, reduce, result );
		return result;
	}

	/// <summary>The bones from <paramref name="root"/> down its first-child line to just above
	/// <paramref name="pin"/>; empty when the pin is not on that line.</summary>
	public static IEnumerable<int> RopeLine( Skeleton skeleton, int root, int pin )
	{
		var line = new List<int>();

		for ( var b = root; b >= 0; b = skeleton.Children( b ).DefaultIfEmpty( -1 ).First() )
		{
			if ( b == pin )
				return line;

			line.Add( b );
		}

		return Enumerable.Empty<int>();
	}

	/// <summary>
	/// Marionette's Wave Down Chain on a kernel clip: <see cref="ChainWave"/> over
	/// <paramref name="rootBone"/>'s chain (the root and its first-child line down to the tip, so
	/// fingers and side branches follow rather than wave), written as keys. With
	/// <paramref name="loop"/> the period is fitted so the wave meets itself at the seam.
	/// </summary>
	public static Result Wave( RigAnimFile clip, Skeleton skeleton, string rootBone, ChainWave.Settings settings, bool loop, bool reduce = true )
	{
		var result = new Result();
		var root = skeleton.IndexOf( rootBone ?? "" );

		if ( root < 0 ) { result.Error = $"the model has no bone '{rootBone}'"; return result; }

		var span = Math.Max( clip.LastFrame, 1 );
		var driven = LiveIkBones( clip, skeleton );
		var chain = new List<int>();

		for ( var b = root; b >= 0; b = skeleton.Children( b ).DefaultIfEmpty( -1 ).First() )
		{
			if ( driven.Contains( skeleton.Bones[b].Name ) )
				result.SkippedForIk.Add( skeleton.Bones[b].Name );
			else
				chain.Add( b );
		}

		if ( chain.Count == 0 ) { result.Error = $"every bone of '{rootBone}''s chain is posed by live IK"; return result; }

		var used = new ChainWave.Settings
		{
			Amplitude = settings.Amplitude, Wavelength = settings.Wavelength, Taper = settings.Taper,
			Swing = settings.Swing, Offset = settings.Offset,
			Period = loop ? ChainWave.FitPeriod( settings.Period, span ) : settings.Period,
		};

		var frames = Enumerable.Range( 0, span + 1 ).Select( f => clip.SampleLocal( skeleton, f ) ).ToList();
		var waved = ChainWave.Apply( skeleton, frames, chain, used );

		Write( clip, skeleton, chain, waved, span, reduce, result );
		return result;
	}

	/// <summary>A key per frame on each of <paramref name="bones"/> from <paramref name="baked"/>,
	/// keyed scale kept, then reduced.</summary>
	static void Write( RigAnimFile clip, Skeleton skeleton, IEnumerable<int> bones, List<Xform[]> baked, int span, bool reduce, Result result )
	{
		foreach ( var bone in bones.OrderBy( b => b ) )
		{
			var name = skeleton.Bones[bone].Name;
			var track = clip.Find( name );

			if ( track is null )
			{
				track = new RigTrack { Name = name, Kind = RigTrackKind.Bone };
				clip.Tracks.Add( track );
			}

			// The sampled pose is rotation and position; a keyed squash survives.
			var scales = Enumerable.Range( 0, span + 1 ).Select( f => track.Keys.Count > 0 ? track.Evaluate( f ).Scale : Vec3.One ).ToArray();

			track.Keys.RemoveAll( k => k.Frame <= span );
			track.Keys.InsertRange( 0, Enumerable.Range( 0, span + 1 ).Select( f =>
			{
				var pose = RigPose.FromXform( baked[f][bone] );
				return new RigKey { Frame = f, Local = new RigPose( pose.Position, pose.Rotation, scales[f] ), Ease = RigEase.Monotone };
			} ) );

			result.Keys += span + 1;

			if ( reduce )
				result.Keys -= Reduce( track, 0, span, 0.05f, 0.5f );

			result.Bones++;
		}
	}

	/// <summary>
	/// Remove keys from <paramref name="from"/> to <paramref name="to"/> that the curve does not
	/// need: each interior key goes when the track without it stays within the tolerances on every
	/// frame it could affect. Marionette's RigKeyOps.Reduce on a kernel track. Returns how many went.
	/// </summary>
	public static int Reduce( RigTrack track, int from, int to, float position = 0.02f, float degrees = 0.25f, float scale = 0.005f )
	{
		var original = Enumerable.Range( from, Math.Max( to - from + 1, 0 ) ).Select( f => track.Evaluate( f ) ).ToArray();
		var removed = 0;

		for ( var i = 1; i < track.Keys.Count - 1; )
		{
			var key = track.Keys[i];

			if ( key.Frame <= from || key.Frame >= to )
			{
				i++;
				continue;
			}

			track.Keys.RemoveAt( i );

			// Curves through keys (Cubic, Monotone) take their tangents from the neighbours, so the
			// segments either side of the gap change too, not just the gap itself.
			var lo = Math.Max( track.Keys[Math.Max( i - 2, 0 )].Frame, from );
			var hi = Math.Min( track.Keys[Math.Min( i + 1, track.Keys.Count - 1 )].Frame, to );
			var fits = true;

			for ( var f = lo; f <= hi && fits; f++ )
			{
				var now = track.Evaluate( f );
				var was = original[f - from];
				fits = (now.Position - was.Position).Length <= position
					&& AngleDegrees( now.Rotation, was.Rotation ) <= degrees
					&& (now.Scale - was.Scale).Length <= scale;
			}

			if ( fits )
			{
				removed++;
				continue;
			}

			track.Keys.Insert( i, key );
			i++;
		}

		return removed;
	}

	/// <summary>Bones a live IK constraint poses at playback - the ones a bake would be overwritten
	/// on. Mirrors RigAnimFile.SolveIk's choice of constraints.</summary>
	public static HashSet<string> LiveIkBones( RigAnimFile clip, Skeleton skeleton )
	{
		var names = new HashSet<string>();
		var goals = clip.IkGoalTracks.Select( g => g.Name ).ToHashSet();
		var constraints = clip.Ik is not null
			? clip.Ik.Where( c => c.Enabled && c.Live && goals.Contains( c.EndBone ) )
			: goals.Select( g => new RigIk { Name = g, EndBone = g } );

		foreach ( var ik in constraints )
		{
			// A chain whose keyed weight lets go somewhere is FK there, so keys on it do show.
			if ( clip.Tracks.Any( t => t.Kind == RigTrackKind.IkWeight && t.Name == ik.EndBone && t.Keys.Any( k => k.Local.Scale.x < 0.999f ) ) )
				continue;

			var bone = skeleton.IndexOf( ik.EndBone );

			if ( bone < 0 )
				continue;

			if ( ik.GoalRotation )
				names.Add( ik.EndBone );

			for ( var k = 0; k < Math.Max( ik.ChainLength, 1 ) && bone >= 0; k++ )
			{
				bone = skeleton.Bones[bone].Parent;

				if ( bone >= 0 )
					names.Add( skeleton.Bones[bone].Name );
			}
		}

		return names;
	}

	static IEnumerable<int> Subtree( Skeleton skeleton, int root )
	{
		yield return root;

		foreach ( var child in skeleton.Children( root ) )
		{
			foreach ( var bone in Subtree( skeleton, child ) )
				yield return bone;
		}
	}

	static float AngleDegrees( Quat a, Quat b )
	{
		var dot = MathF.Abs( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w );
		return 2f * MathF.Acos( Math.Clamp( dot, 0f, 1f ) ) * 180f / MathF.PI;
	}
}
