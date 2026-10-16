using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Shape keys (<see cref="PolyMesh.Morphs"/>) in an FBX: the BlendShape deformer, one
/// BlendShapeChannel per key and the Shape geometry each channel points at - what Blender, Maya and
/// ufbx (s&amp;box's fbx2dmx) read as blendshapes / morph targets.
///
/// THE LAYOUT IS FBX 7.4's, as Blender's own exporter writes it (io_scene_fbx,
/// fbx_data_mesh_shapes_elements):
///
///   Geometry: id, "Geometry::name", "Shape" { Version: 100; Indexes; Vertices; Normals }
///       Indexes are control points of the base mesh; Vertices and Normals are OFFSETS from the
///       base, sparse - only the points the key moves.
///   Deformer: id, "Deformer::model", "BlendShape" { Version: 100 }
///   Deformer: id, "SubDeformer::name", "BlendShapeChannel" { Version: 100; DeformPercent: 0.0;
///       FullWeights: *1 { a: 100 } }
///   C: "OO", shape, channel; C: "OO", channel, blendshape; C: "OO", blendshape, mesh geometry.
///
/// FullWeights 100 means the Shape is the key at full strength; DeformPercent 0 that the file is
/// saved with every key off. Nothing is written for a mesh without keys, so every other FBX is
/// byte-identical.
/// </summary>
internal sealed class FbxBlendShapes
{
	/// <summary>Offsets smaller than this are left out of the sparse arrays (DmxMorphs' threshold).</summary>
	const float Epsilon = 1e-5f;

	readonly List<MeshMorph> _morphs;
	readonly long _blendShapeId;
	readonly long[] _channelIds;
	readonly long[] _shapeIds;

	public int Count => _morphs.Count;

	FbxBlendShapes( List<MeshMorph> morphs, long blendShapeId, long[] channelIds, long[] shapeIds )
	{
		_morphs = morphs;
		_blendShapeId = blendShapeId;
		_channelIds = channelIds;
		_shapeIds = shapeIds;
	}

	/// <summary>Ids for the mesh's keys, or null when it has none.</summary>
	public static FbxBlendShapes Plan( FbxText w, PolyMesh mesh )
	{
		var morphs = mesh.ValidMorphs.ToList();

		if ( morphs.Count == 0 )
			return null;

		var blendShape = w.NextId();
		var channels = new long[morphs.Count];
		var shapes = new long[morphs.Count];

		for ( var i = 0; i < morphs.Count; i++ )
		{
			channels[i] = w.NextId();
			shapes[i] = w.NextId();
		}

		return new FbxBlendShapes( morphs, blendShape, channels, shapes );
	}

	/// <summary>The Shape geometries and the deformers, inside Objects.</summary>
	public void WriteObjects( FbxText w, PolyMesh mesh, string modelName )
	{
		// Offsets of the area-weighted vertex normals, which is what a control-point normal is
		// (FBX's Shape normals are per control point, not per corner).
		var baseNormals = SkinStrain.VertexNormals( mesh.Positions, mesh.Faces );

		for ( var m = 0; m < _morphs.Count; m++ )
		{
			var morph = _morphs[m];
			var shaped = mesh.Positions.Select( ( p, i ) => p + morph.Deltas[i] ).ToList();
			var shapedNormals = SkinStrain.VertexNormals( shaped, mesh.Faces );

			var indexes = new List<long>();
			var offsets = new List<double>();
			var normals = new List<double>();

			for ( var i = 0; i < morph.Deltas.Length; i++ )
			{
				var d = morph.Deltas[i];
				var n = shapedNormals[i] - baseNormals[i];

				if ( d.Length <= Epsilon && n.Length <= Epsilon )
					continue;

				indexes.Add( i );
				offsets.Add( d.x ); offsets.Add( d.y ); offsets.Add( d.z );
				normals.Add( n.x ); normals.Add( n.y ); normals.Add( n.z );
			}

			w.Open( $"Geometry: {_shapeIds[m]}, \"Geometry::{Escape( morph.Name )}\", \"Shape\" " );
			w.Line( "Version: 100" );
			w.IndexArray( "Indexes", indexes );
			w.NumberArray( "Vertices", offsets );
			w.NumberArray( "Normals", normals );
			w.Close();
		}

		w.Open( $"Deformer: {_blendShapeId}, \"Deformer::{Escape( modelName )}\", \"BlendShape\" " );
		w.Line( "Version: 100" );
		w.Close();

		for ( var m = 0; m < _morphs.Count; m++ )
		{
			w.Open( $"Deformer: {_channelIds[m]}, \"SubDeformer::{Escape( _morphs[m].Name )}\", \"BlendShapeChannel\" " );
			w.Line( "Version: 100" );
			w.Line( "DeformPercent: 0.0" );
			w.NumberArray( "FullWeights", new List<double> { 100.0 } );
			w.Close();
		}
	}

	/// <summary>Shape → channel → blend shape → the mesh's geometry.</summary>
	public void WriteConnections( FbxText w, long geometryId )
	{
		w.Line( $"C: \"OO\",{_blendShapeId},{geometryId} ; BlendShape -> Geometry" );

		for ( var m = 0; m < _morphs.Count; m++ )
		{
			w.Line( $"C: \"OO\",{_channelIds[m]},{_blendShapeId} ; BlendShapeChannel -> BlendShape" );
			w.Line( $"C: \"OO\",{_shapeIds[m]},{_channelIds[m]} ; Shape -> BlendShapeChannel" );
		}
	}

	static string Escape( string value ) =>
		string.IsNullOrEmpty( value ) ? "" : value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
}
