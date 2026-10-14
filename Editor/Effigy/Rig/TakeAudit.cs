using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// Sign-off for an animated take: play a clip on a dumped model, between frames as well as on
/// them, and count where the skin passes through the props, through its own other parts, and
/// through itself; how deep; and how close it comes to each prop.
///
/// Everything a take check used to need Blender for - an armature-deformed mesh and a BVH
/// overlap test - is kernel code here, so it runs wherever the tests run and gives the same
/// answer every time.
///
/// CHECKED BETWEEN FRAMES. A finger can pass through a button between two keyed frames that are
/// both clear; the engine plays the in-between, so the check samples it too
/// (<see cref="Options.Substeps"/> per frame).
///
/// BASELINE FIRST. Plenty of models cross themselves in their bind pose - a cuff tucked into a
/// sleeve, a thumb modelled into the palm. Those are the model's, not the take's, so the bind
/// pose is checked once and reported as the baseline; frames are flagged only for crossings above
/// it.
/// </summary>
public sealed class TakeAudit
{
	public sealed class Options
	{
		/// <summary>Samples per frame: 4 checks every quarter frame.</summary>
		public int Substeps = 4;

		/// <summary>Check each skinned part against itself.</summary>
		public bool Self = true;

		/// <summary>Check skinned parts against each other (glove against sleeve).</summary>
		public bool PartPairs = true;

		/// <summary>Check skinned parts against the dumped props.</summary>
		public bool Props = true;

		/// <summary>
		/// For checks between two meshes: a sample only fails when its deepest new crossing goes
		/// further than this. Zero fails any new crossing. The take research's sign-off line is
		/// 0.5-1 cm, about 0.2-0.4 units. Self checks have no depth and ignore it.
		/// </summary>
		public float DepthTolerance;

		/// <summary>Only report the gap to a prop when it is nearer than this.</summary>
		public float GapRange = 8f;

		/// <summary>First and last frame to check; null for the clip's own span.</summary>
		public float? From, To;
	}

	/// <summary>One check (a part against itself, a part against a part, a part against a prop)
	/// over the whole take.</summary>
	public sealed class Check
	{
		public string Name = "";

		/// <summary>A part against itself, which has crossings but no depth.</summary>
		public bool IsSelf;

		/// <summary>Triangle pairs that already cross at the bind pose. A sample only counts the
		/// pairs NOT in here, so a cuff modelled into its sleeve, or two rigid pieces bolted
		/// through each other, never flags a frame however they jitter.</summary>
		public HashSet<(int, int)> BaselinePairs = new();
		public int Baseline => BaselinePairs.Count;

		/// <summary>The most new crossing pairs in any one sample.</summary>
		public int WorstCrossings;
		public float WorstFrame = -1;
		public float WorstDepth;
		public float WorstDepthFrame = -1;

		/// <summary>Sampled frames with crossings the bind pose does not have.</summary>
		public List<float> FramesOver = new();

		/// <summary>The closest approach, for a check against a prop; float.MaxValue when it never
		/// came within range.</summary>
		public float NearestGap = float.MaxValue;
		public float NearestGapFrame = -1;

		public bool Clear => FramesOver.Count == 0;
	}

	public sealed class Result
	{
		public List<Check> Checks = new();
		public List<string> MissingBones = new();
		public int Samples;
		public float From, To;

		public bool Clear => Checks.All( c => c.Clear );

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.AppendLine( Invariant( $"take: frames {From:0.##}..{To:0.##}, {Samples} samples — {(Clear ? "CLEAR" : "CROSSINGS")}" ) );

			if ( MissingBones.Count > 0 )
				sb.AppendLine( $"  tracks with no bone in the model (ignored): {string.Join( ", ", MissingBones )}" );

			foreach ( var c in Checks )
			{
				var line = c.Clear
					? $"  ok    {c.Name}"
					: Invariant( $"  CROSS {c.Name}: {c.FramesOver.Count} sample(s), worst {c.WorstCrossings} new crossing(s) at frame {c.WorstFrame:0.##}" )
						+ (c.WorstDepthFrame >= 0 ? Invariant( $", deepest {c.WorstDepth:0.###} at frame {c.WorstDepthFrame:0.##}" ) : "")
						+ $" ({Ranges( c.FramesOver )})";

				if ( c.Baseline > 0 )
					line += $" [{c.Baseline} already crossing at bind, not counted]";

				if ( c.NearestGap < float.MaxValue )
					line += Invariant( $"; nearest {c.NearestGap:0.###} at frame {c.NearestGapFrame:0.##}" );

				sb.AppendLine( line );
			}

			return sb.ToString().TrimEnd();
		}

