using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// What a skinned FBX holds that the engine will not give back: each mesh's control points at the
/// bind pose, its polygons and their materials, and which bones pull each point how hard.
///
/// WHY READ THE SOURCE. A compiled s&box model hands over its render vertices, indices and
/// materials, and its bones - but not its skin weights; the Vertex it returns has no blend
/// fields. The weights live in the file the model was compiled from, so that is where they are
/// read. <see cref="ModelDump.AttachWeights"/> lines this up with the engine's own model space.
///
/// BINARY FBX 7.x (both the 32-bit and 64-bit offset layouts), and ASCII FBX 7.x through
/// <see cref="FbxBinary"/>'s text reader - the subset the kernel's own <see cref="FbxWriter"/> and
/// the usual DCC exporters write. An ASCII file that reader does not understand is refused with the
/// remedy (re-export as binary) rather than half-read.
///
/// WHAT IMPORT ADDS. <see cref="FbxImport"/> turns a scene into bodies, so each mesh also carries
/// its per-corner normals, UVs and colours and the file's unit and axis declarations - kept here
/// raw, in FBX's own space and units, so the dump/skin readers above see exactly what they did
/// before. The conversion into Effigy's inches and Z-up happens in FbxImport, once.
///
/// Positions are FBX global at the bind pose: a skinned mesh's points through its cluster's
/// Transform, a bone through its cluster's TransformLink - the matrices the exporter wrote for
/// exactly this purpose - so nothing here has to reproduce FBX's pre/post-rotation and pivot
/// rules for skinned content. A mesh with no skin is placed by its Lcl translation, rotation
/// (XYZ, after PreRotation) and scaling, which is what Blender and the kernel's writer emit.
/// </summary>
public sealed class FbxScene
{
	public sealed class Mesh
	{
		public string Name = "";
		public Vec3[] Points = Array.Empty<Vec3>();
		public List<int[]> Polygons = new();

		/// <summary>Material index per polygon, into <see cref="Materials"/>; all zero when the file
		/// says nothing.</summary>
		public int[] PolygonMaterial = Array.Empty<int>();
		public List<string> Materials = new();

		/// <summary>Per control point: (bone name, weight). Null for a mesh with no skin.</summary>
		public List<(string Bone, float Weight)>[] Weights;

		/// <summary>What carried <see cref="Points"/> from the geometry's own space into FBX global
		/// space: the node chain (with geometric offset), or the skin's bind matrices.</summary>
		public Xform Placement = Xform.Identity;

		/// <summary>Per polygon corner, in <see cref="Polygons"/> order, in FBX global space. Null
		/// when the file has no normal layer.</summary>
		public Vec3[] CornerNormals;

		/// <summary>Per polygon corner, as stored: FBX's V runs UP from the bottom of the texture.
		/// Null when the file has no UV layer.</summary>
		public Vec2[] CornerUVs;

		/// <summary>Per polygon corner, straight RGBA. Null when the file has no colour layer.</summary>
		public Vec4[] CornerColors;
		/// <summary>Blendshapes (shape keys), in the file's order. Empty when it has none.</summary>
		public List<Shape> Shapes = new();
	}

	/// <summary>
	/// One BlendShapeChannel and its Shape: the control points it moves and how far, at full
	/// strength, in the same global frame as <see cref="Mesh.Points"/>.
	/// </summary>
	public sealed class Shape
	{
		public string Name = "";
		public int[] Indexes = Array.Empty<int>();
		public Vec3[] Offsets = Array.Empty<Vec3>();

		/// <summary>Per index, the normal's change; empty when the file has none.</summary>
		public Vec3[] NormalOffsets = Array.Empty<Vec3>();

		/// <summary>The channel's weight at which the Shape is reached (FullWeights; 100 for one target).</summary>
		public double FullWeight = 100;

		/// <summary>The channel's saved weight, 0..100.</summary>
		public double DeformPercent;

