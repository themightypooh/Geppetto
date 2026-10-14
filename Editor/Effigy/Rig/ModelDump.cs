using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// A compiled model read out of the engine into a file the kernel can use without it: the bind
/// skeleton, the render triangles with their skin weights, optionally poses sampled from one of
/// its sequences, and optionally the props it was placed among.
///
/// Written by the editor's <c>effigy_model_dump</c> command; also reads the older
/// <c>arms_pose_dump</c> files, which are the same thing without the meshes.
///
/// EVERYTHING IS IN THE MODEL'S OWN SPACE. Bind poses, sampled poses and the skin are what the
/// engine reports for the model; props are carried into that space by
/// <see cref="Prop.Transform"/>, so the arm and the switch it reaches for can be checked against
/// each other directly.
///
/// The file:
/// <code>
/// {
///   "Model": "models/x.vmdl", "Sequence": "idle",
///   "Transform": [px,py,pz, qx,qy,qz,qw, sx,sy,sz],      // where the model stood; optional
///   "Bones": [ { "Name": "hand_R", "Parent": "arm_lower_R", "Bind": [px,py,pz, qx,qy,qz,qw] } ],
///   "Poses": { "0.5": { "hand_R": [px,py,pz, qx,qy,qz,qw] } },            // model space; optional
///   "Meshes": [ { "Name": "glove", "Material": "materials/glove.vmat",
///                 "Positions": [x,y,z, ...], "Triangles": [i,j,k, ...],
///                 "BoneIndices": [4 per vertex, into Bones], "BoneWeights": [4 per vertex] } ],
///   "Props": [ { "Name": "switch", "Model": "models/switch.vmdl",
///                "Transform": [px,py,pz, qx,qy,qz,qw, sx,sy,sz],           // world space
///                "Meshes": [ { "Name", "Material", "Positions", "Triangles" } ] } ]
/// }
/// </code>
/// </summary>
public sealed class ModelDump
{
	public sealed class Part
	{
		public string Name = "";
		public string Material = "";
		public Vec3[] Positions = Array.Empty<Vec3>();

		/// <summary>Three vertex indices per triangle.</summary>
		public int[] Triangles = Array.Empty<int>();

		/// <summary>Per-vertex weights onto <see cref="ModelDump.Skeleton"/>'s bones, or null for a
		/// static mesh.</summary>
		public SkinWeights Weights;

		public int TriangleCount => Triangles.Length / 3;
	}

	public sealed class Prop
	{
		public string Name = "";
		public string Model = "";

		/// <summary>From the prop's own space to the dumped model's space.</summary>
		public Xform Transform = Xform.Identity;

		public List<Part> Meshes = new();

		/// <summary>One mesh's vertices placed in the dumped model's space.</summary>
		public Vec3[] Placed( Part part ) => part.Positions.Select( Transform.TransformPoint ).ToArray();
	}

	public string Model = "";
	public string Sequence = "";

	/// <summary>Where the model stood in the scene it was dumped from.</summary>
	public Xform Placement = Xform.Identity;

	/// <summary>Parents before children, so <see cref="RigAnimFile.ModelSpace"/> can walk it in order.</summary>
	public Skeleton Skeleton = new();

	/// <summary>Each bone's bind transform in model space, by skeleton index.</summary>
	public Xform[] BindWorld = Array.Empty<Xform>();

	/// <summary>Sampled poses by the time they were sampled at, model space, by skeleton index.
	/// A bone the engine did not report sits at its bind.</summary>
	public Dictionary<string, Xform[]> Poses = new();

	public List<Part> Meshes = new();
	public List<Prop> Props = new();

	/// <summary>Skinned vertices of a part at a pose given in model space.</summary>
	public Vec3[] Deform( Part part, IReadOnlyList<Xform> poseWorld ) =>
		part.Weights is null ? part.Positions : SkinBinder.Deform( part.Positions, part.Weights, BindWorld, poseWorld );

	public static ModelDump ReadFile( string path ) => Read( File.ReadAllText( path ) );

