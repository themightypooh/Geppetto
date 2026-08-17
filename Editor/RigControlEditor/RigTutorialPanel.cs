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
	/// <summary>The tutorial's name, in one place. It was written out at three separate call
	/// sites and two of them still said "Build A Wave" long after the content became a switch
	/// flip - which is exactly what a duplicated string does.</summary>
	private const string Title = "Flip A Switch";

	private readonly Widget _list;
	private readonly Editor.Label _heading;
	private readonly Editor.Label _progress;

	private RigTutorial _tutorial;

	/// <summary>Rebuilds on assignment. The constructor's own Rebuild runs before any object
	/// initializer does, so it always sees a null tutorial and bails - leaving the panel blank
	/// until something else happened to refresh it.</summary>
	public RigTutorial Tutorial
	{
		get => _tutorial;
		set
		{
			_tutorial = value;
			Rebuild();
		}
	}

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

		_heading = new Editor.Label( Title );
		_heading.SetStyles( "font-weight: 600; font-size: 20px;" );
		header.Add( _heading, 1 );

		_progress = new Editor.Label( "" ) { Color = Theme.Green };
		_progress.SetStyles( "font-size: 15px; font-weight: 500;" );
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
	/// What you see before starting, and after skipping: what this is, one button to begin, one to
	/// skip, and the opt-out.
	///
	/// The opt-out is deliberately here and not buried in a menu. A panel that opens itself every
	/// time you launch the tool, with no visible way to stop it, is the thing people resent about
	/// tutorials - and the resentment attaches to the tool, not the tutorial. One obvious checkbox
	/// costs nothing and removes the whole objection.
	/// </summary>
	private void BuildStartScreen()
	{
		_heading.Text = Title;
		_progress.Text = $"{Tutorial.StepCount} steps";

		var intro = new Editor.Label(
			"Build a reach-out-and-flip-a-switch animation on the first-person arms, a step at " +
			"a time. Each step ticks itself off as you do it, and points at the panel it means.\n\n" +
			"Almost every action animation is four beats, and this walks through all four:\n\n" +
			"    REST  -  the pose it starts and ends on\n" +
			"    ANTICIPATION  -  a small wind-up AWAY from the action\n" +
			"    EXTREME  -  the action itself, at its furthest point\n" +
			"    SETTLE  -  drifting slightly past, then back\n\n" +
			"Anticipation and settle are the two people skip, and they're most of the difference " +
			"between animation that reads as alive and animation that reads as a machine.\n\n" +
			"You can start it, skip it, or come back later from the Help menu." )
		{ WordWrap = true };

		intro.SetStyles( "font-size: 15px; line-height: 1.35;" );
		_list.Layout.Add( intro );

		_list.Layout.AddSpacingCell( 8f );

		var buttons = _list.Layout.AddRow();
		buttons.Spacing = 8;

		buttons.Add( new Button.Primary( "Start Tutorial", "play_arrow" )
		{
			Clicked = () => { Tutorial.Restart(); Changed?.Invoke(); Rebuild(); }
		} );

		buttons.Add( new Button( "Skip", "close" )
		{
			Clicked = () => { Tutorial.Dismiss(); Changed?.Invoke(); Rebuild(); }
		} );

		buttons.AddStretchCell();

		_list.Layout.AddSpacingCell( 4f );

		var optOut = _list.Layout.AddRow();
		optOut.Spacing = 8;
		optOut.Alignment = TextFlag.LeftCenter;

		var checkbox = optOut.Add( new Checkbox() );
		checkbox.Text = "Don't open this on startup again";

		// Inverted: the cookie stores whether to auto-open, the checkbox asks whether to stop.
		checkbox.Value = !RigTutorial.OpenOnStartup;
		checkbox.Toggled += () => RigTutorial.OpenOnStartup = !checkbox.Value;

		optOut.AddStretchCell();
	}

	/// <summary>
	/// ONE STEP AT A TIME, given the whole panel.
	///
	/// The first version listed all nine at once with the current one highlighted. That shows
	/// progress, but it also puts eight things you are not doing in front of the one you are, and
	/// the instruction you actually need ends up as one line among many. A tutorial is read
	/// mid-task, glanced at between drags - it has to answer "what now" in one look.
	///
	/// Progress hasn't been thrown away, it's just moved: the counter in the header and the row of
	/// dots underneath say where you are without competing for the same space.
	/// </summary>
	public void Rebuild()
	{
		_list.Layout.Clear( true );

		if ( Tutorial is null )
			return;

		if ( !Tutorial.Active )
		{
			BuildStartScreen();
			return;
		}

		_heading.Text = Title;

		var index = Math.Min( Tutorial.CurrentIndex, Tutorial.StepCount - 1 );
		var step = Tutorial.StepAt( index );

		if ( Tutorial.CurrentIndex >= Tutorial.StepCount || step is null )
		{
			_progress.Text = "done";
			BuildFinishScreen();
			return;
		}

		_progress.Text = $"step {index + 1} of {Tutorial.StepCount}";

		var header = _list.Layout.AddRow();
		header.Spacing = 12;
		header.Add( new StepGlyph( this, step.Art, false, true ) );

		var instruction = new Editor.Label( step.Instruction ) { WordWrap = true };
		instruction.SetStyles( "font-weight: 600; font-size: 17px; line-height: 1.3;" );
		header.Add( instruction, 1 );

		_list.Layout.AddSpacingCell( 4f );

		if ( !string.IsNullOrWhiteSpace( step.Detail ) )
		{
			var detail = new Editor.Label( step.Detail )
			{
				WordWrap = true,
				Color = Theme.TextControl.WithAlpha( 0.65f )
			};

			detail.SetStyles( "font-size: 14px; line-height: 1.45;" );
			_list.Layout.Add( detail );
		}

		if ( !string.IsNullOrWhiteSpace( step.Panel ) )
		{
			_list.Layout.AddSpacingCell( 6f );

			var reveal = _list.Layout.AddRow();
			reveal.Add( new Button( $"Show me the {step.Panel} panel", "my_location" )
			{
				Clicked = () => RevealPanel?.Invoke( step.Panel )
			} );
			reveal.AddStretchCell();
		}

		_list.Layout.AddStretchCell();
		_list.Layout.Add( new StepDots( this, Tutorial.StepCount, index ) );
	}

	/// <summary>The end of the run. Says what was built and what to do with it, rather than just
	/// stopping - finishing something should feel like finishing something.</summary>
	private void BuildFinishScreen()
	{
		var done = new Editor.Label(
			"That's a reach and a press, with all four beats in it. Save it, then try the thing " +
			"that teaches the most: drag your keys closer together and play it again. Almost " +
			"every first animation is twice as slow as it should be, and feeling that difference " +
			"is worth more than any amount of re-posing. The same four beats build every other " +
			"action you'll animate." )
		{ WordWrap = true, Color = Theme.Green };

		done.SetStyles( "font-size: 15px; line-height: 1.45;" );
		_list.Layout.Add( done );
		_list.Layout.AddStretchCell();
	}
}

/// <summary>A row of dots, one per step, filled up to where you are - progress without spending
/// the panel on eight instructions you aren't following.</summary>
internal sealed class StepDots : Widget
{
	private readonly int _count;
	private readonly int _current;

	public StepDots( Widget parent, int count, int current ) : base( parent )
	{
		_count = count;
		_current = current;

		FixedHeight = 14;
	}

	protected override void OnPaint()
	{
		if ( _count <= 0 )
			return;

		Paint.Antialiasing = true;
		Paint.ClearPen();

		const float spacing = 14f;
		var totalWidth = (_count - 1) * spacing;
		var startX = (Width - totalWidth) * 0.5f;
		var y = LocalRect.Center.y;

		for ( var i = 0; i < _count; i++ )
		{
			var center = new Vector2( startX + i * spacing, y );

			if ( i == _current )
			{
				Paint.SetBrush( Theme.Yellow );
				Paint.DrawCircle( center, 8f );
				continue;
			}

			Paint.SetBrush( i < _current ? Theme.Green.WithAlpha( 0.7f ) : Theme.TextControl.WithAlpha( 0.25f ) );
			Paint.DrawCircle( center, 5f );
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
