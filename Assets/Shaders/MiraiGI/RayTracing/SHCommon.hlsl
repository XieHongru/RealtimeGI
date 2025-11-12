#ifndef SHCOMMON_HLSL
#define SHCOMMON_HLSL

#include "../../GICommon.hlsl"

struct ThreeBandSHVector
{
	half4 V0;
	half4 V1;
	half V2;
};

struct ThreeBandSHVectorRGB
{
	ThreeBandSHVector R;
	ThreeBandSHVector G;
	ThreeBandSHVector B;
};

ThreeBandSHVector SHBasisFunction3(half3 InputVector)
{
    ThreeBandSHVector Result;
	// These are derived from simplifying SHBasisFunction in C++
    Result.V0.x = 0.282095f;
    Result.V0.y = -0.488603f * InputVector.y;
    Result.V0.z = 0.488603f * InputVector.z;
    Result.V0.w = -0.488603f * InputVector.x;

    half3 VectorSquared = InputVector * InputVector;
    Result.V1.x = 1.092548f * InputVector.x * InputVector.y;
    Result.V1.y = -1.092548f * InputVector.y * InputVector.z;
    Result.V1.z = 0.315392f * (3.0f * VectorSquared.z - 1.0f);
    Result.V1.w = -1.092548f * InputVector.x * InputVector.z;
    Result.V2 = 0.546274f * (VectorSquared.x - VectorSquared.y);

    return Result;
}

ThreeBandSHVectorRGB MulSH3(ThreeBandSHVector A, half3 Color)
{
    ThreeBandSHVectorRGB Result;
    Result.R.V0 = A.V0 * Color.r;
    Result.R.V1 = A.V1 * Color.r;
    Result.R.V2 = A.V2 * Color.r;
    Result.G.V0 = A.V0 * Color.g;
    Result.G.V1 = A.V1 * Color.g;
    Result.G.V2 = A.V2 * Color.g;
    Result.B.V0 = A.V0 * Color.b;
    Result.B.V1 = A.V1 * Color.b;
    Result.B.V2 = A.V2 * Color.b;
    return Result;
}

ThreeBandSHVector MulSH3(ThreeBandSHVector A, half Scalar)
{
    ThreeBandSHVector Result;
    Result.V0 = A.V0 * Scalar;
    Result.V1 = A.V1 * Scalar;
    Result.V2 = A.V2 * Scalar;
    return Result;
}

ThreeBandSHVector AddSH(ThreeBandSHVector A, ThreeBandSHVector B)
{
    ThreeBandSHVector Result = A;
    Result.V0 += B.V0;
    Result.V1 += B.V1;
    Result.V2 += B.V2;
    return Result;
}

ThreeBandSHVectorRGB AddSH(ThreeBandSHVectorRGB A, ThreeBandSHVectorRGB B)
{
    ThreeBandSHVectorRGB Result;
    Result.R = AddSH(A.R, B.R);
    Result.G = AddSH(A.G, B.G);
    Result.B = AddSH(A.B, B.B);
    return Result;
}

half DotSH3(ThreeBandSHVector A, ThreeBandSHVector B)
{
    half Result = dot(A.V0, B.V0);
    Result += dot(A.V1, B.V1);
    Result += A.V2 * B.V2;
    return Result;
}

half3 DotSH3(ThreeBandSHVectorRGB A, ThreeBandSHVector B)
{
    half3 Result = 0;
    Result.r = DotSH3(A.R, B);
    Result.g = DotSH3(A.G, B);
    Result.b = DotSH3(A.B, B);
    return Result;
}

ThreeBandSHVector CalcDiffuseTransferSH3(half3 Normal, half Exponent)
{
    ThreeBandSHVector Result = SHBasisFunction3(Normal);

	// These formula are scaling factors for each SH band that convolve a SH with the circularly symmetric function
	// max(0,cos(theta))^Exponent
    half L0 = 2 * PI / (1 + 1 * Exponent);
    half L1 = 2 * PI / (2 + 1 * Exponent);
    half L2 = Exponent * 2 * PI / (3 + 4 * Exponent + Exponent * Exponent);
    half L3 = (Exponent - 1) * 2 * PI / (8 + 6 * Exponent + Exponent * Exponent);

	// Multiply the coefficients in each band with the appropriate band scaling factor.
    Result.V0.x *= L0;
    Result.V0.yzw *= L1;
    Result.V1.xyzw *= L2;
    Result.V2 *= L2;

    return Result;
}

#endif