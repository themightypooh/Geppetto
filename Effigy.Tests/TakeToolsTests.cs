using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Effigy;
using Effigy.Render;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The take tools: the clash check, the .riganim evaluator, two-bone IK, the model dump reader,
/// the eye-view render, and the audit that strings them together.
///
/// The evaluator's numbers are pinned to Marionette's own: the expected values below are what
/// s&box's Transform.Lerp and Easing produce, and a probe against s&box's assembly over every
/// clip in deep_sentance agreed with this evaluator to 2e-6 in position and 3e-7 in rotation.
/// </summary>
public static class TakeToolsTests
{
	public static void Run()
	{
		Section( "take tools: mesh clash and gap" );
		TestBoxesCross();
		TestSeparatedBoxesAreClearWithAGap();
		TestFaceContactIsNotACrossing();
		TestSelfCrossing();
		TestRefitMovesTheCheck();

		Section( "take tools: .riganim evaluator" );
		TestEasingCurves();
		TestTrackEvaluation();
		TestClipSampling();

		Section( "take tools: two-bone IK" );
		TestIkReachesATarget();
		TestIkClampsAndSoftens();
		TestIkCarriesChildren();

		Section( "take tools: model dump, eye render, audit" );
		TestDumpReadsSkeletonAndSkin();
		TestEyeRender();
		TestAuditFindsAFingerThroughAProp();
	}

	// --- clash -----------------------------------------------------------------------------------

	static PolyMesh Moved( PolyMesh mesh, Vec3 by )
	{
		var copy = mesh.Clone();
		for ( var i = 0; i < copy.Positions.Count; i++ )
			copy.Positions[i] += by;
		return copy;
	}

	static void TestBoxesCross()
	{
		var a = TriangleSet.From( Primitives.Box( 2, 2, 2 ) );
		var b = TriangleSet.From( Moved( Primitives.Box( 2, 2, 2 ), new Vec3( 1.5f, 0.3f, 0.2f ) ) );
		var report = MeshClash.Between( a, b );

		Check( "two overlapping boxes cross", report.Crossings > 0, report.ToString() );
		Check( "and the crossing names faces of both", report.Pairs.All( p => p.FaceA >= 0 && p.FaceA < 6 && p.FaceB >= 0 && p.FaceB < 6 ) );

		// a's corner (1,1,1) is inside b, 0.2 below b's top (z 1.2) - nearer than any other face of
		// b - and b's corner (0.5,-0.7,-0.8) is 0.2 above a's bottom. Depth is to the NEAREST
		// surface, which is the way out, not the way in.
		Check( "and the depth is how far a corner sits inside", MathF.Abs( report.Depth - 0.2f ) < 1e-3f, report.ToString() );
	}

	static void TestSeparatedBoxesAreClearWithAGap()
	{
		var a = TriangleSet.From( Primitives.Box( 2, 2, 2 ) );
		var b = TriangleSet.From( Moved( Primitives.Box( 2, 2, 2 ), new Vec3( 2.75f, 0, 0 ) ) );

		Check( "boxes apart do not cross", MeshClash.Between( a, b ).Clear );

		var gap = MeshClash.Nearest( a, b );
		Check( "and the gap between them is measured", gap.Found && MathF.Abs( gap.Distance - 0.75f ) < 1e-4f, gap.ToString() );
		Check( "between points on the facing sides", MathF.Abs( gap.PointA.x - 1f ) < 1e-4f && MathF.Abs( gap.PointB.x - 1.75f ) < 1e-4f, gap.ToString() );
		Check( "and nothing is found past the range asked", !MeshClash.Nearest( a, b, 0.5f ).Found );
	}

	static void TestFaceContactIsNotACrossing()
	{
		// A finger resting on a button: surfaces flat against each other are contact.
		var a = TriangleSet.From( Primitives.Box( 2, 2, 2 ) );
		var b = TriangleSet.From( Moved( Primitives.Box( 1, 1, 1 ), new Vec3( 0, 0, 1.5f ) ) );

		Check( "a box resting on another does not cross it", MeshClash.Between( a, b ).Clear, MeshClash.Between( a, b ).ToString() );
		Check( "and the gap is zero", MeshClash.Nearest( a, b ).Distance < 1e-4f );
	}

