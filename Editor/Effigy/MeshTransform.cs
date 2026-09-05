using System;

namespace Effigy;

/// <summary>
/// Applying an <see cref="Xform"/> to a mesh.
///
/// Split out of Xform.cs so that file is pure arithmetic on Vec3 and Xform with no mesh types in
/// it. That matters for exactly one reason: the game assembly's sandbox forbids the filesystem, so
/// only part of this kernel can ever ship to it, and the smaller that part's dependencies are the
/// more of it a game can use. Xform pulling in PolyMesh dragged half the kernel across that line
/// for the sake of two functions that live here.
/// </summary>
public static class MeshTransform
{
	/// <summary>
	/// Transform a mesh in place, reversing face winding when the transform flips handedness.
	///
	/// Forgetting the reversal is the single most common mirror bug: the mirrored half renders
	/// black or lit from inside, and the model reads as fine in wireframe. There is a test for it.
	/// </summary>
	public static void Apply( PolyMesh mesh, Xform xform )
	{
		for ( var i = 0; i < mesh.Positions.Count; i++ )
			mesh.Positions[i] = xform.TransformPoint( mesh.Positions[i] );

		if ( !xform.FlipsWinding )
			return;

		foreach ( var f in mesh.Faces )
		{
			Array.Reverse( f.Indices );
			Array.Reverse( f.UVs );
		}
	}

	public static PolyMesh Transformed( PolyMesh mesh, Xform xform )
	{
		var copy = mesh.Clone();
		Apply( copy, xform );
		return copy;
	}

	/// <summary>
	/// Merge `source` into `target`, offsetting indices. Does not weld — two bodies combined this
	/// way stay topologically separate, which is correct for a pattern and is why Validate reports
	/// them as one mesh with several shells rather than as non-manifold.
	/// </summary>
	public static void Append( PolyMesh target, PolyMesh source )
	{
		var offset = target.Positions.Count;

		// Weights have to be reconciled BEFORE the position lists merge, because both sides are
		// padded against their own current vertex count. Merging an unrigged body into a rigged one
		// is normal — a rig usually arrives after some of the model does — so the unrigged side is
		// padded with empty influences rather than treated as an error.
		if ( target.Skin is not null || source.Skin is not null )
		{
			target.Skin ??= new SkinWeights( target.Positions.Count );

			while ( target.Skin.Count < target.Positions.Count )
				target.Skin.Vertices.Add( new[] { new BoneWeight( 0, 1f ) } );

			// An unrigged body merged into a rigged one gets bound to the FIRST BONE rather than
			// left empty. Empty influences pass IsRigged, fail Validate, and export as "no links",
			// which studiomdl reads as the parent bone column - so the body silently ends up rigged
			// to whatever bone 0 happens to be, discovered much later and somewhere else. Binding it
			// explicitly is the same outcome, stated out loud, and it keeps the partition of unity
			// that everything downstream assumes.
			var unrigged = new[] { new BoneWeight( 0, 1f ) };

			for ( var i = 0; i < source.Positions.Count; i++ )
			{
				target.Skin.Vertices.Add( source.Skin is not null && i < source.Skin.Count && source.Skin[i].Length > 0
					? (BoneWeight[])source.Skin[i].Clone()
					: unrigged );
			}
		}

		target.Positions.AddRange( source.Positions );

		// Vertex colours merge the way the skin does: pad whichever side lacks them with transparent,
		// so an unpainted body merged into a painted one simply carries no paint of its own. Done
		// after positions are appended but keyed off the pre-merge offset, which is what keeps the
		// colours parallel to the positions they describe.
		if ( target.VertexColors is not null || source.VertexColors is not null )
		{
			target.VertexColors ??= new Vec4[offset];

			var merged = new Vec4[offset + source.Positions.Count];

			for ( var i = 0; i < offset; i++ )
				merged[i] = i < target.VertexColors.Length ? target.VertexColors[i] : Vec4.Zero;

			for ( var i = 0; i < source.Positions.Count; i++ )
				merged[offset + i] = source.VertexColors is not null && i < source.VertexColors.Length
					? source.VertexColors[i]
					: Vec4.Zero;

			target.VertexColors = merged;
		}

		foreach ( var f in source.Faces )
		{
			var indices = new int[f.Count];

			for ( var i = 0; i < f.Count; i++ )
				indices[i] = f.Indices[i] + offset;

			target.AddFace( indices, (Vec2[])f.UVs.Clone(), f.Material );
		}
	}
}
