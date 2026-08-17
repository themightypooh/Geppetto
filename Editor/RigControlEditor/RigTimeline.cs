using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// One row per keyed bone, a diamond per keyframe (drag to move, Delete to remove, click to jump
/// the playhead there) - matching the reference video exactly rather than the frame-range event
/// bars this used to draw (AnimEvents live in their own tab now, not on this timeline). Frame-to-
/// pixel geometry and drag semantics are the same shape as AnimGraph's own ClipEventLayout/Lanes/
/// Ruler (editor/AnimGraph/Code/Editors), re-keyed off BoneTrack/BoneKeyframe instead.
/// </summary>
internal sealed class RigTimeline : Widget
{
	private readonly RigTimelineLanes _lanes;
	private readonly RigTimelineRuler _ruler;
	private readonly RigTimelineRuler _frameRuler;
	private readonly ScrollArea _scroll;
	private readonly Editor.Label _timeLabel;
	private readonly Button _playButton;
	private readonly Button _loopButton;

	private RigAnimDocument _anim;
	private RealTimeSince _lastStep;
	private bool _playing;
	private bool _looping = true;

	public Action<float> Scrubbed { get; set; }
	public Action Edited { get; set; }

	/// <summary>Raised by the transport's key button - the window owns what "key the selected
	/// bone" means, since it's the thing holding both the document and the viewport.</summary>
	public Action KeyRequested { get; set; }

	/// <summary>Raised when a bone's row is clicked in the timeline gutter, so viewport selection
	/// and timeline selection stay the same thing. Forwarded to the lanes, which own the hit
	/// testing and the painting.</summary>
	public Action<string> BoneRowSelected
	{
		get => _lanes.BoneRowSelected;
		set => _lanes.BoneRowSelected = value;
	}

	public string SelectedBone
	{
		get => _lanes.SelectedBone;
		set => _lanes.SelectedBone = value;
	}

	public float Playhead
	{
		get => _lanes.Playhead;
		set
		{
			value = value.Clamp( 0f, LastFrame );

			if ( _lanes.Playhead == value )
				return;

			_lanes.Playhead = value;
			_ruler.Playhead = value;
			_frameRuler.Playhead = value;
			_frameRuler.Update();

			_lanes.Update();
			_ruler.Update();
			UpdateTimeLabel();
		}
	}

	/// <summary>
	/// Thirty seconds at 30fps - the shortest the timeline is ever allowed to be.
	///
	/// THE TIMELINE IS A CANVAS, NOT A MEASUREMENT OF THE CLIP. It used to end exactly at the
	/// document's FrameCount, so a clip authored at the old 30-frame default gave you one second
	/// of timeline and no way to work past it without going and finding a number field first.
	/// Being told "raise Frame Count" is a worse answer than just having room.
	/// </summary>
	private const int MinimumTimelineFrames = 900;

	/// <summary>The last frame the timeline goes to - the clip's own length, or thirty seconds,
	/// whichever is longer. Everything that positions, scrolls, zooms or clamps reads this.</summary>
	private float LastFrame => MathF.Max( MathF.Max( _anim?.FrameCount ?? 1, MinimumTimelineFrames ) - 1, 1f );

	/// <summary>
	/// Where playback stops - the last keyframe in the clip, not the end of the timeline.
	///
	/// THE CANVAS IS NOT THE ANIMATION. Since the timeline is always at least thirty seconds, a
	/// two second clip played on it would run twenty-eight seconds of nothing before stopping, and
	/// loop with a twenty-eight second gap. Playing to the last key means what you watch is what
	/// you made, at any canvas length.
	///
	/// Falls back to the whole timeline when there are no keys at all, so pressing play on an
	/// empty clip still does something rather than sitting on frame zero looking broken.
	/// </summary>
	private float PlaybackLastFrame
	{
		get
		{
			var last = 0f;

			if ( _anim?.BoneTracks is { } tracks )
			{
				foreach ( var track in tracks )
				{
					foreach ( var key in track.Keyframes )
						last = MathF.Max( last, key.Frame );
				}
			}

			return last > 0f ? last : LastFrame;
		}
	}
	private float FrameRate => _anim?.AnimationSpeed > 0 ? _anim.AnimationSpeed : 30f;

	public RigTimeline( Widget parent ) : base( parent )
	{
		Name = "Timeline";
		WindowTitle = "Timeline";
		SetWindowIcon( "view_timeline" );

		Layout = Layout.Column();

		_lanes = new RigTimelineLanes( this )
		{
			Scrubbed = frame => ScrubTo( frame ),
			Edited = () => Edited?.Invoke(),
		};

		_ruler = new RigTimelineRuler( this, _lanes ) { Scrubbed = ScrubTo, ShowTimecode = true };
		Layout.Add( _ruler );

		// THE REAL SCROLLBAR. On, not Off - the lanes canvas is now as wide as the whole clip
		// instead of the viewport, so the ScrollArea has something genuine to scroll and gives us
		// its own bar with a proportional handle. That handle doubles as a readout of how much
		// clip there is either side of what you can see, which a slider can never show.
		_scroll = Layout.Add( new ScrollArea( this ), 1 );
		_scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		_scroll.HorizontalScrollbarMode = ScrollbarMode.Auto;
		_scroll.Canvas = _lanes;

		// Frame numbers below the tracks, timecode above - the tracks sit between the two units
		// so neither has to be converted in your head.
		_frameRuler = new RigTimelineRuler( this, _lanes ) { Scrubbed = ScrubTo, ShowTimecode = false };
		Layout.Add( _frameRuler );

		// ONE view range, three widgets. Each holds the same object rather than its own copy, so
		// zooming anywhere moves all of them together - a ruler that scrolls independently of the
		// lanes it labels is worse than no ruler at all.
		_ruler.View = _lanes.View;
		_frameRuler.View = _lanes.View;

		_lanes.ViewChanged = RefreshView;
		_ruler.ViewChanged = RefreshView;
		_frameRuler.ViewChanged = RefreshView;

		// All three ask; one place answers. Zoom and shift-wheel from any of them move the same
		// scrollbar, which is the only thing that decides where the view actually sits.
		_lanes.ScrollRequested = ScrollTo;
		_ruler.ScrollRequested = ScrollTo;
		_frameRuler.ScrollRequested = ScrollTo;

		_lanes.ScrollOffset = CurrentScrollX;

		// EVERY transport control lives here, with the timeline it drives - not split between here
		// and the window toolbar. Scrubbing, stepping and playing are all things you do while
		// looking at the timeline; making you travel to the top of the window for half of them and
		// back down for the rest is the kind of split that never stops being mildly annoying.
		var transport = Layout.AddRow();
		transport.Margin = new Sandbox.UI.Margin( 8, 4 );
		transport.Spacing = 4;

		// The key button leads the transport rather than sitting up in the window toolbar: making a
		// keyframe is a thing you do AT a frame, so it belongs next to the controls that choose
		// which frame you're on.
		//
		// The icon is a diamond because that's what a keyframe looks like in every animation tool
		// there is. It was previously "fiber_manual_record", which renders as an anonymous filled
		// circle and reads as "record" at best.
		transport.Add( new RigStatusBar.HintButton( "diamond",
			"Key the selected bone at the playhead, using its current pose (K)", () => KeyRequested?.Invoke() ) );

		transport.AddSpacingCell( 8f );

		transport.Add( new RigStatusBar.HintButton( "skip_previous", "Jump to the first frame", () => ScrubTo( 0f ) ) );
		transport.Add( new RigStatusBar.HintButton( "navigate_before", "Step back one frame", () => StepFrame( -1 ) ) );
		_playButton = transport.Add( new RigStatusBar.HintButton( "play_arrow", "Play or pause the clip", TogglePlay ) );
		transport.Add( new RigStatusBar.HintButton( "navigate_next", "Step forward one frame", () => StepFrame( 1 ) ) );
		transport.Add( new RigStatusBar.HintButton( "skip_next", "Jump to the last frame", () => ScrubTo( LastFrame ) ) );

		_loopButton = transport.Add( new RigStatusBar.HintButton( "loop", "Loop playback when it reaches the end", ToggleLoop ) );

		transport.AddSpacingCell( 8f );
		_timeLabel = transport.Add( new Editor.Label( "0:00 / 0:00" ) );

		transport.AddStretchCell();

		// Preview speed, not clip speed - see PlaybackSpeed.
		_speedButton = transport.Add( new Button( "x1", "speed" ) { Clicked = OpenSpeedMenu, ToolTip = "Preview speed - does not change the clip" } );
		_fpsButton = transport.Add( new Button( "30 FPS", "schedule" ) { Clicked = OpenFpsMenu, ToolTip = "Frame rate of the clip itself - this DOES change playback in game" } );

		UpdateLoopButton();
	}

