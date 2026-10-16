using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The shape keys (morphs, blend shapes) on the selected body, as a list: + starts a key and puts
/// you in Sculpt with it recording, Finish ends it, − removes it, a slider previews it, and Edit
/// re-opens a finished key to sculpt more into it.
///
/// UNDER THE HOOD IT IS STILL THE HISTORY. A key here is a Shape Key feature, the features that
/// shape it, and an End Shape Key, so scripts, saved files and the feature tree keep working; this
/// panel only reads that run of features and writes to it. The window owns all of that (see
/// EffigyWindow.ShapeKeys.cs); the panel draws what it is handed and reports clicks.
/// </summary>
internal sealed class EffigyShapeKeysPanel : Widget
{
	/// <summary>One key. StartId is the Shape Key feature's id, null for a key that came in with the
	/// mesh (an import) rather than from this document's history — those can be previewed only.
	/// Editing: a finished key re-opened with Edit, so new sculpting goes into it.</summary>
	public readonly record struct KeyRow( string Name, string StartId, bool Recording, float Weight, bool Editing = false );

	public Action AddClicked { get; set; }
	public Action FinishClicked { get; set; }
	public Action<string> RemoveClicked { get; set; }
	public Action<string> EditClicked { get; set; }
	public Action<string, string> RenameCommitted { get; set; }
	public Action<string, float> WeightChanged { get; set; }

	private readonly Editor.Label _title;
	private readonly Editor.Label _status;
	private readonly Button _add;
	private readonly Button _finish;
	private readonly Widget _list;

	public EffigyShapeKeysPanel( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 6;

		var header = Layout.AddRow();
		_title = new Editor.Label( "Shape Keys" ) { Color = Theme.TextControl };
		header.Add( _title );
		header.AddStretchCell();

		_add = new Button( "+ New key", this )
		{
			ToolTip = "Start a shape key on the selected body and sculpt it. What you sculpt, edit or detail "
				+ "until Finish becomes the key, and the body goes back to how it was. Animate it in Marionette",
		};
		_add.Clicked = () => AddClicked?.Invoke();
		header.Add( _add );

		_finish = new Button( "Finish key", this )
		{
			ToolTip = "End the key being recorded or edited: everything sculpted into it becomes the key, and the body goes back",
			Visible = false,
		};
		_finish.Clicked = () => FinishClicked?.Invoke();
		header.Add( _finish );

		_status = new Editor.Label( "" ) { WordWrap = true, Color = Theme.TextControl.WithAlpha( 0.7f ) };
		Layout.Add( _status );

		_list = new Widget( this ) { Layout = Layout.Column() };
		_list.Layout.Spacing = 4;
		Layout.Add( _list );
		Layout.AddStretchCell();
	}

	/// <param name="body">The body the keys belong to, or null when none is selected.</param>
	/// <param name="status">A line under the header: what to do next, or why the list is empty.</param>
	public void Show( string body, string status, IReadOnlyList<KeyRow> rows, bool canAdd )
	{
		if ( !_list.IsValid() )
			return;

		// Rebuilt on every studio change: keep a name box that is being typed in.
		if ( _list.Children.OfType<Widget>().SelectMany( r => r.Children ).OfType<LineEdit>().Any( e => e.IsFocused ) )
			return;

		var recording = rows.Any( r => r.Recording || r.Editing );

		_title.Text = body is null ? "Shape Keys" : $"Shape Keys · {body}";
		_status.Text = status ?? "";
		_status.Visible = !string.IsNullOrEmpty( status );
		_add.Visible = !recording;
		_add.Enabled = canAdd;
		_finish.Visible = recording;

		_list.Layout.Clear( true );

		foreach ( var key in rows )
			_list.Layout.Add( BuildRow( key ) );
	}

	private Widget BuildRow( KeyRow key )
	{
		var row = new Widget( _list ) { Layout = Layout.Row() };
		row.Layout.Spacing = 4;

		var name = new LineEdit( key.Name, row )
		{
			FixedWidth = 120f,
			ReadOnly = key.StartId is null,
			ToolTip = key.StartId is null
				? "This key came with the mesh, so it can be previewed but not renamed or removed here"
				: "The key's name, which Marionette's morph curves and the game use. No spaces",
		};

		if ( key.StartId is { } id )
			name.EditingFinished += () =>
			{
				var text = name.Text?.Trim() ?? "";
				if ( text.Length > 0 && text != key.Name )
					RenameCommitted?.Invoke( id, text );
			};

		row.Layout.Add( name );

		if ( key.Recording || key.Editing )
		{
			row.Layout.Add( new Editor.Label( key.Editing ? "editing…" : "recording…" ) { Color = Theme.Primary } );
			row.Layout.AddStretchCell();
		}
		else
		{
			var readout = new Editor.Label( $"{key.Weight:P0}" ) { FixedWidth = 36f, Color = Theme.TextControl.WithAlpha( 0.75f ) };
			var slider = new FloatSlider( row )
			{
				Minimum = 0f,
				Maximum = 1f,
				Step = 0.01f,
				Value = key.Weight,
				ToolTip = "Preview the key at this strength. Only the view changes; the model exports with the key at 0",
			};
			var keyName = key.Name;
			slider.OnValueEdited = () =>
			{
				readout.Text = $"{slider.Value:P0}";
				WeightChanged?.Invoke( keyName, slider.Value );
			};

			row.Layout.Add( slider, 1 );
			row.Layout.Add( readout );
		}

		if ( key.StartId is { } editId && !key.Recording && !key.Editing )
		{
			row.Layout.Add( new IconButton( "edit", () => EditClicked?.Invoke( editId ) )
			{
				ToolTip = "Sculpt more into this key. The model shows the key fully on, and whatever you sculpt, edit or detail "
					+ "is added to it. Press Finish key when you are done",
				IconSize = 16,
				Background = Color.Transparent,
			} );
		}

		if ( key.StartId is { } startId )
		{
			row.Layout.Add( new IconButton( "remove", () => RemoveClicked?.Invoke( startId ) )
			{
				ToolTip = key.Recording
					? "Throw this key away, with everything sculpted into it so far"
					: "Remove this key, with the features that shaped it. Ctrl+Z brings it back",
				IconSize = 16,
				Background = Color.Transparent,
			} );
		}

		return row;
	}
}
