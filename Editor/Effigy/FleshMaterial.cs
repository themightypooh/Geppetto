using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Effigy;

/// <summary>
/// Layered "smart material" for organic surfaces: a stack of colour/roughness layers, each laid on
/// through a mask built from the surface itself (cavity, occlusion, curvature, height), from noise in
/// world space (Worley cells, fbm), or from paint, and baked to an albedo and a roughness texture on
/// the body's own UVs.
///
/// WHY LAYERS AND MASKS AND NOT ONE MORE PAINT TOOL. Every pro organic asset studied for this
/// (see materials.md in the organic-modeling skill) was textured the same way: a base colour, then
/// layers that only show where a mask says - darker and more saturated in the creases, paler on the
/// raised tops, fat or mottle in noise-shaped patches - and a roughness map riding the same masks.
/// Most of the tonal change in a zone is occlusion and cavity, and the rest is one gradient and one
/// patch of noise. That is a short list of masks and a short list of blends, which is what this is.
///
/// WORLD-SPACE NOISE, NOT UV-SPACE. Each texel knows the 3D point it covers, so a Worley cell or an
/// fbm blob is the same size on every island and runs straight across a UV seam - the thing a
/// tiling 2D texture can never do on an unwrapped creature.
///
/// MASKS READ SIGNALS THROUGH <see cref="ISurfaceSignal"/>. Cavity, AO and curvature are per-vertex
/// today (SurfaceDetail), interpolated across each texel's triangle. A per-texel bake drops in by
/// setting <see cref="FleshSurface.Ao"/> (or Cavity, Curvature) to a <see cref="TexelSignal"/> over
/// its map; every preset picks it up without changing.
/// </summary>
public static class FleshMaterial
{
	/// <summary>The presets, in the order the feature offers them. Numbers in each are the measured
	/// medians from materials.md; the section is named on each.</summary>
	public static readonly string[] Presets =
	{
		"Wet flesh", "Raw meat", "Lung", "Gut mucosa", "Bone", "Tooth", "Gum",
		"Wet gore", "Fat", "Infected skin", "Oil black", "Black spike", "Entity ribbon",
	};

	/// <summary>A preset's layer stack by name ("Raw meat", "raw_meat" and "rawmeat" all work), or null.</summary>
	public static FleshStack Preset( string name )
	{
		var key = (name ?? "").ToLowerInvariant().Replace( " ", "" ).Replace( "_", "" );

		return WithShader( key switch
		{
			"wetflesh" => WetFlesh(),
			"rawmeat" => RawMeat(),
			"lung" => Lung(),
			"gutmucosa" or "gut" or "mucosa" => GutMucosa(),
			"bone" => Bone(),
			"tooth" or "teeth" => Tooth(),
			"gum" or "gums" => Gum(),
			"wetgore" or "gore" => WetGore(),
			"fat" or "caul" => Fat(),
			"infectedskin" or "infected" or "corrupted" => InfectedSkin(),
			"oilblack" or "oil" => OilBlack(),
			"blackspike" or "spike" or "spikes" => BlackSpike(),
			"entityribbon" or "ribbon" or "ribbons" => EntityRibbon(),
			_ => null,
		} );
	}

	/// <summary>
	/// Which s&amp;box shader each preset goes out on, and how far light gets through it
	/// (realtime-flesh-shading.md §5.1). Living tissue is skin.shader, the only one with SSS and
	/// transmission. Bone and enamel stay on complex (their look is specular, not scatter); so do oil,
	/// spikes and the entity ribbon, because skin.shader has no glow and refuses see-through
	/// (F_TRANSLUCENT) alongside its SSS. Light-through is the k of §3.3: 1 is real skin; lung and fat
	/// are gel-like and pass light through three times the wall.
	/// </summary>
	static FleshStack WithShader( FleshStack stack )
	{
		if ( stack is null ) return null;

		switch ( stack.Name )
		{
			case "Bone" or "Tooth" or "Oil black" or "Black spike" or "Entity ribbon":
				stack.Shader = FleshShader.Complex;
				stack.LightThrough = 0f;
				break;
			case "Lung" or "Fat":
				stack.LightThrough = 3f;
				break;
			case "Wet flesh" or "Gut mucosa":
				stack.LightThrough = 2f;
				break;
		}

		return stack;
	}

	/// <summary>
	/// The skin.shader numbers for a stack: Facepunch's human body, with the mouth's wet second lobe
	/// on anything wet (wet tissue in s&amp;box is a broad base lobe plus a small sharp one, §4), the
	/// mouth's tighter curvature and less-blurred normal on mucosa and gum, and the light coming
	/// through in the stack's own scatter colour (fat yellow, gore red) where it has one.
	/// </summary>
	public static SkinMaterial.Params SkinParams( FleshStack stack, bool morphs = false )
	{
		var p = SkinMaterial.Body();
		p.Name = stack?.Name ?? "Body";
		p.Morphs = morphs;

		if ( stack is null ) return p;

		if ( stack.Wet >= 0.5f )
		{
			p.SecondLobeRatio = 0.263f;
			p.SecondLobeGlossiness = 0.3f;
		}

		if ( stack.Name is "Gut mucosa" or "Gum" )
		{
			p.CurvatureScale = 0.569f;
			p.DiffuseNormalBlur = 0.435f;
		}

		if ( stack.ScatterTint is { } tint )
			p.TransmissionColor = tint;

		return p;
	}

	/// <summary>
	/// The skin.shader .vmat over a flesh bake (<see cref="SkinMaterial.VmatSource"/> with
	/// <see cref="SkinParams"/>). <paramref name="aoPng"/> is what the red AO bleed tints, so pass the
	/// occlusion map; <paramref name="transmissionPng"/> the mask from a thickness bake
	/// (<see cref="SkinMaterial.TransmissionMask"/>), null for none. Glow is not carried: skin.shader
	/// has no self-illumination, which is why the glowing presets default to complex.
	/// </summary>
	public static string SkinVmatSource( string colorPng, string roughPng, FleshStack stack, string aoPng = null,
		string transmissionPng = null, bool morphs = false, string normalPng = null ) =>
		SkinMaterial.VmatSource( SkinParams( stack, morphs ), colorPng, normalPng, roughPng, aoPng, transmissionPng )
			.Replace( "by Effigy (SkinMaterial).", "by an Effigy FleshMaterial feature (skin shader). Edit the feature, not this file." );

	/// <summary>Bake <paramref name="stack"/> onto <paramref name="mesh"/>'s UVs at <paramref name="size"/>
	/// texels square. <paramref name="detail"/> scales every noise feature size (2 = blobs twice as big).</summary>
	public static FleshMaps Bake( PolyMesh mesh, FleshStack stack, int size, float detail = 1f, int seed = 1, float[] corruption = null )
	{
		if ( stack is null ) throw new ArgumentNullException( nameof( stack ) );
		var surface = new FleshSurface( mesh, size ) { Detail = detail, Seed = seed };
		if ( corruption is not null ) surface.Corruption = new VertexSignal( "corruption:field", _ => corruption );
		return stack.Render( surface );
	}

	/// <summary>
	/// Wall thickness at every vertex, in model units: a ray cast inward along −N (<see cref="MeshRaycast"/>)
	/// to the far wall. −1 where the ray finds no far wall (the open edge of a sheet, a single-sided
	/// membrane), which gets no transmission rather than a guess.
	/// </summary>
	public static float[] Thickness( PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var n = mesh.VertexCount;
		var o = new float[n];
		if ( n == 0 || mesh.FaceCount == 0 ) return o;

		var normals = SurfaceDetail.VertexNormals( mesh );
		var bvh = MeshBVH.Build( mesh );
		// Start a hair inside so the ray does not hit the faces around its own vertex.
		var eps = MathF.Max( Diagonal( mesh ) * 1e-4f, 1e-5f );

		System.Threading.Tasks.Parallel.For( 0, n, i =>
		{
			var dir = -normals[i];
			if ( dir.LengthSquared < 1e-12f ) { o[i] = -1f; return; }
			var hit = MeshRaycast.Raycast( mesh, mesh.Positions[i] + dir * eps, dir, bvh );
			o[i] = hit is { } h ? h.Distance + eps : -1f;
		} );

		return o;
	}

	/// <summary>
	/// The extra maps the skin shader wants over a flesh bake, on the body's own UVs:
	/// occlusion (white open, what the red AO bleed tints) and, when <paramref name="lightThrough"/>
	/// is above 0, the transmission mask from the wall thickness (<see cref="Thickness"/> per vertex,
	/// through skin's red transmittance at thickness × mm-per-unit ÷ k, then onto texels with
	/// <see cref="NormalBake.BakeVertexValues"/>). Texels no face covers are black in the mask.
	/// </summary>
	public static FleshShaderMaps BakeSkinMaps( PolyMesh mesh, int size, float lightThrough, float millimetresPerUnit = 25.4f )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var o = new FleshShaderMaps();
		// The body bakes onto itself: rays start a hair off the surface and land on the same face.
		var options = new BakeOptions { MaxDistance = MathF.Max( Diagonal( mesh ) * 1e-3f, 1e-4f ) };

		var ao = SurfaceDetail.AmbientOcclusion( mesh, 24, FleshSurface.AoReach( mesh ) );
		o.Ao = NormalBake.BakeVertexValues( mesh, mesh, size, size, v => { var g = Math.Clamp( 1f - ao[v], 0f, 1f ); return new Vec3( g, g, g ); }, Vec3.One, options );

		if ( lightThrough > 0f )
		{
			o.Thickness = Thickness( mesh );
			o.TransmissionPerVertex = o.Thickness.Select( t => t < 0f ? 0f
				: Math.Clamp( SkinMaterial.SkinTransmittanceRed( t * millimetresPerUnit / lightThrough ), 0f, 1f ) ).ToArray();
			var mask = o.TransmissionPerVertex;
			o.Transmission = NormalBake.BakeVertexValues( mesh, mesh, size, size, v => new Vec3( mask[v], mask[v], mask[v] ), default, options );
		}

