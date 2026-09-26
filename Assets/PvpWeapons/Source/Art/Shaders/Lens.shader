Shader "Lens"
{
    Properties
    {
        [MainTexture] _MainTex("Texture", 2D) = "white" {}
        [HDR] _Color("Base Color", Color) = (1,1,1,1)
        _Scale("Scale Factor", Range(0.001,10)) = 1
        [HideInInspector] _ProjectionDistance("Projection Distance", Range(0.01,2000)) = 100
        _EdgeFeather("Edge Feather", Range(0,0.25)) = 0.01
        _OutOfBoundsColor("Out Of Bounds Color", Color) = (0,0,0,1)
        [HideInInspector] [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("Depth Test", Float) = 8
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
        [Toggle] _FlipVisibleSide("Flip Visible Side", Float) = 0
    }

    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off ZTest [_ZTest] Cull [_Cull] Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            Name "ForwardOnly" Tags { "LightMode"="ForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct Attributes { float3 positionOS:POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; float cameraSide:TEXCOORD1; };
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_ST; half4 _Color, _OutOfBoundsColor;
            float _Scale, _ProjectionDistance, _EdgeFeather, _FlipVisibleSide;

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 origin = TransformWorldToView(TransformObjectToWorld(float3(0,0,0)));
                float3x3 mv = (float3x3)UNITY_MATRIX_MV;
                float3 axisU = normalize(mul(mv, float3(1,0,0)));
                float3 axisV = normalize(mul(mv, float3(0,1,0)));
                float3 normalVS = normalize(mul(mv, float3(0,0,1)));
                output.cameraSide = dot(-origin, normalVS);
                float distance = max(_ProjectionDistance, 0.01);
                float3 planePoint = origin + normalVS * distance;
                float3 vertexVS = TransformWorldToView(TransformObjectToWorld(input.positionOS));
                float denominator = dot(vertexVS, normalVS);
                denominator = abs(denominator) < 1e-5 ? (denominator < 0 ? -1e-5 : 1e-5) : denominator;
                float3 projected = (dot(planePoint, normalVS) / denominator) * vertexVS;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = float2(dot(projected-planePoint, axisU), dot(projected-planePoint, axisV)) / (max(_Scale,0.001) * distance) + 0.5;
                output.uv = output.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return output;
            }

            half4 frag(Varyings input):SV_Target
            {
                float visibleSideSign = 1.0 - 2.0 * step(0.5, _FlipVisibleSide);
                clip(-input.cameraSide * visibleSideSign);
                float2 safeST = sign(_MainTex_ST.xy) * max(abs(_MainTex_ST.xy), 1e-5);
                float2 baseUV = (input.uv - _MainTex_ST.zw) / safeST;
                float edge = min(min(baseUV.x, baseUV.y), min(1-baseUV.x, 1-baseUV.y));
                float inside = smoothstep(0, max(_EdgeFeather,1e-5), edge);
                half4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, saturate(input.uv)) * _Color;
                return lerp(_OutOfBoundsColor, color, inside);
            }
            ENDHLSL
        }
    }

    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off ZTest [_ZTest] Cull [_Cull] Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            Name "Lens" Tags { "LightMode"="SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float3 positionOS:POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; float cameraSide:TEXCOORD1; };
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_ST; half4 _Color, _OutOfBoundsColor;
            float _Scale, _ProjectionDistance, _EdgeFeather, _FlipVisibleSide;

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 origin = TransformWorldToView(TransformObjectToWorld(float3(0,0,0)));
                float3x3 mv = (float3x3)UNITY_MATRIX_MV;
                float3 axisU = normalize(mul(mv, float3(1,0,0)));
                float3 axisV = normalize(mul(mv, float3(0,1,0)));
                float3 normalVS = normalize(mul(mv, float3(0,0,1)));
                output.cameraSide = dot(-origin, normalVS);
                float distance = max(_ProjectionDistance, 0.01);
                float3 planePoint = origin + normalVS * distance;
                float3 vertexVS = TransformWorldToView(TransformObjectToWorld(input.positionOS));
                float denominator = dot(vertexVS, normalVS);
                denominator = abs(denominator) < 1e-5 ? (denominator < 0 ? -1e-5 : 1e-5) : denominator;
                float3 projected = (dot(planePoint, normalVS) / denominator) * vertexVS;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = float2(dot(projected-planePoint, axisU), dot(projected-planePoint, axisV)) / (max(_Scale,0.001) * distance) + 0.5;
                output.uv = output.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return output;
            }

            half4 frag(Varyings input):SV_Target
            {
                float visibleSideSign = 1.0 - 2.0 * step(0.5, _FlipVisibleSide);
                clip(-input.cameraSide * visibleSideSign);
                float2 safeST = sign(_MainTex_ST.xy) * max(abs(_MainTex_ST.xy), 1e-5);
                float2 baseUV = (input.uv - _MainTex_ST.zw) / safeST;
                float edge = min(min(baseUV.x, baseUV.y), min(1-baseUV.x, 1-baseUV.y));
                float inside = smoothstep(0, max(_EdgeFeather,1e-5), edge);
                half4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, saturate(input.uv)) * _Color;
                return lerp(_OutOfBoundsColor, color, inside);
            }
            ENDHLSL
        }
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off ZTest [_ZTest] Cull [_Cull] Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct Attributes { float4 vertex:POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; float cameraSide:TEXCOORD1; };
            sampler2D _MainTex; float4 _MainTex_ST, _Color, _OutOfBoundsColor;
            float _Scale, _ProjectionDistance, _EdgeFeather, _FlipVisibleSide;

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 origin = UnityObjectToViewPos(float3(0,0,0));
                float3x3 mv = (float3x3)UNITY_MATRIX_MV;
                float3 axisU = normalize(mul(mv, float3(1,0,0)));
                float3 axisV = normalize(mul(mv, float3(0,1,0)));
                float3 normalVS = normalize(mul(mv, float3(0,0,1)));
                output.cameraSide = dot(-origin, normalVS);
                float distance = max(_ProjectionDistance, 0.01);
                float3 planePoint = origin + normalVS * distance;
                float3 vertexVS = UnityObjectToViewPos(input.vertex);
                float denominator = dot(vertexVS, normalVS);
                denominator = abs(denominator) < 1e-5 ? (denominator < 0 ? -1e-5 : 1e-5) : denominator;
                float3 projected = (dot(planePoint, normalVS) / denominator) * vertexVS;
                output.positionCS = UnityObjectToClipPos(input.vertex);
                output.uv = float2(dot(projected-planePoint, axisU), dot(projected-planePoint, axisV)) / (max(_Scale,0.001) * distance) + 0.5;
                output.uv = output.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return output;
            }

            fixed4 frag(Varyings input):SV_Target
            {
                float visibleSideSign = 1.0 - 2.0 * step(0.5, _FlipVisibleSide);
                clip(-input.cameraSide * visibleSideSign);
                float2 safeST = sign(_MainTex_ST.xy) * max(abs(_MainTex_ST.xy), 1e-5);
                float2 baseUV = (input.uv - _MainTex_ST.zw) / safeST;
                float edge = min(min(baseUV.x, baseUV.y), min(1-baseUV.x, 1-baseUV.y));
                float inside = smoothstep(0, max(_EdgeFeather,1e-5), edge);
                fixed4 color = tex2D(_MainTex, saturate(input.uv)) * _Color;
                return lerp(_OutOfBoundsColor, color, inside);
            }
            ENDCG
        }
    }
    FallBack Off
}
