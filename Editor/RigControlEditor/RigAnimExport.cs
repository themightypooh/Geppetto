using Editor;
using Effigy;
using Marionette.EditorTools;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.Tools;

/// <summary>
/// Turns the open .riganim into a compiled model the game can play.
///
/// WHY THIS LIVES IN MARIONETTE, NOT ONLY EFFIGY. Effigy compiles clips INTO a model it authored.
/// Most people here are posing first-person arms or citizen, which they did not author, and the
/// only way they had to "export" was to leave this window, open a different tool, and add the
/// clip to a list that then compiles a different model. There was no button. This is the button.
///
/// WHAT IT WRITES. An animation-only .vmdl whose Base Model is the clip's source model, plus the
/// baked .dmx the compiler reads. That is the documented way to add sequences to an existing
/// model (s&amp;box Model Editor: "Your new VMDL should only hold new animations"). The game then
/// sees a model that looks like the original and has this clip as a named sequence.
/// </summary>
internal static class RigAnimExport
{
	public sealed class Result
	{
		public bool Ok;
		public string Error;
		public string SequenceName;
		public string VmdlAssetPath;
		public string DmxAssetPath;
		public string VdmxAssetPath;
		public int Frames;
		public int MatchedBones;
		public int SkeletonBones;
		public int Cameras;
		public bool Compiled;

		/// <summary>Animation-only .vmdls written for props and parts that carry their own skeleton -
		/// a weapon's bolt, a door's hinge - one per object, each based on that object's model.</summary>
		public List<string> PropVmdlAssetPaths = new();

		/// <summary>Something the export could not carry, said out loud. Not an error - the
		/// export worked - but the difference between "it does not do that" and "it silently
		/// dropped my work" is entirely whether anybody was told.</summary>
		public string Note;
	}

