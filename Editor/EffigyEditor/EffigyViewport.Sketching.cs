using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>Which sketch tool the next viewport click feeds. Mirrors Onshape's sketch toolbar.</summary>
internal enum SketchToolKind
{
	Select,
	Line,
	Rectangle,
	RectangleCentre,
	Circle,
	CircleThreePoint,
	Arc,
	ArcThreePoint,
	Polygon,
	PolygonCircumscribed,
	Slot,
	Point,
}

/// <summary>
/// Plane picking and sketch drawing — the interactive half of the viewport.
///
/// Split out of EffigyViewport.cs because the two concerns barely touch: that file owns the
/// camera, the reference planes and the origin handle, and this one owns a click-driven state
/// machine that only ever reads them. Keeping the state machine separate is also what makes it
/// readable — a half-drawn rectangle and a half-drawn arc are different amounts of pending
/// state, and interleaving them with rendering code was unreadable in the first draft.
/// </summary>
internal sealed partial class EffigyViewport
{
	// --- plane picking ----------------------------------------------------------------------

	/// <summary>While true the three reference planes are pickable and highlight on hover. Set by
	/// the feature dialog when its plane selection box is armed.</summary>
	public bool PlanePickMode { get; set; }

	/// <summary>Fires with the picked plane index — 0 Top (XY), 1 Front (XZ), 2 Right (YZ) —
	/// matching SketchFeature.Plane's ChoiceParam order.</summary>
	public Action<int> PlanePicked { get; set; }

	/// <summary>Plane index under the cursor this frame, or -1. Drawn brighter so it's obvious
	/// what a click would select.</summary>
	private int _hoveredPlane = -1;

	/// <summary>Half-thickness of a plane's pick volume. The planes are drawn as flat wireframe,
	/// which has no volume to hit, so each gets a slab this deep to click on.</summary>
	private const float PlanePickThickness = 1.5f;

	/// <summary>
	/// A clickable slab per reference plane, only while picking is armed.
	///
	/// Called from DrawReferencePlanes so the hitboxes track OriginPosition exactly like the
	/// wireframe does — they were separate at first and drifted apart the moment the origin was
	/// dragged, which made planes pick from where they used to be.
	/// </summary>
	private void DrawPlaneHitboxes()
	{
		_hoveredPlane = -1;

		if ( !PlanePickMode )
			return;

		var s = PlaneSize;

		// Slab dimensions per plane: flat along that plane's normal, full size on the other two.
		HitPlane( 0, new Vector3( s * 2f, s * 2f, PlanePickThickness ) );  // Top   (XY), normal Z
		HitPlane( 1, new Vector3( s * 2f, PlanePickThickness, s * 2f ) );  // Front (XZ), normal Y
		HitPlane( 2, new Vector3( PlanePickThickness, s * 2f, s * 2f ) );  // Right (YZ), normal X
	}

	private void HitPlane( int index, Vector3 size )
	{
		using var scope = Gizmo.Scope( $"plane-pick-{index}", new Transform( OriginPosition ) );

		Gizmo.Hitbox.BBox( BBox.FromPositionAndSize( Vector3.Zero, size ) );

		if ( !Gizmo.IsHovered )
			return;

		_hoveredPlane = index;

		if ( Gizmo.WasLeftMousePressed )
			PlanePicked?.Invoke( index );
	}

	/// <summary>Wash the hovered plane in its own colour so the pick target is unambiguous.
	/// Onshape does the same thing — the plane you are about to choose fills in.</summary>
	private void DrawHoveredPlaneHighlight()
	{
		if ( _hoveredPlane < 0 )
			return;

		var (right, up, colour) = _hoveredPlane switch
		{
			0 => (Vector3.Forward, Vector3.Left, new Color( 0.85f, 0.55f, 0.25f, 0.18f )),
			1 => (Vector3.Forward, Vector3.Up, new Color( 0.25f, 0.5f, 0.85f, 0.18f )),
			_ => (Vector3.Left, Vector3.Up, new Color( 0.25f, 0.78f, 0.45f, 0.18f )),
		};

		var c = OriginPosition;
		var s = PlaneSize;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = colour;

		// Two triangles, drawn as a solid quad. Gizmo.Draw has no quad primitive.
		var a = c + right * s + up * s;
		var b = c - right * s + up * s;
		var d = c - right * s - up * s;
		var e = c + right * s - up * s;

		Gizmo.Draw.SolidTriangle( new Triangle( a, b, d ) );
		Gizmo.Draw.SolidTriangle( new Triangle( a, d, e ) );

		Gizmo.Draw.IgnoreDepth = false;
	}

	// --- sketch mode ------------------------------------------------------------------------

	/// <summary>The sketch being edited, or null when not in sketch mode.</summary>
	public Sketch ActiveSketch { get; private set; }

	public bool IsSketching => ActiveSketch is not null;

	/// <summary>Which tool the next click feeds.</summary>
	public SketchToolKind SketchTool { get; set; } = SketchToolKind.Select;

