using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Workspaces — CAD, Sculpt, Paint, Rig.
//
//  WHY THIS FILE EXISTS AT ALL. Every toolset the editor grew went onto the one
//  stage bar, and the only thing that said which was showing was a word painted
//  small at the right of the tab row. Rig did not even get that: its tools lived
//  in a right-hand dock while every other toolset lived in the bar, so starting
//  a rig was a different KIND of action from starting a sculpt for no reason a
//  user could see. EffigyWorkspaceBar's header has the rest of that argument.
//
//  WHAT A WORKSPACE DRIVES. Three things, and the point of putting them together
//  is that they were previously three unrelated decisions made in three places:
//
//    1. THE STAGE BAR — which stage set is on it. Already existed as
//       EffigyBarMode; this file adds the fourth member and its stage set.
//    2. THE DOCKS — Materials matters in Paint, the rig tree matters in Rig, and
//       neither wants to be open the rest of the time. Per-workspace, remembered.
//    3. THE VIEWPORT'S INPUT — which of its eleven partial files owns a click.
//       Enforced AT THE DOOR (see LeaveCurrentWorkspace) rather than by each
//       handler re-checking the others, which is what it grew into.
//
//  NO SEPARATE WINDOWS, AND NO SECOND VIEWPORT. That was the first idea and it
//  is the wrong one. Each viewport is a live scene render, so four of them is
//  four times the GPU for panes nobody is looking at; worse, one mesh behind four
//  cameras means four selections, four undo stacks and four answers to "which
//  body is active", and the undo snapshot in EffigyWindow is window-global. And
//  the pipeline is not actually linear — paint reveals a bad face and you go back
//  to re-extrude it — so separate windows would tax the move people make most.
//  One window, one document, four ways of looking at it.
//
//  THE WORKSPACE IS NOT STORED. It is derived from _barMode, which the enter and
//  finish paths already maintain and already treat as the truth. A second field
//  would be a second thing to keep in step, and the one bug this whole change is
//  meant to make impossible is chrome disagreeing with mode.
// ============================================================================

public sealed partial class EffigyWindow
{
	private EffigyWorkspaceBar _workspaceBar;

	/// <summary>The rig stage set, built once at startup alongside the other four.</summary>
	private List<EffigyStage> _rigStages;

	private EffigyStageTool _boneTool, _boneFromPartTool, _boneAssignTool, _boneMirrorTool, _boneDeleteTool;
	private EffigyStageTool _boneSoftTool, _softPreviewTool, _softRestTool;
	private EffigyStageTool _rigSubdivideTool, _rigRemeshTool;

	/// <summary>Which part-studio stage was last looked at in the Rig workspace, so leaving and
	/// coming back lands where you were — same courtesy _partStage does for CAD.</summary>
	private int _rigStage;

	/// <summary>
	/// Which workspace the window is in, derived rather than stored.
	///
	/// SKETCH READS AS CAD. A sketch is opened inside CAD and finished again — the stage bar goes
	/// somewhere else for the duration, but you did not leave the workspace, and lighting a
	/// different pill in the switcher while someone draws a rectangle would be the switcher lying
	/// about a thing it exists to state.
	/// </summary>
	private EffigyWorkspace CurrentWorkspace => BarMode switch
	{
		EffigyBarMode.Sculpt => EffigyWorkspace.Model,
		EffigyBarMode.MeshEdit => EffigyWorkspace.Model,
		EffigyBarMode.Paint => EffigyWorkspace.Paint,
		EffigyBarMode.Rig => EffigyWorkspace.Rig,
		EffigyBarMode.Clothing => EffigyWorkspace.Clothing,
		_ => EffigyWorkspace.Cad,
	};

	/// <summary>
	/// The bar mode, and the one place that notices it changed.
	///
	/// A PROPERTY RATHER THAN THE FIELD IT WAS, because six different methods assign it and every
	/// one of them would otherwise have to remember to re-light the switcher and re-lay the docks
	/// afterwards. Five of them remembering and one forgetting is precisely the failure this
	/// change exists to remove, and "you must also call SyncWorkspace" is a rule that only holds
	/// until the next tool gets added. Here it cannot be forgotten because there is nowhere to
	/// forget it.
	/// </summary>
	private EffigyBarMode BarMode
	{
		get => _barMode;
		set
		{
			if ( _barMode == value )
				return;

			var before = CurrentWorkspace;

			_barMode = value;

			var after = CurrentWorkspace;

			SyncViewportMode();

			if ( before != after )
				ApplyWorkspaceDocks( before, after );

			if ( _workspaceBar is not null )
				_workspaceBar.Selected = after;
		}
	}

	private EffigyBarMode _barMode = EffigyBarMode.Part;

	/// <summary>
	/// Push the workspace down onto the viewport's input. In the rig workspace a click is a bone
	/// or a whole part (Assign / Bone from Part need both); CAD idle selection runs everywhere
	/// else. See EffigyViewport.RigMode.
	///
	/// NOT ONLY FROM THE BarMode SETTER, which is where it belongs and is not sufficient on its
	/// own: that setter returns early when the mode has not changed, so a hotload taken while the
	/// rig workspace was already open would leave a freshly-defaulted RigMode at false with nothing
	/// left to switch it back on - the workspace would look right and behave as though it were CAD.
	/// Called from RebuildStages for exactly that, and it is cheap enough to call from anywhere.
	/// </summary>
	private void SyncViewportMode()
	{
		if ( _viewport is not null )
			_viewport.RigMode = CurrentWorkspace == EffigyWorkspace.Rig;
	}

	// --- switching ------------------------------------------------------------------------------

	/// <summary>
	/// A workspace was asked for. This is the whole of what a pill click means.
	///
	/// ASKED FOR, NOT SET. Sculpt and Paint can both refuse at the door — a sculpt feature whose
	/// cage did not build, a body whose UVs cannot carry paint — and when they do, EnterSculpt and
	/// EnterPaint leave the bar mode alone and put the reason in the prompt. Because the switcher
	/// paints from CurrentWorkspace, which is derived from that mode, a refusal simply leaves the
	/// old pill lit. Nothing here has to know which refusals exist.
	/// </summary>
	private void SetWorkspace( EffigyWorkspace workspace )
	{
		if ( _viewport is null || _studio is null )
			return;

		// Re-clicking the workspace you are already in is not a no-op: it is the only way to ask
		// for the dock layout back after dragging it around, and it is what someone reaches for
		// when a panel has gone missing. Everything downstream is idempotent.
		if ( workspace == CurrentWorkspace )
		{
			ApplyWorkspaceDocks( workspace, workspace, force: true );
			return;
		}

		switch ( workspace )
		{
			case EffigyWorkspace.Cad:
				LeaveCurrentWorkspace();
				ShowPartStages( force: true );
				break;

			case EffigyWorkspace.Model:
				EnterSculptWorkspace();
				break;

			case EffigyWorkspace.Paint:
				EnterPaintWorkspace();
				break;

			case EffigyWorkspace.Rig:
				EnterRig();
				break;

			case EffigyWorkspace.Clothing:
				EnterClothing();
				break;
		}

		// The switcher is painted from CurrentWorkspace, and CurrentWorkspace only moved if the
		// entry above actually took. A refusal leaves the pill where it was, which is the honest
		// answer — see the summary.
		if ( _workspaceBar is not null )
			_workspaceBar.Selected = CurrentWorkspace;

		RefreshTutorial();
	}

	/// <summary>
	/// Close whatever session owns the pointer right now.
	///
	/// THE EXCLUSIVITY, IN ONE PLACE. Sketching, sculpting, painting and the bone tool all claim a
	/// left-click in the viewport, and each of the four entry points used to carry its own partial
	/// list of the other three to shut down first — EnterSculpt finished sketches and cancelled the
	/// bone tool, AddPaint finished sketches and sculpts, EnterPaint did both again, and nothing
	/// anywhere finished a paint before arming a bone. Four hand-maintained lists of three is four
	/// chances to miss one, and the one that was missed is exactly the pair that could both be live.
	///
	/// Every entry point now calls this instead. Adding a fifth pointer-owning mode later means
	/// adding one line here rather than finding four lists.
	/// </summary>
	private void LeaveCurrentWorkspace()
	{
		if ( _viewport is null )
			return;

		if ( _viewport.IsSketching )
			FinishSketch();

		if ( _viewport.IsSculpting )
			FinishSculpt();

		if ( _viewport.IsMeshEditing )
			FinishMeshEdit();

		if ( _viewport.IsPainting )
			FinishPaint();

		// The material brush has no feature and no session to commit, so leaving it is only a
		// matter of disarming it - but it MUST be disarmed, or the ring keeps taking clicks in
		// whatever workspace you switched to.
		if ( _viewport.IsMaterialBrushing )
			LeaveMaterialBrush();

		if ( _viewport.IsWeightPainting )
			FinishWeightPaint();

		// The note pen (grease pencil) owns the click the same way every brush does, and it is the
		// one mode none of the entries below ever disarmed - so it stayed armed across a switch,
		// and the next sketch or sculpt also scribbled notes. Notes commit per stroke, so leaving
		// the pen is only a disarm, never a commit. UpdateNoteChecks puts the CAD bar's pen tick
		// back, so it is not still lit as armed when you come back.
		if ( _viewport.IsNoting )
		{
			_viewport.EndNotes();
			UpdateNoteChecks();
		}

		_rigPanel?.CancelBoneTool();

		// The soft preview solves against the rig every frame, and its whole point is the rig
		// workspace; leaving it would leave the bones sagging and swinging while you model. Stopped
		// rather than merely hidden - the bones snap back to their authored pose.
		if ( _viewport.SoftPreviewRunning )
			_viewport.StopSoftPreview();

		// The rig bar is the one mode with nothing to finish — no feature, no session, just a stage
		// set on the bar — so leaving it is only a matter of not still claiming to be in it. The
		// CAD stages are what everything falls back to.
		if ( BarMode is EffigyBarMode.Rig or EffigyBarMode.Clothing )
			BarMode = EffigyBarMode.Part;
	}

	// --- entering sculpt and paint from the switcher ---------------------------------------------

	/// <summary>
	/// "Sculpt" as a workspace, given that sculpting is a thing you do to a FEATURE.
	///
	/// This is the one place the switcher is not a plain mode flip, and it is worth being explicit
	/// about why: there is no ambient state called sculpting. EnterSculpt needs a SculptFeature,
	/// which needs a body under it and a spot in the tree. So the pill has to resolve one, and the
	/// order it tries is the order that makes the fewest surprises:
	///
	///   1. The sculpt you were last in this session, if it is still in the tree. Coming back to
	///      where you were is what leaving and returning should mean.
	///   2. The only sculpt in the document, if there is exactly one. With one candidate there is
	///      nothing to choose and asking would be ceremony.
	///   3. Otherwise land on the workspace's own bar — Subdivide and Sculpt — which is where those
	///      tools live now that they have left CAD. The brushes arrive once a sculpt is open.
	///
	/// Deliberately NOT "the last sculpt in the tree" for case 2 when there are several: which of
	/// three sculpts you meant is a real question, and guessing at it silently rolls the model back
	/// to a different place than you expected. With several and no memory, the home bar is the
	/// answer that cannot be wrong about your intent.
	/// </summary>
	private void EnterSculptWorkspace()
	{
		LeaveCurrentWorkspace();

		// Back into the edit you left, when an edit is the last thing done here and no sculpt is
		// remembered — the Model pill should come back to where you were, whichever mode that was.
		if ( _lastMeshEditFeature is not null && _lastSculptFeature is null && _studio.Features.Contains( _lastMeshEditFeature ) )
		{
			EnterMeshEdit( _lastMeshEditFeature );
			return;
		}

		var sculpts = _studio.Features.OfType<SculptFeature>().ToList();

		var target = _lastSculptFeature is not null && sculpts.Contains( _lastSculptFeature )
			? _lastSculptFeature
			: sculpts.Count == 1 ? sculpts[0] : null;

		if ( target is not null )
		{
			EnterSculpt( target );
			return;
		}

		// Nothing to re-enter: land on the sculpt workspace's own bar, where Subdivide and Sculpt
		// live. The brushes arrive once a sculpt is actually open, not with the workspace itself.
		ShowSculptHome();
	}

	/// <summary>"Paint" as a workspace. Same three-step resolution as sculpt, for the same reason —
	/// see EnterSculptWorkspace, which carries the argument for both.</summary>
	private void EnterPaintWorkspace()
	{
		LeaveCurrentWorkspace();

		var paints = _studio.Features.OfType<PaintFeature>().ToList();

		var target = _lastPaintFeature is not null && paints.Contains( _lastPaintFeature )
			? _lastPaintFeature
			: paints.Count == 1 ? paints[0] : null;

		if ( target is not null )
		{
			EnterPaint( target );
			return;
		}

		ShowPaintHome();
	}

	/// <summary>
	/// Land on the Sculpt workspace's own bar, with no feature open.
	///
	/// BarMode.Sculpt with nothing sculpting is a state that did not exist before the landing bars:
	/// the workspace now has two faces — this home (Subdivide and Sculpt) and the brushes, which
	/// <see cref="EnterSculpt"/> swaps to once a feature is open. RebuildStages tells the two apart
	/// by asking the viewport whether a sculpt is actually running.
	/// </summary>
	private void ShowSculptHome()
	{
		if ( _stageBar is null )
			return;

		BarMode = EffigyBarMode.Sculpt;

		_stageBar.Mode = "MODEL";
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( _sculptHomeStages );

		SetPrompt( _studio.Bodies.Count == 0
			? "Model needs a body — draw a sketch and extrude it, or add a primitive first."
			: "Model: Edit mesh to move vertices, edges and faces, or add a Sculpt to brush detail on." );
	}

	/// <summary>The Paint workspace's home — UV Project and Paint — with no feature open.</summary>
	private void ShowPaintHome()
	{
		if ( _stageBar is null )
			return;

		BarMode = EffigyBarMode.Paint;

		_stageBar.Mode = "PAINT";
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( _paintHomeStages );

		SetPrompt( _studio.Bodies.Count == 0
			? "Paint needs a body — draw a sketch and extrude it, or add a primitive first."
			: "Paint: add a UV Project to unwrap the mesh, or a Paint to brush colour on." );
	}

	/// <summary>
	/// The sculpt and paint features last entered, so the switcher can come back to them.
	///
	/// SEPARATE FROM _sculptFeature and _paintFeature, which are cleared on Finish — those answer
	/// "what am I editing", this answers "what was I editing", and the switcher needs the second
	/// one precisely when the first is null. Held as a reference and checked against the tree
	/// before use, because Undo can take the feature away without telling anyone.
	/// </summary>
	private SculptFeature _lastSculptFeature;

	private PaintFeature _lastPaintFeature;

	// --- the rig workspace ------------------------------------------------------------------------

	/// <summary>
	/// The rig tools, moved out of the panel's header and onto the bar.
	///
	/// SAME ACTIONS, NOT A SECOND COPY. Every one of these calls straight into EffigyRigPanel,
	/// which still owns the skeleton, the pending chain and every refusal — see the block above
	/// CancelBoneTool there. The panel keeps its buttons too: a bar tool and a panel button running
	/// one method is one control reachable from wherever you are looking, and the panel is where
	/// you are looking when you have just clicked a bone in the tree.
	///
	/// THREE TOOLS AND TWO STAGES, not the four-stage set the other workspaces run to. There is no
	/// Pose stage because there is no posing yet — the gizmo in EffigyViewport drags a bone that
	/// already exists and that is all — and a tab with nothing behind it is the thing this editor's
	/// icon set already refuses to draw. Stages get added when tools do.
	/// </summary>
	private List<EffigyStage> BuildRigStages()
	{
		var bones = new EffigyStage { Name = "Bones" };

		_boneTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Bone,
			Label = "Add Bone",
			Tip = "Click the model to place a bone. Click again to extend a chain from it. "
				+ "Select a bone first to branch a new chain from ITS tail.",
			Checkable = true,
			Clicked = () =>
			{
				_rigPanel?.ToggleBoneTool();
				UpdateRigChecks();
			},
		};

		bones.Add( _boneTool );