		return o;
	}

	static float Diagonal( PolyMesh mesh )
	{
		if ( mesh.VertexCount == 0 ) return 0f;
		var min = mesh.Positions[0]; var max = min;
		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		return (max - min).Length;
	}

	/// <summary>The stack at every vertex (no UVs needed): the vertex-colour route for Blob meshes and
	/// for game meshes coloured by vertex. <paramref name="corruption"/> is per vertex, as from <see cref="Corruption"/>.</summary>
	public static FleshVertexMaterial BakeVertices( PolyMesh mesh, FleshStack stack, float detail = 1f, int seed = 1, float[] corruption = null )
	{
		if ( stack is null ) throw new ArgumentNullException( nameof( stack ) );
		var surface = new FleshSurface( mesh ) { Detail = detail, Seed = seed };
		if ( corruption is not null ) surface.Corruption = new VertexSignal( "corruption:field", _ => corruption );
		return stack.RenderVertices( surface );
	}

	/// <summary>
	/// Corruption spreading over a body from seed points (body-horror.md §3.7): geodesic distance d along
	/// the mesh's edges from the nearest seed, fingered by fbm, then c = smoothstep(R, R − W, d + noise).
	/// 1 at the seeds, 0 past <paramref name="radius"/> inches; <paramref name="front"/> is the width of the
	/// ragged edge. <paramref name="finger"/> is the noise amplitude in inches (0.1 × the body's height is
	/// the doc's number). Each <paramref name="protect"/> sphere caps c at its value inside it, so a face or
	/// a hand stays recognisably human.
	/// </summary>
	public static float[] Corruption( PolyMesh mesh, IReadOnlyList<Vec3> seeds, float radius, float front, float finger = -1f, int seed = 1,
		IReadOnlyList<(Vec3 Centre, float Radius, float Cap)> protect = null )
	{
		var n = mesh.Positions.Count;
		var dist = new float[n];
		Array.Fill( dist, float.MaxValue );
		if ( n == 0 || seeds is null || seeds.Count == 0 ) return new float[n];

		// Neighbours across each face, not only along its edges: a path may cut a quad's diagonal,
		// which keeps the edge-graph distance within a few percent of the true geodesic on a quad grid
		// (edges alone overshoot by up to ~40% where the grid runs at 45° to the path).
		var neighbours = new HashSet<int>[n];
		for ( var i = 0; i < n; i++ ) neighbours[i] = new HashSet<int>();
		foreach ( var f in mesh.Faces )
			for ( var a = 0; a < f.Count; a++ )
				for ( var b = 0; b < f.Count; b++ )
					if ( a != b ) neighbours[f.Indices[a]].Add( f.Indices[b] );
		var queue = new PriorityQueue<int, float>();

		// Each seed starts at its nearest vertex, already that far along.
		foreach ( var sp in seeds )
		{
			var best = 0; var bestD = float.MaxValue;
			for ( var i = 0; i < n; i++ )
			{
				var d = (mesh.Positions[i] - sp).LengthSquared;
				if ( d < bestD ) { bestD = d; best = i; }
			}
			var d0 = MathF.Sqrt( bestD );
			if ( d0 < dist[best] ) { dist[best] = d0; queue.Enqueue( best, d0 ); }
		}

		while ( queue.TryDequeue( out var v, out var dv ) )
		{
			if ( dv > dist[v] ) continue;
			foreach ( var o in neighbours[v] )
			{
				var nd = dv + (mesh.Positions[o] - mesh.Positions[v]).Length;
				if ( nd < dist[o] ) { dist[o] = nd; queue.Enqueue( o, nd ); }
			}
		}

		var lo = mesh.Positions.Min( p => p.z );
		var height = MathF.Max( 1e-3f, mesh.Positions.Max( p => p.z ) - lo );
		var amp = finger < 0f ? 0.1f * height : finger;
		var noise = new Noise3( seed * 977 + 13 );
		var scale = 1f / MathF.Max( 0.5f, radius * 0.35f );
		var w = MathF.Max( 1e-3f, front );
		var c = new float[n];

		for ( var i = 0; i < n; i++ )
		{
			if ( dist[i] == float.MaxValue ) continue;
			var d = dist[i] + noise.Fbm( mesh.Positions[i] * scale, 4 ) * amp;
			var t = Math.Clamp( (radius - d) / w, 0f, 1f );
			c[i] = t * t * (3f - 2f * t);

			if ( protect is null ) continue;
			foreach ( var (centre, r, cap) in protect )
			{
				var k = Math.Clamp( ((mesh.Positions[i] - centre).Length - r) / MathF.Max( 1e-3f, r * 0.25f ), 0f, 1f );
				var limit = cap + (1f - cap) * k * k * (3f - 2f * k);
				c[i] = MathF.Min( c[i], limit );
			}
		}

		return c;
	}

	/// <summary>
	/// Packs the four body-horror masks into the vertex colours (body-horror.md §4): R corruption, G
	/// wetness (creases and occlusion, only where corrupted), B veins (or whatever is passed), A occlusion.
	/// A game shader can lerp skin → bruise → raw → oil by R; the presets here read them with
	/// <see cref="Mask.VertexColour"/>.
	/// </summary>
	public static void PackMasks( PolyMesh mesh, float[] corruption, float[] veins = null )
	{
		var n = mesh.Positions.Count;
		var ao = SurfaceDetail.AmbientOcclusion( mesh, 24, MathF.Min( 6f, MathF.Max( 0.5f, mesh.BoundsDiagonal * 0.12f ) ) );
		var cav = SurfaceDetail.Cavity( mesh, 8 );
		var colours = new Vec4[n];
		for ( var i = 0; i < n; i++ )
		{
			var c = corruption is null ? 0f : Math.Clamp( corruption[i], 0f, 1f );
			var wet = Math.Clamp( ao[i] * 0.6f + Math.Clamp( cav[i] / 0.1f, 0f, 1f ) * 0.4f, 0f, 1f );
			colours[i] = new Vec4( c, Math.Clamp( 0.25f + wet * 0.75f, 0f, 1f ) * c, veins is null ? 0f : Math.Clamp( veins[i], 0f, 1f ), ao[i] );
		}
		mesh.VertexColors = colours;
	}

	// --- presets -----------------------------------------------------------------------------------

	static Vec3 Hex( string hex ) => FurMaterial.ParseHex( hex, new Vec3( 1, 0, 1 ) );
	static Vec3 Grey( float v ) => new( v, v, v );

	/// <summary>§1 pod sac: saturated red, AO+cavity to a darker, more saturated crease, milky
	/// mottles on the high domes, paler toward the top; wet domes 0.21, folds 0.32.</summary>
	static FleshStack WetFlesh() => new()
	{
		Name = "Wet flesh",
		Wet = 0.7f, Subsurface = 0.45f,
		BaseColour = Hex( "#97413f" ),
		BaseRoughness = 0.21f,
		Layers =
		{
			new FleshLayer { Name = "Paler toward the top", Blend = FleshBlend.Multiply,
				Ramp = { (0f, Grey( 0.875f )), (1f, Grey( 1.125f )) }, RampBy = Mask.Height( 0f, 1f ) },
			new FleshLayer { Name = "Milky highlights", Colour = Hex( "#ca7874" ), Roughness = 0.22f, Opacity = 0.8f,
				Mask = Mask.Fbm( 1.8f, 0.55f, 0.8f ) * Mask.Cavity( 0f, -0.15f ) * Mask.Height( 0.2f, 1f ) },
			new FleshLayer { Name = "Occlusion", Colour = Hex( "#59201b" ), Roughness = 0.32f,
				Mask = Mask.Ao( 0f, 1f ) * 0.6f + Mask.Cavity( -0.05f, 0.25f ) * 0.4f },
		},
	};

	/// <summary>§6 raw rib: lean red, thin forked fat veins (~12%), blood pooled in the creases,
	/// roughness from saturation (0.90 − 1.1·S). Paint where a fat cap goes.</summary>
	static FleshStack RawMeat() => new()
	{
		Name = "Raw meat",
		Wet = 0.6f, Subsurface = 0.4f,
		BaseColour = Hex( "#be4543" ),
		BaseRoughness = 0.13f,
		SaturationRoughness = 0.6f,
		Layers =
		{
			new FleshLayer { Name = "Pink transition", Colour = Hex( "#c68175" ), Roughness = 0.35f, Opacity = 0.5f,
				Mask = Mask.Fbm( 0.9f, 0.6f, 0.75f ) },
			new FleshLayer { Name = "Marbling", Colour = Hex( "#dcbaaa" ), Roughness = 0.58f,
				Mask = Mask.Max( Mask.Fbm( 1.5f, 0.86f, 0.92f, ridged: true ), Mask.WorleyEdges( 0.5f, 0.05f, 0f ) * Mask.Fbm( 2.5f, 0.55f, 0.7f ) ) },
			new FleshLayer { Name = "Fat cap (painted)", Colour = Hex( "#d5b8a1" ), Roughness = 0.6f, Mask = Mask.Painted() },
			new FleshLayer { Name = "Blood in the creases", Colour = Hex( "#8a130f" ), Roughness = 0.01f,
				Mask = Mask.Cavity( 0.02f, 0.1f ) * Mask.Fbm( 1.2f, 0.45f, 0.6f ) },
		},
	};

	/// <summary>§9 lung: pale salmon, raised paler, alveolar pitting, 4% dark red mottle; 0.30 flat.</summary>
	static FleshStack Lung() => new()
	{
		Name = "Lung",
		Wet = 0.3f, Subsurface = 0.5f,
		BaseColour = Hex( "#c66e74" ),
		BaseRoughness = 0.30f,
		Layers =
		{
			new FleshLayer { Name = "Raised paler", Blend = FleshBlend.Multiply, Colour = Grey( 1.2f ), Mask = Mask.Cavity( 0f, -0.05f ) },
			new FleshLayer { Name = "Alveolar pits", Blend = FleshBlend.Multiply, Colour = new Vec3( 0.84f, 0.79f, 0.79f ), Roughness = 0.33f, Opacity = 0.7f,
				Mask = Mask.WorleyEdges( 0.12f, 0.02f, 0.14f ) },
			new FleshLayer { Name = "Folds", Colour = Hex( "#8e3f43" ), Opacity = 0.6f, Mask = Mask.Ao( 0.3f, 0.8f ) },
			new FleshLayer { Name = "Dark mottle", Colour = Hex( "#650e0f" ), Mask = Mask.Fbm( 1f, 0.74f, 0.86f ) },
		},
	};

	/// <summary>§10 small intestine / colon: wet mucosa, raised paler, folds dark (the haustra are
	/// where AO really is in the albedo), soft mottle; 0.10, 0.14 in the folds.</summary>
	static FleshStack GutMucosa() => new()
	{
		Name = "Gut mucosa",
		Wet = 0.9f, Subsurface = 0.4f,
		BaseColour = Hex( "#a75050" ),
		BaseRoughness = 0.10f,
		Layers =
		{
			new FleshLayer { Name = "Raised paler", Blend = FleshBlend.Multiply, Colour = Grey( 1.25f ), Mask = Mask.Cavity( 0f, -0.08f ) },
			new FleshLayer { Name = "Mottle", Colour = Hex( "#7d3634" ), Opacity = 0.6f, Mask = Mask.Fbm( 1.5f, 0.55f, 0.75f ) },
			new FleshLayer { Name = "Mucosa grain", Blend = FleshBlend.Multiply, Colour = Grey( 0.92f ), Opacity = 0.5f, Mask = Mask.Fbm( 0.1f, 0.4f, 0.7f ) },
			new FleshLayer { Name = "Folds", Colour = Hex( "#532222" ), Roughness = 0.14f, Mask = Mask.Ao( 0.3f, 0.7f ) },
		},
	};

	/// <summary>§3 skull bone: ochre, never white, lighter toward the top, dark pits, sockets
	/// carrying a red-brown into the holes, roughness 0.31-0.51 in blotches not tied to AO.</summary>
	static FleshStack Bone() => new()
	{
		Name = "Bone",
		BaseColour = Hex( "#927c5b" ),
		BaseRoughness = 0.41f,
		Layers =
		{
			new FleshLayer { Name = "Height gradient", Ramp = { (0f, Hex( "#775e47" )), (1f, Hex( "#b9a276" )) }, RampBy = Mask.Height( 0.2f, 1f ) },
			new FleshLayer { Name = "Grime in the cavities", Colour = Hex( "#5f4137" ), Opacity = 0.6f, Mask = Mask.Cavity( 0.02f, 0.12f ) },
			new FleshLayer { Name = "Speckle", Colour = Hex( "#55352c" ), Mask = Mask.WorleyCells( 0.25f, 0.2f, 0.1f ) * Mask.Fbm( 1.5f, 0.45f, 0.6f ) },
			new FleshLayer { Name = "Sockets", Colour = Hex( "#4f2723" ), Roughness = 0.42f, Mask = Mask.Ao( 0.35f, 0.65f ) },
			new FleshLayer { Name = "Roughness blotches", ColourOff = true, Roughness = 0.31f, RoughnessTo = 0.51f, RampBy = Mask.Fbm( 1.2f, 0.2f, 0.8f ) },
		},
	};

	/// <summary>§4/§5 tooth: ivory-ochre, a dark root collar fading out over the lowest third (the
	/// tooth stood root-down), AO multiply, a few brown speckles; crown 0.2, convex crown glossier.</summary>
	static FleshStack Tooth() => new()
	{
		Name = "Tooth",
		Wet = 0.2f, Subsurface = 0.2f,
		BaseColour = Hex( "#cfa783" ),
		BaseRoughness = 0.2f,
		Layers =
		{
			new FleshLayer { Name = "Root collar", Ramp = { (0f, Hex( "#784f3d" )), (0.5f, Hex( "#b88b69" )), (1f, Hex( "#cfa783" )) },
				Roughness = 0.32f, RoughnessTo = 0.2f, RampBy = Mask.Height( 0f, 0.35f ) },
			new FleshLayer { Name = "Occlusion", Blend = FleshBlend.Multiply, Colour = Grey( 0.65f ), Mask = Mask.Ao( 0f, 1f ) },
			new FleshLayer { Name = "Stain speckle", Colour = Hex( "#986f52" ), Opacity = 0.7f,
				Mask = Mask.WorleyCells( 0.15f, 0.14f, 0.07f ) * Mask.Fbm( 1f, 0.5f, 0.7f ) },
			new FleshLayer { Name = "Glossy crown", ColourOff = true, Roughness = 0.12f, Mask = Mask.Cavity( 0f, -0.05f ) * Mask.Height( 0.5f, 0.9f ) },
		},
	};

	/// <summary>§5 gum (realistic): mid pink, ridges a touch paler, only the real pockets darken,
	/// faint petechiae; very low roughness, 0.11 in the pockets.</summary>
	static FleshStack Gum() => new()
	{
		Name = "Gum",
		Wet = 0.8f, Subsurface = 0.5f,
		BaseColour = Hex( "#985457" ),
		BaseRoughness = 0.16f,
		Layers =
		{
			new FleshLayer { Name = "Ridges paler", Blend = FleshBlend.Multiply, Colour = Grey( 1.15f ), Mask = Mask.Cavity( 0f, -0.05f ) },
			new FleshLayer { Name = "Pleats", Colour = Hex( "#79303a" ), Opacity = 0.7f, Mask = Mask.Ao( 0.4f, 0.8f ) },
			new FleshLayer { Name = "Pockets", Colour = Hex( "#87373e" ), Roughness = 0.11f, Mask = Mask.Cavity( 0.03f, 0.12f ) },
			new FleshLayer { Name = "Petechiae", Colour = Hex( "#88383f" ), Opacity = 0.5f,
				Mask = Mask.WorleyCells( 0.06f, 0.14f, 0.08f ) * Mask.Fbm( 0.5f, 0.45f, 0.65f ) },
		},
	};

	/// <summary>
	/// How a stack looks under light in <see cref="Effigy.Render.ShadedRender"/>: the baked maps (or a
	/// per-vertex bake) for colour, roughness and glow, plus the stack's wetness as a clear coat of fluid,
	/// its subsurface as wrap and glow-through, and its iridescence. A judge reads wet flesh from the crisp
	/// streak of the key light in the coat and the red glow at the thin edges, so both are on.
	/// </summary>
	public static Effigy.Render.ShadeMaterial Shade( FleshStack stack, FleshMaps maps = null, FleshVertexMaterial vertex = null )
	{
		var m = new Effigy.Render.ShadeMaterial
		{
			UseVertexColours = vertex is not null,
			Albedo = SrgbLinear( stack.BaseColour ),
			Roughness = stack.BaseRoughness,
			Metallic = stack.Metalness,
			Specular = 0.028f,
			Coat = stack.Wet,
			CoatRoughness = 0.03f + 0.05f * (1f - stack.Wet),
			Subsurface = stack.Subsurface,
			SubsurfaceTint = stack.ScatterTint is { } t ? SrgbLinear( t ) : null,
			ScatterDistance = 0.6f,
			Iridescence = stack.Iridescence,
		};
		if ( maps is not null )
		{
			m.MapSize = maps.Size;
			m.AlbedoMap = maps.Albedo;
			m.RoughnessMap = maps.Roughness;
			m.EmissionMap = maps.Emission;
		}
		if ( vertex is not null )
		{
			m.VertexRoughness = vertex.Roughness;
			m.VertexEmission = vertex.Emission;
		}
		return m;
	}

	static Vec3 SrgbLinear( Vec3 c ) => new( Effigy.Render.ShadedRender.SrgbToLinear( c.x ), Effigy.Render.ShadedRender.SrgbToLinear( c.y ), Effigy.Render.ShadedRender.SrgbToLinear( c.z ) );

	// --- body-horror families (body-horror.md §4: SWtD's two families plus the spikes) --------------

	/// <summary>Opaque wet crimson gore, what the SWtD crew turn into: raw #8a2a2a-#b0473f, darker and
	/// redder in the cavities (#3d0f12), forked fat marbling (#d8c79a, 0.55), blood pooled in the creases
	/// near-mirror; roughness 0.08-0.2 overall, a clear fluid coat, red scatter.</summary>
	static FleshStack WetGore() => new()
	{
		Name = "Wet gore",
		BaseColour = Hex( "#9c3633" ),
		BaseRoughness = 0.14f,
		Wet = 0.85f,
		Subsurface = 0.45f,
		ScatterTint = Hex( "#d0281e" ),
		Layers =
		{
			new FleshLayer { Name = "Raw range", Ramp = { (0f, Hex( "#8a2a2a" )), (1f, Hex( "#b0473f" )) }, RampBy = Mask.Fbm( 2.2f, 0.25f, 0.75f ), Opacity = 0.8f },
			new FleshLayer { Name = "Fat marbling", Colour = Hex( "#d8c79a" ), Roughness = 0.55f, Opacity = 0.75f,
				Mask = Mask.Max( Mask.Fbm( 3f, 0.93f, 0.97f, seed: 3, ridged: true ), Mask.WorleyEdges( 1.4f, 0.04f, 0f, 3 ) * Mask.Fbm( 4f, 0.68f, 0.8f, 5 ) ) },
			new FleshLayer { Name = "Cavities", Colour = Hex( "#3d0f12" ), Roughness = 0.2f,
				Mask = Mask.Ao( 0.15f, 0.8f ) * 0.7f + Mask.Cavity( 0.02f, 0.2f ) * 0.5f },
			new FleshLayer { Name = "Blood in the creases", Colour = Hex( "#5e0b0c" ), Roughness = 0.03f,
				Mask = Mask.Cavity( 0.03f, 0.12f ) * Mask.Fbm( 1f, 0.4f, 0.6f, 7 ) },
		},
	};

	/// <summary>Fat and caul: #d8c79a to #e6d9b8, lobules by Worley cells, pinker veins in the walls,
	/// translucent; roughness 0.55-0.65 (materials.md rule 5).</summary>
	static FleshStack Fat() => new()
	{
		Name = "Fat",
		BaseColour = Hex( "#dfd0a9" ),
		BaseRoughness = 0.6f,
		Wet = 0.35f,
		Subsurface = 0.7f,
		ScatterTint = Hex( "#e8b070" ),
		Layers =
		{
			new FleshLayer { Name = "Lobules", Ramp = { (0f, Hex( "#e6d9b8" )), (1f, Hex( "#d8c79a" )) }, RampBy = Mask.WorleyCells( 0.6f, 0.05f, 0.55f ) },
			new FleshLayer { Name = "Vessels in the walls", Colour = Hex( "#b9655a" ), Roughness = 0.45f, Opacity = 0.55f,
				Mask = Mask.WorleyEdges( 0.6f, 0.04f, 0f ) * Mask.Fbm( 2f, 0.45f, 0.65f, 2 ) },
			new FleshLayer { Name = "Creases", Colour = Hex( "#a88a5c" ), Roughness = 0.5f, Opacity = 0.7f, Mask = Mask.Ao( 0.25f, 0.8f ) },
		},
	};

	/// <summary>
	/// A human skin being turned, driven by corruption (<see cref="Mask.Corruption"/>: vertex colour R
	/// or a <see cref="Corruption"/> field): skin, then bruise (#6b4f63 / #8e8a4f mottled) from 0.3 to
	/// 0.6, then raw (#8a2a2a-#b0473f, #3d0f12 in the cavities) past 0.7, then oil-black at the source.
	/// Thresholds are broken by noise; wetness rides corruption × the creases; roughness falls from dry
	/// skin 0.5 to wet flesh 0.15 to oil 0.05.
	/// </summary>
	static FleshStack InfectedSkin() => new()
	{
		Name = "Infected skin",
		BaseColour = Hex( "#c0907c" ),
		BaseRoughness = 0.5f,
		Wet = 0.6f,
		Subsurface = 0.4f,
		ScatterTint = Hex( "#c8392b" ),
		Layers =
		{
			new FleshLayer { Name = "Skin occlusion", Blend = FleshBlend.Multiply, Colour = Grey( 0.75f ), Mask = Mask.Ao( 0.2f, 0.9f ) },
			new FleshLayer { Name = "Bruise", Ramp = { (0f, Hex( "#6b4f63" )), (1f, Hex( "#8e8a4f" )) }, RampBy = Mask.Fbm( 1.5f, 0.3f, 0.7f, 11 ),
				Roughness = 0.42f, Mask = Mask.Corruption( 0.2f, 0.45f ) * Mask.Fbm( 2.5f, 0.3f, 0.55f, 12 ) + Mask.Corruption( 0.4f, 0.55f ) },
			new FleshLayer { Name = "Raw", Ramp = { (0f, Hex( "#8a2a2a" )), (1f, Hex( "#b0473f" )) }, RampBy = Mask.Fbm( 2f, 0.3f, 0.7f, 13 ),
				Roughness = 0.15f, Mask = Mask.Corruption( 0.6f, 0.8f ) * Mask.Fbm( 2f, 0.25f, 0.5f, 14 ) + Mask.Corruption( 0.75f, 0.85f ) },
			new FleshLayer { Name = "Raw cavities", Colour = Hex( "#3d0f12" ), Roughness = 0.12f,
				Mask = Mask.Corruption( 0.65f, 0.85f ) * (Mask.Ao( 0.2f, 0.8f ) * 0.7f + Mask.Cavity( 0.02f, 0.15f ) * 0.5f) },
			new FleshLayer { Name = "Wet in the creases", ColourOff = true, Roughness = 0.08f,
				Mask = Mask.Corruption( 0.3f, 0.8f ) * Mask.Max( Mask.Cavity( 0.01f, 0.08f ), Mask.VertexColour( 1, 0.3f, 0.9f ) ) },
			new FleshLayer { Name = "Oil at the source", Colour = Hex( "#15121a" ), Roughness = 0.05f, Mask = Mask.Corruption( 0.88f, 0.96f ) },
			new FleshLayer { Name = "Veins (B)", Colour = Hex( "#4b1c3a" ), Roughness = 0.2f, Opacity = 0.8f, Mask = Mask.VertexColour( 2, 0.2f, 0.7f ) },
		},
	};

	/// <summary>SWtD oil-black: #0b0a0d-#15121a, roughness 0.05, an oil-film iridescence on the grazing
	/// reflections, purple-red showing through in the cavities.</summary>
	static FleshStack OilBlack() => new()
	{
		Name = "Oil black",
		BaseColour = Hex( "#110f15" ),
		BaseRoughness = 0.05f,
		Wet = 1f,
		Iridescence = 0.8f,
		Layers =
		{
			new FleshLayer { Name = "Slick variation", Ramp = { (0f, Hex( "#0b0a0d" )), (1f, Hex( "#15121a" )) }, RampBy = Mask.Fbm( 2f, 0.3f, 0.7f ) },
			new FleshLayer { Name = "Flesh beneath", Colour = Hex( "#4a1526" ), Roughness = 0.1f, Opacity = 0.7f,
				Mask = Mask.Cavity( 0.02f, 0.15f ) * Mask.Fbm( 1.5f, 0.4f, 0.6f, 4 ) },
		},
	};

	/// <summary>Black rigid spikes: near-black, glossy (0.1), a dull grime in the cavities and bead
	/// joints, slightly lighter toward the tips.</summary>
	static FleshStack BlackSpike() => new()
	{
		Name = "Black spike",
		BaseColour = Hex( "#0e0c0d" ),
		BaseRoughness = 0.1f,
		Wet = 0.5f,
		Iridescence = 0.2f,
		Layers =
		{
			new FleshLayer { Name = "Tip sheen", Blend = FleshBlend.Multiply, Ramp = { (0f, Grey( 0.9f )), (1f, Grey( 1.6f )) }, RampBy = Mask.Height( 0f, 1f ) },
			new FleshLayer { Name = "Grime", Colour = Hex( "#2a2018" ), Roughness = 0.45f, Opacity = 0.8f, Mask = Mask.Cavity( 0.01f, 0.08f ) },
		},
	};

	/// <summary>The SWtD entity's ribbons: dark membrane, translucent, an oil-film iridescence, and
	/// subtle red and blue bioluminescence along the vein network and the midribs only (0.5-3 nits:
	/// emission strength ~0.35). Ripples by fbm.</summary>
	static FleshStack EntityRibbon() => new()
	{
		Name = "Entity ribbon",
		BaseColour = Hex( "#2a1a2a" ),
		BaseRoughness = 0.08f,
		Wet = 0.9f,
		Subsurface = 0.8f,
		Translucency = 0.45f,
		Iridescence = 1f,
		ScatterTint = Hex( "#c03050" ),
		Layers =
		{
			new FleshLayer { Name = "Membrane variation", Ramp = { (0f, Hex( "#1d1222" )), (0.6f, Hex( "#3b1d33" )), (1f, Hex( "#5a2438" )) }, RampBy = Mask.Fbm( 3f, 0.25f, 0.75f ) },
			new FleshLayer { Name = "Red veins", Colour = Hex( "#e03a2a" ), Roughness = 0.1f, Emission = 0.35f,
				Mask = Mask.WorleyEdges( 1.2f, 0.06f, 0f, 21 ) * Mask.Fbm( 4f, 0.45f, 0.6f, 22 ) },
			new FleshLayer { Name = "Blue growth", Colour = Hex( "#3a64e0" ), Roughness = 0.1f, Emission = 0.3f, Opacity = 0.8f,
				Mask = Mask.WorleyCells( 0.5f, 0.12f, 0.04f, 23 ) * Mask.Fbm( 2.5f, 0.6f, 0.72f, 24 ) },
		},
	};

	// --- output ------------------------------------------------------------------------------------

	/// <summary>
	/// The Complex .vmat over the two maps. Same Layer0 form and parameter names as
	/// <see cref="PaintMaterial.VmatSource"/> and <see cref="FabricMaterial.VmatSource"/>; the
	/// normal stays the engine default because this layer stack makes no height.
	/// </summary>
	public static string VmatSource( string colorPng, string roughPng, float metalness = 0f ) => VmatSource( colorPng, roughPng, null, metalness );

	/// <summary>
	/// The .vmat with the stack's light behaviour as well, in complex.shader_c's own names (read off the
	/// compiled shader's strings): a glow map through F_SELF_ILLUM/TextureSelfIllumMask, see-through
	/// membranes through F_TRANSLUCENT/g_flOpacityScale, and subsurface faked the way body-horror.md §4
	/// says to when there is no SSS: F_DIFFUSE_WRAP with a scatter-coloured wrap. Wet and iridescence have
	/// no Complex parameter; they live in the low roughness and in the render.
	/// </summary>
	public static string VmatSource( string colorPng, string roughPng, FleshStack stack, float metalness = 0f, string emissionPng = null )
	{
		static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );
		var extra = new System.Text.StringBuilder();
		if ( emissionPng is not null )
		{
			extra.Append( "\n\t//---- Self Illum ----\n\tF_SELF_ILLUM 1\n" );
			extra.Append( $"\tTextureSelfIllumMask \"{emissionPng}\"\n\tg_flSelfIllumBrightness \"{F( 1f )}\"\n\tg_flSelfIllumAlbedoFactor \"{F( 1f )}\"\n" );
		}
		if ( stack is { Translucency: > 0f } )
			extra.Append( $"\n\t//---- Translucent ----\n\tF_TRANSLUCENT 1\n\tg_flOpacityScale \"{F( Math.Clamp( 1f - stack.Translucency, 0.05f, 1f ) )}\"\n" );
		if ( stack is { Subsurface: > 0f } )
		{
			var tint = stack.ScatterTint ?? stack.BaseColour;
			extra.Append( $"\n\t//---- Diffuse wrap (subsurface stand-in) ----\n\tF_DIFFUSE_WRAP 1\n\tg_flDiffuseWrap \"{F( Math.Clamp( stack.Subsurface * 0.5f, 0f, 0.5f ) )}\"\n" );
			extra.Append( $"\tg_vDiffuseWrapColor \"[{F( tint.x )} {F( tint.y )} {F( tint.z )} 0.000]\"\n" );
		}
		return VmatCore( colorPng, roughPng, metalness, extra.ToString() );
	}

	static string VmatCore( string colorPng, string roughPng, float metalness, string extra )
	{
		static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

		return
			"// THIS FILE IS AUTO-GENERATED by an Effigy FleshMaterial feature. Edit the feature, not this file.\n" +
			"Layer0\n" +
			"{\n" +
			"\tshader \"complex.vfx\"\n" +
			"\n" +
			"\t//---- Color ----\n" +
			"\tg_flModelTintAmount \"1.000\"\n" +
			"\tg_vColorTint \"[1.000000 1.000000 1.000000 0.000000]\"\n" +
			$"\tTextureColor \"{colorPng}\"\n" +
			"\n" +
			"\t//---- Metalness ----\n" +
			$"\tg_flMetalness \"{F( metalness )}\"\n" +
			"\n" +
			"\t//---- Normal ----\n" +
			"\tTextureNormal \"materials/default/default_normal.tga\"\n" +
			"\n" +
			"\t//---- Roughness ----\n" +
			$"\tTextureRoughness \"{roughPng}\"\n" +
			$"\tg_flRoughnessScaleFactor \"{F( 1f )}\"\n" +
			"\n" +
			"\t//---- Texture Coordinates ----\n" +
			"\tg_nScaleTexCoordUByModelScaleAxis \"0\"\n" +
			"\tg_nScaleTexCoordVByModelScaleAxis \"0\"\n" +
			"\tg_vTexCoordOffset \"[0.000 0.000]\"\n" +
			"\tg_vTexCoordScale \"[1.000 1.000]\"\n" +
			"\tg_vTexCoordScrollSpeed \"[0.000 0.000]\"\n" +
			extra +
			"}\n";
	}
}

