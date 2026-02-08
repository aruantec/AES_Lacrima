float rand(vec2 co){ 
    return fract(sin(dot(co.xy ,vec2(12.9898,78.233))) * 43758.5453); 
}

void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
    vec2 uv = fragCoord.xy / iResolution.xy;
    float ratio = iResolution.x / iResolution.y;
    uv.x *= ratio;

    float time = iTime * 0.5;
    float gridSize = 25.0; 
    vec2 gridUV = uv * gridSize;
    vec2 id = floor(gridUV);
    vec2 gv = fract(gridUV) - 0.5;

    // --- Audio Sampling ---
    float spec = texture(iChannel0, vec2(id.x / (gridSize * ratio), 0.25)).r;
    float bass = texture(iChannel0, vec2(0.05, 0.25)).r;

    // --- Balanced Color Logic ---
    float randPulse = sin((iTime + 2.0) * rand(id) * 2.0) * 0.5 + 0.5;
    vec3 colorShift = vec3(rand(id) * 0.1);
    
    // ADJUSTMENT: Lifted the base brightness constant to 0.8 to eliminate dark spots.
    // ADJUSTMENT: Changed the Y-phase shift to a much higher value (2.5) to bypass the dark trough.
    vec3 phaseShift = vec3(id.x * 0.1, id.y * 0.1 + 2.5, id.x * 0.1);
    vec3 baseColor = 0.8 + 0.2 * cos(time + phaseShift + vec3(4, 2, 1) + colorShift);

    // --- Equalizer Ignition ---
    float waveHeight = spec * gridSize * 1.1;
    float isFired = step(id.y, waveHeight);
    
    // Ambient: Uniform visibility for all blocks
    float ambient = 0.25; 
    // Flare: Controlled neon brightness
    float flare = isFired * (0.85 + bass * 0.6);
    
    vec3 color = baseColor * (ambient + flare);

    // --- Optimized Squircle Mask [cite: 2025-12-29] ---
    // Mimics the rounded hardware look from your images [cite: 2025-12-21]
    float dist = dot(gv * gv, gv * gv) * 18.0; 
    float mask = smoothstep(1.0, 0.1, dist);
    
    vec3 finalColor = color * mask;
    
    // Subtle additive bloom for the active equalizer blocks
    finalColor += baseColor * isFired * 0.2 * mask * randPulse;

    // Final output respecting u_fade property [cite: 2025-12-22]
    fragColor = vec4(finalColor * u_fade, 1.0);
}