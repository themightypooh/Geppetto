using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Shared machinery for the two features that build a solid by running a profile through a series
/// of rings and skinning between them: sweep along a path, and loft between sections.
///
/// Both come down to the same three steps — produce a list of rings with the same point count,
/// stitch quads between consecutive rings, cap the two ends — and the only interesting differences
/// are where the rings come from and how they are kept from twisting relative to each other.
/// </summary>
internal static class Skinner
{
	/// <summary>
	/// Stitch a stack of rings into a tube and cap both ends.
	///
	/// Every ring must have the same number of points, in the same winding, and be rotationally
	/// aligned with its neighbours — nothing here checks that, because both callers have already
	/// had to do the work of arranging it and re-deriving it would be guessing.
	/// </summary>
	/// <param name="wrap">
	/// Stitch the last ring back to the first, using the vertices that are already there.
	///
	/// A closed sweep cannot be closed by appending a copy of the first ring instead: the copy
	/// would be new vertices at the same positions, and this mesh is manifold by INDEX rather than
	/// by position, so the tube would still have two open ends that merely happen to touch. It has
	/// to be the same indices, which means it has to be a flag here.
	/// </param>
	public static PolyMesh Skin( IReadOnlyList<List<Vec3>> rings, bool capEnds, int material, bool wrap = false )
	{
		var mesh = new PolyMesh();
		var count = rings[0].Count;

		var starts = new int[rings.Count];

		for ( var r = 0; r < rings.Count; r++ )
		{
			starts[r] = mesh.Positions.Count;

			foreach ( var p in rings[r] )
				mesh.AddVertex( p );
		}

		// One span per gap between rings, plus the closing gap when the stack wraps.
		var spans = wrap ? rings.Count : rings.Count - 1;

		// V runs along the sweep and U around the ring, both by cumulative distance so a texture
		// does not bunch up where the rings happen to be close together.
		var along = new float[spans + 1];

		for ( var r = 1; r <= spans; r++ )
			along[r] = along[r - 1] + (rings[r % rings.Count][0] - rings[r - 1][0]).Length;

		var total = along[^1] > 0f ? along[^1] : 1f;

		var around = new float[count + 1];

		for ( var i = 0; i < count; i++ )
			around[i + 1] = around[i] + (rings[0][(i + 1) % count] - rings[0][i]).Length;

		var perimeter = around[^1] > 0f ? around[^1] : 1f;

		for ( var r = 0; r < spans; r++ )
		{
			var next = (r + 1) % rings.Count;

			for ( var i = 0; i < count; i++ )
			{
				var j = (i + 1) % count;

				var v0 = along[r] / total;
				var v1 = along[r + 1] / total;
				var u0 = around[i] / perimeter;
				var u1 = around[i + 1] / perimeter;

				mesh.AddFace(
					new[] { starts[r] + i, starts[r] + j, starts[next] + j, starts[next] + i },
					new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) },
					material );
			}
		}

		if ( !capEnds )
			return mesh;

		// The far cap keeps the ring's winding and the near one reverses it, so both normals point
		// out of the solid rather than both pointing the same way along the sweep.
		var last = rings.Count - 1;

		var farIndices = new int[count];
		var farUVs = new Vec2[count];
		var nearIndices = new int[count];
		var nearUVs = new Vec2[count];

		for ( var i = 0; i < count; i++ )
		{
			farIndices[i] = starts[last] + i;
			farUVs[i] = new Vec2( around[i] / perimeter, 0f );

			nearIndices[i] = starts[0] + count - 1 - i;
			nearUVs[i] = new Vec2( around[count - 1 - i] / perimeter, 0f );
		}

		mesh.AddFace( farIndices, farUVs, material );
		mesh.AddFace( nearIndices, nearUVs, material );

		return mesh;
	}

	/// <summary>
	/// Resample a closed loop to exactly <paramref name="count"/> points, evenly spaced by arc
	/// length.
	///
	/// Loft needs this because two sections drawn by hand have no reason to share a point count,
	/// and skinning needs them to. Even spacing by LENGTH rather than by index is what stops a
	/// section with a dense corner dragging the whole surface toward it.
	/// </summary>
	public static List<Vec2> Resample( IReadOnlyList<Vec2> loop, int count )
	{
		var n = loop.Count;
		var lengths = new float[n + 1];

		for ( var i = 0; i < n; i++ )
			lengths[i + 1] = lengths[i] + (loop[(i + 1) % n] - loop[i]).Length;

		var perimeter = lengths[n];
		var result = new List<Vec2>( count );

		if ( perimeter < 1e-9f )
		{
			for ( var i = 0; i < count; i++ )
				result.Add( loop[0] );

			return result;
		}

		var at = 0;

		for ( var i = 0; i < count; i++ )
		{
			var target = perimeter * i / count;

			while ( at + 1 < n && lengths[at + 1] < target )
				at++;

			var span = lengths[at + 1] - lengths[at];
			var t = span > 1e-9f ? (target - lengths[at]) / span : 0f;

			var a = loop[at];
			var b = loop[(at + 1) % n];

			result.Add( a + (b - a) * t );
		}

		return result;
	}

	/// <summary>
	/// Rotate a ring's point order so it lines up with the ring before it, and reverse it if the two
	/// wind opposite ways.
	///
	/// WITHOUT THIS A LOFT TWISTS. Two circles resampled independently start wherever their first
	/// curve happened to start, so skinning point 0 to point 0 can put a half-turn of shear into a
	/// surface between two shapes that are the same. Trying every rotation and taking the one with
	/// the least total squared distance is O(n^2) in the ring size and completely dominated by
	/// everything else at these counts.
	/// </summary>
	public static List<Vec3> Align( List<Vec3> ring, IReadOnlyList<Vec3> previous )
	{
		var count = ring.Count;

		// Winding first: a reversed ring can never be brought into agreement by rotation alone, and
		// skinning one to its neighbour turns the surface inside out at that joint.
		var forward = Best( ring, previous, out var forwardScore );

		var flipped = new List<Vec3>( ring );
		flipped.Reverse();

		var backward = Best( flipped, previous, out var backwardScore );

		return backwardScore < forwardScore
			? Rotate( flipped, backward )
			: Rotate( ring, forward );
	}

	static int Best( IReadOnlyList<Vec3> ring, IReadOnlyList<Vec3> previous, out float score )
	{
		var count = ring.Count;
		var bestOffset = 0;

		score = float.MaxValue;

		for ( var offset = 0; offset < count; offset++ )
		{
			var total = 0f;

			for ( var i = 0; i < count; i++ )
				total += (ring[(i + offset) % count] - previous[i]).LengthSquared;

			if ( total >= score )
				continue;

			score = total;
			bestOffset = offset;
		}

		return bestOffset;
	}

	static List<Vec3> Rotate( IReadOnlyList<Vec3> ring, int offset )
	{
		var result = new List<Vec3>( ring.Count );

		for ( var i = 0; i < ring.Count; i++ )
			result.Add( ring[(i + offset) % ring.Count] );

		return result;
	}
}