/// <summary>What a bake hands back: RGBA albedo, RGBA grey roughness, both <see cref="Size"/>
/// square, row 0 at the top, and which texels a face actually covered.</summary>
public sealed class FleshMaps
{
	public int Size;
	public byte[] Albedo;
	public byte[] Roughness;
	/// <summary>RGBA glow, or null when no layer emits.</summary>
	public byte[] Emission;
	public bool[] Covered;

	/// <summary>The albedo as a paint canvas, so the editor can show it the way it shows paint.</summary>
	public PaintCanvas ToCanvas()
	{
		var canvas = new PaintCanvas( Size, Size );
		Array.Copy( Albedo, canvas.Rgba, Albedo.Length );
		canvas.Invalidate();
		return canvas;
	}
}

/// <summary>The skin shader's extra maps for a flesh bake (<see cref="FleshMaterial.BakeSkinMaps"/>).</summary>
public sealed class FleshShaderMaps
{
	/// <summary>Occlusion, white open to black shut in.</summary>
	public BakedMap Ao;
	/// <summary>Where light comes through, white most. Null when nothing transmits.</summary>
	public BakedMap Transmission;
	/// <summary>Wall thickness per vertex in model units, −1 where no far wall was found. Null with no transmission.</summary>
	public float[] Thickness;
	/// <summary>The mask per vertex, 0..1.</summary>
	public float[] TransmissionPerVertex;
}