	public static ModelDump Read( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		var dump = new ModelDump
		{
			Model = Str( root, "Model" ) ?? "",
			Sequence = Str( root, "Sequence" ) ?? "",
			Placement = root.TryGetProperty( "Transform", out var placed ) ? ReadTransform( placed ) : Xform.Identity,
		};

		// Bones, reordered parents-first. The file's own order is kept as the index weights use.
		var names = new List<string>();
		var parents = new List<string>();
		var binds = new List<Xform>();

		if ( root.TryGetProperty( "Bones", out var bones ) )
		{
			foreach ( var b in bones.EnumerateArray() )
			{
				names.Add( Str( b, "Name" ) ?? $"bone{names.Count}" );
				parents.Add( Str( b, "Parent" ) );
				binds.Add( ReadTransform( b.GetProperty( "Bind" ) ) );
			}
		}

		var fileToSkeleton = new int[names.Count];
		var placedBones = new Dictionary<string, int>();
		var remaining = Enumerable.Range( 0, names.Count ).ToList();
		var bindWorld = new List<Xform>();

		while ( remaining.Count > 0 )
		{
			// A parent that is never listed would stall this; treat such a bone as a root.
			var next = remaining.FirstOrDefault( i => parents[i] is null || placedBones.ContainsKey( parents[i] ) || !names.Contains( parents[i] ), -1 );
			if ( next < 0 ) next = remaining[0];
			remaining.Remove( next );

			var parent = parents[next] is { } p && placedBones.TryGetValue( p, out var pi ) ? pi : -1;
			var world = binds[next];
			var local = parent >= 0 ? bindWorld[parent].Inverse * world : world;
			var index = dump.Skeleton.AddBone( names[next], parent, local );
			placedBones[names[next]] = index;
			fileToSkeleton[next] = index;
			bindWorld.Add( world );
		}

		dump.BindWorld = bindWorld.ToArray();

		if ( root.TryGetProperty( "Poses", out var poses ) && poses.ValueKind == JsonValueKind.Object )
		{
			foreach ( var pose in poses.EnumerateObject() )
			{
				var world = dump.BindWorld.ToArray();

				foreach ( var bone in pose.Value.EnumerateObject() )
					if ( placedBones.TryGetValue( bone.Name, out var i ) )
						world[i] = ReadTransform( bone.Value );

				dump.Poses[pose.Name] = world;
			}
		}

		if ( root.TryGetProperty( "Meshes", out var meshes ) )
			foreach ( var m in meshes.EnumerateArray() )
				dump.Meshes.Add( ReadPart( m, fileToSkeleton ) );

		if ( root.TryGetProperty( "Props", out var props ) )
		{
			var toModel = dump.Placement.Inverse;

			foreach ( var p in props.EnumerateArray() )
			{
				var prop = new Prop
				{
					Name = Str( p, "Name" ) ?? "",
					Model = Str( p, "Model" ) ?? "",
					Transform = toModel * (p.TryGetProperty( "Transform", out var t ) ? ReadTransform( t ) : Xform.Identity),
				};

				if ( p.TryGetProperty( "Meshes", out var pm ) )
					foreach ( var m in pm.EnumerateArray() )
						prop.Meshes.Add( ReadPart( m, null ) );

				dump.Props.Add( prop );
			}
		}

		return dump;
	}

	/// <summary>How well an FBX lined up with the dump, and how far weights had to reach.</summary>
	public sealed class FbxFit
	{
		/// <summary>FBX global space to the dump's model space.</summary>
		public Xform Map = Xform.Identity;

		/// <summary>Bones found in both, and the root-mean-square distance left between them
		/// after the fit - near zero when the FBX is the model's real source.</summary>
		public int BonesMatched;
		public float BoneError;

		/// <summary>The furthest any render vertex was from the FBX point it took weights from.</summary>
		public float WorstVertex;

		/// <summary>Vertices the source left unweighted, given a neighbour's weights.</summary>
		public int Filled;

