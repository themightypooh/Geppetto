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
	/// <summary>World points to mark. Empty draws nothing.</summary>
	public IReadOnlyList<Vector3> Markers { get; set; } = Array.Empty<Vector3>();

	public Color MarkerColor { get; set; } = new( 1f, 0.25f, 0.2f );

	private void DrawMarkers()
	{
		if ( Markers.Count == 0 )
			return;

		// Same size on screen whatever the zoom, like the curve handles: a marker is a flag, not
		// a thing with a size of its own.
		var radius = MathF.Max( 0.05f, UnitsPerPixel() * 3.5f );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = MarkerColor;

		foreach ( var p in Markers )
			Gizmo.Draw.SolidSphere( p, radius, 6, 6 );

		Gizmo.Draw.IgnoreDepth = false;
	}
}
