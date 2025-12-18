#ifndef RESERVOIR_SAMPLING_HLSL
#define RESERVOIR_SAMPLING_HLSL

struct ReservoirSample
{
    float3 radiance;
    float weight;
    float3 rayStart;
    float3 rayStartNormal;
    float3 rayEnd;
    float3 rayEndNormal;
};

struct Reservoir
{
    ReservoirSample currentSample;
    float weightSum; // w in paper
    float sampleCount; // M in paper
    float estimatorWeight; // W in paper
};

float EvaluateTargetPDF(float3 radiance)
{
    return max(1e-3, dot(radiance, float3(0.3, 0.3, 0.3)));
}

ReservoirSample GetReservoirSample(float3 radiance, float sourcePDF)
{
    float targetPDF = EvaluateTargetPDF(radiance);

    ReservoirSample result;
    result.radiance = radiance;
    result.weight = targetPDF / max(sourcePDF, 1e-3);

    return result;
}

void UpdateReservoir(inout Reservoir reservoir, in ReservoirSample newSample, float randomNumber)
{
    reservoir.weightSum += newSample.weight;
    reservoir.sampleCount += 1;

    if (randomNumber < (newSample.weight / reservoir.weightSum))
    {
        reservoir.currentSample = newSample;
    }

    // clamp sample num
#define MAX_SAMPLE_NUM (30.0f)
    if (reservoir.sampleCount > MAX_SAMPLE_NUM)
    {
        reservoir.weightSum *= MAX_SAMPLE_NUM / float(reservoir.sampleCount);
        reservoir.sampleCount = MAX_SAMPLE_NUM;
    }

    // update RIS weight
    float p_hat = EvaluateTargetPDF(reservoir.currentSample.radiance);
    reservoir.estimatorWeight = reservoir.weightSum / (reservoir.sampleCount * p_hat);
}

float SimpleRandom(float2 co)
{
    return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453);
}

// https://discussions.unity.com/t/pack-two-float3-in-one-float/770749
float EncodeFloat3ToFloat(float3 c)
{
    return dot(round((c) * 255), float3(65536, 256, 1));
}

float3 DecodeFloat3FromFloat(float f)
{
    return frac((f) / float3(16777216, 65536, 256));
}

Reservoir DecodeReservoirData(in float4 rawData[4])
{
    Reservoir reservoir = (Reservoir) 0;

    reservoir.weightSum = rawData[0].x;
    reservoir.sampleCount = rawData[0].y;
    reservoir.estimatorWeight = rawData[0].z;

    reservoir.currentSample.radiance = rawData[1].xyz;
    reservoir.currentSample.weight = rawData[1].w;

    reservoir.currentSample.rayStart = rawData[2].xyz;
    reservoir.currentSample.rayStartNormal = DecodeFloat3FromFloat(rawData[2].w) * 2 - 1;
    
    reservoir.currentSample.rayEnd = rawData[3].xyz;
    reservoir.currentSample.rayEndNormal = DecodeFloat3FromFloat(rawData[3].w) * 2 - 1;

    return reservoir;
}

void EncodeReservoirData(in Reservoir reservoir, out float4 rawData[4])
{
    rawData[0].x = reservoir.weightSum;
    rawData[0].y = reservoir.sampleCount;
    rawData[0].z = reservoir.estimatorWeight;
    rawData[0].w = 0;

    rawData[1].xyz = reservoir.currentSample.radiance;
    rawData[1].w = reservoir.currentSample.weight;

    rawData[2].xyz = reservoir.currentSample.rayStart;
    rawData[2].w = EncodeFloat3ToFloat(reservoir.currentSample.rayStartNormal * 0.5 + 0.5);

    rawData[3].xyz = reservoir.currentSample.rayEnd;
    rawData[3].w = EncodeFloat3ToFloat(reservoir.currentSample.rayEndNormal * 0.5 + 0.5);
}

#endif