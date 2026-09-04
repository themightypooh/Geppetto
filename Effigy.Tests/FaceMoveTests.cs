using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Moving a face of a part that already exists — push and pull.
///
/// WHY THIS FILE IS ALL NUMBERS AND NO RENDERS. Rule 1 of the work order is that a mesh can be
/// closed, manifold, Euler-correct and visibly wrong, and every hard bug in this repo's history
/// passed most of its checks. Face moving is the rare operation where that does not have to be a
/// worry, because the invariants are exact:
///
/// - Offsetting one face of a prismatic body by `d` changes enclosed volume by EXACTLY `area * d`.
/// - Translating a facing pair of EQUAL AREA changes it by EXACTLY zero — the two swept volumes
///   cancel.
///
/// So a wall that slides and a wall that quietly thickens are one assertion apart, and neither
/// needs looking at. That second identity is the whole reason Translate and Offset are separate
/// modes, and it is what catches the mistake of implementing one and shipping it as both.
/// </summary>
public static class FaceMoveTests
{
	public static void Run()
	{
		Report.Section( "face move: offset changes volume by exactly area x distance" );
		TestOffsetOneFace();
		TestOffsetInwards();

		Report.Section( "face move: translating a facing pair slides the wall, exactly" );
		TestTranslateFacingPair();
		TestOffsetFacingPairThickens();

		Report.Section( "face move: the neighbours keep their angles" );
		TestSlantedNeighbourKeepsItsAngle();

		Report.Section( "face move: what it refuses" );
		TestRefusesFold();
		TestRefusesAnOverConstrainedCorner();
		TestRefusesCurvedFace();
		TestRefusesCoplanarContradiction();
		TestRefusesNothingPicked();

		Report.Section( "face move: as a feature in the tree" );
		TestFeatureMovesAPrimitive();
		TestFeatureSaysSoWhenTheFaceIsGone();
		TestOffsetAndTranslateAgreeOnASingleFace();
	}

	// --- the exact identities -------------------------------------------------------------------

	static void TestOffsetOneFace()
	{
		var mesh = Primitives.Box( 2f, 3f, 1f );
		var before = mesh.SignedVolume();
		var top = FaceFacing( mesh, new Vec3( 0, 0, 1 ) );
		var area = mesh.FaceArea( mesh.Faces[top] );

		var moved = FaceMove.Offset( mesh, new[] { top }, 0.5f );

		Report.Check( "the result is a valid closed mesh", Closed( moved ), Describe( moved ) );

		Report.Check( "volume grew by exactly area x distance",
			Near( moved.SignedVolume() - before, area * 0.5f ),
			$"{moved.SignedVolume() - before:0.######}, wanted {area * 0.5f:0.######}" );

		Report.Check( "and nothing but the top moved",
			MovedVertices( mesh, moved ) == 4, $"{MovedVertices( mesh, moved )} vertices moved" );
	}

	static void TestOffsetInwards()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		var before = mesh.SignedVolume();
		var top = FaceFacing( mesh, new Vec3( 0, 0, 1 ) );

		// NEGATIVE IS AN ORDINARY DISTANCE, not a second operation: pushing a face into the solid is
		// the same solve with the sign flipped, and the volume identity holds either way.
		var moved = FaceMove.Offset( mesh, new[] { top }, -0.25f );

		Report.Check( "pushing in is valid too", Closed( moved ), Describe( moved ) );

