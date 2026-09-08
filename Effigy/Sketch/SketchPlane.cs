using System;

namespace Effigy;

/// <summary>
/// The plane a sketch lives on: an origin and two in-plane axes, with the normal derived from
/// them.
///
/// Sketch entities are stored in 2D plane coordinates, never in world space. That is the whole
/// reason this type exists — a sketch that stored world positions would silently break the moment
/// its plane moved, and "move the sketch plane and everything on it follows" is a thing people
/// expect from CAD and would notice immediately if it were missing.
/// </summary>
public sealed class SketchPlane
{
	public Vec3 Origin;
	public Vec3 XAxis;
	public Vec3 YAxis;

	public SketchPlane( Vec3 origin, Vec3 xAxis, Vec3 yAxis )
	{
		Origin = origin;
		XAxis = xAxis.Normal;
		YAxis = yAxis.Normal;
	}

	public Vec3 Normal => Vec3.Cross( XAxis, YAxis ).Normal;

	public static SketchPlane XY => new( Vec3.Zero, new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ) );
	public static SketchPlane XZ => new( Vec3.Zero, new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ) );
	public static SketchPlane YZ => new( Vec3.Zero, new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ) );

	/// <summary>A plane parallel to this one, offset along the normal. Onshape's offset plane.</summary>
	public SketchPlane Offset( float distance ) =>
		new( Origin + Normal * distance, XAxis, YAxis );

	/// <summary>
	/// This plane leaned over, turning about one of its own in-plane axes through its own origin.
	/// Onshape's angled plane, minus the part where you pick the edge to hinge on.
	///
	/// THE HINGE IS ONE OF THE PLANE'S OWN AXES, not a line you point at. Onshape asks for an edge
	/// and rotates about that, which is the better answer and needs an axis picker the sketcher does
	/// not have yet. What it has is a frame that is already orthonormal and already deterministic,
	/// so hinging on X or on Y — with a signed angle — reaches all four of the leans anyone asks for
	/// off a given plane. On the three global planes the axes are world axes and this reads exactly
	/// as it sounds; on a plane derived from a face they come from the normal alone (see
	/// FacePlane.FromPointAndNormal) and which is which is worth looking at rather than predicting.
	///
	/// THE ORIGIN DOES NOT MOVE, which is what makes offset-then-tilt mean what it looks like: the
	/// plane pivots where it sits rather than swinging away from the thing it was offset from.
	/// </summary>
	public SketchPlane Tilted( float degrees, bool aboutY )
	{
		if ( degrees == 0f )
			return Clone();

		var rotate = Xform.Rotate( aboutY ? YAxis : XAxis, degrees * MathF.PI / 180f );

		return new SketchPlane( Origin, rotate.TransformDirection( XAxis ), rotate.TransformDirection( YAxis ) );
	}

	public Vec3 ToWorld( Vec2 p ) => Origin + XAxis * p.x + YAxis * p.y;

	/// <summary>Project a world point onto the plane. Anything off the plane loses its offset —
	/// that is intended, this is a projection and not an inverse.</summary>
	public Vec2 ToPlane( Vec3 p )
	{
		var d = p - Origin;
		return new Vec2( Vec3.Dot( d, XAxis ), Vec3.Dot( d, YAxis ) );
	}

	public SketchPlane Clone() => new( Origin, XAxis, YAxis );
}
