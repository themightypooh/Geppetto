using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// DMX export — the skinned path that ModelDoc will actually import.
///
/// WHY THIS EXISTS AND SMD DOES NOT WORK. SmdWriter was built on the documented claim that
/// ModelDoc imports "DMX, SMD, FBX, OBJ, VOX". It does not. Its own loader says:
///
///   LoadModelFile( "%s" ) Failed: Unknown/unsupported geometry/model format in specified
///   filename, Supported types: FBX, DMX, OBJ, VOX
///
/// - the string lives in bin/win64/modeldoc_utils.dll, which contains ".dmx", ".obj" and ".FBX"
/// and no ".smd" anywhere. Of the four, OBJ carries no bones and FBX is a binary format nobody
/// should hand-write, which leaves DMX as the only way to compile a rigged model.
///
/// THE ELEMENT NAMES WERE READ OUT OF THAT SAME BINARY rather than guessed from a format article:
/// DmeModel, DmeDag, DmeMesh, DmeVertexData, DmeFaceSet, DmeMaterial, DmeTransform,
/// DmeTransformList, DmeAxisSystem; the attributes vertexFormat, jointCount, flipVCoordinates,
/// faceSets, faces, mtlName, bindState, currentState, baseStates, jointList, transforms, upAxis,
/// forwardParity, coordSys. The compiler also states its own rules there, which this writer is
/// built to satisfy: "Incorrect number of joint weights or indices specified, must match number of
/// positions values" and "Cannot add vertex data block with different number of normal indices
/// (%d) and vertex indices (%d)".
///
/// AND THAT METHOD HAS A LIMIT, WHICH COST THIS FILE TWO ROUND TRIPS. A string in a DLL tells you a
/// name exists; it does not tell you where it goes or what punctuation surrounds it. Two things
/// were wrong for that reason and neither showed up as anything but "Couldn't load DMX file":
///
///   - the KeyValues2 punctuation (see DmxText.CloseElement and DmxText.ArrayReference);
///   - the vertex format field names, which are position$0 / normal$0 / texcoord$0 /
///     blendweights$0 / blendindices$0, not the plural spellings also present in that binary
///     (see WriteVertexData).
///
/// THE FIX FOR BOTH IS THE SAME AND IT IS ONE COMMAND. The engine ships bin/win64/fbx2dmx.exe and
/// dmxconvert.exe. The first turns any FBX in the project into a DMX the compiler definitely
/// loads, to diff against; the second reads a DMX and reports the first thing wrong with it, with
/// a line number, in about a second:
///
///   fbx2dmx.exe   -i Assets/models/first_person/fp_arms.fbx -o rig_ref.dmx
///   dmxconvert.exe -i export.dmx -o check.dmx
///
/// Do that before reasoning about this format. Effigy.Tests/DmxGrammarTests.cs is the same check
/// with no engine involved.
///
/// KEYVALUES2 TEXT, NOT BINARY. Binary DMX is denser and completely opaque when something is
/// wrong; a text file can be read, diffed and pasted into a bug report. Nothing here is on a hot
/// path — export is a bake.
///
/// WHAT IT CARRIES that SMD did not: n-gons, so the quad cage is not triangulated on the way out.
/// </summary>
public static class DmxWriter
{
	/// <summary>Engines index a fixed number of bones per vertex, and four is effectively
	/// universal. DMX stores a fixed stride, so this is also the array width.</summary>
	public const int MaxInfluences = 4;

	/// <summary>The "model" format version. 22 is the Source 2 era one; the compiler carries a
	/// CDmFormatUpdater_model for older files, so this is the version to write rather than the
	/// oldest one that might still be accepted.</summary>
	private const int ModelFormatVersion = 22;

	// The DmeVertexData field names, in the <semantic>$<set> spelling the compiler keys a vertex
	// format on. $0 is the first set of each; a second UV channel would be texcoord$1. Named here
	// rather than repeated as literals because each one is written twice — once into vertexFormat,
	// once as the array itself — and a pair that disagrees is a field the compiler silently
	// ignores rather than an error.
	private const string Position = "position$0";
	private const string Normal = "normal$0";
	private const string TexCoord = "texcoord$0";
	private const string BlendWeights = "blendweights$0";
	private const string BlendIndices = "blendindices$0";

