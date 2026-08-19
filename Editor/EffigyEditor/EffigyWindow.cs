using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Color palettes for theming — swap at runtime via the toolbar dropdown.
//  Each palette defines every color the UI touches. The Onshape-faithful one
//  is the default; the rest are alternatives.
// ============================================================================

internal sealed class EffigyPalette
{
	public string Name;
	public Color Bg, Chrome, Chrome2, Border, Text, TextDim, Accent, AccentSoft, ViewportBg;

	public static readonly EffigyPalette OnshapeLight = new()
	{
		Name = "Onshape Light",
		Bg = new( 0.914f, 0.922f, 0.929f ),
		Chrome = new( 0.957f, 0.961f, 0.969f ),
		Chrome2 = new( 0.925f, 0.933f, 0.945f ),
		Border = new( 0.827f, 0.843f, 0.863f ),
		Text = new( 0.125f, 0.141f, 0.165f ),
		TextDim = new( 0.439f, 0.467f, 0.498f ),
		Accent = new( 0.039f, 0.518f, 0.780f ),
		AccentSoft = new( 0.867f, 0.925f, 0.973f ),
		ViewportBg = new( 0.867f, 0.878f, 0.894f ),
	};

	public static readonly EffigyPalette OnshapeDark = new()
	{
		Name = "Onshape Dark",
		Bg = new( 0.106f, 0.118f, 0.129f ),
		Chrome = new( 0.149f, 0.161f, 0.176f ),
		Chrome2 = new( 0.173f, 0.188f, 0.204f ),
		Border = new( 0.082f, 0.090f, 0.102f ),
		Text = new( 0.843f, 0.855f, 0.867f ),
		TextDim = new( 0.545f, 0.565f, 0.588f ),
		Accent = new( 0.247f, 0.663f, 0.910f ),
		AccentSoft = new( 0.110f, 0.204f, 0.267f ),
		ViewportBg = new( 0.208f, 0.224f, 0.239f ),
	};

	public static readonly EffigyPalette Blender = new()
	{
		Name = "Blender",
		Bg = new( 0.165f, 0.165f, 0.165f ),
		Chrome = new( 0.208f, 0.208f, 0.208f ),
		Chrome2 = new( 0.235f, 0.235f, 0.235f ),
		Border = new( 0.122f, 0.122f, 0.122f ),
		Text = new( 0.839f, 0.839f, 0.839f ),
		TextDim = new( 0.533f, 0.533f, 0.533f ),
		Accent = new( 0.306f, 0.541f, 0.890f ),
		AccentSoft = new( 0.161f, 0.235f, 0.345f ),
		ViewportBg = new( 0.220f, 0.220f, 0.220f ),
	};

	public static readonly EffigyPalette Fusion = new()
	{
		Name = "Fusion",
		Bg = new( 0.145f, 0.153f, 0.161f ),
		Chrome = new( 0.184f, 0.192f, 0.204f ),
		Chrome2 = new( 0.208f, 0.216f, 0.227f ),
		Border = new( 0.106f, 0.114f, 0.122f ),
		Text = new( 0.878f, 0.886f, 0.894f ),
		TextDim = new( 0.522f, 0.533f, 0.545f ),
		Accent = new( 0.000f, 0.600f, 0.863f ),
		AccentSoft = new( 0.086f, 0.200f, 0.286f ),
		ViewportBg = new( 0.251f, 0.259f, 0.271f ),
	};

	public static readonly EffigyPalette[] All = { OnshapeLight, OnshapeDark, Blender, Fusion };
}

// ============================================================================
//  The main Effigy dock window — Onshape-faithful layout with:
//    Top:    square feature-creation icon buttons floating over the viewport's top edge
//    Left:   flat feature tree (Default geometry → features → bodies)
//    Center: 3D viewport with reference planes, origin, orbit camera
//    Right:  parameter panel for the selected feature
//    Bottom: Part-studio-style tabs
//
//  Registered under Marionette in the Tools menu. Opens from Tools or by
//  double-clicking any Effigy-related asset (if/when one exists).
// ============================================================================

[EditorApp( "Effigy", "view_in_ar", "Parametric modelling, subdivision, and rig-ready mesh export" )]
public sealed class EffigyWindow : DockWindow
{
	// --- core state -------------------------------------------------------------------------

	private PartStudio _studio;
	private EffigyViewport _viewport;

	// --- palette / theming ------------------------------------------------------------------

	private EffigyPalette _palette = EffigyPalette.OnshapeDark;
	private int _paletteIndex = 1; // start dark

	// --- panels -----------------------------------------------------------------------------

	/// <summary>Whether the viewport currently holds preview geometry — drives the one-shot
	/// camera framing in RebuildStudio.</summary>
	private bool _hasPreview;

	private EffigyFeatureTreePanel _featureTree;
	private EffigyFeatureDialog _dialog;
	private Widget _leftPanel;

	/// <summary>The creation-tool strip of square buttons floating over the viewport. Lives on
	/// the viewport rather than in a window toolbar row, so the tools sit on the thing they
	/// act on.</summary>
	private EffigyToolStrip _toolStrip;

	/// <summary>The sketch toolbar - a second row that exists only while a sketch is open, the way
	/// Onshape's does. Its tools mean nothing outside sketch mode, and leaving them visible but
	/// dead is worse than hiding them.</summary>
	private ToolBar _sketchBar;
	private readonly List<(Option Option, SketchToolKind Kind)> _sketchTools = new();
	private Option _constructionOption;
	private DockWidget _centralDock;
	private StatusBar _statusWidget;
	private Editor.Label _statusInfoLabel;
	private Editor.Label _promptLabel;

	public EffigyWindow()
	{
		DeleteOnClose = true;
		Size = new Vector2( 1440, 900 );
		SetWindowIcon( "view_in_ar" );

		_studio = new PartStudio();

		BuildMenuBar();
		BuildDocks();
		BuildToolbar();
		BuildStatusBar();

		ApplyPalette();
		Show();
	}

	// --- menu bar ---------------------------------------------------------------------------

