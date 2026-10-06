using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Model's chrome, from the window's side.
//
//  In the Model workspace the stage bar is HIDDEN, and the header, palette
//  and catalog in EffigyModelChrome.cs take its place on the viewport. This
//  file decides what they hold for each mode — Object, Edit, Sculpt, Retopo
//  — out of the tool objects the window already builds, and puts the bar
//  back the moment the workspace changes. The take-two concept is the spec:
//  modes as pills, the elements and toggles in the header, tiles down the
//  left, one catalog behind Space, the mesh check as a badge.
// ============================================================================

public sealed partial class EffigyWindow
{
	private EffigyViewportHeader _modelHeader;
	private EffigyToolPalette _modelPalette;
	private EffigyToolCatalog _modelCatalog;

	/// <summary>The mesh check as the badge shows it, recomputed with the panel.</summary>
	private (int Level, string Text) _modelCheck = (0, "Mesh is clean");
	private readonly List<(string Label, Action Run)> _modelCheckFixes = new();

	private void BuildModelChrome()
	{
		if ( _viewport is null )
			return;

		_modelHeader = new EffigyViewportHeader( _viewport.Canvas );
		_modelPalette = new EffigyToolPalette( _viewport.Canvas );
		_modelCatalog = new EffigyToolCatalog( _viewport.Canvas );

		BindModelChrome();
		_viewport.AddModelChrome( _modelHeader, _modelPalette, _modelCatalog );
	}

	/// <summary>Delegates, assigned at construction and again after a hotload.</summary>
	private void BindModelChrome()
	{
		if ( _modelHeader is null )
			return;

		_modelHeader.Count = tool =>
		{
			if ( _viewport?.MeshEditSession is not { } s )
				return "";

			if ( ReferenceEquals( tool, _meshVertexTool ) ) return s.SelectedVertices.Count.ToString();
			if ( ReferenceEquals( tool, _meshEdgeTool ) ) return s.SelectedEdges.Count.ToString();
			if ( ReferenceEquals( tool, _meshFaceTool ) ) return s.SelectedFaces.Count.ToString();
			return "";
		};

		_modelHeader.Check = () => _modelCheck;
		_modelHeader.CheckClicked = OpenModelCheckMenu;
		_modelHeader.SearchClicked = OpenModelCatalog;

		_modelCatalog.Opening = () =>
		{
			if ( _viewport?.IsMeshEditing == true ) UpdateMeshEditChecks();
			else if ( _viewport?.IsSculpting == true ) UpdateSculptChecks();
			else UpdateObjectChecks();
		};
	}

	/// <summary>
	/// Put Model's chrome up for <paramref name="mode"/> — "Object", "Edit", "Sculpt" or
	/// "Retopo" — and take the stage bar down. Called by each mode's own entry, after it has set
	/// the bar up, so the bar is still right if the chrome is ever taken away again.
	/// </summary>
	private void ShowModelChrome( string mode )
	{
		if ( _modelHeader is null || _viewport is null )
			return;

		AssignToolKeys();

		_stageBar.Visible = false;

		_modelHeader.Elements.Clear();
		_modelHeader.Toggles.Clear();

		List<EffigyStage> palette;
		IEnumerable<EffigyStage> catalog;

		switch ( mode )
		{
			case "Edit":
			case "Retopo":
				_modelHeader.Elements.AddRange( new[] { _meshVertexTool, _meshEdgeTool, _meshFaceTool }.Where( t => t is not null ) );
				_modelHeader.Toggles.AddRange( new[] { _meshXrayTool, _meshMirrorTool, _meshSnapTool, _meshSoftTool }.Where( t => t is not null ) );

				palette = mode == "Retopo"
					? new List<EffigyStage>
					{
						Group( "Retopo", _meshRetopoTool, _meshStripTool, _meshRelaxTool, _meshEvenQuadsTool ),
						Group( "Select", _meshCircleTool, _meshLassoTool ),
						Group( "Move", _meshMoveTool ),
					}
					: new List<EffigyStage>
					{
						Group( "Select", _meshCircleTool, _meshLassoTool ),
						Group( "Transform", _meshMoveTool, _meshRotateTool, _meshScaleTool ),
						Group( "Add", _meshExtrudeTool, _meshInsetTool, _meshBevelTool, _meshLoopCutTool, _meshKnifeTool ),
					};

				catalog = MeshMenus();
				break;

			case "Sculpt":
				_modelHeader.Toggles.AddRange( new[] { _symmetryTool, _maskTool }.Where( t => t is not null ) );
				palette = new List<EffigyStage> { Group( "Brush", _brushTools.Select( b => b.Tool ).ToArray() ) };
				catalog = _sculptStages;
				break;

			default:
				BuildObjectModeTools();
				palette = new List<EffigyStage>
				{
					Group( "Transform", _objMoveTool, _objRotateTool, _objScaleTool ),
					Group( "Object", _objDuplicateTool, _objSplineTool ),
					Group( "Mesh", _objEditTool, _objSculptTool ),
				};
				catalog = _objectMenus;
				break;
		}

		_modelPalette.SetGroups( palette );
		_modelCatalog.SetGroups( catalog );
		_modelCatalog.Close();

		_modelHeader.Visible = true;
		_modelPalette.Visible = true;
		_modelHeader.Raise();
		_modelPalette.Raise();

		RefreshModelCheck();
		RefreshModelChrome();
	}

