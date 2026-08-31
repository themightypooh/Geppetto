using Editor;
using Marionette.ShaderForge;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.ShaderForgeEditor;

/// <summary>
/// The spell side of the tool. Typing a word turns that effect on the live material immediately —
/// no Generate click, no waiting on a compile. Forge still writes a slim .shader of just the
/// blocks the description selected.
/// </summary>
internal sealed class ShaderForgeGeneratorPanel : Widget
{
	private readonly TextEdit _description;
	private readonly Widget _chips;
	private readonly Editor.Label _lesson;
	private readonly TextEdit _hlsl;
	private readonly Widget _tweaks;
	private readonly Editor.Label _status;
	private readonly LineEdit _fileName;
	private readonly Button _forgeButton;

	private GenerationResult _result;
	private Material _live;
	private float _retryAt;

	public Action<Material> PreviewMaterial { get; set; }
	public Action<string> StatusChanged { get; set; }

	private readonly Dictionary<string, float> _floats = new();
	private readonly Dictionary<string, Color> _colors = new();

	private static readonly string[] Seeds =
	{
		"glowing",
		"glowing neon that pulses",
		"dissolving with fire",
		"frosted glass",
		"cel shaded cartoon",
		"holographic projection",
		"snow on top",
		"legendary loot glow",
		"heat haze",
		"grass blowing in the wind",
		"rim light",
	};

	public ShaderForgeGeneratorPanel( Widget parent ) : base( parent )
	{
		Name = "Generator";
		WindowTitle = "Spell";
		SetWindowIcon( "auto_awesome" );

		MinimumWidth = 340;
		Layout = Layout.Column();
		Layout.Margin = new Sandbox.UI.Margin( 8 );
		Layout.Spacing = 8;

		Layout.Add( new Editor.Label( "Type a word. The model is the sentence." )
		{
			WordWrap = true,
		} );

		var seedRow = Layout.AddRow();
		seedRow.Spacing = 6;
		seedRow.Add( new Button.Primary( "Surprise me", "casino" ) { Clicked = Surprise }, 1 );
		seedRow.Add( new Button( "Clear", "backspace" ) { Clicked = Clear } );

		_description = new TextEdit( this )
		{
			PlaceholderText = "glowing",
			MinimumHeight = 64,
			MaximumHeight = 110,
		};
		_description.TextChanged += _ => Cast();
		Layout.Add( _description );

		_chips = new Widget( this ) { Layout = Layout.Column() };
		_chips.Layout.Spacing = 3;
		Layout.Add( _chips );

		_lesson = new Editor.Label( "" ) { WordWrap = true };
		Layout.Add( _lesson );

		Layout.Add( new Editor.Label( "What the GPU just did" ) );
		_hlsl = new TextEdit( this )
		{
			ReadOnly = true,
			MinimumHeight = 88,
			MaximumHeight = 140,
		};
		Layout.Add( _hlsl );

		_tweaks = new Widget( this ) { Layout = Layout.Column() };
		_tweaks.Layout.Spacing = 3;
		Layout.Add( _tweaks, 1 );

		Layout.Add( new Editor.Label( "Forge a real .shader from this" ) );

		var saveRow = Layout.AddRow();
		saveRow.Spacing = 6;
		_fileName = new LineEdit( "", this ) { PlaceholderText = "my_shader" };
		saveRow.Add( _fileName, 1 );
		saveRow.Add( new Editor.Label( ShaderTemplate.Extension ) );

		_forgeButton = new Button.Primary( "Forge", "auto_awesome" ) { Clicked = Forge, Enabled = false };
		Layout.Add( _forgeButton );

		_status = new Editor.Label( "" ) { WordWrap = true };
		Layout.Add( _status );

		WarmLive();
		Cast();
	}

	/// <summary>The viewport ticks this so a live shader that has not compiled yet gets another
	/// chance every half second without the user pressing anything.</summary>
	public void TickWarmup()
	{
		if ( _live.IsValid() )
			return;

		if ( RealTime.Now < _retryAt )
			return;

		_retryAt = RealTime.Now + 0.6f;
		WarmLive();

		if ( _live.IsValid() )
			Cast();
	}

