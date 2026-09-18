using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette;

/// <summary>
/// A garment worn as real cloth: it hangs off the wearer's shoulders and swings when they move,
/// instead of riding the bones stiffly the way a .clothing model does.
///
/// FED BY EFFIGY. Clothing > Publish > Make live cloth writes a .cloth.json: the garment in the
/// wearer's bind pose, the capsules it hangs on and which bone each capsule and each bit of cloth
/// belongs to. This reads it, puts the capsules on the live bones every frame, and steps the cloth
/// against them.
///
/// POSITION-BASED, the way game cloth is done: particles, distance constraints along every edge
/// and across every fold, collision against round capsules rather than triangles. It is stable at
/// any frame rate, which a spring simulation is not, and it costs a few milliseconds for a
/// T-shirt. It also runs in the editor, so a garment can be watched settling without pressing Play.
///
/// LEASHED. Every particle is held within Leash of where the garment was fitted relative to its
/// bone. Free cloth left behind by a teleport, a respawn or a frame hitch would otherwise be found
/// on the floor; the leash is the long-range attachment every shipped cloth solver has for that.
/// </summary>
[Title( "Garment Cloth" ), Category( "Marionette" ), Icon( "checkroom" )]
public sealed class GarmentCloth : Component, Component.ExecuteInEditor
{
	/// <summary>The character wearing it. Found on this object or its parents when left empty.</summary>
	[Property] public SkinnedModelRenderer Wearer { get; set; }

	/// <summary>The .cloth.json Effigy wrote, e.g. models/effigy/shirt.cloth.json.</summary>
	[Property] public string Cloth { get; set; }

	/// <summary>What it is drawn with. Empty uses the material the garment was given in Effigy.</summary>
	[Property] public Material Material { get; set; }

	/// <summary>1 holds its length like canvas; low values let it stretch like jersey.</summary>
	[Property, Range( 0.05f, 1f )] public float Stiffness { get; set; } = 0.9f;

	/// <summary>How much it resists folding. Low hangs in soft folds, high stays in panels.</summary>
	[Property, Range( 0f, 1f )] public float Bend { get; set; } = 0.15f;

	/// <summary>Inches per second squared. Source's own gravity is 386.</summary>
	[Property] public float Gravity { get; set; } = 386f;

	/// <summary>Velocity lost per step. Higher is heavier, stiller cloth.</summary>
	[Property, Range( 0f, 0.5f )] public float Damping { get; set; } = 0.03f;

	/// <summary>How far any bit of cloth may swing from where it was fitted, in inches.</summary>
	[Property, Range( 0.5f, 24f )] public float Leash { get; set; } = 6f;

	/// <summary>How far off the capsules the cloth sits, so it does not z-fight the body.</summary>
	[Property, Range( 0f, 2f )] public float Skin { get; set; } = 0.25f;

	[Property, Range( 1, 16 )] public int Iterations { get; set; } = 6;

	/// <summary>Draw the capsules the cloth hangs on.</summary>
	[Property] public bool ShowColliders { get; set; }

	sealed class ClothData
	{
		public string Material { get; set; }
		public float[] Positions { get; set; }
		public float[] UVs { get; set; }
		public int[] Triangles { get; set; }
		public string[] VertexBones { get; set; }
		public CapsuleData[] Capsules { get; set; }
	}

	sealed class CapsuleData
	{
		public string Bone { get; set; }
		public float[] A { get; set; }
		public float[] B { get; set; }
		public float Radius { get; set; }
	}

	// The loaded garment, in bone space.
	string _loaded;
	string[] _bones;
	int[] _vertexBone;
	Vector3[] _local;
	Vector2[] _uv;
	int[] _triangles;
	(int Bone, Vector3 A, Vector3 B, float Radius)[] _capsules;
	(int A, int B, float Length, bool Bend)[] _links;

	// The simulation, in world space.
	Vector3[] _x;
	Vector3[] _prev;
	Transform[] _boneWorld;
	(Vector3 A, Vector3 B, float Radius)[] _capsWorld;

