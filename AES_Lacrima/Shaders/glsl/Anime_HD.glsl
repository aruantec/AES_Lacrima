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

vec3 EdgeAwareSmooth(vec2 uv, vec2 texel, float strength)
{
    vec3 center = SampleColor(uv);
    vec3 sum = center;
    float weightSum = 1.0;

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0)
                continue;

            vec2 offset = vec2(float(x), float(y)) * texel;
            vec3 neighbor = SampleColor(uv + offset);
            float diff = dot(abs(neighbor - center), vec3(1.0, 1.0, 1.0));
            float w = exp(-diff * (4.5 + strength));
            sum += neighbor * w;
            weightSum += w;
        }
    }

    return sum / weightSum;
}

vec3 WideSmooth(vec2 uv, vec2 texel)
{
    vec3 sum =
        SampleColor(uv) +
        SampleColor(uv + vec2(texel.x * 2.0, 0.0)) +
        SampleColor(uv - vec2(texel.x * 2.0, 0.0)) +
        SampleColor(uv + vec2(0.0, texel.y * 2.0)) +
        SampleColor(uv - vec2(0.0, texel.y * 2.0));
    return sum * 0.2;
}

vec3 SoftCelColor(vec3 color, float levels)
{
    float luma = max(GetLuma(color), 0.001);
    float x = luma * levels;
    float band = floor(x + 0.0001) / (levels - 1.0);
    float next = min(band + 1.0 / (levels - 1.0), 1.0);
    float blend = smoothstep(0.0, 0.72, fract(x));
    float quantized = mix(band, next, blend);
    return color * (quantized / luma);
}

vec3 ApplyVibrance(vec3 color, float amount)
{
    float luma = GetLuma(color);
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    float boost = amount * (1.0 - sat);
    return mix(vec3(luma, luma, luma), color, 1.0 + boost);
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

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 outputSize = vec2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    vec2 texel = 1.0 / sourceSize;
    vec2 uv = vTex;

    float upscale = max(outputSize.x / sourceSize.x, outputSize.y / sourceSize.y);
    float smoothAmount = clamp((upscale - 1.0) / 2.5, 0.0, 1.0);

    vec3 color = EdgeAwareSmooth(uv, texel, smoothAmount * 6.0);
    vec3 wide = WideSmooth(uv, texel);
    float flatness = 1.0 - clamp(EdgeStrength(uv, texel * 1.1) * 3.5, 0.0, 1.0);
    color = mix(color, wide, flatness * (0.35 + smoothAmount * 0.35));

    color = SoftCelColor(color, 6.0);

    vec3 bloom =
        WideSmooth(uv, texel * 1.5) +
        SampleColor(uv + vec2(texel.x, texel.y)) +
        SampleColor(uv - vec2(texel.x, texel.y));
    bloom *= 0.333;
    vec3 glow = max(bloom - 0.38, 0.0);
    color += glow * (0.22 + smoothAmount * 0.12);

    color = ApplyVibrance(color, 0.42 * saturation);

    vec3 graded;
    graded.r = dot(color, vec3(1.08, 0.05, 0.02));
    graded.g = dot(color, vec3(0.0, 1.04, 0.03));
    graded.b = dot(color, vec3(0.02, 0.06, 1.10));
    color = mix(color, graded, 0.55);

    float luma = GetLuma(color);
    color += vec3(0.018, 0.014, 0.030) * (1.0 - smoothstep(0.0, 0.45, luma));
    color += vec3(0.035, 0.020, 0.010) * smoothstep(0.62, 1.0, luma);

    float edge = EdgeStrength(uv, texel * 1.25);
    float outline = smoothstep(0.08, 0.28, edge);
    color = mix(color, vec3(0.10, 0.11, 0.20), outline * 0.18);

    float lumaOut = GetLuma(color);
    color = mix(vec3(lumaOut, lumaOut, lumaOut), color, saturation * 1.25);
    color = pow(clamp(color, 0.0, 1.0), vec3(0.94));
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
