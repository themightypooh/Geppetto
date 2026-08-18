using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Which body a run of vertices in a merged mesh came from. See PartStudio.ToMeshWithBodies.</summary>
public readonly struct BodyRange
{
	public readonly string BodyId;
	public readonly string BodyName;
	public readonly int Start;
	public readonly int Count;

	public BodyRange( string bodyId, string bodyName, int start, int count )
	{
		BodyId = bodyId;
		BodyName = bodyName;
		Start = start;
		Count = count;
	}

	public bool Contains( int vertex ) => vertex >= Start && vertex < Start + Count;

	public override string ToString() => $"{BodyName} [{Start}..{Start + Count})";
}

/// <summary>
/// Generating skin weights, so nobody has to paint them.
///
/// WHAT THIS IS NOT: heat-diffusion binding. Real heat diffusion solves a Laplacian system over
/// the mesh and is the right long-term answer — it respects the surface, so a weight cannot bleed
/// across a gap that is near in space but far across the mesh. What is here instead is distance to
/// the bone SEGMENT plus optional smoothing over mesh adjacency, which is cheap, has no solver,
/// and is good enough for the hard-surface props this tool is aimed at. The failure case is honest
/// and predictable: two limbs close together in space bleed into each other. That is the day to
/// write the solver.
///
/// THE INTERESTING ONE IS BindBodies. Because the model is parametric, a bone can own a FEATURE
/// rather than a set of vertices — "this cylinder is the forearm". Vertex indices die on every
/// rebuild; a body id does not. So the binding survives a parameter change, and the weights are
/// regenerated rather than invalidated. Blender cannot do this, because it has no history to bind
/// against.
/// </summary>
public static class SkinBinder
{
	/// <summary>Each vertex fully weighted to whichever bone segment is nearest. Blocky across
	/// joints, and exactly right for a rigid mechanical part like a hinge or a piston.</summary>
	public static SkinWeights BindRigid( PolyMesh mesh, Skeleton skeleton )
	{
		RequireBones( skeleton );

		var weights = new SkinWeights();
		var segments = Segments( skeleton );

		foreach ( var p in mesh.Positions )
		{
			var best = 0;
			var bestDist = float.MaxValue;

			for ( var b = 0; b < segments.Length; b++ )
			{
				var d = DistanceToSegment( p, segments[b].Head, segments[b].Tail );

				if ( d < bestDist )
				{
					bestDist = d;
					best = b;
				}
			}

			weights.Vertices.Add( new[] { new BoneWeight( best, 1f ) } );
		}

		return weights;
	}

	/// <summary>
	/// Inverse-distance weighting against every bone segment, so joints bend smoothly.
	///
	/// `falloff` is the exponent: 1 is very soft and reaches across the whole model, 4 is nearly
	/// rigid. 2 is a reasonable default and matches what most auto-skin tools land on.
	/// Contributions below `cutoff` of the strongest are dropped before normalising, which is what
	/// stops a bone on the far side of the model owning half a percent of every vertex.
	/// </summary>
	public static SkinWeights BindSmooth( PolyMesh mesh, Skeleton skeleton, float falloff = 2f, float cutoff = 0.02f )
	{
		RequireBones( skeleton );

		if ( falloff <= 0f )
			throw new ArgumentOutOfRangeException( nameof( falloff ), "Falloff must be positive" );

		var weights = new SkinWeights();
		var segments = Segments( skeleton );
		var distances = new float[segments.Length];

		foreach ( var p in mesh.Positions )
		{
			var nearest = float.MaxValue;

			for ( var b = 0; b < segments.Length; b++ )
			{
				distances[b] = DistanceToSegment( p, segments[b].Head, segments[b].Tail );
				nearest = MathF.Min( nearest, distances[b] );
			}

			// A vertex sitting exactly on a bone would divide by zero. Snapping it wholly to that
			// bone is both the limit of the formula and the answer anyone would expect.
			if ( nearest < 1e-6f )
			{
				var onBone = Array.IndexOf( distances, nearest );
				weights.Vertices.Add( new[] { new BoneWeight( onBone < 0 ? 0 : onBone, 1f ) } );
				continue;
			}

			var acc = new Dictionary<int, float>();
			var strongest = 0f;

			for ( var b = 0; b < segments.Length; b++ )
			{
				var influence = 1f / MathF.Pow( distances[b], falloff );
				acc[b] = influence;
				strongest = MathF.Max( strongest, influence );
			}

			var floor = strongest * cutoff;

			foreach ( var b in new List<int>( acc.Keys ) )
			{
				if ( acc[b] < floor )
					acc.Remove( b );
			}

			weights.Vertices.Add( SkinWeights.Finish( acc ) );
		}

		return weights;
	}

