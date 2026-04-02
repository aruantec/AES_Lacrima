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
uniform vec2 InputSize;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;
in vec2 vTex;
out vec4 fragColor;

void main() {
    vec2 pos = vTex;
    vec2 res = InputSize;
    
    vec2 pixel_pos = pos * res;
    vec2 f = fract(pixel_pos);
    
    // Sample
    vec3 color = texture(Texture, pos).rgb;
    
    // Scaline effect
    float scanline = 0.5 + 0.5 * sin(3.1415 * (pixel_pos.y * 2.0 + 0.5));
    scanline = pow(scanline, 1.5);
    color *= mix(0.7, 1.0, scanline);
    
    // Horizontal sharpness
    float edge = smoothstep(0.4, 0.6, f.x);
    // (Simplified Hyllian logic)
    
    // Apply UI Global Controls
    color *= uBrightness;
    float gray = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(gray), color, uSaturation);
    color *= uColorTint.rgb;

    fragColor = vec4(color, uColorTint.a);
}
#endif
