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
    public Vector3 moveOffset;

    public int numChunksToUpdate;
};

public struct ObjectCullParams
{
    public Vector3 cascadeCenter;
    public Vector3 cascadeSize;
    public Vector3 cascadeResolution;
    public Vector3 updateChunkResolution;
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

    RenderTexture m_VoxelMap;

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

    public void CreateClipmap()
    {
        m_VoxelMap = new RenderTexture(m_VoxelResolution.x, m_VoxelResolution.y, 0, RenderTextureFormat.RGInt);
        m_VoxelMap.dimension = TextureDimension.Tex3D;
        m_VoxelMap.volumeDepth = m_VoxelResolution.z * CASCADE_COUNT;
        m_VoxelMap.enableRandomWrite = true;
        m_VoxelMap.Create();

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

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void Release()
    {
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
        m_UpdateChunkCullingResults = new ComputeBuffer(MAX_UPDATE_CHUNK_PER_FRAME * MAX_OBJECT_NUM_PER_CASCADE, sizeof(int), ComputeBufferType.Structured);
        m_UpdateChunkObjectCounter = new ComputeBuffer(MAX_UPDATE_CHUNK_PER_FRAME, sizeof(int), ComputeBufferType.Structured);
    }

    void UpdateCascadePosition(Camera camera, int cascadeIndex)
    {
        Vector3 cameraPosition = camera.transform.position;
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];

        Vector3 voxelSize = new Vector3(cascadeInfo.cascadeSize.x / m_VoxelResolution.x,
                                        cascadeInfo.cascadeSize.y / m_VoxelResolution.y,
                                        cascadeInfo.cascadeSize.z / m_VoxelResolution.z);
        Vector3 cameraGridId = new Vector3(Mathf.CeilToInt(cameraPosition.x / voxelSize.x),
                                           Mathf.CeilToInt(cameraPosition.y / voxelSize.y),
                                           Mathf.CeilToInt(cameraPosition.z / voxelSize.z));
        Vector3 cascadeCenterGridId = new Vector3(Mathf.CeilToInt(cascadeInfo.cascadeCenter.x / voxelSize.x),
                                                  Mathf.CeilToInt(cascadeInfo.cascadeCenter.y / voxelSize.y),
                                                  Mathf.CeilToInt(cascadeInfo.cascadeCenter.z / voxelSize.z));

        // calculate camera move offset based on voxel grid
        // then update cascade center position
        cascadeInfo.moveOffset = cameraGridId - cascadeCenterGridId;
        cascadeInfo.cascadeCenter = new Vector3(cameraGridId.x * voxelSize.x,
                                                cameraGridId.y * voxelSize.y,
                                                cameraGridId.z * voxelSize.z);
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

        objectCullParams.numObjects = numObjects;
        objectCullParams.cascadeCenter = cascadeInfo.cascadeCenter;
        objectCullParams.cascadeSize = cascadeInfo.cascadeSize;
        objectCullParams.numUpdateChunks = cascadeInfo.numChunksToUpdate;
        objectCullParams.numThreadsForCulling = 8;
        objectCullParams.maxObjectNumPerUpdateChunk = MAX_OBJECT_NUM_PER_UPDATE_CHUNK;
        objectCullParams.cascadeResolution = m_VoxelResolution;
        objectCullParams.updateChunkResolution = m_UpdateChunkResolution;

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
            Debug.Log("Execute");
        }
    }
}
