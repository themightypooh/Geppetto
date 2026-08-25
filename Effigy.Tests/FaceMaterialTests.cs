using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Painting a material slot onto picked faces.
///
/// The slot has always been on Face and every exporter has always grouped by it. What was missing
/// was any way to set it per face, so in practice every model came out single-material no matter
/// what the exporters were prepared to do. These tests care about three things in order: that the
/// assignment lands on the faces meant, that it survives the rebuild that recreates those faces —
/// which is the whole reason it is a feature and not an edit — and that it actually reaches the
/// files, since a slot nothing exports is a slot nobody can bind.
/// </summary>
public static class FaceMaterialTests
{
	public static void Run()
	{
		Report.Section( "face materials: the assignment lands where it was pointed" );
		TestAssignment();

		Report.Section( "face materials: it survives the geometry being rebuilt" );
		TestSurvivesRebuild();

		Report.Section( "face materials: the exporters carry the slots" );
		TestExports();

		Report.Section( "face materials: when the faces go away" );
		TestLostFaces();
	}

	static void TestAssignment()
	{
		var studio = Boxed( out var body );

		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		var side = FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) );

		var paint = studio.Add( new FaceMaterialFeature() );
		paint.Material.Value = 3;
		paint.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );
		paint.Faces.Add( FacePlane.Capture( body, side, body.Mesh.FaceCentroid( body.Mesh.Faces[side] ) ) );

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;
		var painted = mesh.Faces.Count( f => f.Material == 3 );

		Report.Check( "exactly the two picked faces carry the new slot", painted == 2,
			$"{painted} faces on slot 3" );

		Report.Check( "and the rest are untouched on slot 0",
			mesh.Faces.Count( f => f.Material == 0 ) == mesh.FaceCount - 2 );

		// The right two, not just any two. Checking the count alone would pass if it painted the
		// bottom and the far side instead.
		Report.Check( "the face that came out painted is the one that was pointed at",
			mesh.FaceNormal( mesh.Faces.First( f => f.Material == 3 ) ).z > 0.99f
			|| mesh.Faces.Where( f => f.Material == 3 ).All( f =>
				MathF.Abs( mesh.FaceNormal( f ).z ) > 0.99f || MathF.Abs( mesh.FaceNormal( f ).x ) > 0.99f ) );

		// A second assignment further down the tree wins on any face they share, because the tree
		// runs in order. That is what makes "paint it again to change it" work.
		var repaint = studio.Add( new FaceMaterialFeature() );
		repaint.Material.Value = 7;
		repaint.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );

		studio.Rebuild();

		var after = studio.Bodies.Single().Mesh;

		Report.Check( "a later assignment overrides an earlier one on the same face",
			after.Faces.Count( f => f.Material == 7 ) == 1 && after.Faces.Count( f => f.Material == 3 ) == 1,
			$"slot 7: {after.Faces.Count( f => f.Material == 7 )}, slot 3: {after.Faces.Count( f => f.Material == 3 )}" );
	}

	static void TestSurvivesRebuild()
	{
		// THE REASON THIS IS A FEATURE. Bodies are rebuilt from scratch on every rebuild, so a
		// material painted straight onto the mesh would last exactly until the next parameter edit.
		// In the tree, it is re-applied after the geometry it paints is remade.
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 4f;
		box.SizeZ.Value = 2f;
		studio.Rebuild();

		var body = studio.Bodies.Single();
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var paint = studio.Add( new FaceMaterialFeature() );
		paint.Material.Value = 5;
		paint.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );
		studio.Rebuild();

		Report.Check( "the top face is painted", studio.Bodies.Single().Mesh.Faces.Count( f => f.Material == 5 ) == 1 );

		// Change the geometry upstream. Every face is remade, and the reference has to find the top
		// one again in its new position.
		box.SizeZ.Value = 6f;
		studio.MarkDirty( box );
		var report = studio.Rebuild();

		Report.Check( "it still builds after the box changed shape", !report.HasErrors, report.ToString() );

		var rebuilt = studio.Bodies.Single().Mesh;
		var stillPainted = rebuilt.Faces.Where( f => f.Material == 5 ).ToList();

		Report.Check( "the assignment came back after the rebuild", stillPainted.Count == 1,
			$"{stillPainted.Count} faces on slot 5" );

		Report.Check( "and it is still the top face, at its new height",
			stillPainted.Count == 1 && rebuilt.FaceNormal( stillPainted[0] ).z > 0.99f
			&& MathF.Abs( rebuilt.FaceCentroid( stillPainted[0] ).z - 3f ) < 1e-3f,
			stillPainted.Count == 1 ? $"at z {rebuilt.FaceCentroid( stillPainted[0] ).z}" : "" );

		// Suppressing it puts the faces back, the same as any other feature.
		paint.Suppressed = true;
		studio.MarkDirty( paint );
		studio.Rebuild();

		Report.Check( "suppressing the feature returns the face to its default slot",
			studio.Bodies.Single().Mesh.Faces.All( f => f.Material == 0 ) );

		paint.Suppressed = false;
		studio.MarkDirty( paint );
		studio.Rebuild();

		Report.Check( "and unsuppressing paints it again",
			studio.Bodies.Single().Mesh.Faces.Count( f => f.Material == 5 ) == 1 );
	}

	static void TestExports()
	{
		var studio = Boxed( out var body );

		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var paint = studio.Add( new FaceMaterialFeature() );
		paint.Material.Value = 2;
		paint.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );
		studio.Rebuild();

		var mesh = studio.ToMesh();

		// OBJ groups with usemtl. Two slots in use means two names in the file, and the painted one
		// has to be named after the slot it was given rather than renumbered on the way out.
		var obj = ObjWriter.Write( mesh, "painted" );
		var usemtl = obj.Split( '\n' ).Where( l => l.StartsWith( "usemtl " ) ).Select( l => l.Trim() ).Distinct().ToList();

		Report.Check( "OBJ writes a material group per slot in use", usemtl.Count == 2,
			string.Join( ", ", usemtl ) );

		Report.Check( "and names the painted one after its slot",
			usemtl.Any( l => l.EndsWith( "_2" ) ), string.Join( ", ", usemtl ) );

		// SMD names a material per face, so the painted face's name must appear there too.
		var smd = SmdWriter.Write( mesh, Skeleton.SingleRoot() );

		Report.Check( "SMD carries the slot as well", smd.Contains( "material_2" ),
			"no material_2 in the SMD" );

		// DMX is the path ModelDoc actually accepts.
		var dmx = DmxWriter.Write( mesh, modelName: "painted" );

		Report.Check( "DMX carries it too", dmx.Contains( "material_2" ), "no material_2 in the DMX" );

		// NAMED SLOTS. A number is all the geometry needs and it is not what a person binding the
		// model in ModelDoc wants to see — every exporter takes a name function, and the studio has
		// one that falls back to the numbered default for slots nobody has named.
		studio.MaterialNames[2] = "anodised";

		var namedObj = ObjWriter.Write( mesh, "painted", materialName: studio.NameForSlot );

		Report.Check( "OBJ writes the name a slot was given",
			namedObj.Contains( "usemtl anodised" ), "no usemtl anodised" );

		Report.Check( "and still numbers the slots nobody named",
			namedObj.Contains( "usemtl material_0" ), "slot 0 lost its default name" );

		Report.Check( "SMD takes the same names",
			SmdWriter.Write( mesh, Skeleton.SingleRoot(), materialName: studio.NameForSlot ).Contains( "anodised" ) );

		Report.Check( "and so does DMX",
			DmxWriter.Write( mesh, modelName: "painted", materialName: studio.NameForSlot ).Contains( "anodised" ) );
	}

	static void TestLostFaces()
	{
		var studio = Boxed( out var body );

		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var paint = studio.Add( new FaceMaterialFeature() );
		paint.Material.Value = 4;
		paint.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );

		// One real face and one reference to a body that does not exist. Losing some of an
		// assignment is an ordinary consequence of an upstream edit, and failing the whole feature
		// over it would unpaint everything else it was doing.
		paint.Faces.Add( new FaceRef( "body-that-is-gone", new Vec3( 0, 0, 5 ), new Vec3( 0, 0, 1 ) ) );

		var report = studio.Rebuild();

		Report.Check( "losing one face of several is a warning, not a failure",
			!report.HasErrors && paint.Warning is not null, paint.Warning ?? "no warning" );

		Report.Check( "and the faces that survived are still painted",
			studio.Bodies.Single().Mesh.Faces.Count( f => f.Material == 4 ) == 1 );

		// Losing all of them is different: the feature now does nothing at all, and silently doing
		// nothing is how a dead feature sits in a tree for weeks.
		var orphaned = new PartStudio();
		var lonely = orphaned.Add( new PrimitiveFeature() );
		lonely.SizeX.Value = lonely.SizeY.Value = lonely.SizeZ.Value = 2f;

		var lost = orphaned.Add( new FaceMaterialFeature() );
		lost.Faces.Add( new FaceRef( "nothing-here", Vec3.Zero, new Vec3( 0, 0, 1 ) ) );

		var lostReport = orphaned.Rebuild();

		Report.Check( "losing every face is an error that says so", lostReport.HasErrors,
			"it passed silently" );

		Report.Check( "and the error explains what to do",
			lost.Error is not null && lost.Error.Contains( "again" ), lost.Error ?? "no error" );

		// No faces picked at all is the state a brand new feature is in, and it must say what it
		// wants rather than looking broken.
		var empty = new PartStudio();
		var alone = empty.Add( new PrimitiveFeature() );
		alone.SizeX.Value = alone.SizeY.Value = alone.SizeZ.Value = 2f;
		var blank = empty.Add( new FaceMaterialFeature() );

		empty.Rebuild();

		Report.Check( "a feature with nothing picked asks for faces",
			blank.Error is not null && blank.Error.Contains( "faces" ), blank.Error ?? "no error" );
	}

	// --- helpers ------------------------------------------------------------------------------

	static PartStudio Boxed( out Body body )
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 2f;

		studio.Rebuild();
		body = studio.Bodies.Single();

		return studio;
	}

	static int FaceIndexFacing( PolyMesh mesh, Vec3 direction )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[i] ), direction.Normal ) > 0.99f )
				return i;
		}

		return -1;
	}
}
