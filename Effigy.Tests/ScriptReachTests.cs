using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// What a build script can reach that used to be editor-only: edit-mode operations as replayable
/// EditOp features picked by rule, a boolean that runs without s&amp;box, and export with LODs,
/// collision, skinning and FBX. Each verb is checked the way an agent would rely on it - it does
/// the thing, it replays after an upstream change, and it refuses out loud when it can't.
/// </summary>
public static class ScriptReachTests
{
	public static void Run()
	{
		Section( "script reach: edit ops picked by rule" );
		TestExtrudeReplays();
		TestEmptySelectionFails();
		TestInset();
		TestBevel();
		TestLoopCut();
		TestDeleteAndBridge();
		TestSymmetrize();
		TestDissolve();
		TestSelectorRules();
		TestEditOpSurvivesSaveAndLoad();

		Section( "script reach: a boolean with no engine" );
		TestVoxelBooleans();
		TestVoxelBooleanRefusals();

		Section( "script reach: export with LODs, collision, skinning and FBX" );
		TestExportPlainIsUnchanged();
		TestExportLods();
		TestExportCollision();
		TestExportFbx();
		TestExportSkinned();
		TestExportRefusals();
	}

	const string Box = "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=0,0,5\n";

	static (PartStudio Studio, BuildScript.Result Result) Script( string text )
	{
		var studio = new PartStudio();
		return (studio, BuildScript.Run( studio, text ));
	}

	static PolyMesh Mesh( PartStudio studio, string name = "Box" ) => ModelMeasure.FindBody( studio, name )?.Mesh;

	static int BoundaryEdges( PolyMesh m ) => m.BuildEdgeFaces().Values.Count( f => f.Count == 1 );

	static void TestExtrudeReplays()
	{
		var (studio, r) = Script( Box + "edit Box extrude select=normal:up distance=4" );
		var m = Mesh( studio );
		Check( "edit extrude runs", r.Ok, r.ToString() );
		Check( "extrude pulls the top up by the distance", m is not null && MathF.Abs( ModelMeasure.Bounds( m ).Max.z - 14f ) < 1e-3f, m is null ? "no body" : $"{ModelMeasure.Bounds( m ).Max.z}" );
		Check( "and adds the four walls", m?.FaceCount == 10, $"{m?.FaceCount} faces" );
		Check( "and stays closed", m is not null && BoundaryEdges( m ) == 0 );

		var again = BuildScript.Run( studio, "set Box sizez=20" );
		m = Mesh( studio );
		Check( "the extrude replays after the box upstream changes", again.Ok && MathF.Abs( ModelMeasure.Bounds( m ).Max.z - 19f ) < 1e-3f, $"{again} max z {ModelMeasure.Bounds( m ).Max.z}" );

		var (s2, r2) = Script( Box + "add EditOp bodies=Box op=Extrude_individual select=normal:up|normal:front distance=2" );
		var m2 = Mesh( s2 );
		Check( "add EditOp works too, with alternatives", r2.Ok && m2.FaceCount == 14, $"{r2} {m2?.FaceCount}" );
	}

	static void TestEmptySelectionFails()
	{
		var (_, r) = Script( Box + "edit Box extrude select=normal:up&z:..1 distance=4" );
		Check( "a rule that picks nothing fails, not no-ops", !r.Ok && r.Error.Contains( "picks no face" ), r.ToString() );
		Check( "and says which term picked what", r.Error.Contains( "alone picks" ), r.Error );

		var (_, bad) = Script( Box + "edit Box extrude select=sideways distance=4" );
		Check( "a rule that can't be read fails with the grammar", !bad.Ok && bad.Error.Contains( "Rules:" ), bad.ToString() );

		var (_, op) = Script( Box + "edit Box frobnicate" );
		Check( "an unknown operation lists the real ones", !op.Ok && op.Error.Contains( "loopcut" ), op.ToString() );

		var (_, zero) = Script( Box + "edit Box inset select=normal:up distance=0" );
		Check( "an inset of 0 fails with why", !zero.Ok && zero.Error.Contains( "more than 0" ), zero.ToString() );

		var (_, closed) = Script( Box + "edit Box bridge select=all" );
		Check( "bridge on a closed body says it has no rims", !closed.Ok && closed.Error.Contains( "closed" ), closed.ToString() );
	}

