using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Marionette.ShaderForge;

/// <summary>One block the description selected, and how strongly.</summary>
public sealed class BlockMatch
{
	public ShaderBlock Block;

	/// <summary>Sum of the matched phrases' word counts. A two-word phrase outscores a single
	/// word, so "rim light" beats a stray "light".</summary>
	public int Score;

	/// <summary>Which of the block's keywords actually appeared, for the panel to show.</summary>
	public List<string> MatchedKeywords = new();
}

/// <summary>A block that matched but was dropped, and why. Never silent.</summary>
public sealed class BlockRejection
{
	public ShaderBlock Block;
	public string Reason;
}

/// <summary>Everything one Generate press produced.</summary>
public sealed class GenerationResult
{
	/// <summary>False when nothing matched — the shader source is then empty and the panel should
	/// say "no block for that yet" rather than showing a shader.</summary>
	public bool Success;

	public string ShaderSource = "";

	/// <summary>Blocks that made it into the shader, in emission order.</summary>
	public List<BlockMatch> Blocks = new();

	/// <summary>Blocks that matched but lost a conflict.</summary>
	public List<BlockRejection> Rejected = new();

	/// <summary>Words from the description that meant nothing to the library. These are the
	/// roadmap for the next block — see BlockLibrary's note on the living library.</summary>
	public List<string> UnmatchedTerms = new();

	public List<string> Warnings = new();

	/// <summary>A filename derived from the matched blocks, e.g. "glow_pulse".</summary>
	public string SuggestedName = "shader_forge_output";

	/// <summary>Every parameter the generated shader exposes, in declaration order. The tweak
	/// panel builds its controls straight off this rather than reading the compiled schema back,
	/// so tweaking works even where the reflection-based schema read does not.</summary>
	public List<ShaderParam> Params = new();
}

/// <summary>
/// Turns a plain-English description into a compiling .shad file.
///
/// Pipeline, in order: tokenise → match keywords to blocks → resolve conflicts → compose the
/// selected snippets → fill the file template.
///
/// Engine-free on purpose. It references no Sandbox or Editor type, so the whole generator can be
/// exercised from a console runner (see ShaderForge.Tests) without an editor open — the same
/// arrangement the Effigy kernel uses, and for the same reason: this is the half where being
/// subtly wrong produces plausible-looking output.
/// </summary>
public static class ShaderForgeGenerator
{
	/// <summary>Words carrying no effect meaning. Without this every description reports half its
	/// own grammar as "no block for that yet", which trains people to ignore the message.</summary>
	private static readonly HashSet<string> StopWords = new( StringComparer.OrdinalIgnoreCase )
	{
		"a", "an", "and", "the", "with", "that", "this", "it", "its", "is", "are", "be", "of", "on",
		"in", "at", "to", "for", "from", "by", "as", "but", "or", "so", "some", "very", "really",
		"make", "makes", "made", "want", "like", "looks", "look", "looking", "feel", "feels",
		"should", "would", "could", "can", "please", "kind", "sort", "bit", "little", "more",
		"less", "my", "i", "me", "you", "we", "shader", "material", "effect", "effects", "surface",
		"thing", "things", "object", "objects", "model", "models", "mesh", "them", "then", "when",
		"where", "which", "while", "all", "any", "over", "up", "down", "out", "into", "not",
	};

	public static GenerationResult Generate( string description )
	{
		var result = new GenerationResult();

		var words = Tokenise( description );

		if ( words.Count == 0 )
		{
			result.Warnings.Add( "Describe the effect you want — the box is empty." );
			return result;
		}

		var matches = Match( words, out var consumed );

		if ( matches.Count == 0 )
		{
			result.UnmatchedTerms = words.Where( w => !StopWords.Contains( w ) ).Distinct().ToList();
			result.Warnings.Add( "No block for that yet." );
			return result;
		}

		var accepted = ResolveConflicts( matches, result );

		// Emit in library order, not match order, so the same description always produces a
		// byte-identical file and a diff between two generations means something.
		var libraryOrder = BlockLibrary.All
			.Select( ( block, index ) => (block, index) )
			.ToDictionary( x => x.block, x => x.index );

		accepted = accepted
			.OrderBy( m => libraryOrder[m.Block] )
			.ToList();

		result.Blocks = accepted;
		result.UnmatchedTerms = words
			.Where( w => !StopWords.Contains( w ) && !consumed.Contains( w ) )
			.Distinct()
			.ToList();

		result.SuggestedName = string.Join( "_", accepted.Select( m => m.Block.Id ) );
		result.Params = accepted.SelectMany( m => m.Block.Params ).ToList();
		result.ShaderSource = ShaderTemplate.Build( accepted.Select( m => m.Block ).ToList(), description );
		result.Success = true;

		if ( result.UnmatchedTerms.Count > 0 )
		{
			result.Warnings.Add(
				$"No block yet for: {string.Join( ", ", result.UnmatchedTerms )}. " +
				"The rest was generated." );
		}

		return result;
	}

	// --- 1. parse ---------------------------------------------------------------------------

