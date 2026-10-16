using System;

namespace Effigy;

public enum SimpleDeformMode
{
	/// <summary>Turn each slice about the axis, more the further along it is.</summary>
	Twist,
	/// <summary>Curl the axis into an arc toward a side.</summary>
	Bend,
	/// <summary>Grow (or shrink) the cross-section along the axis.</summary>
	Taper,
	/// <summary>Lengthen along the axis and thin the cross-section to keep the volume.</summary>
	Stretch,
}

/// <summary>
/// What a simple deform needs, already resolved to numbers: the frame, the band it acts over and how
/// much. Coordinates along the axis are measured from <see cref="Pivot"/>.
/// </summary>
public sealed class SimpleDeformSettings
{
	public SimpleDeformMode Mode;

	/// <summary>The deform axis, unit length.</summary>
	public Vec3 Axis = new( 0, 0, 1 );

	/// <summary>Bend only: the side the axis curls toward. Unit length, perpendicular to Axis.</summary>
	public Vec3 Toward = new( 1, 0, 0 );

	public Vec3 Pivot;

	/// <summary>Twist and Bend: the total turn over the band, in radians.</summary>
	public float Angle;

	/// <summary>Taper: how much the cross-section grows over the band (0.5 is half as wide again).
	/// Stretch: how much longer the band gets (0.5 is half as long again).</summary>
	public float Factor;

	/// <summary>The band, as distances along the axis from the pivot. Outside it the shape is carried
	/// along rigidly, the way the ends of a bent bar stay straight.</summary>
	public float Lower, Upper;
}

/// <summary>
/// Twist, bend, taper and stretch along an axis — the four classic one-axis deforms (Barr 1984,
/// "Global and local deformations of solid primitives"), with the band limits of a modeller's simple
/// deform.
///
/// THE PIVOT PLANE STAYS PUT. Every mode is measured from where the pivot falls inside the band
/// (clamped into it when the pivot is outside), so that slice is never moved and the deform reads as
/// growing out of it. Past either end of the band the rest of the shape follows rigidly: a twisted
/// band's ends keep their turn, a bent band's ends carry on straight along the arc's tangent, so the
/// surface is continuous at the band's edges.
///
/// Pure geometry: positions move, topology, UVs and skin weights are untouched, so a rigged body
/// stays rigged.
/// </summary>
public static class SimpleDeform
{
	/// <summary>Where <paramref name="p"/> goes.</summary>
	public static Vec3 Deform( Vec3 p, SimpleDeformSettings s )
	{
		var axis = s.Axis.Normal;
		var span = s.Upper - s.Lower;

		if ( span <= 1e-9f )
			return p;

		var q = p - s.Pivot;
		var z = Vec3.Dot( q, axis );
		var zc = Math.Clamp( z, s.Lower, s.Upper );
		var z0 = Math.Clamp( 0f, s.Lower, s.Upper );
		var along = zc - z0;

		switch ( s.Mode )
		{
			case SimpleDeformMode.Twist:
			{
				var theta = s.Angle * along / span;
				return s.Pivot + Rotate( q, axis, theta );
			}

			case SimpleDeformMode.Taper:
			{
				var k = 1f + s.Factor * along / span;
				var radial = q - axis * z;
				return s.Pivot + axis * z + radial * k;
			}

			case SimpleDeformMode.Stretch:
			{
				var k = 1f + s.Factor;

				if ( k <= 1e-6f )
					return p;

				// Inside the band lengths scale by k; the band's ends carry the rest along. The whole
				// cross-section thins by 1/sqrt(k), which keeps the band's volume.
				var newZ = z0 + along * k + (z - zc);
				var radial = (q - axis * z) / MathF.Sqrt( k );
				return s.Pivot + axis * newZ + radial;
			}

			case SimpleDeformMode.Bend:
			{
				if ( MathF.Abs( s.Angle ) < 1e-7f )
					return p;

				var toward = (s.Toward - axis * Vec3.Dot( s.Toward, axis )).Normal;
				var u = Vec3.Dot( q, toward );
				var side = q - axis * z - toward * u;

				// The centre line curls round a circle of radius R toward `toward`; a point u off the
				// centre line rides a circle of radius R - u about the same centre.
				var radius = span / s.Angle;
				var theta = along / radius;
				var r = radius - u;
				var bentU = radius - r * MathF.Cos( theta );
				var bentZ = z0 + r * MathF.Sin( theta );

				// Past the band: straight on along the arc's tangent there.
				var extra = z - zc;
				bentU += extra * MathF.Sin( theta );
				bentZ += extra * MathF.Cos( theta );

				return s.Pivot + toward * bentU + axis * bentZ + side;
			}
		}

		return p;
	}

	/// <summary>Deform every vertex of <paramref name="mesh"/> in place. Returns how many moved.</summary>
	public static int Apply( PolyMesh mesh, SimpleDeformSettings s )
	{
		var moved = 0;

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var before = mesh.Positions[i];
			var after = Deform( before, s );

			if ( (after - before).LengthSquared > 1e-14f )
				moved++;

			mesh.Positions[i] = after;
		}

		return moved;
	}

	/// <summary>Rodrigues' rotation of <paramref name="v"/> about unit <paramref name="axis"/>.</summary>
	internal static Vec3 Rotate( Vec3 v, Vec3 axis, float angle )
	{
		var c = MathF.Cos( angle );
		var s = MathF.Sin( angle );
		return v * c + Vec3.Cross( axis, v ) * s + axis * (Vec3.Dot( axis, v ) * (1f - c));
	}
}
