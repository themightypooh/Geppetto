using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

public enum BrushKind
{
	Smooth,
	Draw,
	Inflate,
	Grab,
	Flatten,
	Pinch,
	/// <summary>Build up toward a plane set above the local surface (Blender's Clay).</summary>
	Clay,
	/// <summary>Clay with a square footprint laid along the stroke (Blender's Clay Strips).</summary>
	ClayStrips,
	/// <summary>Raise the surface to a set height and no higher, however often the stroke passes (Blender's Layer).</summary>
	Layer,
	/// <summary>Cut a groove and pinch the sides in toward the stroke line (Blender's Crease; ZBrush's Dam Standard).</summary>
	Crease,
	/// <summary>Push out in a rounded, swelling shape (Blender's Blob).</summary>
	Blob,
	/// <summary>Draw with a very sharp falloff, for fine lines (Blender's Draw Sharp).</summary>
	DrawSharp,
	/// <summary>Lift what lies below the local surface plane up to it (Blender's Fill).</summary>
	Fill,
	/// <summary>Cut what stands above the local surface plane down to it (Blender's Scrape).</summary>
	Scrape,
	/// <summary>Grab with a long elastic falloff that reaches well past the radius (Blender's Elastic Grab: a two-scale Kelvinlet).</summary>
	ElasticGrab,
	/// <summary>Drag along the stroke, re-finding the surface under each sample, so it pulls out tentacles and limbs (Blender's Snake Hook).</summary>
	SnakeHook,
	/// <summary>Smear the surface along the stroke and flatten it a little, like a thumb through clay (Blender's Thumb).</summary>
	Thumb,
	/// <summary>Even out the spacing of the surface along itself, leaving its shape (Blender's Relax).</summary>
	Relax,
	/// <summary>Slide the surface along itself in the stroke's direction, carrying its detail with it (Blender's Slide, Nudge).</summary>
	Slide,
	/// <summary>Twist the surface about its normal under the brush; Strength is the angle in radians at the centre (Blender's Twist).</summary>
	Twist,
	/// <summary>Grab an open edge and bend it along the drag, fading in over the surface from the edge (Blender's Boundary).</summary>
	Boundary,
	/// <summary>Rotate a whole region about the brush point toward the drag, like posing a limb (Blender's Pose).</summary>
	Pose,
	// Cloth brushes: the surface under the brush (out to 2.5 radii, softly pinned at the rim) is
	// simulated as fabric — every edge keeps its length — while the brush pushes it, so it buckles
	// into folds rather than moving like clay (Blender's cloth brushes).
	/// <summary>Cloth: drag the fabric along the drag.</summary>
	ClothGrab,
	/// <summary>Cloth: push the fabric into the surface.</summary>
	ClothPush,
	/// <summary>Cloth: inflate the fabric along its normals.</summary>
	ClothInflate,
	/// <summary>Cloth: drag the fabric along the stroke.</summary>
	ClothDrag,
	/// <summary>Cloth: pinch toward the stroke line, buckling folds along it.</summary>
	ClothPinchFolds,
	/// <summary>Cloth: grow (or, inverted, shrink) the fabric so it buckles into folds.</summary>
	ClothExpand,
	/// <summary>Cloth: bend the fabric toward a drag, or twist it about the brush's normal.</summary>
	ClothTwist,
	/// <summary>Draw with a flat top: raises evenly over the middle half of the brush (Blender's Plateau).</summary>
	Plateau,
	/// <summary>Press toward a plane just under the surface, like a thumb smoothing clay down (Blender's Clay Thumb).</summary>
	ClayThumb,
	/// <summary>Scrape the high points against two planes angled either side of the stroke, cutting a V (Blender's Multiplane Scrape).</summary>
	MultiplaneScrape
}

public enum BrushFalloff
{
	Smooth,
	Linear,
	Sharp,
	Constant
}

/// <summary>
/// Which plane a brush mirrors its samples across, or none.
///
/// ONE ENUM, SHARED BY SCULPT, PAINT AND WEIGHT PAINT. Mirroring was a `bool MirrorX` in three
/// separate sessions, which made "a model facing +X gets symmetry everywhere" true and "a model
/// facing +Y gets it nowhere" true at once, with no setting that could help. The plane is always
/// the origin plane — nothing here mirrors about an arbitrary plane, and this must not grow that.
/// </summary>
public enum MirrorAxis
{
	None,
	X,
	Y,
	Z
}

/// <summary>One sample on a stroke. The editor produces these; the kernel never learns what a mouse is.</summary>
public readonly struct BrushSample
{
	public readonly Vec3 Position;
	public readonly Vec3 Normal;
	public readonly Vec3 Direction;
	public readonly float Radius;
	public readonly float Strength;

	public BrushSample( Vec3 position, Vec3 normal, float radius, float strength, Vec3 direction = default )
	{
		Position = position;
		Normal = normal;
		Direction = direction;
		Radius = radius;
		Strength = strength;
	}
}

