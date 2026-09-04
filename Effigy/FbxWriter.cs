using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// ASCII FBX 7.4 export — the format the model compiler is happiest with, because it does not
/// parse it itself.
///
/// WHY FBX AND NOT DMX. ModelDoc takes "FBX, DMX, OBJ, VOX". DmxWriter's header argues that DMX is
/// the only option on the grounds that "FBX is a binary format nobody should hand-write". That is
/// wrong twice over, and the engine ships the proof in bin/win64:
///
///   fbx2dmx.exe     the engine's own FBX importer
///   libfbxsdk.dll   Autodesk's official SDK, which reads ASCII FBX as happily as binary
///
/// So writing FBX means fbx2dmx produces the DMX, and the job of getting DMX exactly right stops
/// being ours. That is the entire argument for this file: not that FBX is a nicer format, but that
/// it hands a decades-hardened importer the work we were otherwise doing by hand from strings
/// scraped out of a DLL. DmxWriter still works and still ships — this is the path that does not
/// depend on our own reading of a format.
///
/// IT ALSO COMES WITH AN ORACLE. Any file this writes can be checked without the editor running:
///
///   fbx2dmx.exe -i out/sample_rigged.fbx -o check.dmx
///
/// A malformed FBX is not a bad-looking model, it is a file that does not load, so a render can
/// never validate this. The converter can, and it names what it did not understand.
///
/// WHAT SURVIVES THE TRIP that SMD could not carry: n-gons. FBX ends a polygon by negating the
/// last index rather than assuming three corners, so the quad cage Catmull-Clark needs leaves the
/// tool intact.
/// </summary>
public static class FbxWriter
{
	/// <summary>Engines index a fixed number of bones per vertex and four is effectively
	/// universal. Unlike DMX, FBX does not store a fixed stride — each cluster lists only the
	/// vertices it actually touches — but pruning to the same number keeps the two writers
	/// producing the same rig rather than two subtly different ones.</summary>
	public const int MaxInfluences = 4;

	/// <summary>7.4 is what Blender and Maya emit and what the SDK is most exercised on. Older
	/// 6.x has a different object model entirely; newer versions gain nothing here.</summary>
	private const int FbxVersion = 7400;

	public static void WriteFile(
		PolyMesh mesh,
		string path,
		Skeleton skeleton = null,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees,
		Func<int, string> materialName = null,
		string modelName = null )
	{
		File.WriteAllText( path, Write( mesh, skeleton, smoothingAngleDegrees, materialName, modelName ) );
	}

	public static string Write(
		PolyMesh mesh,
		Skeleton skeleton = null,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees,
		Func<int, string> materialName = null,
		string modelName = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		skeleton ??= Skeleton.SingleRoot();

		if ( skeleton.Count == 0 )
			throw new InvalidOperationException( "FBX needs at least one bone; use Skeleton.SingleRoot for a static model" );

		var skin = mesh.IsRigged ? mesh.Skin : SkinWeights.AllTo( mesh.VertexCount, 0 );
		materialName ??= slot => $"material_{slot}";
		modelName ??= "effigy_model";

		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, smoothingAngleDegrees );

		var w = new FbxText();

		// Ids are sequential from a fixed base rather than random, so two exports of the same model
		// are byte-identical and a diff shows what actually changed. Zero is reserved: it is the
		// scene root every top-level node connects to.
		var idGeometry = w.NextId();
		var idMeshModel = w.NextId();

		var slots = MaterialSlots( mesh );
		var materialIds = new long[slots.Count];

		for ( var i = 0; i < slots.Count; i++ )
			materialIds[i] = w.NextId();

		var boneModelIds = new long[skeleton.Count];
		var boneAttrIds = new long[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			boneModelIds[i] = w.NextId();
			boneAttrIds[i] = w.NextId();
		}

