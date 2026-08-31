using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Effigy.Skeleton, not Sandbox.Skeleton - the engine has a Skeleton type of its own, and every
// Skeleton named here is the CAD one the rig panel builds and the exporters write out.
using Skeleton = Effigy.Skeleton;

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

[EditorApp( "Effigy", "editor/effigy_icon.png", "Parametric modelling, subdivision, and rig-ready mesh export" )]
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
	private EffigyPartsPanel _partsPanel;
	private EffigyMaterialsPanel _materialsPanel;
	private EffigyRigPanel _rigPanel;
	private Widget _leftPanel;

	/// <summary>The creation-tool strip of square buttons floating over the viewport. Lives on
	/// the viewport rather than in a window toolbar row, so the tools sit on the thing they
	/// act on.</summary>
	private EffigyToolStrip _toolStrip;

	/// <summary>The sketch tool strip - floats in the SAME spot as the feature strip and replaces
	/// it while a sketch is open, the way Onshape's toolbar swaps rather than stacks a second row.
	/// Its tools mean nothing outside sketch mode, and leaving them visible but dead is worse than
	/// hiding them - which is exactly what the previous window-docked ToolBar version did, since
	/// hiding IT never touched the unrelated floating feature strip sitting on the canvas.</summary>
	private EffigySketchStrip _sketchStrip;

	/// <summary>The ADD/REMOVE mode strip, shown only while a feature that HAS a Result is open.
	/// See EffigyResultStrip for why it is on the canvas rather than in the dialog.</summary>
	private EffigyResultStrip _resultStrip;

	private readonly List<EffigySketchToolButton> _sketchTools = new();
	private EffigySketchToolButton _constructionButton;
	private DockWidget _centralDock;
	private StatusBar _statusWidget;
	private Editor.Label _statusInfoLabel;
	private Editor.Label _promptLabel;

	/// <summary>
	/// The open window, for console diagnostics to talk to.
	///
	/// A ConCmd is static and the studio it needs to inspect is not, and there is no other route
	/// from the console to the live document. Only ever read by effigy_dump_tree - nothing in the
	/// tool's own behaviour depends on it, so a stale one after a crash costs a wrong dump and
	/// nothing more.
	/// </summary>
	internal static EffigyWindow Current;

	/// <summary>The live part studio, for effigy_dump_tree to read. Read-only by intent - the
	/// diagnostic prints, it does not touch the document.</summary>
	internal PartStudio DiagnosticStudio => _studio;

	public EffigyWindow()
	{
		Current = this;

		DeleteOnClose = true;
		Size = new Vector2( 1440, 900 );

		if ( AppIcon() is { } icon )
			SetWindowIcon( icon );
		else
			SetWindowIcon( "view_in_ar" );

		_studio = new PartStudio();

		// The engine boolean, in front of the kernel before anything can ask for a cut. Remove was
		// wired end to end and waiting on exactly this one translation; see EffigyMeshBoolean.
		EffigyMeshBoolean.Install();

		BuildMenuBar();
		BuildDocks();
		BuildToolbar();
		BuildStatusBar();

		// Last session's palette and grid choice, now that the viewport exists to receive them.
		// ApplyPalette runs inside this.
		RestoreSettings();

		// A window that has only just opened has nothing to lose, and anything during startup that
		// went through RebuildStudio has already set the flag. Without this, closing an untouched
		// Effigy asks whether to save an empty studio — the fastest way to teach someone to click
		// through the very prompt that exists to save their work.
		MarkClean();

		Show();
	}

	/// <summary>
	/// Green-man / oak-face mark for the window tab. The Tools menu itself only takes a Material
	/// Icon name (see the EditorApp attribute) — a pixmap there would go blank — so this is the
	/// place a custom drawing actually shows.
	/// </summary>
	internal static Pixmap AppIcon()
	{
		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( root ) )
			return null;

		foreach ( var rel in new[]
		{
			Path.Combine( "Editor", "EffigyEditor", "effigy_icon.png" ),
			Path.Combine( "Assets", "editor", "effigy_icon.png" ),
		} )
		{
			var path = Path.Combine( root, rel );
			if ( File.Exists( path ) )
				return Pixmap.FromFile( path );
		}

		return null;
	}

	// --- menu bar ---------------------------------------------------------------------------

	private void BuildMenuBar()
	{
		var file = MenuBar.FindOrCreateMenu( "File" );
		file.Clear();
		file.AddOption( "New Studio", "common/new.png", NewStudio );
		file.AddOption( "Open...", "folder_open", Open );
		file.AddSeparator();
		file.AddOption( "Save", "common/save.png", Save, "editor.save" );
		file.AddOption( "Save As...", "save_alt", SaveAs );
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
		edit.AddOption( "Settings...", "settings", OpenSettings );

		var view = MenuBar.FindOrCreateMenu( "View" );
		view.Clear();
		view.AddOption( "Frame Camera", "center_focus_strong", () => _viewport?.FrameCamera() );
		view.AddOption( "Normal to Sketch Plane\tN", "straighten", () => _viewport?.ViewNormalToSketchPlane() );
		view.AddOption( "Shade Material Slots", "palette", ToggleMaterialShading );
		view.AddOption( "Show Sketch Constraints", "rule", ToggleConstraintMarks );

		// "restart_alt" is a Material SYMBOLS name and s&box ships classic Material Icons, so it
		// was drawing nothing at all - see EffigyIcons for why that whole class of name is unsafe.
		view.AddOption( "Reset Origin", "settings_backup_restore", () => _viewport?.ResetOrigin() );

		view.AddSeparator();
		var featuresPanel = view.AddOption( "Feature Tree", "account_tree" );
		featuresPanel.Checkable = true;
		featuresPanel.Checked = true;
		featuresPanel.Toggled += visible => DockManager.SetDockState( "Features", visible );

		var rigPanel = view.AddOption( "Rig", "polyline" );
		rigPanel.Checkable = true;
		rigPanel.Checked = false;
		rigPanel.Toggled += visible => DockManager.SetDockState( "Rig", visible );

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

		// The palette list used to sit here as four checkable options. It lives in Edit > Settings
		// now, as a dropdown — one home per setting, because two controls reading the same value
		// is how one of them ends up showing the wrong tick.
	}

	// --- toolbar (square icon buttons floating over the viewport) -------------------------

	private void BuildToolbar()
	{
		// The creation tools float ON the 3D view at its top-left, one square per button, rather
		// than in a window toolbar row - the viewport is the thing the tools act on. They are
		// parented to the canvas and positioned by the viewport, so the scene fills the whole
		// widget and nothing eats a band off the top of it.
		_toolStrip = new EffigyToolStrip( _viewport.Canvas );
		_sketchStrip = new EffigySketchStrip( _viewport.Canvas );

		// Under the tool strip rather than in the dialog, because the question it answers - "is
		// this about to cut?" - is asked while looking at the MODEL, not at the parameter list.
		_resultStrip = new EffigyResultStrip( _viewport.Canvas ) { Changed = OnResultStripChanged };

		_viewport.CompleteLayout( _toolStrip, _sketchStrip, _resultStrip );

		RefreshToolStrip( force: true );

		BuildSketchToolbar();

		// Belt and braces: the two strips share one spot and exactly one may show. CompleteLayout
		// hides the sketch strip before any button exists; restate it once both strips are fully
		// built so a restored window state can never leak both on screen at once.
		_toolStrip.Visible = true;
		_sketchStrip.Visible = false;
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
		AddSketchTool( EffigyIcon.SelectTool, "Select", "Select - drag a point to move it", SketchToolKind.Select );
		_sketchStrip.AddGap();

		AddSketchTool( EffigyIcon.LineTool, "Line", "Line - click start, click end; keeps chaining until Escape", SketchToolKind.Line );

		// The four families that have more than one way to place them. Each is ONE button with the
		// alternatives behind its chevron, which is how Onshape's sketch row is arranged.
		AddSketchGroup(
			new SketchToolVariant( EffigyIcon.RectangleTool, "Corner rectangle",
				"Corner rectangle - click two opposite corners", SketchToolKind.Rectangle ),
			new SketchToolVariant( EffigyIcon.RectangleCentreTool, "Centre point rectangle",
				"Centre rectangle - click the centre, then a corner", SketchToolKind.RectangleCentre ) );

		AddSketchGroup(
			new SketchToolVariant( EffigyIcon.CircleTool, "Centre circle",
				"Centre circle - click the centre, then a point on the rim", SketchToolKind.Circle ),
			new SketchToolVariant( EffigyIcon.CircleThreePointTool, "3 point circle",
				"3-point circle - click three points on the rim", SketchToolKind.CircleThreePoint ) );

		AddSketchGroup(
			new SketchToolVariant( EffigyIcon.ArcTool, "Centre arc",
				"Centre arc - click the centre, the start, then the end direction", SketchToolKind.Arc ),
			new SketchToolVariant( EffigyIcon.ArcThreePointTool, "3 point arc",
				"3-point arc - click start, end, then a point it passes through", SketchToolKind.ArcThreePoint ) );

		AddSketchGroup(
			new SketchToolVariant( EffigyIcon.PolygonTool, "Inscribed polygon",
				"Inscribed polygon - click the centre, then a corner", SketchToolKind.Polygon ),
			new SketchToolVariant( EffigyIcon.PolygonCircumscribedTool, "Circumscribed polygon",
				"Circumscribed polygon - click the centre, then an edge midpoint", SketchToolKind.PolygonCircumscribed ) );

		AddSketchTool( EffigyIcon.SlotTool, "Slot", "Slot - click both ends of the centre line, then the width", SketchToolKind.Slot );
		AddSketchTool( EffigyIcon.PointTool, "Point", "Point - click to place", SketchToolKind.Point );

		_sketchStrip.AddGap();

		// Construction geometry is a modifier on whatever tool is active, not a tool of its own -
		// same as Onshape's toggle. SketchCurve.Construction and ProfileFinder's handling of it
		// were already in the kernel with nothing in the UI able to set them.
		_constructionButton = _sketchStrip.AddButton( EffigyIcon.ConstructionTool,
			"Construction geometry - reference lines that never become part of a profile",
			checkable: true, clicked: null );
		_constructionButton.Clicked = () => _viewport.ConstructionMode = _constructionButton.Checked;

		_sketchStrip.AddGap();

		var inspector = _sketchStrip.AddButton( EffigyIcon.ProfileInspectorTool,
			"Profile Inspector - shade closed regions and highlight loose ends",
			checkable: true, clicked: null );
		inspector.Checked = true;
		inspector.Clicked = () => _viewport.ProfileInspector = inspector.Checked;

		var finish = _sketchStrip.AddButton( EffigyIcon.FinishSketchTool,
			"Finish Sketch - leave sketch mode and go back to the feature tree",
			checkable: false, clicked: FinishSketch );

		finish.IconColor = EffigyToolStrip.ConfirmColor;
	}

	/// <summary>A tool with only one way to place it: one variant, so no chevron appears.</summary>
	private void AddSketchTool( EffigyIcon icon, string label, string tip, SketchToolKind kind ) =>
		AddSketchGroup( new SketchToolVariant( icon, label, tip, kind ) );

	/// <summary>
	/// One button for a family of tools. The first variant is what it shows to begin with; the
	/// rest sit behind its chevron and take its place once picked.
	/// </summary>
	private void AddSketchGroup( params SketchToolVariant[] variants )
	{
		var button = _sketchStrip.AddButton( variants[0].Icon, variants[0].Tip, checkable: true, clicked: null );

		button.SetVariants( variants );
		button.Checked = variants[0].Kind == SketchToolKind.Select;

		button.VariantChosen = variant =>
		{
			_viewport.SetSketchTool( variant.Kind );
			UpdateSketchToolChecks( variant.Kind );
		};

		_sketchTools.Add( button );
	}

	/// <summary>Only one tool can be active, so the rest have to visibly let go. The floating strip
	/// has no radio-group concept of its own, so the exclusivity is enforced here.</summary>
	private void UpdateSketchToolChecks( SketchToolKind active )
	{
		foreach ( var button in _sketchTools )
		{
			var index = -1;

			for ( var i = 0; i < button.Variants.Count; i++ )
			{
				if ( button.Variants[i].Kind == active )
					index = i;
			}

			button.Checked = index >= 0;

			// A tool armed from somewhere else - a keyboard shortcut, or Escape dropping back to
			// Select - has to appear on the face of its button, or the strip would show one thing
			// while the viewport did another.
			if ( index >= 0 )
				button.ShowVariant( index );
		}
	}

	// --- sketch mode -------------------------------------------------------------------------

	/// <summary>
	/// Enter sketch mode on a Sketch feature: show the sketch toolbar, point the camera straight
	/// at the plane, and start on the Line tool.
	///
	/// The rebuild is needed for SketchFeature.Plane — the Sketch object's actual plane is only
	/// assigned when the feature executes — but the strip swap is UI and must happen first so the
	/// toolbar change is instant.  BeginSketch uses the rebuilt plane, so it comes after.
	/// </summary>
	private void EnterSketch( SketchFeature feature )
	{
		// The sketch strip REPLACES the feature strip - same spot, one visible at a time - rather
		// than the two of them being independent widget systems that both stayed on screen.  Do
		// this BEFORE the rebuild so the toolbar swaps instantly instead of waiting for the
		// (potentially slow) PartStudio rebuild to finish.
		_toolStrip.Visible = false;
		_sketchStrip.Visible = true;

		// Sketching and the bone tool both drive left-clicks in the viewport; only one may own
		// them. The bone tool refuses to arm on top of an open sketch (see SetBoneToolActive), so
		// the only direction this needs covering is the other one - entering a sketch while the
		// bone tool happened to be armed.
		_rigPanel?.CancelBoneTool();

		RebuildStudio();

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

		// Before the strip comes back, so the tools a finished sketch unlocks are already on it
		// rather than appearing a beat later.
		RefreshToolStrip();

		_sketchStrip.Visible = false;
		_toolStrip.Visible = true;

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

		// The dropdown and the strip are two views of one ChoiceParam, so an edit through either
		// has to refresh the other or they disagree about what is armed - which is the exact
		// confusion the strip exists to end.
		_resultStrip?.Bind( _dialog?.Feature, SketchHostBodyId );

		RebuildStudio();
	}

	/// <summary>
	/// A click on the ADD/REMOVE strip. The parameter is already set by the time this runs; what
	/// is left is everything a dropdown change would have done.
	///
	/// The dialog rebuild is not optional. Result decides which parameters a feature even declares
	/// in some cases, and the dropdown four rows down still shows the old value until it is redrawn
	/// - two controls disagreeing about the same value being precisely the failure this is fixing.
	/// </summary>
	private void OnResultStripChanged()
	{
		OnFeatureEdited();

		_dialog?.Rebuild();
	}

	/// <summary>
	/// Which body a sketch was drawn on, or null for one on a global plane. This is what Auto
	/// reads, so it is what the strip's Auto hint has to read too.
	///
	/// Straight off SketchFeature.Face rather than through the kernel's own resolution, because
	/// that needs a FeatureContext which only exists mid-rebuild - see EffigyResultStrip.ResolveAuto.
	/// </summary>
	private string SketchHostBodyId( string sketchId ) =>
		_studio.Features.OfType<SketchFeature>().FirstOrDefault( f => f.Id == sketchId )?.Face?.BodyId;

	/// <summary>The left half of the status bar — what the active tool wants next.</summary>
	private void SetPrompt( string prompt )
	{
		if ( _promptLabel.IsValid() )
			_promptLabel.Text = prompt;
	}

	/// <summary>A square strip button that appends one feature to the history. The factory runs
	/// per click rather than the feature being built up front, so each press makes a new one.</summary>
	// --- which creation tools are on the strip -------------------------------------------------

	/// <summary>
	/// Which feature a strip button makes. An ENUM RATHER THAN A Func&lt;Feature&gt;.
	///
	/// The table below is static, and static state survives a hotload while the assembly under it
	/// does not. A stored lambda therefore comes back pointing into the old assembly, which the
	/// hotloader cannot substitute — clicking a button threw "Unable to find matching substitution
	/// for a lambda method" and every tool was dead until the editor restarted. An enum value is an
	/// int and migrates without any of that; the switch that turns it into a feature is ordinary
	/// code, recompiled with everything else. Same reason no System.Type is held here either.
	/// </summary>
	private enum ToolKind
	{
		Sketch, Primitive, Extrude, Revolve, Bevel, Shell, Subdivide,
		Mirror, LinearPattern, CircularPattern, Transform, UVProject, FaceMaterial,
	}

	/// <summary>Build one, and apply the variant chosen from its dropdown where it has one.</summary>
	private static Feature NewFeature( ToolKind kind, int choice ) => kind switch
	{
		ToolKind.Sketch => new SketchFeature(),
		ToolKind.Primitive => NewPrimitive( choice ),
		ToolKind.Extrude => new ExtrudeFeature(),
		ToolKind.Revolve => new RevolveFeature(),
		ToolKind.Bevel => new BevelFeature(),
		ToolKind.Shell => new ShellFeature(),
		ToolKind.Subdivide => new SubdivideFeature(),
		ToolKind.Mirror => new MirrorFeature(),
		ToolKind.LinearPattern => new LinearPatternFeature(),
		ToolKind.CircularPattern => new CircularPatternFeature(),
		ToolKind.Transform => new TransformFeature(),
		ToolKind.UVProject => new UVProjectFeature(),
		ToolKind.FaceMaterial => new FaceMaterialFeature(),
		_ => throw new ArgumentOutOfRangeException( nameof( kind ), kind, "no feature for this tool" )
	};

	private static PrimitiveFeature NewPrimitive( int shape )
	{
		var feature = new PrimitiveFeature();

		if ( shape >= 0 )
			feature.Shape.Index = shape;

		return feature;
	}

	/// <summary>One button on the feature strip. Held as data rather than written straight into the
	/// layout so the strip can be rebuilt with a subset of them.</summary>
	private sealed class CreateTool
	{
		public EffigyIcon Icon;
		public string Tip;
		public ToolKind Kind;

		/// <summary>Start a new group before this one — a wider gap, the old separator.</summary>
		public bool GapBefore;

		/// <summary>Text beside the glyph, for the one button wide enough to carry it.</summary>
		public string Label;

		public float Width = EffigyToolStrip.ButtonSize;

		/// <summary>Shown from the very start. Everything else waits for a sketch — see
		/// <see cref="RefreshToolStrip"/>.</summary>
		public bool Starter;

		/// <summary>
		/// Variants behind this button, or null for one that just does its thing.
		///
		/// Where they exist the button opens a menu instead of adding anything, and the index
		/// chosen goes to <see cref="NewFeature"/>. Primitive is the case this was built for: six
		/// shapes that are one feature with one parameter set differently, which is a list rather
		/// than six buttons.
		/// </summary>
		public string[] Choices;
	}

	/// <summary>The shapes behind the Primitive button. Taken from PrimitiveFeature.Shape rather
	/// than written out again, so the menu cannot drift from the parameter it sets — a menu naming
	/// a shape the feature has never heard of would set an index that means something else.
	/// </summary>
	private static string[] PrimitiveShapes => new PrimitiveFeature().Shape.Options;

	/// <summary>
	/// The strip's tools, BUILT FRESH ON EVERY READ rather than held in a static field.
	///
	/// Nothing here is expensive — it runs once per toolbar refresh, which happens when a sketch is
	/// finished — and a property cannot carry objects from a dead assembly across a hotload the way
	/// a static field does. Between this and ToolKind replacing the factory delegates, there is no
	/// state left here for a reload to invalidate.
	/// </summary>
	private static CreateTool[] CreateTools => new CreateTool[]
	{
		new() { Icon = EffigyIcon.Sketch, Tip = "Add a Sketch feature — draw lines/arcs on a plane",
			Kind = ToolKind.Sketch, Label = "Sketch", Width = 132f, Starter = true },

		new() { Icon = EffigyIcon.Primitive, Tip = "Add a Primitive — pick a shape",
			Kind = ToolKind.Primitive, GapBefore = true, Starter = true,
			Choices = PrimitiveShapes },

		new() { Icon = EffigyIcon.Extrude, Tip = "Add an Extrude — pull a sketch profile into a solid",
			Kind = ToolKind.Extrude },
		new() { Icon = EffigyIcon.Revolve, Tip = "Add a Revolve — sweep a sketch profile around an axis",
			Kind = ToolKind.Revolve },

		new() { Icon = EffigyIcon.Bevel, Tip = "Add a Bevel — chamfer sharp edges",
			Kind = ToolKind.Bevel, GapBefore = true },
		new() { Icon = EffigyIcon.Shell, Tip = "Add a Shell — hollow to a wall thickness",
			Kind = ToolKind.Shell },
		new() { Icon = EffigyIcon.Subdivide, Tip = "Add a Subdivide — Catmull-Clark subdivision",
			Kind = ToolKind.Subdivide },

		new() { Icon = EffigyIcon.Mirror, Tip = "Add a Mirror — reflect bodies across a plane",
			Kind = ToolKind.Mirror, GapBefore = true },
		new() { Icon = EffigyIcon.LinearPattern, Tip = "Add a Linear Pattern — copy bodies along a direction",
			Kind = ToolKind.LinearPattern },
		new() { Icon = EffigyIcon.CircularPattern, Tip = "Add a Circular Pattern — copy bodies around an axis",
			Kind = ToolKind.CircularPattern },

		new() { Icon = EffigyIcon.Transform, Tip = "Add a Transform — move, rotate or scale bodies",
			Kind = ToolKind.Transform, GapBefore = true },
		new() { Icon = EffigyIcon.UVProject, Tip = "Add a UV Project — re-project UVs (box or planar)",
			Kind = ToolKind.UVProject },
		new() { Icon = EffigyIcon.FaceMaterial, Tip = "Add a Face Material — put picked faces on a material slot",
			Kind = ToolKind.FaceMaterial },
	};

	/// <summary>Whether the strip is currently showing everything, so a refresh that changes
	/// nothing costs nothing.</summary>
	private bool _fullToolsShown;

	/// <summary>
	/// A sketch with something drawn in it exists, so the rest of the tools have something to bite
	/// on.
	///
	/// Curves rather than merely the feature: clicking Sketch adds the feature to the tree straight
	/// away, before a plane is even chosen, so its presence alone would unlock the strip while
	/// there was still nothing to extrude.
	/// </summary>
	private bool HasConfirmedSketch() =>
		_studio is not null
		&& _studio.Features.OfType<SketchFeature>().Any( f => f.Sketch is { Curves.Count: > 0 } );

	/// <summary>
	/// Show the starter tools on their own until a sketch has been drawn, then the whole strip.
	///
	/// TWELVE BUTTONS ON AN EMPTY STUDIO ARE TWELVE WAYS TO GET AN ERROR. Extrude, Revolve, Bevel,
	/// Shell and the rest all need geometry to act on, and adding one before there is any produces
	/// a feature that goes straight to red — correct, and useless as a first impression. Sketch and
	/// Primitive are the only two that can start a part, so at the start they are the only two
	/// offered.
	/// </summary>
	private void RefreshToolStrip( bool force = false )
	{
		if ( _toolStrip is null )
			return;

		var full = HasConfirmedSketch();

		if ( !force && full == _fullToolsShown )
			return;

		_fullToolsShown = full;

		_toolStrip.Clear();

		var any = false;

		foreach ( var tool in CreateTools )
		{
			if ( !full && !tool.Starter )
				continue;

			// Never a leading gap: the group break belongs BETWEEN groups, and the first tool
			// through here may not be the one the gap was authored against.
			if ( tool.GapBefore && any )
				_toolStrip.AddGap();

			// Only the KIND is captured, never the table entry. The closure is rebuilt on every
			// refresh so it always belongs to the current assembly, and an enum carries across a
			// hotload where a delegate does not.
			var kind = tool.Kind;
			var choices = tool.Choices;

			var button = _toolStrip.AddButton( tool.Icon, tool.Tip,
				choices is null
					? () => AddFeature( NewFeature( kind, -1 ) )
					: () => OpenToolChoices( kind, choices ),
				tool.Width );

			button.HasMenu = choices is not null;

			if ( tool.Label is not null )
				button.Label = tool.Label;

			any = true;
		}
	}

	/// <summary>The variant list for a tool that has one — at the cursor, so it opens where the
	/// click was rather than somewhere the mouse has to travel to.</summary>
	private void OpenToolChoices( ToolKind kind, string[] choices )
	{
		var menu = new Menu( _toolStrip );

		for ( var i = 0; i < choices.Length; i++ )
		{
			var choice = i;

			menu.AddOption( choices[choice], null, () => AddFeature( NewFeature( kind, choice ) ) );
		}

		menu.OpenAtCursor();
	}

	/// <summary>
	/// Append a feature and leave it selected with its dialog open — Onshape's behaviour, and the
	/// reason the buttons feel like they did something. A freshly added Extrude with no sketch
	/// above it WILL show an error; that is correct, and the parameter panel is where you fix it.
	/// </summary>
	private void AddFeature( Feature feature )
	{
		RecordUndo();

		// A new feature goes AT THE ROLLBACK BAR, not at the end of the tree - same as Onshape.
		// Appending would drop it below the bar, where it does not get evaluated: you would add an
		// Extrude while rolled back, watch nothing happen, and have no way to tell why. The bar
		// moves down past it so the thing you just added is the last one running.
		if ( _studio.RollbackIndex < _studio.Features.Count )
		{
			_studio.Insert( _studio.RollbackIndex, feature );
			_studio.RollbackIndex++;
		}
		else
		{
			_studio.Add( feature );
		}

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
			VisibilityToggled = OnTreeVisibilityToggled,
			CommandRequested = OnFeatureCommand,
			RenameCommitted = OnFeatureRenamed,
		};

		_dialog = new EffigyFeatureDialog( this, _viewport )
		{
			Edited = OnFeatureEdited,
			Renamed = () => _featureTree?.Rebuild(),
			Accepted = OnDialogAccepted,
			Cancelled = OnDialogCancelled,
			SketchRequested = EnterSketch,
			SketchNameLookup = id => _studio.Features.OfType<SketchFeature>().FirstOrDefault( f => f.Id == id )?.Name,
			PickableBodiesLookup = () => _studio.Bodies,
			BodyNameLookup = id => _studio.Bodies.FirstOrDefault( b => b.Id == id )?.Name,
			OpenedForFeature = f =>
			{
				UpdatePickTargets( f );
				_resultStrip?.Bind( f, SketchHostBodyId );
			},
			MaterialLookup = SlotMaterial,
			MaterialChanged = SetSlotMaterial,
		};

		_partsPanel = new EffigyPartsPanel( this, _studio )
		{
			VisibilityToggled = OnPartVisibilityToggled,
		};

		_materialsPanel = new EffigyMaterialsPanel( this, _studio )
		{
			MaterialChanged = SetSlotMaterial,
		};

		_rigPanel = new EffigyRigPanel( this, _studio, _viewport );

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

		// Parts BELOW the feature tree, the way Onshape stacks them: the recipe on top, what it
		// actually built underneath.
		_leftPanel.Layout.Add( _partsPanel );

		_viewport.SketchEdited = OnSketchEdited;
		_viewport.FaceContextMenuRequested = OpenFaceMaterialMenu;
		_viewport.SketchConstraintMenuRequested = OpenSketchConstraintMenu;

		// Fired BEFORE the viewport changes a sketch, which is the only moment a useful "before"
		// exists to snapshot.
		_viewport.SketchEditing = RecordUndo;
		_viewport.SketchPromptChanged = SetPrompt;

		// Same "before" moment, for the rig: a bone placed, deleted, renamed, or mirrored.
		_rigPanel.RigChanging = RecordUndo;

		_centralDock = DockManager.SetCentralWidget( _viewport );

		DockManager.RegisterDock( new() { Title = "Features", Icon = "account_tree", Area = DockArea.Left, CreateAction = () => _leftPanel } );
		DockManager.RegisterDock( new() { Title = "Rig", Icon = "account_tree", Area = DockArea.Right, CreateAction = () => _rigPanel } );

		// Right, tabbed behind the Rig, because both are things you do to a part that is already
		// modelled and neither is worth permanent screen room while you are still modelling it.
		DockManager.RegisterDock( new() { Title = "Materials", Icon = "palette", Area = DockArea.Right, CreateAction = () => _materialsPanel } );

		// Bumped from Effigy1: the Parameters dock is gone and the tree moved into a shared column
		// with the dialog. A restored Effigy1 layout would reinstate the old two-dock arrangement
		// and BuildDefaultLayout would never run again.
		// Bumped from Effigy2: restored Effigy2 layouts came back degenerate - the Features dock a
		// sliver and stray chrome floating over the viewport - so anyone with one saved never got
		// a usable window. A fresh cookie forces the known-good default layout.
		// Bumped from Effigy3: the Materials dock is new, and a restored Effigy3 layout knows
		// nothing about it - the panel would exist, be wired up, and never appear on screen.
		StateCookie = "Effigy4";
	}

	/// <summary>Hide or show the bodies a feature made, from the Parts list's eye.
	///
	/// MarkDirty is the part that is easy to miss: features before the first dirty one are
	/// restored from the rebuild cache and never re-run, and body visibility is applied while a
	/// feature runs - so without this the eye would flip and nothing on screen would change.</summary>
	private void OnPartVisibilityToggled( string featureId )
	{
		var feature = _studio.Features.FirstOrDefault( f => f.Id == featureId );

		if ( feature is null )
			return;

		feature.Visible = !feature.Visible;
		_studio.MarkDirty( feature );
		RebuildStudio();
	}

	private void OnTreeVisibilityToggled( string key, bool visible )
	{
		if ( _viewport is null )
			return;

		switch ( key )
		{
			case "origin": _viewport.OriginVisible = visible; break;
			case "top": _viewport.TopPlaneVisible = visible; break;
			case "front": _viewport.FrontPlaneVisible = visible; break;
			case "right": _viewport.RightPlaneVisible = visible; break;
			default:
				var sketch = _studio.Features.OfType<SketchFeature>()
					.FirstOrDefault( x => $"sketch:{x.Id}" == key );
				_viewport.SetSketchVisibility( sketch?.Sketch, visible );
				break;
		}
	}

	protected override void BuildDefaultLayout()
	{
		var featuresDock = DockManager.OpenDock( "Features", DockArea.Left, _centralDock );
		DockManager.SetSplitterProportions( featuresDock, 0.26f, 0.74f );

		DockManager.RaiseDock( "Features" );
		DockManager.OpenDock( "Rig", DockArea.Right, _centralDock );
		DockManager.OpenDock( "Materials", DockArea.Right, _centralDock );

		// The Rig in front of the Materials on a fresh window: rigging is the step you reach for
		// first, and the two share the tab.
		DockManager.RaiseDock( "Rig" );
	}

	// --- status bar -------------------------------------------------------------------------

	private void BuildStatusBar()
	{
		_statusWidget = new StatusBar( this );
		_statusWidget.AddWidgetLeft( new Editor.Label( "Effigy" ) { FixedWidth = 52 }, 0 );

		_promptLabel = new Editor.Label( "" );
		_statusWidget.AddWidgetLeft( _promptLabel, 1 );

		_statusInfoLabel = new Editor.Label( "" );
		_statusWidget.AddWidgetRight( _statusInfoLabel, 0 );

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

		// Same reasoning as FinishSketch above, for the bone tool: opening a dialog that may set
		// SketchPickMode (Extrude/Revolve) or arm a body/plane picker of its own would otherwise
		// collide with it exactly the way an open sketch would. Cheap to cancel outright — all
		// that's lost is an empty pending-chain state, not a feature mid-edit.
		if ( _viewport.BoneToolActive && feature != _dialog?.Feature )
			_rigPanel?.CancelBoneTool();

		if ( _dialog is null || (_dialog.IsOpen && _dialog.Feature == feature) )
			return;

		_dialog.Open( feature, isNew: false );
	}

	private void OnDialogAccepted( Feature feature )
	{
		_resultStrip?.Bind( null, SketchHostBodyId );

		if ( _viewport.IsSketching )
			FinishSketch();

		RestoreRollbackAfterEdit();
		RebuildStudio();
	}

	/// <summary>Cancel on a feature that the toolbar had just created removes it outright - the
	/// feature only ever existed to be configured, so an abandoned dialog should leave the tree as
	/// it was. Cancelling an edit has already had its parameters restored by the dialog.</summary>
	private void OnDialogCancelled( Feature feature, bool wasNew )
	{
		_resultStrip?.Bind( null, SketchHostBodyId );

		if ( wasNew )
			_studio.Remove( feature );

		if ( _viewport.IsSketching )
			FinishSketch();

		RestoreRollbackAfterEdit();
		RebuildStudio();
	}

	private void OnStudioChanged()
	{
		RebuildStudio();
	}

	/// <summary>
	/// Where the studio lives on disk, and whether it has been changed since it got there.
	///
	/// EVERY EDIT GOES THROUGH RebuildStudio, which is why the dirty flag is set there rather than
	/// at each of the thirty-odd call sites. Marking at the funnel cannot be forgotten by whoever
	/// adds the thirty-first; marking at the sites is a promise nobody keeps for long. Load, save
	/// and new all rebuild too, so each of those clears the flag afterwards.
	/// </summary>
	private string _documentPath;

	private bool _dirty;

	private void MarkClean()
	{
		_dirty = false;
		UpdateTitle();
	}

	private void UpdateTitle() =>
		Title = $"Effigy - {(_documentPath is null ? "untitled" : Path.GetFileName( _documentPath ))}{(_dirty ? "*" : "")}";

	private void RebuildStudio()
	{
		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}

		var report = _studio.Rebuild();
		_featureTree?.Rebuild();
		_partsPanel?.Refresh();
		_materialsPanel?.Refresh();
		_rigPanel?.RefreshBodyNames();

		// Covers every other way the answer can change — undo back past the first sketch, deleting
		// it, opening a saved studio. Cheap: it returns immediately unless the strip is actually
		// showing the wrong set.
		RefreshToolStrip();

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

		// The preview model is one flat grey, so a material slot is invisible in it. The viewport
		// tints the faces that carry one instead, and needs the bodies to do it - the mesh handed to
		// EffigyPreview above has already been flattened into one and lost which body it came from.
		_viewport?.SetDisplayBodies( _studio.Bodies );

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

	/// <summary>Turn the material-slot tint on and off. On by default: a slot you cannot see is a
	/// slot you cannot check, and slot 0 - which is every face until someone says otherwise - is
	/// not tinted at all, so a model with no materials assigned looks exactly as it did.</summary>
	private void ToggleMaterialShading()
	{
		if ( _viewport is null )
			return;

		_viewport.ShadeMaterialSlots = !_viewport.ShadeMaterialSlots;
	}

	/// <summary>Whether the rules holding a sketch together are drawn on it. On by default — a
	/// constraint you cannot see is a constraint you fight, and until now there were none to see
	/// because there was no way to add one.</summary>
	private void ToggleConstraintMarks()
	{
		if ( _viewport is null )
			return;

		_viewport.ShowConstraintMarks = !_viewport.ShowConstraintMarks;
	}

	/// <summary>Rebuild both sketch lists against the feature a dialog is open on. Called by the
	/// dialog the moment it opens, because the pick list and the auto-arm decision are only
	/// correct relative to THAT feature.</summary>
	private void UpdatePickTargets( Feature editing )
	{
		if ( _viewport is null )
			return;

		var sketchFeatures = _studio.Features.OfType<SketchFeature>().ToList();

		_viewport.SetDisplaySketches( sketchFeatures.Select( f => f.Sketch ) );
		UpdateSketchVisibility( sketchFeatures, editing );

		var cutoff = editing is null ? int.MaxValue : _studio.Features.IndexOf( editing );

		if ( cutoff < 0 )
			cutoff = int.MaxValue;

		_viewport.SetPickableSketches( _studio.Features.Take( cutoff )
			.OfType<SketchFeature>()
			.Select( f => new EffigyViewport.PickableSketch( f.Id, f.Name ?? f.TypeName, f.Sketch ) ) );
	}

	/// <summary>
	/// Hide the sketches that have already been turned into geometry, keeping the eye in the
	/// feature tree authoritative wherever it has been clicked.
	///
	/// The one sketch that is always shown regardless is the one the open dialog is building
	/// from: you cannot pick a region of a sketch that is not on screen, and while a feature is
	/// being edited its input is the thing you are looking at.
	/// </summary>
	private void UpdateSketchVisibility( List<SketchFeature> sketchFeatures, Feature editing )
	{
		var editingId = editing is SketchConsumingFeature consumer
			? _studio.ResolveSketchFeatureId( consumer )
			: null;

		foreach ( var feature in sketchFeatures )
		{
			var visible = _featureTree?.IsVisible( $"sketch:{feature.Id}" ) ?? true;

			_viewport.SetSketchVisibility( feature.Sketch, visible || feature.Id == editingId );
		}
	}

	private void NewStudio() => ConfirmDiscard( () =>
	{
		RecordUndo();
		_studio = new PartStudio();
		_featureTree?.SetStudio( _studio );
		_partsPanel?.SetStudio( _studio );
		_materialsPanel?.SetStudio( _studio );
		_rigPanel?.SetStudio( _studio );
		_dialog?.Close();
		RebuildStudio();

		_documentPath = null;
		MarkClean();
	} );

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

	/// <summary>
	/// Where the feature tree's context menu ends up. The panel raises intent; everything that
	/// needs the studio, the dialog or the undo stack happens here.
	/// </summary>
	private void OnFeatureCommand( Feature feature, EffigyFeatureCommand command )
	{
		if ( feature is null )
			return;

		var index = _studio.Features.IndexOf( feature );

		switch ( command )
		{
			case EffigyFeatureCommand.Edit:
				EditFeature( feature );
				break;

			case EffigyFeatureCommand.Rename:
				_featureTree?.BeginRename( feature );
				break;

			case EffigyFeatureCommand.ToggleSuppress:
				RecordUndo();
				feature.Suppressed = !feature.Suppressed;
				_studio.MarkDirty( feature );
				RebuildStudio();
				break;

			case EffigyFeatureCommand.Delete:
				RecordUndo();

				if ( _dialog?.Feature == feature )
				{
					_dialog.Close();
					RestoreRollbackAfterEdit();
				}

				_studio.Remove( feature );
				RebuildStudio();
				break;

			case EffigyFeatureCommand.MoveUp when index > 0:
				RecordUndo();
				_studio.Move( index, index - 1 );
				RebuildStudio();
				break;

			case EffigyFeatureCommand.MoveDown when index >= 0 && index < _studio.Features.Count - 1:
				RecordUndo();
				_studio.Move( index, index + 1 );
				RebuildStudio();
				break;

			// An explicit move of the bar STICKS. Forgetting the pre-edit position is the point:
			// otherwise closing a dialog that happened to be open would put the bar back and undo
			// the move the user just made by hand.
			case EffigyFeatureCommand.RollbackTo when index >= 0:
				RecordUndo();
				_rollbackBeforeEdit = null;
				SetRollback( index );
				break;

			case EffigyFeatureCommand.RollForward:
				RecordUndo();
				_rollbackBeforeEdit = null;
				SetRollback( int.MaxValue );
				break;
		}
	}

	private void OnFeatureRenamed( Feature feature, string name )
	{
		if ( feature is null )
			return;

		RecordUndo();

		// Blank means "no name of your own", which is what a feature starts with - the tree falls
		// back to the type name. Storing "" instead would print an empty row.
		feature.Name = string.IsNullOrWhiteSpace( name ) ? null : name.Trim();

		_featureTree?.Rebuild();
		_partsPanel?.Refresh();

		if ( _dialog?.Feature == feature )
			_dialog.Open( feature, isNew: false );
	}

	/// <summary>Move the rollback bar and rebuild. RollbackIndex is the index of the first feature
	/// NOT evaluated, so int.MaxValue means "everything runs".</summary>
	private void SetRollback( int index )
	{
		_studio.RollbackIndex = index;
		RebuildStudio();
	}

	/// <summary>
	/// Onshape's edit: roll the model back to how it looked WHEN THIS FEATURE RAN, and open its
	/// parameters. Editing an extrude with six features stacked on top of it is otherwise done
	/// blind - you cannot see the thing you are changing.
	///
	/// The previous bar position is remembered and put back when the dialog closes, so an edit
	/// does not silently leave half the model switched off. An explicit "Roll back to before
	/// this" from the menu is the one that sticks.
	/// </summary>
	private void EditFeature( Feature feature )
	{
		var index = _studio.Features.IndexOf( feature );

		if ( index < 0 )
			return;

		_rollbackBeforeEdit ??= _studio.RollbackIndex;
		_studio.RollbackIndex = index + 1;

		RebuildStudio();

		_featureTree?.Select( feature );
		_dialog?.Open( feature, isNew: false );
	}

	/// <summary>Where the rollback bar was before an Edit temporarily moved it. Null when no edit
	/// has moved it.</summary>
	private int? _rollbackBeforeEdit;

	/// <summary>Put the bar back after an edit finishes, whichever way it finished.</summary>
	private void RestoreRollbackAfterEdit()
	{
		if ( _rollbackBeforeEdit is not { } previous )
			return;

		_rollbackBeforeEdit = null;
		_studio.RollbackIndex = previous;
	}

	private void ToggleSuppressFeature()
	{
		if ( _featureTree?.SelectedFeature is { } feature )
		{
			RecordUndo();
			feature.Suppressed = !feature.Suppressed;

			// Without this the rebuild restores everything above the first dirty feature from the
			// cache, so the feature you just suppressed is re-used exactly as it was and nothing
			// on screen changes.
			_studio.MarkDirty( feature );
			RebuildStudio();
		}
	}

	// --- export / compile (reusing EffigyTool's proven logic) -------------------------------

	[Shortcut( "editor.save", "CTRL+S", ShortcutType.Window )]
	private void Save()
	{
		// A studio that has never been saved has nowhere to go, so Save becomes Save As the first
		// time. Silently doing nothing here is the shape of the bug the rig tool had.
		if ( _documentPath is null )
		{
			SaveAs();
			return;
		}

		WriteDocument( _documentPath );
	}

	private void SaveAs()
	{
		var fd = new FileDialog( null )
		{
			Title = "Save Part Studio As...",
			DefaultSuffix = StudioDocument.Extension,
			Directory = Project.Current?.GetAssetsPath() ?? "",
		};

		fd.SelectFile( _documentPath ?? $"untitled{StudioDocument.Extension}" );
		fd.SetFindFile();
		fd.SetModeSave();
		fd.SetNameFilter( $"Effigy Part Studio (*{StudioDocument.Extension})" );

		if ( !fd.Execute() )
			return;

		WriteDocument( fd.SelectedFile );
	}

	private void WriteDocument( string path )
	{
		try
		{
			StudioDocument.WriteFile( _studio, path );
		}
		catch ( Exception e )
		{
			// Saving is the one operation where failing quietly is unforgivable: the whole point of
			// pressing it is to be able to close the window.
			Log.Error( $"[Effigy] could not save to {path}: {e.Message}" );
			return;
		}

		_documentPath = path;
		MarkClean();

		Log.Info( $"[Effigy] saved {path}" );
	}

	private void Open()
	{
		// The unsaved work belongs to the studio being replaced, so the question comes first.
		ConfirmDiscard( () =>
		{
			var fd = new FileDialog( null )
			{
				Title = "Open Part Studio",
				DefaultSuffix = StudioDocument.Extension,
				Directory = Project.Current?.GetAssetsPath() ?? "",
			};

			fd.SetFindFile();

			// No SetModeOpen call: SetModeSave is the only one of the pair with proven usage in this
			// repo, and an unproven method name is a COMPILE error that takes the whole editor
			// assembly down rather than failing at the one dialog. Not calling it leaves the dialog
			// in its default mode, which at worst is a cosmetic wrinkle on an open dialog.
			fd.SetNameFilter( $"Effigy Part Studio (*{StudioDocument.Extension})" );

			if ( fd.Execute() )
				LoadDocument( fd.SelectedFile );
		} );
	}

	private void LoadDocument( string path )
	{
		PartStudio loaded;

		try
		{
			loaded = StudioDocument.ReadFile( path );
		}
		catch ( Exception e )
		{
			// StudioDocument's errors name the line and what was wrong with it, so they are worth
			// passing through rather than replacing with "could not open".
			Log.Error( $"[Effigy] could not open {path}: {e.Message}" );
			return;
		}

		_studio = loaded;
		_featureTree?.SetStudio( _studio );
		_partsPanel?.SetStudio( _studio );
		_materialsPanel?.SetStudio( _studio );
		_rigPanel?.SetStudio( _studio );
		_dialog?.Close();

		// History belongs to the document that was open. Carrying it across a load would let Ctrl+Z
		// paste the previous model's features into this one.
		_undoStack.Clear();
		_redoStack.Clear();

		RebuildStudio();

		_documentPath = path;
		MarkClean();

		// Deliberately AFTER the rebuild: a file that opens with a broken feature is exactly the
		// file you opened it to fix, and it should be on screen rather than refused.
		Log.Info( $"[Effigy] opened {path}" );
	}

	/// <summary>
	/// Ask before throwing away unsaved work, then run <paramref name="proceed"/>.
	///
	/// Cancel does nothing at all, which is the point of it: the studio is left exactly as it was.
	/// Modelled on the rig tool's, down to the button order — the same question should not be asked
	/// two different ways in one editor.
	/// </summary>
	private void ConfirmDiscard( Action proceed )
	{
		if ( !_dirty )
		{
			proceed();
			return;
		}

		var name = _documentPath is null ? "untitled" : Path.GetFileName( _documentPath );

		var confirm = new PopupWindow( "Unsaved Changes",
			$"\"{name}\" has unsaved changes. Would you like to save now?", "Cancel",
			new Dictionary<string, Action>
			{
				{ "Don\'t Save", proceed },
				{ "Save", () => { Save(); proceed(); } }
			} );

		confirm.Show();
	}

	/// <summary>
	/// Closing with unsaved work asks first.
	///
	/// Returning false CANCELS the close, and the window is closed again from inside the popup once
	/// the question is answered — Don't Save clears the flag first so the second Close sails past
	/// this check rather than asking again forever.
	/// </summary>
	protected override bool OnClose()
	{
		if ( !_dirty )
			return true;

		var name = _documentPath is null ? "untitled" : Path.GetFileName( _documentPath );

		var confirm = new PopupWindow( "Unsaved Changes",
			$"\"{name}\" has unsaved changes. Would you like to save now?", "Cancel",
			new Dictionary<string, Action>
			{
				{ "Don\'t Save", () => { _dirty = false; Close(); } },
				{ "Save", () => { Save(); Close(); } }
			} );

		confirm.Show();
		return false;
	}

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

		// Slot names go through so the file names its materials the way the user did, rather than
		// material_0..63. NameForSlot falls back to the numbers for anything unnamed.
		ObjWriter.WriteFile( _studio.ToMesh(), objPath, "effigy_export",
			materialName: _studio.NameForSlot );
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

		// RIGGED PATH: bones exist in the rig panel, so export DMX (which carries the skeleton
		// and per-vertex weights) instead of a weightless OBJ.
		if ( _rigPanel is { HasBones: true } rig )
		{
			var (mesh, ranges) = _studio.ToMeshWithBodies();
			var skeleton = rig.Skeleton;

			// BindBodies assigns each body's vertices to the bone it was assigned to in the rig
			// panel. Unassigned bodies fall back to nearest-bone rigid weighting. SmoothWeights
			// then diffuses across mesh adjacency so joints bend rather than crease.
			var weights = SkinBinder.BindBodies( mesh, ranges, rig.BodyBoneMap, skeleton );
			weights = SkinBinder.SmoothWeights( mesh, weights );
			mesh.Skin = weights;

			// DMX, not SMD. ModelDoc's loader takes FBX, DMX, OBJ and VOX and nothing else (see
			// DmxWriter for the exact string it prints), so DMX is the only supported format that
			// carries a skeleton and per-vertex weights. The .smd is still written alongside it
			// because every DCC reads one and it costs nothing to keep.
			var smdPath = Path.Combine( folder, "export.smd" );
			SmdWriter.WriteFile( mesh, smdPath, skeleton, materialName: _studio.NameForSlot );

			var dmxPath = Path.Combine( folder, "export.dmx" );
			DmxWriter.WriteFile( mesh, dmxPath, skeleton, materialName: _studio.NameForSlot,
				modelName: "effigy_export" );

			Log.Info( $"[Effigy] wrote {dmxPath} - {skeleton.Count} bones, {mesh.VertexCount} vertices" );

			var vmdlPath = Path.Combine( folder, "export.vmdl" );
			File.WriteAllText( vmdlPath, BuildSkinnedVmdl( "models/effigy/export.dmx", skeleton ) );

			var result = ExternalAssetTools.Register( folder );
			Log.Info( $"[Effigy] wrote {vmdlPath} - {result.Registered} registered" );

			var asset = AssetSystem.FindByPath( "models/effigy/export.vmdl" );

			if ( asset is null )
			{
				Log.Warning( "[Effigy] export.vmdl was written but the asset system couldn't find it" );
				return;
			}

			asset.Compile( true );

			if ( asset.IsCompileFailed )
			{
				Log.Warning( "[Effigy] export.vmdl compile FAILED - the compiler's own output above "
					+ "says why. The .dmx and .smd are both on disk either way." );
				return;
			}

			Log.Info( $"[Effigy] export.vmdl compiled - {skeleton.Count} bone(s), loading into viewport" );
			_viewport?.SetModel( Model.Load( "models/effigy/export.vmdl" ) );
			return;
		}

		// STATIC PATH: no bones — export a weightless OBJ.
		var staticObjPath = Path.Combine( folder, "export.obj" );
		ObjWriter.WriteFile( _studio.ToMesh(), staticObjPath, "effigy_export",
			materialName: _studio.NameForSlot );

		var staticVmdlPath = Path.Combine( folder, "export.vmdl" );
		File.WriteAllText( staticVmdlPath, BuildVmdl( "models/effigy/export.obj" ) );

		var staticResult = ExternalAssetTools.Register( folder );
		Log.Info( $"[Effigy] wrote {staticObjPath} and {staticVmdlPath} — {staticResult.Registered} registered" );

		var staticAsset = AssetSystem.FindByPath( "models/effigy/export.vmdl" );
		if ( staticAsset is null )
		{
			Log.Warning( "[Effigy] export.vmdl was written but asset system couldn't find it" );
			return;
		}

		staticAsset.Compile( true );
		Log.Info( staticAsset.IsCompileFailed
			? "[Effigy] export.vmdl compile FAILED"
			: "[Effigy] export.vmdl compiled — loading into viewport" );

		if ( !staticAsset.IsCompileFailed )
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

	/// <summary>
	/// A skinned .vmdl: the RenderMeshFile points at an SMD (which carries the bone hierarchy,
	/// bind pose, and per-vertex weights). ModelDoc imports the skeleton from the SMD and bakes
	/// everything into the compiled model.
	/// </summary>
	static string BuildSkinnedVmdl( string meshFilename, Skeleton skeleton ) =>
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
		BuildBoneMarkupList( skeleton ) +
		"\t\t]\n" +
		"\t\tmodel_archetype = \"\"\n" +
		"\t\tprimary_associated_entity = \"\"\n" +
		"\t\tanim_graph_name = \"\"\n" +
		"\t\tbase_model_name = \"\"\n" +
		"\t}\n" +
		"}\n";

	/// <summary>
	/// Every bone marked do_not_discard, so ModelDoc keeps bones this export gives it no other
	/// reason to keep.
	///
	/// CONFIRMED, not guessed: Assets/models/first_person/fp_arms.vmdl hit this for real — ModelDoc
	/// prunes any bone that is neither weighted by the mesh nor animated by anything imported, and
	/// arm_root/upper_arm_R/upper_arm_L kept vanishing (27 bones in the FBX, 24 in the compiled
	/// model) until a BoneMarkupList with do_not_discard=true fixed it there. That file's own
	/// BoneMarkupList is the schema this is copied from.
	///
	/// Effigy's export has no AnimationList — nothing here is ever "animated by anything imported"
	/// — so every bone leans entirely on being weighted. BindBodies' nearest-vertex fallback
	/// (SkinBinder.cs) gives most bones at least one vertex, but a short helper joint that never
	/// ends up nearest to anything has nothing else keeping it alive. Marking every bone rather than
	/// guessing which ones are at risk costs nothing and removes the question.
	/// </summary>
	static string BuildBoneMarkupList( Skeleton skeleton )
	{
		if ( skeleton is null || skeleton.Count == 0 )
			return "";

		var sb = new StringBuilder();

		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"BoneMarkupList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );

		foreach ( var bone in skeleton.Bones )
		{
			sb.Append( "\t\t\t\t\t{\n" );
			sb.Append( "\t\t\t\t\t\t_class = \"BoneMarkup\"\n" );
			sb.Append( $"\t\t\t\t\t\ttarget_bone = \"{bone.Name}\"\n" );
			sb.Append( "\t\t\t\t\t\tignore_Translation = false\n" );
			sb.Append( "\t\t\t\t\t\tignore_rotation = false\n" );
			sb.Append( "\t\t\t\t\t\tdo_not_discard = true\n" );
			sb.Append( "\t\t\t\t\t},\n" );
		}

		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t\tbone_cull_type = \"None\"\n" );
		sb.Append( "\t\t\t},\n" );

		return sb.ToString();
	}

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

		/// <summary>
		/// A copy of every sketch's geometry, which is NOT a parameter and so was invisible to
		/// undo entirely.
		///
		/// This is what made Ctrl+Z during sketching so strange: the curves you had drawn were not
		/// in the snapshot, so undo could neither remove nor restore them. It went back to the
		/// last thing that WAS recorded - usually the moment the Sketch feature was added - took
		/// the feature out of the tree, and left the lines it owned still drawn on screen.
		/// </summary>
		public Dictionary<SketchFeature, Sketch> Sketches;

		/// <summary>
		/// The faces each material assignment holds, which are not parameters either and so were
		/// invisible to undo for the same reason sketch geometry was.
		///
		/// This mattered little while the only way to pick faces was a dialog you could cancel. It
		/// matters now that right-clicking a face assigns one: without it, Ctrl+Z after a right-click
		/// took away a feature it had just added and left a face added to an existing one exactly
		/// where it was.
		/// </summary>
		public Dictionary<FaceMaterialFeature, List<FaceRef>> FaceSets;

		/// <summary>Slot names, renamed from the same menu.</summary>
		public Dictionary<int, string> MaterialNames;

		public int RollbackIndex;

		/// <summary>A full clone (Skeleton.Clone) rather than a reference — the rig panel mutates
		/// its own Skeleton in place, so holding the same instance would make every snapshot equal
		/// the current state by the time anyone looked at it again.</summary>
		public Skeleton RigSkeleton;

		public Dictionary<string, string> BodyBoneMap;
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

		var sketches = new Dictionary<SketchFeature, Sketch>();

		foreach ( var feature in _studio.Features.OfType<SketchFeature>() )
			sketches[feature] = feature.Sketch.Clone();

		var faceSets = new Dictionary<FaceMaterialFeature, List<FaceRef>>();

		foreach ( var feature in _studio.Features.OfType<FaceMaterialFeature>() )
			faceSets[feature] = new List<FaceRef>( feature.Faces );

		return new StudioSnapshot
		{
			Features = _studio.Features.ToList(),
			Values = values,
			Sketches = sketches,
			FaceSets = faceSets,
			MaterialNames = new Dictionary<int, string>( _studio.MaterialNames ),
			RollbackIndex = _studio.RollbackIndex,
			RigSkeleton = _rigPanel?.Skeleton.Clone() ?? new Skeleton(),
			BodyBoneMap = _rigPanel is null
				? new Dictionary<string, string>()
				: new Dictionary<string, string>( _rigPanel.BodyBoneMap ),
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

		// Sketch geometry is put back INTO THE EXISTING Sketch objects rather than swapped for the
		// clones. The viewport holds a direct reference to whichever sketch is open, so replacing
		// the object would leave it drawing an orphan - which is the other half of the bug this
		// fixes.
		foreach ( var (feature, sketch) in snapshot.Sketches )
		{
			feature.Sketch.Points = new List<Vec2>( sketch.Points );
			feature.Sketch.Curves = sketch.Curves.Select( c => c.Clone() ).ToList();
			feature.Sketch.Constraints = sketch.Constraints
				.Select( c => new SketchConstraint( c.Kind, c.CurveId ) ).ToList();
		}

		// Put back INTO the existing lists, for the same reason sketch geometry is: the dialog's
		// selection box holds a direct reference to the feature it is editing.
		foreach ( var (feature, faces) in snapshot.FaceSets )
		{
			feature.Faces.Clear();
			feature.Faces.AddRange( faces );
		}

		_studio.MaterialNames.Clear();

		foreach ( var (slot, name) in snapshot.MaterialNames )
			_studio.MaterialNames[slot] = name;

		_rigPanel?.RestoreRig( snapshot.RigSkeleton, snapshot.BodyBoneMap );

		_studio.MarkAllDirty();

		// The dialog may be open on a feature the restore just removed, and its snapshot of
		// "before" is now meaningless either way.
		_dialog?.Close();

		// If the sketch being drawn on no longer exists, sketch mode has to end with it. Leaving
		// it open is what left curves on screen belonging to a feature that had just been undone
		// out of the tree.
		if ( _viewport?.IsSketching == true && ActiveSketchFeature() is null )
			FinishSketch();

		RebuildStudio();
	}

	/// <summary>
	/// Mark an undo point.
	///
	/// Granularity is one dialog session, not one keystroke: this is called when a feature is
	/// added, when its dialog is opened to edit it, and on the structural commands. Recording per
	/// parameter tick would put a hundred steps on the stack for one slider drag.
	///
	/// SKETCHING IS THE EXCEPTION, and deliberately so: there each committed entity is its own
	/// step, because "undo the line I just drew" is what the key means while a sketch is open, and
	/// a dialog session there could be fifty lines long.
	/// </summary>
	private void RecordUndo()
	{
		var snapshot = Capture();

		// A step that changes nothing is a Ctrl+Z that appears broken. Clicks that only advance a
		// tool - the first corner of a rectangle, a grabbed point let go where it was - go through
		// the same path as clicks that do commit something, so the cheapest place to tell them
		// apart is here, by comparing against what is already on top.
		if ( _undoStack.Count > 0 && Same( _undoStack[^1], snapshot ) )
			return;

		_undoStack.Add( snapshot );
		_redoStack.Clear();

		if ( _undoStack.Count > 100 )
			_undoStack.RemoveAt( 0 );
	}

	/// <summary>Whether two snapshots describe the same model - same features in the same order,
	/// same parameter values, same sketch geometry.</summary>
	private static bool Same( StudioSnapshot a, StudioSnapshot b )
	{
		if ( a.RollbackIndex != b.RollbackIndex || a.Features.Count != b.Features.Count )
			return false;

		for ( var i = 0; i < a.Features.Count; i++ )
		{
			if ( !ReferenceEquals( a.Features[i], b.Features[i] ) )
				return false;
		}

		if ( a.Values.Count != b.Values.Count )
			return false;

		foreach ( var (param, value) in a.Values )
		{
			if ( !b.Values.TryGetValue( param, out var other ) || !Equals( value, other ) )
				return false;
		}

		if ( a.Sketches.Count != b.Sketches.Count )
			return false;

		foreach ( var (feature, sketch) in a.Sketches )
		{
			if ( !b.Sketches.TryGetValue( feature, out var other ) || !SameSketch( sketch, other ) )
				return false;
		}

		if ( a.FaceSets.Count != b.FaceSets.Count )
			return false;

		foreach ( var (feature, faces) in a.FaceSets )
		{
			// By COUNT, not by comparing references. Two captures of the same face are not equal, so
			// a per-element comparison would call every snapshot different and put a step on the undo
			// stack for clicks that changed nothing. A count is enough for what this decides: whether
			// a face went in or came out.
			if ( !b.FaceSets.TryGetValue( feature, out var others ) || faces.Count != others.Count )
				return false;
		}

		if ( a.MaterialNames.Count != b.MaterialNames.Count )
			return false;

		foreach ( var (slot, name) in a.MaterialNames )
		{
			if ( !b.MaterialNames.TryGetValue( slot, out var other ) || name != other )
				return false;
		}

		if ( !SameSkeleton( a.RigSkeleton, b.RigSkeleton ) )
			return false;

		if ( a.BodyBoneMap.Count != b.BodyBoneMap.Count )
			return false;

		foreach ( var (body, bone) in a.BodyBoneMap )
		{
			if ( !b.BodyBoneMap.TryGetValue( body, out var other ) || bone != other )
				return false;
		}

		return true;
	}

	/// <summary>Exact comparison, same reasoning as SameSketch's point-by-point check: a bone
	/// nudged by a millionth of a unit through the numeric inspector was still moved on purpose,
	/// and a tolerance here would silently swallow a fine adjustment instead of recording it.</summary>
	private static bool SameSkeleton( Skeleton a, Skeleton b )
	{
		if ( a.Count != b.Count )
			return false;

		for ( var i = 0; i < a.Count; i++ )
		{
			var ba = a.Bones[i];
			var bb = b.Bones[i];

			if ( ba.Name != bb.Name || ba.Parent != bb.Parent || ba.Length != bb.Length )
				return false;

			if ( !ba.Local.X.Equals( bb.Local.X ) || !ba.Local.Y.Equals( bb.Local.Y )
				|| !ba.Local.Z.Equals( bb.Local.Z ) || !ba.Local.Origin.Equals( bb.Local.Origin ) )
				return false;
		}

		return true;
	}

	private static bool SameSketch( Sketch a, Sketch b )
	{
		if ( a.Points.Count != b.Points.Count || a.Curves.Count != b.Curves.Count )
			return false;

		for ( var i = 0; i < a.Points.Count; i++ )
		{
			// Exact comparison on purpose: a point that moved by a millionth of a unit was still
			// moved by the user, and a tolerance here would silently swallow fine adjustments.
			if ( a.Points[i].x != b.Points[i].x || a.Points[i].y != b.Points[i].y )
				return false;
		}

		for ( var i = 0; i < a.Curves.Count; i++ )
		{
			if ( a.Curves[i].Id != b.Curves[i].Id || a.Curves[i].Construction != b.Curves[i].Construction )
				return false;
		}

		return true;
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

		_viewport.SetSketchTool( kind );
		UpdateSketchToolChecks( kind );
	}

	// --- palette / theming ------------------------------------------------------------------

	private void SetPalette( int index )
	{
		_paletteIndex = Math.Clamp( index, 0, EffigyPalette.All.Length - 1 );
		_palette = EffigyPalette.All[_paletteIndex];

		ApplyPalette();

		// No BuildMenuBar() any more. It was here to redraw the View menu's checkmarks, and the
		// palette list is a dropdown in Edit > Settings now — the combo already shows what is
		// selected, and rebuilding the whole menu bar to update a tick that no longer exists was
		// throwing away the Edit and View menus on every palette change.
		EditorCookie.Set( PaletteCookie, _paletteIndex );
	}

	// --- settings ------------------------------------------------------------------------------

	/// <summary>Where the two settings persist between sessions. EditorCookie is the engine's own
	/// per-editor store — the same one the Boolean tool keeps its mode in.</summary>
	private const string PaletteCookie = "Effigy.Palette";

	/// <summary>A NEW KEY, not the old Effigy.ShowSketchGrid. That one meant "grid on the sketch
	/// plane" and defaulted to on; this one means "grid on every plane" and defaults to off. Reusing
	/// the key would have read a value stored against the old meaning and turned every plane's grid
	/// on for anyone who had ever opened the settings window.</summary>
	private const string PlaneGridCookie = "Effigy.ShowPlaneGrid";
	private const string GridSpacingCookie = "Effigy.GridSpacing";
	private const string SnapGridCookie = "Effigy.SnapToGrid";
	private const string SnapPointsCookie = "Effigy.SnapToPoints";

	/// <summary>The open settings window, or null. Held so a second Edit > Settings raises the one
	/// already open rather than stacking another on top of it.</summary>
	private EffigySettingsWindow _settingsWindow;

	private void OpenSettings()
	{
		if ( _settingsWindow.IsValid() )
		{
			_settingsWindow.Focus();
			return;
		}

		_settingsWindow = new EffigySettingsWindow( this, CurrentSettings(), ApplySettings );
		_settingsWindow.Show();
	}

	private EffigySettingsWindow.Values CurrentSettings() => new()
	{
		ShowGrid = _viewport?.ShowPlaneGrid ?? false,
		GridSpacing = _viewport?.GridSpacing ?? 0f,
		SnapToGrid = _viewport?.SnapToGrid ?? true,
		SnapToPoints = _viewport?.SnapToPoints ?? true,
		PaletteIndex = _paletteIndex,
	};

	/// <summary>Take everything the settings window is showing and make it true, then remember it.
	/// Called on every control change rather than behind an OK button — a viewport setting you
	/// cannot see take effect is one you have to guess at.</summary>
	private void ApplySettings( EffigySettingsWindow.Values values )
	{
		if ( _viewport.IsValid() )
		{
			_viewport.ShowPlaneGrid = values.ShowGrid;
			_viewport.GridSpacing = values.GridSpacing;
			_viewport.SnapToGrid = values.SnapToGrid;
			_viewport.SnapToPoints = values.SnapToPoints;
		}

		if ( values.PaletteIndex != _paletteIndex )
			SetPalette( values.PaletteIndex );

		EditorCookie.Set( PlaneGridCookie, values.ShowGrid );
		EditorCookie.Set( GridSpacingCookie, values.GridSpacing );
		EditorCookie.Set( SnapGridCookie, values.SnapToGrid );
		EditorCookie.Set( SnapPointsCookie, values.SnapToPoints );
	}

	/// <summary>Put last session's settings back, before anything is drawn with them.</summary>
	private void RestoreSettings()
	{
		SetPalette( EditorCookie.Get( PaletteCookie, _paletteIndex ) );

		if ( !_viewport.IsValid() )
			return;

		_viewport.ShowPlaneGrid = EditorCookie.Get( PlaneGridCookie, false );
		_viewport.GridSpacing = EditorCookie.Get( GridSpacingCookie, 0f );
		_viewport.SnapToGrid = EditorCookie.Get( SnapGridCookie, true );
		_viewport.SnapToPoints = EditorCookie.Get( SnapPointsCookie, true );
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

		// The strips fill their own rects with this, so the gaps between their buttons read as
		// viewport rather than as chrome. Exactly the viewport's clear colour, or the seam shows.
		if ( _toolStrip is not null )
			_toolStrip.GapColor = _palette.ViewportBg;

		if ( _sketchStrip is not null )
			_sketchStrip.GapColor = _palette.ViewportBg;

		// Grid lines want the palette's dim text colour: it is picked to sit just above the
		// background in every one of these palettes, which is exactly the job.
		_viewport.PlaneColor = _palette.TextDim.WithAlpha( 0.55f );
	}

	// --- constraining a sketch selection --------------------------------------------------------

	/// <summary>
	/// The constraint menu, on a right-click inside a sketch.
	///
	/// A MENU RATHER THAN A TOOLBAR, which is not what Onshape does. The reason is what the offers
	/// are: they change with every click, so a strip of buttons would have to relabel, enable and
	/// disable itself per frame, and every bit of that is widget code this repo cannot compile to
	/// check. A menu is built fresh each time it opens, from machinery already proven in the feature
	/// tree and the face menu, and it puts the choices where the cursor already is.
	///
	/// What may be applied is ConstraintTools' answer, not this method's — it knows a point and a
	/// line make a point-on-line and two lines do not, and it knows what the sketch already says.
	/// </summary>
	private void OpenSketchConstraintMenu()
	{
		if ( _viewport?.ActiveSketch is not { } sketch )
			return;

		var offers = ConstraintTools.Offers( sketch, _viewport.SketchSelection );

		var menu = new Menu( _viewport );

		if ( offers.Count == 0 )
		{
			// SAYING SO IS THE POINT. An empty menu, or no menu at all, reads as a broken right
			// button — the user has selected something and is entitled to know why it buys them
			// nothing.
			menu.AddHeading( "Nothing to constrain from this selection" );

			menu.AddOption( "Clear selection", "backspace", () => _viewport.ClearSketchSelection() );

			menu.OpenAtCursor();
			return;
		}

		menu.AddHeading( Describe( _viewport.SketchSelection ) );

		foreach ( var offer in offers )
		{
			var it = offer;

			var option = menu.AddOption( it.NeedsValue ? $"{it.Label}…" : it.Label, IconFor( it.Kind ),
				() =>
				{
					if ( it.NeedsValue )
						AskForDimension( it );
					else
						ApplyConstraint( it );
				} );

			option.StatusTip = it.Hint;
		}

		menu.AddSeparator();

		menu.AddOption( "Clear selection", "backspace", () => _viewport.ClearSketchSelection() );

		menu.OpenAtCursor();
	}

	/// <summary>
	/// A dimension asks for its number before it is applied, in the one-field popup the feature tree
	/// renames with — pre-filled with what the sketch currently measures.
	///
	/// Pre-filled matters more than it looks. Most dimensions are added to LOCK something where it
	/// already is, and an empty box turns that into measuring by hand and typing a rounded version,
	/// which moves the geometry by however much the rounding was.
	/// </summary>
	private void AskForDimension( ConstraintOffer offer )
	{
		var menu = new Menu( _viewport );

		var edit = new LineEdit( Expression.Format( offer.Value ), menu ) { FixedWidth = 140 };

		edit.ReturnPressed += () =>
		{
			menu.Close();

			// Through the expression evaluator, the same as every numeric field in the dialog, so a
			// dimension can be typed as "25/2" or "3*8" like any other number in this editor.
			// The offer's own unit, so an angle typed as "45" reads as degrees and a length as units —
			// the same evaluator every numeric field in the dialog goes through.
			if ( !Expression.TryEvaluate( edit.Text, string.IsNullOrEmpty( offer.Unit ) ? null : offer.Unit, out var value ) )
			{
				SetPrompt( $"'{edit.Text}' is not a number" );
				return;
			}

			offer.Value = value;
			ApplyConstraint( offer );
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>Apply, and treat it as an edit of the sketch — an undo step, and a rebuild, because
	/// the solve has moved geometry that features downstream are standing on.</summary>
	private void ApplyConstraint( ConstraintOffer offer )
	{
		RecordUndo();

		if ( !_viewport.ApplySketchConstraint( offer ) )
			return;

		OnSketchEdited();
	}

	static string Describe( SketchSelection selection )
	{
		var parts = new List<string>();

		if ( selection.Points.Count > 0 )
			parts.Add( $"{selection.Points.Count} point{(selection.Points.Count == 1 ? "" : "s")}" );

		if ( selection.Curves.Count > 0 )
			parts.Add( $"{selection.Curves.Count} curve{(selection.Curves.Count == 1 ? "" : "s")}" );

		return string.Join( " and ", parts );
	}

	/// <summary>Classic Material Icons only — the set this editor's other menus draw from.</summary>
	static string IconFor( SketchConstraintKind kind ) => kind switch
	{
		SketchConstraintKind.Horizontal => "horizontal_rule",
		SketchConstraintKind.Vertical => "straighten",
		SketchConstraintKind.Coincident => "adjust",
		SketchConstraintKind.Distance => "straighten",
		SketchConstraintKind.EqualLength => "drag_handle",
		SketchConstraintKind.Parallel => "menu",
		SketchConstraintKind.Perpendicular => "square_foot",
		SketchConstraintKind.Angle => "square_foot",
		SketchConstraintKind.PointOnLine => "linear_scale",
		SketchConstraintKind.Symmetric => "flip",
		SketchConstraintKind.Radius => "radio_button_unchecked",
		_ => "rule",
	};

	// --- right-click a face -------------------------------------------------------------------

	/// <summary>
	/// The material menu on a face of the model.
	///
	/// The Face Material feature on the toolbar is how you paint a SET of faces in one go, and it is
	/// the wrong shape for the common case: one face, one slot, now. Opening a dialog, arming a
	/// selection box, clicking the face, closing the dialog is five actions for a thing you were
	/// already pointing at.
	///
	/// It still goes through the history. Writing the slot straight onto the mesh would work until
	/// the next rebuild and then quietly revert — bodies are rebuilt from scratch, which is the whole
	/// reason FaceMaterialFeature exists (see FaceMaterialTests: "the reason this is a feature").
	/// </summary>
	private void OpenFaceMaterialMenu( EffigyFaceHit hit )
	{
		if ( _studio is null || _viewport is null || hit.Body is null )
			return;

		var menu = new Menu( _viewport );

		menu.AddHeading( $"Face — {_studio.NameForSlot( hit.Material )}" );

		foreach ( var slot in MenuMaterialSlots() )
		{
			var value = slot;

			// Slot 0 is the default every face starts on and the one the viewport deliberately does
			// not tint, so it gets the hollow marker — "no material" rather than "material zero".
			var option = menu.AddOption( _studio.NameForSlot( value ),
				value == 0 ? "panorama_fish_eye" : "lens",
				() => AssignFaceMaterial( hit, value ) );

			option.Checkable = true;
			option.Checked = hit.Material == value;
		}

		menu.AddSeparator();

		// The picker rather than the row widget the dialog and the Materials panel use: a menu closes
		// the moment you click anything in it, and it would take an embedded row — and the modal that
		// row had just parented to itself — down with it. Pick is the shared half that survives that.
		var choose = menu.AddOption( $"Choose material for {_studio.NameForSlot( hit.Material )}…", "palette",
			() => EffigyMaterialSlot.Pick( this, hit.Material, SlotMaterial( hit.Material ), SetSlotMaterial ) );

		choose.StatusTip = "Browse for the material this slot exports as";

		var rename = menu.AddOption( $"Rename {_studio.NameForSlot( hit.Material )}…", "edit",
			() => BeginMaterialSlotRename( hit.Material ) );

		rename.StatusTip = "The name every exporter writes for this slot";

		var shade = menu.AddOption( "Shade Material Slots", "palette",
			() => _viewport.ShadeMaterialSlots = !_viewport.ShadeMaterialSlots );

		shade.Checkable = true;
		shade.Checked = _viewport.ShadeMaterialSlots;

		menu.OpenAtCursor();
	}

	/// <summary>
	/// Which slots the menu offers: zero through seven, plus anything the document already uses.
	///
	/// Seven is not arbitrary — it is how many colours the viewport tints with, so every slot on the
	/// menu is one you can tell apart on screen. The kernel allows 0..63 and nobody picks slot 40 off
	/// a list, but a document that arrived with one must not be unreachable, so the slots already in
	/// use are added back in however high they are.
	/// </summary>
	private IEnumerable<int> MenuMaterialSlots()
	{
		var slots = new SortedSet<int>();

		for ( var i = 0; i <= 7; i++ )
			slots.Add( i );

		foreach ( var slot in FaceMaterialEdit.UsedSlots( _studio ) )
			slots.Add( slot );

		return slots;
	}

	/// <summary>Name a slot, in the one-field popup the feature tree renames with.</summary>
	private void BeginMaterialSlotRename( int slot )
	{
		var menu = new Menu( this );
		var edit = new LineEdit( _studio.NameForSlot( slot ), menu ) { FixedWidth = 190 };

		edit.ReturnPressed += () =>
		{
			// Closed BEFORE the edit, because SetSlotMaterial rebuilds and this menu is a child of
			// the window it is rebuilding.
			var name = edit.Text?.Trim();
			menu.Close();

			SetSlotMaterial( slot, name );
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>
	/// What a slot carries, or null when it is still on its numbered default.
	///
	/// Not NameForSlot: that answers "what do the exporters write", which is never null, and the
	/// controls need "has anybody chosen anything", which is the question with an empty answer.
	/// </summary>
	private string SlotMaterial( int slot ) =>
		_studio is not null && _studio.MaterialNames.TryGetValue( slot, out var name )
			&& !string.IsNullOrWhiteSpace( name )
			? name
			: null;

	/// <summary>
	/// Give a slot a material — the one place all three controls land.
	///
	/// It is a document edit like any other: undo first, rebuild after. The rebuild is what repaints
	/// every face on the slot, refreshes the Materials panel, and pushes the new value back into a
	/// feature dialog that happens to be open on the same slot.
	/// </summary>
	private void SetSlotMaterial( int slot, string material )
	{
		if ( _studio is null || slot < 0 )
			return;

		var name = material?.Trim();

		// Clearing it puts the slot back on its numbered default rather than leaving it blank. Every
		// exporter has to write SOMETHING per slot, and an empty usemtl is not it. Typing the default
		// back in by hand means the same thing as clearing it, and is stored the same way — otherwise
		// the slot would read as assigned while exporting exactly what an unassigned one does.
		var clearing = string.IsNullOrWhiteSpace( name ) || name == ObjWriter.DefaultMaterialName( slot );

		if ( clearing ? !_studio.MaterialNames.ContainsKey( slot ) : SlotMaterial( slot ) == name )
			return;

		RecordUndo();

		if ( clearing )
			_studio.MaterialNames.Remove( slot );
		else
			_studio.MaterialNames[slot] = name;

		RebuildStudio();
	}

	/// <summary>Put one face on one slot. The bookkeeping — which assignment to reuse, what happens
	/// to the one the face is leaving, where a new one goes in a rolled-back tree — is
	/// FaceMaterialEdit in the kernel, where FaceMenuTests can hold it to account.</summary>
	private void AssignFaceMaterial( EffigyFaceHit hit, int slot )
	{
		if ( _studio is null || hit.Body is null || hit.Material == slot )
			return;

		RecordUndo();

		if ( FaceMaterialEdit.Assign( _studio, hit.Body.Id, hit.FaceIndex, hit.Reference, slot ) )
			RebuildStudio();
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

/// <summary>
/// The hover-reveal "eye" a tree row uses to toggle visibility — one implementation shared by the
/// Features tree (sketches, default geometry) and the Parts tree (bodies).
///
/// Before this they were two independent copies of the same idea that had quietly drifted: the
/// Features tree reserved 34px of right margin for its secondary text and never hid it, the Parts
/// tree reserved only 30px and hid its face count on hover instead — two different answers to the
/// same "don't let anything sit under the eye" problem, which is exactly the kind of thing that
/// reads as the eye behaving inconsistently between the two trees even though neither was wrong on
/// its own. One rect, one show/hide rule, one click test, everywhere a row has an eye — and
/// SecondaryTextRightMargin so a row's own text picks a margin that is provably wide enough
/// rather than tracking Width by memory in a second place.
/// </summary>
internal static class TreeEyeIcon
{
	/// <summary>Width of the eye's own hit/paint rect, right-aligned to the tree.</summary>
	public const float Width = 24f;

	/// <summary>Gap kept clear between the eye and its own left edge.</summary>
	public const float Padding = 4f;

	/// <summary>How far from the row's right edge a row's OTHER text needs to stay clear of,
	/// whether or not the eye is actually drawn on this frame — the eye still needs the room the
	/// instant the row is hovered, so the margin cannot depend on hover state.</summary>
	public const float SecondaryTextRightMargin = Width + Padding + 6f;

	public static Rect Rect( TreeView tree, VirtualWidget item ) =>
		new( tree.LocalRect.Right - Width - Padding, item.Rect.Top, Width, item.Rect.Height );

	/// <summary>Shown on hover always, and whether or not hovered when the row is hidden — a
	/// hidden row stays obviously hidden rather than only announcing it while the mouse happens to
	/// be there.</summary>
	public static bool ShouldShow( VirtualWidget item, bool visible ) => item.Hovered || !visible;

	public static void Draw( TreeView tree, VirtualWidget item, bool visible )
	{
		if ( !ShouldShow( item, visible ) )
			return;

		Paint.SetPen( visible ? Theme.TextLight : Theme.Text );
		Paint.DrawIcon( Rect( tree, item ), visible ? "visibility" : "visibility_off", 16, TextFlag.Center );
	}

	public static bool WasClicked( TreeView tree, VirtualWidget item, MouseEvent e ) =>
		Rect( tree, item ).IsInside( e.LocalPosition );
}

/// <summary>What the feature tree's context menu asked the window to do. The panel knows what was
/// clicked; the window owns the studio, the dialog and the undo stack, so it does the doing.</summary>
internal enum EffigyFeatureCommand
{
	Edit,
	Rename,
	ToggleSuppress,
	Delete,
	MoveUp,
	MoveDown,
	RollbackTo,
	RollForward,
}

internal sealed class EffigyFeatureTreePanel : Widget
{
	private interface IVisibilityNode
	{
		bool IsVisible { get; }
		string VisibilityKey { get; }
		void ToggleVisibility();
	}

	private sealed class VisibilityTreeView : TreeView
	{
		public VisibilityTreeView( Widget parent ) : base( parent ) { }
		protected override bool OnItemPressed( VirtualWidget item, MouseEvent e )
		{
			if ( item.Object is IVisibilityNode node && TreeEyeIcon.WasClicked( this, item, e ) )
			{
				node.ToggleVisibility();
				return false;
			}
			return base.OnItemPressed( item, e );
		}
	}
	private PartStudio _studio;
	private TreeView _tree;
	private readonly Dictionary<Feature, FeatureNode> _nodes = new();

	public Feature SelectedFeature { get; private set; }

	public Action<Feature> FeatureSelected { get; set; }
	public Action StudioChanged { get; set; }
	public Action<string, bool> VisibilityToggled { get; set; }

	/// <summary>A context-menu item was picked.</summary>
	public Action<Feature, EffigyFeatureCommand> CommandRequested { get; set; }

	/// <summary>A rename was typed and confirmed. Separate from CommandRequested because it
	/// carries the new text, and because the window has to snapshot for undo BEFORE applying
	/// it.</summary>
	public Action<Feature, string> RenameCommitted { get; set; }

	/// <summary>
	/// What a sketch is attached to, shown on its row in the tree.
	///
	/// THE DIFFERENCE THIS MAKES IS THE WHOLE PARAMETRIC MODEL. A sketch on a face moves when that
	/// face moves, so everything built from it follows; a sketch on Top/Front/Right is anchored in
	/// world space and never follows anything. Both are legitimate and they look identical once
	/// the dialog is closed, which makes "why did that not update?" impossible to answer by
	/// looking at the tree. Now the row says which one it is.
	/// </summary>
	public string AttachmentLabel( SketchFeature sketch )
	{
		if ( sketch is null )
			return "";

		if ( sketch.Face is not { } face )
		{
			var offset = sketch.PlaneOffset.Value;

			return offset == 0f ? sketch.Plane.Value : $"{sketch.Plane.Value} {offset:+0.##;-0.##}";
		}

		var body = _studio?.Bodies.FirstOrDefault( b => b.Id == face.BodyId );

		// A face reference that resolves to nothing is the one case worth shouting about: the
		// sketch is about to fail, or already has.
		return body is null ? "face (missing)" : $"on {body.Name ?? "part"}";
	}

	/// <summary>True when the rollback bar sits above this feature, so it is not being evaluated.
	/// Painted dimmer, the way Onshape greys out everything below the bar.</summary>
	public bool IsRolledPast( Feature feature ) =>
		_studio is not null && _studio.Features.IndexOf( feature ) >= _studio.EffectiveCount;

	/// <summary>True for the FIRST feature below the bar - the one the bar is drawn above.</summary>
	public bool IsFirstRolledPast( Feature feature ) =>
		_studio is not null
		&& _studio.RollbackIndex < _studio.Features.Count
		&& _studio.Features.IndexOf( feature ) == _studio.EffectiveCount;

	/// <summary>
	/// Rename in place: a one-field popup at the cursor, which is what Menu.AddWidget is for.
	/// Opened by double-clicking a feature (TreeNode.OnActivated) or from the context menu.
	///
	/// The tree paints its rows virtually - there is no per-row widget to turn into a text box -
	/// so an editor has to be floated over it either way, and a popup is the one the editor
	/// already has machinery for.
	/// </summary>
	public void BeginRename( Feature feature )
	{
		if ( feature is null )
			return;

		var menu = new Menu( this );
		var edit = new LineEdit( feature.Name ?? feature.TypeName, menu ) { FixedWidth = 190 };

		edit.ReturnPressed += () =>
		{
			RenameCommitted?.Invoke( feature, edit.Text );
			menu.Close();
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>The right-click menu on a feature. Every entry acts on the feature that was
	/// clicked rather than on the selection, so right-clicking one row while another is selected
	/// does what it looks like it does.</summary>
	public void OpenFeatureMenu( Feature feature )
	{
		if ( feature is null )
			return;

		var menu = new Menu( this );

		menu.AddOption( "Edit", "edit", () => CommandRequested?.Invoke( feature, EffigyFeatureCommand.Edit ) );
		menu.AddOption( "Rename", "text_fields", () => BeginRename( feature ) );

		menu.AddSeparator();

		menu.AddOption( feature.Suppressed ? "Unsuppress" : "Suppress", "block",
			() => CommandRequested?.Invoke( feature, EffigyFeatureCommand.ToggleSuppress ) );

		if ( feature is SketchFeature )
		{
			var key = $"sketch:{feature.Id}";

			menu.AddOption( IsVisible( key ) ? "Hide sketch" : "Show sketch",
				IsVisible( key ) ? "visibility_off" : "visibility", () => ToggleVisibility( key ) );
		}

		menu.AddSeparator();

		menu.AddOption( "Move up", "arrow_upward", () => CommandRequested?.Invoke( feature, EffigyFeatureCommand.MoveUp ) );
		menu.AddOption( "Move down", "arrow_downward", () => CommandRequested?.Invoke( feature, EffigyFeatureCommand.MoveDown ) );

		menu.AddSeparator();

		menu.AddOption( "Roll back to before this", "history",
			() => CommandRequested?.Invoke( feature, EffigyFeatureCommand.RollbackTo ) );

		if ( _studio is not null && _studio.RollbackIndex < _studio.Features.Count )
		{
			menu.AddOption( "Roll forward to end", "last_page",
				() => CommandRequested?.Invoke( feature, EffigyFeatureCommand.RollForward ) );
		}

		menu.AddSeparator();

		menu.AddOption( "Delete", "delete", () => CommandRequested?.Invoke( feature, EffigyFeatureCommand.Delete ) );

		menu.OpenAtCursor();
	}

	/// <summary>Only keys the user has actually clicked the eye on. Everything else falls through
	/// to DefaultVisible, so an automatic decision (a consumed sketch hiding itself) can be
	/// overridden by hand and STAY overridden across rebuilds.</summary>
	private readonly Dictionary<string, bool> _visibility = new();

	public bool IsVisible( string key ) =>
		_visibility.TryGetValue( key, out var value ) ? value : DefaultVisible( key );

	/// <summary>Everything starts visible except a sketch some later feature has already built
	/// from - Onshape hides those the moment they are consumed, and so do we.</summary>
	private bool DefaultVisible( string key )
	{
		if ( _consumedSketchIds is null || !key.StartsWith( "sketch:" ) )
			return true;

		return !_consumedSketchIds.Contains( key["sketch:".Length..] );
	}

	/// <summary>Recomputed once per Rebuild rather than per eye paint - the tree repaints
	/// constantly and walking the feature list on every row of every frame would be wasteful.</summary>
	private HashSet<string> _consumedSketchIds;
	private void ToggleVisibility( string key )
	{
		var visible = !IsVisible( key );
		_visibility[key] = visible;
		VisibilityToggled?.Invoke( key, visible );
		_tree.Update();
	}
	private void PaintEye( VirtualWidget item, string key ) => TreeEyeIcon.Draw( _tree, item, IsVisible( key ) );

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

		_tree = new VisibilityTreeView( this );
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
		_consumedSketchIds = _studio?.ConsumedSketchIds();

		// Default geometry node (always present, like Onshape)
		var defaultGeo = _tree.AddItem( new DefaultGeometryNode( this ) );
		_tree.Open( defaultGeo );

		// Feature nodes
		foreach ( var feature in _studio.Features )
		{
			if ( IsHiddenFromTree( feature ) )
				continue;

			var node = new FeatureNode( this, feature );
			_nodes[feature] = node;
			_tree.AddItem( node );

			if ( feature.Suppressed )
				_tree.Close( node );
		}
	}

	/// <summary>
	/// Features that do their job without ever needing to be looked at.
	///
	/// FACE MATERIALS ARE BOOKKEEPING, NOT STEPS. Right-clicking a face and picking a slot creates
	/// one of these — one per slot, reused thereafter (FaceMaterialEdit.SlotFeature) — because the
	/// assignment has to live in the history or the next rebuild throws it away. That is a storage
	/// decision, and it was leaking into the tree as a row per slot: paint four faces four colours
	/// and the recipe for the part gained four entries that say nothing about how it was built.
	///
	/// Hiding the row does not hide the effect — the faces stay painted, undo still steps back
	/// through the assignments, and right-clicking the face again is how you change your mind.
	/// </summary>
	private static bool IsHiddenFromTree( Feature feature ) => feature is FaceMaterialFeature;

	// --- tree node types --------------------------------------------------------------------

	/// <summary>Root "Default geometry" node with Origin/Top/Front/Right children.</summary>
	private sealed class DefaultGeometryNode : TreeNode<string>
	{
		private readonly EffigyFeatureTreePanel _panel;

		public DefaultGeometryNode( EffigyFeatureTreePanel panel ) : base( "Default geometry" )
		{
			_panel = panel;
		}

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
				new( _panel, "Origin", "adjust", "origin" ),
				new( _panel, "Top (XY)", "crop_landscape", "top" ),
				new( _panel, "Front (XZ)", "crop_landscape", "front" ),
				new( _panel, "Right (YZ)", "crop_landscape", "right" ),
			} );
		}
	}

	/// <summary>Origin and the three reference planes under "Default geometry".</summary>
	private sealed class DefaultGeometryChildNode : TreeNode<string>
		, IVisibilityNode
	{
		private readonly string _icon;
		private readonly EffigyFeatureTreePanel _panel;
		public string VisibilityKey { get; }
		public bool IsVisible => _panel.IsVisible( VisibilityKey );
		public void ToggleVisibility() => _panel.ToggleVisibility( VisibilityKey );

		public DefaultGeometryChildNode( EffigyFeatureTreePanel panel, string name, string icon, string key ) : base( name )
		{
			_panel = panel;
			_icon = icon;
			VisibilityKey = key;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			Paint.SetPen( Theme.TextLight );
			Paint.DrawIcon( item.Rect, _icon, 14, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text );
			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), Value, TextFlag.LeftCenter );
			_panel.PaintEye( item, VisibilityKey );
		}
	}

	/// <summary>A feature in the tree — icon + name + error/suppressed indicator.</summary>
	private sealed class FeatureNode : TreeNode<Feature>, IVisibilityNode
	{
		private readonly EffigyFeatureTreePanel _panel;
		public string VisibilityKey => $"sketch:{Feature.Id}";
		public bool IsVisible => Feature is SketchFeature && _panel.IsVisible( VisibilityKey );
		public void ToggleVisibility() { if ( Feature is SketchFeature ) _panel.ToggleVisibility( VisibilityKey ); }
		public Feature Feature => Value;

		public FeatureNode( EffigyFeatureTreePanel panel, Feature feature ) : base( feature ) { _panel = panel; }

		/// <summary>Double click renames, which is where every tree in the editor puts it.</summary>
		public override void OnActivated() => _panel.BeginRename( Feature );

		/// <summary>Right click opens the feature menu. Returning true stops the tree falling back
		/// to its own (empty) menu.</summary>
		public override bool OnContextMenu()
		{
			_panel.OpenFeatureMenu( Feature );
			return true;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			// Below the rollback bar: this feature is not being evaluated at all, so it is drawn
			// as history rather than as part of the model. The bar itself is a line across the top
			// of the first such row - the same place Onshape draws it.
			var rolled = _panel.IsRolledPast( Value );

			if ( _panel.IsFirstRolledPast( Value ) )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Yellow.WithAlpha( 0.75f ) );
				Paint.DrawRect( new Rect( item.Rect.Left, item.Rect.Top, item.Rect.Width, 2f ) );
			}

			// Icon color: blue for active, grey for suppressed, red for error
			if ( Value.Suppressed || rolled )
				Paint.SetPen( Theme.TextLight.WithAlpha( 0.5f ) );
			else if ( Value.Error is not null )
				Paint.SetPen( Theme.Red );
			else
				Paint.SetPen( Theme.Blue );

			Paint.DrawIcon( item.Rect, "category", 14, TextFlag.LeftCenter );

			Paint.SetPen( Value.Suppressed || rolled ? Theme.TextLight : Theme.Text );
			var label = $"{Value.Name ?? Value.TypeName}";
			if ( Value.Suppressed )
				label += " (suppressed)";
			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), label, TextFlag.LeftCenter );
			// Right-aligned, clear of the eye's strip: what this sketch is attached to, and
			// therefore whether anything built from it will follow an edit upstream.
			if ( Value is SketchFeature attached )
			{
				Paint.SetPen( Theme.TextLight.WithAlpha( 0.55f ) );
				Paint.DrawText( item.Rect.Shrink( 0, 0, TreeEyeIcon.SecondaryTextRightMargin, 0 ),
					_panel.AttachmentLabel( attached ), TextFlag.RightCenter );
			}

			if ( Value is SketchFeature ) _panel.PaintEye( item, VisibilityKey );
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
	public const float ButtonSpacing = 10f;

	/// <summary>Gap between tool groups. Wider than ButtonSpacing and used nowhere else, so the
	/// grouping reads as deliberate rather than as uneven spacing.</summary>
	public const float GroupSpacing = 30f;

	public EffigyToolStrip( Widget parent ) : base( parent )
	{
		Layout = Layout.Row();
		Layout.Spacing = ButtonSpacing;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		// No background of its own: the strip floats on the 3D view and only its buttons should
		// be visible. Anything painted here would read as a chrome band across the viewport.
		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedHeight = ButtonSize;
		FixedWidth = 0f;
	}

	/// <summary>The colour to fill the strip's own rect with — set to the viewport's background so
	/// the gaps between buttons disappear into the 3D view. See OnPaint.</summary>
	public Color GapColor { get; set; } = Theme.ControlBackground;

	/// <summary>
	/// Fill the whole rect with the VIEWPORT'S OWN BACKGROUND, so the strip vanishes and only its
	/// buttons are left standing on the 3D view as separate keys.
	///
	/// THE STRIP CANNOT SIMPLY NOT PAINT. It was doing exactly that at first, and the gaps came out
	/// white: TranslucentBackground and NoSystemBackground were both already set — they had been
	/// since before the strip ever had an OnPaint — and a rect this widget leaves unpainted still
	/// ends up showing whatever was in the paint buffer. That is the same effect EffigyToolButton
	/// documents when it repaints its own background every frame to wipe the previous frame's hover
	/// glow. Painting something is not optional; the only choice is what.
	///
	/// So it paints the one colour that reads as nothing: whatever the viewport is clearing to.
	/// EffigyWindow.ApplyPalette keeps it in step, so it stays invisible through a palette change.
	/// The honest limit is that this matches the BACKGROUND, not the scene — geometry passing
	/// behind the strip is covered rather than seen through. At the top-left corner where the strip
	/// floats, that is rarely anything.
	/// </summary>
	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	/// <summary>Running total of everything added, kept by hand so FixedWidth can be set outright.
	///
	/// This used to call AdjustSize() and let the widget size itself. That does not survive
	/// spacing CELLS - the strip came out narrower than its contents and the buttons past the cut
	/// simply were not there. Counting what we add is exact and cannot silently lose a cell.</summary>
	private float _contentWidth;

	private void Grew( float cellWidth )
	{
		_contentWidth += (_contentWidth > 0f ? ButtonSpacing : 0f) + cellWidth;
		FixedWidth = _contentWidth;
	}

	/// <summary>Button edge length, shared with EffigySketchToolButton so both strips size
	/// identically. 40 -> 54: the squares are the only chrome on the 3D view and were reading as a
	/// cramped against it, and with no background box left (see OnPaint) they need the extra room
	/// for the glyph itself to carry the button.</summary>
	public const float ButtonSize = 54f;

	/// <summary>How far up the hand-painted glyphs are scaled from the nominal 18x18 box they are
	/// authored in. 1.5 puts a 27px glyph in a 54px button - the same glyph-to-button ratio the
	/// font-icon sketch strip uses, so the two strips still read as one piece of chrome.</summary>
	public const float IconScale = 1.5f;

	/// <summary>Bigger, for the one button wide enough to carry a label. The square buttons are
	/// sized so twelve of them fit across the viewport; the Sketch button is not, and at the shared
	/// scale its glyph was the smallest thing on the largest button. 1.95 puts a 35px pencil in a
	/// 54px button, which is where the wood, the ferrule and the eraser start telling apart.</summary>
	public const float LabelIconScale = 1.95f;

	/// <summary>Point size of a tool button's label.</summary>
	public const float LabelFontSize = 12f;

	/// <summary>
	/// The colour of every CONFIRM action in this editor - accept a feature, finish a sketch,
	/// validate a binding.
	///
	/// A tick drawn in the same grey as everything else is a shape you have to go looking for, and
	/// the two on screen at once - accept the feature, finish the sketch - are the two most
	/// consequential buttons in the tool. Green for commit is one of the few colour conventions
	/// everyone already reads without being taught. Anything new that commits something should use
	/// this rather than picking its own green.
	/// </summary>
	public static Color ConfirmColor => Theme.Green;

	/// <summary>
	/// The ONLY thing a tool button does when you interact with it: a faint halo hugging its outer
	/// edge. Nothing about the button changes colour - not the glyph, not a background box - so a
	/// strip sitting on the 3D view stays still instead of flickering between fills as the cursor
	/// crosses it.
	///
	/// Drawn as concentric rounded rects fading INWARD from the edge, because a widget clips its
	/// own painting: a halo drawn outside LocalRect would simply be cut off. The glyph only fills
	/// the middle half of the button, so there is room for the halo to read as an outside edge.
	/// </summary>
	public static void PaintEdgeGlow( Rect rect, float strength )
	{
		const int Rings = 5;

		Paint.ClearBrush();

		for ( var i = 0; i < Rings; i++ )
		{
			var falloff = 1f - i / (float)Rings;

			Paint.SetPen( Theme.Text.WithAlpha( strength * falloff * falloff * 0.5f ), 1f );
			Paint.DrawRect( rect.Shrink( 0.5f + i ), 6f );
		}
	}

	/// <summary>A crisp ring at the edge, for a mode that is armed and has to stay visibly armed
	/// with the cursor somewhere else. A ring rather than a tint - same no-colour-change rule as
	/// PaintEdgeGlow, so armed reads as a different SHAPE, not a different colour.</summary>
	public static void PaintEdgeRing( Rect rect )
	{
		Paint.ClearBrush();
		Paint.SetPen( Theme.Text.WithAlpha( 0.75f ), 1.5f );
		Paint.DrawRect( rect.Shrink( 1f ), 6f );
	}

	/// <summary><paramref name="width"/> is the button's own width - wider than ButtonSize for the
	/// one button that carries a text label. It has to be passed in rather than set on the button
	/// afterwards, or the strip's own width would not account for it.</summary>
	public EffigyToolButton AddButton( EffigyIcon icon, string tip, Action clicked, float width = ButtonSize )
	{
		var button = new EffigyToolButton( this, icon, tip, clicked );
		button.FixedWidth = width;

		Layout.Add( button );
		Grew( width );

		return button;
	}

	/// <summary>A spacer standing in for the old toolbar's separators, keeping the tool groups
	/// readable. Sized to the difference so the visible gap is exactly GroupSpacing.
	///
	/// A LAYOUT CELL, NOT A WIDGET. This used to add an empty Widget, and an empty Widget paints
	/// the system background - which is where the white blocks between the right-hand tool groups
	/// came from. A spacing cell reserves the room without there being anything there to paint.</summary>
	public void AddGap()
	{
		Layout.AddSpacingCell( GroupSpacing - ButtonSpacing );
		Grew( GroupSpacing - ButtonSpacing );
	}

	/// <summary>
	/// Empty the strip so it can be filled again with a different set of tools.
	///
	/// REBUILT RATHER THAN HIDDEN, because the group gaps are layout SPACING CELLS and not widgets
	/// — there is nothing there to set Visible on. Hiding buttons alone would leave their gaps
	/// behind as holes in the strip, and _contentWidth counts what was added rather than what is
	/// showing, so the bar would also stay full width with an empty tail.
	/// </summary>
	public void Clear()
	{
		Layout.Clear( true );

		_contentWidth = 0f;
		FixedWidth = 0f;
	}
}

