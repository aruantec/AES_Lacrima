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

float3 ApplyDisplayScanlines(float3 color, float pixelY, float strength)
{
    float scan = sin(pixelY * 3.14159265);
    scan = scan * scan;
    return color * (1.0 - scan * strength);
}

float4 main(PSIn input) : SV_TARGET
{
    float2 uv = input.uv;
    float2 texel = 1.0 / float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    float3 color = src.Sample(samp, uv).rgb;
    float3 bloom =
        src.Sample(samp, uv + float2(texel.x, 0.0)).rgb +
        src.Sample(samp, uv - float2(texel.x, 0.0)).rgb +
        src.Sample(samp, uv + float2(0.0, texel.y)).rgb +
        src.Sample(samp, uv - float2(0.0, texel.y)).rgb;
    bloom *= 0.25;
    color += max(bloom - 0.25, 0.0) * 0.35;

    float3 warm;
    warm.r = dot(color, float3(1.05, 0.08, 0.0));
    warm.g = dot(color, float3(0.05, 0.98, 0.02));
    warm.b = dot(color, float3(0.0, 0.04, 0.88));
    color = lerp(color, warm, 0.65);

    color = ApplyDisplayScanlines(color, input.pos.y, 0.14);

    float2 dist = uv - 0.5;
    color *= saturate(1.0 - dot(dist, dist) * 0.45);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 1.05);
    color = pow(saturate(color), 0.94);
    color *= brightness;
    color *= tint.rgb;

    return float4(color, tint.a);
}