		public override string ToString() =>
			$"{BonesMatched} bone(s) matched, {BoneError:0.####} left after the fit, weights reached at most {WorstVertex:0.####}"
			+ (Filled > 0 ? $", {Filled} unweighted vertex(es) took a neighbour's weights" : "");
	}

	/// <summary>
	/// Line an FBX up with this dump's model space by the bones both have. The engine imports an
	/// FBX through an axis swap and a unit scale that depend on the exporter, so rather than
	/// knowing them, this tries every axis permutation and sign with a fitted scale and
	/// translation and keeps the one that puts the FBX's bind-pose bone heads on the engine's.
	/// </summary>
	public FbxFit Fit( FbxScene fbx )
	{
		var pairs = new List<(Vec3 From, Vec3 To)>();

		for ( var i = 0; i < Skeleton.Count; i++ )
			if ( fbx.BoneBind.TryGetValue( Skeleton.Bones[i].Name, out var bind ) )
				pairs.Add( (bind.Origin, BindWorld[i].Origin) );

		if ( pairs.Count < 3 )
			throw new InvalidOperationException( $"only {pairs.Count} bone name(s) are in both the FBX and the model; need 3 to line them up" );

		var fromMean = pairs.Aggregate( Vec3.Zero, ( s, p ) => s + p.From ) / pairs.Count;
		var toMean = pairs.Aggregate( Vec3.Zero, ( s, p ) => s + p.To ) / pairs.Count;
		var best = new FbxFit { BoneError = float.MaxValue, BonesMatched = pairs.Count };

		foreach ( var axes in SignedPermutations() )
		{
			// Least-squares scale for this rotation, then the translation that centres it.
			float dot = 0, norm = 0;
			foreach ( var (f, t) in pairs )
			{
				var r = axes.TransformDirection( f - fromMean );
				dot += Vec3.Dot( r, t - toMean );
				norm += r.LengthSquared;
			}

			if ( norm <= 1e-12f || dot <= 0f )
				continue;

			var scale = dot / norm;
			var map = Xform.Translate( toMean ) * Xform.Scale( new Vec3( scale, scale, scale ) ) * axes * Xform.Translate( -fromMean );
			var error = MathF.Sqrt( pairs.Sum( p => (map.TransformPoint( p.From ) - p.To).LengthSquared ) / pairs.Count );

			if ( error < best.BoneError )
			{
				best.BoneError = error;
				best.Map = map;
			}
		}

		return best;
	}

	/// <summary>
	/// Give this dump's meshes the FBX's skin weights: each render vertex takes the weights of the
	/// nearest FBX control point once the FBX is lined up with <see cref="Fit"/>. The render mesh
	/// was compiled from those very points, so nearest is exact up to float noise; the fit's
	/// <see cref="FbxFit.WorstVertex"/> says so, and a large one says the FBX is not this model's.
	/// </summary>
	public FbxFit AttachWeights( FbxScene fbx )
	{
		var fit = Fit( fbx );
		var points = new List<(Vec3 Point, List<(string Bone, float Weight)> Weights)>();

		foreach ( var m in fbx.Meshes.Where( m => m.Weights is not null ) )
			for ( var i = 0; i < m.Points.Length; i++ )
				points.Add( (fit.Map.TransformPoint( m.Points[i] ), m.Weights[i]) );

		if ( points.Count == 0 )
			throw new InvalidOperationException( "the FBX has no skinned mesh to take weights from" );

		var grid = new PointGrid( points.Select( p => p.Point ).ToArray() );

		foreach ( var part in Meshes )
		{
			part.Weights = new SkinWeights( part.Positions.Length );

			for ( var v = 0; v < part.Positions.Length; v++ )
			{
				var (nearest, distance) = grid.Nearest( part.Positions[v] );
				fit.WorstVertex = MathF.Max( fit.WorstVertex, distance );
				part.Weights.Vertices[v] = ToSkeleton( points[nearest].Weights, fbx );
			}

			fit.Filled += FillUnweighted( part );
		}

		return fit;
	}

