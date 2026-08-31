using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// A cut that severs a part, and the part list catching up with it.
///
/// This was the last of the three cases WHAT-IS-LEFT named under "exercise the boolean past the one
/// case that works", and the only one whose fix was not in the repair at all: the boolean returns a
/// perfectly good mesh, and the bug is that a Body is assumed to be one solid by everything that
/// reads it. So the checks here are about COUNTS AND IDENTITY rather than about geometry — how many
/// bodies, which id kept which piece, and whether two rebuilds agree.
/// </summary>
public static class SplitTests
{
	public static void Run()
	{
		Report.Section( "split: a mesh holding two solids is two solids" );
		TestConnectedPieces();

		Report.Section( "split: pieces come back in the same order every time" );
		TestDeterministicOrder();

		Report.Section( "split: nothing is lost taking a mesh apart" );
		TestNothingLost();

		Report.Section( "split: a severing cut grows the part list" );
		TestSeveringCutAddsBodies();

		Report.Section( "split: a cut that does NOT sever leaves the part list alone" );
		TestOrdinaryCutIsUntouched();
	}

	/// <summary>Two boxes that touch nowhere, in one mesh, are two pieces. One box is one.</summary>
	static void TestConnectedPieces()
	{
		var one = Primitives.Box( 1, 1, 1 );

		Report.Check( "one box is one piece", MeshSplit.PieceCount( one ) == 1,
			$"{MeshSplit.PieceCount( one )}" );

		var two = TwoBoxes( gap: 3f );

		Report.Check( "two separated boxes in one mesh are two pieces",
			MeshSplit.PieceCount( two ) == 2, $"{MeshSplit.PieceCount( two )}" );

		var pieces = MeshSplit.ConnectedPieces( two );

		Report.Check( "and each piece is a closed solid on its own",
			pieces.Count == 2
			&& pieces.All( p => MeshValidator.Validate( p ).IsClosed )
			&& pieces.All( p => MeshValidator.Validate( p ).IsValid ),
			string.Join( "; ", pieces.Select( p => MeshValidator.Validate( p ).ToString() ) ) );

		// TOUCHING AT A CORNER IS ONE SOLID, and that is the conservative rule MeshSplit documents.
		// Splitting something the user thinks of as one part renames bodies underneath them.
		var corner = TwoBoxesSharingACorner();

		Report.Check( "two boxes sharing a corner vertex are ONE piece, not two",
			MeshSplit.PieceCount( corner ) == 1, $"{MeshSplit.PieceCount( corner )}" );
	}

	/// <summary>
	/// The property that makes body ids safe. Two runs over meshes built in DIFFERENT face orders
	/// must still hand the pieces back in the same order, or a rebuild renames them.
	/// </summary>
	static void TestDeterministicOrder()
	{
		// Same two solids, assembled the other way round. A splitter that just returns groups in
		// discovery order passes a same-mesh-twice test and fails this one.
		var forward = TwoUnequalBoxes( bigFirst: true );
		var backward = TwoUnequalBoxes( bigFirst: false );

		var a = MeshSplit.ConnectedPieces( forward );
		var b = MeshSplit.ConnectedPieces( backward );

		Report.Check( "both orders give two pieces", a.Count == 2 && b.Count == 2 );

		var volumesA = a.Select( m => MathF.Abs( m.SignedVolume() ) ).ToList();
		var volumesB = b.Select( m => MathF.Abs( m.SignedVolume() ) ).ToList();

		Report.Check( "largest comes first regardless of how the mesh was assembled",
			volumesA[0] > volumesA[1] && volumesB[0] > volumesB[1],
			$"{volumesA[0]:0.###}/{volumesA[1]:0.###} vs {volumesB[0]:0.###}/{volumesB[1]:0.###}" );

		Report.Check( "and the same piece is first in both", MathF.Abs( volumesA[0] - volumesB[0] ) < 1e-4f,
			$"{volumesA[0]:0.####} vs {volumesB[0]:0.####}" );

		// EQUAL VOLUMES ARE THE CASE THE TIEBREAK EXISTS FOR: a symmetric part cut down the middle.
		// Volume alone cannot order those, and float noise deciding it means the ids swap between
		// rebuilds - which reads as a sketch jumping to the other half of the part.
		var symmetricA = TwoBoxes( gap: 3f );
		var symmetricB = TwoBoxes( gap: 3f, reverse: true );

		var sa = MeshSplit.ConnectedPieces( symmetricA );
		var sb = MeshSplit.ConnectedPieces( symmetricB );

		Report.Check( "two equal halves still come back in a fixed order",
			MathF.Abs( MinX( sa[0] ) - MinX( sb[0] ) ) < 1e-5f
			&& MathF.Abs( MinX( sa[1] ) - MinX( sb[1] ) ) < 1e-5f,
			$"first at x={MinX( sa[0] ):0.###} vs {MinX( sb[0] ):0.###}" );
	}

