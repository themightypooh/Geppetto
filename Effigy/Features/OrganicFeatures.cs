using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Shapes fused into one skin: spheres, capsules, cones, ellipsoids and tubes smooth-unioned, with
/// cutters smooth-subtracted, meshed by <see cref="SurfaceNets"/> at a voxel size.
///
/// WHY THIS IS A FEATURE. The implicit kernel (<see cref="Sdf"/>, <see cref="SdfBlend"/>,
/// <see cref="SdfSubtract"/>) built every creature the test harness made, and none of it could be
/// reached from a build script, the CLI or the MCP: an agent modelling a heart or a gut pile had
/// to write C#. A feature is what the script's `add` already finds by reflection and what a saved
/// document already round-trips, so the whole kernel arrives through one class.
///
/// THE SHAPES ARE TEXT, one per `;`, because that is what a script line and a dialog field can both
/// hold. Points are x,y,z; a leading `-` makes a shape a cutter:
///
///   sphere 0,0,10 5
///   ellipsoid 0,0,10 6,4,8
///   capsule 0,0,0 0,0,12 3
///   cone 0,0,0 4 0,0,10 1.5          (round cone: end A, its radius, end B, its radius)
///   tube 0,0,0|4,0,6|0,0,12 2|1.5|1  (polyline through |-separated points; one radius or one per point)
///   -sphere 0,-5,10 2.5              (cut a hole)
///
/// Any shape may end with mat=N for the material slot of the skin it makes.
///
/// THE RESULT HAS NO UVS. A voxel skin has no parameterisation to inherit; unwrap it (UV project,
/// Unwrap) before baking. Said in the warning rather than left for the bake to refuse.
/// </summary>
public sealed class BlobFeature : Feature
{
	public override string TypeName => "Blob";

