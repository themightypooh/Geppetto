using Editor;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The house tutorial as a dockable panel.
///
/// Effigy's status bar already does the job a status bar is good at - saying what is under the
/// cursor - and a tutorial is not that. Something you are meant to actively follow needs to be
/// looked AT: big enough to read at a glance, with its progress around it so you know where you
/// are. Same conclusion RigTutorialPanel reached, and the same division of labour.
///
/// One step at a time, deliberately. Listing all five puts four things you are not doing in
/// front of the one you are; the header counter and the dots carry progress instead.
/// </summary>
internal sealed class EffigyTutorialPanel : Widget
{
	/// <summary>The tutorial's name, in one place. Rig Control's was written out at three call
	/// sites and two of them went stale - which is exactly what a duplicated string does.</summary>
	private const string Title = "Build a House";

	private readonly Widget _list;
	private readonly Editor.Label _heading;
	private readonly Editor.Label _progress;

	private EffigyTutorial _tutorial;

	/// <summary>Rebuilds on assignment. The constructor's own Rebuild runs before any object
	/// initializer does, so it always sees a null tutorial and bails - leaving the panel blank
	/// until something else happened to refresh it.</summary>
	public EffigyTutorial Tutorial
	{
		get => _tutorial;
		set
		{
			_tutorial = value;
			Rebuild();
		}
	}

	/// <summary>Restart and dismiss come from here as well as the Help menu, so the panel is
	/// self-sufficient once it is open.</summary>
	public Action Changed { get; set; }

	/// <summary>Opens and raises a dock by title - the honest answer to "where is that?", which
	/// is the question every written instruction leaves behind.</summary>
	public Action<string> RevealPanel { get; set; }

	/// <summary>
	/// Which feature-strip button the window should be lighting up, or null for none.
	///
	/// Raised on every Rebuild rather than pushed once when a step changes, because the strip is
	/// rebuilt whenever the document changes shape (see EffigyWindow.RefreshToolStrip) and a
	/// highlight set on a button that has since been thrown away is a highlight on nothing. The
	/// window re-resolves the target each time it is told, and holds no button reference.
	/// </summary>
	public Action<EffigyToolTarget?> HighlightTool { get; set; }

	public EffigyTutorialPanel( Widget parent ) : base( parent )
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

		// Docked along the bottom this panel can be 1500px wide, and a line of text that long is
		// genuinely hard to read - the eye loses its place on the way back to the left margin.
		// Capped at a comfortable measure; the rest of the width stays empty on purpose.
		_list.MaximumWidth = 820;

		scroll.Canvas = _list;

		var buttons = Layout.AddRow();
		buttons.Margin = new Sandbox.UI.Margin( 12, 6, 12, 10 );
		buttons.Spacing = 6;

