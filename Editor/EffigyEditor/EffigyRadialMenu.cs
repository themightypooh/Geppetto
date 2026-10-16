using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// The radial menu a tap of the right mouse button opens at the cursor: a donut wheel of up to eight
/// actions for whatever was under it, each a wedge with its icon, and the middle naming the one you
/// are pointing at with a line on what it does.
///
/// WHY A WHEEL. A list puts every entry at a different distance and makes you read down to find one;
/// a wheel puts each at the same short distance in its own direction, so after a few uses the hand
/// knows "Extrude is up-right" and the eyes are not needed. Held to eight slots for the same reason
/// Blender's pies and Fusion's marking menu are: past that the directions get too close to hit
/// without looking. The full list is never lost — the last slot is always "More…".
///
/// ONE BIG LIT WEDGE is the point of the donut over labels floating round a dot: which item you are
/// about to pick is the most visible thing on screen, and the middle spells it out, so an icon you
/// have not learned yet still tells you what it is before you click it.
///
/// SELECTION IS BY DIRECTION, not by landing on the wedge. Once the cursor is outside the small dead
/// zone in the middle, the wedge whose direction is nearest lights, however far out the cursor is —
/// so a flick works, and there is no dead spot between wedges.
///
/// ENGINE-FREE: geometry, hit testing and drawing only, against IOverlayCanvas, so the headless
/// render (tools/viewcube-sim) draws exactly this. The viewport owns opening, input and the actions
/// (EffigyViewport.Radial.cs). All positions are in render pixels.
/// </summary>
internal static class EffigyRadialMenu
{
	/// <summary>One slot: its name, a Material icon name, whether it can be used now (and why not),
	/// and an optional one-line hint the middle shows while it is pointed at. A disabled slot is still
	/// drawn — dimmed — so the wheel has the same shape every time and the hand can learn it.</summary>
	public readonly record struct Item( string Label, string Icon = null, bool Enabled = true, string DisabledReason = null, string Hint = null );

	public const int MaxItems = 8;

	/// <summary>Inner and outer radius of the ring, logical pixels.</summary>
	public const float InnerPixels = 48f;
	public const float OuterPixels = 108f;

	/// <summary>Radius of the middle where nothing is chosen, logical pixels. Clicking there closes.</summary>
	public const float DeadZonePixels = 22f;

	/// <summary>Gap between neighbouring wedges, degrees, so they read as separate buttons.</summary>
	private const float GapDegrees = 2f;

	/// <summary>Arc pieces per wedge. Each piece is a convex quad, which is what the overlay fills.</summary>
	private const int ArcSteps = 8;

	private const float IconSize = 22f;

	/// <summary>Everything one frame of the menu needs.</summary>
	public readonly record struct State( IReadOnlyList<Item> Items, Vector2 Centre, float Dpi, int Hover, string Title, Color Accent );

	/// <summary>Angle of slot <paramref name="index"/>'s middle, radians: the first straight up, then
	/// clockwise. Screen space, so +y is down.</summary>
	private static float SlotAngle( int index, int count ) =>
		-MathF.PI * 0.5f + index * MathF.Tau / MathF.Max( count, 1 );

	private static Vector2 Polar( Vector2 centre, float angle, float radius ) =>
		centre + new Vector2( MathF.Cos( angle ), MathF.Sin( angle ) ) * radius;

	/// <summary>The slot the cursor points at, or -1 inside the dead zone.</summary>
	public static int HitTest( Vector2 mouse, Vector2 centre, float dpi, int count )
	{
		var offset = mouse - centre;

		if ( count == 0 || offset.Length < DeadZonePixels * dpi )
			return -1;

		var span = MathF.Tau / count;
		var angle = MathF.Atan2( offset.y, offset.x ) - SlotAngle( 0, count );

		// Shift by half a slot so each slot owns the angles either side of its middle.
		var turns = (angle + span * 0.5f) / span;
		var index = (int)MathF.Floor( turns ) % count;

		return index < 0 ? index + count : index;
	}

	/// <summary>Where the wheel may be centred so all of it stays on the canvas. A right-click near
	/// an edge opens it a little way in rather than half off screen.</summary>
	public static Vector2 ClampCentre( Vector2 wanted, Vector2 canvas, float dpi )
	{
		var reach = (OuterPixels + 8f) * dpi;

		return new Vector2(
			Math.Clamp( wanted.x, MathF.Min( reach, canvas.x * 0.5f ), MathF.Max( canvas.x - reach, canvas.x * 0.5f ) ),
			Math.Clamp( wanted.y, MathF.Min( reach, canvas.y * 0.5f ), MathF.Max( canvas.y - reach, canvas.y * 0.5f ) ) );
	}

