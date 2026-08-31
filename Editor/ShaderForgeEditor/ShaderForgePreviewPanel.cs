using Editor;
using Marionette.ShaderForge;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.ShaderForgeEditor;

/// <summary>
/// The preview-side controls: what the shader is being shown on, which of its material slots the
/// preview material lands on, and a way to put an existing hand-written .shader on it.
///
/// That last part is why this panel is worth having before the generator exists at all — loading
/// a .shader from the project and inspecting what it exposes makes the tool a working shader
/// previewer on its own, independent of anything being generated.
/// </summary>
internal sealed class ShaderForgePreviewPanel : Widget
{
	private readonly ShaderForgeViewport _viewport;

	private readonly ComboBox _modelPicker;
	private readonly Widget _slotList;
	private readonly LineEdit _shaderPath;
	private readonly Widget _schemaList;
	private readonly Editor.Label _slotNote;

	private List<ShaderForgeModelEntry> _entries = new();

	/// <summary>Which material slot the preview material is applied to. -1 means the whole model,
	/// which is also the fallback when per-slot overriding is not available.</summary>
	private int _targetSlot = -1;

	/// <summary>The material the generator last handed over, so switching model or slot re-applies
	/// it rather than losing it.</summary>
	private Material _current;

	/// <summary>Tap a lexicon word — the spell panel fills it in.</summary>
	public Action<string> WordChosen { get; set; }

	public ShaderForgePreviewPanel( Widget parent, ShaderForgeViewport viewport ) : base( parent )
	{
		_viewport = viewport;

		Name = "Preview";
		WindowTitle = "Preview";
		SetWindowIcon( "visibility" );

		MinimumWidth = 260;
		Layout = Layout.Column();
		Layout.Margin = new Sandbox.UI.Margin( 8 );
		Layout.Spacing = 8;

		// --- model ------------------------------------------------------------------------------

		Layout.Add( new Editor.Label( "Model" ) );

		_modelPicker = new ComboBox( this );
		Layout.Add( _modelPicker );

		var modelButtons = Layout.AddRow();
		modelButtons.Spacing = 6;
		modelButtons.Add( new Button( "Rescan project", "refresh" ) { Clicked = Rescan }, 1 );
		modelButtons.Add( new Button( "Frame", "center_focus_strong" ) { Clicked = () => _viewport.FrameCamera() } );

		// --- material slots -----------------------------------------------------------------------

		Layout.Add( new Editor.Label( "Material slots" ) );

		_slotNote = new Editor.Label( "" ) { WordWrap = true };
		Layout.Add( _slotNote );

		_slotList = new Widget( this ) { Layout = Layout.Column() };
		_slotList.Layout.Spacing = 2;
		Layout.Add( _slotList );

		// --- existing shader ----------------------------------------------------------------------

		Layout.Add( new Editor.Label( "Words this tool knows — tap one" ) );

		var lexicon = new ScrollArea( this )
		{
			VerticalScrollbarMode = ScrollbarMode.Auto,
			HorizontalScrollbarMode = ScrollbarMode.Off,
			MinimumHeight = 180,
		};
		var lexiconCanvas = new Widget( this ) { Layout = Layout.Column() };
		lexiconCanvas.Layout.Spacing = 3;
		lexicon.Canvas = lexiconCanvas;
		Layout.Add( lexicon, 1 );

		foreach ( var block in BlockLibrary.All )
		{
			var word = block.Keywords[0];
			var button = new Button( $"{block.Title}    {word}" )
			{
				ToolTip = $"{block.Lesson ?? block.Summary}\nWords: {string.Join( ", ", block.Keywords )}",
				Clicked = () => WordChosen?.Invoke( word ),
			};
			lexiconCanvas.Layout.Add( button );
		}

		Layout.Add( new Editor.Label( "Shaders already in this project" ) );

		_shaderPath = new LineEdit( "", this ) { PlaceholderText = "shaders/custom/my_shader.shader" };
		Layout.Add( _shaderPath );

		var shelf = new Widget( this ) { Layout = Layout.Column() };
		shelf.Layout.Spacing = 2;
		Layout.Add( shelf );

		foreach ( var path in ShaderForgeBridge.ProjectShaders().Take( 24 ) )
		{
			var relative = path;
			shelf.Layout.Add( new Button( relative )
			{
				Clicked = () =>
				{
					_shaderPath.Text = relative;
					LoadExisting();
				},
			} );
		}

		Layout.Add( new Button( "Load and apply", "folder_open" ) { Clicked = LoadExisting } );

		_schemaList = new Widget( this ) { Layout = Layout.Column() };
		_schemaList.Layout.Spacing = 2;
		Layout.Add( _schemaList );

		Layout.AddStretchCell();

		Rescan();
	}

	// --- model picking ------------------------------------------------------------------------

