using System;
using System.Collections.Generic;
using Editor;
using Sandbox;

namespace Marionette.EditorTools;

/// <summary>
/// [EditorApp] only takes an icon *name* (or a content path), so neither of this addon's apps can
/// declare its real mark there — Effigy's would draw the stock cube and Marionette's the stock
/// accessibility_new figure. After the menus exist we stamp the proper pixmap onto each Tools
/// option via Option.SetIcon(Pixmap).
///
/// WHY THIS RETRIES INSTEAD OF STAMPING ONCE. Every ingredient this needs arrives on its own
/// schedule: the MenuBar is built by the editor, the Tools menu is filled by whoever registers
/// into it, and Effigy's icon is read through Project.Current, which is null until the project is
/// resolved. Firing on editor.created caught a moment when any of those could still be missing,
/// and the old code answered that by returning quietly — so the icon was simply absent until an
/// unrelated hotload happened to re-run Apply and find everything ready. That is the "blank
/// sometimes, fine next launch" behaviour. Now a failed attempt just leaves the entry unstamped
/// and the frame event tries again, so the ordering stops mattering.
///
/// BOTH APPS GO THROUGH HERE for that same reason. The retry above is the whole value of this
/// file; a second copy of it for Marionette would be a second copy to get subtly wrong.
/// </summary>
internal static class ToolsMenuIcons
{
	/// <summary>One Tools-menu entry to stamp: the option's text, and how to build its pixmap.
	/// The factory is only ever called until it returns non-null, and the result is cached.</summary>
	sealed class Entry
	{
		public string Option;
		public Func<Pixmap> Factory;
		public Pixmap Icon;
		public bool Stamped;
	}

	static readonly Entry[] Entries =
	{
		new() { Option = "Effigy", Factory = EffigyWindow.AppIcon },
		new() { Option = "Marionette", Factory = () => MarionetteIcon.Build() },
	};

	/// <summary>Frames spent with something still unstamped, used to complain exactly once instead
	/// of never (the old silence) or every frame. At editor framerate this is a few seconds.</summary>
	static int _attempts;

	const int ComplainAfter = 600;

	[Event( "editor.created" )]
	static void OnEditorCreated( EditorMainWindow window ) => Rearm();

	[Event( "hotloaded" )]
	[Event( "refresh" )]
	static void OnReload() => Rearm();

	/// <summary>Menus may have been rebuilt from under us, so drop the cached pixmaps along with
	/// the latches and let the frame event re-resolve everything.</summary>
	static void Rearm()
	{
		foreach ( var entry in Entries )
		{
			entry.Stamped = false;
			entry.Icon = null;
		}

		_attempts = 0;
	}

	[EditorEvent.Frame]
	static void OnFrame()
	{
		var pending = false;

		foreach ( var entry in Entries )
		{
			if ( entry.Stamped )
				continue;

			if ( TryApply( entry ) )
				entry.Stamped = true;
			else
				pending = true;
		}

		if ( !pending )
			return;

		if ( ++_attempts == ComplainAfter )
		{
			var missing = new List<string>();
			foreach ( var entry in Entries )
				if ( !entry.Stamped )
					missing.Add( entry.Option );

			Log.Warning( $"Toolshed: could not stamp Tools menu icons for {string.Join( ", ", missing )} — no icon could be built, or the menu option was never registered. Those entries keep their default icons." );
		}
	}

	static bool TryApply( Entry entry )
	{
		// EditorWindow, not SceneViewWidget.Current.GetWindow(). GetWindow() gives back a plain
		// Widget, which has no MenuBar on it — the scene view does not know it is inside the main
		// window. EditorWindow is the editor's own static handle on that window (it is what
		// EditorMainWindow hands out, statically imported through the csproj's GlobalToolsNamespace
		// using), and it is typed, so MenuBar is right there.
		if ( EditorWindow?.MenuBar is not { } bar )
			return false;

		entry.Icon ??= entry.Factory();
		if ( entry.Icon is null )
			return false;

		// FindOrCreateMenu is the only lookup MenuBar exposes — there is no plain FindMenu. On a
		// frame before the editor has built its Tools menu this creates an empty one, which is
		// harmless: the editor's own registration calls FindOrCreateMenu too and so fills this
		// very object, and until it does the GetOption check below keeps the retry alive.
		var tools = bar.FindOrCreateMenu( "Tools" );
		if ( tools is null )
			return false;

		// GetOption, not GetAllOptionsRecursive. The recursive one is INTERNAL to the editor
		// assembly — it shows up in Sandbox.Tools.xml because the doc file carries internals too,
		// which is exactly what makes reading those docs a trap from out here. GetOption is public,
		// takes the option's text, and is what this wanted anyway: one option, by name.
		if ( tools.GetOption( entry.Option ) is not { } option )
			return false;

		option.SetIcon( entry.Icon );
		return true;
	}
}
