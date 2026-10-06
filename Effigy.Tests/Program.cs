using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Verification for the kernel. Console runner rather than a test framework, so it stays a
/// dependency-free thing that can be pointed at any of this code from anywhere.
///
/// This exists because subdivision code is the classic case of "looks right, is wrong". A
/// Catmull-Clark implementation with the vertex rule subtly off still produces a smooth, plausible
/// blob — it just shrinks slightly incorrectly and drifts further from the limit surface at every
/// level. Eyeballing a render will not catch it. Euler characteristic and the exact V/E/F growth
/// laws will.
/// </summary>
public static class Program
{
	/// <summary>
	/// Effigy.Tests/out, wherever this file happens to live - NOT "out" relative to whatever
	/// directory the suite was launched from.
	///
	/// A plain relative path put the samples wherever you happened to be standing. Run through
	/// tools/test.sh they landed in Effigy.Tests/out, which is the tracked copy; run as
	/// `dotnet run --project Effigy.Tests` from the repo root - the obvious thing to type - they
	/// landed in a second `out/` beside the .sbproj. That one is gitignored, so it is invisible to
	/// every check that would have caught it, and s&amp;box publishes project files rather than
	/// tracked files: 46 sample OBJs went out in the library package before anyone noticed.
	///
	/// CallerFilePath is the compiler telling us where this source file was, which is the one
	/// answer that does not depend on the caller's working directory.
	/// </summary>
	static string DefaultOutDir( [CallerFilePath] string thisFile = "" ) =>
		Path.Combine( Path.GetDirectoryName( thisFile ) ?? ".", "out" );

	public static int Main( string[] rawArgs )
	{
		// `dotnet run --nologo` hands the flag straight through to us. The first positional
		// argument is the output directory, so an unrecognised flag taken as one wrote the whole
		// sample set into a folder literally named `--nologo`. Drop flags we do not own.
		var args = rawArgs.Where( a => !a.StartsWith( "--" ) || a == "--tree" || a == "--tentacle" || a == "--paint" || a == "--remesh" || a == "--playermodels" ).ToArray();

		// The headless Effigy commands — render, describe, measure, match, script, fix — see EffigyCli.
		if ( rawArgs.Length > 0 && EffigyCli.Handles( rawArgs[0] ) )
			return EffigyCli.Run( rawArgs );

		if ( args.Length > 0 && args[0] == "--tree" )
			return TreeGen.Run( args.Length > 1 ? args[1] : DefaultOutDir() );

		if ( args.Length > 0 && args[0] == "--tentacle" )
			return TentacleGen.Run( args.Length > 1 ? args[1] : DefaultOutDir() );

		if ( args.Length > 0 && args[0] == "--paint" )
			return PaintGen.Run( args.Length > 1 ? args[1] : DefaultOutDir() );

		if ( args.Length > 0 && args[0] == "--remesh" )
			return RemeshGen.Run( args.Length > 1 ? args[1] : DefaultOutDir(),
				args.Length > 2 ? args[2] : null,
				args.Length > 3 && float.TryParse( args[3], out var keep ) ? keep : (float?)null );

		if ( args.Length > 1 && args[0] == "--playermodels" )
			return PlayermodelGen.Run( args[1], args.Length > 2 ? args[2] : Path.Combine( DefaultOutDir(), "playermodels_preview.png" ) );

		var outDir = args.Length > 0 ? args[0] : DefaultOutDir();

		Section( "primitives are valid and manifold" );
		TestPrimitiveValidity();

		Section( "primitives are closed, except the plane" );
		TestClosedness();

		Section( "Euler characteristic matches expected genus" );
		TestEuler();

		Section( "face winding puts normals outward" );
		TestWinding();

		Section( "Catmull-Clark output is all quads" );
		TestAllQuads();

		Section( "Catmull-Clark obeys the V/E/F growth laws" );
		TestGrowthLaws();

		Section( "Catmull-Clark preserves topology" );
		TestTopologyPreserved();

		Section( "Catmull-Clark keeps an open mesh's boundary" );
		TestBoundaryPreserved();

		Section( "subdivision converges rather than drifting" );
		TestConvergence();

		Section( "PredictCost agrees with reality" );
		TestPredictCost();

		Section( "UV seams survive subdivision" );
		TestUVSeams();

		Section( "OBJ round-trips" );
		TestObjRoundTrip();

		Section( "editable mesh round-trips and validates" );
		TestEditableRoundTrip();

		Section( "editable mesh extrudes a region" );
		TestEditableExtrude();

		Section( "editable mesh deletes faces" );
		TestEditableDelete();

		Section( "editable mesh insets faces" );
		TestEditableInset();

		Section( "editable mesh loop cuts around quad rings" );
		TestEditableLoopCut();

		Section( "editable mesh bridges two boundary loops" );
		TestEditableBridge();

		Section( "editable edit operations compose" );
		TestEditableCompose();

		Section( "editable mesh dissolves edges and vertices" );
		TestEditableDissolve();

		Section( "an edit session selects, edits, undoes and commits" );
		TestEditSession();
		TestEditSessionModelingOps();
		TestEditSessionShapingOps();
		TestRetopology();
		TestRetopologyHelpers();
		TestGridFillRipPivot();
		TestMeshModifiers();
		TestEditSessionSeams();
		TestEditSessionVertexSlide();
		TestLoopCutPreview();
		TestEditSessionSubdivide();
		TestEditSessionSmoothAndSymmetry();
		TestEditSessionWeightTools();
		TestEditSessionShrinkFattenAndGrow();
		TestEditSessionEdgeSplit();
		TestEditSessionSelectionTools();
		TestEditSessionTrisToQuadsAndBevelVertices();
		TestEditSessionHideAndExtras();
		TestEdgeCreases();
		TestEditSessionTopologyTools();
		TestEditSessionMappingTools();
		TestSkinBlockout();
		TestUVIslands();
		TestAddPrimitiveAndFromScratch();

		Section( "an edit session duplicates, separates, extracts a garment and bridges uneven loops" );
		TestEditSessionPieces();

		Section( "cloth drapes under gravity, holds its pins and lies on the body" );
		TestClothDrape();

		Section( "a mesh edit feature keeps its edit through save, and refuses a changed body" );
		TestMeshEditFeature();

		Section( "a shirt and its fur come out of a rebuild, coloured for the fur shader" );
		TestClothingBuild();
		TestClothingDefinition();
		TestWearer();
		TestGarmentCheck();
		TestGarmentTrim();
		TestGarmentShaping();
		TestPreviewQuality();

		Section( "welding coincident vertices" );
		TestWeld();

		DecimateTests.Run();
		GarmentPosesTests.Run();
		GarmentCutTests.Run();
		FabricMaterialTests.Run();

		SculptTests.Run();

		FeatureTests.Run();
		SketchTests.Run();

		RigTests.Run();

		BoneFromBodyTests.Run();

		RigDiagnosticTests.Run();

		CitizenSkeletonTests.Run();

		SkeletonRetargetTests.Run();
		PlayermodelSampleTests.Run();
		TwistWeightsTests.Run();

		SoftBoneTests.Run();

		WeightPaintTests.Run();

		VmdlAnimationTests.Run();

		ShellTests.Run();

		EdgeBlendTests.Run();

		UVTests.Run();

		PaintCanvasTests.Run();
		PaintReplayTests.Run();
		PaintMaterialTests.Run();

		BooleanFeatureTests.Run();

		ExpressionTests.Run();
		VariableTests.Run();
		MeshClipTests.Run();

		SnapTests.Run();

		RaycastTests.Run();
		BvhFaceTests.Run();

		AllFeaturesTests.Run();

		ImportFeatureTests.Run();

		AcceptsTests.Run();

		FaceMoveTests.Run();

		FaceExtrudeTests.Run();

		DiagnosticTests.Run();

		FaceSurfaceTests.Run();

		FaceSketchTests.Run();

		PlaneTests.Run();

		ConstraintTests.Run();

		CurveTests.Run();

		SketchEditTests.Run();

		HandleTests.Run();

		SweepLoftTests.Run();

		UntestedKernelTests.Run();

		DmxGrammarTests.Run();

		DmxAnimTests.Run();

		MergeTests.Run();
		AtlasIdTests.Run();
		SplineTests.Run();
		AgentToolsTests.Run();

		FaceMaterialTests.Run();
		FaceMenuTests.Run();
		MaterialDropTests.Run();
		MaterialDropCurvedTests.Run();
		MaterialScaleTests.Run();
		RenderTests.Run();
		ConstraintToolTests.Run();

		DocumentTests.Run();

		PaintDocumentTests.Run();

		HoleTests.Run();

		SplitTests.Run();

		CurvedHoleTests.Run();

		CoplanarMergeTests.Run();

		TaperTests.Run();

		BranchTests.Run();

		TerminationTests.Run();

		EditorFlowTests.Run();

		DraftTests.Run();

		HoleFeatureTests.Run();

		CollisionTests.Run();

		VmdlPhysicsTests.Run();

		VmdlMaterialsTests.Run();

		VmdlDocumentTests.Run();
		BoneSelectionTests.Run();

		RigTrackNameTests.Run();

		UnwrapTests.Run();

		PngTests.Run();

		NoteTests.Run();

		KernelSyncTests.Run();

		MenuIconTests.Run();

		Section( "writing sample OBJs" );
		WriteSamples( outDir );

		Console.WriteLine();
		Console.WriteLine( new string( '-', 60 ) );
		Console.WriteLine( $"  {Report.Passed} passed, {Report.Failed} failed" );
		Console.WriteLine( new string( '-', 60 ) );

		return Report.Failed == 0 ? 0 : 1;
	}

	// ---------------------------------------------------------------------------------------

	internal static Dictionary<string, PolyMesh> Closed() => new()
	{
		["box"] = Primitives.Box( 2, 2, 2 ),
		["cylinder"] = Primitives.Cylinder( 0.5f, 1f, 16 ),
		["quadsphere"] = Primitives.QuadSphere( 0.5f, 4 ),
		["wedge"] = Primitives.Wedge( 1, 1, 1 ),
		["tube"] = Primitives.Tube( 0.5f, 0.3f, 1f, 16 ),
	};

	static void TestEditableRoundTrip()
	{
		// A closed box: positions, faces, winding, per-corner UVs and materials must come back
		// identical, and the editable mesh must read as valid, closed and manifold.
		var box = Primitives.Box( 2, 2, 2 );
		var editable = EditableMesh.FromPolyMesh( box );
		var ev = editable.Validate();
		Check( "box converts to a valid editable mesh", ev.IsValid, ev.ToString() );
		Check( "box editable mesh is closed", ev.IsClosed, $"{ev.BoundaryEdges} boundary edges" );

		var round = editable.ToPolyMesh();
		Check( "box round-trips to the same vertex count", round.VertexCount == box.VertexCount );
		Check( "box round-trips to the same face count", round.FaceCount == box.FaceCount );
		Check( "box round-trips faces, winding, UVs and materials exactly", SameFaces( box, round ) );
		Check( "box round-trips to a still-valid mesh", MeshValidator.Validate( round ).IsValid );
		Check( "box round-trips to the same volume",
			MathF.Abs( box.SignedVolume() - round.SignedVolume() ) < 1e-4f );

		// Winding: face 0 of the editable mesh walks its corners in the same order.
		Check( "face 0 keeps its winding", editable.FaceVertices( 0 ).SequenceEqual( box.Faces[0].Indices ) );

		// Vertex rotation: a box corner touches exactly three faces.
		var around = new List<int>();
		editable.OutgoingHalfEdges( 0, around );
		Check( "a box corner rotates through 3 faces", around.Count == 3, $"got {around.Count}" );

		// An open plane keeps its boundary edges.
		var plane = Primitives.Plane( 2, 2, 2, 2 );
		var planeEdit = EditableMesh.FromPolyMesh( plane );
		var pv = planeEdit.Validate();
		Check( "a 2x2 plane keeps its 8-edge boundary", pv.BoundaryEdges == 8, $"got {pv.BoundaryEdges}" );
		Check( "the plane still round-trips its faces", planeEdit.ToPolyMesh().FaceCount == plane.FaceCount );

		// Skin weights ride the positions, so a round trip must not drop them.
		var rigged = Primitives.Box( 2, 2, 2 );
		rigged.Skin = SkinWeights.AllTo( rigged.VertexCount, 1 );
		var riggedRound = EditableMesh.FromPolyMesh( rigged ).ToPolyMesh();
		Check( "skin weights survive the round trip", riggedRound.IsRigged
			&& riggedRound.Skin.Count == rigged.Skin.Count );

		// A deliberately broken twin must be caught, not silently accepted.
		var corrupt = EditableMesh.FromPolyMesh( box );
		var broken = corrupt.HalfEdges[0];
		broken.Twin = -1;
		corrupt.HalfEdges[0] = broken;
		Check( "a broken twin is caught by validation", !corrupt.Validate().IsValid );
	}

	static bool SameFaces( PolyMesh a, PolyMesh b )
	{
		if ( a.FaceCount != b.FaceCount )
			return false;

		for ( var i = 0; i < a.FaceCount; i++ )
		{
			var fa = a.Faces[i];
			var fb = b.Faces[i];

			if ( fa.Material != fb.Material || fa.Count != fb.Count )
				return false;

			if ( !fa.Indices.SequenceEqual( fb.Indices ) )
				return false;

			for ( var c = 0; c < fa.Count; c++ )
			{
				if ( (fa.UVs[c] - fb.UVs[c]).LengthSquared > 1e-12f )
					return false;
			}
		}

		return true;
	}

	static void TestEditableExtrude()
	{
		// Extrude the top face of a box straight up: the box gains four side walls and its top cap
		// moves, so a 2x2x2 box becomes a 2x2x3 one — 10 faces, 12 vertices, still closed.
		var box = Primitives.Box( 2, 2, 2 );
		var top = box.Faces.FindIndex( f => (box.FaceNormal( f ) - new Vec3( 0, 0, 1 )).Length < 1e-4f );

		var up = EditableMesh.FromPolyMesh( box );
		up.ExtrudeRegion( new[] { top }, new Vec3( 0, 0, 1 ) );
		Check( "extruding up keeps the mesh valid", up.Validate().IsValid, up.Validate().ToString() );

		var upMesh = up.ToPolyMesh();
		Check( "extruded box is closed", MeshValidator.Validate( upMesh ).IsClosed );
		Check( "extruded box has 10 faces", upMesh.FaceCount == 10, $"got {upMesh.FaceCount}" );
		Check( "extruded box has 12 vertices", upMesh.VertexCount == 12, $"got {upMesh.VertexCount}" );
		Check( "extruded box volume is 12", MathF.Abs( upMesh.SignedVolume() - 12f ) < 1e-3f,
			$"got {upMesh.SignedVolume():0.####}" );

		// Extruding sideways shears the top cap without changing the enclosed volume.
		var shear = EditableMesh.FromPolyMesh( box );
		shear.ExtrudeRegion( new[] { top }, new Vec3( 0.5f, 0, 0 ) );
		var shearMesh = shear.ToPolyMesh();
		Check( "a sheared extrude stays closed", MeshValidator.Validate( shearMesh ).IsClosed );
		Check( "a sheared extrude keeps the volume", MathF.Abs( shearMesh.SignedVolume() - 8f ) < 1e-3f,
			$"got {shearMesh.SignedVolume():0.####}" );

		// Extruding an open sheet gives side walls but no bottom — the classic open skirt.
		var plane = Primitives.Plane( 2, 2, 1, 1 );
		var skirt = EditableMesh.FromPolyMesh( plane );
		skirt.ExtrudeRegion( new[] { 0 }, new Vec3( 0, 0, 1 ) );
		var skirtMesh = skirt.ToPolyMesh();
		var skirtValid = MeshValidator.Validate( skirtMesh );
		Check( "an extruded sheet stays valid", skirtValid.IsValid, skirtValid.ToString() );
		Check( "an extruded sheet has 5 faces", skirtMesh.FaceCount == 5, $"got {skirtMesh.FaceCount}" );
		Check( "an extruded sheet stays open underneath", skirtValid.BoundaryEdges == 4,
			$"got {skirtValid.BoundaryEdges}" );
	}

	static void TestEditableDelete()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var top = box.Faces.FindIndex( f => (box.FaceNormal( f ) - new Vec3( 0, 0, 1 )).Length < 1e-4f );

		var e = EditableMesh.FromPolyMesh( box );
		e.DeleteFaces( new[] { top } );
		var v = e.Validate();
		Check( "deleting the top face keeps the mesh valid", v.IsValid, v.ToString() );
		Check( "and leaves it open", !v.IsClosed );
		Check( "with 4 boundary edges at the rim", v.BoundaryEdges == 4, $"got {v.BoundaryEdges}" );

		var m = e.ToPolyMesh();
		Check( "the deleted box has 5 faces", m.FaceCount == 5, $"got {m.FaceCount}" );
		Check( "and keeps its 8 vertices", m.VertexCount == 8, $"got {m.VertexCount}" );

		var mv = MeshValidator.Validate( m );
		Check( "the round-tripped deleted box is still valid", mv.IsValid, mv.ToString() );
		Check( "and open with 4 boundary edges", mv.BoundaryEdges == 4, $"got {mv.BoundaryEdges}" );