		var idSkin = w.NextId();
		var clusterIds = new long[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
			clusterIds[i] = w.NextId();

		var idPose = w.NextId();

		WriteHeader( w );
		WriteGlobalSettings( w );
		WriteDocuments( w );
		WriteDefinitions( w, slots.Count, skeleton.Count );

		w.Open( "Objects: " );
		{
			WriteGeometry( w, idGeometry, mesh, cornerNormals, normals, slots );
			WriteMeshModel( w, idMeshModel, modelName );

			for ( var i = 0; i < slots.Count; i++ )
				WriteMaterial( w, materialIds[i], materialName( slots[i] ) );

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				WriteLimbAttribute( w, boneAttrIds[i], skeleton.Bones[i] );
				WriteLimbModel( w, boneModelIds[i], skeleton.Bones[i] );
			}

			WriteSkinDeformer( w, idSkin, modelName );

			var clusterVertices = ClusterVertices( mesh, skin, skeleton );

			for ( var i = 0; i < skeleton.Count; i++ )
				WriteCluster( w, clusterIds[i], skeleton, i, clusterVertices[i] );

			WriteBindPose( w, idPose, idMeshModel, boneModelIds, skeleton );
		}
		w.Close();

		WriteConnections( w, idGeometry, idMeshModel, materialIds, boneModelIds, boneAttrIds,
			idSkin, clusterIds, skeleton );

