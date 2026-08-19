using Editor;
using Marionette.ShaderForge;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.ShaderForgeEditor;

/// <summary>
/// The right-hand side of the tool: describe an effect, generate it, see what was understood,
/// tweak the result, save it.
///
/// The reporting is not decoration. A keyword generator's failure mode is producing something
/// plausible for a description it only half understood, and the user having no way to tell. So
/// this panel always shows three things after a Generate: which blocks were used, which were
/// dropped for conflicting, and which of their words meant nothing to the library.
/// </summary>
internal sealed class ShaderForgeGeneratorPanel : Widget
{
	private readonly TextEdit _description;
	private readonly Widget _report;
	private readonly Widget _tweaks;
	private readonly Editor.Label _status;
	private readonly LineEdit _fileName;
	private readonly Button _saveButton;

	private GenerationResult _result;

	/// <summary>Raised with the material to show once a generation previews successfully, or null
	/// when there is nothing to show.</summary>
	public Action<Material> PreviewMaterial { get; set; }

	/// <summary>Raised with a one-line status for the window's status bar.</summary>
	public Action<string> StatusChanged { get; set; }

	/// <summary>Live values for every parameter, so a tweak survives the panel rebuilding.</summary>
	private readonly Dictionary<string, float> _floats = new();
	private readonly Dictionary<string, Color> _colors = new();

	/// <summary>The material currently previewing the generated shader, if it compiled.</summary>
	private Material _material;

	public ShaderForgeGeneratorPanel( Widget parent ) : base( parent )
	{
		Name = "Generator";
		WindowTitle = "Generator";
		SetWindowIcon( "auto_awesome" );

		MinimumWidth = 320;
		Layout = Layout.Column();
		Layout.Margin = new Sandbox.UI.Margin( 8 );
		Layout.Spacing = 8;

		// --- description ---------------------------------------------------------------------

		Layout.Add( new Editor.Label( "Describe the effect you want" ) );

		_description = new TextEdit( this )
		{
			PlaceholderText =
				"e.g. glowing neon edges that pulse\n" +
				"     dissolving with fire\n" +
				"     frosted glass with refraction",
			MinimumHeight = 76,
			MaximumHeight = 140,
		};

		Layout.Add( _description );

		var buttonRow = Layout.AddRow();
		buttonRow.Spacing = 6;

		buttonRow.Add( new Button.Primary( "Generate", "auto_awesome" ) { Clicked = Generate }, 1 );
		buttonRow.Add( new Button( "Clear", "backspace" ) { Clicked = Clear } );

		// --- what it understood ---------------------------------------------------------------

		_report = new Widget( this ) { Layout = Layout.Column() };
		_report.Layout.Spacing = 4;
		Layout.Add( _report );

		// --- tweaks ----------------------------------------------------------------------------

		_tweaks = new Widget( this ) { Layout = Layout.Column() };
		_tweaks.Layout.Spacing = 3;
		Layout.Add( _tweaks );

		Layout.AddStretchCell();

		// --- save --------------------------------------------------------------------------------

		Layout.Add( new Editor.Label( "Save as" ) );

		var saveRow = Layout.AddRow();
		saveRow.Spacing = 6;

		_fileName = new LineEdit( "", this ) { PlaceholderText = "my_shader" };
		saveRow.Add( _fileName, 1 );
		saveRow.Add( new Editor.Label( ShaderForge.ShaderTemplate.Extension ) );

		_saveButton = new Button( "Save to project", "save" ) { Clicked = Save, Enabled = false };
		Layout.Add( _saveButton );

		_status = new Editor.Label( "" ) { WordWrap = true };
		Layout.Add( _status );

		ShowIdle();
	}

	// --- generate ---------------------------------------------------------------------------

