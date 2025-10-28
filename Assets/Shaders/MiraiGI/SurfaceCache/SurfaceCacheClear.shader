Shader "Mirai/SurfaceCacheClear"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    struct VertexInput
    {
        float4 positionOS : POSITION;
        uint instanceID : SV_InstanceID;
    };

    struct FragmentInput
    {
        float4 positionCS : SV_POSITION;
    };

    float _CardAtlasResolution;
    StructuredBuffer<float4> cardClearQuadUVTransformBuffer;

    FragmentInput SurfaceCacheClearVS(VertexInput input)
    {
        FragmentInput output;

        float4 cardSizeAndOffset = _CardClearQuadUVTransformBuffer[input.instanceID];

        float2 scale = cardSizeAndOffset.xy / _CardAtlasResolution;
        float2 offset = (cardSizeAndOffset.zw / _CardAtlasResolution) * 2 - 1;   // map [0, 1] to [-1, 1]
        offset += (scale * 0.5) * 2;  // change pivot from center to topleft, cause ndc space range [-1, 1], so we mul 2 to match the "width"
        float2 positionXY = input.positionOS.xy * scale + offset;

        // need reverse Y?
        positionXY *= float2(1, -1);

	    output.positionCS = float4(positionXY, 0, 1.0f);

        return output;
    }

    struct FragmentOutput
    {
        half4 baseColor : SV_Target0;
        half4 normal : SV_Target1;
        half4 emissive : SV_Target2;
        half4 depth : SV_Target3;
    };

    FragmentOutput SurfaceCacheClearFS(FragmentInput input)
    {
        FragmentOutput output;

        output.baseColor = float4(0, 0, 0, 0);
        output.normal = float4(0, 0, 0, 0);
        output.emissive = float4(0, 0, 0, 0);
        output.depth = float4(0, 0, 0, 0);
                
        return output;
    }
    ENDHLSL
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "SurfaceCacheClear"
            
            HLSLPROGRAM
            #pragma enable_d3d11_debug_symbols
            #pragma vertex SurfaceCacheClearVS
            #pragma fragment SurfaceCacheClearFS
            #pragma multi_compile_instancing

            ENDHLSL
        }
    }
}