	/// <summary>
	/// The pieces add up to what came in. Volume, faces, and the two things a naive extraction drops
	/// on the floor: per-corner UVs and skin weights.
	/// </summary>
	static void TestNothingLost()
	{
		var mesh = TwoBoxes( gap: 3f );

		// Mark every corner so a dropped or reset UV shows up as a number rather than as a shrug.
		var seed = 0;

		foreach ( var face in mesh.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
				face.UVs[i] = new Vec2( seed * 0.01f, i * 0.1f );

			face.Material = seed % 3;
			seed++;
		}

		mesh.Skin = SkinWeights.AllTo( mesh.VertexCount, 0 );

		for ( var i = 0; i < mesh.VertexCount; i++ )
			mesh.Skin[i] = new[] { new BoneWeight( i % 4, 1f ) };

		var wholeVolume = MathF.Abs( mesh.SignedVolume() );
		var pieces = MeshSplit.ConnectedPieces( mesh );

		Report.Check( "the faces are all accounted for",
			pieces.Sum( p => p.FaceCount ) == mesh.FaceCount,
			$"{pieces.Sum( p => p.FaceCount )} of {mesh.FaceCount}" );

		Report.Check( "and so is the volume",
			MathF.Abs( pieces.Sum( p => MathF.Abs( p.SignedVolume() ) ) - wholeVolume ) < 1e-3f,
			$"{pieces.Sum( p => MathF.Abs( p.SignedVolume() ) ):0.####} vs {wholeVolume:0.####}" );

		Report.Check( "material slots come across",
			pieces.SelectMany( p => p.Faces ).Select( f => f.Material ).OrderBy( m => m )
				.SequenceEqual( mesh.Faces.Select( f => f.Material ).OrderBy( m => m ) ) );

		// Per-corner UVs are the reason Extract copies faces rather than re-deriving them, and a
		// splitter that rebuilt faces from positions would zero these without failing anything else.
		var uvsIn = mesh.Faces.SelectMany( f => f.UVs ).Select( uv => (uv.x, uv.y) ).OrderBy( t => t.x ).ThenBy( t => t.y );
		var uvsOut = pieces.SelectMany( p => p.Faces ).SelectMany( f => f.UVs ).Select( uv => (uv.x, uv.y) ).OrderBy( t => t.x ).ThenBy( t => t.y );

		Report.Check( "per-corner UVs come across", uvsIn.SequenceEqual( uvsOut ) );

		Report.Check( "and every piece is still rigged", pieces.All( p => p.IsRigged ),
			string.Join( ", ", pieces.Select( p => $"{p.Skin?.Count ?? -1}/{p.VertexCount}" ) ) );

		// The input is left alone. Callers replace their own mesh with piece 0 and would be very
		// surprised to find the thing they split had been edited under them.
		Report.Check( "the mesh handed in is not modified",
			mesh.FaceCount == 12 && mesh.VertexCount == 16,
			$"{mesh.VertexCount}v/{mesh.FaceCount}f" );
	}

	/// <summary>
	/// End to end: a Remove extrude whose result is two solids. The boolean is stubbed - this is
	/// about what the FEATURE does with the mesh it gets back, which is the part that was missing.
	/// </summary>
	static void TestSeveringCutAddsBodies()
	{
		var previous = MeshBoolean.Provider;

		try
		{
			// The stub stands in for a cut that went all the way through: two blocks, no contact.
			MeshBoolean.Provider = new SeveringBoolean { Result = TwoBoxes( gap: 3f ) };

			var studio = new PartStudio();
			var box = studio.Add( new PrimitiveFeature() );
			box.SizeX.Value = 4f;

			var sketch = studio.Add( new SketchFeature() );
			sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

			var cut = studio.Add( new ExtrudeFeature() );
			cut.SketchFeatureId = sketch.Id;
			cut.Distance.Value = 5f;
			cut.Result.Index = 3; // Remove - see SketchConsumingFeature.ResultRemove

			studio.Rebuild();

			Report.Check( "the studio now holds two bodies", studio.Bodies.Count == 2,
				$"{studio.Bodies.Count}: {string.Join( ", ", studio.Bodies.Select( b => b.Id ) )}" );

			// THE IDENTITY CHECK, and the one that matters most. Everything hanging off this part -
			// a sketch on one of its faces, a later body selection - is holding the original id.
			Report.Check( "the original body keeps its id", studio.Bodies[0].FeatureId == box.Id,
				$"{studio.Bodies[0].FeatureId}" );

			Report.Check( "and the offcut is named after the feature that made it",
				studio.Bodies.Count == 2 && studio.Bodies[1].FeatureId == cut.Id
				&& studio.Bodies[1].Id.StartsWith( cut.Id ),
				studio.Bodies.Count == 2 ? $"{studio.Bodies[1].Id} / {studio.Bodies[1].FeatureId}" : "only one body" );

			Report.Check( "the cut warns rather than fails", cut.Error is null && cut.Warning is not null,
				$"error={cut.Error ?? "none"}, warning={cut.Warning ?? "none"}" );

			// A REBUILD MUST NOT RENAME ANYTHING. The ids are the whole reason the piece order is a
			// promise, so this is the check that promise exists for.
			var idsBefore = studio.Bodies.Select( b => b.Id ).ToList();

			studio.Rebuild();

			Report.Check( "and a second rebuild produces the same ids in the same order",
				studio.Bodies.Select( b => b.Id ).SequenceEqual( idsBefore ),
				string.Join( ", ", studio.Bodies.Select( b => b.Id ) ) );
		}
		finally
		{
			MeshBoolean.Provider = previous;
		}
	}