	private Button _speedButton;
	private Button _fpsButton;

	/// <summary>How fast playback runs relative to the clip's own frame rate.
	///
	/// A REVIEW CONTROL, NOT A CLIP PROPERTY. It never touches the document - a clip previewed at
	/// 0.25x still plays at full speed in game. Timing mistakes that are invisible at full speed
	/// are obvious at a quarter, which is most of why this exists.</summary>
	public float PlaybackSpeed { get; set; } = 1f;

	private void StepFrame( int delta ) => ScrubTo( (Playhead + delta).Clamp( 0f, LastFrame ) );

	private void OpenSpeedMenu()
	{
		var menu = new Menu( this );

		foreach ( var speed in new[] { 0.1f, 0.25f, 0.5f, 1f, 2f } )
		{
			var option = menu.AddOption( $"x{speed:0.##}", null, () =>
			{
				PlaybackSpeed = speed;
				_speedButton.Text = $"x{speed:0.##}";
			} );

			option.Checkable = true;
			option.Checked = PlaybackSpeed.AlmostEqual( speed );
		}

		menu.OpenAtCursor();
	}

	private void OpenFpsMenu()
	{
		if ( _anim is null )
			return;

		var menu = new Menu( this );

		foreach ( var fps in new[] { 10, 12, 24, 30, 60 } )
		{
			var option = menu.AddOption( $"{fps} FPS", null, () =>
			{
				_anim.AnimationSpeed = fps;
				Refresh();
				Edited?.Invoke();
			} );

			option.Checkable = true;
			option.Checked = _anim.AnimationSpeed == fps;
		}

		menu.OpenAtCursor();
	}

	public void SetAsset( RigAnimDocument anim )
	{
		_anim = anim;
		_lanes.Anim = anim;
		Playhead = 0f;

		// Back to the start of the clip at the standard zoom. Carrying the previous clip's scroll
		// position over would open a short clip already scrolled past its own end.
		_lanes.View.PixelsPerFrame = RigTimelineLayout.DefaultPixelsPerFrame;
		_lanes.View.ScrollX = 0f;

		if ( _scroll?.HorizontalScrollbar is { } bar )
			bar.Value = 0;

		Refresh();
	}

	/// <summary>Repaint everything that draws against the view window, without touching the
	/// document - zooming changes what you see, never what's stored.</summary>
	private void RefreshView()
	{
		_lanes.Update();
		_ruler.Update();
		_frameRuler.Update();

		SyncScrollBar();
	}

	/// <summary>
	/// Points the scrollbar at the current view window.
	///
	/// Its range is how far the window can travel - clip length minus what's visible - so it
	/// shrinks to nothing as you zoom out and the handle covers the whole bar when the clip
	/// already fits. Disabled rather than hidden in that case: a control that vanishes reads as
	/// broken, and one that's greyed out says "there is nothing to scroll to", which is the
	/// truth.
	///
	/// Driven from RefreshView, so wheel-panning moves the bar and dragging the bar moves the
	/// view. One view range behind both, same as the rulers.
	/// </summary>
	/// <summary>Moves the ScrollArea, clamped to what it can actually reach. Sizing the canvas
	/// first matters: a zoom-out that shrinks the canvas has to shrink the scroll range before the
	/// new offset is clamped against it, or the view sticks past the end of a clip that just got
	/// narrower.</summary>
	private void ScrollTo( float x )
	{
		SyncScrollBar();

		if ( _scroll?.HorizontalScrollbar is not { } bar )
			return;

		bar.Value = (int)MathF.Max( x, 0f );

		_lanes.View.ScrollX = bar.Value;

		RefreshView();
	}

	/// <summary>The one place that knows where the horizontal scroll actually is. Everything else
	/// reads it from here rather than reaching into the ScrollArea, so there is a single answer.</summary>
	private float CurrentScrollX() =>
		_scroll?.HorizontalScrollbar is { } bar ? bar.Value : 0f;

	private void SyncScrollBar()
	{
		if ( _scroll is null || !_lanes.IsValid() )
			return;

		var layout = new RigTimelineLayout( 0f, _anim?.FrameCount ?? 1, 0, 0f, _lanes.View.PixelsPerFrame );

		// SIZING THE CANVAS IS WHAT CREATES THE SCROLLBAR. A ScrollArea scrolls because its canvas
		// is bigger than its viewport and for no other reason, so the whole feature comes down to
		// this one assignment - the bar, its handle size and its range all follow from it.
		_lanes.MinimumWidth = layout.CanvasWidth;

		// Published for the rulers, which live outside the ScrollArea and are therefore never
		// moved by it. Read once here rather than by each of them, so all three widgets are
		// guaranteed to be drawing the same frame of scroll.
		_lanes.View.ScrollX = CurrentScrollX();
	}

	public void Refresh()
	{
		_lanes.MinimumHeight = RigTimelineLayout.HeightFor( _anim?.BoneTracks.Count ?? 0 );
		_lanes.Update();
		_ruler.FrameCount = (int)LastFrame + 1;
		_ruler.Fps = _anim?.AnimationSpeed ?? 30;
		_ruler.Update();

		_frameRuler.FrameCount = _ruler.FrameCount;
		_frameRuler.Fps = _ruler.Fps;
		_frameRuler.Update();

		// Clip length can change under us (the Frame Count field), which changes how far there is
		// to scroll.
		SyncScrollBar();

		if ( _fpsButton is not null )
			_fpsButton.Text = $"{_ruler.Fps} FPS";

		UpdateTimeLabel();
	}

	private void ScrubTo( float frame )
	{
		Playhead = frame;
		Scrubbed?.Invoke( frame );
	}

	public bool IsPlaying => _playing;
	public bool Looping => _looping;
	public bool HasSelectedKeyframe => _lanes.HasSelectedKeyframe;

	public void DeleteSelectedKeyframe() => _lanes.DeleteSelectedKeyframe();

	public void SetPlaying( bool playing )
	{
		_playing = playing;
		_playButton.Icon = _playing ? "pause" : "play_arrow";
		_lastStep = 0;
	}

	public void Stop()
	{
		SetPlaying( false );
		Playhead = 0f;
	}

	public void SetLooping( bool looping )
	{
		_looping = looping;
		UpdateLoopButton();
	}

	private void TogglePlay() => SetPlaying( !_playing );

	private void ToggleLoop() => SetLooping( !_looping );

	private void UpdateLoopButton()
	{
		_loopButton.Icon = _looping ? "loop" : "trending_flat";
		_loopButton.ToolTip = _looping ? "Looping - click to play once" : "Plays once - click to loop";
	}

