using Avalonia.OpenGL;

namespace AES_Emulation.Windows
{
    public static class GlHelper
    {
        public static (string, bool) GetShaderVersion(GlInterface gl)
        {
            string? version = gl.GetString(0x1F02); // GL_VERSION
            if (string.IsNullOrEmpty(version))
            {
                // Fallback to a default version string if GL_VERSION is not available
                return ("#version 330 core", false);
            }
            bool isES = version.Contains("ES");

            string shaderVersion;
            if (isES)
            {
                shaderVersion = "#version 300 es";
            }
            else
            {
                shaderVersion = "#version 330 core";
                var verToken = version.Split(' ')[0];
                var parts = verToken.Split('.');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out var major) &&
                    int.TryParse(parts[1], out var minor))
                {
                    if (major > 4 || (major == 4 && minor >= 1))
                        shaderVersion = "#version 410 core";
                    else if (major > 3 || (major == 3 && minor >= 3))
                        shaderVersion = "#version 330 core";
                    else if (major == 3 && minor >= 0)
                        shaderVersion = "#version 130";
                }
            }
            return (shaderVersion, isES);
        }
    }
}