	/// <summary>Lowercase, strip punctuation, split. Punctuation becomes a break rather than
	/// vanishing, so "glowing, dissolving" does not become one token.</summary>
	public static List<string> Tokenise( string description )
	{
		if ( string.IsNullOrWhiteSpace( description ) )
			return new List<string>();

		var builder = new StringBuilder( description.Length );

		foreach ( var c in description.ToLowerInvariant() )
			builder.Append( char.IsLetterOrDigit( c ) ? c : ' ' );

		return builder.ToString()
			.Split( ' ', StringSplitOptions.RemoveEmptyEntries )
			.ToList();
	}

	// --- 2. match ---------------------------------------------------------------------------

	/// <summary>
	/// Score every block against the tokenised description.
	///
	/// Phrases are matched as consecutive runs of words, so a block keyed on "rim light" only
	/// fires on those two words together — and scores 2, beating the Emissive block's single
	/// "light" so the description resolves the way a person would read it.
	/// </summary>
	private static List<BlockMatch> Match( List<string> words, out HashSet<string> consumed )
	{
		consumed = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		var matches = new List<BlockMatch>();

		foreach ( var block in BlockLibrary.All )
		{
			var match = new BlockMatch { Block = block };

			foreach ( var keyword in block.Keywords )
			{
				var phrase = keyword.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
				var hit = FindPhrase( words, phrase );

				if ( hit is null )
					continue;

				match.Score += phrase.Length;
				match.MatchedKeywords.Add( keyword );

				// The words the DESCRIPTION used, not the keyword - "pulses" matched "pulse", and
				// consuming "pulse" would leave "pulses" looking unmatched and get it reported as
				// a missing block.
				foreach ( var word in hit )
					consumed.Add( word );
			}

			if ( match.Score > 0 )
				matches.Add( match );
		}

		return matches
			.OrderByDescending( m => m.Score )
			.ThenByDescending( m => m.Block.Priority )
			.ToList();
	}

	/// <summary>Find a phrase as a consecutive run of words. Returns the words the description
	/// actually used, so they can be marked consumed, or null if the phrase is not present.</summary>
	private static List<string> FindPhrase( List<string> words, string[] phrase )
	{
		if ( phrase.Length == 0 )
			return null;

		for ( var start = 0; start + phrase.Length <= words.Count; start++ )
		{
			var hit = true;

			for ( var offset = 0; offset < phrase.Length; offset++ )
			{
				if ( WordMatches( words[start + offset], phrase[offset] ) )
					continue;

				hit = false;
				break;
			}

			if ( hit )
				return words.GetRange( start, phrase.Length );
		}

		return null;
	}

	/// <summary>
	/// One word against one keyword, tolerating the endings English puts on the same idea.
	///
	/// Without this the library would need every inflection spelled out — "pulse", "pulses",
	/// "pulsing", "pulsed" — and would still miss the one nobody thought of. Since this is the
	/// difference between "glowing edges that pulse" working and reporting "no block for: pulses",
	/// it is worth the small risk of an over-eager match.
	///
	/// Deliberately crude: it only ever strips from the description's word toward the keyword, so
	/// it can widen what matches an existing keyword but can never invent a new one.
	/// </summary>
	private static bool WordMatches( string word, string keyword )
	{
		if ( string.Equals( word, keyword, StringComparison.OrdinalIgnoreCase ) )
			return true;

		if ( word.Length <= keyword.Length || !word.StartsWith( keyword, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var suffix = word.Substring( keyword.Length ).ToLowerInvariant();

		return suffix is "s" or "es" or "d" or "ed" or "ing" or "y";
	}

	// --- 3. resolve conflicts ----------------------------------------------------------------

	/// <summary>
	/// Two blocks claiming the same exclusive group cannot both be right — "frosted glass that
	/// dissolves" wants one surface to be translucent and cutout at once. The higher-scoring one
	/// wins, and the loser is REPORTED rather than dropped quietly, because silently generating
	/// two thirds of what was asked for is the failure mode that makes a tool untrustworthy.
	/// </summary>
	private static List<BlockMatch> ResolveConflicts( List<BlockMatch> ranked, GenerationResult result )
	{
		var accepted = new List<BlockMatch>();
		var claimed = new Dictionary<string, ShaderBlock>();

		foreach ( var candidate in ranked )
		{
			var loser = false;

			foreach ( var group in candidate.Block.ExclusiveGroups )
			{
				if ( !claimed.TryGetValue( group, out var winner ) )
					continue;

				result.Rejected.Add( new BlockRejection
				{
					Block = candidate.Block,
					Reason = $"conflicts with {winner.Title} over {group}",
				} );

				result.Warnings.Add(
					$"{candidate.Block.Title} and {winner.Title} cannot combine — both control the " +
					$"surface's {group}. Kept {winner.Title}." );

				loser = true;
				break;
			}

			if ( loser )
				continue;

			foreach ( var group in candidate.Block.ExclusiveGroups )
				claimed[group] = candidate.Block;

			accepted.Add( candidate );
		}

		return accepted;
	}
}
