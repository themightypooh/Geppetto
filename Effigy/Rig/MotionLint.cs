using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// A Marionette clip played headlessly and checked for the mistakes that read as broken motion -
/// the things an animator catches by eye on the fifth viewing, caught by numbers on the first:
///
///   - POPS: a bone that turns or moves far more in one frame than in the frames around it. A key
///     flipped to the far side of a rotation, a Stepped key left in by accident, a solver snapping.
///   - SLIDING CONTACTS: a hand or foot that should be planted and drifts. Planted means its IK goal
///     holds still for a span, or - for any bone named in <see cref="Options.Contacts"/> - that it
///     nearly stops; either way it should not travel while it stays put.
///   - UNREACHED GOALS: an IK goal the chain cannot get its end bone onto.
///   - STRETCH: a bone whose length changes without a keyed scale saying so.
///   - DRIFT IN A HOLD: two keys with the same pose and a Cubic segment between them, which
///     overshoots and wanders through what was meant to be a hold.
///   - THE LOOP SEAM: with <see cref="Options.Loop"/>, the last frame should flow into the first,
///     in pose and in speed.
///   - TWINS: two of the <see cref="Options.Chains"/> moving in lockstep - same rhythm, same
///     moment. Creature limbs read as mechanical the moment two of them match; offset them.
///   - OVERLAP ORDER: for each chain named in <see cref="Options.Chains"/>, the frame each bone hits
///     its peak speed, root to tip. A whip or a tentacle should peak later toward the tip.
///
/// It plays the clip exactly as the take check and the export do (<see cref="RigAnimFile.SampleLocal"/>,
/// live IK and goal spaces included), so what it reports is what the game will show.
/// </summary>
public sealed class MotionLint
{
	public sealed class Options
	{
		/// <summary>Samples per frame. 1 checks keyed frames only; more catches what happens between.</summary>
		public int Substeps = 2;

		public float? From, To;

		/// <summary>Bones to treat as contacts even without an IK goal - feet, a dragged hand. Globs.</summary>
		public List<string> Contacts = new();

		/// <summary>Chains to report overlap order for, as name prefixes ("cable1_") or globs.</summary>
		public List<string> Chains = new();

		public bool Loop;

		/// <summary>A pop: a bone turns more than this many degrees in one frame...</summary>
		public float PopDegrees = 25f;

		/// <summary>...and more than this many times its typical per-frame turn around that moment.</summary>
		public float PopRatio = 4f;

		/// <summary>Planted contacts may drift this far (units) before it counts as sliding.</summary>
		public float SlideTolerance = 0.25f;

		/// <summary>An IK end this far from its goal is not reaching it.</summary>
		public float ReachTolerance = 0.25f;

		/// <summary>A bone length may change by this fraction before it counts as stretching.</summary>
		public float StretchTolerance = 0.02f;

		/// <summary>A goal that moves less than this per frame is holding still.</summary>
		public float PlantSpeed = 0.05f;

		/// <summary>Bones left out of pop, stretch and loop checks: rig helpers that are not skin -
		/// IK targets, ik rules, hold points. Globs. Clear it to check everything.</summary>
		public List<string> Ignore = new( DefaultHelpers );
	}

	public sealed class Finding
	{
		public string Kind;
		public string Bone;
		public float Frame;
		public float Amount;
		public string Detail;
		public bool Fail = true;

		public override string ToString() => string.Create( CultureInfo.InvariantCulture,
			$"  {(Fail ? "FAIL" : "note")}  {Kind,-8} {Bone} @ {Frame:0.##}: {Detail}" );
	}

	public sealed class Result
	{
		public int Frames;
		public float Fps;
		public List<Finding> Findings = new();
		public List<string> Notes = new();

		public bool Clear => Findings.All( f => !f.Fail );

		public IEnumerable<Finding> Of( string kind ) => Findings.Where( f => f.Kind == kind );