	// The drawing.
	GameObject _drawn;
	Mesh _mesh;
	List<SimpleVertex> _vertices;

	protected override void OnEnabled()
	{
		Wearer ??= GetComponentInParent<SkinnedModelRenderer>( true, true )
			?? GetComponentInChildren<SkinnedModelRenderer>( true, true );
	}

	protected override void OnDisabled()
	{
		_drawn?.Destroy();
		_drawn = null;
		_mesh = null;
		_loaded = null;
	}

	protected override void OnUpdate()
	{
		if ( !Wearer.IsValid() || Wearer.Model is null || string.IsNullOrEmpty( Cloth ) )
			return;

		if ( _loaded != Cloth && !Load() )
			return;

		ReadBones();

		// A frame hitch in the editor can be a second long. Stepping a second of cloth at once is
		// how cloth explodes; stepping a thirtieth and letting the leash catch the rest is not.
		var dt = Math.Clamp( Time.Delta, 1f / 240f, 1f / 30f );
		const int substeps = 2;
		var h = dt / substeps;

		for ( var s = 0; s < substeps; s++ )
			Step( h );

		Draw();

		if ( ShowColliders )
			foreach ( var (a, b, r) in _capsWorld )
				Gizmo.Draw.LineCapsule( new Capsule( a, b, r ) );
	}