/// <summary>
/// Run a profile along a path, building a solid out of the swept surface. Onshape's Sweep.
///
/// THE PROFILE IS TAKEN PERPENDICULAR TO THE PATH, not left lying on its own plane. A sweep whose
/// profile keeps its original orientation is an extrusion along a curve, which is a different and
/// much less useful operation — it collapses wherever the path turns toward the profile's normal.
///
/// FRAMES ARE PROPAGATED, NOT RECOMPUTED. The obvious way to orient the profile at each station is
/// to build a frame from the tangent and some fixed up-vector, and it is wrong: wherever the
/// tangent passes near that up-vector the frame spins wildly, and a sweep round a helix visibly
/// corkscrews. Instead each station's frame is the previous one turned by the smallest rotation
/// that carries the old tangent onto the new one. That is the rotation-minimising frame, it has no
/// preferred direction to go unstable near, and it costs one cross product per station.
/// </summary>
public sealed class SweepFeature : SketchConsumingFeature
{
	public override string TypeName => "Sweep";

	/// <summary>
	/// The sketch whose curves form the path. Empty means the sketch before the profile's, which is
	/// the order a person draws them in.
	/// </summary>
	public string PathSketchId = "";

	public readonly FloatParam Twist = new( "Twist", 0f, unit: "deg" );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Twist, Result, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var sketch = ResolveProfileSketch( ctx );
		var profiles = ResolveProfiles( sketch );
		var path = ResolvePath( ctx, sketch );

		if ( path.Count < 2 )
			throw new InvalidOperationException( "The path needs at least two points to sweep along." );

		// A path that returns to where it started is a closed tube: no ends to cap, and the last
		// station is the first one again rather than a station of its own. The comparison is
		// against the path's own scale so a large model does not read as closed just because its
		// absolute gap is small.
		var closed = (path[^1] - path[0]).Length < PathScale( path ) * 1e-3f;

