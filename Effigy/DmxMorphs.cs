using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Shape keys (<see cref="PolyMesh.Morphs"/>) in a model DMX: delta states on the DmeMesh and a
/// combination operator on the root, so ModelDoc compiles each one into a morph (flex controller)
/// that Marionette's morph curves, a .vmdl flex channel or SkinnedModelRenderer.Morphs can drive.
///
/// COPIED FROM FACEPUNCH'S OWN FILE, NOT GUESSED: addons/citizen/Assets/models/citizen/citizen_head.dmx
/// (KeyValues2, exported from 3ds Max, 53 morphs). What it settled:
///
///   - each DmeMesh carries "deltaStates", an element_array of DmeVertexDeltaData, one per morph,
///     named for it: vertexFormat [position$0, normal$0], jointCount 0, flipVCoordinates 1,
///     corrected 1, then SPARSE values - position$0 with position$0Indices into the base state's
///     position$0 array, normal$0 with normal$0Indices into its normal$0 array;
///   - the DmeMesh also has "deltaStateWeights" and "deltaStateWeightsLagged", vector2_array, one
///     "0 0" per delta state;
///   - the ROOT DmElement has "combinationOperator", a DmeCombinationOperator: "controls", one
///     DmeCombinationInputControl per morph {rawControlNames [name], stereo 0, eyelid 0,
///     wrinkleScales [0]}; "controlValues" and "controlValuesLagged", vector3_array "0 0.5 0.5" per
///     control; "useLaggedValues" 0; "targets", the meshes that carry the deltas.
///
/// Nothing here is written for a mesh without morphs, so every other export is byte-identical.
///
/// WRINKLE DATA (opt-in, see <see cref="WrinkleMaps"/>): a key with wrinkle values gets "wrinkle$0" in
/// its vertexFormat, a sparse float_array "wrinkle$0" with "wrinkle$0Indices" into the base
/// position$0 array, and wrinkleScales [1] on its control. The per-control wrinkleScales is in
/// citizen_head.dmx; the per-vertex stream's name is NOT read from a Facepunch file - it follows the
/// "&lt;field&gt;$0" naming of this format's other streams, and older Source DMX names the same field
/// "wrinkle"/"wrinkleIndices" (DmeVertexDeltaData). Whether ModelDoc reads it is an in-engine check.
/// Values run −1 (squashed: TextureSquish*) to +1 (stretched: TextureStretch*), the sign s&amp;box's
/// skinning_cs gives F_WRINKLE (realtime-flesh-shading.md §5).
/// </summary>
public static class DmxMorphs
{
	/// <summary>Offsets smaller than this are left out of the sparse arrays.</summary>
	const float Epsilon = 1e-5f;

	/// <summary>The delta states and their weights, inside the DmeMesh element. <paramref name="cornerNormals"/>
	/// and <paramref name="normals"/> are the base state's, as the writer wrote them.</summary>
	internal static void WriteDeltaStates( DmxText w, PolyMesh mesh, int[][] cornerNormals, List<Vec3> normals,
		IReadOnlyDictionary<string, float[]> wrinkles = null )
	{
		var morphs = mesh.ValidMorphs.ToList();

		if ( morphs.Count == 0 )
			return;

		var baseNormals = GroupNormals( mesh, mesh.Positions, cornerNormals, normals.Count );

		w.OpenArray( "deltaStates", "element_array" );

		foreach ( var morph in morphs )
		{
			w.OpenArrayElement( "DmeVertexDeltaData", w.NextId(), morph.Name );

			var wrinkle = WrinkleFor( wrinkles, morph.Name, mesh.VertexCount );

			w.OpenArray( "vertexFormat", "string_array" );
			w.ArrayValue( "position$0" );
			w.ArrayValue( "normal$0" );
			if ( wrinkle is not null )
				w.ArrayValue( "wrinkle$0" );
			w.CloseArray();

			w.Attribute( "jointCount", "int", "0" );
			w.Attribute( "flipVCoordinates", "bool", "1" );
			w.Attribute( "corrected", "bool", "1" );

			var moved = Enumerable.Range( 0, morph.Deltas.Length ).Where( i => morph.Deltas[i].Length > Epsilon ).ToList();

			w.OpenArray( "position$0", "vector3_array" );

			foreach ( var i in moved )
				w.ArrayValue( DmxText.Vector3( morph.Deltas[i] ) );

			w.CloseArray();
			w.IntArray( "position$0Indices", moved );

			// Normals: each of the base's normal groups re-averaged over the same corners with the
			// morph applied, so the groups (and hard edges) are the base's exactly.
			var shaped = mesh.Positions.Select( ( p, i ) => p + morph.Deltas[i] ).ToList();
			var after = GroupNormals( mesh, shaped, cornerNormals, normals.Count );
			var turned = Enumerable.Range( 0, normals.Count ).Where( n => (after[n] - baseNormals[n]).Length > Epsilon ).ToList();

			w.OpenArray( "normal$0", "vector3_array" );

			foreach ( var n in turned )
				w.ArrayValue( DmxText.Vector3( after[n] - baseNormals[n] ) );

			w.CloseArray();
			w.IntArray( "normal$0Indices", turned );

			if ( wrinkle is not null )
			{
				var wrinkled = Enumerable.Range( 0, wrinkle.Length ).Where( i => MathF.Abs( wrinkle[i] ) > WrinkleEpsilon ).ToList();
				w.FloatArray( "wrinkle$0", wrinkled.ConvertAll( i => Math.Clamp( wrinkle[i], -1f, 1f ) ) );
				w.IntArray( "wrinkle$0Indices", wrinkled );
			}

			w.CloseElement();
		}

		w.CloseArray();

		foreach ( var name in new[] { "deltaStateWeights", "deltaStateWeightsLagged" } )
		{
			w.OpenArray( name, "vector2_array" );

			foreach ( var _ in morphs )
				w.ArrayValue( "0 0" );

			w.CloseArray();
		}
	}