/// <summary>
/// A sketch-tool button in the floating sketch strip - same visual language and sizing as
/// EffigyToolButton (the feature strip's), but font-icon-drawn rather than hand-painted, and
/// checkable, since sketch tools are mutually exclusive modes rather than one-shot commands.
///
/// FONT ICONS, NOT DRAWN ONES, and that is a smaller scope than it looks. EffigyIcons only covers
/// the twelve feature-creation tools; drawing another dozen-plus glyphs for every sketch tool in
/// the same hand-painted style is real design work of its own (see WHAT-IS-LEFT.md 2.6)
/// and is deliberately not attempted here. Every name used is a CLASSIC Material Icon already
/// audited against the same s&box-ships-classic-not-Symbols problem EffigyIcons exists to dodge -
/// see WHAT-IS-BUILT.md on icons - so this does not reintroduce the blank-icon bug, it just
/// does not yet look as considered as the feature strip.
/// </summary>
/// <summary>One entry in a sketch tool's dropdown: the same kind of tool, done a different way.
/// A corner rectangle and a centre rectangle are one button in Onshape, not two.</summary>
internal sealed class SketchToolVariant
{
	public readonly EffigyIcon Icon;
	public readonly string Label;
	public readonly string Tip;
	public readonly SketchToolKind Kind;

