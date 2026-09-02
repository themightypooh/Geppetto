using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Dropping a material on the SIDE of a cylinder, several times, on different faces.
///
/// WHY THIS IS ITS OWN FILE. MaterialDropTests works on a box, and a box is the easy case in a way
/// that hides the whole problem: its six faces have six different normals, so a FaceRef captured on
/// one of them cannot be confused with any other. A cylinder's side is sixteen quads that differ
/// only by a rotation about the axis — same size, same shape, same distance from the origin, and
/// their planes are a few degrees apart. That is precisely where resolving a stored reference back
/// to "the face it was captured on" can land on the wrong one, and every material assignment in the
/// tool survives a rebuild by doing exactly that.
///
/// The symptom this was written for: paint one face of a cylinder, then paint another, and only the
/// first one is ever coloured — every later drop appears to do nothing, or to keep repainting the
/// same face. The editor's raycast is not involved; these tests hand the kernel the exact face
/// index a raycast would have produced.
/// </summary>
public static class MaterialDropCurvedTests
{
	public static void Run()
	{
		Report.Section( "curved drop: two faces of a cylinder keep their own materials" );
		TestTwoSideFaces();

		Report.Section( "curved drop: every face of the side can be painted" );
		TestWholeSide();

		Report.Section( "curved drop: a reference resolves to the face it was captured on" );
		TestReferenceResolution();
	}

	/// <summary>The minimal reproduction: paint two neighbouring side quads, rebuild, expect two.
	/// </summary>
	static void TestTwoSideFaces()
	{
		var studio = Cylinder( out var body );

		var sides = SideFaces( body ).ToList();

		Report.Check( "the cylinder has a many-quad side to test against", sides.Count >= 8, $"{sides.Count} side faces" );

		Drop( studio, body, sides[0], "materials/a.vmat", out var slot );

		studio.Rebuild();
		body = studio.Bodies.Single();

		Drop( studio, body, SideFaces( body ).ToList()[1], "materials/a.vmat", out _ );

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		var painted = studio.Bodies.Single().Mesh.Faces.Count( f => f.Material == slot );

		Report.Check( "both faces are on the slot, not just the first",
			painted == 2, $"{painted} faces painted" );
	}

	/// <summary>
	/// Every side quad in turn. The two-face case can pass by luck — neighbouring quads resolving to
	/// each other cancels out when there are only two of them — and this cannot: sixteen drops must
	/// produce sixteen painted faces and no more.
	/// </summary>
	static void TestWholeSide()
	{
		var studio = Cylinder( out var body );
		var expected = SideFaces( body ).Count();

		for ( var i = 0; i < expected; i++ )
		{
			// Re-resolved every time, because each rebuild remakes the bodies and the indices have
			// to come from the mesh the drop is actually acting on — the same thing the editor does
			// by raycasting afresh.
			body = studio.Bodies.Single();

			var sides = SideFaces( body ).ToList();

			if ( i >= sides.Count )
				break;

			Drop( studio, body, sides[i], "materials/a.vmat", out _ );
			studio.Rebuild();
		}

		var mesh = studio.Bodies.Single().Mesh;
		var painted = mesh.Faces.Count( f => f.Material > 0 );

		Report.Check( $"all {expected} side faces are painted", painted == expected, $"{painted} of {expected}" );

		Report.Check( "and the caps were not caught up in it",
			mesh.Faces.Where( f => f.Material > 0 ).All( f => MathF.Abs( mesh.FaceNormal( f ).z ) < 0.5f ) );
	}

	/// <summary>
	/// The layer underneath, isolated: capture a reference on each side face, then ask what each one
	/// resolves to. If two of them answer with the same face, no amount of care in MaterialDrop can
	/// paint them separately — which is the difference between a bug in the drop and a bug in the
	/// reference.
	/// </summary>
	static void TestReferenceResolution()
	{
		var studio = Cylinder( out var body );
		var sides = SideFaces( body ).ToList();

		var resolved = new List<int>();
		var lost = 0;

		foreach ( var index in sides )
		{
			var reference = Capture( body, index );

			if ( FacePlane.TryResolveFace( studio.Bodies, reference, out var found, out var back ) && found.Id == body.Id )
				resolved.Add( back );
			else
				lost++;
		}

		Report.Check( "every captured side face resolves to something", lost == 0, $"{lost} lost" );

		Report.Check( "each one resolves back to the face it was captured on",
			resolved.SequenceEqual( sides ),
			$"captured {string.Join( ",", sides )} resolved {string.Join( ",", resolved )}" );

		Report.Check( "and no two captures resolve to the same face",
			resolved.Distinct().Count() == resolved.Count );
	}

	// --- helpers ----------------------------------------------------------------------------------

	static bool Drop( PartStudio studio, Body body, int faceIndex, string material, out int slot ) =>
		MaterialDrop.Drop( studio, body.Id, faceIndex, Capture( body, faceIndex ), material, out slot );

	/// <summary>A reference captured at the face's own centroid — where a raycast through the middle
	/// of a face you were pointing at would have landed.</summary>
	static FaceRef Capture( Body body, int faceIndex ) =>
		FacePlane.Capture( body, faceIndex, body.Mesh.FaceCentroid( body.Mesh.Faces[faceIndex] ) );

	/// <summary>The faces around the barrel, cap faces excluded — those have the axis for a normal
	/// and are trivially distinguishable, which is not what is being tested.</summary>
	static IEnumerable<int> SideFaces( Body body )
	{
		var mesh = body.Mesh;

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( MathF.Abs( mesh.FaceNormal( mesh.Faces[i] ).z ) < 0.5f )
				yield return i;
		}
	}

	static PartStudio Cylinder( out Body body )
	{
		var studio = new PartStudio();

		var cylinder = studio.Add( new PrimitiveFeature() );
		cylinder.Shape.Index = 1; // Cylinder
		cylinder.Radius.Value = 2f;
		cylinder.SizeZ.Value = 4f;
		cylinder.Segments.Value = 16;

		studio.Rebuild();
		body = studio.Bodies.Single();

		return studio;
	}
}