	// [EditorEvent.Frame] appears to keep calling Frame() on a closed/replaced RigTimeline
	// instance - "Error calling event 'tool.frame'" fired repeatedly with a NullReferenceException
	// underneath, consistent with touching a native widget (_playButton) whose backing Qt object
	// is already gone. IsValid guards the real cause if this build exposes it; the try/catch is
	// the unconditional backstop either way, since a dead timer tick should never spam the
	// console or risk cascading into other systems the way this one clearly was.
	[EditorEvent.Frame]
	public void Frame()
	{
		if ( !IsValid )
			return;

		try
		{
			if ( !_playing )
				return;

			var elapsed = (float)_lastStep;
			if ( elapsed < 1f / (FrameRate * MathF.Max( PlaybackSpeed, 0.01f )) )
				return;

			_lastStep = 0;

			var next = Playhead + 1f;

			if ( next > PlaybackLastFrame )
			{
				if ( !_looping )
				{
					_playing = false;
					_playButton.Icon = "play_arrow";
					return;
				}

				next = 0f;
			}

			ScrubTo( next );
		}
		catch ( Exception )
		{
			_playing = false;
		}
	}

	private void UpdateTimeLabel()
	{
		var current = TimeSpan.FromSeconds( Playhead / FrameRate );
		var total = TimeSpan.FromSeconds( LastFrame / FrameRate );
		_timeLabel.Text = $"{(int)current.TotalMinutes}:{current.Seconds:00} / {(int)total.TotalMinutes}:{total.Seconds:00}";
	}
}

/// <summary>Frame-to-pixel geometry, shared by the ruler and the lanes so they can't drift apart.</summary>
internal readonly struct RigTimelineLayout
{
	public const float RowHeight = 22f;
	public const float RulerHeight = 22f;
	public const float Gutter = 130f;

	/// <summary>Where the vertical divider is drawn. Its own number rather than LaneArea.Left,
	/// because this is only a line - moving it must not move the lane area, the frame mapping or
	/// anything that hit-tests against them.</summary>
	public const float DividerX = 100f;
	public const float DiamondSize = 9f;

	private const float RightPadding = 10f;

	/// <summary>
	/// Pixels per frame at rest - a frame is 8px wide until you zoom.
	///
	/// THE TIMELINE IS NOW A WIDE CANVAS THAT SCROLLS, not a window that remaps the clip onto a
	/// fixed width. The old model made a frame's width depend on the clip's length, so a 900 frame
	/// clip crushed every frame to a fraction of a pixel and the keys became an unclickable smear -
	/// making a clip longer actively made it harder to work on.
	///
	/// A constant pixels-per-frame means a frame is the same size whether the clip is one second
	/// or thirty, and the ScrollArea's own horizontal scrollbar handles moving along it - a real
	/// scrollbar with a proportional handle, which is also a readout of how much clip there is.
	/// </summary>
	public const float DefaultPixelsPerFrame = 8f;

	/// <summary>Zoom limits. The floor keeps a 900 frame clip inside a sane canvas width; the
	/// ceiling stops a single frame filling the screen.</summary>
	public const float MinPixelsPerFrame = 0.4f;

	public const float MaxPixelsPerFrame = 80f;

	private readonly float _width;
	private readonly int _rows;

	public float LastFrame { get; }

	/// <summary>Width of one frame. Zoom changes this and nothing else.</summary>
	public float PixelsPerFrame { get; }

	/// <summary>
	/// How far the ScrollArea has scrolled, and the thing that makes one FrameToX serve two
	/// coordinate spaces.
	///
	/// The lanes widget IS the ScrollArea's canvas, so its local coordinates are already scrolled -
	/// it builds a layout with ScrollX 0 and gets canvas space. The rulers sit outside the
	/// ScrollArea and don't move, so they build a layout with the real offset and get viewport
	/// space. Same maths, same call, both correct.
	/// </summary>
	public float ScrollX { get; }

	public RigTimelineLayout( float width, int frameCount, int rows, float scrollX = 0f, float pixelsPerFrame = 0f )
	{
		_width = width;
		_rows = rows;
		LastFrame = MathF.Max( frameCount - 1, 1f );

		PixelsPerFrame = (pixelsPerFrame > 0f ? pixelsPerFrame : DefaultPixelsPerFrame)
			.Clamp( MinPixelsPerFrame, MaxPixelsPerFrame );

		ScrollX = scrollX;
	}

	/// <summary>How wide the lanes widget has to be for the whole clip to exist inside the
	/// ScrollArea. This is what gives the scrollbar something to scroll.</summary>
	public float CanvasWidth => Gutter + (LastFrame + 1f) * PixelsPerFrame + RightPadding;

	public Rect LaneArea => new( Gutter, 0f, MathF.Max( _width - Gutter - RightPadding, 1f ), _rows * RowHeight );

	public float FrameToX( float frame ) => Gutter + frame * PixelsPerFrame - ScrollX;

	public float XToFrame( float x ) => ((x + ScrollX - Gutter) / PixelsPerFrame).Clamp( 0f, LastFrame );

	public Rect RowRect( int index ) => new( 0f, index * RowHeight, MathF.Max( _width, Gutter ), RowHeight );

	public Rect DiamondRect( int index, float frame )
	{
		var row = RowRect( index );
		var x = FrameToX( frame );
		var half = DiamondSize * 0.5f;

		return new Rect( x - half, row.Center.y - half, DiamondSize, DiamondSize );
	}

	public int HitRow( Vector2 position )
	{
		var index = (int)(position.y / RowHeight);
		return index >= 0 && index < _rows ? index : -1;
	}

	/// <summary>Ruler divisions spaced so their LABELS don't collide.
	///
	/// minLabelWidth is the width of the widest label the caller will draw, and it matters a lot:
	/// a timecode like "00:00.933" is roughly twice the width of a frame number, so a single
	/// hardcoded spacing crams one ruler into an unreadable smear at small window sizes. Callers
	/// pass what they actually need.</summary>
	public IEnumerable<float> RulerFrames( float minLabelWidth = 38f )
	{
		var step = RulerSteps[^1];

		foreach ( var candidate in RulerSteps )
		{
			if ( candidate * PixelsPerFrame < minLabelWidth )
				continue;

			step = candidate;
			break;
		}

		// Only the frames actually on screen. Derived from the scroll offset and this widget's
		// width rather than from a view range, so it works the same for the rulers (which scroll
		// by offset) and the canvas (which is scrolled for them, and passes ScrollX 0 with its
		// full width - yielding every division across the whole canvas, which is what it wants).
		var firstVisible = (ScrollX - Gutter) / PixelsPerFrame;
		var lastVisible = (ScrollX + _width - Gutter) / PixelsPerFrame;

		var first = MathF.Max( MathF.Floor( firstVisible / step ) * step, 0f );
		var last = MathF.Min( lastVisible, LastFrame );

		for ( var frame = first; frame <= last; frame += step )
			yield return frame;
	}

	private static readonly float[] RulerSteps = [1f, 2f, 5f, 10f, 25f, 50f, 100f, 250f, 500f, 1000f];

	public static float HeightFor( int rows ) => rows * RowHeight;
}

internal sealed class RigTimelineLanes : Widget
{
	public RigAnimDocument Anim { get; set; }
	public float Playhead { get; set; }

	public Action<float> Scrubbed { get; set; }
	public Action Edited { get; set; }

	private bool _grabbed;
	private bool _dragged;
	private BoneTrack _grabTrack;
	private BoneKeyframe _grabKey;
	private float _grabOffset;
	private (BoneTrack Track, BoneKeyframe Key)? _hover;
	private (BoneTrack Track, BoneKeyframe Key)? _selected;

	public RigTimelineLanes( Widget parent ) : base( parent )
	{
		FocusMode = FocusMode.Click;
		MouseTracking = true;
	}

	/// <summary>
	/// The visible frame window, shared by the lanes and both rulers so they can't disagree about
	/// what's on screen. Held here rather than in each widget because a ruler that scrolls
	/// independently of the lanes it labels is worse than no ruler.
	/// </summary>
	/// <summary>
	/// The shared zoom level. Scroll position is NOT here any more - it belongs to the ScrollArea,
	/// which owns the scrollbar and is the only thing that should be deciding where the view sits.
	///
	/// Still a shared object rather than a value per widget, for the original reason: the rulers
	/// and the lanes must never disagree about scale, and the cheapest guarantee of that is their
	/// holding the same instance.
	/// </summary>
	public sealed class ViewRange
	{
		public float PixelsPerFrame = RigTimelineLayout.DefaultPixelsPerFrame;

