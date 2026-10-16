using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.EditorTools;

/// <summary>
/// The bridge from a Marionette clip to an Effigy animation export.
///
/// WHY THE BRIDGE IS HERE AND NOT IN THE KERNEL. `Effigy/` contains no reference to any engine type
/// anywhere in it, and a test enforces that. `RigAnimDocument`, `Transform` and `Asset` are all
/// engine types, so the sampling has to live on this side of the line. What the kernel owns is the
/// part that does not need an engine: `AnimClip` holds the sampled poses and `DmxAnimWriter` writes
/// them, both covered headlessly.
///
/// WHY THIS DOES NOT AUTHOR CLIPS. Effigy owns rig authoring and binding; Marionette owns posing,
/// keyframes, IK and constraints, and that split is a decision on the record rather than an
/// accident (see WHAT-IS-BUILT, "The rig design, as decided"). So the clip picker takes a
/// `.riganim` that already exists and samples it. Effigy does not grow a second timeline.
///
/// The pipeline this closes:
///
///     Effigy: model → rig → Compile .vmdl → export.vmdl
///           → Marionette opens it, poses it, saves a .riganim
///           → Effigy adds that .riganim and compiles it INTO the model
/// </summary>
internal static class EffigyAnimExport
{
	/// <summary>
	/// A clip queued for export: the asset the user picked, and the name it will carry inside the
	/// model. The asset is kept rather than the loaded document so a clip edited in Marionette
	/// after being added is re-read at compile time rather than exported stale.
	/// </summary>
	internal sealed class ClipSource
	{
		public Asset Asset;
		public string Name;
		public bool Looping = true;

		/// <summary>The project path a saved clip pointed at when no asset answers to it any more —
		/// moved, renamed, or in an addon not mounted right now. Kept so saving again writes the
		/// entry back rather than quietly dropping it; see AnimClipsSidecar.</summary>
		public string MissingPath;

		/// <summary>The clip's own file name, which is what a person recognises in a list. The
		/// activity name inside the model defaults to it for the same reason.</summary>
		public static string NameOf( Asset asset ) =>
			Path.GetFileNameWithoutExtension( asset?.Path ?? "clip" );
	}

	// --- sampling -----------------------------------------------------------------------------

	/// <summary>
	/// An engine Transform as a kernel Xform.
	///
	/// AN XFORM'S COLUMNS ARE WHERE THE UNIT AXES LAND — that is Xform's own definition of itself,
	/// and `DmxText.Quaternion` reads them back as exactly that when it turns a basis into the
	/// quaternion a DmeChannel carries. So this has to be the true decomposition of the rotation,
	/// not a naming convention:
	///
	///     X ← the image of (1,0,0), which s&amp;box calls Forward
	///     Y ← the image of (0,1,0), which is Left, i.e. -Right
	///     Z ← the image of (0,0,1), which is Up
	///
	/// THIS USED TO BE (Right, Forward, Up), COPIED FROM `EffigyViewport.ApplyBoneTransform`, and
	/// it is the reason exported clips crumpled the model on playback. That triple is not the
	/// rotation's basis — it is the basis with X and Y swapped and one of them negated, which is a
	/// perfectly valid rotation matrix and therefore compiles without a murmur. What it writes is
	/// `R · Rz(-90°)`: every bone turned a quarter turn about its own Z.
	///
	/// It does not cancel out, which is why it was worth this much comment. Each bone's POSITION is
	/// written correctly in its parent's true frame, so the extra quarter turn on the parent throws
	/// the child sideways rather than merely spinning it in place, and the error compounds down
	/// every chain — a shoulder off by 90° puts the hand somewhere else entirely. Both the bind
	/// pose and the frames come through here, so nothing downstream had a correct value to
	/// disagree with.
	///
	/// SCALE IS NOT IN HERE, deliberately: carried in the basis vectors it would bake into the bone's
	/// rotation and read as a skew. It travels beside the pose instead, as AnimClip.Scales, and goes
	/// out as its own channel - see ToClip and DmxAnimWriter.
	/// </summary>
	public static Xform ToXform( Transform t )
	{
		var rot = t.Rotation;
		var x = rot.Forward;
		var y = -rot.Right;
		var z = rot.Up;

		return new Xform(
			new Vec3( x.x, x.y, x.z ),
			new Vec3( y.x, y.y, y.z ),
			new Vec3( z.x, z.y, z.z ),
			new Vec3( t.Position.x, t.Position.y, t.Position.z ) );
	}

