#version 150

#ifdef VERTEX

layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;

out vec2 vTex;

void main()
{
    vTex = TexCoord;
    gl_Position = vec4(VertexCoord, 0.0, 1.0);
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

float rand(vec2 co)
{
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}

void main()
{
    float iTime = FrameCount * 0.03;
    vec2 uv = vTex;
    
    // 1. Rhythmic tape jump (Vertical instability)
    float jump = (rand(vec2(iTime, 0.0)) > 0.99 ? 0.008 * sin(iTime * 20.0) : 0.0);
    uv.y = mod(uv.y + jump, 1.0);

    // 2. Tracking Noise Bands (Moving BOTTOM TO TOP)
    // Speed varies over time for a more organic analog feel
    float bandSpeed = 0.12 + 0.08 * sin(iTime * 0.4);
    float bandPos = fract(-iTime * bandSpeed); 
    
    float bandDist = abs(uv.y - bandPos);
    float bandIntensity = smoothstep(0.12, 0.0, bandDist);
    
    // Horizontal tearing/displacement in the tracking bands
    float tear = (rand(vec2(iTime, uv.y * 0.2)) - 0.5) * 0.05 * bandIntensity;
    uv.x += tear;

    // 3. VHS horizontal wobble (General tape drift)
    float wobble = sin(iTime * 4.0 + uv.y * 10.0) * 0.0012;
    uv.x += wobble;

    // 4. VCR Head Switching Noise (Lower portion distortion)
    float headArea = 0.08;
    float headMask = smoothstep(headArea, 0.0, uv.y);
    float headSwirl = sin(uv.y * 40.0 - iTime * 12.0) * 0.015 * headMask;
    uv.x += headSwirl;
    float headNoise = (rand(vec2(iTime * 2.0, uv.y)) - 0.5) * 0.04 * headMask;
    uv.x += headNoise;

    // 5. Sample color with chroma shift
    vec2 sampledUv = clamp(uv, 0.0, 1.0);
    float chroma = 0.003 + 0.001 * sin(iTime * 0.5);
    vec3 color;
    color.r = texture(Texture, sampledUv + vec2(chroma, 0.0)).r;
    color.g = texture(Texture, sampledUv).g;
    color.b = texture(Texture, sampledUv - vec2(chroma, 0.0)).b;

    // 6. Signal Artifacts: Snow & Static
    float snow = (rand(sampledUv + iTime) - 0.5) * 0.08;
    float bandStatic = (rand(sampledUv * 0.5 + iTime) - 0.5) * 0.5 * bandIntensity;
    color += (snow + bandStatic) * (1.0 + headMask);

    // 7. UI Global Controls
    color *= uBrightness;
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation * 0.9);
    color *= uColorTint.rgb;

    // 8. Overlays: Scanlines & Vignette
    float scanline = sin(uv.y * TextureSize.y * 2.5) * 0.04 + 0.96;
    color *= scanline;

    float vig = 1.0 - dot(vTex - 0.5, vTex - 0.5) * 0.4;
    color *= vig;

    fragColor = vec4(clamp(color, 0.0, 1.0), uColorTint.a);
}

#endif