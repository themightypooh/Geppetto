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
	public float Exposure = 1f;

	public ShadeScene Add( PolyMesh mesh, ShadeMaterial material = null )
	{
		Items.Add( (mesh, material ?? new ShadeMaterial()) );
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
	public static Raster Sheet( ShadeScene scene, int views, int tile, float elevation = 12f, int supersample = 2, string label = null )
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

	public static Raster Render( ShadeScene scene, ShadeCamera camera, int width, int height, int supersample = 2 )
	{
		var ss = Math.Clamp( supersample, 1, 4 );
		var W = width * ss;
		var H = height * ss;

		// --- one flat list of vertices and triangles over every mesh ---------------------------
		var pos = new List<Vec3>(); var nrm = new List<Vec3>(); var col = new List<Vec3>(); var mat = new List<int>();
		var vrough = new List<float>(); var vemit = new List<Vec3>();
		var tris = new List<(int A, int B, int C)>();
		var triUv = new List<(Vec2 A, Vec2 B, Vec2 C)>();
		for ( var m = 0; m < scene.Items.Count; m++ )
		{
			var (mesh, material) = scene.Items[m];
			var baseIndex = pos.Count;
			var normals = VertexNormals( mesh.Positions, mesh.Faces );
			for ( var i = 0; i < mesh.Positions.Count; i++ )
			{
				pos.Add( mesh.Positions[i] );
				nrm.Add( normals[i] );
				var c = material.UseVertexColours && mesh.VertexColors is { } vc && i < vc.Length
					? new Vec3( SrgbToLinear( vc[i].x ), SrgbToLinear( vc[i].y ), SrgbToLinear( vc[i].z ) )
					: material.Albedo;
				col.Add( c );
				mat.Add( m );
				vrough.Add( material.VertexRoughness is { } vr && i < vr.Length ? vr[i] : material.Roughness );
				vemit.Add( material.VertexEmission is { } ve && i < ve.Length
					? new Vec3( SrgbToLinear( ve[i].x ), SrgbToLinear( ve[i].y ), SrgbToLinear( ve[i].z ) ) + material.Emission
					: material.Emission );
			}
			foreach ( var f in mesh.Faces )
				for ( var k = 1; k + 1 < f.Indices.Length; k++ )
				{
					tris.Add( (baseIndex + f.Indices[0], baseIndex + f.Indices[k], baseIndex + f.Indices[k + 1]) );
					triUv.Add( f.UVs is { } uv && uv.Length == f.Indices.Length ? (uv[0], uv[k], uv[k + 1]) : default );
				}
		}
		var materials = scene.Items.Select( i => i.Material ).ToArray();

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
						if ( Vec3.Dot( n, dir ) > 0 ) n = -n;
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

		// --- screen-space ambient occlusion ---------------------------------------------------
		var ao = new float[W * H];
		Array.Fill( ao, 1f );
		if ( scene.AoStrength > 0f )
		{
			var kernel = AoKernel( 14 );
			Parallel.For( 0, H, y =>
			{
				for ( var x = 0; x < W; x++ )
				{
					var i = y * W + x;
					if ( kind[i] == 0 ) continue;
					var p = wpos[i]; var n = wnrm[i];
					Basis( n, out var t, out var bt );
					var occluded = 0f;
					var rot = Hash( x, y ) * MathF.PI * 2f;
					for ( var k = 0; k < kernel.Length; k++ )
					{
						var kv = kernel[k];
						var ca = MathF.Cos( rot ); var sa = MathF.Sin( rot );
						var kx = kv.x * ca - kv.y * sa; var ky = kv.x * sa + kv.y * ca;
						var s = p + (t * kx + bt * ky + n * kv.z) * scene.AoRadius;
						var d = s - camera.Eye;
						var z = Vec3.Dot( d, fwd );
						if ( z < near ) continue;
						var px = (int)(W * 0.5f + Vec3.Dot( d, right ) * focal / z);
						var py = (int)(H * 0.5f - Vec3.Dot( d, up ) * focal / z);
						if ( px < 0 || py < 0 || px >= W || py >= H ) continue;
						var sceneZ = depth[py * W + px];
						if ( sceneZ < z - 0.02f * scene.AoRadius )
						{
							var range = Smooth( 0f, 1f, scene.AoRadius / MathF.Max( 1e-4f, MathF.Abs( z - sceneZ ) ) );
							occluded += range;
						}
					}
					ao[i] = 1f - scene.AoStrength * occluded / kernel.Length;
				}
			} );
		}

		// --- shade ----------------------------------------------------------------------------
		var hdr = new Vec3[W * H];
		var floorMat = new ShadeMaterial { UseVertexColours = false, Albedo = scene.FloorColour, Roughness = scene.FloorRoughness };
		Parallel.For( 0, H, y =>
		{
			for ( var x = 0; x < W; x++ )
			{
				var i = y * W + x;
				var dir = RayDir( x + 0.5f, y + 0.5f );
				var bg = Vec3.Lerp( scene.BackgroundBottom, scene.BackgroundTop, Math.Clamp( 1f - (y + 0.5f) / H, 0f, 1f ) );
				if ( kind[i] == 0 ) { hdr[i] = bg; continue; }

				ShadeMaterial m; Vec3 albedo; float rough; var glow = Vec3.Zero;
				if ( kind[i] == 1 )
				{
					var (a, b, c) = tris[triId[i]];
					var w = bary[i];
					m = materials[mat[a]];
					albedo = col[a] * w.x + col[b] * w.y + col[c] * w.z;
					rough = vrough[a] * w.x + vrough[b] * w.y + vrough[c] * w.z;
					glow = vemit[a] * w.x + vemit[b] * w.y + vemit[c] * w.z;

					if ( m.MapSize > 0 )
					{
						var (ua, ub, uc) = triUv[triId[i]];
						var u = ua * w.x + ub * w.y + uc * w.z;
						if ( m.AlbedoMap is { } am ) albedo = SampleMap( am, m.MapSize, u, srgb: true );
						if ( m.RoughnessMap is { } rm ) rough = SampleMap( rm, m.MapSize, u, srgb: false ).x;
						if ( m.EmissionMap is { } em ) glow += SampleMap( em, m.MapSize, u, srgb: true );
					}
				}
				else { m = floorMat; albedo = scene.FloorColour; rough = m.Roughness; }

				var colour = ShadePixel( scene, shadows, m, albedo, rough, wpos[i], wnrm[i], -dir, ao[i] ) + glow;
				if ( kind[i] == 2 )
				{
					// The floor fades into the backdrop with distance: a studio sweep, not an edge.
					var dist = (wpos[i] - sceneCentre).Length / (sceneRadius * 6f);
					colour = Vec3.Lerp( colour, bg, Smooth( 0.35f, 1f, dist ) );
				}
				hdr[i] = colour;
			}
		} );

		// --- tonemap, gamma, downsample ---------------------------------------------------------
		var outRaster = new Raster( width, height, 0 );
		Parallel.For( 0, height, y =>
		{
			for ( var x = 0; x < width; x++ )
			{
				var acc = Vec3.Zero;
				for ( var j = 0; j < ss; j++ )
					for ( var k = 0; k < ss; k++ )
						acc += Tonemap( hdr[(y * ss + j) * W + x * ss + k] * scene.Exposure );
				acc /= ss * ss;
				outRaster.Pixels[y * width + x] = Raster.Rgb( (int)(LinearToSrgb( acc.x ) * 255f + 0.5f), (int)(LinearToSrgb( acc.y ) * 255f + 0.5f), (int)(LinearToSrgb( acc.z ) * 255f + 0.5f) );
			}
		} );
		return outRaster;
	}

	/// <summary>Bilinear read of an RGBA map at a UV, to linear when <paramref name="srgb"/>.</summary>
	static Vec3 SampleMap( byte[] map, int size, Vec2 uv, bool srgb )
	{
		var fx = (uv.x - MathF.Floor( uv.x )) * size - 0.5f;
		var fy = Math.Clamp( uv.y, 0f, 1f ) * size - 0.5f;
		var x0 = (int)MathF.Floor( fx ); var y0 = (int)MathF.Floor( fy );
		var tx = fx - x0; var ty = fy - y0;
		Vec3 At( int x, int y )
		{
			x = Math.Clamp( x, 0, size - 1 ); y = Math.Clamp( y, 0, size - 1 );
			var o = (y * size + x) * 4;
			var c = new Vec3( map[o] / 255f, map[o + 1] / 255f, map[o + 2] / 255f );
			return srgb ? new Vec3( SrgbToLinear( c.x ), SrgbToLinear( c.y ), SrgbToLinear( c.z ) ) : c;
		}
		return (At( x0, y0 ) * (1f - tx) + At( x0 + 1, y0 ) * tx) * (1f - ty) + (At( x0, y0 + 1 ) * (1f - tx) + At( x0 + 1, y0 + 1 ) * tx) * ty;
	}

	/// <summary>Thin-film stand-in: a hue that walks blue → purple → green → gold as the view grazes.</summary>
	static Vec3 OilFilm( float t )
	{
		t = Math.Clamp( t, 0f, 1f ) * 3f;
		Vec3 Blue = new( 0.25f, 0.4f, 1f ), Purple = new( 0.7f, 0.25f, 0.95f ), Green = new( 0.25f, 0.95f, 0.55f ), Gold = new( 1f, 0.8f, 0.3f );
		return t < 1f ? Vec3.Lerp( Blue, Purple, t ) : t < 2f ? Vec3.Lerp( Purple, Green, t - 1f ) : Vec3.Lerp( Green, Gold, t - 2f );
	}

	static Vec3 ShadePixel( ShadeScene scene, ShadowMap[] shadows, ShadeMaterial m, Vec3 albedo, float roughness, Vec3 p, Vec3 n, Vec3 v, float ao )
	{
		var nv = MathF.Max( Vec3.Dot( n, v ), 1e-3f );
		var f0 = Vec3.Lerp( new Vec3( m.Specular, m.Specular, m.Specular ), albedo, m.Metallic );
		// The film tints what the surface reflects, never what it scatters: the base stays dark.
		var film = Vec3.One;
		if ( m.Iridescence > 0f )
		{
			var phase = 1f - nv + MathF.Abs( (p.x + p.y * 0.7f + p.z * 0.4f) * 0.05f ) % 0.35f;
			film = Vec3.Lerp( Vec3.One, OilFilm( phase ) * 1.5f, m.Iridescence );
			f0 = Mul( f0, film );
		}
		var diffuseColour = albedo * (1f - m.Metallic);
		var tint = m.SubsurfaceTint ?? Saturate( albedo, 1.1f );
		var coatF = m.Coat * Fresnel( 0.04f, nv );
		var result = Vec3.Zero;

		for ( var li = 0; li < scene.Lights.Count; li++ )
		{
			var light = scene.Lights[li];
			var l = light.Direction;
			var radiance = light.Colour * light.Intensity;
			var nl = Vec3.Dot( n, l );
			var (visible, thickness) = shadows[li] is { } sm ? sm.Sample( p, n, light.Size ) : (1f, float.MaxValue);

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
					var coatRough = MathF.Max( m.CoatRoughness, light.Size * 0.6f );
					var cs = Ggx( nh, nl, nv, coatRough ) * Fresnel( 0.04f, vh ) * m.Coat;
					lit = lit * (1f - coatF) + Mul( radiance, film ) * (cs * nl * visible);
				}
			}
			result += lit;
		}

		// Ambient: the sky from above, bounce from below, both occluded.
		var hemi = Vec3.Lerp( scene.GroundColour, scene.SkyColour, n.z * 0.5f + 0.5f );
		result += Mul( diffuseColour, hemi ) * ao;
		if ( m.Subsurface > 0f ) result += Mul( Mul( tint, albedo ), hemi ) * (0.12f * m.Subsurface * ao);
		// Reflection of the sky gradient along the mirror direction, sharper for smoother surfaces.
		var r = n * (2f * Vec3.Dot( n, v )) - v;
		var env = Vec3.Lerp( scene.GroundColour, scene.SkyColour * 1.3f, Smooth( -0.3f, 0.6f, r.z ) );
		// The studio's lights are big bright shapes in the world, and a glossy surface mirrors them: that
		// crisp white streak is most of what reads as "lacquered". Each light is a soft disc in the
		// reflection, as wide as the light, blurred by the surface's roughness.
		var glossEnv = env;
		foreach ( var light in scene.Lights )
		{
			var c = Vec3.Dot( r, light.Direction );
			var spread = light.Size * 0.5f + m.CoatRoughness * 0.5f;
			var shine = Smooth( MathF.Cos( spread * 1.8f ), MathF.Cos( spread * 0.6f ), c );
			glossEnv += light.Colour * (light.Intensity * 2.2f * shine);
		}
		var envF = Mul( Fresnel( f0, nv ), film ) * (1f - roughness) * (1f - roughness);
		result += Mul( envF, env ) * ao;
		glossEnv = Mul( glossEnv, film );
		if ( m.Coat > 0f ) result = result * (1f - coatF) + glossEnv * (MathF.Max( coatF, 0.06f ) * m.Coat * MathF.Max( ao, 0.5f ));
		return result;
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
