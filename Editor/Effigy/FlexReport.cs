using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// The bend test: fold each joint of a skinned mesh to 90° and 140° on its hinge and see what the
/// skin does — how much of the volume round the joint survives, and whether the skin passes
/// through itself. No clip and no keyframes; the rig and the weights are all it needs.
///
/// WHY THESE TWO NUMBERS. A joint that loses volume is the "candy wrapper" or the collapsed
/// elbow every linear-blend skin falls into; the craft table's line is a local volume ratio above
/// 0.8 at 90° and 140° (craft.md §7.2). A joint whose inside folds through itself is the other
/// failure, and the one a render from the outside hides. <see cref="TakeAudit"/> checks both over
/// an animated take; this is the same check with the take replaced by a fixed bend per joint.
///
/// LOCAL VOLUME, MEASURED FROM THE JOINT. The volume swept from the joint centre to every
/// triangle within two limb radii of it — a cone volume, which is exactly invariant when a part
/// only turns about the joint. What changes it is the skin stretching or collapsing, which is the
/// thing being measured. Two radii, because the blend region of a smooth bind spans about that.
///
/// BASELINE FIRST, as TakeAudit does: crossings the bind pose already has are the model's and are
/// subtracted; only crossings the bend makes count against it.
/// </summary>
public static class FlexReport
{
	public sealed class Bend
	{
		public string Bone = "";
		public float Degrees;
		public Vec3 Axis;
		public float Radius;
		public float VolumeRatio = float.NaN;
		public int NewCrossings;
		public string CrossingAt;

		/// <summary>The posed vertices, kept only when asked for (for stills).</summary>
		public Vec3[] Posed;

		public bool Ok => (float.IsNaN( VolumeRatio ) || VolumeRatio >= 0.8f) && NewCrossings == 0;
	}

	public sealed class Result
	{
		public string Name = "";
		public int Baseline;
		public List<Bend> Bends = new();
		public List<string> Skipped = new();

		public bool Ok => Bends.All( b => b.Ok );

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.AppendLine( Inv( $"{Name}: flex — {Bends.Count} bend{(Bends.Count == 1 ? "" : "s")}, bind pose crosses itself {Baseline} time{(Baseline == 1 ? "" : "s")} (not counted); targets: volume ratio ≥ 0.8, no new crossings" ) );

			foreach ( var b in Bends )
			{
				sb.Append( b.Ok ? "  " : "! " );
				sb.Append( Inv( $"{b.Bone} {N( b.Degrees )}° about {SectionReport.AxisName( b.Axis )}: volume {(float.IsNaN( b.VolumeRatio ) ? "n/a (no closed skin round the joint)" : N( b.VolumeRatio ))}" ) );
				sb.Append( Inv( $", {b.NewCrossings} new crossing{(b.NewCrossings == 1 ? "" : "s")}" ) );
				if ( b.CrossingAt is not null ) sb.Append( Inv( $" (first at {b.CrossingAt})" ) );
				sb.AppendLine( Inv( $", radius {N( b.Radius )}" ) );
			}

			foreach ( var s in Skipped )
				sb.AppendLine( "  skipped " + s );