	private void BuildMenuBar()
	{
		var file = MenuBar.FindOrCreateMenu( "File" );
		file.Clear();
		file.AddOption( "New Studio", "common/new.png", NewStudio );
		file.AddSeparator();
		file.AddOption( "Export OBJ", "file_download", ExportObj );
		file.AddOption( "Compile .vmdl", "build", CompileVmdl );
		file.AddSeparator();
		file.AddOption( "Close", "close", Close );

		var edit = MenuBar.FindOrCreateMenu( "Edit" );
		edit.Clear();
		edit.AddOption( "Undo", "undo", Undo, "editor.undo" );
		edit.AddOption( "Redo", "redo", Redo, "editor.redo" );
		edit.AddSeparator();
		edit.AddOption( "Delete Feature", "delete", DeleteSelectedFeature );
		edit.AddOption( "Move Feature Up", "arrow_upward", MoveFeatureUp );
		edit.AddOption( "Move Feature Down", "arrow_downward", MoveFeatureDown );
		edit.AddSeparator();
		edit.AddOption( "Toggle Suppress", "visibility", ToggleSuppressFeature );

		var view = MenuBar.FindOrCreateMenu( "View" );
		view.Clear();
		view.AddOption( "Frame Camera", "center_focus_strong", () => _viewport?.FrameCamera() );
		view.AddOption( "Normal to Sketch Plane\tN", "straighten", () => _viewport?.ViewNormalToSketchPlane() );

		// "restart_alt" is a Material SYMBOLS name and s&box ships classic Material Icons, so it
		// was drawing nothing at all - see EffigyIcons for why that whole class of name is unsafe.
		view.AddOption( "Reset Origin", "settings_backup_restore", () => _viewport?.ResetOrigin() );

		// The orientations Onshape's view cube offers when you click it. Ours is a corner label
		// rather than a clickable cube, so the list has to be reachable from somewhere.
		view.AddSeparator();

		foreach ( var standard in new[]
		{
			EffigyViewport.StandardView.Isometric,
			EffigyViewport.StandardView.Top,
			EffigyViewport.StandardView.Bottom,
			EffigyViewport.StandardView.Front,
			EffigyViewport.StandardView.Back,
			EffigyViewport.StandardView.Left,
			EffigyViewport.StandardView.Right,
		} )
		{
			var v = standard;
			view.AddOption( v.ToString(), "videocam", () => _viewport?.SetStandardView( v ) );
		}

		// Palette submenu
		view.AddSeparator();
		for ( var i = 0; i < EffigyPalette.All.Length; i++ )
		{
			var idx = i;
			var pal = EffigyPalette.All[idx];
			var opt = view.AddOption( pal.Name, "palette" );
			opt.Checkable = true;
			opt.Checked = idx == _paletteIndex;
			opt.Toggled += b => { if ( b ) SetPalette( idx ); };
		}
	}

	// --- toolbar (square icon buttons floating over the viewport) -------------------------

	private void BuildToolbar()
	{
		// The creation tools float ON the 3D view at its top-left, one square per button, rather
		// than in a window toolbar row - the viewport is the thing the tools act on. They are
		// parented to the canvas and positioned by the viewport, so the scene fills the whole
		// widget and nothing eats a band off the top of it.
		_toolStrip = new EffigyToolStrip( _viewport.Canvas );
		_viewport.CompleteLayout( _toolStrip );

		// --- creation tools (each adds a feature to the studio) ---
		AddCreateButton( EffigyIcon.Sketch, "Add a Sketch feature — draw lines/arcs on a plane", () => new SketchFeature() );
		_toolStrip.AddGap();
		AddCreateButton( EffigyIcon.Primitive, "Add a Primitive (Box, Cylinder, Sphere, etc.)", () => new PrimitiveFeature() );
		AddCreateButton( EffigyIcon.Extrude, "Add an Extrude — pull a sketch profile into a solid", () => new ExtrudeFeature() );
		AddCreateButton( EffigyIcon.Revolve, "Add a Revolve — sweep a sketch profile around an axis", () => new RevolveFeature() );
		_toolStrip.AddGap();
		AddCreateButton( EffigyIcon.Bevel, "Add a Bevel — chamfer sharp edges", () => new BevelFeature() );
		AddCreateButton( EffigyIcon.Shell, "Add a Shell — hollow to a wall thickness", () => new ShellFeature() );
		AddCreateButton( EffigyIcon.Subdivide, "Add a Subdivide — Catmull-Clark subdivision", () => new SubdivideFeature() );
		_toolStrip.AddGap();
		AddCreateButton( EffigyIcon.Mirror, "Add a Mirror — reflect bodies across a plane", () => new MirrorFeature() );
		AddCreateButton( EffigyIcon.LinearPattern, "Add a Linear Pattern — copy bodies along a direction", () => new LinearPatternFeature() );
		AddCreateButton( EffigyIcon.CircularPattern, "Add a Circular Pattern — copy bodies around an axis", () => new CircularPatternFeature() );
		_toolStrip.AddGap();
		AddCreateButton( EffigyIcon.Transform, "Add a Transform — move, rotate or scale bodies", () => new TransformFeature() );
		AddCreateButton( EffigyIcon.UVProject, "Add a UV Project — re-project UVs (box or planar)", () => new UVProjectFeature() );

		BuildSketchToolbar();
	}

	// --- sketch toolbar ----------------------------------------------------------------------

