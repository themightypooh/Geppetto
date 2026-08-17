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
				Instruction = "Click the arm_upper_R dot in the viewport",
				Detail = "The right shoulder. It carries the whole arm, so it goes first - always work outward: shoulder, elbow, wrist, finger. Right-click camera or weapon_root and pick Hide Bone And Children to clear the clutter.",
				Art = StepArt.Bone,
				IsDone = ( _, bone ) => !string.IsNullOrEmpty( bone )
			},
			new()
			{
				Instruction = "Add a Reference Prop and put it where the switch would be",
				Detail = "BonesObject tab. Any model does - a crate is fine. Arm's length out in front. Now you're reaching at something instead of guessing.",
				Art = StepArt.Model,
				Panel = "BonesObject",
				IsDone = ( anim, _ ) => anim?.ReferenceProps?.Any( p => p?.Model is not null ) ?? false
			},
			new()
			{
				Instruction = "REST - at frame 0, press K",
				Detail = "Keys the pose it already has. Every action needs somewhere to leave from and come back to.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyNear( anim, 0, 2 )
			},
			new()
			{
				Instruction = "ANTICIPATION - frame 6, rotate arm_upper_R slightly BACK",
				Detail = "Wind up before you go. A few degrees is plenty. Skip it and the whole thing reads as a machine.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyAfter( anim, 3 )
			},
			new()
			{
				Instruction = "EXTREME - frame 14, swing arm_upper_R forward, then straighten arm_lower_R",
				Detail = "The reach itself. Shoulder first, elbow second - the elbow hangs off the shoulder, so the other order undoes your own work.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyAfter( anim, 10 )
			},
			new()
			{
				Instruction = "Frame 17 - rotate hand_R so the index finger leads",
				Detail = "The wrist aims the hand. This is what turns a flailing limb into a hand about to press something.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyAfter( anim, 15 )
			},
			new()
			{
				Instruction = "CONTACT - frame 19, curl finger_index_0_R and finger_index_1_R",
				Detail = "One or two frames after the reach lands, no more. Right-click the key and set Stepped if you want it to snap.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyAfter( anim, 18 )
			},
			new()
			{
				Instruction = "SETTLE - frame 22, let it drift slightly PAST, then start back",
				Detail = "Nothing heavy stops dead. Two or three degrees past is enough - you feel it more than you see it.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyAfter( anim, 21 )
			},
			new()
			{
				Instruction = "RETURN - frame 28, back to the rest pose",
				Detail = "Right-click your frame 0 key, Copy, then Paste here. Paste lands at the playhead, so it comes back exact.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyAfter( anim, 25 )
			},
			new()
			{
				Instruction = "Press Play, then tighten it up",
				Detail = "Most first animations run at half speed. Drag the keys closer together and play it again - that's the whole lesson.",
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