	private void Generate()
	{
		_result = ShaderForgeGenerator.Generate( _description.PlainText );

		_floats.Clear();
		_colors.Clear();

		BuildReport();
		BuildTweaks();

		_saveButton.Enabled = _result.Success;

		if ( !_result.Success )
		{
			_material = null;
			PreviewMaterial?.Invoke( null );
			SetStatus( "Nothing generated." );
			return;
		}

		if ( string.IsNullOrWhiteSpace( _fileName.Text ) )
			_fileName.Text = ShaderTemplate.SafeFileName( _result.SuggestedName );

		PreviewGenerated();
	}

	/// <summary>
	/// Writing the shader IS the preview path.
	///
	/// A .shader has to be compiled by the asset pipeline before a material can exist for it, and
	/// the pipeline compiles files on disk. So Generate writes to the project, then asks for a
	/// material — which also means the thing being previewed is exactly the file that gets saved,
	/// with no separate in-memory path that could drift from it.
	/// </summary>
	private void PreviewGenerated()
	{
		var name = string.IsNullOrWhiteSpace( _fileName.Text )
			? _result.SuggestedName
			: _fileName.Text;

		var written = ShaderForgeBridge.Write( name, _result.ShaderSource, out var error );

		if ( written is null )
		{
			SetStatus( $"Generated, but could not write it: {error}" );
			return;
		}

		_material = ShaderForgeBridge.MaterialFromShader( ShaderForgeBridge.RelativePathFor( name ) );

		if ( !_material.IsValid() )
		{
			SetStatus(
				$"Wrote {ShaderForgeBridge.RelativePathFor( name )}. It has not compiled yet — " +
				"the preview will fill in once the asset system picks it up, or check the console " +
				"for a shader compile error." );
			PreviewMaterial?.Invoke( null );
			return;
		}

		ApplyAllParams();
		PreviewMaterial?.Invoke( _material );

		SetStatus( $"Previewing {ShaderForgeBridge.RelativePathFor( name )}." );
	}

	private void Clear()
	{
		_description.PlainText = "";
		_result = null;
		_material = null;
		_saveButton.Enabled = false;

		BuildReport();
		BuildTweaks();

		PreviewMaterial?.Invoke( null );
		ShowIdle();
	}

	private void ShowIdle() => SetStatus( "Describe an effect, then press Generate." );

	private void SetStatus( string text )
	{
		if ( _status.IsValid() )
			_status.Text = text;

		StatusChanged?.Invoke( text );
	}

	// --- the report -------------------------------------------------------------------------

	private void BuildReport()
	{
		_report.Layout.Clear( true );

		if ( _result is null )
			return;

		if ( _result.Blocks.Count > 0 )
		{
			_report.Layout.Add( new Editor.Label( $"Using {_result.Blocks.Count} block" +
				$"{(_result.Blocks.Count == 1 ? "" : "s")}" ) );

			foreach ( var match in _result.Blocks )
			{
				var matched = string.Join( ", ", match.MatchedKeywords );

				_report.Layout.Add( new Editor.Label( $"  ✓ {match.Block.Title} — {matched}" )
				{
					ToolTip = match.Block.Summary,
					WordWrap = true,
				} );
			}
		}

		foreach ( var rejection in _result.Rejected )
		{
			_report.Layout.Add( new Editor.Label( $"  ✕ {rejection.Block.Title} — {rejection.Reason}" )
			{
				WordWrap = true,
			} );
		}

		// The "no block for that yet" message the design doc asks for. Shown even on a successful
		// generation, because a partial understanding is the case worth flagging hardest.
		if ( _result.UnmatchedTerms.Count > 0 )
		{
			_report.Layout.Add( new Editor.Label(
				$"No block yet for: {string.Join( ", ", _result.UnmatchedTerms )}" )
			{
				WordWrap = true,
			} );
		}
	}

	// --- tweak controls ---------------------------------------------------------------------

