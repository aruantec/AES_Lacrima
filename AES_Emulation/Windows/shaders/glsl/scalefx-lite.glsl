#version 150

#ifdef VERTEX
layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;
out vec2 vTex;
uniform mat4 MVPMatrix;

void main() {
    vTex = TexCoord;
    gl_Position = MVPMatrix * vec4(VertexCoord, 0.0, 1.0);
}
#endif

#ifdef FRAGMENT
uniform sampler2D Texture;
uniform vec2 TextureSize;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;
in vec2 vTex;
out vec4 fragColor;

// ScaleFX-Lite: Simplified pattern detection for smoothing
void main() {
    vec2 tc = vTex;
    vec2 dx = vec2(1.0 / TextureSize.x, 0.0);
    vec2 dy = vec2(0.0, 1.0 / TextureSize.y);
    
    vec4 c = texture(Texture, tc);
    vec4 l = texture(Texture, tc - dx);
    vec4 r = texture(Texture, tc + dx);
    vec4 u = texture(Texture, tc - dy);
    vec4 d = texture(Texture, tc + dy);
    
    // Check for "staircase" patterns typical of pixel art
    float diff_h = length(l - r);
    float diff_v = length(u - d);
    
    vec4 res = c;
    if (diff_h < 0.1 && diff_v > 0.3) {
        res = mix(c, mix(u, d, 0.5), 0.2); // vertical smoothing
    } else if (diff_v < 0.1 && diff_h > 0.3) {
        res = mix(c, mix(l, r, 0.5), 0.2); // horizontal smoothing
    }
    
    // Apply UI Global Controls
    res.rgb *= uBrightness;
    float gray = dot(res.rgb, vec3(0.299, 0.587, 0.114));
    res.rgb = mix(vec3(gray), res.rgb, uSaturation);
    res.rgb *= uColorTint.rgb;

    fragColor = vec4(res.rgb, uColorTint.a);
}
#endif
