using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Model — Object mode.
//
//  THE HALF OF THE MODEL REDESIGN THAT WAS MISSING. On 2026-09-18 Edit mode
//  became a modeller's layout — a toolbar of held tools, menus for the rest,
//  right-click on the model, Space to search, Object | Edit | Retopo at the
//  right, and a Scene / Mesh check / History dock in place of the CAD tree.
//  But it only switched in while a mesh was open. The moment you pressed
//  Object, the two stage tabs ("Edit", "Sculpt") and the CAD feature tree came
//  back, with Origin and three datum planes at the top — so Model still read
//  as CAD every time you were not mid-edit, which is most of the time.
//
//  Object mode now uses the same bar and the same dock. The toolbar holds
//  Move / Rotate / Scale (the body handle), Duplicate, Edit and Sculpt; the
//  menus are Object, Add and Mesh; the mode switch is Object | Edit | Sculpt |
//  Retopo; a right-click on a body opens the Object menu. The dock shows the
//  bodies, the selected one's check and the history, and stays for the whole
//  workspace — Edit mode fills it from the session, Object mode from the
//  studio (EffigyMeshEditPanel.RefreshObject).
//
//  NOTHING HERE IS A NEW OPERATION. Every entry is a feature the CAD strip
//  already adds (Transform, Mirror, Linear pattern, Primitive, Subdivide,
//  Remesh, Sculpt, Mesh edit) or a part-list command (Join, Delete, Hide),
//  reached from where a modeller looks for it. The history still records
//  each one, and CAD still shows the tree.
// ============================================================================

public sealed partial class EffigyWindow
{
	private EffigyStage _objectCoreStage;
	private List<EffigyStage> _objectMenus;

	private EffigyStageTool _objMoveTool, _objRotateTool, _objScaleTool, _objDuplicateTool, _objMirrorTool,
		_objJoinTool, _objDeleteTool, _objHideTool, _objIsolateTool, _objShowAllTool,
		_objEditTool, _objSculptTool, _objRetopoTool, _objSubdivideTool, _objRemeshTool, _objCadTool,
		_objSplineTool, _objBendTool, _objPartTool, _objProfileTool;

	/// <summary>Whether the Model workspace is showing with nothing open — the state this file is for.</summary>
	private bool InObjectMode =>
		CurrentWorkspace == EffigyWorkspace.Model
		&& _viewport is { IsMeshEditing: false, IsSculpting: false };

