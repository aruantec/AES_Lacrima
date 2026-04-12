#version 120
uniform sampler2D uTex;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uTint;
uniform vec2 uSourceSize;
varying vec2 vTex;

vec3 sample_color(vec2 uv)
{
    return texture2D(uTex, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
}

void main()
{
    vec2 texel = 1.0 / max(uSourceSize, vec2(1.0));
    vec3 center = sample_color(vTex);
    vec3 neighbors =
        sample_color(vTex + vec2(texel.x, 0.0)) +
        sample_color(vTex - vec2(texel.x, 0.0)) +
        sample_color(vTex + vec2(0.0, texel.y)) +
        sample_color(vTex - vec2(0.0, texel.y));

    vec3 sharpened = center * 1.55 - neighbors * 0.1375;
    vec3 color = mix(center, sharpened, 0.85);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation);
    color *= uBrightness;
    color *= uTint.rgb;

    gl_FragColor = vec4(color, uTint.a);
}
