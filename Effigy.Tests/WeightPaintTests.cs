using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Weight painting: the brush, the invariant it must not break, and the layer that makes paint
/// survive a rebuild.
///
/// THE INVARIANT IS THE THING UNDER TEST, not the brush. Every vertex's influences are non-negative
/// and sum to one, and everything downstream leans on it — `Prune` renormalises against it,
/// Catmull-Clark's affine combinations preserve it, the compiler's own culling assumes it. A brush
/// that breaks it produces a model that loads, renders, and deforms wrongly in a way nobody can
/// point at. So the sum is checked after every operation here, including the ones expected to fail.
///
/// Second thing under test, and the one a naive implementation gets wrong in the other direction: a
/// vertex weighted ENTIRELY to the bone being subtracted from has nowhere to put the weight, and
/// both tempting answers are bad. Normalising an all-zero set binds the vertex to nothing, which
/// collapses it to the model origin on export. Leaving 1.0 silently makes the brush look broken. It
/// is refused and counted instead.
/// </summary>
public static class WeightPaintTests
{
	public static void Run()
	{
		Section( "weight paint: the partition of unity survives every brush" );
		TestSumStaysOne();

		Section( "weight paint: what the brush does, and to whom" );
		TestAddTakesFromTheOthers();
		TestSubtractGivesItBack();
		TestSetEasesRatherThanSnapping();
		TestSmoothPullsTowardTheNeighbours();
		TestFalloffMeansTheEdgeIsSofter();

		Section( "weight paint: the vertex that cannot move, and says so" );
		TestASingleInfluenceVertexIsRefusedNotMangled();

		Section( "weight paint: a stroke is one undo entry" );
		TestStrokeUndoRedo();
		TestUndoTakesTheLayerBackToo();

		Section( "weight paint: paint survives a rebuild, and refuses one it cannot" );
		TestPaintSurvivesAMovedCage();
		TestPaintIsKeptRatherThanMisappliedOnATopologyChange();
		TestBonesAreStoredByNameNotIndex();
		TestADeletedBoneIsDroppedAndNamed();
	}

	/// <summary>
	/// Every brush, over every vertex, and the one number that has to hold afterwards. Run against a
	/// mesh whose vertices carry several influences each, because a single-influence mesh cannot
	/// break the invariant however hard it is painted.
	/// </summary>
	static void TestSumStaysOne()
	{
		foreach ( var kind in new[]
		{
			WeightBrushKind.Add, WeightBrushKind.Subtract, WeightBrushKind.Set, WeightBrushKind.Smooth
		} )
		{
			var (mesh, weights, _) = Rigged();

			var stroke = new WeightStroke { Kind = kind, Bone = 1, Target = 0.6f, MirrorX = true };

			// Several dabs, deliberately overlapping, because a brush that renormalises correctly
			// once can still drift when applied on top of its own output.
			for ( var i = 0; i < 12; i++ )
				stroke.Samples.Add( new WeightSample( new Vec3( -1f + i * 0.2f, 0, 0.5f ), 0.9f, 0.4f ) );

			WeightBrush.Apply( mesh, weights, stroke );

			var worst = 0f;
			var negative = 0;

			for ( var v = 0; v < mesh.VertexCount; v++ )
			{
				var sum = 0f;

				foreach ( var w in weights[v] )
				{
					sum += w.Weight;

					if ( w.Weight < 0f )
						negative++;
				}

				worst = MathF.Max( worst, MathF.Abs( sum - 1f ) );
			}

			Check( $"{kind}: every vertex still sums to 1", worst < 1e-4f, $"worst drift {worst:0.#######}" );
			Check( $"{kind}: and no influence went negative", negative == 0, $"{negative} negative" );
		}
	}

