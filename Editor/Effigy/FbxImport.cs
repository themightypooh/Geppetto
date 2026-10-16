using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// An FBX file as Effigy bodies: one piece per mesh, in inches and Z-up, with its UVs, vertex
/// colours and material slots, plus the file's skeleton and skin weights when it has them.
///
/// WHY THIS IS SEPARATE FROM <see cref="FbxScene"/>. The scene reader was written for one job -
/// lining a compiled model's skin weights up with the engine - and it keeps the file's own space
/// and units, because that is what <see cref="ModelDump"/> compares against. Import wants the
/// opposite: everything converted ONCE into the space every other body is in. Doing the
/// conversion here leaves the dump path byte-for-byte what it was.
///
/// UNITS. FBX states centimetres per file unit (UnitScaleFactor). An Effigy unit is an inch, so a
/// point lands at <c>value × UnitScaleFactor / 2.54</c>. That is exactly the inverse of
/// <see cref="FbxWriter.WriteBinaryFile"/>, which writes inches and declares 2.54, so the kernel's
/// own exports come back at the size they left. One exception, said out loud: the writer's ASCII
/// form (<see cref="FbxWriter.Write"/>, the engine's compile path and the test samples) writes
/// the same inches but leaves the factor at its default 1, because fbx2dmx reads raw numbers. A file
/// whose Creator is Effigy and whose factor is 1 is therefore read as inches, not centimetres.
///
/// AXES. The file names its up, front and right axes; Effigy's are +Z up, -Y front, +X right, the
/// same declaration the writer makes. The map between the two is a signed permutation - a
/// rotation for every right-handed file - and a file declaring a left-handed system arrives
/// mirrored with its winding reversed so faces still point out.
///
/// WINDING. Any transform that flips handedness - a mirrored node scale, a left-handed file -
/// reverses each polygon's corner order, the same rule <see cref="Xform.FlipsWinding"/> states
/// for the rest of the kernel. Without it a mirrored part imports inside out.
///
/// NORMALS ARE CHECKED, NOT KEPT. A PolyMesh has no stored normals - every exporter derives them
/// from the faces (<see cref="MeshNormals"/>) - so the file's normals cannot travel. They are
/// still read, and <see cref="Result.InwardPieces"/> counts pieces whose file normals disagree
/// with their faces; that is how an inside-out import gets reported instead of shipped.
/// </summary>
public static class FbxImport
{
	/// <summary>Centimetres in an inch. <see cref="FbxWriter.InchesToCentimetres"/> is the same fact.</summary>
	const double CentimetresPerInch = FbxWriter.InchesToCentimetres;

	public sealed class Result
	{
		/// <summary>One per mesh in the file, named after its model node.</summary>
		public List<ObjReader.ObjPiece> Pieces = new();

		/// <summary>Parallel to <see cref="Pieces"/>: each vertex's (bone name, weight), or null for a
		/// piece with no skin. Kept by NAME because the bones are only resolved against a rig when
		/// the bodies are published - the rig can change without the file changing.</summary>
		public List<List<(string Bone, float Weight)>[]> Weights = new();

		/// <summary>Slot number → the material name the file gave it.</summary>
		public SortedDictionary<int, string> MaterialSlots = new();

		/// <summary>The file's skeleton in Effigy space, or null when it has no bones.</summary>
		public Skeleton Skeleton;

		/// <summary>How many pieces carry skin weights.</summary>
		public int SkinnedPieces => Weights.Count( w => w is not null );

		/// <summary>Pieces whose file normals mostly point the other way from their faces.</summary>
		public int InwardPieces;

		/// <summary>Things the conversion did that a person should know: polygons dropped, unit
		/// assumptions, leaf bones folded into lengths.</summary>
		public List<string> Notes = new();

		/// <summary>Centimetres per file unit the file declared, and the scale that was applied.</summary>
		public double UnitScale, AppliedScale;
	}

	/// <summary>True when the bytes are an FBX file, binary or ASCII.</summary>
	public static bool IsFbx( byte[] bytes ) => FbxNode.IsBinary( bytes ) || FbxNode.IsAscii( bytes );

