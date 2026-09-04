using Editor;
using Marionette;
using Sandbox;
using System;
using System.IO;

namespace Marionette.Tools;

/// <summary>
/// The export button's dialog: a clip name, a loop toggle, and one action.
///
/// A DIALOG RATHER THAN A SILENT FILE WRITE. Exporting a clip into a model has two names that
/// matter (the sequence AnimGraph will ask for, and whether it loops) and a destination that
/// should be visible before anything hits disk. A menu item that just wrote files would look
/// like it did nothing.
/// </summary>
internal sealed class RigAnimExportDialog : Window
{
	private readonly RigAnimDocument _doc;
	private readonly Asset _asset;
	private LineEdit _name;
	private Checkbox _loop;

	public RigAnimExportDialog( Widget owner, RigAnimDocument doc, Asset asset, bool looping )
	{
		_doc = doc;
		_asset = asset;

		Parent = owner;

		WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.CloseButton
			| WindowFlags.WindowSystemMenuHint | WindowFlags.WindowTitle;

		WindowTitle = "Export Animation";
		Size = new Vector2( 540, 420 );
		SetWindowIcon( "file_download" );

		Build( looping );
	}

	private void Build( bool looping )
	{
		var canvas = new Widget( this ) { Layout = Layout.Column() };
		canvas.Layout.Margin = 14;
		canvas.Layout.Spacing = 10;

		canvas.Layout.Add( new Editor.Label(
			"Interaction clips (open a fridge, pull a lever) usually skip this compile. Save the "
			+ ".riganim, add RigAnimPlayerComponent next to the character's SkinnedModelRenderer, "
			+ "assign the clip, uncheck Loop and Play On Start, and call Play() from your use code. "
			+ "Tween the fridge with player.NormalizedTime so the door and the hands share a clock." )
		{ WordWrap = true } );

		canvas.Layout.Add( new Editor.Label(
			"Use Export when you need a named sequence on a compiled model (AnimGraph, "
			+ "renderer.Sequence.Name). That is a different path, not a requirement." )
		{ WordWrap = true, Color = Theme.TextControl } );

		var nameRow = canvas.Layout.AddRow();
		nameRow.Spacing = 8;
		nameRow.Add( new Editor.Label( "Sequence Name" ) { FixedWidth = 110 } );

		var defaultName = Path.GetFileNameWithoutExtension( _asset?.Name ?? "clip" );
		_name = nameRow.Add( new LineEdit( defaultName ), 1 );
		_name.ToolTip = "What the game asks for: renderer.Sequence.Name = this. Does not have to match the file name.";

		var loopRow = canvas.Layout.AddRow();
		loopRow.Spacing = 8;
		_loop = loopRow.Add( new Checkbox( "Looping" ) { Value = looping } );
		_loop.ToolTip = "Idle and walk loop. Fridge-open, fire, reload usually do not.";

		var dest = DestinationHint();
		canvas.Layout.Add( new Editor.Label( dest ) { WordWrap = true, Color = Theme.TextControl } );

		canvas.Layout.AddStretchCell();

		var buttons = canvas.Layout.AddRow();
		buttons.Spacing = 8;
		buttons.AddStretchCell();
		buttons.Add( new Button( "Cancel" ) { Clicked = Close } );

		var go = buttons.Add( new Button( "Export", "file_download" ) { Clicked = Run } );
		go.ToolTip = "Write the .dmx and .vmdl next to this clip, then compile";

		Canvas = canvas;
	}

	private string DestinationHint()
	{
		var model = _doc?.SourceModel?.Name ?? "(no model)";
		var folder = string.IsNullOrEmpty( _asset?.Path )
			? "Assets"
			: Path.GetDirectoryName( _asset.Path.Replace( '/', Path.DirectorySeparatorChar ) )
				?.Replace( '\\', '/' ) ?? "Assets";

		return $"Writes into {folder}/  ·  Base Model: {model}";
	}

	private void Run()
	{
		var result = RigAnimExport.Export( _doc, _asset, _name?.Text, _loop?.Value ?? true );

		if ( !result.Ok )
		{
			RigStatusBar.Show( result.Error );
			new PopupWindow( "Export Failed", result.Error, "OK",
				new System.Collections.Generic.Dictionary<string, System.Action>
				{
					{ "OK", () => { } }
				} ).Show();
			return;
		}

		var body = result.Compiled
			? $"Compiled {result.VmdlAssetPath}\n\n"
				+ $"Sequence playback:\n"
				+ $"  renderer.UseAnimGraph = false;\n"
				+ $"  renderer.Sequence.Name = \"{result.SequenceName}\";\n\n"
				+ $"Interaction clips (fridge, lever, pickup) are usually easier without this file:\n"
				+ $"  add RigAnimPlayerComponent, assign the .riganim, Loop off, Play On Start off,\n"
				+ $"  call Play() on use, tween the prop with player.NormalizedTime.\n\n"
				+ $"{result.Frames} frames, {result.MatchedBones}/{result.SkeletonBones} bones posed."
			: $"Wrote {result.VmdlAssetPath} but compile failed — check the compiler output.\n\n"
				+ $"The animation file is at {result.DmxAssetPath}. You can add it in ModelDoc "
				+ $"as an AnimFile if you want to finish this by hand.";

		RigStatusBar.Show( result.Compiled
			? $"Exported {result.VmdlAssetPath} — Sequence \"{result.SequenceName}\""
			: $"Wrote {result.VmdlAssetPath} but compile failed" );

		new PopupWindow( "Export Complete", body, "OK",
			new System.Collections.Generic.Dictionary<string, System.Action>
			{
				{ "OK", () => { } }
			} ).Show();

		Close();
	}
}
