using System;
using System.IO;

namespace Effigy;

/// <summary>
/// DMX export — the animation path. A skeleton and a set of per-bone channels, which is what
/// ModelDoc's `AnimFile` node wants pointed at it, and therefore what puts a hand-authored clip
/// inside a compiled model where AnimGraph can reach it.
///
/// WHY THIS IS NOT SMD, WHICH IS THE OBVIOUS ANSWER AND THE WRONG ONE. A sequence SMD is the
/// classic way to hand-write animation for a Source model, SmdWriter already emits the exact
/// `skeleton` / `time N` / bone-row block a sequence needs, and extending it looks like an
/// afternoon's work. ModelDoc does not read SMD at all — see DmxWriter's header for the loader
/// string that says so in the compiler's own words. The mesh path learned that the expensive way;
/// this file exists so the animation path does not learn it again.
///
/// COPIED, NOT GUESSED — and there is a command that produces the thing to copy. The engine ships
/// `bin/win64/fbx2dmx.exe`, whose <b>-a</b> flag converts animation rather than geometry, and
/// every element, attribute and spelling below was read off its output for a shipping clip:
///
///   fbx2dmx.exe -a -i addons/citizen/Assets/models/citizen/animations/face/Citizen@Eyes_Blink.fbx -o ref.dmx
///
/// That reference is the only evidence there is about this format, and regenerating it beats
/// reasoning from this comment if anything here ever stops working. What it settled, none of which
/// is inferable from the element names alone:
///
///   - `animationList` hangs off the ROOT DmElement, beside `skeleton` and `model` — not inside
///     the DmeModel, which is where it looks like it should go;
///   - a channel targets the bone's DmeTransform, not its DmeJoint, and does it by id;
///   - each bone needs TWO channels, suffixed `_p` and `_o`, writing `position` and `orientation`
///     respectively. One channel per bone carrying both does not exist;
///   - `mode` is 3 on every channel fbx2dmx writes;
///   - the log layer's `curvetypes` array is present and EMPTY, which is what "no per-key curve
///     override" looks like. Omitting the array is a different statement.
///
/// WHAT IS DELIBERATELY LEFT OUT. The reference also carries a `compressed` binary blob on each
/// log layer — empty in every layer of it. An empty blob says nothing its absence does not, and
/// KeyValues2's binary literal is a multi-line quoted form with no second example to check a guess
/// against, so this writes no `compressed` attribute rather than an invented one.
/// `dmxconvert.exe -i clip.dmx -o check.dmx` is the one-second check that this was the right call;
/// DmxAnimTests runs the same parse with no engine involved.
/// </summary>
public static class DmxAnimWriter
{
	public static void WriteFile( string path, Skeleton skeleton, AnimClip clip, string modelName = null )
	{
		File.WriteAllText( path, Write( skeleton, clip, modelName ) );
	}

	/// <summary>
	/// The animation as a DMX document.
	///
	/// The skeleton written here has to be the SAME skeleton the mesh was exported with. ModelDoc
	/// matches a clip's channels to a model's bones by name, and a bone the clip poses that the
	/// model does not have is dropped silently — so a rig edited between the two exports gives you
	/// a clip that compiles, loads, and moves less of the model than it used to.
	/// </summary>
	public static string Write( Skeleton skeleton, AnimClip clip, string modelName = null )
	{
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		if ( clip is null )
			throw new ArgumentNullException( nameof( clip ) );

		if ( clip.Validate( skeleton ) is { } problem )
			throw new InvalidOperationException( $"Animation clip does not fit its skeleton: {problem}" );

		modelName ??= "effigy_model";

		var w = new DmxText();

		// Same id discipline as the mesh writer: counted, not random, so two exports of the same
		// clip are byte-identical and a diff shows what actually changed.
		var idRoot = w.NextId();
		var idModel = w.NextId();
		var idModelTransform = w.NextId();

		var boneDagIds = new string[skeleton.Count];
		var boneTransformIds = new string[skeleton.Count];
		var bindTransformIds = new string[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			boneDagIds[i] = w.NextId();
			boneTransformIds[i] = w.NextId();
			bindTransformIds[i] = w.NextId();
		}

		var idAnimList = w.NextId();
		var idClip = w.NextId();
		var idTimeFrame = w.NextId();

		w.Raw( $"<!-- dmx encoding keyvalues2 1 format model {DmxWriter.ModelFormatVersion} -->" );
		w.Raw( "" );

		w.OpenElement( "DmElement", idRoot, "root" );

		// No mesh child: an animation DMX carries the rig and the motion, and the geometry lives in
		// the model file this clip gets compiled into.
		DmxWriter.WriteSkeletonModel( w, skeleton, modelName, idModel, idModelTransform,
			boneDagIds, boneTransformIds, bindTransformIds, null );

		w.Attribute( "model", "element", idModel );

		WriteAnimationList( w, skeleton, clip, idAnimList, idClip, idTimeFrame, boneTransformIds );

		w.CloseElement();

		return w.ToString();
	}

	// --- pieces -------------------------------------------------------------------------------

