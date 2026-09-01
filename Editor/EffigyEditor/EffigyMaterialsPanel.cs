using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The Materials dock: every material in the project, as a grid you drag out of.
///
/// WHAT THIS REPLACED, AND WHY. It used to be a column of eight rows — "Slot 3 · material_3
/// (default) · [Browse...] · [×]" — one per slot, and it was the wrong shape for the job in a way
/// that is worth writing down so nobody rebuilds it. It made you start from a NUMBER. To put brushed
/// steel on something you picked a slot you had no opinion about, opened a modal file picker, found
/// the material in there, closed it, and then went and painted faces. Seven eighths of the panel was
/// permanently a list of names of things that did not exist yet, and the materials themselves — the
/// only part with a picture — were never on screen at all.
///
/// So it is the browser now, and the slot is what the panel works out for you rather than what it
/// asks you for. You look at materials, not numbers. Drag one onto a face and that face wears it;
/// double-click and the whole part does. Which slot carries what is <see cref="Effigy.MaterialDrop"/>'s
/// problem, in the kernel where MaterialDropTests can hold it to account.
///
/// The slot has NOT gone away — it is what geometry actually references and what the exporters
/// write. It is now shown rather than chosen: a material the document uses wears its slot number as
/// a badge, the footer counts how many slots are bound, and the right-click menu is where you go
/// when you do want to name a specific one. That is the same information the eight rows carried, in
/// the space the eight rows were wasting.
///
/// This is a small copy of the editor's own asset browser, not the real one. The real AssetBrowser
/// is a whole window — locations, folder tree, tag filters, cloud tabs, a context menu that renames
/// and deletes files on disk — and none of that belongs in a dock whose job is "pick one of these
/// and put it on the model". What is kept is the part that makes it the browser: real asset
/// thumbnails, a search box, and a drag carrying the same payload the real list sets.
/// </summary>
internal sealed class EffigyMaterialsPanel : Widget, AssetSystem.IEventListener
{
	private PartStudio _studio;

	private readonly LineEdit _search;
	private readonly ListView _list;
	private readonly Editor.Label _status;

	/// <summary>Every material in the project, unfiltered. Walked only when the asset system says
	/// something changed — typing in the search box filters this rather than re-walking it.</summary>
	private List<AssetEntry> _all = new();

	/// <summary>Which slot each material on screen is bound to, by the entry drawn for it. Rebuilt
	/// on <see cref="Refresh"/> so <see cref="PaintCell"/> — which runs per cell per frame — reads a
	/// dictionary rather than asking the kernel once per cell.</summary>
	private readonly Dictionary<AssetEntry, int> _slots = new();

	/// <summary>The same materials keyed by MaterialDrop.Normalise of their path, so a slot's name
	/// finds its cell in one lookup. Built with the list in <see cref="Rescan"/> and not touched by
	/// a rebuild, because what the project contains cannot change on one.</summary>
	private readonly Dictionary<string, AssetEntry> _byPath = new();

	/// <summary>Side of one cell, in pixels. Big enough that two greys are different pictures rather
	/// than two grey squares, which is the whole reason to show thumbnails instead of file names.
	/// </summary>
	private const int CellSize = 92;

	/// <summary>A slot was given a material, or cleared back to its numbered default. The same
	/// contract this panel has always had, and still wired to the window's SetSlotMaterial: the
	/// panel reports the pick and the window owns the studio, the undo stack and the rebuild.
	/// </summary>
	public Action<int, string> MaterialChanged { get; set; }

	/// <summary>
	/// A material was double-clicked.
	///
	/// The window binds this to the part's BASE material — slot 0, what every face nobody has
	/// painted is on — which is the one assignment dragging cannot make: a drop lands on ONE face,
	/// and MaterialDrop never allocates slot 0 precisely because doing so would paint the whole
	/// part. Double-click is where "paint the whole part" belongs, so the two gestures cover the two
	/// things you actually want and neither can be the other by accident.
	/// </summary>
	public Action<string> MaterialActivated { get; set; }