	static void TestInset()
	{
		var (studio, r) = Script( Box + "edit Box inset select=normal:up distance=2" );
		var m = Mesh( studio );
		Check( "edit inset runs", r.Ok, r.ToString() );
		Check( "inset rings the top with four faces", m?.FaceCount == 10, $"{m?.FaceCount}" );

		var top = Enumerable.Range( 0, m.FaceCount ).Where( f => m.FaceNormal( m.Faces[f] ).z > 0.99f ).Select( f => m.FaceArea( m.Faces[f] ) ).OrderBy( a => a ).ToList();
		Check( "and the inner face is 6 x 6", top.Count == 5 && MathF.Abs( top.Max() - 36f ) < 0.1f, string.Join( ",", top ) );

		var (_, tooFar) = Script( Box + "edit Box inset select=normal:up distance=6" );
		Check( "an inset past the middle is refused with the most that fits", !tooFar.Ok && tooFar.Error.Contains( "most that fits" ), tooFar.ToString() );
	}

	static void TestBevel()
	{
		var (studio, r) = Script( Box + "edit Box bevel select=normal:up distance=1" );
		var m = Mesh( studio );
		Check( "edit bevel of the top's rim runs", r.Ok, r.ToString() );
		Check( "it chamfers the four rim edges", m is not null && m.FaceCount >= 10 && BoundaryEdges( m ) == 0, $"{m?.FaceCount} faces" );
		Check( "and no vertex is left at the old corner", m is not null && !m.Positions.Any( p => (p - new Vec3( 5, 5, 10 )).Length < 1e-3f ) );

		var (s2, r2) = Script( Box + "edit Box bevel element=edges select=sharp:30 distance=1 segments=3" );
		var m2 = Mesh( s2 );
		Check( "a fillet of every sharp edge", r2.Ok && m2.FaceCount > 26 && BoundaryEdges( m2 ) == 0, $"{r2} {m2?.FaceCount}" );
	}

	static void TestLoopCut()
	{
		var (studio, r) = Script( Box + "edit Box loopcut at=0,0,7 axis=z" );
		var m = Mesh( studio );
		Check( "edit loopcut runs", r.Ok, r.ToString() );
		Check( "it splits the four sides", m?.FaceCount == 10, $"{m?.FaceCount}" );
		Check( "at the height asked", m is not null && m.Positions.Count( p => MathF.Abs( p.z - 7f ) < 1e-3f ) == 4 );

		var (s2, r2) = Script( Box + "edit Box loopcut at=@Box.top-0,0,2 axis=z" );
		Check( "at= takes a landmark", r2.Ok && Mesh( s2 ).Positions.Count( p => MathF.Abs( p.z - 8f ) < 1e-3f ) == 4, r2.ToString() );

		var (_, miss) = Script( Box + "edit Box loopcut at=0,0,30 axis=z" );
		Check( "a point outside the body's span fails with the span", !miss.Ok && miss.Error.Contains( "crosses" ) && miss.Error.Contains( "runs" ), miss.ToString() );
	}

	static void TestDeleteAndBridge()
	{
		var (studio, r) = Script( Box
			+ "edit Box loopcut at=0,0,3 axis=z\n"
			+ "edit Box loopcut at=0,0,7 axis=z\n"
			+ "edit Box delete select=z:3..7&!normal:up&!normal:down\n" );
		var m = Mesh( studio );
		Check( "edit delete takes out the middle band", r.Ok && m.FaceCount == 10 && BoundaryEdges( m ) == 8, $"{r} {m?.FaceCount} faces, {BoundaryEdges( m )} rim edges" );

		var b = BuildScript.Run( studio, "edit Box bridge select=boundary" );
		m = Mesh( studio );
		Check( "edit bridge joins the two rims", b.Ok && m.FaceCount == 14 && BoundaryEdges( m ) == 0, $"{b} {m.FaceCount} faces, {BoundaryEdges( m )} rim edges" );
		Check( "and the bridged box faces out", m.SignedVolume() > 0f, $"{m.SignedVolume()}" );

		var (_, all) = Script( Box + "edit Box delete select=all" );
		Check( "deleting every face is refused", !all.Ok && all.Error.Contains( "every face" ), all.ToString() );
	}

