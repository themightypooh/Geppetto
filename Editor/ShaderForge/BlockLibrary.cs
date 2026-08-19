using System.Collections.Generic;
using System.Linq;

namespace Marionette.ShaderForge;

/// <summary>
/// The locked v1 block set: 18 effects covering what games ask for most often.
///
/// This is a LIVING LIBRARY, deliberately not an attempt to enumerate every shader anyone could
/// want — that space is effectively infinite and chasing it never ships. The rule that keeps it
/// honest is in the generator, not here: a description that matches nothing says "no block for
/// that yet" rather than quietly emitting something plausible-looking. Every such miss is the
/// roadmap for the next block.
///
/// Do not add to this list to cover a hypothetical. Add when a real prompt missed.
/// </summary>
public static class BlockLibrary
{
	/// <summary>Shared value noise. Emitted once, only when a selected block asks for it.</summary>
	public const string NoiseHelpers = @"
float SFHash( float2 p )
{
	return frac( sin( dot( p, float2( 127.1, 311.7 ) ) ) * 43758.5453 );
}

float SFNoise( float2 p )
{
	float2 cell = floor( p );
	float2 f = frac( p );
	f = f * f * ( 3.0 - 2.0 * f );

	float a = SFHash( cell );
	float b = SFHash( cell + float2( 1.0, 0.0 ) );
	float c = SFHash( cell + float2( 0.0, 1.0 ) );
	float d = SFHash( cell + float2( 1.0, 1.0 ) );

	return lerp( lerp( a, b, f.x ), lerp( c, d, f.x ), f.y );
}";

	/// <summary>Needs PixelInput, so it is emitted inside PS rather than COMMON.</summary>
	public const string ViewDirHelper = @"
float3 SFViewDir( PixelInput i )
{
	return normalize( g_vCameraPositionWs - i.vPositionWithOffsetWs.xyz );
}";

	private static List<ShaderBlock> _all;

	public static IReadOnlyList<ShaderBlock> All => _all ??= Build();

	public static ShaderBlock ById( string id ) => All.FirstOrDefault( b => b.Id == id );