		static string Ranges( List<float> frames )
		{
			if ( frames.Count == 0 ) return "";
			var parts = new List<string>();
			var start = frames[0];
			var prev = frames[0];

			for ( var i = 1; i <= frames.Count; i++ )
			{
				if ( i < frames.Count && frames[i] - prev <= 1.0001f )
				{
					prev = frames[i];
					continue;
				}

				parts.Add( start == prev ? Invariant( $"{start:0.##}" ) : Invariant( $"{start:0.##}-{prev:0.##}" ) );

				if ( i < frames.Count )
					start = prev = frames[i];
			}

			return "frames " + string.Join( ", ", parts );
		}
	}

	public readonly ModelDump Dump;
	public readonly RigAnimFile Clip;

	public TakeAudit( ModelDump dump, RigAnimFile clip )
	{
		Dump = dump ?? throw new ArgumentNullException( nameof( dump ) );
		Clip = clip ?? throw new ArgumentNullException( nameof( clip ) );
	}

	/// <summary>Every bone in model space at a (fractional) frame.</summary>
	public Xform[] Pose( float frame ) => RigAnimFile.ModelSpace( Dump.Skeleton, Clip.SampleLocal( Dump.Skeleton, frame ) );

	/// <summary>Every skinned part's vertices at a frame.</summary>
	public Vec3[][] Skin( float frame )
	{
		var pose = Pose( frame );
		return Dump.Meshes.Select( m => Dump.Deform( m, pose ) ).ToArray();
	}

	public Result Run( Options options = null )
	{
		options ??= new Options();
		var result = new Result
		{
			From = options.From ?? 0f,
			To = options.To ?? Clip.LastFrame,
		};

		result.MissingBones = Clip.BoneTracks.Where( t => t.Keys.Count > 0 && Dump.Skeleton.IndexOf( t.Name ) < 0 ).Select( t => t.Name ).ToList();

		// Trees built once on the bind pose and refitted per sample.
		var parts = Dump.Meshes.Select( m => TriangleSet.From( m.Positions, m.Triangles ) ).ToList();
		var props = Dump.Props.SelectMany( p => p.Meshes.Select( m => (Name: $"{p.Name}/{m.Name}", Set: TriangleSet.From( p.Placed( m ), m.Triangles )) ) ).ToList();

		var checks = new List<(Check Check, Func<MeshClash.Report> Run, Func<MeshClash.Gap?> Gap)>();

		for ( var i = 0; i < parts.Count; i++ )
		{
			var a = parts[i];
			var nameA = PartName( i );

			if ( options.Self )
				checks.Add( (new Check { Name = $"{nameA} x itself", IsSelf = true }, () => MeshClash.Self( a, int.MaxValue ), null) );

			if ( options.PartPairs )
				for ( var j = i + 1; j < parts.Count; j++ )
				{
					var b = parts[j];
					checks.Add( (new Check { Name = $"{nameA} x {PartName( j )}" }, () => MeshClash.Between( a, b, int.MaxValue ), null) );
				}

			if ( options.Props )
				foreach ( var (propName, set) in props )
				{
					var range = options.GapRange;
					checks.Add( (new Check { Name = $"{nameA} x {propName}" }, () => MeshClash.Between( a, set, int.MaxValue ), () => MeshClash.Nearest( a, set, range )) );
				}
		}

		result.Checks = checks.Select( c => c.Check ).ToList();

		// Baseline: the bind pose.
		System.Threading.Tasks.Parallel.ForEach( checks, c =>
		{
			foreach ( var pair in c.Run().Pairs )
				c.Check.BaselinePairs.Add( (pair.TriangleA, pair.TriangleB) );
		} );

		var step = 1f / Math.Max( 1, options.Substeps );

		for ( var frame = result.From; frame <= result.To + 1e-4f; frame += step )
		{
			var skin = Skin( frame );

			for ( var i = 0; i < parts.Count; i++ )
				parts[i].Refit( skin[i] );

			result.Samples++;

			// The checks only read the trees, and each writes only its own Check: run them at once.
			System.Threading.Tasks.Parallel.ForEach( checks, c =>
			{
				var (check, run, gap) = c;
				var report = run();

				var fresh = report.Pairs.Count( p => !check.BaselinePairs.Contains( (p.TriangleA, p.TriangleB) ) );

				// Self checks have no depth, so the tolerance cannot pass them; any new self crossing counts.
				var fails = fresh > 0 && (check.IsSelf || options.DepthTolerance <= 0f || report.Depth > options.DepthTolerance);

				if ( fails )
				{
					check.FramesOver.Add( frame );

					if ( fresh > check.WorstCrossings )
					{
						check.WorstCrossings = fresh;
						check.WorstFrame = frame;
					}

					if ( report.Depth > check.WorstDepth )
					{
						check.WorstDepth = report.Depth;
						check.WorstDepthFrame = frame;
					}
				}

				if ( gap?.Invoke() is { Found: true } g && g.Distance < check.NearestGap )
				{
					check.NearestGap = g.Distance;
					check.NearestGapFrame = frame;
				}
			} );
		}

		return result;
	}

	/// <summary>
	/// The take from a camera at a frame: skinned parts and props, each part in its own colour.
	/// With no camera, the model's own origin looking down +x - where a first-person view model's
	/// eye is.
	/// </summary>
	public Raster Still( float frame, EyeCamera camera = null, int width = 640, int height = 360 )
	{
		camera ??= EyeCamera.At( Xform.Identity );
		var skin = Skin( frame );
		var items = new List<EyeItem>();

		for ( var i = 0; i < Dump.Meshes.Count; i++ )
			items.Add( new EyeItem( skin[i], Dump.Meshes[i].Triangles, EyeRender.PartColour( i ) ) );

		var colour = Dump.Meshes.Count;
		foreach ( var prop in Dump.Props )
			foreach ( var m in prop.Meshes )
				items.Add( new EyeItem( prop.Placed( m ), m.Triangles, EyeRender.PartColour( colour++ ) ) );

		return EyeRender.Render( items, camera, width, height );
	}

	/// <summary>A camera on a bone at a frame: x forward, z up, the s&box convention.</summary>
	public EyeCamera BoneCamera( string bone, float frame, float fieldOfView = 80f )
	{
		var index = Dump.Skeleton.IndexOf( bone );
		return index < 0 ? null : EyeCamera.At( Pose( frame )[index], fieldOfView );
	}

	string PartName( int i ) => string.IsNullOrEmpty( Dump.Meshes[i].Name ) ? $"part{i}" : Dump.Meshes[i].Name;

	static string Invariant( FormattableString s ) => s.ToString( CultureInfo.InvariantCulture );
}