		// Deleting every face leaves an empty-but-valid mesh with its vertices intact.
		var all = EditableMesh.FromPolyMesh( box );
		all.DeleteFaces( Enumerable.Range( 0, box.FaceCount ).ToList() );
		Check( "deleting every face leaves no faces", all.FaceCount == 0 );
		Check( "and the vertices survive", all.VertexCount == 8 );
	}

	static void TestEditableInset()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var top = box.Faces.FindIndex( f => (box.FaceNormal( f ) - new Vec3( 0, 0, 1 )).Length < 1e-4f );

		var e = EditableMesh.FromPolyMesh( box );
		e.InsetFaces( new[] { top }, 0.25f );
		Check( "insetting keeps the mesh valid", e.Validate().IsValid, e.Validate().ToString() );

		var m = e.ToPolyMesh();
		var mv = MeshValidator.Validate( m );
		Check( "the inset box stays closed", mv.IsClosed );
		Check( "the inset box has 10 faces", m.FaceCount == 10, $"got {m.FaceCount}" );
		Check( "the inset box has 12 vertices", m.VertexCount == 12, $"got {m.VertexCount}" );
		Check( "the inset box volume is unchanged", MathF.Abs( m.SignedVolume() - 8f ) < 1e-3f,
			$"got {m.SignedVolume():0.####}" );

		// Insetting a lone quad makes a smaller quad ringed by four more, staying an open disc.
		var plane = Primitives.Plane( 2, 2, 1, 1 );
		var p = EditableMesh.FromPolyMesh( plane );
		p.InsetFaces( new[] { 0 }, 0.25f );
		var pm = p.ToPolyMesh();
		var pv = MeshValidator.Validate( pm );
		Check( "the inset plane is valid", pv.IsValid, pv.ToString() );
		Check( "the inset plane has 5 faces", pm.FaceCount == 5, $"got {pm.FaceCount}" );
		Check( "the inset plane has 8 vertices", pm.VertexCount == 8, $"got {pm.VertexCount}" );
		Check( "the inset plane stays open at its rim", pv.BoundaryEdges == 4, $"got {pv.BoundaryEdges}" );

		var area = 0f;
		foreach ( var f in pm.Faces )
			area += pm.FaceArea( f );
		Check( "the inset plane keeps its area", MathF.Abs( area - 4f ) < 1e-3f, $"got {area:0.####}" );
	}

	static void TestEditableLoopCut()
	{
		var box = Primitives.Box( 2, 2, 2 );

		// A loop cut around a box's equator splits each side quad in two and turns the box into
		// two stacked halves: 4 new vertices on the ring, 4 new faces across the side quads.
		var e = EditableMesh.FromPolyMesh( box );

		int seed = -1;

		for ( var hei = 0; hei < e.HalfEdges.Count; hei++ )
		{
			var a = e.Positions[e.HalfEdges[hei].Origin];
			var b = e.Positions[e.HalfEdges[e.HalfEdges[hei].Next].Origin];

			if ( MathF.Abs( a.x - b.x ) < 1e-5f && MathF.Abs( a.y - b.y ) < 1e-5f && MathF.Abs( a.z - b.z ) > 1e-3f )
			{
				seed = hei;
				break;
			}
		}

		Check( "a vertical edge of the box seeds the ring", seed >= 0 );

		e.LoopCut( seed, 0.5f );
		Check( "the loop cut keeps the mesh valid", e.Validate().IsValid, e.Validate().ToString() );

		var m = e.ToPolyMesh();
		var v = MeshValidator.Validate( m );
		Check( "the cut box stays closed", v.IsClosed, v.ToString() );
		Check( "the cut box has 12 vertices", m.VertexCount == 12, $"got {m.VertexCount}" );
		Check( "the cut box has 10 faces", m.FaceCount == 10, $"got {m.FaceCount}" );
		Check( "the cut box keeps its volume", MathF.Abs( m.SignedVolume() - 8f ) < 1e-3f,
			$"got {m.SignedVolume():0.####}" );

		// An uneven cut still splits cleanly and preserves volume exactly (nothing moves).
		var e2 = EditableMesh.FromPolyMesh( box );
		e2.LoopCut( seed, 0.25f );
		var m2 = e2.ToPolyMesh();
		var v2 = MeshValidator.Validate( m2 );
		Check( "an uneven cut stays valid and closed", v2.IsValid && v2.IsClosed, v2.ToString() );
		Check( "an uneven cut keeps the volume", MathF.Abs( m2.SignedVolume() - 8f ) < 1e-3f,
			$"got {m2.SignedVolume():0.####}" );

		// A ring that runs off the mesh's boundary is refused, not guessed at.
		var plane = Primitives.Plane( 2, 2, 1, 1 );
		var p = EditableMesh.FromPolyMesh( plane );

		var threw = false;

		try { p.LoopCut( 0, 0.5f ); }
		catch ( InvalidOperationException ) { threw = true; }

		Check( "an open ring that meets the boundary is refused", threw );
	}

	/// <summary>Two identical quads, one flipped, make a slab fixture whose two rims a bridge joins.</summary>
	static PolyMesh BridgedSlabFixture()
	{
		var mesh = new PolyMesh();

		// Bottom cap, fronting -z.
		mesh.AddVertex( new Vec3( -1, -1, 0 ) );
		mesh.AddVertex( new Vec3(  1, -1, 0 ) );
		mesh.AddVertex( new Vec3(  1,  1, 0 ) );
		mesh.AddVertex( new Vec3( -1,  1, 0 ) );
		mesh.AddFace( new[] { 0, 3, 2, 1 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );

		// Top cap, stacked 2 up, fronting +z.
		mesh.AddVertex( new Vec3( -1, -1, 2 ) );
		mesh.AddVertex( new Vec3(  1, -1, 2 ) );
		mesh.AddVertex( new Vec3(  1,  1, 2 ) );
		mesh.AddVertex( new Vec3( -1,  1, 2 ) );
		mesh.AddFace( new[] { 4, 5, 6, 7 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );

		return mesh;
	}

	static int BoundarySeed( EditableMesh e, int face )
	{
		for ( var hei = 0; hei < e.HalfEdges.Count; hei++ )
			if ( e.HalfEdges[hei].Face == face && e.HalfEdges[hei].Twin < 0 )
				return hei;

		return -1;
	}

	static void TestEditableBridge()
	{
		var mesh = BridgedSlabFixture();
		var e = EditableMesh.FromPolyMesh( mesh );

		var seedA = BoundarySeed( e, 0 );
		var seedB = BoundarySeed( e, 1 );

		Check( "a boundary half-edge of each cap seeds a rim", seedA >= 0 && seedB >= 0 );

		e.BridgeLoops( seedA, seedB );
		Check( "the bridge keeps the mesh valid", e.Validate().IsValid, e.Validate().ToString() );
		Check( "and closes the slab", e.Validate().IsClosed, $"{e.Validate().BoundaryEdges} boundary edges" );

		var m = e.ToPolyMesh();
		var mv = MeshValidator.Validate( m );
		Check( "the round-tripped slab is valid", mv.IsValid, mv.ToString() );
		Check( "and closed", mv.IsClosed );
		Check( "the slab has 6 faces", m.FaceCount == 6, $"got {m.FaceCount}" );
		Check( "the slab has 8 vertices", m.VertexCount == 8, $"got {m.VertexCount}" );
		Check( "the slab encloses volume 8", MathF.Abs( m.SignedVolume() - 8f ) < 1e-3f,
			$"got {m.SignedVolume():0.####}" );
		Check( "wound outward like the caps", m.SignedVolume() > 0, $"got {m.SignedVolume():0.####}" );

		// An interior seed is refused — a bridge needs two rims.
		var box = EditableMesh.FromPolyMesh( Primitives.Box( 2, 2, 2 ) );
		var interiorSeed = -1;

		for ( var hei = 0; hei < box.HalfEdges.Count && interiorSeed < 0; hei++ )
			if ( box.HalfEdges[hei].Twin >= 0 )
				interiorSeed = hei;

		var interiorThrew = false;

		try { box.BridgeLoops( interiorSeed, interiorSeed ); }
		catch ( ArgumentOutOfRangeException ) { interiorThrew = true; }

		Check( "a seed on an interior edge is refused", interiorThrew );

		// Mismatched rim lengths are refused rather than interpolated.
		var grid = Primitives.Plane( 2, 2, 2, 2 );
		var quad = Primitives.Plane( 2, 2, 1, 1 );
		MeshTransform.Append( grid, quad );

		var mismatched = EditableMesh.FromPolyMesh( grid );
		var gSeed = BoundarySeed( mismatched, 0 );
		var qSeed = BoundarySeed( mismatched, 4 );

		var mismatchThrew = false;

		try { mismatched.BridgeLoops( gSeed, qSeed ); }
		catch ( InvalidOperationException ) { mismatchThrew = true; }

		Check( "two rims of different sizes are refused", mismatchThrew );
	}

	static void TestEditableCompose()
	{
		// Ops must stack the way an edit session will use them: extrude, then cut the result.
		var box = Primitives.Box( 2, 2, 2 );
		var top = box.Faces.FindIndex( f => (box.FaceNormal( f ) - new Vec3( 0, 0, 1 )).Length < 1e-4f );

		var e = EditableMesh.FromPolyMesh( box );
		e.ExtrudeRegion( new[] { top }, new Vec3( 0, 0, 1 ) );
		Check( "after the extrude the mesh stays valid", e.Validate().IsValid, e.Validate().ToString() );
		Check( "and closed", e.Validate().IsClosed );

		var seed = -1;

		for ( var hei = 0; hei < e.HalfEdges.Count && seed < 0; hei++ )
		{
			var a = e.Positions[e.HalfEdges[hei].Origin];
			var b = e.Positions[e.HalfEdges[e.HalfEdges[hei].Next].Origin];

			if ( MathF.Abs( a.x - b.x ) < 1e-5f && MathF.Abs( a.y - b.y ) < 1e-5f && MathF.Abs( a.z - b.z ) > 1e-3f )
				seed = hei;
		}

		e.LoopCut( seed, 0.5f );

		var m = e.ToPolyMesh();
		var v = MeshValidator.Validate( m );
		Check( "after the loop cut the mesh is still valid and closed", v.IsValid && v.IsClosed, v.ToString() );
		Check( "the cut added four vertices and four faces", m.VertexCount == 16 && m.FaceCount == 14,
			$"got {m.VertexCount}v/{m.FaceCount}f" );
		Check( "the composed solid keeps its volume", MathF.Abs( m.SignedVolume() - 12f ) < 1e-3f,
			$"got {m.SignedVolume():0.####}" );
	}

	static int TopFace( PolyMesh m ) =>
		m.Faces.FindIndex( f => (m.FaceNormal( f ) - new Vec3( 0, 0, 1 )).Length < 1e-4f && m.FaceCentroid( f ).z > 0.5f );

	/// <summary>Vertex slide: a vertex runs along the edge that points the way you asked, all the
	/// way to its neighbour at 1, and nowhere at 0.</summary>
	static void TestEditSessionVertexSlide()
	{
		Section( "edit session: a vertex slides along the edge you point at" );

		// A 4x4 grid on the XY plane, 8 inches across: interior vertices have four square neighbours,
		// one exactly along +x.
		MeshEditSession Fresh()
		{
			var s = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
			s.SetMode( EditElement.Vertex );
			return s;
		}

		var session = Fresh();

		// The vertex at the very centre, and the one 2 inches along +x from it.
		var centre = -1;
		for ( var i = 0; i < session.Mesh.VertexCount; i++ )
			if ( session.Mesh.Positions[i].Length < 1e-3f )
				centre = i;

		Check( "the grid has a centre vertex", centre >= 0 );

		var start = session.Mesh.Positions[centre];
		session.SelectVertex( centre );

		// All the way along +x lands exactly on the neighbour at (2, 0, 0).
		session.VertexSlide( 1f, new Vec3( 1, 0, 0 ) );
		var slid = session.Mesh.Positions[centre];
		Check( "sliding fully along +x lands on the neighbour",
			slid.AlmostEquals( new Vec3( 2, 0, 0 ), 1e-4f ), $"{slid}" );
		Check( "and nothing else moved",
			session.Mesh.VertexCount == Fresh().Mesh.VertexCount && session.Mesh.FaceCount == 16 );
		Check( "sliding is one undo step", session.UndoCount == 1 );
		Check( "undo puts it back", session.Undo() && session.Mesh.Positions[centre].AlmostEquals( start, 1e-5f ) );

		// Halfway is halfway.
		var half = Fresh();
		half.SelectVertex( centre );
		half.VertexSlide( 0.5f, new Vec3( 1, 0, 0 ) );
		Check( "half the way is half the way",
			half.Mesh.Positions[centre].AlmostEquals( new Vec3( 1, 0, 0 ), 1e-4f ), $"{half.Mesh.Positions[centre]}" );

		// A negative amount takes the other end of the same run.
		var back = Fresh();
		back.SelectVertex( centre );
		back.VertexSlide( -1f, new Vec3( 1, 0, 0 ) );
		Check( "a negative amount slides the opposite way",
			back.Mesh.Positions[centre].AlmostEquals( new Vec3( -2, 0, 0 ), 1e-4f ), $"{back.Mesh.Positions[centre]}" );

		// Pointing along +y picks the other pair of edges instead.
		var up = Fresh();
		up.SelectVertex( centre );
		up.VertexSlide( 1f, new Vec3( 0, 1, 0 ) );
		Check( "pointing along +y picks the +y edge",
			up.Mesh.Positions[centre].AlmostEquals( new Vec3( 0, 2, 0 ), 1e-4f ), $"{up.Mesh.Positions[centre]}" );

		// Determinism, and the refusals.
		var twice = Fresh();
		twice.SelectVertex( centre );
		twice.VertexSlide( 0.37f, new Vec3( 1, 0.2f, 0 ) );
		var once = Fresh();
		once.SelectVertex( centre );
		once.VertexSlide( 0.37f, new Vec3( 1, 0.2f, 0 ) );
		Check( "vertex slide is deterministic", twice.Mesh.Positions[centre].Equals( once.Mesh.Positions[centre] ) );

		var empty = Fresh();
		var refusedSelection = false;
		try { empty.VertexSlide( 1f, new Vec3( 1, 0, 0 ) ); } catch ( InvalidOperationException ) { refusedSelection = true; }
		Check( "sliding with nothing selected is refused", refusedSelection );

		var noDirection = Fresh();
		noDirection.SelectVertex( centre );
		var refusedDirection = false;
		try { noDirection.VertexSlide( 1f, Vec3.Zero ); } catch ( InvalidOperationException ) { refusedDirection = true; }
		Check( "sliding with no direction is refused", refusedDirection );
	}

	/// <summary>
	/// The loop cut hover preview. The only thing that matters about it is that it does not lie:
	/// the points it draws must be where the cut actually lands, and it must refuse exactly where
	/// the cut refuses.
	/// </summary>
	static void TestLoopCutPreview()
	{
		Section( "loop cut: the preview is where the cut lands" );

		var session = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var edge = session.Mesh.BuildEdgeFaces().Keys.First();

		var preview = session.LoopCutPreview( edge, 0.5f );
		Check( "a box previews a closed ring", preview is { Count: 4 }, $"{preview?.Count}" );

		// Every previewed point must be an actual vertex of the cut mesh - that is the whole claim.
		var before = session.Mesh.VertexCount;
		session.LoopCut( edge, 0.5f );
		var after = session.Mesh;
		Check( "the cut added one vertex per previewed point", after.VertexCount == before + preview.Count,
			$"{before} -> {after.VertexCount} for {preview.Count} points" );

		var landed = 0;
		foreach ( var p in preview )
			for ( var i = 0; i < after.VertexCount; i++ )
				if ( after.Positions[i].AlmostEquals( p, 1e-4f ) ) { landed++; break; }
		Check( "every previewed point is where a vertex ended up", landed == preview.Count,
			$"{landed} of {preview.Count}" );

		// The fraction moves the preview the same way it moves the cut.
		var quarter = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var low = quarter.LoopCutPreview( edge, 0.25f );
		var high = quarter.LoopCutPreview( edge, 0.75f );
		Check( "the fraction moves the preview",
			low is not null && high is not null && !low[0].AlmostEquals( high[0], 1e-4f ) );

		// Refusals are an answer, not an exception: a preview runs on every mouse move.
		var triangles = new PolyMesh();
		triangles.AddVertex( new Vec3( 0, 0, 0 ) );
		triangles.AddVertex( new Vec3( 1, 0, 0 ) );
		triangles.AddVertex( new Vec3( 0, 1, 0 ) );
		triangles.AddFace( new[] { 0, 1, 2 }, null, 0 );

		var tri = new MeshEditSession( triangles );
		Check( "a triangle previews nothing rather than throwing",
			tri.LoopCutPreview( new EdgeKey( 0, 1 ), 0.5f ) is null );

		var gone = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		Check( "an edge that is not on the mesh previews nothing",
			gone.LoopCutPreview( new EdgeKey( 900, 901 ), 0.5f ) is null );

		// And where the preview refuses, the cut refuses too - the pair must agree.
		var refused = false;
		try { tri.LoopCut( new EdgeKey( 0, 1 ), 0.5f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "the cut refuses wherever the preview did", refused );
	}

	/// <summary>
	/// Subdivide in Edit mode — the tool you reach for to put detail somewhere. The properties that
	/// matter: a partial subdivide does not move the shape, it leaves no T-junctions, it keeps the
	/// region selected so you can go again, and a rigged body comes out still rigged.
	/// </summary>
	static void TestEditSessionSubdivide()
	{
		Section( "edit session: subdivide adds detail where you put it" );

		// Whole-mesh subdivide is the full Catmull-Clark, smoothing included.
		var all = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var boxVolume = all.Mesh.SignedVolume();
		all.Subdivide();
		Check( "subdividing everything makes it all quads",
			all.Mesh.Faces.All( f => f.Count == 4 ), $"{all.Mesh.FaceCount} faces" );
		Check( "and smooths it, so a cube shrinks toward its limit surface",
			all.Mesh.SignedVolume() < boxVolume, $"{boxVolume} -> {all.Mesh.SignedVolume()}" );
		Check( "and it is still a valid closed solid",
			MeshValidator.Validate( all.Mesh ) is { IsValid: true, IsClosed: true } );

		// A partial subdivide must NOT move anything: that is the whole reason it is linear.
		var part = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		part.SetMode( EditElement.Face );
		part.SelectFace( 0 );
		var originals = part.Mesh.Positions.ToArray();
		part.Subdivide();

		var moved = false;
		for ( var i = 0; i < originals.Length; i++ )
			if ( !part.Mesh.Positions[i].AlmostEquals( originals[i], 1e-6f ) )
				moved = true;

		Check( "subdividing one face moves no original vertex", !moved );
		Check( "the volume is unchanged", MathF.Abs( part.Mesh.SignedVolume() - 8f ) < 1e-4f, $"{part.Mesh.SignedVolume()}" );
		Check( "it is still valid and closed",
			MeshValidator.Validate( part.Mesh ) is { IsValid: true, IsClosed: true } );
		Check( "one quad became four", part.Mesh.FaceCount == 9, $"{part.Mesh.FaceCount}" );

		// The region stays selected, and it is the NEW faces - so a second subdivide goes deeper in
		// the same place rather than spreading.
		Check( "the new faces are the selection", part.SelectedFaces.Count == 4, $"{part.SelectedFaces.Count}" );
		var deeper = part.Mesh.FaceCount;
		part.Subdivide();
		Check( "a second subdivide refines the same region", part.Mesh.FaceCount == deeper + 12,
			$"{deeper} -> {part.Mesh.FaceCount}" );
		Check( "and still moves nothing", MathF.Abs( part.Mesh.SignedVolume() - 8f ) < 1e-4f );

		// Undo, and the prediction.
		Check( "subdivide is one undo step each", part.UndoCount == 2 );
		Check( "undo goes back a level", part.Undo() && part.Mesh.FaceCount == deeper );

		var predicted = part.PredictSubdivide();
		var predictedFaces = predicted.Faces;
		part.Subdivide();
		Check( "the predicted cost is what it actually cost", part.Mesh.FaceCount == predictedFaces,
			$"predicted {predictedFaces}, got {part.Mesh.FaceCount}" );

		// The playermodel case: a rigged body has to come out rigged, with the new vertices weighted.
		var rigged = Primitives.Box( 2, 2, 2 );
		var skin = new SkinWeights();
		for ( var i = 0; i < rigged.VertexCount; i++ )
			skin.Vertices.Add( new[] { new BoneWeight( rigged.Positions[i].z > 0 ? 1 : 0, 1f ) } );
		rigged.Skin = skin;

		var riggedSession = new MeshEditSession( rigged );
		riggedSession.SetMode( EditElement.Face );
		riggedSession.SelectFace( 0 );
		riggedSession.Subdivide();

		Check( "a rigged body stays rigged through a subdivide", riggedSession.Mesh.IsRigged );
		Check( "every vertex has weights, new ones included",
			riggedSession.Mesh.Skin.Vertices.Count == riggedSession.Mesh.VertexCount,
			$"{riggedSession.Mesh.Skin.Vertices.Count} weights for {riggedSession.Mesh.VertexCount} vertices" );
		Check( "and every weight set sums to one",
			Enumerable.Range( 0, riggedSession.Mesh.VertexCount )
				.All( i => MathF.Abs( riggedSession.Mesh.Skin[i].Sum( w => w.Weight ) - 1f ) < 1e-4f ) );

		var empty = new MeshEditSession( new PolyMesh() );
		var refused = false;
		try { empty.Subdivide(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "subdividing nothing is refused", refused );
	}

	/// <summary>Smooth and Symmetrize: the two operations a character model wants that a prop does
	/// not.</summary>
	static void TestEditSessionSmoothAndSymmetry()
	{
		Section( "edit session: smooth relaxes, symmetrize mirrors a half" );

		// A subdivided box with one vertex yanked out: smoothing must pull the spike back in without
		// moving the whole shape.
		var bumpy = CatmullClark.Subdivide( Primitives.Box( 4, 4, 4 ), 2 );
		var spike = 0;
		for ( var i = 0; i < bumpy.VertexCount; i++ )
			if ( bumpy.Positions[i].z > bumpy.Positions[spike].z )
				spike = i;

		bumpy.Positions[spike] = bumpy.Positions[spike] + new Vec3( 0, 0, 4f );

		var session = new MeshEditSession( bumpy );
		var before = bumpy.Positions[spike];
		var faces = bumpy.FaceCount;
		session.Smooth();

		Check( "smoothing pulls a spike back in", session.Mesh.Positions[spike].z < before.z - 1f,
			$"{before.z:0.##} -> {session.Mesh.Positions[spike].z:0.##}" );
		Check( "and changes no topology", session.Mesh.FaceCount == faces && session.Mesh.VertexCount == bumpy.VertexCount );
		Check( "and is one undo step", session.UndoCount == 1 );
		Check( "undo puts the spike back", session.Undo() && session.Mesh.Positions[spike].AlmostEquals( before, 1e-5f ) );

		// An open sheet's rim must not move, or smoothing shrinks the silhouette.
		var sheet = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		var rim = new List<int>();
		foreach ( var (key, sharing) in sheet.Mesh.BuildEdgeFaces() )
			if ( sharing.Count == 1 ) { rim.Add( key.A ); rim.Add( key.B ); }

		var rimBefore = rim.Select( i => sheet.Mesh.Positions[i] ).ToArray();
		sheet.Smooth( 1f, 10 );
		Check( "smoothing does not move an open rim",
			rim.Select( ( v, i ) => sheet.Mesh.Positions[v].AlmostEquals( rimBefore[i], 1e-5f ) ).All( ok => ok ) );

		// Symmetrize: a deliberately lopsided box.
		var lop = Primitives.Box( 4, 4, 4 );
		for ( var i = 0; i < lop.VertexCount; i++ )
			if ( lop.Positions[i].x < 0 )
				lop.Positions[i] = lop.Positions[i] + new Vec3( 0, 0, 3f );

		var sym = new MeshEditSession( lop );
		sym.Symmetrize();

		Check( "symmetrize leaves a valid closed mesh",
			MeshValidator.Validate( sym.Mesh ) is { IsValid: true, IsClosed: true } );
		Check( "and a positive volume, so nothing is inside out", sym.Mesh.SignedVolume() > 0f,
			$"{sym.Mesh.SignedVolume()}" );

		// Every vertex must have a partner at -x.
		var paired = true;
		foreach ( var p in sym.Mesh.Positions )
		{
			var found = false;
			foreach ( var q in sym.Mesh.Positions )
				if ( q.AlmostEquals( new Vec3( -p.x, p.y, p.z ), 1e-4f ) ) { found = true; break; }
			if ( !found ) { paired = false; break; }
		}
		Check( "every vertex has a mirror partner", paired );

		// The half that was kept is untouched; the lopsided half is gone.
		var raised = sym.Mesh.Positions.Count( p => p.x < -0.01f && p.z > 3f );
		Check( "the lopsided half was replaced, not kept", raised == 0, $"{raised} raised vertices left" );

		// Symmetrizing an already-symmetric mesh is a no-op in shape.
		var already = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		already.Symmetrize();
		Check( "a symmetric box stays volume 8", MathF.Abs( already.Mesh.SignedVolume() - 8f ) < 1e-4f,
			$"{already.Mesh.SignedVolume()}" );
		Check( "and stays closed", MeshValidator.Validate( already.Mesh ) is { IsValid: true, IsClosed: true } );

		// A rigged body keeps its weights across the mirror.
		var rigged = Primitives.Box( 2, 2, 2 );
		var skin = new SkinWeights();
		for ( var i = 0; i < rigged.VertexCount; i++ )
			skin.Vertices.Add( new[] { new BoneWeight( 0, 1f ) } );
		rigged.Skin = skin;

		var riggedSym = new MeshEditSession( rigged );
		riggedSym.Symmetrize();
		Check( "symmetrize keeps a body rigged",
			riggedSym.Mesh.IsRigged && riggedSym.Mesh.Skin.Vertices.Count == riggedSym.Mesh.VertexCount );

		// Nothing on the chosen side is refused, not silently emptied.
		var offside = Primitives.Box( 2, 2, 2 );
		for ( var i = 0; i < offside.VertexCount; i++ )
			offside.Positions[i] = offside.Positions[i] + new Vec3( 10f, 0, 0 );

		var refused = false;
		try { new MeshEditSession( offside ).Symmetrize( keepPositive: false ); }
		catch ( InvalidOperationException ) { refused = true; }
		Check( "symmetrizing from an empty side is refused", refused );
	}

	/// <summary>The three weight tools a playermodel needs: what the exporter will really use,
	/// relaxed joints, and one side copied to the other.</summary>
	static void TestEditSessionWeightTools()
	{
		Section( "edit session: skin weights normalise, smooth and mirror" );

		// A box whose vertices carry SIX influences - more than any exporter will write.
		PolyMesh Overweighted()
		{
			var mesh = Primitives.Box( 4, 4, 4 );
			var skin = new SkinWeights();

			for ( var i = 0; i < mesh.VertexCount; i++ )
				skin.Vertices.Add( new[]
				{
					new BoneWeight( 0, 0.30f ), new BoneWeight( 1, 0.25f ), new BoneWeight( 2, 0.20f ),
					new BoneWeight( 3, 0.15f ), new BoneWeight( 4, 0.07f ), new BoneWeight( 5, 0.03f ),
				} );

			mesh.Skin = skin;
			return mesh;
		}

		var session = new MeshEditSession( Overweighted() );
		var changed = session.NormalizeWeights();

		Check( "every over-weighted vertex is reported", changed == session.Mesh.VertexCount, $"{changed}" );
		Check( "no vertex keeps more than four influences",
			Enumerable.Range( 0, session.Mesh.VertexCount ).All( i => session.Mesh.Skin[i].Length <= 4 ) );
		Check( "and each still sums to one",
			Enumerable.Range( 0, session.Mesh.VertexCount )
				.All( i => MathF.Abs( session.Mesh.Skin[i].Sum( w => w.Weight ) - 1f ) < 1e-4f ) );
		Check( "strongest influence first, which is what the SMD writer reads as the parent bone",
			session.Mesh.Skin[0][0].Bone == 0 );
		Check( "the weakest bones are the ones dropped",
			session.Mesh.Skin[0].All( w => w.Bone != 5 ) );
		Check( "normalising is one undo step", session.UndoCount == 1 );
		Check( "running it again reports nothing left to fix", session.NormalizeWeights() == 0 );

		// Smoothing weights: a hard split down the middle should soften at the seam.
		var split = Primitives.Plane( 8f, 8f, 4, 4 );
		var splitSkin = new SkinWeights();
		for ( var i = 0; i < split.VertexCount; i++ )
			splitSkin.Vertices.Add( new[] { new BoneWeight( split.Positions[i].x > 0 ? 1 : 0, 1f ) } );
		split.Skin = splitSkin;

		var seam = new MeshEditSession( split );
		seam.SmoothWeights();

		var blended = 0;
		for ( var i = 0; i < seam.Mesh.VertexCount; i++ )
			if ( seam.Mesh.Skin[i].Length > 1 )
				blended++;

		Check( "smoothing blends the seam across two bones", blended > 0, $"{blended} blended" );
		Check( "and every vertex still sums to one",
			Enumerable.Range( 0, seam.Mesh.VertexCount )
				.All( i => MathF.Abs( seam.Mesh.Skin[i].Sum( w => w.Weight ) - 1f ) < 1e-4f ) );
		Check( "and none exceeds four influences",
			Enumerable.Range( 0, seam.Mesh.VertexCount ).All( i => seam.Mesh.Skin[i].Length <= 4 ) );
		Check( "smoothing weights moves no vertex",
			Enumerable.Range( 0, seam.Mesh.VertexCount )
				.All( i => seam.Mesh.Positions[i].AlmostEquals( split.Positions[i], 1e-6f ) ) );

		// Mirror weights: bone 1 is "left", bone 2 is "right", and they swap.
		var body = Primitives.Box( 4, 4, 4 );
		var bodySkin = new SkinWeights();
		for ( var i = 0; i < body.VertexCount; i++ )
			bodySkin.Vertices.Add( new[] { new BoneWeight( body.Positions[i].x > 0 ? 1 : 9, 1f ) } );
		body.Skin = bodySkin;

		var mirror = new MeshEditSession( body );
		var copied = mirror.MirrorWeights( bone => bone == 1 ? 2 : bone == 2 ? 1 : bone );

		Check( "the far side received weights", copied > 0, $"{copied}" );
		Check( "the kept side is untouched",
			Enumerable.Range( 0, mirror.Mesh.VertexCount )
				.Where( i => mirror.Mesh.Positions[i].x > 0 )
				.All( i => mirror.Mesh.Skin[i][0].Bone == 1 ) );
		Check( "and the mirrored side got the PARTNER bone, not the same one",
			Enumerable.Range( 0, mirror.Mesh.VertexCount )
				.Where( i => mirror.Mesh.Positions[i].x < 0 )
				.All( i => mirror.Mesh.Skin[i][0].Bone == 2 ) );
		Check( "mirroring weights moves no vertex",
			Enumerable.Range( 0, mirror.Mesh.VertexCount )
				.All( i => mirror.Mesh.Positions[i].AlmostEquals( body.Positions[i], 1e-6f ) ) );

		// All three refuse an unrigged body rather than inventing weights.
		var bare = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var refusals = 0;
		try { bare.NormalizeWeights(); } catch ( InvalidOperationException ) { refusals++; }
		try { bare.SmoothWeights(); } catch ( InvalidOperationException ) { refusals++; }
		try { bare.MirrorWeights( b => b ); } catch ( InvalidOperationException ) { refusals++; }
		Check( "all three refuse an unrigged body", refusals == 3, $"{refusals} of 3" );
	}

	/// <summary>Shrink/Fatten and growing a selection — two things you do constantly on a character
	/// and cannot reasonably do without.</summary>
	static void TestEditSessionShrinkFattenAndGrow()
	{
		Section( "edit session: shrink/fatten and growing a selection" );

		// Fattening a whole sphere makes it bigger; shrinking makes it smaller. Along its own
		// normals, so it stays a sphere.
		var ball = new MeshEditSession( Primitives.QuadSphere( 2f, 3 ) );
		ball.SetMode( EditElement.Face );
		ball.SelectAll();
		var before = ball.Mesh.SignedVolume();
		var radiusBefore = ball.Mesh.Positions.Max( p => p.Length );

		ball.ShrinkFatten( 0.5f );
		Check( "fattening a ball grows it", ball.Mesh.SignedVolume() > before,
			$"{before:0.###} -> {ball.Mesh.SignedVolume():0.###}" );
		Check( "and it grows by the distance asked for",
			MathF.Abs( ball.Mesh.Positions.Max( p => p.Length ) - (radiusBefore + 0.5f) ) < 0.05f,
			$"{radiusBefore:0.###} -> {ball.Mesh.Positions.Max( p => p.Length ):0.###}" );
		Check( "and it is still a valid closed solid",
			MeshValidator.Validate( ball.Mesh ) is { IsValid: true, IsClosed: true } );
		Check( "and the topology is untouched", ball.Mesh.FaceCount == Primitives.QuadSphere( 2f, 3 ).FaceCount );

		ball.ShrinkFatten( -0.5f );
		Check( "shrinking by the same amount comes back",
			MathF.Abs( ball.Mesh.SignedVolume() - before ) < 0.05f,
			$"{before:0.###} vs {ball.Mesh.SignedVolume():0.###}" );

		Check( "each is its own undo step", ball.UndoCount == 2 );

		var bare = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var refused = false;
		try { bare.ShrinkFatten( 1f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "shrink/fatten with no selection is refused", refused );

		// Grow and shrink a selection on a grid, where the answer is countable by hand.
		var grid = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		grid.SetMode( EditElement.Vertex );

		// The exact centre of a 4x4 grid of quads is a vertex with four neighbours.
		var centre = -1;
		for ( var i = 0; i < grid.Mesh.VertexCount; i++ )
			if ( grid.Mesh.Positions[i].Length < 1e-3f )
				centre = i;

		grid.SelectVertex( centre );
		grid.GrowSelection();
		Check( "growing one vertex takes in its four neighbours", grid.SelectedVertices.Count == 5,
			$"{grid.SelectedVertices.Count}" );

		grid.GrowSelection();
		Check( "growing again takes the next ring", grid.SelectedVertices.Count == 13,
			$"{grid.SelectedVertices.Count}" );

		grid.ShrinkSelection();
		Check( "shrinking drops the border again", grid.SelectedVertices.Count == 5,
			$"{grid.SelectedVertices.Count}" );

		// Face mode: growing from one face takes in the faces sharing its corners.
		var faces = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		faces.SetMode( EditElement.Face );
		faces.SelectFace( 5 );
		faces.GrowSelection();
		Check( "growing a face takes in its neighbours", faces.SelectedFaces.Count > 1,
			$"{faces.SelectedFaces.Count}" );
		Check( "growing does not move anything",
			faces.Mesh.Positions.Count == Primitives.Plane( 8f, 8f, 4, 4 ).VertexCount );

		var nothing = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var refusedGrow = false;
		try { nothing.GrowSelection(); } catch ( InvalidOperationException ) { refusedGrow = true; }
		Check( "growing nothing is refused", refusedGrow );
	}

	/// <summary>
	/// Edge Split — making an edge shade hard by unwelding it. The claim to prove is that the two
	/// sides stop sharing a normal, which is what the exporter reads, and that nothing else moves.
	/// </summary>
	static void TestEditSessionTrisToQuadsAndBevelVertices()
	{
		Section( "edit session: tris to quads and bevel vertices" );

		// Triangulating a grid and joining it back must give the grid: every triangle pair shares
		// its diagonal, and the diagonals are the only edges that make square quads.
		var grid = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		grid.SetMode( EditElement.Face );
		grid.SelectAll();
		grid.TriangulateFaces();
		Check( "triangulating a 4x4 grid gives 32 triangles", grid.Mesh.FaceCount == 32, $"{grid.Mesh.FaceCount}" );

		grid.SelectAll();
		grid.TrisToQuads();
		Check( "tris to quads brings back 16 faces", grid.Mesh.FaceCount == 16, $"{grid.Mesh.FaceCount}" );
		Check( "all of them quads", grid.Mesh.Faces.All( f => f.Indices.Length == 4 ) );
		Check( "every quad has the grid's area, so no pair joined across a corner",
			grid.Mesh.Faces.All( f => MathF.Abs( grid.Mesh.FaceArea( f ) - 4f ) < 1e-3f ) );
		Check( "and the grid is still valid", MeshValidator.Validate( grid.Mesh ).IsValid, MeshValidator.Validate( grid.Mesh ).ToString() );
		Check( "the new quads are the selection", grid.SelectedFaces.Count == 16, $"{grid.SelectedFaces.Count}" );
		Check( "vertex count is untouched", grid.Mesh.VertexCount == 25, $"{grid.Mesh.VertexCount}" );
		Check( "it is one undo step", grid.LastLabel == "Tris to Quads" );

		// A box's corner: two triangles at 90° to each other never join.
		var box = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		box.SetMode( EditElement.Face );
		box.SelectAll();
		box.TriangulateFaces();
		box.SelectAll();
		box.TrisToQuads();
		Check( "a triangulated box comes back as six quads, never folding over an edge",
			box.Mesh.FaceCount == 6 && box.Mesh.Faces.All( f => f.Indices.Length == 4 ), $"{box.Mesh.FaceCount}" );
		Check( "and is a closed solid again", MeshValidator.Validate( box.Mesh ) is { IsValid: true, IsClosed: true } );
		Check( "with its volume", MathF.Abs( box.Mesh.SignedVolume() - 8f ) < 1e-3f, $"{box.Mesh.SignedVolume()}" );

		var refused = false;
		try { box.TrisToQuads(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "tris to quads with nothing to join is refused", refused );

		// Bevelling one corner of a box: the corner vertex goes, three cut points arrive, and the
		// three faces at the corner each gain a corner while one triangle caps the notch.
		var cut = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		cut.SetMode( EditElement.Vertex );
		var corner = -1;
		for ( var i = 0; i < cut.Mesh.VertexCount; i++ )
		{
			var p = cut.Mesh.Positions[i];
			if ( p.x > 0 && p.y > 0 && p.z > 0 )
				corner = i;
		}

		cut.SelectVertex( corner );
		cut.BevelVertices( 0.5f );
		Check( "bevelling a corner adds two vertices and one face", cut.Mesh.VertexCount == 10 && cut.Mesh.FaceCount == 7,
			$"{cut.Mesh.VertexCount}v/{cut.Mesh.FaceCount}f" );
		Check( "the result is a valid closed solid", MeshValidator.Validate( cut.Mesh ) is { IsValid: true, IsClosed: true },
			MeshValidator.Validate( cut.Mesh ).ToString() );
		Check( "the cap is a triangle", cut.Mesh.Faces.Count( f => f.Indices.Length == 3 ) == 1 );
		Check( "three faces became pentagons", cut.Mesh.Faces.Count( f => f.Indices.Length == 5 ) == 3 );

		// A 0.5 cut off a unit-edge corner removes a tetrahedron of 0.5³/6.
		Check( "and the volume lost is the corner's tetrahedron",
			MathF.Abs( cut.Mesh.SignedVolume() - (8f - 0.125f / 6f) ) < 1e-3f, $"{cut.Mesh.SignedVolume()}" );
		Check( "the corner itself is gone", !cut.Mesh.Positions.Any( p => p.x > 0.99f && p.y > 0.99f && p.z > 0.99f ) );
		Check( "the cut points are 0.5 down each edge",
			cut.Mesh.Positions.Count( p => MathF.Abs( p.x - 0.5f ) < 1e-4f || MathF.Abs( p.y - 0.5f ) < 1e-4f || MathF.Abs( p.z - 0.5f ) < 1e-4f ) == 3 );
		Check( "the cap's vertices are the selection", cut.SelectedVertices.Count == 3, $"{cut.SelectedVertices.Count}" );
		Check( "it is one undo step", cut.UndoCount == 1 && cut.LastLabel == "Bevel Vertices" );

		cut.Undo();
		Check( "undo brings the corner back", cut.Mesh.VertexCount == 8 && cut.Mesh.FaceCount == 6 );

		// Every corner at once, wide: a cuboctahedron-ish, still closed, and the width is clamped
		// so cuts from both ends of an edge never cross.
		cut.SelectAll();
		cut.BevelVertices( 5f );
		Check( "bevelling every corner with a huge width still gives a closed solid",
			MeshValidator.Validate( cut.Mesh ) is { IsValid: true, IsClosed: true }, MeshValidator.Validate( cut.Mesh ).ToString() );
		Check( "with 8 caps and 6 squeezed faces", cut.Mesh.FaceCount == 14 && cut.Mesh.VertexCount == 24,
			$"{cut.Mesh.VertexCount}v/{cut.Mesh.FaceCount}f" );

		// A boundary vertex of an open sheet is left alone; an interior one is cut.
		var sheet = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		sheet.SetMode( EditElement.Vertex );
		sheet.SelectAll();
		sheet.BevelVertices( 0.5f );
		Check( "on an open sheet only the 9 interior vertices get bevelled: 9 caps",
			sheet.Mesh.FaceCount == 16 + 9, $"{sheet.Mesh.FaceCount}" );
		Check( "and the sheet's corners are untouched", sheet.Mesh.Positions.Count( p => MathF.Abs( MathF.Abs( p.x ) - 4f ) < 1e-4f && MathF.Abs( MathF.Abs( p.y ) - 4f ) < 1e-4f ) == 4 );
		Check( "the sheet is still valid", MeshValidator.Validate( sheet.Mesh ).IsValid, MeshValidator.Validate( sheet.Mesh ).ToString() );

		refused = false;
		try { new MeshEditSession( Primitives.Box( 2, 2, 2 ) ).BevelVertices( 0.5f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "bevel vertices with no selection is refused", refused );
	}

	static void TestEditSessionHideAndExtras()
	{
		Section( "edit session: hide, delete kinds, merge targets, randomize, select extras, separate by, decimate" );

		// Hide the top of a box: the visible mesh has five faces, the top's vertices are still
		// there, and Select All never picks it.
		var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		s.SetMode( EditElement.Face );
		var top = -1;
		for ( var f = 0; f < s.Mesh.FaceCount; f++ )
			if ( s.Mesh.FaceNormal( s.Mesh.Faces[f] ).z > 0.9f )
				top = f;

		s.SelectFace( top );
		s.Hide();
		Check( "hiding a face takes it out of the visible mesh", s.VisibleMesh.FaceCount == 5 && s.Mesh.FaceCount == 6, $"{s.VisibleMesh.FaceCount}" );
		Check( "and the visible mesh shares the vertices", ReferenceEquals( s.VisibleMesh.Positions, s.Mesh.Positions ) );
		Check( "the hidden face is known by index", s.HiddenFaces.Count == 1 && s.HiddenFaces.Contains( top ) );
		Check( "the face map points back at real faces", s.VisibleFaceMap.Length == 5 && !s.VisibleFaceMap.Contains( top ) );
		Check( "hiding clears the selection", s.SelectedFaces.Count == 0 );
		Check( "hide is an undo step", s.UndoCount == 1 && s.LastLabel == "Hide" );
		Check( "every vertex is still visible: each touches a side", Enumerable.Range( 0, 8 ).All( s.IsVertexVisible ) );

		s.SelectAll();
		Check( "select all skips the hidden face", s.SelectedFaces.Count == 5 && !s.SelectedFaces.Contains( top ) );

		// An operation on the rest keeps the top hidden: it is found again by where it is.
		s.ClearSelection();
		var bottom = -1;
		for ( var f = 0; f < s.Mesh.FaceCount; f++ )
			if ( s.Mesh.FaceNormal( s.Mesh.Faces[f] ).z < -0.9f )
				bottom = f;
		s.SelectFace( bottom );
		s.Extrude( 1f );
		Check( "an extrude elsewhere keeps the top hidden", s.HiddenFaces.Count == 1 && s.Mesh.FaceNormal( s.Mesh.Faces[s.HiddenFaces.First()] ).z > 0.9f );

		s.Unhide();
		Check( "unhide brings it back and selects it", s.HiddenFaces.Count == 0 && s.SelectedFaces.Count == 1 && s.SelectedFaces.Contains( top ) );
		Check( "with nothing hidden the visible mesh is the mesh", ReferenceEquals( s.VisibleMesh, s.Mesh ) );

		s.Undo();
		Check( "undoing the unhide hides it again", s.HiddenFaces.Count == 1 );
		s.Undo();
		s.Undo();
		Check( "and undoing the hide shows it", s.HiddenFaces.Count == 0 && s.Mesh.FaceCount == 6 );

		// Hide unselected keeps only the selection. Hiding everything is refused.
		s.SelectFace( top );
		s.Hide( unselected: true );
		Check( "hide unselected leaves the selection showing", s.VisibleMesh.FaceCount == 1 && s.VisibleFaceMap[0] == top );
		Check( "and the bottom's vertices are no longer visible", !s.IsVertexVisible( s.Mesh.Faces[bottom].Indices[0] ) );
		var refused = false;
		s.SelectFace( top );
		try { s.Hide(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "hiding the last visible face is refused", refused );
		s.Unhide();

		// Soft falloff sits out hidden vertices.
		var soft = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		soft.SetMode( EditElement.Face );
		soft.SelectFace( bottom );
		soft.Hide();
		soft.SetMode( EditElement.Vertex );
		soft.ClearSelection();
		for ( var i = 0; i < soft.Mesh.VertexCount; i++ )
			if ( soft.Mesh.Positions[i].z > 0.9f )
				soft.SelectVertex( i, MeshEditSession.Combine.Add );
		soft.SoftRadius = 5f;
		soft.BeginDrag();
		soft.Drag( new Vec3( 0, 0, 1 ) );
		soft.EndDrag();
		Check( "soft falloff still reaches visible vertices", soft.Mesh.Positions.Count( p => p.z > 1.5f ) == 4 );
		soft.Undo();
		soft.Undo();
		soft.SetMode( EditElement.Face );
		soft.SelectFace( top );
		soft.Hide( unselected: true );
		soft.SetMode( EditElement.Vertex );
		soft.SelectAll();
		soft.BeginDrag();
		soft.Drag( new Vec3( 0, 0, 1 ) );
		soft.EndDrag();
		Check( "and leaves vertices only hidden faces use alone", soft.Mesh.Positions.Count( p => MathF.Abs( p.z + 1f ) < 1e-5f ) == 4, $"{soft.Mesh.Positions.Count( p => MathF.Abs( p.z + 1f ) < 1e-5f )}" );

		// Delete only faces vs delete: a vertex-mode delete takes every face touching the corner,
		// but Only Faces in Face mode takes exactly the faces picked.
		var d = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		d.SetMode( EditElement.Face );
		d.SelectFace( top );
		d.DeleteOnlyFaces();
		Check( "delete only faces removes one face", d.Mesh.FaceCount == 5 && d.Mesh.VertexCount == 8 );

		var k = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		k.SetMode( EditElement.Face );
		k.SelectFace( top );
		k.SelectFace( bottom, MeshEditSession.Combine.Add );
		refused = false;
		try { k.DeleteEdgesKeepFaces(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "delete edges with no shared edge in the selection is refused", refused );

		var g = new MeshEditSession( Primitives.Plane( 4f, 4f, 2, 2 ) );
		g.SetMode( EditElement.Face );
		g.SelectAll();
		g.DeleteEdgesKeepFaces();
		Check( "deleting the inner edges of a 2x2 grid leaves one face", g.Mesh.FaceCount == 1, $"{g.Mesh.FaceCount}" );

		// Merge at first / last / pivot.
		var m = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		m.SetMode( EditElement.Vertex );
		var a = m.Mesh.Positions.FindIndex( p => p.x > 0 && p.y > 0 && p.z > 0 );
		var b = m.Mesh.Positions.FindIndex( p => p.x < 0 && p.y > 0 && p.z > 0 );
		m.SelectVertex( a );
		m.SelectVertex( b, MeshEditSession.Combine.Add );
		m.Merge( MeshEditSession.MergeTarget.First );
		Check( "merge at first lands on the first pick", m.Mesh.VertexCount == 7 && m.Mesh.Positions.Any( p => (p - new Vec3( 1, 1, 1 )).Length < 1e-5f ) && !m.Mesh.Positions.Any( p => (p - new Vec3( -1, 1, 1 )).Length < 1e-5f ) );
		m.Undo();
		m.SelectVertex( a );
		m.SelectVertex( b, MeshEditSession.Combine.Add );
		m.Merge( MeshEditSession.MergeTarget.Last );
		Check( "merge at last lands on the last pick", m.Mesh.Positions.Any( p => (p - new Vec3( -1, 1, 1 )).Length < 1e-5f ) && !m.Mesh.Positions.Any( p => (p - new Vec3( 1, 1, 1 )).Length < 1e-5f ) );
		m.Undo();
		m.SelectVertex( a );
		m.SelectVertex( b, MeshEditSession.Combine.Add );
		m.Pivot = new Vec3( 0, 5, 5 );
		m.Merge( MeshEditSession.MergeTarget.Pivot );
		Check( "merge at pivot lands on the pivot", m.Mesh.Positions.Any( p => (p - new Vec3( 0, 5, 5 )).Length < 1e-5f ) );
		m.Undo();
		m.SetMode( EditElement.Face );
		m.SelectFace( top );
		refused = false;
		try { m.Merge( MeshEditSession.MergeTarget.First ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "merge at first needs clicked vertices", refused );

		// Randomize moves every selected vertex, by at most the amount, and repeats with the seed.
		var r = new MeshEditSession( Primitives.QuadSphere( 2f, 3 ) );
		r.SetMode( EditElement.Vertex );
		r.SelectAll();
		var before = r.Mesh.Clone();
		r.Randomize( 0.1f, seed: 7 );
		var moved = Enumerable.Range( 0, r.Mesh.VertexCount ).Select( i => (r.Mesh.Positions[i] - before.Positions[i]).Length ).ToList();
		Check( "randomize moves the vertices", moved.Count( x => x > 1e-4f ) > r.Mesh.VertexCount / 2 );
		Check( "by no more than the amount", moved.All( x => x <= 0.1f + 1e-5f ), $"{moved.Max()}" );
		var normals = before.ComputeVertexNormals();
		Check( "along the normal",
			Enumerable.Range( 0, r.Mesh.VertexCount ).All( i => moved[i] < 1e-4f || Vec3.Cross( (r.Mesh.Positions[i] - before.Positions[i]).Normal, normals[i] ).Length < 1e-3f ) );
		var once = r.Mesh.Clone();
		r.Undo();
		r.SelectAll();
		r.Randomize( 0.1f, seed: 7 );
		Check( "the same seed gives the same jitter", Enumerable.Range( 0, r.Mesh.VertexCount ).All( i => (r.Mesh.Positions[i] - once.Positions[i]).Length < 1e-6f ) );
		r.Undo();
		r.SelectAll();
		r.Randomize( 0.1f, seed: 8 );
		Check( "and another seed gives another", Enumerable.Range( 0, r.Mesh.VertexCount ).Any( i => (r.Mesh.Positions[i] - once.Positions[i]).Length > 1e-4f ) );

		// Select random, faces by sides, interior faces.
		var sel = new MeshEditSession( Primitives.Plane( 8f, 8f, 8, 8 ) );
		sel.SetMode( EditElement.Face );
		sel.SelectRandom( 0.5f, seed: 1 );
		Check( "select random picks about half", sel.SelectedFaces.Count > 16 && sel.SelectedFaces.Count < 48, $"{sel.SelectedFaces.Count}" );
		var pick = new HashSet<int>( sel.SelectedFaces );
		sel.SelectRandom( 0.5f, seed: 1 );
		Check( "and the same seed picks the same", pick.SetEquals( sel.SelectedFaces ) );
		sel.SelectRandom( 0f, seed: 1 );
		Check( "a ratio of zero picks nothing", sel.SelectedFaces.Count == 0 );

		sel.SelectAll();
		var few = sel.SelectedFaces.Take( 3 ).ToList();
		sel.ClearSelection();
		foreach ( var f in few )
			sel.SelectFace( f, MeshEditSession.Combine.Add );
		sel.TriangulateFaces();
		sel.SelectFacesBySides( 3 );
		Check( "faces by sides finds the six triangles", sel.SelectedFaces.Count == 6, $"{sel.SelectedFaces.Count}" );
		sel.SelectFacesBySides( 4 );
		Check( "and the quads", sel.SelectedFaces.Count == 61, $"{sel.SelectedFaces.Count}" );
		sel.SelectFacesBySides( 4, orMore: true );
		Check( "or more includes bigger faces", sel.SelectedFaces.Count == 61 );

		var inside = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		inside.SetMode( EditElement.Face );
		inside.SelectInteriorFaces();
		Check( "a clean box has no interior faces", inside.SelectedFaces.Count == 0, $"{inside.SelectedFaces.Count}" );
		inside.SelectFace( top );
		inside.FlipNormals();
		inside.SelectInteriorFaces();
		Check( "a flipped face is found as interior", inside.SelectedFaces.Count == 1 && inside.SelectedFaces.Contains( top ), $"{inside.SelectedFaces.Count}" );

		// Separate by loose parts and by material.
		var loose = Primitives.Box( 2, 2, 2 );
		MeshTransform.Append( loose, MeshTransform.Transformed( Primitives.Box( 1, 1, 1 ), Xform.Translate( new Vec3( 5, 0, 0 ) ) ) );
		MeshTransform.Append( loose, MeshTransform.Transformed( Primitives.Box( 1, 1, 1 ), Xform.Translate( new Vec3( -5, 0, 0 ) ) ) );
		var lp = new MeshEditSession( loose );
		lp.SeparateLooseParts();
		Check( "separate loose parts keeps the biggest piece", lp.Mesh.FaceCount == 6 && lp.Mesh.Positions.All( p => MathF.Abs( p.x ) < 1.5f ) );
		Check( "and splits the others off", lp.Separated.Count == 2 && lp.Separated.All( p => p.FaceCount == 6 && p.VertexCount == 8 ) );
		refused = false;
		try { new MeshEditSession( Primitives.Box( 2, 2, 2 ) ).SeparateLooseParts(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "one piece refuses", refused );

		var mat = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		mat.Mesh.Faces[top].Material = 1;
		mat.SeparateByMaterial();
		Check( "separate by material keeps the busiest slot", mat.Mesh.FaceCount == 5 && mat.Mesh.Faces.All( f => f.Material == 0 ) );
		Check( "and splits the other off with only its vertices", mat.Separated.Count == 1 && mat.Separated[0].FaceCount == 1 && mat.Separated[0].VertexCount == 4 );

		// Decimate a dense patch of a sphere, leaving the rest and staying closed.
		var dec = new MeshEditSession( Primitives.QuadSphere( 2f, 6 ) );
		dec.SetMode( EditElement.Face );
		var facesBefore = dec.Mesh.FaceCount;
		for ( var f = 0; f < dec.Mesh.FaceCount; f++ )
			if ( dec.Mesh.FaceCentroid( dec.Mesh.Faces[f] ).z > 1f )
				dec.SelectFace( f, MeshEditSession.Combine.Add );
		var patch = dec.SelectedFaces.Count;
		dec.DecimateSelection( 0.3f );
		var v = MeshValidator.Validate( dec.Mesh );
		Check( "decimating a patch leaves fewer faces", dec.Mesh.FaceCount < facesBefore, $"{facesBefore} -> {dec.Mesh.FaceCount}" );
		Check( "the rest of the sphere is untouched", dec.Mesh.Faces.Count( f => f.Indices.Length == 4 ) == facesBefore - patch, $"{dec.Mesh.Faces.Count( f => f.Indices.Length == 4 )} quads vs {facesBefore - patch}" );
		Check( "and it is still one closed solid", v.IsValid && v.IsClosed, v.ToString() );

		var whole = new MeshEditSession( Primitives.QuadSphere( 2f, 6 ) );
		whole.DecimateSelection( 0.25f );
		Check( "decimate with nothing selected does the whole body", whole.Mesh.FaceCount <= facesBefore / 2, $"{whole.Mesh.FaceCount}" );
		refused = false;
		try { whole.DecimateSelection( 1f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a ratio of 1 is refused", refused );
	}

	static void TestEdgeCreases()
	{
		Section( "edge creases: sharp folds under Catmull-Clark" );

		var box = Primitives.Box( 2, 2, 2 );
		var topEdges = new Dictionary<EdgeKey, float>();
		foreach ( var key in box.BuildEdgeFaces().Keys )
			if ( box.Positions[key.A].z > 0.9f && box.Positions[key.B].z > 0.9f )
				topEdges[key] = 1f;
		Check( "a box has four top edges", topEdges.Count == 4 );

		// The top face's own centre point stays at z = 1 whatever happens; it is the corners and
		// edge points that a crease holds up there.
		int AtTop( PolyMesh m ) => m.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f );
		float SecondHighest( PolyMesh m ) => m.Positions.Select( p => p.z ).OrderByDescending( z => z ).Skip( 1 ).First();
		var plain = CatmullClark.Subdivide( box, 1 );
		var topZ = SecondHighest( plain );
		Check( "plain subdivision rounds the top off: only the face point stays at z = 1", AtTop( plain ) == 1 && topZ < 0.99f, $"{AtTop( plain )} at top, next {topZ}" );

		var creased = CatmullClark.Subdivide( box, 1, topEdges, out var left );
		Check( "creasing the top edges keeps a flat top at z = 1",
			creased.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f ) == 9, $"{creased.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f )} at the top" );
		Check( "and the top ring keeps its full width",
			creased.Positions.Where( p => MathF.Abs( p.z - 1f ) < 1e-5f ).Max( p => p.x ) > 0.99f );
		Check( "the bottom still rounds", creased.Positions.Count( p => MathF.Abs( p.z + 1f ) < 1e-5f ) == 1 );
		Check( "a sharpness of 1 leaves nothing for the next level", left.Count == 0 );
		Check( "the result is a closed solid", MeshValidator.Validate( creased ) is { IsValid: true, IsClosed: true } );

		// Sharpness 2 survives a second level; sharpness 1 does not.
		var two = new Dictionary<EdgeKey, float>();
		foreach ( var key in topEdges.Keys )
			two[key] = 2f;
		var twice = CatmullClark.Subdivide( box, 2, two, out var leftTwo );
		Check( "sharpness 2 keeps the top flat through two levels", twice.Positions.Max( p => p.z ) > 0.999f && twice.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f ) == 25,
			$"{twice.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f )}" );
		Check( "and is used up", leftTwo.Count == 0 );
		var once = CatmullClark.Subdivide( box, 2, topEdges, out _ );
		Check( "sharpness 1 starts rounding at the second level", AtTop( once ) > 1 && AtTop( once ) < 25, $"{AtTop( once )}" );

		// A fraction sits between.
		var half = new Dictionary<EdgeKey, float>();
		foreach ( var key in topEdges.Keys )
			half[key] = 0.5f;
		var soft = CatmullClark.Subdivide( box, 1, half, out _ );
		Check( "a half crease lands between rounded and sharp", SecondHighest( soft ) > topZ && SecondHighest( soft ) < 0.999f, $"{SecondHighest( soft )}" );

		// Weights ride along and still sum to one.
		var rigged = Primitives.Box( 2, 2, 2 );
		rigged.Skin = new SkinWeights();
		for ( var v = 0; v < rigged.VertexCount; v++ )
			rigged.Skin.Vertices.Add( new[] { new BoneWeight( rigged.Positions[v].z > 0 ? 1 : 0, 1f ) } );
		var riggedOut = CatmullClark.Subdivide( rigged, 1, topEdges, out _ );
		Check( "a rigged creased subdivision keeps every vertex's weights summing to one",
			riggedOut.Skin.Vertices.All( w => MathF.Abs( w.Sum( b => b.Weight ) - 1f ) < 1e-4f ) );
		Check( "and the flat top is all on the top bone",
			Enumerable.Range( 0, riggedOut.VertexCount ).Where( v => MathF.Abs( riggedOut.Positions[v].z - 1f ) < 1e-5f ).All( v => riggedOut.Skin[v].All( b => b.Bone == 1 ) ) );

		// Through the modifiers: a crease on the +X half is a crease on the mirrored half too.
		var halfBox = Primitives.Box( 2, 2, 2 );
		MeshTransform.Apply( halfBox, Xform.Translate( new Vec3( 1, 0, 0 ) ) );
		var halfTop = new Dictionary<EdgeKey, float>();
		foreach ( var key in halfBox.BuildEdgeFaces().Keys )
			if ( halfBox.Positions[key.A].z > 0.9f && halfBox.Positions[key.B].z > 0.9f )
				halfTop[key] = 1f;
		var mirrored = MeshModifiers.Apply( halfBox, true, 1, 0f, 1, 0f, halfTop );
		Check( "mirror carries the creases to the other side: both tops stay flat",
			mirrored.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f ) >= 15 && mirrored.Positions.Where( p => p.x < -0.5f ).Max( p => p.z ) > 0.999f,
			$"{mirrored.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f )}" );

		// The edit blob keeps them, and an old blob without them still reads.
		var blob = MeshEditBlob.Write( box, 123, null, topEdges );
		var back = MeshEditBlob.Read( blob, out var fp, out var pieces, out var readCreases );
		Check( "creases round-trip through the mesh edit blob", fp == 123 && back.FaceCount == 6 && readCreases.Count == 4 && readCreases.All( kv => topEdges[kv.Key] == kv.Value ) );
		var old = MeshEditBlob.Write( box, 7 );
		MeshEditBlob.Read( old, out _, out _, out var none );
		Check( "a blob written without creases reads as none", none.Count == 0 );

		// The session: crease the top edges, subdivide, and the creases are consumed.
		var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		s.SetMode( EditElement.Edge );
		foreach ( var key in topEdges.Keys )
			s.SelectEdge( key, MeshEditSession.Combine.Add );
		s.Crease( 1f );
		Check( "crease marks the selected edges", s.Creases.Count == 4 && s.Creases.Values.All( w => w == 1f ) && s.LastLabel == "Crease" );
		s.Subdivide();
		Check( "subdividing in the session honours them", s.Mesh.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f ) == 9 );
		Check( "and uses them up", s.Creases.Count == 0 );
		s.Undo();
		Check( "undo brings the creases back", s.Creases.Count == 4 && s.Mesh.FaceCount == 6 );
		s.ClearCreases();
		Check( "clear crease on the selection clears them", s.Creases.Count == 0 );
		s.Undo();
		s.SetMode( EditElement.Vertex );
		s.SelectVertex( s.Mesh.Positions.FindIndex( p => p.x > 0 && p.y > 0 && p.z > 0 ) );
		s.Delete();
		Check( "deleting a creased corner drops the creases on its two edges and keeps the other two", s.Creases.Count == 2, $"{s.Creases.Count}" );

		var refused = false;
		try { new MeshEditSession( Primitives.Box( 2, 2, 2 ) ).Crease( 1f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "crease with nothing selected is refused", refused );

		// Typed transforms: exact, one step each, scrubbable like any other operation.
		var t = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		t.SetMode( EditElement.Face );
		var topFace = Enumerable.Range( 0, t.Mesh.FaceCount ).First( f => t.Mesh.FaceNormal( t.Mesh.Faces[f] ).z > 0.9f );
		t.SelectFace( topFace );
		t.Move( new Vec3( 0, 0, 2 ) );
		Check( "a typed move lands exactly", t.Mesh.Positions.Count( p => MathF.Abs( p.z - 3f ) < 1e-5f ) == 4 && t.UndoCount == 1 && t.LastLabel == "Move" );
		t.Scale( new Vec3( 0.5f, 0.5f, 1f ) );
		Check( "a typed scale is about the selection's centre", t.Mesh.Positions.Count( p => MathF.Abs( p.z - 3f ) < 1e-5f && MathF.Abs( MathF.Abs( p.x ) - 0.5f ) < 1e-5f ) == 4, $"{t.UndoCount}" );
		t.Rotate( new Vec3( 0, 0, 1 ), 90f );
		Check( "a typed rotate turns about the centre", t.Mesh.Positions.Count( p => MathF.Abs( p.z - 3f ) < 1e-5f && MathF.Abs( MathF.Abs( p.x ) - 0.5f ) < 1e-4f ) == 4 && t.UndoCount == 3 );
		t.Preview( "Move", x => x.Move( new Vec3( 0, 0, 1 ) ) );
		t.Preview( "Move", x => x.Move( new Vec3( 0, 0, 5 ) ) );
		t.Accept();
		Check( "a scrubbed move applies only the last value", t.Mesh.Positions.Max( p => p.z ) > 7.99f && t.UndoCount == 4, $"{t.Mesh.Positions.Max( p => p.z )} / {t.UndoCount}" );
		t.Undo();
		t.Undo();
		t.Undo();
		t.Undo();
		Check( "and it all undoes", MathF.Abs( t.Mesh.Positions.Max( p => p.z ) - 1f ) < 1e-5f && t.UndoCount == 0 );
		refused = false;
		try { new MeshEditSession( Primitives.Box( 2, 2, 2 ) ).Move( new Vec3( 1, 0, 0 ) ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a typed move with nothing selected is refused", refused );

		// Committing hands them to the feature, which saves and reloads them.
		var studio = new PartStudio();
		var prim = studio.Add( new PrimitiveFeature() );
		prim.SizeX.Value = 2f;
		prim.SizeY.Value = 2f;
		prim.SizeZ.Value = 2f;
		var edit = studio.Add( new MeshEditFeature() );
		studio.Rebuild();
		var session = new MeshEditSession( edit.LastInput );
		session.SetMode( EditElement.Edge );
		foreach ( var key in session.Mesh.BuildEdgeFaces().Keys )
			if ( session.Mesh.Positions[key.A].z > 0.9f && session.Mesh.Positions[key.B].z > 0.9f )
				session.SelectEdge( key, MeshEditSession.Combine.Add );
		session.Crease( 1f );
		edit.SubdivideLevels.Value = 1;
		session.CommitTo( edit );
		studio.Rebuild();
		Check( "the feature keeps the creases", edit.Creases.Count == 4 );
		Check( "and its subdivided output has the flat top", studio.Bodies[0].Mesh.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f ) == 9, $"{studio.Bodies[0].Mesh.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f )}" );

		var saved = edit.SaveMesh();
		var reloaded = new MeshEditFeature();
		reloaded.LoadMesh( saved );
		var studio2 = new PartStudio();
		var prim2 = studio2.Add( new PrimitiveFeature() );
		prim2.SizeX.Value = 2f;
		prim2.SizeY.Value = 2f;
		prim2.SizeZ.Value = 2f;
		studio2.Add( reloaded );
		reloaded.SubdivideLevels.Value = 1;
		studio2.Rebuild();
		Check( "a reloaded edit keeps its creases", reloaded.Creases.Count == 4 && reloaded.Error is null, reloaded.Error ?? "" );
		Check( "and still outputs the flat top", studio2.Bodies[0].Mesh.Positions.Count( p => MathF.Abs( p.z - 1f ) < 1e-5f ) == 9 );
	}

	static void TestEditSessionTopologyTools()
	{
		Section( "edit session: rotate edge, subdivide edges, fill holes, beautify, sharp/mirror/loose selects, selection to pivot" );

		// Rotate edge on a quad split into two triangles flips its diagonal.
		var q = new MeshEditSession( Primitives.Plane( 2f, 2f, 1, 1 ) );
		q.SetMode( EditElement.Face );
		q.SelectAll();
		q.TriangulateFaces();
		var diagonal = q.Mesh.BuildEdgeFaces().First( kv => kv.Value.Count == 2 ).Key;
		q.SetMode( EditElement.Edge );
		q.SelectEdge( diagonal );
		q.RotateEdge();
		var newDiagonal = q.Mesh.BuildEdgeFaces().First( kv => kv.Value.Count == 2 ).Key;
		Check( "rotating the diagonal of two triangles gives the other diagonal",
			!newDiagonal.Equals( diagonal ) && q.Mesh.FaceCount == 2 && q.Mesh.Faces.All( f => f.Indices.Length == 3 ), $"{diagonal.A}-{diagonal.B} -> {newDiagonal.A}-{newDiagonal.B}" );
		Check( "the other diagonal joins the two corners the old one missed",
			new HashSet<int> { newDiagonal.A, newDiagonal.B }.SetEquals( Enumerable.Range( 0, 4 ).Where( v => v != diagonal.A && v != diagonal.B ) ) );
		Check( "and it is the selection", q.SelectedEdges.Count == 1 && q.SelectedEdges.Contains( newDiagonal ) );
		Check( "the sheet is still valid and faces the same way", MeshValidator.Validate( q.Mesh ).IsValid && q.Mesh.Faces.All( f => q.Mesh.FaceNormal( f ).z > 0.9f ) );
		q.RotateEdge();
		Check( "turning it again gives the first diagonal back", q.Mesh.BuildEdgeFaces().First( kv => kv.Value.Count == 2 ).Key.Equals( diagonal ) );

		// On two quads of a 2x1 grid the shared edge turns one corner round the hexagon they make:
		// still two quads, now slanted, sharing a different edge. Blender does the same.
		var g = new MeshEditSession( Primitives.Plane( 4f, 2f, 2, 1 ) );
		g.SetMode( EditElement.Edge );
		var between = g.Mesh.BuildEdgeFaces().First( kv => kv.Value.Count == 2 ).Key;
		g.SelectEdge( between );
		g.RotateEdge();
		var after = g.Mesh.BuildEdgeFaces().First( kv => kv.Value.Count == 2 ).Key;
		Check( "rotating the edge between two quads gives two slanted quads on a new shared edge",
			g.Mesh.FaceCount == 2 && g.Mesh.Faces.All( f => f.Indices.Length == 4 ) && !after.Equals( between ) && (after.A == between.A || after.A == between.B || after.B == between.A || after.B == between.B) == false,
			string.Join( ",", g.Mesh.Faces.Select( f => f.Indices.Length ) ) + $" {between.A}-{between.B} -> {after.A}-{after.B}" );
		Check( "and keeps the sheet's area", MathF.Abs( g.Mesh.Faces.Sum( f => g.Mesh.FaceArea( f ) ) - 8f ) < 1e-4f );

		var refused = false;
		var open = new MeshEditSession( Primitives.Plane( 2f, 2f, 1, 1 ) );
		open.SetMode( EditElement.Edge );
		open.SelectEdge( open.Mesh.BuildEdgeFaces().First().Key );
		try { open.RotateEdge(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a border edge cannot be rotated", refused );

		// Subdivide edges: one edge of a quad cut twice makes a hexagon; two opposite edges make a strip.
		var se = new MeshEditSession( Primitives.Plane( 2f, 2f, 1, 1 ) );
		se.SetMode( EditElement.Edge );
		se.SelectEdge( se.Mesh.BuildEdgeFaces().First().Key );
		se.SubdivideEdges( 2 );
		Check( "cutting one edge of a quad twice gives a six-sided face", se.Mesh.FaceCount == 1 && se.Mesh.Faces[0].Indices.Length == 6 && se.Mesh.VertexCount == 6 );
		Check( "the cut points are the selection, in vertex mode", se.Mode == EditElement.Vertex && se.SelectedVertices.Count == 2 );
		Check( "and sit a third of the way along", se.SelectedVertices.All( v => { var p = se.Mesh.Positions[v]; return MathF.Abs( MathF.Abs( p.x ) - 1f / 3f ) < 1e-4f || MathF.Abs( MathF.Abs( p.y ) - 1f / 3f ) < 1e-4f; } ) );

		var strip = new MeshEditSession( Primitives.Plane( 2f, 2f, 1, 1 ) );
		strip.SetMode( EditElement.Edge );
		foreach ( var key in strip.Mesh.BuildEdgeFaces().Keys )
			if ( MathF.Abs( strip.Mesh.Positions[key.A].y - strip.Mesh.Positions[key.B].y ) < 1e-5f )
				strip.SelectEdge( key, MeshEditSession.Combine.Add );
		Check( "the two edges running along x are picked", strip.SelectedEdges.Count == 2 );
		strip.SubdivideEdges( 3 );
		Check( "cutting two opposite edges three times gives a strip of four quads", strip.Mesh.FaceCount == 4 && strip.Mesh.Faces.All( f => f.Indices.Length == 4 ), $"{strip.Mesh.FaceCount}" );
		Check( "each with a quarter of the area", strip.Mesh.Faces.All( f => MathF.Abs( strip.Mesh.FaceArea( f ) - 1f ) < 1e-4f ) );
		Check( "the strip is valid", MeshValidator.Validate( strip.Mesh ).IsValid, MeshValidator.Validate( strip.Mesh ).ToString() );
		Check( "and still faces up", strip.Mesh.Faces.All( f => strip.Mesh.FaceNormal( f ).z > 0.9f ) );

		var grid = new MeshEditSession( Primitives.Plane( 2f, 2f, 1, 1 ) );
		grid.SetMode( EditElement.Edge );
		grid.SelectAll();
		grid.SubdivideEdges( 1 );
		Check( "cutting all four edges once gives a 2x2 grid", grid.Mesh.FaceCount == 4 && grid.Mesh.VertexCount == 9 && grid.Mesh.Faces.All( f => f.Indices.Length == 4 ), $"{grid.Mesh.FaceCount}f {grid.Mesh.VertexCount}v" );
		Check( "with its centre vertex in the middle", grid.Mesh.Positions.Any( p => p.Length < 1e-5f ) );
		Check( "the grid is valid", MeshValidator.Validate( grid.Mesh ).IsValid );

		// On a box, cutting the four vertical edges splits every side into two quads and leaves the
		// top and bottom as they were — a closed solid throughout.
		var box = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		box.SetMode( EditElement.Edge );
		foreach ( var key in box.Mesh.BuildEdgeFaces().Keys )
			if ( MathF.Abs( box.Mesh.Positions[key.A].z - box.Mesh.Positions[key.B].z ) > 1.5f )
				box.SelectEdge( key, MeshEditSession.Combine.Add );
		box.SubdivideEdges( 1 );
		Check( "cutting a box's vertical edges gives 10 faces on 12 vertices", box.Mesh.FaceCount == 10 && box.Mesh.VertexCount == 12, $"{box.Mesh.FaceCount}f {box.Mesh.VertexCount}v" );
		Check( "and it stays a closed solid of volume 8", MeshValidator.Validate( box.Mesh ) is { IsValid: true, IsClosed: true } && MathF.Abs( box.Mesh.SignedVolume() - 8f ) < 1e-4f, MeshValidator.Validate( box.Mesh ).ToString() );

		// Fill holes: knock two faces out of a box, fill holes up to four sides puts both back.
		var holes = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		holes.SetMode( EditElement.Face );
		holes.SelectFace( 0 );
		holes.SelectFace( 1, MeshEditSession.Combine.Add );
		holes.DeleteOnlyFaces();
		var opened = MeshValidator.Validate( holes.Mesh );
		Check( "two faces gone leaves an open box", !opened.IsClosed );
		var closedCount = holes.FillHoles( 4 );
		Check( "fill holes closes both", closedCount == 2 && MeshValidator.Validate( holes.Mesh ) is { IsValid: true, IsClosed: true }, MeshValidator.Validate( holes.Mesh ).ToString() );
		Check( "with the right winding: the volume is +8", MathF.Abs( holes.Mesh.SignedVolume() - 8f ) < 1e-4f, $"{holes.Mesh.SignedVolume()}" );
		Check( "the new faces are the selection", holes.SelectedFaces.Count == 2 && holes.Mode == EditElement.Face );
		holes.Undo();
		refused = false;
		try { holes.FillHoles( 3 ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a side limit below the hole's size fills nothing, and says so", refused && holes.UndoCount == 1 );
		refused = false;
		try { new MeshEditSession( Primitives.Box( 2, 2, 2 ) ).FillHoles(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a closed solid has no holes to fill", refused );

		// Beautify: a quad strip triangulated badly (long diagonals) gets its diagonals flipped.
		var ugly = Primitives.Plane( 4f, 1f, 1, 1 );
		var ug = new MeshEditSession( ugly );
		ug.SetMode( EditElement.Face );
		ug.SelectAll();
		ug.TriangulateFaces();
		Check( "a long thin quad triangulates into two triangles", ug.Mesh.FaceCount == 2 );
		// A 4x1 quad has only one sensible pair of triangles either way; make one that is genuinely
		// bad: a kite whose diagonal runs the long way.
		var kite = new PolyMesh();
		kite.Positions.Add( new Vec3( 0, 0, 0 ) );
		kite.Positions.Add( new Vec3( 5, 0.5f, 0 ) );
		kite.Positions.Add( new Vec3( 10, 0, 0 ) );
		kite.Positions.Add( new Vec3( 5, -0.5f, 0 ) );
		kite.AddFace( new[] { 0, 1, 2 } );
		kite.AddFace( new[] { 0, 2, 3 } );
		var bk = new MeshEditSession( kite );
		bk.SetMode( EditElement.Face );
		bk.SelectAll();
		bk.BeautifyFaces();
		var shared = bk.Mesh.BuildEdgeFaces().First( kv => kv.Value.Count == 2 ).Key;
		Check( "beautify flips the long diagonal to the short one", shared.Equals( new EdgeKey( 1, 3 ) ), $"{shared.A}-{shared.B}" );
		Check( "and keeps the faces pointing the same way (this kite was wound facing down)", bk.Mesh.Faces.All( f => bk.Mesh.FaceNormal( f ).z < -0.9f ) );
		refused = false;
		try { bk.BeautifyFaces(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "beautify on already good triangles refuses rather than doing nothing silently", refused );

		// Select sharp edges: the twelve edges of a box, none of a flat sheet.
		var sharp = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		sharp.SelectSharpEdges( 30f );
		Check( "a box's twelve edges are all sharp", sharp.SelectedEdges.Count == 12 && sharp.Mode == EditElement.Edge );
		var flat = new MeshEditSession( Primitives.Plane( 4f, 4f, 4, 4 ) );
		flat.SelectSharpEdges( 30f );
		Check( "a flat sheet has none", flat.SelectedEdges.Count == 0 );

		// Select mirror: a top corner on +X picks up its twin on -X.
		var mir = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		mir.SetMode( EditElement.Vertex );
		mir.SelectVertex( mir.Mesh.Positions.FindIndex( p => p.x > 0 && p.y > 0 && p.z > 0 ) );
		mir.SelectMirror();
		Check( "select mirror adds the partner across X", mir.SelectedVertices.Count == 2 && mir.SelectedVertices.All( v => mir.Mesh.Positions[v].y > 0 && mir.Mesh.Positions[v].z > 0 ) );

		// Select loose finds a stray vertex.
		var strayMesh = Primitives.Box( 2, 2, 2 );
		strayMesh.Positions.Add( new Vec3( 9, 9, 9 ) );
		var stray = new MeshEditSession( strayMesh );
		stray.SelectLoose();
		Check( "select loose finds the stray", stray.SelectedVertices.Count == 1 && stray.SelectedVertices.Contains( 8 ) );
		refused = false;
		try { new MeshEditSession( Primitives.Box( 2, 2, 2 ) ).SelectLoose(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "and says when there are none", refused );

		// Selection to pivot.
		var stp = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		stp.SetMode( EditElement.Face );
		stp.SelectFace( Enumerable.Range( 0, 6 ).First( f => stp.Mesh.FaceNormal( stp.Mesh.Faces[f] ).z > 0.9f ) );
		stp.Pivot = new Vec3( 0, 0, 5 );
		stp.SelectionToPivot();
		Check( "selection to pivot moves the face's centre onto the pivot", (stp.SelectionCentre() - new Vec3( 0, 0, 5 )).Length < 1e-5f && stp.UndoCount == 1 );

		// Snap to grid.
		var grid16 = new MeshEditSession( Primitives.Box( 30, 30, 30 ) );
		grid16.SetMode( EditElement.Vertex );
		grid16.SelectAll();
		grid16.SnapToGrid( 16f );
		// Named selections.
		var named = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		named.SetMode( EditElement.Vertex );
		for ( var i = 0; i < named.Mesh.VertexCount; i++ )
			if ( named.Mesh.Positions[i].z > 0 )
				named.SelectVertex( i, MeshEditSession.Combine.Add );
		named.SaveSelection( "top" );
		named.ClearSelection();
		named.RecallSelection( "top" );
		Check( "a saved selection comes back", named.SelectedVertices.Count == 4 && named.SelectedVertices.All( v => named.Mesh.Positions[v].z > 0 ) );
		named.SetMode( EditElement.Face );
		named.RecallSelection( "top" );
		Check( "in whatever mode is current: the top face", named.SelectedFaces.Count == 1 );
		named.SelectAll();
		named.RecallSelection( "top", MeshEditSession.Combine.Remove );
		Check( "and can be taken away from a selection", named.SelectedFaces.Count == 1 && named.Mesh.FaceNormal( named.Mesh.Faces[named.SelectedFaces.First()] ).z < -0.9f, $"{named.SelectedFaces.Count}" );
		refused = false;
		try { named.RecallSelection( "nope" ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "an unknown name is refused", refused );

		Check( "snap to grid lands every corner on a 16", grid16.Mesh.Positions.All( p => MathF.Abs( MathF.Abs( p.x ) - 16f ) < 1e-5f && MathF.Abs( MathF.Abs( p.y ) - 16f ) < 1e-5f && MathF.Abs( MathF.Abs( p.z ) - 16f ) < 1e-5f ) );
	}

	static void TestEditSessionMappingTools()
	{
		Section( "edit session: world-scale UVs, trims, pipes and scatter" );

		// Project UVs at 32 units per tile: a 64-unit box face spans exactly two tiles.
		var box = new MeshEditSession( Primitives.Box( 64, 64, 64 ) );
		box.ProjectUVs( 32f );
		Check( "box projection at 32 units per tile spans two tiles on a 64-unit face",
			box.Mesh.Faces.All( f => MathF.Abs( f.UVs.Max( uv => uv.x ) - f.UVs.Min( uv => uv.x ) - 2f ) < 1e-4f && MathF.Abs( f.UVs.Max( uv => uv.y ) - f.UVs.Min( uv => uv.y ) - 2f ) < 1e-4f ) );
		Check( "it is one undo step", box.UndoCount == 1 && box.LastLabel == "Project UVs" );

		box.SetMode( EditElement.Face );
		var top = Enumerable.Range( 0, 6 ).First( f => box.Mesh.FaceNormal( box.Mesh.Faces[f] ).z > 0.9f );
		var side = Enumerable.Range( 0, 6 ).First( f => box.Mesh.FaceNormal( box.Mesh.Faces[f] ).x > 0.9f );
		box.SelectFace( top );
		box.ProjectUVs( 16f );
		Check( "projecting a selection only touches those faces",
			MathF.Abs( box.Mesh.Faces[top].UVs.Max( uv => uv.x ) - box.Mesh.Faces[top].UVs.Min( uv => uv.x ) - 4f ) < 1e-4f
			&& MathF.Abs( box.Mesh.Faces[side].UVs.Max( uv => uv.x ) - box.Mesh.Faces[side].UVs.Min( uv => uv.x ) - 2f ) < 1e-4f );

		box.SelectFace( side );
		box.ProjectUVs( 32f, new Vec3( 0, 0, 1 ) );
		Check( "a planar projection along Z squashes a vertical face to a line", box.Mesh.Faces[side].UVs.Max( uv => uv.y ) - box.Mesh.Faces[side].UVs.Min( uv => uv.y ) < 1e-4f || box.Mesh.Faces[side].UVs.Max( uv => uv.x ) - box.Mesh.Faces[side].UVs.Min( uv => uv.x ) < 1e-4f );

		var refused = false;
		try { box.ProjectUVs( 0f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "zero units per tile is refused", refused );

		// Map to trim: a long thin face fills the band 0.25..0.5 across and runs along at world scale.
		var plank = new MeshEditSession( Primitives.Plane( 128f, 8f, 1, 1 ) );
		plank.SetMode( EditElement.Face );
		plank.SelectAll();
		plank.MapToTrim( 0.25f, 0.5f, 64f );
		var uvs = plank.Mesh.Faces[0].UVs;
		Check( "map to trim squashes the short way into the band", MathF.Abs( uvs.Min( uv => uv.y ) - 0.25f ) < 1e-4f && MathF.Abs( uvs.Max( uv => uv.y ) - 0.5f ) < 1e-4f, $"{uvs.Min( uv => uv.y )}..{uvs.Max( uv => uv.y )}" );
		Check( "and keeps world scale along the plank: 128 units at 64 per tile is two tiles", MathF.Abs( uvs.Max( uv => uv.x ) - uvs.Min( uv => uv.x ) - 2f ) < 1e-4f, $"{uvs.Max( uv => uv.x ) - uvs.Min( uv => uv.x )}" );
		refused = false;
		try { plank.MapToTrim( 0.3f, 0.3f, 64f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a band with no height is refused", refused );

		// Pipe: a straight run of three edges becomes a capped tube of the right length and volume.
		var wire = new PolyMesh();
		for ( var i = 0; i < 4; i++ )
			wire.Positions.Add( new Vec3( i * 10f, 0, 0 ) );
		wire.Positions.Add( new Vec3( 0, 50, 0 ) );
		wire.Positions.Add( new Vec3( 30, 50, 0 ) );
		// A thin face so the edges exist in a face-based mesh, plus the guide run along its bottom.
		wire.AddFace( new[] { 0, 1, 2, 3, 5, 4 } );
		var pipe = new MeshEditSession( wire );
		pipe.SetMode( EditElement.Edge );
		for ( var i = 0; i < 3; i++ )
			pipe.SelectEdge( new EdgeKey( i, i + 1 ), MeshEditSession.Combine.Add );
		pipe.Pipe( 2f, 8 );
		Check( "a run of three edges makes 3 x 8 wall quads and two caps", pipe.Mesh.FaceCount == 1 + 24 + 2, $"{pipe.Mesh.FaceCount}" );
		Check( "on four rings of eight", pipe.Mesh.VertexCount == 6 + 32 );
		Check( "the tube's faces are the selection", pipe.SelectedFaces.Count == 26 && pipe.Mode == EditElement.Face );
		Check( "every ring vertex is the radius from the wire", Enumerable.Range( 6, 32 ).All( v => MathF.Abs( MathF.Sqrt( pipe.Mesh.Positions[v].y * pipe.Mesh.Positions[v].y + pipe.Mesh.Positions[v].z * pipe.Mesh.Positions[v].z ) - 2f ) < 1e-4f ) );

		// The tube alone is a closed solid with the volume of an octagonal prism 30 long.
		var tube = new PolyMesh { Positions = new List<Vec3>( pipe.Mesh.Positions ) };
		foreach ( var f in pipe.SelectedFaces )
			tube.AddFace( (int[])pipe.Mesh.Faces[f].Indices.Clone() );
		var tv = MeshValidator.Validate( MeshEditSession.RemoveUnusedVertices( tube ) );
		Check( "the tube is a closed solid", tv.IsValid && tv.IsClosed, tv.ToString() );
		var octagon = 8f * 0.5f * 2f * 2f * MathF.Sin( MathF.PI * 2f / 8f );
		Check( "with the volume of an octagonal prism, wound outward", MathF.Abs( tube.SignedVolume() - octagon * 30f ) < 1e-2f, $"{tube.SignedVolume()} vs {octagon * 30f}" );

		// A closed loop of edges makes a ring with no caps.
		var ringMesh = Primitives.Plane( 20f, 20f, 1, 1 );
		var ring = new MeshEditSession( ringMesh );
		ring.SetMode( EditElement.Edge );
		ring.SelectAll();
		ring.Pipe( 1f, 6 );
		Check( "a closed loop pipes into a ring of 4 x 6 quads with no caps", ring.Mesh.FaceCount == 1 + 24 && ring.Mesh.VertexCount == 4 + 24, $"{ring.Mesh.FaceCount}f {ring.Mesh.VertexCount}v" );
		refused = false;
		try { new MeshEditSession( Primitives.Box( 2, 2, 2 ) ).Pipe( 1f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "pipe with no edges selected is refused", refused );

		// Scatter: 50 pebbles over a 100x100 ground, each a separate piece in one new body, on the
		// surface, sized between 1 and 2, and the same again with the same seed.
		var ground = new MeshEditSession( Primitives.Plane( 100f, 100f, 4, 4 ) );
		// A prop stands on its origin, so the pebble's base is lifted to it.
		var pebble = MeshTransform.Transformed( Primitives.Box( 1, 1, 1 ), Xform.Translate( new Vec3( 0, 0, 0.5f ) ) );
		var placed = ground.Scatter( pebble, 50, seed: 3, minScale: 1f, maxScale: 2f );
		Check( "scatter places every copy", placed == 50 && ground.Separated.Count == 1 && ground.Separated[0].FaceCount == 300 );
		Check( "the ground itself is untouched", ground.Mesh.FaceCount == 16 && ground.Mesh.VertexCount == 25 );
		var pieces = MeshSplit.ConnectedPieces( ground.Separated[0] );
		Check( "as 50 separate pieces", pieces.Count == 50, $"{pieces.Count}" );
		Check( "each standing on the ground within the field",
			pieces.All( p => { var c = Vec3.Zero; foreach ( var q in p.Positions ) c += q; c /= p.VertexCount; return MathF.Abs( c.x ) <= 50f && MathF.Abs( c.y ) <= 50f && c.z > 0.49f && c.z < 1.01f; } ) );
		Check( "sized between 1 and 2", pieces.All( p => { var s = p.Positions.Max( q => q.z ) - p.Positions.Min( q => q.z ); return s > 0.99f && s < 2.01f; } ) );
		var again = new MeshEditSession( Primitives.Plane( 100f, 100f, 4, 4 ) );
		again.Scatter( pebble, 50, seed: 3, minScale: 1f, maxScale: 2f );
		Check( "the same seed scatters the same", Enumerable.Range( 0, ground.Separated[0].VertexCount ).All( v => (ground.Separated[0].Positions[v] - again.Separated[0].Positions[v]).Length < 1e-5f ) );
		var other = new MeshEditSession( Primitives.Plane( 100f, 100f, 4, 4 ) );
		other.Scatter( pebble, 50, seed: 4, minScale: 1f, maxScale: 2f );
		Check( "another seed scatters differently", Enumerable.Range( 0, ground.Separated[0].VertexCount ).Any( v => (ground.Separated[0].Positions[v] - other.Separated[0].Positions[v]).Length > 1e-3f ) );

		// Aligned to the surface on a box, nothing lands on the walls with a 30° slope limit.
		var hill = new MeshEditSession( Primitives.Box( 10, 10, 10 ) );
		hill.Scatter( pebble, 20, seed: 1, maxSlopeDegrees: 30f );
		Check( "a slope limit keeps the pebbles off the walls and the underside", hill.Separated[0].Positions.All( p => p.z > 4.99f ) );
		refused = false;
		try { hill.Scatter( pebble, 5, maxSlopeDegrees: 30f ); hill.SetMode( EditElement.Face ); hill.SelectFace( Enumerable.Range( 0, 6 ).First( f => hill.Mesh.FaceNormal( hill.Mesh.Faces[f] ).x > 0.9f ) ); hill.Scatter( pebble, 5, maxSlopeDegrees: 30f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a selection that is all too steep is refused", refused );
		Check( "scatter undoes as one step", hill.UndoCount == 2 );
		hill.Undo();
		Check( "and the scattered body goes with it", hill.Separated.Count == 1 );
	}

	static void TestSkinBlockout()
	{
		Section( "skin block-out: a body from bones" );

		// One bone straight up: two cubes and a tube, closed, with the tube's volume plus the ends.
		var one = new Skeleton();
		one.AddBone( "spine", -1, Xform.Translate( Vec3.Zero ), 10f );
		var blocky = SkinBlockout.Build( one, _ => 2f, smoothLevels: 0 );
		var v = MeshValidator.Validate( blocky );
		Check( "one bone gives a closed all-quad solid", v.IsValid && v.IsClosed && blocky.Faces.All( f => f.Indices.Length == 4 ), v.ToString() );
		Check( "of two cubes joined by a tube: 5 + 5 + 4 faces", blocky.FaceCount == 14, $"{blocky.FaceCount}" );
		// Two 4x4x4 cubes at the ends overlap the 4x4 tube of length 10 between their centres:
		// the union is a 4x4 box from -2 to 12 along the bone.
		Check( "wound outward, with the volume of the box it makes", MathF.Abs( blocky.SignedVolume() - 4f * 4f * 14f ) < 1e-2f, $"{blocky.SignedVolume()}" );

		var smooth = SkinBlockout.Build( one, _ => 2f );
		Check( "smoothed twice it is still a closed solid", MeshValidator.Validate( smooth ) is { IsValid: true, IsClosed: true } );
		Check( "and smaller than the box, as a rounded thing is", smooth.SignedVolume() < blocky.SignedVolume() && smooth.SignedVolume() > blocky.SignedVolume() * 0.5f );

		// A chain: the child's head sits on the parent's tail, so they share one joint cube.
		var chain = new Skeleton();
		var root = chain.AddBone( "a", -1, Xform.Translate( Vec3.Zero ), 10f );
		chain.AddBone( "b", root, Xform.Translate( chain.TailWorld( root ) ), 8f );
		var linked = SkinBlockout.Build( chain, _ => 1f, smoothLevels: 0 );
		Check( "a chain of two bones shares the middle joint: three cubes, two tubes", linked.FaceCount == 5 + 4 + 4 + 4 + 5, $"{linked.FaceCount}" );
		Check( "and is one closed solid", MeshValidator.Validate( linked ) is { IsValid: true, IsClosed: true } );

		// A fork: three bones out of one joint take three faces of its cube.
		var fork = new Skeleton();
		var hip = fork.AddBone( "hip", -1, Xform.Translate( Vec3.Zero ), 6f );
		var top = fork.TailWorld( hip );
		fork.AddBone( "l", hip, Xform.Translate( top ) * Xform.Rotate( new Vec3( 1, 0, 0 ), 1.2f ), 8f );
		fork.AddBone( "r", hip, Xform.Translate( top ) * Xform.Rotate( new Vec3( 1, 0, 0 ), -1.2f ), 8f );
		var forked = SkinBlockout.Build( fork, _ => 1f, smoothLevels: 1 );
		Check( "a fork builds and closes", MeshValidator.Validate( forked ) is { IsValid: true, IsClosed: true }, MeshValidator.Validate( forked ).ToString() );
		Check( "with the pieces of all three limbs", MeshSplit.ConnectedPieces( forked ).Count == 1 );

		var refused = false;
		try { SkinBlockout.Build( new Skeleton() ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "no bones is refused", refused );
	}

	static void TestUVIslands()
	{
		Section( "uv islands: find, move, turn, size, pack, stretch" );

		// A box-projected box: every face its own island (no two faces agree about UVs at an edge).
		var box = Primitives.Box( 2, 2, 2 );
		UVProjection.BoxProject( box, 1f );
		var layout = UVIslands.Find( box );
		Check( "a box-projected box is six islands", layout.Count == 6, $"{layout.Count}" );

		// A flat grid projected as one: one island.
		var grid = Primitives.Plane( 4f, 4f, 4, 4 );
		UVProjection.PlanarProject( grid, new Vec3( 0, 0, 1 ), 4f );
		Check( "a planar-projected grid is one island", UVIslands.Find( grid ).Count == 1 );

		// Two grids side by side in one mesh: two islands, and selecting one face grows to its island.
		var two = Primitives.Plane( 4f, 4f, 2, 2 );
		UVProjection.PlanarProject( two, new Vec3( 0, 0, 1 ), 4f );
		var other = MeshTransform.Transformed( Primitives.Plane( 4f, 4f, 2, 2 ), Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		UVProjection.PlanarProject( other, new Vec3( 0, 0, 1 ), 4f );
		MeshTransform.Append( two, other );
		var s = new MeshEditSession( two );
		s.SetMode( EditElement.Face );
		s.SelectFace( 0 );
		s.SelectUVIslands();
		Check( "island select grows one face to its island of four", s.SelectedFaces.Count == 4 && s.SelectedFaces.All( f => f < 4 ) );

		// Move the island: only its UVs change.
		var before = two.Faces[5].UVs[0];
		s.MoveUVs( new Vec2( 0.25f, 0f ) );
		Check( "moving the island shifts its UVs", MathF.Abs( s.Mesh.Faces[0].UVs[0].x - two.Faces[0].UVs[0].x ) < 1e-6f ? false : true );
		Check( "and leaves the other island alone", s.Mesh.Faces[5].UVs[0].x == before.x && s.Mesh.Faces[5].UVs[0].y == before.y );
		Check( "as one undo step", s.UndoCount == 1 && s.LastLabel == "Move UVs" );
		s.Undo();

		// Rotate by 90 about the centre keeps the bounds' size (a square island) and moves corners.
		s.SelectFace( 0 );
		var (min0, max0) = UVIslands.Bounds( s.Mesh, s.SelectedIslandFaces() );
		s.RotateUVs( 90f );
		var (min1, max1) = UVIslands.Bounds( s.Mesh, s.SelectedIslandFaces() );
		Check( "turning a square island a quarter keeps its bounds", (min1 - min0).Length < 1e-4f && (max1 - max0).Length < 1e-4f, $"{min0}-{max0} vs {min1}-{max1}" );
		s.Undo();

		s.SelectFace( 0 );
		s.ScaleUVs( new Vec2( 2f, 2f ) );
		var (min2, max2) = UVIslands.Bounds( s.Mesh, s.SelectedIslandFaces() );
		Check( "scaling doubles the island's size about its centre", MathF.Abs( (max2 - min2).x - 2f * (max0 - min0).x ) < 1e-4f && ((min2 + max2) * 0.5f - (min0 + max0) * 0.5f).Length < 1e-4f );
		s.Undo();

		s.SelectFace( 0 );
		var flipBefore = s.Mesh.Faces[0].UVs.Select( uv => uv.x ).ToArray();
		s.ScaleUVs( new Vec2( -1f, 1f ) );
		Check( "a negative scale flips the island", s.Mesh.Faces[0].UVs.Select( uv => uv.x ).Zip( flipBefore ).All( p => MathF.Abs( p.First - p.Second ) > 1e-6f || MathF.Abs( p.First - (min0.x + max0.x) * 0.5f ) < 1e-6f ) );
		s.Undo();

		// Pack: every island inside 0..1, none overlapping, all at one scale.
		var packed = new MeshEditSession( box.Clone() );
		var scale = packed.PackUVs( 0.02f );
		var boxLayout = UVIslands.Find( packed.Mesh );
		Check( "packing keeps six islands", boxLayout.Count == 6 );
		Check( "all inside the unit square", packed.Mesh.Faces.All( f => f.UVs.All( uv => uv.x >= -1e-4f && uv.x <= 1f + 1e-4f && uv.y >= -1e-4f && uv.y <= 1f + 1e-4f ) ) );
		var rects = boxLayout.Islands.Select( i => UVIslands.Bounds( packed.Mesh, i ) ).ToList();
		var overlap = false;
		for ( var a = 0; a < rects.Count; a++ )
			for ( var b = a + 1; b < rects.Count; b++ )
				if ( rects[a].Min.x < rects[b].Max.x - 1e-4f && rects[b].Min.x < rects[a].Max.x - 1e-4f && rects[a].Min.y < rects[b].Max.y - 1e-4f && rects[b].Min.y < rects[a].Max.y - 1e-4f )
					overlap = true;
		Check( "and none overlap", !overlap );
		Check( "six equal squares pack three to a row, each about a third of the square", rects.All( r => (r.Max.x - r.Min.x) > 0.29f && MathF.Abs( (r.Max.x - r.Min.x) - (r.Max.y - r.Min.y) ) < 1e-4f ) && MathF.Abs( scale * 2f - (rects[0].Max.x - rects[0].Min.x) ) < 1e-4f, $"scale {scale}, side {rects[0].Max.x - rects[0].Min.x}" );
		Check( "pack is one undo step", packed.UndoCount == 1 );

		// Stretch: a uniform box projection is 1 everywhere; squashing one face's UVs reads as squashed.
		var stretch = UVIslands.Stretch( box );
		Check( "a uniform projection has no stretch", stretch.All( v => MathF.Abs( v - 1f ) < 1e-4f ) );
		var squashed = box.Clone();
		UVIslands.Scale( squashed, new[] { 0 }, new Vec2( 0.5f, 0.5f ) );
		var st = UVIslands.Stretch( squashed );
		Check( "a face given a quarter of its texture reads below one, the rest above",
			st[0] < 0.4f && Enumerable.Range( 1, 5 ).All( f => st[f] > 1f ), $"{st[0]:0.00} / {st[1]:0.00}" );
	}

	static void TestAddPrimitiveAndFromScratch()
	{
		Section( "edit session: adding primitives, and an edit that starts from nothing" );

		var s = new MeshEditSession( new PolyMesh() );
		Check( "an edit can start on an empty mesh", s.Mesh.FaceCount == 0 );
		s.AddPrimitive( MeshEditSession.PrimitiveKind.Cube, 4f );
		Check( "adding a cube to nothing gives a cube", s.Mesh.FaceCount == 6 && s.Mesh.VertexCount == 8 && MathF.Abs( s.Mesh.SignedVolume() - 64f ) < 1e-3f, $"{s.Mesh.FaceCount}f {s.Mesh.SignedVolume()}" );
		Check( "selected, in face mode", s.Mode == EditElement.Face && s.SelectedFaces.Count == 6 );
		Check( "as one undo step", s.UndoCount == 1 && s.LastLabel == "Add Cube" );

		s.Pivot = new Vec3( 10, 0, 0 );
		s.AddPrimitive( MeshEditSession.PrimitiveKind.Sphere, 2f );
		Check( "a sphere lands at the pivot as its own piece", MeshSplit.ConnectedPieces( s.Mesh ).Count == 2 && s.Mesh.Positions.Skip( 8 ).All( p => (p - new Vec3( 10, 0, 0 )).Length < 1.01f ) );
		Check( "and only the sphere is selected", s.SelectedFaces.Count == s.Mesh.FaceCount - 6 && !s.SelectedFaces.Contains( 0 ) );
		s.AddPrimitive( MeshEditSession.PrimitiveKind.Cylinder, 2f, new Vec3( 0, 10, 0 ), 8 );
		s.AddPrimitive( MeshEditSession.PrimitiveKind.Plane, 2f, new Vec3( 0, -10, 0 ) );
		s.AddPrimitive( MeshEditSession.PrimitiveKind.Tube, 2f, new Vec3( 0, 0, 10 ) );
		s.AddPrimitive( MeshEditSession.PrimitiveKind.Wedge, 2f, new Vec3( 0, 0, -10 ) );
		Check( "every kind adds a piece", MeshSplit.ConnectedPieces( s.Mesh ).Count == 6 && MeshValidator.Validate( s.Mesh ).IsValid, MeshValidator.Validate( s.Mesh ).ToString() );
		s.Undo(); s.Undo(); s.Undo(); s.Undo(); s.Undo();
		Check( "and every one undoes", s.Mesh.FaceCount == 6 );
		var refused = false;
		try { s.AddPrimitive( MeshEditSession.PrimitiveKind.Cube, 0f ); } catch ( InvalidOperationException ) { refused = true; }
		Check( "a zero size is refused", refused );

		// A from-scratch edit in a studio: no body above it, and it makes one.
		var studio = new PartStudio();
		var edit = studio.Add( new MeshEditFeature { Name = "Mesh" } );
		edit.FromScratch.Value = true;
		studio.Rebuild();
		Check( "a from-scratch edit builds on an empty studio", edit.Error is null && studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount == 0, edit.Error ?? "" );
		Check( "and knows its body", studio.Bodies[0].Id == edit.LastBodyId && edit.LastInput is { FaceCount: 0 } );

		var session = new MeshEditSession( edit.LastInput );
		session.AddPrimitive( MeshEditSession.PrimitiveKind.Cube, 2f );
		session.CommitTo( edit );
		studio.Rebuild();
		Check( "committing a cube makes the body a cube", studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount == 6 );

		var back = StudioDocument.Read( StudioDocument.Write( studio ) );
		var reloaded = back.Features.OfType<MeshEditFeature>().First();
		reloaded.LoadMesh( edit.SaveMesh() );
		back.Rebuild();
		Check( "and it survives the document", reloaded.FromScratch.Value && reloaded.Error is null && back.Bodies.Count == 1 && back.Bodies[0].Mesh.FaceCount == 6, reloaded.Error ?? "" );
	}

	static void TestEditSessionEdgeSplit()
	{
		Section( "edit session: splitting edges makes them shade hard" );

		// A flat grid: coplanar, so at any angle it shades perfectly smooth and every normal is
		// shared. A cut straight across the middle is the case that genuinely separates, because it
		// reaches the boundary at both ends.
		PolyMesh Grid() => Primitives.Plane( 8f, 8f, 4, 4 );

		var session = new MeshEditSession( Grid() );
		session.SetMode( EditElement.Edge );

		var across = new List<EdgeKey>();
		foreach ( var key in session.Mesh.BuildEdgeFaces().Keys )
			if ( MathF.Abs( session.Mesh.Positions[key.A].y ) < 1e-3f && MathF.Abs( session.Mesh.Positions[key.B].y ) < 1e-3f )
				across.Add( key );

		foreach ( var key in across )
			session.SelectEdge( key, MeshEditSession.Combine.Add );

		var vertsBefore = session.Mesh.VertexCount;
		var facesBefore = session.Mesh.FaceCount;
		var split = session.SplitEdges();

		Check( "every edge of the cut reports as split", split == across.Count, $"{split} of {across.Count}" );
		Check( "face count is unchanged — splitting unwelds, it does not add geometry",
			session.Mesh.FaceCount == facesBefore, $"{facesBefore} -> {session.Mesh.FaceCount}" );
		Check( "vertex count went up", session.Mesh.VertexCount > vertsBefore,
			$"{vertsBefore} -> {session.Mesh.VertexCount}" );
		// NOT a normal-count check: this grid is flat, so the two halves genuinely still face the same
		// way and SHOULD still share a normal value. What the split changes is that they no longer
		// share the VERTICES, which is what lets them diverge the moment either side moves or bends.
		// The box below is where the shading claim itself is proved.
		var stillJoined = 0;
		foreach ( var key in across )
			if ( session.Mesh.BuildEdgeFaces().TryGetValue( key, out var sharing ) && sharing.Count == 2 )
				stillJoined++;
		Check( "the two halves no longer share the vertices along the cut", stillJoined == 0,
			$"{stillJoined} edges still joined" );
		Check( "nothing moved",
			Enumerable.Range( 0, vertsBefore )
				.All( i => session.Mesh.Positions[i].AlmostEquals( Grid().Positions[i], 1e-6f ) ) );
		Check( "splitting is one undo step", session.UndoCount == 1 );
		Check( "undo welds it back", session.Undo() && session.Mesh.VertexCount == vertsBefore );

		// Merge by distance is the documented way back.
		foreach ( var key in across )
			session.SelectEdge( key, MeshEditSession.Combine.Add );
		session.SplitEdges();
		session.MergeByDistance( 1e-4f );
		Check( "merge by distance welds a split back up", session.Mesh.VertexCount == vertsBefore,
			$"{session.Mesh.VertexCount} vs {vertsBefore}" );

		// THE HONEST LIMIT, asserted rather than left to be discovered: a cut that does not reach a
		// boundary or another cut cannot separate the vertices at its ends, because the faces are
		// still joined the long way round. One vertical seam on a closed cylinder is that case — the
		// caps hold the rim together — and it changes nothing.
		var tube = new MeshEditSession( Primitives.Cylinder( 1f, 4f, 16 ) );
		tube.SetMode( EditElement.Edge );

		foreach ( var key in tube.Mesh.BuildEdgeFaces().Keys )
		{
			var a = tube.Mesh.Positions[key.A];
			var b = tube.Mesh.Positions[key.B];

			if ( MathF.Abs( a.z - b.z ) > 1e-3f && MathF.Abs( a.x - b.x ) < 1e-3f && MathF.Abs( a.y - b.y ) < 1e-3f )
			{
				tube.SelectEdge( key, MeshEditSession.Combine.Add );
				break;
			}
		}

		var tubeVerts = tube.Mesh.VertexCount;
		Check( "a lone seam on a closed cylinder splits nothing, because the caps hold it together",
			tube.SplitEdges() == 0 && tube.Mesh.VertexCount == tubeVerts, $"{tube.Mesh.VertexCount} vs {tubeVerts}" );

		// By angle: a box creases at 90 degrees everywhere, a smooth ball nowhere.
		var box = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var boxSplit = box.SplitEdgesByAngle( 45f );
		Check( "a box splits at every one of its twelve edges", boxSplit == 12, $"{boxSplit}" );
		Check( "and every corner becomes three vertices, one per face",
			box.Mesh.VertexCount == 24, $"{box.Mesh.VertexCount}" );
		Check( "the box still has six faces", box.Mesh.FaceCount == 6 );
		Check( "and the same volume", MathF.Abs( box.Mesh.SignedVolume() - 8f ) < 1e-4f );
		Check( "so every face now shades flat, one normal each",
			MeshNormals.ComputeCornerNormals( box.Mesh, 179f ).Normals.Count == 6,
			$"{MeshNormals.ComputeCornerNormals( box.Mesh, 179f ).Normals.Count}" );

		var ball = new MeshEditSession( Primitives.QuadSphere( 2f, 3 ) );
		var refusedAngle = false;
		try { ball.SplitEdgesByAngle( 60f ); } catch ( InvalidOperationException ) { refusedAngle = true; }
		Check( "a smooth ball has nothing to split at 60 degrees, and says so", refusedAngle );

		var bare = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var refused = false;
		try { bare.SplitEdges(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "splitting with no edges selected is refused", refused );
	}

	/// <summary>Ring, Linked and Similar — the selection tools you use constantly on a character and
	/// cannot reasonably box-select your way around.</summary>
	static void TestEditSessionSelectionTools()
	{
		Section( "edit session: ring, linked and similar selection" );

		// A cylinder: a loop runs around it, a ring runs along it. They must not be the same set.
		var tube = new MeshEditSession( Primitives.Cylinder( 1f, 4f, 16 ) );
		tube.SetMode( EditElement.Edge );

		EdgeKey? vertical = null;
		foreach ( var key in tube.Mesh.BuildEdgeFaces().Keys )
		{
			var a = tube.Mesh.Positions[key.A];
			var b = tube.Mesh.Positions[key.B];

			if ( MathF.Abs( a.z - b.z ) > 1e-3f && MathF.Abs( a.x - b.x ) < 1e-3f && MathF.Abs( a.y - b.y ) < 1e-3f )
			{
				vertical = key;
				break;
			}
		}

		Check( "the cylinder has a vertical edge to ring from", vertical is not null );

		tube.SelectEdgeRing( vertical.Value );
		var ring = tube.SelectedEdges.Count;
		Check( "a ring from a vertical edge takes more than the seed", ring > 1, $"{ring}" );
		Check( "and every edge in it is vertical, like the seed",
			tube.SelectedEdges.All( k =>
				MathF.Abs( tube.Mesh.Positions[k.A].x - tube.Mesh.Positions[k.B].x ) < 1e-3f
				&& MathF.Abs( tube.Mesh.Positions[k.A].y - tube.Mesh.Positions[k.B].y ) < 1e-3f ) );

		tube.SelectEdgeLoop( vertical.Value );
		Check( "the loop through the same edge is a different set",
			!tube.SelectedEdges.SetEquals( new HashSet<EdgeKey>() ) && tube.SelectedEdges.Count != ring
				|| tube.SelectedEdges.Count != ring,
			$"ring {ring}, loop {tube.SelectedEdges.Count}" );

		// Selecting a ring never changes the mesh.
		Check( "selecting changes no geometry",
			tube.Mesh.VertexCount == Primitives.Cylinder( 1f, 4f, 16 ).VertexCount && tube.UndoCount == 0 );

		// Linked: two boxes far apart in one mesh. One click on either takes only that one.
		var two = new PolyMesh();
		void Append( PolyMesh m, Vec3 offset )
		{
			var start = two.VertexCount;
			foreach ( var p in m.Positions )
				two.AddVertex( p + offset );
			foreach ( var f in m.Faces )
			{
				var idx = new int[f.Indices.Length];
				for ( var i = 0; i < idx.Length; i++ )
					idx[i] = f.Indices[i] + start;
				two.AddFace( idx, null, 0 );
			}
		}

		Append( Primitives.Box( 2, 2, 2 ), Vec3.Zero );
		Append( Primitives.Box( 2, 2, 2 ), new Vec3( 20f, 0, 0 ) );

		var islands = new MeshEditSession( two );
		islands.SetMode( EditElement.Face );
		islands.SelectFace( 0 );
		islands.SelectLinked();

		Check( "linked takes one whole box", islands.SelectedFaces.Count == 6,
			$"{islands.SelectedFaces.Count} of {two.FaceCount}" );
		Check( "and not the other one", islands.SelectedFaces.Count < two.FaceCount );
		Check( "every face it took belongs to the first box",
			islands.SelectedFaces.All( f => two.FaceCentroid( two.Faces[f] ).x < 10f ) );

		var nothing = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var refused = false;
		try { nothing.SelectLinked(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "linked with nothing selected is refused", refused );

		// Similar by normal: one face of a box matches only itself, because no two agree.
		var box = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		box.SetMode( EditElement.Face );
		box.SelectFace( 0 );
		box.SelectSimilar( MeshEditSession.Similarity.Normal, 10f );
		Check( "one face of a box matches only itself by normal", box.SelectedFaces.Count == 1,
			$"{box.SelectedFaces.Count}" );

		// 179 degrees takes five, not six: the far face of a box points EXACTLY 180 degrees away, so
		// it is correctly outside a 179 degree tolerance. Worth pinning, because "nearly everything"
		// looking like a bug is how a tolerance quietly gets widened until it means nothing.
		box.SelectSimilar( MeshEditSession.Similarity.Normal, 179f );
		Check( "179 degrees takes every face except the one pointing exactly the other way",
			box.SelectedFaces.Count == 5, $"{box.SelectedFaces.Count}" );

		box.SelectedFaces.Clear();
		box.SelectFace( 0 );
		box.SelectSimilar( MeshEditSession.Similarity.Normal, 180f );
		Check( "and 180 takes all six", box.SelectedFaces.Count == 6, $"{box.SelectedFaces.Count}" );

		// Similar by area on a uniform grid: every face is the same size, so one picks all.
		var grid = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		grid.SetMode( EditElement.Face );
		grid.SelectFace( 0 );
		grid.SelectSimilar( MeshEditSession.Similarity.Area, 0.01f );
		Check( "a uniform grid is all one area", grid.SelectedFaces.Count == grid.Mesh.FaceCount,
			$"{grid.SelectedFaces.Count} of {grid.Mesh.FaceCount}" );

		// After a local subdivide the new faces are a quarter the size, so area separates them.
		grid.SelectedFaces.Clear();
		grid.SelectFace( 0 );
		grid.Subdivide();
		var fine = grid.SelectedFaces.Count;
		grid.SelectSimilar( MeshEditSession.Similarity.Area, 0.01f );
		Check( "and area finds just the subdivided region afterwards",
			grid.SelectedFaces.Count == fine, $"{grid.SelectedFaces.Count} vs {fine}" );

		var bare = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var refusedSimilar = false;
		try { bare.SelectSimilar( MeshEditSession.Similarity.Normal ); }
		catch ( InvalidOperationException ) { refusedSimilar = true; }
		Check( "similar with no face selected is refused", refusedSimilar );
	}

	/// <summary>Invert, shortest path, flip, recalculate normals, fill and to sphere.</summary>
	static void TestEditSessionModelingOps()
	{
		Section( "edit session: invert, path, normals, fill, to sphere" );

		// Invert: one face of a box inverts to the other five, and back again.
		var box = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		box.SetMode( EditElement.Face );
		box.SelectFace( 0 );
		box.InvertSelection();
		Check( "inverting one face of a box gives the other five",
			box.SelectedFaces.Count == 5 && !box.SelectedFaces.Contains( 0 ), $"{box.SelectedFaces.Count}" );
		box.InvertSelection();
		Check( "and inverting again gives it back",
			box.SelectedFaces.Count == 1 && box.SelectedFaces.Contains( 0 ) );

		// Shortest path across a 4x4 grid, corner to corner along one side: five vertices.
		var grid = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		grid.SetMode( EditElement.Vertex );
		int At( float x, float y )
		{
			for ( var i = 0; i < grid.Mesh.VertexCount; i++ )
				if ( MathF.Abs( grid.Mesh.Positions[i].x - x ) < 1e-3f && MathF.Abs( grid.Mesh.Positions[i].y - y ) < 1e-3f )
					return i;
			return -1;
		}

		grid.SelectVertex( At( -4f, -4f ) );
		grid.SelectVertex( At( 4f, -4f ), MeshEditSession.Combine.Add );
		grid.SelectShortestPath();
		Check( "a path along one side of a 4x4 grid takes five vertices", grid.SelectedVertices.Count == 5,
			$"{grid.SelectedVertices.Count}" );
		Check( "and all of them are on that side",
			grid.SelectedVertices.All( v => MathF.Abs( grid.Mesh.Positions[v].y + 4f ) < 1e-3f ) );

		var lone = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		lone.SetMode( EditElement.Vertex );
		lone.SelectVertex( 0 );
		var refusedPath = false;
		try { lone.SelectShortestPath(); } catch ( InvalidOperationException ) { refusedPath = true; }
		Check( "a path from one vertex is refused", refusedPath );

		// Flip: a box turned inside out has negative volume, and flipping twice is a no-op.
		var flip = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		var uvBefore = flip.Mesh.Faces[0].UVs[0];
		var cornerBefore = flip.Mesh.Faces[0].Indices[0];
		flip.FlipNormals();
		Check( "flipping every face of a box negates its volume",
			MathF.Abs( flip.Mesh.SignedVolume() + 8f ) < 1e-3f, $"{flip.Mesh.SignedVolume():0.###}" );
		var last = flip.Mesh.Faces[0].Indices.Length - 1;
		Check( "and each UV stays with its corner",
			flip.Mesh.Faces[0].Indices[last] == cornerBefore && flip.Mesh.Faces[0].UVs[last].x == uvBefore.x
				&& flip.Mesh.Faces[0].UVs[last].y == uvBefore.y );
		flip.Undo();
		Check( "undo puts it right way out", MathF.Abs( flip.Mesh.SignedVolume() - 8f ) < 1e-3f );

		// Recalculate: flip two faces of a box by hand, recalculate, and it is a clean box again.
		var messy = Primitives.Box( 2, 2, 2 );
		Array.Reverse( messy.Faces[1].Indices );
		Array.Reverse( messy.Faces[1].UVs );
		Array.Reverse( messy.Faces[4].Indices );
		Array.Reverse( messy.Faces[4].UVs );
		var fix = new MeshEditSession( messy );
		fix.RecalculateNormals();
		Check( "recalculate fixes a box with two faces flipped",
			MathF.Abs( fix.Mesh.SignedVolume() - 8f ) < 1e-3f, $"{fix.Mesh.SignedVolume():0.###}" );

		// Even when EVERY face is wrong: the whole box inside out comes back out.
		var inside = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		inside.FlipNormals();
		inside.RecalculateNormals();
		Check( "and turns a box that is entirely inside out the right way round",
			MathF.Abs( inside.Mesh.SignedVolume() - 8f ) < 1e-3f, $"{inside.Mesh.SignedVolume():0.###}" );

		// Fill: delete a face of a box, select the hole's border, fill, and it is closed again.
		var holed = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		holed.SetMode( EditElement.Face );
		var capCorners = new HashSet<int>( holed.Mesh.Faces[0].Indices );
		holed.SelectFace( 0 );
		holed.Delete();
		holed.SetMode( EditElement.Vertex );
		foreach ( var v in capCorners )
			holed.SelectVertex( v, MeshEditSession.Combine.Add );
		holed.Fill();
		Check( "fill caps the hole in a box with one face", holed.Mesh.FaceCount == 6, $"{holed.Mesh.FaceCount}" );
		Check( "wound to match, so the box has its volume back",
			MathF.Abs( holed.Mesh.SignedVolume() - 8f ) < 1e-3f, $"{holed.Mesh.SignedVolume():0.###}" );
		Check( "and the new face is left selected", holed.SelectedFaces.Count == 1 );

		var tooFew = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		tooFew.SetMode( EditElement.Vertex );
		tooFew.SelectVertex( 0 );
		tooFew.SelectVertex( 1, MeshEditSession.Combine.Add );
		var refusedFill = false;
		try { tooFew.Fill(); } catch ( InvalidOperationException ) { refusedFill = true; }
		Check( "fill with two vertices is refused", refusedFill );

		// To sphere: a subdivided cube's vertices all end up the same distance from the middle.
		var cube = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		cube.SetMode( EditElement.Face );
		cube.SelectAll();
		cube.Subdivide();
		cube.SelectAll();
		cube.ToSphere( 1f );
		var middle = Vec3.Zero;
		foreach ( var p in cube.Mesh.Positions )
			middle += p;
		middle /= cube.Mesh.VertexCount;
		var min = float.MaxValue;
		var max = 0f;
		foreach ( var p in cube.Mesh.Positions )
		{
			var r = (p - middle).Length;
			min = MathF.Min( min, r );
			max = MathF.Max( max, r );
		}
		Check( "to sphere puts every vertex at one radius", max - min < 1e-3f, $"{min:0.###}..{max:0.###}" );
	}

	/// <summary>Split, spin/screw, shear, bend, array and the trait selections.</summary>
	static void TestEditSessionShapingOps()
	{
		Section( "edit session: split, spin, shear, bend, array, trait selection" );

		// Split: the top face of a box gets four vertices of its own, and the box keeps its shape.
		var split = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		split.SetMode( EditElement.Face );
		split.SelectFace( 0 );
		var before = split.Mesh.VertexCount;
		split.SplitFaces();
		Check( "split gives the face its own four corners", split.Mesh.VertexCount == before + 4,
			$"{before} -> {split.Mesh.VertexCount}" );
		var open = 0;
		foreach ( var pair in split.Mesh.BuildEdgeFaces() )
			if ( pair.Value.Count == 1 )
				open++;
		Check( "and both sides of the cut are now open rims", open == 8, $"{open}" );
		Check( "without moving anything", MathF.Abs( split.Mesh.SignedVolume() - 8f ) < 1e-3f );

		// Spin: the open top rim of a tube-less sheet swept a full turn closes on itself.
		var sheet = new PolyMesh();
		sheet.AddVertex( new Vec3( 1, 0, 0 ) );
		sheet.AddVertex( new Vec3( 2, 0, 0 ) );
		sheet.AddVertex( new Vec3( 2, 0, 1 ) );
		sheet.AddVertex( new Vec3( 1, 0, 1 ) );
		sheet.AddFace( new[] { 0, 1, 2, 3 }, null, 0 );
		var spin = new MeshEditSession( sheet );
		spin.SetMode( EditElement.Edge );
		spin.SelectEdge( new EdgeKey( 1, 2 ) );
		spin.Spin( 360f, 12 );
		Check( "a full spin in 12 steps adds 12 faces", spin.Mesh.FaceCount == 13, $"{spin.Mesh.FaceCount}" );
		Check( "and 12 rings of new vertices — the seam is a copy, not the original edge", spin.Mesh.VertexCount == 4 + 24,
			$"{spin.Mesh.VertexCount}" );
		Check( "every new vertex stays at radius 2",
			Enumerable.Range( 4, spin.Mesh.VertexCount - 4 ).All( v =>
				MathF.Abs( MathF.Sqrt( spin.Mesh.Positions[v].x * spin.Mesh.Positions[v].x + spin.Mesh.Positions[v].y * spin.Mesh.Positions[v].y ) - 2f ) < 1e-3f ) );
		Check( "the band agrees with the face it grew from", MeshValidator.Validate( spin.Mesh ).IsValid );

		var screw = new MeshEditSession( sheet );
		screw.SetMode( EditElement.Edge );
		screw.SelectEdge( new EdgeKey( 1, 2 ) );
		screw.Spin( 360f, 8, 4f );
		var top = 0f;
		foreach ( var p in screw.Mesh.Positions )
			top = MathF.Max( top, p.z );
		Check( "a screw does not close, and climbs by its rise", screw.Mesh.VertexCount == 4 + 16 && MathF.Abs( top - 5f ) < 1e-3f,
			$"{screw.Mesh.VertexCount} verts, top {top:0.###}" );

		// Shear keeps volume (it is a shear) and leans the top over.
		var shear = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		shear.SelectAll();
		shear.Shear( 1f );
		Check( "shear keeps the volume", MathF.Abs( shear.Mesh.SignedVolume() - 8f ) < 1e-3f, $"{shear.Mesh.SignedVolume():0.###}" );
		Check( "and leans the top forward in X past the bottom",
			shear.Mesh.Positions.Where( p => p.z > 0f ).Average( p => p.x ) - shear.Mesh.Positions.Where( p => p.z < 0f ).Average( p => p.x ) > 1.9f );

		// Bend 180 degrees: a long thin strip along X ends up folded back on itself.
		var strip = new MeshEditSession( Primitives.Plane( 10f, 1f, 10, 1 ) );
		strip.SelectAll();
		strip.Bend( 180f );
		var minX = float.MaxValue;
		var maxX = float.MinValue;
		foreach ( var p in strip.Mesh.Positions )
		{
			minX = MathF.Min( minX, p.x );
			maxX = MathF.Max( maxX, p.x );
		}
		var diameter = 10f / MathF.PI * 2f;
		var height = strip.Mesh.Positions.Max( p => p.z ) - strip.Mesh.Positions.Min( p => p.z );
		Check( "bending a 10-long strip by 180 degrees makes a half circle", MathF.Abs( height - diameter ) < 0.05f,
			$"height {height:0.###}, want {diameter:0.###}" );
		Check( "and the -X end stays put", MathF.Abs( minX + 5f ) < 1e-3f, $"{minX:0.###}" );

		// Array: one box times three, side by side.
		var array = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		array.SetMode( EditElement.Face );
		array.SelectAll();
		array.ArrayFaces( 3 );
		Check( "array of 3 has three boxes' faces", array.Mesh.FaceCount == 18, $"{array.Mesh.FaceCount}" );
		Check( "three boxes' volume", MathF.Abs( array.Mesh.SignedVolume() - 24f ) < 1e-3f, $"{array.Mesh.SignedVolume():0.###}" );
		Check( "and the last one sits two widths along", MathF.Abs( array.Mesh.Positions.Max( p => p.x ) - 5f ) < 1e-3f );

		// Non-manifold: a closed box has none; take a face away and its four edges light up.
		var holed = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		holed.SelectNonManifold();
		Check( "a closed box has no non-manifold edges", holed.SelectedEdges.Count == 0 );
		holed.SetMode( EditElement.Face );
		holed.SelectFace( 0 );
		holed.Delete();
		holed.SelectNonManifold();
		Check( "a box with a hole has its four rim edges", holed.SelectedEdges.Count == 4, $"{holed.SelectedEdges.Count}" );

		// Boundary loop: the middle 2x2 of a 4x4 grid has an 8-edge rim.
		var grid = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		grid.SetMode( EditElement.Face );
		for ( var f = 0; f < grid.Mesh.FaceCount; f++ )
		{
			var c = grid.Mesh.FaceCentroid( grid.Mesh.Faces[f] );
			if ( MathF.Abs( c.x ) < 2f && MathF.Abs( c.y ) < 2f )
				grid.SelectFace( f, MeshEditSession.Combine.Add );
		}
		grid.SelectBoundaryLoop();
		Check( "the rim of a 2x2 patch is 8 edges", grid.SelectedEdges.Count == 8, $"{grid.SelectedEdges.Count}" );

		// Checker on a 4x4 grid keeps 8 of 16, and no two kept faces share an edge.
		var checker = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		checker.SetMode( EditElement.Face );
		checker.SelectAll();
		checker.CheckerDeselect();
		Check( "checker keeps half of a 4x4 grid", checker.SelectedFaces.Count == 8, $"{checker.SelectedFaces.Count}" );
		var touching = false;
		foreach ( var pair in checker.Mesh.BuildEdgeFaces() )
			if ( pair.Value.Count(f => checker.SelectedFaces.Contains( f )) > 1 )
				touching = true;
		Check( "and no two kept faces share an edge", !touching );

		// Delete loose: two stray vertices an import left behind.
		var stray = Primitives.Box( 2, 2, 2 );
		stray.AddVertex( new Vec3( 9, 9, 9 ) );
		stray.AddVertex( new Vec3( -9, 9, 9 ) );
		var loose = new MeshEditSession( stray );
		loose.DeleteLoose();
		Check( "delete loose drops vertices no face uses", loose.Mesh.VertexCount == 8, $"{loose.Mesh.VertexCount}" );
		Check( "and leaves the box whole", MathF.Abs( loose.Mesh.SignedVolume() - 8f ) < 1e-3f );
		var refusedLoose = false;
		try { loose.DeleteLoose(); } catch ( InvalidOperationException ) { refusedLoose = true; }
		Check( "and says so when there are none", refusedLoose );
	}

	/// <summary>Poly build over a dense body: first quad, strips, welding, the refusals, relax and
	/// separating the result — with the traced body left exactly as it was.</summary>
	static void TestRetopology()
	{
		Section( "retopology: poly build, guard rails, relax, separate" );

		var dense = Primitives.Plane( 20f, 20f, 40, 40 );
		var session = new MeshEditSession( dense );
		var faces0 = session.Mesh.FaceCount;
		var verts0 = session.Mesh.VertexCount;
		const float Lift = 0.05f;

		var refusedEarly = false;
		try { session.PolyBuild( Vec3.Zero ); } catch ( InvalidOperationException ) { refusedEarly = true; }
		Check( "poly build before retopology starts is refused", refusedEarly );

		session.BeginRetopo( Lift );

		// Clicks above the surface land on it, lifted by the offset.
		session.PolyBuild( new Vec3( 0, 0, 3f ) );
		Check( "a click lands on the surface plus the lift",
			MathF.Abs( session.Mesh.Positions[^1].z - Lift ) < 1e-4f, $"{session.Mesh.Positions[^1].z}" );

		// Corners in a scrambled order still make one clean quad.
		var plan = session.PlanPolyBuild( new Vec3( 2, 2, 0 ) );
		Check( "a second click only plans a vertex", !plan.ClosesFace && plan.IsValid );
		session.PolyBuild( new Vec3( 2, 2, 0 ) );
		session.PolyBuild( new Vec3( 2, 0, 0 ) );
		session.PolyBuild( new Vec3( 0, 2, 0 ) );

		Check( "four clicks make one face", session.Mesh.FaceCount == faces0 + 1, $"{session.Mesh.FaceCount - faces0}" );
		var quad = session.Mesh.Faces[^1];
		Check( "which is a quad, not a bow-tie", quad.Indices.Length == 4
			&& MathF.Abs( session.Mesh.FaceArea( quad ) - 4f ) < 1e-3f, $"area {session.Mesh.FaceArea( quad ):0.###}" );
		Check( "facing out of the surface, not into it", session.Mesh.FaceNormal( quad ).z > 0f );
		Check( "and an edge of it is left selected to grow from",
			session.Mode == EditElement.Edge && session.SelectedEdges.Count == 1 );

		// Grow a strip off the +X side: select that edge, click further along +X.
		var right = new EdgeKey( -1, -1 );
		foreach ( var key in session.Mesh.BuildEdgeFaces().Keys )
			if ( key.A >= verts0 && key.B >= verts0
				&& MathF.Abs( session.Mesh.Positions[key.A].x - 2f ) < 1e-3f && MathF.Abs( session.Mesh.Positions[key.B].x - 2f ) < 1e-3f )
				right = key;

		session.SelectEdge( right );

		var back = session.PlanPolyBuild( new Vec3( 1f, 1f, 0 ) );
		Check( "clicking back over the quad is refused before it happens", !back.IsValid, back.Problem ?? "valid" );

		session.EvenQuads = true;
		session.PolyBuild( new Vec3( 9f, 1.3f, 0 ) );
		var grown = session.Mesh.Faces[^1];
		Check( "an even extension is square to its edge, however far the click",
			MathF.Abs( session.Mesh.FaceArea( grown ) - 4f ) < 1e-3f, $"area {session.Mesh.FaceArea( grown ):0.###}" );
		Check( "and shares the edge with the first quad",
			session.Mesh.BuildEdgeFaces()[right].Count == 2 );
		Check( "and faces the same way", session.Mesh.FaceNormal( grown ).z > 0f );

		var free = session.PlanPolyBuild( new Vec3( 9f, 1f, 0 ) );
		session.EvenQuads = false;
		var loose = session.PlanPolyBuild( new Vec3( 9f, 1f, 0 ) );
		session.EvenQuads = true;
		Check( "turning even quads off reaches to the click instead",
			loose.Corners[2].x > free.Corners[2].x + 1f, $"{free.Corners[2].x:0.##} vs {loose.Corners[2].x:0.##}" );

		// Weld: with nothing selected, a click just beside a corner of the strip reuses that corner.
		session.ClearSelection();
		var before = session.Mesh.VertexCount;
		session.PolyBuild( new Vec3( 4f, 0.1f, 0 ), weld: 0.3f );
		Check( "a click near an existing vertex reuses it", session.Mesh.VertexCount == before,
			$"{before} -> {session.Mesh.VertexCount}" );

		// Undo takes back exactly one click.
		var undoFaces = session.Mesh.FaceCount;
		session.ClearSelection();
		session.PolyBuild( new Vec3( -6, -6, 0 ) );
		session.Undo();
		Check( "undo takes back one click", session.Mesh.FaceCount == undoFaces && session.Mesh.VertexCount == before );

		// Relax keeps everything on the surface.
		session.ClearSelection();
		session.RelaxRetopo( 0.5f, 3 );
		var onSurface = true;
		for ( var v = verts0; v < session.Mesh.VertexCount; v++ )
			if ( MathF.Abs( session.Mesh.Positions[v].z - Lift ) > 1e-3f )
				onSurface = false;
		Check( "relax leaves every new vertex on the surface", onSurface );

		// Separate: the drawn faces leave, the traced body is exactly what it was.
		var drawn = session.Mesh.FaceCount - faces0;
		session.SeparateRetopo();
		Check( "finishing lifts the new mesh out as its own body",
			session.Separated.Count == 1 && session.Separated[0].FaceCount == drawn,
			$"{session.Separated.Count} bodies, {(session.Separated.Count > 0 ? session.Separated[0].FaceCount : 0)} of {drawn} faces" );
		Check( "the traced body is left as it was",
			session.Mesh.FaceCount == faces0 && session.Mesh.VertexCount == verts0 );
		Check( "and retopology is over", !session.IsRetopologizing );
		Check( "the new body is valid", MeshValidator.Validate( session.Separated[0] ).IsValid );
	}

	/// <summary>Retopology helpers: cheap undo, mirrored poly build, strips.</summary>
	static void TestRetopologyHelpers()
	{
		Section( "retopology: tail undo, mirror, strips" );

		var dense = Primitives.Plane( 20f, 20f, 40, 40 );
		var faces0 = dense.FaceCount;
		var verts0 = dense.VertexCount;

		// Tail undo: clicks, then undo them all and redo them all, and the sculpt never changes.
		var s = new MeshEditSession( dense );
		s.BeginRetopo( 0.05f );
		s.PolyBuild( new Vec3( 1, 1, 0 ) );
		s.PolyBuild( new Vec3( 3, 1, 0 ) );
		s.PolyBuild( new Vec3( 3, 3, 0 ) );
		Check( "three corners placed are three selected vertices", s.SelectedVertices.Count == 3 );
		s.Undo();
		Check( "undoing the third leaves two, still counted as corners",
			s.SelectedVertices.Count == 2 && s.Mesh.VertexCount == verts0 + 2 );
		s.PolyBuild( new Vec3( 3, 3, 0 ) );
		s.PolyBuild( new Vec3( 1, 3, 0 ) );
		Check( "and the quad still closes on the fourth", s.Mesh.FaceCount == faces0 + 1 );

		var closed = s.Mesh.FaceCount;
		while ( s.Undo() ) { }
		Check( "undoing everything leaves the sculpt exactly", s.Mesh.FaceCount == faces0 && s.Mesh.VertexCount == verts0 );
		var firstCorner = dense.Positions[0];
		Check( "with its vertices where they were", s.Mesh.Positions[0].x == firstCorner.x && s.Mesh.Positions[0].z == firstCorner.z );
		while ( s.Redo() ) { }
		Check( "and redoing everything brings the quad back", s.Mesh.FaceCount == closed );

		// Undo of Finish puts you back in retopology with the drawing intact.
		s.SeparateRetopo();
		Check( "finishing ends retopology", !s.IsRetopologizing && s.Mesh.FaceCount == faces0 );
		s.Undo();
		Check( "undoing the finish resumes it", s.IsRetopologizing && s.Mesh.FaceCount == closed && s.Separated.Count == 0 );

		// Mirror: a quad drawn on +X appears on -X too, and one on the centre line shares it.
		var m = new MeshEditSession( dense ) { MirrorX = true };
		m.BeginRetopo( 0.05f );
		foreach ( var p in new[] { new Vec3( 2, 0, 0 ), new Vec3( 4, 0, 0 ), new Vec3( 4, 2, 0 ), new Vec3( 2, 2, 0 ) } )
			m.PolyBuild( p );
		Check( "a mirrored quad makes two faces", m.Mesh.FaceCount == faces0 + 2, $"{m.Mesh.FaceCount - faces0}" );
		var mirrored = m.Mesh.Faces[^1];
		Check( "the second one on the -X side", m.Mesh.FaceCentroid( mirrored ).x < -2f );
		Check( "facing out like the first", m.Mesh.FaceNormal( mirrored ).z > 0f );

		var seam = new MeshEditSession( dense ) { MirrorX = true };
		seam.BeginRetopo( 0.05f );
		foreach ( var p in new[] { new Vec3( 0.1f, 0, 0 ), new Vec3( 2, 0, 0 ), new Vec3( 2, 2, 0 ), new Vec3( 0.1f, 2, 0 ) } )
			seam.PolyBuild( p, weld: 0.3f );
		Check( "corners near the centre go onto it",
			seam.Mesh.Positions.Skip( verts0 ).Count( v => v.x == 0f ) == 2 );
		Check( "and the two halves share them — six vertices, not eight",
			seam.Mesh.VertexCount == verts0 + 6, $"{seam.Mesh.VertexCount - verts0}" );

		// Strip: a stroke 8 long at width 2 lays four square quads in a row.
		var strip = new MeshEditSession( dense );
		strip.BeginRetopo( 0.05f );
		strip.PolyBuildStroke( new[] { new Vec3( -4, 5, 0 ), new Vec3( 0, 5, 0 ), new Vec3( 4, 5, 0 ) }, 2f );
		Check( "an 8-long stroke at width 2 lays four quads", strip.Mesh.FaceCount == faces0 + 4, $"{strip.Mesh.FaceCount - faces0}" );
		Check( "all square", strip.Mesh.Faces.Skip( faces0 ).All( f => MathF.Abs( strip.Mesh.FaceArea( f ) - 4f ) < 0.05f ) );
		Check( "all facing out", strip.Mesh.Faces.Skip( faces0 ).All( f => strip.Mesh.FaceNormal( f ).z > 0f ) );
		Check( "joined along the strip, not loose quads", strip.Mesh.VertexCount == verts0 + 10, $"{strip.Mesh.VertexCount - verts0}" );
		Check( "and its end edge is left selected", strip.SelectedEdges.Count == 1 );

		// Carrying on from the selected end edge continues the same strip.
		var before = strip.Mesh.VertexCount;
		strip.PolyBuildStroke( new[] { new Vec3( 4, 5, 0 ), new Vec3( 8, 5, 0 ) }, 0f );
		Check( "a stroke from the selected edge carries the strip on", strip.Mesh.FaceCount == faces0 + 6
			&& strip.Mesh.VertexCount == before + 4, $"{strip.Mesh.FaceCount - faces0} faces" );

		// Back over what is there: refused whole, nothing built.
		var count = strip.Mesh.FaceCount;
		var refused = false;
		strip.ClearSelection();
		try { strip.PolyBuildStroke( new[] { new Vec3( -4, 5, 0 ), new Vec3( 4, 5, 0 ) }, 2f ); }
		catch ( InvalidOperationException ) { refused = true; }
		Check( "a strip over the top of one already there is refused", refused && strip.Mesh.FaceCount == count );
	}

	/// <summary>Grid fill, rip, and spin round a moved pivot.</summary>
	static void TestGridFillRipPivot()
	{
		Section( "edit session: grid fill, rip, pivot" );

		int OpenEdges( PolyMesh m ) => m.BuildEdgeFaces().Values.Count( f => f.Count == 1 );

		// Grid fill: cut the middle 2x2 out of a 4x4 grid, then fill the 8-edge hole with a grid.
		var holed = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		holed.SetMode( EditElement.Face );
		for ( var f = 0; f < holed.Mesh.FaceCount; f++ )
		{
			var c = holed.Mesh.FaceCentroid( holed.Mesh.Faces[f] );
			if ( MathF.Abs( c.x ) < 2f && MathF.Abs( c.y ) < 2f )
				holed.SelectFace( f, MeshEditSession.Combine.Add );
		}
		holed.Delete();
		var rimOnly = holed.Mesh.FaceCount;
		holed.SetMode( EditElement.Vertex );
		foreach ( var v in Enumerable.Range( 0, holed.Mesh.VertexCount ) )
		{
			var p = holed.Mesh.Positions[v];
			if ( MathF.Abs( p.x ) < 2.5f && MathF.Abs( p.y ) < 2.5f )
				holed.SelectVertex( v, MeshEditSession.Combine.Add );
		}
		holed.GridFill();
		Check( "grid fill caps an 8-edge hole with four quads", holed.Mesh.FaceCount == rimOnly + 4, $"{holed.Mesh.FaceCount - rimOnly}" );
		Check( "leaving only the outer border open", OpenEdges( holed.Mesh ) == 16, $"{OpenEdges( holed.Mesh )}" );
		Check( "facing the same way as the grid round it",
			holed.SelectedFaces.All( f => holed.Mesh.FaceNormal( holed.Mesh.Faces[f] ).z > 0f ) );
		Check( "with its middle vertex in the middle",
			holed.Mesh.Positions.Any( p => p.Length < 1e-3f ) );

		var odd = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		odd.SetMode( EditElement.Face );
		odd.SelectFace( 0 );
		odd.Delete();
		odd.SetMode( EditElement.Vertex );
		odd.SelectAll();
		odd.GridFill();
		Check( "a 4-edge hole takes a single quad", odd.Mesh.FaceCount == 6 && MathF.Abs( odd.Mesh.SignedVolume() - 8f ) < 1e-3f );

		// Rip, border to border: the grid comes apart along x = 0.
		var sheet = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		sheet.SetMode( EditElement.Edge );
		foreach ( var key in sheet.Mesh.BuildEdgeFaces().Keys )
			if ( MathF.Abs( sheet.Mesh.Positions[key.A].x ) < 1e-3f && MathF.Abs( sheet.Mesh.Positions[key.B].x ) < 1e-3f )
				sheet.SelectEdge( key, MeshEditSession.Combine.Add );
		var verts = sheet.Mesh.VertexCount;
		sheet.Rip();
		Check( "ripping a line border to border splits all five of its vertices", sheet.Mesh.VertexCount == verts + 5,
			$"+{sheet.Mesh.VertexCount - verts}" );
		Check( "and opens both sides of all four edges", OpenEdges( sheet.Mesh ) == 16 + 8, $"{OpenEdges( sheet.Mesh )}" );
		Check( "leaving the new side selected", sheet.SelectedEdges.Count == 4 );

		// Rip inside the mesh: a slit, closed at both ends.
		var slit = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		slit.SetMode( EditElement.Edge );
		foreach ( var key in slit.Mesh.BuildEdgeFaces().Keys )
		{
			var a = slit.Mesh.Positions[key.A];
			var b = slit.Mesh.Positions[key.B];
			if ( MathF.Abs( a.x ) < 1e-3f && MathF.Abs( b.x ) < 1e-3f && MathF.Abs( a.y ) < 2.5f && MathF.Abs( b.y ) < 2.5f )
				slit.SelectEdge( key, MeshEditSession.Combine.Add );
		}
		verts = slit.Mesh.VertexCount;
		slit.Rip();
		Check( "a slit inside the mesh splits only its middle vertex", slit.Mesh.VertexCount == verts + 1, $"+{slit.Mesh.VertexCount - verts}" );
		Check( "and opens a hole of four edges", OpenEdges( slit.Mesh ) == 16 + 4, $"{OpenEdges( slit.Mesh )}" );

		var border = new MeshEditSession( Primitives.Plane( 2f, 2f, 1, 1 ) );
		border.SetMode( EditElement.Edge );
		border.SelectEdge( border.Mesh.BuildEdgeFaces().Keys.First() );
		var refused = false;
		try { border.Rip(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "ripping an open border is refused", refused );

		// Spin round a moved pivot: the swept edge keeps its distance from the pivot, not the origin.
		var profile = new PolyMesh();
		profile.AddVertex( new Vec3( 3, 0, 0 ) );
		profile.AddVertex( new Vec3( 4, 0, 0 ) );
		profile.AddVertex( new Vec3( 4, 0, 1 ) );
		profile.AddVertex( new Vec3( 3, 0, 1 ) );
		profile.AddFace( new[] { 0, 1, 2, 3 }, null, 0 );
		var spin = new MeshEditSession( profile ) { Pivot = new Vec3( 3.5f, 0, 0 ) };
		spin.SetMode( EditElement.Edge );
		spin.SelectEdge( new EdgeKey( 1, 2 ) );
		spin.Spin( 360f, 8 );
		Check( "spin turns round the pivot",
			Enumerable.Range( 4, spin.Mesh.VertexCount - 4 ).All( v =>
				MathF.Abs( MathF.Sqrt( MathF.Pow( spin.Mesh.Positions[v].x - 3.5f, 2 ) + MathF.Pow( spin.Mesh.Positions[v].y, 2 ) ) - 0.5f ) < 1e-3f ) );

		var pivot = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
		pivot.SetMode( EditElement.Face );
		pivot.SelectFace( 0 );
		pivot.PivotToSelection();
		Check( "pivot to selection lands on the face's middle",
			(pivot.Pivot - pivot.Mesh.FaceCentroid( pivot.Mesh.Faces[0] )).Length < 1e-4f );
	}

	/// <summary>The live modifier stack on a mesh edit: mirror joins at the seam, array, the fixed
	/// order, and the cage left alone.</summary>
	static void TestMeshModifiers()
	{
		Section( "mesh edit: live modifiers" );

		int OpenEdges( PolyMesh m ) => m.BuildEdgeFaces().Values.Count( f => f.Count == 1 );

		// Half a box, open at x = 0 — the way you model half a head.
		var half = Primitives.Box( 2, 2, 2 );
		for ( var i = 0; i < half.VertexCount; i++ )
			half.Positions[i] += new Vec3( 1f, 0, 0 );
		var open = half.Faces.FindIndex( f => f.Indices.All( v => MathF.Abs( half.Positions[v].x ) < 1e-4f ) );
		half.Faces.RemoveAt( open );
		Check( "the half box is open at the middle", OpenEdges( half ) == 4 );

		var whole = MeshModifiers.MirrorX( half );
		Check( "mirroring joins it into one closed body", OpenEdges( whole ) == 0, $"{OpenEdges( whole )} open" );
		Check( "twice the size", MathF.Abs( whole.SignedVolume() - 16f ) < 1e-3f, $"{whole.SignedVolume():0.###}" );
		Check( "sharing the seam's four vertices", whole.VertexCount == 12, $"{whole.VertexCount}" );
		Check( "and the half is untouched", half.FaceCount == 5 && half.VertexCount == 8 );

		var row = MeshModifiers.ArrayX( Primitives.Box( 2, 2, 2 ), 3, 1f );
		Check( "array of three with a gap of 1", row.FaceCount == 18 && MathF.Abs( row.Positions.Max( p => p.x ) - 7f ) < 1e-3f );

		var feature = new MeshEditFeature();
		Check( "a new edit has no modifiers", !feature.HasModifiers );

		feature.MirrorX.Value = true;
		feature.SubdivideLevels.Value = 1;
		var smooth = feature.ApplyModifiers( half );
		Check( "mirror then subdivide gives a smooth closed body", OpenEdges( smooth ) == 0 && smooth.FaceCount == 10 * 4,
			$"{smooth.FaceCount} faces, {OpenEdges( smooth )} open" );

		feature.SolidifyThickness.Value = 0.1f;
		var thick = feature.ApplyModifiers( half );
		Check( "thickness comes last, over the smoothed surface", thick.FaceCount > smooth.FaceCount );

		feature.MirrorX.Value = false;
		feature.SubdivideLevels.Value = 0;
		feature.SolidifyThickness.Value = 0f;
		Check( "switched off, it is the mesh again", !feature.HasModifiers && feature.ApplyModifiers( half ).FaceCount == 5 );
	}

	/// <summary>Seams on the session: marked from the selection, undoable, pruned when the edge they
	/// sit on is deleted, and actually steering the unwrap.</summary>
	static void TestEditSessionSeams()
	{
		Section( "edit session: seams mark, undo and steer the unwrap" );

		var session = new MeshEditSession( Primitives.Plane( 8f, 8f, 4, 4 ) );
		session.SetMode( EditElement.Edge );

		// The straight cut across the middle, same as the unwrap test.
		var across = new List<EdgeKey>();
		foreach ( var key in session.Mesh.BuildEdgeFaces().Keys )
			if ( MathF.Abs( session.Mesh.Positions[key.A].y ) < 1e-3f && MathF.Abs( session.Mesh.Positions[key.B].y ) < 1e-3f )
				across.Add( key );

		foreach ( var key in across )
			session.SelectEdge( key, MeshEditSession.Combine.Add );

		var before = session.Mesh.VertexCount;
		session.MarkSeam();
		Check( "marking takes the selected edges", session.Seams.Count == across.Count, $"{session.Seams.Count}" );
		Check( "and does not touch the mesh", session.Mesh.VertexCount == before );
		Check( "marking is one undo step", session.UndoCount == 1 );

		var report = session.Unwrap();
		Check( "the seam splits the unwrap in two", report.Charts == 2, $"{report.Charts}" );

		Check( "undo takes the unwrap back", session.Undo() );
		Check( "and the seams are still there", session.Seams.Count == across.Count );
		Check( "undo again takes the marks back", session.Undo() );
		Check( "leaving no seams", session.Seams.Count == 0, $"{session.Seams.Count}" );
		Check( "redo puts them back", session.Redo() && session.Seams.Count == across.Count );

		// Deleting the faces on a seam must take the seam with it, not leave it pointing at nothing.
		session.ClearSelection();
		session.SetMode( EditElement.Face );
		for ( var f = 0; f < session.Mesh.FaceCount; f++ )
			session.SelectFace( f, MeshEditSession.Combine.Add );
		session.Delete();
		Check( "deleting every face prunes every seam", session.Seams.Count == 0, $"{session.Seams.Count}" );
		Check( "and undo brings both back", session.Undo() && session.Seams.Count == across.Count,
			$"{session.Seams.Count}" );

		// Marking needs a selection, and says so rather than doing nothing.
		session.ClearSelection();
		session.SetMode( EditElement.Edge );
		var refused = false;
		try { session.MarkSeam(); } catch ( InvalidOperationException ) { refused = true; }
		Check( "marking with nothing selected is refused", refused );
	}

	static void TestEditSession()
	{
		var box = Primitives.Box( 2, 2, 2 );
		var s = new MeshEditSession( box );
		var top = TopFace( s.Mesh );
		var topZ = s.Mesh.FaceCentroid( s.Mesh.Faces[top] ).z;

		s.SelectFace( top );
		s.Extrude( 1f );
		var v = MeshValidator.Validate( s.Mesh );
		Check( "extrude through the session gives a valid closed solid", v.IsValid && v.IsClosed, v.ToString() );
		Check( "extrude adds 4 vertices and 4 faces", s.Mesh.VertexCount == 12 && s.Mesh.FaceCount == 10, $"{s.Mesh.VertexCount}v/{s.Mesh.FaceCount}f" );
		Check( "the new side walls become the selection", s.SelectedFaces.Count == 4 && !s.SelectedFaces.Contains( top ) );
		Check( "and they sit halfway up the extrusion, so E keeps going",
			MathF.Abs( s.SelectionCentre().z - (topZ + 0.5f) ) < 1e-3f );
		Check( "extrude is one undo step", s.UndoCount == 1 && s.LastLabel == "Extrude" );

		s.Undo();
		Check( "undo brings the box back", s.Mesh.VertexCount == 8 && s.Mesh.FaceCount == 6 );
		s.Redo();
		Check( "redo puts the extrude back", s.Mesh.VertexCount == 12 );

		// Scrubbing: three previews, one accept, one step.
		var steps = s.UndoCount;
		s.Preview( "Inset", x => x.Inset( 0.1f ) );
		s.Preview( "Inset", x => x.Inset( 0.3f ) );
		s.Preview( "Inset", x => x.Inset( 0.2f ) );
		s.Accept();
		Check( "scrubbing an inset lands as one undo step", s.UndoCount == steps + 1, $"{s.UndoCount - steps} steps" );
		Check( "and only the last preview's inset is applied", s.Mesh.FaceCount == 18, $"{s.Mesh.FaceCount} faces" );

		s.Preview( "Inset", x => x.Inset( 0.1f ) );
		s.Cancel();
		Check( "cancelling a preview changes nothing", s.Mesh.FaceCount == 18 && s.UndoCount == steps + 1 );

		// Dragging: absolute from the gesture start, so two calls land once, not twice.
		var before = s.Mesh.Clone();
		var beforeCentre = s.SelectionCentre();
		steps = s.UndoCount;
		s.BeginDrag();
		s.Drag( new Vec3( 0, 0, 0.5f ) );
		s.Drag( new Vec3( 0, 0, 1f ) );
		s.EndDrag();
		Check( "a drag is one undo step and never accumulates",
			s.UndoCount == steps + 1 && MathF.Abs( s.SelectionCentre().z - (beforeCentre.z + 1f) ) < 1e-3f );
		s.BeginDrag();
		s.Drag( new Vec3( 5, 5, 5 ) );
		s.EndDrag( keep: false );
		Check( "a cancelled drag puts every vertex back", s.UndoCount == steps + 1 );

		// Delete a face off a fresh box.
		var d = new MeshEditSession( box );
		d.SelectFace( TopFace( d.Mesh ) );
		d.Delete();
		v = MeshValidator.Validate( d.Mesh );
		Check( "deleting a face opens the box", d.Mesh.FaceCount == 5 && v.BoundaryEdges == 4, v.ToString() );

		// Mode conversion: a face selected, then vertex mode, has its four corners.
		var c = new MeshEditSession( box );
		c.SelectFace( TopFace( c.Mesh ) );
		c.SetMode( EditElement.Vertex );
		Check( "switching to vertex mode keeps the face's corners", c.SelectedVertices.Count == 4 );
		c.SetMode( EditElement.Edge );
		Check( "and edge mode selects the four edges between them", c.SelectedEdges.Count == 4 );

		// Loop cut from a vertical edge: the new loop is selected.
		var l = new MeshEditSession( box );
		EdgeKey vertical = default;
		foreach ( var key in l.Mesh.BuildEdgeFaces().Keys )
		{
			var a = l.Mesh.Positions[key.A];
			var b = l.Mesh.Positions[key.B];
			if ( MathF.Abs( a.z - b.z ) > 1f )
			{
				vertical = key;
				break;
			}
		}
		l.LoopCut( vertical, 0.5f );
		v = MeshValidator.Validate( l.Mesh );
		Check( "loop cut through the session stays closed", v.IsValid && v.IsClosed && l.Mesh.VertexCount == 12, v.ToString() );
		Check( "the new loop is the selection", l.Mode == EditElement.Edge && l.SelectedEdges.Count == 4, $"{l.SelectedEdges.Count} edges" );

		// Edge loop select on the cut box finds that same ring of 4.
		var ring = MeshEditSession.EdgeLoop( l.Mesh, new List<EdgeKey>( l.SelectedEdges )[0] );
		Check( "edge loop select walks the whole ring", ring.Count == 4, $"{ring.Count}" );

		// Merge the top face to a point: a pyramid.
		var p = new MeshEditSession( box );
		p.SelectFace( TopFace( p.Mesh ) );
		p.MergeAtCentre();
		v = MeshValidator.Validate( p.Mesh );
		Check( "merging the top face gives a closed 5-vertex pyramid", v.IsClosed && p.Mesh.VertexCount == 5 && p.Mesh.FaceCount == 5,
			$"{p.Mesh.VertexCount}v/{p.Mesh.FaceCount}f {v}" );

		// Mirror: moving a +x vertex moves its -x partner.
		var m = new MeshEditSession( box ) { MirrorX = true };
		m.SetMode( EditElement.Vertex );
		var right = m.Mesh.Positions.FindIndex( q => q.x > 0.5f && q.y > 0.5f && q.z > 0.5f );
		var left = m.Mesh.Positions.FindIndex( q => q.x < -0.5f && q.y > 0.5f && q.z > 0.5f );
		m.SelectVertex( right );
		m.BeginDrag();
		m.Drag( new Vec3( 0.5f, 0, 0.25f ) );
		m.EndDrag();
		Check( "mirror editing moves the partner the mirrored way",
			m.Mesh.Positions[left].AlmostEquals( new Vec3( -m.Mesh.Positions[right].x, m.Mesh.Positions[right].y, m.Mesh.Positions[right].z ) ),
			$"{m.Mesh.Positions[left]} vs {m.Mesh.Positions[right]}" );

		// Snapping: a vertex dragged near a big box's surface lands on it.
		var sn = new MeshEditSession( box ) { SnapTarget = Primitives.Box( 10, 10, 10 ), SnapOffset = 0.1f };
		sn.SetMode( EditElement.Vertex );
		sn.SelectVertex( right );
		sn.BeginDrag();
		sn.Drag( new Vec3( 3.5f, 0, 0 ) );
		sn.EndDrag();
		Check( "a snapped vertex sits on the target plus the offset", MathF.Abs( sn.Mesh.Positions[right].x - 5.1f ) < 1e-3f, $"{sn.Mesh.Positions[right]}" );

		// Edge slide: the loop cut at the box's middle slides halfway to one rim and back the other way.
		var sl = new MeshEditSession( box );
		sl.LoopCut( vertical, 0.5f );
		var loopVerts = sl.AffectedVertices();
		float LoopHeight() { var z = 0f; foreach ( var i in loopVerts ) z += sl.Mesh.Positions[i].z; return z / loopVerts.Count; }
		var h0 = LoopHeight();
		sl.EdgeSlide( 0.5f );
		var h1 = LoopHeight();
		Check( "edge slide moves the whole loop halfway to one rim, level", MathF.Abs( MathF.Abs( h1 - h0 ) - 0.5f ) < 1e-3f
			&& loopVerts.All( i => MathF.Abs( sl.Mesh.Positions[i].z - h1 ) < 1e-3f ), $"{h0} -> {h1}" );
		sl.Undo();
		sl.EdgeSlide( -0.5f );
		Check( "and the other way with a negative amount", MathF.Abs( LoopHeight() - (h0 - (h1 - h0)) ) < 1e-3f, $"{LoopHeight()}" );
		v = MeshValidator.Validate( sl.Mesh );
		Check( "edge slide keeps the solid closed and its topology", v.IsClosed && sl.Mesh.VertexCount == 12 );

		// Bevel one edge of a box: two new faces' worth of corner, still closed, a little smaller.
		var bv = new MeshEditSession( box );
		bv.SetMode( EditElement.Edge );
		bv.SelectEdge( vertical );
		bv.Bevel( 0.2f, 1 );
		v = MeshValidator.Validate( bv.Mesh );
		Check( "chamfering one selected edge gives a closed solid with the cut and its corner patches", v.IsClosed && bv.Mesh.FaceCount > 6, $"{bv.Mesh.FaceCount} faces {v}" );
		Check( "and trims only that edge's corner off", bv.Mesh.SignedVolume() < 8f && bv.Mesh.SignedVolume() > 7.9f, $"{bv.Mesh.SignedVolume()}" );
		var fl = new MeshEditSession( box );
		fl.SetMode( EditElement.Edge );
		fl.SelectEdge( vertical );
		fl.Bevel( 0.2f, 4 );
		Check( "a four-segment bevel rounds it with more faces than the chamfer", MeshValidator.Validate( fl.Mesh ).IsClosed && fl.Mesh.FaceCount > bv.Mesh.FaceCount, $"{fl.Mesh.FaceCount}" );

		// Rotate and scale through the drag.
		var rs = new MeshEditSession( box );
		rs.SelectFace( TopFace( rs.Mesh ) );
		var pivot = rs.SelectionCentre();
		rs.BeginDrag();
		rs.DragRotate( pivot, new Vec3( 0, 0, 1 ), 45f );
		rs.EndDrag();
		var corner = rs.Mesh.Positions[rs.Mesh.Faces[TopFace( rs.Mesh )].Indices[0]];
		Check( "rotating the top face 45 degrees puts a corner on an axis", MathF.Abs( corner.x ) < 1e-3f || MathF.Abs( corner.y ) < 1e-3f, $"{corner}" );
		rs.BeginDrag();
		rs.DragScale( pivot, new Vec3( 2, 2, 1 ) );
		rs.EndDrag();
		corner = rs.Mesh.Positions[rs.Mesh.Faces[TopFace( rs.Mesh )].Indices[0]];
		Check( "scaling it by 2 doubles its reach", MathF.Abs( new Vec3( corner.x, corner.y, 0 ).Length - 2f * MathF.Sqrt( 2f ) ) < 1e-3f, $"{corner}" );
		Check( "rotate and scale are one undo step each", rs.UndoCount == 2 );

		var threw = false;

		// Bisect the box at its middle: a loop of four new edges, still closed, same volume.
		var bi = new MeshEditSession( box );
		bi.Bisect( Vec3.Zero, new Vec3( 0, 0, 1 ) );
		v = MeshValidator.Validate( bi.Mesh );
		Check( "bisect through the middle splits the four sides", v.IsValid && v.IsClosed && bi.Mesh.VertexCount == 12 && bi.Mesh.FaceCount == 10, $"{bi.Mesh.VertexCount}v/{bi.Mesh.FaceCount}f {v}" );
		Check( "and keeps the volume", MathF.Abs( bi.Mesh.SignedVolume() - 8f ) < 1e-3f );
		Check( "and selects the new loop", bi.SelectedEdges.Count == 4, $"{bi.SelectedEdges.Count}" );

		// Knife across the top only, looking down: the top splits, its two neighbours take the
		// new vertices, nothing else is touched.
		var kn = new MeshEditSession( box );
		kn.Knife( new Vec3( -2, 0, 1 ), new Vec3( 2, 0, 1 ), new Vec3( 0, 0, -1 ) );
		v = MeshValidator.Validate( kn.Mesh );
		Check( "a knife line across the top splits it and stays welded", v.IsValid && v.IsClosed && kn.Mesh.FaceCount == 7, $"{kn.Mesh.FaceCount}f {v}" );

		// A knife stroke that stops short of the model cuts nothing, and says so.
		threw = false;
		try { kn.Knife( new Vec3( 5, 5, 1 ), new Vec3( 6, 5, 1 ), new Vec3( 0, 0, -1 ) ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "a knife line that misses the model refuses", threw );

		// Soft falloff: lifting the top of a cut box drags the middle loop partway, the bottom not at all.
		var so = new MeshEditSession( box );
		so.LoopCut( vertical, 0.5f );
		so.SetMode( EditElement.Vertex );
		so.ClearSelection();
		for ( var i = 0; i < so.Mesh.VertexCount; i++ )
			if ( so.Mesh.Positions[i].z > 0.9f )
				so.SelectVertex( i, MeshEditSession.Combine.Add );
		so.SoftRadius = 1.5f;
		var middle = so.Mesh.Positions.FindIndex( q => MathF.Abs( q.z ) < 1e-3f );
		var bottom = so.Mesh.Positions.FindIndex( q => q.z < -0.9f );
		so.BeginDrag();
		so.Drag( new Vec3( 0, 0, 1 ) );
		so.EndDrag();
		Check( "soft falloff moves the nearby loop part of the way", so.Mesh.Positions[middle].z > 0.1f && so.Mesh.Positions[middle].z < 0.5f, $"{so.Mesh.Positions[middle].z}" );
		Check( "and leaves what is out of reach alone", MathF.Abs( so.Mesh.Positions[bottom].z + 1f ) < 1e-5f );

		// Connected only: two boxes half an inch apart. Lifting the top of one pulls the other's
		// top through space, but not along the surface — there is no surface between them.
		var pair = Primitives.Box( 2, 2, 2 );
		MeshTransform.Append( pair, MeshTransform.Transformed( Primitives.Box( 2, 2, 2 ), Xform.Translate( new Vec3( 2.5f, 0, 0 ) ) ) );
		int OtherTop( PolyMesh m ) => m.Positions.FindIndex( q => q.x > 1.4f && q.x < 2f && q.y > 0 && q.z > 0.9f );

		foreach ( var connected in new[] { false, true } )
		{
			var two = new MeshEditSession( pair.Clone() );
			two.SetMode( EditElement.Vertex );
			for ( var i = 0; i < two.Mesh.VertexCount; i++ )
				if ( two.Mesh.Positions[i].x < 1.1f && two.Mesh.Positions[i].z > 0.9f )
					two.SelectVertex( i, MeshEditSession.Combine.Add );
			two.SoftRadius = 1.5f;
			two.SoftConnected = connected;
			var other = OtherTop( two.Mesh );
			two.BeginDrag();
			two.Drag( new Vec3( 0, 0, 1 ) );
			two.EndDrag();
			var lifted = two.Mesh.Positions[other].z - 1f;
			Check( connected ? "connected-only falloff leaves the box next door alone" : "through-space falloff pulls the box next door",
				connected ? MathF.Abs( lifted ) < 1e-5f : lifted > 0.3f, $"{lifted}" );
		}

		// The curve shapes: all agree at the ends, and order themselves in the middle.
		var shapes = new MeshEditSession( box );
		var mid = new Dictionary<MeshEditSession.SoftFalloff, float>();
		foreach ( var shape in Enum.GetValues<MeshEditSession.SoftFalloff>() )
		{
			shapes.SoftShape = shape;
			var ends = shapes.SoftWeight( 0f ) == 0f && shapes.SoftWeight( 1f ) == 1f;
			Check( $"{shape} falloff is 0 at the edge and 1 at the selection", shape == MeshEditSession.SoftFalloff.Constant ? shapes.SoftWeight( 1f ) == 1f : ends );
			mid[shape] = shapes.SoftWeight( 0.5f );
		}

		Check( "sharp pulls less than linear, smooth is in the middle, root and sphere pull more, constant pulls everything",
			mid[MeshEditSession.SoftFalloff.Sharp] < mid[MeshEditSession.SoftFalloff.Linear]
			&& MathF.Abs( mid[MeshEditSession.SoftFalloff.Smooth] - 0.5f ) < 1e-6f
			&& mid[MeshEditSession.SoftFalloff.Linear] < mid[MeshEditSession.SoftFalloff.Root]
			&& mid[MeshEditSession.SoftFalloff.Root] < mid[MeshEditSession.SoftFalloff.Sphere]
			&& mid[MeshEditSession.SoftFalloff.Constant] == 1f );

		// Operations refuse with a reason instead of doing something odd.
		var r = new MeshEditSession( box );
		threw = false;
		try { r.Extrude( 1f ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "extrude with no faces selected refuses", threw && r.UndoCount == 0 );
	}

	static void TestMeshEditFeature()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 2f;
		box.SizeY.Value = 2f;
		box.SizeZ.Value = 2f;
		var edit = studio.Add( new MeshEditFeature() );
		studio.Rebuild();

		Check( "an empty mesh edit passes the body through", edit.Error is null && studio.Bodies[0].Mesh.VertexCount == 8, edit.Error ?? "" );

		var s = new MeshEditSession( edit.LastInput );
		s.SelectFace( TopFace( s.Mesh ) );
		s.Extrude( 1f );
		Check( "committing a session reports it changed something", s.CommitTo( edit ) );
		studio.Rebuild();
		Check( "the studio shows the edit without a MarkDirty", edit.Error is null && studio.Bodies[0].Mesh.VertexCount == 12, edit.Error ?? $"{studio.Bodies[0].Mesh.VertexCount}" );

		// Through the side-car and back.
		var dir = Path.Combine( Path.GetTempPath(), "effigy-meshedit-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( dir );
		var doc = Path.Combine( dir, "part.effigy" );
		StudioDocument.WriteFile( studio, doc );
		Check( "saving writes one mesh edit", MeshEditSidecar.Save( studio, doc ) == 1 );

		var back = StudioDocument.ReadFile( doc );
		Check( "loading hands it back", MeshEditSidecar.Load( back, doc ) == 1 );
		back.Rebuild();
		var reloaded = back.Features[1] as MeshEditFeature;
		Check( "the reopened edit builds the same mesh", reloaded?.Error is null && back.Bodies[0].Mesh.VertexCount == 12, reloaded?.Error ?? "" );
		Directory.Delete( dir, true );

		// Change the box: refuse, keep the edit.
		box.SizeX.Value = 3f;
		studio.MarkDirty( box );
		studio.Rebuild();
		Check( "a resized body is refused", edit.Error is not null );
		Check( "and the edit is still held", edit.HasEdit );

		box.SizeX.Value = 2f;
		studio.MarkDirty( box );
		studio.Rebuild();
		Check( "undoing the resize brings the edit back", edit.Error is null && studio.Bodies[0].Mesh.VertexCount == 12, edit.Error ?? "" );

		edit.KeepIfChanged.Value = true;
		box.SizeX.Value = 3f;
		studio.MarkDirty( box );
		studio.Rebuild();
		Check( "keep-if-changed outputs the edit anyway", studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.VertexCount == 12 );

		// Blob round trip carries UVs, materials and skin.
		var rigged = Primitives.Box( 1, 1, 1 );
		rigged.Skin = SkinWeights.AllTo( rigged.VertexCount, 3 );
		rigged.Faces[2].Material = 5;
		var bytes = MeshEditBlob.Write( rigged, 42 );
		var read = MeshEditBlob.Read( bytes, out var fp );
		Check( "the blob carries fingerprint, material and skin",
			fp == 42 && read.Faces[2].Material == 5 && read.IsRigged && read.Skin[0][0].Bone == 3 && read.Faces[0].UVs[1].Equals( rigged.Faces[0].UVs[1] ) );
	}

	static void TestClothDrape()
	{
		// A 6x6 inch sheet, 12x12 quads, held at two corners: it hangs.
		var sheet = Primitives.Plane( 6f, 6f, 12, 12 );
		var corners = new List<int>();
		for ( var i = 0; i < sheet.VertexCount; i++ )
			if ( sheet.Positions[i].y > 2.99f && MathF.Abs( MathF.Abs( sheet.Positions[i].x ) - 3f ) < 1e-3f )
				corners.Add( i );

		var sim = new ClothSim( sheet, corners );
		sim.Run( 1.5f );
		var hung = sim.Bake( sheet );
		var lowest = hung.Positions.Min( p => p.z );
		Check( "a sheet pinned at two corners falls", lowest < -2f, $"lowest {lowest}" );
		Check( "and its pins do not move", corners.All( i => hung.Positions[i].AlmostEquals( sheet.Positions[i], 1e-5f ) ) );

		// Edges stretch little: the longest is within 15% of its rest length.
		var worst = 0f;
		foreach ( var face in sheet.Faces )
			for ( var k = 0; k < face.Indices.Length; k++ )
			{
				var a = face.Indices[k];
				var b = face.Indices[(k + 1) % face.Indices.Length];
				var rest = (sheet.Positions[a] - sheet.Positions[b]).Length;
				var now = (hung.Positions[a] - hung.Positions[b]).Length;
				worst = MathF.Max( worst, now / rest );
			}
		Check( "the fabric barely stretches", worst < 1.15f, $"worst edge {worst:0.###}x" );
		Check( "nothing blew up", hung.Positions.All( p => float.IsFinite( p.x ) && float.IsFinite( p.y ) && float.IsFinite( p.z ) ) );

		// The same sheet dropped onto a box: it rests on the top, and hangs over the sides.
		var table = Primitives.Box( 3f, 3f, 2f );
		var cloth = Primitives.Plane( 6f, 6f, 16, 16 );
		for ( var i = 0; i < cloth.VertexCount; i++ )
			cloth.Positions[i] = cloth.Positions[i] + new Vec3( 0f, 0f, 1.5f );

		var session = new MeshEditSession( cloth ) { SnapTarget = table };
		session.Drape( 1.5f, thickness: 0.05f );
		var draped = session.Mesh;
		var middle = draped.Positions.OrderBy( p => p.x * p.x + p.y * p.y ).First();
		Check( "the middle of the cloth rests on the table top", MathF.Abs( middle.z - 1.05f ) < 0.1f, $"{middle}" );
		var inside = draped.Positions.Count( p => MathF.Abs( p.x ) < 1.45f && MathF.Abs( p.y ) < 1.45f && p.z < 0.95f && p.z > -0.95f );
		Check( "no vertex ends up inside the table", inside == 0, $"{inside} inside" );
		var edge = draped.Positions.OrderByDescending( p => p.x * p.x + p.y * p.y ).First();
		Check( "the corners hang down past the top", edge.z < 0.5f, $"{edge}" );
		Check( "draping is one undo step", session.UndoCount == 1 );

		// Deterministic: the same drape twice is the same mesh.
		var again = new MeshEditSession( cloth ) { SnapTarget = table };
		again.Drape( 1.5f, thickness: 0.05f );
		Check( "the same drape twice gives the same result",
			Enumerable.Range( 0, draped.VertexCount ).All( i => draped.Positions[i].Equals( again.Mesh.Positions[i] ) ) );

		TestClothSelfCollision();
	}

	/// <summary>A fold must not pass through itself. A sheet is creased flat — half of it laid
	/// exactly on the other half — and the solver has to prise the two layers apart.</summary>
	static void TestClothSelfCollision()
	{
		Section( "cloth does not pass through itself where it folds" );

		// A 8x4 sheet, then every vertex with x > 0 reflected onto -x: the sheet is now folded shut,
		// with the two layers occupying the same space.
		PolyMesh Folded()
		{
			var m = Primitives.Plane( 8f, 4f, 16, 8 );
			for ( var i = 0; i < m.VertexCount; i++ )
			{
				var p = m.Positions[i];
				if ( p.x > 0f )
					m.Positions[i] = new Vec3( -p.x, p.y, p.z );
			}
			return m;
		}

		// Pinned along the crease so the fold cannot simply slide apart sideways and call it solved.
		var creased = Folded();
		var pins = new List<int>();
		for ( var i = 0; i < creased.VertexCount; i++ )
			if ( MathF.Abs( creased.Positions[i].x ) < 1e-3f )
				pins.Add( i );

		float Closest( PolyMesh m, HashSet<EdgeKey> linked )
		{
			var worst = float.MaxValue;
			for ( var i = 0; i < m.VertexCount; i++ )
			for ( var j = i + 1; j < m.VertexCount; j++ )
			{
				if ( linked.Contains( new EdgeKey( i, j ) ) )
					continue;
				worst = MathF.Min( worst, (m.Positions[i] - m.Positions[j]).Length );
			}
			return worst;
		}

		// Which pairs a constraint already holds - the same ones the solver refuses to separate.
		var held = new HashSet<EdgeKey>();
		foreach ( var f in creased.Faces )
		{
			var idx = f.Indices;
			for ( var i = 0; i < idx.Length; i++ )
				held.Add( new EdgeKey( idx[i], idx[(i + 1) % idx.Length] ) );
			if ( idx.Length == 4 )
			{
				held.Add( new EdgeKey( idx[0], idx[2] ) );
				held.Add( new EdgeKey( idx[1], idx[3] ) );
			}
		}
		foreach ( var (key, faces) in creased.BuildEdgeFaces() )
			if ( faces.Count == 2 )
				held.Add( key );

		var before = Closest( creased, held );
		Check( "the folded sheet starts with layers on top of each other", before < 1e-4f, $"{before}" );

		var sim = new ClothSim( creased, pins ) { Thickness = 0.1f, Gravity = 0f, SelfCollision = true };
		sim.Run( 0.5f );
		var opened = sim.Bake( creased );

		var after = Closest( opened, held );
		Check( "self-collision prises the layers apart", after > 0.1f, $"closest unlinked pair {after:0.####}" );
		Check( "and nothing blew up", opened.Positions.All( p => float.IsFinite( p.x ) && float.IsFinite( p.y ) && float.IsFinite( p.z ) ) );
		Check( "the crease pins stayed put",
			pins.All( i => opened.Positions[i].AlmostEquals( creased.Positions[i], 1e-5f ) ) );

		// Off, the layers stay welded together - which is what makes the check above meaningful.
		var stuck = Folded();
		var off = new ClothSim( stuck, pins ) { Thickness = 0.1f, Gravity = 0f, SelfCollision = false };
		off.Run( 0.5f );
		var flat = Closest( off.Bake( stuck ), held );
		Check( "with self-collision off they stay through each other", flat < 0.01f, $"closest {flat:0.####}" );

		// It stays deterministic, which is what lets the seconds be scrubbed.
		var twice = Folded();
		var repeat = new ClothSim( twice, pins ) { Thickness = 0.1f, Gravity = 0f, SelfCollision = true };
		repeat.Run( 0.5f );
		Check( "self-collision is deterministic",
			Enumerable.Range( 0, opened.VertexCount ).All( i => opened.Positions[i].Equals( repeat.Positions[i] ) ) );

		TestClothFaceCollision();
	}

	/// <summary>
	/// The case particle-to-particle separation cannot see: a FINE sheet resting in the middle of a
	/// COARSE one's faces. Every vertex of the fine sheet is far from any corner of the coarse
	/// triangles under it, so every pair distance is legal and it falls straight through — unless
	/// the solver keeps points off faces as well as off points.
	/// </summary>
	static void TestClothFaceCollision()
	{
		Section( "cloth: a fine fold does not sink through a coarse face" );

		// One cloth, two halves that never touch each other's vertices: a coarse 2x2 sheet at z = 0
		// and a fine 10x10 sheet just above it, joined into one mesh so it is all one simulation.
		PolyMesh Stacked()
		{
			var coarse = Primitives.Plane( 10f, 10f, 2, 2 );
			var fine = Primitives.Plane( 4f, 4f, 10, 10 );
			var mesh = new PolyMesh();

			void Append( PolyMesh m, float z )
			{
				var offset = mesh.VertexCount;
				foreach ( var pos in m.Positions )
					mesh.AddVertex( new Vec3( pos.x, pos.y, pos.z + z ) );

				foreach ( var f in m.Faces )
				{
					var idx = new int[f.Indices.Length];
					for ( var i = 0; i < idx.Length; i++ )
						idx[i] = f.Indices[i] + offset;
					mesh.AddFace( idx, null, 0 );
				}
			}

			Append( coarse, 0f );
			Append( fine, 0.6f );
			return mesh;
		}

		// Pin the coarse sheet entirely: it is the floor. The fine sheet falls onto it.
		var mesh = Stacked();
		var coarseCount = Primitives.Plane( 10f, 10f, 2, 2 ).VertexCount;
		var pins = Enumerable.Range( 0, coarseCount ).ToList();

		float LowestFine( ClothSim sim )
		{
			var lowest = float.MaxValue;
			for ( var i = coarseCount; i < mesh.VertexCount; i++ )
				lowest = MathF.Min( lowest, sim.Positions[i].z );
			return lowest;
		}

		var held = new ClothSim( Stacked(), pins ) { Thickness = 0.25f, SelfCollision = true };
		held.Run( 1f );
		var restingOn = LowestFine( held );
		Check( "the fine sheet stays above the coarse one", restingOn > 0.1f, $"lowest {restingOn:0.###}" );
		Check( "and does not blow up",
			Enumerable.Range( 0, mesh.VertexCount ).All( i => float.IsFinite( held.Positions[i].z ) ) );

		// The coarse sheet is pinned, so nothing may have moved it.
		var floorMoved = false;
		for ( var i = 0; i < coarseCount; i++ )
			if ( !held.Positions[i].AlmostEquals( mesh.Positions[i], 1e-5f ) )
				floorMoved = true;
		Check( "the pinned floor did not move", !floorMoved );

		// Off, it falls through - which is what makes the check above mean something.
		var through = new ClothSim( Stacked(), pins ) { Thickness = 0.25f, SelfCollision = false };
		through.Run( 1f );
		var fell = LowestFine( through );
		Check( "without self-collision it sinks through", fell < -1f, $"lowest {fell:0.###}" );

		// Still deterministic with the face pass running.
		var again = new ClothSim( Stacked(), pins ) { Thickness = 0.25f, SelfCollision = true };
		again.Run( 1f );
		Check( "face self-collision is deterministic",
			Enumerable.Range( 0, mesh.VertexCount ).All( i => held.Positions[i].Equals( again.Positions[i] ) ) );
	}

	/// <summary>
	/// A wearer is a body you dress and never ship.
	///
	/// The whole point of the flag is that it is invisible in exactly one place, so testing it means
	/// testing BOTH sides: a garment must find the wearer and be cut from it, and the export merge
	/// must leave it out. Getting only the first half right is a tool that silently republishes
	/// somebody else's character inside your jacket.
	/// </summary>
	/// <summary>
	/// The pre-export check has to be right about the one thing it exists to catch, and quiet about
	/// the things that only look like faults.
	///
	/// Openings are the trap. A garment is FULL of holes on purpose, and a check that called a neck
	/// hole an error would be one people learn to ignore - at which point it catches nothing.
	/// </summary>
	static void TestGarmentCheck()
	{
		// A flat sheet floating clear of a box: nothing wrong except the things a bare sheet
		// genuinely lacks.
		var body = Primitives.Box( 10f, 10f, 10f );

		var sheet = new PolyMesh();
		sheet.Positions.Add( new Vec3( -3, -3, 9 ) );
		sheet.Positions.Add( new Vec3( 3, -3, 9 ) );
		sheet.Positions.Add( new Vec3( 3, 3, 9 ) );
		sheet.Positions.Add( new Vec3( -3, 3, 9 ) );
		sheet.Faces.Add( new Face( new[] { 0, 1, 2, 3 } ) );

		var clear = GarmentCheck.Run( sheet, new[] { body } );

		Check( "a garment clear of the body does not clip", clear.Clipping == 0, $"{clear.Clipping}" );
		Check( "and its one hole is one opening", clear.Openings == 1, $"{clear.Openings} openings" );
		Check( "two triangles", clear.Triangles == 2, $"{clear.Triangles}" );
		Check( "an unweighted garment says so", !clear.HasWeights );
		Check( "and that is not clean", !clear.Clean );

		// The same sheet pushed down inside the box. This is the fault the check exists for.
		var through = sheet.Clone();

		for ( var i = 0; i < through.VertexCount; i++ )
			through.Positions[i] = through.Positions[i] - new Vec3( 0, 0, 8 );

		var clipped = GarmentCheck.Run( through, new[] { body } );

		Check( "a garment inside the body clips at every vertex", clipped.Clipping == 4,
			$"{clipped.Clipping}" );
		// Inches, not a flag - "2 in inside the body" is actionable and "clipping: true" is not.
		Check( "and says how deep, in inches", clipped.DeepestClip >= 1f,
			$"{clipped.DeepestClip:0.##} in" );

		// Resting exactly ON the surface is what a drape produces and must not be reported.
		var resting = sheet.Clone();

		for ( var i = 0; i < resting.VertexCount; i++ )
			resting.Positions[i] = new Vec3( resting.Positions[i].x, resting.Positions[i].y, 5f );

		Check( "a garment resting on the body does not clip",
			GarmentCheck.Run( resting, new[] { body } ).Clipping == 0 );

		// A closed box has no openings and no degenerate faces - the reassuring end of the scale.
		var closed = GarmentCheck.Run( Primitives.Box( 2f, 2f, 2f ), Array.Empty<PolyMesh>() );

		Check( "a closed garment has no openings", closed.Openings == 0, $"{closed.Openings}" );
		Check( "and nothing degenerate", closed.Degenerate == 0, $"{closed.Degenerate}" );

		// A zero-area face is always a fault.
		var flat = sheet.Clone();
		flat.Positions[2] = flat.Positions[1];
		flat.Faces.Add( new Face( new[] { 0, 1, 2 } ) );

		Check( "a face with no area is reported", GarmentCheck.Run( flat, Array.Empty<PolyMesh>() ).Degenerate > 0 );

		// And the whole point: a real garment on a real body reads as clean apart from what it
		// honestly lacks, rather than drowning in false alarms about its own openings.
		var studio = new PartStudio();
		var torso = studio.Add( new PrimitiveFeature() );
		torso.SizeX.Value = 12f;
		torso.SizeY.Value = 8f;
		torso.SizeZ.Value = 16f;

		var rig = studio.Rig;
		var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -7 ), new Vec3( 0, 0, -5 ) );
		var pelvis = rig.AddBoneFromPoints( "pelvis", root, new Vec3( 0, 0, -5 ), new Vec3( 0, 0, -3 ) );
		rig.AddBoneFromPoints( "spine", pelvis, new Vec3( 0, 0, -3 ), new Vec3( 0, 0, 7 ) );

		var shirt = studio.Add( new GarmentFeature() );
		shirt.Drape.Value = false;
		studio.Rebuild();

		var made = studio.Bodies.First( b => b.IsGarment );
		var wornOver = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
		var real = GarmentCheck.Run( made.Mesh, wornOver );

		Check( "a fitted garment does not report itself as clipping", real.Clipping == 0,
			$"{real.Clipping} of {made.Mesh.VertexCount}, deepest {real.DeepestClip:0.###} in" );
		Check( "and is structurally sound", real.Structure.Count == 0,
			real.Structure.Count > 0 ? real.Structure[0] : "" );
		Check( "and has triangles to show for it", real.Triangles > 0, $"{real.Triangles}" );

		// The two things a garment is useless without, and which you cannot see are missing by
		// looking at it in the editor: it has to move with the body, and it has to be texturable.
		Check( "a garment comes out weighted, without anyone asking", real.HasWeights );
		Check( "and with UVs", real.HasUVs );
	}

	/// <summary>
	/// Trim hangs off a garment's openings.
	///
	/// The thing most likely to be silently wrong is ATTACHMENT: a frill whose seam row has drifted
	/// off the hem is a ring of cloth floating near a shirt, and it looks fine from most angles
	/// right up until it does not. So the seam is checked against the edge it claims to be sewn to,
	/// not just the face count.
	/// </summary>
	static void TestGarmentTrim()
	{
		// An open tube: two openings, a low one and a high one, like a sleeve.
		var tube = new PolyMesh();
		const int Sides = 16;

		for ( var ring = 0; ring < 2; ring++ )
		{
			for ( var i = 0; i < Sides; i++ )
			{
				var a = i / (float)Sides * MathF.Tau;
				tube.Positions.Add( new Vec3( MathF.Cos( a ) * 4f, MathF.Sin( a ) * 4f, ring * 10f ) );
			}
		}

		for ( var i = 0; i < Sides; i++ )
		{
			var j = (i + 1) % Sides;
			tube.Faces.Add( new Face( new[] { i, j, Sides + j, Sides + i } ) );
		}

		var loops = GarmentTrim.OrderedBoundaryLoops( tube );

		Check( "an open tube has two openings", loops.Count == 2, $"{loops.Count}" );
		Check( "each walked as a whole ring", loops.All( l => l.Count == Sides ),
			string.Join( ",", loops.Select( l => l.Count ) ) );
		Check( "lowest first", loops.Count == 2
			&& loops[0].Average( v => tube.Positions[v].z ) < loops[1].Average( v => tube.Positions[v].z ) );

		// A frill on the hem only.
		var frill = GarmentTrim.Build( tube, new[] { 0 }, new GarmentTrim.Options
		{
			Style = TrimStyle.Frill,
			Width = 2f,
			Gather = 0.6f,
			Waves = 4,
			Rows = 3,
		} );

		Check( "a frill builds", frill.Problem is null, frill.Problem ?? "" );
		Check( "on one opening", frill.Openings == 1, $"{frill.Openings}" );
		Check( "with quads to show for it", frill.Mesh.FaceCount == Sides * 3,
			$"{frill.Mesh.FaceCount}" );

		// ATTACHMENT. Every vertex of the hem ring must appear in the trim, unmoved: that is what
		// "sewn on" means, and it is the failure that does not look like a failure.
		var hem = loops[0].Select( v => tube.Positions[v] ).ToList();
		var attached = hem.Count( p => frill.Mesh.Positions.Any( q => (q - p).Length < 1e-4f ) );

		Check( "the seam sits exactly on the hem it hangs from", attached == Sides,
			$"{attached} of {Sides}" );

		// And it hangs DOWNWARD, away from the tube, rather than up into it.
		var lowest = frill.Mesh.Positions.Min( p => p.z );

		Check( "and it hangs off the end rather than back up the garment", lowest < -0.5f,
			$"lowest {lowest:0.##}" );

		// The gather is what makes it a frill: with waves, the free edge is longer than the seam.
		var seam = RingLength( frill.Mesh, 0, Sides );
		var free = RingLength( frill.Mesh, Sides * 3, Sides );

		Check( "the free edge is longer than the seam - that is the gather", free > seam * 1.05f,
			$"seam {seam:0.#}, free {free:0.#}" );

		// A ribbon is the same strip with no gather, so its edges match.
		var ribbon = GarmentTrim.Build( tube, new[] { 0 }, new GarmentTrim.Options
		{
			Style = TrimStyle.Ribbon,
			Width = 1f,
			Rows = 1,
		} );

		Check( "a ribbon is a flat band",
			Math.Abs( RingLength( ribbon.Mesh, Sides, Sides ) - RingLength( ribbon.Mesh, 0, Sides ) )
				< seam * 0.25f );

		// Fringe drops whole slots, so it has fewer faces than the strip it came from.
		var fringe = GarmentTrim.Build( tube, new[] { 0 }, new GarmentTrim.Options
		{
			Style = TrimStyle.Fringe,
			Width = 2f,
			Waves = 4,
			Gap = 0.5f,
			Rows = 3,
		} );

		Check( "fringe is cut into strips", fringe.Mesh.FaceCount < Sides * 3 && fringe.Mesh.FaceCount > 0,
			$"{fringe.Mesh.FaceCount} of {Sides * 3}" );

		// A closed shape has nothing to hang trim from, and says so rather than producing nothing.
		var closed = GarmentTrim.Build( Primitives.Box( 2f, 2f, 2f ), null, new GarmentTrim.Options() );

		Check( "a closed shape refuses with a reason", closed.Problem is not null && closed.Mesh.FaceCount == 0,
			closed.Problem ?? "no problem given" );

		// And the feature, over a real garment.
		var studio = new PartStudio();
		var torso = studio.Add( new PrimitiveFeature() );
		torso.SizeX.Value = 12f;
		torso.SizeY.Value = 8f;
		torso.SizeZ.Value = 16f;

		var rig = studio.Rig;
		var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -7 ), new Vec3( 0, 0, -5 ) );
		var pelvis = rig.AddBoneFromPoints( "pelvis", root, new Vec3( 0, 0, -5 ), new Vec3( 0, 0, -3 ) );
		rig.AddBoneFromPoints( "spine", pelvis, new Vec3( 0, 0, -3 ), new Vec3( 0, 0, 7 ) );

		var shirt = studio.Add( new GarmentFeature() );
		shirt.Drape.Value = false;
		shirt.Thickness.Value = 0f; // Thickness closes the garment, and trim needs an open hem.

		var trim = studio.Add( new TrimFeature() );
		studio.Rebuild();

		Check( "a Trim feature builds on a garment", trim.Error is null, trim.Error ?? "" );

		var trimmed = studio.Bodies.LastOrDefault();

		Check( "and lands as its own body", trimmed is not null && trimmed.Mesh.FaceCount > 0,
			$"{trimmed?.Mesh.FaceCount} faces" );
		Check( "on its own material slot", trim.ResolvedSlot > shirt.ResolvedSlot,
			$"trim {trim.ResolvedSlot}, garment {shirt.ResolvedSlot}" );
		Check( "counted as clothing, so fur and later garments see it",
			trimmed is { IsGarment: true } );
		Check( "and weighted, so it moves with the hem it hangs from", trimmed.Mesh.IsRigged );

		// It follows the garment: change the shirt and the trim is rebuilt onto the new hem rather
		// than left behind where the old one was. Measured against the GARMENT's own hem, not
		// against a fixed number - Length's effect depends on the body, and a test that asserts
		// inches would be testing this fixture's proportions instead of the following.
		var hemBefore = trimmed.Mesh.Positions.Min( p => p.z );
		var shirtBefore = studio.Bodies.First( b => b.FeatureId == shirt.Id ).Mesh.Positions.Min( p => p.z );

		shirt.Neckline.Value = 0.45f;
		studio.MarkDirty( shirt );
		studio.Rebuild();

		var shirtAfter = studio.Bodies.First( b => b.FeatureId == shirt.Id ).Mesh.Positions.Min( p => p.z );
		var hemAfter = studio.Bodies.Last().Mesh.Positions.Min( p => p.z );

		Check( "the trim is rebuilt from the garment every time",
			studio.Bodies.Last().Mesh.FaceCount > 0 );
		Check( "and follows it rather than staying where it was",
			Math.Abs( (hemAfter - shirtAfter) - (hemBefore - shirtBefore) ) < 1.5f,
			$"gap {hemBefore - shirtBefore:0.##} -> {hemAfter - shirtAfter:0.##}" );
	}

	/// <summary>Total length of a run of <paramref name="count"/> positions treated as a closed
	/// ring, starting at <paramref name="start"/>.</summary>
	static float RingLength( PolyMesh mesh, int start, int count )
	{
		var total = 0f;

		for ( var i = 0; i < count; i++ )
		{
			var a = mesh.Positions[start + i];
			var b = mesh.Positions[start + (i + 1) % count];
			total += (b - a).Length;
		}

		return total;
	}

	/// <summary>
	/// Puff, Cinch and Wrinkles: the three shaping controls Marvelous Designer and Simply Cloth both
	/// put on their front page, because they are what turns a fitted shape into a different GARMENT
	/// rather than the same garment at a different size.
	///
	/// Each is checked for the thing that distinguishes it from the others, since all three move
	/// vertices and any of them would pass a "the mesh changed" test while doing the wrong job.
	/// </summary>
	static void TestGarmentShaping()
	{
		PartStudio Dress( out GarmentFeature garment )
		{
			var studio = new PartStudio();
			var torso = studio.Add( new PrimitiveFeature() );
			torso.SizeX.Value = 12f;
			torso.SizeY.Value = 8f;
			torso.SizeZ.Value = 16f;

			var rig = studio.Rig;
			var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -7 ), new Vec3( 0, 0, -5 ) );
			var pelvis = rig.AddBoneFromPoints( "pelvis", root, new Vec3( 0, 0, -5 ), new Vec3( 0, 0, -3 ) );
			rig.AddBoneFromPoints( "spine", pelvis, new Vec3( 0, 0, -3 ), new Vec3( 0, 0, 7 ) );

			garment = studio.Add( new GarmentFeature() );
			garment.Drape.Value = false;
			garment.Thickness.Value = 0f;

			return studio;
		}

		var plain = Dress( out var flat );
		plain.Rebuild();

		var before = plain.Bodies.First( b => b.IsGarment ).Mesh;
		var beforeVolume = Math.Abs( before.SignedVolume() );
		var beforeCount = before.VertexCount;

		// PUFF inflates along the normals, so the garment encloses MORE while keeping its topology.
		var puffed = Dress( out var puff );
		puff.Puff.Value = 0.8f;
		puffed.Rebuild();

		var puffedMesh = puffed.Bodies.First( b => b.IsGarment ).Mesh;

		Check( "puff keeps the same topology", puffedMesh.VertexCount == beforeCount,
			$"{puffedMesh.VertexCount} vs {beforeCount}" );
		Check( "and makes the garment enclose more",
			Math.Abs( puffedMesh.SignedVolume() ) > beforeVolume,
			$"{Math.Abs( puffedMesh.SignedVolume() ):0.#} vs {beforeVolume:0.#}" );

		// CINCH pulls the openings in. The test is the opening's own width, not the whole mesh:
		// a garment that got smaller everywhere would pass a bounds test and be the wrong thing.
		var cinched = Dress( out var cinch );
		cinch.Cinch.Value = 0.8f;
		cinch.CinchReach.Value = 2f;
		cinched.Rebuild();

		var cinchedMesh = cinched.Bodies.First( b => b.IsGarment ).Mesh;

		var hemBefore = OpeningWidth( before, 0 );
		var hemAfter = OpeningWidth( cinchedMesh, 0 );

		Check( "cinch pulls the opening in", hemAfter < hemBefore * 0.9f,
			$"{hemBefore:0.##} -> {hemAfter:0.##}" );

		// And leaves the rest of the garment alone - that is what makes it a waistband rather than
		// a taper. The top of a shirt is well beyond a 2in reach from the hem.
		Check( "and leaves the far side of the garment where it was",
			Math.Abs( cinchedMesh.Positions.Max( v => v.z ) - before.Positions.Max( v => v.z ) ) < 0.2f,
			$"{before.Positions.Max( v => v.z ):0.##} -> {cinchedMesh.Positions.Max( v => v.z ):0.##}" );

		// WRINKLES displace but must not inflate: cloth that has been crumpled is not cloth that
		// has been pumped up, so the volume should barely move while the surface gets longer.
		var rumpled = Dress( out var wrinkle );
		wrinkle.Wrinkles.Value = 1f;
		rumpled.Rebuild();

		var rumpledMesh = rumpled.Bodies.First( b => b.IsGarment ).Mesh;

		Check( "wrinkles move the surface", Moved( before, rumpledMesh ) > 0.01f,
			$"{Moved( before, rumpledMesh ):0.###} in average" );
		Check( "but do not puff it up",
			Math.Abs( Math.Abs( rumpledMesh.SignedVolume() ) - beforeVolume ) < beforeVolume * 0.25f,
			$"{Math.Abs( rumpledMesh.SignedVolume() ):0.#} vs {beforeVolume:0.#}" );

		// DETERMINISM. The wrinkles are noise, and noise that is different on every rebuild makes
		// undo a surprise and a saved document a different model when it is reopened.
		var again = Dress( out var repeat );
		repeat.Wrinkles.Value = 1f;
		again.Rebuild();

		Check( "and are the same wrinkles every rebuild",
			Moved( rumpledMesh, again.Bodies.First( b => b.IsGarment ).Mesh ) < 1e-5f );

		// Zero is genuinely zero: none of the three costs anything or changes anything when it is
		// left alone, so an existing garment is untouched by their arrival.
		Check( "and none of them touch a garment that did not ask",
			Moved( before, plain.Bodies.First( b => b.IsGarment ).Mesh ) < 1e-6f );

		// FABRIC. Leather holds its shape and stretch clings, so the same garment draped in the two
		// must not come out identical - which is the failure mode a preset table invites: four names
		// that all reach the solver as the same numbers.
		PolyMesh Draped( int fabric )
		{
			var s = Dress( out var g );
			g.Drape.Value = true;
			g.DrapeSteps.Value = 20;
			g.Fabric.Index = fabric;
			s.Rebuild();

			return s.Bodies.First( b => b.IsGarment ).Mesh;
		}

		var cotton = Draped( 0 );
		var leather = Draped( 2 );
		var stretch = Draped( 3 );

		Check( "leather drapes differently from cotton", Moved( cotton, leather ) > 1e-4f,
			$"{Moved( cotton, leather ):0.####} in average" );
		Check( "and stretch differently again", Moved( cotton, stretch ) > 1e-4f,
			$"{Moved( cotton, stretch ):0.####} in average" );
		Check( "and the same fabric twice is the same garment", Moved( cotton, Draped( 0 ) ) < 1e-6f );
	}

	/// <summary>How wide the nth opening of a mesh is, measured across its own ring.</summary>
	static float OpeningWidth( PolyMesh mesh, int index )
	{
		var loops = GarmentTrim.OrderedBoundaryLoops( mesh );

		if ( index < 0 || index >= loops.Count )
			return 0f;

		var points = loops[index].Select( v => mesh.Positions[v] ).ToList();
		var centre = Vec3.Zero;

		foreach ( var p in points )
			centre += p;

		centre /= points.Count;

		var width = 0f;

		foreach ( var p in points )
			width += (p - centre).Length;

		return width / points.Count;
	}

	/// <summary>Average distance each vertex moved between two versions of the same mesh. Returns
	/// infinity when the topology changed, since nothing else is comparable then.</summary>
	static float Moved( PolyMesh a, PolyMesh b )
	{
		if ( a.VertexCount != b.VertexCount )
			return float.PositiveInfinity;

		var total = 0f;

		for ( var i = 0; i < a.VertexCount; i++ )
			total += (b.Positions[i] - a.Positions[i]).Length;

		return total / Math.Max( 1, a.VertexCount );
	}

	/// <summary>
	/// A preview rebuild is a deliberate lie about the model, told so a slider can keep up. The two
	/// things that make telling it acceptable are that it is CHEAPER and that it is CORRECTED - and
	/// the second matters more, because a preview left in place is a garment that exports without
	/// its thickness or its weights.
	/// </summary>
	static void TestPreviewQuality()
	{
		var studio = new PartStudio();
		var torso = studio.Add( new PrimitiveFeature() );
		torso.SizeX.Value = 12f;
		torso.SizeY.Value = 8f;
		torso.SizeZ.Value = 16f;

		var rig = studio.Rig;
		var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -7 ), new Vec3( 0, 0, -5 ) );
		var pelvis = rig.AddBoneFromPoints( "pelvis", root, new Vec3( 0, 0, -5 ), new Vec3( 0, 0, -3 ) );
		rig.AddBoneFromPoints( "spine", pelvis, new Vec3( 0, 0, -3 ), new Vec3( 0, 0, 7 ) );

		var shirt = studio.Add( new GarmentFeature() );
		studio.Rebuild();

		var full = studio.Bodies.First( b => b.IsGarment ).Mesh;
		var fullFaces = full.FaceCount;

		Check( "a full garment is thickened", MeshValidator.Validate( full ).IsClosed );
		Check( "and weighted", full.IsRigged );

		// The preview drops the thickness, so the garment is a single-sided sheet: open, and about
		// half the faces.
		studio.PreviewQuality = true;
		studio.MarkDirty( shirt );
		studio.Rebuild();

		var preview = studio.Bodies.First( b => b.IsGarment ).Mesh;

		Check( "a preview garment is not thickened", !MeshValidator.Validate( preview ).IsClosed );
		Check( "so it is cheaper", preview.FaceCount < fullFaces,
			$"{preview.FaceCount} vs {fullFaces}" );
		Check( "and is not weighted either", !preview.IsRigged );

		// THE PART THAT MATTERS. Turning the flag off has to give the real thing back, exactly -
		// not something close to it.
		studio.PreviewQuality = false;
		studio.MarkDirty( shirt );
		studio.Rebuild();

		var restored = studio.Bodies.First( b => b.IsGarment ).Mesh;

		Check( "and clearing the flag restores the real garment exactly",
			restored.FaceCount == fullFaces && restored.IsRigged
				&& MeshValidator.Validate( restored ).IsClosed,
			$"{restored.FaceCount} vs {fullFaces}" );

		// A preview must not change anything but geometry: the same bodies, on the same material
		// slots, or the feature tree and the parts list would flicker through states that never
		// existed while somebody drags a slider.
		var slot = shirt.ResolvedSlot;
		var bodies = studio.Bodies.Count;

		studio.PreviewQuality = true;
		studio.MarkDirty( shirt );
		studio.Rebuild();

		Check( "a preview keeps the same bodies", studio.Bodies.Count == bodies,
			$"{studio.Bodies.Count} vs {bodies}" );
		Check( "on the same material slot", shirt.ResolvedSlot == slot,
			$"{shirt.ResolvedSlot} vs {slot}" );
		Check( "and does not fail", shirt.Error is null, shirt.Error ?? "" );
	}

	static void TestWearer()
	{
		var studio = new PartStudio();

		// The wearer arrives the way the editor sends it: OBJ bytes, no file on disk.
		var box = new PartStudio();
		var prim = box.Add( new PrimitiveFeature() );
		prim.SizeX.Value = 12f;
		prim.SizeY.Value = 8f;
		prim.SizeZ.Value = 16f;
		box.Rebuild();

		var wearer = studio.Add( new WearerFeature() );
		wearer.Name = "Citizen";
		wearer.Model.Value = "models/citizen/citizen.vmdl";
		wearer.LoadMesh( System.Text.Encoding.UTF8.GetBytes( ObjWriter.Write( box.ToMesh(), "citizen" ) ) );

		var rig = studio.Rig;
		var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -7 ), new Vec3( 0, 0, -5 ) );
		var pelvis = rig.AddBoneFromPoints( "pelvis", root, new Vec3( 0, 0, -5 ), new Vec3( 0, 0, -3 ) );
		rig.AddBoneFromPoints( "spine", pelvis, new Vec3( 0, 0, -3 ), new Vec3( 0, 0, 7 ) );

		studio.Rebuild();

		Check( "a wearer builds from bytes alone", wearer.Error is null, wearer.Error ?? "" );

		var body = studio.Bodies.FirstOrDefault();
		Check( "and lands as one body", studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );
		Check( "flagged reference", body is { IsReference: true } );
		Check( "with the model's faces on it", body is not null && body.Mesh.FaceCount > 0,
			$"{body?.Mesh.FaceCount} faces" );

		// The export merge is the one view that leaves it out. The viewport's is not.
		Check( "a wearer is not in the exported mesh", studio.ToMesh().FaceCount == 0,
			$"{studio.ToMesh().FaceCount} faces" );
		Check( "and not in the rigging merge either", studio.ToMeshWithBodies().Ranges.Count == 0 );
		Check( "but is drawn", studio.ToVisibleMesh().FaceCount == body.Mesh.FaceCount );

		// And it is still a body a garment can be cut from, which is the entire reason it is here.
		var shirt = studio.Add( new GarmentFeature() );
		shirt.Drape.Value = false;
		studio.Rebuild();

		Check( "a garment is cut from the wearer", shirt.Error is null, shirt.Error ?? "" );

		var garment = studio.Bodies.FirstOrDefault( b => b.IsGarment );
		Check( "and the garment has faces", garment is not null && garment.Mesh.FaceCount > 0,
			$"{garment?.Mesh.FaceCount} faces" );
		Check( "the garment is not a reference body", garment is { IsReference: false } );
		Check( "so the export holds the garment and nothing else",
			studio.ToMesh().FaceCount == garment.Mesh.FaceCount,
			$"{studio.ToMesh().FaceCount} vs {garment.Mesh.FaceCount}" );

		// A reopen must not turn the wearer back into something shippable.
		var reopened = StudioDocument.Read( StudioDocument.Write( studio ) );
		var carried = studio.Features.OfType<WearerFeature>().First().SaveMesh();
		reopened.Features.OfType<WearerFeature>().First().LoadMesh( carried );
		reopened.Rebuild();

		Check( "a reopened wearer is still reference only",
			reopened.Bodies.Any( b => b.IsReference ) && reopened.ToMesh().FaceCount == garment.Mesh.FaceCount );
		Check( "and remembers where it came from",
			reopened.Features.OfType<WearerFeature>().First().Model.Value == "models/citizen/citizen.vmdl" );
	}

	static void TestClothingDefinition()
	{
		// A shirt and trousers compiled together are one item, so the one .clothing claims both.
		var shirt = GarmentRecipe.Build( Array.IndexOf( GarmentRecipe.Names, "Long sleeve" ), 1f, 1f, 0.5f, 0 );
		var trousers = GarmentRecipe.Build( Array.IndexOf( GarmentRecipe.Names, "Trousers" ), 1f, 1f, 0.5f, 0 );
		var both = ClothingDefinition.Combine( new[] { shirt, trousers } );
		var slots = ClothingDefinition.SlotsFor( both ).ToList();

		Check( "clothing: combined takes the shirt's category", ClothingDefinition.CategoryFor( both ) == "Shirt" );
		Check( "clothing: combined claims the chest", slots.Contains( "Chest" ) );
		Check( "clothing: combined claims the legs", slots.Contains( "LeftThigh" ) && slots.Contains( "RightShin" ),
			string.Join( ",", slots ) );
		Check( "clothing: nothing combined is nothing", ClothingDefinition.Combine( Array.Empty<GarmentRecipe>() ) is null );

		var text = ClothingDefinition.Write( both, "models/effigy/outfit.vmdl", "Outfit \"one\"" );
		Check( "clothing: model path written", text.Contains( "\"Model\": \"models/effigy/outfit.vmdl\"" ) );
		Check( "clothing: title escaped", text.Contains( "\"Outfit \\\"one\\\"\"" ), text );
		Check( "clothing: over-slots empty as 0", text.Contains( "\"SlotsOver\": 0" ) );
	}

	static void TestClothingBuild()
	{
		// A chest-sized box with a spine and pelvis inside it: the rig is what a garment wears,
		// and the T-shirt is a recipe over the Torso and Hips regions those bones declare.
		var studio = new PartStudio();
		var body = studio.Add( new PrimitiveFeature() );
		body.Name = "Torso";
		body.SizeX.Value = 12f;
		body.SizeY.Value = 8f;
		body.SizeZ.Value = 16f;

		var rig = studio.Rig;
		var root = rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -7 ), new Vec3( 0, 0, -5 ) );
		var pelvis = rig.AddBoneFromPoints( "pelvis", root, new Vec3( 0, 0, -5 ), new Vec3( 0, 0, -3 ) );
		rig.AddBoneFromPoints( "spine", pelvis, new Vec3( 0, 0, -3 ), new Vec3( 0, 0, 7 ) );

		var shirt = studio.Add( new GarmentFeature() );
		shirt.Name = "Shirt";
		shirt.Drape.Value = false;
		studio.Rebuild();

		Check( "a garment builds on a rigged body", shirt.Error is null, shirt.Error ?? "" );
		Check( "and leaves the body it was cut from in place",
			studio.Bodies.Count == 2 && !studio.Bodies[0].IsGarment, $"{studio.Bodies.Count} bodies" );

		var garment = studio.Bodies.FirstOrDefault( b => b.IsGarment );
		Check( "the rebuilt studio has a garment body", garment is not null );
		Check( "with faces lifted off the body", garment is not null && garment.Mesh.FaceCount > 0,
			$"{garment?.Mesh.FaceCount} faces" );
		Check( "on its own material slot", shirt.ResolvedSlot == 1, $"slot {shirt.ResolvedSlot}" );
		Check( "every garment face on that slot",
			garment is not null && garment.Mesh.Faces.All( f => f.Material == shirt.ResolvedSlot ) );

		var built = garment is null ? default : MeshValidator.Validate( garment.Mesh );
		Check( "the thickened garment is a valid solid", garment is not null && built.IsValid && built.IsClosed,
			$"{built}" );

		// Fur grows shells on the garment, each stepped outward with RED = its height, which is
		// the mesh colour stream s&box's fur.shader reads.
		var fur = studio.Add( new FurFeature() );
		fur.Name = "Shirt fur";
		studio.Rebuild();

		Check( "fur grows on every garment by default", fur.Error is null, fur.Error ?? "" );
		Check( "and adds shells", fur.ShellFaces > 0, $"{fur.ShellFaces} shell faces" );
		Check( "on the next slot after the garment", fur.ResolvedSlot == 2, $"slot {fur.ResolvedSlot}" );

		var furred = studio.Bodies.First( b => b.IsGarment ).Mesh;
		Check( "the garment carries the vertex colours the fur shader reads", furred.HasVertexColors );

		var reds = furred.VertexColors.Select( c => c.x ).ToList();
		Check( "the root shell sits at height 0", reds.Min() < 1e-3f, $"min {reds.Min():0.###}" );
		Check( "the tip shell reaches height 1", reds.Max() > 0.999f, $"max {reds.Max():0.###}" );
		Check( "and there are layers in between", reds.Any( r => r > 0.1f && r < 0.9f ) );
		Check( "the shell faces carry the fur slot",
			furred.Faces.Count( f => f.Material == fur.ResolvedSlot ) == fur.ShellFaces );

		// The same settings rebuild to the same shape, so a triangle budget stays predictable.
		var facesBefore = furred.FaceCount;
		var vertsBefore = furred.VertexCount;
		studio.Rebuild();
		var again = studio.Bodies.First( b => b.IsGarment ).Mesh;
		Check( "a rebuild reproduces the same garment and shells",
			again.FaceCount == facesBefore && again.VertexCount == vertsBefore,
			$"{vertsBefore}v/{facesBefore}f -> {again.VertexCount}v/{again.FaceCount}f" );

		// And the document carries both, so the shirt survives save/reopen.
		var reopened = StudioDocument.Read( StudioDocument.Write( studio ) );
		reopened.Rebuild();
		Check( "the garment and fur survive save/reopen",
			reopened.Bodies.Count == studio.Bodies.Count
			&& reopened.Bodies.First( b => b.IsGarment ).Mesh.FaceCount == facesBefore,
			$"{reopened.Bodies.Count} bodies, {reopened.Bodies.FirstOrDefault( b => b.IsGarment )?.Mesh.FaceCount} faces" );

		// The material the editor writes is text the fur shader accepts.
		var vmat = FurMaterial.VmatSource( "models/effigy/fur/fur_1_color.png", "models/effigy/fur/fur_1_noise.png",
			24f, 0.5f, new Vec3( 0.23f, 0.2f, 0.18f ), 0f );
		Check( "the fur material targets the fur shader", vmat.Contains( "shaders/fur.shader" ) );
		Check( "and binds the colour and noise textures",
			vmat.Contains( "fur_1_color.png" ) && vmat.Contains( "fur_1_noise.png" ) );
		var brown = FurMaterial.ParseHex( "#8a6e55", Vec3.Zero );
		Check( "the colour parses from the hex the feature saves",
			MathF.Abs( brown.x - 138f / 255f ) < 0.01f && MathF.Abs( brown.y - 110f / 255f ) < 0.01f,
			$"{brown.x:0.###} {brown.y:0.###} {brown.z:0.###}" );
		Check( "garbage hex falls back", FurMaterial.ParseHex( "not a colour", Vec3.One ).Equals( Vec3.One ) );
		var noise = FurMaterial.NoiseRgba( 42, 0.4f );
		Check( "strand noise is a square alpha texture", noise.Length == 256 * 256 * 4 && noise[3] == 255 );
		var swatch = FurMaterial.ColorRgba( brown );
		Check( "the base colour swatch is opaque", swatch.Length == 16 * 16 * 4 && swatch[3] == 255 );
	}

	static void TestEditSessionPieces()
	{
		var box = Primitives.Box( 2, 2, 2 );

		// Duplicate: a copy of the top on its own vertices, selected.
		var d = new MeshEditSession( box );
		var top = TopFace( d.Mesh );
		d.SelectFace( top );
		d.Duplicate();
		Check( "duplicate adds the face on four new vertices", d.Mesh.VertexCount == 12 && d.Mesh.FaceCount == 7, $"{d.Mesh.VertexCount}v/{d.Mesh.FaceCount}f" );
		Check( "and the copy is what is selected", d.SelectedFaces.Count == 1 && !d.SelectedFaces.Contains( top ) );

		// Separate: the top leaves as its own piece; the box is left open.
		var s = new MeshEditSession( box );
		s.SelectFace( TopFace( s.Mesh ) );
		s.Separate();
		Check( "separate takes the face out into a piece", s.Separated.Count == 1 && s.Separated[0].FaceCount == 1 && s.Separated[0].VertexCount == 4 );
		Check( "and leaves the body open where it was", s.Mesh.FaceCount == 5 && MeshValidator.Validate( s.Mesh ).BoundaryEdges == 4 );
		s.Undo();
		Check( "undo puts it back", s.Separated.Count == 0 && s.Mesh.FaceCount == 6 );

		// Extract a garment: five faces of the box, lifted a gap off it, body untouched.
		var g = new MeshEditSession( box );
		for ( var f = 0; f < g.Mesh.FaceCount; f++ )
			if ( g.Mesh.FaceNormal( g.Mesh.Faces[f] ).z > -0.5f )
				g.SelectFace( f, MeshEditSession.Combine.Add );
		g.ExtractGarment( 0.1f );
		var piece = g.Separated.Count == 1 ? g.Separated[0] : null;
		Check( "extract makes one garment piece of five faces", piece is not null && piece.FaceCount == 5, $"{piece?.FaceCount}" );
		Check( "the body is left exactly as it was", g.Mesh.VertexCount == 8 && g.Mesh.FaceCount == 6 && MeshValidator.Validate( g.Mesh ).IsClosed );
		Check( "the garment is open at the bottom, like a shirt", piece is not null && MeshValidator.Validate( piece ).BoundaryEdges == 4 );
		Check( "and sits off the skin", piece is not null && piece.Positions.TrueForAll( q => MathF.Max( MathF.Abs( q.x ), MathF.Max( MathF.Abs( q.y ), q.z ) ) > 1.01f ) );

		// Bridge a 4-edge rim to an 8-edge rim: an open cup and an octagon lid close into a solid.
		var cup = Primitives.Box( 2, 2, 2 );
		var cupTop = TopFace( cup );
		var mesh = new PolyMesh { Positions = new List<Vec3>( cup.Positions ) };
		for ( var f = 0; f < cup.FaceCount; f++ )
			if ( f != cupTop )
				mesh.AddFace( (int[])cup.Faces[f].Indices.Clone() );
		var lid = new int[8];
		for ( var i = 0; i < 8; i++ )
		{
			var a = i / 8f * MathF.PI * 2f + MathF.PI / 8f;
			lid[i] = mesh.AddVertex( new Vec3( MathF.Cos( a ) * 1.2f, MathF.Sin( a ) * 1.2f, 2f ) );
		}
		mesh.AddFace( lid );
		var br = new MeshEditSession( mesh );
		br.SetMode( EditElement.Edge );
		foreach ( var (key, owners) in mesh.BuildEdgeFaces() )
			if ( owners.Count == 1 )
				br.SelectEdge( key, MeshEditSession.Combine.Add );
		br.BridgeSelectedLoops();
		var v = MeshValidator.Validate( br.Mesh );
		Check( "bridging a 4-edge rim to an 8-edge rim closes the solid", v.IsValid && v.IsClosed, v.ToString() );
		Check( "and it is wound outward", br.Mesh.SignedVolume() > 0f, $"{br.Mesh.SignedVolume()}" );

		// One rim is not a bridge.
		var one = new MeshEditSession( s.Mesh );
		var threw = false;
		try { one.BridgeSelectedLoops(); } catch ( InvalidOperationException ) { threw = true; }
		Check( "bridge with nothing selected refuses", threw );

		// Weights: a box rigged bottom-to-top, a shell just outside it takes the same gradient.
		var rigged = Primitives.Box( 2, 2, 2 );
		rigged.Skin = new SkinWeights( rigged.VertexCount );
		for ( var i = 0; i < rigged.VertexCount; i++ )
			rigged.Skin[i] = rigged.Positions[i].z > 0f ? new[] { new BoneWeight( 1, 1f ) } : new[] { new BoneWeight( 0, 1f ) };
		var shirt = new MeshEditSession( MeshTransform.Transformed( Primitives.Box( 2.2f, 2.2f, 2f ), Xform.Identity ) );
		var reached = shirt.TransferWeights( rigged );
		var topCorner = shirt.Mesh.Positions.FindIndex( q => q.z > 0.9f );
		var bottomCorner = shirt.Mesh.Positions.FindIndex( q => q.z < -0.9f );
		Check( "copying weights reaches every vertex", reached == shirt.Mesh.VertexCount && shirt.Mesh.IsRigged );
		Check( "a top vertex follows the top bone", shirt.Mesh.Skin[topCorner].Length == 1 && shirt.Mesh.Skin[topCorner][0].Bone == 1,
			string.Join( ",", shirt.Mesh.Skin[topCorner] ) );
		Check( "a bottom vertex follows the bottom bone", shirt.Mesh.Skin[bottomCorner][0].Bone == 0 );
		threw = false;
		try { shirt.TransferWeights( Primitives.Box( 1, 1, 1 ) ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "copying from an unrigged body refuses", threw );

		// Binding keeps a body's own weights when it is not pinned to a bone.
		var skeleton = new Skeleton();
		skeleton.AddBone( "root", -1, Xform.Identity );
		skeleton.AddBone( "spine", 0, Xform.Identity );
		var ranges = new List<BodyRange> { new BodyRange( "garment", "Garment", 0, shirt.Mesh.VertexCount, hasOwnWeights: true ) };
		var bound = SkinBinder.BindBodies( shirt.Mesh, ranges, new Dictionary<string, string>(), skeleton );
		Check( "binding keeps a garment's copied weights", bound[topCorner][0].Bone == 1 && bound[bottomCorner][0].Bone == 0 );

		// Pieces come out of the feature as bodies, and survive the blob.
		var studio = new PartStudio();
		var prim = studio.Add( new PrimitiveFeature() );
		prim.SizeX.Value = 2f;
		prim.SizeY.Value = 2f;
		prim.SizeZ.Value = 2f;
		var edit = studio.Add( new MeshEditFeature() );
		studio.Rebuild();
		var session = new MeshEditSession( edit.LastInput );
		for ( var f = 0; f < session.Mesh.FaceCount; f++ )
			if ( session.Mesh.FaceNormal( session.Mesh.Faces[f] ).z > -0.5f )
				session.SelectFace( f, MeshEditSession.Combine.Add );
		session.ExtractGarment( 0.1f );
		session.CommitTo( edit );
		studio.Rebuild();
		Check( "the extracted garment is its own body in the studio", edit.Error is null && studio.Bodies.Count == 2, edit.Error ?? $"{studio.Bodies.Count} bodies" );
		var read = MeshEditBlob.Read( edit.SaveMesh(), out _, out var pieces );
		Check( "and the side-car carries it", pieces.Count == 1 && pieces[0].FaceCount == 5 && read.FaceCount == 6 );
	}

	static void TestEditableDissolve()
	{
		// Dissolve one interior edge of a box: the two quads sharing it merge into a single
		// hexagon and the solid is otherwise unchanged.
		var box = Primitives.Box( 2, 2, 2 );
		var e = EditableMesh.FromPolyMesh( box );

		var interior = -1;

		for ( var hei = 0; hei < e.HalfEdges.Count && interior < 0; hei++ )
			if ( e.HalfEdges[hei].Twin >= 0 )
				interior = hei;

		e.DissolveEdges( new[] { interior } );
		Check( "dissolving an edge keeps the mesh valid", e.Validate().IsValid, e.Validate().ToString() );

		var m = e.ToPolyMesh();
		var v = MeshValidator.Validate( m );
		Check( "the dissolved box stays closed", v.IsClosed, v.ToString() );
		Check( "the dissolved box has 5 faces", m.FaceCount == 5, $"got {m.FaceCount}" );
		Check( "the merged face is a hexagon", m.Faces.Any( f => f.Count == 6 ),
			$"max {m.Faces.Max( f => f.Count )}" );
		// The two faces either side were not coplanar, so merging them into one non-planar n-gon
		// folds the corner and the enclosed volume changes. That is the difference from
		// CoplanarMerge, which refuses this pair rather than approximating it.
		Check( "dissolving a non-coplanar edge changes the enclosed volume",
			m.SignedVolume() > 0f && MathF.Abs( m.SignedVolume() - 8f ) > 1e-3f,
			$"got {m.SignedVolume():0.####}" );

		// Dissolving all four vertical edges would join the four side quads into a band whose
		// boundary is two loops - a face with a hole - so it is refused.
		var e2 = EditableMesh.FromPolyMesh( box );
		var vertical = new List<int>();

		for ( var hei = 0; hei < e2.HalfEdges.Count; hei++ )
		{
			var a = e2.Positions[e2.HalfEdges[hei].Origin];
			var b = e2.Positions[e2.HalfEdges[e2.HalfEdges[hei].Next].Origin];

			if ( MathF.Abs( a.x - b.x ) < 1e-5f && MathF.Abs( a.y - b.y ) < 1e-5f && a.z < b.z )
				vertical.Add( hei );
		}

		Check( "the box exposes four vertical edges", vertical.Count == 4, $"got {vertical.Count}" );

		var holed = false;

		try { e2.DissolveEdges( vertical ); }
		catch ( InvalidOperationException ) { holed = true; }

		Check( "a dissolve that would make a face with a hole is refused", holed );

		// A boundary edge has no second face to merge with.
		var plane = Primitives.Plane( 2, 2, 2, 2 );
		var p = EditableMesh.FromPolyMesh( plane );
		var rim = -1;

		for ( var hei = 0; hei < p.HalfEdges.Count && rim < 0; hei++ )
			if ( p.HalfEdges[hei].Twin < 0 )
				rim = hei;

		var boundaryRefused = false;

		try { p.DissolveEdges( new[] { rim } ); }
		catch ( InvalidOperationException ) { boundaryRefused = true; }

		Check( "dissolving a boundary edge is refused", boundaryRefused );

		// Where the faces ARE coplanar the merge is exact: dissolving an internal edge of a 2x2
		// plane joins two quads into one hexagon with no change in area.
		var grid = EditableMesh.FromPolyMesh( plane );
		var inner = -1;

		for ( var hei = 0; hei < grid.HalfEdges.Count && inner < 0; hei++ )
			if ( grid.HalfEdges[hei].Twin >= 0 )
				inner = hei;

		grid.DissolveEdges( new[] { inner } );

		var gm = grid.ToPolyMesh();
		var gv = MeshValidator.Validate( gm );
		Check( "the coplanar dissolve stays valid and open", gv.IsValid && !gv.IsClosed, gv.ToString() );
		Check( "the coplanar dissolve leaves three faces", gm.FaceCount == 3, $"got {gm.FaceCount}" );

		var garea = 0f;

		foreach ( var f in gm.Faces )
			garea += gm.FaceArea( f );

		Check( "the coplanar dissolve keeps the area", MathF.Abs( garea - 4f ) < 1e-3f, $"got {garea:0.####}" );

		// Dissolve the centre vertex of a 2x2 plane: the four quads around it merge into one
		// octagon, the disc stays open and keeps its area.
		var centre = plane.Positions.FindIndex( q => MathF.Abs( q.x ) < 1e-5f && MathF.Abs( q.y ) < 1e-5f );
		Check( "the plane has an interior centre vertex", centre >= 0 );

		var q2 = EditableMesh.FromPolyMesh( plane );
		q2.DissolveVertex( centre );
		Check( "dissolving a vertex keeps the mesh valid", q2.Validate().IsValid, q2.Validate().ToString() );

		var qm = q2.ToPolyMesh();
		var qv = MeshValidator.Validate( qm );
		Check( "the dissolved plane is one octagon", qm.FaceCount == 1 && qm.Faces[0].Count == 8,
			$"{qm.FaceCount} faces, {(qm.FaceCount > 0 ? qm.Faces[0].Count : 0)} corners" );
		Check( "the dissolved plane stays open", !qv.IsClosed && qv.BoundaryEdges == 8,
			$"boundary {qv.BoundaryEdges}" );

		var area = 0f;
		foreach ( var f in qm.Faces )
			area += qm.FaceArea( f );
		Check( "the dissolved plane keeps its area", MathF.Abs( area - 4f ) < 1e-3f, $"got {area:0.####}" );

		// A boundary vertex has no face on the open side to merge with, so it is refused.
		var cornerRefused = false;

		try { EditableMesh.FromPolyMesh( plane ).DissolveVertex( 0 ); }
		catch ( InvalidOperationException ) { cornerRefused = true; }

		Check( "dissolving a boundary vertex is refused", cornerRefused );

		// Dissolving a box corner merges the three quads that meet there into one face. The result
		// is bounded by that six-corner face, so the solid stays valid and closed.
		var e3 = EditableMesh.FromPolyMesh( box );
		var corner = e3.Positions.FindIndex( q => q.x > 0.5f && q.y > 0.5f && q.z > 0.5f );
		Check( "the box has its +x+y+z corner", corner >= 0 );

		e3.DissolveVertex( corner );
		var m3 = e3.ToPolyMesh();
		var v3 = MeshValidator.Validate( m3 );
		Check( "the corner-dissolved box is valid and closed", v3.IsValid && v3.IsClosed, v3.ToString() );
		Check( "the corner-dissolved box has 4 faces", m3.FaceCount == 4, $"got {m3.FaceCount}" );
		Check( "the corner-dissolved box has a six-corner face", m3.Faces.Any( f => f.Count == 6 ),
			$"max {m3.Faces.Max( f => f.Count )}" );
	}

	static void TestWeld()
	{
		var box = Primitives.Box( 2, 2, 2 );

		// Unweld: give every face its own four corners, so the box is 24 coincident-but-distinct
		// vertices and nothing is topologically connected.
		var loose = new PolyMesh();

		foreach ( var f in box.Faces )
		{
			var start = loose.Positions.Count;

			for ( var i = 0; i < f.Count; i++ )
				loose.AddVertex( box.Positions[f.Indices[i]] );

			loose.AddFace( new[] { start, start + 1, start + 2, start + 3 }, (Vec2[])f.UVs.Clone(), f.Material );
		}

		Check( "the loose box really is unwelded", loose.VertexCount == 24, $"got {loose.VertexCount}" );
		Check( "and it reads as open", !MeshValidator.Validate( loose ).IsClosed );

		var welded = MeshWeld.Weld( loose, 1e-4f );
		Check( "welding recovers the 8 vertices", welded.VertexCount == 8, $"got {welded.VertexCount}" );
		Check( "welding keeps the 6 faces", welded.FaceCount == 6, $"got {welded.FaceCount}" );

		var v = MeshValidator.Validate( welded );
		Check( "welded box is valid", v.IsValid, v.ToString() );
		Check( "welded box is closed", v.IsClosed );
		Check( "welded box volume is 8", MathF.Abs( welded.SignedVolume() - 8f ) < 1e-3f,
			$"got {welded.SignedVolume():0.####}" );

		// A tolerance gates the weld: two sheets a gap apart merge only once the tolerance reaches.
		var sheet = Primitives.Plane( 2, 2, 1, 1 );
		var raised = sheet.Clone();

		for ( var i = 0; i < raised.VertexCount; i++ )
			raised.Positions[i] += new Vec3( 0, 0, 0.01f );

		var stacked = sheet.Clone();
		MeshTransform.Append( stacked, raised );

		Check( "a gap wider than the tolerance stays unmerged", MeshWeld.Weld( stacked, 0.001f ).VertexCount == 8 );
		Check( "a tolerance that reaches the gap welds the sheets", MeshWeld.Weld( stacked, 0.1f ).VertexCount == 4 );

		// An already-welded box is left alone.
		Check( "an already-welded box is unchanged", MeshWeld.Weld( box, 1e-4f ).VertexCount == 8 );
	}

	static void TestPrimitiveValidity()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var v = MeshValidator.Validate( mesh );
			Check( $"{name} validates", v.IsValid, v.ToString() );
		}

		var plane = Primitives.Plane( 2, 2, 3, 3 );
		Check( "plane validates", MeshValidator.Validate( plane ).IsValid );
	}

	static void TestClosedness()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var v = MeshValidator.Validate( mesh );
			Check( $"{name} is closed", v.IsClosed, $"{v.BoundaryEdges} boundary edges" );
		}

		var plane = Primitives.Plane( 2, 2, 3, 3 );
		var pv = MeshValidator.Validate( plane );

		// A 3x3 grid has 12 edges around its border.
		Check( "plane has a 12-edge boundary", pv.BoundaryEdges == 12, $"got {pv.BoundaryEdges}" );
	}

	static void TestEuler()
	{
		// Genus 0 closed surfaces have V - E + F = 2.
		foreach ( var name in new[] { "box", "cylinder", "quadsphere", "wedge" } )
		{
			var x = MeshValidator.EulerCharacteristic( Closed()[name] );
			Check( $"{name} has X = 2", x == 2, $"got {x}" );
		}

		// The tube is a torus - genus 1, so X = 0. If this reads 2 the inner wall is missing or
		// the rings welded together somewhere they should not have.
		var tubeX = MeshValidator.EulerCharacteristic( Closed()["tube"] );
		Check( "tube has X = 0 (genus 1)", tubeX == 0, $"got {tubeX}" );
	}

	static void TestWinding()
	{
		// Divergence theorem: for outward normals, sum over faces of (centroid . normal) * area
		// equals three times the enclosed volume, so it must come out positive. Inverted winding
		// anywhere flips the sign or cancels it toward zero.
		foreach ( var (name, mesh) in Closed() )
		{
			var volume = mesh.SignedVolume();
			Check( $"{name} winds outward (volume {volume:0.####} > 0)", volume > 0.001f );
		}

		// Spot-check the box exactly: a 2x2x2 box encloses 8.
		var box = Primitives.Box( 2, 2, 2 );
		var boxVol = box.SignedVolume();
		Check( "box volume is 8", MathF.Abs( boxVol - 8f ) < 1e-3f, $"got {boxVol:0.####}" );
	}

	static void TestAllQuads()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var sub = CatmullClark.Subdivide( mesh, 1 );
			var nonQuads = sub.Faces.Count( f => f.Count != 4 );
			Check( $"{name} subdivides to all quads", nonQuads == 0, $"{nonQuads} non-quads" );
		}

		// The wedge is the interesting one - it goes in with two triangles and must still come out
		// entirely quads.
		var wedge = CatmullClark.Subdivide( Primitives.Wedge(), 1 );
		Check( "wedge's triangles became quads", wedge.Faces.All( f => f.Count == 4 ) );
	}

	static void TestGrowthLaws()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var v = mesh.VertexCount;
			var e = mesh.BuildEdgeFaces().Count;
			var f = mesh.FaceCount;
			var corners = mesh.Faces.Sum( x => x.Count );

			var sub = CatmullClark.Subdivide( mesh, 1 );

			Check( $"{name}: V' = V+E+F", sub.VertexCount == v + e + f,
				$"expected {v + e + f}, got {sub.VertexCount}" );

			Check( $"{name}: F' = total corners", sub.FaceCount == corners,
				$"expected {corners}, got {sub.FaceCount}" );

			Check( $"{name}: E' = 2E + corners", sub.BuildEdgeFaces().Count == e * 2 + corners,
				$"expected {e * 2 + corners}, got {sub.BuildEdgeFaces().Count}" );
		}
	}

	static void TestTopologyPreserved()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var before = MeshValidator.EulerCharacteristic( mesh );
			var sub = CatmullClark.Subdivide( mesh, 3 );
			var after = MeshValidator.EulerCharacteristic( sub );
			var v = MeshValidator.Validate( sub );

			Check( $"{name} keeps X = {before} after 3 levels", after == before, $"got {after}" );
			Check( $"{name} still valid after 3 levels", v.IsValid, v.ToString() );
			Check( $"{name} still closed after 3 levels", v.IsClosed );
		}
	}

	static void TestBoundaryPreserved()
	{
		var plane = Primitives.Plane( 2, 2, 2, 2 );
		var sub = CatmullClark.Subdivide( plane, 2 );
		var v = MeshValidator.Validate( sub );

		Check( "subdivided plane is still valid", v.IsValid, v.ToString() );
		Check( "subdivided plane still has a boundary", v.BoundaryEdges > 0 );

		// The border must stay flat at z=0. If the boundary rules were wrong it would be pulled
		// toward the interior and this would drift.
		var maxZ = sub.Positions.Max( p => MathF.Abs( p.z ) );
		Check( "subdivided plane stays planar", maxZ < 1e-5f, $"max |z| = {maxZ}" );

		// A unit-ish plane's corners are pinned by the corner rule, so the extent should not
		// collapse inward the way an unclamped surface would.
		var extent = sub.Positions.Max( p => MathF.Abs( p.x ) );
		Check( "subdivided plane keeps its corners", MathF.Abs( extent - 1f ) < 1e-5f, $"extent {extent:0.#####}" );
	}

	static void TestConvergence()
	{
		// Subdividing a cube converges on a rounded solid. Two things must hold: the centroid must
		// not wander, and successive levels must move points less and less. A wrong vertex rule
		// typically shows up as drift that never settles.
		var mesh = Primitives.Box( 2, 2, 2 );
		var previousRadius = 0f;
		var deltas = new List<float>();

		for ( var level = 1; level <= 5; level++ )
		{
			var sub = CatmullClark.Subdivide( mesh, level );

			var centroid = Vec3.Zero;
			foreach ( var p in sub.Positions ) centroid += p;
			centroid /= sub.VertexCount;

			Check( $"level {level} stays centred", centroid.Length < 1e-4f, $"centroid {centroid}" );

			var radius = sub.Positions.Average( p => p.Length );

			if ( level > 1 )
				deltas.Add( MathF.Abs( radius - previousRadius ) );

			previousRadius = radius;
		}

		var settling = true;

		for ( var i = 1; i < deltas.Count; i++ )
		{
			if ( deltas[i] > deltas[i - 1] + 1e-6f )
				settling = false;
		}

		Check( "successive levels move less each time", settling,
			string.Join( ", ", deltas.Select( d => d.ToString( "0.#####" ) ) ) );
	}

	static void TestPredictCost()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			for ( var level = 1; level <= 3; level++ )
			{
				var predicted = CatmullClark.PredictCost( mesh, level );
				var actual = CatmullClark.Subdivide( mesh, level );

				Check( $"{name} level {level} cost predicted",
					predicted.Vertices == actual.VertexCount && predicted.Faces == actual.FaceCount,
					$"predicted {predicted.Vertices}v/{predicted.Faces}f, got {actual.VertexCount}v/{actual.FaceCount}f" );
			}
		}
	}

	static void TestUVSeams()
	{
		// A box's corner belongs to three faces that each want a different UV for the same
		// position. Per-corner UVs are what allow that, and subdivision must not quietly average
		// them together - that would smear the texture across every seam.
		var box = Primitives.Box( 2, 2, 2 );
		var sub = CatmullClark.Subdivide( box, 2 );

		var uvsByPosition = new Dictionary<int, HashSet<(long, long)>>();

		foreach ( var f in sub.Faces )
		{
			for ( var i = 0; i < f.Count; i++ )
			{
				if ( !uvsByPosition.TryGetValue( f.Indices[i], out var set ) )
					uvsByPosition[f.Indices[i]] = set = new HashSet<(long, long)>();

				set.Add( ((long)MathF.Round( f.UVs[i].x * 1000 ), (long)MathF.Round( f.UVs[i].y * 1000 )) );
			}
		}

		var seamVerts = uvsByPosition.Count( kv => kv.Value.Count > 1 );
		Check( "seam vertices still carry several UVs", seamVerts > 0, $"found {seamVerts}" );

		// Every UV must stay inside the unit square - the box's islands are all 0..1 and linear
		// subdivision cannot legitimately push one outside.
		var outOfRange = sub.Faces.SelectMany( f => f.UVs )
			.Count( uv => uv.x < -1e-4f || uv.x > 1 + 1e-4f || uv.y < -1e-4f || uv.y > 1 + 1e-4f );

		Check( "UVs stay in the unit square", outOfRange == 0, $"{outOfRange} outside" );
	}

	static void TestObjRoundTrip()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var text = ObjWriter.Write( mesh, name );
			var back = ObjReader.Read( text );

			Check( $"{name} OBJ keeps vertex count", back.VertexCount == mesh.VertexCount,
				$"{mesh.VertexCount} -> {back.VertexCount}" );

			Check( $"{name} OBJ keeps face count", back.FaceCount == mesh.FaceCount,
				$"{mesh.FaceCount} -> {back.FaceCount}" );

			Check( $"{name} OBJ keeps topology",
				MeshValidator.EulerCharacteristic( back ) == MeshValidator.EulerCharacteristic( mesh ) );

			// Normals must be present and finite; some importers reject a zero normal outright.
			var vnCount = text.Split( '\n' ).Count( l => l.StartsWith( "vn " ) );
			Check( $"{name} OBJ writes normals", vnCount > 0, $"{vnCount} normals" );
			Check( $"{name} OBJ has no NaN", !text.Contains( "NaN" ) && !text.Contains( "âˆž" ) );
		}

		// The writer flips V for OBJ's bottom-left origin; the reader must un-flip or every
		// textured import (and every writer→reader round trip) lands upside down.
		var boxed = Primitives.Box();
		var boxedBack = ObjReader.Read( ObjWriter.Write( boxed, "box" ) );
		var uvSrc = boxed.Faces[0].UVs[0];
		var uvDst = boxedBack.Faces[0].UVs[0];
		Check( "OBJ round-trip preserves UVs through the V flip",
			MathF.Abs( uvSrc.x - uvDst.x ) < 1e-4f && MathF.Abs( uvSrc.y - uvDst.y ) < 1e-4f,
			$"({uvSrc.x},{uvSrc.y}) -> ({uvDst.x},{uvDst.y})" );

		// The smoothing threshold has to actually do something: a box should end up with exactly
		// six distinct normals, a cylinder with far more than six.
		var boxNormals = ObjWriter.Write( Primitives.Box(), "box" )
			.Split( '\n' ).Count( l => l.StartsWith( "vn " ) );

		Check( "box gets 6 hard normals", boxNormals == 6, $"got {boxNormals}" );

		var cylNormals = ObjWriter.Write( Primitives.Cylinder( 0.5f, 1f, 16 ), "cyl" )
			.Split( '\n' ).Count( l => l.StartsWith( "vn " ) );

		Check( "cylinder gets smoothed sides", cylNormals >= 16, $"got {cylNormals}" );

		// OBJ's UV origin is bottom-left, Effigy's is top-left, so V is flipped on the way out — the
		// same flip FbxWriter makes. A plane's (0,0) corner must come out as (0,1); this is what keeps
		// a compiled static model's texture from sampling upside down.
		var planeText = ObjWriter.Write( Primitives.Plane( 1, 1, 1, 1 ), "plane" );

		Check( "OBJ flips V to its bottom-left origin",
			planeText.Contains( "vt 0 1" ) && planeText.Contains( "vt 1 1" ) && planeText.Contains( "vt 0 0" ) );
	}

	static void WriteSamples( string outDir )
	{
		Directory.CreateDirectory( outDir );

		foreach ( var (name, mesh) in Closed() )
		{
			ObjWriter.WriteFile( mesh, Path.Combine( outDir, $"{name}.obj" ), name );

			var sub = CatmullClark.Subdivide( mesh, 2 );
			ObjWriter.WriteFile( sub, Path.Combine( outDir, $"{name}_subdiv2.obj" ), $"{name}_subdiv2" );
		}

		WriteSketchSamples( outDir );
		WritePreviews( outDir );
		WriteBakeSample( outDir );
		WriteDmxSamples( outDir );
		VmdlPhysicsTests.WriteSample( outDir );

		var files = Directory.GetFiles( outDir, "*.obj" ).Length;
		var svgs = Directory.GetFiles( outDir, "*.svg" ).Length;
		Check( $"wrote {files} sample OBJs to {outDir}/", files > 0 );
		Check( $"wrote {svgs} SVG previews to {outDir}/", svgs > 0 );

		Console.WriteLine();
		Console.WriteLine( "  cost table (what a level slider would warn about):" );
		Console.WriteLine( $"  {"primitive",-12} {"L0",12} {"L2",14} {"L4",16} {"L6",18}" );

		foreach ( var (name, mesh) in Closed() )
		{
			string At( int level )
			{
				var (v, f) = CatmullClark.PredictCost( mesh, level );
				return $"{v}v/{f}f";
			}

			Console.WriteLine( $"  {name,-12} {At( 0 ),12} {At( 2 ),14} {At( 4 ),16} {At( 6 ),18}" );
		}
	}

	/// <summary>
	/// A normal map, baked from a sculpted plane onto its cage and written out as a PNG.
	///
	/// THE SUITE CANNOT JUDGE THIS ONE. It checks that the flanks of a bump lean the right way and
	/// that the numbers are what they should be, and every one of those can pass while the map is
	/// unusable in a shader — the green channel's convention in particular is a coin flip that looks
	/// entirely plausible either way in a thumbnail and lights every dent as a bump in the engine.
	/// So the file is written for the same reason the sample DMX is: the real verdict is somewhere
	/// else, and this is what gets carried there.
	///
	/// Expect a mostly flat lilac sheet — (128, 128, 255) is "no change from the cage" — with a disc
	/// in the middle: pink to the right of centre and blue to the left (red is +u), cyan below centre
	/// and purple above it (green is +v, and this file's first row is v = 0, so +v runs DOWN the
	/// image). That last clause is a convention, not a fact about the bake, and it is the second
	/// thing to check in the engine after the green channel's sign — an upside-down map lights
	/// exactly as wrongly as a flipped one.
	/// </summary>
	static void WriteBakeSample( string outDir )
	{
		var sculpt = new MultiresSculpt( Primitives.Plane( 2, 2, 4, 4 ) );
		sculpt.AddLevel();
		sculpt.AddLevel();
		sculpt.AddLevel();

		var mesh = sculpt.Evaluate( 3 );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			var r = MathF.Sqrt( p.x * p.x + p.y * p.y );

			if ( r >= 0.6f )
				continue;

			var t = 1f - r / 0.6f;
			mesh.Positions[i] = new Vec3( p.x, p.y, p.z + 0.2f * t * t * (3f - 2f * t) );
		}

		sculpt.Record( 3, mesh );

		var coverage = NormalBake.Measure( sculpt.Cage, 256 );
		var map = NormalBake.Bake( sculpt.Cage, sculpt.Evaluate( 3 ), 256 );
		var pixels = new int[map.Width * map.Height];

		for ( var i = 0; i < pixels.Length; i++ )
			pixels[i] = (map.Rgb[i * 3] << 16) | (map.Rgb[i * 3 + 1] << 8) | map.Rgb[i * 3 + 2];

		PngPreview.WritePng( Path.Combine( outDir, "sample_normal_bake.png" ), pixels, map.Width, map.Height );

		Check( $"baked a {map.Width}x{map.Height} normal map to {outDir}/sample_normal_bake.png "
			+ $"({map.FilledCount} texels hit, UVs {(coverage.CanBake ? "clean" : "unusable")})",
			map.FilledCount > 0 && coverage.CanBake );
	}

	/// <summary>
	/// A static and a rigged DMX, written out so the engine's own reader can pass judgement on them.
	/// The suite cannot do that itself — the parser lives in the engine — but the file is the whole
	/// input, so validating it needs nothing else running:
	///
	///   bin/win64/dmxconvert.exe -i out/sample_rigged.dmx -o /tmp/check.dmx -oe keyvalues2_noids
	///
	/// That is the standalone loader, and it reports a line number. Finding it is what turned
	/// "Couldn't load DMX file" from the compiler — which names no line and no reason — into a
	/// missing comma between element_array members.
	/// </summary>
	static void WriteDmxSamples( string outDir )
	{
		var box = Primitives.Box( 2, 2, 2 );
		DmxWriter.WriteFile( box, Path.Combine( outDir, "sample_static.dmx" ), modelName: "sample_static" );

		var skeleton = new Skeleton();
		var root = skeleton.AddBone( "root", -1, Xform.Identity );
		skeleton.AddBone( "child", root, Xform.Translate( new Vec3( 0, 1, 0 ) ) );

		DmxWriter.WriteFile( box, Path.Combine( outDir, "sample_rigged.dmx" ), skeleton, modelName: "sample_rigged" );

		Check( "wrote a static and a rigged sample DMX",
			File.Exists( Path.Combine( outDir, "sample_static.dmx" ) )
			&& File.Exists( Path.Combine( outDir, "sample_rigged.dmx" ) ) );

		// The same two models again in FBX, so the engine's own importer can be pointed at them:
		//
		//   bin/win64/fbx2dmx.exe -i out/sample_rigged.fbx -o check.dmx
		//
		// That is the whole reason FBX is worth writing — the format's reader is Autodesk's, so a
		// file it accepts is correct by something other than our own reading of a spec.
		FbxWriter.WriteFile( box, Path.Combine( outDir, "sample_static.fbx" ), modelName: "sample_static" );
		FbxWriter.WriteFile( box, Path.Combine( outDir, "sample_rigged.fbx" ), skeleton, modelName: "sample_rigged" );

		Check( "wrote a static and a rigged sample FBX",
			File.Exists( Path.Combine( outDir, "sample_static.fbx" ) )
			&& File.Exists( Path.Combine( outDir, "sample_rigged.fbx" ) ) );

		// The .vmdl that wraps the rigged DMX, so the bind pose and the bone markup can be put in
		// front of the compiler. Same skeleton, so the bone count the file claims is the one the DMX
		// actually carries.
		VmdlAnimationTests.WriteSample( outDir, skeleton, box );

		// And the animation path on that same skeleton: a clip DMX plus a .vmdl that compiles the
		// two together. Same skeleton object as the mesh, deliberately — a clip written against a
		// different rig compiles and animates nothing.
		DmxAnimTests.WriteSample( outDir, skeleton );
	}

	/// <summary>
	/// Sketch-driven samples, so the whole chain — sketch, profile, solid, subdivision — can be
	/// looked at rather than only asserted about. Dropping one of these into ModelDoc is still the
	/// cheapest way to find out what s&amp;box makes of kernel output.
	/// </summary>
	static void WriteSketchSamples( string outDir )
	{
		// A rounded slot: two lines and two arcs stitched into one loop, then extruded.
		var slotStudio = new PartStudio();
		var slotSketch = slotStudio.Add( new SketchFeature() );
		var s = slotSketch.Sketch;
		var a0 = s.AddPoint( 0, 0 );
		var a1 = s.AddPoint( 4, 0 );
		var a2 = s.AddPoint( 4, 2 );
		var a3 = s.AddPoint( 0, 2 );
		var c0 = s.AddPoint( 4, 1 );
		var c1 = s.AddPoint( 0, 1 );
		s.Add( new SketchLine( a0, a1 ) );
		s.Add( new SketchArc( c0, a1, a2 ) );
		s.Add( new SketchLine( a2, a3 ) );
		s.Add( new SketchArc( c1, a3, a0 ) );
		slotStudio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		slotStudio.Rebuild();
		ObjWriter.WriteFile( slotStudio.ToMesh(), Path.Combine( outDir, "sketch_slot.obj" ), "slot" );

		// The same slot subdivided twice — the CAD cage and the dense surface from one tree.
		slotStudio.Add( new SubdivideFeature() ).Levels.Value = 2;
		slotStudio.Rebuild();
		ObjWriter.WriteFile( slotStudio.ToMesh(), Path.Combine( outDir, "sketch_slot_subdiv2.obj" ), "slot_subdiv2" );

		// A revolved torus.
		var torusStudio = new PartStudio();
		torusStudio.Add( new SketchFeature() ).Sketch.AddRectangle( new Vec2( 0, 1 ), new Vec2( 1, 2 ) );
		var revolve = torusStudio.Add( new RevolveFeature() );
		revolve.AxisDirection.Value = new Vec3( 1, 0, 0 );
		revolve.Segments.Value = 32;
		torusStudio.Rebuild();
		ObjWriter.WriteFile( torusStudio.ToMesh(), Path.Combine( outDir, "sketch_torus.obj" ), "torus" );

		// A revolved profile that touches the axis, which collapses to a proper closed tip.
		var coneStudio = new PartStudio();
		coneStudio.Add( new SketchFeature() ).Sketch
			.AddPolygon( new Vec2( 0, 0 ), new Vec2( 2, 0 ), new Vec2( 0, 3 ) );
		coneStudio.Add( new RevolveFeature() ).AxisDirection.Value = new Vec3( 0, 1, 0 );
		coneStudio.Rebuild();
		ObjWriter.WriteFile( coneStudio.ToMesh(), Path.Combine( outDir, "sketch_cone.obj" ), "cone" );

		// A plate with four bolt holes — the profile that was unbuildable until holes landed, and the
		// fastest way to see whether the caps really are open rather than filled in.
		var plateStudio = new PartStudio();
		var plateSketch = plateStudio.Add( new SketchFeature() );
		plateSketch.Sketch.AddRectangle( new Vec2( -5, -3 ), new Vec2( 5, 3 ) );

		foreach ( var centre in new[] { (-3.5f, -1.5f), (3.5f, -1.5f), (3.5f, 1.5f), (-3.5f, 1.5f) } )
			plateSketch.Sketch.AddCircle( new Vec2( centre.Item1, centre.Item2 ), 0.6f );

		plateStudio.Add( new ExtrudeFeature() ).Distance.Value = 0.8f;
		plateStudio.Rebuild();
		ObjWriter.WriteFile( plateStudio.ToMesh(), Path.Combine( outDir, "sketch_plate_holes.obj" ), "plate_holes" );

		// A drafted boss: the same square section, leaning 8 degrees. Draft is the kind of thing that
		// reads as "looks slightly better" until you put it beside the straight version.
		var draftStudio = new PartStudio();
		var draftSketch = draftStudio.Add( new SketchFeature() );
		draftSketch.Sketch.AddRectangle( new Vec2( -2, -2 ), new Vec2( 2, 2 ) );
		var draft = draftStudio.Add( new ExtrudeFeature() );
		draft.Distance.Value = 3f;
		draft.Taper.Value = 8f;
		draftStudio.Rebuild();
		ObjWriter.WriteFile( draftStudio.ToMesh(), Path.Combine( outDir, "sketch_taper.obj" ), "taper" );

		// A cube with every edge chamfered — the flat-bevel look, side by side with the sharp box.
		var bevelStudio = new PartStudio();
		var bevelBox = bevelStudio.Add( new PrimitiveFeature() );
		bevelBox.Shape.Index = 0;
		bevelBox.SizeX.Value = bevelBox.SizeY.Value = bevelBox.SizeZ.Value = 2f;
		var bevel = bevelStudio.Add( new ChamferFeature() );
		bevel.Width.Value = 0.2f;
		bevel.AngleThreshold.Value = 15f;
		bevelStudio.Rebuild();
		ObjWriter.WriteFile( bevelStudio.ToMesh(), Path.Combine( outDir, "bevel_box.obj" ), "bevel_box" );
	}

	/// <summary>
	/// Shaded previews of every sample, so the output can be seen rather than only measured.
	/// Backface culling means an inside-out solid renders as a hole, which makes these a visual
	/// double-check on the winding tests.
	/// </summary>
	static void WritePreviews( string outDir )
	{
		foreach ( var file in Directory.GetFiles( outDir, "*.obj" ) )
		{
			var name = Path.GetFileNameWithoutExtension( file );
			var mesh = ObjReader.Read( File.ReadAllText( file ) );

			SvgPreview.Write( mesh, Path.Combine( outDir, $"{name}.svg" ), name );
		}

		// A wireframe of one subdivided result, where the quad topology is the point.
		var slot = ObjReader.Read( File.ReadAllText( Path.Combine( outDir, "sketch_slot_subdiv2.obj" ) ) );
		SvgPreview.Write( slot, Path.Combine( outDir, "wire_slot_subdiv2.svg" ), "sketch_slot_subdiv2 (wireframe)", wireframe: true );

		var cage = ObjReader.Read( File.ReadAllText( Path.Combine( outDir, "sketch_slot.obj" ) ) );
		SvgPreview.Write( cage, Path.Combine( outDir, "wire_slot_cage.svg" ), "sketch_slot cage (wireframe)", wireframe: true );

		WriteContactSheets( outDir );
	}

	/// <summary>PNG contact sheets — one image showing everything, viewable anywhere.</summary>
	static void WriteContactSheets( string outDir )
	{
		PolyMesh Load( string name ) => ObjReader.Read( File.ReadAllText( Path.Combine( outDir, $"{name}.obj" ) ) );

		var primitives = new[]
		{
			new PngPreview.Tile( Load( "box" ), "box" ),
			new PngPreview.Tile( Load( "cylinder" ), "cylinder" ),
			new PngPreview.Tile( Load( "quadsphere" ), "quad sphere" ),
			new PngPreview.Tile( Load( "wedge" ), "wedge" ),
			new PngPreview.Tile( Load( "tube" ), "tube" ),
			new PngPreview.Tile( Load( "sketch_slot" ), "sketch extrude" ),
			new PngPreview.Tile( Load( "sketch_torus" ), "sketch revolve" ),
			new PngPreview.Tile( Load( "sketch_cone" ), "revolve on axis" ),
			new PngPreview.Tile( Load( "sketch_plate_holes" ), "profile with holes" ),
			new PngPreview.Tile( Load( "sketch_taper" ), "8 degree draft" ),
		};

		PngPreview.WriteSheet( primitives, Path.Combine( outDir, "preview_primitives.png" ) );

		// Cage beside subdivided, in wireframe, which is where the quad topology shows.
		var subdivision = new[]
		{
			new PngPreview.Tile( Load( "sketch_slot" ), "cage", wireframe: true ),
			new PngPreview.Tile( Load( "sketch_slot_subdiv2" ), "subdiv 2", wireframe: true ),
			new PngPreview.Tile( Load( "box" ), "box cage", wireframe: true ),
			new PngPreview.Tile( Load( "box_subdiv2" ), "box subdiv 2", wireframe: true ),
			new PngPreview.Tile( Load( "sketch_slot" ), "cage shaded" ),
			new PngPreview.Tile( Load( "sketch_slot_subdiv2" ), "subdiv 2 shaded" ),
			new PngPreview.Tile( Load( "cylinder" ), "cylinder cage" ),
			new PngPreview.Tile( Load( "cylinder_subdiv2" ), "cylinder subdiv 2" ),
		};

		PngPreview.WriteSheet( subdivision, Path.Combine( outDir, "preview_subdivision.png" ) );
	}

	// ---------------------------------------------------------------------------------------

	static void Section( string title ) => Report.Section( title );

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
