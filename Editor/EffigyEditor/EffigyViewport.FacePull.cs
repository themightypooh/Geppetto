using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>What a face pull is acting on, handed to the window so it can drive the feature.</summary>
internal sealed class EffigyFacePull
{
	public string SketchFeatureId;
	public Vec2 Seed;
	public Profile Profile;

	/// <summary>Signed distance along the sketch plane's normal, live as the drag happens.</summary>
	public float Distance;
}

/// <summary>
/// The drag handle that pulls a chosen face out into a solid.
///
/// It belongs to the FEATURE BEING EDITED, not to whatever the cursor happens to be over. The flow
/// is Onshape's: press Extrude, the dialog opens waiting for a face, every closed region lights up,
/// you click one — and only then does the handle appear, on that face. Drag it and the solid grows
/// live; or ignore it and type an exact distance into the dialog, which is the same number from the
/// other end. Push back through the plane and the distance goes negative, which is the same feature
/// with Flip set.
///
/// An earlier pass put a handle on every hovered face and created the Extrude on first drag. That
/// is a second, competing way to start the same feature, and it fought the toolbar button for the
/// same click. One gesture, driven by the open dialog.
///
/// NO Gizmo.Control HERE, deliberately. Gizmo.Control.Position is a three-axis widget that returns
/// a per-frame delta, and two of those axes mean nothing for a face pull — the whole gesture is
/// one degree of freedom along the plane normal. So the handle is drawn by hand, hit-tested with
/// Gizmo.Hitbox.Sphere, and the distance comes from the closest point between the cursor ray and
/// the normal axis. Every Gizmo member used here is one RigViewport already uses.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>Fires continuously while a face is being dragged. The window creates the Extrude on
	/// the first call whose distance clears the threshold, and updates it on every call after.</summary>
	public Action<EffigyFacePull> FacePullChanged { get; set; }

	/// <summary>The drag finished — the feature it created stands on its own from here.</summary>
	public Action FacePullEnded { get; set; }

	/// <summary>The face the open feature is built from, or null when nothing is being edited.
	/// Set by the window whenever the dialog's feature gains or changes a region.</summary>
	private EffigyFacePull _pullTarget;

	private bool _pulling;

	/// <summary>World-space anchor and axis of the drag, fixed at press so the handle does not
	/// chase the cursor across the face while it is being dragged.</summary>
	private Vector3 _pullAnchor;
	private Vector3 _pullAxis;

	/// <summary>Axis parameter under the cursor when the drag began, subtracted from every later
	/// reading so the face does not jump by the handle's own length on the first frame.</summary>
	private float _pullOriginT;

	private static readonly Color PullHandleColor = new( 0.35f, 0.80f, 1f, 0.95f );
	private static readonly Color PullHandleHoverColor = new( 0.55f, 0.92f, 1f, 1f );
	private static readonly Color PullAxisColor = new( 0.35f, 0.80f, 1f, 0.35f );

	/// <summary>
	/// Point the handle at a face. Distance is the feature's current value, so the handle sits
	/// where the solid already reaches rather than back on the sketch plane.
	/// </summary>
	public void SetPullTarget( string sketchFeatureId, Vec2 seed, float distance )
	{
		_pullTarget = new EffigyFacePull
		{
			SketchFeatureId = sketchFeatureId,
			Seed = seed,
			Distance = distance,
		};
	}

	public void ClearPullTarget()
	{
		_pullTarget = null;
		_pulling = false;
	}

	/// <summary>
	/// Draw the handle for the targeted face and run the drag.
	///
	/// Called from the finished-sketch pass, after hover has been resolved. Suppressed while a
	/// sketch is open or a region box is armed: in both of those the click already means something
	/// else, and stealing it was the first thing that broke when faces became clickable.
	/// </summary>
	private void FacePullFrame()
	{
		if ( IsSketching || PlanePickMode || RegionPickMode || _pullTarget is null )
		{
			CancelPull();
			return;
		}

		if ( !ResolveTargetFace( out var sketch, out var profile ) )
			return;

		if ( _pulling )
		{
			UpdatePull();
			return;
		}

		var anchor = AnchorFor( sketch, profile );
		var axis = ToWorldDir( sketch.Plane.Normal ).Normal;

		DrawPullHandle( anchor, axis, _pullTarget.Distance, out var grabbed );

		if ( !grabbed || !Gizmo.WasLeftMousePressed )
			return;

		if ( !ClosestAxisPoint( anchor, axis, out var t ) )
			return;

		_pulling = true;
		_pullAnchor = anchor;
		_pullAxis = axis;

		// Subtract where the handle already is, so grabbing a face that is out at 3 units does not
		// snap it back to zero on the first frame.
		_pullOriginT = t - _pullTarget.Distance;
		_pullTarget.Profile = profile;
	}

	/// <summary>
	/// Find the live profile the target seed falls in.
	///
	/// Re-resolved every frame rather than held: the cached profile objects are rebuilt whenever
	/// the studio rebuilds, which during a drag is every frame. The seed is the stable handle —
	/// that is the entire reason RegionSeed is a point (see SketchFeatures.cs).
	/// </summary>
	private bool ResolveTargetFace( out Sketch sketch, out Profile profile )
	{
		sketch = null;
		profile = null;

		foreach ( var (featureId, candidate, profiles) in _visibleSketches )
		{
			if ( featureId != _pullTarget.SketchFeatureId )
				continue;

			foreach ( var p in profiles )
			{
				if ( !p.Contains( _pullTarget.Seed ) )
					continue;

				sketch = candidate;
				profile = p;
				return true;
			}
		}

		return false;
	}

	private void UpdatePull()
	{
		// Mouse released anywhere ends it, including off the handle - a drag that has to finish on
		// the thing it started on is a drag people lose halfway through.
		if ( !Gizmo.IsLeftMouseDown )
		{
			_pulling = false;
			FacePullEnded?.Invoke();
			return;
		}

		if ( ClosestAxisPoint( _pullAnchor, _pullAxis, out var t ) )
		{
			_pullTarget.Distance = t - _pullOriginT;
			FacePullChanged?.Invoke( _pullTarget );
		}

		DrawPullHandle( _pullAnchor, _pullAxis, _pullTarget.Distance, out _ );
	}

	private void CancelPull()
	{
		if ( !_pulling )
			return;

		_pulling = false;
		FacePullEnded?.Invoke();
	}

	/// <summary>Where the handle sits on a face: the average of the region's boundary points, which
	/// is inside it for anything convex. Concave regions fall back to their first vertex, which is
	/// on the boundary rather than inside but is still somewhere you can see and grab.</summary>
	private Vector3 AnchorFor( Sketch sketch, Profile profile )
	{
		var seed = SeedOf( profile );
		return PlaneToWorld( sketch.Plane, profile.Contains( seed ) ? seed : profile.Outer[0] );
	}

	private static Vec2 SeedOf( Profile profile )
	{
		float x = 0f, y = 0f;

		foreach ( var p in profile.Outer )
		{
			x += p.x;
			y += p.y;
		}

		var count = MathF.Max( profile.Outer.Count, 1 );
		return new Vec2( x / count, y / count );
	}

	/// <summary>
	/// How far along the axis the cursor currently points.
	///
	/// Closest point between two lines: the axis through the face, and the ray under the cursor.
	/// Both directions are unit length, so the usual a and c terms are 1 and the whole thing
	/// collapses to t = (b*e - d) / (1 - b*b). Near-parallel is rejected rather than divided
	/// through - looking straight down the pull axis, every cursor position is equally valid and
	/// the face would shoot to infinity.
	/// </summary>
	private bool ClosestAxisPoint( Vector3 anchor, Vector3 axis, out float t )
	{
		t = 0f;

		var ray = Gizmo.CurrentRay;
		var r = anchor - ray.Position;

		var b = Vector3.Dot( axis, ray.Forward );
		var denom = 1f - b * b;

		if ( MathF.Abs( denom ) < 1e-4f )
			return false;

		var d = Vector3.Dot( axis, r );
		var e = Vector3.Dot( ray.Forward, r );

		t = (b * e - d) / denom;
		return true;
	}
}
