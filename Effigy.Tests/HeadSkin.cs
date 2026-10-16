using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

public static partial class CreatureGen
{
	/// <summary>
	/// Skin surface detail for a head at sculpt level 3 (about 0.04 in between vertices), as
	/// displacement along the normals of <paramref name="mesh"/> at rest (head inches). The base
	/// sculpt is smooth below about 0.3 in, which is what reads as a mannequin in clay. Two of the
	/// detail levels from skin-wrinkles.md §4 fit in geometry at this density:
	/// - tissue: soft lumps of fat and muscle under the skin, 0.4–1 in across, up to 0.015 in;
	/// - microrelief: the network of fine creases skin is broken into (Worley cell edges), cells
	///   0.3 in, grooves about 0.07 in wide and 0.006 in deep, stretched along the face's flow.
	/// Pores are below this density (they go in the normal map). Lips, lids, the inside of the
	/// mouth and the sockets get none; the nose gets a coarser, deeper network (its pores and oil).
	/// </summary>
	static PolyMesh SkinDetail( PolyMesh mesh, int seed, float strength = 1f )
	{
		var n = mesh.VertexCount;
		var normals = SkinStrain.VertexNormals( mesh.Positions, mesh.Faces );
		// Regions by the artists' face sets: 2/3 sockets, 6 mouth bag, 7 lips; 9 nose.
		var none = new bool[n];
		var nose = new bool[n];
		foreach ( var f in mesh.Faces )
			foreach ( var i in f.Indices )
			{
				if ( f.Material is 2 or 3 or 6 ) none[i] = true;
				if ( f.Material == 9 ) nose[i] = true;
			}
		var noise = new Noise3( seed );
		var outp = mesh.Positions.ToArray();
		for ( var i = 0; i < n; i++ )
		{
			if ( none[i] || normals[i].LengthSquared == 0f ) continue;
			var p = mesh.Positions[i];
			// No detail on the vermilion or the lid margins, fading in over 0.15 in.
			var lip = Smoothstep( 0.55f, 0.4f, MathF.Abs( p.z + 3.05f ) ) * Smoothstep( 1.3f, 1.0f, MathF.Abs( p.y ) ) * Smoothstep( 3.7f, 4.0f, p.x );
			var lid = MathF.Max( 0f, 1f - ((p - new Vec3( 3.5f, 1.25f * MathF.Sign( p.y ), 0.15f )).Length - 0.55f) / 0.25f );
			var keep = (1f - Math.Clamp( lip, 0f, 1f )) * (1f - Math.Clamp( lid, 0f, 1f ));
			if ( keep <= 0f ) continue;
			// More on the face than the scalp.
			var face = 0.45f + 0.55f * Smoothstep( 1.5f, 3.2f, p.x );

			var tissue = noise.Fbm( p * 1.6f + new Vec3( 3.1f, 0, 0 ), 3 ) * 0.014f;
			var cell = nose[i] ? 0.22f : 0.3f;
			// The face's flow: creases run mostly across the vertical pull of gravity and muscle,
			// so cells are stretched sideways (scaled down in y). Warped so the network isn't a lattice.
			var q = new Vec3( p.x, p.y * 0.75f, p.z ) / cell + noise.Vector( p * 0.9f ) * 0.35f;
			var (f1, f2) = Worley( q, seed );
			var edge = 1f - Smoothstep( 0f, 0.22f, f2 - f1 );
			var depth = nose[i] ? 0.009f : 0.006f;
			var broken = Smoothstep( -0.25f, 0.25f, noise.Sample( p * 2.3f + new Vec3( 0, 7.7f, 0 ) ) );
			var micro = -depth * edge * (0.35f + 0.65f * broken);
			outp[i] = p + normals[i] * ((tissue + micro) * keep * face * strength);
		}
		return new PolyMesh( outp, mesh.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, f.Material ) ) );
	}

	/// <summary>Distances to the nearest and second-nearest jittered cell point (Worley F1, F2).</summary>
	static (float F1, float F2) Worley( Vec3 q, int seed )
	{
		int cx = (int)MathF.Floor( q.x ), cy = (int)MathF.Floor( q.y ), cz = (int)MathF.Floor( q.z );
		float f1 = float.MaxValue, f2 = float.MaxValue;
		for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
				for ( var dz = -1; dz <= 1; dz++ )
				{
					int x = cx + dx, y = cy + dy, z = cz + dz;
					var h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791 ^ seed * 2654435761u);
					float R() { h ^= h << 13; h ^= h >> 17; h ^= h << 5; return (h & 0xFFFFFF) / (float)0x1000000; }
					var pt = new Vec3( x + 0.08f + 0.84f * R(), y + 0.08f + 0.84f * R(), z + 0.08f + 0.84f * R() );
					var d = (pt - q).Length;
					if ( d < f1 ) { f2 = f1; f1 = d; }
					else if ( d < f2 ) f2 = d;
				}
		return (f1, f2);
	}
}