	public static Result Export( RigAnimDocument doc, Asset riganim, string clipName, bool looping )
	{
		if ( doc is null )
			return Fail( "Nothing open to export." );

		if ( riganim is null )
			return Fail( "Save the clip first — export writes next to the .riganim." );

		if ( doc.SourceModel is null )
			return Fail( "Set a Model in BonesObject first — export has to know which skeleton this clip is for." );

		// Skeleton tracks only. A .vmdl animation is bone channels, so a clip whose keys are all
		// on whole parts has nothing to write here - and saying "nothing to export" about a clip
		// you can plainly see keyframes in would read as the exporter being broken.
		// An IK goal moves bones too - a clip posed entirely by live IK has goal keys and no others.
		var hasGoals = doc.IkGoalTracks.Any( t => t.Keyframes.Count > 0 );

		if ( doc.BoneTracks is null || (!doc.SkeletonTracks.Any( t => t.Keyframes.Count > 0 ) && !hasGoals) )
		{
			return Fail( doc.PartTracks.Any( t => t.Keyframes.Count > 0 )
				? "This clip only keys whole parts, which a .vmdl animation can't carry — play it with a RigAnimPlayerComponent instead, or key some bones."
				: "Nothing to export — key some bones first." );
		}

		var name = string.IsNullOrWhiteSpace( clipName )
			? Path.GetFileNameWithoutExtension( riganim.Name )
			: clipName.Trim();

		if ( string.IsNullOrWhiteSpace( name ) )
			name = "clip";

		var fileStem = Sanitise( name );
		var modelPath = PathOf( doc.SourceModel );

		if ( string.IsNullOrWhiteSpace( modelPath ) )
			return Fail( "The source model has no path, so a Base Model vmdl cannot be written." );

		Skeleton skeleton;

		try
		{
			skeleton = SkeletonFromModel( doc.SourceModel );
		}
		catch ( Exception e )
		{
			return Fail( $"Could not read the model's skeleton: {e.Message}" );
		}

		if ( skeleton.Count == 0 )
			return Fail( "The source model has no bones — there is nothing to animate." );

		AnimClip clip;
		int matched;
		List<string> unmatched;

		try
		{
			clip = EffigyAnimExport.ToClip( doc, skeleton, name, looping, out matched, out unmatched );
		}
		catch ( Exception e )
		{
			return Fail( $"Could not sample the clip: {e.Message}" );
		}

		if ( matched == 0 && !hasGoals )
		{
			return Fail( "This clip names none of the model's bones, so it would compile and animate "
				+ "nothing. It was probably authored against a different model." );
		}

		var assetsRoot = EffigyAssetFolder.AssetsRoot();

		if ( string.IsNullOrWhiteSpace( assetsRoot ) )
			return Fail( "Could not find the project's Assets folder." );

		var folder = FolderOf( riganim, assetsRoot );

		if ( folder is null )
			return Fail( "The .riganim is not inside Assets, so there is nowhere legal to write the export." );

		Directory.CreateDirectory( folder );

		var dmxFile = $"{fileStem}.dmx";
		var vmdlFile = $"{fileStem}.vmdl";
		var dmxAbs = Path.Combine( folder, dmxFile );
		var vmdlAbs = Path.Combine( folder, vmdlFile );

		try
		{
			DmxAnimWriter.WriteFile( dmxAbs, skeleton, clip, Path.GetFileNameWithoutExtension( modelPath ) );
		}
		catch ( Exception e )
		{
			return Fail( $"Could not write the animation file: {e.Message}" );
		}

		var relativeFolder = Path.GetRelativePath( assetsRoot, folder ).Replace( '\\', '/' );

		if ( relativeFolder == "." )
			relativeFolder = "";

		var dmxAsset = string.IsNullOrEmpty( relativeFolder ) ? dmxFile : $"{relativeFolder}/{dmxFile}";
		var vmdlAsset = string.IsNullOrEmpty( relativeFolder ) ? vmdlFile : $"{relativeFolder}/{vmdlFile}";

		var entry = new VmdlAnimation.ClipEntry( name, dmxAsset, looping );

		try
		{
			File.WriteAllText( vmdlAbs, AnimationOnlyVmdl( modelPath, entry ) );
		}
		catch ( Exception e )
		{
			return Fail( $"Could not write the model: {e.Message}" );
		}

		// Cameras that leave the window, as a shot file beside the animation. See DmxCameraWriter
		// for why this is the one piece of the export that is built rather than copied.
		var exportedCameras = doc.Cameras?.Where( c => c is not null && c.Export && c.Enabled ).ToList()
			?? new List<RigCamera>();

		var vdmxFile = $"{fileStem}.vdmx";
		var vdmxAbs = Path.Combine( folder, vdmxFile );
		var vdmxAsset = string.IsNullOrEmpty( relativeFolder ) ? vdmxFile : $"{relativeFolder}/{vdmxFile}";

		if ( exportedCameras.Count > 0 )
		{
			try
			{
				DmxCameraWriter.WriteFile( vdmxAbs, exportedCameras );
				EffigyAssetFolder.Register( folder );
			}
			catch ( Exception e )
			{
				return Fail( $"Could not write the camera file: {e.Message}" );
			}
		}

		if ( unmatched.Count > 0 )
		{
			Log.Warning( $"[Marionette] clip '{name}' poses {unmatched.Count} bone(s) this model does "
				+ $"not have, which will not animate: {string.Join( ", ", unmatched.Take( 8 ) )}"
				+ (unmatched.Count > 8 ? " ..." : "") );
		}

		// Props with skeletons of their own get their own animation .vmdl - see ExportProps.
		var propVmdls = ExportProps( doc, folder, relativeFolder, fileStem, name, looping, out var propNote );

		EffigyAssetFolder.Register( folder );

		var asset = AssetSystem.FindByPath( vmdlAsset ) ?? AssetSystem.RegisterFile( vmdlAbs );
		var compiled = false;

		if ( asset is null )
		{
			Log.Warning( $"[Marionette] wrote {vmdlAsset} but the asset system could not find it" );
		}
		else
		{
			asset.Compile( true );
			compiled = !asset.IsCompileFailed;

			if ( !compiled )
			{
				Log.Warning( $"[Marionette] {vmdlAsset} compile FAILED — the compiler's own output "
					+ "above says why. The .dmx is on disk either way." );
			}
		}

		Log.Info( $"[Marionette] exported {vmdlAsset} — sequence '{name}', {clip.FrameCount} frame(s), "
			+ $"{matched}/{skeleton.Count} bone(s) posed, compiled={compiled}" );

		return new Result
		{
			Ok = true,
			SequenceName = name,
			VmdlAssetPath = vmdlAsset,
			DmxAssetPath = dmxAsset,
			VdmxAssetPath = exportedCameras.Count > 0 ? vdmxAsset : null,
			Frames = clip.FrameCount,
			MatchedBones = matched,
			SkeletonBones = skeleton.Count,
			Cameras = exportedCameras.Count,
			Compiled = compiled,
			PropVmdlAssetPaths = propVmdls,
			Note = JoinNote( JoinNote( JoinNote( LightNote( doc ), CameraNote( doc, vdmxAsset ) ),
				JoinNote( PartNote( doc ), MorphNote( doc ) ) ), JoinNote( ScaleNote( clip ), propNote ) ),
		};
	}

