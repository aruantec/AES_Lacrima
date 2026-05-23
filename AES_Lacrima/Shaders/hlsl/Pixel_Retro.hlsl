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

float4 main(PSIn input) : SV_TARGET
{
    float2 outputSize = float2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float blocks = min(outputSize.x, outputSize.y) / 3.5;
    blocks = clamp(blocks, 96.0, 420.0);

    float2 blockUv = floor(input.uv * blocks) + 0.5;
    float2 snappedUv = blockUv / blocks;

    float3 color = src.Sample(samp, snappedUv).rgb;

    float row = fmod(blockUv.y, 2.0);
    color *= row < 1.0 ? 1.0 : 0.9;

    float2 cell = frac(input.uv * blocks);
    float grid = smoothstep(0.92, 1.0, max(cell.x, cell.y)) + smoothstep(0.08, 0.0, min(cell.x, cell.y));
    color *= 1.0 - grid * 0.08;

    color = pow(saturate(color), 0.96);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
