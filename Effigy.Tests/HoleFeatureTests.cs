using System;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The hole tool, checked as a SHAPE rather than as a hole.
///
/// The cut itself is the engine's boolean, and it is not here. What is here is the negative this
/// hands it: the right diameter, pointing the right way, starting proud of the surface it enters,
/// and — for a counterbore or a countersink — wider at the mouth than at the bottom. Every one of
/// those can be wrong while the boolean succeeds, and a hole in the wrong place is a hole.
/// </summary>
public static class HoleFeatureTests
{
	public static void Run()
	{
		Section( "hole: the shape of the void" );
		TestASimpleHoleIsAShaftOfTheRightSize();
		TestItStartsProudOfTheSurfaceItEnters();
		TestItPointsWhereItWasAimed();
		TestABlindHoleStopsAndAThroughHoleDoesNot();
		TestACounterboreIsWiderAtTheMouth();
		TestACountersinkFollowsItsAngle();
		TestAHeadNarrowerThanTheShaftIsRefused();
		TestTheFeatureDrillsThroughTheProvider();
	}

	static (Vec3 Min, Vec3 Max) Bounds( PolyMesh mesh )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return (min, max);
	}

	/// <summary>Widest radius about the drilling axis among vertices near a given depth.</summary>
	static float RadiusAt( PolyMesh mesh, float z, float tolerance = 1e-3f )
	{
		var widest = 0f;

		foreach ( var p in mesh.Positions )
		{
			if ( MathF.Abs( p.z - z ) <= tolerance )
				widest = MathF.Max( widest, MathF.Sqrt( p.x * p.x + p.y * p.y ) );
		}

		return widest;
	}

	static void TestASimpleHoleIsAShaftOfTheRightSize()
	{
		// Drilled straight down from the origin, so the tool's own coordinates are the answer.
		var tool = HoleOperation.Build( HoleStyle.Simple, Vec3.Zero, new Vec3( 0, 0, -1 ),
			diameter: 0.5f, depth: 1f, headDiameter: 0f, headDepth: 0f, sinkAngleDegrees: 90f, through: 4f );

		var (min, max) = Bounds( tool );

		Check( "the shaft is the diameter it was given",
			MathF.Abs( (max.x - min.x) - 0.5f ) < 0.01f, $"{max.x - min.x:0.####} across" );
		Check( "and it is a closed solid the boolean can use", MeshValidator.Validate( tool ).IsClosed,
			MeshValidator.Validate( tool ).ToString() );
	}

	static void TestItStartsProudOfTheSurfaceItEnters()
	{
		// A tool whose end cap sits exactly ON the face it enters gives the boolean two coplanar
		// faces to resolve, and those are the ones that produce slivers rather than a clean mouth.
		var tool = HoleOperation.Build( HoleStyle.Simple, Vec3.Zero, new Vec3( 0, 0, -1 ),
			diameter: 0.5f, depth: 1f, headDiameter: 0f, headDepth: 0f, sinkAngleDegrees: 90f, through: 4f );

		var (min, max) = Bounds( tool );

		Check( "the mouth sits above the surface it drills into", max.z > 1e-4f, $"top at {max.z:0.#####}" );
		Check( "and the bottom is at the depth asked for",
			MathF.Abs( min.z + 1f ) < 0.05f, $"bottom at {min.z:0.####}, wanted -1" );
	}

	static void TestItPointsWhereItWasAimed()
	{
		// Drilling along the face's OUTWARD normal would put the tool entirely outside the body and
		// cut nothing — and a boolean that removes nothing succeeds, so it would look like the
		// feature quietly not working. The direction is the whole of that bug.
		var along = HoleOperation.Build( HoleStyle.Simple, Vec3.Zero, new Vec3( 1, 0, 0 ),
			diameter: 0.4f, depth: 2f, headDiameter: 0f, headDepth: 0f, sinkAngleDegrees: 90f, through: 4f );

		var (min, max) = Bounds( along );

		Check( "aimed along +X, the tool runs along +X", max.x > 1.5f && min.x < 0.1f,
			$"x from {min.x:0.###} to {max.x:0.###}" );
		Check( "and is only its own diameter across the other axes",
			max.z - min.z < 0.5f && max.y - min.y < 0.5f,
			$"{max.y - min.y:0.###} by {max.z - min.z:0.###}" );

		// Straight up is the case the alignment maths has to special-case: the cross product with +Z
		// is zero and gives no axis to turn about.
		var up = HoleOperation.Build( HoleStyle.Simple, Vec3.Zero, new Vec3( 0, 0, 1 ),
			diameter: 0.4f, depth: 2f, headDiameter: 0f, headDepth: 0f, sinkAngleDegrees: 90f, through: 4f );

		var upBounds = Bounds( up );

		Check( "and drilling straight back up +Z still produces a solid",
			upBounds.Max.z > 1.5f && MeshValidator.Validate( up ).IsClosed,
			$"z to {upBounds.Max.z:0.###}" );
	}

	static void TestABlindHoleStopsAndAThroughHoleDoesNot()
	{
		var blind = HoleOperation.Build( HoleStyle.Simple, Vec3.Zero, new Vec3( 0, 0, -1 ),
			diameter: 0.3f, depth: 0.5f, headDiameter: 0f, headDepth: 0f, sinkAngleDegrees: 90f, through: 6f );

		var through = HoleOperation.Build( HoleStyle.Simple, Vec3.Zero, new Vec3( 0, 0, -1 ),
			diameter: 0.3f, depth: 0f, headDiameter: 0f, headDepth: 0f, sinkAngleDegrees: 90f, through: 6f );

		var blindDepth = -Bounds( blind ).Min.z;
		var throughDepth = -Bounds( through ).Min.z;

		Check( "a blind hole stops where it was told", MathF.Abs( blindDepth - 0.5f ) < 0.05f,
			$"{blindDepth:0.###} deep" );
		Check( "a through hole runs past the body entirely", throughDepth > 6f,
			$"{throughDepth:0.###} deep against a body {6f} across" );
	}

	static void TestACounterboreIsWiderAtTheMouth()
	{
		var tool = HoleOperation.Build( HoleStyle.Counterbore, Vec3.Zero, new Vec3( 0, 0, -1 ),
			diameter: 0.4f, depth: 2f, headDiameter: 0.8f, headDepth: 0.5f, sinkAngleDegrees: 90f, through: 6f );

		var atHead = RadiusAt( tool, -0.5f, 0.02f );
		var atShaft = RadiusAt( tool, -1.5f, 0.6f );

		Check( "the head is the width it was given", MathF.Abs( atHead - 0.4f ) < 0.02f,
			$"radius {atHead:0.####}, wanted 0.4" );
		Check( "and the shaft below it is narrower", atShaft < 0.25f, $"radius {atShaft:0.####}" );
		Check( "the head stops at its own depth",
			RadiusAt( tool, -0.8f, 0.05f ) < 0.25f, "the head runs deeper than asked" );
	}

	static void TestACountersinkFollowsItsAngle()
	{
		// A countersink is specified by an included angle and a head size, never by a depth - the
		// depth follows, and working it out is the whole reason this is a feature rather than a
		// second circle in a sketch.
		var tool = HoleOperation.Build( HoleStyle.Countersink, Vec3.Zero, new Vec3( 0, 0, -1 ),
			diameter: 0.4f, depth: 2f, headDiameter: 1.0f, headDepth: 0f, sinkAngleDegrees: 90f, through: 6f );

		// 90 degrees included: the cone falls (1.0 - 0.4) / 2 = 0.3 over its own radius difference.
		var expected = (1.0f - 0.4f) * 0.5f / MathF.Tan( 45f * MathF.PI / 180f );
		var atCone = RadiusAt( tool, -expected, 0.02f );

		// MEASURED AT THE SURFACE, which is where a countersink's head diameter is specified and
		// where no vertex sits: the tool's rings are above the surface and at the bottom of the cone,
		// so the number that matters is the one between them. Reading the widest ring instead would
		// have called an over-wide mouth correct, which is exactly the bug this caught - the cone was
		// reaching its head diameter a third of a unit ABOVE the part.
		var (min, max) = Bounds( tool );
		var topRing = RadiusAt( tool, max.z, 0.02f );
		var atSurface = topRing + (atCone - topRing) * (max.z / (max.z + expected));

		Check( "the mouth is the head diameter where it meets the surface",
			MathF.Abs( atSurface - 0.5f ) < 0.03f,
			$"radius {atSurface:0.####} at the surface, wanted 0.5 (top ring {topRing:0.####} at z {max.z:0.###})" );
		Check( "and the cone reaches the shaft at the depth its angle implies",
			MathF.Abs( atCone - 0.2f ) < 0.05f, $"radius {atCone:0.####} at {expected:0.###} deep, wanted 0.2" );

		// A shallower included angle makes a deeper cone for the same head, which is the whole
		// relationship: get the tangent the wrong way up and every countersink comes out inverted.
		var steep = HoleOperation.Build( HoleStyle.Countersink, Vec3.Zero, new Vec3( 0, 0, -1 ),
			diameter: 0.4f, depth: 2f, headDiameter: 1.0f, headDepth: 0f, sinkAngleDegrees: 60f, through: 6f );

		var deeper = (1.0f - 0.4f) * 0.5f / MathF.Tan( 30f * MathF.PI / 180f );

		Check( "a narrower angle sinks deeper for the same head", deeper > expected,
			$"{deeper:0.###} against {expected:0.###}" );
		Check( "and the tool follows it", RadiusAt( steep, -deeper, 0.03f ) < 0.25f,
			"the 60 degree cone did not reach the shaft where its angle says" );
	}

	static void TestAHeadNarrowerThanTheShaftIsRefused()
	{
		// Not clamped: a counterbore whose head is narrower than its shaft is a number somebody typed
		// wrongly, and quietly widening it would drill a hole they did not ask for.
		var threw = false;

		try
		{
			HoleOperation.Build( HoleStyle.Counterbore, Vec3.Zero, new Vec3( 0, 0, -1 ),
				diameter: 0.8f, depth: 2f, headDiameter: 0.4f, headDepth: 0.5f, sinkAngleDegrees: 90f, through: 6f );
		}
		catch ( InvalidOperationException )
		{
			threw = true;
		}

		Check( "a head narrower than the shaft is refused", threw );
	}

	static void TestTheFeatureDrillsThroughTheProvider()
	{
		// End to end: the feature has to reach the boolean, with a tool that overlaps the body. A
		// tool aimed the wrong way still reaches the boolean and still succeeds, so the check is on
		// what it was HANDED, not on whether it was called.
		var previous = MeshBoolean.Provider;

		try
		{
			var stub = new RecordingBoolean();
			MeshBoolean.Provider = stub;

			var studio = new PartStudio();
			var box = studio.Add( new PrimitiveFeature() );
			box.SizeX.Value = 4f;
			box.SizeY.Value = 4f;
			box.SizeZ.Value = 4f;

			studio.Rebuild();

			var body = studio.Bodies[0];
			var top = -1;

			for ( var i = 0; i < body.Mesh.FaceCount; i++ )
			{
				if ( body.Mesh.FaceNormal( body.Mesh.Faces[i] ).Normal.z > 0.99f )
					top = i;
			}

			var hole = studio.Add( new HoleFeature() );
			hole.Diameter.Value = 0.5f;
			hole.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );

			studio.Rebuild();

			Check( "the feature built without complaint", hole.Error is null, hole.Error ?? "" );
			Check( "and reached the boolean", stub.Calls == 1, $"{stub.Calls} calls" );
			Check( "as a subtraction", stub.LastOp == BooleanOp.Subtract, $"{stub.LastOp}" );

			// The tool has to go DOWN into the block, not up off it.
			var (min, max) = Bounds( stub.LastTool );

			Check( "with a tool that goes into the material rather than off it",
				min.z < 1.9f, $"tool spans z {min.z:0.###} to {max.z:0.###}, block top is 2" );
			Check( "and overlaps the block it is cutting", max.z > 1.9f && min.z < 0f,
				$"tool spans z {min.z:0.###} to {max.z:0.###}" );
		}
		finally
		{
			MeshBoolean.Provider = previous;
		}
	}

	sealed class RecordingBoolean : IMeshBoolean
	{
		public int Calls;
		public BooleanOp LastOp;
		public PolyMesh LastTool;

		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			Calls++;
			LastOp = op;
			LastTool = tool;

			result = target.Clone();
			error = null;
			return true;
		}
	}
}
