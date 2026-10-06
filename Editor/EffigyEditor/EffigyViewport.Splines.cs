using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Drawing a spline: click to drop points, drag a point to move it, and the curve follows.
///
/// A CLICK LANDS ON THE MODEL WHEN THERE IS ONE UNDER IT, and on a plane through the last point
/// facing the camera otherwise — so a cable can be clicked straight onto a body's surface, and
/// the run between two bodies is clicked out in the air at the height you were already at. The
/// first point with nothing under it lands on the ground plane, which is where a path usually
/// starts. A drag moves the point in the plane facing the camera through it: the point goes
/// where the mouse goes, and depth is left alone.
///
/// The feature owns the points; this only edits them and says so. The curve drawn here is
/// sampled from the feature directly, so it follows the mouse without waiting on a rebuild,
/// and the rebuild that follows an edit is what turns it into the tube.
/// </summary>
internal sealed partial class EffigyViewport
{
	public SplineFeature SplineFeature { get; private set; }
	public bool IsSplineEditing => SplineFeature is not null;

	/// <summary>A point was added, moved or removed. The window marks the feature dirty and rebuilds.</summary>
	public Action SplineEdited { get; set; }

	/// <summary>Enter, or a double-click: the drawing is done.</summary>
	public Action SplineFinishRequested { get; set; }

	private int _splineHover = -1;
	private int _splineDrag = -1;
	private bool _splineDragMoved;
	private Vector3 _splineDragNormal;
	private Vec3 _splineDragOrigin;

	private static readonly Color SplineColor = new( 0.94f, 0.63f, 0.23f );
	private static readonly Color SplinePointColor = new( 0.96f, 0.96f, 0.98f );
	private static readonly Color SplineHoverColor = new( 0.36f, 0.58f, 1f );

	public void BeginSplineEdit( SplineFeature feature )
	{
		SplineFeature = feature;
		_splineHover = -1;
		_splineDrag = -1;
		Update();
	}

	public void EndSplineEdit()
	{
		SplineFeature = null;
		_splineHover = -1;
		_splineDrag = -1;
		Update();
	}

	/// <summary>Remove the last point — Backspace while drawing.</summary>
	public bool RemoveLastSplinePoint()
	{
		if ( SplineFeature is not { } feature || feature.Points.Count == 0 )
			return false;

		feature.Points.RemoveAt( feature.Points.Count - 1 );
		_splineHover = -1;
		SplineEdited?.Invoke();
		return true;
	}

	private void SplineFrame()
	{
		if ( SplineFeature is not { } feature )
			return;

		var points = feature.Points;
		var ray = Gizmo.CurrentRay;
		var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
		var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );
		var cameraForward = Gizmo.CameraTransform.Rotation.Forward;

		// --- the drag, first, so a held button keeps its point even off the handle ---
		if ( _splineDrag >= 0 )
		{
			if ( Gizmo.IsLeftMouseDown && _splineDrag < points.Count )
			{
				if ( RayPlane( origin, direction, _splineDragOrigin, new Vec3( _splineDragNormal.x, _splineDragNormal.y, _splineDragNormal.z ), out var moved ) )
				{
					if ( (moved - points[_splineDrag]).LengthSquared > 1e-10f )
					{
						points[_splineDrag] = moved;
						_splineDragMoved = true;
					}
				}
			}
			else
			{
				var moved = _splineDragMoved;
				_splineDrag = -1;
				_splineDragMoved = false;

				if ( moved )
					SplineEdited?.Invoke();
			}
		}

		// --- hover: the nearest point within a handle's width of the cursor ray ---
		_splineHover = -1;

		if ( _canvasHasCursor && _splineDrag < 0 )
		{
			var best = float.MaxValue;

			for ( var i = 0; i < points.Count; i++ )
			{
				var toPoint = points[i] - origin;
				var along = Vec3.Dot( toPoint, direction );

				if ( along <= 0f )
					continue;

				var perpendicular = (toPoint - direction * along).Length;
				var handle = HandleRadius( along ) * 1.6f;

				if ( perpendicular < handle && along < best )
				{
					best = along;
					_splineHover = i;
				}
			}
		}

		// --- where a click would land ---
		Vec3? landing = null;

		if ( _canvasHasCursor && _splineHover < 0 && _splineDrag < 0 )
			landing = SplineLanding( origin, direction, points, cameraForward );

