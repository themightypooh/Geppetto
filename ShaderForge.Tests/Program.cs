using Marionette.ShaderForge;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.ShaderForge.Tests;

/// <summary>
/// Verification for the generator. Console runner rather than a test framework, matching
/// Effigy.Tests, so it stays dependency-free and runnable from anywhere.
///
/// This exists because a keyword generator's failure mode is not crashing — it is producing
/// something plausible. A shader that omits the block you asked for, or silently drops half a
/// description, still compiles and still renders. Eyeballing the preview will not catch that.
/// Asserting on which blocks a description selects, and on the structure of the emitted file,
/// will.
/// </summary>
public static class Program
{
	public static int Main( string[] args )
	{
		var outDir = args.Length > 0 ? args[0] : "out";

		Section( "every block is well formed" );
		TestLibraryIntegrity();

		Section( "descriptions select the blocks a person would expect" );
		TestKeywordMatching();

		Section( "phrases beat the single words inside them" );
		TestPhrasePriority();

		Section( "unmatched descriptions say so rather than inventing" );
		TestNoMatch();

		Section( "conflicting blocks resolve to one, and report the other" );
		TestConflicts();

		Section( "emitted shaders have the required structure" );
		TestShaderStructure();

		Section( "block code only reaches the stage it belongs to" );
		TestStagePlacement();

		Section( "generation is deterministic" );
		TestDeterminism();

		Section( "parameter declarations are valid HLSL" );
		TestParamDeclarations();

		Section( "filenames are safe" );
		TestFileNames();

		WriteSamples( outDir );

		Console.WriteLine();
		Console.WriteLine( $"{Report.Passed} passed, {Report.Failed} failed" );

		return Report.Failed == 0 ? 0 : 1;
	}

	// --- library ----------------------------------------------------------------------------

	private static void TestLibraryIntegrity()
	{
		var blocks = BlockLibrary.All;

		Check( "the locked v1 set is 18 blocks", blocks.Count == 18, $"{blocks.Count}" );

		Check( "ids are unique",
			blocks.Select( b => b.Id ).Distinct().Count() == blocks.Count );

		Check( "every block has an id, title and summary",
			blocks.All( b => !string.IsNullOrWhiteSpace( b.Id )
				&& !string.IsNullOrWhiteSpace( b.Title )
				&& !string.IsNullOrWhiteSpace( b.Summary ) ) );

		Check( "every block has at least one keyword",
			blocks.All( b => b.Keywords.Length > 0 ) );

		// A keyword owned by two blocks makes selection arbitrary — whichever the ranking happens
		// to favour wins, and the same description can pick differently as the library grows.
		var duplicated = blocks
			.SelectMany( b => b.Keywords.Select( k => (Keyword: k, b.Id) ) )
			.GroupBy( x => x.Keyword )
			.Where( g => g.Count() > 1 )
			.ToList();

		Check( "no keyword is claimed by two blocks", duplicated.Count == 0,
			string.Join( "; ", duplicated.Select( g => $"{g.Key} -> {string.Join( ",", g.Select( x => x.Id ) )}" ) ) );

		Check( "parameter names are unique across the whole library",
			blocks.SelectMany( b => b.Params ).Select( p => p.Name ).Distinct().Count()
				== blocks.SelectMany( b => b.Params ).Count() );

		Check( "every block contributes code or supplies the pulse",
			blocks.All( b => b.ProvidesPulse
				|| !string.IsNullOrWhiteSpace( b.VertexCode )
				|| !string.IsNullOrWhiteSpace( b.UvCode )
				|| !string.IsNullOrWhiteSpace( b.SurfaceCode )
				|| !string.IsNullOrWhiteSpace( b.PostCode ) ) );

		Check( "exactly one block supplies the pulse",
			blocks.Count( b => b.ProvidesPulse ) == 1 );

		Check( "float params have a range that contains their default",
			blocks.SelectMany( b => b.Params )
				.Where( p => p.Kind == ShaderParamKind.Float )
				.All( p => p.Default >= p.Min && p.Default <= p.Max ) );

		Check( "colour params carry three components",
			blocks.SelectMany( b => b.Params )
				.Where( p => p.Kind == ShaderParamKind.Color )
				.All( p => p.DefaultColor is { Length: 3 } ) );
	}

	// --- matching ---------------------------------------------------------------------------