	/// <summary>Change the active sketch tool through one state boundary. Switching tools abandons
	/// the half-finished entity, matching CAD sketchers instead of carrying stale clicks into the
	/// next tool.</summary>
	public void SetSketchTool( SketchToolKind tool )
	{
		if ( SketchTool == tool )
			return;

		_pending.Clear();
		SketchTool = tool;
		PushPrompt();
	}

	/// <summary>Raised after any edit to the active sketch, so the studio can rebuild.</summary>
	public Action SketchEdited { get; set; }

	/// <summary>Raised with a one-line prompt for the status bar — "click the first corner",
	/// and so on. A CAD tool that doesn't say what it wants next is unusable.</summary>
	public Action<string> SketchPromptChanged { get; set; }

	/// <summary>Number of sides a Polygon tool click produces.</summary>
	public int PolygonSides { get; set; } = 6;

	/// <summary>Draw construction geometry — reference lines and circles that shape the sketch but
	/// never become part of a profile. SketchCurve.Construction already exists in the kernel and
	/// ProfileFinder already skips them; this is just the switch for it.</summary>
	public bool ConstructionMode { get; set; }

	/// <summary>Show Onshape-style sketch diagnostics: loose endpoints and branching points.</summary>
	public bool ProfileInspector { get; set; }

	/// <summary>Grid snap in sketch units. Zero disables it.</summary>
	public float SketchGridSnap { get; set; } = 0.25f;

	/// <summary>Points clicked so far for the tool in progress. Cleared on completion or Escape.</summary>
	private readonly List<Vec2> _pending = new();

	/// <summary>Where the cursor last hit the sketch plane, for rubber-band preview.</summary>
	private Vec2 _cursorOnPlane;
	private bool _cursorOnPlaneValid;

	/// <summary>The plane-picking click must not also become the first sketch click. Plane picking
	/// and sketch input happen in the same viewport pass, so entering sketch mode during the plane
	/// hitbox callback otherwise starts a line from that original click.</summary>
	private bool _ignoreNextSketchClick;

	/// <summary>Existing sketch point the cursor is snapping to, or -1. Drawn as a ring so the
	/// snap is visible before you commit to it — without that, closing a profile is guesswork.</summary>
	private int _snapPoint = -1;
	private int _inferenceAxis;

	/// <summary>Radius in world units within which the cursor snaps to an existing point.</summary>
	private const float SnapRadius = 4f;

	/// <summary>Distance in sketch units for horizontal/vertical inference. This is deliberately
	/// smaller than point snapping: alignment should assist a click, not pull it across the sketch.</summary>
	private const float AlignmentSnapRadius = 1f;

	public void BeginSketch( Sketch sketch )
	{
		ActiveSketch = sketch;
		SketchTool = SketchToolKind.Line;
		_pending.Clear();
		// Keep the user's current camera. CursorToPlane projects onto the selected plane from any
		// view, so entering a sketch does not need to force a new perspective or zoom level.
		PushPrompt();
	}

	/// <summary>Plane picking and sketch input share one viewport frame. Mark the picking click so
	/// it cannot also become the first line endpoint when the sketch opens.</summary>
	public void IgnoreNextSketchClick() => _ignoreNextSketchClick = true;

	public void EndSketch()
	{
		ActiveSketch = null;
		SketchTool = SketchToolKind.Select;
		_pending.Clear();
		_ignoreNextSketchClick = false;
		SketchPromptChanged?.Invoke( "" );
	}

	/// <summary>Cancel the half-drawn thing, or the tool itself if nothing is half-drawn. Same
	/// two-stage Escape as Onshape.</summary>
	public void CancelSketchTool()
	{
		if ( _pending.Count > 0 )
		{
			_pending.Clear();
			PushPrompt();
			return;
		}

		SketchTool = SketchToolKind.Select;
		PushPrompt();
	}

	/// <summary>Look straight down the sketch plane's normal, the way entering a sketch in any CAD
	/// package does. Drawing on a plane seen edge-on is impossible, so this is not optional.</summary>
	public void LookAtSketchPlane()
	{
		if ( ActiveSketch is null )
			return;

		var normal = ToWorldDir( ActiveSketch.Plane.Normal );
		var up = ToWorldDir( ActiveSketch.Plane.YAxis );
		var centre = OriginPosition + ToWorldDir( ActiveSketch.Plane.Origin );

		_camera.WorldPosition = centre + normal * (PlaneSize * 1.6f);
		_camera.WorldRotation = Rotation.LookAt( -normal, up );
		_camera.ZNear = 0.5f;
	}

	// --- kernel <-> world -------------------------------------------------------------------

	// Effigy's Vec3 and s&box's Vector3 are the same axes in the same order (x forward, y left,
	// z up) - the OBJ exporter writes kernel coordinates straight through and the result stood at
	// correct scale next to a citizen. So this is a re-type, not a transform.
	private static Vector3 ToWorldDir( Vec3 v ) => new( v.x, v.y, v.z );

	/// <summary>Sketch-plane (u,v) to a point in the viewport, including the origin handle's
	/// offset so sketch geometry sits on the reference planes as drawn.</summary>
	private Vector3 PlaneToWorld( Vec2 uv )
	{
		var p = ActiveSketch.Plane;
		return OriginPosition + ToWorldDir( p.Origin ) + ToWorldDir( p.XAxis ) * uv.x + ToWorldDir( p.YAxis ) * uv.y;
	}