	public readonly StringParam Shapes = new( "Shapes", "sphere 0,0,10 6" );
	public readonly FloatParam Blend = new( "Blend", 2f, 0f, unit: "u" );
	public readonly FloatParam CutBlend = new( "Cut blend", 0.5f, 0f, unit: "u" );
	public readonly FloatParam Voxel = new( "Voxel", 0.5f, 0.01f, unit: "u" );
	public readonly IntParam Smooth = new( "Smooth passes", 2, 0, 20 );
	public readonly FloatParam Noise = new( "Noise", 0f, 0f, unit: "u" );
	public readonly FloatParam NoiseSize = new( "Noise size", 6f, 0.01f, unit: "u" );
	public readonly IntParam Seed = new( "Seed", 1, 0, 100000 ) { Slider = false };
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Shapes, Blend, CutBlend, Voxel, Smooth, Noise, NoiseSize, Seed, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Smooth, Seed, Material };

	/// <summary>A sampling grid past this many voxels takes minutes and gigabytes; refuse it with
	/// the voxel size that would fit rather than hang the rebuild.</summary>
	const long MaxVoxels = 60_000_000;

	protected override void Execute( FeatureContext ctx )
	{
		var adds = new List<Sdf>();
		var cuts = new List<Sdf>();

		foreach ( var (text, index) in (Shapes.Value ?? "").Split( ';' ).Select( ( s, i ) => (s.Trim(), i) ) )
		{
			if ( text.Length == 0 )
				continue;

			var cutter = text.StartsWith( '-' );
			var leaf = ParseShape( cutter ? text[1..].Trim() : text.TrimStart( '+' ).Trim(), index + 1 );
			(cutter ? cuts : adds).Add( leaf );
		}

		if ( adds.Count == 0 )
		{
			FailOn( "Shapes",
				"There is nothing to fuse",
				"Blob needs at least one shape that is not a cutter.",
				"Add a shape: sphere 0,0,10 6",
				"Or remove the leading '-' from one of the cutters" );
		}

		Sdf field = adds.Count == 1 ? adds[0] : new SdfBlend( Blend.Clamped, adds.ToArray() );

		if ( cuts.Count > 0 )
			field = new SdfSubtract( field, CutBlend.Clamped, cuts.ToArray() );

		if ( Noise.Value > 0f )
			field = new SdfDisplace( field, Noise.Clamped, 1f / NoiseSize.Clamped, 3, Seed.Clamped );

		var voxel = Voxel.Clamped;
		var size = field.Max - field.Min + Vec3.One * voxel * 4f;
		var voxels = (long)(size.x / voxel + 1) * (long)(size.y / voxel + 1) * (long)(size.z / voxel + 1);

		if ( voxels > MaxVoxels )
		{
			var fits = MathF.Cbrt( size.x * size.y * size.z / MaxVoxels );
			FailOn( "Voxel", MathF.Ceiling( fits * 100f ) / 100f,
				"The voxel is too small for the size of the blob",
				$"Meshing would sample {voxels:N0} voxels; the limit is {MaxVoxels:N0}.",
				$"Set Voxel to {fits:0.##} or more" );
		}

		var result = SurfaceNets.Mesh( field, voxel );
		var mesh = result.Mesh;

		if ( mesh.FaceCount == 0 )
		{
			Fail(
				"The shapes made no surface",
				"The cutters removed everything, or every shape is smaller than one voxel.",
				"Make the shapes bigger than the voxel, or the voxel smaller",
				"Check the cutters do not cover the whole blob" );
		}

		if ( Smooth.Clamped > 0 )
			MeshSmooth.Taubin( mesh, Smooth.Clamped );

		// Each face takes the slot of the shape nearest its first corner: a tooth leaf's skin stays
		// tooth even where the blend has run it into gum.
		foreach ( var face in mesh.Faces )
			face.Material = result.Nearest[face.Indices[0]]?.Material ?? Material.Clamped;

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );

		Warning = $"{mesh.FaceCount:N0} quads from {adds.Count} shape(s)" + (cuts.Count > 0 ? $" and {cuts.Count} cutter(s)" : "")
			+ ". It has no UVs yet: add a UV project (Unwrap) before baking.";
	}

	Sdf ParseShape( string text, int number )
	{
		var words = text.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries ).ToList();
		var material = Material.Clamped;

		for ( var i = words.Count - 1; i >= 0; i-- )
		{
			if ( !words[i].StartsWith( "mat=", StringComparison.OrdinalIgnoreCase ) )
				continue;

			material = (int)Num( words[i][4..], number, text );
			words.RemoveAt( i );
		}

		if ( words.Count == 0 )
			BadShape( number, text, "it is empty" );

		var kind = words[0].ToLowerInvariant();
		var a = words.Skip( 1 ).ToList();

		SdfLeaf leaf = kind switch
		{
			"sphere" when a.Count == 2 => new SdfSphere( Pt( a[0], number, text ), Num( a[1], number, text ) ),
			"ellipsoid" when a.Count == 2 => new SdfEllipsoid( Pt( a[0], number, text ), Pt( a[1], number, text ) ),
			"capsule" when a.Count == 3 => new SdfCapsule( Pt( a[0], number, text ), Pt( a[1], number, text ), Num( a[2], number, text ) ),
			"cone" when a.Count == 4 => new SdfRoundCone( Pt( a[0], number, text ), Num( a[1], number, text ), Pt( a[2], number, text ), Num( a[3], number, text ) ),
			"tube" when a.Count == 2 => Tube( a[0], a[1], number, text ),
			"sphere" or "ellipsoid" or "capsule" or "cone" or "tube" => BadShape( number, text, $"{kind} takes {Usage( kind )}" ),
			_ => BadShape( number, text, $"'{kind}' is not a shape; use sphere, ellipsoid, capsule, cone or tube" ),
		};

		leaf.Material = material;
		leaf.Tag = $"shape{number}";
		return leaf;
	}

	SdfLeaf Tube( string pointsText, string radiiText, int number, string text )
	{
		var points = pointsText.Split( '|', StringSplitOptions.RemoveEmptyEntries ).Select( p => Pt( p, number, text ) ).ToList();
		var radii = radiiText.Split( '|', StringSplitOptions.RemoveEmptyEntries ).Select( r => Num( r, number, text ) ).ToList();

		if ( points.Count < 2 )
			BadShape( number, text, "a tube needs two or more points, separated by |" );

		if ( radii.Count == 1 )
			radii = Enumerable.Repeat( radii[0], points.Count ).ToList();

		if ( radii.Count != points.Count )
			BadShape( number, text, $"a tube needs one radius, or one per point ({points.Count})" );

		return new SdfTube( points, radii );
	}

	static string Usage( string kind ) => kind switch
	{
		"sphere" => "a centre and a radius: sphere 0,0,10 5",
		"ellipsoid" => "a centre and three radii: ellipsoid 0,0,10 6,4,8",
		"capsule" => "two ends and a radius: capsule 0,0,0 0,0,12 3",
		"cone" => "an end, its radius, the other end, its radius: cone 0,0,0 4 0,0,10 1.5",
		_ => "points and radii: tube 0,0,0|4,0,6|0,0,12 2|1.5|1",
	};

	static Vec3 Pt( string s, int number, string text )
	{
		if ( !ModelMeasure.TryParseVec3( s, out var p ) )
			BadShape( number, text, $"'{s}' is not a point x,y,z" );
		return p;
	}

	static float Num( string s, int number, string text )
	{
		if ( !float.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) )
			BadShape( number, text, $"'{s}' is not a number" );
		return v;
	}

	static SdfLeaf BadShape( int number, string text, string why )
	{
		FailOn( "Shapes", $"Shape {number} cannot be read", $"'{text}': {why}.", "Fix that shape, or remove it" );
		return null;
	}
}

