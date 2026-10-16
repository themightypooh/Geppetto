using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// How a mesh is built, in numbers an agent can act on: what its faces are, where its poles sit,
/// how even its edges are, how square its quads are, whether it is manifold and symmetric, and
/// how many loops ring each joint of the rig.
///
/// THE CHECKLIST IS A CRAFT TABLE, NOT OUR INVENTION. Each line is a check from the modelling
/// research's "quantitative checks an agent can compute" (craft.md §7.2) with its target beside
/// it: quads at least 85% overall, no n-gons, no valence-6 poles, poles more than 1.5 local edge
/// lengths from a joint, edge-length CV under 0.4, quads no longer than 3:1 and skewed less than
/// 20° on average, symmetric to 0.5% of the bounds, and at least three loops at every joint.
/// A line that misses says so with a "!", so a report is read for its marks, not re-derived.
///
/// MEASURED ON THE WELDED SURFACE. A primitive's corners are split for its UVs and hard edges;
/// counting those splits as borders would report a closed box as 12 open edges. Positions within
/// a hair of each other are one vertex here; the mesh itself is not changed.
///
/// LANDMARK-RELATIVE PLACES. A pole at (3.18, -1.02, 41.7) means nothing to the person reading
/// it; "@Forearm_L+0.4,0,-0.3" does, and it is also a point a build script takes back, so a
/// report line can be pasted straight into a `sculpt` or `edit` that fixes it.
/// </summary>
public static class TopologyReport
{
	public sealed class Pole
	{
		public int Vertex;
		public int Valence;
		public Vec3 Position;
		public string Where = "";

		/// <summary>Distance to the nearest bone head, or +inf without a rig.</summary>
		public float JointDistance = float.PositiveInfinity;
		public string Joint;

		/// <summary>Mean length of the edges at this vertex: the "local edge length" the joint rule is in.</summary>
		public float LocalEdge;

		public bool NearJoint => JointDistance < 1.5f * LocalEdge;
	}

	public sealed class JointLoops
	{
		public string Bone = "";
		public float Radius;
		public int Loops;

		/// <summary>The rings counted along each side of the limb (eight half-planes round the bone).</summary>
		public int[] PerSide = Array.Empty<int>();
	}

	public sealed class Result
	{
		public string Name = "";
		public int Vertices, Faces, Triangles, Quads, Ngons, Degenerate;
		public int Edges, BoundaryEdges, NonManifoldEdges, LooseVertices;
		public int Valence3, Valence5, Valence6Plus, BoundaryPoles;
		public List<Pole> Poles = new();

		public float EdgeMean, EdgeCv, EdgeMin, EdgeMax;
		public float AspectMean, AspectP90, AspectMax;
		public int AspectOver3;
		public float SkewMean, SkewP90, SkewMax;
		public float TriMinAngle = float.NaN;
		public int TrisUnder15;

		/// <summary>Left-right (y = 0 plane) Hausdorff distance and where it is worst; NaN when the
		/// mesh does not straddle y = 0 (one side of a pair, a prop off to the side).</summary>
		public float SymmetryHausdorff = float.NaN, SymmetryMean = float.NaN, Diagonal;
		public string SymmetryWorst = "";

		public List<JointLoops> Joints = new();
		public string JointNote;

		public float QuadPercent => Faces == 0 ? 0f : 100f * Quads / Faces;
		public float TriPercent => Faces == 0 ? 0f : 100f * Triangles / Faces;
		public float NgonPercent => Faces == 0 ? 0f : 100f * Ngons / Faces;
		public int PoleCount => Valence3 + Valence5 + Valence6Plus;

		public override string ToString() => Describe();