	/// <summary>
	/// The toolbar and the menus, built once. The same tool objects go on both, so an enabled state
	/// set by <see cref="UpdateObjectChecks"/> is what both show — the reason Edit mode shares its
	/// tools between its toolbar and its menus too.
	/// </summary>
	private void BuildObjectModeTools()
	{
		if ( _objectCoreStage is not null )
			return;

		static EffigyStageTool Tool( EffigyIcon icon, string label, string tip, Action clicked, bool checkable = false ) =>
			new() { Icon = icon, Label = label, Tip = tip, Clicked = clicked, Checkable = checkable };

		_objMoveTool = Tool( EffigyIcon.Transform, "Move", "Move the selected body with a handle (W). Saved as a Transform", () => StartBodyTransform( EffigyViewport.BodyDragMode.Move ), checkable: true );
		_objRotateTool = Tool( EffigyIcon.CircularPattern, "Rotate", "Turn the selected body with a handle (E)", () => StartBodyTransform( EffigyViewport.BodyDragMode.Rotate ), checkable: true );
		_objScaleTool = Tool( EffigyIcon.Primitive, "Scale", "Scale the selected body with a handle (R)", () => StartBodyTransform( EffigyViewport.BodyDragMode.Scale ), checkable: true );
		_objDuplicateTool = Tool( EffigyIcon.LinearPattern, "Duplicate", "A copy of the selected body beside it. Saved as a Linear pattern, so the count and spacing can still be changed", DuplicateBody );
		_objMirrorTool = Tool( EffigyIcon.Mirror, "Mirror", "A mirrored copy of the selected body across a plane", () => AddFeature( NewFeature( ToolKind.Mirror, -1 ) ) );
		_objJoinTool = Tool( EffigyIcon.Boolean, "Join", "Make one body of the selected ones, as a new Import. Ctrl+Z brings them back", () => JoinParts( _viewport?.IdleBodyIds.ToList() ) );
		_objDeleteTool = Tool( EffigyIcon.DeleteGeometry, "Delete", "Remove the selected bodies. Ctrl+Z brings them back", () => DeleteParts( _viewport?.IdleBodyIds.ToList() ) );
		_objHideTool = Tool( EffigyIcon.SelectFace, "Hide", "Hide the selected bodies. They still export", () => ForEachSelectedBody( id => OnPartCommand( id, EffigyPartCommand.ToggleVisibility ) ) );
		_objIsolateTool = Tool( EffigyIcon.SelectFace, "Show only this", "Hide everything except the selected body", () => ForEachSelectedBody( id => OnPartCommand( id, EffigyPartCommand.Isolate ), first: true ) );
		_objShowAllTool = Tool( EffigyIcon.SelectFace, "Show all", "Show every hidden body again", () => { if ( _studio.Bodies.Count > 0 ) OnPartCommand( _studio.Bodies[0].Id, EffigyPartCommand.ShowAll ); } );

		_objEditTool = Tool( EffigyIcon.MeshEdit, "Edit", "Edit the selected body's vertices, edges and faces (Tab). Saved as a Mesh edit", AddMeshEdit );
		_objSculptTool = Tool( EffigyIcon.Sculpt, "Sculpt", "Brush detail onto the selected body in levels. Saved as a Sculpt", () => AddFeature( NewFeature( ToolKind.Sculpt, -1 ) ) );
		_objRetopoTool = Tool( EffigyIcon.SelectFace, "Retopo", "Draw a clean, light mesh over the selected body", StartRetopoFromObject );
		_objSubdivideTool = Tool( EffigyIcon.Subdivide, "Subdivide", "Smooth the selected body with Catmull-Clark subdivision", () => AddFeature( NewFeature( ToolKind.Subdivide, -1 ) ) );
		_objRemeshTool = Tool( EffigyIcon.Remesh, "Remesh", "Reduce a dense body to a triangle budget, keeping its shape", () => AddFeature( NewFeature( ToolKind.Remesh, -1 ) ) );
		_objSplineTool = Tool( EffigyIcon.SplineTool, "Spline", "Draw a curve: click points in the viewport, and it becomes a tube — a cable, a horn, a tail. A Bend or a Sweep can follow it too", AddSpline );
		_objBendTool = Tool( EffigyIcon.Drape, "Bend along curve", "Bend the selected body along the last spline drawn: its length follows the curve", () => AddFeature( NewFeature( ToolKind.CurveDeform, -1 ) ) );
		_objPartTool = Tool( EffigyIcon.Primitive, "Part from the library", "A cog, bolt, rivet, knob, hinge, panel, pipe elbow, strap or buckle, from a few dials", () => AddFeature( NewFeature( ToolKind.Part, -1 ) ) );
		_objProfileTool = Tool( EffigyIcon.Loft, "Profile body", "A body from a front outline and a side outline — how wide and how deep at each height", () => AddFeature( NewFeature( ToolKind.Profile, -1 ) ) );
		_objCadTool = Tool( EffigyIcon.Sketch, "Open in CAD", "The same history as a feature tree, with sketches, extrudes and fillets", () => SetWorkspace( EffigyWorkspace.Cad ) );

		// The toolbar: what you hold. Everything on it is on a menu too.
		_objectCoreStage = new EffigyStage { Name = "Object" };
		foreach ( var tool in new[] { _objMoveTool, _objRotateTool, _objScaleTool, _objDuplicateTool, _objEditTool, _objSculptTool } )
			_objectCoreStage.Add( tool );

		var objectMenu = new EffigyStage { Name = "Object" };
		foreach ( var tool in new[] { _objMoveTool, _objRotateTool, _objScaleTool, null, _objDuplicateTool, _objMirrorTool, _objJoinTool, null, _objHideTool, _objIsolateTool, _objShowAllTool, null, _objDeleteTool } )
			objectMenu.Add( tool );

		// Add: a body from nothing. The primitive shapes come off the feature, as the CAD strip's
		// Primitive dropdown does, so a shape the kernel grows appears here without a second list.
		var add = new EffigyStage { Name = "Add" };
		var shapes = PrimitiveShapes;
		for ( var i = 0; i < shapes.Length; i++ )
		{
			var shape = i;
			add.Add( Tool( EffigyIcon.Primitive, shapes[i], $"Add a {shapes[i].ToLowerInvariant()}", () => AddFeature( NewPrimitive( shape ) ) ) );
		}
		add.Add( null );
		add.Add( _objSplineTool );
		add.Add( _objPartTool );
		add.Add( _objProfileTool );
		add.Add( null );
		add.Add( Tool( EffigyIcon.Primitive, "Import mesh...", "Bring in an .obj, .fbx or .vmdl as a body", () => AddFeature( NewFeature( ToolKind.Import, -1 ) ) ) );
		add.Add( Tool( EffigyIcon.Bone, "Body from Bones", "A skin block-out built on the rig, ready to edit", BodyFromBones ) );
		add.Add( null );
		add.Add( Tool( EffigyIcon.Plane, "Front reference image...", "A picture behind the model to draw against, seen from the front", () => AddReferenceImage( "front" ) ) );
		add.Add( Tool( EffigyIcon.Plane, "Side reference image...", "A picture seen from the side", () => AddReferenceImage( "side" ) ) );
		add.Add( Tool( EffigyIcon.Plane, "Top reference image...", "A picture seen from above", () => AddReferenceImage( "top" ) ) );

		var mesh = new EffigyStage { Name = "Mesh" };
		foreach ( var tool in new[] { _objEditTool, _objSculptTool, _objRetopoTool, null, _objSubdivideTool, _objRemeshTool, _objBendTool, null, _objCadTool } )
			mesh.Add( tool );

		_objectMenus = new List<EffigyStage> { objectMenu, add, mesh };
	}

