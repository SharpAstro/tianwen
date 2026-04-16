Run TianWen tests with a name filter.

Usage: /test-filter <pattern>
Example: /test-filter Mosaic
Example: /test-filter Session
Example: /test-filter Guider|NeuralGuide
Example: /test-filter Catalog

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

The filter pattern is: $ARGUMENTS