	private static List<ShaderBlock> Build() => new()
	{
		// ================= the essentials =================

		new ShaderBlock
		{
			Id = "emissive",
			Title = "Emissive Output",
			Summary = "HDR emissive added to the surface",
			Keywords = new[] { "glow", "glowing", "neon", "emissive", "emit", "light up", "lit up", "bright" },
			Params = new[]
			{
				ShaderParam.Color( "g_vSfEmissiveColor", "Emissive Colour", 1.0f, 0.65f, 0.25f ),
				ShaderParam.Float( "g_flSfEmissiveStrength", "Emissive Strength", 2.0f, 0.0f, 20.0f ),
			},
			SurfaceCode = @"
	m.Emission += g_vSfEmissiveColor * g_flSfEmissiveStrength * SFPulse();",
		},

		new ShaderBlock
		{
			Id = "dissolve",
			Title = "Dissolve",
			Summary = "Noise-threshold alpha cutout with a burning edge",
			Keywords = new[] { "dissolve", "dissolving", "disintegrate", "disintegrating", "burn away", "burn", "burning" },
			ExclusiveGroups = new[] { "opacity" },
			Priority = 10,
			NeedsNoise = true,
			Params = new[]
			{
				ShaderParam.Float( "g_flSfDissolveAmount", "Dissolve Amount", 0.0f, 0.0f, 1.0f ),
				ShaderParam.Float( "g_flSfDissolveEdge", "Edge Width", 0.08f, 0.001f, 0.4f ),
				ShaderParam.Color( "g_vSfDissolveEdgeColor", "Edge Colour", 1.0f, 0.4f, 0.05f ),
				ShaderParam.Float( "g_flSfDissolveScale", "Noise Scale", 12.0f, 1.0f, 60.0f ),
			},
			SurfaceCode = @"
	// Cut where the noise falls below the threshold, and light the surviving rim so the cut edge
	// reads as burning rather than as a hole.
	float sfDissolveNoise = SFNoise( i.vTextureCoords.xy * g_flSfDissolveScale );
	float sfDissolveEdge = sfDissolveNoise - g_flSfDissolveAmount;

	clip( sfDissolveEdge );

	float sfDissolveRim = saturate( 1.0 - sfDissolveEdge / max( g_flSfDissolveEdge, 0.0001 ) );
	m.Emission += g_vSfDissolveEdgeColor * sfDissolveRim * 6.0;",
		},

		new ShaderBlock
		{
			Id = "toon",
			Title = "Toon Lighting",
			Summary = "Quantises the lit result into hard bands",
			Keywords = new[] { "toon", "cel", "cel shaded", "cartoon", "cartoony", "flat shaded", "anime" },
			ExclusiveGroups = new[] { "lighting" },
			Priority = 5,
			Params = new[]
			{
				ShaderParam.Float( "g_flSfToonSteps", "Bands", 3.0f, 2.0f, 8.0f ),
				ShaderParam.Float( "g_flSfToonBoost", "Band Contrast", 1.0f, 0.0f, 2.0f ),
			},
			// Quantising the shaded luminance in Post, rather than replacing the lighting model.
			// A true cel shader overrides the diffuse response, which means owning the whole
			// shading path and losing everything else the standard model provides. Banding the
			// result gets the look, composes with every other block, and is honest about being an
			// approximation.
			PostCode = @"
	float sfToonLum = dot( result.rgb, float3( 0.299, 0.587, 0.114 ) );

	if ( sfToonLum > 0.0001 )
	{
		float sfToonSteps = max( g_flSfToonSteps, 1.0 );
		float sfToonQuant = floor( sfToonLum * sfToonSteps + 0.5 ) / sfToonSteps;
		float sfToonScale = lerp( 1.0, sfToonQuant / sfToonLum, saturate( g_flSfToonBoost ) );
		result.rgb *= sfToonScale;
	}",
		},

		new ShaderBlock
		{
			Id = "glass",
			Title = "Glass Material",
			Summary = "Fresnel transparency with a tint and a polished surface",
			Keywords = new[] { "glass", "glassy", "ice", "icy", "crystal", "transparent", "see through", "frosted" },
			ExclusiveGroups = new[] { "opacity", "lighting" },
			Priority = 8,
			NeedsViewDir = true,
			NeedsTranslucent = true,
			Params = new[]
			{
				ShaderParam.Float( "g_flSfGlassOpacity", "Base Opacity", 0.18f, 0.0f, 1.0f ),
				ShaderParam.Float( "g_flSfGlassFresnel", "Fresnel Power", 3.0f, 0.5f, 8.0f ),
				ShaderParam.Color( "g_vSfGlassTint", "Tint", 0.85f, 0.93f, 1.0f ),
				ShaderParam.Float( "g_flSfGlassRoughness", "Surface Roughness", 0.08f, 0.0f, 1.0f ),
			},
			SurfaceCode = @"
	// Edge-on faces go opaque, face-on faces go clear - the whole reason glass reads as glass.
	float3 sfGlassView = SFViewDir( i );
	float sfGlassFacing = 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfGlassView ) );
	float sfGlassFresnel = pow( sfGlassFacing, g_flSfGlassFresnel );

