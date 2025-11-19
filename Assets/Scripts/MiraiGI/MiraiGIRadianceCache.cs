using System;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static UnityEditor.Rendering.ShadowCascadeGUI;

public enum ProbeVisualizeMode
{
    RadianceProbe = 0,
    IrradianceProbe,
    Num
}

public class MiraiGIRadianceCache
{
    public uint frameNumberRenderThread;

    int m_VoxelLightingCheckerBoardSize = 2;
    ComputeBuffer m_ValidVoxelCounter;
    ComputeBuffer m_ValidVoxelBuffer;
    ComputeBuffer m_VoxelLightingIndirectArgs;
    RenderTexture m_VoxelPoolRadiance;

    // radiance and irradiance probe share same probe placement
    int m_ProbeUpdateCheckerBoardSize = 2;
    ComputeBuffer m_ValidProbeCounter;
    ComputeBuffer m_ValidProbeBuffer;
    RenderTexture[] m_ProbePositionOffsetVolume;

    // gather irradiance for all cascade
    ComputeBuffer m_IrradianceProbeGatherIndirectArgs;
    RenderTexture[] m_ProbeIrradianceCache;

    // just capture radiance probe for highest cascade
    int m_RadianceProbeResolution = 16;
    Vector2Int m_RadianceProbeCountInAtlasXY = new Vector2Int(128, 128);
    RenderTexture m_RadianceProbeAtlas;
    RenderTexture m_RadianceProbeDistanceAtlas;

    // radiance probe need large resolution to store radiance in atlas, so we sparse allocate it
    ComputeBuffer m_RadianceProbeAllocator;
    ComputeBuffer m_RadianceProbeFreeList;      // empty probe id list
    ComputeBuffer m_RadianceProbeReleaseList;   // pending probes to release at this frame
    RenderTexture m_RadianceProbeIdVolume;      // store index to RadianceProbeAtlas
    ComputeBuffer m_RadianceProbeReleaseIndirectArgs;
    ComputeBuffer m_RadianceProbeCaptureIndirectArgs;

    ComputeShader m_VoxelLightingCS;
    ComputeShader m_VoxelPoolInitCS;

    Mesh m_ProbeSphereMesh;

    // init data
    int[] m_ValidVoxelCounterInitData;
    int[] m_ValidVoxelBufferInitData;
    int[] m_ValidProbeCounterInitData;
    int[] m_ValidProbeBufferInitData;
    int[] m_RadianceProbeAllocatorInitData;
    int[] m_RadianceProbeFreeListInitData;

    public RenderTexture GetVoxelPoolRadiance() => m_VoxelPoolRadiance;

    public void Init()
    {
        frameNumberRenderThread = 0;

        m_VoxelLightingCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelLighting/VoxelLighting.compute");
        m_VoxelPoolInitCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelPoolInit.compute");

        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        m_ProbeSphereMesh = sphere.GetComponent<MeshFilter>().mesh;
        //sphere.SetActive(false);
    }

    public void Release()
    {
        m_ValidVoxelCounter?.Release();
        m_ValidVoxelBuffer?.Release();
        m_VoxelLightingIndirectArgs?.Release();
        m_VoxelPoolRadiance?.Release();

        m_ValidProbeCounter?.Release();
        m_ValidProbeBuffer?.Release();
        for (int i = 0; i < m_ProbePositionOffsetVolume.Length; i++)
        {
            m_ProbePositionOffsetVolume[i].Release();
        }

        m_IrradianceProbeGatherIndirectArgs?.Release();
        for (int i = 0; i < m_ProbeIrradianceCache.Length; i++)
        {
            m_ProbeIrradianceCache[i].Release();
        }

        m_RadianceProbeAtlas.Release();

        m_RadianceProbeAllocator.Release();
        m_RadianceProbeFreeList.Release();
        m_RadianceProbeReleaseList.Release();
        m_RadianceProbeIdVolume.Release();
        m_RadianceProbeReleaseIndirectArgs.Release();
        m_RadianceProbeCaptureIndirectArgs.Release();

        m_ValidVoxelCounter = null;
        m_ValidVoxelBuffer = null;
        m_VoxelLightingIndirectArgs = null;
        m_VoxelPoolRadiance = null;

        m_ValidProbeCounter = null;
        m_ValidProbeBuffer = null;
        for (int i = 0; i < m_ProbePositionOffsetVolume.Length; i++)
        {
            m_ProbePositionOffsetVolume[i] = null;
        }

        m_IrradianceProbeGatherIndirectArgs = null;
        for (int i = 0; i < m_ProbeIrradianceCache.Length; i++)
        {
            m_ProbeIrradianceCache[i] = null;
        }

        m_RadianceProbeAtlas = null;

        m_RadianceProbeAllocator = null;
        m_RadianceProbeFreeList = null;
        m_RadianceProbeReleaseList = null;
        m_RadianceProbeIdVolume = null;
        m_RadianceProbeReleaseIndirectArgs = null;
        m_RadianceProbeCaptureIndirectArgs = null;
    }

