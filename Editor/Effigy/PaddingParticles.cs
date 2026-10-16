using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// What one Padding did to one garment, per vertex of the garment's outer sheet
/// (MeshSolidify.OuterSheet, or the garment itself when it has no thickness).
/// </summary>
public sealed class PaddingField
{
	/// <summary>How much thicker each sheet vertex got, in inches.</summary>
	public readonly float[] Thickness;

	/// <summary>The same without the pad's Thickness, 0..1: region mask, feather and quilt.</summary>
	public readonly float[] Weight;

	/// <summary>The pad's Stiffer and Heavier at full weight.</summary>
	public readonly float Stiffer, Heavier;

	public PaddingField( float[] thickness, float[] weight, float stiffer, float heavier )
	{
		Thickness = thickness ?? Array.Empty<float>();
		Weight = weight ?? Array.Empty<float>();
		Stiffer = stiffer;
		Heavier = heavier;
	}
}

/// <summary>
/// Padding as live cloth: per-particle thickness, bend stiffness and mass for one garment.
///
/// THE KERNEL HALF ONLY. The particles are numbered the way Make live cloth numbers them - the
/// garment's outer sheet, fur left off, in vertex order - so particle i is sheet vertex i. Writing
/// these into the .cloth.json and stepping them in GarmentCloth (a thicker collision gap, a bigger
/// bend scale, and corrections split by mass, wa/(wa+wb), instead of 50/50) is the live-cloth
/// runtime's side and is not done here.
/// </summary>
public sealed class PaddingParticles
{
	/// <summary>Extra thickness per particle, in inches (0 unpadded). Added to the cloth's own.</summary>
	public float[] Thickness;

	/// <summary>Bend stiffness multiplier per particle, 1 unpadded.</summary>
	public float[] BendScale;

	/// <summary>Mass multiplier per particle, 1 unpadded.</summary>
	public float[] Mass;

	/// <summary>Any particle padded at all.</summary>
	public bool Any => Thickness.Any( t => t > 0f );

	/// <summary>Kept at or under the Heavier slider's top: heavier and the light cloth beside it
	/// cannot hold it up.</summary>
	public const float MaxMass = 4f;

	/// <summary>The Stiffer slider's top, kept when two pads overlap.</summary>
	public const float MaxBend = 10f;

	/// <summary>
	/// Every Padding in <paramref name="paddings"/> that padded body <paramref name="bodyId"/>,
	/// combined for a garment with <paramref name="particles"/> particles. Overlapping pads (a yoke
	/// and a shoulder pad) add their thickness and their extra stiffness and weight, capped at the
	/// sliders' tops. Suppressed or failed paddings, and ones that did not reach this garment, are
	/// skipped; a field for a different particle count (the garment was rebuilt since) is skipped.
	/// </summary>
	public static PaddingParticles For( string bodyId, int particles, IEnumerable<PaddingFeature> paddings )
	{
		var result = new PaddingParticles
		{
			Thickness = new float[Math.Max( 0, particles )],
			BendScale = Enumerable.Repeat( 1f, Math.Max( 0, particles ) ).ToArray(),
			Mass = Enumerable.Repeat( 1f, Math.Max( 0, particles ) ).ToArray(),
		};

		foreach ( var padding in paddings ?? Enumerable.Empty<PaddingFeature>() )
		{
			if ( padding is null || padding.Suppressed || padding.Error is not null )
				continue;

			if ( bodyId is null || !padding.Fields.TryGetValue( bodyId, out var field ) )
				continue;

			if ( field.Thickness.Length != particles || field.Weight.Length != particles )
				continue;

			for ( var i = 0; i < particles; i++ )
			{
				var w = field.Weight[i];

				if ( w <= 0f )
					continue;

				result.Thickness[i] += field.Thickness[i];
				result.BendScale[i] = MathF.Min( MaxBend, result.BendScale[i] + (field.Stiffer - 1f) * w );
				result.Mass[i] = MathF.Min( MaxMass, result.Mass[i] + (field.Heavier - 1f) * w );
			}
		}

		return result;
	}
}
