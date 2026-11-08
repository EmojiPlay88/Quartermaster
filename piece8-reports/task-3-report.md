# Task 3 report: Counter image rendering

Status: DONE

## Implemented
- `src/Quartermaster.Render/CounterImages.cs`: `RowSize` (64x48), `Key` (type string + trait states + moved flag + size), `PreviewSize` (long side max(200, 2x native), capped to 80% of screen on both axes, aspect kept), `TryGetCached`, `Render` (lock-guarded renderer, double-checked cache, null on exception/invalid size/null piece), `Clear`.
- Drawing is rendered at the target scale through `RenderSprite(p, scale)` (no upscaling). The 1.5x scale cap is kept only for `RowSize` so palette output is unchanged.
- `LruImageCache.Clear()` added. `Clear()` bumps a generation counter so an in-flight render does not repopulate the cache after a clear.
- `PaletteThumbs` keeps its public API and 64x48 size; now delegates to `CounterImages`.

## Tests
- New `tests/Quartermaster.Render.Tests/CounterImagesTests.cs`: 10 tests (the 9 named plus `Render_Preview_RendersAtScaleSharp`). All pass.
- Full `dotnet test Quartermaster.slnx`: all green (Render.Tests 831, Core 260, Rules 181, Vassal 119, Net 41, Ocr 41, Relay 38 + 1 skipped).

## TDD evidence
- RED: with CounterImages.cs removed, test project failed to compile (CS0103 'CounterImages' does not exist). Note: I wrote the implementation before the RED run and demonstrated RED by temporarily moving the file away.
- GREEN: 10/10 pass after restoring.

## Files changed
- src/Quartermaster.Render/CounterImages.cs (new)
- src/Quartermaster.Render/LruImageCache.cs (Clear)
- src/Quartermaster.App/PaletteThumbs.cs
- tests/Quartermaster.Render.Tests/CounterImagesTests.cs (new)

## Self-review / concerns
- Rendering one piece blocks others (single lock), as before; cache hits bypass the lock.
- A piece with no image yields a transparent image (32x32 fallback box), not null, matching old palette behaviour; "undrawable" = null piece, bad size, or exception.
- Cached SKImages are shared; callers must not dispose them (evicted images are left to the finalizer, like LruImageCache elsewhere).
- Preview size callers must pass native bounds themselves (e.g. `PieceGeometry.LocalBounds`).
