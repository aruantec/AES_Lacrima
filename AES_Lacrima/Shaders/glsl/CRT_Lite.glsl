#version 120
uniform sampler2D uTex;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uTint;
varying vec2 vTex;

void main()
{
    vec2 uv = vTex;
    vec4 c = texture2D(uTex, uv);

    float scan = 0.93 + 0.07 * sin(uv.y * 900.0);
    c.rgb *= scan;

    float gray = dot(c.rgb, vec3(0.299, 0.587, 0.114));
    c.rgb = mix(vec3(gray), c.rgb, max(uSaturation, 0.0));
    c.rgb *= max(uBrightness, 0.0);
    c *= uTint;

    gl_FragColor = c;
}
