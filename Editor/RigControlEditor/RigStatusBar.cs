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
				Instruction = "Set Source Model in the BonesObject tab",
				Detail = "Any skinned model works. If you have nothing in mind, the Citizen is installed with s&box and is rigged.",
				Art = StepArt.Model,
				Panel = "BonesObject",
				IsDone = ( anim, _ ) => anim?.SourceModel is not null
			},
			new()
			{
				Instruction = "Click a bone dot in the viewport - try an upper arm",
				Detail = "Bones draw through the mesh, so the ones inside the model are still clickable. Work big bones first: a shoulder carries the elbow and hand with it, so posing a hand and then moving the shoulder throws the hand away.",
				Art = StepArt.Bone,
				IsDone = ( _, bone ) => !string.IsNullOrEmpty( bone )
			},
			new()
			{
				Instruction = "At frame 0, drag the bone to rotate it - this is the arm's rest pose",
				Detail = "Dragging rotates by default, because joints pivot rather than slide. Hold E if you genuinely need to move one. Rest pose first: it's the shape the wave starts and ends on.",
				Art = StepArt.Rotate,
				IsDone = ( anim, _ ) => KeyNear( anim, 0, 2 )
			},
			new()
			{
				Instruction = "Move the playhead to ~frame 8 and rotate the arm out to one side",
				Detail = "This is an extreme - one of the two poses the wave swings between. Getting both extremes down before anything else is how animation is built; the in-between takes care of itself.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyAfter( anim, 4 )
			},
			new()
			{
				Instruction = "Now ~frame 16, rotate it across to the other side - the second extreme",
				Detail = "Two extremes and the shape of the motion exists. Press Play now if you like - it'll be floaty, but you'll see whether the idea reads.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyAfter( anim, 12 )
			},
			new()
			{
				Instruction = "Last, near frame 24, bring it back to about the rest pose so it loops",
				Detail = "Ending where you started is what lets a clip repeat without a visible jump. Right-click that first key and Copy, then paste it here to land exactly back.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyAfter( anim, 20 )
			},
			new()
			{
				Instruction = "Press Play. Too slow? Drag the keys closer together - waves are fast",
				Detail = "Most first animations are half the speed they should be. A whole wave usually wants well under a second. Preview at x0.25 from the timeline if you need to see what the timing is really doing.",
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