/// <summary>
/// A greyscale image laid over a brush's footprint that scales its strength (Blender's brush
/// texture in Area Plane mapping): how sculptors stamp pores, scales and wrinkle patterns. Row 0 is
/// the image's top. The image's right runs along <see cref="Right"/> projected onto the surface
/// under the brush; it spans the brush's diameter, times <see cref="Scale"/>.
/// </summary>
public sealed class BrushAlpha
{
	public float[] Values;
	public int Width, Height;
	public Vec3 Right = new( 1, 0, 0 );
	public float Scale = 1f;

	/// <summary>A brush alpha from PNG bytes: its brightness (times its own alpha, if it has one).</summary>
	public static BrushAlpha FromPng( byte[] png )
	{
		var (rgba, w, h) = PngReader.Read( png );
		var values = new float[w * h];
		for ( var i = 0; i < values.Length; i++ )
			values[i] = (0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2]) / 255f * (rgba[i * 4 + 3] / 255f);
		return new BrushAlpha { Values = values, Width = w, Height = h };
	}

	/// <summary>The image at (u, v), 0..1 across from its left and down from its top, bilinear; 0 outside it.</summary>
	public float Sample( float u, float v )
	{
		if ( Values is null || u < 0f || v < 0f || u > 1f || v > 1f ) return 0f;
		var x = u * (Width - 1); var y = v * (Height - 1);
		int x0 = (int)x, y0 = (int)y, x1 = Math.Min( x0 + 1, Width - 1 ), y1 = Math.Min( y0 + 1, Height - 1 );
		float fx = x - x0, fy = y - y0;
		float At( int xi, int yi ) => Values[yi * Width + xi];
		return (At( x0, y0 ) * (1 - fx) + At( x1, y0 ) * fx) * (1 - fy) + (At( x0, y1 ) * (1 - fx) + At( x1, y1 ) * fx) * fy;
	}
}

/// <summary>A list of samples plus the brush that consumes them.</summary>
public sealed class BrushStroke
{
	public BrushKind Kind;
	public BrushFalloff Falloff = BrushFalloff.Smooth;
	public MirrorAxis Mirror;

	/// <summary>More mirror planes, any combination; added to <see cref="Mirror"/>. See <see cref="BrushSymmetry"/>.</summary>
	public SymmetryAxes MirrorAxes;

	/// <summary>Radial symmetry: the stroke repeats this many times evenly round <see cref="RadialAxis"/>
	/// through the origin. 1 is off.</summary>
	public int Radial = 1;

	public MirrorAxis RadialAxis = MirrorAxis.Z;

	/// <summary>Every copy this stroke is applied as, identity first.</summary>
	public List<SymmetryCopy> SymmetryCopies() =>
		BrushSymmetry.Copies( MirrorAxes | BrushSymmetry.AxesOf( Mirror ), Radial, RadialAxis );

	/// <summary>An image scaling the brush's strength across its footprint, or null.</summary>
	public BrushAlpha Alpha;
	/// <summary>Read each sample's Strength on the shared calibrated scale (<see cref="BrushCalibration"/>):
	/// the same number does about the same amount in every brush. Off, Strength is the brush's raw kernel unit.</summary>
	public bool Calibrated;
	public readonly List<BrushSample> Samples = new();
}

/// <summary>
/// Per-stroke undo: the original position of every vertex the stroke actually moved.
/// A naive undo snapshots the whole mesh; this stores only the working set.
/// </summary>
public sealed class BrushUndo
{
	readonly Dictionary<int, Vec3> _previous = new();

	public int Count => _previous.Count;

	/// <summary>Every vertex this stroke moved, with the position it had BEFORE.</summary>
	public IReadOnlyDictionary<int, Vec3> Previous => _previous;

	internal void Remember( int vertex, Vec3 position ) => _previous.TryAdd( vertex, position );

	/// <summary>
	/// Fold a later undo into this one, so several brush applications read as a single stroke.
	///
	/// EARLIEST WINS, which is what TryAdd gives: a vertex moved by three dabs in one stroke has to
	/// go back to where it was before the FIRST of them, not before the last. Absorbing in the other
	/// order would leave the model two thirds sculpted after an undo, which looks like a brush bug
	/// rather than an undo one.
	/// </summary>
	public void Absorb( BrushUndo later )
	{
		if ( later is null )
			throw new ArgumentNullException( nameof( later ) );

		foreach ( var (vertex, position) in later._previous )
			_previous.TryAdd( vertex, position );
	}

	public void Restore( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		foreach ( var (vertex, position) in _previous )
			mesh.Positions[vertex] = position;
	}
}