	private static void TestKeywordMatching()
	{
		// The three examples from the tool's own placeholder text. If these ever stop working the
		// first thing anyone types at the tool fails.
		ExpectBlocks( "glowing neon edges that pulse", "emissive", "pulse" );
		ExpectBlocks( "dissolving with fire", "dissolve" );
		ExpectBlocks( "frosted glass with refraction", "glass" );

		ExpectBlocks( "cel shaded cartoon look", "toon" );
		ExpectBlocks( "water with waves", "water" );
		ExpectBlocks( "grass blowing in the wind", "wind" );
		ExpectBlocks( "flash red when damaged", "hitflash" );
		ExpectBlocks( "holographic projection", "hologram" );
		ExpectBlocks( "snow on top of everything", "snow" );
		ExpectBlocks( "heat haze shimmer", "distort" );
		ExpectBlocks( "legendary loot glow", "loot", "emissive" );
		ExpectBlocks( "tint it by team", "team", "tint" );
	}

	private static void TestPhrasePriority()
	{
		// "rim light" must select Rim Light, not the Emissive block that also owns "light up".
		var result = ShaderForgeGenerator.Generate( "a blue rim light" );

		Check( "'rim light' selects the rim block",
			result.Blocks.Any( b => b.Block.Id == "rim" ) );

		var rim = result.Blocks.First( b => b.Block.Id == "rim" );

		Check( "the two-word phrase scores above a single word", rim.Score >= 2, $"{rim.Score}" );
	}

	private static void TestNoMatch()
	{
		var result = ShaderForgeGenerator.Generate( "something nobody has built yet" );

		Check( "an unmatched description does not claim success", !result.Success );
		Check( "it emits no shader", string.IsNullOrEmpty( result.ShaderSource ) );
		Check( "it says no block for that yet",
			result.Warnings.Any( w => w.Contains( "No block", StringComparison.OrdinalIgnoreCase ) ) );

		var empty = ShaderForgeGenerator.Generate( "" );
		Check( "an empty description is handled", !empty.Success );

		var stopWordsOnly = ShaderForgeGenerator.Generate( "make the thing look like a shader" );
		Check( "a description of nothing but filler does not generate", !stopWordsOnly.Success );

		// The living-library promise: a miss has to be reported by name so it can become the next
		// block. Reporting nothing is as bad as generating nothing.
		Check( "unmatched terms are named",
			result.UnmatchedTerms.Count > 0,
			string.Join( ",", result.UnmatchedTerms ) );

		Check( "filler words are not reported as missing blocks",
			!result.UnmatchedTerms.Contains( "the" ) && !result.UnmatchedTerms.Contains( "a" ) );
	}

	private static void TestConflicts()
	{
		// Glass is translucent, Dissolve is a cutout. One surface cannot be both.
		var result = ShaderForgeGenerator.Generate( "dissolving glass" );

		Check( "only one opacity block survives",
			result.Blocks.Count( b => b.Block.ExclusiveGroups.Contains( "opacity" ) ) == 1 );

		Check( "the loser is reported, not dropped silently", result.Rejected.Count >= 1 );

		Check( "the conflict produces a warning naming both",
			result.Warnings.Any( w => w.Contains( "cannot combine" ) ) );

		Check( "the shader is still generated from what did fit", result.Success );

		// Glass also owns the lighting response, so toon cannot ride along with it.
		var lighting = ShaderForgeGenerator.Generate( "cartoon glass" );

		Check( "only one lighting block survives",
			lighting.Blocks.Count( b => b.Block.ExclusiveGroups.Contains( "lighting" ) ) == 1 );
	}

	// --- emission ---------------------------------------------------------------------------

