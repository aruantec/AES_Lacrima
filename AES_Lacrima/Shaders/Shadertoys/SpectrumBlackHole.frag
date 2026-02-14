float hash(float n) { return fract(sin(n) * 43758.5453123); }

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec2 res = iResolution.xy;
    vec2 uv = (fragCoord - 0.5 * res) / res.y;
    
    // --- Circular Spectrum Mapping ---
    float a = atan(uv.y, uv.x);
    float d = length(uv);

    // Balanced Mirrored Mapping: ensures symmetry across all quadrants.
    // We shift the mapping by 45 degrees and use a power transform to expand
    // the treble regions visually, making the circular shape more stable.
    float normAngle = pow(abs(cos(a + 0.78539)), 0.7);

    // Sample Spectrum (Frequency data)
    float rawSpec = texture(iChannel0, vec2(normAngle, 0.25)).r;

    // Visually normalize: dampen dominant bass and boost high-end visibility.
    float spec = pow(rawSpec, 0.8) * (0.6 + 0.4 * normAngle);

    // Sample Waveform (Time domain data)
    float wave = texture(iChannel0, vec2(normAngle, 0.75)).r;
    
    // Bass and treble intensity
    float bass = texture(iChannel0, vec2(0.05, 0.25)).r;
    float treble = texture(iChannel0, vec2(0.8, 0.25)).r;

    vec3 col = vec3(0.0);

    // --- Circular Spectrum Bars (Classic Visualizer look) ---
    float numBars = 72.0;
    float barIdx = floor(normAngle * numBars);
    float barPos = (barIdx + 0.5) / numBars;
    
    // Get single freq sample for this bar
    float barFreqRaw = texture(iChannel0, vec2(barPos, 0.25)).r;
    float barFreq = pow(barFreqRaw, 0.8) * (0.6 + 0.4 * barPos);

    // Bar geometry
    float angleInBar = fract(normAngle * numBars);
    float barWidth = 0.75; // Space between bars
    
    float innerRad = 0.2 + bass * 0.05;
    float barLen = barFreq * 0.6;
    
    // Draw the bar
    if (d > innerRad && d < innerRad + barLen && abs(angleInBar - 0.5) * 2.0 < barWidth) {
        float bVal = smoothstep(innerRad + barLen, innerRad, d);
        col += u_primary.rgb * bVal * 1.5;
    }
    
    // Glow at the tips of the bars
    float tipGlow = 0.005 / (abs(d - (innerRad + barLen)) + 0.01);
    col += u_primary.rgb * tipGlow * barFreq * smoothstep(0.5, 0.0, abs(angleInBar - 0.5));

    // --- Inner Pulsing Waveform ---
    float waveRadius = innerRad - 0.05 + (wave - 0.5) * 0.15;
    float wDist = abs(d - waveRadius);
    float wLine = 0.003 / (wDist + 0.005);
    col += u_primary.rgb * wLine * 0.8;

    // --- Central Pulsing Core ---
    float core = 0.01 / (d + 0.01);
    col += u_primary.rgb * core * (1.0 + bass * 2.0);

    // --- Starfield reflecting treble ---
    float stars = hash(dot(uv, vec2(12.9898, 4.1414)));
    if (stars > 0.996) {
        float flare = smoothstep(0.4, 1.0, treble);
        col += vec3(1.0) * stars * (0.2 + flare * 1.5);
    }
    
    // Digital scanline effect
    float scan = sin(uv.y * 150.0 + iTime * 6.0) * 0.05;
    col += u_primary.rgb * scan * (0.5 + 0.5 * bass);

    // Vignette and fade-out
    col *= smoothstep(1.6, 0.4, d);

    fragColor = vec4(col * u_fade, 1.0);
}