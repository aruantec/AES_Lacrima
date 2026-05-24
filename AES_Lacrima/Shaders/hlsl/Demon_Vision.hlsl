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

float rand(float2 co)
{
    return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453);
}

float4 main(PSIn input) : SV_TARGET
{
    float t = timeSeconds;
    float2 uv = input.uv;
    float2 texel = 1.0 / float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    float2 center = float2(0.5, 0.5);
    float2 fromCenter = uv - center;
    float dist = length(fromCenter);

    float shimmer = sin(t * 3.5 + uv.y * 28.0) * 0.0015;
    float2 sampleUv = uv + float2(shimmer, shimmer * 0.6);

    float luma = dot(src.Sample(samp, sampleUv).rgb, float3(0.299, 0.587, 0.114));
    float lumaL = dot(src.Sample(samp, sampleUv - float2(texel.x, 0.0)).rgb, float3(0.299, 0.587, 0.114));
    float lumaR = dot(src.Sample(samp, sampleUv + float2(texel.x, 0.0)).rgb, float3(0.299, 0.587, 0.114));
    float lumaU = dot(src.Sample(samp, sampleUv - float2(0.0, texel.y)).rgb, float3(0.299, 0.587, 0.114));
    float lumaD = dot(src.Sample(samp, sampleUv + float2(0.0, texel.y)).rgb, float3(0.299, 0.587, 0.114));
    float edge = abs(luma * 4.0 - lumaL - lumaR - lumaU - lumaD);

    float noise = (rand(uv * float2(640.0, 360.0) + t * 41.0) - 0.5) * 0.1;
    float emberBurst = step(0.991, rand(float2(floor(t * 6.0), uv.x * 3.0))) * 0.2;
    luma = saturate(pow(luma * 1.3 + 0.03 + noise + emberBurst, 0.88));

    float3 hellDark = float3(0.06, 0.0, 0.01);
    float3 hellMid = float3(0.5, 0.03, 0.01);
    float3 hellHot = float3(1.0, 0.22, 0.03);
    float3 hellCore = float3(1.0, 0.7, 0.15);

    float3 color = lerp(hellDark, hellMid, smoothstep(0.0, 0.42, luma));
    color = lerp(color, hellHot, smoothstep(0.32, 0.8, luma));
    color = lerp(color, hellCore, smoothstep(0.7, 0.98, luma));

    color += float3(1.0, 0.12, 0.02) * edge * 2.2 * smoothstep(0.015, 0.22, edge);

    float ember = step(0.987, rand(uv * float2(900.0, 520.0) + t * 17.0));
    color += float3(1.0, 0.35, 0.05) * ember * smoothstep(0.9, 0.25, dist) * 0.65;

    float pulse = 0.9 + 0.1 * sin(t * 2.6);
    color *= pulse;

    float vignette = smoothstep(1.05, 0.32, dist * 1.12);
    float sightMask = smoothstep(1.08, 0.68, dist);
    color *= lerp(0.2, 1.0, vignette);

    float scan = sin(input.pos.y * 3.14159265 * 0.5);
    scan = scan * scan;
    color *= 1.0 - scan * 0.07;

    float ring = smoothstep(0.022, 0.0, abs(dist - 0.64)) * 0.38;
    color += float3(0.95, 0.06, 0.01) * ring * sightMask;

    float sightLines = abs(sin((fromCenter.y + t * 0.5) * 42.0 + fromCenter.x * 18.0));
    sightLines = smoothstep(0.92, 0.98, sightLines) * 0.08 * sightMask;
    color += float3(0.8, 0.05, 0.01) * sightLines;

    float lumaOut = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(lumaOut, lumaOut, lumaOut), color, saturation * 1.35);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
