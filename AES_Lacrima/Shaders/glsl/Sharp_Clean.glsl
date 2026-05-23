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

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 outputSize = vec2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    vec2 sampleSize = vec2(min(sourceSize.x, outputSize.x), min(sourceSize.y, outputSize.y));
    vec2 texel = 1.0 / sampleSize;
    vec2 uv = vTex;

    vec3 center = SampleColor(uv);
    vec3 neighbors =
        SampleColor(uv + vec2(texel.x, 0.0)) +
        SampleColor(uv - vec2(texel.x, 0.0)) +
        SampleColor(uv + vec2(0.0, texel.y)) +
        SampleColor(uv - vec2(0.0, texel.y));

    vec3 sharpened = center * 1.55 - neighbors * 0.1375;
    vec3 color = mix(center, sharpened, 0.85);
    color = clamp(color, 0.0, 1.0);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;
    color = clamp(color, 0.0, 1.0);

    fragColor = vec4(color, tint.a);
}

#endif
