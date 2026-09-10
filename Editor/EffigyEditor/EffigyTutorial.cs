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
	AddBone,
	BoneFromPart,
	Subdivide,
	Sculpt,
	Paint,
}

/// <summary>Which first-run lesson is on the panel. House is CAD; Rigging is the smallest
/// loop that still needs a bone, an assignment and a pose; Hand walks the four workspaces
/// on one model.</summary>
internal enum EffigyLesson
{
	House,
	Rigging,
	Hand,
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
	public readonly EffigyWorkspace Workspace;
	public readonly bool Posing;

	public EffigyTutorialState( PartStudio studio, EffigyWorkspace workspace = EffigyWorkspace.Cad,
		bool posing = false )
	{
		Studio = studio;
		Workspace = workspace;
		Posing = posing;
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

	/// <summary>Bodies a "make a bone from this part" click would actually accept. A cube has
	/// no long axis and is skipped, so a tutorial that asks for a post has to count measurable
	/// parts, not merely solids.</summary>
	public int MeasurableBodies =>
		Studio?.Bodies.Count( b => BoneFromBody.TryDerive( b.Mesh, out _, out _, out _, null ) ) ?? 0;

	public int BoneCount => Studio?.Rig?.Count ?? 0;

	public int AssignedBodies => Studio?.BodyBoneMap?.Count ?? 0;

	/// <summary>Bones that hang off another bone. A hand whose every bone is a root will not
	/// curl a finger when the palm turns.</summary>
	public int ChildBones =>
		Studio?.Rig?.Bones.Count( b => b.Parent >= 0 ) ?? 0;
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

		/// <summary>A workspace pill on the switcher. The panel offers a button that switches,
		/// because a highlight on a bar the reader is not looking at is a highlight on nothing.</summary>
		Workspace,
	}

	/// <summary>A drawn glyph per step, painted rather than shipped as art - same reasoning as
	/// RigTutorial.StepArt, and the same reason EffigyIcons exists at all.</summary>
	public enum StepArt
	{
		Solid,
		Hole,
		Export,
		Bone,
		Pose,
		Sculpt,
		Paint,
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

		/// <summary>Which workspace pill to offer, when Points is Workspace.</summary>
		public EffigyWorkspace Workspace { get; init; }

		/// <summary>True once the reader has actually done this.</summary>
		public Func<EffigyTutorialState, bool> IsDone { get; init; }
	}

	private readonly List<Step> _house;
	private readonly List<Step> _rigging;
	private readonly List<Step> _hand;
	private List<Step> _steps;

	public EffigyLesson Lesson { get; private set; } = EffigyLesson.House;

	public string Title => Lesson switch
	{
		EffigyLesson.Rigging => "Rig a Signpost",
		EffigyLesson.Hand => "Make a Hand",
		_ => "Build a House",
	};

	public EffigyTutorial()
	{
		_house = HouseSteps();
		_rigging = RiggingSteps();
		_hand = HandSteps();
		_steps = _house;
	}

	static List<Step> HouseSteps() => new()
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

	static List<Step> RiggingSteps() => new()
		{
			// ---------------------------------------------------------------------------------
			//  PHASE 1 - THE PARTS
			//
			//  Two solids, each with a long axis, so "make a bone from this part" has something
			//  to measure. A cube is skipped, which is the whole reason these are a post and a
			//  sign rather than two default boxes.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "A tall post",
				Bullets = new[]
				{
					"Click Primitive and pick Box",
					"Set Width 0.5, Depth 0.5, Height 6",
				},
				Detail = "A bone is measured along the longest axis of a part. A cube has none, "
					+ "and Make a bone from this part will skip it. Tall is the whole point.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Primitive,
				IsDone = s => s.HasClean<PrimitiveFeature>() && s.MeasurableBodies >= 1
			},

			new()
			{
				Instruction = "A flat sign on it",
				Bullets = new[]
				{
					"Click Primitive again and pick Box",
					"Set Width 3, Height 2, Depth 0.3",
					"Lift it onto the post: set Position's Z to 2.5",
				},
				Detail = "Two bodies, not one box with a second box merged in. Each part will get "
					+ "its own bone, and posing one will leave the other standing - which is how "
					+ "you can see that the rig is doing anything at all.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Primitive,
				IsDone = s => s.SolidCount >= 2 && s.MeasurableBodies >= 2
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 2 - THE BONES
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Switch to the Rig workspace",
				Bullets = new[]
				{
					"Click Rig on the bar above the tools",
				},
				Detail = "CAD is where parts are made. Rig is where they get a skeleton. The two "
					+ "are meant to feel like the same tool in a different mode, not like a second "
					+ "window.",
				Art = StepArt.Bone,
				Points = PointAt.Workspace,
				Workspace = EffigyWorkspace.Rig,
				IsDone = s => s.Workspace == EffigyWorkspace.Rig
			},

