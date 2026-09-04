using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Painted weights that survive a rebuild — the part of weight painting that is actually hard.
///
/// THE PROBLEM IS THE SAME ONE THE SCULPT STAGE HAD. Effigy never stores a rig by vertex index; a
/// rebuild re-derives every weight from the body-to-bone assignments (`SkinBinder.BindBodies`), and
/// that is exactly what lets the parametric history stay alive under a rig. Painting fights that
/// head on: a painted vertex IS an edit expressed per vertex, and the next rebuild would throw it
/// away. Recomputing from assignments would then look like the brush had never worked.
///
/// THE ANSWER IS THE SCULPT ANSWER, and it is worth seeing that it is the same shape. A sculpt is
/// stored as deltas keyed on the cage's TOPOLOGY (`MultiresSculpt.TopologyId` — counts and face
/// indices, deliberately not positions, because positions are what a parametric edit is expected to
/// change). Topology unchanged means the vertex numbering means the same thing it did, so the edits
/// go straight back on. Topology changed means it does not, so the edits are KEPT and marked stale
/// rather than silently misapplied. That is the rule here too, and this class is deliberately small
/// because that rule is the whole of it.
///
/// TWO THINGS ARE STORED DIFFERENTLY FROM A SCULPT, and both matter.
///
/// **Bones are stored by NAME, never by index.** Bone names are the stable identifier this whole
/// project already committed to — Rig Control keys clips by name, and renaming one is a documented
/// breaking change. Indices are not stable at all: `Skeleton.RemoveBone` re-indexes everything after
/// the hole, so a layer holding index 4 would silently move to a different bone the first time
/// somebody deleted a bone above it. Names cost a dictionary lookup on apply and remove the entire
/// class of bug.
///
/// **The painted RESULT is stored, not a delta from the auto weights.** A delta of a normalised
/// quantity is not a well-defined thing: adding one breaks the sum, and re-normalising afterwards
/// gives an answer that depends on what the auto-binder happened to produce that run. Storing what
/// the vertex should end up as makes a repaint idempotent and makes "what did I paint here" a
/// question with an answer.
/// </summary>
public sealed class WeightPaintLayer
{
	/// <summary>Painted vertices, by index into the mesh this layer was captured on. Valid only
	/// while <see cref="Topology"/> matches — see the class comment.</summary>
	readonly Dictionary<int, List<(string Bone, float Weight)>> _painted = new();

	/// <summary>The topology this layer's vertex numbers mean something on.</summary>
	public long Topology { get; private set; }

	/// <summary>How many vertices carry paint.</summary>
	public int Count => _painted.Count;

	public bool IsEmpty => _painted.Count == 0;

	/// <summary>
	/// Whether the layer has been kept across a topology change it cannot be applied over.
	///
	/// Set rather than thrown, and the paint is NOT discarded, because the change may well be
	/// undone: the user pushes a fillet radius past what the boolean can take, the cage's face count
	/// moves, and pulling it back should not have cost them their paint. Same reasoning as
	/// SculptFeature's stale flag.
	/// </summary>
	public bool IsStale { get; private set; }

	public WeightPaintLayer() { }

	public WeightPaintLayer( PolyMesh mesh )
	{
		Topology = MultiresSculpt.TopologyId( mesh );
	}

	/// <summary>Every painted vertex, for a writer or a UI that wants to list them.</summary>
	public IEnumerable<(int Vertex, IReadOnlyList<(string Bone, float Weight)> Weights)> Painted
	{
		get
		{
			foreach ( var (vertex, weights) in _painted )
				yield return (vertex, weights);
		}
	}

	/// <summary>
	/// Record what a vertex now weighs, resolving indices to names against the skeleton it was
	/// painted with.
	///
	/// A vertex naming a bone the skeleton does not have is a caller fault rather than a shape, so
	/// it throws: the alternative is a layer that silently forgets an influence and a painted vertex
	/// whose weights no longer sum to one.
	/// </summary>
	public void Capture( int vertex, BoneWeight[] weights, Skeleton skeleton )
	{
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		if ( weights is null || weights.Length == 0 )
		{
			_painted.Remove( vertex );
			return;
		}

		var named = new List<(string, float)>( weights.Length );

		foreach ( var w in weights )
		{
			if ( w.Bone < 0 || w.Bone >= skeleton.Count )
				throw new ArgumentException( $"vertex {vertex} is weighted to bone {w.Bone}, and the skeleton has {skeleton.Count}" );

			named.Add( (skeleton.Bones[w.Bone].Name, w.Weight) );
		}

		_painted[vertex] = named;
	}