		/// <summary>Where the horizontal scroll currently is, in canvas pixels. Written by the
		/// timeline from the ScrollArea each frame so the rulers can offset by it.</summary>
		public float ScrollX;

		/// <summary>
		/// Zoom about a fixed frame - the one under the cursor - and report how far the scroll has
		/// to move to keep that frame under the pointer.
		///
		/// Returns the new scroll offset rather than applying it, because the scroll belongs to
		/// the ScrollArea and this type has no business reaching into a widget.
		/// </summary>
		public float ZoomAt( float anchorFrame, float anchorViewportX, float factor )
		{
			PixelsPerFrame = (PixelsPerFrame * factor)
				.Clamp( RigTimelineLayout.MinPixelsPerFrame, RigTimelineLayout.MaxPixelsPerFrame );

			// The anchor frame's new position in canvas space, minus where on screen it has to
			// stay, IS the scroll offset. anchorViewportX must be measured from the ScrollArea's
			// left edge - the lanes widget has to subtract the current scroll first, since its own
			// coordinates are canvas ones.
			return MathF.Max( RigTimelineLayout.Gutter + anchorFrame * PixelsPerFrame - anchorViewportX, 0f );
		}
	}

	public ViewRange View { get; set; } = new();

	/// <summary>Canvas space: this widget IS the ScrollArea's canvas, so it is already translated
	/// and must not subtract the offset again. Its own width is the full canvas width.</summary>
	private RigTimelineLayout Geometry => new( Width, (Anim?.FrameCount ?? 1), Anim?.BoneTracks.Count ?? 0,
		0f, View.PixelsPerFrame );

	/// <summary>
	/// The divider and the gutter's right edge, IN CANVAS SPACE.
	///
	/// Both are pinned to the screen, so in this widget's coordinates they slide right as you
	/// scroll. Everything that asks "is this behind the name column" has to ask against these
	/// rather than the raw constants - the constants are screen positions, and this widget does
	/// not draw in screen positions.
	/// </summary>
	private float DividerCanvasX => View.ScrollX + RigTimelineLayout.DividerX;

	private float GutterCanvasX => View.ScrollX + RigTimelineLayout.Gutter;

	/// <summary>
	/// Ctrl+wheel zooms about the cursor, Shift+wheel pans. Plain wheel is deliberately left to
	/// the ScrollArea so the bone list still scrolls vertically.
	///
	/// Modifiers come off the event (HasCtrl/HasShift) rather than from a global key query, and
	/// the anchor comes from the last mouse position rather than the wheel event, because
	/// WheelEvent carries no position - this is the same shape MovieMaker's own timeline uses.
	/// </summary>
	protected override void OnMouseWheel( WheelEvent e )
	{
		var layout = Geometry;

		if ( e.HasCtrl )
		{
			// 1.15 per notch: fine enough to land on a zoom deliberately, coarse enough that
			// crossing a long clip doesn't take a dozen scrolls.
			//
			// _lastMousePos is in CANVAS space here - this widget is the scrolled canvas - so the
			// scroll has to come back off it to get the on-screen position the anchor must hold.
			var anchorFrame = layout.XToFrame( _lastMousePos.x );
			var anchorViewportX = _lastMousePos.x - View.ScrollX;

			ScrollRequested?.Invoke( View.ZoomAt( anchorFrame, anchorViewportX, e.Delta > 0 ? 1f / 1.15f : 1.15f ) );

			ViewChanged?.Invoke();
			e.Accept();
			return;
		}

		if ( e.HasShift )
		{
			// Pan by a fifth of a screen per notch. The ScrollArea owns the position now, so this
			// asks for a new offset rather than moving a view range of its own.
			var page = MathF.Max( Width * 0.2f, 32f );

			ScrollRequested?.Invoke( MathF.Max( View.ScrollX + (e.Delta > 0 ? -page : page), 0f ) );

			ViewChanged?.Invoke();
			e.Accept();
			return;
		}

		base.OnMouseWheel( e );
	}

	/// <summary>Asks the timeline to move the ScrollArea. Raised rather than done here, because
	/// the ScrollArea is the canvas's parent and a canvas scrolling itself is how you get a widget
	/// fighting its own container.</summary>
	public Action<float> ScrollRequested { get; set; }

	private Vector2 _lastMousePos;

	public Action ViewChanged { get; set; }

	/// <summary>
	/// Reads the live horizontal scroll off the ScrollArea.
	///
	/// PULLED AT PAINT TIME, NOT PUSHED ON CHANGE, because dragging the scrollbar raises nothing
	/// we can subscribe to - the ScrollArea simply moves its canvas and repaints it. Without this
	/// the pinned name column would keep using a stale offset and slide away precisely when you
	/// scrolled by hand, which is the one case the pinning exists for.
	/// </summary>
	public Func<float> ScrollOffset { get; set; }

	private float _paintedScrollX = float.NaN;

	protected override void OnPaint()
	{
		if ( ScrollOffset is not null )
			View.ScrollX = ScrollOffset();

		// The rulers live outside the ScrollArea, so nothing moves or repaints them when it
		// scrolls. This widget does get repainted, so it's the only thing in a position to notice
		// and tell them. Guarded on an actual change, or a repaint would schedule a repaint.
		if ( View.ScrollX != _paintedScrollX )
		{
			_paintedScrollX = View.ScrollX;
			ViewChanged?.Invoke();
		}

		var layout = Geometry;

		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Theme.WidgetBackground );
		Paint.DrawRect( LocalRect );

		// Drawn before the early-out below, so the column edge is there even with nothing keyed -
		// it's part of the furniture, not a thing that appears once you have tracks.
		PaintGutterDivider( layout );

		var tracks = Anim?.BoneTracks;