	/// <summary>
	/// Bind whole bodies to named bones — the parametric path.
	///
	/// Every vertex of a bound body gets that bone at full weight. Bodies with no mapping fall back
	/// to the nearest bone, so a partial mapping still produces a complete rig rather than a mesh
	/// with holes in its weighting.
	///
	/// Follow it with SmoothWeights to soften the seams between bodies; on its own this produces
	/// rigid parts, which is the correct result for machinery and the wrong one for a limb.
	/// </summary>
	public static SkinWeights BindBodies(
		PolyMesh mesh,
		IReadOnlyList<BodyRange> ranges,
		IReadOnlyDictionary<string, string> bodyIdToBoneName,
		Skeleton skeleton )
	{
		RequireBones( skeleton );

		if ( ranges is null ) throw new ArgumentNullException( nameof( ranges ) );
		if ( bodyIdToBoneName is null ) throw new ArgumentNullException( nameof( bodyIdToBoneName ) );

		var fallback = BindRigid( mesh, skeleton );
		var weights = new SkinWeights( mesh.VertexCount );

		for ( var i = 0; i < mesh.VertexCount; i++ )
			weights[i] = fallback[i];

		foreach ( var range in ranges )
		{
			if ( !bodyIdToBoneName.TryGetValue( range.BodyId, out var boneName ) )
				continue;

			var bone = skeleton.IndexOf( boneName );

			if ( bone < 0 )
				throw new InvalidOperationException(
					$"Body '{range.BodyName}' is bound to bone '{boneName}', which is not in the skeleton" );

			var bound = new[] { new BoneWeight( bone, 1f ) };

			for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
				weights[i] = bound;
		}

		return weights;
	}

	/// <summary>
	/// Average each vertex's weights with its mesh neighbours, a few times.
	///
	/// This is what turns a rigid body-bound rig into one that bends: it diffuses weight ACROSS THE
	/// SURFACE rather than through space, so it respects the mesh the way plain distance weighting
	/// does not. It is a poor man's heat diffusion — same idea, no solver, run to a fixed iteration
	/// count instead of to convergence.
	///
	/// `strength` is how far each pass moves toward the neighbour average; 1 is a full replace.
	/// Each pass is an affine combination, so the partition of unity survives any number of them.
	/// </summary>
	public static SkinWeights SmoothWeights( PolyMesh mesh, SkinWeights weights, int iterations = 2, float strength = 0.5f )
	{
		if ( iterations <= 0 )
			return weights;

		strength = Math.Clamp( strength, 0f, 1f );

		var neighbours = mesh.BuildVertexEdges();
		var current = weights;

		for ( var pass = 0; pass < iterations; pass++ )
		{
			var next = new SkinWeights( mesh.VertexCount );

			for ( var v = 0; v < mesh.VertexCount; v++ )
			{
				var edges = neighbours[v];

				if ( edges.Count == 0 )
				{
					next[v] = current[v];
					continue;
				}

				var terms = new List<(BoneWeight[], float)>( edges.Count + 1 )
				{
					(current[v], 1f - strength)
				};

				var share = strength / edges.Count;

				foreach ( var e in edges )
					terms.Add( (current[e.A == v ? e.B : e.A], share) );

				next[v] = SkinWeights.Blend( terms );
			}

			current = next;
		}

		return current;
	}

	// --- helpers -----------------------------------------------------------------------------

	static void RequireBones( Skeleton skeleton )
	{
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		if ( skeleton.Count == 0 )
			throw new InvalidOperationException( "Cannot bind to a skeleton with no bones" );
	}

	static (Vec3 Head, Vec3 Tail)[] Segments( Skeleton skeleton )
	{
		var segments = new (Vec3, Vec3)[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
			segments[i] = (skeleton.HeadWorld( i ), skeleton.TailWorld( i ));

		return segments;
	}

	/// <summary>Distance from a point to a line segment — not to the infinite line, which would
	/// let a short bone influence vertices far off its ends.</summary>
	public static float DistanceToSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var lengthSquared = ab.LengthSquared;

		if ( lengthSquared < 1e-12f )
			return (p - a).Length;

		var t = Math.Clamp( Vec3.Dot( p - a, ab ) / lengthSquared, 0f, 1f );
		return (p - (a + ab * t)).Length;
	}
}
