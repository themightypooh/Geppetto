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

	/// <summary>
	/// The inverse transform. General 3x3 inverse rather than the transpose shortcut, because an
	/// Xform is allowed to carry scale and a mirror — the transpose is only correct for a pure
	/// rotation, and silently wrong for everything else this type can hold.
	///
	/// Throws on a singular basis. A zero-scaled transform has no inverse and returning identity
	/// would hide the mistake somewhere far away from its cause.
	/// </summary>
	public Xform Inverse => InverseOf( this );

	static Xform InverseOf( Xform t )
	{
		var det = t.Determinant;

		if ( MathF.Abs( det ) < 1e-12f )
			throw new InvalidOperationException( "Xform is singular and cannot be inverted" );

		// For a matrix whose COLUMNS are X, Y, Z, the inverse has ROWS (Y×Z)/det, (Z×X)/det,
		// (X×Y)/det. Xform stores columns, so the rows are transposed back into columns below.
		var r0 = Vec3.Cross( t.Y, t.Z ) / det;
		var r1 = Vec3.Cross( t.Z, t.X ) / det;
		var r2 = Vec3.Cross( t.X, t.Y ) / det;

		var origin = new Vec3(
			-Vec3.Dot( r0, t.Origin ),
			-Vec3.Dot( r1, t.Origin ),
			-Vec3.Dot( r2, t.Origin ) );

		return new Xform(
			new Vec3( r0.x, r1.x, r2.x ),
			new Vec3( r0.y, r1.y, r2.y ),
			new Vec3( r0.z, r1.z, r2.z ),
			origin );
	}

	/// <summary>
	/// Euler angles in radians, in the convention R = Rz(z) * Ry(y) * Rx(x) — X applied first.
	///
	/// This is general maths, not an export detail, but it is worth saying where it gets used:
	/// SMD stores bone rotations exactly this way, and getting the composition order backwards
	/// produces a skeleton that looks plausible in the node list and is wrong the moment a bone
	/// is not axis-aligned. There is a round-trip test.
	///
	/// The basis is normalised first, so a transform carrying scale still yields the rotation it
	/// represents. A mirrored basis has no Euler representation at all — bones are never mirrored,
	/// and a caller that manages it gets a wrong answer rather than an exception, which is the one
	/// rough edge here.
	/// </summary>
	public Vec3 ToEulerXyz()
	{
		var x = X.Normal;
		var y = Y.Normal;
		var z = Z.Normal;

		// Matrix entry naming below is [row][column]; Xform stores columns, so m20 is X.z.
		var m20 = x.z;

		// cos(pitch) collapses at the poles and the remaining two angles stop being separable —
		// the classic gimbal case. Pin roll to zero and fold everything into the first angle.
		if ( MathF.Abs( m20 ) > 0.999999f )
		{
			var pitch = m20 < 0f ? MathF.PI / 2f : -MathF.PI / 2f;

			return m20 < 0f
				? new Vec3( MathF.Atan2( y.x, y.y ), pitch, 0f )
				: new Vec3( MathF.Atan2( -y.x, y.y ), pitch, 0f );
		}

		return new Vec3(
			MathF.Atan2( y.z, z.z ),
			MathF.Asin( -m20 ),
			MathF.Atan2( x.y, x.x ) );
	}

	/// <summary>Rebuilds a rotation from ToEulerXyz's angles. Same convention, so the two
	/// round-trip.</summary>
	public static Xform FromEulerXyz( Vec3 radians )
	{
		return Rotate( new Vec3( 0, 0, 1 ), radians.z )
			* Rotate( new Vec3( 0, 1, 0 ), radians.y )
			* Rotate( new Vec3( 1, 0, 0 ), radians.x );
	}
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

		// Weights have to be reconciled BEFORE the position lists merge, because both sides are
		// padded against their own current vertex count. Merging an unrigged body into a rigged one
		// is normal — a rig usually arrives after some of the model does — so the unrigged side is
		// padded with empty influences rather than treated as an error.
		if ( target.Skin is not null || source.Skin is not null )
		{
			target.Skin ??= new SkinWeights( target.Positions.Count );

			while ( target.Skin.Count < target.Positions.Count )
				target.Skin.Vertices.Add( new[] { new BoneWeight( 0, 1f ) } );

			// An unrigged body merged into a rigged one gets bound to the FIRST BONE rather than
			// left empty. Empty influences pass IsRigged, fail Validate, and export as "no links",
			// which studiomdl reads as the parent bone column - so the body silently ends up rigged
			// to whatever bone 0 happens to be, discovered much later and somewhere else. Binding it
			// explicitly is the same outcome, stated out loud, and it keeps the partition of unity
			// that everything downstream assumes.
			var unrigged = new[] { new BoneWeight( 0, 1f ) };

			for ( var i = 0; i < source.Positions.Count; i++ )
			{
				target.Skin.Vertices.Add( source.Skin is not null && i < source.Skin.Count && source.Skin[i].Length > 0
					? (BoneWeight[])source.Skin[i].Clone()
					: unrigged );
			}
		}

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
