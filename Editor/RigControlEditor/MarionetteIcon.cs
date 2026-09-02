using Editor;
using Sandbox;

namespace Marionette.EditorTools;

/// <summary>
/// Marionette's Tools-menu mark: a control bar with strings running down to the puppet hanging
/// off them — the thing you actually hold to work a marionette.
///
/// WHY SVG AND NOT A PNG. Effigy's mark is a file on disk (see EffigyWindow.AppIcon), which means
/// it can go missing, get resized badly, or fall out of sync between its two copies in the tree.
/// Sandbox.Bitmap is Skia-backed and takes an SVG string directly, so this one is authored in the
/// source and rasterised at whatever size is asked for. Nothing to ship, nothing to lose.
///
/// STYLE MATCHES EFFIGY deliberately: cream fills, one heavy near-black outline, and a fat white
/// halo behind the whole silhouette so it reads as a sticker on the dark editor chrome. The
/// strings carry the SAME outline weight as everything else rather than being drawn hairline —
/// at the ~16px the menu actually renders this, hairlines vanish and the icon turns to mush.
/// </summary>
internal static class MarionetteIcon
{
	const string Cream = "#F4EFE2";
	const string Ink = "#16181C";
	const string Halo = "#FFFFFF";

	/// <summary>Outline weight, and the halo weight behind it. The halo is drawn as the same
	/// geometry stroked far fatter and painted first, so it spreads evenly on every side.</summary>
	const int InkWidth = 9;
	const int HaloWidth = 26;

	/// <summary>Strings are drawn much finer than the figure, with a halo just wide enough to
	/// keep them off the dark editor chrome.</summary>
	const int StringWidth = 4;
	const int StringHaloWidth = 12;

	/// <summary>
	/// The artwork, in a 256x256 viewBox. Laid out top to bottom: the control bar and its grip,
	/// three strings, then the puppet. Shapes carry only their own fill override — stroke colour
	/// and width are inherited from the group, which is what lets the same body be stroked twice
	/// at two weights for the halo.
	/// </summary>
	/// <summary>
	/// The control and the puppet, in a 256x256 viewBox. Shapes carry only their own fill
	/// override — stroke colour and width are inherited from the group, which is what lets the
	/// same body be stroked twice at two weights for the halo.
	/// </summary>
	const string Figure =
		// --- the control: a long bar with an upright grip crossing it ---
		"""<rect x="44" y="52" width="168" height="20" rx="10"/>""" +
		"""<rect x="118" y="22" width="20" height="64" rx="10"/>""" +
		// --- puppet: head, torso, arms out to the strung hands, legs ---
		"""<circle cx="128" cy="136" r="24"/>""" +
		"""<rect x="110" y="162" width="36" height="46" rx="16"/>""" +
		"""<path fill="none" d="M112 176 L74 167"/>""" +
		"""<path fill="none" d="M144 176 L182 167"/>""" +
		"""<circle cx="72" cy="166" r="9"/>""" +
		"""<circle cx="184" cy="166" r="9"/>""" +
		"""<path fill="none" d="M120 208 L110 230"/>""" +
		"""<path fill="none" d="M136 208 L146 230"/>""";

	/// <summary>
	/// The three strings — bar down to the head, and to each hand. Kept apart from the figure so
	/// they can be stroked thin: at the figure's weight they read as structural outline rather
	/// than as string, which is the whole point of the mark.
	/// </summary>
	const string Strings =
		"""<path fill="none" d="M62 72 L72 166"/>""" +
		"""<path fill="none" d="M128 86 L128 112"/>""" +
		"""<path fill="none" d="M194 72 L184 166"/>""";

	/// <summary>
	/// Four passes, back to front: both halos, then both inks. Strings get a slimmer halo than
	/// the figure so a hairline does not end up wrapped in a fat white sleeve, and the string ink
	/// lands before the figure ink so the strings pass behind the puppet rather than across it.
	///
	/// Written out as literal copies rather than &lt;defs&gt; + &lt;use&gt; because the renderer
	/// here is Skia's, and whether it honours SVG2 bare href or wants xlink:href is not worth
	/// finding out the hard way — a blank icon is exactly the failure this area already had once.
	/// </summary>
	static string BuildSvg() =>
		"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">""" +
		Group( Strings, Halo, StringHaloWidth ) +
		Group( Figure, Halo, HaloWidth ) +
		Group( Strings, Ink, StringWidth ) +
		Group( Figure, Ink, InkWidth ) +
		"""</svg>""";

	static string Group( string body, string stroke, int width ) =>
		$"""<g fill="{Cream}" stroke="{stroke}" stroke-width="{width}" stroke-linecap="round" stroke-linejoin="round">{body}</g>""";

	/// <summary>Rasterise the mark. Returns null rather than throwing if the SVG will not render,
	/// so the menu stamper just keeps its default icon instead of taking the editor down.</summary>
	public static Pixmap Build( int size = 256 )
	{
		try
		{
			var bitmap = Bitmap.CreateFromSvgString( BuildSvg(), size, size );
			return bitmap is null ? null : Pixmap.FromBitmap( bitmap );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"Marionette: could not rasterise the menu icon — {e.Message}" );
			return null;
		}
	}
}