	/// <summary>
	/// The tools from Onshape's sketch row that this kernel can actually build.
	///
	/// Line, rectangle, circle, arc, polygon and point all map onto SketchLine / SketchArc /
	/// SketchCircle. The rest of Onshape's row — spline, trim, extend, offset, dimensions,
	/// constraints — has no kernel behind it (the handoff notes the constraint solver is the one
	/// sketcher piece not built), so those buttons are absent rather than present and dead.
	/// </summary>
	private void BuildSketchToolbar()
	{
		_sketchBar = new ToolBar( this, "EffigySketchToolbar" );
		_sketchBar.SetIconSize( 20 );
		AddToolBar( _sketchBar, ToolbarPosition.Top );

		AddSketchTool( "Select", "near_me", "Select — click without drawing", SketchToolKind.Select );
		_sketchBar.AddSeparator();

		AddSketchTool( "Line", "show_chart", "Line — click start, click end; keeps chaining until Escape", SketchToolKind.Line );

		AddSketchTool( "Rectangle", "crop_square", "Corner rectangle — click two opposite corners", SketchToolKind.Rectangle );
		AddSketchTool( "Centre Rectangle", "crop_free", "Centre rectangle — click the centre, then a corner", SketchToolKind.RectangleCentre );

		AddSketchTool( "Circle", "panorama_fish_eye", "Centre circle — click the centre, then a point on the rim", SketchToolKind.Circle );
		AddSketchTool( "3-Point Circle", "trip_origin", "3-point circle — click three points on the rim", SketchToolKind.CircleThreePoint );

		AddSketchTool( "Arc", "cached", "Centre arc — click the centre, the start, then the end direction", SketchToolKind.Arc );
		AddSketchTool( "3-Point Arc", "timeline", "3-point arc — click start, end, then a point it passes through", SketchToolKind.ArcThreePoint );

		AddSketchTool( "Polygon", "change_history", "Inscribed polygon — click the centre, then a corner", SketchToolKind.Polygon );
		AddSketchTool( "Circumscribed Polygon", "details", "Circumscribed polygon — click the centre, then an edge midpoint", SketchToolKind.PolygonCircumscribed );

		AddSketchTool( "Slot", "linear_scale", "Slot — click both ends of the centre line, then the width", SketchToolKind.Slot );
		AddSketchTool( "Point", "fiber_manual_record", "Point — click to place", SketchToolKind.Point );

		_sketchBar.AddSeparator();

		// Construction geometry is a modifier on whatever tool is active, not a tool of its own -
		// same as Onshape's toggle. SketchCurve.Construction and ProfileFinder's handling of it
		// were already in the kernel with nothing in the UI able to set them.
		var construction = new Option( "Construction", "gesture" )
		{
			ToolTip = "Construction geometry — reference lines that never become part of a profile",
			StatusTip = "Draw construction geometry: shapes the sketch, never extrudes",
			Checkable = true,
		};

		construction.Toggled += on => _viewport.ConstructionMode = on;
		_sketchBar.AddOption( construction );
		_constructionOption = construction;

		_sketchBar.AddSeparator();

		var inspector = new Option( "Profile Inspector", "select_all" )
		{
			ToolTip = "Profile Inspector — shade closed regions and highlight loose ends",
			StatusTip = "Closed profiles shade blue; loose or branching points show orange",
			Checkable = true,
			Checked = true,
		};

		inspector.Toggled += on => _viewport.ProfileInspector = on;
		_sketchBar.AddOption( inspector );

		_sketchBar.AddOption( new Option( "Finish Sketch", "check" )
		{
			ToolTip = "Leave sketch mode",
			StatusTip = "Leave sketch mode and go back to the feature tree",
			Triggered = FinishSketch,
		} );

		_sketchBar.Hide();
	}

	private void AddSketchTool( string label, string icon, string tip, SketchToolKind kind )
	{
		var option = new Option( label, icon )
		{
			ToolTip = tip,
			StatusTip = tip,
			Checkable = true,
			Checked = kind == SketchToolKind.Select,
		};

		option.Triggered = () =>
		{
			_viewport.SetSketchTool( kind );
			UpdateSketchToolChecks( kind );
		};

		_sketchBar.AddOption( option );
		_sketchTools.Add( (option, kind) );
	}

	/// <summary>Only one tool can be active, so the rest have to visibly let go. ToolBar has no
	/// radio-group concept, so the exclusivity is enforced here.</summary>
	private void UpdateSketchToolChecks( SketchToolKind active )
	{
		foreach ( var (option, kind) in _sketchTools )
			option.Checked = kind == active;
	}

	// --- sketch mode -------------------------------------------------------------------------

	/// <summary>
	/// Enter sketch mode on a Sketch feature: show the sketch toolbar, point the camera straight
	/// at the plane, and start on the Line tool.
	///
	/// The rebuild first is not optional. SketchFeature.Plane is a parameter; the Sketch object's
	/// actual plane is only assigned when the feature executes, so entering a sketch without
	/// rebuilding would draw onto whatever plane the sketch was last built with — or the XY
	/// default on a brand new one, regardless of what the selection box says.
	/// </summary>
	private void EnterSketch( SketchFeature feature )
	{
		RebuildStudio();

		_sketchBar.Show();
		_viewport.BeginSketch( feature.Sketch );

		_viewport.ConstructionMode = false;

		if ( _constructionOption is not null )
			_constructionOption.Checked = false;

		UpdateSketchToolChecks( _viewport.SketchTool );
	}

	private void FinishSketch()
	{
		if ( !_viewport.IsSketching )
			return;

		_viewport.EndSketch();
		_sketchBar.Hide();
		UpdateSketchToolChecks( SketchToolKind.Select );

		SetPrompt( "" );
		RebuildStudio();
	}

	/// <summary>A curve was drawn. Rebuilding here is what makes an extrude above the sketch update
	/// as you draw its profile.</summary>
	private void OnSketchEdited()
	{
		// The curve just drawn lives inside a SketchFeature's Sketch object, and PartStudio caches
		// a CLONE of that sketch after the feature runs (Snapshot.Of). Without marking it dirty the
		// clone is what every downstream feature keeps reading, so an extrude above the sketch never
		// sees the profile just closed.
		if ( ActiveSketchFeature() is { } sketchFeature )
			_studio.MarkDirty( sketchFeature );

		RebuildStudio();
		_dialog?.Rebuild();
	}

	/// <summary>The feature that owns the sketch currently being drawn on, by identity.</summary>
	private SketchFeature ActiveSketchFeature()
	{
		if ( _viewport?.ActiveSketch is not { } active )
			return null;

		return _studio.Features
			.OfType<SketchFeature>()
			.FirstOrDefault( f => ReferenceEquals( f.Sketch, active ) );
	}

