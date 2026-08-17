using Editor;
using Marionette;
using Sandbox;
using System;

namespace Marionette.Tools;

/// <summary>
/// The tutorial as a dockable panel rather than a line of text in the status bar.
///
/// It started in the status bar and that was the wrong home for it: a single line competing with
/// hover hints, at the very bottom of the window, in a strip the eye reads as chrome. Something
/// you are meant to actively follow needs somewhere it can be looked AT - big enough to read at a
/// glance, with the steps around it for context so you know where you are in the process.
///
/// The status bar keeps what it's genuinely good at: telling you what the thing under the cursor
/// does. That's a different job and it doesn't compete with this.
/// </summary>
internal sealed class RigTutorialPanel : Widget
{
	private readonly Widget _list;
	private readonly Editor.Label _heading;
	private readonly Editor.Label _progress;

	public RigTutorial Tutorial { get; set; }

	/// <summary>Restart/dismiss come from here as well as the Help menu, so the panel is
	/// self-sufficient once it's open.</summary>
	public Action Changed { get; set; }

	/// <summary>Opens and raises a dock by title. "Where is that?" is the question every written
	/// instruction leaves behind, and the honest answer is to just show them.</summary>
	public Action<string> RevealPanel { get; set; }

	public RigTutorialPanel( Widget parent ) : base( parent )
	{
		Name = "Tutorial";
		WindowTitle = "Tutorial";
		SetWindowIcon( "school" );

		Layout = Layout.Column();
		Layout.Margin = 0;

		var header = Layout.AddRow();
		header.Margin = new Sandbox.UI.Margin( 12, 10, 12, 6 );
		header.Spacing = 8;

		_heading = new Editor.Label( "Build A Wave" );
		_heading.SetStyles( "font-weight: 600; font-size: 15px;" );
		header.Add( _heading, 1 );

		_progress = new Editor.Label( "" ) { Color = Theme.Green };
		header.Add( _progress );

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.VerticalScrollbarMode = ScrollbarMode.Auto;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		_list = new Widget( this ) { Layout = Layout.Column() };
		_list.Layout.Margin = new Sandbox.UI.Margin( 12, 4, 12, 12 );
		_list.Layout.Spacing = 8;
		scroll.Canvas = _list;

		var buttons = Layout.AddRow();
		buttons.Margin = new Sandbox.UI.Margin( 12, 6, 12, 10 );
		buttons.Spacing = 6;

		buttons.Add( new Button( "Restart", "restart_alt" )
		{
			Clicked = () => { Tutorial?.Restart(); Changed?.Invoke(); Rebuild(); }
		} );

		buttons.AddStretchCell();

		buttons.Add( new Button( "Dismiss", "close" )
		{
			Clicked = () => { Tutorial?.Dismiss(); Changed?.Invoke(); Rebuild(); }
		} );

		Rebuild();
	}

	/// <summary>
	/// Every step is listed, not just the current one, and each is marked done / current / ahead.
	///
	/// Showing one instruction at a time - which is what the status bar did - hides how long the
	/// process is and where you are in it, and both of those are most of what makes a tutorial
	/// feel finishable rather than endless. Completed steps stay visible for the same reason:
	/// progress you can see is the thing that makes you do the next one.
	/// </summary>
	public void Rebuild()
	{
		_list.Layout.Clear( true );

		if ( Tutorial is null )
			return;

		if ( !Tutorial.Active )
		{
			_heading.Text = "Tutorial";
			_progress.Text = "";

			_list.Layout.Add( new Editor.Label(
				"Not running. Restart below, or Help → Start Wave Tutorial.\n\n" +
				"It walks through building one real animation - a wave - a step at a time, and " +
				"ticks each step off as you actually do it." )
			{ WordWrap = true } );

			return;
		}

		_heading.Text = "Build A Wave";
		_progress.Text = $"{Math.Min( Tutorial.CurrentIndex + 1, Tutorial.StepCount )} / {Tutorial.StepCount}";

		for ( var i = 0; i < Tutorial.StepCount; i++ )
		{
			var step = Tutorial.StepAt( i );
			if ( step is null )
				continue;

			var isDone = i < Tutorial.CurrentIndex;
			var isCurrent = i == Tutorial.CurrentIndex;

			var row = _list.Layout.AddRow();
			row.Spacing = 10;

			row.Add( new StepGlyph( this, step.Art, isDone, isCurrent ) );

			var column = row.AddColumn( 1 );
			column.Spacing = 3;

			var text = new Editor.Label( step.Instruction )
			{
				WordWrap = true,
				// Done steps fade back, the current one is the only thing at full contrast, and
				// steps ahead sit between the two - so the eye lands on the current one first.
				Color = isDone
					? Theme.TextControl.WithAlpha( 0.4f )
					: isCurrent ? Theme.TextControl : Theme.TextControl.WithAlpha( 0.6f )
			};

			if ( isCurrent )
				text.SetStyles( "font-weight: 600;" );

			column.Add( text );

			// The why, and the way there - shown only for the step you're on. Every step carrying
			// its own paragraph turns the panel into the wall of text this replaced.
			if ( isCurrent )
			{
				if ( !string.IsNullOrWhiteSpace( step.Detail ) )
				{
					column.Add( new Editor.Label( step.Detail )
					{
						WordWrap = true,
						Color = Theme.TextControl.WithAlpha( 0.55f )
					} );
				}

				if ( !string.IsNullOrWhiteSpace( step.Panel ) )
				{
					var reveal = column.AddRow();
					reveal.Add( new Button( $"Show me the {step.Panel} panel", "my_location" )
					{
						Clicked = () => RevealPanel?.Invoke( step.Panel )
					} );
					reveal.AddStretchCell();
				}
			}
		}

		if ( Tutorial.CurrentIndex >= Tutorial.StepCount )
		{
			var done = new Editor.Label( "That's a wave. Save it, then try changing the timing - drag the keys closer together and play it back." )
			{ WordWrap = true, Color = Theme.Green };

			_list.Layout.Add( done );
		}
	}
}

