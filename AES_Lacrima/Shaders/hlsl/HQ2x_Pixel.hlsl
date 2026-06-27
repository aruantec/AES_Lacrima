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

float3 FetchPixel(int2 pos, float2 sourceSize)
{
    float2 uv = (float2(pos) + 0.5) / sourceSize;
    return src.Sample(samp, saturate(uv)).rgb;
}

float ColorDiff(float3 a, float3 b)
{
    return abs(a.r - b.r) + abs(a.g - b.g) + abs(a.b - b.b);
}

float4 main(PSIn input) : SV_TARGET
{
    float2 sourceSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 coord = input.uv * sourceSize;
    int2 fp = int2(floor(coord));
    float2 fc = frac(coord);

    float3 p = FetchPixel(fp, sourceSize);
    float3 a = FetchPixel(fp + int2(-1, -1), sourceSize);
    float3 b = FetchPixel(fp + int2(0, -1), sourceSize);
    float3 c = FetchPixel(fp + int2(1, -1), sourceSize);
    float3 d = FetchPixel(fp + int2(-1, 0), sourceSize);
    float3 e = FetchPixel(fp + int2(1, 0), sourceSize);
    float3 f = FetchPixel(fp + int2(-1, 1), sourceSize);
    float3 g = FetchPixel(fp + int2(0, 1), sourceSize);
    float3 h = FetchPixel(fp + int2(1, 1), sourceSize);

    float3 color = p;
    bool top = fc.y < 0.5;
    bool left = fc.x < 0.5;

    if (top && left)
    {
        float d0 = ColorDiff(d, b) + ColorDiff(b, f) + ColorDiff(d, f);
        float d1 = ColorDiff(a, d) + ColorDiff(d, g) + ColorDiff(a, g);
        float d2 = ColorDiff(b, c) + ColorDiff(c, e) + ColorDiff(b, e);
        float d3 = ColorDiff(p, b) + ColorDiff(p, d) + ColorDiff(b, d);

        if (d0 < d1 && d0 < d2 && d0 < d3)
            color = (d + b) * 0.5;
        else if (d1 < d2 && d1 < d3)
            color = d;
        else if (d2 < d3)
            color = b;
    }
    else if (top && !left)
    {
        float d0 = ColorDiff(b, f) + ColorDiff(f, h) + ColorDiff(b, h);
        float d1 = ColorDiff(b, c) + ColorDiff(c, e) + ColorDiff(b, e);
        float d2 = ColorDiff(c, e) + ColorDiff(e, h) + ColorDiff(c, h);
        float d3 = ColorDiff(p, b) + ColorDiff(p, e) + ColorDiff(b, e);

        if (d0 < d1 && d0 < d2 && d0 < d3)
            color = (b + f) * 0.5;
        else if (d1 < d2 && d1 < d3)
            color = b;
        else if (d2 < d3)
            color = e;
    }
    else if (!top && left)
    {
        float d0 = ColorDiff(d, h) + ColorDiff(h, f) + ColorDiff(d, f);
        float d1 = ColorDiff(a, d) + ColorDiff(d, g) + ColorDiff(a, g);
        float d2 = ColorDiff(d, g) + ColorDiff(g, h) + ColorDiff(d, h);
        float d3 = ColorDiff(p, d) + ColorDiff(p, g) + ColorDiff(d, g);

        if (d0 < d1 && d0 < d2 && d0 < d3)
            color = (d + g) * 0.5;
        else if (d1 < d2 && d1 < d3)
            color = d;
        else if (d2 < d3)
            color = g;
    }
    else
    {
        float d0 = ColorDiff(h, f) + ColorDiff(f, b) + ColorDiff(h, b);
        float d1 = ColorDiff(c, e) + ColorDiff(e, h) + ColorDiff(c, h);
        float d2 = ColorDiff(g, h) + ColorDiff(h, e) + ColorDiff(g, e);
        float d3 = ColorDiff(p, e) + ColorDiff(p, g) + ColorDiff(e, g);

        if (d0 < d1 && d0 < d2 && d0 < d3)
            color = (g + e) * 0.5;
        else if (d1 < d2 && d1 < d3)
            color = e;
        else if (d2 < d3)
            color = g;
    }

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