/// <summary>What a per-vertex bake hands back, one entry per mesh vertex: colour (sRGB 0..1, ready
/// for <see cref="PolyMesh.VertexColors"/>), roughness, and glow (null when no layer emits).</summary>
public sealed class FleshVertexMaterial
{
	public Vec4[] Colour;
	public float[] Roughness;
	public Vec3[] Emission;
}

/// <summary>The s&amp;box shader a flesh .vmat is written for.</summary>
public enum FleshShader
{
	/// <summary>skin.shader: curvature SSS, red AO bleed, second wet lobe, transmission through thin parts.</summary>
	Skin,
	/// <summary>complex.shader: plain PBR with a wrap-light stand-in for SSS; carries glow and see-through.</summary>
	Complex,
}

public enum FleshBlend
{
	/// <summary>Replace, at the mask's coverage.</summary>
	Normal,
	/// <summary>Base times colour: a grey above 1 lightens, below 1 darkens. Roughness multiplies too.</summary>
	Multiply,
	Screen,
	Overlay,
	/// <summary>Colour added. Roughness adds as a signed offset.</summary>
	Add,
}

/// <summary>
/// One layer: a colour (or a ramp along another mask), a roughness, the mask that says where, and
/// how it combines with what is under it.
/// </summary>
public sealed class FleshLayer
{
	public string Name;
	public Vec3 Colour = new( 1, 1, 1 );

