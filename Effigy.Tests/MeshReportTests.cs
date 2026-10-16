using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Render;

namespace Effigy.Tests;

/// <summary>
/// The agent-visible mesh reports: `topology`, `section`, `flex` and multi-view `match`. Each is
/// checked against a mesh whose answer is known in closed form — a box, a subdivided cube, an
/// n-sided cylinder, a lathed capsule with rings at known heights — so the numbers are tested,
/// not just the wording.
/// </summary>
public static class MeshReportTests
{
	public static void Run( string outDir )
	{
		Report.Section( "mesh reports: section" );
		TestSectionBox();
		TestSectionCylinder();
		TestSectionCapsule();
		TestSectionHoleAndTwoLimbs();
		TestSectionScript();
		TestSectionAlongBone();

		Report.Section( "mesh reports: topology" );
		TestTopologyBox();
		TestTopologySubdividedCube();
		TestTopologyOpenAndNonManifold();
		TestTopologySymmetry();
		TestTopologyJointLoops();
		TestTopologyScript();

		Report.Section( "mesh reports: flex" );
		TestHingeAxis();
		TestFlexKernel();
		TestFlexScript( outDir );

		Report.Section( "mesh reports: match several views" );
		TestMatchViews( outDir );
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
	static bool Near( float a, float b, float tol ) => MathF.Abs( a - b ) <= tol;

	/// <summary>
	/// A surface of revolution about z: a ring of <paramref name="sides"/> vertices at each
	/// (z, radius), quads between rings; a radius of 0 at either end is a pole closed with a fan.
	/// </summary>
	internal static PolyMesh Lathe( IReadOnlyList<(float Z, float R)> rings, int sides )
	{
		var mesh = new PolyMesh();
		var start = new List<int>();

		foreach ( var (z, r) in rings )
		{
			start.Add( mesh.VertexCount );
			if ( r <= 0f ) { mesh.AddVertex( new Vec3( 0, 0, z ) ); continue; }
			for ( var i = 0; i < sides; i++ )
			{
				var a = i * MathF.Tau / sides;
				mesh.AddVertex( new Vec3( MathF.Cos( a ) * r, MathF.Sin( a ) * r, z ) );
			}
		}

		for ( var k = 0; k + 1 < rings.Count; k++ )
		{
			var lowPole = rings[k].R <= 0f;
			var highPole = rings[k + 1].R <= 0f;

			for ( var i = 0; i < sides; i++ )
			{
				var j = (i + 1) % sides;
				if ( lowPole ) mesh.AddFace( new[] { start[k], start[k + 1] + j, start[k + 1] + i } );
				else if ( highPole ) mesh.AddFace( new[] { start[k] + i, start[k] + j, start[k + 1] } );
				else mesh.AddFace( new[] { start[k] + i, start[k] + j, start[k + 1] + j, start[k + 1] + i } );
			}
		}

		return mesh;
	}

	/// <summary>An open tube of radius <paramref name="r"/> from z=0 to z=<paramref name="height"/>, a ring every <paramref name="step"/>.</summary>
	internal static PolyMesh Tube( float r, float height, float step, int sides ) =>
		Lathe( Enumerable.Range( 0, (int)MathF.Round( height / step ) + 1 ).Select( i => (i * step, r) ).ToList(), sides );

	/// <summary>A capsule: hemispheres of radius r, a cylinder of length len between them, z from 0.</summary>
	internal static PolyMesh CapsuleMesh( float r, float len, int sides, int capRings, float step )
	{
		var rings = new List<(float, float)>();
		for ( var i = 0; i <= capRings; i++ )
		{
			var a = -MathF.PI / 2 + i * (MathF.PI / 2) / capRings;
			rings.Add( (r + MathF.Sin( a ) * r, MathF.Cos( a ) * r) );
		}
		for ( var z = r + step; z < r + len - 1e-4f; z += step ) rings.Add( (z, r) );
		for ( var i = 0; i <= capRings; i++ )
		{
			var a = i * (MathF.PI / 2) / capRings;
			rings.Add( (r + len + MathF.Sin( a ) * r, MathF.Cos( a ) * r) );
		}
		rings[0] = (0f, 0f);
		rings[^1] = (2 * r + len, 0f);
		return Lathe( rings, sides );
	}

	static float PolygonArea( int n, float r ) => 0.5f * n * r * r * MathF.Sin( MathF.Tau / n );
	static float PolygonPerimeter( int n, float r ) => n * 2f * r * MathF.Sin( MathF.PI / n );

	static Skeleton TwoBones( float jointZ, float topZ, float lean = 0f )
	{
		var rig = new Skeleton();
		var upper = rig.AddBoneFromPoints( "Upper", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, jointZ ) );
		rig.AddBoneFromPoints( "Lower", upper, new Vec3( 0, 0, jointZ ), new Vec3( lean, 0, topZ ) );
		return rig;
	}

