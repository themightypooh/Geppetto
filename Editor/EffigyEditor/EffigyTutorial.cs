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
	Playermodel,
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

	// --- the playermodel lesson's vocabulary ---------------------------------------------------
	//
	// NAMES, NOT COUNTS, which is the one place this lesson differs from every other. Elsewhere a
	// step is done when there are enough bones; here a bone called `upperarm_L` hanging off one
	// called `clavicle_L` is the entire point, and a count would tick off a rig that is correctly
	// shaped and spelled in a way citizen's animations have never heard of.

	public bool HasBone( string name ) => (Studio?.Rig?.IndexOf( name ) ?? -1) >= 0;

	/// <summary>The name of a bone's parent, "" for a root, null when there is no such bone. Three
	/// answers rather than two, because "it is a root" and "it does not exist yet" are different
	/// states and the first step of the lesson turns on telling them apart.</summary>
	public string ParentOf( string name )
	{
		var rig = Studio?.Rig;
		var index = rig?.IndexOf( name ) ?? -1;

		if ( index < 0 )
			return null;

		var parent = rig.Bones[index].Parent;

		return parent < 0 ? "" : rig.Bones[parent].Name;
	}

	/// <summary>Every bone in the list exists, and each hangs off the one before it. The first name
	/// is only required to exist - it is a bone an earlier step already made, and whether IT has a
	/// parent is that step's business.</summary>
	public bool Chain( params string[] names )
	{
		if ( names is null || names.Length == 0 )
			return false;

		if ( !HasBone( names[0] ) )
			return false;

		for ( var i = 1; i < names.Length; i++ )
		{
			if ( !string.Equals( ParentOf( names[i] ), names[i - 1], StringComparison.OrdinalIgnoreCase ) )
				return false;
		}

		return true;
	}

	/// <summary>The lowest point of the model, for "is it standing on the floor". float.MaxValue
	/// when there is no geometry at all, so a check reading this never mistakes an empty document
	/// for a model resting at zero.</summary>
	public float LowestPoint
	{
		get
		{
			var lowest = float.MaxValue;

			if ( Studio is null )
				return lowest;

			foreach ( var body in Studio.Bodies )
			{
				foreach ( var p in body.Mesh.Positions )
				{
					if ( p.z < lowest )
						lowest = p.z;
				}
			}

			return lowest;
		}
	}
}

