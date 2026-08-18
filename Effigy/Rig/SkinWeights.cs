using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>One bone's influence over one vertex.</summary>
public readonly struct BoneWeight
{
	public readonly int Bone;
	public readonly float Weight;

	public BoneWeight( int bone, float weight )
	{
		Bone = bone;
		Weight = weight;
	}

	public override string ToString() => $"{Bone}:{Weight:0.###}";
}

/// <summary>
/// Per-vertex bone influences, parallel to PolyMesh.Positions.
///
/// INFLUENCE COUNT IS UNBOUNDED HERE, ON PURPOSE. Engines want four; subdivision and weight
/// smoothing both produce more than four as a matter of course, and clamping at every intermediate
/// step throws away weight that later steps would have used, so the result depends on how many
/// times you happened to smooth. Keeping authoring lossless and pruning once, at export, makes the
/// output a function of the input instead. See Prune.
///
/// The invariant everything else relies on: every vertex's weights are non-negative and sum to 1.
/// Blend preserves it for any affine combination, which is exactly what Catmull-Clark is made of —
/// see the note in CatmullClark about why that is not a lucky accident.
/// </summary>
public sealed class SkinWeights
{
	public List<BoneWeight[]> Vertices = new();

	public int Count => Vertices.Count;

	public SkinWeights() { }

	public SkinWeights( int vertexCount )
	{
		for ( var i = 0; i < vertexCount; i++ )
			Vertices.Add( Array.Empty<BoneWeight>() );
	}

	public BoneWeight[] this[int i]
	{
		get => Vertices[i];
		set => Vertices[i] = value;
	}

	/// <summary>Everything weighted to one bone. What a static model exports as.</summary>
	public static SkinWeights AllTo( int vertexCount, int bone )
	{
		var w = new SkinWeights();
		var single = new[] { new BoneWeight( bone, 1f ) };

		for ( var i = 0; i < vertexCount; i++ )
			w.Vertices.Add( single );

		return w;
	}

	/// <summary>
	/// Combine several weighted influence sets into one — the operation subdivision, smoothing and
	/// interpolation are all made of.
	///
	/// If the incoming coefficients sum to 1 and each input is a partition of unity, so is the
	/// result. That is the whole reason this is the only way weights are ever combined.
	/// </summary>
	public static BoneWeight[] Blend( IEnumerable<(BoneWeight[] Weights, float Coefficient)> terms )
	{
		var acc = new Dictionary<int, float>();

		foreach ( var (weights, k) in terms )
		{
			if ( weights is null || k == 0f )
				continue;

			foreach ( var bw in weights )
			{
				acc.TryGetValue( bw.Bone, out var existing );
				acc[bw.Bone] = existing + bw.Weight * k;
			}
		}

		return Finish( acc );
	}

	public static BoneWeight[] Blend( params (BoneWeight[] Weights, float Coefficient)[] terms ) =>
		Blend( (IEnumerable<(BoneWeight[], float)>)terms );

	/// <summary>
	/// Drop to at most `max` influences per vertex and renormalise.
	///
	/// Called once, at export, because engines index a fixed number of bones per vertex — four,
	/// almost universally. Dropping the smallest influences and rescaling the survivors is the
	/// standard approach and is visually invisible at four: the fifth influence on a Catmull-Clark
	/// vertex is worth well under a percent.
	/// </summary>
	public static BoneWeight[] Prune( BoneWeight[] weights, int max )
	{
		if ( max <= 0 )
			throw new ArgumentOutOfRangeException( nameof( max ) );

		if ( weights is null || weights.Length == 0 )
			return Array.Empty<BoneWeight>();

		if ( weights.Length <= max )
			return weights;

		var kept = weights.OrderByDescending( w => w.Weight ).Take( max ).ToArray();
		var total = kept.Sum( w => w.Weight );

		if ( total <= 1e-9f )
			return new[] { new BoneWeight( kept[0].Bone, 1f ) };

		for ( var i = 0; i < kept.Length; i++ )
			kept[i] = new BoneWeight( kept[i].Bone, kept[i].Weight / total );

		return kept;
	}

	/// <summary>Normalise, drop negligible and negative influences, and order strongest first.
	/// Shared by Blend and the binders so there is one definition of a tidy weight set.</summary>
	internal static BoneWeight[] Finish( Dictionary<int, float> acc, float epsilon = 1e-6f )
	{
		var total = 0f;

		foreach ( var v in acc.Values )
		{
			if ( v > epsilon )
				total += v;
		}

		if ( total <= 0f )
			return Array.Empty<BoneWeight>();

		var result = new List<BoneWeight>( acc.Count );

		foreach ( var (bone, v) in acc )
		{
			if ( v > epsilon )
				result.Add( new BoneWeight( bone, v / total ) );
		}

		result.Sort( ( a, b ) => b.Weight.CompareTo( a.Weight ) );
		return result.ToArray();
	}

	public SkinWeights Clone()
	{
		var s = new SkinWeights();

		foreach ( var w in Vertices )
			s.Vertices.Add( (BoneWeight[])w.Clone() );

		return s;
	}

	/// <summary>
	/// What is wrong with these weights, as data rather than an exception — same posture as
	/// MeshValidator, and for the same reason: mid-edit states are allowed to be briefly invalid.
	/// </summary>
	public List<string> Validate( int vertexCount, int boneCount )
	{
		var errors = new List<string>();

		if ( Vertices.Count != vertexCount )
			errors.Add( $"{Vertices.Count} weight sets for {vertexCount} vertices" );

		for ( var i = 0; i < Vertices.Count; i++ )
		{
			var w = Vertices[i];

			if ( w.Length == 0 )
			{
				errors.Add( $"vertex {i} is unweighted" );
				continue;
			}

			var sum = 0f;

			foreach ( var bw in w )
			{
				if ( bw.Bone < 0 || bw.Bone >= boneCount )
					errors.Add( $"vertex {i} references bone {bw.Bone}, out of range" );

				if ( bw.Weight < 0f )
					errors.Add( $"vertex {i} has a negative weight on bone {bw.Bone}" );

				sum += bw.Weight;
			}

			if ( MathF.Abs( sum - 1f ) > 1e-3f )
				errors.Add( $"vertex {i} weights sum to {sum:0.####}, not 1" );
		}

		return errors;
	}
}
