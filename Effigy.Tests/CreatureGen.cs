using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Body-horror creatures built from the implicit kernel: a field of blended parts, meshed with
/// surface nets, labelled by part for material and bone, and written out for rendering and for
/// the engine.
///
/// Invoked as: Effigy.Tests.exe --creature NAME [outDir] [key=value...]
/// </summary>
public static partial class CreatureGen
{
	public const int SkinMat = 0, FleshMat = 1, ClothMat = 2, BoneMat = 3, CavityMat = 4, SpikeMat = 5, FatMat = 6, EyeMat = 7, GumMat = 8, HairMat = 9;

	public static readonly Vec3[] Palette =
	{
		new( 0.70f, 0.52f, 0.44f ),   // skin
		new( 0.33f, 0.04f, 0.05f ),   // flesh
		new( 0.16f, 0.18f, 0.24f ),   // cloth (navy workwear)
		new( 0.85f, 0.80f, 0.68f ),   // bone / teeth
		new( 0.12f, 0.02f, 0.03f ),   // cavity
		new( 0.03f, 0.03f, 0.035f ),  // spike
		new( 0.85f, 0.78f, 0.55f ),   // fat
		new( 0.90f, 0.90f, 0.88f ),   // eye
		new( 0.55f, 0.15f, 0.20f ),   // gum / lip
		new( 0.10f, 0.07f, 0.05f ),   // hair
		new( 0.08f, 0.06f, 0.05f ),   // boot
		new( 0.95f, 0.75f, 0.10f ),   // hard hat
	};

	public sealed class Options
	{
		public float Voxel = 0.5f;
		public int Seed = 7;
		public int Smooth = 3;
		public Dictionary<string, string> Extra = new();

		public float Get( string key, float fallback ) =>
			Extra.TryGetValue( key, out var v ) && float.TryParse( v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) ? f : fallback;
	}

	public static int Run( string name, string outDir, string[] extra )
	{
		Directory.CreateDirectory( outDir );

		var options = new Options();
		foreach ( var kv in extra )
		{
			var eq = kv.IndexOf( '=' );
			if ( eq <= 0 ) continue;
			var key = kv[..eq];
			var value = kv[(eq + 1)..];
			options.Extra[key] = value;
			if ( key == "voxel" ) options.Voxel = float.Parse( value, CultureInfo.InvariantCulture );
			if ( key == "seed" ) options.Seed = int.Parse( value );
			if ( key == "smooth" ) options.Smooth = int.Parse( value );
		}

		MeshParts.Clear();
		Detail = null;
		Sdf field = name switch
		{
			"smoke" => Smoke( options ),
			"limbtest" => LimbTest( options ),
			"partstest" => PartsTest( options ),
			"realparts" => RealPartsTest( options ),
			"heads" => HeadsTest( options ),
			"choir" => Choir( options ),
			"stalk" => Stalk( options ),
			"bloom" => Bloom( options ),
			_ when name.StartsWith( "study_" ) => PartStudy( name, options ),
			_ => throw new ArgumentException( $"No creature called '{name}'" ),
		};

		return Build( name, field, options, outDir );
	}

	/// <summary>A creature's surface pass on its voxel skin, given the leaf that made each vertex; set by the creature's builder.</summary>
	static Action<PolyMesh, Sdf[]> Detail;

	/// <summary>
	/// The usual secondary pass for a fused creature, after the organic-modeling skill's surface
	/// recipe: grown seams where soft parts meet, hanging folds where skin is loose
	/// (<paramref name="looseness"/> by tag), pustules on the flesh, then grain over all of it.
	/// Sizes suit a skin meshed at about 0.3 in.
	/// </summary>
	static Action<PolyMesh, Sdf[]> FleshDetail( int seed )
	{
		return ( mesh, nearest ) =>
		{
			bool Soft( Sdf leaf ) => leaf is not null && leaf.Material is SkinMat or FleshMat or ClothMat or FatMat or GumMat;
			var ids = new Dictionary<string, int>();
			var part = nearest.Select( l => Soft( l ) ? (ids.TryGetValue( l.Tag ?? "?", out var k ) ? k : ids[l.Tag ?? "?"] = ids.Count) : -1 ).ToArray();
			// Seams only where separate bodies fuse: a hand on its own wrist or a head on its own neck
			// is one body, and a grown seam there reads as a cuff or a collar.
			var tagOf = ids.OrderBy( kv => kv.Value ).Select( kv => kv.Key ).ToArray();
			bool Joint( string t ) => t.Contains( "hand", StringComparison.OrdinalIgnoreCase ) || t.StartsWith( "head", StringComparison.OrdinalIgnoreCase ) || t.EndsWith( "_collar" ) || t.EndsWith( "_spur" );
			var seams = SurfaceDetail.Seams( mesh, part, 9f, ( a, b ) => !Joint( tagOf[a] ) && !Joint( tagOf[b] ) );
			// Detail goes where it means something and nowhere else: about 40% of the skin is busy.
			var busy = SurfaceDetail.BusyMask( mesh, 0.4f, 0.06f, seed + 5 );
			SurfaceDetail.GrownSeam( mesh, seams, new SeamOptions { Depth = 0.7f, Width = 0.6f, Roll = 0.8f, RollOffset = 1.4f, RollWidth = 1.1f, Sag = 0.8f, Seed = seed } );
			// A few folds hung from each seam on its lower side, not a field of them.
			SurfaceDetail.AnchoredFolds( mesh, seams.Distance, v => seams.Other[v] >= 0 && !seams.Top[v] && Soft( nearest[v] ),
				new AnchoredFoldOptions { Count = 3, First = 2.2f, Spacing = 1.7f, Amplitude = 0.55f, Seed = seed + 1 } );
			// Pustules in three clusters near the seams on the flesh.
			SurfaceDetail.Stamps( mesh, SurfaceDetail.Clusters( mesh, v => nearest[v]?.Material == FleshMat && seams.Distance[v] < 5f, 3, 8, 3f, 0.4f, 1.5f, seed + 2 ) );
			SurfaceDetail.Grain( mesh, 0.05f, 0.35f, 1.2f, 0.03f, seed + 4, v => Soft( nearest[v] ) ? busy[v] : 0f );
		};
	}