		/// <summary>The offsets as one array over all <paramref name="pointCount"/> control points.</summary>
		public Vec3[] Dense( int pointCount )
		{
			var dense = new Vec3[pointCount];

			for ( var k = 0; k < Math.Min( Indexes.Length, Offsets.Length ); k++ )
				if ( Indexes[k] >= 0 && Indexes[k] < pointCount )
					dense[Indexes[k]] = Offsets[k];

			return dense;
		}
	}

	/// <summary>Centimetres per file unit (GlobalSettings UnitScaleFactor). 1 when the file does not
	/// say, which is FBX's own default.</summary>
	public double UnitScale = 1;

	/// <summary>The file's declared axes, as (axis 0..2, sign ±1). The defaults are the FBX SDK's
	/// own when GlobalSettings is absent: Y up, +Z front, +X right.</summary>
	public (int Axis, int Sign) Up = (1, 1), Front = (2, 1), Coord = (0, 1);

	/// <summary>FBXHeaderExtension Creator, e.g. "Effigy" or "Blender (stable FBX IO) - …".</summary>
	public string Creator = "";

	/// <summary>The header's FBX version, e.g. 7400.</summary>
	public int Version;

	/// <summary>A bone's NodeAttribute Size, in file units, when it has one. Effigy writes the
	/// bone's length here; other exporters write a display radius.</summary>
	public Dictionary<string, float> BoneSize = new();

	/// <summary>Bones some skin cluster actually deforms with.</summary>
	public HashSet<string> SkinBones = new();

	/// <summary>Each skeleton node's class (LimbNode, Null, Root…), by name.</summary>
	public Dictionary<string, string> BoneClass = new();

	/// <summary>Skeleton node names in the order the file lists them - stable, unlike a dictionary.</summary>
	public List<string> BoneOrder = new();

	public List<Mesh> Meshes = new();

	/// <summary>Each bone's global transform at the bind pose, by name.</summary>
	public Dictionary<string, Xform> BoneBind = new();

	/// <summary>Each bone's parent bone, by name; absent for a root.</summary>
	public Dictionary<string, string> BoneParent = new();

	public static FbxScene ReadFile( string path ) => Read( File.ReadAllBytes( path ) );