    public void Update(ref RenderingData renderingData, MiraiGIGPUScene scene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Update radiance cache");

        PrepareRenderResources(scene);

        int cascadeIdMax = MiraiGIClipmap.CASCADE_COUNT - 1;
        for (int cascadeId = cascadeIdMax; cascadeId >= 0; cascadeId -= 1)
        {
            if (GlobalSettings.Instance.freezeLightingForDebug > 0)
            {
                continue;
            }

            PickValidVoxel(cmd, scene, cascadeId);

            VoxelLighting(cmd, ref renderingData, scene, cascadeId);

            PickValidProbe(cmd, scene, cascadeId);

            bool needRadianceProbe = GlobalSettings.Instance.irradianceProbeSampleRadianceProbe > 0;
            if (cascadeId == cascadeIdMax && needRadianceProbe)
            {
                RadianceProbeCapture(cmd, scene, cascadeId);
            }

            IrradianceProbeGather(cmd, scene, cascadeId);
        }

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        // forbid overflow
        frameNumberRenderThread = (frameNumberRenderThread + 1) % 8;
    }

    public void PrepareRenderResources(MiraiGIGPUScene scene)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;

        // --------------------------------------------
        // Voxel Radiance Cache Prepare
        // --------------------------------------------
        Vector3Int pageCountInXYZ = clipmap.voxelPageCountInXYZ;
        Vector3Int voxelRadiancePoolSize = pageCountInXYZ * GlobalShared.VOXEL_BLOCK_SIZE;
        voxelRadiancePoolSize.z *= 2;// for two side voxel
        // TODO: pool size change
        if (m_VoxelPoolRadiance == null)
        {
            m_VoxelPoolRadiance = new RenderTexture(voxelRadiancePoolSize.x, voxelRadiancePoolSize.y, 0, RenderTextureFormat.RGB111110Float);
            m_VoxelPoolRadiance.dimension = TextureDimension.Tex3D;
            m_VoxelPoolRadiance.volumeDepth = voxelRadiancePoolSize.z;
            m_VoxelPoolRadiance.enableRandomWrite = true;
            m_VoxelPoolRadiance.Create();

            CommandBuffer cmd = CommandBufferPool.Get("Init Voxel Pool Radiance");
            cmd.SetComputeTextureParam(m_VoxelPoolInitCS, 1, Shader.PropertyToID("_RWVoxelPoolRadiance"), m_VoxelPoolRadiance);
            cmd.DispatchCompute(m_VoxelPoolInitCS, 1, voxelRadiancePoolSize.x / 4, voxelRadiancePoolSize.y / 4, voxelRadiancePoolSize.z / 8);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        if (m_ValidVoxelCounter == null)
        {
            m_ValidVoxelCounter = new ComputeBuffer(1, sizeof(int));
            m_ValidVoxelCounterInitData = new int[1] { 0 };
        }

        Vector3Int blockCountInXYZ = clipmap.voxelResolution / GlobalShared.VOXEL_BLOCK_SIZE;
        int blockCountToLight1D = blockCountInXYZ.x * blockCountInXYZ.y * blockCountInXYZ.z;
        if (m_ValidVoxelBuffer == null)
        {
            m_ValidVoxelBuffer = new ComputeBuffer(blockCountToLight1D * GlobalShared.VOXEL_COUNT_PER_BLOCK, sizeof(int), ComputeBufferType.Structured);
            m_ValidVoxelBufferInitData = new int[blockCountToLight1D * GlobalShared.VOXEL_COUNT_PER_BLOCK];
            for (int i = 0; i < blockCountToLight1D * GlobalShared.VOXEL_COUNT_PER_BLOCK; i++)
            {
                m_ValidVoxelBufferInitData[i] = 0;
            }
        }

        if (m_VoxelLightingIndirectArgs == null)
        {
            m_VoxelLightingIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
        }

        // --------------------------------------------
        // Probe Prepare
        // --------------------------------------------
        Vector3Int probeCountInXYZ = blockCountInXYZ;
        Vector3Int probeVolumeResolution = probeCountInXYZ;
        probeVolumeResolution.z *= 7;

        if (m_ValidProbeCounter == null)
        {
            m_ValidProbeCounter = new ComputeBuffer(1, sizeof(int));
            m_ValidProbeCounterInitData = new int[1] { 0 };
        }

        if (m_ValidProbeBuffer == null)
        {
            m_ValidProbeBuffer = new ComputeBuffer(blockCountToLight1D, sizeof(int), ComputeBufferType.Structured);
            m_ValidProbeBufferInitData = new int[blockCountToLight1D];
            for (int i = 0; i < blockCountToLight1D; i++)
            {
                m_ValidProbeBufferInitData[i] = 0;
            }
        }

        if (m_IrradianceProbeGatherIndirectArgs == null)
        {
            m_IrradianceProbeGatherIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
        }

        if (m_ProbeIrradianceCache == null)
        {
            m_ProbeIrradianceCache = new RenderTexture[MiraiGIClipmap.CASCADE_COUNT];
            m_ProbePositionOffsetVolume = new RenderTexture[MiraiGIClipmap.CASCADE_COUNT];

            for (int cascadeId = 0; cascadeId < MiraiGIClipmap.CASCADE_COUNT; cascadeId++)
            {
                m_ProbeIrradianceCache[cascadeId] = new RenderTexture(probeVolumeResolution.x, probeVolumeResolution.y, 0, RenderTextureFormat.ARGBFloat);
                m_ProbeIrradianceCache[cascadeId].dimension = TextureDimension.Tex3D;
                m_ProbeIrradianceCache[cascadeId].volumeDepth = probeVolumeResolution.z;
                m_ProbeIrradianceCache[cascadeId].enableRandomWrite = true;
                m_ProbeIrradianceCache[cascadeId].Create();

                m_ProbePositionOffsetVolume[cascadeId] = new RenderTexture(probeCountInXYZ.x, probeCountInXYZ.y, 0, RenderTextureFormat.ARGB32);
                m_ProbePositionOffsetVolume[cascadeId].dimension = TextureDimension.Tex3D;
                m_ProbePositionOffsetVolume[cascadeId].volumeDepth = probeCountInXYZ.z;
                m_ProbePositionOffsetVolume[cascadeId].enableRandomWrite = true;
                m_ProbePositionOffsetVolume[cascadeId].Create();

                // TODO: init
            }
        }

        // --------------------------------------------
        // Probe Allocate
        // --------------------------------------------
        m_RadianceProbeResolution = GlobalSettings.Instance.radianceProbeResolution;
        Vector2Int atlasResolution = m_RadianceProbeCountInAtlasXY * m_RadianceProbeResolution;
        if (m_RadianceProbeAtlas == null)
        {
            m_RadianceProbeAtlas = new RenderTexture(atlasResolution.x, atlasResolution.y, 0, RenderTextureFormat.RGB111110Float);
            m_RadianceProbeAtlas.enableRandomWrite = true;
            m_RadianceProbeAtlas.Create();
        }

        if (m_RadianceProbeDistanceAtlas == null)
        {
            m_RadianceProbeDistanceAtlas = new RenderTexture(atlasResolution.x, atlasResolution.y, 0, RenderTextureFormat.R16);
            m_RadianceProbeDistanceAtlas.enableRandomWrite = true;
            m_RadianceProbeDistanceAtlas.Create();
        }

        if (m_RadianceProbeIdVolume == null)
        {
            m_RadianceProbeIdVolume = new RenderTexture(probeCountInXYZ.x, probeCountInXYZ.y, 0, RenderTextureFormat.RInt);
            m_RadianceProbeIdVolume.dimension = TextureDimension.Tex3D;
            m_RadianceProbeIdVolume.volumeDepth = probeCountInXYZ.z;
            m_RadianceProbeIdVolume.enableRandomWrite = true;
            m_RadianceProbeIdVolume.Create();

            // TODO: init
        }

        if (m_RadianceProbeAllocator == null)
        {
            m_RadianceProbeAllocator = new ComputeBuffer(4, sizeof(int));
            m_RadianceProbeAllocatorInitData = new int[4] { 0, 0, 0, 0 };
            m_RadianceProbeAllocator.SetData(m_RadianceProbeAllocatorInitData);
        }

        int radianceProbeCount = m_RadianceProbeCountInAtlasXY.x * m_RadianceProbeCountInAtlasXY.y;
        if (m_RadianceProbeFreeList == null)
        {
            m_RadianceProbeFreeList = new ComputeBuffer(radianceProbeCount, sizeof(int));
            m_RadianceProbeFreeListInitData = new int[radianceProbeCount];
            for (int i = 0; i < radianceProbeCount; i++)
            {
                m_RadianceProbeFreeListInitData[i] = i;
            }
            m_RadianceProbeFreeList.SetData(m_RadianceProbeFreeListInitData);
        }

        if (m_RadianceProbeReleaseList == null)
        {
            m_RadianceProbeReleaseList = new ComputeBuffer(radianceProbeCount, sizeof(int));
        }

        if (m_RadianceProbeReleaseIndirectArgs == null)
        {
            m_RadianceProbeReleaseIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
        }

        if (m_RadianceProbeCaptureIndirectArgs == null)
        {
            m_RadianceProbeCaptureIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
        }

        // --------------------------------------------
        // Flush data per frame
        // --------------------------------------------
        m_ValidVoxelCounter.SetData(m_ValidVoxelCounterInitData);
        m_ValidVoxelBuffer.SetData(m_ValidVoxelBufferInitData);
        m_ValidProbeCounter.SetData(m_ValidProbeCounterInitData);
        m_ValidProbeBuffer.SetData(m_ValidProbeBufferInitData);
    }

