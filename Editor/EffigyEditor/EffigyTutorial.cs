using Editor;
using Effigy;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// A feature-strip button a tutorial step can point at.
///
/// ITS OWN ENUM RATHER THAN EffigyWindow.ToolKind, which is private and says in its own comment
/// that it exists to survive hotloads. This lists only the handful of tools the tutorial actually
/// names, so adding a feature to the strip never silently adds a thing the tutorial claims to
/// teach, and the window keeps one switch mapping these onto its own kinds.
/// </summary>
internal enum EffigyToolTarget
{
	Primitive,
	Hole,
}

/// <summary>
/// Everything a tutorial step is allowed to look at when deciding whether it has been done.
///
/// A struct passed in rather than the window handed over, because the difference decides what
/// kind of check a step can write. Given the window a step could call RebuildStudio, open a
/// dialog, or read a field that only exists on Tuesdays - and IsDone runs on every rebuild, so
/// any of that would be a live hazard. Given this, the worst a step can do is ask a question.
/// </summary>
internal readonly struct EffigyTutorialState
{
	public readonly PartStudio Studio;

	public EffigyTutorialState( PartStudio studio )
	{
		Studio = studio;
	}

	// --- the vocabulary the steps below are written in ---------------------------------------
	//
	// Every one of these is a question about the SHAPE of the document rather than its numbers.
	// That is deliberate and it is the lesson RigTutorial paid for three times: a check that
	// tests a proxy ticks off when the proxy is true, which is not the same day as when the
	// reader did the thing. "A solid exists" is checkable and true; "you drew a 40x20
	// rectangle" is neither the point nor something anyone should have to hit.

	/// <summary>Features of one kind that actually ran. A feature carrying an Error produced no
	/// geometry, so counting it as done would wave the reader past a step they have not
	/// completed - and onto one that builds on geometry that is not there.</summary>
	public IEnumerable<T> Clean<T>() where T : Feature =>
		Studio?.Features.OfType<T>().Where( f => f.Error is null && !f.Suppressed )
		?? Enumerable.Empty<T>();

	public bool HasClean<T>() where T : Feature => Clean<T>().Any();

	/// <summary>Bodies that enclose something. A body with no volume is a surface, a sliver, or
	/// the wreckage of a boolean that went wrong, and none of those are "you made a solid".</summary>
	public int SolidCount =>
		Studio?.Bodies.Count( b => MathF.Abs( b.Mesh.SignedVolume() ) > 1e-4f ) ?? 0;
}

/// <summary>
/// The house tutorial: five steps from an empty studio to a small house you can export.
///
/// A HOUSE AND NOTHING MORE, which is the point of the first tutorial in a series. It teaches the
/// one loop every later lesson builds on - put a solid on screen, put a second solid against it,
/// cut openings through them - without touching sketching, subdivision or the rig. Those are each
/// their own later tutorial; this one is meant to be finished in minutes and to leave the reader
/// holding something.
///
/// The shape of this class is RigTutorial's, deliberately, down to the auto-advance latch - see
/// Evaluate. What is new is Points: a step can name something on screen for the panel to
/// highlight, because "click Hole" is a sentence that still leaves you hunting a strip of
/// twenty glyphs.
/// </summary>
internal sealed class EffigyTutorial
{
	/// <summary>
	/// What a step wants the reader to look at.
	///
	/// A CLOSED SET, NOT A COORDINATE. A step can only point at something the tool is able to
	/// find on its own, so a step that points nowhere is visible while it is being written
	/// rather than as an arrow into empty space at runtime. It also means moving, resizing or
	/// rebuilding the strip cannot leave a highlight stranded: nothing here is a position.
	/// </summary>
	public enum PointAt
	{
		None,

		/// <summary>A button on the feature strip, named by the feature it makes. Resolved to a
		/// live button by the window on every refresh - never held - because RefreshToolStrip
		/// rebuilds the strip as the document changes and a kept reference goes stale the moment
		/// the first sketch exists.</summary>
		Tool,

		/// <summary>A dock, by the title it was registered under. The panel offers to open and
		/// raise it, which is the honest answer to "where is that".</summary>
		Panel,

