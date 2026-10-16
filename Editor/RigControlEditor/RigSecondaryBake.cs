using Effigy;
using Marionette.EditorTools;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.Tools;

/// <summary>
/// Secondary motion for a Marionette clip: pick a bone, and it and every bone below it lag, swing
/// and settle behind the animation as if on springs - a cable trailing its hand, a sac sloshing on
/// a heap, a tail, an ear. The result is written as ordinary keys, so it plays the same in the
/// editor, in game and in an export, and can be edited like anything else.
///
/// THE SIMULATION RUNS ON THE CLIP AS IT PLAYS, live IK included: the clip is sampled exactly as
/// the export samples it (EffigyAnimExport.ToClip), the springs are run by the kernel's ChainSpring,
/// and the soft bones' new locals become a key on every frame, then reduced to the keys the curve
/// needs.
///
/// A BONE INSIDE A LIVE IK CHAIN IS SKIPPED. The solver poses that bone on every frame at playback,
/// so a key written to it would be overwritten and the bake would look like it did nothing. Such
/// bones are named in the result instead.
/// </summary>
internal static class RigSecondaryBake
{
	/// <summary>The presets are the kernel's (ChainSpring.Presets), so the headless sim and the MCP
	/// tool mean the same by "loose" as this menu does.</summary>
	public static ChainSpring.Preset[] Presets => ChainSpring.Presets;

	public sealed class Result
	{
		public bool Ok;
		public string Message;
		public int Bones;
		public int Keys;
		public List<string> SkippedForIk = new();
	}

