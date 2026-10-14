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
/// BINARY FBX ONLY (7.x, both the 32-bit and 64-bit offset layouts). ASCII FBX is refused rather
/// than half-read. Blender, Maya and the kernel's own <see cref="FbxWriter"/> all write binary.
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
	}

	public List<Mesh> Meshes = new();

	/// <summary>Each bone's global transform at the bind pose, by name.</summary>
	public Dictionary<string, Xform> BoneBind = new();

	/// <summary>Each bone's parent bone, by name; absent for a root.</summary>
	public Dictionary<string, string> BoneParent = new();

	public static FbxScene ReadFile( string path ) => Read( File.ReadAllBytes( path ) );

	public static FbxScene Read( byte[] file )
	{
		var root = FbxNode.Parse( file );
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
			foreach ( var v in geometry.Child( "PolygonVertexIndex" )?.Ints() ?? Array.Empty<int>() )
			{
				current.Add( v < 0 ? ~v : v );
				if ( v >= 0 ) continue;
				mesh.Polygons.Add( current.ToArray() );
				current.Clear();
			}

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
			var placement = model is null ? Xform.Identity : GlobalOf( model, byId, Parents );
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
			scene.Meshes.Add( mesh );
		}

		// Bones without a cluster (not skinning anything) still help line the skeleton up.
		foreach ( var limb in objects.Children.Where( n => n.Name == "Model" && n.ClassName is "LimbNode" or "Null" ) )
		{
			if ( !scene.BoneBind.ContainsKey( limb.ObjectName ) )
				scene.BoneBind[limb.ObjectName] = GlobalOf( limb, byId, Parents );

			if ( Parents( (long)limb.Props[0], "Model" ).FirstOrDefault() is { } parent && parent.ClassName is "LimbNode" or "Null" )
				scene.BoneParent[limb.ObjectName] = parent.ObjectName;
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

	static Xform LocalOf( FbxNode model )
	{
		Vec3 Prop( string name, Vec3 fallback )
		{
			foreach ( var p in model.Child( "Properties70" )?.Children ?? new List<FbxNode>() )
				if ( p.Props.Count >= 7 && p.Props[0] as string == name )
					return new Vec3( (float)Convert.ToDouble( p.Props[4] ), (float)Convert.ToDouble( p.Props[5] ), (float)Convert.ToDouble( p.Props[6] ) );
			return fallback;
		}

		static Xform Euler( Vec3 degrees )
		{
			var r = degrees * (MathF.PI / 180f);
			return Xform.Rotate( new Vec3( 0, 0, 1 ), r.z ) * Xform.Rotate( new Vec3( 0, 1, 0 ), r.y ) * Xform.Rotate( new Vec3( 1, 0, 0 ), r.x );
		}

		return Xform.Translate( Prop( "Lcl Translation", Vec3.Zero ) )
			* Euler( Prop( "PreRotation", Vec3.Zero ) )
			* Euler( Prop( "Lcl Rotation", Vec3.Zero ) )
			* Xform.Scale( Prop( "Lcl Scaling", Vec3.One ) );
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

	public static FbxNode Parse( byte[] file )
	{
		const string magic = "Kaydara FBX Binary  ";

		if ( file.Length < 27 || Encoding.ASCII.GetString( file, 0, magic.Length ) != magic )
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