	private void WarmLive()
	{
		_live = ShaderForgeBridge.EnsureLiveMaterial();

		if ( !_live.IsValid() )
		{
			PreviewMaterial?.Invoke( null );
			return;
		}

		ShaderForgeBridge.ApplyLive( _live, new GenerationResult(), _floats, _colors );
		PreviewMaterial?.Invoke( _live );
	}

	private void Surprise()
	{
		var rng = new Random();
		var pool = BlockLibrary.All.OrderBy( _ => rng.Next() ).ToList();
		var picked = new List<ShaderBlock>();
		var claimed = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var block in pool )
		{
			if ( block.ExclusiveGroups.Any( claimed.Contains ) )
				continue;

			picked.Add( block );

			foreach ( var group in block.ExclusiveGroups )
				claimed.Add( group );

			if ( picked.Count >= 2 + rng.Next( 2 ) )
				break;
		}

		if ( picked.Count == 0 )
			picked.Add( BlockLibrary.ById( "emissive" ) );

		_description.PlainText = string.Join( " ", picked.Select( b => b.Keywords[0] ) );
		Cast();
	}

	public void CastFrom( string phrase )
	{
		_description.PlainText = phrase;
		Cast();
	}

	private void Cast()
	{
		if ( !_live.IsValid() )
			WarmLive();

		var text = _description.PlainText ?? "";
		_result = string.IsNullOrWhiteSpace( text )
			? new GenerationResult()
			: ShaderForgeGenerator.Generate( text );

		if ( string.IsNullOrWhiteSpace( text ) )
			_result.Warnings.Clear();

		BuildChips();
		BuildLesson();
		BuildTweaks();
		BuildHlsl();

		_forgeButton.Enabled = _result.Success;

		if ( _result.Success && string.IsNullOrWhiteSpace( _fileName.Text ) )
			_fileName.Text = ShaderTemplate.SafeFileName( _result.SuggestedName );

		if ( _live.IsValid() )
		{
			ShaderForgeBridge.ApplyLive( _live, _result, _floats, _colors );
			PreviewMaterial?.Invoke( _live );
		}

		if ( string.IsNullOrWhiteSpace( text ) )
		{
			SetStatus( _live.IsValid()
				? "Waiting. Type glowing."
				: "Warming up the live shader — type anyway, it will catch up." );
			return;
		}

		if ( !_result.Success )
		{
			var hint = _result.UnmatchedTerms
				.Select( ShaderForgeGenerator.Suggest )
				.FirstOrDefault( s => s is not null );

			SetStatus( hint is null
				? "No block for that yet — tap a word on the left, or Surprise me."
				: $"No block for that yet. Did you mean {hint}?" );
			return;
		}

		var titles = string.Join( ", ", _result.Blocks.Select( b => b.Block.Title ) );
		SetStatus( _live.IsValid()
			? $"Live: {titles}."
			: $"Understood {titles}, waiting for the live shader to compile." );
	}

	private void Clear()
	{
		_description.PlainText = "";
		_floats.Clear();
		_colors.Clear();
		_fileName.Text = "";
		Cast();
	}

	private void SetStatus( string text )
	{
		if ( _status.IsValid() )
			_status.Text = text;

		StatusChanged?.Invoke( text );
	}

	// --- chips / lesson / hlsl --------------------------------------------------------------

	private void BuildChips()
	{
		_chips.Layout.Clear( true );

		if ( _result is null )
			return;

		foreach ( var match in _result.Blocks )
		{
			var words = string.Join( ", ", match.MatchedKeywords );
			_chips.Layout.Add( new Editor.Label( $"● {match.Block.Title}  ←  {words}" )
			{
				ToolTip = match.Block.Lesson ?? match.Block.Summary,
				WordWrap = true,
			} );
		}

		foreach ( var rejection in _result.Rejected )
		{
			_chips.Layout.Add( new Editor.Label( $"✕ {rejection.Block.Title} — {rejection.Reason}" )
			{
				WordWrap = true,
			} );
		}

		if ( _result.UnmatchedTerms.Count > 0 )
		{
			var named = _result.UnmatchedTerms.Select( term =>
			{
				var hint = ShaderForgeGenerator.Suggest( term );
				return hint is null ? term : $"{term} (close to {hint})";
			} );

			_chips.Layout.Add( new Editor.Label( $"No block yet for: {string.Join( ", ", named )}" )
			{
				WordWrap = true,
			} );
		}
	}

	private void BuildLesson()
	{
		if ( _result is null || !_result.Success )
		{
			_lesson.Text = "A shader is a tiny program that runs once per pixel on the model. " +
				"Each word you type turns one of those programs on.";
			return;
		}

		_lesson.Text = string.Join( "\n\n", _result.Blocks.Select( m =>
			$"{m.Block.Title}. {m.Block.Lesson ?? m.Block.Summary}" ) );
	}

	private void BuildHlsl()
	{
		if ( _result is null || !_result.Success )
		{
			_hlsl.PlainText = "";
			return;
		}

		var bits = new List<string>();

		foreach ( var match in _result.Blocks )
		{
			var snippet = match.Block.SurfaceCode
				?? match.Block.VertexCode
				?? match.Block.UvCode
				?? match.Block.PostCode
				?? "// this block drives other blocks (see SFPulse)";

			bits.Add( $"// {match.Block.Title}\n{snippet.Trim()}" );
		}

		_hlsl.PlainText = string.Join( "\n\n", bits );
	}

	private void BuildTweaks()
	{
		_tweaks.Layout.Clear( true );

		if ( _result is null || !_result.Success || _result.Params.Count == 0 )
			return;

		_tweaks.Layout.Add( new Editor.Label( "Tweak" ) );

		foreach ( var match in _result.Blocks )
		{
			if ( match.Block.Params.Length == 0 )
				continue;

			_tweaks.Layout.Add( new Editor.Label( match.Block.Title ) );

			foreach ( var param in match.Block.Params )
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
				var start = _colors.TryGetValue( param.Name, out var existing )
					? existing
					: new Color( param.DefaultColor[0], param.DefaultColor[1], param.DefaultColor[2] );

				_colors[param.Name] = start;

				var picker = new ColorPicker( row ) { Value = start };
				picker.ValueChanged = value =>
				{
					_colors[param.Name] = value;
					ShaderForgeBridge.SetParam( _live, param, 0f, value, false );
				};
				row.Layout.Add( picker, 1 );
				break;
			}

			default:
			{
				var start = _floats.TryGetValue( param.Name, out var existing ) ? existing : param.Default;
				_floats[param.Name] = start;

				var slider = new FloatSlider( row )
				{
					Minimum = param.Min,
					Maximum = param.Max,
					Step = (param.Max - param.Min) / 200f,
					Value = start,
				};
				slider.OnValueEdited = () =>
				{
					_floats[param.Name] = slider.Value;
					ShaderForgeBridge.SetParam( _live, param, slider.Value, default, slider.Value > 0.5f );
				};
				row.Layout.Add( slider, 1 );
				break;
			}
		}

		return row;
	}

	private void Forge()
	{
		if ( _result is null || !_result.Success )
			return;

		var name = string.IsNullOrWhiteSpace( _fileName.Text )
			? _result.SuggestedName
			: _fileName.Text;

		var written = ShaderForgeBridge.Write( name, _result.ShaderSource, out var error );

		if ( written is null )
		{
			SetStatus( $"Could not forge: {error}" );
			return;
		}

		SetStatus( $"Forged {ShaderForgeBridge.RelativePathFor( name )} — that's a real shader file, " +
			"hot-reload puts it in the material editor." );
	}

	internal static IReadOnlyList<string> ExamplePhrases => Seeds;
}
