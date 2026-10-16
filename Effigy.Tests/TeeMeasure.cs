using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// A top measured the way a garment factory measures one, so it can be held up against a real
/// shirt's spec sheet: chest one inch under the armhole, body length from the high point of the
/// shoulder to the hem, neck width and drops, sleeve opening and length, hem. Circumferences
/// are taken as a tape would - round the outside (convex hull) of the cloth in that plane - and
/// doubled flat widths on the spec side are compared against them.
///
/// The wearer is measured too (chest round the fullest part, arms down), because that is how a
/// size is chosen: the s&amp;box male's chest picks his size on the chart, and the shirt is compared
/// with that size.
/// </summary>
public static class TeeMeasure
{
	public sealed class Result
	{
		public float BodyHem, BodyArm, BodyWidest;
		public float BodyChest, Chest, Hem, BodyLength, NeckWidth, FrontDrop, BackDrop, SleeveOpening, SleeveLengthCB, Muscle;
	}

	/// <summary>Perimeter of the convex hull of 2D points.</summary>
	static float Round( IEnumerable<Vec2> points )
	{
		var pts = points.Distinct().OrderBy( p => p.x ).ThenBy( p => p.y ).ToList();

		if ( pts.Count < 3 )
			return 0f;

		var hull = new List<Vec2>();

		for ( var pass = 0; pass < 2; pass++ )
		{
			var start = hull.Count;

			foreach ( var p in pass == 0 ? pts : Enumerable.Reverse( pts ) )
			{
				while ( hull.Count >= start + 2 && Vec2.Cross( hull[^1] - hull[^2], p - hull[^2] ) <= 0f )
					hull.RemoveAt( hull.Count - 1 );

				hull.Add( p );
			}

			hull.RemoveAt( hull.Count - 1 );
		}

		var len = 0f;

		for ( var i = 0; i < hull.Count; i++ )
			len += (hull[(i + 1) % hull.Count] - hull[i]).Length;

		return len;
	}

	/// <summary>Where a mesh crosses the horizontal plane at <paramref name="z"/>.</summary>
	static List<Vec3> Slice( PolyMesh mesh, float z )
	{
		var hits = new List<Vec3>();

		foreach ( var f in mesh.Faces )
			for ( var c = 0; c < f.Count; c++ )
			{
				var a = mesh.Positions[f.Indices[c]];
				var b = mesh.Positions[f.Indices[(c + 1) % f.Count]];

				if ( (a.z - z) * (b.z - z) > 0f || MathF.Abs( b.z - a.z ) < 1e-6f )
					continue;

				hits.Add( a + (b - a) * ((z - a.z) / (b.z - a.z)) );
			}

		return hits;
	}