		_boneFromPartTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Bone,
			Label = "Bone from Part",
			Tip = "Measure the selected part and add a bone down its longest axis, already pinned "
				+ "to it. Select a bone first to parent the new one.",
			Clicked = () =>
			{
				MakeBonesFromSelectedParts();
				UpdateRigChecks();
			},
		};

		bones.Add( _boneFromPartTool );

		_boneDeleteTool = new EffigyStageTool
		{
			Icon = EffigyIcon.CutTool,
			Label = "Delete",
			Tip = "Delete the selected bone. Its children re-parent to its parent.",
			// No RecordUndo here, unlike most destructive buttons in this window: DeleteBone fires
			// RigChanging on its way in, and that IS RecordUndo (wired in BuildDocks). Recording
			// again would put two identical snapshots on the stack and cost two Ctrl+Zs to undo one
			// delete. Same reason Mirror does not record either.
			Clicked = () =>
			{
				_rigPanel?.DeleteSelected();
				UpdateRigChecks();
			},
		};

		bones.Add( _boneDeleteTool );

		var bind = new EffigyStage { Name = "Bind" };

		_boneAssignTool = new EffigyStageTool
		{
			Icon = EffigyIcon.BoneBind,
			Label = "Assign Body",
			Tip = "Select a bone, select a part, then press this to pin them. "
				+ "With no part selected it arms click-to-assign in the viewport. "
				+ "Anything left unassigned falls back to the nearest bone.",
			Checkable = true,
			Clicked = () =>
			{
				_rigPanel?.ToggleAssignBodyTool();
				UpdateRigChecks();
			},
		};

		bind.Add( _boneAssignTool );

		_boneMirrorTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Mirror,
			Label = "Mirror",
			Tip = "Mirror the selected bone and everything under it across the centre line, "
				+ "onto the same parent.",
			Clicked = () =>
			{
				_rigPanel?.MirrorSelected();
				UpdateRigChecks();
			},
		};

		bind.Add( _boneMirrorTool );

		// --- Soft ---
		//
		// The stage the kernel has been waiting for. SoftBone, SoftPose and SoftSolver have been
		// written, tested and shipped to the game assembly since before this bar existed, and
		// RigDiagnostics has been checking soft bones the whole time - it will tell you one has a
		// zero cone - while nothing in the editor could make a bone soft for it to complain about.
		//
		// THE NUMBERS ARE NOT HERE. Stiffness, damping, weight and cone are properties of ONE bone,
		// the way its head and tail are, so they live in the rig panel's inspector beside them
		// rather than on a bar that is about verbs. What the bar gets is the verb - make this bone
		// soft - and the two controls that only make sense while something is wobbling.
		var soft = new EffigyStage { Name = "Soft" };

		_boneSoftTool = new EffigyStageTool
		{
			Icon = EffigyIcon.BoneSoft,
			Label = "Make Soft",
			Tip = "Let the selected bone lag and swing behind the pose. "
				+ "Stiffness, damping, weight and cone are in the Rig panel under the bone's head and tail.",
			Checkable = true,
			Clicked = () =>
			{
				_rigPanel?.ToggleSelectedSoft();
				UpdateRigChecks();
			},
		};

		soft.Add( _boneSoftTool );

		_softPreviewTool = new EffigyStageTool
		{
			Icon = EffigyIcon.SoftPreview,
			Label = "Preview",
			Tip = "Run the soft-bone solver on the rig, so you can see the swing while you tune it. "
				+ "Drag the part around to push the bones.",
			Checkable = true,
			Clicked = () =>
			{
				ToggleSoftPreview();
				UpdateRigChecks();
			},
		};

		soft.Add( _softPreviewTool );

		_softRestTool = new EffigyStageTool
		{
			Icon = EffigyIcon.SoftRest,
			Label = "Rest",
			Tip = "Forget the motion and put every soft bone back on its pose. "
				+ "What you want after flinging the model about.",
			Clicked = () =>
			{
				_viewport?.RestSoftPreview();
				UpdateRigChecks();
			},
		};

		soft.Add( _softRestTool );

		var weights = new EffigyStage { Name = "Weights" };

		weights.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.BoneBind,
			Label = "Paint Weights",
			Tip = "Paint which bone owns which part of the mesh. The ramp is a texture atlas, "
				+ "not vertex colour — pick a bone in the Rig tree first.",
			Checkable = true,
			Clicked = ToggleWeightPaint,
		} );

		// --- Mesh ---
		//
		// Subdivide, on the rig bar, because the mesh being too coarse to bend is a RIGGING
		// problem and it is discovered here — you paint a shoulder, drag the arm, and watch the
		// elbow crease into a hinge instead of a bend. The fix for that is more loops across the
		// joint, and until now the only door to one was the Sculpt workspace: a detour that reads
		// as "you are about to sculpt" when you are not, and that rolls the model back to a sculpt
		// feature on the way.
		//
		// SAME FEATURE, DIFFERENT DEFAULT. This adds the same SubdivideFeature the Sculpt bar
		// adds — one implementation, one tree row, undoable and editable like any other — but with
		// All Faces ticked, which is the linear form that adds density and leaves every vertex
		// where it is. The whole-body Catmull-Clark the sculpt bar defaults to SMOOTHS, pulling
		// the cage onto its limit surface: exactly right when you are about to sculpt the
		// silhouette, and exactly wrong when you have already bound bones to that silhouette and
		// only want it to bend. The tick is in the dialog either way, so the default is a starting
		// point rather than a decision taken away.
		var mesh = new EffigyStage { Name = "Mesh" };

		_rigSubdivideTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Subdivide,
			Label = "Subdivide",
			Tip = "Add loops to the selected part so it bends instead of creasing. "
				+ "Adds a Subdivide set to All Faces — density without moving the shape.",
			Clicked = AddRigSubdivide,
		};

		mesh.Add( _rigSubdivideTool );

		// AND ITS OPPOSITE, on the same bar, because the rig workspace is where BOTH halves of "this
		// mesh is the wrong density" are discovered. Too coarse creases at the elbow; too dense and
		// the weight brush is a slideshow, auto-skin takes a minute, and the compile writes a model
		// nothing wants. The second is what an imported part is, every time.
		_rigRemeshTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Remesh,
			Label = "Remesh",
			Tip = "Reduce the selected part to a triangle budget so it can be weighted and posed. "
				+ "Keeps the shape; returns triangles.",
			Clicked = AddRigRemesh,
		};

		mesh.Add( _rigRemeshTool );

		var ready = new EffigyStage { Name = "Export" };

		ready.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.BoneBind,
			Label = "Check model",
			Tip = "Everything that would make this fail as a PLAYERMODEL rather than as a mesh — weights the exporter will not carry, unweighted vertices, wrong scale, feet off the floor. Run it before Make Player",
			Clicked = CheckPlayermodel,
		} );

		return new List<EffigyStage> { bones, bind, soft, weights, mesh, ready };
	}

	/// <summary>
	/// Run the playermodel readiness check and put it in the console, worst first.
	///
	/// Runs the real <see cref="Playermodel.Build"/> first rather than checking the studio as it
	/// stands: binding, fitting and twist spreading all change the weights, so a check before them
	/// passes models that fail after — which is precisely the class of problem this exists to catch.
	/// </summary>
	private void CheckPlayermodel()
	{
		if ( _studio.Rig.Count == 0 )
		{
			SetPrompt( "Check model: there is no rig yet. Make bones from the bodies first." );
			return;
		}

		List<Playermodel.Finding> findings;

		try
		{
			findings = Playermodel.Check( Playermodel.Build( _studio ), DmxWriter.MaxInfluences );
		}
		catch ( Exception e )
		{
			SetPrompt( $"Check model: the playermodel could not even be built — {e.Message}" );
			return;
		}

		if ( findings.Count == 0 )
		{
			SetPrompt( "Check model: nothing to report. This is ready to be a playermodel." );
			Log.Info( "[pm] check: nothing to report." );
			return;
		}

		foreach ( var finding in findings )
		{
			var line = $"[pm] {finding.Severity}: {finding.Problem}. {finding.Remedy}";

			if ( finding.Severity == Playermodel.Severity.Note )
				Log.Info( line );
			else
				Log.Warning( line );
		}

		var problems = findings.Count( f => f.Severity == Playermodel.Severity.Problem );
		var warnings = findings.Count( f => f.Severity == Playermodel.Severity.Warning );

		SetPrompt( problems > 0
			? $"Check model: {problems} problem(s) would stop this working as a playermodel — see the console. Worst: {findings[0].Problem}."
			: warnings > 0
				? $"Check model: it will work, but {warnings} thing(s) will look wrong — see the console. First: {findings[0].Problem}."
				: $"Check model: {findings.Count} note(s) in the console, nothing wrong." );
	}

	/// <summary>
	/// The Rig bar's Subdivide — a SubdivideFeature over the selected part, defaulted to density
	/// rather than smoothing. See the Mesh stage above for why the default differs from Sculpt's.
	///
	/// GOES THROUGH AddFeature like every other creation button, so the undo step, the rollback
	/// placement and the dialog are the ones the rest of the editor already agreed on. What it
	/// does NOT do is leave the rig workspace: the feature lands in the tree, the studio rebuilds
	/// under the skeleton, and the bones stay put because Subdivide replaces a body's mesh in
	/// place and never its id — which is the id the bone assignments are keyed on.
	/// </summary>
	private void AddRigSubdivide()
	{
		// AddFeature refuses a Subdivide with nothing picked rather than quietly densifying every
		// part in the document, and says so in the prompt. The bar can do better than a prompt
		// after the fact: the tool is locked until something is selected, the same way Bone from
		// Part is, so the refusal is visible before the click. Kept as a guard anyway — a stale
		// check is how a locked button gets clicked.
		if ( _viewport is null || _viewport.IdleFaces.Count == 0 && _viewport.IdleBodyIds.Count == 0 )
		{
			SetPrompt( "Subdivide needs to know which part — click one in the Parts list on the left, "
				+ "or pick faces in the viewport, then press Subdivide again." );
			return;
		}

		var feature = (SubdivideFeature)NewFeature( ToolKind.Subdivide, -1 );

		// Only when the selection is a whole part. A face pick is already the local, linear form —
		// AllFaces is not even read then — and ticking it would be writing a setting the feature
		// will ignore, which is worse than leaving it alone because the dialog would still show it.
		if ( _viewport.IdleFaces.Count == 0 )
			feature.AllFaces.Value = true;

		AddFeature( feature );
	}

	/// <summary>
	/// The Rig bar's Remesh — a RemeshFeature over the selected part.
	///
	/// SAME SHAPE AS <see cref="AddRigSubdivide"/> and for the same reasons: through AddFeature so
	/// the undo step and the dialog are the shared ones, locked behind a selection so the refusal
	/// is visible before the click rather than as a prompt after it, and it does not leave the
	/// workspace, because Remesh replaces a body's mesh and never its id.
	///
	/// WHAT IT DOES NOT DO is pick a budget for you. A part with no bones on it yet has no obvious
	/// right answer and the dialog opens on the question — see RemeshFeature.Target for why the
	/// default is a percentage rather than a count.
	/// </summary>
	private void AddRigRemesh()
	{
		if ( _viewport is null || _viewport.IdleBodyIds.Count == 0 )
		{
			SetPrompt( "Remesh needs to know which part — click one in the Parts list on the left, "
				+ "or click the solid in the viewport, then press Remesh again." );
			return;
		}

		AddFeature( NewFeature( ToolKind.Remesh, -1 ) );
	}

	/// <summary>
	/// Start or stop the soft-bone preview, and say so in the prompt.
	///
	/// The prompt is doing real work here rather than narrating: the preview is driven by gravity
	/// and by the pose gizmo and by nothing else, which is the right design (see
	/// EffigyViewport.SoftPreview.cs) but is not guessable from a button called Preview. A rig that
	/// sags an inch and stops looks like a broken preview until you know that settling is the
	/// point.
	/// </summary>
	private void ToggleWeightPaint()
	{
		if ( _viewport is { IsWeightPainting: true } )
		{
			FinishWeightPaint();
			return;
		}

		EnterWeightPaint();
	}

	private void EnterWeightPaint()
	{
		if ( _viewport is null || _studio is null || _rigPanel is not { HasBones: true } rig )
		{
			SetPrompt( "Paint Weights needs a skeleton — place a bone first." );
			return;
		}

		if ( !rig.HasSelectedBone )
		{
			SetPrompt( "Pick a bone in the Rig tree, then press Paint Weights." );
			return;
		}

		BarMode = EffigyBarMode.Rig;

		var (mesh, ranges) = _studio.ToMeshWithBodies();

		if ( !NormalBake.Measure( mesh ).CanBake )
			UVUnwrap.Unwrap( mesh );

		var weights = SkinBinder.BindBodies( mesh, ranges, rig.BodyBoneMap, rig.Skeleton );
		weights = SkinBinder.SmoothWeights( mesh, weights );

		if ( _studio.WeightPaint is { Count: > 0 } layer && layer.CanApply( mesh, out _ ) )
			layer.Apply( mesh, weights, rig.Skeleton, out _ );

		var session = new WeightPaintSession( mesh, weights, rig.Skeleton, _studio.WeightPaint )
		{
			Bone = rig.SelectedBoneIndex,
			Radius = 0.25f,
		};
		session.Radius = session.SuggestedRadius;

		_viewport.BeginWeightPaint( session );
		_weightBar?.Bind( session, rig.SelectedBoneName );
		SetPrompt( $"Painting {rig.SelectedBoneName}. The ramp is a texture, not vertex colour. Drag on the mesh." );
	}

	private void FinishWeightPaint()
	{
		if ( _viewport is null || !_viewport.IsWeightPainting )
			return;

		if ( _viewport.WeightSession is not null )
			_studio.WeightPaint = _viewport.WeightSession.Layer;

		_viewport.EndWeightPaint();
		_weightBar?.Bind( null, null );
		RebuildStudio();
	}

	private void ToggleSoftPreview()
	{
		if ( _viewport is null )
			return;

		SetPrompt( _viewport.ToggleSoftPreview()
			? "Soft preview: gravity is pulling the soft bones. Drag a bone to make the ones below it swing. "
				+ "Rest puts them back."
			: "" );
	}

	/// <summary>
	/// Push the panel's state onto the bar.
	///
	/// The panel is the one that knows, and it changes its mind without being asked — Escape closes
	/// a chain, clicking a different bone in the tree disarms an assign — so this is wired to its
	/// ToolStateChanged as well as being called after every tool click. A tick that can go stale
	/// on the two commonest gestures in the workspace is worse than no tick.
	/// </summary>
	private void UpdateRigChecks()
	{
		if ( _rigPanel is null )
			return;

		var hasBone = _rigPanel.HasSelectedBone;

		if ( _boneTool is not null )
		{
			_boneTool.Checked = _rigPanel.BoneToolActive;

			// The panel's own button rewrites itself to "Branch from 'upper_arm'" when a bone is
			// selected, because that is the gesture nobody discovers on their own. Worth carrying
			// onto the bar for the same reason — but on the TIP rather than the label, which has a
			// measured button around it that would jump width on every selection change.
			_boneTool.Tip = hasBone
				? $"Click the model to extend a new chain from '{_rigPanel.SelectedBoneName}'. Escape when done."
				: "Click the model to place a bone. Click again to extend the chain. "
					+ "Select a bone first to branch from its tail.";
		}

		var hasParts = _viewport is { IdleBodyIds.Count: > 0 };

		if ( _boneFromPartTool is not null )
		{
			_boneFromPartTool.Enabled = hasParts;
			_boneFromPartTool.DisabledReason = hasParts
				? null
				: "Select a part first — in the Parts list or the viewport";
		}

		// A face pick counts here where it does not for Bone from Part: Subdivide takes faces as
		// well as bodies, and picking the four faces across an elbow is the precise version of what
		// this button is for. Measuring a bone out of a face selection is not a thing.
		var hasGeometry = hasParts || _viewport is { IdleFaces.Count: > 0 };

		if ( _rigSubdivideTool is not null )
		{
			_rigSubdivideTool.Enabled = hasGeometry;
			_rigSubdivideTool.DisabledReason = hasGeometry
				? null
				: "Select a part first — in the Parts list, or pick faces in the viewport";
		}

		// Remesh keys off hasParts, not hasGeometry. A face pick names its body and Subdivide takes
		// that as a valid target, but there is no per-face Remesh — the quadrics are summed over the
		// whole surface and a boundary drawn through a face selection would be preserved as a border,
		// which is the opposite of what picking a region would mean.
		if ( _rigRemeshTool is not null )
		{
			_rigRemeshTool.Enabled = hasParts;
			_rigRemeshTool.DisabledReason = hasParts
				? null
				: "Select a part first — in the Parts list, or click the solid in the viewport";
		}

		if ( _boneAssignTool is not null )
		{
			_boneAssignTool.Checked = _rigPanel.AssigningBody;
			_boneAssignTool.Tip = hasBone && hasParts
				? $"Pin the selected part(s) to '{_rigPanel.SelectedBoneName}'."
				: hasBone
					? "Click bodies in the viewport to pin them to the selected bone. "
						+ "Anything left unassigned falls back to the nearest bone."
					: "Select a bone, select a part, then press Assign.";
		}

		if ( _boneSoftTool is not null )
			_boneSoftTool.Checked = _rigPanel.SelectedBoneIsSoft;

		if ( _softPreviewTool is not null )
			_softPreviewTool.Checked = _viewport?.SoftPreviewRunning ?? false;

		// Preview and Rest are about the rig as a whole rather than the selection, so they follow a
		// different rule from the three below: something to simulate, not something selected. A rig
		// with no soft bones would preview a skeleton that cannot move, which is a button that
		// appears to do nothing.
		var anySoft = _rigPanel.SoftBoneCount > 0;

		foreach ( var tool in new[] { _softPreviewTool, _softRestTool } )
		{
			if ( tool is null )
				continue;

			tool.Enabled = anySoft;
			tool.DisabledReason = anySoft ? null : "Nothing is soft yet - select a bone and press Make Soft";
		}

		// Assign, Mirror and Delete all act on "the selected bone" and do nothing without one. The
		// lock reason is the tooltip, the same contract the CAD stages' starter lock uses.
		foreach ( var tool in new[] { _boneAssignTool, _boneMirrorTool, _boneDeleteTool, _boneSoftTool } )
		{
			if ( tool is null )
				continue;

			tool.Enabled = hasBone;
			tool.DisabledReason = hasBone ? null : "Select a bone first — in the viewport or the Rig panel";
		}

		_stageBar?.Refresh();
	}

	/// <summary>
	/// Enter the rig workspace.
	///
	/// THE ONLY WORKSPACE WITH NOTHING TO OPEN. Sculpt and paint are scoped to a feature and roll
	/// the model back to it; a rig is one skeleton over the whole finished studio, owned by the
	/// panel for the life of the window. So this is a plain mode change — which is what makes it
	/// the odd one out in the other direction, and worth saying rather than leaving as an absence
	/// somebody later reads as a missing rollback.
	/// </summary>
	private void EnterRig()
	{
		if ( _viewport is null || _stageBar is null )
			return;

		LeaveCurrentWorkspace();

		// A dialog and the rig bar would be two things claiming the model at once, the same
		// argument EnterSculpt makes for closing it.
		_dialog?.Close();

		BarMode = EffigyBarMode.Rig;

		// Whatever was picked in CAD is still lit, and in a workspace where faces can no longer be
		// clicked a highlighted face is a selection you cannot clear by clicking off it. It is also
		// what the face-drag arrow hangs off, which is one more thing over the model competing for
		// a click meant for a bone.
		_viewport.ClearIdleSelection();

		_stageBar.Mode = "RIG";

		// NO FINISH BUTTON. Finish means "commit what you have been doing to the feature tree", and
		// there is no feature here to commit to — every bone edit already landed on the skeleton
		// when it was made. A green tick that only changed which tools were on screen would be
		// teaching the wrong thing about what green means in this editor.
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( _rigStages, _rigStage );

		UpdateRigChecks();

		SetPrompt( _rigPanel is { HasBones: true }
			? "Rig: click a bone to select it, or place more with Add Bone."
			: "Rig: press Add Bone and click the model to place your first bone." );
	}

	// --- dock layouts ----------------------------------------------------------------------------

	/// <summary>
	/// Which docks each workspace opens with.
	///
	/// A STARTING POINT, NOT A CAGE. Whatever you do to the docks while you are in a workspace is
	/// captured on the way out and restored on the way back (see ApplyWorkspaceDocks), so these
	/// values only decide what the FIRST visit looks like. A layout table that overrode the user
	/// every time would be the tool rearranging itself under someone who had already arranged it.
	///
	/// Tutorial and Console are deliberately absent. They are not about which part of the pipeline
	/// you are in — they are open because you opened them — so a workspace switch leaves them
	/// exactly as it found them.
	/// </summary>
	private static readonly Dictionary<EffigyWorkspace, (bool Features, bool Materials, bool Rig)> WorkspaceDocks = new()
	{
		// The feature tree is on in three of four: it is the document's history, and going back to
		// re-extrude something is the move that made a single window right in the first place.
		[EffigyWorkspace.Cad] = (Features: true, Materials: false, Rig: false),
		[EffigyWorkspace.Model] = (Features: true, Materials: false, Rig: false),
		[EffigyWorkspace.Paint] = (Features: true, Materials: true, Rig: false),

		// Rig is the exception. The skeleton tree wants the right-hand side to itself, and by the
		// time you are placing bones the feature tree is history you are no longer editing.
		[EffigyWorkspace.Rig] = (Features: false, Materials: false, Rig: true),

		// Clothing wants both: the tree because a garment IS a feature you go back and retune, and
		// Materials because a fabric is the thing you reach for straight after the garment fits.
		// The rig tree stays shut - Wearer fills the skeleton, and a garment reads it without
		// anyone needing to look at it.
		[EffigyWorkspace.Clothing] = (Features: true, Materials: true, Rig: false),
	};

	/// <summary>What the docks looked like the last time each workspace was left.</summary>
	private readonly Dictionary<EffigyWorkspace, (bool Features, bool Materials, bool Rig)> _workspaceDocks = new();

	/// <summary>
	/// Capture the layout the old workspace is being left in, then lay out the new one.
	///
	/// The capture is what makes the table above a default rather than a rule: open the Materials
	/// browser while sculpting and it is still there next time you sculpt, without any of this
	/// having to know you did it.
	/// </summary>
	private void ApplyWorkspaceDocks( EffigyWorkspace from, EffigyWorkspace to, bool force = false )
	{
		if ( _rigPanel is null || _materialsPanel is null )
			return; // Still building. BuildDocks lays out the first workspace itself.

		if ( from != to )
			_workspaceDocks[from] = ReadDockState();

		var wanted = _workspaceDocks.TryGetValue( to, out var remembered ) && !force
			? remembered
			: WorkspaceDocks[to];

		SetDockIfChanged( "Features", wanted.Features );
		SetDockIfChanged( "Materials", wanted.Materials );
		SetDockIfChanged( "Rig", wanted.Rig );

		// The one dock a workspace is actually ABOUT gets raised, not merely opened — it may be
		// tabbed behind another on the same edge, and an open-but-hidden panel is the same as a
		// closed one to the person looking for it.
		switch ( to )
		{
			case EffigyWorkspace.Paint: DockManager.RaiseDock( "Materials" ); break;
			case EffigyWorkspace.Rig: DockManager.RaiseDock( "Rig" ); break;
			case EffigyWorkspace.Clothing: DockManager.RaiseDock( "Features" ); break;
		}

		// The View menu's ticks were written for the layout that just went away.
		SyncDockChecks();
	}

	private (bool Features, bool Materials, bool Rig) ReadDockState() =>
		(DockManager.IsDockOpen( "Features" ),
			DockManager.IsDockOpen( "Materials" ),
			DockManager.IsDockOpen( "Rig" ));

	/// <summary>Asked before set, because SetDockState on a dock already in that state still costs
	/// a relayout — and four of those per switch is a visible flicker on a window this size.
	/// </summary>
	private void SetDockIfChanged( string title, bool open )
	{
		if ( DockManager.IsDockOpen( title ) != open )
			DockManager.SetDockState( title, open );
	}
}

