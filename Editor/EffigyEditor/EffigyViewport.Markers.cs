using Editor;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

// ============================================================================
//  Markers: points somebody wants looked at, drawn over everything.
//
//  The clothing Test stage puts one on every vertex that clips through the
//  body in a pose. They are not picked, not saved and not part of the model;
//  they are the answer to "where", which a count in the console is not.
// ============================================================================

internal sealed partial class EffigyViewport
{
	/// <summary>World points to mark, each with a heat 0..1 that colours it from yellow (barely)
	/// to red (badly). Empty draws nothing.</summary>
	public IReadOnlyList<(Vector3 P, float Heat)> Markers { get; set; } = Array.Empty<(Vector3, float)>();

	public Color MarkerCool { get; set; } = new( 1f, 0.85f, 0.2f );
	public Color MarkerHot { get; set; } = new( 1f, 0.15f, 0.1f );

	/// <summary>The clothing test bar, parked under the other overlays. Shown and hidden by the
	/// window with the test pose.</summary>
	public void AddTestOverlay( Widget bar )
	{
		bar.Position = OverlayMargin + new Vector2( 0f, 46f );
		bar.Visible = false;
	}

	private void DrawMarkers()
	{
		if ( Markers.Count == 0 )
			return;

		// Same size on screen whatever the zoom, like the curve handles: a marker is a flag, not
		// a thing with a size of its own.
		var radius = MathF.Max( 0.05f, UnitsPerPixel() * 3.5f );

		Gizmo.Draw.IgnoreDepth = true;

		foreach ( var (p, heat) in Markers )
		{
			Gizmo.Draw.Color = Color.Lerp( MarkerCool, MarkerHot, Math.Clamp( heat, 0f, 1f ) );
			Gizmo.Draw.SolidSphere( p, radius * (0.8f + 0.5f * heat), 6, 6 );
		}

		Gizmo.Draw.IgnoreDepth = false;
	}
}