	/// <summary>
	/// Object mode's bar. Called by ShowSculptHome — the Model workspace's landing — in place of
	/// the two stage tabs it used to put up.
	/// </summary>
	private void ShowObjectModeBar()
	{
		if ( _stageBar is null )
			return;

		BuildObjectModeTools();

		_stageBar.Mode = null;
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( new[] { _objectCoreStage } );
		_stageBar.SetMenus( _objectMenus );
		_stageBar.MenuOpening = UpdateObjectChecks;
		UpdateModelModeSwitch( "Object" );
		ShowModelChrome( "Object" );
		UpdateObjectChecks();
		SyncModelDock();
	}

	/// <summary>
	/// Object | Edit | Sculpt | Retopo, with <paramref name="active"/> lit. One switch for the
	/// whole workspace: Edit mode and the sculpt brushes show the same four, so changing mode is
	/// always the same click in the same place, and leaving a mode is picking the next one.
	/// </summary>
	private void UpdateModelModeSwitch( string active )
	{
		if ( _stageBar is null )
			return;

		var segments = new[]
		{
			new EffigyModeSegment { Label = "Object", Tip = "Whole bodies: move, copy, join, delete. Leaving an edit or a sculpt keeps it", Active = active == "Object", Clicked = () => SwitchModelMode( "Object" ) },
			new EffigyModeSegment { Label = "Edit", Tip = "Edit vertices, edges and faces (Tab)", Active = active == "Edit", Clicked = () => SwitchModelMode( "Edit" ) },
			new EffigyModeSegment { Label = "Sculpt", Tip = "Brush detail onto the body in levels", Active = active == "Sculpt", Clicked = () => SwitchModelMode( "Sculpt" ) },
			new EffigyModeSegment { Label = "Retopo", Tip = "Draw a clean, light mesh over the body", Active = active == "Retopo", Clicked = () => SwitchModelMode( "Retopo" ) },
		};

		_stageBar.SetSegments( segments );

		if ( _modelHeader is not null )
		{
			_modelHeader.Modes.Clear();
			_modelHeader.Modes.AddRange( segments );
			_modelHeader.Update();
		}
	}

	/// <summary>
	/// The mode switch's one handler. Leaving is always a commit, never a discard — the old Finish
	/// buttons kept the work, and so does this — and entering is what the Object-mode tool of the
	/// same name does, so the switch and the toolbar cannot disagree about what "Edit" means.
	/// </summary>
	private void SwitchModelMode( string mode )
	{
		if ( _viewport is null )
			return;

		var editing = _viewport.IsMeshEditing;
		var retopo = editing && _viewport.MeshEditSession is { IsRetopologizing: true };
		var sculpting = _viewport.IsSculpting;

		switch ( mode )
		{
			case "Object":
				if ( editing ) FinishMeshEdit();
				else if ( sculpting ) FinishSculpt();
				else ShowSculptHome();
				return;

			case "Edit":
				if ( retopo ) { ToggleMeshRetopo(); UpdateModelModeSwitch( "Edit" ); return; }
				if ( editing ) return;
				if ( sculpting ) FinishSculpt();
				AddMeshEdit();
				return;

			case "Sculpt":
				if ( sculpting ) return;
				if ( editing ) FinishMeshEdit();
				AddFeature( NewFeature( ToolKind.Sculpt, -1 ) );
				return;

			case "Retopo":
				if ( retopo ) return;
				if ( sculpting ) FinishSculpt();
				StartRetopoFromObject();
				return;
		}
	}