	static void TestSymmetrize()
	{
		var (studio, r) = Script( "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=2,0,5\nedit Box symmetrize axis=x" );
		var (min, max) = ModelMeasure.Bounds( Mesh( studio ) );
		Check( "edit symmetrize mirrors +x onto -x", r.Ok && MathF.Abs( min.x + 7f ) < 1e-3f && MathF.Abs( max.x - 7f ) < 1e-3f, $"{r} x {min.x}..{max.x}" );

		var (s2, r2) = Script( "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=0,0,2\nedit Box symmetrize axis=z side=negative" );
		var m2 = Mesh( s2 );
		var (min2, max2) = ModelMeasure.Bounds( m2 );
		Check( "symmetrize on z keeping the - half", r2.Ok && MathF.Abs( min2.z + 3f ) < 1e-3f && MathF.Abs( max2.z - 3f ) < 1e-3f, $"{r2} z {min2.z}..{max2.z}" );
		Check( "and does not turn the mesh inside out", m2.SignedVolume() > 0f && BoundaryEdges( m2 ) == 0, $"{m2.SignedVolume()}" );

		var (s3, r3) = Script( "add Primitive name=Box shape=Box sizex=10 sizey=10 sizez=10 position=0,3,5\nedit Box symmetrize axis=y" );
		var (min3, max3) = ModelMeasure.Bounds( Mesh( s3 ) );
		Check( "symmetrize on y keeping the + half", r3.Ok && MathF.Abs( min3.y + 8f ) < 1e-3f && MathF.Abs( max3.y - 8f ) < 1e-3f && MathF.Abs( max3.z - 10f ) < 1e-3f, $"{r3} y {min3.y}..{max3.y}" );
	}

	static void TestDissolve()
	{
		var (studio, r) = Script( Box + "edit Box loopcut at=0,0,5 axis=z\nedit Box dissolve select=z:5..5&!sharp:30" );
		var m = Mesh( studio );
		Check( "edit dissolve takes out a cut loop's edges", r.Ok && m.FaceCount == 6, $"{r} {m?.FaceCount}" );

		var (s2, r2) = Script( Box + "edit Box loopcut at=0,0,5 axis=z\nedit Box dissolve element=faces select=normal:front" );
		Check( "dissolving faces merges the region into one", r2.Ok && Mesh( s2 ).FaceCount == 9, $"{r2} {Mesh( s2 )?.FaceCount}" );

		var (s3, r3) = Script( Box + "edit Box loopcut at=0,0,5 axis=z\nedit Box limiteddissolve" );
		Check( "limited dissolve merges the coplanar halves", r3.Ok && Mesh( s3 ).FaceCount == 6, $"{r3} {Mesh( s3 )?.FaceCount}" );

		var (_, r4) = Script( Box + "edit Box limiteddissolve" );
		Check( "limited dissolve on a plain box changed nothing and says so", !r4.Ok && r4.Error.Contains( "changed nothing" ), r4.ToString() );
	}

	static void TestSelectorRules()
	{
		var box = Primitives.Box( 10, 10, 10 );
		int Faces( string rule ) => MeshSelector.TryParse( rule, out var s, out _ ) ? s.Faces( box ).Count() : -1;

		Check( "all picks every face", Faces( "all" ) == box.FaceCount );
		Check( "normal:up picks one face of a box", Faces( "normal:up" ) == 1 );
		Check( "normal by vector", Faces( "normal:1,0,0~1" ) == 1 );
		Check( "a band by percent", Faces( "height:90%..100%" ) == 1, $"{Faces( "height:90%..100%" )}" );
		Check( "index ranges", Faces( "index:0..2" ) == 3 );
		Check( "not", Faces( "!normal:up" ) == box.FaceCount - 1 );
		Check( "near a point", Faces( "near:0,0,5~1" ) == 1 && Faces( "near:0,0,5~0.5" ) == 1 && Faces( "near:0,0,0~0.1" ) == 0, $"{Faces( "near:0,0,5~1" )}" );
		Check( "boundary picks nothing on a closed box", Faces( "boundary" ) == 0 );
		Check( "every box face touches a sharp edge", Faces( "sharp:30" ) == 6 );
		Check( "a bad term is refused", !MeshSelector.TryParse( "z:5", out _, out var why ) && why.Contains( "lo..hi" ), why );
		Check( "edges by vertex pair", MeshSelector.TryParse( "index:0/1", out var e, out _ ) && e.Edges( box ).Count() <= 1 );
	}