	/// <summary>Bake <paramref name="rootBone"/> and everything below it with
	/// <paramref name="settings"/>. <paramref name="loop"/> warms the springs up on one pass of the
	/// clip so its last frame flows into its first.
	///
	/// The rig's Soft Colliders and floor push the tails out, and its Limit constraints hold every
	/// swung bone (Effigy ChainSpring.Options, what the live player and the headless sim use).
	/// <paramref name="rope"/> runs the root's first-child line as a rope; <paramref name="pin"/>, a
	/// bone on that line, holds its head to the clip - the line's live-IK bones are then baked too,
	/// as Effigy ClipBake.Secondary does.</summary>
	public static Result Bake( RigAnimDocument doc, string rootBone, SoftBone settings, bool loop, bool reduce = true, bool rope = false, string pin = null )
	{
		if ( doc?.SourceModel is null )
			return Fail( "Set a Model in the Scene tab first - the bake needs the skeleton." );

		if ( settings is null )
			return Fail( "No spring settings." );

		var skeleton = RigAnimExport.SkeletonFromModel( doc.SourceModel );
		var root = skeleton.IndexOf( rootBone ?? "" );

		if ( root < 0 )
			return Fail( $"The model has no bone '{rootBone}'." );

		var span = EffigyAnimExport.FrameSpan( doc );

		if ( span < 1 )
			return Fail( "The clip has no motion to react to - key at least two frames first." );

		var pinned = string.IsNullOrEmpty( pin ) ? -1 : skeleton.IndexOf( pin );

		if ( !string.IsNullOrEmpty( pin ) && (pinned < 0 || !ClipBake.RopeLine( skeleton, root, pinned ).Any()) )
			return Fail( $"Pin '{pin}' is not on '{rootBone}''s first-child line." );

		var line = rope && pinned >= 0 ? ClipBake.RopeLine( skeleton, root, pinned ).ToHashSet() : new HashSet<int>();
		var driven = LiveIkBones( doc, skeleton );
		var result = new Result();
		var soft = new Dictionary<int, SoftBone>();

		foreach ( var bone in Subtree( skeleton, root ) )
		{
			var name = skeleton.Bones[bone].Name;

			if ( driven.Contains( name ) && !line.Contains( bone ) )
			{
				result.SkippedForIk.Add( name );
				continue;
			}

			soft[bone] = settings.Clone();
		}

		if ( soft.Count == 0 )
			return Fail( $"Every bone under '{rootBone}' is posed by live IK, which would overwrite the bake on every frame." );

		var clip = EffigyAnimExport.ToClip( doc, skeleton, "secondary", loop, out _, out _ );
		var rate = doc.AnimationSpeed > 0 ? doc.AnimationSpeed : 30;
		var options = Collision( doc ).ToOptions( skeleton, EffigyAnimExport.Limits( doc ) );

		if ( rope )
			options.Ropes.Add( new ChainSpring.Rope( root, pinned ) );

		var baked = ChainSpring.Bake( skeleton, clip.Frames, soft, rate, loop: loop, options: options );

		WriteKeys( doc, skeleton, soft.Keys, baked, span, reduce, result );

		result.Ok = true;
		result.Message = $"baked secondary motion on {result.Bones} bone(s) under '{rootBone}', {result.Keys} key(s), frames 0-{span}"
			+ (loop ? " as a loop" : "")
			+ (rope ? pinned >= 0 ? $", as a rope pinned at {pin}" : ", as a rope" : "")
			+ (options.Capsules.Count > 0 || options.Ground is not null ? $", colliding with {options.Capsules.Count} capsule(s){(options.Ground is { } g ? $" and the floor at z={g:0.#}" : "")}" : "")
			+ (result.SkippedForIk.Count > 0 ? $"; skipped {result.SkippedForIk.Count} posed by live IK ({string.Join( ", ", result.SkippedForIk.Take( 6 ) )})" : "");

		return result;
	}

	/// <summary>
	/// Wave Down Chain: a sine travelling from <paramref name="rootBone"/> down its first-child line
	/// to the tip, added to what the clip already does and written as keys (ChainWave in the kernel,
	/// shared with the headless sim). With <paramref name="loop"/> the period is fitted to the clip so
	/// the wave meets itself at the seam. Bones a live IK chain poses are skipped, as for the bake.
	/// </summary>
	public static Result Wave( RigAnimDocument doc, string rootBone, ChainWave.Settings settings, bool loop, bool reduce = true )
	{
		if ( doc?.SourceModel is null )
			return Fail( "Set a Model in the Scene tab first - the wave needs the skeleton." );

		var skeleton = RigAnimExport.SkeletonFromModel( doc.SourceModel );
		var root = skeleton.IndexOf( rootBone ?? "" );

		if ( root < 0 )
			return Fail( $"The model has no bone '{rootBone}'." );

		var span = Math.Max( EffigyAnimExport.FrameSpan( doc ), (int)MathF.Round( settings.Period ) );
		var driven = LiveIkBones( doc, skeleton );
		var result = new Result();
		var chain = new List<int>();

		for ( var b = root; b >= 0; b = skeleton.Children( b ).DefaultIfEmpty( -1 ).First() )
		{
			if ( driven.Contains( skeleton.Bones[b].Name ) )
				result.SkippedForIk.Add( skeleton.Bones[b].Name );
			else
				chain.Add( b );
		}

		if ( chain.Count == 0 )
			return Fail( $"Every bone of '{rootBone}''s chain is posed by live IK." );

		var used = new ChainWave.Settings
		{
			Amplitude = settings.Amplitude, Wavelength = settings.Wavelength, Taper = settings.Taper,
			Swing = settings.Swing, Offset = settings.Offset,
			Period = loop ? ChainWave.FitPeriod( settings.Period, span ) : settings.Period,
		};

		// ToClip samples to the last key; a clip shorter than one swing is extended to it.
		var clip = EffigyAnimExport.ToClip( doc, skeleton, "wave", loop, out _, out _ );

		while ( clip.Frames.Count <= span )
			clip.AddFrame( (Xform[])clip.Frames[^1].Clone() );

		var waved = ChainWave.Apply( skeleton, clip.Frames, chain, used );

		WriteKeys( doc, skeleton, chain, waved, span, reduce, result );

		result.Ok = true;
		result.Message = $"waved {result.Bones} bone(s) down '{rootBone}', {used.Amplitude:0.#}° every {used.Period:0.#} frames, {result.Keys} key(s), frames 0-{span}"
			+ (result.SkippedForIk.Count > 0 ? $"; skipped {result.SkippedForIk.Count} posed by live IK" : "");

		return result;
	}

	static void WriteKeys( RigAnimDocument doc, Skeleton skeleton, IEnumerable<int> bones, List<Xform[]> baked, int span, bool reduce, Result result )
	{
		foreach ( var bone in bones.OrderBy( b => b ) )
		{
			var name = skeleton.Bones[bone].Name;
			var track = doc.GetOrAddTrack( name );

			// The clip's own scale is kept: the sampled pose is rotation and position only, and a
			// squash keyed on a tentacle should survive having lag added to it.
			var keyed = track.Keyframes.Count > 0;
			var scales = Enumerable.Range( 0, span + 1 ).Select( f => keyed ? track.Evaluate( f ).Scale : Vector3.One ).ToArray();

			result.Keys += RigKeyOps.Bake( track, 0, span, f => ToTransform( baked[f][bone], scales[f] ) );

			if ( reduce )
			{
				var written = track.Keyframes.Where( k => k.Frame <= span ).Select( k => (track, k) ).ToList();
				result.Keys -= RigKeyOps.Reduce( written, 0.05f, 0.5f );
			}

			result.Bones++;
		}
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

	/// <summary>Every bone a live, animated IK constraint poses at playback.</summary>
	static HashSet<string> LiveIkBones( RigAnimDocument doc, Skeleton skeleton )
	{
		var names = new HashSet<string>();

		foreach ( var ik in doc.RigAsset?.IkConstraints ?? new List<IkConstraint>() )
		{
			if ( ik is null || !ik.Enabled || !ik.Live || doc.FindIkGoalTrack( ik.TargetBone ) is not { Keyframes.Count: > 0 } )
				continue;

			// A chain whose keyed weight lets go somewhere is FK there, so keys on it do show - the
			// free cable is exactly what wants the springs. Held frames still solve over them.
			if ( doc.FindIkWeightTrack( ik.TargetBone )?.Keyframes.Any( k => k.Local.Scale.x < 0.999f ) == true )
				continue;

			var bone = skeleton.IndexOf( ik.TargetBone );

			// The end bone only moves (or takes the goal's rotation) - its own rotation key still
			// counts unless the goal turns it, so only the bones above it are driven.
			if ( ik.GoalRotation && bone >= 0 )
				names.Add( ik.TargetBone );

			for ( var k = 0; k < Math.Max( ik.ChainLength, 1 ) && bone >= 0; k++ )
			{
				bone = skeleton.Bones[bone].Parent;

				if ( bone >= 0 )
					names.Add( skeleton.Bones[bone].Name );
			}
		}

		return names;
	}

	/// <summary>The rig's Soft Colliders, floor and thickness as the kernel reads them from a .ctrlrig.</summary>
	public static RigSoftCollision Collision( RigAnimDocument doc )
	{
		var result = new RigSoftCollision();

		if ( doc?.RigAsset is not { } rig )
			return result;

		foreach ( var c in rig.SoftColliders ?? new List<RigSoftCollider>() )
		{
			if ( c is { Enabled: true } && !string.IsNullOrEmpty( c.Bone ) )
				result.Capsules.Add( (c.Bone, c.Radius) );
		}

		result.Ground = rig.Ground ? rig.GroundHeight : null;
		result.Thickness = rig.SoftThickness;
		return result;
	}

	/// <summary>The end of the first live IK chain on <paramref name="rootBone"/>'s first-child line -
	/// where a rope bake offers to pin. Null when none.</summary>
	public static string IkEndOnLine( RigAnimDocument doc, string rootBone )
	{
		if ( doc?.SourceModel is null || doc.RigAsset?.IkConstraints is not { Count: > 0 } constraints )
			return null;

		var skeleton = RigAnimExport.SkeletonFromModel( doc.SourceModel );
		var root = skeleton.IndexOf( rootBone ?? "" );

		if ( root < 0 )
			return null;

		for ( var b = skeleton.Children( root ).DefaultIfEmpty( -1 ).First(); b >= 0; b = skeleton.Children( b ).DefaultIfEmpty( -1 ).First() )
		{
			var name = skeleton.Bones[b].Name;

			if ( constraints.Any( c => c is { Enabled: true, Live: true } && c.TargetBone == name && doc.FindIkGoalTrack( name ) is { Keyframes.Count: > 0 } ) )
				return name;
		}

		return null;
	}

	/// <summary>A kernel local back into the engine's Transform - EffigyAnimExport.ToXform the
	/// other way round.</summary>
	public static Transform ToTransform( Xform x, Vector3 scale )
	{
		var q = Quat.FromXform( x );
		return new Transform( new Vector3( x.Origin.x, x.Origin.y, x.Origin.z ), new Rotation( q.x, q.y, q.z, q.w ), scale );
	}

	static Result Fail( string message ) => new() { Ok = false, Message = message };
}
