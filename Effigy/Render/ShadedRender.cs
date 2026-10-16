using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Effigy.Render;

/// <summary>How a surface takes light. Values are 0..1 unless noted.</summary>
public sealed class ShadeMaterial
{
	/// <summary>Colour when the mesh has no vertex colours (or <see cref="UseVertexColours"/> is off). Linear RGB.</summary>
	public Vec3 Albedo = new( 0.62f, 0.6f, 0.57f );
	public bool UseVertexColours = true;
	/// <summary>Microfacet roughness of the base layer: 0 is a mirror, 1 is chalk.</summary>
	public float Roughness = 0.6f;
	/// <summary>Reflectance at normal incidence for non-metals (0.04 is plastic, skin, candy).</summary>
	public float Specular = 0.04f;
	public float Metallic;
	/// <summary>A clear lacquer layer over the base: candy shell, car paint, wet skin.</summary>
	public float Coat;
	public float CoatRoughness = 0.04f;
	/// <summary>Light wrapping round the terminator and glowing through thin parts: wax, candy, skin.</summary>
	public float Subsurface;
	/// <summary>The colour light picks up inside the material; null uses the albedo, saturated.</summary>
	public Vec3? SubsurfaceTint;
	/// <summary>How far light travels inside before it is used up, in model units.</summary>
	public float ScatterDistance = 0.5f;

	/// <summary>Baked maps on the mesh's UVs, RGBA, <see cref="MapSize"/> square, row 0 at v = 0, colours
	/// sRGB. Each one, when set, replaces the matching constant per pixel. The emission map is added as light.</summary>
	public byte[] AlbedoMap, RoughnessMap, EmissionMap;
	public int MapSize;
	/// <summary>More baked maps, also <see cref="MapSize"/> square on the same UVs. Metalness (red) replaces
	/// <see cref="Metallic"/>, ambient occlusion (red) darkens the ambient and reflections, and the normal
	/// map is tangent space, OpenGL (green up, as s&amp;box's own normal maps are).</summary>
	public byte[] MetallicMap, NormalMap, AoMap;
	/// <summary>Multiplies the albedo map (a vmat's colour tint).</summary>
	public Vec3 MapTint = Vec3.One;
	/// <summary>Repeats of every map across 0..1 UV: (8, 8) tiles a leather grain eight times each way.
	/// A vmat's g_vTexCoordScale.</summary>
	public Vec2 MapScale = new( 1f, 1f );
	/// <summary>Added to the UVs after <see cref="MapScale"/>, in tiles: a vmat's g_vTexCoordOffset.</summary>
	public Vec2 MapOffset;
	/// <summary>On, v clamps at 0..1 instead of repeating, so an atlas's top and bottom rows never bleed into
	/// each other. Off (the default, as s&amp;box's samplers are), UVs past 0..1 repeat the map: a garment's
	/// foot-per-unit UVs, a strip longer than one tile.</summary>
	public bool ClampMaps;
	/// <summary>Where the maps come from: UVs, a triplanar projection, or each where it fits.</summary>
	public ShadeMapping Mapping = ShadeMapping.Auto;
	/// <summary>Model units per tile of the triplanar projection; 0 turns it off for <see cref="ShadeMapping.Auto"/>.
	/// For parts with no UVs: a weave, a twill or a grain needs no unwrap this way.</summary>
	public float TriplanarScale;
	/// <summary>Scales the normal map's tilt: 0 is flat, 1 as painted, 2 twice as deep.</summary>
	public float NormalStrength = 1f;
	/// <summary>Off (the default, as in Unreal's clear coat): the coat reflects off the smooth surface while the
	/// base takes the normal map, which is what makes lacquered carbon and polished leather read as glossy.
	/// On: the coat follows the map too (wet, bumpy).</summary>
	public bool CoatFollowsNormalMap;

	/// <summary>Per vertex of the mesh, replacing <see cref="Roughness"/> (a per-vertex material bake).</summary>
	public float[] VertexRoughness;
	/// <summary>Per vertex of the mesh, sRGB 0..1, added as light.</summary>
	public Vec3[] VertexEmission;
	/// <summary>Glow everywhere, linear.</summary>
	public Vec3 Emission;
	/// <summary>Oil-film hue shift on grazing reflections, 0..1 (blue → purple → green → gold with angle).</summary>
	public float Iridescence;

	public static ShadeMaterial Clay() => new() { UseVertexColours = false };
	public static ShadeMaterial HardCandy() => new() { Roughness = 0.18f, Coat = 1f, CoatRoughness = 0.03f, Subsurface = 0.55f, ScatterDistance = 0.35f };
}

/// <summary>How linear light becomes display values.</summary>
public enum ShadeTonemap
{
	/// <summary>Narkowicz's ACES fit at 0.8 exposure: the renderer's original curve.</summary>
	Legacy,
	/// <summary>Narkowicz's ACES fit at full exposure: contrasty, saturated highlights.</summary>
	Aces,
	/// <summary>AgX (Troy Sobotka's, in Wrensch's fit): bright colours desaturate to white the way film does,
	/// so a cyan emitter's core goes white and its glow stays cyan. The Blender 4 default.</summary>
	AgX,
	/// <summary>AgX with the "punchy" look: more contrast and saturation.</summary>
	AgXPunchy,
}

/// <summary>A directional light: where it comes from, how bright (linear, 1 is a sunny key), and how big
/// its source looks, which softens shadows and widens highlights.</summary>
public sealed class ShadeLight
{
	public Vec3 Direction;
	public Vec3 Colour = new( 1, 1, 1 );
	public float Intensity = 1f;
	/// <summary>Angular size of the source, in radians: 0 is a hard point, 0.3 a big softbox.</summary>
	public float Size = 0.15f;
	public bool Shadows = true;

	public ShadeLight( Vec3 towardLight, float intensity, float size = 0.15f, Vec3? colour = null )
	{
		Direction = towardLight.Normal;
		Intensity = intensity;
		Size = size;
		if ( colour is Vec3 c ) Colour = c;
	}
}

public sealed class ShadeCamera
{
	public Vec3 Eye, Target, Up = new( 0, 0, 1 );
	/// <summary>Vertical field of view in degrees.</summary>
	public float Fov = 30f;

	public ShadeCamera( Vec3 eye, Vec3 target, float fov = 30f ) { Eye = eye; Target = target; Fov = fov; }
}

/// <summary>Everything in the picture besides the camera.</summary>
public sealed class ShadeScene
{
	public readonly List<(PolyMesh Mesh, ShadeMaterial Material)> Items = new();
	public readonly List<ShadeLight> Lights = new();

	/// <summary>Backdrop gradient, linear RGB, bottom to top of the frame.</summary>
	public Vec3 BackgroundBottom = new( 0.62f, 0.55f, 0.66f ), BackgroundTop = new( 0.38f, 0.47f, 0.66f );
	/// <summary>Ambient light from above and from below (bounce), linear RGB.</summary>
	public Vec3 SkyColour = new( 0.42f, 0.46f, 0.55f ), GroundColour = new( 0.22f, 0.2f, 0.22f );

	public bool Floor = true;
	public float FloorHeight;
	public Vec3 FloorColour = new( 0.42f, 0.4f, 0.44f );
	public float FloorRoughness = 0.45f;

	/// <summary>Screen-space ambient occlusion: reach in model units, and how strong (0 turns it off).</summary>
	public float AoRadius = 0.6f, AoStrength = 1f;
	/// <summary>Occlusion samples per pixel (14 is the old look; 24-32 is cleaner).</summary>
	public int AoSamples = 14;
	/// <summary>Smooth the occlusion with a depth- and normal-aware blur, so more of it can be used without grain.</summary>
	public bool AoDenoise;
	public float Exposure = 1f;

	/// <summary>Image-based lighting: reflections and soft ambient from a procedural studio. Null keeps the
	/// old sky-gradient reflections and two-colour ambient. See <see cref="ShadeEnvironment.Studio"/>.</summary>
	public ShadeEnvironment Environment;
	/// <summary>Short screen-space shadows marched toward each shadowing light, for the creases and contacts a
	/// shadow map is too coarse to see: model units of reach, 0 off, negative for half of <see cref="AoRadius"/>.</summary>
	public float ContactShadows;
	/// <summary>Glow round anything bright: 0 off, ~0.5 a soft halo, 1 strong.</summary>
	public float Bloom;
	/// <summary>Linear brightness (after exposure) above which a pixel blooms. Emission blooms whatever its
	/// brightness, scaled by <see cref="EmissiveBloom"/>.</summary>
	public float BloomThreshold = 1.2f;
	/// <summary>How wide the glow spreads, 0..1 (0 tight, 1 most of the frame).</summary>
	public float BloomRadius = 0.55f;
	public float EmissiveBloom = 1f;
	public ShadeTonemap Tonemap = ShadeTonemap.Legacy;
	/// <summary>Darkens the frame's corners, 0..1.</summary>
	public float Vignette;
	/// <summary>The floor mirrors what stands on it (screen-space), 0..1, blurred by <see cref="FloorReflectionBlur"/>.</summary>
	public float FloorReflection;
	public float FloorReflectionBlur = 0.12f;
	/// <summary>Downsample the supersampled frame with a tent filter rather than a box: thin parts stop stair-stepping.</summary>
	public bool SoftFilter;
	/// <summary>
	/// Antialiasing: visibility samples per output pixel (rounded to 1, 4, 9 … 64), multi-jittered and
	/// resolved with a Blackman-Harris filter, while the shading still runs once per shading pixel. 1 turns it
	/// off (the supersampled frame is filtered down as before). See <see cref="CoverageResolve"/>.
	/// </summary>
	public int Coverage = 16;
	/// <summary>Widen the gloss where the surface curves fast within a pixel (Tokuyoshi and Kaplanyan's
	/// geometric specular antialiasing), so a highlight on a small round part does not sparkle.</summary>
	public bool SpecularAa = true;

