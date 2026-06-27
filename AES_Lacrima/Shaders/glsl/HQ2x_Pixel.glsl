#ifdef VERTEX

layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;
out vec2 vTex;

void main()
{
    vTex = TexCoord;
    gl_Position = vec4(VertexCoord, 0.0, 1.0);
}

#endif

#ifdef FRAGMENT

uniform sampler2D Texture;
uniform float FrameCount;
uniform float FrameDirection;
uniform vec2 TextureSize;
uniform vec2 InputSize;
uniform vec2 OutputSize;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

#define timeSeconds (FrameCount / 60.0)
#define brightness uBrightness
#define saturation uSaturation
#define tint uColorTint
#define sourceWidth TextureSize.x
#define sourceHeight TextureSize.y
#define outputWidth OutputSize.x
#define outputHeight OutputSize.y
#define sourceIsSrgb 1.0

vec3 FetchPixel(ivec2 pos, vec2 sourceSize)
{
    vec2 uv = (vec2(pos) + 0.5) / sourceSize;
    return texture(Texture, clamp(uv, 0.0, 1.0)).rgb;
}

float ColorDiff(vec3 a, vec3 b)
{
    return abs(a.r - b.r) + abs(a.g - b.g) + abs(a.b - b.b);
}

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 coord = vTex * sourceSize;
    ivec2 fp = ivec2(floor(coord));
    vec2 fc = fract(coord);

    vec3 p = FetchPixel(fp, sourceSize);
    vec3 a = FetchPixel(fp + ivec2(-1, -1), sourceSize);
    vec3 b = FetchPixel(fp + ivec2(0, -1), sourceSize);
    vec3 c = FetchPixel(fp + ivec2(1, -1), sourceSize);
    vec3 d = FetchPixel(fp + ivec2(-1, 0), sourceSize);
    vec3 e = FetchPixel(fp + ivec2(1, 0), sourceSize);
    vec3 f = FetchPixel(fp + ivec2(-1, 1), sourceSize);
    vec3 g = FetchPixel(fp + ivec2(0, 1), sourceSize);
    vec3 h = FetchPixel(fp + ivec2(1, 1), sourceSize);

    vec3 color = p;
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

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