	/// <summary>
	/// Normals that came with the mesh rather than being derived from it.
	///
	/// Effigy's own meshes have no authored normals — they are a quad cage, and recomputing from
	/// geometry at a smoothing angle is the right answer. Meshes that arrive from somewhere else
	/// often do: a game's exported character carries normals that were baked against its original
	/// high-poly, and recomputing them replaces shading the artist authored with shading derived
	/// from the low-poly silhouette. That is a visible downgrade, not a rounding difference, which
	/// is why this is a supported input rather than something a caller works around.
	/// </summary>
	public sealed class AuthoredNormals
	{
		/// <summary>Indexed [face][corner], each entry an index into <see cref="Values"/>.</summary>
		public int[][] CornerIndices;

		/// <summary>The distinct normals themselves, shared between corners that agree.</summary>
		public List<Vec3> Values;
	}

	public static void WriteFile(
		PolyMesh mesh,
		string path,
		Skeleton skeleton = null,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees,
		Func<int, string> materialName = null,
		string modelName = null,
		AuthoredNormals authoredNormals = null )
	{
		File.WriteAllText( path,
			Write( mesh, skeleton, smoothingAngleDegrees, materialName, modelName, authoredNormals ) );
	}

	public static string Write(
		PolyMesh mesh,
		Skeleton skeleton = null,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees,
		Func<int, string> materialName = null,
		string modelName = null,
		AuthoredNormals authoredNormals = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		skeleton ??= Skeleton.SingleRoot();

		if ( skeleton.Count == 0 )
			throw new InvalidOperationException( "DMX needs at least one bone; use Skeleton.SingleRoot for a static model" );

		var skin = mesh.IsRigged ? mesh.Skin : SkinWeights.AllTo( mesh.VertexCount, 0 );
		materialName ??= slot => $"material_{slot}";
		modelName ??= "effigy_model";

		var (cornerNormals, normals) = authoredNormals is null
			? MeshNormals.ComputeCornerNormals( mesh, smoothingAngleDegrees )
			: Validated( authoredNormals, mesh );

		var w = new DmxText();

		// Ids are sequential rather than random, so two exports of the same model are
		// byte-identical and a diff shows what actually changed.
		var idRoot = w.NextId();
		var idModel = w.NextId();
		var idModelTransform = w.NextId();

		var boneDagIds = new string[skeleton.Count];
		var boneTransformIds = new string[skeleton.Count];
		var bindTransformIds = new string[skeleton.Count];

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			boneDagIds[i] = w.NextId();
			boneTransformIds[i] = w.NextId();
			bindTransformIds[i] = w.NextId();
		}

		var idMeshDag = w.NextId();
		var idMeshTransform = w.NextId();
		var idMesh = w.NextId();
		var idVertexData = w.NextId();

		w.Raw( $"<!-- dmx encoding keyvalues2 1 format model {ModelFormatVersion} -->" );
		w.Raw( "" );

		w.OpenElement( "DmElement", idRoot, "root" );

		w.OpenAttributeElement( "skeleton", "DmeModel", idModel, modelName );
		{
			WriteTransform( w, "transform", idModelTransform, modelName, Xform.Identity );
			w.Attribute( "shape", "element", "" );
			w.Attribute( "visible", "bool", "1" );

			// children: the bone hierarchy's roots, then the mesh. A DmeDag is both a transform
			// node and a place to hang geometry, which is why bones and meshes share one list.
			w.OpenArray( "children", "element_array" );

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				if ( skeleton.Bones[i].Parent < 0 )
					WriteBoneDag( w, skeleton, i, boneDagIds, boneTransformIds );
			}

			WriteMeshDag( w, idMeshDag, idMeshTransform, idMesh, idVertexData,
				mesh, skin, skeleton, cornerNormals, normals, materialName );

			w.CloseArray();

			// jointList: what jointIndices in the vertex data index INTO. Written in skeleton
			// order, so a bone's index in Skeleton.Bones is its index here — the one invariant
			// that makes the weights mean anything.
			w.OpenArray( "jointList", "element_array" );

			for ( var i = 0; i < skeleton.Count; i++ )
				w.ArrayReference( boneDagIds[i] );

			w.CloseArray();

			// baseStates: the bind pose, as its own copy of every bone transform. It has to agree
			// with the dag hierarchy above; both are written from Bone.Local, once each.
			w.OpenArray( "baseStates", "element_array" );
			w.OpenArrayElement( "DmeTransformList", w.NextId(), "bind pose" );
			w.OpenArray( "transforms", "element_array" );