	static void TestAddTakesFromTheOthers()
	{
		// Two bones at a quarter each and a third at a half. Raising bone 0 must take from the other
		// two IN PROPORTION - a brush that took it all from the largest, or split it evenly, would
		// pass a sum check and change the shape of the blend.
		var before = new[]
		{
			new BoneWeight( 0, 0.25f ), new BoneWeight( 1, 0.25f ), new BoneWeight( 2, 0.5f )
		};

		var after = WeightBrush.Retarget( before, 0, 0.5f );

		Check( "the painted bone lands on its target", Near( Of( after, 0 ), 0.5f ), $"{Of( after, 0 ):0.####}" );
		Check( "and the others keep their ratio to each other",
			Near( Of( after, 2 ) / Of( after, 1 ), 2f ),
			$"{Of( after, 1 ):0.####} and {Of( after, 2 ):0.####}" );
		Check( "summing to what is left", Near( Of( after, 1 ) + Of( after, 2 ), 0.5f ) );

		// A bone the vertex does not have yet is introduced rather than ignored - the common case
		// when painting a bone onto a region the auto-binder never gave it.
		var fresh = WeightBrush.Retarget( new[] { new BoneWeight( 3, 1f ) }, 7, 0.4f );

		Check( "a bone not on the vertex yet is added", Near( Of( fresh, 7 ), 0.4f ), $"{Of( fresh, 7 ):0.####}" );
		Check( "and the one that was there makes room", Near( Of( fresh, 3 ), 0.6f ), $"{Of( fresh, 3 ):0.####}" );
	}

	static void TestSubtractGivesItBack()
	{
		var before = new[] { new BoneWeight( 0, 0.8f ), new BoneWeight( 1, 0.1f ), new BoneWeight( 2, 0.1f ) };
		var after = WeightBrush.Retarget( before, 0, 0.4f );

		Check( "the painted bone comes down", Near( Of( after, 0 ), 0.4f ), $"{Of( after, 0 ):0.####}" );
		Check( "and the others share what it gave up, in proportion",
			Near( Of( after, 1 ), 0.3f ) && Near( Of( after, 2 ), 0.3f ),
			$"{Of( after, 1 ):0.####} and {Of( after, 2 ):0.####}" );

		// TAKEN TO ZERO, THE BONE LEAVES. A zero influence is an export slot spent on nothing, and
		// Prune would rather have the room - four is the whole budget.
		var gone = WeightBrush.Retarget( before, 0, 0f );

		Check( "a bone taken to zero is removed rather than left at 0",
			gone.All( w => w.Bone != 0 ), string.Join( ", ", gone.Select( w => w.ToString() ) ) );
		Check( "and what is left still sums to 1", Near( gone.Sum( w => w.Weight ), 1f ) );
	}

	static void TestSetEasesRatherThanSnapping()
	{
		var (mesh, weights, _) = Rigged();

		// One dab, dead centre of the mesh, at a strength well below 1. Set that SNAPPED would put
		// every vertex in the disc on the target regardless of how far out it was, which is a
		// hard-edged patch of weight that no amount of falloff softens.
		var stroke = new WeightStroke { Kind = WeightBrushKind.Set, Bone = 1, Target = 1f };
		stroke.Samples.Add( new WeightSample( Vec3.Zero, 4f, 0.5f ) );

		WeightBrush.Apply( mesh, weights, stroke );

		var values = new List<(float Distance, float Weight)>();

		for ( var v = 0; v < mesh.VertexCount; v++ )
			values.Add( ((mesh.Positions[v] - Vec3.Zero).Length, Of( weights[v], 1 )) );

		var near = values.OrderBy( t => t.Distance ).First();
		var far = values.OrderByDescending( t => t.Distance ).First();

		Check( "Set moves the nearest vertex further than the furthest one",
			near.Weight > far.Weight + 1e-3f, $"{near.Weight:0.###} near, {far.Weight:0.###} far" );
		Check( "and does not reach the target in one dab at half strength",
			near.Weight < 1f - 1e-3f, $"{near.Weight:0.####}" );
	}

	static void TestSmoothPullsTowardTheNeighbours()
	{
		var (mesh, weights, _) = Rigged();

		// A deliberate discontinuity: one vertex given entirely to a bone none of its neighbours
		// carry. That is exactly what an auto-bind mistake looks like on a model, and what smoothing
		// is for.
		var spike = 0;
		weights[spike] = new[] { new BoneWeight( 2, 1f ) };

		var neighbourCarry = Neighbours( mesh, spike ).Average( n => Of( weights[n], 2 ) );

		var stroke = new WeightStroke { Kind = WeightBrushKind.Smooth };
		stroke.Samples.Add( new WeightSample( mesh.Positions[spike], 1.5f, 1f ) );

		WeightBrush.Apply( mesh, weights, stroke );

		var now = Of( weights[spike], 2 );

		Check( "smoothing pulls a spike down toward its neighbours",
			now < 1f - 1e-3f && now > neighbourCarry - 1e-3f,
			$"{now:0.####}, neighbours average {neighbourCarry:0.####}" );
		Check( "and the vertex still sums to 1", Near( weights[spike].Sum( w => w.Weight ), 1f ) );
	}