	public static FbxScene Read( byte[] file )
	{
		var root = FbxNode.ParseAny( file );
		var objects = root.Child( "Objects" ) ?? throw new FormatException( "FBX has no Objects section" );

		var byId = new Dictionary<long, FbxNode>();
		foreach ( var o in objects.Children )
			if ( o.Props.Count > 0 && o.Props[0] is long id )
				byId[id] = o;

		// Connections: child -> parents, parent -> children (object-object only).
		var parentsOf = new Dictionary<long, List<long>>();
		var childrenOf = new Dictionary<long, List<long>>();

		foreach ( var c in root.Child( "Connections" )?.Children ?? new List<FbxNode>() )
		{
			if ( c.Name != "C" || c.Props.Count < 3 || c.Props[1] is not long child || c.Props[2] is not long parent )
				continue;

			if ( !parentsOf.TryGetValue( child, out var ps ) ) parentsOf[child] = ps = new List<long>();
			if ( !childrenOf.TryGetValue( parent, out var cs ) ) childrenOf[parent] = cs = new List<long>();
			ps.Add( parent );
			cs.Add( child );
		}

		IEnumerable<FbxNode> Children( long id, string kind ) =>
			childrenOf.TryGetValue( id, out var cs ) ? cs.Where( byId.ContainsKey ).Select( c => byId[c] ).Where( n => n.Name == kind ) : Enumerable.Empty<FbxNode>();

		IEnumerable<FbxNode> Parents( long id, string kind ) =>
			parentsOf.TryGetValue( id, out var ps ) ? ps.Where( byId.ContainsKey ).Select( p => byId[p] ).Where( n => n.Name == kind ) : Enumerable.Empty<FbxNode>();

		var scene = new FbxScene();
		ReadSettings( root, scene );

		foreach ( var geometry in objects.Children.Where( n => n.Name == "Geometry" && n.ClassName == "Mesh" ) )
		{
			var id = (long)geometry.Props[0];
			var model = Parents( id, "Model" ).FirstOrDefault();
			var mesh = new Mesh { Name = model?.ObjectName ?? geometry.ObjectName };

			var raw = geometry.Child( "Vertices" )?.Doubles() ?? Array.Empty<double>();
			var local = new Vec3[raw.Length / 3];
			for ( var i = 0; i < local.Length; i++ )
				local[i] = new Vec3( (float)raw[i * 3], (float)raw[i * 3 + 1], (float)raw[i * 3 + 2] );

			// Polygons end on a negative index: the last corner, stored as ~index.
			var current = new List<int>();
			var cornerPoint = new List<int>();
			var cornerPolygon = new List<int>();
			foreach ( var v in geometry.Child( "PolygonVertexIndex" )?.Ints() ?? Array.Empty<int>() )
			{
				current.Add( v < 0 ? ~v : v );
				cornerPoint.Add( v < 0 ? ~v : v );
				cornerPolygon.Add( mesh.Polygons.Count );
				if ( v >= 0 ) continue;
				mesh.Polygons.Add( current.ToArray() );
				current.Clear();
			}

			// A trailing polygon with no closing negative index is cut off, not invented.
			if ( current.Count > 0 )
			{
				cornerPoint.RemoveRange( cornerPoint.Count - current.Count, current.Count );
				cornerPolygon.RemoveRange( cornerPolygon.Count - current.Count, current.Count );
			}

			var normals = Layer( geometry, "LayerElementNormal", "Normals", "NormalsIndex", 3, cornerPoint, cornerPolygon,
				( d, i ) => new Vec3( (float)d[i], (float)d[i + 1], (float)d[i + 2] ) );
			mesh.CornerUVs = Layer( geometry, "LayerElementUV", "UV", "UVIndex", 2, cornerPoint, cornerPolygon,
				( d, i ) => new Vec2( (float)d[i], (float)d[i + 1] ) );
			mesh.CornerColors = Layer( geometry, "LayerElementColor", "Colors", "ColorIndex", 4, cornerPoint, cornerPolygon,
				( d, i ) => new Vec4( (float)d[i], (float)d[i + 1], (float)d[i + 2], (float)d[i + 3] ) );

			mesh.PolygonMaterial = new int[mesh.Polygons.Count];
			if ( geometry.Child( "LayerElementMaterial" ) is { } layer && layer.Child( "Materials" )?.Ints() is { } mats )
			{
				var allSame = (layer.Child( "MappingInformationType" )?.Props.FirstOrDefault() as string) == "AllSame";
				for ( var p = 0; p < mesh.PolygonMaterial.Length; p++ )
					mesh.PolygonMaterial[p] = allSame ? (mats.Length > 0 ? mats[0] : 0) : p < mats.Length ? mats[p] : 0;
			}

			if ( model is not null )
				mesh.Materials = Children( (long)model.Props[0], "Material" ).Select( m => m.ObjectName ).ToList();

			// Skin: geometry <- Deformer(Skin) <- Deformer(Cluster) <- Model(bone).
			var placement = model is null ? Xform.Identity : GlobalOf( model, byId, Parents ) * GeometricOf( model );
			var skin = Children( id, "Deformer" ).FirstOrDefault( d => d.ClassName == "Skin" );

			if ( skin is not null )
			{
				mesh.Weights = new List<(string, float)>[local.Length];
				for ( var i = 0; i < local.Length; i++ ) mesh.Weights[i] = new List<(string, float)>();

				var placedBySkin = false;

				foreach ( var cluster in Children( (long)skin.Props[0], "Deformer" ).Where( d => d.ClassName == "Cluster" ) )
				{
					var bone = Children( (long)cluster.Props[0], "Model" ).FirstOrDefault();
					if ( bone is null ) continue;

					if ( cluster.Child( "TransformLink" )?.Doubles() is { Length: 16 } link )
						scene.BoneBind[bone.ObjectName] = Matrix( link );

					scene.SkinBones.Add( bone.ObjectName );

					if ( !placedBySkin && cluster.Child( "Transform" )?.Doubles() is { Length: 16 } meshAtBind )
					{
						// Transform is the cluster's bone-to-mesh frame at bind:
						// TransformLink^-1 * meshGlobal. The mesh's global bind is the product back.
						var linkX = cluster.Child( "TransformLink" )?.Doubles() is { Length: 16 } l ? Matrix( l ) : Xform.Identity;
						placement = linkX * Matrix( meshAtBind );
						placedBySkin = true;
					}

					var indexes = cluster.Child( "Indexes" )?.Ints() ?? Array.Empty<int>();
					var weights = cluster.Child( "Weights" )?.Doubles() ?? Array.Empty<double>();

					for ( var k = 0; k < Math.Min( indexes.Length, weights.Length ); k++ )
						if ( indexes[k] >= 0 && indexes[k] < local.Length && weights[k] > 0 )
							mesh.Weights[indexes[k]].Add( (bone.ObjectName, (float)weights[k]) );
				}
			}

			mesh.Points = local.Select( placement.TransformPoint ).ToArray();

			// Blendshapes: geometry <- Deformer(BlendShape) <- Deformer(BlendShapeChannel) <- Geometry(Shape).
			// Offsets are directions, so only the placement's turn and scale apply.
			foreach ( var blend in Children( id, "Deformer" ).Where( d => d.ClassName == "BlendShape" ) )
			{
				foreach ( var channel in Children( (long)blend.Props[0], "Deformer" ).Where( d => d.ClassName == "BlendShapeChannel" ) )
				{
					var target = Children( (long)channel.Props[0], "Geometry" ).FirstOrDefault( g => g.ClassName == "Shape" );

					if ( target is null )
						continue;

					var offsets = target.Child( "Vertices" )?.Doubles() ?? Array.Empty<double>();
					var normalOffsets = target.Child( "Normals" )?.Doubles() ?? Array.Empty<double>();

					static Vec3[] Triples( double[] v, Xform x ) =>
						Enumerable.Range( 0, v.Length / 3 ).Select( i => x.TransformDirection( new Vec3( (float)v[i * 3], (float)v[i * 3 + 1], (float)v[i * 3 + 2] ) ) ).ToArray();

					var deform = channel.Child( "DeformPercent" )?.Props.FirstOrDefault();

					mesh.Shapes.Add( new Shape
					{
						Name = channel.ObjectName.Length > 0 ? channel.ObjectName : target.ObjectName,
						Indexes = target.Child( "Indexes" )?.Ints() ?? Array.Empty<int>(),
						Offsets = Triples( offsets, placement ),
						NormalOffsets = Triples( normalOffsets, placement ),
						FullWeight = channel.Child( "FullWeights" )?.Doubles() is { Length: > 0 } full ? full[0] : 100,
						DeformPercent = deform is null ? 0 : Convert.ToDouble( deform ),
					} );
				}
			}

			mesh.Placement = placement;
			mesh.CornerNormals = normals is null ? null : NormalsToGlobal( normals, placement );
			scene.Meshes.Add( mesh );
		}

		// Bones without a cluster (not skinning anything) still help line the skeleton up.
		foreach ( var limb in objects.Children.Where( n => n.Name == "Model" && (n.ClassName is "LimbNode" or "Null" or "Root" || scene.SkinBones.Contains( n.ObjectName )) ) )
		{
			if ( !scene.BoneBind.ContainsKey( limb.ObjectName ) )
				scene.BoneBind[limb.ObjectName] = GlobalOf( limb, byId, Parents );

			if ( Parents( (long)limb.Props[0], "Model" ).FirstOrDefault() is { } parent && (parent.ClassName is "LimbNode" or "Null" or "Root" || scene.SkinBones.Contains( parent.ObjectName )) )
				scene.BoneParent[limb.ObjectName] = parent.ObjectName;

			if ( !scene.BoneClass.ContainsKey( limb.ObjectName ) )
			{
				scene.BoneClass[limb.ObjectName] = limb.ClassName;
				scene.BoneOrder.Add( limb.ObjectName );
			}

			// The bone's NodeAttribute hangs off its Model.
			foreach ( var attribute in Children( (long)limb.Props[0], "NodeAttribute" ) )
				foreach ( var p in attribute.Child( "Properties70" )?.Children ?? new List<FbxNode>() )
					if ( p.Props.Count >= 5 && p.Props[0] as string == "Size" )
						scene.BoneSize[limb.ObjectName] = (float)Convert.ToDouble( p.Props[4] );
		}

		return scene;
	}

