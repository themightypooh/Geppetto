using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Move faces of a solid that already exists, and let the walls around them follow.
///
/// THE OPERATION THAT MAKES A PART EDITABLE. Everything else in this kernel builds forwards: a
/// sketch becomes a prism, a prism gets a fillet. Nothing could take a face of a finished part and
/// simply move it, which is the single most reached-for edit in any CAD tool and the one po hit
/// first — select a face, push it, watch the solid grow.
///
/// TWO MODES, ONE SOLVE, and that is the whole design.
///
/// - **Offset** moves each chosen face along its OWN normal by the same distance. On a facing pair
///   that makes the wall between them thicker or thinner.
/// - **Translate** moves the chosen faces together along one direction. On a facing pair that
///   slides the wall and KEEPS its thickness — material added on one side and taken from the other,
///   which is the thing you actually want when a wall is in the wrong place.
///
/// They differ only in the number handed to each face: offset gives every face `t_f = distance`,
/// translate gives it `t_f = dot(n_f, v)`. That is it. Writing it this way means one tested path
/// rather than two that drift, and it is why the projection is written out below rather than
/// branching on the mode inside the solve.
///
/// WHERE MOVED FACES MEET FACES THAT STAY, which is the only hard part. A rim vertex has to land
/// where the moved face's plane AT ITS NEW OFFSET meets its unmoved neighbours' planes UNCHANGED.
/// That is exactly what PlaneOffset solves, in closed form, and it is the same machinery shell and
/// bevel already stand on — with one generalisation: a distance per plane rather than one distance
/// for all of them, because the moved face asks for `t` and the neighbours ask for zero.
///
/// The pay-off is that slanted neighbours come out right for free. Slide a wall past a chamfered
/// edge and the chamfer keeps its angle, because the chamfer's plane is one of the constraints and
/// nothing here ever assumed a vertex travels along a normal.
///
/// WHAT IS REFUSED RATHER THAN APPROXIMATED, all of it deliberate and none of it a TODO:
///
/// - A face that is not planar. "Along the normal" of a face that has no single normal is not a
///   small error, it is not an operation. Same standing as Draft's refusal to taper a face that
///   looks along the pull.
/// - A move that contradicts itself — a face moving while a COPLANAR neighbour stays put asks one
///   vertex to be in two places, and PlaneOffset reports it rather than fitting something plausible.
/// - A solve with no exact answer, which is anti-parallel neighbours.
/// - A face that turns itself inside out on the way, which is the local signature of the surface
///   having been pushed through itself. Same check ShellOperation makes, for the same reason.
///
/// WHAT IS NOT CHECKED, said out loud so it is not discovered as a bug: a face pushed far enough to
/// meet geometry it does not touch. That needs a genuine boolean, and faking it would produce a
/// self-intersecting mesh that passes every validator in this repo. The local checks below catch
/// every case where something goes wrong AT the moved face; two distant walls closing on each other
/// is the case they cannot see.
/// </summary>
public static class FaceMove
{
	/// <summary>How far off its own plane a face's corners may sit, relative to the face's own size,
	/// before it stops being a plane you can move. Loose enough to accept a face that arithmetic has
	/// nudged, tight enough to refuse anything actually curved.</summary>
	public const float PlanarTolerance = 1e-3f;

	/// <summary>
	/// Move each face along its own normal by <paramref name="distance"/>. Positive is outward.
	///
	/// On a facing pair this is the mode that changes a wall's THICKNESS: each face travels along
	/// its own outward normal, so the two move apart.
	/// </summary>
	public static PolyMesh Offset( PolyMesh mesh, IReadOnlyCollection<int> faces, float distance ) =>
		Move( mesh, faces, distance, direction: null );