	static void TestFalloffMeansTheEdgeIsSofter()
	{
		// MEASURED AS THE CHANGE, NOT THE VALUE, and that distinction is the test getting itself
		// right rather than a nicety: Add targets `current + amount`, so two vertices given the same
		// amount still end on different values when they started on different ones. Comparing final
		// weights would call a correct constant falloff uneven.
		//
		// Constant falloff is the control - with it, distance inside the brush stops mattering. Any
		// other kind must move the near vertex further than the far one, or the falloff is not
		// reaching the weight maths at all.
		float Spread( BrushFalloff falloff )
		{
			var (mesh, weights, _) = Rigged();
			var before = Snapshot( weights );

			var stroke = new WeightStroke { Kind = WeightBrushKind.Add, Bone = 1, Falloff = falloff };
			stroke.Samples.Add( new WeightSample( Vec3.Zero, 4f, 0.4f ) );

			WeightBrush.Apply( mesh, weights, stroke );

			var ordered = Enumerable.Range( 0, mesh.VertexCount )
				.OrderBy( v => (mesh.Positions[v] - Vec3.Zero).Length )
				.ToList();

			var near = Of( weights[ordered[0]], 1 ) - Of( before[ordered[0]], 1 );
			var far = Of( weights[ordered[^1]], 1 ) - Of( before[ordered[^1]], 1 );

			return near - far;
		}

		Check( "a smooth falloff moves the near vertex more than the far one", Spread( BrushFalloff.Smooth ) > 1e-3f,
			$"{Spread( BrushFalloff.Smooth ):0.####}" );
		Check( "and a constant one moves them by the same amount", MathF.Abs( Spread( BrushFalloff.Constant ) ) < 1e-4f,
			$"{Spread( BrushFalloff.Constant ):0.#######}" );
	}

	/// <summary>
	/// The case with no answer. See this file's own comment for why both alternatives to refusing
	/// are worse than refusing.
	/// </summary>
	static void TestASingleInfluenceVertexIsRefusedNotMangled()
	{
		var only = new[] { new BoneWeight( 4, 1f ) };

		Check( "a vertex with one influence cannot have it lowered",
			WeightBrush.Retarget( only, 4, 0.5f ) is null );
		Check( "nor taken to zero", WeightBrush.Retarget( only, 4, 0f ) is null );
		Check( "but painting a DIFFERENT bone onto it works, which is the way out",
			WeightBrush.Retarget( only, 9, 0.5f ) is { Length: 2 } );

		// End to end: a rigidly bound mesh is entirely single-influence, so subtracting from it must
		// change nothing at all and must SAY how much it could not change.
		var mesh = Primitives.Box( 2, 2, 2 );
		var weights = SkinWeights.AllTo( mesh.VertexCount, 0 );

		var stroke = new WeightStroke { Kind = WeightBrushKind.Subtract, Bone = 0 };
		stroke.Samples.Add( new WeightSample( Vec3.Zero, 10f, 1f ) );

		var undo = WeightBrush.Apply( mesh, weights, stroke );

		Check( "a rigidly bound mesh is left completely alone", undo.Count == 0, $"{undo.Count} changed" );
		Check( "and every vertex still weighs 1 to its bone",
			Enumerable.Range( 0, mesh.VertexCount ).All( v => Near( Of( weights[v], 0 ), 1f ) ) );
		Check( "and the tool can say how many vertices it could not move",
			WeightBrush.CountLocked( mesh, weights, stroke ) == mesh.VertexCount,
			$"{WeightBrush.CountLocked( mesh, weights, stroke )} of {mesh.VertexCount}" );
	}

