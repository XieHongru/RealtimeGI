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

float4 UniformSampleHemisphere(float2 E)
{
    float Phi = 2 * PI * E.x;
    float CosTheta = E.y;
    float SinTheta = sqrt(1 - CosTheta * CosTheta);

    float3 H;
    H.x = SinTheta * cos(Phi);
    H.y = SinTheta * sin(Phi);
    H.z = CosTheta;

    float PDF = 1.0 / (2 * PI);

    return float4(H, PDF);
}

float2 UniformSampleDisk(float2 E)
{
    float Theta = 2 * PI * E.x;
    float Radius = sqrt(E.y);
    return Radius * float2(cos(Theta), sin(Theta));
}

float3x3 GetTangentBasis(float3 TangentZ)
{
    const float Sign = TangentZ.z >= 0 ? 1 : -1;
    const float a = -rcp(Sign + TangentZ.z);
    const float b = TangentZ.x * TangentZ.y * a;
	
    float3 TangentX = { 1 + Sign * a * TangentZ.x * TangentZ.x, Sign * b, -Sign * TangentZ.x };
    float3 TangentY = { b, Sign + a * TangentZ.y * TangentZ.y, -TangentZ.y  };

    return float3x3(TangentX, TangentY, TangentZ);
}

#endif