/// <summary>
/// One sculpt stroke, as data: a brush dragged along a path of points over a body, replayed on
/// every rebuild.
///
/// WHY A STROKE IS A FEATURE. The editor's sculpt keeps its result in a sidecar of per-level deltas
/// (<see cref="SculptFeature"/>), which is right for a hand on a mouse and useless to an agent: there
/// is no line it can write that puts a crease in. A stroke written as points is reproducible, can be
/// edited and re-run like any other parameter, and survives the body above it changing, because it
/// is re-projected onto whatever surface is there when it runs.
///
/// THE PATH IS PROJECTED, NOT TRUSTED. Each point is resampled along the path at Spacing × radius
/// and moved to the nearest point on the body, so a path sketched an inch off the skin still lands
/// on it. A path that lands nowhere is an error, not a stroke that quietly did nothing.
///
/// DENSITY IS THE CALLER'S. Brushes move vertices; a radius smaller than two edges has nothing to
/// move and the stroke says so. Put a Subdivide above it.
/// </summary>
public sealed class StrokeFeature : Feature
{
	public override string TypeName => "Stroke";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>The path, in model space. Landmarks are allowed in a script.</summary>
	public List<Vec3> Points = new();

	public readonly ChoiceParam Brush = new( "Brush", Enum.GetNames( typeof( BrushKind ) ), (int)BrushKind.Draw );
	public readonly FloatParam Radius = new( "Radius", 1f, 0.001f, unit: "u" );
	public readonly FloatParam Strength = new( "Strength", 0.5f, 0f, 10f );
	public readonly BoolParam Invert = new( "Invert", false );
	public readonly ChoiceParam Falloff = new( "Falloff", Enum.GetNames( typeof( BrushFalloff ) ) );
	/// <summary>Mirror planes. The first four are the single planes older files stored by index; the
	/// rest are combinations. See <see cref="BrushSymmetry.Choices"/>.</summary>
	public readonly ChoiceParam Mirror = new( "Mirror", BrushSymmetry.ChoiceNames() );