	/// <summary>
	/// How many frames of this document are worth writing.
	///
	/// NOT `FrameCount`, which defaults to 900 — thirty seconds at 30fps — because the timeline
	/// reads as broken when it is shorter (see RigAnimDocument). Writing to that default would put
	/// 900 frames of mostly-identical poses in the file for every bone and two channels each, for a
	/// clip whose last key is at frame 20. The last keyframe is where the animation actually ends;
	/// past it every track is constant by `Evaluate`'s own definition.
	///
	/// Clamped to FrameCount so a document whose keys were dragged out beyond its declared length
	/// exports what it claims to be rather than silently more.
	/// </summary>
	public static int FrameSpan( RigAnimDocument doc )
	{
		var last = 0;

		foreach ( var track in doc.BoneTracks )
		{
			foreach ( var key in track.Keyframes )
				last = Math.Max( last, key.Frame );
		}

		return Math.Min( last, Math.Max( doc.FrameCount - 1, 0 ) );
	}

	/// <summary>
	/// The clip, sampled onto the skeleton it will be compiled against.
	///
	/// THE FALLBACK IS THE BIND POSE, NOT ZERO, and that is the whole reason this is not a two-line
	/// loop. `BoneTrack.Evaluate` returns `Transform.Zero` for a track with no keyframes, and a
	/// bone with no track at all has no Evaluate to call — so the obvious version poses every
	/// unkeyed bone at the origin and collapses the parts of the model nobody animated into a heap
	/// at the root. An unkeyed bone should stay exactly where the rig put it.
	///
	/// THE SKELETON IS EFFIGY'S, and bones are matched to tracks BY NAME. That is the same contract
	/// ModelDoc uses to match a clip to a model, so a track naming a bone this rig does not have is
	/// dropped here for the same reason the compiler would drop it — but here it can be counted and
	/// reported, which is the difference between a clip that quietly animates less of the model and
	/// one that says so.
	/// </summary>
	public static AnimClip ToClip( RigAnimDocument doc, Skeleton skeleton, string name,
		bool looping, out int matched, out List<string> unmatched ) =>
		ToClip( doc, skeleton, name, looping, RigTrackName.RootSubject, out matched, out unmatched );

