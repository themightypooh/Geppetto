using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// Bones from a <c>rigging_cables.json</c>: the file the Rigging creature's generator writes for
/// each muscle-to-tendon cable it builds (centreline, radius per point, the root and its
/// direction, and the frame the cable ends in). This turns that file into the rig the Rigging
/// animation plan specifies, so the creature can be rigged the moment the generator runs.
///
///   { "cables": [ { "name": "cable0", "role": "taut_pipe",
///       "root": [x,y,z], "rootDir": [x,y,z],
///       "end": { "origin": [x,y,z], "forward": [x,y,z], "up": [x,y,z] },
///       "points": [[x,y,z], …], "radii": [r, …],
///       "hand": "left" | "right",   (optional; see below)
///       "bones": 12 } ] }           (optional chain length)
///
/// WHAT IT BUILDS, per the plan (rigging-plan.md §1): <c>root</c> on the floor under the heap
/// and <c>heap</c> at the middle of the cable roots; per cable a <c>cableN_stump</c> (the rolled
/// skin collar), a chain <c>cableN_00</c>… evenly spaced in arc length from the stump to the
/// wrist, then either a hand - <c>cableN_hand</c> and five three-bone fingers - or, for a role
/// that names a foot, <c>cableN_foot</c> and <c>cableN_toes</c>. The heap's other bones (lungs,
/// sacs, faces) are not in the file and are added with <c>bone</c> lines.
///
/// CHAIN LENGTH: the file's "bones", else the plan's rule - joints about 3.5 times the cable's
/// mean radius apart, kept inside 6 to 16 - else whatever the caller passes per cable.
///
/// FINGER NAMES follow the plan, <c>cableN_{thumb,index,middle,ring,pinky}_{0,1,2}</c> (0/1/2 =
/// the MCP, PIP and DIP joints), NOT <see cref="SimRig.Tentacle"/>'s <c>cableN_fK_k</c>: these are
/// the names the plan's grips, curls and marks are written against. The fingers are placed open,
/// on the knuckle stations of the hand mesh the generator uses (MCP 4.45, PIP 5.85, DIP 6.85 in
/// from the wrist, tip at 7.6); the grip curls are poses, keyed in the animation.
///
/// HANDEDNESS. The generator's file does not say which hands are left and which right (its grip
/// call knows; the writer does not record it). A cable's "hand" field wins; otherwise the
/// caller's list of right hands; otherwise left. A left hand's thumb is on the hand's right
/// (palm down, fingers forward), a right hand's on its left. The two lines to add to the
/// generator's writer are in cloud-context/side-session-notes/marionette-rigging.
/// </summary>
public static class RigCables
{
	public sealed class Cable
	{
		public string Name;
		public string Role;
		public Vec3 Root, RootDir;
		public Vec3 EndOrigin, EndForward, EndUp;
		public List<Vec3> Points = new();
		public List<float> Radii = new();
		public string Hand;
		public int Bones;

