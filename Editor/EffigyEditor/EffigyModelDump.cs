using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// A compiled model out of the engine and into a file the kernel reads without it - for checking
/// animated takes headlessly (tools/effigy.sh take), where Blender used to be needed.
///
///     effigy_model_dump models/arms.vmdl C:/out/arms_dump.json
///     effigy_model_dump models/arms.vmdl C:/out/arms_dump.json pull_monitor 0,0.5,1.2
///     effigy_model_dump models/arms.vmdl C:/out/arms_dump.json "" "" switch_plate,switch_lever
///
/// Writes the bind skeleton, every LOD-0 draw call's triangles in model space with its material,
/// optionally the model's pose at moments of one of its sequences, and optionally the named
/// GameObjects of the open scene as props - placed relative to the model, if the scene has it.
///
/// SKIN WEIGHTS COME FROM THE SOURCE FBX. The engine's Vertex carries no blend indices or
/// weights, so they are read from the .fbx the .vmdl was compiled from and matched to the render
/// vertices in the kernel (<see cref="ModelDump.AttachWeights"/>). A model built from anything else
/// is dumped without weights, and says so. Offline, `tools/effigy.sh fbxdump` builds the same
/// file from the FBX and any dump that has the bones.
///
/// The format is <see cref="ModelDump"/>'s, and it reads the older arms_pose_dump files too.
/// </summary>
public static class EffigyModelDump
{
	[ConCmd( "effigy_model_dump" )]
	public static void Dump( string modelPath, string output, string sequence = "", string times = "", string props = "" )
	{
		var model = Model.Load( modelPath );

		if ( model is null || model.IsError || model.Bones is null )
		{
			Log.Error( $"[dump] could not load {modelPath}" );
			return;
		}

		var dump = FromModel( model, modelPath );
		dump.Sequence = sequence ?? "";

		if ( !string.IsNullOrWhiteSpace( sequence ) && !string.IsNullOrWhiteSpace( times ) )
			SamplePoses( model, sequence, times, dump );

		var scene = SceneEditorSession.Active?.Scene;

		if ( scene is not null )
		{
			// Where the model stands in the open scene, if it is in it, so props land relative to it.
			var self = scene.GetAllComponents<SkinnedModelRenderer>()
				.FirstOrDefault( r => r.IsValid() && SamePath( r.Model?.ResourcePath, modelPath ) );

			if ( self is not null )
				dump.Placement = ToXform( self.WorldTransform );
		}

		if ( !string.IsNullOrWhiteSpace( props ) )
		{
			if ( scene is null )
				Log.Warning( "[dump] props were asked for but no scene is open; dumping without them" );
			else
				AddProps( scene, props, dump );
		}

		// Weights from the model's source FBX.
		var fbx = SourceFbx( modelPath );

		if ( fbx is null )
			Log.Warning( $"[dump] {modelPath} is not built from an .fbx this can find, so the dump has no skin weights" );
		else if ( dump.Meshes.Count > 0 )
		{
			try
			{
				var fit = dump.AttachWeights( FbxScene.ReadFile( fbx ) );
				Log.Info( $"[dump] weights from {fbx}: {fit}" );

				if ( fit.BoneError > 0.05f || fit.WorstVertex > 0.05f )
					Log.Warning( "[dump] the FBX does not line up with the compiled model closely - check it is the model's real source" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[dump] could not take weights from {fbx}: {e.Message}" );
			}
		}

		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( output ) ) ?? "." );
		dump.WriteFile( output );