	/// <summary>
	/// The clip as above, for one object in it: the main model (<paramref name="subject"/> empty),
	/// or a prop or part that carries its own skeleton - a weapon's bolt, a door's hinge - whose
	/// tracks are "subject/bone". That is how the export gives a rigged prop its own animation.
	///
	/// SCALE goes into AnimClip.Scales (a bone's uniform scale, the mean of its axes), only when some
	/// bone actually scales, so a clip that does not bakes exactly as before.
	///
	/// LIVE IK is solved into the main model's frames the way playback solves it - keyframes first,
	/// then each live constraint toward its goal - by the kernel's ChainIk, so the exported motion is
	/// the motion the viewport showed. A prop's skeleton has no goals; goals are the main model's.
	/// </summary>
	public static AnimClip ToClip( RigAnimDocument doc, Skeleton skeleton, string name,
		bool looping, string subject, out int matched, out List<string> unmatched )
	{
		if ( doc is null )
			throw new ArgumentNullException( nameof( doc ) );

		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		// Tracks with no keys are not tracks: Evaluate would answer Transform.Zero for every frame
		// of them, which is the collapse-to-origin case above.
		// THE MAIN MODEL'S BONES ONLY. A clip can also carry whole-part tracks and the bones of
		// other objects in the scene (a weapon's bolt, a door's hinge) - none of which exist in
		// THIS skeleton. Left in, every one of them would be reported as a bone the model is
		// missing, which reads as the clip being authored against the wrong model.
		var prefix = string.IsNullOrEmpty( subject ) ? null : subject + RigTrackName.Separator;

		var tracks = doc.SkeletonTracks
			.Where( t => !string.IsNullOrWhiteSpace( t.BoneName ) && t.Keyframes.Count > 0 )
			.Where( t => prefix is null
				? RigTrackName.SubjectOf( t.BoneName ) == RigTrackName.RootSubject
				: t.BoneName.StartsWith( prefix, StringComparison.Ordinal ) && t.BoneName.Length > prefix.Length )
			.GroupBy( t => prefix is null ? t.BoneName : t.BoneName[prefix.Length..] )
			.ToDictionary( g => g.Key, g => g.First() );

		matched = skeleton.Bones.Count( b => tracks.ContainsKey( b.Name ) );

		unmatched = tracks.Keys
			.Where( boneName => skeleton.IndexOf( boneName ) < 0 )
			.OrderBy( n => n )
			.ToList();

		var rate = doc.AnimationSpeed > 0 ? doc.AnimationSpeed : 30;
		var span = FrameSpan( doc );

		var clip = new AnimClip
		{
			Name = name,
			FrameRate = rate,
			Looping = looping,
		};

		var ik = prefix is null ? LiveIk( doc ) : null;

		// The rig's Limit constraints, as the viewport and the game apply them: every bone clamped as
		// it is read, keyed or at bind, and every IK solve kept inside them. Main model only - a
		// limit names one of its bones.
		var limits = prefix is null ? Limits( doc ) : null;
		var morphs = new Dictionary<string, float>();

		// A single-frame clip is a pose, and the writer accepts one: N frames span N-1 intervals,
		// so one frame is a zero-length clip rather than an error.
		for ( var f = 0; f <= span; f++ )
		{
			var pose = new Xform[skeleton.Count];
			float[] scales = null;

			for ( var b = 0; b < skeleton.Count; b++ )
			{
				if ( !tracks.TryGetValue( skeleton.Bones[b].Name, out var track ) )
				{
					pose[b] = RigLimit.Clamp( limits, skeleton.Bones[b].Name, skeleton.Bones[b].Local );
					continue;
				}

				var local = track.Evaluate( f );
				pose[b] = RigLimit.Clamp( limits, skeleton.Bones[b].Name, ToXform( local ) );

				var uniform = (local.Scale.x + local.Scale.y + local.Scale.z) / 3f;

				if ( MathF.Abs( uniform - 1f ) > 1e-5f )
				{
					scales ??= Enumerable.Repeat( 1f, skeleton.Count ).ToArray();
					scales[b] = uniform;
				}
			}

			if ( ik is not null )
				pose = SolveIk( doc, skeleton, pose, ik, f, limits );

			clip.AddFrame( pose, scales );

			// The main model's morphs - keyed curves and events, as the player plays them - become
			// flex channels in the DMX (DmxAnimWriter.WriteMorphChannel).
			if ( prefix is null )
			{
				doc.MorphWeightsAt( f, morphs );
				clip.SetMorphs( morphs );
			}
		}

		return clip;
	}

	/// <summary>The rig's live constraints that have a keyed goal in this clip, as the kernel's
	/// RigIk - or null when there is nothing to solve, which is every clip that bakes its IK.</summary>
	static List<RigIk> LiveIk( RigAnimDocument doc )
	{
		if ( doc.RigAsset?.IkConstraints is not { Count: > 0 } constraints )
			return null;

		var live = constraints
			.Where( c => c is not null && c.Enabled && c.Live && doc.FindIkGoalTrack( c.TargetBone ) is { Keyframes.Count: > 0 } )
			.Select( c => new RigIk
			{
				Name = c.Name,
				EndBone = c.TargetBone,
				ChainLength = c.ChainLength,
				Pole = new Vec3( c.PoleDirection.x, c.PoleDirection.y, c.PoleDirection.z ),
				Weight = c.Weight,
				Iterations = c.Iterations,
				Fabrik = c.Solver == IkSolver.Fabrik,
				Spline = c.Solver == IkSolver.Spline,
				Enabled = true,
				Live = true,
				GoalRotation = c.GoalRotation,
				MaxStretch = c.MaxStretch,
			} )
			.ToList();

		return live.Count > 0 ? live : null;
	}

