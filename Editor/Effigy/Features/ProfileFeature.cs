using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A body from two outlines: how wide it is at each height (the front view) and how deep (the
/// side view). Rings are stacked up the height and skinned. This is how a character block-out
/// is actually described — "narrow at the ankle, wide at the hip, narrow at the waist" — and it
/// is a far better fit for an agent than pushing vertices: a torso is eight numbers.
/// </summary>
public static class ProfileBody
{
	/// <param name="front">(height, half-width) pairs, any order; width runs along y.</param>
	/// <param name="side">(height, half-depth) pairs; depth runs along x. Empty uses the front.</param>
	/// <param name="squareness">0 is an ellipse at every ring, 1 is nearly a rectangle.</param>
	public static PolyMesh Build( IReadOnlyList<Vec2> front, IReadOnlyList<Vec2> side, int sides, float squareness, int material )
		=> Build( front, side, null, null, sides, squareness, material, 2 );

	/// <param name="offsets">(height, forward, sideways) triples: where each ring's centre sits,
	/// interpolated between them. This is what curves a limb — a tail that sweeps back, an arm
	/// that hangs forward — without a second feature.</param>
	/// <param name="twists">(height, degrees) pairs: each ring turned about the stacking axis.</param>
	/// <param name="axis">Which world axis the heights run along: 0 x (forward), 1 y (left), 2 z
	/// (up). A leg stacks along z; an arm held out stacks along y; a tail along x.</param>
	public static PolyMesh Build( IReadOnlyList<Vec2> front, IReadOnlyList<Vec2> side, IReadOnlyList<Vec3> offsets, IReadOnlyList<Vec2> twists, int sides, float squareness, int material, int axis )
	{
		if ( front is null || front.Count < 2 )
			throw new ArgumentException( "A profile needs at least two heights.", nameof( front ) );

		var f = front.OrderBy( p => p.x ).ToList();
		var s = side is { Count: >= 2 } ? side.OrderBy( p => p.x ).ToList() : f;

		// Every height either outline names, so a step in one is honoured by the other.
		var o = offsets is { Count: > 0 } ? offsets.OrderBy( p => p.x ).ToList() : null;
		var t = twists is { Count: > 0 } ? twists.OrderBy( p => p.x ).ToList() : null;

		var heights = f.Select( p => p.x ).Concat( s.Select( p => p.x ) )
			.Concat( o?.Select( p => p.x ) ?? Enumerable.Empty<float>() )
			.Concat( t?.Select( p => p.x ) ?? Enumerable.Empty<float>() )
			.Where( z => z >= MathF.Max( f[0].x, s[0].x ) - 1e-6f && z <= MathF.Min( f[^1].x, s[^1].x ) + 1e-6f )
			.Distinct().OrderBy( z => z ).ToList();

		// Rings only at the named heights would make a curve a polyline and a twist a corkscrew of
		// facets. Between stations, add rings: three per gap for a curve, and enough that no ring
		// turns more than twelve degrees from the last for a twist.
		if ( o is { Count: > 1 } || t is { Count: > 1 } )
		{
			var extra = new List<float>();

			for ( var i = 0; i + 1 < heights.Count; i++ )
			{
				var steps = o is { Count: > 1 } ? 3 : 1;

				if ( t is { Count: > 1 } )
				{
					var turn = MathF.Abs( Sample( t, heights[i + 1] ) - Sample( t, heights[i] ) );
					steps = Math.Max( steps, (int)MathF.Ceiling( turn / 12f ) );
				}

				for ( var k = 1; k < steps; k++ )
					extra.Add( heights[i] + (heights[i + 1] - heights[i]) * k / steps );
			}

			heights = heights.Concat( extra ).Distinct().OrderBy( z => z ).ToList();
		}

		if ( heights.Count < 2 )
			throw new ArgumentException( "The front and side outlines do not overlap in height.", nameof( side ) );

		sides = Math.Max( 4, sides );
		var n = 2f + Math.Clamp( squareness, 0f, 1f ) * 10f;
		var rings = new List<List<Vec3>>();

		foreach ( var z in heights )
		{
			var halfWidth = MathF.Max( Sample( f, z ), 1e-4f );
			var halfDepth = MathF.Max( Sample( s, z ), 1e-4f );
			var offset = o is null ? Vec2.Zero : SampleOffset( o, z );
			var twist = t is null ? 0f : Sample( t, z ) * MathF.PI / 180f;
			var ring = new List<Vec3>( sides );

			for ( var k = 0; k < sides; k++ )
			{
				var a = k * MathF.PI * 2f / sides;
				var c = MathF.Cos( a );
				var si = MathF.Sin( a );
				var d = MathF.Sign( c ) * MathF.Pow( MathF.Abs( c ), 2f / n ) * halfDepth;
				var w = MathF.Sign( si ) * MathF.Pow( MathF.Abs( si ), 2f / n ) * halfWidth;

				if ( twist != 0f )
				{
					var ct = MathF.Cos( twist ); var st = MathF.Sin( twist );
					(d, w) = (d * ct - w * st, d * st + w * ct);
				}

				d += offset.x;
				w += offset.y;

				// depth d and width w sit across the stacking axis; z runs along it.
				ring.Add( axis switch
				{
					0 => new Vec3( z, w, d ),
					1 => new Vec3( d, z, w ),
					_ => new Vec3( d, w, z ),
				} );
			}

			rings.Add( ring );
		}

		return Skinner.Skin( rings, capEnds: true, material );
	}

	/// <summary>The centre offset at a height: a Catmull-Rom through the stations, so the spine is
	/// one smooth curve rather than eased hops between stations — those flatten at every station
	/// and read as ripples once the body is subdivided.</summary>
	static Vec2 SampleOffset( List<Vec3> offsets, float z )
	{
		if ( offsets.Count == 1 || z <= offsets[0].x ) return new Vec2( offsets[0].y, offsets[0].z );
		if ( z >= offsets[^1].x ) return new Vec2( offsets[^1].y, offsets[^1].z );

		var i = 0;
		while ( i + 2 < offsets.Count && z > offsets[i + 1].x ) i++;

		Vec3 At( int k ) => offsets[Math.Clamp( k, 0, offsets.Count - 1 )];
		var p0 = At( i - 1 ); var p1 = At( i ); var p2 = At( i + 1 ); var p3 = At( i + 2 );
		var span = p2.x - p1.x;
		var t = span < 1e-8f ? 0f : (z - p1.x) / span;

		// Non-uniform Catmull-Rom on the height parameter, so unevenly spaced stations do not overshoot.
		float CR( float a, float b, float c, float d, float ta, float tb, float tc, float td, float u )
		{
			float L( float x, float y, float tx, float ty ) => MathF.Abs( ty - tx ) < 1e-8f ? x : x * ((ty - u) / (ty - tx)) + y * ((u - tx) / (ty - tx));
			var a1 = L( a, b, ta, tb ); var a2 = L( b, c, tb, tc ); var a3 = L( c, d, tc, td );
			var b1 = L( a1, a2, ta, tc ); var b2 = L( a2, a3, tb, td );
			return L( b1, b2, tb, tc );
		}

		var ta = p0.x; var tb = p1.x; var tc = p2.x; var td = p3.x;
		if ( ta >= tb ) ta = tb - MathF.Max( span, 1e-3f );
		if ( td <= tc ) td = tc + MathF.Max( span, 1e-3f );
		var u = tb + t * span;

		return new Vec2( CR( p0.y, p1.y, p2.y, p3.y, ta, tb, tc, td, u ), CR( p0.z, p1.z, p2.z, p3.z, ta, tb, tc, td, u ) );
	}

	static float Sample( List<Vec2> outline, float z )
	{
		if ( z <= outline[0].x ) return outline[0].y;
		if ( z >= outline[^1].x ) return outline[^1].y;

		for ( var i = 0; i + 1 < outline.Count; i++ )
		{
			if ( z > outline[i + 1].x )
				continue;

			var span = outline[i + 1].x - outline[i].x;
			var t = span < 1e-8f ? 0f : (z - outline[i].x) / span;
			return outline[i].y + (outline[i + 1].y - outline[i].y) * t;
		}

		return outline[^1].y;
	}
}

/// <summary>A body from a front outline and a side outline. See <see cref="ProfileBody"/>.</summary>
public sealed class ProfileFeature : Feature
{
	public override string TypeName => "Profile body";

	/// <summary>`height,half-width;…` pairs, typed. Width is left–right (y). Text rather than a point
	/// list so it can be edited in the dialog as well as by a script; the default is a small torso,
	/// so a fresh one builds and shows what the numbers mean.</summary>
	public readonly StringParam Front = new( "Front (height,half-width;…)", "0,4;8,6;20,6;30,5;34,3" );

	/// <summary>`height,half-depth;…` pairs. Depth is front–back (x). Empty means the same as the front.</summary>
	public readonly StringParam Side = new( "Side (height,half-depth;…)", "" );

	/// <summary>`height,forward,sideways;…` — where the ring's centre sits at each height. This is
	/// how a limb curves: an arm that hangs forward, a tail that sweeps back.</summary>
	public readonly StringParam Curve = new( "Curve (height,forward,side;…)", "" );

	/// <summary>`height,degrees;…` — each ring turned about the axis. A twisted horn, a wrist.</summary>
	public readonly StringParam Twist = new( "Twist (height,degrees;…)", "" );

	/// <summary>Which way the heights run. Up for a torso or a leg; forward for a tail or a snout;
	/// left for an arm held out.</summary>
	public readonly ChoiceParam Along = new( "Along", new[] { "Up (z)", "Forward (x)", "Left (y)" } );

	/// <summary>Also make the mirror across the body's left–right plane, as a second body: one line
	/// for both legs. Names get L and R.</summary>
	public readonly BoolParam Mirror = new( "Mirror left-right", false );

	public readonly IntParam Sides = new( "Sides", 16, 4, 64 );
	public readonly FloatParam Squareness = new( "Squareness", 0.15f, 0f, 1f );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Front, Side, Curve, Twist, Along, Mirror, Sides, Squareness, Position, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Twist, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var front = Parse( Front.Value );
		var side = Parse( Side.Value );

		if ( front.Count < 2 )
		{
			FailOn( "Front (height,half-width;…)",
				"The front outline needs at least two pairs.",
				$"Front is '{Front.Value}', which gives {front.Count} usable pair(s).",
				"Write it as height,half-width pairs separated by semicolons: 0,4;20,6;40,3",
				"Add Side pairs too for a depth that differs from the width" );
		}

		var axis = Along.Index switch { 1 => 0, 2 => 1, _ => 2 };
		var mesh = ProfileBody.Build( front, side, ParseTriples( Curve.Value ), Parse( Twist.Value ), Sides.Clamped, Squareness.Clamped, Material.Clamped, axis );

		if ( Position.Value.LengthSquared > 0f )
			MeshTransform.Apply( mesh, Xform.Translate( Position.Value ) );

		if ( !Mirror.Value )
		{
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
			return;
		}

		// The mirror is a second body, flipped across y = 0 with its winding put back. "Leg" becomes
		// "Leg L" and "Leg R"; a name already ending in L or R keeps its side and mirrors to the other.
		var mirrored = mesh.Clone();
		MeshTransform.Apply( mirrored, Xform.Scale( new Vec3( 1, -1, 1 ) ) );
		foreach ( var face in mirrored.Faces )
			Array.Reverse( face.Indices );

		var (left, right) = SideNames( Name );
		var thisIsLeft = Position.Value.y >= 0f;
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), thisIsLeft ? left : right, mesh ) );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), thisIsLeft ? right : left, mirrored ) );
	}

	static (string Left, string Right) SideNames( string name )
	{
		name = string.IsNullOrWhiteSpace( name ) ? "Profile" : name.Trim();

		if ( name.EndsWith( "_L", StringComparison.OrdinalIgnoreCase ) || name.EndsWith( " L", StringComparison.OrdinalIgnoreCase ) )
			return (name, name[..^1] + "R");

		if ( name.EndsWith( "_R", StringComparison.OrdinalIgnoreCase ) || name.EndsWith( " R", StringComparison.OrdinalIgnoreCase ) )
			return (name[..^1] + "L", name);

		return (name + " L", name + " R");
	}

	/// <summary>`a,b,c;…` to triples, for the curve.</summary>
	public static List<Vec3> ParseTriples( string text )
	{
		var triples = new List<Vec3>();

		foreach ( var part in (text ?? "").Split( new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			var abc = part.Split( ',' );

			if ( abc.Length == 3
				&& float.TryParse( abc[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a )
				&& float.TryParse( abc[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b )
				&& float.TryParse( abc[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c ) )
				triples.Add( new Vec3( a, b, c ) );
		}

		return triples;
	}

	/// <summary>`a,b;a,b;…` to pairs. Spaces are allowed anywhere; a pair that does not parse is skipped.</summary>
	public static List<Vec2> Parse( string text )
	{
		var pairs = new List<Vec2>();

		foreach ( var part in (text ?? "").Split( new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			var ab = part.Split( ',' );

			if ( ab.Length == 2
				&& float.TryParse( ab[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a )
				&& float.TryParse( ab[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b ) )
				pairs.Add( new Vec2( a, b ) );
		}

		return pairs;
	}
}
