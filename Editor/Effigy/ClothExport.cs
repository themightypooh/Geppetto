using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// A garment as live cloth: the file Marionette's GarmentCloth component simulates in the game.
///
/// WHY A SECOND EXPORT. A .clothing item is a skinned model - the shirt is baked into the pose
/// the drape left it in and then rides the bones rigidly. That is how every s&amp;box garment
/// works and it is fine for a fitted top; it is not a shirt hanging loose off a robot's
/// shoulders, which has to swing when he turns. That takes a simulation at runtime, and the
/// simulation needs things a model cannot carry: which bone each collider rides on and how thick
/// it is, and which bone each bit of cloth is leashed to.
///
/// EVERYTHING IN THE WEARER'S BIND POSE, in model space - the same space the garment was fitted
/// in. The component reads the model's bind transforms at start and turns each of these into an
/// offset from its bone, so the file does not care what pose the character is in when it loads.
///
/// JSON, WRITTEN BY HAND, for the reason ClothingDefinition gives: the kernel stays engine-free
/// and testable, and the game reads it with the engine's own deserialiser.
/// </summary>
public static class ClothExport
{
	/// <param name="garment">The finished garment, in the wearer's model space.</param>
	/// <param name="capsules">What it hangs on - see BoneCapsules. Their Bone indexes <paramref name="skeleton"/>.</param>
	public static string Write( PolyMesh garment, IReadOnlyList<Capsule> capsules, Skeleton skeleton, string material )
	{
		string Name( int bone ) => bone >= 0 && bone < skeleton.Count ? skeleton.Bones[bone].Name : "";

		var usable = capsules.Where( c => c.Bone >= 0 && c.Bone < skeleton.Count ).ToList();
		var sb = new StringBuilder();
		var inv = CultureInfo.InvariantCulture;

		string F( float v ) => float.IsFinite( v ) ? v.ToString( "0.####", inv ) : "0";

		sb.Append( "{\n" );
		sb.Append( $"  \"Material\": \"{Escape( material ?? "" )}\",\n" );

		// Per vertex: its bind position, a UV (the first corner that names one - a seam's two
		// sides differ, and cloth is simulated per vertex, not per corner) and the bone it is
		// leashed to, which is the bone of the capsule it lies nearest.
		var uvs = new Vec2[garment.VertexCount];

		foreach ( var face in garment.Faces )
		{
			if ( face.UVs is null )
				continue;

			for ( var c = 0; c < face.Count && c < face.UVs.Length; c++ )
				if ( uvs[face.Indices[c]].Equals( default( Vec2 ) ) )
					uvs[face.Indices[c]] = face.UVs[c];
		}

		sb.Append( "  \"Positions\": [" );
		sb.Append( string.Join( ",", garment.Positions.Select( p => $"{F( p.x )},{F( p.y )},{F( p.z )}" ) ) );
		sb.Append( "],\n" );

		sb.Append( "  \"UVs\": [" );
		sb.Append( string.Join( ",", uvs.Select( u => $"{F( u.x )},{F( u.y )}" ) ) );
		sb.Append( "],\n" );

		sb.Append( "  \"Triangles\": [" );
		var tris = new List<int>();

		foreach ( var face in garment.Faces )
			for ( var c = 1; c + 1 < face.Count; c++ )
			{
				tris.Add( face.Indices[0] );
				tris.Add( face.Indices[c] );
				tris.Add( face.Indices[c + 1] );
			}

		sb.Append( string.Join( ",", tris ) );
		sb.Append( "],\n" );

		sb.Append( "  \"VertexBones\": [" );
		sb.Append( string.Join( ",", garment.Positions.Select( p =>
		{
			var best = -1;
			var bestD = float.MaxValue;

			foreach ( var c in usable )
			{
				var d = (c.Closest( p ) - p).Length - c.Radius;

				if ( d < bestD )
				{
					bestD = d;
					best = c.Bone;
				}
			}

			return $"\"{Escape( Name( best ) )}\"";
		} ) ) );
		sb.Append( "],\n" );

		sb.Append( "  \"Capsules\": [" );
		sb.Append( string.Join( ",", usable.Select( c =>
			$"{{\"Bone\":\"{Escape( Name( c.Bone ) )}\",\"A\":[{F( c.A.x )},{F( c.A.y )},{F( c.A.z )}],"
			+ $"\"B\":[{F( c.B.x )},{F( c.B.y )},{F( c.B.z )}],\"Radius\":{F( c.Radius )}}}" ) ) );
		sb.Append( "]\n" );

		sb.Append( "}\n" );
		return sb.ToString();
	}

	static string Escape( string s ) => s.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
}
