using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>What a weight-paint stroke does to the bone being painted.</summary>
public enum WeightBrushKind
{
	/// <summary>Raise the painted bone's influence, taking what it gains from the others.</summary>
	Add,

	/// <summary>Lower it, giving what it loses back to the others.</summary>
	Subtract,

	/// <summary>Move it toward an absolute value rather than by an amount.</summary>
	Set,

	/// <summary>Move every influence toward the average of the vertex's neighbours. Painted bone
	/// independent — it smooths the whole set, which is what makes a seam disappear.</summary>
	Smooth,
}

/// <summary>One sample on a weight stroke. The editor makes these; the kernel never learns what a
/// mouse is — same split as <see cref="BrushSample"/>, and deliberately the same shape.</summary>
public readonly struct WeightSample
{
	public readonly Vec3 Position;
	public readonly float Radius;
	public readonly float Strength;

	public WeightSample( Vec3 position, float radius, float strength )
	{
		Position = position;
		Radius = radius;
		Strength = strength;
	}
}

public sealed class WeightStroke
{
	public WeightBrushKind Kind;
	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>Which bone is being painted, by INDEX into the skeleton this stroke is running
	/// against. Ignored by Smooth, which has no one bone.</summary>
	public int Bone;

	/// <summary>Where Set is heading. Ignored by everything else.</summary>
	public float Target = 1f;

	public bool MirrorX;

	public readonly List<WeightSample> Samples = new();
}

/// <summary>
/// Per-stroke undo: the previous influences of every vertex the stroke actually changed.
///
/// Same shape and the same reasoning as <see cref="BrushUndo"/> — a naive undo snapshots every
/// vertex's weights, and a stroke touches a handful.
/// </summary>
public sealed class WeightUndo
{
	readonly Dictionary<int, BoneWeight[]> _previous = new();

	public int Count => _previous.Count;

	public IReadOnlyDictionary<int, BoneWeight[]> Previous => _previous;

	public void Record( int vertex, BoneWeight[] weights )
	{
		// FIRST WRITE WINS. A stroke crosses the same vertex several times as the cursor moves, and
		// the state to go back to is the one before the stroke began, not before its last sample.
		if ( !_previous.ContainsKey( vertex ) )
			_previous[vertex] = weights;
	}

	public void Absorb( WeightUndo later )
	{
		foreach ( var (vertex, weights) in later._previous )
			Record( vertex, weights );
	}

	public void Restore( SkinWeights weights )
	{
		foreach ( var (vertex, previous) in _previous )
		{
			if ( vertex >= 0 && vertex < weights.Count )
				weights[vertex] = previous;
		}
	}
}