		public string Describe( int maxPoles = 12 )
		{
			var sb = new StringBuilder();
			string Mark( bool ok ) => ok ? " " : "!";
			void Line( bool ok, FormattableString text ) => sb.Append( Mark( ok ) ).Append( ' ' ).AppendLine( text.ToString( CultureInfo.InvariantCulture ) );

			sb.AppendLine( Inv( $"{Name}: topology — {Vertices} vertices, {Faces} faces, {Edges} edges (welded)" ) );
			Line( Faces == 0 || QuadPercent >= 85f, $"faces: {N( QuadPercent )}% quads, {N( TriPercent )}% triangles, {N( NgonPercent )}% n-gons ({Quads} / {Triangles} / {Ngons}); target ≥ 85% quads" );
			Line( Ngons == 0, $"n-gons: {Ngons}; target 0" );
			if ( Degenerate > 0 ) Line( false, $"degenerate faces (no area or a repeated corner): {Degenerate}; target 0" );
			Line( NonManifoldEdges == 0, $"non-manifold edges (3+ faces): {NonManifoldEdges}; open edges: {BoundaryEdges}; loose vertices: {LooseVertices}" );

			var nearJoint = Poles.Count( p => p.NearJoint );
			Line( Valence6Plus == 0 && nearJoint == 0, $"poles (interior, valence ≠ 4): {PoleCount} — {Valence3} three-edge, {Valence5} five-edge, {Valence6Plus} six-or-more{(BoundaryPoles > 0 ? $"; {BoundaryPoles} on open borders" : "")}; target: valence 6+ = 0, none within 1.5 edge lengths of a joint ({nearJoint} are)" );

			foreach ( var pole in Poles.OrderByDescending( p => p.Valence >= 6 ).ThenByDescending( p => p.NearJoint ).ThenBy( p => p.Vertex ).Take( maxPoles ) )
			{
				sb.Append( Inv( $"    v{pole.Valence} at {pole.Where}" ) );
				if ( pole.Joint is not null ) sb.Append( Inv( $" — {N( pole.JointDistance )} from {pole.Joint} ({N( pole.JointDistance / MathF.Max( pole.LocalEdge, 1e-6f ) )} edge lengths){(pole.NearJoint ? " ! on the joint" : "")}" ) );
				sb.AppendLine();
			}

			if ( Poles.Count > maxPoles ) sb.AppendLine( Inv( $"    … {Poles.Count - maxPoles} more" ) );

			Line( EdgeCv < 0.4f, $"edge length: mean {N( EdgeMean )}, min {N( EdgeMin )}, max {N( EdgeMax )}, CV {N( EdgeCv )}; target CV < 0.4 (whole body: regions of different density raise it)" );

			if ( Quads > 0 )
			{
				Line( AspectP90 <= 3f, $"quad aspect (longest ÷ shortest edge): mean {N( AspectMean )}, 90th percentile {N( AspectP90 )}, max {N( AspectMax )}, {AspectOver3} over 3; target ≤ 3 (≤ 4 tapering)" );
				Line( SkewMean < 20f && SkewMax < 45f, $"quad skew (corner angle off 90°): mean {N( SkewMean )}°, 90th percentile {N( SkewP90 )}°, max {N( SkewMax )}°; target mean < 20°, max < 45°" );
			}

			if ( Triangles > 0 )
				Line( TriMinAngle >= 15f, $"triangles: smallest angle {N( TriMinAngle )}°, {TrisUnder15} under 15°; target ≥ 15°" );

			if ( float.IsNaN( SymmetryHausdorff ) )
				sb.AppendLine( "  symmetry: the mesh does not straddle y = 0, so there is no mirror to compare (one side of a pair?)" );
			else
				Line( SymmetryHausdorff <= 0.005f * Diagonal, $"symmetry across y = 0: Hausdorff {N( SymmetryHausdorff )} ({N( 100f * SymmetryHausdorff / MathF.Max( Diagonal, 1e-6f ) )}% of the bounds' diagonal), mean {N( SymmetryMean )}, worst at {SymmetryWorst}; target ≤ 0.5%" );

			if ( JointNote is not null )
				sb.AppendLine( "  loops per joint: " + JointNote );

			foreach ( var j in Joints )
				Line( j.Loops >= 3, $"loops at {j.Bone}: {j.Loops} within ±{N( j.Radius )} (one limb radius) of the joint; sides read {string.Join( ",", j.PerSide )}; target ≥ 3" );

			return sb.ToString().TrimEnd();
		}
	}

	/// <summary>
	/// Measure <paramref name="mesh"/>. With a <paramref name="rig"/>, poles are also placed against
	/// the joints and every bone whose head is inside this mesh gets a loop count. Places are named
	/// against the rig's bones, else against <paramref name="name"/>'s sides.
	/// </summary>
	public static Result Measure( PolyMesh mesh, string name = null, Skeleton rig = null )
	{
		var result = new Result { Name = name ?? "mesh" };

		if ( mesh is null || mesh.FaceCount == 0 )
			return result;

		var (min, max) = ModelMeasure.Bounds( mesh );
		result.Diagonal = (max - min).Length;
		var weld = MathF.Max( result.Diagonal * 1e-6f, 1e-7f );

		// Weld.
		var map = new int[mesh.VertexCount];
		var positions = new List<Vec3>();
		var ids = new Dictionary<(long, long, long), int>();
		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			var key = ((long)MathF.Round( p.x / weld ), (long)MathF.Round( p.y / weld ), (long)MathF.Round( p.z / weld ));
			if ( !ids.TryGetValue( key, out var id ) ) { id = positions.Count; ids[key] = id; positions.Add( p ); }
			map[i] = id;
		}