/// <summary>
/// Brushes as pure functions over a mesh and a stroke. Spatial queries go through
/// <see cref="MeshBVH"/>; the kernel does not know about a cursor.
/// </summary>
public static class Brush
{
	/// <param name="neighbors">
	/// Vertex adjacency, if the caller already has it. Optional, and worth passing whenever this
	/// is called more than once on the same mesh: building it walks every face, so an interactive
	/// sculpt that calls Apply per mouse sample rebuilt the whole mesh's adjacency for every dab -
	/// on a dense body that is most of the frame and tens of megabytes of garbage, thrown away and
	/// recomputed identically a few milliseconds later. Topology does not change under a stroke,
	/// which is exactly what makes it cacheable; see SculptSession, which holds it beside the BVH
	/// and drops both together.
	/// </param>
	/// <param name="strokeSoFar">The undo of the stroke this call continues, when a caller applies a stroke a dab at a time (an
	/// editor does): Layer measures its height from where vertices were when the STROKE began, not this dab.</param>
	public static BrushUndo Apply( PolyMesh mesh, BrushStroke stroke, SculptFrames frames = null, float[] mask = null, MeshBVH bvh = null, List<EdgeKey>[] neighbors = null, BrushUndo strokeSoFar = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( stroke is null )
			throw new ArgumentNullException( nameof( stroke ) );

		if ( frames is not null && frames.Count != mesh.VertexCount )
			throw new ArgumentException( $"frames ({frames.Count}) and mesh ({mesh.VertexCount}) disagree" );

		if ( mask is not null && mask.Length != mesh.VertexCount )
			throw new ArgumentException( $"mask ({mask.Length}) and mesh ({mesh.VertexCount}) disagree" );

		bvh ??= MeshBVH.Build( mesh );

		// Frames and adjacency are each O(vertices) to build and each is read by exactly one brush —
		// frames by Inflate, adjacency by Smooth. Every other brush leaves them null, so a caller
		// that does not need them (a Flatten stroke on a dense import) skips two full walks of the
		// mesh per stroke. Built here when absent rather than silently dropped, so a bare call still
		// works.
		if ( frames is null && UsesSurfaceFrames( stroke.Kind ) )
			frames = SculptFrames.Build( mesh );

		if ( neighbors is null && UsesNeighbours( stroke.Kind ) )
			neighbors = mesh.BuildVertexEdges();

		var found = new List<int>();
		var undo = new BrushUndo();
		IReadOnlyList<BrushSample> samples = stroke.Calibrated
			? stroke.Samples.ConvertAll( s => BrushCalibration.ToKernel( stroke.Kind, s ) )
			: stroke.Samples;

		if ( IsCloth( stroke.Kind ) )
		{
			neighbors ??= mesh.BuildVertexEdges();
			frames ??= SculptFrames.Build( mesh );
			Vec3? last = null;
			foreach ( var sample in samples )
			{
				var dir = last is { } lp && (sample.Position - lp).LengthSquared > 1e-12f ? (sample.Position - lp).Normal : sample.Direction.LengthSquared > 1e-12f ? sample.Direction.Normal : Vec3.Zero;
				last = sample.Position;
				ClothDab( mesh, stroke, frames, mask, bvh, neighbors, found, undo, sample, dir );
			}
			return undo;
		}

		// The stroke and its symmetric copies: mirror planes, any combination, times radial repeats.
		var copies = stroke.SymmetryCopies();

		Vec3? previous = null;
		foreach ( var sample in samples )
		{
			// The stroke's direction here, for brushes laid along it (strips, creases): from the last
			// sample, or the sample's own Direction when it is the first.
			var dir = previous is { } prev && (sample.Position - prev).LengthSquared > 1e-12f ? (sample.Position - prev).Normal : sample.Direction.LengthSquared > 1e-12f ? sample.Direction.Normal : Vec3.Zero;
			previous = sample.Position;
			ApplySample( mesh, stroke, frames, mask, bvh, neighbors, found, undo, sample, dir, strokeSoFar );

			for ( var c = 1; c < copies.Count; c++ )
				ApplySample( mesh, stroke, frames, mask, bvh, neighbors, found, undo, Copy( sample, copies[c], stroke.Kind ), copies[c].Apply( dir ), strokeSoFar );
		}

		return undo;
	}

	/// <summary>Brushes that read surface frames (normals per vertex); a caller brushing a stroke a dab at a time should build them once per stroke and pass them in.</summary>
	public static bool UsesSurfaceFrames( BrushKind kind ) => UsesArea( kind ) || IsCloth( kind );

	/// <summary>Brushes that read vertex adjacency (Smooth, Relax, Boundary, the cloth brushes).</summary>
	public static bool UsesNeighbours( BrushKind kind ) => kind is BrushKind.Smooth or BrushKind.Relax or BrushKind.Boundary || IsCloth( kind );

	public static bool IsCloth( BrushKind kind ) => kind is BrushKind.ClothGrab or BrushKind.ClothPush or BrushKind.ClothInflate or BrushKind.ClothDrag
		or BrushKind.ClothPinchFolds or BrushKind.ClothExpand or BrushKind.ClothTwist;

