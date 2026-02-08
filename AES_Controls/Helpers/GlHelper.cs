using Avalonia.OpenGL;

namespace AES_Controls.Helpers
{
    /// <summary>
    /// Small helper for working with the OpenGL
    /// </summary>
    public static class GlHelper
    {
        /// <summary>
        /// Determines an appropriate GLSL shader version directive for the
        /// current GL context and whether the context represents OpenGL ES.
        /// </summary>
        /// <param name="gl">The GL interface to query.</param>
        /// <returns>
        /// A tuple where the first item is the shader version directive
        /// (for example "#version 330 core") and the second item is true
        /// when the context is OpenGL ES.
        /// </returns>
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
