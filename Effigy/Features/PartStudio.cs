using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>What a rebuild did. Returned rather than logged, so a UI can show it and a test can
/// assert on it.</summary>
public sealed class RebuildReport
{
	public int FeaturesEvaluated;
	public int FeaturesReused;
	public int FeaturesSuppressed;
	public List<(string FeatureId, string Message)> Errors = new();

	public bool HasErrors => Errors.Count > 0;

	public override string ToString() =>
		$"{FeaturesEvaluated} evaluated, {FeaturesReused} reused, {FeaturesSuppressed} suppressed"
		+ (HasErrors ? $", {Errors.Count} errors" : "");
}

/// <summary>
/// An ordered feature history that rebuilds into a set of bodies. Onshape's Part Studio.
///
/// TWO THINGS MAKE THIS PARAMETRIC RATHER THAN A PILE OF BAKES:
///
///   Rollback — evaluate only the first N features, so you can go back and work as the model was
///   at that point. RollbackIndex is the bar Onshape draws between two features.
///
///   Incremental rebuild — the body list after each feature is cached, so editing feature 7 of 20
///   re-runs 7 onward and reuses the snapshot from 6. Without this every parameter drag re-runs
///   the whole tree, and the tool stops feeling live at about a dozen features.
///
/// The cache holds deep clones, which costs memory proportional to tree length times model size.
/// That is the obvious thing to optimise later — snapshot only at intervals, or make features
/// declare what they read — but not before it actually hurts, because the correctness of "editing
/// upstream cannot be affected by downstream state" is worth more than the memory.
/// </summary>
public sealed class PartStudio
{
	public List<Feature> Features = new();

	/// <summary>How many features to evaluate. int.MaxValue means all of them; anything less is
	/// the rollback bar sitting above feature RollbackIndex.</summary>
	public int RollbackIndex = int.MaxValue;

	/// <summary>Result of the last rebuild.</summary>
	public List<Body> Bodies { get; private set; } = new();

	/// <summary>A snapshot of everything a feature can see, taken after each one runs.</summary>
	sealed class Snapshot
	{
		public List<Body> Bodies;
		public Dictionary<string, Sketch> Sketches;

		public static Snapshot Of( FeatureContext ctx ) => new()
		{
			Bodies = ctx.Bodies.Select( b => b.Clone() ).ToList(),
			Sketches = ctx.Sketches.ToDictionary( kv => kv.Key, kv => kv.Value.Clone() )
		};

		public void RestoreInto( FeatureContext ctx )
		{
			ctx.Bodies = Bodies.Select( b => b.Clone() ).ToList();
			ctx.Sketches = Sketches.ToDictionary( kv => kv.Key, kv => kv.Value.Clone() );
		}
	}

	// _cache[i] is the state AFTER feature i ran.
	readonly List<Snapshot> _cache = new();
	int _dirtyFrom;

	public PartStudio()
	{
		_dirtyFrom = 0;
	}

	public int EffectiveCount => Math.Min( RollbackIndex, Features.Count );

	// --- editing ---------------------------------------------------------------------------

	public T Add<T>( T feature ) where T : Feature
	{
		feature.Name ??= DefaultName( feature );
		Features.Add( feature );
		MarkDirty( Features.Count - 1 );
		return feature;
	}

	public T Insert<T>( int index, T feature ) where T : Feature
	{
		feature.Name ??= DefaultName( feature );
		Features.Insert( index, feature );
		MarkDirty( index );
		return feature;
	}

	public void Remove( Feature feature )
	{
		var index = Features.IndexOf( feature );

		if ( index < 0 )
			return;

		Features.RemoveAt( index );
		MarkDirty( index );
	}

	public void Move( int from, int to )
	{
		if ( from == to || from < 0 || from >= Features.Count || to < 0 || to >= Features.Count )
			return;

		var f = Features[from];
		Features.RemoveAt( from );
		Features.Insert( to, f );

		// Everything from the earlier of the two positions is now standing on different geometry.
		MarkDirty( Math.Min( from, to ) );
	}

	/// <summary>Call after changing any parameter on a feature. Everything from here down is
	/// standing on geometry that may have changed.</summary>
	public void MarkDirty( int index )
	{
		_dirtyFrom = Math.Min( _dirtyFrom, Math.Max( 0, index ) );
	}