	/// <summary>
	/// A parameter on the open feature changed.
	///
	/// MARKING IT DIRTY IS THE ENTIRE POINT OF THIS METHOD. PartStudio caches the body list after
	/// each feature and only re-runs from the first dirty one — and Rebuild() ends by setting
	/// _dirtyFrom to the feature count, so a rebuild with nothing marked reuses the whole cache and
	/// re-executes NOTHING.
	///
	/// This was wired straight to RebuildStudio, so every edit made through a feature dialog was
	/// silently thrown away: the sketch plane dropdown (which is why a sketch stayed on XY however
	/// many times you picked Front or Right), an extrude distance, subdivide levels, every checkbox.
	/// Picking highlighted beautifully and then changed nothing.
	/// </summary>
	private void OnFeatureEdited()
	{
		if ( _dialog?.Feature is { } feature )
			_studio.MarkDirty( feature );

		RebuildStudio();
	}

	/// <summary>The left half of the status bar — what the active tool wants next.</summary>
	private void SetPrompt( string prompt )
	{
		if ( _promptLabel.IsValid() )
			_promptLabel.Text = prompt;
	}

	/// <summary>A square strip button that appends one feature to the history. The factory runs
	/// per click rather than the feature being built up front, so each press makes a new one.</summary>
	private void AddCreateButton( EffigyIcon icon, string tip, Func<Feature> factory ) =>
		_toolStrip.AddButton( icon, tip, () => AddFeature( factory() ) );

	/// <summary>
	/// Append a feature and leave it selected with its dialog open — Onshape's behaviour, and the
	/// reason the buttons feel like they did something. A freshly added Extrude with no sketch
	/// above it WILL show an error; that is correct, and the parameter panel is where you fix it.
	/// </summary>
	private void AddFeature( Feature feature )
	{
		RecordUndo();

		_studio.Add( feature );
		RebuildStudio();

		_featureTree?.Select( feature );

		// Select() above already opened the dialog through the tree's selection callback, but as
		// an edit. Reopening marks it as new, which is what makes Cancel delete it rather than
		// leaving a half-configured feature behind.
		_dialog?.Open( feature, isNew: true );
	}

	// --- docks (viewport, feature tree, parameter panel) -----------------------------------

	private void BuildDocks()
	{
		_viewport = new EffigyViewport( this );

		_featureTree = new EffigyFeatureTreePanel( this, _studio )
		{
			FeatureSelected = OnFeatureSelected,
			StudioChanged = OnStudioChanged,
		};

		_dialog = new EffigyFeatureDialog( this, _viewport )
		{
			Edited = OnFeatureEdited,
			Renamed = () => _featureTree?.Rebuild(),
			Accepted = OnDialogAccepted,
			Cancelled = OnDialogCancelled,
			SketchRequested = EnterSketch,
			SketchNameLookup = id => _studio.Features.OfType<SketchFeature>().FirstOrDefault( f => f.Id == id )?.Name,
			OpenedForFeature = UpdatePickTargets,
		};

		// Dialog ABOVE the tree in one column, which is where Onshape puts it. It was a separate
		// right-hand dock at first and that was the single biggest reason the tool did not read as
		// Onshape: the thing you are editing and the history you are editing it in belong in the
		// same column, and the viewport gets everything else.
		_leftPanel = new Widget( this ) { Layout = Layout.Column() };
		_leftPanel.Name = "Features";
		_leftPanel.WindowTitle = "Features";
		_leftPanel.SetWindowIcon( "account_tree" );
		_leftPanel.Layout.Add( _dialog );
		_leftPanel.Layout.Add( _featureTree, 1 );

		_viewport.SketchEdited = OnSketchEdited;
		_viewport.SketchPromptChanged = SetPrompt;

		_centralDock = DockManager.SetCentralWidget( _viewport );

		DockManager.RegisterDock( new() { Title = "Features", Icon = "account_tree", Area = DockArea.Left, CreateAction = () => _leftPanel } );

		// Bumped from Effigy1: the Parameters dock is gone and the tree moved into a shared column
		// with the dialog. A restored Effigy1 layout would reinstate the old two-dock arrangement
		// and BuildDefaultLayout would never run again.
		StateCookie = "Effigy2";
	}

	protected override void BuildDefaultLayout()
	{
		var featuresDock = DockManager.OpenDock( "Features", DockArea.Left, _centralDock );
		DockManager.SetSplitterProportions( featuresDock, 0.26f, 0.74f );

		DockManager.RaiseDock( "Features" );
	}

	// --- status bar -------------------------------------------------------------------------

	private void BuildStatusBar()
	{
		// StatusBar has no layout of its own until one is assigned - dropping this initialiser is
		// what made BuildStatusBar throw on the very next line.
		_statusWidget = new StatusBar( this ) { Layout = Layout.Row() };
		_statusWidget.Layout.Margin = new Sandbox.UI.Margin( 8, 2 );
		_statusWidget.Layout.Spacing = 16;

		_statusWidget.Layout.Add( new Editor.Label( "Effigy" ) { FixedWidth = 52 } );

		_promptLabel = new Editor.Label( "" );
		_statusWidget.Layout.Add( _promptLabel );

		_statusWidget.Layout.AddStretchCell();

		_statusInfoLabel = new Editor.Label( "" );
		_statusWidget.Layout.Add( _statusInfoLabel );

		_viewport.ModelInfoChanged = info =>
		{
			if ( _statusInfoLabel.IsValid() )
				_statusInfoLabel.Text = info;
		};

		StatusBar = _statusWidget;
	}

	// --- feature actions --------------------------------------------------------------------

	/// <summary>
	/// Selection in the tree opens that feature's dialog.
	///
	/// A null selection deliberately does nothing. Every rebuild clears and refills the tree,
	/// which momentarily reports "nothing selected" - closing the dialog on that would slam it
	/// shut on the first tick of every slider drag, since dragging rebuilds.
	/// </summary>
	private void OnFeatureSelected( Feature feature )
	{
		if ( feature is null )
			return;

		if ( _viewport.IsSketching && feature != _dialog?.Feature )
			FinishSketch();

		if ( _dialog is null || (_dialog.IsOpen && _dialog.Feature == feature) )
			return;

		_dialog.Open( feature, isNew: false );
	}

