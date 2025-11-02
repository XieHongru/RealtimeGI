using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using static Unity.VisualScripting.Dependencies.Sqlite.SQLiteConnection;

public class MiraiGICascadeInfo
{
    public Vector3 cascadeCenter;
    public Vector3 cascadeSize;
    public Vector3Int moveOffset;
    public Vector3Int chunkCountInXYZ;
    public List<int> chunksToUpdate = new List<int>();
    public Vector3Int deltaChunk;

    Queue<int> pendingUpdateChunks = new Queue<int>();
    HashSet<int> updateChunksLookUp = new HashSet<int>();

    public bool HasChunksToUpdate()
    {
        return pendingUpdateChunks.Count > 0;
    }

    public int PopUpdateChunk()
    {
        if (pendingUpdateChunks.Count == 0)
        {
            return -1;
        }

        int popElement = pendingUpdateChunks.Dequeue();
        updateChunksLookUp.Remove(popElement);

        return popElement;
    }

    public void PushUpdateChunk(int chunkId)
    {
        bool isAlreadyInSet = updateChunksLookUp.Add(chunkId);
        if (!isAlreadyInSet)
        {
            return;
        }

        pendingUpdateChunks.Enqueue(chunkId);
    }
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
    Vector3Int m_VoxelPagePoolSize = new Vector3Int(32, 32, 32);
    const int CASCADE_COUNT = 4;
    const int MAX_OBJECT_NUM_PER_CASCADE = 2048;
    const int MAX_UPDATE_CHUNK_PER_FRAME = 16;
    const int MAX_OBJECT_NUM_PER_UPDATE_CHUNK = 64;
    const int MAX_CASCADE_COUNT = 4;
    const int VOXEL_BLOCK_SIZE = 4;
    const int PAGE_ID_INVALID = (0x3FFFFFFF);

    RenderTexture m_VoxelMap;
    RenderTexture m_VoxelPageClipmap;
    RenderTexture m_VoxelPagePool;
    RenderTexture m_VisualizeColorTarget;
    RenderTexture m_VisualizeDepthTarget;

    MiraiGICascadeInfo[] m_CascadeInfos;
    ObjectCullParams[] m_ObjectCullParams;
    ComputeBuffer[] m_ObjectCullParamsCB;

    ComputeBuffer[] m_UpdateChunkList;
    ComputeBuffer m_ClipmapObjectCounter;
    ComputeBuffer m_ClipmapCullingResult;
    ComputeBuffer m_UpdateChunkCullingIndirectArgs;
    // 2D array as 1D, single row represent an update chunk
    ComputeBuffer m_UpdateChunkCullingResults;
    ComputeBuffer m_UpdateChunkObjectCounter;

    ComputeBuffer m_VoxelPageAllocator;
    ComputeBuffer m_VoxelPageFreeList;
    ComputeBuffer m_VoxelPageReleaseList;
    ComputeBuffer m_VoxelPageReleaseIndirectArgs;

    RenderTexture m_VoxelOccupy;

    ComputeShader m_CullObjectCS;
    ComputeShader m_VoxelInjectCS;
    ComputeShader m_VisualizeClipmapCS;
    ComputeShader m_VoxelPageReleaseCS;
    ComputeShader m_VoxelPageClipmapInitCS;