	/// <summary>16 doubles, column-major, translation last: FBX's matrix layout.</summary>
	static Xform Matrix( double[] m ) => new(
		new Vec3( (float)m[0], (float)m[1], (float)m[2] ),
		new Vec3( (float)m[4], (float)m[5], (float)m[6] ),
		new Vec3( (float)m[8], (float)m[9], (float)m[10] ),
		new Vec3( (float)m[12], (float)m[13], (float)m[14] ) );

	/// <summary>A model's global transform from its Lcl properties up the parent chain.</summary>
	static Xform GlobalOf( FbxNode model, Dictionary<long, FbxNode> byId, Func<long, string, IEnumerable<FbxNode>> parents )
	{
		var global = Xform.Identity;
		var guard = 0;

		for ( var node = model; node is not null && guard++ < 256; node = parents( (long)node.Props[0], "Model" ).FirstOrDefault() )
			global = LocalOf( node ) * global;

		return global;
	}

	/// <summary>
	/// A model's transform relative to its parent, by the FBX SDK's full formula:
	///
	///   T * Roff * Rp * Rpre * R * Rpost⁻¹ * Rp⁻¹ * Soff * Sp * S * Sp⁻¹
	///
	/// Pivots, offsets and post-rotation are zero in what Blender and the kernel's writer emit, so
	/// for those files this is translation, pre-rotation, rotation and scale - what this read before
	/// - but a file from a tool that does use pivots lands where that tool showed it. R follows the
	/// node's RotationOrder; pre- and post-rotation are always XYZ, as the SDK defines them.
	/// </summary>
	static Xform LocalOf( FbxNode model )
	{
		var t = Prop( model, "Lcl Translation", Vec3.Zero );
		var r = Prop( model, "Lcl Rotation", Vec3.Zero );
		var s = Prop( model, "Lcl Scaling", Vec3.One );
		var pre = Prop( model, "PreRotation", Vec3.Zero );
		var post = Prop( model, "PostRotation", Vec3.Zero );
		var rOff = Prop( model, "RotationOffset", Vec3.Zero );
		var rPivot = Prop( model, "RotationPivot", Vec3.Zero );
		var sOff = Prop( model, "ScalingOffset", Vec3.Zero );
		var sPivot = Prop( model, "ScalingPivot", Vec3.Zero );
		var order = (int)PropScalar( model, "RotationOrder", 0 );

		var postRotation = Euler( post, 0 );

		return Xform.Translate( t ) * Xform.Translate( rOff ) * Xform.Translate( rPivot )
			* Euler( pre, 0 ) * Euler( r, order ) * postRotation.Inverse * Xform.Translate( -rPivot )
			* Xform.Translate( sOff ) * Xform.Translate( sPivot ) * Xform.Scale( s ) * Xform.Translate( -sPivot );
	}

