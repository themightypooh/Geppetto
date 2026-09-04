using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Geometry the sketch does not own, projected into its plane so it can be seen and snapped to.
///
/// WHAT THIS IS FOR. A sketch on the face of an existing part is nearly always ABOUT that face —
/// a boss centred on it, a pocket set in from one of its corners, a rib running along one of its
/// edges. Until this existed the face went blank the moment the sketcher opened: the plane was
/// derived from it and then nothing about it was drawn or snappable, so lining a new rectangle up
/// with the edge directly underneath it was done by eye against the shaded solid. That is exactly
/// the kind of "close enough" that turns into a 0.03-unit sliver after an extrude.
///
/// EVERY OTHER CAD PACKAGE CALLS THIS PROJECTED REFERENCE GEOMETRY and makes you ask for it a
/// curve at a time — Onshape's Use tool, SolidWorks' Convert Entities. Effigy shows the whole
/// face's boundary automatically instead, because the face was CHOSEN as the plane a moment ago,
/// which is a much stronger statement of intent than clicking one edge of it.
///
/// IT IS NOT PART OF THE SKETCH. Nothing here is in Sketch.Points or Sketch.Curves, so it never
/// reaches ProfileFinder, never extrudes, and is never saved. It is rebuilt from the model every
/// time the sketch is opened, which is what keeps it honest when the face underneath changes —
/// the same reason FaceRef stores geometry rather than "Face6".
/// </summary>
public sealed class SketchReference
{
	/// <summary>Corners of the referenced geometry, in sketch-plane coordinates.</summary>
	public readonly List<Vec2> Points = new();

	/// <summary>Edges, as index pairs into <see cref="Points"/>.</summary>
	public readonly List<(int A, int B)> Edges = new();

	public bool IsEmpty => Points.Count == 0;

	/// <summary>An edge as the two positions it runs between. Bounds-checked to a zero-length
	/// segment rather than throwing: this is drawn and hit-tested every frame from an index that a
	/// rebuild underneath could have invalidated, and a viewport that throws once per frame is
	/// worse than one that briefly draws nothing.</summary>
	public (Vec2 A, Vec2 B) Segment( int index )
	{
		if ( index < 0 || index >= Edges.Count )
			return (Vec2.Zero, Vec2.Zero);

		var (a, b) = Edges[index];

		if ( a < 0 || a >= Points.Count || b < 0 || b >= Points.Count )
			return (Vec2.Zero, Vec2.Zero);

		return (Points[a], Points[b]);
	}

	/// <summary>
	/// Copy one reference edge into the sketch as a real line — Onshape's Use, one edge at a time.
	///
	/// WHY A COPY AND NOT A LIVE LINK. Onshape's projected curves stay attached to what they were
	/// taken from and move when it moves. That is the better behaviour and it is also a whole
	/// feature: the sketch would need a second class of curve that the user cannot drag or delete,
	/// that ProfileFinder unions in, and that is rebuilt rather than saved. A copy is what the tool
	/// does here, and it is honest about it — the line becomes ordinary sketch geometry, yours to
	/// trim and drag, and it does NOT follow the face afterwards.
	///
	/// Points are reused through SketchSnapper.PointIndex, so an edge copied in welds onto whatever
	/// is already at its ends rather than laying a second point on top of the first. That is what
	/// makes "use all four edges, then draw a line across" close into two regions instead of into
	/// nothing at all.
	/// </summary>
	/// <returns>The line added, or null when the edge is degenerate or the sketch already has it.</returns>
	public SketchLine UseEdge( Sketch sketch, int edgeIndex )
	{
		if ( sketch is null || edgeIndex < 0 || edgeIndex >= Edges.Count )
			return null;

		var (a, b) = Segment( edgeIndex );

		var start = SketchSnapper.PointIndex( sketch, a );
		var end = SketchSnapper.PointIndex( sketch, b );

		// A zero-length line is not geometry - ProfileFinder links it into the adjacency map twice
		// at one point and calls the sketch branching. The line tool has the same guard.
		if ( start == end )
			return null;

		// USING THE SAME EDGE TWICE MUST NOT LAY A SECOND LINE ON THE FIRST. Two curves between the
		// same pair of points is exactly the branching case ProfileFinder refuses, so clicking an
		// edge you already used would quietly destroy the profile you were building.
		foreach ( var curve in sketch.Curves )
		{
			var (from, to) = curve.Endpoints;

			if ( (from == start && to == end) || (from == end && to == start) )
				return null;
		}

		return sketch.Add( new SketchLine( start, end ) );
	}