	/// <summary>Colour stops 0..1. When set, the layer's colour at a texel is the ramp at
	/// <see cref="RampBy"/>'s value there instead of <see cref="Colour"/>.</summary>
	public List<(float At, Vec3 Colour)> Ramp = new();

	/// <summary>What drives the ramp and <see cref="RoughnessTo"/>. Null drives them by the layer's own mask.</summary>
	public Mask RampBy;

	/// <summary>Null leaves roughness alone.</summary>
	public float? Roughness;

	/// <summary>With <see cref="Roughness"/>: roughness runs from that to this along <see cref="RampBy"/>.</summary>
	public float? RoughnessTo;

	/// <summary>A roughness-only layer.</summary>
	public bool ColourOff;

	public Mask Mask = Mask.All;
	public FleshBlend Blend = FleshBlend.Normal;
	public float Opacity = 1f;

	/// <summary>A tileable picture laid on triplanar (projected down x, y and z, blended by the normal),
	/// so it needs no UVs and runs across seams. When set, the layer's colour is the picture times
	/// <see cref="Colour"/>; the ramp is not used.</summary>
	public TriplanarImage Image;

	/// <summary>How many inches one repeat of <see cref="Image"/> covers.</summary>
	public float ImageTile = 12f;

	/// <summary>0..1: the layer also glows its colour where its mask is (veins, midribs, growth tips).</summary>
	public float Emission;

	internal Vec3 ColourAt( float t )
	{
		if ( Ramp.Count == 0 ) return Colour;
		if ( t <= Ramp[0].At ) return Ramp[0].Colour;

		for ( var i = 1; i < Ramp.Count; i++ )
		{
			if ( t > Ramp[i].At ) continue;
			var (a, ca) = Ramp[i - 1];
			var (b, cb) = Ramp[i];
			var k = b - a < 1e-6f ? 1f : (t - a) / (b - a);
			return ca + (cb - ca) * k;
		}

		return Ramp[^1].Colour;
	}
}

/// <summary>A base colour and roughness with layers over them, bottom first.</summary>
public sealed class FleshStack
{
	public string Name;
	public Vec3 BaseColour = new( 0.6f, 0.3f, 0.3f );
	public float BaseRoughness = 0.3f;
	public float Metalness;

	/// <summary>
	/// 0..1: how far the final roughness is pulled toward 0.90 − 1.1·S of the final albedo (materials.md
	/// rule 6, fitted on the raw rib at r = 0.87). One rule that keeps lean, fat and blood consistent
	/// wherever the colour layers put them.
	/// </summary>
	public float SaturationRoughness;

	public List<FleshLayer> Layers = new();

	/// <summary>
	/// How the surface takes light beyond albedo and roughness, for the render and the .vmat. Wet: a
	/// clear coat of fluid over everything, 0..1. Subsurface: light wrapping and glowing through, 0..1
	/// (flesh, fat, membranes). Translucency: how much the eye sees through it, 0..1 (ribbons and
	/// membranes only; opaque gore is 0). Iridescence: the oil-film hue shift on grazing reflections,
	/// 0..1 (SWtD oil; not metal).
	/// </summary>
	public float Wet, Subsurface, Translucency, Iridescence;

	/// <summary>The colour light picks up inside (null: the albedo, saturated). Flesh scatters red.</summary>
	public Vec3? ScatterTint;

	/// <summary>Which s&amp;box shader the .vmat is written for. Skin (SSS + transmission) unless the
	/// preset says otherwise; see FleshMaterial.WithShader.</summary>
	public FleshShader Shader = FleshShader.Skin;

	/// <summary>How far light gets through thin parts on the skin shader: the k of
	/// realtime-flesh-shading.md §3.3 (1 = real skin, 3–10 gel-like flesh). 0 writes no transmission map.</summary>
	public float LightThrough = 1f;

