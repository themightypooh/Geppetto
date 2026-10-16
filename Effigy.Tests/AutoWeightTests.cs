using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Bone heat and geodesic voxel weights. The claims checked are the ones that make them worth
/// having over distance weighting: smooth monotone falloff across a joint, a partition of unity
/// with at most four influences, NO bleed between limbs that are close in space but separate on
/// the surface, and an honest, reported fallback on the meshes heat cannot solve.
/// </summary>
public static class AutoWeightTests
{
	public static void Run()
	{
		Section( "auto weights: bone heat across a joint" );
		TestJoint( BindMethod.Heat );

		Section( "auto weights: geodesic voxel across a joint" );
		TestJoint( BindMethod.Voxel );

		Section( "auto weights: close limbs do not bleed, and fallbacks are reported" );
		TestNoBleedBetweenLimbs();
		TestNonManifoldFallsBackToVoxel();
		TestHiddenPieceFallsBack();
		TestHelperBonesSkipped();

		Section( "auto weights: SkinBinder.Bind keeps the parametric rules" );
		TestBindKeepsPinnedBodies();
		TestMethodNames();
		TestHeatTiming();
	}

	/// <summary>A closed cylinder along +Z from z=0 to <paramref name="height"/>: rings every
	/// <paramref name="step"/>, capped by fans to a centre vertex at each end.</summary>
	public static PolyMesh Cylinder( float radius, float height, float step, int segments, Vec3 offset = default )
	{
		var m = new PolyMesh();
		var rings = (int)MathF.Round( height / step );

		for ( var r = 0; r <= rings; r++ )
			for ( var s = 0; s < segments; s++ )
			{
				var a = s * MathF.Tau / segments;
				m.AddVertex( offset + new Vec3( radius * MathF.Cos( a ), radius * MathF.Sin( a ), r * height / rings ) );
			}

		for ( var r = 0; r < rings; r++ )
			for ( var s = 0; s < segments; s++ )
			{
				var s1 = (s + 1) % segments;
				m.AddFace( new[] { r * segments + s, r * segments + s1, (r + 1) * segments + s1, (r + 1) * segments + s } );
			}

		var bottom = m.AddVertex( offset );
		var top = m.AddVertex( offset + new Vec3( 0, 0, height ) );

		for ( var s = 0; s < segments; s++ )
		{
			var s1 = (s + 1) % segments;
			m.AddFace( new[] { bottom, s1, s } );
			m.AddFace( new[] { top, rings * segments + s, rings * segments + s1 } );
		}

		return m;
	}

	static Skeleton TwoBones( float height, Vec3 offset = default )
	{
		var sk = new Skeleton();
		var lower = sk.AddBoneFromPoints( "lower", -1, offset + new Vec3( 0, 0, 0 ), offset + new Vec3( 0, 0, height / 2 ) );
		sk.AddBoneFromPoints( "upper", lower, offset + new Vec3( 0, 0, height / 2 ), offset + new Vec3( 0, 0, height ) );
		return sk;
	}

	static SkinWeights Bind( BindMethod method, PolyMesh mesh, Skeleton sk, out BindReport report ) =>
		SkinBinder.Bind( mesh, null, null, sk, method, out report );

	static float WeightOf( BoneWeight[] w, int bone ) => w.Where( b => b.Bone == bone ).Sum( b => b.Weight );

	static bool Valid( SkinWeights w, int vertices, int max, out string why )
	{
		why = null;

		if ( w.Count != vertices ) { why = $"{w.Count} weight sets for {vertices} vertices"; return false; }

		for ( var v = 0; v < w.Count; v++ )
		{
			var set = w[v];
			var sum = set.Sum( b => b.Weight );

			if ( set.Length == 0 || set.Length > max || Math.Abs( sum - 1f ) > 1e-4f || set.Any( b => b.Weight < 0 || float.IsNaN( b.Weight ) ) )
			{
				why = $"vertex {v}: {string.Join( ",", set.Select( b => b.ToString() ) )}";
				return false;
			}
		}

		return true;
	}

	static void TestJoint( BindMethod method )
	{
		const float height = 10f;
		var mesh = Cylinder( 1f, height, 0.25f, 16 );
		var sk = TwoBones( height );
		var w = Bind( method, mesh, sk, out var report );

		Check( $"{method}: every vertex sums to 1 with 1–4 influences", Valid( w, mesh.VertexCount, 4, out var why ), why ?? report.ToString() );
		Check( $"{method}: the requested method ran on the whole mesh", report.Used == method && report.Fallback is null, report.ToString() );

		// Mean upper-bone weight per ring, bottom to top.
		var rings = new SortedDictionary<float, List<float>>();

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var z = MathF.Round( mesh.Positions[v].z * 4f ) / 4f;
			if ( !rings.TryGetValue( z, out var list ) ) rings[z] = list = new List<float>();
			list.Add( WeightOf( w[v], 1 ) );
		}

		var profile = rings.Select( kv => (Z: kv.Key, W: kv.Value.Average()) ).ToList();
		var drops = 0;

		for ( var i = 1; i < profile.Count; i++ )
			if ( profile[i].W < profile[i - 1].W - 1e-3f ) drops++;

