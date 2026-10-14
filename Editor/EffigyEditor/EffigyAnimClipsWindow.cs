using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The clips that will be compiled into the model, and the buttons that add and remove them.
///
/// A DIALOG RATHER THAN A SUBMENU, which was the first shape this took. The list changes while the
/// tool is open, and a menu built once in BuildMenuBar shows whatever was true when the window
/// opened. Rebuilding the menu bar to refresh it is exactly the mistake SetPalette records at the
/// bottom of EffigyWindow — it throws away the Edit and View menus to redraw one entry. A dialog
/// owns its own list and can rebuild it as often as it likes.
///
/// WHAT THIS DOES NOT DO. It does not edit animation. A clip is a `.riganim` authored in
/// Marionette, and this picks one — Effigy owns rigging, Marionette owns motion, and that division
/// is on the record (WHAT-IS-BUILT, "The rig design, as decided"). The two things editable here are
/// the two that belong to the EXPORT rather than to the animation: what the clip is called inside
/// the model, and whether it loops.
/// </summary>
internal sealed class EffigyAnimClipsWindow : Window
{
	private readonly List<EffigyAnimExport.ClipSource> _clips;

	/// <summary>Raised after any edit to the list — add, remove, rename, looping — so the owner can
	/// dirty its document. The list is saved beside the .effigy (AnimClipsSidecar), so an edit here
	/// is an unsaved change like any other.</summary>
	private readonly Action _changed;

	private Widget _list;
	private Editor.Label _empty;

	public EffigyAnimClipsWindow( Widget owner, List<EffigyAnimExport.ClipSource> clips, Action changed = null )
	{
		_clips = clips;
		_changed = changed;

		// Owned by Effigy, for the reason EffigySettingsWindow spells out: parenting a dialog to a
		// different top-level window hands focus to that window's group and drops Effigy behind it.
		Parent = owner;

		WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.CloseButton
			| WindowFlags.WindowSystemMenuHint | WindowFlags.WindowTitle;

		WindowTitle = "Animation Clips";
		Size = new Vector2( 460, 380 );

		SetWindowIcon( "movie" );

		Build();
	}