			for ( var i = 0; i < skeleton.Count; i++ )
				WriteTransformElement( w, bindTransformIds[i], skeleton.Bones[i].Name, skeleton.Bones[i].Local );

			w.CloseArray();
			w.CloseElement();
			w.CloseArray();

			// Z up, Y forward — the axes the kernel builds in, stated rather than left to a guess
			// at import time.
			w.OpenAttributeElement( "axisSystem", "DmeAxisSystem", w.NextId(), "axisSystem" );
			w.Attribute( "upAxis", "int", "3" );
			w.Attribute( "forwardParity", "int", "1" );
			w.Attribute( "coordSys", "int", "0" );
			w.CloseElement();
		}
		w.CloseElement();

		// The model and the skeleton are the same element — one is the geometry's root, the other
		// the pose's, and in a single-model file they are one DmeModel.
		w.Attribute( "model", "element", idModel );

		w.CloseElement();

		return w.ToString();
	}

	// --- pieces -------------------------------------------------------------------------------

	/// <summary>
	/// Check supplied normals against the mesh before writing them.
	///
	/// Every one of these mistakes produces a file that parses and then fails somewhere else — a
	/// short corner array walks off the end mid-write, an out-of-range index writes a normal index
	/// the compiler resolves to nothing. Both are far cheaper to find here, against the mesh, than
	/// as "Node 'Body_LOD0' resolve failure" with no line number.
	/// </summary>
	static (int[][], List<Vec3>) Validated( AuthoredNormals authored, PolyMesh mesh )
	{
		if ( authored.Values is null || authored.CornerIndices is null )
			throw new ArgumentException( "AuthoredNormals needs both Values and CornerIndices" );

		if ( authored.CornerIndices.Length != mesh.FaceCount )
			throw new ArgumentException(
				$"AuthoredNormals covers {authored.CornerIndices.Length} faces, the mesh has {mesh.FaceCount}" );

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var corners = authored.CornerIndices[fi];

			if ( corners is null || corners.Length != mesh.Faces[fi].Count )
				throw new ArgumentException(
					$"AuthoredNormals gives face {fi} {corners?.Length ?? 0} corners, it has {mesh.Faces[fi].Count}" );

			foreach ( var n in corners )
			{
				if ( n < 0 || n >= authored.Values.Count )
					throw new ArgumentException(
						$"AuthoredNormals face {fi} indexes normal {n}, only {authored.Values.Count} exist" );
			}
		}

		return (authored.CornerIndices, authored.Values);
	}

	/// <summary>
	/// One bone, and its children under it.
	///
	/// A BONE IS A DmeJoint, NOT A DmeDag, and nothing about the file says so. The two carry
	/// identical attributes — transform, shape, visible, children — so a skeleton written as
	/// DmeDag parses, validates, resolves every reference, and compiles without one word of
	/// complaint. It simply produces a model with **zero bones**: the compiler builds its skeleton
	/// from the joints, and a DmeDag is a plain transform node it has no reason to treat as one.
	/// The mesh is unaffected, so the model renders perfectly and cannot be posed, which is the
	/// one thing a rigged export exists to do.
	///
	/// Confirmed both ways: fp_arms compiles to 24 bones, this writer's output compiled to 0 until
	/// this line said DmeJoint. `model_bones &lt;path&gt;` (Editor/HaloMount/HaloModelInspect.cs) is
	/// how to check — a .vmdl_c does not surrender its bone names to a text scan, so "I could not
	/// find the names in the file" means nothing at all here.
	///
	/// The mesh's own dag stays a DmeDag. Only bones are joints.
	/// </summary>
	static void WriteBoneDag( DmxText w, Skeleton skeleton, int index, string[] dagIds, string[] transformIds )
	{
		var bone = skeleton.Bones[index];

		w.OpenArrayElement( "DmeJoint", dagIds[index], bone.Name );
		WriteTransform( w, "transform", transformIds[index], bone.Name, bone.Local );
		w.Attribute( "shape", "element", "" );
		w.Attribute( "visible", "bool", "1" );

		w.OpenArray( "children", "element_array" );

		// Skeleton stores parents before children, so every child of this bone is later in the
		// list and this recursion terminates without needing a visited set.
		for ( var i = index + 1; i < skeleton.Count; i++ )
		{
			if ( skeleton.Bones[i].Parent == index )
				WriteBoneDag( w, skeleton, i, dagIds, transformIds );
		}

		w.CloseArray();
		w.CloseElement();
	}

	static void WriteMeshDag( DmxText w, string dagId, string transformId, string meshId, string vertexDataId,
		PolyMesh mesh, SkinWeights skin, Skeleton skeleton, int[][] cornerNormals, List<Vec3> normals,
		Func<int, string> materialName )
	{
		w.OpenArrayElement( "DmeDag", dagId, "mesh" );
		WriteTransform( w, "transform", transformId, "mesh", Xform.Identity );

		w.OpenAttributeElement( "shape", "DmeMesh", meshId, "mesh" );
		{
			w.Attribute( "visible", "bool", "1" );

			WriteVertexData( w, vertexDataId, mesh, skin, skeleton, cornerNormals, normals );

			// currentState and baseStates point AT the bind state rather than duplicating it. A
			// second copy is a second thing to keep in step, for nothing: this mesh is not posed.
			w.Attribute( "currentState", "element", vertexDataId );

			w.OpenArray( "baseStates", "element_array" );
			w.ArrayReference( vertexDataId );
			w.CloseArray();

			WriteFaceSets( w, mesh, materialName );
		}
		w.CloseElement();

		w.Attribute( "visible", "bool", "1" );
		w.OpenArray( "children", "element_array" );
		w.CloseArray();
		w.CloseElement();
	}

	/// <summary>
	/// Positions, normals and UVs, each as a value array plus a per-face-corner index array.
	///
	/// THE NAMES ARE NOT THE OBVIOUS ONES. A DmeVertexData field is named
	/// <c>&lt;semantic&gt;$&lt;set&gt;</c> and its index array is that name with "Indices" appended:
	/// position$0 / position$0Indices, normal$0, texcoord$0, blendweights$0, blendindices$0. The
	/// plural spellings this writer used first — positions, normals, textureCoordinates,
	/// jointWeights, jointIndices — are real strings in modeldoc_utils.dll, which is where they were
	/// read from, but they are not what a vertex format is keyed on, and the compiler answers a file
	/// full of them with "Failed to load mesh 0/1: Missing position values".
	///
	/// These were taken from what the engine's own fbx2dmx.exe writes, which is the reference worth
	/// having: convert any FBX in the project and read the DmeVertexData it produces.
	///
	///   fbx2dmx.exe -i Assets/models/first_person/fp_arms.fbx -o rig_ref.dmx
	///
	/// The three index arrays MUST be the same length — the compiler says so outright ("Cannot add
	/// vertex data block with different number of normal indices (%d) and vertex indices (%d)") —
	/// so all three are filled in one walk over face corners and cannot drift apart.
	///
	/// Weights are per POSITION, not per corner, which is the other rule it states: "Incorrect
	/// number of joint weights or indices specified, must match number of positions values".
	/// blendweights$0 and blendindices$0 carry no index array of their own for exactly that reason:
	/// they are jointCount entries per position, indexed by the position index. fp_arms confirms the
	/// shape — 260 positions, 260 blendweights at jointCount 1, against 944 face corners.
	/// </summary>
	static void WriteVertexData( DmxText w, string id, PolyMesh mesh, SkinWeights skin, Skeleton skeleton,
		int[][] cornerNormals, List<Vec3> normals )
	{
		w.OpenAttributeElement( "bindState", "DmeVertexData", id, "bind" );

		w.Attribute( "flipVCoordinates", "bool", "0" );
		w.Attribute( "jointCount", "int", MaxInfluences.ToString( CultureInfo.InvariantCulture ) );

		w.OpenArray( "vertexFormat", "string_array" );
		w.ArrayValue( Position );
		w.ArrayValue( TexCoord );
		w.ArrayValue( Normal );
		w.ArrayValue( BlendWeights );
		w.ArrayValue( BlendIndices );
		w.CloseArray();

		// Positions, shared between faces exactly as the cage shares them.
		w.OpenArray( Position, "vector3_array" );

		foreach ( var p in mesh.Positions )
			w.ArrayValue( DmxText.Vector3( p ) );

		w.CloseArray();

		// One entry per corner-normal group, so hard edges survive the trip.
		w.OpenArray( Normal, "vector3_array" );

		foreach ( var n in normals )
			w.ArrayValue( DmxText.Vector3( n ) );

		w.CloseArray();

		var uvs = new List<Vec2>();
		var positionIndices = new List<int>();
		var normalIndices = new List<int>();
		var uvIndices = new List<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			for ( var c = 0; c < face.Count; c++ )
			{
				positionIndices.Add( face.Indices[c] );
				normalIndices.Add( cornerNormals[fi][c] );

				uvIndices.Add( uvs.Count );
				uvs.Add( face.UVs is not null && c < face.UVs.Length ? face.UVs[c] : default );
			}
		}

		w.OpenArray( TexCoord, "vector2_array" );

		foreach ( var uv in uvs )
			w.ArrayValue( DmxText.Vector2( uv ) );

		w.CloseArray();

		w.IntArray( Position + "Indices", positionIndices );
		w.IntArray( Normal + "Indices", normalIndices );
		w.IntArray( TexCoord + "Indices", uvIndices );

		var weights = new List<float>( mesh.VertexCount * MaxInfluences );
		var joints = new List<int>( mesh.VertexCount * MaxInfluences );

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var influences = v < skin.Count ? skin[v] : Array.Empty<BoneWeight>();
			var kept = Prune( influences, skeleton.Count );

			for ( var i = 0; i < MaxInfluences; i++ )
			{
				if ( i < kept.Count )
				{
					joints.Add( kept[i].Bone );
					weights.Add( kept[i].Weight );
					continue;
				}

				// Padding rides on bone 0 at zero weight: a real index the compiler can look up,
				// contributing nothing.
				joints.Add( 0 );
				weights.Add( 0f );
			}
		}

		w.FloatArray( BlendWeights, weights );
		w.IntArray( BlendIndices, joints );

		w.CloseElement();
	}

	/// <summary>Strongest influences first, capped, renormalised, and with anything pointing at a
	/// bone outside the skeleton dropped rather than written out to be looked up.</summary>
	static List<BoneWeight> Prune( BoneWeight[] influences, int boneCount )
	{
		var kept = new List<BoneWeight>( MaxInfluences );

		foreach ( var influence in influences )
		{
			if ( influence.Bone < 0 || influence.Bone >= boneCount || influence.Weight <= 0f )
				continue;

			kept.Add( influence );
		}

		kept.Sort( ( a, b ) => b.Weight.CompareTo( a.Weight ) );

		if ( kept.Count > MaxInfluences )
			kept.RemoveRange( MaxInfluences, kept.Count - MaxInfluences );

		var total = 0f;

		foreach ( var influence in kept )
			total += influence.Weight;

		if ( kept.Count == 0 || total <= 0f )
			return new List<BoneWeight> { new( 0, 1f ) };

		for ( var i = 0; i < kept.Count; i++ )
			kept[i] = new BoneWeight( kept[i].Bone, kept[i].Weight / total );

		return kept;
	}

	/// <summary>
	/// One face set per material slot in use, each holding its faces as runs of face-corner
	/// indices terminated by -1.
	///
	/// N-GONS GO STRAIGHT THROUGH. DMX ends a face on -1 rather than assuming three, so the quad
	/// cage does not have to be triangulated to leave the tool — which is half the reason this
	/// format is worth the write over SMD.
	/// </summary>
	static void WriteFaceSets( DmxText w, PolyMesh mesh, Func<int, string> materialName )
	{
		var byMaterial = new Dictionary<int, List<int>>();
		var corner = 0;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( !byMaterial.TryGetValue( face.Material, out var faces ) )
				byMaterial[face.Material] = faces = new List<int>();

			for ( var c = 0; c < face.Count; c++ )
				faces.Add( corner + c );

			faces.Add( -1 );
			corner += face.Count;
		}

		var slots = new List<int>( byMaterial.Keys );
		slots.Sort();

		w.OpenArray( "faceSets", "element_array" );

		foreach ( var slot in slots )
		{
			w.OpenArrayElement( "DmeFaceSet", w.NextId(), $"faceset_{slot}" );

			w.OpenAttributeElement( "material", "DmeMaterial", w.NextId(), materialName( slot ) );
			w.Attribute( "mtlName", "string", materialName( slot ) );
			w.CloseElement();

			w.IntArray( "faces", byMaterial[slot] );
			w.CloseElement();
		}

		w.CloseArray();
	}

	static void WriteTransform( DmxText w, string attribute, string id, string name, Xform x )
	{
		w.OpenAttributeElement( attribute, "DmeTransform", id, name );
		w.Attribute( "position", "vector3", DmxText.Vector3( x.Origin ) );
		w.Attribute( "orientation", "quaternion", DmxText.Quaternion( x ) );
		w.CloseElement();
	}

	static void WriteTransformElement( DmxText w, string id, string name, Xform x )
	{
		w.OpenArrayElement( "DmeTransform", id, name );
		w.Attribute( "position", "vector3", DmxText.Vector3( x.Origin ) );
		w.Attribute( "orientation", "quaternion", DmxText.Quaternion( x ) );
		w.CloseElement();
	}
}