	static void TestSelfCrossing()
	{
		Check( "a box does not cross itself", MeshClash.Self( TriangleSet.From( Primitives.Box( 2, 2, 2 ) ) ).Clear );

		// Two quads in one mesh, the second stabbing through the first: a mesh through itself.
		var mesh = new PolyMesh();
		var q = new[]
		{
			new Vec3( -1, -1, 0 ), new Vec3( 1, -1, 0 ), new Vec3( 1, 1, 0 ), new Vec3( -1, 1, 0 ),
			new Vec3( 0, -0.5f, -1 ), new Vec3( 0, 0.5f, -1 ), new Vec3( 0, 0.5f, 1 ), new Vec3( 0, -0.5f, 1 ),
		};
		foreach ( var p in q ) mesh.AddVertex( p );
		mesh.AddFace( new[] { 0, 1, 2, 3 } );
		mesh.AddFace( new[] { 4, 5, 6, 7 } );

		var self = MeshClash.Self( TriangleSet.From( mesh ) );
		Check( "a sheet stabbed through by its own other sheet crosses itself", self.Crossings > 0, self.ToString() );
		Check( "and each crossing is between the two faces, not within one", self.Pairs.All( p => p.FaceA != p.FaceB ) );

		// A plane grid: neighbours share edges and corners, which is not a crossing.
		Check( "a flat grid's neighbours are not crossings", MeshClash.Self( TriangleSet.From( Primitives.Plane( 4, 4, 6, 6 ) ) ).Clear );
	}

	static void TestRefitMovesTheCheck()
	{
		var mover = Primitives.Box( 2, 2, 2 );
		var a = TriangleSet.From( mover );
		var b = TriangleSet.From( Moved( Primitives.Box( 2, 2, 2 ), new Vec3( 5, 0.3f, 0.2f ) ) );

		Check( "apart before the refit", MeshClash.Between( a, b ).Clear );

		a.Refit( mover.Positions.Select( p => p + new Vec3( 4, 0, 0 ) ).ToArray() );
		Check( "crossing after moving into it", MeshClash.Between( a, b ).Crossings > 0 );
		Check( "and the gap follows", MeshClash.Nearest( a, b ).Distance == 0f );

		// A grid folded back through itself: the refitted tree (split for the flat grid) and a tree
		// built fresh on the fold must both find exactly what a brute-force check does. They once
		// differed - the self walk skipped pairs across two leaves by triangle index, so which half
		// it dropped depended on the tree's shape.
		var grid = Primitives.Plane( 10, 10, 24, 24 );
		var folded = grid.Positions.Select( p => p.x <= 0f ? p : new Vec3( -p.x * 0.8f, p.y, 0.6f * MathF.Sin( p.x * 2f ) * (p.y * 0.3f) ) ).ToArray();
		var refit = TriangleSet.From( grid );
		refit.Refit( folded );
		var fresh = TriangleSet.From( folded, grid.Faces.SelectMany( f => new[] { f.Indices[0], f.Indices[1], f.Indices[2], f.Indices[0], f.Indices[2], f.Indices[3] } ).ToArray() );
		var viaRefit = MeshClash.Self( refit, int.MaxValue );
		var viaFresh = MeshClash.Self( fresh, int.MaxValue );
		var bruteForce = 0;
		for ( var s = 0; s < fresh.Count; s++ )
			for ( var t = s + 1; t < fresh.Count; t++ )
				if ( !fresh.Adjacent( s, t ) && MeshClash.TrianglesCross( fresh.A( s ), fresh.B( s ), fresh.C( s ), fresh.A( t ), fresh.B( t ), fresh.C( t ) ) )
					bruteForce++;

		Check( "a folded grid crosses itself", bruteForce > 0, $"{bruteForce}" );
		Check( "and the tree finds every crossing a brute-force check does", viaFresh.Crossings == bruteForce, $"{viaFresh.Crossings} vs {bruteForce}" );
		Check( "whether the tree was refitted or rebuilt", viaRefit.Crossings == bruteForce, $"{viaRefit.Crossings} vs {bruteForce}" );
	}

