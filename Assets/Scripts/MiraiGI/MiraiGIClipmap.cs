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

public class UpdateChunk
{
    public int index1D = 0;
    public uint timeStamp = 0;
}

public class MiraiGICascadeInfo
{
    public Vector3 cascadeCenter;
    public Vector3 cascadeSize;
    public Vector3Int scrolling;
    public Vector3Int chunkCountInXYZ;
    public Vector3Int deltaChunk;
    public Vector3Int updateChunkResolution = new Vector3Int(16, 16, 16);

    Queue<UpdateChunk> pendingUpdateChunks = new Queue<UpdateChunk>();
    HashSet<int> updateChunksLookUp = new HashSet<int>();

    public List<int> chunksToUpdate = new List<int>();  // chunks to update in current frame, chunk may dirty in several frames ago
    public List<int> chunksToCleanup = new List<int>(); // chunks to cleanup, when a chunk dirty it will be clean at cur frame

    public bool HasChunksToUpdate()
    {
        return pendingUpdateChunks.Count > 0;
    }

    public bool PopUpdateChunk(out UpdateChunk outChunk)
    {
        if (pendingUpdateChunks.Count == 0)
        {
            outChunk = new UpdateChunk();
            return false;
        }

        outChunk = pendingUpdateChunks.Dequeue();
        updateChunksLookUp.Remove(outChunk.index1D);

        return true;
    }

    public void PushUpdateChunk(UpdateChunk chunk)
    {
        bool isAlreadyInSet = updateChunksLookUp.Add(chunk.index1D);
        if (!isAlreadyInSet)
        {
            return;
        }

        pendingUpdateChunks.Enqueue(chunk);
    }

    public void PopulateUpdateChunkList()
    {
        int maxUpdateChunkPerFrame = GlobalSettings.Instance.chunkCountToUpdatePerFrame;

        chunksToUpdate.Clear();

        // fetch from pending queue
        for (int i = 0; i < maxUpdateChunkPerFrame; i++)
        {
            UpdateChunk chunk = new UpdateChunk();
            if (!PopUpdateChunk(out chunk))
            {
                break;
            }

            chunksToUpdate.Add(chunk.index1D);
        }
    }

    public void PopulateUpdateChunkCleanupList(uint frameIndex)
    {
        chunksToCleanup.Clear();

        // if a chunk been marked as dirty in this frame, we cleanup it
        foreach (UpdateChunk dirtyChunk in pendingUpdateChunks)
	    {
            if (dirtyChunk.timeStamp == frameIndex)
            {
                chunksToCleanup.Add(dirtyChunk.index1D);
            }
        }
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
    public float rejectFactor;
    public float voxelCellSize;
}

// ---------------------------------------------------
// Clipmap data is per view info
// TODO: support muti-camera view
// ---------------------------------------------------
public class MiraiGIClipmap
{
    public MiraiGIRadianceCache radianceCache;

    public const int CASCADE_COUNT = 4;
    public const int MAX_CASCADE_COUNT = 4;
    public Vector3Int voxelResolution = new Vector3Int(128, 128, 128);
    public Vector3Int voxelPageCountInXYZ = new Vector3Int(32, 32, 32);

    public MiraiGICascadeInfo[] cascadeInfos;

    const int MAX_OBJECT_NUM_PER_CASCADE = 2048;
    const int MAX_UPDATE_CHUNK_PER_FRAME = 64;
    const int MAX_OBJECT_NUM_PER_UPDATE_CHUNK = 64;
    const int VOXEL_BLOCK_SIZE = 4;
    const int PAGE_ID_INVALID = (0x3FFFFFFF);
    const int VISUALIZE_MODE = 1;

    RenderTexture m_VoxelMap;
    RenderTexture m_VoxelPageClipmap;
    RenderTexture m_VisualizeColorTarget;
    RenderTexture m_VisualizeDepthTarget;

    // ping-pong swap texture
    RenderTexture[] m_DistanceFieldClipmap;

    // sparse store per-voxel material attribute, all clips share same physic texture
    // note: per-mesh material attribute is store in surface cache atlas, like BLAS
    // voxel pool will store per-instance material attribute, like TLAS
    RenderTexture m_VoxelPoolBaseColor;
    RenderTexture m_VoxelPoolNormal;
    RenderTexture m_VoxelPoolEmissive;

    ObjectCullParams[] m_ObjectCullParams;
    ComputeBuffer[] m_ObjectCullParamsCB;

    ComputeBuffer[] m_UpdateChunkList;
    ComputeBuffer[] m_UpdateChunkCleanupList;
    ComputeBuffer m_ClipmapObjectCounter;
    ComputeBuffer m_ClipmapCullingResult;
    ComputeBuffer m_UpdateChunkCullingIndirectArgs;
    // 2D array as 1D, single row represent an update chunk
    ComputeBuffer m_UpdateChunkCullingResults;
    ComputeBuffer m_UpdateChunkObjectCounter;

    // last element in list is pointer to next read write position
    ComputeBuffer m_VoxelPageFreeList;
    ComputeBuffer m_VoxelPageReleaseList;
    ComputeBuffer m_VoxelPageReleaseIndirectArgs;
    ComputeBuffer m_PageCountToReleaseCounter;

    RenderTexture m_VoxelOccupy;

