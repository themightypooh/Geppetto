using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// One outer garment measured against one garment worn under it: how much of the outer one has
/// ended up underneath. See <see cref="GarmentLayerCheck"/>.
/// </summary>
public sealed class LayerClip
{
	public string Outer = "";
	public string Inner = "";

	/// <summary>Outer-garment vertices that were near enough to the inner garment to measure.</summary>
	public int Tested;

	/// <summary>Of those, how many sit further under the inner garment's outside than the
	/// tolerance allows: the jacket showing the shirt through it.</summary>
	public int Through;

	/// <summary>How far under the worst of them is, in inches.</summary>
	public float Deepest;

	/// <summary>Where they are, one point per vertex, for the viewport's marks.</summary>
	public List<Vec3> Points = new();

	/// <summary>How deep each of <see cref="Points"/> is, in inches, in the same order.</summary>
	public List<float> Depths = new();

	/// <summary>The outer garment's vertex count, measured or not, for a share.</summary>
	public int OuterVertices;

	public float Share => OuterVertices == 0 ? 0f : Through / (float)OuterVertices;

	public override string ToString() => Through == 0
		? $"{Outer} stays over {Inner}."
		: $"{Through} of {OuterVertices} {Outer} vertices go under {Inner} ({Share:P1}), deepest {Deepest:0.##} in. "
			+ $"Raise {Outer}'s Clearance or a looser Fit; check {Outer} is below {Inner} in the tree.";
}

/// <summary>
/// Garment against garment: does a jacket stay outside the shirt it is worn over?
///
/// GarmentCheck asks whether a garment goes through the BODY. This is the other half: a layered
/// outfit where every garment clears the skin can still have the jacket's shoulder under the
/// shirt, and that shows as the shirt's colour through the jacket in the game.
///
/// WHICH PAIRS. Only a lower Layer under a higher one (Base 0, Shirt 1, Jacket 2, Outer 3; see
/// GarmentFeature.Layer). Two garments in the same layer - trousers and a tee are both Shirt -
/// are not stacked, and either may tuck into the other.
///
/// MEASURED AGAINST THE REAL SURFACE, NOT PARTICLE NORMALS. The inner garment's OUTSIDE wall
/// (MeshSolidify.OuterSheet - its lining faces the body, and against that every jacket vertex
/// would read as inside), and the outer garment's INSIDE wall, the side that touches the shirt.
/// A vertex counts only when its nearest point on the shirt is straight below it, inside a face:
/// past an open cuff or collar the nearest point is on the edge and which side you are on means
/// nothing.
///
/// TWO TOLERANCES. Settled - the bind pose, where the fit put it - allows a tenth of an inch, the
/// fit's own resting error. Posed allows a quarter: linear skinning thins a bent elbow, and both
/// garments thin with it.
/// </summary>
public static class GarmentLayerCheck
{
	/// <summary>How far under counts at the bind pose, in inches.</summary>
	public const float SettledTolerance = 0.1f;

	/// <summary>How far under counts in a test pose, in inches.</summary>
	public const float PosedTolerance = 0.25f;

	/// <summary>How far from the inner garment a vertex is still measured. Further than this it is
	/// clear of it - a sleeve's cuff past the end of the shirt's.</summary>
	public const float Reach = 4f;

	/// <summary>The cosine below which the nearest point is on an edge rather than straight below.</summary>
	const float StraightBelow = 0.9f;

	/// <summary>
	/// Every outer garment against every garment in a lower layer. Garments with a negative layer
	/// (trims, which have none) are left out. One result per pair measured, worst first.
	/// </summary>
	public static List<LayerClip> Run( IReadOnlyList<(string Name, PolyMesh Mesh, int Layer)> garments, float tolerance = SettledTolerance )
	{
		var results = new List<LayerClip>();

		if ( garments is null )
			return results;

		foreach ( var outer in garments )
		{
			if ( outer.Mesh is null || outer.Layer < 0 )
				continue;

			foreach ( var inner in garments )
			{
				if ( inner.Mesh is null || inner.Layer < 0 || inner.Layer >= outer.Layer || ReferenceEquals( inner.Mesh, outer.Mesh ) )
					continue;

				var clip = Pair( outer.Mesh, inner.Mesh, tolerance );
				clip.Outer = outer.Name ?? "";
				clip.Inner = inner.Name ?? "";
				results.Add( clip );
			}
		}

		return results.OrderByDescending( r => r.Through ).ThenByDescending( r => r.Deepest ).ToList();
	}

	/// <summary>How much of <paramref name="outer"/> is under <paramref name="inner"/>.</summary>
	public static LayerClip Pair( PolyMesh outer, PolyMesh inner, float tolerance = SettledTolerance )
	{
		var clip = new LayerClip();

		if ( outer is null || inner is null || outer.VertexCount == 0 || inner.FaceCount == 0 )
			return clip;

		var innerSheet = MeshSolidify.OuterSheet( inner, out _ ) ?? inner;

		// The outer garment's near wall is the first `offset` vertices of a solidified mesh
		// (Solidify writes the near wall, then the far wall). Unthickened, it is all of them.
		var count = MeshSolidify.OuterSheet( outer, out var offset ) is not null ? offset : outer.VertexCount;
		clip.OuterVertices = count;

		var tree = MeshBVH.Build( innerSheet );

		for ( var i = 0; i < count; i++ )
		{
			var p = outer.Positions[i];

			if ( tree.NearestSurface( innerSheet, p, Reach ) is not { } hit )
				continue;

			var d = p - hit.Point;
			var along = Vec3.Dot( d, hit.Normal );

			// Past an opening's edge: the sign of `along` says nothing there.
			if ( hit.Distance > 1e-4f && MathF.Abs( along ) < StraightBelow * hit.Distance )
				continue;

			clip.Tested++;
			var depth = -along;

			if ( depth <= tolerance )
				continue;

			clip.Through++;
			clip.Deepest = MathF.Max( clip.Deepest, depth );
			clip.Points.Add( p );
			clip.Depths.Add( depth );
		}

		return clip;
	}
}