		if ( tracks is null || tracks.Count == 0 )
		{
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.4f ) );
			Paint.DrawText( LocalRect, "No bones keyed yet - pose one in the viewport to start", TextFlag.Center );
			return;
		}

		for ( var i = 0; i < tracks.Count; i++ )
			PaintRow( layout, i, tracks[i] );

		PaintGrid( layout );

		for ( var i = 0; i < tracks.Count; i++ )
			PaintKeyframes( layout, i, tracks[i] );

		PaintPlayhead( layout );
	}

	private void PaintRow( RigTimelineLayout layout, int index, BoneTrack track )
	{
		var row = layout.RowRect( index );
		var isSelected = track.BoneName == SelectedBone;

		Paint.ClearPen();
		Paint.SetBrush( index % 2 == 1 ? Theme.WindowBackground.WithAlpha( 0.2f ) : Color.Transparent );
		Paint.DrawRect( row );

		// The selected bone's whole row is tinted, and its name marked, so which bone you're
		// posing is answerable from either the viewport or the timeline.
		if ( isSelected )
		{
			Paint.SetBrush( Theme.Yellow.WithAlpha( 0.12f ) );
			Paint.DrawRect( row );
		}

		// THE NAME COLUMN IS PINNED, and this offset is how.
		//
		// This widget is the ScrollArea's canvas, so scrolling right moves ALL of it left -
		// including the names, which would slide off the edge and leave you looking at unlabelled
		// rows. Drawing the column at the current scroll offset cancels that exactly, so it holds
		// still on screen while the tracks move underneath it.
		//
		// The alternative was splitting the names into their own widget outside the ScrollArea,
		// which fixes horizontal at the cost of breaking vertical: it would then need its own
		// scroll kept in step with the tracks' one, and two scrolls that must agree is a worse
		// problem than one offset.
		var pinned = View.ScrollX;

		Paint.ClearPen();
		Paint.SetBrush( Theme.WidgetBackground );
		Paint.DrawRect( new Rect( pinned, row.Top, RigTimelineLayout.Gutter, row.Height ) );

		if ( isSelected )
		{
			Paint.SetBrush( Theme.Yellow.WithAlpha( 0.12f ) );
			Paint.DrawRect( new Rect( pinned, row.Top, RigTimelineLayout.Gutter, row.Height ) );

			Paint.SetBrush( Theme.Yellow );
			Paint.DrawRect( new Rect( pinned, row.Top, 2f, row.Height ) );
		}

		Paint.SetDefaultFont( 7, isSelected ? 600 : 400 );
		Paint.SetPen( isSelected ? Theme.Yellow : Theme.TextControl.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pinned + 8f, row.Top, RigTimelineLayout.Gutter - 16f, row.Height ),
			track.BoneName, TextFlag.LeftCenter );
	}

	/// <summary>Which bone the viewport has selected, so the matching row can be highlighted.</summary>
	public string SelectedBone { get; set; }

	/// <summary>Raised when a bone's name is clicked in the gutter.</summary>
	public Action<string> BoneRowSelected { get; set; }

	/// <summary>The bone row under a point, if the point is in the name gutter rather than out
	/// among the keyframes.</summary>
	private BoneTrack HitBoneRow( Vector2 position )
	{
		if ( position.x >= GutterCanvasX || Anim is null )
			return null;

		var index = Geometry.HitRow( position );

		return index >= 0 && index < Anim.BoneTracks.Count ? Anim.BoneTracks[index] : null;
	}

	/// <summary>Right-clicking a bone's name - the track-wide operations, which previously had no
	/// home at all: the only way to clear a bone was a menu-bar item acting on whatever the
	/// viewport happened to have selected.</summary>
	private void OpenBoneRowMenu( BoneTrack track )
	{
		var menu = new Menu( this );

		menu.AddHeading( track.BoneName );

		menu.AddOption( "Select This Bone", "my_location", () => BoneRowSelected?.Invoke( track.BoneName ) )
			.StatusTip = "Select it in the viewport so it can be posed";

		menu.AddSeparator();

		var setAll = menu.AddMenu( "Set All Keys To", "show_chart" );

		foreach ( var mode in Enum.GetValues<KeyInterpolation>() )
		{
			var captured = mode;

			setAll.AddOption( Label( mode ), null, () =>
			{
				foreach ( var key in track.Keyframes )
					key.Interpolation = captured;

				Update();
				Edited?.Invoke();
			} );
		}

		menu.AddSeparator();

		var clear = menu.AddOption( "Clear All Keyframes", "clear_all", () =>
		{
			track.Keyframes.Clear();
			Update();
			Edited?.Invoke();
		} );

		clear.Enabled = track.Keyframes.Count > 0;
		clear.StatusTip = "Remove every keyframe on this bone, leaving the track in place";

		menu.AddOption( "Delete Track", "delete", () =>
		{
			Anim?.BoneTracks.Remove( track );
			Update();
			Edited?.Invoke();
		} ).StatusTip = "Remove the bone from the timeline entirely";

		menu.OpenAtCursor();
	}

	/// <summary>
	/// The edge between the bone-name column and the lanes.
	///
	/// Without it the names float in the same field as the keyframes and the eye has nothing to
	/// stop at, which is what makes a dense timeline hard to read - you lose which row you're on
	/// halfway across.
	///
	/// Deliberately dim. It's a boundary, not information, and it sits behind the keyframes.
	/// </summary>
	private void PaintGutterDivider( RigTimelineLayout layout )
	{
		Paint.SetPen( Theme.TextControl.WithAlpha( 0.2f ) );
		Paint.DrawLine(
			new Vector2( DividerCanvasX, 0f ),
			new Vector2( DividerCanvasX, Height ) );
	}

	private void PaintGrid( RigTimelineLayout layout )
	{
		Paint.SetPen( Theme.WindowBackground.WithAlpha( 0.5f ) );

		foreach ( var frame in layout.RulerFrames() )
		{
			var x = layout.FrameToX( frame );

			// Same rule as the ruler's labels - the grid stops at the divider rather than ruling
			// lines through the bone names.
			if ( x < DividerCanvasX )
				continue;

			Paint.DrawLine( new Vector2( x, 0f ), new Vector2( x, Height ) );
		}
	}

	/// <summary>
	/// Ringed dots joined by a segment line, the way MovieMaker draws a channel.
	///
	/// The connecting segment isn't decoration - it's coloured by the OUTGOING interpolation of
	/// the key on its left, so the easing of the whole clip is readable at a glance instead of
	/// only through a right-click menu. A stepped segment is drawn as a flat hold with a riser at
	/// the end, which is literally what it does to the pose.
	/// </summary>
	private const int CurveSamples = 20;

	private void PaintKeyframes( RigTimelineLayout layout, int index, BoneTrack track )
	{
		var row = layout.RowRect( index );
		var y = row.Center.y;

		// track.Keyframes is ALREADY SORTED - SetKeyframe inserts in place and EnsureSorted keeps
		// it that way, precisely so nothing has to sort per frame. This used to be
		// OrderBy(...).ToList(), which allocated a throwaway list for every visible track on every
		// repaint; the document's own comment calls that out as the thing to avoid, and the paint
		// path was quietly doing it anyway.
		var ordered = track.Keyframes;

		for ( var i = 0; i < ordered.Count - 1; i++ )
		{
			var from = layout.FrameToX( ordered[i].Frame );
			var to = layout.FrameToX( ordered[i + 1].Frame );

			// Wholly off the left - nothing to draw.
			if ( to < DividerCanvasX )
				continue;

			var key = ordered[i];

			// Eased segments share the green family - they are the same idea at different
			// strengths, and the curve drawn on the segment already says which half is eased.
			var color = key.Interpolation switch
			{
				KeyInterpolation.Smooth => Theme.Green.WithAlpha( 0.7f ),
				KeyInterpolation.EaseIn => Theme.Green.WithAlpha( 0.5f ),
				KeyInterpolation.EaseOut => Theme.Green.WithAlpha( 0.5f ),
				KeyInterpolation.Linear => Theme.Blue.WithAlpha( 0.7f ),
				_ => Theme.TextControl.WithAlpha( 0.4f )
			};

			Paint.SetPen( color, 2f );

			if ( key.Interpolation == KeyInterpolation.Stepped )
			{
				// Flat hold, then a vertical riser at the moment it snaps. The hold starts at the
				// divider when the key itself is off to the left.
				Paint.DrawLine( new Vector2( MathF.Max( from, DividerCanvasX ), y ), new Vector2( to, y ) );
				Paint.DrawLine( new Vector2( to, y - 5f ), new Vector2( to, y + 5f ) );
				continue;
			}

			// The curve is SAMPLED FROM Ease() itself, not faked with a hand-drawn bezier, so what
			// you see is the function playback actually uses. Plotted as the easing's deviation
			// from a straight line: Smooth bows into an S (slow out, fast through, slow in),
			// Linear stays flat because its deviation is zero by definition. Change Ease and this
			// drawing changes with it - it can't drift out of sync with the real behaviour.
			var amplitude = row.Height * 0.32f;
			var previous = new Vector2( from, y );

			for ( var step = 1; step <= CurveSamples; step++ )
			{
				var t = step / (float)CurveSamples;
				var point = new Vector2(
					MathX.Lerp( from, to, t ),
					y - (key.Ease( t ) - t) * amplitude );

				// Samples left of the divider are carried forward without being drawn, so the
				// curve starts exactly at the divider instead of either poking into the names or
				// beginning a few samples late with a visible notch.
				if ( point.x < DividerCanvasX )
				{
					previous = point;
					continue;
				}

				Paint.DrawLine( previous.x < DividerCanvasX
					? new Vector2( DividerCanvasX, previous.y )
					: previous, point );

				previous = point;
			}
		}

		foreach ( var key in track.Keyframes )
		{
			var center = layout.DiamondRect( index, key.Frame ).Center;

			// A key scrolled off the left edge would be drawn on top of the bone name, where it
			// looks like it belongs to the name column rather than to the track.
			if ( center.x < DividerCanvasX )
				continue;

			var isSelected = _selected is { } sel && sel.Track == track && sel.Key == key;
			var isHovered = _hover is { } hov && hov.Track == track && hov.Key == key;

			var radius = RigTimelineLayout.DiamondSize * 0.5f;
			var outer = isSelected ? Color.White : isHovered ? Theme.Yellow : Theme.Blue;

			// Drawn as an outer shape with the background punched out of the middle, so every key
			// reads as a ring. Selected fills solid instead - the one key you're acting on should
			// be the only solid thing on the row.
			Paint.ClearPen();
			Paint.SetBrush( outer );
			PaintKeyShape( center, radius * 2f, key.Interpolation );

			if ( isSelected )
				continue;

			Paint.SetBrush( Theme.WidgetBackground );
			PaintKeyShape( center, radius, key.Interpolation );
		}
	}

	/// <summary>
	/// THE SHAPE IS THE INTERPOLATION MODE. Round eases, angular is linear, square holds.
	///
	/// This is the one place the tool deliberately doesn't copy MovieMaker, which draws every key
	/// as the same dot - meaning the only way to see a key's easing there is to right-click it one
	/// at a time. Encoding it in the silhouette makes the timing of a whole clip readable in a
	/// glance, and it survives colourblindness and a dark screen in a way a colour swap wouldn't.
	/// </summary>
	/// <summary>Menu label for an interpolation mode. ToString() gives "EaseIn", which reads as a
	/// code identifier that leaked into the UI.</summary>
	private static string Label( KeyInterpolation mode ) => mode switch
	{
		KeyInterpolation.EaseIn => "Ease In",
		KeyInterpolation.EaseOut => "Ease Out",
		_ => mode.ToString()
	};

	private static void PaintKeyShape( Vector2 center, float radius, KeyInterpolation interpolation )
	{
		switch ( interpolation )
		{
			case KeyInterpolation.Linear:
				Paint.DrawPolygon(
					center + new Vector2( 0f, -radius ),
					center + new Vector2( radius, 0f ),
					center + new Vector2( 0f, radius ),
					center + new Vector2( -radius, 0f ) );
				return;

			case KeyInterpolation.Stepped:
				// Slightly tucked in - a square at the same radius reads noticeably bigger than a
				// circle, because it covers the corners too.
				var side = radius * 1.7f;
				Paint.DrawRect( new Rect( center.x - side * 0.5f, center.y - side * 0.5f, side, side ), 1f );
				return;

			default:
				// Rect form, matching RigHelpBox - the center+float overload is ambiguous about
				// whether the float is a radius or a diameter, and a wrong guess there is a
				// silently double-sized dot.
				Paint.DrawCircle( new Rect( center.x - radius, center.y - radius, radius * 2f, radius * 2f ) );
				return;
		}
	}

	private void PaintPlayhead( RigTimelineLayout layout )
	{
		var x = layout.FrameToX( Playhead );

		// Scrolled off the left, the playhead would otherwise be drawn straight down the bone
		// names - see the divider rule in PaintGutterDivider.
		if ( x < DividerCanvasX )
			return;

		Paint.SetPen( Theme.Primary.WithAlpha( 0.8f ), 1f );
		Paint.DrawLine( new Vector2( x, 0f ), new Vector2( x, Height ) );
	}

	private (BoneTrack Track, BoneKeyframe Key)? HitKeyframe( RigTimelineLayout layout, int row, Vector2 position )
	{
		if ( row < 0 || Anim is null || row >= Anim.BoneTracks.Count )
			return null;

		var track = Anim.BoneTracks[row];

		foreach ( var key in track.Keyframes )
		{
			var rect = layout.DiamondRect( row, key.Frame );

			// Keys scrolled behind the name column are not DRAWN there (see PaintKeyframes), so
			// they must not be clickable there either. Without this, the bone-name strip contains
			// invisible hitboxes that select and drag keys you can't see - and since the click
			// also scrubs, the playhead jumps somewhere unrelated at the same time.
			if ( rect.Center.x < DividerCanvasX )
				continue;

			if ( rect.IsInside( position ) )
				return (track, key);
		}

		return null;
	}

	protected override void OnMousePress( MouseEvent e )
	{
		Focus();

		if ( Anim is null || !e.LeftMouseButton )
			return;

		// Clicking a bone's name selects that bone, rather than doing nothing - the gutter looked
		// like a list of things you could pick and wasn't one.
		if ( HitBoneRow( e.LocalPosition ) is { } clickedBone )
		{
			BoneRowSelected?.Invoke( clickedBone.BoneName );
			e.Accepted = true;
			return;
		}

		var layout = Geometry;
		var row = layout.HitRow( e.LocalPosition );
		var hit = HitKeyframe( layout, row, e.LocalPosition );

		_selected = hit;
		_grabbed = false;

		if ( hit is not { } key )
			return;

		_grabbed = true;
		_dragged = false;
		_grabTrack = key.Track;
		_grabKey = key.Key;
		_grabOffset = layout.XToFrame( e.LocalPosition.x ) - key.Key.Frame;

		Scrubbed?.Invoke( key.Key.Frame );

		e.Accepted = true;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		// Kept for the wheel handler - WheelEvent carries no position, so the zoom anchor has to
		// come from the last place the mouse actually was.
		_lastMousePos = e.LocalPosition;

		var layout = Geometry;

		if ( !_grabbed || !e.ButtonState.HasFlag( MouseButtons.Left ) )
		{
			UpdateHover( layout, e.LocalPosition );
			return;
		}

		var frame = (int)MathF.Round( layout.XToFrame( e.LocalPosition.x ) - _grabOffset );
		frame = frame.Clamp( 0, (int)layout.LastFrame );

		if ( frame != _grabKey.Frame )
		{
			_grabKey.Frame = frame;
			_dragged = true;
			Scrubbed?.Invoke( frame );
			Update();
		}

		e.Accepted = true;
	}

	private void UpdateHover( RigTimelineLayout layout, Vector2 position )
	{
		var row = Anim is null ? -1 : layout.HitRow( position );
		var hit = HitKeyframe( layout, row, position );

		Cursor = hit is null ? CursorShape.Arrow : CursorShape.Finger;

		if ( _hover?.Key == hit?.Key )
			return;

		_hover = hit;
		Update();
	}

	protected override void OnMouseLeave()
	{
		if ( _hover is null )
			return;

		_hover = null;
		Update();
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		_grabbed = false;

		if ( !_dragged )
			return;

		_dragged = false;
		Edited?.Invoke();
	}

	// Static so a pose can be carried between clips, not just within one. A keyframe is a small
	// value object, so this holds a copy rather than a reference - copying then editing the
	// original shouldn't silently change what's on the clipboard.
	private static BoneKeyframe _clipboard;

	protected override void OnContextMenu( ContextMenuEvent e )
	{
		if ( Anim is null )
			return;

		// The name gutter gets the track-wide menu; out among the keyframes you get the per-key one.
		if ( HitBoneRow( e.LocalPosition ) is { } boneRow )
		{
			OpenBoneRowMenu( boneRow );
			e.Accepted = true;
			return;
		}

		var layout = Geometry;
		var row = layout.HitRow( e.LocalPosition );

		// Right-clicking a keyframe selects it first, so the menu always acts on what's under the
		// cursor rather than on whatever happened to be selected before.
		if ( HitKeyframe( layout, row, e.LocalPosition ) is { } hit )
			_selected = hit;

		var menu = new Menu( this );
		BuildKeyframeMenu( menu );
		menu.OpenAtCursor();

		Update();
	}

	private void BuildKeyframeMenu( Menu menu )
	{
		if ( _selected is not { } sel )
		{
			var pasteHere = menu.AddOption( "Paste", "content_paste", PasteClipboard );
			pasteHere.Enabled = false;
			pasteHere.StatusTip = "Select a keyframe's row first - paste needs to know which bone to paste onto";
			return;
		}

		// Bone name and frame as the heading itself rather than a disabled row - three greyed-out
		// lines of readout read as broken menu items, and cost more vertical space than the
		// actions underneath them.
		menu.AddHeading( $"{Shorten( sel.Track.BoneName )} — frame {sel.Key.Frame}" );

		// Editable, not a readout. Typing an exact number is the whole reason to open this menu on
		// a keyframe you've already dragged into roughly the right place - and it's the one thing
		// dragging in the viewport genuinely can't do.
		menu.AddWidget( BuildValueRow( "rotation", sel, true ) );
		menu.AddWidget( BuildValueRow( "position", sel, false ) );

		menu.AddSeparator();

		var interpolation = menu.AddMenu( "Interpolation Mode", "show_chart" );

		foreach ( var mode in Enum.GetValues<KeyInterpolation>() )
		{
			var option = interpolation.AddOption( Label( mode ), null, () => SetInterpolation( mode ) );
			option.Checkable = true;
			option.Checked = sel.Key.Interpolation == mode;
			option.StatusTip = mode switch
			{
				KeyInterpolation.Smooth => "Ease out of this key and into the next - the default for body motion",
				KeyInterpolation.Linear => "Constant speed, no easing - for mechanical motion",
				KeyInterpolation.EaseIn => "Start slow, arrive at full speed - the wind-up half of an action",
				KeyInterpolation.EaseOut => "Leave fast, settle slowly - what makes a movement land with weight",
				_ => "Hold this pose until the next key, then snap"
			};
		}

		menu.AddSeparator();
		menu.AddHeading( "Clipboard" );

		menu.AddOption( "Copy", "content_copy", CopySelected );
		menu.AddOption( "Cut", "content_cut", () => { CopySelected(); DeleteSelectedKeyframe(); } );

		var paste = menu.AddOption( "Paste", "content_paste", PasteClipboard );
		paste.Enabled = _clipboard is not null;
		paste.StatusTip = "Paste the copied pose onto this bone at the playhead";

		menu.AddOption( "Delete", "delete", DeleteSelectedKeyframe );
	}

	/// <summary>Long rig bones ("arm_lower_R_twistctrl1") push the menu absurdly wide and bury the
	/// frame number at the end. The tail is the part that identifies it, so the front is what
	/// gets dropped.</summary>
	private static string Shorten( string bone, int max = 22 ) =>
		string.IsNullOrEmpty( bone ) || bone.Length <= max ? bone : "…" + bone[^max..];

	/// <summary>
	/// An X/Y/Z row of editable fields, axis-coloured red/green/blue.
	///
	/// The colours aren't decoration - they're the same axis convention as the viewport gizmo's
	/// arrows, so "the red one" means one thing everywhere in the tool. Committing a field writes
	/// straight through to the keyframe and re-poses the viewport, so a typed number is visible
	/// immediately rather than on the next scrub.
	/// </summary>
	private Widget BuildValueRow( string label, (BoneTrack Track, BoneKeyframe Key) sel, bool rotation )
	{
		var row = new Widget( this ) { Layout = Layout.Row() };
		row.Layout.Margin = new Sandbox.UI.Margin( 8, 2 );
		row.Layout.Spacing = 4;

		row.Layout.Add( new Editor.Label( label ) { FixedWidth = 52 } );

		var angles = sel.Key.Local.Rotation.Angles();
		var position = sel.Key.Local.Position;

		var values = rotation
			? new[] { angles.pitch, angles.yaw, angles.roll }
			: new[] { position.x, position.y, position.z };

		// Matches the gizmo's arrow colours, not the pitch/yaw/roll naming - what you see in the
		// viewport is what you're typing into.
		var colors = new[] { Theme.Red, Theme.Green, Theme.Blue };
		var names = rotation ? new[] { "P", "Y", "R" } : new[] { "X", "Y", "Z" };

		for ( var axis = 0; axis < 3; axis++ )
		{
			var index = axis;

			var chip = new Editor.Label( names[axis] ) { FixedWidth = 12, Color = colors[axis] };
			chip.SetStyles( "font-weight: 600;" );
			row.Layout.Add( chip );

			var edit = new LineEdit( $"{values[axis]:0.##}", row ) { FixedWidth = 52 };

			edit.TextEdited += text =>
			{
				if ( !float.TryParse( text, out var parsed ) )
					return;

				ApplyValue( sel, rotation, index, parsed );
			};

			row.Layout.Add( edit );
		}

		return row;
	}

	private void ApplyValue( (BoneTrack Track, BoneKeyframe Key) sel, bool rotation, int axis, float value )
	{
		var local = sel.Key.Local;

		if ( rotation )
		{
			var angles = local.Rotation.Angles();

			if ( axis == 0 ) angles.pitch = value;
			else if ( axis == 1 ) angles.yaw = value;
			else angles.roll = value;

			local = new Transform( local.Position, angles.ToRotation(), local.Scale );
		}
		else
		{
			var position = local.Position;

			if ( axis == 0 ) position.x = value;
			else if ( axis == 1 ) position.y = value;
			else position.z = value;

			local = new Transform( position, local.Rotation, local.Scale );
		}

		sel.Key.Local = local;

		Update();

		// Re-poses the viewport so a typed number shows up now, not on the next scrub.
		Scrubbed?.Invoke( Playhead );
		Edited?.Invoke();
	}

	private void SetInterpolation( KeyInterpolation mode )
	{
		if ( _selected is not { } sel )
			return;

		sel.Key.Interpolation = mode;

		Update();
		Edited?.Invoke();
	}

	private void CopySelected()
	{
		if ( _selected is not { } sel )
			return;

		_clipboard = new BoneKeyframe
		{
			Frame = sel.Key.Frame,
			Local = sel.Key.Local,
			Interpolation = sel.Key.Interpolation
		};
	}

	/// <summary>Pastes onto the selected bone's track at the playhead - not at the frame the pose
	/// was copied from, which would make copying a key to a different time impossible.</summary>
	private void PasteClipboard()
	{
		if ( _clipboard is null || _selected is not { } sel )
			return;

		var frame = (int)MathF.Round( Playhead );

		sel.Track.SetKeyframe( frame, _clipboard.Local );

		if ( sel.Track.Keyframes.FirstOrDefault( k => k.Frame == frame ) is { } pasted )
			pasted.Interpolation = _clipboard.Interpolation;

		Update();
		Edited?.Invoke();
	}

	public bool HasSelectedKeyframe => _selected is not null;

	public void DeleteSelectedKeyframe()
	{
		if ( _selected is not { } sel )
			return;

		sel.Track.Keyframes.Remove( sel.Key );
		_selected = null;

		Update();
		Edited?.Invoke();
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		if ( e.Key != KeyCode.Delete )
			return;

		DeleteSelectedKeyframe();
		e.Accepted = true;
	}
}