	/// <summary>
	/// Move every chosen face together, by one displacement.
	///
	/// On a facing pair this is the mode that SLIDES a wall and keeps its thickness — the two faces
	/// travel the same way, so the gap between them is unchanged and the material is added on one
	/// side and taken from the other.
	///
	/// A face whose normal is perpendicular to the travel gets `t_f = 0` and so does not move at
	/// all, which is correct rather than a special case: sliding a wall sideways does not move the
	/// faces at its ends, it stretches them.
	/// </summary>
	public static PolyMesh Translate( PolyMesh mesh, IReadOnlyCollection<int> faces, Vec3 displacement ) =>
		Move( mesh, faces, distance: 0f, direction: displacement );

	/// <summary>
	/// The shared solve. <paramref name="direction"/> null means offset mode; otherwise it is the
	/// translation and <paramref name="distance"/> is ignored.
	/// </summary>
	static PolyMesh Move( PolyMesh mesh, IReadOnlyCollection<int> faces, float distance, Vec3? direction )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( faces is null || faces.Count == 0 )
			throw new InvalidOperationException( "No faces picked — click the faces you want to move." );

		var moved = new HashSet<int>();

		foreach ( var index in faces )
		{
			if ( index < 0 || index >= mesh.Faces.Count )
			{
				throw new InvalidOperationException(
					$"Face {index} is not on this part any more — it has {mesh.Faces.Count} faces." );
			}

			moved.Add( index );
		}

		var normals = new Vec3[mesh.Faces.Count];

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
			normals[fi] = mesh.FaceNormal( mesh.Faces[fi] );

		// --- how far each moved face is asked to travel ----------------------------------------
		//
		// The one line where the two modes differ. Offset hands every face the same number; translate
		// PROJECTS the displacement onto each face's normal, which is what makes a facing pair slide
		// rather than fatten.
		var target = new Dictionary<int, float>( moved.Count );

		foreach ( var fi in moved )
		{
			RefuseIfNotPlanar( mesh, fi, normals[fi] );

			target[fi] = direction is { } v ? Vec3.Dot( normals[fi], v ) : distance;
		}

		if ( target.Values.All( t => MathF.Abs( t ) < 1e-9f ) )
			return mesh.Clone();

		// --- every vertex a moved face touches --------------------------------------------------
		var vertexFaces = mesh.BuildVertexFaces();
		var positions = new List<Vec3>( mesh.Positions );

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			var incident = vertexFaces[vi];

			if ( !incident.Any( moved.Contains ) )
				continue;

			var (planes, distances) = Constraints( mesh, vi, incident, normals, target );

			if ( !PlaneOffset.TrySolve( planes, distances, out var displacement ) )
			{
				throw new InvalidOperationException(
					"That move has no exact answer where the faces meet — a corner here is asked to sit "
					+ "against neighbours that face away from each other. Move fewer faces at once, or "
					+ "move them a shorter way." );
			}