	/// <summary>
	/// One dab of a cloth brush: the region out to 2.5 radii is fabric. Each vertex is pushed by the
	/// brush (weighted by falloff), then edge lengths are restored a few times over — position-based
	/// dynamics — with the rim held: weight to move ramps from 1 inside the brush to 0 at the rim.
	/// Expand changes the rest lengths instead of pushing, so the fabric buckles by itself.
	/// </summary>
	static void ClothDab( PolyMesh mesh, BrushStroke stroke, SculptFrames frames, float[] mask, MeshBVH bvh,
		List<EdgeKey>[] neighbors, List<int> found, BrushUndo undo, BrushSample sample, Vec3 strokeDir )
	{
		if ( sample.Radius <= 0f ) return;
		const float limit = 2.5f;
		bvh.VerticesInRadius( mesh, sample.Position, sample.Radius * limit, found );
		if ( found.Count == 0 ) return;
		var n = sample.Normal.LengthSquared >= 0.5f ? sample.Normal.Normal : new Vec3( 0, 0, 1 );
		var index = new Dictionary<int, int>( found.Count );
		for ( var k = 0; k < found.Count; k++ ) index[found[k]] = k;
		var pos = found.Select( v => mesh.Positions[v] ).ToArray();
		var free = new float[found.Count];
		var push = new float[found.Count];
		for ( var k = 0; k < found.Count; k++ )
		{
			var t = (pos[k] - sample.Position).Length / sample.Radius;
			// Free to move inside the brush, held at the rim (Blender's soft pin, falloff 0.75 of the limit).
			free[k] = 1f - Smooth01( (t - 0.75f * limit) / (0.25f * limit) );
			push[k] = Falloff( t, stroke.Falloff ) * (mask is null ? 1f : mask[found[k]]);
		}
		// The fabric's edges among the simulated vertices, at their current length (or grown, for Expand).
		var edges = new List<(int A, int B, float Length)>();
		for ( var k = 0; k < found.Count; k++ )
			foreach ( var key in neighbors[found[k]] )
			{
				var other = key.A == found[k] ? key.B : key.A;
				if ( other < found[k] || !index.TryGetValue( other, out var j ) ) continue;
				var rest = (pos[k] - pos[j]).Length;
				if ( stroke.Kind == BrushKind.ClothExpand )
					rest *= 1f + sample.Strength * 0.5f * (push[k] + push[j]);
				edges.Add( (k, j, rest) );
			}
		var along = strokeDir - n * Vec3.Dot( strokeDir, n );
		along = along.LengthSquared > 1e-10f ? along.Normal : Perpendicular( n );
		var across = Vec3.Cross( n, along ).Normal;
		var turn = stroke.Kind == BrushKind.ClothTwist ? Xform.Rotate( n, sample.Strength ) : Xform.Identity;
		// A sheet grown evenly can stay in its plane; real skin and fabric never start flat, and the
		// folds come from their irregularities at fold scale. Seed the buckling with smooth noise
		// about half a radius across, in proportion to the growth; bending keeps the folds soft.
		if ( stroke.Kind == BrushKind.ClothExpand )
		{
			var seed = new Noise3( 17 );
			var f = 1f / (0.5f * sample.Radius);
			for ( var k = 0; k < found.Count; k++ )
				pos[k] += n * (seed.Sample( pos[k] * f ) * 0.35f * sample.Radius * MathF.Abs( sample.Strength ) * push[k]);
		}
		var start = (Vec3[])pos.Clone();
		for ( var k = 0; k < found.Count; k++ )
		{
			var w = push[k] * sample.Strength;
			if ( w == 0f && stroke.Kind != BrushKind.ClothTwist ) continue;
			var p = pos[k];
			pos[k] = stroke.Kind switch
			{
				BrushKind.ClothGrab => p + sample.Direction * push[k] * MathF.Abs( sample.Strength ),
				// Pushes the fabric into the surface, along the brush normal.
				BrushKind.ClothPush => p - n * w,
				BrushKind.ClothInflate => p + frames.At[found[k]].Normal * w,
				BrushKind.ClothDrag => p + along * w,
				BrushKind.ClothPinchFolds => p - across * (MathF.Sign( Vec3.Dot( p - sample.Position, across ) ) * w),
				// With a drag, the whole region bends along it as one piece; without, it twists about the normal.
				// With a drag the whole region bends along it as one piece; without, it twists about the normal.
				BrushKind.ClothTwist => sample.Direction.LengthSquared > 1e-12f
					? p + sample.Direction * (free[k] * MathF.Abs( sample.Strength ))
					: sample.Position + Xform.Rotate( n, sample.Strength * push[k] ).TransformDirection( p - sample.Position ),
				_ => p,
			};
		}
		// Neighbour lists among the simulated vertices, for bending.
		var adj = new List<int>[found.Count];
		for ( var k = 0; k < found.Count; k++ ) adj[k] = new List<int>();
		foreach ( var (a, b, _) in edges ) { adj[a].Add( b ); adj[b].Add( a ); }

		// Restore edge lengths, held vertices barely moving. Between passes, bending stiffness: the
		// displacement (not the shape) is smoothed over neighbours, so the fabric resists folding
		// sharply and buckles into folds a few edges wide, not crumples one edge wide.
		for ( var it = 0; it < ClothIterations; it++ )
		{
			foreach ( var (a, b, rest) in edges )
			{
				var d = pos[b] - pos[a];
				var len = d.Length;
				if ( len < 1e-9f ) continue;
				var wa = free[a]; var wb = free[b];
				if ( wa + wb < 1e-6f ) continue;
				var corr = d * ((len - rest) / len / (wa + wb) * 0.8f);
				pos[a] += corr * wa;
				pos[b] -= corr * wb;
			}
			var moved = new Vec3[found.Count];
			for ( var k = 0; k < found.Count; k++ ) moved[k] = pos[k] - start[k];
			for ( var k = 0; k < found.Count; k++ )
			{
				if ( adj[k].Count == 0 || free[k] <= 0f ) continue;
				var avg = Vec3.Zero;
				foreach ( var j in adj[k] ) avg += moved[j];
				avg /= adj[k].Count;
				pos[k] += (avg - moved[k]) * (ClothBend * free[k]);
			}
		}
		for ( var k = 0; k < found.Count; k++ )
		{
			// The rim stays where it was.
			var p = Vec3.Lerp( start[k], pos[k], free[k] );
			if ( p.AlmostEquals( mesh.Positions[found[k]], 1e-8f ) ) continue;
			undo.Remember( found[k], mesh.Positions[found[k]] );
			mesh.Positions[found[k]] = p;
		}
		bvh.RefitRegion( mesh, sample.Position, sample.Radius * limit );
	}

