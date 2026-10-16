# Samples

Recorded frame sequences for tuning and regression-testing change detection.

- `real/<name>/`: captures of real videos (V1–V3 in the implementation plan, §11), recorded every tick with `scrimshaw-harness record`. `v3` is the Phase 0 window capture; `v1`, `v2` and `v3-full` are full screen (Phase 2).
- `synthetic/<name>/`: sequences from `scrimshaw-harness synth`.

Each folder holds `frame_NNNNN.png` files (git-ignored because they're large) and a committed `labels.json`.
The labels list which frames show each page cleanly, plus ranges where saving is acceptable either way.
The format is documented in `tools/Scrimshaw.Harness/Labels.cs`.

```
dotnet run --project tools/Scrimshaw.Harness -- replay samples/real/v3
```