		var at = profile.First( p => MathF.Abs( p.Z - height / 2 ) < 0.01f ).W;
		var bottom = profile[0].W;
		var top = profile[^1].W;
		var blended = profile.Count( p => p.W > 0.1f && p.W < 0.9f );

		Check( $"{method}: the upper bone's weight rises monotonically up the cylinder", drops == 0,
			string.Join( " ", profile.Select( p => $"{p.Z:0.##}:{p.W:0.00}" ) ) );
		Check( $"{method}: the ends belong to their own bones", bottom < 0.02f && top > 0.98f, $"bottom {bottom:0.000}, top {top:0.000}" );
		Check( $"{method}: the joint is shared about evenly", at > 0.3f && at < 0.7f, $"{at:0.00} at the joint" );
		Check( $"{method}: the falloff is smooth — several rings blend, it is not a hard cut", blended >= 3 && blended <= profile.Count - 4,
			$"{blended} of {profile.Count} rings between 0.1 and 0.9" );

		// Rings are symmetric around the axis: every vertex of a ring should agree.
		var spread = rings.Values.Max( l => l.Max() - l.Min() );
		Check( $"{method}: weights are the same all the way round a ring", spread < 0.05f, $"widest spread {spread:0.000}" );
	}

	static void TestNoBleedBetweenLimbs()
	{
		// Two legs, 0.4 apart in space, joined only through a hip bar far above. Distance weighting
		// lets each leg's inside face take weight from the other leg's bone.
		var left = Cylinder( 1f, 8f, 0.25f, 16, new Vec3( -1.2f, 0, 0 ) );
		var right = Cylinder( 1f, 8f, 0.25f, 16, new Vec3( 1.2f, 0, 0 ) );
		var mesh = Merge( left, right );

		var sk = new Skeleton();
		sk.AddBoneFromPoints( "leg_l", -1, new Vec3( -1.2f, 0, 8 ), new Vec3( -1.2f, 0, 0 ) );
		sk.AddBoneFromPoints( "leg_r", -1, new Vec3( 1.2f, 0, 8 ), new Vec3( 1.2f, 0, 0 ) );

		float Bleed( SkinWeights w )
		{
			var worst = 0f;
			for ( var v = 0; v < mesh.VertexCount; v++ )
			{
				var own = v < left.VertexCount ? 0 : 1;
				worst = Math.Max( worst, 1f - WeightOf( w[v], own ) );
			}
			return worst;
		}

		var distance = Bleed( SkinBinder.BindSmooth( mesh, sk ) );
		var heat = Bleed( Bind( BindMethod.Heat, mesh, sk, out var hr ) );
		var voxel = Bleed( Bind( BindMethod.Voxel, mesh, sk, out var vr ) );

		Check( "distance weighting does bleed between close legs (the problem being solved)", distance > 0.2f, $"{distance:P0} foreign" );
		Check( "bone heat: no weight crosses between the legs", heat < 1e-3f, $"{heat:P2} foreign; {hr}" );
		Check( "voxel: no weight crosses between the legs", voxel < 1e-3f, $"{voxel:P2} foreign; {vr}" );
	}

	static PolyMesh Merge( params PolyMesh[] parts )
	{
		var m = new PolyMesh();

		foreach ( var part in parts )
		{
			var baseIndex = m.VertexCount;
			foreach ( var p in part.Positions ) m.AddVertex( p );
			foreach ( var f in part.Faces ) m.AddFace( f.Indices.Select( i => i + baseIndex ).ToArray() );
		}

		return m;
	}

	static void TestNonManifoldFallsBackToVoxel()
	{
		var mesh = Cylinder( 1f, 6f, 0.5f, 12 );
		// A fin sharing one side edge of the cylinder: that edge now has three faces.
		var a = mesh.Faces[5].Indices[0];
		var b = mesh.Faces[5].Indices[3];
		var tip = mesh.AddVertex( (mesh.Positions[a] + mesh.Positions[b]) * 0.5f + new Vec3( 2, 0, 0 ) );
		mesh.AddFace( new[] { a, b, tip } );

		var w = Bind( BindMethod.Heat, mesh, TwoBones( 6f ), out var report );
		Check( "heat on a non-manifold mesh falls back to voxel and says why",
			report.Used == BindMethod.Voxel && report.Fallback?.Contains( "non-manifold" ) == true && Valid( w, mesh.VertexCount, 4, out _ ),
			report.ToString() );
	}

	static void TestHiddenPieceFallsBack()
	{
		// A closed body with a separate small ball sealed inside its middle, the bones outside along
		// the side. No straight line from the ball reaches the bone without crossing the outer skin.
		var outer = Primitives.QuadSphere( 4f, 8 );
		var inner = Primitives.QuadSphere( 0.5f, 3 );
		var mesh = Merge( outer, inner );

		var outside = new Skeleton();
		var r2 = outside.AddBoneFromPoints( "a", -1, new Vec3( 6, 0, -3 ), new Vec3( 6, 0, 0 ) );
		outside.AddBoneFromPoints( "b", r2, new Vec3( 6, 0, 0 ), new Vec3( 6, 0, 3 ) );

		var w = Bind( BindMethod.Heat, mesh, outside, out var report );
		Check( "a piece that can see no bone is weighted by voxel, the rest by heat, and both counts are reported",
			report.Used == BindMethod.Heat && report.VoxelVertices == inner.VertexCount && report.HeatVertices == outer.VertexCount
			&& report.Fallback?.Contains( "cannot see any bone" ) == true && Valid( w, mesh.VertexCount, 4, out _ ),
			report.ToString() );
	}

	static void TestHelperBonesSkipped()
	{
		var mesh = Cylinder( 1f, 6f, 0.5f, 12 );
		var sk = TwoBones( 6f );
		sk.AddBoneFromPoints( "lower_twist", 0, new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 2 ) );
		var w = Bind( BindMethod.Heat, mesh, sk, out var report );
		var twist = Enumerable.Range( 0, mesh.VertexCount ).Max( v => WeightOf( w[v], 2 ) );

		Check( "helper bones (twist, IK, face) get no weight, and the report names them",
			twist == 0f && report.Notes.Any( n => n.Contains( "lower_twist" ) ), report.ToString() );
	}

	static void TestBindKeepsPinnedBodies()
	{
		var mesh = Merge( Cylinder( 1f, 6f, 0.5f, 12 ), Primitives.QuadSphere( 0.6f, 3 ) );
		var cylinderCount = mesh.VertexCount - Primitives.QuadSphere( 0.6f, 3 ).VertexCount;

		// Move the ball to sit near the top of the cylinder, inside the upper bone's reach.
		for ( var v = cylinderCount; v < mesh.VertexCount; v++ )
			mesh.Positions[v] = mesh.Positions[v] + new Vec3( 0, 0, 5f );

		var ranges = new[]
		{
			new BodyRange( "arm", "Arm", 0, cylinderCount ),
			new BodyRange( "ball", "Ball", cylinderCount, mesh.VertexCount - cylinderCount ),
		};

		var map = new Dictionary<string, string> { ["ball"] = "lower" };
		var w = SkinBinder.Bind( mesh, ranges, map, TwoBones( 6f ), BindMethod.Heat, out var report );
		var ballRigid = Enumerable.Range( cylinderCount, mesh.VertexCount - cylinderCount ).All( v => w[v].Length == 1 && w[v][0].Bone == 0 );
		var armBlends = Enumerable.Range( 0, cylinderCount ).Any( v => w[v].Length > 1 );

		Check( "a body pinned to a bone stays rigid to it under heat; the rest is heat-weighted",
			ballRigid && armBlends && report.Notes.Any( n => n.Contains( "kept rigid" ) ), report.ToString() );

		var bodies = SkinBinder.Bind( mesh, ranges, map, TwoBones( 6f ), BindMethod.Bodies, out _ );
		var legacy = SkinBinder.SmoothWeights( mesh, SkinBinder.BindBodies( mesh, ranges, map, TwoBones( 6f ) ) );
		var same = Enumerable.Range( 0, mesh.VertexCount ).All( v => bodies[v].SequenceEqual( legacy[v] ) );
		Check( "the Bodies method is exactly the old bind (BindBodies then SmoothWeights)", same );

		var threw = false;
		try { SkinBinder.Bind( mesh, ranges, new Dictionary<string, string> { ["ball"] = "nope" }, TwoBones( 6f ), BindMethod.Voxel, out _ ); }
		catch ( InvalidOperationException e ) { threw = e.Message.Contains( "nope" ) && e.Message.Contains( "Re-assign" ); }
		Check( "a body pinned to a missing bone is refused with the bone's name and a remedy", threw );
	}

	static void TestMethodNames()
	{
		var ok = Enum.GetValues<BindMethod>().All( m => SkinBinder.TryParseMethod( SkinBinder.MethodName( m ), out var back ) && back == m );
		Check( "every bind method round-trips through its script name", ok && !SkinBinder.TryParseMethod( "magic", out _ ) );
	}

	static void TestHeatTiming()
	{
		var mesh = Cylinder( 2f, 40f, 0.1f, 96 );
		var sk = new Skeleton();
		var prev = -1;

		for ( var i = 0; i < 8; i++ )
			prev = sk.AddBoneFromPoints( $"seg{i}", prev, new Vec3( 0, 0, i * 5 ), new Vec3( 0, 0, (i + 1) * 5 ) );

		var sw = Stopwatch.StartNew();
		var w = Bind( BindMethod.Heat, mesh, sk, out var report );
		var heatMs = sw.Elapsed.TotalMilliseconds;
		sw.Restart();
		Bind( BindMethod.Voxel, mesh, sk, out _ );
		var voxelMs = sw.Elapsed.TotalMilliseconds;

		Console.WriteLine( $"  note  {mesh.VertexCount} vertices, 8 bones: heat {heatMs:F0} ms, voxel {voxelMs:F0} ms" );
		Check( "a ~40k-vertex heat bind completes with valid weights", Valid( w, mesh.VertexCount, 4, out var why ), why ?? report.ToString() );
	}
}
