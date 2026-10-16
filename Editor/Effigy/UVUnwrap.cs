using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>What an unwrap produced, so a caller can say so rather than guess.</summary>
public sealed class UnwrapReport
{
	public readonly int Charts;
	public readonly int Faces;
	public readonly int SkippedFaces;
	public readonly float Scale;

	/// <summary>
	/// How far the layout bends the surface, as an area-weighted mean and a worst case over the
	/// unwrapped triangles. 1 is a perfect isometry: every triangle keeps its shape AND gets the
	/// layout's average texel density. 1.5 means some direction across the triangle gets half as
	/// many texels again, or two thirds as many, as the average says it should. See
	/// <see cref="UVIslands.Distortion"/> for the measure itself. Zero when nothing was measured.
	/// </summary>
	public readonly float MeanStretch;
	public readonly float MaxStretch;

	/// <summary>Anything a caller adds after the unwrap - a texel density, a repack - said on the
	/// end of <see cref="ToString"/>. Null for none.</summary>
	public string Note;

	public UnwrapReport( int charts, int faces, int skipped, float scale, float meanStretch = 0f, float maxStretch = 0f )
	{
		Charts = charts;
		Faces = faces;
		SkippedFaces = skipped;
		Scale = scale;
		MeanStretch = meanStretch;
		MaxStretch = maxStretch;
	}

	public override string ToString() =>
		$"{Faces} faces in {Charts} chart(s) at {Scale:0.####} units per UV unit"
		+ (MeanStretch > 0f ? $", stretch {MeanStretch:0.00} mean / {MaxStretch:0.00} max" : "")
		+ (SkippedFaces > 0 ? $", {SkippedFaces} degenerate face(s) skipped" : "")
		+ (string.IsNullOrEmpty( Note ) ? "" : $"; {Note}");
}

/// <summary>
/// How an unwrap decides where the seams go.
///
/// <see cref="Angle"/> is the hard-surface rule: a new chart wherever the surface turns further than
/// the charting angle, so a box comes apart at its corners and a fillet stays whole.
///
/// <see cref="Organic"/> is for closed, blobby, sculpted things - a lung lobe, a heart chamber, a
/// body - where the angle rule is useless: such a surface turns gently EVERYWHERE, so the angle rule
/// either keeps it whole (and it cannot flatten) or shatters it into dozens of curvature-sized
/// patches, each with a seam a texture artist has to paint across. Organic instead starts from each
/// whole connected piece and only cuts where it has to - see <see cref="UVUnwrap"/>'s ORGANIC SEAMS.
/// </summary>
public enum UnwrapMode
{
	Angle,
	Organic,
}

/// <summary>
/// How a chart is laid flat once its faces are chosen. <see cref="Conformal"/> is the real one;
/// <see cref="Planar"/> is the old projection, kept because it is the honest baseline to measure
/// the conformal one against and because a single face that the solver cannot handle still needs
/// somewhere to go.
/// </summary>
public enum UnwrapFlatten
{
	Conformal,
	Planar,
}

