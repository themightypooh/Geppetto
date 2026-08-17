// Pixelated arms -- chunky texels on the model's OWN skin, with a PS1-style ragged silhouette.
//
// Why this exists: s&box's built-in Pixelate component pixelates the WHOLE screen in screen
// space, and there is no clean way to scope it to one object (render-to-texture needs internal
// ViewSetup). This shader pixelates at the material level instead, so applying it as a material
// override on the viewmodel arms renderer chunky-ifies ONLY the arms and leaves the world
// untouched. ViewArmsComponent puts it on while its Pixelate toggle is on.
//
// EVERY VARIABLE IN HERE IS PREFIXED Arm. Not a style rule -- these are globals in a shared
// namespace with the engine's material system, and a plain name like g_vColor or g_flRoughness
// can collide with something the engine already declares and quietly lose. The arms rendered as
// saturated red and yellow for several rounds while the albedo was multiplied by a g_vColor that
// wasn't the one this file thought it was declaring. Keep new parameters prefixed.
//
// The knobs:
//
//   ArmVertSnap   THE RAGGED EDGE. Classic PS1 vertex snapping: after projection, each vertex is
//                 quantised to a grid of N screen pixels, so the silhouette can only land on grid
//                 corners and comes out stair-stepped and wobbling as the arms move. Doing it
//                 post-projection rather than in object space is what makes the steps a constant
//                 size on screen and gives the authentic swim -- object-space snapping just
//                 shatters the mesh. w > 0 is checked so vertices behind the eye are left alone;
//                 dividing by a negative w mirrors them across the screen.
//   ArmTexelGrid  THE CHUNKY SKIN. Quantises the UV to a fixed grid, so the 2048px arm texture
//                 reads as a handful of big square texels locked to the surface, sliding with it
//                 as it moves. No derivatives involved, which is why it can't speckle.
//   ArmPixelBlock optional screen-space quantisation on top, locking texels to the SCREEN instead
//                 of the surface. Off by default -- it reconstructs the UV at the block centre
//                 from ddx/ddy, and derivatives are meaningless across a triangle edge.
//   ArmObjSnap    object-space vertex snapping. Off by default; see the warning below.
//   ArmFlatShade  per-triangle face normal for hard low-poly facets.
//   ArmColorSteps posterizes the final colour -> banded retro lighting. Off: it quantises the
//                 SHADED result, which shifts the skin tone off what the texture says.
//   ArmDebug      1 = dump the raw texture sample, unlit and unquantised. The fastest way to tell
//                 a texture problem from a shading problem apart, since they look alike on screen.
//
// FAILURE MODES THAT ALREADY HAPPENED HERE. Every one of them looked like a wrong texture, which
// is why they are written down -- three separate causes produce near-identical saturated garbage,
// and guessing between them by eye burned several rounds:
//
//   Solid black arms under a white crosshatch. ArmFlatShade with an unguarded normalize. Object-
//   space vertex snapping collapses triangles onto a single grid point, cross() of the position
//   derivatives goes to zero, normalize( 0 ) is NaN, and a NaN normal poisons the shading model.
//   The cross product is length-checked now.
//
//   Speckled grid over the skin. Reconstructing the NORMAL at the block centre from ddx/ddy.
//   Normals are not smooth across triangle boundaries, so extrapolating one several pixels
//   sideways produces garbage on every edge in the mesh. Only the UV is extrapolated now.
//
//   Saturated red/yellow/black arms. A colliding global name -- see the note at the top. Ruled
//   out first, wrongly, in this order: the bound texture (the material's own thumbnail is plainly
//   skin, and the vtex bound is the _color one), then the overlay-pass lighting, then mip
//   selection. None of those were it. When output stops responding to which texture you bind but
//   still responds to the lighting floats, suspect the albedo path, not the texture.
//
// AND ONE THAT IS NOT A SETTING: do not snap vPositionSs or vPositionWithOffsetWs to the block
// centre. Moving them moves the tiled light-cluster and shadow-map lookups off this pixel and the
// shading model reads another pixel's lighting. Quantise the ALBEDO; leave the lighting's inputs
// alone.
//
// Every parameter is Attribute-driven rather than UiGroup-driven, matching glass_grime and the
// rest of the project: the component owns the values and pushes them every frame, so they can be
// dragged in the inspector and seen live. The .vmat carries no values at all.

