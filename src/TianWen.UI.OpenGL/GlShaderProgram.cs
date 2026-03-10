using Silk.NET.OpenGL;

namespace TianWen.UI.OpenGL;

/// <summary>
/// Compiles and links a vertex/fragment shader pair into an OpenGL program.
/// Disposes the intermediate shader objects after linking.
/// </summary>
internal sealed class GlShaderProgram : IDisposable
{
    private readonly GL _gl;

    public uint Handle { get; }

    private GlShaderProgram(GL gl, uint handle)
    {
        _gl = gl;
        Handle = handle;
    }

    public static GlShaderProgram Create(GL gl, string vertexSource, string fragmentSource)
    {
        var vs = CompileShader(gl, ShaderType.VertexShader, vertexSource);
        var fs = CompileShader(gl, ShaderType.FragmentShader, fragmentSource);

        var handle = gl.CreateProgram();
        gl.AttachShader(handle, vs);
        gl.AttachShader(handle, fs);
        gl.LinkProgram(handle);

        gl.GetProgram(handle, ProgramPropertyARB.LinkStatus, out var status);
        if (status == 0)
        {
            var log = gl.GetProgramInfoLog(handle);
            gl.DeleteProgram(handle);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        gl.DetachShader(handle, vs);
        gl.DetachShader(handle, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        return new GlShaderProgram(gl, handle);
    }

    public void Use() => _gl.UseProgram(Handle);

    public void SetMatrix4(string name, ReadOnlySpan<float> matrix)
    {
        var location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.UniformMatrix4(location, 1, false, matrix);
        }
    }

    public void SetVector2(string name, float x, float y)
    {
        var location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform2(location, x, y);
        }
    }

    public void SetMatrix2(string name, ReadOnlySpan<float> matrix)
    {
        var location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.UniformMatrix2(location, 1, false, matrix);
        }
    }

    public void SetVector3(string name, float x, float y, float z)
    {
        var location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform3(location, x, y, z);
        }
    }

    public void SetVector4(string name, float x, float y, float z, float w)
    {
        var location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform4(location, x, y, z, w);
        }
    }

    public void SetInt(string name, int value)
    {
        var location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    public void SetFloat(string name, float value)
    {
        var location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    public void Dispose() => _gl.DeleteProgram(Handle);

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var status);
        if (status == 0)
        {
            var log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }

        return shader;
    }
}
