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

float3 AcesTonemap(float3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

float3 ApplyVibrance(float3 color, float amount)
{
    float luma = dot(color, float3(0.299, 0.587, 0.114));
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    float boost = amount * (1.0 - sat);
    return lerp(float3(luma, luma, luma), color, 1.0 + boost);
}

float4 main(PSIn input) : SV_TARGET
{
    float2 sourceSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 texel = 1.0 / sourceSize;
    float2 uv = input.uv;

    float3 center = SampleColor(uv);
    float3 blur =
        SampleColor(uv + float2(texel.x, 0.0)) +
        SampleColor(uv - float2(texel.x, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y)) +
        SampleColor(uv - float2(0.0, texel.y));
    blur *= 0.25;

    float3 highlights = max(center - 0.38, 0.0);
    float3 glow = max(blur - 0.30, 0.0);
    float3 color = center + highlights * 0.85 + glow * 0.55;

    color = AcesTonemap(color * 1.12);

    float3 graded;
    graded.r = dot(color, float3(1.08, 0.06, -0.02));
    graded.g = dot(color, float3(-0.02, 1.04, 0.04));
    graded.b = dot(color, float3(0.02, 0.06, 1.10));
    color = lerp(color, graded, 0.72);

    float shadowLift = smoothstep(0.0, 0.35, dot(color, float3(0.333, 0.333, 0.333)));
    color += float3(0.02, 0.04, 0.06) * (1.0 - shadowLift);
    color += float3(0.06, 0.03, 0.0) * smoothstep(0.55, 1.0, dot(color, float3(0.299, 0.587, 0.114)));

    color = ApplyVibrance(color, 0.55 * saturation);
    color = pow(saturate(color), 0.92);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 1.18);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
