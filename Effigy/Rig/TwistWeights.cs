using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Spread a limb bone's weight onto the twist bones that share its length.
///
/// WHY A RETARGET LEAVES A MODEL LOOKING FINE UNTIL IT MOVES. Citizen's arms and legs are not one
/// bone each. Every upper and lower limb bone carries a pair of twist bones - `arm_lower_L_twist0`
/// at the elbow, `arm_lower_L_twist1` partway to the wrist - which the anim graph drives at
/// fractions of the joint's roll. Citizen's own mesh is weighted across them, so a forearm that
/// rotates ninety degrees at the wrist unwinds gradually along its length, the way an arm does.
///
/// A mesh that arrives from somewhere else has never heard of them. <see cref="SkeletonRetarget"/>
/// maps its single `forearm_L` onto `arm_lower_L` and stops, which is correct as far as it goes -
/// the geometry lands in the right place and the model stands there looking perfect. Then the
/// graph rolls the wrist, the whole rotation lands on one ring of vertices because that is the
/// only bone carrying any weight, and the limb pinches shut. It reads as a broken retarget or bad
/// weights rather than as a rig the mesh does not know about, which is a long way to look for it.
///
/// HOW THE WEIGHT IS SPLIT. The twist bones sit at known points along their parent: twist0 at the
/// head, twist1 a fixed distance down, and the parent itself owns the far end. So each is treated
/// as a station on the bone, and a vertex's weight is split between the two stations it lies
/// between, linearly. That is the same shape as the ramp the graph drives them with, and it needs
/// nothing to be authored - the geometry already says where each vertex sits along the limb.
///
/// This is a correction to derived weights, so it runs BEFORE any paint layer: somebody who has
/// painted a vertex by hand meant it, and gets to overrule this.
/// </summary>
public static class TwistWeights
{
	/// <summary>
	/// Redistribute <paramref name="mesh"/>'s weights across whatever twist bones
	/// <paramref name="skeleton"/> has. Returns how many vertices changed.
	///
	/// A skeleton with no twist bones is left alone, so this is safe to call unconditionally.
	/// Naming follows citizen's `&lt;bone&gt;_twist&lt;n&gt;` convention; anything else is ignored
	/// rather than guessed at.
	/// </summary>
	public static int Spread( PolyMesh mesh, Skeleton skeleton )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( skeleton );

		if ( !mesh.IsRigged )
			return 0;

		// Per parent bone: the stations along it, nearest the head first, each with the bone that
		// owns it. The parent itself is always the last station, at its own tail.
		var stations = new Dictionary<int, List<(float Along, int Bone)>>();

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var name = skeleton.Bones[b].Name;
			var cut = name.LastIndexOf( "_twist", StringComparison.OrdinalIgnoreCase );

			if ( cut < 0 || !int.TryParse( name[(cut + 6)..], out _ ) )
				continue;

			var parent = skeleton.IndexOf( name[..cut] );

			if ( parent < 0 || skeleton.Bones[b].Parent != parent )
				continue;

			// How far down the parent this twist sits, measured along the parent's own axis. The
			// axis is the geometric head-to-child direction, not the frame's +Y - citizen's bones
			// are +X-down-the-bone, and measuring along +Y would place every twist at the wrong
			// station.
			var along = Vec3.Dot(
				skeleton.WorldBind( b ).Origin - skeleton.WorldBind( parent ).Origin,
				skeleton.BoneDirection( parent ) );

			if ( !stations.TryGetValue( parent, out var list ) )
				stations[parent] = list = new List<(float, int)>();

			list.Add( (along, b) );
		}

		if ( stations.Count == 0 )
			return 0;

		foreach ( var (parent, list) in stations )
		{
			list.Add( (Extent( skeleton, parent, list ), parent) );
			list.Sort( ( x, y ) => x.Along.CompareTo( y.Along ) );
		}

		var changed = 0;

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var weights = mesh.Skin[v];

			if ( weights.Length == 0 || !weights.Any( w => stations.ContainsKey( w.Bone ) ) )
				continue;

			var spread = new Dictionary<int, float>();

			foreach ( var w in weights )
			{
				if ( !stations.TryGetValue( w.Bone, out var list ) )
				{
					spread[w.Bone] = spread.GetValueOrDefault( w.Bone ) + w.Weight;
					continue;
				}

				var bone = skeleton.WorldBind( w.Bone );
				var along = Vec3.Dot( mesh.Positions[v] - bone.Origin, skeleton.BoneDirection( w.Bone ) );

				foreach ( var (to, share) in Split( list, along ) )
					spread[to] = spread.GetValueOrDefault( to ) + w.Weight * share;
			}

			mesh.Skin[v] = spread.Where( kv => kv.Value > 1e-5f )
				.Select( kv => new BoneWeight( kv.Key, kv.Value ) )
				.OrderByDescending( w => w.Weight )
				.ToArray();

			changed++;
		}

		return changed;
	}

	/// <summary>
	/// How far the parent bone actually reaches, as the station that owns its far end.
	///
	/// NOT Bone.Length, which is where this went wrong the first time. A skeleton built from a
	/// table of transforms - <see cref="CitizenSkeleton"/> is one - never sets a length, so every
	/// bone reports the default 1. Placing the parent's station an inch down a seven inch forearm
	/// puts it BEFORE the twist that sits at 3.9, inverting the order and handing everything past
	/// the elbow to a bone that is driven at a fraction of the joint's roll. The limb collapses,
	/// and only once something actually drives the twists - so it looks like a new bug in whatever
	/// changed last.
	///
	/// The distance to the furthest child that is not itself a twist is the honest measure: it is
	/// where the next joint is, which is where this bone's flesh ends. Bone.Length is the fallback
	/// for a genuine leaf, and the twists themselves are a floor so the order can never invert
	/// again whatever the geometry says.
	/// </summary>
	static float Extent( Skeleton skeleton, int parent, List<(float Along, int Bone)> twists )
	{
		var axis = skeleton.BoneDirection( parent );
		var origin = skeleton.WorldBind( parent ).Origin;
		var reach = 0f;

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			if ( skeleton.Bones[b].Parent != parent || twists.Any( t => t.Bone == b ) )
				continue;

			reach = MathF.Max( reach, Vec3.Dot( skeleton.WorldBind( b ).Origin - origin, axis ) );
		}

		if ( reach <= 1e-3f )
			reach = MathF.Max( skeleton.Bones[parent].Length, 1e-3f );

		// Never inside a twist that is already placed further down.
		foreach ( var t in twists )
			reach = MathF.Max( reach, t.Along + 1e-3f );

		return reach;
	}

	/// <summary>
	/// Which stations a point at <paramref name="along"/> belongs to, and how much of each.
	///
	/// Between two stations it is a straight ramp; past either end it is entirely the station at
	/// that end. Clamping rather than extrapolating matters at the shoulder, where geometry rides
	/// above the bone's head and a ramp run backwards would hand it a negative share.
	/// </summary>
	static IEnumerable<(int Bone, float Share)> Split( List<(float Along, int Bone)> stations, float along )
	{
		if ( along <= stations[0].Along )
		{
			yield return (stations[0].Bone, 1f);
			yield break;
		}

		for ( var i = 1; i < stations.Count; i++ )
		{
			if ( along > stations[i].Along )
				continue;

			var span = stations[i].Along - stations[i - 1].Along;
			var t = span <= 1e-6f ? 1f : (along - stations[i - 1].Along) / span;

			yield return (stations[i - 1].Bone, 1f - t);
			yield return (stations[i].Bone, t);
			yield break;
		}

		yield return (stations[^1].Bone, 1f);
	}
}