	/// <summary>Intersect the cursor ray with the sketch plane. False when the plane is edge-on or
	/// behind the camera, in which case there is no sensible point to report.</summary>
	private bool CursorToPlane( out Vec2 uv )
	{
		uv = Vec2.Zero;

		if ( ActiveSketch is null )
			return false;

		var p = ActiveSketch.Plane;
		var origin = OriginPosition + ToWorldDir( p.Origin );
		var normal = ToWorldDir( p.Normal );

		var ray = Gizmo.CurrentRay;
		var denom = Vector3.Dot( ray.Forward, normal );

		if ( MathF.Abs( denom ) < 1e-5f )
			return false;

		var t = Vector3.Dot( origin - ray.Position, normal ) / denom;

		if ( t <= 0f )
			return false;

		var hit = ray.Position + ray.Forward * t;
		var d = hit - origin;

		uv = new Vec2( Vector3.Dot( d, ToWorldDir( p.XAxis ) ), Vector3.Dot( d, ToWorldDir( p.YAxis ) ) );
		return true;
	}

	/// <summary>
	/// Snap the raw plane hit to an existing sketch point if one is close, otherwise to the grid.
	///
	/// Point snapping is what makes closed profiles possible at all: Sketch.AddPoint appends
	/// unconditionally, so two clicks at visually the same place produce two points a hair apart,
	/// and ProfileFinder then sees an open chain and refuses to extrude it.
	/// </summary>
	private Vec2 SnapPoint( Vec2 raw )
	{
		_snapPoint = -1;
		_inferenceAxis = 0;

		// The active line is the strongest inference target. Evaluate it before generic point/grid
		// snapping so a near-horizontal or near-vertical second click cannot be swallowed by a less
		// useful grid result.
		if ( SketchTool == SketchToolKind.Line && _pending.Count == 1 )
		{
			var start = _pending[0];
			var dx = MathF.Abs( raw.x - start.x );
			var dy = MathF.Abs( raw.y - start.y );

			if ( dx <= AlignmentSnapRadius && dx <= dy )
			{
				_inferenceAxis = 1;
				raw = new Vec2( start.x, raw.y );
			}
			else if ( dy <= AlignmentSnapRadius )
			{
				_inferenceAxis = 2;
				raw = new Vec2( raw.x, start.y );
			}
		}

		var best = SnapRadius * SnapRadius;

		for ( var i = 0; i < ActiveSketch.Points.Count; i++ )
		{
			var dist = (ActiveSketch.Points[i] - raw).LengthSquared;

			if ( dist >= best )
				continue;

			best = dist;
			_snapPoint = i;
		}

		if ( _snapPoint >= 0 )
			return ActiveSketch.Points[_snapPoint];

		var snapped = SketchGridSnap > 0f
			? new Vec2(
				MathF.Round( raw.x / SketchGridSnap ) * SketchGridSnap,
				MathF.Round( raw.y / SketchGridSnap ) * SketchGridSnap )
			: raw;

		// Onshape's inference lines make it easy to keep geometry level or vertical. Use the
		// existing sketch points plus the two sketch axes as alignment targets. Point snapping above
		// still wins when the cursor is actually on a point.
		var xTarget = 0f;
		var yTarget = 0f;
		var xDistance = MathF.Abs( snapped.x );
		var yDistance = MathF.Abs( snapped.y );

		foreach ( var point in ActiveSketch.Points )
		{
			var dx = MathF.Abs( snapped.x - point.x );
			if ( dx < xDistance )
			{
				xDistance = dx;
				xTarget = point.x;
			}

			var dy = MathF.Abs( snapped.y - point.y );
			if ( dy < yDistance )
			{
				yDistance = dy;
				yTarget = point.y;
			}
		}

		if ( xDistance <= AlignmentSnapRadius )
		{
			snapped = new Vec2( xTarget, snapped.y );
			_inferenceAxis |= 1;
		}

		if ( yDistance <= AlignmentSnapRadius )
		{
			snapped = new Vec2( snapped.x, yTarget );
			_inferenceAxis |= 2;
		}

		// The active line gets its own inference target. This is what makes the second click stay
		// horizontal or vertical with the first click even when there are no other points nearby.
		if ( SketchTool == SketchToolKind.Line && _pending.Count == 1 && _inferenceAxis == 0 )
		{
			var start = _pending[0];
			var dx = MathF.Abs( snapped.x - start.x );
			var dy = MathF.Abs( snapped.y - start.y );

			if ( dx <= AlignmentSnapRadius && dx <= dy )
			{
				snapped = new Vec2( start.x, snapped.y );
				_inferenceAxis |= 1;
			}
			else if ( dy <= AlignmentSnapRadius )
			{
				snapped = new Vec2( snapped.x, start.y );
				_inferenceAxis |= 2;
			}
		}

		return snapped;
	}

	/// <summary>Reuse an existing point index when the coordinate already exists, so shared corners
	/// really are shared and the chain closes. The kernel's AddPoint deliberately does not do this
	/// - it is a UI concern, and a coordinate-typing caller wants the literal point.</summary>
	private int PointIndex( Vec2 p )
	{
		for ( var i = 0; i < ActiveSketch.Points.Count; i++ )
		{
			if ( (ActiveSketch.Points[i] - p).LengthSquared < 1e-8f )
				return i;
		}

		return ActiveSketch.AddPoint( p );
	}