	/// <summary>Forget one vertex's paint, so the auto weights show through it again.</summary>
	public bool Clear( int vertex ) => _painted.Remove( vertex );

	public void ClearAll()
	{
		_painted.Clear();
		IsStale = false;
	}

	/// <summary>
	/// Whether this layer can be applied to a mesh, and if not, why — in the shape a diagnostic
	/// wants: what stopped it, with both models' numbers, and what would work.
	/// </summary>
	public bool CanApply( PolyMesh mesh, out string why )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( _painted.Count == 0 )
		{
			why = null;
			return true;
		}

		if ( MultiresSculpt.TopologyId( mesh ) == Topology )
		{
			why = null;
			return true;
		}

		why = $"The rebuilt mesh has {mesh.VertexCount} vertices and {mesh.FaceCount} faces, and its "
			+ "topology is not the one this weight paint was made on. Painted weights are stored per "
			+ $"vertex, so the {_painted.Count} painted vertices cannot be placed on it. The paint is "
			+ "kept: undo the feature edit that changed the topology and it comes straight back.";
		return false;
	}

	/// <summary>
	/// Put the paint back on top of freshly auto-bound weights, and say how many vertices took it.
	///
	/// **After the auto-bind, never instead of it.** Everything the brush never touched has to come
	/// from `BindBodies` as it always did, or a rebuild that moved geometry would leave the unpainted
	/// nine tenths of the model weighted for where it used to be.
	///
	/// A bone that has since been DELETED is dropped from the vertex and what is left renormalised —
	/// which is the only defensible answer, because the alternatives are a vertex whose weights do
	/// not sum to one and a hard failure over a bone the user removed on purpose. `Missing` names
	/// them so the tool can say so.
	/// </summary>
	public int Apply( PolyMesh mesh, SkinWeights weights, Skeleton skeleton, out List<string> missing )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( weights is null )
			throw new ArgumentNullException( nameof( weights ) );

		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		missing = new List<string>();

		if ( _painted.Count == 0 )
		{
			IsStale = false;
			return 0;
		}

		if ( !CanApply( mesh, out _ ) )
		{
			IsStale = true;
			return 0;
		}

		if ( weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );

		var index = new Dictionary<string, int>( StringComparer.Ordinal );

		for ( var i = 0; i < skeleton.Count; i++ )
			index[skeleton.Bones[i].Name] = i;

		var applied = 0;

		foreach ( var (vertex, named) in _painted )
		{
			if ( vertex < 0 || vertex >= weights.Count )
				continue;

			var resolved = new List<BoneWeight>( named.Count );

			foreach ( var (name, weight) in named )
			{
				if ( index.TryGetValue( name, out var bone ) )
					resolved.Add( new BoneWeight( bone, weight ) );
				else if ( !missing.Contains( name ) )
					missing.Add( name );
			}

			// Every bone this vertex was painted to is gone. Leaving it as the auto-bind made it is
			// better than leaving it bound to nothing, and it is what the user would get if they had
			// never painted it - which is exactly the state deleting those bones put them back in.
			if ( resolved.Count == 0 )
				continue;

			weights[vertex] = SkinWeights.Blend( new[] { (resolved.ToArray(), 1f) } );
			applied++;
		}

		IsStale = false;
		return applied;
	}

	/// <summary>Re-key the layer onto a mesh whose topology it now matches. Called after a rebuild
	/// that changed the topology and was then undone.</summary>
	public void Rebase( PolyMesh mesh )
	{
		Topology = MultiresSculpt.TopologyId( mesh );
		IsStale = false;
	}

	/// <summary>Follow a bone rename, so paint is not lost to a rename the rest of the project
	/// treats as an ordinary edit.</summary>
	public int RenameBone( string from, string to )
	{
		if ( string.IsNullOrEmpty( from ) || string.IsNullOrEmpty( to ) || from == to )
			return 0;

		var touched = 0;

		foreach ( var (_, named) in _painted )
		{
			for ( var i = 0; i < named.Count; i++ )
			{
				if ( named[i].Bone != from )
					continue;

				named[i] = (to, named[i].Weight);
				touched++;
			}
		}

		return touched;
	}

	public WeightPaintLayer Clone()
	{
		var copy = new WeightPaintLayer { Topology = Topology, IsStale = IsStale };

		foreach ( var (vertex, named) in _painted )
			copy._painted[vertex] = new List<(string, float)>( named );

		return copy;
	}

	public override string ToString() =>
		_painted.Count == 0 ? "no paint" : $"{_painted.Count} painted vertices{(IsStale ? ", stale" : "")}";
}
