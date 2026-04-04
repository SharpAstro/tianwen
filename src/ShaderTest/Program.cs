using Vortice.ShaderCompiler;

var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
foreach (var file in Directory.GetFiles(dir, "test_shader_*.frag").Order())
{
    var source = File.ReadAllText(file);
    using var compiler = new Compiler();
    var options = new CompilerOptions
    {
        TargetEnv = TargetEnvironmentVersion.Vulkan_1_0,
        ShaderStage = ShaderKind.FragmentShader
    };
    var result = compiler.Compile(source, Path.GetFileName(file), options);
    if (result.Status == CompilationStatus.Success)
        Console.WriteLine($"OK: {Path.GetFileName(file)} ({result.Bytecode.Length} bytes SPIR-V)");
    else
        Console.WriteLine($"FAIL: {Path.GetFileName(file)}\n  {result.ErrorMessage}");
}