	// --- per-frame sketch pass ---------------------------------------------------------------

	private void SketchFrame()
	{
		if ( ActiveSketch is null )
			return;

		// && short-circuits past the out parameter, so the hover test has to come first on its own.
		_cursorOnPlaneValid = false;
		var raw = Vec2.Zero;

		if ( _canvasHasCursor )
			_cursorOnPlaneValid = CursorToPlane( out raw );

		if ( _cursorOnPlaneValid )
			_cursorOnPlane = SketchTool == SketchToolKind.Select ? raw : SnapPoint( raw );

		DrawSketch();
		DrawPendingPreview();

		if ( _ignoreNextSketchClick )
		{
			_ignoreNextSketchClick = false;
			return;
		}

		if ( _canvasHasCursor && _cursorOnPlaneValid && Gizmo.WasLeftMousePressed && SketchTool != SketchToolKind.Select )
			ClickTool( _cursorOnPlane );
	}

	/// <summary>Feed one click to whichever tool is active. Each tool collects the points it needs
	/// in _pending and commits when it has them all.</summary>
	private void ClickTool( Vec2 p )
	{
		_pending.Add( p );

		switch ( SketchTool )
		{
			case SketchToolKind.Point:
				PointIndex( p );
				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.Line when _pending.Count == 2:
				var line = Track( new SketchLine( PointIndex( _pending[0] ), PointIndex( _pending[1] ) ) );
				if ( (_inferenceAxis & 1) != 0 )
					ActiveSketch.AddConstraint( line, SketchConstraintKind.Vertical );
				else if ( (_inferenceAxis & 2) != 0 )
					ActiveSketch.AddConstraint( line, SketchConstraintKind.Horizontal );

				// Chain: the end of this line is the start of the next, which is how every CAD
				// line tool behaves. Escape breaks the chain.
				var last = _pending[1];
				_pending.Clear();
				_pending.Add( last );
				Edited();
				break;

			case SketchToolKind.RectangleCentre when _pending.Count == 2:
				var span = _pending[1] - _pending[0];
				var cmin = new Vec2( _pending[0].x - MathF.Abs( span.x ), _pending[0].y - MathF.Abs( span.y ) );
				var cmax = new Vec2( _pending[0].x + MathF.Abs( span.x ), _pending[0].y + MathF.Abs( span.y ) );

				if ( cmax.x - cmin.x > 1e-4f && cmax.y - cmin.y > 1e-4f )
					AddPolygonReusing( RectangleCorners( cmin, cmax ) );

				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.CircleThreePoint when _pending.Count == 3:
				if ( Circumcentre( _pending[0], _pending[1], _pending[2] ) is { } cc )
					Track( new SketchCircle( PointIndex( cc ), Dist( cc, _pending[0] ) ) );

				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.ArcThreePoint when _pending.Count == 3:
				CommitThreePointArc();
				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.Slot when _pending.Count == 3:
				CommitSlot();
				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.PolygonCircumscribed when _pending.Count == 2:
				var apothem = Dist( _pending[0], _pending[1] );

				if ( apothem > 1e-4f )
					AddPolygonReusing( RegularPolygon( _pending[0], _pending[1], PolygonSides, circumscribed: true ) );

				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.Rectangle when _pending.Count == 2:
				var min = new Vec2( MathF.Min( _pending[0].x, _pending[1].x ), MathF.Min( _pending[0].y, _pending[1].y ) );
				var max = new Vec2( MathF.Max( _pending[0].x, _pending[1].x ), MathF.Max( _pending[0].y, _pending[1].y ) );

				if ( max.x - min.x > 1e-4f && max.y - min.y > 1e-4f )
					AddPolygonReusing( RectangleCorners( min, max ) );

				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.Circle when _pending.Count == 2:
				var radius = Dist( _pending[0], _pending[1] );

				if ( radius > 1e-4f )
					Track( new SketchCircle( PointIndex( _pending[0] ), radius ) );

				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.Arc when _pending.Count == 3:
				CommitArc();
				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.Polygon when _pending.Count == 2:
				var r = Dist( _pending[0], _pending[1] );

				if ( r > 1e-4f )
					AddPolygonReusing( RegularPolygon( _pending[0], _pending[1], PolygonSides ) );

				_pending.Clear();
				Edited();
				break;
		}

		PushPrompt();
	}

	/// <summary>
	/// Centre/start/end arc, with the end click only supplying a direction.
	///
	/// The third click will not usually land exactly on the circle through the second, and
	/// SketchArc stores three point indices with no radius of its own - so an end point off the
	/// circle is not a slightly-wrong arc, it is an inconsistent one. Projecting the end onto the
	/// radius makes the click mean "this direction", which is what the user is aiming at anyway.
	/// </summary>
	private void CommitArc()
	{
		var centre = _pending[0];
		var start = _pending[1];
		var radius = Dist( centre, start );

		if ( radius <= 1e-4f )
			return;

		var toEnd = _pending[2] - centre;

		if ( toEnd.Length <= 1e-4f )
			return;

		var end = centre + toEnd.Normal * radius;

		// Counter-clockwise unless the click sits clockwise of the start, so the arc runs the
		// short way round toward where the cursor actually was.
		var cross = (start.x - centre.x) * (end.y - centre.y) - (start.y - centre.y) * (end.x - centre.x);

		Track( new SketchArc( PointIndex( centre ), PointIndex( start ), PointIndex( end ), cross < 0f ) );
	}

