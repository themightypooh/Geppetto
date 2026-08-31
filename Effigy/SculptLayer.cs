using System;

namespace Effigy;

/// <summary>
/// One sculpt's displacements, stored as per-vertex coefficients in <see cref="SculptFrame"/>.
/// Capture reads world positions into that frame; Apply writes them back. The mesh is not the
/// source of truth — the deltas are — so a cage rebuild can re-derive frames and re-apply.
/// </summary>
public sealed class SculptLayer
{
	public readonly Vec3[] Deltas;

	public SculptLayer( Vec3[] deltas )
	{
		Deltas = deltas ?? throw new ArgumentNullException( nameof( deltas ) );
	}

	public int Count => Deltas.Length;

	/// <summary>Frame-space deltas that take <paramref name="rest"/> onto <paramref name="displaced"/>.</summary>
	public static SculptLayer Capture( PolyMesh rest, PolyMesh displaced, SculptFrames frames )
	{
		if ( rest is null )
			throw new ArgumentNullException( nameof( rest ) );

		if ( displaced is null )
			throw new ArgumentNullException( nameof( displaced ) );

		if ( frames is null )
			throw new ArgumentNullException( nameof( frames ) );

		if ( rest.VertexCount != displaced.VertexCount || rest.VertexCount != frames.Count )
			throw new ArgumentException(
				$"Capture needs matching vertex counts (rest {rest.VertexCount}, displaced {displaced.VertexCount}, frames {frames.Count})" );

		var deltas = new Vec3[rest.VertexCount];

		for ( var i = 0; i < rest.VertexCount; i++ )
			deltas[i] = frames.At[i].FromWorld( displaced.Positions[i] - rest.Positions[i] );

		return new SculptLayer( deltas );
	}

	/// <summary>Add this layer's deltas onto <paramref name="mesh"/>, which must be at rest.</summary>
	public void Apply( PolyMesh mesh, SculptFrames frames )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( frames is null )
			throw new ArgumentNullException( nameof( frames ) );

		if ( mesh.VertexCount != Deltas.Length || mesh.VertexCount != frames.Count )
			throw new ArgumentException(
				$"Apply needs matching vertex counts (mesh {mesh.VertexCount}, deltas {Deltas.Length}, frames {frames.Count})" );

		for ( var i = 0; i < mesh.VertexCount; i++ )
			mesh.Positions[i] += frames.At[i].ToWorld( Deltas[i] );
	}
}
