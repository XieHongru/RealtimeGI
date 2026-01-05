#ifndef SSRT_RAY_CAST_HLSL
#define SSRT_RAY_CAST_HLSL

// Refer to Unreal Engine

// Number of sample batched at same time.
#define SSRT_SAMPLE_BATCH_SIZE 4

#ifndef IS_SSGI_SHADER
#define IS_SSGI_SHADER 0
#endif

#define SSGI_TRACE_CONE 0

/** Return float multiplier to scale RayStepScreen such that it clip it right at the edge of the screen. */
float GetStepScreenFactorToClipAtScreenEdge(float2 RayStartScreen, float2 RayStepScreen)
{
	// Computes the scale down factor for RayStepScreen required to fit on the X and Y axis in order to clip it in the viewport
    const float RayStepScreenInvFactor = 0.5 * length(RayStepScreen);
    const float2 S = 1 - max(abs(RayStepScreen + RayStartScreen * RayStepScreenInvFactor) - RayStepScreenInvFactor, 0.0f) / abs(RayStepScreen);

	// Rescales RayStepScreen accordingly
    const float RayStepFactor = min(S.x, S.y) / RayStepScreenInvFactor;

    return RayStepFactor;
}

/** Structure that represent a ray to be shot in screen space. */
struct SSRTRay
{
    float3 RayStartScreen;
    float3 RayStepScreen;

    float CompareTolerance;
};

/** Compile a ray for screen space ray casting. */
SSRTRay InitScreenSpaceRayFromWorldSpace(
    float4x4 TranslatedWorldToClipMatrix,
    float4x4 ViewToClipMatrix,
	float3 RayOriginTranslatedWorld,
	float3 WorldRayDirection,
	float SceneDepth)
{
    float4 RayStartClip = mul(TranslatedWorldToClipMatrix, float4(RayOriginTranslatedWorld, 1));
    float4 RayEndClip = mul(TranslatedWorldToClipMatrix, float4(RayOriginTranslatedWorld + WorldRayDirection * SceneDepth, 1));

    float3 RayStartScreen = RayStartClip.xyz * rcp(RayStartClip.w);
    float3 RayEndScreen = RayEndClip.xyz * rcp(RayEndClip.w);

    float4 RayDepthClip = RayStartClip + mul(ViewToClipMatrix, float4(0, 0, SceneDepth, 0));
    float3 RayDepthScreen = RayDepthClip.xyz * rcp(RayDepthClip.w);

    SSRTRay Ray;
    Ray.RayStartScreen = RayStartScreen;
    Ray.RayStepScreen = RayEndScreen - RayStartScreen;
	
    Ray.RayStepScreen *= GetStepScreenFactorToClipAtScreenEdge(RayStartScreen.xy, Ray.RayStepScreen.xy);

	// TODO
#if IS_SSGI_SHADER
		Ray.CompareTolerance = max(abs(Ray.RayStepScreen.z), (RayStartScreen.z - RayDepthScreen.z) * 2);
#else
    Ray.CompareTolerance = max(abs(Ray.RayStepScreen.z), (RayStartScreen.z - RayDepthScreen.z) * 4);
#endif

    return Ray;
}