		var faces = new List<int[]>( mesh.FaceCount );
		foreach ( var face in mesh.Faces )
		{
			var ring = new List<int>( face.Count );
			foreach ( var index in face.Indices )
			{
				var w = map[index];
				if ( ring.Count == 0 || ring[^1] != w ) ring.Add( w );
			}
			if ( ring.Count > 1 && ring[^1] == ring[0] ) ring.RemoveAt( ring.Count - 1 );

			if ( ring.Count < 3 || ring.Distinct().Count() != ring.Count || mesh.FaceArea( face ) <= 1e-12f )
			{
				result.Degenerate++;
				if ( ring.Count < 3 || ring.Distinct().Count() != ring.Count ) continue;
			}

			faces.Add( ring.ToArray() );
		}

		result.Vertices = positions.Count;
		result.Faces = faces.Count;
		result.Triangles = faces.Count( f => f.Length == 3 );
		result.Quads = faces.Count( f => f.Length == 4 );
		result.Ngons = faces.Count( f => f.Length > 4 );

		// Edges, valence, borders.
		var edges = new Dictionary<(int, int), int>();
		foreach ( var f in faces )
			for ( var i = 0; i < f.Length; i++ )
			{
				var a = f[i]; var b = f[(i + 1) % f.Length];
				var key = a < b ? (a, b) : (b, a);
				edges[key] = edges.TryGetValue( key, out var c ) ? c + 1 : 1;
			}

		result.Edges = edges.Count;
		var valence = new int[positions.Count];
		var onBorder = new bool[positions.Count];
		var edgeSum = new float[positions.Count];
		var lengths = new List<float>( edges.Count );

		foreach ( var ((a, b), count) in edges )
		{
			valence[a]++; valence[b]++;
			var len = (positions[a] - positions[b]).Length;
			edgeSum[a] += len; edgeSum[b] += len;
			lengths.Add( len );
			if ( count == 1 ) { result.BoundaryEdges++; onBorder[a] = onBorder[b] = true; }
			else if ( count > 2 ) result.NonManifoldEdges++;
		}

		result.LooseVertices = valence.Count( v => v == 0 );

		if ( lengths.Count > 0 )
		{
			var mean = lengths.Average();
			var variance = lengths.Sum( l => (l - mean) * (l - mean) ) / lengths.Count;
			result.EdgeMean = mean;
			result.EdgeCv = mean > 0f ? MathF.Sqrt( variance ) / mean : 0f;
			result.EdgeMin = lengths.Min();
			result.EdgeMax = lengths.Max();
		}

		var joints = rig is { Count: > 0 }
			? Enumerable.Range( 0, rig.Count ).Where( b => !Skeleton.IsAuxiliaryBone( rig.Bones[b].Name ) ).Select( b => (rig.Bones[b].Name, rig.HeadWorld( b )) ).ToList()
			: new List<(string, Vec3)>();

		for ( var v = 0; v < positions.Count; v++ )
		{
			if ( valence[v] == 0 ) continue;
			if ( onBorder[v] ) { if ( valence[v] > 3 || valence[v] < 2 ) result.BoundaryPoles++; continue; }
			if ( valence[v] == 4 ) continue;

			if ( valence[v] == 3 ) result.Valence3++;
			else if ( valence[v] == 5 ) result.Valence5++;
			else if ( valence[v] >= 6 ) result.Valence6Plus++;
			else continue;

			var pole = new Pole { Vertex = v, Valence = valence[v], Position = positions[v], LocalEdge = edgeSum[v] / valence[v] };
			pole.Where = Where( positions[v], name, min, max, rig );

			foreach ( var (bone, head) in joints )
			{
				var d = (positions[v] - head).Length;
				if ( d < pole.JointDistance ) { pole.JointDistance = d; pole.Joint = bone; }
			}

			result.Poles.Add( pole );
		}

		QuadShape( faces, positions, result );
		Symmetry( mesh, positions, name, min, max, rig, result );

		if ( rig is { Count: > 0 } )
			CountJointLoops( mesh, rig, result );
		else
			result.JointNote = "no rig, so no joints to count loops at (add bones to get this line)";

