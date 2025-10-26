using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static Unity.VisualScripting.Dependencies.Sqlite.SQLiteConnection;

public class MiraiGICascadeInfo
{
    public Queue<int> pendingUpdateChunks = new Queue<int>();
    public Vector3 cascadeCenter;
    public Vector3 cascadeSize = new Vector3(32.0f, 32.0f, 32.0f);
    public Vector3Int moveOffset;

    public int numChunksToUpdate;
};

public struct ObjectCullParams
{
    public Vector4 cascadeCenter;
    public Vector4 cascadeSize;
    public Vector4 cascadeResolution;
    public Vector4 updateChunkResolution;
    public int numObjects;
    public int numUpdateChunks;
    public int numThreadsForCulling;
    public int maxObjectNumPerUpdateChunk;
}

// ---------------------------------------------------
// Clipmap data is per view info
// TODO: support muti-camera view
// ---------------------------------------------------
public class MiraiGIClipmap
{
    Vector3Int m_VoxelResolution = new Vector3Int(128, 128, 128);
    Vector3Int m_UpdateChunkResolution = new Vector3Int(16, 16, 16);
    const int CASCADE_COUNT = 1;
    const int MAX_OBJECT_NUM_PER_CASCADE = 2048;
    const int MAX_UPDATE_CHUNK_PER_FRAME = 16;
    const int MAX_OBJECT_NUM_PER_UPDATE_CHUNK = 64;
    const int MAX_CASCADE_COUNT = 4;
    const int VOXEL_BLOCK_SIZE = 4;

    RenderTexture m_VoxelMap;
    RenderTexture m_VisualizeColorTarget;
    RenderTexture m_VisualizeDepthTarget;

    MiraiGICascadeInfo[] m_CascadeInfos;
    ObjectCullParams[] m_ObjectCullParams;
    ComputeBuffer[] m_ObjectCullParamsCB;

    ComputeBuffer m_UpdateChunkList;
    ComputeBuffer m_ClipmapObjectCounter;
    ComputeBuffer m_ClipmapCullingResult;
    ComputeBuffer m_UpdateChunkCullingIndirectArgs;
    // 2D array as 1D, single row represent an update chunk
    ComputeBuffer m_UpdateChunkCullingResults;
    ComputeBuffer m_UpdateChunkObjectCounter;

    ComputeShader m_CullObjectCS;
    ComputeShader m_VoxelInjectCS;
    ComputeShader m_VisualizeClipmapCS;