	/// <summary>
	/// A dump built from an FBX alone, on the skeleton (and poses) of <paramref name="bones"/> -
	/// an <c>arms_pose_dump</c> file is enough. The FBX's own polygons stand in for the render
	/// triangles: the same surface, triangulated as a fan, one part per mesh and material.
	/// </summary>
	public static ModelDump FromFbx( ModelDump bones, FbxScene fbx, out FbxFit fit )
	{
		fit = bones.Fit( fbx );
		var map = fit.Map;
		var dump = new ModelDump
		{
			Model = bones.Model,
			Sequence = bones.Sequence,
			Placement = bones.Placement,
			Skeleton = bones.Skeleton,
			BindWorld = bones.BindWorld,
			Poses = bones.Poses,
			Props = bones.Props,
		};

		foreach ( var mesh in fbx.Meshes )
		{
			var slots = mesh.PolygonMaterial.Distinct().OrderBy( s => s ).ToList();

			foreach ( var slot in slots )
			{
				var material = slot < mesh.Materials.Count ? mesh.Materials[slot] : "";
				var part = new Part
				{
					Name = slots.Count > 1 ? $"{mesh.Name}/{(material.Length > 0 ? material : slot.ToString())}" : mesh.Name,
					Material = material,
				};

				// Only the points this material's polygons use, renumbered.
				var remap = new Dictionary<int, int>();
				var positions = new List<Vec3>();
				var triangles = new List<int>();

				int Use( int point )
				{
					if ( !remap.TryGetValue( point, out var index ) )
					{
						remap[point] = index = positions.Count;
						positions.Add( map.TransformPoint( mesh.Points[point] ) );
					}
					return index;
				}

				for ( var p = 0; p < mesh.Polygons.Count; p++ )
				{
					if ( mesh.PolygonMaterial[p] != slot ) continue;
					var poly = mesh.Polygons[p];
					for ( var k = 1; k + 1 < poly.Length; k++ )
						triangles.AddRange( new[] { Use( poly[0] ), Use( poly[k] ), Use( poly[k + 1] ) } );
				}

				part.Positions = positions.ToArray();
				part.Triangles = triangles.ToArray();

				if ( mesh.Weights is not null )
				{
					part.Weights = new SkinWeights( part.Positions.Length );
					foreach ( var (point, index) in remap )
						part.Weights.Vertices[index] = dump.ToSkeleton( mesh.Weights[point], fbx );
				}

				fit.Filled += FillUnweighted( part );
				dump.Meshes.Add( part );
			}
		}

		return dump;
	}

	/// <summary>
	/// Give a vertex with no weights those of a weighted vertex it shares a triangle with, walking
	/// outwards until one is found. A source mesh can leave a stray vertex unweighted - at a seam,
	/// a cap - and on its own it stays at the bind pose while its neighbours move, which reads as a
	/// spike across the screen. Returns how many were filled.
	/// </summary>
	public static int FillUnweighted( Part part )
	{
		if ( part.Weights is null )
			return 0;

		var bare = Enumerable.Range( 0, part.Positions.Length ).Where( v => part.Weights.Vertices[v].Length == 0 ).ToList();
		if ( bare.Count == 0 )
			return 0;

		var neighbours = new List<int>[part.Positions.Length];
		for ( var t = 0; t + 2 < part.Triangles.Length; t += 3 )
			for ( var k = 0; k < 3; k++ )
			{
				var a = part.Triangles[t + k];
				(neighbours[a] ??= new List<int>()).Add( part.Triangles[t + (k + 1) % 3] );
				(neighbours[a] ??= new List<int>()).Add( part.Triangles[t + (k + 2) % 3] );
			}

		var filled = 0;
		foreach ( var start in bare )
		{
			var seen = new HashSet<int> { start };
			var queue = new Queue<int>();
			queue.Enqueue( start );

			while ( queue.Count > 0 )
			{
				var v = queue.Dequeue();
				if ( part.Weights.Vertices[v].Length > 0 )
				{
					part.Weights.Vertices[start] = part.Weights.Vertices[v];
					filled++;
					break;
				}

				foreach ( var n in neighbours[v] ?? new List<int>() )
					if ( seen.Add( n ) ) queue.Enqueue( n );
			}
		}

		return filled;
	}

