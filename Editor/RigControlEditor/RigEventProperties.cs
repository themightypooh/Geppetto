using Editor;
using Marionette;
using Sandbox;
using System;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The AnimEvents tab - the event list up top ("Add Event" alongside it), the selected event's
/// fields below via a ControlSheet built off its SerializedObject (the same machinery the
/// Inspector uses for any [Property]-decorated type, so Name/StartFrame/EndFrame/Side/Model/
/// AttachBone/FollowBone/Offset all show up with the right widget per type for free).
/// </summary>
internal sealed class RigEventProperties : Widget
{
	private RigAnimDocument _asset;
	private RigEvent _selected;

	private ListView _list;
	private Widget _detail;

	public Action Edited { get; set; }
	public Action<RigEvent> Selected { get; set; }

	public RigEventProperties( Widget parent ) : base( parent )
	{
		Name = "AnimEvents";
		WindowTitle = "AnimEvents";
		SetWindowIcon( "bolt" );

		Layout = Layout.Column();

		Layout.Add( RigHelpBox.Create( this,
			"The list of frame-ranged events on this clip. Each one spawns a prop model, attaches " +
			"it to a bone for a span of frames, then removes it - the \"burger appears in hand_L " +
			"from frame 0 to 270\" idea.",
			new[]
			{
				RigHelpBox.S( "Fields",
					"Name - just a label, for your own reference.\n" +
					"Start / End Frame - the event is active only while the playhead is inside " +
					"this range.\n" +
					"Model - the prop to spawn (drag one in, or use the folder icon).\n" +
					"Attach Bone - the exact bone name to parent it to (e.g. hand_L).\n" +
					"Follow Bone - on: re-parents to the bone every frame, for something held. " +
					"off: snaps in place once at Start Frame and stays there, for something set " +
					"down.\n" +
					"Offset - position/rotation/scale relative to the bone.\n" +
					"Side - Client spawns locally only (cheap, cosmetic). Server spawns on the " +
					"host, for anything that should be networked or hittable by other players." ),

				RigHelpBox.S( "How to use it",
					"Add Event creates one; select it in the list to edit its fields below; Delete " +
					"Event removes whichever one is selected. Nothing here plays back " +
					"automatically in-game - RigEventPlayerComponent, on the model in your actual " +
					"scene, reads this list and does the spawning at runtime." ),

				RigHelpBox.S( "Morph Events (below)",
					"Same start/end-frame idea, but drives a morph target's value instead of " +
					"spawning a prop - for facial expressions or similar." ),
			} ) );

		var bar = Layout.AddRow();
		bar.Margin = 4;
		bar.Spacing = 4;
		bar.Add( new Editor.Label( "Events" ), 1 );
		bar.Add( new Button( "Add Event", "add" ) { Clicked = AddEvent } );

		_list = Layout.Add( new ListView( this ) { ItemSize = new Vector2( -1, 28 ) } );
		_list.ItemPaint = PaintRow;
		_list.ItemSelected = o => Select( o as RigEvent );
		_list.FixedHeight = 140;

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		_detail = new Widget( this ) { Layout = Layout.Column() };
		_detail.Layout.Margin = 8;
		_detail.Layout.Spacing = 4;
		scroll.Canvas = _detail;

		Rebuild();
	}

	public void SetAsset( RigAnimDocument asset )
	{
		_asset = asset;
		_selected = null;
		Rebuild();
	}

	public void Select( RigEvent evt )
	{
		_selected = evt;
		Selected?.Invoke( evt );
		RebuildDetail();
	}

	private void AddEvent()
	{
		if ( _asset is null )
			return;

		var evt = new RigEvent { Name = "event", StartFrame = 0, EndFrame = Math.Min( 10, Math.Max( 1, _asset.FrameCount - 1 ) ) };
		_asset.Events.Add( evt );

		Rebuild();
		Select( evt );
		Edited?.Invoke();
	}

	private void DeleteEvent( RigEvent evt )
	{
		if ( _asset is null || !_asset.Events.Remove( evt ) )
			return;

		if ( _selected == evt )
			Select( null );

		Rebuild();
		Edited?.Invoke();
	}