	/// <summary>Retopo from outside an edit: open the body in Edit mode, then switch retopology on.</summary>
	private void StartRetopoFromObject()
	{
		if ( _viewport is null )
			return;

		if ( !_viewport.IsMeshEditing )
			AddMeshEdit();

		if ( _viewport.MeshEditSession is { IsRetopologizing: false } )
			ToggleMeshRetopo();

		UpdateModelModeSwitch( _viewport.MeshEditSession is { IsRetopologizing: true } ? "Retopo" : "Edit" );
	}

	/// <summary>
	/// Move / Rotate / Scale: the body handle, which only exists while a Transform's dialog is open
	/// (EffigyViewport.BodyDrag has the argument). With one open, this just switches the handle;
	/// otherwise it adds a Transform on the selection and opens on that handle.
	/// </summary>
	private void StartBodyTransform( EffigyViewport.BodyDragMode mode )
	{
		if ( _viewport is null || _studio is null )
			return;

		if ( _dialog?.Feature is not TransformFeature || !_viewport.BodyDragEnabled )
		{
			if ( _viewport.IdleBodyIds.Count == 0 )
			{
				SetPrompt( "Click a body first, then Move, Rotate or Scale it." );
				return;
			}

			AddFeature( NewFeature( ToolKind.Transform, -1 ) );
		}

		_viewport.SetBodyDragMode( mode );
		UpdateObjectChecks();
		_viewport.Update();
	}

	/// <summary>
	/// A copy beside the original: a Linear pattern of two, spaced by the body's own width, so the
	/// copy lands clear of it rather than on top. The pattern's count and spacing stay editable in
	/// the history, which is more than a plain copy would give.
	/// </summary>
	private void DuplicateBody()
	{
		if ( _viewport is null || _studio is null )
			return;

		var ids = _viewport.IdleBodyIds;

		if ( ids.Count == 0 )
		{
			SetPrompt( "Click a body first, then Duplicate it." );
			return;
		}

		var feature = new LinearPatternFeature { Name = "Duplicate" };
		feature.Count.Value = 2;

		var width = 0f;

		foreach ( var body in _studio.Bodies.Where( b => ids.Contains( b.Id ) ) )
		{
			if ( body.Mesh is not { } mesh || mesh.Positions.Count == 0 )
				continue;

			var min = mesh.Positions.Min( p => p.x );
			var max = mesh.Positions.Max( p => p.x );
			width = MathF.Max( width, max - min );
		}

		if ( width > 0f )
			feature.Spacing.Value = width * 1.25f;

		AddFeature( feature );
	}

	private void ForEachSelectedBody( Action<string> action, bool first = false )
	{
		if ( _viewport is null )
			return;

		var ids = _viewport.IdleBodyIds.ToList();

		if ( ids.Count == 0 )
		{
			SetPrompt( "Click a body first." );
			return;
		}

		if ( first )
		{
			action( ids[0] );
			return;
		}

		foreach ( var id in ids )
			action( id );
	}

	/// <summary>
	/// Enabled states and ticks for the Object tools, from the selection and the open dialog. Run
	/// before a menu opens and after every selection change, the way Edit mode's checks are.
	/// </summary>
	private void UpdateObjectChecks()
	{
		if ( _objectCoreStage is null || _viewport is null || _studio is null )
			return;

		var selected = _viewport.IdleBodyIds.Count;
		var bodies = _studio.Bodies.Count;
		var handle = _dialog?.Feature is TransformFeature && _viewport.BodyDragEnabled;
		var mode = _viewport.CurrentBodyDragMode;

		const string pickOne = "Click a body first";

		Need( _objMoveTool, selected > 0 || handle, pickOne );
		Need( _objRotateTool, selected > 0 || handle, pickOne );
		Need( _objScaleTool, selected > 0 || handle, pickOne );
		Need( _objDuplicateTool, selected > 0, pickOne );
		Need( _objMirrorTool, selected > 0, pickOne );
		Need( _objJoinTool, selected > 1, "Select two or more bodies (Ctrl+click) to join" );
		Need( _objDeleteTool, selected > 0, pickOne );
		Need( _objHideTool, selected > 0, pickOne );
		Need( _objIsolateTool, selected > 0 && bodies > 1, bodies > 1 ? pickOne : "There is only one body" );
		Need( _objShowAllTool, _studio.HiddenBodyIds.Count > 0, "Nothing is hidden" );
		Need( _objEditTool, true, null );
		Need( _objSculptTool, bodies > 0, "Add a body first" );
		Need( _objRetopoTool, bodies > 0, "Add a body first" );
		Need( _objSubdivideTool, selected > 0, pickOne );
		Need( _objRemeshTool, selected > 0, pickOne );
		Need( _objBendTool, selected > 0 && _studio.Features.OfType<SplineFeature>().Any(), selected == 0 ? pickOne : "Draw a Spline first (Add ▸ Spline)" );

		_objMoveTool.Checked = handle && mode == EffigyViewport.BodyDragMode.Move;
		_objRotateTool.Checked = handle && mode == EffigyViewport.BodyDragMode.Rotate;
		_objScaleTool.Checked = handle && mode == EffigyViewport.BodyDragMode.Scale;

		_stageBar?.Refresh();
		RefreshModelChrome();
	}