	static void TestStrokeUndoRedo()
	{
		var (mesh, weights, skeleton) = Rigged();
		var session = new WeightPaintSession( mesh, weights, skeleton ) { Bone = 1, Radius = 1.5f, Strength = 0.5f };

		var before = Snapshot( weights );

		// Down the +z axis at the top face, which the box's own vertices are under.
		Check( "the stroke lands on the model",
			session.BeginStroke( new Vec3( 0, 0, 10 ), new Vec3( 0, 0, -1 ) ) );

		session.MoveTo( new Vec3( 1.2f, 0, 10 ), new Vec3( 0, 0, -1 ) );

		var edit = session.EndStroke();

		Check( "and commits as one edit", edit is { Count: > 0 }, $"{edit?.Count ?? -1} vertices" );
		Check( "which changed the weights", !SameAs( weights, before ) );

		Check( "undo puts them back exactly", session.Undo() && SameAs( weights, before ) );
		Check( "redo puts them forward again", session.Redo() && !SameAs( weights, before ) );

		// A whole gesture is ONE entry - a user does not mean "one dab" by undo, and MoveTo above
		// produced several.
		Check( "and the whole gesture was one undo entry", session.Undo() && !session.CanUndo );
	}

	/// <summary>
	/// The failure that would be invisible for several actions and then blamed on the rebuild:
	/// undoing the weights while leaving the paint recorded.
	/// </summary>
	static void TestUndoTakesTheLayerBackToo()
	{
		var (mesh, weights, skeleton) = Rigged();
		var auto = Snapshot( weights );
		var session = new WeightPaintSession( mesh, weights, skeleton ) { Bone = 1, Radius = 1.5f, Strength = 0.6f };

		session.BeginStroke( new Vec3( 0, 0, 10 ), new Vec3( 0, 0, -1 ) );
		session.EndStroke();

		Check( "the stroke was recorded as paint", session.Layer.Count > 0, session.Layer.ToString() );

		session.Undo();

		// Re-applying the layer over the auto weights has to produce the auto weights, or the undone
		// stroke comes back the next time anything rebuilds.
		var rebound = Restore( auto );

		session.Layer.Apply( mesh, rebound, skeleton, out _ );

		Check( "and undoing it means a rebuild does not bring it back", SameAs( rebound, auto ),
			"the layer still carries the undone stroke" );
	}

	/// <summary>
	/// The whole point of the layer. A parametric edit moves the cage's positions and keeps its
	/// topology — the overwhelmingly common case — and the paint has to ride it.
	/// </summary>
	static void TestPaintSurvivesAMovedCage()
	{
		var (mesh, weights, skeleton) = Rigged();
		var session = new WeightPaintSession( mesh, weights, skeleton ) { Bone = 1, Radius = 1.5f, Strength = 0.7f };

		session.BeginStroke( new Vec3( 0, 0, 10 ), new Vec3( 0, 0, -1 ) );
		session.EndStroke();

		var painted = Snapshot( weights );

		// The rebuild: same topology, different positions, and freshly auto-bound weights that know
		// nothing about the paint.
		var rebuilt = mesh.Clone();

		for ( var v = 0; v < rebuilt.VertexCount; v++ )
			rebuilt.Positions[v] = new Vec3( rebuilt.Positions[v].x, rebuilt.Positions[v].y, rebuilt.Positions[v].z * 1.2f );

		var auto = SkinBinder.BindSmooth( rebuilt, skeleton );

		Check( "the layer accepts a cage that only moved", session.Layer.CanApply( rebuilt, out var why ), why );

		var applied = session.Layer.Apply( rebuilt, auto, skeleton, out var missing );

		Check( "and puts every painted vertex back", applied == session.Layer.Count,
			$"{applied} of {session.Layer.Count}" );
		Check( "with nothing missing", missing.Count == 0, string.Join( ", ", missing ) );

		var matched = 0;

		foreach ( var (vertex, _) in session.Layer.Painted )
		{
			if ( SameWeights( auto[vertex], painted[vertex] ) )
				matched++;
		}

		Check( "and the weights are the painted ones, not the binder's", matched == session.Layer.Count,
			$"{matched} of {session.Layer.Count}" );
	}

