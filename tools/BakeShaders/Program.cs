using Vortice.ShaderCompiler;

// Bakes TianWen.UI.Shared's GLSL 450 shaders (Shaders/*.vert, Shaders/*.frag) to SPIR-V at build
// time, so the Vk*Pipeline classes load pre-compiled bytecode instead of invoking shaderc at runtime.
// Runtime shaderc (Silk.NET.Shaderc.Native via Vortice.ShaderCompiler) ships NO android RID, so the
// runtime path was unloadable on Android; baking here removes shaderc from the shipped assembly
// entirely (also trims AOT surface + first-frame startup cost). Mirrors SdlVulkan.Renderer's
// tools/BakeShaders. Uses the exact CompilerOptions the runtime used (Vulkan_1_0) so the SPIR-V is
// equivalent to the old runtime compile.
//
// usage: BakeShaders <shaderDir> [--target spirv]
//   <shaderDir>  directory holding *.vert / *.frag; output goes to <shaderDir>/spirv/
//   --target     output format (default: spirv; only spirv is implemented)

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: BakeShaders <shaderDir> [--target spirv]");
    return 1;
}

var shaderDir = Path.GetFullPath(args[0]);
var target = "spirv";
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--target" && i + 1 < args.Length)
        target = args[++i].ToLowerInvariant();
    else
    {
        Console.Error.WriteLine($"unknown argument: {args[i]}");
        return 1;
    }
}

if (!Directory.Exists(shaderDir))
{
    Console.Error.WriteLine($"shader directory not found: {shaderDir}");
    return 1;
}

if (target != "spirv")
{
    Console.Error.WriteLine($"unknown target: {target} (only spirv is implemented)");
    return 1;
}

var outDir = Path.Combine(shaderDir, "spirv");
Directory.CreateDirectory(outDir);

// *.vert -> VertexShader, *.frag -> FragmentShader. Ordered for a stable, reviewable build log.
var sources = Directory.GetFiles(shaderDir, "*.vert")
    .Concat(Directory.GetFiles(shaderDir, "*.frag"))
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToArray();

if (sources.Length == 0)
{
    Console.Error.WriteLine($"no *.vert / *.frag shaders found in {shaderDir}");
    return 1;
}

using var compiler = new Compiler();
var baked = 0;
var manifest = new System.Text.StringBuilder();
foreach (var src in sources)
{
    var fileName = Path.GetFileName(src);
    var kind = Path.GetExtension(src) == ".vert" ? ShaderKind.VertexShader : ShaderKind.FragmentShader;
    var source = File.ReadAllText(src);

    // The build's TWSH0001 check compares each source's content with the hash recorded here, never
    // modification times: a time check fires for ever on a clone that pulled a source edit which bakes
    // to identical SPIR-V (a comment, whitespace), since git then rewrites the source and not the .spv
    // (#792). Hash the bytes git stores (LF), so the record is the same whichever host baked it.
    var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n"))));
    manifest.Append(hash).Append("  ").Append(fileName).Append('\n');

    // Shader source must be ASCII: shaderc's lexer chokes on non-ASCII bytes even inside //
    // comments (a stray em dash reports as a cryptic "unexpected end of file"). Point at it clearly.
    var badLine = source.Split('\n').Select((line, n) => (line, n)).FirstOrDefault(t => t.line.Any(c => c > 0x7F));
    if (badLine.line is not null)
        Console.Error.WriteLine($"warning: {fileName}({badLine.n + 1}) contains non-ASCII characters; shaderc may reject it. Use ASCII (e.g. '--' for em dashes).");

    // Vulkan_1_0 target env matches the baseline the Vk*Pipeline classes compiled against and keeps
    // the bytecode valid on the widest device range (incl. Mali baseline on Android).
    var options = new CompilerOptions
    {
        TargetEnv = TargetEnvironmentVersion.Vulkan_1_0,
        ShaderStage = kind
    };
    var result = compiler.Compile(source, fileName, options);
    if (result.Status != CompilationStatus.Success)
    {
        Console.Error.WriteLine($"shader compilation failed ({fileName}): {result.ErrorMessage}");
        return 3;
    }

    var outPath = Path.Combine(outDir, fileName + ".spv");
    File.WriteAllBytes(outPath, result.Bytecode);
    Console.WriteLine($"{fileName} -> {Path.GetFileName(outPath)} ({result.Bytecode.Length} bytes)");
    baked++;
}

// sha256sum format, paths relative to the shader directory, so `sha256sum -c spirv/sources.sha256` run
// from there checks it by hand too. Written only once every shader compiled, so a failed bake never
// records a source as baked.
File.WriteAllText(Path.Combine(outDir, "sources.sha256"), manifest.ToString(), new System.Text.UTF8Encoding(false));

Console.WriteLine($"baked {baked} shader(s) [{target}] -> {outDir}");
return 0;
