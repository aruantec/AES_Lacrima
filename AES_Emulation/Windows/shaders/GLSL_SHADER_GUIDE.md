# GLSL Shader Development Guide

This guide explains how to create and add GLSL shaders for the AES_Lacrima game capture system.

## Directory Structure

All shaders should be placed in: `AES_Controls/EmuGrabbing/shaders/glsl/`

Supported file extensions:
- `.glsl` - Single GLSL shader files (recommended)
- `.glslp` - RetroArch shader preset files
- `.slang` - Slang shader files
- `.slangp` - Slang preset files

## Shader Template

Here's a basic template for creating GLSL shaders:

```glsl
#version 150

// Input texture sampler
uniform sampler2D tex;

// Common shader uniforms (automatically provided by the pipeline)
uniform float FrameCount;        // Frame number since shader load
uniform float FrameDirection;    // 1.0 for forward, -1.0 for reverse
uniform vec2 TextureSize;        // Input texture dimensions
uniform vec2 InputSize;          // Input video frame dimensions
uniform vec2 OutputSize;         // Output render target dimensions

// Vertex shader input (provided by the control)
in vec2 vTex;                    // Texture coordinates [0, 1]

// Fragment shader output
out vec4 FragColor;              // Final pixel color

void main()
{
    // Sample the input texture
    vec4 color = texture(tex, vTex);
    
    // Apply your effect here
    // Examples:
    // - Color manipulation
    // - Filtering
    // - Distortion
    // - Bloom/Glow
    
    // Output the final color
    FragColor = color;
}
```

## Important Notes

1. **Version Requirement**: Use `#version 150` or compatible version
2. **Sampler Name**: Always use `tex` as the sampler name
3. **Vertex Coordinates**: Use `vTex` for normalized texture coordinates [0, 1]
4. **Output Variable**: Use `FragColor` for the final color output
5. **Uniforms**: Use the standard uniforms provided (see above)

## Common Patterns

### Simple Color Effect
```glsl
void main()
{
    vec4 color = texture(tex, vTex);
    // Desaturate
    float gray = dot(color.rgb, vec3(0.299, 0.587, 0.114));
    FragColor = vec4(vec3(gray), color.a);
}
```

### Pixel Shader Effect
```glsl
void main()
{
    float pixelSize = 0.02;
    vec2 pixelated = floor(vTex / pixelSize) * pixelSize;
    FragColor = texture(tex, pixelated);
}
```

### Scanline Effect
```glsl
void main()
{
    vec4 color = texture(tex, vTex);
    float lines = sin(vTex.y * 800.0) * 0.5 + 0.5;
    FragColor = color * mix(0.8, 1.0, lines);
}
```

### Screen Distortion
```glsl
void main()
{
    vec2 uv = vTex - 0.5;
    float len = length(uv);
    float bend = 0.1;
    vec2 distorted = (uv / len) * tan(len * bend) * (2.0 / bend) + 0.5;
    FragColor = texture(tex, distorted);
}
```

## Testing Your Shaders

1. Create your `.glsl` file in `AES_Controls/EmuGrabbing/shaders/glsl/`
2. Update `MainWindow.axaml` to point to your shader:
   ```xml
   RetroarchShaderFile="E:\Projects\AES_Lacrima\AES_Controls\EmuGrabbing\shaders\glsl\your-shader.glsl"
   ```
3. Build and run the application
4. Check the Debug Output window for any compilation errors
5. Look for messages like:
   - `[WGC] Loaded GLSL Shader: ...` - Success
   - `[WGC] LoadShaderPreset Error: ...` - Failed

## Performance Considerations

- Keep shaders simple to maintain 60 FPS
- Avoid expensive operations per pixel (texture lookups, loops)
- Use built-in functions (sin, cos, pow) as they're optimized
- Use lower precision when possible (mediump for mobile)

## Uniforms Available

| Uniform | Type | Purpose |
|---------|------|---------|
| `tex` | sampler2D | The game capture texture |
| `FrameCount` | float | Incremental frame counter |
| `FrameDirection` | float | Animation direction |
| `TextureSize` | vec2 | Texture pixel dimensions |
| `InputSize` | vec2 | Input video frame size |
| `OutputSize` | vec2 | Output render target size |

Use `TextureSize` for per-pixel calculations and `InputSize`/`OutputSize` for aspect ratio aware effects.

## Example Complete Shader Files

See the following for working examples:
- `pixelation.glsl` - Pixelation effect
- `crt-geom.glsl` - CRT geometry shader (if available)
- Any other `.glsl` files in the glsl folder