		public override string ToString()
		{
			var text = new StringBuilder();
			text.AppendLine( string.Create( CultureInfo.InvariantCulture, $"motion: {Frames} frame(s) at {Fps} fps" ) );

			foreach ( var note in Notes )
				text.AppendLine( $"  {note}" );

			foreach ( var group in Findings.GroupBy( f => f.Kind ) )
			{
				var list = group.ToList();
				foreach ( var f in list.Take( 12 ) )
					text.AppendLine( f.ToString() );
				if ( list.Count > 12 )
					text.AppendLine( $"  ... and {list.Count - 12} more {group.Key}" );
			}

			var fails = Findings.Count( f => f.Fail );
			text.Append( fails == 0 ? "clean" : $"{fails} problem(s)" );
			return text.ToString();
		}
	}

	/// <summary>Name patterns of rig helper bones - targets and attachment points that move however
	/// the rig likes and carry no skin.</summary>
	public static readonly string[] DefaultHelpers = { "*ikrule*", "*IK_target*", "*_ik_*", "*_IK_*", "hold_*", "*_attach*", "*camera*" };

	/// <summary>Whether <paramref name="bone"/> matches any of <paramref name="patterns"/>.</summary>
	public static bool Matches( string bone, IEnumerable<string> patterns ) => patterns.Any( p => Glob( bone, p ) );

	public readonly Skeleton Skeleton;
	public readonly RigAnimFile Clip;

	public MotionLint( Skeleton skeleton, RigAnimFile clip )
	{
		Skeleton = skeleton ?? throw new ArgumentNullException( nameof( skeleton ) );
		Clip = clip ?? throw new ArgumentNullException( nameof( clip ) );
	}

	public Xform[] Pose( float frame ) => RigAnimFile.ModelSpace( Skeleton, Clip.SampleLocal( Skeleton, frame ) );

	public Result Run( Options options = null )
	{
		options ??= new Options();
		var result = new Result { Fps = Clip.FrameRate };

		var from = Math.Max( options.From ?? 0f, 0f );
		var to = Math.Min( options.To ?? Clip.LastFrame, Clip.LastFrame );
		var steps = Math.Max( options.Substeps, 1 );
		var count = Math.Max( (int)MathF.Round( (to - from) * steps ), 0 ) + 1;
		var times = Enumerable.Range( 0, count ).Select( i => from + i / (float)steps ).ToArray();
		var poses = times.Select( Pose ).ToArray();
		result.Frames = (int)MathF.Round( to - from ) + 1;

		if ( count < 2 )
		{
			result.Notes.Add( "the clip is a single pose - nothing moves to check" );
			return result;
		}

		CheckFinite( poses, times, result );
		CheckPops( poses, times, steps, options, result );
		CheckStretch( poses, times, options, result );
		CheckGoals( poses, times, steps, options, result );
		CheckContacts( poses, times, steps, options, result );
		CheckHolds( result );

		if ( options.Loop )
			CheckLoop( options, result );

		foreach ( var chain in options.Chains )
			ReportOverlap( chain, poses, times, result );

		if ( options.Chains.Count > 1 )
			CheckTwins( options, poses, times, result );

		return result;
	}

	// --- checks ---------------------------------------------------------------------------------

	void CheckFinite( Xform[][] poses, float[] times, Result result )
	{
		for ( var i = 0; i < poses.Length; i++ )
		{
			for ( var b = 0; b < Skeleton.Count; b++ )
			{
				var o = poses[i][b].Origin;

				if ( float.IsFinite( o.x ) && float.IsFinite( o.y ) && float.IsFinite( o.z ) && float.IsFinite( poses[i][b].X.x ) )
					continue;

				result.Findings.Add( new Finding { Kind = "nan", Bone = Name( b ), Frame = times[i], Detail = "the pose is not a number - a degenerate key or solve" } );
				return;
			}
		}
	}

