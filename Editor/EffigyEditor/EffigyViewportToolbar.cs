using Editor;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// A tool button drawn as its own box, for the strip that sits along the top of the viewport.
///
/// Not an Editor.Option on a ToolBar. A ToolBar paints one continuous strip of chrome and packs
/// its options edge to edge, which is what put twelve undifferentiated glyphs in the window's
/// title chrome and made them read as decoration rather than as the tools. Each of these paints
/// its own bordered box, so a tool is a target you can see the edges of before you click it.
/// </summary>
internal sealed class EffigyToolButton : Widget
{
	private string _icon;

	/// <summary>Settable, not init-only: a grouped box adopts the icon of whichever variant was
	/// last chosen from its chevron, so the strip shows the tool you are actually holding.</summary>
	public string Icon
	{
		get => _icon;
		set
		{
			if ( _icon == value )
				return;

			_icon = value;
			Update();
		}
	}

	/// <summary>Draws the little chevron that says "there are variants under this one", the way
	/// Onshape's grouped feature buttons do.</summary>
	public bool HasDropdown { get; init; }

	/// <summary>A toggle rather than a command: the box flips its own Checked state on click, and
	/// the handler reads it. Exclusive groups (the sketch tools) then correct the rest of the strip
	/// through EffigyViewportToolbar.SetChecked.</summary>
	public bool Checkable { get; init; }

	private bool _checked;

	public bool Checked
	{
		get => _checked;
		set
		{
			if ( _checked == value )
				return;

			_checked = value;
			Update();
		}
	}

	public Action Clicked { get; set; }

	/// <summary>Right-click, or a click on the chevron, opens the variant menu.</summary>
	public Action DropdownRequested { get; set; }

	private bool _pressed;

	public EffigyToolButton( Widget parent ) : base( parent )
	{
		// FixedWidth/FixedHeight rather than FixedSize, and MouseTracking + IsUnderMouse rather
		// than tracking hover by hand - both copied from RigIconButton, which is the one custom
		// button in this repo already proven against the editor's API.
		FixedWidth = 38f;
		FixedHeight = 34f;

		Cursor = CursorShape.Finger;
		MouseTracking = true;
	}

	protected override void OnPaint()
	{
		var rect = LocalRect;
		var hovered = IsUnderMouse;

		Paint.Antialiasing = true;
		Paint.ClearPen();

		// Checked beats hovered: while a sketch tool is armed it must stay obviously armed even
		// as the cursor wanders over its neighbours.
		if ( _checked )
			Paint.SetBrush( Theme.Blue.WithAlpha( 0.30f ) );
		else if ( _pressed )
			Paint.SetBrush( Theme.Blue.WithAlpha( 0.22f ) );
		else if ( hovered )
			Paint.SetBrush( Theme.ControlBackground.WithAlpha( 0.95f ) );
		else
			Paint.SetBrush( Theme.ControlBackground.WithAlpha( 0.55f ) );

		Paint.DrawRect( rect, 3f );

		Paint.ClearBrush();
		Paint.SetPen( _checked ? Theme.Blue : Theme.WindowBackground.WithAlpha( 0.9f ) );
		Paint.DrawRect( rect, 3f );

		Paint.SetPen( _checked ? Theme.Blue : Theme.TextControl.WithAlpha( Enabled ? 0.95f : 0.35f ) );

		// Leave room on the right for the chevron so the glyph does not sit under it.
		var iconRect = HasDropdown ? rect.Shrink( 0f, 0f, 8f, 0f ) : rect;
		Paint.DrawIcon( iconRect, Icon, 20, TextFlag.Center );

		if ( !HasDropdown )
			return;

		Paint.SetPen( Theme.TextControl.WithAlpha( 0.55f ) );
		Paint.DrawIcon( new Rect( rect.Right - 11f, rect.Top, 10f, rect.Height ), "expand_more", 10, TextFlag.Center );
	}

	protected override void OnMouseEnter()
	{
		base.OnMouseEnter();
		Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();
		_pressed = false;
		Update();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( !Enabled )
			return;

		// Right-click anywhere, or left-click on the chevron itself, asks for the variants.
		if ( e.RightMouseButton || (HasDropdown && e.LocalPosition.x >= Width - 12f) )
		{
			DropdownRequested?.Invoke();
			return;
		}

		if ( !e.LeftMouseButton )
			return;

		_pressed = true;
		Update();
		e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );

		if ( !_pressed )
			return;

		_pressed = false;
		Update();

		// Only fires if released while still over the button - dragging off to cancel is what
		// every other button does.
		if ( !Enabled || !IsUnderMouse )
			return;

		if ( Checkable )
			Checked = !Checked;

		Clicked?.Invoke();
	}
}

/// <summary>
/// The strip of tool boxes along the top of the viewport.
///
/// It lives inside the viewport rather than in the window's toolbar area on purpose: these are
/// modelling tools, they act on what is in the graphics area, and Onshape puts them directly above
/// it. Sitting up in the window chrome next to File/Edit/View made them look like window
/// furniture. The sketch strip uses the same widget and takes the same position, replacing the
/// feature strip for as long as a sketch is open.
/// </summary>
internal sealed class EffigyViewportToolbar : Widget
{
	private readonly List<EffigyToolButton> _buttons = new();

	public EffigyViewportToolbar( Widget parent ) : base( parent )
	{
		Layout = Layout.Row();
		Layout.Margin = new Sandbox.UI.Margin( 6, 7 );
		Layout.Spacing = 6;
	}

	public EffigyToolButton AddTool( string icon, string tip, Action clicked,
		bool checkable = false, Action dropdown = null )
	{
		var button = new EffigyToolButton( this )
		{
			Icon = icon,
			ToolTip = tip,
			StatusTip = tip,
			Checkable = checkable,
			HasDropdown = dropdown is not null,
			Clicked = clicked,
			DropdownRequested = dropdown,
		};

		Layout.Add( button );
		_buttons.Add( button );

		return button;
	}

	/// <summary>A gap rather than a drawn rule. The boxes already separate themselves; a divider
	/// on top of that is one more line for no extra information.</summary>
	public void AddSeparator() => Layout.AddSpacingCell( 14f );

	/// <summary>Exclusive selection across the strip - only one sketch tool can be armed, and the
	/// others have to visibly let go. Editor.ToolBar has no radio-group concept and neither does
	/// this, so it is enforced here.</summary>
	public void SetChecked( EffigyToolButton active )
	{
		foreach ( var button in _buttons )
			button.Checked = button == active;
	}
}