	private static void TestShaderStructure()
	{
		var result = ShaderForgeGenerator.Generate( "glowing neon that pulses" );
		var source = result.ShaderSource;

		foreach ( var required in new[] { "HEADER", "FEATURES", "MODES", "COMMON", "VS", "PS" } )
			Check( $"emits a {required} block", source.Contains( required ) );

		Check( "declares MainVs", source.Contains( "PixelInput MainVs( VertexInput i )" ) );
		Check( "declares MainPs", source.Contains( "float4 MainPs( PixelInput i ) : SV_Target0" ) );
		Check( "builds a Material", source.Contains( "Material m = Material::From( i );" ) );
		Check( "shades it", source.Contains( "ShadingModelStandard::Shade( i, m )" ) );
		Check( "returns a result", source.Contains( "return result;" ) );

		Check( "braces balance", source.Count( c => c == '{' ) == source.Count( c => c == '}' ),
			$"{source.Count( c => c == '{' )} vs {source.Count( c => c == '}' )}" );

		Check( "parens balance", source.Count( c => c == '(' ) == source.Count( c => c == ')' ) );

		// SFPulse is emitted unconditionally so any block can multiply by it — the mechanism that
		// makes "glowing edges that pulse" work without the two blocks knowing about each other.
		Check( "SFPulse is always defined", source.Contains( "float SFPulse()" ) );

		var noPulse = ShaderForgeGenerator.Generate( "just a glow" );
		Check( "SFPulse still defined without a pulse block",
			noPulse.ShaderSource.Contains( "float SFPulse()" ) );
		Check( "and returns 1.0 when nothing modulates it",
			noPulse.ShaderSource.Contains( "return 1.0;" ) );

		// Translucency has to be defined BEFORE shared.hlsl is included or it selects nothing.
		var glass = ShaderForgeGenerator.Generate( "glass" ).ShaderSource;
		Check( "translucent shaders define S_TRANSLUCENT", glass.Contains( "#define S_TRANSLUCENT 1" ) );
		Check( "and define it before including shared.hlsl",
			glass.IndexOf( "#define S_TRANSLUCENT", StringComparison.Ordinal )
				< glass.IndexOf( "common/shared.hlsl", StringComparison.Ordinal ) );

		Check( "opaque shaders do not define it",
			!ShaderForgeGenerator.Generate( "a glow" ).ShaderSource.Contains( "S_TRANSLUCENT" ) );

		// Noise helpers cost instructions; only pull them in when something samples noise.
		Check( "noise helpers appear when a block needs them",
			ShaderForgeGenerator.Generate( "dissolve" ).ShaderSource.Contains( "float SFNoise(" ) );

		Check( "and stay out when nothing does",
			!ShaderForgeGenerator.Generate( "team colour" ).ShaderSource.Contains( "float SFNoise(" ) );

		Check( "view-dir helper appears when a block needs it",
			ShaderForgeGenerator.Generate( "rim light" ).ShaderSource.Contains( "float3 SFViewDir(" ) );

		Check( "and stays out when nothing does",
			!ShaderForgeGenerator.Generate( "team colour" ).ShaderSource.Contains( "float3 SFViewDir(" ) );
	}

	private static void TestStagePlacement()
	{
		// Wind is a vertex block. Its displacement must land in VS and nowhere else — a vertex
		// snippet that leaks into PS references vPositionOs, which does not exist there, and the
		// shader fails to compile.
		//
		// Line endings are normalised first: the emitter uses Environment.NewLine, so searching for
		// "\nVS\n" finds nothing on Windows and every check below would pass vacuously.
		var source = Normalise( ShaderForgeGenerator.Generate( "grass in the wind with a glow" ).ShaderSource );

		var vsStart = source.IndexOf( "\nVS\n", StringComparison.Ordinal );
		var psStart = source.IndexOf( "\nPS\n", StringComparison.Ordinal );

		Check( "VS comes before PS", vsStart >= 0 && psStart > vsStart, $"vs {vsStart} ps {psStart}" );

		var vs = source.Substring( vsStart, psStart - vsStart );
		var ps = source.Substring( psStart );

		Check( "wind displacement is in the vertex stage", vs.Contains( "g_flSfWindStrength" ) );
		Check( "and not in the pixel stage", !ps.Contains( "g_flSfWindStrength" ) );

		Check( "emission is in the pixel stage", ps.Contains( "m.Emission" ) );
		Check( "and not in the vertex stage", !vs.Contains( "m.Emission" ) );

		Check( "vPositionOs is declared before it is written",
			vs.IndexOf( "float3 vPositionOs = i.vPositionOs;", StringComparison.Ordinal )
				< vs.IndexOf( "vPositionOs.xy +=", StringComparison.Ordinal ) );

		Check( "the displaced position is fed back before ProcessVertex",
			vs.IndexOf( "i.vPositionOs = vPositionOs;", StringComparison.Ordinal )
				< vs.IndexOf( "ProcessVertex( i )", StringComparison.Ordinal ) );

		// A uv warp has to happen before the material samples its textures, or it warps nothing.
		var warp = Normalise( ShaderForgeGenerator.Generate( "heat haze" ).ShaderSource );

		Check( "uv warps precede Material::From",
			warp.IndexOf( "i.vTextureCoords.xy +=", StringComparison.Ordinal )
				< warp.IndexOf( "Material m = Material::From", StringComparison.Ordinal ) );

		// Toon bands the lit result, so it must run after shading, not before.
		var toon = Normalise( ShaderForgeGenerator.Generate( "cel shaded" ).ShaderSource );

		Check( "post code runs after shading",
			toon.IndexOf( "ShadingModelStandard::Shade", StringComparison.Ordinal )
				< toon.IndexOf( "sfToonLum", StringComparison.Ordinal ) );
	}