	/// <summary>Copy every reference edge in, which is the common case: the whole face outline, so a
	/// single line drawn across it closes two regions. Returns how many were added — edges already
	/// in the sketch are skipped, so running it twice is harmless.</summary>
	public int UseAll( Sketch sketch )
	{
		var added = 0;

		for ( var i = 0; i < Edges.Count; i++ )
		{
			if ( UseEdge( sketch, i ) is not null )
				added++;
		}

		return added;
	}

	/// <summary>
	/// The boundary of the face a sketch is attached to, in that sketch's plane.
	///
	/// THE BOUNDARY OF THE SURFACE, NOT OF ONE n-GON. A face that has been through a boolean is
	/// usually several faces sharing a plane, and outlining each of them separately draws the seams
	/// where they were split — lines that are not edges of anything, sitting in the middle of what
	/// looks like one flat surface, and snapping to them is snapping to an artefact of how the mesh
	/// happens to be cut up. FaceSurface is what decides where the surface stops, and it is the
	/// same answer the viewport highlights and the edge picker offers: three things that used to
	/// each work it out for themselves and disagree on screen at the same time.
	///
	/// PROJECTED, NOT INTERSECTED, so a sketch with an offset still gets the face's outline —
	/// directly below where it will be drawn, which is what makes the offset useful for a boss
	/// standing clear of the surface it grows from.
	/// </summary>
	public static SketchReference FromFace( IEnumerable<Body> bodies, FaceRef reference, SketchPlane plane )
	{
		var result = new SketchReference();

		if ( plane is null || !FacePlane.TryResolveFace( bodies, reference, out var body, out var faceIndex ) )
			return result;

		var mesh = body.Mesh;

		// Scaled to the part, for the same reason every other tolerance in the sketcher is: a
		// constant that is generous on a 100-unit block silently merges every vertex of a 0.1-unit
		// one. See SketchSnapper's header for what fixed tolerances did to this sketcher.
		var tolerance = MathF.Max( mesh.BoundsDiagonal * 1e-4f, 1e-5f );

		var surface = FaceSurface.FromFace( mesh, faceIndex );
		var mapped = new Dictionary<int, int>();

		foreach ( var (from, to) in surface.Boundary )
		{
			var a = Map( result, mapped, mesh, plane, tolerance, from );
			var b = Map( result, mapped, mesh, plane, tolerance, to );

			if ( a != b )
				result.Edges.Add( (a, b) );
		}

		return result;
	}

	/// <summary>Mesh vertex to reference point, projected and de-duplicated. Two mesh vertices at
	/// the same position — which a boolean leaves behind routinely — must become ONE snap target,
	/// or the corner of the face has two dots on it and the cursor picks between them at
	/// random.</summary>
	static int Map( SketchReference result, Dictionary<int, int> mapped, PolyMesh mesh,
		SketchPlane plane, float tolerance, int vertex )
	{
		if ( mapped.TryGetValue( vertex, out var existing ) )
			return existing;

		var p = plane.ToPlane( mesh.Positions[vertex] );

		for ( var i = 0; i < result.Points.Count; i++ )
		{
			if ( (result.Points[i] - p).LengthSquared > tolerance * tolerance )
				continue;

			mapped[vertex] = i;
			return i;
		}

		result.Points.Add( p );
		mapped[vertex] = result.Points.Count - 1;

		return result.Points.Count - 1;
	}
}