	public SketchToolVariant( EffigyIcon icon, string label, string tip, SketchToolKind kind )
	{
		Icon = icon;
		Label = label;
		Tip = tip;
		Kind = kind;
	}
}

internal sealed class EffigySketchToolButton : Widget
{
	private EffigyIcon _icon;
	private readonly bool _checkable;
	private bool _pressed;
	private bool _pressedChevron;

	public bool Checked { get; set; }
	public Action Clicked { get; set; }

	/// <summary>Overrides the glyph colour for a button that means something in particular - the
	/// finish-sketch tick is green like every other confirm. Null leaves it as ordinary chrome.</summary>
	public Color? IconColor { get; set; }

	/// <summary>
	/// The variants this button can arm, or empty for a button that does one thing.
	///
	/// Twelve drawing tools in a row is a wall to read every time you want a circle. Onshape puts
	/// the variants of one idea behind one button - rectangle, circle, arc, polygon each have two
	/// or three ways to place them - and shows the one you used last. Four buttons come off the
	/// strip and nothing becomes unreachable.
	/// </summary>
	private readonly List<SketchToolVariant> _variants = new();

	public IReadOnlyList<SketchToolVariant> Variants => _variants;

	/// <summary>Which variant the button currently shows and arms when clicked. Onshape keeps the
	/// last one you picked on the face of the button, so the second use is a single click.</summary>
	public int Current { get; private set; }