/// <summary>
/// The KeyValues2 side of DMX: indentation, quoting, element blocks and arrays.
///
/// Split out from DmxWriter so the model layout above reads as a model layout rather than as
/// string building, and so the one place that decides how a value is quoted is one place.
/// </summary>
internal sealed class DmxText
{
	private readonly StringBuilder _sb = new();
	private readonly List<bool> _containers = new();
	private int _depth;
	private int _nextId;

	/// <summary>Every element needs a unique id in GUID text form. These are counted rather than
	/// random so the file is reproducible.</summary>
	public string NextId() => $"{++_nextId:x8}-0000-0000-0000-000000000000";

	public void Raw( string text ) => _sb.Append( text ).Append( '\n' );

	public void OpenElement( string type, string id, string name )
	{
		Indent();
		_sb.Append( Quote( type ) ).Append( '\n' );
		OpenBrace();
		Attribute( "id", "elementid", id );
		Attribute( "name", "string", name );
	}

	/// <summary>An element that is the value of a named attribute: <c>"skeleton" "DmeModel" { }</c>.</summary>
	public void OpenAttributeElement( string attribute, string type, string id, string name )
	{
		Indent();
		_sb.Append( Quote( attribute ) ).Append( ' ' ).Append( Quote( type ) ).Append( '\n' );
		OpenBrace();
		Attribute( "id", "elementid", id );
		Attribute( "name", "string", name );
	}