		// --- input ---
		if ( _canvasHasCursor && Gizmo.WasLeftMousePressed && !Gizmo.HasHovered && _splineDrag < 0 )
		{
			if ( _splineHover >= 0 )
			{
				_splineDrag = _splineHover;
				_splineDragMoved = false;
				_splineDragNormal = cameraForward;
				_splineDragOrigin = points[_splineHover];
			}
			else if ( landing is { } at )
			{
				points.Add( at );
				SplineEdited?.Invoke();
			}
		}

		// --- drawing ---
		var spline = feature.ToSpline();
		var path = spline.Sample( 10 );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = SplineColor;
		Gizmo.Draw.LineThickness = 2.5f;

		for ( var i = 0; i + 1 < path.Count; i++ )
			Gizmo.Draw.Line( ToWorldDir( path[i] ), ToWorldDir( path[i + 1] ) );

		if ( spline.Closed && path.Count > 2 )
			Gizmo.Draw.Line( ToWorldDir( path[^1] ), ToWorldDir( path[0] ) );

		// The straight polyline between control points, faint, so the curve's relationship to
		// the clicks stays readable on a tight bend.
		if ( spline.Smooth && points.Count > 2 )
		{
			Gizmo.Draw.Color = SplineColor.WithAlpha( 0.25f );
			Gizmo.Draw.LineThickness = 1f;

			for ( var i = 0; i + 1 < points.Count; i++ )
				Gizmo.Draw.Line( ToWorldDir( points[i] ), ToWorldDir( points[i + 1] ) );
		}

		for ( var i = 0; i < points.Count; i++ )
		{
			var world = ToWorldDir( points[i] );
			var distance = (points[i] - origin).Length;
			var radius = HandleRadius( distance );
			var lit = i == _splineHover || i == _splineDrag;

			Gizmo.Draw.Color = lit ? SplineHoverColor : i == points.Count - 1 ? SplineColor : SplinePointColor;
			Gizmo.Draw.SolidSphere( world, lit ? radius * 1.3f : radius, 8, 8 );
		}

		if ( landing is { } ghost )
		{
			var world = ToWorldDir( ghost );
			var radius = HandleRadius( (ghost - origin).Length );

			Gizmo.Draw.Color = SplineColor.WithAlpha( 0.55f );
			Gizmo.Draw.LineSphere( new Sphere( world, radius ), 8 );

			if ( points.Count > 0 )
			{
				Gizmo.Draw.Color = SplineColor.WithAlpha( 0.35f );
				Gizmo.Draw.LineThickness = 1f;
				Gizmo.Draw.Line( ToWorldDir( points[^1] ), world );
			}
		}

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;
	}

	/// <summary>A handle that stays the same size on screen: a fraction of its distance from the eye.</summary>
	private static float HandleRadius( float distance ) => MathF.Max( distance * 0.008f, 0.02f );

	/// <summary>
	/// Where a click lands: the model's surface under the cursor, else a plane facing the camera
	/// through the last point, else the ground.
	/// </summary>
	private Vec3? SplineLanding( Vec3 origin, Vec3 direction, List<Vec3> points, Vector3 cameraForward )
	{
		var visible = _displayBodies.Where( b => b?.Mesh is not null && b.Visible ).ToList();

		if ( visible.Count > 0 && MeshRaycast.Raycast( visible, origin, direction, PickTreeFor ) is { } hit )
			return hit.Hit.Point;

		if ( points.Count > 0 )
		{
			var normal = new Vec3( cameraForward.x, cameraForward.y, cameraForward.z );

			if ( RayPlane( origin, direction, points[^1], normal, out var onView ) )
				return onView;
		}

		if ( RayPlane( origin, direction, Vec3.Zero, new Vec3( 0, 0, 1 ), out var onGround ) )
			return onGround;

		return null;
	}

	private static bool RayPlane( Vec3 origin, Vec3 direction, Vec3 planePoint, Vec3 planeNormal, out Vec3 hit )
	{
		hit = default;

		var denominator = Vec3.Dot( direction, planeNormal );

		if ( MathF.Abs( denominator ) < 1e-6f )
			return false;

		var t = Vec3.Dot( planePoint - origin, planeNormal ) / denominator;

		if ( t <= 0f )
			return false;

		hit = origin + direction * t;
		return true;
	}
}