		return result;
	}

	static void QuadShape( List<int[]> faces, List<Vec3> positions, Result result )
	{
		var aspects = new List<float>();
		var skews = new List<float>();
		var triMin = float.PositiveInfinity;

		foreach ( var f in faces )
		{
			if ( f.Length == 4 )
			{
				float shortest = float.MaxValue, longest = 0f;
				for ( var i = 0; i < 4; i++ )
				{
					var len = (positions[f[(i + 1) % 4]] - positions[f[i]]).Length;
					shortest = MathF.Min( shortest, len ); longest = MathF.Max( longest, len );
					skews.Add( MathF.Abs( Angle( positions[f[(i + 3) % 4]], positions[f[i]], positions[f[(i + 1) % 4]] ) - 90f ) );
				}
				aspects.Add( shortest > 1e-9f ? longest / shortest : 1e6f );
			}
			else if ( f.Length == 3 )
			{
				for ( var i = 0; i < 3; i++ )
					triMin = MathF.Min( triMin, Angle( positions[f[(i + 2) % 3]], positions[f[i]], positions[f[(i + 1) % 3]] ) );
				result.TrisUnder15 += Enumerable.Range( 0, 3 ).Any( i => Angle( positions[f[(i + 2) % 3]], positions[f[i]], positions[f[(i + 1) % 3]] ) < 15f ) ? 1 : 0;
			}
		}

		if ( aspects.Count > 0 )
		{
			aspects.Sort();
			result.AspectMean = aspects.Average();
			result.AspectP90 = aspects[(int)MathF.Min( aspects.Count - 1, MathF.Floor( aspects.Count * 0.9f ) )];
			result.AspectMax = aspects[^1];
			result.AspectOver3 = aspects.Count( a => a > 3f );
		}

		if ( skews.Count > 0 )
		{
			skews.Sort();
			result.SkewMean = skews.Average();
			result.SkewP90 = skews[(int)MathF.Min( skews.Count - 1, MathF.Floor( skews.Count * 0.9f ) )];
			result.SkewMax = skews[^1];
		}

		if ( !float.IsPositiveInfinity( triMin ) )
			result.TriMinAngle = triMin;
	}

	static float Angle( Vec3 prev, Vec3 at, Vec3 next )
	{
		var a = (prev - at).Normal;
		var b = (next - at).Normal;
		return MathF.Acos( Math.Clamp( Vec3.Dot( a, b ), -1f, 1f ) ) * 180f / MathF.PI;
	}

	/// <summary>Mirror every vertex across y = 0 and find the surface nearest it: the largest of
	/// those distances is the (symmetric) Hausdorff distance between the mesh and its mirror.</summary>
	static void Symmetry( PolyMesh mesh, List<Vec3> positions, string name, Vec3 min, Vec3 max, Skeleton rig, Result result )
	{
		var width = max.y - min.y;
		if ( min.y > -0.05f * width || max.y < 0.05f * width )
			return;

		var set = TriangleSet.From( mesh );
		var worst = -1f;
		var worstAt = Vec3.Zero;
		var total = 0f;

		foreach ( var p in positions )
		{
			var mirrored = new Vec3( p.x, -p.y, p.z );
			var d = set.Nearest( mirrored ).Distance;
			total += d;
			if ( d > worst ) { worst = d; worstAt = p; }
		}

		result.SymmetryHausdorff = MathF.Max( worst, 0f );
		result.SymmetryMean = positions.Count > 0 ? total / positions.Count : 0f;
		result.SymmetryWorst = Where( worstAt, name, min, max, rig );
	}

	/// <summary>
	/// Loops per joint: for each bone whose head is inside this mesh, the edge rings within one
	/// limb radius either side of the head. Cut the mesh lengthwise with eight half-planes round
	/// the bone (offset by an odd angle so none runs exactly along an edge), and count where each
	/// meets an edge inside the band on the limb's surface: each edge met there is one ring
	/// crossing that side. The median side is the count, so one side running through a pole or a
	/// stray diagonal does not decide it.
	/// </summary>
	static void CountJointLoops( PolyMesh mesh, Skeleton rig, Result result )
	{
		var weld = MathF.Max( mesh.BoundsDiagonal * 1e-5f, 1e-6f );

		for ( var b = 0; b < rig.Count; b++ )
		{
			if ( Skeleton.IsAuxiliaryBone( rig.Bones[b].Name ) )
				continue;

			if ( !JointFrame( mesh, rig, b, out var head, out var dir, out var radius ) )
				continue;

			var across = SectionReport.InPlane( dir, rig.WorldBind( b ).X );
			var around = Vec3.Cross( dir, across ).Normal;
			var sides = new List<int>();

			for ( var k = 0; k < 4; k++ )
			{
				var angle = (7.3f + 45f * k) * MathF.PI / 180f;
				var inPlane = across * MathF.Cos( angle ) + around * MathF.Sin( angle );
				var normal = Vec3.Cross( dir, inPlane ).Normal;
				var points = new List<Vec3>();
				var seen = new HashSet<(long, long, long)>();

				foreach ( var (a, c) in MeshSection.CrossSection( mesh, head, normal ) )
					foreach ( var p in new[] { a, c } )
					{
						var key = ((long)MathF.Round( p.x / weld ), (long)MathF.Round( p.y / weld ), (long)MathF.Round( p.z / weld ));
						if ( seen.Add( key ) ) points.Add( p );
					}

				int pos = 0, neg = 0;
				foreach ( var p in points )
				{
					var rel = p - head;
					var along = Vec3.Dot( rel, dir );
					if ( MathF.Abs( along ) > radius ) continue;
					var off = rel - dir * along;
					var r = off.Length;
					if ( r < 0.25f * radius || r > 1.75f * radius ) continue;
					if ( Vec3.Dot( off, inPlane ) > 0f ) pos++; else neg++;
				}

				sides.Add( pos );
				sides.Add( neg );
			}

			var sorted = sides.OrderBy( s => s ).ToList();
			result.Joints.Add( new JointLoops
			{
				Bone = rig.Bones[b].Name,
				Radius = radius,
				Loops = sorted[sorted.Count / 2],
				PerSide = sides.ToArray(),
			} );
		}

		if ( result.Joints.Count == 0 )
			result.JointNote = "no bone's head is inside this mesh";
	}

	/// <summary>
	/// The joint at bone <paramref name="bone"/>'s head as this mesh sees it: the head, the bone's
	/// direction, and the limb's radius there — from the outline of the cross-section through the
	/// head that encloses it (the radius of a circle of the same area). False when no outline of
	/// this mesh encloses the head: the bone is not in this body.
	/// </summary>
	public static bool JointFrame( PolyMesh mesh, Skeleton rig, int bone, out Vec3 head, out Vec3 dir, out float radius )
	{
		head = rig.HeadWorld( bone );
		dir = rig.BoneDirection( bone );
		radius = 0f;

		var slice = SectionReport.Cut( mesh, head, dir );
		var loop = SectionReport.Containing( slice, head );

		if ( loop is null || loop.Area <= 0f )
			return false;

		radius = MathF.Sqrt( loop.Area / MathF.PI );
		return radius > 0f;
	}

	/// <summary>
	/// A place said against the nearest landmark, in the script's own point syntax: the nearest
	/// bone head or tail when there is a rig, else the nearest side of the body's bounds.
	/// </summary>
	public static string Where( Vec3 p, string bodyName, Vec3 min, Vec3 max, Skeleton rig )
	{
		var best = float.MaxValue;
		var label = "";
		var at = Vec3.Zero;

		void Try( string name, Vec3 point )
		{
			var d = (p - point).Length;
			if ( d < best ) { best = d; label = name; at = point; }
		}

		if ( rig is { Count: > 0 } )
		{
			// Heads first: a parent's tail usually sits on its child's head, and the joint is
			// better named by the bone that bends there.
			for ( var b = 0; b < rig.Count; b++ )
				if ( !Skeleton.IsAuxiliaryBone( rig.Bones[b].Name ) )
					Try( rig.Bones[b].Name, rig.HeadWorld( b ) );

			for ( var b = 0; b < rig.Count; b++ )
				if ( !Skeleton.IsAuxiliaryBone( rig.Bones[b].Name ) )
					Try( rig.Bones[b].Name + ".tail", rig.TailWorld( b ) );
		}

		if ( label.Length == 0 && bodyName is not null )
		{
			var c = (min + max) * 0.5f;
			Try( bodyName + ".top", new Vec3( c.x, c.y, max.z ) );
			Try( bodyName + ".bottom", new Vec3( c.x, c.y, min.z ) );
			Try( bodyName + ".front", new Vec3( max.x, c.y, c.z ) );
			Try( bodyName + ".back", new Vec3( min.x, c.y, c.z ) );
			Try( bodyName + ".left", new Vec3( c.x, max.y, c.z ) );
			Try( bodyName + ".right", new Vec3( c.x, min.y, c.z ) );
			Try( bodyName, c );
		}

		if ( label.Length == 0 )
			return SectionReport.P( p );

		var o = p - at;
		return Inv( $"@{label}+{N( o.x )},{N( o.y )},{N( o.z )}" );
	}

	static string N( float v ) => SectionReport.N( v );
	static string Inv( FormattableString s ) => s.ToString( CultureInfo.InvariantCulture );
}