	void CheckPops( Xform[][] poses, float[] times, int steps, Options options, Result result )
	{
		var n = poses.Length;

		for ( var b = 0; b < Skeleton.Count; b++ )
		{
			if ( Matches( Name( b ), options.Ignore ) )
				continue;

			// Degrees per FRAME between consecutive samples, relative to the parent so a turning
			// body does not make every child look like it popped.
			var parent = Skeleton.Bones[b].Parent;
			var turn = new float[n - 1];

			for ( var i = 0; i < n - 1; i++ )
			{
				var a = Relative( poses[i], b, parent );
				var c = Relative( poses[i + 1], b, parent );
				turn[i] = Angle( a, c ) * steps;
			}

			var reported = -10;

			for ( var i = 0; i < turn.Length; i++ )
			{
				if ( turn[i] < options.PopDegrees || i - reported < 4 * steps )
					continue;

				// Typical turn around here: the median of a window, this sample left out.
				var window = Enumerable.Range( Math.Max( 0, i - 3 * steps ), Math.Min( turn.Length, i + 3 * steps + 1 ) - Math.Max( 0, i - 3 * steps ) )
					.Where( j => j != i ).Select( j => turn[j] ).OrderBy( v => v ).ToList();
				var typical = window.Count > 0 ? window[window.Count / 2] : 0f;

				if ( turn[i] < options.PopRatio * MathF.Max( typical, 1f ) )
					continue;

				reported = i;
				result.Findings.Add( new Finding
				{
					Kind = "pop", Bone = Name( b ), Frame = times[i], Amount = turn[i],
					Detail = string.Create( CultureInfo.InvariantCulture, $"turns {turn[i]:0}°/frame against {typical:0.#}°/frame around it" ),
				} );
			}
		}
	}

	void CheckStretch( Xform[][] poses, float[] times, Options options, Result result )
	{
		var bind = RigAnimFile.ModelSpace( Skeleton, Skeleton.Bones.Select( x => x.Local ).ToArray() );

		for ( var b = 0; b < Skeleton.Count; b++ )
		{
			var parent = Skeleton.Bones[b].Parent;

			if ( parent < 0 )
				continue;

			var rest = (bind[b].Origin - bind[parent].Origin).Length;

			if ( Matches( Name( b ), options.Ignore ) )
				continue;

			if ( rest < 1e-3f || KeysScale( Name( b ) ) || KeysScale( Name( parent ) ) || KeysPosition( Name( b ) ) )
				continue;

			var worst = 0f;
			var at = 0f;

			for ( var i = 0; i < poses.Length; i++ )
			{
				var change = MathF.Abs( (poses[i][b].Origin - poses[i][parent].Origin).Length / rest - 1f );

				if ( change > worst ) { worst = change; at = times[i]; }
			}

			if ( worst > options.StretchTolerance )
			{
				result.Findings.Add( new Finding
				{
					Kind = "stretch", Bone = Name( b ), Frame = at, Amount = worst,
					Detail = string.Create( CultureInfo.InvariantCulture, $"its length changes {worst * 100f:0.#}% with no scale or position key saying so" ),
				} );
			}
		}
	}

	void CheckGoals( Xform[][] poses, float[] times, int steps, Options options, Result result )
	{
		foreach ( var goal in Clip.IkGoalTracks )
		{
			var bone = Skeleton.IndexOf( goal.Name );

			if ( bone < 0 )
			{
				result.Notes.Add( $"IK goal '{goal.Name}' names a bone this model does not have" );
				continue;
			}

			// Only where the clip means the goal: a keyed IK weight under 1 lets the chain go (a free
			// cable on its sim), and a hand that is not held is neither reaching nor planted.
			var targets = times.Select( t => Clip.IkWeightAt( goal.Name, t ) >= 0.999f ? Clip.IkGoalAt( goal.Name, t )?.Position : null ).ToArray();
			var worst = 0f;
			var worstAt = 0f;

			for ( var i = 0; i < poses.Length; i++ )
			{
				if ( targets[i] is not { } target )
					continue;

				var miss = (poses[i][bone].Origin - target).Length;

				if ( miss > worst ) { worst = miss; worstAt = times[i]; }
			}

			if ( worst > options.ReachTolerance )
			{
				result.Findings.Add( new Finding
				{
					Kind = "reach", Bone = goal.Name, Frame = worstAt, Amount = worst,
					Detail = string.Create( CultureInfo.InvariantCulture, $"ends {worst:0.##} units from its goal - out of the chain's reach, or the chain is too short" ),
				} );
			}

			// Planted spans: the goal holds still. The end bone should hold with it.
			foreach ( var (start, end) in Spans( targets.Select( t => t ?? Vec3.Zero ).ToArray(), steps, options.PlantSpeed ) )
				if ( Enumerable.Range( start, end - start + 1 ).All( i => targets[i] is not null ) )
					SlideIn( bone, poses, times, start, end, options, result, "goal held" );
		}
	}