	/// <summary>How strongly cloth resists bending: the share of each pass's displacement pulled toward its neighbours' (0 crumples like foil, 0.5 folds like heavy canvas).</summary>
	public static float ClothBend = 0.35f;

	/// <summary>Constraint passes per cloth dab: more lets the fabric reach the lengths it was given (and buckle fully).</summary>
	public static int ClothIterations = 40;

	static float Smooth01( float x ) { x = Math.Clamp( x, 0f, 1f ); return x * x * (3f - 2f * x); }

	/// <summary>Brushes that work from the local surface (its average normal and plane under the brush) rather than the sample's normal alone.</summary>
	static bool UsesArea( BrushKind kind ) => kind is BrushKind.ClayThumb or BrushKind.MultiplaneScrape or BrushKind.Inflate or BrushKind.Flatten or BrushKind.Thumb or BrushKind.Relax or BrushKind.Slide or BrushKind.Twist or BrushKind.Pose or BrushKind.Clay or BrushKind.ClayStrips or BrushKind.Layer
		or BrushKind.Crease or BrushKind.Blob or BrushKind.Fill or BrushKind.Scrape;

	static void ApplySample(
		PolyMesh mesh, BrushStroke stroke, SculptFrames frames, float[] mask,
		MeshBVH bvh, List<EdgeKey>[] neighbors, List<int> found, BrushUndo undo, BrushSample sample, Vec3 strokeDir = default, BrushUndo strokeSoFar = null )
	{
		if ( sample.Radius <= 0f )
			return;

		// A square footprint reaches its corners at √2 radii; the elastic falloff reaches six radii.
		var reach = stroke.Kind switch { BrushKind.ClayStrips => 1.415f, BrushKind.ElasticGrab => 6f, BrushKind.Pose => 1.7f, BrushKind.Boundary => 3f, _ => 1f };
		bvh.VerticesInRadius( mesh, sample.Position, sample.Radius * reach, found );

		var n = sample.Normal.LengthSquared >= 0.5f ? sample.Normal.Normal : new Vec3( 0, 0, 1 );
		var planePoint = Vec3.Zero;
		var planeCount = 0;

		if ( stroke.Kind == BrushKind.Flatten )
		{
			foreach ( var vi in found )
			{
				planePoint += mesh.Positions[vi];
				planeCount++;
			}

			if ( planeCount > 0 )
				planePoint /= planeCount;
			else
				planePoint = sample.Position;
		}

		// The local surface under the brush: its falloff-weighted centroid and average normal.
		var areaC = sample.Position;
		var areaN = n;
		if ( UsesArea( stroke.Kind ) && frames is not null && found.Count > 0 )
		{
			var cs = Vec3.Zero; var ns = Vec3.Zero; var ws = 0f;
			foreach ( var vi in found )
			{
				var w = Falloff( MathF.Min( (mesh.Positions[vi] - sample.Position).Length / sample.Radius, 1f ), BrushFalloff.Smooth ) + 1e-3f;
				cs += mesh.Positions[vi] * w; ns += frames.At[vi].Normal * w; ws += w;
			}
			areaC = cs / ws;
			if ( ns.LengthSquared > 1e-12f ) areaN = ns.Normal;
			if ( Vec3.Dot( areaN, n ) < 0f && sample.Normal.LengthSquared >= 0.5f ) areaN = -areaN;
		}
		// Boundary: surface distance from the open edge the brush is on, out to two radii.
		Dictionary<int, float> fromEdge = null;
		if ( stroke.Kind == BrushKind.Boundary && neighbors is not null )
			fromEdge = EdgeDistances( mesh, neighbors, found, sample, sample.Radius );

		// Pose: a turn about the brush point, toward the drag, by the angle the drag makes at one radius.
		var poseAxis = Vec3.Zero; var poseAngle = 0f;
		if ( stroke.Kind == BrushKind.Pose && sample.Direction.LengthSquared > 1e-12f )
		{
			var drag = sample.Direction - areaN * Vec3.Dot( sample.Direction, areaN );
			poseAxis = Vec3.Cross( areaN, drag );
			if ( poseAxis.LengthSquared > 1e-12f ) poseAxis = poseAxis.Normal;
			// The drag measured in radii is the angle (Blender's pose turns ~90° for a drag of 1.7 radii),
			// turning the side the drag leaves up toward the viewer.
			poseAngle = -MathF.Min( drag.Length / sample.Radius, MathF.PI ) * MathF.Sign( sample.Strength == 0f ? 1f : sample.Strength );
		}

		// Across and along the stroke, in the surface.
		var along = (strokeDir - areaN * Vec3.Dot( strokeDir, areaN ));
		along = along.LengthSquared > 1e-10f ? along.Normal : Perpendicular( areaN );
		var across = Vec3.Cross( areaN, along ).Normal;

		foreach ( var vi in found )
		{
			var pos = mesh.Positions[vi];
			var dist = (pos - sample.Position).Length;
			var t = dist / sample.Radius;
			if ( stroke.Kind == BrushKind.ClayStrips )
			{
				// Square tip, rounded a little at the corners.
				var u = MathF.Abs( Vec3.Dot( pos - sample.Position, along ) ) / sample.Radius;
				var v = MathF.Abs( Vec3.Dot( pos - sample.Position, across ) ) / sample.Radius;
				t = MathF.Pow( MathF.Pow( u, 6f ) + MathF.Pow( v, 6f ), 1f / 6f );
			}
			// Strips are flat-topped with a short edge: full height out to most of the footprint.
			if ( stroke.Kind == BrushKind.ClayStrips ) t = Math.Clamp( (t - 0.72f) / 0.28f, 0f, 1f );
			if ( stroke.Kind == BrushKind.Boundary )
			{
				if ( fromEdge is null || !fromEdge.TryGetValue( vi, out var e ) ) continue;
				t = e / sample.Radius;
			}
			// Pose moves the whole region about evenly, fading only in its outer band.
			if ( stroke.Kind == BrushKind.Pose ) t = Math.Clamp( (t - 0.8f) / 0.53f, 0f, 1f );
			if ( stroke.Kind == BrushKind.Plateau ) t = Math.Clamp( (t - 0.45f) / 0.55f, 0f, 1f );
			var shape = stroke.Kind switch
			{
				BrushKind.DrawSharp => MathF.Pow( Falloff( t, BrushFalloff.Sharp ), 2f ),
				BrushKind.ElasticGrab => Kelvinlet( dist, sample.Radius ),
				// Fades straight down to nothing one radius in from the edge.
				BrushKind.Boundary => 1f - t,
				_ => Falloff( t, stroke.Falloff ),
			};
			var weight = shape * sample.Strength * (mask is null ? 1f : mask[vi]);
			if ( stroke.Alpha is { } alpha )
			{
				// The image on the plane under the brush: its right along Right (projected), its down
				// along the other way; it spans the diameter at Scale 1.
				var right = alpha.Right - areaN * Vec3.Dot( alpha.Right, areaN );
				right = right.LengthSquared > 1e-10f ? right.Normal : Perpendicular( areaN );
				var up = Vec3.Cross( areaN, right ).Normal;
				var span = 2f * sample.Radius * alpha.Scale;
				var d = pos - sample.Position;
				weight *= alpha.Sample( 0.5f + Vec3.Dot( d, right ) / span, 0.5f - Vec3.Dot( d, up ) / span );
			}

			if ( MathF.Abs( weight ) < 1e-8f )
				continue;

			Vec3 next;

			switch ( stroke.Kind )
			{
				case BrushKind.Smooth:
					next = Vec3.Lerp( pos, NeighbourAverage( mesh, neighbors[vi], vi ), Math.Clamp( weight, 0f, 1f ) );
					break;

				case BrushKind.Draw:
					next = pos + n * weight;
					break;

				case BrushKind.Inflate:
					next = pos + frames.At[vi].Normal * weight;
					break;

				case BrushKind.Grab:
				case BrushKind.ElasticGrab:
				case BrushKind.SnakeHook:
					next = pos + sample.Direction * weight;
					break;

				case BrushKind.Thumb:
				{
					// Half the drag, along the surface only, and pressed a little flatter.
					var slide = sample.Direction - areaN * Vec3.Dot( sample.Direction, areaN );
					var press = Vec3.Dot( pos - areaC, areaN );
					next = pos + slide * (0.5f * weight) - areaN * (press * Math.Clamp( 0.1f * shape, 0f, 1f ));
					break;
				}

				case BrushKind.Slide:
					next = pos + along * weight;
					break;

				case BrushKind.Twist:
				{
					var turn = Xform.Rotate( areaN, weight );
					next = sample.Position + turn.TransformDirection( pos - sample.Position );
					break;
				}

				case BrushKind.Boundary:
					next = pos + sample.Direction * weight;
					break;

				case BrushKind.Pose:
				{
					if ( poseAngle == 0f ) continue;
					var turn = Xform.Rotate( poseAxis, poseAngle * shape * Math.Clamp( MathF.Abs( sample.Strength ), 0f, 1f ) );
					next = sample.Position + turn.TransformDirection( pos - sample.Position );
					break;
				}

				case BrushKind.Relax:
				{
					// Toward the neighbours' average, but only along the surface: spacing evens out, shape stays.
					var toward = NeighbourAverage( mesh, neighbors[vi], vi ) - pos;
					toward -= areaN * Vec3.Dot( toward, areaN );
					next = pos + toward * Math.Clamp( weight, 0f, 1f );
					break;
				}

				case BrushKind.Flatten:
					// Toward the local surface plane (its falloff-weighted centroid and normal).
					var d = Vec3.Dot( pos - areaC, areaN );
					next = pos - areaN * (d * Math.Clamp( weight, 0f, 1f ));
					break;

				case BrushKind.Pinch:
					var height = Vec3.Dot( pos - sample.Position, n );
					var closest = sample.Position + n * height;
					next = Vec3.Lerp( pos, closest, Math.Clamp( weight, 0f, 1f ) );
					break;

				case BrushKind.DrawSharp:
				case BrushKind.Plateau:
					next = pos + n * weight;
					break;

				case BrushKind.ClayThumb:
				{
					// Toward a plane a little under the local surface (above, inverted).
					var plane = areaC - areaN * (0.15f * sample.Radius * MathF.Sign( sample.Strength == 0f ? 1f : sample.Strength ));
					var gap = Vec3.Dot( plane - pos, areaN );
					next = pos + areaN * (gap * Math.Clamp( MathF.Abs( weight ), 0f, 1f ));
					break;
				}

				case BrushKind.MultiplaneScrape:
				{
					// Two planes through the local surface, tilted 30° either way about the stroke: a vertex
					// standing above both is cut down to the nearer, so the stroke leaves a V.
					var tilt = 30f * MathF.PI / 180f;
					var n1 = (areaN * MathF.Cos( tilt ) + across * MathF.Sin( tilt )).Normal;
					var n2 = (areaN * MathF.Cos( tilt ) - across * MathF.Sin( tilt )).Normal;
					var h1 = Vec3.Dot( pos - areaC, n1 ); var h2 = Vec3.Dot( pos - areaC, n2 );
					if ( h1 <= 0f || h2 <= 0f ) continue;
					var (nn, h) = h1 < h2 ? (n1, h1) : (n2, h2);
					next = pos - nn * (h * Math.Clamp( MathF.Abs( weight ), 0f, 1f ));
					break;
				}

				case BrushKind.Clay:
				case BrushKind.ClayStrips:
				{
					// Toward a plane a quarter radius above the local surface (below, inverted),
					// moving only what lies short of it, a share of the way per dab.
					var plane = areaC + areaN * (0.25f * sample.Radius * MathF.Sign( sample.Strength ));
					var gap = Vec3.Dot( plane - pos, areaN );
					if ( gap * MathF.Sign( sample.Strength ) <= 0f ) continue;
					next = pos + areaN * (gap * Math.Clamp( MathF.Abs( weight ), 0f, 1f ));
					break;
				}

				case BrushKind.Fill:
				case BrushKind.Scrape:
				{
					var gap = Vec3.Dot( areaC - pos, areaN );
					var lifting = (stroke.Kind == BrushKind.Fill) == (sample.Strength >= 0f);
					if ( lifting ? gap <= 0f : gap >= 0f ) continue;
					next = pos + areaN * (gap * Math.Clamp( MathF.Abs( weight ), 0f, 1f ));
					break;
				}

				case BrushKind.Layer:
				{
					// Up to Strength inches over where the vertex was when the stroke began, eased in.
					var origin = strokeSoFar is not null && strokeSoFar.Previous.TryGetValue( vi, out var o0 ) ? o0 : undo.Previous.TryGetValue( vi, out var o ) ? o : pos;
					var target = sample.Strength * shape;
					var now = Vec3.Dot( pos - origin, areaN );
					var room = target - now;
					if ( MathF.Abs( room ) < 1e-6f || MathF.Sign( room ) != MathF.Sign( target ) ) continue;
					next = pos + areaN * (room * 0.5f);
					break;
				}

				case BrushKind.Crease:
				{
					// In along the normal, and the sides pinched in toward the stroke line.
					var side = Vec3.Dot( pos - sample.Position, across );
					next = pos - areaN * weight - across * (side * Math.Clamp( 0.5f * shape, 0f, 1f ));
					break;
				}

				case BrushKind.Blob:
				{
					// Out, and out from the centre too: a swelling with rounded sides.
					var radial = pos - sample.Position;
					radial -= areaN * Vec3.Dot( radial, areaN );
					next = pos + (areaN + radial / sample.Radius * 0.6f) * weight;
					break;
				}

				default:
					continue;
			}

			if ( next.AlmostEquals( pos, 1e-8f ) )
				continue;

			undo.Remember( vi, pos );
			mesh.Positions[vi] = next;
		}

		// ONLY WHERE THE BRUSH WAS. A full refit rebuilds every box in the tree, which on a dense
		// sculpt is most of the frame spent on the 99% of the mesh the sample could not reach.
		// The vertices that moved are the ones VerticesInRadius returned, so the sphere that found
		// them is exactly the region that can need new bounds.
		bvh.RefitRegion( mesh, sample.Position, sample.Radius );
	}