	/// <summary>Raised when a variant is chosen, whether by clicking the button or picking from
	/// its menu.</summary>
	public Action<SketchToolVariant> VariantChosen { get; set; }

	/// <summary>Width of the strip on the right edge that opens the menu instead of arming the
	/// tool. Onshape splits its buttons the same way: glyph on the left, chevron on the right.</summary>
	private const float ChevronWidth = 15f;

	private bool HasMenu => _variants.Count > 1;

	public void SetVariants( IEnumerable<SketchToolVariant> variants )
	{
		_variants.Clear();
		_variants.AddRange( variants );

		if ( _variants.Count > 0 )
			ShowVariant( 0 );
	}

	/// <summary>Put a variant on the face of the button without arming it - used when a tool is
	/// armed from somewhere else (a keyboard shortcut, or Escape falling back to Select) and the
	/// strip has to agree with what the viewport is actually doing.</summary>
	public void ShowVariant( int index )
	{
		if ( index < 0 || index >= _variants.Count )
			return;

		Current = index;
		_icon = _variants[index].Icon;
		ToolTip = _variants[index].Tip;
		StatusTip = _variants[index].Tip;
		Update();
	}

	private void OpenVariantMenu()
	{
		var menu = new Menu( this );

		for ( var i = 0; i < _variants.Count; i++ )
		{
			var index = i;
			var variant = _variants[i];

			// NO ICON. EffigyIcon is a DRAWN glyph - EffigyIcons.Draw paints into a widget's paint
			// context - while a Menu option takes a Material Icon NAME, which is the very lookup
			// these icons exist to get away from. The label and the check mark carry the variant.
			var option = menu.AddOption( variant.Label, null, () =>
			{
				ShowVariant( index );
				VariantChosen?.Invoke( variant );
			} );

			option.Checkable = true;
			option.Checked = i == Current;
		}

		menu.OpenAtCursor();
	}