	/// <summary>An element inside an element_array, which carries its type but no attribute
	/// name.</summary>
	public void OpenArrayElement( string type, string id, string name )
	{
		Indent();
		_sb.Append( Quote( type ) ).Append( '\n' );
		OpenBrace();
		Attribute( "id", "elementid", id );
		Attribute( "name", "string", name );
	}

	/// <summary>Closes an element body. An element that is a member of an element_array has to be
	/// followed by a comma exactly like a plain array value is; without it the reader stops at the
	/// next member with "Expecting ',', didn't find it!" and the file fails to load outright.
	/// CloseArray's TrimTrailingComma then takes the one after the final member back off.</summary>
	public void CloseElement()
	{
		PopContainer();
		_depth--;
		Indent();
		_sb.Append( InArray ? "},\n" : "}\n" );
	}

	public void Attribute( string name, string type, string value )
	{
		Indent();
		_sb.Append( Quote( name ) ).Append( ' ' ).Append( Quote( type ) ).Append( ' ' )
			.Append( Quote( value ) ).Append( '\n' );
	}

	public void OpenArray( string name, string type )
	{
		Indent();
		_sb.Append( Quote( name ) ).Append( ' ' ).Append( Quote( type ) ).Append( '\n' );
		Indent();
		_sb.Append( "[\n" );
		_depth++;
		PushContainer( true );
	}