	// --- evaluator -----------------------------------------------------------------------------

	static void TestEasingCurves()
	{
		// s&box's Easing.QuadraticInOut / In / Out at t = 0.3, 0.5, 0.8.
		Check( "Smooth is quadratic in-out", Near( RigKey.Apply( RigEase.Smooth, 0.3f ), 0.18f ) && Near( RigKey.Apply( RigEase.Smooth, 0.5f ), 0.5f ) && Near( RigKey.Apply( RigEase.Smooth, 0.8f ), 0.92f ) );
		Check( "Ease In starts slow", Near( RigKey.Apply( RigEase.EaseIn, 0.3f ), 0.09f ) );
		Check( "Ease Out arrives slow", Near( RigKey.Apply( RigEase.EaseOut, 0.3f ), 0.51f ) );
		Check( "Linear is the identity", Near( RigKey.Apply( RigEase.Linear, 0.37f ), 0.37f ) );
		Check( "Stepped holds", RigKey.Apply( RigEase.Stepped, 0.99f ) == 0f );
		Check( "the modes keep Marionette's ordinals", (int)RigEase.Smooth == 0 && (int)RigEase.Linear == 1 && (int)RigEase.Stepped == 2 && (int)RigEase.EaseIn == 3 && (int)RigEase.EaseOut == 4 );
	}

	static string Key( int frame, Vec3 p, Quat q, string ease ) => string.Format( CultureInfo.InvariantCulture,
		"{{\"Frame\":{0},\"Local\":{{\"Position\":\"{1},{2},{3}\",\"Scale\":\"1,1,1\",\"Rotation\":\"{4},{5},{6},{7}\"}},\"Interpolation\":\"{8}\"}}",
		frame, p.x, p.y, p.z, q.x, q.y, q.z, q.w, ease );

	static string Clip( params (string Bone, string Target, string[] Keys)[] tracks )
	{
		var sb = new StringBuilder( "{\"SourceModel\":\"models/test.vmdl\",\"AnimationSpeed\":24,\"FrameCount\":40,\"BoneTracks\":[" );
		sb.Append( string.Join( ",", tracks.Select( t => $"{{\"BoneName\":\"{t.Bone}\",\"Target\":\"{t.Target}\",\"Keyframes\":[{string.Join( ",", t.Keys )}]}}" ) ) );
		return sb.Append( "]}" ).ToString();
	}

	static void TestTrackEvaluation()
	{
		var turn = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 2 );
		var json = Clip( ("arm", "Bone", new[]
		{
			Key( 0, Vec3.Zero, Quat.Identity, "Smooth" ),
			Key( 10, new Vec3( 10, 0, 0 ), turn, "Linear" ),
			Key( 20, new Vec3( 10, 10, 0 ), turn, "Stepped" ),
			Key( 30, new Vec3( 0, 10, 0 ), Quat.Identity, "Linear" ),
		}) );

		var clip = RigAnimFile.Read( json );
		var track = clip.Find( "arm" );

		Check( "a .riganim reads its tracks, rate and model", track is { Keys.Count: 4 } && clip.FrameRate == 24 && clip.SourceModel == "models/test.vmdl" );
		Check( "held before the first key", track.Evaluate( -5 ).Position.AlmostEquals( Vec3.Zero ) );
		Check( "held after the last", track.Evaluate( 99 ).Position.AlmostEquals( new Vec3( 0, 10, 0 ) ) );
		Check( "Smooth eases the segment leaving its key", track.Evaluate( 3 ).Position.AlmostEquals( new Vec3( 1.8f, 0, 0 ) ), track.Evaluate( 3 ).Position.ToString() );
		Check( "Linear is even", track.Evaluate( 15 ).Position.AlmostEquals( new Vec3( 10, 5, 0 ) ), track.Evaluate( 15 ).Position.ToString() );
		Check( "Stepped holds until the next key", track.Evaluate( 29.9f ).Position.AlmostEquals( new Vec3( 10, 10, 0 ) ) );
		Check( "and fractional frames are fine", track.Evaluate( 12.5f ).Position.AlmostEquals( new Vec3( 10, 2.5f, 0 ) ) );

