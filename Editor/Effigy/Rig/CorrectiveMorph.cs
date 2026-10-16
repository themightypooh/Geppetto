using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// One corrective shape on the rig: a bone, the bend it is made for, and the axis it bends about
/// (zero = <see cref="CorrectiveMorph.AutoAxis"/>). Saved with the rig; the export turns each into a
/// morph on the skinned mesh (<see cref="CorrectiveMorph.Build"/>).
/// </summary>
public sealed class CorrectiveShape
{
	public string Bone;

	/// <summary>The bend the shape fully corrects, in degrees (90 = a right-angle elbow).</summary>
	public float Angle = 90f;

	/// <summary>World axis the joint bends about; zero picks one (see <see cref="CorrectiveMorph.AutoAxis"/>).</summary>
	public Vec3 Axis;

	/// <summary>The morph's name in the model: bone_bend90.</summary>
	public string MorphName => CorrectiveMorph.Name( Bone, Angle );

	public CorrectiveShape Clone() => new() { Bone = Bone, Angle = Angle, Axis = Axis };

	public override string ToString() => Axis.LengthSquared > 0f
		? string.Format( CultureInfo.InvariantCulture, "{0} {1:0.###} {2:0.####} {3:0.####} {4:0.####}", Bone, Angle, Axis.x, Axis.y, Axis.z )
		: string.Format( CultureInfo.InvariantCulture, "{0} {1:0.###}", Bone, Angle );

