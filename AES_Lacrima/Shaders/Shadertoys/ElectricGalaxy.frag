float seg(in vec2 p, in vec2 a, in vec2 b) {
    vec2 pa = p-a, ba = b-a;
    float h = clamp( dot(pa,ba)/dot(ba,ba), 0.0, 1.0 );
    return length( pa - ba*h );
}

float hash(float n) { return fract(sin(n) * 43758.5453123); }

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n = i.x + i.y * 57.0;
    return mix(mix(hash(n), hash(n + 1.0), f.x),
               mix(hash(n + 57.0), hash(n + 58.0), f.x), f.y);
}

float fbm(vec2 p) {
    float f = 0.0;
    f += 0.5000 * noise(p); p = p * 2.02;
    f += 0.2500 * noise(p); p = p * 2.03;
    f += 0.1250 * noise(p); p = p * 2.01;
    f += 0.0625 * noise(p);
    return f / 0.9375;
}

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

    // Sample the spectrum
    float rawHeight = texture(iChannel0, vec2(normAngle, 0.5)).r;

    // Visually normalize: dampen the overwhelming bass intensity (near normAngle=0)
    // and compress the response to make high frequencies more visible.
    float barHeight = pow(rawHeight, 0.8) * (0.6 + 0.4 * normAngle);
    float bass = texture(iChannel0, vec2(0.05, 0.5)).r;
    
    // --- Electricity Effect ---
    vec3 col = vec3(0.0);
    
    // Create multiple electric arcs
    for (int i = 0; i < 3; i++) {
        float it = float(i);
        float t = iTime * (1.0 + it * 0.2);
        
        // Jittery polar coordinates
        float noiseVal = fbm(vec2(a * 3.0 + it, t));
        float radius = 0.2 + 0.3 * barHeight + 0.1 * noiseVal;
        
        // Distance to the jittery circle arc
        float arcDist = abs(d - radius);
        
        // Electricity "glow" and sharpness
        float intensity = 0.002 / (arcDist + 0.005);
        intensity *= smoothstep(0.4, 0.0, arcDist);
        
        // Flicker based on noise
        float flicker = step(0.5, noise(vec2(t * 10.0, it)));
        col += u_primary.rgb * intensity * (0.5 + 0.5 * flicker) * (barHeight + 0.5);
    }
    
    // --- Electric Spikes ---
    // Random radial spikes of electricity
    float spikes = fbm(vec2(a * 10.0, iTime * 5.0));
    float spikeIntensity = smoothstep(0.7 - barHeight * 0.3, 1.0, spikes);
    col += u_primary.rgb * spikeIntensity * (0.2 / (d + 0.1)) * (barHeight + 0.2);
    
    // --- Central Glow ---
    col += u_primary.rgb * (0.02 / (d + 0.01)) * (bass + 0.2);
    
    // Add some random "sparks"
    float sparks = hash(dot(uv, vec2(12.9898, 78.233)) + iTime);
    if (sparks > 0.99 && d < 0.5 * barHeight + 0.2) {
        col += vec3(1.0) * bass;
    }

    fragColor = vec4(col * u_fade, 1.0);
}