	/// <summary>
	/// FBX weights onto this skeleton. A bone the compiled model does not have - model compilers
	/// prune bones that nothing animates - hands its weight to its nearest ancestor that it does,
	/// which is where the compiler put those vertices too. Dropping them instead leaves the
	/// vertices at the bind pose while everything round them moves: long spikes off the mesh.
	/// </summary>
	BoneWeight[] ToSkeleton( List<(string Bone, float Weight)> weights, FbxScene fbx )
	{
		var byBone = new Dictionary<int, float>();
		var total = 0f;

		foreach ( var (bone, weight) in weights )
		{
			if ( weight <= 0f ) continue;

			var name = bone;
			var index = Skeleton.IndexOf( name );
			for ( var guard = 0; index < 0 && guard < 256 && fbx.BoneParent.TryGetValue( name, out var parent ); guard++ )
			{
				name = parent;
				index = Skeleton.IndexOf( name );
			}

			if ( index < 0 ) continue;
			byBone[index] = byBone.GetValueOrDefault( index ) + weight;
			total += weight;
		}

		return total > 0f ? byBone.Select( w => new BoneWeight( w.Key, w.Value / total ) ).ToArray() : Array.Empty<BoneWeight>();
	}

	static IEnumerable<Xform> SignedPermutations()
	{
		var axes = new[] { new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ) };
		var orders = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };

		foreach ( var o in orders )
			for ( var signs = 0; signs < 8; signs++ )
				yield return new Xform(
					axes[o[0]] * ((signs & 1) != 0 ? -1 : 1),
					axes[o[1]] * ((signs & 2) != 0 ? -1 : 1),
					axes[o[2]] * ((signs & 4) != 0 ? -1 : 1),
					Vec3.Zero );
	}

	/// <summary>Nearest-point lookup on a uniform grid.</summary>
	sealed class PointGrid
	{
		readonly Vec3[] _points;
		readonly Dictionary<(int, int, int), List<int>> _cells = new();
		readonly float _cell;

		public PointGrid( Vec3[] points )
		{
			_points = points;
			var lo = points.Aggregate( points[0], ( a, p ) => new Vec3( MathF.Min( a.x, p.x ), MathF.Min( a.y, p.y ), MathF.Min( a.z, p.z ) ) );
			var hi = points.Aggregate( points[0], ( a, p ) => new Vec3( MathF.Max( a.x, p.x ), MathF.Max( a.y, p.y ), MathF.Max( a.z, p.z ) ) );
			var volume = MathF.Max( (hi.x - lo.x) * (hi.y - lo.y) * (hi.z - lo.z), 1e-9f );
			_cell = MathF.Max( MathF.Cbrt( volume / points.Length ) * 2f, 1e-4f );

			for ( var i = 0; i < points.Length; i++ )
			{
				var key = Key( points[i] );
				if ( !_cells.TryGetValue( key, out var list ) ) _cells[key] = list = new List<int>();
				list.Add( i );
			}
		}

		(int, int, int) Key( Vec3 p ) => ((int)MathF.Floor( p.x / _cell ), (int)MathF.Floor( p.y / _cell ), (int)MathF.Floor( p.z / _cell ));

		public (int Index, float Distance) Nearest( Vec3 p )
		{
			var (cx, cy, cz) = Key( p );
			var best = -1;
			var bestD = float.MaxValue;

			// Grow the search shell until a hit is found and the shell is past it.
			for ( var r = 0; r < 4096; r++ )
			{
				for ( var x = cx - r; x <= cx + r; x++ )
					for ( var y = cy - r; y <= cy + r; y++ )
						for ( var z = cz - r; z <= cz + r; z++ )
						{
							if ( Math.Max( Math.Abs( x - cx ), Math.Max( Math.Abs( y - cy ), Math.Abs( z - cz ) ) ) != r )
								continue;

							if ( !_cells.TryGetValue( (x, y, z), out var list ) )
								continue;

							foreach ( var i in list )
							{
								var d = (_points[i] - p).LengthSquared;
								if ( d < bestD ) { bestD = d; best = i; }
							}
						}

				if ( best >= 0 && MathF.Sqrt( bestD ) <= r * _cell )
					break;
			}

			return (best, MathF.Sqrt( bestD ));
		}
	}

	public void WriteFile( string path ) => File.WriteAllText( path, Write() );

	/// <summary>The file format in the class summary. Bone weights are written four per vertex,
	/// the heaviest four, the way an engine would keep them.</summary>
	public string Write()
	{
		using var stream = new MemoryStream();
		using ( var w = new Utf8JsonWriter( stream ) )
		{
			w.WriteStartObject();
			w.WriteString( "Model", Model );
			if ( !string.IsNullOrEmpty( Sequence ) ) w.WriteString( "Sequence", Sequence );
			WriteTransform( w, "Transform", Placement );

			w.WriteStartArray( "Bones" );
			for ( var i = 0; i < Skeleton.Count; i++ )
			{
				var bone = Skeleton.Bones[i];
				w.WriteStartObject();
				w.WriteString( "Name", bone.Name );
				if ( bone.Parent >= 0 ) w.WriteString( "Parent", Skeleton.Bones[bone.Parent].Name ); else w.WriteNull( "Parent" );
				WriteTransform( w, "Bind", BindWorld[i] );
				w.WriteEndObject();
			}
			w.WriteEndArray();

			if ( Poses.Count > 0 )
			{
				w.WriteStartObject( "Poses" );
				foreach ( var (time, pose) in Poses )
				{
					w.WriteStartObject( time );
					for ( var i = 0; i < Skeleton.Count; i++ )
						WriteTransform( w, Skeleton.Bones[i].Name, pose[i] );
					w.WriteEndObject();
				}
				w.WriteEndObject();
			}

			w.WriteStartArray( "Meshes" );
			foreach ( var part in Meshes )
				WritePart( w, part );
			w.WriteEndArray();

			w.WriteStartArray( "Props" );
			foreach ( var prop in Props )
			{
				w.WriteStartObject();
				w.WriteString( "Name", prop.Name );
				w.WriteString( "Model", prop.Model );
				WriteTransform( w, "Transform", Placement * prop.Transform );
				w.WriteStartArray( "Meshes" );
				foreach ( var part in prop.Meshes )
					WritePart( w, part );
				w.WriteEndArray();
				w.WriteEndObject();
			}
			w.WriteEndArray();

			w.WriteEndObject();
		}

		return System.Text.Encoding.UTF8.GetString( stream.ToArray() );
	}

	static void WritePart( Utf8JsonWriter w, Part part )
	{
		w.WriteStartObject();
		w.WriteString( "Name", part.Name );
		w.WriteString( "Material", part.Material );

		w.WriteStartArray( "Positions" );
		foreach ( var p in part.Positions ) { w.WriteNumberValue( p.x ); w.WriteNumberValue( p.y ); w.WriteNumberValue( p.z ); }
		w.WriteEndArray();

		w.WriteStartArray( "Triangles" );
		foreach ( var t in part.Triangles ) w.WriteNumberValue( t );
		w.WriteEndArray();

		if ( part.Weights is not null )
		{
			var heaviest = part.Weights.Vertices.Select( v => v.OrderByDescending( b => b.Weight ).Take( 4 ).ToArray() ).ToList();

			w.WriteStartArray( "BoneIndices" );
			foreach ( var v in heaviest )
				for ( var k = 0; k < 4; k++ ) w.WriteNumberValue( k < v.Length ? v[k].Bone : 0 );
			w.WriteEndArray();

			w.WriteStartArray( "BoneWeights" );
			foreach ( var v in heaviest )
				for ( var k = 0; k < 4; k++ ) w.WriteNumberValue( k < v.Length ? v[k].Weight : 0f );
			w.WriteEndArray();
		}

		w.WriteEndObject();
	}

	static void WriteTransform( Utf8JsonWriter w, string name, Xform t )
	{
		var q = Quat.FromXform( t );
		var s = Quat.ScaleOf( t );
		w.WriteStartArray( name );
		foreach ( var v in new[] { t.Origin.x, t.Origin.y, t.Origin.z, q.x, q.y, q.z, q.w } ) w.WriteNumberValue( v );
		if ( MathF.Abs( s.x - 1 ) > 1e-5f || MathF.Abs( s.y - 1 ) > 1e-5f || MathF.Abs( s.z - 1 ) > 1e-5f )
			foreach ( var v in new[] { s.x, s.y, s.z } ) w.WriteNumberValue( v );
		w.WriteEndArray();
	}

	/// <summary>All of a dump's own meshes as one triangle list, for a whole-model check.</summary>
	public (Vec3[] Positions, int[] Triangles) Merge( IReadOnlyList<Vec3[]> deformed = null )
	{
		var positions = new List<Vec3>();
		var triangles = new List<int>();

		for ( var i = 0; i < Meshes.Count; i++ )
		{
			var offset = positions.Count;
			positions.AddRange( deformed?[i] ?? Meshes[i].Positions );
			triangles.AddRange( Meshes[i].Triangles.Select( t => t + offset ) );
		}

		return (positions.ToArray(), triangles.ToArray());
	}

	static Part ReadPart( JsonElement m, int[] fileToSkeleton )
	{
		var part = new Part
		{
			Name = Str( m, "Name" ) ?? "",
			Material = Str( m, "Material" ) ?? "",
		};

		var flat = Numbers( m, "Positions" );
		part.Positions = new Vec3[flat.Length / 3];
		for ( var i = 0; i < part.Positions.Length; i++ )
			part.Positions[i] = new Vec3( flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2] );

		part.Triangles = Numbers( m, "Triangles" ).Select( v => (int)v ).ToArray();

		var indices = Numbers( m, "BoneIndices" );
		var weights = Numbers( m, "BoneWeights" );

		if ( fileToSkeleton is not null && indices.Length > 0 && indices.Length == weights.Length && part.Positions.Length > 0 )
		{
			var per = indices.Length / part.Positions.Length;
			part.Weights = new SkinWeights( part.Positions.Length );

			for ( var v = 0; v < part.Positions.Length; v++ )
			{
				var list = new List<BoneWeight>();
				var total = 0f;

				for ( var k = 0; k < per; k++ )
				{
					var w = weights[v * per + k];
					var b = (int)indices[v * per + k];

					if ( w <= 0f || b < 0 || b >= fileToSkeleton.Length )
						continue;

					list.Add( new BoneWeight( fileToSkeleton[b], w ) );
					total += w;
				}

				// Engines store weights quantised (bytes summing to 255); renormalise so a vertex
				// fully on one bone moves exactly with it.
				part.Weights.Vertices[v] = total > 0f
					? list.Select( bw => new BoneWeight( bw.Bone, bw.Weight / total ) ).ToArray()
					: Array.Empty<BoneWeight>();
			}
		}

		return part;
	}

	/// <summary>[px,py,pz, qx,qy,qz,qw] with an optional [sx,sy,sz] after.</summary>
	static Xform ReadTransform( JsonElement e )
	{
		var v = e.EnumerateArray().Select( x => x.GetSingle() ).ToArray();

		if ( v.Length < 7 )
			throw new FormatException( $"a transform needs 7 numbers (position, quaternion), got {v.Length}" );

		var scale = v.Length >= 10 ? new Vec3( v[7], v[8], v[9] ) : v.Length == 8 ? new Vec3( v[7], v[7], v[7] ) : Vec3.One;
		return new Quat( v[3], v[4], v[5], v[6] ).Normalized.ToXform( new Vec3( v[0], v[1], v[2] ), scale );
	}

	static float[] Numbers( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var a ) && a.ValueKind == JsonValueKind.Array
			? a.EnumerateArray().Select( x => x.GetSingle() ).ToArray()
			: Array.Empty<float>();

	static string Str( JsonElement e, string name ) =>
		e.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
