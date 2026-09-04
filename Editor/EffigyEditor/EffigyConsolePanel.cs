using Editor;
using System;
using System.Reflection;

namespace Marionette.EditorTools;

/// <summary>
/// The editor's own console, docked inside Effigy.
///
/// NOT A CONSOLE OF OUR OWN, and that is the whole design. s&amp;box already has one: filtering by
/// level, a term filter, the stack trace inspector, command entry with autocomplete, and — the
/// part that actually matters here — it is where compile failures land. Writing a second one would
/// mean a second log capture, a second filter, and a second thing to keep in step with whatever
/// the engine's LogEvent grows next. It would also be a console that agrees with the real one
/// right up until the moment they disagree, which is the moment you need it.
///
/// WHY IT NEEDS ANY CODE AT ALL. `Editor.ConsoleWidget` is internal to Sandbox.Tools, so it cannot
/// be named from this assembly even though its constructor is public. Reflection is the whole of
/// what this file does: find the type, build one, put it in a layout. Everything visible in the
/// dock is the engine's widget, unmodified.
///
/// AND WHY IT PUTS `Instance` BACK. ConsoleWidget carries a static Instance that the rest of the
/// editor routes through — compile diagnostics among them. Constructing a second one points that
/// static at ours, which would quietly cost the main editor window its console. So the previous
/// value is restored the moment ours is built: the main console keeps being the one the editor
/// talks to, and ours shows the log because it hooks the logger itself, the way the first one did.
///
/// It degrades to a sentence rather than an exception. A missing internal type is exactly the kind
/// of thing an engine update changes, and a dock that says why it is empty is worth far more than
/// a window that will not open.
/// </summary>
internal sealed class EffigyConsolePanel : Widget
{
	/// <summary>The engine's console widget, or null when this build does not have one where we
	/// looked. Held as the base type because the real one cannot be named here.</summary>
	private readonly Widget _console;

	public EffigyConsolePanel( Widget parent ) : base( parent )
	{
		Name = "Console";
		WindowTitle = "Console";
		SetWindowIcon( "terminal" );

		Layout = Layout.Column();
		Layout.Margin = 0;

		_console = TryCreateEditorConsole( this );

		if ( _console is not null )
		{
			Layout.Add( _console, 1 );
			return;
		}

		var note = new Editor.Label( "This build of s&box does not expose Editor.ConsoleWidget — "
			+ "use the main editor window's console instead." )
		{
			WordWrap = true,
			Color = Theme.TextLight.WithAlpha( 0.7f ),
		};

		Layout.Margin = 12;
		Layout.Add( note );
		Layout.AddStretchCell();
	}

	/// <summary>
	/// Build the editor's console widget without being able to name its type.
	///
	/// Everything here is allowed to fail, and failing means "no console" rather than "no Effigy".
	/// The type is internal, so a rename in an engine update is a normal event rather than an
	/// exceptional one, and it must not take the whole window down with it.
	/// </summary>
	private static Widget TryCreateEditorConsole( Widget parent )
	{
		try
		{
			// Found through a type this assembly CAN name, rather than by loading Sandbox.Tools by
			// string: Editor.ConsoleSystem is public and lives in the same assembly, so this asks
			// the engine where its own console is instead of hardcoding an assembly name.
			//
			// FULLY QUALIFIED ON PURPOSE. There is also a Sandbox.ConsoleSystem, in a different
			// assembly, and both are in scope here through the global usings - resolving to that
			// one would look for Editor.ConsoleWidget in Sandbox.Engine, not find it, and leave
			// the dock showing the "not exposed" note forever with nothing to say it had guessed.
			var type = typeof( Editor.ConsoleSystem ).Assembly.GetType( "Editor.ConsoleWidget", throwOnError: false );

			if ( type is null )
				return null;

			var instance = type.GetProperty( "Instance",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static );

			// Read BEFORE constructing, because the constructor is what overwrites it.
			var previous = instance is { CanRead: true } ? instance.GetValue( null ) : null;

			var console = Activator.CreateInstance( type, parent ) as Widget;

			// Put the editor's own console back on the static the rest of the editor routes
			// through. See the type comment: leaving ours there costs the main window its console.
			if ( previous is not null && instance is { CanWrite: true } )
				instance.SetValue( null, previous );

			return console;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Effigy] could not open the editor console in a dock: {e.Message}" );
			return null;
		}
	}
}
