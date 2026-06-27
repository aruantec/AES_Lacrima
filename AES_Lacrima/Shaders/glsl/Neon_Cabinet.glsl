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

vec3 ApplyDisplayScanlines(vec3 color, float pixelY, float strength)
{
    float scan = sin(pixelY * 3.14159265);
    scan = scan * scan;
    return color * (1.0 - scan * strength);
}

vec3 SampleColor(vec2 uv)
{
    return texture(Texture, clamp(uv, 0.0, 1.0)).rgb;
}

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 texel = 1.0 / sourceSize;
    vec2 uv = vTex;
    float chroma = 2.0 / sourceSize.x;

    vec3 color;
    color.r = SampleColor(uv + vec2(chroma, 0.0)).r;
    color.g = SampleColor(uv).g;
    color.b = SampleColor(uv - vec2(chroma, 0.0)).b;

    vec3 blur =
        SampleColor(uv + vec2(texel.x, 0.0)) +
        SampleColor(uv - vec2(texel.x, 0.0)) +
        SampleColor(uv + vec2(0.0, texel.y)) +
        SampleColor(uv - vec2(0.0, texel.y));
    blur *= 0.25;

    vec3 highlights = max(color - 0.45, 0.0);
    vec3 glow = max(blur - 0.38, 0.0);
    color += highlights * 0.35 + glow * 0.28;

    float slot = 0.90 + 0.10 * sin(gl_FragCoord.x * 3.14159265);
    color *= slot;

    color = ApplyDisplayScanlines(color, gl_FragCoord.y, 0.26);

    vec2 dist = uv - 0.5;
    float vignette = clamp(1.0 - dot(dist, dist) * 0.55, 0.0, 1.0);
    color *= vignette;

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 1.15);
    color = pow(clamp(color, 0.0, 1.0), vec3(0.94));
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