internal sealed class RigTimelineRuler : Widget
{
	private readonly Widget _lanes;

	public int FrameCount { get; set; } = 30;
	public float Playhead { get; set; }
	public Action<float> Scrubbed { get; set; }

	/// <summary>Frames per second, for turning a frame index into a timecode. Comes from the
	/// clip's Animation Speed.</summary>
	public int Fps { get; set; } = 30;

	/// <summary>Timecode above the lanes, frame numbers below - the same split MovieMaker uses.
	/// Time is what you think in when judging whether an action reads; frames are what you think
	/// in when placing a key. Showing both means never converting in your head.</summary>
	public bool ShowTimecode { get; set; } = true;

	private bool _scrubbing;

	public RigTimelineRuler( Widget parent, Widget lanes ) : base( parent )
	{
		_lanes = lanes;
		FixedHeight = RigTimelineLayout.RulerHeight;
		Cursor = CursorShape.SizeH;

		// Needed for the wheel zoom anchor: without it OnMouseMove only fires while a button is
		// held, so the anchor would be stale whenever you simply hover and scroll.
		MouseTracking = true;
	}

	/// <summary>Compact, and only as precise as the clip is long.
	///
	/// MovieMaker's full "00:00.000" is nine characters for what is usually under a second of
	/// animation - at a small window size those labels collide into an unreadable strip. A clip
	/// under a minute doesn't need a minutes field, and one under ten seconds doesn't need
	/// milliseconds.</summary>
	private string Timecode( float frame )
	{
		var seconds = frame / MathF.Max( Fps, 1 );
		var total = FrameCount / MathF.Max( Fps, 1 );

		if ( total < 10f )
			return $"{seconds:0.00}s";

		if ( total < 60f )
			return $"{seconds:0.0}s";

		return $"{(int)(seconds / 60f)}:{seconds % 60f:00.0}";
	}