	// --- section -----------------------------------------------------------------------------------

	static void TestSectionBox()
	{
		var box = Primitives.Box( 10, 6, 4 );
		var mid = SectionReport.Cut( box, Vec3.Zero, new Vec3( 0, 0, 1 ) );
		Check( "box slice: one closed outline", mid.Loops.Count == 1 && mid.Loops[0].Closed, $"{mid.Loops.Count}" );
		Check( "box slice: width is y (6), depth is x (10)", Near( mid.Width, 6, 1e-3f ) && Near( mid.Depth, 10, 1e-3f ), mid.Describe() );
		Check( "box slice: perimeter 32, area 60", Near( mid.Perimeter, 32, 1e-3f ) && Near( mid.Area, 60, 1e-2f ), mid.Describe() );

		var top = SectionReport.Cut( box, new Vec3( 0, 0, 2 ), new Vec3( 0, 0, 1 ) );
		Check( "a slice exactly at the top face still reads the box, not a miss", top.Hit && Near( top.Area, 60, 0.01f ), top.Describe() );

		var miss = SectionReport.Cut( box, new Vec3( 0, 0, 9 ), new Vec3( 0, 0, 1 ) );
		Check( "a slice above the box misses", !miss.Hit && miss.Describe().Contains( "misses" ) );
	}

	static void TestSectionCylinder()
	{
		const int n = 24;
		var can = Primitives.Cylinder( 2f, 10f, n );
		var ring = SectionReport.Cut( can, new Vec3( 0, 0, 1.3f ), new Vec3( 0, 0, 1 ) );
		Check( "cylinder slice: the n-gon's exact area", ring.Loops.Count == 1 && Near( ring.Area, PolygonArea( n, 2f ), 1e-3f ), $"{ring.Area} vs {PolygonArea( n, 2f )}" );
		Check( "cylinder slice: the n-gon's exact perimeter", Near( ring.Perimeter, PolygonPerimeter( n, 2f ), 1e-3f ), $"{ring.Perimeter} vs {PolygonPerimeter( n, 2f )}" );

		// Straight down the axis, through the seam vertices at ±y — the case MeshSection alone loses.
		var lengthwise = SectionReport.Cut( can, Vec3.Zero, new Vec3( 1, 0, 0 ) );
		Check( "a cut through the cylinder's axis is one closed 4 × 10 rectangle", lengthwise.Loops.Count == 1 && lengthwise.Loops[0].Closed
			&& Near( lengthwise.Area, 40f, 0.01f ) && Near( lengthwise.Perimeter, 28f, 0.01f ), lengthwise.Describe() );
	}

	static void TestSectionCapsule()
	{
		const int n = 32;
		var capsule = CapsuleMesh( 3f, 8f, n, 6, 1f );
		var middle = SectionReport.Cut( capsule, new Vec3( 0, 0, 7f ), new Vec3( 0, 0, 1 ) );
		Check( "capsule middle: radius-3 n-gon area", Near( middle.Area, PolygonArea( n, 3f ), 1e-3f ), $"{middle.Area} vs {PolygonArea( n, 3f )}" );
		Check( "capsule middle: width and depth are the diameter", Near( middle.Width, 6f, 0.02f ) && Near( middle.Depth, 6f, 0.02f ), middle.Describe() );

		// A ring of the top cap sits at 30° of latitude: radius 3·cos 30°.
		var capZ = 3f + 8f + MathF.Sin( MathF.PI / 6 ) * 3f;
		var cap = SectionReport.Cut( capsule, new Vec3( 0, 0, capZ ), new Vec3( 0, 0, 1 ) );
		var capR = 3f * MathF.Cos( MathF.PI / 6 );
		// A cut exactly through a ring's vertices is moved a hundred-thousandth of the model's size
		// off them (SectionReport.OffVertices); on a sloped cap that is worth ~1e-4 of the area.
		Check( "capsule cap ring: the smaller circle's area", Near( cap.Area, PolygonArea( n, capR ), 1e-3f * PolygonArea( n, capR ) ), $"{cap.Area} vs {PolygonArea( n, capR )}" );
		Check( "areas follow the outline (cap smaller than middle)", cap.Area < middle.Area );
	}