	private void OnDialogAccepted( Feature feature )
	{
		if ( _viewport.IsSketching )
			FinishSketch();

		RebuildStudio();
	}

	/// <summary>Cancel on a feature that the toolbar had just created removes it outright - the
	/// feature only ever existed to be configured, so an abandoned dialog should leave the tree as
	/// it was. Cancelling an edit has already had its parameters restored by the dialog.</summary>
	private void OnDialogCancelled( Feature feature, bool wasNew )
	{
		if ( wasNew )
			_studio.Remove( feature );

		if ( _viewport.IsSketching )
			FinishSketch();

		RebuildStudio();
	}

	private void OnStudioChanged()
	{
		RebuildStudio();
	}

	private void RebuildStudio()
	{
		var report = _studio.Rebuild();
		_featureTree?.Rebuild();

		// Show whatever DID build, errors or not. A broken feature halfway down the tree should
		// leave the part above it on screen — going blank hides the very geometry you need to
		// look at to work out what the failing feature is missing.
		// Preview shows only what is visible; export below deliberately still takes everything.
		var preview = EffigyPreview.Build( _studio.ToVisibleMesh() );

		// Frame only when geometry first appears. Every later rebuild leaves the camera alone,
		// because rebuilds also happen on every parameter tick and the view must hold still
		// while you drag.
		_viewport?.SetModel( preview, frameCamera: preview is not null && !_hasPreview );
		_hasPreview = preview is not null;

		// Rebuild() above discarded every tree node, taking the highlight with it. The feature
		// being edited has to stay visibly selected or the tree and the dialog disagree about
		// what you are working on.
		if ( _dialog?.Feature is { } editing )
			_featureTree?.Select( editing );

		UpdateDisplaySketches();

		// Feature.Error and Feature.Warning are only meaningful once the studio has tried to run
		// the feature, so the dialog's state is refreshed here rather than when it was opened.
		_dialog?.RefreshState();

		if ( report.HasErrors )
			Log.Warning( $"[Effigy] rebuild: {string.Join( "; ", report.Errors.Select( e => e.Message ) )}" );
	}

	/// <summary>Push all committed sketches from the feature tree into the viewport so they
	/// remain visible after leaving sketch mode, and push the subset a feature being edited is
	/// allowed to pick — only sketches standing before it in the history, since a feature cannot
	/// consume a sketch that has not run yet.</summary>
	private void UpdateDisplaySketches() => UpdatePickTargets( _dialog?.Feature );

	/// <summary>Rebuild both sketch lists against the feature a dialog is open on. Called by the
	/// dialog the moment it opens, because the pick list and the auto-arm decision are only
	/// correct relative to THAT feature.</summary>
	private void UpdatePickTargets( Feature editing )
	{
		if ( _viewport is null )
			return;

		var sketchFeatures = _studio.Features.OfType<SketchFeature>().ToList();

		_viewport.SetDisplaySketches( sketchFeatures.Select( f => f.Sketch ) );

		var cutoff = editing is null ? int.MaxValue : _studio.Features.IndexOf( editing );

		if ( cutoff < 0 )
			cutoff = int.MaxValue;

		_viewport.SetPickableSketches( _studio.Features.Take( cutoff )
			.OfType<SketchFeature>()
			.Select( f => new EffigyViewport.PickableSketch( f.Id, f.Name ?? f.TypeName, f.Sketch ) ) );
	}

	private void NewStudio()
	{
		RecordUndo();
		_studio = new PartStudio();
		_featureTree?.SetStudio( _studio );
		_dialog?.Close();
		RebuildStudio();
	}

	private void DeleteSelectedFeature()
	{
		if ( _featureTree?.SelectedFeature is { } feature )
		{
			RecordUndo();
			_studio.Remove( feature );
			_dialog?.Close();
			RebuildStudio();
		}
	}

	private void MoveFeatureUp()
	{
		if ( _featureTree?.SelectedFeature is not { } feature )
			return;

		var idx = _studio.Features.IndexOf( feature );
		if ( idx > 0 )
		{
			RecordUndo();
			_studio.Move( idx, idx - 1 );
			RebuildStudio();
		}
	}

	private void MoveFeatureDown()
	{
		if ( _featureTree?.SelectedFeature is not { } feature )
			return;

		var idx = _studio.Features.IndexOf( feature );
		if ( idx < _studio.Features.Count - 1 )
		{
			RecordUndo();
			_studio.Move( idx, idx + 1 );
			RebuildStudio();
		}
	}

	private void ToggleSuppressFeature()
	{
		if ( _featureTree?.SelectedFeature is { } feature )
		{
			RecordUndo();
			feature.Suppressed = !feature.Suppressed;
			RebuildStudio();
		}
	}

	// --- export / compile (reusing EffigyTool's proven logic) -------------------------------

	private void ExportObj()
	{
		var report = _studio.Rebuild();
		if ( report.HasErrors || _studio.Bodies.Count == 0 )
		{
			Log.Warning( "[Effigy] cannot export — studio has errors or no bodies" );
			return;
		}

		var folder = KitConfig.ResolveAssetFolder( "models/effigy" );
		Directory.CreateDirectory( folder );

		var objPath = Path.Combine( folder, "export.obj" );
		ObjWriter.WriteFile( _studio.ToMesh(), objPath, "effigy_export" );
		Log.Info( $"[Effigy] exported {objPath}" );
	}

	private void CompileVmdl()
	{
		var report = _studio.Rebuild();
		if ( report.HasErrors || _studio.Bodies.Count == 0 )
		{
			Log.Warning( "[Effigy] cannot compile — studio has errors or no bodies" );
			return;
		}

		var folder = KitConfig.ResolveAssetFolder( "models/effigy" );
		Directory.CreateDirectory( folder );

		var objPath = Path.Combine( folder, "export.obj" );
		ObjWriter.WriteFile( _studio.ToMesh(), objPath, "effigy_export" );

		var vmdlPath = Path.Combine( folder, "export.vmdl" );
		File.WriteAllText( vmdlPath, BuildVmdl( "models/effigy/export.obj" ) );

		var result = ExternalAssetTools.Register( folder );
		Log.Info( $"[Effigy] wrote {objPath} and {vmdlPath} — {result.Registered} registered" );

		var asset = AssetSystem.FindByPath( "models/effigy/export.vmdl" );
		if ( asset is null )
		{
			Log.Warning( "[Effigy] export.vmdl was written but asset system couldn't find it" );
			return;
		}

		asset.Compile( true );
		Log.Info( asset.IsCompileFailed
			? "[Effigy] export.vmdl compile FAILED"
			: "[Effigy] export.vmdl compiled — loading into viewport" );

		if ( !asset.IsCompileFailed )
		{
			var model = Model.Load( "models/effigy/export.vmdl" );
			_viewport?.SetModel( model );
		}
	}