// ============================================================================
//  Model > Edit — direct polygon editing.
//
//  Built the way Sculpt is: a feature in the tree (MeshEditFeature) holds the result, a session
//  (MeshEditSession, in the kernel) holds the working mesh, selection and undo while you work, and
//  Finish commits the session to the feature as ONE step on the document's undo stack.
//
//  THE OPERATION STAYS OPEN. Extrude, Inset, Loop cut and Solidify run as a preview the moment you
//  press them, and their number sits on the floating bar, re-running the operation live, until
//  Done or Cancel. Pressing another tool accepts the one that was open.
// ============================================================================
public sealed partial class EffigyWindow
{
	private List<EffigyStage> _meshEditStages;
	private MeshEditFeature _meshEditFeature;
	private MeshEditFeature _lastMeshEditFeature;
	private EffigyMeshEditBar _meshEditBar;

	/// <summary>Edit mode's toolbar. Its tools are the same objects the menus hold.</summary>
	private EffigyStage _meshCoreStage;

	private EffigyStageTool _meshVertexTool, _meshEdgeTool, _meshFaceTool;
	private EffigyStageTool _meshExtrudeTool, _meshExtrudeNormalsTool, _meshExtrudeIndividualTool, _meshInsetTool, _meshLoopCutTool;
	private EffigyStageTool _meshMergeTool, _meshDissolveTool, _meshDeleteTool;
	private EffigyStageTool _meshMirrorTool, _meshSnapTool, _meshWrapTool;
	private EffigyStageTool _meshKnifeTool, _meshSoftTool, _meshSoftConnectedTool, _meshSoftShapeTool;
	private EffigyStageTool _meshWeightsTool;
	private EffigyStageTool _meshLassoTool, _meshBisectTool, _meshDuplicateTool, _meshBridgeTool, _meshExtractTool, _meshSeparateTool;
	private EffigyStageTool _meshXrayTool, _meshMoveTool, _meshRotateTool, _meshScaleTool, _meshSlideTool, _meshBevelTool;
	private EffigyStageTool _meshDrapeTool, _meshFabricTool;
	private EffigyStageTool _meshSeamTool, _meshUnseamTool, _meshUnwrapTool;
	private EffigyStageTool _meshVertexSlideTool;
	private EffigyStageTool _meshSubdivideTool, _meshSmoothTool, _meshSymmetrizeTool;
	private EffigyStageTool _meshNormalizeWeightsTool, _meshSmoothWeightsTool, _meshMirrorWeightsTool;
	private EffigyStageTool _meshShrinkFattenTool, _meshGrowTool, _meshShrinkSelectionTool;
	private EffigyStageTool _meshLinkedTool, _meshSimilarTool, _meshPathTool, _meshFillTool, _meshCircleTool;
	private EffigyStageTool _meshRetopoTool, _meshRelaxTool, _meshEvenQuadsTool, _meshFinishRetopoTool, _meshStripTool;
	private EffigyStageTool _modMirrorTool, _modArrayTool, _modSubdivideTool, _modSolidifyTool;
	private EffigyStageTool _meshSplitEdgeTool, _meshSharpenTool;
	private EffigyStageTool _meshTriangulateTool, _meshPokeFacesTool, _meshLimitedDissolveTool, _meshFlattenTool;
	private EffigyStageTool _meshConnectTool, _meshLoopCircleTool, _meshLoopSpaceTool, _meshOrganicRelaxTool;
	private EffigyStageTool _meshTrisToQuadsTool, _meshBevelVerticesTool;
	private EffigyStageTool _meshHideTool, _meshHideOthersTool, _meshUnhideTool, _meshRandomSelectTool, _meshTrianglesTool, _meshNgonsTool, _meshInteriorTool;
	private EffigyStageTool _meshDeleteFacesTool, _meshDeleteEdgesTool, _meshMergeFirstTool, _meshMergeLastTool, _meshMergePivotTool, _meshRandomizeTool, _meshDecimateTool;
	private EffigyStageTool _meshLoosePartsTool, _meshByMaterialTool, _meshCreaseTool, _meshUncreaseTool;
	private readonly List<EffigyStageTool> _meshTypedTools = new();
	private EffigyStageTool _meshRotateEdgeTool, _meshRotateEdgeBackTool, _meshSubdivideEdgesTool, _meshFillHolesTool, _meshBeautifyTool;
	private EffigyStageTool _meshSharpSelectTool, _meshMirrorSelectTool, _meshLooseSelectTool, _meshToPivotTool;
	private EffigyStageTool _meshProjectUVsTool, _meshPlanarUVsTool, _meshTrimRowTool, _meshTrimRowsTool, _meshPipeTool, _meshScatterTool;

	/// <summary>How many rows the trim sheet has, for Trim row. Eight is the usual sheet.</summary>
	private int _meshTrimRows = 8;

	/// <summary>Units per tile for world-scale mapping. 32 matches s&box's tiling materials at
	/// 512 px on 16 px per unit; change it with Project UVs and the trims follow.</summary>
	private float _meshUnitsPerTile = 32f;

	/// <summary>Which fabric <see cref="StartMeshDrape"/> hangs the cloth as. Cycled by the Fabric tool.</summary>
	private Fabric _meshFabric = Fabric.Cotton;

	private const char MeshKeyLoopCut = (char)18;
	private const char MeshKeyBevel = (char)2;
	private const char MeshKeyXray = (char)26;
	private const char MeshKeySeam = (char)5;
	private const char MeshKeyShrinkFatten = (char)19;
	private const char MeshKeySimilar = (char)20;
	private const char MeshKeyInvert = (char)9;
	private const char MeshKeyRecalculate = (char)14;
	private const char MeshKeySplit = (char)25;
	private const char MeshKeySearch = ' ';
	private const char MeshKeyExtrudeNormals = (char)30;
	private const char MeshKeyTriangulate = (char)31;
	private const char MeshKeyPokeFaces = (char)28;
	private const char MeshKeyLimitedDissolve = (char)29;
	private const char MeshKeyTrisToQuads = (char)16;
	private const char MeshKeyBevelVertices = (char)22;
	private const char MeshKeySoftConnected = (char)15;
	private const char MeshKeyHide = (char)8;
	private const char MeshKeyHideOthers = (char)4;
	private const char MeshKeyUnhide = (char)21;
	private const char MeshKeyCrease = (char)3;