	public static Result Read( byte[] bytes )
	{
		if ( FbxNode.IsBinary( bytes ) && BitConverter.ToInt32( bytes, 23 ) is var version and < 7000 )
			throw new FormatException( $"this is FBX {version / 1000}.{version % 1000 / 100}, an older object model with no Geometry objects; re-export it as FBX 7.x (2011 or newer)" );

		var scene = FbxScene.Read( bytes );
		return FromScene( scene );
	}

	public static Result FromScene( FbxScene scene )
	{
		var result = new Result { UnitScale = scene.UnitScale };

		var effigyWroteInches = scene.Creator.StartsWith( "Effigy", StringComparison.Ordinal ) && Math.Abs( scene.UnitScale - 1 ) < 1e-9;
		var scale = effigyWroteInches ? 1.0 : scene.UnitScale / CentimetresPerInch;
		result.AppliedScale = scale;

		if ( effigyWroteInches )
			result.Notes.Add( "Written by Effigy's engine export, which keeps inches without declaring them - read as inches." );

		var axes = AxisMap( scene );
		var toEffigy = axes * Xform.Scale( new Vec3( (float)scale, (float)scale, (float)scale ) );

		if ( axes.FlipsWinding )
			result.Notes.Add( "The file declares a left-handed axis system; it was mirrored into Effigy's and its faces turned to match." );

		var slots = AssignSlots( scene, result.MaterialSlots );
		var dropped = 0;

		for ( var m = 0; m < scene.Meshes.Count; m++ )
		{
			var source = scene.Meshes[m];
			var flip = (toEffigy * source.Placement).FlipsWinding;
			var (mesh, weights, droppedHere, inward) = Convert( source, toEffigy, axes, flip, slots[m] );
			dropped += droppedHere;

			if ( mesh.FaceCount == 0 )
				continue;

			if ( inward )
				result.InwardPieces++;

			result.Pieces.Add( new ObjReader.ObjPiece( source.Name, mesh ) );
			result.Weights.Add( weights );
		}

		if ( dropped > 0 )
			result.Notes.Add( $"{dropped} polygon(s) with fewer than three distinct corners were left out." );

		// The geometry stands on its own: a skeleton this cannot make sense of is a note, not a
		// reason to refuse the meshes.
		try
		{
			result.Skeleton = BuildSkeleton( scene, axes, scale, result.Notes );
		}
		catch ( Exception e ) when ( e is ArgumentException or InvalidOperationException or KeyNotFoundException )
		{
			result.Skeleton = null;
			result.Notes.Add( $"The file's skeleton could not be read ({e.Message}); the meshes came in without it." );
		}

		return result;
	}

	/// <summary>
	/// The rotation (or mirror) from the file's declared axes to Effigy's: right → +X, up → +Z,
	/// front → -Y. A signed permutation, so it is exact.
	/// </summary>
	public static Xform AxisMap( FbxScene scene )
	{
		static Vec3 Unit( (int Axis, int Sign) a ) => a.Axis switch
		{
			0 => new Vec3( a.Sign, 0, 0 ),
			1 => new Vec3( 0, a.Sign, 0 ),
			_ => new Vec3( 0, 0, a.Sign ),
		};

		// Three distinct axes or the declaration means nothing; fall back to the SDK default.
		var right = scene.Coord;
		var up = scene.Up;
		var front = scene.Front;

		if ( right.Axis == up.Axis || right.Axis == front.Axis || up.Axis == front.Axis )
		{
			right = (0, 1);
			up = (1, 1);
			front = (2, 1);
		}

		// file = F * effigy, where F's columns are the file directions of Effigy's +X, +Y, +Z:
		// +X is right, +Z is up, +Y is -front. The map back is F's inverse, its transpose.
		var f = new Xform( Unit( right ), Unit( front ) * -1f, Unit( up ), Vec3.Zero );
		return f.Inverse;
	}