	private void Build()
	{
		var canvas = new Widget( this ) { Layout = Layout.Column() };
		canvas.Layout.Margin = 12;
		canvas.Layout.Spacing = 8;

		var intro = new Editor.Label(
			"Clips compiled into the model by File → Compile .vmdl. Each one is a .riganim "
			+ "posed in Marionette against this model's own rig." )
		{
			WordWrap = true,
		};

		canvas.Layout.Add( intro );

		var scroll = canvas.Layout.Add( new ScrollArea( canvas ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		_list = new Widget( canvas ) { Layout = Layout.Column() };
		_list.Layout.Margin = 2;
		_list.Layout.Spacing = 6;
		scroll.Canvas = _list;

		var buttons = canvas.Layout.AddRow();
		buttons.Spacing = 6;
		buttons.Add( new Button( "Add Clip...", "add" ) { Clicked = OpenPicker } );
		buttons.AddStretchCell();
		buttons.Add( new Button( "Close" ) { Clicked = Close } );

		Canvas = canvas;

		Rebuild();
	}

	/// <summary>
	/// The rows, thrown away and rebuilt.
	///
	/// Cheap enough at this size, and it is the version that cannot go stale — a row holds a
	/// reference to its ClipSource, so a partial update would have to track which row was which
	/// after a removal reindexed everything.
	/// </summary>
	private void Rebuild()
	{
		_list.Layout.Clear( true );

		if ( _clips.Count == 0 )
		{
			_empty = new Editor.Label(
				"No clips. The model still compiles — it just has no animation in it." )
			{
				WordWrap = true,
			};

			_list.Layout.Add( _empty );
			_list.Layout.AddStretchCell();

			return;
		}

		foreach ( var clip in _clips.ToList() )
			AddRow( clip );

		_list.Layout.AddStretchCell();
	}

	private void AddRow( EffigyAnimExport.ClipSource clip )
	{
		var row = new Widget( _list ) { Layout = Layout.Column() };
		row.Layout.Spacing = 4;

		var top = row.Layout.AddRow();
		top.Spacing = 6;

		var name = new LineEdit( clip.Name, row )
		{
			ToolTip = "The name this clip has INSIDE the model — what AnimGraph and "
				+ "SetAnimParameter will ask for. It does not have to match the file name.",
		};

		// TextEdited rather than EditingFinished: the dialog can be closed with the caret still in
		// the box, and a name typed but not committed would export under the old one.
		name.TextEdited += text =>
		{
			clip.Name = text;
			_changed?.Invoke();
		};

		top.Add( name, 1 );

		var remove = new Button( "", "delete", row )
		{
			FixedWidth = 28,
			ToolTip = "Take this clip out of the export. The .riganim file is left alone.",
			Clicked = () =>
			{
				_clips.Remove( clip );
				_changed?.Invoke();
				Rebuild();
			},
		};

		top.Add( remove );

		var bottom = row.Layout.AddRow();
		bottom.Spacing = 6;

		var path = new Editor.Label( clip.Asset?.Path ?? $"(missing) {clip.MissingPath}" )
		{
			ToolTip = clip.Asset?.Path ?? clip.MissingPath,
		};

		bottom.Add( path, 1 );
		bottom.Add( new Editor.Label( "Looping" ) );

		var looping = new EffigyToggleSwitch( row, clip.Looping );
		looping.ValueChanged = value =>
		{
			clip.Looping = value;
			_changed?.Invoke();
		};

		bottom.Add( looping );

		_list.Layout.Add( row );
	}

	/// <summary>
	/// Same picker Marionette opens its own clips with, filtered to the same resource type.
	/// </summary>
	private void OpenPicker()
	{
		var picker = AssetPicker.Create( this, AssetType.FromType( typeof( RigAnimDocument ) ),
			new AssetPicker.PickerOptions() );

		picker.Title = "Add Animation Clip";

		picker.OnAssetPicked = assets =>
		{
			var added = false;

			foreach ( var asset in assets ?? Enumerable.Empty<Asset>() )
			{
				if ( asset is null )
					continue;

				// Adding the same file twice would write one .dmx and list it twice, so the model
				// would carry two identical animations under two names.
				if ( _clips.Any( c => (c.Asset?.Path ?? c.MissingPath) == asset.Path ) )
					continue;

				_clips.Add( new EffigyAnimExport.ClipSource
				{
					Asset = asset,
					Name = UniqueName( EffigyAnimExport.ClipSource.NameOf( asset ) ),
				} );

				added = true;
			}

			if ( added )
				_changed?.Invoke();

			Rebuild();
		};

		picker.Show();
	}

	/// <summary>
	/// A name no other queued clip is using. Two clips called the same thing is a warning at export
	/// time and a dropped clip; catching it here means the list never contains the problem.
	/// </summary>
	private string UniqueName( string wanted )
	{
		if ( !_clips.Any( c => string.Equals( c.Name, wanted, StringComparison.OrdinalIgnoreCase ) ) )
			return wanted;

		for ( var n = 2; n < 1000; n++ )
		{
			var candidate = $"{wanted}_{n}";

			if ( !_clips.Any( c => string.Equals( c.Name, candidate, StringComparison.OrdinalIgnoreCase ) ) )
				return candidate;
		}

		return wanted;
	}
}

/// <summary>
/// Where the Animation Clips list lives next to a document.
///
/// One JSON file beside the .effigy: `model.effigy` keeps its clips in `model.animclips.json`. Not
/// in the document itself, for the reason on EffigyWindow._animClips — a .riganim references a
/// model, so a model referencing its .riganims is a loop StudioDocument would have to break. A
/// side-car is editor-only, so the kernel never sees it and the loop never forms.
///
/// Each clip is stored by its project-relative asset path (Asset.Path) and resolved again with
/// AssetSystem.FindByPath on load, the same way the rest of the editor finds models and materials.
/// A path nothing answers to is kept as MissingPath rather than dropped, so a clip whose addon is
/// not mounted today is not erased by the next save.
///
/// UNLIKE SculptSidecar and ImportSidecar, saving an EMPTY list deletes the file. Theirs are keyed
/// by feature id, so a stale blob is simply never claimed; this one is the whole list, so a stale
/// file would put back every clip the user had just removed.
/// </summary>
internal static class AnimClipsSidecar
{
	public const string Suffix = ".animclips.json";

	public static string PathFor( string documentPath )
	{
		if ( string.IsNullOrWhiteSpace( documentPath ) )
			throw new ArgumentException( "A document path is needed to find its animation clips.", nameof( documentPath ) );

		var dir = Path.GetDirectoryName( documentPath ) ?? "";
		return Path.Combine( dir, Path.GetFileNameWithoutExtension( documentPath ) + Suffix );
	}

	sealed class ClipsData
	{
		public int Version { get; set; } = 1;
		public ClipData[] Clips { get; set; }
	}

	sealed class ClipData
	{
		public string Asset { get; set; }
		public string Name { get; set; }
		public bool Looping { get; set; } = true;
	}

	/// <summary>Write the list, or delete the file if the list is empty. Returns how many it wrote.</summary>
	public static int Save( IReadOnlyList<EffigyAnimExport.ClipSource> clips, string documentPath )
	{
		var path = PathFor( documentPath );

		var entries = (clips ?? Array.Empty<EffigyAnimExport.ClipSource>())
			.Where( c => c is not null && !string.IsNullOrWhiteSpace( c.Asset?.Path ?? c.MissingPath ) )
			.Select( c => new ClipData { Asset = c.Asset?.Path ?? c.MissingPath, Name = c.Name, Looping = c.Looping } )
			.ToArray();

		if ( entries.Length == 0 )
		{
			if ( File.Exists( path ) )
				File.Delete( path );

			return 0;
		}

		File.WriteAllText( path, Json.Serialize( new ClipsData { Clips = entries } ) );
		return entries.Length;
	}

	/// <summary>
	/// Replace <paramref name="clips"/> with what is saved beside the document. No file is an empty
	/// list. A file that will not parse throws, and the list is left empty rather than half-filled.
	/// </summary>
	public static int Load( List<EffigyAnimExport.ClipSource> clips, string documentPath )
	{
		if ( clips is null )
			throw new ArgumentNullException( nameof( clips ) );

		clips.Clear();

		var path = PathFor( documentPath );

		if ( !File.Exists( path ) )
			return 0;

		var data = Json.Deserialize<ClipsData>( File.ReadAllText( path ) )
			?? throw new InvalidDataException( "The clip list is empty or not JSON." );

		foreach ( var entry in data.Clips ?? Array.Empty<ClipData>() )
		{
			if ( entry is null || string.IsNullOrWhiteSpace( entry.Asset ) )
				continue;

			var asset = AssetSystem.FindByPath( entry.Asset );

			if ( asset is null )
				Log.Warning( $"[Effigy] clip '{entry.Name}' points at {entry.Asset}, which is not in the project - kept, but it will not export" );

			clips.Add( new EffigyAnimExport.ClipSource
			{
				Asset = asset,
				MissingPath = asset is null ? entry.Asset : null,
				Name = string.IsNullOrWhiteSpace( entry.Name ) ? Path.GetFileNameWithoutExtension( entry.Asset ) : entry.Name,
				Looping = entry.Looping,
			} );
		}

		return clips.Count;
	}
}
