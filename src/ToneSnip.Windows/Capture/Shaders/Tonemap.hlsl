// GPU twins of ToneSnip.Core's CPU tonemappers and HDR readouts, over a captured R16G16B16A16_FLOAT (scRGB, 1.0 = 80
// nits) texture. Kept bit-for-bit close to DesktopTonemapper, HableTonemapper, AcesLut, SrgbTable and ZebraMask.Exceeds:
// any change there must be mirrored here, and the bytecode rebuilt (see ShaderBytecodeTests). `tonesnip-debug --selftest`
// and tools/gpu-tonemap-bench measure the gap on real hardware.
//
// CSTonemap: one thread per pixel, writes packed BGRA8 (B | G<<8 | R<<16 | A<<24) into an R32_UINT texture, so the
//            readback is a row copy into a BgraImage. A texture rather than a raw buffer: an 8K frame's output is over
//            the 128 MB a buffer is guaranteed to be allowed.
// CSStats:   luminance peak and sum over a rectangle, one partial per 64x64 tile, summed on the CPU.
// CSZebra:   one bit per pixel, set where luminance x exposure is above SDR white; one thread per 32-pixel word.
//
// Compiled with D3DCOMPILE_IEEE_STRICTNESS: the NaN/inf handling below must survive the optimiser.

Texture2D<float4> Src : register(t0);
RWTexture2D<uint> Dst : register(u0);
RWStructuredBuffer<float2> Partials : register(u1);
RWByteAddressBuffer Mask : register(u2);

cbuffer Params : register(b0)
{
    uint2 Size;          // output (region) size in pixels
    uint2 Origin;        // region's top-left in the source texture
    uint Mode;           // 0 desktop, 1 hable, 2 aces, 3 passthrough (clip + encode)
    uint Clip;           // desktop: knee >= 1
    float Scale;         // 80 / sdrWhite * exposure
    float SdrWhite;      // nits
    float PqPeak;        // PQ(peak nits)
    float Ks;            // knee start, normalised PQ
    float MaxLum;        // PQ(sdr white) / PQ(peak)
    float InvWhite;      // hable 1 / Curve(white)
    float HableRaw0;     // hable Raw(0)
    uint TilesX;         // CSStats: tiles per row; CSZebra: mask words per row
    float ZebraWhite;    // CSZebra: SDR white in scRGB
    float ZebraExposure; // CSZebra: exposure multiplier
};

static const float PqM1 = 0.1593017578125;
static const float PqM2 = 78.84375;
static const float PqC1 = 0.8359375;
static const float PqC2 = 18.8515625;
static const float PqC3 = 18.6875;

float PqEncode(float nits)
{
    float y = clamp(nits / 10000.0, 0.0, 1.0);
    float ym1 = pow(y, PqM1);
    return pow((PqC1 + PqC2 * ym1) / (1.0 + PqC3 * ym1), PqM2);
}

float PqDecode(float pq)
{
    float e = clamp(pq, 0.0, 1.0);
    float ep = pow(e, 1.0 / PqM2);
    float num = max(ep - PqC1, 0.0);
    float den = PqC2 - PqC3 * ep;
    if (den <= 0.0) return 10000.0;
    return pow(num / den, 1.0 / PqM1) * 10000.0;
}

float Lum709(float3 c) { return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b; }

float SrgbEncode(float l)
{
    if (l <= 0.0) return 0.0;
    if (l >= 1.0) return 1.0;
    return l <= 0.0031308 ? l * 12.92 : 1.055 * pow(l, 1.0 / 2.4) - 0.055;
}

// SrgbTable.Encode: a 4096-step table of rounded encodes. Reproduced by quantising first.
uint EncodeTable(float l)
{
    if (!(l > 0.0)) return 0;
    if (l >= 1.0) return 255;
    float q = floor(l * 4096.0 + 0.5) / 4096.0;
    return (uint)clamp(round(SrgbEncode(q) * 255.0), 0.0, 255.0);
}

// AcesLut: an exact encode of each half value.
uint EncodeExact(float l) { return (uint)clamp(round(SrgbEncode(l) * 255.0), 0.0, 255.0); }

float Finite(float v) { return isnan(v) ? 0.0 : clamp(v, -65504.0, 65504.0); }

float Compress(float y)
{
    float e1 = PqEncode(y * SdrWhite) / PqPeak;
    if (e1 <= Ks) return y;
    float t = min((e1 - Ks) / (1.0 - Ks), 1.0);
    float t2 = t * t, t3 = t2 * t;
    float e2 = (2.0 * t3 - 3.0 * t2 + 1.0) * Ks
             + (t3 - 2.0 * t2 + t) * (1.0 - Ks)
             + (-2.0 * t3 + 3.0 * t2) * MaxLum;
    return min(PqDecode(e2 * PqPeak) / SdrWhite, 1.0);
}