	/// <summary>Same one-node RenderMeshFile shape as EffigyTool.BuildVmdl.</summary>
	static string BuildVmdl( string meshFilename ) =>
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n" +
		"{\n" +
		"\trootNode = \n" +
		"\t{\n" +
		"\t\t_class = \"RootNode\"\n" +
		"\t\tchildren = \n" +
		"\t\t[\n" +
		"\t\t\t{\n" +
		"\t\t\t\t_class = \"RenderMeshList\"\n" +
		"\t\t\t\tchildren = \n" +
		"\t\t\t\t[\n" +
		"\t\t\t\t\t{\n" +
		"\t\t\t\t\t\t_class = \"RenderMeshFile\"\n" +
		"\t\t\t\t\t\tname = \"Body_LOD0\"\n" +
		"\t\t\t\t\t\tchildren = \n" +
		"\t\t\t\t\t\t[\n" +
		"\t\t\t\t\t\t]\n" +
		$"\t\t\t\t\t\tfilename = \"{meshFilename}\"\n" +
		"\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n" +
		"\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]\n" +
		"\t\t\t\t\t\timport_scale = 1.0\n" +
		"\t\t\t\t\t\talign_origin_x_type = \"None\"\n" +
		"\t\t\t\t\t\talign_origin_y_type = \"None\"\n" +
		"\t\t\t\t\t\talign_origin_z_type = \"None\"\n" +
		"\t\t\t\t\t\tparent_bone = \"\"\n" +
		"\t\t\t\t\t},\n" +
		"\t\t\t\t]\n" +
		"\t\t\t},\n" +
		"\t\t]\n" +
		"\t\tmodel_archetype = \"\"\n" +
		"\t\tprimary_associated_entity = \"\"\n" +
		"\t\tanim_graph_name = \"\"\n" +
		"\t\tbase_model_name = \"\"\n" +
		"\t}\n" +
		"}\n";

	// --- undo / redo -------------------------------------------------------------------------

	/// <summary>
	/// A point in the studio's history: which features exist, in what order, with what values,
	/// and where the rollback bar was.
	///
	/// The values are the part that was missing. The previous version snapshotted
	/// `_studio.Features.Select( f => f ).ToList()` - a shallow copy of the LIST, holding the same
	/// Feature objects. Parameters are the storage in this kernel (see Feature.cs: "The parameter
	/// object IS the storage"), so undo restored membership and order while silently keeping every
	/// number the user had changed since. Ctrl+Z after a parameter edit did nothing at all.
	///
	/// Values are keyed by parameter object rather than by index, because PrimitiveFeature returns
	/// a different Parameters list per shape - indices are not stable across a shape change, and
	/// the parameter objects are (they are readonly fields on the feature).
	/// </summary>
	private sealed class StudioSnapshot
	{
		public List<Feature> Features;
		public Dictionary<IParam, object> Values;
		public int RollbackIndex;
	}

	private readonly List<StudioSnapshot> _undoStack = new();
	private readonly List<StudioSnapshot> _redoStack = new();

	private StudioSnapshot Capture()
	{
		var values = new Dictionary<IParam, object>();

		foreach ( var feature in _studio.Features )
		{
			foreach ( var param in feature.Parameters )
			{
				if ( ParamValue( param ) is { } value )
					values[param] = value;
			}
		}

		return new StudioSnapshot
		{
			Features = _studio.Features.ToList(),
			Values = values,
			RollbackIndex = _studio.RollbackIndex,
		};
	}

	private static object ParamValue( IParam param ) => param switch
	{
		FloatParam f => f.Value,
		IntParam i => i.Value,
		BoolParam b => b.Value,
		Vec3Param v => v.Value,
		ChoiceParam c => c.Index,
		_ => null,
	};

	private void Restore( StudioSnapshot snapshot )
	{
		_studio.Features = snapshot.Features.ToList();
		_studio.RollbackIndex = snapshot.RollbackIndex;

		foreach ( var (param, value) in snapshot.Values )
		{
			switch ( param )
			{
				case FloatParam f when value is float v: f.Value = v; break;
				case IntParam i when value is int v: i.Value = v; break;
				case BoolParam b when value is bool v: b.Value = v; break;
				case Vec3Param p when value is Vec3 v: p.Value = v; break;
				case ChoiceParam c when value is int v: c.Index = v; break;
			}
		}

		_studio.MarkAllDirty();

		// The dialog may be open on a feature the restore just removed, and its snapshot of
		// "before" is now meaningless either way.
		_dialog?.Close();

		RebuildStudio();
	}

	/// <summary>
	/// Mark an undo point.
	///
	/// Granularity is one dialog session, not one keystroke: this is called when a feature is
	/// added, when its dialog is opened to edit it, and on the structural commands. Recording per
	/// parameter tick would put a hundred steps on the stack for one slider drag.
	/// </summary>
	private void RecordUndo()
	{
		_undoStack.Add( Capture() );
		_redoStack.Clear();

		if ( _undoStack.Count > 100 )
			_undoStack.RemoveAt( 0 );
	}

	// ShortcutType.Window, matching RigControlWindow and ShaderGraph's MainWindow. Without the
	// attribute the Edit menu's "editor.undo" name resolves to nothing and Ctrl+Z never reaches
	// this window - the menu item worked and the key did not.
	[Shortcut( "editor.undo", "CTRL+Z", ShortcutType.Window )]
	private void Undo()
	{
		if ( _undoStack.Count == 0 )
			return;

		_redoStack.Add( Capture() );

		var previous = _undoStack[^1];
		_undoStack.RemoveAt( _undoStack.Count - 1 );

		Restore( previous );
	}

