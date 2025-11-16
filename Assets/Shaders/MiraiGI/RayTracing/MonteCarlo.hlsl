#ifndef MONTECARLO_HLSL
#define MONTECARLO_HLSL

#include "../../GICommon.hlsl"

float2 Hammersley16(uint Index, uint NumSamples, uint2 Random)
{
    float E1 = frac((float) Index / NumSamples + float(Random.x) * (1.0 / 65536.0));
    float E2 = float((ReverseBits32(Index) >> 16) ^ Random.y) * (1.0 / 65536.0);
    return float2(E1, E2);
}

float4 UniformSampleSphere(float2 E)
{
    float Phi = 2 * PI * E.x;
    float CosTheta = 1 - 2 * E.y;
    float SinTheta = sqrt(1 - CosTheta * CosTheta);

    float3 H;
    H.x = SinTheta * cos(Phi);
    H.y = SinTheta * sin(Phi);
    H.z = CosTheta;

    float PDF = 1.0 / (4 * PI);

    return float4(H, PDF);
}

#endif