using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Tapering faces of a solid that already exists, so a part can leave a mould.
///
/// EXTRUDE'S TAPER COVERS A FACE BEING MADE; this covers one that is already there. They are not the
/// same operation and neither substitutes for the other: taper is a parameter of the pull that
/// created the wall, and by the time you know the part needs draft the pull is usually twenty
/// features back and its profile has been filleted, patterned and cut since.
///
/// THE METHOD, which is the whole of it: every vertex moves along the HORIZONTAL component of its
/// own normal — horizontal meaning perpendicular to the pull direction — by an amount proportional
/// to its signed distance from the neutral plane. Vertices on the neutral plane do not move at all,
/// which is what makes that plane the parting line. Above it the wall leans one way, below it the
/// other.
///
/// A FACE THAT FACES THE PULL CANNOT BE DRAFTED. Its normal has no horizontal component, so there is
/// no direction to lean it in — drafting the top of a box along +Z is not a small effect, it is not
/// an operation. Said outright rather than silently doing nothing.
/// </summary>
public static class DraftOperation
{
	/// <summary>
	/// Draft <paramref name="faceIndices"/> of <paramref name="mesh"/>, returning a new mesh.
	///
	/// The neutral plane is perpendicular to <paramref name="pull"/> and passes through
	/// <paramref name="neutralPoint"/>. A positive angle leans each face outward as it goes with the
	/// pull, which is the direction that lets a part lift out of a mould pulled that way.
	/// </summary>
	public static PolyMesh Draft( PolyMesh mesh, IReadOnlyCollection<int> faceIndices,
		Vec3 neutralPoint, Vec3 pull, float angleDegrees )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( faceIndices is null || faceIndices.Count == 0 )
			throw new InvalidOperationException( "Draft needs at least one face to act on." );

		if ( pull.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "The pull direction has no length, so there is no draft to apply." );

		if ( MathF.Abs( angleDegrees ) < 1e-4f )
			throw new InvalidOperationException( "A draft angle of zero would change nothing." );

		if ( MathF.Abs( angleDegrees ) >= 89f )
			throw new InvalidOperationException(
				$"A draft angle of {angleDegrees}deg is past vertical; the wall would fold through itself." );

		var direction = pull.Normal;
		var tangent = MathF.Tan( angleDegrees * MathF.PI / 180f );

		foreach ( var index in faceIndices )
		{
			if ( index < 0 || index >= mesh.FaceCount )
				throw new ArgumentOutOfRangeException( nameof( faceIndices ),
					$"Face {index} is not on this body, which has {mesh.FaceCount} faces." );
		}

		// The normal a vertex is drafted along is averaged over the SELECTED faces only. A vertex on
		// the boundary of the selection also belongs to faces that are staying put, and letting those
		// into the average would lean it by an amount that has nothing to do with what was asked for.
		var accumulated = new Dictionary<int, Vec3>();
		var counts = new Dictionary<int, int>();

		foreach ( var index in faceIndices )
		{
			var face = mesh.Faces[index];
			var normal = mesh.FaceNormal( face );

			if ( normal.LengthSquared < 1e-16f )
				continue;

			normal = normal.Normal;

			foreach ( var vertex in face.Indices )
			{
				accumulated[vertex] = accumulated.TryGetValue( vertex, out var sum )
					? sum + normal
					: normal;

				counts[vertex] = counts.TryGetValue( vertex, out var n ) ? n + 1 : 1;
			}
		}

		var flat = 0;
		var result = mesh.Clone();

		foreach ( var (vertex, sum) in accumulated )
		{
			if ( sum.LengthSquared < 1e-16f )
				continue;

			var count = counts[vertex];
			var normal = sum.Normal;
			var horizontal = normal - direction * Vec3.Dot( normal, direction );

			// The face looks straight along the pull. There is no direction to lean it in.
			if ( horizontal.LengthSquared < 1e-8f )
			{
				flat++;
				continue;
			}

			// A CORNER BELONGS TO TWO WALLS AND HAS TO LEAN BOTH OF THEM BY THE ANGLE.
			//
			// Moving it along the averaged normal by distance x tan leans each wall by LESS than
			// asked: on a box corner the average points along the diagonal, so each wall only gets
			// its component - about 7 degrees out of 10. The part comes out under-drafted, which is
			// the failure that matters here, because the whole reason for a draft angle is that a
			// mould needs at least that much and a bit less will not release.
			//
			// Scaling by the average of dot(average, face normal) - which is exactly |sum| / count -
			// puts each face back on the angle it was given.
			var share = sum.Length / count;

			if ( share < 1e-4f )
			{
				// The picked faces at this vertex point in opposing directions, so no single
				// displacement can lean them all outward. Leave it where it is rather than send it
				// somewhere arbitrary; the validation below will speak if that broke anything.
				continue;
			}

			var p = result.Positions[vertex];
			var distance = Vec3.Dot( p - neutralPoint, direction );

			result.Positions[vertex] = p + horizontal.Normal * (distance * tangent / share);
		}

