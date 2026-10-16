using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The B-Mesh / SQM stick-figure cage (<see cref="SkeletonCage"/>) and the hull joints it gives
/// <see cref="SkinBlockout"/>: every result must be all quads, closed, manifold, one piece of
/// genus zero (Euler characteristic 2), and wound outward. Checked on a lone stick, a biped, a
/// quadruped, stars of five, seven and twelve branches (past the cube joint's six), a bent chain
/// for twist, the citizen rig, and the refusals.
/// </summary>
public static class SkeletonCageTests
{
	public static void Run()
	{
		Section( "skeleton cage: B-Mesh / SQM hull joints" );
		TestLoneStick();
		TestBiped();
		TestQuadruped();
		TestStars();
		TestBentChainDoesNotTwist();
		TestTransitionBetweenJoints();
		TestFitReachesTheRadius();
		TestCitizenHullBlockout();
		TestRefusals();
	}

	/// <summary>All quads, closed, manifold, one piece, sphere topology, positive volume.</summary>
	internal static bool IsCleanCage( PolyMesh mesh, out string detail )
	{
		var v = MeshValidator.Validate( mesh );
		var quads = mesh.Faces.All( f => f.Count == 4 );
		var pieces = MeshSplit.ConnectedPieces( mesh ).Count;
		var euler = MeshValidator.EulerCharacteristic( mesh );
		var volume = mesh.SignedVolume();
		var unused = mesh.VertexCount - mesh.Faces.SelectMany( f => f.Indices ).Distinct().Count();
		detail = $"{v} quads={quads} pieces={pieces} euler={euler} volume={volume:0.###} unused={unused} faces={mesh.FaceCount}";
		return v.IsValid && v.IsClosed && quads && pieces == 1 && euler == 2 && volume > 0f && unused == 0;
	}

	static SkeletonCage.Graph Chains( params (Vec3 P, float R)[][] chains )
	{
		var g = new SkeletonCage.Graph();
		foreach ( var chain in chains )
		{
			var prev = -1;
			foreach ( var (p, r) in chain )
			{
				var n = g.AddNode( p, r );
				if ( prev >= 0 )
					g.AddEdge( prev, n );
				prev = n;
			}
		}

		return g;
	}

	static (Vec3, float) J( float x, float y, float z, float r ) => (new Vec3( x, y, z ), r);

	static void TestLoneStick()
	{
		var g = Chains( new[] { J( 0, 0, 0, 2 ), J( 0, 0, 10, 2 ) } );
		var cage = SkeletonCage.Build( g );
		Check( "a lone stick is a clean all-quad solid", IsCleanCage( cage, out var d ), d );

		// Eight sides by default: every ring is an octagon, so the tube is all valence four; a quad
		// fan cap has a valence-four tip and four valence-three rim corners, as a cube's are.
		var valence = Valence( cage );
		Check( "a lone stick's vertices are valence 4, and 8 cap corners valence 3", valence.All( x => x is 3 or 4 ) && valence.Count( x => x == 3 ) == 8, string.Join( ",", valence.GroupBy( x => x ).Select( x => $"{x.Key}x{x.Count()}" ) ) );

		var four = SkeletonCage.Build( g, new SkeletonCage.Options { Sides = 4 } );
		Check( "four sides makes a box-like stick, capped by single quads", IsCleanCage( four, out d ), d );

		var smooth = SkeletonCage.Build( g, new SkeletonCage.Options { SmoothLevels = 2 } );
		Check( "and it stays clean subdivided", IsCleanCage( smooth, out d ), d );
	}

