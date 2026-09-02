using Editor;
using Effigy;
using System;
using System.Collections.Generic;
using System.Linq;

// Effigy.Skeleton, not Sandbox.Skeleton - the same aliasing every other file in this folder does.
using Skeleton = Effigy.Skeleton;

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
	Sketch,
	Extrude,
	Revolve,
	Fillet,
	Shell,
	Subdivide,
	UVProject,
	Sculpt,
}

/// <summary>
/// Everything a tutorial step is allowed to look at when deciding whether it has been done.
///
/// A struct passed in rather than the window handed over, because the difference decides what
/// kind of check a step can write. Given the window a step could call RebuildStudio, open a
/// dialog, or read a field that only exists on Tuesdays - and IsDone runs on every rebuild, so
/// any of that would be a live hazard. Given this, the worst a step can do is ask a question.
///
/// The two booleans are latches the window sets, not things that can be read off the document.
/// Rolling back and rolling forward again leaves a studio identical to one that was never rolled
/// back at all, and a bake writes a file and is over. Neither leaves a trace to inspect, so the
/// only honest way to check them is for the window to remember it saw them happen.
/// </summary>
internal readonly struct EffigyTutorialState
{
	public readonly PartStudio Studio;
	public readonly Skeleton Skeleton;
	public readonly IReadOnlyDictionary<string, string> BodyBoneMap;

	/// <summary>The rollback bar has been dragged up above at least one feature and then put
	/// back. See the struct summary for why this is a latch.</summary>
	public readonly bool RolledBackAndForward;

	/// <summary>A normal map has been baked and written this session.</summary>
	public readonly bool Baked;

	public EffigyTutorialState( PartStudio studio, Skeleton skeleton,
		IReadOnlyDictionary<string, string> bodyBoneMap, bool rolledBackAndForward, bool baked )
	{
		Studio = studio;
		Skeleton = skeleton;
		BodyBoneMap = bodyBoneMap;
		RolledBackAndForward = rolledBackAndForward;
		Baked = baked;
	}

	// --- the vocabulary the steps below are written in ---------------------------------------
	//
	// Every one of these is a question about the SHAPE of the document rather than its numbers.
	// That is deliberate and it is the lesson RigTutorial paid for three times: a check that
	// tests a proxy ticks off when the proxy is true, which is not the same day as when the
	// reader did the thing. "A closed solid exists" is checkable and true; "you drew a 40x20
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
/// The lamp tutorial: nine steps from an empty studio to a rigged model Marionette can pose.
///
/// A DESK LAMP AND NOT A CHARACTER, which is the choice the rest of this file follows from. The
/// lamp is the only subject that earns every stage honestly - a revolved base is a real lathe
/// profile rather than a demonstration of revolve, a shelled shade is the textbook case for
/// shell, and four rigid segments are what a lamp actually is, so binding bodies to bones is the
/// right rig rather than a stand-in for the weight painting that has no editor yet.
///
/// The shape of this class is RigTutorial's, deliberately, down to the auto-advance latch - see
/// Evaluate. What is new is Points: a step can name something on screen for the panel to
/// highlight, because "click Extrude" is a sentence that still leaves you hunting a strip of
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
		Sketch,
		Solid,
		Blend,
		Rollback,
		Unwrap,
		Sculpt,
		Bone,
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
			//  PHASE 1 - GET A SOLID
			//
			//  Every minute spent here is a minute the reader has not yet seen anything happen,
			//  so the first step is a circle and a number and there is a solid on screen. The
			//  parametric argument is true and it can wait; nobody stays for an argument.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Everything starts on a sketch",
				Bullets = new[]
				{
					"Click Sketch, and pick the Top plane",
					"Draw a circle roughly 6 units across",
					"Finish the sketch, then click Extrude and give it 1.5",
				},
				Detail = "That is the lamp's base. A sketch is a plane with curves on it and nothing more - "
					+ "it is the extrude underneath that turns it into something solid.",
				Art = StepArt.Sketch,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Sketch,

				// A clean extrude AND a body with volume. Either alone lies: an extrude that
				// errored is still an ExtrudeFeature sitting in the tree, and a body can exist
				// with no volume at all if the profile never closed.
				IsDone = s => s.HasClean<ExtrudeFeature>() && s.SolidCount >= 1
			},

			new()
			{
				Instruction = "Soften the rim, and meet a refusal",
				Bullets = new[]
				{
					"Click Fillet and pick the base's top edge",
					"Try a radius of 4 first",
					"Then bring it down to 0.3",
				},
				Detail = "4 is deliberately too big and the tool will say so - naming the largest radius that "
					+ "actually fits on this model. Features here refuse with numbers rather than failing "
					+ "silently, and that is worth seeing once on purpose before it happens by accident.",
				Art = StepArt.Blend,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Fillet,
				IsDone = s => s.HasClean<FilletFeature>()
			},

			new()
			{
				Instruction = "Now the two arms",
				Bullets = new[]
				{
					"Sketch a long thin rectangle on the Front plane",
					"Extrude it",
					"Do it once more, for the upper arm",
				},
				Detail = "They do not have to meet neatly. The bones you place later are what makes the lamp "
					+ "articulate, and they do not care whether the parts touch.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Extrude,
				IsDone = s => s.SolidCount >= 3
			},

			new()
			{
				Instruction = "The shade is a revolve, then a shell",
				Bullets = new[]
				{
					"Sketch the shade's outline on the Front plane - a slanted line and a flat top",
					"Click Revolve",
					"Then Shell it, picking the open bottom face",
				},
				Detail = "Revolve spins the profile about its own left edge, like a lathe. Shell hollows the "
					+ "result and leaves whichever face you picked open, which on a lampshade is the whole point.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Revolve,
				IsDone = s => s.HasClean<RevolveFeature>() && s.HasClean<ShellFeature>()
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 2 - CAGE AND SURFACE
			//
			//  The part that justifies the tool existing. If a reader only ever does phase 1 they
			//  have used a worse Onshape; everything that makes this worth building is here.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Subdivide, then get back underneath it",
				Bullets = new[]
				{
					"Click Subdivide and pick the base",
					"Drag the rollback bar in the feature tree up above it",
					"Change the base's radius, then drag the bar back down",
				},
				Detail = "Rolling back switches off everything below the bar, so you are editing the low-poly "
					+ "cage rather than the smoothed result. That cage is what carries the UVs, what receives "
					+ "the sculpt and what gets skinned - subdivision is a feature in the tree precisely so "
					+ "you can always get under it.",
				Art = StepArt.Rollback,
				Points = PointAt.Panel,
				Panel = "Features",
				IsDone = s => s.HasClean<SubdivideFeature>() && s.RolledBackAndForward
			},

			new()
			{
				Instruction = "Lay out UVs - Unwrap, not Box",
				Bullets = new[]
				{
					"Click UV Project and pick the base",
					"Set Mode to Unwrap",
				},
				Detail = "Box and Planar tile on purpose, which means two faces can land on the same texels. "
					+ "A texture survives that; the normal map in the next step does not - it bakes without "
					+ "complaining and comes out wrong wherever they overlap. Unwrap is the only mode a bake "
					+ "can use, and it is not the default.",
				Art = StepArt.Unwrap,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.UVProject,

				// The MODE, not just the feature. A UV Project left on Box is the exact mistake
				// this step exists to prevent, and ticking it off would be worse than no check.
				IsDone = s => s.Clean<UVProjectFeature>().Any( f => f.Mode.Value == "Unwrap" )
			},

			new()
			{
				Instruction = "Sculpt the base, then bake it down",
				Bullets = new[]
				{
					"Click Sculpt and pick the base",
					"Knock a few dents into it with the brush",
					"Bake the normal map",
				},
				Detail = "The base is cast metal, not milled - a few soft dents stop it reading as CAD output. "
					+ "The bake moves that detail onto the low-poly cage as a normal map, so the model you "
					+ "ship stays cheap and still looks like it was cast.",
				Art = StepArt.Sculpt,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Sculpt,
				IsDone = s => s.Baked
					&& s.Clean<SculptFeature>().Any( f => f.Sculpt is { Revision: > 0 } )
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 3 - RIG AND OUT
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Four bones, one per part",
				Bullets = new[]
				{
					"Open the Rig panel and click Add Bone",
					"Click up the lamp: base, lower arm, upper arm, shade",
					"Then select each bone and use Assign Body to pin its part to it",
				},
				Detail = "Assigning a body binds all of it rigidly to one bone, which is exactly right here - "
					+ "a lamp's parts really are rigid and it is the joints between them that move. Anything "
					+ "left unassigned falls back to nearest-bone weighting, which is a guess.",
				Art = StepArt.Bone,
				Points = PointAt.Panel,
				Panel = "Rig",

				// Bones AND every solid accounted for. Four bones with nothing bound to them is a
				// skeleton floating inside an unrigged model, and it looks identical in the
				// viewport to one that works.
				IsDone = s => s.Skeleton is { Count: >= 4 }
					&& s.BodyBoneMap is { Count: >= 4 }
			},

			new()
			{
				Instruction = "Compile it, and take it to Marionette",
				Bullets = new[]
				{
					"File → Compile .vmdl",
					"Open Tools → Marionette and load the lamp",
					"Pose the arms and key them",
				},
				Detail = "That is the whole pipeline in one model: a parametric cage, sculpted detail baked "
					+ "onto it, and a skeleton - which is the point at which this tool's job ends and the "
					+ "animation editor's begins.",
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
	/// which panels you want to see is not a property of the lamp you happen to have open. Its
	/// own key, separate from Rig Control's: dismissing one tutorial is not a statement about
	/// the other.
	///
	/// DEFAULTS TO FALSE. Effigy opens with the feature tree and the viewport and nothing else,
	/// and a tutorial that shows up uninvited is an extra panel to close like any other. It is in
	/// Help > Start Lamp Tutorial and in View > Tutorial, both one click.
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
