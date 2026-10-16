using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Cylinder and sphere projection: UVs from wrapping the texture round an axis, for a pipe, a limb,
/// a can, a head or an eye - the shapes a box projection cuts into four pieces.
///
/// U GOES ONCE ROUND. Both map the angle about the axis to U, 0 to 1, so a texture that tiles in U
/// wraps without a seam showing. V is height along the axis for the cylinder (scaled so texels are
/// square at the average radius) and latitude for the sphere (0 at the bottom pole, 1 at the top -
/// the equirectangular layout spherical textures come in).
///
/// THE WRAP SEAM IS PER FACE. A face that straddles the place where U wraps from 1 back to 0 would
/// otherwise stretch backwards across the whole texture. Its corners on the low side are moved up
/// by one, so the face spans a little past 1 instead - the texture repeats, so that is the same
/// texels. A corner ON the axis (a cap's centre, a pole) has no angle at all and takes the average
/// U of the face's other corners, which is what keeps a pole fan from twisting.
///
/// HANDEDNESS. U runs anticlockwise looking down the axis and V runs up it, so on the outside of
/// the surface (u, v, normal) is right-handed, the same rule <see cref="BoxProject"/> keeps: the
/// texture reads the right way round from outside.
/// </summary>
public static partial class UVProjection
{
	/// <summary>
	/// Wrap <paramref name="faces"/> (all faces when null) round <paramref name="axis"/> through
	/// <paramref name="centre"/> (the faces' bounds centre when null).
	/// </summary>
	public static void CylinderProject( PolyMesh mesh, Vec3 axis, Vec3? centre = null, IEnumerable<int> faces = null ) =>
		Wrap( mesh, axis, centre, faces, sphere: false );

	/// <summary>
	/// Map <paramref name="faces"/> (all faces when null) by longitude and latitude about
	/// <paramref name="axis"/>, from <paramref name="centre"/> (the faces' bounds centre when null).
	/// </summary>
	public static void SphereProject( PolyMesh mesh, Vec3 axis, Vec3? centre = null, IEnumerable<int> faces = null ) =>
		Wrap( mesh, axis, centre, faces, sphere: true );

	static void Wrap( PolyMesh mesh, Vec3 axis, Vec3? centre, IEnumerable<int> faces, bool sphere )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var a = axis.Normal;
		if ( a.LengthSquared < 0.5f )
			throw new InvalidOperationException( "The projection axis has no length. Give it the direction the shape runs along, such as 0,0,1." );

		var chosen = faces?.Where( f => f >= 0 && f < mesh.FaceCount ).Distinct().ToList() ?? Enumerable.Range( 0, mesh.FaceCount ).ToList();
		if ( chosen.Count == 0 )
			throw new InvalidOperationException( "There are no faces to project." );

		var c = centre ?? BoundsCentre( mesh, chosen );

		// Any reference direction square to the axis; the one the axis leans on least is best conditioned.
		var seed = MathF.Abs( a.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		var r0 = (seed - a * Vec3.Dot( seed, a )).Normal;
		var r1 = Vec3.Cross( a, r0 );

		// The average distance from the axis sets the cylinder's V scale.
		var radius = 0.0;
		var heightMin = float.MaxValue;
		var used = new HashSet<int>();
		foreach ( var f in chosen )
			foreach ( var v in mesh.Faces[f].Indices )
				used.Add( v );

		foreach ( var v in used )
		{
			var d = mesh.Positions[v] - c;
			var h = Vec3.Dot( d, a );
			radius += (d - a * h).Length;
			heightMin = MathF.Min( heightMin, h );
		}

		radius /= Math.Max( 1, used.Count );
		if ( !sphere && radius < 1e-6 )
			throw new InvalidOperationException( "Every vertex is on the projection axis, so there is nothing to wrap round. Pick an axis the shape runs along, not one through all of it." );

		var circumference = (float)(2.0 * Math.PI * radius);

		foreach ( var f in chosen )
		{
			var face = mesh.Faces[f];
			var n = face.Count;
			var us = new float[n];
			var vs = new float[n];
			var onAxis = new bool[n];

			for ( var i = 0; i < n; i++ )
			{
				var d = mesh.Positions[face.Indices[i]] - c;
				var h = Vec3.Dot( d, a );
				var radial = d - a * h;
				var rl = radial.Length;
				var length = d.Length;

				// "On the axis" relative to the shape's own size, so a tiny model is not all poles.
				onAxis[i] = rl <= 1e-5f * MathF.Max( 1f, sphere ? length : (float)radius );
				us[i] = onAxis[i] ? 0f : MathF.Atan2( Vec3.Dot( radial, r1 ), Vec3.Dot( radial, r0 ) ) / MathF.Tau + 0.5f;

				if ( sphere )
					vs[i] = length > 1e-12f ? MathF.Asin( Math.Clamp( h / length, -1f, 1f ) ) / MathF.PI + 0.5f : 0.5f;
				else
					vs[i] = (h - heightMin) / circumference;
			}

			// Unwrap the angle across the 1 -> 0 seam: lift the low side by a turn.
			var min = float.MaxValue;
			var max = float.MinValue;
			for ( var i = 0; i < n; i++ )
			{
				if ( onAxis[i] )
					continue;
				min = MathF.Min( min, us[i] );
				max = MathF.Max( max, us[i] );
			}

			if ( max - min > 0.5f )
			{
				for ( var i = 0; i < n; i++ )
					if ( !onAxis[i] && us[i] < 0.5f )
						us[i] += 1f;
			}

			// A corner on the axis takes the average angle of the others.
			var sum = 0f;
			var count = 0;
			for ( var i = 0; i < n; i++ )
			{
				if ( onAxis[i] )
					continue;
				sum += us[i];
				count++;
			}

			for ( var i = 0; i < n; i++ )
			{
				if ( onAxis[i] )
					us[i] = count > 0 ? sum / count : 0.5f;
				face.UVs[i] = new Vec2( us[i], vs[i] );
			}
		}
	}

	static Vec3 BoundsCentre( PolyMesh mesh, IEnumerable<int> faces )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var f in faces )
		{
			foreach ( var v in mesh.Faces[f].Indices )
			{
				var p = mesh.Positions[v];
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
		}

		return (min + max) * 0.5f;
	}

	/// <summary>The direction the faces' bounds are longest along - the axis a limb or a pipe runs
	/// on, which is the useful default for a cylinder projection.</summary>
	public static Vec3 LongestAxis( PolyMesh mesh, IEnumerable<int> faces = null )
	{
		var chosen = faces ?? Enumerable.Range( 0, mesh.FaceCount );
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var f in chosen )
		{
			foreach ( var v in mesh.Faces[f].Indices )
			{
				var p = mesh.Positions[v];
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
		}

		var s = max - min;
		return s.x >= s.y && s.x >= s.z ? new Vec3( 1, 0, 0 ) : s.y >= s.z ? new Vec3( 0, 1, 0 ) : new Vec3( 0, 0, 1 );
	}
}
