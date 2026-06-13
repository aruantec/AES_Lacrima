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
uniform float FrameCount;
uniform float FrameDirection;
uniform vec2 TextureSize;
uniform vec2 InputSize;
uniform vec2 OutputSize;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

#define timeSeconds (FrameCount / 60.0)
#define brightness uBrightness
#define saturation uSaturation
#define tint uColorTint
#define sourceWidth TextureSize.x
#define sourceHeight TextureSize.y
#define outputWidth OutputSize.x
#define outputHeight OutputSize.y
#define sourceIsSrgb 1.0

float rand(vec2 co)
{
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}

float hash11(float p)
{
    return fract(sin(p * 127.1) * 43758.5453123);
}

float EdgeSampleGuard(vec2 uv)
{
    vec2 c = abs(uv - 0.5) * 2.0;
    float d = max(c.x, c.y);
    return 1.0 - smoothstep(0.90, 1.0, d);
}

vec3 SampleRgb(vec2 uv, vec2 offset)
{
    float guard = EdgeSampleGuard(uv);
    return texture(Texture, clamp(uv + offset * guard, 0.0, 1.0)).rgb;
}

vec3 SampleRgb(vec2 uv)
{
    return SampleRgb(uv, vec2(0.0, 0.0));
}

float GlitchBurstMask(float t)
{
    const float cycleLen = 2.75;
    float cycleIdx = floor(t / cycleLen);
    float cycleRand = hash11(cycleIdx * 13.7);
    float burstStart = cycleRand * (cycleLen - 0.18);
    float localT = t - cycleIdx * cycleLen - burstStart;
    float burstDur = 0.055 + hash11(cycleIdx * 5.3) * 0.075;
    return step(0.0, localT) * step(localT, burstDur);
}

vec2 ApplySliceDisplacement(vec2 uv, float inBurst, float cycleIdx, float burstFrame)
{
    vec2 sampleUv = uv;
    float sliceShift = 0.0;

    for (int i = 0; i < 3; i++)
    {
        float fi = float(i);
        float sliceY = hash11(cycleIdx * 10.0 + fi * 3.1) * 0.86 + 0.07;
        float sliceH = 0.022 + hash11(cycleIdx * 20.0 + fi) * 0.05;
        if (abs(uv.y - sliceY) < sliceH * 0.5)
            sliceShift += (hash11(cycleIdx + fi * 7.3 + burstFrame) - 0.5) * 0.038;
    }

    sampleUv.x += sliceShift * inBurst;

    vec2 blockId = floor(uv * vec2(72.0, 40.0));
    float blockHash = hash11(blockId.x * 17.0 + blockId.y * 31.0 + cycleIdx);
    if (inBurst > 0.5 && blockHash > 0.935)
    {
        sampleUv += (vec2(hash11(blockId.x + cycleIdx + 1.7), hash11(blockId.y + cycleIdx + 2.3)) - 0.5) * 0.028;
    }

    return sampleUv;
}

void main()
{
    float t = timeSeconds;
    vec2 uv = vTex;
    float edgeGuard = EdgeSampleGuard(uv);

    const float cycleLen = 2.75;
    float cycleIdx = floor(t / cycleLen);
    float inBurst = GlitchBurstMask(t);
    float burstFrame = floor(mod(t, cycleLen) * 95.0);
    float microBurst = inBurst * (0.72 + 0.28 * hash11(cycleIdx * 100.0 + burstFrame));

    vec2 sampleUv = ApplySliceDisplacement(uv, inBurst, cycleIdx, burstFrame);

    float pulse = 0.82 + 0.18 * sin(t * 1.8);
    float rgbSplit = mix(0.0016, 0.013, microBurst) + 0.0008 * pulse;
    vec2 texel = vec2(1.0 / max(sourceWidth, 1.0), 1.0 / max(sourceHeight, 1.0));

    vec3 color;
    color.r = SampleRgb(sampleUv, vec2(rgbSplit * 1.35, -texel.y * 0.6)).r;
    color.g = SampleRgb(sampleUv).g;
    color.b = SampleRgb(sampleUv, vec2(-rgbSplit * 1.35, texel.y * 0.6)).b;

    vec3 cyanGhost = SampleRgb(sampleUv, vec2(-rgbSplit * 0.55, texel.y));
    vec3 magentaGhost = SampleRgb(sampleUv, vec2(rgbSplit * 0.55, -texel.y));
    color += vec3(0.0, 0.96, 1.0) * dot(cyanGhost, vec3(0.333)) * (0.10 * microBurst + 0.03 * pulse);
    color += vec3(1.0, 0.0, 0.71) * dot(magentaGhost, vec3(0.333)) * (0.09 * microBurst + 0.025 * pulse);

    float scan = sin(uv.y * max(outputHeight, 1.0) * 3.14159265);
    scan = scan * scan;
    color *= 1.0 - scan * (0.05 + 0.07 * microBurst);

    float flashBand = step(0.58, hash11(floor(uv.y * 48.0) + cycleIdx + burstFrame));
    float flash = flashBand * microBurst * 0.16;
    vec3 flashColor = hash11(cycleIdx + burstFrame + 0.5) > 0.5
        ? vec3(0.0, 0.94, 1.0)
        : vec3(1.0, 0.0, 0.67);
    color += flashColor * flash * edgeGuard;

    vec2 px = uv * vec2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float noise = (rand(px + t * 60.0) - 0.5) * (0.035 + 0.05 * microBurst);
    float sparkle = step(0.992, rand(floor(px * 0.42) + t * 2.7)) * microBurst * 0.22;
    color += noise + sparkle;

    vec2 vig = uv - 0.5;
    color *= clamp(1.0 - dot(vig, vig) * 0.28, 0.0, 1.0);
    color = mix(color, color * vec3(0.92, 0.97, 1.08), 0.18 * microBurst);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