	/// <summary>The same instance the lanes hold, so ruler and lanes always agree on what window
	/// is on screen.</summary>
	public RigTimelineLanes.ViewRange View { get; set; } = new();

	/// <summary>VIEWPORT space: this widget sits outside the ScrollArea and never moves, so it has
	/// to subtract the scroll itself. Its own width, not the canvas's - it only draws what fits on
	/// screen. That one difference from the lanes' Geometry is the whole of how the two coordinate
	/// spaces are kept apart.</summary>
	private RigTimelineLayout Geometry => new( Width, FrameCount, 0, View.ScrollX, View.PixelsPerFrame );

	/// <summary>Zoom and pan work from the rulers too - they're the natural place to reach for it,
	/// and having it only work over the lanes would be an arbitrary dead zone.</summary>
	protected override void OnMouseWheel( WheelEvent e )
	{
		var layout = Geometry;

		if ( e.HasCtrl )
		{
			// _lastMouseX is already viewport space here, so unlike the lanes there is nothing to
			// convert - it goes to both arguments' worth of meaning directly.
			ScrollRequested?.Invoke( View.ZoomAt( layout.XToFrame( _lastMouseX ), _lastMouseX,
				e.Delta > 0 ? 1f / 1.15f : 1.15f ) );

			ViewChanged?.Invoke();
			e.Accept();
			return;
		}

		if ( e.HasShift )
		{
			var page = MathF.Max( Width * 0.2f, 32f );

			ScrollRequested?.Invoke( MathF.Max( View.ScrollX + (e.Delta > 0 ? -page : page), 0f ) );

			ViewChanged?.Invoke();
			e.Accept();
			return;
		}

		base.OnMouseWheel( e );
	}