	/// <summary>The geometric offset: a transform on the geometry alone, not inherited by children.</summary>
	static Xform GeometricOf( FbxNode model ) =>
		Xform.Translate( Prop( model, "GeometricTranslation", Vec3.Zero ) )
		* Euler( Prop( model, "GeometricRotation", Vec3.Zero ), 0 )
		* Xform.Scale( Prop( model, "GeometricScaling", Vec3.One ) );

	static Vec3 Prop( FbxNode model, string name, Vec3 fallback )
	{
		foreach ( var p in model.Child( "Properties70" )?.Children ?? new List<FbxNode>() )
			if ( p.Props.Count >= 7 && p.Props[0] as string == name )
				return new Vec3( (float)Convert.ToDouble( p.Props[4] ), (float)Convert.ToDouble( p.Props[5] ), (float)Convert.ToDouble( p.Props[6] ) );
		return fallback;
	}

	static double PropScalar( FbxNode node, string name, double fallback )
	{
		foreach ( var p in node?.Child( "Properties70" )?.Children ?? new List<FbxNode>() )
			if ( p.Props.Count >= 5 && p.Props[0] as string == name && p.Props[4] is not string )
				return Convert.ToDouble( p.Props[4] is bool b ? (b ? 1 : 0) : p.Props[4] );
		return fallback;
	}

