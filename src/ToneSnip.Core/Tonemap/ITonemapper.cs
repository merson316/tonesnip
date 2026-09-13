namespace ToneSnip.Core.Tonemap;

public interface ITonemapper
{
    /// <summary>
    /// Maps one pixel in place. Input: linear scRGB where 1.0 = 80 nits (may exceed 1, may be negative).
    /// Output: linear display-referred RGB in [0,1] where 1.0 = SDR white. Callers apply sRGB encoding.
    /// </summary>
    void Map(ref float r, ref float g, ref float b);
}