	/// <summary>The combination operator, on the root element: one control per morph, targeting
	/// the mesh with id <paramref name="meshId"/>.</summary>
	internal static void WriteCombinationOperator( DmxText w, PolyMesh mesh, string meshId,
		IReadOnlyDictionary<string, float[]> wrinkles = null )
	{
		var names = mesh.ValidMorphs.Select( m => m.Name ).ToList();

		if ( names.Count == 0 )
			return;

		w.OpenAttributeElement( "combinationOperator", "DmeCombinationOperator", w.NextId(), "" );

		w.OpenArray( "controls", "element_array" );

		foreach ( var name in names )
		{
			w.OpenArrayElement( "DmeCombinationInputControl", w.NextId(), name );
			w.OpenArray( "rawControlNames", "string_array" );
			w.ArrayValue( name );
			w.CloseArray();
			w.Attribute( "stereo", "bool", "0" );
			w.Attribute( "eyelid", "bool", "0" );
			w.OpenArray( "wrinkleScales", "float_array" );
			w.ArrayValue( WrinkleFor( wrinkles, name, mesh.VertexCount ) is null ? "0" : "1" );
			w.CloseArray();
			w.CloseElement();
		}

		w.CloseArray();

		foreach ( var values in new[] { "controlValues", "controlValuesLagged" } )
		{
			w.OpenArray( values, "vector3_array" );

			foreach ( var _ in names )
				w.ArrayValue( "0 0.5 0.5" );

			w.CloseArray();
		}

		w.Attribute( "useLaggedValues", "bool", "0" );

		w.OpenArray( "targets", "element_array" );
		w.ArrayReference( meshId );
		w.CloseArray();

		w.CloseElement();
	}

	/// <summary>Wrinkle values under this size are left out of the sparse array.</summary>
	const float WrinkleEpsilon = 1e-3f;

	/// <summary>The key's wrinkle values, when given and the right length.</summary>
	static float[] WrinkleFor( IReadOnlyDictionary<string, float[]> wrinkles, string name, int vertexCount ) =>
		wrinkles is not null && wrinkles.TryGetValue( name, out var values ) && values is not null && values.Length == vertexCount ? values : null;

	/// <summary>Each normal group's direction: the area-weighted normals of the faces whose corners
	/// use it, on <paramref name="positions"/>, normalised.</summary>
	static Vec3[] GroupNormals( PolyMesh mesh, IReadOnlyList<Vec3> positions, int[][] cornerNormals, int count )
	{
		var sum = new Vec3[count];

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			var n = Vec3.Zero;

			// Newell: an area-weighted normal for any polygon.
			for ( var c = 0; c < face.Count; c++ )
			{
				var a = positions[face.Indices[c]];
				var b = positions[face.Indices[(c + 1) % face.Count]];
				n += new Vec3( (a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y) );
			}

			for ( var c = 0; c < face.Count; c++ )
			{
				var g = cornerNormals[fi][c];

				if ( g >= 0 && g < count )
					sum[g] += n;
			}
		}

		return sum.Select( v => v.LengthSquared > 1e-20f ? v.Normal : v ).ToArray();
	}
}