	public EffigySketchToolButton( Widget parent, EffigyIcon icon, string tip, bool checkable ) : base( parent )
	{
		_icon = icon;
		_checkable = checkable;

		ToolTip = tip;
		StatusTip = tip;
		Cursor = CursorShape.Finger;
		MouseTracking = true;

		// THE BUTTON HAS NO BACKGROUND OF ITS OWN EITHER. Only the strips set these, and a plain
		// Widget paints the system background - a white square. That went unnoticed while every
		// button painted an opaque rect over itself; the moment they stopped, the strip turned
		// into a white slab with near-white glyphs invisible on top of it. It is also what left
		// the hover glow smeared on screen after the cursor moved away: with nothing clearing the
		// widget's rect between paints, whatever was drawn last frame just stayed there.
		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedSize = new Vector2( EffigyToolStrip.ButtonSize, EffigyToolStrip.ButtonSize );
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		// Always repaint the strip background over our rect to wipe any stale glow from the
		// previous frame. Without this, TranslucentBackground leaves the old rings in the paint
		// buffer and the halo never fully disappears.
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground.WithAlpha( 0.85f ) );
		Paint.DrawRect( LocalRect, 6f );

		var hovered = IsUnderMouse;

		// NOTHING PAINTED AT REST, AND NOTHING EVER CHANGES COLOUR. The strip floats on the 3D
		// view; a box behind every button turned it into a chrome band, and swapping fills and
		// glyph colours per state made the whole row flicker as the cursor crossed it. Hover and
		// press are an edge glow, armed is an edge ring - see PaintEdgeGlow.
		if ( Checked )
			EffigyToolStrip.PaintEdgeRing( LocalRect );