    public void PickValidVoxel(CommandBuffer cmd, MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo clipmapInfo = clipmap.cascadeInfos[cascadeId];

        m_VoxelLightingCheckerBoardSize = GlobalSettings.Instance.voxelLightingCheckerBoardSize;
        m_VoxelLightingCheckerBoardSize = Mathf.NextPowerOfTwo(m_VoxelLightingCheckerBoardSize);
        m_VoxelLightingCheckerBoardSize = Mathf.Clamp(m_VoxelLightingCheckerBoardSize, 1, 4);

        Vector3Int blockCountInXYZ = clipmap.voxelResolution / GlobalShared.VOXEL_BLOCK_SIZE;
        Vector3Int blockCountToLightInXYZ = blockCountInXYZ / m_VoxelLightingCheckerBoardSize;
        int maxFrameNum = m_VoxelLightingCheckerBoardSize * m_VoxelLightingCheckerBoardSize * m_VoxelLightingCheckerBoardSize;
        Vector3Int checkerBoardOffset = GlobalShared.Index1DTo3DLinear((int)frameNumberRenderThread % maxFrameNum, 
                                                                        new Vector3Int(m_VoxelLightingCheckerBoardSize, m_VoxelLightingCheckerBoardSize, m_VoxelLightingCheckerBoardSize));

        int kernel = m_VoxelLightingCS.FindKernel("PickValidVoxel");

        cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeIndex"), cascadeId);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeResolution"), (Vector3) clipmap.voxelResolution);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeMoveOffset"), (Vector3) clipmapInfo.moveOffset);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CheckerBoardInfo"), new Vector4(checkerBoardOffset.x, checkerBoardOffset.y, checkerBoardOffset.z, m_VoxelLightingCheckerBoardSize));

        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelBitOccupyClipmap"), clipmap.GetVoxelMap());

        cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWValidVoxelCounter"), m_ValidVoxelCounter);
        cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWValidVoxelBuffer"), m_ValidVoxelBuffer);

        cmd.DispatchCompute(m_VoxelLightingCS, kernel, blockCountToLightInXYZ.x / 4, blockCountToLightInXYZ.y / 4, blockCountToLightInXYZ.z / 4);
    }

    public void VoxelLighting(CommandBuffer cmd, ref RenderingData renderingData, MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo cascadeInfo = clipmap.cascadeInfos[cascadeId];
        MiraiGICascadeInfo[] cascadeInfos = clipmap.cascadeInfos;

        // get main light
        GameObject mainLightObject = GameObject.Find("Directional Light");
        Light mainLight = mainLightObject.GetComponent<Light>();
        Vector3 mainLightDirection = mainLight.transform.forward * -1;
        Color mainLightColor = mainLight.color;

        // get main light shadow
        Matrix4x4[] worldToShadowMatrices = new Matrix4x4[4];
        Vector4[] shadowBounds = new Vector4[4];
        RenderTexture shadowDepthTexture = null;
        if (mainLight)
        {
            GetMainLightShadowInfos(mainLight, ref renderingData, out worldToShadowMatrices, out shadowBounds, out shadowDepthTexture);
        }

        // 1. build indirect args cause valid voxel num is unpredictable
        {
            int kernel = m_VoxelLightingCS.FindKernel("BuildVoxelLightingIndirectArgs");

            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_ThreadsCountForVoxelLighting"), 8);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidVoxelCounter"), m_ValidVoxelCounter);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWIndirectArgs"), m_VoxelLightingIndirectArgs);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, 1, 1, 1);
        }

        // 2. calculate lighting for each voxel
        {
            int kernel = m_VoxelLightingCS.FindKernel("VoxelLighting");

            // voxel RT
            SetupVoxelRaytracingParameters(cmd, m_VoxelLightingCS, kernel, scene, cascadeId);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidVoxelCounter"), m_ValidVoxelCounter);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidVoxelBuffer"), m_ValidVoxelBuffer);

            // light && shadow
            cmd.SetComputeMatrixParam(m_VoxelLightingCS, Shader.PropertyToID("_CameraViewProjectionMatrix"), Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_MainLightDirection"), mainLightDirection);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_MainLightColor"), mainLightColor);
            cmd.SetComputeFloatParam(m_VoxelLightingCS, Shader.PropertyToID("_ShadowRayMaxDistance"), GlobalSettings.Instance.shadowRayMaxDistance);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_ShadowRayBoostClipmapOffset"), GlobalSettings.Instance.shadowRayBoostClipmapOffset);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CSMNumCascades"), 4);
            cmd.SetComputeVectorArrayParam(m_VoxelLightingCS, Shader.PropertyToID("_CSMShadowBounds"), shadowBounds);
            cmd.SetComputeMatrixArrayParam(m_VoxelLightingCS, Shader.PropertyToID("_CSMWorldToShadowMatrices"), worldToShadowMatrices);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ShadowDepthTexture"), shadowDepthTexture);

            // indirect lighting
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ProbeIrradianceCache"), m_ProbeIrradianceCache[cascadeId]);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ProbePositionOffsetVolume"), m_ProbePositionOffsetVolume[cascadeId]);

            // output
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWVoxelPoolRadiance"), m_VoxelPoolRadiance);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, m_VoxelLightingIndirectArgs, 0);
        }
    }

    void PickValidProbe(CommandBuffer cmd, MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo cascadeInfo = clipmap.cascadeInfos[cascadeId];

        int checkerBoardSize = GlobalSettings.Instance.probeUpdateCheckerBoardSize;
        checkerBoardSize = Mathf.NextPowerOfTwo(checkerBoardSize);
        checkerBoardSize = Mathf.Clamp(checkerBoardSize, 1, 4);

        // one thread for one block, one block for one probe
        Vector3Int probeCountInXYZ = clipmap.voxelResolution / GlobalShared.VOXEL_BLOCK_SIZE;
        int maxFrameNum = checkerBoardSize * checkerBoardSize * checkerBoardSize;
        Vector3Int checkerBoardOffset = GlobalShared.Index1DTo3DLinear((int)frameNumberRenderThread % maxFrameNum, 
                                                                        new Vector3Int(checkerBoardSize, checkerBoardSize, checkerBoardSize));

        int kernel = m_VoxelLightingCS.FindKernel("PickValidProbe");

        cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeIndex"), cascadeId);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeResolution"), (Vector3) clipmap.voxelResolution);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeMoveOffset"), (Vector3)cascadeInfo.moveOffset);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CheckerBoardInfo"), 
                                new Vector4(checkerBoardOffset.x, checkerBoardOffset.y, checkerBoardOffset.z, checkerBoardSize));

        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelBitOccupyClipmap"), clipmap.GetVoxelMap());
        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWProbePositionOffsetVolume"), m_ProbePositionOffsetVolume[cascadeId]);
        cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWValidProbeCounter"), m_ValidProbeCounter);
        cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWValidProbeBuffer"), m_ValidProbeBuffer);

        Vector3Int probeCountToUpdateInXYZ = probeCountInXYZ / checkerBoardSize;
        cmd.DispatchCompute(m_VoxelLightingCS, kernel, probeCountToUpdateInXYZ.x / 4, probeCountToUpdateInXYZ.y / 4, probeCountToUpdateInXYZ.z / 4);
    }

    void RadianceProbeCapture(CommandBuffer cmd, MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo[] cascadeInfos = clipmap.cascadeInfos;
        MiraiGICascadeInfo cascadeInfo = clipmap.cascadeInfos[cascadeId];

        int checkerBoardSize = m_ProbeUpdateCheckerBoardSize;
        int maxFrameNum = checkerBoardSize * checkerBoardSize * checkerBoardSize;
        Vector3Int checkerBoardOffset = GlobalShared.Index1DTo3DLinear((int)frameNumberRenderThread % maxFrameNum, 
                                                                        new Vector3Int(checkerBoardSize, checkerBoardSize, checkerBoardSize));

        int radianceProbeCount = m_RadianceProbeCountInAtlasXY.x * m_RadianceProbeCountInAtlasXY.y;
        Vector3Int probeCountInXYZ = clipmap.voxelResolution / GlobalShared.VOXEL_BLOCK_SIZE;
        Vector3Int probeCountToUpdateInXYZ = probeCountInXYZ / checkerBoardSize;

        // 1. allocate atlas space for radiance probe
        {
            int kernel = m_VoxelLightingCS.FindKernel("RadianceProbeAllocate");

            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeIndex"), cascadeId);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeResolution"), (Vector3) clipmap.voxelResolution);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeMoveOffset"), (Vector3) cascadeInfo.moveOffset);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CheckerBoardInfo"),
                                        new Vector4(checkerBoardOffset.x, checkerBoardOffset.y, checkerBoardOffset.z, checkerBoardSize));
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_RadianceProbeCount"), radianceProbeCount);

            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeAllocator"), m_RadianceProbeAllocator);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeFreeList"), m_RadianceProbeFreeList);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeReleaseList"), m_RadianceProbeReleaseList);

            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ProbePositionOffsetVolume"), m_ProbePositionOffsetVolume[cascadeId]);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeIdVolume"), m_RadianceProbeIdVolume);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, probeCountToUpdateInXYZ.x / 4, probeCountToUpdateInXYZ.y / 4, probeCountToUpdateInXYZ.z / 4 );
        }

        // 2. build indirect args for probe release and probe capture
        {
            int kernel = m_VoxelLightingCS.FindKernel("BuildRadianceProbeReleaseAndCaptureIndirectArgs");

            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_RadianceProbeCount"), radianceProbeCount);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeResolution"), (Vector3) clipmap.voxelResolution);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_RadianceProbeResolution"), m_RadianceProbeResolution);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_ThreadCountForProbeRelease"), 8);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_ThreadCountForProbeCapture"), new Vector2(8, 8));
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidProbeCounter"), m_ValidProbeCounter);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeAllocator"), m_RadianceProbeAllocator);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWProbeReleaseIndirectArgs"), m_RadianceProbeReleaseIndirectArgs);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWProbeCaptureIndirectArgs"), m_RadianceProbeCaptureIndirectArgs);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, 1, 1, 1);
        }

        // 3. probe release
        {
            int kernel = m_VoxelLightingCS.FindKernel("RadianceProbeRelease");

            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeAllocator"), m_RadianceProbeAllocator);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeFreeList"), m_RadianceProbeFreeList);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeReleaseList"), m_RadianceProbeReleaseList);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, m_RadianceProbeReleaseIndirectArgs, 0);
        }

        // 4. probe capture
        {
            int kernel = m_VoxelLightingCS.FindKernel("RadianceProbeCapture");

            // voxel RT
            SetupVoxelRaytracingParameters(cmd, m_VoxelLightingCS, kernel, scene, cascadeId);

            // pass
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidProbeBuffer"), m_ValidProbeBuffer);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_RadianceProbeResolution"), m_RadianceProbeResolution);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_RadianceProbeCountInAtlasXY"), (Vector2) m_RadianceProbeCountInAtlasXY);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ProbePositionOffsetVolume"), m_ProbePositionOffsetVolume[cascadeId]);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RadianceProbeIdVolume"), m_RadianceProbeIdVolume);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeAtlas"), m_RadianceProbeAtlas);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWRadianceProbeDistanceAtlas"), m_RadianceProbeDistanceAtlas);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, m_RadianceProbeCaptureIndirectArgs, 0);
        }
    }

    void IrradianceProbeGather(CommandBuffer cmd, MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo[] cascadeInfos = clipmap.cascadeInfos;

        // 1. build indirect args
        {
            int kernel = m_VoxelLightingCS.FindKernel("BuildIrradianceProbeGatherIndirectArgs");

            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidProbeCounter"), m_ValidProbeCounter);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWIndirectArgs"), m_IrradianceProbeGatherIndirectArgs);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, 1, 1, 1);
        }

        // 2. probe gather irradiance
        // note: one group for one probe, one thread for one ray
        {
            bool sampleRadianceProbe = GlobalSettings.Instance.irradianceProbeSampleRadianceProbe > 0;
            if (sampleRadianceProbe)
            {
                m_VoxelLightingCS.EnableKeyword("USE_RADIANCE_PROBE_AS_FALLBACK");
            }
            else
            {
                m_VoxelLightingCS.DisableKeyword("USE_RADIANCE_PROBE_AS_FALLBACK");
            }

            int kernel = m_VoxelLightingCS.FindKernel("IrradianceProbeGather");

            // voxel RT
            SetupVoxelRaytracingParameters(cmd, m_VoxelLightingCS, kernel, scene, cascadeId);

            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_RadianceProbeResolution"), m_RadianceProbeResolution);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_RadianceProbeCountInAtlasXY"), (Vector2)m_RadianceProbeCountInAtlasXY);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidProbeBuffer"), m_ValidProbeBuffer);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ProbePositionOffsetVolume"), m_ProbePositionOffsetVolume[cascadeId]);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RadianceProbeAtlas"), m_RadianceProbeAtlas);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RadianceProbeDistanceAtlas"), m_RadianceProbeDistanceAtlas);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RadianceProbeIdVolume"), m_RadianceProbeIdVolume);

            int maxCascadeId = cascadeInfos.Length - 1;
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RadianceProbePositionOffsetVolume"), m_ProbePositionOffsetVolume[maxCascadeId]);

            // output
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWProbeIrradianceCache"), m_ProbeIrradianceCache[cascadeId]);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, m_IrradianceProbeGatherIndirectArgs, 0);
        }
    }

    void SetupVoxelRaytracingParameters(CommandBuffer cmd, ComputeShader computeShader, int kernel,
                                        MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo[] cascadeInfos = clipmap.cascadeInfos;

        Vector4[] cascadeCenterArray = new Vector4[MiraiGIClipmap.MAX_CASCADE_COUNT];
        Vector4[] cascadeSizeArray = new Vector4[MiraiGIClipmap.MAX_CASCADE_COUNT];
        Vector4[] cascadeMoveOffsetArray = new Vector4[MiraiGIClipmap.MAX_CASCADE_COUNT];

        for (int cascadeIndex = 0; cascadeIndex < cascadeInfos.Length; cascadeIndex++)
        {
            MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];
            cascadeCenterArray[cascadeIndex] = cascadeInfo.cascadeCenter;
            cascadeSizeArray[cascadeIndex] = cascadeInfo.cascadeSize;
            cascadeMoveOffsetArray[cascadeIndex] = (Vector3)cascadeInfo.moveOffset;
        }

        // volume
        cmd.SetComputeIntParam(computeShader, Shader.PropertyToID("_CascadeIndex"), cascadeId);
        cmd.SetComputeIntParam(computeShader, Shader.PropertyToID("_CascadeCount"), cascadeInfos.Length);

        cmd.SetComputeVectorParam(computeShader, Shader.PropertyToID("_CascadeResolution"), (Vector3)clipmap.voxelResolution);
        cmd.SetComputeVectorArrayParam(computeShader, Shader.PropertyToID("_CascadeCenterArray"), cascadeCenterArray);
        cmd.SetComputeVectorArrayParam(computeShader, Shader.PropertyToID("_CascadeSizeArray"), cascadeSizeArray);
        cmd.SetComputeVectorArrayParam(computeShader, Shader.PropertyToID("_CascadeMoveOffsetArray"), cascadeMoveOffsetArray);

        // voxel
        cmd.SetComputeVectorParam(computeShader, Shader.PropertyToID("_VoxelPageCountInXYZ"), (Vector3)clipmap.voxelPageCountInXYZ);
        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelBitOccupyClipmap"), clipmap.GetVoxelMap());
        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPageClipmap"), clipmap.GetVoxelPageClipmap());
        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPoolBaseColor"), clipmap.GetVoxelPoolBaseColor());
        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPoolNormal"), clipmap.GetVoxelPoolNormal());
        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPoolEmissive"), clipmap.GetVoxelPoolEmissive());
        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPoolRadiance"), m_VoxelPoolRadiance);
    }

    public void VisualizeProbe(CommandBuffer cmd, MiraiGIGPUScene scene, ProbeVisualizeMode visualizeMode)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo[] cascadeInfos = clipmap.cascadeInfos;

        int cascadeId = (visualizeMode == ProbeVisualizeMode.IrradianceProbe)
                            ? (GlobalSettings.Instance.visualizeIrradianceProbe - 1)
                            : ((GlobalSettings.Instance.visualizeRadianceProbe > 0) ? (cascadeInfos.Length - 1) : -1);

        if (cascadeId < 0)
        {
            return;
        }

        Shader probeVisualizeShader = Shader.Find("Mirai/VisualizeProbe");
        Material probeVisualizeMaterial = new Material(probeVisualizeShader);
        probeVisualizeMaterial.enableInstancing = true;

        cmd.SetRenderTarget(clipmap.GetVisualizeColorTarget(), Shader.GetGlobalTexture("_CameraDepthTexture"));

        probeVisualizeMaterial.SetInt(Shader.PropertyToID("_VisualizeMode"), (visualizeMode == ProbeVisualizeMode.IrradianceProbe ? 0 : 1));
        probeVisualizeMaterial.SetInt(Shader.PropertyToID("_RadianceProbeResolution"), m_RadianceProbeResolution);
        probeVisualizeMaterial.SetVector(Shader.PropertyToID("_RadianceProbeCountInAtlasXY"), (Vector2) m_RadianceProbeCountInAtlasXY);
        probeVisualizeMaterial.SetInt(Shader.PropertyToID("_CascadeIndex"), cascadeId);
        probeVisualizeMaterial.SetInt(Shader.PropertyToID("_CascadeCount"), MiraiGIClipmap.CASCADE_COUNT);
        probeVisualizeMaterial.SetVector(Shader.PropertyToID("_CascadeResolution"), (Vector3)clipmap.voxelResolution);
        probeVisualizeMaterial.SetVector(Shader.PropertyToID("_CascadeCenter"), cascadeInfos[cascadeId].cascadeCenter);
        probeVisualizeMaterial.SetVector(Shader.PropertyToID("_CascadeSize"), cascadeInfos[cascadeId].cascadeSize);
        probeVisualizeMaterial.SetVector(Shader.PropertyToID("_CascadeMoveOffset"), (Vector3)cascadeInfos[cascadeId].moveOffset);
        probeVisualizeMaterial.SetTexture(Shader.PropertyToID("_VoxelBitOccupyClipmap"), clipmap.GetVoxelMap());
        probeVisualizeMaterial.SetTexture(Shader.PropertyToID("_ProbeIrradianceCache"), m_ProbeIrradianceCache[cascadeId]);
        probeVisualizeMaterial.SetTexture(Shader.PropertyToID("_ProbePositionOffsetVolume"), m_ProbePositionOffsetVolume[cascadeId]);
        probeVisualizeMaterial.SetTexture(Shader.PropertyToID("_RadianceProbeAtlas"), m_RadianceProbeAtlas);
        probeVisualizeMaterial.SetTexture(Shader.PropertyToID("_RadianceProbeIdVolume"), m_RadianceProbeIdVolume);
        probeVisualizeMaterial.SetMatrix(Shader.PropertyToID("_CameraViewProjection"), Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix);

        Vector3Int probeCountInXYZ = clipmap.voxelResolution / GlobalShared.VOXEL_BLOCK_SIZE;
        int instanceCount = probeCountInXYZ.x * probeCountInXYZ.y * probeCountInXYZ.z;

        // TODO: low efficience, limited with Unity of DrawMeshInstanced (max 511 instances per drawcall)
        Matrix4x4[] instanceMatrices = new Matrix4x4[instanceCount];
        MaterialPropertyBlock props = new MaterialPropertyBlock();
        for (int i = 0; i < instanceCount; i++)
        {
            instanceMatrices[i] = Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix;
            props.SetInt(Shader.PropertyToID("_ProbeIndex"), i);
            cmd.DrawMesh(m_ProbeSphereMesh, instanceMatrices[i], probeVisualizeMaterial, 0, 0, props);
        }
        //cmd.DrawMeshInstanced(m_ProbeSphereMesh, 0, probeVisualizeMaterial, 0, instanceMatrices, instanceCount);
    }

    void GetMainLightShadowInfos(Light light, ref RenderingData renderingData,
        out Matrix4x4[] outWorldToShadowMatrices, out Vector4[] outShadowBounds, out RenderTexture outShadowDepthTexture)
    {
        outWorldToShadowMatrices = new Matrix4x4[4];
        outShadowBounds = new Vector4[4];

        Light mainLight = GameObject.Find("Directional Light").GetComponent<Light>();
        int shadowLightIndex = renderingData.lightData.mainLightIndex;
        int renderTargetWidth = renderingData.shadowData.mainLightShadowmapWidth;
        int renderTargetHeight = renderingData.shadowData.mainLightShadowmapHeight;
        int shadowResolution = ShadowUtils.GetMaxTileResolutionInAtlas(renderingData.shadowData.mainLightShadowmapWidth,
                renderingData.shadowData.mainLightShadowmapHeight, 4);

        ShadowSliceData[] cascadeSlices = new ShadowSliceData[4];
        Vector4[] cascadeSplitDistances = new Vector4[4];

        for (int cascadeIndex = 0; cascadeIndex < 4; cascadeIndex++)
        {
            bool success = ShadowUtils.ExtractDirectionalLightMatrix(ref renderingData.cullResults, ref renderingData.shadowData,
                    shadowLightIndex, cascadeIndex, renderTargetWidth, renderTargetHeight, shadowResolution, mainLight.shadowNearPlane,
                    out cascadeSplitDistances[cascadeIndex], out cascadeSlices[cascadeIndex]);

            if (success)
            {
                // world to shadow matrices
                outWorldToShadowMatrices[cascadeIndex] = cascadeSlices[cascadeIndex].shadowTransform;
                // shadow bounds
                outShadowBounds[cascadeIndex] = cascadeSplitDistances[cascadeIndex];
            }
        }

        outShadowDepthTexture = (RenderTexture) Shader.GetGlobalTexture("_MainLightShadowmapTexture");
    }
}