	/// <summary>The far more common case, and the one a regression here would break: a pocket.</summary>
	static void TestOrdinaryCutIsUntouched()
	{
		var previous = MeshBoolean.Provider;

		try
		{
			MeshBoolean.Provider = new SeveringBoolean { Result = Primitives.Box( 4, 4, 4 ) };

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() ).SizeX.Value = 4f;

			var sketch = studio.Add( new SketchFeature() );
			sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

			var cut = studio.Add( new ExtrudeFeature() );
			cut.SketchFeatureId = sketch.Id;
			cut.Distance.Value = 1f;
			cut.Result.Index = 3;

			studio.Rebuild();

			Report.Check( "a pocket leaves one body", studio.Bodies.Count == 1,
				$"{studio.Bodies.Count}" );
			Report.Check( "and says nothing about separation", cut.Warning is null,
				cut.Warning ?? "" );
		}
		finally
		{
			MeshBoolean.Provider = previous;
		}
	}

	// --- fixtures ---------------------------------------------------------------------------------

	static float MinX( PolyMesh mesh ) => mesh.Positions.Min( p => p.x );

	/// <summary>Two unit boxes with a gap between them, in one mesh. What a severing cut returns.</summary>
	static PolyMesh TwoBoxes( float gap, bool reverse = false )
	{
		var left = Primitives.Box( 1, 1, 1 );
		var right = Primitives.Box( 1, 1, 1 );

		MeshTransform.Apply( left, Xform.Translate( new Vec3( -gap * 0.5f, 0, 0 ) ) );
		MeshTransform.Apply( right, Xform.Translate( new Vec3( gap * 0.5f, 0, 0 ) ) );

		var mesh = reverse ? right : left;
		MeshTransform.Append( mesh, reverse ? left : right );

		return mesh;
	}

	/// <summary>Two boxes of different sizes, assembled in either order.</summary>
	static PolyMesh TwoUnequalBoxes( bool bigFirst )
	{
		var big = Primitives.Box( 2, 2, 2 );
		var small = Primitives.Box( 1, 1, 1 );

		MeshTransform.Apply( big, Xform.Translate( new Vec3( -4, 0, 0 ) ) );
		MeshTransform.Apply( small, Xform.Translate( new Vec3( 4, 0, 0 ) ) );

		var mesh = bigFirst ? big : small;
		MeshTransform.Append( mesh, bigFirst ? small : big );

		return mesh;
	}

	/// <summary>
	/// Two boxes meeting at exactly one vertex. Built by hand rather than by Append, because they
	/// have to SHARE the index — two coincident but distinct vertices is the other case, and it is
	/// the one that should split.
	/// </summary>
	static PolyMesh TwoBoxesSharingACorner()
	{
		var mesh = Primitives.Box( 1, 1, 1 );
		var second = Primitives.Box( 1, 1, 1 );

		MeshTransform.Apply( second, Xform.Translate( new Vec3( 1, 1, 1 ) ) );

		// The shared point sits at (0.5, 0.5, 0.5) in both. Find it in each and weld the second onto
		// the first's index as the meshes are appended.
		var shared = new Vec3( 0.5f, 0.5f, 0.5f );
		var hostIndex = mesh.Positions.FindIndex( p => (p - shared).Length < 1e-4f );
		var guestIndex = second.Positions.FindIndex( p => (p - shared).Length < 1e-4f );

		if ( hostIndex < 0 || guestIndex < 0 )
			throw new InvalidOperationException( "the corner fixture no longer shares a corner" );

		var offset = mesh.VertexCount;
		var map = new int[second.VertexCount];

		for ( var i = 0; i < second.VertexCount; i++ )
		{
			if ( i == guestIndex )
			{
				map[i] = hostIndex;
				continue;
			}

			map[i] = mesh.AddVertex( second.Positions[i] );
		}

		foreach ( var face in second.Faces )
			mesh.AddFace( face.Indices.Select( i => map[i] ).ToArray(), (Vec2[])face.UVs.Clone(), face.Material );

		_ = offset;
		return mesh;
	}

	/// <summary>A boolean that hands back a fixed mesh. What the cut returns is the point of these
	/// tests; how it was computed is not.</summary>
	sealed class SeveringBoolean : IMeshBoolean
	{
		public PolyMesh Result;

		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			result = Result.Clone();
			error = null;
			return true;
		}
	}
}
