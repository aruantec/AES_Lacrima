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

bool SameColor(vec3 a, vec3 b)
{
    return all(lessThan(abs(a - b), vec3(1.0 / 255.0)));
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
        if (!SameColor(a, b) && !SameColor(a, d))
            color = a;
    }
    else if (top && !left)
    {
        if (!SameColor(c, b) && !SameColor(c, e))
            color = c;
    }
    else if (!top && left)
    {
        if (!SameColor(f, d) && !SameColor(f, g))
            color = f;
    }
    else
    {
        if (!SameColor(h, g) && !SameColor(h, e))
            color = h;
    }

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
