using System;
using System.Globalization;
using System.IO;
using System.Text;
using Marionette.EditorTools;
using Sandbox;

/// <summary>Writes each primitive as SVG. Icons are approximated with text glyphs — the editor
/// draws Material icons, which an SVG renderer has no font for.</summary>
sealed class SvgCanvas : IOverlayCanvas
{
	readonly StringBuilder _sb = new();
	static string F( float v ) => v.ToString( "0.##", CultureInfo.InvariantCulture );
	static string C( Color c ) => $"rgb({(int)(c.r * 255)},{(int)(c.g * 255)},{(int)(c.b * 255)})\" fill-opacity=\"{F( c.a )}";
	static string S( Color c ) => $"rgb({(int)(c.r * 255)},{(int)(c.g * 255)},{(int)(c.b * 255)})\" stroke-opacity=\"{F( c.a )}";

	public void Circle( Vector2 c, float radius, Color fill, Color border = default, float borderSize = 0f )
	{
		_sb.Append( $"<circle cx=\"{F( c.x )}\" cy=\"{F( c.y )}\" r=\"{F( radius )}\" fill=\"{C( fill )}\"" );
		if ( borderSize > 0f ) _sb.Append( $" stroke=\"{S( border )}\" stroke-width=\"{F( borderSize )}\"" );
		_sb.AppendLine( "/>" );
	}

	public void RoundedRect( Rect r, float radius, Color fill, Color border = default, float borderSize = 0f )
	{
		_sb.Append( $"<rect x=\"{F( r.Left )}\" y=\"{F( r.Top )}\" width=\"{F( r.Width )}\" height=\"{F( r.Height )}\" rx=\"{F( radius )}\" fill=\"{C( fill )}\"" );
		if ( borderSize > 0f ) _sb.Append( $" stroke=\"{S( border )}\" stroke-width=\"{F( borderSize )}\"" );
		_sb.AppendLine( "/>" );
	}

	public void Line( Vector2 a, Vector2 b, float t, Color color ) =>
		_sb.AppendLine( $"<line x1=\"{F( a.x )}\" y1=\"{F( a.y )}\" x2=\"{F( b.x )}\" y2=\"{F( b.y )}\" stroke=\"{S( color )}\" stroke-width=\"{F( t )}\"/>" );

	public void Text( string text, Vector2 p, float size, Color color ) =>
		_sb.AppendLine( $"<text x=\"{F( p.x )}\" y=\"{F( p.y )}\" font-family=\"Roboto,DejaVu Sans,sans-serif\" font-size=\"{F( size )}\" text-anchor=\"middle\" dominant-baseline=\"central\" fill=\"{C( color )}\">{text}</text>" );

	public void Polygon( Vector2[] points, Color color )
	{
		_sb.Append( "<polygon points=\"" );
		foreach ( var p in points ) _sb.Append( $"{F( p.x )},{F( p.y )} " );
		// Stroked in its own colour: an SVG renderer antialiases each edge, so neighbouring pieces
		// of one wedge would otherwise show hairline seams the editor's triangles do not have.
		_sb.AppendLine( $"\" fill=\"{C( color )}\" stroke=\"{S( color )}\" stroke-width=\"0.8\"/>" );
	}

	public void Icon( string icon, Rect rect, float size, Color color )
	{
		// Stand-ins for the Material icons the editor draws — close enough to judge a layout by.
		var glyph = icon switch
		{
			"chevron_left" => "‹", "chevron_right" => "›", "expand_less" => "˄", "expand_more" => "˅",
			"rotate_left" => "↺", "rotate_right" => "↻", "home" => "⌂", "close" => "✕", "check" => "✓",
			"edit" or "draw" => "✎", "open_in_full" => "⤢", "open_with" => "✥", "radio_button_unchecked" => "○",
			"more_horiz" => "⋯", "palette" => "◐", "flip" => "⇆", "change_history" => "△",
			"check_box_outline_blank" => "□", "file_open" => "▤", "fit_screen" => "⛶", "redo" => "↷", "undo" => "↶",
			"content_copy" => "⧉", "content_cut" => "✂", "delete" => "🗑", "visibility_off" => "◌",
			"crop_square" => "▢", "crop_portrait" => "▯", "horizontal_rule" => "—", "architecture" => "⌒",
			"link" => "⛓", "near_me" => "➤", "build" => "⚒",
			_ => "?",
		};
		Text( glyph, rect.Center, size, color );
	}

