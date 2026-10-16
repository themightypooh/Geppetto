using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A body from a skeleton — Blender's Skin modifier, aimed at a rig instead of a wire. Every
/// joint becomes a cube the size of its radius, every bone a tube between the two cubes it joins,
/// and a couple of levels of Catmull-Clark turn the result into a smooth, all-quad blob that is
/// already a closed solid with a clean flow along each limb. The block-out a character starts
/// from, in one click, on the bones it will be rigged to.
///
/// SIX BONES PER JOINT AT MOST with cube joints: each takes one face of the joint's cube, and the
/// seventh is refused rather than fudged. <see cref="Joints.Hull"/> lifts that: the joint is the
/// convex hull of its bones' directions (<see cref="SkeletonCage"/>), so a hand with five fingers
/// and a wrist, or a spider's body, builds.
/// </summary>
public static class SkinBlockout
{
	/// <summary>
	/// Build the body. <paramref name="radiusOf"/> gives each bone's thickness, or null for a
	/// fifth of its length; <paramref name="smoothLevels"/> is how many times to subdivide the
	/// blocky skin (0 keeps the cubes and tubes, which is what to edit by hand).
	/// </summary>
	public static PolyMesh Build( Skeleton skeleton, Func<int, float> radiusOf = null, int smoothLevels = 2 )
	{
		Collect( skeleton, radiusOf, out var nodes, out var nodeRadius, out var bones );
		return BuildCubes( nodes, nodeRadius, bones, smoothLevels );
	}

	/// <summary>How the joints are made. Cube is the original: a cube per joint, six bones at most,
	/// and what every caller that does not ask gets, so its output never moves. Hull is the B-Mesh /
	/// SQM joint — see <see cref="SkeletonCage"/>: any number of bones, rings carried along each
	/// limb without twisting.</summary>
	public enum Joints { Cube, Hull }

	/// <summary>
	/// Build the body with the chosen <paramref name="joints"/>. Hull joints take
	/// <paramref name="sides"/> for limbs that touch no branching joint, and with
	/// <paramref name="fit"/> the smoothed body is pulled onto the round-cone shape of the bones,
	/// so each bone's radius is the thickness it ends up with.
	/// </summary>
	public static PolyMesh Build( Skeleton skeleton, Func<int, float> radiusOf, int smoothLevels, Joints joints, int sides = 8, bool fit = false )
	{
		Collect( skeleton, radiusOf, out var nodes, out var nodeRadius, out var bones );
		if ( joints == Joints.Cube )
			return BuildCubes( nodes, nodeRadius, bones, smoothLevels );

		// A skeleton is a tree, but joints welded by position can close a loop (two bones that end
		// on the same spot). The cage is built on the spanning tree; every joint is still in it.
		var graph = new SkeletonCage.Graph();
		for ( var i = 0; i < nodes.Count; i++ )
		{
			graph.Nodes.Add( nodes[i] );
			graph.Radii.Add( nodeRadius[i] );
		}

		var root = new int[nodes.Count];
		for ( var i = 0; i < root.Length; i++ )
			root[i] = i;
		int Find( int x ) { while ( root[x] != x ) x = root[x] = root[root[x]]; return x; }

		foreach ( var (head, tail) in bones )
		{
			if ( head == tail || Find( head ) == Find( tail ) )
				continue;

			root[Find( head )] = Find( tail );
			graph.AddEdge( head, tail );
		}

		return SkeletonCage.Build( graph, new SkeletonCage.Options { Sides = sides, SmoothLevels = smoothLevels, Fit = fit } );
	}

