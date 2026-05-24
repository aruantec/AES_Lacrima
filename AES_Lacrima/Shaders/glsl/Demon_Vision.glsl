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

float rand(vec2 co)
{
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}

void main()
{
    float t = timeSeconds;
    vec2 uv = vTex;
    vec2 texel = 1.0 / vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    vec2 center = vec2(0.5, 0.5);
    vec2 fromCenter = uv - center;
    float dist = length(fromCenter);

    float shimmer = sin(t * 3.5 + uv.y * 28.0) * 0.0015;
    vec2 sampleUv = uv + vec2(shimmer, shimmer * 0.6);

    float luma = dot(texture(Texture, sampleUv).rgb, vec3(0.299, 0.587, 0.114));
    float lumaL = dot(texture(Texture, sampleUv - vec2(texel.x, 0.0)).rgb, vec3(0.299, 0.587, 0.114));
    float lumaR = dot(texture(Texture, sampleUv + vec2(texel.x, 0.0)).rgb, vec3(0.299, 0.587, 0.114));
    float lumaU = dot(texture(Texture, sampleUv - vec2(0.0, texel.y)).rgb, vec3(0.299, 0.587, 0.114));
    float lumaD = dot(texture(Texture, sampleUv + vec2(0.0, texel.y)).rgb, vec3(0.299, 0.587, 0.114));
    float edge = abs(luma * 4.0 - lumaL - lumaR - lumaU - lumaD);

    float noise = (rand(uv * vec2(640.0, 360.0) + t * 41.0) - 0.5) * 0.1;
    float emberBurst = step(0.991, rand(vec2(floor(t * 6.0), uv.x * 3.0))) * 0.2;
    luma = clamp(pow(luma * 1.3 + 0.03 + noise + emberBurst, 0.88), 0.0, 1.0);

    vec3 hellDark = vec3(0.06, 0.0, 0.01);
    vec3 hellMid = vec3(0.5, 0.03, 0.01);
    vec3 hellHot = vec3(1.0, 0.22, 0.03);
    vec3 hellCore = vec3(1.0, 0.7, 0.15);

    vec3 color = mix(hellDark, hellMid, smoothstep(0.0, 0.42, luma));
    color = mix(color, hellHot, smoothstep(0.32, 0.8, luma));
    color = mix(color, hellCore, smoothstep(0.7, 0.98, luma));

    color += vec3(1.0, 0.12, 0.02) * edge * 2.2 * smoothstep(0.015, 0.22, edge);

    float ember = step(0.987, rand(uv * vec2(900.0, 520.0) + t * 17.0));
    color += vec3(1.0, 0.35, 0.05) * ember * smoothstep(0.9, 0.25, dist) * 0.65;

    float pulse = 0.9 + 0.1 * sin(t * 2.6);
    color *= pulse;

    float vignette = smoothstep(1.05, 0.32, dist * 1.12);
    float sightMask = smoothstep(1.08, 0.68, dist);
    color *= mix(0.2, 1.0, vignette);

    float scan = sin(gl_FragCoord.y * 3.14159265 * 0.5);
    scan = scan * scan;
    color *= 1.0 - scan * 0.07;

    float ring = smoothstep(0.022, 0.0, abs(dist - 0.64)) * 0.38;
    color += vec3(0.95, 0.06, 0.01) * ring * sightMask;

    float sightLines = abs(sin((fromCenter.y + t * 0.5) * 42.0 + fromCenter.x * 18.0));
    sightLines = smoothstep(0.92, 0.98, sightLines) * 0.08 * sightMask;
    color += vec3(0.8, 0.05, 0.01) * sightLines;

    float lumaOut = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(lumaOut, lumaOut, lumaOut), color, saturation * 1.35);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