	/// <summary>
	/// Material name → slot, the same across every mesh in the file. A name the kernel wrote
	/// (<c>material_N</c>, see <see cref="ObjWriter.DefaultMaterialName"/>) keeps slot N, so an
	/// Effigy export comes back on the slots it left on. Every other name takes the lowest free slot
	/// in the order the file first uses it. Returns, per mesh, its local material index → slot.
	/// </summary>
	static List<int[]> AssignSlots( FbxScene scene, SortedDictionary<int, string> named )
	{
		var bySlotName = new Dictionary<string, int>( StringComparer.Ordinal );

		foreach ( var mesh in scene.Meshes )
		{
			foreach ( var name in mesh.Materials )
			{
				if ( TryDefaultSlot( name, out var slot ) && !named.ContainsKey( slot ) )
				{
					bySlotName[name] = slot;
					named[slot] = name;
				}
			}
		}

		var next = 0;

		foreach ( var mesh in scene.Meshes )
		{
			foreach ( var name in mesh.Materials )
			{
				if ( bySlotName.ContainsKey( name ) )
					continue;

				while ( named.ContainsKey( next ) )
					next++;

				bySlotName[name] = next;
				named[next] = name;
			}
		}

		return scene.Meshes.Select( m => m.Materials.Select( n => bySlotName[n] ).ToArray() ).ToList();
	}

	static bool TryDefaultSlot( string name, out int slot )
	{
		const string prefix = "material_";
		slot = -1;

		return name is not null
			&& name.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
			&& int.TryParse( name.AsSpan( prefix.Length ), NumberStyles.Integer, CultureInfo.InvariantCulture, out slot )
			&& slot >= 0;
	}

	static (PolyMesh Mesh, List<(string Bone, float Weight)>[] Weights, int Dropped, bool Inward) Convert(
		FbxScene.Mesh source, Xform toEffigy, Xform axes, bool flip, int[] slotOf )
	{
		var mesh = new PolyMesh();
		var remap = new int[source.Points.Length];
		Array.Fill( remap, -1 );

		var colourSum = new Dictionary<int, (Vec4 Sum, int Count)>();
		var dropped = 0;
		var agree = 0;
		var disagree = 0;
		var corner = 0;

		for ( var p = 0; p < source.Polygons.Count; p++ )
		{
			var polygon = source.Polygons[p];
			var first = corner;
			corner += polygon.Length;

			if ( polygon.Any( i => i < 0 || i >= source.Points.Length ) )
			{
				dropped++;
				continue;
			}

			// A corner repeating the one before it (a collapsed edge, common in decimated scans) is
			// skipped rather than kept as a zero-length edge; what is left must still be a polygon.
			var kept = new List<int>( polygon.Length );

			for ( var k = 0; k < polygon.Length; k++ )
			{
				if ( kept.Count > 0 && polygon[kept[^1]] == polygon[k] )
					continue;

				kept.Add( k );
			}

			while ( kept.Count > 1 && polygon[kept[^1]] == polygon[kept[0]] )
				kept.RemoveAt( kept.Count - 1 );

			if ( kept.Count < 3 )
			{
				dropped++;
				continue;
			}

			var count = kept.Count;
			var indices = new int[count];
			var uvs = new Vec2[count];

			for ( var k = 0; k < count; k++ )
			{
				// Reversed corner order when the transform mirrors: same polygon, same UV per corner,
				// faces pointing out again.
				var from = kept[flip ? count - 1 - k : k];
				var point = polygon[from];
				var c = first + from;

				if ( remap[point] < 0 )
					remap[point] = mesh.AddVertex( toEffigy.TransformPoint( source.Points[point] ) );

				indices[k] = remap[point];

				// FBX's V runs up from the bottom; Effigy's runs down from the top. The same flip
				// the writer makes on the way out.
				if ( source.CornerUVs is { } cornerUVs && c < cornerUVs.Length )
					uvs[k] = new Vec2( cornerUVs[c].x, 1f - cornerUVs[c].y );

				if ( source.CornerColors is { } colours && c < colours.Length )
				{
					colourSum.TryGetValue( indices[k], out var acc );
					colourSum[indices[k]] = (acc.Sum + colours[c], acc.Count + 1);
				}
			}

			var local = p < source.PolygonMaterial.Length ? source.PolygonMaterial[p] : 0;
			var slot = local >= 0 && local < slotOf.Length ? slotOf[local] : 0;
			var face = mesh.AddFace( indices, uvs, slot );

			if ( source.CornerNormals is { } normals && first + polygon.Length <= normals.Length )
			{
				var faceNormal = mesh.FaceNormal( face );
				var fileNormal = Vec3.Zero;

				for ( var k = 0; k < polygon.Length; k++ )
					fileNormal += axes.TransformDirection( normals[first + k] );

				var d = Vec3.Dot( faceNormal, fileNormal );

				if ( d > 0 )
					agree++;
				else if ( d < 0 )
					disagree++;
			}
		}

		if ( colourSum.Count > 0 )
		{
			mesh.VertexColors = new Vec4[mesh.VertexCount];

			for ( var v = 0; v < mesh.VertexCount; v++ )
				mesh.VertexColors[v] = colourSum.TryGetValue( v, out var acc ) && acc.Count > 0
					? acc.Sum * (1f / acc.Count)
					: new Vec4( 1, 1, 1, 1 );
		}

		List<(string Bone, float Weight)>[] weights = null;

		if ( source.Weights is not null && mesh.VertexCount > 0 )
		{
			weights = new List<(string, float)>[mesh.VertexCount];

			for ( var point = 0; point < remap.Length; point++ )
			{
				if ( remap[point] >= 0 )
					weights[remap[point]] = point < source.Weights.Length && source.Weights[point] is { } w
						? new List<(string, float)>( w )
						: new List<(string, float)>();
			}
		}

		return (mesh, weights, dropped, disagree > agree);
	}

