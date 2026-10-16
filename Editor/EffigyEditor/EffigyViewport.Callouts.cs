using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The stack of callouts in the canvas's bottom-left corner (see EffigyCallout): newest at the
/// bottom, older ones pushed up, each gone when its time is up, when it is closed, or — for one
/// keyed to something that can be fixed, like a failed feature — when the thing is fixed.
///
/// KEYED, so the same problem never stacks up twice. A feature that fails on every rebuild while
/// you drag a slider is one callout being refreshed, not a tower of identical ones.
/// </summary>
internal sealed partial class EffigyViewport
{
	private readonly List<(string Key, EffigyCallout Callout)> _callouts = new();

	/// <summary>The most callouts on screen at once. Past this the oldest goes — a pile that
	/// covers the part is worse than losing a stale message.</summary>
	private const int MaxCallouts = 4;

	private const float CalloutGap = 6f;

	/// <summary>
	/// Show a callout, or refresh the one already showing under <paramref name="key"/>.
	/// <paramref name="lifetime"/> null keeps it until it is closed or <see cref="ClearCallout"/>
	/// takes it away.
	/// </summary>
	public void ShowCallout( string key, EffigyCalloutKind kind, string text, (string Label, Action Run)[] actions = null, float? lifetime = null )
	{
		var index = _callouts.FindIndex( c => c.Key == key );
		EffigyCallout callout;

		if ( index >= 0 && _callouts[index].Callout.IsValid() )
		{
			callout = _callouts[index].Callout;

			// Back to the bottom: it is the newest thing that happened.
			_callouts.RemoveAt( index );
		}
		else
		{
			if ( index >= 0 )
				_callouts.RemoveAt( index );

			callout = new EffigyCallout( _canvas );
		}

		callout.Show( kind, text, actions, lifetime );
		callout.Visible = true;
		_callouts.Add( (key, callout) );

		while ( _callouts.Count > MaxCallouts )
		{
			_callouts[0].Callout?.Destroy();
			_callouts.RemoveAt( 0 );
		}

		PlaceCallouts();
	}

	/// <summary>
	/// The viewport would not do what was just asked: the reason goes on the status bar as a
	/// prompt always has, and up in an amber callout so it is not mistaken for one.
	/// </summary>
	public void Refuse( string why )
	{
		SketchPromptChanged?.Invoke( why );
		ShowCallout( "refusal", EffigyCalloutKind.Refusal, why, lifetime: 6f );
	}

	public void ClearCallout( string key ) => ClearCallouts( k => k == key );

	public void ClearCallouts( Func<string, bool> which )
	{
		for ( var i = _callouts.Count - 1; i >= 0; i-- )
		{
			if ( !which( _callouts[i].Key ) )
				continue;

			_callouts[i].Callout?.Destroy();
			_callouts.RemoveAt( i );
		}
	}

	/// <summary>The keys showing now, for a caller reconciling its own set against them.</summary>
	public IEnumerable<string> CalloutKeys => _callouts.Select( c => c.Key );

	private bool CalloutsUnderMouse => _callouts.Any( c => c.Callout.IsValid() && c.Callout.IsUnderMouse );

	/// <summary>Drop the finished ones and stack the rest up from the bottom-left. Every frame.</summary>
	private void PlaceCallouts()
	{
		for ( var i = _callouts.Count - 1; i >= 0; i-- )
		{
			var callout = _callouts[i].Callout;

			if ( callout.IsValid() && !callout.Expired )
				continue;

			callout?.Destroy();
			_callouts.RemoveAt( i );
		}

		var y = _canvas.Height - OverlayMargin.y;

		for ( var i = _callouts.Count - 1; i >= 0; i-- )
		{
			var callout = _callouts[i].Callout;
			y -= callout.Height;

			var at = new Vector2( OverlayMargin.x, y );

			if ( (callout.Position - at).Length > 0.5f )
				callout.Position = at;

			y -= CalloutGap;
		}
	}
}