			return sb.ToString().TrimEnd();
		}
	}

	/// <summary>
	/// The axis bone <paramref name="bone"/> hinges about: across the bend its chain already has
	/// (parent's direction × this bone's), signed so a positive angle folds further; for a chain
	/// modelled dead straight, the bone's own X, its sideways axis.
	/// </summary>
	public static Vec3 HingeAxis( Skeleton rig, int bone )
	{
		var dir = rig.BoneDirection( bone );
		var parent = rig.Bones[bone].Parent;

		if ( parent >= 0 )
		{
			var across = Vec3.Cross( rig.BoneDirection( parent ), dir );
			if ( across.Length > MathF.Sin( 5f * MathF.PI / 180f ) )
				return across.Normal;
		}

		return SectionReport.InPlane( dir, rig.WorldBind( bone ).X );
	}

	/// <summary>
	/// The bones worth bending: not helpers, not roots, and with skin on both sides of the joint —
	/// some vertices following the bone (or what hangs off it), and some not.
	/// </summary>
	public static List<int> DefaultBones( Skeleton rig, SkinWeights weights )
	{
		var list = new List<int>();

		for ( var b = 0; b < rig.Count; b++ )
		{
			if ( rig.Bones[b].Parent < 0 || Skeleton.IsAuxiliaryBone( rig.Bones[b].Name ) )
				continue;

			var moving = Subtree( rig, b );
			int follow = 0, stay = 0;

			for ( var v = 0; v < weights.Count; v++ )
			{
				var w = weights[v].Where( x => moving[x.Bone] ).Sum( x => x.Weight );
				if ( w >= 0.5f ) follow++; else stay++;
			}

			if ( follow > 0 && stay > 0 )
				list.Add( b );
		}

		return list;
	}

	/// <summary>
	/// Bend each of <paramref name="bones"/> by each of <paramref name="degrees"/> and measure.
	/// <paramref name="axis"/> overrides the hinge (world space) for every bone. A bone whose head
	/// no outline of the mesh encloses is skipped and listed with why.
	/// </summary>
	public static Result Run( PolyMesh mesh, SkinWeights weights, Skeleton rig, IEnumerable<int> bones,
		IReadOnlyList<float> degrees, Vec3? axis = null, string name = null, bool keepPoses = false )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( weights is null || weights.Count != mesh.VertexCount ) throw new ArgumentException( "weights must cover every vertex", nameof( weights ) );
		if ( rig is not { Count: > 0 } ) throw new ArgumentException( "flex needs bones", nameof( rig ) );

		var result = new Result { Name = name ?? "mesh" };
		var bind = Enumerable.Range( 0, rig.Count ).Select( rig.WorldBind ).ToArray();
		var set = TriangleSet.From( mesh );
		var baseline = new HashSet<(int, int)>( MeshClash.Self( set, int.MaxValue ).Pairs.Select( p => (p.TriangleA, p.TriangleB) ) );
		result.Baseline = baseline.Count;
		var (min, max) = ModelMeasure.Bounds( mesh );

		foreach ( var b in bones )
		{
			if ( !TopologyReport.JointFrame( mesh, rig, b, out var head, out _, out var radius ) )
			{
				result.Skipped.Add( $"{rig.Bones[b].Name}: its head {SectionReport.P( rig.HeadWorld( b ) )} is not inside {result.Name} — no skin wraps that joint" );
				continue;
			}

			var hinge = (axis ?? HingeAxis( rig, b )).Normal;
			var moving = Subtree( rig, b );
			var region = new List<int>();

			for ( var t = 0; t < set.Count; t++ )
				if ( ((set.A( t ) + set.B( t ) + set.C( t )) / 3f - head).Length <= 2f * radius )
					region.Add( t );

			var before = ConeVolume( set, region, mesh.Positions, head );

			foreach ( var deg in degrees )
			{
				var turn = Xform.RotateAbout( head, hinge, deg * MathF.PI / 180f );
				var pose = new Xform[bind.Length];
				for ( var i = 0; i < pose.Length; i++ )
					pose[i] = moving[i] ? turn * bind[i] : bind[i];

				var posed = SkinBinder.Deform( mesh.Positions, weights, bind, pose );
				var after = ConeVolume( set, region, posed, head );

				set.Refit( posed );
				var clash = MeshClash.Self( set, int.MaxValue );
				var fresh = clash.Pairs.Where( p => !baseline.Contains( (p.TriangleA, p.TriangleB) ) ).ToList();
				set.Refit( mesh.Positions );

				var bend = new Bend
				{
					Bone = rig.Bones[b].Name,
					Degrees = deg,
					Axis = hinge,
					Radius = radius,
					VolumeRatio = MathF.Abs( before ) > 1e-9f ? after / before : float.NaN,
					NewCrossings = fresh.Count,
					Posed = keepPoses ? posed : null,
				};

				if ( fresh.Count > 0 )
				{
					var t = fresh[0].TriangleA;
					var c = (posed[set.Corners[t * 3]] + posed[set.Corners[t * 3 + 1]] + posed[set.Corners[t * 3 + 2]]) / 3f;
					bend.CrossingAt = TopologyReport.Where( c, name, min, max, rig );
				}

				result.Bends.Add( bend );
			}
		}

		return result;
	}

	/// <summary>Volume of the cone from <paramref name="apex"/> to the listed triangles. Unchanged
	/// by any rotation about the apex, so only stretching and collapsing move it.</summary>
	static float ConeVolume( TriangleSet set, List<int> triangles, IReadOnlyList<Vec3> positions, Vec3 apex )
	{
		var v = 0f;
		foreach ( var t in triangles )
		{
			var a = positions[set.Corners[t * 3]] - apex;
			var b = positions[set.Corners[t * 3 + 1]] - apex;
			var c = positions[set.Corners[t * 3 + 2]] - apex;
			v += Vec3.Dot( a, Vec3.Cross( b, c ) ) / 6f;
		}

		return v;
	}

	/// <summary>The bone and everything parented under it, by index.</summary>
	static bool[] Subtree( Skeleton rig, int root )
	{
		var inside = new bool[rig.Count];
		inside[root] = true;

		// Parents come before children in Effigy's skeletons, but not necessarily in an imported
		// one, so walk each bone up rather than relying on order.
		for ( var i = 0; i < rig.Count; i++ )
			for ( var p = i; p >= 0; p = rig.Bones[p].Parent )
				if ( p == root ) { inside[i] = true; break; }

		return inside;
	}

	static string N( float v ) => SectionReport.N( v );
	static string Inv( FormattableString s ) => s.ToString( CultureInfo.InvariantCulture );
}
