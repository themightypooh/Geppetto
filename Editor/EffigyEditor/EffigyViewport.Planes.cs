using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Datum planes in the viewport: the ones a PlaneFeature put there, as opposed to the three global
/// ones the origin owns.
///
/// A SEPARATE PARTIAL BECAUSE THEY ARE A SEPARATE KIND OF THING. The three reference planes are
/// fixtures — always three, always square to the world, addressed by an index 0..2, resizable by
/// their corners — and every one of those facts is baked into the code that draws and picks them.
/// A datum plane is none of them: there can be none or twenty, it can lean any way it likes, and it
/// is identified by a feature id. Bolting that onto the index-based code would have meant every
/// method there growing a "unless it is one of the other ones" branch, and the corner-resize
/// machinery would have been the first thing to break.
///
/// WHAT THEY SHARE IS THE CLICK. Both are answers to "which plane?", and the selection box asks that
/// question once — so both register hitboxes in the same frame, both compare against the nearest
/// solid the same way, and a click resolves to whichever the cursor was actually over. There is no
/// mode to switch between them, for the same reason picking a plane and picking a face is not two
/// modes either.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>
	/// A plane published by a PlaneFeature, ready to be drawn and clicked.
	///
	/// Handed in whole every rebuild rather than the viewport reaching into the studio: the plane a
	/// feature published is a rebuild's output, and a viewport holding the feature would be drawing
	/// whatever the parameters say right now, which during a drag is not what the model is.
	/// </summary>
	internal sealed class PickablePlane
	{
		public string FeatureId;
		public string Name;
		public SketchPlane Plane;

		/// <summary>Drawn at all. Hidden planes are invisible and unclickable — a document with a
		/// dozen of them is otherwise a fog.</summary>
		public bool Visible = true;

		/// <summary>
		/// Offerable as an answer to "which plane?", which is NOT the same as being drawn.
		///
		/// The plane whose dialog is open is the case that forced the split. It cannot be built on
		/// itself and neither can anything at or below it in the tree, so it must not be pickable —
		/// and it is the one plane you most need to LOOK at while you drag its offset. Filtering the
		/// list by tree position did both at once and made the plane you were editing disappear the
		/// moment you opened it.
		/// </summary>
		public bool Pickable = true;

		/// <summary>
		/// Which way this plane's Offset moves it — <see cref="PlaneFeature.OffsetAxis"/>, worked out
		/// by the window and handed over with everything else.
		///
		/// HANDED IN RATHER THAN DERIVED HERE, for the reason the plane itself is: the viewport does
		/// not hold features. Recovering this needs the Angle and the Hinge, which live on the
		/// feature, and a viewport that reached for them would be reading parameters that a drag in
		/// progress has already changed — the exact staleness this class exists to avoid.
		/// </summary>
		public Vec3 OffsetAxis;

		public PickablePlane( string featureId, string name, SketchPlane plane, bool visible, bool pickable,
			Vec3 offsetAxis )
		{
			FeatureId = featureId;
			Name = name;
			OffsetAxis = offsetAxis;
			Plane = plane;
			Visible = visible;
			Pickable = pickable;
		}
	}

	/// <summary>
	/// How far a datum plane reaches from its own origin.
	///
	/// SMALLER THAN THE REFERENCE PLANES on purpose. Those are the world's frame and are meant to
	/// span it; a datum plane is a local decision — 2 units off this face, 30° round that axis — and
	/// several of them at 128 units across would bury the part they were made for. This is large
	/// enough to read as a plane and small enough that three of them are still three things.
	/// </summary>
	private const float DatumPlaneHalfSize = PlaneSize * 0.55f;

	/// <summary>Half-thickness of the pick slab, matching the reference planes' so a datum plane is
	/// no harder to hit than a global one.</summary>
	private const float DatumPlanePickThickness = PlanePickThickness;

	/// <summary>Lilac, and deliberately none of the three axis colours. Orange, blue and green mean
	/// Top, Front and Right everywhere in this editor, and a fourth plane wearing one of them would
	/// be read as one of those three seen from an angle.</summary>
	private static readonly Color DatumPlaneColour = new( 0.68f, 0.55f, 0.9f, 0.6f );

	private readonly List<PickablePlane> _datumPlanes = new();

	/// <summary>Feature id of the datum plane under the cursor this frame, or null.</summary>
	private string _hoveredDatumPlane;

	/// <summary>Fires with the picked plane's feature id. Set by the selection box alongside
	/// PlanePicked, so one armed box takes either kind of answer.</summary>
	public Action<string> DatumPlanePicked { get; set; }

	/// <summary>
	/// A plane to draw its own two in-plane axes on, or null.
	///
	/// SET WHILE A PLANE'S DIALOG IS OPEN, because that dialog asks which of those axes to hinge on
	/// and the answer is not guessable. On the three global planes the axes are world axes and
	/// "its X axis" reads as it sounds; on a plane derived from a face they come from the normal
	/// alone (FacePlane.FromPointAndNormal), so the only honest way to say which is which is to
	/// draw them.
	/// </summary>
	public string PlaneAxesShownOn { get; set; }

	public IReadOnlyList<PickablePlane> DatumPlanes => _datumPlanes;

	/// <summary>Replace the datum planes wholesale. Same shape as SetPickableSketches, and called
	/// from the same place for the same reason.</summary>
	public void SetDatumPlanes( IEnumerable<PickablePlane> planes )
	{
		_datumPlanes.Clear();

		if ( planes is null )
			return;

		foreach ( var plane in planes )
		{
			if ( plane?.Plane is not null )
				_datumPlanes.Add( plane );
		}
	}

	/// <summary>Where a datum plane sits in the viewport. Datum planes are built in model space and
	/// the model is drawn relative to the origin handle, exactly like the active sketch plane a few
	/// lines away in DrawReferencePlanes.</summary>
	private Vector3 DatumOrigin( PickablePlane plane ) =>
		OriginPosition + new Vector3( plane.Plane.Origin.x, plane.Plane.Origin.y, plane.Plane.Origin.z );

	private static Vector3 DatumRight( PickablePlane plane ) =>
		new( plane.Plane.XAxis.x, plane.Plane.XAxis.y, plane.Plane.XAxis.z );

	private static Vector3 DatumUp( PickablePlane plane ) =>
		new( plane.Plane.YAxis.x, plane.Plane.YAxis.y, plane.Plane.YAxis.z );

	/// <summary>Which way the plane faces. Cross( X, Y ) is SketchPlane.Normal's own definition, so
	/// this says what the kernel says rather than something close to it — which matters where the
	/// sign is load-bearing, as it is for the offset handle's arrow.</summary>
	private static Vector3 DatumNormal( PickablePlane plane ) =>
		Vector3.Cross( DatumRight( plane ), DatumUp( plane ) ).Normal;

	/// <summary>
	/// Outline, name, and a hover wash. Called from DrawReferencePlanes so datum planes are drawn in
	/// the same pass and under the same depth rules as the planes they sit among.
	/// </summary>
	private void DrawDatumPlanes()
	{
		foreach ( var plane in _datumPlanes )
		{
			if ( !plane.Visible )
				continue;

			var centre = DatumOrigin( plane );
			var right = DatumRight( plane );
			var up = DatumUp( plane );
			var hovered = _hoveredDatumPlane == plane.FeatureId;

			DrawPlaneOutline( centre, right, up, DatumPlaneHalfSize,
				hovered ? DatumPlaneColour.WithAlpha( 0.95f ) : DatumPlaneColour );

			if ( hovered )
			{
				// The same wash the reference planes get, so the thing about to be picked fills in
				// whichever kind of plane it is.
				Gizmo.Draw.IgnoreDepth = true;
				Gizmo.Draw.Color = DatumPlaneColour.WithAlpha( 0.18f );

				var a = centre + right * DatumPlaneHalfSize + up * DatumPlaneHalfSize;
				var b = centre - right * DatumPlaneHalfSize + up * DatumPlaneHalfSize;
				var c = centre - right * DatumPlaneHalfSize - up * DatumPlaneHalfSize;
				var d = centre + right * DatumPlaneHalfSize - up * DatumPlaneHalfSize;

				Gizmo.Draw.SolidTriangle( new Triangle( a, b, c ) );
				Gizmo.Draw.SolidTriangle( new Triangle( a, c, d ) );
				Gizmo.Draw.IgnoreDepth = false;
			}

			if ( plane.FeatureId == PlaneAxesShownOn )
				DrawPlaneAxes( centre, right, up );

			if ( string.IsNullOrEmpty( plane.Name ) )
				continue;

			// NAMED, unlike the three global planes, because those are three and these are however
			// many somebody made. "Which of these is Roof line?" has no answer from the geometry —
			// two parallel planes a unit apart look identical — and the feature tree cannot say
			// either, since a row in a list does not point at anything in space.
			Gizmo.Draw.Color = DatumPlaneColour.WithAlpha( hovered ? 0.95f : 0.6f );
			Gizmo.Draw.WorldText( plane.Name,
				new Transform( centre - right * DatumPlaneHalfSize + up * (DatumPlaneHalfSize + 4f) ),
				"Roboto", 9f, TextFlag.LeftBottom );
		}
	}

	/// <summary>The two in-plane axes, named, so the Tilt about dropdown is a thing you can read off
	/// the model rather than find by trying both.</summary>
	private static void DrawPlaneAxes( Vector3 centre, Vector3 right, Vector3 up )
	{
		var length = DatumPlaneHalfSize * 0.8f;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2f;

		Gizmo.Draw.Color = DatumPlaneColour.WithAlpha( 0.9f );
		Gizmo.Draw.Line( centre, centre + right * length );
		Gizmo.Draw.WorldText( "X", new Transform( centre + right * (length + 5f) ), "Roboto", 10f, TextFlag.Center );

		Gizmo.Draw.Line( centre, centre + up * length );
		Gizmo.Draw.WorldText( "Y", new Transform( centre + up * (length + 5f) ), "Roboto", 10f, TextFlag.Center );

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
	}

	/// <summary>
	/// A clickable slab per datum plane, only while picking is armed. Called from DrawPlaneHitboxes
	/// so the two kinds of plane are registered in one place and resolve against the same solid.
	/// </summary>
	private void DrawDatumPlaneHitboxes()
	{
		_hoveredDatumPlane = null;

		if ( !PlanePickMode )
			return;

		foreach ( var plane in _datumPlanes )
		{
			if ( !plane.Visible || !plane.Pickable )
				continue;

			// THE NEARER TARGET WINS, exactly as it does for a reference plane: a solid in front of
			// this plane takes the click and the plane does not so much as light up.
			if ( DatumRayDistance( plane, out var distance ) && FacePickDistance < distance )
				continue;

			var normal = DatumNormal( plane );

			using var scope = Gizmo.Scope( $"datum-plane-{plane.FeatureId}",
				new Transform( DatumOrigin( plane ), Rotation.LookAt( normal, DatumUp( plane ) ) ) );

			// Local x is the rotation's forward, which is the plane normal — so the slab is thin
			// along the normal and full size across the plane.
			Gizmo.Hitbox.BBox( BBox.FromPositionAndSize( Vector3.Zero,
				new Vector3( DatumPlanePickThickness, DatumPlaneHalfSize * 2f, DatumPlaneHalfSize * 2f ) ) );

			if ( !Gizmo.IsHovered )
				continue;

			_hoveredDatumPlane = plane.FeatureId;

			if ( Gizmo.WasLeftMousePressed )
				DatumPlanePicked?.Invoke( plane.FeatureId );
		}
	}

	/// <summary>How far along the cursor ray this datum plane sits, or false when the ray runs
	/// parallel to it or meets it behind the camera. The arbitrary-orientation twin of
	/// PlaneRayDistance.</summary>
	private bool DatumRayDistance( PickablePlane plane, out float distance )
	{
		distance = float.PositiveInfinity;

		var normal = DatumNormal( plane );
		var ray = Gizmo.CurrentRay;
		var denominator = Vector3.Dot( ray.Forward, normal );

		if ( MathF.Abs( denominator ) < 1e-5f )
			return false;

		var t = Vector3.Dot( DatumOrigin( plane ) - ray.Position, normal ) / denominator;

		if ( t <= 0f )
			return false;

		distance = t;
		return true;
	}

	// --- the offset handle -------------------------------------------------------------------

	/// <summary>Raised once when the offset handle is grabbed, before it has moved.</summary>
	public Action PlaneOffsetDragBegan { get; set; }

	/// <summary>
	/// Raised every frame the offset handle moves, with how far it has been dragged SINCE it was
	/// grabbed — the total, not the frame's delta.
	///
	/// The same shape FaceDragMoved has and for the same reason: what reads this sets a parameter
	/// from it, and a parameter is a value rather than something to integrate.
	/// </summary>
	public Action<float> PlaneOffsetDragged { get; set; }

	private bool _draggingPlaneOffset;
	private Vector3 _planeOffsetAnchor;
	private Vector3 _planeOffsetAxis = Vector3.Up;
	private float _planeOffsetDistance;

	/// <summary>
	/// One arrow, along the direction the open plane's Offset actually travels.
	///
	/// WHY A PLANE NEEDS ITS OWN HANDLE AT ALL when Offset is a field you can type in: the number is
	/// only meaningful against something you can see. "Two units off that face" is a decision about
	/// where the rib goes, and typing 2 to find out whether 2 was right is the interaction Move Face
	/// and the extrude already replaced with an arrow — see EffigyViewport.FaceDrag, whose reasoning
	/// this follows down to the single axis.
	///
	/// ONLY WHILE THE PLANE'S OWN DIALOG IS OPEN, which is the same gate the in-plane axes use and is
	/// why it reads PlaneAxesShownOn rather than carrying a second flag that could drift out of step
	/// with it. Both mean "the plane being edited", and a datum plane that grew a permanent arrow
	/// would put a grabbable control on every plane in a document that has twenty.
	///
	/// ALONG THE OFFSET AXIS, NOT THE PLANE'S NORMAL. On a tilted plane those are different
	/// directions — Execute offsets and then tilts about the origin it landed on — so the arrow
	/// points where the plane MOVES rather than where it faces. PlaneFeature.OffsetAxis explains the
	/// difference; the window works it out and hands it over on the PickablePlane.
	/// </summary>
	private void PlaneOffsetHandleFrame()
	{
		if ( PlaneOffsetDragged is null || string.IsNullOrEmpty( PlaneAxesShownOn ) )
		{
			EndPlaneOffsetDrag();
			return;
		}

		// The tools that own the mouse. A sketch being drawn, the sculpt and paint brushes and the
		// bone tool all have a click of their own, and an arrow floating over the model while one of
		// them is armed invites a click that will not do what it looks like. Not while the drag is
		// already running: it has the button, so nothing else can have started.
		if ( (IsSketching || IsSculpting || IsPainting || IsMaterialBrushing || IsNoting || BoneToolActive)
			&& !_draggingPlaneOffset )
		{
			EndPlaneOffsetDrag();
			return;
		}

		// MID-DRAG THE PLANE IS MOVING, which is the whole point of the drag and is exactly why the
		// handle cannot be re-read from it. Each frame writes Offset, the studio rebuilds, and the
		// plane this was drawn from comes back somewhere new — so anchoring to its live origin would
		// add the movement a second time and the arrow would run away from the cursor at double
		// speed. Anchor where the grab happened and add what has been dragged since.
		if ( _draggingPlaneOffset )
		{
			DrawPlaneOffsetHandle( _planeOffsetAnchor + _planeOffsetAxis * _planeOffsetDistance, _planeOffsetAxis );
			return;
		}

		foreach ( var plane in _datumPlanes )
		{
			if ( plane.FeatureId != PlaneAxesShownOn )
				continue;

			// A plane hidden by its eye in the feature tree is not there to be grabbed. The dialog
			// can still be open on it — hiding one does not close it — and an arrow hanging in space
			// with no plane under it is a control for something invisible.
			if ( !plane.Visible )
				return;

			DrawPlaneOffsetHandle( DatumOrigin( plane ),
				new Vector3( plane.OffsetAxis.x, plane.OffsetAxis.y, plane.OffsetAxis.z ) );
			return;
		}
	}

	/// <summary>
	/// The arrow, and the drag it reports.
	///
	/// ONE ARROW PUSHES BOTH WAYS: dragging back past the tail gives a negative distance and the
	/// plane goes the other way, which is what a signed offset means and what the face handle
	/// already does with the same single arrow.
	///
	/// THE ENGINE HIDES IT NEAR HEAD-ON, within ten degrees of the view direction, because screen
	/// movement stops mapping to axis movement there. Looking straight down a plane's normal
	/// therefore leaves no handle — correct, since the drag would be meaningless at that angle, but
	/// worth knowing before concluding it broke. Orbit a few degrees.
	/// </summary>
	private void DrawPlaneOffsetHandle( Vector3 origin, Vector3 axis )
	{
		using var scope = Gizmo.Scope( "plane-offset-drag", new Transform( origin ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		if ( Gizmo.Control.Arrow( "plane-offset", axis, out var distance ) )
		{
			if ( !_draggingPlaneOffset )
			{
				_draggingPlaneOffset = true;
				_planeOffsetAnchor = origin;
				_planeOffsetAxis = axis;
				_planeOffsetDistance = 0f;
				PlaneOffsetDragBegan?.Invoke();
			}

			// Accumulated rather than assigned: these controls report the change since the last frame
			// and return false on any frame the value did not move. The axis is the one the drag
			// STARTED with, held for the same reason the anchor is.
			_planeOffsetDistance += distance;

			PlaneOffsetDragged.Invoke( _planeOffsetDistance );
			return;
		}

		EndPlaneOffsetDrag();
	}

	private void EndPlaneOffsetDrag()
	{
		if ( !_draggingPlaneOffset )
			return;

		_draggingPlaneOffset = false;
		_planeOffsetDistance = 0f;
	}
}
