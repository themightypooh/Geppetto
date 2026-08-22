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
		if ( index == 0 && !TopPlaneVisible || index == 1 && !FrontPlaneVisible || index == 2 && !RightPlaneVisible )
			return;

		using var scope = Gizmo.Scope( $"plane-pick-{index}", new Transform( OriginPosition ) );

		Gizmo.Hitbox.BBox( BBox.FromPositionAndSize( Vector3.Zero, size ) );

		if ( !Gizmo.IsHovered )
			return;

		_hoveredPlane = index;

		if ( Gizmo.WasLeftMousePressed )
			PlanePicked?.Invoke( index );
	}

	// --- sketch picking ---------------------------------------------------------------------

	/// <summary>A committed sketch the viewport can offer for picking. Extrude and Revolve use
	/// this to choose the profile they consume — the same "click what you mean" affordance the
	/// plane selector gives a new Sketch.</summary>
	internal sealed class PickableSketch
	{
		public string FeatureId;
		public string Name;
		public Sketch Sketch;

		public PickableSketch( string featureId, string name, Sketch sketch )
		{
			FeatureId = featureId;
			Name = name;
			Sketch = sketch;
		}
	}

	/// <summary>While true the committed sketches are pickable and highlight on hover. Set by
	/// the feature dialog when its sketch selection box is armed.</summary>
	public bool SketchPickMode { get; set; }

	/// <summary>Fires with the picked sketch's SketchFeature id.</summary>
	public Action<string> SketchPicked { get; set; }

	/// <summary>Raised when Escape cancels an armed pick mode, so the dialog's selection box
	/// can stand down too — the viewport owns the key, the box owns its painted state.</summary>
	public Action PickModeCancelled { get; set; }

	/// <summary>Sketches currently offered for picking, pushed by the window after each
	/// rebuild. Only sketches that sit before the feature being edited are listed.</summary>
	public IReadOnlyList<PickableSketch> PickableSketches => _pickableSketches;

	private readonly List<PickableSketch> _pickableSketches = new();

	/// <summary>Feature id of the sketch under the cursor this frame, or null.</summary>
	private string _hoveredSketchId;

	/// <summary>Distance in sketch units within which the cursor counts as pointing at a
	/// sketch's curves. Generous on purpose: the curves are thin and the part may be small.</summary>
	private const float SketchPickRadius = 5f;

	public void SetPickableSketches( IEnumerable<PickableSketch> sketches )
	{
		_pickableSketches.Clear();

		if ( sketches is not null )
			_pickableSketches.AddRange( sketches );
	}

	/// <summary>Status-bar prompt for pick modes, reusing the sketch prompt channel — it is the
	/// same "what the tool wants next" line.</summary>
	public void SetPickPrompt( string text ) => SketchPromptChanged?.Invoke( text );

	/// <summary>
	/// Hover and click resolution for sketch picking. A sketch is a set of curves on a plane, so
	/// "pointing at it" means the cursor ray lands on that plane near one of its curves — no
	/// hitbox exists for that shape, and a bounding slab would overlap every sketch on the same
	/// plane. Nearest curve within SketchPickRadius wins.
	/// </summary>
	private void SketchPickFrame()
	{
		_hoveredSketchId = null;

		if ( !SketchPickMode || IsSketching || _pickableSketches.Count == 0 || !_canvasHasCursor )
			return;

		var ray = Gizmo.CurrentRay;
		var best = SketchPickRadius;

		foreach ( var pickable in _pickableSketches )
		{
			if ( !RayToPlane( pickable.Sketch.Plane, ray.Position, ray.Forward, out var uv ) )
				continue;

			foreach ( var curve in pickable.Sketch.Curves )
			{
				if ( curve.Construction )
					continue;

				foreach ( var p in curve.Tessellate( pickable.Sketch, pickable.Sketch.Tolerance ) )
				{
					var dist = (p - uv).Length;

					if ( dist < best )
					{
						best = dist;
						_hoveredSketchId = pickable.FeatureId;
					}
				}
			}
		}

		if ( _hoveredSketchId is null )
			return;

		DrawSketchPickHighlight( _hoveredSketchId );

		if ( Gizmo.WasLeftMousePressed )
			SketchPicked?.Invoke( _hoveredSketchId );
	}

	/// <summary>Intersect a ray with any sketch plane. The active-sketch version above this is
	/// the same math against ActiveSketch.Plane.</summary>
	private bool RayToPlane( SketchPlane p, Vector3 rayPosition, Vector3 rayForward, out Vec2 uv )
	{
		uv = Vec2.Zero;

		var origin = OriginPosition + ToWorldDir( p.Origin );
		var normal = ToWorldDir( p.Normal );
		var denom = Vector3.Dot( rayForward, normal );

		if ( MathF.Abs( denom ) < 1e-5f )
			return false;

		var t = Vector3.Dot( origin - rayPosition, normal ) / denom;

		if ( t <= 0f )
			return false;

		var hit = rayPosition + rayForward * t;
		var d = hit - origin;

		uv = new Vec2( Vector3.Dot( d, ToWorldDir( p.XAxis ) ), Vector3.Dot( d, ToWorldDir( p.YAxis ) ) );
		return true;
	}

	/// <summary>Redraw the hovered sketch bright and filled so the pick target is unambiguous —
	/// the same fill-in treatment the reference planes get while picking one.</summary>
	private void DrawSketchPickHighlight( string featureId )
	{
		var pickable = _pickableSketches.FirstOrDefault( s => s.FeatureId == featureId );

		if ( pickable is null )
			return;

		var sketch = pickable.Sketch;
		var plane = sketch.Plane;

		Gizmo.Draw.IgnoreDepth = true;

		Gizmo.Draw.Color = new Color( 0.25f, 0.65f, 1f, 0.25f );
		foreach ( var profile in ProfileFinder.Find( sketch ).Profiles )
			DrawRegionFan( plane, profile.Outer );

		Gizmo.Draw.LineThickness = 3f;
		Gizmo.Draw.Color = new Color( 0.45f, 0.85f, 1f, 1f );

		foreach ( var curve in sketch.Curves )
		{
			if ( curve.Construction )
				continue;

			var pts = curve.Tessellate( sketch, sketch.Tolerance );

			for ( var i = 0; i < pts.Count - 1; i++ )
				Gizmo.Draw.Line( PlaneToWorld( plane, pts[i] ), PlaneToWorld( plane, pts[i + 1] ) );
		}

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
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
		ClearDimension();

		if ( SketchTool == tool )
			return;

		_pending.Clear();
		_chainStartIndex = -1;
		SketchTool = tool;
		PushPrompt();
	}

	/// <summary>Raised after any edit to the active sketch, so the studio can rebuild.</summary>
	public Action SketchEdited { get; set; }

	/// <summary>
	/// Raised immediately BEFORE the active sketch changes, so the window can take an undo
	/// snapshot while the old state still exists.
	///
	/// Separate from SketchEdited because "after" is useless for undo: by the time the studio
	/// hears about an edit, the thing to go back to is gone. Fired once per user action - a
	/// committed entity, a dragged point, a typed dimension - not once per frame.
	/// </summary>
	public Action SketchEditing { get; set; }

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

	/// <summary>Grid snap in sketch units. Zero means AUTOMATIC — a 1/2/5 x 10^n step chosen so the
	/// grid stays about GridPixels apart on screen however far you are zoomed in.</summary>
	public float SketchGridSnap { get; set; } = 0f;

	/// <summary>Snapping and point reuse, which are sketch maths rather than UI and therefore live
	/// in the kernel where they can be tested without s&amp;box. This only feeds it tolerances.</summary>
	private readonly SketchSnapper _snapper = new();

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
	private int _chainStartIndex = -1;

	/// <summary>Sketches from finished SketchFeatures, drawn dimmer so the user can see
	/// committed geometry without it competing with the active sketch.</summary>
	private List<Sketch> _displaySketches = new();
	private readonly HashSet<Sketch> _hiddenSketches = new();

	// --- point dragging ---------------------------------------------------------------------

	/// <summary>Index of the point being dragged, or -1. Dragging is a SELECT-tool action: while a
	/// drawing tool is armed every click is meant to place geometry, and grabbing an existing point
	/// instead is how you end up moving the sketch when you meant to add to it.</summary>
	private int _dragPoint = -1;

	/// <summary>The point under the cursor while the Select tool is active, or -1. Drawn larger so
	/// there is something to aim at before you press.</summary>
	private int _hoverPoint = -1;

	/// <summary>Set once the drag has actually moved the point, so a click that grabs and releases
	/// without moving does not push an undo step or a rebuild.</summary>
	private bool _dragMoved;

	/// <summary>Handle radius in SCREEN PIXELS, converted to sketch units per frame. Sketches can
	/// be one unit across or a thousand; a fixed world radius is either invisible or covers the
	/// whole sketch.</summary>
	private const float PointHandlePixels = 7f;

	/// <summary>
	/// Hit-test, highlight and drag the active sketch's points.
	///
	/// The hitbox/hover/press dance is the editor's own gizmo machinery, the same as the origin
	/// handle and RigViewport's bone handles, so a point competes for the cursor on equal terms
	/// with everything else in the scene instead of being hit-tested by hand.
	///
	/// The drag itself is direct rather than a three-arrow control: a sketch point has two degrees
	/// of freedom, both in the plane, and it follows the cursor through the same snapping every
	/// click goes through - so a dragged point lands on the grid, lines up with its neighbours and
	/// merges onto another point exactly the way a drawn one does.
	/// </summary>
	private void SketchPointHandles()
	{
		_hoverPoint = -1;

		if ( ActiveSketch is null || SketchTool != SketchToolKind.Select )
		{
			EndPointDrag();
			return;
		}

		if ( _dragPoint >= ActiveSketch.Points.Count )
			EndPointDrag();

		var radius = UnitsPerPixel() * PointHandlePixels;

		if ( _dragPoint >= 0 )
		{
			DragPoint( radius );
			return;
		}

		for ( var i = 0; i < ActiveSketch.Points.Count; i++ )
		{
			using var scope = Gizmo.Scope( $"sketch-point-{i}", new Transform( PlaneToWorld( ActiveSketch.Points[i] ) ) );

			Gizmo.Hitbox.DepthBias = 0.01f;
			Gizmo.Hitbox.Sphere( new Sphere( 0f, radius ) );

			if ( !Gizmo.IsHovered )
				continue;

			_hoverPoint = i;

			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = SketchDragColor;
			Gizmo.Draw.SolidSphere( 0f, radius, 10, 10 );
			Gizmo.Draw.IgnoreDepth = false;

			if ( Gizmo.WasLeftMousePressed )
			{
				_dragPoint = i;
				_dragMoved = false;
			}

			break;
		}
	}

	private void DragPoint( float radius )
	{
		var index = _dragPoint;

		// Released: commit once, with a rebuild, rather than on every frame of the drag.
		if ( !Gizmo.IsLeftMouseDown )
		{
			EndPointDrag();

			if ( _dragMoved )
				Edited();

			return;
		}

		if ( _cursorOnPlaneValid )
		{
			// Everything else in the sketch is a snap target; the point in your hand is not, or it
			// would snap straight back onto itself and never move.
			_snapper.IgnorePoint = index;
			var target = SnapPoint( _cursorOnPlane );
			_snapper.IgnorePoint = -1;

			if ( Dist( target, ActiveSketch.Points[index] ) > 1e-6f )
			{
				// Snapshot on the FIRST movement, before the point is written: grabbing a point
				// and letting go without moving it should not be an undo step, and the whole drag
				// should be one step rather than one per frame.
				if ( !_dragMoved )
					SketchEditing?.Invoke();

				ActiveSketch.Points[index] = target;
				_dragMoved = true;
			}
		}

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = SketchDragColor;
		Gizmo.Draw.SolidSphere( PlaneToWorld( ActiveSketch.Points[index] ), radius, 10, 10 );
		Gizmo.Draw.IgnoreDepth = false;
	}

	private void EndPointDrag()
	{
		_dragPoint = -1;
		_dragMoved = false;
	}

	// --- dimensions -------------------------------------------------------------------------

	/// <summary>
	/// The line the dimension box is currently attached to: the one just drawn, until the next
	/// click replaces it or Escape dismisses it. Null means no box.
	///
	/// This is the second half of Onshape's dimension behaviour. The first half is the live
	/// readout while you drag, which is drawn straight from the pending points and needs no state
	/// at all; this is the part where the number stops being a readout and becomes an input.
	/// </summary>
	private SketchLine _dimensionLine;

	/// <summary>What has been typed into the box so far. Empty means the box is showing the
	/// line's measured length instead - typing the first digit is what replaces it, the same as
	/// typing into a field whose contents were selected.</summary>
	private string _dimensionInput = "";

	/// <summary>Screen-pixel size of every dimension readout.</summary>
	private const float DimensionTextSize = 13f;

	/// <summary>The live readout while dragging: quiet, because it is only telling you what you
	/// are already doing.</summary>
	private static readonly Color DimensionLiveColor = new( 0.78f, 0.86f, 0.95f, 0.95f );

	/// <summary>The committed dimension: bright, because it is now something you can type into.
	/// The colour change IS the signal that the number went from readout to field.</summary>
	private static readonly Color DimensionEditColor = new( 0.35f, 0.85f, 1f, 1f );

	private static readonly Color DimensionBoxColor = new( 0.08f, 0.10f, 0.12f, 0.92f );

	/// <summary>Enough digits to be useful without turning into noise. Sketch units are
	/// dimensionless, so there is no sensible fixed precision - scale it to the value.</summary>
	private static string FormatLength( float value ) =>
		value >= 100f ? value.ToString( "F1" )
		: value >= 10f ? value.ToString( "F2" )
		: value.ToString( "F3" );

	/// <summary>Text pinned to a world position but drawn at a fixed SCREEN size, so a readout is
	/// as legible zoomed out as zoomed in. Gizmo.Draw.WorldText scales with distance and would be
	/// unreadable at exactly the moments you need it.</summary>
	private static void DrawDimensionText( Vector3 world, string text, Color color, float yOffset = -16f )
	{
		Gizmo.Draw.Color = color;
		Gizmo.Draw.ScreenText( text, world, new Vector2( 0f, yOffset ), "Roboto", DimensionTextSize, TextFlag.Center );
	}

	private void ClearDimension()
	{
		_dimensionLine = null;
		_dimensionInput = "";
	}

	/// <summary>
	/// The boxed, editable dimension on the line that was just drawn.
	///
	/// The box is built from the CAMERA's right and up vectors rather than the sketch plane's, so
	/// it faces you from any angle, and it is sized in world units converted from pixels through
	/// UnitsPerPixel - the same conversion the snap tolerances use - so it stays one size on
	/// screen however far in you are zoomed.
	/// </summary>
	private void DrawDimensionBox()
	{
		if ( _dimensionLine is null || ActiveSketch is null )
			return;

		if ( _dimensionLine.Start >= ActiveSketch.Points.Count || _dimensionLine.End >= ActiveSketch.Points.Count )
		{
			ClearDimension();
			return;
		}

		var a = ActiveSketch.Points[_dimensionLine.Start];
		var b = ActiveSketch.Points[_dimensionLine.End];
		var text = _dimensionInput.Length > 0 ? _dimensionInput : FormatLength( Dist( a, b ) );

		var centre = PlaneToWorld( (a + b) * 0.5f );
		var units = UnitsPerPixel();
		var rotation = Gizmo.CameraTransform.Rotation;

		// Roughly the width of the glyphs plus padding. Paint's text metrics are not available
		// from inside a Gizmo pass, and a box a few pixels wide of the number is not a problem.
		var right = rotation.Right * ((text.Length * 7.5f + 16f) * 0.5f * units);
		var up = rotation.Up * (11f * units);
		var lift = rotation.Up * (16f * units);

		var p0 = centre - right - up + lift;
		var p1 = centre + right - up + lift;
		var p2 = centre + right + up + lift;
		var p3 = centre - right + up + lift;

		Gizmo.Draw.IgnoreDepth = true;

		Gizmo.Draw.Color = DimensionBoxColor;
		Gizmo.Draw.SolidTriangle( p0, p1, p2 );
		Gizmo.Draw.SolidTriangle( p0, p2, p3 );

		Gizmo.Draw.Color = DimensionEditColor;
		Gizmo.Draw.LineThickness = 1.5f;
		Gizmo.Draw.Line( p0, p1 );
		Gizmo.Draw.Line( p1, p2 );
		Gizmo.Draw.Line( p2, p3 );
		Gizmo.Draw.Line( p3, p0 );

		DrawDimensionText( centre, text, DimensionEditColor );

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
	}

	/// <summary>
	/// Keys typed while a dimension box is up. Returns true when the key was consumed, so the
	/// viewport's own Escape handling does not also see it.
	///
	/// Digits and a decimal point go into the box, Enter applies, Escape dismisses it - and
	/// dismissing is a SEPARATE stage from Escape's existing back-out-of-the-tool behaviour, so
	/// leaving the number alone does not also throw away the tool you are drawing with.
	/// </summary>
	public bool HandleDimensionKey( KeyEvent e )
	{
		if ( _dimensionLine is null || ActiveSketch is null )
			return false;

		switch ( e.Key )
		{
			case KeyCode.Return:
			case KeyCode.Enter:
				ApplyDimension();
				e.Accepted = true;
				return true;

			case KeyCode.Backspace:
				if ( _dimensionInput.Length > 0 )
					_dimensionInput = _dimensionInput[..^1];

				e.Accepted = true;
				return true;

			case KeyCode.Escape:
				ClearDimension();
				e.Accepted = true;
				return true;
		}

		if ( string.IsNullOrEmpty( e.Text ) )
			return false;

		var c = e.Text[0];

		if ( !char.IsDigit( c ) && c != '.' )
			return false;

		_dimensionInput += c;
		e.Accepted = true;
		return true;
	}

	/// <summary>
	/// Set the line to the typed length by sliding its END point along its own direction, leaving
	/// the start where it is - which is what you mean when you draw a line and then type a number.
	///
	/// Points are shared by index (see SketchCurve), so moving the end point moves everything else
	/// joined to it. That is correct for the chain you are drawing: the next segment starts from
	/// the point that just moved, and _pending has to be dragged along with it or the chain would
	/// carry on from where the corner used to be.
	/// </summary>
	private void ApplyDimension()
	{
		if ( _dimensionLine is null || ActiveSketch is null )
			return;

		if ( !float.TryParse( _dimensionInput, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out var length ) || length <= 0f )
		{
			ClearDimension();
			return;
		}

		var start = ActiveSketch.Points[_dimensionLine.Start];
		var end = ActiveSketch.Points[_dimensionLine.End];
		var delta = end - start;

		if ( delta.Length < 1e-5f )
		{
			ClearDimension();
			return;
		}

		SketchEditing?.Invoke();

		var moved = start + delta.Normal * length;
		ActiveSketch.Points[_dimensionLine.End] = moved;

		for ( var i = 0; i < _pending.Count; i++ )
		{
			if ( Dist( _pending[i], end ) < 1e-5f )
				_pending[i] = moved;
		}

		ClearDimension();
		Edited();
	}

	public void SetSketchVisibility( Sketch sketch, bool visible )
	{
		if ( sketch is null )
			return;

		if ( visible )
			_hiddenSketches.Remove( sketch );
		else
			_hiddenSketches.Add( sketch );
	}

	/// <summary>Cursor-to-point snapping distance, in SCREEN PIXELS.</summary>
	private const float SnapPixels = 12f;

	/// <summary>Horizontal/vertical inference distance, in SCREEN PIXELS. Deliberately smaller than
	/// point snapping: alignment should assist a click, not pull it across the sketch.</summary>
	private const float AlignmentPixels = 7f;

	/// <summary>Roughly how far apart the snap grid should look on screen, in pixels.</summary>
	private const float GridPixels = 14f;

	/// <summary>
	/// How many sketch units one screen pixel covers, at the sketch plane's depth.
	///
	/// EVERY SNAP TOLERANCE IS DERIVED FROM THIS, and that is the whole fix. They used to be fixed
	/// sketch-unit constants - the point-snap radius was 4 units - which is reasonable on a sketch
	/// spanning tens of units and catastrophic on a part one unit across, where every existing
	/// point sits inside the snap radius of every new click. A four-corner rectangle collapsed to
	/// two points joined by degenerate zero-length lines; ProfileFinder counted those twice at the
	/// shared point, called it a branching sketch, and the region never closed. Zooming in made it
	/// worse, because the tolerance did not move with the view.
	/// </summary>
	private float UnitsPerPixel()
	{
		var plane = ActiveSketch?.Plane ?? SketchPlane.XY;
		var origin = OriginPosition + ToWorldDir( plane.Origin );
		var distance = MathF.Max( (origin - _camera.WorldPosition).Length, 0.01f );

		// Half the view's world height at that depth, over half its height in pixels.
		var halfHeight = MathF.Tan( _camera.FieldOfView.DegreeToRadian() * 0.5f ) * distance;

		return halfHeight / MathF.Max( _canvas.Size.y * 0.5f, 1f );
	}

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

	/// <summary>Replace the set of finished sketches drawn in the viewport. Called after each
	/// studio rebuild so the user can see committed geometry even when not actively sketching.</summary>
	public void SetDisplaySketches( IEnumerable<Sketch> sketches )
	{
		_displaySketches = sketches?.ToList() ?? new List<Sketch>();
	}

	// --- face-of-solid picking --------------------------------------------------------------

	/// <summary>While true, existing bodies are pickable by their faces — the "or click a face of
	/// something already built" half of choosing a sketch plane. Set by the plane selector when
	/// it offers this alongside the three reference planes.</summary>
	public bool FacePickMode { get; set; }

	/// <summary>Fires with a FaceRef built from the click — the body id, the hit point, and that
	/// face's normal. See FaceRef and FacePlane in the kernel for why it is geometry rather than a
	/// face index.</summary>
	public Action<FaceRef> FacePicked { get; set; }

	/// <summary>Bodies that can be clicked while FacePickMode is armed. Pushed by the window from
	/// PartStudio.Bodies, the same way SetDisplaySketches is pushed from the sketch features.</summary>
	public void SetPickableBodies( IEnumerable<Body> bodies )
	{
		_pickableBodies = bodies?.ToList() ?? new List<Body>();
	}

	private List<Body> _pickableBodies = new();

	/// <summary>Body id under the cursor this frame, or null — drawn brighter and reported to the
	/// status prompt, the same treatment PlanePickMode gives a hovered reference plane.</summary>
	private string _hoveredFaceBodyId;

	/// <summary>
	/// Resolve the click, if any, against every pickable body's actual triangles.
	///
	/// The raycast itself is MeshRaycast.Raycast in the kernel — pure geometry, proven by
	/// RaycastTests against a box's six faces and known normals. This is only the adapter: turn
	/// the cursor into a ray in kernel coordinates, and turn the winning hit into a FaceRef.
	/// </summary>
	private void FacePickFrame()
	{
		_hoveredFaceBodyId = null;

		if ( !FacePickMode || _pickableBodies.Count == 0 || !_canvasHasCursor )
			return;

		var ray = Gizmo.CurrentRay;

		// Vector3 -> Vec3 is a straight re-type, not a transform - ToWorldDir does the same thing
		// in the other direction elsewhere in this file, because the kernel's axes and s&box's
		// line up exactly (see EffigyTool's own note on this).
		var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
		var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

		var result = MeshRaycast.Raycast( _pickableBodies, origin, direction );

		if ( result is not { } hit )
			return;

		_hoveredFaceBodyId = hit.Body.Id;

		DrawHoveredFace( hit.Body, hit.Hit.FaceIndex );

		if ( !Gizmo.WasLeftMousePressed )
			return;

		// Capture rather than the raw constructor: it records WHERE ON THE FACE the click landed,
		// which is what lets the sketch ride the face when it later moves or resizes.
		FacePicked?.Invoke( FacePlane.Capture( hit.Body, hit.Hit.FaceIndex, hit.Hit.Point ) );
	}

	/// <summary>Shading and outline for the ONE face under the cursor while a sketch is choosing
	/// where to live.</summary>
	private static readonly Color FaceHighlightColor = new( 0.35f, 0.75f, 1f, 1f );

	/// <summary>
	/// Light up the exact face the cursor is over.
	///
	/// The raycast already knows which face it hit - it has to, to report a normal - so the pick
	/// was landing on a specific face while the viewport showed nothing at all. Clicking a face to
	/// sketch on it was therefore aim-and-hope, and on a part with several faces meeting at an
	/// angle there was no way to tell which one you were about to get.
	///
	/// Shaded with the same ear clipping the render mesh uses, so a concave face highlights as the
	/// shape it actually is.
	///
	/// DEPTH-TESTED, but nudged a hair toward the camera first. Drawing it through the geometry
	/// (IgnoreDepth) was worse than it sounds: a face that something else stands in front of - the
	/// side of a block another extrude grows out of - had its highlight painted straight over the
	/// thing in front, so it read as a rectangle passing through the model. Drawing it flat on the
	/// surface instead z-fights with the surface itself. Pulling each corner a fraction of its own
	/// view distance toward the camera gets both: whatever is genuinely in front occludes it, and
	/// the face it belongs to never fights it.
	/// </summary>
	private void DrawHoveredFace( Body body, int faceIndex )
	{
		if ( body?.Mesh is not { } mesh || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return;

		var face = mesh.Faces[faceIndex];

		if ( face.Count < 3 )
			return;

		var eye = _camera.WorldPosition;
		var corners = new List<Vector3>( face.Count );
		var flat = new List<Vec3>( face.Count );

		for ( var i = 0; i < face.Count; i++ )
		{
			var p = mesh.Positions[face.Indices[i]];
			// Lifted proportionally to distance, because depth precision is: a fixed nudge that
			// clears the surface up close is invisible across a large part, and vice versa.
			flat.Add( p );
			corners.Add( Lift( p, eye ) );
		}

		Gizmo.Draw.IgnoreDepth = false;

		Gizmo.Draw.Color = FaceHighlightColor.WithAlpha( 0.22f );

		foreach ( var (a, b, c) in Triangulate.Face( flat ) )
			Gizmo.Draw.SolidTriangle( new Triangle( corners[a], corners[b], corners[c] ) );

		Gizmo.Draw.Color = FaceHighlightColor;
		Gizmo.Draw.LineThickness = 2.5f;

		for ( var i = 0; i < corners.Count; i++ )
			Gizmo.Draw.Line( corners[i], corners[(i + 1) % corners.Count] );

		DrawFaceFootprints( body, mesh, face, flat, eye );

		Gizmo.Draw.LineThickness = 1f;
	}

	/// <summary>
	/// Outline where OTHER bodies meet this face.
	///
	/// A face's own perimeter is not the whole story of what is on it. Bodies are never unioned,
	/// so a block standing on a slab leaves the slab's top face as one unbroken rectangle - and
	/// highlighting only that rectangle says the face is clear when half of it is underneath
	/// something. These are the lines where the other solids actually land on it.
	///
	/// Clipped to the face rather than drawn whole: the section of a neighbouring body runs across
	/// the face's entire infinite plane, and the parts of it beyond this face's edges belong to
	/// nothing being highlighted.
	/// </summary>
	private void DrawFaceFootprints( Body body, PolyMesh mesh, Face face, List<Vec3> flat, Vector3 eye )
	{
		if ( _pickableBodies.Count < 2 )
			return;

		var normal = mesh.FaceNormal( face );
		var centroid = mesh.FaceCentroid( face );
		var plane = FacePlane.FromPointAndNormal( centroid, normal );

		var outline = new List<Vec2>( flat.Count );

		foreach ( var p in flat )
			outline.Add( plane.ToPlane( p ) );

		Gizmo.Draw.LineThickness = 2f;

		foreach ( var other in _pickableBodies )
		{
			if ( other?.Mesh is null || ReferenceEquals( other, body ) )
				continue;

			foreach ( var (a, b) in MeshSection.CrossSection( other.Mesh, centroid, normal ) )
			{
				foreach ( var (from, to) in ClipToPolygon( plane.ToPlane( a ), plane.ToPlane( b ), outline ) )
					Gizmo.Draw.Line( Lift( plane.ToWorld( from ), eye ), Lift( plane.ToWorld( to ), eye ) );
			}
		}
	}

	/// <summary>The parts of a segment that lie inside a polygon, in plane coordinates. Split at
	/// every edge crossing, then keep the pieces whose midpoint is inside - which needs no
	/// special cases for a segment that enters and leaves several times.</summary>
	private static List<(Vec2 From, Vec2 To)> ClipToPolygon( Vec2 start, Vec2 end, List<Vec2> polygon )
	{
		var kept = new List<(Vec2, Vec2)>();
		var direction = end - start;

		if ( direction.LengthSquared < 1e-12f || polygon.Count < 3 )
			return kept;

		var cuts = new List<float> { 0f, 1f };

		for ( var i = 0; i < polygon.Count; i++ )
		{
			var a = polygon[i];
			var edge = polygon[(i + 1) % polygon.Count] - a;
			var denominator = Vec2.Cross( direction, edge );

			if ( MathF.Abs( denominator ) < 1e-12f )
				continue;

			var t = Vec2.Cross( a - start, edge ) / denominator;
			var u = Vec2.Cross( a - start, direction ) / denominator;

			if ( t > 0f && t < 1f && u >= 0f && u <= 1f )
				cuts.Add( t );
		}

		cuts.Sort();

		for ( var i = 0; i + 1 < cuts.Count; i++ )
		{
			var from = start + direction * cuts[i];
			var to = start + direction * cuts[i + 1];

			if ( Inside( (from + to) * 0.5f, polygon ) )
				kept.Add( (from, to) );
		}

		return kept;
	}

	/// <summary>Crossing count: a ray from the point crosses the boundary an odd number of times
	/// exactly when the point is inside.</summary>
	private static bool Inside( Vec2 point, List<Vec2> polygon )
	{
		var inside = false;

		for ( int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++ )
		{
			var a = polygon[i];
			var b = polygon[j];

			if ( a.y > point.y != b.y > point.y
				&& point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x )
			{
				inside = !inside;
			}
		}

		return inside;
	}

	/// <summary>The same camera-ward nudge the highlight itself uses, so the footprint lines sit in
	/// front of the face rather than fighting it.</summary>
	private static Vector3 Lift( Vec3 point, Vector3 eye )
	{
		var world = new Vector3( point.x, point.y, point.z );
		var toEye = eye - world;

		return world + toEye.Normal * (toEye.Length * FaceHighlightLift);
	}

	/// <summary>How far toward the camera the highlight is pulled, as a fraction of the distance to
	/// each corner. Small enough that it never separates visibly from the face, large enough to
	/// clear the depth buffer.</summary>
	private const float FaceHighlightLift = 0.0015f;

	public void EndSketch()
	{
		ClearDimension();
		EndPointDrag();
		ActiveSketch = null;
		SketchTool = SketchToolKind.Select;
		_pending.Clear();
		_chainStartIndex = -1;
		_ignoreNextSketchClick = false;
		SketchPromptChanged?.Invoke( "" );
	}

	/// <summary>Cancel the half-drawn thing, or the tool itself if nothing is half-drawn. Same
	/// two-stage Escape as Onshape.</summary>
	public void CancelSketchTool()
	{
		ClearDimension();
		EndPointDrag();

		if ( _pending.Count > 0 )
		{
			_pending.Clear();
			_chainStartIndex = -1;
			PushPrompt();
			return;
		}

		SketchTool = SketchToolKind.Select;
		PushPrompt();
	}

	// --- kernel <-> world -------------------------------------------------------------------

	// Effigy's Vec3 and s&box's Vector3 are the same axes in the same order (x forward, y left,
	// z up) - the OBJ exporter writes kernel coordinates straight through and the result stood at
	// correct scale next to a citizen. So this is a re-type, not a transform.
	private static Vector3 ToWorldDir( Vec3 v ) => new( v.x, v.y, v.z );

	/// <summary>Sketch-plane (u,v) to a point in the viewport, including the origin handle's
	/// offset so sketch geometry sits on the reference planes as drawn.</summary>
	private Vector3 PlaneToWorld( Vec2 uv ) => PlaneToWorld( ActiveSketch.Plane, uv );

	private Vector3 PlaneToWorld( SketchPlane plane, Vec2 uv )
	{
		return OriginPosition + ToWorldDir( plane.Origin ) + ToWorldDir( plane.XAxis ) * uv.x + ToWorldDir( plane.YAxis ) * uv.y;
	}

	/// <summary>Intersect the cursor ray with the sketch plane. False when the plane is edge-on or
	/// behind the camera, in which case there is no sensible point to report.</summary>
	private bool CursorToPlane( out Vec2 uv )
	{
		uv = Vec2.Zero;

		if ( ActiveSketch is null )
			return false;

		return CursorToPlane( ActiveSketch.Plane, out uv );
	}

	/// <summary>Intersect the cursor ray with any sketch plane. Split out from the active-sketch
	/// version so a finished sketch's faces can be hit-tested without entering it.</summary>
	private bool CursorToPlane( SketchPlane p, out Vec2 uv )
	{
		uv = Vec2.Zero;

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
		// Tolerances are a fixed number of SCREEN PIXELS, converted here to sketch units at the
		// plane's depth. That conversion is the whole reason snapping survives a part one unit
		// across; see SketchSnapper for what it looked like when they were world constants.
		var unitsPerPixel = UnitsPerPixel();

		_snapper.PointRadius = SnapPixels * unitsPerPixel;
		_snapper.AlignmentRadius = AlignmentPixels * unitsPerPixel;
		_snapper.GridStep = SketchGridSnap > 0f
			? SketchGridSnap
			: SketchSnapper.AutoGridStep( unitsPerPixel, GridPixels );

		var result = _snapper.Snap( ActiveSketch, raw, _pending,
			SketchTool == SketchToolKind.Line && _pending.Count == 1 );

		_snapPoint = result.SnappedPointIndex;
		_inferenceAxis = result.InferenceAxis;

		return result.Point;
	}

	/// <summary>Point reuse lives in the kernel with the rest of the snapping - see
	/// SketchSnapper.PointIndex.</summary>
	private int PointIndex( Vec2 p ) => SketchSnapper.PointIndex( ActiveSketch, p );

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
		DrawDimensionBox();
		SketchPointHandles();

		if ( _ignoreNextSketchClick )
		{
			_ignoreNextSketchClick = false;
			return;
		}

		if ( _canvasHasCursor && _cursorOnPlaneValid && Gizmo.WasLeftMousePressed && SketchTool != SketchToolKind.Select && _dragPoint < 0 )
			ClickTool( _cursorOnPlane );
	}

	/// <summary>Feed one click to whichever tool is active. Each tool collects the points it needs
	/// in _pending and commits when it has them all.</summary>
	private void ClickTool( Vec2 p )
	{
		// The click is about to add points or a curve, so the state to go back to is the one that
		// exists right now.
		SketchEditing?.Invoke();

		// Any click moves on from the last dimension - the box belongs to the thing you just drew,
		// not to the thing you are drawing now. The line tool re-arms it below.
		ClearDimension();

		_pending.Add( p );

		switch ( SketchTool )
		{
			case SketchToolKind.Point:
				PointIndex( p );
				_pending.Clear();
				Edited();
				break;

			case SketchToolKind.Line when _pending.Count == 1:
				_chainStartIndex = PointIndex( _pending[0] );
				break;

			case SketchToolKind.Line when _pending.Count == 2:
			{
				var from = PointIndex( _pending[0] );
				var to = PointIndex( _pending[1] );

				// A ZERO-LENGTH LINE IS NOT GEOMETRY. It is what a click that snapped back onto its
				// own start point produces, and ProfileFinder links it into the adjacency map TWICE
				// at that one point - so a perfectly good corner reports as joining three curves,
				// the sketch is called branching, and the region never closes. AddPolygonReusing
				// has always guarded this; the line tool never did.
				if ( from != to )
				{
					var line = Track( new SketchLine( from, to ) );

					if ( (_inferenceAxis & 1) != 0 )
						ActiveSketch.AddConstraint( line, SketchConstraintKind.Vertical );
					else if ( (_inferenceAxis & 2) != 0 )
						ActiveSketch.AddConstraint( line, SketchConstraintKind.Horizontal );

					// The line is down; its length becomes an editable dimension until the next
					// click replaces it.
					_dimensionLine = line;
					_dimensionInput = "";
				}

				// Chain closes back to the start point — break the chain.
				if ( to == _chainStartIndex && _chainStartIndex >= 0 )
				{
					_pending.Clear();
					_chainStartIndex = -1;
				}
				else
				{
					// Chain: the end of this line is the start of the next.
					var last = _pending[1];
					_pending.Clear();
					_pending.Add( last );
				}
				Edited();
				break;
			}

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

	/// <summary>A point under the cursor, or in your hand. Warm, so it reads as "grabbable" rather
	/// than as another piece of the sketch.</summary>
	private static readonly Color SketchDragColor = new( 1f, 0.78f, 0.25f, 1f );
	private static readonly Color SketchConstrainedColor = new( 0.08f, 0.08f, 0.08f, 1f );

	/// <summary>Draw every curve already in the sketch, plus its points.</summary>
	private void DrawSketch()
	{
		var profiles = ProfileFinder.Find( ActiveSketch );

		// Closed regions are selectable areas in Onshape, not just wire loops. The fill is drawn
		// behind the edge work so it never hides the actual geometry.
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
			Gizmo.Draw.SolidSphere( PlaneToWorld( p ), 1.25f, 10, 10 );

		if ( ProfileInspector )
			DrawProfileDiagnostics();

		if ( _snapPoint >= 0 && _snapPoint < ActiveSketch.Points.Count )
		{
			Gizmo.Draw.Color = SketchPreviewColor;
			Gizmo.Draw.SolidSphere( PlaneToWorld( ActiveSketch.Points[_snapPoint] ), 1.5f, 10, 10 );
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

	private void DrawRegionFan( List<Vec2> loop ) => DrawRegionFan( ActiveSketch.Plane, loop );

	private void DrawRegionFan( SketchPlane plane, List<Vec2> loop )
	{
		if ( loop.Count < 3 )
			return;

		// Ear clipped rather than fanned, so the shading matches the region the extrude will
		// actually build - a fan shades a concave profile's notch as if it were part of the face.
		foreach ( var (a, b, c) in Triangulate.Polygon( loop ) )
		{
			Gizmo.Draw.SolidTriangle( new Triangle(
				PlaneToWorld( plane, loop[a] ), PlaneToWorld( plane, loop[b] ), PlaneToWorld( plane, loop[c] ) ) );
		}
	}

	/// <summary>
	/// Draw finished sketches from the feature tree so they remain visible after leaving sketch
	/// mode. Dimmer than the active sketch to avoid visual competition, but clear enough that the
	/// user can see what profiles exist.
	/// </summary>
	private void DrawCommittedSketches()
	{
		if ( _displaySketches.Count == 0 )
			return;

		// DEPTH-TESTED, unlike everything else this viewport overlays. A committed sketch sits in
		// the same place as the solid built from it, so drawing it with IgnoreDepth put its curves
		// and its shaded regions on top of the body - the part came out looking like a wireframe
		// shell you could see the far side of. The active sketch is drawn elsewhere and keeps its
		// draw-through behaviour; you are working on that one and it has to stay reachable.
		Gizmo.Draw.IgnoreDepth = false;

		foreach ( var sketch in _displaySketches )
		{
			if ( sketch == ActiveSketch || _hiddenSketches.Contains( sketch ) )
				continue;

			var profiles = ProfileFinder.Find( sketch );

			// Shade closed regions
			Gizmo.Draw.Color = SketchRegionColor.WithAlpha( 0.08f );
			foreach ( var profile in profiles.Profiles )
				DrawRegionFan( sketch.Plane, profile.Outer );

			// Draw curves
			foreach ( var curve in sketch.Curves )
			{
				var pts = curve.Tessellate( sketch, sketch.Tolerance );
				Gizmo.Draw.Color = curve.Construction
					? SketchConstructionColor.WithAlpha( 0.5f )
					: SketchColor.WithAlpha( 0.6f );
				Gizmo.Draw.LineThickness = curve.Construction ? 1f : 1.5f;

				for ( var i = 0; i < pts.Count - 1; i++ )
				{
					if ( curve.Construction && i % 2 == 1 )
						continue;

					Gizmo.Draw.Line( PlaneToWorld( sketch.Plane, pts[i] ), PlaneToWorld( sketch.Plane, pts[i + 1] ) );
				}
			}

			// Draw points
			Gizmo.Draw.LineThickness = 2f;
			Gizmo.Draw.Color = SketchPointColor.WithAlpha( 0.7f );
			foreach ( var p in sketch.Points )
				Gizmo.Draw.SolidSphere( PlaneToWorld( sketch.Plane, p ), 1.0f, 8, 8 );
		}

		Gizmo.Draw.LineThickness = 1f;
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

			Gizmo.Draw.SolidSphere( PlaneToWorld( ActiveSketch.Points[point] ), count > 2 ? 1.2f : 1.0f, 10, 10 );
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

		// Pending endpoints are not in ActiveSketch.Points until the entity is committed. Draw them
		// explicitly so the user can see the actual point the next click will connect to.
		foreach ( var p in _pending )
			Gizmo.Draw.SolidSphere( PlaneToWorld( p ), 1.5f, 10, 10 );

		// Cursor crosshair, so the snapped position is visible even with nothing pending.
		var c = PlaneToWorld( _cursorOnPlane );
		Gizmo.Draw.SolidSphere( c, 0.75f, 8, 8 );

		if ( _pending.Count > 0 )
		{
			switch ( SketchTool )
			{
				case SketchToolKind.Line:
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					LiveLength( _pending[0], _cursorOnPlane );
					break;

				case SketchToolKind.Rectangle:
					DrawLoopPreview( RectangleCorners(
						new Vec2( MathF.Min( _pending[0].x, _cursorOnPlane.x ), MathF.Min( _pending[0].y, _cursorOnPlane.y ) ),
						new Vec2( MathF.Max( _pending[0].x, _cursorOnPlane.x ), MathF.Max( _pending[0].y, _cursorOnPlane.y ) ) ) );
					LiveSize( _cursorOnPlane,
						MathF.Abs( _cursorOnPlane.x - _pending[0].x ), MathF.Abs( _cursorOnPlane.y - _pending[0].y ) );
					break;

				case SketchToolKind.Circle:
					DrawCirclePreview( _pending[0], Dist( _pending[0], _cursorOnPlane ) );
					LiveRadius( _pending[0], _cursorOnPlane );
					break;

				case SketchToolKind.RectangleCentre:
					var span = _cursorOnPlane - _pending[0];
					DrawLoopPreview( RectangleCorners(
						new Vec2( _pending[0].x - MathF.Abs( span.x ), _pending[0].y - MathF.Abs( span.y ) ),
						new Vec2( _pending[0].x + MathF.Abs( span.x ), _pending[0].y + MathF.Abs( span.y ) ) ) );
					LiveSize( _cursorOnPlane, MathF.Abs( span.x ) * 2f, MathF.Abs( span.y ) * 2f );
					break;

				case SketchToolKind.CircleThreePoint when _pending.Count == 1:
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					LiveLength( _pending[0], _cursorOnPlane );
					break;

				case SketchToolKind.CircleThreePoint:
					if ( Circumcentre( _pending[0], _pending[1], _cursorOnPlane ) is { } rimCentre )
					{
						DrawCirclePreview( rimCentre, Dist( rimCentre, _pending[0] ) );
						LiveRadius( rimCentre, _pending[0] );
					}
					break;

				case SketchToolKind.ArcThreePoint when _pending.Count == 1:
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					LiveLength( _pending[0], _cursorOnPlane );
					break;

				case SketchToolKind.ArcThreePoint:
					DrawThreePointArcPreview( _pending[0], _pending[1], _cursorOnPlane );
					break;

				case SketchToolKind.Slot when _pending.Count == 1:
					Gizmo.Draw.Line( PlaneToWorld( _pending[0] ), c );
					LiveLength( _pending[0], _cursorOnPlane );
					break;

				case SketchToolKind.Slot:
					DrawSlotPreview( _pending[0], _pending[1], _cursorOnPlane );
					break;

				case SketchToolKind.PolygonCircumscribed:
					DrawLoopPreview( RegularPolygon( _pending[0], _cursorOnPlane, PolygonSides, circumscribed: true ) );
					LiveRadius( _pending[0], _cursorOnPlane );
					break;

				case SketchToolKind.Polygon:
					DrawLoopPreview( RegularPolygon( _pending[0], _cursorOnPlane, PolygonSides ) );
					LiveRadius( _pending[0], _cursorOnPlane );
					break;

				case SketchToolKind.Arc when _pending.Count == 1:
					DrawCirclePreview( _pending[0], Dist( _pending[0], _cursorOnPlane ) );
					LiveRadius( _pending[0], _cursorOnPlane );
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

	/// <summary>Length of the segment being dragged, at its midpoint. THE reason this whole block
	/// exists: a line with no number on it is a line you are guessing at.</summary>
	private void LiveLength( Vec2 from, Vec2 to ) =>
		DrawDimensionText( PlaneToWorld( (from + to) * 0.5f ), FormatLength( Dist( from, to ) ), DimensionLiveColor );

	private void LiveRadius( Vec2 centre, Vec2 rim ) =>
		DrawDimensionText( PlaneToWorld( (centre + rim) * 0.5f ), $"R {FormatLength( Dist( centre, rim ) )}", DimensionLiveColor );

	/// <summary>Both sides of a rectangle at once, at the corner you are dragging.</summary>
	private void LiveSize( Vec2 at, float width, float height ) =>
		DrawDimensionText( PlaneToWorld( at ), $"{FormatLength( width )} × {FormatLength( height )}", DimensionLiveColor, -20f );

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
		SketchToolKind.Select => "Select - drag any point to move it; everything joined to it follows",
		_ => "Sketching - pick a tool from the sketch toolbar",
	};
}