	internal static SkeletonCage.Graph Biped()
	{
		return Chains(
			new[] { J( 0, 0, 40, 5 ), J( 0, 0, 52, 5.5f ), J( 0, 0, 62, 6 ) },        // pelvis → chest
			new[] { J( 0, 0, 62, 6 ), J( 0, 0, 68, 2 ), J( 0, 0, 74, 4.5f ) },        // neck, head
			new[] { J( 0, 0, 40, 5 ), J( 5, 0, 36, 3.5f ), J( 6, 0, 20, 2.6f ), J( 6, 0, 3, 2 ), J( 6, -5, 0, 1.6f ) }, // left leg
			new[] { J( 0, 0, 40, 5 ), J( -5, 0, 36, 3.5f ), J( -6, 0, 20, 2.6f ), J( -6, 0, 3, 2 ), J( -6, -5, 0, 1.6f ) },
			new[] { J( 0, 0, 62, 6 ), J( 8, 0, 63, 2.4f ), J( 20, 0, 60, 2f ), J( 31, 0, 58, 1.5f ) },  // left arm
			new[] { J( 0, 0, 62, 6 ), J( -8, 0, 63, 2.4f ), J( -20, 0, 60, 2f ), J( -31, 0, 58, 1.5f ) } );
	}

	static void TestBiped()
	{
		var g = Biped();
		var cage = SkeletonCage.Build( g, new SkeletonCage.Options(), out var report );
		Check( "a biped is a clean all-quad solid", IsCleanCage( cage, out var d ), d );
		Check( "with two branching joints (pelvis, chest)", report.BranchJoints == 2, $"{report.BranchJoints}" );
		Check( "and it is mirror symmetric to the vertex", IsMirrorSymmetric( cage, 1e-3f ) );

		var smooth = SkeletonCage.Build( g, new SkeletonCage.Options { SmoothLevels = 2 } );
		Check( "and stays clean subdivided twice", IsCleanCage( smooth, out d ), d );

		// Every joint is inside the body: the cage wraps the stick figure.
		var bvh = MeshBVH.Build( smooth );
		var inside = g.Nodes.All( p => Inside( smooth, bvh, p ) );
		Check( "every joint of the stick figure is inside the subdivided body", inside );
	}

	static void TestQuadruped()
	{
		// A spine with shoulders and hips, four legs, a neck and head, a tail.
		var g = Chains(
			new[] { J( -20, 0, 30, 6 ), J( -10, 0, 31, 6.5f ), J( 0, 0, 31, 6.5f ), J( 12, 0, 32, 6 ) },  // hips → shoulders
			new[] { J( 12, 0, 32, 6 ), J( 20, 0, 38, 3 ), J( 26, 0, 40, 4 ) },                             // neck, head
			new[] { J( -20, 0, 30, 6 ), J( -30, 0, 31, 1.5f ), J( -42, 0, 26, 0.8f ) },                    // tail
			new[] { J( 12, 0, 32, 6 ), J( 12, 5, 22, 2.5f ), J( 12, 5, 10, 1.8f ), J( 13, 5, 0, 1.4f ) },
			new[] { J( 12, 0, 32, 6 ), J( 12, -5, 22, 2.5f ), J( 12, -5, 10, 1.8f ), J( 13, -5, 0, 1.4f ) },
			new[] { J( -20, 0, 30, 6 ), J( -20, 5, 20, 3f ), J( -22, 5, 10, 2f ), J( -20, 5, 0, 1.4f ) },
			new[] { J( -20, 0, 30, 6 ), J( -20, -5, 20, 3f ), J( -22, -5, 10, 2f ), J( -20, -5, 0, 1.4f ) } );

		var cage = SkeletonCage.Build( g, new SkeletonCage.Options(), out var report );
		Check( "a quadruped is a clean all-quad solid", IsCleanCage( cage, out var d ), d );
		Check( "its hips and shoulders are four-way joints", report.BranchJoints == 2 && report.MaxBranches == 4, $"{report.BranchJoints} joints, max {report.MaxBranches}" );
		var smooth = SkeletonCage.Build( g, new SkeletonCage.Options { SmoothLevels = 2 } );
		Check( "and it stays clean subdivided", IsCleanCage( smooth, out d ), d );
	}

