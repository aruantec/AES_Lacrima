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

vec3 SampleColor(vec2 uv)
{
    return texture(Texture, clamp(uv, 0.0, 1.0)).rgb;
}

float GetLuma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

vec3 SmoothSample(vec2 uv, vec2 texel)
{
    vec3 sum =
        SampleColor(uv) +
        SampleColor(uv + vec2(texel.x, 0.0)) +
        SampleColor(uv - vec2(texel.x, 0.0)) +
        SampleColor(uv + vec2(0.0, texel.y)) +
        SampleColor(uv - vec2(0.0, texel.y));
    return sum * 0.2;
}

float EdgeStrength(vec2 uv, vec2 texel)
{
    float c = GetLuma(SampleColor(uv));
    float l = GetLuma(SampleColor(uv - vec2(texel.x, 0.0)));
    float r = GetLuma(SampleColor(uv + vec2(texel.x, 0.0)));
    float u = GetLuma(SampleColor(uv - vec2(0.0, texel.y)));
    float d = GetLuma(SampleColor(uv + vec2(0.0, texel.y)));
    return abs(c * 4.0 - l - r - u - d);
}

vec3 SoftToonColor(vec3 color, float levels)
{
    float luma = max(GetLuma(color), 0.001);
    float x = luma * levels;
    float band = floor(x + 0.0001) / (levels - 1.0);
    float next = min(band + 1.0 / (levels - 1.0), 1.0);
    float blend = smoothstep(0.0, 0.65, fract(x));
    float quantized = mix(band, next, blend);
    return color * (quantized / luma);
}

void main()
{
    vec2 texel = 1.0 / vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 uv = vTex;

    vec3 color = SmoothSample(uv, texel * 0.75);
    color = SoftToonColor(color, 5.0);

    float edge = EdgeStrength(uv, texel * 1.25);
    float outline = smoothstep(0.1, 0.32, edge);
    vec3 ink = vec3(0.08, 0.1, 0.18);
    color = mix(color, ink, outline * 0.28);

    float lumaOut = GetLuma(color);
    color = mix(vec3(lumaOut, lumaOut, lumaOut), color, saturation * 1.35);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
