using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Splines, from the window's side.
//
//  A SplineFeature is drawn in the viewport while its dialog is open: the
//  dialog opening arms the viewport (OpenedForFeature → BeginSplineDraw), the
//  dialog closing disarms it (EffigyFeatureDialog.Close), and every point the
//  viewport adds or moves comes back through OnSplineEdited as a rebuild —
//  the same shape as a sketch, which is drawn in the viewport while its
//  feature is open and rebuilt on every edit.
// ============================================================================

public sealed partial class EffigyWindow
{
	private void BeginSplineDraw( SplineFeature spline )
	{
		if ( _viewport is null || spline is null )
			return;

		_viewport.SplineEdited = OnSplineEdited;
		_viewport.SplineFinishRequested = () => _dialog?.AcceptNow();
		_viewport.BeginSplineEdit( spline );

		SetPrompt( spline.Points.Count == 0
			? "Spline: click to drop points — on a body, or in the air. Drag a point to move it, Backspace takes the last one back, Enter or the tick finishes."
			: "Spline: click to add points, drag one to move it. Backspace removes the last, Enter or the tick finishes." );
	}

	/// <summary>A point was added, moved or removed: the feature's inputs changed, so it re-runs —
	/// which is what turns the curve into the tube, and moves anything bent along it.</summary>
	private void OnSplineEdited()
	{
		if ( _viewport?.SplineFeature is not { } spline )
			return;

		_studio.MarkDirty( spline );
		RebuildStudio();
		_dialog?.RefreshValues();
	}

	/// <summary>Add a spline from Object mode: the feature goes in and its dialog opens, which
	/// arms the drawing. Nothing is selected for it — a spline is drawn from nothing.</summary>
	private void AddSpline()
	{
		AddFeature( NewFeature( ToolKind.Spline, -1 ) );
	}
}
