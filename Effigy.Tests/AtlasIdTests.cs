using System;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The atlas id, judged by the one thing it exists for: it must tell "same mesh, re-unwrapped" from
/// "same mesh, same atlas", because <see cref="MultiresSculpt.TopologyId"/> cannot.
/// </summary>
public static class AtlasIdTests
{
	public static void Run()
	{
		Section( "atlas id: a re-unwrap is a different atlas" );
		TestUnwrappingTheSameMeshTwiceGivesTheSameId();
		TestAMarginChangeChangesTheId();
		TestMovingAVertexDoesNotChangeTheId();
		TestTopologyIdIgnoresTheAtlasAndAtlasIdDoesNot();
		TestAnEmptyMeshReturnsAStableValue();
		TestRasteriseIsVisibleAndHandsBackUsableBarycentrics();
	}

	static void TestUnwrappingTheSameMeshTwiceGivesTheSameId()
	{
		// Unwrapping is deterministic (UnwrapTests already asserts that), so two unwraps of the same
		// mesh land on the same atlas and must therefore land on the same id. If this fails the id
		// is hashing something unstable, and a cache keyed on it would re-render for no reason.
		var a = Primitives.Box( 2, 2, 2 );
		var b = Primitives.Box( 2, 2, 2 );

		UVUnwrap.Unwrap( a );
		UVUnwrap.Unwrap( b );

		Check( "two unwraps of the same mesh give the same id", AtlasId.Of( a ) == AtlasId.Of( b ) );
	}

	static void TestAMarginChangeChangesTheId()
	{
		// The gutter between islands moves every island in the atlas, so the id has to change. This
		// is the whole reason AtlasId exists.
		var tight = Primitives.Box( 2, 2, 2 );
		UVUnwrap.Unwrap( tight, margin: 0f );

		var spaced = Primitives.Box( 2, 2, 2 );
		UVUnwrap.Unwrap( spaced, margin: 0.05f );

		Check( "a different island margin changes the id", AtlasId.Of( tight ) != AtlasId.Of( spaced ) );
	}

	static void TestMovingAVertexDoesNotChangeTheId()
	{
		// The id hashes UVs, not positions, so a parametric edit that moves geometry but leaves the
		// atlas alone must not change it. That is what lets a cached canvas survive a normal edit.
		var a = Primitives.Box( 2, 2, 2 );
		UVUnwrap.Unwrap( a );

		var moved = a.Clone();
		moved.Positions[0] += new Vec3( 1, 2, 3 );

		Check( "moving a vertex without touching UVs keeps the id", AtlasId.Of( a ) == AtlasId.Of( moved ) );
	}

	static void TestTopologyIdIgnoresTheAtlasAndAtlasIdDoesNot()
	{
		// THE POINT OF THE FILE. TopologyId ignores UVs, so it is the same across a margin change
		// that rearranges the atlas. AtlasId must not be. A cache keyed on TopologyId alone would
		// hand back a canvas built for the old atlas; keyed on both, it can tell.
		var tight = Primitives.Box( 2, 2, 2 );
		UVUnwrap.Unwrap( tight, margin: 0f );

		var spaced = Primitives.Box( 2, 2, 2 );
		UVUnwrap.Unwrap( spaced, margin: 0.05f );

		Check( "TopologyId is unchanged by the margin change",
			MultiresSculpt.TopologyId( tight ) == MultiresSculpt.TopologyId( spaced ) );
		Check( "AtlasId is not", AtlasId.Of( tight ) != AtlasId.Of( spaced ) );
	}

	static void TestAnEmptyMeshReturnsAStableValue()
	{
		// An empty mesh has no UVs to hash, so the id is just the FNV offset basis. It must not
		// throw and must be the same every call, or a document with an empty body breaks a cache
		// check.
		var a = AtlasId.Of( new PolyMesh() );
		var b = AtlasId.Of( new PolyMesh() );

		Check( "an empty mesh returns a stable value", a == b, $"{a} vs {b}" );
	}

	static void TestRasteriseIsVisibleAndHandsBackUsableBarycentrics()
	{
		// Rasterise is internal now so the paint dab can walk the same texels the bake walks. Called
		// here, the fact that this line compiles is the proof of the visibility change; the
		// barycentrics it hands back must reconstruct the texel centre, because that reconstruction
		// is how a dab turns a texel into a 3D point to weight by distance.
		var a = new Vec2( 0, 0 );
		var b = new Vec2( 1, 0 );
		var c = new Vec2( 0, 1 );

		var count = 0;

		NormalBake.Rasterise( a, b, c, 4, 4, ( x, y, wa, wb, wc ) =>
		{
			count++;

			var centre = new Vec2( (x + 0.5f) / 4f, (y + 0.5f) / 4f );
			var rebuilt = a * wa + b * wb + c * wc;

			Check( $"texel ({x},{y}) barycentrics sum to one", MathF.Abs( wa + wb + wc - 1f ) < 1e-5f,
				$"{wa} + {wb} + {wc} = {wa + wb + wc}" );
			Check( $"texel ({x},{y}) barycentrics reconstruct the centre",
				MathF.Abs( rebuilt.x - centre.x ) < 1e-5f && MathF.Abs( rebuilt.y - centre.y ) < 1e-5f,
				$"({rebuilt.x}, {rebuilt.y}) vs ({centre.x}, {centre.y})" );
		} );

		// The lower-left half of a 4x4 map: every centre with x > 0, y > 0 and x + y <= 1, which is
		// ten texels. The exact number is the point — it pins the top-left fill rule in place.
		Check( "the triangle walks exactly ten texels", count == 10, $"{count}" );
	}
}