/// <summary>
/// The house tutorial: from never having used CAD to a small house you can export.
///
/// A HOUSE AND NOTHING MORE, which is the point of the first tutorial in a series. It teaches the
/// one loop every later lesson builds on - put a solid on screen, put a second solid against it,
/// cut openings through them - without touching sketching, subdivision or the rig. Those are each
/// their own later tutorial; this one is meant to be finished in minutes and to leave the reader
/// holding something.
///
/// WRITTEN FOR SOMEONE WHO HAS NEVER TOUCHED CAD. Every bullet is one exact action - what to click,
/// where it is, what to type - and every Detail says what just happened, in a friendly voice. No
/// tours of the toolset: a first-timer needs the next click, not the map. One Reading step for the
/// camera comes first, because nothing else works until you can look at the model.
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
		Walk,

		/// <summary>The window itself - a frame with a side column and a bar. For the steps that
		/// teach the tool rather than the model.</summary>
		Screen,
	}

	public sealed class Step
	{
		public string Instruction { get; init; }

		/// <summary>
		/// A step to read rather than do - where things are, how the camera moves. The panel gives it
		/// a Next button, because there is nothing for IsDone to notice and the chevron alone reads as
		/// "skip", which is the wrong word for having read it.
		/// </summary>
		public bool Reading { get; init; }

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
	private readonly List<Step> _playermodel;
	private List<Step> _steps;

	public EffigyLesson Lesson { get; private set; } = EffigyLesson.House;

	public string Title => Lesson switch
	{
		EffigyLesson.Rigging => "Rig a Signpost",
		EffigyLesson.Hand => "Make a Hand",
		EffigyLesson.Playermodel => "Make a Playermodel",
		_ => "Build a House",
	};

	public EffigyTutorial()
	{
		_house = HouseSteps();
		_rigging = RiggingSteps();
		_hand = HandSteps();
		_playermodel = PlayermodelSteps();
		_steps = _house;
	}

	static List<Step> HouseSteps() => new()
		{
			// ---------------------------------------------------------------------------------
			//  PHASE 1 - SAY HI
			//
			//  ONE reading step, about the camera and nothing else. An earlier draft opened with a
			//  tour of every tab and workspace, and po's answer was that a first-timer does not
			//  want the whole toolset - they want the next click. So every step below names
			//  exactly what to click, where it is, what to type, and what just happened.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Take a look around",
				Bullets = new[]
				{
					"The big space in the middle is where your house will appear",
					"Hold the right mouse button down and move the mouse - the view turns",
					"Still holding it, tap S to back away a little, or W to move closer",
					"Lost? Click View in the menu at the very top, then Isometric",
				},
				Detail = "That's all the camera you need today! The red, green and blue lines crossing "
					+ "in the middle are the three directions every size gets measured along - you'll "
					+ "meet them in the very next step.",
				Art = StepArt.Screen,
				Reading = true,
				IsDone = _ => false
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 2 - THE SHAPE
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Make the walls",
				Bullets = new[]
				{
					"Click Primitive - it's glowing yellow in the bar above the 3D view",
					"A little menu drops down. Click Box",
					"A settings box opens on the left. Type 8 into Width, 6 into Depth and 4 into Height",
					"Click the ✓ at the top of the settings box",
				},
				Detail = "Ta-da, walls! A primitive is a ready-made shape you describe with numbers "
					+ "instead of drawing. Width runs along the red line, Depth along the green, Height "
					+ "up the blue. See \"Box\" in the Features list on the left? That's line one of "
					+ "your recipe.",
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
				Instruction = "Put a roof on it",
				Bullets = new[]
				{
					"Click Primitive again, and this time pick Wedge",
					"Type 8 into Width, 6 into Depth and 2 into Height",
					"Find Position - it has three boxes, X, Y and Z. Type 3 into Z",
					"Click the ✓",
				},
				Detail = "Why 3? Every shape is built around its own middle. Your walls are 4 tall with "
					+ "their middle at 0, so their top is at 2. The roof is 2 tall, so its middle goes at "
					+ "3 - and its bottom lands exactly on top of the walls. A wedge is a ramp, so you get "
					+ "a roof that slopes one way, like a cosy shed.",
				Art = StepArt.Solid,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Primitive,
				IsDone = s => s.Clean<PrimitiveFeature>().Any( f => f.Shape.Value == "Wedge" )
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 3 - THE OPENINGS
			//
			//  The holes are the part worth noticing: they are not deletions, they are subtractions
			//  the tool re-runs whenever the house changes.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Drill two windows",
				Bullets = new[]
				{
					"Click Hole - it's glowing yellow in the bar above the 3D view",
					"Your mouse is now a drill! Click high up on one of the two long walls",
					"Click a second spot beside it for the other window",
					"Type 0.8 into Diameter, leave Depth at 0, and click the ✓",
				},
				Detail = "A hole takes material away instead of adding it. Depth 0 means straight "
					+ "through, like a real window - turn the camera and you can peek out the other "
					+ "side. Clicked the wrong spot? Click the ✕ and drill again, no harm done.",
				Art = StepArt.Hole,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Hole,

				// Two spots, not merely a clean Hole - the first click makes the feature clean, and
				// advancing on it would move on while the reader is still placing the second window.
				IsDone = s => s.Clean<HoleFeature>().Sum( f => f.Faces.Count ) >= 2
			},

			new()
			{
				Instruction = "Add a front door",
				Bullets = new[]
				{
					"Click Hole one more time",
					"Click low down on the same wall, between the two windows",
					"Type 1.2 into Diameter and click the ✓",
				},
				Detail = "A round door - very hobbit. Now the CAD magic: right-click the last Hole in "
					+ "the Features list, pick Edit, and change 1.2 to 1.6. The wall re-cuts itself "
					+ "around the bigger door, because nothing was ever really chopped - it's all still "
					+ "just numbers in the recipe.",
				Art = StepArt.Hole,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.Hole,

				// Three openings, whatever features they live in. The windows step already needed
				// two, so two here would tick off the moment the windows were done - which is what
				// the first version of this lesson did.
				IsDone = s => s.Clean<HoleFeature>().Sum( f => f.Faces.Count ) >= 3
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 4 - TAKE IT
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Save it and take it with you",
				Bullets = new[]
				{
					"Click File in the menu at the very top, then Save. If it asks for a name, my_house is a fine one",
					"Click File again, then Export OBJ",
				},
				Detail = "Save keeps the recipe, so tomorrow you can open it and move a window. Export "
					+ "OBJ writes just the finished shape - a mesh that any 3D program can open.",
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
	/// The playermodel lesson: a humanoid already modelled, turned into something citizen's
	/// animations will drive.
	///
	/// THE ONLY LESSON THAT IS ABOUT NAMES. Every other tutorial here teaches a tool - put a solid
	/// down, cut a hole, brush a surface. This one teaches a CONVENTION, and the tool work in it is
	/// two buttons pressed twenty times. So the checks test names and parents rather than counts
	/// (see EffigyTutorialState's playermodel helpers), and the Detail on every step spends its
	/// words on why the name matters rather than on where the button is.
	///
	/// NO JARGON, ANYWHERE IN IT. "Retarget", "bind pose", "bone markup" and "ignore translation"
	/// are all real things happening underneath, and a reader who has just modelled their first
	/// robot needs none of them to finish this. What they need is: your model keeps its own shape,
	/// the animations only bend joints, and the names have to match. That is the whole of it.
	///
	/// "THE BUILT-IN CHARACTER" RATHER THAN "CITIZEN", in every line the reader sees. Citizen is
	/// what the model is called in the engine and in every comment in this codebase, and it means
	/// nothing at all to somebody who has used s&box for a week.
	/// </summary>
	static List<Step> PlayermodelSteps() => new()
		{
			// ---------------------------------------------------------------------------------
			//  PHASE 1 - HOW IT STANDS
			//
			//  Nothing here makes geometry. The model already exists, and the only thing that can
			//  go wrong before a single bone is made is that it is standing in the wrong place -
			//  which is invisible in the viewport and obvious the moment it walks.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Stand it the way the built-in character stands",
				Bullets = new[]
				{
					"Feet flat on the floor - the lowest point of the model at zero",
					"Facing forward, along the red arrow",
					"Arms out to the sides, legs straight down",
					"Hips about 31 units off the floor",
				},
				Detail = "The animations only bend joints, so your proportions are yours to choose - "
					+ "except the hips. The walk decides how high those ride, and a model far from 31 "
					+ "units will float or sink.",
				Art = StepArt.Solid,

				// Geometry AND height. An empty document has no lowest point - LowestPoint returns
				// float.MaxValue there rather than zero - so nothing can tick this off by default.
				IsDone = s => s.SolidCount >= 1 && MathF.Abs( s.LowestPoint ) <= 1f
			},

			new()
			{
				Instruction = "Switch to Rig",
				Bullets = new[]
				{
					"Click Rig on the bar above the tools",
				},
				Detail = "CAD is where parts are made; Rig is where they get a skeleton. Nothing you "
					+ "do from here on changes the shape of the model.",
				Art = StepArt.Bone,
				Points = PointAt.Workspace,
				Workspace = EffigyWorkspace.Rig,
				IsDone = s => s.Workspace == EffigyWorkspace.Rig
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 2 - THE SKELETON
			//
			//  Built root-first, because Bone from Part hangs the new bone off whichever bone is
			//  selected - so the order the bones are made in IS the hierarchy.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "The hips come first",
				Bullets = new[]
				{
					"Make sure no bone is selected in the Rig tree",
					"Click the hip part, then press Bone from Part",
					"If it is not already called pelvis, right-click it in the Rig tree and rename it",
				},
				Detail = "Everything else hangs off this one, so it goes first and on its own. The name "
					+ "has to be exactly pelvis - a bone spelled any other way is one the animations "
					+ "cannot find. Spelling is the one thing here that has to be right to the letter.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.ParentOf( "pelvis" ) == ""
			},

			new()
			{
				Instruction = "Up the back",
				Bullets = new[]
				{
					"Select the pelvis bone, then Bone from Part on the lower back: spine_01",
					"Select spine_01, then Bone from Part on the next one up: spine_02",
					"Select spine_02, then Bone from Part on the ribcage: chest",
				},
				Detail = "A selected bone becomes the parent of the next one, so the order you make "
					+ "them in is the order they hang in - which is what makes the upper body follow "
					+ "the hips. Fewer back bones is fine; the missing ones just stay put.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.Chain( "pelvis", "spine_01", "spine_02", "chest" )
			},

			new()
			{
				Instruction = "Neck and head",
				Bullets = new[]
				{
					"Select chest, then Bone from Part on the neck: neck",
					"Select neck, then Bone from Part on the head: head",
				},
				Detail = "Get this wrong - a head parented straight to the hips, say - and it still "
					+ "compiles, still loads, and then stays perfectly level while the body leans.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.Chain( "chest", "neck", "head" )
			},

			new()
			{
				Instruction = "Both arms",
				Bullets = new[]
				{
					"Select chest, then Bone from Part on BOTH shoulder parts at once",
					"Then each side in turn: upperarm, forearm, hand",
					"Left side ends in _L, right side in _R - the model's own left and right",
				},
				Detail = "Select two parts and press Bone from Part once - both hang off whatever was "
					+ "selected. _L is the side on YOUR left when you stand behind the model, which is "
					+ "the side the animations mean by left.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.Chain( "chest", "clavicle_L", "upperarm_L", "forearm_L", "hand_L" )
					&& s.Chain( "chest", "clavicle_R", "upperarm_R", "forearm_R", "hand_R" )
			},

			new()
			{
				Instruction = "Both legs",
				Bullets = new[]
				{
					"Select pelvis, then Bone from Part on BOTH thigh parts at once",
					"Then each side: calf, then foot",
					"A toe is optional - call it toe_L and toe_R if you have one",
				},
				Detail = "Legs hang off the hips, not off the back. The walk plants the feet on the "
					+ "floor, so a foot bone pointing the wrong way is the mistake you see first.",
				Art = StepArt.Bone,
				Points = PointAt.Tool,
				Tool = EffigyToolTarget.BoneFromPart,
				IsDone = s => s.Chain( "pelvis", "thigh_L", "calf_L", "foot_L" )
					&& s.Chain( "pelvis", "thigh_R", "calf_R", "foot_R" )
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 3 - PROVE IT
			//
			//  Two checks, in increasing cost. Dragging an arm catches a wrong parent in a second;
			//  walking it catches everything else.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Wiggle it before you build it",
				Bullets = new[]
				{
					"Press Pose in the Rig panel",
					"Drag an upper arm, then drag the hips",
				},
				Detail = "The arm should swing from the shoulder and take the forearm and hand with "
					+ "it; the hips should carry everything. If a limb stays behind it is on the wrong "
					+ "bone - right-click it in the Rig tree and Parent to the right one. Pose is a "
					+ "scratchpad; turning it off puts the model back.",
				Art = StepArt.Pose,
				Points = PointAt.Panel,
				Panel = "Rig",
				IsDone = s => s.Posing
			},

			new()
			{
				Instruction = "Walk around in it",
				Bullets = new[]
				{
					"File -> Make Player",
					"Open the scene it names in the console, and press Play",
				},
				Detail = "You get a floor, a light and a player wearing your model. Read the console "
					+ "afterwards: it names any bone the animations did not recognise. That is not an "
					+ "error - the bone just holds still - but it is almost always a typo.",
				Art = StepArt.Walk,
				Points = PointAt.Menu,
				IsDone = _ => false
			},

			// ---------------------------------------------------------------------------------
			//  PHASE 4 - KEEP IT
			//
			//  The scene Make Player writes is a test rig, and the lesson used to stop there -
			//  which left the reader holding a model and no idea how to put it in the game they
			//  actually came here to make. This step happens entirely OUTSIDE Effigy, in the scene
			//  editor, so it points at nothing and spells out the whole path in its bullets.
			// ---------------------------------------------------------------------------------

			new()
			{
				Instruction = "Put it on the player in your own scene",
				Bullets = new[]
				{
					"Open your scene and pick the object with a Player Controller on it",
					"Open up its Body child and find the Skinned Model Renderer",
					"Set that renderer's Model to your model, in models/effigy",
					"No Body child? The Player Controller has a Create Body button that makes one",
				},
				Detail = "Leave Use Anim Graph ticked on the renderer - that is what lets the "
					+ "animations drive it. Keep the Body child at 0,0,0 so its feet sit at the "
					+ "player's own origin, and check the Player Controller's Renderer box points at "
					+ "that Skinned Model Renderer.",

				// Nothing in Effigy to light up: every click in this step is in the scene editor.
				Art = StepArt.Export,
				Points = PointAt.None,
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
		Select( lesson );
		Active = true;
	}

	/// <summary>
	/// Choose a lesson and show its START SCREEN, rather than dropping into step one.
	///
	/// WHY THIS IS NOT THE SAME AS Restart. The playermodel lesson asks a question before it
	/// begins - example robot, or your own model - and that question lives on the start screen.
	/// Restart sets Active, the panel renders steps instead of the start screen when Active is set,
	/// so opening that lesson from the Help menu skipped the only screen its two buttons exist on.
	/// The reader landed on step one with nothing loaded and no way to ask for the example, which
	/// is exactly what po reported on 2026-09-10.
	///
	/// The other three lessons still go straight in from the menu. They have nothing to choose:
	/// their start screen is a description, and a reader who picked the lesson by name has read it.
	/// </summary>
	public void Offer( EffigyLesson lesson )
	{
		Select( lesson );
		Active = false;
	}

	void Select( EffigyLesson lesson )
	{
		Lesson = lesson;
		_steps = lesson switch
		{
			EffigyLesson.Rigging => _rigging,
			EffigyLesson.Hand => _hand,
			EffigyLesson.Playermodel => _playermodel,
			_ => _house,
		};
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
