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

	/// <summary>Features that built, but not from everything they were given.</summary>
	public List<(string FeatureId, string Message)> Warnings = new();

	public bool HasErrors => Errors.Count > 0;

	public bool HasWarnings => Warnings.Count > 0;

	public override string ToString() =>
		$"{FeaturesEvaluated} evaluated, {FeaturesReused} reused, {FeaturesSuppressed} suppressed"
		+ (HasErrors ? $", {Errors.Count} errors" : "")
		+ (HasWarnings ? $", {Warnings.Count} warnings" : "");
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

	/// <summary>
	/// What each material slot is called, for the slots someone has bothered to name.
	///
	/// Faces carry a slot NUMBER, which is all the geometry needs and all it should know. But a
	/// number is what the exporters were writing out too — material_0, material_3 — so binding a
	/// model in ModelDoc meant remembering which number meant what. A name travels with the file.
	///
	/// On the studio rather than on the mesh because a slot means the same thing across every body
	/// in the document: slot 2 is "rubber" everywhere or it is nothing.
	/// </summary>
	public Dictionary<int, string> MaterialNames = new();

	/// <summary>
	/// How many world units one repeat of a slot's material covers, for the slots somebody has
	/// resized. Absent means one-to-one — see <see cref="MaterialScale"/> for why that is the
	/// default and why the number lives on the slot rather than on the face.
	///
	/// Beside MaterialNames because it is the same kind of fact about the same thing: what the slot
	/// carries, and how big it is. A dictionary keyed the same way, saved on the same line-per-slot
	/// rule, and cleared by the same drop that retires a name.
	/// </summary>
	public Dictionary<int, Vec2> MaterialScales = new();

	/// <summary>The name for a slot, falling back to the numbered default. Pass this straight to any
	/// of the exporters.</summary>
	public string NameForSlot( int slot ) =>
		MaterialNames.TryGetValue( slot, out var name ) && !string.IsNullOrWhiteSpace( name )
			? name
			: ObjWriter.DefaultMaterialName( slot );

	/// <summary>
	/// Names given to a body, keyed by body id.
	///
	/// Bodies are remade every rebuild and otherwise take the name of the feature that made them.
	/// That is the right default and the wrong override: a pattern of eight cubes would all follow
	/// "Linear pattern 1", and renaming one in the Parts list would have nowhere to live. Body ids
	/// are stable (<c>{featureId}b{n}</c>), so a name keyed by id survives a rebuild the way
	/// MaterialNames survive one.
	/// </summary>
	public Dictionary<string, string> BodyNames = new();

	/// <summary>
	/// Bodies hidden from the viewport, keyed by id. Independent of Feature.Visible: hiding one
	/// copy of a pattern must not hide the rest, and hiding is not suppression.
	/// </summary>
	public HashSet<string> HiddenBodyIds = new();

	/// <summary>
	/// THE MODEL'S PIVOT, in the same coordinates every feature builds in.
	///
	/// Everything the kernel makes is built where the features put it; this says which point of
	/// that is (0,0,0) to whatever consumes the model. The writers subtract it on the way out, so a
	/// part built ten units up exports standing on zero with its origin under its feet rather than
	/// floating ten units above it. Nothing inside the rebuild reads it — moving the pivot cannot
	/// change what the features produce, only where the result is measured from, which is why
	/// dragging the handle does not dirty a single feature.
	///
	/// On the studio rather than on the viewport because it is a property of the MODEL and has to
	/// survive being saved: a pivot that is lost on reopen is not a pivot, it is a camera setting.
	/// </summary>
	public Vec3 Origin;

	/// <summary>
	/// Grease-pencil notes: things the modeller wrote to themselves, drawn over the part.
	///
	/// NOT IN <see cref="Features"/>, AND THAT IS THE DESIGN. A note has to be impossible to export.
	/// Hanging it off the feature list and teaching every writer to skip it would work until the
	/// next writer, which would be written by someone who had no reason to know the rule; hanging
	/// it here means <see cref="Rebuild"/> never evaluates one and <see cref="ToMesh"/> has nothing
	/// to filter, so there is no skip-the-notes branch anywhere that could be forgotten. Notes
	/// cannot reach the OBJ, the DMX or the compiled vmdl because nothing joins them to the mesh in
	/// the first place. NoteTests asserts it from the outside.
	///
	/// Document state, next to MaterialNames and HiddenBodyIds, for the same reason the pivot is:
	/// an annotation that is lost on reopen is not an annotation.
	/// </summary>
	public List<Note> Notes = new();

	/// <summary>
	/// The skeleton the part is rigged with, and which body hangs off which bone.
	///
	/// DOCUMENT STATE, for exactly the reason Notes above are: a rig that is lost on reopen is not
	/// a rig. It lived on the editor's rig PANEL until soft bones needed somewhere to be saved, and
	/// the panel's copy was cleared on every load and written to no file at all - place bones, save,
	/// reopen, and they were simply gone. Nothing said so, because nothing had ever been asked to.
	///
	/// NOT IN <see cref="Features"/>, same rule the notes follow. A rig is not a modelling
	/// operation: it does not build geometry, it does not participate in rollback, and putting it
	/// in the tree would mean every writer and every rebuild needed a skip-the-rig branch that
	/// somebody would eventually forget. Nothing joins it to the mesh except the exporters that ask
	/// for it by name.
	///
	/// The map is body id -> bone NAME rather than index, because SkinBinder.BindBodies takes it
	/// that way and because an index is not stable across a delete while a name is what a rename
	/// has to chase anyway.
	/// </summary>
	public Skeleton Rig = new();

	public Dictionary<string, string> BodyBoneMap = new();

	/// <summary>Result of the last rebuild.</summary>
	public List<Body> Bodies { get; private set; } = new();

	/// <summary>A snapshot of everything a feature can see, taken after each one runs.</summary>
	sealed class Snapshot
	{
		public List<Body> Bodies;
		public Dictionary<string, Sketch> Sketches;

		/// <summary>Carried like everything else a feature can see. Left out, an incremental
		/// rebuild that resumes from the cache would find the sketch but not what it is attached
		/// to, and the extrude above it would quietly start making its own body again.</summary>
		public Dictionary<string, string> SketchHostBodies;

		public static Snapshot Of( FeatureContext ctx ) => new()
		{
			Bodies = ctx.Bodies.Select( b => b.Clone() ).ToList(),
			Sketches = ctx.Sketches.ToDictionary( kv => kv.Key, kv => kv.Value.Clone() ),
			SketchHostBodies = new Dictionary<string, string>( ctx.SketchHostBodies )
		};

		public void RestoreInto( FeatureContext ctx )
		{
			ctx.Bodies = Bodies.Select( b => b.Clone() ).ToList();
			ctx.Sketches = Sketches.ToDictionary( kv => kv.Key, kv => kv.Value.Clone() );
			ctx.SketchHostBodies = new Dictionary<string, string>( SketchHostBodies );
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

		// A feature holding mutable state of its own gets to veto its own cache entry — see
		// Feature.IsStale. Checked before anything is restored, so the snapshot is never read at all.
		for ( var i = 0; i < reusableUpTo; i++ )
		{
			if ( !Features[i].IsStale )
				continue;

			reusableUpTo = i;
			break;
		}

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

		// A REUSED FEATURE'S ERROR IS STILL AN ERROR. Features before the dirty point are not re-run,
		// so they never re-report - and the report came back clean while an upstream feature was
		// still broken, purely because the edit happened downstream of it. Carrying the errors
		// forward keeps HasErrors meaning "this model has something wrong with it" rather than
		// "something went wrong during this particular rebuild".
		for ( var i = 0; i < reusableUpTo; i++ )
		{
			if ( Features[i].Error is not null )
				report.Errors.Add( (Features[i].Id, Features[i].Error) );

			if ( Features[i].Warning is not null )
				report.Warnings.Add( (Features[i].Id, Features[i].Warning) );
		}

		for ( var i = reusableUpTo; i < count; i++ )
		{
			var feature = Features[i];

			// Bodies added by this feature inherit its visibility. Doing it here rather than in
			// every feature's Execute means a new feature type gets it for free and cannot forget.
			var bodiesBefore = ctx.Bodies.Count;

			feature.Run( ctx );

			for ( var b = bodiesBefore; b < ctx.Bodies.Count; b++ )
			{
				ctx.Bodies[b].Visible = feature.Visible;
				ctx.Bodies[b].FeatureId = feature.Id;
			}

			if ( feature.Suppressed )
				report.FeaturesSuppressed++;
			else
				report.FeaturesEvaluated++;

			if ( feature.Error is not null )
				report.Errors.Add( (feature.Id, feature.Error) );

			if ( feature.Warning is not null )
				report.Warnings.Add( (feature.Id, feature.Warning) );

			_cache.Add( Snapshot.Of( ctx ) );
		}

		// Features past the rollback bar are neither evaluated nor errors; just note them.
		for ( var i = count; i < Features.Count; i++ )
		{
			Features[i].Error = null;
			Features[i].Warning = null;
			Features[i].Diagnostic = null;
		}

		Bodies = ctx.Bodies;
		ApplyBodyPresentation();

		// AFTER the feature loop, for the same reason ApplyBodyPresentation is: the features produce
		// the UVs this divides, so running it any earlier would hand already-scaled UVs to the
		// feature above and scale them twice. Safe to run on a rebuild that re-evaluated nothing,
		// because the cache holds clones taken inside the loop and ctx.Bodies was cloned back out of
		// it — what this touches is never what the next rebuild reads.
		MaterialScale.Apply( this );

		_dirtyFrom = count;

		return report;
	}

	/// <summary>
	/// Put Parts-list names and hide flags onto the bodies this rebuild just produced.
	///
	/// MUST RUN AFTER the feature loop. Each feature writes Body.Visible from Feature.Visible and
	/// Body.Name from Feature.Name as it goes, which is the right default and would wipe a rename
	/// or a hide if this ran first. Incremental rebuilds restore those same defaults from the
	/// cache, so this also has to run when nothing was re-evaluated.
	/// </summary>
	void ApplyBodyPresentation()
	{
		foreach ( var body in Bodies )
		{
			if ( BodyNames.TryGetValue( body.Id, out var name ) && !string.IsNullOrWhiteSpace( name ) )
				body.Name = name;

			if ( HiddenBodyIds.Contains( body.Id ) )
				body.Visible = false;
		}
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
	/// <summary>Every body, hidden or not. Export takes this: hiding a part is a working
	/// convenience, not a statement that it should leave the model.</summary>
	public PolyMesh ToMesh()
	{
		var merged = new PolyMesh();

		foreach ( var b in Bodies )
			MeshTransform.Append( merged, b.Mesh );

		return merged;
	}

	/// <summary>Only what is being drawn. The viewport preview takes this.</summary>
	public PolyMesh ToVisibleMesh()
	{
		var merged = new PolyMesh();

		foreach ( var b in Bodies )
		{
			if ( b.Visible )
				MeshTransform.Append( merged, b.Mesh );
		}

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

	/// <summary>
	/// Which SketchFeature a sketch-consuming feature actually builds from, resolving the empty
	/// "most recent one" case the same way SketchConsumingFeature.ResolveSketch does at rebuild
	/// time - the nearest running SketchFeature ABOVE it in the tree. Null when there is none.
	/// </summary>
	public string ResolveSketchFeatureId( SketchConsumingFeature consumer )
	{
		if ( consumer is null )
			return null;

		// A feature waiting to be pointed at a sketch resolves to NOTHING rather than to the most
		// recent one: it consumes nothing yet, so the sketch it might end up using has to stay visible
		// and pickable - which is how you point at it in the first place. See
		// SketchConsumingFeature.AwaitingPick.
		if ( consumer.IsAwaitingPick )
			return null;

		if ( !string.IsNullOrEmpty( consumer.SketchFeatureId ) )
			return consumer.SketchFeatureId;

		var index = Features.IndexOf( consumer );

		if ( index < 0 )
			index = Features.Count;

		for ( var i = Math.Min( index, EffectiveCount ) - 1; i >= 0; i-- )
		{
			if ( Features[i] is SketchFeature sketch && !sketch.Suppressed )
				return sketch.Id;
		}

		return null;
	}

	/// <summary>
	/// The sketches some later feature has already turned into geometry.
	///
	/// Onshape hides these as soon as they are consumed, and it is not decoration: a sketch left
	/// on screen sits in exactly the same place as the solid built from it, so the part reads as a
	/// wireframe shell rather than as a finished body. Rolled-back and suppressed features are
	/// excluded - a feature that is not running has not consumed anything.
	/// </summary>
	public HashSet<string> ConsumedSketchIds()
	{
		var consumed = new HashSet<string>();

		for ( var i = 0; i < EffectiveCount; i++ )
		{
			if ( Features[i].Suppressed || Features[i] is not SketchConsumingFeature consumer )
				continue;

			if ( ResolveSketchFeatureId( consumer ) is { Length: > 0 } id )
				consumed.Add( id );
		}

		return consumed;
	}

	public int TotalFaceCount => Bodies.Sum( b => b.Mesh.FaceCount );
	public int TotalVertexCount => Bodies.Sum( b => b.Mesh.VertexCount );
}
