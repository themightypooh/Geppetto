using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// Numbers about the model, and a plain-text account of it: what an agent reasons from when it
/// cannot see, and what it checks its picture against when it can.
///
/// LANDMARKS ARE THE POINT. "Put the buckle at the waist" needs a place called the waist. A bone's
/// name is one; a body's name with a side (`Torso.top`, `Hand_L.front`) is another; the origin and
/// the ground are two more. <see cref="Landmark"/> resolves all of them to a point, and the build
/// script uses the same resolver, so a script and a question agree about where the wrist is.
/// </summary>
public static class ModelMeasure
{
	public sealed class BodyReport
	{
		public Body Body;
		public Vec3 Min, Max, Centre, Size;
		public int Vertices, Faces, Triangles;
		public int OpenEdges, NonManifoldEdges;
		public bool Closed => OpenEdges == 0 && NonManifoldEdges == 0;
	}

	public static BodyReport Report( Body body )
	{
		var report = new BodyReport { Body = body };

		if ( body?.Mesh is not { } mesh || mesh.Positions.Count == 0 )
			return report;

		var (min, max) = Bounds( mesh );
		report.Min = min;
		report.Max = max;
		report.Size = max - min;
		report.Centre = (min + max) * 0.5f;
		report.Vertices = mesh.VertexCount;
		report.Faces = mesh.FaceCount;
		report.Triangles = mesh.Faces.Sum( f => Math.Max( 0, f.Indices.Length - 2 ) );

		var check = MeshValidator.Validate( mesh );
		report.OpenEdges = check.BoundaryEdges;
		report.NonManifoldEdges = check.NonManifoldEdges;

		return report;
	}

