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

vec3 BicubicBlend(vec2 uv, vec2 texel)
{
    vec3 sum = vec3(0.0);
    float wsum = 0.0;

    for (int y = -2; y <= 2; y++)
    {
        for (int x = -2; x <= 2; x++)
        {
            vec2 offset = vec2(float(x), float(y)) * texel;
            float dist = length(vec2(float(x), float(y)));
            float w = exp(-dist * dist * 0.55);
            sum += SampleColor(uv + offset) * w;
            wsum += w;
        }
    }

    return sum / wsum;
}

vec3 ApplyVibrance(vec3 color, float amount)
{
    float luma = GetLuma(color);
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    float boost = amount * (1.0 - sat);
    return mix(vec3(luma, luma, luma), color, 1.0 + boost);
}

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 outputSize = vec2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    vec2 texel = 1.0 / sourceSize;
    vec2 uv = vTex;

    float upscale = max(outputSize.x / sourceSize.x, outputSize.y / sourceSize.y);
    float smoothMix = clamp(0.55 + upscale * 0.12, 0.0, 1.0);

    vec3 sharp = SampleColor(uv);
    vec3 smooth = BicubicBlend(uv, texel);
    vec3 color = mix(sharp, smooth, smoothMix);

    vec3 wide =
        SampleColor(uv + vec2(texel.x * 3.0, 0.0)) +
        SampleColor(uv - vec2(texel.x * 3.0, 0.0)) +
        SampleColor(uv + vec2(0.0, texel.y * 3.0)) +
        SampleColor(uv - vec2(0.0, texel.y * 3.0)) +
        color;
    wide *= 0.2;
    color = mix(color, wide, 0.28);

    color = ApplyVibrance(color, 0.55 * saturation);

    vec3 graded;
    graded.r = dot(color, vec3(1.10, 0.04, 0.03));
    graded.g = dot(color, vec3(-0.01, 1.06, 0.04));
    graded.b = dot(color, vec3(0.03, 0.05, 1.12));
    color = mix(color, graded, 0.62);

    float luma = GetLuma(color);
    color += vec3(0.015, 0.012, 0.028) * (1.0 - smoothstep(0.0, 0.50, luma));
    color += vec3(0.040, 0.022, 0.012) * smoothstep(0.58, 1.0, luma);

    vec3 bloom = wide;
    color += max(bloom - 0.42, 0.0) * 0.18;

    float lumaOut = GetLuma(color);
    color = mix(vec3(lumaOut, lumaOut, lumaOut), color, saturation * 1.35);
    color = pow(clamp(color, 0.0, 1.0), vec3(0.92));
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
