public class GlobalShared
{
    public static int OBJECT_ID_INVALID = -1;
    public static int MAX_CARDS_PER_MESH = 12;
    public static int VOXEL_BLOCK_SIZE = 4;
    public static int VOXEL_COUNT_PER_BLOCK = VOXEL_BLOCK_SIZE * VOXEL_BLOCK_SIZE * VOXEL_BLOCK_SIZE;
    public static int MAX_CASCADE_COUNT = 4;
    public static int PAGE_ID_INVALID = 0x3FFFFFFF;
}

public enum CardCaptureRTSlot
{
    BaseColor = 0,
    Normal,
    Emissive,
    Depth,
    Num
}