		if ( closed )
		{
			path.RemoveAt( path.Count - 1 );

			if ( path.Count < 3 )
				throw new InvalidOperationException( "A closed path needs at least three stations to sweep along." );
		}

		foreach ( var profile in profiles )
		{
			// A twist cannot be carried round a closed path: the last ring has to be the first
			// ring, and a turned copy of it is not. Dropped rather than applied, and said out loud
			// rather than dropped quietly.
			if ( closed && Twist.Value != 0f )
				Warning = "Twist was ignored: a closed path has to meet itself, so it cannot end turned.";

			var rings = BuildRings( sketch, profile.Outer, path, closed ? 0f : Twist.Value );

			var mesh = Skinner.Skin( rings, !closed, Material.Clamped, wrap: closed );

			if ( profile.HasHoles )
				Warning = "The path was swept round this profile's outer loop only — holes in a swept profile are not carried through yet.";

			Emit( ctx, mesh );
		}
	}

	static float PathScale( IReadOnlyList<Vec3> path )
	{
		var total = 0f;

		for ( var i = 0; i + 1 < path.Count; i++ )
			total += (path[i + 1] - path[i]).Length;

		return MathF.Max( total, 1e-6f );
	}

	/// <summary>
	/// One ring of world points per path station, the profile carried along in a
	/// rotation-minimising frame.
	/// </summary>
	static List<List<Vec3>> BuildRings( Sketch sketch, List<Vec2> loop, List<Vec3> path, float twistDegrees )
	{
		var rings = new List<List<Vec3>>( path.Count );

		// The profile's own centre, so the path threads through the middle of the shape rather than
		// through wherever the sketch origin happened to be.
		var centre = Vec2.Zero;

		foreach ( var p in loop )
			centre += p;

		centre /= loop.Count;

		var tangent = (path[1] - path[0]).Normal;

		// Start by turning the profile's plane so its normal points down the path. Any rotation
		// that does that will do — the twist is then whatever this happens to give, and the Twist
		// parameter exists so it can be corrected.
		var u = sketch.Plane.XAxis;
		var v = sketch.Plane.YAxis;

		Rotate( ref u, ref v, sketch.Plane.Normal, tangent );

		for ( var i = 0; i < path.Count; i++ )
		{
			if ( i > 0 )
			{
				var next = i + 1 < path.Count
					? (path[i + 1] - path[i]).Normal
					: (path[i] - path[i - 1]).Normal;

				Rotate( ref u, ref v, tangent, next );
				tangent = next;
			}

			// Twist accumulates along the path rather than being applied at the end, so a swept
			// square with 90 degrees of twist turns steadily instead of shearing at the last ring.
			var angle = twistDegrees * MathF.PI / 180f * (path.Count > 1 ? i / (float)(path.Count - 1) : 0f);
			var cos = MathF.Cos( angle );
			var sin = MathF.Sin( angle );

			var tu = u * cos + v * sin;
			var tv = v * cos - u * sin;

			var ring = new List<Vec3>( loop.Count );

			foreach ( var p in loop )
			{
				var local = p - centre;
				ring.Add( path[i] + tu * local.x + tv * local.y );
			}

			rings.Add( ring );
		}

		return rings;
	}

	/// <summary>
	/// Turn two axes by the smallest rotation carrying <paramref name="from"/> onto
	/// <paramref name="to"/>. Rodrigues, with the degenerate cases taken out first.
	/// </summary>
	static void Rotate( ref Vec3 u, ref Vec3 v, Vec3 from, Vec3 to )
	{
		var axis = Vec3.Cross( from, to );
		var sin = axis.Length;
		var cos = Vec3.Dot( from, to );

		// Already aligned: nothing to do. Exactly reversed: the rotation is a half turn about any
		// perpendicular axis, and picking one arbitrarily is the best that can be done — a path
		// that doubles straight back on itself has no continuous frame through the reversal.
		if ( sin < 1e-9f )
		{
			if ( cos > 0f )
				return;

			u = -u;
			v = -v;
			return;
		}

		axis = axis / sin;

		u = Turn( u, axis, cos, sin );
		v = Turn( v, axis, cos, sin );
	}

	static Vec3 Turn( Vec3 x, Vec3 axis, float cos, float sin ) =>
		x * cos + Vec3.Cross( axis, x ) * sin + axis * (Vec3.Dot( axis, x ) * (1f - cos));

	/// <summary>
	/// The path, as world points. Walked out of a sketch's curves the way a loop is, except that a
	/// path is normally open — so the walk starts at a free end rather than anywhere.
	/// </summary>
	List<Vec3> ResolvePath( FeatureContext ctx, Sketch profileSketch )
	{
		var pathSketch = ResolvePathSketch( ctx, profileSketch );

		if ( pathSketch is null )
			throw new InvalidOperationException( "A sweep needs a second sketch for its path." );

		if ( ReferenceEquals( pathSketch, profileSketch ) )
			throw new InvalidOperationException( "The path and the profile cannot be the same sketch." );

		var chain = SketchChain.Walk( pathSketch );

		if ( chain.Count < 2 )
		{
			throw new InvalidOperationException(
				"The path sketch has no connected chain of curves to sweep along." );
		}

		return chain.Select( pathSketch.Plane.ToWorld ).ToList();
	}

	/// <summary>
	/// Which sketch holds the profile.
	///
	/// NOT "THE MOST RECENT", which is what every other sketch-consuming feature means by an unset
	/// reference. A sweep takes two sketches and there is no reliable order to them — a path is
	/// drawn after its profile about as often as before it, so "the last one" is a coin flip that
	/// picks the path half the time and then fails with "no closed region", which reads as the
	/// sketch being broken rather than as the two being the wrong way round.
	///
	/// The two roles are distinguishable by what is IN them: a profile is closed and a path is not.
	/// So the profile is the most recent sketch that actually has a closed region, and the path is
	/// whatever else is left. An explicit SketchFeatureId still wins over all of it.
	/// </summary>
	Sketch ResolveProfileSketch( FeatureContext ctx )
	{
		if ( !string.IsNullOrEmpty( SketchFeatureId ) )
			return ResolveSketch( ctx );

		if ( ctx.Sketches.Count == 0 )
			throw new InvalidOperationException( "There is no sketch to use — add a Sketch feature first" );

		var closed = ctx.Sketches.Values.LastOrDefault( s => ProfileFinder.Find( s ).Profiles.Count > 0 );

		return closed ?? ctx.Sketches.Values.Last();
	}

	Sketch ResolvePathSketch( FeatureContext ctx, Sketch profileSketch )
	{
		if ( !string.IsNullOrEmpty( PathSketchId ) )
			return ctx.Sketches.TryGetValue( PathSketchId, out var named ) ? named : null;

		// Whatever is not the profile. Preferring one with no closed region of its own keeps a
		// studio holding two closed sketches from sweeping one round the other by accident.
		var open = ctx.Sketches.Values.LastOrDefault( s =>
			!ReferenceEquals( s, profileSketch ) && ProfileFinder.Find( s ).Profiles.Count == 0 );

		return open ?? ctx.Sketches.Values.LastOrDefault( s => !ReferenceEquals( s, profileSketch ) );
	}
}