	/// <summary>Euler angles in degrees, composed in FBX's EFbxRotationOrder: 0 XYZ, 1 XZY, 2 YZX,
	/// 3 YXZ, 4 ZXY, 5 ZYX - the first letter is applied first. Anything else reads as XYZ.</summary>
	static Xform Euler( Vec3 degrees, int order )
	{
		var r = degrees * (MathF.PI / 180f);
		var x = Xform.Rotate( new Vec3( 1, 0, 0 ), r.x );
		var y = Xform.Rotate( new Vec3( 0, 1, 0 ), r.y );
		var z = Xform.Rotate( new Vec3( 0, 0, 1 ), r.z );

		return order switch
		{
			1 => y * z * x,
			2 => x * z * y,
			3 => z * x * y,
			4 => y * x * z,
			5 => x * y * z,
			_ => z * y * x,
		};
	}

	/// <summary>
	/// GlobalSettings and the header: units, axes, creator and version. Absent values keep the FBX
	/// SDK's defaults.
	/// </summary>
	static void ReadSettings( FbxNode root, FbxScene scene )
	{
		var header = root.Child( "FBXHeaderExtension" );

		if ( header?.Child( "FBXVersion" )?.Props.FirstOrDefault() is { } version and not string )
			scene.Version = (int)Convert.ToDouble( version );

		if ( header?.Child( "Creator" )?.Props.FirstOrDefault() is string creator )
			scene.Creator = creator;
		else if ( root.Child( "Creator" )?.Props.FirstOrDefault() is string topCreator )
			scene.Creator = topCreator;

		var settings = root.Child( "GlobalSettings" );

		if ( settings is null )
			return;

		var unit = PropScalar( settings, "UnitScaleFactor", 1 );
		scene.UnitScale = unit > 0 && double.IsFinite( unit ) ? unit : 1;

		(int, int) Axis( string name, (int Axis, int Sign) fallback )
		{
			var axis = (int)PropScalar( settings, name, fallback.Axis );
			var sign = PropScalar( settings, name + "Sign", fallback.Sign ) < 0 ? -1 : 1;
			return axis is >= 0 and <= 2 ? (axis, sign) : fallback;
		}

		scene.Up = Axis( "UpAxis", scene.Up );
		scene.Front = Axis( "FrontAxis", scene.Front );
		scene.Coord = Axis( "CoordAxis", scene.Coord );
	}

	/// <summary>
	/// One per-corner channel (normals, UVs, colours) read through its mapping and reference
	/// modes. Null when the geometry has no such layer, or the layer's mapping is one this does not
	/// know - then the channel is simply absent rather than wrong.
	/// </summary>
	static T[] Layer<T>( FbxNode geometry, string layerName, string dataName, string indexName, int stride,
		List<int> cornerPoint, List<int> cornerPolygon, Func<double[], int, T> make )
	{
		var layer = geometry.Child( layerName );
		var data = layer?.Child( dataName )?.Doubles();

		if ( data is null || data.Length < stride )
			return null;

		var mapping = layer.Child( "MappingInformationType" )?.Props.FirstOrDefault() as string ?? "ByPolygonVertex";
		var reference = layer.Child( "ReferenceInformationType" )?.Props.FirstOrDefault() as string ?? "Direct";
		var index = reference is "IndexToDirect" or "Index" ? layer.Child( indexName )?.Ints() : null;

		if ( reference is "IndexToDirect" or "Index" && index is null )
			return null;

		var corners = new T[cornerPoint.Count];
		var count = data.Length / stride;

		for ( var c = 0; c < corners.Length; c++ )
		{
			var key = mapping switch
			{
				"ByPolygonVertex" => c,
				"ByVertice" or "ByVertex" or "ByControlPoint" => cornerPoint[c],
				"ByPolygon" => cornerPolygon[c],
				"AllSame" => 0,
				_ => -1,
			};

			if ( key < 0 && mapping is not ("ByPolygonVertex" or "ByVertice" or "ByVertex" or "ByControlPoint" or "ByPolygon" or "AllSame") )
				return null;

			if ( index is not null )
				key = key >= 0 && key < index.Length ? index[key] : -1;

			if ( key >= 0 && key < count )
				corners[c] = make( data, key * stride );
		}

		return corners;
	}