	public EffigyMaterialsPanel( Widget parent, PartStudio studio ) : base( parent )
	{
		Name = "Materials";
		WindowTitle = "Materials";
		SetWindowIcon( "palette" );

		_studio = studio;

		Layout = Layout.Column();

		var header = Layout.AddRow();
		header.Margin = new Sandbox.UI.Margin( 6, 6, 6, 2 );
		header.Spacing = 6;

		_search = new LineEdit( this ) { PlaceholderText = "Search materials" };
		_search.TextEdited += _ => Populate();
		header.Add( _search, 1 );

		var reload = new Button( "", "refresh", this ) { FixedWidth = 26, ToolTip = "Look for materials again" };
		reload.Clicked = Rescan;
		header.Add( reload );

		_list = Layout.Add( new ListView( this ), 1 );
		_list.ItemSize = new Vector2( CellSize, CellSize + 22 );
		_list.ItemSpacing = 2;
		_list.MultiSelect = false;
		_list.Margin = new Sandbox.UI.Margin( 4 );
		_list.ItemPaint = PaintCell;

		// Thumbnails are rendered on demand and cost real time, so the list only asks for the ones
		// scrolled into view — the same deal the editor's own asset list makes. Without this, every
		// material in the project would be rendered on the first frame the dock is opened.
		_list.ItemScrollEnter = item => (item as AssetEntry)?.OnScrollEnter();
		_list.ItemScrollExit = item => (item as AssetEntry)?.OnScrollExit();

		_list.ItemSelected = item => ShowPath( item as AssetEntry );
		_list.ItemActivated = item => Activate( item as AssetEntry );
		_list.ItemContextMenu = item => OpenCellMenu( item as AssetEntry );
		_list.ItemDrag = BeginDrag;

		var footer = Layout.AddRow();
		footer.Margin = new Sandbox.UI.Margin( 8, 2, 8, 6 );

		_status = new Editor.Label( "" ) { Color = Theme.TextLight.WithAlpha( 0.6f ) };
		footer.Add( _status, 1 );

		Rescan();
	}

	public void SetStudio( PartStudio studio )
	{
		_studio = studio ?? new PartStudio();
		Refresh();
	}

	/// <summary>
	/// Bring the panel up to date with the studio.
	///
	/// This runs on EVERY rebuild, which includes every tick of a dragged parameter, so it must stay
	/// cheap — and it is: the grid's CONTENTS come from the asset system and cannot change on a
	/// rebuild, so nothing here touches the list's items. All that moves is which slot each material
	/// is bound to, which is a dictionary and a line of footer text. The old panel had to work much
	/// harder here, rebuilding rows only when the set of slots changed, because its rows WERE the
	/// document; these cells are the project.
	/// </summary>
	public void Refresh()
	{
		MapSlots();
		ShowSummary();

		// Repaint, because the badges are drawn from _slots and nothing else would ask for a frame.
		_list?.Update();
	}

	/// <summary>
	/// Work out which of the materials on screen the document has bound to a slot.
	///
	/// Walks the SLOTS and looks each one up, not the materials asking each one which slot it is on.
	/// The two give the same answer and cost wildly different amounts: there are a handful of named
	/// slots and there can be hundreds of materials in a project, and this runs on every rebuild —
	/// which is every tick of a dragged parameter.
	///
	/// Matched through MaterialDrop.Normalise rather than by comparing the strings directly, because
	/// a slot named with backslashes and an asset path with forward ones are the same material. That
	/// rule lives in the kernel and is used from there rather than restated here: a second copy
	/// would agree with the first until one of them learned something.
	///
	/// Lowest slot wins if a document names two with the same material, matching SlotCarrying — the
	/// badge must show the slot a drop would actually reuse.
	/// </summary>
	private void MapSlots()
	{
		_slots.Clear();

		if ( _studio is null )
			return;

		foreach ( var (slot, name) in _studio.MaterialNames.OrderBy( kv => kv.Key ) )
		{
			if ( MaterialDrop.Normalise( name ) is not { } key )
				continue;

			if ( _byPath.TryGetValue( key, out var entry ) && !_slots.ContainsKey( entry ) )
				_slots[entry] = slot;
		}
	}

	/// <summary>
	/// Start the drag. This is the feature.
	///
	/// Data.Text is the RELATIVE path, and it has to be: it is what the editor's own asset list puts
	/// there, so anything in the editor that already accepts a dragged material accepts one from
	/// this dock too, and it is what EffigyMaterialSlot stores when you pick through browse — two
	/// routes to the same slot must not write two different spellings of the same asset.
	///
	/// Data.Url is the absolute path as a file:// URI, again matching the asset list. Some drop
	/// targets read one, some the other, and a drag that fills in only half of it works everywhere
	/// you tested and nowhere else.
	/// </summary>
	private bool BeginDrag( object item )
	{
		if ( item is not AssetEntry entry || entry.Asset is null )
			return false;

		var drag = new Drag( this );

		drag.Data.Text = entry.Asset.RelativePath;
		drag.Data.Url = new Uri( "file:///" + entry.Asset.AbsolutePath );
		drag.Execute();

		return true;
	}