	/// <summary>The joints and the spans between them: one node per bone end, merged where a child's
	/// head sits on its parent's tail.</summary>
	static void Collect( Skeleton skeleton, Func<int, float> radiusOf, out List<Vec3> nodes, out List<float> nodeRadius, out List<(int head, int tail)> bones )
	{
		if ( skeleton is null || skeleton.Count == 0 )
			throw new InvalidOperationException( "There are no bones to build a body on. Draw a rig first." );

		// Length is an inch on a citizen bone, and the limb is a foot long. The default thickness
		// follows the segment that is actually built — see TryBodySpan — unless the caller set one.
		radiusOf ??= i => DefaultRadius( skeleton, i );

		// Joints: heads and tails, merged where a child's head sits on its parent's tail.
		var nodeList = new List<Vec3>();
		var radiusList = new List<float>();
		int NodeAt( Vec3 p, float r )
		{
			for ( var i = 0; i < nodeList.Count; i++ )
			{
				if ( (nodeList[i] - p).LengthSquared < 1e-4f )
				{
					radiusList[i] = MathF.Max( radiusList[i], r );
					return i;
				}
			}

			nodeList.Add( p );
			radiusList.Add( r );
			return nodeList.Count - 1;
		}

		// One node index per bone end, so a child that does not sit on its parent's tail can
		// still be bridged to the parent's joint. Auxiliary bones are not nodes: a twist is a
		// station on the arm, and building it makes a second arm.
		var headNode = new int[skeleton.Count];
		var tailNode = new int[skeleton.Count];
		for ( var n = 0; n < skeleton.Count; n++ )
		{
			headNode[n] = -1;
			tailNode[n] = -1;
		}

		var spans = new List<(int head, int tail)>();
		for ( var b = 0; b < skeleton.Count; b++ )
		{
			if ( !skeleton.TryBodySpan( b, out var head, out var tail ) )
				continue;

			var r = MathF.Max( radiusOf( b ), 0.01f );
			headNode[b] = NodeAt( head, r );

			// A fan joint (pelvis, hand) keeps a short authored tail that points nowhere. The
			// children are bridged below; a stub beside them is a wart on the joint.
			var fan = false;
			foreach ( var c in skeleton.Children( b ) )
			{
				if ( Skeleton.IsAuxiliaryBone( skeleton.Bones[c].Name ) || headNode[c] == headNode[b] )
					continue;

				if ( !skeleton.TryBodySpan( c, out var childHead, out _ ) )
					continue;

				if ( (childHead - head).LengthSquared > 1e-4f && (childHead - tail).LengthSquared > 1e-4f )
					fan = true;
			}

			if ( fan && (tail - head).Length < 2f )
				continue;

			tailNode[b] = NodeAt( tail, r );
			spans.Add( (headNode[b], tailNode[b]) );
		}

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			if ( headNode[b] < 0 )
				continue;

			foreach ( var c in skeleton.Children( b ) )
			{
				if ( headNode[c] < 0 || headNode[c] == headNode[b] || headNode[c] == tailNode[b] )
					continue;

				spans.Add( (headNode[b], headNode[c]) );
			}
		}

		if ( spans.Count == 0 )
			throw new InvalidOperationException( "Every bone has no length, so there is nothing to build along." );