	static void TestPaintIsKeptRatherThanMisappliedOnATopologyChange()
	{
		var (mesh, weights, skeleton) = Rigged();
		var session = new WeightPaintSession( mesh, weights, skeleton ) { Bone = 1, Radius = 1.5f, Strength = 0.7f };

		session.BeginStroke( new Vec3( 0, 0, 10 ), new Vec3( 0, 0, -1 ) );
		session.EndStroke();

		var count = session.Layer.Count;
		var subdivided = CatmullClark.Subdivide( mesh, 1 );
		var auto = SkinBinder.BindSmooth( subdivided, skeleton );
		var untouched = Snapshot( auto );

		Check( "a topology change is refused", !session.Layer.CanApply( subdivided, out var why ) );
		Check( "and the refusal names both models' numbers",
			why is not null && why.Contains( subdivided.VertexCount.ToString() ) && why.Contains( count.ToString() ),
			why );

		var applied = session.Layer.Apply( subdivided, auto, skeleton, out _ );

		Check( "nothing is applied", applied == 0, $"{applied}" );
		Check( "the auto weights are left exactly as the binder made them", SameAs( auto, untouched ) );

		// KEPT, NOT DISCARDED. The topology change may well be undone - a fillet radius pushed too
		// far and pulled back - and losing somebody's paint to that would be unforgivable.
		Check( "the paint is kept", session.Layer.Count == count, $"{session.Layer.Count} of {count}" );
		Check( "and marked stale so the tool can say so", session.Layer.IsStale );

		session.Layer.Rebase( mesh );

		Check( "and rebasing onto the original mesh makes it usable again",
			!session.Layer.IsStale && session.Layer.CanApply( mesh, out _ ) );
	}

	/// <summary>
	/// Bone indices are not stable — `Skeleton.RemoveBone` re-indexes everything after the hole — so
	/// a layer holding indices would silently move to a different bone. Names are what this project
	/// keys rigs by everywhere else.
	/// </summary>
	static void TestBonesAreStoredByNameNotIndex()
	{
		var (mesh, weights, skeleton) = Rigged();
		var session = new WeightPaintSession( mesh, weights, skeleton ) { Bone = 2, Radius = 1.5f, Strength = 0.8f };

		session.BeginStroke( new Vec3( 0, 0, 10 ), new Vec3( 0, 0, -1 ) );
		session.EndStroke();

		var vertex = session.Layer.Painted.First().Vertex;
		var paintedSet = session.Layer.Painted.First( p => p.Vertex == vertex ).Weights;
		var paintedOnTip = Of( weights[vertex], 2 );

		Check( "the stroke put weight on the bone it was pointed at", paintedOnTip > 1e-3f,
			$"{paintedOnTip:0.####}" );

		// DELETE A BONE ABOVE THE PAINTED ONE, which is the case indices cannot survive: removing
		// index 1 from a three-bone skeleton leaves two, so a layer holding index 2 would be out of
		// range and the paint would vanish entirely. Holding the name, it is found at its new index.
		var trimmed = skeleton.Clone();
		trimmed.RemoveBone( 1 );

		var auto = SkinBinder.BindSmooth( mesh, trimmed );

		session.Layer.Apply( mesh, auto, trimmed, out var missing );

		var now = trimmed.IndexOf( skeleton.Bones[2].Name );

		// The painted share, renormalised over the bones that are still there - which is what the
		// layer documents itself as doing when a bone the paint referred to is gone.
		var survived = paintedSet.Where( w => trimmed.IndexOf( w.Bone ) >= 0 ).Sum( w => w.Weight );
		var expected = paintedOnTip / survived;

		Check( "after a bone above it is deleted, the paint follows the NAME to its new index",
			now >= 0 && Near( Of( auto[vertex], now ), expected ),
			$"'{skeleton.Bones[2].Name}' is now index {now}, carrying {Of( auto[vertex], now ):0.####}, expected {expected:0.####}" );
		Check( "and the deleted bone is reported rather than silently dropped",
			missing.Contains( skeleton.Bones[1].Name ), string.Join( ", ", missing ) );

		// A rename is an ordinary edit elsewhere in this project, so paint follows it too.
		var renamed = skeleton.Clone();
		renamed.RenameBone( 2, "tip_renamed" );

		Check( "and a rename can be followed", session.Layer.RenameBone( skeleton.Bones[2].Name, "tip_renamed" ) > 0 );

		var afterRename = SkinBinder.BindSmooth( mesh, renamed );
		session.Layer.Apply( mesh, afterRename, renamed, out var stillMissing );

		Check( "leaving nothing missing", stillMissing.Count == 0, string.Join( ", ", stillMissing ) );
	}