	/// <summary>
	/// Right-click a material: bind it to a slot by hand, or take it off the one it is on.
	///
	/// This is where the old eight rows went. Everything they could do is here — put a material on
	/// slot 5 without touching any geometry, take it off again — but reached from the material,
	/// which is the thing you have in mind, rather than from a number you do not. It is also the
	/// only route to a slot ABOVE seven, for a document that arrived using one.
	/// </summary>
	private void OpenCellMenu( AssetEntry entry )
	{
		if ( entry?.Asset is not { } asset || _studio is null )
			return;

		var menu = new Menu( this );
		var path = asset.RelativePath;
		var current = _slots.TryGetValue( entry, out var bound ) ? bound : -1;

		menu.AddHeading( Path.GetFileName( path ) );

		var whole = menu.AddOption( "Use for the whole part", "format_paint", () => MaterialActivated?.Invoke( path ) );
		whole.StatusTip = "Slot 0 — every face nobody has painted";
		whole.Checkable = true;
		whole.Checked = current == 0;

		var slots = menu.AddMenu( "Bind to slot", "layers" );

		foreach ( var slot in BindableSlots( current ) )
		{
			var option = slots.AddOption( _studio.NameForSlot( slot ), null, () => MaterialChanged?.Invoke( slot, path ) );

			option.Checkable = true;
			option.Checked = slot == current;
		}

		if ( current >= 0 )
		{
			var clear = menu.AddOption( $"Unbind from slot {current}", "backspace",
				() => MaterialChanged?.Invoke( current, null ) );

			clear.StatusTip = $"Back to the default name — exports as {ObjWriter.DefaultMaterialName( current )}";
		}

		menu.OpenAtCursor();
	}

	/// <summary>
	/// Which slots the bind menu offers: zero through seven, plus anything the document already
	/// uses, plus whichever one this material is on.
	///
	/// Seven is not arbitrary — it is how many colours the viewport tints slots with, so every slot
	/// offered is one you can tell apart on screen. The kernel allows 0..63 and nobody picks slot 40
	/// off a list, but a document that arrived with one must not be unreachable.
	/// </summary>
	private IEnumerable<int> BindableSlots( int current )
	{
		var slots = new SortedSet<int>();

		for ( var i = 0; i <= 7; i++ )
			slots.Add( i );

		foreach ( var slot in FaceMaterialEdit.UsedSlots( _studio ) )
			slots.Add( slot );

		if ( current >= 0 )
			slots.Add( current );

		return slots;
	}

	/// <summary>Walk the asset system again. Cheap enough for a dock opening or an asset import, not
	/// cheap enough for a keystroke — which is why searching filters <see cref="_all"/> instead.
	/// </summary>
	private void Rescan()
	{
		_all = AssetSystem.All
			.Where( a => a is not null && a.AssetType == AssetType.Material )
			.OrderBy( a => a.RelativePath, StringComparer.OrdinalIgnoreCase )
			.Select( a => new AssetEntry( a ) )
			.ToList();

		_byPath.Clear();

		foreach ( var entry in _all )
		{
			// First wins, so the index agrees with the ordering above rather than with whichever
			// duplicate the asset system happened to hand over last.
			if ( MaterialDrop.Normalise( entry.Asset?.RelativePath ) is { } key )
				_byPath.TryAdd( key, entry );
		}

		MapSlots();
		Populate();
	}

	/// <summary>
	/// Put the matching materials in the list.
	///
	/// Matched against the whole relative path rather than the file name, because materials are
	/// organised by folder and "dev" or "metal" is a folder far more often than it is a file name —
	/// searching only names would answer "no materials" for a project full of them.
	/// </summary>
	private void Populate()
	{
		var query = _search.Value?.Trim();

		var shown = string.IsNullOrEmpty( query )
			? _all
			: _all.Where( e => e.Asset.RelativePath.Contains( query, StringComparison.OrdinalIgnoreCase ) ).ToList();

		_list.SetItems( shown );

		ShowSummary();
	}

	/// <summary>
	/// The footer: how many materials are listed, and what the part is still missing.
	///
	/// The second half is the one thing the old row list was genuinely good at — an unbound slot was
	/// visible as a gap rather than discovered on export. It does NOT survive as a bound-over-total
	/// ratio, which was the obvious translation and a useless one: the total would be the slots the
	/// document has an opinion about, and naming a slot is what gives it one, so the two numbers
	/// would be equal almost always and would read as "everything is fine" while a slot the geometry
	/// paints sat unnamed.
	///
	/// The number that means something is the count of slots a FaceMaterialFeature paints that
	/// nobody has bound a material to. Those export as `material_4` and are exactly the thing you
	/// find out about too late. Which ones they are is answered by the badges: a painted slot with
	/// no material has no badge anywhere in the grid.
	/// </summary>
	private void ShowSummary()
	{
		if ( !_status.IsValid() )
			return;

		if ( _all.Count == 0 )
		{
			_status.Text = "No materials found in this project";
			return;
		}

		var listed = _list.Items.Count();
		var count = listed == _all.Count ? $"{_all.Count} materials" : $"{listed} of {_all.Count}";

		var bound = _studio?.MaterialNames.Count( kv => !string.IsNullOrWhiteSpace( kv.Value ) ) ?? 0;

		if ( bound == 0 )
		{
			_status.Text = $"{count} — drag onto a face, double-click for the whole part";
			return;
		}

		var unnamed = FaceMaterialEdit.UsedSlots( _studio )
			.Count( slot => string.IsNullOrWhiteSpace( SlotMaterial( slot ) ) );

		_status.Text = unnamed == 0
			? $"{count} · {bound} bound"
			: $"{count} · {bound} bound, {unnamed} painted slot{(unnamed == 1 ? "" : "s")} unnamed";
	}

