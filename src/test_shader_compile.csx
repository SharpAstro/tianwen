#r "nuget: Vortice.ShaderCompiler, 1.9.0"

using Vortice.ShaderCompiler;

void TestShader(string path)
{
    var source = File.ReadAllText(path);
    using var compiler = new Compiler();
    var options = new CompilerOptions
    {
        TargetEnv = TargetEnvironmentVersion.Vulkan_1_0,
        ShaderStage = ShaderKind.FragmentShader
    };
    var result = compiler.Compile(source, Path.GetFileName(path), options);
    if (result.Status == CompilationStatus.Success)
        Console.WriteLine($"OK: {path} ({result.Bytecode.Length} bytes SPIR-V)");
    else
        Console.WriteLine($"FAIL: {path}\n  {result.ErrorMessage}");
}

TestShader("test_shader_original.frag");
TestShader("test_shader_step1.frag");
TestShader("test_shader_step2.frag");
