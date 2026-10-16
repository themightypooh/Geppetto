using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// A spot on a garment, as a little frame lying on the cloth: where, which way is out of the cloth
/// (Normal), which way is up the body (Up) and which way is to the viewer's right (Right).
/// Right x Up = Normal, so anything laid out in (Right, Up) reads the right way round from outside.
/// </summary>
public readonly struct SurfaceFrame
{
	public readonly Vec3 Point, Normal, Up, Right;

	public SurfaceFrame( Vec3 point, Vec3 normal, Vec3 up )
	{
		Point = point;
		Normal = normal.Normal;
		var u = up - Normal * Vec3.Dot( up, Normal );

		if ( u.LengthSquared < 1e-8f )
		{
			var any = MathF.Abs( Normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
			u = any - Normal * Vec3.Dot( any, Normal );
		}

		Up = u.Normal;
		Right = Vec3.Cross( Up, Normal ).Normal;
	}

	/// <summary>A point in this frame: x to the right, y up, z out of the cloth.</summary>
	public Vec3 At( float x, float y, float z = 0f ) => Point + Right * x + Up * y + Normal * z;

	/// <summary>The same frame turned about its normal by <paramref name="degrees"/>.</summary>
	public SurfaceFrame Turned( float degrees )
	{
		var a = degrees * MathF.PI / 180f;
		return new SurfaceFrame( Point, Normal, Up * MathF.Cos( a ) - Right * MathF.Sin( a ) );
	}
}

/// <summary>
/// One garment's cloth, for laying details on: finds named spots on it from the rig, and walks
/// across it so a pocket wraps a thigh rather than standing off it flat.
///
/// WHY WALK. A cargo pocket is seven inches wide and a thigh is twenty-two round, so a flat
/// rectangle touching the middle of the pocket floats an inch off the cloth at its sides. Marching
/// across the surface in small steps, re-landing on the cloth each step, is how the pocket's
/// sides end up on the side of the leg - the way a sewn-on piece of fabric actually lies.
/// </summary>
public sealed class DetailSurface
{
	public readonly PolyMesh Mesh;
	readonly MeshBVH _bvh;

	public DetailSurface( PolyMesh mesh )
	{
		Mesh = mesh;
		_bvh = MeshBVH.Build( mesh );
	}

	/// <summary>Land a point on the cloth. The normal is turned to agree with <paramref name="n"/>,
	/// so a garment with two walls keeps facing out.</summary>
	public bool Snap( ref Vec3 p, ref Vec3 n, float reach = 3f )
	{
		var hit = _bvh.NearestSurface( Mesh, p, reach );

		if ( hit is not { } h )
			return false;

		var normal = h.Normal;

		if ( Vec3.Dot( normal, n ) < 0f )
			normal = -normal;

		p = h.Point;
		n = normal;
		return true;
	}

	public MeshHit? Ray( Vec3 origin, Vec3 direction ) => _bvh.Raycast( Mesh, origin, direction );

	/// <summary>Walk <paramref name="distance"/> inches across the cloth from <paramref name="from"/>
	/// along <paramref name="direction"/> (a tangent), carrying the frame with it.</summary>
	public SurfaceFrame Walk( SurfaceFrame from, Vec3 direction, float distance )
	{
		if ( MathF.Abs( distance ) < 1e-5f )
			return from;

		if ( distance < 0f )
		{
			direction = -direction;
			distance = -distance;
		}

		var steps = Math.Max( 1, (int)MathF.Ceiling( distance / 0.2f ) );
		var h = distance / steps;
		var p = from.Point;
		var n = from.Normal;
		var up = from.Up;
		var d = direction.Normal;

		for ( var s = 0; s < steps; s++ )
		{
			var q = p + d * h + n * 0.05f;
			var m = n;

			if ( !Snap( ref q, ref m, 1.5f ) )
			{
				q = p + d * h;
				m = n;
			}

			var td = d - m * Vec3.Dot( d, m );
			var tu = up - m * Vec3.Dot( up, m );

			if ( td.LengthSquared > 1e-8f ) d = td.Normal;
			if ( tu.LengthSquared > 1e-8f ) up = tu.Normal;

			p = q;
			n = m;
		}

		return new SurfaceFrame( p, n, up );
	}

	/// <summary>A frame <paramref name="x"/> inches right and <paramref name="y"/> up across the cloth.</summary>
	public SurfaceFrame Offset( SurfaceFrame from, float x, float y )
	{
		var up = Walk( from, from.Up, y );
		return Walk( up, up.Right, x );
	}

	/// <summary>
	/// Frames over a width x height patch centred on <paramref name="centre"/>, (2nu+1) x (2nv+1)
	/// of them, [i, j] with i across and j up. The centre column is walked first and each row out
	/// from it, so the patch lies on the cloth however the cloth curves.
	/// </summary>
	public SurfaceFrame[,] Grid( SurfaceFrame centre, float width, float height, int nu, int nv )
	{
		var grid = new SurfaceFrame[2 * nu + 1, 2 * nv + 1];
		var du = nu > 0 ? width * 0.5f / nu : 0f;
		var dv = nv > 0 ? height * 0.5f / nv : 0f;
		var column = new SurfaceFrame[2 * nv + 1];
		column[nv] = centre;

		for ( var j = 1; j <= nv; j++ )
		{
			column[nv + j] = Walk( column[nv + j - 1], column[nv + j - 1].Up, dv );
			column[nv - j] = Walk( column[nv - j + 1], column[nv - j + 1].Up, -dv );
		}

		for ( var j = 0; j <= 2 * nv; j++ )
		{
			grid[nu, j] = column[j];

			for ( var i = 1; i <= nu; i++ )
			{
				grid[nu + i, j] = Walk( grid[nu + i - 1, j], grid[nu + i - 1, j].Right, du );
				grid[nu - i, j] = Walk( grid[nu - i + 1, j], grid[nu - i + 1, j].Right, -du );
			}
		}

		return grid;
	}

	/// <summary>
	/// The cloth all the way round a body part, at the point <paramref name="centre"/> on its
	/// axis: one point and outward direction per angle. Rays go OUT from the axis and keep the
	/// outer wall of the first layer they meet, so a band round the waist is not stopped by the
	/// sleeve hanging beside it.
	/// </summary>
	public List<(Vec3 Point, Vec3 Out)> Ring( Vec3 centre, Vec3 axis, int count )
	{
		axis = axis.Normal;
		var reference = MathF.Abs( axis.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var e1 = Vec3.Cross( axis, reference ).Normal;
		var e2 = Vec3.Cross( axis, e1 ).Normal;
		var ring = new List<(Vec3, Vec3)>();

		for ( var k = 0; k < count; k++ )
		{
			var a = k / (float)count * MathF.PI * 2f;
			var dir = e1 * MathF.Cos( a ) + e2 * MathF.Sin( a );
			var hits = MeshRaycast.AllHits( Mesh, centre, dir ).Where( h => h.Distance > 1e-3f ).ToList();

			if ( hits.Count == 0 )
				continue;

			var first = hits[0].Distance;
			var outer = hits.Last( h => h.Distance <= first + 0.6f );
			ring.Add( (outer.Point, dir) );
		}

		return ring;
	}
}

/// <summary>
/// Named places on a body where garment details go - chest, thigh, elbow - read off the rig the
/// way a garment's cut is, so "a cargo pocket on the thigh" means the same on Citizen and on
/// anything else with a thigh bone.
/// </summary>
public static class DetailSpots
{
	public static readonly string[] Names =
	{
		"Chest", "Belly", "Back", "Shoulder", "Collar", "Hip", "Seat",
		"Thigh", "Knee", "Shin", "Upper arm", "Elbow", "Forearm", "Wrist",
	};

	public static readonly string[] SideNames = { "Left", "Right", "Both" };

	/// <summary>+1 for the body's left (+y), -1 for its right.</summary>
	public static int[] Sides( int sideIndex ) => sideIndex switch
	{
		0 => new[] { 1 },
		1 => new[] { -1 },
		_ => new[] { 1, -1 },
	};

	/// <summary>The region, how far along it, how far off the middle (central regions only) and
	/// which way the ray comes in from, in body terms: x front, y out to this side, z up.</summary>
	static (BodyRegion Region, float T, float Lateral, Vec3 Aim) Spec( string spot ) => spot switch
	{
		"Chest" => (BodyRegion.Torso, 0.32f, 3.5f, new Vec3( 1, 0, 0 )),
		"Belly" => (BodyRegion.Torso, 0.8f, 2.5f, new Vec3( 1, 0, 0 )),
		"Back" => (BodyRegion.Torso, 0.4f, 3f, new Vec3( -1, 0, 0 )),
		"Shoulder" => (BodyRegion.Torso, 0.02f, 5f, new Vec3( 0.1f, 0.3f, 1 )),
		"Collar" => (BodyRegion.Torso, 0.08f, 2.6f, new Vec3( 1, 0, 0.7f )),
		"Hip" => (BodyRegion.Hips, 0.35f, 0f, new Vec3( 0.35f, 1, 0 )),
		"Seat" => (BodyRegion.Hips, 0.55f, 3.2f, new Vec3( -1, 0, 0 )),
		"Thigh" => (BodyRegion.UpperLeg, 0.45f, 0f, new Vec3( 0.12f, 1, 0 )),
		"Knee" => (BodyRegion.UpperLeg, 0.97f, 0f, new Vec3( 1, 0, 0 )),
		"Shin" => (BodyRegion.LowerLeg, 0.4f, 0f, new Vec3( 1, 0, 0 )),
		"Upper arm" => (BodyRegion.UpperArm, 0.4f, 0f, new Vec3( 0.1f, 1, 0.8f )),
		"Elbow" => (BodyRegion.UpperArm, 0.97f, 0f, new Vec3( -1, 0, 0 )),
		"Forearm" => (BodyRegion.LowerArm, 0.45f, 0f, new Vec3( 0.2f, 1, 0.8f )),
		"Wrist" => (BodyRegion.LowerArm, 0.88f, 0f, new Vec3( 0.2f, 1, 0.8f )),
		_ => (BodyRegion.Torso, 0.3f, 0f, new Vec3( 1, 0, 0 )),
	};

	/// <summary>Where the spot's axis is and the line it runs along, on one side. False when the
	/// rig has no such part.</summary>
	public static bool Axis( BodyRegions.Map map, string spot, int side, out Vec3 centre, out Vec3 axis )
	{
		var (region, t, lateral, _) = Spec( spot );
		centre = axis = Vec3.Zero;

		if ( !map.Chain( region, side, out var from, out var to ) )
			return false;

		centre = Vec3.Lerp( from, to, t ) + (BodyRegions.Map.IsCentral( region ) ? new Vec3( 0, lateral * side, 0 ) : Vec3.Zero);
		axis = (to - from).Normal;
		return true;
	}

	/// <summary>
	/// The spot on this cloth, or null when the garment does not reach it (a thigh on a T-shirt).
	/// <paramref name="across"/> moves it outward (away from the middle of the body) and
	/// <paramref name="up"/> up the body, both in inches across the cloth, so one number moves the
	/// left and right copies apart symmetrically.
	/// </summary>
	public static SurfaceFrame? Find( DetailSurface cloth, BodyRegions.Map map, string spot, int side,
		float across = 0f, float up = 0f )
	{
		var (region, _, _, aimLocal) = Spec( spot );

		if ( !Axis( map, spot, side, out var centre, out var axis ) )
			return null;

		var aim = new Vec3( aimLocal.x, aimLocal.y * side, aimLocal.z ).Normal;
		var central = BodyRegions.Map.IsCentral( region );

		if ( !central )
		{
			var p = aim - axis * Vec3.Dot( aim, axis );
			aim = p.LengthSquared > 1e-6f ? p.Normal : aim;
		}

		var hit = cloth.Ray( centre + aim * 40f, -aim );

		if ( hit is not { } h )
			return null;

		// Only the cloth over THIS part: a ray at the thigh that lands on the hem of a jacket
		// hanging past it is not the thigh.
		if ( (h.Point - centre).Length > 14f )
			return null;

		var normal = Vec3.Dot( h.Normal, aim ) < 0f ? -h.Normal : h.Normal;
		var upRef = central ? new Vec3( 0, 0, 1 ) : -axis;

		if ( MathF.Abs( Vec3.Dot( upRef.Normal, normal ) ) > 0.85f )
			upRef = new Vec3( 1, 0, 0 );

		var frame = new SurfaceFrame( h.Point, normal, upRef );

		if ( MathF.Abs( up ) > 1e-4f )
			frame = cloth.Walk( frame, frame.Up, up );

		if ( MathF.Abs( across ) > 1e-4f )
		{
			// Outward is whichever way along Right leads away from the body's middle.
			var outward = Vec3.Dot( frame.Right, new Vec3( 0, side, 0 ) ) >= 0f ? 1f : -1f;
			frame = cloth.Walk( frame, frame.Right, across * outward );
		}

		return frame;
	}
}

/// <summary>
/// The shapes garment details are made of: a panel lying on the cloth, small turned parts (studs,
/// grommets, snaps), tubes along a path (wire, cable, chain links, safety pins), and bands round a
/// limb. Everything lands on its own material slot, given by the caller.
/// </summary>
public static class DetailShapes
{
	/// <summary>Add a face whose normal faces <paramref name="facing"/>, reversing it if it was
	/// wound the other way. Every builder here goes through this, so no part comes out inside-out.</summary>
	public static void Face( PolyMesh m, int[] indices, Vec3 facing, int slot, Vec2[] uvs = null )
	{
		var f = new Face( indices, uvs, slot );

		if ( Vec3.Dot( m.FaceNormal( f ), facing ) < 0f )
		{
			Array.Reverse( f.Indices );

			if ( f.UVs is not null )
				Array.Reverse( f.UVs );
		}

		m.Faces.Add( f );
	}

	/// <summary>Copy <paramref name="part"/> (built in x right, y up, z out of the cloth) onto the
	/// frame. Its own slots are kept unless <paramref name="slot"/> is given.</summary>
	public static void Place( PolyMesh into, PolyMesh part, SurfaceFrame f, float lift = 0f, int slot = -1 )
	{
		var start = into.VertexCount;

		foreach ( var p in part.Positions )
			into.AddVertex( f.At( p.x, p.y, p.z + lift ) );

		foreach ( var face in part.Faces )
			into.Faces.Add( new Face( face.Indices.Select( i => i + start ).ToArray(), (Vec2[])face.UVs?.Clone(),
				slot >= 0 ? slot : face.Material ) );
	}

	/// <summary>
	/// A shape turned round z from a profile of (radius, height) points, bottom-outside first and
	/// ending at the top. A zero radius is a single point (a tip, the middle of a disc).
	/// </summary>
	public static PolyMesh Lathe( IReadOnlyList<Vec2> profile, int segments, int slot = 0 )
	{
		var m = new PolyMesh();
		var rings = new List<int[]>();

		foreach ( var p in profile )
		{
			if ( p.x < 1e-5f )
			{
				rings.Add( new[] { m.AddVertex( new Vec3( 0, 0, p.y ) ) } );
				continue;
			}

			var r = new int[segments];

			for ( var s = 0; s < segments; s++ )
			{
				var a = s / (float)segments * MathF.PI * 2f;
				r[s] = m.AddVertex( new Vec3( MathF.Cos( a ) * p.x, MathF.Sin( a ) * p.x, p.y ) );
			}

			rings.Add( r );
		}

		for ( var k = 0; k + 1 < rings.Count; k++ )
		{
			var a = rings[k];
			var b = rings[k + 1];

			if ( a.Length == 1 && b.Length == 1 )
				continue;

			for ( var s = 0; s < segments; s++ )
			{
				var s1 = (s + 1) % segments;

				if ( a.Length == 1 )
					m.AddFace( new[] { a[0], b[s1], b[s] }, null, slot );
				else if ( b.Length == 1 )
					m.AddFace( new[] { a[s], a[s1], b[0] }, null, slot );
				else
					m.AddFace( new[] { a[s], a[s1], b[s1], b[s] }, null, slot );
			}
		}

		return m;
	}

	static Vec2 V( float r, float z ) => new( r, z );

	/// <summary>A square nailhead stud: four faces to a point.</summary>
	public static PolyMesh Pyramid( float width, float height, int slot )
	{
		var m = new PolyMesh();
		var w = width * 0.5f;
		var a = m.AddVertex( new Vec3( -w, -w, -0.02f ) );
		var b = m.AddVertex( new Vec3( w, -w, -0.02f ) );
		var c = m.AddVertex( new Vec3( w, w, -0.02f ) );
		var d = m.AddVertex( new Vec3( -w, w, -0.02f ) );
		var a1 = m.AddVertex( new Vec3( -w, -w, 0.03f ) );
		var b1 = m.AddVertex( new Vec3( w, -w, 0.03f ) );
		var c1 = m.AddVertex( new Vec3( w, w, 0.03f ) );
		var d1 = m.AddVertex( new Vec3( -w, w, 0.03f ) );
		var top = m.AddVertex( new Vec3( 0, 0, height ) );
		var o = new Vec3( 0, 0, height * 0.3f );

		void Side( int p, int q, int p1, int q1 )
		{
			var mid = (m.Positions[p] + m.Positions[q]) * 0.5f;
			Face( m, new[] { p, q, q1, p1 }, mid - o, slot );
			Face( m, new[] { p1, q1, top }, (m.Positions[p1] + m.Positions[q1]) * 0.5f - o, slot );
		}

		Side( a, b, a1, b1 );
		Side( b, c, b1, c1 );
		Side( c, d, c1, d1 );
		Side( d, a, d1, a1 );
		return m;
	}

	public static PolyMesh Cone( float radius, float height, int slot ) => Lathe( new[]
	{
		V( radius * 1.15f, -0.02f ), V( radius * 1.15f, 0.04f ), V( radius, 0.06f ), V( radius * 0.15f, height * 0.94f ), V( 0, height ),
	}, 12, slot );

	public static PolyMesh Dome( float radius, float height, int slot ) => Lathe( new[]
	{
		V( radius, -0.02f ), V( radius, 0.01f ), V( radius * 0.93f, height * 0.45f ), V( radius * 0.72f, height * 0.8f ),
		V( radius * 0.38f, height * 0.97f ), V( 0, height ),
	}, 12, slot );

	/// <summary>A grommet: a rolled ring round a hole, the hole on <paramref name="holeSlot"/>.</summary>
	public static PolyMesh Grommet( float outer, float inner, int slot, int holeSlot )
	{
		var m = Lathe( new[]
		{
			V( outer, -0.02f ), V( outer, 0.01f ), V( outer * 0.93f, 0.045f ), V( (outer + inner) * 0.5f, 0.06f ),
			V( inner * 1.06f, 0.045f ), V( inner, 0.01f ), V( inner, -0.03f ),
		}, 14, slot );
		var hole = Lathe( new[] { V( inner * 1.01f, -0.005f ), V( 0, -0.005f ) }, 14, holeSlot );
		Place( m, hole, new SurfaceFrame( Vec3.Zero, new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ) ) );
		return m;
	}

	public static PolyMesh Snap( float radius, int slot ) => Lathe( new[]
	{
		V( radius, -0.02f ), V( radius, 0.05f ), V( radius * 0.92f, 0.08f ), V( radius * 0.75f, 0.09f ),
		V( radius * 0.6f, 0.12f ), V( 0, 0.13f ),
	}, 12, slot );

	public static PolyMesh Button( float radius, int slot ) => Lathe( new[]
	{
		V( radius, -0.02f ), V( radius, 0.07f ), V( radius * 0.9f, 0.09f ), V( radius * 0.78f, 0.075f ), V( 0, 0.07f ),
	}, 12, slot );

	/// <summary>A socket: a raised collar round a dark well.</summary>
	public static PolyMesh Port( float radius, int slot, int holeSlot )
	{
		var m = Lathe( new[]
		{
			V( radius, -0.02f ), V( radius, 0.16f ), V( radius * 0.92f, 0.2f ), V( radius * 0.62f, 0.2f ), V( radius * 0.6f, 0.04f ),
		}, 14, slot );
		var well = Lathe( new[] { V( radius * 0.6f, 0.04f ), V( 0, 0.04f ) }, 14, holeSlot );
		Place( m, well, new SurfaceFrame( Vec3.Zero, new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ) ) );
		return m;
	}

	/// <summary>
	/// A round tube along a path, carried round corners by parallel transport so it never twists.
	/// UVs run along the path (u in inches) and round it (v 0..1).
	/// </summary>
	public static void Tube( PolyMesh into, IReadOnlyList<Vec3> path, float radius, int sides, bool closed, int slot,
		bool caps = true )
	{
		var n = path.Count;

		if ( n < 2 )
			return;

		var tangents = new Vec3[n];

		for ( var i = 0; i < n; i++ )
		{
			var prev = closed ? path[(i - 1 + n) % n] : path[Math.Max( 0, i - 1 )];
			var next = closed ? path[(i + 1) % n] : path[Math.Min( n - 1, i + 1 )];
			tangents[i] = (next - prev).Normal;
		}

		var reference = MathF.Abs( tangents[0].z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var normal = Vec3.Cross( tangents[0], reference ).Normal;
		var rings = new int[n][];
		var along = 0f;

		for ( var i = 0; i < n; i++ )
		{
			if ( i > 0 )
			{
				along += (path[i] - path[i - 1]).Length;
				var t = normal - tangents[i] * Vec3.Dot( normal, tangents[i] );
				normal = t.LengthSquared > 1e-10f ? t.Normal : normal;
			}

			var binormal = Vec3.Cross( tangents[i], normal ).Normal;
			rings[i] = new int[sides];

			for ( var s = 0; s < sides; s++ )
			{
				var a = s / (float)sides * MathF.PI * 2f;
				rings[i][s] = into.AddVertex( path[i] + (normal * MathF.Cos( a ) + binormal * MathF.Sin( a )) * radius );
			}
		}

		var segs = closed ? n : n - 1;
		var u = 0f;

		for ( var i = 0; i < segs; i++ )
		{
			var j = (i + 1) % n;
			var u1 = u + (path[j] - path[i]).Length;

			for ( var s = 0; s < sides; s++ )
			{
				var s1 = (s + 1) % sides;
				var mid = (path[i] + path[j]) * 0.5f;
				var corner = (into.Positions[rings[i][s]] + into.Positions[rings[i][s1]]) * 0.5f;
				Face( into, new[] { rings[i][s], rings[i][s1], rings[j][s1], rings[j][s] }, corner - path[i], slot,
					new[] { new Vec2( u, s / (float)sides ), new Vec2( u, (s + 1f) / sides ), new Vec2( u1, (s + 1f) / sides ), new Vec2( u1, s / (float)sides ) } );
			}

			u = u1;
		}

		if ( closed || !caps )
			return;

		Face( into, rings[0], -tangents[0], slot );
		Face( into, rings[n - 1], tangents[n - 1], slot );
	}

	/// <summary>A closed loop: an ellipse <paramref name="a"/> long and <paramref name="b"/> wide,
	/// in the plane of <paramref name="along"/> and <paramref name="side"/>.</summary>
	public static List<Vec3> Loop( Vec3 centre, Vec3 along, Vec3 side, float a, float b, int count )
	{
		var pts = new List<Vec3>( count );

		for ( var k = 0; k < count; k++ )
		{
			var t = k / (float)count * MathF.PI * 2f;
			pts.Add( centre + along * (MathF.Cos( t ) * a) + side * (MathF.Sin( t ) * b) );
		}

		return pts;
	}

	/// <summary>A rounded rectangle path in the (x, y) plane at height z.</summary>
	public static List<Vec3> RoundedRect( float w, float h, float r, float z, int perCorner = 3 )
	{
		var pts = new List<Vec3>();
		var cx = w * 0.5f - r;
		var cy = h * 0.5f - r;
		var centres = new[] { (cx, cy), (-cx, cy), (-cx, -cy), (cx, -cy) };

		for ( var c = 0; c < 4; c++ )
		{
			for ( var k = 0; k <= perCorner; k++ )
			{
				var a = (c * 90f + k * 90f / perCorner) * MathF.PI / 180f;
				pts.Add( new Vec3( centres[c].Item1 + MathF.Cos( a ) * r, centres[c].Item2 + MathF.Sin( a ) * r, z ) );
			}
		}

		return pts;
	}

	/// <summary>A safety pin lying on the cloth along x, <paramref name="length"/> long.</summary>
	public static PolyMesh SafetyPin( float length, int slot )
	{
		var m = new PolyMesh();
		var wire = MathF.Max( 0.018f, length * 0.022f );
		var half = length * 0.5f;
		var gap = length * 0.09f;
		var path = new List<Vec3>();

		// The pin: from inside the clasp, back along the bottom, round the spring, and back along
		// the top into the clasp. The bottom wire dips into the cloth, as a pinned pin does.
		path.Add( new Vec3( half - 0.06f, -gap, wire ) );
		path.Add( new Vec3( 0f, -gap, -wire * 0.5f ) );
		path.Add( new Vec3( -half + gap, -gap, wire ) );

		for ( var k = 0; k <= 12; k++ )
		{
			var a = -MathF.PI * 0.5f - k / 12f * MathF.PI * 2.25f;
			path.Add( new Vec3( -half + gap + MathF.Cos( a ) * gap, MathF.Sin( a ) * gap, wire * (1.2f + k * 0.05f) ) );
		}

		path.Add( new Vec3( -half + gap * 1.5f, gap, wire * 1.5f ) );
		path.Add( new Vec3( half - 0.06f, gap, wire * 1.5f ) );
		Tube( m, path, wire, 5, false, slot );

		// The clasp: a little folded cap over both ends.
		var clasp = Lathe( new[] { V( gap * 1.5f, 0f ), V( gap * 1.5f, wire * 3.2f ), V( gap * 1.2f, wire * 3.8f ), V( 0, wire * 3.8f ) }, 8, slot );
		Place( m, clasp, new SurfaceFrame( new Vec3( half - gap * 1.1f, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ) ) );
		return m;
	}

	/// <summary>A buckle frame with its prong, <paramref name="w"/> across the strap.</summary>
	public static PolyMesh Buckle( float w, float h, int slot )
	{
		var m = new PolyMesh();
		var wire = MathF.Max( 0.05f, w * 0.06f );
		var frame = RoundedRect( h, w, MathF.Min( h, w ) * 0.2f, wire * 1.6f );
		Tube( m, frame, wire, 6, true, slot );
		Tube( m, new List<Vec3> { new( -h * 0.5f, 0, wire * 1.6f ), new( h * 0.55f, 0, wire * 2.8f ) }, wire * 0.7f, 5, false, slot );
		return m;
	}

	/// <summary>A zip pull: a flat tab hanging from a slider.</summary>
	public static PolyMesh ZipPull( float length, int slot )
	{
		var m = new PolyMesh();
		var slider = Primitives.Box( 0.32f, 0.22f, 0.1f, slot );
		Place( m, slider, new SurfaceFrame( new Vec3( 0, 0, 0.05f ), new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ) ) );
		var tab = Primitives.Box( 0.24f, length, 0.05f, slot );
		Place( m, tab, new SurfaceFrame( new Vec3( 0, -length * 0.5f - 0.08f, 0.08f ), new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ) ) );
		return m;
	}

	/// <summary>
	/// A sheet lying on the cloth over a grid of frames, raised by <paramref name="height"/>(x, y)
	/// inches. Where height is zero the sheet sinks just under the cloth, so a shape's outline is
	/// wherever its height falls to nothing - a rounded pocket, a hexagonal plate - with no edge
	/// to hide. Faces entirely under the cloth are left out.
	/// </summary>
	public static void Panel( PolyMesh into, SurfaceFrame[,] grid, float width, float height,
		Func<float, float, float> raise, Func<float, float, int> slotAt, float uvPerInch )
	{
		var nu = grid.GetLength( 0 );
		var nv = grid.GetLength( 1 );
		var ids = new int[nu, nv];
		var lifted = new bool[nu, nv];
		var coords = new Vec2[nu, nv];

		for ( var i = 0; i < nu; i++ )
		{
			for ( var j = 0; j < nv; j++ )
			{
				var x = nu > 1 ? -width * 0.5f + width * i / (nu - 1) : 0f;
				var y = nv > 1 ? -height * 0.5f + height * j / (nv - 1) : 0f;
				var h = raise( x, y );
				lifted[i, j] = h > 0.004f;
				var f = grid[i, j];
				ids[i, j] = into.AddVertex( f.Point + f.Normal * (lifted[i, j] ? h : -0.03f) );
				coords[i, j] = uvPerInch > 0f
					? new Vec2( x * uvPerInch, -y * uvPerInch )
					: new Vec2( (x + width * 0.5f) / width, 1f - (y + height * 0.5f) / height );
			}
		}

		for ( var i = 0; i + 1 < nu; i++ )
		{
			for ( var j = 0; j + 1 < nv; j++ )
			{
				if ( !lifted[i, j] && !lifted[i + 1, j] && !lifted[i + 1, j + 1] && !lifted[i, j + 1] )
					continue;

				var x = -width * 0.5f + width * (i + 0.5f) / (nu - 1);
				var y = -height * 0.5f + height * (j + 0.5f) / (nv - 1);
				var facing = grid[i, j].Normal + grid[i + 1, j + 1].Normal;
				Face( into, new[] { ids[i, j], ids[i + 1, j], ids[i + 1, j + 1], ids[i, j + 1] }, facing, slotAt( x, y ),
					new[] { coords[i, j], coords[i + 1, j], coords[i + 1, j + 1], coords[i, j + 1] } );
			}
		}
	}

	/// <summary>Signed distance to a rounded rectangle centred at the origin: negative inside.</summary>
	public static float RoundedRectDistance( float x, float y, float halfW, float halfH, float radius )
	{
		radius = MathF.Min( radius, MathF.Min( halfW, halfH ) );
		var qx = MathF.Abs( x ) - halfW + radius;
		var qy = MathF.Abs( y ) - halfH + radius;
		var outside = new Vec2( MathF.Max( qx, 0f ), MathF.Max( qy, 0f ) ).Length;
		return outside + MathF.Min( MathF.Max( qx, qy ), 0f ) - radius;
	}

	public static float Smooth( float e0, float e1, float x )
	{
		var t = Math.Clamp( (x - e0) / (e1 - e0), 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	/// <summary>
	/// A flat band all the way round a body part: a strap, a reflective stripe, an arm band.
	/// <paramref name="lower"/> and <paramref name="upper"/> are the cloth round the part at the
	/// band's two edges, matched by angle.
	/// </summary>
	public static void Band( PolyMesh into, List<(Vec3 Point, Vec3 Out)> lower, List<(Vec3 Point, Vec3 Out)> upper,
		float lift, float thickness, int slot )
	{
		var n = Math.Min( lower.Count, upper.Count );

		if ( n < 3 )
			return;

		var li = new int[n];
		var lo = new int[n];
		var ui = new int[n];
		var uo = new int[n];

		for ( var k = 0; k < n; k++ )
		{
			li[k] = into.AddVertex( lower[k].Point + lower[k].Out * lift );
			lo[k] = into.AddVertex( lower[k].Point + lower[k].Out * (lift + thickness) );
			ui[k] = into.AddVertex( upper[k].Point + upper[k].Out * lift );
			uo[k] = into.AddVertex( upper[k].Point + upper[k].Out * (lift + thickness) );
		}

		var along = 0f;

		for ( var k = 0; k < n; k++ )
		{
			var k1 = (k + 1) % n;
			var o = (lower[k].Out + lower[k1].Out).Normal;
			var axis = ((upper[k].Point - lower[k].Point) + (upper[k1].Point - lower[k1].Point)).Normal;
			var step = (lower[k1].Point - lower[k].Point).Length;
			var uvs = new[] { new Vec2( along, 1f ), new Vec2( along + step, 1f ), new Vec2( along + step, 0f ), new Vec2( along, 0f ) };
			Face( into, new[] { lo[k], lo[k1], uo[k1], uo[k] }, o, slot, uvs );
			Face( into, new[] { uo[k], uo[k1], ui[k1], ui[k] }, axis, slot );
			Face( into, new[] { li[k], li[k1], lo[k1], lo[k] }, -axis, slot );
			along += step;
		}
	}

	/// <summary>Points every <paramref name="spacing"/> inches along a polyline.</summary>
	public static List<int> Resample( IReadOnlyList<Vec3> path, float spacing, bool closed, out List<Vec3> points )
	{
		if ( !float.IsFinite( spacing ) || spacing <= 0f )
			throw new ArgumentOutOfRangeException( nameof( spacing ) );
		points = new List<Vec3>();
		var at = new List<int>();
		var n = path.Count;

		if ( n == 0 )
			return at;

		var segs = closed ? n : n - 1;
		var carry = 0f;
		points.Add( path[0] );
		at.Add( 0 );

		for ( var i = 0; i < segs; i++ )
		{
			var a = path[i];
			var b = path[(i + 1) % n];
			var len = (b - a).Length;
			var d = spacing - carry;

			while ( d <= len )
			{
				points.Add( Vec3.Lerp( a, b, d / len ) );
				at.Add( d / len < 0.5f ? i : (i + 1) % n );
				d += spacing;
			}

			carry = len - (d - spacing);
		}

		if ( closed && points.Count > 1 && (points[^1] - points[0]).Length < spacing * 0.5f )
		{
			points.RemoveAt( points.Count - 1 );
			at.RemoveAt( at.Count - 1 );
		}

		return at;
	}
}

/// <summary>
/// A garment's openings as paths lying on the cloth, for studding a collar, piping a hem with
/// light, or rubbing an edge thin: each point with its outward normal and the direction across
/// the cloth away from the edge.
/// </summary>
public static class DetailEdges
{
	public sealed class Edge
	{
		public List<Vec3> Points = new();
		public List<Vec3> Normals = new();
		public List<Vec3> Inward = new();
	}

	/// <summary>The cloth sheet a garment's openings are on: the outer wall of a thickened garment,
	/// the garment itself otherwise.</summary>
	public static PolyMesh Sheet( PolyMesh garment ) => MeshSolidify.OuterSheet( garment, out _ ) ?? garment;

	/// <summary>
	/// The openings, lowest first (hem, then cuffs, then collar on a top). <paramref name="which"/>
	/// is the Trim convention: 0 all, 1 lowest, 2 highest, 3 the ones between.
	/// </summary>
	public static List<Edge> Openings( PolyMesh garment, int which )
	{
		var sheet = Sheet( garment );
		var loops = GarmentTrim.OrderedBoundaryLoops( sheet );
		var picked = new List<List<int>>();

		for ( var i = 0; i < loops.Count; i++ )
		{
			var keep = which switch
			{
				1 => i == 0,
				2 => i == loops.Count - 1,
				3 => i > 0 && i < loops.Count - 1,
				_ => true,
			};

			if ( keep )
				picked.Add( loops[i] );
		}

		var normals = sheet.ComputeVertexNormals();
		var centroid = sheet.Positions.Aggregate( Vec3.Zero, ( a, b ) => a + b ) / Math.Max( 1, sheet.VertexCount );

		// Which way is out: most of the sheet's normals point away from its middle when they are
		// the outside's. A sheet lifted the other way round is flipped wholesale.
		var agree = 0f;

		for ( var v = 0; v < sheet.VertexCount; v++ )
			agree += Vec3.Dot( normals[v], sheet.Positions[v] - centroid );

		var flip = agree < 0f ? -1f : 1f;
		var neighbours = new Dictionary<int, List<int>>();

		foreach ( var f in sheet.Faces )
		{
			for ( var k = 0; k < f.Count; k++ )
			{
				var a = f.Indices[k];
				var b = f.Indices[(k + 1) % f.Count];

				if ( !neighbours.TryGetValue( a, out var la ) ) neighbours[a] = la = new List<int>();
				if ( !neighbours.TryGetValue( b, out var lb ) ) neighbours[b] = lb = new List<int>();
				la.Add( b );
				lb.Add( a );
			}
		}

		var result = new List<Edge>();

		foreach ( var loop in picked )
		{
			var onLoop = loop.ToHashSet();
			var edge = new Edge();

			foreach ( var v in loop )
			{
				var p = sheet.Positions[v];
				var n = normals[v] * flip;
				var inward = Vec3.Zero;

				if ( neighbours.TryGetValue( v, out var ns ) )
					foreach ( var w in ns.Where( w => !onLoop.Contains( w ) ) )
						inward += sheet.Positions[w] - p;

				inward -= n * Vec3.Dot( inward, n );
				edge.Points.Add( p );
				edge.Normals.Add( n.Normal );
				edge.Inward.Add( inward.LengthSquared > 1e-10f ? inward.Normal : Vec3.Zero );
			}

			// A vertex with no inner neighbour borrows from the ones either side.
			for ( var k = 0; k < edge.Inward.Count; k++ )
				if ( edge.Inward[k].LengthSquared < 0.5f )
					edge.Inward[k] = (edge.Inward[(k + 1) % edge.Inward.Count] + edge.Inward[(k - 1 + edge.Inward.Count) % edge.Inward.Count]).Normal;

			result.Add( edge );
		}

		return result;
	}

	/// <summary>The edge moved <paramref name="inset"/> inches onto the cloth and
	/// <paramref name="lift"/> off it, smoothed so a stud row or a wire does not follow every facet.</summary>
	public static List<(Vec3 Point, Vec3 Normal, Vec3 Inward)> Inset( Edge edge, float inset, float lift, int smooth = 2 )
	{
		var n = edge.Points.Count;
		var pts = new Vec3[n];

		for ( var k = 0; k < n; k++ )
			pts[k] = edge.Points[k] + edge.Inward[k] * inset + edge.Normals[k] * lift;

		// Taubin (a shrink step, then a slightly bigger grow step), not plain averaging: averaging
		// pulls a loop in, and on an 18-point cuff two passes moved it 0.12 in toward the wrist -
		// more than the lift - so cuff studs and piping came out inside the sleeve.
		for ( var pass = 0; pass < smooth * 2; pass++ )
		{
			var next = new Vec3[n];
			var weight = pass % 2 == 0 ? 0.5f : -0.53f;

			for ( var k = 0; k < n; k++ )
				next[k] = pts[k] + ((pts[(k - 1 + n) % n] + pts[(k + 1) % n]) * 0.5f - pts[k]) * weight;

			pts = next;
		}

		return Enumerable.Range( 0, n ).Select( k => (pts[k], edge.Normals[k], edge.Inward[k]) ).ToList();
	}
}

/// <summary>What one of a detail's material slots should look like. The editor writes a .vmat from
/// this and binds it to the slot - see DetailMaterials.</summary>
public sealed class DetailMaterial
{
	/// <summary>hard, glow, worn, dirt, hole</summary>
	public string Kind = "hard";
	public int Slot;
	/// <summary>Unique and file-safe: the feature's id and what the slot is for.</summary>
	public string Key = "";
	public Vec3 Colour = new( 0.8f, 0.8f, 0.82f );
	public float Metalness = 1f;
	public float Roughness = 0.3f;

	// Glow
	public string Pattern = "Solid";
	public string Text = "";
	public float Speed;
	public float Brightness = 4f;
	/// <summary>How wide the lit surface is against how tall, so a scrolling sign shows the right
	/// number of letters. Strips and wires use their length per tile repeat instead.</summary>
	public float Aspect = 2f;

	// Worn and dirt: the cloth slot this is a variant of.
	public int SourceSlot = -1;

	/// <summary>The recipe string the editor compares to decide whether files need rewriting.</summary>
	public string Recipe => FormattableString.Invariant(
		$"{Kind}|{Slot}|{Colour.x:R}|{Colour.y:R}|{Colour.z:R}|{Metalness:R}|{Roughness:R}|{Pattern}|{Text}|{Speed:R}|{Brightness:R}|{Aspect:R}|{SourceSlot}" );
}

/// <summary>Anything that hands the editor material slots to write.</summary>
public interface IDetailMaterials
{
	IReadOnlyList<DetailMaterial> DetailMaterials { get; }
}

/// <summary>
/// The looks garment details need, as .vmat text and small generated textures: metal finishes,
/// lit panels and wire (s&amp;box Complex's self-illumination, scrolled by the shader itself, so a
/// sign moves with no code), worn and dirty versions of a cloth, and a cut-out for holes.
/// </summary>
public static class DetailMaterials
{
	public static readonly string[] Finishes = { "Silver", "Chrome", "Gunmetal", "Black", "Gold", "Brass", "Copper" };

	public static (Vec3 Colour, float Metal, float Rough) Finish( string name ) => name switch
	{
		"Chrome" => (new Vec3( 0.92f, 0.93f, 0.95f ), 1f, 0.08f),
		"Gunmetal" => (new Vec3( 0.32f, 0.33f, 0.36f ), 1f, 0.35f),
		"Black" => (new Vec3( 0.05f, 0.05f, 0.06f ), 0.6f, 0.35f),
		"Gold" => (new Vec3( 1f, 0.78f, 0.34f ), 1f, 0.22f),
		"Brass" => (new Vec3( 0.82f, 0.66f, 0.36f ), 1f, 0.35f),
		"Copper" => (new Vec3( 0.95f, 0.6f, 0.45f ), 1f, 0.3f),
		_ => (new Vec3( 0.8f, 0.8f, 0.82f ), 1f, 0.25f),
	};

	public static DetailMaterial Metal( string key, int slot, string finish )
	{
		var (c, m, r) = Finish( finish );
		return new DetailMaterial { Kind = "hard", Key = key, Slot = slot, Colour = c, Metalness = m, Roughness = r };
	}

	public static DetailMaterial Dark( string key, int slot ) =>
		new() { Kind = "hard", Key = key, Slot = slot, Colour = new Vec3( 0.02f, 0.02f, 0.02f ), Metalness = 0f, Roughness = 0.9f };

	public static readonly string[] Patterns = { "Solid", "Scrolling text", "Chevrons", "Equaliser", "Code rain", "Grid pulse", "Dots" };

	/// <summary>A cloth colour as it goes worn: paler, greyer.</summary>
	public static Vec3 Worn( Vec3 c )
	{
		var grey = (c.x + c.y + c.z) / 3f;
		var desat = Vec3.Lerp( c, new Vec3( grey, grey, grey ), 0.35f );
		return Vec3.Lerp( desat, new Vec3( 0.92f, 0.92f, 0.9f ), 0.3f );
	}

	/// <summary>A cloth colour with grime in it: darker, browner.</summary>
	public static Vec3 Dirty( Vec3 c ) => c * 0.55f + new Vec3( 0.2f, 0.15f, 0.09f ) * 0.45f;

	static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

	/// <summary>A plain material: colour, metalness and roughness, no textures.</summary>
	public static string HardVmat( DetailMaterial m ) =>
		"// THIS FILE IS AUTO-GENERATED by an Effigy garment detail. Edit the feature, not this file.\n" +
		"Layer0\n{\n" +
		"\tshader \"complex.vfx\"\n" +
		$"\tg_vColorTint \"[{F( m.Colour.x )} {F( m.Colour.y )} {F( m.Colour.z )} 0.000000]\"\n" +
		"\tg_flModelTintAmount \"1.000\"\n" +
		$"\tg_flMetalness \"{F( m.Metalness )}\"\n" +
		$"\tg_flRoughnessScaleFactor \"{F( m.Roughness )}\"\n" +
		"}\n";

	/// <summary>
	/// A lit surface: the pattern as colour and as the self-illumination mask, scrolled sideways by
	/// the shader at <see cref="DetailMaterial.Speed"/> tiles a second. The window onto the tile is
	/// set by the texture-coordinate scale, so a sign shows a few letters at a time.
	/// </summary>
	public static string GlowVmat( DetailMaterial m, string colorPng, string maskPng, float uScale )
	{
		return
			"// THIS FILE IS AUTO-GENERATED by an Effigy garment detail. Edit the feature, not this file.\n" +
			"Layer0\n{\n" +
			"\tshader \"complex.vfx\"\n" +
			"\tF_SELF_ILLUM 1\n" +
			$"\tTextureColor \"{colorPng}\"\n" +
			$"\tTextureSelfIllumMask \"{maskPng}\"\n" +
			$"\tg_flSelfIllumBrightness \"{F( m.Brightness )}\"\n" +
			"\tg_flSelfIllumAlbedoFactor \"1.000\"\n" +
			"\tg_flMetalness \"0.000\"\n" +
			"\tg_flRoughnessScaleFactor \"0.350\"\n" +
			$"\tg_vTexCoordScale \"[{F( uScale )} 1.000]\"\n" +
			$"\tg_vTexCoordScrollSpeed \"[{F( m.Speed )} 0.000]\"\n" +
			"}\n";
	}

	/// <summary>Faces on this slot are not drawn: a hole cut in the cloth.</summary>
	public static string HoleVmat( string clearPng ) =>
		"// THIS FILE IS AUTO-GENERATED by an Effigy garment detail. Edit the feature, not this file.\n" +
		"Layer0\n{\n" +
		"\tshader \"complex.vfx\"\n" +
		"\tF_ALPHA_TEST 1\n" +
		$"\tTextureColor \"{clearPng}\"\n" +
		$"\tTextureTranslucency \"{clearPng}\"\n" +
		"\tg_flAlphaTestReference \"0.500\"\n" +
		"}\n";

	/// <summary>A 4x4 all-clear image for the hole material.</summary>
	public static byte[] Clear() => new byte[4 * 4 * 4];

	/// <summary>
	/// The lit pattern, as (colour RGBA, mask RGBA, width, height, uScale). Light is drawn as round
	/// LEDs on a grid for the panel patterns, so it reads as a real display rather than a glowing
	/// rectangle; a solid strip is one even glow.
	/// </summary>
	public static (byte[] Color, byte[] Mask, int Width, int Height, float UScale) GlowMaps( DetailMaterial m )
	{
		const int pitch = 8;
		var rows = 12;
		var visible = Math.Max( 4, (int)MathF.Round( rows * MathF.Max( 0.25f, m.Aspect ) ) );
		var level = new List<float[]>();

		switch ( m.Pattern )
		{
			case "Scrolling text":
			{
				var text = string.IsNullOrWhiteSpace( m.Text ) ? "EFFIGY" : m.Text.ToUpperInvariant();
				text = text.Substring( 0, Math.Min( text.Length, 128 ) );
				var cols = new List<float[]>();

				foreach ( var ch in text + "   " )
				{
					var glyph = Font.Glyph( ch );

					for ( var gx = 0; gx < 5; gx++ )
					{
						var col = new float[rows];

						for ( var gy = 0; gy < 7; gy++ )
							if ( (glyph[gy] >> (4 - gx) & 1) != 0 )
								col[gy + 2] = 1f;

						cols.Add( col );
					}

					cols.Add( new float[rows] );
				}

				level.AddRange( cols );
				break;
			}

			case "Chevrons":
			{
				for ( var x = 0; x < 24; x++ )
				{
					var col = new float[rows];

					for ( var y = 0; y < rows; y++ )
					{
						var d = (x + MathF.Abs( y - (rows - 1) * 0.5f )) % 8f;
						col[y] = d < 3f ? 1f : 0f;
					}

					level.Add( col );
				}

				break;
			}

			case "Equaliser":
			{
				var rng = new Random( 7 );

				for ( var x = 0; x < 64; x++ )
				{
					var col = new float[rows];
					var h = x % 3 == 2 ? 0 : 2 + rng.Next( rows - 2 );

					for ( var y = 0; y < h; y++ )
						col[rows - 1 - y] = y > rows * 0.7f ? 0.7f : 1f;

					level.Add( col );
				}

				break;
			}

			case "Code rain":
			{
				var rng = new Random( 11 );

				for ( var x = 0; x < 48; x++ )
				{
					var col = new float[rows];
					var head = rng.Next( rows );
					var len = 3 + rng.Next( 6 );

					for ( var k = 0; k < len; k++ )
						col[(head - k + rows * 4) % rows] = k == 0 ? 1f : 0.85f - k * 0.1f;

					level.Add( col );
				}

				break;
			}

			case "Grid pulse":
			{
				for ( var x = 0; x < 32; x++ )
				{
					var col = new float[rows];
					var wave = 0.5f + 0.5f * MathF.Cos( x / 32f * MathF.PI * 2f );

					for ( var y = 0; y < rows; y++ )
						col[y] = (x % 4 == 0 || y % 4 == 0) ? 1f : wave * 0.35f;

					level.Add( col );
				}

				break;
			}

			case "Dots":
			{
				for ( var x = 0; x < 8; x++ )
				{
					var col = new float[rows];

					for ( var y = 0; y < rows; y++ )
						col[y] = x % 2 == 0 ? (x % 8 == 0 ? 1f : 0.45f) : 0f;

					level.Add( col );
				}

				break;
			}

			default:
			{
				// Solid: one even glow, no LED grid.
				var c = new byte[16 * 16 * 4];
				var k = new byte[16 * 16 * 4];

				for ( var i = 0; i < 16 * 16; i++ )
				{
					c[i * 4] = B( m.Colour.x );
					c[i * 4 + 1] = B( m.Colour.y );
					c[i * 4 + 2] = B( m.Colour.z );
					c[i * 4 + 3] = 255;
					k[i * 4] = k[i * 4 + 1] = k[i * 4 + 2] = k[i * 4 + 3] = 255;
				}

				return (c, k, 16, 16, 1f);
			}
		}

		// A power of two wide, so the tile wraps cleanly however long the text is.
		var columns = level.Count;
		var width = 16;

		while ( width < columns * pitch )
			width *= 2;

		columns = width / pitch;

		while ( level.Count < columns )
			level.Add( new float[rows] );

		var height = rows * pitch;
		var color = new byte[width * height * 4];
		var mask = new byte[width * height * 4];

		for ( var y = 0; y < height; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var cx = x / pitch;
				var cy = y / pitch;
				var fx = (x % pitch + 0.5f) / pitch - 0.5f;
				var fy = (y % pitch + 0.5f) / pitch - 0.5f;
				var dot = 1f - DetailShapes.Smooth( 0.28f, 0.42f, MathF.Sqrt( fx * fx + fy * fy ) );
				var on = level[cx][cy] * dot;
				var i = (y * width + x) * 4;

				// Unlit LEDs still show faintly, the way a real panel's dark dots do.
				var shade = MathF.Max( on, dot * 0.06f );
				color[i] = B( m.Colour.x * shade );
				color[i + 1] = B( m.Colour.y * shade );
				color[i + 2] = B( m.Colour.z * shade );
				color[i + 3] = 255;
				mask[i] = mask[i + 1] = mask[i + 2] = B( on );
				mask[i + 3] = 255;
			}
		}

		var uScale = MathF.Min( 1f, visible / (float)columns );
		return (color, mask, width, height, uScale);
	}

	static byte B( float v ) => (byte)Math.Clamp( (int)MathF.Round( v * 255f ), 0, 255 );

	/// <summary>A 5x7 capitals-and-digits font for scrolling signs. Each row is five bits, left
	/// column in the high bit.</summary>
	public static class Font
	{
		public static int[] Glyph( char c ) => c switch
		{
			'A' => new[] { 14, 17, 17, 31, 17, 17, 17 },
			'B' => new[] { 30, 17, 17, 30, 17, 17, 30 },
			'C' => new[] { 14, 17, 16, 16, 16, 17, 14 },
			'D' => new[] { 30, 17, 17, 17, 17, 17, 30 },
			'E' => new[] { 31, 16, 16, 30, 16, 16, 31 },
			'F' => new[] { 31, 16, 16, 30, 16, 16, 16 },
			'G' => new[] { 14, 17, 16, 23, 17, 17, 15 },
			'H' => new[] { 17, 17, 17, 31, 17, 17, 17 },
			'I' => new[] { 14, 4, 4, 4, 4, 4, 14 },
			'J' => new[] { 7, 2, 2, 2, 2, 18, 12 },
			'K' => new[] { 17, 18, 20, 24, 20, 18, 17 },
			'L' => new[] { 16, 16, 16, 16, 16, 16, 31 },
			'M' => new[] { 17, 27, 21, 21, 17, 17, 17 },
			'N' => new[] { 17, 17, 25, 21, 19, 17, 17 },
			'O' => new[] { 14, 17, 17, 17, 17, 17, 14 },
			'P' => new[] { 30, 17, 17, 30, 16, 16, 16 },
			'Q' => new[] { 14, 17, 17, 17, 21, 18, 13 },
			'R' => new[] { 30, 17, 17, 30, 20, 18, 17 },
			'S' => new[] { 15, 16, 16, 14, 1, 1, 30 },
			'T' => new[] { 31, 4, 4, 4, 4, 4, 4 },
			'U' => new[] { 17, 17, 17, 17, 17, 17, 14 },
			'V' => new[] { 17, 17, 17, 17, 17, 10, 4 },
			'W' => new[] { 17, 17, 17, 21, 21, 21, 10 },
			'X' => new[] { 17, 17, 10, 4, 10, 17, 17 },
			'Y' => new[] { 17, 17, 10, 4, 4, 4, 4 },
			'Z' => new[] { 31, 1, 2, 4, 8, 16, 31 },
			'0' => new[] { 14, 17, 19, 21, 25, 17, 14 },
			'1' => new[] { 4, 12, 4, 4, 4, 4, 14 },
			'2' => new[] { 14, 17, 1, 2, 4, 8, 31 },
			'3' => new[] { 31, 2, 4, 2, 1, 17, 14 },
			'4' => new[] { 2, 6, 10, 18, 31, 2, 2 },
			'5' => new[] { 31, 16, 30, 1, 1, 17, 14 },
			'6' => new[] { 6, 8, 16, 30, 17, 17, 14 },
			'7' => new[] { 31, 1, 2, 4, 8, 8, 8 },
			'8' => new[] { 14, 17, 17, 14, 17, 17, 14 },
			'9' => new[] { 14, 17, 17, 15, 1, 2, 12 },
			'!' => new[] { 4, 4, 4, 4, 4, 0, 4 },
			'?' => new[] { 14, 17, 1, 2, 4, 0, 4 },
			'.' => new[] { 0, 0, 0, 0, 0, 12, 12 },
			'-' => new[] { 0, 0, 0, 31, 0, 0, 0 },
			'+' => new[] { 0, 4, 4, 31, 4, 4, 0 },
			':' => new[] { 0, 12, 12, 0, 12, 12, 0 },
			'/' => new[] { 1, 1, 2, 4, 8, 16, 16 },
			'<' => new[] { 2, 4, 8, 16, 8, 4, 2 },
			'>' => new[] { 8, 4, 2, 1, 2, 4, 8 },
			'#' => new[] { 10, 10, 31, 10, 31, 10, 10 },
			'*' => new[] { 0, 4, 21, 14, 21, 4, 0 },
			'&' => new[] { 12, 18, 20, 8, 21, 18, 13 },
			_ => new int[7],
		};
	}
}