HEADER
{
    Description = "Pixelated arms -- chunky texels, PS1 vertex snapping";
}

FEATURES
{
    #include "common/features.hlsl"
}

MODES
{
    Forward();
    Depth( S_MODE_DEPTH );
    ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
    #include "common/shared.hlsl"

    // The real arms albedo, handed over by ViewArmsComponent.
    Texture2D g_tArmSkin      < Attribute( "ArmColorTex" ); SrgbRead( true ); >;

    // 0 until the component has found a texture -- fall back to the flat tint rather than sampling
    // an unbound texture.
    float  g_flArmHasTex      < Attribute( "ArmHasTex" );      Default( 0.0 ); >;

    // ONLY used when there is no texture. It is deliberately not multiplied over the sampled skin:
    // a tint in the albedo path is exactly what went wrong above, and a tint on real skin is not
    // wanted anyway.
    float4 g_vArmFallback     < Attribute( "ArmPixelColor" );  Default4( 0.76, 0.66, 0.58, 1.0 ); >;

    // Pushed by the component rather than read from g_vViewportSize, because the vertex snap needs
    // it in the VERTEX stage and the engine's viewport globals aren't guaranteed there.
    float2 g_vArmScreenSize   < Attribute( "ArmScreenSize" );  Default2( 1920.0, 1080.0 ); >;

    float  g_flArmVertSnap    < Attribute( "ArmVertSnap" );    Default( 6.0 ); >;
    float  g_flArmTexelGrid   < Attribute( "ArmTexelGrid" );   Default( 180.0 ); >;
    float  g_flArmBlock       < Attribute( "ArmPixelBlock" );  Default( 0.0 ); >;
    float  g_flArmObjSnap     < Attribute( "ArmPixelSize" );   Default( 0.0 ); >;
    float  g_flArmFlatShade   < Attribute( "ArmFlatShade" );   Default( 0.0 ); >;
    float  g_flArmColorSteps  < Attribute( "ArmColorSteps" );  Default( 0.0 ); >;
    float  g_flArmDebug       < Attribute( "ArmDebug" );       Default( 0.0 ); >;
    float  g_flArmMip         < Attribute( "ArmMip" );         Default( 5.0 ); >;
    float  g_flArmColorDepth  < Attribute( "ArmColorDepth" );  Default( 0.0 ); >;

    // Which arm to throw away, by the sign of its object-space y: +1 hides the left arm, -1 the
    // right, 0 keeps both. See the note in MainVs.
    float  g_flArmHideSide    < Attribute( "ArmHideSide" );    Default( 0.0 ); >;

    float  g_flArmRough       < Attribute( "ArmRoughness" );   Default( 0.85 ); >;
    float  g_flArmMetal       < Attribute( "ArmMetalness" );   Default( 0.0 ); >;

    // 1 = the self-contained wrap-lambert below, 0 = the engine's standard shading model. These
    // arms draw on the overlay layer (RenderOptions.Overlay, Game = false), a pass after the main
    // lighting pass, so the self-lit path is the dependable one.
    float  g_flArmSelfLit     < Attribute( "ArmSelfLit" );     Default( 1.0 ); >;

    float3 g_vArmLightDir     < Attribute( "ArmLightDir" );    Default3( -0.4, -0.6, 0.7 ); >;
    float  g_flArmAmbient     < Attribute( "ArmAmbient" );     Default( 0.55 ); >;
    float  g_flArmDiffuse     < Attribute( "ArmDiffuse" );     Default( 0.75 ); >;
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
        // WHICH ARM THIS VERTEX BELONGS TO, smuggled to the pixel shader so it can drop one of them.
        //
        // Object-space y, read before any snapping. s&box is Source convention -- +y is left -- so
        // the sign alone identifies the arm, and it does so in the model's own space, which does not
        // move with the animation. A hand that reaches across the body still belongs to the arm it
        // grew from.
        //
        // Hiding an arm this way rather than by collapsing its bones is deliberate and was arrived
        // at the hard way: collapsing wrote zero-scale bone overrides, and the mirror in
        // ViewArmsComponent reads the left arm's posed bones back a frame later to drive the right
        // one. It read its own collapsed output, mirrored zero scale onto the right arm, and BOTH
        // arms vanished. Discarding in the pixel shader touches no bone state, so the loop is gone.
        //
        // vTextureCoords.zw are free -- this mesh has one UV set and only .xy is sampled.
        float flSide = i.vPositionOs.y;

        // Guarded rather than clamped: a size of zero means "off", and dividing by a tiny epsilon
        // would fold the whole mesh onto one point.
        if ( g_flArmObjSnap > 0.0001 )
            i.vPositionOs.xyz = floor( i.vPositionOs.xyz / g_flArmObjSnap ) * g_flArmObjSnap;

        PixelInput o = ProcessVertex( i );
        o.vTextureCoords.z = flSide;
        o = FinalizeVertex( o );

        // PS1 vertex snapping, in screen pixels. Perspective divide by hand, quantise, multiply w
        // back in so the rasteriser still interpolates correctly.
        if ( g_flArmVertSnap >= 1.5 && o.vPositionPs.w > 0.0 )
        {
            float2 vNdc = o.vPositionPs.xy / o.vPositionPs.w;
            float2 vPixels = ( vNdc * 0.5 + 0.5 ) * g_vArmScreenSize;

            vPixels = floor( vPixels / g_flArmVertSnap ) * g_flArmVertSnap;

            vNdc = ( vPixels / g_vArmScreenSize ) * 2.0 - 1.0;
            o.vPositionPs.xy = vNdc * o.vPositionPs.w;
        }

        return o;
    }
}