	/// <summary>
	/// The file's bones as an Effigy skeleton: bind pose in Effigy space, parents first.
	///
	/// WHICH NODES ARE BONES. LimbNodes, and any node a skin cluster deforms with. A Null that skins
	/// nothing - Blender's armature object, a grouping empty - is not a bone; its transform is
	/// already folded into the bind matrices below it. Blender's "_end" leaf bones exist only to
	/// show the length of the last bone in a chain, so they set that length and are not added.
	///
	/// LENGTH. Effigy's own writer stores a bone's length as its NodeAttribute Size; other
	/// exporters store a display radius there, so for them the length is the distance to the bone's
	/// child (or leaf), and failing that the parent's.
	/// </summary>
	static Skeleton BuildSkeleton( FbxScene scene, Xform axes, double scale, List<string> notes )
	{
		bool IsBone( string name ) =>
			scene.SkinBones.Contains( name )
			|| (scene.BoneClass.TryGetValue( name, out var kind ) && kind == "LimbNode");

		var children = new Dictionary<string, List<string>>();

		foreach ( var (child, parent) in scene.BoneParent )
		{
			if ( !children.TryGetValue( parent, out var list ) )
				children[parent] = list = new List<string>();

			list.Add( child );
		}

		var leaves = new HashSet<string>( StringComparer.Ordinal );

		foreach ( var name in scene.BoneOrder )
		{
			if ( name.EndsWith( "_end", StringComparison.Ordinal )
				&& !scene.SkinBones.Contains( name )
				&& !children.ContainsKey( name )
				&& scene.BoneParent.ContainsKey( name ) )
				leaves.Add( name );
		}

		// A nameless node cannot be a bone: every format, and Skeleton.AddBone, keys on the name.
		var names = scene.BoneOrder.Where( n => !string.IsNullOrWhiteSpace( n ) && IsBone( n ) && !leaves.Contains( n ) && scene.BoneBind.ContainsKey( n ) ).ToList();

		// Clustered bones the Objects walk never listed (a Model of another class) still belong.
		foreach ( var name in scene.SkinBones )
			if ( !string.IsNullOrWhiteSpace( name ) && !names.Contains( name ) && scene.BoneBind.ContainsKey( name ) )
				names.Add( name );

		if ( names.Count == 0 )
			return null;

		var included = new HashSet<string>( names, StringComparer.Ordinal );

		string BoneParentOf( string name )
		{
			var guard = 0;

			for ( var p = scene.BoneParent.GetValueOrDefault( name ); p is not null && guard++ < 256; p = scene.BoneParent.GetValueOrDefault( p ) )
				if ( included.Contains( p ) )
					return p;

			return null;
		}

		var s = (float)scale;
		var world = new Dictionary<string, Xform>( StringComparer.Ordinal );

		foreach ( var name in names )
		{
			var g = scene.BoneBind[name];
			var basis = axes * new Xform( g.X, g.Y, g.Z, Vec3.Zero );

			static Vec3 Unit( Vec3 v, Vec3 fallback ) => v.LengthSquared > 1e-20f ? v.Normal : fallback;

			world[name] = new Xform(
				Unit( basis.X, new Vec3( 1, 0, 0 ) ),
				Unit( basis.Y, new Vec3( 0, 1, 0 ) ),
				Unit( basis.Z, new Vec3( 0, 0, 1 ) ),
				axes.TransformPoint( g.Origin ) * s );
		}

		var skeleton = new Skeleton();
		var index = new Dictionary<string, int>( StringComparer.Ordinal );
		var effigySizes = scene.Creator.StartsWith( "Effigy", StringComparison.Ordinal );

		void Add( string name, int depth )
		{
			if ( index.ContainsKey( name ) || depth > 256 )
				return;

			var parentName = BoneParentOf( name );

			if ( parentName is not null && !index.ContainsKey( parentName ) )
				Add( parentName, depth + 1 );

			var parent = parentName is null ? -1 : index[parentName];
			var w = world[name];
			var local = parent < 0 ? w : SafeInverse( world[parentName] ) * w;
			var length = BoneLength( name, w, scene, children, leaves, world, effigySizes, s );

			if ( length <= 1e-4f )
				length = parent >= 0 ? MathF.Max( skeleton.Bones[parent].Length * 0.5f, 0.1f ) : 1f;

			index[name] = skeleton.AddBone( name, parent, local, length );
		}

		foreach ( var name in names )
			Add( name, 0 );

		if ( leaves.Count > 0 )
			notes.Add( $"{leaves.Count} end-of-chain marker bone(s) set their chain's length and were not added as bones." );

		return skeleton;
	}

