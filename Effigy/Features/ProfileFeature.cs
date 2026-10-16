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
		=> Build( front, side, new ProfileShape { Offsets = offsets, Twists = twists, Sides = sides, Squareness = squareness, Material = material, Axis = axis } ).Mesh;

	/// <summary>The full form: every option on <see cref="ProfileShape"/>, and the faces a caller
	/// needs to know about afterwards (the caps, for joining a limb on).</summary>
	public static ProfileBuild Build( IReadOnlyList<Vec2> front, IReadOnlyList<Vec2> side, ProfileShape shape )
	{
		if ( front is null || front.Count < 2 )
			throw new ArgumentException( "A profile needs at least two heights.", nameof( front ) );

		shape ??= new ProfileShape();

		var f = front.OrderBy( p => p.x ).ToList();
		var s = side is { Count: >= 2 } ? side.OrderBy( p => p.x ).ToList() : f;

		// Every height either outline names, so a step in one is honoured by the other.
		var o = shape.Offsets is { Count: > 0 } ? shape.Offsets.OrderBy( p => p.x ).ToList() : null;
		var t = shape.Twists is { Count: > 0 } ? shape.Twists.OrderBy( p => p.x ).ToList() : null;
		var sq = Sorted( shape.SquarenessCurve );
		var flat = Sorted( shape.Flatten );
		var keel = Sorted( shape.Keel );

		var heights = f.Select( p => p.x ).Concat( s.Select( p => p.x ) )
			.Concat( o?.Select( p => p.x ) ?? Enumerable.Empty<float>() )
			.Concat( t?.Select( p => p.x ) ?? Enumerable.Empty<float>() )
			.Concat( sq?.Select( p => p.x ) ?? Enumerable.Empty<float>() )
			.Concat( flat?.Select( p => p.x ) ?? Enumerable.Empty<float>() )
			.Concat( keel?.Select( p => p.x ) ?? Enumerable.Empty<float>() )
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

		var sides = Math.Max( 4, shape.Sides );
		var custom = shape.Axis == AxisCustom;
		var axis = custom ? 2 : Math.Clamp( shape.Axis, 0, 2 );
		var rings = new List<List<Vec3>>();

		// Each ring is laid out as (d, w) about the stacking axis, then placed by the axis switch
		// below. For Up that placement is the identity, so the ring runs anticlockwise about +z and
		// Skinner winds the skin outward. For Forward and Left the placement swaps two axes - a
		// mirror - which turns the ring clockwise and turned every Forward/Left profile inside out:
		// back faces outward, normals pointing in, so the engine culled the outside and lit the
		// inside. Walking the ring the other way for those two axes undoes the mirror. A custom
		// direction is built Up and then turned, and a turn is not a mirror.
		var winding = axis == 2 ? 1f : -1f;

		foreach ( var z in heights )
		{
			var halfWidth = MathF.Max( Sample( f, z ), 1e-4f );
			var halfDepth = MathF.Max( Sample( s, z ), 1e-4f );
			var offset = o is null ? Vec2.Zero : SampleOffset( o, z );
			var twist = t is null ? 0f : Sample( t, z ) * MathF.PI / 180f;
			var n = 2f + Math.Clamp( sq is null ? shape.Squareness : Sample( sq, z ), 0f, 1f ) * 10f;
			var flatten = flat is null ? 0f : Math.Clamp( Sample( flat, z ), -1f, 1f );
			var keelAmount = keel is null ? 0f : Math.Clamp( Sample( keel, z ), -1f, 1f );
			var ring = new List<Vec3>( sides );

			for ( var k = 0; k < sides; k++ )
			{
				var a = winding * k * MathF.PI * 2f / sides;
				var c = MathF.Cos( a );
				var si = MathF.Sin( a );
				var d = MathF.Sign( c ) * MathF.Pow( MathF.Abs( c ), 2f / n ) * halfDepth;
				var w = MathF.Sign( si ) * MathF.Pow( MathF.Abs( si ), 2f / n ) * halfWidth;

				// Flatten squashes one half of the section toward the left-right line through its
				// centre: positive the back half (a flat back, a D-shaped thigh), negative the front.
				if ( flatten > 0f && d < 0f ) d *= 1f - flatten;
				if ( flatten < 0f && d > 0f ) d *= 1f + flatten;

				// Keel pulls the front centre out into a ridge (a breastbone, a shin, a hull), or with
				// a negative amount presses a groove in. A narrow bump so the flanks stay put.
				if ( keelAmount != 0f && c > 0f )
					d += keelAmount * halfDepth * MathF.Pow( c, 8f );

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

		var quadCaps = shape.QuadCaps && sides % 2 == 0;
		var mesh = Skinner.Skin( rings, capEnds: !quadCaps, shape.Material );
		var result = new ProfileBuild { Mesh = mesh, Sides = sides, Rings = rings.Count, QuadCaps = quadCaps };
		var sideFaces = (rings.Count - 1) * sides;

		if ( quadCaps )
		{
			// Far cap keeps the ring's winding, the near one reverses it, as Skinner's single faces do.
			result.FarCap = AddQuadCap( mesh, (rings.Count - 1) * sides, sides, reverse: false, shape.Material );
			result.NearCap = AddQuadCap( mesh, 0, sides, reverse: true, shape.Material );
		}
		else
		{
			// Skinner adds the far cap, then the near one, after the side quads.
			result.FarCap = new List<int> { sideFaces };
			result.NearCap = new List<int> { sideFaces + 1 };
		}

		if ( shape.FitLimit )
		{
			result.FitMiss = SubdivisionLimit.FitToLimit( mesh, 400, 1e-4f * MathF.Max( 1f, heights[^1] - heights[0] ) );

			// A one-face cap's centre is not a vertex, so nothing pins it; say how far short it falls.
			if ( !quadCaps )
				result.CapShortfall = CapShortfall( mesh, result, heights[^1] - heights[0] );
		}

		if ( custom )
			MeshTransform.Apply( mesh, Turn( shape.Direction, shape.Roll ) );

		return result;
	}

	/// <summary>The Along option that reads <see cref="ProfileShape.Direction"/>.</summary>
	public const int AxisCustom = 3;

	/// <summary>The turn that takes +z, the direction a profile is built along, onto
	/// <paramref name="direction"/> by the shortest way, after a roll of <paramref name="rollDegrees"/>
	/// about it. Shortest keeps the profile's width running left-right whenever the direction
	/// lies in the forward-up plane, which is where most limbs and tails point.</summary>
	public static Xform Turn( Vec3 direction, float rollDegrees )
	{
		var z = new Vec3( 0, 0, 1 );
		var d = direction.LengthSquared > 1e-12f ? direction.Normal : z;
		var roll = Xform.Rotate( z, rollDegrees * MathF.PI / 180f );
		var axis = Vec3.Cross( z, d );
		var sin = axis.Length;
		var cos = Vec3.Dot( z, d );

		Xform turn;

		if ( sin < 1e-6f )
			turn = cos > 0f ? Xform.Identity : Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI );
		else
			turn = Xform.Rotate( axis / sin, MathF.Atan2( sin, cos ) );

		return turn * roll;
	}

	/// <summary>
	/// Close a ring with quads instead of one face: a centre vertex and N/2 quads, each taking two
	/// ring edges. All quads means the whole body subdivides without a single n-gon, the centre is
	/// a vertex the limit fit can pin, and a limb can be joined on with nothing but quads.
	/// </summary>
	static List<int> AddQuadCap( PolyMesh mesh, int start, int sides, bool reverse, int material )
	{
		var centre = Vec3.Zero;

		for ( var k = 0; k < sides; k++ )
			centre += mesh.Positions[start + k];

		var hub = mesh.AddVertex( centre / sides );
		var faces = new List<int>( sides / 2 );

		Vec2 Uv( int k )
		{
			var a = k * MathF.PI * 2f / sides;
			return new Vec2( MathF.Cos( a ) * 0.5f + 0.5f, MathF.Sin( a ) * 0.5f + 0.5f );
		}

		for ( var q = 0; q < sides / 2; q++ )
		{
			var k0 = 2 * q;
			var k1 = (2 * q + 1) % sides;
			var k2 = (2 * q + 2) % sides;

			faces.Add( mesh.Faces.Count );

			if ( !reverse )
				mesh.AddFace( new[] { hub, start + k0, start + k1, start + k2 },
					new[] { new Vec2( 0.5f, 0.5f ), Uv( k0 ), Uv( k1 ), Uv( k2 ) }, material );
			else
				mesh.AddFace( new[] { hub, start + k2, start + k1, start + k0 },
					new[] { new Vec2( 0.5f, 0.5f ), Uv( k2 ), Uv( k1 ), Uv( k0 ) }, material );
		}

		return faces;
	}

	/// <summary>How far inside the outline's ends the subdivided surface stops, for one-face caps: the
	/// limit of each cap's centre against the cap's own plane, measured along the body.</summary>
	static float CapShortfall( PolyMesh cage, ProfileBuild r, float length )
	{
		var (once, map) = CatmullClark.SubdivideWithMap( cage );
		var limit = SubdivisionLimit.LimitPositions( once );
		var worst = 0f;

		foreach ( var (cap, ring) in new[] { (r.NearCap[0], 0), (r.FarCap[0], r.Rings - 1) } )
		{
			var centre = Vec3.Zero;

			for ( var k = 0; k < r.Sides; k++ )
				centre += cage.Positions[ring * r.Sides + k];

			centre /= r.Sides;

			// The body's direction at this end, from the neighbouring ring's centre.
			var other = ring == 0 ? 1 : r.Rings - 2;
			var inner = Vec3.Zero;

			for ( var k = 0; k < r.Sides; k++ )
				inner += cage.Positions[other * r.Sides + k];

			inner /= r.Sides;

			var outward = (centre - inner).Normal;
			var facePoint = map.SourceVertexCount + map.SourceEdgeCount + cap;
			var target = Vec3.Zero;

			// Where the outline puts this end: the ring's limit centre, which the fit pinned.
			for ( var k = 0; k < r.Sides; k++ )
				target += limit[ring * r.Sides + k];

			target /= r.Sides;
			worst = MathF.Max( worst, Vec3.Dot( target - limit[facePoint], outward ) );
		}

		return MathF.Max( 0f, worst );
	}

	static List<Vec2> Sorted( IReadOnlyList<Vec2> curve ) => curve is { Count: > 0 } ? curve.OrderBy( p => p.x ).ToList() : null;

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

/// <summary>Everything about a <see cref="ProfileBody"/> apart from its two outlines.</summary>
public sealed class ProfileShape
{
	/// <summary>(height, forward, sideways): where each ring's centre sits.</summary>
	public IReadOnlyList<Vec3> Offsets;

	/// <summary>(height, degrees): each ring turned about the stacking axis.</summary>
	public IReadOnlyList<Vec2> Twists;

	/// <summary>(height, 0..1): the squareness at each height, in place of <see cref="Squareness"/>.</summary>
	public IReadOnlyList<Vec2> SquarenessCurve;

	/// <summary>(height, −1..1): positive flattens the back half of the section, negative the front.</summary>
	public IReadOnlyList<Vec2> Flatten;

	/// <summary>(height, −1..1): a ridge down the front, as a fraction of the half-depth; negative is a groove.</summary>
	public IReadOnlyList<Vec2> Keel;

	public int Sides = 16;
	public float Squareness = 0.15f;
	public int Material;

	/// <summary>0 forward (x), 1 left (y), 2 up (z), or <see cref="ProfileBody.AxisCustom"/> for <see cref="Direction"/>.</summary>
	public int Axis = 2;

	public Vec3 Direction = new( 0, 0, 1 );

	/// <summary>Degrees the section turns about a custom direction before it is pointed there.</summary>
	public float Roll;

	/// <summary>Close the ends with quads round a centre vertex instead of one face. Needs an even
	/// number of sides; an odd count keeps the single face.</summary>
	public bool QuadCaps;

	/// <summary>Move the cage so its subdivided (limit) surface, not the cage, meets the outline.</summary>
	public bool FitLimit;
}

/// <summary>What <see cref="ProfileBody.Build(IReadOnlyList{Vec2}, IReadOnlyList{Vec2}, ProfileShape)"/> made.</summary>
public sealed class ProfileBuild
{
	public PolyMesh Mesh;
	public int Sides, Rings;
	public bool QuadCaps;

	/// <summary>Faces closing the first and the last ring.</summary>
	public List<int> NearCap = new(), FarCap = new();

	/// <summary>With the limit fit: the largest distance any vertex's limit point still misses its
	/// outline target by.</summary>
	public float FitMiss;

	/// <summary>With the limit fit and one-face caps: how far short of the ends the subdivided
	/// surface stops, because a single cap face has no centre vertex to pin.</summary>
	public float CapShortfall;
}

/// <summary>A body from a front outline and a side outline. See <see cref="ProfileBody"/>.</summary>
public sealed class ProfileFeature : Feature, IScriptValueReader
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
	/// left for an arm held out; a custom direction for anything at an angle — a horn swept back
	/// and out, a splayed leg. A script can give the direction straight away: `along=1,0,1`.</summary>
	public readonly ChoiceParam Along = new( "Along", new[] { "Up (z)", "Forward (x)", "Left (y)", "Custom direction" } );

	/// <summary>With Along set to a custom direction: which way the heights run, as x,y,z. Any
	/// length; only the direction counts.</summary>
	public readonly Vec3Param AlongDirection = new( "Along direction", new Vec3( 0, 0, 1 ) );

	/// <summary>With a custom direction: the section turned about that direction, in degrees, so
	/// its width can face where it should.</summary>
	public readonly FloatParam Roll = new( "Roll", 0f, unit: "deg" );

	/// <summary>Also make the mirror across the body's left–right plane, as a second body: one line
	/// for both legs. Names get L and R.</summary>
	public readonly BoolParam Mirror = new( "Mirror left-right", false );

	public readonly IntParam Sides = new( "Sides", 16, 4, 64 );
	public readonly FloatParam Squareness = new( "Squareness", 0.15f, 0f, 1f );

	/// <summary>`height,amount;…` — squareness that changes up the body, 0 round to 1 boxy, in place
	/// of the single Squareness: a round neck on a square chest.</summary>
	public readonly StringParam SquarenessCurve = new( "Squareness by height (height,amount;…)", "" );

	/// <summary>`height,amount;…` — flatten the back half of the section (positive, up to 1 for flat)
	/// or the front half (negative): a flat back, a D-shaped thigh, a flat sole.</summary>
	public readonly StringParam Flatten = new( "Flatten (height,amount;…)", "" );

	/// <summary>`height,amount;…` — a ridge down the front centre, as a fraction of the depth (a
	/// breastbone, a shin, a hull); negative presses a groove in.</summary>
	public readonly StringParam Keel = new( "Keel (height,amount;…)", "" );

	/// <summary>Which surface the outline describes. Cage is the mesh as built. Smoothed surface moves
	/// the cage out so that, once subdivided, the body is exactly as wide and as tall as the outline
	/// — a leg that reaches the floor after Subdivide.</summary>
	public readonly ChoiceParam Fit = new( "Fit outline to", new[] { "Cage", "Limit (smoothed surface)" } );

	/// <summary>How the two ends close: one face, or quads round a centre point (all quads, needs an
	/// even number of sides). Quads let the smoothed fit reach the very ends and a limb join on.</summary>
	public readonly ChoiceParam Caps = new( "Caps", new[] { "One face", "Quads" } );

	/// <summary>Grow this body out of another one as a single mesh: the name of the body to join it
	/// to. Its first height is where it meets that body.</summary>
	public readonly StringParam Branch = new( "Branch from body", "" );

	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	bool CustomAxis => Along.Index == ProfileBody.AxisCustom;

	public override IReadOnlyList<IParam> Parameters => CustomAxis
		? new IParam[] { Front, Side, Curve, Twist, Along, AlongDirection, Roll, Mirror, Sides, Squareness, SquarenessCurve, Flatten, Keel, Fit, Caps, Branch, Position, Material }
		: new IParam[] { Front, Side, Curve, Twist, Along, Mirror, Sides, Squareness, SquarenessCurve, Flatten, Keel, Fit, Caps, Branch, Position, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Twist, SquarenessCurve, Flatten, Keel, Caps, Material };

	/// <summary>`along=x,y,z` in a build script: three numbers are a custom direction rather than
	/// one of the named options, so a limb at an angle is one word.</summary>
	public bool HandlesScriptValue( string key, string value, out string error )
	{
		error = null;

		if ( !string.Equals( key, "along", StringComparison.OrdinalIgnoreCase ) || !(value ?? "").Contains( ',' ) )
			return false;

		var parts = value.Split( ',' );
		var xyz = new float[3];

		if ( parts.Length != 3 || !parts.Select( ( p, i ) => float.TryParse( p.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out xyz[i] ) ).All( ok => ok ) )
		{
			error = $"'{value}' is not a direction. Give three numbers, x,y,z (1,0,1 is forward and up), or one of {string.Join( ", ", Along.Options )}.";
			return true;
		}

		Along.Index = ProfileBody.AxisCustom;
		AlongDirection.Value = new Vec3( xyz[0], xyz[1], xyz[2] );
		return true;
	}

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

		var sides = Sides.Clamped;

		if ( CustomAxis && AlongDirection.Value.LengthSquared < 1e-12f )
		{
			FailOn( "Along direction",
				"The custom direction is zero.",
				"Along direction is 0,0,0, which points nowhere, so there is no way for the heights to run.",
				"Give a direction such as 0,0,1 (up) or 1,0,1 (forward and up)",
				"Set Along back to Up, Forward or Left" );
		}

		var shape = new ProfileShape
		{
			Offsets = ParseTriples( Curve.Value ),
			Twists = Parse( Twist.Value ),
			SquarenessCurve = ParseCurve( SquarenessCurve, "0,0.1;30,0.6" ),
			Flatten = ParseCurve( Flatten, "10,0;20,0.6" ),
			Keel = ParseCurve( Keel, "10,0;20,0.3" ),
			Sides = sides,
			Squareness = Squareness.Clamped,
			Material = Material.Clamped,
			Axis = Along.Index switch { 1 => 0, 2 => 1, 3 => ProfileBody.AxisCustom, _ => 2 },
			Direction = AlongDirection.Value,
			Roll = Roll.Value,
			QuadCaps = Caps.Index == 1,
			FitLimit = Fit.Index == 1,
		};

		var built = ProfileBody.Build( front, side, shape );
		var mesh = built.Mesh;

		if ( shape.QuadCaps && !built.QuadCaps )
		{
			Warn( "The ends were closed with one face, not quads.",
				$"Quad caps pair the ring's edges two at a time, so they need an even number of sides, and Sides is {sides}.",
				$"Set Sides to {sides + 1}" );
		}

		if ( shape.FitLimit && built.CapShortfall > 1e-3f * MathF.Max( 1f, front.Max( p => p.x ) - front.Min( p => p.x ) ) )
		{
			Warn( $"Once smoothed, the ends stop {built.CapShortfall:0.###} short of the outline.",
				"The fit pins every vertex, but a one-face cap has no vertex at its centre, so subdivision still rounds the ends in.",
				"Set Caps to Quads" + (sides % 2 == 0 ? "" : $" and Sides to {sides + 1}") );
		}

		if ( Position.Value.LengthSquared > 0f )
			MeshTransform.Apply( mesh, Xform.Translate( Position.Value ) );

		var pieces = new List<(string Name, PolyMesh Mesh)>();

		if ( !Mirror.Value )
		{
			pieces.Add( (Name, mesh) );
		}
		else
		{
			// The mirror is a second body, flipped across y = 0. "Leg" becomes "Leg L" and "Leg R"; a name
			// already ending in L or R keeps its side and mirrors to the other. MeshTransform.Apply puts
			// the winding back itself when the transform flips handedness; reversing the faces again
			// here turned every mirrored side inside out.
			var mirrored = mesh.Clone();
			MeshTransform.Apply( mirrored, Xform.Scale( new Vec3( 1, -1, 1 ) ) );

			var (left, right) = SideNames( Name );
			var thisIsLeft = Position.Value.y >= 0f;
			pieces.Add( (thisIsLeft ? left : right, mesh) );
			pieces.Add( (thisIsLeft ? right : left, mirrored) );
		}

		if ( string.IsNullOrWhiteSpace( Branch.Value ) )
		{
			foreach ( var (name, piece) in pieces )
				ctx.Bodies.Add( new Body( ctx.NewBodyId(), name, piece ) );

			return;
		}

		JoinInto( ctx, pieces, built.NearCap );
	}

	/// <summary>Join each piece into the body named by Branch: one mesh, one body, the parent's.</summary>
	void JoinInto( FeatureContext ctx, List<(string Name, PolyMesh Mesh)> pieces, List<int> baseFaces )
	{
		var wanted = Branch.Value.Trim();
		var parent = ctx.Bodies.LastOrDefault( b => string.Equals( b.Name, wanted, StringComparison.OrdinalIgnoreCase ) );

		if ( parent is null )
		{
			var names = ctx.Bodies.Select( b => b.Name ).Where( n => !string.IsNullOrWhiteSpace( n ) ).Distinct().ToList();
			FailOn( "Branch from body",
				$"There is no body called '{wanted}' to grow this out of.",
				names.Count == 0
					? "No body has been built above this feature yet."
					: $"The bodies above this are: {Listed( names )}.",
				"Type the name of the body this limb grows from",
				"Move this feature below the one that makes that body",
				"Clear Branch from body to keep this a separate body" );
		}

		if ( parent.Mesh.HasMorphs || parent.Mesh.IsRigged )
		{
			FailOn( "Branch from body",
				$"'{parent.Name}' already has {(parent.Mesh.IsRigged ? "skin weights" : "shape keys")}, which joining would lose.",
				"Joining rebuilds the body's vertex list, and weights and shape keys are stored per vertex.",
				"Move this feature above the rig or the shape keys",
				"Clear Branch from body to keep this a separate body" );
		}

		foreach ( var (name, piece) in pieces )
		{
			var joined = MeshBranch.Join( parent.Mesh, piece, baseFaces );

			if ( joined.Mesh is null )
			{
				FailOn( "Branch from body",
					$"'{name}' could not be joined to '{parent.Name}': {joined.Problem}",
					joined.Cause,
					"Use an even number of Sides, and quad Caps on the body it grows from",
					"Move the first height so the limb starts on a part of the body made of quads, away from its ends",
					"Use fewer Sides so the opening is smaller than the body is round" );
			}

			parent.Mesh = joined.Mesh;

			if ( joined.StartsInside )
			{
				Warn( $"'{name}' starts inside '{parent.Name}', so the join folds back through it.",
					"The limb's first ring is under the body's surface where they meet.",
					"Move Position or the first height out to the body's surface" );
			}
			else if ( joined.FootprintRatio < 0.5f || joined.FootprintRatio > 2f )
			{
				Warn( $"The opening in '{parent.Name}' is {joined.FootprintRatio:0.##}× the size of '{name}' where it meets it, so the join flares.",
					$"A {joined.PatchA} × {joined.PatchB} patch of the body's faces was opened for the limb's base ring, and those faces are {(joined.FootprintRatio < 1f ? "smaller" : "larger")} than that ring.",
					joined.FootprintRatio < 1f ? "Join the limb before subdividing the body, or use fewer Sides on the body" : "Use more Sides on the limb, or make its first height wider" );
			}
		}
	}

	/// <summary>A `height,amount;…` curve, refusing text that is there but does not read, so a typo
	/// cannot quietly leave the body as it was.</summary>
	List<Vec2> ParseCurve( StringParam param, string example )
	{
		var text = (param.Value ?? "").Trim();

		if ( text.Length == 0 )
			return null;

		var parts = text.Split( new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries ).Where( p => p.Trim().Length > 0 ).ToList();
		var pairs = Parse( text );

		if ( pairs.Count != parts.Count )
		{
			FailOn( param.Label,
				$"{param.Label.Split( '(' )[0].Trim()} has {parts.Count - pairs.Count} part(s) that are not height,amount pairs.",
				$"It reads '{text}'. Each part must be two numbers, a height and an amount, separated by a comma.",
				$"Write it like {example}",
				"Clear it to leave the shape as it is" );
		}

		return pairs;
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
