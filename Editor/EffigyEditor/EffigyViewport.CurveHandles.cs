using Editor;
using Effigy;
using Sandbox;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// The grips that sit ON a curve, so the parts of a sketch that are not points can still be
/// changed by hand.
///
/// WHAT WAS MISSING. Every point in a sketch could be dragged, and for a line that is the whole of
/// it — but a circle's radius is a number on the curve, an ellipse's minor axis is another, and an
/// arc's bulge is its centre, which sits nowhere near the arc and cannot be moved without changing
/// what the endpoints mean. So a circle drawn slightly too small could be dimensioned, deleted or
/// redrawn, and not simply pulled bigger. This is the missing gesture: a single grip at the middle
/// of each curve that drives the thing that curve actually has.
///
/// THE MATHS IS NOT HERE. SketchHandles in the kernel owns where a grip sits and what dragging it
/// does; this file is a hit test, a colour and the undo bookkeeping. That split is why the
/// circumcentre behind the arc bulge is covered by HandleTests rather than by whoever last read
/// this file — the editor assembly does not compile outside s&amp;box, and a bulge that takes the
/// long way round the circle still looks like an arc on screen.
///
/// PRECEDENCE. Points first, then constraint glyphs, then these, then plain selection. A grip is
/// drawn on top of its curve, so it has to take the click before the curve does, or every grab
/// would also toggle the curve into the selection.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>The grip under the cursor, or null. Rebuilt every frame — the geometry moves under
	/// these constantly, and a stale grip drags the wrong curve.</summary>
	private CurveHandle? _hoverHandle;

	/// <summary>The curve whose grip is in hand, or null when nothing is being dragged.</summary>
	private string _handleCurveId;
	private CurveHandleKind _handleKind;

	/// <summary>Set once the drag has actually changed the curve, so a grab that goes nowhere does
	/// not push an undo step or a rebuild — the same rule the point drag follows.</summary>
	private bool _handleMoved;

	/// <summary>Where the CURSOR was when the grip was grabbed, and whether the undo snapshot for
	/// this drag has been taken yet.
	///
	/// SketchEditing captures the document as it is when it is called, so it has to fire before the
	/// first write - which means the decision to snapshot is made from the cursor having moved
	/// rather than from the curve having changed, because by the time the curve has changed the
	/// state worth going back to is gone.</summary>
	private Vec2 _handleGrabbedAt;
	private bool _handleSnapshotTaken;

	/// <summary>
	/// How far the cursor must travel before a press on a grip counts as a drag rather than a
	/// click, in SCREEN PIXELS.
	///
	/// A press with no dead zone at all is still a drag by a pixel or two, because a hand moves
	/// while a button goes down - so clicking a grip to select its curve would move the curve by a
	/// hair and put an undo step on the stack for it. Small enough that a real drag feels immediate.
	/// </summary>
	private const float HandleDragDeadZonePixels = 2f;

	/// <summary>The grip the status line is currently describing, so the prompt is pushed when the
	/// hover CHANGES rather than on every frame of it.</summary>
	private string _promptedHandleId;

	/// <summary>Whether a grip is being dragged right now. The selection code has to know: the
	/// cursor leaves the grip the moment the drag starts moving, and a curve underneath it must not
	/// quietly become selected on the way past.</summary>
	public bool DraggingCurveHandle => _handleCurveId is not null;

	/// <summary>Whether a grip owns this cursor, hovered or held.</summary>
	private bool CursorOnCurveHandle => _hoverHandle is not null || DraggingCurveHandle;

	/// <summary>Grip radius and pick radius in SCREEN PIXELS. Same reasoning as the point handles:
	/// a sketch can be one unit across or a thousand, and a fixed world size is either invisible or
	/// swallows the drawing.</summary>
	private const float CurveHandlePixels = 3.5f;
	private const float CurveHandlePickPixels = 7f;

	/// <summary>A resting grip: visible, and quieter than a sketch point, because a point is real
	/// geometry and this is a control that happens to be drawn on it.</summary>
	private static readonly Color CurveHandleColor = new( 0.55f, 0.8f, 0.95f, 0.55f );

	/// <summary>
	/// Hit-test, draw and drag the curve grips.
	///
	/// Hit-tested in plane coordinates rather than with a Gizmo hitbox, which the point handles use.
	/// Hitboxes compete by depth and every one of these sits on the same plane as the points, so the
	/// winner between a grip and a point a few pixels away would be decided by the projection rather
	/// than by which the user is aiming at. Measuring on the plane lets the order be stated instead:
	/// points first, and a grip only gets the cursor when nothing above it wanted it.
	/// </summary>
	private void SketchCurveHandleFrame()
	{
		_hoverHandle = null;

		if ( ActiveSketch is null || SketchTool != SketchToolKind.Select )
		{
			EndCurveHandleDrag();
			_promptedHandleId = null;
			return;
		}

		if ( DraggingCurveHandle )
		{
			DragCurveHandle();
			return;
		}

		var handles = SketchHandles.For( ActiveSketch );

		if ( handles.Count == 0 )
			return;

		var units = UnitsPerPixel();
		var radius = units * CurveHandlePixels;

		// A grip is only grabbable when nothing above it in the order wants the cursor. It is still
		// DRAWN in all those cases — hiding it as the cursor passes a point would make the sketch
		// flicker, and the thing being communicated is where the grips are, not which is armed.
		var grabbable = _canvasHasCursor && _cursorOnPlaneValid && _dragPoint < 0 && _hoverPoint < 0
			&& !CursorOnConstraintMark;

		var reach = units * CurveHandlePickPixels;
		var best = reach;

		if ( grabbable )
		{
			foreach ( var handle in handles )
			{
				var distance = Dist( _cursorOnPlane, handle.At );

				if ( distance >= best )
					continue;

				best = distance;
				_hoverHandle = handle;
			}
		}

		Gizmo.Draw.IgnoreDepth = true;

		foreach ( var handle in handles )
		{
			var hovered = _hoverHandle is { } hover && hover.CurveId == handle.CurveId;

			Gizmo.Draw.Color = hovered ? SketchDragColor : CurveHandleColor;
			Gizmo.Draw.SolidSphere( PlaneToWorld( handle.At ), hovered ? units * CurveHandlePickPixels * 0.65f : radius, 10, 10 );
		}

		Gizmo.Draw.IgnoreDepth = false;

		PushHandlePrompt();

		if ( _hoverHandle is { } grabbed && Gizmo.WasLeftMousePressed )
		{
			_handleCurveId = grabbed.CurveId;
			_handleKind = grabbed.Kind;
			_handleGrabbedAt = grabbed.At;
			_handleMoved = false;
			_handleSnapshotTaken = false;
		}
	}

	private void DragCurveHandle()
	{
		// Released: commit once, with a rebuild, rather than on every frame of the drag.
		if ( !Gizmo.IsLeftMouseDown )
		{
			var changed = _handleMoved;
			var dragged = _handleSnapshotTaken;
			var curveId = _handleCurveId;

			EndCurveHandleDrag();

			// Rebuild only when the geometry really moved. Crossing the dead zone and being refused
			// the whole way - an arc held flat on its own chord - is a drag that did nothing, and
			// nothing is what should happen at the far end of it.
			if ( changed )
				Edited();

			// A PRESS THAT NEVER LEFT THE DEAD ZONE IS A CLICK ON THE CURVE, and selects it - the
			// same rule the point handles follow. Without this the grip would be a dead spot in the
			// middle of every line, which is exactly where a person clicks to select one.
			else if ( !dragged )
				Toggle( curveId );

			return;
		}

		// Past the dead zone this is a drag; inside it, it is still a click that has not been let go
		// of yet, and the curve must not move at all.
		var travelled = _cursorOnPlaneValid
			&& Dist( _cursorOnPlane, _handleGrabbedAt ) >= UnitsPerPixel() * HandleDragDeadZonePixels;

		if ( travelled )
		{
			// The grid and the alignment guides still apply, so a circle can be pulled to a round
			// radius. POINT SNAPPING DOES NOT: it would drag a line's middle onto an unrelated
			// corner, and jump a circle's rim onto whatever point was nearest the size being aimed
			// for. Neither is anything the user asked for by grabbing a grip.
			_suppressPointSnap = true;
			var target = SnapPoint( _cursorOnPlane );
			_suppressPointSnap = false;

			// BEFORE the write, and once for the whole drag, so undo goes back to the shape as it
			// was picked up rather than to a frame into the gesture.
			if ( !_handleSnapshotTaken )
			{
				SketchEditing?.Invoke();
				_handleSnapshotTaken = true;
			}

			if ( SketchHandles.Drag( ActiveSketch, _handleCurveId, _handleKind, target ) )
			{
				_handleMoved = true;

				// Re-solved around a point the drag is deliberately NOT moving - see
				// SketchHandles.Pin. Without this a grip walks the sketch straight through its own
				// constraints, the same way the point drag used to.
				if ( ActiveSketch.Constraints.Count > 0 )
					SketchSolver.Solve( ActiveSketch, SketchHandles.Pin( ActiveSketch, _handleCurveId, _handleKind ) );
			}
		}

		// The grip is redrawn from where the curve ACTUALLY ended up rather than at the cursor: a
		// refused drag - a circle pulled to nothing, an arc flattened onto its chord - has to look
		// refused rather than leaving the grip hanging where the geometry would not follow.
		if ( CurrentHandlePosition() is { } at )
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = SketchDragColor;
			Gizmo.Draw.SolidSphere( PlaneToWorld( at ), UnitsPerPixel() * CurveHandlePickPixels * 0.65f, 10, 10 );
			Gizmo.Draw.IgnoreDepth = false;
		}
	}

	/// <summary>
	/// Say what the grip under the cursor does, and put the ordinary prompt back when the cursor
	/// leaves it.
	///
	/// A dot on a curve is not self-explanatory - the one on a circle changes its radius and the one
	/// on a line moves the whole thing, and nothing about either says so. The status line already
	/// exists to answer exactly this question for every tool, so it answers it here too.
	/// </summary>
	private void PushHandlePrompt()
	{
		var hovered = _hoverHandle?.CurveId;

		if ( hovered == _promptedHandleId )
			return;

		_promptedHandleId = hovered;

		SketchPromptChanged?.Invoke( _hoverHandle is { } handle
			? HandlePrompt( handle.Kind )
			: SelectionPrompt() ?? CurrentPrompt() );
	}

	private static string HandlePrompt( CurveHandleKind kind ) => kind switch
	{
		CurveHandleKind.LineMiddle => "Drag to move the whole line, or click to select it",
		CurveHandleKind.ArcBulge => "Drag to change how far the arc bulges - both of its ends stay put",
		CurveHandleKind.CircleRim => "Drag to change the circle's radius",
		CurveHandleKind.EllipseMinor => "Drag to change the ellipse's minor axis",
		_ => "Drag to change this curve",
	};

	private Vec2? CurrentHandlePosition()
	{
		var curve = ActiveSketch?.Curves.Find( c => c.Id == _handleCurveId );

		return curve is null ? null : SketchHandles.At( ActiveSketch, curve );
	}

	private void EndCurveHandleDrag()
	{
		_handleCurveId = null;
		_handleMoved = false;
		_handleSnapshotTaken = false;
	}
}