	public void CloseArray()
	{
		TrimTrailingComma();
		PopContainer();
		_depth--;
		Indent();
		_sb.Append( "]\n" );
	}

	/// <summary>A plain value in an array. Every entry is quoted, including numbers — that is how
	/// Valve's own KeyValues2 writer emits them, and the parser reads the type from the array's
	/// declaration rather than from the token.</summary>
	public void ArrayValue( string value )
	{
		Indent();
		_sb.Append( Quote( value ) ).Append( ",\n" );
	}

	/// <summary>
	/// A reference to an element defined elsewhere. KeyValues2 spells this as TWO tokens —
	/// <c>"element" "&lt;id&gt;"</c> — which is what fbx2dmx's own output writes for every entry of
	/// children and jointList. A bare quoted id is read as an element TYPE name instead, and the
	/// parser then waits for a body that never comes.
	/// </summary>
	public void ArrayReference( string id )
	{
		Indent();
		_sb.Append( Quote( "element" ) ).Append( ' ' ).Append( Quote( id ) ).Append( ",\n" );
	}

	public void IntArray( string name, List<int> values )
	{
		OpenArray( name, "int_array" );

		foreach ( var v in values )
			ArrayValue( v.ToString( CultureInfo.InvariantCulture ) );

		CloseArray();
	}

