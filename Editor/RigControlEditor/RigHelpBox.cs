using Editor;
using Sandbox;

namespace Marionette.Editor;

/// <summary>
/// The "Documentation" style collapsible note used throughout the stock Inspector (a chevron, a
/// circled ? badge, a bold title, a blue accent line under the whole block) - reused here so each
/// tab opens with the same look instead of a hand-rolled one-off.
/// </summary>
internal static class RigHelpBox
{
	/// <summary>One labelled chunk of the note - "Model", "IK Constraint", whatever the reader
	/// needs to look up on its own rather than hunt for inside one wall of text.</summary>
	public readonly record struct Section( string Heading, string Body );

	public static Section S( string heading, string body ) => new( heading, body );

	/// <summary>
	/// intro: a plain, unlabelled paragraph at the top - what this tab is, in one or two
	/// sentences. sections: each gets its own bold heading and paragraph below it, so a reader
	/// can scan headings instead of parsing a block of text to find the one field they care about.
	///
	/// COLLAPSED BY DEFAULT. Expanded, three tabs of reference text is the first thing a new user
	/// sees, before they've done anything for it to be about - which reads as homework and gets
	/// skipped wholesale, taking the useful parts with it. The status bar's tutorial handles
	/// "what do I do now" one step at a time; this is the reference you open once you have a
	/// specific question, and the header line says which question it answers.
	/// </summary>
	public static Widget Create( Widget parent, string intro, Section[] sections, bool startExpanded = false )
	{
		var expanded = startExpanded;

		var box = new Widget( parent ) { Layout = Layout.Column() };
		box.Layout.Margin = 0;

		box.OnPaintOverride = () =>
		{
			// Accent line along the bottom, the one clearly-visible border the reference has.
			Paint.ClearPen();
			Paint.SetBrush( Theme.Blue );
			Paint.DrawRect( new Rect( 0, box.Height - 2, box.Width, 2 ) );
			return false;
		};

		var header = new HeaderWidget( box ) { FixedHeight = 28 };
		box.Layout.Add( header );

		var body = new Widget( box ) { Layout = Layout.Column() };
		body.Layout.Margin = new Sandbox.UI.Margin( 32, 0, 12, 12 );
		body.Layout.Spacing = 10;

		if ( !string.IsNullOrWhiteSpace( intro ) )
			body.Layout.Add( new Editor.Label( intro ) { WordWrap = true } );

		foreach ( var section in sections )
		{
			var block = body.Layout.AddColumn();
			block.Spacing = 2;

			var heading = new Editor.Label( section.Heading ) { Color = Theme.Blue };
			heading.SetStyles( "font-weight: 600;" );
			block.Add( heading );

			block.Add( new Editor.Label( section.Body ) { WordWrap = true } );
		}

		box.Layout.Add( body );

		void UpdateState()
		{
			header.Expanded = expanded;
			body.Visible = expanded;
		}

		UpdateState();

		header.Clicked = () =>
		{
			expanded = !expanded;
			UpdateState();
		};

		return box;
	}

	/// <summary>Chevron + circled "?" + bold "Documentation" title, hand-painted so it actually
	/// matches the reference instead of approximating it with stock Label/Button widgets.</summary>
	private sealed class HeaderWidget : Widget
	{
		public bool Expanded { get; set; } = true;
		public System.Action Clicked { get; set; }

		public HeaderWidget( Widget parent ) : base( parent )
		{
			Cursor = CursorShape.Finger;
		}

		protected override void OnPaint()
		{
			var rect = LocalRect;

			Paint.Antialiasing = true;

			// Chevron
			Paint.ClearBrush();
			Paint.SetPen( Theme.Blue );
			Paint.DrawIcon( new Rect( 6, 0, 16, rect.Height ), Expanded ? "expand_more" : "chevron_right", 16, TextFlag.Center );

			// Circled "?" badge - DrawCircle takes a Rect (confirmed from existing usage
			// elsewhere in this project), not a center+radius pair.
			var badgeCenter = new Vector2( 34, rect.Center.y );
			Paint.ClearPen();
			Paint.SetBrush( Theme.Blue );
			Paint.DrawCircle( new Rect( badgeCenter.x - 8, badgeCenter.y - 8, 16, 16 ) );

			Paint.SetPen( Theme.WindowBackground );
			Paint.SetDefaultFont( 8, 700 );
			Paint.DrawText( new Rect( badgeCenter.x - 8, badgeCenter.y - 8, 16, 16 ), "?", TextFlag.Center );

			// Title
			Paint.SetPen( Theme.Blue );
			Paint.SetDefaultFont( 8, 600 );
			Paint.DrawText( new Rect( 50, 0, rect.Width - 58, rect.Height ), "Documentation", TextFlag.LeftCenter );
		}

		protected override void OnMouseClick( MouseEvent e )
		{
			base.OnMouseClick( e );
			Clicked?.Invoke();
		}
	}
}
