using System;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The one thing this file exists to hold: an OBJ-backed .vmdl carries the import_rotation
/// correction and a DMX-backed one does not.
///
/// THIS WAS A REAL MODEL THAT COMPILED LYING ON ITS SIDE. The correction was measured once and
/// written into EffigyWindow.BuildVmdl, and then the same template was copied into
/// BuildSkinnedVmdl, TreeGen and TentacleGen without it — three silent copies, because a literal
/// string cannot tell you which mesh format it is about. Deriving the rotation from the filename
/// is what makes the mistake unrepresentable; this is what keeps it that way.
/// </summary>
public static class VmdlDocumentTests
{
	public static void Run()
	{
		Section( "vmdl document: an OBJ is turned, a DMX is not" );
		TestObjGetsTheCorrection();
		TestDmxAndSmdDoNot();

		Section( "vmdl document: the nodes each kind of model needs" );
		TestStaticHasOneMeshFile();
		TestSkinnedCarriesTheBindPose();
	}

	static void TestObjGetsTheCorrection()
	{
		Check( "an OBJ needs the -90/-90 inverse of ModelDoc's axis permutation",
			VmdlDocument.ImportRotation( "models/effigy/x.obj" ) == "-90.0, -90.0, 0.0" );

		// Case does not decide it. An exporter that wrote .OBJ would otherwise land on its side.
		Check( "the extension test is case insensitive",
			VmdlDocument.ImportRotation( "X.OBJ" ) == "-90.0, -90.0, 0.0" );

		var vmdl = VmdlDocument.Static( "models/effigy/remesh_all.obj" );

		Check( "a static OBJ model writes the corrected rotation",
			vmdl.Contains( "import_rotation = [ -90.0, -90.0, 0.0 ]" ) );
	}

	static void TestDmxAndSmdDoNot()
	{
		// Only the OBJ importer turns the mesh. Applying the correction to a DMX would break the
		// rigged path, which is the export that actually ships.
		Check( "the DMX importer lands the mesh where the file says",
			VmdlDocument.ImportRotation( "models/effigy/x.dmx" ) == "0.0, 0.0, 0.0" );

		Check( "nor does an SMD get turned",
			VmdlDocument.ImportRotation( "models/effigy/x.smd" ) == "0.0, 0.0, 0.0" );

		var skinned = VmdlDocument.Skinned( "models/effigy/x.dmx", new Skeleton() );

		Check( "a skinned DMX model leaves the rotation alone",
			skinned.Contains( "import_rotation = [ 0.0, 0.0, 0.0 ]" ) );
	}

	static void TestStaticHasOneMeshFile()
	{
		var vmdl = VmdlDocument.Static( "models/effigy/box.obj", VmdlPhysics.MeshFromRender() );

		Check( "the document has a root node", vmdl.Contains( "_class = \"RootNode\"" ) );
		Check( "it names the mesh", vmdl.Contains( "filename = \"models/effigy/box.obj\"" ) );

		Check( "exactly one render mesh, not a duplicated node",
			CountOf( vmdl, "_class = \"RenderMeshFile\"" ) == 1 );

		Check( "the physics the caller passed is in it", vmdl.Contains( "PhysicsMeshFromRender" ) );

		// A static model has no skeleton, so no bone markup: those nodes are the rigged path's
		// business and describe bones a static mesh does not have.
		Check( "no bone markup on a static model", !vmdl.Contains( "BoneMarkup" ) );
	}

	static void TestSkinnedCarriesTheBindPose()
	{
		var skinned = VmdlDocument.Skinned( "models/effigy/x.dmx", new Skeleton() );

		Check( "a rigged model carries the bind pose, or morph targets and IK break quietly",
			skinned.Contains( "AnimationList" ) || skinned.Contains( "BindPose" ) );

		// The caller's own AnimationList replaces the bind-pose-only default rather than joining it.
		var withClips = VmdlDocument.Skinned( "models/effigy/x.dmx", new Skeleton(),
			animations: "\t\t\tCLIPS\n" );

		Check( "clips reach the document", withClips.Contains( "CLIPS" ) );
		Check( "and are not written twice", CountOf( withClips, "CLIPS" ) == 1 );
	}

	static int CountOf( string haystack, string needle )
	{
		var n = 0;

		for ( var i = haystack.IndexOf( needle, StringComparison.Ordinal ); i >= 0;
			i = haystack.IndexOf( needle, i + needle.Length, StringComparison.Ordinal ) )
			n++;

		return n;
	}
}