		nodes = nodeList;
		nodeRadius = radiusList;
		bones = spans;
	}

	static PolyMesh BuildCubes( List<Vec3> nodes, List<float> nodeRadius, List<(int head, int tail)> bones, int smoothLevels )
	{
		// A cube per joint: eight corners, six faces, each face free until a bone claims it.
		var mesh = new PolyMesh();
		var cubeCorner = new int[nodes.Count][];
		var faceTaken = new bool[nodes.Count][];
		for ( var n = 0; n < nodes.Count; n++ )
		{
			var c = nodes[n];
			var r = nodeRadius[n];
			cubeCorner[n] = new int[8];
			for ( var k = 0; k < 8; k++ )
			{
				cubeCorner[n][k] = mesh.Positions.Count;
				mesh.Positions.Add( c + new Vec3( (k & 1) == 0 ? -r : r, (k & 2) == 0 ? -r : r, (k & 4) == 0 ? -r : r ) );
			}

			faceTaken[n] = new bool[6];
		}

		// The six faces of a cube, wound outward, and the way each looks.
		var faceNormals = new[] { new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, -1, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 0, -1 ) };
		var faceCorners = new[]
		{
			new[] { 1, 3, 7, 5 },   // +x
			new[] { 0, 4, 6, 2 },   // -x
			new[] { 2, 6, 7, 3 },   // +y
			new[] { 0, 1, 5, 4 },   // -y
			new[] { 4, 5, 7, 6 },   // +z
			new[] { 0, 2, 3, 1 },   // -z
		};

		int Claim( int node, Vec3 direction )
		{
			var best = -1;
			var bestDot = float.MinValue;
			for ( var f = 0; f < 6; f++ )
			{
				if ( faceTaken[node][f] )
					continue;

				var d = Vec3.Dot( faceNormals[f], direction );
				if ( d > bestDot )
				{
					bestDot = d;
					best = f;
				}
			}

			if ( best < 0 )
				throw new InvalidOperationException( "A joint has more than six bones meeting at it, which is more than a cube joint can take. Build it with hull joints instead, which take any number." );

			faceTaken[node][best] = true;
			return best;
		}

		// Tubes: each bone claims a face at each end and bridges them.
		foreach ( var (head, tail) in bones )
		{
			var axis = (nodes[tail] - nodes[head]).Normal;
			var fa = Claim( head, axis );
			var fb = Claim( tail, axis * -1f );

			var a = new int[4];
			var b = new int[4];
			for ( var i = 0; i < 4; i++ )
			{
				a[i] = cubeCorner[head][faceCorners[fa][i]];
				b[i] = cubeCorner[tail][faceCorners[fb][3 - i]];
			}

			// Turn the far ring so its first corner is the one nearest the near ring's first,
			// measured across the axis, so the tube does not twist.
			var start = 0;
			var bestD = float.MaxValue;
			var pa = mesh.Positions[a[0]] - axis * Vec3.Dot( mesh.Positions[a[0]] - nodes[head], axis );
			for ( var j = 0; j < 4; j++ )
			{
				var pb = mesh.Positions[b[j]] - axis * Vec3.Dot( mesh.Positions[b[j]] - nodes[head], axis );
				var d = (pb - pa).LengthSquared;
				if ( d < bestD )
				{
					bestD = d;
					start = j;
				}
			}

			for ( var i = 0; i < 4; i++ )
			{
				var i2 = (i + 1) % 4;
				var j = (start + i) % 4;
				var j2 = (start + i + 1) % 4;
				mesh.AddFace( new[] { a[i], a[i2], b[j2], b[j] } );
			}
		}

		// Whatever faces no bone claimed stay: they cap the ends of limbs and close the joints.
		for ( var n = 0; n < nodes.Count; n++ )
		{
			for ( var f = 0; f < 6; f++ )
			{
				if ( faceTaken[n][f] )
					continue;

				var idx = new int[4];
				for ( var i = 0; i < 4; i++ )
					idx[i] = cubeCorner[n][faceCorners[f][i]];
				mesh.AddFace( idx );
			}
		}

		return smoothLevels > 0 ? CatmullClark.Subdivide( mesh, smoothLevels ) : mesh;
	}

	/// <summary>
	/// A fifth of the segment that is actually built. On a citizen bone that is the distance to
	/// the next joint, not the authored inch. A head's children are eyes and ears, which are not
	/// part of the span, so the skull takes its size from them. A fan joint's own span is that
	/// authored inch; it fills out toward the children that leave it.
	/// </summary>
	static float DefaultRadius( Skeleton skeleton, int bone )
	{
		if ( !skeleton.TryBodySpan( bone, out var head, out var tail ) )
			return 0.01f;

		var span = (tail - head).Length;
		var reach = span;
		var face = 0f;
		var headBone = skeleton.Bones[bone].Name.Contains( "head", StringComparison.OrdinalIgnoreCase );

		foreach ( var c in skeleton.Children( bone ) )
		{
			var d = (skeleton.HeadWorld( c ) - head).Length;

			if ( d < 1e-3f )
				continue;

			var child = skeleton.Bones[c].Name;

			// Eyes and ears say how big a skull is. An IK target on the hand is forty inches
			// away and must not do the same job, or the palm becomes a crate.
			if ( headBone && IsFaceBone( child ) )
				face = MathF.Max( face, d );
			else if ( !Skeleton.IsAuxiliaryBone( child ) )
				reach = MathF.Max( reach, d );
		}

		var r = span * 0.2f;

		if ( face > span )
			r = MathF.Max( r, face * 0.42f );

		// A fan joint's own span is the short authored tail. Fill it out toward the children
		// that leave it, or a pelvis is an inch cube between two legs.
		if ( reach > span + 0.5f && span < 2f )
			r = MathF.Max( r, reach * 0.35f );

		// Spine bones are short, so a fifth of one is a stick. The trunk wants the width of
		// the ribs, which a spine segment does not carry.
		var name = skeleton.Bones[bone].Name;
		if ( name.Contains( "pelvis", StringComparison.OrdinalIgnoreCase )
			|| name.Contains( "spine", StringComparison.OrdinalIgnoreCase )
			|| name.Contains( "chest", StringComparison.OrdinalIgnoreCase ) )
			r = MathF.Max( r, 3.2f );

		return MathF.Max( r, 0.01f );
	}

	static bool IsFaceBone( string name )
	{
		var n = name.ToLowerInvariant();
		return n.Contains( "eye" ) || n.Contains( "ear" ) || n.Contains( "lid" )
			|| n.Contains( "face" ) || n.Contains( "jaw" );
	}
}
