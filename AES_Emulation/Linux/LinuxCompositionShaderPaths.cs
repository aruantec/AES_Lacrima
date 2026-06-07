using System;
using System.IO;

namespace AES_Emulation.Linux;

internal static class LinuxCompositionShaderPaths
{
    public static string? ResolvePresetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".glsl" or ".glslp" or ".slang" or ".slangp" or ".hlsl"))
            return null;

        if (ext.Contains("slang", StringComparison.Ordinal))
        {
            var glslPath = path
                .Replace(".slangp", ".glslp", StringComparison.OrdinalIgnoreCase)
                .Replace(".slang", ".glsl", StringComparison.OrdinalIgnoreCase)
                .Replace($"{Path.DirectorySeparatorChar}slang{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}glsl{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(glslPath))
                return glslPath;
        }
        if (ext == ".hlsl")
            return ResolveGlslFallbackForHlsl(path);

        return path;
    }

    public static string? ResolveGlslFallbackForHlsl(string hlslPath)
    {
        if (string.IsNullOrWhiteSpace(hlslPath))
            return null;

        var glslPath = hlslPath
            .Replace($"{Path.DirectorySeparatorChar}hlsl{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}glsl{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            .Replace(".hlsl", ".glsl", StringComparison.OrdinalIgnoreCase);

        return File.Exists(glslPath) ? glslPath : null;
    }
}