	/// <summary>
	/// An animation-only .vmdl for each prop or part in the clip that has a compiled model and keyed
	/// bones of its own - "magazine/latch", "door/handle/hinge".
	///
	/// These used to be dropped without a word: the export took the main model's bones and nothing
	/// else, so the bolt that racks in the editor stood still in game. Each goes out exactly as the
	/// main model does - Base Model is the prop's own .vmdl, the clip is a sequence of the same name -
	/// so the game plays it on the prop with the same call. An object drawn from an OBJ has no model to
	/// extend, and is named in the note instead.
	/// </summary>
	static List<string> ExportProps( RigAnimDocument doc, string folder, string relativeFolder,
		string fileStem, string clipName, bool looping, out string note )
	{
		var written = new List<string>();
		var skipped = new List<string>();
		note = null;

		// Every object that can own bone tracks, with its model when it has one.
		var owners = new List<(string Subject, Model Model)>();

		foreach ( var prop in doc.ReferenceProps ?? new List<ReferenceProp>() )
		{
			if ( prop is not null && !string.IsNullOrWhiteSpace( prop.Name ) )
				owners.Add( (prop.Name, prop.Model) );
		}

		foreach ( var obj in doc.Objects ?? new List<RigObject>() )
		{
			if ( obj is null )
				continue;

			owners.Add( (obj.Name, null) );

			foreach ( var part in obj.Parts ?? new List<RigObjectPart>() )
			{
				if ( part is not null )
					owners.Add( (RigTrackName.Qualify( obj.Name, part.Name ), part.Model) );
			}
		}

		// Longest owner first, so "door/handle/hinge" is the handle's hinge and not a bone of the door.
		owners = owners.OrderByDescending( o => o.Subject.Length ).ToList();

		var bySubject = new Dictionary<string, Model>();

		foreach ( var track in doc.SkeletonTracks.Where( t => t.Keyframes.Count > 0 ) )
		{
			if ( RigTrackName.SubjectOf( track.BoneName ) == RigTrackName.RootSubject )
				continue;

			var owner = owners.FirstOrDefault( o => track.BoneName.StartsWith( o.Subject + RigTrackName.Separator, StringComparison.Ordinal ) );

			if ( owner.Subject is null )
				continue;

			bySubject.TryAdd( owner.Subject, owner.Model );
		}

		foreach ( var (subject, model) in bySubject )
		{
			var modelPath = PathOf( model );

			if ( modelPath is null )
			{
				skipped.Add( subject );
				continue;
			}

			try
			{
				var skeleton = SkeletonFromModel( model );
				var clip = EffigyAnimExport.ToClip( doc, skeleton, clipName, looping, subject, out var matched, out _ );

				if ( matched == 0 )
				{
					skipped.Add( subject );
					continue;
				}

				var stem = $"{fileStem}_{Sanitise( subject )}";
				var dmxFile = $"{stem}.dmx";
				var vmdlFile = $"{stem}.vmdl";
				var dmxAsset = string.IsNullOrEmpty( relativeFolder ) ? dmxFile : $"{relativeFolder}/{dmxFile}";
				var vmdlAsset = string.IsNullOrEmpty( relativeFolder ) ? vmdlFile : $"{relativeFolder}/{vmdlFile}";

				DmxAnimWriter.WriteFile( Path.Combine( folder, dmxFile ), skeleton, clip, Path.GetFileNameWithoutExtension( modelPath ) );
				File.WriteAllText( Path.Combine( folder, vmdlFile ), AnimationOnlyVmdl( modelPath, new VmdlAnimation.ClipEntry( clipName, dmxAsset, looping ) ) );

				var asset = AssetSystem.FindByPath( vmdlAsset ) ?? AssetSystem.RegisterFile( Path.Combine( folder, vmdlFile ) );
				asset?.Compile( true );

				written.Add( vmdlAsset );
				Log.Info( $"[Marionette] exported {vmdlAsset} for '{subject}' - {matched}/{skeleton.Count} bone(s), compiled={asset is not null && !asset.IsCompileFailed}" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[Marionette] could not export the bones of '{subject}': {e.Message}" );
				skipped.Add( subject );
			}
		}

		var lines = new List<string>();

		if ( written.Count > 0 )
			lines.Add( $"Props with skeletons of their own were exported beside it, one .vmdl each: {string.Join( ", ", written )}." );

		if ( skipped.Count > 0 )
			lines.Add( $"The keyed bones of {string.Join( ", ", skipped )} were not exported - an object needs a compiled model (not an OBJ) whose skeleton has those bones." );

		note = lines.Count > 0 ? string.Join( " ", lines ) : null;
		return written;
	}

	/// <summary>Whole-part tracks move objects, not bones, and a .vmdl animation is bone channels.</summary>
	static string PartNote( RigAnimDocument doc )
	{
		var parts = doc.PartTracks.Count( t => t.Keyframes.Count > 0 );

		if ( parts == 0 )
			return null;

		return $"This clip moves {parts} whole object(s) or part(s). A .vmdl animation is bone channels and "
			+ "has nowhere to put an object's own transform, so those are not in it. RigAnimPlayerComponent "
			+ "plays them - bind each object under Parts.";
	}

	/// <summary>Morph events are not bone channels either.</summary>
	static string MorphNote( RigAnimDocument doc )
	{
		var morphs = doc.MorphEvents?.Count( m => m is not null ) ?? 0;

		if ( morphs == 0 )
			return null;

		return $"This clip has {morphs} morph event(s). They are not in the .vmdl - a morph in a compiled "
			+ "animation needs the model's own flex controllers, which this export does not author. "
			+ "RigAnimPlayerComponent applies them on the same clock as the bones.";
	}

	/// <summary>Scale is the one channel built rather than copied from a reference export - say so.</summary>
	static string ScaleNote( AnimClip clip ) =>
		clip.Scales is null ? null
			: "Some bones scale in this clip. Scale goes out as a third channel per scaled bone, alongside "
			+ "position and rotation; check it in game, since every other channel here was copied from a "
			+ "reference export and this one could not be.";

	/// <summary>
	/// What to say about a clip's lights, which a .vmdl animation cannot carry.
	///
	/// There is nowhere in the format for one - it is bone channels - so a light ticked for
	/// export goes out through RigAnimPlayerComponent instead, which spawns it beside the model.
	/// Saying nothing here would make that look like a bug the first time somebody exported a
	/// lit clip and got an unlit one.
	/// </summary>
	static string LightNote( RigAnimDocument doc )
	{
		var exported = doc.Lights?.Count( l => l is not null && l.Export && l.Enabled ) ?? 0;

		if ( exported == 0 )
			return null;

		return $"This clip has {exported} light(s) marked Export With Clip. A .vmdl animation is "
			+ "bone channels and cannot carry a light, so they are not in this file. Play the "
			+ "clip with a RigAnimPlayerComponent instead and it spawns them beside the model.";
	}

	/// <summary>
	/// What to say about a clip's cameras, which ride in a separate .vdmx shot file rather than
	/// the .vmdl animation.
	/// </summary>
	static string CameraNote( RigAnimDocument doc, string vdmxAsset )
	{
		var exported = doc.Cameras?.Count( c => c is not null && c.Export && c.Enabled ) ?? 0;

		if ( exported == 0 )
			return null;

		var keyed = doc.Cameras.Count( c => c is not null && c.Export && c.Enabled && doc.FindCameraTrack( c.Name ) is { Keyframes.Count: > 0 } );

		return $"This clip has {exported} camera(s) marked Export With Clip. They are not in the "
			+ ".vmdl - that file is bone channels - so they were written to "
			+ $"{vdmxAsset} instead."
			+ (keyed > 0
				? $" {keyed} of them move, and the shot file holds each one where it sits unkeyed; the move "
					+ "plays through RigAnimPlayerComponent, which follows the keys."
				: "");
	}

	static string JoinNote( string a, string b )
	{
		if ( string.IsNullOrEmpty( a ) )
			return b;

		if ( string.IsNullOrEmpty( b ) )
			return a;

		return a + "\n\n" + b;
	}

	static Result Fail( string error ) => new() { Ok = false, Error = error };

	/// <summary>
	/// The model's bind-pose skeleton, in parent-before-child order, which is what
	/// <see cref="Skeleton.AddBone"/> requires and what the DMX writer walks.
	///
	/// Bind locals are converted the same way RigViewport.BindPoseFor does: despite the name,
	/// Bone.LocalTransform is MODEL space, and stacking it as parent-space explodes the mesh.
	/// </summary>
	public static Skeleton SkeletonFromModel( Model model )
	{
		var skeleton = new Skeleton();

		if ( model?.Bones is not { } bones )
			return skeleton;

		var remaining = bones.AllBones.ToList();
		var indexByName = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );

		while ( remaining.Count > 0 )
		{
			var next = remaining.Find( b =>
				b.Parent is null || indexByName.ContainsKey( b.Parent.Name ) );

			if ( next is null )
			{
				// A parent the collection does not list - treat the rest as roots rather than
				// hanging the export. Better a slightly wrong hierarchy than no file.
				next = remaining[0];
			}

			remaining.Remove( next );

			if ( string.IsNullOrWhiteSpace( next.Name ) || indexByName.ContainsKey( next.Name ) )
				continue;

			var parent = next.Parent is { } p && indexByName.TryGetValue( p.Name, out var parentIndex )
				? parentIndex
				: -1;

			var local = BindLocal( next );

			skeleton.AddBone( next.Name, parent, EffigyAnimExport.ToXform( local ) );
			indexByName[next.Name] = skeleton.Count - 1;
		}

		return skeleton;
	}

