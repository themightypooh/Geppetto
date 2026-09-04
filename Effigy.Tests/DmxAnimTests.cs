using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The animation DMX, parsed rather than searched for substrings.
///
/// SAME LESSON AS DmxGrammarTests, which is why this file exists at all rather than a handful of
/// Contains() checks bolted onto that one. Every mistake this format can make produces a file that
/// looks right in a text editor and fails as "Couldn't load DMX file" with no line number: a
/// missing comma between array members, a bare id where the two-token reference form belongs, a
/// channel pointing at a DmeJoint instead of its DmeTransform. Only a parse sees any of them.
///
/// WHAT A HEADLESS TEST CAN AND CANNOT SETTLE HERE. It can settle that the document parses, that
/// its channels reach real bones, that the time and value arrays agree in length, and that the
/// poses that went in are the poses that came out. It cannot settle whether ModelDoc likes the
/// result — that is a compile, and the sample file the suite writes to out/ is there to be
/// compiled. `dmxconvert.exe -i out/sample_anim.dmx -o check.dmx` is the intermediate step, and it
/// reports the first structural fault with a line number in about a second.
/// </summary>
public static class DmxAnimTests
{
	public static void Run()
	{
		Section( "animation DMX: the document parses as KeyValues2" );
		TestParses();

		Section( "animation DMX: the channels reach the bones they name" );
		TestChannels();

		Section( "animation DMX: the poses that went in come back out" );
		TestValues();

		Section( "animation clips refuse to be written wrong" );
		TestContract();

		Section( "vmdl animation: clips in the AnimationList" );
		TestVmdlNode();
	}

	// --- fixtures -----------------------------------------------------------------------------