	/// <summary>"bone angle [ax ay az]", as <see cref="ToString"/> writes it; null when it does not parse.</summary>
	public static CorrectiveShape Parse( string text )
	{
		var p = (text ?? "").Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		if ( p.Length < 2 || !float.TryParse( p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var angle ) )
			return null;

		var c = new CorrectiveShape { Bone = p[0], Angle = angle };
		if ( p.Length >= 5
			&& float.TryParse( p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
			&& float.TryParse( p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
			&& float.TryParse( p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
			c.Axis = new Vec3( x, y, z );
		return c;
	}
}

/// <summary>
/// Corrective morphs for the volume linear blend skinning loses at a bent joint.
///
/// s&amp;box skins by blending bone MATRICES (core/shaders/skinning_cs.shader: 4 weights, no dual
/// quaternions). A vertex weighted w to the bending side and 1 − w to the parent lands at
/// J + [(1 − w)·I + w·R(A)]·(v − J). In the plane the joint bends in, that blended matrix is a
/// rotation times a SCALE s(w) = √((1 − w)² + w² + 2w(1 − w)·cos A) — at w = ½ it is cos(A/2), so
/// the joint ring keeps 71% of its cross-section at 90° and 34% at 140°, however many loops it has
/// (realtime-flesh-shading.md §6.2). The part along the bend axis is untouched; the ring flattens
/// into an ellipse.
///
/// So the exact fix, in the rest pose where a morph lives: push each vertex's in-plane offset from
/// the joint out by 1/s(w) − 1. Skinned at angle A, the blended matrix's s cancels it and every
/// vertex is back at its rest distance from the joint. Vertices fully on one side (w = 0 or 1) have
/// s = 1 and do not move, so the shape is confined to the blend zone by the weights themselves.
///
/// The morph is exact at the angle it is built for. Driven by <see cref="WeightAt"/> it tracks the
/// mid-ring's loss in between (that is the curve a joint-angle driver in the animgraph or a
/// Marionette morph curve should follow).
/// </summary>
public static class CorrectiveMorph
{
	/// <summary>bone_bend90: what the export names the morph, and what drives it.</summary>
	public static string Name( string bone, float angle ) =>
		$"{(bone ?? "bone").Trim().Replace( ' ', '_' )}_bend{MathF.Round( angle ).ToString( CultureInfo.InvariantCulture )}";

	/// <summary>
	/// The axis a bone bends about when none is given: across the chain if it already bends (the
	/// parent's direction × this bone's), else at right angles to the bone and to the model's front
	/// (+X in s&amp;box), so a leg hanging down bends about Y and an arm held out sideways bends about
	/// Z: both fold toward the front.
	/// </summary>
	public static Vec3 AutoAxis( Skeleton skeleton, int bone )
	{
		var d = skeleton.BoneDirection( bone ).Normal;
		var parent = skeleton.Bones[bone].Parent;

		if ( parent >= 0 )
		{
			var across = Vec3.Cross( skeleton.BoneDirection( parent ).Normal, d );
			if ( across.Length > 0.17f ) // more than about 10° of bend already
				return across.Normal;
		}

		var a = Vec3.Cross( d, new Vec3( 1, 0, 0 ) );
		if ( a.Length < 0.3f ) a = Vec3.Cross( d, new Vec3( 0, 0, 1 ) );
		return a.Normal;
	}

	/// <summary>The scale the blended matrix applies in the bend plane at weight <paramref name="w"/> and bend <paramref name="radians"/>.</summary>
	public static float InPlaneScale( float w, float radians )
	{
		var s2 = (1f - w) * (1f - w) + w * w + 2f * w * (1f - w) * MathF.Cos( radians );
		return MathF.Sqrt( MathF.Max( s2, 0f ) );
	}

	/// <summary>
	/// The morph weight that matches the mid-ring loss at <paramref name="degrees"/> for a shape built
	/// at <paramref name="keyDegrees"/>: (1/cos(θ/2) − 1) / (1/cos(A/2) − 1). 0 straight, 1 at the key
	/// angle; about 0.39 at 60° for a 90° key.
	/// </summary>
	public static float WeightAt( float degrees, float keyDegrees )
	{
		static float Loss( float deg ) => 1f / MathF.Max( MathF.Cos( MathF.Abs( deg ) * MathF.PI / 360f ), 0.2f ) - 1f;
		var key = Loss( keyDegrees );
		return key <= 1e-6f ? 0f : Math.Clamp( Loss( degrees ) / key, 0f, 2f );
	}

	/// <summary>The bone and every bone under it: the side of the joint that bends.</summary>
	public static bool[] Subtree( Skeleton skeleton, int bone )
	{
		var o = new bool[skeleton.Count];
		o[bone] = true;
		for ( var i = 0; i < skeleton.Count; i++ )
			for ( var p = skeleton.Bones[i].Parent; p >= 0; p = skeleton.Bones[p].Parent )
				if ( p == bone ) { o[i] = true; break; }
		return o;
	}

	/// <summary>Per vertex, the share of its weight on the bending side (0 parent side, 1 bending side).</summary>
	public static float[] BendWeights( SkinWeights skin, bool[] subtree, int count )
	{
		var o = new float[count];
		for ( var v = 0; v < count && v < skin.Count; v++ )
		{
			var total = 0f; var bent = 0f;
			foreach ( var b in skin.Vertices[v] ?? Array.Empty<BoneWeight>() )
			{
				if ( b.Weight <= 0f ) continue;
				total += b.Weight;
				if ( b.Bone >= 0 && b.Bone < subtree.Length && subtree[b.Bone] ) bent += b.Weight;
			}
			o[v] = total > 1e-9f ? bent / total : 0f;
		}
		return o;
	}

	/// <summary>
	/// The corrective morph's per-vertex offsets for <paramref name="shape"/> on a skinned mesh.
	/// Weights are taken as the 4-bone set the export writes (pruned and renormalised), so the shape
	/// matches what the GPU blends. Null when the bone is not in the skeleton or the mesh has no skin.
	/// </summary>
	public static Vec3[] Deltas( PolyMesh mesh, Skeleton skeleton, CorrectiveShape shape, int maxInfluences = 4 )
	{
		if ( mesh?.Skin is null || skeleton is null || shape is null ) return null;
		var bone = skeleton.IndexOf( shape.Bone );
		if ( bone < 0 ) return null;

		var axis = shape.Axis.LengthSquared > 1e-12f ? shape.Axis.Normal : AutoAxis( skeleton, bone );
		var joint = skeleton.HeadWorld( bone );
		var radians = MathF.Min( MathF.Abs( shape.Angle ), 170f ) * MathF.PI / 180f;

		var pruned = new SkinWeights( mesh.VertexCount );
		for ( var v = 0; v < mesh.VertexCount && v < mesh.Skin.Count; v++ )
			pruned.Vertices[v] = SkinWeights.Prune( mesh.Skin.Vertices[v], maxInfluences );
		var w = BendWeights( pruned, Subtree( skeleton, bone ), mesh.VertexCount );

		var deltas = new Vec3[mesh.VertexCount];
		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			if ( w[v] <= 1e-4f || w[v] >= 1f - 1e-4f ) continue;
			var s = MathF.Max( InPlaneScale( w[v], radians ), 0.2f ); // 1/s capped at ×5 near a full fold
			var off = mesh.Positions[v] - joint;
			var inPlane = off - axis * Vec3.Dot( off, axis );
			deltas[v] = inPlane * (1f / s - 1f);
		}
		return deltas;
	}

	/// <summary>
	/// Add <paramref name="shape"/> to <paramref name="mesh"/> as a morph (replacing one of the same
	/// name). Returns the morph, or null when it cannot be built (no skin, no such bone).
	/// </summary>
	public static MeshMorph Build( PolyMesh mesh, Skeleton skeleton, CorrectiveShape shape, int maxInfluences = 4 )
	{
		var d = Deltas( mesh, skeleton, shape, maxInfluences );
		if ( d is null ) return null;
		var name = shape.MorphName;
		mesh.Morphs.RemoveAll( m => m?.Name == name );
		var morph = new MeshMorph { Name = name, Deltas = d };
		mesh.Morphs.Add( morph );
		return morph;
	}

	/// <summary>
	/// Every shape in <paramref name="shapes"/> onto the export mesh. Returns a line per shape for the
	/// export log: what was added, or why one was skipped (so a typo'd bone is not a silent no-op).
	/// </summary>
	public static List<string> BuildAll( PolyMesh mesh, Skeleton skeleton, IEnumerable<CorrectiveShape> shapes, int maxInfluences = 4 )
	{
		var log = new List<string>();
		foreach ( var shape in shapes ?? Enumerable.Empty<CorrectiveShape>() )
		{
			if ( shape is null ) continue;
			if ( skeleton?.IndexOf( shape.Bone ) is not >= 0 )
			{
				log.Add( $"corrective shape for '{shape.Bone}' skipped: no bone of that name" );
				continue;
			}
			var m = Build( mesh, skeleton, shape, maxInfluences );
			var moved = m?.Deltas.Count( x => x.LengthSquared > 1e-10f ) ?? 0;
			log.Add( m is null ? $"corrective shape for '{shape.Bone}' skipped: the mesh has no skin weights"
				: moved == 0 ? $"corrective shape {m.Name}: no vertex is shared between {shape.Bone} and its parent, so there is nothing to correct (the joint is rigid)"
				: $"corrective shape {m.Name}: {moved} vertices, drive it 0 straight to 1 at {shape.Angle:0}°" );
		}
		return log;
	}

	/// <summary>
	/// Linear blend skinning of a two-sided bend, as the GPU does it: the bone's side rotates
	/// <paramref name="degrees"/> about <paramref name="axis"/> through its head, the parent's stays,
	/// each vertex blends the two by its weight, after <paramref name="morph"/> × <paramref name="morphWeight"/>
	/// is added in the rest pose. For measuring and previewing what a corrective does.
	/// </summary>
	public static Vec3[] Bend( PolyMesh mesh, Skeleton skeleton, string bone, float degrees, Vec3 axis,
		Vec3[] morph = null, float morphWeight = 1f, int maxInfluences = 4 )
	{
		var b = skeleton.IndexOf( bone );
		if ( b < 0 || mesh.Skin is null ) return mesh.Positions.ToArray();
		if ( axis.LengthSquared < 1e-12f ) axis = AutoAxis( skeleton, b );
		var joint = skeleton.HeadWorld( b );
		var q = Quat.FromAxisAngle( axis.Normal, degrees * MathF.PI / 180f );

		var pruned = new SkinWeights( mesh.VertexCount );
		for ( var v = 0; v < mesh.VertexCount && v < mesh.Skin.Count; v++ )
			pruned.Vertices[v] = SkinWeights.Prune( mesh.Skin.Vertices[v], maxInfluences );
		var w = BendWeights( pruned, Subtree( skeleton, b ), mesh.VertexCount );

		var o = new Vec3[mesh.VertexCount];
		for ( var v = 0; v < o.Length; v++ )
		{
			var p = mesh.Positions[v] + (morph is null ? default : morph[v] * morphWeight);
			var rotated = joint + q.Rotate( p - joint );
			o[v] = p * (1f - w[v]) + rotated * w[v];
		}
		return o;
	}
}