			new()
			{
				Instruction = "A bone down the post",
				Bullets = new[]
				{
					"Click the tall part — in the viewport or the Parts list",
					"Press Bone from Part on the bar",
				},
				Detail = "The bone is measured along the post and pinned to it, already named after "
					+ "the part. Right-click the part and pick Make a bone from this part does the "
					+ "same thing. You should see the bone running through the post.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.BoneCount >= 1 && s.AssignedBodies >= 1
			},

			new()
			{
				Instruction = "Hang the sign off that bone",
				Bullets = new[]
				{
					"Click the post's bone in the Rig tree so it is selected",
					"Select the sign, then Bone from Part again",
				},
				Detail = "A selected bone is the parent of the next one. The sign's bone hangs off "
					+ "the post's, pointing away from it. That chain is the whole of a rig. Assign "
					+ "Body is the other button: it pins a part to a bone that already exists, "
					+ "without making a new one.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.BoneCount >= 2 && s.AssignedBodies >= 2
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 3 - THE POSE
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Pose it and watch it swing",
				Bullets = new[]
				{
					"Press Pose in the Rig panel",
					"Drag the sign's bone",
				},
				Detail = "If the sign swings and the post stays, the assignment is right. If the "
					+ "whole model rotates as one, a part is still pinned to the wrong bone. "
					+ "Pose is a scratchpad - Reset Pose or toggling Pose off puts it back.",
				Art = StepArt.Pose,
				Points = PointAt.Panel,
				Panel = "Rig",
				IsDone = s => s.Posing
			},