/// <summary>
/// The little drawing beside each step.
///
/// Painted rather than an image asset: it ships with the code, scales with the panel, follows the
/// editor theme, and there's no binary to keep in sync with anything. It also doubles as the
/// step's status - done steps go green and dim, the current one is bright, the rest sit back - so
/// one thing carries both "what is this about" and "where am I".
/// </summary>
internal sealed class StepGlyph : Widget
{
	private readonly RigTutorial.StepArt _art;
	private readonly bool _done;
	private readonly bool _current;

	public StepGlyph( Widget parent, RigTutorial.StepArt art, bool done, bool current ) : base( parent )
	{
		_art = art;
		_done = done;
		_current = current;

		FixedWidth = 34;
		FixedHeight = 34;
	}

	private static Vector2 ArcPoint( Vector2 center, float radius, float degrees )
	{
		var radians = degrees * MathF.PI / 180f;
		return center + new Vector2( MathF.Cos( radians ) * radius, -MathF.Sin( radians ) * radius );
	}

	protected override void OnPaint()
	{
		var color = _done ? Theme.Green : _current ? Theme.Yellow : Theme.TextControl.WithAlpha( 0.35f );
		var center = LocalRect.Center;

		Paint.Antialiasing = true;

		// A soft plate behind the glyph, brightest on the current step - gives the row an anchor
		// for the eye without needing a heavier highlight on the text.
		Paint.ClearPen();
		Paint.SetBrush( color.WithAlpha( _current ? 0.16f : 0.07f ) );
		Paint.DrawRect( LocalRect, 6f );

		Paint.SetPen( color, _current ? 2f : 1.5f );
		Paint.ClearBrush();

		switch ( _art )
		{
			// A torso and two arms - "pick a model".
			case RigTutorial.StepArt.Model:
				Paint.DrawCircle( center + new Vector2( 0, -8 ), 6f );
				Paint.DrawLine( center + new Vector2( 0, -4 ), center + new Vector2( 0, 7 ) );
				Paint.DrawLine( center + new Vector2( 0, 0 ), center + new Vector2( -7, 6 ) );
				Paint.DrawLine( center + new Vector2( 0, 0 ), center + new Vector2( 7, 6 ) );
				break;

			// A joint with its chain - the bone dots as they're drawn in the viewport.
			case RigTutorial.StepArt.Bone:
				Paint.DrawLine( center + new Vector2( -8, 7 ), center );
				Paint.DrawLine( center, center + new Vector2( 8, -7 ) );
				Paint.SetBrush( color );
				Paint.DrawCircle( center, 5f );
				Paint.ClearBrush();
				Paint.DrawCircle( center + new Vector2( -8, 7 ), 3f );
				Paint.DrawCircle( center + new Vector2( 8, -7 ), 3f );
				break;

			// An arc with a head on it - rotation, the default drag. Stepped out of line segments
			// rather than DrawArc, whose overload I'd guessed at and got wrong; a dozen lines is
			// indistinguishable at this size and cannot be wrong about an API.
			case RigTutorial.StepArt.Rotate:
			{
				const int segments = 14;
				const float sweep = 290f;
				const float radius = 9f;

				var previous = ArcPoint( center, radius, 40f );

				for ( var i = 1; i <= segments; i++ )
				{
					var point = ArcPoint( center, radius, 40f + sweep * i / segments );
					Paint.DrawLine( previous, point );
					previous = point;
				}

				Paint.SetBrush( color );
				Paint.DrawPolygon(
					center + new Vector2( 4, -9 ),
					center + new Vector2( 11, -7 ),
					center + new Vector2( 5, -2 ) );
				break;
			}

			// A key on a track - the timeline lane, in miniature.
			case RigTutorial.StepArt.Keyframe:
				Paint.DrawLine( center + new Vector2( -12, 0 ), center + new Vector2( 12, 0 ) );
				Paint.SetBrush( color );
				Paint.DrawPolygon(
					center + new Vector2( 0, -7 ),
					center + new Vector2( 7, 0 ),
					center + new Vector2( 0, 7 ),
					center + new Vector2( -7, 0 ) );
				break;

			case RigTutorial.StepArt.Play:
				Paint.SetBrush( color );
				Paint.DrawPolygon(
					center + new Vector2( -5, -8 ),
					center + new Vector2( 9, 0 ),
					center + new Vector2( -5, 8 ) );
				break;
		}

		// Completed steps get a tick over the top, so "done" reads at a glance without having to
		// compare colours between rows.
		if ( !_done )
			return;

		Paint.SetPen( Theme.Green, 2f );
		Paint.ClearBrush();
		Paint.DrawLine( center + new Vector2( 3, 8 ), center + new Vector2( 7, 12 ) );
		Paint.DrawLine( center + new Vector2( 7, 12 ), center + new Vector2( 14, 3 ) );
	}
}
