using AES_Core.IO;
using Avalonia.OpenGL;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AES_Controls.GL;

/// <summary>
/// Persists linked OpenGL program binaries on disk (same approach as <see cref="GlShaderToyControl"/>).
/// </summary>
public static class GlProgramBinaryCache
{
    public const string ShaderToyCategory = "ShaderToy";
    public const string EmulationCategory = "Emulation";

    private const int GlProgramBinaryLength = 0x8741;
    private const int GlLinkStatus = 0x8B82;

    private static string CacheRoot => Path.Combine(ApplicationPaths.CacheDirectory, "ShaderCache");

    public static string ComputeKey(string content)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(content)))[..16];
    }

    public static string ComputeKey(string vertexSource, string fragmentSource) =>
        ComputeKey(vertexSource + "\n---\n" + fragmentSource);

    public static int TryLoadProgram(GlInterface gl, string category, string cacheKey)
    {
        var cacheFile = GetCacheFilePath(category, cacheKey);
        if (File.Exists(cacheFile))
        {
            var loaded = LoadProgramBinary(gl, cacheFile);
            if (loaded != 0)
                return loaded;
        }

        if (!string.Equals(category, ShaderToyCategory, StringComparison.Ordinal))
            return 0;

        var legacyCacheFile = Path.Combine(CacheRoot, $"{cacheKey}.bin");
        return File.Exists(legacyCacheFile) ? LoadProgramBinary(gl, legacyCacheFile) : 0;
    }

    public static void SaveProgram(GlInterface gl, int program, string category, string cacheKey)
    {
        if (program == 0)
            return;

        SaveProgramBinary(gl, program, GetCacheFilePath(category, cacheKey));
    }

    private static string GetCacheFilePath(string category, string cacheKey)
    {
        var directory = Path.Combine(CacheRoot, category);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{cacheKey}.bin");
    }

    private static unsafe int LoadProgramBinary(GlInterface gl, string file)
    {
        try
        {
            using var reader = new BinaryReader(File.Open(file, FileMode.Open));
            int format = reader.ReadInt32();
            byte[] data = reader.ReadBytes((int)(reader.BaseStream.Length - 4));
            int program = gl.CreateProgram();
            fixed (byte* pData = data)
            {
                var func =
                    (delegate* unmanaged[Stdcall]<int, int, void*, int, void>)gl.GetProcAddress("glProgramBinary");
                if (func != null)
                    func(program, format, pData, data.Length);
            }

            int success = 0;
            gl.GetProgramiv(program, GlLinkStatus, &success);
            if (success != 0)
                return program;

            gl.DeleteProgram(program);
            File.Delete(file);
        }
        catch
        {
            // ignored
        }

        return 0;
    }

    private static unsafe void SaveProgramBinary(GlInterface gl, int program, string file)
    {
        int length = 0;
        gl.GetProgramiv(program, GlProgramBinaryLength, &length);
        if (length <= 0)
            return;

        byte[] buffer = new byte[length];
        int retLen = 0, format = 0;
        fixed (byte* pBuf = buffer)
        {
            var func =
                (delegate* unmanaged[Stdcall]<int, int, int*, int*, void*, void>)gl.GetProcAddress("glGetProgramBinary");
            if (func != null)
                func(program, length, &retLen, &format, pBuf);
        }

        if (retLen <= 0)
            return;

        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var writer = new BinaryWriter(File.Open(file, FileMode.Create));
        writer.Write(format);
        writer.Write(buffer, 0, retLen);
    }
}