	static int Build( string name, Sdf field, Options options, string outDir )
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var result = SurfaceNets.Mesh( field, options.Voxel );
		var mesh = result.Mesh;
		Console.WriteLine( $"{name}: {result.VoxelsSampled:N0} voxels, sampled {result.Sampling.TotalSeconds:0.0}s, built {result.Building.TotalSeconds:0.0}s, {mesh.VertexCount:N0} verts {mesh.FaceCount:N0} quads" );

		if ( options.Smooth > 0 )
			MeshSmooth.Taubin( mesh, options.Smooth );

		// The creature's secondary surface pass (grown seams, folds, stamps, grain), on the voxel skin
		// before the sculpted parts go in, knowing which part made each vertex.
		if ( Detail is not null )
		{
			var t0 = clock.Elapsed;
			Detail( mesh, result.Nearest );
			Console.WriteLine( $"  detail pass {(clock.Elapsed - t0).TotalSeconds:0.0}s" );
		}

		mesh.VertexColors = new Vec4[mesh.VertexCount];
		var counts = new Dictionary<string, int>();

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var leaf = result.Nearest[i];
			var material = leaf?.Material ?? FleshMat;
			var c = Palette[Math.Clamp( material, 0, Palette.Length - 1 )];
			mesh.VertexColors[i] = new Vec4( c.x, c.y, c.z, 1f );