	/// <summary>
	/// Controls built from the generator's own parameter list rather than from a schema read back
	/// off the compiled shader.
	///
	/// The generator declared every one of these, so it already knows their names, types, ranges
	/// and defaults exactly — reading them back would be a round trip through an API whose shape
	/// this tool is not certain of, to learn something it already knew.
	/// </summary>
	private void BuildTweaks()
	{
		_tweaks.Layout.Clear( true );

		if ( _result is null || !_result.Success || _result.Params.Count == 0 )
			return;

		_tweaks.Layout.Add( new Editor.Label( "Tweak" ) );

		foreach ( var block in _result.Blocks )
		{
			if ( block.Block.Params.Length == 0 )
				continue;

			_tweaks.Layout.Add( new Editor.Label( block.Block.Title ) );

			foreach ( var param in block.Block.Params )
				_tweaks.Layout.Add( BuildParamRow( param ) );
		}
	}

	private Widget BuildParamRow( ShaderParam param )
	{
		var row = new Widget( _tweaks ) { Layout = Layout.Row() };
		row.Layout.Spacing = 6;
		row.Layout.Add( new Editor.Label( param.Label ) { FixedWidth = 130 } );

		switch ( param.Kind )
		{
			case ShaderParamKind.Color:
			{
				var start = new Color( param.DefaultColor[0], param.DefaultColor[1], param.DefaultColor[2] );
				_colors[param.Name] = start;

				var picker = new ColorPicker( row ) { Value = start };

				picker.ValueChanged = value =>
				{
					_colors[param.Name] = value;
					ShaderForgeBridge.SetParam( _material, param, 0f, value, false );
				};

				row.Layout.Add( picker, 1 );
				break;
			}

			case ShaderParamKind.Bool:
			{
				_floats[param.Name] = param.Default;

				var toggle = new Checkbox( "" ) { Value = param.Default > 0.5f };

				toggle.Toggled = () =>
				{
					_floats[param.Name] = toggle.Value ? 1f : 0f;
					ShaderForgeBridge.SetParam( _material, param, 0f, default, toggle.Value );
				};

				row.Layout.Add( toggle, 1 );
				break;
			}

			default:
			{
				_floats[param.Name] = param.Default;

				var slider = new FloatSlider( row )
				{
					Minimum = param.Min,
					Maximum = param.Max,
					Step = (param.Max - param.Min) / 200f,
					Value = param.Default,
				};

				slider.OnValueEdited = () =>
				{
					_floats[param.Name] = slider.Value;
					ShaderForgeBridge.SetParam( _material, param, slider.Value, default, false );
				};

				row.Layout.Add( slider, 1 );
				break;
			}
		}

		return row;
	}

	/// <summary>Push every current value at a freshly created material, so a regenerate does not
	/// silently reset tweaks the user already made.</summary>
	private void ApplyAllParams()
	{
		if ( _result is null )
			return;

		foreach ( var param in _result.Params )
		{
			if ( param.Kind == ShaderParamKind.Color )
			{
				var color = _colors.TryGetValue( param.Name, out var c )
					? c
					: new Color( param.DefaultColor[0], param.DefaultColor[1], param.DefaultColor[2] );

				ShaderForgeBridge.SetParam( _material, param, 0f, color, false );
				continue;
			}

			var value = _floats.TryGetValue( param.Name, out var v ) ? v : param.Default;
			ShaderForgeBridge.SetParam( _material, param, value, default, value > 0.5f );
		}
	}

	// --- save --------------------------------------------------------------------------------

	/// <summary>
	/// Re-writes under whatever name the box currently holds.
	///
	/// Generate already wrote the file (it has to — the asset pipeline compiles from disk), so
	/// this is really "save under this name". It stays a separate button because renaming after
	/// seeing the result is the normal way people work, and because a button that says Save should
	/// exist even when it has little left to do.
	/// </summary>
	private void Save()
	{
		if ( _result is null || !_result.Success )
			return;

		var name = string.IsNullOrWhiteSpace( _fileName.Text )
			? _result.SuggestedName
			: _fileName.Text;

		var written = ShaderForgeBridge.Write( name, _result.ShaderSource, out var error );

		if ( written is null )
		{
			SetStatus( $"Could not save: {error}" );
			return;
		}

		SetStatus( $"Saved {ShaderForgeBridge.RelativePathFor( name )} — hot-reload makes it " +
			"available in the material editor." );

		PreviewGenerated();
	}
}
