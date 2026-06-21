using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class GlobalShared
{
    public static int OBJECT_ID_INVALID = -1;
    public static int MAX_CARDS_PER_MESH = 12;
    public static int VOXEL_BLOCK_SIZE = 4;
    public static int VOXEL_COUNT_PER_BLOCK = VOXEL_BLOCK_SIZE * VOXEL_BLOCK_SIZE * VOXEL_BLOCK_SIZE;
    public static int MAX_CASCADE_COUNT = 4;
    public static int PAGE_ID_INVALID = 0x3FFFFFFF;
    public static int PROBE_ID_INVALID = 0x3FFFFFFF;
    public static int MAX_HZB_LEVEL = 8;

    public static int MAX_SAMPLE_COUNT = 16;
    public static int curSample = 0;

    public static int Index3DTo1DLinear(Vector3Int index3D, Vector3Int size3D)
    {
        int res = 0;
        res += index3D.x * 1;
	    res += index3D.y * size3D.x;
        res += index3D.z * (size3D.x * size3D.y);
	    return res;
    }

    public static Vector3Int Index1DTo3DLinear(int index1D, Vector3Int size3D)
    {
        Vector3Int res = new Vector3Int(0, 0, 0);

        res.z = index1D / (size3D.x * size3D.y);
        index1D -= res.z * (size3D.x * size3D.y);

        res.y = index1D / size3D.x;
        index1D -= res.y * size3D.x;

        res.x = index1D;

        return res;
    }

    static float Halton(int index, int modulus)
    {
        // Reversing digit order in the given modulus in floating point.
        float result = 0.0f;
        float factor = 1.0f;

        for (; index > 0; index /= modulus)
        {
            factor /= modulus;
            result += factor * (index % modulus);
        }

        return result;
    }

    public static Vector2 GetHaltonSamplerNext()
    {
        Vector2 value = new Vector2(Halton(curSample, 2), Halton(curSample, 3));

        // Modular increment.
        ++curSample;
        curSample = curSample % MAX_SAMPLE_COUNT;

        // Map the result so that [0, 1) maps to [-0.5, 0.5) and 0 maps to the origin.
        return new Vector2((value.x + 0.5f) % 1.0f, (value.y + 0.5f) % 1.0f) - Vector2.one * 0.5f;
    }
}

public enum CardCaptureRTSlot
{
    BaseColor = 0,
    Normal,
    Emissive,
    Depth,
    Num
}