	public void Rebuild()
	{
		_list.SetItems( _asset?.Events.Cast<object>().ToList() ?? new() );
		RebuildDetail();
	}

	private void PaintRow( VirtualWidget vw )
	{
		if ( vw.Object is not RigEvent evt )
			return;

		var rect = vw.Rect;

		if ( Paint.HasSelected || Paint.HasMouseOver )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.Primary.WithAlpha( Paint.HasSelected ? 0.5f : 0.2f ) );
			Paint.DrawRect( rect, 3 );
		}

		Paint.ClearBrush();
		Paint.SetPen( Paint.HasSelected ? Color.White : Theme.Text );
		Paint.DrawText( new Rect( rect.Left + 8, rect.Top, rect.Width * 0.5f, rect.Height ), evt.Name, TextFlag.LeftCenter );

		Paint.SetPen( Theme.Text.WithAlpha( 0.6f ) );
		Paint.DrawText( new Rect( rect.Left, rect.Top, rect.Width - 12, rect.Height ),
			$"{evt.StartFrame}-{evt.EndFrame}  ·  {evt.Side}", TextFlag.RightCenter );
	}

	private void RebuildDetail()
	{
		_detail.Layout.Clear( true );

		if ( _asset is null )
		{
			_detail.Layout.Add( new Editor.Label( "Open a Rig Events asset to edit its events." ) { Enabled = false } );
			return;
		}

		if ( _selected is null )
		{
			_detail.Layout.Add( new Editor.Label( "Select an event above to edit it." ) { Enabled = false } );
		}
		else
		{
			var serialized = EditorTypeLibrary.GetSerializedObject( _selected );

			serialized.OnPropertyChanged += _ => Edited?.Invoke();

			var sheet = new ControlSheet();
			sheet.AddObject( serialized );

			_detail.Layout.Add( sheet );

			var deleteRow = _detail.Layout.AddRow();
			deleteRow.AddStretchCell();
			deleteRow.Add( new Button( "Delete Event", "delete" )
			{
				Clicked = () => DeleteEvent( _selected )
			} );
		}

		BuildMorphEvents();
	}

	/// <summary>Morph triggers (the "eat morph" row in the reference video) - simple frame-ranged
	/// entries independent of the bone events above, so they're always shown rather than gated on
	/// a selection.</summary>
	private void BuildMorphEvents()
	{
		_detail.Layout.AddSpacingCell( 12 );

		var header = _detail.Layout.AddRow();
		header.Add( new Editor.Label( "Morph Events" ) { Enabled = false }, 1 );
		header.Add( new Button( "add", "add" ) { Clicked = AddMorphEvent } );

		foreach ( var morph in _asset.MorphEvents )
			_detail.Layout.Add( MorphRow( morph ) );
	}

	private Widget MorphRow( MorphEvent morph )
	{
		var row = new Widget( _detail ) { Layout = Layout.Column() };
		row.Layout.Margin = 4;

		var title = row.Layout.AddRow();
		title.Add( new Editor.Label( string.IsNullOrWhiteSpace( morph.Name ) ? "morph" : morph.Name ), 1 );
		title.Add( new Button( "delete", "delete" )
		{
			Clicked = () =>
			{
				_asset.MorphEvents.Remove( morph );
				Rebuild();
				Edited?.Invoke();
			}
		} );

		var sheet = new ControlSheet();
		var serialized = EditorTypeLibrary.GetSerializedObject( morph );
		serialized.OnPropertyChanged += _ => Edited?.Invoke();
		sheet.AddObject( serialized );
		row.Layout.Add( sheet );

		return row;
	}

	private void AddMorphEvent()
	{
		if ( _asset is null )
			return;

		_asset.MorphEvents.Add( new MorphEvent { Name = "morph", StartFrame = 0, EndFrame = Math.Min( 10, Math.Max( 1, _asset.FrameCount - 1 ) ) } );

		Rebuild();
		Edited?.Invoke();
	}
}