	static SkeletonCage.Graph Star( int arms, bool flat, float r = 2f )
	{
		var chains = new List<(Vec3, float)[]>();
		for ( var i = 0; i < arms; i++ )
		{
			var a = 2f * MathF.PI * i / arms;
			var z = flat ? 0f : (i % 2 == 0 ? 4f : -3f);
			var tip = new Vec3( MathF.Cos( a ) * 12f, MathF.Sin( a ) * 12f, z );
			chains.Add( new[] { (Vec3.Zero, r * 1.6f), (tip * 0.5f, r), (tip, r * 0.7f) } );
		}

		return Chains( chains.ToArray() );
	}

	static void TestStars()
	{
		foreach ( var (arms, flat) in new[] { (5, true), (5, false), (7, true), (12, false), (3, true) } )
		{
			var g = Star( arms, flat );
			var cage = SkeletonCage.Build( g, new SkeletonCage.Options(), out var report );
			Check( $"a {arms}-branch {(flat ? "flat" : "crooked")} star is a clean all-quad solid", IsCleanCage( cage, out var d ), d );
			Check( $"  with one joint of {arms} branches", report.BranchJoints == 1 && report.MaxBranches == arms );
			var smooth = SkeletonCage.Build( g, new SkeletonCage.Options { SmoothLevels = 1 } );
			Check( $"  and clean subdivided", IsCleanCage( smooth, out d ), d );
		}

		// A sphere-full of spikes: limbs in every direction, so the hull needs no extra points.
		var dirs = new List<(Vec3, float)[]>();
		foreach ( var dir in new[] { new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, -1, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 0, -1 ), new Vec3( 1, 1, 1 ).Normal, new Vec3( -1, -1, 1 ).Normal } )
			dirs.Add( new[] { (Vec3.Zero, 3f), (dir * 10f, 1f) } );
		var urchin = SkeletonCage.Build( Chains( dirs.ToArray() ), new SkeletonCage.Options(), out var ur );
		Check( "an eight-spike urchin is clean with no extra joint points", IsCleanCage( urchin, out var ud ) && ur.ExtraJointPoints == 0, ud + $" extra={ur.ExtraJointPoints}" );