		Report.Check( "and takes exactly area x distance away",
			Near( moved.SignedVolume() - before, -0.25f ),
			$"{moved.SignedVolume() - before:0.######}, wanted -0.25" );
	}

	/// <summary>
	/// THE ASSERTION THIS WHOLE MODE EXISTS FOR. po's wall: pick both faces, give a direction, and
	/// the wall moves without changing thickness. Equal-area facing pair, so the material added on
	/// one side is exactly the material taken from the other and the volume is untouched.
	/// </summary>
	static void TestTranslateFacingPair()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		var before = mesh.SignedVolume();
		var top = FaceFacing( mesh, new Vec3( 0, 0, 1 ) );
		var bottom = FaceFacing( mesh, new Vec3( 0, 0, -1 ) );

		var moved = FaceMove.Translate( mesh, new[] { top, bottom }, new Vec3( 0, 0, 0.3f ) );

		Report.Check( "the slid wall is still a valid closed solid", Closed( moved ), Describe( moved ) );

		Report.Check( "and its volume is unchanged to the last digit",
			Near( moved.SignedVolume(), before ),
			$"{moved.SignedVolume():0.######}, wanted {before:0.######}" );

		// The thickness itself, measured rather than inferred from the volume: a pair that swelled
		// on one side and shrank on the other by the same amount would pass the volume check.
		Report.Check( "the wall is exactly as thick as it was",
			Near( Extent( moved, 2 ), Extent( mesh, 2 ) ),
			$"{Extent( moved, 2 ):0.######} thick, was {Extent( mesh, 2 ):0.######}" );

		Report.Check( "and it has actually moved",
			Near( Centre( moved, 2 ) - Centre( mesh, 2 ), 0.3f ),
			$"centre moved {Centre( moved, 2 ) - Centre( mesh, 2 ):0.######}, wanted 0.3" );
	}

	/// <summary>The same pair in the other mode, which is the distinction a single implementation
	/// pretending to be both would get wrong: offset moves them APART.</summary>
	static void TestOffsetFacingPairThickens()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		var top = FaceFacing( mesh, new Vec3( 0, 0, 1 ) );
		var bottom = FaceFacing( mesh, new Vec3( 0, 0, -1 ) );

		var moved = FaceMove.Offset( mesh, new[] { top, bottom }, 0.2f );

		Report.Check( "offsetting a facing pair thickens the wall by twice the distance",
			Near( Extent( moved, 2 ), 1.4f ), $"{Extent( moved, 2 ):0.######}, wanted 1.4" );

		Report.Check( "and leaves it where it was",
			Near( Centre( moved, 2 ), Centre( mesh, 2 ) ),
			$"{Centre( moved, 2 ):0.######}, wanted {Centre( mesh, 2 ):0.######}" );

		Report.Check( "volume follows both faces",
			Near( moved.SignedVolume(), 1.4f ), $"{moved.SignedVolume():0.######}, wanted 1.4" );
	}

	/// <summary>
	/// The pay-off of solving planes rather than sliding vertices along normals: a slanted neighbour
	/// keeps its angle for free, because its plane is one of the constraints.
	///
	/// The obvious implementation — move each vertex of the face along the FACE's normal — stretches
	/// the slant to a different angle, and looks perfectly fine while doing it. This is the check
	/// that tells the two apart, and the numbers are exact: the drafted wall's normal must not move
	/// at all, and the top must end up exactly where it was asked to be.
	/// </summary>
	static void TestSlantedNeighbourKeepsItsAngle()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		var wall = FaceFacing( mesh, new Vec3( 1, 0, 0 ) );

		// A drafted wall rather than a chamfer, because a chamfered CORNER is over-constrained and is
		// refused — see TestRefusesAnOverConstrainedCorner just below, which is the other half of this
		// pair. Drafting a single wall leaves every top vertex on three planes, which is the ordinary
		// case and the one push-pull lives in.
		var drafted = DraftOperation.Draft( mesh, new[] { wall }, Vec3.Zero, new Vec3( 0, 0, 1 ), 10f );

		var slant = drafted.FaceNormal( drafted.Faces[wall] );

		Report.Check( "the fixture really is slanted",
			MathF.Abs( slant.z ) > 0.01f && MathF.Abs( slant.x ) > 0.9f, Show( slant ) );

		var top = FaceFacing( drafted, new Vec3( 0, 0, 1 ) );
		var height = drafted.FaceCentroid( drafted.Faces[top] ).z;

		var moved = FaceMove.Offset( drafted, new[] { top }, 0.3f );

		Report.Check( "the part is still valid after moving a face beside a slanted one",
			Closed( moved ), Describe( moved ) );

		Report.Check( "the slanted wall kept its angle exactly",
			Vec3.Dot( slant, moved.FaceNormal( moved.Faces[wall] ) ) > 0.999999f,
			$"normal went from {Show( slant )} to {Show( moved.FaceNormal( moved.Faces[wall] ) )}" );

		Report.Check( "and the moved face went exactly as far as it was asked to",
			Near( moved.FaceCentroid( moved.Faces[top] ).z - height, 0.3f ),
			$"{moved.FaceCentroid( moved.Faces[top] ).z - height:0.######}, wanted 0.3" );

		// The top of a drafted box CHANGES SIZE as it rises — this fixture leans its wall outward, so
		// the top gets bigger — which is exactly why the flat case's `volume == area x distance`
		// identity is deliberately not asserted here. It would be false, and it would be false for a
		// good reason. Stated out loud so the next person does not add it and then delete this test
		// when it fails.
		Report.Check( "the top follows the slant rather than sliding straight up",
			moved.FaceArea( moved.Faces[top] ) > drafted.FaceArea( drafted.Faces[top] ) + 1e-4f,
			$"{moved.FaceArea( moved.Faces[top] ):0.####} against {drafted.FaceArea( drafted.Faces[top] ):0.####}" );
	}

	// --- refusals ---------------------------------------------------------------------------------

	static void TestRefusesFold()
	{
		var mesh = Primitives.Box( 1f, 1f, 1f );
		var top = FaceFacing( mesh, new Vec3( 0, 0, 1 ) );

		Report.Check( "pushing a face straight through the far side is refused",
			Refuses( () => FaceMove.Offset( mesh, new[] { top }, -2f ), out var why ), why );

		Report.Check( "and the message says what to do about it",
			why.Contains( "shorter" ), why );
	}

	/// <summary>
	/// A CHAMFERED CORNER IS OVER-CONSTRAINED, AND THAT IS A LIMIT RATHER THAN A BUG.
	///
	/// Chamfer a box and each top corner vertex sits on four planes at once: the top, two edge
	/// chamfers, and the little corner triangle. Move the top and the point where the first three
	/// meet is perfectly well defined — and it is no longer on the fourth. There is no position for
	/// that vertex that satisfies everything, because the honest answer is not a position at all: the
	/// corner triangle has to GROW, which means the one vertex becomes three. Splitting vertices is a
	/// different and much larger algorithm than moving them.
	///
	/// So it refuses, and this test pins that refusal so the limit is a decision on the record rather
	/// than something the next person rediscovers with a broken part. If vertex splitting is ever
	/// written, this is the test that should start failing.
	/// </summary>
	static void TestRefusesAnOverConstrainedCorner()
	{
		var chamfered = EdgeBlend.Chamfer( Primitives.Box( 1f, 1f, 1f ), 0.15f, 30f );
		var top = FaceFacing( chamfered, new Vec3( 0, 0, 1 ) );

		Report.Check( "moving the top of a chamfered box is refused rather than fudged",
			Refuses( () => FaceMove.Offset( chamfered, new[] { top }, 0.3f ), out var why ), why );

		Report.Check( "and it says the corner is the problem",
			why.Contains( "corner" ), why );
	}

	static void TestRefusesCurvedFace()
	{
		// A quad with one corner lifted off the plane of the other three. There is no single normal
		// here, so "move it along its normal" is not an operation rather than an inaccurate one.
		var mesh = Primitives.Box( 1f, 1f, 1f );
		var top = FaceFacing( mesh, new Vec3( 0, 0, 1 ) );
		var corner = mesh.Faces[top].Indices[0];

		mesh.Positions[corner] += new Vec3( 0, 0, 0.3f );

		Report.Check( "a face that is not flat is refused",
			Refuses( () => FaceMove.Offset( mesh, new[] { top }, 0.2f ), out var why ), why );

		Report.Check( "and says so in those words", why.Contains( "not flat" ), why );
	}

	static void TestRefusesCoplanarContradiction()
	{
		// Split the top of a box in two, then move only half of it. The two halves share a plane and
		// a row of vertices, so those vertices are asked to be in two places at once. Refused where
		// the two faces can be named, rather than fitted to something plausible.
		var mesh = Primitives.Box( 1f, 1f, 1f );
		var top = FaceFacing( mesh, new Vec3( 0, 0, 1 ) );
		var half = SplitTopInHalf( mesh, top );

		Report.Check( "moving half of a flat surface is refused",
			Refuses( () => FaceMove.Offset( mesh, new[] { half }, 0.2f ), out var why ), why );

		Report.Check( "and says to take the whole surface",
			why.Contains( "whole flat surface" ), why );
	}

	static void TestRefusesNothingPicked()
	{
		var mesh = Primitives.Box();

		Report.Check( "no faces at all is refused rather than silently doing nothing",
			Refuses( () => FaceMove.Offset( mesh, Array.Empty<int>(), 0.2f ), out var why ), why );
	}

	// --- as a feature -----------------------------------------------------------------------------

	static void TestFeatureMovesAPrimitive()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );
		studio.Rebuild();

		var body = studio.Bodies.Single();
		var before = body.Mesh.SignedVolume();
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		var area = body.Mesh.FaceArea( body.Mesh.Faces[top] );

		var move = studio.Add( new MoveFaceFeature() );
		move.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );
		move.Distance.Value = 0.4f;

		var report = studio.Rebuild();

		Report.Check( "a face of a primitive can be pulled with no sketch anywhere in the document",
			!report.HasErrors, report.ToString() );

		var after = studio.Bodies.Single().Mesh;

		Report.Check( "and the solid grew by exactly area x distance",
			Near( after.SignedVolume() - before, area * 0.4f ),
			$"{after.SignedVolume() - before:0.######}, wanted {area * 0.4f:0.######}" );

		Report.Check( "the part is still one closed body", Closed( after ), Describe( after ) );

		// The parametric half: change the number, rebuild, and the part follows. This is what makes
		// it a feature rather than a bake.
		move.Distance.Value = 0.8f;
		studio.MarkDirty( move );
		studio.Rebuild();

		Report.Check( "editing the distance afterwards moves the face again",
			Near( studio.Bodies.Single().Mesh.SignedVolume() - before, area * 0.8f ),
			$"{studio.Bodies.Single().Mesh.SignedVolume() - before:0.######}" );
	}

	/// <summary>
	/// The bet direct editing makes: a MoveFace holds a FaceRef, and an upstream edit that destroys
	/// that face has to make this feature fail OUT LOUD. Anything that quietly reattaches to whatever
	/// looks similar is worse than an error, because the part then rebuilds wrong and says nothing.
	/// </summary>
	static void TestFeatureSaysSoWhenTheFaceIsGone()
	{
		var studio = new PartStudio();
		var primitive = studio.Add( new PrimitiveFeature() );
		studio.Rebuild();

		var body = studio.Bodies.Single();
		var top = FaceFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var move = studio.Add( new MoveFaceFeature() );
		move.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );

		studio.Rebuild();

		Report.Check( "the move builds while its face is there", move.Error is null, move.Error ?? "" );

		// Take the part out from under it.
		studio.Remove( primitive );
		studio.Rebuild();

		Report.Check( "and fails loudly once the face it named is gone",
			move.Error is not null, "it built anyway" );

		Report.Check( "with a message that says the faces are the problem",
			move.Error is not null && move.Error.Contains( "faces" ), move.Error ?? "" );
	}

	/// <summary>
	/// On ONE face the two modes are the same move, provided the translation is along that face's
	/// normal — which is the sanity check that they really are one solve with a different `t`.
	/// </summary>
	static void TestOffsetAndTranslateAgreeOnASingleFace()
	{
		var mesh = Primitives.Box( 1f, 2f, 1f );
		var top = FaceFacing( mesh, new Vec3( 0, 0, 1 ) );

		var offset = FaceMove.Offset( mesh, new[] { top }, 0.35f );
		var translated = FaceMove.Translate( mesh, new[] { top }, new Vec3( 0, 0, 0.35f ) );

		var worst = 0f;

		for ( var i = 0; i < offset.VertexCount; i++ )
			worst = MathF.Max( worst, (offset.Positions[i] - translated.Positions[i]).Length );

		Report.Check( "offset and translate agree along a face's own normal", worst < 1e-5f,
			$"worst vertex differs by {worst:0.#######}" );
	}

	// --- fixtures ---------------------------------------------------------------------------------

	static bool Near( float a, float b ) => MathF.Abs( a - b ) < 1e-4f;

	static bool Closed( PolyMesh mesh )
	{
		var validation = MeshValidator.Validate( mesh );

		return validation.IsValid && validation.IsClosed;
	}

	static string Describe( PolyMesh mesh ) => MeshValidator.Validate( mesh ).ToString();

	static string Show( Vec3 v ) => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})";

	static bool Refuses( Action action, out string message )
	{
		try
		{
			action();
			message = "it did it anyway";
			return false;
		}
		catch ( InvalidOperationException e )
		{
			message = e.Message;
			return true;
		}
	}

	static int FaceFacing( PolyMesh mesh, Vec3 direction )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[i] ), direction.Normal ) > 0.999f )
				return i;
		}

		throw new InvalidOperationException( $"no face pointing {Show( direction )}" );
	}

	/// <summary>The one face of a chamfered box that points neither along an axis nor against
	/// one.</summary>
	static int SlantedFace( PolyMesh mesh )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			var n = mesh.FaceNormal( mesh.Faces[i] );

			if ( MathF.Abs( n.x ) > 0.2f && MathF.Abs( n.z ) > 0.2f && MathF.Abs( n.y ) < 0.2f )
				return i;
		}

		return -1;
	}

	/// <summary>Cut the top face of a box into two coplanar halves, and return one of them.</summary>
	static int SplitTopInHalf( PolyMesh mesh, int top )
	{
		var face = mesh.Faces[top];
		var corners = face.Indices.Select( i => mesh.Positions[i] ).ToList();

		var midA = mesh.AddVertex( (corners[0] + corners[1]) / 2f );
		var midB = mesh.AddVertex( (corners[2] + corners[3]) / 2f );

		mesh.Faces.RemoveAt( top );

		mesh.AddFace( new[] { face.Indices[0], midA, midB, face.Indices[3] } );
		var half = mesh.Faces.Count - 1;
		mesh.AddFace( new[] { midA, face.Indices[1], face.Indices[2], midB } );

		return half;
	}

	static int MovedVertices( PolyMesh before, PolyMesh after )
	{
		var count = 0;

		for ( var i = 0; i < before.VertexCount; i++ )
		{
			if ( (after.Positions[i] - before.Positions[i]).LengthSquared > 1e-12f )
				count++;
		}

		return count;
	}

	static float Extent( PolyMesh mesh, int axis )
	{
		var lo = float.MaxValue;
		var hi = float.MinValue;

		foreach ( var p in mesh.Positions )
		{
			var v = axis == 0 ? p.x : axis == 1 ? p.y : p.z;

			lo = MathF.Min( lo, v );
			hi = MathF.Max( hi, v );
		}

		return hi - lo;
	}

	static float Centre( PolyMesh mesh, int axis )
	{
		var lo = float.MaxValue;
		var hi = float.MinValue;

		foreach ( var p in mesh.Positions )
		{
			var v = axis == 0 ? p.x : axis == 1 ? p.y : p.z;

			lo = MathF.Min( lo, v );
			hi = MathF.Max( hi, v );
		}

		return (lo + hi) / 2f;
	}
}