			var tag = leaf?.Tag ?? "?";
			counts[tag] = counts.GetValueOrDefault( tag ) + 1;
		}

		// Faces take the material most of their corners wear.
		foreach ( var face in mesh.Faces )
		{
			var tally = new Dictionary<int, int>();
			foreach ( var i in face.Indices )
			{
				var m = result.Nearest[i]?.Material ?? FleshMat;
				tally[m] = tally.GetValueOrDefault( m ) + 1;
			}
			face.Material = tally.OrderByDescending( kv => kv.Value ).First().Key;
		}

		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		Console.WriteLine( $"  bounds {min} .. {max}  size {max - min}" );
		Console.WriteLine( "  parts: " + string.Join( ", ", counts.OrderByDescending( kv => kv.Value ).Select( kv => $"{kv.Key}={kv.Value}" ) ) );

		// Sculpted parts go in as their own meshes, untouched by the voxel grid.
		if ( MeshParts.Count > 0 )
		{
			mesh = AppendParts( mesh, MeshParts );
			Console.WriteLine( $"  + {MeshParts.Count} sculpted parts, now {mesh.VertexCount:N0} verts {mesh.FaceCount:N0} faces" );
		}

		var obj = Path.Combine( outDir, name + ".obj" );
		ObjWriter.WriteFile( mesh, obj, name );
		Console.WriteLine( $"  wrote {obj} in {clock.Elapsed.TotalSeconds:0.0}s" );
		return 0;
	}

	// --- sculpted mesh parts ---------------------------------------------------------------------

	/// <summary>
	/// A sculpted part carried through as a real mesh: canonical vertices, the warps that distort
	/// it (applied forward, in order, in its own space), then a placement and a scale.
	/// </summary>
	public sealed class MeshPart
	{
		public PolyMesh Mesh;
		public List<SdfWarp> Warps = new();
		public Xform Placement = Xform.Identity;
		public float Scale = 1f;
		public int Material;
		public string Tag;
		public int Bone = -1;
		/// <summary>Measure how far the warps stretched the skin, then thin it, striate it along
		/// the stretch and tear it where it went past breaking. For skin, not for teeth.</summary>
		public bool Tension;
		public int Seed = 1;
		/// <summary>Stretch past which the tension pass tears skin by itself; very high turns
		/// automatic tears off and leaves only the cuts made on purpose.</summary>
		public float TearAt = 2.2f;
		/// <summary>Skin thickness given to every open edge (cuts and tears), and the rim tube's radius.</summary>
		public float Thickness = 0.15f, RimRadius = 0.045f;
		/// <summary>Open edges with rest z below this (in part space) are left alone: the neck cut.</summary>
		public float IgnoreEdgesBelowZ = -5.5f;
		/// <summary>Strands of skin bridging a wound: pairs of part-space points, each snapped to
		/// the nearest vertex, joined after the warps with a sagging thin tube.</summary>
		public List<(Vec3 A, Vec3 B, float Radius)> Strands = new();

		/// <summary>Optional limit on where a warped point may go, given where it started
		/// (both in part space): keeps a stretched web from bowing out past the face.</summary>
		public Func<Vec3, Vec3, Vec3> Clamp;
		/// <summary>Open edges that are lips, not wounds (by rest position in part space): they
		/// curl into the mouth toward <see cref="LipCurlDir"/> instead of getting a wound wall.</summary>
		public Func<Vec3, bool> IsLipEdge;
		/// <summary>How much of the curl a lip vertex gets (0..1), by rest position: tapers it to
		/// nothing at the ends of the cut so the corners close as skin.</summary>
		public Func<Vec3, float> LipCurlAmount;
		public float LipCurl = 0.18f;
		public Vec3 LipCurlDir = new( -1, 0, 0 );
		/// <summary>Relax this many times wherever the skin stretched past 1.5, so a web lies
		/// smooth instead of in ribbons.</summary>
		public int RelaxStretched;
		/// <summary>Local relaxes after the pose, by rest position in part space: (centre, radius,
		/// iterations), fading to nothing at the radius. For ropes and crumples a pose left behind.</summary>
		public List<(Vec3 Center, float Radius, int Iterations)> RelaxAt = new();

		/// <summary>Open edges to leave alone (by rest position in part space), besides those
		/// under <see cref="IgnoreEdgesBelowZ"/>: cuts made to fit the part to another, like the
		/// neck. They get no wound wall; with <see cref="SkirtTo"/> set they get a skirt instead.</summary>
		public Func<Vec3, bool> IgnoreEdge;
		/// <summary>Ignored edges that still get cleaned of spikes and loose scraps (no wall or rim):
		/// lining cut away by rest position, whose edge comes out ragged.</summary>
		public Func<Vec3, bool> CleanIgnored;
		/// <summary>An axis (part space) the ignored edges are skirted toward: each edge vertex
		/// gets a strip running <see cref="SkirtDepth"/> of the way to the axis, so the cut buries
		/// inside the part it meets instead of showing a gap.</summary>
		public (Vec3 A, Vec3 B)? SkirtTo;
		public float SkirtDepth = 0.45f;
		/// <summary>Which ignored edges get the skirt (rest position, part space); null = all of them.</summary>
		public Func<Vec3, bool> SkirtEdge;
		/// <summary>Drop spikes, slivers and loose pieces from the open edges before rolling them.</summary>
		public bool CleanEdges = true;
		/// <summary>How far in from a lip edge the curl reaches, measured over the skin (part inches).</summary>
		public float LipRollWidth = 0.22f;

		/// <summary>Swept compression folds where the smallest principal stretch drops under
		/// <see cref="FoldBelow"/>: crest spacing in part inches (0 = off), amplitude capped at <see cref="FoldMax"/>.</summary>
		public float FoldWavelength, FoldBelow = 0.78f, FoldMax = 0.07f;
		/// <summary>Squeezed regions smaller than this many vertices get no folds.</summary>
		public int FoldMinVerts = 120;
		/// <summary>Where folds may go at all (rest position, part space); null = anywhere.</summary>
		public Func<Vec3, bool> FoldMask;
		/// <summary>Vertices (by mesh index) that get neither folds nor striae: lips, the inside of the mouth.</summary>
		public bool[] SkinResponseBlocked;
		/// <summary>Striae where the largest principal stretch passes <see cref="StriaeAbove"/>:
		/// spacing, groove half-width and depth in part inches (spacing 0 = off). Across the
		/// stretch like real stretch marks, or along it for drag streaks.</summary>
		public float StriaeSpacing, StriaeAbove = 1.3f, StriaeWidth = 0.03f, StriaeDepth = 0.025f;
		public bool StriaeAlong;
		/// <summary>Explicit folds swept along curves, laid in after the warps and the relax.</summary>
		public List<Fold> Folds = new();
		/// <summary>Per-vertex colour by rest position (part space), instead of the material's flat one.</summary>
		public Func<Vec3, Vec3> Shade;
		/// <summary>Tendons across an opening, pinned to two mesh vertices (A to B, after the warps):
		/// a bundle of twisted fibres flared where they attach and thinned in the middle. Reach
		/// under 1 is a snapped one, hanging from A that fraction of the way and tapering off.</summary>
		public List<(int A, int B, float Radius, float Reach)> Tendons = new();
		public Vec3 TendonColour = new( 0.78f, 0.52f, 0.5f );
		/// <summary>Vertex ranges the tension pass appended for tendons (coloured with <see cref="TendonColour"/>).</summary>
		public List<(int From, int To)> TendonRanges = new();
		/// <summary>Per-vertex colour overrides (by mesh vertex index), for regions like the mouth bag.</summary>
		public Vec3?[] Colours;
		/// <summary>Set by the tension pass: per vertex, +1 pale (striae), −1 shadowed (fold valleys).</summary>
		public float[] Tone;

		/// <summary>Where each mesh vertex already is before <see cref="Warps"/> (part space), when an
		/// earlier stage posed it, e.g. a cage edit carried through Multires. Null: the mesh's own positions.</summary>
		public Vec3[] Posed;

		/// <summary>Vertex <paramref name="i"/> through the pose, the warps, the clamp and the placement.</summary>
		public Vec3 BakeAt( int i )
		{
			var p = Mesh.Positions[i];
			var q = Posed is not null && i < Posed.Length ? Posed[i] : p;
			foreach ( var w in Warps )
				q = w.Forward( q );
			if ( Clamp is not null )
				q = Clamp( p, q );
			return Placement.TransformPoint( q * Scale );
		}

		public Vec3 Bake( Vec3 p )
		{
			var q = p;
			foreach ( var w in Warps )
				q = w.Forward( q );
			if ( Clamp is not null )
				q = Clamp( p, q );
			return Placement.TransformPoint( q * Scale );
		}
	}

	public static readonly List<MeshPart> MeshParts = new();

	static PolyMesh AppendParts( PolyMesh body, List<MeshPart> parts )
	{
		var positions = new List<Vec3>( body.Positions );
		var faces = new List<Face>( body.Faces );
		var colors = new List<Vec4>( body.VertexColors ?? new Vec4[body.VertexCount] );

		// EFFIGY_ONLY=tag1,tag2 keeps only those parts (exact tags), for looking at one piece alone.
		var only = Environment.GetEnvironmentVariable( "EFFIGY_ONLY" )?.Split( ',' );
		foreach ( var part in parts )
		{
			if ( only is not null && !only.Contains( part.Tag ) ) continue;
			var offset = positions.Count;
			var c = Palette[Math.Clamp( part.Material, 0, Palette.Length - 1 )];
			var baked = Enumerable.Range( 0, part.Mesh.VertexCount ).Select( part.BakeAt ).ToArray();
			if ( Environment.GetEnvironmentVariable( "EFFIGY_PART_DEBUG" ) is { } want && (part.Tag ?? "").StartsWith( want ) && baked.Length > 0 )
			{
				var lo = baked.Aggregate( ( a, b ) => new Vec3( MathF.Min( a.x, b.x ), MathF.Min( a.y, b.y ), MathF.Min( a.z, b.z ) ) );
				var hi = baked.Aggregate( ( a, b ) => new Vec3( MathF.Max( a.x, b.x ), MathF.Max( a.y, b.y ), MathF.Max( a.z, b.z ) ) );
				Console.WriteLine( $"  part {part.Tag}: {baked.Length} verts, {lo} .. {hi}" );
			}
			var keep = part.Mesh.Faces;
			if ( part.Tension )
			{
				var (bp, kf) = TensionPass( part, baked );
				baked = bp.ToArray();
				keep = kf;
			}
			for ( var i = 0; i < baked.Length; i++ )
			{
				positions.Add( baked[i] );
				var sc = part.Shade is not null && i < part.Mesh.VertexCount ? part.Shade( part.Mesh.Positions[i] ) : c;
				if ( part.Colours is not null && i < part.Colours.Length && part.Colours[i] is { } own ) sc = own;
				foreach ( var (from, to) in part.TendonRanges )
					if ( i >= from && i < to ) sc = part.TendonColour;
				if ( part.Tone is not null && i < part.Tone.Length && part.Tone[i] != 0f )
				{
					var t = Math.Clamp( part.Tone[i], -1f, 1f );
					var grey = (sc.x + sc.y + sc.z) / 3f;
					sc = t > 0f ? Vec3.Lerp( sc, Vec3.Lerp( sc, new Vec3( grey, grey, grey ), 0.3f ) * 1.12f, t ) : sc * (1f + 0.4f * t);
				}
				colors.Add( new Vec4( sc.x, sc.y, sc.z, 1f ) );
			}
			foreach ( var f in keep )
				faces.Add( new Face( f.Indices.Select( i => i + offset ).ToArray(), null, part.Material ) );
		}

		return new PolyMesh( positions, faces ) { VertexColors = colors.ToArray() };
	}

	/// <summary>
	/// Skin under strain. λ is how much each vertex's edges grew from rest to warped. Past 1.3
	/// the skin thins (pushed in up to 0.05 in) and gets ridged striations across the stretch;
	/// past about 2.2, with noise on the threshold, it tears: those faces go.
	/// </summary>
	static (List<Vec3> Positions, List<Face> Faces) TensionPass( MeshPart part, Vec3[] baked )
	{
		var mesh = part.Mesh;
		var rest = mesh.Positions.Select( p => part.Placement.TransformPoint( p * part.Scale ) ).ToArray();
		var n = mesh.VertexCount;
		var sum = new float[n];
		var cnt = new int[n];
		var bestRatio = new float[n];
		var bestDir = new Vec3[n];
		var normals = new Vec3[n];
		foreach ( var f in mesh.Faces )
		{
			var idx = f.Indices;
			var fn = Vec3.Zero;
			for ( var i = 0; i < idx.Length; i++ )
			{
				int a = idx[i], b = idx[(i + 1) % idx.Length];
				var r = (rest[a] - rest[b]).Length;
				var w = baked[a] - baked[b];
				var ratio = r > 1e-6f ? w.Length / r : 1f;
				sum[a] += ratio; cnt[a]++; sum[b] += ratio; cnt[b]++;
				if ( ratio > bestRatio[a] ) { bestRatio[a] = ratio; bestDir[a] = w; }
				if ( ratio > bestRatio[b] ) { bestRatio[b] = ratio; bestDir[b] = w; }
				var pa = baked[a]; var pb = baked[b];
				fn += new Vec3( (pa.y - pb.y) * (pa.z + pb.z), (pa.z - pb.z) * (pa.x + pb.x), (pa.x - pb.x) * (pa.y + pb.y) );
			}
			foreach ( var i in idx ) normals[i] += fn;
		}
		var lambda = new float[n];
		var noise = new Noise3( part.Seed );
		var outp = new Vec3[n];
		for ( var i = 0; i < n; i++ )
		{
			lambda[i] = cnt[i] > 0 ? sum[i] / cnt[i] : 1f;
			var nrm = normals[i].LengthSquared > 1e-12f ? normals[i].Normal : Vec3.Zero;
			var p = baked[i];
			var l = lambda[i];
			// Stretched skin thins; its striae come after the relax, so the relax can't erase them.
			if ( l > 1.3f && nrm.LengthSquared > 0f )
				p -= nrm * MathF.Min( 0.05f, 0.02f * (l - 1f) ) * part.Scale;
			outp[i] = p;
		}
		if ( part.RelaxStretched > 0 )
		{
			var nb = MeshSmooth.Neighbours( new PolyMesh( outp, mesh.Faces ) );
			for ( var it = 0; it < part.RelaxStretched; it++ )
			{
				var next = (Vec3[])outp.Clone();
				for ( var i = 0; i < n; i++ )
				{
					// Stretched webs and crumpled compression both get relaxed.
					var over = lambda[i] > 1.5f ? (lambda[i] - 1.5f) : lambda[i] < 0.72f ? (0.72f - lambda[i]) * 2f : 0f;
					if ( over <= 0f || nb[i].Length == 0 ) continue;
					var avg = Vec3.Zero;
					foreach ( var j in nb[i] ) avg += outp[j];
					avg /= nb[i].Length;
					var k = Math.Clamp( over, 0f, 1f ) * 0.6f;
					next[i] = outp[i] + (avg - outp[i]) * k;
				}
				outp = next;
			}
		}

		if ( part.RelaxAt.Count > 0 )
		{
			var nb = MeshSmooth.Neighbours( new PolyMesh( outp, mesh.Faces ) );
			foreach ( var (c, r, iterations) in part.RelaxAt )
			{
				var wts = mesh.Positions.Select( q => SdfWarp.Falloff( (q - c).Length, r ) ).ToArray();
				for ( var it = 0; it < iterations; it++ )
				{
					var next = (Vec3[])outp.Clone();
					for ( var i = 0; i < n; i++ )
					{
						if ( wts[i] <= 0f || nb[i].Length == 0 ) continue;
						var avg = Vec3.Zero;
						foreach ( var j in nb[i] ) avg += outp[j];
						avg /= nb[i].Length;
						next[i] = outp[i] + (avg - outp[i]) * (0.5f * wts[i]);
					}
					outp = next;
				}
			}
		}

		SkinResponse( part, mesh.Faces, rest, baked, outp );
		ApplyFolds( part, mesh.Faces, outp );

		var faces = new List<Face>();
		foreach ( var f in mesh.Faces )
		{
			var l = 0f;
			var cen = Vec3.Zero;
			foreach ( var i in f.Indices ) { l += lambda[i]; cen += rest[i]; }
			l /= f.Indices.Length;
			cen /= f.Indices.Length;
			if ( l + 0.4f * noise.Fbm( cen * 3f / part.Scale, 2 ) > part.TearAt ) continue;
			faces.Add( f );
		}

		bool Ignored( int v ) => mesh.Positions[v].z < part.IgnoreEdgesBelowZ || (part.IgnoreEdge?.Invoke( mesh.Positions[v] ) ?? false);
		if ( part.CleanEdges )
			faces = CleanEdges( faces, v => Ignored( v ) && !(part.CleanIgnored?.Invoke( mesh.Positions[v] ) ?? false) );

		// Roll every open edge (tears and the cut lips): smooth the boundary along its own loop
		// to lose the stair-steps, then curl it in, so an edge reads as skin with thickness.
		var edgeUse = new Dictionary<(int, int), int>();
		foreach ( var f in faces )
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				int a = f.Indices[i], b = f.Indices[(i + 1) % f.Indices.Length];
				var key = a < b ? (a, b) : (b, a);
				edgeUse[key] = edgeUse.GetValueOrDefault( key ) + 1;
			}
		var ring = new Dictionary<int, List<int>>();
		var skirt = new List<(int, int)>();
		foreach ( var (key, uses) in edgeUse )
		{
			if ( uses != 1 ) continue;
			if ( Ignored( key.Item1 ) || Ignored( key.Item2 ) )
			{
				if ( part.SkirtEdge is null || (part.SkirtEdge( mesh.Positions[key.Item1] ) && part.SkirtEdge( mesh.Positions[key.Item2] )) ) skirt.Add( key );
				continue;
			}
			(ring.TryGetValue( key.Item1, out var la ) ? la : ring[key.Item1] = new List<int>()).Add( key.Item2 );
			(ring.TryGetValue( key.Item2, out var lb ) ? lb : ring[key.Item2] = new List<int>()).Add( key.Item1 );
		}
		for ( var it = 0; it < 6; it++ )
		{
			var next = new Dictionary<int, Vec3>();
			foreach ( var (v, nb) in ring )
			{
				if ( nb.Count != 2 ) continue;
				next[v] = outp[v] * 0.5f + (outp[nb[0]] + outp[nb[1]]) * 0.25f;
			}
			foreach ( var (v, p) in next ) outp[v] = p;
		}
		// Curl: the rim and the ring just inside it tip in toward the inside of the skin.
		var roll = 0.06f * part.Scale;
		var inner = new HashSet<int>();
		foreach ( var f in faces )
			if ( f.Indices.Any( ring.ContainsKey ) )
				foreach ( var i in f.Indices )
					if ( !ring.ContainsKey( i ) ) inner.Add( i );
		// Lips curl in toward the mouth, vermilion into mucosa; wounds roll under.
		var curl = part.Placement.TransformDirection( part.LipCurlDir ).Normal;
		bool Lip( int v ) => part.IsLipEdge is not null && part.IsLipEdge( mesh.Positions[v] );
		foreach ( var v in ring.Keys )
			if ( normals[v].LengthSquared > 1e-12f && !Lip( v ) ) outp[v] -= normals[v].Normal * roll;
		foreach ( var v in inner )
			if ( normals[v].LengthSquared > 1e-12f && !Lip( v ) ) outp[v] -= normals[v].Normal * roll * 0.35f;
		// The lip roll goes by distance from the lip edge over the skin, not by ring: a smooth
		// profile across however many vertices fit, so a dense corner doesn't fold into shards.
		var lipEdge = ring.Keys.Where( Lip ).ToList();
		if ( lipEdge.Count > 0 && part.LipRollWidth > 0f )
		{
			var g = EdgeDistance( mesh.Positions, MeshSmooth.Neighbours( new PolyMesh( mesh.Positions, faces ) ), lipEdge, part.LipRollWidth );
			for ( var v = 0; v < n; v++ )
			{
				if ( g[v] > part.LipRollWidth || normals[v].LengthSquared <= 1e-12f ) continue;
				var u = 1f - g[v] / part.LipRollWidth;
				var sm = u * u * (3f - 2f * u);
				var amt = part.LipCurlAmount?.Invoke( mesh.Positions[v] ) ?? 1f;
				outp[v] += (curl * MathF.Pow( sm, 1.5f ) - normals[v].Normal * 0.45f * sm * sm * sm) * part.LipCurl * amt * part.Scale;
			}
		}

		var positions = outp.ToList();

		// Thickness: a wall from every open edge back into the skin, so a cut shows a cut face of
		// flesh rather than the edge of a paper shell; and a rolled rim tube along the edge.
		var wallOf = new Dictionary<int, int>();
		foreach ( var v in ring.Keys.Where( v => !Lip( v ) ) )
		{
			var nrm = normals[v].LengthSquared > 1e-12f ? normals[v].Normal : Vec3.Zero;
			wallOf[v] = positions.Count;
			positions.Add( outp[v] - nrm * part.Thickness * part.Scale );
		}
		foreach ( var f in faces.ToList() )
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				int a = f.Indices[i], b = f.Indices[(i + 1) % f.Indices.Length];
				var key = a < b ? (a, b) : (b, a);
				if ( !edgeUse.TryGetValue( key, out var uses ) || uses != 1 || !wallOf.ContainsKey( a ) || !wallOf.ContainsKey( b ) ) continue;
				// Same winding sense as the face so the wall faces out of the cut.
				faces.Add( new Face( new[] { b, a, wallOf[a], wallOf[b] } ) );
			}
		foreach ( var loop in Loops( ring ).Where( l => l.Count( Lip ) < l.Count / 2 ) )
			AppendTube( positions, faces, loop.Select( v => outp[v] - (normals[v].LengthSquared > 1e-12f ? normals[v].Normal : Vec3.Zero) * part.RimRadius * part.Scale * 0.5f ).ToList(), part.RimRadius * part.Scale, closed: loop.Count > 2 && ring[loop[0]].Contains( loop[^1] ) );

		// Skirt: cut edges fitted to another part run a strip in toward its axis, so the seam
		// buries inside it instead of opening a dark gap when the warps pull the skin away.
		if ( part.SkirtTo is { } axis && skirt.Count > 0 )
		{
			var ax = part.Placement.TransformPoint( axis.A * part.Scale );
			var bx = part.Placement.TransformPoint( axis.B * part.Scale );
			var skirtOf = new Dictionary<int, int>();
			int SkirtVertex( int v )
			{
				if ( skirtOf.TryGetValue( v, out var i ) ) return i;
				var ab = bx - ax;
				var t = Math.Clamp( Vec3.Dot( outp[v] - ax, ab ) / MathF.Max( ab.LengthSquared, 1e-9f ), 0f, 1f );
				var c = ax + ab * t;
				skirtOf[v] = positions.Count;
				positions.Add( Vec3.Lerp( outp[v], c, part.SkirtDepth ) );
				return skirtOf[v];
			}
			var want = new HashSet<(int, int)>( skirt );
			foreach ( var f in faces.ToList() )
				for ( var i = 0; i < f.Indices.Length; i++ )
				{
					int a = f.Indices[i], b = f.Indices[(i + 1) % f.Indices.Length];
					if ( !want.Contains( a < b ? (a, b) : (b, a) ) ) continue;
					faces.Add( new Face( new[] { b, a, SkirtVertex( a ), SkirtVertex( b ) } ) );
				}
		}

		// Tendons across the opening.
		part.TendonRanges.Clear();
		var tendonRng = new Random( part.Seed + 5 );
		foreach ( var (a, b, r, reach) in part.Tendons )
		{
			var from = positions.Count;
			if ( Environment.GetEnvironmentVariable( "TENDON_DEBUG" ) is not null ) Console.WriteLine( $"  tendon {part.Tag}: {outp[a]} -> {outp[b]} rest {part.Mesh.Positions[a]} -> {part.Mesh.Positions[b]}" );
			Tendons.Append( positions, faces, outp[a], outp[b], r * part.Scale, reach, tendonRng );
			part.TendonRanges.Add( (from, positions.Count) );
		}

		// Strands across wounds.
		foreach ( var (a, b, r) in part.Strands )
		{
			var ia = Nearest( mesh.Positions, a );
			var ib = Nearest( mesh.Positions, b );
			var pa = outp[ia];
			var pb = outp[ib];
			var span = (pb - pa).Length;
			var pts = new List<Vec3>();
			for ( var k = 0; k <= 8; k++ )
			{
				var t = k / 8f;
				pts.Add( Vec3.Lerp( pa, pb, t ) + new Vec3( 0, 0, -span * 0.22f * 4f * t * (1f - t) ) );
			}
			AppendTube( positions, faces, pts, r * part.Scale, closed: false, taperMiddle: 0.6f );
		}

		return (positions, faces);
	}

	static int Nearest( List<Vec3> points, Vec3 p )
	{
		var best = 0;
		var bd = float.MaxValue;
		for ( var i = 0; i < points.Count; i++ )
		{
			var d = (points[i] - p).LengthSquared;
			if ( d < bd ) { bd = d; best = i; }
		}
		return best;
	}

	/// <summary>Ordered vertex loops (or chains) through a boundary adjacency.</summary>
	static List<List<int>> Loops( Dictionary<int, List<int>> ring )
	{
		var seen = new HashSet<int>();
		var loops = new List<List<int>>();
		// Chains first (start at ends), then closed loops.
		foreach ( var start in ring.Keys.OrderBy( v => ring[v].Count == 2 ? 1 : 0 ) )
		{
			if ( seen.Contains( start ) ) continue;
			var loop = new List<int> { start };
			seen.Add( start );
			var cur = start;
			while ( true )
			{
				var next = ring[cur].FirstOrDefault( n => !seen.Contains( n ), -1 );
				if ( next < 0 ) break;
				loop.Add( next );
				seen.Add( next );
				cur = next;
			}
			if ( loop.Count >= 3 ) loops.Add( loop );
		}
		return loops;
	}

	/// <summary>A tube swept along a polyline, appended to a vertex and face list.</summary>
	static void AppendTube( List<Vec3> positions, List<Face> faces, List<Vec3> path, float radius, bool closed, int sides = 6, float taperMiddle = 1f )
	{
		if ( path.Count < 2 || radius <= 0f ) return;
		var start = positions.Count;
		var n = path.Count;
		var prevX = Vec3.Zero;
		for ( var i = 0; i < n; i++ )
		{
			var a = path[closed ? (i - 1 + n) % n : Math.Max( i - 1, 0 )];
			var b = path[closed ? (i + 1) % n : Math.Min( i + 1, n - 1 )];
			var t = (b - a).LengthSquared > 1e-12f ? (b - a).Normal : new Vec3( 1, 0, 0 );
			Orthonormal( t, out var x, out var y );
			if ( i > 0 && Vec3.Dot( x, prevX ) < 0f ) { x = -x; y = -y; }
			prevX = x;
			var u = (float)i / Math.Max( n - 1, 1 );
			var r = radius * (1f - (1f - taperMiddle) * MathF.Sin( MathF.PI * u ));
			for ( var k = 0; k < sides; k++ )
			{
				var ang = MathF.PI * 2f * k / sides;
				positions.Add( path[i] + (x * MathF.Cos( ang ) + y * MathF.Sin( ang )) * r );
			}
		}
		var rings = closed ? n : n - 1;
		for ( var i = 0; i < rings; i++ )
		{
			var i2 = (i + 1) % n;
			for ( var k = 0; k < sides; k++ )
			{
				var k2 = (k + 1) % sides;
				faces.Add( new Face( new[] { start + i * sides + k, start + i * sides + k2, start + i2 * sides + k2, start + i2 * sides + k } ) );
			}
		}
	}

	/// <summary>A mesh rotated about its own origin, then scaled and moved.</summary>
	public static PolyMesh Posed( PolyMesh mesh, Xform rotation, Vec3 offset, float scale = 1f ) =>
		new( mesh.Positions.Select( p => rotation.TransformDirection( p ) * scale + offset ), mesh.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, f.Material ) ) );

	/// <summary>A mesh from a small field, at a fine voxel, smoothed: for sculpt-scale pieces
	/// like a tongue that have no source mesh.</summary>
	public static PolyMesh MeshFromField( Sdf field, float voxel, int smooth = 2 )
	{
		var m = SurfaceNets.Mesh( field, voxel ).Mesh;
		if ( smooth > 0 ) MeshSmooth.Taubin( m, smooth );
		return m;
	}

	/// <summary>The connected pieces of a mesh, as lists of face indices.</summary>
	public static List<List<int>> Components( PolyMesh mesh )
	{
		var parent = Enumerable.Range( 0, mesh.VertexCount ).ToArray();
		int Find( int x ) { while ( parent[x] != x ) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
		foreach ( var f in mesh.Faces )
			for ( var i = 1; i < f.Indices.Length; i++ )
			{
				var a = Find( f.Indices[0] );
				var b = Find( f.Indices[i] );
				if ( a != b ) parent[a] = b;
			}
		var groups = new Dictionary<int, List<int>>();
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var r = Find( mesh.Faces[fi].Indices[0] );
			(groups.TryGetValue( r, out var l ) ? l : groups[r] = new List<int>()).Add( fi );
		}
		return groups.Values.ToList();
	}

	/// <summary>A mesh of the given faces only.</summary>
	public static PolyMesh FacesOf( PolyMesh mesh, IEnumerable<int> faces )
	{
		var set = new HashSet<int>( faces );
		var i = 0;
		return KeepFacesN( mesh, ( _, _ ) => set.Contains( i++ ) );
	}

	/// <summary>As KeepFaces, but the predicate also sees each face's unit normal.</summary>
	public static PolyMesh KeepFacesN( PolyMesh mesh, Func<Vec3, Vec3, bool> keep )
	{
		var normal = new Dictionary<Face, Vec3>();
		foreach ( var f in mesh.Faces )
		{
			var n = Vec3.Zero;
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				var a = mesh.Positions[f.Indices[i]];
				var b = mesh.Positions[f.Indices[(i + 1) % f.Indices.Length]];
				n += new Vec3( (a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y) );
			}
			normal[f] = n.LengthSquared > 1e-12f ? n.Normal : Vec3.Zero;
		}
		var sub = new PolyMesh( mesh.Positions, mesh.Faces.Where( f =>
		{
			var c = Vec3.Zero;
			foreach ( var i in f.Indices ) c += mesh.Positions[i];
			return keep( c / f.Indices.Length, normal[f] );
		} ) );
		return KeepFaces( sub, _ => true );
	}

	/// <summary>A copy of a mesh keeping only the faces the predicate keeps (by face centroid),
	/// with unused vertices dropped.</summary>
	public static PolyMesh KeepFaces( PolyMesh mesh, Func<Vec3, bool> keep )
	{
		var map = new Dictionary<int, int>();
		var positions = new List<Vec3>();
		var faces = new List<Face>();
		foreach ( var f in mesh.Faces )
		{
			var c = Vec3.Zero;
			foreach ( var i in f.Indices ) c += mesh.Positions[i];
			c /= f.Indices.Length;
			if ( !keep( c ) ) continue;
			var idx = new int[f.Indices.Length];
			for ( var k = 0; k < idx.Length; k++ )
			{
				if ( !map.TryGetValue( f.Indices[k], out var ni ) )
				{
					ni = positions.Count;
					positions.Add( mesh.Positions[f.Indices[k]] );
					map[f.Indices[k]] = ni;
				}
				idx[k] = ni;
			}
			faces.Add( new Face( idx, null, f.Material ) );
		}
		return new PolyMesh( positions, faces );
	}

	/// <summary>A copy of a mesh moved into another part's space: scaled, then translated.</summary>
	public static PolyMesh Moved( PolyMesh mesh, Vec3 offset, float scale = 1f ) =>
		new( mesh.Positions.Select( p => p * scale + offset ), mesh.Faces.Select( f => new Face( (int[])f.Indices.Clone(), null, f.Material ) ) );

	/// <summary>A closed ellipsoid mesh (for a mouth bag or an eye socket's dark).</summary>
	public static PolyMesh EllipsoidMesh( Vec3 center, Vec3 radii, int rings = 10, int segments = 16 )
	{
		var positions = new List<Vec3>();
		var faces = new List<Face>();
		for ( var r = 0; r <= rings; r++ )
		{
			var phi = MathF.PI * r / rings;
			for ( var s = 0; s < segments; s++ )
			{
				var th = MathF.PI * 2f * s / segments;
				positions.Add( center + new Vec3( radii.x * MathF.Sin( phi ) * MathF.Cos( th ), radii.y * MathF.Sin( phi ) * MathF.Sin( th ), radii.z * MathF.Cos( phi ) ) );
			}
		}
		for ( var r = 0; r < rings; r++ )
			for ( var s = 0; s < segments; s++ )
			{
				var a = r * segments + s;
				var b = r * segments + (s + 1) % segments;
				var c = (r + 1) * segments + (s + 1) % segments;
				var d = (r + 1) * segments + s;
				faces.Add( new Face( new[] { a, d, c, b } ) );
			}
		return new PolyMesh( positions, faces );
	}

	// --- helpers for building creatures ---------------------------------------------------------

	public static float Rand( Random rng, float a, float b ) => a + (float)rng.NextDouble() * (b - a);

	public static Vec3 RandDir( Random rng )
	{
		while ( true )
		{
			var v = new Vec3( Rand( rng, -1, 1 ), Rand( rng, -1, 1 ), Rand( rng, -1, 1 ) );
			if ( v.LengthSquared is > 0.01f and < 1f )
				return v.Normal;
		}
	}

	public static void Orthonormal( Vec3 d, out Vec3 x, out Vec3 y )
	{
		d = d.Normal;
		var helper = MathF.Abs( d.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		x = Vec3.Cross( d, helper ).Normal;
		y = Vec3.Cross( d, x ).Normal;
	}

	public static Vec3 Rotate( Vec3 v, Vec3 axis, float degrees ) =>
		Xform.Rotate( axis, degrees * MathF.PI / 180f ).TransformDirection( v );

	/// <summary>
	/// A tendril: a tube that starts along <paramref name="direction"/>, wanders with
	/// <paramref name="curl"/>, sags under <paramref name="gravity"/> and tapers to
	/// <paramref name="tipRadius"/>. Lumpy along its length when <paramref name="beads"/> is set.
	/// </summary>
	public static SdfTube Tendril( Random rng, Vec3 start, Vec3 direction, float length, float rootRadius, float tipRadius,
		int segments = 12, float curl = 0.25f, float gravity = 0.15f, float beads = 0f, float taperPower = 1f, Vec3? floor = null )
	{
		var points = new List<Vec3>();
		var radii = new List<float>();
		var p = start;
		var d = direction.Normal;
		var step = length / segments;

		for ( var i = 0; i <= segments; i++ )
		{
			var t = (float)i / segments;
			points.Add( p );
			var r = rootRadius + (tipRadius - rootRadius) * MathF.Pow( t, taperPower );
			if ( beads > 0f )
				r *= 1f + beads * MathF.Sin( t * segments * 1.7f + rng.Next( 3 ) );
			radii.Add( MathF.Max( r, 0.05f ) );

			d = (d + RandDir( rng ) * curl + new Vec3( 0, 0, -gravity * t )).Normal;
			p += d * step;

			if ( floor is { } f && p.z < f.z + r )
			{
				p = new Vec3( p.x, p.y, f.z + r * 0.6f );
				d = new Vec3( d.x, d.y, 0f ).Normal;
			}
		}

		return new SdfTube( points, radii );
	}

	/// <summary>A clump of overlapping spheres around a centre: a tumour, a grape-cluster, a lump.</summary>
	public static void Lumps( SdfBlend into, Random rng, Vec3 center, float spread, int count, float minR, float maxR, int material, int bone, string tag )
	{
		for ( var i = 0; i < count; i++ )
		{
			var r = Rand( rng, minR, maxR );
			var c = center + RandDir( rng ) * Rand( rng, 0f, spread );
			into.Add( new SdfSphere( c, r ) { Material = material, Bone = bone, Tag = tag } );
		}
	}

	// --- creatures ------------------------------------------------------------------------------

	/// <summary>Two spheres, a capsule and a tendril: the mesher's own smoke test.</summary>
	static Sdf Smoke( Options o )
	{
		var rng = new Random( o.Seed );
		var body = new SdfBlend( 3f );
		body.Add( new SdfSphere( new Vec3( 0, 0, 12 ), 10f ) { Material = FleshMat, Tag = "mass" } );
		body.Add( new SdfSphere( new Vec3( 8, 4, 20 ), 6f ) { Material = SkinMat, Tag = "head" } );
		body.Add( new SdfCapsule( new Vec3( -4, -4, 2 ), new Vec3( -14, -10, 0 ), 2.5f ) { Material = ClothMat, Tag = "leg" } );
		body.Add( new SdfRoundCone( new Vec3( 6, -6, 10 ), 2.5f, new Vec3( 18, -12, 4 ), 0.8f ) { Material = SkinMat, Tag = "arm" } );

		for ( var i = 0; i < 6; i++ )
			body.Add( Tendril( rng, new Vec3( -2, 2, 20 ), new Vec3( -0.5f, 0.3f, 1f ), 22f, 1.2f, 0.2f, beads: 0.2f ).With( FleshMat, "tendril" ) );

		var cut = new SdfSubtract( body, 1.5f );
		cut.Add( new SdfEllipsoid( new Vec3( 9, 0, 11 ), new Vec3( 5, 3, 2 ) ) { Material = CavityMat, Tag = "mouth" } );

		return new SdfDisplace( cut, 0.6f, 0.35f, 3, o.Seed, warp: 0.3f );
	}

	public static T With<T>( this T sdf, int material, string tag, int bone = -1 ) where T : Sdf
	{
		sdf.Material = material;
		sdf.Tag = tag;
		sdf.Bone = bone;
		return sdf;
	}
}
