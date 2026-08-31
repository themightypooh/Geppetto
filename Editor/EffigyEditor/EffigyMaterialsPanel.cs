using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Every material slot in the document, in one list.
///
/// The feature dialog answers "what does the slot this feature paints with look like" and the face
/// menu answers "what does the slot under the cursor look like". Neither answers "what does this
/// part look like", which is the question you actually have when binding a finished model — and
/// answering it by right-clicking your way around the geometry hunting for a face on slot 5 is not
/// answering it. This panel is the whole set at once, so an unassigned slot is visible as a gap
/// rather than discovered on export.
///
/// Rows are <see cref="EffigyMaterialSlot"/>, the same control the other two use. It writes to
/// PartStudio.MaterialNames through the window, because the window owns the studio, the undo stack
/// and the rebuild — the panel reports the pick rather than acting on it, exactly as the Parts
/// list reports its eye.
/// </summary>
internal sealed class EffigyMaterialsPanel : Widget
{
	private PartStudio _studio;
	private readonly Widget _list;
	private readonly Editor.Label _summary;

	/// <summary>The rows on screen, by slot. Kept so an ordinary rebuild can push new names into
	/// them instead of replacing them — see <see cref="Refresh"/>.</summary>
	private readonly Dictionary<int, EffigyMaterialSlot> _rows = new();

	/// <summary>A slot was given a material, or cleared back to its numbered default.</summary>
	public Action<int, string> MaterialChanged { get; set; }

	public EffigyMaterialsPanel( Widget parent, PartStudio studio ) : base( parent )
	{
		Name = "Materials";
		WindowTitle = "Materials";
		SetWindowIcon( "palette" );

		_studio = studio;
		Layout = Layout.Column();

		var header = Layout.AddRow();
		header.Margin = new Sandbox.UI.Margin( 8, 4 );
		header.Spacing = 8;
		header.Add( new Editor.Label( "Materials" ) { FixedWidth = 80 } );

		_summary = new Editor.Label( "" ) { Color = Theme.TextLight.WithAlpha( 0.6f ) };
		header.Add( _summary, 1 );

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		_list = new Widget( this ) { Layout = Layout.Column() };
		_list.Layout.Margin = new Sandbox.UI.Margin( 6, 4 );
		_list.Layout.Spacing = 3;
		scroll.Canvas = _list;

		Refresh();
	}

	public void SetStudio( PartStudio studio )
	{
		_studio = studio ?? new PartStudio();
		Refresh();
	}

	/// <summary>
	/// Bring the list up to date with the studio.
	///
	/// Two paths, and the split is not premature: this runs on EVERY rebuild, which includes every
	/// tick of a dragged parameter. The SET of slots changes rarely — painting a face onto slot 9 is
	/// what makes slot 9 exist — so the common call finds the same slots it already shows and only
	/// pushes names into the rows already there. Rebuilding them all would throw away and remake a
	/// dozen widgets per frame of a drag, and would do it while standing inside one of those
	/// widgets' own click handlers, which is how the × button would delete the button being clicked.
	/// </summary>
	public void Refresh()
	{
		var slots = Slots().ToList();

		if ( !_rows.Keys.OrderBy( s => s ).SequenceEqual( slots ) )
			RebuildRows( slots );

		foreach ( var (slot, row) in _rows )
		{
			if ( row.IsValid() )
				row.Refresh( MaterialFor( slot ) );
		}

		if ( _summary.IsValid() )
			_summary.Text = $"{slots.Count( s => MaterialFor( s ) is not null )}/{slots.Count} assigned";
	}

	private void RebuildRows( List<int> slots )
	{
		_rows.Clear();
		_list.Layout.Clear( true );

		foreach ( var slot in slots )
		{
			var row = new EffigyMaterialSlot( _list, slot, MaterialFor( slot ) )
			{
				Changed = ( s, path ) => MaterialChanged?.Invoke( s, path ),
			};

			_rows[slot] = row;
			_list.Layout.Add( row );
		}
	}

	/// <summary>
	/// What a slot carries, or null.
	///
	/// Deliberately not NameForSlot: that substitutes material_N for an unassigned slot, and the row
	/// draws its own greyed-out version of exactly that. Handing it the substitute would make every
	/// empty slot look deliberately named, and would count every one of them as assigned.
	/// </summary>
	private string MaterialFor( int slot ) =>
		_studio is not null && _studio.MaterialNames.TryGetValue( slot, out var name )
			&& !string.IsNullOrWhiteSpace( name )
			? name
			: null;

	/// <summary>
	/// Which slots to list: zero through seven, plus anything the document already uses.
	///
	/// The same set the face menu offers, and for the same reason — seven is how many colours the
	/// viewport tints with, so every slot listed is one you can tell apart on screen, and a document
	/// that arrived using slot 40 must not have an unreachable material.
	///
	/// Slot 0 is included and is not a special case here. It is the absence of an ASSIGNMENT, which
	/// the viewport shows by leaving those faces untinted, but it is still a real slot on export:
	/// every face nobody painted goes out as material_0, so it is usually the largest surface on the
	/// model and the one most worth naming.
	/// </summary>
	private IEnumerable<int> Slots()
	{
		var slots = new SortedSet<int>();

		for ( var i = 0; i <= 7; i++ )
			slots.Add( i );

		foreach ( var slot in FaceMaterialEdit.UsedSlots( _studio ) )
			slots.Add( slot );

		return slots;
	}
}
