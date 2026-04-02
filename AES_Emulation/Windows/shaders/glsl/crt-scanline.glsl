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

// UI Controls
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

vec2 crt(vec2 uv)
{
    uv = (uv - 0.5) * 2.0;
    uv *= 1.1;
    uv.x *= 1.0 + pow((abs(uv.y) / 5.0), 2.0);
    uv.y *= 1.0 + pow((abs(uv.x) / 4.0), 2.0);
    uv = (uv / 2.0) + 0.5;
    uv = uv * 0.92 + 0.04;
    uv = clamp(uv, 0.0, 1.0);
    return uv;
}

void main()
{
    vec2 q = vTex * TextureSize / InputSize;
    vec2 uv = q;
    uv = crt(uv) * InputSize / TextureSize;

    // Chromatic aberration for CRT effect
    float chroma = 0.0025;
    vec3 color;
    color.r = texture(Texture, uv + vec2(chroma, 0.0)).r;
    color.g = texture(Texture, uv).g;
    color.b = texture(Texture, uv - vec2(chroma, 0.0)).b;

    // Scanline effect (dark lines for better visibility)
    float line = sin(uv.y * TextureSize.y * 3.14159 * 2.0);
    line = line * line;
    float scanline = 1.0 - (0.5 * line * 0.8);
    color *= scanline;

    // Vignette to make scanlines more pronounced
    vec2 center_dist = q - 0.5;
    float vignette = 1.0 - dot(center_dist, center_dist) * 0.5;
    color *= clamp(vignette, 0.0, 1.0);

    // Border mask (soft rounded corners)
    vec2 p = -1.0 + 2.0 * q;
    float f = (1.0 - p.x * p.x) * (1.0 - p.y * p.y);
    float frameShape = 0.35;
    float frameLimit = 0.30;
    float frameSharpness = 1.10;
    float frame = clamp(frameSharpness * (pow(f, frameShape) - frameLimit), 0.0, 1.0);
    color *= frame;

    // Apply UI Global Controls
    color *= uBrightness;
    
    // Saturation
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation);
    
    // Tint
    color *= uColorTint.rgb;

    fragColor = vec4(color, uColorTint.a);
}

#endif
