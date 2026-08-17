using Editor;
using Marionette;
using Sandbox;
using System;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The bone-name column down the left of the timeline.
///
/// It used to be painted inside the lanes canvas at the current scroll offset, cancelling the
/// horizontal scroll so it appeared to hold still. That worked, but it meant the ScrollArea - and
/// therefore its scrollbar - began at the far left of the window while every ruler tick was offset
/// by the gutter, so the scrollbar visibly ran past where everything else started.
///
/// It's a real widget now, sitting beside the ScrollArea rather than inside it, so the scroll area
/// starts where the tracks start and the scrollbar lines up for free.
///
/// The objection to doing this earlier was vertical scroll: a second scrolling widget would have
/// to be kept in step with the first, and two scrolls that must agree is a worse bug than one
/// offset. That turns out not to apply - this widget doesn't scroll. It reads the lanes' vertical
/// offset when it paints and draws its rows shifted by it. One direction, no state, nothing to
/// fall out of sync.
/// </summary>
internal sealed class RigBoneColumn : Widget
{
	public RigAnimDocument Anim { get; set; }

	/// <summary>Which bone the viewport has selected, so the row can be marked.</summary>
	public string SelectedBone { get; set; }

	/// <summary>How far the tracks beside this have scrolled vertically. Pulled at paint time
	/// rather than pushed on change - the ScrollArea raises nothing we can hook, and reading it is
	/// cheap.</summary>
	public Func<float> ScrollY { get; set; }

	public Action<string> BoneSelected { get; set; }
	public Action<BoneTrack> ContextMenuRequested { get; set; }

	public RigBoneColumn( Widget parent ) : base( parent )
	{
		FixedWidth = RigTimelineLayout.Gutter;
	}

	private float Offset => ScrollY?.Invoke() ?? 0f;

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Theme.WidgetBackground );
		Paint.DrawRect( LocalRect );

		var tracks = Anim?.BoneTracks;

		if ( tracks is not null )
		{
			var scrollY = Offset;

			for ( var i = 0; i < tracks.Count; i++ )
			{
				var top = i * RigTimelineLayout.RowHeight - scrollY;

				// Rows scrolled out of view still get walked, but not drawn - the count here is
				// bone tracks, not something unbounded.
				if ( top + RigTimelineLayout.RowHeight < 0f || top > Height )
					continue;

				PaintRow( tracks[i], top );
			}
		}

		// The edge the tracks begin at. Drawn last so no row background covers it.
		Paint.SetPen( Theme.TextControl.WithAlpha( 0.2f ) );
		Paint.DrawLine( new Vector2( Width - 1f, 0f ), new Vector2( Width - 1f, Height ) );
	}

	private void PaintRow( BoneTrack track, float top )
	{
		var isSelected = track.BoneName == SelectedBone;
		var row = new Rect( 0f, top, Width, RigTimelineLayout.RowHeight );

		Paint.ClearPen();

		if ( isSelected )
		{
			Paint.SetBrush( Theme.Yellow.WithAlpha( 0.12f ) );
			Paint.DrawRect( row );

			// A bar hard against the left edge, so the selected bone is findable without reading
			// any of the names.
			Paint.SetBrush( Theme.Yellow );
			Paint.DrawRect( new Rect( 0f, top, 2f, RigTimelineLayout.RowHeight ) );
		}

		Paint.SetDefaultFont( 7, isSelected ? 600 : 400 );
		Paint.SetPen( isSelected ? Theme.Yellow : Theme.TextControl.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( 8f, top, Width - 16f, RigTimelineLayout.RowHeight ),
			track.BoneName, TextFlag.LeftCenter );
	}

	private BoneTrack TrackAt( Vector2 position )
	{
		if ( Anim is null )
			return null;

		var index = (int)((position.y + Offset) / RigTimelineLayout.RowHeight);

		return index >= 0 && index < Anim.BoneTracks.Count ? Anim.BoneTracks[index] : null;
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton || TrackAt( e.LocalPosition ) is not { } track )
			return;

		BoneSelected?.Invoke( track.BoneName );
		e.Accepted = true;
	}

	protected override void OnContextMenu( ContextMenuEvent e )
	{
		if ( TrackAt( e.LocalPosition ) is not { } track )
			return;

		ContextMenuRequested?.Invoke( track );
	}
}