	static float BoneLength( string name, Xform world, FbxScene scene, Dictionary<string, List<string>> children,
		HashSet<string> leaves, Dictionary<string, Xform> worlds, bool effigySizes, float scale )
	{
		if ( effigySizes && scene.BoneSize.TryGetValue( name, out var size ) && size > 0 )
			return size * scale;

		if ( !children.TryGetValue( name, out var kids ) )
			return 0;

		foreach ( var kid in kids )
		{
			if ( worlds.TryGetValue( kid, out var k ) )
				return (k.Origin - world.Origin).Length;

			if ( leaves.Contains( kid ) && scene.BoneBind.TryGetValue( kid, out var leafBind ) )
				return LeafDistance( leafBind, world, scene, scale );
		}

		return 0;
	}

	static float LeafDistance( Xform leafBind, Xform parentWorld, FbxScene scene, float scale )
	{
		var leaf = AxisMap( scene ).TransformPoint( leafBind.Origin ) * scale;
		return (leaf - parentWorld.Origin).Length;
	}

	static Xform SafeInverse( Xform x )
	{
		try { return x.Inverse; }
		catch ( InvalidOperationException ) { return Xform.Identity; }
	}

	/// <summary>
	/// A piece's named weights against a rig: per vertex, its influences on bones the rig has,
	/// renormalised. Returns null when any vertex would be left with no weight at all - a part
	/// half-weighted is worse than one the binder weights from scratch - and lists every file bone
	/// the rig lacks in <paramref name="missing"/>.
	/// </summary>
	public static SkinWeights Resolve( List<(string Bone, float Weight)>[] weights, Skeleton rig, ISet<string> missing, out int unweighted )
	{
		unweighted = 0;

		if ( weights is null || rig is not { Count: > 0 } )
			return null;

		var skin = new SkinWeights();

		foreach ( var vertex in weights )
		{
			var acc = new Dictionary<int, float>();

			foreach ( var (bone, weight) in vertex ?? new List<(string, float)>() )
			{
				var b = rig.IndexOf( bone );

				if ( b < 0 )
				{
					missing?.Add( bone );
					continue;
				}

				if ( weight > 0 && float.IsFinite( weight ) )
					acc[b] = acc.GetValueOrDefault( b ) + weight;
			}

			if ( acc.Count == 0 )
			{
				unweighted++;
				skin.Vertices.Add( Array.Empty<BoneWeight>() );
				continue;
			}

			skin.Vertices.Add( SkinWeights.Finish( acc ) );
		}

		return unweighted > 0 ? null : skin;
	}
}
