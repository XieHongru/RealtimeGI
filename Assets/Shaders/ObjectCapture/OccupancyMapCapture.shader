Shader "Mirai/OccupancyMapCapture"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    #define BASE_OM_SIZE 128
    #define BIT_PER_TEX 32
    #define UINT_PER_TEX 1
    #define BIT_PER_UINT 32

    struct VertexInput
    {
        float4 positionOS : POSITION;
    };

    struct FragmentInput
    {
        float4 positionCS : SV_POSITION;
        float3 positionWS : TEXCOORD0;
    };

    RWTexture2DArray<uint> _RWOccupancyMap;

    FragmentInput OccupancyMapCaptureVS(VertexInput input)
    {
        FragmentInput output;

        output.positionWS = TransformObjectToWorld(input.positionOS);
        output.positionCS = TransformObjectToHClip(input.positionOS);

        return output;
    }

    float OccupancyMapCaptureFS(FragmentInput input) : SV_TARGET
    {
        uint2 pixelIndex = uint2(input.positionCS.xy);

        // Generate occupancy map
        uint result = 0;
        // reverse z
        float depth = 1.0f - input.positionCS.z;
        float zPos = depth * BASE_OM_SIZE; // Depth in the range of [0, BASE_OM_SIZE]
        if (zPos >= BASE_OM_SIZE)
        {
            discard; // Discard if the depth is out of range
        }
        uint zIndex = uint(floor(zPos));
        uint arrayIndex = zIndex / BIT_PER_TEX; // Which texture in the array
        uint remainder = zIndex % BIT_PER_UINT; // Which bit in the uint
        result = 1 << (BIT_PER_UINT - 1 - remainder); // Set the bit to 1
        InterlockedOr(_RWOccupancyMap[uint3(pixelIndex, arrayIndex)], result);

        return depth;
    }
    ENDHLSL
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "OccupancyMapCapture"
            
            HLSLPROGRAM
            #pragma target 5.0
            #pragma enable_d3d11_debug_symbols
            #pragma vertex OccupancyMapCaptureVS
            #pragma fragment OccupancyMapCaptureFS
            #pragma multi_compile_instancing

            ENDHLSL
        }
    }
}