	/// <summary>Closed loop of lines through the given corners, reusing point indices. Sketch's own
	/// AddPolygon always adds fresh points, which breaks snapping onto existing geometry.</summary>
	private void AddPolygonReusing( Vec2[] corners )
	{
		var indices = corners.Select( PointIndex ).ToArray();

		for ( var i = 0; i < indices.Length; i++ )
		{
			var a = indices[i];
			var b = indices[(i + 1) % indices.Length];

			if ( a != b )
				Track( new SketchLine( a, b ) );
		}
	}

	private static Vec2[] RectangleCorners( Vec2 min, Vec2 max ) => new[]
	{
		new Vec2( min.x, min.y ),
		new Vec2( max.x, min.y ),
		new Vec2( max.x, max.y ),
		new Vec2( min.x, max.y ),
	};

	/// <summary>Regular n-gon inscribed in the circle through the second click, with one vertex at
	/// that click so the shape follows the cursor's angle.</summary>
	/// <summary>Regular n-gon. Inscribed puts a vertex on the clicked point; circumscribed puts an
	/// edge midpoint there, so the click distance is the apothem — the two variants Onshape's
	/// polygon dropdown offers, and they give different sizes for the same click.</summary>
	private static Vec2[] RegularPolygon( Vec2 centre, Vec2 vertex, int sides, bool circumscribed = false )
	{
		sides = Math.Clamp( sides, 3, 64 );

		var spoke = vertex - centre;
		var radius = spoke.Length;
		var start = MathF.Atan2( spoke.y, spoke.x );

		if ( circumscribed )
		{
			// Clicked distance is the apothem, so grow to the circumradius and rotate half a step
			// to put the edge midpoint under the cursor instead of a corner.
			radius /= MathF.Cos( MathF.PI / sides );
			start -= MathF.PI / sides;
		}

		var corners = new Vec2[sides];

		for ( var i = 0; i < sides; i++ )
		{
			var a = start + i * MathF.Tau / sides;
			corners[i] = new Vec2( centre.x + MathF.Cos( a ) * radius, centre.y + MathF.Sin( a ) * radius );
		}

		return corners;
	}

	private static float Dist( Vec2 a, Vec2 b ) => (b - a).Length;

	/// <summary>Add a curve, stamping the construction flag on it. Every tool goes through here so
	/// the toggle cannot be silently skipped by one of them.</summary>
	private T Track<T>( T curve ) where T : SketchCurve
	{
		curve.Construction = ConstructionMode;
		return ActiveSketch.Add( curve );
	}

	/// <summary>Centre of the circle through three points, or null when they are collinear — in
	/// which case there is no circle and the tool should quietly do nothing rather than divide by
	/// zero and scatter a NaN through the sketch.</summary>
	private static Vec2? Circumcentre( Vec2 a, Vec2 b, Vec2 c )
	{
		var d = 2f * (a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y));

		if ( MathF.Abs( d ) < 1e-7f )
			return null;

		var a2 = a.x * a.x + a.y * a.y;
		var b2 = b.x * b.x + b.y * b.y;
		var c2 = c.x * c.x + c.y * c.y;