    ComputeShader m_CullObjectCS;
    ComputeShader m_VoxelInjectCS;
    ComputeShader m_VisualizeClipmapCS;
    ComputeShader m_VoxelPageReleaseCS;
    ComputeShader m_VoxelPoolInitCS;

    public RenderTexture GetVoxelMap() => m_VoxelMap;
    public RenderTexture GetVoxelPageClipmap() => m_VoxelPageClipmap;
    public RenderTexture GetVoxelPoolBaseColor() => m_VoxelPoolBaseColor;
    public RenderTexture GetVoxelPoolNormal() => m_VoxelPoolNormal;
    public RenderTexture GetVoxelPoolEmissive() => m_VoxelPoolEmissive;
    public RenderTexture GetVisualizeColorTarget() => m_VisualizeColorTarget;
    public RenderTexture GetVisualizeDepthTarget() => m_VisualizeDepthTarget;
    public RenderTexture GetDistanceFieldClipmap() => m_DistanceFieldClipmap[(radianceCache.frameNumberRenderThread + 0) % 2];
    public RenderTexture GetDistanceFieldClipmapNextFrame() => m_DistanceFieldClipmap[(radianceCache.frameNumberRenderThread + 1) % 2];
    public ComputeBuffer GetUpdateChunkCleanupList(int cascadeId) => m_UpdateChunkCleanupList[cascadeId];

    public void CreateClipmap()
    {
        m_CullObjectCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/CullObject.compute");
        m_VoxelInjectCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelInject.compute");
        m_VisualizeClipmapCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VisualizeClipmap.compute");
        m_VoxelPageReleaseCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelPageRelease.compute");
        m_VoxelPoolInitCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelPoolInit.compute");

        Vector3Int clipmapResolution = new Vector3Int(voxelResolution.x / VOXEL_BLOCK_SIZE, 
                                                        voxelResolution.y / VOXEL_BLOCK_SIZE, 
                                                        voxelResolution.z * CASCADE_COUNT / VOXEL_BLOCK_SIZE);

        m_VoxelMap = new RenderTexture(clipmapResolution.x, clipmapResolution.y, 0, RenderTextureFormat.RGInt);
        m_VoxelMap.dimension = TextureDimension.Tex3D;
        m_VoxelMap.volumeDepth = clipmapResolution.z;
        m_VoxelMap.enableRandomWrite = true;
        m_VoxelMap.Create();

        m_VisualizeColorTarget = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 24, RenderTextureFormat.ARGBFloat);
        m_VisualizeColorTarget.depth = 24;
        m_VisualizeColorTarget.enableRandomWrite = true;
        m_VisualizeColorTarget.Create();
        m_VisualizeDepthTarget = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 24, RenderTextureFormat.Depth);
        m_VisualizeDepthTarget.Create();

        m_VoxelOccupy = new RenderTexture(128, 128, 0, RenderTextureFormat.RFloat);
        m_VoxelOccupy.dimension = TextureDimension.Tex3D;
        m_VoxelOccupy.volumeDepth = 128 * CASCADE_COUNT;
        m_VoxelOccupy.enableRandomWrite = true;
        m_VoxelOccupy.Create();

        cascadeInfos = new MiraiGICascadeInfo[CASCADE_COUNT];
        m_ObjectCullParams = new ObjectCullParams[CASCADE_COUNT];
        m_ObjectCullParamsCB = new ComputeBuffer[CASCADE_COUNT];

        m_UpdateChunkList = new ComputeBuffer[CASCADE_COUNT];
        m_UpdateChunkCleanupList = new ComputeBuffer[CASCADE_COUNT];

        for (int cascadeId = 0; cascadeId < CASCADE_COUNT; cascadeId++)
        {
            cascadeInfos[cascadeId] = new MiraiGICascadeInfo();
            MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeId];
            cascadeInfo.updateChunkResolution = voxelResolution / GlobalShared.UPDATE_CHUNK_NUM;

            Vector3Int updateChunkDimension = new Vector3Int(
                voxelResolution.x / cascadeInfo.updateChunkResolution.x,
                voxelResolution.y / cascadeInfo.updateChunkResolution.y,
                voxelResolution.z / cascadeInfo.updateChunkResolution.z
            );
            int updateChunkCount = updateChunkDimension.x * updateChunkDimension.y * updateChunkDimension.z;

            cascadeInfo.cascadeCenter = Camera.main.transform.position;
            cascadeInfo.cascadeSize = new Vector3(32, 32, 32) * (1 << cascadeId);
            cascadeInfo.scrolling = Vector3Int.zero;
            cascadeInfo.chunkCountInXYZ = updateChunkDimension; // TODO: no effect
            // mark all chunks dirty
            for (int chunkId = 0; chunkId < updateChunkCount; chunkId++)
            {
                UpdateChunk dirtyChunk = new UpdateChunk();
                dirtyChunk.index1D = chunkId;
                dirtyChunk.timeStamp = 0;
                cascadeInfo.PushUpdateChunk(dirtyChunk);
            }

            m_ObjectCullParams[cascadeId] = new ObjectCullParams();
            m_ObjectCullParamsCB[cascadeId] = new ComputeBuffer(1, Marshal.SizeOf<ObjectCullParams>());