		/// <summary>Somewhere in the menu bar. NOTHING CAN BE HIGHLIGHTED HERE and the step text
		/// has to carry the whole path itself: a Menu is built fresh every time it opens and does
		/// not exist in between, so there is no widget to point at. Kept as its own case rather
		/// than as None so the limit is stated where a step author will read it.</summary>
		Menu,
	}

	/// <summary>A drawn glyph per step, painted rather than shipped as art - same reasoning as
	/// RigTutorial.StepArt, and the same reason EffigyIcons exists at all.</summary>
	public enum StepArt
	{
		Solid,
		Hole,
		Export,
	}

	public sealed class Step
	{
		public string Instruction { get; init; }

		/// <summary>What you DO, one per bullet, scannable without reading a sentence.</summary>
		public string[] Bullets { get; init; }

		/// <summary>The why, in a line or two. The part that means you still know what you are
		/// doing after the tutorial is over.</summary>
		public string Detail { get; init; }

		public StepArt Art { get; init; }

		public PointAt Points { get; init; }

		/// <summary>Which tool the strip should highlight, when Points is Tool.</summary>
		public EffigyToolTarget Tool { get; init; }

		/// <summary>Which dock to offer, when Points is Panel.</summary>
		public string Panel { get; init; }

		/// <summary>True once the reader has actually done this.</summary>
		public Func<EffigyTutorialState, bool> IsDone { get; init; }
	}

	private readonly List<Step> _steps;