		buttons.Add( new Button( "Restart", "replay" )
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
	/// What you see before starting, and after skipping: what this builds, one button to begin,
	/// one to skip, and the opt-out.
	///
	/// The opt-out is here rather than buried in a menu on purpose. A panel that opens itself on
	/// every launch with no visible way to stop it is the thing people resent about tutorials -
	/// and the resentment attaches to the tool, not the tutorial. One checkbox removes the whole
	/// objection for nothing.
	/// </summary>
	private void BuildStartScreen()
	{
		_heading.Text = Title;

		// No step count here. "9 steps" reads as a length to get through, which is the wrong
		// first impression of something meant to take twenty minutes and be enjoyable.
		_progress.Text = "";

		// Separate labels rather than one string with line breaks in it. Escaped newlines have
		// been written into files in this repo as real ones twice, leaving string literals
		// unterminated; separate labels cannot do that.
		AddLine( "You will build a small house: a box for the walls, a wedge for a sloped roof, "
			+ "and holes cut through the walls for windows and a door. It is the smallest model that "
			+ "still needs everything a first session teaches.", 15f, 0.95f );

		_list.Layout.AddSpacingCell( 6f );

		AddLine( "It runs in two phases:", 14f, 0.8f );

		_list.Layout.AddSpacingCell( 4f );

		AddPhase( "THE SHAPE", "two primitives - a box, then a wedge" );
		AddPhase( "THE CUTS", "holes for the windows and the door" );

		_list.Layout.AddSpacingCell( 6f );

		AddLine( "The holes are the part worth noticing. You are not deleting wall to make a window - "
			+ "you are telling the tool to subtract a cylinder, and it re-does that subtraction "
			+ "whenever you resize the house. That is what parametric means, and it is the idea every "
			+ "later tutorial builds on.", 14f, 0.8f );

		_list.Layout.AddSpacingCell( 6f );

		var note = new Editor.Label( "*Steps tick themselves off as you do them - nothing here is locked*" )
		{ WordWrap = true, Color = Theme.TextControl.WithAlpha( 0.65f ) };

		note.SetStyles( "font-size: 13px; font-style: italic;" );
		_list.Layout.Add( note );

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
		checkbox.Value = !EffigyTutorial.OpenOnStartup;
		checkbox.Toggled += () => EffigyTutorial.OpenOnStartup = !checkbox.Value;

		optOut.AddStretchCell();
	}

	/// <summary>One action in a step. The marker is its own fixed-width label so wrapped text
	/// lines up under itself instead of running back under the bullet.</summary>
	private void AddBullet( string text )
	{
		var row = _list.Layout.AddRow();
		row.Margin = new Sandbox.UI.Margin( 4, 0, 0, 0 );
		row.Spacing = 8;

		var marker = new Editor.Label( "•" ) { FixedWidth = 10, Color = Theme.Yellow };
		marker.SetStyles( "font-size: 15px; font-weight: 600;" );
		row.Add( marker );

		var label = new Editor.Label( text ) { WordWrap = true };
		label.SetStyles( "font-size: 14px; line-height: 1.35;" );
		row.Add( label, 1 );
	}

	/// <summary>A paragraph in the start screen. alpha dims it relative to the body text.</summary>
	private void AddLine( string text, float size, float alpha )
	{
		var label = new Editor.Label( text )
		{
			WordWrap = true,
			Color = Theme.TextControl.WithAlpha( alpha )
		};

		label.SetStyles( $"font-size: {size:0}px; line-height: 1.4;" );
		_list.Layout.Add( label );
	}

	/// <summary>One of the three phases: its name in colour, what is in it alongside. A row each
	/// rather than a block of prose, so the shape of the run can be taken in at a glance.</summary>
	private void AddPhase( string name, string contents )
	{
		var row = _list.Layout.AddRow();
		row.Margin = new Sandbox.UI.Margin( 12, 0, 0, 0 );
		row.Spacing = 8;

		var label = new Editor.Label( name ) { Color = Theme.Yellow, FixedWidth = 96 };
		label.SetStyles( "font-size: 14px; font-weight: 600;" );
		row.Add( label );

		var body = new Editor.Label( "-  " + contents )
		{
			WordWrap = true,
			Color = Theme.TextControl.WithAlpha( 0.8f )
		};

		body.SetStyles( "font-size: 14px;" );
		row.Add( body, 1 );
	}

	public void Rebuild()
	{
		_list.Layout.Clear( true );

		if ( Tutorial is null )
			return;

		if ( !Tutorial.Active )
		{
			// Nothing to light up while the start screen is showing. Said explicitly rather than
			// left to fall through: a highlight surviving a Dismiss would sit on the strip with
			// no panel open to explain it.
			HighlightTool?.Invoke( null );
			BuildStartScreen();
			return;
		}

		_heading.Text = Title;

		var index = Math.Min( Tutorial.CurrentIndex, Tutorial.StepCount - 1 );
		var step = Tutorial.StepAt( index );

		if ( Tutorial.CurrentIndex >= Tutorial.StepCount || step is null )
		{
			HighlightTool?.Invoke( null );
			_progress.Text = "done";
			BuildFinishScreen();
			return;
		}

		HighlightTool?.Invoke(
			step.Points == EffigyTutorial.PointAt.Tool ? step.Tool : null );

		_progress.Text = $"step {index + 1} of {Tutorial.StepCount}";

		var header = _list.Layout.AddRow();
		header.Spacing = 12;
		header.Add( new EffigyStepGlyph( this, step.Art, false, true ) );

		var instruction = new Editor.Label( step.Instruction ) { WordWrap = true };
		instruction.SetStyles( "font-weight: 600; font-size: 17px; line-height: 1.3;" );
		header.Add( instruction, 1 );

		_list.Layout.AddSpacingCell( 6f );

		// Actions as bullets, the why as prose underneath. A step is two different things - what
		// to do and why it matters - and running them together means the instructions have to be
		// read to be found. Bullets can be scanned; prose cannot.
		if ( step.Bullets is { Length: > 0 } bullets )
		{
			foreach ( var bullet in bullets )
				AddBullet( bullet );

			_list.Layout.AddSpacingCell( 8f );
		}

		if ( !string.IsNullOrWhiteSpace( step.Detail ) )
		{
			var detail = new Editor.Label( step.Detail )
			{
				WordWrap = true,
				Color = Theme.TextControl.WithAlpha( 0.6f )
			};

			detail.SetStyles( "font-size: 13px; line-height: 1.45;" );
			_list.Layout.Add( detail );
		}

		BuildPointer( step );

		_list.Layout.AddSpacingCell( 12f );

		// NO stretch cell before this. Pinning navigation to the bottom is fine in a tall side
		// dock and awful in a short wide one, where it opens a void between the text and the
		// controls. Following the content directly looks deliberate at any dock size.
		var nav = _list.Layout.AddRow();
		nav.Spacing = 8;
		nav.Alignment = TextFlag.Center;

		var back = new Button( "", "chevron_left" )
		{
			Clicked = () => { Tutorial.Back(); Rebuild(); },
			ToolTip = "Previous step"
		};

		back.Enabled = Tutorial.CanGoBack;
		nav.Add( back );

		nav.Add( new EffigyStepDots( this, Tutorial.StepCount, index ), 1 );

		var forward = new Button( "", "chevron_right" )
		{
			Clicked = () => { Tutorial.Forward(); Rebuild(); },
			ToolTip = "Skip ahead without doing this step"
		};

		forward.Enabled = Tutorial.CanGoForward;
		nav.Add( forward );
	}

	/// <summary>
	/// The "where is that?" affordance, which is a different thing for each kind of target.
	///
	/// A tool is already lit up on the strip by the time this runs, so all that is left is to say
	/// so - a button here would only re-do what the highlight has done. A panel gets a button,
	/// because a dock that is closed cannot be pointed at. A menu gets a plain line of text and
	/// nothing else, because a Menu is built when it opens and does not exist in between, so
	/// there is no widget in the world to highlight.
	/// </summary>
	private void BuildPointer( EffigyTutorial.Step step )
	{
		switch ( step.Points )
		{
			case EffigyTutorial.PointAt.Tool:
				_list.Layout.AddSpacingCell( 6f );

				var lit = new Editor.Label( "The button to press is lit up on the toolbar." )
				{ WordWrap = true, Color = Theme.Yellow.WithAlpha( 0.85f ) };

				lit.SetStyles( "font-size: 13px;" );
				_list.Layout.Add( lit );
				break;

			case EffigyTutorial.PointAt.Panel when !string.IsNullOrWhiteSpace( step.Panel ):
				_list.Layout.AddSpacingCell( 6f );

				var reveal = _list.Layout.AddRow();
				reveal.Add( new Button( $"Show me the {step.Panel} panel", "my_location" )
				{
					Clicked = () => RevealPanel?.Invoke( step.Panel )
				} );
				reveal.AddStretchCell();
				break;

			case EffigyTutorial.PointAt.Menu:
				_list.Layout.AddSpacingCell( 6f );

				var path = new Editor.Label( "This one is in the menu bar along the top." )
				{ WordWrap = true, Color = Theme.TextControl.WithAlpha( 0.5f ) };

				path.SetStyles( "font-size: 13px;" );
				_list.Layout.Add( path );
				break;
		}
	}

	/// <summary>The end of the run. Says what was built and what to do with it, rather than just
	/// stopping - finishing something should feel like finishing something.</summary>
	private void BuildFinishScreen()
	{
		var done = new Editor.Label(
			"That is a house, and it is still a recipe. Change the box and the roof follows; widen "
			+ "the door and the wall re-cuts itself around it. Nothing you did was a one-way edit, "
			+ "which is the whole point of modelling this way - and the next tutorial starts where "
			+ "this one leaves off: drawing the shapes a primitive cannot make." )
		{ WordWrap = true, Color = Theme.Green };

		done.SetStyles( "font-size: 15px; line-height: 1.45;" );
		_list.Layout.Add( done );
		_list.Layout.AddStretchCell();
	}
}

/// <summary>
/// The drawn mark beside a step.
///
/// Painted rather than an image asset: it ships with the code, scales with the panel, follows the
/// theme, and there is no binary to keep in sync. It also carries the step's status - done goes
/// green, current is bright, the rest sit back - so one thing answers both "what is this about"
/// and "where am I".
/// </summary>
internal sealed class EffigyStepGlyph : Widget
{
	private readonly EffigyTutorial.StepArt _art;
	private readonly bool _done;
	private readonly bool _current;