float3 Desktop(float3 c)
{
    c = float3(Finite(c.r), Finite(c.g), Finite(c.b));
    c = max(c * Scale, 0.0);
    float y = Lum709(c);
    if (y > 0.0)
    {
        float yOut = Clip ? min(y, 1.0) : Compress(y);
        if (yOut < y) c *= yOut / y;
    }
    return min(c, 1.0);
}

float HableRaw(float x)
{
    const float A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
    return (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F);
}

float Hable1(float v)
{
    // CPU: Math.Clamp(Curve(Max(v*scale, 0)) * invWhite, 0, 1); a NaN from inf/inf stays NaN there and encodes as 0.
    float r = (HableRaw(max(v * Scale, 0.0)) - HableRaw0) * InvWhite;
    return isnan(r) ? 0.0 : clamp(r, 0.0, 1.0);
}

float Aces1(float v)
{
    // AcesLut: NaN -> 0, +inf -> 65504, -inf -> 0 before the curve.
    if (isnan(v)) v = 0.0; else if (isinf(v)) v = v > 0.0 ? 65504.0 : 0.0;
    float x = max(v * Scale, 0.0);
    return clamp(x * (2.51 * x + 0.03) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

uint Pack(uint r, uint g, uint b) { return b | (g << 8) | (r << 16) | (255u << 24); }

[numthreads(8, 8, 1)]
void CSTonemap(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Size.x || id.y >= Size.y) return;
    float3 c = Src.Load(int3(Origin + id.xy, 0)).rgb;
    uint packed;
    if (Mode == 0)
    {
        float3 m = Desktop(c);
        packed = Pack(EncodeTable(m.r), EncodeTable(m.g), EncodeTable(m.b));
    }
    else if (Mode == 1)
        packed = Pack(EncodeTable(Hable1(c.r)), EncodeTable(Hable1(c.g)), EncodeTable(Hable1(c.b)));
    else if (Mode == 2)
        packed = Pack(EncodeExact(Aces1(c.r)), EncodeExact(Aces1(c.g)), EncodeExact(Aces1(c.b)));
    else
        packed = Pack(EncodeTable(c.r), EncodeTable(c.g), EncodeTable(c.b));
    Dst[id.xy] = packed;
}

// ---------------------------------------------------------------------------------------------------- statistics

groupshared float2 Shared[256];

// 16x16 threads, each over 4x4 pixels: one 64x64 tile per group. Partial = (peak nits, sum of nits).
[numthreads(16, 16, 1)]
void CSStats(uint3 gid : SV_GroupID, uint3 tid : SV_GroupThreadID, uint gi : SV_GroupIndex)
{
    float peak = 0.0, sum = 0.0;
    uint2 base = gid.xy * 64 + tid.xy * 4;
    [unroll] for (uint j = 0; j < 4; j++)
    [unroll] for (uint i = 0; i < 4; i++)
    {
        uint2 p = base + uint2(i, j);
        if (p.x < Size.x && p.y < Size.y)
        {
            float v = Lum709(Src.Load(int3(Origin + p, 0)).rgb) * 80.0;
            if (!isnan(v)) { peak = max(peak, v); sum += v; }
        }
    }
    Shared[gi] = float2(peak, sum);
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint s = 128; s > 0; s >>= 1)
    {
        if (gi < s) Shared[gi] = float2(max(Shared[gi].x, Shared[gi + s].x), Shared[gi].y + Shared[gi + s].y);
        GroupMemoryBarrierWithGroupSync();
    }
    if (gi == 0) Partials[gid.y * TilesX + gid.x] = Shared[0];
}

// --------------------------------------------------------------------------------------------------------- zebra

// ZebraMask.Exceeds: !(luminance * exposure <= white), so NaN is striped. `precise` keeps the products and sums
// unfused, as the CPU computes them.
[numthreads(8, 8, 1)]
void CSZebra(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= TilesX || id.y >= Size.y) return;
    uint word = 0;
    uint x0 = id.x * 32;
    for (uint b = 0; b < 32; b++)
    {
        uint x = x0 + b;
        if (x >= Size.x) break;
        float3 c = Src.Load(int3(Origin + uint2(x, id.y), 0)).rgb;
        precise float y = 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
        precise float lit = y * ZebraExposure;
        if (!(lit <= ZebraWhite)) word |= 1u << b;
    }
    Mask.Store((id.y * TilesX + id.x) * 4, word);
}