	// CTRL+Y, which is what this editor's own asset editors bind redo to.
	[Shortcut( "editor.redo", "CTRL+Y", ShortcutType.Window )]
	private void Redo()
	{
		if ( _redoStack.Count == 0 )
			return;

		_undoStack.Add( Capture() );

		var next = _redoStack[^1];
		_redoStack.RemoveAt( _redoStack.Count - 1 );

		Restore( next );
	}

	// --- sketch shortcuts --------------------------------------------------------------------

	// Onshape's own sketch keys: N looks square at the sketch plane, L is line, C is circle,
	// Q toggles construction geometry. They are documented shortcuts, not invented ones.

	[Shortcut( "effigy.view.normal", "N", ShortcutType.Window )]
	private void ShortcutViewNormal() => _viewport?.ViewNormalToSketchPlane();

	[Shortcut( "effigy.sketch.line", "L", ShortcutType.Window )]
	private void ShortcutLineTool() => ArmSketchTool( SketchToolKind.Line );

	[Shortcut( "effigy.sketch.circle", "C", ShortcutType.Window )]
	private void ShortcutCircleTool() => ArmSketchTool( SketchToolKind.Circle );

	[Shortcut( "effigy.sketch.construction", "Q", ShortcutType.Window )]
	private void ShortcutConstruction()
	{
		if ( _viewport?.IsSketching != true || _constructionOption is null )
			return;

		_constructionOption.Checked = !_constructionOption.Checked;
		_viewport.ConstructionMode = _constructionOption.Checked;
	}

	/// <summary>A sketch tool key outside sketch mode has nothing to arm, and silently switching a
	/// hidden tool would leave the strip disagreeing with the viewport next time it opened.</summary>
	private void ArmSketchTool( SketchToolKind kind )
	{
		if ( _viewport?.IsSketching != true )
			return;

		_viewport.SetSketchTool( kind );
		UpdateSketchToolChecks( kind );
	}

	// --- palette / theming ------------------------------------------------------------------

	private void SetPalette( int index )
	{
		_paletteIndex = index;
		_palette = EffigyPalette.All[index];
		ApplyPalette();
		BuildMenuBar(); // rebuild so checkmarks update
	}

	/// <summary>
	/// Push the active palette at everything that reads one.
	///
	/// This set a single property that the camera had already read once in the viewport's
	/// constructor, before any palette was applied - so all four palettes rendered identically.
	/// See EffigyViewport.BackgroundColor for the other half of that fix.
	/// </summary>
	private void ApplyPalette()
	{
		if ( !_viewport.IsValid() )
			return;

		_viewport.BackgroundColor = _palette.ViewportBg;

		// Grid lines want the palette's dim text colour: it is picked to sit just above the
		// background in every one of these palettes, which is exactly the job.
		_viewport.PlaneColor = _palette.TextDim.WithAlpha( 0.55f );
	}
}

// ============================================================================
//  The left panel — a flat feature tree matching Onshape's Part Studio layout:
//
//    FEATURES (2)
//    ├─ Default geometry
//    │   ├─ Origin
//    │   ├─ Top
//    │   ├─ Front
//    │   └─ Right
//    ├─ Box
//    └─ Subdivide
//
//  Selecting a feature shows its parameters in the right panel.
//  Uses TreeView + TreeNode<T> — the same pattern as RigBonesPanel.
// ============================================================================

internal sealed class EffigyFeatureTreePanel : Widget
{
	private PartStudio _studio;
	private TreeView _tree;
	private readonly Dictionary<Feature, FeatureNode> _nodes = new();

	public Feature SelectedFeature { get; private set; }

	public Action<Feature> FeatureSelected { get; set; }
	public Action StudioChanged { get; set; }

	public EffigyFeatureTreePanel( Widget parent, PartStudio studio ) : base( parent )
	{
		Name = "Features";
		WindowTitle = "Features";
		SetWindowIcon( "account_tree" );

		_studio = studio;
		Layout = Layout.Column();

		var header = new Widget( this ) { Layout = Layout.Row() };
		header.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		header.Layout.Spacing = 8;
		header.Layout.Add( new Editor.Label( "Features" ) { FixedWidth = 80 } );
		header.Layout.Add( new Editor.Label( "" ), 1 );
		Layout.Add( header );

		_tree = new TreeView( this );
		_tree.OnSelectionChanged = objs =>
		{
			if ( objs?.FirstOrDefault() is FeatureNode node )
			{
				SelectedFeature = node.Feature;
				FeatureSelected?.Invoke( node.Feature );
			}
			else
			{
				SelectedFeature = null;
				FeatureSelected?.Invoke( null );
			}
		};
		Layout.Add( _tree, 1 );

		// Bottom: Bodies summary
		var bodies = new Widget( this ) { Layout = Layout.Row() };
		bodies.Layout.Margin = new Sandbox.UI.Margin( 8, 6 );
		var bodiesLabel = new Editor.Label( "" );
		bodies.Layout.Add( bodiesLabel );
		Layout.Add( bodies );

		Rebuild();
	}

	public void SetStudio( PartStudio studio )
	{
		// This used to drop the argument on the floor, so File > New Studio rebuilt the tree
		// against the OLD studio and the window kept showing features that were gone.
		_studio = studio ?? new PartStudio();
		Rebuild();
	}

	/// <summary>Select a feature by identity. Rebuild throws the nodes away and makes new ones,
	/// so a caller holding a Feature cannot select it without this lookup.</summary>
	public void Select( Feature feature )
	{
		if ( feature is null || !_nodes.TryGetValue( feature, out var node ) )
			return;

		SelectedFeature = feature;
		_tree.SelectItem( node );
	}

	public void Rebuild()
	{
		_tree.Clear();
		_nodes.Clear();
		SelectedFeature = null;

		// Default geometry node (always present, like Onshape)
		var defaultGeo = _tree.AddItem( new DefaultGeometryNode() );
		_tree.Open( defaultGeo );

		// Feature nodes
		foreach ( var feature in _studio.Features )
		{
			var node = new FeatureNode( feature );
			_nodes[feature] = node;
			_tree.AddItem( node );

			if ( feature.Suppressed )
				_tree.Close( node );
		}
	}

