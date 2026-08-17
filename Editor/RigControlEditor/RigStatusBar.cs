using Editor;
using Marionette;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The strip along the bottom of the window: what you're hovering on the left, the current
/// tutorial step on the right.
///
/// This is the answer to the documentation being overwhelming. The old approach put every
/// explanation on screen at once, before the reader had done anything or had any reason to care
/// about most of it - so it read as a wall and got skipped, which means it may as well not have
/// been written. Same words, delivered one at a time at the moment they're relevant, are
/// something people actually read. Modelled on the in-game PressApp's status bar.
/// </summary>
/// Derives from Editor.StatusBar rather than plain Widget because that's the only type
/// Window.StatusBar accepts, and Window.StatusBar is the only place a DockWindow will put a
/// full-width strip - the dock manager owns everything else in the client area.
internal sealed class RigStatusBar : Editor.StatusBar
{
	/// <summary>Set by whatever the mouse is over. Static because the things that want to explain
	/// themselves - bone dots in a gizmo viewport, keyframes on a custom-painted timeline - are
	/// scattered across widgets that have no reference to this bar and shouldn't need one.</summary>
	public static string Hint { get; private set; }

	private static RigStatusBar _instance;

	public static void Show( string hint )
	{
		if ( Hint == hint )
			return;

		Hint = hint;
		_instance?.Update();
	}

	public static void Clear( string ifMatching = null )
	{
		if ( ifMatching is not null && Hint != ifMatching )
			return;

		Hint = null;
		_instance?.Update();
	}

	public RigTutorial Tutorial { get; set; }

	/// <summary>
	/// A Button that reports itself to the status bar on hover.
	///
	/// s&box's Widget exposes OnMouseEnter/OnMouseLeave as virtuals, not as events, so there's no
	/// way to attach a hint to a stock Button from outside - it has to be a subclass. Hence this:
	/// every control built with it explains itself in the bar, instead of the bar only ever
	/// describing the viewport (which was the complaint - most of the tool was silent).
	/// </summary>
	public sealed class HintButton : Button
	{
		private readonly string _hint;

		public HintButton( string icon, string hint, Action clicked, Widget parent = null ) : base( "", icon, parent )
		{
			_hint = hint;

			// Native tooltip as well as the status bar - the bar is easy to miss while your eyes
			// are on the control you're hovering.
			ToolTip = hint;
			Clicked = clicked;
		}

		// Qualified - unqualified Show/Clear would bind to Widget's own Show(), which does
		// something entirely different.
		protected override void OnMouseEnter()
		{
			base.OnMouseEnter();
			RigStatusBar.Show( _hint );
		}

		protected override void OnMouseLeave()
		{
			base.OnMouseLeave();
			RigStatusBar.Clear( _hint );
		}
	}

	public RigStatusBar( Widget parent ) : base( parent )
	{
		_instance = this;
		FixedHeight = 24;
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		if ( _instance == this )
			_instance = null;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Theme.SurfaceBackground );
		Paint.DrawRect( LocalRect );

		Paint.SetPen( Theme.WindowBackground );
		Paint.DrawLine( new Vector2( 0f, 0f ), new Vector2( Width, 0f ) );

		// Font 9, not 7. This bar is the tool's primary explanation surface - if it's too small to
		// read at a glance it may as well be blank, and 7 was being read as chrome rather than as
		// something addressed to you.
		Paint.SetDefaultFont( 9 );

		var step = Tutorial?.CurrentStep;
		var stepWidth = step is null ? 0f : Width * 0.45f;

		if ( !string.IsNullOrWhiteSpace( Hint ) )
		{
			Paint.SetPen( Theme.TextControl );
			Paint.DrawText( new Rect( 12f, 0f, Width - stepWidth - 24f, Height ), Hint, TextFlag.LeftCenter );
		}
		else if ( step is null )
		{
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.35f ) );
			Paint.DrawText( new Rect( 12f, 0f, Width - 24f, Height ), "Ready", TextFlag.LeftCenter );
		}

		if ( step is null )
			return;

		var index = Tutorial.CurrentIndex + 1;
		var total = Tutorial.StepCount;

		Paint.SetDefaultFont( 9, 500 );
		Paint.SetPen( Theme.Green );
		Paint.DrawText( new Rect( Width - stepWidth - 12f, 0f, stepWidth, Height ),
			$"Tutorial {index}/{total}   {step.Instruction}", TextFlag.RightCenter );
	}
}