	static void TestEditOpSurvivesSaveAndLoad()
	{
		var (studio, r) = Script( Box + "edit Box extrude select=normal:up distance=4\nedit Box bevel select=normal:up distance=0.5" );
		var text = StudioDocument.Write( studio );
		var loaded = StudioDocument.Read( text );
		loaded.Rebuild();
		Check( "edit ops save and replay from the document", r.Ok && Mesh( loaded )?.FaceCount == Mesh( studio ).FaceCount && loaded.Features.OfType<EditOpFeature>().Count() == 2,
			$"{r} {Mesh( loaded )?.FaceCount} vs {Mesh( studio )?.FaceCount}" );
	}

	const string TwoBoxes = "add Primitive name=A shape=Box sizex=10 sizey=10 sizez=10 position=0,0,0\n"
		+ "add Primitive name=B shape=Box sizex=10 sizey=10 sizez=10 position=5,0,0\n";

	static void TestVoxelBooleans()
	{
		var previous = MeshBoolean.Provider;
		MeshBoolean.Provider = null;

		try
		{
			foreach ( var (op, volume) in new[] { ("Union", 1500f), ("Subtract", 500f), ("Intersect", 500f) } )
			{
				var (studio, r) = Script( TwoBoxes + $"add Boolean operation={op} targets=A tools=B" );
				var a = Mesh( studio, "A" );
				var v = a?.SignedVolume() ?? 0f;
				Check( $"script {op.ToLowerInvariant()} runs with no engine boolean", r.Ok, r.ToString() );
				Check( $"and the {op.ToLowerInvariant()} has the right volume (within 4%)", MathF.Abs( v - volume ) < volume * 0.04f, $"{v:0.#} vs {volume}" );
				Check( $"closed, one body", a is not null && BoundaryEdges( a ) == 0 && ModelMeasure.FindBody( studio, "B" ) is null && MeshSplit.PieceCount( a ) == 1 );
				Check( "and says it was a voxel boolean", r.Log.Any( l => l.Contains( "voxel boolean" ) ), r.ToString() );
			}

			var (s2, r2) = Script( TwoBoxes + "add Boolean operation=Subtract targets=A tools=B method=Voxel voxel=0.25" );
			var (min, max) = ModelMeasure.Bounds( Mesh( s2, "A" ) );
			Check( "a subtract cuts where the tool was", r2.Ok && max.x < 0.2f && min.x < -4.8f, $"{r2} x {min.x}..{max.x}" );
		}
		finally { MeshBoolean.Provider = previous; }
	}

	static void TestVoxelBooleanRefusals()
	{
		var (_, apart) = Script( "add Primitive name=A shape=Box sizex=2 sizey=2 sizez=2\n"
			+ "add Primitive name=B shape=Box sizex=2 sizey=2 sizez=2 position=10,0,0\n"
			+ "add Boolean operation=Subtract targets=A tools=B method=Voxel" );
		Check( "a voxel subtract that misses says by how much", !apart.Ok && apart.Error.Contains( "do not overlap" ), apart.ToString() );

		var (_, tiny) = Script( TwoBoxes + "add Boolean operation=Union targets=A tools=B method=Voxel voxel=0.01" );
		Check( "a voxel too small says the size that fits", !tiny.Ok && tiny.Error.Contains( "Voxel to" ), tiny.ToString() );

		var (_, plane) = Script( TwoBoxes.Replace( "shape=Box sizex=10 sizey=10 sizez=10 position=5", "shape=Plane sizex=10 sizey=10 sizez=10 position=5" )
			+ "add Boolean operation=Union targets=A tools=B method=Voxel" );
		Check( "an open tool is refused", !plane.Ok && plane.Error.Contains( "open" ), plane.ToString() );

		var previous = MeshBoolean.Provider;
		MeshBoolean.Provider = null;
		try
		{
			var (_, exact) = Script( TwoBoxes + "add Boolean operation=Union targets=A tools=B method=Exact" );
			Check( "method=Exact with no engine still refuses, pointing at the editor", !exact.Ok && exact.Error.Contains( "editor" ), exact.ToString() );
		}
		finally { MeshBoolean.Provider = previous; }
	}

	static string ExportDir( string what )
	{
		var dir = Path.Combine( Path.GetTempPath(), "effigy-script-export", what + "-" + Guid.NewGuid().ToString( "N" )[..6] );
		Directory.CreateDirectory( dir );
		return dir;
	}

	const string Ball = "add Primitive name=Ball shape=Sphere radius=5 divisions=12 position=0,0,5\n";