	public void MarkDirty( Feature feature )
	{
		var index = Features.IndexOf( feature );

		if ( index >= 0 )
			MarkDirty( index );
	}

	public void MarkAllDirty() => _dirtyFrom = 0;

	/// <summary>Unique name in the style Onshape uses — "Box 1", "Box 2", "Linear pattern 1".</summary>
	string DefaultName( Feature feature )
	{
		var n = 1;

		while ( Features.Any( f => f.Name == $"{feature.TypeName} {n}" ) )
			n++;

		return $"{feature.TypeName} {n}";
	}

	// --- rebuild ---------------------------------------------------------------------------

	public RebuildReport Rebuild()
	{
		var report = new RebuildReport();
		var count = EffectiveCount;

		// The cache can only be trusted up to the first dirty feature, and only as far as it was
		// filled last time.
		var reusableUpTo = Math.Min( _dirtyFrom, _cache.Count );
		reusableUpTo = Math.Min( reusableUpTo, count );

		var ctx = new FeatureContext();

		if ( reusableUpTo > 0 )
		{
			// Clone out of the cache rather than handing the cached state to the features, or the
			// next rebuild reuses a snapshot that a feature has since mutated.
			_cache[reusableUpTo - 1].RestoreInto( ctx );
			report.FeaturesReused = reusableUpTo;
		}

		// Keep body ids climbing past anything already present, so a rebuild starting mid-tree
		// does not reissue an id a cached body already holds.
		ctx.SeedIdCounter( HighestBodyNumber( ctx.Bodies ) + 1 );

		if ( _cache.Count > reusableUpTo )
			_cache.RemoveRange( reusableUpTo, _cache.Count - reusableUpTo );

		for ( var i = reusableUpTo; i < count; i++ )
		{
			var feature = Features[i];
			feature.Run( ctx );

			if ( feature.Suppressed )
				report.FeaturesSuppressed++;
			else
				report.FeaturesEvaluated++;

			if ( feature.Error is not null )
				report.Errors.Add( (feature.Id, feature.Error) );

			_cache.Add( Snapshot.Of( ctx ) );
		}

		// Features past the rollback bar are neither evaluated nor errors; just note them.
		for ( var i = count; i < Features.Count; i++ )
			Features[i].Error = null;

		Bodies = ctx.Bodies;
		_dirtyFrom = count;

		return report;
	}

	static int HighestBodyNumber( IEnumerable<Body> bodies )
	{
		var highest = 0;

		foreach ( var b in bodies )
		{
			if ( b.Id.StartsWith( "body" ) && int.TryParse( b.Id[4..], out var n ) )
				highest = Math.Max( highest, n );
		}

		return highest;
	}

	/// <summary>Every body merged into one mesh, which is what export wants.</summary>
	public PolyMesh ToMesh()
	{
		var merged = new PolyMesh();

		foreach ( var b in Bodies )
			MeshTransform.Append( merged, b.Mesh );

		return merged;
	}

	/// <summary>
	/// The same merge as ToMesh, but recording which run of vertices came from which body.
	///
	/// This is what makes feature-bound rigging possible. Vertex indices are meaningless across a
	/// rebuild — change one number upstream and every index after it moves — but body ids are
	/// stable, so a rig stored as "body3 is the forearm" can be reapplied to the new geometry
	/// instead of being invalidated by it. See SkinBinder.BindBodies.
	/// </summary>
	public (PolyMesh Mesh, List<BodyRange> Ranges) ToMeshWithBodies()
	{
		var merged = new PolyMesh();
		var ranges = new List<BodyRange>( Bodies.Count );

		foreach ( var b in Bodies )
		{
			var start = merged.VertexCount;
			MeshTransform.Append( merged, b.Mesh );
			ranges.Add( new BodyRange( b.Id, b.Name, start, merged.VertexCount - start ) );
		}

		return (merged, ranges);
	}

	public int TotalFaceCount => Bodies.Sum( b => b.Mesh.FaceCount );
	public int TotalVertexCount => Bodies.Sum( b => b.Mesh.VertexCount );
}
