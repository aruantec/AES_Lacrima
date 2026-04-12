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
    vec3 blur =
        sample_color(vTex + vec2(texel.x, 0.0)) +
        sample_color(vTex - vec2(texel.x, 0.0)) +
        sample_color(vTex + vec2(0.0, texel.y)) +
        sample_color(vTex - vec2(0.0, texel.y));
    blur *= 0.25;

    vec3 bloom = max(center - 0.55, vec3(0.0)) * 1.8 + max(blur - 0.45, vec3(0.0)) * 1.1;
    vec3 color = center + bloom * 0.35;
    color = color / (1.0 + color);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation * 1.18);
    color *= uBrightness;
    color *= uTint.rgb;

    gl_FragColor = vec4(color, uTint.a);
}