	private void Rescan()
	{
		_entries = ShaderForgeModelLibrary.Build();

		_modelPicker.Clear();

		for ( var i = 0; i < _entries.Count; i++ )
		{
			var index = i;
			var entry = _entries[index];

			_modelPicker.AddItem( entry.Label, "", () => SelectModel( index ), "", index == 0, true );
		}

		SelectModel( 0 );
	}

	private void SelectModel( int index )
	{
		if ( index < 0 || index >= _entries.Count )
			return;

		var entry = _entries[index];
		var model = ShaderForgeModelLibrary.Resolve( entry );

		if ( model is null )
			return;

		_viewport.SetModel( model, entry.IsPrimitive ? entry.Label : null );

		_targetSlot = -1;
		BuildSlotList();
		ReapplyMaterial();
	}

	// --- slots ------------------------------------------------------------------------------

	private void BuildSlotList()
	{
		_slotList.Layout.Clear( true );

		var slots = ShaderForgeModelLibrary.MaterialSlots( _viewport.CurrentModel );

		_slotList.Layout.Add( BuildSlotRow( "Whole model", -1 ) );

		for ( var i = 0; i < slots.Count; i++ )
			_slotList.Layout.Add( BuildSlotRow( $"{i}: {slots[i]}", i ) );

		_slotNote.Text = slots.Count > 1
			? "Apply the previewed shader to one slot, or to the whole model."
			: "This model has a single material.";
	}

	private Widget BuildSlotRow( string label, int slotIndex )
	{
		var row = new Widget( _slotList ) { Layout = Layout.Row() };
		row.Layout.Spacing = 6;

		var toggle = new Checkbox( label ) { Value = slotIndex == _targetSlot };

		toggle.Toggled = () =>
		{
			_targetSlot = slotIndex;

			// Radio behaviour: only one target at a time. Checkbox has no group concept, so the
			// exclusivity is enforced by rebuilding.
			BuildSlotList();
			ReapplyMaterial();
		};

		row.Layout.Add( toggle, 1 );
		return row;
	}

	/// <summary>Put the current preview material wherever the slot selection now says.</summary>
	private void ReapplyMaterial()
	{
		if ( !_current.IsValid() )
		{
			_viewport.SetMaterialOverride( null );
			return;
		}

		if ( _targetSlot < 0 )
		{
			_viewport.SetMaterialOverride( _current );
			return;
		}

		if ( _viewport.TrySetSlotOverride( _targetSlot, _current ) )
			return;

		// Per-slot targeting is unavailable in this build. Say so rather than appearing to have
		// done it — painting every slot when one was asked for looks like the tool ignoring input.
		_viewport.SetMaterialOverride( _current );
		_slotNote.Text = "Per-slot overriding is not available in this engine build — the shader " +
			"was applied to the whole model instead.";
	}

	/// <summary>Called by the window when the generator produces (or clears) a material.</summary>
	public void SetPreviewMaterial( Material material )
	{
		_current = material;
		ReapplyMaterial();
	}

	// --- existing shaders ---------------------------------------------------------------------

	/// <summary>
	/// Put a hand-written .shader on the preview model and list what it exposes.
	///
	/// The schema read is best-effort by design — see ShaderForgeBridge.ReadSchema. If it comes
	/// back empty the shader still previews; you just do not get the parameter list.
	/// </summary>
	private void LoadExisting()
	{
		_schemaList.Layout.Clear( true );

		var path = _shaderPath.Text?.Trim();

		if ( string.IsNullOrWhiteSpace( path ) )
		{
			_schemaList.Layout.Add( new Editor.Label( "Enter a shader path first." ) { WordWrap = true } );
			return;
		}

		var material = ShaderForgeBridge.MaterialFromShader( path );

		if ( !material.IsValid() )
		{
			_schemaList.Layout.Add( new Editor.Label(
				$"Could not build a material from {path}. Check the path and that it has compiled." )
			{
				WordWrap = true,
			} );

			return;
		}

		SetPreviewMaterial( material );

		var shader = ShaderForgeBridge.LoadShader( path );
		var schema = ShaderForgeBridge.ReadSchema( shader );

		if ( schema.Count == 0 )
		{
			_schemaList.Layout.Add( new Editor.Label(
				"Applied. Could not read its parameter schema — run `shaderforge_probe` to see " +
				"whether this engine build exposes Shader.Schema as assumed." )
			{
				WordWrap = true,
			} );

			return;
		}

		_schemaList.Layout.Add( new Editor.Label( $"Exposes {schema.Count} entries" ) );

		foreach ( var entry in schema.Take( 40 ) )
			_schemaList.Layout.Add( new Editor.Label( $"  {entry.Type}: {entry.Name}" ) { WordWrap = true } );
	}
}