	/// <summary>Radial symmetry: the stroke repeats this many times evenly round the radial axis
	/// through the origin. 1 is off.</summary>
	public readonly IntParam Radial = new( "Radial copies", 1, 1, 64 );
	public readonly ChoiceParam RadialAxis = new( "Radial axis", new[] { "X", "Y", "Z" }, 2 );
	public readonly FloatParam Spacing = new( "Spacing", 0.25f, 0.02f, 4f );
	public readonly ChoiceParam Mask = new( "Only", new[] { "Everywhere", "Creases", "Ridges" } );
	public readonly StringParam Alpha = new( "Alpha PNG", "" );
	public readonly FloatParam AlphaScale = new( "Alpha scale", 1f, 0.01f, 100f );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Brush, Radius, Strength, Invert, Falloff, Mirror, Radial, RadialAxis, Spacing, Mask, Alpha, AlphaScale };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Falloff, RadialAxis, Spacing, Mask, Alpha, AlphaScale };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Points.Count == 0 )
		{
			Fail(
				"The stroke has no path",
				"A stroke is a brush dragged along points, and none are set.",
				"Set points: points=x,y,z;x,y,z (or @landmarks)" );
		}

		var alpha = LoadAlpha();
		var radius = Radius.Clamped;
		var path = Resample( Points, MathF.Max( radius * Spacing.Clamped, 1e-4f ) );
		var kind = (BrushKind)Brush.Index;
		var touched = 0;
		var missed = new List<string>();
		var coarse = new List<string>();
		var coarseIds = new List<string>();
		var coarsest = 0f;

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var bvh = MeshBVH.Build( mesh );
			var stroke = new BrushStroke
			{
				Kind = kind,
				Falloff = (BrushFalloff)Falloff.Index,
				MirrorAxes = BrushSymmetry.Choices[Math.Clamp( Mirror.Index, 0, BrushSymmetry.Choices.Length - 1 )],
				Radial = Radial.Clamped,
				RadialAxis = (MirrorAxis)(RadialAxis.Index + 1),
				Alpha = alpha,
			};

			// Reach: far enough to forgive a path drawn off the skin, near enough that a path over one
			// body is not pulled onto another across a gap.
			var (lo, hi) = ModelMeasure.Bounds( mesh );
			var reach = MathF.Max( radius * 4f, (hi - lo).Length * 0.05f );
			var strength = Invert.Value ? -Strength.Clamped : Strength.Clamped;

			foreach ( var p in path )
			{
				if ( bvh.NearestSurface( mesh, p, reach ) is { } hit )
					stroke.Samples.Add( new BrushSample( hit.Point, hit.Normal, radius, strength ) );
			}

			if ( stroke.Samples.Count == 0 )
			{
				missed.Add( body.Name ?? body.Id );
				continue;
			}

			var mask = Mask.Index switch
			{
				1 => AutoMask.Cavity( mesh ),
				2 => AutoMask.Cavity( mesh, inverted: true ),
				_ => null,
			};

			var undo = Effigy.Brush.Apply( mesh, stroke, mask: mask, bvh: bvh );
			touched += undo.Count;

			if ( undo.Count == 0 )
			{
				var edge = MeanEdge( mesh );
				coarse.Add( edge > radius * 0.5f
					? $"{body.Name ?? body.Id}: the brush is {radius:0.##} across and its edges are {edge:0.##} long, so no vertex sits under it"
					: $"{body.Name ?? body.Id}: every vertex under it was masked out, or the strength is zero" );

				if ( edge > radius * 0.5f )
				{
					coarseIds.Add( body.Id );
					coarsest = MathF.Max( coarsest, edge );
				}
			}
		}

		// A stroke that moved nothing anywhere is an error: it is in the history claiming to be a
		// crease, and the skin is untouched. One that missed only some of its bodies is a warning.
		if ( touched == 0 && coarseIds.Count > 0 )
		{
			Fail(
				RemedyAction.SubdivideAbove( coarseIds, coarsest, radius * 0.5f ),
				"The stroke moved nothing",
				string.Join( "; ", coarse ) + ".",
				"Put a Subdivide above this, or make the radius bigger",
				"Check Strength and Only" );
		}

		if ( touched == 0 )
		{
			Fail(
				"The stroke moved nothing",
				coarse.Count > 0
					? string.Join( "; ", coarse ) + "."
					: "No point on the path is near the surface of the bodies it names.",
				coarse.Count > 0 ? "Put a Subdivide above this, or make the radius bigger" : "Move the points onto the body, or use landmarks like @Body.front",
				coarse.Count > 0 ? "Check Strength and Only" : "Check the Bodies selection" );
		}

		if ( missed.Count + coarse.Count > 0 )
		{
			Warn(
				"The stroke missed some bodies",
				string.Join( "; ", missed.Select( m => $"{m}: the path is nowhere near it" ).Concat( coarse ) ) + ".",
				"Take those bodies out of the selection, or move the path onto them" );
		}
	}

	BrushAlpha LoadAlpha()
	{
		var path = (Alpha.Value ?? "").Trim();

		if ( path.Length == 0 )
			return null;

		if ( !File.Exists( path ) )
		{
			FailOn( "Alpha PNG",
				"The alpha image is not there",
				$"No file at {Path.GetFullPath( path )}.",
				"Give a full path to a greyscale PNG",
				"Or clear Alpha PNG for a plain brush" );
		}

		var alpha = BrushAlpha.FromPng( File.ReadAllBytes( path ) );
		alpha.Scale = AlphaScale.Clamped;
		return alpha;
	}

	static List<Vec3> Resample( List<Vec3> points, float step )
	{
		var output = new List<Vec3> { points[0] };

		for ( var i = 1; i < points.Count; i++ )
		{
			var a = points[i - 1];
			var b = points[i];
			var n = Math.Max( 1, (int)MathF.Ceiling( (b - a).Length / step ) );

			for ( var k = 1; k <= n; k++ )
				output.Add( a + (b - a) * (k / (float)n) );
		}

		return output;
	}

	internal static float MeanEdge( PolyMesh mesh )
	{
		double sum = 0;
		long count = 0;

		foreach ( var f in mesh.Faces )
			for ( var i = 0; i < f.Count; i++ )
			{
				sum += (mesh.Positions[f.Indices[i]] - mesh.Positions[f.Indices[(i + 1) % f.Count]]).Length;
				count++;
			}

		return count == 0 ? 0f : (float)(sum / count);
	}
}

