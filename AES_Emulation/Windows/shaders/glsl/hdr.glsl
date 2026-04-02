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
uniform float FrameCount;
uniform float FrameDirection;

// UI Controls from SlangShaderPipeline
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

void main()
{
    vec4 tex = texture(Texture, vTex);
    vec3 color = tex.rgb;

    // 1. Linearize approximate sRGB
    color = pow(color, vec3(2.2));

    // 2. Fake HDR Expansion
    // Calculate luminance
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    
    // S-Curve Contrast Enhancement
    // This pushes darks down and highlights up
    vec3 colorContrasted = color * color * (3.0 - 2.0 * color);
    
    // Highlight Expansion: selectively boost high luminance areas
    // This simulates that "peak brightness" pop
    float highlightMask = smoothstep(0.4, 1.0, luma);
    vec3 highBoost = color * (1.0 + highlightMask * 1.5);
    
    color = mix(colorContrasted, highBoost, 0.5);

    // 3. Apply UI controls
    color *= uBrightness;
    
    // Saturation boost (SDR saturation is usually too low for HDR feel)
    // Baseline boost of 1.2 combined with user control
    float gray = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(gray), color, uSaturation * 1.15);

    // 4. Tint
    color *= uColorTint.rgb;

    // 5. Cinematic Tonemapping (ACES approximation)
    // This helps roll off the boosted highlights smoothly
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    color = clamp((color * (a * color + b)) / (color * (c * color + d) + e), 0.0, 1.0);

    // 6. Gamma correction back to sRGB/Display space
    color = pow(color, vec3(1.0 / 2.2));

    // Optional: Subtle Vignette
    vec2 center = vTex - 0.5;
    float vignette = 1.0 - dot(center, center) * 0.25;
    color *= vignette;

    fragColor = vec4(color, tex.a * uColorTint.a);
}

#endif