/// <summary>
/// A guided build of one real animation - a wave - rather than a description of what the buttons
/// do.
///
/// Steps advance by WATCHING THE DOCUMENT, not by the reader clicking "Next". You can't tick a
/// step off without having actually done it, so nobody arrives at the end having read six things
/// and animated nothing. The pose-to-pose order it walks through (extremes first, in-betweens
/// after) is the order that makes hand-keyed animation read, and doing it once in the right order
/// teaches more than a paragraph saying so.
/// </summary>
internal sealed class RigTutorial
{
	public sealed class Step
	{
		public string Instruction { get; init; }

		/// <summary>True once the reader has actually done this.</summary>
		public Func<RigAnimDocument, string, bool> IsDone { get; init; }
	}

	private readonly List<Step> _steps;

	public RigTutorial()
	{
		_steps = new List<Step>
		{
			new()
			{
				Instruction = "Set Source Model in the BonesObject tab",
				IsDone = ( anim, _ ) => anim?.SourceModel is not null
			},
			new()
			{
				Instruction = "Click a bone in the viewport - try an upper arm",
				IsDone = ( _, bone ) => !string.IsNullOrEmpty( bone )
			},
			new()
			{
				Instruction = "At frame 0, drag the bone to rotate it - this is the arm's rest pose",
				IsDone = ( anim, _ ) => KeyNear( anim, 0, 2 )
			},
			new()
			{
				Instruction = "Move the playhead to ~frame 8 and rotate the arm out to one side",
				IsDone = ( anim, _ ) => KeyAfter( anim, 4 )
			},
			new()
			{
				Instruction = "Now ~frame 16, rotate it across to the other side - that's the wave's second extreme",
				IsDone = ( anim, _ ) => KeyAfter( anim, 12 )
			},
			new()
			{
				Instruction = "Last, near frame 24, bring it back to about the rest pose so it loops",
				IsDone = ( anim, _ ) => KeyAfter( anim, 20 )
			},
			new()
			{
				Instruction = "Press Play. Too slow? Drag the keys closer together - waves are fast",
				IsDone = ( _, _ ) => false
			}
		};
	}

	/// <summary>Any bone keyed within tolerance of a frame - the reader shouldn't have to land on
	/// an exact frame for the tutorial to notice they did the thing.</summary>
	private static bool KeyNear( RigAnimDocument anim, int frame, int tolerance ) =>
		anim?.BoneTracks.Any( t => t.Keyframes.Any( k => Math.Abs( k.Frame - frame ) <= tolerance ) ) ?? false;

	private static bool KeyAfter( RigAnimDocument anim, int frame ) =>
		anim?.BoneTracks.Any( t => t.Keyframes.Any( k => k.Frame >= frame ) ) ?? false;

	public bool Active { get; private set; } = true;

	public int CurrentIndex { get; private set; }

	public int StepCount => _steps.Count;

	public Step CurrentStep => Active && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

	public void Restart()
	{
		Active = true;
		CurrentIndex = 0;
	}

	public void Dismiss() => Active = false;

	/// <summary>Advance past every step already satisfied. Loops rather than stepping once, so a
	/// reader who does three things before looking down isn't left three steps behind.</summary>
	public bool Evaluate( RigAnimDocument anim, string selectedBone )
	{
		if ( !Active )
			return false;

		var moved = false;

		while ( CurrentIndex < _steps.Count && _steps[CurrentIndex].IsDone( anim, selectedBone ) )
		{
			CurrentIndex++;
			moved = true;
		}

		return moved;
	}
}