		return w.ToString();
	}

	// --- header and scene-level blocks ------------------------------------------------------------

	static void WriteHeader( FbxText w )
	{
		w.Raw( "; FBX 7.4.0 project file" );
		w.Raw( "; Written by Effigy. Text rather than binary on purpose: a file that can be read," );
		w.Raw( "; diffed and pasted into a bug report is worth more here than a smaller one." );
		w.Raw( "" );

		w.Open( "FBXHeaderExtension: " );
		w.Line( "FBXHeaderVersion: 1003" );
		w.Line( $"FBXVersion: {FbxVersion}" );

		// The SDK reads this block but nothing downstream depends on the values, and a real clock
		// reading would make two exports of the same model differ. A fixed stamp keeps them equal.
		w.Open( "CreationTimeStamp: " );
		w.Line( "Version: 1000" );
		w.Line( "Year: 1970" );
		w.Line( "Month: 1" );
		w.Line( "Day: 1" );
		w.Line( "Hour: 0" );
		w.Line( "Minute: 0" );
		w.Line( "Second: 0" );
		w.Line( "Millisecond: 0" );
		w.Close();

		w.Line( "Creator: \"Effigy\"" );
		w.Close();

		w.Raw( "" );
	}

	/// <summary>
	/// The axis system and units, stated rather than left to the importer to guess.
	///
	/// Effigy builds in Source convention — +x forward, +y left, +z up, one unit one inch — so up
	/// is Z (axis 2) and front is -Y. Declaring it means fbx2dmx converts if it wants a different
	/// handedness, instead of silently reading the numbers as though they were already its own.
	/// </summary>
	static void WriteGlobalSettings( FbxText w )
	{
		w.Open( "GlobalSettings: " );
		w.Line( "Version: 1000" );
		w.Open( "Properties70: " );
		w.Line( "P: \"UpAxis\", \"int\", \"Integer\", \"\",2" );
		w.Line( "P: \"UpAxisSign\", \"int\", \"Integer\", \"\",1" );
		w.Line( "P: \"FrontAxis\", \"int\", \"Integer\", \"\",1" );
		w.Line( "P: \"FrontAxisSign\", \"int\", \"Integer\", \"\",-1" );
		w.Line( "P: \"CoordAxis\", \"int\", \"Integer\", \"\",0" );
		w.Line( "P: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1" );
		w.Line( "P: \"OriginalUpAxis\", \"int\", \"Integer\", \"\",2" );
		w.Line( "P: \"OriginalUpAxisSign\", \"int\", \"Integer\", \"\",1" );
		w.Line( "P: \"UnitScaleFactor\", \"double\", \"Number\", \"\",1" );
		w.Line( "P: \"OriginalUnitScaleFactor\", \"double\", \"Number\", \"\",1" );
		w.Close();
		w.Close();
		w.Raw( "" );
	}

	/// <summary>
	/// The scene document, and the empty References block that follows it.
	///
	/// THIS BLOCK IS NOT OPTIONAL AND ITS ABSENCE IS SILENT. A file without it parses perfectly and
	/// imports as an empty scene: every Objects entry is read, every Connection is read, and then
	/// none of it is attached to anything, because `C: "OO", node, 0` names a root node that the
	/// document is what declares. The first version of this writer left it out and fbx2dmx produced
	/// a DmeModel with an empty children array, an empty jointList and no mesh — no warning, no
	/// error, exit code zero.
	/// </summary>
	static void WriteDocuments( FbxText w )
	{
		var idScene = w.NextId();

		w.Open( "Documents: " );
		w.Line( "Count: 1" );
		w.Open( $"Document: {idScene}, \"\", \"Scene\" " );
		w.Open( "Properties70: " );
		w.Line( "P: \"SourceObject\", \"object\", \"\", \"\"" );
		w.Line( "P: \"ActiveAnimStackName\", \"KString\", \"\", \"\", \"\"" );
		w.Close();
		w.Line( "RootNode: 0" );
		w.Close();
		w.Close();
		w.Raw( "" );

		w.Open( "References: " );
		w.Close();
		w.Raw( "" );
	}

	/// <summary>
	/// A count of what follows, by type. The SDK preallocates from this and tolerates it being
	/// wrong, but a file whose Definitions disagree with its Objects is the kind of thing that
	/// loads in one importer and not the next, so it is written honestly.
	/// </summary>
	static void WriteDefinitions( FbxText w, int materialCount, int boneCount )
	{
		// One Model per bone plus the mesh's; one NodeAttribute per bone; one Skin plus one
		// Cluster per bone; one Geometry; one Pose; GlobalSettings itself.
		var total = 1 + (boneCount + 1) + boneCount + materialCount + (1 + boneCount) + 1 + 1;

		w.Open( "Definitions: " );
		w.Line( "Version: 100" );
		w.Line( $"Count: {total}" );

		ObjectType( w, "GlobalSettings", 1 );
		ObjectType( w, "Model", boneCount + 1 );
		ObjectType( w, "NodeAttribute", boneCount );
		ObjectType( w, "Geometry", 1 );
		ObjectType( w, "Material", materialCount );
		ObjectType( w, "Deformer", boneCount + 1 );
		ObjectType( w, "Pose", 1 );

		w.Close();
		w.Raw( "" );
	}

	static void ObjectType( FbxText w, string name, int count )
	{
		w.Open( $"ObjectType: \"{name}\", " );
		w.Line( $"Count: {count}" );
		w.Close();
	}

	// --- geometry ---------------------------------------------------------------------------------

	/// <summary>
	/// The mesh: shared positions, an n-gon-capable index list, per-corner normals and UVs, and a
	/// material index per polygon.
	/// </summary>
	static void WriteGeometry( FbxText w, long id, PolyMesh mesh, int[][] cornerNormals,
		List<Vec3> normals, List<int> slots )
	{
		w.Open( $"Geometry: {id}, \"Geometry::mesh\", \"Mesh\" " );

		var vertices = new List<double>( mesh.VertexCount * 3 );

		foreach ( var p in mesh.Positions )
		{
			vertices.Add( p.x );
			vertices.Add( p.y );
			vertices.Add( p.z );
		}

		w.NumberArray( "Vertices", vertices );

		// POLYGON BOUNDARIES ARE ENCODED IN THE SIGN. There is no per-face corner count anywhere
		// in an FBX; the last index of each polygon is bitwise-negated (~i, i.e. -i - 1) and that
		// is the only thing marking where one face ends. Getting it wrong does not fail to parse,
		// it produces a single enormous polygon.
		var polygonIndices = new List<long>();
		var normalValues = new List<double>();
		var uvValues = new List<double>();
		var uvIndices = new List<long>();
		var materialIndices = new List<long>();

		var slotToDense = new Dictionary<int, int>();

		for ( var i = 0; i < slots.Count; i++ )
			slotToDense[slots[i]] = i;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			for ( var c = 0; c < face.Count; c++ )
			{
				var index = face.Indices[c];
				polygonIndices.Add( c == face.Count - 1 ? ~(long)index : index );

				var n = normals[cornerNormals[fi][c]];
				normalValues.Add( n.x );
				normalValues.Add( n.y );
				normalValues.Add( n.z );

				var uv = face.UVs is not null && c < face.UVs.Length ? face.UVs[c] : default;
				uvIndices.Add( uvValues.Count / 2 );

				// FBX puts the UV origin at the bottom-left where Effigy's is top-left, so V is
				// flipped on the way out. This is the same flip DmxWriter expresses by leaving
				// flipVCoordinates off and writing V as-is — the two formats simply disagree about
				// which end of the texture is zero.
				uvValues.Add( uv.x );
				uvValues.Add( 1.0 - uv.y );
			}

			materialIndices.Add( slotToDense.TryGetValue( face.Material, out var dense ) ? dense : 0 );
		}

		w.IndexArray( "PolygonVertexIndex", polygonIndices );
		w.Line( "GeometryVersion: 124" );

		w.Open( "LayerElementNormal: 0 " );
		w.Line( "Version: 102" );
		w.Line( "Name: \"\"" );
		w.Line( "MappingInformationType: \"ByPolygonVertex\"" );
		w.Line( "ReferenceInformationType: \"Direct\"" );
		w.NumberArray( "Normals", normalValues );
		w.Close();

		w.Open( "LayerElementUV: 0 " );
		w.Line( "Version: 101" );
		w.Line( "Name: \"UVMap\"" );
		w.Line( "MappingInformationType: \"ByPolygonVertex\"" );
		w.Line( "ReferenceInformationType: \"IndexToDirect\"" );
		w.NumberArray( "UV", uvValues );
		w.IndexArray( "UVIndex", uvIndices );
		w.Close();

		// ByPolygon, not ByPolygonVertex: a face belongs to exactly one slot in Effigy, which is
		// also what makes clicking a face to assign a material meaningful.
		w.Open( "LayerElementMaterial: 0 " );
		w.Line( "Version: 101" );
		w.Line( "Name: \"\"" );
		w.Line( "MappingInformationType: \"ByPolygon\"" );
		w.Line( "ReferenceInformationType: \"IndexToDirect\"" );
		w.IndexArray( "Materials", materialIndices );
		w.Close();

		// The Layer is what actually turns the three LayerElement blocks above into channels the
		// importer reads. Without it they are present and ignored.
		w.Open( "Layer: 0 " );
		w.Line( "Version: 100" );
		LayerElement( w, "LayerElementNormal" );
		LayerElement( w, "LayerElementUV" );
		LayerElement( w, "LayerElementMaterial" );
		w.Close();

		w.Close();
	}

	static void LayerElement( FbxText w, string type )
	{
		w.Open( "LayerElement: " );
		w.Line( $"Type: \"{type}\"" );
		w.Line( "TypedIndex: 0" );
		w.Close();
	}

	static void WriteMeshModel( FbxText w, long id, string modelName )
	{
		w.Open( $"Model: {id}, \"Model::{Escape( modelName )}\", \"Mesh\" " );
		w.Line( "Version: 232" );
		w.Open( "Properties70: " );
		w.Line( "P: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",0,0,0" );
		w.Line( "P: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",0,0,0" );
		w.Line( "P: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",1,1,1" );
		w.Close();
		w.Line( "Shading: T" );
		w.Line( "Culling: \"CullingOff\"" );
		w.Close();
	}

	static void WriteMaterial( FbxText w, long id, string name )
	{
		w.Open( $"Material: {id}, \"Material::{Escape( name )}\", \"\" " );
		w.Line( "Version: 102" );
		w.Line( "ShadingModel: \"lambert\"" );
		w.Line( "MultiLayer: 0" );
		w.Open( "Properties70: " );
		w.Line( "P: \"DiffuseColor\", \"Color\", \"\", \"A\",0.8,0.8,0.8" );
		w.Close();
		w.Close();
	}

	// --- skeleton ---------------------------------------------------------------------------------

	/// <summary>A bone is two objects in FBX: a Model that carries the transform and sits in the
	/// node hierarchy, and a NodeAttribute that says the Model is a bone rather than a null.
	/// Omitting the attribute gives you a rig of empties that still deform, which then imports
	/// somewhere else as no skeleton at all.</summary>
	static void WriteLimbAttribute( FbxText w, long id, Bone bone )
	{
		w.Open( $"NodeAttribute: {id}, \"NodeAttribute::{Escape( bone.Name )}\", \"LimbNode\" " );
		w.Line( "TypeFlags: \"Skeleton\"" );
		w.Open( "Properties70: " );
		w.Line( $"P: \"Size\", \"double\", \"Number\", \"\",{Number( bone.Length )}" );
		w.Close();
		w.Close();
	}

	static void WriteLimbModel( FbxText w, long id, Bone bone )
	{
		var (translation, rotationDegrees, scale) = Decompose( bone.Local );

		w.Open( $"Model: {id}, \"Model::{Escape( bone.Name )}\", \"LimbNode\" " );
		w.Line( "Version: 232" );
		w.Open( "Properties70: " );
		w.Line( $"P: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",{Number( translation.x )},{Number( translation.y )},{Number( translation.z )}" );
		w.Line( $"P: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",{Number( rotationDegrees.x )},{Number( rotationDegrees.y )},{Number( rotationDegrees.z )}" );
		w.Line( $"P: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",{Number( scale.x )},{Number( scale.y )},{Number( scale.z )}" );
		w.Close();
		w.Line( "Shading: T" );
		w.Line( "Culling: \"CullingOff\"" );
		w.Close();
	}

	// --- skinning ---------------------------------------------------------------------------------

	static void WriteSkinDeformer( FbxText w, long id, string modelName )
	{
		w.Open( $"Deformer: {id}, \"Deformer::Skin {Escape( modelName )}\", \"Skin\" " );
		w.Line( "Version: 101" );
		w.Line( "Link_DeformAcuracy: 50" );
		w.Close();
	}

	/// <summary>
	/// One cluster per bone, holding only the vertices that bone actually touches.
	///
	/// This is where FBX differs most from DMX: DMX stores a fixed four influences on every vertex
	/// and pads the unused ones with zero-weight entries, while FBX inverts the relationship and
	/// lists, per bone, which vertices it moves. The pruning happens before either, so both formats
	/// describe the same rig.
	///
	/// TransformLink is the bone's world transform at bind time; Transform is the mesh's, which is
	/// identity here because the mesh model sits at the origin. The importer derives the inverse
	/// bind from the pair, so writing one and not the other silently bakes the bind pose in.
	/// </summary>
	static void WriteCluster( FbxText w, long id, Skeleton skeleton, int bone, List<(int Vertex, float Weight)> influences )
	{
		w.Open( $"Deformer: {id}, \"SubDeformer::Cluster {Escape( skeleton.Bones[bone].Name )}\", \"Cluster\" " );
		w.Line( "Version: 100" );
		w.Line( "UserData: \"\", \"\"" );

		// A cluster with no vertices is still written. Dropping it would leave the bone out of the
		// skin entirely, so it would not appear in the compiled skeleton and anything parented to
		// it in the editor would lose its target.
		var indices = new List<long>( influences.Count );
		var weights = new List<double>( influences.Count );

		foreach ( var (vertex, weight) in influences )
		{
			indices.Add( vertex );
			weights.Add( weight );
		}

		w.IndexArray( "Indexes", indices );
		w.NumberArray( "Weights", weights );
		w.NumberArray( "Transform", MatrixValues( Xform.Identity ) );
		w.NumberArray( "TransformLink", MatrixValues( skeleton.WorldBind( bone ) ) );

		w.Close();
	}

	/// <summary>Per-bone vertex lists, built by inverting the per-vertex influence lists once so
	/// the cluster writer does not walk the whole skin per bone.</summary>
	static List<(int Vertex, float Weight)>[] ClusterVertices( PolyMesh mesh, SkinWeights skin, Skeleton skeleton )
	{
		var byBone = new List<(int, float)>[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
			byBone[i] = new List<(int, float)>();

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var influences = v < skin.Count ? skin[v] : Array.Empty<BoneWeight>();

			foreach ( var (boneIndex, weight) in Prune( influences, skeleton.Count ) )
				byBone[boneIndex].Add( (v, weight) );
		}

		return byBone;
	}

	/// <summary>Strongest influences first, capped at MaxInfluences, renormalised, and with
	/// anything pointing outside the skeleton dropped rather than written out to be looked up.
	/// Deliberately the same rule DmxWriter applies, so the two exports rig identically.</summary>
	static List<(int Bone, float Weight)> Prune( BoneWeight[] influences, int boneCount )
	{
		var kept = new List<(int Bone, float Weight)>( MaxInfluences );

		foreach ( var influence in influences )
		{
			if ( influence.Bone < 0 || influence.Bone >= boneCount || influence.Weight <= 0f )
				continue;

			kept.Add( (influence.Bone, influence.Weight) );
		}

		kept.Sort( ( a, b ) => b.Weight.CompareTo( a.Weight ) );

		if ( kept.Count > MaxInfluences )
			kept.RemoveRange( MaxInfluences, kept.Count - MaxInfluences );

		var total = 0f;

		foreach ( var influence in kept )
			total += influence.Weight;

		if ( kept.Count == 0 || total <= 0f )
			return new List<(int, float)> { (0, 1f) };

		for ( var i = 0; i < kept.Count; i++ )
			kept[i] = (kept[i].Bone, kept[i].Weight / total);

		return kept;
	}

	/// <summary>
	/// The bind pose as its own record, in world space, for every node the skin touches.
	///
	/// This duplicates what the clusters already say. FBX wants it anyway: importers that do not
	/// read cluster matrices read this instead, and the two disagreeing is a classic source of a
	/// model that binds correctly in one tool and folds in half in another.
	/// </summary>
	static void WriteBindPose( FbxText w, long id, long meshModelId, long[] boneModelIds, Skeleton skeleton )
	{
		w.Open( $"Pose: {id}, \"Pose::BIND_POSES\", \"BindPose\" " );
		w.Line( "Type: \"BindPose\"" );
		w.Line( "Version: 100" );
		w.Line( $"NbPoseNodes: {skeleton.Count + 1}" );

		w.Open( "PoseNode: " );
		w.Line( $"Node: {meshModelId}" );
		w.NumberArray( "Matrix", MatrixValues( Xform.Identity ) );
		w.Close();

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			w.Open( "PoseNode: " );
			w.Line( $"Node: {boneModelIds[i]}" );
			w.NumberArray( "Matrix", MatrixValues( skeleton.WorldBind( i ) ) );
			w.Close();
		}

		w.Close();
	}

	// --- connections ------------------------------------------------------------------------------

	/// <summary>
	/// What actually assembles the file.
	///
	/// Every object written above is inert until it is wired in here — a mesh with no connection to
	/// a model is not an error, it is a mesh that does not appear. That makes a missing connection
	/// the worst failure mode in the format: silent omission rather than a parse error. Each one
	/// below is listed with the direction it needs, because "OO" is child-then-parent and reversing
	/// a pair is exactly as silent.
	/// </summary>
	static void WriteConnections( FbxText w, long geometryId, long meshModelId, long[] materialIds,
		long[] boneModelIds, long[] boneAttrIds, long skinId, long[] clusterIds, Skeleton skeleton )
	{
		w.Open( "Connections: " );

		// The mesh node hangs off the scene root, which is always id 0.
		Connect( w, meshModelId, 0, $"Model::{skeleton.Count} bone rig -> scene root" );
		Connect( w, geometryId, meshModelId, "Geometry -> Model" );

		foreach ( var materialId in materialIds )
			Connect( w, materialId, meshModelId, "Material -> Model" );

		// Bones: attribute to its model, model to its parent's model, roots to the scene root.
		for ( var i = 0; i < skeleton.Count; i++ )
		{
			Connect( w, boneAttrIds[i], boneModelIds[i], "NodeAttribute -> Model" );

			var parent = skeleton.Bones[i].Parent;
			Connect( w, boneModelIds[i], parent < 0 ? 0 : boneModelIds[parent], "Model -> parent" );
		}

		// The skin sits on the GEOMETRY, not the model, and each cluster on the skin.
		Connect( w, skinId, geometryId, "Deformer -> Geometry" );

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			Connect( w, clusterIds[i], skinId, "SubDeformer -> Deformer" );

			// And the bone connects INTO the cluster, not the other way round: the limb is the
			// cluster's source. This is the one pair that reads backwards to most people.
			Connect( w, boneModelIds[i], clusterIds[i], "Model -> SubDeformer" );
		}

		w.Close();
	}

	static void Connect( FbxText w, long child, long parent, string comment )
	{
		w.Line( $"C: \"OO\",{child},{parent} ; {comment}" );
	}

	// --- shared helpers ---------------------------------------------------------------------------

	/// <summary>The material slots the mesh actually uses, in ascending order. A dense list, so a
	/// mesh using slots 0 and 7 writes two materials rather than eight.</summary>
	static List<int> MaterialSlots( PolyMesh mesh )
	{
		var used = new SortedSet<int>();

		foreach ( var face in mesh.Faces )
			used.Add( face.Material );

		if ( used.Count == 0 )
			used.Add( 0 );

		return new List<int>( used );
	}

	/// <summary>
	/// An Xform as the 16 doubles FBX stores a matrix in: row-major with the translation last,
	/// which is the same layout Xform already has (X, Y and Z are the images of the unit axes, so
	/// they are the rows here, and Origin is the fourth).
	/// </summary>
	static List<double> MatrixValues( Xform x ) => new()
	{
		x.X.x, x.X.y, x.X.z, 0.0,
		x.Y.x, x.Y.y, x.Y.z, 0.0,
		x.Z.x, x.Z.y, x.Z.z, 0.0,
		x.Origin.x, x.Origin.y, x.Origin.z, 1.0,
	};

	/// <summary>
	/// An Xform split into the translation, XYZ Euler in DEGREES and scale that FBX's Lcl
	/// properties want.
	///
	/// The Euler conversion is Xform's own, which composes Rz*Ry*Rx — the same order FBX's default
	/// eEulerXYZ uses — and which has a branch for gimbal lock, where the generic formula collapses.
	/// That matters here and is not hypothetical: rig root bones routinely sit at exactly a quarter
	/// turn, which is the locked case.
	/// </summary>
	static (Vec3 Translation, Vec3 RotationDegrees, Vec3 Scale) Decompose( Xform x )
	{
		var sx = x.X.Length;
		var sy = x.Y.Length;
		var sz = x.Z.Length;

		// A zero-length axis cannot be normalised and cannot be a rotation either; fall back to
		// unit rather than producing NaNs that spread through the whole file.
		var rotation = new Xform(
			sx > 1e-8f ? x.X / sx : new Vec3( 1, 0, 0 ),
			sy > 1e-8f ? x.Y / sy : new Vec3( 0, 1, 0 ),
			sz > 1e-8f ? x.Z / sz : new Vec3( 0, 0, 1 ),
			Vec3.Zero );

		var radians = rotation.ToEulerXyz();
		var toDegrees = 180f / MathF.PI;

		return (x.Origin,
			new Vec3( radians.x * toDegrees, radians.y * toDegrees, radians.z * toDegrees ),
			new Vec3( sx > 1e-8f ? sx : 1f, sy > 1e-8f ? sy : 1f, sz > 1e-8f ? sz : 1f ));
	}

	static string Number( float v ) => v.ToString( "0.######", CultureInfo.InvariantCulture );

	static string Escape( string value ) =>
		string.IsNullOrEmpty( value ) ? "" : value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
}

/// <summary>
/// The ASCII FBX 7.x block syntax, which is a plain nested `Name: args {` / `}` tree with one
/// special case: an array is written as `*count {` followed by a single `a:` line of values.
/// </summary>
internal sealed class FbxText
{
	private readonly StringBuilder _sb = new();
	private int _depth;
	private long _nextId = 1000000;

	/// <summary>Object ids are int64 and only have to be unique within the file. Counting from a
	/// fixed base keeps two exports of the same model byte-identical; zero is reserved for the
	/// scene root.</summary>
	public long NextId() => _nextId++;

	public void Raw( string text ) => _sb.Append( text ).Append( '\n' );

	public void Line( string text )
	{
		Indent();
		_sb.Append( text ).Append( '\n' );
	}

	public void Open( string header )
	{
		Indent();
		_sb.Append( header ).Append( "{\n" );
		_depth++;
	}

	public void Close()
	{
		_depth--;
		Indent();
		_sb.Append( "}\n" );
	}

	public void NumberArray( string name, List<double> values )
	{
		Array( name, values.Count, () =>
		{
			for ( var i = 0; i < values.Count; i++ )
			{
				if ( i > 0 )
					_sb.Append( ',' );

				_sb.Append( values[i].ToString( "0.######", CultureInfo.InvariantCulture ) );
			}
		} );
	}

	public void IndexArray( string name, List<long> values )
	{
		Array( name, values.Count, () =>
		{
			for ( var i = 0; i < values.Count; i++ )
			{
				if ( i > 0 )
					_sb.Append( ',' );

				_sb.Append( values[i].ToString( CultureInfo.InvariantCulture ) );
			}
		} );
	}

	/// <summary>
	/// `Name: *count { a: v,v,v }`. The count is part of the syntax, not a hint — the SDK
	/// allocates from it, so a count that disagrees with the number of values is a corrupt file
	/// rather than a slow one. Writing it from the list's own Count is what keeps them equal.
	/// </summary>
	void Array( string name, int count, Action writeValues )
	{
		Indent();
		_sb.Append( name ).Append( ": *" ).Append( count.ToString( CultureInfo.InvariantCulture ) ).Append( " {\n" );
		_depth++;

		Indent();
		_sb.Append( "a: " );
		writeValues();
		_sb.Append( '\n' );

		_depth--;
		Indent();
		_sb.Append( "}\n" );
	}

	void Indent() => _sb.Append( '\t', _depth );

	public override string ToString() => _sb.ToString();
}
