using Editor;
using Sandbox;

namespace Marionette.ShaderForgeEditor;

/// <summary>
/// Shader Forge — a shader previewer paired with a keyword-driven generator.
///
/// Describe an effect in plain English, get a working .shader file, see it on a model straight away,
/// tweak it, save it. The generator works from a library of composable HLSL blocks rather than a
/// language model, so what it produces is deterministic, readable, and yours to edit — see
/// Editor/ShaderForge for the kernel and WHAT-IS-BUILT.md for the scope.
///
/// Layout follows the design doc: preview on the left and centre, generator on the right.
/// </summary>
[EditorApp( "Shader Forge", "auto_awesome", "Shader previewer and keyword-driven shader generator" )]
public sealed class ShaderForgeWindow : DockWindow
{
	private ShaderForgeViewport _viewport;
	private ShaderForgePreviewPanel _previewPanel;
	private ShaderForgeGeneratorPanel _generatorPanel;

	private DockWidget _centralDock;
	private StatusBar _statusWidget;
	private Editor.Label _statusInfoLabel;
	private Editor.Label _statusMessageLabel;

	public ShaderForgeWindow()
	{
		DeleteOnClose = true;
		Size = new Vector2( 1440, 900 );
		SetWindowIcon( "auto_awesome" );

		BuildMenuBar();
		BuildDocks();
		BuildStatusBar();

		Show();
	}

	// --- menu bar ---------------------------------------------------------------------------

	private void BuildMenuBar()
	{
		var file = MenuBar.FindOrCreateMenu( "File" );
		file.Clear();
		file.AddOption( "Close", "close", Close );

		var view = MenuBar.FindOrCreateMenu( "View" );
		view.Clear();
		view.AddOption( "Frame Camera", "center_focus_strong", () => _viewport?.FrameCamera() );

		var help = MenuBar.FindOrCreateMenu( "Help" );
		help.Clear();

		// The probe is the first thing to reach for when the preview misbehaves, so it is a menu
		// item rather than console-only trivia. See ShaderForgeBridge for why it exists.
		help.AddOption( "Check engine shader APIs", "biotech", ShaderForgeBridge.Probe );
		help.AddOption( "Try 'glowing'", "wb_incandescent", () => _generatorPanel?.CastFrom( "glowing" ) );
	}

	// --- docks ------------------------------------------------------------------------------

	private void BuildDocks()
	{
		_viewport = new ShaderForgeViewport( this );
		_previewPanel = new ShaderForgePreviewPanel( this, _viewport );
		_generatorPanel = new ShaderForgeGeneratorPanel( this );

		// The generator hands its material to the preview panel rather than straight to the
		// viewport, because the preview panel owns which slot it lands on.
		_generatorPanel.PreviewMaterial = material => _previewPanel?.SetPreviewMaterial( material );
		_generatorPanel.StatusChanged = SetMessage;
		_previewPanel.WordChosen = word => _generatorPanel.CastFrom( word );

		_viewport.Ticked = () => _generatorPanel.TickWarmup();

		_centralDock = DockManager.SetCentralWidget( _viewport );

		DockManager.RegisterDock( new()
		{
			Title = "Preview",
			Icon = "visibility",
			Area = DockArea.Left,
			CreateAction = () => _previewPanel,
		} );

		DockManager.RegisterDock( new()
		{
			Title = "Generator",
			Icon = "auto_awesome",
			Area = DockArea.Right,
			CreateAction = () => _generatorPanel,
		} );

		// Bumped from ShaderForge1, which had no docks at all. A restored ShaderForge1 layout
		// would leave both panels closed and BuildDefaultLayout would never run again — the trap
		// HANDOFF.md records hitting with the Rig tool's docks.
		// ShaderForge2 hid the docks for anyone who had opened the old window. Live needs both
		// panels on screen or typing a word looks like nothing is hooked up.
		StateCookie = "ShaderForgeLive1";
	}

	protected override void BuildDefaultLayout()
	{
		var previewDock = DockManager.OpenDock( "Preview", DockArea.Left, _centralDock );
		DockManager.SetSplitterProportions( previewDock, 0.22f, 0.78f );

		var generatorDock = DockManager.OpenDock( "Generator", DockArea.Right, _centralDock );
		DockManager.SetSplitterProportions( generatorDock, 0.72f, 0.28f );

		DockManager.RaiseDock( "Preview" );
		DockManager.RaiseDock( "Generator" );
	}

	// --- status bar -------------------------------------------------------------------------

	private void BuildStatusBar()
	{
		_statusWidget = new StatusBar( this ) { Layout = Layout.Row() };
		_statusWidget.Layout.Margin = new Sandbox.UI.Margin( 8, 2 );
		_statusWidget.Layout.Spacing = 16;

		_statusWidget.Layout.Add( new Editor.Label( "Shader Forge" ) { FixedWidth = 96 } );

		_statusMessageLabel = new Editor.Label( "" );
		_statusWidget.Layout.Add( _statusMessageLabel );

		_statusWidget.Layout.AddStretchCell();

		_statusInfoLabel = new Editor.Label( "" );
		_statusWidget.Layout.Add( _statusInfoLabel );

		_viewport.ModelInfoChanged = info =>
		{
			if ( _statusInfoLabel.IsValid() )
				_statusInfoLabel.Text = info;
		};

		// The viewport loaded its default primitive in its own constructor, before this callback
		// existed to hear about it — seed the label from its current state.
		_statusInfoLabel.Text = _viewport.ModelInfo;

		StatusBar = _statusWidget;
	}

	private void SetMessage( string text )
	{
		if ( _statusMessageLabel.IsValid() )
			_statusMessageLabel.Text = text;
	}
}
