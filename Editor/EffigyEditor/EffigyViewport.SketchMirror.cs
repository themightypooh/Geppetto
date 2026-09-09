using Editor;
using Effigy;
using Sandbox;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Mirror: reflect the selected geometry across a line you click. Onshape's sketch Mirror.
///
/// THE ONE TOOL HERE THAT CONSUMES A SELECTION rather than making one. Every other tool acts on
/// whatever is under the cursor when it is pressed; this acts on what was picked with the Select
/// tool before it was armed, and its single click only names the axis. That is why it needs no
/// dialog and no phase of its own - the box select in EffigyViewport.Constraints.cs is the half
/// that collects, and this is the half that acts.
///
/// THE REFLECTION ITSELF IS THE KERNEL'S - see SketchEdit.Mirror, which also records the Symmetric
/// constraints that make it a mirror rather than a paste. Nothing here computes geometry except
/// the preview, and that calls the same SketchEdit.Reflect the commit will, so the ghost cannot
/// disagree with the result.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>The curves currently selected, resolved. Curves that have since been deleted drop
	/// out rather than arriving as nulls.</summary>
	private List<SketchCurve> SelectedSketchCurves() =>
		SketchSelection.Curves.Select( FindCurve ).Where( c => c is not null ).ToList();

	/// <summary>The line under the cursor, or null when what is there is not a line. A mirror axis
	/// has to be straight, and the refusal has to name that rather than doing nothing.</summary>
	private SketchLine MirrorAxisUnderCursor() => FindCurve( CurveUnderCursor() ) as SketchLine;

	private void ApplyMirror( Vec2 p )
	{
		if ( !HasSketchSelection )
		{
			SetSketchPrompt( "Mirror - pick what to mirror with the Select tool first (drag a box "
				+ "round it), then click the line to reflect it across." );
			return;
		}

		if ( CurveUnderCursor() is not { } id || FindCurve( id ) is not { } curve )
		{
			SetSketchPrompt( "Mirror - click the line to reflect the selection across." );
			return;
		}

		if ( curve is not SketchLine axis )
		{
			SetSketchPrompt( "Mirror - the mirror line has to be a straight line; that is a "
				+ $"{curve.GetType().Name.Replace( "Sketch", "" ).ToLower()}." );
			return;
		}

		// NO SketchEditing HERE. ClickTool took the undo snapshot before handing the click on, the
		// same as Trim and Extend - a second snapshot makes the first Ctrl+Z look like it did
		// nothing.
		if ( !SketchEdit.Mirror( ActiveSketch, SelectedSketchCurves(), SketchSelection.Points,
			axis, out var created, out var error ) )
		{
			SetSketchPrompt( $"Mirror - {error}" );
			return;
		}

		SetSketchPrompt( created.Count == 1
			? "Mirror - one curve reflected across that line, and held symmetric to its original."
			: $"Mirror - {created.Count} curves reflected across that line, and held symmetric to "
				+ "their originals." );

		Edited();
	}

	/// <summary>The ghost of what the click would make, drawn across the line under the cursor.
	/// The axis is lit as well, because "which line am I about to mirror across" is the only
	/// question this tool asks.</summary>
	private void DrawMirrorPreview()
	{
		if ( !HasSketchSelection || MirrorAxisUnderCursor() is not { } axis )
			return;

		var a = ActiveSketch.Points[axis.Start];
		var b = ActiveSketch.Points[axis.End];

		Gizmo.Draw.Color = SketchSelectedColor;
		Gizmo.Draw.LineThickness = 3f;
		Gizmo.Draw.Line( PlaneToWorld( a ), PlaneToWorld( b ) );

		Gizmo.Draw.Color = SketchPreviewColor;
		Gizmo.Draw.LineThickness = 2f;

		foreach ( var curve in SelectedSketchCurves() )
		{
			if ( ReferenceEquals( curve, axis ) )
				continue;

			var points = curve.Tessellate( ActiveSketch, ActiveSketch.Tolerance );

			for ( var i = 0; i < points.Count - 1; i++ )
			{
				Gizmo.Draw.Line(
					PlaneToWorld( SketchEdit.Reflect( points[i], a, b ) ),
					PlaneToWorld( SketchEdit.Reflect( points[i + 1], a, b ) ) );
			}
		}

		var units = UnitsPerPixel();

		foreach ( var point in SketchSelection.Points )
		{
			Gizmo.Draw.SolidSphere(
				PlaneToWorld( SketchEdit.Reflect( ActiveSketch.Points[point], a, b ) ),
				units * PendingPointPixels, 8, 8 );
		}
	}

	private string MirrorPrompt()
	{
		if ( !HasSketchSelection )
			return "Mirror - pick what to mirror with the Select tool first (drag a box round it), "
				+ "then come back and click the line";

		var count = SketchSelection.Curves.Count + SketchSelection.Points.Count;

		return $"Mirror - click the line to reflect the {count} selected "
			+ $"thing{(count == 1 ? "" : "s")} across";
	}
}
