---
name: test-filter
description: Run TianWen tests matching a name pattern via `dotnet test --filter "FullyQualifiedName~<pattern>"`. Use when the user asks to run specific tests by name (e.g. "run the Mosaic tests", "test the Session class", "run guider tests"). Dispatches between TianWen.Lib.Tests and TianWen.Lib.Tests.Functional based on the pattern.
---

Usage: `/test-filter <pattern>` or pass the pattern as an argument.

Examples: `Mosaic`, `Session`, `Guider|NeuralGuide`, `Catalog`.

Runs from src/ directory:
```
cd src && dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~<pattern>"
```

For functional tests (Session*, GuiderCalibration*, GuideLoop*), use the
functional test project instead:
```
cd src && dotnet test TianWen.Lib.Tests.Functional --filter "FullyQualifiedName~<pattern>"
```

IMPORTANT: Never run unit and functional tests concurrently - thread pool
starvation causes flakes. Run one project at a time.

Show full output - do not pipe through head or tail.