/// <summary>
/// Painting skin weights by hand, to fix what auto-weighting gets wrong.
///
/// WHY IT EXISTS. `SkinBinder` is nearest-bone weighting smoothed across adjacency — a poor man's
/// heat diffusion, and it is right most of the time and wrong in exactly the places that show: a
/// finger that picks up the neighbouring finger's bone because the two are closer through space than
/// along the surface, an armpit, the inside of an elbow. Those are minutes of painting and hours of
/// anything else.
///
/// THE INVARIANT IS THE WHOLE PROBLEM. Every vertex's influences are non-negative and sum to one,
/// and everything downstream leans on that — `Prune` renormalises against it, Catmull-Clark's affine
/// combinations preserve it, and the compiler's own culling assumes it. So a brush cannot simply add
/// to one bone: whatever the painted bone gains has to come from the others, proportionally, and
/// whatever it loses has to go back to them the same way. Every operation here is written as "move
/// the painted bone to w, and rescale the rest to 1 - w".
///
/// THE CASE THAT HAS NO ANSWER, and the one to know about: a vertex weighted ENTIRELY to the bone
/// you are subtracting from has nowhere to put the weight. Rescaling an empty remainder is a divide
/// by zero, and the tempting fixes are both wrong — normalising an all-zero set gives a vertex bound
/// to nothing, which collapses to the model origin on export, and quietly leaving 1.0 in place makes
/// the brush look broken. So such a vertex is left alone and reported: `WeightUndo` never sees it,
/// and `WeightPaintSession` counts them so the tool can say "these vertices have only one bone;
/// paint the bone you want them to move to instead".
///
/// INFLUENCE COUNT IS NOT CAPPED HERE, deliberately, for the reason SkinWeights already gives:
/// authoring stays lossless and `Prune` runs once, at export. Painting can and will produce a vertex
/// with six influences, and that is fine.
/// </summary>
public static class WeightBrush
{
	/// <summary>
	/// Apply a stroke, and hand back what it would take to undo it.
	///
	/// The mesh is read for positions and adjacency and is never written — it is the weights that
	/// change, which is the one structural difference from the sculpt brush.
	/// </summary>
	public static WeightUndo Apply( PolyMesh mesh, SkinWeights weights, WeightStroke stroke,
		float[] mask = null, MeshBVH bvh = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( weights is null )
			throw new ArgumentNullException( nameof( weights ) );

		if ( stroke is null )
			throw new ArgumentNullException( nameof( stroke ) );

		if ( weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );

		if ( mask is not null && mask.Length != mesh.VertexCount )
			throw new ArgumentException( $"mask ({mask.Length}) and mesh ({mesh.VertexCount}) disagree" );

		if ( stroke.Kind != WeightBrushKind.Smooth && stroke.Bone < 0 )
			throw new ArgumentException( "a weight stroke that is not Smooth has to name a bone" );

		bvh ??= MeshBVH.Build( mesh );

		var neighbours = mesh.BuildVertexEdges();
		var found = new List<int>();
		var undo = new WeightUndo();

		foreach ( var sample in stroke.Samples )
		{
			ApplySample( mesh, weights, stroke, mask, bvh, neighbours, found, undo, sample );

			if ( !stroke.MirrorX )
				continue;

			ApplySample( mesh, weights, stroke, mask, bvh, neighbours, found, undo,
				new WeightSample(
					new Vec3( -sample.Position.x, sample.Position.y, sample.Position.z ),
					sample.Radius, sample.Strength ) );
		}

		return undo;
	}

	/// <summary>
	/// How many vertices under this stroke could not move because they have only one influence.
	///
	/// Asked separately rather than returned from Apply, because the tool wants it to warn BEFORE
	/// the user has painted a stroke that visibly does nothing.
	/// </summary>
	public static int CountLocked( PolyMesh mesh, SkinWeights weights, WeightStroke stroke, MeshBVH bvh = null )
	{
		if ( stroke.Kind is not (WeightBrushKind.Subtract or WeightBrushKind.Set) )
			return 0;

		bvh ??= MeshBVH.Build( mesh );

		var found = new List<int>();
		var locked = new HashSet<int>();

		foreach ( var sample in stroke.Samples )
		{
			bvh.VerticesInRadius( mesh, sample.Position, sample.Radius, found );

			foreach ( var vi in found )
			{
				if ( OnlyInfluence( weights[vi], stroke.Bone ) )
					locked.Add( vi );
			}
		}

		return locked.Count;
	}

	static void ApplySample(
		PolyMesh mesh, SkinWeights weights, WeightStroke stroke, float[] mask,
		MeshBVH bvh, List<EdgeKey>[] neighbours, List<int> found, WeightUndo undo, WeightSample sample )
	{
		if ( sample.Radius <= 0f )
			return;

		bvh.VerticesInRadius( mesh, sample.Position, sample.Radius, found );

		foreach ( var vi in found )
		{
			var distance = (mesh.Positions[vi] - sample.Position).Length;
			var t = distance / sample.Radius;
			var amount = Brush.Falloff( t, stroke.Falloff ) * sample.Strength * (mask is null ? 1f : mask[vi]);

			if ( MathF.Abs( amount ) < 1e-6f )
				continue;

			var before = weights[vi];
			var after = stroke.Kind == WeightBrushKind.Smooth
				? SmoothOne( weights, neighbours[vi], vi, Math.Clamp( amount, 0f, 1f ) )
				: Retarget( before, stroke.Bone, TargetFor( before, stroke, amount ) );

			if ( after is null || Same( before, after ) )
				continue;

			undo.Record( vi, before );
			weights[vi] = after;
		}
	}