	public FleshMaps Render( FleshSurface s )
	{
		if ( s.PerVertex ) throw new ArgumentException( "A per-vertex surface bakes with RenderVertices, not to maps.", nameof( s ) );
		var n = s.Size * s.Size;
		var (colour, rough, emit) = Evaluate( s );

		Pad( s, colour, rough, emit );

		var maps = new FleshMaps { Size = s.Size, Albedo = new byte[n * 4], Roughness = new byte[n * 4], Covered = s.Covered };
		if ( emit is not null ) maps.Emission = new byte[n * 4];

		for ( var t = 0; t < n; t++ )
		{
			maps.Albedo[t * 4] = Byte( colour[t].x );
			maps.Albedo[t * 4 + 1] = Byte( colour[t].y );
			maps.Albedo[t * 4 + 2] = Byte( colour[t].z );
			maps.Albedo[t * 4 + 3] = 255;
			var g = Byte( rough[t] );
			maps.Roughness[t * 4] = g;
			maps.Roughness[t * 4 + 1] = g;
			maps.Roughness[t * 4 + 2] = g;
			maps.Roughness[t * 4 + 3] = 255;

			if ( emit is not null )
			{
				maps.Emission[t * 4] = Byte( emit[t].x );
				maps.Emission[t * 4 + 1] = Byte( emit[t].y );
				maps.Emission[t * 4 + 2] = Byte( emit[t].z );
				maps.Emission[t * 4 + 3] = 255;
			}
		}

		return maps;
	}

	/// <summary>
	/// The same stack evaluated at every vertex instead of every texel: for meshes with no UVs (a Blob
	/// straight out of SurfaceNets) or for a game mesh coloured by vertex. Noise is in world space either
	/// way, so the pattern is the one a texture bake would give, at vertex resolution.
	/// </summary>
	public FleshVertexMaterial RenderVertices( FleshSurface s )
	{
		if ( !s.PerVertex ) throw new ArgumentException( "RenderVertices needs a per-vertex surface: new FleshSurface( mesh ).", nameof( s ) );
		var (colour, rough, emit) = Evaluate( s );
		return new FleshVertexMaterial
		{
			Colour = colour.Select( c => new Vec4( c.x, c.y, c.z, 1f ) ).ToArray(),
			Roughness = rough,
			Emission = emit,
		};
	}

	(Vec3[] Colour, float[] Rough, Vec3[] Emit) Evaluate( FleshSurface s )
	{
		var n = s.Count;
		var masks = Layers.Select( l => l.Mask.Evaluate( s ) ).ToArray();
		var ramps = Layers.Select( l => l.Ramp.Count > 0 || l.RoughnessTo.HasValue ? (l.RampBy ?? l.Mask).Evaluate( s ) : null ).ToArray();
		var images = Layers.Select( l => l.Image is { } img ? s.Triplanar( img, l.ImageTile ) : null ).ToArray();
		var colour = new Vec3[n];
		var rough = new float[n];
		var emit = Layers.Any( l => l.Emission > 0f ) ? new Vec3[n] : null;

		Parallel.For( 0, n, t =>
		{
			{
				if ( !s.Covered[t] ) return;
				var c = BaseColour;
				var r = BaseRoughness;
				var e = Vec3.Zero;

				for ( var li = 0; li < Layers.Count; li++ )
				{
					var layer = Layers[li];
					var m = Math.Clamp( masks[li][t], 0f, 1f ) * layer.Opacity;
					if ( m <= 0f ) continue;
					var k = ramps[li] is { } rk ? Math.Clamp( rk[t], 0f, 1f ) : 0f;
					var lc = images[li] is { } im ? new Vec3( im[t].x * layer.Colour.x, im[t].y * layer.Colour.y, im[t].z * layer.Colour.z ) : layer.ColourAt( k );

					if ( layer.Emission > 0f ) e += lc * (layer.Emission * m);

					if ( !layer.ColourOff )
					{
						var blended = layer.Blend switch
						{
							FleshBlend.Multiply => new Vec3( c.x * lc.x, c.y * lc.y, c.z * lc.z ),
							FleshBlend.Screen => new Vec3( Screen( c.x, lc.x ), Screen( c.y, lc.y ), Screen( c.z, lc.z ) ),
							FleshBlend.Overlay => new Vec3( Overlay( c.x, lc.x ), Overlay( c.y, lc.y ), Overlay( c.z, lc.z ) ),
							FleshBlend.Add => c + lc,
							_ => lc,
						};
						c = c + (blended - c) * m;
					}

					if ( layer.Roughness is { } lr )
					{
						if ( layer.RoughnessTo is { } to ) lr += (to - lr) * k;
						r = layer.Blend switch
						{
							FleshBlend.Multiply => r * (1f + (lr - 1f) * m),
							FleshBlend.Add => r + lr * m,
							_ => r + (lr - r) * m,
						};
					}
				}

				c = new Vec3( Math.Clamp( c.x, 0f, 1f ), Math.Clamp( c.y, 0f, 1f ), Math.Clamp( c.z, 0f, 1f ) );

				if ( SaturationRoughness > 0f )
				{
					var max = MathF.Max( c.x, MathF.Max( c.y, c.z ) );
					var sat = max <= 1e-5f ? 0f : (max - MathF.Min( c.x, MathF.Min( c.y, c.z ) )) / max;
					r += (Math.Clamp( 0.90f - 1.1f * sat, 0f, 1f ) - r) * SaturationRoughness;
				}

				colour[t] = c;
				rough[t] = Math.Clamp( r, 0f, 1f );
				if ( emit is not null ) emit[t] = new Vec3( Math.Clamp( e.x, 0f, 1f ), Math.Clamp( e.y, 0f, 1f ), Math.Clamp( e.z, 0f, 1f ) );
			}
		} );

		return (colour, rough, emit);
	}

	/// <summary>Bleed covered texels into the gutter so filtering at an island's edge finds colour,
	/// then fill whatever is left with the stack's base, never black.</summary>
	void Pad( FleshSurface s, Vec3[] colour, float[] rough, Vec3[] emit )
	{
		var size = s.Size;
		var filled = (bool[])s.Covered.Clone();

		for ( var pass = 0; pass < 8; pass++ )
		{
			var added = new List<(int T, Vec3 C, float R, Vec3 E)>();

			for ( var y = 0; y < size; y++ )
				for ( var x = 0; x < size; x++ )
				{
					var t = y * size + x;
					if ( filled[t] ) continue;
					var c = Vec3.Zero; var r = 0f; var e = Vec3.Zero; var k = 0;
					for ( var dy = -1; dy <= 1; dy++ )
						for ( var dx = -1; dx <= 1; dx++ )
						{
							int nx = x + dx, ny = y + dy;
							if ( nx < 0 || ny < 0 || nx >= size || ny >= size ) continue;
							var o = ny * size + nx;
							if ( !filled[o] ) continue;
							c += colour[o]; r += rough[o]; if ( emit is not null ) e += emit[o]; k++;
						}
					if ( k > 0 ) added.Add( (t, c / k, r / k, e / k) );
				}

			if ( added.Count == 0 ) break;
			foreach ( var (t, c, r, e) in added ) { colour[t] = c; rough[t] = r; if ( emit is not null ) emit[t] = e; filled[t] = true; }
		}

		for ( var t = 0; t < filled.Length; t++ )
			if ( !filled[t] ) { colour[t] = BaseColour; rough[t] = BaseRoughness; }
	}

	static float Screen( float a, float b ) => 1f - (1f - a) * (1f - b);
	static float Overlay( float a, float b ) => a < 0.5f ? 2f * a * b : 1f - 2f * (1f - a) * (1f - b);
	static byte Byte( float v ) => (byte)Math.Clamp( (int)MathF.Round( v * 255f ), 0, 255 );
}

/// <summary>A scalar over the surface, one value per texel. The plug point for bakers: per-vertex
/// signals interpolate, a per-texel map is read straight.</summary>
public interface ISurfaceSignal
{
	/// <summary>Identifies the signal for caching within one bake.</summary>
	string Key { get; }

	float[] Texels( FleshSurface surface );
}

/// <summary>A per-vertex value, interpolated across each texel's triangle.</summary>
public sealed class VertexSignal : ISurfaceSignal
{
	readonly Func<PolyMesh, float[]> _values;

	public VertexSignal( string key, Func<PolyMesh, float[]> values )
	{
		Key = key;
		_values = values;
	}

	public string Key { get; }

	public float[] Texels( FleshSurface s ) => s.FromVertices( _values( s.Mesh ) );
}

/// <summary>
/// A map already baked on the same UVs (a per-texel AO or curvature bake), read from its red
/// channel, <paramref name="lo"/> at 0 and <paramref name="hi"/> at 255. Resampled nearest when
/// its size differs from the bake's.
/// </summary>
public sealed class TexelSignal : ISurfaceSignal
{
	readonly BakedMap _map;
	readonly float _lo, _hi;

	public TexelSignal( string key, BakedMap map, float lo = 0f, float hi = 1f )
	{
		Key = key;
		_map = map ?? throw new ArgumentNullException( nameof( map ) );
		_lo = lo;
		_hi = hi;
	}

	public string Key { get; }

	public float[] Texels( FleshSurface s ) => s.PerUv( ( u, v ) =>
	{
		var mx = Math.Clamp( (int)(u * _map.Width), 0, _map.Width - 1 );
		var my = Math.Clamp( (int)(v * _map.Height), 0, _map.Height - 1 );
		return _lo + (_hi - _lo) * (_map.Rgb[(my * _map.Width + mx) * 3] / 255f);
	} );
}

/// <summary>
/// The mesh rasterised into its UV square once: for each texel, the triangle covering it and where
/// in it. Every signal and mask reads through this, and caches its result here for the bake.
/// </summary>
public sealed class FleshSurface
{
	public readonly PolyMesh Mesh;
	/// <summary>Texels across the map; 0 on a per-vertex surface.</summary>
	public readonly int Size;
	public readonly bool[] Covered;
	public readonly int CoveredCount;

	/// <summary>True when each sample is a vertex, not a texel (see the one-argument constructor).</summary>
	public readonly bool PerVertex;

