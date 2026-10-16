using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A belt's buckle: a rectangular frame with a centre bar, on the front of the band.
///
/// A belt without a buckle reads as a waistband. A jeans buckle is about 2.4 x 2 inches outside on
/// a 1.5-inch belt (the frame clears the strap by a quarter inch top and bottom), the frame bars a
/// quarter inch wide and about 3/16 deep. It is added to the belt's own mesh, on its slot, as its
/// own closed shell, so it moves with the belt and takes its weights.
/// </summary>
public static class GarmentBelt
{
	/// <summary>Add a buckle to <paramref name="belt"/> at the front of the band. Returns the
	/// buckle's centre, or null when the belt has no front to put it on.</summary>
	public static Vec3? AddBuckle( PolyMesh belt, int slot, float bandWidth = 1.5f )
	{
		if ( belt.VertexCount < 8 )
			return null;

		var cy = belt.Positions.Average( p => p.y );
		var front = belt.Positions.Where( p => MathF.Abs( p.y - cy ) < 1f ).DefaultIfEmpty( belt.Positions[0] ).OrderByDescending( p => p.x ).First();
		var zs = belt.Positions.Where( p => MathF.Abs( p.y - cy ) < 1.5f ).Select( p => p.z ).DefaultIfEmpty( front.z ).ToList();
		var centre = new Vec3( front.x + 0.1f, cy, (zs.Max() + zs.Min()) * 0.5f );

		var height = bandWidth + 0.5f;
		var width = height * 1.2f;
		const float Bar = 0.25f;
		const float Depth = 0.19f;

		void Box( Vec3 lo, Vec3 hi )
		{
			var b = belt.VertexCount;

			for ( var k = 0; k < 8; k++ )
				belt.AddVertex( new Vec3( (k & 1) == 0 ? lo.x : hi.x, (k & 2) == 0 ? lo.y : hi.y, (k & 4) == 0 ? lo.z : hi.z ) );

			// Outward-wound quads of an axis-aligned box.
			int[][] quads =
			{
				new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, // -z, +z
				new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, // -y, +y
				new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 }, // -x, +x
			};

			foreach ( var q in quads )
				belt.AddFace( q.Select( v => b + v ).ToArray(), new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, slot );
		}

		var x0 = centre.x;
		var x1 = centre.x + Depth;
		var hy = width * 0.5f;
		var hz = height * 0.5f;

		Box( new Vec3( x0, cy - hy, centre.z + hz - Bar ), new Vec3( x1, cy + hy, centre.z + hz ) );      // top
		Box( new Vec3( x0, cy - hy, centre.z - hz ), new Vec3( x1, cy + hy, centre.z - hz + Bar ) );      // bottom
		Box( new Vec3( x0, cy - hy, centre.z - hz + Bar ), new Vec3( x1, cy - hy + Bar, centre.z + hz - Bar ) ); // right side
		Box( new Vec3( x0, cy + hy - Bar, centre.z - hz + Bar ), new Vec3( x1, cy + hy, centre.z + hz - Bar ) ); // left side
		Box( new Vec3( x0, cy - Bar * 0.4f, centre.z - hz + Bar ), new Vec3( x1, cy + Bar * 0.4f, centre.z + hz - Bar ) ); // centre bar

		return centre;
	}

	/// <summary>The belt's length round, inches: the outer sheet's top opening, the way a tape goes
	/// round the band.</summary>
	public static float Round( PolyMesh belt )
	{
		var sheet = MeshSolidify.OuterSheet( belt, out _ ) ?? belt;
		var loops = GarmentTrim.OrderedBoundaryLoops( sheet );

		if ( loops.Count == 0 )
			return 0f;

		var loop = loops[^1];
		var round = 0f;

		for ( var i = 0; i < loop.Count; i++ )
			round += (sheet.Positions[loop[(i + 1) % loop.Count]] - sheet.Positions[loop[i]]).Length;

		return round;
	}
}