	// --- tree node types --------------------------------------------------------------------

	/// <summary>Root "Default geometry" node with Origin/Top/Front/Right children.</summary>
	private sealed class DefaultGeometryNode : TreeNode<string>
	{
		public DefaultGeometryNode() : base( "Default geometry" ) { }

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			Paint.SetPen( Theme.TextLight );
			Paint.DrawIcon( item.Rect, "folder", 14, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text );
			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), Value, TextFlag.LeftCenter );
		}

		protected override void BuildChildren()
		{
			ClearChildren();
			AddItems( new DefaultGeometryChildNode[]
			{
				new( "Origin", "adjust" ),
				new( "Top (XY)", "crop_landscape" ),
				new( "Front (XZ)", "crop_landscape" ),
				new( "Right (YZ)", "crop_landscape" ),
			} );
		}
	}

	/// <summary>Origin and the three reference planes under "Default geometry".</summary>
	private sealed class DefaultGeometryChildNode : TreeNode<string>
	{
		private readonly string _icon;

		public DefaultGeometryChildNode( string name, string icon ) : base( name )
		{
			_icon = icon;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			Paint.SetPen( Theme.TextLight );
			Paint.DrawIcon( item.Rect, _icon, 14, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text );
			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), Value, TextFlag.LeftCenter );
		}
	}

	/// <summary>A feature in the tree — icon + name + error/suppressed indicator.</summary>
	private sealed class FeatureNode : TreeNode<Feature>
	{
		public Feature Feature => Value;

		public FeatureNode( Feature feature ) : base( feature ) { }

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			// Icon color: blue for active, grey for suppressed, red for error
			if ( Value.Suppressed )
				Paint.SetPen( Theme.TextLight.WithAlpha( 0.5f ) );
			else if ( Value.Error is not null )
				Paint.SetPen( Theme.Red );
			else
				Paint.SetPen( Theme.Blue );

			Paint.DrawIcon( item.Rect, "category", 14, TextFlag.LeftCenter );

			Paint.SetPen( Value.Suppressed ? Theme.TextLight : Theme.Text );
			var label = $"{Value.Name ?? Value.TypeName}";
			if ( Value.Suppressed )
				label += " (suppressed)";
			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), label, TextFlag.LeftCenter );
		}
	}
}

// ============================================================================
//  The tool strip — a row of square icon buttons sitting at the top of the
//  viewport's Column layout, one square per creation tool. It replaces the
//  old window toolbar row: the buttons sit on the viewport they act on, at
//  the same edge the sketch toolbar appears when a sketch opens.
//
//  The strip is added to the viewport's Column layout above the 3D canvas,
//  which fills the rest of the space. Same painting pattern as the
//  hand-painted RigIconButton in the rig editor.
// ============================================================================

internal sealed class EffigyToolStrip : Widget
{
	/// <summary>Gap between buttons inside a group. Every one is identical - the strip was
	/// previously left to the layout's own distribution, which spread twelve buttons across the
	/// full width of the viewport at whatever intervals happened to fall out.</summary>
	private const float ButtonSpacing = 3f;

	/// <summary>Gap between tool groups. Wider than ButtonSpacing and used nowhere else, so the
	/// grouping reads as deliberate rather than as uneven spacing.</summary>
	private const float GroupSpacing = 11f;

	public EffigyToolStrip( Widget parent ) : base( parent )
	{
		Layout = Layout.Row();
		Layout.Spacing = ButtonSpacing;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		// No background of its own: the strip floats on the 3D view and only its buttons should
		// be visible. Anything painted here would read as a chrome band across the viewport.
		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedHeight = 28;
	}

	public EffigyToolButton AddButton( EffigyIcon icon, string tip, Action clicked )
	{
		var button = new EffigyToolButton( this, icon, tip, clicked );
		Layout.Add( button );
		AdjustSize();
		return button;
	}

	/// <summary>A spacer standing in for the old toolbar's separators, keeping the tool groups
	/// readable. Sized to the difference so the visible gap is exactly GroupSpacing.</summary>
	public void AddGap()
	{
		Layout.Add( new Widget( this ) { FixedWidth = GroupSpacing - ButtonSpacing } );
		AdjustSize();
	}
}

/// <summary>One square of the strip — a hand-painted icon button, 28x28, matching the
/// editor's own button states so it reads as chrome rather than a foreign object.</summary>
internal sealed class EffigyToolButton : Widget
{
	private readonly EffigyIcon _icon;
	private readonly Action _clicked;
	private bool _pressed;

	public EffigyToolButton( Widget parent, EffigyIcon icon, string tip, Action clicked ) : base( parent )
	{
		_icon = icon;
		_clicked = clicked;

		ToolTip = tip;
		StatusTip = tip;
		Cursor = CursorShape.Finger;
		MouseTracking = true;

		FixedSize = new Vector2( 28, 28 );
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		var hovered = IsUnderMouse;

		// Sitting on the 3D view rather than in a toolbar, the button has to carry its own
		// contrast - the scene behind it can be any colour. Mostly-opaque at rest, fully opaque
		// once you are on it.
		Paint.ClearPen();
		Paint.SetBrush( _pressed
			? Theme.ControlBackground.Darken( 0.2f )
			: hovered ? Theme.ControlBackground.Lighten( 0.4f ) : Theme.ControlBackground.WithAlpha( 0.82f ) );

		Paint.DrawRect( LocalRect, 4f );

		EffigyIcons.Draw( _icon, LocalRect.Center, hovered ? Theme.Text : Theme.TextLight );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		_pressed = true;
		Update();
		e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( !_pressed )
			return;

		_pressed = false;
		Update();

		// Only fires if released while still over the button - dragging off to cancel is
		// what every other button does.
		if ( IsUnderMouse )
			_clicked?.Invoke();
	}

	protected override void OnMouseEnter()
	{
		base.OnMouseEnter();
		Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();

		_pressed = false;
		Update();
	}
}
