using Editor;
using Effigy;
using Effigy.Render;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Set Origin, Render Turnaround and the symmetry badge (usability plan #7).
//
//  All three were agent-only: `pivot`, `render shade=` and `symmetry` had no
//  button. The kernel does the work (StudioOrigin, Turnaround, SymmetryCheck);
//  this file is where a modeller finds it — Object ▸ Origin to…, File ▸ Render
//  Turnaround, and a line on the Model check badge.
//
//  THE ORIGIN HERE IS THE MODEL'S, PartStudio.Origin: the yellow handle, and
//  what every export from this window subtracts. View ▸ Reset Origin used to
//  be the only menu entry for it, filed under View as if it were a camera
//  setting; it is now Origin to World Centre beside the other three, on
//  Model's Object menu and on Edit ▸ Set Origin for every workspace.
// ============================================================================

public sealed partial class EffigyWindow
{
	private EffigyStageTool _objOriginGeometryTool, _objOriginBottomTool, _objOriginPointTool, _objOriginWorldTool;
	private EffigyStageTool _renderTurnaroundTool, _renderTurnaroundMaterialTool;
	private EffigyStage _renderCatalog;
	private EffigyViewport.PointPickRequest _originPick;
	private string _turnaroundFolder;

	/// <summary>The badge's symmetry reading for the body it checks, or null when there is none.</summary>
	private SymmetryCheck.Reading? _modelSymmetry;

	private const string OriginGeometryTip = "Move the model's origin to the middle of the selected body, or of the whole model. Exports put that point at (0,0,0)";
	private const string OriginBottomTip = "Move the model's origin to the middle of the underside, so it exports standing on (0,0,0). Measures the selected body, or the whole model";
	private const string OriginPointTip = "Click on the model (or the ground past it) to put the origin there — a hinge, an axle, a handle. Escape cancels";
	private const string OriginWorldTip = "Put the model's origin back at (0,0,0), so it exports exactly where it was built";

	/// <summary>
	/// Edit ▸ Set Origin: the same four commands on the menu bar, so every workspace has them — CAD
	/// shows the origin handle too, and View ▸ Reset Origin (which this replaces) was its only way
	/// back to zero from the menus.
	/// </summary>
	private void AddOriginMenu( Menu edit )
	{
		var origin = edit.AddMenu( "Set Origin", "adjust" );
		origin.AddOption( "Origin to Geometry", "center_focus_strong", () => SetModelOrigin( OriginTarget.Geometry ) ).ToolTip = OriginGeometryTip;
		origin.AddOption( "Origin to Bottom", "vertical_align_bottom", () => SetModelOrigin( OriginTarget.Bottom ) ).ToolTip = OriginBottomTip;
		origin.AddOption( "Origin to Clicked Point", "ads_click", PickOrigin ).ToolTip = OriginPointTip;
		origin.AddOption( "Origin to World Centre", "settings_backup_restore", () => SetModelOrigin( OriginTarget.World ) ).ToolTip = OriginWorldTip;
	}

	/// <summary>Object ▸ Origin to …: four entries after a separator, on the menu passed in.</summary>
	private void AddOriginTools( EffigyStage objectMenu )
	{
		static EffigyStageTool Tool( string label, string tip, Action clicked ) =>
			new() { Icon = EffigyIcon.Transform, Label = label, Tip = tip, Clicked = clicked };

		_objOriginGeometryTool = Tool( "Origin to Geometry", OriginGeometryTip, () => SetModelOrigin( OriginTarget.Geometry ) );
		_objOriginBottomTool = Tool( "Origin to Bottom", OriginBottomTip, () => SetModelOrigin( OriginTarget.Bottom ) );
		_objOriginPointTool = Tool( "Origin to Clicked Point", OriginPointTip, PickOrigin );
		_objOriginWorldTool = Tool( "Origin to World Centre", OriginWorldTip, () => SetModelOrigin( OriginTarget.World ) );

		objectMenu.Add( null );
		objectMenu.Add( _objOriginGeometryTool );
		objectMenu.Add( _objOriginBottomTool );
		objectMenu.Add( _objOriginPointTool );
		objectMenu.Add( _objOriginWorldTool );
	}

	/// <summary>Enabled states for the origin tools, with UpdateObjectChecks.</summary>
	private void UpdateOriginChecks()
	{
		if ( _studio is null )
			return;

		var geometry = StudioOrigin.Exported( _studio ).Count > 0;
		const string none = "Add a body first: there is nothing to measure the origin from";

		Need( _objOriginGeometryTool, geometry, none );
		Need( _objOriginBottomTool, geometry, none );
		Need( _objOriginPointTool, geometry, "Add a body first: there is nothing to click on" );
		Need( _objOriginWorldTool, StudioOrigin.HasModelOrigin( _studio ), "The origin is already at the world centre" );
		Need( _renderTurnaroundTool, geometry, "Add a body first: there is nothing to render" );
		Need( _renderTurnaroundMaterialTool, geometry, "Add a body first: there is nothing to render" );

		if ( _objOriginPointTool is not null )
			_objOriginPointTool.Checked = _originPick is not null && ReferenceEquals( _viewport?.PointPick, _originPick );
	}

	/// <summary>
	/// Move the model's origin, measured on the selected bodies or the whole model, and show it on
	/// the handle. Dirties the document through OnOriginMoved, as dragging the handle does.
	/// </summary>
	private void SetModelOrigin( OriginTarget target, Vec3 point = default )
	{
		if ( _studio is null || _viewport is null )
			return;

		var was = _studio.Origin;
		var ids = target is OriginTarget.Geometry or OriginTarget.Bottom ? _viewport.IdleBodyIds.ToList() : null;

		if ( !StudioOrigin.SetModel( _studio, target, ids, point, out var said ) )
		{
			Refuse( said );
			return;
		}

		if ( (_studio.Origin - was).Length < 1e-5f )
		{
			SetPrompt( $"The origin is already there, at ({_studio.Origin.x:0.###}, {_studio.Origin.y:0.###}, {_studio.Origin.z:0.###})." );
			return;
		}

		SyncOriginFromStudio();
		SetPrompt( said );
		Log.Info( $"[Effigy] {said}" );
		UpdateObjectChecks();
	}

	/// <summary>Origin to Clicked Point: arm the viewport's click-to-place; the click sets the origin.</summary>
	private void PickOrigin()
	{
		if ( _viewport is null || _studio is null )
			return;

		if ( _originPick is not null && ReferenceEquals( _viewport.PointPick, _originPick ) )
		{
			_viewport.EndPointPick();
			return;
		}

		var bodies = StudioOrigin.Exported( _studio );

		if ( bodies.Count == 0 )
		{
			Refuse( "Origin not moved: there is no geometry to click on. Add a body first." );
			return;
		}

		var (min, max) = ModelMeasure.Bounds( bodies );

		_originPick = new EffigyViewport.PointPickRequest
		{
			Anchor = (min + max) * 0.5f,
			Picked = ( p, _, _ ) =>
			{
				_viewport.EndPointPick();
				SetModelOrigin( OriginTarget.Point, p );
			},
			Ended = () =>
			{
				_originPick = null;
				_viewport?.SetPickPrompt( "" );
				UpdateObjectChecks();
			},
		};

		_viewport.BeginPointPick( _originPick );
		_viewport.SetPickPrompt( "Origin: click on the model, or on the ground past it, where the origin goes. Escape cancels." );
		UpdateObjectChecks();
	}

	// --- render turnaround ----------------------------------------------------------------------

	/// <summary>The two turnaround commands, for File and for the Space catalog.</summary>
	private void BuildRenderTools()
	{
		if ( _renderTurnaroundTool is not null )
			return;

		_renderTurnaroundTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Primitive,
			Label = "Render Turnaround",
			Tip = "Save pictures of the model in plain clay from the front, a quarter, the side and the back, plus one sheet of all four, in a folder you choose",
			Clicked = () => RenderTurnaround( clay: true ),
		};

		_renderTurnaroundMaterialTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Paint,
			Label = "Render Turnaround with Materials",
			Tip = "The same four pictures and sheet, shaded with the model's skin materials and painted colours where it has them",
			Clicked = () => RenderTurnaround( clay: false ),
		};

		_renderCatalog = new EffigyStage { Name = "Render" };
		_renderCatalog.Add( _renderTurnaroundTool );
		_renderCatalog.Add( _renderTurnaroundMaterialTool );
	}

	/// <summary>Object mode's Space catalog: its menus plus the Render group.</summary>
	private IEnumerable<EffigyStage> ObjectCatalog()
	{
		BuildRenderTools();
		return _objectMenus.Append( _renderCatalog );
	}

	/// <summary>
	/// File ▸ Render Turnaround: choose a folder, rebuild, render, save the sheet and the views, open
	/// the sheet. Blocking, like Compile — the prompt says so first, the log says when it is done.
	/// </summary>
	private void RenderTurnaround( bool clay )
	{
		if ( _studio is null )
			return;

		var fd = new FileDialog( null )
		{
			Title = clay ? "Render Turnaround: choose a folder for the pictures" : "Render Turnaround with Materials: choose a folder for the pictures",
			Directory = _turnaroundFolder
				?? (_documentPath is not null ? Path.GetDirectoryName( Path.GetFullPath( _documentPath ) ) : null)
				?? Project.Current?.GetAssetsPath() ?? "",
		};

		fd.SetFindDirectory();

		if ( !fd.Execute() || string.IsNullOrWhiteSpace( fd.SelectedFile ) )
			return;

		var folder = fd.SelectedFile;
		_turnaroundFolder = folder;

		var report = RebuildForExport( "the turnaround" );
		if ( report.HasErrors || _studio.Bodies.Count == 0 )
		{
			CannotExport( "render a turnaround", report );
			return;
		}

		var name = _documentPath is not null ? Path.GetFileNameWithoutExtension( _documentPath ) : "untitled";
		SetPrompt( $"Rendering the turnaround of {name}..." );
		Log.Info( $"[Effigy] rendering a {(clay ? "clay" : "material")} turnaround of {name} into {folder}" );

		var result = Turnaround.Write( _studio, folder, name, new TurnaroundOptions { Clay = clay, Label = name } );

		foreach ( var line in result.Log )
			Log.Info( $"[Effigy] turnaround: {line}" );

		if ( !result.Ok )
		{
			ReportFailure( $"Render Turnaround: {result.Error}" );
			return;
		}

		var sheet = result.Written[0];
		SetPrompt( $"Turnaround saved: {Path.GetFileName( sheet )} and {result.Written.Count - 1} views in {folder}." );

		try
		{
			EditorUtility.OpenFile( sheet );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Effigy] the turnaround is saved at {sheet}, but it could not be opened ({e.Message}). Open it from that folder." );
		}
	}

	// --- symmetry on the Model check badge -------------------------------------------------------

	/// <summary>Measure the badge's mesh and give the badge's short line for it ("Symmetric X").</summary>
	private string MeasureBadgeSymmetry( PolyMesh mesh )
	{
		_modelSymmetry = mesh is { VertexCount: > 0 } ? SymmetryCheck.Measure( mesh ) : null;
		return _modelSymmetry?.Short;
	}

	/// <summary>The badge's tooltip: what it checks, and the symmetry in full.</summary>
	private string ModelCheckTip() =>
		"The mesh check and the left-right symmetry. Click for the list and the fixes"
		+ (_modelSymmetry is { } s ? $"\n{s.Describe()}" : "");

	/// <summary>The symmetry lines on the badge's menu: the reading, and Symmetrize when it applies.</summary>
	private void AddSymmetryToCheckMenu( Menu menu )
	{
		if ( _modelSymmetry is not { } s )
			return;

		menu.AddSeparator();
		var reading = menu.AddOption( s.Level == 0 ? $"Symmetric across {s.Axis} = 0" : $"{s.Error * 100f:0.#}% off symmetric across {s.Axis} = 0", null, null );
		reading.Enabled = false;
		reading.ToolTip = s.Describe();

		if ( s.Level > 0 && s.Axis == 'X' )
		{
			var fix = menu.AddOption( "Symmetrize (keep the +X half)", null, () => RunObjectFix( "Symmetrize" ) );
			fix.ToolTip = "Open the body in Edit mode and make its -X half a mirror of its +X half";
		}
	}
}
