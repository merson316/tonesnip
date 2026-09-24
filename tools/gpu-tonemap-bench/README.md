# gpu-tonemap-bench

Measures the app's GPU tonemap path (`src/ToneSnip.Windows/Capture/GpuHdrFrame.cs`, `GpuTonemapper.cs` and
`Shaders/Tonemap.hlsl`) against its CPU path (`HalfFrame`) on this machine's real WGC captures. It uses the app's own
`ScreenCapture`, so the capture is the same in both paths. It is not part of the app, and CI does not build it.

```
tools/gpu-tonemap-bench/build.sh                  build into dist/gpu-tonemap-bench/ (Windows .NET SDK, mapped drive)
gpu-tonemap-bench list                            monitors, and adapter memory totals
gpu-tonemap-bench compile                         what a run-time D3DCompile would cost (the app embeds bytecode)
gpu-tonemap-bench diff [--rounds N]               CPU vs GPU bytes on each HDR frame, for every curve; the HDR
                                                  readouts (1-px nits, rect stats, fp16 crop, zebra mask); then a
                                                  stress image on every adapter and on WARP
gpu-tonemap-bench run --path cpu|gpu [--runs N] [--gap ms] [--tonemap t]
                                                  a snip's timings and memory, in a process of its own for each path
```

`diff` keeps each frame on the GPU as the app does and reads its pixels back whole, which is bit-exact (the crop and
1-px checks confirm it), so both paths see identical pixels. The stress image holds every half-float bit pattern in
each channel (NaN, infinities, negatives, subnormals) and a 30-stop hue ramp: pow and exp2 precision is the driver's,
so a result on one vendor says nothing about another.

GPU memory comes from the adapter counters (`\GPU Adapter Memory(*)\Dedicated Usage`, through PDH), not the
per-process ones, which climb when resources are re-created. The adapter figure is noisy (±50 MB): other apps and
DWM share the adapter.

The shader bytecode is rebuilt with `tools/compile-shaders.sh`, not with this tool.

`BENCH_VERBOSE=1` prints each exposure pass of `run`.

## Results: 2026-09-23 (dev/1.0.4-gpu)

AW3425DW 3440x1440 HDR (SDR white 212, peak 456) on an RTX 4090, and a UP2516D 1440x2560 SDR panel on the AMD iGPU.
`run`, three alternating runs a path, 10 snips each, medians of snips 2 to 10:

| | CPU | GPU |
|---|---|---|
| grab to BGRA, first snip | 293–301 ms | 278–284 ms |
| grab to BGRA, median | 44–45 ms | 27–29 ms |
| private WS peak in a grab | 155–192 MB | 89 MB |
| private WS held between snips | 155 MB | 71 MB |
| private WS after release | 133–134 MB | 47–50 MB |
| exposure re-tonemap, full frame | 5.7–5.8 ms | 3.9–4.3 ms |
| nits under cursor | 0.000 ms | 0.03–0.05 ms |
| selection stats | 0.10–0.12 ms (sampled) | 0.11–0.14 ms (every pixel) |
| fp16 crop 1720x720 + auto exposure | 29 ms | 32–33 ms |

`diff`: on real frames desktop (knee 1), Hable and ACES are bit-identical; desktop at knee 0.5 or 0.6 has 1–5 of 4.95 M
pixels 1 LSB off. On the stress image the worst is 1 LSB in at most 0.02 % of pixels, on the RTX 4090, the AMD iGPU
and WARP alike; the 1-px readout, the stats (to 0.01 nits), the fp16 crop and the zebra mask match the CPU exactly.