	public static Result Measure( PolyMesh garment, PolyMesh wearer, Skeleton rig )
	{
		var r = new Result();
		var map = new BodyRegions.Map( rig );
		var sheet = MeshSolidify.OuterSheet( garment, out _ ) ?? garment;

		map.Chain( BodyRegion.UpperArm, 1, out var shoulderL, out var elbowL );
		map.Chain( BodyRegion.LowerArm, 1, out _, out var wristL );
		var armpitZ = shoulderL.z - (shoulderL - elbowL).Length * 0.25f;

		// The torso's half-width at the armpit: cloth further out than that is sleeve.
		var torso = wearer.Positions.Where( p => map.Locate( p ).Region is BodyRegion.Torso or BodyRegion.Hips ).ToList();
		var halfWidth = torso.Where( p => MathF.Abs( p.z - armpitZ ) < 1f ).Select( p => MathF.Abs( p.y ) ).DefaultIfEmpty( 8f ).Max();

		// Body chest: round the fullest part, arms down - the torso alone, so the arms (out in the
		// bind pose) are not in the tape.
		r.BodyChest = Enumerable.Range( 0, 8 ).Select( k => armpitZ - k * 0.5f )
			.Select( z => Round( torso.Where( p => MathF.Abs( p.z - z ) < 0.3f ).Select( p => new Vec2( p.x, p.y ) ) ) ).Max();

		// The tape goes round the body of the shirt, not the sleeves: anything further out than an
		// inch past the torso's own width at that height is the underside of a sleeve.
		var chestZ = armpitZ - 1f;
		var torsoHalf = torso.Where( p => MathF.Abs( p.z - chestZ ) < 0.3f ).Select( p => MathF.Abs( p.y ) ).DefaultIfEmpty( halfWidth ).Max();
		r.Chest = Round( Slice( sheet, chestZ ).Where( p => MathF.Abs( p.y ) <= torsoHalf + 1f ).Select( p => new Vec2( p.x, p.y ) ) );


		var loops = GarmentTrim.OrderedBoundaryLoops( sheet );
		Vec3 Centre( List<int> l ) => l.Aggregate( Vec3.Zero, ( acc, i ) => acc + sheet.Positions[i] ) / l.Count;

		var hem = loops.OrderBy( l => Centre( l ).z ).FirstOrDefault( l => MathF.Abs( Centre( l ).y ) < 2f );
		var collar = loops.OrderByDescending( l => Centre( l ).z ).FirstOrDefault( l => MathF.Abs( Centre( l ).y ) < 2f );
		var sleeve = loops.Where( l => Centre( l ).y > halfWidth ).OrderByDescending( l => (Centre( l ) - shoulderL).Length ).FirstOrDefault();

		if ( hem is not null )
		{
			r.Hem = Round( hem.Select( i => new Vec2( sheet.Positions[i].x, sheet.Positions[i].y ) ) );

			// The body under the hem, round both legs and the seat the way a tape would go.
			var hz = hem.Average( i => sheet.Positions[i].z );

			// The widest the body gets between the chest and the hem - what a shirt that does not
			// stretch has to get over.
			var trunk = wearer.Positions.Where( p => map.Locate( p ).Region is BodyRegion.Torso or BodyRegion.Hips or BodyRegion.UpperLeg ).ToList();
			for ( var z = armpitZ - 1f; z >= hz; z -= 0.5f )
			{
				var zz = z;
				r.BodyWidest = MathF.Max( r.BodyWidest, Round( trunk.Where( p => MathF.Abs( p.z - zz ) < 0.3f ).Select( p => new Vec2( p.x, p.y ) ) ) );
			}
			r.BodyHem = Round( wearer.Positions.Where( p => MathF.Abs( p.z - hz ) < 0.3f
				&& map.Locate( p ).Region is BodyRegion.Hips or BodyRegion.UpperLeg ).Select( p => new Vec2( p.x, p.y ) ) );
		}

		if ( collar is not null && hem is not null )
		{
			var c = collar.Select( i => sheet.Positions[i] ).ToList();
			var hps = c.Max( p => p.z );
			var mid = c.Where( p => MathF.Abs( p.y ) < 0.6f ).ToList();
			var front = mid.Count > 0 ? mid.MaxBy( p => p.x ) : c.MaxBy( p => p.x );
			var back = mid.Count > 0 ? mid.MinBy( p => p.x ) : c.MinBy( p => p.x );

			r.BodyLength = hps - hem.Min( i => sheet.Positions[i].z );
			r.NeckWidth = c.Max( p => p.y ) - c.Min( p => p.y );
			r.FrontDrop = hps - front.z;
			r.BackDrop = hps - back.z;

			if ( sleeve is not null )
			{
				// Centre back of the neck, over the top of the shoulder, down the top of the arm
				// to the sleeve's hem: the way the sleeve length is taped on a spec sheet.
				var s = sleeve.Select( i => sheet.Positions[i] ).ToList();
				var axis = (wristL - shoulderL).Normal;
				var hemTop = s.MaxBy( p => p.z );
				var overShoulder = new Vec3( shoulderL.x, shoulderL.y, hps - 1f );
				r.SleeveLengthCB = (overShoulder - back).Length + (hemTop - overShoulder).Length;

				var u = Vec3.Cross( axis, new Vec3( 1, 0, 0 ) ).Normal;
				var v = Vec3.Cross( axis, u );
				r.SleeveOpening = Round( s.Select( p => new Vec2( Vec3.Dot( p, u ), Vec3.Dot( p, v ) ) ) );

				// The bare arm where the sleeve ends.
				var sc = s.Aggregate( Vec3.Zero, ( acc, p ) => acc + p ) / s.Count;
				// Skin within half an inch of the sleeve-end's plane and five of its centre: the arm.
				var ringNormal = Vec3.Cross( s[1] - s[0], s[s.Count / 3] - s[0] ).Normal;
				r.BodyArm = Round( wearer.Positions.Where( p => MathF.Abs( Vec3.Dot( p - sc, ringNormal ) ) < 0.5f && (p - sc).Length < 5f )
					.Select( p => new Vec2( Vec3.Dot( p, u ), Vec3.Dot( p, v ) ) ) );
			}
		}

		return r;
	}

	/// <summary>The reference: a size M classic tee. Chest, length and sleeve from Gildan 5000's
	/// spec sheet; neck, drops, sleeve opening and hem from a German brand's regular-fit chart.
	/// Flat widths doubled to rounds; cm to inches.</summary>
	public static readonly (string Name, float Value)[] SizeM =
	{
		("chest round (1in under armhole)", 40f),
		("hem round", 40.6f),
		("body length (HPS to hem)", 29f),
		("neck width", 6.5f),
		("front neck drop", 3.0f),
		("back neck drop", 0.4f),
		("sleeve opening round", 12.6f),
		("sleeve length from centre back", 17f),
	};

	public static IEnumerable<(string Name, float Ours, float Real)> Compare( Result r ) => new[]
	{
		(SizeM[0].Name, r.Chest, SizeM[0].Value),
		(SizeM[1].Name, r.Hem, SizeM[1].Value),
		(SizeM[2].Name, r.BodyLength, SizeM[2].Value),
		(SizeM[3].Name, r.NeckWidth, SizeM[3].Value),
		(SizeM[4].Name, r.FrontDrop, SizeM[4].Value),
		(SizeM[5].Name, r.BackDrop, SizeM[5].Value),
		(SizeM[6].Name, r.SleeveOpening, SizeM[6].Value),
		(SizeM[7].Name, r.SleeveLengthCB, SizeM[7].Value),
	};
}
