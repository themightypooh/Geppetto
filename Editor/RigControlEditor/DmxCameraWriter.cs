using Effigy;
using Marionette;
using Marionette.EditorTools;
using Sandbox;
using System.Collections.Generic;
using System.IO;

namespace Marionette.Tools;

/// <summary>
/// Writes a clip's exported cameras out as a .vdmx - the DMX file a Source 2 tool reads for a
/// camera shot.
///
/// HONESTY ABOUT THIS FILE. The bone-animation DMX writer (DmxAnimWriter) was copied, attribute by
/// attribute, from what the engine's own fbx2dmx.exe emits for a shipping clip - that is the
/// project's standard for "a DMX we are sure the engine loads". There is no fbx2dmx for cameras and
/// no .vdmx anywhere in the install to copy from, so this writer is BUILT TO THE DOCUMENTED SHAPE
/// RATHER THAN COPIED FROM A REFERENCE, and it has not been confirmed to load. Before relying on
/// it, validate it the same way the rest of this format was validated:
///
///   dmxconvert.exe -i <stem>.vdmx -o check.vdmx
///
/// dmxconvert reports the first thing wrong, with a line number, in about a second. The element
/// and attribute spellings below (DmeCamera, transform, fov, znear, zfar) are the working set from
/// Source 2's camera DMX; the header and array name are the two guesses most likely to need
/// adjusting.
/// </summary>
internal static class DmxCameraWriter
{
	/// <summary>
	/// The cameras as a DMX document: a root element holding one DmeCamera per shot, each with its
	/// own inline transform and lens.
	/// </summary>
	public static string Write( IReadOnlyList<RigCamera> cameras )
	{
		var w = new DmxText();

		w.Raw( "<!-- dmx encoding keyvalues2 1 format camera 1 -->" );
		w.Raw( "" );

		var idRoot = w.NextId();

		w.OpenElement( "DmElement", idRoot, "root" );

		w.OpenArray( "cameras", "element_array" );

		foreach ( var camera in cameras )
			WriteCamera( w, camera );

		w.CloseArray();

		w.CloseElement();

		return w.ToString();
	}

	public static void WriteFile( string path, IReadOnlyList<RigCamera> cameras ) =>
		File.WriteAllText( path, Write( cameras ) );

	static void WriteCamera( DmxText w, RigCamera camera )
	{
		var name = string.IsNullOrWhiteSpace( camera.Name ) ? "camera" : camera.Name;

		w.OpenArrayElement( "DmeCamera", w.NextId(), name );

		WriteTransform( w, EffigyAnimExport.ToXform( camera.World ) );

		w.Attribute( "fov", "float", DmxText.Number( camera.FieldOfView ) );
		w.Attribute( "znear", "float", DmxText.Number( camera.ZNear ) );
		w.Attribute( "zfar", "float", DmxText.Number( camera.ZFar ) );

		w.CloseElement();
	}

	/// <summary>The camera's transform, inline the same way the model writer hangs a DmeTransform
	/// off a joint.</summary>
	static void WriteTransform( DmxText w, Xform x )
	{
		w.OpenAttributeElement( "transform", "DmeTransform", w.NextId(), "transform" );
		w.Attribute( "position", "vector3", DmxText.Vector3( x.Origin ) );
		w.Attribute( "orientation", "quaternion", DmxText.Quaternion( x ) );
		w.CloseElement();
	}
}
