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
    float edgeGuard = EdgeSampleGuard(uv);

    float swayX = 0.022 * sin(t * 0.85) + 0.014 * sin(t * 1.7 + 2.0);
    float swayY = 0.018 * cos(t * 0.65 + 1.0) + 0.010 * sin(t * 2.1);
    float roll = 0.025 * sin(t * 0.5);
    float c = cos(roll);
    float s = sin(roll);
    float2 centered = uv - 0.5;
    float2 rocked = float2(c * centered.x - s * centered.y, s * centered.x + c * centered.y);
    float2 baseUv = lerp(uv, rocked + float2(swayX, swayY) + 0.5, edgeGuard);

    float blurAmt = 0.004 + 0.002 * sin(t * 0.9);
    float2 texel = blurAmt / float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    float3 sharp = SampleRgb(baseUv);
    float3 blurred =
        SampleRgb(baseUv, float2(texel.x, 0.0)) +
        SampleRgb(baseUv, float2(-texel.x, 0.0)) +
        SampleRgb(baseUv, float2(0.0, texel.y)) +
        SampleRgb(baseUv, float2(0.0, -texel.y)) +
        SampleRgb(baseUv, texel) +
        SampleRgb(baseUv, -texel);
    blurred /= 6.0;

    float3 color = lerp(sharp, blurred, 0.62);

    float doubleStrength = 0.35 + 0.12 * sin(t * 1.2);
    float2 ghostOffset = float2(0.018 * sin(t * 0.7), 0.012 * cos(t * 1.1)) * doubleStrength;
    float3 ghost = SampleRgb(baseUv, ghostOffset);
    color = lerp(color, ghost, 0.42);

    float2 secondGhost = float2(-0.014 * cos(t * 0.9), 0.016 * sin(t * 0.6)) * doubleStrength;
    color = lerp(color, SampleRgb(baseUv, secondGhost), 0.22);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 0.72);

    float warmShift = 0.04 * sin(t * 0.4);
    color.r += warmShift;
    color.b -= warmShift * 0.6;
    color = saturate(color * float3(1.02, 0.98, 0.94));

    float tunnel = 1.0 - dot(centered, centered) * 0.55;
    color *= saturate(tunnel);

    float wobbleLine = sin(uv.y * 40.0 + t * 3.0) * 0.015 * edgeGuard;
    color *= 1.0 + wobbleLine;

    float nausea = 0.5 + 0.5 * sin(t * 0.35);
    color.g *= 0.96 + nausea * 0.04;
    color.r *= 1.0 + nausea * 0.03;

    float lumaOut = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(lumaOut, lumaOut, lumaOut), color, saturation * 0.75);
    color *= brightness * 0.96;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
