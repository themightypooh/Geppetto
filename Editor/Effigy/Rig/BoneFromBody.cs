using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A bone shaped like the body it will move.
///
/// WHY THIS MEASURES GEOMETRY RATHER THAN READING THE FEATURE THAT BUILT IT. The obvious way to
/// turn a modelled finger into a bone is to ask its Extrude: that feature knows a direction, a
/// distance and a sketch plane, so head, tail and roll all fall straight out of parameters somebody
/// already typed. It is also a trap. It answers for exactly the feature types that happen to carry
/// an axis and has nothing to say about every body that reached its shape another way — a
/// primitive, a revolve, a boolean of two solids, anything filleted or shelled after the fact — and
/// it has to be extended once per feature type by whoever adds the next one, who will have no
/// reason to know they were supposed to. The tool would appear to work and would quietly cover less
/// of the modeller's vocabulary every release.
///
/// A body is a bag of positions no matter what built it. One reader covers the whole tool, cannot
/// fall behind the feature list, and keeps working through a rebuild that changes which features
/// produced the shape. The cost is that the answer is inferred rather than declared, which is why
/// everything below is stated as what it measures rather than what it intends.
///
/// WHAT IT MEASURES. The principal axes of the body's vertices: the direction they spread out along
/// most becomes the bone's aim, the widest direction across that becomes its roll, and the bone
/// spans the body's full extent along the aim. For anything limb-shaped — which is what people rig
/// — that is the answer they would have drawn by hand.
/// </summary>
public static class BoneFromBody
{
	/// <summary>
	/// Head, tail and an up-hint for a bone spanning <paramref name="positions"/>, or false if the
	/// points have no length to span.
	///
	/// <paramref name="anchor"/> is a point the bone should START near — the parent bone's tail,
	/// when there is a parent. WITHOUT IT THERE IS NO WAY TO KNOW WHICH END IS THE ROOT: a
	/// principal axis is a line, not an arrow, and a finger measured on its own is as much
	/// fingertip-to-knuckle as the reverse. The fallback orientation is therefore canonical rather
	/// than correct — the same shape always yields the same answer, so a patterned row of fingers
	/// comes out consistent — and the caller is expected to pass an anchor whenever it knows one.
	///
	/// The returned <c>up</c> is a HINT in the sense <see cref="Skeleton.AddBoneFromPoints"/> means
	/// it: the widest direction across the bone, already perpendicular to the aim, which that method
	/// is free to fall back from if it ever degenerates.
	/// </summary>
	public static bool TryDerive( IReadOnlyList<Vec3> positions,
		out Vec3 head, out Vec3 tail, out Vec3 up, Vec3? anchor = null )
	{
		head = Vec3.Zero;
		tail = Vec3.Zero;
		up = new Vec3( 0, 0, 1 );

		if ( positions is null || positions.Count < 2 )
			return false;

		var centre = Vec3.Zero;

		foreach ( var p in positions )
			centre += p;

		centre /= positions.Count;

		// The covariance of the point cloud about its centre. Symmetric, so six terms rather than
		// nine, and deliberately NOT divided by the count: eigenvectors do not care about scale and
		// the division is one more place for a zero-length body to produce a NaN.
		float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;

		foreach ( var p in positions )
		{
			var d = p - centre;

			xx += d.x * d.x;
			xy += d.x * d.y;
			xz += d.x * d.z;
			yy += d.y * d.y;
			yz += d.y * d.z;
			zz += d.z * d.z;
		}

		var aim = DominantAxis( xx, xy, xz, yy, yz, zz );

		if ( aim.LengthSquared < 0.5f )
			return false;

		// A principal axis has no sign, so pin one. Largest-magnitude component positive is
		// arbitrary but total: it depends only on the shape, so two bodies of the same shape in the
		// same orientation — a pattern, a mirror — always come out pointing the same way, which is
		// the property that actually matters when the fallback is used.
		if ( LargestComponent( aim ) < 0 )
			aim = -aim;

		float min = float.MaxValue, max = float.MinValue;

		foreach ( var p in positions )
		{
			var t = Vec3.Dot( p - centre, aim );

			if ( t < min ) min = t;
			if ( t > max ) max = t;
		}

		if ( max - min < 1e-4f )
			return false;

		head = centre + aim * min;
		tail = centre + aim * max;
		up = WidestAcross( positions, centre, aim );

		// The anchor knows what the geometry cannot: which end joins the rest of the model. It only
		// ever swaps the two ends — it never moves them — so the bone still spans exactly the body.
		if ( anchor is Vec3 near
			&& (near - tail).LengthSquared < (near - head).LengthSquared )
		{
			(head, tail) = (tail, head);
		}

		return true;
	}