	/// <summary>The rig's Limit constraints as the kernel's RigLimit - null when it has none, which
	/// limits nothing. Same fields RigLimit.ReadRig reads from the .ctrlrig.</summary>
	public static List<RigLimit> Limits( RigAnimDocument doc )
	{
		if ( doc?.RigAsset?.LimitConstraints is not { Count: > 0 } constraints )
			return null;

		var list = constraints
			.Where( c => c is not null && c.Enabled && !string.IsNullOrEmpty( c.TargetBone ) )
			.Select( c => new RigLimit
			{
				Name = c.Name,
				Bone = c.TargetBone,
				Weight = c.Weight,
				Enabled = true,
				Min = new Vec3( c.MinAngles.pitch, c.MinAngles.yaw, c.MinAngles.roll ),
				Max = new Vec3( c.MaxAngles.pitch, c.MaxAngles.yaw, c.MaxAngles.roll ),
			} )
			.ToList();

		return list.Count > 0 ? list : null;
	}

	/// <summary>One frame's live IK: to model space, each constraint toward its goal, back to local.
	/// RigAnimDocument.IkGoalAt hands the goal over in the model's space, whatever it was keyed in.</summary>
	static Xform[] SolveIk( RigAnimDocument doc, Skeleton skeleton, Xform[] local, List<RigIk> constraints, int frame, List<RigLimit> limits )
	{
		var world = RigAnimFile.ModelSpace( skeleton, local );

		foreach ( var ik in constraints )
		{
			// Out of whatever space it was keyed in - an object-space goal rides its object.
			if ( doc.IkGoalAt( ik.EndBone, frame ) is not { } goal )
				continue;

			var r = goal.Rotation;
			var pose = new RigPose( new Vec3( goal.Position.x, goal.Position.y, goal.Position.z ), new Quat( r.x, r.y, r.z, r.w ), Vec3.One );

			// The clip's keyed IK weight (Marionette TrackTarget.IkWeight): 0 leaves the chain on its keys.
			var keyedWeight = doc.IkWeightAt( ik.EndBone, frame );

			if ( keyedWeight > 0f && ChainIk.Apply( skeleton, world, keyedWeight < 1f ? ik.Weighted( keyedWeight ) : ik, pose, limits ) is { } solved )
				world = solved;
		}

		return RigAnimFile.LocalSpace( skeleton, world );
	}

	// --- export -------------------------------------------------------------------------------

	/// <summary>
	/// Write every queued clip beside the model and return the entries that name them in the
	/// .vmdl. A clip that cannot be read or does not fit the rig is reported and SKIPPED rather
	/// than failing the whole compile — the model and the clips that did work are still worth
	/// having, and the log says which one was dropped and why.
	/// </summary>
	public static List<VmdlAnimation.ClipEntry> WriteClips( IEnumerable<ClipSource> sources,
		string folder, string assetPrefix, Skeleton skeleton ) =>
		WriteClips( sources, folder, assetPrefix, skeleton, out _ );