	public string Svg( int w, int h, string bg, string caption ) =>
		$"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\"><rect width=\"100%\" height=\"100%\" fill=\"{bg}\"/>{_sb}"
		+ $"<text x=\"6\" y=\"{h - 8}\" font-family=\"DejaVu Sans\" font-size=\"11\" fill=\"#8a8f99\">{caption}</text></svg>";
}

static class Program
{
	static void Main( string[] args )
	{
		var outDir = args.Length > 0 ? args[0] : "out";
		Directory.CreateDirectory( outDir );

		var iso = new Vector3( 1f, -1f, 0.65f ).Normal;
		var blue = new Color( 0.24f, 0.55f, 0.95f );

		// (name, camera direction from target, hover piece, hover button, hovered)
		var scenes = new (string Name, Vector3 Dir, Vector3? Hover, ViewCubeButton Button, bool Hovered)[]
		{
			("iso", iso, null, ViewCubeButton.None, false),
			("iso-hover-face", iso, new Vector3( 0, -1, 0 ), ViewCubeButton.None, true),
			("iso-hover-edge", iso, new Vector3( 1, -1, 0 ), ViewCubeButton.None, true),
			("iso-hover-corner", iso, new Vector3( 1, -1, 1 ), ViewCubeButton.None, true),
			("iso-hover-button", iso, null, ViewCubeButton.Right, true),
			("front", new Vector3( 0, -1, 0 ), null, ViewCubeButton.None, false),
			("top", new Vector3( 0, 0, 1 ), null, ViewCubeButton.None, false),
			("back-low", new Vector3( -0.6f, 1f, -0.4f ).Normal, null, ViewCubeButton.None, false),
		};

		foreach ( var (name, dir, hover, button, hovered) in scenes )
		{
			var up = MathF.Abs( dir.z ) > 0.99f ? Vector3.Left : Vector3.Up;
			var camera = Rotation.LookAt( -dir, up );
			var canvas = new SvgCanvas();
			EffigyViewCube.Draw( canvas, new EffigyViewCube.State( camera, new Vector2( 130, 120 ), 1f, hover, button, hovered, blue ) );
			File.WriteAllText( Path.Combine( outDir, name + ".svg" ), canvas.Svg( 260, 250, "#1f2226", name ) );
		}

		Console.WriteLine( $"wrote {scenes.Length} views to {outDir}" );

		// The radial menu, in the contexts it opens in, idle and with a slot lit.
		var I = new Func<string, string, bool, EffigyRadialMenu.Item>( ( l, i, e ) =>
			new EffigyRadialMenu.Item( l, i, e, e ? null : "Pick lines or points with Select first", e ? "pull this face out" : null ) );
		var face = new[]
		{
			I( "Sketch on face", "draw", true ), I( "Extrude", "open_in_full", true ), I( "Move face", "open_with", true ),
			I( "Hole", "radio_button_unchecked", true ), I( "More…", "more_horiz", true ), I( "Material", "palette", true ),
			I( "Both sides", "flip", true ), I( "Draft", "change_history", true ),
		};
		var empty = new[]
		{
			I( "Sketch", "draw", true ), I( "Box", "check_box_outline_blank", true ), I( "Import", "file_open", true ),
			I( "Fit view", "fit_screen", true ), I( "Redo", "redo", false ), I( "Undo", "undo", true ), I( "Home view", "home", true ),
		};
		var sketch = new[]
		{
			I( "Line", "horizontal_rule", true ), I( "Rectangle", "crop_square", true ), I( "Circle", "radio_button_unchecked", true ), I( "Arc", "architecture", true ),
			I( "Finish sketch", "check", true ), I( "Constrain…", "link", false ), I( "Trim", "content_cut", true ), I( "Select", "near_me", true ),
		};
		var radials = new (string Name, EffigyRadialMenu.Item[] Items, int Hover, string Title)[]
		{
			("radial-face", face, -1, "Face of Box"),
			("radial-face-hover", face, 1, "Face of Box"),
			("radial-empty", empty, 3, null),
			("radial-sketch", sketch, 5, "Sketch 1"),
		};

		foreach ( var (name, items, hover, title) in radials )
		{
			var canvas = new SvgCanvas();
			EffigyRadialMenu.Draw( canvas, new EffigyRadialMenu.State( items, new Vector2( 260, 170 ), 1f, hover, title, blue ) );
			File.WriteAllText( Path.Combine( outDir, name + ".svg" ), canvas.Svg( 520, 340, "#1f2226", name ) );
		}
	}
}
