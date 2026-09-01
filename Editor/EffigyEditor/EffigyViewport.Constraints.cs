using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Selecting sketch geometry, so a constraint has something to act on.
///
/// The solver has been able to satisfy eleven kinds of rule for a while and there has been no way
/// to ask for one, because there was nothing in the sketch that a rule could be pointed AT — the
/// Select tool could drag a point and that was the whole of it. This adds a persistent selection of
/// points and curves, and hands it to ConstraintTools, which answers what may be applied.
///
/// CLICK ACCUMULATES, RATHER THAN CTRL-CLICK. Onshape wants a modifier and this deliberately does
/// not, for a reason that is about this repo rather than about taste: no modifier-key API is proven
/// anywhere in the corpus here, and an unproven member name is a compile error that takes the whole
/// editor assembly down. Accumulating on plain clicks needs nothing unproven and suits the job
/// anyway — a constraint is two or three picks and then a menu, so the common case is picking
/// several things in a row. Clicking a selected thing again drops it; clicking empty space or
/// pressing Escape clears.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>What is selected in the sketch right now. Read by the window to build the
	/// constraint menu; the kernel's ConstraintTools takes exactly this type.</summary>
	public SketchSelection SketchSelection { get; private set; } = new();

	/// <summary>Raised when the right button asks for the constraint menu while a sketch is open.
	/// The viewport resolves what is selected and stops there — WHICH constraints that allows is a
	/// question for the kernel, and the window is what owns the answer.</summary>
	public Action SketchConstraintMenuRequested { get; set; }

	/// <summary>Raised after a constraint is applied and the sketch re-solved, so the window can
	/// snapshot undo and rebuild.</summary>
	public Action SketchConstraintApplied { get; set; }

	/// <summary>Curve under the cursor while selecting, or null.</summary>
	private string _hoverCurveId;

	/// <summary>The point a press landed on, held until the release decides whether that press was
	/// a click (select it) or the start of a drag (leave the selection alone).</summary>
	private int _pressedPoint = -1;

	/// <summary>
	/// Where that point was when it was pressed, and how many points the sketch had.
	///
	/// The drag code already tracks whether it moved, in _dragMoved — and clears it in
	/// EndPointDrag() on the same frame the button comes up, BEFORE this file gets to look. So the
	/// press has to be classified against state this file owns. Comparing the position also handles
	/// a drag that came back to where it started, which is a click by any useful definition.
	///
	/// The point count is the second half of it: dropping a point onto another MERGES them, which
	/// renumbers everything after it, and a stale index would then select whatever moved up.
	/// </summary>
	private Vec2 _pressedAt;
	private int _pressedPointCount;

	private static readonly Color SketchSelectedColor = new( 0.35f, 0.78f, 1f, 1f );
	private static readonly Color SketchHoverColor = new( 0.6f, 0.88f, 1f, 1f );

	/// <summary>How close the cursor has to be to a curve to hit it, in SCREEN PIXELS — the same
	/// treatment the point handles get, and for the same reason: a sketch can be one unit across or
	/// a thousand, and a fixed distance in sketch units is either untouchable or catches everything.
	/// </summary>
	private const float CurvePickPixels = 6f;

	public bool HasSketchSelection =>
		SketchSelection is not null && !SketchSelection.IsEmpty;

	public void ClearSketchSelection()
	{
		SketchSelection = new SketchSelection();
		PushPrompt();
	}

	/// <summary>
	/// Hover, click and draw the selection. Runs after the point handles, which have already had
	/// first refusal on the cursor — a point sitting on a curve must select the point.
	/// </summary>
	private void SketchSelectionFrame()
	{
		_hoverCurveId = null;

		if ( ActiveSketch is null || SketchTool != SketchToolKind.Select )
		{
			_pressedPoint = -1;
			return;
		}

		PruneSelection();
		DrawSketchSelection();

		// A press that landed on a point is settled on RELEASE: moved means it was a drag and the
		// selection is none of its business, still means it was a click.
		if ( _pressedPoint >= 0 && !Gizmo.IsLeftMouseDown )
		{
			var stillThere = _pressedPointCount == ActiveSketch.Points.Count
				&& _pressedPoint < ActiveSketch.Points.Count;

			if ( stillThere && (ActiveSketch.Points[_pressedPoint] - _pressedAt).Length < 1e-6f )
				Toggle( _pressedPoint );

			_pressedPoint = -1;
		}

		// A live drag of either kind is none of the selection's business: the cursor leaves what it
		// grabbed the moment it starts moving, and everything it crosses on the way would otherwise
		// be selected behind it.
		if ( _dragPoint >= 0 || DraggingCurveHandle )
			return;

		if ( !_canvasHasCursor || !_cursorOnPlaneValid )
			return;

		// A glyph under the cursor owns the click — it is drawn on top of the geometry, so without
		// this, removing a rule would also select whatever line the mark was sitting over.
		if ( CursorOnConstraintMark )
			return;

		if ( _hoverPoint >= 0 )
		{
			// The point handles own this cursor. Remember the press so the release can classify it.
			if ( Gizmo.WasLeftMousePressed )
			{
				_pressedPoint = _hoverPoint;
				_pressedAt = ActiveSketch.Points[_hoverPoint];
				_pressedPointCount = ActiveSketch.Points.Count;
			}

			return;
		}

		// A grip on the curve owns the click the same way a constraint glyph does - it is drawn on
		// the curve, so without this every grab would also toggle that curve into the selection.
		if ( CursorOnCurveHandle )
			return;

		_hoverCurveId = CurveUnderCursor();

		if ( !Gizmo.WasLeftMousePressed )
			return;

		if ( _hoverCurveId is not null )
		{
			Toggle( _hoverCurveId );
			return;
		}

		// Empty plane. Clearing here is what makes the accumulating selection bearable — there is
		// always somewhere to click that means "start again".
		if ( HasSketchSelection )
			ClearSketchSelection();
	}

	/// <summary>The curve nearest the cursor within the pick radius, or null. Measured against the
	/// TESSELLATION rather than the ideal curve, so an arc is picked where it is drawn.</summary>
	private string CurveUnderCursor()
	{
		var reach = UnitsPerPixel() * CurvePickPixels;
		var best = reach;
		string found = null;

		foreach ( var curve in ActiveSketch.Curves )
		{
			var pts = curve.Tessellate( ActiveSketch, ActiveSketch.Tolerance );

			for ( var i = 0; i < pts.Count - 1; i++ )
			{
				var d = DistanceToSegment( _cursorOnPlane, pts[i], pts[i + 1] );

				if ( d >= best )
					continue;

				best = d;
				found = curve.Id;
			}
		}

		return found;
	}

	private static float DistanceToSegment( Vec2 p, Vec2 a, Vec2 b )
	{
		var along = b - a;
		var lengthSquared = along.LengthSquared;

		if ( lengthSquared < 1e-12f )
			return (p - a).Length;

		var t = Math.Clamp( Vec2.Dot( p - a, along ) / lengthSquared, 0f, 1f );

		return (p - (a + along * t)).Length;
	}

	private void Toggle( int point )
	{
		if ( !SketchSelection.Points.Remove( point ) )
			SketchSelection.Points.Add( point );

		PushPrompt();
	}

	private void Toggle( string curveId )
	{
		if ( !SketchSelection.Curves.Remove( curveId ) )
			SketchSelection.Curves.Add( curveId );

		PushPrompt();
	}

	/// <summary>
	/// Drop anything the selection refers to that is no longer there.
	///
	/// Undo, a deleted curve and a re-solve that merged two points all leave a selection pointing at
	/// something gone. A stale POINT index is the dangerous one: indices are positional, so a
	/// removed point does not leave a hole, it renumbers everything after it — a stale index still
	/// resolves, to the wrong point, and the constraint would be applied to whatever moved up.
	/// </summary>
	private void PruneSelection()
	{
		SketchSelection.Points.RemoveAll( p => p < 0 || p >= ActiveSketch.Points.Count );
		SketchSelection.Curves.RemoveAll( id => ActiveSketch.Curves.All( c => c.Id != id ) );
	}

	private void DrawSketchSelection()
	{
		Gizmo.Draw.IgnoreDepth = true;

		foreach ( var curve in ActiveSketch.Curves )
		{
			var selected = SketchSelection.Curves.Contains( curve.Id );
			var hovered = curve.Id == _hoverCurveId;

			if ( !selected && !hovered )
				continue;

			Gizmo.Draw.Color = selected ? SketchSelectedColor : SketchHoverColor;
			Gizmo.Draw.LineThickness = selected ? 4f : 3f;

			var pts = curve.Tessellate( ActiveSketch, ActiveSketch.Tolerance );

			for ( var i = 0; i < pts.Count - 1; i++ )
				Gizmo.Draw.Line( PlaneToWorld( pts[i] ), PlaneToWorld( pts[i + 1] ) );
		}

		Gizmo.Draw.LineThickness = 2f;
		Gizmo.Draw.Color = SketchSelectedColor;

		var radius = UnitsPerPixel() * 4.5f;

		foreach ( var point in SketchSelection.Points )
			Gizmo.Draw.SolidSphere( PlaneToWorld( ActiveSketch.Points[point] ), radius, 10, 10 );

		Gizmo.Draw.IgnoreDepth = false;
	}

	/// <summary>
	/// Apply one offer, re-solve, and report.
	///
	/// PINNED ON THE FIRST SELECTED POINT where there is one. The solver has to hold something still
	/// or the whole sketch is free to slide, and its default is point 0 — which is wherever the user
	/// first clicked, possibly a long time ago and nowhere near what they are working on. Pinning
	/// something they just selected makes the sketch resolve around their attention.
	/// </summary>
	public bool ApplySketchConstraint( ConstraintOffer offer )
	{
		if ( ActiveSketch is null || offer is null )
			return false;

		var pin = SketchSelection.Points.Count > 0 ? SketchSelection.Points[0] : 0;

		if ( pin < 0 || pin >= ActiveSketch.Points.Count )
			pin = 0;

		var result = ConstraintTools.ApplyAndSolve( ActiveSketch, offer, pin );

		if ( !result.Applied )
		{
			SketchPromptChanged?.Invoke( result.Message );
			return false;
		}

		SketchConstraintApplied?.Invoke();

		// The degrees of freedom are the one number a CAD user actually wants after a constraint,
		// and the solver has been reporting them all along with nothing to show them.
		SketchPromptChanged?.Invoke( result.Solve.DegreesOfFreedom == 0
			? $"{offer.Label} applied — the sketch is fully defined"
			: $"{offer.Label} applied — {result.Solve.DegreesOfFreedom} degree{(result.Solve.DegreesOfFreedom == 1 ? "" : "s")} of freedom left" );

		return true;
	}


	// --- the marks on the sketch ------------------------------------------------------------------

	/// <summary>Where each glyph ended up this frame, so a click can find one. Rebuilt every frame
	/// rather than cached: the geometry moves under them constantly, and a stale hit box that
	/// deletes the wrong rule is worse than no hit box at all.</summary>
	private readonly List<(ConstraintMarker Marker, Vec2 At)> _markerHits = new();

	private ConstraintMarker _hoverMarker;

	/// <summary>How far off the geometry a glyph sits, and how close a click has to land, both in
	/// SCREEN PIXELS — the kernel gives an anchor and a direction and deliberately leaves the
	/// distance alone, because a sketch can be one unit across or a thousand.</summary>
	private const float MarkerOffsetPixels = 14f;
	private const float MarkerPickPixels = 9f;

	private static readonly Color ConstraintMarkColor = new( 0.45f, 0.85f, 0.6f, 1f );
	private static readonly Color DimensionMarkColor = new( 1f, 0.82f, 0.35f, 1f );
	private static readonly Color MarkHoverColor = new( 1f, 0.42f, 0.38f, 1f );

	/// <summary>Whether the rules that hold the sketch together are drawn on it. On by default —
	/// a constraint you cannot see is a constraint you fight.</summary>
	public bool ShowConstraintMarks { get; set; } = true;

	/// <summary>
	/// Draw a glyph per rule, and let one be clicked away.
	///
	/// DELETING IS ON THE GLYPH rather than in a list somewhere, because "why will this line not
	/// move" is a question about a specific place on the drawing, and the answer should be sitting
	/// there next to it.
	/// </summary>
	private void ConstraintMarkFrame()
	{
		_markerHits.Clear();
		_hoverMarker = null;

		if ( ActiveSketch is null || !ShowConstraintMarks )
			return;

		var units = UnitsPerPixel();
		var offset = units * MarkerOffsetPixels;
		var reach = units * MarkerPickPixels;

		var hoverable = SketchTool == SketchToolKind.Select && _canvasHasCursor && _cursorOnPlaneValid;

		foreach ( var marker in ConstraintTools.Markers( ActiveSketch ) )
		{
			var at = marker.Anchor + marker.Away * offset;

			// A mark with no side to sit on — a coincidence, a symmetry — is nudged up and right so
			// it clears the point it belongs to instead of being drawn on top of it.
			if ( marker.Away.Length < 1e-6f )
				at = marker.Anchor + new Vec2( 1f, 1f ).Normal * offset;

			_markerHits.Add( (marker, at) );

			if ( hoverable && _hoverMarker is null && (_cursorOnPlane - at).Length < reach )
				_hoverMarker = marker;

			var color = _hoverMarker == marker ? MarkHoverColor
				: marker.IsDimension ? DimensionMarkColor : ConstraintMarkColor;

			DrawDimensionText( PlaneToWorld( at ), marker.Label, color, 0f );
		}

		if ( _hoverMarker is null )
			return;

		// Only once it is actually under the cursor, so the prompt is not shouting the whole time.
		SketchPromptChanged?.Invoke( $"{Name( _hoverMarker.Kind )} — click to remove it" );

		if ( Gizmo.WasLeftMousePressed )
			RemoveConstraint( _hoverMarker.Constraint );
	}

	/// <summary>
	/// Delete one rule and re-solve.
	///
	/// The sketch does NOT spring back to where it was before the rule was added — nothing recorded
	/// that, and inventing it would be worse than leaving the geometry alone. Removing a constraint
	/// only ever gives freedom back; what the shape does with that freedom is up to the next drag.
	/// </summary>
	private void RemoveConstraint( SketchConstraint constraint )
	{
		if ( ActiveSketch is null || constraint is null )
			return;

		SketchEditing?.Invoke();

		if ( !ActiveSketch.Constraints.Remove( constraint ) )
			return;

		var result = SketchSolver.Solve( ActiveSketch );

		SketchConstraintApplied?.Invoke();

		SketchPromptChanged?.Invoke( result.DegreesOfFreedom == 0
			? "Constraint removed — the sketch is still fully defined"
			: $"Constraint removed — {result.DegreesOfFreedom} degree{(result.DegreesOfFreedom == 1 ? "" : "s")} of freedom" );
	}

	/// <summary>Whether a click this frame landed on a glyph, so the selection code can leave it
	/// alone. Without this, clicking a mark to delete it would ALSO clear the selection, or pick
	/// whatever curve happened to be under the glyph.</summary>
	private bool CursorOnConstraintMark => _hoverMarker is not null;

	new static string Name( SketchConstraintKind kind ) => kind switch
	{
		SketchConstraintKind.Horizontal => "Horizontal",
		SketchConstraintKind.Vertical => "Vertical",
		SketchConstraintKind.Coincident => "Coincident",
		SketchConstraintKind.Distance => "Distance",
		SketchConstraintKind.EqualLength => "Equal length",
		SketchConstraintKind.Parallel => "Parallel",
		SketchConstraintKind.Perpendicular => "Perpendicular",
		SketchConstraintKind.Angle => "Angle",
		SketchConstraintKind.PointOnLine => "Point on line",
		SketchConstraintKind.Symmetric => "Symmetric",
		SketchConstraintKind.Radius => "Radius",
		SketchConstraintKind.Diameter => "Diameter",
		SketchConstraintKind.Midpoint => "Midpoint",
		SketchConstraintKind.Concentric => "Concentric",
		SketchConstraintKind.Fixed => "Fix",
		SketchConstraintKind.Tangent => "Tangent",
		SketchConstraintKind.TangentArcs => "Tangent",
		_ => "Constraint",
	};

	/// <summary>What the status bar says about the selection, appended to the tool's own prompt.</summary>
	private string SelectionPrompt()
	{
		if ( !HasSketchSelection )
			return null;

		var parts = new List<string>();

		if ( SketchSelection.Points.Count > 0 )
			parts.Add( $"{SketchSelection.Points.Count} point{(SketchSelection.Points.Count == 1 ? "" : "s")}" );

		if ( SketchSelection.Curves.Count > 0 )
			parts.Add( $"{SketchSelection.Curves.Count} curve{(SketchSelection.Curves.Count == 1 ? "" : "s")}" );

		var offers = ConstraintTools.Offers( ActiveSketch, SketchSelection ).Count;

		return offers > 0
			? $"{string.Join( " and ", parts )} selected — right-click for {offers} constraint{(offers == 1 ? "" : "s")}"
			: $"{string.Join( " and ", parts )} selected — nothing can be constrained from this; click empty space to start again";
	}
}