	static void WriteAnimationList( DmxText w, Skeleton skeleton, AnimClip clip,
		string idAnimList, string idClip, string idTimeFrame, string[] boneTransformIds )
	{
		w.OpenAttributeElement( "animationList", "DmeAnimationList", idAnimList, "anim" );
		w.OpenArray( "animations", "element_array" );

		w.OpenArrayElement( "DmeChannelsClip", idClip, clip.Name );
		{
			w.OpenAttributeElement( "timeFrame", "DmeTimeFrame", idTimeFrame, "timeFrame" );
			w.Attribute( "start", "time", DmxText.Time( 0f ) );
			w.Attribute( "duration", "time", DmxText.Time( clip.Duration ) );
			w.Attribute( "offset", "time", DmxText.Time( 0f ) );
			w.Attribute( "scale", "float", "1" );
			w.CloseElement();

			w.OpenArray( "channels", "element_array" );

			// Two channels per bone, position then orientation, in skeleton order. The order is not
			// load-bearing — channels name their own target — but keeping it stable is what makes
			// two exports of the same clip diffable.
			for ( var i = 0; i < skeleton.Count; i++ )
			{
				WritePositionChannel( w, skeleton, clip, i, boneTransformIds[i] );
				WriteOrientationChannel( w, skeleton, clip, i, boneTransformIds[i] );
			}

			w.CloseArray();
		}
		w.CloseElement();

		w.CloseArray();
		w.CloseElement();
	}

	static void WritePositionChannel( DmxText w, Skeleton skeleton, AnimClip clip, int bone, string targetId )
	{
		w.OpenArrayElement( "DmeChannel", w.NextId(), $"{skeleton.Bones[bone].Name}_p" );
		WriteChannelTarget( w, targetId, "position" );

		w.OpenAttributeElement( "log", "DmeVector3Log", w.NextId(), "vector3 log" );
		w.OpenArray( "layers", "element_array" );
		w.OpenArrayElement( "DmeVector3LogLayer", w.NextId(), "vector3 log" );

		WriteTimes( w, clip );

		w.OpenArray( "values", "vector3_array" );

		for ( var f = 0; f < clip.FrameCount; f++ )
			w.ArrayValue( DmxText.Vector3( clip.Frames[f][bone].Origin ) );

		w.CloseArray();
		w.CloseElement();
		w.CloseArray();

		w.Attribute( "usedefaultvalue", "bool", "0" );
		w.Attribute( "defaultvalue", "vector3", "0 0 0" );
		w.CloseElement();

		w.CloseElement();
	}

	static void WriteOrientationChannel( DmxText w, Skeleton skeleton, AnimClip clip, int bone, string targetId )
	{
		w.OpenArrayElement( "DmeChannel", w.NextId(), $"{skeleton.Bones[bone].Name}_o" );
		WriteChannelTarget( w, targetId, "orientation" );

		w.OpenAttributeElement( "log", "DmeQuaternionLog", w.NextId(), "quaternion log" );
		w.OpenArray( "layers", "element_array" );
		w.OpenArrayElement( "DmeQuaternionLogLayer", w.NextId(), "quaternion log" );

		WriteTimes( w, clip );

		w.OpenArray( "values", "quaternion_array" );

		for ( var f = 0; f < clip.FrameCount; f++ )
			w.ArrayValue( DmxText.Quaternion( clip.Frames[f][bone] ) );

		w.CloseArray();
		w.CloseElement();
		w.CloseArray();

		w.Attribute( "usedefaultvalue", "bool", "0" );
		w.Attribute( "defaultvalue", "quaternion", "0 0 0 1" );
		w.CloseElement();

		w.CloseElement();
	}

	/// <summary>
	/// The half of a channel that says where its values go.
	///
	/// `fromElement` and `fromAttribute` are empty because nothing drives this channel — it is a
	/// stored curve, not a connection between two live elements, which is the other thing a
	/// DmeChannel gets used for. `mode` 3 is what fbx2dmx writes on every channel of an exported
	/// clip; the enum it comes from is not in anything readable here, so this is copied rather than
	/// named.
	/// </summary>
	static void WriteChannelTarget( DmxText w, string targetId, string attribute )
	{
		w.Attribute( "fromElement", "element", "" );
		w.Attribute( "fromAttribute", "string", "" );
		w.Attribute( "fromIndex", "int", "0" );
		w.Attribute( "toElement", "element", targetId );
		w.Attribute( "toAttribute", "string", attribute );
		w.Attribute( "toIndex", "int", "0" );
		w.Attribute( "mode", "int", "3" );
	}

	/// <summary>
	/// The sample times, plus the empty curvetypes array that goes with them.
	///
	/// Written once for both channel kinds because the two have to agree exactly: a position log
	/// and an orientation log with different time arrays is a bone whose translation and rotation
	/// drift apart, which reads as a rigging fault rather than an export one.
	/// </summary>
	static void WriteTimes( DmxText w, AnimClip clip )
	{
		w.OpenArray( "times", "time_array" );

		for ( var f = 0; f < clip.FrameCount; f++ )
			w.ArrayValue( DmxText.Time( clip.TimeOf( f ) ) );

		w.CloseArray();

		// Present and empty — see the header. This is "no per-key curve override", not "no curves".
		w.OpenArray( "curvetypes", "int_array" );
		w.CloseArray();
	}
}
