precision highp float;

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    // Safety check for resolution
    vec2 res = iResolution.xy + 0.001;
    vec2 uv = fragCoord / res;
    vec2 p = (fragCoord - 0.5 * res) / res.y;
    
    // 1. Audio Sampling
    // Uses the 512-bin smoothed buffer
    float spectrum = texture(iChannel0, vec2(uv.x, 0.5)).r;
    float bass = texture(iChannel0, vec2(0.05, 0.5)).r;
    
    // 2. Scrolling Grid logic from original design
    float perspective = 1.0 / (abs(p.y + 0.8) + 0.01);
    vec2 gridUv = vec2(p.x * perspective, perspective + iTime * 2.0);
    vec2 grid = abs(fract(gridUv - 0.5) - 0.5) / (fwidth(gridUv) + 0.01);
    float lines = 1.0 - min(grid.x, grid.y);
    
    // 3. Colors (Bottom Grid)
    vec3 color = vec3(0.0);
    if (p.y < 0.0) {
        float fade = smoothstep(0.0, -0.8, p.y);
        color = mix(vec3(0.5, 0.0, 1.0), vec3(0.0, 1.0, 1.0), uv.x) * lines * fade;
    }
    
    // 4. Reactive Horizon Math
    float spikes = abs(cos(5.0 * uv.x * 3.14159)); 
    float waveHeight = spectrum * 0.25 * spikes; 
    float distToWave = abs(p.y - waveHeight);
    
    // --- Pronounced Real Neon Red ---
    // Pure electric red: vec3(1.0, 0.0, 0.02)
    // We increase the numerator (0.045) for a much stronger aura
    float redGlow = 0.045 / (distToWave + 0.05);
    vec3 neonRed = vec3(1.0, 0.0, 0.02) * (1.2 + bass * 0.6);
    color += neonRed * redGlow;
    
    // --- Thicker Neon White Core ---
    // Increased numerator and slightly adjusted thickness bias for visibility
    float whiteGlow = 0.012 / (distToWave + 0.02);
    vec3 neonWhite = vec3(1.0, 0.95, 0.95);
    color += neonWhite * whiteGlow * (1.0 + bass * 0.4);

    // Final output using u_fade for smooth UI transitions
    fragColor = vec4(color * u_fade, 1.0);
}