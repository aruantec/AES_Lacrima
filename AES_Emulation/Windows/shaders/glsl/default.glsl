#if defined(VERTEX)
layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;
out vec2 vTex;
uniform mat4 MVPMatrix;

void main() {
    vTex = TexCoord;
    gl_Position = MVPMatrix * vec4(VertexCoord, 0.0, 1.0);
}
#elif defined(FRAGMENT)
uniform sampler2D Texture;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;
in vec2 vTex;
out vec4 fragColor;

void main() {
    vec4 col = texture(Texture, vTex);
    
    // Apply Brightness
    col.rgb *= uBrightness;
    
    // Apply Saturation (Luminance preserving)
    float gray = dot(col.rgb, vec3(0.299, 0.587, 0.114));
    col.rgb = mix(vec3(gray), col.rgb, uSaturation);
    
    // Apply Tint
    col *= uColorTint;
    
    fragColor = col;
}
#endif