		var half = track.Evaluate( 5 ).Rotation.Rotate( new Vec3( 1, 0, 0 ) );
		Check( "rotation is slerped", half.AlmostEquals( new Vec3( MathF.Sqrt( 0.5f ), MathF.Sqrt( 0.5f ), 0 ), 1e-4f ), half.ToString() );
	}

	static void TestClipSampling()
	{
		var skeleton = new Skeleton();
		var root = skeleton.AddBone( "root", -1, Xform.Translate( new Vec3( 0, 0, 5 ) ) );
		skeleton.AddBone( "arm", root, Xform.Translate( new Vec3( 3, 0, 0 ) ) );

		var json = Clip(
			("arm", "Bone", new[] { Key( 0, new Vec3( 3, 0, 0 ), Quat.Identity, "Linear" ), Key( 8, new Vec3( 3, 0, 4 ), Quat.Identity, "Linear" ) }),
			("door", "Part", new[] { Key( 0, Vec3.Zero, Quat.Identity, "Linear" ), Key( 20, Vec3.One, Quat.Identity, "Linear" ) }),
			("ghost", "Bone", new[] { Key( 0, Vec3.Zero, Quat.Identity, "Linear" ) }) );

		var clip = RigAnimFile.Read( json );
		var pose = clip.SampleLocal( skeleton, 4 );

		Check( "a bone with no track sits at its bind", pose[root].Origin.AlmostEquals( new Vec3( 0, 0, 5 ) ) );
		Check( "a keyed bone takes its track", pose[1].Origin.AlmostEquals( new Vec3( 3, 0, 2 ) ) );
		Check( "part tracks are kept apart from bone tracks", clip.BoneTracks.Count() == 2 && clip.Tracks.Single( t => t.IsPart ).Name == "door" );

		var world = RigAnimFile.ModelSpace( skeleton, pose );
		Check( "model space composes parents", world[1].Origin.AlmostEquals( new Vec3( 3, 0, 7 ) ) );
		Check( "and local space undoes it", RigAnimFile.LocalSpace( skeleton, world )[1].Origin.AlmostEquals( new Vec3( 3, 0, 2 ) ) );

		// Last key is 20 (the part), the clip is 40 long: export bakes 0..20, like Marionette.
		var baked = clip.ToClip( skeleton, "wave" );
		Check( "baking spans every frame up to the last key", baked.FrameCount == 21 && baked.FrameRate == 24 && baked.Validate( skeleton ) is null, $"{baked.FrameCount} frames" );
	}

	// --- IK --------------------------------------------------------------------------------------

	static Skeleton Arm()
	{
		var s = new Skeleton();
		var shoulder = s.AddBone( "shoulder", -1, Xform.Identity );
		var elbow = s.AddBone( "elbow", shoulder, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		var wrist = s.AddBone( "wrist", elbow, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
		s.AddBone( "finger", wrist, Xform.Translate( new Vec3( 2, 0, 0 ) ) );
		return s;
	}

	static void TestIkReachesATarget()
	{
		var s = Arm();
		var world = SoftSolver.BindPose( s );
		var target = new Vec3( 12, 6, 0 );
		var solved = TwoBoneIk.Apply( s, world, 2, target, new Vec3( 0, 0, 1 ) );

		Check( "IK puts the wrist on a reachable target", solved is not null && (solved[2].Origin - target).Length < 1e-3f, solved?[2].Origin.ToString() );
		Check( "without stretching the upper bone", MathF.Abs( (solved[1].Origin - solved[0].Origin).Length - 10f ) < 1e-3f );
		Check( "or the lower one", MathF.Abs( (solved[2].Origin - solved[1].Origin).Length - 10f ) < 1e-3f );
		Check( "and the elbow bends towards the pole", solved[1].Origin.z > 1f, solved[1].Origin.ToString() );

		var other = TwoBoneIk.Apply( s, world, 2, target, new Vec3( 0, 0, -1 ) );
		Check( "and away from it when the pole flips", other[1].Origin.z < -1f, other[1].Origin.ToString() );
		Check( "a chain that is not three deep is refused", TwoBoneIk.Apply( s, world, 1, target, new Vec3( 0, 0, 1 ) ) is null );
	}

	static void TestIkClampsAndSoftens()
	{
		var hard = TwoBoneIk.TrySolve( Vec3.Zero, new Vec3( 10, 0, 0 ), new Vec3( 20, 0, 0 ), new Vec3( 0, 50, 0 ), new Vec3( 0, 0, 1 ), 0f, out var h );
		Check( "an out-of-reach target straightens the arm towards it", hard && MathF.Abs( h.End.Length - 20f ) < 0.01f && h.End.y > 19.9f && !h.Reached, h.End.ToString() );

		// Soft: the reach eases towards full extension instead of hitting it.
		var near = new Vec3( 0, 19.5f, 0 );
		TwoBoneIk.TrySolve( Vec3.Zero, new Vec3( 10, 0, 0 ), new Vec3( 20, 0, 0 ), near, new Vec3( 0, 0, 1 ), 2f, out var soft );
		TwoBoneIk.TrySolve( Vec3.Zero, new Vec3( 10, 0, 0 ), new Vec3( 20, 0, 0 ), near, new Vec3( 0, 0, 1 ), 0f, out var stiff );
		Check( "softness holds a near-full reach short of the target", soft.End.Length < stiff.End.Length - 0.05f && soft.End.Length > 18f, $"{soft.End.Length} vs {stiff.End.Length}" );

		var last = 0f;
		var smooth = true;
		for ( var d = 15f; d < 30f; d += 0.25f )
		{
			TwoBoneIk.TrySolve( Vec3.Zero, new Vec3( 10, 0, 0 ), new Vec3( 20, 0, 0 ), new Vec3( 0, d, 0 ), new Vec3( 0, 0, 1 ), 2f, out var r );
			if ( r.End.Length < last - 1e-4f || r.End.Length > 20f ) smooth = false;
			last = r.End.Length;
		}
		Check( "and the soft reach never runs backwards or past full length", smooth );
	}

	static void TestIkCarriesChildren()
	{
		var s = Arm();
		var world = SoftSolver.BindPose( s );
		var solved = TwoBoneIk.Apply( s, world, 2, new Vec3( 12, 6, 0 ), new Vec3( 0, 0, 1 ) );

		Check( "a bone below the end moves with the wrist", (solved[3].Origin - solved[2].Origin).Length is var d && MathF.Abs( d - 2f ) < 1e-3f, solved[3].Origin.ToString() );
		Check( "and the wrist keeps its rotation", solved[2].X.AlmostEquals( world[2].X, 1e-4f ) );
	}

	// --- dump, render, audit -------------------------------------------------------------------

	static string F( float v ) => v.ToString( "R", CultureInfo.InvariantCulture );

	/// <summary>A two-bone "finger": a 1x1x6 bar along +x, the far half weighted to the tip bone.
	/// The tip bone is listed BEFORE its parent, as engines sometimes do.</summary>
	static string FingerDump( string props = "" )
	{
		var bar = Primitives.Box( 6, 1, 1 );
		var positions = bar.Positions.Select( p => p + new Vec3( 3, 0, 0 ) ).ToList();
		var tris = new List<int>();
		foreach ( var f in bar.Faces )
			for ( var k = 1; k + 1 < f.Indices.Length; k++ )
				tris.AddRange( new[] { f.Indices[0], f.Indices[k], f.Indices[k + 1] } );

		var idx = positions.SelectMany( p => p.x > 3f ? new[] { 0, 0, 0, 0 } : new[] { 1, 0, 0, 0 } );
		var wts = positions.SelectMany( p => new[] { 1f, 0, 0, 0 } );

		return "{\"Model\":\"models/finger.vmdl\",\"Bones\":["
			+ "{\"Name\":\"tip\",\"Parent\":\"base\",\"Bind\":[3,0,0,0,0,0,1]},"
			+ "{\"Name\":\"base\",\"Parent\":null,\"Bind\":[0,0,0,0,0,0,1]}],"
			+ "\"Poses\":{\"1\":{\"tip\":[3,0,0,0,0,0.7071068,0.7071068]}},"
			+ "\"Meshes\":[{\"Name\":\"finger\",\"Material\":\"materials/skin.vmat\","
			+ "\"Positions\":[" + string.Join( ",", positions.SelectMany( p => new[] { F( p.x ), F( p.y ), F( p.z ) } ) ) + "],"
			+ "\"Triangles\":[" + string.Join( ",", tris ) + "],"
			+ "\"BoneIndices\":[" + string.Join( ",", idx ) + "],"
			+ "\"BoneWeights\":[" + string.Join( ",", wts.Select( F ) ) + "]}]"
			+ props + "}";
	}

	static void TestDumpReadsSkeletonAndSkin()
	{
		var dump = ModelDump.Read( FingerDump() );

		Check( "a dump puts parents before children", dump.Skeleton.Bones[0].Name == "base" && dump.Skeleton.Bones[1].Parent == 0 );
		Check( "and keeps bind poses in model space", dump.BindWorld[1].Origin.AlmostEquals( new Vec3( 3, 0, 0 ) ) && dump.Skeleton.Bones[1].Local.Origin.AlmostEquals( new Vec3( 3, 0, 0 ) ) );

		var part = dump.Meshes.Single();
		Check( "a mesh comes with its triangles and material", part.TriangleCount == 12 && part.Material == "materials/skin.vmat" );
		Check( "and weights mapped onto the reordered skeleton", part.Weights.Vertices.Where( ( w, v ) => part.Positions[v].x > 3f ).All( w => w.Single().Bone == 1 ) );

		var atBind = dump.Deform( part, dump.BindWorld );
		Check( "skinning at the bind pose changes nothing", atBind.Zip( part.Positions ).All( p => p.First.AlmostEquals( p.Second ) ) );

		var bent = dump.Deform( part, dump.Poses["1"] );
		var tipEnd = bent.Where( ( p, v ) => part.Positions[v].x > 5.9f ).ToList();
		Check( "a sampled pose bends the tip ninety degrees", tipEnd.All( p => p.y > 2.4f && MathF.Abs( p.x - 3f ) < 0.6f ), string.Join( " ", tipEnd ) );
	}

	static void TestEyeRender()
	{
		var box = Moved( Primitives.Box( 2, 2, 2 ), new Vec3( 10, 0, 0 ) );
		var tris = box.Faces.SelectMany( f => new[] { f.Indices[0], f.Indices[1], f.Indices[2], f.Indices[0], f.Indices[2], f.Indices[3] } ).ToList();
		var camera = EyeCamera.At( Xform.Identity, 60f );
		var image = EyeRender.Render( new[] { new EyeItem( box.Positions, tris, 0xff0000 ) }, camera, 64, 64 );

		var centre = image.Pixels[32 * 64 + 32];
		var corner = image.Pixels[0];
		Check( "a box ahead of the camera fills the middle of the frame", ((centre >> 16) & 0xFF) > 60 && (centre & 0xFF) < 40, centre.ToString( "x6" ) );
		Check( "and not the corners", corner != centre );

		// Perspective: the box at twice the distance covers about a quarter of the pixels.
		int Lit( Raster r ) => r.Pixels.Count( p => ((p >> 16) & 0xFF) > 40 && (p & 0xFF) < 40 );
		var far = Moved( box, new Vec3( 10, 0, 0 ) );
		var farImage = EyeRender.Render( new[] { new EyeItem( far.Positions, tris, 0xff0000 ) }, camera, 64, 64 );
		var ratio = Lit( image ) / (float)Math.Max( 1, Lit( farImage ) );
		Check( "and further away it is smaller, as perspective should be", ratio > 3f && ratio < 5.5f, $"{Lit( image )} vs {Lit( farImage )} pixels" );

		// A camera inside a wall does not lose the wall: it is cut at the near plane.
		var wall = new[] { new Vec3( -5, -5, -5 ), new Vec3( 20, -5, -5 ), new Vec3( 20, -5, 5 ), new Vec3( -5, -5, 5 ) };
		var wallImage = EyeRender.Render( new[] { new EyeItem( wall, new[] { 0, 1, 2, 0, 2, 3 }, 0x00ff00 ) }, camera, 64, 64 );
		Check( "geometry crossing the near plane is clipped, not dropped", wallImage.Pixels.Count( p => ((p >> 8) & 0xFF) > 30 ) > 100 );

		var sheet = EyeRender.Sheet( new[] { image, farImage, wallImage }, 2, new[] { "near", "far", "wall" } );
		Check( "a contact sheet lays tiles out in a grid", sheet.Width == 64 * 2 + 2 && sheet.Height == 64 * 2 + 2 );
	}

	static void TestAuditFindsAFingerThroughAProp()
	{
		// A block across the finger's sweep: x 1.5..4.5, y 2.5..4.5, z -2..2. The finger starts
		// straight along +x, 2 clear of it, and the clip bends the tip up into it.
		var plate = Primitives.Box( 3, 2, 4 );
		var plateTris = plate.Faces.SelectMany( f => new[] { f.Indices[0], f.Indices[1], f.Indices[2], f.Indices[0], f.Indices[2], f.Indices[3] } );
		var props = ",\"Props\":[{\"Name\":\"plate\",\"Model\":\"models/plate.vmdl\",\"Transform\":[3,3.5,0,0,0,0,1],"
			+ "\"Meshes\":[{\"Name\":\"body\",\"Positions\":[" + string.Join( ",", plate.Positions.SelectMany( p => new[] { F( p.x ), F( p.y ), F( p.z ) } ) ) + "],"
			+ "\"Triangles\":[" + string.Join( ",", plateTris ) + "]}]}]";

		var dump = ModelDump.Read( FingerDump( props ) );
		var turn = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 2 );
		var clip = RigAnimFile.Read( Clip( ("tip", "Bone", new[] { Key( 0, new Vec3( 3, 0, 0 ), Quat.Identity, "Linear" ), Key( 8, new Vec3( 3, 0, 0 ), turn, "Linear" ) }) ) );

		var result = new TakeAudit( dump, clip ).Run( new TakeAudit.Options { Substeps = 2 } );
		var hit = result.Checks.Single( c => c.Name == "finger x plate/body" );

		Check( "the audit finds the finger going through the plate", !hit.Clear && hit.WorstDepth > 0.1f, result.ToString() );
		Check( "not at the start, where it is clear", hit.FramesOver.Min() > 0f, string.Join( ",", hit.FramesOver ) );
		Check( "and it measures how close the finger came before", hit.NearestGap == 0f );
		Check( "the take is not clear", !result.Clear );

		var still = new TakeAudit( dump, clip ).Still( 0, EyeCamera.LookAt( new Vec3( 3, -12, 6 ), new Vec3( 3, 0, 0 ), 70f ), 96, 64 );
		Check( "and a still of it renders the finger and the plate", still.Pixels.Distinct().Count() > 4 );

		// Straight clip, no bend: clear, and the gap is the distance from the finger to the plate.
		var still2 = RigAnimFile.Read( Clip( ("tip", "Bone", new[] { Key( 0, new Vec3( 3, 0, 0 ), Quat.Identity, "Linear" ) }) ) );
		var clear = new TakeAudit( dump, still2 ).Run();
		var gap = clear.Checks.Single( c => c.Name == "finger x plate/body" );
		Check( "a take that keeps clear is clear", clear.Clear, clear.ToString() );
		Check( "and reports its nearest approach", MathF.Abs( gap.NearestGap - 2f ) < 1e-3f, gap.NearestGap.ToString() );
	}

	static bool Near( float a, float b ) => MathF.Abs( a - b ) < 1e-5f;
}