		if ( _pressed || hovered )
			EffigyToolStrip.PaintEdgeGlow( LocalRect, _pressed ? 1.4f : 1f );

		// With a menu the glyph shifts left to make room for the chevron, so the two never sit on
		// top of each other and the button still reads as one thing.
		var glyphRect = HasMenu ? LocalRect.Shrink( 0, 0, ChevronWidth, 0 ) : LocalRect;

		Paint.SetPen( IconColor ?? Theme.Text );
		// Drawn rather than looked up in a font, same as the feature strip. See EffigyIcons for what
		// the font names were costing: half this row was showing a Material glyph that had nothing to
		// do with the operation behind it.
		EffigyIcons.Draw( _icon, glyphRect.Center, IconColor ?? Theme.Text, EffigyToolStrip.IconScale );

		if ( !HasMenu )
			return;

		Paint.SetPen( Theme.TextLight.WithAlpha( hovered ? 0.9f : 0.55f ) );
		Paint.DrawIcon( ChevronRect, "arrow_drop_down", 16, TextFlag.Center );
	}

	private Rect ChevronRect => new( LocalRect.Right - ChevronWidth, LocalRect.Top, ChevronWidth, LocalRect.Height );

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		// Which half was pressed decides what the release does. Recorded on PRESS so a drag that
		// starts on the chevron and ends over the glyph cannot arm a tool you did not ask for.
		_pressedChevron = HasMenu && ChevronRect.IsInside( e.LocalPosition );
		_pressed = true;

		Update();
		e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( !_pressed )
			return;

		var chevron = _pressedChevron;

		_pressed = false;
		_pressedChevron = false;
		Update();

		if ( !IsUnderMouse )
			return;

		if ( chevron )
		{
			OpenVariantMenu();
			return;
		}

		if ( _checkable )
			Checked = !Checked;

		// A button with variants arms the one on its face; anything else just does its one job.
		if ( _variants.Count > 0 )
		{
			VariantChosen?.Invoke( _variants[Current] );
			return;
		}

		Clicked?.Invoke();
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