	/// <summary>How many samples: texels (Size²) or vertices.</summary>
	public int Count => PerVertex ? Mesh.Positions.Count : Size * Size;

	/// <summary>Body-horror corruption, 0 untouched to 1 fully turned (body-horror.md §3.7). Defaults to
	/// the red channel of the vertex colours, the packed-mask convention; set it to a
	/// <see cref="FleshMaterial.Corruption"/> field to grow it from seed points instead.</summary>
	public ISurfaceSignal Corruption = new VertexSignal( "corruption:vc0", m => VertexChannel( m, 0 ) );

	/// <summary>Multiplies every noise feature size.</summary>
	public float Detail = 1f;
	public int Seed = 1;

	/// <summary>Cavity in inches, + in creases (SurfaceDetail.Cavity). Swap in a per-texel bake here.</summary>
	public ISurfaceSignal Cavity = new VertexSignal( "cavity", m => SurfaceDetail.Cavity( m, 8 ) );

	/// <summary>Ambient occlusion, 0 open to 1 shut in (SurfaceDetail.AmbientOcclusion). Swap in a per-texel bake here.</summary>
	public ISurfaceSignal Ao = new VertexSignal( "ao", m => SurfaceDetail.AmbientOcclusion( m, 24, AoReach( m ) ) );

	/// <summary>Convexity per edge length, + on ridges, − in creases: a short-range cavity, flipped,
	/// over the mean edge so it means the same at any density. Swap in a per-texel bake here.</summary>
	public ISurfaceSignal Curvature = new VertexSignal( "curvature", Convexity );

	/// <summary>Height, 0 at the body's lowest point and 1 at its highest.</summary>
	public ISurfaceSignal Height = new VertexSignal( "height", m =>
	{
		var lo = m.Positions.Min( p => p.z );
		var span = MathF.Max( 1e-6f, m.Positions.Max( p => p.z ) - lo );
		return m.Positions.Select( p => (p.z - lo) / span ).ToArray();
	} );

	// Per texel: the fan triangle covering it and two barycentrics (the third is 1 − both).
	readonly int[] _tri;
	readonly float[] _wa, _wb;
	readonly List<(int A, int B, int C)> _tris = new();
	readonly Dictionary<string, float[]> _cache = new();

	/// <summary>A per-vertex surface: every mask and layer is evaluated at the mesh's vertices. Needs
	/// no UVs.</summary>
	public FleshSurface( PolyMesh mesh )
	{
		Mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		PerVertex = true;
		Covered = Enumerable.Repeat( true, mesh.Positions.Count ).ToArray();
		CoveredCount = Covered.Length;
	}

	public FleshSurface( PolyMesh mesh, int size )
	{
		Mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		if ( size < 4 ) throw new ArgumentOutOfRangeException( nameof( size ), "A map needs at least 4 texels across." );
		Size = size;
		Covered = new bool[size * size];
		_tri = new int[size * size];
		_wa = new float[size * size];
		_wb = new float[size * size];

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count ) continue;
			var corners = new List<Vec3>( face.Count );
			for ( var c = 0; c < face.Count; c++ ) corners.Add( mesh.Positions[face.Indices[c]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var id = _tris.Count;
				_tris.Add( (face.Indices[ia], face.Indices[ib], face.Indices[ic]) );
				NormalBake.Rasterise( face.UVs[ia], face.UVs[ib], face.UVs[ic], size, size, ( x, y, wa, wb, wc ) =>
				{
					var t = y * size + x;
					Covered[t] = true;
					_tri[t] = id;
					_wa[t] = wa;
					_wb[t] = wb;
				} );
			}
		}

		CoveredCount = Covered.Count( c => c );
	}

	/// <summary>The 3D point a texel covers (zero where nothing does).</summary>
	public Vec3 PositionAt( int texel )
	{
		if ( PerVertex ) return Mesh.Positions[texel];
		if ( !Covered[texel] ) return Vec3.Zero;
		var (a, b, c) = _tris[_tri[texel]];
		var wa = _wa[texel]; var wb = _wb[texel];
		return Mesh.Positions[a] * wa + Mesh.Positions[b] * wb + Mesh.Positions[c] * (1f - wa - wb);
	}

	/// <summary>A per-vertex array interpolated to every covered texel.</summary>
	public float[] FromVertices( float[] values )
	{
		if ( PerVertex ) return (float[])values.Clone();
		var o = new float[Size * Size];
		for ( var t = 0; t < o.Length; t++ )
		{
			if ( !Covered[t] ) continue;
			var (a, b, c) = _tris[_tri[t]];
			var wa = _wa[t]; var wb = _wb[t];
			o[t] = values[a] * wa + values[b] * wb + values[c] * (1f - wa - wb);
		}
		return o;
	}

	/// <summary>A signal's texels, computed once per bake.</summary>
	public float[] Read( ISurfaceSignal signal ) => Cached( "signal:" + signal.Key, () => signal.Texels( this ) );

	internal float[] Cached( string key, Func<float[]> make )
	{
		lock ( _cache )
		{
			if ( _cache.TryGetValue( key, out var hit ) ) return hit;
		}
		var made = make();
		lock ( _cache ) _cache[key] = made;
		return made;
	}

	/// <summary>Per texel, in parallel, over covered texels only.</summary>
	internal float[] PerTexel( Func<Vec3, float> f )
	{
		var o = new float[Count];
		Parallel.For( 0, Count, t =>
		{
			if ( Covered[t] ) o[t] = f( PositionAt( t ) );
		} );
		return o;
	}

	/// <summary>The smooth surface normal at a sample (vertex normals, interpolated across a texel's triangle).</summary>
	public Vec3 NormalAt( int t )
	{
		var n = _normals ??= Mesh.ComputeVertexNormals();
		if ( PerVertex ) return n[t];
		if ( !Covered[t] ) return new Vec3( 0, 0, 1 );
		var (a, b, c) = _tris[_tri[t]];
		var wa = _wa[t]; var wb = _wb[t];
		return (n[a] * wa + n[b] * wb + n[c] * (1f - wa - wb)).Normal;
	}

	Vec3[] _normals;

	/// <summary>A picture sampled triplanar at every sample, cached per bake.</summary>
	internal Vec3[] Triplanar( TriplanarImage image, float tile )
	{
		var key = $"tri:{image.Key}:{tile.ToString( CultureInfo.InvariantCulture )}";
		lock ( _images )
		{
			if ( _images.TryGetValue( key, out var hit ) ) return hit;
		}
		var o = new Vec3[Count];
		Parallel.For( 0, Count, t =>
		{
			if ( Covered[t] ) o[t] = image.Sample( PositionAt( t ), NormalAt( t ), tile * Detail );
		} );
		lock ( _images ) _images[key] = o;
		return o;
	}

	readonly Dictionary<string, Vec3[]> _images = new();

	/// <summary>
	/// A value read from a UV-space picture at every sample: at the texel itself, or on a per-vertex
	/// surface at the vertex's UV (0 where the vertex has none). <paramref name="sample"/> gets u, v in 0..1.
	/// </summary>
	internal float[] PerUv( Func<float, float, float> sample )
	{
		var o = new float[Count];
		if ( PerVertex )
		{
			foreach ( var f in Mesh.Faces )
			{
				if ( f.UVs is null || f.UVs.Length != f.Count ) continue;
				for ( var c = 0; c < f.Count; c++ ) o[f.Indices[c]] = sample( f.UVs[c].x, f.UVs[c].y );
			}
			return o;
		}
		for ( var y = 0; y < Size; y++ )
			for ( var x = 0; x < Size; x++ )
				o[y * Size + x] = sample( (float)x / Size, (float)y / Size );
		return o;
	}

	internal static float[] VertexChannel( PolyMesh m, int channel )
	{
		var o = new float[m.Positions.Count];
		if ( !m.HasVertexColors ) return o;
		for ( var i = 0; i < o.Length; i++ )
		{
			var c = m.VertexColors[i];
			o[i] = channel switch { 0 => c.x, 1 => c.y, 2 => c.z, _ => c.w };
		}
		return o;
	}

	internal static float AoReach( PolyMesh m )
	{
		if ( m.VertexCount == 0 ) return 6f;
		var min = m.Positions[0]; var max = min;
		foreach ( var p in m.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		return Math.Clamp( (max - min).Length * 0.25f, 0.5f, 6f );
	}

	static float[] Convexity( PolyMesh m )
	{
		var cav = SurfaceDetail.Cavity( m, 2 );
		var total = 0f; var count = 0;
		foreach ( var f in m.Faces )
			for ( var i = 0; i < f.Count; i++ )
			{
				total += (m.Positions[f.Indices[i]] - m.Positions[f.Indices[(i + 1) % f.Count]]).Length;
				count++;
			}
		var edge = count == 0 ? 1f : MathF.Max( 1e-6f, total / count );
		return cav.Select( c => -c / edge ).ToArray();
	}
}

/// <summary>
/// Where a layer shows, 0..1 per texel. Sources remap through a smoothstep from <c>lo</c> to
/// <c>hi</c> (reversed when lo &gt; hi, so <c>Cavity( 0, -0.05 )</c> is "raised"); masks combine
/// with <c>*</c> (both), <c>+</c>, <see cref="Max"/> (either), <see cref="Min"/> and <see cref="Invert"/>.
/// </summary>
public sealed class Mask
{
	readonly string _key;
	readonly Func<FleshSurface, float[]> _eval;

	Mask( string key, Func<FleshSurface, float[]> eval )
	{
		_key = key;
		_eval = eval;
	}

	public float[] Evaluate( FleshSurface s ) => s.Cached( "mask:" + _key, () => _eval( s ) );

	public static Mask All { get; } = new( "all", s => Enumerable.Repeat( 1f, s.Count ).ToArray() );

	/// <summary>Cavity in inches, + in creases.</summary>
	public static Mask Cavity( float lo, float hi ) => From( "cavity", s => s.Cavity, lo, hi );