	void CheckContacts( Xform[][] poses, float[] times, int steps, Options options, Result result )
	{
		if ( options.Contacts.Count == 0 )
			return;

		var goalBones = Clip.IkGoalTracks.Select( g => g.Name ).ToHashSet();

		for ( var b = 0; b < Skeleton.Count; b++ )
		{
			var name = Name( b );

			if ( goalBones.Contains( name ) || !options.Contacts.Any( c => Glob( name, c ) ) )
				continue;

			// Planted: slow enough to be standing on something. Slide is how far it travels while so.
			var path = poses.Select( p => p[b].Origin ).ToArray();

			foreach ( var (start, end) in Spans( path, steps, options.PlantSpeed * 4f ) )
				SlideIn( b, poses, times, start, end, options, result, "planted" );
		}
	}

	void SlideIn( int bone, Xform[][] poses, float[] times, int start, int end, Options options, Result result, string why )
	{
		var anchor = poses[start][bone].Origin;
		var worst = 0f;

		for ( var i = start; i <= end; i++ )
			worst = MathF.Max( worst, (poses[i][bone].Origin - anchor).Length );

		if ( worst <= options.SlideTolerance )
			return;

		result.Findings.Add( new Finding
		{
			Kind = "slide", Bone = Name( bone ), Frame = times[start], Amount = worst,
			Detail = string.Create( CultureInfo.InvariantCulture, $"{why} {times[start]:0.#}-{times[end]:0.#} but drifts {worst:0.##} units" ),
		} );
	}

	void CheckHolds( Result result )
	{
		foreach ( var track in Clip.Tracks.Where( t => t.Kind is RigTrackKind.Bone or RigTrackKind.IkGoal ) )
		{
			for ( var k = 0; k + 1 < track.Keys.Count; k++ )
			{
				var a = track.Keys[k];
				var b = track.Keys[k + 1];

				if ( a.Ease is not (RigEase.Cubic) || b.Frame - a.Frame < 2 || !Same( a.Local, b.Local ) )
					continue;

				var worst = 0f;
				var at = 0f;

				for ( var f = a.Frame + 1; f < b.Frame; f++ )
				{
					var p = track.Evaluate( f );
					var d = (p.Position - a.Local.Position).Length + Angle( p.Rotation, a.Local.Rotation ) * 0.05f;
					if ( d > worst ) { worst = d; at = f; }
				}

				if ( worst > 0.05f )
				{
					result.Findings.Add( new Finding
					{
						Kind = "hold", Bone = track.Name, Frame = at, Amount = worst,
						Detail = $"keys {a.Frame} and {b.Frame} are the same pose but the Cubic curve wanders between them - make key {a.Frame} Monotone or Stepped",
					} );
				}
			}
		}
	}

	void CheckLoop( Options options, Result result )
	{
		var first = 0f;
		var last = (float)Clip.LastFrame;

		if ( last < 2f )
			return;

		var p0 = Pose( first );
		var pn = Pose( last );
		var v0a = Pose( first + 1f );
		var vna = Pose( last - 1f );
		var worstPose = 0f;
		var worstSpeed = 0f;
		string poseBone = null, speedBone = null;

		for ( var b = 0; b < Skeleton.Count; b++ )
		{
			if ( Matches( Name( b ), options.Ignore ) )
				continue;

			var d = (p0[b].Origin - pn[b].Origin).Length + Angle( Quat.FromXform( p0[b] ), Quat.FromXform( pn[b] ) ) * 0.05f;
			if ( d > worstPose ) { worstPose = d; poseBone = Name( b ); }

			// Speed leaving frame 0 against speed arriving at the last frame.
			var leave = v0a[b].Origin - p0[b].Origin;
			var arrive = pn[b].Origin - vna[b].Origin;
			var s = (leave - arrive).Length;
			if ( s > worstSpeed ) { worstSpeed = s; speedBone = Name( b ); }
		}

		if ( worstPose > options.SlideTolerance )
		{
			result.Findings.Add( new Finding
			{
				Kind = "loop", Bone = poseBone, Frame = last, Amount = worstPose,
				Detail = string.Create( CultureInfo.InvariantCulture, $"the last frame is {worstPose:0.##} away from the first - the loop jumps" ),
			} );
		}

		if ( worstSpeed > options.SlideTolerance * 2f )
		{
			result.Findings.Add( new Finding
			{
				Kind = "loop", Bone = speedBone, Frame = last, Amount = worstSpeed, Fail = false,
				Detail = string.Create( CultureInfo.InvariantCulture, $"arrives at the seam moving differently from how it leaves ({worstSpeed:0.##} units/frame) - the loop hitches" ),
			} );
		}
	}

