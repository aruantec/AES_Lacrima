#version 120
uniform sampler2D uTex;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uTint;
uniform vec2 uSourceSize;
varying vec2 vTex;

vec2 crt(vec2 uv)
{
    uv = (uv - 0.5) * 2.0;
    uv *= 1.1;
    uv.x *= 1.0 + pow(abs(uv.y) / 5.0, 2.0);
    uv.y *= 1.0 + pow(abs(uv.x) / 4.0, 2.0);
    uv = (uv / 2.0) + 0.5;
    uv = uv * 0.92 + 0.04;
    return clamp(uv, vec2(0.0), vec2(1.0));
}

void main()
{
    vec2 textureSize = max(uSourceSize, vec2(1.0));
    vec2 q = vTex;
    vec2 uv = crt(q);

    float chroma = 0.0025;
    vec3 color;
    color.r = texture2D(uTex, clamp(uv + vec2(chroma, 0.0), vec2(0.0), vec2(1.0))).r;
    color.g = texture2D(uTex, clamp(uv, vec2(0.0), vec2(1.0))).g;
    color.b = texture2D(uTex, clamp(uv - vec2(chroma, 0.0), vec2(0.0), vec2(1.0))).b;

    float scanWave = sin(uv.y * textureSize.y * 6.28318);
    scanWave *= scanWave;
    color *= 1.0 - (0.5 * scanWave * 0.8);

    vec2 centerDist = q - 0.5;
    float vignette = 1.0 - dot(centerDist, centerDist) * 0.5;
    color *= clamp(vignette, 0.0, 1.0);

    vec2 p = -1.0 + 2.0 * q;
    float f = (1.0 - p.x * p.x) * (1.0 - p.y * p.y);
    float frame = clamp(1.10 * (pow(f, 0.35) - 0.30), 0.0, 1.0);
    color *= frame;

    color *= uBrightness;
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation);
    color *= uTint.rgb;

    gl_FragColor = vec4(color, uTint.a);
}