/// <summary>
/// The secondary and tertiary skin passes of <see cref="SurfaceDetail"/> as a feature: grain (tissue
/// lumps and a Worley crease network), pustule clusters, an even scatter of domes or pits, and sag
/// folds — masked to creases or ridges, to a share of the skin, and to a reach around a point.
///
/// WHY THE MASKS MATTER MORE THAN THE PASSES. The judges' first lesson was that detail spread evenly
/// reads as noise: sculptors keep most skin calm. So Busy defaults below one (about 40% of the skin
/// takes the pass, in broad patches) and Near/Reach confine a pass to where it means something.
///
/// Sizes are in model units (inches on a creature). A pass finer than the mesh moves nothing; the
/// feature measures that and says to subdivide, rather than leaving the skin untouched in silence.
/// </summary>
public sealed class DetailFeature : Feature
{
	public override string TypeName => "Surface detail";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Pass = new( "Pass", new[] { "Grain", "Pustules", "Scatter", "Sag folds" } );

	/// <summary>Grain: the crease cells' width. Pustules: the biggest one's radius. Scatter: each
	/// stamp's radius. Sag folds: the distance between fold crests.</summary>
	public readonly FloatParam Size = new( "Size", 1f, 0.001f, unit: "u" );

	/// <summary>Grain: how deep the creases are cut. Scatter and folds: the height; negative pits.</summary>
	public readonly FloatParam Depth = new( "Depth", 0.05f, unit: "u" );

	public readonly FloatParam Lumps = new( "Lumps", 0.05f, 0f, unit: "u" );
	public readonly FloatParam LumpSize = new( "Lump size", 3f, 0.01f, unit: "u" );
	public readonly IntParam Clusters = new( "Clusters", 3, 1, 200 );
	public readonly IntParam PerCluster = new( "Per cluster", 8, 1, 500 );
	public readonly FloatParam Spread = new( "Spread", 3f, 0.01f, unit: "u" );
	public readonly FloatParam Spacing = new( "Spacing", 2f, 0.01f, unit: "u" );
	public readonly BoolParam Pits = new( "Pits, not domes", false );
	public readonly FloatParam Busy = new( "Busy", 0.4f, 0.01f, 1f );
	public readonly FloatParam BusySize = new( "Busy patch size", 16f, 0.1f, unit: "u" );
	public readonly ChoiceParam Only = new( "Only", new[] { "Everywhere", "Creases", "Ridges" } );
	public readonly Vec3Param Near = new( "Near", Vec3.Zero );
	public readonly FloatParam Reach = new( "Reach", 0f, 0f, unit: "u" );
	public readonly Vec3Param Up = new( "Up", new Vec3( 0, 0, 1 ) );
	public readonly IntParam Seed = new( "Seed", 1, 0, 100000 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Pass, Size, Depth, Lumps, LumpSize, Clusters, PerCluster, Spread, Spacing, Pits, Busy, BusySize, Only, Near, Reach, Up, Seed };

	public override IReadOnlyList<IParam> AdvancedParameters =>
		new IParam[] { Lumps, LumpSize, Clusters, PerCluster, Spread, Spacing, Pits, BusySize, Up, Seed };

