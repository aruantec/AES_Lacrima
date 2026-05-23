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

    float vignette = smoothstep(0.85, 0.25, dist);
    float scopeMask = smoothstep(1.05, 0.72, dist);

    float luma = dot(src.Sample(samp, uv).rgb, float3(0.299, 0.587, 0.114));
    luma += dot(src.Sample(samp, uv + texel * 0.5).rgb, float3(0.299, 0.587, 0.114)) * 0.15;
    luma = saturate(luma * 1.35 + 0.04);

    float noise = (rand(uv * float2(640.0, 360.0) + t * 37.0) - 0.5) * 0.12;
    float staticBurst = step(0.992, rand(float2(floor(t * 8.0), 0.0))) * 0.25;
    luma += noise + staticBurst;

    float scan = sin(input.pos.y * 3.14159265);
    scan = scan * scan;
    luma *= 1.0 - scan * 0.08;

    float3 nvGreen = float3(0.15, 1.0, 0.22);
    float3 nvDim = float3(0.02, 0.35, 0.06);
    float3 color = lerp(nvDim, nvGreen, pow(luma, 0.82));

    float hotSpot = smoothstep(0.55, 0.95, luma);
    color += float3(0.35, 1.0, 0.4) * hotSpot * 0.45;

    float ring = smoothstep(0.02, 0.0, abs(dist - 0.68)) * 0.35;
    color += nvGreen * ring;

    color *= vignette;
    color *= scopeMask;

    float crosshair = 0.0;
    float2 ch = abs(fromCenter);
    crosshair += smoothstep(0.004, 0.0, ch.x) * step(ch.y, 0.18) * 0.25;
    crosshair += smoothstep(0.004, 0.0, ch.y) * step(ch.x, 0.18) * 0.25;
    color += nvGreen * crosshair * scopeMask;

    float lumaOut = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(lumaOut, lumaOut, lumaOut), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
