using System;

namespace Effigy;

/// <summary>
/// A rotation as a unit quaternion.
///
/// The kernel otherwise carries rotation inside <see cref="Xform"/>'s basis, which is right for
/// composing and wrong for blending: interpolating two bases component-wise shears them. Anything
/// that reads keyframes - a .riganim, a pose dump - stores quaternions and has to slerp them, so
/// this exists for that and converts to an Xform at the boundary.
///
/// SLERP IS System.Numerics.Quaternion.Slerp, not a hand-rolled one. s&box's Rotation.Slerp is a
/// wrapper over the same function, and Marionette's Transform.Lerp calls it, so a clip evaluated
/// here lands on exactly the floats the editor's playback does. A "cleaner" slerp would differ in
/// the last bits and the parity tests that pin this to Marionette would start failing.
/// </summary>
public readonly struct Quat
{
	public readonly float x, y, z, w;

	public Quat( float x, float y, float z, float w )
	{
		this.x = x;
		this.y = y;
		this.z = z;
		this.w = w;
	}

	public static readonly Quat Identity = new( 0, 0, 0, 1 );

	public static Quat operator *( Quat a, Quat b ) => From( ToNumerics( a ) * ToNumerics( b ) );

	public Quat Inverse => From( System.Numerics.Quaternion.Inverse( ToNumerics( this ) ) );

	public Quat Normalized => From( System.Numerics.Quaternion.Normalize( ToNumerics( this ) ) );

	public Vec3 Rotate( Vec3 v )
	{
		var r = System.Numerics.Vector3.Transform( new System.Numerics.Vector3( v.x, v.y, v.z ), ToNumerics( this ) );
		return new Vec3( r.X, r.Y, r.Z );
	}

	public static Quat FromAxisAngle( Vec3 axis, float radians ) =>
		From( System.Numerics.Quaternion.CreateFromAxisAngle( new System.Numerics.Vector3( axis.x, axis.y, axis.z ), radians ) );

	/// <summary>
	/// s&amp;box's Rotation.From( Angles ), in degrees: roll about x, then pitch about y, then yaw
	/// about z - the closed form the engine itself uses, so a placement read from a .riganim turns
	/// exactly as the editor drew it.
	/// </summary>
	public static Quat FromAngles( float pitch, float yaw, float roll )
	{
		const float half = MathF.PI / 360f;
		var (sp, cp) = MathF.SinCos( pitch * half );
		var (sy, cy) = MathF.SinCos( yaw * half );
		var (sr, cr) = MathF.SinCos( roll * half );

		return new Quat(
			sr * cp * cy - cr * sp * sy,
			cr * sp * cy + sr * cp * sy,
			cr * cp * sy - sr * sp * cy,
			cr * cp * cy + sr * sp * sy );
	}

	public static Quat Slerp( Quat a, Quat b, float t ) =>
		From( System.Numerics.Quaternion.Slerp( ToNumerics( a ), ToNumerics( b ), t ) );

	/// <summary>The basis this rotation turns the world axes into, scaled per axis, at an origin.</summary>
	public Xform ToXform( Vec3 origin, Vec3 scale ) =>
		new( Rotate( new Vec3( 1, 0, 0 ) ) * scale.x, Rotate( new Vec3( 0, 1, 0 ) ) * scale.y, Rotate( new Vec3( 0, 0, 1 ) ) * scale.z, origin );

	public Xform ToXform( Vec3 origin ) => ToXform( origin, Vec3.One );

	/// <summary>
	/// The rotation part of a transform. Scale is divided out of each axis first, so a scaled
	/// bone still gives a unit quaternion; shear has no quaternion and is lost.
	/// </summary>
	public static Quat FromXform( Xform t )
	{
		var x = t.X.Normal;
		var y = t.Y.Normal;
		var z = t.Z.Normal;

		// Column-major basis into System.Numerics' row-vector matrix: each axis is a row.
		var m = new System.Numerics.Matrix4x4(
			x.x, x.y, x.z, 0,
			y.x, y.y, y.z, 0,
			z.x, z.y, z.z, 0,
			0, 0, 0, 1 );

		return From( System.Numerics.Quaternion.Normalize( System.Numerics.Quaternion.CreateFromRotationMatrix( m ) ) );
	}

	/// <summary>Per-axis scale of a transform: the length of each basis vector.</summary>
	public static Vec3 ScaleOf( Xform t ) => new( t.X.Length, t.Y.Length, t.Z.Length );

	static System.Numerics.Quaternion ToNumerics( Quat q ) => new( q.x, q.y, q.z, q.w );

	static Quat From( System.Numerics.Quaternion q ) => new( q.X, q.Y, q.Z, q.W );

	public override string ToString() => $"({x:0.####}, {y:0.####}, {z:0.####}, {w:0.####})";
}
