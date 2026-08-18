using System;
using System.Collections.Generic;

namespace ModelKernel;

/// <summary>
/// Welds vertices by position so shared corners become one index rather than several coincident
/// ones.
///
/// This matters more than it looks. A mesh built face-by-face without welding is topologically a
/// pile of loose quads — every edge reads as a boundary, Catmull-Clark treats the whole thing as
/// border, and the result falls apart into disconnected sheets. Anything constructed here goes
/// through the welder.
/// </summary>
sealed class VertexWelder
{
	readonly Dictionary<(long, long, long), int> _map = new();
	readonly PolyMesh _mesh;
	readonly float _scale;

	public VertexWelder( PolyMesh mesh, float tolerance = 1e-5f )
	{
		_mesh = mesh;
		_scale = 1f / tolerance;
	}

	public int Add( Vec3 p )
	{
		// Quantising to an integer lattice, rather than comparing floats pairwise, keeps this O(1)
		// per vertex. Two positions closer than the tolerance land in the same bucket.
		var key = (
			(long)MathF.Round( p.x * _scale ),
			(long)MathF.Round( p.y * _scale ),
			(long)MathF.Round( p.z * _scale ));

		if ( _map.TryGetValue( key, out var existing ) )
			return existing;

		var index = _mesh.AddVertex( p );
		_map[key] = index;
		return index;
	}
}

/// <summary>
/// Parametric primitives, all quad-dominant on purpose.
///
/// QUADS ARE A HARD REQUIREMENT, NOT A PREFERENCE. These feed Catmull-Clark, which turns clean
/// quads into a clean subdivision surface and turns triangle soup into a lumpy one. That is why
/// there is no UV sphere here — its pole fans are triangles and they pinch visibly under a sculpt
/// brush. QuadSphere costs nothing extra and has no poles.
///
/// Every primitive is closed and manifold, which is what lets collision fall out of the primitive
/// list later instead of needing convex decomposition of the triangles.
/// </summary>
public static class Primitives
{
	/// <summary>Axis-aligned box. 8 vertices, 6 quads, one UV island per face.</summary>
	public static PolyMesh Box( float sizeX = 1f, float sizeY = 1f, float sizeZ = 1f, int material = 0 )
	{
		var hx = sizeX * 0.5f;
		var hy = sizeY * 0.5f;
		var hz = sizeZ * 0.5f;

		var m = new PolyMesh();

		//   0..3 bottom (z-), 4..7 top (z+), counter-clockwise seen from +Z
		m.AddVertex( new Vec3( -hx, -hy, -hz ) );
		m.AddVertex( new Vec3( hx, -hy, -hz ) );
		m.AddVertex( new Vec3( hx, hy, -hz ) );
		m.AddVertex( new Vec3( -hx, hy, -hz ) );
		m.AddVertex( new Vec3( -hx, -hy, hz ) );
		m.AddVertex( new Vec3( hx, -hy, hz ) );
		m.AddVertex( new Vec3( hx, hy, hz ) );
		m.AddVertex( new Vec3( -hx, hy, hz ) );

		var uv = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };

		// Winding is chosen so each face's Newell normal points out of the box. Verified by test.
		m.AddFace( new[] { 0, 3, 2, 1 }, (Vec2[])uv.Clone(), material ); // -Z
		m.AddFace( new[] { 4, 5, 6, 7 }, (Vec2[])uv.Clone(), material ); // +Z
		m.AddFace( new[] { 0, 1, 5, 4 }, (Vec2[])uv.Clone(), material ); // -Y
		m.AddFace( new[] { 1, 2, 6, 5 }, (Vec2[])uv.Clone(), material ); // +X
		m.AddFace( new[] { 2, 3, 7, 6 }, (Vec2[])uv.Clone(), material ); // +Y
		m.AddFace( new[] { 3, 0, 4, 7 }, (Vec2[])uv.Clone(), material ); // -X

