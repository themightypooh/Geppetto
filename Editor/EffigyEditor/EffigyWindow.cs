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
//  The main Effigy dock window — Onshape's Part Studio layout:
//
//    Left:   the feature dialog, above the feature tree, above the parts list
//    Centre: the 3D viewport, with the tool strips along its top edge
//    Bottom: status bar — what the active tool wants next, and mesh stats
//
//  The tools live in the VIEWPORT rather than in the window's toolbar area, and
//  the dialog shares the left column with the tree rather than having a dock of
//  its own. Both are Onshape's arrangement, and both were the difference
//  between this reading as a CAD tool and reading as a form with a 3D preview.
//
//  Registered as "Effigy" in the Tools menu — RigControlWindow is the one
//  called "Marionette".
// ============================================================================

// Named "Effigy", not "Marionette". RigControlWindow already registers an EditorApp called
// Marionette, so this one put a second identically-named entry in the Tools menu with nothing to
// tell them apart — and the modelling tool is Effigy, which is what its own docs call it.
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

	/// <summary>The sketch tools, which replace the feature strip along the top of the viewport
	/// for as long as a sketch is open - the way Onshape swaps its toolbar. They mean nothing
	/// outside sketch mode, and leaving them visible but dead is worse than hiding them.</summary>
	private readonly List<(EffigyToolButton Button, SketchToolKind Kind)> _sketchTools = new();
	private EffigyToolButton _constructionButton;
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
		edit.AddSeparator();
		edit.AddOption( "Roll to Selected Feature", "history",
			() => { if ( _featureTree?.SelectedFeature is { } f ) RollToHere( f ); } );
		edit.AddOption( "Roll to End", "last_page", RollToEnd );

		var view = MenuBar.FindOrCreateMenu( "View" );
		view.Clear();
		view.AddOption( "Frame Camera", "center_focus_strong", () => _viewport?.FrameCamera() );
		view.AddOption( "Normal to Sketch Plane\tN", "straighten", () => _viewport?.ViewNormalToSketchPlane() );
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
	// --- feature toolbar (a strip of boxes along the top of the viewport) --------------------

	/// <summary>
	/// The feature tools, in Onshape's order: create, then modify, then repeat, then place.
	///
	/// These used to be Options on an Editor.ToolBar docked to the top of the WINDOW, which put
	/// them up in the chrome beside File/Edit/View where they read as window furniture rather than
	/// as the modelling tools. They belong directly above the graphics area they act on, each in
	/// its own box - see EffigyViewportToolbar.
	///
	/// Grouped tools carry a chevron and open a menu of their variants, the way Onshape's Fillet
	/// and Pattern buttons do, rather than spending a top-level box on every variant.
	/// </summary>
	private void BuildToolbar()
	{
		// Undo/redo first and leftmost, where Onshape's toolbar starts. They sit in their own
		// group so they stay put when the feature tools are swapped out for the sketch tools.
		var history = _viewport.HistoryTools;

		history.AddTool( "undo", "Undo (Ctrl+Z)", Undo );
		history.AddTool( "redo", "Redo (Ctrl+Y)", Redo );
		history.AddSeparator();

		var bar = _viewport.FeatureTools;

		bar.AddTool( "edit", "Sketch - draw lines, arcs and circles on a plane",
			() => AddFeature( new SketchFeature() ) );

		bar.AddSeparator();

		bar.AddTool( "widgets", "Primitive - box, cylinder, sphere, wedge or tube",
			() => AddFeature( new PrimitiveFeature() ) );

		bar.AddSeparator();

		// --- build geometry out of a sketch ---
		bar.AddTool( "expand_less", "Extrude - pull a sketch profile into a solid",
			() => AddFeature( new ExtrudeFeature() ) );

		bar.AddTool( "360", "Revolve - sweep a sketch profile around an axis",
			() => AddFeature( new RevolveFeature() ) );

		bar.AddSeparator();

		// --- modify what is already there ---
		bar.AddTool( "call_made", "Bevel - chamfer every edge sharper than a threshold",
			() => AddFeature( new BevelFeature() ) );

		bar.AddTool( "crop_square", "Shell - hollow the body out to a wall thickness",
			() => AddFeature( new ShellFeature() ) );

		bar.AddTool( "grid_on", "Subdivide - Catmull-Clark subdivision",
			() => AddFeature( new SubdivideFeature() ) );

		bar.AddSeparator();

		// --- repeat it ---
		bar.AddTool( "content_copy", "Linear pattern - copy bodies along a direction (right-click for circular)",
			() => AddFeature( new LinearPatternFeature() ),
			dropdown: () =>
			{
				var menu = new Menu( this );
				menu.AddOption( "Linear pattern", "content_copy", () => AddFeature( new LinearPatternFeature() ) );
				menu.AddOption( "Circular pattern", "rotate_right", () => AddFeature( new CircularPatternFeature() ) );
				menu.OpenAtCursor();
			} );

		bar.AddTool( "flip", "Mirror - reflect bodies across a plane",
			() => AddFeature( new MirrorFeature() ) );

		bar.AddSeparator();

		// --- place it ---
		bar.AddTool( "open_with", "Transform - move, rotate or scale bodies",
			() => AddFeature( new TransformFeature() ) );

		bar.AddTool( "texture", "UV Project - re-project UVs, box or planar",
			() => AddFeature( new UVProjectFeature() ) );

		BuildSketchToolbar();
	}

	// --- sketch toolbar ----------------------------------------------------------------------

	/// <summary>
	/// The tools from Onshape's sketch row that this kernel can actually build.
	///
	/// Line, rectangle, circle, arc, polygon, slot and point all map onto SketchLine / SketchArc /
	/// SketchCircle. The rest of Onshape's row - spline, trim, extend, offset, dimensions,
	/// constraints - has no kernel behind it (the constraint solver is the one sketcher piece not
	/// built), so those buttons are absent rather than present and dead.
	///
	/// Variants sit under chevrons exactly as they do in Onshape: one Rectangle box offering
	/// corner and centre, one Circle box offering centre and 3-point, and so on. The previous
	/// version spent a top-level button on each variant, which is how a sketch row ends up
	/// fourteen glyphs wide with no way to tell which is which.
	/// </summary>
	private void BuildSketchToolbar()
	{
		var bar = _viewport.SketchTools;

		AddSketchTool( bar, "near_me", "Select - click without drawing", SketchToolKind.Select );

		bar.AddSeparator();

		AddSketchTool( bar, "show_chart", "Line - click start, click end; keeps chaining until Escape",
			SketchToolKind.Line );

		AddSketchTool( bar, "crop_square", "Corner rectangle - click two opposite corners",
			SketchToolKind.Rectangle,
			variants: new[]
			{
				("Corner rectangle", "crop_square", SketchToolKind.Rectangle),
				("Centre rectangle", "crop_free", SketchToolKind.RectangleCentre),
			} );

		AddSketchTool( bar, "panorama_fish_eye", "Centre circle - click the centre, then a point on the rim",
			SketchToolKind.Circle,
			variants: new[]
			{
				("Centre circle", "panorama_fish_eye", SketchToolKind.Circle),
				("3-point circle", "trip_origin", SketchToolKind.CircleThreePoint),
			} );

		AddSketchTool( bar, "cached", "Centre arc - click the centre, the start, then the end direction",
			SketchToolKind.Arc,
			variants: new[]
			{
				("Centre arc", "cached", SketchToolKind.Arc),
				("3-point arc", "timeline", SketchToolKind.ArcThreePoint),
			} );

		AddSketchTool( bar, "change_history", "Inscribed polygon - click the centre, then a corner",
			SketchToolKind.Polygon,
			variants: new[]
			{
				("Inscribed polygon", "change_history", SketchToolKind.Polygon),
				("Circumscribed polygon", "details", SketchToolKind.PolygonCircumscribed),
			} );

		AddSketchTool( bar, "linear_scale", "Slot - click both ends of the centre line, then the width",
			SketchToolKind.Slot );

		AddSketchTool( bar, "fiber_manual_record", "Point - click to place", SketchToolKind.Point );

		bar.AddSeparator();

		// Construction geometry is a modifier on whatever tool is active, not a tool of its own -
		// same as Onshape's toggle, and on the same Q key. SketchCurve.Construction and
		// ProfileFinder's handling of it were already in the kernel with nothing able to set them.
		_constructionButton = bar.AddTool( "gesture",
			"Construction geometry (Q) - reference lines that never become part of a profile",
			null, checkable: true );

		_constructionButton.Clicked = () => _viewport.ConstructionMode = _constructionButton.Checked;

		var inspector = bar.AddTool( "select_all",
			"Profile inspector - shade closed regions and highlight loose ends",
			null, checkable: true );

		inspector.Checked = true;
		_viewport.ProfileInspector = true;

		inspector.Clicked = () => _viewport.ProfileInspector = inspector.Checked;

		bar.AddSeparator();

		bar.AddTool( "check", "Finish sketch (Enter) - leave sketch mode", FinishSketch );
	}

	/// <summary>
	/// One sketch tool box. When variants are supplied the box carries a chevron, opens them on
	/// right-click or on the chevron itself, and adopts whichever variant was chosen so the box
	/// keeps showing the tool you are actually holding.
	/// </summary>
	private void AddSketchTool( EffigyViewportToolbar bar, string icon, string tip, SketchToolKind kind,
		(string Label, string Icon, SketchToolKind Kind)[] variants = null )
	{
		EffigyToolButton button = null;

		// The click reads the box's CURRENT kind rather than closing over the one it was built
		// with, or picking "3-point circle" from the chevron and then clicking the box would arm
		// the centre circle again.
		button = bar.AddTool( icon, tip, () => ChooseSketchTool( CurrentKindOf( button, kind ) ),
			checkable: true,
			dropdown: variants is null
				? null
				: () =>
				{
					var menu = new Menu( this );

					foreach ( var variant in variants )
					{
						var v = variant;

						menu.AddOption( v.Label, v.Icon, () =>
						{
							// Retarget the box - kind, icon and tooltip - so the strip shows the
							// variant in use rather than always showing the group's default.
							ReplaceSketchToolKind( button, v.Kind );
							button.Icon = v.Icon;
							button.ToolTip = v.Label;
							ChooseSketchTool( v.Kind );
						} );
					}

					menu.OpenAtCursor();
				} );

		button.Checked = kind == SketchToolKind.Select;
		_sketchTools.Add( (button, kind) );
	}

	/// <summary>Which tool a grouped box is currently pointed at.</summary>
	private SketchToolKind CurrentKindOf( EffigyToolButton button, SketchToolKind fallback )
	{
		foreach ( var (b, k) in _sketchTools )
		{
			if ( b == button )
				return k;
		}

		return fallback;
	}

	/// <summary>Point an existing box at a different tool kind after its variant menu was used.</summary>
	private void ReplaceSketchToolKind( EffigyToolButton button, SketchToolKind kind )
	{
		for ( var i = 0; i < _sketchTools.Count; i++ )
		{
			if ( _sketchTools[i].Button != button )
				continue;

			_sketchTools[i] = (button, kind);
			return;
		}
	}

	private void ChooseSketchTool( SketchToolKind kind )
	{
		_viewport.SetSketchTool( kind );
		UpdateSketchToolChecks( kind );
	}

	/// <summary>Only one tool can be active, so the rest have to visibly let go. Construction and
	/// the profile inspector are toggles rather than tools and are left alone.</summary>
	private void UpdateSketchToolChecks( SketchToolKind active )
	{
		foreach ( var (button, kind) in _sketchTools )
			button.Checked = kind == active;
	}

	// --- sketch mode -------------------------------------------------------------------------

	/// <summary>
	/// Enter sketch mode on a Sketch feature: swap the viewport's tool strip for the sketch one
	/// and start on the Line tool.
	///
	/// The rebuild first is not optional. SketchFeature.Plane is a parameter; the Sketch object's
	/// actual plane is only assigned when the feature executes, so entering a sketch without
	/// rebuilding would draw onto whatever plane the sketch was last built with — or the XY
	/// default on a brand new one, regardless of what the selection box says.
	///
	/// The camera is deliberately left alone. Onshape does not rotate the view when you pick a
	/// plane, and taking a three-quarter view away from someone who set it up to sketch against
	/// existing geometry is worse than the N key they can press themselves.
	/// </summary>
	private void EnterSketch( SketchFeature feature )
	{
		RebuildStudio();

		ShowSketchTools( true );
		_viewport.BeginSketch( feature.Sketch );

		_viewport.ConstructionMode = false;

		if ( _constructionButton is not null )
			_constructionButton.Checked = false;

		UpdateSketchToolChecks( _viewport.SketchTool );
	}

	private void FinishSketch()
	{
		if ( !_viewport.IsSketching )
			return;

		_viewport.EndSketch();
		ShowSketchTools( false );
		UpdateSketchToolChecks( SketchToolKind.Select );

		SetPrompt( "" );
		RebuildStudio();
	}

	/// <summary>One strip at a time, in the same place. Onshape replaces its toolbar on entering a
	/// sketch rather than stacking a second row, and stacking was costing a row of viewport height
	/// permanently to hold tools that are dead most of the time.</summary>
	private void ShowSketchTools( bool sketching )
	{
		if ( _viewport?.SketchTools.IsValid() == true )
			_viewport.SketchTools.Visible = sketching;

		if ( _viewport?.FeatureTools.IsValid() == true )
			_viewport.FeatureTools.Visible = !sketching;
	}

	/// <summary>A curve was drawn. Rebuilding here is what makes an extrude above the sketch update
	/// as you draw its profile.</summary>
	private void OnSketchEdited()
	{
		RebuildStudio();
		_dialog?.Rebuild();
	}

	/// <summary>The left half of the status bar — what the active tool wants next.</summary>
	private void SetPrompt( string prompt )
	{
		if ( _promptLabel.IsValid() )
			_promptLabel.Text = prompt;
	}

	/// <summary>A toolbar button that appends one feature to the history. The factory runs per
	/// click rather than the feature being built up front, so each press makes a new one.</summary>
	private Option CreateOption( string text, string icon, string tip, Func<Feature> factory ) =>
		new( text, icon )
		{
			ToolTip = tip,
			StatusTip = tip,
			Triggered = () => AddFeature( factory() ),
		};

	/// <summary>
	/// Append a feature and leave it selected with its dialog open — Onshape's behaviour, and the
	/// reason the buttons feel like they did something. A freshly added Extrude with no sketch
	/// above it WILL show an error; that is correct, and the parameter panel is where you fix it.
	/// </summary>
	private void AddFeature( Feature feature )
	{
		RecordUndo();

		// A brand new Extrude is WAITING for a face, so it must not build anything yet. Left to
		// run, a null RegionSeed means "every closed region" — the documented default, and correct
		// for a feature someone configured deliberately, but as the response to pressing the
		// toolbar button it slams a finished solid onto the screen before you have chosen anything.
		// Suppression is lifted the moment a face is picked, in OnRegionPicked.
		if ( feature is SketchConsumingFeature { RegionSeed: null } )
			feature.Suppressed = true;

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
			ContextMenuRequested = OpenFeatureMenu,
			RollToEndRequested = RollToEnd,
		};

		_viewport.RegionPicked = OnRegionPicked;
		_viewport.FacePullChanged = OnFacePullChanged;
		_viewport.FacePullEnded = OnFacePullEnded;

		_dialog = new EffigyFeatureDialog( this, _viewport )
		{
			Edited = RebuildStudio,
			Renamed = () => _featureTree?.Rebuild(),
			Accepted = OnDialogAccepted,
			Cancelled = OnDialogCancelled,
			SketchRequested = EnterSketch,
			RepeatRequested = AddFeature,
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

		// One undo step per dialog session. Taken here, before any edit, so Ctrl+Z after fiddling
		// with a feature's parameters goes back to how it was when you opened it.
		RecordUndo();

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
		var preview = EffigyPreview.Build( _studio.ToMesh() );

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

		PushSketchesToViewport();
		RefreshPullTarget();

		// Feature.Error is only meaningful once the studio has tried to run it, so the dialog's
		// red/blocked state has to be refreshed here rather than when it was opened.
		_dialog?.RefreshState();

		if ( report.HasErrors )
			Log.Warning( $"[Effigy] rebuild: {string.Join( "; ", report.Errors.Select( e => e.Message ) )}" );
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

	// --- sketches and faces in the viewport ----------------------------------------------------

	/// <summary>
	/// Hand the viewport every sketch the studio currently has, so finished sketches keep drawing
	/// and their closed regions stay pickable after you leave them.
	///
	/// Rolled-back features are excluded: a feature past the bar did not run, so its sketch has no
	/// resolved plane for this rebuild and drawing it would put geometry on screen that nothing in
	/// the model is standing on.
	/// </summary>
	private void PushSketchesToViewport()
	{
		if ( !_viewport.IsValid() )
			return;

		var active = _studio.Features.Take( _studio.EffectiveCount ).ToList();

		_viewport.SetVisibleSketches( active
			.OfType<SketchFeature>()
			.Select( f => (f.Id, f.Sketch) ) );

		// Which faces are already spoken for, so they draw as taken rather than as free.
		_viewport.SetSelectedRegions( active
			.OfType<SketchConsumingFeature>()
			.Where( f => f.RegionSeed is not null )
			.Select( f => (ResolveSketchId( f, active ), f.RegionSeed.Value) )
			.Where( r => !string.IsNullOrEmpty( r.Item1 ) ) );
	}

	/// <summary>
	/// Which sketch a consuming feature actually reads.
	///
	/// An empty SketchFeatureId means "the most recent one", matching
	/// SketchConsumingFeature.ResolveSketch — so the viewport has to resolve it the same way or a
	/// face selected on the default sketch would draw as unselected.
	/// </summary>
	private static string ResolveSketchId( SketchConsumingFeature feature, List<Feature> active )
	{
		if ( !string.IsNullOrEmpty( feature.SketchFeatureId ) )
			return feature.SketchFeatureId;

		// Only sketches ABOVE the consumer are visible to it, same as the rebuild sees them.
		var index = active.IndexOf( feature );

		return active.Take( index < 0 ? active.Count : index )
			.OfType<SketchFeature>()
			.LastOrDefault()
			?.Id ?? "";
	}

	/// <summary>
	/// A face was clicked while the open feature's region box was armed.
	///
	/// The seed is the click point itself, which is inside the region by construction — that is
	/// the whole reason RegionSeed is a point rather than an index (see SketchFeatures.cs).
	/// </summary>
	private void OnRegionPicked( string sketchFeatureId, Vec2 seed )
	{
		if ( _dialog?.Feature is not SketchConsumingFeature consumer )
			return;

		consumer.SketchFeatureId = sketchFeatureId;
		consumer.RegionSeed = seed;

		// It has what it was waiting for, so let it build.
		consumer.Suppressed = false;

		_studio.MarkDirty( consumer );
		_viewport.RegionPickMode = false;

		// Redraw the dialog so the box stops saying "click a face", then rebuild so the extrude
		// immediately shows the one region rather than all of them. RebuildStudio points the pull
		// handle at the face that was just chosen, which is what makes the gizmo appear.
		_dialog.Rebuild();
		RebuildStudio();
	}

	// --- drag the chosen face out into a solid --------------------------------------------------

	/// <summary>
	/// The gizmo moved. The feature already exists — it was created by the toolbar button and is
	/// sitting open in the dialog waiting for exactly this — so this only writes the distance.
	///
	/// No undo step of its own: the dialog session is the undo granularity everywhere else in this
	/// tool, and it was already recorded when the feature was added. Ctrl+Z after dragging a face
	/// out undoes the extrude, which is what "undo that" means here.
	/// </summary>
	private void OnFacePullChanged( EffigyFacePull pull )
	{
		if ( _dialog?.Feature is not ExtrudeFeature extrude )
			return;

		// A negative pull is a real answer - it means through the plane the other way - so the sign
		// goes into Flip rather than being thrown away. The floor keeps it off exactly zero, which
		// the kernel rejects outright ("Distance cannot be zero").
		extrude.Distance.Value = MathF.Max( MathF.Abs( pull.Distance ), 1e-4f );
		extrude.Flip.Value = pull.Distance < 0f;

		_studio.MarkDirty( extrude );
		RebuildStudio();

		// Push the new number into the dialog's field, so the typed value and the gizmo are two
		// views of one thing rather than two competing sources of truth.
		_dialog.RefreshValues();
	}

	private void OnFacePullEnded() => _dialog?.RefreshState();

	/// <summary>
	/// Keep the viewport's handle pointed at whatever the open dialog is building.
	///
	/// Only Extrude gets a handle: it is the one feature whose single parameter is a distance along
	/// the face's own normal, which is the only thing this gesture can express. Revolve's angle is
	/// not that, so it gets the selection box and the dialog and no gizmo.
	/// </summary>
	private void RefreshPullTarget()
	{
		if ( !_viewport.IsValid() )
			return;

		// Written out rather than as `is not ExtrudeFeature { RegionSeed: { } seed } extrude`:
		// designations inside a NEGATED nested pattern are the kind of definite-assignment corner
		// that is not worth betting a compile on when the plain version reads the same.
		var extrude = _dialog?.Feature as ExtrudeFeature;

		if ( extrude?.RegionSeed is null )
		{
			_viewport.ClearPullTarget();
			return;
		}

		var seed = extrude.RegionSeed.Value;

		var active = _studio.Features.Take( _studio.EffectiveCount ).ToList();
		var sketchId = ResolveSketchId( extrude, active );

		if ( string.IsNullOrEmpty( sketchId ) )
		{
			_viewport.ClearPullTarget();
			return;
		}

		var distance = extrude.Flip.Value ? -extrude.Distance.Value : extrude.Distance.Value;

		_viewport.SetPullTarget( sketchId, seed, distance );
	}

	// --- rollback ----------------------------------------------------------------------------

	/// <summary>
	/// Onshape's "Roll to here": evaluate only the features ABOVE this one, so you can go back and
	/// work on the model as it was at that point.
	///
	/// PartStudio.RollbackIndex and the snapshot cache behind it have been in the kernel since it
	/// was written — its own class comment calls rollback one of the "two things that make this
	/// parametric rather than a pile of bakes" — and the editor referenced it exactly zero times.
	/// This is the whole feature: set an int, rebuild.
	///
	/// No MarkAllDirty. Rebuild truncates its cache to EffectiveCount on the way down and re-runs
	/// from there on the way back up, so rolling is already incremental; forcing a full rebuild
	/// would only throw that away.
	/// </summary>
	private void RollToHere( Feature feature )
	{
		var index = _studio.Features.IndexOf( feature );

		if ( index < 0 || _studio.RollbackIndex == index )
			return;

		RecordUndo();
		_studio.RollbackIndex = index;
		RebuildStudio();
	}

	private void RollToEnd()
	{
		if ( _studio.RollbackIndex == int.MaxValue )
			return;

		RecordUndo();
		_studio.RollbackIndex = int.MaxValue;
		RebuildStudio();
	}

	// --- feature context menu ------------------------------------------------------------------

	/// <summary>
	/// Right-click on the feature list. Onshape puts rename, suppress, roll-to-here and delete
	/// here, and before this every one of them was reachable only from the Edit menu.
	/// </summary>
	private void OpenFeatureMenu( Feature feature )
	{
		var menu = new Menu( this );

		if ( feature is null )
		{
			// Nothing selected — the only command that still means something is the global one.
			var rollEnd = menu.AddOption( "Roll to end", "last_page", RollToEnd );
			rollEnd.Enabled = _studio.RollbackIndex != int.MaxValue;

			menu.OpenAtCursor();
			return;
		}

		menu.AddHeading( feature.Name ?? feature.TypeName );

		// The dialog owns the name field, so "edit" and "rename" are the same command.
		menu.AddOption( "Edit / rename", "edit", () => OnFeatureSelected( feature ) );

		menu.AddSeparator();
		menu.AddOption( feature.Suppressed ? "Unsuppress" : "Suppress", "visibility", ToggleSuppressFeature );

		menu.AddSeparator();
		menu.AddOption( "Roll to here", "history", () => RollToHere( feature ) );

		var toEnd = menu.AddOption( "Roll to end", "last_page", RollToEnd );
		toEnd.Enabled = _studio.RollbackIndex != int.MaxValue;

		menu.AddSeparator();
		menu.AddOption( "Move up", "arrow_upward", MoveFeatureUp );
		menu.AddOption( "Move down", "arrow_downward", MoveFeatureDown );

		menu.AddSeparator();
		menu.AddOption( "Delete", "delete", DeleteSelectedFeature );

		menu.OpenAtCursor();
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
		if ( _viewport?.IsSketching != true || _constructionButton is null )
			return;

		_constructionButton.Checked = !_constructionButton.Checked;
		_viewport.ConstructionMode = _constructionButton.Checked;
	}

	/// <summary>A sketch tool key outside sketch mode has nothing to arm, and silently switching a
	/// hidden tool would leave the strip disagreeing with the viewport next time it opened.</summary>
	private void ArmSketchTool( SketchToolKind kind )
	{
		if ( _viewport?.IsSketching != true )
			return;

		ChooseSketchTool( kind );
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
	/// This used to set a single auto-property that nothing downstream ever read again, so all
	/// four palettes rendered identically — see EffigyViewport.BackgroundColor for the half of
	/// that bug that lived at the other end.
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
//  The left panel — Onshape's Part Studio feature list:
//
//    FEATURES
//    ├─ Default geometry
//    │   ├─ Origin
//    │   ├─ Top (XY) / Front (XZ) / Right (YZ)
//    ├─ Sketch 1
//    ├─ Extrude 1
//    ──────────── rollback bar ────────────
//    └─ Subdivide 1        (rolled back, not evaluated)
//
//    PARTS
//    └─ Part 1 · 384 faces
//
//  Right-click a feature for rename / suppress / roll to here / delete, which
//  is where Onshape puts all of them.
// ============================================================================

internal sealed class EffigyFeatureTreePanel : Widget
{
	private PartStudio _studio;
	private TreeView _tree;
	private readonly Dictionary<Feature, FeatureNode> _nodes = new();

	private Editor.Label _rollbackLabel;
	private Widget _partsRows;
	private readonly List<Widget> _partWidgets = new();

	public Feature SelectedFeature { get; private set; }

	public Action<Feature> FeatureSelected { get; set; }
	public Action StudioChanged { get; set; }

	/// <summary>Right-click on the list. The window owns the menu because every command on it
	/// needs RecordUndo and RebuildStudio, which live there.</summary>
	public Action<Feature> ContextMenuRequested { get; set; }

	/// <summary>"Roll to end" from the panel's own control row.</summary>
	public Action RollToEndRequested { get; set; }

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
		header.Layout.AddStretchCell();
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

		// --- rollback control row ---
		//
		// Onshape's rollback bar is dragged between two features in the list. A TreeView gives no
		// row to drag a bar into, so the state is shown here and moved by "Roll to here" from the
		// context menu - the same command Onshape offers on right-click, which is how most people
		// move the bar anyway.
		var rollback = new Widget( this ) { Layout = Layout.Row() };
		rollback.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		rollback.Layout.Spacing = 6;

		_rollbackLabel = new Editor.Label( "" );
		rollback.Layout.Add( _rollbackLabel, 1 );

		rollback.Layout.Add( new Button( "Roll to end", "last_page" )
		{
			Clicked = () => RollToEndRequested?.Invoke(),
		} );

		Layout.Add( rollback );

		// --- parts list ---
		var partsHeader = new Widget( this ) { Layout = Layout.Row() };
		partsHeader.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		partsHeader.Layout.Add( new Editor.Label( "Parts" ) { FixedWidth = 80 } );
		partsHeader.Layout.AddStretchCell();
		Layout.Add( partsHeader );

		_partsRows = new Widget( this ) { Layout = Layout.Column() };
		Layout.Add( _partsRows );

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

	/// <summary>
	/// Right-click anywhere in the panel acts on the selected feature.
	///
	/// It is on the panel rather than on the TreeView's rows because TreeNode has no context-menu
	/// hook proven against this editor's API. The cost is that you select first and right-click
	/// second, rather than right-clicking straight onto a row.
	/// </summary>
	protected override void OnContextMenu( ContextMenuEvent e )
	{
		ContextMenuRequested?.Invoke( SelectedFeature );
		e.Accepted = true;
	}

	public void Rebuild()
	{
		_tree.Clear();
		_nodes.Clear();
		SelectedFeature = null;

		// Default geometry node (always present, like Onshape)
		var defaultGeo = _tree.AddItem( new DefaultGeometryNode() );
		_tree.Open( defaultGeo );

		// Feature nodes. Anything at or past the rollback bar is drawn as not evaluated.
		var effective = _studio.EffectiveCount;

		for ( var i = 0; i < _studio.Features.Count; i++ )
		{
			var feature = _studio.Features[i];
			var node = new FeatureNode( feature )
			{
				RolledBack = i >= effective,
				IsFirstRolledBack = i == effective,
			};

			_nodes[feature] = node;
			_tree.AddItem( node );
		}

		RebuildRollbackLabel( effective );
		RebuildPartsList();
	}

	private void RebuildRollbackLabel( int effective )
	{
		if ( !_rollbackLabel.IsValid() )
			return;

		var total = _studio.Features.Count;
		var rolledBack = total - effective;

		_rollbackLabel.Text = rolledBack > 0
			? $"Rolled back — {effective} of {total} features active"
			: $"{total} feature{(total == 1 ? "" : "s")}";

		_rollbackLabel.Color = rolledBack > 0 ? Theme.Yellow : Theme.TextControl.WithAlpha( 0.6f );
	}

	/// <summary>
	/// The parts the last rebuild produced. Onshape's list carries a per-part hide/show eye; that
	/// is left out rather than stubbed, because the viewport previews one merged mesh
	/// (PartStudio.ToMesh) and there is nothing per-body to hide yet.
	/// </summary>
	private void RebuildPartsList()
	{
		foreach ( var w in _partWidgets )
			w.Destroy();

		_partWidgets.Clear();

		if ( !_partsRows.IsValid() )
			return;

		if ( _studio.Bodies.Count == 0 )
		{
			AddPartRow( "No parts yet", Theme.TextControl.WithAlpha( 0.45f ) );
			return;
		}

		foreach ( var body in _studio.Bodies )
			AddPartRow( $"{body.Name ?? body.Id} · {body.Mesh?.FaceCount ?? 0} faces", Theme.TextControl );
	}

	private void AddPartRow( string text, Color colour )
	{
		var row = new Widget( _partsRows ) { Layout = Layout.Row() };
		row.Layout.Margin = new Sandbox.UI.Margin( 16, 2 );
		row.Layout.Add( new Editor.Label( text ) { Color = colour } );

		_partsRows.Layout.Add( row );
		_partWidgets.Add( row );
	}

	// --- tree node types --------------------------------------------------------------------

	/// <summary>Root "Default geometry" node with Origin/Top/Front/Right children.</summary>
	private sealed class DefaultGeometryNode : TreeNode<string>
	{
		public DefaultGeometryNode() : base( "Default geometry" ) { }

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			Paint.SetPen( Theme.TextControl.WithAlpha( 0.6f ) );
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

			Paint.SetPen( Theme.TextControl.WithAlpha( 0.6f ) );
			Paint.DrawIcon( item.Rect, _icon, 14, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text );
			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), Value, TextFlag.LeftCenter );
		}
	}

	/// <summary>A feature in the tree — icon, name, and its state: suppressed, errored, or sitting
	/// past the rollback bar.</summary>
	private sealed class FeatureNode : TreeNode<Feature>
	{
		public Feature Feature => Value;

		/// <summary>Past the rollback bar, so this feature did not run at all.</summary>
		public bool RolledBack { get; init; }

		/// <summary>The first feature past the bar — the only one that draws the bar itself.</summary>
		public bool IsFirstRolledBack { get; init; }

		public FeatureNode( Feature feature ) : base( feature ) { }

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			var inactive = RolledBack || Value.Suppressed;

			if ( inactive )
				Paint.SetPen( Theme.TextControl.WithAlpha( 0.35f ) );
			else if ( Value.Error is not null )
				Paint.SetPen( Theme.Red );
			else
				Paint.SetPen( Theme.Blue );

			Paint.DrawIcon( item.Rect, RolledBack ? "history" : "category", 14, TextFlag.LeftCenter );

			Paint.SetPen( inactive ? Theme.TextControl.WithAlpha( 0.5f ) : Theme.Text );

			var label = Value.Name ?? Value.TypeName;

			// A consumer with no region yet is suppressed for a different reason than one somebody
			// suppressed on purpose - it is mid-creation, waiting on a face - and saying
			// "suppressed" about it reads as a state you have to undo rather than one you finish.
			if ( Value is SketchConsumingFeature { RegionSeed: null } && Value.Suppressed )
				label += "  (select a face)";
			else if ( Value.Suppressed )
				label += "  (suppressed)";
			else if ( RolledBack )
				label += "  (rolled back)";

			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), label, TextFlag.LeftCenter );

			// A rule above the first rolled-back feature: this is the rollback bar itself, drawn
			// where Onshape draws it. Only the first one — a rule above every rolled-back feature
			// would read as a list of separators rather than as one bar.
			if ( !IsFirstRolledBack )
				return;

			Paint.SetPen( Theme.Yellow.WithAlpha( 0.5f ) );
			Paint.DrawLine( new Vector2( item.Rect.Left, item.Rect.Top ), new Vector2( item.Rect.Right, item.Rect.Top ) );
		}
	}
}