	public EffigyTutorial()
	{
		_steps = new List<Step>
		{
			// ---------------------------------------------------------------------------------
			//  PHASE 1 - THE SHAPE
			//
			//  The whole lesson is "a solid, then another solid, then a cut", so the first
			//  minutes put two primitives on screen and the rest is openings.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "The walls are one box",
				Bullets = new[]
				{
					"Click Primitive and pick Box from its chevron",
					"Set Width 8, Depth 6 and Height 4",
				},
				Detail = "A box is a primitive - a whole solid made from numbers rather than drawn. "
					+ "Starting from one is the fast route when the shape is already a cube; sketching "
					+ "is for the shapes that are not.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Primitive,

				// A clean primitive AND a body with volume. Either alone lies: a primitive that
				// errored is still a PrimitiveFeature sitting in the tree, and a body can exist
				// with no volume at all if the shape collapsed.
				IsDone = s => s.HasClean<PrimitiveFeature>() && s.SolidCount >= 1
			},

			new()
			{
				Instruction = "A wedge for the sloped roof",
				Bullets = new[]
				{
					"Click Primitive again and pick Wedge",
					"Match the house - Width 8, Depth 6, Height 2",
					"Lift it onto the roof line: set Position's Z to 3",
				},
				Detail = "The wedge is a ramp, and its two ends are triangles - the sloped roof in "
					+ "cross-section. Primitives cannot rotate, so the slope always runs along X. The "
					+ "classic peaked roof is two wedges back to back, and that is a later lesson "
					+ "about Mirror.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Primitive,
				IsDone = s => s.Clean<PrimitiveFeature>().Any( f => f.Shape.Value == "Wedge" )
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 2 - THE OPENINGS
			//
			//  The holes are the part worth noticing: they are not deletions, they are subtractions
			//  the tool re-runs whenever the house changes.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Cut the windows",
				Bullets = new[]
				{
					"Click Hole and pick the front face of the walls",
					"Set Diameter to about 0.8 and leave Depth at 0 (through)",
					"Pick a second spot, and the hole follows",
				},
				Detail = "A hole is a subtract, not a delete. It drills a cylinder into the face "
					+ "along that face's own normal, straight through to the other side at depth 0. "
					+ "Make two windows now, both on the same face.",
				Art = StepArt.Hole,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Hole,
				IsDone = s => s.HasClean<HoleFeature>()
			},

			new()
			{
				Instruction = "And the door",
				Bullets = new[]
				{
					"Click Hole again on the face below the windows",
					"Give it a wider Diameter, around 1.2",
				},
				Detail = "The door is the same tool with a bigger number, which is the point. You are "
					+ "not drawing openings - you are describing them, and a door is just a wider "
					+ "cylinder. Change the house later and both the windows and the door re-cut "
					+ "themselves, because the recipe remembers what they are.",
				Art = StepArt.Hole,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Hole,

				// Two openings, whatever features they live in. The reader might drill windows and
				// door with one Hole feature or two, and the check must not care which.
				IsDone = s => s.Clean<HoleFeature>().Sum( f => f.Faces.Count ) >= 2
			},

			new()
			{
				Instruction = "Take the house with you",
				Bullets = new[]
				{
					"File → Export OBJ",
					"Open it in whatever you like - it is a real mesh",
				},
				Detail = "Everything up to here was a recipe, and the recipe is what makes it "
					+ "editable: change the box, and the roof and the holes all follow. Export writes "
					+ "the current shape out as a mesh, for anything that does not care how it was made.",
				Art = StepArt.Export,

				// The one place nothing can be highlighted - a Menu does not exist between
				// openings. Hence the full path spelled out in the bullet.
				Points = PointAt.Menu,

				// The last step is not a checkbox. Same as RigTutorial's "find the timing":
				// finishing is something the reader decides, not something a predicate notices.
				IsDone = _ => false
			},
		};
	}

	/// <summary>
	/// Whether the tutorial dock opens itself when Effigy starts.
	///
	/// EditorCookie, so it survives restarts and belongs to the person rather than the document -
	/// which panels you want to see is not a property of the house you happen to have open. Its
	/// own key, separate from Rig Control's: dismissing one tutorial is not a statement about
	/// the other.
	///
	/// DEFAULTS TO FALSE. Effigy opens with the feature tree and the viewport and nothing else,
	/// and a tutorial that shows up uninvited is an extra panel to close like any other. It is in
	/// Help > Start House Tutorial and in View > Tutorial, both one click.
	/// </summary>
	public static bool OpenOnStartup
	{
		get => EditorCookie.Get( "effigy.tutorial.openonstartup", false );
		set => EditorCookie.Set( "effigy.tutorial.openonstartup", value );
	}

	/// <summary>Starts inactive so the panel shows its start screen first. Nobody should be
	/// dropped into step one of something they never asked for.</summary>
	public bool Active { get; private set; }

	public int CurrentIndex { get; private set; }

	public int StepCount => _steps.Count;

	public Step CurrentStep => Active && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

	public Step StepAt( int index ) => index >= 0 && index < _steps.Count ? _steps[index] : null;

	/// <summary>The furthest step reached, so stepping back does not immediately snap forward
	/// again. See Evaluate.</summary>
	private int _furthest;

	public void Restart()
	{
		Active = true;
		CurrentIndex = 0;
		_furthest = 0;
	}

	public void Dismiss() => Active = false;

	public bool CanGoBack => Active && CurrentIndex > 0;

	public bool CanGoForward => Active && CurrentIndex < _steps.Count;

	public void Back()
	{
		if ( !CanGoBack )
			return;

		CurrentIndex--;
	}

	/// <summary>Skip forward without having done the step. Some are worth reading and not
	/// following, and a tutorial that can only be advanced by obeying it is a cage.</summary>
	public void Forward()
	{
		if ( !CanGoForward )
			return;

		CurrentIndex++;
		_furthest = Math.Max( _furthest, CurrentIndex );
	}

	/// <summary>
	/// Advance past every step already satisfied, and say whether anything moved.
	///
	/// DOES NOT FIGHT A MANUAL REWIND. These conditions stay true once satisfied - a filleted
	/// edge is still filleted afterwards - so stepping back would re-satisfy the step just left
	/// and snap forward again on the same rebuild, making the Back button look broken. While the
	/// reader is behind the furthest point they reached, auto-advance stops entirely and picks up
	/// once they are back at the front. Straight from RigTutorial, where it was a real bug.
	///
	/// Loops rather than stepping once, so someone who does three things before looking down is
	/// not left three steps behind.
	/// </summary>
	public bool Evaluate( in EffigyTutorialState state )
	{
		if ( !Active )
			return false;

		if ( CurrentIndex < _furthest )
			return false;

		var moved = false;

		while ( CurrentIndex < _steps.Count && _steps[CurrentIndex].IsDone( state ) )
		{
			CurrentIndex++;
			moved = true;
		}

		_furthest = Math.Max( _furthest, CurrentIndex );

		return moved;
	}
}