	/// <summary>
	/// Surface distance from the open edge for the vertices a Boundary brush found: seeded from the
	/// found vertices that lie on the edge (edges used by one face), spread over the found set.
	/// </summary>
	static Dictionary<int, float> EdgeDistances( PolyMesh mesh, List<EdgeKey>[] neighbors, List<int> found, BrushSample sample, float limit )
	{
		var inFound = new HashSet<int>( found );
		var edgeUse = new Dictionary<(int, int), int>();
		foreach ( var face in mesh.Faces )
			for ( var k = 0; k < face.Indices.Length; k++ )
			{
				int a = face.Indices[k], b = face.Indices[(k + 1) % face.Indices.Length];
				if ( !inFound.Contains( a ) && !inFound.Contains( b ) ) continue;
				var key = a < b ? (a, b) : (b, a);
				edgeUse[key] = edgeUse.GetValueOrDefault( key ) + 1;
			}
		var dist = new Dictionary<int, float>();
		var queue = new PriorityQueue<int, float>();
		foreach ( var ((a, b), uses) in edgeUse )
		{
			if ( uses != 1 ) continue;
			foreach ( var v in new[] { a, b } )
				// The whole open edge within reach goes, not just the part under the brush (Blender's
				// boundary falloff is constant along the edge by default).
				if ( inFound.Contains( v ) && !dist.ContainsKey( v ) ) { dist[v] = 0f; queue.Enqueue( v, 0f ); }
		}
		while ( queue.TryDequeue( out var v, out var d ) )
		{
			if ( d > dist[v] ) continue;
			foreach ( var key in neighbors[v] )
			{
				var u = key.A == v ? key.B : key.A;
				if ( !inFound.Contains( u ) ) continue;
				var nd = d + (mesh.Positions[u] - mesh.Positions[v]).Length;
				if ( nd > limit || (dist.TryGetValue( u, out var old ) && old <= nd) ) continue;
				dist[u] = nd; queue.Enqueue( u, nd );
			}
		}
		return dist;
	}

