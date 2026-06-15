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

vec3 SrgbToLinear(vec3 c)
{
    vec3 lo = c / 12.92;
    vec3 hi = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(lo, hi, step(vec3(0.04045), c));
}

vec3 LinearToSrgb(vec3 c)
{
    vec3 lo = c * 12.92;
    vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
    return mix(lo, hi, step(vec3(0.0031308), c));
}

vec3 SampleColor(vec2 uv)
{
    vec3 c = texture(Texture, clamp(uv, 0.0, 1.0)).rgb;
    if (sourceIsSrgb > 0.5)
        c = SrgbToLinear(c);
    return c;
}

vec3 AcesTonemap(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    vec3 mapped = (x * (a * x + b)) / (x * (c * x + d) + e);
    return clamp(mapped, 0.0, 1.0);
}

vec3 ApplyVibrance(vec3 color, float amount)
{
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    float boost = amount * (1.0 - sat);
    return mix(vec3(luma, luma, luma), color, 1.0 + boost);
}

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 texel = 1.0 / sourceSize;
    vec2 uv = vTex;

    vec3 center = SampleColor(uv);
    vec3 blur =
        SampleColor(uv + vec2(texel.x, 0.0)) +
        SampleColor(uv - vec2(texel.x, 0.0)) +
        SampleColor(uv + vec2(0.0, texel.y)) +
        SampleColor(uv - vec2(0.0, texel.y));
    blur *= 0.25;

    vec3 highlights = max(center - 0.50, 0.0);
    vec3 glow = max(blur - 0.42, 0.0);
    vec3 color = center + highlights * 0.30 + glow * 0.20;

    color = AcesTonemap(color * 1.04);

    vec3 graded;
    graded.r = dot(color, vec3(1.04, 0.03, -0.01));
    graded.g = dot(color, vec3(-0.01, 1.02, 0.02));
    graded.b = dot(color, vec3(0.01, 0.03, 1.05));
    color = mix(color, graded, 0.48);

    float shadowLift = smoothstep(0.0, 0.30, dot(color, vec3(0.333, 0.333, 0.333)));
    color += vec3(0.010, 0.014, 0.020) * (1.0 - shadowLift);
    color += vec3(0.028, 0.014, 0.0) * smoothstep(0.65, 1.0, dot(color, vec3(0.299, 0.587, 0.114)));

    color = ApplyVibrance(color, 0.35 * saturation);
    color = pow(clamp(color, 0.0, 1.0), vec3(0.97));

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);

    if (sourceIsSrgb > 0.5)
        color = LinearToSrgb(color);

    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
