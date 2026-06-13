cbuffer Params : register(b0)
{
    float brightness;
    float saturation;
    float sourceWidth;
    float sourceHeight;
    float4 tint;
    float outputWidth;
    float outputHeight;
    float sourceIsSrgb;
    float timeSeconds;
};

Texture2D src : register(t0);
SamplerState samp : register(s0);

struct PSIn
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD;
};

float hash11(float p)
{
    return frac(sin(p * 127.1) * 43758.5453123);
}

float EdgeSampleGuard(float2 uv)
{
    float2 c = abs(uv - 0.5) * 2.0;
    float d = max(c.x, c.y);
    return 1.0 - smoothstep(0.90, 1.0, d);
}

float3 SampleRgb(float2 uv, float2 offset)
{
    float guard = EdgeSampleGuard(uv);
    return src.Sample(samp, saturate(uv + offset * guard)).rgb;
}

float3 SampleRgb(float2 uv)
{
    return SampleRgb(uv, float2(0.0, 0.0));
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

float2 ApplySliceDisplacement(float2 uv, float inBurst, float cycleIdx, float burstFrame)
{
    float2 sampleUv = uv;
    float sliceShift = 0.0;

    [unroll]
    for (int i = 0; i < 3; i++)
    {
        float fi = (float)i;
        float sliceY = hash11(cycleIdx * 10.0 + fi * 3.1) * 0.86 + 0.07;
        float sliceH = 0.022 + hash11(cycleIdx * 20.0 + fi) * 0.05;
        if (abs(uv.y - sliceY) < sliceH * 0.5)
            sliceShift += (hash11(cycleIdx + fi * 7.3 + burstFrame) - 0.5) * 0.038;
    }

    sampleUv.x += sliceShift * inBurst;

    float2 blockId = floor(uv * float2(72.0, 40.0));
    float blockHash = hash11(blockId.x * 17.0 + blockId.y * 31.0 + cycleIdx);
    if (inBurst > 0.5 && blockHash > 0.935)
    {
        sampleUv += (float2(hash11(blockId.x + cycleIdx + 1.7), hash11(blockId.y + cycleIdx + 2.3)) - 0.5) * 0.028;
    }

    return sampleUv;
}

float rand(float2 co)
{
    return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453);
}

float4 main(PSIn input) : SV_TARGET
{
    float t = timeSeconds;
    float2 uv = input.uv;
    float edgeGuard = EdgeSampleGuard(uv);

    const float cycleLen = 2.75;
    float cycleIdx = floor(t / cycleLen);
    float inBurst = GlitchBurstMask(t);
    float burstFrame = floor(fmod(t, cycleLen) * 95.0);
    float microBurst = inBurst * (0.72 + 0.28 * hash11(cycleIdx * 100.0 + burstFrame));

    float2 sampleUv = ApplySliceDisplacement(uv, inBurst, cycleIdx, burstFrame);

    float pulse = 0.82 + 0.18 * sin(t * 1.8);
    float rgbSplit = lerp(0.0016, 0.013, microBurst) + 0.0008 * pulse;
    float2 texel = float2(1.0 / max(sourceWidth, 1.0), 1.0 / max(sourceHeight, 1.0));

    float3 color;
    color.r = SampleRgb(sampleUv, float2(rgbSplit * 1.35, -texel.y * 0.6)).r;
    color.g = SampleRgb(sampleUv).g;
    color.b = SampleRgb(sampleUv, float2(-rgbSplit * 1.35, texel.y * 0.6)).b;

    float3 cyanGhost = SampleRgb(sampleUv, float2(-rgbSplit * 0.55, texel.y));
    float3 magentaGhost = SampleRgb(sampleUv, float2(rgbSplit * 0.55, -texel.y));
    color += float3(0.0, 0.96, 1.0) * dot(cyanGhost, float3(0.333, 0.333, 0.333)) * (0.10 * microBurst + 0.03 * pulse);
    color += float3(1.0, 0.0, 0.71) * dot(magentaGhost, float3(0.333, 0.333, 0.333)) * (0.09 * microBurst + 0.025 * pulse);

    float scan = sin(uv.y * max(outputHeight, 1.0) * 3.14159265);
    scan = scan * scan;
    color *= 1.0 - scan * (0.05 + 0.07 * microBurst);

    float flashBand = step(0.58, hash11(floor(uv.y * 48.0) + cycleIdx + burstFrame));
    float flash = flashBand * microBurst * 0.16;
    float3 flashColor = hash11(cycleIdx + burstFrame + 0.5) > 0.5
        ? float3(0.0, 0.94, 1.0)
        : float3(1.0, 0.0, 0.67);
    color += flashColor * flash * edgeGuard;

    float2 px = uv * float2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float noise = (rand(px + t * 60.0) - 0.5) * (0.035 + 0.05 * microBurst);
    float sparkle = step(0.992, rand(floor(px * 0.42) + t * 2.7)) * microBurst * 0.22;
    color += noise + sparkle;

    float2 vig = uv - 0.5;
    color *= saturate(1.0 - dot(vig, vig) * 0.28);
    color = lerp(color, color * float3(0.92, 0.97, 1.08), 0.18 * microBurst);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