/** Cast a screen space ray. */
bool CastScreenSpaceRay(
	Texture2D Texture, SamplerState Sampler,
	SSRTRay Ray,
	float Roughness,
	uint NumSteps, float StepOffset,
	float4 HZBUvFactorAndInvFactor,
	bool bDebugPrint,
    float4 ScreenPositionScaleBias,
	out float3 OutHitUVz,
	out float Level)
{
    const float3 RayStartScreen = Ray.RayStartScreen;
    float3 RayStepScreen = Ray.RayStepScreen;

    float3 RayStartUVz = float3((RayStartScreen.xy * float2(0.5, 0.5) + 0.5) * HZBUvFactorAndInvFactor.xy, RayStartScreen.z);
    float3 RayStepUVz = float3(RayStepScreen.xy * float2(0.5, 0.5) * HZBUvFactorAndInvFactor.xy, RayStepScreen.z);
	
    const float Step = 1.0 / NumSteps;
    float CompareTolerance = Ray.CompareTolerance * Step;
	
    float LastDiff = 0;
    Level = 1;

	//StepOffset = View.GeneralPurposeTweak;

    RayStepUVz *= Step;
    float3 RayUVz = RayStartUVz + RayStepUVz * StepOffset;
	
    float4 MultipleSampleDepthDiff;
    bool4 bMultipleSampleHit; // TODO: Might consumes VGPRS if bug in compiler.
    bool bFoundAnyHit = false;

    uint i;

    [loop]
    for (i = 0; i < NumSteps; i += SSRT_SAMPLE_BATCH_SIZE)
    {
        float2 SamplesUV[SSRT_SAMPLE_BATCH_SIZE];
        float4 SamplesZ;
        float4 SamplesMip;
        
		{
            [unroll(SSRT_SAMPLE_BATCH_SIZE)]
            for (uint j = 0; j < SSRT_SAMPLE_BATCH_SIZE; j++)
            {
                SamplesUV[j] = RayUVz.xy + (float(i) + float(j + 1)) * RayStepUVz.xy;
                SamplesZ[j] = RayUVz.z + (float(i) + float(j + 1)) * RayStepUVz.z;
            }
		
            SamplesMip.xy = Level;
            Level += (8.0 / NumSteps) * Roughness;
		
            SamplesMip.zw = Level;
            Level += (8.0 / NumSteps) * Roughness;
        }

		// Sample the scene depth.
        float4 SampleDepth;
		{
            [unroll(SSRT_SAMPLE_BATCH_SIZE)]
            for (uint j = 0; j < SSRT_SAMPLE_BATCH_SIZE; j++)
            {
                SampleDepth[j] = Texture.SampleLevel(Sampler, SamplesUV[j], SamplesMip[j]).r;
            }
        }

		// Evaluates the intersections.
        MultipleSampleDepthDiff = SamplesZ - SampleDepth;
        bMultipleSampleHit = abs(MultipleSampleDepthDiff + CompareTolerance) < CompareTolerance;
        bFoundAnyHit = any(bMultipleSampleHit);

        [branch]
        if (bFoundAnyHit)
        {
            break;
        }

        LastDiff = MultipleSampleDepthDiff.w;
    } // for( uint i = 0; i < NumSteps; i += 4 )
	
	// Compute the output coordinates.
    [branch]
    if (bFoundAnyHit)
    {
        {
            float DepthDiff0 = MultipleSampleDepthDiff[2];
            float DepthDiff1 = MultipleSampleDepthDiff[3];
            float Time0 = 3;

            [flatten]
            if (bMultipleSampleHit[2])
            {
                DepthDiff0 = MultipleSampleDepthDiff[1];
                DepthDiff1 = MultipleSampleDepthDiff[2];
                Time0 = 2;
            }
            [flatten]
            if (bMultipleSampleHit[1])
            {
                DepthDiff0 = MultipleSampleDepthDiff[0];
                DepthDiff1 = MultipleSampleDepthDiff[1];
                Time0 = 1;
            }
            [flatten]
            if (bMultipleSampleHit[0])
            {
                DepthDiff0 = LastDiff;
                DepthDiff1 = MultipleSampleDepthDiff[0];
                Time0 = 0;
            }

            Time0 += float(i);

            float Time1 = Time0 + 1;

			// Find more accurate hit using line segment intersection
            float TimeLerp = saturate(DepthDiff0 / (DepthDiff0 - DepthDiff1));
            float IntersectTime = Time0 + TimeLerp;
			//float IntersectTime = lerp( Time0, Time1, TimeLerp );
				
            OutHitUVz = RayUVz + RayStepUVz * IntersectTime;
        }

        OutHitUVz.xy *= HZBUvFactorAndInvFactor.zw;
        OutHitUVz.xy = OutHitUVz.xy * float2(2, -2) + float2(-1, 1);
        OutHitUVz.xy = OutHitUVz.xy * ScreenPositionScaleBias.xy + ScreenPositionScaleBias.wz;
    }
    else
    {
        OutHitUVz = float3(0, 0, 0);
    }
	
    return bFoundAnyHit;
}

bool RayCast(
	Texture2D Texture, SamplerState Sampler,
	float3 RayOriginTranslatedWorld, float3 RayDirection,
	float Roughness, float SceneDepth,
	uint NumSteps, float StepOffset,
	float4 HZBUvFactorAndInvFactor,
	bool bDebugPrint,
    float4x4 TranslatedWorldToClipMatrix,
    float4x4 ViewToClipMatrix,
    float4 ScreenPositionScaleBias,
	out float3 OutHitUVz,
	out float Level)
{
    SSRTRay Ray = InitScreenSpaceRayFromWorldSpace(TranslatedWorldToClipMatrix, ViewToClipMatrix, RayOriginTranslatedWorld, RayDirection, SceneDepth);

    return CastScreenSpaceRay(
		Texture, Sampler,
		Ray,
		Roughness, NumSteps, StepOffset,
		HZBUvFactorAndInvFactor, bDebugPrint,
        ScreenPositionScaleBias,
		/* out */ OutHitUVz,
		/* out */ Level);
}

#endif