/// <summary>
/// The floating sketch tool strip. Same shape as EffigyToolStrip - no background of its own,
/// floats on the 3D view, positioned by EffigyViewport.CompleteLayout at the identical spot the
/// feature strip uses, so showing one and hiding the other reads as one strip changing rather than
/// two unrelated pieces of chrome.
/// </summary>
internal sealed class EffigySketchStrip : Widget
{
	private const float ButtonSpacing = EffigyToolStrip.ButtonSpacing;
	private const float GroupSpacing = EffigyToolStrip.GroupSpacing;

	public EffigySketchStrip( Widget parent ) : base( parent )
	{
		Layout = Layout.Row();
		Layout.Spacing = ButtonSpacing;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedHeight = EffigyToolStrip.ButtonSize;
		FixedWidth = 0f;
	}

	/// <summary>The viewport's background, same as EffigyToolStrip.GapColor and for the same
	/// reason — see that OnPaint for why a strip cannot just decline to paint.</summary>
	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	/// <summary>Counted by hand for the same reason as EffigyToolStrip._contentWidth.</summary>
	private float _contentWidth;

	private void Grew( float cellWidth )
	{
		_contentWidth += (_contentWidth > 0f ? ButtonSpacing : 0f) + cellWidth;
		FixedWidth = _contentWidth;
	}