	private void HideModelChrome()
	{
		if ( _modelHeader is null )
			return;

		_modelHeader.Visible = false;
		_modelPalette.Visible = false;
		_modelCatalog.Close();

		if ( _stageBar is not null )
			_stageBar.Visible = true;
	}

	/// <summary>Repaint the chrome. Cheap; called wherever the bar used to be refreshed.</summary>
	private void RefreshModelChrome()
	{
		if ( _modelHeader is not { Visible: true } )
			return;

		_modelHeader.Update();
		_modelPalette?.Update();
	}

	/// <summary>The chrome follows the workspace: up in Model, down elsewhere. Called from the
	/// BarMode setter beside the dock sync, for the same reason.</summary>
	private void SyncModelChrome()
	{
		if ( CurrentWorkspace != EffigyWorkspace.Model )
			HideModelChrome();
	}

	private static EffigyStage Group( string name, params EffigyStageTool[] tools )
	{
		var stage = new EffigyStage { Name = name };
		foreach ( var tool in tools )
			if ( tool is not null )
				stage.Add( tool );
		return stage;
	}

	/// <summary>The keys the tiles print. Set once; harmless to repeat after a hotload.</summary>
	private void AssignToolKeys()
	{
		void Key( EffigyStageTool tool, string key ) { if ( tool is not null ) tool.Key = key; }

		Key( _meshVertexTool, "1" ); Key( _meshEdgeTool, "2" ); Key( _meshFaceTool, "3" );
		Key( _meshCircleTool, "C" ); Key( _meshLassoTool, null );
		Key( _meshMoveTool, "G" ); Key( _meshRotateTool, "R" ); Key( _meshScaleTool, "S" );
		Key( _meshExtrudeTool, "E" ); Key( _meshInsetTool, "I" ); Key( _meshBevelTool, "^B" ); Key( _meshLoopCutTool, "^R" ); Key( _meshKnifeTool, "K" );
		Key( _meshXrayTool, "⌥Z" ); Key( _meshSoftTool, "O" );
		Key( _objMoveTool, "W" ); Key( _objRotateTool, "E" ); Key( _objScaleTool, "R" ); Key( _objEditTool, "Tab" );

		for ( var i = 0; i < _brushTools.Count && i < 9; i++ )
			_brushTools[i].Tool.Key = (i + 1).ToString();

		Key( _maskTool, "M" ); Key( _symmetryTool, "X" );
	}

	private void OpenModelCatalog()
	{
		if ( _modelCatalog is null || _modelHeader is not { Visible: true } )
			return;

		_modelCatalog.Open();
	}

	// --- the mesh check badge -------------------------------------------------------------------

	/// <summary>
	/// The check the badge shows, from the edit session while one is open and from the selected
	/// (or only) body otherwise. Recomputed with the panel — every mesh change and rebuild — not
	/// per paint: validating a dense mesh is not free.
	/// </summary>
	private void RefreshModelCheck()
	{
		_modelCheckFixes.Clear();

		PolyMesh mesh = null;
		var editing = _viewport?.MeshEditSession is { } session;

		if ( editing )
		{
			mesh = _viewport.MeshEditSession.Mesh;
		}
		else if ( _studio is not null )
		{
			var ids = _viewport?.IdleBodyIds ?? (IReadOnlyList<string>)Array.Empty<string>();
			var body = _studio.Bodies.Count == 1 ? _studio.Bodies[0] : _studio.Bodies.FirstOrDefault( b => ids.Contains( b.Id ) );
			mesh = body?.Mesh;

			if ( mesh is null )
			{
				_modelCheck = (0, _studio.Bodies.Count == 0 ? "No body yet" : "Click a body to check it");
				RefreshModelChrome();
				return;
			}
		}

		if ( mesh is null )
		{
			_modelCheck = (0, "Mesh is clean");
			RefreshModelChrome();
			return;
		}

		var check = MeshValidator.Validate( mesh );
		var used = new HashSet<int>();
		foreach ( var face in mesh.Faces )
			used.UnionWith( face.Indices );
		var loose = mesh.VertexCount - used.Count;

		var parts = new List<string>();
		if ( check.NonManifoldEdges > 0 ) parts.Add( $"{check.NonManifoldEdges:N0} non-manifold" );
		if ( check.BoundaryEdges > 0 ) parts.Add( $"{check.BoundaryEdges:N0} open edges" );
		if ( loose > 0 ) parts.Add( $"{loose:N0} loose" );

		var level = check.NonManifoldEdges > 0 ? 2 : parts.Count > 0 ? 1 : 0;
		_modelCheck = (level, parts.Count == 0 ? "Mesh is clean" : string.Join( " · ", parts ));

		if ( check.BoundaryEdges > 0 || check.NonManifoldEdges > 0 )
			_modelCheckFixes.Add( ("Show the open and non-manifold edges", () => RunObjectFix( "Non-manifold" )) );

		if ( loose > 0 )
			_modelCheckFixes.Add( ($"Remove {loose:N0} loose vertices", () => RunObjectFix( "Delete loose" )) );

		RefreshModelChrome();
	}

	private void OpenModelCheckMenu()
	{
		var menu = new Menu( this );
		menu.AddHeading( _modelCheck.Text );

		if ( _modelCheckFixes.Count == 0 )
		{
			var option = menu.AddOption( "Nothing to fix", null, null );
			option.Enabled = false;
		}

		foreach ( var (label, run) in _modelCheckFixes )
			menu.AddOption( label, null, run );

		menu.OpenAtCursor();
	}
}