	m.Albedo *= g_vSfGlassTint;
	m.Roughness = g_flSfGlassRoughness;
	m.Opacity = saturate( g_flSfGlassOpacity + sfGlassFresnel );",
		},

		new ShaderBlock
		{
			Id = "water",
			Title = "Water Surface",
			Summary = "Vertex wave displacement with a polished, scrolling surface",
			Keywords = new[] { "water", "ocean", "sea", "wave", "waves", "ripple", "ripples", "liquid" },
			Params = new[]
			{
				ShaderParam.Float( "g_flSfWaveAmplitude", "Wave Height", 3.0f, 0.0f, 32.0f ),
				ShaderParam.Float( "g_flSfWaveFrequency", "Wave Frequency", 0.06f, 0.001f, 0.5f ),
				ShaderParam.Float( "g_flSfWaveSpeed", "Wave Speed", 1.5f, 0.0f, 8.0f ),
				ShaderParam.Color( "g_vSfWaterTint", "Water Tint", 0.25f, 0.55f, 0.75f ),
			},
			VertexCode = @"
	// Two crossed sine waves - one sine alone reads as a flag, not a surface.
	float sfWavePhaseA = ( vPositionOs.x + vPositionOs.y ) * g_flSfWaveFrequency + g_flTime * g_flSfWaveSpeed;
	float sfWavePhaseB = ( vPositionOs.x - vPositionOs.y ) * g_flSfWaveFrequency * 1.7 + g_flTime * g_flSfWaveSpeed * 0.8;
	vPositionOs.z += ( sin( sfWavePhaseA ) + sin( sfWavePhaseB ) * 0.5 ) * g_flSfWaveAmplitude;",
			SurfaceCode = @"
	m.Albedo *= g_vSfWaterTint;
	m.Roughness = min( m.Roughness, 0.12 );",
		},

		new ShaderBlock
		{
			Id = "wind",
			Title = "Wind Deformation",
			Summary = "Sine sway in world space, stronger further from the root",
			Keywords = new[] { "wind", "windy", "foliage", "grass", "leaves", "flag", "sway", "swaying", "blowing" },
			Params = new[]
			{
				ShaderParam.Float( "g_flSfWindStrength", "Wind Strength", 2.0f, 0.0f, 24.0f ),
				ShaderParam.Float( "g_flSfWindSpeed", "Wind Speed", 1.5f, 0.0f, 8.0f ),
				ShaderParam.Float( "g_flSfWindFalloff", "Height Falloff", 0.05f, 0.001f, 0.5f ),
			},
			VertexCode = @"
	// Masked by height so the base stays planted - without this the whole mesh slides sideways.
	float sfWindMask = saturate( vPositionOs.z * g_flSfWindFalloff );
	float sfWindPhase = g_flTime * g_flSfWindSpeed + ( vPositionOs.x + vPositionOs.y ) * 0.04;
	vPositionOs.xy += sin( sfWindPhase ) * g_flSfWindStrength * sfWindMask;",
		},

		new ShaderBlock
		{
			Id = "hitflash",
			Title = "Hit Flash",
			Summary = "Emissive burst driven by a value the game sets on impact",
			Keywords = new[] { "hit flash", "hit", "damage", "damaged", "flash", "hurt", "impact" },
			Params = new[]
			{
				ShaderParam.Float( "g_flSfHitFlash", "Flash Amount", 0.0f, 0.0f, 1.0f ),
				ShaderParam.Color( "g_vSfHitFlashColor", "Flash Colour", 1.0f, 1.0f, 1.0f ),
			},
			SurfaceCode = @"
	// Driven, not animated: the game writes g_flSfHitFlash on impact and decays it. Animating it
	// here would make every object in the scene flash on the same clock.
	m.Albedo = lerp( m.Albedo, g_vSfHitFlashColor, saturate( g_flSfHitFlash ) );
	m.Emission += g_vSfHitFlashColor * g_flSfHitFlash * 8.0;",
		},

		new ShaderBlock
		{
			Id = "rim",
			Title = "Rim Light",
			Summary = "Fresnel rim colour added to the silhouette",
			Keywords = new[] { "rim", "rim light", "rimlight", "backlight", "back light", "fresnel", "edge light" },
			NeedsViewDir = true,
			Params = new[]
			{
				ShaderParam.Color( "g_vSfRimColor", "Rim Colour", 0.4f, 0.7f, 1.0f ),
				ShaderParam.Float( "g_flSfRimPower", "Rim Tightness", 3.0f, 0.5f, 8.0f ),
				ShaderParam.Float( "g_flSfRimStrength", "Rim Strength", 1.5f, 0.0f, 10.0f ),
			},
			SurfaceCode = @"
	float3 sfRimView = SFViewDir( i );
	float sfRim = pow( 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfRimView ) ), g_flSfRimPower );
	m.Emission += g_vSfRimColor * sfRim * g_flSfRimStrength * SFPulse();",
		},

		new ShaderBlock
		{
			Id = "hologram",
			Title = "Hologram",
			Summary = "Scanlines, flicker and transparency",
			Keywords = new[] { "hologram", "holographic", "holo", "glitch", "glitchy", "projection" },
			ExclusiveGroups = new[] { "opacity" },
			Priority = 6,
			NeedsTranslucent = true,
			Params = new[]
			{
				ShaderParam.Color( "g_vSfHologramColor", "Hologram Colour", 0.3f, 0.9f, 1.0f ),
				ShaderParam.Float( "g_flSfScanlineDensity", "Scanline Density", 1.2f, 0.05f, 6.0f ),
				ShaderParam.Float( "g_flSfScanlineSpeed", "Scanline Speed", 1.0f, 0.0f, 10.0f ),
				ShaderParam.Float( "g_flSfHologramOpacity", "Opacity", 0.6f, 0.0f, 1.0f ),
			},
			SurfaceCode = @"
	// Scanlines in SCREEN space, so they stay put as the object turns - the giveaway that
	// something is projected rather than painted on.
	float sfHoloScan = sin( i.vPositionSs.y * g_flSfScanlineDensity - g_flTime * g_flSfScanlineSpeed * 6.0 ) * 0.5 + 0.5;
	float sfHoloFlicker = 0.9 + 0.1 * sin( g_flTime * 37.0 );

	m.Emission += g_vSfHologramColor * ( 0.35 + sfHoloScan * 0.9 ) * sfHoloFlicker * 3.0;
	m.Opacity = saturate( g_flSfHologramOpacity * ( 0.55 + sfHoloScan * 0.45 ) * sfHoloFlicker );",
		},

		new ShaderBlock
		{
			Id = "outline",
			Title = "Outline",
			Summary = "Silhouette-edge band in a chosen colour",
			Keywords = new[] { "outline", "outlined", "border", "silhouette", "stroke" },
			NeedsViewDir = true,
			Params = new[]
			{
				ShaderParam.Color( "g_vSfOutlineColor", "Outline Colour", 0.05f, 0.05f, 0.06f ),
				ShaderParam.Float( "g_flSfOutlineWidth", "Outline Width", 0.35f, 0.01f, 1.0f ),
				ShaderParam.Float( "g_flSfOutlineGlow", "Outline Glow", 0.0f, 0.0f, 8.0f ),
			},
			// A silhouette band from the view-facing term, NOT an inverted hull. A real hull
			// outline is a second pass with front-face culling, and multi-pass shaders are out of
			// v1 scope. This holds up on rounded shapes and thins out on hard edges seen flat on.
			SurfaceCode = @"
	float3 sfOutlineView = SFViewDir( i );
	float sfOutlineFacing = 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfOutlineView ) );
	float sfOutlineMask = smoothstep( 1.0 - g_flSfOutlineWidth, 1.0, sfOutlineFacing );

	m.Albedo = lerp( m.Albedo, g_vSfOutlineColor, sfOutlineMask );
	m.Emission += g_vSfOutlineColor * sfOutlineMask * g_flSfOutlineGlow;",
		},

		new ShaderBlock
		{
			Id = "pulse",
			Title = "Time Modulation",
			Summary = "Drives every other block's intensity on a sine",
			Keywords = new[] { "pulse", "pulsing", "pulsate", "flicker", "flickering", "breathe", "breathing", "throb", "throbbing", "blink", "blinking" },
			ProvidesPulse = true,
			Params = new[]
			{
				ShaderParam.Float( "g_flSfPulseSpeed", "Pulse Speed", 2.0f, 0.0f, 12.0f ),
				ShaderParam.Float( "g_flSfPulseDepth", "Pulse Depth", 0.6f, 0.0f, 1.0f ),
			},
			// No code of its own - it supplies SFPulse's body, which the emissive-style blocks
			// already multiply by. That is what makes "glowing edges that pulse" work without
			// either block referring to the other.
		},

		new ShaderBlock
		{
			Id = "tint",
			Title = "Colour Tint",
			Summary = "Multiplies the surface by a chosen colour",
			Keywords = new[] { "tint", "tinted", "recolor", "recolour", "colour", "color", "colored", "coloured" },
			Priority = -5,
			Params = new[]
			{
				ShaderParam.Color( "g_vSfTintColor", "Tint Colour", 1.0f, 1.0f, 1.0f ),
				ShaderParam.Float( "g_flSfTintAmount", "Tint Amount", 1.0f, 0.0f, 1.0f ),
			},
			SurfaceCode = @"
	m.Albedo = lerp( m.Albedo, m.Albedo * g_vSfTintColor, g_flSfTintAmount );",
		},

		// ================= the game-specific ones =================

		new ShaderBlock
		{
			Id = "health",
			Title = "Health Reactive",
			Summary = "Colour and pulse driven by a health value the game sets",
			// "hurt" deliberately belongs to Hit Flash, not here - a keyword owned by two blocks
			// makes selection arbitrary, and "hurt" reads as the moment of damage.
			Keywords = new[] { "health", "low health", "dying", "wounded", "critical" },
			Params = new[]
			{
				ShaderParam.Float( "g_flSfHealthFraction", "Health Fraction", 1.0f, 0.0f, 1.0f ),
				ShaderParam.Color( "g_vSfHealthLowColor", "Critical Colour", 1.0f, 0.12f, 0.1f ),
				ShaderParam.Float( "g_flSfHealthPulseSpeed", "Critical Pulse Speed", 6.0f, 0.0f, 20.0f ),
			},
			SurfaceCode = @"
	// Pulses faster as it gets worse, which reads as urgency without needing a number on screen.
	float sfHealthDanger = 1.0 - saturate( g_flSfHealthFraction );
	float sfHealthBeat = sin( g_flTime * g_flSfHealthPulseSpeed * sfHealthDanger ) * 0.5 + 0.5;

	m.Albedo = lerp( m.Albedo, g_vSfHealthLowColor, sfHealthDanger * 0.6 );
	m.Emission += g_vSfHealthLowColor * sfHealthDanger * sfHealthBeat * 2.5;",
		},

		new ShaderBlock
		{
			Id = "loot",
			Title = "Loot Glow",
			Summary = "Rim glow coloured by a rarity tier",
			Keywords = new[] { "loot", "rarity", "rare", "legendary", "epic", "item glow", "pickup glow", "drop" },
			NeedsViewDir = true,
			Params = new[]
			{
				ShaderParam.Float( "g_flSfRarity", "Rarity Tier", 1.0f, 0.0f, 4.0f ),
				ShaderParam.Float( "g_flSfLootGlow", "Glow Strength", 2.5f, 0.0f, 12.0f ),
			},
			CommonCode = @"
// Rarity ramp: 0 common, 1 uncommon, 2 rare, 3 epic, 4 legendary. Interpolating rather than
// branching means a designer can sit a drop between tiers while tuning.
float3 SFRarityColor( float tier )
{
	float t = clamp( tier, 0.0, 4.0 );

	float3 c = float3( 0.62, 0.62, 0.62 );
	c = lerp( c, float3( 0.20, 0.80, 0.25 ), saturate( t ) );
	c = lerp( c, float3( 0.20, 0.45, 0.95 ), saturate( t - 1.0 ) );
	c = lerp( c, float3( 0.65, 0.25, 0.95 ), saturate( t - 2.0 ) );
	c = lerp( c, float3( 1.00, 0.62, 0.10 ), saturate( t - 3.0 ) );

	return c;
}",
			SurfaceCode = @"
	float3 sfLootView = SFViewDir( i );
	float sfLootRim = pow( 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfLootView ) ), 2.5 );
	m.Emission += SFRarityColor( g_flSfRarity ) * sfLootRim * g_flSfLootGlow * SFPulse();",
		},

		new ShaderBlock
		{
			Id = "interactable",
			Title = "Interactable Highlight",
			Summary = "Highlight that fades in when the object becomes usable",
			Keywords = new[] { "interactable", "interactive", "usable", "use", "pickup", "pick up", "highlight", "hover" },
			NeedsViewDir = true,
			Params = new[]
			{
				ShaderParam.Float( "g_flSfHighlight", "Highlight Amount", 0.0f, 0.0f, 1.0f ),
				ShaderParam.Color( "g_vSfHighlightColor", "Highlight Colour", 1.0f, 0.9f, 0.45f ),
				ShaderParam.Float( "g_flSfHighlightStrength", "Highlight Strength", 3.0f, 0.0f, 12.0f ),
			},
			SurfaceCode = @"
	float3 sfHighlightView = SFViewDir( i );
	float sfHighlightRim = pow( 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfHighlightView ) ), 2.0 );
	m.Emission += g_vSfHighlightColor * sfHighlightRim * g_flSfHighlightStrength * saturate( g_flSfHighlight ) * SFPulse();",
		},

		new ShaderBlock
		{
			Id = "team",
			Title = "Team Colour",
			Summary = "Tints the surface by a team index",
			Keywords = new[] { "team", "teams", "faction", "friendly", "enemy", "ally", "squad" },
			Params = new[]
			{
				ShaderParam.Float( "g_flSfTeamIndex", "Team Index", 0.0f, 0.0f, 7.0f ),
				ShaderParam.Float( "g_flSfTeamBlend", "Team Blend", 0.75f, 0.0f, 1.0f ),
			},
			CommonCode = @"
// Eight fixed team colours, picked to stay distinguishable for the most common colour-blindness
// types - red/green as the only difference between two teams is a real accessibility failure.
float3 SFTeamColor( float index )
{
	int t = (int)clamp( index, 0.0, 7.0 );

	if ( t == 0 ) return float3( 0.20, 0.45, 0.95 );
	if ( t == 1 ) return float3( 0.95, 0.30, 0.20 );
	if ( t == 2 ) return float3( 0.95, 0.80, 0.15 );
	if ( t == 3 ) return float3( 0.25, 0.75, 0.45 );
	if ( t == 4 ) return float3( 0.70, 0.35, 0.90 );
	if ( t == 5 ) return float3( 0.20, 0.80, 0.85 );
	if ( t == 6 ) return float3( 0.95, 0.55, 0.15 );

	return float3( 0.85, 0.85, 0.88 );
}",
			SurfaceCode = @"
	m.Albedo = lerp( m.Albedo, SFTeamColor( g_flSfTeamIndex ), g_flSfTeamBlend );",
		},

		new ShaderBlock
		{
			Id = "snow",
			Title = "Snow Cover",
			Summary = "White blend on upward-facing surfaces",
			Keywords = new[] { "snow", "snowy", "frost", "frosty", "ice coat", "iced", "winter" },
			Params = new[]
			{
				ShaderParam.Float( "g_flSfSnowAmount", "Snow Amount", 0.6f, 0.0f, 1.0f ),
				ShaderParam.Color( "g_vSfSnowColor", "Snow Colour", 0.92f, 0.95f, 1.0f ),
				ShaderParam.Float( "g_flSfSnowSharpness", "Edge Sharpness", 3.0f, 0.1f, 16.0f ),
			},
			SurfaceCode = @"
	// World up, not object up: snow settles the same way however the prop was authored or rotated.
	float sfSnowFacing = saturate( dot( normalize( i.vNormalWs.xyz ), float3( 0.0, 0.0, 1.0 ) ) );
	float sfSnowMask = saturate( pow( sfSnowFacing, g_flSfSnowSharpness ) * g_flSfSnowAmount * 2.0 );

	m.Albedo = lerp( m.Albedo, g_vSfSnowColor, sfSnowMask );
	m.Roughness = lerp( m.Roughness, 0.85, sfSnowMask );",
		},

		new ShaderBlock
		{
			Id = "distort",
			Title = "Heat Distortion",
			Summary = "Animated noise warp of the surface's own texture coordinates",
			Keywords = new[] { "distort", "distortion", "heat", "heat haze", "haze", "warp", "warped", "shimmer", "mirage" },
			NeedsNoise = true,
			Params = new[]
			{
				ShaderParam.Float( "g_flSfDistortStrength", "Distortion Strength", 0.03f, 0.0f, 0.25f ),
				ShaderParam.Float( "g_flSfDistortSpeed", "Rise Speed", 0.4f, 0.0f, 4.0f ),
				ShaderParam.Float( "g_flSfDistortScale", "Noise Scale", 6.0f, 0.5f, 40.0f ),
			},
			// Warps the surface's OWN uvs, not the frame buffer. A true screen-space heat haze
			// samples a copy of what is behind the object, which needs a translucent pass and a
			// frame-buffer grab - out of v1 scope. On a textured surface this reads correctly;
			// on an untextured one there is nothing to warp and it will look like nothing.
			UvCode = @"
	float2 sfDistortUv = i.vTextureCoords.xy * g_flSfDistortScale + float2( 0.0, -g_flTime * g_flSfDistortSpeed );
	float2 sfDistortOffset = float2( SFNoise( sfDistortUv ), SFNoise( sfDistortUv + 17.3 ) ) - 0.5;
	i.vTextureCoords.xy += sfDistortOffset * g_flSfDistortStrength;",
		},
	};
}
