using Editor;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

internal enum EffigyCalloutKind
{
	/// <summary>Something failed — a feature that built nothing, an export or save that threw.</summary>
	Error,

	/// <summary>The editor would not do what was asked, and says why — "click a body first".</summary>
	Refusal,
}

/// <summary>
/// One message in the viewport's bottom-left stack: a dark panel with a coloured left edge, an
/// icon, the message, and buttons for what to do about it. Red for an error, amber for a refusal.
///
/// THIS IS FOR WHEN SOMETHING WENT WRONG, not for prompts. The status bar already says what the
/// active tool wants next; a refusal used to go to the same line, in the same grey, and read as
/// one more prompt — or to the console, where nobody looks. A callout is the editor saying it
/// could not do the thing you just tried.
///
/// PAINTED WHOLE, every pixel of it. A widget floating on the 3D canvas cannot be see-through
/// (the scene is a native surface Qt does not composite under its children), so anything left
/// unpainted shows as black. That is also why the corners are square.
/// </summary>
internal sealed class EffigyCallout : Widget
{
	public const float CalloutWidth = 460f;

	private const float PadX = 10f;
	private const float PadY = 8f;
	private const float IconSize = 16f;
	private const float LineHeight = 16f;
	private const float ButtonHeight = 22f;
	private const float FontSize = 9f;

	private static readonly Color Background = new( 0.15f, 0.15f, 0.19f, 1f );

	private EffigyCalloutKind _kind;
	private string _text;
	private (string Label, Action Run)[] _actions;
	private readonly List<string> _lines = new();
	private readonly List<Rect> _buttonRects = new();
	private Rect _closeRect;
	private int _hoverButton = -1;
	private bool _hoverClose;

	/// <summary>Seconds before it goes on its own, or null to stay until dismissed or resolved.</summary>
	public float? Lifetime { get; private set; }

	/// <summary>When it was last shown or refreshed, for the lifetime.</summary>
	public float ShownAt { get; private set; }

	public bool Dismissed { get; private set; }

	public EffigyCallout( Widget parent ) : base( parent )
	{
		NoSystemBackground = true;
		MouseTracking = true;
		FixedWidth = CalloutWidth;
	}

	/// <summary>Set or replace what it says, and restart its clock.</summary>
	public void Show( EffigyCalloutKind kind, string text, (string Label, Action Run)[] actions, float? lifetime )
	{
		_kind = kind;
		_text = text ?? "";
		_actions = actions ?? Array.Empty<(string, Action)>();
		Lifetime = lifetime;
		ShownAt = RealTime.Now;
		Dismissed = false;
		Relayout();
		Update();
	}

	/// <summary>Whether its time is up. A callout under the mouse waits — it goes while you are not
	/// reading it, never while you are.</summary>
	public bool Expired => Dismissed
		|| (Lifetime is { } life && !IsUnderMouse && RealTime.Now - ShownAt > life);

	private Color Accent => _kind == EffigyCalloutKind.Error ? Theme.Red : Theme.Yellow;

	private string IconName => _kind == EffigyCalloutKind.Error ? "error_outline" : "block";

	/// <summary>Wrap the text and lay out the buttons, then size to fit them.</summary>
	private void Relayout()
	{
		Paint.SetDefaultFont( FontSize );

		var textLeft = PadX + 3f + IconSize + 8f;
		var closeWidth = 18f;
		var textWidth = CalloutWidth - textLeft - PadX - closeWidth;

		_lines.Clear();

		foreach ( var paragraph in _text.Split( '\n' ) )
		{
			var line = "";

			foreach ( var word in paragraph.Split( ' ' ) )
			{
				var attempt = line.Length == 0 ? word : line + " " + word;

				if ( line.Length > 0 && Paint.MeasureText( attempt ).x > textWidth )
				{
					_lines.Add( line );
					line = word;
				}
				else
				{
					line = attempt;
				}
			}

			_lines.Add( line );
		}

		var y = PadY + _lines.Count * LineHeight;

		_buttonRects.Clear();

		if ( _actions.Length > 0 )
		{
			y += 6f;
			var x = textLeft;

			foreach ( var (label, _) in _actions )
			{
				var width = Paint.MeasureText( label ).x + 18f;
				_buttonRects.Add( new Rect( x, y, width, ButtonHeight ) );
				x += width + 6f;
			}

			y += ButtonHeight;
		}

		_closeRect = new Rect( CalloutWidth - PadX - closeWidth + 4f, PadY - 1f, closeWidth, closeWidth );
		FixedHeight = MathF.Max( y + PadY, PadY * 2f + IconSize );
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		Paint.ClearPen();
		Paint.SetBrush( Background );
		Paint.DrawRect( new Rect( 0f, 0f, Width, Height ) );

		// The edge that says how much to care, as in the mock you picked.
		Paint.SetBrush( Accent );
		Paint.DrawRect( new Rect( 0f, 0f, 3f, Height ) );

		Paint.SetPen( Accent );
		Paint.DrawIcon( new Rect( PadX + 3f, PadY, IconSize, IconSize ), IconName, IconSize, TextFlag.Center );

		var textLeft = PadX + 3f + IconSize + 8f;

		Paint.SetDefaultFont( FontSize );
		Paint.SetPen( Color.Lerp( Accent, Color.White, 0.65f ) );

		for ( var i = 0; i < _lines.Count; i++ )
			Paint.DrawText( new Rect( textLeft, PadY + i * LineHeight, Width - textLeft - PadX - 18f, LineHeight ), _lines[i], TextFlag.LeftCenter );

		for ( var i = 0; i < _buttonRects.Count; i++ )
		{
			var rect = _buttonRects[i];
			var hovered = i == _hoverButton;

			Paint.ClearPen();
			Paint.SetBrush( hovered ? Accent.WithAlpha( 0.35f ) : new Color( 0.24f, 0.24f, 0.30f ) );
			Paint.DrawRect( rect, 3f );

			Paint.SetPen( hovered ? Color.White : Color.White.WithAlpha( 0.85f ) );
			Paint.DrawText( rect, _actions[i].Label, TextFlag.Center );
		}

		Paint.SetPen( Color.White.WithAlpha( _hoverClose ? 0.9f : 0.4f ) );
		Paint.DrawIcon( _closeRect, "close", 14f, TextFlag.Center );
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		// Copied out first: MouseEvent is a ref struct and cannot be captured by the lambda.
		var at = e.LocalPosition;
		var button = _buttonRects.FindIndex( r => r.IsInside( at ) );
		var close = _closeRect.IsInside( at );

		if ( button == _hoverButton && close == _hoverClose )
			return;

		_hoverButton = button;
		_hoverClose = close;
		Cursor = button >= 0 || close ? CursorShape.Finger : CursorShape.Arrow;
		Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();

		if ( _hoverButton < 0 && !_hoverClose )
			return;

		_hoverButton = -1;
		_hoverClose = false;
		Update();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		// Swallow every press on the panel so it never reaches the scene behind it.
		e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( _closeRect.IsInside( e.LocalPosition ) )
		{
			Dismissed = true;
			return;
		}

		var at = e.LocalPosition;
		var button = _buttonRects.FindIndex( r => r.IsInside( at ) );

		if ( button < 0 )
			return;

		var run = _actions[button].Run;

		// A button does its thing and takes the message with it — what it said has been acted on.
		Dismissed = true;
		run?.Invoke();
	}
}