	/// <summary>Occlusion, 0 open to 1 shut in.</summary>
	public static Mask Ao( float lo, float hi ) => From( "ao", s => s.Ao, lo, hi );

	/// <summary>Convexity per edge length, + on ridges.</summary>
	public static Mask Curvature( float lo, float hi ) => From( "curvature", s => s.Curvature, lo, hi );

	/// <summary>Height over the body, 0 bottom to 1 top.</summary>
	public static Mask Height( float lo, float hi ) => From( "height", s => s.Height, lo, hi );

	/// <summary>Any signal, e.g. a <see cref="TexelSignal"/> over a bake.</summary>
	public static Mask Signal( ISurfaceSignal signal, float lo, float hi ) => From( "sig:" + signal.Key, _ => signal, lo, hi );

	static Mask From( string name, Func<FleshSurface, ISurfaceSignal> pick, float lo, float hi ) =>
		new( $"{name}({F( lo )},{F( hi )})", s => Remap( s.Read( pick( s ) ), lo, hi ) );

	/// <summary>Worley cells <paramref name="cell"/> inches across, by distance to the cell's point
	/// (F1, about 0 at the centre to 0.6 at the walls): pits, spots, alveoli.</summary>
	public static Mask WorleyCells( float cell, float lo, float hi, int seed = 0 ) =>
		new( $"wc({F( cell )},{F( lo )},{F( hi )},{seed})", s =>
		{
			var size = cell * s.Detail;
			return Remap( s.PerTexel( p => SurfaceDetail.Worley( p / size, s.Seed + seed ).F1 ), lo, hi );
		} );

	/// <summary>Worley cell walls (F2 − F1, 0 on a wall): veins, networks, cracks.</summary>
	public static Mask WorleyEdges( float cell, float lo, float hi, int seed = 0 ) =>
		new( $"we({F( cell )},{F( lo )},{F( hi )},{seed})", s =>
		{
			var size = cell * s.Detail;
			return Remap( s.PerTexel( p => { var (f1, f2) = SurfaceDetail.Worley( p / size, s.Seed + seed ); return f2 - f1; } ), lo, hi );
		} );

	/// <summary>Fractal noise, blobs about <paramref name="size"/> inches across, 0..1 centred on
	/// 0.5. Ridged folds it into thin bright lines (1 on the ridge), for marbling and veins.</summary>
	public static Mask Fbm( float size, float lo, float hi, int seed = 0, int octaves = 4, bool ridged = false ) =>
		new( $"fbm({F( size )},{F( lo )},{F( hi )},{seed},{octaves},{ridged})", s =>
		{
			var noise = new Noise3( s.Seed * 31 + seed );
			var scale = 1f / (size * s.Detail);
			return Remap( s.PerTexel( p =>
			{
				var v = noise.Fbm( p * scale, octaves );
				return ridged ? 1f - MathF.Min( 1f, MathF.Abs( v ) * 2f ) : Math.Clamp( 0.5f + v * 0.9f, 0f, 1f );
			} ), lo, hi );
		} );

	/// <summary>The body's paint (any Paint feature before this one), by coverage. 0 where unpainted.</summary>
	public static Mask Painted( float lo = 0f, float hi = 1f ) =>
		new( $"paint({F( lo )},{F( hi )})", s =>
		{
			if ( s.Mesh.Paint is not { } canvas ) return new float[s.Count];
			return Remap( s.PerUv( ( u, v ) =>
			{
				var cx = Math.Clamp( (int)(u * canvas.Width), 0, canvas.Width - 1 );
				var cy = Math.Clamp( (int)(v * canvas.Height), 0, canvas.Height - 1 );
				return canvas.Rgba[(cy * canvas.Width + cx) * 4 + 3] / 255f;
			} ), lo, hi );
		} );

	/// <summary>One channel of the vertex colours (0 R, 1 G, 2 B, 3 A), 0 where the mesh has none. The
	/// packed-mask convention from body-horror.md §4: R corruption, G wetness, B veins, A occlusion.</summary>
	public static Mask VertexColour( int channel, float lo = 0f, float hi = 1f ) =>
		From( "vc" + channel, s => new VertexSignal( "vc" + channel, m => FleshSurface.VertexChannel( m, channel ) ), lo, hi );

	/// <summary>The surface's corruption signal (<see cref="FleshSurface.Corruption"/>), 0 untouched to 1 turned.</summary>
	public static Mask Corruption( float lo, float hi ) => From( "corruption", s => s.Corruption, lo, hi );

	/// <summary>A tileable picture's brightness laid on triplanar, <paramref name="tile"/> inches a repeat.</summary>
	public static Mask Triplanar( TriplanarImage image, float tile, float lo = 0f, float hi = 1f ) =>
		new( $"tri({image.Key},{F( tile )},{F( lo )},{F( hi )})", s =>
			Remap( s.Triplanar( image, tile ).Select( c => c.x * 0.2126f + c.y * 0.7152f + c.z * 0.0722f ).ToArray(), lo, hi ) );

	public static Mask operator *( Mask a, Mask b ) => Zip( a, b, "*", ( x, y ) => x * y );
	public static Mask operator +( Mask a, Mask b ) => Zip( a, b, "+", ( x, y ) => x + y );
	public static Mask operator *( Mask a, float k ) => new( $"({a._key}*{F( k )})", s => a.Evaluate( s ).Select( v => v * k ).ToArray() );
	public static Mask Max( Mask a, Mask b ) => Zip( a, b, "max", MathF.Max );
	public static Mask Min( Mask a, Mask b ) => Zip( a, b, "min", MathF.Min );
	public Mask Invert() => new( $"(1-{_key})", s => Evaluate( s ).Select( v => 1f - v ).ToArray() );

	static Mask Zip( Mask a, Mask b, string op, Func<float, float, float> f ) =>
		new( $"({a._key}{op}{b._key})", s =>
		{
			var x = a.Evaluate( s );
			var y = b.Evaluate( s );
			var o = new float[x.Length];
			for ( var i = 0; i < o.Length; i++ ) o[i] = f( x[i], y[i] );
			return o;
		} );

	static float[] Remap( float[] values, float lo, float hi )
	{
		var o = new float[values.Length];
		var span = MathF.Abs( hi - lo ) < 1e-9f ? 1e-9f : hi - lo;
		for ( var i = 0; i < o.Length; i++ )
		{
			var t = Math.Clamp( (values[i] - lo) / span, 0f, 1f );
			o[i] = t * t * (3f - 2f * t);
		}
		return o;
	}

	static string F( float v ) => v.ToString( "0.####", CultureInfo.InvariantCulture );
}

/// <summary>
/// A tileable picture for triplanar layering: projected along x, y and z and blended by how much the
/// surface faces each axis, so a scanned meat, fat or mucosa swatch goes on a mesh with no UVs and
/// runs straight across seams (how Hamilton's Anguish was shaded; body-horror.md §0). Colours stay
/// sRGB 0..1, like every other colour in a stack.
/// </summary>
public sealed class TriplanarImage
{
	public readonly int Width, Height;
	public readonly string Key;
	readonly Vec3[] _rgb;

	public TriplanarImage( int width, int height, Vec3[] rgb, string key )
	{
		if ( width < 1 || height < 1 || rgb is null || rgb.Length != width * height )
			throw new ArgumentException( "A picture needs width × height colours." );
		Width = width;
		Height = height;
		_rgb = rgb;
		Key = key ?? "image";
	}

	public static TriplanarImage FromPng( string path )
	{
		var (w, h, px, _) = Effigy.Render.Raster.LoadPng( path );
		var rgb = new Vec3[w * h];
		for ( var i = 0; i < rgb.Length; i++ )
			rgb[i] = new Vec3( ((px[i] >> 16) & 255) / 255f, ((px[i] >> 8) & 255) / 255f, (px[i] & 255) / 255f );
		return new TriplanarImage( w, h, rgb, Path.GetFileName( path ) + ":" + new FileInfo( path ).Length );
	}

	/// <summary>
	/// The picture at <paramref name="p"/> on a surface facing <paramref name="n"/>, one repeat every
	/// <paramref name="tile"/> inches. <paramref name="sharpness"/> narrows the blend between the three
	/// projections (4 keeps the smear to the 45° bands).
	/// </summary>
	public Vec3 Sample( Vec3 p, Vec3 n, float tile, float sharpness = 4f )
	{
		var wx = MathF.Pow( MathF.Abs( n.x ), sharpness );
		var wy = MathF.Pow( MathF.Abs( n.y ), sharpness );
		var wz = MathF.Pow( MathF.Abs( n.z ), sharpness );
		var sum = wx + wy + wz;
		if ( sum < 1e-8f ) { wz = 1f; sum = 1f; }
		var k = 1f / MathF.Max( 1e-4f, tile );
		return (Bilinear( p.y * k, p.z * k ) * wx + Bilinear( p.x * k, p.z * k ) * wy + Bilinear( p.x * k, p.y * k ) * wz) / sum;
	}

	Vec3 Bilinear( float u, float v )
	{
		var fx = (u - MathF.Floor( u )) * Width - 0.5f;
		var fy = (v - MathF.Floor( v )) * Height - 0.5f;
		var x0 = (int)MathF.Floor( fx ); var y0 = (int)MathF.Floor( fy );
		var tx = fx - x0; var ty = fy - y0;
		Vec3 At( int x, int y ) => _rgb[(((y % Height) + Height) % Height) * Width + (((x % Width) + Width) % Width)];
		var top = At( x0, y0 ) * (1f - tx) + At( x0 + 1, y0 ) * tx;
		var bottom = At( x0, y0 + 1 ) * (1f - tx) + At( x0 + 1, y0 + 1 ) * tx;
		return top * (1f - ty) + bottom * ty;
	}
}