	protected override void Execute( FeatureContext ctx )
	{
		var moved = 0f;
		var coarse = new List<string>();
		var coarseIds = new List<string>();
		var coarsest = 0f;

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var before = mesh.Positions.ToArray();
			var mask = BuildMask( mesh );
			bool Where( int v ) => mask[v] > 0.5f;

			switch ( Pass.Index )
			{
				case 0:
					SurfaceDetail.Grain( mesh, Lumps.Clamped, 1f / LumpSize.Clamped, Size.Clamped, Depth.Value, Seed.Clamped, v => mask[v] );
					break;

				case 1:
				{
					var stamps = SurfaceDetail.Clusters( mesh, Where, Clusters.Clamped, PerCluster.Clamped, Spread.Clamped, Size.Clamped * 0.3f, Size.Clamped, Seed.Clamped );
					SurfaceDetail.Stamps( mesh, Pits.Value ? stamps.Select( s => s with { Height = -s.Height } ) : stamps );
					break;
				}

				case 2:
				{
					var height = MathF.Abs( Depth.Value ) * (Pits.Value || Depth.Value < 0f ? -1f : 1f);
					var picked = SurfaceDetail.PoissonOnSurface( mesh, Spacing.Clamped, Where, Seed.Clamped );
					SurfaceDetail.Stamps( mesh, picked.Select( v => new SurfaceStamp( before[v], Size.Clamped, height ) ) );
					break;
				}

				default:
					SurfaceDetail.SagFolds( mesh, mask, new FoldOptions { Spacing = Size.Clamped, Amplitude = Depth.Value, Up = Up.Value, Seed = Seed.Clamped } );
					break;
			}

			var most = 0f;
			for ( var v = 0; v < mesh.VertexCount; v++ )
				most = MathF.Max( most, (mesh.Positions[v] - before[v]).Length );

			moved = MathF.Max( moved, most );

			var edge = StrokeFeature.MeanEdge( mesh );
			if ( edge > Size.Clamped * 0.5f )
			{
				coarse.Add( $"{body.Name ?? body.Id} (edges {edge:0.##}, size {Size.Clamped:0.##})" );
				coarseIds.Add( body.Id );
				coarsest = MathF.Max( coarsest, edge );
			}
		}

		var subdivide = coarseIds.Count > 0 ? RemedyAction.SubdivideAbove( coarseIds, coarsest, Size.Clamped * 0.5f ) : null;

		if ( moved <= 1e-6f && subdivide is not null )
		{
			Fail(
				subdivide,
				"The detail pass moved nothing",
				"The detail is finer than the mesh: " + string.Join( ", ", coarse ) + ".",
				"Put a Subdivide above this, or make Size bigger",
				"Widen Reach, raise Busy, or set Only to Everywhere" );
		}

		if ( moved <= 1e-6f )
		{
			Fail(
				"The detail pass moved nothing",
				coarse.Count > 0
					? "The detail is finer than the mesh: " + string.Join( ", ", coarse ) + "."
					: "The masks left no skin for it: Only, Busy and Reach together exclude every vertex.",
				"Put a Subdivide above this, or make Size bigger",
				"Widen Reach, raise Busy, or set Only to Everywhere" );
		}

		if ( coarse.Count > 0 )
		{
			Warn(
				subdivide,
				"Some detail is finer than the mesh",
				"Edges longer than half the detail size alias it into facets: " + string.Join( ", ", coarse ) + ".",
				"Put a Subdivide above this",
				"Or make Size bigger" );
		}
	}

	float[] BuildMask( PolyMesh mesh )
	{
		var n = mesh.VertexCount;
		var mask = Busy.Clamped >= 0.999f
			? Enumerable.Repeat( 1f, n ).ToArray()
			: SurfaceDetail.BusyMask( mesh, Busy.Clamped, 1f / BusySize.Clamped, Seed.Clamped + 5 );

		if ( Only.Index > 0 )
		{
			var cavity = AutoMask.Cavity( mesh, inverted: Only.Index == 2 );
			for ( var v = 0; v < n; v++ ) mask[v] *= cavity[v];
		}

		if ( Reach.Value > 0f )
		{
			for ( var v = 0; v < n; v++ )
			{
				var t = Math.Clamp( 1f - (mesh.Positions[v] - Near.Value).Length / Reach.Value, 0f, 1f );
				mask[v] *= t * t * (3f - 2f * t);
			}
		}

		return mask;
	}
}