	/// <inheritdoc cref="WriteClips(IEnumerable{ClipSource}, string, string, Skeleton)"/>
	/// <param name="skipped">Each clip left out, as "name (why)" - so the window can say
	/// "Compiled, but 2 clips were left out" instead of a plain "Compiled" over a model that is
	/// missing its walk.</param>
	public static List<VmdlAnimation.ClipEntry> WriteClips( IEnumerable<ClipSource> sources,
		string folder, string assetPrefix, Skeleton skeleton, out List<string> skipped )
	{
		var entries = new List<VmdlAnimation.ClipEntry>();
		skipped = new List<string>();
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var source in sources ?? Enumerable.Empty<ClipSource>() )
		{
			if ( source?.Asset is null )
			{
				if ( source is not null )
					skipped.Add( $"{source.Name ?? "a clip"} (its animation asset is missing)" );
				continue;
			}

			// Re-read at compile time: the asset is the source of truth, so a clip edited in
			// Marionette since it was added exports as it is now.
			// Same call shape RigControlWindow.LoadAsset uses, which is the one known to work.
			if ( !source.Asset.TryLoadResource( out RigAnimDocument doc ) || doc is null )
			{
				Log.Warning( $"[Effigy] clip '{source.Name}' could not be loaded from "
					+ $"{source.Asset.Path} - skipping it" );
				skipped.Add( $"{source.Name ?? source.Asset.Path} (could not be loaded)" );
				continue;
			}

			var name = string.IsNullOrWhiteSpace( source.Name )
				? ClipSource.NameOf( source.Asset )
				: source.Name;

			// Two clips sharing a name would overwrite each other's .dmx and give the model two
			// animations it cannot tell apart.
			if ( !used.Add( name ) )
			{
				Log.Warning( $"[Effigy] two clips are both called '{name}' - skipping the second" );
				skipped.Add( $"{name} (a second clip with the same name)" );
				continue;
			}

			AnimClip clip;
			int matched;
			List<string> unmatched;

			try
			{
				clip = ToClip( doc, skeleton, name, source.Looping, out matched, out unmatched );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[Effigy] clip '{name}' could not be sampled: {e.Message}" );
				skipped.Add( $"{name} (could not be sampled: {e.Message})" );
				continue;
			}

			// A clip that poses none of this model's bones compiles perfectly and animates nothing,
			// which is the single most confusing outcome available here. It is almost always a clip
			// authored against a different rig.
			if ( matched == 0 )
			{
				Log.Warning( $"[Effigy] clip '{name}' names none of this rig's bones - skipping it. "
					+ "It was probably authored against a different model; ModelDoc matches clips to "
					+ "bones by name." );
				skipped.Add( $"{name} (names none of this rig's bones)" );
				continue;
			}

			if ( unmatched.Count > 0 )
			{
				Log.Warning( $"[Effigy] clip '{name}' poses {unmatched.Count} bone(s) this rig does "
					+ $"not have, which will not animate: {string.Join( ", ", unmatched.Take( 8 ) )}"
					+ (unmatched.Count > 8 ? " ..." : "") );
			}

			var fileName = $"anim_{Sanitise( name )}.dmx";

			try
			{
				DmxAnimWriter.WriteFile( Path.Combine( folder, fileName ), skeleton, clip,
					"effigy_export" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[Effigy] clip '{name}' could not be written: {e.Message}" );
				skipped.Add( $"{name} (could not be written: {e.Message})" );
				continue;
			}

			entries.Add( new VmdlAnimation.ClipEntry( name, $"{assetPrefix}/{fileName}",
				source.Looping, Marionette.Tools.RigRootMotionTool.ToKernel( doc.RootMotion ) ) );

			Log.Info( $"[Effigy] wrote {fileName} - {clip.FrameCount} frame(s) at "
				+ $"{clip.FrameRate}fps, {matched}/{skeleton.Count} bone(s) posed" );
		}

		return entries;
	}

	/// <summary>
	/// A clip name as a file name. The name itself is the user's and goes into the model unchanged;
	/// this only governs what lands on disk, because a clip called "reach / grab" is a valid
	/// animation name and not a valid path.
	/// </summary>
	static string Sanitise( string name )
	{
		var chars = name.Select( c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' ? c : '_' )
			.ToArray();

		var cleaned = new string( chars ).Trim( '_' );

		return string.IsNullOrEmpty( cleaned ) ? "clip" : cleaned.ToLowerInvariant();
	}
}
