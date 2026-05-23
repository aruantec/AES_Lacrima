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

void main()
{
    vec3 color = texture(Texture, vTex).rgb;

    vec3 mask;
    float x = gl_FragCoord.x;
    float r = 0.5 + 0.5 * sin(x * 3.14159265 * 0.5);
    float g = 0.5 + 0.5 * sin(x * 3.14159265 * 0.5 + 2.094);
    float b = 0.5 + 0.5 * sin(x * 3.14159265 * 0.5 + 4.189);
    mask = vec3(r, g, b);
    mask = mix(vec3(1.0, 1.0, 1.0), mask, 0.22);
    color *= mask;

    color = ApplyDisplayScanlines(color, gl_FragCoord.y, 0.18);

    vec2 dist = vTex - 0.5;
    color *= clamp(1.0 - dot(dist, dist) * 0.38, 0.0, 1.0);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
