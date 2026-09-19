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
/// SIX BONES PER JOINT AT MOST: each takes one face of the joint's cube. A joint with more is
/// how a real skeleton never looks, so the seventh bone is refused rather than fudged.
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
		if ( skeleton is null || skeleton.Count == 0 )
			throw new InvalidOperationException( "There are no bones to build a body on. Draw a rig first." );

		radiusOf ??= i => MathF.Max( skeleton.Bones[i].Length * 0.2f, 0.01f );

		// Joints: heads and tails, merged where a child's head sits on its parent's tail.
		var nodes = new List<Vec3>();
		var nodeRadius = new List<float>();
		int NodeAt( Vec3 p, float r )
		{
			for ( var i = 0; i < nodes.Count; i++ )
			{
				if ( (nodes[i] - p).LengthSquared < 1e-4f )
				{
					nodeRadius[i] = MathF.Max( nodeRadius[i], r );
					return i;
				}
			}

			nodes.Add( p );
			nodeRadius.Add( r );
			return nodes.Count - 1;
		}

		var bones = new List<(int head, int tail)>();
		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var head = skeleton.HeadWorld( b );
			var tail = skeleton.TailWorld( b );
			if ( (tail - head).LengthSquared < 1e-6f )
				continue;

			var r = MathF.Max( radiusOf( b ), 0.01f );
			bones.Add( (NodeAt( head, r ), NodeAt( tail, r )) );
		}

		if ( bones.Count == 0 )
			throw new InvalidOperationException( "Every bone has no length, so there is nothing to build along." );

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
				throw new InvalidOperationException( "A joint has more than six bones meeting at it, which is more than a body can be built round." );

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
}
