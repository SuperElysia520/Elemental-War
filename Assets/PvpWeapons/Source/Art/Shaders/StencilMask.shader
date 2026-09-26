Shader "StencilMask"
{
    Properties
    {
        [HideInInspector] [MainTexture] _MainTex("Alpha Mask", 2D) = "white" {}
        [HideInInspector] [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 0
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5

        [HideInInspector] [IntRange] _StencilRef("Stencil Reference", Range(0,255)) = 64
        [HideInInspector] [IntRange] _StencilReadMask("Stencil Read Mask", Range(0,255)) = 64
        [HideInInspector] [IntRange] _StencilWriteMask("Stencil Write Mask", Range(0,255)) = 64
        [HideInInspector] [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp("Stencil Comparison", Float) = 8
        [HideInInspector] [Enum(UnityEngine.Rendering.StencilOp)] _StencilPass("Stencil Pass", Float) = 2
        [HideInInspector] [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("Depth Test", Float) = 4

        // Keep Cull Off. The camera-side test makes the mask one-sided
        // without depending on the mesh normals.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
        [Toggle] _FlipVisibleSide("Flip Visible Side", Float) = 0
    }

    // ================================================================
    // HDRP
    // Important: every pass owns its HLSL code. There is no HLSLINCLUDE,
    // so HDRP headers cannot leak into the URP program.
    // ================================================================
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }

        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "Queue" = "Geometry-10"
            "RenderType" = "Opaque"
        }

        ColorMask 0
        ZWrite Off
        ZTest [_ZTest]
        Cull [_Cull]

        Stencil
        {
            Ref [_StencilRef]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
            Comp [_StencilComp]
            Pass [_StencilPass]
            Fail Keep
            ZFail Keep
        }

        Pass
        {
            Name "DepthForwardOnly"
            Tags { "LightMode" = "DepthForwardOnly" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VertHDRPDepth
            #pragma fragment FragHDRPDepth
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float cameraSide : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Cutoff;
                float _FlipVisibleSide;
            CBUFFER_END

            Varyings VertHDRPDepth(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;

                float3 originVS = TransformWorldToView(TransformObjectToWorld(float3(0.0, 0.0, 0.0)));
                float3 forwardVS = normalize(mul((float3x3)UNITY_MATRIX_MV, float3(0.0, 0.0, 1.0)));
                output.cameraSide = dot(-originVS, forwardVS);

                return output;
            }

            half4 FragHDRPDepth(Varyings input) : SV_Target
            {
                float sideSign = (_FlipVisibleSide < 0.5) ? 1.0 : -1.0;
                clip(input.cameraSide * sideSign);

                #if defined(_ALPHATEST_ON)
                    clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a - _Cutoff);
                #endif

                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VertHDRPForward
            #pragma fragment FragHDRPForward
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float cameraSide : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Cutoff;
                float _FlipVisibleSide;
            CBUFFER_END

            Varyings VertHDRPForward(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;

                float3 originVS = TransformWorldToView(TransformObjectToWorld(float3(0.0, 0.0, 0.0)));
                float3 forwardVS = normalize(mul((float3x3)UNITY_MATRIX_MV, float3(0.0, 0.0, 1.0)));
                output.cameraSide = dot(-originVS, forwardVS);

                return output;
            }

            half4 FragHDRPForward(Varyings input) : SV_Target
            {
                float sideSign = (_FlipVisibleSide < 0.5) ? 1.0 : -1.0;
                clip(input.cameraSide * sideSign);

                #if defined(_ALPHATEST_ON)
                    clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a - _Cutoff);
                #endif

                return 0;
            }
            ENDHLSL
        }
    }

    // ================================================================
    // URP
    // ================================================================
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }

        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-10"
            "RenderType" = "Opaque"
        }

        ColorMask 0
        ZWrite Off
        ZTest [_ZTest]
        Cull [_Cull]

        Stencil
        {
            Ref [_StencilRef]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
            Comp [_StencilComp]
            Pass [_StencilPass]
            Fail Keep
            ZFail Keep
        }

        Pass
        {
            Name "StencilMask"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex VertURP
            #pragma fragment FragURP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float cameraSide : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Cutoff;
                float _FlipVisibleSide;
            CBUFFER_END

            Varyings VertURP(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;

                float3 originVS = TransformWorldToView(TransformObjectToWorld(float3(0.0, 0.0, 0.0)));
                float3 forwardVS = normalize(mul((float3x3)UNITY_MATRIX_MV, float3(0.0, 0.0, 1.0)));
                output.cameraSide = dot(-originVS, forwardVS);

                return output;
            }

            half4 FragURP(Varyings input) : SV_Target
            {
                float sideSign = (_FlipVisibleSide < 0.5) ? 1.0 : -1.0;
                clip(input.cameraSide * sideSign);

                #if defined(_ALPHATEST_ON)
                    clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a - _Cutoff);
                #endif

                return 0;
            }
            ENDHLSL
        }
    }

    // ================================================================
    // Built-in Render Pipeline
    // ================================================================
    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-10"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        ColorMask 0
        ZWrite Off
        ZTest [_ZTest]
        Cull [_Cull]

        Stencil
        {
            Ref [_StencilRef]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
            Comp [_StencilComp]
            Pass [_StencilPass]
            Fail Keep
            ZFail Keep
        }

        Pass
        {
            Name "StencilMask"

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex VertBuiltIn
            #pragma fragment FragBuiltIn
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float cameraSide : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;
            float _FlipVisibleSide;

            Varyings VertBuiltIn(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);

                float3 originVS = UnityObjectToViewPos(float3(0.0, 0.0, 0.0));
                float3 forwardVS = normalize(mul((float3x3)UNITY_MATRIX_MV, float3(0.0, 0.0, 1.0)));
                output.cameraSide = dot(-originVS, forwardVS);

                return output;
            }

            fixed4 FragBuiltIn(Varyings input) : SV_Target
            {
                float sideSign = (_FlipVisibleSide < 0.5) ? 1.0 : -1.0;
                clip(input.cameraSide * sideSign);

                #if defined(_ALPHATEST_ON)
                    clip(tex2D(_MainTex, input.uv).a - _Cutoff);
                #endif

                return 0;
            }
            ENDCG
        }
    }

    FallBack Off
}