	static Transform BindLocal( BoneCollection.Bone bone ) =>
		bone.Parent is { } parent
			? parent.LocalTransform.ToLocal( bone.LocalTransform )
			: bone.LocalTransform;

	static string PathOf( Model model )
	{
		if ( model is null )
			return null;

		var path = model.Name;

		return string.IsNullOrWhiteSpace( path ) ? null : path.Replace( '\\', '/' );
	}

	static string FolderOf( Asset riganim, string assetsRoot )
	{
		var relative = (riganim.Path ?? "").Replace( '/', Path.DirectorySeparatorChar );
		var abs = Path.GetFullPath( Path.Combine( assetsRoot, relative ) );
		var folder = Path.GetDirectoryName( abs );

		if ( string.IsNullOrWhiteSpace( folder ) )
			return null;

		var full = Path.GetFullPath( folder );

		return full.StartsWith( assetsRoot, StringComparison.OrdinalIgnoreCase ) ? full : null;
	}

	/// <summary>
	/// Animation-only vmdl: Base Model is the original, children are just the AnimationList.
	/// Mesh, physics and materials stay on the base — putting them here is how you accidentally
	/// replace the model instead of extending it.
	/// </summary>
	static string AnimationOnlyVmdl( string baseModel, VmdlAnimation.ClipEntry clip ) =>
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n"
		+ "{\n"
		+ "\trootNode = \n"
		+ "\t{\n"
		+ "\t\t_class = \"RootNode\"\n"
		+ "\t\tchildren = \n"
		+ "\t\t[\n"
		+ VmdlAnimation.AnimationList( clip )
		+ "\t\t]\n"
		+ "\t\tmodel_archetype = \"\"\n"
		+ "\t\tprimary_associated_entity = \"\"\n"
		+ "\t\tanim_graph_name = \"\"\n"
		+ $"\t\tbase_model_name = \"{baseModel}\"\n"
		+ "\t}\n"
		+ "}\n";

	static string Sanitise( string name )
	{
		var chars = name.Select( c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' ? c : '_' )
			.ToArray();

		var cleaned = new string( chars ).Trim( '_' );

		return string.IsNullOrEmpty( cleaned ) ? "clip" : cleaned.ToLowerInvariant();
	}
}
