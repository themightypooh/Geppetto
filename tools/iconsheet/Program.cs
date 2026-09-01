// Writes the contact sheet.
//
// The geometry is the editor's own: EffigyToolStrip.ButtonSize is 54 and IconScale is 1.5, so
// every glyph is drawn at the centre of a ButtonSize square at IconScale - the same numbers the strip
// passes. That matters more than it sounds. A glyph authored against a nominal 18x18 box and shown
// at 18px reads fine and says nothing about how it sits in a 54px button, which is the open
// question this sheet exists to answer.
//
// Two colours per glyph rather than one, because the strip's colour comes from Theme.Text and the
// editor has both palettes. A stroke weight that reads on dark can close up on light.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using Editor;
using Marionette.EditorTools;
using Sandbox;

namespace Marionette.IconSheet;

internal static class Program
{
	// --- the strip's own numbers, read rather than copied -------------------------------------
	//
	// These two decide everything the sheet is for: how big the button is and how far up the glyph
	// is scaled inside it. Copying them here would make the sheet answer a question about a strip
	// that does not exist the moment somebody edits EffigyWindow - which is precisely the failure
	// this sheet was built to catch, so it must not be the failure the sheet itself has. They are
	// parsed out of EffigyToolStrip instead, the same way the glyph source is linked rather than
	// copied. If the parse ever fails the run stops; a sheet drawn at a guessed scale is worse
	// than no sheet, because it looks like evidence.

	private static readonly float ButtonSize = StripConstant( "ButtonSize" );
	private static readonly float IconScale = StripConstant( "IconScale" );