	public void FloatArray( string name, List<float> values )
	{
		OpenArray( name, "float_array" );

		foreach ( var v in values )
			ArrayValue( Number( v ) );

		CloseArray();
	}

	public override string ToString() => _sb.ToString();

	// --- formatting ---------------------------------------------------------------------------

	public static string Number( float v ) => v.ToString( "0.######", CultureInfo.InvariantCulture );

	public static string Vector2( Vec2 v ) => $"{Number( v.x )} {Number( v.y )}";

	public static string Vector3( Vec3 v ) => $"{Number( v.x )} {Number( v.y )} {Number( v.z )}";

	/// <summary>
	/// The rotation part of an Xform as a quaternion, x y z w.
	///
	/// Xform stores the images of the unit axes, so X, Y and Z are the COLUMNS of the rotation
	/// matrix — getting that backwards transposes every bone, which mirrors the rig rather than
	/// failing outright. Shepperd's method: pick the largest of the four candidate denominators so
	/// the division is never by something near zero.
	/// </summary>
	public static string Quaternion( Xform t )
	{
		// m[row][column], columns being the transformed axes.
		var m00 = t.X.x; var m01 = t.Y.x; var m02 = t.Z.x;
		var m10 = t.X.y; var m11 = t.Y.y; var m12 = t.Z.y;
		var m20 = t.X.z; var m21 = t.Y.z; var m22 = t.Z.z;

		var trace = m00 + m11 + m22;
		float x, y, z, w;

		if ( trace > 0f )
		{
			var s = MathF.Sqrt( trace + 1f ) * 2f;
			w = 0.25f * s;
			x = (m21 - m12) / s;
			y = (m02 - m20) / s;
			z = (m10 - m01) / s;
		}
		else if ( m00 > m11 && m00 > m22 )
		{
			var s = MathF.Sqrt( 1f + m00 - m11 - m22 ) * 2f;
			w = (m21 - m12) / s;
			x = 0.25f * s;
			y = (m01 + m10) / s;
			z = (m02 + m20) / s;
		}
		else if ( m11 > m22 )
		{
			var s = MathF.Sqrt( 1f + m11 - m00 - m22 ) * 2f;
			w = (m02 - m20) / s;
			x = (m01 + m10) / s;
			y = 0.25f * s;
			z = (m12 + m21) / s;
		}
		else
		{
			var s = MathF.Sqrt( 1f + m22 - m00 - m11 ) * 2f;
			w = (m10 - m01) / s;
			x = (m02 + m20) / s;
			y = (m12 + m21) / s;
			z = 0.25f * s;
		}

		var length = MathF.Sqrt( x * x + y * y + z * z + w * w );

		if ( length > 1e-8f )
		{
			x /= length; y /= length; z /= length; w /= length;
		}
		else
		{
			x = 0f; y = 0f; z = 0f; w = 1f;
		}

		return $"{Number( x )} {Number( y )} {Number( z )} {Number( w )}";
	}

	static string Quote( string value ) => $"\"{Escape( value )}\"";

	static string Escape( string value )
	{
		if ( string.IsNullOrEmpty( value ) )
			return "";

		return value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
	}

	void OpenBrace()
	{
		Indent();
		_sb.Append( "{\n" );
		_depth++;
		PushContainer( false );
	}

	/// <summary>Whether the container currently being written into is an element_array rather than
	/// an element body. Only array members take a trailing comma.</summary>
	bool InArray => _containers.Count > 0 && _containers[^1];

	void PushContainer( bool isArray ) => _containers.Add( isArray );

	void PopContainer()
	{
		if ( _containers.Count > 0 )
			_containers.RemoveAt( _containers.Count - 1 );
	}

	void Indent() => _sb.Append( '\t', _depth );

	/// <summary>KeyValues2 tolerates a trailing comma in most readers, but not all of them, and a
	/// file that loads in one tool and not the next is the worst kind of export bug.</summary>
	void TrimTrailingComma()
	{
		var i = _sb.Length - 1;

		while ( i >= 0 && (_sb[i] == '\n' || _sb[i] == '\t') )
			i--;

		if ( i >= 0 && _sb[i] == ',' )
			_sb.Remove( i, 1 );
	}
}