		public bool IsFoot => (Role ?? "").Contains( "foot", StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>What the import did, cable by cable, for the script log.</summary>
	public sealed class Report
	{
		public readonly List<string> Lines = new();
		public int Bones;
	}

	/// <summary>Read the file's text. Throws <see cref="FormatException"/> naming what is wrong
	/// and where.</summary>
	public static List<Cable> Parse( string json )
	{
		JsonDocument doc;

		try
		{
			doc = JsonDocument.Parse( json ?? "" );
		}
		catch ( JsonException e )
		{
			throw new FormatException( $"The cable file is not valid JSON: {e.Message}" );
		}

		using ( doc )
		{
			if ( doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty( "cables", out var cables )
				|| cables.ValueKind != JsonValueKind.Array )
				throw new FormatException( "The cable file has no \"cables\" list. It should look like { \"cables\": [ { \"name\": …, \"points\": … } ] }." );

			var result = new List<Cable>();
			var index = 0;

			foreach ( var c in cables.EnumerateArray() )
			{
				var where = $"cable {index}";
				var cable = new Cable
				{
					Name = Text( c, "name" ) ?? $"cable{index}",
					Role = Text( c, "role" ) ?? "",
					Hand = Text( c, "hand" ),
				};

				where = $"cable '{cable.Name}'";

				if ( !c.TryGetProperty( "points", out var points ) || points.ValueKind != JsonValueKind.Array )
					throw new FormatException( $"{where} has no \"points\" list." );

				foreach ( var p in points.EnumerateArray() )
					cable.Points.Add( Vector( p, $"{where} points" ) );

				if ( cable.Points.Count < 2 )
					throw new FormatException( $"{where} has {cable.Points.Count} point(s); a centreline needs at least two." );

				if ( c.TryGetProperty( "radii", out var radii ) && radii.ValueKind == JsonValueKind.Array )
				{
					foreach ( var r in radii.EnumerateArray() )
						cable.Radii.Add( (float)r.GetDouble() );
				}

				if ( cable.Radii.Count != 0 && cable.Radii.Count != cable.Points.Count )
					throw new FormatException( $"{where} has {cable.Points.Count} points but {cable.Radii.Count} radii; they go one per point." );

				cable.Root = c.TryGetProperty( "root", out var root ) ? Vector( root, $"{where} root" ) : cable.Points[0];
				cable.RootDir = c.TryGetProperty( "rootDir", out var dir ) ? Vector( dir, $"{where} rootDir" ) : (cable.Points[1] - cable.Points[0]);

				if ( !c.TryGetProperty( "end", out var end ) || end.ValueKind != JsonValueKind.Object )
					throw new FormatException( $"{where} has no \"end\" frame (origin, forward, up)." );

				cable.EndOrigin = end.TryGetProperty( "origin", out var eo ) ? Vector( eo, $"{where} end.origin" ) : cable.Points[^1];
				cable.EndForward = end.TryGetProperty( "forward", out var ef ) ? Vector( ef, $"{where} end.forward" ) : (cable.Points[^1] - cable.Points[^2]);
				cable.EndUp = end.TryGetProperty( "up", out var eu ) ? Vector( eu, $"{where} end.up" ) : new Vec3( 0, 0, 1 );

				if ( cable.EndForward.Length < 1e-6f )
					throw new FormatException( $"{where}: end.forward has no length." );

				if ( c.TryGetProperty( "bones", out var bones ) && bones.ValueKind == JsonValueKind.Number )
					cable.Bones = bones.GetInt32();

				result.Add( cable );
				index++;
			}

			if ( result.Count == 0 )
				throw new FormatException( "The cable file's \"cables\" list is empty." );

			var dup = result.GroupBy( c => c.Name ).FirstOrDefault( g => g.Count() > 1 );

			if ( dup is not null )
				throw new FormatException( $"Two cables are called '{dup.Key}'. Bone names come from cable names, so they must differ." );

			return result;
		}
	}

	static string Text( JsonElement e, string key ) =>
		e.TryGetProperty( key, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	static Vec3 Vector( JsonElement e, string where )
	{
		if ( e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 3 )
			throw new FormatException( $"{where}: expected [x, y, z]." );

		var a = e.EnumerateArray().Select( v => (float)v.GetDouble() ).ToArray();
		return new Vec3( a[0], a[1], a[2] );
	}

	/// <summary>The plan's chain length for a cable: its own "bones", else joints about 3.5 × its
	/// mean radius apart, within 6..16.</summary>
	public static int ChainLength( Cable cable )
	{
		if ( cable.Bones > 0 )
			return cable.Bones;

		var length = 0f;
		for ( var i = 1; i < cable.Points.Count; i++ )
			length += (cable.Points[i] - cable.Points[i - 1]).Length;

		var radius = cable.Radii.Count > 0 ? cable.Radii.Average() : 1f;
		var count = (int)MathF.Round( length / MathF.Max( 3.5f * radius, 1e-3f ) );
		return Math.Clamp( count, 6, 16 );
	}

	/// <summary>The five fingers' names on one hand, MCP to DIP, as the plan writes them.</summary>
	public static readonly string[] Fingers = { "thumb", "index", "middle", "ring", "pinky" };

	/// <summary>
	/// Add the cables' bones to <paramref name="skeleton"/>. <paramref name="rightHands"/> names the
	/// cables whose hand is a right hand when the file does not say; <paramref name="counts"/>
	/// overrides the chain length per cable name.
	/// </summary>
	public static Report Build( Skeleton skeleton, IReadOnlyList<Cable> cables,
		IEnumerable<string> rightHands = null, IReadOnlyDictionary<string, int> counts = null )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		var report = new Report();
		var right = new HashSet<string>( rightHands ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase );
		var unknown = right.Where( n => cables.All( c => !string.Equals( c.Name, n, StringComparison.OrdinalIgnoreCase ) ) ).ToList();

		if ( unknown.Count > 0 )
			throw new ArgumentException( $"right= names {string.Join( ", ", unknown )}, which the file does not have. Cables: {string.Join( ", ", cables.Select( c => c.Name ) )}." );

		if ( counts is not null )
		{
			var missing = counts.Keys.Where( n => cables.All( c => c.Name != n ) ).ToList();

			if ( missing.Count > 0 )
				throw new ArgumentException( $"counts= names {string.Join( ", ", missing )}, which the file does not have. Cables: {string.Join( ", ", cables.Select( c => c.Name ) )}." );
		}

		var start = skeleton.Count;
		var centre = cables.Aggregate( Vec3.Zero, ( sum, c ) => sum + c.Root ) / cables.Count;
		var floor = MathF.Min( 0f, cables.SelectMany( c => c.Points ).Min( p => p.z ) );
		var up = new Vec3( 0, 0, 1 );

		var root = Ensure( skeleton, "root", -1, new Vec3( centre.x, centre.y, floor ), new Vec3( centre.x, centre.y, floor + 4f ), new Vec3( 1, 0, 0 ) );
		var heap = Ensure( skeleton, "heap", root, centre, centre + up * 6f, new Vec3( 1, 0, 0 ) );

		foreach ( var cable in cables )
		{
			var path = cable.Points;
			var r0 = cable.Radii.Count > 0 ? cable.Radii[0] : 1f;

			// The stump: the collar where the cable leaves the heap, a short bone from the root to a
			// point a radius and a half along the centreline (never more than a tenth of it).
			var total = 0f;
			for ( var i = 1; i < path.Count; i++ )
				total += (path[i] - path[i - 1]).Length;

			var stumpLength = MathF.Min( 1.5f * r0, 0.1f * total );
			var (stumpTail, rest) = SplitAt( path, stumpLength );
			var stumpHead = cable.Root;

			if ( (stumpTail - stumpHead).Length < 1e-4f )
				stumpHead = stumpTail - cable.RootDir.Normal * MathF.Max( stumpLength, 0.5f );

			var stump = skeleton.AddBoneFromPoints( $"{cable.Name}_stump", heap, stumpHead, stumpTail,
				RigBuild.DefaultUp( stumpTail - stumpHead ) );

			var count = counts is not null && counts.TryGetValue( cable.Name, out var n ) ? n : ChainLength( cable );

			// The chain ends where the hand starts: the wrist, which is the end frame's origin for a
			// hand. A foot's frame sits on the floor, so its chain ends at the centreline's end (the
			// ankle) instead.
			var chainEnd = cable.IsFoot ? path[^1] : cable.EndOrigin;
			var chainPath = new List<Vec3>( rest );

			if ( (chainPath[^1] - chainEnd).Length > 1e-3f )
				chainPath.Add( chainEnd );

			var chain = RigBuild.Chain( skeleton, cable.Name, stump, chainPath, count );
			var last = chain[^1];
			var f = cable.EndForward.Normal;
			var u = (cable.EndUp - f * Vec3.Dot( cable.EndUp, f )).Normal;

			if ( u.Length < 0.5f )
				u = RigBuild.DefaultUp( f );

			if ( cable.IsFoot )
			{
				var ball = cable.EndOrigin + f * 4.0f;
				var foot = skeleton.AddBoneFromPoints( $"{cable.Name}_foot", last, chainEnd, ball, u );
				skeleton.AddBoneFromPoints( $"{cable.Name}_toes", foot, ball, ball + f * 2.5f, u );
				report.Lines.Add( $"{cable.Name}: stump, {count} chain bones, foot and toes" );
				continue;
			}

			var isRight = string.Equals( cable.Hand, "right", StringComparison.OrdinalIgnoreCase )
				|| (cable.Hand is null && right.Contains( cable.Name ));
			var wrist = cable.EndOrigin;
			var hand = skeleton.AddBoneFromPoints( $"{cable.Name}_hand", last, wrist, wrist + f * 4.45f, u );

			// Hand frame: forward down the fingers, up out of the back of the hand, "left" to the
			// hand's left. A left hand palm-down has its thumb on its right.
			var left = Vec3.Cross( u, f ).Normal;
			var thumbSide = isRight ? left : left * -1f;
			var lanes = new[] { 1.15f, 0.55f, 0f, -0.55f, -1.05f };

			for ( var k = 1; k < Fingers.Length; k++ )
			{
				var lane = thumbSide * lanes[k];
				var stations = new[] { 4.45f, 5.85f, 6.85f, k == 4 ? 7.3f : 7.6f };
				var parent = hand;

				for ( var j = 0; j < 3; j++ )
				{
					parent = skeleton.AddBoneFromPoints( $"{cable.Name}_{Fingers[k]}_{j}", parent,
						wrist + f * stations[j] + lane, wrist + f * stations[j + 1] + lane, u );
				}
			}

			// The thumb leaves the palm side of the wrist, out toward its own side and forward.
			var thumbDir = (f * 0.6f + thumbSide * 0.75f - u * 0.3f).Normal;
			var t = wrist + f * 1.2f + thumbSide * 0.9f - u * 0.4f;
			var thumbParent = hand;

			foreach ( var (j, length) in new[] { (0, 1.6f), (1, 1.3f), (2, 1.1f) } )
			{
				thumbParent = skeleton.AddBoneFromPoints( $"{cable.Name}_thumb_{j}", thumbParent, t, t + thumbDir * length, u );
				t += thumbDir * length;
			}

			report.Lines.Add( $"{cable.Name}: stump, {count} chain bones, {(isRight ? "right" : "left")} hand with 5 fingers" );
		}

		report.Bones = skeleton.Count - start;
		return report;
	}

	static int Ensure( Skeleton skeleton, string name, int parent, Vec3 head, Vec3 tail, Vec3 up )
	{
		var existing = skeleton.IndexOf( name );
		return existing >= 0 ? existing : skeleton.AddBoneFromPoints( name, parent, head, tail, up );
	}

	/// <summary>The point <paramref name="distance"/> along the polyline and the polyline from that
	/// point to its end.</summary>
	static (Vec3 Point, List<Vec3> Remainder) SplitAt( IReadOnlyList<Vec3> path, float distance )
	{
		var walked = 0f;

		for ( var i = 1; i < path.Count; i++ )
		{
			var step = (path[i] - path[i - 1]).Length;

			if ( walked + step >= distance && step > 1e-9f )
			{
				var point = Vec3.Lerp( path[i - 1], path[i], (distance - walked) / step );
				var rest = new List<Vec3> { point };

				for ( var j = i; j < path.Count; j++ )
				{
					if ( (path[j] - rest[^1]).Length > 1e-5f )
						rest.Add( path[j] );
				}

				return (point, rest);
			}

			walked += step;
		}

		return (path[^1], new List<Vec3> { path[^2], path[^1] });
	}

	/// <summary>"cable0:12,cable1:11" as a name → count map.</summary>
	public static Dictionary<string, int> ParseCounts( string text )
	{
		var map = new Dictionary<string, int>();

		foreach ( var part in (text ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var colon = part.IndexOf( ':' );

			if ( colon <= 0 || !int.TryParse( part[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n ) || n < 1 || n > 64 )
				throw new FormatException( $"counts: '{part}' should be cable:bones, e.g. cable0:12 (1 to 64 bones)." );

			map[part[..colon]] = n;
		}

		return map;
	}
}