	static void TestExportPlainIsUnchanged()
	{
		var dir = ExportDir( "plain" );
		var r = BuildScript.Run( new PartStudio(), Box + "export models/test/box", dir );
		var vmdl = Path.Combine( dir, "models/test/box/box.vmdl" );
		Check( "plain export still writes obj + vmdl", r.Ok && File.Exists( Path.Combine( dir, "models/test/box/box.obj" ) ) && File.Exists( vmdl ), r.ToString() );
		Check( "with no collision unless asked", File.Exists( vmdl ) && !File.ReadAllText( vmdl ).Contains( "PhysicsShapeList" ) );
	}

	static void TestExportLods()
	{
		var dir = ExportDir( "lods" );
		var r = BuildScript.Run( new PartStudio(), Ball + "export models/test/ball lods=0.5,0.25,0.1", dir );
		var folder = Path.Combine( dir, "models/test/ball" );
		var vmdl = File.Exists( Path.Combine( folder, "ball.vmdl" ) ) ? File.ReadAllText( Path.Combine( folder, "ball.vmdl" ) ) : "";
		Check( "export lods= runs", r.Ok, r.ToString() );
		Check( "and writes a mesh per level", Enumerable.Range( 1, 3 ).All( i => File.Exists( Path.Combine( folder, $"ball_lod{i}.obj" ) ) ) );
		Check( "and a vmdl with a LOD group per level", vmdl.Contains( "LODGroupList" ) && vmdl.Split( "_class = \"LODGroup\"" ).Length - 1 == 4, vmdl.Length.ToString() );

		int Tris( string file ) => ObjReader.Read( File.ReadAllText( Path.Combine( folder, file ) ) ) is { } m ? Decimate.TriangleCount( m ) : -1;
		var counts = new[] { "ball.obj", "ball_lod1.obj", "ball_lod2.obj", "ball_lod3.obj" }.Select( Tris ).ToArray();
		Check( "each level coarser than the last", counts.Zip( counts.Skip( 1 ) ).All( p => p.Second < p.First && p.Second > 0 ), string.Join( ",", counts ) );
		Check( "and the log says the counts", r.Log.Any( l => l.Contains( "LODs:" ) ), r.ToString() );

		var r1 = BuildScript.Run( new PartStudio(), Ball + "export models/test/ball1 lods=1 loddistance=100,300", ExportDir( "lods1" ) );
		Check( "lods=1 with distances", r1.Ok && r1.Log.Any( l => l.Contains( "from 100" ) && l.Contains( "from 300" ) ), r1.ToString() );
	}

	static void TestExportCollision()
	{
		foreach ( var (mode, expect) in new[] { ("auto", "PhysicsShapeBox"), ("hulls", "PhysicsShapeHull"), ("render", "PhysicsMeshFromRender") } )
		{
			var dir = ExportDir( "col-" + mode );
			var r = BuildScript.Run( new PartStudio(), Box + $"export models/test/box collision={mode} surface=metal", dir );
			var vmdl = Path.Combine( dir, "models/test/box/box.vmdl" );
			var text = File.Exists( vmdl ) ? File.ReadAllText( vmdl ) : "";
			Check( $"collision={mode} writes {expect}", r.Ok && text.Contains( expect ) && text.Contains( "\"metal\"" ), r.ToString() );
		}

		var dir2 = ExportDir( "col-pivot" );
		var r2 = BuildScript.Run( new PartStudio(), Box + "export models/test/box collision=auto", dir2 );
		var box = File.ReadAllText( Path.Combine( dir2, "models/test/box/box.vmdl" ) );
		Check( "the collision box moves onto the export's pivot (bottom centre)", r2.Ok && box.Contains( "origin = [ 0.0, 0.0, 5.0 ]" ), Snippet( box, "PhysicsShapeBox" ) );

		var split = BuildScript.Run( new PartStudio(), Box + "add Primitive name=Lid shape=Box sizex=10 sizey=10 sizez=2 position=0,0,20\nexport models/test/kit split=1 collision=hulls", ExportDir( "col-split" ) );
		Check( "split parts each get their own hull", split.Ok && split.Log.Count( l => l.Contains( "1 hull(s), one per body" ) ) == 2, split.ToString() );
	}

	static string Snippet( string text, string near )
	{
		var i = text.IndexOf( near, StringComparison.Ordinal );
		return i < 0 ? "(missing)" : text.Substring( i, Math.Min( 400, text.Length - i ) );
	}

