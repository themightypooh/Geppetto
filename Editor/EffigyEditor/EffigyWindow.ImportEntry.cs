namespace Marionette.EditorTools;

/// <summary>
/// The one wording for Import. It is reachable from File, from Model ▸ Add and from CAD's Sketch
/// stage (plus the CAD right-click pie), and the three used to say three different things — one of
/// them promised .fbx and .vmdl that the reader refuses. Every entry reads its label and tip from
/// here and adds the same <see cref="Effigy.ImportFeature"/>, so they cannot drift apart again.
///
/// TO ADD A FORMAT: teach ImportFeature to read it, then extend <see cref="ImportFormats"/> and
/// <see cref="ImportTip"/>. Nothing else names the formats.
/// </summary>
public sealed partial class EffigyWindow
{
	/// <summary>Menu label (File and Model ▸ Add). CAD's bar button is the short "Import".</summary>
	private const string ImportMenuLabel = "Import Mesh...";

	/// <summary>The formats ImportFeature actually reads, as the right-click pie shows them.</summary>
	private const string ImportFormats = ".obj";

	private const string ImportTip = "Bring in a mesh file as a body — a sculpt, a scan, a kitbash piece. Reads Wavefront " + ImportFormats + " files";
}
