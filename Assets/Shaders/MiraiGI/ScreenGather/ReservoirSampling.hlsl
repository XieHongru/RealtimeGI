#ifndef RESERVOIR_SAMPLING_HLSL
#define RESERVOIR_SAMPLING_HLSL

// https://blog.demofox.org/2020/05/25/casual-shadertoy-path-tracing-1-basic-camera-diffuse-emissive/
uint GetSimpleRandomSeed(float2 pixelIndex, uint frameCounter)
{
    return uint(
		uint(pixelIndex.x) * uint(1973) +
		uint(pixelIndex.y) * uint(9277) +
		uint(frameCounter) * uint(26699)
	) | uint(1);
}

uint wang_hash(inout uint seed)
{
    seed = uint(seed ^ uint(61)) ^ uint(seed >> uint(16));
    seed *= uint(9);
    seed = seed ^ (seed >> 4);
    seed *= uint(0x27d4eb2d);
    seed = seed ^ (seed >> 15);
    return seed;
}
 
float SimpleRandom(inout uint seed)
{
    return float(wang_hash(seed)) / 4294967296.0;
}

// -------------------------------------------------------------------------------- //
// https://d1qx31qr3h6wln.cloudfront.net/publications/ReSTIR%20GI.pdf
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

#define TARGET_PDF_WITH_COSINE 1

// get target pdf in specific position and surface normal
float EvaluateTargetPDF(in ReservoirSample sample, float3 evaluatePointPosition, float3 evaluatePointNormal)
{
    float cosineWeight = 1.0;
    
        // note: 
    // if using next event estimator (NEE) to trace both direct and indirect lighting, we should follow the advice of paper and don't use cosine weight
    // but we only trace indirect lighting here, so we consider cosine weight to get more smooth result
#if TARGET_PDF_WITH_COSINE
    float3 rayDirection = normalize(sample.rayEnd - evaluatePointPosition); // connect the sample with evaluate point
    cosineWeight = max(dot(rayDirection, evaluatePointNormal), 0.1); // clamp to prevent firefly
#endif

    float targetPDF = dot(sample.radiance, (0.33).xxx) * cosineWeight;
    return max(targetPDF, 1e-3);
}

#define LengthSquare(x) (dot(x, x))

void UpdateReservoir(inout Reservoir reservoir, in ReservoirSample newSample, float selectionWeight, float randomNumber)
{
    // accumulate weight
    reservoir.weightSum += selectionWeight;
    reservoir.sampleCount += 1;

    // randomly accept or discard sample
    if (randomNumber < (selectionWeight / reservoir.weightSum))
    {
        reservoir.currentSample = newSample;
    }
}

void ClampSampleNumAndUpdateEstimatorWeight(inout Reservoir reservoir, float3 evaluatePointPosition, float3 evaluatePointNormal, float maxSampleCount)
{
    // clamp sample num
    if (reservoir.sampleCount > maxSampleCount)
    {
        reservoir.weightSum *= maxSampleCount / float(reservoir.sampleCount);
        reservoir.sampleCount = maxSampleCount;
    }

    // update estimator weight
    float targetPDF = EvaluateTargetPDF(reservoir.currentSample, evaluatePointPosition, evaluatePointNormal);
    reservoir.estimatorWeight = reservoir.weightSum / (reservoir.sampleCount * targetPDF); // W = w / (M * p_hat) in paper
}

float2 DirectionToOctahedron(float3 N)
{
    N.xy /= dot(1, abs(N));
    if (N.z <= 0)
    {
        N.xy = (1 - abs(N.yx)) * (N.xy >= 0 ? float2(1, 1) : float2(-1, -1));
    }
    return N.xy;
}

float3 OctahedronToDirection(float2 Oct)
{
    float3 N = float3(Oct, 1 - dot(1, abs(Oct)));
    if (N.z < 0)
    {
        N.xy = (1 - abs(N.yx)) * (N.xy >= 0 ? float2(1, 1) : float2(-1, -1));
    }
    return normalize(N);
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
    reservoir.currentSample.rayStartNormal = OctahedronToDirection(float2(rawData[2].w, rawData[3].w));
    
    reservoir.currentSample.rayEnd = rawData[3].xyz;
    reservoir.currentSample.rayEndNormal = OctahedronToDirection(float2(rawData[0].w, rawData[1].w));

    return reservoir;
}

void EncodeReservoirData(in Reservoir reservoir, out float4 rawData[4])
{
    float2 rayStartNormalOct = DirectionToOctahedron(reservoir.currentSample.rayStartNormal);
    float2 rayEndNormalOct = DirectionToOctahedron(reservoir.currentSample.rayEndNormal);
    
    rawData[0].x = reservoir.weightSum;
    rawData[0].y = reservoir.sampleCount;
    rawData[0].z = reservoir.estimatorWeight;
    rawData[0].w = rayEndNormalOct.x;

    rawData[1].xyz = reservoir.currentSample.radiance;
    rawData[1].w = rayEndNormalOct.y;

    rawData[2].xyz = reservoir.currentSample.rayStart;
    rawData[2].w = rayStartNormalOct.x;

    rawData[3].xyz = reservoir.currentSample.rayEnd;
    rawData[3].w = rayStartNormalOct.y;
}

#endif