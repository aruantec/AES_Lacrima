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

float4 main(PSIn input) : SV_TARGET
{
    float t = timeSeconds;
    float2 uv = input.uv;
    float2 center = uv - 0.5;
    float edgeGuard = EdgeSampleGuard(uv);

    float breathe = 0.018 * sin(t * 0.7) + 0.012 * sin(t * 1.3 + 1.0);
    float swirl = 0.06 * sin(t * 0.45);
    float c = cos(swirl);
    float s = sin(swirl);
    float2 warped = float2(c * center.x - s * center.y, s * center.x + c * center.y);
    warped *= 1.0 + breathe;
    float2 wuv = lerp(uv, warped + 0.5, edgeGuard);

    float chroma = 0.012 + 0.008 * sin(t * 1.1);
    float3 color;
    color.r = SampleRgb(wuv, float2(chroma * sin(t * 0.9), chroma * cos(t * 0.6))).r;
    color.g = SampleRgb(wuv, float2(chroma * cos(t * 1.2), -chroma * sin(t * 0.8))).g;
    color.b = SampleRgb(wuv, float2(-chroma, chroma * sin(t * 1.4))).b;

    float pulse = 0.5 + 0.5 * sin(t * 1.6);
    float3 tintA = float3(1.2, 0.5, 1.4);
    float3 tintB = float3(0.4, 1.3, 0.9);
    float3 tintC = float3(1.3, 0.9, 0.3);
    color *= lerp(tintA, lerp(tintB, tintC, pulse), 0.5 + 0.5 * sin(t * 0.55 + 2.0));

    float edge = length(center);
    float rainbow = sin(edge * 14.0 - t * 2.5) * 0.5 + 0.5;
    color += float3(rainbow, 1.0 - rainbow, 0.5 + 0.5 * sin(t * 2.0)) * 0.12 * smoothstep(0.65, 0.15, edge) * edgeGuard;

    float glow = dot(SampleRgb(wuv), float3(0.299, 0.587, 0.114));
    color += float3(0.5, 0.2, 0.8) * glow * 0.2 * (0.6 + 0.4 * sin(t * 3.0));

    float soft = dot(SampleRgb(wuv, center * 0.02), float3(0.333, 0.333, 0.333));
    color = lerp(color, color * (1.0 + soft * 0.35), 0.35);

    float2 vigCenter = uv - 0.5;
    color *= saturate(1.0 - dot(vigCenter, vigCenter) * 0.25);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 1.45);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
