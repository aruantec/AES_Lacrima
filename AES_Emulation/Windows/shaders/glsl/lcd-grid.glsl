#version 150

#ifdef VERTEX

layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;

out vec2 vTex;
uniform mat4 MVPMatrix;

void main()
{
    vTex = TexCoord;
    gl_Position = MVPMatrix * vec4(VertexCoord, 0.0, 1.0);
}

#endif

#ifdef FRAGMENT

uniform sampler2D Texture;
uniform vec2 TextureSize;
uniform vec2 InputSize;
uniform vec2 OutputSize;

// UI Controls
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

void main()
{
    // Basic LCD grid effect
    // scale to input size
    vec2 texelSize = 1.0 / TextureSize;
    vec2 pos = vTex * TextureSize;
    
    // Grid lines
    vec2 grid = abs(fract(pos - 0.5) - 0.5) / (0.5 * fwidth(pos));
    float gridLine = min(grid.x, grid.y);
    float mask = smoothstep(0.0, 1.0, gridLine);
    
    // RGB Subpixels
    float subpixel = fract(pos.x * 3.0);
    vec3 subMask = vec3(0.0);
    if (subpixel < 1.0) subMask.r = 1.0;
    else if (subpixel < 2.0) subMask.g = 1.0;
    else subMask.b = 1.0;
    
    vec4 color = texture(Texture, vTex);
    
    // Apply masks
    color.rgb *= mix(0.7, 1.0, mask);
    color.rgb *= mix(vec3(0.8), vec3(1.2), subMask);
    
    // Apply UI Global Controls
    color.rgb *= uBrightness;
    
    // Saturation
    float luma = dot(color.rgb, vec3(0.299, 0.587, 0.114));
    color.rgb = mix(vec3(luma), color.rgb, uSaturation);
    
    // Tint
    color.rgb *= uColorTint.rgb;

    fragColor = vec4(color.rgb, uColorTint.a);
}

#endif