	private void ToggleMeshKnife()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.IsPreviewing )
			AcceptMeshOp();

		// One armed tool at a time, or two previews chase the same cursor.
		_viewport.MeshLoopCutArmed = false;

		_viewport.MeshKnifeArmed = !_viewport.MeshKnifeArmed;
		_viewport.MeshKnifeBisect = false;

		SetPrompt( _viewport.MeshKnifeArmed
			? "Knife: click points on the model. Each click cuts from the one before. Enter or Esc puts it down."
			: "Knife put down." );

		OnMeshEditChanged();
	}

	private void CopyMeshWeights()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		var source = OtherBodiesMesh( withSkin: true );

		if ( source is null || !source.IsRigged )
		{
			SetPrompt( "Copy weights: no other body here carries skin weights. Rig the character first (Rig workspace), or import a rigged one." );
			return;
		}

		RunMeshOp( "Copy weights", s => SetPrompt( $"Copy weights: {s.TransferWeights( source ):N0} vertices now follow the body under them." ) );
	}

	private void OnMeshPolyBuild( Vec3 at, bool closeTriangle, float weld ) =>
		RunMeshOp( "Poly build", s => s.PolyBuild( at, closeTriangle, weld ) );

	/// <summary>
	/// Start or stop drawing new topology over this body. Starting makes the body as it stands the
	/// surface everything snaps to; stopping keeps what was drawn in this body (Finish retopo is
	/// the one that lifts it out).
	/// </summary>
	private void ToggleMeshRetopo()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.IsRetopologizing )
		{
			session.EndRetopo();
			SetPrompt( "Retopology off. What you drew is still part of this body — Separate (P) or Delete it if you did not mean to keep it." );
			OnMeshEditChanged();
			UpdateMeshModeSwitch();
			return;
		}

		RunMeshOp( "Retopo", s => s.BeginRetopo( MeshSize() * 0.003f ) );

		UpdateMeshModeSwitch();

		if ( session.IsRetopologizing )
			SetPrompt( "Retopo: Ctrl+click four points to lay the first quad, then Ctrl+click beside the selected edge to grow a strip. The ghost shows the next face — red means it would be refused. Click an open edge to grow from it instead." );
	}

	private void OnMeshPolyStroke( List<Vec3> stroke, float width, float weld ) =>
		RunMeshOp( "Strip", s => s.PolyBuildStroke( stroke, width, weld ) );

	private void ToggleMeshStrip()
	{
		if ( _viewport is null )
			return;

		_viewport.RetopoStrip = !_viewport.RetopoStrip;
		SetPrompt( _viewport.RetopoStrip
			? "Strip brush: Ctrl+drag along the form to lay a strip of quads. Start on the selected edge to carry on from it."
			: "Strip brush off: Ctrl+click lays one face at a time." );
		OnMeshEditChanged();
	}

	private void SetMeshPivot()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( Editor.Application.IsKeyDown( KeyCode.Shift ) )
		{
			session.Pivot = Vec3.Zero;
			SetPrompt( "Pivot back at the origin." );
		}
		else if ( session.AffectedVertices().Count == 0 )
			SetPrompt( "Pivot: select something to put the pivot at. Shift-click puts it back at the origin." );
		else
		{
			session.PivotToSelection();
			SetPrompt( "Pivot moved to the selection. Spin and Screw now turn round it." );
		}

		OnMeshEditChanged();
	}

	/// <summary>
	/// Change one of the edit's live modifiers: one document undo step, the history marked to
	/// rebuild, and the preview over the cage refreshed straight away.
	/// </summary>
	/// <summary>The live modifier pass for the preview, fed the session's creases rather than the
	/// feature's, which only catch up when the edit is committed.</summary>
	private Func<PolyMesh, PolyMesh> MeshModifiersFor( MeshEditFeature feature )
	{
		if ( !feature.HasModifiers )
			return null;

		return mesh => feature.ApplyModifiers( mesh, _viewport?.MeshEditSession?.Creases ?? feature.Creases );
	}

	private void SetMeshModifier( string what, Action<MeshEditFeature> change )
	{
		if ( _meshEditFeature is not { } feature || _viewport is null )
			return;

		RecordUndo();
		change( feature );
		_studio.MarkDirty( feature );
		_viewport.MeshEditModifiers = MeshModifiersFor( feature );
		_viewport.RefreshMeshEditPreview();
		SetPrompt( what ?? DescribeMeshModifiers() );
		OnMeshEditChanged();
	}

	private void ToggleModMirror() => SetMeshModifier( null, f => f.MirrorX.Value = !f.MirrorX.Value );

	private void CycleModArray() => SetMeshModifier( null, f => f.ArrayCount.Value = f.ArrayCount.Clamped >= 5 ? 1 : f.ArrayCount.Clamped + 1 );

	private void CycleModSubdivide() => SetMeshModifier( null, f => f.SubdivideLevels.Value = (f.SubdivideLevels.Clamped + 1) % 4 );

	private void ToggleModSolidify() => SetMeshModifier( null, f => f.SolidifyThickness.Value = f.SolidifyThickness.Value > 0f ? 0f : MeshSize() * 0.01f );

	/// <summary>A line saying what the live modifiers are now, for the prompt.</summary>
	private string DescribeMeshModifiers()
	{
		if ( _meshEditFeature is not { HasModifiers: true } f )
			return "No live modifiers: the body is exactly the mesh you edit.";

		var parts = new List<string>();
		if ( f.MirrorX.Value ) parts.Add( "mirrored across X" );
		if ( f.ArrayCount.Clamped > 1 ) parts.Add( $"{f.ArrayCount.Clamped} copies along X" );
		if ( f.SubdivideLevels.Clamped > 0 ) parts.Add( $"subdivided {f.SubdivideLevels.Clamped}x" );
		if ( f.SolidifyThickness.Value > 0f ) parts.Add( $"{f.SolidifyThickness.Value:0.###} thick" );

		return $"Live: {string.Join( ", ", parts )}. You edit the cage (the wire); the body gets the result. Change the numbers in the feature's settings.";
	}

	private void ToggleMeshEvenQuads()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		session.EvenQuads = !session.EvenQuads;
		SetPrompt( session.EvenQuads
			? "Even quads: each new face is square to the edge it grows from, wherever you click."
			: "Free quads: each new face reaches exactly to the click." );
		OnMeshEditChanged();
	}

	private void FinishMeshRetopo()
	{
		RunMeshOp( "Finish retopo", s => s.SeparateRetopo() );
		UpdateMeshModeSwitch();

		if ( _viewport?.MeshEditSession is { IsRetopologizing: false, Separated.Count: > 0 } )
			SetPrompt( "Retopology lifted out — it becomes its own body when you press Finish. The sculpt under it is untouched." );
	}

	private void ToggleMeshCircle()
	{
		if ( _viewport is null )
			return;

		_viewport.MeshCircle = !_viewport.MeshCircle;

		if ( _viewport.MeshCircle )
			SetPrompt( "Circle select: hold the mouse and paint over what you want. Ctrl paints it off, [ and ] resize, C or Esc to stop." );

		OnMeshEditChanged();
	}

	private void ToggleMeshLasso()
	{
		if ( _viewport is null )
			return;

		_viewport.MeshLasso = !_viewport.MeshLasso;
		OnMeshEditChanged();
	}

	private void ArmMeshBisect()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.IsPreviewing )
			AcceptMeshOp();

		_viewport.MeshKnifeArmed = true;
		_viewport.MeshKnifeBisect = true;
		SetPrompt( "Bisect: click two points on the model. The slice runs all the way through along that line, into the screen." );
		OnMeshEditChanged();
	}

	private void OnMeshBisectCut( Vec3 from, Vec3 to, Vec3 view )
	{
		var normal = Vec3.Cross( to - from, view );

		if ( normal.LengthSquared < 1e-12f )
		{
			SetPrompt( "Bisect: the two points were too close. Click two points further apart." );
			return;
		}

		RunMeshOp( "Bisect", s => s.Bisect( from, normal ) );
		OnMeshEditChanged();
	}

	private void OnMeshKnifeCut( Vec3 from, Vec3 to, Vec3 view, bool throughAll ) =>
		RunMeshOp( "Knife", s => s.Knife( from, to, view, throughAll ) );

	private void SaveMeshSelection()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.AffectedVertices().Count == 0 )
		{
			SetPrompt( "Select something to save first." );
			return;
		}

		var name = $"Selection {session.NamedSelections.Count + 1}";
		session.SaveSelection( name );
		SetPrompt( $"Saved as {name}. Recall selection brings it back." );
		OnMeshEditChanged();
	}

	private void RecallMeshSelection()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.NamedSelections.Count == 0 )
		{
			SetPrompt( "Nothing is saved yet. Select something and press Save selection." );
			return;
		}

		var how = Editor.Application.IsKeyDown( KeyCode.Shift ) ? MeshEditSession.Combine.Add
			: Editor.Application.IsKeyDown( KeyCode.Control ) ? MeshEditSession.Combine.Remove
			: MeshEditSession.Combine.Replace;

		var menu = new Menu( _viewport );
		menu.AddHeading( "Saved selections" );
		foreach ( var name in session.NamedSelections.Keys )
		{
			var captured = name;
			menu.AddOption( captured, null, () => RunMeshOp( "Recall", s => s.RecallSelection( captured, how ) ) );
		}

		menu.AddSeparator();
		foreach ( var name in session.NamedSelections.Keys )
		{
			var captured = name;
			menu.AddOption( $"Forget {captured}", "delete", () => { session.ForgetSelection( captured ); OnMeshEditChanged(); } );
		}

		menu.OpenAtCursor();
	}

	private void ToggleMeshSoft()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		session.SoftRadius = session.SoftRadius > 0f ? 0f : MeshSize() * 0.15f;
		OnMeshEditChanged();
	}

	private void ToggleMeshSoftConnected()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		session.SoftConnected = !session.SoftConnected;
		if ( session.SoftConnected && session.SoftRadius <= 0f )
			session.SoftRadius = MeshSize() * 0.15f;

		SetPrompt( session.SoftConnected
			? "Soft falloff follows the surface: nearby but unconnected parts stay put."
			: "Soft falloff reaches through space." );
		OnMeshEditChanged();
	}

	private void CycleMeshSoftShape()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		var shapes = Enum.GetValues<MeshEditSession.SoftFalloff>();
		session.SoftShape = shapes[(Array.IndexOf( shapes, session.SoftShape ) + 1) % shapes.Length];
		SetPrompt( $"Soft falloff shape: {session.SoftShape}." );
		OnMeshEditChanged();
	}

	private void ScaleMeshSoft( float factor )
	{
		if ( _viewport is { MeshCircle: true } )
		{
			_viewport.MeshCircleRadius = Math.Clamp( _viewport.MeshCircleRadius * factor, 0.005f, 1f );
			OnMeshEditChanged();
			return;
		}

		if ( _viewport?.MeshEditSession is not { SoftRadius: > 0f } session )
			return;

		session.SoftRadius *= factor;
		SetPrompt( $"Soft falloff reaches {session.SoftRadius:0.##} units." );
		OnMeshEditChanged();
	}

	private void SetMeshHandle( EffigyViewport.BodyDragMode mode )
	{
		if ( _viewport is null )
			return;

		_viewport.MeshHandleMode = mode;
		OnMeshEditChanged();
	}

	private void ToggleMeshXray()
	{
		if ( _viewport is null )
			return;

		_viewport.MeshXray = !_viewport.MeshXray;
		OnMeshEditChanged();
	}

	/// <summary>Bevel with the width on the bar. Four segments rounds; Shift makes it one, a chamfer.</summary>
	private void StartBevel()
	{
		var segments = Editor.Application.IsKeyDown( KeyCode.Shift ) ? 1 : 4;
		StartMeshOp( segments == 1 ? "Chamfer" : "Bevel", MeshSize() * 0.02f, ( s, v ) => s.Bevel( v, segments ) );
	}

	private string _meshOpName;

	/// <summary>Called once the viewport exists. Kept here so the whole of Edit mode is in one place.</summary>
	private void BuildMeshEditBar()
	{
		if ( _viewport is null || _meshEditBar is not null )
			return;

		_meshEditBar = new EffigyMeshEditBar( _viewport.Canvas )
		{
			Accepted = AcceptMeshOp,
			Cancelled = CancelMeshOp,
		};

		_viewport.AddMeshEditOverlay( _meshEditBar );
		_viewport.MeshEditChanged = OnMeshEditChanged;
		_viewport.MeshEditKeyPressed = MeshEditKey;
		_viewport.MeshKnifeCut = OnMeshKnifeCut;
		_viewport.MeshPolyBuild = OnMeshPolyBuild;
		_viewport.MeshPolyStroke = OnMeshPolyStroke;
		_viewport.MeshBisectCut = OnMeshBisectCut;
		_viewport.MeshLoopCutPlaced = PlaceLoopCut;
		_viewport.MeshEditContextMenuRequested = OpenMeshContextMenu;
	}

	/// <summary>Edit mode's bar: the toolbar of held tools, with every tool group as a menu above.</summary>
	private void ShowMeshEditBar()
	{
		_stageBar.Mode = null;
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( new[] { _meshCoreStage } );
		_stageBar.SetMenus( MeshMenus() );
		_stageBar.MenuOpening = UpdateMeshEditChecks;
		UpdateMeshModeSwitch();
		ShowMeshEditPanel( true );
	}

	/// <summary>Swap the left dock between the CAD tree and Edit mode's own column.</summary>
	private void ShowMeshEditPanel( bool editing )
	{
		if ( _meshPanel is null )
			return;

		_meshPanel.Visible = editing;

		if ( _featureTree is not null )
			_featureTree.Visible = !editing;

		if ( _partsPanel is not null )
			_partsPanel.Visible = !editing;

		if ( editing )
			RefreshMeshEditPanel();
	}

	private void RefreshMeshEditPanel()
	{
		if ( _meshPanel is { Visible: true } )
			_meshPanel.Refresh( _viewport?.MeshEditSession, _studio.Bodies, _meshEditFeature?.LastBodyId );
	}

	/// <summary>Undo until the step at <paramref name="index"/> in the history is gone too.</summary>
	private void UndoMeshTo( int index )
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.IsPreviewing )
			CancelMeshOp();

		while ( session.UndoCount > index && session.Undo() ) { }

		_viewport.RefreshMeshEditPreview();
		OnMeshEditChanged();
	}

	/// <summary>
	/// Object | Edit | Retopo. Picking Object is how you leave — the edit is kept, exactly as the
	/// old Finish button kept it — so there is no separate button to hunt for.
	/// </summary>
	private void UpdateMeshModeSwitch()
	{
		var retopo = _viewport?.MeshEditSession is { IsRetopologizing: true };

		_stageBar.SetSegments( new[]
		{
			new EffigyModeSegment { Label = "Object", Tip = "Stop editing. Your edit is kept, and Ctrl+Z still undoes it", Clicked = FinishMeshEdit },
			new EffigyModeSegment { Label = "Edit", Tip = "Edit vertices, edges and faces", Active = !retopo, Clicked = () => { if ( _viewport?.MeshEditSession is { IsRetopologizing: true } ) ToggleMeshRetopo(); UpdateMeshModeSwitch(); } },
			new EffigyModeSegment { Label = "Retopo", Tip = "Draw a clean, light mesh over this body", Active = retopo, Clicked = () => { if ( _viewport?.MeshEditSession is { IsRetopologizing: false } ) ToggleMeshRetopo(); UpdateMeshModeSwitch(); } },
		} );
	}

	/// <summary>
	/// Space: find any tool by name. A menu with a filter box on top, refilled on every keystroke —
	/// the way s&box's own node graph finds a node — listing each match with the menu it lives in,
	/// so searching also teaches where things are.
	/// </summary>
	private void OpenMeshToolSearch()
	{
		if ( _viewport?.MeshEditSession is null )
			return;

		UpdateMeshEditChecks();

		var menu = new Menu( _viewport );

		var row = new Widget( menu ) { Layout = Layout.Row() };
		row.Layout.Margin = 6;

		var search = new MenuSearchEdit( row ) { PlaceholderText = "Search every tool: grid fill, rip, bevel…", MinimumWidth = 280f };
		search.TextChanged += text => FillMeshToolSearch( menu, text );

		// Enter runs the first match that can run, so "gr⏎" is a grid fill.
		search.ReturnPressed += () =>
		{
			if ( _meshSearchFirst is { } first )
			{
				menu.Close();
				first.Clicked?.Invoke();
			}
		};

		row.Layout.Add( search );
		menu.AddWidget( row );

		FillMeshToolSearch( menu, null );
		menu.OpenAtCursor();
		search.Focus();
	}

	private EffigyStageTool _meshSearchFirst;

	/// <summary>A line edit that lives in a menu: a click in it must not close the menu.</summary>
	private sealed class MenuSearchEdit : LineEdit
	{
		public MenuSearchEdit( Widget parent ) : base( parent ) { }

		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			e.Accepted = true;
		}
	}

	private void FillMeshToolSearch( Menu menu, string filter )
	{
		var visible = menu.Visible;

		// The same trick the node graph plays to refill an open menu without it flickering shut.
		var setFlag = typeof( Widget ).GetMethod( "SetFlag",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic );

		if ( visible )
			setFlag?.Invoke( menu, new object[] { 15, false } );

		menu.RemoveMenus();
		menu.RemoveOptions();

		var shown = 0;
		var seen = new HashSet<EffigyStageTool>();
		_meshSearchFirst = null;

		foreach ( var stage in MeshMenus() )
		{
			foreach ( var tool in stage.Tools )
			{
				if ( tool is null || !seen.Add( tool ) )
					continue;

				if ( !string.IsNullOrWhiteSpace( filter )
					&& tool.Label.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0
					&& (tool.Tip ?? "").IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0 )
					continue;

				var run = tool.Clicked;
				var option = menu.AddOption( $"{tool.Label}    ·  {stage.Name}", null, () => run?.Invoke() );

				if ( _meshSearchFirst is null && tool.Enabled && !string.IsNullOrWhiteSpace( filter ) )
					_meshSearchFirst = tool;
				option.Enabled = tool.Enabled;
				option.Checkable = tool.Checkable;
				option.Checked = tool.Checked;
				option.ToolTip = tool.Enabled ? tool.Tip : tool.DisabledReason ?? tool.Tip;

				// A long list is a list nobody reads; the filter is how you get to the rest.
				if ( ++shown >= 24 )
					break;
			}

			if ( shown >= 24 )
				break;
		}

		if ( shown == 0 )
			menu.AddOption( "No tool by that name", null, null ).Enabled = false;

		if ( visible )
		{
			setFlag?.Invoke( menu, new object[] { 15, true } );
			menu.AdjustSize();
			menu.Update();
		}
	}

	private List<EffigyStage> _meshMenus;
	private List<EffigyStage> _meshMenusBuiltFrom;

	/// <summary>
	/// The menu bar: the same tools as the stages, regrouped by what they work on — Blender's
	/// Select / Add / Vertex / Edge / Face / Mesh — rather than by the order they were written in.
	/// A tool is picked from its old group by label (Array and Smooth are both an operation and a
	/// modifier, so the group matters), and any tool not placed here lands at the end of Mesh, so
	/// a tool added later can never go missing from the menus. "-" is a divider.
	/// </summary>
	private List<EffigyStage> MeshMenus()
	{
		if ( _meshMenus is not null && ReferenceEquals( _meshMenusBuiltFrom, _meshEditStages ) )
			return _meshMenus;

		var placed = new HashSet<EffigyStageTool>();

		EffigyStage Menu( string name, params string[] picks )
		{
			var menu = new EffigyStage { Name = name };

			foreach ( var pick in picks )
			{
				if ( pick == "-" )
				{
					if ( menu.Tools.Count > 0 && menu.Tools[^1] is not null )
						menu.Tools.Add( null );
					continue;
				}

				var split = pick.Split( '/' );
				var group = split.Length > 1 ? split[0] : null;
				var label = split[^1];

				foreach ( var stage in _meshEditStages )
				{
					if ( group is not null && stage.Name != group )
						continue;

					var tool = stage.Tools.Find( t => t is not null && t.Label == label );
					if ( tool is null )
						continue;

					menu.Tools.Add( tool );
					placed.Add( tool );
					break;
				}
			}

			if ( menu.Tools.Count > 0 && menu.Tools[^1] is null )
				menu.Tools.RemoveAt( menu.Tools.Count - 1 );

			return menu;
		}

		var menus = new List<EffigyStage>
		{
			Menu( "Select", "All", "Invert", "Grow", "Shrink", "-", "Linked", "Similar", "Path", "Checker", "Random", "Select/Mirror", "-", "Save selection", "Recall selection", "-", "Non-manifold", "Border", "Sharp edges", "Triangles", "N-gons", "Interior", "Loose", "-", "Lasso", "Circle", "X-ray", "-", "Hide", "Hide others", "Unhide" ),
			Menu( "Add", "Extrude", "Extrude along normals", "Extrude individual", "Inset", "Bevel", "Loop cut", "Knife", "Bisect", "-", "Fill", "Grid fill", "Bridge", "-", "Add/Duplicate", "Add/Array", "Spin", "Screw", "Pivot here", "-", "Add/Subdivide" ),
			Menu( "Vertex", "Merge", "Merge first", "Merge last", "Merge at pivot", "By distance", "Connect", "Bevel vertices", "-", "Vertex slide", "Clean up/Smooth", "Clean up/Relax", "Clean up/Flatten", "Loop circle", "Loop space", "To sphere", "Randomize" ),
			Menu( "Edge", "Edge slide", "Rip", "Rotate edge", "Subdivide edges", "Pipe", "-", "Loop circle", "Loop space", "-", "Make hard", "Harden creases", "Crease", "Clear crease", "-", "Mark seam", "Clear seam", "-", "Delete edges" ),
			Menu( "Face", "Triangulate", "Tris to quads", "Beautify", "Poke faces", "Fill holes", "Scatter", "-", "Split", "Separate", "Loose parts", "By material", "Extract", "-", "Flip", "Fix normals", "-", "Solidify", "-", "Delete faces only" ),
			Menu( "Transform", "Move X", "Move Y", "Move Z", "-", "Rotate X", "Rotate Y", "Rotate Z", "-", "Scale by", "Flatten X", "Flatten Y", "Flatten Z", "-", "Pivot here", "To pivot", "Snap to grid" ),
			Menu( "Mesh", "Move", "Rotate", "Scale", "Soft", "Connected", "Falloff shape", "-", "Shrink/Fatten", "Shear", "Bend", "-", "Symmetrize", "Mirror X", "Snap", "Shrinkwrap", "-", "Dissolve", "Limited dissolve", "Decimate", "Delete", "Delete loose" ),
			Menu( "UV", "Unwrap", "Mark seam", "Clear seam", "-", "Project UVs", "Project from above", "-", "Trim row", "Trim rows" ),
			Menu( "Modifiers", "Modifiers/Live mirror", "Modifiers/Array", "Modifiers/Smooth", "Crease", "Clear crease", "Modifiers/Thickness" ),
			Menu( "Retopo", "Retopo/Retopo", "Even quads", "Strip brush", "Relax", "Finish retopo" ),
			Menu( "Skin", "Copy weights", "Smooth weights", "Mirror weights", "Fix weights" ),
			Menu( "Cloth", "Drape", "Cotton" ),
		};

		// Anything the lists above do not name still has to be reachable.
		var mesh = menus.Find( m => m.Name == "Mesh" );
		var first = true;
		foreach ( var stage in _meshEditStages )
		{
			foreach ( var tool in stage.Tools )
			{
				if ( tool is null || placed.Contains( tool ) || !placed.Add( tool ) )
					continue;

				if ( first )
				{
					mesh.Tools.Add( null );
					first = false;
				}

				mesh.Tools.Add( tool );
			}
		}

		_meshMenus = menus;
		_meshMenusBuiltFrom = _meshEditStages;
		return menus;
	}

	/// <summary>A tool from the menus by its label, or null.</summary>
	private EffigyStageTool MeshToolNamed( string label )
	{
		var split = label.Split( '/' );
		var group = split.Length > 1 ? split[0] : null;
		var name = split[^1];

		foreach ( var stage in _meshEditStages )
		{
			if ( group is not null && stage.Name != group )
				continue;

			foreach ( var tool in stage.Tools )
				if ( tool.Label == name )
					return tool;
		}

		return null;
	}

	/// <summary>
	/// The right-click menu: what can be done to what is selected, by element — Blender's context
	/// menu. "-" is a divider. The tools are the menu bar's own, so their state is the same.
	/// </summary>
	private void OpenMeshContextMenu()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		UpdateMeshEditChecks();

		var labels = session.IsRetopologizing
			? new[] { "Relax", "Even quads", "Strip brush", "-", "Grow", "Linked", "Invert", "-", "Delete", "Finish retopo" }
			: session.Mode switch
			{
				EditElement.Vertex => new[] { "Connect", "Bevel vertices", "-", "Loop circle", "Loop space", "-", "Merge", "Merge first", "Merge last", "By distance", "Vertex slide", "Smooth", "Clean up/Relax", "Flatten", "To sphere", "Randomize", "-", "Fill", "Rip", "-", "Grow", "Shrink", "Linked", "Invert", "-", "Hide", "Hide others", "Unhide", "-", "Dissolve", "Delete" },
				EditElement.Edge => new[] { "Extrude", "-", "Loop cut", "Bevel", "Edge slide", "-", "Loop circle", "Loop space", "-", "Bridge", "Fill", "Grid fill", "Rip", "Rotate edge", "Subdivide edges", "Pipe", "-", "Make hard", "Crease", "Mark seam", "-", "Grow", "Linked", "Invert", "-", "Hide", "Hide others", "Unhide", "-", "Dissolve", "Delete edges", "Delete" },
				_ => new[] { "Extrude", "Extrude along normals", "Extrude individual", "Inset", "Duplicate", "Subdivide", "-", "Triangulate", "Tris to quads", "Beautify", "Poke faces", "-", "Split", "Separate", "Flip", "Fix normals", "-", "Grow", "Shrink", "Linked", "Similar", "Invert", "-", "Hide", "Hide others", "Unhide", "-", "Delete faces only", "Delete" },
			};

		var tools = new List<EffigyStageTool>();
		foreach ( var label in labels )
		{
			if ( label == "-" )
			{
				if ( tools.Count > 0 && tools[^1] is not null )
					tools.Add( null );
			}
			else if ( MeshToolNamed( label ) is { } tool )
				tools.Add( tool );
		}

		var menu = new Menu( _viewport );
		menu.AddHeading( session.IsRetopologizing ? "Retopology" : session.Mode.ToString() );
		EffigyStageBar.AddTools( menu, tools );
		menu.OpenAtCursor();
	}

	/// <summary>The Model workspace's Edit stage on its home bar: the one button that starts editing.</summary>
	private EffigyStage BuildMeshEditHomeStage()
	{
		var stage = new EffigyStage { Name = "Edit" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.MeshEdit,
			Label = "Edit mesh",
			Tip = "Edit a body's vertices, edges and faces directly. Saved as a Mesh edit in the history",
			Clicked = AddMeshEdit,
		} );

		return stage;
	}

	private List<EffigyStage> BuildMeshEditStages()
	{
		var select = new EffigyStage { Name = "Select" };

		_meshVertexTool = MeshTool( select, EffigyIcon.SelectVertex, "Vertex", "Pick vertices (1)", () => SetMeshElement( EditElement.Vertex ), checkable: true );
		_meshEdgeTool = MeshTool( select, EffigyIcon.SelectEdge, "Edge", "Pick edges (2). Alt+click selects a whole loop, Ctrl+Alt+click the ring around it", () => SetMeshElement( EditElement.Edge ), checkable: true );
		_meshFaceTool = MeshTool( select, EffigyIcon.SelectFace, "Face", "Pick faces (3)", () => SetMeshElement( EditElement.Face ), checkable: true );
		MeshTool( select, EffigyIcon.SelectTool, "All", "Select everything (A). Drag on empty space for a box; click empty space for nothing", MeshSelectAll );
		_meshLassoTool = MeshTool( select, EffigyIcon.SplineTool, "Lasso", "Drag on empty space draws a free lasso instead of a box", ToggleMeshLasso, checkable: true );
		MeshTool( select, EffigyIcon.SelectEdge, "Non-manifold", "Select every open border and three-way junction — on something meant to be solid, each one is a hole or a bad join", () => RunMeshOp( "Non-manifold", s => s.SelectNonManifold() ) );
		MeshTool( select, EffigyIcon.SelectEdge, "Border", "Swap the selected faces for the loop of edges round their edge — ready to bridge, extrude or mark as a seam", () => RunMeshOp( "Border", s => s.SelectBoundaryLoop() ) );
		_meshSharpSelectTool = MeshTool( select, EffigyIcon.SelectEdge, "Sharp edges", "Select every edge where the faces meet at more than 30° — the edges to crease, harden or bevel", () => RunMeshOp( "Sharp edges", s => s.SelectSharpEdges( 30f ) ) );
		_meshMirrorSelectTool = MeshTool( select, EffigyIcon.Mirror, "Mirror", "Add the selection's twin across X, so both sides get the same edit", () => RunMeshOp( "Mirror", s => s.SelectMirror() ) );
		MeshTool( select, EffigyIcon.SelectVertex, "Save selection", "Keep this selection under a name, to get back with Recall — the ear, the fingers, the hem", SaveMeshSelection );
		MeshTool( select, EffigyIcon.SelectVertex, "Recall selection", "Select a saved selection again. Shift adds it, Ctrl takes it away", RecallMeshSelection );
		_meshLooseSelectTool = MeshTool( select, EffigyIcon.SelectVertex, "Loose", "Select the vertices no face uses, to see what Delete loose would remove", () => RunMeshOp( "Loose", s => s.SelectLoose() ) );
		MeshTool( select, EffigyIcon.SelectFace, "Checker", "Drop every other face (or vertex) of the selection in a checkerboard — for alternating panels and studs", () => RunMeshOp( "Checker", s => s.CheckerDeselect() ) );
		_meshCircleTool = MeshTool( select, EffigyIcon.CircleTool, "Circle", "Paint the selection on with a circle brush: hold the mouse and sweep. Ctrl paints it off; [ and ] resize (C)", ToggleMeshCircle, checkable: true );
		_meshGrowTool = MeshTool( select, EffigyIcon.SelectTool, "Grow", "Take in everything touching the selection (+). Click from one face to spread over a region without dragging a box over geometry you cannot see", () => RunMeshOp( "Grow", s => s.GrowSelection() ) );
		_meshShrinkSelectionTool = MeshTool( select, EffigyIcon.SelectTool, "Shrink", "Drop the selection's border, leaving its interior (-)", () => RunMeshOp( "Shrink", s => s.ShrinkSelection() ) );
		_meshLinkedTool = MeshTool( select, EffigyIcon.SelectTool, "Linked", "Take in every piece the selection touches (L). One click on an ear takes the whole ear when it is its own piece", () => RunMeshOp( "Linked", s => s.SelectLinked() ) );
		_meshSimilarTool = MeshTool( select, EffigyIcon.SelectTool, "Similar", "Add every face pointing the same way as the picked faces, within 10 degrees (Shift+L)", () => RunMeshOp( "Similar", s => s.SelectSimilar( MeshEditSession.Similarity.Normal, 10f ) ) );
		MeshTool( select, EffigyIcon.SelectTool, "Invert", "Select everything that is not selected, and nothing that is (Ctrl+I)", () => RunMeshOp( "Invert", s => s.InvertSelection() ) );
		_meshPathTool = MeshTool( select, EffigyIcon.SelectTool, "Path", "Pick two vertices (or two faces) and take the shortest line between them — where a seam or a waistline should run", () => RunMeshOp( "Path", s => s.SelectShortestPath() ) );
		_meshXrayTool = MeshTool( select, EffigyIcon.ProfileInspectorTool, "X-ray", "See through the model: box select picks what is behind too (Alt+Z)", ToggleMeshXray, checkable: true );
		_meshHideTool = MeshTool( select, EffigyIcon.SelectFace, "Hide", "Hide the selected faces so they neither show nor get in the way (H). Alt+H brings everything back", () => RunMeshOp( "Hide", s => s.Hide() ) );
		_meshHideOthersTool = MeshTool( select, EffigyIcon.SelectFace, "Hide others", "Hide everything except the selection — the way to work on a hand without the rest of the body in the way (Shift+H)", () => RunMeshOp( "Hide others", s => s.Hide( unselected: true ) ) );
		_meshUnhideTool = MeshTool( select, EffigyIcon.SelectFace, "Unhide", "Show every hidden face again, selected so it can go straight back (Alt+H)", () => RunMeshOp( "Unhide", s => s.Unhide() ) );
		_meshRandomSelectTool = MeshTool( select, EffigyIcon.SelectVertex, "Random", "Select half of what is showing, at random — for scattering, or roughing up part of a surface", () => RunMeshOp( "Random", s => s.SelectRandom( 0.5f, Environment.TickCount ) ) );
		_meshTrianglesTool = MeshTool( select, EffigyIcon.SelectFace, "Triangles", "Select every triangle — the odd faces in a quad model, worth finding before they cause a pinch", () => RunMeshOp( "Triangles", s => s.SelectFacesBySides( 3 ) ) );
		_meshNgonsTool = MeshTool( select, EffigyIcon.SelectFace, "N-gons", "Select every face with five or more sides — what a subdivide or an export will not like", () => RunMeshOp( "N-gons", s => s.SelectFacesBySides( 5, orMore: true ) ) );
		_meshInteriorTool = MeshTool( select, EffigyIcon.SelectFace, "Interior", "Select faces that point into the model — the leftovers inside a join or boolean, and faces that will show black", () => RunMeshOp( "Interior", s => s.SelectInteriorFaces() ) );

		var move = new EffigyStage { Name = "Transform" };

		_meshMoveTool = MeshTool( move, EffigyIcon.Transform, "Move", "Move handle (G)", () => SetMeshHandle( EffigyViewport.BodyDragMode.Move ), checkable: true );
		_meshRotateTool = MeshTool( move, EffigyIcon.CircularPattern, "Rotate", "Rotate handle (R)", () => SetMeshHandle( EffigyViewport.BodyDragMode.Rotate ), checkable: true );
		_meshScaleTool = MeshTool( move, EffigyIcon.Primitive, "Scale", "Scale handle (S)", () => SetMeshHandle( EffigyViewport.BodyDragMode.Scale ), checkable: true );

		// Typed transforms: the number on the bar is the whole gesture, for when "up by exactly
		// two" matters more than a drag. Hold Ctrl while dragging a handle to snap it instead.
		_meshTypedTools.Clear();
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.Transform, "Move X", "Move the selection along X by exactly this much. Type the number", () => StartMeshOp( "Move X", MeshSize() * 0.05f, ( s, v ) => s.Move( new Vec3( v, 0, 0 ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.Transform, "Move Y", "Move the selection along Y by exactly this much", () => StartMeshOp( "Move Y", MeshSize() * 0.05f, ( s, v ) => s.Move( new Vec3( 0, v, 0 ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.Transform, "Move Z", "Move the selection along Z by exactly this much", () => StartMeshOp( "Move Z", MeshSize() * 0.05f, ( s, v ) => s.Move( new Vec3( 0, 0, v ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.CircularPattern, "Rotate X", "Turn the selection about X through its centre by this many degrees. Shift-click turns about the pivot", () => StartMeshOp( "Rotate X", 15f, ( s, v ) => s.Rotate( new Vec3( 1, 0, 0 ), v, Editor.Application.IsKeyDown( KeyCode.Shift ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.CircularPattern, "Rotate Y", "Turn the selection about Y through its centre by this many degrees. Shift-click turns about the pivot", () => StartMeshOp( "Rotate Y", 15f, ( s, v ) => s.Rotate( new Vec3( 0, 1, 0 ), v, Editor.Application.IsKeyDown( KeyCode.Shift ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.CircularPattern, "Rotate Z", "Turn the selection about Z through its centre by this many degrees. Shift-click turns about the pivot", () => StartMeshOp( "Rotate Z", 15f, ( s, v ) => s.Rotate( new Vec3( 0, 0, 1 ), v, Editor.Application.IsKeyDown( KeyCode.Shift ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.Primitive, "Scale by", "Scale the selection about its centre by this factor: 2 doubles it, 0.5 halves it. Shift-click scales about the pivot", () => StartMeshOp( "Scale", 1.5f, ( s, v ) => s.Scale( new Vec3( v, v, v ), Editor.Application.IsKeyDown( KeyCode.Shift ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.Primitive, "Flatten X", "Squash the selection flat along X, onto the plane through its centre — 0 on that axis, the rest untouched", () => RunMeshOp( "Scale", s => s.Scale( new Vec3( 0, 1, 1 ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.Primitive, "Flatten Y", "Squash the selection flat along Y", () => RunMeshOp( "Scale", s => s.Scale( new Vec3( 1, 0, 1 ) ) ) ) );
		_meshTypedTools.Add( MeshTool( move, EffigyIcon.Primitive, "Flatten Z", "Squash the selection flat along Z", () => RunMeshOp( "Scale", s => s.Scale( new Vec3( 1, 1, 0 ) ) ) ) );
		_meshSoftTool = MeshTool( move, EffigyIcon.SculptSmooth, "Soft", "Soft falloff: moving pulls nearby vertices along too. [ and ] change its reach (O)", ToggleMeshSoft, checkable: true );
		_meshSoftConnectedTool = MeshTool( move, EffigyIcon.SculptSmooth, "Connected", "Soft falloff along the surface only, so a lip moves without the other lip and a finger without the one beside it (Alt+O)", ToggleMeshSoftConnected, checkable: true );
		_meshSoftShapeTool = MeshTool( move, EffigyIcon.SculptSmooth, "Falloff shape", "Cycle the soft falloff's curve: Smooth, Sphere, Root, Inverse Square, Sharp, Linear, Constant", CycleMeshSoftShape );
		_meshSlideTool = MeshTool( move, EffigyIcon.LoopCut, "Edge slide", "Slide the selected edge loop towards one neighbour or the other", () => StartMeshOp( "Edge slide", 0.25f, ( s, v ) => s.EdgeSlide( v ) ) );
		_meshShrinkFattenTool = MeshTool( move, EffigyIcon.Shell, "Shrink/Fatten", "Move the selection along its own normals — thicken a limb, or pull a surface in, without changing its shape (Alt+S)", () => StartMeshOp( "Shrink/Fatten", MeshSize() * 0.02f, ( s, v ) => s.ShrinkFatten( v ) ) );
		MeshTool( move, EffigyIcon.Transform, "Shear", "Lean the selection over along X, more the higher up it is. 1 is 45 degrees", () => StartMeshOp( "Shear", 0.5f, ( s, v ) => s.Shear( v ) ) );
		MeshTool( move, EffigyIcon.CircularPattern, "Bend", "Curl the selection up along its length in X, by this many degrees. The -X end stays put — tails, toes, horns", () => StartMeshOp( "Bend", 90f, ( s, v ) => s.Bend( v ) ) );
		_meshVertexSlideTool = MeshTool( move, EffigyIcon.SelectVertex, "Vertex slide", "Slide the selected vertices along the edge that runs left-to-right across the view. Orbit so the edge you want lies across the screen, then scrub", StartVertexSlide );

		var add = new EffigyStage { Name = "Add" };

		_meshExtrudeTool = MeshTool( add, EffigyIcon.Extrude, "Extrude", "Pull the selected faces out and build walls to them (E)", () => StartMeshOp( "Extrude", MeshSize() * 0.1f, ( s, v ) => s.Extrude( v ) ) );
		_meshExtrudeNormalsTool = MeshTool( add, EffigyIcon.Extrude, "Extrude along normals", "Pull the selected faces along their own normals. The faces stay together (Alt+E)", () => StartMeshOp( "Extrude Along Normals", MeshSize() * 0.1f, ( s, v ) => s.ExtrudeAlongNormals( v ) ) );
		_meshExtrudeIndividualTool = MeshTool( add, EffigyIcon.Extrude, "Extrude individual", "Pull each selected face along its own normal. The faces pull apart", () => StartMeshOp( "Extrude Individual", MeshSize() * 0.1f, ( s, v ) => s.ExtrudeIndividual( v ) ) );
		_meshInsetTool = MeshTool( add, EffigyIcon.Inset, "Inset", "Shrink the selected faces inward, leaving a ring of faces around them (I)", () => StartMeshOp( "Inset", MeshSize() * 0.03f, ( s, v ) => s.Inset( v ) ) );
		_meshLoopCutTool = MeshTool( add, EffigyIcon.LoopCut, "Loop cut", "Cut a new loop across the ring of faces. Move over the model and click where you want it, or select one edge first (Ctrl+R)", StartLoopCut, checkable: true );
		_meshKnifeTool = MeshTool( add, EffigyIcon.CutTool, "Knife", "Click points on the model; each click cuts from the last. X-ray cuts through the back too. Enter or Esc to stop (K)", ToggleMeshKnife, checkable: true );
		_meshBisectTool = MeshTool( add, EffigyIcon.MirrorTool, "Bisect", "Click two points: the model is sliced all the way through along that line", ArmMeshBisect );
		_meshDuplicateTool = MeshTool( add, EffigyIcon.LinearPattern, "Duplicate", "Copy the selected faces in place; move the copy with the handle (Shift+D)", () => RunMeshOp( "Duplicate", s => s.Duplicate() ) );
		_meshBridgeTool = MeshTool( add, EffigyIcon.Loft, "Bridge", "Join two open rims with a band of faces — even with different edge counts. Select an edge loop round each hole", () => RunMeshOp( "Bridge", s => s.BridgeSelectedLoops() ) );
		MeshTool( add, EffigyIcon.Transform, "Pivot here", "Put the pivot — what Spin and Screw turn round — at the middle of the selection. Shift-click puts it back at the origin", SetMeshPivot );
		MeshTool( add, EffigyIcon.CircularPattern, "Spin", "Sweep the selected edges round the up axis through the pivot, for this many degrees — a lathe. Select a rim and spin it into a bowl or a button", () => StartMeshOp( "Spin", 360f, ( s, v ) => s.Spin( v, Math.Max( 4, (int)MathF.Round( MathF.Abs( v ) / 15f ) ) ) ) );
		MeshTool( add, EffigyIcon.CircularPattern, "Screw", "One full turn of Spin that climbs as it goes — this far up Z. Horns, springs, threads", () => StartMeshOp( "Screw", MeshSize() * 0.25f, ( s, v ) => s.Spin( 360f, 24, v ) ) );
		MeshTool( add, EffigyIcon.LinearPattern, "Array", "Repeat the selected faces in a row along X, this many in all — teeth, rivets, buttons", () => StartMeshOp( "Array", 3f, ( s, v ) => s.ArrayFaces( (int)MathF.Round( v ) ) ) );
		MeshTool( add, EffigyIcon.Boolean, "Split", "Tear the selected faces loose from the rest where they stand, so moving them opens a gap (Y)", () => RunMeshOp( "Split", s => s.SplitFaces() ) );
		MeshTool( add, EffigyIcon.Subdivide, "Grid fill", "Cap a hole with a grid of quads that flows with its rim, instead of one big face — for eye sockets, necks and cuffs. The rim needs an even number of edges; the number turns the grid round it", () => StartMeshOp( "Grid fill", 0f, ( s, v ) => s.GridFill( (int)MathF.Round( v ) ) ) );
		MeshTool( add, EffigyIcon.CutTool, "Rip", "Tear the mesh open along the selected edges, so moving them opens a slit — a mouth, a pocket, the front of a jacket (V)", () => RunMeshOp( "Rip", s => s.Rip() ) );
		_meshFillTool = MeshTool( add, EffigyIcon.SelectFace, "Fill", "Cap a hole with one face: select the vertices or edges round it. Three or four loose vertices make a face too (F)", () => RunMeshOp( "Fill", s => s.Fill() ) );
		_meshFillHolesTool = MeshTool( add, EffigyIcon.SelectFace, "Fill holes", "Cap every open hole with up to this many sides, in one go. Bigger openings are left alone: they are more likely a missing side than a hole", () => StartMeshOp( "Fill holes", 4f, ( s, v ) => s.FillHoles( Math.Max( 3, (int)MathF.Round( v ) ) ) ) );
		_meshSubdivideEdgesTool = MeshTool( add, EffigyIcon.Subdivide, "Subdivide edges", "Cut the selected edges into this many pieces. Two opposite edges of a quad make a strip across it; all four make a grid. Nothing is left with a crack", () => StartMeshOp( "Subdivide edges", 2f, ( s, v ) => s.SubdivideEdges( Math.Max( 1, (int)MathF.Round( v ) - 1 ) ) ) );
		_meshRotateEdgeTool = MeshTool( add, EffigyIcon.CircularPattern, "Rotate edge", "Turn the selected edge one corner round the two faces it separates — the fix for a diagonal running the wrong way. Shift-click turns it the other way", () => RunMeshOp( "Rotate edge", s => s.RotateEdge( Editor.Application.IsKeyDown( KeyCode.Shift ) ) ) );
		_meshBeautifyTool = MeshTool( add, EffigyIcon.SelectFace, "Beautify", "Flip the diagonals between the selected triangles wherever that makes them better shaped — what to run after a triangulate leaves slivers", () => RunMeshOp( "Beautify", s => s.BeautifyFaces() ) );
		_meshPipeTool = MeshTool( add, EffigyIcon.CircularPattern, "Pipe", "Build a tube of this radius along the selected edges — cables, pipes, rails, branches. Rings never twist, ends are capped, and the guide edges stay. Shift-click makes it six-sided", () => StartMeshOp( "Pipe", MeshSize() * 0.01f, ( s, v ) => s.Pipe( MathF.Max( v, 0.001f ), Editor.Application.IsKeyDown( KeyCode.Shift ) ? 6 : 8 ) ) );
		_meshScatterTool = MeshTool( add, EffigyIcon.Primitive, "Scatter", "Scatter this many copies of another body over the selected faces (or the whole surface) — rocks over ground, tufts over a field. Stood on the surface, turned at random, sized 80–120%, nothing steeper than 45°. The copies become a body of their own. Shift-click keeps them upright", StartMeshScatter );
		_meshTypedTools.Add( MeshTool( add, EffigyIcon.Transform, "Snap to grid", "Snap the selected vertices onto a grid of this many units — 16 makes a wall piece's edges land where the next piece meets them", () => StartMeshOp( "Snap to grid", 16f, ( s, v ) => s.SnapToGrid( MathF.Max( v, 0.001f ) ) ) ) );
		_meshToPivotTool = MeshTool( add, EffigyIcon.Transform, "To pivot", "Move the selection so its centre lands on the pivot — the other half of Pivot here", () => RunMeshOp( "To pivot", s => s.SelectionToPivot() ) );
		_meshSubdivideTool = MeshTool( add, EffigyIcon.Subdivide, "Subdivide", "Split the selected faces into four, for somewhere you want more detail. With nothing selected it subdivides and smooths the whole body. Skin weights come with it", SubdivideMesh );
		_meshBevelTool = MeshTool( add, EffigyIcon.Fillet, "Bevel", "Round off the selected edges. The number is the width; Shift-click for a flat chamfer (Ctrl+B)", StartBevel );

		var clean = new EffigyStage { Name = "Clean up" };

		_meshMergeTool = MeshTool( clean, EffigyIcon.Merge, "Merge", "Merge the selected vertices at their centre (M)", () => RunMeshOp( "Merge", s => s.MergeAtCentre() ) );
		_meshMergeFirstTool = MeshTool( clean, EffigyIcon.Merge, "Merge first", "Merge the selected vertices onto the first one you clicked", () => RunMeshOp( "Merge", s => s.Merge( MeshEditSession.MergeTarget.First ) ) );
		_meshMergeLastTool = MeshTool( clean, EffigyIcon.Merge, "Merge last", "Merge the selected vertices onto the last one you clicked", () => RunMeshOp( "Merge", s => s.Merge( MeshEditSession.MergeTarget.Last ) ) );
		_meshMergePivotTool = MeshTool( clean, EffigyIcon.Merge, "Merge at pivot", "Merge the selected vertices at the pivot — Pivot here puts it where you want", () => RunMeshOp( "Merge", s => s.Merge( MeshEditSession.MergeTarget.Pivot ) ) );
		MeshTool( clean, EffigyIcon.Merge, "By distance", "Weld vertices that sit on top of each other", () => StartMeshOp( "Merge by distance", MeshSize() * 0.001f, ( s, v ) => s.MergeByDistance( v ) ) );
		_meshDissolveTool = MeshTool( clean, EffigyIcon.Dissolve, "Dissolve", "Remove the selected edges or vertices and join the faces around them", () => RunMeshOp( "Dissolve", s => s.Dissolve() ) );
		_meshLimitedDissolveTool = MeshTool( clean, EffigyIcon.Dissolve, "Limited dissolve", "Merge coplanar faces across the entire mesh to simplify it", () => RunMeshOp( "Limited dissolve", s => s.LimitedDissolve() ) );
		MeshTool( clean, EffigyIcon.DeleteGeometry, "Delete loose", "Remove vertices no face uses — invisible, but still exported", () => RunMeshOp( "Delete loose", s => s.DeleteLoose() ) );
		_meshDeleteTool = MeshTool( clean, EffigyIcon.DeleteGeometry, "Delete", "Delete what is selected, leaving a hole (X)", () => RunMeshOp( "Delete", s => s.Delete() ) );
		_meshDeleteFacesTool = MeshTool( clean, EffigyIcon.DeleteGeometry, "Delete faces only", "Delete exactly the selected faces, keeping the edges and vertices other faces still use", () => RunMeshOp( "Delete faces", s => s.DeleteOnlyFaces() ) );
		_meshDeleteEdgesTool = MeshTool( clean, EffigyIcon.Dissolve, "Delete edges", "Remove the selected edges and join the faces on either side into one, instead of leaving a hole. Faces selected: every edge between them goes", () => RunMeshOp( "Delete edges", s => s.DeleteEdgesKeepFaces() ) );
		_meshRandomizeTool = MeshTool( clean, EffigyIcon.SculptSmooth, "Randomize", "Jitter the selected vertices along their normals by up to this much — quick roughness for rock, bark or cloth", () => StartMeshOp( "Randomize", MeshSize() * 0.01f, ( s, v ) => s.Randomize( v ) ) );
		_meshDecimateTool = MeshTool( clean, EffigyIcon.Dissolve, "Decimate", "Cut the selected faces (or the whole body) down to this share of their triangles, keeping the shape. The rest of the body is untouched", () => StartMeshOp( "Decimate", 0.5f, ( s, v ) => s.DecimateSelection( Math.Clamp( v, 0.01f, 0.99f ) ) ) );
		_meshSplitEdgeTool = MeshTool( clean, EffigyIcon.Boolean, "Make hard", "Make the selected edges shade hard, by unwelding the model along them. Merge by distance puts it back", SplitMeshEdges );
		_meshSharpenTool = MeshTool( clean, EffigyIcon.Chamfer, "Harden creases", "Make every edge that creases more than this angle shade hard, in one go — for a model that should read as hard-surface", () => StartMeshOp( "Harden creases", 40f, ( s, v ) => s.SplitEdgesByAngle( v ) ) );
		_meshSmoothTool = MeshTool( clean, EffigyIcon.SculptSmooth, "Smooth", "Relax the selected vertices towards their neighbours — what you want straight after a subdivide. Open rims stay put. Nothing selected smooths everything", () => StartMeshOp( "Smooth", 0.5f, ( s, v ) => s.Smooth( v ) ) );
		_meshOrganicRelaxTool = MeshTool( clean, EffigyIcon.SculptSmooth, "Relax", "Relax vertices along the surface without collapsing volume — LoopTools Relax", () => StartMeshOp( "Relax", 0.5f, ( s, v ) => s.Relax( v ) ) );
		_meshLoopCircleTool = MeshTool( clean, EffigyIcon.CircularPattern, "Loop circle", "Turn the selected vertices into a regular circle on their best-fit plane — LoopTools Circle", () => RunMeshOp( "Circle", s => s.LoopCircle() ) );
		_meshLoopSpaceTool = MeshTool( clean, EffigyIcon.LinearPattern, "Loop space", "Distribute the selected vertices evenly along their edge loop — LoopTools Space", () => RunMeshOp( "Space", s => s.LoopSpace() ) );
		_meshConnectTool = MeshTool( clean, EffigyIcon.CutTool, "Connect", "Connect two selected vertices by cutting an edge across their shared face (J)", () => RunMeshOp( "Connect", s => s.ConnectVertices() ) );
		_meshFlattenTool = MeshTool( clean, EffigyIcon.SculptSmooth, "Flatten", "Flatten the selected vertices onto their best-fit plane", () => RunMeshOp( "Flatten", s => s.FlattenFaces() ) );
		MeshTool( clean, EffigyIcon.SculptSmooth, "To sphere", "Round the selected vertices off towards a ball — for a head, an eye or a knuckle blocked out as a cube. 1 is a full sphere", () => StartMeshOp( "To sphere", 1f, ( s, v ) => s.ToSphere( v ) ) );
		MeshTool( clean, EffigyIcon.Mirror, "Fix normals", "Make the selected faces (or all of them) agree with each other and face outward — the fix for faces that show black (Shift+N)", () => RunMeshOp( "Recalculate normals", s => s.RecalculateNormals() ) );
		MeshTool( clean, EffigyIcon.Mirror, "Flip", "Turn the selected faces (or all of them) inside out", () => RunMeshOp( "Flip normals", s => s.FlipNormals() ) );
		_meshTriangulateTool = MeshTool( clean, EffigyIcon.SelectFace, "Triangulate", "Split the selected faces into triangles (Ctrl+T)", () => RunMeshOp( "Triangulate", s => s.TriangulateFaces() ) );
		_meshPokeFacesTool = MeshTool( clean, EffigyIcon.SelectVertex, "Poke faces", "Add a vertex in the center of the selected faces and triangulate them (Alt+P)", () => RunMeshOp( "Poke Faces", s => s.PokeFaces() ) );
		_meshTrisToQuadsTool = MeshTool( clean, EffigyIcon.SelectFace, "Tris to quads", "Join neighbouring selected triangles back into quads — the undo for a triangulated import (Alt+J)", () => RunMeshOp( "Tris to Quads", s => s.TrisToQuads() ) );
		_meshBevelVerticesTool = MeshTool( clean, EffigyIcon.Chamfer, "Bevel vertices", "Cut the corner off the selected vertices and cap each notch with a flat face. The number is how far down each edge to cut (Ctrl+Shift+B)", () => StartMeshOp( "Bevel Vertices", MeshSize() * 0.02f, ( s, v ) => s.BevelVertices( v ) ) );

		var surface = new EffigyStage { Name = "Surface" };

		_meshSymmetrizeTool = MeshTool( surface, EffigyIcon.Mirror, "Symmetrize", "Make the model symmetric: keep the +X half and replace the other with a mirror of it. Cuts along X = 0 first, so faces crossing the middle are split rather than lost. Shift-click keeps the -X half instead", SymmetrizeMesh );
		_meshMirrorTool = MeshTool( surface, EffigyIcon.Mirror, "Mirror X", "Edit both sides at once: moving a vertex moves its partner across X = 0", ToggleMeshMirror, checkable: true );
		var modifiers = new EffigyStage { Name = "Modifiers" };

		_meshCreaseTool = MeshTool( modifiers, EffigyIcon.Chamfer, "Crease", "Keep the selected edges sharp under the Smooth modifier (Shift+E). The number is how many levels the fold holds for: 1 for one level, 2 for two; a fraction softens it", () => StartMeshOp( "Crease", 1f, ( s, v ) => s.Crease( Math.Max( v, 0f ) ) ) );
		_meshUncreaseTool = MeshTool( modifiers, EffigyIcon.Chamfer, "Clear crease", "Let the selected edges round off again under Smooth. Nothing selected clears every crease", () => RunMeshOp( "Clear crease", s => s.ClearCreases() ) );

		_modMirrorTool = MeshTool( modifiers, EffigyIcon.Mirror, "Live mirror", "Show and output this mesh mirrored across X, joined down the middle — model half, get the whole. Unlike Symmetrize it stays live: keep editing the half", ToggleModMirror, checkable: true );
		_modArrayTool = MeshTool( modifiers, EffigyIcon.LinearPattern, "Array", "Repeat the whole mesh along X, live. Each click adds a copy, up to five, then off", CycleModArray, checkable: true );
		_modSubdivideTool = MeshTool( modifiers, EffigyIcon.Subdivide, "Smooth", "Show and output the mesh subdivided and smoothed, live — block out in a few quads, see the smooth result. Each click is one more level, up to three, then off", CycleModSubdivide, checkable: true );
		_modSolidifyTool = MeshTool( modifiers, EffigyIcon.Solidify, "Thickness", "Give the surface a thickness, live — draw a garment as a sheet and see it as cloth. Set the exact value in the feature's settings", ToggleModSolidify, checkable: true );

		var retopo = new EffigyStage { Name = "Retopo" };

		_meshRetopoTool = MeshTool( retopo, EffigyIcon.Shrinkwrap, "Retopo", "Draw a clean, light mesh over this body: Ctrl+click places, and everything sticks to the surface. The body itself is not changed", ToggleMeshRetopo, checkable: true );
		_meshEvenQuadsTool = MeshTool( retopo, EffigyIcon.SelectFace, "Even quads", "Each new face is square to the edge it grows from, however far you click. Off: faces reach exactly to the click", ToggleMeshEvenQuads, checkable: true );
		_meshStripTool = MeshTool( retopo, EffigyIcon.Loft, "Strip brush", "Ctrl+drag lays a whole strip of square quads along the stroke — round a limb, down a jaw. Start on the selected edge to continue from it", ToggleMeshStrip, checkable: true );
		_meshRelaxTool = MeshTool( retopo, EffigyIcon.SculptSmooth, "Relax", "Even out the new mesh and put it back on the surface. Works on the selection, or all of it", () => StartMeshOp( "Relax", 0.5f, ( s, v ) => s.RelaxRetopo( v ) ) );
		_meshFinishRetopoTool = MeshTool( retopo, EffigyIcon.Boolean, "Finish retopo", "Lift the new mesh out as a body of its own. The sculpt under it stays exactly as it was", FinishMeshRetopo );

		_meshSnapTool = MeshTool( surface, EffigyIcon.Shrinkwrap, "Snap", "Dragged vertices stick to the surface of the other bodies — for fitting clothing", ToggleMeshSnap, checkable: true );
		_meshExtractTool = MeshTool( surface, EffigyIcon.Shell, "Extract", "Clothing start: copy the selected faces of the body, lift them off the skin by this gap and make them a body of their own", () => StartMeshOp( "Extract garment", MeshSize() * 0.005f, ( s, v ) => s.ExtractGarment( v ) ) );
		_meshSeparateTool = MeshTool( surface, EffigyIcon.Boolean, "Separate", "Move the selected faces out into a body of their own (P)", () => RunMeshOp( "Separate", s => s.Separate() ) );
		_meshLoosePartsTool = MeshTool( surface, EffigyIcon.Boolean, "Loose parts", "Split every disconnected piece off into a body of its own. The biggest piece stays", () => RunMeshOp( "Separate loose parts", s => s.SeparateLooseParts() ) );
		_meshByMaterialTool = MeshTool( surface, EffigyIcon.Boolean, "By material", "Split the body into one body per material. The busiest material stays", () => RunMeshOp( "Separate by material", s => s.SeparateByMaterial() ) );
		_meshWeightsTool = MeshTool( surface, EffigyIcon.BoneBind, "Copy weights", "Give this mesh the skin weights of the rigged body under it, so a garment moves with the body", CopyMeshWeights );
		_meshWrapTool = MeshTool( surface, EffigyIcon.Shrinkwrap, "Shrinkwrap", "Pull the selection (or everything) onto the other bodies, keeping this gap", () => StartMeshOp( "Shrinkwrap", MeshSize() * 0.01f, ( s, v ) => s.Shrinkwrap( v, MeshSize() ) ) );
		MeshTool( surface, EffigyIcon.Solidify, "Solidify", "Give the surface a thickness — the last step of a garment", () => StartMeshOp( "Solidify", MeshSize() * 0.01f, ( s, v ) => s.Solidify( v ) ) );

		var weights = new EffigyStage { Name = "Weights" };

		_meshSmoothWeightsTool = MeshTool( weights, EffigyIcon.SculptSmooth, "Smooth weights", "Relax the skin weights across the selection — the fix for a bound elbow that creases instead of bending. Nothing selected smooths the whole body", () => StartMeshOp( "Smooth weights", 0.5f, ( s, v ) => s.SmoothWeights( v ) ) );
		_meshMirrorWeightsTool = MeshTool( weights, EffigyIcon.Mirror, "Mirror weights", "Copy the weights from the +X half onto the -X half, swapping the rig's left and right bones. What you run after Symmetrize. Shift-click copies the other way", MirrorMeshWeights );
		_meshNormalizeWeightsTool = MeshTool( weights, EffigyIcon.BoneBind, "Fix weights", "Cut every vertex down to the four bones the exporter will actually write, strongest first, summing to one — so what you see here is what the game deforms", NormalizeMeshWeights );

		var cloth = new EffigyStage { Name = "Cloth" };

		_meshDrapeTool = MeshTool( cloth, EffigyIcon.Drape, "Drape", "Let the cloth fall under gravity onto the other bodies. What you have selected is pinned — a collar, a waistband — and the number is how many seconds it settles for", StartMeshDrape );
		_meshFabricTool = MeshTool( cloth, EffigyIcon.Fabric, "Cotton", "What the cloth is made of: cotton hangs soft, denim and leather hold their folds, stretch clings. Click to change", CycleMeshFabric );

		var uv = new EffigyStage { Name = "UV" };

		_meshSeamTool = MeshTool( uv, EffigyIcon.Seam, "Mark seam", "Cut the texture along the selected edges (Ctrl+E). The unwrap is never allowed to cross a seam, so this is where you decide the texture may break — Alt+click takes a whole loop", () => RunMeshOp( "Mark seam", s => s.MarkSeam() ) );
		_meshUnseamTool = MeshTool( uv, EffigyIcon.Seam, "Clear seam", "Unmark the selected edges. With nothing selected, clears every seam on the mesh", ClearMeshSeam );
		_meshProjectUVsTool = MeshTool( uv, EffigyIcon.Unwrap, "Project UVs", "Map the selected faces (or all of them) at a world scale, so a tiling material repeats every this many units — the same on every map prop. Each face takes the axis it most faces", () => StartMeshOp( "Project UVs", _meshUnitsPerTile, ( s, v ) => { _meshUnitsPerTile = MathF.Max( v, 0.01f ); s.ProjectUVs( _meshUnitsPerTile ); } ) );
		_meshPlanarUVsTool = MeshTool( uv, EffigyIcon.Unwrap, "Project from above", "Map the selected faces straight down at this many units per tile — one continuous texture for a floor, a road, a sign. Shift-click projects along X instead", () => StartMeshOp( "Project UVs", _meshUnitsPerTile, ( s, v ) => { _meshUnitsPerTile = MathF.Max( v, 0.01f ); s.ProjectUVs( _meshUnitsPerTile, Editor.Application.IsKeyDown( KeyCode.Shift ) ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 ) ); } ) );
		_meshTrimRowTool = MeshTool( uv, EffigyIcon.Unwrap, "Trim row", "Map the selected faces onto one row of a trim sheet: the number is which row, counting from the top. Runs along the face at the Project UVs scale", () => StartMeshOp( "Trim row", 1f, ( s, v ) => { var row = Math.Clamp( (int)MathF.Round( v ), 1, _meshTrimRows ); s.MapToTrim( (row - 1) / (float)_meshTrimRows, row / (float)_meshTrimRows, _meshUnitsPerTile ); } ) );
		_meshTrimRowsTool = MeshTool( uv, EffigyIcon.Unwrap, "Trim rows", "How many rows the trim sheet has — set it once for the sheet you use", () => { _meshTrimRows = Math.Max( 1, _meshTrimRows == 8 ? 4 : _meshTrimRows == 4 ? 16 : 8 ); SetPrompt( $"Trim sheet: {_meshTrimRows} rows. Trim row picks one." ); } );
		_meshUnwrapTool = MeshTool( uv, EffigyIcon.Unwrap, "Unwrap", "Lay the mesh out flat so it can be painted or baked (U), cutting at your seams and wherever the surface turns more than this angle", StartMeshUnwrap );

		// The toolbar: the tools you hold rather than look up, always on screen. Everything —
		// these included — is also in the menus above it.
		_meshCoreStage = new EffigyStage { Name = "Tools" };
		foreach ( var tool in new[] { _meshVertexTool, _meshEdgeTool, _meshFaceTool, _meshCircleTool, _meshLassoTool,
			_meshMoveTool, _meshRotateTool, _meshScaleTool, _meshExtrudeTool, _meshExtrudeNormalsTool, _meshExtrudeIndividualTool, _meshInsetTool, _meshBevelTool, _meshLoopCutTool, _meshKnifeTool,
			_meshXrayTool, _meshMirrorTool, _meshSnapTool } )
			_meshCoreStage.Add( tool );

		return new List<EffigyStage> { select, move, add, clean, surface, modifiers, retopo, weights, cloth, uv };

	}

	private static EffigyStageTool MeshTool( EffigyStage stage, EffigyIcon icon, string label, string tip, Action clicked, bool checkable = false )
	{
		var tool = new EffigyStageTool { Icon = icon, Label = label, Tip = tip, Clicked = clicked, Checkable = checkable };
		stage.Add( tool );
		return tool;
	}

	/// <summary>Add a Mesh edit on the selected body (or the only one) and step straight in, the way
	/// Paint does: its one input is "which body", which the viewport selection already names.</summary>
	private void AddMeshEdit()
	{
		if ( _viewport is null )
			return;

		if ( _studio.Bodies.Count == 0 )
		{
			SetPrompt( "Edit mesh needs a body — add a primitive, or draw a sketch and extrude it." );
			return;
		}

		LeaveCurrentWorkspace();

		var feature = new MeshEditFeature();

		RecordUndo();
		ApplyIdleGeometrySelection( feature );
		InsertAtRollback( feature );
		RebuildStudio();

		EnterMeshEdit( feature );
	}

	private void EnterMeshEdit( MeshEditFeature feature )
	{
		if ( feature is null || _viewport is null )
			return;

		if ( BarMode == EffigyBarMode.MeshEdit && ReferenceEquals( _meshEditFeature, feature ) && _viewport.IsMeshEditing )
			return;

		LeaveCurrentWorkspace();

		var index = _studio.Features.IndexOf( feature );

		if ( index >= 0 && _studio.RollbackIndex != index + 1 )
		{
			_rollbackBeforeEdit ??= _studio.RollbackIndex;
			_studio.RollbackIndex = index + 1;
			RebuildStudio();
		}

		if ( feature.LastInput is null || feature.Error is not null && feature.Edited is null )
		{
			SetPrompt( feature.Error ?? "This mesh edit has no body under it yet. Pick one body, then try again." );
			return;
		}

		BuildMeshEditBar();

		_meshEditFeature = feature;
		_lastMeshEditFeature = feature;

		BarMode = EffigyBarMode.MeshEdit;

		ShowMeshEditBar();

		_dialog?.Close();

		var session = new MeshEditSession( feature.LastInput, feature.Edited );
		session.LoadCreases( feature.Creases );
		_viewport.BeginMeshEdit( session );
		_uvPanel?.Bind( session );
		_viewport.MeshEditModifiers = MeshModifiersFor( feature );
		_meshEditBar.Bind( session );

		UpdateMeshEditChecks();

		SetPrompt( "Edit: click to select, drag the arrows to move. E extrudes, I insets, Ctrl+R loop cuts, X deletes." );
	}

	/// <summary>Commit and leave. The whole session is one step on the document's undo stack.</summary>
	private void FinishMeshEdit()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.IsPreviewing )
			session.Accept();

		var feature = _meshEditFeature;

		if ( feature is not null && session.HasChanges )
		{
			RecordUndo();
			session.CommitTo( feature );
			_studio.MarkDirty( feature );

			if ( !_dirty )
			{
				_dirty = true;
				UpdateTitle();
			}
		}

		CloseMeshEdit();

		ShowSculptHome();
		RestoreRollbackAfterEdit();
		RebuildStudio();
	}

	/// <summary>End the session without committing — the feature was undone out of the tree.</summary>
	private void CloseMeshEdit()
	{
		_viewport?.EndMeshEdit();
		_meshEditBar?.Bind( null );
		_uvPanel?.Bind( null );
		ShowMeshEditPanel( false );
		_meshOpName = null;
		_meshEditFeature = null;
	}

	// --- operations -----------------------------------------------------------------------------

	private float MeshSize()
	{
		var d = _viewport?.MeshEditSession?.Mesh.BoundsDiagonal ?? 1f;
		return d > 1e-4f ? d : 1f;
	}

	/// <summary>Start a scrubbed operation: preview it now, put its number on the bar.</summary>
	private void StartMeshOp( string name, float amount, Action<MeshEditSession, float> run )
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.IsPreviewing )
			AcceptMeshOp();

		// Rounded to something typeable: 0.1 of a 34.64-inch diagonal is 3.464, and nobody wants to
		// read that on a field they are about to nudge.
		amount = MathF.Round( amount, amount >= 1f ? 1 : 3 );

		if ( !TryPreview( session, name, s => run( s, amount ) ) )
			return;

		_meshOpName = name;
		_meshEditBar?.ShowOperation( name, amount, v => TryPreview( session, name, s => run( s, v ) ) );
		OnMeshEditChanged();
	}

	private bool TryPreview( MeshEditSession session, string name, Action<MeshEditSession> run )
	{
		try
		{
			session.Preview( name, run );
			_viewport?.RefreshMeshEditPreview();
			_meshEditBar?.Refresh();
			return true;
		}
		catch ( Exception e ) when ( e is InvalidOperationException or ArgumentException )
		{
			// A refusal is a sentence for the reader, not a stack trace — every kernel operation
			// throws with one. The preview has already put the mesh back.
			if ( !session.IsPreviewing )
				_meshEditBar?.ShowOperation( null, 0f, null );

			SetPrompt( $"{name}: {e.Message}" );
			return false;
		}
	}

	private void AcceptMeshOp()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		session.Accept();
		_meshOpName = null;
		_meshEditBar?.ShowOperation( null, 0f, null );
		OnMeshEditChanged();
	}

	private void CancelMeshOp()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		session.Cancel();
		_meshOpName = null;
		_meshEditBar?.ShowOperation( null, 0f, null );
		_viewport.RefreshMeshEditPreview();
		OnMeshEditChanged();
	}

	/// <summary>An operation with no number: run it now, as one step.</summary>
	private void RunMeshOp( string name, Action<MeshEditSession> run )
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.IsPreviewing )
			AcceptMeshOp();

		try
		{
			run( session );
		}
		catch ( Exception e ) when ( e is InvalidOperationException or ArgumentException )
		{
			SetPrompt( $"{name}: {e.Message}" );
			return;
		}

		_viewport.RefreshMeshEditPreview();
		OnMeshEditChanged();
	}

	/// <summary>
	/// Arm the loop cut: the loop follows the cursor over the model and a click places it. An edge
	/// already selected skips the hover and cuts there straight away, which is what a keyboard-driven
	/// user expects after Alt+clicking a loop.
	/// </summary>
	private void StartLoopCut()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( _viewport.MeshLoopCutArmed )
		{
			_viewport.MeshLoopCutArmed = false;
			OnMeshEditChanged();
			return;
		}

		if ( session.IsPreviewing )
			AcceptMeshOp();

		if ( session.SelectedEdges.Count == 1 )
		{
			PlaceLoopCut( session.SelectedEdges.First() );
			return;
		}

		_viewport.MeshKnifeArmed = false;
		_viewport.MeshLoopCutArmed = true;
		SetPrompt( "Loop cut: move over the model to see where the loop lands, then click to place it. Escape puts it down." );
		OnMeshEditChanged();
	}

	/// <summary>An edge was chosen — by the click, or by already being selected. The cut itself is
	/// the usual scrubbed operation, so the loop can still be slid before it is accepted.</summary>
	private void PlaceLoopCut( EdgeKey edge )
	{
		StartMeshOp( "Loop cut", 0.5f, ( s, v ) => s.LoopCut( edge, Math.Clamp( v, 0.02f, 0.98f ) ) );
	}

	private void SetMeshElement( EditElement element )
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		session.SetMode( element );
		OnMeshEditChanged();
	}

	private void MeshSelectAll()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		var all = session.Mode switch
		{
			EditElement.Vertex => session.SelectedVertices.Count >= session.Mesh.VertexCount,
			EditElement.Edge => false,
			_ => session.SelectedFaces.Count >= session.Mesh.FaceCount,
		};

		// A second A clears, the way Blender's used to — "select all" pressed on everything
		// selected has only one useful meaning.
		if ( all )
			session.ClearSelection();
		else
			session.SelectAll();

		OnMeshEditChanged();
	}

	private void ToggleMeshMirror()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		session.MirrorX = !session.MirrorX;
		OnMeshEditChanged();
	}

	/// <summary>
	/// Hang the cloth. The pins are whatever is selected and the floor is every other body, which is
	/// the garment case exactly: select the collar, press Drape, scrub the seconds until the shirt
	/// sits right. The collider is passed in rather than taken from Snap, so draping does not turn
	/// drag-snapping on behind the user's back.
	/// </summary>
	private void StartMeshDrape()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		var floor = session.SnapTarget ?? OtherBodiesMesh();

		if ( session.AffectedVertices().Count == 0 && floor is null )
		{
			SetPrompt( "Drape needs something to hold the cloth up: select the vertices that pin it — a collar, a waistband — or add the body it should land on." );
			return;
		}

		var thickness = MeshSize() * 0.004f;

		StartMeshOp( "Drape", 1f, ( s, v ) => s.Drape( v, _meshFabric, thickness, floor ) );
	}

	/// <summary>Cotton, denim, leather, stretch — and the button wears the name of the one it is on,
	/// so the setting is readable without opening anything.</summary>
	private void CycleMeshFabric()
	{
		var all = (Fabric[])Enum.GetValues( typeof( Fabric ) );
		_meshFabric = all[(Array.IndexOf( all, _meshFabric ) + 1) % all.Length];

		if ( _meshFabricTool is not null )
			_meshFabricTool.Label = _meshFabric.ToString();

		SetPrompt( $"Fabric: {_meshFabric}. Drape again to see it hang differently." );
		OnMeshEditChanged();
	}

	/// <summary>
	/// Vertex slide. The rail is chosen against the view's right, which is the closest thing to
	/// Blender's "drag towards the edge you want" that a scrubbed number can be: the direction is
	/// read ONCE when the tool starts, so orbiting mid-scrub does not change which edge you are on.
	/// </summary>
	private void StartVertexSlide()
	{
		if ( _viewport is null )
			return;

		var direction = _viewport.CameraRight;

		StartMeshOp( "Vertex slide", 0.25f, ( s, v ) => s.VertexSlide( v, direction ) );
	}

	/// <summary>
	/// Subdivide, saying what it will cost first. This is the one operation that can turn a workable
	/// mesh into an unworkable one in a single click, so past a limit it asks for the click twice:
	/// the first press reports the cost and the second does it.
	/// </summary>
	private void SubdivideMesh()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.IsPreviewing )
			AcceptMeshOp();

		var (vertices, faces) = session.PredictSubdivide();
		var where = session.SelectedFaces.Count > 0
			? $"{session.SelectedFaces.Count} face(s)"
			: "the whole body";

		// Confirm only when it is genuinely large. Asking every time trains you to click through it.
		const int heavy = 100_000;

		if ( faces > heavy && _meshSubdivideWarned != faces )
		{
			_meshSubdivideWarned = faces;
			SetPrompt( $"Subdividing {where} would make {faces:N0} faces and {vertices:N0} vertices, which will be slow to work with. Press Subdivide again to go ahead." );
			return;
		}

		_meshSubdivideWarned = -1;
		RunMeshOp( "Subdivide", s => s.Subdivide() );
		SetPrompt( $"Subdivided {where}: {session.Mesh.FaceCount:N0} faces, {session.Mesh.VertexCount:N0} vertices." );
	}

	private int _meshSubdivideWarned = -1;

	/// <summary>
	/// Symmetrize. Destructive in a way the other tools are not — half the model is replaced — so it
	/// says which half it kept afterwards, and Shift picks the other one.
	/// </summary>
	private void SymmetrizeMesh()
	{
		var keepPositive = !Editor.Application.IsKeyDown( KeyCode.Shift );

		RunMeshOp( "Symmetrize", s => s.Symmetrize( keepPositive ) );

		if ( _viewport?.MeshEditSession is { } session )
			SetPrompt( $"Symmetrized about X = 0, keeping the {(keepPositive ? "+X" : "-X")} half: {session.Mesh.FaceCount:N0} faces. Shift-click to keep the other side instead." );
	}

	/// <summary>
	/// Cut the weights down to what the exporter will really write. Says how many vertices were
	/// affected, because that number is the interesting part: anything above zero was deforming
	/// differently in the game from what the viewport was showing.
	/// </summary>
	private void NormalizeMeshWeights()
	{
		RunMeshOp( "Fix weights", s =>
		{
			var changed = s.NormalizeWeights( DmxWriter.MaxInfluences );

			SetPrompt( changed == 0
				? "Weights are already what the exporter will write: four bones a vertex, summing to one."
				: $"Fixed {changed:N0} vertex(es) that carried more influences than the exporter writes — those were deforming differently in game." );
		} );
	}

	/// <summary>
	/// Mirror weights, pairing the rig's bones by name. The kernel does not know what a bone is
	/// called, so the L/R map is built here and handed in.
	/// </summary>
	private void MirrorMeshWeights()
	{
		if ( _viewport?.MeshEditSession is null )
			return;

		var partner = BoneMirrorMap( out var pairs );

		if ( pairs == 0 )
			SetPrompt( "No left/right bone pairs in this rig, so weights will mirror onto the same bones. That is right for a spine and wrong for arms." );

		var fromPositive = !Editor.Application.IsKeyDown( KeyCode.Shift );

		RunMeshOp( "Mirror weights", s =>
		{
			var copied = s.MirrorWeights( partner, fromPositive );

			SetPrompt( copied == 0
				? "No vertex on the far side lined up with one on this side. Symmetrize the geometry first — mirrored weights need mirrored vertices."
				: $"Mirrored weights onto {copied:N0} vertex(es) across {pairs} left/right bone pair(s)." );
		} );
	}

	/// <summary>
	/// Each bone's mirror partner, by name. The Citizen names its sides with an `_L`/`_R` suffix
	/// (`arm_upper_L`), which is the convention this follows; a bone whose partner is not in the rig
	/// maps to itself, which is the right answer for a spine and a visible-enough wrong one for a
	/// limb that was named by hand.
	/// </summary>
	private Func<int, int> BoneMirrorMap( out int pairs )
	{
		var bones = _studio.Rig.Bones;
		var byName = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );

		for ( var i = 0; i < bones.Count; i++ )
			if ( !string.IsNullOrEmpty( bones[i].Name ) )
				byName[bones[i].Name] = i;

		var map = new int[bones.Count];
		var found = 0;

		for ( var i = 0; i < bones.Count; i++ )
		{
			map[i] = i;

			var name = bones[i].Name;

			if ( string.IsNullOrEmpty( name ) )
				continue;

			// `arm_upper_L` <-> `arm_upper_R`, and the same with a trailing number (`finger_L1`).
			var flipped = FlipSideSuffix( name );

			if ( flipped is not null && byName.TryGetValue( flipped, out var other ) )
			{
				map[i] = other;
				found++;
			}
		}

		pairs = found / 2;
		return bone => bone >= 0 && bone < map.Length ? map[bone] : bone;
	}

	/// <summary>`..._L` becomes `..._R` and back, including when digits follow the side letter.
	/// Null when the name does not name a side.</summary>
	private static string FlipSideSuffix( string name )
	{
		for ( var i = name.Length - 1; i >= 1; i-- )
		{
			if ( char.IsDigit( name[i] ) )
				continue;

			var c = name[i];

			if ( (c != 'L' && c != 'R' && c != 'l' && c != 'r') || name[i - 1] != '_' )
				return null;

			var swapped = c switch { 'L' => 'R', 'R' => 'L', 'l' => 'r', _ => 'l' };
			return name[..i] + swapped + name[(i + 1)..];
		}

		return null;
	}

	/// <summary>Split, saying how many edges genuinely came apart — which is not always all of them,
	/// because a cut that does not reach a boundary leaves its ends joined.</summary>
	private void SplitMeshEdges()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		var asked = session.SelectedEdges.Count;

		RunMeshOp( "Make hard", s =>
		{
			var split = s.SplitEdges();

			SetPrompt( split == asked
				? $"{split} edge(s) now shade hard."
				: $"{split} of {asked} edge(s) came apart. The rest are still joined at their ends — a cut only separates where it reaches a boundary or another cut." );
		} );
	}

	/// <summary>Unmark the selected edges, or — with nothing selected — every seam on the mesh, which
	/// is the way out of a marking session that went wrong.</summary>
	private void ClearMeshSeam()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.Seams.Count == 0 )
		{
			SetPrompt( "There are no seams to clear." );
			return;
		}

		if ( session.SelectedEdges.Count == 0 )
		{
			var all = session.Seams.Count;
			RunMeshOp( "Clear seams", s => s.ClearSeams() );
			SetPrompt( $"Cleared all {all} seam edge(s)." );
			return;
		}

		RunMeshOp( "Clear seam", s => s.MarkSeam( false ) );
	}

	/// <summary>
	/// Unwrap, scrubbing the charting angle. The report is worth saying out loud — the island count
	/// is how you tell a good unwrap from one that shattered, and a skipped face is a degenerate one
	/// that will bake as a hole.
	/// </summary>
	private void StartMeshUnwrap()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.Seams.Count == 0 )
			SetPrompt( "Unwrapping on the angle alone. Mark seams first to choose where the texture may break." );

		StartMeshOp( "Unwrap", 66f, ( s, v ) =>
		{
			var report = s.Unwrap( Math.Clamp( v, 1f, 179f ) );
			SetPrompt( $"Unwrap: {report}" );
		} );
	}

	/// <summary>Scatter another body over this one. One other body is used as it is; more than one
	/// asks which, then the count scrubs like any other operation.</summary>
	private void StartMeshScatter()
	{
		if ( _viewport?.MeshEditSession is null )
			return;

		var upright = Editor.Application.IsKeyDown( KeyCode.Shift );
		var others = _studio.Bodies.Where( b => b.Id != _meshEditFeature?.LastBodyId && b.Mesh is { FaceCount: > 0 } ).ToList();

		void Go( PolyMesh prop, string name )
		{
			StartMeshOp( "Scatter", 20f, ( s, v ) =>
			{
				var placed = s.Scatter( prop, Math.Max( 1, (int)MathF.Round( v ) ), seed: 1, minScale: 0.8f, maxScale: 1.2f, alignToSurface: !upright, maxSlopeDegrees: 45f );
				SetPrompt( $"{placed} copies of {name} scattered. They are a body of their own once you leave the edit." );
			} );
		}

		if ( others.Count == 0 )
		{
			SetPrompt( "Scatter needs another body to scatter — the rock, the tuft, the crate. There is only this one." );
			return;
		}

		if ( others.Count == 1 )
		{
			Go( others[0].Mesh, others[0].Name );
			return;
		}

		var menu = new Menu( _viewport );
		menu.AddHeading( "Scatter which body?" );
		foreach ( var body in others )
		{
			var captured = body;
			menu.AddOption( captured.Name ?? captured.Id, null, () => Go( captured.Mesh, captured.Name ?? captured.Id ) );
		}

		menu.OpenAtCursor();
	}

	/// <summary>Snap onto every OTHER body — the one being edited cannot be its own target, or a
	/// dragged vertex would stick to where it already is.</summary>
	private void ToggleMeshSnap()
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return;

		if ( session.SnapTarget is not null )
		{
			session.SnapTarget = null;
			OnMeshEditChanged();
			return;
		}

		var target = OtherBodiesMesh();

		if ( target is null )
		{
			SetPrompt( "Snap needs another body to stick to — a character to fit clothing onto, say. There is only this one." );
			UpdateMeshEditChecks();
			return;
		}

		session.SnapTarget = target;
		session.SnapOffset = MeshSize() * 0.005f;
		SetPrompt( "Snap on: dragged vertices land on the nearest surface of the other bodies." );
		OnMeshEditChanged();
	}

	private PolyMesh OtherBodiesMesh( bool withSkin = false )
	{
		var skip = _meshEditFeature?.LastBodyId;
		var combined = new PolyMesh();
		var skin = withSkin ? new SkinWeights() : null;

		// Everything built, not just what the rollback shows: fitting a shirt to a body that is
		// further down the history is the normal case, not the exception.
		foreach ( var body in _studio.Bodies )
		{
			if ( body.Id == skip || body.Mesh is null || body.Mesh.FaceCount == 0 )
				continue;

			var offset = combined.VertexCount;
			combined.Positions.AddRange( body.Mesh.Positions );

			// Weights only count when every contributing body has them — half a skinned source
			// would hand the garment bone 0 wherever it lay over the unrigged half.
			if ( skin is not null )
			{
				if ( body.Mesh.IsRigged )
					skin.Vertices.AddRange( body.Mesh.Skin.Vertices );
				else
					skin = null;
			}

			foreach ( var face in body.Mesh.Faces )
			{
				var indices = new int[face.Indices.Length];
				for ( var i = 0; i < indices.Length; i++ )
					indices[i] = face.Indices[i] + offset;

				combined.AddFace( indices, (Vec2[])face.UVs.Clone(), face.Material );
			}
		}

		combined.Skin = skin;
		return combined.FaceCount == 0 ? null : combined;
	}

	/// <summary>Shrinkwrap and snap both need the target; wrap is useless without one.</summary>
	private void OnMeshEditChanged()
	{
		UpdateMeshEditChecks();
		_meshEditBar?.Refresh();
		RefreshMeshEditPanel();
		_uvPanel?.Refresh();
	}

	/// <summary>Ticks on the pick-mode and toggle buttons, and greyed tools that say what they need —
	/// a tool that cannot use the selection says what to pick instead of doing nothing.</summary>
	private void UpdateMeshEditChecks()
	{
		var session = _viewport?.MeshEditSession;

		if ( _meshVertexTool is not null )
		{
			_meshVertexTool.Checked = session?.Mode == EditElement.Vertex;
			_meshEdgeTool.Checked = session?.Mode == EditElement.Edge;
			_meshFaceTool.Checked = session?.Mode == EditElement.Face;
			_meshMirrorTool.Checked = session?.MirrorX ?? false;
			_meshSnapTool.Checked = session?.SnapTarget is not null;
			_meshXrayTool.Checked = _viewport?.MeshXray ?? false;
			_meshKnifeTool.Checked = _viewport?.MeshKnifeArmed ?? false;
			_meshSoftTool.Checked = session is { SoftRadius: > 0f };
			_meshSoftConnectedTool.Checked = session is { SoftConnected: true };
			_meshMoveTool.Checked = _viewport?.MeshHandleMode == EffigyViewport.BodyDragMode.Move;
			_meshRotateTool.Checked = _viewport?.MeshHandleMode == EffigyViewport.BodyDragMode.Rotate;
			_meshScaleTool.Checked = _viewport?.MeshHandleMode == EffigyViewport.BodyDragMode.Scale;

			var faces = session?.SelectedFaces.Count ?? 0;
			var edges = session?.SelectedEdges.Count ?? 0;
			var verts = session?.AffectedVertices().Count ?? 0;

			Need( _meshExtrudeTool, faces > 0 || edges > 0, "Select one or more faces (3) or edges (2) to extrude" );
			Need( _meshExtrudeNormalsTool, faces > 0, "Select one or more faces (3) to extrude" );
			Need( _meshExtrudeIndividualTool, faces > 0, "Select one or more faces (3) to extrude" );
			Need( _meshInsetTool, faces > 0, "Select one or more faces (3) to inset" );
			_meshLoopCutTool.Checked = _viewport?.MeshLoopCutArmed ?? false;
			Need( _meshBevelTool, edges > 0 || faces > 0, "Select the edges (2) to round off, or faces (3) to round their border" );
			Need( _meshDuplicateTool, faces > 0, "Select faces (3) to duplicate" );
			Need( _meshSeparateTool, faces > 0, "Select the faces (3) to split off into their own body" );
			Need( _meshExtractTool, faces > 0, "Select the body's faces (3) the garment should cover" );
			Need( _meshBridgeTool, edges >= 6, "Select an edge loop round each of two open holes (Alt+click)" );
			_meshLassoTool.Checked = _viewport?.MeshLasso ?? false;
			_meshCircleTool.Checked = _viewport?.MeshCircle ?? false;
			_meshRetopoTool.Checked = session is { IsRetopologizing: true };
			_meshEvenQuadsTool.Checked = session is { EvenQuads: true };
			_meshStripTool.Checked = _viewport?.RetopoStrip ?? false;
			_modMirrorTool.Checked = _meshEditFeature is { MirrorX.Value: true };
			_modArrayTool.Checked = _meshEditFeature?.ArrayCount.Clamped > 1;
			_modSubdivideTool.Checked = _meshEditFeature?.SubdivideLevels.Clamped > 0;
			_modSolidifyTool.Checked = _meshEditFeature?.SolidifyThickness.Value > 0f;
			Need( _meshSlideTool, edges > 0, "Select an edge loop (Alt+click an edge in Edge mode) to slide" );
			Need( _meshVertexSlideTool, verts > 0, "Select the vertices (1) to slide along their edges" );
			Need( _meshMergeTool, verts >= 2, "Select two or more vertices to merge" );
			Need( _meshDissolveTool, session is not null && session.Mode != EditElement.Face && verts > 0, "Select edges (2) or vertices (1) to dissolve" );
			Need( _meshDeleteTool, verts > 0, "Select something to delete" );
			Need( _meshDeleteFacesTool, faces > 0, "Select faces (3) to delete" );
			Need( _meshDeleteEdgesTool, edges > 0 || faces > 1, "Select edges (2) to remove, or neighbouring faces (3) to join" );
			Need( _meshMergeFirstTool, verts >= 2, "Click two or more vertices (1), in the order that matters" );
			Need( _meshMergeLastTool, verts >= 2, "Click two or more vertices (1), in the order that matters" );
			Need( _meshMergePivotTool, verts >= 2, "Select two or more vertices to merge" );
			Need( _meshRandomizeTool, verts > 0, "Select the vertices to jitter" );
			Need( _meshDecimateTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to decimate" );
			Need( _meshHideTool, verts > 0, "Select what to hide (H)" );
			Need( _meshHideOthersTool, verts > 0, "Select what to keep showing (Shift+H)" );
			Need( _meshUnhideTool, session is { HasHiddenFaces: true }, "Nothing is hidden" );
			Need( _meshRandomSelectTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to select" );
			Need( _meshTrianglesTool, session is { Mesh.FaceCount: > 0 }, "There are no faces" );
			Need( _meshNgonsTool, session is { Mesh.FaceCount: > 0 }, "There are no faces" );
			Need( _meshInteriorTool, session is { Mesh.FaceCount: > 0 }, "There are no faces" );
			Need( _meshLoosePartsTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to split" );
			Need( _meshCreaseTool, edges > 0 || faces > 0, "Select the edges (2) that should stay sharp when smoothed (Shift+E)" );
			Need( _meshRotateEdgeTool, edges > 0, "Select the edge (2) to turn" );
			Need( _meshSubdivideEdgesTool, edges > 0 || faces > 0, "Select the edges (2) to cut" );
			Need( _meshFillHolesTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to fill" );
			Need( _meshBeautifyTool, faces > 1, "Select the triangles (3) to tidy" );
			Need( _meshSharpSelectTool, session is { Mesh.FaceCount: > 0 }, "There are no edges" );
			Need( _meshMirrorSelectTool, verts > 0, "Select something to mirror the selection of" );
			Need( _meshLooseSelectTool, session is { Mesh.VertexCount: > 0 }, "There are no vertices" );
			Need( _meshToPivotTool, verts > 0, "Select what to move to the pivot" );
			Need( _meshProjectUVsTool, session is { Mesh.FaceCount: > 0 }, "There are no faces to map" );
			Need( _meshPlanarUVsTool, session is { Mesh.FaceCount: > 0 }, "There are no faces to map" );
			Need( _meshTrimRowTool, faces > 0, "Select the faces (3) to put on a trim row" );
			Need( _meshPipeTool, edges > 0, "Select the run of edges (2) to build a tube along" );
			Need( _meshScatterTool, session is { Mesh.FaceCount: > 0 } && _studio.Bodies.Count( b => b.Id != _meshEditFeature?.LastBodyId && b.Mesh is { FaceCount: > 0 } ) > 0, "Scatter needs another body to scatter over this one — add the rock first" );
			foreach ( var typed in _meshTypedTools )
				Need( typed, verts > 0, "Select what to move" );
			Need( _meshUncreaseTool, edges > 0 || faces > 0 || session is { Creases.Count: > 0 }, "There are no creases" );
			Need( _meshByMaterialTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to split" );
			Need( _meshWrapTool, session is not null && _studio.Bodies.Count > 1, "Needs another body to wrap onto" );
			Need( _meshSubdivideTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to subdivide" );
			Need( _meshSmoothTool, session is { Mesh.VertexCount: > 0 }, "There is nothing to smooth" );
			Need( _meshShrinkFattenTool, verts > 0, "Select what you want to move along its normals" );
			Need( _meshGrowTool, verts > 0, "Select something to grow from" );
			Need( _meshShrinkSelectionTool, verts > 0, "There is no selection to shrink" );
			Need( _meshLinkedTool, verts > 0, "Select something to grow from" );
			Need( _meshSimilarTool, session is { SelectedFaces.Count: > 0 }, "Select a face (3) to match against" );
			Need( _meshPathTool, session is { SelectedVertices.Count: 2 } || session is { SelectedFaces.Count: 2 }, "Pick exactly two vertices (1) or two faces (3)" );
			Need( _meshFillTool, verts >= 3, "Select the vertices or edges round a hole" );
			Need( _meshTriangulateTool, faces > 0, "Select faces (3) to triangulate (Ctrl+T)" );
			Need( _meshPokeFacesTool, faces > 0, "Select faces (3) to poke (Alt+P)" );
			Need( _meshTrisToQuadsTool, faces > 1, "Select neighbouring triangles (3) to join (Alt+J)" );
			Need( _meshBevelVerticesTool, verts > 0, "Select the vertices (1) whose corners to cut (Ctrl+Shift+B)" );
			Need( _meshLimitedDissolveTool, session is { Mesh.FaceCount: > 0 }, "There are no faces to dissolve" );
			Need( _meshFlattenTool, verts >= 3, "Select 3 or more vertices to flatten" );
			Need( _meshConnectTool, verts == 2, "Select exactly two vertices sharing a face to connect (J)" );
			Need( _meshLoopCircleTool, verts >= 3, "Select at least 3 vertices or an edge loop to make circular" );
			Need( _meshLoopSpaceTool, verts >= 3, "Select at least 3 vertices or an edge loop to space evenly" );
			Need( _meshOrganicRelaxTool, session is { Mesh.VertexCount: > 0 }, "There is nothing to relax" );
			Need( _meshRelaxTool, session is { IsRetopologizing: true }, "Start Retopo first" );
			Need( _meshEvenQuadsTool, session is { IsRetopologizing: true }, "Start Retopo first" );
			Need( _meshStripTool, session is { IsRetopologizing: true }, "Start Retopo first" );
			Need( _meshFinishRetopoTool, session is { IsRetopologizing: true }, "Start Retopo first" );
			Need( _meshSplitEdgeTool, edges > 0, "Select the edges (2) that should shade hard" );
			Need( _meshSharpenTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to harden" );
			Need( _meshSymmetrizeTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to symmetrize" );

			var hasWeights = session is { Mesh.IsRigged: true };
			Need( _meshNormalizeWeightsTool, hasWeights, "This body has no skin weights yet — rig it first" );
			Need( _meshSmoothWeightsTool, hasWeights, "This body has no skin weights yet — rig it first" );
			Need( _meshMirrorWeightsTool, hasWeights, "This body has no skin weights yet — rig it first" );
			Need( _meshSeamTool, edges > 0, "Select the edges (2) to cut the texture along. Alt+click takes a whole loop" );
			Need( _meshUnseamTool, session is { Seams.Count: > 0 }, "There are no seams to clear" );
			Need( _meshUnwrapTool, session is { Mesh.FaceCount: > 0 }, "There is nothing to unwrap" );
			Need( _meshDrapeTool, session is not null && (verts > 0 || _studio.Bodies.Count > 1),
				"Select the vertices that pin the cloth up, or add the body it should land on" );
		}

		_stageBar?.Refresh();
	}

	private static void Need( EffigyStageTool tool, bool ok, string reason )
	{
		if ( tool is null )
			return;

		tool.Enabled = ok;
		tool.DisabledReason = ok ? null : reason;
	}

	// --- keys -----------------------------------------------------------------------------------

	/// <summary>Edit mode's keys. Each existing viewport shortcut that shares a key calls this first
	/// and stops if it answered — the same guard-per-mode arrangement X already uses for sculpt and
	/// paint.</summary>
	private const char MeshKeyEnter = (char)13;
	private const char MeshKeyEscape = (char)27;

	private bool MeshEditKey( char key )
	{
		if ( _viewport?.MeshEditSession is null )
			return false;

		switch ( key )
		{
			case '1': SetMeshElement( EditElement.Vertex ); return true;
			case '2': SetMeshElement( EditElement.Edge ); return true;
			case '3': SetMeshElement( EditElement.Face ); return true;
			case 'E': _meshExtrudeTool?.Clicked?.Invoke(); return true;
			case 'I': _meshInsetTool?.Clicked?.Invoke(); return true;
			case MeshKeyLoopCut: StartLoopCut(); return true;
			case MeshKeyBevel: StartBevel(); return true;
			case MeshKeyXray: ToggleMeshXray(); return true;
			case MeshKeySeam: RunMeshOp( "Mark seam", s => s.MarkSeam() ); return true;
			case 'U': StartMeshUnwrap(); return true;
			case 'K': ToggleMeshKnife(); return true;
			case 'D': RunMeshOp( "Duplicate", s => s.Duplicate() ); return true;
			case 'P': RunMeshOp( "Separate", s => s.Separate() ); return true;
			case 'O': ToggleMeshSoft(); return true;
			case MeshKeySoftConnected: ToggleMeshSoftConnected(); return true;
			case MeshKeyHide: RunMeshOp( "Hide", s => s.Hide() ); return true;
			case MeshKeyCrease: _meshCreaseTool?.Clicked?.Invoke(); return true;
			case MeshKeyHideOthers: RunMeshOp( "Hide others", s => s.Hide( unselected: true ) ); return true;
			case MeshKeyUnhide: RunMeshOp( "Unhide", s => s.Unhide() ); return true;
			case '[': ScaleMeshSoft( 0.8f ); return true;
			case ']': ScaleMeshSoft( 1.25f ); return true;
			case 'G': case 'W': SetMeshHandle( EffigyViewport.BodyDragMode.Move ); return true;
			case 'R': SetMeshHandle( EffigyViewport.BodyDragMode.Rotate ); return true;
			case 'S': SetMeshHandle( EffigyViewport.BodyDragMode.Scale ); return true;
			case 'X': RunMeshOp( "Delete", s => s.Delete() ); return true;
			case 'M': RunMeshOp( "Merge", s => s.MergeAtCentre() ); return true;
			case 'A': MeshSelectAll(); return true;
			case '+': case '=': RunMeshOp( "Grow", s => s.GrowSelection() ); return true;
			case '-': RunMeshOp( "Shrink", s => s.ShrinkSelection() ); return true;
			case MeshKeySimilar: RunMeshOp( "Similar", s => s.SelectSimilar( MeshEditSession.Similarity.Normal, 10f ) ); return true;
			case 'L': RunMeshOp( "Linked", s => s.SelectLinked() ); return true;
			case 'F': RunMeshOp( "Fill", s => s.Fill() ); return true;
			case 'C': ToggleMeshCircle(); return true;
			case 'J': RunMeshOp( "Connect", s => s.ConnectVertices() ); return true;
			case 'V': RunMeshOp( "Rip", s => s.Rip() ); return true;
			case MeshKeySplit: RunMeshOp( "Split", s => s.SplitFaces() ); return true;
			case MeshKeySearch: OpenMeshToolSearch(); return true;
			case MeshKeyExtrudeNormals: _meshExtrudeNormalsTool?.Clicked?.Invoke(); return true;
			case MeshKeyTriangulate: RunMeshOp( "Triangulate", s => s.TriangulateFaces() ); return true;
			case MeshKeyPokeFaces: RunMeshOp( "Poke Faces", s => s.PokeFaces() ); return true;
			case MeshKeyLimitedDissolve: RunMeshOp( "Limited dissolve", s => s.LimitedDissolve() ); return true;
			case MeshKeyTrisToQuads: RunMeshOp( "Tris to Quads", s => s.TrisToQuads() ); return true;
			case MeshKeyBevelVertices: _meshBevelVerticesTool?.Clicked?.Invoke(); return true;
			case MeshKeyEscape when _viewport.MeshCircle: ToggleMeshCircle(); return true;
			case MeshKeyInvert: RunMeshOp( "Invert", s => s.InvertSelection() ); return true;
			case MeshKeyRecalculate: RunMeshOp( "Recalculate normals", s => s.RecalculateNormals() ); return true;
			case MeshKeyShrinkFatten: _meshShrinkFattenTool?.Clicked?.Invoke(); return true;
			case MeshKeyEnter when _viewport.MeshKnifeArmed:
			case MeshKeyEscape when _viewport.MeshKnifeArmed:
				ToggleMeshKnife();
				return true;
			case MeshKeyEnter when _viewport.MeshLoopCutArmed:
			case MeshKeyEscape when _viewport.MeshLoopCutArmed:
				_viewport.MeshLoopCutArmed = false;
				OnMeshEditChanged();
				return true;
			case MeshKeyEnter: AcceptMeshOp(); return true;
			case MeshKeyEscape:
				if ( _viewport.MeshEditSession.IsPreviewing )
					CancelMeshOp();
				else
				{
					_viewport.MeshEditSession.ClearSelection();
					OnMeshEditChanged();
				}
				return true;
		}

		return false;
	}

	[Shortcut( "effigy.mesh.inset", "I", typeof( EffigyViewport ) )]
	private void ShortcutMeshInset() => MeshEditKey( 'I' );

	[Shortcut( "effigy.mesh.loopcut", "CTRL+R", typeof( EffigyViewport ) )]
	private void ShortcutMeshLoopCut() => MeshEditKey( MeshKeyLoopCut );

	[Shortcut( "effigy.mesh.bevel", "CTRL+B", typeof( EffigyViewport ) )]
	private void ShortcutMeshBevel() => MeshEditKey( MeshKeyBevel );

	[Shortcut( "effigy.mesh.xray", "ALT+Z", typeof( EffigyViewport ) )]
	private void ShortcutMeshXray() => MeshEditKey( MeshKeyXray );

	[Shortcut( "effigy.mesh.seam", "CTRL+E", typeof( EffigyViewport ) )]
	private void ShortcutMeshSeam() => MeshEditKey( MeshKeySeam );

	[Shortcut( "effigy.mesh.unwrap", "U", typeof( EffigyViewport ) )]
	private void ShortcutMeshUnwrap() => MeshEditKey( 'U' );

	[Shortcut( "effigy.mesh.shrinkfatten", "ALT+S", typeof( EffigyViewport ) )]
	private void ShortcutMeshShrinkFatten() => MeshEditKey( MeshKeyShrinkFatten );

	[Shortcut( "effigy.mesh.grow", "+", typeof( EffigyViewport ) )]
	private void ShortcutMeshGrow() => MeshEditKey( '+' );

	[Shortcut( "effigy.mesh.shrinkselection", "-", typeof( EffigyViewport ) )]
	private void ShortcutMeshShrinkSelection() => MeshEditKey( '-' );

	[Shortcut( "effigy.mesh.linked", "L", typeof( EffigyViewport ) )]
	private void ShortcutMeshLinked() => MeshEditKey( 'L' );

	[Shortcut( "effigy.mesh.similar", "SHIFT+L", typeof( EffigyViewport ) )]
	private void ShortcutMeshSimilar() => MeshEditKey( MeshKeySimilar );

	[Shortcut( "effigy.mesh.circle", "C", typeof( EffigyViewport ) )]
	private void ShortcutMeshCircle() => MeshEditKey( 'C' );

	[Shortcut( "effigy.mesh.search", "SPACE", typeof( EffigyViewport ) )]
	private void ShortcutMeshSearch() => MeshEditKey( MeshKeySearch );

	[Shortcut( "effigy.mesh.extrude_normals", "ALT+E", typeof( EffigyViewport ) )]
	private void ShortcutMeshExtrudeNormals() => MeshEditKey( MeshKeyExtrudeNormals );

	[Shortcut( "effigy.mesh.triangulate", "CTRL+T", typeof( EffigyViewport ) )]
	private void ShortcutMeshTriangulate() => MeshEditKey( MeshKeyTriangulate );

	[Shortcut( "effigy.mesh.poke_faces", "ALT+P", typeof( EffigyViewport ) )]
	private void ShortcutMeshPokeFaces() => MeshEditKey( MeshKeyPokeFaces );

	[Shortcut( "effigy.mesh.tris_to_quads", "ALT+J", typeof( EffigyViewport ) )]
	private void ShortcutMeshTrisToQuads() => MeshEditKey( MeshKeyTrisToQuads );

	[Shortcut( "effigy.mesh.bevel_vertices", "CTRL+SHIFT+B", typeof( EffigyViewport ) )]
	private void ShortcutMeshBevelVertices() => MeshEditKey( MeshKeyBevelVertices );

	[Shortcut( "effigy.mesh.split", "Y", typeof( EffigyViewport ) )]
	private void ShortcutMeshSplit() => MeshEditKey( MeshKeySplit );

	[Shortcut( "effigy.mesh.rip", "V", typeof( EffigyViewport ) )]
	private void ShortcutMeshRip() => MeshEditKey( 'V' );

	[Shortcut( "effigy.mesh.fill", "F", typeof( EffigyViewport ) )]
	private void ShortcutMeshFill() => MeshEditKey( 'F' );

	[Shortcut( "effigy.mesh.connect", "J", typeof( EffigyViewport ) )]
	private void ShortcutMeshConnect() => MeshEditKey( 'J' );

	[Shortcut( "effigy.mesh.invert", "CTRL+I", typeof( EffigyViewport ) )]
	private void ShortcutMeshInvert() => MeshEditKey( MeshKeyInvert );

	[Shortcut( "effigy.mesh.recalculatenormals", "SHIFT+N", typeof( EffigyViewport ) )]
	private void ShortcutMeshRecalculate() => MeshEditKey( MeshKeyRecalculate );

	[Shortcut( "effigy.mesh.duplicate", "SHIFT+D", typeof( EffigyViewport ) )]
	private void ShortcutMeshDuplicate() => MeshEditKey( 'D' );

	[Shortcut( "effigy.mesh.separate", "P", typeof( EffigyViewport ) )]
	private void ShortcutMeshSeparate() => MeshEditKey( 'P' );

	[Shortcut( "effigy.mesh.knife", "K", typeof( EffigyViewport ) )]
	private void ShortcutMeshKnife() => MeshEditKey( 'K' );

	[Shortcut( "effigy.mesh.crease", "SHIFT+E", typeof( EffigyViewport ) )]
	private void ShortcutMeshCrease() => MeshEditKey( MeshKeyCrease );

	[Shortcut( "effigy.mesh.hide", "H", typeof( EffigyViewport ) )]
	private void ShortcutMeshHide() => MeshEditKey( MeshKeyHide );

	[Shortcut( "effigy.mesh.hide_others", "SHIFT+H", typeof( EffigyViewport ) )]
	private void ShortcutMeshHideOthers() => MeshEditKey( MeshKeyHideOthers );

	[Shortcut( "effigy.mesh.unhide", "ALT+H", typeof( EffigyViewport ) )]
	private void ShortcutMeshUnhide() => MeshEditKey( MeshKeyUnhide );

	[Shortcut( "effigy.mesh.soft_connected", "ALT+O", typeof( EffigyViewport ) )]
	private void ShortcutMeshSoftConnected() => MeshEditKey( MeshKeySoftConnected );

	[Shortcut( "effigy.mesh.soft", "O", typeof( EffigyViewport ) )]
	private void ShortcutMeshSoft() => MeshEditKey( 'O' );

	[Shortcut( "effigy.mesh.move", "G", typeof( EffigyViewport ) )]
	private void ShortcutMeshMove() => MeshEditKey( 'G' );

	[Shortcut( "effigy.mesh.scale", "S", typeof( EffigyViewport ) )]
	private void ShortcutMeshScale() => MeshEditKey( 'S' );

	[Shortcut( "effigy.mesh.selectall", "A", typeof( EffigyViewport ) )]
	private void ShortcutMeshSelectAll() => MeshEditKey( 'A' );

	[Shortcut( "effigy.mesh.delete", "DEL", typeof( EffigyViewport ) )]
	private void ShortcutMeshDelete() => MeshEditKey( 'X' );

	/// <summary>Undo inside the session while one is open, for the same reason sculpt owns undo: the
	/// studio's stack restores a feature list the live session may not survive.</summary>
	private bool StepMeshEditHistory( bool redo )
	{
		if ( _viewport?.MeshEditSession is not { } session )
			return false;

		if ( session.IsPreviewing )
		{
			CancelMeshOp();
			return true;
		}

		if ( !(redo ? session.Redo() : session.Undo()) )
			SetPrompt( redo ? "Nothing to redo in this edit." : "Nothing to undo in this edit. Finish, then Ctrl+Z undoes the whole edit." );

		_viewport.RefreshMeshEditPreview();
		OnMeshEditChanged();
		return true;
	}
}