	/// <summary>What a slot carries, or null. Deliberately not NameForSlot, which substitutes
	/// material_N for an unbound slot and so has no empty answer — and "is anything bound here" is
	/// the question with one.</summary>
	private string SlotMaterial( int slot ) =>
		_studio is not null && _studio.MaterialNames.TryGetValue( slot, out var name )
			&& !string.IsNullOrWhiteSpace( name )
			? name
			: null;

	private void ShowPath( AssetEntry entry )
	{
		if ( _status.IsValid() && entry?.Asset is { } asset )
			_status.Text = asset.RelativePath;
	}

	/// <summary>Double-click. Reported rather than acted on, exactly as the old rows reported their
	/// picks: this dock does not own the studio and must not edit it.</summary>
	private void Activate( AssetEntry entry )
	{
		if ( entry?.Asset is { } asset )
			MaterialActivated?.Invoke( asset.RelativePath );
	}

	/// <summary>
	/// One cell: thumbnail, the file name under it, and the slot badge if the part uses it.
	///
	/// Hand-painted rather than borrowed from AssetList.PaintIconMode because that one also draws
	/// the asset-type strip and the type badge, which exist to tell a .vmat from a .vmdl in a list
	/// holding both. Everything here is a material, so those would be a coloured bar on every cell
	/// saying the same thing — and the corner they occupy is where the slot badge, which says
	/// something different per cell, needs to go.
	/// </summary>
	private void PaintCell( VirtualWidget item )
	{
		if ( item.Object is not AssetEntry entry )
			return;

		var rect = item.Rect.Shrink( 2 );

		if ( Paint.HasSelected || Paint.HasPressed )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.Blue.Darken( 0.4f ) );
			Paint.DrawRect( rect, Theme.ControlRadius );
		}
		else if ( Paint.HasMouseOver )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.SurfaceLightBackground.WithAlpha( 0.4f ) );
			Paint.DrawRect( rect, Theme.ControlRadius );
		}

		var icon = rect.Shrink( 4 );
		icon.Height = icon.Width;

		Paint.BilinearFiltering = true;
		entry.DrawIcon( icon );
		Paint.BilinearFiltering = false;

		var text = rect.Shrink( 4, 0 );
		text.Top = icon.Bottom + 2;

		Paint.SetDefaultFont( 7 );
		Paint.ClearBrush();
		Paint.SetPen( Theme.Text.WithAlpha( 0.8f ) );

		var name = Path.GetFileNameWithoutExtension( entry.Name );

		Paint.DrawText( text, Paint.GetElidedText( name, text.Width, ElideMode.Middle ), TextFlag.LeftTop );

		if ( _slots.TryGetValue( entry, out var slot ) )
			PaintSlotBadge( icon, slot );
	}

	/// <summary>
	/// The slot number, in the slot's own viewport colour.
	///
	/// The COLOUR is the point, more than the number: the viewport shades painted faces with a
	/// per-slot palette, so a badge in the matching colour is what connects the green patch on the
	/// model to the material that put it there. Slot 0 gets the neutral treatment because the
	/// viewport pointedly does not tint it — it is the part's base, not a painted patch.
	/// </summary>
	private static void PaintSlotBadge( Rect icon, int slot )
	{
		var badge = new Rect( icon.Right - 20, icon.Top + 2, 18, 14 );

		Paint.ClearPen();
		Paint.SetBrush( slot == 0 ? Theme.ControlBackground : EffigyViewport.SlotColor( slot ) );
		Paint.DrawRect( badge, 3 );

		Paint.SetDefaultFont( 6 );
		Paint.ClearBrush();
		Paint.SetPen( slot == 0 ? Theme.Text.WithAlpha( 0.8f ) : Color.Black.WithAlpha( 0.85f ) );
		Paint.DrawText( badge, slot.ToString(), TextFlag.Center );
	}

	/// <summary>
	/// A material was added, deleted or reimported somewhere else in the editor.
	///
	/// Worth listening for rather than leaving to the reload button: the ordinary way to get a
	/// material into an Effigy part is to make one in the material editor and then come here for it,
	/// and a browser that cannot see the material you just made is a browser you stop trusting.
	/// </summary>
	void AssetSystem.IEventListener.OnAssetSystemChanges() => Rescan();
}
