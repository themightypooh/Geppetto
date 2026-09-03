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

		/// <summary>The clip's own file name, which is what a person recognises in a list. The
		/// activity name inside the model defaults to it for the same reason.</summary>
		public static string NameOf( Asset asset ) =>
			Path.GetFileNameWithoutExtension( asset?.Path ?? "clip" );
	}

	// --- sampling -----------------------------------------------------------------------------

	/// <summary>
	/// An engine Transform as a kernel Xform, in the basis convention the rest of the rig uses:
	/// X is right, Y is bone forward, Z is up. Same decomposition as
	/// `EffigyViewport.ApplyBoneTransform`, which is where that convention is stated.
	///
	/// SCALE IS DROPPED, deliberately. A DmeChannel writes position and orientation and nothing
	/// else, so a scaled keyframe has nowhere to go in the format; carrying it into the basis
	/// vectors instead would bake a scale into the bone's rotation and read as a skew.
	/// </summary>
	public static Xform ToXform( Transform t )
	{
		var rot = t.Rotation;
		var right = rot.Right;
		var forward = rot.Forward;
		var up = rot.Up;

		return new Xform(
			new Vec3( right.x, right.y, right.z ),
			new Vec3( forward.x, forward.y, forward.z ),
			new Vec3( up.x, up.y, up.z ),
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
		bool looping, out int matched, out List<string> unmatched )
	{
		if ( doc is null )
			throw new ArgumentNullException( nameof( doc ) );

		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		// Tracks with no keys are not tracks: Evaluate would answer Transform.Zero for every frame
		// of them, which is the collapse-to-origin case above.
		var tracks = doc.BoneTracks
			.Where( t => !string.IsNullOrWhiteSpace( t.BoneName ) && t.Keyframes.Count > 0 )
			.GroupBy( t => t.BoneName )
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

		// A single-frame clip is a pose, and the writer accepts one: N frames span N-1 intervals,
		// so one frame is a zero-length clip rather than an error.
		for ( var f = 0; f <= span; f++ )
		{
			var pose = new Xform[skeleton.Count];

			for ( var b = 0; b < skeleton.Count; b++ )
			{
				pose[b] = tracks.TryGetValue( skeleton.Bones[b].Name, out var track )
					? ToXform( track.Evaluate( f ) )
					: skeleton.Bones[b].Local;
			}

			clip.AddFrame( pose );
		}

		return clip;
	}

	// --- export -------------------------------------------------------------------------------

	/// <summary>
	/// Write every queued clip beside the model and return the entries that name them in the
	/// .vmdl. A clip that cannot be read or does not fit the rig is reported and SKIPPED rather
	/// than failing the whole compile — the model and the clips that did work are still worth
	/// having, and the log says which one was dropped and why.
	/// </summary>
	public static List<VmdlAnimation.ClipEntry> WriteClips( IEnumerable<ClipSource> sources,
		string folder, string assetPrefix, Skeleton skeleton )
	{
		var entries = new List<VmdlAnimation.ClipEntry>();
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var source in sources ?? Enumerable.Empty<ClipSource>() )
		{
			if ( source?.Asset is null )
				continue;

			// Re-read at compile time: the asset is the source of truth, so a clip edited in
			// Marionette since it was added exports as it is now.
			// Same call shape RigControlWindow.LoadAsset uses, which is the one known to work.
			if ( !source.Asset.TryLoadResource( out RigAnimDocument doc ) || doc is null )
			{
				Log.Warning( $"[Effigy] clip '{source.Name}' could not be loaded from "
					+ $"{source.Asset.Path} - skipping it" );
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
				continue;
			}

			entries.Add( new VmdlAnimation.ClipEntry( name, $"{assetPrefix}/{fileName}",
				source.Looping ) );

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