		return m;
	}

	/// <summary>Flat grid in XY. Open — this is the one primitive with a boundary, which makes it
	/// the useful case for testing the boundary rules in Catmull-Clark.</summary>
	public static PolyMesh Plane( float sizeX = 1f, float sizeY = 1f, int segmentsX = 1, int segmentsY = 1, int material = 0 )
	{
		segmentsX = Math.Max( 1, segmentsX );
		segmentsY = Math.Max( 1, segmentsY );

		var m = new PolyMesh();

		for ( var iy = 0; iy <= segmentsY; iy++ )
		{
			for ( var ix = 0; ix <= segmentsX; ix++ )
			{
				m.AddVertex( new Vec3(
					(ix / (float)segmentsX - 0.5f) * sizeX,
					(iy / (float)segmentsY - 0.5f) * sizeY,
					0f ) );
			}
		}

		var stride = segmentsX + 1;

		for ( var iy = 0; iy < segmentsY; iy++ )
		{
			for ( var ix = 0; ix < segmentsX; ix++ )
			{
				var a = iy * stride + ix;
				var b = a + 1;
				var c = a + stride + 1;
				var d = a + stride;

				var u0 = ix / (float)segmentsX;
				var u1 = (ix + 1) / (float)segmentsX;
				var v0 = iy / (float)segmentsY;
				var v1 = (iy + 1) / (float)segmentsY;

				m.AddFace(
					new[] { a, b, c, d },
					new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) },
					material );
			}
		}

		return m;
	}

	/// <summary>Cylinder about Z. Sides are quads; the caps are single n-gons rather than triangle
	/// fans, because Catmull-Clark turns an n-gon into n clean quads and turns a fan into a mess
	/// around a high-valence hub.</summary>
	public static PolyMesh Cylinder( float radius = 0.5f, float height = 1f, int segments = 16, int material = 0 )
	{
		segments = Math.Max( 3, segments );

		var m = new PolyMesh();
		var hz = height * 0.5f;

		for ( var i = 0; i < segments; i++ )
		{
			var a = i / (float)segments * MathF.Tau;
			m.AddVertex( new Vec3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, -hz ) );
		}

		for ( var i = 0; i < segments; i++ )
		{
			var a = i / (float)segments * MathF.Tau;
			m.AddVertex( new Vec3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, hz ) );
		}

		for ( var i = 0; i < segments; i++ )
		{
			var next = (i + 1) % segments;
			var u0 = i / (float)segments;
			var u1 = (i + 1) / (float)segments;

			m.AddFace(
				new[] { i, next, segments + next, segments + i },
				new[] { new Vec2( u0, 0 ), new Vec2( u1, 0 ), new Vec2( u1, 1 ), new Vec2( u0, 1 ) },
				material );
		}

		// Bottom cap runs backwards so its normal points -Z.
		var bottom = new int[segments];
		var bottomUV = new Vec2[segments];

		for ( var i = 0; i < segments; i++ )
		{
			bottom[i] = segments - 1 - i;
			var a = (segments - 1 - i) / (float)segments * MathF.Tau;
			bottomUV[i] = new Vec2( MathF.Cos( a ) * 0.5f + 0.5f, MathF.Sin( a ) * 0.5f + 0.5f );
		}

		m.AddFace( bottom, bottomUV, material );

		var top = new int[segments];
		var topUV = new Vec2[segments];

		for ( var i = 0; i < segments; i++ )
		{
			top[i] = segments + i;
			var a = i / (float)segments * MathF.Tau;
			topUV[i] = new Vec2( MathF.Cos( a ) * 0.5f + 0.5f, MathF.Sin( a ) * 0.5f + 0.5f );
		}

		m.AddFace( top, topUV, material );

		return m;
	}

	/// <summary>
	/// Sphere built as a subdivided cube projected onto the sphere.
	///
	/// Deliberately not a UV sphere. A UV sphere concentrates a triangle fan at each pole and
	/// crowds vertices near them; both artefacts survive every subdivision level and show up the
	/// first time anyone drags a brush across a pole. A quad sphere is all quads with only eight
	/// valence-3 vertices, spread evenly.
	/// </summary>
	public static PolyMesh QuadSphere( float radius = 0.5f, int segments = 4, int material = 0 )
	{
		segments = Math.Max( 1, segments );

		var m = new PolyMesh();
		var weld = new VertexWelder( m );

		// The six cube faces, each as an origin corner plus two edge vectors.
		var faces = new (Vec3 Origin, Vec3 U, Vec3 V)[]
		{
			(new Vec3( -1, -1, -1 ), new Vec3( 0, 2, 0 ), new Vec3( 2, 0, 0 )),  // -Z
			(new Vec3( -1, -1, 1 ), new Vec3( 2, 0, 0 ), new Vec3( 0, 2, 0 )),   // +Z
			(new Vec3( -1, -1, -1 ), new Vec3( 2, 0, 0 ), new Vec3( 0, 0, 2 )),  // -Y
			(new Vec3( 1, -1, -1 ), new Vec3( 0, 2, 0 ), new Vec3( 0, 0, 2 )),   // +X
			(new Vec3( 1, 1, -1 ), new Vec3( -2, 0, 0 ), new Vec3( 0, 0, 2 )),   // +Y
			(new Vec3( -1, 1, -1 ), new Vec3( 0, -2, 0 ), new Vec3( 0, 0, 2 )),  // -X
		};

		foreach ( var (origin, uDir, vDir) in faces )
		{
			for ( var iv = 0; iv < segments; iv++ )
			{
				for ( var iu = 0; iu < segments; iu++ )
				{
					var corners = new int[4];
					var uvs = new Vec2[4];
					var offsets = new[] { (0, 0), (1, 0), (1, 1), (0, 1) };

					for ( var c = 0; c < 4; c++ )
					{
						var (du, dv) = offsets[c];
						var fu = (iu + du) / (float)segments;
						var fv = (iv + dv) / (float)segments;

						// Project the cube point onto the sphere by normalising it.
						var cube = origin + uDir * fu + vDir * fv;
						corners[c] = weld.Add( cube.Normal * radius );
						uvs[c] = new Vec2( fu, fv );
					}

					m.AddFace( corners, uvs, material );
				}
			}
		}

		return m;
	}

	/// <summary>Right-angle wedge — a ramp. The two ends are triangles, which is unavoidable for
	/// this shape; everything else is quads.</summary>
	public static PolyMesh Wedge( float sizeX = 1f, float sizeY = 1f, float sizeZ = 1f, int material = 0 )
	{
		var hx = sizeX * 0.5f;
		var hy = sizeY * 0.5f;
		var hz = sizeZ * 0.5f;

		var m = new PolyMesh();

		m.AddVertex( new Vec3( -hx, -hy, -hz ) ); // 0
		m.AddVertex( new Vec3( hx, -hy, -hz ) );  // 1
		m.AddVertex( new Vec3( hx, hy, -hz ) );   // 2
		m.AddVertex( new Vec3( -hx, hy, -hz ) );  // 3
		m.AddVertex( new Vec3( -hx, -hy, hz ) );  // 4  high edge, -Y side
		m.AddVertex( new Vec3( -hx, hy, hz ) );   // 5  high edge, +Y side

		var quadUV = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
		var triUV = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 0, 1 ) };

		m.AddFace( new[] { 0, 3, 2, 1 }, (Vec2[])quadUV.Clone(), material ); // base, -Z
		m.AddFace( new[] { 1, 2, 5, 4 }, (Vec2[])quadUV.Clone(), material ); // slope
		m.AddFace( new[] { 3, 0, 4, 5 }, (Vec2[])quadUV.Clone(), material ); // back, -X
		m.AddFace( new[] { 0, 1, 4 }, (Vec2[])triUV.Clone(), material );     // -Y end
		m.AddFace( new[] { 2, 3, 5 }, (Vec2[])triUV.Clone(), material );     // +Y end

		return m;
	}

	/// <summary>Hollow tube about Z. All quads, closed, and a good stress case for the validator
	/// because it is genus 1 — Euler characteristic 0, not 2.</summary>
	public static PolyMesh Tube( float outerRadius = 0.5f, float innerRadius = 0.3f, float height = 1f, int segments = 16, int material = 0 )
	{
		segments = Math.Max( 3, segments );

		if ( innerRadius >= outerRadius )
			throw new ArgumentException( "innerRadius must be smaller than outerRadius" );

		var m = new PolyMesh();
		var hz = height * 0.5f;

		// Four rings: outer-bottom, outer-top, inner-bottom, inner-top.
		void Ring( float radius, float z )
		{
			for ( var i = 0; i < segments; i++ )
			{
				var a = i / (float)segments * MathF.Tau;
				m.AddVertex( new Vec3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, z ) );
			}
		}

		Ring( outerRadius, -hz ); // 0
		Ring( outerRadius, hz );  // 1
		Ring( innerRadius, -hz ); // 2
		Ring( innerRadius, hz );  // 3

		var ob = 0;
		var ot = segments;
		var ib = segments * 2;
		var it = segments * 3;

		Vec2[] UV( int i ) => new[]
		{
			new Vec2( i / (float)segments, 0 ),
			new Vec2( (i + 1) / (float)segments, 0 ),
			new Vec2( (i + 1) / (float)segments, 1 ),
			new Vec2( i / (float)segments, 1 )
		};

		for ( var i = 0; i < segments; i++ )
		{
			var n = (i + 1) % segments;

			m.AddFace( new[] { ob + i, ob + n, ot + n, ot + i }, UV( i ), material );  // outer wall
			m.AddFace( new[] { ib + n, ib + i, it + i, it + n }, UV( i ), material );  // inner wall, reversed
			m.AddFace( new[] { ot + i, ot + n, it + n, it + i }, UV( i ), material );  // top annulus
			m.AddFace( new[] { ib + i, ib + n, ob + n, ob + i }, UV( i ), material );  // bottom annulus
		}

		return m;
	}
}
