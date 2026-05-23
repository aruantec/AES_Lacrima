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
    float3 color = src.Sample(samp, input.uv).rgb;

    float3 mask;
    float x = input.pos.x;
    float r = 0.5 + 0.5 * sin(x * 3.14159265 * 0.5);
    float g = 0.5 + 0.5 * sin(x * 3.14159265 * 0.5 + 2.094);
    float b = 0.5 + 0.5 * sin(x * 3.14159265 * 0.5 + 4.189);
    mask = float3(r, g, b);
    mask = lerp(float3(1.0, 1.0, 1.0), mask, 0.22);
    color *= mask;

    color = ApplyDisplayScanlines(color, input.pos.y, 0.18);

    float2 dist = input.uv - 0.5;
    color *= saturate(1.0 - dot(dist, dist) * 0.38);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
