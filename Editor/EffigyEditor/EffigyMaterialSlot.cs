using Editor;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// One material slot, and the material assigned to it.
///
/// WHAT A SLOT ACTUALLY IS. Effigy's faces carry an integer, not a material — Face.Material is a
/// group number, and PartStudio.MaterialNames maps that number to a name which ObjWriter emits as
/// `usemtl`. The number is the thing geometry references and it has to stay an integer for that to
/// keep working across a rebuild. This control does not replace it; it fills in the other half, so
/// the name a slot carries is a real material asset path rather than "material_3".
///
/// ONE CONTROL, THREE HOMES. The same row appears beside the slot number in a feature's dialog and
/// once per slot in the Materials panel; the face right-click menu opens the same picker through
/// <see cref="Pick"/> rather than embedding the row, because a menu closes the instant you click
/// anything in it and would take the row — and the modal it parented — down with it.
///
/// Picking is per SLOT, not per face or per feature: assigning here repaints every face on that
/// slot across the whole part. That is the same thing Blender's material slots do, and it is what
/// makes painting faces worth doing at all.
/// </summary>
internal sealed class EffigyMaterialSlot : Widget
{
	private readonly int _slot;
	private readonly Editor.Label _name;
	private string _current;

	/// <summary>Called with the slot and its new material path, or null when cleared.</summary>
	public Action<int, string> Changed { get; set; }

	public EffigyMaterialSlot( Widget parent, int slot, string current, bool showSlotLabel = true ) : base( parent )
	{
		_slot = slot;
		_current = current;

		Layout = Layout.Row();
		Layout.Spacing = 6;

		if ( showSlotLabel )
			Layout.Add( new Editor.Label( $"Slot {slot}" ) { FixedWidth = 62 } );

		_name = new Editor.Label( Describe( current ) ) { Color = NameColour( current ) };
		Layout.Add( _name, 1 );

		var browse = new Button( "Browse..." ) { FixedWidth = 78 };
		browse.Clicked = () => Pick( this, _slot, _current, Apply );
		Layout.Add( browse );

		var clear = new Button( "×" ) { FixedWidth = 24, ToolTip = "Back to the default name for this slot" };
		clear.Clicked = () => Apply( _slot, null );
		Layout.Add( clear );

		Refresh( current );
	}

	/// <summary>Show a value that changed somewhere else — the same slot edited from the panel while
	/// its feature's dialog is open, or an undo.</summary>
	public void Refresh( string path )
	{
		_current = path;

		if ( !_name.IsValid() )
			return;

		_name.Text = Describe( path );
		_name.Color = NameColour( path );

		// The path is the tooltip because the label shows only the file name. Materials live several
		// folders deep and the full path is what tells two `metal.vmat`s apart, but it is also far
		// wider than any row this sits in.
		_name.ToolTip = string.IsNullOrWhiteSpace( path )
			? $"Nothing picked — exports as material_{_slot}"
			: path;
	}

	/// <summary>
	/// The editor's own material browser, exactly as the Hotspot editor opens it.
	///
	/// Static so the face menu can open it after closing itself, with nothing of this widget left
	/// alive. OnAssetPicked rather than OnAssetHighlighted: highlighting fires as you arrow through
	/// the list, and every one of those would be a studio edit and a rebuild.
	/// </summary>
	public static void Pick( Widget parent, int slot, string current, Action<int, string> picked )
	{
		var picker = AssetPicker.Create( parent, AssetType.Material );

		picker.Window.Title = $"Material for slot {slot}";
		picker.OnAssetPicked = assets =>
		{
			foreach ( var asset in assets )
			{
				// RelativePath, not Path or AbsolutePath. It is what the asset system resolves a
				// material by and what an exported OBJ's usemtl has to say to mean anything on
				// another machine; an absolute path is true only on this one.
				picked?.Invoke( slot, asset?.RelativePath );
				break;
			}
		};

		picker.Show();

		// Open the browser standing on the slot's current material rather than at the top of the
		// list, so re-picking is a nudge instead of a hunt. A slot still carrying a hand-typed name
		// resolves to nothing and the picker just opens unselected.
		if ( !string.IsNullOrWhiteSpace( current ) )
			picker.SetSelection( current );
	}

	private void Apply( int slot, string path )
	{
		Refresh( path );
		Changed?.Invoke( slot, path );
	}

	/// <summary>Nothing assigned reads as the default rather than as blank, because a slot always
	/// exports SOMETHING — ObjWriter falls back to material_N — and an empty label would suggest the
	/// face has no material at all.</summary>
	private string Describe( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return $"material_{_slot} (default)";

		// Only the last segment, and only when the value looks like a path at all: a document written
		// before this control existed carries whatever name somebody typed, and chopping "brushed
		// steel" at a slash it does not have would show it back unchanged anyway.
		var cut = path.LastIndexOfAny( new[] { '/', '\\' } );

		return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
	}

	private static Color NameColour( string path ) =>
		string.IsNullOrWhiteSpace( path ) ? Theme.TextControl.WithAlpha( 0.55f ) : Theme.Text;
}
