using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A reference to a face of some body, stored as geometry rather than as an index.
///
/// THIS IS THE WHOLE POINT OF THE TYPE. FreeCAD refers to sub-elements by name — "Face6" — and
/// that name comes from the shape's element ordering, which changes whenever anything upstream
/// changes. A pocket attached to Face6 silently moves to a different face after an unrelated edit.
/// It is the topological naming problem and it is their best-known long-running defect.
///
/// A point on the face plus its normal can be RE-FOUND after a rebuild. It survives any edit that
/// does not destroy the face, and it degrades honestly when one does — nothing matches, and the
/// feature says so, rather than silently attaching somewhere else.
///
/// Same principle as SketchConsumingFeature.RegionSeed, for the same reason.
///
/// BUT PURE GEOMETRY IS NOT ENOUGH ON ITS OWN, and a test caught that. A point and a normal survive
/// an unrelated edit upstream perfectly, and break the moment the referenced face ITSELF moves —
/// make the box taller and the stored point is no longer anywhere near its top face. FreeCAD's
/// "Face6" has the opposite failure: it follows a face that moves, and silently jumps to a
/// different one when the ordering changes.
///
/// So the reference carries the BODY it was taken from as well. Body ids are already kept stable
/// across rebuilds for exactly this kind of use (see FeatureContext.SeedIdCounter). Resolution is
/// then: find that body, take the faces pointing the right way, and among those pick the one
/// nearest the stored point. The point disambiguates between candidates rather than acting as a
/// hard constraint, which is what lets the face move and still be found.
/// </summary>
public readonly struct FaceRef
{
	/// <summary>Which body the face belongs to. Ids are stable across rebuilds.</summary>
	public readonly string BodyId;

	/// <summary>A point on the face when it was chosen, in model space. Used to pick between faces
	/// of the same body pointing the same way — not as an exact test, so the face may move.</summary>
	public readonly Vec3 Point;

	/// <summary>The face's outward normal, which disambiguates the two faces of a thin wall that
	/// a point alone would not tell apart.</summary>
	public readonly Vec3 Normal;

	public FaceRef( string bodyId, Vec3 point, Vec3 normal )
	{
		BodyId = bodyId;
		Point = point;
		Normal = normal.Normal;
	}
}

/// <summary>
/// Turning a face of an existing body into a plane you can sketch on.
///
/// Neither Solvespace nor FreeCAD treats "sketch on a face" as a sketching mode: it is a DERIVED
/// PLANE, and the sketcher then works exactly as it always does. Solvespace has workplane groups;
/// FreeCAD has an Attacher that recomputes a placement from whatever it is attached to. This is
/// Effigy's version, and it changes nothing about how sketching works.
/// </summary>
public static class FacePlane
{
	/// <summary>
	/// Build a sketch plane at a point with a given normal.
	///
	/// The in-plane axes are derived from the normal alone, deterministically, so the same face
	/// yields the same axes on every rebuild. Taking them from the face's own first edge would be
	/// tempting and wrong: edge order changes when the mesh is rebuilt, and the sketch would spin
	/// on its own plane while its coordinates stayed the same.
	/// </summary>
	public static SketchPlane FromPointAndNormal( Vec3 point, Vec3 normal )
	{
		var n = normal.Normal;

		// Cross with whichever world axis the normal is least aligned to, so the result never
		// collapses to zero length.
		var seed = MathF.Abs( n.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );

		var x = Vec3.Cross( seed, n ).Normal;
		var y = Vec3.Cross( n, x ).Normal;

		return new SketchPlane( point, x, y );
	}

	/// <summary>
	/// Find the face a reference points at, and return the plane to sketch on.
	///
	/// Matching is by geometry: the face's normal must agree with the reference's, and the
	/// reference point must lie on the face's plane. Among the faces that qualify, the one whose
	/// centroid is nearest the reference point wins — which is what keeps a reference on the right
	/// face of two coplanar ones.
	/// </summary>
	public static bool TryResolve( IEnumerable<Body> bodies, FaceRef reference, out SketchPlane plane,
		float normalTolerance = 0.01f )
	{
		plane = null;

		if ( bodies is null )
			return false;

		// Scoped to the body it came from. Without that, "the point is no longer on the plane"
		// either has to fail when the face moves, or has to search the whole model and risk
		// landing on some unrelated coplanar face.
		Body body = null;

		foreach ( var candidate in bodies )
		{
			if ( candidate?.Mesh is not null && candidate.Id == reference.BodyId )
			{
				body = candidate;
				break;
			}
		}

		if ( body is null )
			return false;

		var bestDistance = float.MaxValue;
		Vec3 bestOrigin = default;
		Vec3 bestNormal = default;
		var found = false;

		foreach ( var face in body.Mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var normal = body.Mesh.FaceNormal( face );

			// Same way up. A thin wall has two faces on nearly the same plane and only the normal
			// separates them.
			if ( Vec3.Dot( normal, reference.Normal ) < 1f - normalTolerance )
				continue;

			var centroid = body.Mesh.FaceCentroid( face );
			var distance = (centroid - reference.Point).Length;

			if ( distance >= bestDistance )
				continue;

			bestDistance = distance;
			bestOrigin = centroid;
			bestNormal = normal;
			found = true;
		}

		if ( !found )
			return false;

		// Anchor at the stored point projected onto the face it resolved to, so sketch coordinates
		// stay put when the face moves along its own normal - which is exactly what happens when
		// the block underneath gets taller.
		var offset = Vec3.Dot( reference.Point - bestOrigin, bestNormal );
		var origin = reference.Point - bestNormal * offset;

		plane = FromPointAndNormal( origin, bestNormal );
		return true;
	}
}