	/// <summary>
	/// The elastic grab's falloff: a two-scale regularised Kelvinlet (de Goes and James 2017),
	/// the response of an elastic solid to a pull, with the far field cancelled by a wider second
	/// scale so it fades to nothing. Scales 1.07 and 2.67 radii: fitted to Blender's Elastic Grab
	/// to within 0.004 out to four radii. 1 at the grab point.
	/// </summary>
	public static float Kelvinlet( float distance, float radius )
	{
		var e1 = 1.07f * radius; var e2 = 2.5f * e1;
		static float K( float r, float e ) { var re = MathF.Sqrt( r * r + e * e ); return 1f / re + e * e / (2f * re * re * re); }
		return (K( distance, e1 ) - K( distance, e2 )) / (K( 0f, e1 ) - K( 0f, e2 ));
	}

	static Vec3 Perpendicular( Vec3 n )
	{
		var a = MathF.Abs( n.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		return Vec3.Cross( n, a ).Normal;
	}

	public static float Falloff( float t, BrushFalloff kind )
	{
		t = Math.Clamp( t, 0f, 1f );

		return kind switch
		{
			BrushFalloff.Constant => 1f,
			BrushFalloff.Linear => 1f - t,
			BrushFalloff.Sharp => (1f - t) * (1f - t),
			_ => 1f - t * t * (3f - 2f * t)
		};
	}

	static Vec3 NeighbourAverage( PolyMesh mesh, List<EdgeKey> edges, int vi )
	{
		if ( edges.Count == 0 )
			return mesh.Positions[vi];

		var sum = Vec3.Zero;

		foreach ( var key in edges )
			sum += mesh.Positions[key.A == vi ? key.B : key.A];

		return sum / edges.Count;
	}

	/// <summary>Flip a vector across the chosen origin plane. The one place the "which component"
	/// decision lives, shared by sculpt, paint and weight paint so they cannot drift apart.</summary>
	public static Vec3 Mirror( Vec3 v, MirrorAxis axis ) => axis switch
	{
		MirrorAxis.X => new Vec3( -v.x, v.y, v.z ),
		MirrorAxis.Y => new Vec3( v.x, -v.y, v.z ),
		MirrorAxis.Z => new Vec3( v.x, v.y, -v.z ),
		_ => v
	};

	/// <summary>A sample carried to one symmetric copy. Turning brushes turn the other way in a
	/// reflected copy, so a twist mirrors into its mirror image rather than the same spin.</summary>
	static BrushSample Copy( BrushSample s, SymmetryCopy copy, BrushKind kind ) =>
		new(
			copy.Apply( s.Position ),
			copy.Apply( s.Normal ),
			s.Radius,
			IsTurning( kind ) ? s.Strength * copy.Det : s.Strength,
			copy.Apply( s.Direction ) );

	/// <summary>Brushes whose Strength is an angle about the surface normal.</summary>
	static bool IsTurning( BrushKind kind ) => kind is BrushKind.Twist;
}