	void ReportOverlap( string chain, Xform[][] poses, float[] times, Result result )
	{
		var bones = Enumerable.Range( 0, Skeleton.Count )
			.Where( b => Glob( Name( b ), chain.Contains( '*' ) ? chain : chain + "*" ) )
			.ToList();

		if ( bones.Count < 2 )
		{
			result.Notes.Add( $"chain '{chain}': fewer than two bones match" );
			return;
		}

		// Skeleton order is parents first, so a chain's bones come out root to tip.
		var peaks = new List<(string Bone, float Frame)>();

		foreach ( var b in bones )
		{
			var parent = Skeleton.Bones[b].Parent;
			var best = 0f;
			var at = times[0];

			for ( var i = 0; i + 1 < poses.Length; i++ )
			{
				var turn = Angle( Relative( poses[i], b, parent ), Relative( poses[i + 1], b, parent ) );
				if ( turn > best ) { best = turn; at = times[i]; }
			}

			if ( best > 0.01f )
				peaks.Add( (Name( b ), at) );
		}

		var inOrder = peaks.Zip( peaks.Skip( 1 ) ).Count( p => p.Second.Frame >= p.First.Frame );
		result.Notes.Add( string.Create( CultureInfo.InvariantCulture,
			$"chain '{chain}' peak turn by bone (root→tip): {string.Join( " ", peaks.Select( p => $"{p.Bone}@{p.Frame:0.#}" ) )} - {inOrder}/{Math.Max( peaks.Count - 1, 0 )} steps peak later toward the tip" ) );
	}

	void CheckTwins( Options options, Xform[][] poses, float[] times, Result result )
	{
		// Bone by bone at matching depth: each bone's turning speed per sample, lightly smoothed (a
		// reduced take jitters), correlated with the bone at the same depth in the other chain. A
		// chain sums over bones at different phases, which would hide the very offset that keeps
		// two cables apart - so compare like with like and take the median.
		var series = new List<(string Chain, List<float[]> Bones)>();

		foreach ( var chain in options.Chains )
		{
			var pattern = chain.Contains( '*' ) ? chain : chain + "*";
			var bones = Enumerable.Range( 0, Skeleton.Count ).Where( b => Glob( Name( b ), pattern ) ).ToList();
			var speeds = new List<float[]>();

			foreach ( var b in bones )
			{
				var raw = new float[poses.Length - 1];

				for ( var i = 0; i + 1 < poses.Length; i++ )
					raw[i] = Angle( Relative( poses[i], b, Skeleton.Bones[b].Parent ), Relative( poses[i + 1], b, Skeleton.Bones[b].Parent ) );

				if ( raw.Sum() < 1f )
					continue;

				var smooth = new float[raw.Length];

				for ( var i = 0; i < raw.Length; i++ )
				{
					var lo = Math.Max( i - 2, 0 );
					var hi = Math.Min( i + 2, raw.Length - 1 );
					var sum = 0f;
					for ( var j = lo; j <= hi; j++ ) sum += raw[j];
					smooth[i] = sum / (hi - lo + 1);
				}

				speeds.Add( smooth );
			}

			if ( speeds.Count > 0 )
				series.Add( (chain, speeds) );
		}

		for ( var a = 0; a < series.Count; a++ )
		{
			for ( var b = a + 1; b < series.Count; b++ )
			{
				var pairs = Math.Min( series[a].Bones.Count, series[b].Bones.Count );
				var scores = Enumerable.Range( 0, pairs ).Select( k => Correlation( series[a].Bones[k], series[b].Bones[k] ) ).OrderBy( v => v ).ToList();
				var r = scores.Count > 0 ? scores[scores.Count / 2] : 0f;

				if ( r < 0.95f )
					continue;

				result.Findings.Add( new Finding
				{
					Kind = "twins", Bone = $"{series[a].Chain} & {series[b].Chain}", Frame = times[0], Amount = r, Fail = false,
					Detail = string.Create( CultureInfo.InvariantCulture, $"move in lockstep (median correlation {r:0.###} bone for bone) - offset one by a few frames, or change its timing" ),
				} );
			}
		}
	}

	static float Correlation( float[] x, float[] y )
	{
		var n = Math.Min( x.Length, y.Length );

		if ( n < 3 )
			return 0f;

		double mx = 0, my = 0;
		for ( var i = 0; i < n; i++ ) { mx += x[i]; my += y[i]; }
		mx /= n; my /= n;

		double sxy = 0, sxx = 0, syy = 0;

		for ( var i = 0; i < n; i++ )
		{
			var dx = x[i] - mx;
			var dy = y[i] - my;
			sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
		}

		return sxx < 1e-12 || syy < 1e-12 ? 0f : (float)(sxy / Math.Sqrt( sxx * syy ));
	}

	// --- helpers --------------------------------------------------------------------------------

	string Name( int b ) => Skeleton.Bones[b].Name;

	bool KeysScale( string bone ) =>
		Clip.Find( bone ) is { } t && t.Keys.Any( k => MathF.Abs( k.Local.Scale.x - 1f ) > 1e-3f || MathF.Abs( k.Local.Scale.y - 1f ) > 1e-3f || MathF.Abs( k.Local.Scale.z - 1f ) > 1e-3f );

	bool KeysPosition( string bone )
	{
		if ( Clip.Find( bone ) is not { Keys.Count: > 0 } t )
			return false;

		var bind = Skeleton.Bones[Skeleton.IndexOf( bone )].Local.Origin;
		return t.Keys.Any( k => (k.Local.Position - bind).Length > 1e-2f );
	}

	static Quat Relative( Xform[] pose, int b, int parent ) =>
		parent >= 0 ? Quat.FromXform( pose[parent] ).Inverse * Quat.FromXform( pose[b] ) : Quat.FromXform( pose[b] );

	/// <summary>The angle between two rotations, degrees, 0..180.</summary>
	static float Angle( Quat a, Quat b )
	{
		var dot = MathF.Abs( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w );
		return 2f * MathF.Acos( Math.Clamp( dot, 0f, 1f ) ) * 180f / MathF.PI;
	}

	static bool Same( RigPose a, RigPose b ) =>
		(a.Position - b.Position).Length < 1e-3f && Angle( a.Rotation, b.Rotation ) < 0.05f && (a.Scale - b.Scale).Length < 1e-3f;

	/// <summary>Index spans (inclusive) where a path moves less than <paramref name="speed"/> per
	/// frame for at least three frames.</summary>
	static IEnumerable<(int Start, int End)> Spans( Vec3[] path, int steps, float speed )
	{
		var start = -1;

		for ( var i = 1; i <= path.Length; i++ )
		{
			var still = i < path.Length && (path[i] - path[i - 1]).Length * steps < speed;

			if ( still && start < 0 )
				start = i - 1;

			if ( !still && start >= 0 )
			{
				if ( i - 1 - start >= 3 * steps )
					yield return (start, i - 1);

				start = -1;
			}
		}
	}

	public static bool Glob( string name, string pattern )
	{
		if ( pattern == "*" ) return true;
		if ( !pattern.Contains( '*' ) ) return name == pattern;

		var parts = pattern.Split( '*' );
		var at = 0;

		for ( var i = 0; i < parts.Length; i++ )
		{
			if ( parts[i].Length == 0 ) continue;
			var found = name.IndexOf( parts[i], at, StringComparison.Ordinal );
			if ( found < 0 || (i == 0 && found != 0) ) return false;
			at = found + parts[i].Length;
		}

		return parts[^1].Length == 0 || name.EndsWith( parts[^1], StringComparison.Ordinal );
	}
}
