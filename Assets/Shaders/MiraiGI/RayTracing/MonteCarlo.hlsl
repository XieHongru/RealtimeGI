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

float VisibleGGXPDF(float3 V, float3 H, float a2)
{
    float NoV = V.z;
    float NoH = H.z;
    float VoH = dot(V, H);

    float d = (NoH * a2 - NoH) * NoH + 1;
    float D = a2 / (PI * d * d);

    float PDF = 2 * VoH * D / (NoV + sqrt(NoV * (NoV - NoV * a2) + a2));
    return PDF;
}

// [ Heitz 2018, "Sampling the GGX Distribution of Visible Normals" ]
// http://jcgt.org/published/0007/04/01/

float4 ImportanceSampleVisibleGGX(float2 DiskE, float a2, float3 V)
{
	// NOTE: See below for anisotropic version that avoids this sqrt
    float a = sqrt(a2);

	// stretch
    float3 Vh = normalize(float3(a * V.xy, V.z));

	// Stable tangent basis based on V
	// Tangent0 is orthogonal to N
    float LenSq = Vh.x * Vh.x + Vh.y * Vh.y;
    float3 Tangent0 = LenSq > 0 ? float3(-Vh.y, Vh.x, 0) * rsqrt(LenSq) : float3(1, 0, 0);
    float3 Tangent1 = cross(Vh, Tangent0);

    float2 p = DiskE;
    float s = 0.5 + 0.5 * Vh.z;
    p.y = (1 - s) * sqrt(1 - p.x * p.x) + s * p.y;

    float3 H;
    H = p.x * Tangent0;
    H += p.y * Tangent1;
    H += sqrt(saturate(1 - dot(p, p))) * Vh;

	// unstretch
    H = normalize(float3(a * H.xy, max(0.0, H.z)));

    return float4(H, VisibleGGXPDF(V, H, a2));
}

#endif