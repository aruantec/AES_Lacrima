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

float3 SampleColor(float2 uv)
{
    return src.Sample(samp, saturate(uv)).rgb;
}

float GetLuma(float3 color)
{
    return dot(color, float3(0.299, 0.587, 0.114));
}

float3 SmoothSample(float2 uv, float2 texel)
{
    float3 sum =
        SampleColor(uv) +
        SampleColor(uv + float2(texel.x, 0.0)) +
        SampleColor(uv - float2(texel.x, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y)) +
        SampleColor(uv - float2(0.0, texel.y));
    return sum * 0.2;
}

float EdgeStrength(float2 uv, float2 texel)
{
    float c = GetLuma(SampleColor(uv));
    float l = GetLuma(SampleColor(uv - float2(texel.x, 0.0)));
    float r = GetLuma(SampleColor(uv + float2(texel.x, 0.0)));
    float u = GetLuma(SampleColor(uv - float2(0.0, texel.y)));
    float d = GetLuma(SampleColor(uv + float2(0.0, texel.y)));
    return abs(c * 4.0 - l - r - u - d);
}

float3 SoftToonColor(float3 color, float levels)
{
    float luma = max(GetLuma(color), 0.001);
    float x = luma * levels;
    float band = floor(x + 0.0001) / (levels - 1.0);
    float next = min(band + 1.0 / (levels - 1.0), 1.0);
    float blend = smoothstep(0.0, 0.65, frac(x));
    float quantized = lerp(band, next, blend);
    return color * (quantized / luma);
}

float4 main(PSIn input) : SV_TARGET
{
    float2 texel = 1.0 / float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 uv = input.uv;

    float3 color = SmoothSample(uv, texel * 0.75);
    color = SoftToonColor(color, 5.0);

    float edge = EdgeStrength(uv, texel * 1.25);
    float outline = smoothstep(0.1, 0.32, edge);
    float3 ink = float3(0.08, 0.1, 0.18);
    color = lerp(color, ink, outline * 0.28);

    float lumaOut = GetLuma(color);
    color = lerp(float3(lumaOut, lumaOut, lumaOut), color, saturation * 1.35);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
