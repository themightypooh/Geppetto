using Effigy;

namespace Marionette.ShaderForgeEditor;

/// <summary>Which built-in primitive the preview model picker can show before any project model
/// is loaded (that scan is Phase 2 — see the tool design doc).</summary>
internal enum ShaderForgePrimitiveKind
{
	Sphere,
	Cube,
	Plane,
	Cylinder,
}

/// <summary>
/// Builds the model picker's stock shapes from the Effigy kernel rather than shipping separate
/// preview meshes — <c>Effigy.Primitives</c> already produces exactly these shapes with clean
/// normals, and <c>EffigyPreview</c> (see Editor/EffigyEditor) already turns a PolyMesh into a
/// runtime Model with a dev material. Reusing both means Shader Forge carries zero geometry code
/// of its own.
/// </summary>
internal static class ShaderForgePrimitives
{
	public static string Label( ShaderForgePrimitiveKind kind ) => kind switch
	{
		ShaderForgePrimitiveKind.Sphere => "Sphere",
		ShaderForgePrimitiveKind.Cube => "Cube",
		ShaderForgePrimitiveKind.Plane => "Plane",
		ShaderForgePrimitiveKind.Cylinder => "Cylinder",
		_ => kind.ToString(),
	};

	public static Sandbox.Model Build( ShaderForgePrimitiveKind kind )
	{
		var mesh = kind switch
		{
			ShaderForgePrimitiveKind.Sphere => Primitives.QuadSphere( radius: 24f, segments: 4 ),
			ShaderForgePrimitiveKind.Cube => Primitives.Box( sizeX: 40f, sizeY: 40f, sizeZ: 40f ),
			ShaderForgePrimitiveKind.Plane => Primitives.Plane( sizeX: 48f, sizeY: 48f ),
			ShaderForgePrimitiveKind.Cylinder => Primitives.Cylinder( radius: 20f, height: 44f, segments: 24 ),
			_ => Primitives.QuadSphere( radius: 24f, segments: 4 ),
		};

		return Marionette.EditorTools.EffigyPreview.Build( mesh );
	}
}