		return new Vec2(
			(a2 * (b.y - c.y) + b2 * (c.y - a.y) + c2 * (a.y - b.y)) / d,
			(a2 * (c.x - b.x) + b2 * (a.x - c.x) + c2 * (b.x - a.x)) / d );
	}

	private static float NormAngle( float a )
	{
		while ( a < 0f ) a += MathF.Tau;
		while ( a >= MathF.Tau ) a -= MathF.Tau;
		return a;
	}

	/// <summary>
	/// Start, end, and a point the arc passes through — Onshape's 3-point arc, and the one people
	/// actually reach for, because you rarely know where the centre is.
	///
	/// Direction comes from whether the through-point falls inside the counter-clockwise sweep
	/// from start to end. Guessing it instead gives an arc that takes the long way round half the
	/// time, which looks like a bug even though the endpoints are right.
	/// </summary>
	private void CommitThreePointArc()
	{
		var start = _pending[0];
		var end = _pending[1];
		var through = _pending[2];

		if ( Circumcentre( start, through, end ) is not { } centre )
			return;

		var a0 = MathF.Atan2( start.y - centre.y, start.x - centre.x );
		var toEnd = NormAngle( MathF.Atan2( end.y - centre.y, end.x - centre.x ) - a0 );
		var toThrough = NormAngle( MathF.Atan2( through.y - centre.y, through.x - centre.x ) - a0 );

		Track( new SketchArc( PointIndex( centre ), PointIndex( start ), PointIndex( end ), toThrough > toEnd ) );
	}

	/// <summary>
	/// A straight slot: two parallel lines capped by two semicircles. Three clicks — the two ends
	/// of the centre line, then the width.
	///
	/// Built from the primitives the kernel already has rather than added as a curve type, because
	/// a slot is not a new kind of geometry, it is a shape. Both caps sweep clockwise so the four
	/// curves form one continuous loop that ProfileFinder can close.
	/// </summary>
	private void CommitSlot()
	{
		var a = _pending[0];
		var b = _pending[1];
		var axis = b - a;

		if ( axis.Length < 1e-4f )
			return;

		var dir = axis.Normal;
		var perp = new Vec2( -dir.y, dir.x );

		// Width is the clicked point's distance from the centre line, so the slot follows the
		// cursor sideways rather than needing a typed number.
		var half = MathF.Abs( Vec2.Dot( _pending[2] - a, perp ) );

		if ( half < 1e-4f )
			return;

		var p1 = PointIndex( a + perp * half );
		var p2 = PointIndex( b + perp * half );
		var p3 = PointIndex( b - perp * half );
		var p4 = PointIndex( a - perp * half );
		var ca = PointIndex( a );
		var cb = PointIndex( b );

		Track( new SketchLine( p1, p2 ) );
		Track( new SketchArc( cb, p2, p3, true ) );
		Track( new SketchLine( p3, p4 ) );
		Track( new SketchArc( ca, p4, p1, true ) );
	}

	private void Edited()
	{
		SketchEdited?.Invoke();
	}

	// --- sketch rendering --------------------------------------------------------------------

	private static readonly Color SketchColor = new( 0.35f, 0.75f, 1f, 0.95f );
	private static readonly Color SketchPointColor = new( 0.95f, 0.95f, 0.95f, 0.9f );
	private static readonly Color SketchPreviewColor = new( 1f, 0.8f, 0.3f, 0.9f );
	private static readonly Color SketchConstructionColor = new( 0.6f, 0.55f, 0.9f, 0.7f );
	private static readonly Color SketchRegionColor = new( 0.25f, 0.65f, 1f, 0.12f );
	private static readonly Color SketchGapColor = new( 1f, 0.35f, 0.15f, 1f );
	private static readonly Color SketchConstrainedColor = new( 0.08f, 0.08f, 0.08f, 1f );

	/// <summary>Draw every curve already in the sketch, plus its points.</summary>
	private void DrawSketch()
	{
		var profiles = ProfileFinder.Find( ActiveSketch );

		// Closed regions are selectable areas in Onshape, not just wire loops. A fan is sufficient
		// for the convex profiles currently supported by the sketch tools and keeps the fill behind
		// the edge work so it never hides the actual geometry.
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = SketchRegionColor;
		foreach ( var profile in profiles.Profiles )
			DrawRegionFan( profile.Outer );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2f;
		Gizmo.Draw.Color = SketchColor;

		foreach ( var curve in ActiveSketch.Curves )
		{
			var pts = curve.Tessellate( ActiveSketch, ActiveSketch.Tolerance );

			// Construction geometry is dashed and dimmed - it never becomes part of a profile, so
			// it must not read as an edge you are about to extrude.
			var constrained = ActiveSketch.Constraints.Any( c => c.CurveId == curve.Id );
			Gizmo.Draw.Color = curve.Construction
				? SketchConstructionColor
				: constrained ? SketchConstrainedColor : SketchColor;
			Gizmo.Draw.LineThickness = curve.Construction ? 1f : 2f;

			for ( var i = 0; i < pts.Count - 1; i++ )
			{
				if ( curve.Construction && i % 2 == 1 )
					continue;

				Gizmo.Draw.Line( PlaneToWorld( pts[i] ), PlaneToWorld( pts[i + 1] ) );
			}
		}

		Gizmo.Draw.LineThickness = 2f;

		// Points last so they sit on top of the curves through them.
		Gizmo.Draw.Color = SketchPointColor;

		foreach ( var p in ActiveSketch.Points )
			Gizmo.Draw.SolidSphere( PlaneToWorld( p ), 0.45f, 6, 6 );

		if ( ProfileInspector )
			DrawProfileDiagnostics();

		if ( _snapPoint >= 0 && _snapPoint < ActiveSketch.Points.Count )
		{
			Gizmo.Draw.Color = SketchPreviewColor;
			Gizmo.Draw.SolidSphere( PlaneToWorld( ActiveSketch.Points[_snapPoint] ), 0.9f, 8, 8 );
		}

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;

		if ( _inferenceAxis != 0 && _pending.Count > 0 )
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = SketchPreviewColor.WithAlpha( 0.7f );
			Gizmo.Draw.LineThickness = 1f;
			var start = _pending[0];

			if ( (_inferenceAxis & 1) != 0 )
				Gizmo.Draw.Line( PlaneToWorld( new Vec2( start.x, -PlaneSize ) ), PlaneToWorld( new Vec2( start.x, PlaneSize ) ) );

			if ( (_inferenceAxis & 2) != 0 )
				Gizmo.Draw.Line( PlaneToWorld( new Vec2( -PlaneSize, start.y ) ), PlaneToWorld( new Vec2( PlaneSize, start.y ) ) );

			Gizmo.Draw.IgnoreDepth = false;
		}
	}

	private void DrawRegionFan( List<Vec2> loop )
	{
		if ( loop.Count < 3 )
			return;

		var centre = loop[0];
		for ( var i = 1; i < loop.Count - 1; i++ )
			Gizmo.Draw.SolidTriangle( new Triangle(
				PlaneToWorld( centre ), PlaneToWorld( loop[i] ), PlaneToWorld( loop[i + 1] ) ) );
	}

	/// <summary>Highlight every non-construction point whose endpoint degree is not exactly two.
	/// Degree one is a loose end; degree three or more is a branch the profile walker cannot choose
	/// between. Both are the classic "looks closed but will not shade" failure.</summary>
	private void DrawProfileDiagnostics()
	{
		var degree = new Dictionary<int, int>();

		foreach ( var curve in ActiveSketch.Curves.Where( c => !c.Construction ) )
		{
			switch ( curve )
			{
				case SketchLine line:
					AddDegree( line.Start );
					AddDegree( line.End );
					break;
				case SketchArc arc:
					AddDegree( arc.Start );
					AddDegree( arc.End );
					break;
			}
		}

		Gizmo.Draw.Color = SketchGapColor;
		foreach ( var (point, count) in degree )
		{
			if ( count == 2 )
				continue;

			Gizmo.Draw.SolidSphere( PlaneToWorld( ActiveSketch.Points[point] ), count > 2 ? 0.8f : 0.7f, 8, 8 );
		}

		void AddDegree( int point ) => degree[point] = degree.TryGetValue( point, out var count ) ? count + 1 : 1;
	}

	/// <summary>Rubber-band the shape the current clicks would produce if the next click landed
	/// where the cursor is. Without this you are drawing blind.</summary>
	private void DrawPendingPreview()
	{
		if ( !_cursorOnPlaneValid || SketchTool == SketchToolKind.Select )
			return;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2f;
		Gizmo.Draw.Color = SketchPreviewColor;

		// Cursor crosshair, so the snapped position is visible even with nothing pending.
		var c = PlaneToWorld( _cursorOnPlane );
		Gizmo.Draw.SolidSphere( c, 0.5f, 6, 6 );

		if ( _pending.Count > 0 )
		{
			switch ( SketchTool )
			{
				case SketchToolKind.Line:
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					break;

				case SketchToolKind.Rectangle:
					DrawLoopPreview( RectangleCorners(
						new Vec2( MathF.Min( _pending[0].x, _cursorOnPlane.x ), MathF.Min( _pending[0].y, _cursorOnPlane.y ) ),
						new Vec2( MathF.Max( _pending[0].x, _cursorOnPlane.x ), MathF.Max( _pending[0].y, _cursorOnPlane.y ) ) ) );
					break;

				case SketchToolKind.Circle:
					DrawCirclePreview( _pending[0], Dist( _pending[0], _cursorOnPlane ) );
					break;

				case SketchToolKind.RectangleCentre:
					var span = _cursorOnPlane - _pending[0];
					DrawLoopPreview( RectangleCorners(
						new Vec2( _pending[0].x - MathF.Abs( span.x ), _pending[0].y - MathF.Abs( span.y ) ),
						new Vec2( _pending[0].x + MathF.Abs( span.x ), _pending[0].y + MathF.Abs( span.y ) ) ) );
					break;

				case SketchToolKind.CircleThreePoint when _pending.Count == 1:
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					break;

				case SketchToolKind.CircleThreePoint:
					if ( Circumcentre( _pending[0], _pending[1], _cursorOnPlane ) is { } rimCentre )
						DrawCirclePreview( rimCentre, Dist( rimCentre, _pending[0] ) );
					break;

				case SketchToolKind.ArcThreePoint when _pending.Count == 1:
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					break;

				case SketchToolKind.ArcThreePoint:
					DrawThreePointArcPreview( _pending[0], _pending[1], _cursorOnPlane );
					break;

				case SketchToolKind.Slot when _pending.Count == 1:
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					break;

				case SketchToolKind.Slot:
					DrawSlotPreview( _pending[0], _pending[1], _cursorOnPlane );
					break;

				case SketchToolKind.PolygonCircumscribed:
					DrawLoopPreview( RegularPolygon( _pending[0], _cursorOnPlane, PolygonSides, circumscribed: true ) );
					break;

				case SketchToolKind.Polygon:
					DrawLoopPreview( RegularPolygon( _pending[0], _cursorOnPlane, PolygonSides ) );
					break;

				case SketchToolKind.Arc when _pending.Count == 1:
					DrawCirclePreview( _pending[0], Dist( _pending[0], _cursorOnPlane ) );
					break;

				case SketchToolKind.Arc:
					DrawCirclePreview( _pending[0], Dist( _pending[0], _pending[1] ) );
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					break;
			}
		}

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
	}

	private void DrawLoopPreview( Vec2[] corners )
	{
		for ( var i = 0; i < corners.Length; i++ )
			Gizmo.Draw.Line( PlaneToWorld( corners[i] ), PlaneToWorld( corners[(i + 1) % corners.Length] ) );
	}

	private void DrawThreePointArcPreview( Vec2 start, Vec2 end, Vec2 through )
	{
		if ( Circumcentre( start, through, end ) is not { } centre )
		{
			// Collinear - there is no arc, so show the chord rather than nothing.
			Gizmo.Draw.Line( PlaneToWorld( start ), PlaneToWorld( end ) );
			return;
		}

		var radius = Dist( centre, start );
		var a0 = MathF.Atan2( start.y - centre.y, start.x - centre.x );
		var toEnd = NormAngle( MathF.Atan2( end.y - centre.y, end.x - centre.x ) - a0 );
		var toThrough = NormAngle( MathF.Atan2( through.y - centre.y, through.x - centre.x ) - a0 );

		var sweep = toThrough > toEnd ? toEnd - MathF.Tau : toEnd;
		var steps = Math.Max( 8, (int)(MathF.Abs( sweep ) * 12f) );
		var prev = start;

		for ( var i = 1; i <= steps; i++ )
		{
			var ang = a0 + sweep * (i / (float)steps);
			var next = new Vec2( centre.x + MathF.Cos( ang ) * radius, centre.y + MathF.Sin( ang ) * radius );
			Gizmo.Draw.Line( PlaneToWorld( prev ), PlaneToWorld( next ) );
			prev = next;
		}
	}

	private void DrawSlotPreview( Vec2 a, Vec2 b, Vec2 widthPoint )
	{
		var axis = b - a;

		if ( axis.Length < 1e-4f )
			return;

		var dir = axis.Normal;
		var perp = new Vec2( -dir.y, dir.x );
		var half = MathF.Abs( Vec2.Dot( widthPoint - a, perp ) );

		if ( half < 1e-4f )
			return;

		Gizmo.Draw.Line( PlaneToWorld( a + perp * half ), PlaneToWorld( b + perp * half ) );
		Gizmo.Draw.Line( PlaneToWorld( a - perp * half ), PlaneToWorld( b - perp * half ) );

		DrawCirclePreview( a, half );
		DrawCirclePreview( b, half );
	}

	private void DrawCirclePreview( Vec2 centre, float radius )
	{
		if ( radius <= 1e-4f )
			return;

		const int segments = 48;
		var prev = new Vec2( centre.x + radius, centre.y );

		for ( var i = 1; i <= segments; i++ )
		{
			var a = i * MathF.Tau / segments;
			var next = new Vec2( centre.x + MathF.Cos( a ) * radius, centre.y + MathF.Sin( a ) * radius );
			Gizmo.Draw.Line( PlaneToWorld( prev ), PlaneToWorld( next ) );
			prev = next;
		}
	}

	// --- prompts ------------------------------------------------------------------------------

	private void PushPrompt()
	{
		SketchPromptChanged?.Invoke( CurrentPrompt() );
	}

	private string CurrentPrompt() => SketchTool switch
	{
		SketchToolKind.Line when _pending.Count == 0 => "Line - click the start point",
		SketchToolKind.Line => "Line - click the end point, or press Escape to break the chain",
		SketchToolKind.Rectangle when _pending.Count == 0 => "Rectangle - click the first corner",
		SketchToolKind.Rectangle => "Rectangle - click the opposite corner",
		SketchToolKind.Circle when _pending.Count == 0 => "Circle - click the centre",
		SketchToolKind.Circle => "Circle - click a point on the circumference",
		SketchToolKind.Arc when _pending.Count == 0 => "Arc - click the centre",
		SketchToolKind.Arc when _pending.Count == 1 => "Arc - click the start point",
		SketchToolKind.Arc => "Arc - click the end direction",
		SketchToolKind.Polygon when _pending.Count == 0 => $"{PolygonSides}-sided polygon - click the centre",
		SketchToolKind.Polygon => $"{PolygonSides}-sided polygon - click a corner",
		SketchToolKind.RectangleCentre when _pending.Count == 0 => "Centre rectangle - click the centre",
		SketchToolKind.RectangleCentre => "Centre rectangle - click a corner",
		SketchToolKind.CircleThreePoint when _pending.Count < 2 => $"3-point circle - click point {_pending.Count + 1} of 3 on the rim",
		SketchToolKind.CircleThreePoint => "3-point circle - click the third point on the rim",
		SketchToolKind.ArcThreePoint when _pending.Count == 0 => "3-point arc - click the start point",
		SketchToolKind.ArcThreePoint when _pending.Count == 1 => "3-point arc - click the end point",
		SketchToolKind.ArcThreePoint => "3-point arc - click a point the arc passes through",
		SketchToolKind.PolygonCircumscribed when _pending.Count == 0 => $"{PolygonSides}-sided circumscribed polygon - click the centre",
		SketchToolKind.PolygonCircumscribed => $"{PolygonSides}-sided circumscribed polygon - click an edge midpoint",
		SketchToolKind.Slot when _pending.Count == 0 => "Slot - click one end of the centre line",
		SketchToolKind.Slot when _pending.Count == 1 => "Slot - click the other end of the centre line",
		SketchToolKind.Slot => "Slot - click to set the width",
		SketchToolKind.Point => "Point - click to place",
		_ => "Sketching - pick a tool from the sketch toolbar",
	};
}