	public Action<float> ScrollRequested { get; set; }

	private float _lastMouseX;

	public Action ViewChanged { get; set; }

	protected override void OnPaint()
	{
		var layout = Geometry;

		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Theme.SurfaceBackground );
		Paint.DrawRect( LocalRect );

		if ( _lanes.Width < RigTimelineLayout.Gutter )
			return;

		Paint.SetDefaultFont( 7 );
		Paint.SetPen( Theme.TextControl.WithAlpha( 0.45f ) );
		Paint.DrawText( new Rect( 8f, 0f, RigTimelineLayout.Gutter - 16f, Height ),
			ShowTimecode ? "BONE" : "FRAME", TextFlag.LeftCenter );

		// Same edge as the lanes draw, at the same x and the same weight, so the column reads as
		// one continuous strip through the ruler, the tracks and the second ruler rather than as
		// three separate widgets that happen to be stacked.
		Paint.SetPen( Theme.TextControl.WithAlpha( 0.2f ) );
		Paint.DrawLine(
			new Vector2( RigTimelineLayout.DividerX, 0f ),
			new Vector2( RigTimelineLayout.DividerX, Height ) );

		// Timecodes are the wider label, so they get the wider minimum spacing.
		var labelWidth = ShowTimecode ? 58f : 40f;

		foreach ( var frame in layout.RulerFrames( labelWidth ) )
		{
			var x = layout.FrameToX( frame );

			// NOTHING IN THE RULER CROSSES THE DIVIDER. RulerFrames starts at the last tick BEFORE
			// the view, so that first label sits left of the lane area - fine when the view starts
			// at zero and nothing is there, and a label printed on top of "BONE" the moment you
			// scroll.
			if ( x < RigTimelineLayout.DividerX )
				continue;

			Paint.SetPen( Theme.TextControl.WithAlpha( 0.25f ) );

			// Ticks hang off the edge that faces the lanes, so the two rulers bracket the tracks.
			Paint.DrawLine(
				new Vector2( x, ShowTimecode ? Height - 5f : 0f ),
				new Vector2( x, ShowTimecode ? Height : 5f ) );

			if ( x + labelWidth * 0.7f > layout.LaneArea.Right + 8f )
				continue;

			Paint.SetPen( Theme.TextControl.WithAlpha( 0.6f ) );
			Paint.DrawText( new Rect( x + 4f, 0f, labelWidth, Height ),
				ShowTimecode ? Timecode( frame ) : $"{(int)frame}", TextFlag.LeftCenter );
		}

		// The rule bracketing the tracks - under the top ruler, over the bottom one. It was
		// Theme.WindowBackground, which is all but invisible against the ruler's own surface, so
		// the rulers bled into the track area. Same colour and weight as the gutter edge, so the
		// three of them read as one frame around the tracks rather than three unrelated lines.
		Paint.SetPen( Theme.TextControl.WithAlpha( 0.2f ) );
		Paint.DrawLine(
			new Vector2( 0f, ShowTimecode ? Height - 1f : 0f ),
			new Vector2( Width, ShowTimecode ? Height - 1f : 0f ) );

		PaintPlayhead( layout );
	}

	/// <summary>A flag pointing at the lanes, matching MovieMaker's - the top ruler's points down,
	/// the bottom ruler's points up, so the playhead reads as one continuous marker through the
	/// whole timeline rather than two unrelated arrows.</summary>
	private void PaintPlayhead( RigTimelineLayout layout )
	{
		var x = layout.FrameToX( Playhead );

		// The flag is drawn from its centre and is 8px wide, so it starts spilling into the name
		// column half a flag before its frame reaches the divider - hence the half-width margin
		// rather than a plain x < DividerX. The lanes' playhead has the same rule; this one was
		// missed because it's a different widget drawing a different shape for the same thing.
		if ( x + 4f < RigTimelineLayout.DividerX )
			return;

		Paint.ClearPen();
		Paint.SetBrush( Theme.Yellow );

		if ( ShowTimecode )
		{
			Paint.DrawRect( new Rect( x - 4f, Height - 11f, 8f, 7f ), 1f );
			Paint.DrawPolygon(
				new Vector2( x - 4f, Height - 5f ),
				new Vector2( x + 4f, Height - 5f ),
				new Vector2( x, Height - 1f ) );
			return;
		}

		Paint.DrawRect( new Rect( x - 4f, 4f, 8f, 7f ), 1f );
		Paint.DrawPolygon(
			new Vector2( x - 4f, 5f ),
			new Vector2( x + 4f, 5f ),
			new Vector2( x, 1f ) );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton || e.LocalPosition.x < RigTimelineLayout.Gutter )
			return;

		_scrubbing = true;
		Scrub( e.LocalPosition );
		e.Accepted = true;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		_lastMouseX = e.LocalPosition.x;

		if ( !_scrubbing )
			return;

		Scrub( e.LocalPosition );
		e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e ) => _scrubbing = false;

	private void Scrub( Vector2 position )
	{
		var layout = Geometry;
		Scrubbed?.Invoke( layout.XToFrame( position.x ) );
	}
}