	/// <summary>Normals follow the inverse transpose, so a non-uniform scale bends them correctly.</summary>
	static Vec3[] NormalsToGlobal( Vec3[] normals, Xform placement )
	{
		Xform inverse;

		try { inverse = placement.Inverse; }
		catch ( InvalidOperationException ) { return normals; }

		var result = new Vec3[normals.Length];

		for ( var i = 0; i < normals.Length; i++ )
		{
			var n = normals[i];
			var g = new Vec3( Vec3.Dot( inverse.X, n ), Vec3.Dot( inverse.Y, n ), Vec3.Dot( inverse.Z, n ) );
			result[i] = g.LengthSquared > 1e-20f ? g.Normal : n;
		}

		return result;
	}
}

/// <summary>One record of a binary FBX: a name, its typed properties and its children.</summary>
public sealed class FbxNode
{
	public string Name = "";
	public List<object> Props = new();
	public List<FbxNode> Children = new();

	public FbxNode Child( string name ) => Children.FirstOrDefault( c => c.Name == name );

	/// <summary>An object's name, the part of "name\0\x01Class" before the separator.</summary>
	public string ObjectName => Props.Count > 1 && Props[1] is string s ? s.Split( '\0' )[0] : "";

	/// <summary>An object's class, e.g. "Mesh", "LimbNode", "Skin", "Cluster".</summary>
	public string ClassName => Props.Count > 2 && Props[2] is string s ? s : "";

	public double[] Doubles() => Props.FirstOrDefault() switch
	{
		double[] d => d,
		float[] f => f.Select( x => (double)x ).ToArray(),
		_ => null
	};

	public int[] Ints() => Props.FirstOrDefault() switch
	{
		int[] i => i,
		long[] l => l.Select( x => (int)x ).ToArray(),
		_ => null
	};

	/// <summary>Binary FBX, or ASCII FBX re-read through <see cref="FbxBinary"/>'s text reader.</summary>
	public static FbxNode ParseAny( byte[] file )
	{
		if ( IsBinary( file ) )
			return Parse( file );

		if ( !IsAscii( file ) )
			throw new FormatException( "not an FBX file: no binary FBX header and no ASCII FBX header" );

		byte[] binary;

		try
		{
			binary = FbxBinary.FromAscii( Encoding.UTF8.GetString( file ) );
		}
		catch ( Exception e ) when ( e is FormatException or OverflowException or InvalidOperationException or ArgumentException )
		{
			throw new FormatException( $"this ASCII FBX uses syntax the kernel's text reader does not understand ({e.Message}); re-export it as binary FBX" );
		}

		return Parse( binary );
	}

	const string BinaryMagic = "Kaydara FBX Binary  ";

	/// <summary>True when the bytes start with the binary FBX magic.</summary>
	public static bool IsBinary( byte[] file ) =>
		file is not null && file.Length >= 27 && Encoding.ASCII.GetString( file, 0, BinaryMagic.Length ) == BinaryMagic;

	/// <summary>True when the bytes look like ASCII FBX: an FBXHeaderExtension block near the top, the
	/// way every ASCII FBX starts after its comment lines.</summary>
	public static bool IsAscii( byte[] file )
	{
		if ( file is null || file.Length < 16 )
			return false;

		var head = Encoding.UTF8.GetString( file, 0, Math.Min( file.Length, 4096 ) );
		return head.Contains( "FBXHeaderExtension:", StringComparison.Ordinal );
	}