	private static void TestDeterminism()
	{
		var first = ShaderForgeGenerator.Generate( "glowing dissolving snowy thing" ).ShaderSource;
		var second = ShaderForgeGenerator.Generate( "glowing dissolving snowy thing" ).ShaderSource;

		Check( "the same description generates the same file", first == second );

		// Word order must not change the output, or regenerating after an edit produces a
		// meaningless diff.
		var reordered = ShaderForgeGenerator.Generate( "snowy dissolving glowing thing" ).ShaderSource;

		Check( "word order does not change the file", first == reordered );
	}

	private static void TestParamDeclarations()
	{
		foreach ( var block in BlockLibrary.All )
		{
			foreach ( var declaration in block.Declarations() )
			{
				var ok = declaration.EndsWith( ">;" )
					&& declaration.Contains( "UiGroup(" )
					&& declaration.Count( c => c == '<' ) == 1
					&& declaration.Count( c => c == '>' ) == 1
					&& declaration.Count( c => c == '"' ) % 2 == 0;

				Check( $"{block.Id}: {declaration.Split( ' ' )[1]} declares cleanly", ok, declaration );
			}
		}

		// Floats need a decimal point in HLSL - Default( 1 ) on a float is an int literal.
		var emissive = BlockLibrary.ById( "emissive" );
		var strength = emissive.Declarations().First( d => d.Contains( "g_flSfEmissiveStrength" ) );

		Check( "float defaults carry a decimal point", strength.Contains( "2.0" ), strength );
	}

	private static void TestFileNames()
	{
		Check( "spaces become underscores", ShaderTemplate.SafeFileName( "my shader" ) == "my_shader" );
		Check( "path separators cannot escape", !ShaderTemplate.SafeFileName( "../../etc/passwd" ).Contains( "/" ) );
		Check( "an empty name falls back", ShaderTemplate.SafeFileName( "" ) == "shader_forge_output" );
		Check( "a name of pure punctuation falls back", ShaderTemplate.SafeFileName( "!!!" ) == "shader_forge_output" );
		Check( "case is normalised", ShaderTemplate.SafeFileName( "MyShader" ) == "myshader" );
	}

	// --- samples ----------------------------------------------------------------------------

	/// <summary>
	/// Writes one .shader per sample description. These are the fastest way to see whether the
	/// output is actually right — drop one into the project and let the compiler judge it, which
	/// is the one check this runner cannot do for itself.
	/// </summary>
	private static void WriteSamples( string outDir )
	{
		Directory.CreateDirectory( outDir );

		var samples = new Dictionary<string, string>
		{
			["glow_pulse"] = "glowing neon edges that pulse",
			["dissolve_fire"] = "dissolving with fire",
			["glass"] = "frosted glass",
			["toon"] = "cel shaded cartoon",
			["water"] = "ocean water with waves",
			["wind"] = "grass blowing in the wind",
			["hologram"] = "glitchy holographic projection",
			["loot"] = "legendary loot glow that pulses",
			["snow_rim"] = "snow covered with a blue rim light",
			["everything"] = "glowing pulsing snowy windy team coloured interactable loot",
		};

		foreach ( var (name, description) in samples )
		{
			var result = ShaderForgeGenerator.Generate( description );

			if ( !result.Success )
			{
				Check( $"sample '{name}' generates", false, description );
				continue;
			}

			File.WriteAllText( Path.Combine( outDir, $"{name}{ShaderTemplate.Extension}" ), result.ShaderSource );
		}

		Console.WriteLine();
		Console.WriteLine( $"wrote {samples.Count} sample shaders to {outDir}/" );
	}

	// --- plumbing ---------------------------------------------------------------------------

	private static void ExpectBlocks( string description, params string[] expectedIds )
	{
		var result = ShaderForgeGenerator.Generate( description );
		var got = result.Blocks.Select( b => b.Block.Id ).ToList();

		foreach ( var id in expectedIds )
			Check( $"\"{description}\" selects {id}", got.Contains( id ), $"got: {string.Join( ",", got )}" );
	}

	/// <summary>Collapse CRLF so position assertions mean the same thing on every platform.</summary>
	private static string Normalise( string source ) => source.Replace( "\r\n", "\n" );

	private static void Section( string title ) => Report.Section( title );

	private static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