/// <summary>
/// UVs that do not overlap, so a bake has somewhere to write.
///
/// WHY THIS HAD TO EXIST. <see cref="UVProjection"/> box- and planar-projects, and BOTH overlap by
/// construction on a closed solid: box projection deliberately maps +X and -X onto the same square,
/// because it is built for tiling a texture across a wall. That is right for a wall and useless for
/// a bake, which needs every face to own its own texels. Until this, `NormalBake.Measure` correctly
/// refused every model the tool could make, and the sculpt pipeline could not pay off on anything
/// but a hand-UV'd plane.
///
/// CHART, FLATTEN, PACK - the three steps every unwrapper has, in their simplest honest form:
///
/// 1. **Chart.** Flood-fill faces into groups whose normals stay within an angle of the group's own
///    running average. On hard-surface CAD that puts each flat side in its own chart and follows a
///    fillet or a cylinder wall around as one piece, which is exactly where the seams should be.
///
/// 2. **Flatten.** Least-squares conformal maps (LSCM, Lévy et al. 2002) - see FLATTENING below.
///    This used to project each chart onto the plane of its average normal, which is exact on a flat
///    chart and squashes everything that leans away from that plane: a face tilted 60 degrees got
///    half its texels in one direction, and the wall of a cylinder chart came out as a strip twice
///    as dense at its middle as at its edges. Fine for the boxes this tool started with, useless
///    for anything sculpted - and the study of six pro organic assets this was rebuilt against had
///    every one of them laid out with seams along the form and little distortion.
///
/// 3. **Pack.** One scale for every chart, so texel density is uniform and the bake resolves detail
///    evenly, then shelf-pack the boxes into the unit square with a margin between them. The margin
///    is not decoration: the bake bleeds its islands outward so seams do not glow under mipmapping,
///    and without a gutter that bleed runs into the neighbouring island.
///
/// PER-CORNER UVs MAKE THE SEAMS FREE. A vertex on a chart boundary belongs to faces in two charts
/// and simply gets a different UV in each, which is what a seam IS. Nothing has to be split.
/// </summary>
public static partial class UVUnwrap
{
	/// <summary>
	/// Unwrap in place.
	///
	/// <paramref name="angleDegrees"/> is how far a face's normal may sit from its chart's average
	/// before it starts a new chart. Sixty-six is a little over a right angle's worth of curvature
	/// either side of the average, which keeps a cylinder wall whole and still splits a box corner.
	///
	/// <paramref name="margin"/> is the gutter between islands as a fraction of the square.
	///
	/// <paramref name="seams"/> are edges a chart may never cross, whatever the angle across them
	/// says — the user's own cuts, Blender's Mark Seam. The angle rule still applies everywhere
	/// else, so marking a few seams steers the result rather than replacing it: mark the inside of
	/// a sleeve and the sleeve unrolls as one island instead of being split wherever it curves past
	/// the tolerance.
	///
	/// <paramref name="mode"/> picks the seam rule (see <see cref="UnwrapMode"/>); Organic ignores
	/// the charting angle. <paramref name="flatten"/> exists so the old planar projection can still
	/// be asked for - the tests measure the conformal flatten against it.
	/// </summary>
	public static UnwrapReport Unwrap( PolyMesh mesh, float angleDegrees = 66f, float margin = 0.01f,
		IReadOnlySet<EdgeKey> seams = null, UnwrapMode mode = UnwrapMode.Angle,
		UnwrapFlatten flatten = UnwrapFlatten.Conformal )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		return UnwrapAll( new[] { mesh }, angleDegrees, margin, seams, mode, flatten );
	}

	/// <summary>
	/// Unwrap several meshes into ONE atlas: every chart of every mesh packed together into the same
	/// 0..1 square, none overlapping, at one scale.
	///
	/// WHY ONE SCALE IS UNIFORM TEXEL DENSITY. Each chart comes out of flattening at its true surface
	/// area (see the conformal flatten's last step), so packing them all at a single scale gives every
	/// square inch of every body the same number of texels: a hull and a bolt head on the same map
	/// are as sharp as each other. Unwrapping each body on its own fills a whole square per body,
	/// which is what overlaps when they are exported as one model with one texture.
	///
	/// <paramref name="seams"/> are edge keys, which are vertex indices, so they only mean anything
	/// on one mesh; with several they apply to the first. Left null, each mesh is cut along the
	/// seams it carries itself (<see cref="PolyMesh.Seams"/>, marked in a Mesh edit).
	/// </summary>
	public static UnwrapReport UnwrapAll( IReadOnlyList<PolyMesh> meshes, float angleDegrees = 66f, float margin = 0.01f,
		IReadOnlySet<EdgeKey> seams = null, UnwrapMode mode = UnwrapMode.Angle,
		UnwrapFlatten flatten = UnwrapFlatten.Conformal )
	{
		if ( meshes is null )
			throw new ArgumentNullException( nameof( meshes ) );

		margin = Math.Clamp( margin, 0f, 0.2f );

		var flattened = new List<Chart>();
		var skipped = 0;
		var faceCount = 0;

		for ( var owner = 0; owner < meshes.Count; owner++ )
		{
			var mesh = meshes[owner] ?? throw new ArgumentException( "A mesh in the list is null.", nameof( meshes ) );
			var meshSeams = seams is null ? (IReadOnlySet<EdgeKey>)mesh.LiveSeams() : owner == 0 ? seams : null;
			var first = flattened.Count;
			faceCount += mesh.FaceCount;

			var charts = mode == UnwrapMode.Organic
				? Components( mesh, meshSeams, 90f )
				: BuildCharts( mesh, angleDegrees, meshSeams );

			var context = new FlattenContext( mesh, meshSeams, mode );

			foreach ( var faces in charts )
			{
				if ( flatten == UnwrapFlatten.Planar )
				{
					var chart = Flatten( mesh, faces );

					if ( chart is null )
						skipped += faces.Count;
					else
						flattened.Add( chart );

					continue;
				}

				skipped += FlattenConformal( context, faces, new HashSet<EdgeKey>(), 0, flattened );
			}

			for ( var i = first; i < flattened.Count; i++ )
				flattened[i].Owner = owner;
		}

		var scale = Pack( flattened, margin );
		var written = new List<int>[meshes.Count];

		for ( var i = 0; i < written.Length; i++ )
			written[i] = new List<int>();

		foreach ( var chart in flattened )
		{
			chart.WriteTo( meshes[chart.Owner] );
			written[chart.Owner].AddRange( chart.Faces );
		}

		// Face-weighted across meshes, so a bolt head does not count as much as the hull.
		var meanSum = 0f;
		var max = 0f;
		var measured = 0;

		for ( var i = 0; i < meshes.Count; i++ )
		{
			if ( written[i].Count == 0 )
				continue;

			var (mean, worst) = UVIslands.Distortion( meshes[i], written[i] );
			meanSum += mean * written[i].Count;
			measured += written[i].Count;
			max = MathF.Max( max, worst );
		}

		return new UnwrapReport( flattened.Count, faceCount - skipped, skipped, scale, measured > 0 ? meanSum / measured : 0f, max );
	}

	// --- 1. charting ------------------------------------------------------------------------

	/// <summary>
	/// Group faces that face roughly the same way and touch each other.
	///
	/// The comparison is against the chart's RUNNING AVERAGE rather than its seed. Comparing against
	/// the seed makes the result depend on which face happened to be first and caps a chart at the
	/// tolerance no matter how gently it curves; comparing against the average lets a cylinder wall
	/// go all the way round, which is one seam instead of sixteen.
	/// </summary>
	static List<List<int>> BuildCharts( PolyMesh mesh, float angleDegrees, IReadOnlySet<EdgeKey> seams )
	{
		var limit = MathF.Cos( Math.Clamp( angleDegrees, 1f, 179f ) * MathF.PI / 180f );
		var neighbours = FaceNeighbours( mesh, seams );
		var chartOf = new int[mesh.FaceCount];

		for ( var i = 0; i < chartOf.Length; i++ )
			chartOf[i] = -1;

		var charts = new List<List<int>>();
		var queue = new Queue<int>();

		// Seeded in face order so the same mesh always unwraps the same way. A chart layout that
		// shuffled between runs would move every texel in the map for no reason.
		for ( var seed = 0; seed < mesh.FaceCount; seed++ )
		{
			if ( chartOf[seed] >= 0 )
				continue;

			var index = charts.Count;
			var faces = new List<int> { seed };

			charts.Add( faces );
			chartOf[seed] = index;

			var sum = mesh.FaceNormal( mesh.Faces[seed] );
			var average = Normalised( sum, new Vec3( 0, 0, 1 ) );

			queue.Clear();
			queue.Enqueue( seed );

			while ( queue.Count > 0 )
			{
				foreach ( var next in neighbours[queue.Dequeue()] )
				{
					if ( chartOf[next] >= 0 )
						continue;

					var normal = mesh.FaceNormal( mesh.Faces[next] );

					if ( normal.LengthSquared < 1e-16f || Vec3.Dot( Normalised( normal, average ), average ) < limit )
						continue;

					chartOf[next] = index;
					faces.Add( next );

					sum += Normalised( normal, average );
					average = Normalised( sum, average );

					queue.Enqueue( next );
				}
			}
		}

		return charts;
	}

	/// <summary>
	/// Faces sharing an edge. A non-manifold edge joins everything on it, which is what keeps a
	/// chart from leaking through a seam it should have stopped at.
	///
	/// A marked seam is simply left out of the adjacency: the two faces across it stop being
	/// neighbours, so the flood fill cannot reach one from the other and they land in different
	/// charts. Because the UVs are per corner, that is the whole of what a seam has to do — the
	/// vertices on it keep one position and get a UV per chart.
	/// </summary>
	static List<int>[] FaceNeighbours( PolyMesh mesh, IReadOnlySet<EdgeKey> seams = null )
	{
		var result = new List<int>[mesh.FaceCount];

		for ( var i = 0; i < result.Length; i++ )
			result[i] = new List<int>();

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( seams is { Count: > 0 } && seams.Contains( key ) )
				continue;

			for ( var a = 0; a < faces.Count; a++ )
			{
				for ( var b = a + 1; b < faces.Count; b++ )
				{
					result[faces[a]].Add( faces[b] );
					result[faces[b]].Add( faces[a] );
				}
			}
		}

		return result;
	}

	/// <summary>
	/// Each connected piece of the mesh as one chart, cut only by marked seams. Where Organic starts:
	/// a blob is ONE thing until something proves it has to come apart, rather than dozens of
	/// curvature-sized patches that then have to be argued back together.
	///
	/// The one exception is a FOLD: two faces turned more than <paramref name="foldDegrees"/>
	/// against each other. A skin body has these where its blockout pinches - 64 of them on the
	/// Citizen one - and a crease that sharp is both where an artist would hide a seam and where
	/// LSCM turns triangles over, so it is cut from the start rather than found by failing.
	/// </summary>
	static List<List<int>> Components( PolyMesh mesh, IReadOnlySet<EdgeKey> seams, float foldDegrees )
	{
		var neighbours = FaceNeighbours( mesh, seams );
		var normals = new Vec3[mesh.FaceCount];
		var fold = MathF.Cos( foldDegrees * MathF.PI / 180f );

		for ( var f = 0; f < normals.Length; f++ )
			normals[f] = mesh.FaceNormal( mesh.Faces[f] );
		var seen = new bool[mesh.FaceCount];
		var result = new List<List<int>>();
		var queue = new Queue<int>();

		for ( var seed = 0; seed < mesh.FaceCount; seed++ )
		{
			if ( seen[seed] )
				continue;

			var faces = new List<int>();
			seen[seed] = true;
			queue.Enqueue( seed );

			while ( queue.Count > 0 )
			{
				var at = queue.Dequeue();
				faces.Add( at );

				foreach ( var next in neighbours[at] )
				{
					if ( seen[next] || Vec3.Dot( normals[at], normals[next] ) < fold )
						continue;

					seen[next] = true;
					queue.Enqueue( next );
				}
			}

			result.Add( faces );
		}

		return result;
	}

	// --- 2. flattening ----------------------------------------------------------------------

	sealed class Chart
	{
		public readonly List<int> Faces = new();
		public readonly List<Vec2[]> Corners = new();

		public Vec2 Min;
		public Vec2 Size;
		public Vec2 Offset;
		public float Scale = 1f;

		/// <summary>Which mesh of an atlas the chart's faces belong to (see UnwrapAll).</summary>
		public int Owner;

		/// <summary>Turned a quarter turn to pack better. Costs nothing - a chart has no up.</summary>
		public bool Turned;

		/// <summary>Width and height as the packer sees them, which swap when the chart is turned.</summary>
		public Vec2 Packed => Turned ? new Vec2( Size.y, Size.x ) : Size;

		/// <summary>Write the packed UVs onto the mesh. The chart holds them in its own plane's
		/// units until here, so the packer can move it about without touching the mesh.</summary>
		public void WriteTo( PolyMesh mesh )
		{
			for ( var i = 0; i < Faces.Count; i++ )
			{
				var uvs = Corners[i];
				var face = mesh.Faces[Faces[i]];
				var written = new Vec2[uvs.Length];

				for ( var c = 0; c < uvs.Length; c++ )
				{
					var x = (uvs[c].x - Min.x) * Scale;
					var y = (uvs[c].y - Min.y) * Scale;

					// A quarter turn, which is why the packer could swap the chart's width and height.
					// (x, y) -> (y, w - x) keeps the winding, and a chart whose winding flipped would
					// bake with its tangent frame mirrored - the same inverted-green failure a
					// mirrored island has, arrived at from the other direction.
					written[c] = Turned
						? new Vec2( Offset.x + y, Offset.y + Size.x * Scale - x )
						: new Vec2( Offset.x + x, Offset.y + y );
				}

				face.UVs = written;
			}
		}
	}

	/// <summary>
	/// The old flatten: project the chart onto the plane of its average normal. Exact on a flat
	/// chart, and squashes whatever leans away from that plane by the cosine of the lean. Kept as
	/// the baseline the conformal flatten is measured against, and as the last resort for a single
	/// face the solver could not lay out - one flat-ish face projected onto its own plane is about
	/// as undistorted as it gets anyway.
	/// </summary>
	static Chart Flatten( PolyMesh mesh, List<int> faces )
	{
		var normal = Vec3.Zero;

		foreach ( var index in faces )
			normal += mesh.FaceNormal( mesh.Faces[index] );

		if ( normal.LengthSquared < 1e-16f )
			return null;

		normal = normal.Normal;

		// Any tangent will do, but it has to be a FUNCTION OF THE NORMAL and nothing else, or the
		// same chart flattens differently depending on which face seeded it.
		var seed = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var tangent = Vec3.Cross( seed, normal ).Normal;

		if ( tangent.LengthSquared < 0.5f )
			return null;

		var bitangent = Vec3.Cross( normal, tangent ).Normal;

		var chart = new Chart();
		var min = new Vec2( float.MaxValue, float.MaxValue );
		var max = new Vec2( float.MinValue, float.MinValue );

		foreach ( var index in faces )
		{
			var face = mesh.Faces[index];
			var uvs = new Vec2[face.Count];

			for ( var c = 0; c < face.Count; c++ )
			{
				var p = mesh.Positions[face.Indices[c]];
				uvs[c] = new Vec2( Vec3.Dot( p, tangent ), Vec3.Dot( p, bitangent ) );

				min = new Vec2( MathF.Min( min.x, uvs[c].x ), MathF.Min( min.y, uvs[c].y ) );
				max = new Vec2( MathF.Max( max.x, uvs[c].x ), MathF.Max( max.y, uvs[c].y ) );
			}

			chart.Faces.Add( index );
			chart.Corners.Add( uvs );
		}

		if ( chart.Faces.Count == 0 )
			return null;

		chart.Min = min;
		chart.Size = new Vec2( MathF.Max( max.x - min.x, 0f ), MathF.Max( max.y - min.y, 0f ) );

		return chart;
	}

	// --- 2b. conformal flattening -----------------------------------------------------------
	//
	// FLATTENING. Least-squares conformal maps: find the UV for every vertex of the chart that keeps
	// each triangle's ANGLES as close as possible to what they are on the surface, in the
	// least-squares sense. A map that keeps angles is the Cauchy-Riemann equations holding on every
	// triangle, and on a piecewise-linear triangle each of those is one complex linear equation in
	// its three corner UVs - so the whole chart is one sparse linear least-squares problem.
	//
	// WHY CONFORMAL RATHER THAN SOMETHING THAT ALSO KEEPS AREA. Nothing flattens a curved surface
	// keeping both; something has to give. Angle-preserving means a texel stays square everywhere,
	// so a painted pore or a baked normal is round wherever it lands and only its SIZE drifts across
	// the chart. Area-preserving maps shear instead, and a sheared texel smears a normal map in one
	// direction - far more visible. Size drift is the thing chart splitting below controls.
	//
	// WHY LSCM AND NOT ABF++. ABF solves for the angles first and is slightly better on very curved
	// charts, but it is a nonlinear Newton solve with its own failure modes. LSCM is one linear solve
	// with a unique answer once two vertices are pinned, and the organic seam rule below keeps
	// charts from ever being curved enough for the difference to matter.
	//
	// WHY CONJUGATE GRADIENT. The normal equations are symmetric positive definite once two vertices
	// are pinned, which is exactly what CG is for, and CG only needs the matrix as a function - so
	// there is no sparse matrix type, no factorisation, and no external library: the product is one
	// loop over the triangles. Jacobi preconditioning and a planar first guess keep the iteration
	// counts modest on the charts this produces.

	/// <summary>
	/// Charts whose mean stretch is above this get a relief cut or are split by the organic rule.
	/// Half a sphere lays out at about 1.24 and is kept whole; a chart with a protrusion in it - a
	/// hand on the end of an arm - measures in the hundreds before its relief cut and near 1.2 after.
	/// 1.25 was tried first and turned a three-lobed blob into four islands for 0.1 less stretch.
	/// </summary>
	const float OrganicStretchLimit = 1.3f;

	/// <summary>Charts smaller than this are never split for stretch alone. Splitting a handful of
	/// faces buys a seam and almost no stretch.</summary>
	const int OrganicMinimumSplit = 16;

	/// <summary>At most 2^depth charts per piece from stretch splitting. A ceiling, not a target.</summary>
	const int OrganicMaximumDepth = 12;

	/// <summary>How many tube-opening cuts one chart may take before it is split instead.</summary>
	const int MaximumLoopCuts = 8;

	/// <summary>How many relief cuts (see FlattenConformal) one chart may take before it is split.</summary>
	const int MaximumReliefCuts = 12;

	/// <summary>How many relief cuts a chart whose layout FOLDED may take. Lower than for stretch,
	/// because a fold the first couple of cuts did not fix is a boundary crossing a cut cannot fix,
	/// and every attempt is a full solve.</summary>
	const int MaximumFoldReliefCuts = 2;

	sealed class FlattenContext
	{
		public readonly PolyMesh Mesh;
		public readonly IReadOnlySet<EdgeKey> Seams;
		public readonly UnwrapMode Mode;
		public readonly Vec3[] Normals;
		public readonly Vec3[] Centroids;

		public FlattenContext( PolyMesh mesh, IReadOnlySet<EdgeKey> seams, UnwrapMode mode )
		{
			Mesh = mesh;
			Seams = seams;
			Mode = mode;
			Normals = new Vec3[mesh.FaceCount];
			Centroids = new Vec3[mesh.FaceCount];

			for ( var f = 0; f < mesh.FaceCount; f++ )
			{
				Normals[f] = mesh.FaceNormal( mesh.Faces[f] );
				Centroids[f] = mesh.FaceCentroid( mesh.Faces[f] );
			}
		}

		/// <summary>
		/// How concave the edge between two faces is: 0 for flat or convex, rising towards 1 as the
		/// two faces fold up towards each other. Measured as how far each face's centroid sits above
		/// the other's plane, so it needs no shared-edge orientation and works on n-gons.
		/// </summary>
		public float Bend( int f, int g )
		{
			var d = Centroids[g] - Centroids[f];

			if ( d.LengthSquared < 1e-20f )
				return 0f;

			d = d.Normal;
			return (Vec3.Dot( Normals[f], d ) - Vec3.Dot( Normals[g], d )) * 0.5f;
		}
	}

	/// <summary>
	/// A chart's faces as the solver sees them: corners welded into vertices wherever two faces share
	/// an edge that is not a seam or a cut. Welding CORNERS rather than reusing mesh vertex indices is
	/// what lets a seam that runs into the middle of a chart actually open - a mesh vertex on a slit
	/// becomes two chart vertices, one per side, the way Blender's Mark Seam behaves.
	/// </summary>
	sealed class Patch
	{
		public List<int> Faces;
		public int[] CornerStart;
		public int[] Vertex;
		public readonly List<int> MeshVertex = new();
		public readonly List<(int A, int B, int C)> Triangles = new();
		public readonly List<(int A, int B)> Boundary = new();

		/// <summary>Edges shared by two faces of the patch: the faces (patch-local), the two chart
		/// vertices and the mesh edge.</summary>
		public readonly List<(int F, int G, int U, int V, EdgeKey Key)> Interior = new();

		public int Components;
		public int[] ComponentOfFace;

		/// <summary>Boundary loop per chart vertex, -1 off the boundary.</summary>
		public int[] LoopOf;
		public int Loops;

		public int VertexCount => MeshVertex.Count;

		public static Patch Build( PolyMesh mesh, List<int> faces, IReadOnlySet<EdgeKey> seams, HashSet<EdgeKey> cuts )
		{
			var patch = new Patch { Faces = faces, CornerStart = new int[faces.Count + 1] };

			for ( var i = 0; i < faces.Count; i++ )
				patch.CornerStart[i + 1] = patch.CornerStart[i] + mesh.Faces[faces[i]].Count;

			var corners = patch.CornerStart[faces.Count];
			var parent = new int[corners];

			for ( var i = 0; i < corners; i++ )
				parent[i] = i;

			var edges = new Dictionary<EdgeKey, List<(int Face, int Corner)>>();

			for ( var i = 0; i < faces.Count; i++ )
			{
				var idx = mesh.Faces[faces[i]].Indices;

				for ( var c = 0; c < idx.Length; c++ )
				{
					var key = new EdgeKey( idx[c], idx[(c + 1) % idx.Length] );

					if ( !edges.TryGetValue( key, out var list ) )
						edges[key] = list = new List<(int, int)>( 2 );

					list.Add( (i, c) );
				}
			}

			var welded = new List<(int F, int G, EdgeKey Key)>();

			foreach ( var (key, list) in edges )
			{
				// Exactly two faces, or it stays open. A non-manifold edge welded three ways would fold
				// one sheet onto another, which no flat layout can hold.
				if ( list.Count != 2 || list[0].Face == list[1].Face )
					continue;

				if ( (seams is { Count: > 0 } && seams.Contains( key )) || cuts.Contains( key ) )
					continue;

				var (fa, ca) = list[0];
				var (fb, cb) = list[1];

				Union( parent, patch.CornerOf( mesh, fa, key.A ), patch.CornerOf( mesh, fb, key.A ) );
				Union( parent, patch.CornerOf( mesh, fa, key.B ), patch.CornerOf( mesh, fb, key.B ) );
				welded.Add( (fa, fb, key) );
			}

			var local = new Dictionary<int, int>();
			patch.Vertex = new int[corners];

			for ( var i = 0; i < faces.Count; i++ )
			{
				var idx = mesh.Faces[faces[i]].Indices;

				for ( var c = 0; c < idx.Length; c++ )
				{
					var root = Find( parent, patch.CornerStart[i] + c );

					if ( !local.TryGetValue( root, out var v ) )
					{
						v = patch.MeshVertex.Count;
						local[root] = v;
						patch.MeshVertex.Add( idx[c] );
					}

					patch.Vertex[patch.CornerStart[i] + c] = v;
				}

				// A fan per face. The solver works on triangles; the faces keep their own corners.
				for ( var c = 1; c + 1 < idx.Length; c++ )
				{
					patch.Triangles.Add( (patch.Vertex[patch.CornerStart[i]], patch.Vertex[patch.CornerStart[i] + c],
						patch.Vertex[patch.CornerStart[i] + c + 1]) );
				}
			}

			foreach ( var (f, g, key) in welded )
			{
				var u = patch.Vertex[patch.CornerOf( mesh, f, key.A )];
				var v = patch.Vertex[patch.CornerOf( mesh, f, key.B )];
				patch.Interior.Add( (f, g, u, v, key) );
			}

			// Boundary: a chart edge only one face uses. Directed by the face's winding.
			var uses = new Dictionary<(int, int), int>();

			for ( var i = 0; i < faces.Count; i++ )
			{
				var n = patch.CornerStart[i + 1] - patch.CornerStart[i];

				for ( var c = 0; c < n; c++ )
				{
					var a = patch.Vertex[patch.CornerStart[i] + c];
					var b = patch.Vertex[patch.CornerStart[i] + (c + 1) % n];
					var k = (Math.Min( a, b ), Math.Max( a, b ));
					uses[k] = uses.TryGetValue( k, out var count ) ? count + 1 : 1;
				}
			}

			for ( var i = 0; i < faces.Count; i++ )
			{
				var n = patch.CornerStart[i + 1] - patch.CornerStart[i];

				for ( var c = 0; c < n; c++ )
				{
					var a = patch.Vertex[patch.CornerStart[i] + c];
					var b = patch.Vertex[patch.CornerStart[i] + (c + 1) % n];

					if ( a != b && uses[(Math.Min( a, b ), Math.Max( a, b ))] == 1 )
						patch.Boundary.Add( (a, b) );
				}
			}

			// Connected pieces (through welded edges) and boundary loops, both by union-find.
			var vparent = new int[patch.VertexCount];

			for ( var i = 0; i < vparent.Length; i++ )
				vparent[i] = i;

			foreach ( var (a, b, c) in patch.Triangles )
			{
				Union( vparent, a, b );
				Union( vparent, a, c );
			}

			// Two-cornered faces have no triangle; tie their corners anyway.
			for ( var i = 0; i < faces.Count; i++ )
			{
				for ( var c = patch.CornerStart[i] + 1; c < patch.CornerStart[i + 1]; c++ )
					Union( vparent, patch.Vertex[patch.CornerStart[i]], patch.Vertex[c] );
			}

			var componentIds = new Dictionary<int, int>();
			patch.ComponentOfFace = new int[faces.Count];

			for ( var i = 0; i < faces.Count; i++ )
			{
				var root = Find( vparent, patch.Vertex[patch.CornerStart[i]] );

				if ( !componentIds.TryGetValue( root, out var id ) )
					componentIds[root] = id = componentIds.Count;

				patch.ComponentOfFace[i] = id;
			}

			patch.Components = componentIds.Count;

			var lparent = new int[patch.VertexCount];

			for ( var i = 0; i < lparent.Length; i++ )
				lparent[i] = i;

			foreach ( var (a, b) in patch.Boundary )
				Union( lparent, a, b );

			patch.LoopOf = new int[patch.VertexCount];
			Array.Fill( patch.LoopOf, -1 );

			var loopIds = new Dictionary<int, int>();

			foreach ( var (a, b) in patch.Boundary )
			{
				foreach ( var v in new[] { a, b } )
				{
					var root = Find( lparent, v );

					if ( !loopIds.TryGetValue( root, out var id ) )
						loopIds[root] = id = loopIds.Count;

					patch.LoopOf[v] = id;
				}
			}

			patch.Loops = loopIds.Count;
			return patch;
		}

		/// <summary>The corner of patch face <paramref name="face"/> that sits on mesh vertex
		/// <paramref name="vertex"/>.</summary>
		int CornerOf( PolyMesh mesh, int face, int vertex )
		{
			var idx = mesh.Faces[Faces[face]].Indices;

			for ( var c = 0; c < idx.Length; c++ )
			{
				if ( idx[c] == vertex )
					return CornerStart[face] + c;
			}

			return CornerStart[face];
		}

		static int Find( int[] parent, int i )
		{
			while ( parent[i] != i )
			{
				parent[i] = parent[parent[i]];
				i = parent[i];
			}

			return i;
		}

		static void Union( int[] parent, int a, int b )
		{
			a = Find( parent, a );
			b = Find( parent, b );

			// Smaller root wins, so the result does not depend on the order edges were visited in.
			if ( a < b )
				parent[b] = a;
			else if ( b < a )
				parent[a] = b;
		}
	}

	/// <summary>
	/// Lay one chart flat, cutting or splitting it as needed, and add the result to
	/// <paramref name="output"/>. Returns how many faces had to be skipped.
	///
	/// THE ORDER OF REPAIRS, cheapest seam first:
	///
	/// 1. Pieces that only touch at a vertex are laid out separately - they were never one sheet.
	/// 2. A chart with several boundary loops (a tube, a ring, a sleeve) gets ONE cut joining two of
	///    them, routed through concave and downward-facing edges, so a tube unrolls along a seam on
	///    its underside instead of being squeezed into a ring.
	/// 3. A chart with no boundary at all - a closed blob - cannot flatten, so it is split in two.
	/// 4. A chart whose layout folds over itself is split in two.
	/// 5. Organic only: a chart that flattens but stretches more than <see cref="OrganicStretchLimit"/>
	///    gets a RELIEF CUT - a seam from its most shrunken point to its edge - for as long as each
	///    cut helps, and only then is it split in two. A layout that folded gets relief cuts too,
	///    because the fold is almost always inside the part the map shrank.
	///
	/// A split is a Voronoi cut between the chart's two most distant faces, grown so that crossing a
	/// concave edge is expensive - the two halves meet in the creases. See <see cref="Split"/>.
	/// </summary>
	static int FlattenConformal( FlattenContext ctx, List<int> faces, HashSet<EdgeKey> cuts, int depth, List<Chart> output,
		int loopCuts = 0, int reliefCuts = 0, float before = float.MaxValue, Dictionary<int, Vec2> previous = null )
	{
		var mesh = ctx.Mesh;
		var patch = Patch.Build( mesh, faces, ctx.Seams, cuts );

		if ( patch.Components > 1 )
		{
			var skipped = 0;

			for ( var c = 0; c < patch.Components; c++ )
			{
				var piece = new List<int>();

				for ( var i = 0; i < faces.Count; i++ )
				{
					if ( patch.ComponentOfFace[i] == c )
						piece.Add( faces[i] );
				}

				skipped += FlattenConformal( ctx, piece, cuts, depth, output, loopCuts );
			}

			return skipped;
		}

		if ( patch.Loops > 1 && loopCuts < MaximumLoopCuts )
		{
			var opened = new HashSet<EdgeKey>( cuts );

			if ( CutBetweenLoops( ctx, patch, opened ) )
				return FlattenConformal( ctx, faces, opened, depth, output, loopCuts + 1, reliefCuts, before );
		}

		Chart chart = null;
		double[] uv = null;

		if ( patch.Loops > 0 )
		{
			uv = Solve( mesh, patch, previous );

			if ( uv is not null && Embedded( patch, uv ) )
				chart = MakeChart( mesh, patch, uv );
		}

		if ( chart is not null )
		{
			var stretch = ChartStretch( mesh, chart );
			var tooStretched = ctx.Mode == UnwrapMode.Organic
				&& faces.Count >= OrganicMinimumSplit
				&& depth < OrganicMaximumDepth
				&& stretch > OrganicStretchLimit;

			if ( !tooStretched )
			{
				output.Add( chart );
				return 0;
			}

			// RELIEF CUT BEFORE A SPLIT. A conformal map shrinks anything that sticks out of a chart -
			// a finger, a horn, a lobe - by a factor that grows with how far it sticks out, and that is
			// where nearly all of a stretchy chart's stretch lives. One seam from the tip of the
			// protrusion back to the chart's edge lets it unfold instead, which is what an artist does
			// with the seam up the inside of an arm. It costs one seam and no island, so it is tried
			// first - and kept only while each cut actually helps.
			if ( reliefCuts < MaximumReliefCuts && stretch < before * 0.97f )
			{
				var opened = new HashSet<EdgeKey>( cuts );

				if ( CutToMostShrunk( ctx, patch, uv, opened ) )
					return FlattenConformal( ctx, faces, opened, depth, output, loopCuts, reliefCuts + 1, stretch, ByMeshVertex( patch, uv ) );
			}
		}
		else if ( uv is not null && ctx.Mode == UnwrapMode.Organic && reliefCuts < MaximumFoldReliefCuts && faces.Count >= OrganicMinimumSplit )
		{
			// A layout that folded is almost always folded INSIDE a protrusion the map has shrunk to
			// nearly nothing, where a tiny triangle turns over in the rounding. The relief cut that
			// would have fixed the stretch fixes that too, and keeps the chart whole.
			var opened = new HashSet<EdgeKey>( cuts );

			if ( CutToMostShrunk( ctx, patch, uv, opened ) )
				return FlattenConformal( ctx, faces, opened, depth, output, loopCuts, reliefCuts + 1, before, ByMeshVertex( patch, uv ) );
		}

		if ( chart is null && faces.Count == 1 )
		{
			var single = Flatten( mesh, faces );

			if ( single is null )
				return 1;

			output.Add( single );
			return 0;
		}

		var (first, second) = Split( ctx, patch );

		// The halves start with no cuts of their own. A cut made to open this chart may end inside
		// one half as a dangling slit, which buys nothing and is the one place LSCM's free boundary
		// can fold over itself; each half that is still a tube gets its own cut.
		return FlattenConformal( ctx, first, new HashSet<EdgeKey>(), depth + 1, output )
			+ FlattenConformal( ctx, second, new HashSet<EdgeKey>(), depth + 1, output );
	}

	/// <summary>
	/// Cut a chart in two along its creases.
	///
	/// The two seeds are the chart's most distant pair of faces (farthest from face 0, then farthest
	/// from that), so the cut lands across the chart's long axis and both halves are about the same
	/// size. Both then grow at once, Dijkstra over face adjacency, and every face goes to whichever
	/// seed reaches it first. That makes the cut a bisector - and it is the cost of a step that
	/// steers it: stepping across a CONCAVE edge costs up to thirty times its length, so a front that
	/// reaches a crease stalls there while the other side fills in up to it. The halves meet in the
	/// fold under a lobe, around the neck of a bulge, in the gap between two blended blobs - the
	/// places a seam is hidden by the form itself, which is where an artist would have put it.
	/// </summary>
	static (List<int> First, List<int> Second) Split( FlattenContext ctx, Patch patch )
	{
		var faces = patch.Faces;
		var n = faces.Count;
		var adjacency = new List<(int To, double Cost)>[n];

		for ( var i = 0; i < n; i++ )
			adjacency[i] = new List<(int, double)>();

		foreach ( var (f, g, _, _, _) in patch.Interior )
		{
			var length = (ctx.Centroids[faces[g]] - ctx.Centroids[faces[f]]).Length;
			var concave = MathF.Max( 0f, ctx.Bend( faces[f], faces[g] ) );
			var cost = length * (1.0 + 30.0 * concave) + 1e-9;

			adjacency[f].Add( (g, cost) );
			adjacency[g].Add( (f, cost) );
		}

		var a = Farthest( Grow( adjacency, new[] { 0 } ).Distance );
		var b = Farthest( Grow( adjacency, new[] { a } ).Distance );

		if ( b == a )
			b = a == 0 ? 1 : 0;

		var owner = Grow( adjacency, new[] { a, b } ).Owner;
		var first = new List<int>();
		var second = new List<int>();

		for ( var i = 0; i < n; i++ )
			(owner[i] == 1 ? second : first).Add( faces[i] );

		// Unreachable faces land in the first half, which can only happen when the adjacency is
		// broken; never leave a half empty, or the recursion would not shrink.
		if ( second.Count == 0 )
		{
			second.Add( first[^1] );
			first.RemoveAt( first.Count - 1 );
		}

		return (first, second);
	}

	/// <summary>Multi-source Dijkstra over a weighted graph: distance to, and index of, the nearest
	/// source. Ties go to the lower node index so the result is a function of the graph.</summary>
	static (double[] Distance, int[] Owner) Grow( List<(int To, double Cost)>[] adjacency, int[] sources )
	{
		var n = adjacency.Length;
		var distance = new double[n];
		var owner = new int[n];

		Array.Fill( distance, double.PositiveInfinity );
		Array.Fill( owner, -1 );

		var queue = new PriorityQueue<int, (double, int)>();

		for ( var s = 0; s < sources.Length; s++ )
		{
			distance[sources[s]] = 0;
			owner[sources[s]] = s;
			queue.Enqueue( sources[s], (0, sources[s]) );
		}

		while ( queue.TryDequeue( out var at, out var key ) )
		{
			if ( key.Item1 > distance[at] )
				continue;

			foreach ( var (to, cost) in adjacency[at] )
			{
				var d = distance[at] + cost;

				if ( d >= distance[to] )
					continue;

				distance[to] = d;
				owner[to] = owner[at];
				queue.Enqueue( to, (d, to) );
			}
		}

		return (distance, owner);
	}

	static int Farthest( double[] distance )
	{
		var best = 0;

		for ( var i = 1; i < distance.Length; i++ )
		{
			if ( !double.IsInfinity( distance[i] ) && distance[i] > distance[best] )
				best = i;
		}

		return best;
	}

	/// <summary>
	/// Open a tube: add to <paramref name="cuts"/> the cheapest edge path from one boundary loop to
	/// another, so the chart has one loop fewer.
	///
	/// A tube, a sleeve or a ring laid out uncut is still a valid layout - the conformal map of a
	/// tube is an annulus - but a useless one: an annulus squeezes the far end of the tube by a
	/// factor that grows EXPONENTIALLY with its length, so an arm would get a few texels at the
	/// wrist. Cut along its length, the same tube unrolls into a strip with almost none. The route
	/// is <see cref="CutPath"/>'s, so the seam lands along the tube's underside or in a crease.
	/// </summary>
	static bool CutBetweenLoops( FlattenContext ctx, Patch patch, HashSet<EdgeKey> cuts )
	{
		// From every vertex of the loop the lowest-numbered vertex is on, to any other loop.
		var source = -1;

		for ( var v = 0; v < patch.VertexCount && source < 0; v++ )
		{
			if ( patch.LoopOf[v] >= 0 )
				source = patch.LoopOf[v];
		}

		return CutPath( ctx, patch, cuts, v => patch.LoopOf[v] == source, v => patch.LoopOf[v] >= 0 && patch.LoopOf[v] != source );
	}

	/// <summary>A layout keyed by mesh vertex, to seed the next solve with. A vertex split by a
	/// seam keeps one side's UV, which is close enough for a first guess.</summary>
	static Dictionary<int, Vec2> ByMeshVertex( Patch patch, double[] uv )
	{
		var result = new Dictionary<int, Vec2>( patch.VertexCount );

		for ( var i = 0; i < patch.VertexCount; i++ )
			result.TryAdd( patch.MeshVertex[i], new Vec2( (float)uv[i * 2], (float)uv[i * 2 + 1] ) );

		return result;
	}

	/// <summary>
	/// The relief cut: a seam from the most shrunken part of the layout - the tip of whatever sticks
	/// out - back to the chart's boundary, along the same concave, downward-facing route a tube cut
	/// takes. See FlattenConformal.
	/// </summary>
	static bool CutToMostShrunk( FlattenContext ctx, Patch patch, double[] uv, HashSet<EdgeKey> cuts )
	{
		var mesh = ctx.Mesh;
		var ratios = new List<(double Ratio, int Vertex)>();
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		for ( var t = 0; t < patch.Triangles.Count; t++ )
		{
			var (a, b, c) = patch.Triangles[t];
			var pa = mesh.Positions[patch.MeshVertex[a]];
			var area3 = 0.5 * Vec3.Cross( mesh.Positions[patch.MeshVertex[b]] - pa, mesh.Positions[patch.MeshVertex[c]] - pa ).Length;

			min = new Vec3( MathF.Min( min.x, pa.x ), MathF.Min( min.y, pa.y ), MathF.Min( min.z, pa.z ) );
			max = new Vec3( MathF.Max( max.x, pa.x ), MathF.Max( max.y, pa.y ), MathF.Max( max.z, pa.z ) );

			if ( area3 <= 1e-20 )
				continue;

			ratios.Add( (Math.Abs( Area( uv, a, b, c ) ) / area3, a) );
		}

		if ( ratios.Count == 0 )
			return false;

		ratios.Sort();

		// SEVERAL TIPS A ROUND. A body chart has a head, two hands and two feet, each shrunk by the
		// map, and one solve per protrusion was most of the organic unwrap's time on a skin body.
		// So every tip that is shrunk far below the chart's typical scale AND sits well away from the
		// tips already taken gets its seam in the same round. The worst one always does.
		var typical = ratios[ratios.Count / 2].Ratio;
		var apart = (max - min).Length * 0.2f;
		var tips = new List<int>();

		foreach ( var (ratio, vertex) in ratios )
		{
			if ( tips.Count > 0 && (ratio > typical * 0.02 || tips.Count >= 4) )
				break;

			if ( patch.LoopOf[vertex] >= 0 )
				continue;

			var p = mesh.Positions[patch.MeshVertex[vertex]];

			if ( tips.Any( t => (mesh.Positions[patch.MeshVertex[t]] - p).Length < apart ) )
				continue;

			tips.Add( vertex );
		}

		var added = false;

		foreach ( var tip in tips )
			added |= CutPath( ctx, patch, cuts, v => v == tip, v => patch.LoopOf[v] >= 0 );

		return added;
	}

	/// <summary>
	/// Add to <paramref name="cuts"/> the cheapest edge path, through the chart's interior, from any
	/// vertex <paramref name="from"/> accepts to the nearest one <paramref name="to"/> accepts.
	///
	/// The cost is what an artist would pick: concave edges (the inside of a fold) are cheap, convex
	/// ones dear, and edges whose faces look up cost half again as much as ones that look down -
	/// s&amp;box is Z-up, so on a limb the seam runs along the underside, out of sight.
	/// </summary>
	static bool CutPath( FlattenContext ctx, Patch patch, HashSet<EdgeKey> cuts, Func<int, bool> from, Func<int, bool> to )
	{
		var mesh = ctx.Mesh;
		var n = patch.VertexCount;
		var adjacency = new List<(int To, double Cost, EdgeKey Key)>[n];

		for ( var i = 0; i < n; i++ )
			adjacency[i] = new List<(int, double, EdgeKey)>();

		foreach ( var (f, g, u, v, key) in patch.Interior )
		{
			var length = (mesh.Positions[key.A] - mesh.Positions[key.B]).Length;
			var bend = ctx.Bend( patch.Faces[f], patch.Faces[g] );
			var up = MathF.Max( 0f, (ctx.Normals[patch.Faces[f]].z + ctx.Normals[patch.Faces[g]].z) * 0.5f );
			var cost = length * (bend >= 0f ? 1.0 / (1.0 + 8.0 * bend) : 1.0 - 4.0 * bend) * (1.0 + 0.5 * up) + 1e-9;

			adjacency[u].Add( (v, cost, key) );
			adjacency[v].Add( (u, cost, key) );
		}

		var distance = new double[n];
		var via = new int[n];
		var edgeVia = new EdgeKey[n];
		var queue = new PriorityQueue<int, (double, int)>();

		Array.Fill( distance, double.PositiveInfinity );
		Array.Fill( via, -1 );

		for ( var v = 0; v < n; v++ )
		{
			if ( !from( v ) )
				continue;

			distance[v] = 0;
			queue.Enqueue( v, (0, v) );
		}

		while ( queue.TryDequeue( out var at, out var key ) )
		{
			if ( key.Item1 > distance[at] )
				continue;

			if ( to( at ) && !from( at ) )
			{
				var added = false;

				for ( var v = at; via[v] >= 0; v = via[v] )
					added |= cuts.Add( edgeVia[v] );

				return added;
			}

			foreach ( var (next, cost, edge) in adjacency[at] )
			{
				var d = distance[at] + cost;

				if ( d >= distance[next] )
					continue;

				distance[next] = d;
				via[next] = at;
				edgeVia[next] = edge;
				queue.Enqueue( next, (d, next) );
			}
		}

		return false;
	}

	/// <summary>
	/// The LSCM solve. Returns a UV per chart vertex, or null when the chart has nothing to solve.
	///
	/// Two boundary vertices are pinned, as far apart as the boundary allows, at their true 3D
	/// distance along the U axis. Two pins are the fewest that remove the similarity freedom (a
	/// conformal map can be moved, turned and scaled for free) and pinning more would force the
	/// layout rather than let it find its shape. Far apart, so the pins themselves bend nothing.
	/// </summary>
	/// With <paramref name="userPins"/> (chart vertex to UV, at least two), those vertices are held
	/// where they are given instead - the user's pins - and the first guess is <paramref name="previous"/>
	/// as it stands, so the layout keeps its place and size. (Groundwork for UV pins; nothing passes it yet.)
	static double[] Solve( PolyMesh mesh, Patch patch, Dictionary<int, Vec2> previous = null, IReadOnlyDictionary<int, Vec2> userPins = null )
	{
		var n = patch.VertexCount;
		var pinnedByUser = userPins is { Count: >= 2 };

		if ( (patch.Boundary.Count == 0 && !pinnedByUser) || n < 3 )
			return null;

		var p = new Vec3[n];

		for ( var i = 0; i < n; i++ )
			p[i] = mesh.Positions[patch.MeshVertex[i]];

		// Pins: the boundary vertex farthest from the first boundary vertex, then the one farthest
		// from that. Farthest-point twice is a cheap, deterministic stand-in for the diameter.
		var pin0 = 0;
		var pin1 = 0;
		var span = 0.0;

		if ( !pinnedByUser )
		{
			var first = patch.Boundary[0].A;
			pin0 = FarthestOnBoundary( patch, p, first );
			pin1 = FarthestOnBoundary( patch, p, pin0 );
			span = (p[pin1] - p[pin0]).Length;

			if ( pin0 == pin1 || span < 1e-12 )
				return null;
		}

		// Per triangle: its corners' complex coefficients, already weighted by 1/sqrt(area).
		var count = patch.Triangles.Count;
		var tv = new int[count * 3];
		var ca = new double[count * 3];
		var cb = new double[count * 3];
		var areas = new double[count];
		var largest = 0.0;

		for ( var t = 0; t < count; t++ )
		{
			var (a, b, c) = patch.Triangles[t];
			areas[t] = 0.5 * Vec3.Cross( p[b] - p[a], p[c] - p[a] ).Length;
			largest = Math.Max( largest, areas[t] );
		}

		var used = 0;
		var normal = Vec3.Zero;

		for ( var t = 0; t < count; t++ )
		{
			// A sliver with no area has no angles to keep. Leaving it out costs nothing; leaving it in
			// puts an infinite weight on it.
			if ( areas[t] <= largest * 1e-10 )
				continue;

			var (a, b, c) = patch.Triangles[t];
			var e1 = p[b] - p[a];
			var e2 = p[c] - p[a];
			var x = e1.Normal;
			var nz = Vec3.Cross( e1, e2 ).Normal;
			var y = Vec3.Cross( nz, x );

			normal += Vec3.Cross( e1, e2 );

			// The triangle in its own plane, counter-clockwise: q0 = 0, q1 on the x axis.
			double q1x = e1.Length, q2x = Vec3.Dot( e2, x ), q2y = Vec3.Dot( e2, y );
			var w = 1.0 / Math.Sqrt( areas[t] );

			// W_j = q_(j+2) - q_(j+1): the Cauchy-Riemann residual on the triangle is sum_j W_j U_j.
			tv[used * 3] = a;
			tv[used * 3 + 1] = b;
			tv[used * 3 + 2] = c;
			ca[used * 3] = (q2x - q1x) * w;
			cb[used * 3] = q2y * w;
			ca[used * 3 + 1] = -q2x * w;
			cb[used * 3 + 1] = -q2y * w;
			ca[used * 3 + 2] = q1x * w;
			cb[used * 3 + 2] = 0;
			used++;
		}

		if ( used == 0 )
			return null;

		// The product with the normal equations' matrix, as a loop over triangles.
		void Apply( double[] x, double[] result )
		{
			Array.Clear( result );

			for ( var t = 0; t < used; t++ )
			{
				double re = 0, im = 0;

				for ( var j = 0; j < 3; j++ )
				{
					var v = tv[t * 3 + j];
					var u0 = x[v * 2];
					var v0 = x[v * 2 + 1];
					re += ca[t * 3 + j] * u0 - cb[t * 3 + j] * v0;
					im += cb[t * 3 + j] * u0 + ca[t * 3 + j] * v0;
				}

				for ( var j = 0; j < 3; j++ )
				{
					var v = tv[t * 3 + j];
					result[v * 2] += ca[t * 3 + j] * re + cb[t * 3 + j] * im;
					result[v * 2 + 1] += -cb[t * 3 + j] * re + ca[t * 3 + j] * im;
				}
			}
		}

		var size = n * 2;
		var free = new bool[size];
		Array.Fill( free, true );
		var pinned = new double[size];

		if ( pinnedByUser )
		{
			foreach ( var (v, at) in userPins )
			{
				if ( v < 0 || v >= n )
					continue;

				free[v * 2] = free[v * 2 + 1] = false;
				pinned[v * 2] = at.x;
				pinned[v * 2 + 1] = at.y;
			}
		}
		else
		{
			free[pin0 * 2] = free[pin0 * 2 + 1] = free[pin1 * 2] = free[pin1 * 2 + 1] = false;
			pinned[pin1 * 2] = span;
		}

		var diagonal = new double[size];

		for ( var t = 0; t < used; t++ )
		{
			for ( var j = 0; j < 3; j++ )
			{
				var v = tv[t * 3 + j];
				var d = ca[t * 3 + j] * ca[t * 3 + j] + cb[t * 3 + j] * cb[t * 3 + j];
				diagonal[v * 2] += d;
				diagonal[v * 2 + 1] += d;
			}
		}

		// The first guess, turned and scaled so the pins land where they are pinned. After a relief
		// cut it is the layout from before the cut (by mesh vertex), which differs from the answer
		// only around the new seam - on a 7000-vertex body chart that halves CG's iterations. The
		// first time it is the old planar projection, which on a gently curved chart is already most
		// of the answer.
		var guess = new double[size];
		var source = new double[size];

		if ( previous is not null )
		{
			for ( var i = 0; i < n; i++ )
			{
				if ( previous.TryGetValue( patch.MeshVertex[i], out var at ) )
				{
					source[i * 2] = at.x;
					source[i * 2 + 1] = at.y;
				}
			}
		}
		else
		{
			normal = normal.LengthSquared > 1e-20f ? normal.Normal : new Vec3( 0, 0, 1 );
			var seed = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
			var tangent = Vec3.Cross( seed, normal ).Normal;
			var bitangent = Vec3.Cross( normal, tangent ).Normal;

			for ( var i = 0; i < n; i++ )
			{
				source[i * 2] = Vec3.Dot( p[i], tangent );
				source[i * 2 + 1] = Vec3.Dot( p[i], bitangent );
			}
		}

		double z0r = source[pin0 * 2], z0i = source[pin0 * 2 + 1];
		double z1r = source[pin1 * 2] - z0r, z1i = source[pin1 * 2 + 1] - z0i;
		var z1 = z1r * z1r + z1i * z1i;
		var reach = 0.0;

		for ( var i = 0; i < n; i++ )
			reach = Math.Max( reach, Math.Abs( source[i * 2] - z0r ) + Math.Abs( source[i * 2 + 1] - z0i ) );

		// The user's pins already say where the layout sits, so the present UVs are the guess as they are.
		if ( pinnedByUser )
			Array.Copy( source, guess, size );
		else if ( z1 > reach * reach * 1e-8 && z1 > 0 )
		{
			// Multiply by span / z1 as complex numbers: span * conj(z1) / |z1|^2.
			var mr = span * z1r / z1;
			var mi = -span * z1i / z1;

			for ( var i = 0; i < n; i++ )
			{
				var zr = source[i * 2] - z0r;
				var zi = source[i * 2 + 1] - z0i;
				guess[i * 2] = zr * mr - zi * mi;
				guess[i * 2 + 1] = zr * mi + zi * mr;
			}
		}

		// Preconditioned CG on the free unknowns. The pins are moved to the right-hand side.
		var rhs = new double[size];
		Apply( pinned, rhs );

		var x0 = new double[size];

		for ( var i = 0; i < size; i++ )
		{
			rhs[i] = free[i] ? -rhs[i] : 0;
			x0[i] = free[i] ? guess[i] : 0;

			if ( diagonal[i] <= 0 )
				diagonal[i] = 1;
		}

		var ax = new double[size];
		Apply( x0, ax );

		var r = new double[size];
		var z = new double[size];
		var dir = new double[size];

		for ( var i = 0; i < size; i++ )
		{
			r[i] = free[i] ? rhs[i] - ax[i] : 0;
			z[i] = r[i] / diagonal[i];
			dir[i] = z[i];
		}

		var rz = Dot( r, z );
		var stop = Math.Max( Dot( rhs, rhs ), 1e-30 ) * 1e-18;
		var limit = Math.Min( 40000, 10 * n + 500 );

		for ( var iteration = 0; iteration < limit && Dot( r, r ) > stop; iteration++ )
		{
			Apply( dir, ax );

			for ( var i = 0; i < size; i++ )
			{
				if ( !free[i] )
					ax[i] = 0;
			}

			var denominator = Dot( dir, ax );

			if ( denominator <= 0 )
				break;

			var alpha = rz / denominator;

			for ( var i = 0; i < size; i++ )
			{
				x0[i] += alpha * dir[i];
				r[i] -= alpha * ax[i];
				z[i] = r[i] / diagonal[i];
			}

			var next = Dot( r, z );
			var beta = next / rz;
			rz = next;

			for ( var i = 0; i < size; i++ )
				dir[i] = z[i] + beta * dir[i];
		}

		for ( var i = 0; i < size; i++ )
			x0[i] += pinned[i];

		for ( var i = 0; i < size; i++ )
		{
			if ( !double.IsFinite( x0[i] ) )
				return null;
		}

		return x0;
	}

	static double Dot( double[] a, double[] b )
	{
		var sum = 0.0;

		for ( var i = 0; i < a.Length; i++ )
			sum += a[i] * b[i];

		return sum;
	}

	static int FarthestOnBoundary( Patch patch, Vec3[] p, int from )
	{
		var best = from;
		var bestDistance = -1f;

		foreach ( var (a, _) in patch.Boundary )
		{
			var d = (p[a] - p[from]).LengthSquared;

			if ( d > bestDistance || (d == bestDistance && a < best) )
			{
				best = a;
				bestDistance = d;
			}
		}

		return best;
	}

	/// <summary>
	/// Whether the layout is one-to-one: no triangle turned over, and no two boundary edges crossing.
	/// For a sheet that is the whole test - a layout that keeps every triangle the right way up and
	/// whose outline does not cross itself cannot overlap anywhere inside either.
	///
	/// LSCM is not guaranteed to pass this. It is on any chart this unwrap would sensibly produce,
	/// and the rare chart that folds is split rather than shipped, because one folded triangle is a
	/// texel two faces both claim, and that is exactly what NormalBake.Measure refuses.
	/// </summary>
	static bool Embedded( Patch patch, double[] uv )
	{
		var total = 0.0;
		var signed = new double[patch.Triangles.Count];

		for ( var t = 0; t < signed.Length; t++ )
		{
			var (a, b, c) = patch.Triangles[t];
			signed[t] = Area( uv, a, b, c );
			total += signed[t];
		}

		if ( !(total > 0) )
			return false;

		foreach ( var s in signed )
		{
			if ( s < -total * 1e-9 )
				return false;
		}

		return !BoundaryCrosses( patch, uv );
	}

	static double Area( double[] uv, int a, int b, int c ) =>
		0.5 * ((uv[b * 2] - uv[a * 2]) * (uv[c * 2 + 1] - uv[a * 2 + 1]) - (uv[c * 2] - uv[a * 2]) * (uv[b * 2 + 1] - uv[a * 2 + 1]));

	/// <summary>Any two boundary edges crossing, by a uniform grid over the layout's bounds so a
	/// long outline is not every pair against every pair.</summary>
	static bool BoundaryCrosses( Patch patch, double[] uv )
	{
		var edges = patch.Boundary;

		if ( edges.Count < 4 )
			return false;

		double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

		foreach ( var (a, _) in edges )
		{
			minX = Math.Min( minX, uv[a * 2] );
			maxX = Math.Max( maxX, uv[a * 2] );
			minY = Math.Min( minY, uv[a * 2 + 1] );
			maxY = Math.Max( maxY, uv[a * 2 + 1] );
		}

		var cells = Math.Clamp( (int)Math.Sqrt( edges.Count ), 1, 256 );
		var cw = Math.Max( (maxX - minX) / cells, 1e-12 );
		var ch = Math.Max( (maxY - minY) / cells, 1e-12 );
		var grid = new List<int>[cells * cells];

		int Cell( double v, double min, double w ) => Math.Clamp( (int)((v - min) / w), 0, cells - 1 );

		for ( var e = 0; e < edges.Count; e++ )
		{
			var (a, b) = edges[e];
			var x0 = Cell( Math.Min( uv[a * 2], uv[b * 2] ), minX, cw );
			var x1 = Cell( Math.Max( uv[a * 2], uv[b * 2] ), minX, cw );
			var y0 = Cell( Math.Min( uv[a * 2 + 1], uv[b * 2 + 1] ), minY, ch );
			var y1 = Cell( Math.Max( uv[a * 2 + 1], uv[b * 2 + 1] ), minY, ch );

			for ( var y = y0; y <= y1; y++ )
			{
				for ( var x = x0; x <= x1; x++ )
				{
					var list = grid[y * cells + x] ??= new List<int>();

					foreach ( var other in list )
					{
						var (c, d) = edges[other];

						if ( a == c || a == d || b == c || b == d )
							continue;

						if ( Cross( uv, a, b, c, d ) )
							return true;
					}

					list.Add( e );
				}
			}
		}

		return false;
	}

	/// <summary>Whether segments ab and cd properly cross. Touching at an end does not count; the
	/// fill rule gives a shared point to one side only.</summary>
	static bool Cross( double[] uv, int a, int b, int c, int d )
	{
		double Orient( int p, int q, int r ) =>
			(uv[q * 2] - uv[p * 2]) * (uv[r * 2 + 1] - uv[p * 2 + 1]) - (uv[r * 2] - uv[p * 2]) * (uv[q * 2 + 1] - uv[p * 2 + 1]);

		var d1 = Orient( a, b, c );
		var d2 = Orient( a, b, d );
		var d3 = Orient( c, d, a );
		var d4 = Orient( c, d, b );

		return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
	}

	/// <summary>
	/// The solved layout as a chart, scaled so its UV area equals its surface area.
	///
	/// LSCM's own scale is set by where the pins went and drifts with curvature, so without this a
	/// curved chart would pack at a different texel density from a flat one beside it. Scaling every
	/// chart to its true area first is what lets the packer's one-scale-for-everything mean uniform
	/// density again.
	/// </summary>
	static Chart MakeChart( PolyMesh mesh, Patch patch, double[] uv )
	{
		var area3 = 0.0;
		var areaUV = 0.0;

		for ( var t = 0; t < patch.Triangles.Count; t++ )
		{
			var (a, b, c) = patch.Triangles[t];
			var pa = mesh.Positions[patch.MeshVertex[a]];
			area3 += 0.5 * Vec3.Cross( mesh.Positions[patch.MeshVertex[b]] - pa, mesh.Positions[patch.MeshVertex[c]] - pa ).Length;
			areaUV += Math.Abs( Area( uv, a, b, c ) );
		}

		var scale = areaUV > 1e-30 && area3 > 0 ? Math.Sqrt( area3 / areaUV ) : 1.0;
		var chart = new Chart();
		var min = new Vec2( float.MaxValue, float.MaxValue );
		var max = new Vec2( float.MinValue, float.MinValue );

		for ( var i = 0; i < patch.Faces.Count; i++ )
		{
			var count = patch.CornerStart[i + 1] - patch.CornerStart[i];
			var corners = new Vec2[count];

			for ( var c = 0; c < count; c++ )
			{
				var v = patch.Vertex[patch.CornerStart[i] + c];
				corners[c] = new Vec2( (float)(uv[v * 2] * scale), (float)(uv[v * 2 + 1] * scale) );

				min = new Vec2( MathF.Min( min.x, corners[c].x ), MathF.Min( min.y, corners[c].y ) );
				max = new Vec2( MathF.Max( max.x, corners[c].x ), MathF.Max( max.y, corners[c].y ) );
			}

			chart.Faces.Add( patch.Faces[i] );
			chart.Corners.Add( corners );
		}

		chart.Min = min;
		chart.Size = new Vec2( MathF.Max( max.x - min.x, 0f ), MathF.Max( max.y - min.y, 0f ) );

		return chart;
	}

	/// <summary>The area-weighted mean stretch of one chart, before packing, against its own
	/// density - the number the organic rule splits on.</summary>
	static float ChartStretch( PolyMesh mesh, Chart chart )
	{
		var area3 = 0.0;
		var areaUV = 0.0;

		for ( var i = 0; i < chart.Faces.Count; i++ )
		{
			var face = mesh.Faces[chart.Faces[i]];
			var uvs = chart.Corners[i];

			for ( var c = 1; c + 1 < face.Count; c++ )
			{
				var p0 = mesh.Positions[face.Indices[0]];
				area3 += 0.5 * Vec3.Cross( mesh.Positions[face.Indices[c]] - p0, mesh.Positions[face.Indices[c + 1]] - p0 ).Length;
				areaUV += Math.Abs( Vec2.Cross( uvs[c] - uvs[0], uvs[c + 1] - uvs[0] ) ) * 0.5;
			}
		}

		if ( area3 <= 0 || areaUV <= 0 )
			return 1f;

		var density = Math.Sqrt( areaUV / area3 );
		var sum = 0.0;
		var weight = 0.0;

		for ( var i = 0; i < chart.Faces.Count; i++ )
		{
			var face = mesh.Faces[chart.Faces[i]];
			var uvs = chart.Corners[i];

			for ( var c = 1; c + 1 < face.Count; c++ )
			{
				var p0 = mesh.Positions[face.Indices[0]];
				var p1 = mesh.Positions[face.Indices[c]];
				var p2 = mesh.Positions[face.Indices[c + 1]];
				var s = UVIslands.TriangleStretch( p0, p1, p2, uvs[0], uvs[c], uvs[c + 1], density );

				if ( s < 0 )
					continue;

				var a = 0.5 * Vec3.Cross( p1 - p0, p2 - p0 ).Length;
				sum += s * a;
				weight += a;
			}
		}

		return weight > 0 ? (float)(sum / weight) : 1f;
	}

	// --- 3. packing -------------------------------------------------------------------------

	/// <summary>
	/// Shelf-pack the charts into the unit square and return the scale used.
	///
	/// ONE SCALE FOR EVERYTHING, chosen by binary search for the largest that still fits. Packing
	/// each chart to fill its own slot would waste no space and give a big flat face the same number
	/// of texels as a tiny bevel, so the bake would resolve the bevel beautifully and the face not at
	/// all. Uniform density is the whole reason to pack rather than to fit.
	///
	/// Shelves rather than anything cleverer: charts are sorted tallest first and laid in rows. It
	/// wastes some square, and every alternative worth having needs a real bin packer.
	/// </summary>
	static float Pack( List<Chart> charts, float margin )
	{
		if ( charts.Count == 0 )
			return 1f;

		// TRIED BOTH WAYS UP, AND THE BETTER ONE KEPT.
		//
		// A chart has no up - its UVs are a projection nobody reads as oriented - so turning one
		// costs nothing, and shelf packing does much better on a row of similar heights. But "turn
		// every tall chart" is too blunt a rule to trust: measured across the primitives it gains 21
		// points on a long box and LOSES 7 on a cylinder, whose charts were already a uniform height
		// and got shuffled into a ragged one.
		//
		// Packing is cheap and the answer is measurable, so both policies are packed and the one with
		// the larger scale wins. That can only ever be as good as the better of the two.
		var best = 0f;
		var bestTurned = false;
		var bestByArea = false;

		// Four arrangements, and the one that packs largest wins. Each is one bisection over a list
		// of a few dozen boxes, so the whole search is far cheaper than the raycasting the bake does
		// afterwards - and the answer is measured rather than argued about.
		foreach ( var turnTall in new[] { false, true } )
		{
			foreach ( var byArea in new[] { false, true } )
			{
				Arrange( charts, turnTall, byArea );

				var scale = Search( charts, margin );

				if ( scale <= best )
					continue;

				best = scale;
				bestTurned = turnTall;
				bestByArea = byArea;
			}
		}

		Arrange( charts, bestTurned, bestByArea );

		if ( best <= 0f )
			best = 1e-4f;

		TryPack( charts, best, margin, commit: true );
		return best;
	}

	/// <summary>Put the charts in one of the four arrangements the packer tries.</summary>
	static void Arrange( List<Chart> charts, bool turnTall, bool byArea )
	{
		foreach ( var chart in charts )
			chart.Turned = turnTall && chart.Size.y > chart.Size.x;

		Order( charts, byArea );
	}

	/// <summary>
	/// Tallest first, and TOTALLY ordered.
	///
	/// List.Sort is not stable, so a comparison on height alone leaves charts of equal height in
	/// whatever order the sort happened to land on. That was harmless while the list was sorted once;
	/// trying several arrangements means sorting it again to commit the winner, and two different
	/// orders means committing at a scale measured against a different arrangement - which packs the
	/// islands ON TOP of each other. The tell was coverage coming out HIGHER than any single policy
	/// could achieve, and 19% of a cylinder's texels claimed twice.
	///
	/// Introduced by the multi-arrangement search above and fixed here, rather than inherited.
	///
	/// The width and the first face index break every tie, so the order is a function of the charts.
	/// </summary>
	static void Order( List<Chart> charts, bool byArea ) => charts.Sort( ( a, b ) =>
	{
		var first = byArea
			? (b.Packed.x * b.Packed.y).CompareTo( a.Packed.x * a.Packed.y )
			: b.Packed.y.CompareTo( a.Packed.y );

		if ( first != 0 )
			return first;

		var byHeight = b.Packed.y.CompareTo( a.Packed.y );

		if ( byHeight != 0 )
			return byHeight;

		var byWidth = b.Packed.x.CompareTo( a.Packed.x );

		if ( byWidth != 0 )
			return byWidth;

		// The owning mesh before the face index: in an atlas, face 0 of two bodies is two charts.
		return a.Owner != b.Owner ? a.Owner.CompareTo( b.Owner ) : a.Faces[0].CompareTo( b.Faces[0] );
	} );

	/// <summary>The largest scale this arrangement still fits at, by bisection.</summary>
	static float Search( List<Chart> charts, float margin )
	{

		var low = 0f;
		var high = 1f;

		// An upper bound that certainly fails, so the search below has something to close on.
		foreach ( var chart in charts )
		{
			var largest = MathF.Max( chart.Packed.x, chart.Packed.y );

			if ( largest > 1e-9f )
				high = MathF.Max( high, 2f / largest );
		}

		var best = 0f;

		for ( var i = 0; i < 40; i++ )
		{
			var mid = (low + high) * 0.5f;

			if ( TryPack( charts, mid, margin, commit: false ) )
			{
				best = mid;
				low = mid;
			}
			else
			{
				high = mid;
			}
		}

		return best;
	}

	static bool TryPack( List<Chart> charts, float scale, float margin, bool commit )
	{
		var x = margin;
		var y = margin;
		var shelfHeight = 0f;

		foreach ( var chart in charts )
		{
			var w = chart.Packed.x * scale;
			var h = chart.Packed.y * scale;

			if ( w > 1f - margin * 2f || h > 1f - margin * 2f )
				return false;

			// New shelf when this one has run out of width.
			if ( x + w + margin > 1f )
			{
				x = margin;
				y += shelfHeight + margin;
				shelfHeight = 0f;
			}

			if ( y + h + margin > 1f )
				return false;

			if ( commit )
			{
				chart.Scale = scale;
				chart.Offset = new Vec2( x, y );
			}

			x += w + margin;
			shelfHeight = MathF.Max( shelfHeight, h );
		}

		return true;
	}

	static Vec3 Normalised( Vec3 v, Vec3 fallback ) => v.LengthSquared > 1e-16f ? v.Normal : fallback;
}