            m_UpdateChunkList[cascadeId] = new ComputeBuffer(MAX_UPDATE_CHUNK_PER_FRAME, sizeof(int), ComputeBufferType.Raw);
            m_UpdateChunkCleanupList[cascadeId] = new ComputeBuffer(GlobalShared.UPDATE_CHUNK_NUM * GlobalShared.UPDATE_CHUNK_NUM * GlobalShared.UPDATE_CHUNK_NUM, sizeof(int), ComputeBufferType.Raw);
        }

        Vector3Int DFTextureResolution = clipmapResolution * VOXEL_BLOCK_SIZE;
        m_DistanceFieldClipmap = new RenderTexture[2];
        for (int i = 0; i < 2; i++)
        {
            m_DistanceFieldClipmap[i] = new RenderTexture(DFTextureResolution.x, DFTextureResolution.y, 0, RenderTextureFormat.R8);
            m_DistanceFieldClipmap[i].dimension = TextureDimension.Tex3D;
            m_DistanceFieldClipmap[i].volumeDepth = DFTextureResolution.z;
            m_DistanceFieldClipmap[i].enableRandomWrite = true;
            m_DistanceFieldClipmap[i].Create();
        }

        // page table
        m_VoxelPageClipmap = new RenderTexture(clipmapResolution.x, clipmapResolution.y, 0, RenderTextureFormat.RInt);
        m_VoxelPageClipmap.dimension = TextureDimension.Tex3D;
        m_VoxelPageClipmap.volumeDepth = clipmapResolution.z;
        m_VoxelPageClipmap.enableRandomWrite = true;
        m_VoxelPageClipmap.Create();

        // TODO: adaptive size, now for 128*128*128*4 clipmap we use 32x32x32 block pages (128x128x128 total)
        // TODO: check if need re-create, 
        Vector3Int textureSize = voxelPageCountInXYZ * VOXEL_BLOCK_SIZE;

        m_VoxelPoolBaseColor = new RenderTexture(textureSize.x, textureSize.y, 0, RenderTextureFormat.ARGB32);
        m_VoxelPoolBaseColor.dimension = TextureDimension.Tex3D;
        m_VoxelPoolBaseColor.volumeDepth = textureSize.z;
        m_VoxelPoolBaseColor.enableRandomWrite = true;
        m_VoxelPoolBaseColor.Create();

        m_VoxelPoolNormal = new RenderTexture(textureSize.x, textureSize.y, 0, RenderTextureFormat.ARGBHalf);
        m_VoxelPoolNormal.dimension = TextureDimension.Tex3D;
        m_VoxelPoolNormal.volumeDepth = textureSize.z;
        m_VoxelPoolNormal.enableRandomWrite = true;
        m_VoxelPoolNormal.Create();

        m_VoxelPoolEmissive = new RenderTexture(textureSize.x, textureSize.y, 0, RenderTextureFormat.ARGBHalf);
        m_VoxelPoolEmissive.dimension = TextureDimension.Tex3D;
        m_VoxelPoolEmissive.volumeDepth = textureSize.z;
        m_VoxelPoolEmissive.enableRandomWrite = true;
        m_VoxelPoolEmissive.Create();

        CommandBuffer cmd = CommandBufferPool.Get("Init Voxel Page");

        cmd.SetComputeTextureParam(m_VoxelPoolInitCS, 0, Shader.PropertyToID("_RWVoxelMap"), m_VoxelMap);
        cmd.SetComputeTextureParam(m_VoxelPoolInitCS, 0, Shader.PropertyToID("_RWVoxelPageClipmap"), m_VoxelPageClipmap);
        cmd.SetComputeTextureParam(m_VoxelPoolInitCS, 0, Shader.PropertyToID("_RWVoxelPoolBaseColor"), m_VoxelPoolBaseColor);
        cmd.SetComputeTextureParam(m_VoxelPoolInitCS, 0, Shader.PropertyToID("_RWVoxelPoolNormal"), m_VoxelPoolNormal);
        cmd.SetComputeTextureParam(m_VoxelPoolInitCS, 0, Shader.PropertyToID("_RWVoxelPoolEmissive"), m_VoxelPoolEmissive);
        cmd.DispatchCompute(m_VoxelPoolInitCS, 0, clipmapResolution.x / 4, clipmapResolution.y / 4, clipmapResolution.z / 4);

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        // free list
        int pageCount = voxelPageCountInXYZ.x * voxelPageCountInXYZ.y * voxelPageCountInXYZ.z;
        int elementCountFreeList = pageCount + 1;// we use last element as allocator pointer
        m_VoxelPageFreeList = new ComputeBuffer(elementCountFreeList, sizeof(int), ComputeBufferType.Structured);
        int[] freeList = new int[elementCountFreeList];
        for (int i = 0; i < pageCount; i++)
        {
            freeList[i] = i;
        }
        freeList[pageCount] = 0;
        m_VoxelPageFreeList.SetData(freeList);

        // release list
        m_VoxelPageReleaseList = new ComputeBuffer(elementCountFreeList, sizeof(int), ComputeBufferType.Structured);

        m_VoxelPageReleaseIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
    }

    public void UpdateClipmap(Camera camera, MiraiGIGPUScene gpuScene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Update Clipmap");

        PrepareRenderResources();

        for (int cascadeIndex = 0; cascadeIndex < CASCADE_COUNT; cascadeIndex++)
        {
            UpdateCascadePosition(camera, cascadeIndex);
            MarkDirtyChunksToUpdate(gpuScene, camera, cascadeIndex);
            UploadChunkIds(gpuScene, camera, cascadeIndex);
            PrepareConstantBuffer(gpuScene, cascadeIndex);
            CullObjectToClipmap(cmd, gpuScene, camera, cascadeIndex);
            CullObjectToUpdateChunk(cmd, gpuScene, camera, cascadeIndex);
            VoxelInject(cmd, gpuScene, cascadeIndex);
            DistanceFieldPropagate(cmd, gpuScene, cascadeIndex);
        }

        ReleaseVoxelPage(cmd);

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void Release()
    {
        m_VoxelMap?.Release();
        m_VoxelPageClipmap?.Release();
        m_VisualizeColorTarget?.Release();
        m_VisualizeDepthTarget?.Release();
        for (int i = 0; i < 2; i++)
        {
            m_DistanceFieldClipmap[i].Release();
        }
        m_VoxelPoolBaseColor?.Release();
        m_VoxelPoolNormal?.Release();
        m_VoxelPoolEmissive?.Release();
        m_VoxelMap = null;
        m_VoxelPageClipmap = null;
        m_VisualizeColorTarget = null;
        m_VisualizeDepthTarget = null;
        for (int i = 0; i < 2; i++)
        {
            m_DistanceFieldClipmap[i] = null;
        }
        m_VoxelPoolBaseColor = null;
        m_VoxelPoolNormal = null;
        m_VoxelPoolEmissive = null;

        for (int cascadeId = 0; cascadeId < CASCADE_COUNT; cascadeId++)
        {
            m_ObjectCullParamsCB[cascadeId]?.Release();
            m_ObjectCullParamsCB[cascadeId] = null;
            m_UpdateChunkList[cascadeId]?.Release();
            m_UpdateChunkList[cascadeId] = null;
            m_UpdateChunkCleanupList[cascadeId]?.Release();
            m_UpdateChunkCleanupList[cascadeId] = null;
        }
        m_ClipmapObjectCounter?.Release();
        m_ClipmapCullingResult?.Release();
        m_UpdateChunkCullingIndirectArgs?.Release();
        m_UpdateChunkCullingResults?.Release();
        m_UpdateChunkObjectCounter?.Release();
        m_VoxelPageFreeList?.Release();
        m_VoxelPageReleaseList?.Release();
        m_VoxelPageReleaseIndirectArgs?.Release();
        m_PageCountToReleaseCounter?.Release();
        m_UpdateChunkList = null;
        m_UpdateChunkCleanupList = null;
        m_ClipmapObjectCounter = null;
        m_ClipmapCullingResult = null;
        m_UpdateChunkCullingIndirectArgs = null;
        m_UpdateChunkCullingResults = null;
        m_UpdateChunkObjectCounter = null;
        m_VoxelPageFreeList = null;
        m_VoxelPageReleaseList = null;
        m_VoxelPageReleaseIndirectArgs = null;
        m_PageCountToReleaseCounter = null;

        m_VoxelOccupy?.Release();
        m_VoxelOccupy = null;
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

        int[] chunkCounter = new int[MAX_UPDATE_CHUNK_PER_FRAME];
        for (int i = 0; i < MAX_UPDATE_CHUNK_PER_FRAME; i++)
        {
            chunkCounter[i] = 0;
        }
        int[] clipmapCulling = new int[MAX_OBJECT_NUM_PER_CASCADE];
        for (int i = 0; i < MAX_OBJECT_NUM_PER_CASCADE; i++)
        {
            clipmapCulling[i] = 0;
        }
        int[] cullingResults = new int[MAX_UPDATE_CHUNK_PER_FRAME * MAX_OBJECT_NUM_PER_UPDATE_CHUNK];
        for (int i = 0; i < MAX_UPDATE_CHUNK_PER_FRAME * MAX_OBJECT_NUM_PER_UPDATE_CHUNK; i++)
        {
            cullingResults[i] = 0;
        }
        m_ClipmapObjectCounter.SetData(new int[1] { 0 });
        m_UpdateChunkObjectCounter.SetData(chunkCounter);
        m_ClipmapCullingResult.SetData(clipmapCulling);
        m_UpdateChunkCullingResults.SetData(cullingResults);
    }

    void UpdateCascadePosition(Camera camera, int cascadeIndex)
    {
        Vector3 cameraPosition = camera.transform.position;
        MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];

        Vector3 voxelSize = new Vector3(cascadeInfo.cascadeSize.x / voxelResolution.x,
                                        cascadeInfo.cascadeSize.y / voxelResolution.y,
                                        cascadeInfo.cascadeSize.z / voxelResolution.z);
        Vector3Int chunkResolution = cascadeInfo.updateChunkResolution;
        Vector3 chunkSize = new Vector3(voxelSize.x * chunkResolution.x,
                                        voxelSize.y * chunkResolution.y,
                                        voxelSize.z * chunkResolution.z);

        // 1. calc moved cascade center, min move step in a chunk
        Vector3Int cameraChunkId = new Vector3Int(Mathf.FloorToInt(cameraPosition.x / chunkSize.x),
                                                    Mathf.FloorToInt(cameraPosition.y / chunkSize.y),
                                                    Mathf.FloorToInt(cameraPosition.z / chunkSize.z));
        Vector3Int cascadeCenterChunkId = new Vector3Int(Mathf.FloorToInt(cascadeInfo.cascadeCenter.x / chunkSize.x),
                                                            Mathf.FloorToInt(cascadeInfo.cascadeCenter.y / chunkSize.y),
                                                            Mathf.FloorToInt(cascadeInfo.cascadeCenter.z / chunkSize.z));
        Vector3Int deltaChunk = cameraChunkId - cascadeCenterChunkId;

        // 2. calc scrolling address
        cascadeInfo.scrolling += new Vector3Int(deltaChunk.x * chunkResolution.x, deltaChunk.y * chunkResolution.y, deltaChunk.z * chunkResolution.z);
        cascadeInfo.scrolling.x = cascadeInfo.scrolling.x % voxelResolution.x;
        cascadeInfo.scrolling.y = cascadeInfo.scrolling.y % voxelResolution.y;
        cascadeInfo.scrolling.z = cascadeInfo.scrolling.z % voxelResolution.z;

        cascadeInfo.scrolling.x += (cascadeInfo.scrolling.x < 0) ? voxelResolution.x : 0;
        cascadeInfo.scrolling.y += (cascadeInfo.scrolling.y < 0) ? voxelResolution.y : 0;
        cascadeInfo.scrolling.z += (cascadeInfo.scrolling.z < 0) ? voxelResolution.z : 0;

        // 3. update cascade new center
        cascadeInfo.cascadeCenter = new Vector3(cameraChunkId.x * chunkSize.x,
                                                cameraChunkId.y * chunkSize.y,
                                                cameraChunkId.z * chunkSize.z);
        cascadeInfo.deltaChunk = deltaChunk;
    }

    void MarkDirtyChunksToUpdate(MiraiGIGPUScene scene, Camera camera, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];

        Vector3 voxelSize = new Vector3(cascadeInfo.cascadeSize.x / voxelResolution.x,
                                        cascadeInfo.cascadeSize.y / voxelResolution.y,
                                        cascadeInfo.cascadeSize.z / voxelResolution.z);
        Vector3 chunkSize = new Vector3(voxelSize.x * cascadeInfo.updateChunkResolution.x,
                                        voxelSize.y * cascadeInfo.updateChunkResolution.y,
                                        voxelSize.z * cascadeInfo.updateChunkResolution.z);
        Vector3Int chunkCountInXYZ = new Vector3Int(voxelResolution.x / cascadeInfo.updateChunkResolution.x,
                                                    voxelResolution.y / cascadeInfo.updateChunkResolution.y,
                                                    voxelResolution.z / cascadeInfo.updateChunkResolution.z);
        Vector3Int deltaChunk = cascadeInfo.deltaChunk;

        // 1. move pending dirty chunks that haven't been update
        List<UpdateChunk> dirtyChunks = new List<UpdateChunk>();
        while (cascadeInfo.HasChunksToUpdate())
        {
            UpdateChunk updateChunk = new UpdateChunk();
            cascadeInfo.PopUpdateChunk(out updateChunk);
            dirtyChunks.Add(updateChunk);
        }
        foreach (UpdateChunk dirtyChunk in dirtyChunks)
        {
            Vector3Int chunkIndex3D = Index1DTo3DLinear(dirtyChunk.index1D, chunkCountInXYZ);
            Vector3Int movedChunkIndex3D = chunkIndex3D - deltaChunk;

            movedChunkIndex3D.x = movedChunkIndex3D.x % chunkCountInXYZ.x;
            movedChunkIndex3D.y = movedChunkIndex3D.y % chunkCountInXYZ.y;
            movedChunkIndex3D.z = movedChunkIndex3D.z % chunkCountInXYZ.z;

            movedChunkIndex3D.x += (movedChunkIndex3D.x < 0) ? chunkCountInXYZ.x : 0;
            movedChunkIndex3D.y += (movedChunkIndex3D.y < 0) ? chunkCountInXYZ.y : 0;
            movedChunkIndex3D.z += (movedChunkIndex3D.z < 0) ? chunkCountInXYZ.z : 0;

            // we don't record time stamp here, cause this chunk is added before
            dirtyChunk.index1D = Index3DTo1DLinear(movedChunkIndex3D, chunkCountInXYZ);
            cascadeInfo.PushUpdateChunk(dirtyChunk);
        }

        // 2.mark XZ plane's new coming chunks as dirty if volume move along Y axis
        // for 8x8x8 block, we may mark [0~8, 0~1, 0~8] as dirty when volume move 2 block in Y axis
        MarkChunkPlaneAsDirty(this, scene, chunkCountInXYZ, deltaChunk, cascadeInfo, 0);
        MarkChunkPlaneAsDirty(this, scene, chunkCountInXYZ, deltaChunk, cascadeInfo, 1);
        MarkChunkPlaneAsDirty(this, scene, chunkCountInXYZ, deltaChunk, cascadeInfo, 2);

        // 3. TODO: mark chunks as dirty when primitive move
    }

    void UploadChunkIds(MiraiGIGPUScene scene, Camera camera, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];

        // 1. populate array
        cascadeInfo.PopulateUpdateChunkCleanupList((uint)scene.frameNumber);
        cascadeInfo.PopulateUpdateChunkList();

        List<int> chunksToUpdate = cascadeInfo.chunksToUpdate;
        List<int> chunksToCleanup = cascadeInfo.chunksToCleanup;

        // 2. upload update chunk list
        m_UpdateChunkList[cascadeIndex].SetData(chunksToUpdate);

        // 3. upload update chunk cleanup list
        m_UpdateChunkCleanupList[cascadeIndex].SetData(chunksToCleanup);
    }

    void PrepareConstantBuffer(MiraiGIGPUScene scene, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];
        int numObjects = scene.GPUSceneData.objects.Count;
        ObjectCullParams objectCullParams = m_ObjectCullParams[cascadeIndex];

        objectCullParams.cascadeCenter = cascadeInfo.cascadeCenter;
        objectCullParams.cascadeSize = cascadeInfo.cascadeSize;
        objectCullParams.cascadeResolution = (Vector3)voxelResolution;
        objectCullParams.updateChunkResolution = (Vector3)cascadeInfo.updateChunkResolution;
        objectCullParams.numObjects = numObjects;
        objectCullParams.numUpdateChunks = cascadeInfo.chunksToUpdate.Count;
        objectCullParams.numThreadsForCulling = 8;
        objectCullParams.maxObjectNumPerUpdateChunk = MAX_OBJECT_NUM_PER_UPDATE_CHUNK;
        objectCullParams.rejectFactor = cascadeIndex + 1.0f;
        objectCullParams.voxelCellSize = cascadeInfo.cascadeSize.x / voxelResolution.x;

        m_ObjectCullParamsCB[cascadeIndex].SetData(new ObjectCullParams[] { objectCullParams });
    }

    void CullObjectToClipmap(CommandBuffer cmd, MiraiGIGPUScene scene, Camera camera, int cascadeIndex)
    {
        MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];
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
        MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];
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
            // TODO: clear counter buffer

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
        MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];

        int kernel = m_VoxelInjectCS.FindKernel("VoxelInject");

        bool useDistanceField = GlobalSettings.Instance.useDistanceField > 0;
        if (useDistanceField)
        {
            m_VoxelInjectCS.EnableKeyword("USE_DISTANCE_FIELD");
        }
        else
        {
            m_VoxelInjectCS.DisableKeyword("USE_DISTANCE_FIELD");
        }

        cmd.SetComputeConstantBufferParam(m_VoxelInjectCS, Shader.PropertyToID("_Params"), m_ObjectCullParamsCB[cascadeIndex], 0, Marshal.SizeOf<ObjectCullParams>());
        cmd.SetComputeIntParam(m_VoxelInjectCS, Shader.PropertyToID("_SurfaceCacheAtlasResolution"), 2048);
        cmd.SetComputeIntParam(m_VoxelInjectCS, Shader.PropertyToID("_CascadeIndex"), cascadeIndex);
        cmd.SetComputeVectorParam(m_VoxelInjectCS, Shader.PropertyToID("_CascadeScrolling"), (Vector3)cascadeInfo.scrolling);
        cmd.SetComputeVectorParam(m_VoxelInjectCS, Shader.PropertyToID("_VoxelPageCountInXYZ"), (Vector3)voxelPageCountInXYZ);

        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkList"), m_UpdateChunkList[cascadeIndex]);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkCullingResult"), m_UpdateChunkCullingResults);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_UpdateChunkObjectCounter"), m_UpdateChunkObjectCounter);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_ObjectsInfo"), scene.GPUSceneData.objectInfoBuffer);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_SurfaceCacheInfoBuffer"), scene.surfaceCache.GetSurfaceCacheInfoBuffer());
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_CardInfoBuffer"), scene.surfaceCache.GetCardInfoBuffer());
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPageFreeList"), m_VoxelPageFreeList);
        cmd.SetComputeBufferParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPageReleaseList"), m_VoxelPageReleaseList);

        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_SurfaceCacheAtlasDepth"), scene.surfaceCache.GetSurfaceCacheTexture((int)CardCaptureRTSlot.Depth));
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelBitOccupyClipmap"), m_VoxelMap);
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPageClipmap"), m_VoxelPageClipmap);
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_SurfaceCacheAtlasBaseColor"), scene.surfaceCache.GetSurfaceCacheTexture((int)CardCaptureRTSlot.BaseColor));
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_SurfaceCacheAtlasNormal"), scene.surfaceCache.GetSurfaceCacheTexture((int)CardCaptureRTSlot.Normal));
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_SurfaceCacheAtlasEmissive"), scene.surfaceCache.GetSurfaceCacheTexture((int)CardCaptureRTSlot.Emissive));
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPoolBaseColor"), m_VoxelPoolBaseColor);
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPoolNormal"), m_VoxelPoolNormal);
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelPoolEmissive"), m_VoxelPoolEmissive);
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWDistanceFieldClipmap"), GetDistanceFieldClipmap());

        //cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWVoxelOccupy"), m_VoxelOccupy);

        Vector3Int groupCount = new Vector3Int(Mathf.CeilToInt((float)cascadeInfo.updateChunkResolution.x / 4 * cascadeInfo.chunksToUpdate.Count),
                                                Mathf.CeilToInt((float)cascadeInfo.updateChunkResolution.y / 4),
                                                Mathf.CeilToInt((float)cascadeInfo.updateChunkResolution.z / 4));
        if(groupCount.x > 0)
        {
            cmd.DispatchCompute(m_VoxelInjectCS, kernel, groupCount.x, groupCount.y, groupCount.z);
        }
    }

    void DistanceFieldPropagate(CommandBuffer cmd, MiraiGIGPUScene scene, int cascadeId)
    {
        if (GlobalSettings.Instance.useDistanceField <= 0)
        {
            return;
        }

        MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeId];

        int kernel = m_VoxelInjectCS.FindKernel("DistanceFieldPropagate");

        cmd.SetComputeIntParam(m_VoxelInjectCS, Shader.PropertyToID("_CascadeIndex"), cascadeId);
        cmd.SetComputeVectorParam(m_VoxelInjectCS, Shader.PropertyToID("_CascadeResolution"), (Vector3)voxelResolution);
        cmd.SetComputeVectorParam(m_VoxelInjectCS, Shader.PropertyToID("_CascadeScrolling"), (Vector3)cascadeInfo.scrolling);
        cmd.SetComputeVectorParam(m_VoxelInjectCS, Shader.PropertyToID("_CascadeSize"), cascadeInfo.cascadeSize);
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_DistanceFieldClipmap"), GetDistanceFieldClipmap());
        cmd.SetComputeTextureParam(m_VoxelInjectCS, kernel, Shader.PropertyToID("_RWDistanceFieldClipmap"), GetDistanceFieldClipmapNextFrame());

        cmd.DispatchCompute(m_VoxelInjectCS, kernel, voxelResolution.x / 4, voxelResolution.y / 4, voxelResolution.z / 4);
    }

    void ReleaseVoxelPage(CommandBuffer cmd)
    {
        m_PageCountToReleaseCounter?.Release();
        m_PageCountToReleaseCounter = new ComputeBuffer(1, sizeof(int));
        m_PageCountToReleaseCounter.SetData(new int[1] { 0 });

        // 1. build indirect dispatch args
        {
            int kernel = m_VoxelPageReleaseCS.FindKernel("BuildVoxelPageReleaseIndirectArgs");
            cmd.SetComputeIntParam(m_VoxelPageReleaseCS, Shader.PropertyToID("_ThreadCountForPageRelease"), 8);
            cmd.SetComputeVectorParam(m_VoxelPageReleaseCS, Shader.PropertyToID("_VoxelPageCountInXYZ"), (Vector3)voxelPageCountInXYZ);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWVoxelPageFreeList"), m_VoxelPageFreeList);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWVoxelPageReleaseList"), m_VoxelPageReleaseList);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWPageCountToReleaseCounter"), m_PageCountToReleaseCounter);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWIndirectArgs"), m_VoxelPageReleaseIndirectArgs);

            cmd.DispatchCompute(m_VoxelPageReleaseCS, kernel, 1, 1, 1);
        }

        // 2. do give empty voxel pages back to free list
        {
            int kernel = m_VoxelPageReleaseCS.FindKernel("VoxelPageRelease");
            cmd.SetComputeVectorParam(m_VoxelPageReleaseCS, Shader.PropertyToID("_VoxelPageCountInXYZ"), (Vector3)voxelPageCountInXYZ);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWVoxelPageFreeList"), m_VoxelPageFreeList);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_RWVoxelPageReleaseList"), m_VoxelPageReleaseList);
            cmd.SetComputeBufferParam(m_VoxelPageReleaseCS, kernel, Shader.PropertyToID("_PageCountToReleaseCounter"), m_PageCountToReleaseCounter);

            cmd.DispatchCompute(m_VoxelPageReleaseCS, kernel, m_VoxelPageReleaseIndirectArgs, 0);
        }
    }

    public void VisualizeMiraiGIScene(CommandBuffer cmd, MiraiGIGPUScene scene, Camera camera)
    {
        if (GlobalSettings.Instance.voxelVisualizeMode <= 0 || GlobalSettings.Instance.voxelVisualizeMode > 4)
        {
            return;
        }

        MiraiGIRadianceCache radianceCache = scene.miraiGIRadianceCache;

        int kernel = m_VisualizeClipmapCS.FindKernel("VisualizeClipmap");

        SetupVoxelRaytracingParameters(cmd, m_VisualizeClipmapCS, kernel, scene);
        radianceCache.SetupProbeVolumeParameters(cmd, m_VisualizeClipmapCS, kernel, scene);

        cmd.SetComputeVectorParam(m_VisualizeClipmapCS, Shader.PropertyToID("_ScreenResolution"), new Vector4(camera.pixelWidth, camera.pixelHeight));
        cmd.SetComputeVectorParam(m_VisualizeClipmapCS, Shader.PropertyToID("_CameraPosition"), camera.transform.position);
        cmd.SetComputeMatrixParam(m_VisualizeClipmapCS, Shader.PropertyToID("_InvViewProjMat"), (camera.projectionMatrix * camera.worldToCameraMatrix).inverse);

        float near = camera.nearClipPlane;
        float far = camera.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_VisualizeClipmapCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_VisualizeClipmapCS, Shader.PropertyToID("_InvProjMat"), camera.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_VisualizeClipmapCS, Shader.PropertyToID("_InvViewMat"), camera.cameraToWorldMatrix);

        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_RWSceneColorTexture"), m_VisualizeColorTarget);
        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));

        cmd.SetComputeIntParam(m_VisualizeClipmapCS, Shader.PropertyToID("_VisualizeMode"), GlobalSettings.Instance.voxelVisualizeMode);
        cmd.SetComputeIntParam(m_VisualizeClipmapCS, Shader.PropertyToID("_VisualizeCascadeLevel"), GlobalSettings.Instance.voxelVisualizeCascadeLevel);

        int visualizeUpdateChunk = GlobalSettings.Instance.voxelVisualizeUpdateChunk;
        int selectCascade = Mathf.Clamp(visualizeUpdateChunk - 1, 0, cascadeInfos.Length - 1);
        cmd.SetComputeIntParam(m_VisualizeClipmapCS, Shader.PropertyToID("_VisualizeUpdateChunk"), Mathf.Clamp(visualizeUpdateChunk, 0, cascadeInfos.Length));
        cmd.SetComputeIntParam(m_VisualizeClipmapCS, Shader.PropertyToID("_UpdateChunkCount"), cascadeInfos[selectCascade].chunksToUpdate.Count);
        cmd.SetComputeVectorParam(m_VisualizeClipmapCS, Shader.PropertyToID("_UpdateChunkResolution"), (Vector3) cascadeInfos[selectCascade].updateChunkResolution);
        cmd.SetComputeBufferParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_UpdateChunkList"), m_UpdateChunkList[selectCascade]);

        cmd.SetComputeTextureParam(m_VisualizeClipmapCS, kernel, Shader.PropertyToID("_VoxelOccupy"), m_VoxelOccupy);

        cmd.DispatchCompute(m_VisualizeClipmapCS, kernel, Mathf.CeilToInt((float)camera.pixelWidth / 8), Mathf.CeilToInt((float)camera.pixelHeight / 8), 1);
    }


    public void SetupVoxelRaytracingParameters(CommandBuffer cmd, ComputeShader computeShader, int kernel,
                                        MiraiGIGPUScene scene, int cascadeId = 0)
    {
        MiraiGIRadianceCache radianceCache = scene.miraiGIRadianceCache;

        Vector4[] cascadeCenterArray = new Vector4[MAX_CASCADE_COUNT];
        Vector4[] cascadeSizeArray = new Vector4[MAX_CASCADE_COUNT];
        Vector4[] cascadeScrollingArray = new Vector4[MAX_CASCADE_COUNT];

        for (int cascadeIndex = 0; cascadeIndex < cascadeInfos.Length; cascadeIndex++)
        {
            MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];
            cascadeCenterArray[cascadeIndex] = cascadeInfo.cascadeCenter;
            cascadeSizeArray[cascadeIndex] = cascadeInfo.cascadeSize;
            cascadeScrollingArray[cascadeIndex] = (Vector3)cascadeInfo.scrolling;
        }

        // volume
        cmd.SetComputeIntParam(computeShader, Shader.PropertyToID("_CascadeIndex"), cascadeId);
        cmd.SetComputeIntParam(computeShader, Shader.PropertyToID("_CascadeCount"), cascadeInfos.Length);

        cmd.SetComputeVectorParam(computeShader, Shader.PropertyToID("_CascadeResolution"), (Vector3)voxelResolution);
        cmd.SetComputeVectorArrayParam(computeShader, Shader.PropertyToID("_CascadeCenterArray"), cascadeCenterArray);
        cmd.SetComputeVectorArrayParam(computeShader, Shader.PropertyToID("_CascadeSizeArray"), cascadeSizeArray);
        cmd.SetComputeVectorArrayParam(computeShader, Shader.PropertyToID("_CascadeScrollingArray"), cascadeScrollingArray);

        // voxel
        cmd.SetComputeVectorParam(computeShader, Shader.PropertyToID("_VoxelPageCountInXYZ"), (Vector3)voxelPageCountInXYZ);
        cmd.SetComputeTextureParam(computeShader, kernel, Shader.PropertyToID("_VoxelBitOccupyClipmap"), m_VoxelMap);
        cmd.SetComputeTextureParam(computeShader, kernel, Shader.PropertyToID("_VoxelPageClipmap"), m_VoxelPageClipmap);
        cmd.SetComputeTextureParam(computeShader, kernel, Shader.PropertyToID("_VoxelPoolBaseColor"), m_VoxelPoolBaseColor);
        cmd.SetComputeTextureParam(computeShader, kernel, Shader.PropertyToID("_VoxelPoolNormal"), m_VoxelPoolNormal);
        cmd.SetComputeTextureParam(computeShader, kernel, Shader.PropertyToID("_VoxelPoolEmissive"), m_VoxelPoolEmissive);
        cmd.SetComputeTextureParam(computeShader, kernel, Shader.PropertyToID("_VoxelPoolRadiance"), radianceCache.GetVoxelPoolRadiance());
        cmd.SetComputeTextureParam(computeShader, kernel, Shader.PropertyToID("_DistanceFieldClipmap"), GetDistanceFieldClipmap());
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
    void MarkChunkPlaneAsDirty(MiraiGIClipmap miraiGIClipmap, MiraiGIGPUScene scene, Vector3Int chunkCountInXYZ, Vector3Int deltaChunk, MiraiGICascadeInfo cascadeInfo, int axis)
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

                    UpdateChunk dirtyChunk = new UpdateChunk();
                    dirtyChunk.index1D = Index3DTo1DLinear(chunkIndex3D, chunkCountInXYZ);
                    dirtyChunk.timeStamp = (uint)scene.frameNumber;
                    cascadeInfo.PushUpdateChunk(dirtyChunk);
                }
            }
        }
    }
}
