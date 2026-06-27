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

float3 ApplyVibrance(float3 color, float amount)
{
    float luma = dot(color, float3(0.299, 0.587, 0.114));
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    float boost = amount * (1.0 - sat);
    return lerp(float3(luma, luma, luma), color, 1.0 + boost);
}

float3 SampleColor(float2 uv)
{
    return src.Sample(samp, saturate(uv)).rgb;
}

float4 main(PSIn input) : SV_TARGET
{
    float2 sourceSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 outputSize = float2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float2 sampleSize = float2(min(sourceSize.x, outputSize.x), min(sourceSize.y, outputSize.y));
    float2 texel = 1.0 / sampleSize;
    float2 uv = input.uv;

    float3 center = SampleColor(uv);
    float3 neighbors =
        SampleColor(uv + float2(texel.x, 0.0)) +
        SampleColor(uv - float2(texel.x, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y)) +
        SampleColor(uv - float2(0.0, texel.y));

    float3 sharpened = center * 1.45 - neighbors * 0.1125;
    float3 color = lerp(center, sharpened, 0.55);

    color = ApplyVibrance(color, 0.28 * saturation);

    float3 graded;
    graded.r = dot(color, float3(1.06, 0.04, -0.02));
    graded.g = dot(color, float3(-0.01, 1.03, 0.02));
    graded.b = dot(color, float3(0.02, 0.02, 1.04));
    color = lerp(color, graded, 0.35);

    float shadowLift = smoothstep(0.0, 0.28, dot(color, float3(0.333, 0.333, 0.333)));
    color += float3(0.012, 0.010, 0.008) * (1.0 - shadowLift);

    color = ApplyDisplayScanlines(color, input.pos.y, 0.08);

    float2 dist = uv - 0.5;
    color *= saturate(1.0 - dot(dist, dist) * 0.22);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation);
    color = pow(saturate(color), 0.97);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
