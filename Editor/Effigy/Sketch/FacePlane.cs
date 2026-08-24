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

	/// <summary>
	/// Where on the face the sketch sits, as a distance IN FROM THE FACE'S NEAREST EDGE along each
	/// of the face's own axes. This is what makes a sketch ride its face.
	///
	/// Three rules were possible and only this one matches what people mean. Anchoring to the
	/// absolute point ties the sketch to the face's infinite PLANE: shorten an extrude and its own
	/// side faces shrink away from underneath the sketch, leaving everything built on it hanging in
	/// the air. Anchoring to the CENTROID follows the face but only halfway — a tab placed 10 units
	/// in from the end of a 125-long face is 52.5 from the centre, and staying 52.5 from the centre
	/// of a 75-long face puts it 15 units past the end. Anchoring in from the nearest edge keeps
	/// "10 units in from the end" true at any length, which is the thing that was actually meant.
	/// </summary>
	public readonly Vec2 Anchor;

	/// <summary>Which side of the face each axis of <see cref="Anchor"/> is measured from: false is
	/// the low edge, true the high one. Whichever the sketch was nearer to when it was placed — a
	/// tab near the end of a bar holds its distance from THAT end, not from the far one.</summary>
	public readonly bool AnchorFromMaxX;
	public readonly bool AnchorFromMaxY;

	/// <summary>False for a reference made before an anchor was recorded, which falls back to
	/// sitting at the centre of whatever face it resolves to. Without the distinction those
	/// references would read a (0,0) anchor as "hard against the bottom-left corner".</summary>
	public readonly bool Anchored;

	/// <summary>The face's outward normal, which disambiguates the two faces of a thin wall that
	/// a point alone would not tell apart.</summary>
	public readonly Vec3 Normal;

	public FaceRef( string bodyId, Vec3 point, Vec3 normal )
	{
		BodyId = bodyId;
		Point = point;
		Normal = normal.Normal;
		Anchor = Vec2.Zero;
		AnchorFromMaxX = false;
		AnchorFromMaxY = false;
		Anchored = false;
	}

	public FaceRef( string bodyId, Vec3 point, Vec3 normal, Vec2 anchor, bool fromMaxX, bool fromMaxY )
	{
		BodyId = bodyId;
		Point = point;
		Normal = normal.Normal;
		Anchor = anchor;
		AnchorFromMaxX = fromMaxX;
		AnchorFromMaxY = fromMaxY;
		Anchored = true;
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
	/// Capture a reference to the face that was just clicked, recording where on that face the
	/// click landed relative to its centroid. Use this rather than the FaceRef constructor
	/// directly: a reference built without an anchor sits at the centre of whatever face it
	/// resolves to, which is not where anyone clicked.
	/// </summary>
	public static FaceRef Capture( Body body, int faceIndex, Vec3 point )
	{
		if ( body?.Mesh is not { } mesh || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return new FaceRef( body?.Id, point, new Vec3( 0, 0, 1 ) );

		var face = mesh.Faces[faceIndex];
		var normal = mesh.FaceNormal( face );
		var centroid = mesh.FaceCentroid( face );
		var plane = FromPointAndNormal( centroid, normal );

		var bounds = Bounds( mesh, face, plane );
		var local = plane.ToPlane( point );

		// Measured in from whichever edge it is nearer, per axis independently. A sketch near one
		// end and centred across the width therefore holds its inset from that end and stays
		// roughly central across the width, which is how it looks to whoever placed it.
		var fromMaxX = local.x - bounds.MinX > bounds.MaxX - local.x;
		var fromMaxY = local.y - bounds.MinY > bounds.MaxY - local.y;

		var anchor = new Vec2(
			fromMaxX ? bounds.MaxX - local.x : local.x - bounds.MinX,
			fromMaxY ? bounds.MaxY - local.y : local.y - bounds.MinY );

		return new FaceRef( body.Id, point, normal, anchor, fromMaxX, fromMaxY );
	}

	/// <summary>A face's extent in its own plane axes. The axes come from the normal alone (see
	/// FromPointAndNormal), so this is the same box every rebuild for as long as the face points
	/// the same way.</summary>
	static (float MinX, float MaxX, float MinY, float MaxY) Bounds( PolyMesh mesh, Face face, SketchPlane plane )
	{
		var minX = float.MaxValue;
		var maxX = float.MinValue;
		var minY = float.MaxValue;
		var maxY = float.MinValue;

		for ( var i = 0; i < face.Count; i++ )
		{
			var p = plane.ToPlane( mesh.Positions[face.Indices[i]] );

			minX = MathF.Min( minX, p.x );
			maxX = MathF.Max( maxX, p.x );
			minY = MathF.Min( minY, p.y );
			maxY = MathF.Max( maxY, p.y );
		}

		return (minX, maxX, minY, maxY);
	}

	/// <summary>
	/// Find the face a reference points at, and return the plane to sketch on.
	///
	/// Matching is by geometry: the face's normal must agree with the reference's, and the
	/// reference point must lie on the face's plane. Among the faces that qualify, the one whose
	/// centroid is nearest the reference point wins — which is what keeps a reference on the right
	/// face of two coplanar ones.
	/// </summary>
	/// <summary>
	/// Find the face a reference points at: which body, and which face of it.
	///
	/// Split out of TryResolve because two different things now need to re-find a face — a sketch
	/// deriving a plane from it, and a material assignment painting it — and they must agree
	/// exactly about which face that is. Two copies of "nearest face pointing the right way" that
	/// drifted apart would show up as a material landing on one face while the sketch drawn on it
	/// went somewhere else, which is not a failure anyone would enjoy diagnosing.
	/// </summary>
	public static bool TryResolveFace( IEnumerable<Body> bodies, FaceRef reference, out Body body,
		out int faceIndex, float normalTolerance = 0.01f )
	{
		body = null;
		faceIndex = -1;

		if ( bodies is null )
			return false;

		// Scoped to the body it came from. Without that, "the point is no longer on the plane"
		// either has to fail when the face moves, or has to search the whole model and risk
		// landing on some unrelated coplanar face.
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

		for ( var i = 0; i < body.Mesh.Faces.Count; i++ )
		{
			var face = body.Mesh.Faces[i];

			if ( face.Count < 3 )
				continue;

			var normal = body.Mesh.FaceNormal( face );

			// Same way up. A thin wall has two faces on nearly the same plane and only the normal
			// separates them.
			if ( Vec3.Dot( normal, reference.Normal ) < 1f - normalTolerance )
				continue;

			var distance = (body.Mesh.FaceCentroid( face ) - reference.Point).Length;

			if ( distance >= bestDistance )
				continue;

			bestDistance = distance;
			faceIndex = i;
		}

		return faceIndex >= 0;
	}

	public static bool TryResolve( IEnumerable<Body> bodies, FaceRef reference, out SketchPlane plane,
		float normalTolerance = 0.01f )
	{
		plane = null;

		if ( !TryResolveFace( bodies, reference, out var body, out var faceIndex, normalTolerance ) )
			return false;

		var bestFace = body.Mesh.Faces[faceIndex];
		var bestOrigin = body.Mesh.FaceCentroid( bestFace );
		var bestNormal = body.Mesh.FaceNormal( bestFace );

		// ANCHORED TO THE FACE'S EDGES, NOT TO THE PLANE. The origin is rebuilt from the face's
		// CURRENT extent plus the stored inset, so a sketch placed ten units in from the end of a
		// bar is ten units in from the end however long the bar becomes. Projecting the stored
		// absolute point onto the plane instead (what this used to do) is identical whenever the
		// plane moves, and silently wrong whenever the face moves within a plane that does not -
		// which is exactly what shortening an extrude does to its own side faces.
		var axes = FromPointAndNormal( bestOrigin, bestNormal );
		var local = Vec2.Zero;

		if ( reference.Anchored )
		{
			var bounds = Bounds( body.Mesh, bestFace, axes );

			local = new Vec2(
				reference.AnchorFromMaxX ? bounds.MaxX - reference.Anchor.x : bounds.MinX + reference.Anchor.x,
				reference.AnchorFromMaxY ? bounds.MaxY - reference.Anchor.y : bounds.MinY + reference.Anchor.y );
		}

		var origin = bestOrigin + axes.XAxis * local.x + axes.YAxis * local.y;

		plane = FromPointAndNormal( origin, bestNormal );
		return true;
	}
}
