#version 150

#ifdef VERTEX

layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;

out vec2 vTex;
uniform mat4 MVPMatrix;

void main()
{
    vTex = TexCoord;
    gl_Position = MVPMatrix * vec4(VertexCoord, 0.0, 1.0);
}

#endif

#ifdef FRAGMENT

uniform sampler2D Texture;
uniform vec2 TextureSize;
uniform vec2 InputSize;
uniform vec2 OutputSize;
uniform float FrameCount;
uniform float FrameDirection;

// UI Controls
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

void main()
{
    // Number of blocks across the widest dimension (adjust for stronger/weaker pixelation)
    float PIXEL_BLOCKS = 260.0; // higher = smaller pixels

    // Compute which block this fragment falls into in UV space
    vec2 block = floor(vTex * PIXEL_BLOCKS);
    vec2 block_center_uv = (block + 0.5) / PIXEL_BLOCKS;

    // Convert UV center to integer texel coordinates and sample with texelFetch for a hard pixel look
    ivec2 texelCoord = ivec2(floor(block_center_uv * TextureSize + 0.5));
    // clamp to texture bounds
    texelCoord = clamp(texelCoord, ivec2(0), ivec2(max(1, int(TextureSize.x) - 1), max(1, int(TextureSize.y) - 1)));
    vec3 color = texelFetch(Texture, texelCoord, 0).rgb;

    // Add simple alternating scanline dimming for a retro feel
    float is_even = mod(block.y, 2.0);
    float scanline = is_even < 1.0 ? 1.0 : 0.9;
    color *= scanline;

    // Slight contrast tweak
    color = pow(color, vec3(0.96));

    // Apply UI Global Controls
    color *= uBrightness;
    
    // Saturation
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation);
    
    // Tint
    color *= uColorTint.rgb;

    fragColor = vec4(color, uColorTint.a);
}

#endif
