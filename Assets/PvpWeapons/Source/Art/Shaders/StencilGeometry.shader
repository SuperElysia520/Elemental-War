Shader "StencilGeometry"
{
    Properties
    {
        [Header(Shared Lit Inputs All Render Pipelines)]
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}

        [Normal] _NormalMap("Normal Map", 2D) = "bump" {}
        _NormalStrength("Normal Strength (0 disables)", Range(0,2)) = 1

        // One packed map for HDRP, URP and Built-in.
        // R = Metallic, G = Ambient Occlusion, B = Detail Mask, A = Smoothness or Roughness.
        _MaskMap("Mask Map (R Metallic, G AO, B Detail, A Smoothness/Roughness)", 2D) = "white" {}
        _Metallic("Metallic Strength", Range(0,1)) = 0
        _Smoothness("Smoothness Strength", Range(0,1)) = 0.5
        [Toggle] _InvertSmoothness("Mask Alpha Is Roughness", Float) = 0
        _OcclusionStrength("Occlusion Strength", Range(0,1)) = 1

        [HDR] _EmissiveColor("Emission Color", Color) = (0,0,0,0)
        _EmissionMap("Emission Map", 2D) = "white" {}

        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5

        [Header(Stencil Test)]
        [HideInInspector] [IntRange] _StencilRef("Stencil Reference", Range(1,255)) = 64
        [HideInInspector] [IntRange] _StencilReadMask("Stencil Read Mask", Range(1,255)) = 64
        [HideInInspector] [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp("Stencil Comparison", Float) = 6
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2

        // HDRP Lit defaults required by LitData.hlsl
        [HideInInspector] _MetallicRemapMin("", Float) = 0
        [HideInInspector] _MetallicRemapMax("", Float) = 1
        [HideInInspector] _SmoothnessRemapMin("", Float) = 0
        [HideInInspector] _SmoothnessRemapMax("", Float) = 1
        [HideInInspector] _AlphaRemapMin("", Float) = 0
        [HideInInspector] _AlphaRemapMax("", Float) = 1
        [HideInInspector] _AORemapMin("", Float) = 0
        [HideInInspector] _AORemapMax("", Float) = 1
        [HideInInspector] _NormalMapOS("", 2D) = "white" {}
        [HideInInspector] _BentNormalMap("", 2D) = "bump" {}
        [HideInInspector] _BentNormalMapOS("", 2D) = "white" {}
        [HideInInspector] _HeightMap("", 2D) = "black" {}
        [HideInInspector] _HeightAmplitude("", Float) = 0.02
        [HideInInspector] _HeightCenter("", Float) = 0.5
        [HideInInspector] _HeightMapParametrization("", Int) = 0
        [HideInInspector] _HeightOffset("", Float) = 0
        [HideInInspector] _HeightMin("", Float) = -1
        [HideInInspector] _HeightMax("", Float) = 1
        [HideInInspector] _HeightTessAmplitude("", Float) = 2
        [HideInInspector] _HeightTessCenter("", Float) = 0.5
        [HideInInspector] _HeightPoMAmplitude("", Float) = 2
        [HideInInspector] _DetailMap("", 2D) = "linearGrey" {}
        [HideInInspector] _DetailAlbedoScale("", Float) = 1
        [HideInInspector] _DetailNormalScale("", Float) = 1
        [HideInInspector] _DetailSmoothnessScale("", Float) = 1
        [HideInInspector] _TangentMap("", 2D) = "bump" {}
        [HideInInspector] _TangentMapOS("", 2D) = "white" {}
        [HideInInspector] _Anisotropy("", Float) = 0
        [HideInInspector] _AnisotropyMap("", 2D) = "white" {}
        [HideInInspector] _SubsurfaceMask("", Float) = 1
        [HideInInspector] _SubsurfaceMaskMap("", 2D) = "white" {}
        [HideInInspector] _TransmissionMask("", Float) = 1
        [HideInInspector] _TransmissionMaskMap("", 2D) = "white" {}
        [HideInInspector] _Thickness("", Float) = 1
        [HideInInspector] _ThicknessMap("", 2D) = "white" {}
        [HideInInspector] _ThicknessRemap("", Vector) = (0,1,0,0)
        [HideInInspector] _IridescenceThickness("", Float) = 1
        [HideInInspector] _IridescenceThicknessMap("", 2D) = "white" {}
        [HideInInspector] _IridescenceThicknessRemap("", Vector) = (0,1,0,0)
        [HideInInspector] _IridescenceMask("", Float) = 1
        [HideInInspector] _IridescenceMaskMap("", 2D) = "white" {}
        [HideInInspector] _CoatMask("", Float) = 0
        [HideInInspector] _CoatMaskMap("", 2D) = "white" {}
        [HideInInspector] _EnergyConservingSpecularColor("", Float) = 1
        [HideInInspector] _SpecularColor("", Color) = (1,1,1,1)
        [HideInInspector] _SpecularColorMap("", 2D) = "white" {}
        [HideInInspector] _SpecularOcclusionMode("", Int) = 1
        [HideInInspector] _EmissiveColorLDR("", Color) = (0,0,0,0)
        [HideInInspector] _AlbedoAffectEmissive("", Float) = 0
        [HideInInspector] _EmissiveIntensityUnit("", Int) = 0
        [HideInInspector] _UseEmissiveIntensity("", Int) = 0
        [HideInInspector] _EmissiveIntensity("", Float) = 1
        [HideInInspector] _EmissiveExposureWeight("", Float) = 1
        [HideInInspector] _UseShadowThreshold("", Float) = 0
        [HideInInspector] _AlphaCutoffEnable("", Float) = 0
        [HideInInspector] _AlphaCutoffShadow("", Float) = 0.5
        [HideInInspector] _AlphaCutoffPrepass("", Float) = 0.5
        [HideInInspector] _AlphaCutoffPostpass("", Float) = 0.5
        [HideInInspector] _TransparentDepthPrepassEnable("", Float) = 0
        [HideInInspector] _TransparentBackfaceEnable("", Float) = 0
        [HideInInspector] _TransparentDepthPostpassEnable("", Float) = 0
        [HideInInspector] _TransparentSortPriority("", Float) = 0
        [HideInInspector] _RefractionModel("", Int) = 0
        [HideInInspector] _Ior("", Float) = 1.5
        [HideInInspector] _TransmittanceColor("", Color) = (1,1,1,1)
        [HideInInspector] _TransmittanceColorMap("", 2D) = "white" {}
        [HideInInspector] _ATDistance("", Float) = 1
        [HideInInspector] _TransparentWritingMotionVec("", Float) = 0
        [HideInInspector] _PerPixelSorting("", Float) = 0
        [HideInInspector] _SurfaceType("", Float) = 0
        [HideInInspector] _BlendMode("", Float) = 0
        [HideInInspector] _SrcBlend("", Float) = 1
        [HideInInspector] _DstBlend("", Float) = 0
        [HideInInspector] _DstBlend2("", Float) = 0
        [HideInInspector] _AlphaSrcBlend("", Float) = 1
        [HideInInspector] _AlphaDstBlend("", Float) = 0
        [HideInInspector] _ZWrite("", Float) = 1
        [HideInInspector] _TransparentZWrite("", Float) = 0
        [HideInInspector] _CullMode("", Float) = 2
        [HideInInspector] _CullModeForward("", Float) = 2
        [HideInInspector] _TransparentCullMode("", Int) = 2
        [HideInInspector] _OpaqueCullMode("", Int) = 2
        [HideInInspector] _ZTestDepthEqualForOpaque("", Int) = 4
        [HideInInspector] _ZTestGBuffer("", Int) = 4
        [HideInInspector] _ZTestTransparent("", Int) = 4
        [HideInInspector] _EnableFogOnTransparent("", Float) = 1
        [HideInInspector] _EnableBlendModePreserveSpecularLighting("", Float) = 1
        [HideInInspector] _DoubleSidedEnable("", Float) = 0
        [HideInInspector] _DoubleSidedNormalMode("", Float) = 1
        [HideInInspector] _DoubleSidedConstants("", Vector) = (1,1,-1,0)
        [HideInInspector] _DoubleSidedGIMode("", Float) = 0
        [HideInInspector] _UVBase("", Float) = 0
        [HideInInspector] _ObjectSpaceUVMapping("", Float) = 0
        [HideInInspector] _TexWorldScale("", Float) = 1
        [HideInInspector] _InvTilingScale("", Float) = 1
        [HideInInspector] _UVMappingMask("", Color) = (1,0,0,0)
        [HideInInspector] _NormalMapSpace("", Float) = 0
        [HideInInspector] _MaterialID("", Int) = 1
        [HideInInspector] _TransmissionEnable("", Float) = 1
        [HideInInspector] _DisplacementMode("", Int) = 0
        [HideInInspector] _DisplacementLockObjectScale("", Float) = 1
        [HideInInspector] _DisplacementLockTilingScale("", Float) = 1
        [HideInInspector] _DepthOffsetEnable("", Float) = 0
        [HideInInspector] _EnableGeometricSpecularAA("", Float) = 0
        [HideInInspector] _SpecularAAScreenSpaceVariance("", Float) = 0.1
        [HideInInspector] _SpecularAAThreshold("", Float) = 0.2
        [HideInInspector] _PPDMinSamples("", Float) = 5
        [HideInInspector] _PPDMaxSamples("", Float) = 15
        [HideInInspector] _PPDLodThreshold("", Float) = 5
        [HideInInspector] _PPDPrimitiveLength("", Float) = 1
        [HideInInspector] _PPDPrimitiveWidth("", Float) = 1
        [HideInInspector] _InvPrimScale("", Vector) = (1,1,0,0)
        [HideInInspector] _UVDetail("", Float) = 0
        [HideInInspector] _UVDetailsMappingMask("", Color) = (1,0,0,0)
        [HideInInspector] _LinkDetailsWithBase("", Float) = 1
        [HideInInspector] _EmissiveColorMode("", Float) = 1
        [HideInInspector] _UVEmissive("", Float) = 0
        [HideInInspector] _ObjectSpaceUVMappingEmissive("", Float) = 0
        [HideInInspector] _TexWorldScaleEmissive("", Float) = 1
        [HideInInspector] _UVMappingMaskEmissive("", Color) = (1,0,0,0)
        [HideInInspector] _MainTex("", 2D) = "white" {}
        [HideInInspector] _Color("", Color) = (1,1,1,1)
        [HideInInspector] _SupportDecals("", Float) = 1
        [HideInInspector] _ReceivesSSR("", Float) = 1
        [HideInInspector] _ReceivesSSRTransparent("", Float) = 0
        [HideInInspector] _AddPrecomputedVelocity("", Float) = 0
        [HideInInspector] _RayTracing("", Float) = 0
        [HideInInspector] _DiffusionProfile("", Int) = 0
        [HideInInspector] _DiffusionProfileAsset("", Vector) = (0,0,0,0)
        [HideInInspector] _DiffusionProfileHash("", Float) = 0
        [HideInInspector] _ZClip("", Float) = 1
        [HideInInspector] _Surface("URP Surface Type", Float) = 0
        [HideInInspector][NoScaleOffset] unity_Lightmaps("", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset] unity_LightmapsInd("", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset] unity_ShadowMasks("", 2DArray) = "" {}
    }


    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline"="HDRenderPipeline" "RenderType"="HDLitShader" "Queue"="Geometry" }


        Pass
        {
            Name "DepthForwardOnly"
            Tags { "LightMode"="DepthForwardOnly" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp [_StencilComp]
                Pass Keep
            }

            HLSLPROGRAM
                #pragma target 4.5

                #define SUPPORT_GLOBAL_MIP_BIAS
                #define HAVE_VERTEX_MODIFICATION
                #define SUPPORT_BLENDMODE_PRESERVE_SPECULAR_LIGHTING
                #define _NORMALMAP_TANGENT_SPACE
                #define _NORMALMAP
                #ifndef _SURFACE_TYPE_TRANSPARENT
                    #define _DEFERRED_CAPABLE_MATERIAL
                #endif
                #ifdef _HEIGHTMAP
                    #define _CONSERVATIVE_DEPTH_OFFSET
                #endif

                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"

                // Shared material properties mapped to HDRP Lit's internal names.
                #define _BaseColorMap _BaseMap
                #define sampler_BaseColorMap sampler_BaseMap
                #define _BaseColorMap_ST _BaseMap_ST
                #define _BaseColorMap_TexelSize _BaseMap_TexelSize
                #define _NormalScale _NormalStrength
                #define _SmoothnessRemapMin _InvertSmoothness
                #define _AlphaCutoff _Cutoff
                #define _AORemapMin _OcclusionStrength
                #define _EmissiveColorMap _EmissionMap
                #define sampler_EmissiveColorMap sampler_EmissionMap
                #define _EmissiveColorMap_ST _EmissionMap_ST
                #define _EmissiveColorMap_TexelSize _EmissionMap_TexelSize

                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitProperties.hlsl"

                // Shared alpha channel can contain either smoothness or roughness.
                #undef _AORemapMin
                #undef _SmoothnessRemapMin
                #define _AORemapMin (1.0 - _OcclusionStrength)
                #define _AORemapMax 1.0
                #define _MetallicRemapMin 0.0
                #define _MetallicRemapMax _Metallic
                #define _SmoothnessRemapMin _InvertSmoothness
                #define _SmoothnessRemapMax lerp(_Smoothness, (1.0 - _Smoothness), _InvertSmoothness)
                #define _MASKMAP
                #define _EMISSIVE_COLOR_MAP
                #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
                #pragma multi_compile_instancing
                #pragma instancing_options renderinglayer
                #pragma multi_compile _ WRITE_NORMAL_BUFFER
                #pragma multi_compile_fragment _ WRITE_MSAA_DEPTH
                #pragma shader_feature_local _ALPHATEST_ON

                #define SHADERPASS SHADERPASS_DEPTH_ONLY
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.hlsl"
                #ifdef WRITE_NORMAL_BUFFER
                    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/ShaderPass/LitSharePass.hlsl"
                #else
                    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/ShaderPass/LitDepthPass.hlsl"
                #endif
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitData.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassDepthOnly.hlsl"
                #pragma vertex Vert
                #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode"="ForwardOnly" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp [_StencilComp]
                Pass Keep
            }

            HLSLPROGRAM
                #pragma target 4.5

                #define SUPPORT_GLOBAL_MIP_BIAS
                #define HAVE_VERTEX_MODIFICATION
                #define SUPPORT_BLENDMODE_PRESERVE_SPECULAR_LIGHTING
                #define _NORMALMAP_TANGENT_SPACE
                #define _NORMALMAP
                #ifndef _SURFACE_TYPE_TRANSPARENT
                    #define _DEFERRED_CAPABLE_MATERIAL
                #endif
                #ifdef _HEIGHTMAP
                    #define _CONSERVATIVE_DEPTH_OFFSET
                #endif

                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"

                // Shared material properties mapped to HDRP Lit's internal names.
                #define _BaseColorMap _BaseMap
                #define sampler_BaseColorMap sampler_BaseMap
                #define _BaseColorMap_ST _BaseMap_ST
                #define _BaseColorMap_TexelSize _BaseMap_TexelSize
                #define _NormalScale _NormalStrength
                #define _SmoothnessRemapMin _InvertSmoothness
                #define _AlphaCutoff _Cutoff
                #define _AORemapMin _OcclusionStrength
                #define _EmissiveColorMap _EmissionMap
                #define sampler_EmissiveColorMap sampler_EmissionMap
                #define _EmissiveColorMap_ST _EmissionMap_ST
                #define _EmissiveColorMap_TexelSize _EmissionMap_TexelSize

                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitProperties.hlsl"

                // Shared alpha channel can contain either smoothness or roughness.
                #undef _AORemapMin
                #undef _SmoothnessRemapMin
                #define _AORemapMin (1.0 - _OcclusionStrength)
                #define _AORemapMax 1.0
                #define _MetallicRemapMin 0.0
                #define _MetallicRemapMax _Metallic
                #define _SmoothnessRemapMin _InvertSmoothness
                #define _SmoothnessRemapMax lerp(_Smoothness, (1.0 - _Smoothness), _InvertSmoothness)
                #define _MASKMAP
                #define _EMISSIVE_COLOR_MAP
                #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
                #pragma multi_compile_instancing
                #pragma instancing_options renderinglayer
                #pragma multi_compile _ LIGHTMAP_ON
                #pragma multi_compile _ LIGHTMAP_BICUBIC_SAMPLING
                #pragma multi_compile _ DIRLIGHTMAP_COMBINED
                #pragma multi_compile_fragment _ SHADOWS_SHADOWMASK
                #pragma multi_compile_fragment SCREEN_SPACE_SHADOWS_OFF SCREEN_SPACE_SHADOWS_ON
                #pragma multi_compile_fragment PUNCTUAL_SHADOW_LOW PUNCTUAL_SHADOW_MEDIUM PUNCTUAL_SHADOW_HIGH
                #pragma multi_compile_fragment DIRECTIONAL_SHADOW_LOW DIRECTIONAL_SHADOW_MEDIUM DIRECTIONAL_SHADOW_HIGH
                #pragma multi_compile_fragment AREA_SHADOW_MEDIUM AREA_SHADOW_HIGH
                #pragma multi_compile_fragment USE_FPTL_LIGHTLIST USE_CLUSTERED_LIGHTLIST
                #pragma shader_feature_local _ALPHATEST_ON

                #ifndef SHADER_STAGE_FRAGMENT
                    #define SHADOW_LOW
                    #ifndef USE_FPTL_LIGHTLIST
                        #define USE_FPTL_LIGHTLIST
                    #endif
                #endif

                #define SHADERPASS SHADERPASS_FORWARD
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Lighting.hlsl"
                #define HAS_LIGHTLOOP
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightLoop/LightLoopDef.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightLoop/LightLoop.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/ShaderPass/LitSharePass.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitData.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassForward.hlsl"
                #pragma vertex Vert
                #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull [_Cull]
            ZClip On
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
                #pragma target 4.5

                #define SUPPORT_GLOBAL_MIP_BIAS
                #define HAVE_VERTEX_MODIFICATION
                #define SUPPORT_BLENDMODE_PRESERVE_SPECULAR_LIGHTING
                #define _NORMALMAP_TANGENT_SPACE
                #define _NORMALMAP
                #ifndef _SURFACE_TYPE_TRANSPARENT
                    #define _DEFERRED_CAPABLE_MATERIAL
                #endif
                #ifdef _HEIGHTMAP
                    #define _CONSERVATIVE_DEPTH_OFFSET
                #endif

                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"

                // Shared material properties mapped to HDRP Lit's internal names.
                #define _BaseColorMap _BaseMap
                #define sampler_BaseColorMap sampler_BaseMap
                #define _BaseColorMap_ST _BaseMap_ST
                #define _BaseColorMap_TexelSize _BaseMap_TexelSize
                #define _NormalScale _NormalStrength
                #define _SmoothnessRemapMin _InvertSmoothness
                #define _AlphaCutoff _Cutoff
                #define _AORemapMin _OcclusionStrength
                #define _EmissiveColorMap _EmissionMap
                #define sampler_EmissiveColorMap sampler_EmissionMap
                #define _EmissiveColorMap_ST _EmissionMap_ST
                #define _EmissiveColorMap_TexelSize _EmissionMap_TexelSize

                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitProperties.hlsl"

                // Shared alpha channel can contain either smoothness or roughness.
                #undef _AORemapMin
                #undef _SmoothnessRemapMin
                #define _AORemapMin (1.0 - _OcclusionStrength)
                #define _AORemapMax 1.0
                #define _MetallicRemapMin 0.0
                #define _MetallicRemapMax _Metallic
                #define _SmoothnessRemapMin _InvertSmoothness
                #define _SmoothnessRemapMax lerp(_Smoothness, (1.0 - _Smoothness), _InvertSmoothness)
                #define _MASKMAP
                #define _EMISSIVE_COLOR_MAP
                #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
                #pragma multi_compile_instancing
                #pragma instancing_options renderinglayer
                #pragma shader_feature_local _ALPHATEST_ON
                #define SHADERPASS SHADERPASS_SHADOWS
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/ShaderPass/LitDepthPass.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitData.hlsl"
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassDepthOnly.hlsl"
                #pragma vertex Vert
                #pragma fragment Frag
            ENDHLSL
        }

        UsePass "HDRP/Lit/META"
    }

    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Opaque"
            "Queue"="Geometry"
            "UniversalMaterialType"="Lit"
            "IgnoreProjector"="True"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }

            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull [_Cull]
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp [_StencilComp]
                Pass Keep
            }

            HLSLPROGRAM
                #pragma target 3.5
                #pragma vertex LitPassVertex
                #pragma fragment LitPassFragment

                #pragma shader_feature_local_fragment _ALPHATEST_ON

                #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
                #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
                #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
                #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
                #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
                #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
                #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
                #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
                #pragma multi_compile_fragment _ _LIGHT_COOKIES
                #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

                #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
                #pragma multi_compile _ SHADOWS_SHADOWMASK
                #pragma multi_compile _ DIRLIGHTMAP_COMBINED
                #pragma multi_compile _ LIGHTMAP_ON
                #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
                #pragma multi_compile _ DYNAMICLIGHTMAP_ON
                #pragma multi_compile_fog

                #pragma multi_compile_instancing
                #pragma instancing_options renderinglayer

                // URP material declarations are deliberately local to this pass.
                // Do not move them to HLSLINCLUDE: that leaks URP headers into HDRP.
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"

                #define _BumpMap _NormalMap
                #define _NORMALMAP 1
                #define sampler_BumpMap sampler_NormalMap
                #define _BumpScale _NormalStrength

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

                TEXTURE2D(_MaskMap); SAMPLER(sampler_MaskMap);

                CBUFFER_START(UnityPerMaterial)
                    float4 _BaseMap_ST;
                    half4 _BaseColor;
                    half4 _EmissiveColor;
                    half _Cutoff;
                    half _Metallic;
                    half _Smoothness;
                    half _InvertSmoothness;
                    half _NormalStrength;
                    half _OcclusionStrength;
                    half _Surface;
                CBUFFER_END

                inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData surfaceData)
                {
                    ZERO_INITIALIZE(SurfaceData, surfaceData);

                    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
                    surfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);
                    surfaceData.albedo = AlphaModulate(albedoAlpha.rgb * _BaseColor.rgb, surfaceData.alpha);

                    half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uv);

                    surfaceData.metallic = saturate(mask.r * _Metallic);
                    surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                    half smoothFromMap = saturate(mask.a * _Smoothness);
                    surfaceData.smoothness = lerp(smoothFromMap, 1.0h - smoothFromMap, saturate(_InvertSmoothness));
                    surfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
                    surfaceData.occlusion = lerp(1.0h, mask.g, _OcclusionStrength);
                    surfaceData.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, uv).rgb * _EmissiveColor.rgb;
                    surfaceData.clearCoatMask = 0.0h;
                    surfaceData.clearCoatSmoothness = 0.0h;
                }

                // URP 6.5 calls the parameterless overload; older URP uses the half overload.
                inline bool IsSurfaceTypeTransparent() { return _Surface > 0.0h; }

                // This pass supplies its own material CBUFFER and surface initializer.
                // Newer LitForwardPass versions include LitInput, which would redefine both.
                #define UNIVERSAL_LIT_INPUT_INCLUDED
                #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp [_StencilComp]
                Pass Keep
            }

            HLSLPROGRAM
                #pragma target 2.0
                #pragma vertex ShadowPassVertex
                #pragma fragment ShadowPassFragment
                #pragma shader_feature_local _ALPHATEST_ON
                #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
                #pragma multi_compile_instancing

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

                CBUFFER_START(UnityPerMaterial)
                    float4 _BaseMap_ST;
                    half4 _BaseColor;
                    half4 _EmissiveColor;
                    half _Cutoff;
                    half _Metallic;
                    half _Smoothness;
                    half _InvertSmoothness;
                    half _NormalStrength;
                    half _OcclusionStrength;
                    half _Surface;
                CBUFFER_END

                #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp [_StencilComp]
                Pass Keep
            }

            HLSLPROGRAM
                #pragma target 2.0
                #pragma vertex DepthOnlyVertex
                #pragma fragment DepthOnlyFragment
                #pragma shader_feature_local _ALPHATEST_ON
                #pragma multi_compile_instancing

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

                CBUFFER_START(UnityPerMaterial)
                    float4 _BaseMap_ST;
                    half4 _BaseColor;
                    half4 _EmissiveColor;
                    half _Cutoff;
                    half _Metallic;
                    half _Smoothness;
                    half _InvertSmoothness;
                    half _NormalStrength;
                    half _OcclusionStrength;
                    half _Surface;
                CBUFFER_END

                #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            Cull [_Cull]
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp [_StencilComp]
                Pass Keep
            }

            HLSLPROGRAM
                #pragma target 2.0
                #pragma vertex DepthNormalsVertex
                #pragma fragment DepthNormalsFragment
                #pragma shader_feature_local _ALPHATEST_ON
                #pragma multi_compile_instancing

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"

                #define _BumpMap _NormalMap
                #define _NORMALMAP 1
                #define sampler_BumpMap sampler_NormalMap
                #define _BumpScale _NormalStrength

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

                CBUFFER_START(UnityPerMaterial)
                    float4 _BaseMap_ST;
                    half4 _BaseColor;
                    half4 _EmissiveColor;
                    half _Cutoff;
                    half _Metallic;
                    half _Smoothness;
                    half _InvertSmoothness;
                    half _NormalStrength;
                    half _OcclusionStrength;
                    half _Surface;
                CBUFFER_END

                #include "Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Meta"
            Tags { "LightMode"="Meta" }
            Cull Off

            HLSLPROGRAM
                #pragma target 2.0
                #pragma vertex UniversalVertexMeta
                #pragma fragment UniversalFragmentMetaLit
                #pragma shader_feature_local_fragment _ALPHATEST_ON
                #pragma shader_feature EDITOR_VISUALIZATION

                // URP material declarations are deliberately local to this pass.
                // Do not move them to HLSLINCLUDE: that leaks URP headers into HDRP.
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"

                #define _BumpMap _NormalMap
                #define _NORMALMAP 1
                #define sampler_BumpMap sampler_NormalMap
                #define _BumpScale _NormalStrength

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

                TEXTURE2D(_MaskMap); SAMPLER(sampler_MaskMap);

                CBUFFER_START(UnityPerMaterial)
                    float4 _BaseMap_ST;
                    half4 _BaseColor;
                    half4 _EmissiveColor;
                    half _Cutoff;
                    half _Metallic;
                    half _Smoothness;
                    half _InvertSmoothness;
                    half _NormalStrength;
                    half _OcclusionStrength;
                    half _Surface;
                CBUFFER_END

                inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData surfaceData)
                {
                    ZERO_INITIALIZE(SurfaceData, surfaceData);

                    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
                    surfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);
                    surfaceData.albedo = AlphaModulate(albedoAlpha.rgb * _BaseColor.rgb, surfaceData.alpha);

                    half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uv);

                    surfaceData.metallic = saturate(mask.r * _Metallic);
                    surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                    half smoothFromMap = saturate(mask.a * _Smoothness);
                    surfaceData.smoothness = lerp(smoothFromMap, 1.0h - smoothFromMap, saturate(_InvertSmoothness));
                    surfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
                    surfaceData.occlusion = lerp(1.0h, mask.g, _OcclusionStrength);
                    surfaceData.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, uv).rgb * _EmissiveColor.rgb;
                    surfaceData.clearCoatMask = 0.0h;
                    surfaceData.clearCoatSmoothness = 0.0h;
                }

                #include "Packages/com.unity.render-pipelines.universal/Shaders/LitMetaPass.hlsl"
            ENDHLSL
        }
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull [_Cull]
        Stencil { Ref [_StencilRef] ReadMask [_StencilReadMask] Comp [_StencilComp] Pass Keep }
        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma shader_feature_local _ALPHATEST_ON
        #pragma multi_compile_instancing
        sampler2D _BaseMap,_NormalMap,_MaskMap,_EmissionMap;
        fixed4 _BaseColor,_EmissiveColor;half _NormalStrength,_Metallic,_Smoothness,_InvertSmoothness,_OcclusionStrength,_Cutoff;
        struct Input{float2 uv_BaseMap;};
        void surf(Input input,inout SurfaceOutputStandard output){fixed4 albedo=tex2D(_BaseMap,input.uv_BaseMap)*_BaseColor;
        #if defined(_ALPHATEST_ON)
            clip(albedo.a-_Cutoff);
        #endif
            half4 mask=tex2D(_MaskMap,input.uv_BaseMap);output.Albedo=albedo.rgb;output.Alpha=albedo.a;
            output.Normal=UnpackScaleNormal(tex2D(_NormalMap,input.uv_BaseMap),_NormalStrength);
            output.Metallic=mask.r*_Metallic;half smoothFromMap=saturate(mask.a*_Smoothness);output.Smoothness=lerp(smoothFromMap,1-smoothFromMap,saturate(_InvertSmoothness));output.Occlusion=lerp(1,mask.g,_OcclusionStrength);output.Emission=tex2D(_EmissionMap,input.uv_BaseMap).rgb*_EmissiveColor.rgb;}
        ENDCG
    }
    FallBack "Standard"
}
