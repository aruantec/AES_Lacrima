float seg(in vec2 p, in vec2 a, in vec2 b) {
    vec2 pa = p-a, ba = b-a;
    float h = clamp( dot(pa,ba)/dot(ba,ba), 0.0, 1.0 );
    return length( pa - ba*h );
}

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec2 res = iResolution.xy;
    vec2 uv = (fragCoord - 0.5 * res) / res.y;
    
    // --- Circular Spectrum Mapping ---
    float a = atan(uv.y, uv.x);
    // Normalize angle to 0.0 - 1.0 for texture sampling
    float normAngle = (a + 3.14159) / (2.0 * 3.14159);
    // Sample the spectrum at this specific angle
    float barHeight = texture(iChannel0, vec2(normAngle, 0.5)).r;
    float bass = texture(iChannel0, vec2(0.05, 0.5)).r;
    
    // --- Reactive Movement ---
    // The center 'p' pulses with the bass
    float pPulse = 1.0 + (bass * 0.3);
    vec2 p = pPulse * cos(a + iTime) * vec2(cos(0.5 * iTime), sin(0.3 * iTime));
    
    // Safety epsilons to prevent log(0) crashes
    float d1 = length(uv - p) + 0.001;
    float d2 = length(uv) + 0.001;
    
    // Web distortion
    float logDist = log(d2) * 0.25 - 0.5 * iTime;
    vec2 uv2 = 2. * cos(logDist + log(vec2(d1, d2) / (d1 + d2)));
    
    vec3 col = vec3(0.0);
    float c = cos(10. * length(uv2) + 4. * iTime);
    
    // --- Bar Behavior for Rays ---
    // We treat the rays like physical bars by using the barHeight 
    // to control the threshold of the exponential "spike"
    float rayPattern = abs(cos(9. * a + iTime) * uv.x + sin(9. * a + iTime) * uv.y);
    
    // By subtracting (barHeight * 0.6) from the distance check, 
    // the rays physically "grow" outward as the volume increases.
    float intensity = exp(-8.0 * (rayPattern + 0.1 * c - (barHeight * 0.6)));
    
    // --- Coloring ---
    // Use u_primary and add a glow based on bass intensity
    vec3 baseColor = u_primary.rgb * (1.0 + barHeight);
    col += (0.5 + 0.5 * c) * baseColor * intensity;
    
    // Add a central pulse glow
    col += (bass * 0.1) * u_primary.rgb / d2;

    fragColor = vec4(col * u_fade, 1.0);
}