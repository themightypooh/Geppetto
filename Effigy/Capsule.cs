using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A segment with a radius - the shape cloth collides with when it is worn.
///
/// WHY NOT THE MESH. Cloth tested against a character's triangles snags on every bolt and falls
/// through every gap, and on a body built from parts (Camhead is struts and plates with air
/// between them) it has nothing to rest on at all. A capsule per bone is what real clothing
/// sims collide with: round, so cloth slides over it; solid, so there is no gap to fall into;
/// and a dozen of them cost less per particle than one BVH query. A shoulder capsule is the
/// ledge a shirt hangs from.
/// </summary>
public readonly struct Capsule
{
	public readonly Vec3 A;
	public readonly Vec3 B;
	public readonly float Radius;

	/// <summary>The skeleton bone it rides on, or -1. Live cloth needs it to move the capsule with
	/// the animation; a static drape ignores it.</summary>
	public readonly int Bone;

	public Capsule( Vec3 a, Vec3 b, float radius, int bone = -1 )
	{
		A = a;
		B = b;
		Radius = radius;
		Bone = bone;
	}

	/// <summary>The nearest point on the capsule's core segment.</summary>
	public Vec3 Closest( Vec3 p )
	{
		var ab = B - A;
		var len = ab.LengthSquared;
		var t = len < 1e-10f ? 0f : Math.Clamp( Vec3.Dot( p - A, ab ) / len, 0f, 1f );
		return A + ab * t;
	}

	/// <summary>Push <paramref name="p"/> out to the surface plus <paramref name="skin"/>.
	/// True when it moved.</summary>
	public bool Push( ref Vec3 p, float skin, out Vec3 normal )
	{
		var core = Closest( p );
		var d = p - core;
		var dist = d.Length;
		var want = Radius + skin;
		normal = Vec3.Zero;

		if ( dist >= want )
			return false;

		normal = dist > 1e-6f ? d * (1f / dist) : new Vec3( 1f, 0f, 0f );
		p = core + normal * want;
		return true;
	}
}

/// <summary>Capsules fitted to a rig from the body wearing it.</summary>
public static class BoneCapsules
{
	/// <summary>
	/// One capsule per region bone, as thick as the body around it.
	///
	/// THE RADIUS IS A PERCENTILE, not the furthest vertex. The furthest vertex round a spine is
	/// the tip of a shoulder pad or an antenna cable; a capsule that big swallows the arms. Most of
	/// the body's bulk is what a garment rests on, so the radius is the distance most of that
	/// bone's vertices sit within.
	/// </summary>
	public static List<Capsule> Build( BodyRegions.Map map, IEnumerable<PolyMesh> bodies, float percentile = 0.75f )
	{
		var segments = map.Segments;
		var near = new List<float>[segments.Count];

		for ( var i = 0; i < near.Length; i++ )
			near[i] = new List<float>();

		foreach ( var body in bodies )
		{
			foreach ( var p in body.Positions )
			{
				var best = -1;
				var bestD = float.MaxValue;

				for ( var s = 0; s < segments.Count; s++ )
				{
					var d = (new Capsule( segments[s].A, segments[s].B, 0f ).Closest( p ) - p).LengthSquared;

					if ( d < bestD )
					{
						bestD = d;
						best = s;
					}
				}

				if ( best >= 0 )
					near[best].Add( MathF.Sqrt( bestD ) );
			}
		}

		var capsules = new List<Capsule>();

		for ( var s = 0; s < segments.Count; s++ )
		{
			var region = segments[s].Region;

			// Hands, feet and the head are never under a shirt, and their bones are fingers,
			// toes and eyelids - dozens of tiny capsules that would only snag a hem.
			if ( region is BodyRegion.Hand or BodyRegion.Foot or BodyRegion.Head || near[s].Count < 8 )
				continue;

			near[s].Sort();
			var radius = near[s][Math.Clamp( (int)(near[s].Count * percentile), 0, near[s].Count - 1 )];

			// Cables and rods under half an inch are detail cloth rides over, not something it
			// hangs on - and each one is a test per particle per step.
			if ( radius >= 0.5f )
				capsules.Add( new Capsule( segments[s].A, segments[s].B, radius, map.SegmentBones[s] ) );
		}

		return capsules;
	}
}