	/// <summary>Convenience over a whole mesh, which is what a caller holding a Body has.</summary>
	public static bool TryDerive( PolyMesh mesh,
		out Vec3 head, out Vec3 tail, out Vec3 up, Vec3? anchor = null )
	{
		if ( mesh is null )
		{
			head = tail = Vec3.Zero;
			up = new Vec3( 0, 0, 1 );
			return false;
		}

		return TryDerive( mesh.Positions, out head, out tail, out up, anchor );
	}

	/// <summary>
	/// The eigenvector of the covariance with the largest eigenvalue, by power iteration.
	///
	/// Iteration rather than a closed form because the closed form for a symmetric 3x3 goes through
	/// trigonometric roots that lose precision exactly where this is used most — a long thin limb,
	/// where one eigenvalue dwarfs the other two. That same lopsidedness is what makes power
	/// iteration converge almost immediately here.
	///
	/// A BODY WITH NO LONGEST AXIS gets an arbitrary but repeatable answer rather than a refusal. A
	/// cube's three eigenvalues are equal and every direction is a legitimate principal axis, so
	/// there is nothing to be right about; the seed decides, the seed is derived from the shape, and
	/// the same cube therefore always produces the same bone. A caller who wants a specific
	/// direction out of a symmetric body has to say so by drawing the bone.
	/// </summary>
	static Vec3 DominantAxis( float xx, float xy, float xz, float yy, float yz, float zz )
	{
		Vec3 Multiply( Vec3 v ) => new(
			xx * v.x + xy * v.y + xz * v.z,
			xy * v.x + yy * v.y + yz * v.z,
			xz * v.x + yz * v.y + zz * v.z );

		// Seeding with a world axis risks starting exactly perpendicular to the answer, which
		// stalls the iteration. Multiplying a spread of directions through the matrix once and
		// keeping the longest result cannot: the product is already biased toward the dominant
		// eigenvector, and only an all-zero matrix leaves every candidate at zero.
		var seed = Vec3.Zero;

		foreach ( var candidate in new[]
		{
			new Vec3( 1, 1, 1 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 )
		} )
		{
			var mapped = Multiply( candidate );

			if ( mapped.LengthSquared > seed.LengthSquared )
				seed = mapped;
		}

		var v = seed.Normal;

		if ( v.LengthSquared < 0.5f )
			return Vec3.Zero;

		for ( var i = 0; i < 64; i++ )
		{
			var next = Multiply( v ).Normal;

			if ( next.LengthSquared < 0.5f )
				return v;

			// Converged: another sixty iterations would move it by less than the tolerance every
			// consumer of this already rounds away.
			if ( (next - v).LengthSquared < 1e-14f )
				return next;

			v = next;
		}

		return v;
	}

	/// <summary>
	/// The direction across the bone that the body is widest in — its roll.
	///
	/// Solved in the two dimensions that are left once the aim is fixed, where the dominant
	/// eigenvector of a symmetric 2x2 IS a closed form and needs no iteration at all. A section that
	/// is genuinely round makes the off-diagonal term vanish and the answer falls out as "either of
	/// the two axes we happened to build", which is the honest result for a cylinder: nothing about
	/// it prefers a roll.
	/// </summary>
	static Vec3 WidestAcross( IReadOnlyList<Vec3> positions, Vec3 centre, Vec3 aim )
	{
		var seed = MathF.Abs( aim.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
		var e0 = Vec3.Cross( seed, aim ).Normal;
		var e1 = Vec3.Cross( aim, e0 );

		float a = 0, b = 0, d = 0;

		foreach ( var p in positions )
		{
			var r = p - centre;
			var u = Vec3.Dot( r, e0 );
			var v = Vec3.Dot( r, e1 );

			a += u * u;
			b += u * v;
			d += v * v;
		}

		if ( MathF.Abs( b ) < 1e-12f )
			return a >= d ? e0 : e1;

		var theta = 0.5f * MathF.Atan2( 2f * b, a - d );

		return e0 * MathF.Cos( theta ) + e1 * MathF.Sin( theta );
	}

	/// <summary>The component with the largest magnitude, ties broken x then y then z so the
	/// choice is a function of the vector alone.</summary>
	static float LargestComponent( Vec3 v )
	{
		var ax = MathF.Abs( v.x );
		var ay = MathF.Abs( v.y );
		var az = MathF.Abs( v.z );

		if ( ax >= ay && ax >= az )
			return v.x;

		return ay >= az ? v.y : v.z;
	}
}