	bool Load()
	{
		OnDisabled();
		_loaded = Cloth;
		_boneWorld = null;
		_capsWorld = null;

		ClothData data;

		try
		{
			data = Json.Deserialize<ClothData>( FileSystem.Mounted.ReadAllText( Cloth ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[GarmentCloth] could not read {Cloth}: {e.Message}" );
			return false;
		}

		if ( data?.Positions is null || data.Triangles is null || data.Capsules is null || data.Capsules.Length == 0 )
		{
			Log.Warning( $"[GarmentCloth] {Cloth} has no cloth in it - make it again with Make live cloth." );
			return false;
		}

		var model = Wearer.Model;
		var bones = new List<string>();
		var bind = new List<Transform>();

		int BoneIndex( string name )
		{
			var i = bones.IndexOf( name );

			if ( i >= 0 )
				return i;

			// A bone the model does not have (renamed since the cloth was made) still needs a
			// frame to hang in; the model's root is the honest fallback, and says so once.
			if ( string.IsNullOrEmpty( name ) || !model.Bones.HasBone( name ) )
			{
				if ( !string.IsNullOrEmpty( name ) )
					Log.Warning( $"[GarmentCloth] {model.Name} has no bone {name}; that part of the cloth hangs from the root." );

				name = model.Bones.Root?.Name ?? "";
				i = bones.IndexOf( name );

				if ( i >= 0 )
					return i;
			}

			bones.Add( name );
			bind.Add( model.GetBoneTransform( name ) );
			return bones.Count - 1;
		}

		static Vector3 V( float[] a, int i ) => new( a[i], a[i + 1], a[i + 2] );

		var count = data.Positions.Length / 3;
		_vertexBone = new int[count];
		_local = new Vector3[count];
		_uv = new Vector2[count];

		for ( var i = 0; i < count; i++ )
		{
			var bone = BoneIndex( data.VertexBones is not null && i < data.VertexBones.Length ? data.VertexBones[i] : "" );
			_vertexBone[i] = bone;
			_local[i] = bind[bone].PointToLocal( V( data.Positions, i * 3 ) );

			if ( data.UVs is not null && i * 2 + 1 < data.UVs.Length )
				_uv[i] = new Vector2( data.UVs[i * 2], data.UVs[i * 2 + 1] );
		}

		_capsules = data.Capsules.Select( c =>
		{
			var bone = BoneIndex( c.Bone );
			return (bone, bind[bone].PointToLocal( V( c.A, 0 ) ), bind[bone].PointToLocal( V( c.B, 0 ) ), c.Radius);
		} ).ToArray();

		_bones = bones.ToArray();
		_triangles = data.Triangles;
		_links = BuildLinks( data.Positions, _triangles );

		if ( Material is null && !string.IsNullOrEmpty( data.Material ) )
			Material = Material.Load( data.Material );

		// Start the cloth where it was fitted, on the bones as they are now.
		ReadBones();
		_x = new Vector3[count];
		_prev = new Vector3[count];

		for ( var i = 0; i < count; i++ )
			_x[i] = _prev[i] = _boneWorld[_vertexBone[i]].PointToWorld( _local[i] );

		return true;
	}

	/// <summary>Every edge, plus a link across every fold - the two corners opposite a shared
	/// edge. The edges hold the length; the cross links are what stops the cloth folding flat
	/// like paper, and Bend sets how hard they push.</summary>
	static (int, int, float, bool)[] BuildLinks( float[] positions, int[] tris )
	{
		Vector3 P( int i ) => new( positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2] );

		var links = new List<(int, int, float, bool)>();
		var edges = new Dictionary<(int, int), int>();

		void Edge( int a, int b, int opposite )
		{
			var key = a < b ? (a, b) : (b, a);

			if ( edges.TryGetValue( key, out var other ) )
			{
				if ( other >= 0 && other != opposite )
				{
					links.Add( (other, opposite, (P( other ) - P( opposite )).Length, true) );
					edges[key] = -1;
				}

				return;
			}

			edges[key] = opposite;
			links.Add( (a, b, (P( a ) - P( b )).Length, false) );
		}

		for ( var t = 0; t + 2 < tris.Length; t += 3 )
		{
			Edge( tris[t], tris[t + 1], tris[t + 2] );
			Edge( tris[t + 1], tris[t + 2], tris[t] );
			Edge( tris[t + 2], tris[t], tris[t + 1] );
		}

		return links.ToArray();
	}

	void ReadBones()
	{
		_boneWorld ??= new Transform[_bones?.Length ?? 0];

		if ( _bones is null )
			return;

		if ( _boneWorld.Length != _bones.Length )
			_boneWorld = new Transform[_bones.Length];

		for ( var b = 0; b < _bones.Length; b++ )
		{
			// Not animating (the editor, a ragdoll with no graph) still has a pose: the bind pose,
			// placed where the renderer is.
			_boneWorld[b] = Wearer.TryGetBoneTransform( _bones[b], out var tx )
				? tx
				: Wearer.WorldTransform.ToWorld( Wearer.Model.GetBoneTransform( _bones[b] ) );
		}

		_capsWorld ??= new (Vector3, Vector3, float)[_capsules.Length];

		for ( var c = 0; c < _capsules.Length; c++ )
		{
			var (bone, a, b, r) = _capsules[c];
			var world = _boneWorld[bone];
			_capsWorld[c] = (world.PointToWorld( a ), world.PointToWorld( b ), r * world.UniformScale);
		}
	}

	void Step( float h )
	{
		var gravity = Vector3.Down * Gravity * h * h;
		var keep = 1f - Damping;

		for ( var i = 0; i < _x.Length; i++ )
		{
			var velocity = (_x[i] - _prev[i]) * keep;
			_prev[i] = _x[i];
			_x[i] += velocity + gravity;
		}

		var bend = Bend * Bend;

		for ( var it = 0; it < Iterations; it++ )
		{
			foreach ( var (a, b, rest, isBend) in _links )
			{
				var d = _x[a] - _x[b];
				var len = d.Length;

				if ( len < 1e-6f )
					continue;

				var k = isBend ? bend : Stiffness;
				var correction = d * ((len - rest) / len * 0.5f * k);
				_x[a] -= correction;
				_x[b] += correction;
			}
		}

		// Once per step, after the links: the position the step ends on is the one that has to
		// be off the body, and doing it per iteration was most of the frame.
		Collide();
		Tether();
	}

	void Collide()
	{
		for ( var i = 0; i < _x.Length; i++ )
		{
			foreach ( var (a, b, radius) in _capsWorld )
			{
				var ab = b - a;
				var lenSq = ab.LengthSquared;
				var t = lenSq < 1e-8f ? 0f : Math.Clamp( Vector3.Dot( _x[i] - a, ab ) / lenSq, 0f, 1f );
				var core = a + ab * t;
				var d = _x[i] - core;
				var dist = d.Length;
				var want = radius + Skin;

				if ( dist >= want )
					continue;

				var normal = dist > 1e-5f ? d / dist : Vector3.Up;
				var pushed = core + normal * want;

				// Friction: a contact keeps little of the slide it did this step, so a shirt rests
				// on a shoulder instead of sliding off it.
				var moved = pushed - _prev[i];
				var slide = moved - normal * Vector3.Dot( moved, normal );
				_x[i] = pushed - slide * 0.6f;
			}
		}
	}

	void Tether()
	{
		for ( var i = 0; i < _x.Length; i++ )
		{
			var home = _boneWorld[_vertexBone[i]].PointToWorld( _local[i] );
			var d = _x[i] - home;
			var len = d.Length;

			if ( len > Leash )
			{
				_x[i] = home + d * (Leash / len);

				// Take the velocity with it, or the particle snaps straight back out next step.
				_prev[i] = _x[i];
			}
		}
	}

	/// <summary>
	/// Stream the particles into the mesh. Both sides are drawn - a shirt is seen from inside at
	/// the collar and the hem - so every vertex is written twice, the back copy with its normal
	/// turned round and its triangles wound the other way.
	/// </summary>
	void Draw()
	{
		var n = _x.Length;
		var normals = new Vector3[n];

		for ( var t = 0; t + 2 < _triangles.Length; t += 3 )
		{
			var a = _triangles[t];
			var b = _triangles[t + 1];
			var c = _triangles[t + 2];
			var face = Vector3.Cross( _x[b] - _x[a], _x[c] - _x[a] );
			normals[a] += face;
			normals[b] += face;
			normals[c] += face;
		}

		if ( !_drawn.IsValid() )
			Build();

		var space = _drawn.WorldTransform;
		var bounds = BBox.FromPositionAndSize( space.PointToLocal( _x[0] ), 1f );

		for ( var i = 0; i < n; i++ )
		{
			var p = space.PointToLocal( _x[i] );
			var normal = space.Rotation.Inverse * normals[i].Normal;
			var tangent = Vector3.Cross( normal, Vector3.Up ).Normal;

			_vertices[i] = new SimpleVertex( p, normal, tangent, _uv[i] );
			_vertices[i + n] = new SimpleVertex( p, -normal, tangent, _uv[i] );
			bounds = bounds.AddPoint( p );
		}

		_mesh.SetVertexBufferData( _vertices, 0 );
		_mesh.Bounds = bounds;
	}

	void Build()
	{
		var n = _x.Length;
		_vertices = new List<SimpleVertex>( new SimpleVertex[n * 2] );

		var indices = new List<int>( _triangles.Length * 2 );
		indices.AddRange( _triangles );

		for ( var t = 0; t + 2 < _triangles.Length; t += 3 )
		{
			indices.Add( _triangles[t] + n );
			indices.Add( _triangles[t + 2] + n );
			indices.Add( _triangles[t + 1] + n );
		}

		_mesh = new Mesh( Material ?? Material.Load( "materials/default.vmat" ) );
		_mesh.CreateVertexBuffer<SimpleVertex>( _vertices.Count, _vertices );
		_mesh.CreateIndexBuffer( indices.Count, indices );

		// A child that is never saved: the cloth is rebuilt from the file every time, and a
		// scene that serialised the generated model would carry a stale copy of it forever.
		_drawn = new GameObject( GameObject, true, "cloth" );
		_drawn.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.Hidden;

		var renderer = _drawn.Components.Create<ModelRenderer>();
		renderer.Model = Model.Builder.AddMesh( _mesh ).Create();
	}
}
