using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

// ============================================================================
// Click-to-place points for feature settings that are stored as typed coordinates.
//
// Blob's shapes, Surface detail's Near and Flesh Material's corruption seeds are all x,y,z text,
// because that is what a build script and a saved document can both hold. A player expects to
// click on the model instead. This is that click: the dialog arms it with what to do with the
// point, the viewport finds the point (on the skin under the cursor, or on a plane square to the
// view when the cursor is past the model) and hands it back. The text stays the storage.
// ============================================================================

internal sealed partial class EffigyViewport
{
	/// <summary>What an armed point pick does with a click.</summary>
	public sealed class PointPickRequest
	{
		/// <summary>Press, drag out a radius, release — Blob's spheres. Off: a plain click.</summary>
		public bool Drag { get; init; }

		/// <summary>Size of the ghost drawn before the press, so the click shows roughly what it adds.</summary>
		public float GhostRadius { get; init; } = 1f;

		/// <summary>Where a click past the model lands: on the plane through this point, square to the view.</summary>
		public Vec3 Anchor { get; init; }

		/// <summary>The point, the dragged radius (0 for a plain click) and whether Shift was held.</summary>
		public Action<Vec3, float, bool> Picked { get; init; }

		/// <summary>The pick was put down — Escape, another tool, or the dialog closing.</summary>
		public Action Ended { get; init; }
	}

	private static readonly Color PointPickColor = new( 1f, 0.6f, 0.15f, 0.8f );
	private static readonly Color PointPickCutColor = new( 0.4f, 0.75f, 1f, 0.8f );

	private Vec3? _pointPickPress;

	public PointPickRequest PointPick { get; private set; }

	public bool PointPicking => PointPick is not null;

	public void BeginPointPick( PointPickRequest request )
	{
		var was = PointPick;
		PointPick = request;
		_pointPickPress = null;

		if ( was is not null && !ReferenceEquals( was, request ) )
			was.Ended?.Invoke();
	}

	public void EndPointPick()
	{
		if ( PointPick is not { } was )
			return;

		PointPick = null;
		_pointPickPress = null;
		was.Ended?.Invoke();
	}

	private void PointPickFrame()
	{
		if ( PointPick is not { } request )
			return;

		var ray = Gizmo.CurrentRay;
		var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
		var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z ).Normal;
		var cut = Gizmo.IsShiftPressed;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = cut ? PointPickCutColor : PointPickColor;

		// Mid-drag: the radius is how far the cursor has gone from the press, measured on the plane
		// through the press square to the view, so it is exactly as big as it looks on screen.
		if ( _pointPickPress is { } press )
		{
			var at = OnViewPlane( press, origin, direction ) ?? press;
			var radius = MathF.Max( 0.1f, (at - press).Length );

			Gizmo.Draw.LineSphere( new Sphere( new Vector3( press.x, press.y, press.z ), radius ), 16 );
			Gizmo.Draw.IgnoreDepth = false;

			if ( !Gizmo.IsLeftMouseDown )
			{
				_pointPickPress = null;
				request.Picked?.Invoke( press, radius, cut );
			}

			return;
		}

		if ( !_canvasHasCursor )
		{
			Gizmo.Draw.IgnoreDepth = false;
			return;
		}

		Vec3? point = MeshRaycast.Raycast( _displayBodies, origin, direction, PickTreeFor ) is { } hit
			? hit.Hit.Point
			: OnViewPlane( request.Anchor, origin, direction );

		if ( point is not { } p )
		{
			Gizmo.Draw.IgnoreDepth = false;
			return;
		}

		var world = new Vector3( p.x, p.y, p.z );

		if ( request.Drag )
			Gizmo.Draw.LineSphere( new Sphere( world, request.GhostRadius ), 12 );

		Gizmo.Draw.SolidSphere( world, UnitsPerPixel() * 5f, 8, 8 );
		Gizmo.Draw.IgnoreDepth = false;

		if ( !Gizmo.WasLeftMousePressed )
			return;

		if ( request.Drag )
			_pointPickPress = p;
		else
			request.Picked?.Invoke( p, 0f, cut );
	}

	/// <summary>Where the ray crosses the plane through <paramref name="through"/> facing the camera.</summary>
	private Vec3? OnViewPlane( Vec3 through, Vec3 origin, Vec3 direction )
	{
		var forward = _camera.WorldRotation.Forward;
		var normal = new Vec3( forward.x, forward.y, forward.z );
		var denominator = Vec3.Dot( direction, normal );

		if ( MathF.Abs( denominator ) < 1e-6f )
			return null;

		var t = Vec3.Dot( through - origin, normal ) / denominator;
		return t > 0f ? origin + direction * t : null;
	}
}