	public static FbxNode Parse( byte[] file )
	{
		if ( !IsBinary( file ) )
			throw new FormatException( "not a binary FBX (ASCII FBX is not supported; re-export as binary)" );

		var version = BitConverter.ToInt32( file, 23 );
		var wide = version >= 7500;
		var root = new FbxNode { Name = "" };
		var at = 27;

		while ( at < file.Length )
		{
			var node = ReadNode( file, ref at, wide );
			if ( node is null ) break;
			root.Children.Add( node );
		}

		return root;
	}

	static FbxNode ReadNode( byte[] b, ref int at, bool wide )
	{
		long end, count;

		if ( wide )
		{
			end = BitConverter.ToInt64( b, at );
			count = BitConverter.ToInt64( b, at + 8 );
			at += 24;
		}
		else
		{
			end = BitConverter.ToUInt32( b, at );
			count = BitConverter.ToUInt32( b, at + 4 );
			at += 12;
		}

		var nameLength = b[at++];

		// The null record that closes a list of children.
		if ( end == 0 )
			return null;

		var node = new FbxNode { Name = Encoding.ASCII.GetString( b, at, nameLength ) };
		at += nameLength;

		for ( var i = 0; i < count; i++ )
			node.Props.Add( ReadProp( b, ref at ) );

		while ( at < end )
		{
			var child = ReadNode( b, ref at, wide );
			if ( child is null ) break;
			node.Children.Add( child );
		}

		at = (int)end;
		return node;
	}

	static object ReadProp( byte[] b, ref int at )
	{
		var type = (char)b[at++];

		switch ( type )
		{
			case 'Y': at += 2; return (long)BitConverter.ToInt16( b, at - 2 );
			case 'C': at += 1; return b[at - 1] != 0;
			case 'I': at += 4; return (long)BitConverter.ToInt32( b, at - 4 );
			case 'F': at += 4; return (double)BitConverter.ToSingle( b, at - 4 );
			case 'D': at += 8; return BitConverter.ToDouble( b, at - 8 );
			case 'L': at += 8; return BitConverter.ToInt64( b, at - 8 );
			case 'S':
			case 'R':
			{
				var length = BitConverter.ToInt32( b, at );
				at += 4;
				var bytes = new byte[length];
				Array.Copy( b, at, bytes, 0, length );
				at += length;
				return type == 'S' ? Encoding.UTF8.GetString( bytes ) : bytes;
			}
			case 'f': case 'd': case 'l': case 'i': case 'b':
			{
				var length = BitConverter.ToInt32( b, at );
				var encoding = BitConverter.ToInt32( b, at + 4 );
				var compressed = BitConverter.ToInt32( b, at + 8 );
				at += 12;

				var size = type switch { 'f' or 'i' => 4, 'd' or 'l' => 8, _ => 1 };
				byte[] data;

				if ( encoding == 1 )
				{
					using var input = new MemoryStream( b, at, compressed );
					using var z = new ZLibStream( input, CompressionMode.Decompress );
					data = new byte[length * size];
					var read = 0;
					while ( read < data.Length )
					{
						var n = z.Read( data, read, data.Length - read );
						if ( n == 0 ) break;
						read += n;
					}
				}
				else
				{
					data = new byte[length * size];
					Array.Copy( b, at, data, 0, data.Length );
				}

				at += compressed;

				switch ( type )
				{
					case 'f': { var a = new float[length]; Buffer.BlockCopy( data, 0, a, 0, data.Length ); return a; }
					case 'd': { var a = new double[length]; Buffer.BlockCopy( data, 0, a, 0, data.Length ); return a; }
					case 'l': { var a = new long[length]; Buffer.BlockCopy( data, 0, a, 0, data.Length ); return a; }
					case 'i': { var a = new int[length]; Buffer.BlockCopy( data, 0, a, 0, data.Length ); return a; }
					default: return data;
				}
			}
			default:
				throw new FormatException( $"unknown FBX property type '{type}' at byte {at - 1}" );
		}
	}
}