	public EffigySketchToolButton AddButton( EffigyIcon icon, string tip, bool checkable, Action clicked )
	{
		var button = new EffigySketchToolButton( this, icon, tip, checkable ) { Clicked = clicked };

		Layout.Add( button );
		Grew( EffigyToolStrip.ButtonSize );

		return button;
	}

	/// <summary>Same layout cell as EffigyToolStrip.AddGap, and for the same reason - an empty
	/// spacer Widget paints the system background over the 3D view.</summary>
	public void AddGap()
	{
		Layout.AddSpacingCell( GroupSpacing - ButtonSpacing );
		Grew( GroupSpacing - ButtonSpacing );
	}
}


/// <summary>One square of the strip — a hand-painted icon button, EffigyToolStrip.ButtonSize
/// square, transparent at rest and picking up the editor's own button states on hover/press.</summary>
internal sealed class EffigyToolButton : Widget
{
	private readonly EffigyIcon _icon;
	private readonly Action _clicked;
	private bool _pressed;
	public string Label { get; set; }

	/// <summary>Draw the little chevron that says this button opens a list rather than doing one
	/// thing. Set by the strip for the tools that have variants behind them.</summary>
	public bool HasMenu { get; set; }

	public EffigyToolButton( Widget parent, EffigyIcon icon, string tip, Action clicked ) : base( parent )
	{
		_icon = icon;
		_clicked = clicked;

		ToolTip = tip;
		StatusTip = tip;
		Cursor = CursorShape.Finger;
		MouseTracking = true;

		// THE BUTTON HAS NO BACKGROUND OF ITS OWN EITHER. Only the strips set these, and a plain
		// Widget paints the system background - a white square. That went unnoticed while every
		// button painted an opaque rect over itself; the moment they stopped, the strip turned
		// into a white slab with near-white glyphs invisible on top of it. It is also what left
		// the hover glow smeared on screen after the cursor moved away: with nothing clearing the
		// widget's rect between paints, whatever was drawn last frame just stayed there.
		TranslucentBackground = true;
		NoSystemBackground = true;

		FixedSize = new Vector2( EffigyToolStrip.ButtonSize, EffigyToolStrip.ButtonSize );
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		// Always repaint the strip background over our rect to wipe any stale glow from the
		// previous frame. Without this, TranslucentBackground leaves the old rings in the paint
		// buffer and the halo never fully disappears.
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground.WithAlpha( 0.85f ) );
		Paint.DrawRect( LocalRect, 6f );

		var hovered = IsUnderMouse;

		// NOTHING PAINTED AT REST, AND NOTHING EVER CHANGES COLOUR - see
		// EffigySketchToolButton.OnPaint. Hover and press are an edge glow and nothing else; the
		// glyph is drawn in exactly the same colour in every state.
		if ( _pressed || hovered )
			EffigyToolStrip.PaintEdgeGlow( LocalRect, _pressed ? 1.4f : 1f );

		// The glyphs are authored in a nominal 18x18 box and scaled to the button by EffigyIcons
		// itself, so growing ButtonSize grows the drawing with it.
		if ( string.IsNullOrEmpty( Label ) )
		{
			EffigyIcons.Draw( _icon, LocalRect.Center, Theme.Text, EffigyToolStrip.IconScale );
		}
		else
		{
			EffigyIcons.Draw( _icon, new Vector2( 31, LocalRect.Center.y ), Theme.Text, EffigyToolStrip.LabelIconScale );

			Paint.SetDefaultFont( EffigyToolStrip.LabelFontSize, 500 );
			Paint.SetPen( Theme.Text );
			Paint.DrawText( LocalRect.Shrink( 56, 0, 8, 0 ), Label, TextFlag.LeftCenter );
		}

		if ( HasMenu )
			PaintChevron();
	}

	/// <summary>
	/// A triangle in the bottom-right corner — the same signal the sketch strip's variant buttons
	/// give, so "this one opens a list" looks the same on both strips.
	///
	/// Deliberately not subtle. The first version was five pixels at half alpha and was invisible
	/// on a 54px button: the button looked like every other one, so nothing suggested clicking it
	/// would offer a choice.
	/// </summary>
	private void PaintChevron()
	{
		const float size = 9f;
		const float inset = 4f;

		var x = LocalRect.Width - inset;
		var y = LocalRect.Height - inset;

		Paint.ClearPen();
		Paint.SetBrush( Theme.Text.WithAlpha( IsUnderMouse ? 1f : 0.8f ) );

		Paint.DrawPolygon(
			new Vector2( x - size, y ),
			new Vector2( x, y - size ),
			new Vector2( x, y ) );
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


// ============================================================================
//  The Parts list — the bodies the feature tree has actually produced, in
//  their own list below it. Onshape's Parts panel: the feature tree is the
//  RECIPE, this is the RESULT, and the two are not the same thing. Three
//  features can make one part and one pattern feature can make eight.
// ============================================================================

internal sealed class EffigyPartsPanel : Widget
{
	private PartStudio _studio;
	private readonly PartsTreeView _tree;

	/// <summary>Feature id of the part whose eye was clicked. The window owns the studio and the
	/// rebuild, so the panel reports the click rather than acting on it.</summary>
	public Action<string> VisibilityToggled { get; set; }

	public EffigyPartsPanel( Widget parent, PartStudio studio ) : base( parent )
	{
		Name = "Parts";
		WindowTitle = "Parts";

		_studio = studio;
		Layout = Layout.Column();

		var header = new Widget( this ) { Layout = Layout.Row() };
		header.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		header.Layout.Spacing = 8;
		header.Layout.Add( new Editor.Label( "Parts" ) { FixedWidth = 80 } );
		header.Layout.Add( new Editor.Label( "" ), 1 );
		Layout.Add( header );

		_tree = new PartsTreeView( this );
		Layout.Add( _tree, 1 );

		// Tall enough for a few parts without taking the feature tree's room - the tree above it
		// is the one that grows.
		MinimumHeight = 118f;

		Refresh();
	}

	public void SetStudio( PartStudio studio )
	{
		_studio = studio ?? new PartStudio();
		Refresh();
	}

	public void Refresh()
	{
		_tree.Clear();

		if ( _studio is null || _studio.Bodies.Count == 0 )
		{
			_tree.AddItem( new EmptyPartsNode() );
			return;
		}

		foreach ( var body in _studio.Bodies )
			_tree.AddItem( new PartNode( this, body ) );
	}

	private sealed class PartsTreeView : TreeView
	{
		public PartsTreeView( Widget parent ) : base( parent ) { }

		protected override bool OnItemPressed( VirtualWidget item, MouseEvent e )
		{
			if ( item.Object is PartNode node && TreeEyeIcon.WasClicked( this, item, e ) )
			{
				node.ToggleVisibility();
				return false;
			}

			return base.OnItemPressed( item, e );
		}
	}

	/// <summary>One body: name, face count, and an eye.</summary>
	private sealed class PartNode : TreeNode<Body>
	{
		private readonly EffigyPartsPanel _panel;

		public PartNode( EffigyPartsPanel panel, Body body ) : base( body ) { _panel = panel; }

		/// <summary>The eye toggles the FEATURE that made this body, not the body - Body.Visible is
		/// re-derived from it on every rebuild, so setting it here would last until the next
		/// parameter tick and no longer. One consequence worth knowing: hiding a part made by a
		/// pattern hides every copy that pattern produced, because they share a feature.</summary>
		public void ToggleVisibility() => _panel.VisibilityToggled?.Invoke( Value.FeatureId );

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			var visible = Value.Visible;

			Paint.SetPen( visible ? Theme.Green.WithAlpha( 0.8f ) : Theme.TextLight.WithAlpha( 0.5f ) );
			Paint.DrawIcon( item.Rect, "view_in_ar", 14, TextFlag.LeftCenter );

			Paint.SetPen( visible ? Theme.Text : Theme.TextLight );
			Paint.DrawText( item.Rect.Shrink( 22, 0, TreeEyeIcon.SecondaryTextRightMargin, 0 ),
				Value.Name ?? "Part", TextFlag.LeftCenter );

			// Always drawn, same as the Features tree's attachment label — the shared margin
			// already keeps it clear of the eye, so there is no need to make it vanish and
			// reappear on hover the way this row used to.
			Paint.SetPen( Theme.TextLight.WithAlpha( 0.6f ) );
			Paint.DrawText( item.Rect.Shrink( 0, 0, TreeEyeIcon.SecondaryTextRightMargin, 0 ),
				$"{Value.Mesh?.FaceCount ?? 0}", TextFlag.RightCenter );

			TreeEyeIcon.Draw( _panel._tree, item, visible );
		}
	}

	/// <summary>Shown instead of an empty list, because an empty panel reads as broken.</summary>
	private sealed class EmptyPartsNode : TreeNode<string>
	{
		public EmptyPartsNode() : base( "No parts yet" ) { }

		public override void OnPaint( VirtualWidget item )
		{
			Paint.SetPen( Theme.TextLight.WithAlpha( 0.6f ) );
			Paint.DrawText( item.Rect.Shrink( 8, 0, 0, 0 ), Value, TextFlag.LeftCenter );
		}
	}
}
