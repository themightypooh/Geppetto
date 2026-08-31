using Editor;
using Sandbox;

namespace Marionette.EditorTools;

/// <summary>
/// [EditorApp] only takes an icon *name* (or a content path). The Tools dropdown was still
/// drawing the cube because that name was view_in_ar. After the menus exist we stamp the
/// green-man pixmap onto the Effigy option via Option.SetIcon(Pixmap).
/// </summary>
internal static class EffigyToolsMenuIcon
{
	[Event( "editor.created" )]
	static void OnEditorCreated( EditorMainWindow window ) => Apply( window?.MenuBar );

	[Event( "hotloaded" )]
	[Event( "refresh" )]
	static void OnReload()
	{
		// EditorWindow, not SceneViewWidget.Current.GetWindow(). GetWindow() gives back a plain
		// Widget, which has no MenuBar on it — the scene view does not know it is inside the main
		// window. EditorWindow is the editor's own static handle on that window (it is what
		// EditorMainWindow hands out, statically imported through the csproj's GlobalToolsNamespace
		// using), and it is typed, so MenuBar is right there.
		Apply( EditorWindow?.MenuBar );
	}

	static void Apply( MenuBar bar )
	{
		if ( bar is null )
			return;

		var icon = EffigyWindow.AppIcon();
		if ( icon is null )
			return;

		var tools = bar.FindOrCreateMenu( "Tools" );
		if ( tools is null )
			return;

		// GetOption, not GetAllOptionsRecursive. The recursive one is INTERNAL to the editor
		// assembly — it shows up in Sandbox.Tools.xml because the doc file carries internals too,
		// which is exactly what makes reading those docs a trap from out here. GetOption is public,
		// takes the option's text, and is what this wanted anyway: one option, by name.
		tools.GetOption( "Effigy" )?.SetIcon( icon );
	}
}