	static void TestSectionHoleAndTwoLimbs()
	{
		const int n = 32;
		var tube = Primitives.Tube( 3f, 2f, 6f, n );
		var s = SectionReport.Cut( tube, new Vec3( 0, 0, 0.7f ), new Vec3( 0, 0, 1 ) );
		Check( "a hollow tube: an outline and a hole", s.Loops.Count == 2 && s.Loops.Count( l => l.IsHole ) == 1, s.Describe() );
		Check( "the hole comes off the area", Near( s.Area, PolygonArea( n, 3f ) - PolygonArea( n, 2f ), 1e-2f ), $"{s.Area}" );

		var left = Primitives.Cylinder( 1f, 10f, 16 );
		var right = Primitives.Cylinder( 1f, 10f, 16 );
		MeshTransform.Apply( left, Xform.Translate( new Vec3( 0, 3, 0 ) ) );
		MeshTransform.Apply( right, Xform.Translate( new Vec3( 0, -3, 0 ) ) );
		var legs = SectionReport.Cut( new[] { left, right }, new Vec3( 0, 0, 2 ), new Vec3( 0, 0, 1 ) );
		Check( "two legs cut at once: two outlines, kept apart", legs.Loops.Count == 2 && legs.Loops.All( l => !l.IsHole && Near( l.Area, PolygonArea( 16, 1f ), 1e-3f ) ), legs.Describe() );
		Check( "and the slice's width spans both", Near( legs.Width, 8f, 1e-3f ), $"{legs.Width}" );
	}

	static void TestSectionScript()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=Box shape=Box sizex=10 sizey=6 sizez=4 position=0,0,2\nsection Box z=1,3 x=0" );
		Check( "section script runs", r.Ok, r.ToString() );
		Check( "one line per slice, in the script's terms", r.Log.Count( l => l.StartsWith( "Box z=" ) ) == 2 && r.Log.Any( l => l.StartsWith( "Box x=0" ) ), r.ToString() );
		Check( "the slice reads width/perimeter/area", r.Log.Any( l => l.Contains( "width 6 (y)" ) && l.Contains( "perimeter 32" ) && l.Contains( "area 60" ) ), r.ToString() );

		var range = BuildScript.Run( studio, "section Box z=0.5..3.5:4" );
		Check( "a range z=a..b:n makes n slices", range.Ok && range.Log.Count( l => l.StartsWith( "Box z=" ) ) == 4, range.ToString() );

		var miss = BuildScript.Run( studio, "section Box z=50" );
		Check( "every slice missing fails, with the body's span", !miss.Ok && miss.Error.Contains( "No slice touches" ) && miss.Error.Contains( "z 0..4" ), miss.ToString() );

		var nowhere = BuildScript.Run( studio, "section Box" );
		Check( "no cut named fails with the usage", !nowhere.Ok && nowhere.Error.Contains( "z=" ), nowhere.ToString() );

		var noBone = BuildScript.Run( studio, "section Box along=Thigh" );
		Check( "along= without a rig fails and says so", !noBone.Ok && noBone.Error.Contains( "no bones" ), noBone.ToString() );