	public static (Vec3 Min, Vec3 Max) Bounds( PolyMesh mesh )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return mesh.Positions.Count == 0 ? (Vec3.Zero, Vec3.Zero) : (min, max);
	}

	public static (Vec3 Min, Vec3 Max) Bounds( IEnumerable<Body> bodies )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		var any = false;

		foreach ( var body in bodies )
		{
			if ( body?.Mesh is not { } mesh || mesh.Positions.Count == 0 )
				continue;

			var (a, b) = Bounds( mesh );
			min = new Vec3( MathF.Min( min.x, a.x ), MathF.Min( min.y, a.y ), MathF.Min( min.z, a.z ) );
			max = new Vec3( MathF.Max( max.x, b.x ), MathF.Max( max.y, b.y ), MathF.Max( max.z, b.z ) );
			any = true;
		}

		return any ? (min, max) : (Vec3.Zero, Vec3.Zero);
	}

	/// <summary>
	/// The model in words. Overall size and where it stands, then a line per body, then the rig
	/// and the history with anything that failed. Written for a reader that cannot see the
	/// picture, so every line carries a number.
	/// </summary>
	public static string Describe( PartStudio studio )
	{
		if ( studio is null )
			return "No studio.";

		var sb = new StringBuilder();
		var bodies = studio.Bodies.Where( b => b?.Mesh is not null ).ToList();

		if ( bodies.Count == 0 )
		{
			sb.AppendLine( "No bodies. The history has " + studio.Features.Count + " feature(s)." );
		}
		else
		{
			var (min, max) = Bounds( bodies );
			var size = max - min;
			sb.AppendLine( $"{bodies.Count} bod{(bodies.Count == 1 ? "y" : "ies")}, {N( size.x )} x {N( size.y )} x {N( size.z )} units (x forward, y left, z up)." );
			sb.AppendLine( $"Bounds x {N( min.x )}..{N( max.x )}, y {N( min.y )}..{N( max.y )}, z {N( min.z )}..{N( max.z )}. "
				+ (MathF.Abs( min.z ) < 0.05f * MathF.Max( size.z, 1f ) ? "Stands on the ground." : min.z > 0 ? $"Floats {N( min.z )} above the ground." : $"Sinks {N( -min.z )} below the ground.") );

			var total = bodies.Sum( b => b.Mesh.Faces.Sum( f => Math.Max( 0, f.Indices.Length - 2 ) ) );
			sb.AppendLine( $"{total:N0} triangles in all." );
			sb.AppendLine();

			foreach ( var body in bodies )
			{
				var r = Report( body );
				var where = Where( r.Centre, min, max );
				sb.Append( $"- {body.Name ?? body.Id}: {N( r.Size.x )} x {N( r.Size.y )} x {N( r.Size.z )}, centre ({N( r.Centre.x )}, {N( r.Centre.y )}, {N( r.Centre.z )}){where}, {r.Triangles:N0} tris" );

				if ( !r.Closed )
					sb.Append( $", {r.OpenEdges} open edge(s), {r.NonManifoldEdges} non-manifold" );

				if ( !body.Visible )
					sb.Append( ", hidden" );

				sb.AppendLine( "." );
			}
		}

		if ( studio.Rig is { Count: > 0 } rig )
		{
			sb.AppendLine();
			sb.AppendLine( $"Rig: {rig.Count} bones — " + string.Join( ", ", rig.Bones.Select( b => b.Name ) ) + "." );
		}

		var failed = studio.Features.Where( f => f.Error is not null ).ToList();
		var warned = studio.Features.Where( f => f.Error is null && f.Warning is not null ).ToList();

		sb.AppendLine();
		sb.AppendLine( $"History: {studio.Features.Count} feature(s): " + string.Join( " > ", studio.Features.Select( f => (f.Name ?? f.TypeName) + (f.Suppressed ? " (off)" : "") ) ) + "." );

		foreach ( var f in failed )
			sb.AppendLine( $"  FAILED {f.Name ?? f.TypeName}: {f.Error}" );

		foreach ( var f in warned )
			sb.AppendLine( $"  note {f.Name ?? f.TypeName}: {f.Warning}" );

		return sb.ToString().TrimEnd();
	}

	static string Where( Vec3 centre, Vec3 min, Vec3 max )
	{
		var size = max - min;
		var parts = new List<string>();

		if ( size.z > 1e-3f )
		{
			var t = (centre.z - min.z) / size.z;
			parts.Add( t > 0.66f ? "upper" : t < 0.33f ? "lower" : "middle" );
		}

		if ( size.y > 1e-3f && MathF.Abs( centre.y - (min.y + max.y) * 0.5f ) > size.y * 0.15f )
			parts.Add( centre.y > (min.y + max.y) * 0.5f ? "left" : "right" );

		if ( size.x > 1e-3f && MathF.Abs( centre.x - (min.x + max.x) * 0.5f ) > size.x * 0.15f )
			parts.Add( centre.x > (min.x + max.x) * 0.5f ? "front" : "back" );

		return parts.Count == 0 ? "" : " (" + string.Join( " ", parts ) + ")";
	}

	/// <summary>
	/// A named place on the model, or null with a reason.
	///
	/// `origin`, `ground`, a bone name (its head), `bone.tail`, `bone.mid`, a body name (its
	/// centre), `body.top|bottom|front|back|left|right|min|max`, or a literal `x,y,z`. Names
	/// match case-insensitively, and a space in a name can be written as an underscore.
	/// </summary>
	public static bool TryLandmark( PartStudio studio, string name, out Vec3 point, out string reason )
	{
		point = Vec3.Zero;
		reason = null;

		if ( string.IsNullOrWhiteSpace( name ) )
		{
			reason = "No landmark named.";
			return false;
		}

		name = name.Trim();

		if ( TryParseVec3( name, out point ) )
			return true;

		if ( name.Equals( "origin", StringComparison.OrdinalIgnoreCase ) )
			return true;

		if ( name.Equals( "ground", StringComparison.OrdinalIgnoreCase ) )
		{
			var (min, max) = Bounds( studio.Bodies );
			point = new Vec3( (min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, 0f );
			return true;
		}

		var dot = name.LastIndexOf( '.' );
		var head = dot > 0 ? name[..dot] : name;
		var side = dot > 0 ? name[(dot + 1)..].ToLowerInvariant() : "";

		var body = FindBody( studio, head );

		if ( body?.Mesh is not null )
		{
			var (min, max) = Bounds( body.Mesh );
			var c = (min + max) * 0.5f;

			point = side switch
			{
				"top" => new Vec3( c.x, c.y, max.z ),
				"bottom" => new Vec3( c.x, c.y, min.z ),
				"front" => new Vec3( max.x, c.y, c.z ),
				"back" => new Vec3( min.x, c.y, c.z ),
				"left" => new Vec3( c.x, max.y, c.z ),
				"right" => new Vec3( c.x, min.y, c.z ),
				"min" => min,
				"max" => max,
				"" or "centre" or "center" => c,
				_ => c,
			};

			if ( side.Length > 0 && side is not ("top" or "bottom" or "front" or "back" or "left" or "right" or "min" or "max" or "centre" or "center") )
			{
				reason = $"'{side}' is not a side of a body. Use top, bottom, front, back, left, right, min, max or centre.";
				return false;
			}

			return true;
		}

		var rig = studio.Rig;

		if ( rig is { Count: > 0 } )
		{
			var index = FindBone( rig, head );

			if ( index >= 0 )
			{
				point = side switch
				{
					"tail" => rig.TailWorld( index ),
					"mid" => (rig.HeadWorld( index ) + rig.TailWorld( index )) * 0.5f,
					_ => rig.HeadWorld( index ),
				};

				return true;
			}
		}

		reason = $"No body or bone called '{head}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}"
			+ (rig is { Count: > 0 } ? $". Bones: {string.Join( ", ", rig.Bones.Select( b => b.Name ) )}" : "") + ".";
		return false;
	}

	public static Body FindBody( PartStudio studio, string name )
	{
		if ( studio is null || string.IsNullOrWhiteSpace( name ) )
			return null;

		var wanted = name.Replace( '_', ' ' ).Trim();

		return studio.Bodies.FirstOrDefault( b => b.Id == name )
			?? studio.Bodies.FirstOrDefault( b => string.Equals( b.Name, name, StringComparison.OrdinalIgnoreCase ) )
			?? studio.Bodies.FirstOrDefault( b => string.Equals( b.Name, wanted, StringComparison.OrdinalIgnoreCase ) )
			?? studio.Bodies.FirstOrDefault( b => string.Equals( b.Name?.Replace( ' ', '_' ), name, StringComparison.OrdinalIgnoreCase ) );
	}

	public static int FindBone( Skeleton rig, string name )
	{
		for ( var i = 0; i < rig.Count; i++ )
			if ( string.Equals( rig.Bones[i].Name, name, StringComparison.OrdinalIgnoreCase )
				|| string.Equals( rig.Bones[i].Name?.Replace( ' ', '_' ), name, StringComparison.OrdinalIgnoreCase ) )
				return i;

		return -1;
	}

	public static bool TryParseVec3( string text, out Vec3 v )
	{
		v = Vec3.Zero;
		var parts = text.Split( ',' );

		if ( parts.Length != 3 )
			return false;

		if ( !float.TryParse( parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x )
			|| !float.TryParse( parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y )
			|| !float.TryParse( parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z ) )
			return false;

		v = new Vec3( x, y, z );
		return true;
	}

	/// <summary>What is at a point: the body it is inside or nearest, how far from that surface,
	/// and the nearest bone.</summary>
	public static string At( PartStudio studio, Vec3 p )
	{
		var sb = new StringBuilder();
		sb.Append( $"At ({N( p.x )}, {N( p.y )}, {N( p.z )}): " );

		Body nearest = null;
		var nearestDistance = float.MaxValue;

		foreach ( var body in studio.Bodies )
		{
			if ( body?.Mesh is null || body.Mesh.Faces.Count == 0 )
				continue;

			var d = DistanceToSurface( body.Mesh, p );

			if ( d < nearestDistance )
			{
				nearestDistance = d;
				nearest = body;
			}
		}

		if ( nearest is null )
		{
			sb.Append( "no bodies." );
		}
		else
		{
			var inside = IsInside( nearest.Mesh, p );
			sb.Append( inside
				? $"inside {nearest.Name ?? nearest.Id}, {N( nearestDistance )} from its surface."
				: $"{N( nearestDistance )} outside {nearest.Name ?? nearest.Id}." );
		}

		if ( studio.Rig is { Count: > 0 } rig )
		{
			var best = -1;
			var bestDistance = float.MaxValue;

			for ( var i = 0; i < rig.Count; i++ )
			{
				var d = DistanceToSegment( p, rig.HeadWorld( i ), rig.TailWorld( i ) );
				if ( d < bestDistance ) { bestDistance = d; best = i; }
			}

			if ( best >= 0 )
				sb.Append( $" Nearest bone {rig.Bones[best].Name}, {N( bestDistance )} away." );
		}

		return sb.ToString();
	}

	/// <summary>Distance from a point to the nearest triangle of the mesh.</summary>
	public static float DistanceToSurface( PolyMesh mesh, Vec3 p )
	{
		var best = float.MaxValue;

		foreach ( var face in mesh.Faces )
		{
			for ( var k = 1; k + 1 < face.Indices.Length; k++ )
			{
				var d = DistanceToTriangle( p, mesh.Positions[face.Indices[0]], mesh.Positions[face.Indices[k]], mesh.Positions[face.Indices[k + 1]] );
				if ( d < best ) best = d;
			}
		}

		return best == float.MaxValue ? 0f : best;
	}

	/// <summary>Inside test by ray parity along +x. Good enough for a closed mesh; an open one
	/// answers whatever the parity happens to be.</summary>
	public static bool IsInside( PolyMesh mesh, Vec3 p )
	{
		var hits = 0;
		var direction = new Vec3( 1f, 0.000173f, 0.000091f ).Normal;

		foreach ( var face in mesh.Faces )
		{
			for ( var k = 1; k + 1 < face.Indices.Length; k++ )
			{
				if ( RayTriangle( p, direction, mesh.Positions[face.Indices[0]], mesh.Positions[face.Indices[k]], mesh.Positions[face.Indices[k + 1]] ) )
					hits++;
			}
		}

		return (hits & 1) == 1;
	}

	static bool RayTriangle( Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c )
	{
		var e1 = b - a;
		var e2 = c - a;
		var h = Vec3.Cross( d, e2 );
		var det = Vec3.Dot( e1, h );

		if ( MathF.Abs( det ) < 1e-9f )
			return false;

		var inv = 1f / det;
		var s = o - a;
		var u = inv * Vec3.Dot( s, h );

		if ( u < 0f || u > 1f )
			return false;

		var q = Vec3.Cross( s, e1 );
		var v = inv * Vec3.Dot( d, q );

		if ( v < 0f || u + v > 1f )
			return false;

		return inv * Vec3.Dot( e2, q ) > 1e-7f;
	}

	public static float DistanceToSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var t = ab.LengthSquared < 1e-12f ? 0f : Math.Clamp( Vec3.Dot( p - a, ab ) / ab.LengthSquared, 0f, 1f );
		return (p - (a + ab * t)).Length;
	}

	/// <summary>Point–triangle distance (Ericson, Real-Time Collision Detection).</summary>
	public static float DistanceToTriangle( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		var ab = b - a; var ac = c - a; var ap = p - a;
		var d1 = Vec3.Dot( ab, ap ); var d2 = Vec3.Dot( ac, ap );
		if ( d1 <= 0f && d2 <= 0f ) return (p - a).Length;

		var bp = p - b;
		var d3 = Vec3.Dot( ab, bp ); var d4 = Vec3.Dot( ac, bp );
		if ( d3 >= 0f && d4 <= d3 ) return (p - b).Length;

		var vc = d1 * d4 - d3 * d2;
		if ( vc <= 0f && d1 >= 0f && d3 <= 0f ) { var v = d1 / (d1 - d3); return (p - (a + ab * v)).Length; }

		var cp = p - c;
		var d5 = Vec3.Dot( ab, cp ); var d6 = Vec3.Dot( ac, cp );
		if ( d6 >= 0f && d5 <= d6 ) return (p - c).Length;

		var vb = d5 * d2 - d1 * d6;
		if ( vb <= 0f && d2 >= 0f && d6 <= 0f ) { var w = d2 / (d2 - d6); return (p - (a + ac * w)).Length; }

		var va = d3 * d6 - d5 * d4;
		if ( va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f ) { var w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return (p - (b + (c - b) * w)).Length; }

		var denom = 1f / (va + vb + vc);
		var vv = vb * denom; var ww = vc * denom;
		return (p - (a + ab * vv + ac * ww)).Length;
	}

	/// <summary>
	/// How far from left–right symmetric a mesh is: each vertex mirrored across y = 0 (the
	/// engine's left–right plane) and matched to its nearest vertex, averaged, as a fraction of
	/// the mesh's width. 0 is a perfect mirror; 0.05 is a visibly lopsided character.
	/// </summary>
	public static float SymmetryError( PolyMesh mesh )
	{
		if ( mesh is null || mesh.Positions.Count == 0 )
			return 0f;

		var (min, max) = Bounds( mesh );
		var width = MathF.Max( max.y - min.y, 1e-6f );

		// A grid over the vertices so this is not quadratic on a dense mesh.
		var cell = width / 24f;
		var grid = new Dictionary<(int, int, int), List<int>>();

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var key = ((int)MathF.Floor( p.x / cell ), (int)MathF.Floor( p.y / cell ), (int)MathF.Floor( p.z / cell ));
			if ( !grid.TryGetValue( key, out var list ) ) grid[key] = list = new List<int>();
			list.Add( i );
		}

		var total = 0f;

		foreach ( var p in mesh.Positions )
		{
			var m = new Vec3( p.x, -p.y, p.z );
			var best = float.MaxValue;
			var kx = (int)MathF.Floor( m.x / cell ); var ky = (int)MathF.Floor( m.y / cell ); var kz = (int)MathF.Floor( m.z / cell );

			for ( var dx = -1; dx <= 1; dx++ )
				for ( var dy = -1; dy <= 1; dy++ )
					for ( var dz = -1; dz <= 1; dz++ )
					{
						if ( !grid.TryGetValue( (kx + dx, ky + dy, kz + dz), out var list ) )
							continue;

						foreach ( var j in list )
						{
							var d = (mesh.Positions[j] - m).Length;
							if ( d < best ) best = d;
						}
					}

			total += best == float.MaxValue ? cell * 2f : best;
		}

		return total / mesh.Positions.Count / width;
	}

	static string N( float v ) => MathF.Abs( v ) >= 100f ? $"{v:0}" : MathF.Abs( v ) >= 10f ? $"{v:0.#}" : $"{v:0.##}";
}