    public void CreateClipmap()
    {
        m_CullObjectCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/CullObject.compute");
        m_VoxelInjectCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelInject.compute");
        m_VisualizeClipmapCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VisualizeClipmap.compute");
        m_VoxelPageReleaseCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelPageRelease.compute");
        m_VoxelPageClipmapInitCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelPageClipmapInit.compute");

        Vector3Int clipmapResolution = new Vector3Int(m_VoxelResolution.x / VOXEL_BLOCK_SIZE, 
                                                        m_VoxelResolution.y / VOXEL_BLOCK_SIZE, 
                                                        m_VoxelResolution.z * CASCADE_COUNT / VOXEL_BLOCK_SIZE);

        m_VoxelMap = new RenderTexture(clipmapResolution.x, clipmapResolution.y, 0, RenderTextureFormat.RGInt);
        m_VoxelMap.dimension = TextureDimension.Tex3D;
        m_VoxelMap.volumeDepth = clipmapResolution.z;
        m_VoxelMap.enableRandomWrite = true;
        m_VoxelMap.Create();

        m_VisualizeColorTarget = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 0, RenderTextureFormat.RGB111110Float);
        m_VisualizeColorTarget.enableRandomWrite = true;
        m_VisualizeColorTarget.Create();
        m_VisualizeDepthTarget = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 0, RenderTextureFormat.RFloat);
        m_VisualizeDepthTarget.Create();

        m_VoxelOccupy = new RenderTexture(128, 128, 0, RenderTextureFormat.RInt);
        m_VoxelOccupy.dimension = TextureDimension.Tex3D;
        m_VoxelOccupy.volumeDepth = 128 * CASCADE_COUNT;
        m_VoxelOccupy.enableRandomWrite = true;
        m_VoxelOccupy.Create();

        m_CascadeInfos = new MiraiGICascadeInfo[CASCADE_COUNT];
        m_ObjectCullParams = new ObjectCullParams[CASCADE_COUNT];
        m_ObjectCullParamsCB = new ComputeBuffer[CASCADE_COUNT];

        m_UpdateChunkList = new ComputeBuffer[CASCADE_COUNT];

        Vector3Int updateChunkDimension = new Vector3Int(
            m_VoxelResolution.x / m_UpdateChunkResolution.x,
            m_VoxelResolution.y / m_UpdateChunkResolution.y,
            m_VoxelResolution.z / m_UpdateChunkResolution.z
        );
        int updateChunkCount = updateChunkDimension.x * updateChunkDimension.y * updateChunkDimension.z;

        for (int cascadeId = 0; cascadeId < CASCADE_COUNT; cascadeId++)
        {
            m_CascadeInfos[cascadeId] = new MiraiGICascadeInfo();
            m_CascadeInfos[cascadeId].cascadeCenter = Camera.main.transform.position;
            m_CascadeInfos[cascadeId].cascadeSize = new Vector3(32, 32, 32) * (1 << cascadeId);
            m_CascadeInfos[cascadeId].moveOffset = Vector3Int.zero;
            m_CascadeInfos[cascadeId].chunkCountInXYZ = updateChunkDimension; // TODO: no effect
            for (int chunkId = 0; chunkId < updateChunkCount; chunkId++)
            {
                m_CascadeInfos[cascadeId].PushUpdateChunk(chunkId);
            }

            m_ObjectCullParams[cascadeId] = new ObjectCullParams();
            m_ObjectCullParamsCB[cascadeId] = new ComputeBuffer(1, Marshal.SizeOf<ObjectCullParams>());

            m_UpdateChunkList[cascadeId] = new ComputeBuffer(MAX_UPDATE_CHUNK_PER_FRAME, sizeof(int), ComputeBufferType.Raw);
        }

        // page table
        m_VoxelPageClipmap = new RenderTexture(clipmapResolution.x, clipmapResolution.y, 0, RenderTextureFormat.RInt);
        m_VoxelPageClipmap.dimension = TextureDimension.Tex3D;
        m_VoxelPageClipmap.volumeDepth = clipmapResolution.z;
        m_VoxelPageClipmap.enableRandomWrite = true;

        CommandBuffer cmd = CommandBufferPool.Get("Init Voxel Page Clipmap");

        cmd.SetComputeTextureParam(m_VoxelPageClipmapInitCS, 0, Shader.PropertyToID("_RWVoxelPageClipmap"), m_VoxelPageClipmap);
        cmd.DispatchCompute(m_VoxelPageClipmapInitCS, 0, clipmapResolution.x / 4, clipmapResolution.y / 4, clipmapResolution.z / 4);

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        // TODO: adaptive size, now for 128*128*128*4 clipmap we use 32x32x32 block pages (128x128x128 total)
        // TODO: check if need re-create, 
        Vector3Int numPagesInXYZ = m_VoxelPagePoolSize;
        m_VoxelPagePool = new RenderTexture(numPagesInXYZ.x * VOXEL_BLOCK_SIZE, numPagesInXYZ.y * VOXEL_BLOCK_SIZE, 0, RenderTextureFormat.RGInt);
        m_VoxelPagePool.dimension = TextureDimension.Tex3D;
        m_VoxelPagePool.volumeDepth = numPagesInXYZ.z * VOXEL_BLOCK_SIZE;
        m_VoxelPagePool.enableRandomWrite = true;

        // page allocator
        int numPages = numPagesInXYZ.x * numPagesInXYZ.y * numPagesInXYZ.z;
        m_VoxelPageAllocator = new ComputeBuffer(4, sizeof(int), ComputeBufferType.Structured);
        // TODO: fill 0

        // free list
        int numBytesFreeList = sizeof(uint) * numPages;
        m_VoxelPageFreeList = new ComputeBuffer(numPages, sizeof(int), ComputeBufferType.Structured);
        int[] freeList = new int[numPages];
        for (int i = 0; i < freeList.Length; i++)
        {
            freeList[i] = i;
        }
        m_VoxelPageFreeList.SetData(freeList);

        m_VoxelPageReleaseList = new ComputeBuffer(numPages, sizeof(int), ComputeBufferType.Structured);

        m_VoxelPageReleaseIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
    }

    public void UpdateClipmap(Camera camera, MiraiGIGPUScene gpuScene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Update Clipmap");

        PrepareRenderResources();

        for (int cascadeIndex = 0; cascadeIndex < CASCADE_COUNT; cascadeIndex++)
        {
            UpdateCascadePosition(camera, cascadeIndex);
            MarkDirtyChunksToUpdate(camera, cascadeIndex);
            UploadChunkIds(gpuScene, camera, cascadeIndex);
            PrepareConstantBuffer(gpuScene, cascadeIndex);
            CullObjectToClipmap(cmd, gpuScene, camera, cascadeIndex);
            CullObjectToUpdateChunk(cmd, gpuScene, camera, cascadeIndex);
            VoxelInject(cmd, gpuScene, cascadeIndex);
        }

        ReleaseVoxelPage(cmd);

        VisualizeClipmap(cmd, gpuScene, camera);

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void Release()
    {
        m_VoxelMap?.Release();
        m_VoxelPageClipmap?.Release();
        m_VoxelPagePool?.Release();
        m_VisualizeColorTarget?.Release();
        m_VisualizeDepthTarget?.Release();
        m_VoxelMap = null;
        m_VoxelPageClipmap = null;
        m_VoxelPagePool = null;
        m_VisualizeColorTarget = null;
        m_VisualizeDepthTarget = null;

        for (int cascadeId = 0; cascadeId < CASCADE_COUNT; cascadeId++)
        {
            m_ObjectCullParamsCB[cascadeId]?.Release();
            m_ObjectCullParamsCB[cascadeId] = null;
            m_UpdateChunkList[cascadeId]?.Release();
            m_UpdateChunkList[cascadeId] = null;
        }
        m_ClipmapObjectCounter?.Release();
        m_ClipmapCullingResult?.Release();
        m_UpdateChunkCullingIndirectArgs?.Release();
        m_UpdateChunkCullingResults?.Release();
        m_UpdateChunkObjectCounter?.Release();
        m_VoxelPageAllocator?.Release();
        m_VoxelPageFreeList?.Release();
        m_VoxelPageReleaseList?.Release();
        m_VoxelPageReleaseIndirectArgs?.Release();
        m_UpdateChunkList = null;
        m_ClipmapObjectCounter = null;
        m_ClipmapCullingResult = null;
        m_UpdateChunkCullingIndirectArgs = null;
        m_UpdateChunkCullingResults = null;
        m_UpdateChunkObjectCounter = null;
        m_VoxelPageAllocator = null;
        m_VoxelPageFreeList = null;
        m_VoxelPageReleaseList = null;
        m_VoxelPageReleaseIndirectArgs = null;
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
        Vector3 chunkSize = new Vector3(voxelSize.x * m_UpdateChunkResolution.x,
                                        voxelSize.y * m_UpdateChunkResolution.y,
                                        voxelSize.z * m_UpdateChunkResolution.z);
        Vector3Int chunkResolution = new Vector3Int(m_VoxelResolution.x / m_UpdateChunkResolution.x,
                                                    m_VoxelResolution.y / m_UpdateChunkResolution.y,
                                                    m_VoxelResolution.z / m_UpdateChunkResolution.z);

        // 1. calc moved cascade center, min move step in a chunk
        Vector3Int cameraChunkId = new Vector3Int(Mathf.FloorToInt(cameraPosition.x / chunkSize.x),
                                                    Mathf.FloorToInt(cameraPosition.y / chunkSize.y),
                                                    Mathf.FloorToInt(cameraPosition.z / chunkSize.z));
        Vector3Int cascadeCenterChunkId = new Vector3Int(Mathf.FloorToInt(cascadeInfo.cascadeCenter.x / chunkSize.x),
                                                            Mathf.FloorToInt(cascadeInfo.cascadeCenter.y / chunkSize.y),
                                                            Mathf.FloorToInt(cascadeInfo.cascadeCenter.z / chunkSize.z));
        Vector3Int deltaChunk = cameraChunkId - cascadeCenterChunkId;

        // 2. calc rolling address
        // RollingInfo is for block (4x4x4) rolling address, so we map DeltaChunk to DeltaBlock
        Vector3Int blockResolution = m_VoxelResolution / VOXEL_BLOCK_SIZE;
        Vector3Int deltaVoxelBlock = new Vector3Int(deltaChunk.x * m_UpdateChunkResolution.x, 
                                                    deltaChunk.y * m_UpdateChunkResolution.y, 
                                                    deltaChunk.z * m_UpdateChunkResolution.z) / VOXEL_BLOCK_SIZE;
        cascadeInfo.moveOffset += deltaVoxelBlock;
        cascadeInfo.moveOffset.x = cascadeInfo.moveOffset.x % blockResolution.x;
        cascadeInfo.moveOffset.y = cascadeInfo.moveOffset.y % blockResolution.y;
        cascadeInfo.moveOffset.z = cascadeInfo.moveOffset.z % blockResolution.z;

        cascadeInfo.moveOffset.x += (cascadeInfo.moveOffset.x < 0) ? blockResolution.x : 0;
        cascadeInfo.moveOffset.y += (cascadeInfo.moveOffset.y < 0) ? blockResolution.y : 0;
        cascadeInfo.moveOffset.z += (cascadeInfo.moveOffset.z < 0) ? blockResolution.z : 0;

        // 3. update cascade new center
        cascadeInfo.cascadeCenter = new Vector3(cameraChunkId.x * chunkSize.x,
                                                cameraChunkId.y * chunkSize.y,
                                                cameraChunkId.z * chunkSize.z);
        cascadeInfo.deltaChunk = deltaChunk;
    }

    void MarkDirtyChunksToUpdate(Camera camera, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];

        Vector3 voxelSize = new Vector3(cascadeInfo.cascadeSize.x / m_VoxelResolution.x,
                                        cascadeInfo.cascadeSize.y / m_VoxelResolution.y,
                                        cascadeInfo.cascadeSize.z / m_VoxelResolution.z);
        Vector3 chunkSize = new Vector3(voxelSize.x * m_UpdateChunkResolution.x,
                                        voxelSize.y * m_UpdateChunkResolution.y,
                                        voxelSize.z * m_UpdateChunkResolution.z);
        Vector3Int chunkCountInXYZ = new Vector3Int(m_VoxelResolution.x / m_UpdateChunkResolution.x,
                                                    m_VoxelResolution.y / m_UpdateChunkResolution.y,
                                                    m_VoxelResolution.z / m_UpdateChunkResolution.z);
        Vector3Int deltaChunk = cascadeInfo.deltaChunk;

        // 1. move pending dirty chunks that haven't been update
        List<int> dirtyChunks = new List<int>();
        while (cascadeInfo.HasChunksToUpdate())
        {
            int chunkIndex1D = cascadeInfo.PopUpdateChunk();
            dirtyChunks.Add(chunkIndex1D);
        }
        foreach (int chunkIndex1d in dirtyChunks)
        {
            Vector3Int chunkIndex3D = Index1DTo3DLinear(chunkIndex1d, chunkCountInXYZ);
            Vector3Int movedChunkIndex3D = chunkIndex3D - deltaChunk;

            if (movedChunkIndex3D.x < 0 || movedChunkIndex3D.y < 0 || movedChunkIndex3D.z < 0 ||
                movedChunkIndex3D.x > chunkCountInXYZ.x || movedChunkIndex3D.y > chunkCountInXYZ.y || movedChunkIndex3D.z > chunkCountInXYZ.z)
            {
                continue;
            }

            int movedChunkIndex1D = Index3DTo1DLinear(chunkIndex3D, chunkCountInXYZ);
            cascadeInfo.PushUpdateChunk(movedChunkIndex1D);
        }

        // 2.mark XZ plane's new coming chunks as dirty if volume move along Y axis
        // for 8x8x8 block, we may mark [0~8, 0~1, 0~8] as dirty when volume move 2 block in Y axis
        MarkChunkPlaneAsDirty(chunkCountInXYZ, deltaChunk, cascadeInfo, 0);
        MarkChunkPlaneAsDirty(chunkCountInXYZ, deltaChunk, cascadeInfo, 1);
        MarkChunkPlaneAsDirty(chunkCountInXYZ, deltaChunk, cascadeInfo, 2);

        // 3. TODO: mark chunks as dirty when primitive move
    }

    void UploadChunkIds(MiraiGIGPUScene scene, Camera camera, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = m_CascadeInfos[cascadeIndex];
        List<int> chunksToUpdate = cascadeInfo.chunksToUpdate;
        chunksToUpdate.Clear();

        // fetch from pending queue
        for (int i = 0; i < MAX_UPDATE_CHUNK_PER_FRAME; i++)
        {
            int chunkId = cascadeInfo.PopUpdateChunk();
            if (chunkId == -1)
                break;

            chunksToUpdate.Add(chunkId);
        }

        // for debug, give back to queue
        //{
        //    for (int i = 0; i < cascadeInfo.numChunksToUpdate; i++)
        //    {
        //        int chunkId = chunksToUpdate[i];
        //        cascadeInfo.pendingUpdateChunks.Enqueue(chunkId);
        //    }
        //}

        m_UpdateChunkList[cascadeIndex].SetData(chunksToUpdate);

        if (chunksToUpdate.Count > 0)
        {
            //Debug.Log("update chunks: " + string.Join(", ", chunksToUpdate));
        }
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
        objectCullParams.numUpdateChunks = cascadeInfo.chunksToUpdate.Count;
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
            cmd.SetComputeBufferParam(m_CullObjectCS, kernel, Shader.PropertyToID("_UpdateChunkList"), m_UpdateChunkList[cascadeIndex]);
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
        cmd.SetComputeVectorParam(m_VoxelInjectCS, Shader.PropertyToID("_VoxelPagePoolSize"), (Vector3)m_VoxelPagePoolSize);

        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkList"), m_UpdateChunkList[cascadeIndex]);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkCullingResult"), m_UpdateChunkCullingResults);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkObjectCounter"), m_UpdateChunkObjectCounter);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_ObjectsInfo"), scene.GPUSceneData.objectInfoBuffer);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_CardMatrixBuffer"), scene.surfaceCache.GetCardMatrixBuffer());
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_CardUVTransformBuffer"), scene.surfaceCache.GetCardUVTransformBuffer());
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPageAllocator"), m_VoxelPageAllocator);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPageFreeList"), m_VoxelPageFreeList);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPageReleaseList"), m_VoxelPageReleaseList);

        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_SurfaceCacheAtlasDepth"), scene.surfaceCache.GetSurfaceCacheTexture(3));
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelBitOccupyClipmap"), m_VoxelMap);
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPageClipmap"), m_VoxelPageClipmap);
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPagePool"), m_VoxelPagePool);

        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelOccupy"), m_VoxelOccupy);

        Vector3Int groupCount = new Vector3Int(Mathf.CeilToInt((float)m_UpdateChunkResolution.x / 4 * cascadeInfo.chunksToUpdate.Count),
                                                Mathf.CeilToInt((float)m_UpdateChunkResolution.y / 4),
                                                Mathf.CeilToInt((float)m_UpdateChunkResolution.z / 4));
        if(groupCount.x > 0)
        {
            cmd.DispatchCompute(m_VoxelInjectCS, kernel, groupCount.x, groupCount.y, groupCount.z);
        }
    }

    void ReleaseVoxelPage(CommandBuffer cmd)
    {
        // 1. build indirect dispatch args
        {
            int kernel = m_VoxelPageReleaseCS.FindKernel("BuildVoxelPageReleaseIndirectArgs");
            cmd.SetComputeIntParam(m_VoxelPageReleaseCS, Shader.PropertyToID("_NumThreadsForPageRelease"), 1);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWVoxelPageAllocator"), m_VoxelPageAllocator);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWIndirectArgs"), m_VoxelPageReleaseIndirectArgs);

            cmd.DispatchCompute(m_VoxelPageReleaseCS, kernel, 1, 1, 1);
        }

        // 2. do give empty voxel pages back to free list
        {
            int kernel = m_VoxelPageReleaseCS.FindKernel("VoxelPageRelease");
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_VoxelPageReleaseList"), m_VoxelPageReleaseList);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWVoxelPageAllocator"), m_VoxelPageAllocator);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWVoxelPageFreeList"), m_VoxelPageFreeList);

            cmd.DispatchCompute(m_VoxelPageReleaseCS, kernel, m_VoxelPageReleaseIndirectArgs, 0);
        }
    }

    void VisualizeClipmap(CommandBuffer cmd, MiraiGIGPUScene scene, Camera camera)
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

        cmd.SetComputeIntParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeCount"), CASCADE_COUNT);
        cmd.SetComputeVectorParam(m_VisualizeClipmapCS, Shader.PropertyToID("_VoxelPagePoolSize"), (Vector3)m_VoxelPagePoolSize);
        cmd.SetComputeVectorArrayParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeCenterArray"), cascadeCenterArray);
        cmd.SetComputeVectorArrayParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeSizeArray"), cascadeSizeArray);
        cmd.SetComputeVectorArrayParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeResolutionArray"), cascadeResolutionArray);
        cmd.SetComputeVectorArrayParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CascadeMoveOffsetArray"), cascadeMoveOffsetArray);

        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_BitOccupyClipmap"), m_VoxelMap);
        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_VoxelPageClipmap"), m_VoxelPageClipmap);
        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_VoxelPagePool"), m_VoxelPagePool);
        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_SurfaceCacheAtlasToVisualize"), scene.surfaceCache.GetSurfaceCacheTexture(0));
        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));
        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_RWSceneColorTexture"), m_VisualizeColorTarget);

        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_VoxelOccupy"), m_VoxelOccupy);

        cmd.DispatchCompute(m_VisualizeClipmapCS, kernel, Mathf.CeilToInt((float)camera.pixelWidth / 8), Mathf.CeilToInt((float)camera.pixelHeight / 8), 1);
    }

    int Index3DTo1DLinear(Vector3Int index3D, Vector3Int size3D)
    {
        int res = 0;
        res += index3D.x * 1;
	    res += index3D.y * size3D.x;
        res += index3D.z * (size3D.x * size3D.y);
	    return res;
    }

    Vector3Int Index1DTo3DLinear(int index1D, Vector3Int size3D)
    {
        Vector3Int res = Vector3Int.zero;

        res.z = index1D / (size3D.x * size3D.y);
        index1D -= res.z * (size3D.x * size3D.y);

        res.y = index1D / size3D.x;
        index1D -= res.y * size3D.x;

        res.x = index1D;

        return res;
    }

    // mark XZ plane's new coming chunks as dirty if volume move along Y axis
    // for 8x8x8 block, we may mark [0~8, 0~1, 0~8] as dirty when volume move 2 block in Y axis
    void MarkChunkPlaneAsDirty(Vector3Int chunkCountInXYZ, Vector3Int deltaChunk, MiraiGICascadeInfo cascadeInfo, int axis)
    {
        int deltaChunkInAxis = deltaChunk[axis];
        int chunkCountInX = chunkCountInXYZ[(axis + 0) % 3];
        int chunkCountInY = chunkCountInXYZ[(axis + 1) % 3];
        int chunkCountInZ = chunkCountInXYZ[(axis + 2) % 3];

        if (deltaChunkInAxis == 0)
        {
            return;
        }

        int start = 0, end = 0;
        if (deltaChunkInAxis > 0)   // [112 ~ 128]
        {
            start = chunkCountInX - Math.Abs(deltaChunkInAxis);
            end = chunkCountInX - 1;
        }
        else    // [0 ~ 16]
        {
            start = 0;
            end = Math.Abs(deltaChunkInAxis) - 1;
        }

        start = Mathf.Max(start, 0);
        end = Mathf.Min(end, chunkCountInX - 1);

        for (int X = start; X <= end; X++)
        {
            for (int Y = 0; Y < chunkCountInY; Y++)
            {
                for (int Z = 0; Z < chunkCountInZ; Z++)
                {
                    Vector3Int chunkIndex3D = Vector3Int.zero;
                    chunkIndex3D[(axis + 0) % 3] = X;
                    chunkIndex3D[(axis + 1) % 3] = Y;
                    chunkIndex3D[(axis + 2) % 3] = Z;

                    cascadeInfo.PushUpdateChunk(Index3DTo1DLinear(chunkIndex3D, chunkCountInXYZ));
                }
            }
        }
    }
}