	public EffigyStepGlyph( Widget parent, EffigyTutorial.StepArt art, bool done, bool current )
		: base( parent )
	{
		_art = art;
		_done = done;
		_current = current;

		FixedWidth = 34;
		FixedHeight = 34;
	}

	protected override void OnPaint()
	{
		var color = _done ? Theme.Green : _current ? Theme.Yellow : Theme.TextControl.WithAlpha( 0.35f );
		var center = LocalRect.Center;

		Paint.Antialiasing = true;

		// A soft plate behind the glyph, brightest on the current step - gives the row an anchor
		// for the eye without a heavier highlight on the text.
		Paint.ClearPen();
		Paint.SetBrush( color.WithAlpha( _current ? 0.16f : 0.07f ) );
		Paint.DrawRect( LocalRect, 6f );

		Paint.SetPen( color, _current ? 2f : 1.5f );
		Paint.ClearBrush();

		switch ( _art )
		{
			// A box drawn as a box: a face, and the two edges that give it depth.
			case EffigyTutorial.StepArt.Solid:
				Paint.DrawLine( center + new Vector2( -10, 3 ), center + new Vector2( -10, -5 ) );
				Paint.DrawLine( center + new Vector2( -10, -5 ), center + new Vector2( 4, -5 ) );
				Paint.DrawLine( center + new Vector2( 4, -5 ), center + new Vector2( 4, 3 ) );
				Paint.DrawLine( center + new Vector2( 4, 3 ), center + new Vector2( -10, 3 ) );
				Paint.DrawLine( center + new Vector2( -10, -5 ), center + new Vector2( -4, -10 ) );
				Paint.DrawLine( center + new Vector2( -4, -10 ), center + new Vector2( 10, -10 ) );
				Paint.DrawLine( center + new Vector2( 10, -10 ), center + new Vector2( 4, -5 ) );
				Paint.DrawLine( center + new Vector2( 10, -10 ), center + new Vector2( 10, -2 ) );
				Paint.DrawLine( center + new Vector2( 10, -2 ), center + new Vector2( 4, 3 ) );
				break;

			// A hole through a wall: the opening's ring, and its drilled centre.
			case EffigyTutorial.StepArt.Hole:
				Paint.SetPen( color, _current ? 2f : 1.5f );
				Paint.DrawCircle( center, 8f );
				Paint.ClearPen();
				Paint.SetBrush( color );
				Paint.DrawCircle( center, 2.5f );
				break;

			// An arrow leaving a tray - export.
			case EffigyTutorial.StepArt.Export:
				Paint.DrawLine( center + new Vector2( -10, 4 ), center + new Vector2( -10, 10 ) );
				Paint.DrawLine( center + new Vector2( -10, 10 ), center + new Vector2( 10, 10 ) );
				Paint.DrawLine( center + new Vector2( 10, 10 ), center + new Vector2( 10, 4 ) );
				Paint.DrawLine( center + new Vector2( 0, -10 ), center + new Vector2( 0, 4 ) );
				Paint.SetBrush( color );
				Paint.DrawPolygon(
					center + new Vector2( -5, -3 ),
					center + new Vector2( 5, -3 ),
					center + new Vector2( 0, -11 ) );
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

/// <summary>A row of dots, one per step, filled up to where you are - progress without spending
/// the panel on eight instructions you are not following.</summary>
internal sealed class EffigyStepDots : Widget
{
	private readonly int _count;
	private readonly int _current;

	public EffigyStepDots( Widget parent, int count, int current ) : base( parent )
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