    public void CreateClipmap()
    {
        m_VoxelMap = new RenderTexture(m_VoxelResolution.x / VOXEL_BLOCK_SIZE, m_VoxelResolution.y / VOXEL_BLOCK_SIZE, 0, RenderTextureFormat.RGInt);
        m_VoxelMap.dimension = TextureDimension.Tex3D;
        m_VoxelMap.volumeDepth = m_VoxelResolution.z * CASCADE_COUNT / VOXEL_BLOCK_SIZE;
        m_VoxelMap.enableRandomWrite = true;
        m_VoxelMap.Create();

        m_VisualizeColorTarget = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 0, RenderTextureFormat.RGB111110Float);
        m_VisualizeColorTarget.enableRandomWrite = true;
        m_VisualizeColorTarget.Create();
        m_VisualizeDepthTarget = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 0, RenderTextureFormat.RFloat);
        m_VisualizeDepthTarget.Create();

        m_CascadeInfos = new MiraiGICascadeInfo[CASCADE_COUNT];
        m_ObjectCullParams = new ObjectCullParams[CASCADE_COUNT];
        m_ObjectCullParamsCB = new ComputeBuffer[CASCADE_COUNT];

        Vector3Int updateChunkDimension = new Vector3Int(
            m_VoxelResolution.x / m_UpdateChunkResolution.x,
            m_VoxelResolution.y / m_UpdateChunkResolution.y,
            m_VoxelResolution.z / m_UpdateChunkResolution.z
        );
        int updateChunkCount = updateChunkDimension.x * updateChunkDimension.y * updateChunkDimension.z;

        for (int cascadeId = 0; cascadeId < CASCADE_COUNT; cascadeId++)
        {
            m_CascadeInfos[cascadeId] = new MiraiGICascadeInfo();
            for (int chunkId = 0; chunkId < updateChunkCount; chunkId++)
            {
                m_CascadeInfos[cascadeId].pendingUpdateChunks.Enqueue(chunkId);
            }

            m_ObjectCullParams[cascadeId] = new ObjectCullParams();
            m_ObjectCullParamsCB[cascadeId] = new ComputeBuffer(1, Marshal.SizeOf<ObjectCullParams>());
        }

        m_UpdateChunkList = new ComputeBuffer(MAX_UPDATE_CHUNK_PER_FRAME, sizeof(int), ComputeBufferType.Raw);

        m_CullObjectCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/CullObject.compute");
        m_VoxelInjectCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelInject.compute");
        m_VisualizeClipmapCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VisualizeClipmap.compute");
    }

    public void UpdateClipmap(Camera camera, MiraiGIGPUScene gpuScene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Update Clipmap");

        PrepareRenderResources();

        for (int cascadeIndex = 0; cascadeIndex < CASCADE_COUNT; cascadeIndex++)
        {
            UpdateCascadePosition(camera, cascadeIndex);
            UploadChunkIds(gpuScene, camera, cascadeIndex);
            PrepareConstantBuffer(gpuScene, cascadeIndex);
            CullObjectToClipmap(cmd, gpuScene, camera, cascadeIndex);
            CullObjectToUpdateChunk(cmd, gpuScene, camera, cascadeIndex);
            VoxelInject(cmd, gpuScene, cascadeIndex);
        }

        // TODO: solve view bug
        VisualizeClipmap(cmd, camera);

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void Release()
    {
        m_VoxelMap?.Release();
        m_VisualizeColorTarget?.Release();
        m_VisualizeDepthTarget?.Release();
        m_VoxelMap = null;
        m_VisualizeColorTarget = null;
        m_VisualizeDepthTarget = null;

        for (int cascadeId = 0; cascadeId < CASCADE_COUNT; cascadeId++)
        {
            m_ObjectCullParamsCB[cascadeId]?.Release();
            m_ObjectCullParamsCB[cascadeId] = null;
        }
        m_UpdateChunkList?.Release();
        m_ClipmapObjectCounter?.Release();
        m_ClipmapCullingResult?.Release();
        m_UpdateChunkCullingIndirectArgs?.Release();
        m_UpdateChunkCullingResults?.Release();
        m_UpdateChunkObjectCounter?.Release();
        m_UpdateChunkList = null;
        m_ClipmapObjectCounter = null;
        m_ClipmapCullingResult = null;
        m_UpdateChunkCullingIndirectArgs = null;
        m_UpdateChunkCullingResults = null;
        m_UpdateChunkObjectCounter = null;
    }

    void PrepareRenderResources()
    {
        m_ClipmapObjectCounter?.Release();
        m_ClipmapCullingResult?.Release();
        m_UpdateChunkCullingIndirectArgs?.Release();
        m_UpdateChunkCullingResults?.Release();
        m_UpdateChunkObjectCounter?.Release();

        m_ClipmapObjectCounter = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Structured);
        m_ClipmapCullingResult = new ComputeBuffer(MAX_OBJECT_NUM_PER_CASCADE, sizeof(int), ComputeBufferType.Structured);
        m_UpdateChunkCullingIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
        m_UpdateChunkCullingResults = new ComputeBuffer(MAX_UPDATE_CHUNK_PER_FRAME * MAX_OBJECT_NUM_PER_UPDATE_CHUNK, sizeof(int), ComputeBufferType.Structured);
        m_UpdateChunkObjectCounter = new ComputeBuffer(MAX_UPDATE_CHUNK_PER_FRAME, sizeof(int), ComputeBufferType.Structured);

        //int[] chunkCounter = new int[MAX_UPDATE_CHUNK_PER_FRAME];
        //int[] testCounter = new int[MAX_UPDATE_CHUNK_PER_FRAME];
        //for (int i = 0; i < MAX_UPDATE_CHUNK_PER_FRAME; i++)
        //{
        //    chunkCounter[i] = 0;
        //    testCounter[i] = 0;
        //}
        //int[] clipmapCulling = new int[MAX_OBJECT_NUM_PER_CASCADE];
        //for (int i = 0; i < MAX_OBJECT_NUM_PER_CASCADE; i++)
        //{
        //    clipmapCulling[i] = 0;
        //}
        //int[] cullingResults = new int[MAX_UPDATE_CHUNK_PER_FRAME * MAX_OBJECT_NUM_PER_UPDATE_CHUNK];
        //for (int i = 0; i < MAX_UPDATE_CHUNK_PER_FRAME * MAX_OBJECT_NUM_PER_UPDATE_CHUNK; i++)
        //{
        //    cullingResults[i] = 0;
        //}
        //m_ClipmapObjectCounter.SetData(new int[1] {0});
        //m_UpdateChunkObjectCounter.SetData(chunkCounter);
        //m_ClipmapCullingResult.SetData(clipmapCulling);
        //m_UpdateChunkCullingResults.SetData(cullingResults);
    }

    void UpdateCascadePosition(Camera camera, int cascadeIndex)
    {
        Vector3 cameraPosition = camera.transform.position;
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];

        Vector3 voxelSize = new Vector3(cascadeInfo.cascadeSize.x / m_VoxelResolution.x,
                                        cascadeInfo.cascadeSize.y / m_VoxelResolution.y,
                                        cascadeInfo.cascadeSize.z / m_VoxelResolution.z);
        Vector3 voxelBlockSize = voxelSize * VOXEL_BLOCK_SIZE;
        Vector3Int blockResolution = m_VoxelResolution / VOXEL_BLOCK_SIZE;

        Vector3Int cameraBlockId = new Vector3Int(Mathf.CeilToInt(cameraPosition.x / voxelBlockSize.x),
                                                    Mathf.CeilToInt(cameraPosition.y / voxelBlockSize.y),
                                                    Mathf.CeilToInt(cameraPosition.z / voxelBlockSize.z));
        Vector3Int cascadeCenterBlockId = new Vector3Int(Mathf.CeilToInt(cascadeInfo.cascadeCenter.x / voxelBlockSize.x),
                                                            Mathf.CeilToInt(cascadeInfo.cascadeCenter.y / voxelBlockSize.y),
                                                            Mathf.CeilToInt(cascadeInfo.cascadeCenter.z / voxelBlockSize.z));

        // calculate camera move offset based on voxel grid
        // then update cascade center position
        cascadeInfo.moveOffset += cameraBlockId - cascadeCenterBlockId;
        cascadeInfo.moveOffset.x = cascadeInfo.moveOffset.x % blockResolution.x;
        cascadeInfo.moveOffset.y = cascadeInfo.moveOffset.y % blockResolution.y;
        cascadeInfo.moveOffset.z = cascadeInfo.moveOffset.z % blockResolution.z;

        cascadeInfo.cascadeCenter = new Vector3(cameraBlockId.x * voxelBlockSize.x,
                                                cameraBlockId.y * voxelBlockSize.y,
                                                cameraBlockId.z * voxelBlockSize.z);
    }

    void UploadChunkIds(MiraiGIGPUScene scene, Camera camera, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];
        cascadeInfo.numChunksToUpdate = 0;

        int[] chunksToUpdate = new int[MAX_UPDATE_CHUNK_PER_FRAME];

        // fetch from pending queue
        for (int i = 0; i < MAX_UPDATE_CHUNK_PER_FRAME; i++)
        {
            if (cascadeInfo.pendingUpdateChunks.Count == 0)
            {
                break;
            }

            int chunkId = cascadeInfo.pendingUpdateChunks.Peek();
            cascadeInfo.pendingUpdateChunks.Dequeue();

            chunksToUpdate[i] = chunkId;
            cascadeInfo.numChunksToUpdate += 1;
        }

        // for debug, give back to queue
        //{
        //    for (int i = 0; i < cascadeInfo.numChunksToUpdate; i++)
        //    {
        //        int chunkId = chunksToUpdate[i];
        //        cascadeInfo.pendingUpdateChunks.Enqueue(chunkId);
        //    }
        //}

        m_UpdateChunkList.SetData(chunksToUpdate);
    }

    void PrepareConstantBuffer(MiraiGIGPUScene scene, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];
        int numObjects = scene.GPUSceneData.objects.Count;
        ObjectCullParams objectCullParams = m_ObjectCullParams[cascadeIndex];

        objectCullParams.cascadeCenter = cascadeInfo.cascadeCenter;
        objectCullParams.cascadeSize = cascadeInfo.cascadeSize;
        objectCullParams.cascadeResolution = (Vector3)m_VoxelResolution;
        objectCullParams.updateChunkResolution = (Vector3)m_UpdateChunkResolution;
        objectCullParams.numObjects = numObjects;
        objectCullParams.numUpdateChunks = cascadeInfo.numChunksToUpdate;
        objectCullParams.numThreadsForCulling = 8;
        objectCullParams.maxObjectNumPerUpdateChunk = MAX_OBJECT_NUM_PER_UPDATE_CHUNK;

        m_ObjectCullParamsCB[cascadeIndex].SetData(new ObjectCullParams[] { objectCullParams });
    }

    void CullObjectToClipmap(CommandBuffer cmd, MiraiGIGPUScene scene, Camera camera, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];
        int numObjects = scene.GPUSceneData.objects.Count;

        int kernel = m_CullObjectCS.FindKernel("CullObjectToClipmap");
        // compute shader params
        cmd.SetComputeConstantBufferParam(m_CullObjectCS, Shader.PropertyToID("_Params"), m_ObjectCullParamsCB[cascadeIndex], 0 , Marshal.SizeOf<ObjectCullParams>());
        cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_ObjectsInfo"), scene.GPUSceneData.objectInfoBuffer);
        cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_RWClipmapCullingResult"), m_ClipmapCullingResult);
        cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_RWClipmapObjectCounter"), m_ClipmapObjectCounter);

        cmd.DispatchCompute(m_CullObjectCS, kernel, Mathf.CeilToInt((float)numObjects / 8), 1, 1);
    }

    void CullObjectToUpdateChunk(CommandBuffer cmd, MiraiGIGPUScene scene, Camera camera, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];
        int kernel;

        // 1. build indirect dispatch args
        {
            kernel = m_CullObjectCS.FindKernel("BuildUpdateChunkCullingIndirectArgs");
            // compute shader params
            cmd.SetComputeConstantBufferParam(m_CullObjectCS, Shader.PropertyToID("_Params"), m_ObjectCullParamsCB[cascadeIndex], 0, Marshal.SizeOf<ObjectCullParams>());
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_ClipmapObjectCounter"), m_ClipmapObjectCounter);
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_RWIndirectArgs"), m_UpdateChunkCullingIndirectArgs);

            cmd.DispatchCompute(m_CullObjectCS, kernel, 1, 1, 1);
        }

        // 2. dispatch culling thread
        {
            kernel = m_CullObjectCS.FindKernel("CullObjectToUpdateChunk");
            // compute shader params
            cmd.SetComputeConstantBufferParam(m_CullObjectCS, Shader.PropertyToID("_Params"), m_ObjectCullParamsCB[cascadeIndex], 0, Marshal.SizeOf<ObjectCullParams>());
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_UpdateChunkList"), m_UpdateChunkList);
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_ObjectsInfo"), scene.GPUSceneData.objectInfoBuffer);
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_ClipmapCullingResult"), m_ClipmapCullingResult);
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_ClipmapObjectCounter"), m_ClipmapObjectCounter);
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_RWUpdateChunkCullingResult"), m_UpdateChunkCullingResults);
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_RWUpdateChunkObjectCounter"), m_UpdateChunkObjectCounter);

            cmd.DispatchCompute(m_CullObjectCS, kernel, m_UpdateChunkCullingIndirectArgs, 0);
        }
    }

    void VoxelInject(CommandBuffer cmd, MiraiGIGPUScene scene, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];

        int kernel = m_VoxelInjectCS.FindKernel("VoxelInject");

        cmd.SetComputeConstantBufferParam(m_VoxelInjectCS, Shader.PropertyToID("_Params"), m_ObjectCullParamsCB[cascadeIndex], 0, Marshal.SizeOf<ObjectCullParams>());
        cmd.SetComputeIntParam(m_VoxelInjectCS, Shader.PropertyToID("_SurfaceCacheAtlasResolution"), 2048);
        cmd.SetComputeIntParam(m_VoxelInjectCS, Shader.PropertyToID("_CascadeIndex"), cascadeIndex);
        cmd.SetComputeVectorParam(m_VoxelInjectCS, Shader.PropertyToID("_CascadeMoveOffset"), (Vector3)cascadeInfo.moveOffset);

        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkList"), m_UpdateChunkList);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkCullingResult"), m_UpdateChunkCullingResults);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkObjectCounter"), m_UpdateChunkObjectCounter);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_ObjectsInfo"), scene.GPUSceneData.objectInfoBuffer);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_CardMatrixBuffer"), scene.surfaceCache.GetCardMatrixBuffer());

        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_SurfaceCacheAtlasDepth"), scene.surfaceCache.GetSurfaceCacheTexture(3));
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelBitOccupyClipmap"), m_VoxelMap);

        Vector3Int groupCount = new Vector3Int(Mathf.CeilToInt((float)m_UpdateChunkResolution.x / 4 * cascadeInfo.numChunksToUpdate),
                                                Mathf.CeilToInt((float)m_UpdateChunkResolution.y / 4),
                                                Mathf.CeilToInt((float)m_UpdateChunkResolution.z / 4));
        if(groupCount.x > 0)
        {
            cmd.DispatchCompute(m_VoxelInjectCS, kernel, groupCount.x, groupCount.y, groupCount.z);
        }
    }

    void VisualizeClipmap(CommandBuffer cmd, Camera camera)
    {
        Vector4[] cascadeCenterArray = new Vector4[MAX_CASCADE_COUNT];
        Vector4[] cascadeSizeArray = new Vector4[MAX_CASCADE_COUNT];
        Vector4[] cascadeResolutionArray = new Vector4[MAX_CASCADE_COUNT];
        Vector4[] cascadeMoveOffsetArray = new Vector4[MAX_CASCADE_COUNT];

        for (int cascadeId = 0; cascadeId < m_CascadeInfos.Length; cascadeId++)
        {
            MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeId];
            cascadeCenterArray[cascadeId] = cascadeInfo.cascadeCenter;
            cascadeSizeArray[cascadeId] = cascadeInfo.cascadeSize;
            cascadeResolutionArray[cascadeId] = (Vector3)m_VoxelResolution;
            cascadeMoveOffsetArray[cascadeId] = (Vector3)cascadeInfo.moveOffset;
        }

        int kernel = m_VisualizeClipmapCS.FindKernel("VisualizeClipmap");

        cmd.SetComputeVectorParam(m_VisualizeClipmapCS, Shader.PropertyToID("_ScreenResolution"), new Vector4(camera.pixelWidth, camera.pixelHeight));
        cmd.SetComputeVectorParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CameraPosition"), camera.transform.position);
        cmd.SetComputeMatrixParam(m_VisualizeClipmapCS, Shader.PropertyToID("_InvViewProjMat"), (camera.projectionMatrix * camera.worldToCameraMatrix).inverse);

        cmd.SetComputeVectorArrayParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeCenterArray"), cascadeCenterArray);
        cmd.SetComputeVectorArrayParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeSizeArray"), cascadeSizeArray);
        cmd.SetComputeVectorArrayParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeResolutionArray"), cascadeResolutionArray);
        cmd.SetComputeVectorArrayParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeMoveOffsetArray"), cascadeMoveOffsetArray);

        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_BitOccupyClipmap"), m_VoxelMap);
        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));
        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_RWSceneColorTexture"), m_VisualizeColorTarget);

        cmd.DispatchCompute(m_VisualizeClipmapCS, kernel, Mathf.CeilToInt((float)camera.pixelWidth / 8), Mathf.CeilToInt((float)camera.pixelHeight / 8), 1);
    }
}