			positions[vi] = mesh.Positions[vi] + displacement;
		}

		var result = mesh.Clone();
		result.Positions = positions;

		RefuseIfFolded( mesh, result, normals );

		return result;
	}

	/// <summary>
	/// The planes meeting at one vertex and the offset each is asked for: the moved faces' own
	/// targets, and ZERO for everything else, because a face that is not moving must end up exactly
	/// where it already is.
	///
	/// Near-duplicate normals collapse to one constraint, the way they do for shell — one flat
	/// surface split into several polygons is one plane, and feeding a least-squares fit the same
	/// constraint five times biases it toward whichever surface happens to be cut up the most. But
	/// duplicates that DISAGREE about their offset are not duplicates at all: that is a face being
	/// moved while a coplanar neighbour stays put, which asks this vertex to be in two places. It is
	/// refused here, where the two faces can be named, rather than surfacing as an inexact solve.
	/// </summary>
	static (List<Vec3> Planes, List<float> Distances) Constraints( PolyMesh mesh, int vertex,
		List<int> incident, Vec3[] normals, Dictionary<int, float> target )
	{
		var planes = new List<Vec3>( 4 );
		var distances = new List<float>( 4 );

		foreach ( var fi in incident )
		{
			var normal = normals[fi];
			var t = target.TryGetValue( fi, out var wanted ) ? wanted : 0f;
			var duplicate = -1;

			for ( var i = 0; i < planes.Count; i++ )
			{
				if ( 1.0 - Vec3.Dot( planes[i], normal ) < PlaneOffset.DistinctTolerance )
				{
					duplicate = i;
					break;
				}
			}

			if ( duplicate < 0 )
			{
				planes.Add( normal );
				distances.Add( t );
				continue;
			}

			if ( MathF.Abs( distances[duplicate] - t ) > 1e-6f * MathF.Max( 1f, MathF.Abs( t ) ) )
			{
				throw new InvalidOperationException(
					"One of these faces sits in the same plane as a face that is staying put, so the "
					+ "corner between them would have to be in two places at once. Select the whole "
					+ "flat surface, not part of it." );
			}
		}

		return (planes, distances);
	}

	/// <summary>
	/// A face whose corners do not lie in one plane has no single normal, so "move it along its
	/// normal" has no meaning — and the answer that comes out of pretending otherwise is a
	/// plausible-looking mesh that is wrong by an amount nobody can predict.
	///
	/// Measured against the face's OWN SIZE rather than the model's, so the rule is the same on a
	/// rivet and on a wall.
	/// </summary>
	static void RefuseIfNotPlanar( PolyMesh mesh, int index, Vec3 normal )
	{
		var face = mesh.Faces[index];
		var centroid = mesh.FaceCentroid( face );
		var size = 0f;
		var worst = 0f;

		foreach ( var i in face.Indices )
		{
			var offset = mesh.Positions[i] - centroid;

			size = MathF.Max( size, offset.Length );
			worst = MathF.Max( worst, MathF.Abs( Vec3.Dot( normal, offset ) ) );
		}

		if ( size > 0f && worst > PlanarTolerance * size )
		{
			throw new InvalidOperationException(
				$"That face is not flat — its corners sit {worst:0.####} off a single plane, so it has "
				+ "no one direction to move along. Moving a curved surface is not this tool." );
		}
	}

	/// <summary>
	/// Refuse a move that pushed a face through itself.
	///
	/// A face that has TURNED OVER relative to where it started has been driven past its own
	/// neighbours — its normal reverses and its area passes through zero on the way. That is a local
	/// signature nothing can hide, it costs one normal per touched face, and it is the same check
	/// ShellOperation makes for the same reason. A face that collapsed to nothing is caught too: its
	/// normal is undefined by then, so the area is what says so.
	///
	/// Only faces that actually moved are examined. The rest of the part is untouched by
	/// construction and checking it would be checking the input.
	/// </summary>
	static void RefuseIfFolded( PolyMesh before, PolyMesh after, Vec3[] normals )
	{
		for ( var fi = 0; fi < after.Faces.Count; fi++ )
		{
			var face = after.Faces[fi];
			var stayed = true;

			foreach ( var i in face.Indices )
			{
				if ( (after.Positions[i] - before.Positions[i]).LengthSquared > 0f )
				{
					stayed = false;
					break;
				}
			}

			if ( stayed )
				continue;

			// A FACE THAT ARRIVED DEGENERATE IS NOT THIS MOVE'S FAULT. Its normal is already zero, so
			// every comparison against it reads as a fold and the move gets refused for damage it did
			// not do. Real meshes carry these: chamfering a single edge of a box leaves a zero-area
			// sliver on the side face it ran into, and pulling the top of that box is a perfectly
			// ordinary thing to want afterwards.
			if ( normals[fi].LengthSquared < 1e-12f )
				continue;

			var area = after.FaceArea( face );

			if ( area <= 1e-12f )
			{
				throw new InvalidOperationException(
					"That move collapses a face of the part to nothing. Move it a shorter way." );
			}

			if ( Vec3.Dot( after.FaceNormal( face ), normals[fi] ) <= 0f )
			{
				throw new InvalidOperationException(
					"That move pushes the part through itself — a face ends up facing the other way. "
					+ "Move it a shorter way, or move the faces around it too." );
			}
		}
	}
}
