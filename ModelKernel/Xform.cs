using System;

namespace ModelKernel;

/// <summary>
/// A rigid-plus-scale transform: a 3x3 basis and a translation.
///
/// Not a full 4x4 — nothing here needs projection, and keeping it to 12 floats makes the mirror
/// case easier to reason about. Mirroring is the reason this type exists at all rather than a
/// position/rotation/scale triple: a mirror is a basis with negative determinant, which no
/// non-negative scale can express.
/// </summary>
public readonly struct Xform
{
	// Column vectors of the basis: where the unit X, Y and Z axes land.
	public readonly Vec3 X, Y, Z, Origin;

	public Xform( Vec3 x, Vec3 y, Vec3 z, Vec3 origin )
	{
		X = x;
		Y = y;
		Z = z;
		Origin = origin;
	}

	public static readonly Xform Identity = new(
		new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), Vec3.Zero );

	public Vec3 TransformPoint( Vec3 p ) => X * p.x + Y * p.y + Z * p.z + Origin;

	/// <summary>Directions ignore translation — used for normals and axes.</summary>
	public Vec3 TransformDirection( Vec3 d ) => X * d.x + Y * d.y + Z * d.z;

	/// <summary>
	/// Negative when the transform flips handedness, which happens for a mirror and for an odd
	/// number of negative scale axes.
	///
	/// THIS IS LOAD-BEARING. A flipped transform turns every face inside out — the geometry looks
	/// correct in a solid render right up until you notice the lighting is wrong and the normals
	/// point into the model. Anything applying an Xform to a mesh has to check this and reverse
	/// the winding. See MeshTransform.Apply.
	/// </summary>
	public float Determinant =>
		X.x * (Y.y * Z.z - Y.z * Z.y)
		- Y.x * (X.y * Z.z - X.z * Z.y)
		+ Z.x * (X.y * Y.z - X.z * Y.y);

	public bool FlipsWinding => Determinant < 0f;

	public static Xform Translate( Vec3 offset ) =>
		new( Identity.X, Identity.Y, Identity.Z, offset );

	public static Xform Scale( Vec3 s ) =>
		new( new Vec3( s.x, 0, 0 ), new Vec3( 0, s.y, 0 ), new Vec3( 0, 0, s.z ), Vec3.Zero );

	/// <summary>Rotation about an arbitrary axis through the origin, via Rodrigues' formula.</summary>
	public static Xform Rotate( Vec3 axis, float radians )
	{
		var a = axis.Normal;

		if ( a.LengthSquared < 0.5f )
			return Identity;

		var c = MathF.Cos( radians );
		var s = MathF.Sin( radians );
		var t = 1f - c;

		return new Xform(
			new Vec3( t * a.x * a.x + c, t * a.x * a.y + s * a.z, t * a.x * a.z - s * a.y ),
			new Vec3( t * a.x * a.y - s * a.z, t * a.y * a.y + c, t * a.y * a.z + s * a.x ),
			new Vec3( t * a.x * a.z + s * a.y, t * a.y * a.z - s * a.x, t * a.z * a.z + c ),
			Vec3.Zero );
	}

	/// <summary>Reflection in the plane through `point` with the given normal.</summary>
	public static Xform Mirror( Vec3 planePoint, Vec3 planeNormal )
	{
		var n = planeNormal.Normal;

		if ( n.LengthSquared < 0.5f )
			return Identity;

		// Householder reflection: I - 2nn^T, then translated so the plane passes through the point.
		Vec3 Reflect( Vec3 v ) => v - n * (2f * Vec3.Dot( v, n ));

		var basisX = Reflect( new Vec3( 1, 0, 0 ) );
		var basisY = Reflect( new Vec3( 0, 1, 0 ) );
		var basisZ = Reflect( new Vec3( 0, 0, 1 ) );
		var origin = n * (2f * Vec3.Dot( planePoint, n ));

		return new Xform( basisX, basisY, basisZ, origin );
	}

	/// <summary>Rotation about an axis that does not pass through the origin.</summary>
	public static Xform RotateAbout( Vec3 axisPoint, Vec3 axisDirection, float radians )
	{
		var r = Rotate( axisDirection, radians );
		return Translate( axisPoint ) * r * Translate( -axisPoint );
	}

	/// <summary>Applies `b` first, then `a` — the usual matrix convention.</summary>
	public static Xform operator *( Xform a, Xform b ) => new(
		a.TransformDirection( b.X ),
		a.TransformDirection( b.Y ),
		a.TransformDirection( b.Z ),
		a.TransformPoint( b.Origin ) );
}

public static class MeshTransform
{
	/// <summary>
	/// Transform a mesh in place, reversing face winding when the transform flips handedness.
	///
	/// Forgetting the reversal is the single most common mirror bug: the mirrored half renders
	/// black or lit from inside, and the model reads as fine in wireframe. There is a test for it.
	/// </summary>
	public static void Apply( PolyMesh mesh, Xform xform )
	{
		for ( var i = 0; i < mesh.Positions.Count; i++ )
			mesh.Positions[i] = xform.TransformPoint( mesh.Positions[i] );

		if ( !xform.FlipsWinding )
			return;

		foreach ( var f in mesh.Faces )
		{
			Array.Reverse( f.Indices );
			Array.Reverse( f.UVs );
		}
	}

	public static PolyMesh Transformed( PolyMesh mesh, Xform xform )
	{
		var copy = mesh.Clone();
		Apply( copy, xform );
		return copy;
	}

	/// <summary>
	/// Merge `source` into `target`, offsetting indices. Does not weld — two bodies combined this
	/// way stay topologically separate, which is correct for a pattern and is why Validate reports
	/// them as one mesh with several shells rather than as non-manifold.
	/// </summary>
	public static void Append( PolyMesh target, PolyMesh source )
	{
		var offset = target.Positions.Count;
		target.Positions.AddRange( source.Positions );

		foreach ( var f in source.Faces )
		{
			var indices = new int[f.Count];

			for ( var i = 0; i < f.Count; i++ )
				indices[i] = f.Indices[i] + offset;

			target.AddFace( indices, (Vec2[])f.UVs.Clone(), f.Material );
		}
	}
}