			new()
			{
				Instruction = "Compile it",
				Bullets = new[]
				{
					"File → Compile .vmdl",
				},
				Detail = "That writes a skinned model you can drop in a scene or open in "
					+ "Marionette. The recipe stays in the .effigy - change a box and compile "
					+ "again, and the bones follow the parts.",
				Art = StepArt.Export,
				Points = PointAt.Menu,
				IsDone = _ => false
			},
		};

	static List<Step> HandSteps() => new()
		{
			// ---------------------------------------------------------------------------------
			//  PHASE 1 - THE PARTS
			//
			//  Separate bodies, not one merged mesh. Each phalanx is its own part so Bone from
			//  Part has something to measure, and posing one joint leaves the others standing.
			//  Cubes are skipped, so every box here is longer in one axis than the other two.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "The palm",
				Bullets = new[]
				{
					"Click Primitive and pick Box",
					"Set Width 2.5, Depth 1, Height 3",
				},
				Detail = "Height is the long axis, wrist to knuckles, so a bone down the palm has "
					+ "something to measure. A cube would be skipped. Leave it at the origin.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Primitive,
				IsDone = s => s.HasClean<PrimitiveFeature>() && s.MeasurableBodies >= 1
			},

			new()
			{
				Instruction = "Index finger, three joints",
				Bullets = new[]
				{
					"Another Box: Width 0.6, Depth 0.6, Height 1.4. Position X 0.8, Z 2.2",
					"The middle: 0.5 × 0.5 × 1.1 at X 0.8, Z 3.5",
					"The tip: 0.45 × 0.45 × 0.8 at X 0.8, Z 4.4",
				},
				Detail = "Three parts, not one long finger. Each will get its own bone, and that "
					+ "is how a finger curls instead of waving as a stick. Keep them in a line up Z.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Primitive,
				IsDone = s => s.SolidCount >= 4 && s.MeasurableBodies >= 4
			},

			new()
			{
				Instruction = "The other fingers, and a thumb",
				Bullets = new[]
				{
					"Three more 0.6 × 0.6 × 1.4 boxes at Z 2.2, X 0.2, then −0.4, then −1.0",
					"A thumb: 0.7 × 0.7 × 1.5 at X 1.3, Y 0.5, Z 0.3",
				},
				Detail = "One box per remaining digit is enough for this lesson. A production hand "
					+ "gives every joint three segments the way the index already has. The thumb "
					+ "sits off to the side so it is not another finger in a row.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Primitive,
				IsDone = s => s.SolidCount >= 8 && s.MeasurableBodies >= 8
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 2 - SCULPT
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Switch to Sculpt",
				Bullets = new[]
				{
					"Click Sculpt on the bar above the tools",
				},
				Detail = "A Sculpt carries its own levels — you do not Subdivide first. Subdivide "
					+ "is for smoothing a part you are not going to brush.",
				Art = StepArt.Sculpt,
				Points = PointAt.Workspace,
				Workspace = EffigyWorkspace.Sculpt,
				IsDone = s => s.Workspace == EffigyWorkspace.Sculpt
			},

			new()
			{
				Instruction = "Brush some knuckles",
				Bullets = new[]
				{
					"Click the palm in the Parts list",
					"Press Sculpt, then drag on the surface",
				},
				Detail = "Draw pushes out, Ctrl inverts and carves. The levels are the cage: drop "
					+ "down to work broadly, step up for the crease of a knuckle. Finish when the "
					+ "palm no longer reads as a box.",
				Art = StepArt.Sculpt,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Sculpt,
				IsDone = s => s.HasClean<SculptFeature>()
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 3 - PAINT
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Switch to Paint",
				Bullets = new[]
				{
					"Click Paint on the bar above the tools",
				},
				Detail = "Colour is a texture atlas, not vertex paint. If the part has no UVs that "
					+ "can hold a stroke, Paint inserts an Unwrap for you.",
				Art = StepArt.Paint,
				Points = PointAt.Workspace,
				Workspace = EffigyWorkspace.Paint,
				IsDone = s => s.Workspace == EffigyWorkspace.Paint
			},

			new()
			{
				Instruction = "Paint the skin",
				Bullets = new[]
				{
					"Select a part, press Paint",
					"Pick a colour and drag",
				},
				Detail = "One body at a time. Repeat on the fingers if you want, or leave them: "
					+ "the lesson is that paint survives a rebuild, not that every phalanx is "
					+ "shaded. Ctrl erases.",
				Art = StepArt.Paint,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Paint,
				IsDone = s => s.HasClean<PaintFeature>()
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 4 - THE BONES
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Switch to Rig",
				Bullets = new[]
				{
					"Click Rig on the bar above the tools",
				},
				Detail = "The parts are done. A skeleton is one bone per part, parented so a "
					+ "finger hangs off the palm and a tip hangs off the middle joint.",
				Art = StepArt.Bone,
				Points = PointAt.Workspace,
				Workspace = EffigyWorkspace.Rig,
				IsDone = s => s.Workspace == EffigyWorkspace.Rig
			},

			new()
			{
				Instruction = "A bone down the palm",
				Bullets = new[]
				{
					"Click the palm — viewport or Parts list",
					"Press Bone from Part",
				},
				Detail = "This is the root of the hand. Everything else hangs off it, the way a "
					+ "trigger hangs off a gun's root. Select it and leave it selected.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.BoneCount >= 1 && s.AssignedBodies >= 1
			},

			new()
			{
				Instruction = "Chain the index finger",
				Bullets = new[]
				{
					"With the palm bone selected, Bone from Part on the first joint",
					"Select that new bone, then Bone from Part on the middle, then the tip",
				},
				Detail = "A selected bone is the parent of the next one. Palm → knuckle → middle "
					+ "→ tip is a finger. If a bone came out as its own root, right-click it in "
					+ "the Rig tree and Parent to the one it should hang off.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.BoneCount >= 4 && s.ChildBones >= 3
			},

			new()
			{
				Instruction = "Hang the other digits off the palm",
				Bullets = new[]
				{
					"Select the palm bone",
					"Bone from Part on each remaining finger, and the thumb",
				},
				Detail = "They all parent to the palm because they are one-bone digits. A "
					+ "three-joint finger on each would parent joint-to-joint the way the index "
					+ "already does. Pose the palm and every child should follow.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.BoneCount >= 8 && s.AssignedBodies >= 8 && s.ChildBones >= 7
			},

			new()
			{
				Instruction = "Curl a finger",
				Bullets = new[]
				{
					"Press Pose in the Rig panel",
					"Drag the index tip, then the palm",
				},
				Detail = "The tip should curl without taking the palm with it. The palm should "
					+ "carry the whole hand. If a finger stays behind, it is still a root — Parent "
					+ "to the palm. Pose is a scratchpad; Reset Pose puts the bind back.",
				Art = StepArt.Pose,
				Points = PointAt.Panel,
				Panel = "Rig",
				IsDone = s => s.Posing
			},

			new()
			{
				Instruction = "Compile it",
				Bullets = new[]
				{
					"File → Compile .vmdl",
				},
				Detail = "A skinned hand you can drop in a scene or open in Marionette. The "
					+ "recipe stays in the .effigy: change a box, compile again, and the bones "
					+ "follow the parts.",
				Art = StepArt.Export,
				Points = PointAt.Menu,
				IsDone = _ => false
			},
		};

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

	public void Restart( EffigyLesson lesson )
	{
		Lesson = lesson;
		_steps = lesson switch
		{
			EffigyLesson.Rigging => _rigging,
			EffigyLesson.Hand => _hand,
			_ => _house,
		};
		Active = true;
		CurrentIndex = 0;
		_furthest = 0;
	}

	public int CurrentIndex { get; private set; }

	public int StepCount => _steps.Count;

	public Step CurrentStep => Active && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

	public Step StepAt( int index ) => index >= 0 && index < _steps.Count ? _steps[index] : null;

	/// <summary>The furthest step reached, so stepping back does not immediately snap forward
	/// again. See Evaluate.</summary>
	private int _furthest;

	public void Restart() => Restart( Lesson );

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
