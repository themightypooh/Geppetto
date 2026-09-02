using System;

namespace Effigy;

/// <summary>
/// Per-vertex protection for one sculpt level. 1 means "brush me normally", 0 means "leave me
/// alone", and everything between is a soft edge.
///
/// THE SENSE IS THE WAY IT IS BECAUSE <see cref="Brush"/> ALREADY MULTIPLIES BY IT. A stroke's
/// weight is falloff x strength x mask, so an all-ones mask is the same as no mask at all and a
/// freshly made one changes nothing. Storing "how protected" instead would invert every stroke in
/// the tool the moment a mask existed, which is the sort of thing that reads as the brush being
/// broken.
///
/// It is deliberately NOT persisted. A mask is a working aid — the sculpting equivalent of a
/// selection — and it belongs to the session that painted it. Saving one would mean a format
/// change and a promise to keep it correct across a cage edit, for something whose whole use is
/// "hold this bit still while I do the next ten strokes".
/// </summary>
public sealed class SculptMask
{
	readonly float[] _values;

	public SculptMask( int count )
	{
		if ( count < 0 )
			throw new ArgumentOutOfRangeException( nameof( count ) );

		_values = new float[count];
		Clear();
	}

	public int Count => _values.Length;

	/// <summary>Bumped by every change. A display cache keyed on the sculpt's revision alone cannot
	/// see a mask move, and hide-by-mask would serve the mesh from before the last stroke.</summary>
	public int Revision { get; private set; }

	/// <summary>The array <see cref="Brush.Apply"/> takes. Live, not a copy.</summary>
	public float[] Values => _values;

	public float this[int index]
	{
		get => _values[index];
		set
		{
			_values[index] = Math.Clamp( value, 0f, 1f );
			Revision++;
		}
	}

	/// <summary>Nothing protected — the state in which the mask does not exist as far as a brush
	/// is concerned.</summary>
	public void Clear()
	{
		Array.Fill( _values, 1f );
		Revision++;
	}

	/// <summary>Protect everything, which is where "mask all but this" starts.</summary>
	public void Protect()
	{
		Array.Fill( _values, 0f );
		Revision++;
	}

	public void Invert()
	{
		for ( var i = 0; i < _values.Length; i++ )
			_values[i] = 1f - _values[i];

		Revision++;
	}

	/// <summary>Whether anything is protected at all, so a UI can say so and a brush can skip the
	/// multiply.</summary>
	public bool Any
	{
		get
		{
			foreach ( var v in _values )
			{
				if ( v < 0.999f )
					return true;
			}

			return false;
		}
	}

	/// <summary>How much of the mesh is protected, 0 to 1 — what a readout shows.</summary>
	public float ProtectedFraction
	{
		get
		{
			if ( _values.Length == 0 )
				return 0f;

			var sum = 0f;

			foreach ( var v in _values )
				sum += 1f - v;

			return sum / _values.Length;
		}
	}

	/// <summary>
	/// Paint one dab, in the same shape a brush stroke has: a point, a radius, a falloff and a
	/// strength. Positive strength protects, negative releases, so one control does both ways round.
	/// </summary>
	public void Paint( PolyMesh mesh, MeshBVH bvh, Vec3 point, float radius, float strength, BrushFalloff falloff )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( mesh.VertexCount != _values.Length )
			throw new ArgumentException(
				$"This mask has {_values.Length} values and the mesh has {mesh.VertexCount} vertices." );

		if ( radius <= 0f )
			return;

		var found = new System.Collections.Generic.List<int>();

		if ( bvh is not null )
		{
			bvh.VerticesInRadius( mesh, point, radius, found );
		}
		else
		{
			var r2 = radius * radius;

			for ( var i = 0; i < mesh.VertexCount; i++ )
			{
				if ( (mesh.Positions[i] - point).LengthSquared <= r2 )
					found.Add( i );
			}
		}

		foreach ( var vi in found )
		{
			var t = (mesh.Positions[vi] - point).Length / radius;
			var w = Brush.Falloff( t, falloff ) * strength;

			// Protecting SUBTRACTS, because 1 is unprotected. Getting this backwards is the one
			// mistake this whole file's comment header exists to prevent.
			_values[vi] = Math.Clamp( _values[vi] - w, 0f, 1f );
		}

		Revision++;
	}

	/// <summary>
	/// A copy of <paramref name="mesh"/> with the fully protected parts dropped — "hide by mask".
	///
	/// A face goes only when EVERY corner of it is protected past the threshold. Dropping a face
	/// because one corner was masked would eat the boundary of every mask, so the visible edge would
	/// creep inward each time it was used.
	///
	/// Vertices are kept in place rather than compacted, so a vertex index still means the same
	/// vertex — a hidden mesh is for looking at, and anything else here is indexed per vertex.
	/// </summary>
	public PolyMesh Hide( PolyMesh mesh, float threshold = 0.5f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( mesh.VertexCount != _values.Length )
			throw new ArgumentException(
				$"This mask has {_values.Length} values and the mesh has {mesh.VertexCount} vertices." );

		var result = new PolyMesh();

		foreach ( var p in mesh.Positions )
			result.Positions.Add( p );

		foreach ( var face in mesh.Faces )
		{
			var allProtected = true;

			foreach ( var index in face.Indices )
			{
				if ( _values[index] > 1f - threshold )
				{
					allProtected = false;
					break;
				}
			}

			if ( allProtected )
				continue;

			result.Faces.Add( new Face( (int[])face.Indices.Clone(), (Vec2[])face.UVs.Clone(), face.Material ) );
		}

		return result;
	}
}