	static void TestADeletedBoneIsDroppedAndNamed()
	{
		var (mesh, weights, skeleton) = Rigged();
		var session = new WeightPaintSession( mesh, weights, skeleton ) { Bone = 2, Radius = 4f, Strength = 0.5f };

		session.BeginStroke( new Vec3( 0, 0, 10 ), new Vec3( 0, 0, -1 ) );
		session.EndStroke();

		var trimmed = skeleton.Clone();
		trimmed.RemoveBone( 2 );

		var auto = SkinBinder.BindSmooth( mesh, trimmed );

		session.Layer.Apply( mesh, auto, trimmed, out var missing );

		Check( "a bone the paint referred to and that is gone is named",
			missing.Count == 1 && missing[0] == skeleton.Bones[2].Name, string.Join( ", ", missing ) );

		// The remaining influences are renormalised rather than left summing to less than one, which
		// is the only defensible answer - the alternative is a partially-bound vertex.
		var worst = 0f;

		for ( var v = 0; v < mesh.VertexCount; v++ )
			worst = MathF.Max( worst, MathF.Abs( auto[v].Sum( w => w.Weight ) - 1f ) );

		Check( "and every vertex still sums to 1 afterwards", worst < 1e-4f, $"worst drift {worst:0.#######}" );
	}

	// --- fixtures ---------------------------------------------------------------------------------

	/// <summary>
	/// A subdivided box with a three-bone chain up its z axis, smooth-bound. Subdivided on purpose:
	/// smooth binding on a plain box gives nearly every vertex one influence, and a mesh that cannot
	/// break the invariant cannot test it either.
	/// </summary>
	static (PolyMesh Mesh, SkinWeights Weights, Skeleton Skeleton) Rigged()
	{
		var mesh = CatmullClark.Subdivide( Primitives.Box( 2, 2, 4 ), 2 );

		var skeleton = new Skeleton();
		var root = skeleton.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -2 ), new Vec3( 0, 0, -0.5f ) );
		var mid = skeleton.AddBoneFromPoints( "mid", root, new Vec3( 0, 0, -0.5f ), new Vec3( 0, 0, 1f ) );
		skeleton.AddBoneFromPoints( "tip", mid, new Vec3( 0, 0, 1f ), new Vec3( 0, 0, 2f ) );

		var weights = SkinBinder.BindSmooth( mesh, skeleton );

		return (mesh, weights, skeleton);
	}

	static IEnumerable<int> Neighbours( PolyMesh mesh, int vertex )
	{
		foreach ( var e in mesh.BuildVertexEdges()[vertex] )
			yield return e.A == vertex ? e.B : e.A;
	}

	static float Of( BoneWeight[] weights, int bone )
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

	static bool Near( float a, float b ) => MathF.Abs( a - b ) < 1e-4f;

	static BoneWeight[][] Snapshot( SkinWeights weights )
	{
		var copy = new BoneWeight[weights.Count][];

		for ( var i = 0; i < weights.Count; i++ )
			copy[i] = (BoneWeight[])weights[i].Clone();

		return copy;
	}

	static SkinWeights Restore( BoneWeight[][] snapshot )
	{
		var weights = new SkinWeights();

		foreach ( var w in snapshot )
			weights.Vertices.Add( (BoneWeight[])w.Clone() );

		return weights;
	}

	static bool SameAs( SkinWeights weights, BoneWeight[][] snapshot )
	{
		if ( weights.Count != snapshot.Length )
			return false;

		for ( var i = 0; i < snapshot.Length; i++ )
		{
			if ( !SameWeights( weights[i], snapshot[i] ) )
				return false;
		}

		return true;
	}

	static bool SameWeights( BoneWeight[] a, BoneWeight[] b )
	{
		if ( a.Length != b.Length )
			return false;

		for ( var i = 0; i < a.Length; i++ )
		{
			if ( a[i].Bone != b[i].Bone || MathF.Abs( a[i].Weight - b[i].Weight ) > 1e-5f )
				return false;
		}

		return true;
	}
}