	public static void Draw( IOverlayCanvas draw, State state )
	{
		var s = state.Dpi;
		var c = state.Centre;
		var count = Math.Min( state.Items.Count, MaxItems );

		if ( count == 0 )
			return;

		var ring = new Color( 0.15f, 0.16f, 0.19f, 0.96f );
		var middle = new Color( 0.09f, 0.10f, 0.12f, 0.97f );
		var inner = InnerPixels * s;
		var outer = OuterPixels * s;
		var span = MathF.Tau / count;
		var gap = GapDegrees * MathF.PI / 180f;

		for ( var i = 0; i < count; i++ )
		{
			var item = state.Items[i];
			var lit = i == state.Hover;
			var from = SlotAngle( i, count ) - span * 0.5f + gap * 0.5f;
			var to = SlotAngle( i, count ) + span * 0.5f - gap * 0.5f;

			var fill = lit
				? (item.Enabled ? state.Accent : new Color( 0.32f, 0.33f, 0.37f, 0.97f ))
				: ring;

			// The wedge as a run of quads along its arc. A lit wedge reaches a little further out,
			// so the choice reads by shape as well as by colour.
			var reach = lit ? outer + 6f * s : outer;

			for ( var k = 0; k < ArcSteps; k++ )
			{
				var a0 = from + (to - from) * k / ArcSteps;
				var a1 = from + (to - from) * (k + 1) / ArcSteps;

				draw.Polygon( new[]
				{
					Polar( c, a0, inner ), Polar( c, a0, reach ), Polar( c, a1, reach ), Polar( c, a1, inner ),
				}, fill );
			}

			var iconColour = !item.Enabled
				? new Color( 0.48f, 0.50f, 0.55f )
				: lit ? Color.White : new Color( 0.82f, 0.85f, 0.90f );

			var at = Polar( c, SlotAngle( i, count ), (inner + outer) * 0.5f );

			if ( item.Icon is not null )
			{
				var size = IconSize * s;
				draw.Icon( item.Icon, new Rect( at.x - size * 0.5f, at.y - size * 0.5f, size, size ), size, iconColour );
			}
			else
			{
				// No icon: the first few letters, so the slot is never a blank wedge.
				var label = item.Label ?? "";
				draw.Text( label.Length > 5 ? label[..5] : label, at, 11f * s, iconColour );
			}
		}

		// The middle names what is pointed at — and why it is greyed, if it is — or, pointing at
		// nothing, what was right-clicked.
		draw.Circle( c, inner - 3f * s, middle );

		if ( state.Hover >= 0 && state.Hover < count )
		{
			var item = state.Items[state.Hover];
			draw.Text( item.Label, c + new Vector2( 0f, -7f * s ), 13f * s, item.Enabled ? Color.White : new Color( 0.70f, 0.72f, 0.76f ) );

			var hint = item.Enabled ? item.Hint : item.DisabledReason ?? "Not available here";

			if ( !string.IsNullOrEmpty( hint ) )
				draw.Text( Shorten( hint, 22 ), c + new Vector2( 0f, 10f * s ), 9.5f * s,
					item.Enabled ? new Color( 0.62f, 0.65f, 0.70f ) : new Color( 0.95f, 0.72f, 0.30f ) );
		}
		else
		{
			draw.Text( string.IsNullOrEmpty( state.Title ) ? "Point to pick" : state.Title, c + new Vector2( 0f, -6f * s ), 11.5f * s, new Color( 0.80f, 0.82f, 0.86f ) );
			draw.Text( "click here to close", c + new Vector2( 0f, 10f * s ), 9f * s, new Color( 0.50f, 0.52f, 0.56f ) );
		}
	}

	/// <summary>Fit a hint to the middle: whole words up to <paramref name="max"/> characters, with an
	/// ellipsis if it had to stop. The full reason still goes in the callout if a greyed slot is clicked.</summary>
	private static string Shorten( string text, int max )
	{
		if ( text.Length <= max )
			return text;

		var cut = text.LastIndexOf( ' ', max );
		return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd( ',', '.', ' ', '—', '-' ) + "…";
	}
}