	/// <summary>
	/// Everything that makes a render read as a finished piece, on at once: the studio environment, bent-normal
	/// AO with more samples and denoising, contact shadows, bloom, AgX, a vignette, a faint floor reflection and
	/// the tent filter. Lights, materials, colours and exposure are left as they are. Returns the scene.
	/// </summary>
	public ShadeScene Portfolio()
	{
		Environment ??= ShadeEnvironment.Studio();
		AoSamples = Math.Max( AoSamples, 24 ); AoDenoise = true;
		if ( ContactShadows == 0f ) ContactShadows = -1f;
		if ( Bloom == 0f ) Bloom = 0.6f;
		Tonemap = ShadeTonemap.AgXPunchy;
		if ( Vignette == 0f ) Vignette = 0.3f;
		if ( FloorReflection == 0f ) FloorReflection = 0.15f;
		SoftFilter = true;
		return this;
	}

	/// <summary>Per mesh in <see cref="Items"/>: a material for each <see cref="Face.Material"/> id, overriding the
	/// item's own material (which stays the fallback for ids not listed).</summary>
	public readonly Dictionary<PolyMesh, IReadOnlyDictionary<int, ShadeMaterial>> FaceMaterials = new();

	public ShadeScene Add( PolyMesh mesh, ShadeMaterial material = null )
	{
		Items.Add( (mesh, material ?? new ShadeMaterial()) );
		return this;
	}

	/// <summary>
	/// One mesh wearing several materials, picked per face by <see cref="Face.Material"/>. Prefer this to
	/// splitting the mesh by material: split parts get their normals each on their own, so every material
	/// boundary shows as a lit seam; here the normals are the whole mesh's, smooth across the boundary.
	/// </summary>
	public ShadeScene Add( PolyMesh mesh, IReadOnlyDictionary<int, ShadeMaterial> byFaceMaterial, ShadeMaterial fallback = null )
	{
		Items.Add( (mesh, fallback ?? new ShadeMaterial()) );
		if ( byFaceMaterial is not null ) FaceMaterials[mesh] = byFaceMaterial;
		return this;
	}

	/// <summary>A three-light studio: a big soft key front-right-above, a cool fill, a bright rim from behind.</summary>
	public ShadeScene StudioLights( Vec3 cameraForward )
	{
		var f = cameraForward.Normal;
		var right = Vec3.Cross( f, new Vec3( 0, 0, 1 ) ).Normal;
		Lights.Add( new ShadeLight( -f * 0.65f + right * 0.55f + new Vec3( 0, 0, 0.75f ), 2.6f, 0.22f ) );
		Lights.Add( new ShadeLight( -f * 0.6f - right * 0.8f + new Vec3( 0, 0, 0.25f ), 0.7f, 0.4f, new Vec3( 0.85f, 0.92f, 1.05f ) ) { Shadows = false } );
		Lights.Add( new ShadeLight( f * 0.8f - right * 0.2f + new Vec3( 0, 0, 0.6f ), 2.2f, 0.15f ) );
		return this;
	}
}

/// <summary>
/// A shaded, lit render of meshes: perspective, per-pixel gloss and clear coat, soft shadows,
/// ambient occlusion, a cheap subsurface glow, a studio floor and backdrop, all on the CPU into a
/// <see cref="Raster"/>.
///
/// NOT THE MEASURING VIEW. <see cref="ModelRender"/> is orthographic and flat on purpose so a render
/// can be held to a ruler; this one is for judging how a thing LOOKS: material, form under light,
/// a turntable or an animation frame. It is a rasteriser, not a path tracer: shadows come from shadow
/// maps, occlusion from the depth buffer, translucency from the light's depth map, reflections from the
/// sky gradient. Each is the standard real-time trick, so a picture here is close to what an engine
/// would show, which is the point.
/// </summary>
public static class ShadedRender
{
	/// <summary>
	/// A labelled turnaround: <paramref name="views"/> renders evenly round the scene (0° is the front,
	/// looking down −x like ModelRender's front view), each <paramref name="tile"/> pixels square,
	/// <paramref name="elevation"/> degrees up, side by side. The floor sits under the lowest point. A
	/// scene with no lights gets the studio three-light set per view, so the key always comes from the
	/// camera's front-right.
	/// </summary>
	public static Raster Sheet( ShadeScene scene, int views, int tile, float elevation = 12f, int supersample = 1, string label = null )
	{
		views = Math.Clamp( views, 1, 12 );
		var bmin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var bmax = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var (mesh, _) in scene.Items )
			foreach ( var p in mesh.Positions )
			{
				bmin = new Vec3( MathF.Min( bmin.x, p.x ), MathF.Min( bmin.y, p.y ), MathF.Min( bmin.z, p.z ) );
				bmax = new Vec3( MathF.Max( bmax.x, p.x ), MathF.Max( bmax.y, p.y ), MathF.Max( bmax.z, p.z ) );
			}
		if ( bmin.x > bmax.x ) { bmin = new Vec3( -1, -1, 0 ); bmax = new Vec3( 1, 1, 1 ); }

		var centre = (bmin + bmax) * 0.5f;
		var radius = MathF.Max( 1e-3f, (bmax - bmin).Length * 0.5f );
		const float fov = 30f;
		var dist = radius / MathF.Sin( fov * MathF.PI / 360f ) * 1.08f;
		scene.FloorHeight = bmin.z;
		scene.AoRadius = radius * 0.08f;
		var studio = scene.Lights.Count == 0;