	/// <summary>Where this sample wants the painted bone to end up on this vertex.</summary>
	static float TargetFor( BoneWeight[] weights, WeightStroke stroke, float amount )
	{
		var current = WeightOf( weights, stroke.Bone );

		return stroke.Kind switch
		{
			WeightBrushKind.Add => current + amount,
			WeightBrushKind.Subtract => current - amount,
			// Set eases toward the target rather than snapping, so the falloff still shapes the edge
			// of the brush. Snapping would give a hard-edged disc regardless of falloff.
			WeightBrushKind.Set => current + (stroke.Target - current) * Math.Clamp( amount, 0f, 1f ),
			_ => current,
		};
	}

	/// <summary>
	/// The vertex's influences with one bone moved to `target` and the rest rescaled to fill the
	/// remainder. Null when it cannot be done.
	///
	/// This is the one function the invariant lives in. Three cases it has to get right:
	///
	/// - the bone is not on the vertex yet and is being raised — it gets added, and the existing
	///   influences are scaled down to make room.
	/// - the bone is being taken to zero — it is REMOVED rather than left at 0, because a zero
	///   influence is an export slot spent on nothing and `Prune` would rather have the room.
	/// - the bone is the vertex's only influence and is being lowered — refused, because there is
	///   nothing to rescale up. See the class comment.
	/// </summary>
	public static BoneWeight[] Retarget( BoneWeight[] weights, int bone, float target )
	{
		target = Math.Clamp( target, 0f, 1f );

		weights ??= Array.Empty<BoneWeight>();

		var others = 0f;

		foreach ( var w in weights )
		{
			if ( w.Bone != bone )
				others += w.Weight;
		}

		// Nothing else to give to or take from. Raising to a full 1 is still meaningful (it is
		// already 1), and anything less would leave the remainder nowhere to go.
		if ( others <= 1e-6f && target < 1f - 1e-6f )
			return null;

		// A vertex with no influences at all is not something painting should be inventing weights
		// for - it means the mesh was never bound - so it is only ever taken straight to the bone.
		if ( weights.Length == 0 )
			return target > 1e-6f ? new[] { new BoneWeight( bone, 1f ) } : null;

		var result = new List<BoneWeight>( weights.Length + 1 );
		var scale = others > 1e-9f ? (1f - target) / others : 0f;

		if ( target > 1e-6f )
			result.Add( new BoneWeight( bone, target ) );

		foreach ( var w in weights )
		{
			if ( w.Bone == bone )
				continue;

			var scaled = w.Weight * scale;

			// Dropping what rounds to nothing keeps a painted vertex from accumulating a tail of
			// influences worth a millionth each, which cost an export slot and buy nothing.
			if ( scaled > 1e-5f )
				result.Add( new BoneWeight( w.Bone, scaled ) );
		}

		if ( result.Count == 0 )
			return null;

		// Through Blend, so normalisation and ordering are the same one definition the binders and
		// subdivision already go through rather than a second one written here.
		return SkinWeights.Blend( new[] { (result.ToArray(), 1f) } );
	}

	/// <summary>One vertex moved toward the average of its neighbours' influences.</summary>
	static BoneWeight[] SmoothOne( SkinWeights weights, List<EdgeKey> edges, int vertex, float amount )
	{
		if ( edges.Count == 0 || amount <= 0f )
			return weights[vertex];

		var terms = new List<(BoneWeight[], float)>( edges.Count + 1 )
		{
			(weights[vertex], 1f - amount)
		};

		var share = amount / edges.Count;

		foreach ( var e in edges )
			terms.Add( (weights[e.A == vertex ? e.B : e.A], share) );

		return SkinWeights.Blend( terms );
	}

	static float WeightOf( BoneWeight[] weights, int bone )
	{
		if ( weights is null )
			return 0f;

		foreach ( var w in weights )
		{
			if ( w.Bone == bone )
				return w.Weight;
		}

		return 0f;
	}

	static bool OnlyInfluence( BoneWeight[] weights, int bone ) =>
		weights is { Length: 1 } && weights[0].Bone == bone;

	static bool Same( BoneWeight[] a, BoneWeight[] b )
	{
		if ( a is null || b is null || a.Length != b.Length )
			return false;

		for ( var i = 0; i < a.Length; i++ )
		{
			if ( a[i].Bone != b[i].Bone || MathF.Abs( a[i].Weight - b[i].Weight ) > 1e-6f )
				return false;
		}

		return true;
	}
}