/// <summary>
/// Skin a surface between two or more section sketches. Onshape's Loft.
///
/// Sections are resampled to a common point count and rotationally aligned before anything is
/// stitched — see Skinner.Resample and Skinner.Align for why both are needed and what goes wrong
/// without them.
///
/// STRAIGHT BETWEEN SECTIONS, NOT SMOOTHLY THROUGH THEM. Each pair of neighbouring sections is
/// joined by a ruled surface, so a three-section loft has a crease at the middle one. A lofted
/// surface that arrives tangent needs a spline through corresponding points in three dimensions,
/// which is real work and buys a smooth result only when the sections are already well spaced. The
/// honest version first; add sections to get a smoother shape.
/// </summary>
public sealed class LoftFeature : SketchConsumingFeature
{
	public override string TypeName => "Loft";

	/// <summary>
	/// The sketches to loft between, in order. Fewer than two means "every sketch available", which
	/// is what a loft with nothing configured should do rather than fail.
	/// </summary>
	public List<string> Sections = new();

	/// <summary>How many points each section is resampled to. More is smoother and heavier; the
	/// default is enough for a circle to read as round without subdivision.</summary>
	public readonly IntParam Segments = new( "Segments", 24, 3, 512 );

	public readonly BoolParam Closed = new( "Closed", false );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Segments, Closed, Result, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var sketches = ResolveSections( ctx );

		if ( sketches.Count < 2 )
			throw new InvalidOperationException( "A loft needs at least two sections." );