		var sheet = new Raster( tile * views, tile, 0 );
		for ( var v = 0; v < views; v++ )
		{
			var yaw = v * 360f / views;
			var y = yaw * MathF.PI / 180f; var e = elevation * MathF.PI / 180f;
			var dir = new Vec3( MathF.Cos( y ) * MathF.Cos( e ), MathF.Sin( y ) * MathF.Cos( e ), MathF.Sin( e ) );
			var camera = new ShadeCamera( centre + dir * dist, centre, fov );
			if ( studio ) { scene.Lights.Clear(); scene.StudioLights( -dir ); }

			var r = Render( scene, camera, tile, tile, supersample );
			for ( var row = 0; row < tile; row++ )
				Array.Copy( r.Pixels, row * tile, sheet.Pixels, row * tile * views + v * tile, tile );

			var name = yaw == 0f ? "front" : yaw == 180f ? "back" : $"turn {yaw:0}";
			var text = string.IsNullOrEmpty( label ) ? name : $"{label} {name}";
			sheet.Rect( v * tile, 0, Raster.TextWidth( text ) + 12, 22, Raster.Rgb( 0, 0, 0 ), 0.55f );
			sheet.Text( v * tile + 6, 4, text, Raster.Rgb( 255, 255, 255 ) );
		}
		if ( studio ) scene.Lights.Clear();
		return sheet;
	}

	public static Raster Render( ShadeScene scene, ShadeCamera camera, int width, int height, int supersample = 1 )
	{
		var ss = Math.Clamp( supersample, 1, 4 );
		var W = width * ss;
		var H = height * ss;

		// --- one flat list of vertices and triangles over every mesh ---------------------------
		var pos = new List<Vec3>(); var nrm = new List<Vec3>(); var col = new List<Vec3>(); var mat = new List<int>();
		var vrough = new List<float>(); var vemit = new List<Vec3>();
		var tris = new List<(int A, int B, int C)>();
		var triItem = new List<int>();
		var triUv = new List<(Vec2 A, Vec2 B, Vec2 C)>();
		var triTan = new List<(Vec3 T, Vec3 B)>();
		var materialList = new List<ShadeMaterial>();
		var materialIndex = new Dictionary<ShadeMaterial, int>( ReferenceEqualityComparer.Instance );
		int MaterialId( ShadeMaterial sm )
		{
			if ( !materialIndex.TryGetValue( sm, out var id ) ) { id = materialList.Count; materialList.Add( sm ); materialIndex[sm] = id; }
			return id;
		}
		for ( var item = 0; item < scene.Items.Count; item++ )
		{
			var (mesh, itemMaterial) = scene.Items[item];
			scene.FaceMaterials.TryGetValue( mesh, out var byFace );
			// Normals over the whole mesh, so a material boundary is not a shading seam.
			var normals = VertexNormals( mesh.Positions, mesh.Faces );
			// One flat vertex per (mesh vertex, material): attributes that come from the material differ per side.
			var flat = new Dictionary<(int, int), int>();
			int Vertex( int i, int mi, ShadeMaterial material )
			{
				if ( flat.TryGetValue( (i, mi), out var at ) ) return at;
				at = pos.Count; flat[(i, mi)] = at;
				pos.Add( mesh.Positions[i] );
				nrm.Add( normals[i] );
				var c = material.UseVertexColours && mesh.VertexColors is { } vc && i < vc.Length
					? new Vec3( SrgbToLinear( vc[i].x ), SrgbToLinear( vc[i].y ), SrgbToLinear( vc[i].z ) )
					: material.Albedo;
				col.Add( c );
				mat.Add( mi );
				vrough.Add( material.VertexRoughness is { } vr && i < vr.Length ? vr[i] : material.Roughness );
				vemit.Add( material.VertexEmission is { } ve && i < ve.Length
					? new Vec3( SrgbToLinear( ve[i].x ), SrgbToLinear( ve[i].y ), SrgbToLinear( ve[i].z ) ) + material.Emission
					: material.Emission );
				return at;
			}
			foreach ( var f in mesh.Faces )
			{
				var material = byFace is not null && byFace.TryGetValue( f.Material, out var fm ) && fm is not null ? fm : itemMaterial;
				var mi = MaterialId( material );
				for ( var k = 1; k + 1 < f.Indices.Length; k++ )
				{
					tris.Add( (Vertex( f.Indices[0], mi, material ), Vertex( f.Indices[k], mi, material ), Vertex( f.Indices[k + 1], mi, material )) );
					triItem.Add( item );
					var tuv = f.UVs is { } uv && uv.Length == f.Indices.Length ? (uv[0], uv[k], uv[k + 1]) : default;
					triUv.Add( tuv );
					triTan.Add( material.NormalMap is null ? default
						: Tangents( mesh.Positions[f.Indices[0]], mesh.Positions[f.Indices[k]], mesh.Positions[f.Indices[k + 1]], tuv.Item1, tuv.Item2, tuv.Item3 ) );
				}
			}
		}
		var materials = materialList.ToArray();

		// --- camera ---------------------------------------------------------------------------
		var fwd = (camera.Target - camera.Eye).Normal;
		var right = Vec3.Cross( fwd, camera.Up ).Normal;
		var up = Vec3.Cross( right, fwd ).Normal;
		var focal = H * 0.5f / MathF.Tan( camera.Fov * MathF.PI / 360f );
		const float near = 0.05f;

		var sx = new float[pos.Count]; var sy = new float[pos.Count]; var sz = new float[pos.Count];
		Parallel.For( 0, pos.Count, i =>
		{
			var d = pos[i] - camera.Eye;
			var z = Vec3.Dot( d, fwd );
			sz[i] = z;
			var iz = 1f / MathF.Max( z, near );
			sx[i] = W * 0.5f + Vec3.Dot( d, right ) * focal * iz;
			sy[i] = H * 0.5f - Vec3.Dot( d, up ) * focal * iz;
		} );

		// Per triangle, the mip level its maps want: texels per pixel from its UV area over its screen area.
		var triUvOk = new bool[tris.Count];
		var triLod = new float[tris.Count];
		Parallel.For( 0, tris.Count, t =>
		{
			var (a, b, c) = tris[t];
			var (ua, ub, uc) = triUv[t];
			var uvArea = MathF.Abs( (ub.x - ua.x) * (uc.y - ua.y) - (ub.y - ua.y) * (uc.x - ua.x) ) * 0.5f;
			triUvOk[t] = uvArea > 1e-12f;
			var m = materials[mat[a]];
			if ( m.MapSize <= 0 || !triUvOk[t] ) return;
			var scrArea = MathF.Abs( (sx[b] - sx[a]) * (sy[c] - sy[a]) - (sy[b] - sy[a]) * (sx[c] - sx[a]) ) * 0.5f;
			var texels = uvArea * m.MapSize * m.MapSize * MathF.Abs( m.MapScale.x * m.MapScale.y );
			triLod[t] = MathF.Max( 0f, 0.5f * MathF.Log2( MathF.Max( texels, 1e-12f ) / MathF.Max( scrArea, 1e-6f ) ) );
		} );

		// --- G-buffer: per pixel, the nearest triangle and where on it ------------------------
		var depth = new float[W * H];
		var triId = new int[W * H];
		var bary = new Vec3[W * H];
		Array.Fill( depth, float.MaxValue );
		Array.Fill( triId, -1 );
		Rasterize( tris, sx, sy, sz, W, H, near, depth, triId, bary, perspective: true );

		// --- shadow maps, one per shadowing light ---------------------------------------------
		var bmin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var bmax = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in pos )
		{
			bmin = new Vec3( MathF.Min( bmin.x, p.x ), MathF.Min( bmin.y, p.y ), MathF.Min( bmin.z, p.z ) );
			bmax = new Vec3( MathF.Max( bmax.x, p.x ), MathF.Max( bmax.y, p.y ), MathF.Max( bmax.z, p.z ) );
		}
		if ( pos.Count == 0 ) { bmin = new Vec3( -1, -1, 0 ); bmax = new Vec3( 1, 1, 1 ); }
		var sceneCentre = (bmin + bmax) * 0.5f;
		var sceneRadius = MathF.Max( (bmax - bmin).Length * 0.5f, 1e-3f );
		var shadows = scene.Lights.Select( l => l.Shadows && tris.Count > 0 ? ShadowMap.Build( l, tris, pos, sceneCentre, sceneRadius * 1.6f, 2048 ) : null ).ToArray();

		// --- the floor plane, where no mesh covers a pixel or the floor is nearer --------------
		Vec3 RayDir( float px, float py ) => (fwd * focal + right * (px - W * 0.5f) - up * (py - H * 0.5f)).Normal;

		// --- world position and normal per covered pixel --------------------------------------
		var wpos = new Vec3[W * H];
		var wnrm = new Vec3[W * H];
		var kind = new byte[W * H]; // 0 background, 1 mesh, 2 floor
		Parallel.For( 0, H, y =>
		{
			for ( var x = 0; x < W; x++ )
			{
				var i = y * W + x;
				var dir = RayDir( x + 0.5f, y + 0.5f );
				var floorT = float.MaxValue;
				if ( scene.Floor && dir.z < -1e-4f )
				{
					var t = (scene.FloorHeight - camera.Eye.z) / dir.z;
					if ( t > 0 ) floorT = t;
				}
				var t0 = triId[i];
				if ( t0 >= 0 )
				{
					var (a, b, c) = tris[t0];
					var w = bary[i];
					var p = pos[a] * w.x + pos[b] * w.y + pos[c] * w.z;
					var meshT = (p - camera.Eye).Length;
					if ( meshT <= floorT )
					{
						var n = (nrm[a] * w.x + nrm[b] * w.y + nrm[c] * w.z).Normal;
						if ( Vec3.Dot( n, dir ) > -0.05f )
						{
							// Facing away (or edge-on). Seen from behind - the inside of a sleeve or a mouth - the
							// FACE faces away too, and the normal is flipped to light that side. On the silhouette of a
							// smooth body the face still faces the camera and only the smoothed normal has turned past
							// edge-on; flipping that lit the rim from behind and drew a black line round everything.
							// There it is bent back until it just faces the camera.
							var fn = Vec3.Cross( pos[b] - pos[a], pos[c] - pos[a] );
							if ( Vec3.Dot( fn, n ) < 0f ) fn = -fn;
							if ( Vec3.Dot( fn, dir ) > 0f ) n = -n;
							else n = (n - dir * (Vec3.Dot( n, dir ) + 0.05f)).Normal;
						}
						wpos[i] = p; wnrm[i] = n; kind[i] = 1;
						depth[i] = Vec3.Dot( p - camera.Eye, fwd );
						continue;
					}
				}
				if ( floorT < float.MaxValue && floorT < sceneRadius * 40f )
				{
					var p = camera.Eye + dir * floorT;
					wpos[i] = p; wnrm[i] = new Vec3( 0, 0, 1 ); kind[i] = 2;
					depth[i] = Vec3.Dot( p - camera.Eye, fwd );
				}
				else depth[i] = float.MaxValue;
			}
		} );

		var env = scene.Environment;
		var envBake = env?.Baked();
		var frame = new Frame
		{
			W = W, H = H, Depth = depth, Eye = camera.Eye, Fwd = fwd, Right = right, Up = up, Focal = focal,
			Contact = scene.ContactShadows < 0f ? scene.AoRadius * 0.5f : scene.ContactShadows,
			Env = env, EnvBake = envBake,
		};
		if ( env is not null )
		{
			// The environment's frame: x from the subject toward the camera, y to the camera's right, z up.
			var front = new Vec3( -fwd.x, -fwd.y, 0f );
			front = front.LengthSquared > 1e-8f ? front.Normal : new Vec3( 1, 0, 0 );
			if ( !env.CameraRelative ) front = new Vec3( 1, 0, 0 );
			var yaw = env.Yaw * MathF.PI / 180f;
			front = new Vec3( front.x * MathF.Cos( yaw ) - front.y * MathF.Sin( yaw ), front.x * MathF.Sin( yaw ) + front.y * MathF.Cos( yaw ), 0f );
			frame.EnvX = front; frame.EnvY = env.CameraRelative ? Vec3.Cross( front, new Vec3( 0, 0, 1 ) ) : new Vec3( -front.y, front.x, 0f );
		}

		// --- screen-space ambient occlusion, and the bent normal (the least occluded direction) --
		var ao = new float[W * H];
		Array.Fill( ao, 1f );
		var bent = env is not null ? new Vec3[W * H] : null;
		if ( scene.AoStrength > 0f )
		{
			var kernel = AoKernel( Math.Clamp( scene.AoSamples, 4, 64 ) );
			Parallel.For( 0, H, y =>
			{
				for ( var x = 0; x < W; x++ )
				{
					var i = y * W + x;
					if ( kind[i] == 0 ) continue;
					var p = wpos[i]; var n = wnrm[i];
					Basis( n, out var t, out var bt );
					var occluded = 0f;
					var open = Vec3.Zero;
					var rot = Hash( x, y ) * MathF.PI * 2f;
					var ca = MathF.Cos( rot ); var sa = MathF.Sin( rot );
					// Under the denoiser each pixel takes every other sample, alternating in a checkerboard: the
					// blur gathers both halves back, at half the cost.
					var kStep = scene.AoDenoise ? 2 : 1;
					for ( var k = scene.AoDenoise ? (x + y) & 1 : 0; k < kernel.Length; k += kStep )
					{
						var kv = kernel[k];
						var kx = kv.x * ca - kv.y * sa; var ky = kv.x * sa + kv.y * ca;
						var offset = t * kx + bt * ky + n * kv.z;
						var s = p + offset * scene.AoRadius;
						var d = s - camera.Eye;
						var z = Vec3.Dot( d, fwd );
						if ( z < near ) continue;
						var px = (int)(W * 0.5f + Vec3.Dot( d, right ) * focal / z);
						var py = (int)(H * 0.5f - Vec3.Dot( d, up ) * focal / z);
						if ( px < 0 || py < 0 || px >= W || py >= H ) { open += offset; continue; }
						var sceneZ = depth[py * W + px];
						var occ = 0f;
						if ( sceneZ < z - 0.02f * scene.AoRadius )
						{
							occ = Smooth( 0f, 1f, scene.AoRadius / MathF.Max( 1e-4f, MathF.Abs( z - sceneZ ) ) );
							occluded += occ;
						}
						if ( bent is not null ) open += offset * (1f - occ);
					}
					ao[i] = 1f - scene.AoStrength * occluded * kStep / kernel.Length;
					if ( bent is not null )
					{
						var b = open.LengthSquared > 1e-10f ? open.Normal : n;
						// Halfway to the normal: the full bent normal over-corrects on noisy samples.
						bent[i] = (b + n).LengthSquared > 1e-8f ? (b + n).Normal : n;
					}
				}
			} );
			if ( scene.AoDenoise ) DenoiseAo( ao, depth, wnrm, kind, W, H, ss );
		}
		else if ( bent is not null ) for ( var i = 0; i < bent.Length; i++ ) bent[i] = wnrm[i];

		// --- shade ----------------------------------------------------------------------------
		var hdr = new Vec3[W * H];
		var emitBuf = scene.Bloom > 0f ? new Vec3[W * H] : null;
		var floorMat = new ShadeMaterial { UseVertexColours = false, Albedo = scene.FloorColour, Roughness = scene.FloorRoughness };
		Parallel.For( 0, H, y =>
		{
			Span<float> contact = stackalloc float[Math.Max( 1, scene.Lights.Count )];
			for ( var x = 0; x < W; x++ )
			{
				var i = y * W + x;
				var dir = RayDir( x + 0.5f, y + 0.5f );
				var bg = Vec3.Lerp( scene.BackgroundBottom, scene.BackgroundTop, Math.Clamp( 1f - (y + 0.5f) / H, 0f, 1f ) );
				if ( kind[i] == 0 ) { hdr[i] = bg; continue; }

				ShadeMaterial m; Vec3 albedo; float rough; var glow = Vec3.Zero;
				var n = wnrm[i]; var occl = ao[i]; var metal = -1f;
				var p = wpos[i];
				if ( kind[i] == 1 )
				{
					var tri = triId[i];
					var (a, b, c) = tris[tri];
					var w = bary[i];
					m = materials[mat[a]];
					albedo = col[a] * w.x + col[b] * w.y + col[c] * w.z;
					rough = vrough[a] * w.x + vrough[b] * w.y + vrough[c] * w.z;
					glow = vemit[a] * w.x + vemit[b] * w.y + vemit[c] * w.z;

					if ( m.MapSize > 0 )
					{
						var tri3 = m.Mapping == ShadeMapping.Triplanar
							|| (m.Mapping == ShadeMapping.Auto && !triUvOk[tri] && m.TriplanarScale > 0f);
						if ( tri3 && m.TriplanarScale > 0f )
						{
							var lod = MathF.Max( 0f, MathF.Log2( depth[i] / focal * m.MapSize / m.TriplanarScale ) );
							Triplanar( m, p / m.TriplanarScale, n, lod, ref albedo, ref rough, ref metal, ref glow, ref occl, ref n );
						}
						else
						{
							var (ua, ub, uc) = triUv[tri];
							var u = ua * w.x + ub * w.y + uc * w.z;
							u = new Vec2( u.x * m.MapScale.x + m.MapOffset.x, u.y * m.MapScale.y + m.MapOffset.y );
							var lod = triLod[tri];
							var wrap = !m.ClampMaps;
							var normalVar = 0f;
							if ( m.AlbedoMap is { } am ) albedo = Mul( MapMips.Get( am, m.MapSize, MapKind.Srgb ).Sample( u, lod, wrap ), m.MapTint );
							if ( m.RoughnessMap is { } rm ) rough = MapMips.Get( rm, m.MapSize, MapKind.Linear ).Sample( u, lod, wrap ).x;
							if ( m.EmissionMap is { } em ) glow += MapMips.Get( em, m.MapSize, MapKind.Srgb ).Sample( u, lod, wrap );
							if ( m.MetallicMap is { } mm ) metal = MapMips.Get( mm, m.MapSize, MapKind.Linear ).Sample( u, lod, wrap ).x;
							if ( m.AoMap is { } om ) occl *= MapMips.Get( om, m.MapSize, MapKind.Linear ).Sample( u, lod, wrap ).x;
							if ( m.NormalMap is { } nm )
							{
								// Tangent frame from the triangle's UVs, made square to the shading normal.
								var (tt, tb) = triTan[tri];
								var tn = MapMips.Get( nm, m.MapSize, MapKind.Normal ).Sample( u, lod, wrap );
								normalVar = Toksvig( tn.Length );
								tn = new Vec3( tn.x * m.NormalStrength, tn.y * m.NormalStrength, tn.z );
								if ( m.MapScale.x < 0f ) tn = new Vec3( -tn.x, tn.y, tn.z );
								if ( m.MapScale.y < 0f ) tn = new Vec3( tn.x, -tn.y, tn.z );
								var tg = (tt - n * Vec3.Dot( n, tt ));
								if ( tg.LengthSquared > 1e-10f )
								{
									tg = tg.Normal;
									var bt = Vec3.Cross( n, tg );
									if ( Vec3.Dot( bt, tb ) < 0f ) bt = -bt;
									var bentN = (tg * tn.x + bt * tn.y + n * MathF.Max( tn.z, 0.05f )).Normal;
									if ( bentN.LengthSquared > 0.5f ) n = bentN;
								}
							}
							if ( normalVar > 0f ) rough = ToksvigRoughness( rough, normalVar );
						}
					}
				}
				else { m = floorMat; albedo = scene.FloorColour; rough = m.Roughness; }

				// Geometric specular AA: the normal's change to the next pixel over is curvature the pixel
				// averages; as roughness it blurs the highlight instead of letting it sparkle (Tokuyoshi &
				// Kaplanyan 2019, sigma² 0.25 for the pixel filter, clamped at 0.18).
				if ( scene.SpecularAa && kind[i] == 1 )
				{
					var item = triItem[triId[i]];
					Vec3 Step( int j ) => j >= 0 && j < kind.Length && kind[j] == 1 && triItem[triId[j]] == item ? wnrm[j] - wnrm[i] : Vec3.Zero;
					var ddx = x + 1 < W ? Step( i + 1 ) : Step( i - 1 );
					var ddy = y + 1 < H ? Step( i + W ) : Step( i - W );
					var variance = MathF.Min( 0.5f * (ddx.LengthSquared + ddy.LengthSquared), 0.18f );
					if ( variance > 1e-6f ) rough = ToksvigRoughness( rough, variance );
				}

				if ( frame.Contact > 0f )
					for ( var li = 0; li < scene.Lights.Count; li++ )
						contact[li] = scene.Lights[li].Shadows && Vec3.Dot( wnrm[i], scene.Lights[li].Direction ) > -0.1f
							? frame.ContactShadow( p, wnrm[i], scene.Lights[li].Direction, Hash( x + 17, y + 31 ) ) : 1f;
				else contact.Fill( 1f );

				var colour = ShadePixel( scene, shadows, frame, m, albedo, rough, p, n, wnrm[i], bent is null ? n : BendLike( bent[i], wnrm[i], n ), -dir, occl, metal, contact ) + glow;
				if ( emitBuf is not null ) emitBuf[i] = glow;
				if ( kind[i] == 2 )
				{
					// The floor fades into the backdrop with distance: a studio sweep, not an edge.
					var dist = (wpos[i] - sceneCentre).Length / (sceneRadius * 6f);
					colour = Vec3.Lerp( colour, bg, Smooth( 0.35f, 1f, dist ) );
				}
				hdr[i] = colour;
			}
		} );

		if ( scene.FloorReflection > 0f && scene.Floor ) FloorReflect( scene, frame, hdr, kind, wpos, sceneCentre, sceneRadius, RayDir, ss );

		// --- bloom, tonemap, gamma, downsample --------------------------------------------------
		var bloom = scene.Bloom > 0f ? BuildBloom( scene, hdr, emitBuf, W, H, ss, width, height ) : null;
		var outRaster = new Raster( width, height, 0 );
		var coverage = scene.Coverage > 1 && tris.Count > 0;
		var halfDiag = MathF.Sqrt( width * width + height * height ) * 0.5f;
		// Each supersample through the curve once, then filtered down: averaging after the curve keeps
		// bright edges from fattening.
		var toned = new Vec3[W * H];
		Parallel.For( 0, H, yy =>
		{
			for ( var xx = 0; xx < W; xx++ )
				toned[yy * W + xx] = Tone( scene, hdr[yy * W + xx] * scene.Exposure, bloom, width, height, (xx + 0.5f) / ss, (yy + 0.5f) / ss );
		} );
		// Coverage AA: the edges from many cheap visibility samples, each coloured by a shaded pixel.
		var resolved = coverage ? CoverageResolve.Resolve( new CoverageResolve.Frame
		{
			Toned = toned, W = W, H = H, Ss = ss, Kind = kind, TriId = triId, Depth = depth,
			TriItem = triItem.ToArray(), Tris = tris, Sx = sx, Sy = sy, Sz = sz, Near = near,
		}, width, height, scene.Coverage ) : null;
		Parallel.For( 0, height, y =>
		{
			for ( var x = 0; x < width; x++ )
			{
				var acc = Vec3.Zero;
				if ( resolved is not null ) acc = resolved[y * width + x];
				else if ( scene.SoftFilter )
				{
					// A tent one output pixel wide each way, over the supersamples round this pixel.
					var wsum = 0f;
					for ( var j = -ss; j < 2 * ss; j++ )
					{
						var yy = y * ss + j; if ( yy < 0 || yy >= H ) continue;
						var dy = MathF.Abs( (j + 0.5f) / ss - 0.5f ); if ( dy >= 1f ) continue;
						for ( var k = -ss; k < 2 * ss; k++ )
						{
							var xx = x * ss + k; if ( xx < 0 || xx >= W ) continue;
							var dx = MathF.Abs( (k + 0.5f) / ss - 0.5f ); if ( dx >= 1f ) continue;
							var wt = (1f - dx) * (1f - dy);
							acc += toned[yy * W + xx] * wt;
							wsum += wt;
						}
					}
					acc /= wsum;
				}
				else
				{
					for ( var j = 0; j < ss; j++ )
						for ( var k = 0; k < ss; k++ )
							acc += toned[(y * ss + j) * W + x * ss + k];
					acc /= ss * ss;
				}
				if ( scene.Vignette > 0f )
				{
					var r = MathF.Sqrt( (x + 0.5f - width * 0.5f) * (x + 0.5f - width * 0.5f) + (y + 0.5f - height * 0.5f) * (y + 0.5f - height * 0.5f) ) / halfDiag;
					acc *= 1f - scene.Vignette * MathF.Pow( r, 2.4f );
				}
				outRaster.Pixels[y * width + x] = Raster.Rgb( (int)(LinearToSrgb( acc.x ) * 255f + 0.5f), (int)(LinearToSrgb( acc.y ) * 255f + 0.5f), (int)(LinearToSrgb( acc.z ) * 255f + 0.5f) );
			}
		} );
		return outRaster;
	}


	/// <summary>One supersample to display-linear: add its bloom, then the scene's curve.</summary>
	static Vec3 Tone( ShadeScene scene, Vec3 c, Vec3[] bloom, int w, int h, float ox, float oy )
	{
		if ( bloom is not null ) c += SampleBuffer( bloom, w, h, ox, oy ) * scene.Bloom;
		return scene.Tonemap switch
		{
			ShadeTonemap.Aces => AcesFit( c ),
			ShadeTonemap.AgX => AgX( c, false ),
			ShadeTonemap.AgXPunchy => AgX( c, true ),
			_ => Tonemap( c ),
		};
	}

	/// <summary>The bent normal is found on the smooth surface; carry it onto the normal-mapped one so the
	/// map's detail survives in the ambient.</summary>
	static Vec3 BendLike( Vec3 bent, Vec3 geo, Vec3 mapped )
	{
		var d = bent - geo;
		var b = mapped + d;
		return b.LengthSquared > 1e-8f ? b.Normal : mapped;
	}

	/// <summary>Variance a shortened (mip-averaged) normal stands for.</summary>
	static float Toksvig( float len )
	{
		len = Math.Clamp( len, 1e-3f, 1f );
		var v = (1f - len) / len;
		return v < 0.004f ? 0f : v;
	}

	static float ToksvigRoughness( float rough, float variance )
	{
		var a = rough * rough;
		return MathF.Min( 1f, MathF.Sqrt( MathF.Sqrt( a * a + variance ) ) );
	}

	/// <summary>Maps projected along the three axes and blended by the normal (Golus's whiteout blend for the normal map).</summary>
	static void Triplanar( ShadeMaterial m, Vec3 p, Vec3 n0, float lod, ref Vec3 albedo, ref float rough, ref float metal, ref Vec3 glow, ref float occl, ref Vec3 n )
	{
		var bw = new Vec3( MathF.Pow( MathF.Abs( n0.x ), 4f ), MathF.Pow( MathF.Abs( n0.y ), 4f ), MathF.Pow( MathF.Abs( n0.z ), 4f ) );
		bw /= MathF.Max( bw.x + bw.y + bw.z, 1e-6f );
		var uvX = new Vec2( p.z, p.y ); var uvY = new Vec2( p.x, p.z ); var uvZ = new Vec2( p.x, p.y );
		Vec3 Tri( MapMips mm ) => mm.Sample( uvX, lod, true ) * bw.x + mm.Sample( uvY, lod, true ) * bw.y + mm.Sample( uvZ, lod, true ) * bw.z;
		if ( m.AlbedoMap is { } am ) albedo = Mul( Tri( MapMips.Get( am, m.MapSize, MapKind.Srgb ) ), m.MapTint );
		if ( m.RoughnessMap is { } rm ) rough = Tri( MapMips.Get( rm, m.MapSize, MapKind.Linear ) ).x;
		if ( m.EmissionMap is { } em ) glow += Tri( MapMips.Get( em, m.MapSize, MapKind.Srgb ) );
		if ( m.MetallicMap is { } mt ) metal = Tri( MapMips.Get( mt, m.MapSize, MapKind.Linear ) ).x;
		if ( m.AoMap is { } om ) occl *= Tri( MapMips.Get( om, m.MapSize, MapKind.Linear ) ).x;
		if ( m.NormalMap is { } nm )
		{
			var mips = MapMips.Get( nm, m.MapSize, MapKind.Normal );
			var s = m.NormalStrength;
			// Rows run along +axis, a normal map's green points up the image, so green is flipped here.
			Vec3 T( Vec2 uv ) { var t = mips.Sample( uv, lod, true ); return new Vec3( t.x * s, -t.y * s, t.z ); }
			var tx = T( uvX ); var ty = T( uvY ); var tz = T( uvZ );
			var variance = Toksvig( tx.Length * bw.x + ty.Length * bw.y + tz.Length * bw.z );
			var wx = new Vec3( MathF.Abs( tx.z ) * n0.x, tx.y + n0.y, tx.x + n0.z );
			var wy = new Vec3( ty.x + n0.x, MathF.Abs( ty.z ) * n0.y, ty.y + n0.z );
			var wz = new Vec3( tz.x + n0.x, tz.y + n0.y, MathF.Abs( tz.z ) * n0.z );
			var nn = wx * bw.x + wy * bw.y + wz * bw.z;
			if ( nn.LengthSquared > 1e-8f && Vec3.Dot( nn, n0 ) > 0f ) n = nn.Normal;
			if ( variance > 0f ) rough = ToksvigRoughness( rough, variance );
		}
	}

	/// <summary>What the shading needs to know about the frame beyond the pixel: the depth buffer for contact
	/// shadows, and the environment's frame.</summary>
	sealed class Frame
	{
		public int W, H; public float[] Depth; public Vec3 Eye, Fwd, Right, Up; public float Focal, Contact;
		public ShadeEnvironment Env; public EnvBake EnvBake; public Vec3 EnvX, EnvY;

		public Vec3 ToEnv( Vec3 d ) => new( Vec3.Dot( d, EnvX ), Vec3.Dot( d, EnvY ), d.z );

		/// <summary>March a short way toward the light through the depth buffer; 0 where something is in the way.</summary>
		public float ContactShadow( Vec3 p, Vec3 n, Vec3 l, float jitter )
		{
			const int steps = 8;
			var len = Contact;
			var start = p + n * (len * 0.04f);
			for ( var k = 0; k < steps; k++ )
			{
				var s = start + l * (len * (k + jitter) / steps);
				var d = s - Eye;
				var z = Vec3.Dot( d, Fwd );
				if ( z < 0.05f ) return 1f;
				var px = (int)(W * 0.5f + Vec3.Dot( d, Right ) * Focal / z);
				var py = (int)(H * 0.5f - Vec3.Dot( d, Up ) * Focal / z);
				if ( px < 0 || py < 0 || px >= W || py >= H ) return 1f;
				var sceneZ = Depth[py * W + px];
				var dz = z - sceneZ;
				// Behind the visible surface, but not so far behind that it is a different, distant object.
				if ( dz > len * 0.03f && dz < len * 0.8f ) return Smooth( 0f, 1f, (k + jitter) / steps ) * 0.6f;
			}
			return 1f;
		}
	}

	static void DenoiseAo( float[] ao, float[] depth, Vec3[] nrm, byte[] kind, int W, int H, int ss )
	{
		var r = 2 * ss;
		var tmp = new float[ao.Length];
		for ( var pass = 0; pass < 2; pass++ )
		{
			var src = pass == 0 ? ao : tmp; var dst = pass == 0 ? tmp : ao;
			var dx = pass == 0 ? 1 : 0; var dy = pass == 0 ? 0 : 1;
			Parallel.For( 0, H, y =>
			{
				for ( var x = 0; x < W; x++ )
				{
					var i = y * W + x;
					if ( kind[i] == 0 ) { dst[i] = src[i]; continue; }
					var z = depth[i]; var n = nrm[i];
					float sum = 0, wsum = 0;
					for ( var k = -r; k <= r; k++ )
					{
						int xx = x + k * dx, yy = y + k * dy;
						if ( xx < 0 || yy < 0 || xx >= W || yy >= H ) continue;
						var j = yy * W + xx;
						if ( kind[j] == 0 ) continue;
						var w = MathF.Exp( -(k * k) / (r * r * 0.5f) );
						w *= MathF.Max( 0f, 1f - MathF.Abs( depth[j] - z ) / (z * 0.01f + 1e-4f) );
						w *= MathF.Pow( MathF.Max( 0f, Vec3.Dot( nrm[j], n ) ), 8f );
						sum += src[j] * w; wsum += w;
					}
					dst[i] = wsum > 1e-6f ? sum / wsum : src[i];
				}
			} );
		}
	}

	/// <summary>Screen-space mirror on the floor: march the reflected ray through the depth buffer, borrow the
	/// colour of the mesh pixel it lands on, then blur the reflection (not the floor) for a satin finish.</summary>
	static void FloorReflect( ShadeScene scene, Frame f, Vec3[] hdr, byte[] kind, Vec3[] wpos, Vec3 centre, float radius, Func<float, float, Vec3> rayDir, int ss )
	{
		var W = f.W; var H = f.H;
		var reach = radius * 2.5f;
		const int steps = 32;
		var col = new Vec3[W * H]; var wt = new float[W * H];
		Parallel.For( 0, H, y =>
		{
			for ( var x = 0; x < W; x++ )
			{
				var i = y * W + x;
				if ( kind[i] != 2 ) continue;
				var v = rayDir( x + 0.5f, y + 0.5f );
				var r = new Vec3( v.x, v.y, -v.z );
				var p = wpos[i];
				// Only the stretch of the ray inside the scene's bounding sphere can hit anything.
				var oc = p - centre; var bq = Vec3.Dot( oc, r ); var cq = oc.LengthSquared - radius * radius;
				var disc = bq * bq - cq;
				if ( disc <= 0f ) continue;
				var sq = MathF.Sqrt( disc );
				var t0 = MathF.Max( 0f, -bq - sq ); var t1 = MathF.Min( reach, -bq + sq );
				if ( t1 <= t0 ) continue;
				var stepLen = (t1 - t0) / steps;
				for ( var k = 1; k <= steps; k++ )
				{
					var travelled = t0 + stepLen * (k - 0.5f + Hash( x, y + k ) * 0.5f);
					var s = p + r * travelled;
					var d = s - f.Eye;
					var z = Vec3.Dot( d, f.Fwd );
					if ( z < 0.05f ) break;
					var px = (int)(W * 0.5f + Vec3.Dot( d, f.Right ) * f.Focal / z);
					var py = (int)(H * 0.5f - Vec3.Dot( d, f.Up ) * f.Focal / z);
					if ( px < 0 || py < 0 || px >= W || py >= H ) break;
					var j = py * W + px;
					if ( kind[j] != 1 ) continue;
					var dz = z - f.Depth[j];
					if ( dz > 0f && dz < stepLen * 3f + radius * 0.05f )
					{
						// Bisect back toward the last step in front, so hits do not band at the step length.
						float lo = MathF.Max( t0, travelled - stepLen ), hi = travelled;
						for ( var b = 0; b < 5; b++ )
						{
							var mid = (lo + hi) * 0.5f;
							var sm = p + r * mid; var dm = sm - f.Eye; var zm = Vec3.Dot( dm, f.Fwd );
							var qx = Math.Clamp( (int)(W * 0.5f + Vec3.Dot( dm, f.Right ) * f.Focal / zm), 0, W - 1 );
							var qy = Math.Clamp( (int)(H * 0.5f - Vec3.Dot( dm, f.Up ) * f.Focal / zm), 0, H - 1 );
							var q = qy * W + qx;
							if ( kind[q] == 1 && zm > f.Depth[q] ) { hi = mid; j = q; } else lo = mid;
						}
						travelled = hi;
						// Fresnel off a dielectric floor, fading with the reflected ray's length.
						var nv = MathF.Max( -v.z, 0f );
						var fr = Fresnel( 0.04f, nv ) * 4f + 0.08f;
						var fade = 1f - Smooth( 0.3f, 1f, travelled / reach );
						var w = Math.Clamp( scene.FloorReflection * fr * fade, 0f, 1f );
						col[i] = hdr[j] * w; wt[i] = w;
						break;
					}
				}
			}
		} );
		var blur = Math.Max( 0, (int)MathF.Round( scene.FloorReflectionBlur * 30f * ss ) );
		if ( blur > 0 )
		{
			for ( var pass = 0; pass < 2; pass++ ) { BoxBlur( col, wt, W, H, blur, true ); BoxBlur( col, wt, W, H, blur, false ); }
		}
		Parallel.For( 0, W * H, i =>
		{
			if ( kind[i] != 2 || wt[i] <= 1e-5f ) return;
			var c = col[i] / wt[i];
			hdr[i] = Vec3.Lerp( hdr[i], c, Math.Clamp( wt[i], 0f, 1f ) );
		} );
	}

	/// <summary>A running-sum box blur of a premultiplied colour and its weight, along rows or columns.</summary>
	static void BoxBlur( Vec3[] col, float[] wt, int W, int H, int r, bool rows )
	{
		var lines = rows ? H : W; var len = rows ? W : H;
		Parallel.For( 0, lines, line =>
		{
			var bc = new Vec3[len]; var bw = new float[len];
			int Idx( int k ) => rows ? line * W + k : k * W + line;
			var sc = Vec3.Zero; var sw = 0f;
			for ( var k = -r; k <= r; k++ ) { var kk = Math.Clamp( k, 0, len - 1 ); sc += col[Idx( kk )]; sw += wt[Idx( kk )]; }
			for ( var k = 0; k < len; k++ )
			{
				bc[k] = sc; bw[k] = sw;
				var add = Math.Min( k + r + 1, len - 1 ); var sub = Math.Max( k - r, 0 );
				sc += col[Idx( add )] - col[Idx( sub )]; sw += wt[Idx( add )] - wt[Idx( sub )];
			}
			var inv = 1f / (2 * r + 1);
			for ( var k = 0; k < len; k++ ) { col[Idx( k )] = bc[k] * inv; wt[Idx( k )] = bw[k] * inv; }
		} );
	}

	/// <summary>The glow, at output resolution: a bright pass (plus all emission) blurred over a mip pyramid
	/// and summed, so the halo has a tight core and a long soft tail.</summary>
	static Vec3[] BuildBloom( ShadeScene scene, Vec3[] hdr, Vec3[] emit, int W, int H, int ss, int w, int h )
	{
		var src = new Vec3[w * h];
		var thr = scene.BloomThreshold;
		Parallel.For( 0, h, y =>
		{
			for ( var x = 0; x < w; x++ )
			{
				var acc = Vec3.Zero;
				for ( var j = 0; j < ss; j++ )
					for ( var k = 0; k < ss; k++ )
					{
						var i = (y * ss + j) * W + x * ss + k;
						var c = hdr[i] * scene.Exposure;
						var lum = c.x * 0.2126f + c.y * 0.7152f + c.z * 0.0722f;
						// A soft knee rather than a hard cut, so the bloom has no visible edge.
						var knee = thr * 0.5f;
						var soft = Math.Clamp( lum - thr + knee, 0f, 2f * knee );
						soft = soft * soft / (4f * knee + 1e-5f);
						var contrib = MathF.Max( soft, lum - thr ) / MathF.Max( lum, 1e-5f );
						acc += c * contrib;
						if ( emit is not null ) acc += emit[i] * (scene.Exposure * scene.EmissiveBloom);
					}
				src[y * w + x] = acc / (ss * ss);
			}
		} );

		// Pyramid: each level half the last, blurred; deeper levels spread further.
		var levels = new List<(Vec3[] Px, int W, int H)>();
		var cur = src; int cw = w, ch = h;
		var depthMax = 2 + (int)MathF.Round( Math.Clamp( scene.BloomRadius, 0f, 1f ) * 5f );
		for ( var l = 0; l < depthMax && cw > 4 && ch > 4; l++ )
		{
			var nw = Math.Max( 1, cw / 2 ); var nh = Math.Max( 1, ch / 2 );
			var half = new Vec3[nw * nh];
			var c0 = cur; var pw = cw; var ph = ch;
			Parallel.For( 0, nh, y =>
			{
				for ( var x = 0; x < nw; x++ )
				{
					int x0 = Math.Min( 2 * x, pw - 1 ), x1 = Math.Min( 2 * x + 1, pw - 1 ), y0 = Math.Min( 2 * y, ph - 1 ), y1 = Math.Min( 2 * y + 1, ph - 1 );
					half[y * nw + x] = (c0[y0 * pw + x0] + c0[y0 * pw + x1] + c0[y1 * pw + x0] + c0[y1 * pw + x1]) * 0.25f;
				}
			} );
			Blur5( half, nw, nh );
			levels.Add( (half, nw, nh) );
			cur = half; cw = nw; ch = nh;
		}

		var outB = new Vec3[w * h];
		var weights = new float[levels.Count]; var total = 0f;
		for ( var l = 0; l < levels.Count; l++ ) { weights[l] = MathF.Pow( 0.6f + 0.35f * scene.BloomRadius, l ); total += weights[l]; }
		Parallel.For( 0, h, y =>
		{
			for ( var x = 0; x < w; x++ )
			{
				var acc = Vec3.Zero;
				for ( var l = 0; l < levels.Count; l++ )
				{
					var (px, lw, lh) = levels[l];
					acc += SampleBuffer( px, lw, lh, (x + 0.5f) * lw / w, (y + 0.5f) * lh / h ) * weights[l];
				}
				outB[y * w + x] = acc / total;
			}
		} );
		return outB;
	}

	static void Blur5( Vec3[] px, int w, int h )
	{
		float[] k = { 1f / 16f, 4f / 16f, 6f / 16f, 4f / 16f, 1f / 16f };
		var tmp = new Vec3[px.Length];
		for ( var y = 0; y < h; y++ )
			for ( var x = 0; x < w; x++ )
			{
				var a = Vec3.Zero;
				for ( var i = -2; i <= 2; i++ ) a += px[y * w + Math.Clamp( x + i, 0, w - 1 )] * k[i + 2];
				tmp[y * w + x] = a;
			}
		for ( var y = 0; y < h; y++ )
			for ( var x = 0; x < w; x++ )
			{
				var a = Vec3.Zero;
				for ( var i = -2; i <= 2; i++ ) a += tmp[Math.Clamp( y + i, 0, h - 1 ) * w + x] * k[i + 2];
				px[y * w + x] = a;
			}
	}

	/// <summary>Bilinear read of a buffer at a position in its own pixels (centres at +0.5).</summary>
	static Vec3 SampleBuffer( Vec3[] px, int w, int h, float x, float y )
	{
		var fx = x - 0.5f; var fy = y - 0.5f;
		var x0 = (int)MathF.Floor( fx ); var y0 = (int)MathF.Floor( fy );
		var tx = fx - x0; var ty = fy - y0;
		Vec3 At( int a, int b ) => px[Math.Clamp( b, 0, h - 1 ) * w + Math.Clamp( a, 0, w - 1 )];
		return Vec3.Lerp( Vec3.Lerp( At( x0, y0 ), At( x0 + 1, y0 ), tx ), Vec3.Lerp( At( x0, y0 + 1 ), At( x0 + 1, y0 + 1 ), tx ), ty );
	}

	/// <summary>Thin-film stand-in: a hue that walks blue → purple → green → gold as the view grazes.</summary>
	static Vec3 OilFilm( float t )
	{
		t = Math.Clamp( t, 0f, 1f ) * 3f;
		Vec3 Blue = new( 0.25f, 0.4f, 1f ), Purple = new( 0.7f, 0.25f, 0.95f ), Green = new( 0.25f, 0.95f, 0.55f ), Gold = new( 1f, 0.8f, 0.3f );
		return t < 1f ? Vec3.Lerp( Blue, Purple, t ) : t < 2f ? Vec3.Lerp( Purple, Green, t - 1f ) : Vec3.Lerp( Green, Gold, t - 2f );
	}

	static Vec3 ShadePixel( ShadeScene scene, ShadowMap[] shadows, Frame frame, ShadeMaterial m, Vec3 albedo, float roughness, Vec3 p, Vec3 n, Vec3 ngeo, Vec3 bentN, Vec3 v, float ao, float metallic, ReadOnlySpan<float> contact )
	{
		var metal = Math.Clamp( metallic >= 0f ? metallic : m.Metallic, 0f, 1f );
		roughness = Math.Clamp( roughness, 0f, 1f );
		var nv = MathF.Max( Vec3.Dot( n, v ), 1e-3f );
		var f0 = Vec3.Lerp( new Vec3( m.Specular, m.Specular, m.Specular ), albedo, metal );
		// The film tints what the surface reflects, never what it scatters: the base stays dark.
		var film = Vec3.One;
		if ( m.Iridescence > 0f )
		{
			var phase = 1f - nv + MathF.Abs( (p.x + p.y * 0.7f + p.z * 0.4f) * 0.05f ) % 0.35f;
			film = Vec3.Lerp( Vec3.One, OilFilm( phase ) * 1.5f, m.Iridescence );
			f0 = Mul( f0, film );
		}
		var diffuseColour = albedo * (1f - metal);
		var tint = m.SubsurfaceTint ?? Saturate( albedo, 1.1f );
		var nc = m.CoatFollowsNormalMap ? n : ngeo;
		var nvc = MathF.Max( Vec3.Dot( nc, v ), 1e-3f );
		var coatF = m.Coat * Fresnel( 0.04f, nvc );
		var result = Vec3.Zero;

		for ( var li = 0; li < scene.Lights.Count; li++ )
		{
			var light = scene.Lights[li];
			var l = light.Direction;
			var radiance = light.Colour * light.Intensity;
			var nl = Vec3.Dot( n, l );
			var (visible, thickness) = shadows[li] is { } sm ? sm.Sample( p, n, light.Size ) : (1f, float.MaxValue);
			visible *= contact[li];

			// Diffuse, wrapped round the terminator by the subsurface amount.
			var wrap = m.Subsurface * 0.5f;
			var diff = MathF.Max( 0f, (nl + wrap) / (1f + wrap) );
			var lit = Mul( diffuseColour, radiance ) * (diff * visible / MathF.PI * 3.1f);
			// Light that went in elsewhere and comes out here: strong where the part is thin.
			if ( m.Subsurface > 0f )
			{
				var through = thickness < float.MaxValue ? MathF.Exp( -thickness / MathF.Max( 1e-3f, m.ScatterDistance ) ) : 0f;
				var back = MathF.Pow( MathF.Max( 0f, Vec3.Dot( v, -l ) ), 2f ) * 0.7f + 0.3f;
				lit += Mul( Mul( tint, albedo ), radiance ) * (m.Subsurface * through * back * 0.45f);
			}

			if ( nl > 0f )
			{
				var h = (l + v).Normal;
				var nh = MathF.Max( Vec3.Dot( n, h ), 0f );
				var vh = MathF.Max( Vec3.Dot( v, h ), 0f );
				// A light with size widens the highlight: the roughness it sees is never below its size.
				var rough = MathF.Max( roughness, light.Size * 0.6f );
				var spec = Ggx( nh, nl, nv, rough );
				var F = Fresnel( f0, vh );
				lit += Mul( F, radiance ) * (spec * nl * visible);
				if ( m.Coat > 0f )
				{
					// The lacquer lies smooth over the detail: its highlight follows the surface, not the normal map.
					var nlc = MathF.Max( Vec3.Dot( nc, l ), 0f );
					var coatRough = MathF.Max( m.CoatRoughness, light.Size * 0.6f );
					var cs = nlc > 0f ? Ggx( MathF.Max( Vec3.Dot( nc, h ), 0f ), nlc, nvc, coatRough ) * Fresnel( 0.04f, vh ) * m.Coat : 0f;
					lit = lit * (1f - coatF) + Mul( radiance, film ) * (cs * nlc * visible);
				}
			}
			result += lit;
		}

		var r = n * (2f * Vec3.Dot( n, v )) - v;
		if ( frame.EnvBake is { } bake )
		{
			// --- image-based lighting: SH diffuse on the bent normal, split-sum GGX specular -------
			var env = frame.Env;
			var ambient = Mul( diffuseColour, bake.Diffuse( frame.ToEnv( bentN ) ) ) * (env.Diffuse * ao);
			if ( m.Subsurface > 0f ) ambient += Mul( Mul( tint, albedo ), bake.Diffuse( frame.ToEnv( bentN ) ) ) * (0.15f * m.Subsurface * env.Diffuse * ao);

			// Reflections that point into the surface (a normal map tilting past the silhouette) see nothing.
			var horizon = Math.Clamp( 1f + 1.3f * Vec3.Dot( r, ngeo ), 0f, 1f ); horizon *= horizon;
			var (A, B) = BrdfLut.Get( nv, roughness );
			// Multiple scattering (Fdez-Agüera): what single-bounce GGX loses comes back tinted by F0,
			// so rough gold stays gold instead of going grey-dark.
			var ess = A + B;
			var fss = f0 * A + Vec3.One * B;
			var spec = Mul( fss, Vec3.One + f0 * ((1f - ess) / MathF.Max( ess, 1e-3f )) );
			var so = SpecularOcclusion( nv, ao, roughness ) * horizon;
			ambient += Mul( Mul( spec, film ), bake.Specular( frame.ToEnv( r ), roughness ) ) * (env.Specular * so);

			if ( m.Coat > 0f )
			{
				var rc = nc * (2f * Vec3.Dot( nc, v )) - v;
				var (cA, cB) = BrdfLut.Get( nvc, m.CoatRoughness );
				var coatSpec = bake.Specular( frame.ToEnv( rc ), m.CoatRoughness ) * ((0.04f * cA + cB) * m.Coat * env.Specular * SpecularOcclusion( nvc, ao, m.CoatRoughness ));
				ambient = ambient * (1f - coatF) + Mul( coatSpec, film );
			}
			return result + ambient;
		}

		// Ambient: the sky from above, bounce from below, both occluded.
		var hemi = Vec3.Lerp( scene.GroundColour, scene.SkyColour, n.z * 0.5f + 0.5f );
		result += Mul( diffuseColour, hemi ) * ao;
		if ( m.Subsurface > 0f ) result += Mul( Mul( tint, albedo ), hemi ) * (0.12f * m.Subsurface * ao);
		// Reflection of the sky gradient along the mirror direction, sharper for smoother surfaces.
		var envG = Vec3.Lerp( scene.GroundColour, scene.SkyColour * 1.3f, Smooth( -0.3f, 0.6f, r.z ) );
		// The studio's lights are big bright shapes in the world, and a glossy surface mirrors them: that
		// crisp white streak is most of what reads as "lacquered". Each light is a soft disc in the
		// reflection, as wide as the light, blurred by the surface's roughness.
		var glossEnv = envG;
		foreach ( var light in scene.Lights )
		{
			var c = Vec3.Dot( r, light.Direction );
			var spread = light.Size * 0.5f + m.CoatRoughness * 0.5f;
			var shine = Smooth( MathF.Cos( spread * 1.8f ), MathF.Cos( spread * 0.6f ), c );
			glossEnv += light.Colour * (light.Intensity * 2.2f * shine);
		}
		var envF = Mul( Fresnel( f0, nv ), film ) * (1f - roughness) * (1f - roughness);
		result += Mul( envF, envG ) * ao;
		glossEnv = Mul( glossEnv, film );
		if ( m.Coat > 0f ) result = result * (1f - coatF) + glossEnv * (MathF.Max( coatF, 0.06f ) * m.Coat * MathF.Max( ao, 0.5f ));
		return result;
	}

	/// <summary>Lagarde's specular occlusion: AO darkens reflections less on smooth surfaces and at grazing angles.</summary>
	static float SpecularOcclusion( float nv, float ao, float rough )
	{
		var a = rough * rough;
		return Math.Clamp( MathF.Pow( nv + ao, MathF.Pow( 2f, -16f * a - 1f ) ) - 1f + ao, 0f, 1f );
	}

	// --- shadow map --------------------------------------------------------------------------

	sealed class ShadowMap
	{
		int _size; float _half; Vec3 _c, _r, _u, _f; float[] _depth;

		public static ShadowMap Build( ShadeLight light, List<(int A, int B, int C)> tris, List<Vec3> pos, Vec3 centre, float half, int size )
		{
			var sm = new ShadowMap { _size = size, _half = half, _c = centre, _f = -light.Direction };
			sm._r = Vec3.Cross( sm._f, MathF.Abs( sm._f.z ) < 0.95f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) ).Normal;
			sm._u = Vec3.Cross( sm._r, sm._f ).Normal;
			var sx = new float[pos.Count]; var sy = new float[pos.Count]; var sz = new float[pos.Count];
			for ( var i = 0; i < pos.Count; i++ ) (sx[i], sy[i], sz[i]) = sm.Project( pos[i] );
			sm._depth = new float[size * size];
			Array.Fill( sm._depth, float.MaxValue );
			Rasterize( tris, sx, sy, sz, size, size, float.MinValue, sm._depth, null, null, perspective: false );
			return sm;
		}

		(float X, float Y, float Z) Project( Vec3 p )
		{
			var d = p - _c;
			return (_size * 0.5f + Vec3.Dot( d, _r ) / _half * _size * 0.5f, _size * 0.5f - Vec3.Dot( d, _u ) / _half * _size * 0.5f, Vec3.Dot( d, _f ));
		}

		static readonly (float X, float Y)[] Disc = BuildDisc( 16 );

		/// <summary>How much of the light reaches p (0..1, soft), and how much material the light
		/// crossed to get there (for translucency).</summary>
		public (float Visible, float Thickness) Sample( Vec3 p, Vec3 n, float size )
		{
			var texel = 2f * _half / _size;
			var (x, y, z) = Project( p + n * (texel * 1.5f) );
			var radius = 1.5f + size * 22f;
			var lit = 0f; var count = 0;
			var bias = texel * 1.2f;
			var cx = (int)x; var cy = (int)y;
			var nearest = float.MaxValue;
			if ( cx >= 0 && cy >= 0 && cx < _size && cy < _size ) nearest = _depth[cy * _size + cx];
			foreach ( var (dx, dy) in Disc )
			{
				var px = (int)(x + dx * radius); var py = (int)(y + dy * radius);
				count++;
				if ( px < 0 || py < 0 || px >= _size || py >= _size ) { lit += 1f; continue; }
				if ( z - bias <= _depth[py * _size + px] ) lit += 1f;
			}
			var thickness = nearest < float.MaxValue ? MathF.Max( 0f, z - nearest ) : 0f;
			return (lit / count, thickness);
		}
	}

	// --- shared raster loop --------------------------------------------------------------------

	/// <summary>Depth-tested triangles into a buffer, rows banded and banded rows in parallel. With
	/// ids, also which triangle won each pixel and its (perspective-correct) barycentrics.</summary>
	static void Rasterize( List<(int A, int B, int C)> tris, float[] sx, float[] sy, float[] sz, int W, int H, float near, float[] depth, int[] id, Vec3[] bary, bool perspective )
	{
		const int band = 16;
		var bands = (H + band - 1) / band;
		var bins = new List<int>[bands];
		for ( var b = 0; b < bands; b++ ) bins[b] = new List<int>();
		for ( var t = 0; t < tris.Count; t++ )
		{
			var (a, b2, c) = tris[t];
			if ( perspective && (sz[a] < near || sz[b2] < near || sz[c] < near) ) continue;
			var y0 = (int)MathF.Floor( MathF.Min( sy[a], MathF.Min( sy[b2], sy[c] ) ) );
			var y1 = (int)MathF.Ceiling( MathF.Max( sy[a], MathF.Max( sy[b2], sy[c] ) ) );
			var x0 = MathF.Min( sx[a], MathF.Min( sx[b2], sx[c] ) ); var x1 = MathF.Max( sx[a], MathF.Max( sx[b2], sx[c] ) );
			if ( y1 < 0 || y0 >= H || x1 < 0 || x0 >= W ) continue;
			for ( var bi = Math.Max( 0, y0 / band ); bi <= Math.Min( bands - 1, y1 / band ); bi++ ) bins[bi].Add( t );
		}
		Parallel.For( 0, bands, bi =>
		{
			var rowLo = bi * band; var rowHi = Math.Min( H - 1, rowLo + band - 1 );
			foreach ( var t in bins[bi] )
			{
				var (a, b, c) = tris[t];
				float ax = sx[a], ay = sy[a], bx = sx[b], by = sy[b], cx = sx[c], cy = sy[c];
				var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
				if ( MathF.Abs( area ) < 1e-10f ) continue;
				var inv = 1f / area;
				var minX = Math.Max( 0, (int)MathF.Floor( MathF.Min( ax, MathF.Min( bx, cx ) ) ) );
				var maxX = Math.Min( W - 1, (int)MathF.Ceiling( MathF.Max( ax, MathF.Max( bx, cx ) ) ) );
				var minY = Math.Max( rowLo, (int)MathF.Floor( MathF.Min( ay, MathF.Min( by, cy ) ) ) );
				var maxY = Math.Min( rowHi, (int)MathF.Ceiling( MathF.Max( ay, MathF.Max( by, cy ) ) ) );
				float za = sz[a], zb = sz[b], zc = sz[c];
				for ( var y = minY; y <= maxY; y++ )
				{
					var py = y + 0.5f;
					for ( var x = minX; x <= maxX; x++ )
					{
						var px = x + 0.5f;
						var w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
						var w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
						var w2 = 1f - w0 - w1;
						if ( w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f ) continue;
						float z;
						if ( perspective )
						{
							// Perspective-correct: interpolate 1/z, and weights over z.
							var iz = w0 / za + w1 / zb + w2 / zc;
							z = 1f / iz;
							if ( bary is not null ) { w0 = w0 / za * z; w1 = w1 / zb * z; w2 = w2 / zc * z; }
						}
						else z = w0 * za + w1 * zb + w2 * zc;
						var i = y * W + x;
						if ( z >= depth[i] ) continue;
						depth[i] = z;
						if ( id is not null ) { id[i] = t; bary[i] = new Vec3( w0, w1, w2 ); }
					}
				}
			}
		} );
	}

	/// <summary>Model-space directions of +u and +v across a triangle, for tangent-space normal maps.</summary>
	static (Vec3 T, Vec3 B) Tangents( Vec3 p0, Vec3 p1, Vec3 p2, Vec2 u0, Vec2 u1, Vec2 u2 )
	{
		var e1 = p1 - p0; var e2 = p2 - p0;
		float du1 = u1.x - u0.x, dv1 = u1.y - u0.y, du2 = u2.x - u0.x, dv2 = u2.y - u0.y;
		var det = du1 * dv2 - du2 * dv1;
		if ( MathF.Abs( det ) < 1e-12f ) return default;
		var r = 1f / det;
		// Effigy's v runs down the image (top-left origin) while a normal map's green points up the
		// image, so the "up" bitangent is -dP/dv.
		return ((e1 * dv2 - e2 * dv1) * r, -(e2 * du1 - e1 * du2) * r);
	}

	/// <summary>Area-weighted smooth normals per vertex (Newell's method per face, so n-gons are fine).</summary>
	static Vec3[] VertexNormals( IList<Vec3> pos, IEnumerable<Face> faces )
	{
		var normals = new Vec3[pos.Count];
		foreach ( var f in faces )
		{
			var idx = f.Indices;
			var fn = Vec3.Zero;
			for ( var i = 0; i < idx.Length; i++ )
			{
				var a = pos[idx[i]]; var b = pos[idx[(i + 1) % idx.Length]];
				fn += new Vec3( (a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y) );
			}
			foreach ( var i in idx ) normals[i] += fn;
		}
		for ( var i = 0; i < normals.Length; i++ )
			normals[i] = normals[i].LengthSquared > 1e-12f ? normals[i].Normal : Vec3.Zero;
		return normals;
	}

	// --- maths ---------------------------------------------------------------------------------

	static float Ggx( float nh, float nl, float nv, float roughness )
	{
		var a = MathF.Max( roughness * roughness, 0.0016f );
		var a2 = a * a;
		var d = nh * nh * (a2 - 1f) + 1f;
		var D = a2 / (MathF.PI * d * d);
		var k = a * 0.5f;
		var G = nl / (nl * (1f - k) + k) * (nv / (nv * (1f - k) + k));
		return D * G / MathF.Max( 4f * nl * nv, 1e-4f );
	}

	static float Fresnel( float f0, float c ) => f0 + (1f - f0) * MathF.Pow( 1f - Math.Clamp( c, 0f, 1f ), 5f );
	static Vec3 Fresnel( Vec3 f0, float c ) { var k = MathF.Pow( 1f - Math.Clamp( c, 0f, 1f ), 5f ); return f0 + (Vec3.One - f0) * k; }
	static Vec3 Mul( Vec3 a, Vec3 b ) => new( a.x * b.x, a.y * b.y, a.z * b.z );
	static float Smooth( float a, float b, float x ) { var t = Math.Clamp( (x - a) / (b - a), 0f, 1f ); return t * t * (3f - 2f * t); }
	static Vec3 Saturate( Vec3 c, float amount ) { var g = (c.x + c.y + c.z) / 3f; return new Vec3( MathF.Max( 0, g + (c.x - g) * amount ), MathF.Max( 0, g + (c.y - g) * amount ), MathF.Max( 0, g + (c.z - g) * amount ) ); }

	/// <summary>A filmic curve (Narkowicz's ACES fit): highlights roll off instead of clipping.</summary>
	static Vec3 Tonemap( Vec3 c )
	{
		static float T( float x ) => Math.Clamp( x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f );
		return new Vec3( T( c.x * 0.8f ), T( c.y * 0.8f ), T( c.z * 0.8f ) );
	}

	static Vec3 AcesFit( Vec3 c )
	{
		static float T( float x ) => Math.Clamp( x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f );
		return new Vec3( T( c.x ), T( c.y ), T( c.z ) );
	}

	/// <summary>AgX in Benjamin Wrensch's polynomial fit (as in Blender 4): log encoding in an inset gamut, a
	/// sigmoid, back out. Returns display-linear.</summary>
	static Vec3 AgX( Vec3 c, bool punchy )
	{
		c = new Vec3( MathF.Max( c.x, 0f ), MathF.Max( c.y, 0f ), MathF.Max( c.z, 0f ) );
		var x = 0.842479062253094f * c.x + 0.0784335999999992f * c.y + 0.0792237451477643f * c.z;
		var y = 0.0423282422610123f * c.x + 0.878468636469772f * c.y + 0.0791661274605434f * c.z;
		var z = 0.0423756549057051f * c.x + 0.0784336f * c.y + 0.879142973793104f * c.z;
		const float lo = -12.47393f, hi = 4.026069f;
		static float Curve( float v )
		{
			v = (Math.Clamp( MathF.Log2( MathF.Max( v, 1e-10f ) ), lo, hi ) - lo) / (hi - lo);
			var v2 = v * v; var v4 = v2 * v2;
			return 15.5f * v4 * v2 - 40.14f * v4 * v + 31.96f * v4 - 6.868f * v2 * v + 0.4298f * v2 + 0.1191f * v - 0.00232f;
		}
		x = Curve( x ); y = Curve( y ); z = Curve( z );
		if ( punchy )
		{
			x = MathF.Pow( MathF.Max( x, 0f ), 1.35f ); y = MathF.Pow( MathF.Max( y, 0f ), 1.35f ); z = MathF.Pow( MathF.Max( z, 0f ), 1.35f );
			var luma = 0.2126f * x + 0.7152f * y + 0.0722f * z;
			x = luma + 1.4f * (x - luma); y = luma + 1.4f * (y - luma); z = luma + 1.4f * (z - luma);
		}
		var r = 1.19687900512017f * x - 0.0980208811401368f * y - 0.0990297440797205f * z;
		var g = -0.0528968517574562f * x + 1.15190312990417f * y - 0.0989611768448433f * z;
		var b = -0.0529716355144438f * x - 0.0980434501171241f * y + 1.15107367264116f * z;
		static float Lin( float v ) => MathF.Pow( Math.Clamp( v, 0f, 1f ), 2.2f );
		return new Vec3( Lin( r ), Lin( g ), Lin( b ) );
	}

	public static float SrgbToLinear( float c ) => c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );
	public static float LinearToSrgb( float c ) => Math.Clamp( c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow( c, 1f / 2.4f ) - 0.055f, 0f, 1f );

	static void Basis( Vec3 n, out Vec3 t, out Vec3 b )
	{
		t = Vec3.Cross( n, MathF.Abs( n.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) ).Normal;
		b = Vec3.Cross( n, t );
	}

	static Vec3[] AoKernel( int count )
	{
		var rng = new Random( 7 );
		var k = new Vec3[count];
		for ( var i = 0; i < count; i++ )
		{
			var u = (float)rng.NextDouble(); var a = (float)rng.NextDouble() * MathF.PI * 2f;
			var r = MathF.Sqrt( u );
			var v = new Vec3( r * MathF.Cos( a ), r * MathF.Sin( a ), MathF.Sqrt( 1f - u ) );
			var s = (i + 1f) / count;
			k[i] = v * (0.15f + 0.85f * s * s);
		}
		return k;
	}

	static (float, float)[] BuildDisc( int count )
	{
		var d = new (float, float)[count];
		for ( var i = 0; i < count; i++ )
		{
			var r = MathF.Sqrt( (i + 0.5f) / count );
			var a = i * 2.39996f;
			d[i] = (r * MathF.Cos( a ), r * MathF.Sin( a ));
		}
		return d;
	}

	static float Hash( int x, int y )
	{
		var h = (uint)(x * 374761393 + y * 668265263);
		h = (h ^ (h >> 13)) * 1274126177u;
		return (h ^ (h >> 16)) / (float)uint.MaxValue;
	}
}