		var bad = BuildScript.Run( studio, "section Box z=1,banana" );
		Check( "a bad number fails, naming it", !bad.Ok && bad.Error.Contains( "banana" ), bad.ToString() );
	}

	static void TestSectionAlongBone()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=Leg shape=Cylinder radius=2 sizez=10 segments=24 position=0,0,5" );
		studio.Rig = TwoBones( 5f, 10f );
		var s = BuildScript.Run( studio, "section Leg along=Lower at=0.2,0.8" );
		Check( "section along a bone runs", r.Ok && s.Ok, s.ToString() );
		Check( "slices across the bone read the limb's circle", s.Log.Count( l => l.Contains( "along Lower" ) && l.Contains( $"area {SectionReport.N( PolygonArea( 24, 2f ) )}" ) ) == 2, s.ToString() );

		var unknown = BuildScript.Run( studio, "section Leg along=Tail" );
		Check( "an unknown bone fails and lists the bones", !unknown.Ok && unknown.Error.Contains( "Upper" ) && unknown.Error.Contains( "Lower" ), unknown.ToString() );
	}

	// --- topology ----------------------------------------------------------------------------------

	static void TestTopologyBox()
	{
		var t = TopologyReport.Measure( Primitives.Box( 2, 2, 2 ), "Box" );
		Check( "box: welded to 8 vertices, 6 quads, 12 edges", t.Vertices == 8 && t.Quads == 6 && t.Faces == 6 && t.Edges == 12, $"{t.Vertices} {t.Quads} {t.Edges}" );
		Check( "box: closed and manifold", t.BoundaryEdges == 0 && t.NonManifoldEdges == 0 );
		Check( "box: 8 three-edge poles, no others", t.Valence3 == 8 && t.Valence5 == 0 && t.Valence6Plus == 0 );
		Check( "box: even edges (CV 0), square quads", Near( t.EdgeCv, 0f, 1e-4f ) && Near( t.AspectMax, 1f, 1e-4f ) && Near( t.SkewMax, 0f, 0.01f ) );
		Check( "box: symmetric", Near( t.SymmetryHausdorff, 0f, 1e-4f ) );
		Check( "box: poles are named against the body's sides, in script point syntax", t.Poles.All( p => p.Where.StartsWith( "@Box." ) ), t.Poles[0].Where );
	}

	static void TestTopologySubdividedCube()
	{
		var cube = CatmullClark.Subdivide( Primitives.Box( 2, 2, 2 ), 1 );
		var t = TopologyReport.Measure( cube, "Cube" );
		Check( "subdivided cube: 26 vertices, 24 quads, 48 edges (V-E+F = 2)", t.Vertices == 26 && t.Quads == 24 && t.Edges == 48, $"{t.Vertices} {t.Quads} {t.Edges}" );
		Check( "subdivided cube: still 8 three-edge poles, the rest valence 4", t.Valence3 == 8 && t.Valence5 == 0 && t.Valence6Plus == 0, $"{t.Valence3}/{t.Valence5}/{t.Valence6Plus}" );
		Check( "subdivided cube: 100% quads", Near( t.QuadPercent, 100f, 1e-3f ) );

		var tris = Lathe( new List<(float, float)> { (0, 0), (1, 1), (2, 0) }, 6 );
		var tt = TopologyReport.Measure( tris, "Bicone" );
		Check( "a fan of triangles: 100% triangles, and two six-edge poles flagged", Near( tt.TriPercent, 100f, 1e-3f ) && tt.Valence6Plus == 2 && tt.Describe().Contains( "! poles" ), tt.Describe() );
	}

	static void TestTopologyOpenAndNonManifold()
	{
		var grid = Primitives.Plane( 4, 4, 4, 4 );
		var t = TopologyReport.Measure( grid, "Grid" );
		Check( "a 4×4 grid: 16 open edges round it, no interior poles", t.BoundaryEdges == 16 && t.PoleCount == 0, $"{t.BoundaryEdges} {t.PoleCount}" );

		var fin = new PolyMesh();
		var a = fin.AddVertex( new Vec3( 0, 0, 0 ) );
		var b = fin.AddVertex( new Vec3( 0, 0, 1 ) );
		foreach ( var dir in new[] { new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ), new Vec3( 0, 1, 0 ) } )
		{
			var c = fin.AddVertex( dir + new Vec3( 0, 0, 1 ) );
			var d = fin.AddVertex( dir );
			fin.AddFace( new[] { a, b, c, d } );
		}
		var f = TopologyReport.Measure( fin, "Fin" );
		Check( "three faces on one edge: one non-manifold edge, flagged", f.NonManifoldEdges == 1 && f.Describe().Contains( "! non-manifold" ), f.Describe() );

		var cyl = TopologyReport.Measure( Primitives.Cylinder( 1, 2, 8 ), "Can" );
		Check( "a cylinder's caps are n-gons, and flagged", cyl.Ngons == 2 && cyl.Describe().Contains( "! n-gons: 2" ), cyl.Describe() );
	}

	static void TestTopologySymmetry()
	{
		var box = Primitives.Box( 10, 10, 10 );
		MeshTransform.Apply( box, Xform.Translate( new Vec3( 0, 2, 0 ) ) );
		var t = TopologyReport.Measure( box, "Box" );
		Check( "a box 2 off centre: mirror Hausdorff is 4 (y -7 against the wall at -3)", Near( t.SymmetryHausdorff, 4f, 1e-3f ), $"{t.SymmetryHausdorff}" );
		Check( "and it is flagged against the 0.5% target", t.Describe().Contains( "! symmetry" ) );

		var side = Primitives.Box( 2, 2, 2 );
		MeshTransform.Apply( side, Xform.Translate( new Vec3( 0, 10, 0 ) ) );
		Check( "a body all on one side is not compared, and says why", float.IsNaN( TopologyReport.Measure( side, "Arm_L" ).SymmetryHausdorff )
			&& TopologyReport.Measure( side, "Arm_L" ).Describe().Contains( "does not straddle" ) );
	}

	static void TestTopologyJointLoops()
	{
		var rig = TwoBones( 5.5f, 10f );

		// Rings every 1 and radius ~2: ±1 radius round z=5.5 holds the rings at 4, 5, 6, 7.
		var sparse = TopologyReport.Measure( Tube( 2f, 10f, 1f, 12 ), "Arm", rig );
		var lower = sparse.Joints.FirstOrDefault( j => j.Bone == "Lower" );
		Check( "loops per joint: 4 rings within one radius of the elbow", lower?.Loops == 4, lower is null ? sparse.Describe() : $"{lower.Loops} sides {string.Join( ",", lower.PerSide )} r {lower.Radius}" );
		Check( "the radius is the limb's (≈ 2)", lower is not null && Near( lower.Radius, MathF.Sqrt( PolygonArea( 12, 2f ) / MathF.PI ), 1e-2f ), $"{lower?.Radius}" );

		// Rings every 0.5: 4.0, 4.5 … 7.0 — seven.
		var dense = TopologyReport.Measure( Tube( 2f, 10f, 0.5f, 12 ), "Arm", rig );
		Check( "denser rings, more loops: 7", dense.Joints.FirstOrDefault( j => j.Bone == "Lower" )?.Loops == 7, dense.Describe() );

		// Two rings either side of nothing: a long gap at the joint.
		var bare = Lathe( new List<(float, float)> { (0, 2), (2, 2), (9, 2), (10, 2) }, 12 );
		var b = TopologyReport.Measure( bare, "Arm", rig );
		Check( "no ring at the joint: 0 loops, flagged against the 3-loop target", b.Joints.FirstOrDefault( j => j.Bone == "Lower" )?.Loops == 0 && b.Describe().Contains( "! loops at Lower" ), b.Describe() );

		var elsewhere = Tube( 2f, 10f, 1f, 12 );
		MeshTransform.Apply( elsewhere, Xform.Translate( new Vec3( 30, 0, 0 ) ) );
		var away = TopologyReport.Measure( elsewhere, "Prop", rig );
		Check( "a body the bones are not in gets no joint lines, and says so", away.Joints.Count == 0 && away.JointNote.Contains( "no bone" ), away.Describe() );

		var cube = TopologyReport.Measure( Primitives.Box( 2, 2, 2 ), "Box", rig );
		Check( "with a rig, poles are named against the nearest bone", cube.Poles.All( p => p.Where.StartsWith( "@Upper" ) || p.Where.StartsWith( "@Lower" ) ), cube.Poles[0].Where );

		// A finely subdivided cube keeps its eight corner poles far apart in edge lengths; put a
		// joint on one of them.
		var fine = CatmullClark.Subdivide( Primitives.Box( 20, 20, 20 ), 3 );
		var corner = TopologyReport.Measure( fine, "Box" ).Poles[0].Position;
		var onJoint = new Skeleton();
		onJoint.AddBoneFromPoints( "Root", -1, new Vec3( 0, 0, -40 ), new Vec3( 0, 0, -30 ) );
		onJoint.AddBoneFromPoints( "Knee", 0, corner, corner + new Vec3( 0, 0, 5 ) );
		var flagged = TopologyReport.Measure( fine, "Box", onJoint );
		Check( "a pole sitting on a joint is flagged", flagged.Poles.Count( p => p.NearJoint ) == 1 && flagged.Describe().Contains( "! on the joint" ), flagged.Describe() );
	}

	static void TestTopologyScript()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=Box shape=Box sizex=2 sizey=2 sizez=2 position=0,0,1\nadd Primitive name=Can shape=Cylinder radius=1 sizez=2 segments=8 position=5,0,1\ntopology\ntopology Box poles=2" );
		Check( "topology script runs", r.Ok, r.ToString() );
		Check( "topology with no body reports every body", r.Log.Count( l => l.StartsWith( "Box: topology" ) ) == 2 && r.Log.Any( l => l.StartsWith( "Can: topology" ) ), r.ToString() );
		Check( "poles= limits the listed poles", r.Log.Any( l => l.Contains( "… 6 more" ) ), r.ToString() );

		var missing = BuildScript.Run( studio, "topology Nope" );
		Check( "an unknown body fails, listing the bodies", !missing.Ok && missing.Error.Contains( "Box" ) && missing.Error.Contains( "Can" ), missing.ToString() );

		var option = BuildScript.Run( studio, "topology Box loops=3" );
		Check( "an unknown option fails", !option.Ok && option.Error.Contains( "loops" ), option.ToString() );

		var empty = BuildScript.Run( new PartStudio(), "topology" );
		Check( "nothing to measure fails, not no-ops", !empty.Ok && empty.Error.Contains( "No body" ), empty.ToString() );
	}

	// --- flex --------------------------------------------------------------------------------------

	static void TestHingeAxis()
	{
		var bent = TwoBones( 5f, 10f, lean: 1f );
		var axis = FlexReport.HingeAxis( bent, 1 );
		Check( "a chain bent toward +x hinges about +y (folding further)", Near( axis.y, 1f, 1e-3f ), $"{axis.x} {axis.y} {axis.z}" );

		var straight = TwoBones( 5f, 10f );
		var s = FlexReport.HingeAxis( straight, 1 );
		Check( "a straight chain hinges across the bone", Near( Vec3.Dot( s, new Vec3( 0, 0, 1 ) ), 0f, 1e-3f ) && Near( s.Length, 1f, 1e-3f ) );
	}

	static void TestFlexKernel()
	{
		var rig = TwoBones( 5f, 10f );
		var arm = Lathe( Enumerable.Range( 0, 21 ).Select( i => (i * 0.5f, 1.5f) ).Prepend( (0f, 0f) ).Append( (10f, 0f) ).ToList(), 16 );
		var smooth = SkinBinder.SmoothWeights( arm, SkinBinder.BindRigid( arm, rig ), 6, 0.5f );
		var rigid = SkinBinder.BindRigid( arm, rig );
		var lower = new[] { 1 };

		var still = FlexReport.Run( arm, smooth, rig, lower, new[] { 0f }, name: "Arm" );
		Check( "flex 0°: volume kept exactly, nothing crosses", still.Bends.Count == 1 && Near( still.Bends[0].VolumeRatio, 1f, 1e-4f ) && still.Bends[0].NewCrossings == 0, still.ToString() );

		var bent = FlexReport.Run( arm, smooth, rig, lower, new[] { 90f, 140f }, name: "Arm" );
		var v90 = bent.Bends[0].VolumeRatio;
		var v140 = bent.Bends[1].VolumeRatio;
		Check( "a smooth bind loses volume when bent, more at 140° than 90°", v90 < 0.999f && v140 < v90 && v140 > 0f, bent.ToString() );

		var hard = FlexReport.Run( arm, rigid, rig, lower, new[] { 140f }, name: "Arm" );
		Check( "a rigid bind folded to 140° passes through itself", hard.Bends[0].NewCrossings > 0 && !hard.Ok && hard.ToString().Contains( "! Lower 140°" ), hard.ToString() );
		Check( "and says where, against a bone", hard.Bends[0].CrossingAt?.StartsWith( "@" ) == true, hard.Bends[0].CrossingAt );

		var outside = new Skeleton();
		outside.AddBoneFromPoints( "Root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 5 ) );
		outside.AddBoneFromPoints( "Far", 0, new Vec3( 40, 0, 5 ), new Vec3( 40, 0, 9 ) );
		var skipped = FlexReport.Run( arm, SkinBinder.BindRigid( arm, outside ), outside, new[] { 1 }, new[] { 90f }, name: "Arm" );
		Check( "a joint the skin does not wrap is skipped with the reason", skipped.Bends.Count == 0 && skipped.Skipped.Count == 1 && skipped.Skipped[0].Contains( "not inside" ), skipped.ToString() );
	}

	static void TestFlexScript( string outDir )
	{
		var dir = Path.Combine( outDir, "mesh-reports" );
		var studio = new PartStudio();
		var add = BuildScript.Run( studio, "add Primitive name=Arm shape=Cylinder radius=1.5 sizez=10 segments=16 position=0,0,5\nadd Subdivide bodies=Arm levels=2" );

		var noRig = BuildScript.Run( studio, "flex Arm" );
		Check( "flex with no rig fails and says how to get one", add.Ok && !noRig.Ok && noRig.Error.Contains( "no bones" ), $"{add} / {noRig}" );

		studio.Rig = TwoBones( 5f, 10f );
		var r = BuildScript.Run( studio, $"flex Arm angles=90 render={dir} views=2 size=96" );
		Check( "flex script runs on the studio's bind", r.Ok, r.ToString() );
		Check( "it bends the joint the skin spans (Lower), not the root", r.Log.Any( l => l.Contains( "Lower 90°" ) ) && !r.Log.Any( l => l.Contains( "Upper 90°" ) ), r.ToString() );
		Check( "and writes a labelled still per bend", r.Written.Count == 1 && File.Exists( r.Written[0] ), string.Join( ", ", r.Written ) );

		var unknown = BuildScript.Run( studio, "flex Arm bones=Wrist" );
		Check( "an unknown bone fails, listing the bones", !unknown.Ok && unknown.Error.Contains( "Lower" ), unknown.ToString() );

		var root = BuildScript.Run( studio, "flex Arm bones=Upper" );
		Check( "a root bone is refused: no joint to bend", !root.Ok && root.Error.Contains( "root" ), root.ToString() );

		var axis = BuildScript.Run( studio, "flex Arm bones=Lower axis=sideways" );
		Check( "a bad axis fails with the choices", !axis.Ok && axis.Error.Contains( "auto, x, y, z" ), axis.ToString() );

		var all = BuildScript.Run( studio, "flex all bones=Lower angles=45" );
		Check( "flex all bends the whole model", all.Ok && all.Log.Any( l => l.StartsWith( "model: flex" ) ), all.ToString() );
	}

	// --- match -------------------------------------------------------------------------------------

	static void TestMatchViews( string outDir )
	{
		var dir = Path.Combine( outDir, "mesh-reports" );
		Directory.CreateDirectory( dir );
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=Torso shape=Box sizex=6 sizey=12 sizez=20 position=0,0,10" );

		var plain = new RenderOptions { Wireframe = false, Labels = false, Grid = false, Axes = false };
		var front = Path.Combine( dir, "ref_front.png" );
		var side = Path.Combine( dir, "ref_side.png" );
		ModelRender.Named( studio.Bodies, "front", 256, plain, null ).SavePng( front );
		ModelRender.Named( studio.Bodies, "side", 256, plain, null ).SavePng( side );

		var m = BuildScript.Run( studio, $"match front={front} side={side} out={dir}" );
		Check( "match over two views runs", r.Ok && m.Ok, m.ToString() );
		Check( "each view reports, prefixed with its name", m.Log.Any( l => l.StartsWith( "front:" ) ) && m.Log.Any( l => l.StartsWith( "side:" ) ), m.ToString() );
		var combined = m.Log.FirstOrDefault( l => l.StartsWith( "combined:" ) );
		Check( "and one combined score, high against its own renders", combined is not null && float.Parse( combined.Split( ' ' )[2], System.Globalization.CultureInfo.InvariantCulture ) > 0.9f, combined );
		Check( "diffs are written per view", m.Written.Count == 2 && m.Written.All( File.Exists ) );

		var bad = BuildScript.Run( studio, $"match front={front} under={side}" );
		Check( "an unknown view fails, listing the views", !bad.Ok && bad.Error.Contains( "under" ) && bad.Error.Contains( "front" ), bad.ToString() );

		var missing = BuildScript.Run( studio, $"match front={Path.Combine( dir, "nope.png" )}" );
		Check( "a missing reference fails, naming the path", !missing.Ok && missing.Error.Contains( "nope.png" ), missing.ToString() );

		var single = BuildScript.Run( studio, $"match front {front}" );
		Check( "the one-view form is unchanged", single.Ok && !single.Log.Any( l => l.StartsWith( "combined:" ) ), single.ToString() );
	}
}
