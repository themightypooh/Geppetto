using System;
using System.Collections.Generic;

namespace Marionette.ShaderForge;

/// <summary>What kind of control a parameter should get, and how it is declared in HLSL.</summary>
public enum ShaderParamKind
{
	/// <summary>A float with a range — a slider.</summary>
	Float,

	/// <summary>A float3 tagged UiType( Color ) — a colour picker.</summary>
	Color,

	/// <summary>A bool — a checkbox.</summary>
	Bool,
}

/// <summary>
/// One exposed shader parameter. This is the single source of truth for three separate things:
/// the HLSL declaration written into the .shad, the tweak control the panel builds, and the
/// <c>Material.Set</c> call that pushes an edited value at the live preview. Keeping them off one
/// definition is what stops the UI and the shader drifting apart.
/// </summary>
public sealed class ShaderParam
{
	/// <summary>HLSL variable name, engine convention: g_fl* float, g_v* vector, g_b* bool.</summary>
	public string Name;

	public ShaderParamKind Kind;

	/// <summary>Label shown in the material editor and in this tool's tweak panel.</summary>
	public string Label;

	/// <summary>UiGroup name — parameters from one block group together.</summary>
	public string Group;

	public float Default;
	public float Min = 0f;
	public float Max = 1f;

	/// <summary>RGB default for <see cref="ShaderParamKind.Color"/>.</summary>
	public float[] DefaultColor;

	public static ShaderParam Float( string name, string label, float def, float min, float max ) =>
		new() { Name = name, Kind = ShaderParamKind.Float, Label = label, Default = def, Min = min, Max = max };

	public static ShaderParam Color( string name, string label, float r, float g, float b ) =>
		new() { Name = name, Kind = ShaderParamKind.Color, Label = label, DefaultColor = new[] { r, g, b } };

	public static ShaderParam Bool( string name, string label, bool def ) =>
		new() { Name = name, Kind = ShaderParamKind.Bool, Label = label, Default = def ? 1f : 0f };

	/// <summary>The HLSL declaration line, including its UI annotations.</summary>
	public string Declare( int orderInGroup )
	{
		var ui = $"UiGroup( \"{Group},10/{orderInGroup}\" );";

		return Kind switch
		{
			ShaderParamKind.Color =>
				$"float3 {Name} < UiType( Color ); {ui} Default3( {F( DefaultColor[0] )}, {F( DefaultColor[1] )}, {F( DefaultColor[2] )} ); >;",

			ShaderParamKind.Bool =>
				$"bool {Name} < {ui} Default( {(Default > 0.5f ? 1 : 0)} ); >;",

			_ =>
				$"float {Name} < {ui} Default( {F( Default )} ); Range( {F( Min )}, {F( Max )} ); >;",
		};
	}

	/// <summary>HLSL float literals always need a decimal point, or `Default( 1 )` on a float
	/// reads as an int and some drivers complain.</summary>
	private static string F( float v ) => v.ToString( "0.0###", System.Globalization.CultureInfo.InvariantCulture );
}

/// <summary>
/// One self-contained visual effect: the keywords that select it, the parameters it exposes, and
/// the HLSL it contributes to each stage of the generated shader.
///
/// Code goes into one of five slots, which is what lets unrelated blocks combine without knowing
/// about each other. The slots run in this order inside the generated shader:
///
///   Common   helper functions, emitted once at file scope (both stages can see them)
///   Vertex   mutates `vPositionOs` before ProcessVertex — displacement
///   Uv       mutates `i.vTextureCoords` before Material::From — texture-space warps
///   Surface  mutates `m` after Material::From — albedo, emission, opacity, roughness
///   Post     mutates `result` after shading — anything that has to see the lit colour
/// </summary>
public sealed class ShaderBlock
{
	/// <summary>Stable identifier, used in save files and test assertions.</summary>
	public string Id;

	public string Title;

	/// <summary>One line shown in the panel next to the matched block.</summary>
	public string Summary;

	/// <summary>Words and phrases that select this block. Multi-word phrases are matched as
	/// phrases and score higher than single words — see KeywordMatcher.</summary>
	public string[] Keywords = Array.Empty<string>();

	/// <summary>
	/// Blocks sharing a group are mutually exclusive; the best-scoring one wins and the rest are
	/// reported as rejected rather than silently dropped.
	///
	/// "opacity" — blocks that own how the surface fades (cutout vs translucent) and cannot both
	/// write m.Opacity meaningfully. "lighting" — blocks that own the surface's light response.
	/// </summary>
	public string[] ExclusiveGroups = Array.Empty<string>();

	/// <summary>Tie-break when two blocks score identically. Higher wins.</summary>
	public int Priority;

	public ShaderParam[] Params = Array.Empty<ShaderParam>();

	/// <summary>Pulls in the value-noise helpers.</summary>
	public bool NeedsNoise;

	/// <summary>Pulls in SFViewDir, which needs PixelInput and so lives in the PS block.</summary>
	public bool NeedsViewDir;

	/// <summary>Forces the shader translucent.</summary>
	public bool NeedsTranslucent;

	/// <summary>Marks the block that supplies SFPulse's body. SFPulse is always emitted — it
	/// returns 1.0 when no time-modulation block was selected — so any block can multiply by it
	/// unconditionally and "glowing edges that pulse" works without the two blocks knowing about
	/// each other.</summary>
	public bool ProvidesPulse;

	public string CommonCode;
	public string VertexCode;
	public string UvCode;
	public string SurfaceCode;
	public string PostCode;

	/// <summary>Every parameter declaration for this block, numbered within its UiGroup.</summary>
	public IEnumerable<string> Declarations()
	{
		for ( var i = 0; i < Params.Length; i++ )
		{
			Params[i].Group ??= Title;
			yield return Params[i].Declare( i + 1 );
		}
	}
}
