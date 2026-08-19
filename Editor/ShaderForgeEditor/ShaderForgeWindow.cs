using Editor;
using Sandbox;

namespace Marionette.ShaderForgeEditor;

/// <summary>
/// Shader Forge — a shader previewer paired with a keyword-driven generator (see the tool design
/// doc). This is Phase 1 only: the editor window shell, a live orbit-camera preview of a stock
/// primitive under a small lighting rig, and a picker to switch between primitives. Nothing here
/// touches shaders yet — that starts at Phase 3 (manual preview) and Phase 4 (generation), each
/// building on this shell rather than the other way around.
/// </summary>
[EditorApp( "Marionette", "auto_awesome", "Shader previewer and keyword-driven shader generator" )]
public sealed class ShaderForgeWindow : DockWindow
{
	private ShaderForgeViewport _viewport;
	private StatusBar _statusWidget;
	private Editor.Label _statusInfoLabel;

	public ShaderForgeWindow()
	{
		DeleteOnClose = true;
		Size = new Vector2( 1280, 800 );
		SetWindowIcon( "auto_awesome" );

		BuildMenuBar();
		BuildDocks();
		BuildToolbar();
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
	}

	// --- toolbar ----------------------------------------------------------------------------

	/// <summary>The model picker — stock primitives only for now. Loading real project .vmdl files
	/// is Phase 2; the combo box here is built so adding those entries later is just more calls to
	/// <c>AddItem</c>, not a new control.</summary>
	private void BuildToolbar()
	{
		var bar = new ToolBar( this, "ShaderForgeToolbar" );
		bar.SetIconSize( 22 );
		AddToolBar( bar, ToolbarPosition.Top );

		bar.AddWidget( new Editor.Label( "Model" ) );

		var combo = new ComboBox( bar );

		foreach ( ShaderForgePrimitiveKind kind in System.Enum.GetValues( typeof( ShaderForgePrimitiveKind ) ) )
		{
			var k = kind;
			combo.AddItem( ShaderForgePrimitives.Label( k ), "", () => _viewport.SetPrimitive( k ), "",
				k == ShaderForgePrimitiveKind.Sphere, true );
		}

		bar.AddWidget( combo );
	}

	// --- docks ------------------------------------------------------------------------------

	private void BuildDocks()
	{
		_viewport = new ShaderForgeViewport( this );
		DockManager.SetCentralWidget( _viewport );

		// No side docks yet — the right-hand generator panel arrives with Phase 4. An empty dock
		// registered ahead of the feature it serves is a control with nothing behind it, which is
		// exactly what this project avoids elsewhere (see EffigyWindow's sketch toolbar).
		StateCookie = "ShaderForge1";
	}

	// --- status bar -------------------------------------------------------------------------

	private void BuildStatusBar()
	{
		_statusWidget = new StatusBar( this ) { Layout = Layout.Row() };
		_statusWidget.Layout.Margin = new Sandbox.UI.Margin( 8, 2 );
		_statusWidget.Layout.Spacing = 16;

		_statusWidget.Layout.Add( new Editor.Label( "Shader Forge" ) { FixedWidth = 96 } );
		_statusWidget.Layout.AddStretchCell();

		_statusInfoLabel = new Editor.Label( "" );
		_statusWidget.Layout.Add( _statusInfoLabel );

		_viewport.ModelInfoChanged = info =>
		{
			if ( _statusInfoLabel.IsValid() )
				_statusInfoLabel.Text = info;
		};

		// The viewport already loaded its default primitive in its own constructor, before this
		// callback existed to hear about it — set the label from its current state once here.
		_statusInfoLabel.Text = _viewport.ModelInfo;

		StatusBar = _statusWidget;
	}
}