PS
{
    #include "common/pixel.hlsl"

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        // Drop the off arm. Compared against 0 rather than by sign() so a vertex sitting exactly on
        // the centre line survives either way instead of flickering between the two.
        if ( g_flArmHideSide > 0.5 && i.vTextureCoords.z > 0.0 )
            discard;

        if ( g_flArmHideSide < -0.5 && i.vTextureCoords.z < 0.0 )
            discard;

        float2 vUv = i.vTextureCoords.xy;
        float3 vNormalWs = normalize( i.vNormalWs.xyz );

        // The UV gradients BEFORE any quantisation, kept for the SampleGrad below. Taken here
        // because ddx of the quantised UV is zero inside a block and enormous at its edge, which
        // is useless for picking a mip.
        float2 vDdxUv = ddx( vUv );
        float2 vDdyUv = ddy( vUv );

        // Straight dump of the texture, no quantisation and no lighting. A texture problem and a
        // shading problem look identical on a pixelated arm, and this tells them apart in one look.
        if ( g_flArmDebug > 0.5 )
            return float4( g_tArmSkin.SampleLevel( g_sBilinearWrap, vUv, g_flArmMip ).rgb, 1.0 );

        // Optional screen-locked quantisation. Only the UV is extrapolated to the block centre --
        // extrapolating the normal as well is what produced the speckled grid.
        if ( g_flArmBlock >= 1.5 )
        {
            float2 vCentre = ( floor( i.vPositionSs.xy / g_flArmBlock ) + 0.5 ) * g_flArmBlock;
            float2 vDelta = vCentre - i.vPositionSs.xy;

            vUv += vDdxUv * vDelta.x + vDdyUv * vDelta.y;
        }

        // Surface-locked quantisation. This is the one doing the work by default.
        if ( g_flArmTexelGrid >= 2.0 )
            vUv = ( floor( vUv * g_flArmTexelGrid ) + 0.5 ) / g_flArmTexelGrid;

        // Hard facets. LENGTH-CHECKED: a snapped or degenerate triangle gives a zero-length cross
        // product, and normalize( 0 ) is NaN, which turns the whole arm black.
        if ( g_flArmFlatShade > 0.0 )
        {
            float3 vCross = cross( ddx( i.vPositionWithOffsetWs.xyz ), ddy( i.vPositionWithOffsetWs.xyz ) );

            if ( dot( vCross, vCross ) > 1e-12 )
            {
                float3 nFlat = normalize( vCross );

                // Orient to the model's own normal so back-facing triangles don't flip into shadow.
                if ( dot( nFlat, vNormalWs ) < 0.0 )
                    nFlat = -nFlat;

                vNormalWs = normalize( lerp( vNormalWs, nFlat, g_flArmFlatShade ) );
            }
        }

        // AN EXPLICIT, DELIBERATELY LOW MIP. This is not a quality compromise, it is the fix for
        // the speckle, and it took a while to see because the speckle only appeared at some angles.
        //
        // This texture is loaded at runtime and bound through a render attribute, which does not
        // register a streaming request for it -- and the material that WOULD stream it is the one
        // this shader is overriding, so it never renders and never asks. Only the always-resident
        // tail mips are actually in memory. Any sample that selects a higher mip reads unallocated
        // memory and decodes BC garbage, which is why it came and went with the viewing angle:
        // the angle is what picks the mip.
        //
        // So the mip is pinned to one the streaming system always keeps. It also happens to be
        // free chunkiness -- mip 5 of 2048 is 64x64, which is the look anyway.
        //
        // The sampled skin is used AS IS. Nothing multiplies it -- see the header.
        float3 vAlbedo = g_vArmFallback.rgb;

        if ( g_flArmHasTex > 0.5 )
            vAlbedo = g_tArmSkin.SampleLevel( g_sPointWrap, vUv, g_flArmMip ).rgb;

        float4 vColor;

        if ( g_flArmSelfLit > 0.5 )
        {
            // Wrap lambert against a fixed direction, plus ambient. Half-lambert ( *0.5 + 0.5 )
            // rather than saturate( dot ), so the shadowed side of a forearm stays readable skin
            // instead of going to black -- these are viewmodel arms, they're meant to be seen.
            float flWrap = dot( vNormalWs, normalize( g_vArmLightDir ) ) * 0.5 + 0.5;
            vColor = float4( vAlbedo * ( g_flArmAmbient + g_flArmDiffuse * flWrap ), 1.0 );
        }
        else
        {
            Material m = Material::Init();
            m.Albedo = vAlbedo;
            m.Normal = vNormalWs;
            m.Roughness = g_flArmRough;
            m.Metalness = g_flArmMetal;

            vColor = ShadingModelStandard::Shade( i, m );
        }

        // Reduced colour depth with an ordered dither -- what old hardware actually did, and what
        // sells "retro" harder than pixel size alone. Rounding to N levels per channel on its own
        // gives ugly flat bands across a forearm; the 4x4 Bayer threshold breaks each band into a
        // stipple that the eye reads as the in-between colour, so the palette drops without the
        // skin tone going with it.
        //
        // The dither cell is sized to the VERTEX SNAP grid rather than to one screen pixel. At one
        // pixel it's invisible noise at this resolution and just looks like film grain; matched to
        // the pixel grid it reads as a deliberate stipple.
        if ( g_flArmColorDepth >= 2.0 )
        {
            const float flBayer[16] =
            {
                 0.0,  8.0,  2.0, 10.0,
                12.0,  4.0, 14.0,  6.0,
                 3.0, 11.0,  1.0,  9.0,
                15.0,  7.0, 13.0,  5.0
            };

            float2 vCell = floor( i.vPositionSs.xy / max( g_flArmVertSnap, 1.0 ) );
            int iIndex = ( ( (int)vCell.y & 3 ) * 4 ) + ( (int)vCell.x & 3 );
            float flThreshold = ( flBayer[iIndex] + 0.5 ) / 16.0;

            vColor.rgb = floor( vColor.rgb * g_flArmColorDepth + flThreshold ) / g_flArmColorDepth;
        }

        // Posterize into a small number of brightness steps. Separate from the dither above and off
        // by default -- this one has no dither to hide the banding, so it flattens skin into poster
        // paint. Use ArmColorDepth for a retro palette; use this only when hard bands are wanted.
        if ( g_flArmColorSteps >= 2.0 )
            vColor.rgb = floor( vColor.rgb * g_flArmColorSteps + 0.5 ) / g_flArmColorSteps;

        return vColor;
    }
}
