using Editor;
using Effigy;
using Sandbox;
using System;
using System.IO;

namespace Marionette.EditorTools;

// ============================================================================
//  Build scripts on the live studio — what effigy_run (EffigyMcp.cs) calls.
//
//  One undo step for the whole script, the dialog closed first so no feature
//  is half-edited under it, and the same RebuildStudio every other change goes
//  through, so the tree, the parts, the dock and the viewport all follow.
// ============================================================================

public sealed partial class EffigyWindow
{
	/// <summary>Run a build script on the open studio as one undo step. Returns the script's log,
	/// with the failing line's diagnostic if it stopped.</summary>
	public string RunBuildScript( string script )
	{
		if ( _studio is null || string.IsNullOrWhiteSpace( script ) )
			return "Nothing to run.";

		// A script edits the model directly; anything mid-edit in the viewport would be edited
		// out from under itself.
		LeaveCurrentWorkspace();
		_dialog?.Close();

		RecordUndo();

		var baseDir = _documentPath is not null
			? Path.GetDirectoryName( Path.GetFullPath( _documentPath ) )
			: Project.Current?.GetAssetsPath() ?? Directory.GetCurrentDirectory();

		var result = BuildScript.Run( _studio, script, baseDir );

		RebuildStudio();
		SetPrompt( result.Ok ? "Script ran." : $"Script stopped at line {result.ErrorLine}: {result.Error}" );

		return result.ToString();
	}

	/// <summary>File ▸ Run Build Script…: pick a script file and run it, so a player can use the
	/// scripts Claude writes without the MCP bridge. Same path as effigy_run; the log goes to the
	/// console and a stop goes to the prompt line.</summary>
	private void RunBuildScriptFile()
	{
		var fd = new FileDialog( null )
		{
			Title = "Run build script...",
			DefaultSuffix = ".txt",
			Directory = _documentPath is not null
				? Path.GetDirectoryName( Path.GetFullPath( _documentPath ) )
				: Project.Current?.GetAssetsPath() ?? "",
		};

		fd.SetFindFile();
		fd.SetNameFilter( "Build script (*.txt *.effigyscript);;All files (*.*)" );

		if ( !fd.Execute() || string.IsNullOrWhiteSpace( fd.SelectedFile ) )
			return;

		string text;
		try
		{
			text = File.ReadAllText( fd.SelectedFile );
		}
		catch ( Exception e )
		{
			ReportFailure( $"Can't read {Path.GetFileName( fd.SelectedFile )}: {e.Message}" );
			return;
		}

		Log.Info( $"[Effigy] {Path.GetFileName( fd.SelectedFile )}:\n{RunBuildScript( text )}" );
	}

	/// <summary>Open a document by path, for the bridge. Unsaved work in the current one is kept
	/// only through Ctrl+Z — the bridge has no dialog to ask with, so it does not ask. A file that
	/// will not read says so in the window (red callout) and leaves the open studio as it was;
	/// <see cref="LastOpenFailed"/> tells the bridge.</summary>
	public void OpenDocumentPath( string path )
	{
		LeaveCurrentWorkspace();
		_dialog?.Close();
		LastOpenFailed = !LoadDocument( path );
	}

	/// <summary>True when the last <see cref="OpenDocumentPath"/> could not read its file.</summary>
	public bool LastOpenFailed { get; private set; }

	/// <summary>Save to a path, or to the document's own. Returns where it went, or why not.</summary>
	public string SaveDocumentTo( string path )
	{
		path = string.IsNullOrWhiteSpace( path ) ? _documentPath : path;

		if ( path is null )
			return "The document has never been saved: give a path.";

		if ( !path.EndsWith( StudioDocument.Extension, StringComparison.OrdinalIgnoreCase ) )
			path += StudioDocument.Extension;

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) ) ?? "." );
		}
		catch ( Exception e )
		{
			var failed = $"Can't save {Path.GetFileName( path )}: its folder could not be made ({e.Message}).";
			ReportFailure( failed );
			return failed;
		}

		// WriteDocument's own line: "Saved X.", or what failed and what did not get written.
		return WriteDocument( path );
	}
}