	/// <summary>Reads `public const float NAME = VALUE;` out of EffigyWindow.cs.</summary>
	private static float StripConstant( string name )
	{
		var source = FindEditorSource();
		var text = File.ReadAllText( source );
		var match = Regex.Match( text, @"public const float " + Regex.Escape( name ) + @"\s*=\s*([0-9.]+)f\s*;" );

		if ( !match.Success )
			throw new InvalidOperationException( $"could not find `public const float {name}` in {source} - the sheet will not guess it" );

		return float.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture );
	}

	/// <summary>Walks up from the working directory for the editor window's source. The tool is
	/// normally run as `dotnet run --project tools/iconsheet` from the repo root, but walking up
	/// means it also works from inside its own directory.</summary>
	private static string FindEditorSource()
	{
		var directory = new DirectoryInfo( Directory.GetCurrentDirectory() );

		while ( directory is not null )
		{
			var candidate = Path.Combine( directory.FullName, "Editor", "EffigyEditor", "EffigyWindow.cs" );

			if ( File.Exists( candidate ) ) return candidate;

			directory = directory.Parent;
		}

		throw new FileNotFoundException( "no Editor/EffigyEditor/EffigyWindow.cs above the working directory - run this from inside the repo" );
	}

	/// <summary>Theme.Text on the dark palette, and on the light one. Read off the editor's own
	/// chrome rather than picked - a glyph is judged against the background it will sit on.</summary>
	private static readonly Color OnDark = new( 0.878f, 0.886f, 0.898f );
	private static readonly Color OnLight = new( 0.169f, 0.184f, 0.200f );

	private const string DarkBackground = "#2b2f33";
	private const string LightBackground = "#eceff1";

	/// <summary>Where each run of the enum starts, so the sheet is grouped the way the strip is
	/// rather than being 47 glyphs in one wall.</summary>
	private static readonly (EffigyIcon First, string Title, string Note)[] Sections =
	{
		(EffigyIcon.Sketch, "Feature strip",
			"The originals. These have been seen in the editor and are here as the reference weight everything else should match."),
		(EffigyIcon.SelectTool, "Sketch tools",
			"Drawn to replace generic Material Icon names. Never rendered until now."),
		(EffigyIcon.Sculpt, "Sculpt strip",
			"Each brush drawn as what it does to a surface line, not as a tool shape. Never rendered until now."),
		(EffigyIcon.Draft, "Face tools",
			"Draft and Hole, which act on a picked face. Never rendered until now."),
		(EffigyIcon.EllipseTool, "The six later sketch tools",
			"Ellipse, spline, trim, extend, fillet, offset. Never rendered until now."),
	};

	private static int Main( string[] args )
	{
		var outDir = args.Length > 0 ? args[0] : "out";
		var svgDir = Path.Combine( outDir, "icons" );

		Directory.CreateDirectory( svgDir );

		var icons = (EffigyIcon[])Enum.GetValues( typeof( EffigyIcon ) );
		var sheet = new StringBuilder();
		var extents = new List<(EffigyIcon Icon, float Width, float Height)>();

		foreach ( var section in Sections )
		{
			sheet.Append( $"<section>\n<h2>{section.Title}</h2>\n<p class=\"note\">{section.Note}</p>\n<div class=\"grid\">\n" );

			foreach ( var icon in Run( icons, section ) )
			{
				var dark = Render( icon, OnDark );
				var extent = Paint.Extent;
				var light = Render( icon, OnLight );

				extents.Add( (icon, extent.Width, extent.Height) );

				File.WriteAllText( Path.Combine( svgDir, $"{icon}.svg" ), Document( dark, DarkBackground ) );

				sheet.Append( $"<figure><div class=\"pair\">" );
				sheet.Append( $"<div class=\"cell dark\">{Inline( dark )}</div>" );
				sheet.Append( $"<div class=\"cell light\">{Inline( light )}</div>" );
				sheet.Append( $"</div><figcaption>{icon}</figcaption></figure>\n" );
			}

			sheet.Append( "</div>\n</section>\n" );
		}

		var html = Template.Replace( "{{BODY}}", sheet.ToString() )
			.Replace( "{{COUNT}}", icons.Length.ToString( CultureInfo.InvariantCulture ) )
			.Replace( "{{BUTTON}}", ButtonSize.ToString( "0.#", CultureInfo.InvariantCulture ) )
			.Replace( "{{SCALE}}", IconScale.ToString( "0.##", CultureInfo.InvariantCulture ) );

		var path = Path.Combine( outDir, "icon-sheet.html" );
		File.WriteAllText( path, html );

		Console.WriteLine( $"wrote {icons.Length} glyphs to {svgDir}/ and the sheet to {path}" );
		Console.WriteLine();
		ReportExtents( extents );

		return 0;
	}

	/// <summary>
	/// How much of its button each glyph's ink actually covers.
	///
	/// The point of printing this rather than leaving it to the eye: every glyph in this file
	/// claims to be authored against a nominal 18x18 box, and the first run of this sheet showed
	/// the largest covering twice as much of its button as the smallest. A strip whose glyphs vary
	/// two-fold in optical size reads as unfinished however good each drawing is, and no amount of
	/// looking at one icon at a time will show it.
	/// </summary>
	private static void ReportExtents( List<(EffigyIcon Icon, float Width, float Height)> extents )
	{
		var sorted = new List<(EffigyIcon Icon, float Width, float Height)>( extents );
		sorted.Sort( ( a, b ) => MathF.Max( b.Width, b.Height ).CompareTo( MathF.Max( a.Width, a.Height ) ) );

		Console.WriteLine( $"  glyph coverage of the {ButtonSize:0}px button, largest dimension first" );
		Console.WriteLine( $"  (authored against a nominal 18x18 box, so at scale {IconScale} they should all land near {18f * IconScale / ButtonSize * 100f:0}%)" );
		Console.WriteLine();

		foreach ( var entry in sorted )
		{
			var largest = MathF.Max( entry.Width, entry.Height );
			var mark = largest > 18f * IconScale + 0.5f ? "  <- outside the box" : "";

			Console.WriteLine( $"    {entry.Icon,-28} {entry.Width,6:0.0} x {entry.Height,-6:0.0} {largest / ButtonSize * 100f,5:0.0}%{mark}" );
		}

		var largestDimensions = new List<float>();

		foreach ( var entry in extents )
			largestDimensions.Add( MathF.Max( entry.Width, entry.Height ) );

		largestDimensions.Sort();

		var median = largestDimensions.Count == 0 ? 0f : largestDimensions[largestDimensions.Count / 2];

		Console.WriteLine();
		Console.WriteLine( $"  median {median:0.0}px, {median / ButtonSize * 100f:0}% of the button" );
	}

	/// <summary>The icons belonging to one section: from its first entry up to the next section's.</summary>
	private static IEnumerable<EffigyIcon> Run( EffigyIcon[] all, (EffigyIcon First, string Title, string Note) section )
	{
		var started = false;

		foreach ( var icon in all )
		{
			if ( icon == section.First ) started = true;
			else if ( started && IsSectionStart( icon ) ) yield break;

			if ( started ) yield return icon;
		}
	}

	private static bool IsSectionStart( EffigyIcon icon )
	{
		foreach ( var section in Sections )
			if ( section.First == icon ) return true;

		return false;
	}

	/// <summary>One glyph's elements, drawn at the strip's own size and scale.</summary>
	private static string Render( EffigyIcon icon, Color color )
	{
		Paint.Begin();
		EffigyIcons.Draw( icon, new Vector2( ButtonSize / 2f, ButtonSize / 2f ), color, IconScale );
		return Paint.End();
	}

	private static string Inline( string elements )
		=> $"<svg viewBox=\"0 0 {ButtonSize} {ButtonSize}\" width=\"{ButtonSize}\" height=\"{ButtonSize}\">{elements}</svg>";

	private static string Document( string elements, string background )
		=> $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {ButtonSize} {ButtonSize}\" width=\"{ButtonSize}\" height=\"{ButtonSize}\">"
			+ $"<rect width=\"{ButtonSize}\" height=\"{ButtonSize}\" fill=\"{background}\" />{elements}</svg>";

	private const string Template = """
<!doctype html>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Effigy icon sheet</title>
<style>
  :root { color-scheme: light dark; --ink: #16181a; --paper: #f7f8f9; --muted: #5b6167; --line: #d8dbde; }
  @media (prefers-color-scheme: dark) {
    :root { --ink: #e6e8ea; --paper: #17191b; --muted: #9aa1a8; --line: #2e3236; }
  }
  body { margin: 0; padding: 24px; background: var(--paper); color: var(--ink);
         font: 14px/1.5 ui-sans-serif, system-ui, -apple-system, sans-serif; }
  h1 { font-size: 20px; margin: 0 0 4px; }
  h2 { font-size: 15px; margin: 32px 0 4px; }
  .lede, .note { color: var(--muted); margin: 0 0 12px; max-width: 62ch; }
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); gap: 14px; }
  figure { margin: 0; }
  .pair { display: flex; gap: 6px; }
  .cell { width: 54px; height: 54px; border-radius: 6px; border: 1px solid var(--line); flex: none; }
  .cell.dark { background: #2b2f33; }
  .cell.light { background: #eceff1; }
  figcaption { font-size: 11px; color: var(--muted); margin-top: 6px; word-break: break-word; }
</style>
<h1>Effigy icon sheet — {{COUNT}} glyphs</h1>
<p class="lede">Every hand-drawn <code>EffigyIcon</code>, rendered from the editor's own source at
the strip's real geometry: a {{BUTTON}}&times;{{BUTTON}} button with the glyph at scale {{SCALE}}. Left swatch is the
dark palette, right is light. Generated by <code>tools/iconsheet</code>; no s&amp;box involved.</p>
{{BODY}}
""";
}