	/// <summary>A two-bone chain, which is the smallest rig where a parent-relative pose can be
	/// wrong in a way a single bone would hide.</summary>
	static Skeleton Chain()
	{
		var skeleton = new Skeleton();
		var root = skeleton.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ) );
		skeleton.AddBoneFromPoints( "child", root, new Vec3( 0, 1, 0 ), new Vec3( 0, 2, 0 ) );

		return skeleton;
	}

	/// <summary>A clip that moves: the child bone swings through a quarter turn while the root
	/// travels. A clip of identical frames would pass a length check and hide a writer that
	/// emitted frame zero every time.</summary>
	static AnimClip Swing( Skeleton skeleton, int frames = 4 )
	{
		var clip = new AnimClip { Name = "swing", FrameRate = 30f, Looping = true };

		for ( var f = 0; f < frames; f++ )
		{
			var t = f / (float)(frames - 1);
			var pose = new Xform[skeleton.Count];

			pose[0] = Xform.Translate( new Vec3( t * 10f, 0, 0 ) );
			pose[1] = Xform.Rotate( new Vec3( 0, 0, 1 ), t * MathF.PI / 2f )
				* Xform.Translate( new Vec3( 0, 1, 0 ) );

			clip.AddFrame( pose );
		}

		return clip;
	}

	// --- the checks ---------------------------------------------------------------------------

	static void TestParses()
	{
		var skeleton = Chain();
		var text = DmxAnimWriter.Write( skeleton, Swing( skeleton ), "anim_grammar" );

		var root = DmxGrammarTests.Parse( text, out var error );

		Check( "a clip export parses", root is not null, error );

		if ( root is null )
			return;

		// The two mistakes the mesh writer made, which this writer inherits the shape of and could
		// therefore make again. Named individually so a failure says which one came back.
		Check( "every element_array member is comma-separated",
			!text.Contains( "}\n\t\t\t\"Dme" ) && !text.Contains( "}\n\t\t\"Dme" ),
			"an element sits directly against the next member's type name" );

		Check( "references are the two-token form, not a bare id",
			!System.Text.RegularExpressions.Regex.IsMatch( text, "\n\\s*\"[0-9a-f]{8}-" ),
			"a bare quoted id would be read as an element type name" );

		Check( "no array ends on a trailing comma",
			!System.Text.RegularExpressions.Regex.IsMatch( text, ",\\s*\\]" ) );

		// A one-bone rig with a one-frame clip is the degenerate end, where an off-by-one in
		// duration or in the comma trimming has nowhere to hide.
		var single = Skeleton.SingleRoot();
		var still = new AnimClip { Name = "still" };
		still.AddFrame( new[] { Xform.Identity } );

		Check( "a single-bone single-frame clip parses",
			DmxGrammarTests.Parse( DmxAnimWriter.Write( single, still ), out var stillError ) is not null,
			stillError );
	}

	static void TestChannels()
	{
		var skeleton = Chain();
		var clip = Swing( skeleton );
		var root = DmxGrammarTests.Parse( DmxAnimWriter.Write( skeleton, clip, "anim_grammar" ), out var error );

		if ( root is null )
		{
			Check( "the tree is readable", false, error );
			return;
		}

		// animationList hangs off the ROOT, beside skeleton and model. Inside the DmeModel is
		// where it looks like it belongs and is not where fbx2dmx puts it.
		var list = root.Element( "animationList" );

		Check( "the root carries a DmeAnimationList",
			list is not null && list.Type == "DmeAnimationList",
			list is null ? "no animationList attribute on the root element" : list.Type );

		Check( "the skeleton is still there beside it",
			root.Element( "skeleton" )?.Type == "DmeModel" );

		var channels = DmxGrammarTests.FindAll( root, "DmeChannel" );

		// Two per bone, position and orientation. One channel carrying both does not exist.
		Check( "two channels per bone", channels.Count == skeleton.Count * 2,
			$"{channels.Count} channels for {skeleton.Count} bone(s)" );

		foreach ( var bone in skeleton.Bones )
		{
			Check( $"{bone.Name} has a position channel",
				channels.Any( c => c.Name == $"{bone.Name}_p" ) );
			Check( $"{bone.Name} has an orientation channel",
				channels.Any( c => c.Name == $"{bone.Name}_o" ) );
		}

		Check( "position channels write the position attribute",
			channels.Where( c => c.Name.EndsWith( "_p" ) ).All( c => c.Values.GetValueOrDefault( "toAttribute" ) == "position" ) );

		Check( "orientation channels write the orientation attribute",
			channels.Where( c => c.Name.EndsWith( "_o" ) ).All( c => c.Values.GetValueOrDefault( "toAttribute" ) == "orientation" ) );

		// THE ONE THAT IS EASY TO GET WRONG AND IMPOSSIBLE TO SEE. A channel targets the bone's
		// DmeTransform. Pointing it at the DmeJoint parses, loads, and animates nothing.
		var transformIds = DmxGrammarTests.FindAll( root, "DmeTransform" )
			.Select( t => t.Values.GetValueOrDefault( "id" ) )
			.Where( id => id is not null )
			.ToHashSet();

		var jointIds = DmxGrammarTests.FindAll( root, "DmeJoint" )
			.Select( j => j.Values.GetValueOrDefault( "id" ) )
			.Where( id => id is not null )
			.ToHashSet();

		var targets = channels.Select( c => c.Values.GetValueOrDefault( "toElement" ) ).ToList();

		Check( "every channel targets a DmeTransform that exists",
			targets.All( t => t is not null && transformIds.Contains( t ) ),
			"a channel points at an id no DmeTransform in this document has" );

		Check( "and no channel targets a DmeJoint",
			targets.All( t => !jointIds.Contains( t ) ),
			"a joint-targeted channel animates nothing and reports no error" );

		Check( "every channel is mode 3, as fbx2dmx writes them",
			channels.All( c => c.Values.GetValueOrDefault( "mode" ) == "3" ) );

		Check( "nothing drives these channels",
			channels.All( c => c.Values.GetValueOrDefault( "fromElement" ) == "" ) );
	}

	static void TestValues()
	{
		var skeleton = Chain();
		var clip = Swing( skeleton, 4 );
		var root = DmxGrammarTests.Parse( DmxAnimWriter.Write( skeleton, clip ), out var error );

		if ( root is null )
		{
			Check( "the tree is readable", false, error );
			return;
		}

		var channels = DmxGrammarTests.FindAll( root, "DmeChannel" );
		var rootPos = channels.FirstOrDefault( c => c.Name == "root_p" );

		Check( "the root's position channel is there", rootPos is not null );

		if ( rootPos is null )
			return;

		var layer = DmxGrammarTests.FindFirst( rootPos, "DmeVector3LogLayer" );

		Check( "it carries a log layer", layer is not null );

		if ( layer is null )
			return;

		var times = layer.Array( "times" );
		var values = layer.Array( "values" );

		// A times array that disagrees with its values array is a clip that plays at the wrong
		// speed or truncates, depending on which is shorter, and never says so.
		Check( "one time per frame", times.Count == clip.FrameCount,
			$"{times.Count} times for {clip.FrameCount} frames" );
		Check( "one value per frame", values.Count == clip.FrameCount,
			$"{values.Count} values for {clip.FrameCount} frames" );

		Check( "curvetypes is present and empty", layer.Arrays.ContainsKey( "curvetypes" )
			&& layer.Array( "curvetypes" ).Count == 0,
			"an absent array and an empty one are different statements to this reader" );

		// Frame times come from the frame rate, not from an index. At 30fps frame 3 is 0.1s.
		Check( "frame times follow the frame rate",
			times.Count == 4 && times[0].Value == "0.0000" && times[3].Value == "0.1000",
			times.Count == 4 ? $"{times[0].Value} .. {times[3].Value}" : "wrong count" );

		// The values themselves: the root travels 0 -> 10 on x across the clip.
		Check( "the first frame's position is the one that went in",
			values.Count == 4 && values[0].Value == "0 0 0", values.FirstOrDefault()?.Value );
		Check( "and the last frame's is too",
			values.Count == 4 && values[3].Value == "10 0 0", values.LastOrDefault()?.Value );

		// A writer that emitted frame zero every time would pass every length check above.
		Check( "the frames are not all the same",
			values.Select( v => v.Value ).Distinct().Count() == 4,
			"every frame wrote the same value" );

		var clipNode = DmxGrammarTests.FindFirst( root, "DmeChannelsClip" );

		Check( "the clip keeps its name", clipNode?.Name == "swing", clipNode?.Name );

		// N frames span N-1 intervals: four frames at 30fps is a tenth of a second, not two
		// fifteenths. Off by one here stretches every clip by a frame.
		var timeFrame = DmxGrammarTests.FindFirst( root, "DmeTimeFrame" );

		Check( "duration counts intervals, not frames",
			timeFrame?.Values.GetValueOrDefault( "duration" ) == "0.1000",
			timeFrame?.Values.GetValueOrDefault( "duration" ) );
	}

	static void TestContract()
	{
		var skeleton = Chain();

		Check( "a clip with no frames is refused", Throws( () =>
			DmxAnimWriter.Write( skeleton, new AnimClip { Name = "empty" } ) ) );

		// A ragged frame list comes from a sampler that grew a bone mid-loop. Downstream it is an
		// index walk off the end of an array in the middle of writing a file.
		var ragged = new AnimClip { Name = "ragged" };
		ragged.AddFrame( new[] { Xform.Identity, Xform.Identity } );
		ragged.AddFrame( new[] { Xform.Identity } );

		Check( "a ragged frame list is refused", Throws( () => DmxAnimWriter.Write( skeleton, ragged ) ) );

		var wrongWidth = new AnimClip { Name = "narrow" };
		wrongWidth.AddFrame( new[] { Xform.Identity } );

		Check( "a frame that does not cover every bone is refused",
			Throws( () => DmxAnimWriter.Write( skeleton, wrongWidth ) ) );

		Check( "a null skeleton is refused", Throws( () => DmxAnimWriter.Write( null, Swing( skeleton ) ) ) );
		Check( "a null clip is refused", Throws( () => DmxAnimWriter.Write( skeleton, null ) ) );

		var zeroRate = new AnimClip { Name = "zero", FrameRate = 0f };
		zeroRate.AddFrame( new[] { Xform.Identity, Xform.Identity } );

		Check( "a zero frame rate is refused", Throws( () => DmxAnimWriter.Write( skeleton, zeroRate ) ) );

		// Validate is the same answer without the throw, so a UI can say what is wrong before it
		// asks for a file.
		Check( "Validate names the problem rather than just failing",
			ragged.Validate( skeleton )?.Contains( "frame 1" ) == true, ragged.Validate( skeleton ) );

		Check( "and says nothing about a clip that is fine",
			Swing( skeleton ).Validate( skeleton ) is null );

		// Two exports of the same clip are byte-identical, which is what makes a diff mean
		// something when one of them stops working.
		Check( "the writer is deterministic",
			DmxAnimWriter.Write( skeleton, Swing( skeleton ) ) == DmxAnimWriter.Write( skeleton, Swing( skeleton ) ) );
	}

	static void TestVmdlNode()
	{
		// No clips: byte-identical to what BindPoseList always wrote, because every existing
		// skinned export goes through this path now.
		Check( "an empty animation list is still just the bind pose",
			VmdlAnimation.AnimationList() == VmdlAnimation.BindPoseList() );

		Check( "and it carries exactly one AnimBindPose",
			Count( VmdlAnimation.BindPoseList(), "_class = \"AnimBindPose\"" ) == 1 );

		var node = VmdlAnimation.AnimationList(
			new VmdlAnimation.ClipEntry( "wave", "models/effigy/wave.dmx", true ),
			new VmdlAnimation.ClipEntry( "idle", "models/effigy/idle.dmx" ) );

		Check( "clips join the bind pose rather than replacing it",
			Count( node, "_class = \"AnimBindPose\"" ) == 1 && Count( node, "_class = \"AnimFile\"" ) == 2,
			$"{Count( node, "_class = \"AnimBindPose\"" )} bind pose(s), {Count( node, "_class = \"AnimFile\"" )} clip(s)" );

		Check( "still one AnimationList", Count( node, "_class = \"AnimationList\"" ) == 1 );

		Check( "braces balance", Count( node, "{" ) == Count( node, "}" ) );
		Check( "and brackets balance", Count( node, "[" ) == Count( node, "]" ) );
		Check( "the node is a complete child entry, comma and all", node.TrimEnd( '\n' ).EndsWith( "}," ) );

		Check( "each clip names itself and its source file",
			node.Contains( "name = \"wave\"" ) && node.Contains( "source_filename = \"models/effigy/wave.dmx\"" ) );

		Check( "looping is carried per clip, not shared",
			node.Contains( "looping = true" ) && node.Contains( "looping = false" ) );

		// EVERY FIELD, for the same reason as the bind pose: the compiler's defaults are not
		// documented anywhere this project can read, and the file known to work carries all of them.
		foreach ( var field in new[]
		{
			"name", "activity_name", "activity_weight", "weight_list_name", "fade_in_time",
			"fade_out_time", "looping", "delta", "worldSpace", "hidden", "anim_markup_ordered",
			"disable_compression", "disable_interpolation", "enable_scale", "source_filename",
			"start_frame", "end_frame", "framerate", "take", "reverse",
		} )
		{
			Check( $"an AnimFile carries {field}",
				VmdlAnimation.AnimFile( new VmdlAnimation.ClipEntry( "c", "c.dmx" ) ).Contains( $"{field} = " ),
				"missing" );
		}

		// -1 means "the whole file at its own rate". A real number here trims or resamples the
		// clip, which is a silently shorter animation rather than an error.
		var one = VmdlAnimation.AnimFile( new VmdlAnimation.ClipEntry( "c", "c.dmx" ) );

		Check( "the frame range says 'all of it'",
			one.Contains( "start_frame = -1" ) && one.Contains( "end_frame = -1" ) );
		Check( "and the framerate defers to the source",
			one.Contains( "framerate = -1.0" ) );
	}

	// --- the sample ---------------------------------------------------------------------------

	/// <summary>
	/// A clip on the rigged sample's own skeleton, plus a .vmdl that compiles the two together —
	/// the whole animation path as files, so the engine can pass judgement on it:
	///
	///     bin/win64/dmxconvert.exe -i out/sample_anim.dmx -o check.dmx
	///     copy out/sample_{rigged,anim}.dmx and out/sample_anim.vmdl into Assets/models/effigy_probe/
	///     register_external_assets, asset_compile, then load it and ask for its animation list
	///
	/// A model that compiles and reports a clip called "wave" is the answer. The suite cannot get
	/// that far on its own — the compiler is the engine's — but the files are the whole input, so
	/// nothing else has to be running to check them.
	///
	/// THE SKELETON IS THE CALLER'S, not one built here, and that is the point of the parameter.
	/// A clip written against its own idea of the rig would compile and animate nothing, which is
	/// exactly the failure this sample exists to make visible.
	/// </summary>
	internal static void WriteSample( string outDir, Skeleton skeleton )
	{
		// A quarter turn of the child bone over half a second, which is enough movement to see in
		// a preview and short enough to scrub through.
		var clip = new AnimClip { Name = "wave", FrameRate = 30f, Looping = true };

		for ( var f = 0; f < 16; f++ )
		{
			var t = f / 15f;
			var pose = new Xform[skeleton.Count];

			pose[0] = skeleton.Bones[0].Local;
			pose[1] = Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.Sin( t * MathF.Tau ) * 0.5f )
				* skeleton.Bones[1].Local;

			clip.AddFrame( pose );
		}

		DmxAnimWriter.WriteFile( System.IO.Path.Combine( outDir, "sample_anim.dmx" ),
			skeleton, clip, "sample_rigged" );

		var vmdl =
			"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n"
			+ "{\n\trootNode = \n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren = \n\t\t[\n"
			+ "\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren = \n\t\t\t\t[\n"
			+ "\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"RenderMeshFile\"\n\t\t\t\t\t\tname = \"Body_LOD0\"\n"
			+ "\t\t\t\t\t\tchildren = \n\t\t\t\t\t\t[\n\t\t\t\t\t\t]\n"
			+ "\t\t\t\t\t\tfilename = \"models/effigy_probe/sample_rigged.dmx\"\n"
			+ "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n"
			+ "\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]\n"
			+ "\t\t\t\t\t\timport_scale = 1.0\n"
			+ "\t\t\t\t\t\talign_origin_x_type = \"None\"\n"
			+ "\t\t\t\t\t\talign_origin_y_type = \"None\"\n"
			+ "\t\t\t\t\t\talign_origin_z_type = \"None\"\n"
			+ "\t\t\t\t\t\tparent_bone = \"\"\n\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n"
			+ VmdlAnimation.BoneMarkupList( skeleton )
			+ VmdlAnimation.AnimationList(
				new VmdlAnimation.ClipEntry( "wave", "models/effigy_probe/sample_anim.dmx", true ) )
			+ "\t\t]\n\t\tmodel_archetype = \"\"\n\t\tprimary_associated_entity = \"\"\n"
			+ "\t\tanim_graph_name = \"\"\n\t\tbase_model_name = \"\"\n\t}\n}\n";

		System.IO.File.WriteAllText( System.IO.Path.Combine( outDir, "sample_anim.vmdl" ), vmdl );

		Check( $"wrote {outDir}/sample_anim.dmx and .vmdl - compile them and the model should carry a 'wave' clip",
			System.IO.File.Exists( System.IO.Path.Combine( outDir, "sample_anim.dmx" ) )
			&& Count( vmdl, "_class = \"AnimFile\"" ) == 1
			&& Count( vmdl, "_class = \"AnimBindPose\"" ) == 1
			&& Count( vmdl, "{" ) == Count( vmdl, "}" ) );
	}

	// --- helpers ------------------------------------------------------------------------------

	static bool Throws( Action action )
	{
		try
		{
			action();
			return false;
		}
		catch
		{
			return true;
		}
	}

	static int Count( string text, string needle )
	{
		var n = 0;

		for ( var i = text.IndexOf( needle, StringComparison.Ordinal ); i >= 0;
			i = text.IndexOf( needle, i + needle.Length, StringComparison.Ordinal ) )
		{
			n++;
		}

		return n;
	}
}
