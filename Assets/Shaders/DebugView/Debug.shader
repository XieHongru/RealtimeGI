Shader "Mirai/Debug"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    int _DebugMode;
    float _VoxelSize[4];
    float3 _CascadeMin[4];
    float3 _CascadeMax[4];

    struct Attributes
    {
        float4 positionOS : POSITION;
    };

    struct Varyings
    {
        float3 positionWS : TEXCOORD0;
        float4 positionCS : SV_POSITION;
    };

    float3 MapCascadeToColorArray(int value)
    {
        const float3 colorMap[4] = {
            float3(1.0, 0.0, 0.0),
            float3(0.0, 1.0, 0.0),
            float3(0.0, 0.0, 1.0),
            float3(1.0, 1.0, 0.0)
        };
    
        if (value >= 0 && value < 4)
            return colorMap[value];
        else
            return float3(0.5, 0.5, 0.5);
    }

    float3 MapVoxelToColorOptimized(int3 coord)
    {
        const int prime1 = 196613;
        const int prime2 = 1572869;
        const int prime3 = 32452843;
        const float norm = 1.0f / 1048576.0f;
    
        int hash_r = (coord.x * prime1 + coord.y * prime2 + coord.z * prime3) % 1048576;
        int hash_g = (coord.y * prime1 + coord.z * prime2 + coord.x * prime3) % 1048576;
        int hash_b = (coord.z * prime1 + coord.x * prime2 + coord.y * prime3) % 1048576;
    
        float r = frac(float(hash_r) * norm * 1.61803398875f);
        float g = frac(float(hash_g) * norm * 2.61803398875f);
        float b = frac(float(hash_b) * norm * 3.61803398875f);
    
        return float3(r, g, b);
    }

    Varyings vert (Attributes input)
    {
        Varyings output;
                
        output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
        output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                
        return output;
    }

    float4 frag (Varyings input) : SV_Target
    {
        int curCascade = 0;
        for( ; curCascade < 4; curCascade++)
        {
            if (all(input.positionWS > _CascadeMin[curCascade]) && all(input.positionWS < _CascadeMax[curCascade]))
                break;
        }

        if(_DebugMode == 0)
        {
            return float4(MapCascadeToColorArray(curCascade), 1.0f);
        }
        if(_DebugMode == 1)
        {
            int3 voxelOffset = (input.positionWS - _CascadeMin[curCascade]) / _VoxelSize[curCascade];
            return float4(MapVoxelToColorOptimized(voxelOffset), 1.0f);
        }

        return float4(1, 0, 0, 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "DebugView"
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}