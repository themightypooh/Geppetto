using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Turn on <c>F_MORPH_SUPPORTED</c> in a .vmat - the material switch s&amp;box's shaders need before
/// the GPU applies a mesh's morphs (core/shaders/vr_common_features.fxc: <c>Feature( F_MORPH_SUPPORTED,
/// 0..1, "Animation" )</c>; Facepunch's human vmats set it as <c>F_MORPH_SUPPORTED 1</c> under an
/// "Animation" heading right after the shader line - see SkinMaterial).
///
/// ONLY EFFIGY'S OWN FILES. A material the export did not write (a library or engine vmat) is never
/// touched: <see cref="Enable"/> leaves any text without Effigy's AUTO-GENERATED header as it is.
/// The export calls this for every material on a mesh with shape keys; a writer that already sets
/// the flag (SkinMaterial with Morphs on) is left alone.
/// </summary>
public static class VmatMorph
{
	/// <summary>Whether <paramref name="vmat"/> already turns morphs on.</summary>
	public static bool IsOn( string vmat ) => vmat?.Contains( "F_MORPH_SUPPORTED 1" ) == true;

	/// <summary>The vmat with the flag on, or null when it is not Effigy's to change (or has no
	/// shader line to put it under). Unchanged text when it is already on.</summary>
	public static string Enable( string vmat )
	{
		if ( vmat is null || !vmat.Contains( "AUTO-GENERATED" ) )
			return null;

		if ( IsOn( vmat ) )
			return vmat;

		var shader = vmat.IndexOf( "shader \"", StringComparison.Ordinal );

		if ( shader < 0 )
			return null;

		var lineEnd = vmat.IndexOf( '\n', shader );

		if ( lineEnd < 0 )
			return null;

		var newline = lineEnd > 0 && vmat[lineEnd - 1] == '\r' ? "\r\n" : "\n";
		return vmat.Insert( lineEnd + 1, $"{newline}\t//---- Animation ----{newline}\tF_MORPH_SUPPORTED 1{newline}" );
	}

	/// <summary>
	/// Turn the flag on in every material file <paramref name="mesh"/>'s faces use, the same pass the
	/// editor's Compile runs after writing a DMX with shape keys. <paramref name="pathFor"/> maps a
	/// material name (what the slot exports as) to its file, or null when it has none. Returns one
	/// line per material that needed something said: turned on, or why it could not be.
	/// </summary>
	public static List<string> EnableFiles( PolyMesh mesh, Func<int, string> nameForSlot, Func<string, string> pathFor )
	{
		var said = new List<string>();

		if ( mesh is null || !mesh.HasMorphs )
			return said;

		foreach ( var slot in mesh.Faces.Select( f => f.Material ).Distinct().OrderBy( s => s ) )
		{
			var name = nameForSlot( slot );
			var path = string.IsNullOrWhiteSpace( name ) ? null : pathFor( name );

			if ( path is null || !File.Exists( path ) )
			{
				said.Add( $"slot {slot} ({name}) has no .vmat file at {path ?? "(no path)"}, so its shape keys will not show "
					+ $"until its material sets F_MORPH_SUPPORTED 1 (Animation). Assign one with `material {slot} materials/x.vmat`." );
				continue;
			}

			var text = File.ReadAllText( path );

			if ( IsOn( text ) )
				continue;

			if ( Enable( text ) is { } on )
			{
				File.WriteAllText( path, on );
				said.Add( $"{name}: F_MORPH_SUPPORTED on, for the shape keys" );
			}
			else
				said.Add( $"{name} is not an Effigy-written material, so its shape keys will not show until it sets F_MORPH_SUPPORTED 1 (Animation)" );
		}

		return said;
	}
}