		if ( flat > 0 && flat == accumulated.Count )
		{
			throw new InvalidOperationException(
				"Every picked face looks straight along the pull direction, so none of them can be drafted." );
		}

		Validate( mesh, result, faceIndices, angleDegrees );

		return result;
	}

	/// <summary>
	/// The three checks LoopOffset already uses, for the same reason it uses them: a wall leaned too
	/// far does not fail, it turns inside out, and the result is closed, manifold and wrong.
	///
	/// The third one — a face whose normal reversed — is the one that catches it. Area alone stays
	/// positive through an inversion, because area has no sign in three dimensions.
	/// </summary>
	static void Validate( PolyMesh before, PolyMesh after, IReadOnlyCollection<int> faceIndices, float angleDegrees )
	{
		foreach ( var index in faceIndices )
		{
			var face = after.Faces[index];
			var area = after.FaceArea( face );

			if ( area < 1e-9f )
			{
				throw new InvalidOperationException(
					$"A draft of {angleDegrees}deg collapses face {index} to nothing." );
			}

			var was = before.FaceNormal( before.Faces[index] );
			var now = after.FaceNormal( face );

			if ( was.LengthSquared > 1e-16f && now.LengthSquared > 1e-16f
				&& Vec3.Dot( was.Normal, now.Normal ) <= 0f )
			{
				throw new InvalidOperationException(
					$"A draft of {angleDegrees}deg turns face {index} inside out." );
			}

			// THE THIRD CHECK, and the one that actually catches a drafted box.
			//
			// A wall whose bottom edge has swung past its own far side is a bow-tie: two of its
			// corners have crossed over. It is not collapsed - it still has area - and its Newell
			// normal still points roughly where it did, so neither of the checks above sees it. Only
			// asking whether an EDGE now runs backwards does.
			//
			// This is the check LoopOffset's comment calls the one that catches the inside-out case
			// the other two call healthy, and it was named here before it was written.
			var original = before.Faces[index];

			for ( var c = 0; c < face.Count; c++ )
			{
				var next = (c + 1) % face.Count;

				var wasEdge = before.Positions[original.Indices[next]] - before.Positions[original.Indices[c]];
				var nowEdge = after.Positions[face.Indices[next]] - after.Positions[face.Indices[c]];

				if ( wasEdge.LengthSquared < 1e-12f || nowEdge.LengthSquared < 1e-12f )
					continue;

				if ( Vec3.Dot( wasEdge.Normal, nowEdge.Normal ) <= 0f )
				{
					throw new InvalidOperationException(
						$"A draft of {angleDegrees}deg folds face {index} through itself." );
				}
			}
		}
	}

	/// <summary>
	/// The largest angle that still leaves every picked face the right way out, by bisection.
	///
	/// The same trick fillet and shell use to answer "what WOULD work" — a refusal that names a
	/// number you can act on is worth ten that only say no.
	/// </summary>
	public static float LargestAngle( PolyMesh mesh, IReadOnlyCollection<int> faceIndices,
		Vec3 neutralPoint, Vec3 pull, float wanted )
	{
		var sign = wanted < 0f ? -1f : 1f;
		var low = 0f;
		var high = MathF.Min( MathF.Abs( wanted ), 88f );

		for ( var i = 0; i < 24; i++ )
		{
			var mid = (low + high) * 0.5f;

			try
			{
				Draft( mesh, faceIndices, neutralPoint, pull, mid * sign );
				low = mid;
			}
			catch ( InvalidOperationException )
			{
				high = mid;
			}
		}

		return low * sign;
	}
}
