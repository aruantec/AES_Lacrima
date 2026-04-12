#version 120
uniform sampler2D uTex;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uTint;
uniform vec2 uSourceSize;
varying vec2 vTex;

void main()
{
    vec2 texSize = max(uSourceSize, vec2(1.0));
    vec3 color;
    color.r = texture2D(uTex, clamp(vTex + vec2(0.0015, 0.0), vec2(0.0), vec2(1.0))).r;
    color.g = texture2D(uTex, clamp(vTex, vec2(0.0), vec2(1.0))).g;
    color.b = texture2D(uTex, clamp(vTex - vec2(0.0015, 0.0), vec2(0.0), vec2(1.0))).b;

    float scan = sin(vTex.y * texSize.y * 6.28318);
    scan *= scan;
    color *= 1.0 - scan * 0.28;

    float slot = 0.94 + 0.06 * sin(vTex.x * texSize.x * 3.14159);
    color *= slot;

    vec2 dist = vTex - 0.5;
    float vignette = clamp(1.0 - dot(dist, dist) * 0.35, 0.0, 1.0);
    color *= vignette;

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation * 1.08);
    color *= uBrightness;
    color *= uTint.rgb;

    gl_FragColor = vec4(color, uTint.a);
}
