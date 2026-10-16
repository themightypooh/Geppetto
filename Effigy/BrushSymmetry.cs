using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Origin planes a stroke mirrors across, any combination. X|Y|Z mirrors into all eight octants.</summary>
[Flags]
public enum SymmetryAxes
{
	None = 0,
	X = 1,
	Y = 2,
	Z = 4,
}

/// <summary>
/// One symmetric copy of a stroke: an orthogonal 3 × 3 map through the origin, as rows. Det is -1 for a
/// copy that is a reflection (an odd number of mirrors), which is what turns a clockwise twist into its
/// mirror image.
/// </summary>
public readonly struct SymmetryCopy
{
	public readonly Vec3 R0, R1, R2;
	public readonly float Det;

	public SymmetryCopy( Vec3 r0, Vec3 r1, Vec3 r2, float det )
	{
		R0 = r0;
		R1 = r1;
		R2 = r2;
		Det = det;
	}

	public Vec3 Apply( Vec3 v ) => new( Vec3.Dot( R0, v ), Vec3.Dot( R1, v ), Vec3.Dot( R2, v ) );

	public bool IsIdentity =>
		(R0 - new Vec3( 1, 0, 0 )).LengthSquared < 1e-10f
		&& (R1 - new Vec3( 0, 1, 0 )).LengthSquared < 1e-10f
		&& (R2 - new Vec3( 0, 0, 1 )).LengthSquared < 1e-10f;

	internal bool SameAs( SymmetryCopy o ) =>
		(R0 - o.R0).LengthSquared < 1e-8f && (R1 - o.R1).LengthSquared < 1e-8f && (R2 - o.R2).LengthSquared < 1e-8f;
}

/// <summary>
/// Every copy a symmetric stroke is applied as: each combination of the chosen mirror planes, each
/// turned through the N steps of radial symmetry about an axis through the origin. Mirror X with radial
/// 6 about Z is twelve copies — a six-armed star whose arms are each symmetric about their own middle.
///
/// SHARED BY SCULPT AND STROKE so the two cannot disagree about what "radial 5" means, the same reason
/// <see cref="Brush.Mirror(Vec3, MirrorAxis)"/> is the one place a single mirror lives.
///
/// Copies that land on each other (radial 2 about Z already contains the X+Y mirror's rotation) are
/// applied once, not twice.
/// </summary>
public static class BrushSymmetry
{
	public static SymmetryAxes AxesOf( MirrorAxis mirror ) => mirror switch
	{
		MirrorAxis.X => SymmetryAxes.X,
		MirrorAxis.Y => SymmetryAxes.Y,
		MirrorAxis.Z => SymmetryAxes.Z,
		_ => SymmetryAxes.None,
	};

	/// <summary>The copies, identity first. <paramref name="radial"/> below 2 means no radial symmetry.</summary>
	public static List<SymmetryCopy> Copies( SymmetryAxes mirrors, int radial = 1, MirrorAxis radialAxis = MirrorAxis.Z )
	{
		var result = new List<SymmetryCopy>();
		var steps = Math.Max( 1, radial );
		var axis = radialAxis switch
		{
			MirrorAxis.X => new Vec3( 1, 0, 0 ),
			MirrorAxis.Y => new Vec3( 0, 1, 0 ),
			_ => new Vec3( 0, 0, 1 ),
		};

		for ( var m = 0; m < 8; m++ )
		{
			if ( ((int)mirrors & m) != m )
				continue;

			var sx = (m & 1) != 0 ? -1f : 1f;
			var sy = (m & 2) != 0 ? -1f : 1f;
			var sz = (m & 4) != 0 ? -1f : 1f;

			for ( var k = 0; k < steps; k++ )
			{
				var angle = 2f * MathF.PI * k / steps;

				// Rotation rows times the mirror diagonal: column j of the rotation scaled by s_j.
				var ex = SimpleDeform.Rotate( new Vec3( 1, 0, 0 ), axis, angle ) * sx;
				var ey = SimpleDeform.Rotate( new Vec3( 0, 1, 0 ), axis, angle ) * sy;
				var ez = SimpleDeform.Rotate( new Vec3( 0, 0, 1 ), axis, angle ) * sz;

				var copy = new SymmetryCopy(
					new Vec3( ex.x, ey.x, ez.x ),
					new Vec3( ex.y, ey.y, ez.y ),
					new Vec3( ex.z, ey.z, ez.z ),
					sx * sy * sz );

				if ( !result.Exists( c => c.SameAs( copy ) ) )
					result.Add( copy );
			}
		}

		return result;
	}

	/// <summary>Read "X+Y", "xz", "XYZ", "none" as mirror planes. False for anything else.</summary>
	public static bool TryParseAxes( string text, out SymmetryAxes axes )
	{
		axes = SymmetryAxes.None;
		text = (text ?? "").Trim().ToUpperInvariant();

		if ( text is "" or "NONE" or "OFF" )
			return true;

		foreach ( var c in text )
		{
			switch ( c )
			{
				case 'X': axes |= SymmetryAxes.X; break;
				case 'Y': axes |= SymmetryAxes.Y; break;
				case 'Z': axes |= SymmetryAxes.Z; break;
				case '+': case ',': case ' ': break;
				default: return false;
			}
		}

		return true;
	}

	/// <summary>"X+Y" for X|Y, "None" for none.</summary>
	public static string Name( SymmetryAxes axes )
	{
		if ( axes == SymmetryAxes.None )
			return "None";

		var parts = new List<string>();
		if ( axes.HasFlag( SymmetryAxes.X ) ) parts.Add( "X" );
		if ( axes.HasFlag( SymmetryAxes.Y ) ) parts.Add( "Y" );
		if ( axes.HasFlag( SymmetryAxes.Z ) ) parts.Add( "Z" );
		return string.Join( "+", parts );
	}

	/// <summary>The mirror options as a dropdown lists them, in the order <see cref="StrokeFeature"/> stores
	/// by index: the four single choices MirrorAxis always had first, so saved files keep their meaning.</summary>
	public static readonly SymmetryAxes[] Choices =
	{
		SymmetryAxes.None, SymmetryAxes.X, SymmetryAxes.Y, SymmetryAxes.Z,
		SymmetryAxes.X | SymmetryAxes.Y, SymmetryAxes.X | SymmetryAxes.Z, SymmetryAxes.Y | SymmetryAxes.Z,
		SymmetryAxes.X | SymmetryAxes.Y | SymmetryAxes.Z,
	};

	public static string[] ChoiceNames()
	{
		var names = new string[Choices.Length];
		for ( var i = 0; i < names.Length; i++ ) names[i] = Name( Choices[i] );
		return names;
	}
}
