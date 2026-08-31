// Shader Forge live preview. Every library block is in here, gated off
// until a word turns it on. Do not edit — the tool rewrites this on open.

HEADER
{
	Description = "Shader Forge live preview";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
}

COMMON
{
	#include "common/shared.hlsl"

	float g_flSfOn_emissive < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_emissive" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_dissolve < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_dissolve" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_toon < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_toon" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_glass < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_glass" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_water < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_water" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_wind < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_wind" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_hitflash < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_hitflash" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_rim < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_rim" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_hologram < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_hologram" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_outline < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_outline" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_pulse < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_pulse" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_tint < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_tint" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_health < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_health" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_loot < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_loot" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_interactable < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_interactable" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_team < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_team" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_snow < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_snow" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfOn_distort < UiGroup( "Live,10/0" ); Attribute( "g_flSfOn_distort" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;

	// --- Emissive Output ---
	float3 g_vSfEmissiveColor < UiType( Color ); UiGroup( "Emissive Output,10/1" ); Attribute( "g_vSfEmissiveColor" ); Default3( 1.0, 0.65, 0.25 ); >;
	float g_flSfEmissiveStrength < UiGroup( "Emissive Output,10/2" ); Attribute( "g_flSfEmissiveStrength" ); Default( 2.0 ); Range( 0.0, 20.0 ); >;

	// --- Dissolve ---
	float g_flSfDissolveAmount < UiGroup( "Dissolve,10/1" ); Attribute( "g_flSfDissolveAmount" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float g_flSfDissolveEdge < UiGroup( "Dissolve,10/2" ); Attribute( "g_flSfDissolveEdge" ); Default( 0.08 ); Range( 0.001, 0.4 ); >;
	float3 g_vSfDissolveEdgeColor < UiType( Color ); UiGroup( "Dissolve,10/3" ); Attribute( "g_vSfDissolveEdgeColor" ); Default3( 1.0, 0.4, 0.05 ); >;
	float g_flSfDissolveScale < UiGroup( "Dissolve,10/4" ); Attribute( "g_flSfDissolveScale" ); Default( 12.0 ); Range( 1.0, 60.0 ); >;

	// --- Toon Lighting ---
	float g_flSfToonSteps < UiGroup( "Toon Lighting,10/1" ); Attribute( "g_flSfToonSteps" ); Default( 3.0 ); Range( 2.0, 8.0 ); >;
	float g_flSfToonBoost < UiGroup( "Toon Lighting,10/2" ); Attribute( "g_flSfToonBoost" ); Default( 1.0 ); Range( 0.0, 2.0 ); >;

	// --- Glass Material ---
	float g_flSfGlassOpacity < UiGroup( "Glass Material,10/1" ); Attribute( "g_flSfGlassOpacity" ); Default( 0.18 ); Range( 0.0, 1.0 ); >;
	float g_flSfGlassFresnel < UiGroup( "Glass Material,10/2" ); Attribute( "g_flSfGlassFresnel" ); Default( 3.0 ); Range( 0.5, 8.0 ); >;
	float3 g_vSfGlassTint < UiType( Color ); UiGroup( "Glass Material,10/3" ); Attribute( "g_vSfGlassTint" ); Default3( 0.85, 0.93, 1.0 ); >;
	float g_flSfGlassRoughness < UiGroup( "Glass Material,10/4" ); Attribute( "g_flSfGlassRoughness" ); Default( 0.08 ); Range( 0.0, 1.0 ); >;

	// --- Water Surface ---
	float g_flSfWaveAmplitude < UiGroup( "Water Surface,10/1" ); Attribute( "g_flSfWaveAmplitude" ); Default( 3.0 ); Range( 0.0, 32.0 ); >;
	float g_flSfWaveFrequency < UiGroup( "Water Surface,10/2" ); Attribute( "g_flSfWaveFrequency" ); Default( 0.06 ); Range( 0.001, 0.5 ); >;
	float g_flSfWaveSpeed < UiGroup( "Water Surface,10/3" ); Attribute( "g_flSfWaveSpeed" ); Default( 1.5 ); Range( 0.0, 8.0 ); >;
	float3 g_vSfWaterTint < UiType( Color ); UiGroup( "Water Surface,10/4" ); Attribute( "g_vSfWaterTint" ); Default3( 0.25, 0.55, 0.75 ); >;

	// --- Wind Deformation ---
	float g_flSfWindStrength < UiGroup( "Wind Deformation,10/1" ); Attribute( "g_flSfWindStrength" ); Default( 2.0 ); Range( 0.0, 24.0 ); >;
	float g_flSfWindSpeed < UiGroup( "Wind Deformation,10/2" ); Attribute( "g_flSfWindSpeed" ); Default( 1.5 ); Range( 0.0, 8.0 ); >;
	float g_flSfWindFalloff < UiGroup( "Wind Deformation,10/3" ); Attribute( "g_flSfWindFalloff" ); Default( 0.05 ); Range( 0.001, 0.5 ); >;

	// --- Hit Flash ---
	float g_flSfHitFlash < UiGroup( "Hit Flash,10/1" ); Attribute( "g_flSfHitFlash" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float3 g_vSfHitFlashColor < UiType( Color ); UiGroup( "Hit Flash,10/2" ); Attribute( "g_vSfHitFlashColor" ); Default3( 1.0, 1.0, 1.0 ); >;

	// --- Rim Light ---
	float3 g_vSfRimColor < UiType( Color ); UiGroup( "Rim Light,10/1" ); Attribute( "g_vSfRimColor" ); Default3( 0.4, 0.7, 1.0 ); >;
	float g_flSfRimPower < UiGroup( "Rim Light,10/2" ); Attribute( "g_flSfRimPower" ); Default( 3.0 ); Range( 0.5, 8.0 ); >;
	float g_flSfRimStrength < UiGroup( "Rim Light,10/3" ); Attribute( "g_flSfRimStrength" ); Default( 1.5 ); Range( 0.0, 10.0 ); >;

	// --- Hologram ---
	float3 g_vSfHologramColor < UiType( Color ); UiGroup( "Hologram,10/1" ); Attribute( "g_vSfHologramColor" ); Default3( 0.3, 0.9, 1.0 ); >;
	float g_flSfScanlineDensity < UiGroup( "Hologram,10/2" ); Attribute( "g_flSfScanlineDensity" ); Default( 1.2 ); Range( 0.05, 6.0 ); >;
	float g_flSfScanlineSpeed < UiGroup( "Hologram,10/3" ); Attribute( "g_flSfScanlineSpeed" ); Default( 1.0 ); Range( 0.0, 10.0 ); >;
	float g_flSfHologramOpacity < UiGroup( "Hologram,10/4" ); Attribute( "g_flSfHologramOpacity" ); Default( 0.6 ); Range( 0.0, 1.0 ); >;

	// --- Outline ---
	float3 g_vSfOutlineColor < UiType( Color ); UiGroup( "Outline,10/1" ); Attribute( "g_vSfOutlineColor" ); Default3( 0.05, 0.05, 0.06 ); >;
	float g_flSfOutlineWidth < UiGroup( "Outline,10/2" ); Attribute( "g_flSfOutlineWidth" ); Default( 0.35 ); Range( 0.01, 1.0 ); >;
	float g_flSfOutlineGlow < UiGroup( "Outline,10/3" ); Attribute( "g_flSfOutlineGlow" ); Default( 0.0 ); Range( 0.0, 8.0 ); >;

	// --- Time Modulation ---
	float g_flSfPulseSpeed < UiGroup( "Time Modulation,10/1" ); Attribute( "g_flSfPulseSpeed" ); Default( 2.0 ); Range( 0.0, 12.0 ); >;
	float g_flSfPulseDepth < UiGroup( "Time Modulation,10/2" ); Attribute( "g_flSfPulseDepth" ); Default( 0.6 ); Range( 0.0, 1.0 ); >;

	// --- Colour Tint ---
	float3 g_vSfTintColor < UiType( Color ); UiGroup( "Colour Tint,10/1" ); Attribute( "g_vSfTintColor" ); Default3( 1.0, 1.0, 1.0 ); >;
	float g_flSfTintAmount < UiGroup( "Colour Tint,10/2" ); Attribute( "g_flSfTintAmount" ); Default( 1.0 ); Range( 0.0, 1.0 ); >;

	// --- Health Reactive ---
	float g_flSfHealthFraction < UiGroup( "Health Reactive,10/1" ); Attribute( "g_flSfHealthFraction" ); Default( 1.0 ); Range( 0.0, 1.0 ); >;
	float3 g_vSfHealthLowColor < UiType( Color ); UiGroup( "Health Reactive,10/2" ); Attribute( "g_vSfHealthLowColor" ); Default3( 1.0, 0.12, 0.1 ); >;
	float g_flSfHealthPulseSpeed < UiGroup( "Health Reactive,10/3" ); Attribute( "g_flSfHealthPulseSpeed" ); Default( 6.0 ); Range( 0.0, 20.0 ); >;

	// --- Loot Glow ---
	float g_flSfRarity < UiGroup( "Loot Glow,10/1" ); Attribute( "g_flSfRarity" ); Default( 1.0 ); Range( 0.0, 4.0 ); >;
	float g_flSfLootGlow < UiGroup( "Loot Glow,10/2" ); Attribute( "g_flSfLootGlow" ); Default( 2.5 ); Range( 0.0, 12.0 ); >;

	// --- Interactable Highlight ---
	float g_flSfHighlight < UiGroup( "Interactable Highlight,10/1" ); Attribute( "g_flSfHighlight" ); Default( 0.0 ); Range( 0.0, 1.0 ); >;
	float3 g_vSfHighlightColor < UiType( Color ); UiGroup( "Interactable Highlight,10/2" ); Attribute( "g_vSfHighlightColor" ); Default3( 1.0, 0.9, 0.45 ); >;
	float g_flSfHighlightStrength < UiGroup( "Interactable Highlight,10/3" ); Attribute( "g_flSfHighlightStrength" ); Default( 3.0 ); Range( 0.0, 12.0 ); >;

	// --- Team Colour ---
	float g_flSfTeamIndex < UiGroup( "Team Colour,10/1" ); Attribute( "g_flSfTeamIndex" ); Default( 0.0 ); Range( 0.0, 7.0 ); >;
	float g_flSfTeamBlend < UiGroup( "Team Colour,10/2" ); Attribute( "g_flSfTeamBlend" ); Default( 0.75 ); Range( 0.0, 1.0 ); >;

	// --- Snow Cover ---
	float g_flSfSnowAmount < UiGroup( "Snow Cover,10/1" ); Attribute( "g_flSfSnowAmount" ); Default( 0.6 ); Range( 0.0, 1.0 ); >;
	float3 g_vSfSnowColor < UiType( Color ); UiGroup( "Snow Cover,10/2" ); Attribute( "g_vSfSnowColor" ); Default3( 0.92, 0.95, 1.0 ); >;
	float g_flSfSnowSharpness < UiGroup( "Snow Cover,10/3" ); Attribute( "g_flSfSnowSharpness" ); Default( 3.0 ); Range( 0.1, 16.0 ); >;

	// --- Heat Distortion ---
	float g_flSfDistortStrength < UiGroup( "Heat Distortion,10/1" ); Attribute( "g_flSfDistortStrength" ); Default( 0.03 ); Range( 0.0, 0.25 ); >;
	float g_flSfDistortSpeed < UiGroup( "Heat Distortion,10/2" ); Attribute( "g_flSfDistortSpeed" ); Default( 0.4 ); Range( 0.0, 4.0 ); >;
	float g_flSfDistortScale < UiGroup( "Heat Distortion,10/3" ); Attribute( "g_flSfDistortScale" ); Default( 6.0 ); Range( 0.5, 40.0 ); >;

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
	}

	float SFPulse()
	{
		if ( g_flSfOn_pulse > 0.5 )
		{
			float wave = sin( g_flTime * g_flSfPulseSpeed * 6.2831853 ) * 0.5 + 0.5;
			return 1.0 - g_flSfPulseDepth + g_flSfPulseDepth * wave;
		}

		return 1.0;
	}

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
	}

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
	}
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		float3 vPositionOs = i.vPositionOs;

		// --- Water Surface ---
		if ( g_flSfOn_water > 0.5 )
		{
			// Two crossed sine waves - one sine alone reads as a flag, not a surface.
				float sfWavePhaseA = ( vPositionOs.x + vPositionOs.y ) * g_flSfWaveFrequency + g_flTime * g_flSfWaveSpeed;
				float sfWavePhaseB = ( vPositionOs.x - vPositionOs.y ) * g_flSfWaveFrequency * 1.7 + g_flTime * g_flSfWaveSpeed * 0.8;
				vPositionOs.z += ( sin( sfWavePhaseA ) + sin( sfWavePhaseB ) * 0.5 ) * g_flSfWaveAmplitude;
		}

		// --- Wind Deformation ---
		if ( g_flSfOn_wind > 0.5 )
		{
			// Masked by height so the base stays planted - without this the whole mesh slides sideways.
				float sfWindMask = saturate( vPositionOs.z * g_flSfWindFalloff );
				float sfWindPhase = g_flTime * g_flSfWindSpeed + ( vPositionOs.x + vPositionOs.y ) * 0.04;
				vPositionOs.xy += sin( sfWindPhase ) * g_flSfWindStrength * sfWindMask;
		}

		i.vPositionOs = vPositionOs;
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"

	float3 SFViewDir( PixelInput i )
	{
		return normalize( g_vCameraPositionWs - i.vPositionWithOffsetWs.xyz );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{

		// --- Heat Distortion ---
		if ( g_flSfOn_distort > 0.5 )
		{
			float2 sfDistortUv = i.vTextureCoords.xy * g_flSfDistortScale + float2( 0.0, -g_flTime * g_flSfDistortSpeed );
				float2 sfDistortOffset = float2( SFNoise( sfDistortUv ), SFNoise( sfDistortUv + 17.3 ) ) - 0.5;
				i.vTextureCoords.xy += sfDistortOffset * g_flSfDistortStrength;
		}

		// Init, not From: From samples the material's colour texture, and this
		// preview mesh has none — that is the red checkerboard. A solid albedo
		// is the clay; words paint on top of it.
		Material m = Material::Init();
		m.Albedo = float3( 0.62, 0.63, 0.66 );
		m.Normal = normalize( i.vNormalWs.xyz );
		m.Roughness = 0.45;
		m.Metalness = 0.0;
		m.AmbientOcclusion = 1.0;
		m.Opacity = 1.0;
		m.Emission = float3( 0.0, 0.0, 0.0 );

		// --- Emissive Output ---
		if ( g_flSfOn_emissive > 0.5 )
		{
			m.Emission += g_vSfEmissiveColor * g_flSfEmissiveStrength * SFPulse();
		}

		// --- Dissolve ---
		if ( g_flSfOn_dissolve > 0.5 )
		{
			// Cut where the noise falls below the threshold, and light the surviving rim so the cut edge
				// reads as burning rather than as a hole.
				float sfDissolveNoise = SFNoise( i.vTextureCoords.xy * g_flSfDissolveScale );
				float sfDissolveEdge = sfDissolveNoise - g_flSfDissolveAmount;

				clip( sfDissolveEdge );

				float sfDissolveRim = saturate( 1.0 - sfDissolveEdge / max( g_flSfDissolveEdge, 0.0001 ) );
				m.Emission += g_vSfDissolveEdgeColor * sfDissolveRim * 6.0;
		}

		// --- Glass Material ---
		if ( g_flSfOn_glass > 0.5 )
		{
			// Edge-on faces go opaque, face-on faces go clear - the whole reason glass reads as glass.
				float3 sfGlassView = SFViewDir( i );
				float sfGlassFacing = 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfGlassView ) );
				float sfGlassFresnel = pow( sfGlassFacing, g_flSfGlassFresnel );

				m.Albedo *= g_vSfGlassTint;
				m.Roughness = g_flSfGlassRoughness;
				m.Opacity = saturate( g_flSfGlassOpacity + sfGlassFresnel );
		}

		// --- Water Surface ---
		if ( g_flSfOn_water > 0.5 )
		{
			m.Albedo *= g_vSfWaterTint;
				m.Roughness = min( m.Roughness, 0.12 );
		}

		// --- Hit Flash ---
		if ( g_flSfOn_hitflash > 0.5 )
		{
			// Driven, not animated: the game writes g_flSfHitFlash on impact and decays it. Animating it
				// here would make every object in the scene flash on the same clock.
				m.Albedo = lerp( m.Albedo, g_vSfHitFlashColor, saturate( g_flSfHitFlash ) );
				m.Emission += g_vSfHitFlashColor * g_flSfHitFlash * 8.0;
		}

		// --- Rim Light ---
		if ( g_flSfOn_rim > 0.5 )
		{
			float3 sfRimView = SFViewDir( i );
				float sfRim = pow( 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfRimView ) ), g_flSfRimPower );
				m.Emission += g_vSfRimColor * sfRim * g_flSfRimStrength * SFPulse();
		}

		// --- Hologram ---
		if ( g_flSfOn_hologram > 0.5 )
		{
			// Scanlines in SCREEN space, so they stay put as the object turns - the giveaway that
				// something is projected rather than painted on.
				float sfHoloScan = sin( i.vPositionSs.y * g_flSfScanlineDensity - g_flTime * g_flSfScanlineSpeed * 6.0 ) * 0.5 + 0.5;
				float sfHoloFlicker = 0.9 + 0.1 * sin( g_flTime * 37.0 );

				m.Emission += g_vSfHologramColor * ( 0.35 + sfHoloScan * 0.9 ) * sfHoloFlicker * 3.0;
				m.Opacity = saturate( g_flSfHologramOpacity * ( 0.55 + sfHoloScan * 0.45 ) * sfHoloFlicker );
		}

		// --- Outline ---
		if ( g_flSfOn_outline > 0.5 )
		{
			float3 sfOutlineView = SFViewDir( i );
				float sfOutlineFacing = 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfOutlineView ) );
				float sfOutlineMask = smoothstep( 1.0 - g_flSfOutlineWidth, 1.0, sfOutlineFacing );

				m.Albedo = lerp( m.Albedo, g_vSfOutlineColor, sfOutlineMask );
				m.Emission += g_vSfOutlineColor * sfOutlineMask * g_flSfOutlineGlow;
		}

		// --- Colour Tint ---
		if ( g_flSfOn_tint > 0.5 )
		{
			m.Albedo = lerp( m.Albedo, m.Albedo * g_vSfTintColor, g_flSfTintAmount );
		}

		// --- Health Reactive ---
		if ( g_flSfOn_health > 0.5 )
		{
			// Pulses faster as it gets worse, which reads as urgency without needing a number on screen.
				float sfHealthDanger = 1.0 - saturate( g_flSfHealthFraction );
				float sfHealthBeat = sin( g_flTime * g_flSfHealthPulseSpeed * sfHealthDanger ) * 0.5 + 0.5;

				m.Albedo = lerp( m.Albedo, g_vSfHealthLowColor, sfHealthDanger * 0.6 );
				m.Emission += g_vSfHealthLowColor * sfHealthDanger * sfHealthBeat * 2.5;
		}

		// --- Loot Glow ---
		if ( g_flSfOn_loot > 0.5 )
		{
			float3 sfLootView = SFViewDir( i );
				float sfLootRim = pow( 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfLootView ) ), 2.5 );
				m.Emission += SFRarityColor( g_flSfRarity ) * sfLootRim * g_flSfLootGlow * SFPulse();
		}

		// --- Interactable Highlight ---
		if ( g_flSfOn_interactable > 0.5 )
		{
			float3 sfHighlightView = SFViewDir( i );
				float sfHighlightRim = pow( 1.0 - saturate( dot( normalize( i.vNormalWs.xyz ), sfHighlightView ) ), 2.0 );
				m.Emission += g_vSfHighlightColor * sfHighlightRim * g_flSfHighlightStrength * saturate( g_flSfHighlight ) * SFPulse();
		}

		// --- Team Colour ---
		if ( g_flSfOn_team > 0.5 )
		{
			m.Albedo = lerp( m.Albedo, SFTeamColor( g_flSfTeamIndex ), g_flSfTeamBlend );
		}

		// --- Snow Cover ---
		if ( g_flSfOn_snow > 0.5 )
		{
			// World up, not object up: snow settles the same way however the prop was authored or rotated.
				float sfSnowFacing = saturate( dot( normalize( i.vNormalWs.xyz ), float3( 0.0, 0.0, 1.0 ) ) );
				float sfSnowMask = saturate( pow( sfSnowFacing, g_flSfSnowSharpness ) * g_flSfSnowAmount * 2.0 );

				m.Albedo = lerp( m.Albedo, g_vSfSnowColor, sfSnowMask );
				m.Roughness = lerp( m.Roughness, 0.85, sfSnowMask );
		}

		float4 result = ShadingModelStandard::Shade( i, m );

		// --- Toon Lighting ---
		if ( g_flSfOn_toon > 0.5 )
		{
			float sfToonLum = dot( result.rgb, float3( 0.299, 0.587, 0.114 ) );

				if ( sfToonLum > 0.0001 )
				{
					float sfToonSteps = max( g_flSfToonSteps, 1.0 );
					float sfToonQuant = floor( sfToonLum * sfToonSteps + 0.5 ) / sfToonSteps;
					float sfToonScale = lerp( 1.0, sfToonQuant / sfToonLum, saturate( g_flSfToonBoost ) );
					result.rgb *= sfToonScale;
				}
		}

		return result;
	}
}
