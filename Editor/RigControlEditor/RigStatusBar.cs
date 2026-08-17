using Editor;
using Marionette;
using Sandbox;
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

		// HOVER HINTS ONLY. The tutorial used to share this strip and was unreadable in it - one
		// line of text at the bottom of the window, competing with whatever the cursor was over,
		// in a band the eye reads as chrome. It lives in its own dock now, where it can be looked
		// at. This bar does the one thing a status bar is good at: saying what's under the cursor.
		Paint.SetDefaultFont( 9 );

		if ( !string.IsNullOrWhiteSpace( Hint ) )
		{
			Paint.SetPen( Theme.TextControl );
			Paint.DrawText( new Rect( 12f, 0f, Width - 24f, Height ), Hint, TextFlag.LeftCenter );
			return;
		}

		Paint.SetPen( Theme.TextControl.WithAlpha( 0.35f ) );
		Paint.DrawText( new Rect( 12f, 0f, Width - 24f, Height ), "Ready", TextFlag.LeftCenter );
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
	/// <summary>A drawn glyph per step. Painted rather than an image asset so it ships with the
	/// code, scales with the panel and follows the editor theme - and so the tool has no binary
	/// art to keep in sync with anything.</summary>
	public enum StepArt
	{
		Model,
		Bone,
		Rotate,
		Keyframe,
		Play
	}

	public sealed class Step
	{
		public string Instruction { get; init; }

		/// <summary>The why. Instructions tell you what to press; this is the part that means you
		/// still know what you're doing after the tutorial ends.</summary>
		public string Detail { get; init; }

		public StepArt Art { get; init; }

		/// <summary>Dock this step is about, so the panel can offer to open it. Answers "where is
		/// that?", which is the question a written instruction always leaves behind.</summary>
		public string Panel { get; init; }

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
				Instruction = "To pose a bone, you first have to select it",
				Detail = "There are three ways, and they all do the same thing: click the bone's dot in the viewport, click its name in the timeline's left column, or click it in the bone tree in the BonesObject tab. The selected bone turns yellow and gets a gizmo you can drag. Select arm_upper_R - the right shoulder - to carry on.",
				Art = StepArt.Bone,
				IsDone = ( _, bone ) => !string.IsNullOrEmpty( bone )
			},
			new()
			{
				Instruction = "To reach for something, you need something to reach for",
				Detail = "In the BonesObject tab, add a Reference Prop and pick models/lightswitch/lightswitch_plate.vmdl - it ships with Marionette. Place it about arm's length in front of the hand. Posing at a real object beats imagining where one would be.",
				Art = StepArt.Model,
				Panel = "BonesObject",
				IsDone = ( anim, _ ) => anim?.ReferenceProps?.Any( p => p?.Model is not null ) ?? false
			},
			new()
			{
				Instruction = "Every action needs a pose to leave from and come back to",
				Detail = "At frame 0, press K. That keys the pose the arm already has - no posing needed. You'll copy this exact key to the end of the clip later so the whole thing settles back.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyNear( anim, 0, 2 )
			},
			new()
			{
				Instruction = "To stop the reach looking mechanical, wind up before it goes",
				Detail = "Move to frame 6 and rotate arm_upper_R slightly BACK, away from the switch. A few degrees is plenty. This is anticipation - real movement always loads before it fires, and it's the beat most people skip.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyAfter( anim, 3 )
			},
			new()
			{
				Instruction = "Now the reach itself - the pose the whole clip is about",
				Detail = "At frame 14, swing arm_upper_R forward and up, then straighten arm_lower_R. Shoulder first, elbow second: the elbow hangs off the shoulder, so doing it the other way round undoes your own work.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyAfter( anim, 10 )
			},
			new()
			{
				Instruction = "To read as a hand about to press, aim the wrist",
				Detail = "At frame 17, rotate hand_R until the palm faces the switch and the index finger leads. Until now the hand has just been dragged along by the arm, pointing wherever the elbow left it.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyAfter( anim, 15 )
			},
			new()
			{
				Instruction = "Contact should land, not ease in",
				Detail = "At frame 19, curl finger_index_0_R and finger_index_1_R onto the switch. Keep it one or two frames after the reach arrives. Right-click the key and set Interpolation Mode to Stepped if you want it to snap outright.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyAfter( anim, 18 )
			},
			new()
			{
				Instruction = "Nothing heavy stops dead, so let the arm overshoot",
				Detail = "At frame 22, push arm_upper_R two or three degrees PAST the contact pose, then start it back. This is the settle - the other beat people skip, and the reason a limb reads as having weight.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyAfter( anim, 21 )
			},
			new()
			{
				Instruction = "Close the loop so the clip can repeat cleanly",
				Detail = "Right-click your frame 0 key and Copy. Move the playhead to frame 28 and Paste - paste lands at the playhead rather than where it came from, so the arm returns to exactly the pose it started in.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyAfter( anim, 25 )
			},
			new()
			{
				Instruction = "The poses are done - now find the timing",
				Detail = "Press Play. Almost every first animation runs at half the speed it should, so drag the keys closer together and play it again. That comparison teaches more than any amount of re-posing.",
				Art = StepArt.Play,
				Panel = "Timeline",
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

	/// <summary>
	/// Whether the tutorial dock opens itself when the tool starts.
	///
	/// EditorCookie, so it survives restarts and lives nowhere near a document - which panels you
	/// like seeing is a property of you, not of the clip you happen to have open. Same mechanism
	/// the editor's own preview widgets use for their settings.
	/// </summary>
	public static bool OpenOnStartup
	{
		get => EditorCookie.Get( "marionette.tutorial.openonstartup", true );
		set => EditorCookie.Set( "marionette.tutorial.openonstartup", value );
	}

	/// <summary>Starts inactive so the panel shows its start screen first. Nobody should be
	/// dropped into step one of something they never asked for.</summary>
	public bool Active { get; private set; }

	public int CurrentIndex { get; private set; }

	public int StepCount => _steps.Count;

	public Step CurrentStep => Active && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

	/// <summary>Any step by index, so the panel can list the whole run rather than only the one
	/// you're on - seeing what's done and what's left is most of what makes it feel finishable.</summary>
	public Step StepAt( int index ) => index >= 0 && index < _steps.Count ? _steps[index] : null;

	public void Restart()
	{
		Active = true;
		CurrentIndex = 0;
		_furthest = 0;
	}

	public void Dismiss() => Active = false;

	/// <summary>Advance past every step already satisfied. Loops rather than stepping once, so a
	/// reader who does three things before looking down isn't left three steps behind.</summary>
	/// <summary>The furthest step reached, so stepping back doesn't immediately snap forward again.
	/// See Evaluate.</summary>
	private int _furthest;

	public bool CanGoBack => Active && CurrentIndex > 0;

	public bool CanGoForward => Active && CurrentIndex < _steps.Count;

	/// <summary>Step back one. Auto-advance stays out of the way until you catch up again.</summary>
	public void Back()
	{
		if ( !CanGoBack )
			return;

		CurrentIndex--;
	}

	/// <summary>Skip forward without having done the step - some are worth reading and not
	/// following, and a tutorial that can only be advanced by obeying it is a cage.</summary>
	public void Forward()
	{
		if ( !CanGoForward )
			return;

		CurrentIndex++;
		_furthest = Math.Max( _furthest, CurrentIndex );
	}

	public bool Evaluate( RigAnimDocument anim, string selectedBone )
	{
		if ( !Active )
			return false;

		// DON'T FIGHT A MANUAL REWIND. Steps tick off when their condition holds, and those
		// conditions stay true once satisfied - a keyframe at frame 6 is still there afterwards.
		// So stepping back would re-satisfy the step you just left and snap forward again the
		// same frame, making the Back button look broken. While you're behind the furthest point
		// reached, auto-advance stops entirely; it picks up again once you're back at the front.
		if ( CurrentIndex < _furthest )
			return false;

		var moved = false;

		while ( CurrentIndex < _steps.Count && _steps[CurrentIndex].IsDone( anim, selectedBone ) )
		{
			CurrentIndex++;
			moved = true;
		}

		_furthest = Math.Max( _furthest, CurrentIndex );

		return moved;
	}
}