	/// <summary>
	/// Right-click on a body in Object mode: select it and open the Object menu on it, the way
	/// Edit mode's right-click opens the menu for the element under the mouse.
	/// </summary>
	private void OpenObjectMenu( Body body )
	{
		if ( body is null || _viewport is null )
			return;

		BuildObjectModeTools();

		// Ctrl keeps the selection, so a right-click on a second body can Join the two.
		if ( !Editor.Application.IsKeyDown( KeyCode.Control ) || !_viewport.IdleBodyIds.Contains( body.Id ) )
		{
			var ids = Editor.Application.IsKeyDown( KeyCode.Control )
				? _viewport.IdleBodyIds.Append( body.Id ).ToList()
				: new List<string> { body.Id };

			_viewport.SelectBodies( ids );
		}

		UpdateObjectChecks();

		var menu = new Menu( this );
		menu.AddHeading( body.Name ?? "Body" );
		EffigyStageBar.AddTools( menu, _objectMenus[0].Tools );
		menu.AddSeparator();
		EffigyStageBar.AddTools( menu, _objectMenus[2].Tools.Where( t => t is not null && t != _objCadTool ) );
		menu.OpenAtCursor();
	}

	/// <summary>
	/// The left dock follows the workspace: the Model panel in Model, the CAD tree everywhere
	/// else. Edit mode fills the panel from its session and Object mode from the studio, so the
	/// same three headings are there whichever mode you are in.
	/// </summary>
	private void SyncModelDock()
	{
		if ( _meshPanel is null )
			return;

		var model = CurrentWorkspace == EffigyWorkspace.Model;

		_meshPanel.Visible = model;

		if ( _featureTree is not null )
			_featureTree.Visible = !model;

		if ( _partsPanel is not null )
			_partsPanel.Visible = !model;

		if ( model )
			RefreshMeshEditPanel();
	}

	/// <summary>
	/// The Model panel's delegates. Assigned at construction and again after a hotload, since a
	/// lambda compiled into the dead assembly is a Scene row that selects nothing.
	/// </summary>
	private void BindModelDock()
	{
		if ( _meshPanel is null )
			return;

		_meshPanel.RunOp = RunMeshOp;
		_meshPanel.UndoTo = UndoMeshTo;

		// Object mode's half of the panel: the Scene list selects like the Parts list (Ctrl adds
		// or removes), a right-click on a row is the Object menu, History rows open their feature,
		// and a fix under the check opens the body in Edit mode with that tool run.
		_meshPanel.SelectBody = id =>
		{
			if ( _viewport is null )
				return;

			var ids = Editor.Application.IsKeyDown( KeyCode.Control )
				? _viewport.IdleBodyIds.Contains( id )
					? _viewport.IdleBodyIds.Where( i => i != id ).ToList()
					: _viewport.IdleBodyIds.Append( id ).ToList()
				: new List<string> { id };

			_viewport.SelectBodies( ids );
		};

		_meshPanel.BodyMenu = id => OpenObjectMenu( _studio?.Bodies.FirstOrDefault( b => b.Id == id ) );
		_meshPanel.OpenFeature = EditFeature;
		_meshPanel.RunTool = RunObjectFix;
	}

	/// <summary>A fix button under the Object-mode check: open the body in Edit mode with that
	/// tool run, since every fix is an edit.</summary>
	private void RunObjectFix( string tool )
	{
		if ( _viewport is null )
			return;

		if ( !_viewport.IsMeshEditing )
			AddMeshEdit();

		if ( !_viewport.IsMeshEditing )
			return;

		MeshToolNamed( tool )?.Clicked?.Invoke();
	}
}