		Log.Info( $"[dump] {modelPath}: {dump.Skeleton.Count} bones, {dump.Meshes.Count} draw call(s), "
			+ $"{dump.Meshes.Sum( m => m.TriangleCount )} triangles, {dump.Poses.Count} pose(s), {dump.Props.Count} prop(s) -> {output}" );
	}

	/// <summary>
	/// A loaded model's bind skeleton and LOD-0 meshes, in model space - everything of a dump that
	/// comes from the compiled model itself. No weights (see <see cref="SourceFbx"/> and
	/// <see cref="ModelDump.AttachWeights"/>), no poses, no props, no placement: the console command
	/// adds those, and so does Marionette's Take Check, each from its own place.
	/// </summary>
	public static ModelDump FromModel( Model model, string label = null )
	{
		label ??= model?.ResourcePath ?? "model";
		var dump = new ModelDump { Model = label };

		if ( model?.Bones is null )
			return dump;

		// Bones in the engine's order, binds in model space (the same reading arms_pose_dump uses).
		var index = new Dictionary<string, int>();
		var bind = new List<Xform>();

		foreach ( var bone in model.Bones.AllBones )
		{
			var world = ToXform( bone.LocalTransform );
			var parent = bone.Parent is { } p && index.TryGetValue( p.Name, out var pi ) ? pi : -1;
			var local = parent >= 0 ? bind[parent].Inverse * world : world;
			index[bone.Name] = dump.Skeleton.AddBone( bone.Name, parent, local );
			bind.Add( world );
		}

		dump.BindWorld = bind.ToArray();
		dump.Meshes.AddRange( ReadMeshes( model, label ) );
		return dump;
	}

	/// <summary>A model as a dump prop: its LOD-0 meshes, placed by <paramref name="toModel"/>
	/// (the prop's transform in the dumped model's space).</summary>
	public static ModelDump.Prop PropFromModel( string name, Model model, Xform toModel )
	{
		var prop = new ModelDump.Prop { Name = name, Model = model?.ResourcePath ?? "", Transform = toModel };

		if ( model is not null )
			prop.Meshes.AddRange( ReadMeshes( model, name ) );

		return prop;
	}

	/// <summary>A kernel mesh (an OBJ lump, say) as a dump part: every face triangulated, nothing
	/// renumbered. Null for a mesh with no faces.</summary>
	public static ModelDump.Part PartFromMesh( string name, PolyMesh mesh )
	{
		if ( mesh is null || mesh.FaceCount == 0 )
			return null;

		var triangles = new List<int>();

		foreach ( var face in mesh.Faces )
		{
			var ix = face.Indices;

			if ( ix.Length == 3 )
			{
				triangles.AddRange( ix );
				continue;
			}

			if ( ix.Length < 3 )
				continue;

			foreach ( var (a, b, c) in Effigy.Triangulate.Face( ix.Select( i => mesh.Positions[i] ).ToList() ) )
			{
				triangles.Add( ix[a] );
				triangles.Add( ix[b] );
				triangles.Add( ix[c] );
			}
		}

		return new ModelDump.Part { Name = name, Positions = mesh.Positions.ToArray(), Triangles = triangles.ToArray() };
	}

	/// <summary>
	/// Each LOD-0 draw call as its own part: its triangles, renumbered onto the vertices it uses,
	/// with its material. Indices are checked against the vertex buffer rather than trusted, since
	/// GetVertices is marked experimental by the engine.
	/// </summary>
	static IEnumerable<ModelDump.Part> ReadMeshes( Model model, string label )
	{
		var vertices = model.GetVertices();
		var indices = model.GetIndices();

		if ( vertices is null || indices is null || vertices.Length == 0 )
		{
			Log.Warning( $"[dump] {label} gave back no vertices" );
			yield break;
		}

		var meshes = model.MeshInfo?.Meshes ?? Array.Empty<Model.ModelMeshInfo.MeshData>();
		var number = 0;

		foreach ( var mesh in meshes )
		{
			// LOD 0 only: a mesh that is not drawn at the top LOD is a lower-detail copy.
			if ( mesh.LodMask != 0 && (mesh.LodMask & 1) == 0 )
				continue;

			foreach ( var call in mesh.DrawCalls ?? Array.Empty<Model.ModelMeshInfo.DrawCallData>() )
			{
				var remap = new Dictionary<int, int>();
				var positions = new List<Vec3>();
				var triangles = new List<int>();
				var bad = 0;

				for ( var i = 0; i + 2 < call.Indices; i += 3 )
				{
					var corner = new int[3];
					var ok = true;

					for ( var k = 0; k < 3; k++ )
					{
						var at = call.StartIndex + i + k;
						var v = at < indices.Length ? (int)indices[at] + call.BaseVertex : -1;
						if ( v < 0 || v >= vertices.Length ) { ok = false; break; }

						if ( !remap.TryGetValue( v, out var local ) )
						{
							remap[v] = local = positions.Count;
							var p = vertices[v].Position;
							positions.Add( new Vec3( p.x, p.y, p.z ) );
						}

						corner[k] = local;
					}

					if ( ok ) triangles.AddRange( corner ); else bad++;
				}

				if ( bad > 0 )
					Log.Warning( $"[dump] {label}: {bad} triangle(s) of draw call {number} pointed outside the vertex buffer and were skipped" );

				var material = call.Material?.ResourcePath ?? "";

				yield return new ModelDump.Part
				{
					Name = string.IsNullOrEmpty( mesh.Name ) ? $"draw{number}" : $"{mesh.Name}/{Path.GetFileNameWithoutExtension( material )}",
					Material = material,
					Positions = positions.ToArray(),
					Triangles = triangles.ToArray(),
				};

				number++;
			}
		}
	}

	/// <summary>The model's pose at moments of a sequence, in model space - arms_pose_dump's method.</summary>
	static void SamplePoses( Model model, string sequence, string times, ModelDump dump )
	{
		var scene = Scene.CreateEditorScene();

		try
		{
			using var scope = scene.Push();
			var renderer = new GameObject( true, "dump" ).GetOrAddComponent<SkinnedModelRenderer>( false );
			renderer.Model = model;
			renderer.UseAnimGraph = false;
			renderer.PlaybackRate = 0f;
			renderer.Enabled = true;
			var tick = 0;

			foreach ( var text in times.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
			{
				renderer.Sequence.Name = sequence;
				renderer.Sequence.Looping = false;
				renderer.Sequence.Time = text.ToFloat();

				// Bone reads land a tick after they are set.
				for ( var i = 0; i < 4; i++ )
					scene.EditorTick( ++tick / 60f, 0f );

				var pose = dump.BindWorld.ToArray();
				foreach ( var bone in model.Bones.AllBones )
					if ( dump.Skeleton.IndexOf( bone.Name ) is var b and >= 0 && renderer.TryGetBoneTransform( bone, out var world ) )
						pose[b] = ToXform( world );

				dump.Poses[text.Trim()] = pose;
			}
		}
		finally
		{
			scene.Destroy();
		}
	}

	static void AddProps( Scene scene, string names, ModelDump dump )
	{
		var toModel = dump.Placement.Inverse;

		foreach ( var name in names.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var renderers = scene.GetAllComponents<ModelRenderer>()
				.Where( r => r.IsValid() && r.Model is not null && r.GameObject.Name == name )
				.ToList();

			if ( renderers.Count == 0 )
			{
				Log.Warning( $"[dump] no object called '{name}' with a model in the open scene" );
				continue;
			}

			foreach ( var r in renderers )
				dump.Props.Add( PropFromModel( name, r.Model, toModel * ToXform( r.WorldTransform ) ) );
		}
	}

	/// <summary>The .fbx a .vmdl's render mesh was compiled from, on disk, or null.</summary>
	public static string SourceFbx( string modelPath )
	{
		var vmdl = AssetSystem.FindByPath( modelPath )?.AbsolutePath;

		if ( vmdl is null || !File.Exists( vmdl ) )
			return null;

		var text = File.ReadAllText( vmdl );
		var block = text.IndexOf( "RenderMeshFile", StringComparison.Ordinal );
		var match = Regex.Match( block >= 0 ? text[block..] : text, "filename\\s*=\\s*\"([^\"]+\\.fbx)\"", RegexOptions.IgnoreCase );

		if ( !match.Success )
			return null;

		var relative = match.Groups[1].Value;
		var found = AssetSystem.FindByPath( relative )?.AbsolutePath;

		if ( found is not null && File.Exists( found ) )
			return found;

		// Beside the .vmdl's own content folder: models/x.vmdl -> <root>/models/x.fbx.
		var root = vmdl[..^modelPath.Length].TrimEnd( '/', '\\' );
		var guess = Path.Combine( root, relative );
		return File.Exists( guess ) ? guess : null;
	}

	static bool SamePath( string a, string b ) =>
		string.Equals( (a ?? "").Replace( '\\', '/' ).Trim(), (b ?? "").Replace( '\\', '/' ).Trim(), StringComparison.OrdinalIgnoreCase );

	public static Xform ToXform( Transform t ) =>
		new Quat( t.Rotation.x, t.Rotation.y, t.Rotation.z, t.Rotation.w ).ToXform(
			new Vec3( t.Position.x, t.Position.y, t.Position.z ),
			new Vec3( t.Scale.x, t.Scale.y, t.Scale.z ) );
}
