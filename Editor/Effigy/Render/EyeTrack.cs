using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy.Render;

/// <summary>
/// A camera that moves over a take: one row per frame of where the eye is and which way it
/// looks, read from a take file and interpolated between rows.
///
/// A first-person take is judged from its own eye, and that eye moves - it leans into a reach,
/// dips for a press. A still taken from a fixed point is a different shot from the one the
/// player gets, so stills follow the track instead.
///
/// The file is JSON with a frame rate ("Fps") and an array of rows under a key ("Eye" by
/// default), each row one of:
/// <list type="bullet">
/// <item>9 numbers: position x,y,z, forward x,y,z, up x,y,z.</item>
/// <item>10 numbers: the same 9 and one more, which is ignored.</item>
/// <item>6 numbers: position x,y,z, then pitch, yaw, roll in degrees, in s&box's
/// Rotation.From(pitch, yaw, roll) order - yaw about z, pitch about y with down positive,
/// roll about x.</item>
/// </list>
/// Positions are in whatever space the take was keyed in - for a view model, the arms' own.
/// </summary>
public sealed class EyeTrack
{
	public float FrameRate = 30f;
	public List<(Vec3 Position, Vec3 Forward, Vec3 Up)> Rows = new();

	public float Duration => Rows.Count <= 1 ? 0f : (Rows.Count - 1) / FrameRate;

	public static EyeTrack ReadFile( string path, string key = "Eye" ) => Read( File.ReadAllText( path ), key );

	public static EyeTrack Read( string json, string key = "Eye" )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		var track = new EyeTrack
		{
			FrameRate = root.TryGetProperty( "Fps", out var fps ) && fps.ValueKind == JsonValueKind.Number ? fps.GetSingle() : 30f,
		};

		if ( !root.TryGetProperty( key, out var rows ) || rows.ValueKind != JsonValueKind.Array )
			throw new FormatException( $"the take file has no \"{key}\" rows" );

		foreach ( var row in rows.EnumerateArray() )
		{
			var v = row.EnumerateArray().Select( x => x.GetSingle() ).ToArray();
			var position = new Vec3( v[0], v[1], v[2] );

			if ( v.Length >= 9 )
				track.Rows.Add( (position, new Vec3( v[3], v[4], v[5] ).Normal, new Vec3( v[6], v[7], v[8] ).Normal) );
			else if ( v.Length >= 6 )
			{
				var basis = FromAngles( v[3], v[4], v[5] );
				track.Rows.Add( (position, basis.X.Normal, basis.Z.Normal) );
			}
			else
				throw new FormatException( $"an eye row needs 6, 9 or 10 numbers, not {v.Length}" );
		}

		if ( track.Rows.Count == 0 )
			throw new FormatException( $"the take file's \"{key}\" has no rows" );

		return track;
	}

	/// <summary>s&box's Rotation.From(pitch, yaw, roll) as a basis: x forward, z up.</summary>
	public static Xform FromAngles( float pitch, float yaw, float roll )
	{
		const float d = MathF.PI / 180f;
		return Xform.Rotate( new Vec3( 0, 0, 1 ), yaw * d )
			* Xform.Rotate( new Vec3( 0, 1, 0 ), pitch * d )
			* Xform.Rotate( new Vec3( 1, 0, 0 ), roll * d );
	}

	/// <summary>The camera at a time in seconds, between the two nearest rows; held at the ends.</summary>
	public EyeCamera At( float seconds, float fieldOfView = 80f )
	{
		var f = Math.Clamp( seconds * FrameRate, 0f, Rows.Count - 1 );
		var i = Math.Min( (int)MathF.Floor( f ), Rows.Count - 1 );
		var j = Math.Min( i + 1, Rows.Count - 1 );
		var t = f - i;
		var a = Rows[i];
		var b = Rows[j];

		return new EyeCamera
		{
			Position = Vec3.Lerp( a.Position, b.Position, t ),
			Forward = Vec3.Lerp( a.Forward, b.Forward, t ).Normal,
			Up = Vec3.Lerp( a.Up, b.Up, t ).Normal,
			FieldOfView = fieldOfView,
		};
	}
}
