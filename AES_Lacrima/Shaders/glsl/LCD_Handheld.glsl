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
    vec2 pixel = vTex * texSize;
    vec2 cell = fract(pixel);

    vec3 color = texture2D(uTex, clamp(vTex, vec2(0.0), vec2(1.0))).rgb;
    float rowMask = mix(0.88, 1.04, smoothstep(0.10, 0.55, sin(cell.y * 3.14159)));
    float columnMask = 0.92 + 0.08 * sin(cell.x * 3.14159 * 3.0);
    color *= rowMask * columnMask;

    color *= vec3(0.90, 1.02, 0.86);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation * 0.92);
    color *= uBrightness;
    color *= uTint.rgb;

    gl_FragColor = vec4(color, uTint.a);
}