		// Two limbs almost along each other still build.
		var close = Chains(
			new[] { J( 0, 0, 0, 2 ), J( 10, 0, 0, 1 ) },
			new[] { J( 0, 0, 0, 2 ), J( 10, 0.3f, 0, 1 ) },
			new[] { J( 0, 0, 0, 2 ), J( -10, 0, 0, 1 ) } );
		Check( "two nearly parallel limbs out of one joint still build clean", IsCleanCage( SkeletonCage.Build( close ), out var cd ), cd );
	}

	static void TestBentChainDoesNotTwist()
	{
		// A chain that bends in three dimensions, with many joints. Parallel transport keeps each
		// ring's first vertex on the same side: every quad's rungs run along the limb, never across.
		var pts = new List<(Vec3, float)>();
		for ( var i = 0; i <= 16; i++ )
		{
			var a = i * 0.45f;
			pts.Add( (new Vec3( MathF.Cos( a ) * 10f, MathF.Sin( a ) * 10f, i * 2.5f ), 1.2f) );
		}

		var g = Chains( pts.ToArray() );
		var cage = SkeletonCage.Build( g, new SkeletonCage.Options { RingSpacing = 100f } );
		Check( "a corkscrew chain is a clean all-quad solid", IsCleanCage( cage, out var d ), d );

		// Worst quad: the angle between its two rung edges (the ones joining rings), which a twist
		// tears apart. Rungs are the quad edges far from both cap tips.
		var worst = 0f;
		var ends = new[] { pts[0].Item1, pts[^1].Item1 };
		foreach ( var f in cage.Faces )
		{
			var p = f.Indices.Select( i => cage.Positions[i] ).ToArray();
			if ( ends.Any( e => (cage.FaceCentroid( f ) - e).Length < 2.5f ) )
				continue; // the caps are fans, not rows
			var e0 = (p[1] - p[0]).Normal;
			var e2 = (p[2] - p[3]).Normal;
			var e1 = (p[2] - p[1]).Normal;
			var e3 = (p[3] - p[0]).Normal;
			worst = MathF.Max( worst, MathF.Min( Angle( e0, e2 ), Angle( e1, e3 ) ) );
		}

		Check( "and no quad is twisted: opposite edges of every quad within 35°", worst < 35f, $"{worst:0.#}°" );
	}

	static void TestTransitionBetweenJoints()
	{
		// A three-way joint (rings of 8) joined straight to a seven-way joint (rings of other sizes):
		// the limb between them changes over with three-to-one quads and stays all quads.
		var chains = new List<(Vec3, float)[]>
		{
			new[] { J( 0, 0, 0, 3 ), J( 20, 0, 0, 3 ) },
			new[] { J( 0, 0, 0, 3 ), J( -8, 8, 0, 1 ) },
			new[] { J( 0, 0, 0, 3 ), J( -8, -8, 0, 1 ) },
		};
		for ( var i = 0; i < 6; i++ )
		{
			var a = 2f * MathF.PI * i / 6f;
			chains.Add( new[] { J( 20, 0, 0, 3 ), J( 30, MathF.Cos( a ) * 8f, MathF.Sin( a ) * 8f, 1f ) } );
		}

		var cage = SkeletonCage.Build( Chains( chains.ToArray() ), new SkeletonCage.Options(), out var report );
		Check( "a limb between joints of different ring sizes is clean", IsCleanCage( cage, out var d ), d + $" transitions={report.Transitions}" );
		Check( "  and it went through a ring-size change", report.Transitions >= 1, $"{report.Transitions}" );
	}

	static void TestFitReachesTheRadius()
	{
		var g = Chains( new[] { J( 0, 0, 0, 2 ), J( 0, 0, 20, 2 ) } );
		var loose = SkeletonCage.Build( g, new SkeletonCage.Options { SmoothLevels = 2 } );
		var fitted = SkeletonCage.Build( g, new SkeletonCage.Options { SmoothLevels = 2, Fit = true } );

		static (float Min, float Max) Mid( PolyMesh m ) =>
			m.Positions.Where( p => p.z > 7f && p.z < 13f ).Select( p => MathF.Sqrt( p.x * p.x + p.y * p.y ) ).Aggregate( (Min: float.MaxValue, Max: 0f), ( a, r ) => (MathF.Min( a.Min, r ), MathF.Max( a.Max, r )) );

		var (lmin, _) = Mid( loose );
		var (fmin, fmax) = Mid( fitted );
		Check( "unfitted, a smoothed stick is thinner than its radius", lmin < 1.97f, $"{lmin:0.###}" );
		Check( "fitted, the middle of the stick is the radius typed, within 1%", MathF.Abs( fmin - 2f ) < 0.02f && MathF.Abs( fmax - 2f ) < 0.02f, $"{fmin:0.###}..{fmax:0.###}" );
		var tip = fitted.Positions.Max( p => p.z );
		Check( "and the cap reaches the sphere at the end", MathF.Abs( tip - 22f ) < 0.05f, $"{tip:0.###}" );
		Check( "and it is still clean", IsCleanCage( fitted, out var d ), d );
	}

	static void TestCitizenHullBlockout()
	{
		var rig = CitizenSkeleton.Build();
		var body = SkinBlockout.Build( rig, null, 0, SkinBlockout.Joints.Hull );
		Check( "the citizen rig with hull joints is a clean all-quad cage", IsCleanCage( body, out var d ), d );
		var smooth = SkinBlockout.Build( rig, null, 2, SkinBlockout.Joints.Hull );
		Check( "  and subdivided twice", IsCleanCage( smooth, out d ), d );

		// Seven bones from one joint: refused by cubes, built by hulls.
		var fan = new Skeleton();
		var root = fan.AddBone( "palm", -1, Xform.Translate( Vec3.Zero ), 4f );
		var top = fan.TailWorld( root );
		for ( var i = 0; i < 7; i++ )
			fan.AddBone( $"finger{i}", root, Xform.Translate( top ) * Xform.Rotate( new Vec3( 1, 0, 0 ), -1.2f + i * 0.4f ), 6f );

		var refused = false;
		try { SkinBlockout.Build( fan, _ => 1f, 0 ); }
		catch ( InvalidOperationException e ) { refused = e.Message.Contains( "hull" ); }
		Check( "seven bones at a joint: cube joints refuse and point at hull joints", refused );
		var hand = SkinBlockout.Build( fan, _ => 1f, 1, SkinBlockout.Joints.Hull );
		Check( "  and hull joints build it clean", IsCleanCage( hand, out d ), d );

		// The original path is unchanged when not asked: same count as before this existed.
		var one = new Skeleton();
		one.AddBone( "spine", -1, Xform.Translate( Vec3.Zero ), 10f );
		Check( "cube joints stay the default and unchanged", SkinBlockout.Build( one, _ => 2f, 0 ).FaceCount == 14
			&& SkinBlockout.Build( one, _ => 2f, 0, SkinBlockout.Joints.Cube ).FaceCount == 14 );
	}

	static void TestRefusals()
	{
		static string Refusal( Action build )
		{
			try { build(); }
			catch ( InvalidOperationException e ) { return e.Message; }
			return null;
		}

		var loop = new SkeletonCage.Graph();
		var a = loop.AddNode( new Vec3( 0, 0, 0 ), 1 );
		var b = loop.AddNode( new Vec3( 10, 0, 0 ), 1 );
		var c = loop.AddNode( new Vec3( 5, 8, 0 ), 1 );
		loop.AddEdge( a, b );
		loop.AddEdge( b, c );
		loop.AddEdge( c, a );
		Check( "a loop is refused, saying which limb closes it", Refusal( () => SkeletonCage.Build( loop ) )?.Contains( "loop" ) == true );
		Check( "no limbs is refused", Refusal( () => SkeletonCage.Build( new SkeletonCage.Graph() ) ) is not null );
		var g = Chains( new[] { J( 0, 0, 0, 1 ), J( 0, 0, 5, 1 ) } );
		Check( "an odd number of sides is refused with the fix", Refusal( () => SkeletonCage.Build( g, new SkeletonCage.Options { Sides = 5 } ) )?.Contains( "even" ) == true );
		var flat = new SkeletonCage.Graph();
		flat.Nodes.Add( Vec3.Zero ); flat.Radii.Add( 0f );
		flat.Nodes.Add( new Vec3( 1, 0, 0 ) ); flat.Radii.Add( 1f );
		flat.AddEdge( 0, 1 );
		Check( "a joint with no thickness is refused", Refusal( () => SkeletonCage.Build( flat ) )?.Contains( "thickness" ) == true );
	}

	// ---- helpers ----

	static int[] Valence( PolyMesh mesh )
	{
		var edges = mesh.BuildVertexEdges();
		return edges.Select( e => e.Count ).ToArray();
	}

	static float Angle( Vec3 a, Vec3 b ) => MathF.Acos( Math.Clamp( Vec3.Dot( a, b ), -1f, 1f ) ) * 180f / MathF.PI;

	static bool IsMirrorSymmetric( PolyMesh mesh, float tolerance )
	{
		var all = mesh.Positions;
		foreach ( var p in all )
		{
			var m = new Vec3( -p.x, p.y, p.z );
			if ( !all.Any( q => (q - m).LengthSquared < tolerance * tolerance ) )
				return false;
		}

		return true;
	}

	/// <summary>Inside by ray parity along +x.</summary>
	static bool Inside( PolyMesh mesh, MeshBVH bvh, Vec3 p )
	{
		var hits = 0;
		var origin = p;
		var dir = new Vec3( 1f, 0.0123f, 0.0071f ).Normal;
		for ( var i = 0; i < 64; i++ )
		{
			var hit = bvh.Raycast( mesh, origin, dir );
			if ( hit is not { } h )
				break;
			hits++;
			origin = h.Point + dir * 1e-3f;
		}

		return hits % 2 == 1;
	}
}