	static void TestExportFbx()
	{
		var dir = ExportDir( "fbx" );
		var r = BuildScript.Run( new PartStudio(), Box + "export models/test/box fbx=1", dir );
		var fbx = Path.Combine( dir, "models/test/box/box.fbx" );
		Check( "fbx=1 writes a binary fbx beside the model", r.Ok && File.Exists( fbx ) && System.Text.Encoding.ASCII.GetString( File.ReadAllBytes( fbx ), 0, 18 ) == "Kaydara FBX Binary", r.ToString() );
		Check( "and the vmdl still loads the obj", File.ReadAllText( Path.Combine( dir, "models/test/box/box.vmdl" ) ).Contains( "box.obj" ) );
	}

	static PartStudio RiggedStudio()
	{
		var studio = new PartStudio();
		var r = BuildScript.Run( studio, "add Primitive name=Leg shape=Box sizex=4 sizey=4 sizez=20 position=0,0,10" );
		var hip = studio.Rig.AddBoneFromPoints( "hip", -1, new Vec3( 0, 0, 20 ), new Vec3( 0, 0, 10 ) );
		studio.Rig.AddBoneFromPoints( "knee", hip, new Vec3( 0, 0, 10 ), new Vec3( 0, 0, 0 ) );
		return r.Ok ? studio : null;
	}

	static void TestExportSkinned()
	{
		var studio = RiggedStudio();
		var dir = ExportDir( "skin" );
		var r = BuildScript.Run( studio, "export models/test/leg collision=auto fbx=1", dir );
		var folder = Path.Combine( dir, "models/test/leg" );
		var vmdl = File.Exists( Path.Combine( folder, "leg.vmdl" ) ) ? File.ReadAllText( Path.Combine( folder, "leg.vmdl" ) ) : "";
		Check( "a studio with bones exports skinned", r.Ok && File.Exists( Path.Combine( folder, "leg.dmx" ) ) && File.Exists( Path.Combine( folder, "leg.smd" ) ), r.ToString() );
		Check( "the vmdl loads the dmx and marks up the bones", vmdl.Contains( "leg.dmx" ) && vmdl.Contains( "\"hip\"" ) && vmdl.Contains( "\"knee\"" ), vmdl.Length.ToString() );
		Check( "its collision hull rides a bone", vmdl.Contains( "PhysicsShapeHull" ) && System.Text.RegularExpressions.Regex.IsMatch( vmdl, "parent_bone = \"(hip|knee)\"" ), Snippet( vmdl, "PhysicsShapeHull" ) );

		var dmx = File.ReadAllText( Path.Combine( folder, "leg.dmx" ) );
		Check( "the dmx carries weights", dmx.Contains( "blendweights$0" ) && dmx.Contains( "blendindices$0" ), dmx.Length.ToString() );
		Check( "and the skinned fbx is written too", File.Exists( Path.Combine( folder, "leg.fbx" ) ) );

		var stat = BuildScript.Run( RiggedStudio(), "export models/test/legstatic skin=0", ExportDir( "skin0" ) );
		Check( "skin=0 exports the rigged studio static", stat.Ok && !stat.Written.Any( w => w.EndsWith( ".dmx" ) ), stat.ToString() );
	}

	static void TestExportRefusals()
	{
		string Err( string script, PartStudio studio = null ) => BuildScript.Run( studio ?? new PartStudio(), script, ExportDir( "bad" ) ).Error ?? "(ok)";

		Check( "skin=1 with no rig is refused", Err( Box + "export m/x skin=1" ).Contains( "no bones" ) );
		Check( "lods on a skinned export now writes skinned levels (ExportGapsTests checks them)", Err( "export m/x lods=1", RiggedStudio() ) == "(ok)" );
		Check( "skin with split is refused", Err( "export m/x skin=1 split=1", RiggedStudio() ).Contains( "split" ) );
		Check( "a bad lod list says what it takes", Err( Box + "export m/x lods=0.25,0.5" ).Contains( "coarser" ) );
		Check( "loddistance needs one per level", Err( Box + "export m/x lods=0.5,0.25 loddistance=100" ).Contains( "one per level" ) );
		Check( "an unknown collision mode", Err( Box + "export m/x collision=mesh" ).Contains( "none, auto, hulls or render" ) );
		Check( "an unknown option lists the real ones", Err( Box + "export m/x lod=1" ).Contains( "lods=" ) );
	}
}