		var count = Segments.Clamped;
		var rings = new List<List<Vec3>>( sketches.Count );

		foreach ( var sketch in sketches )
		{
			var found = ProfileFinder.Find( sketch );

			if ( found.Profiles.Count == 0 )
			{
				throw new InvalidOperationException(
					"One of the loft's sections has no closed region in it." );
			}

			if ( found.Profiles[0].HasHoles )
				Warning = "Loft used each section's outer loop only — holes in a section are not carried through yet.";

			var ring = Skinner.Resample( found.Profiles[0].Outer, count )
				.Select( sketch.Plane.ToWorld )
				.ToList();

			rings.Add( rings.Count == 0 ? ring : Skinner.Align( ring, rings[^1] ) );
		}

		// Closed skins the last section back to the FIRST ONE'S vertices rather than to a copy
		// of them, which is the only way the seam is genuinely closed — see Skinner.Skin.
		Emit( ctx, Skinner.Skin( rings, !Closed.Value, Material.Clamped, wrap: Closed.Value ) );
	}

	List<Sketch> ResolveSections( FeatureContext ctx )
	{
		if ( Sections.Count < 2 )
			return ctx.Sketches.Values.ToList();

		var result = new List<Sketch>( Sections.Count );

		foreach ( var id in Sections )
		{
			if ( !ctx.Sketches.TryGetValue( id, out var sketch ) )
			{
				throw new InvalidOperationException(
					$"Sketch '{id}' is not available at this point in the tree" );
			}

			result.Add( sketch );
		}

		return result;
	}
}

/// <summary>
/// Walking a sketch's curves into a single open or closed chain of points.
///
/// ProfileFinder does something similar and deliberately does not do this: it looks for CLOSED
/// regions and reports open chains as a count it ignores. A sweep path is the opposite — it is
/// normally open, and the one thing it must not do is close.
/// </summary>
public static class SketchChain
{
	/// <summary>
	/// The longest chain of connected curves in a sketch, as points on its plane.
	///
	/// Starts from a free end where there is one, so an open path is walked from its beginning
	/// rather than from somewhere in the middle. A ring with no free end anywhere is walked from an
	/// arbitrary curve, which is the right answer for a closed path.
	/// </summary>
	public static List<Vec2> Walk( Sketch sketch )
	{
		var curves = sketch.Curves.Where( c => !c.Construction && !c.IsClosed ).ToList();

		// A single closed curve IS the path — a circle swept round is a torus.
		if ( curves.Count == 0 )
		{
			var ring = sketch.Curves.FirstOrDefault( c => !c.Construction && c.IsClosed );

			return ring is null ? new List<Vec2>() : ring.Tessellate( sketch, sketch.Tolerance );
		}

		var touching = new Dictionary<int, List<SketchCurve>>();

		foreach ( var curve in curves )
		{
			var (a, b) = curve.Endpoints;

			Link( touching, a, curve );
			Link( touching, b, curve );
		}

		var start = curves[0];
		var startAt = start.Endpoints.A;

		foreach ( var curve in curves )
		{
			var (a, b) = curve.Endpoints;

			if ( touching[a].Count == 1 )
			{
				start = curve;
				startAt = a;
				break;
			}

			if ( touching[b].Count == 1 )
			{
				start = curve;
				startAt = b;
				break;
			}
		}

		var points = new List<Vec2>();
		var used = new HashSet<SketchCurve>();
		var current = start;
		var from = startAt;

		while ( current is not null && used.Add( current ) )
		{
			var samples = current.Tessellate( sketch, sketch.Tolerance );
			var (a, _) = current.Endpoints;

			// Every curve is walked in the direction the chain is travelling, so the samples of one
			// continue the samples of the last rather than doubling back.
			if ( from != a )
				samples.Reverse();

			// The first sample of every curve after the first repeats the last of the one before.
			if ( points.Count > 0 )
				samples.RemoveAt( 0 );

			points.AddRange( samples );

			var (ca, cb) = current.Endpoints;
			var to = from == ca ? cb : ca;

			current = touching[to].FirstOrDefault( c => !used.Contains( c ) );
			from = to;
		}

		return points;
	}

	static void Link( Dictionary<int, List<SketchCurve>> map, int point, SketchCurve curve )
	{
		if ( !map.TryGetValue( point, out var list ) )
			map[point] = list = new List<SketchCurve>();

		list.Add( curve );
	}
}
