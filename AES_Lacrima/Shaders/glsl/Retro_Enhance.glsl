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
    vec2 outputSize = vec2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    vec2 sampleSize = vec2(min(sourceSize.x, outputSize.x), min(sourceSize.y, outputSize.y));
    vec2 texel = 1.0 / sampleSize;
    vec2 uv = vTex;

    vec3 center = texture(Texture, clamp(uv, 0.0, 1.0)).rgb;
    vec3 neighbors =
        texture(Texture, clamp(uv + vec2(texel.x, 0.0), 0.0, 1.0)).rgb +
        texture(Texture, clamp(uv - vec2(texel.x, 0.0), 0.0, 1.0)).rgb +
        texture(Texture, clamp(uv + vec2(0.0, texel.y), 0.0, 1.0)).rgb +
        texture(Texture, clamp(uv - vec2(0.0, texel.y), 0.0, 1.0)).rgb;

    vec3 sharpened = center * 1.45 - neighbors * 0.1125;
    vec3 color = mix(center, sharpened, 0.55);

    color = ApplyVibrance(color, 0.28 * saturation);

    vec3 graded;
    graded.r = dot(color, vec3(1.06, 0.04, -0.02));
    graded.g = dot(color, vec3(-0.01, 1.03, 0.02));
    graded.b = dot(color, vec3(0.02, 0.02, 1.04));
    color = mix(color, graded, 0.35);

    float shadowLift = smoothstep(0.0, 0.28, dot(color, vec3(0.333, 0.333, 0.333)));
    color += vec3(0.012, 0.010, 0.008) * (1.0 - shadowLift);

    color = ApplyDisplayScanlines(color, gl_FragCoord.y, 0.08);

    vec2 dist = uv - 0.5;
    color *= clamp(1.0 - dot(dist, dist) * 0.22, 0.0, 1.0);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color = pow(clamp(color, 0.0, 1.0), vec3(0.97));
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
