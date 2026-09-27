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

## Readback bands: 2026-09-27 (dev/1.0.5-memory)

Same machine, `run --path gpu --runs 10`, two passes each, medians of snips 2 to 10. `run` now also times the snip's auto
exposure from the frame's samples alone (`IHdrFrame.Luminances`, over the same 1720x720 rectangle) and the Settings
preview's downsample, which the app takes from every HDR grab. The half-float readback was tried double-buffered, as
the BGRA one is, and every readback band at 2, 8 and 16 MB against today's 4 MB:

| half bands | band size | crop + AE | AE samples | preview | exposure | WS peak / held |
|---|---|---|---|---|---|---|
| one | 4 MB (as shipped) | 28.8–31.3 ms | 7.8–8.1 ms | 2.8–2.9 ms | 1.4–1.7 ms | 93–95 / 78 MB |
| two | 4 MB | 29.8–30.3 ms | 8.2–8.3 ms | 2.8 ms | 1.5 ms | 105 / 91 MB |
| two | 2 MB | 29.7–30.4 ms | 8.1–8.6 ms | 2.7–3.8 ms | 1.8 ms | 91–93 / 77 MB |
| two | 8 MB | 30.4 ms | 8.6 ms | 3.4 ms | 1.3 ms | 134 / 123 MB |
| two | 16 MB | 29.2–29.6 ms | 7.7–7.9 ms | 2.6–2.7 ms | 1.4 ms | 144 / 89 MB |

Neither buys time: the copies are small next to the Map round trips, and the full-frame exposure pass is already
1.4 ms. Wider bands only add system memory, so the bands stay as they are.
