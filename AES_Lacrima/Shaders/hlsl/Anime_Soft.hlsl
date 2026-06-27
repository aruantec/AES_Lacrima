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

float3 BicubicBlend(float2 uv, float2 texel)
{
    float3 sum = float3(0.0, 0.0, 0.0);
    float wsum = 0.0;

    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            float2 offset = float2(x, y) * texel;
            float dist = length(float2(x, y));
            float w = exp(-dist * dist * 0.55);
            sum += SampleColor(uv + offset) * w;
            wsum += w;
        }
    }

    return sum / wsum;
}

float3 ApplyVibrance(float3 color, float amount)
{
    float luma = GetLuma(color);
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    float boost = amount * (1.0 - sat);
    return lerp(float3(luma, luma, luma), color, 1.0 + boost);
}

float4 main(PSIn input) : SV_TARGET
{
    float2 sourceSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 outputSize = float2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float2 texel = 1.0 / sourceSize;
    float2 uv = input.uv;

    float upscale = max(outputSize.x / sourceSize.x, outputSize.y / sourceSize.y);
    float smoothMix = saturate(0.55 + upscale * 0.12);

    float3 sharp = SampleColor(uv);
    float3 smooth = BicubicBlend(uv, texel);
    float3 color = lerp(sharp, smooth, smoothMix);

    float3 wide =
        SampleColor(uv + float2(texel.x * 3.0, 0.0)) +
        SampleColor(uv - float2(texel.x * 3.0, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y * 3.0)) +
        SampleColor(uv - float2(0.0, texel.y * 3.0)) +
        color;
    wide *= 0.2;
    color = lerp(color, wide, 0.28);

    color = ApplyVibrance(color, 0.55 * saturation);

    float3 graded;
    graded.r = dot(color, float3(1.10, 0.04, 0.03));
    graded.g = dot(color, float3(-0.01, 1.06, 0.04));
    graded.b = dot(color, float3(0.03, 0.05, 1.12));
    color = lerp(color, graded, 0.62);

    float luma = GetLuma(color);
    color += float3(0.015, 0.012, 0.028) * (1.0 - smoothstep(0.0, 0.50, luma));
    color += float3(0.040, 0.022, 0.012) * smoothstep(0.58, 1.0, luma);

    float3 bloom = wide;
    color += max(bloom - 0.42, 0.0) * 0.18;

    float lumaOut = GetLuma(color);
    color = lerp(float3(lumaOut, lumaOut, lumaOut), color, saturation * 1.35);
    color = pow(saturate(color), 0.92);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
