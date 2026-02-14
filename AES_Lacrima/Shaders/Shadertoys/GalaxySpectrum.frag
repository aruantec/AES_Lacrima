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
    float bass = texture(iChannel0, vec2(0.04, 0.5)).r;
    float mid = texture(iChannel0, vec2(0.25, 0.5)).r;
    float treble = texture(iChannel0, vec2(0.75, 0.5)).r;
    
    vec3 col = vec3(0.0);
    
    vec3 bg = vec3(0.02, 0.02, 0.035) + 0.08 * vec3(0.2, 0.3, 0.6) * (1.0 - d);
    float stars = step(0.998, hash(dot(uv * 150.0, vec2(12.9898, 78.233)) + iTime));
    col += bg + stars * vec3(0.6, 0.7, 1.0);

    float baseRadius = 0.28 + 0.08 * bass;
    float ringWidth = 0.02 + 0.08 * barHeight;
    float ring = smoothstep(ringWidth, 0.0, abs(d - baseRadius));
    vec3 ringColor = mix(vec3(0.15, 0.2, 0.5), u_primary.rgb, 0.7 + 0.3 * mid);
    col += ring * ringColor * (0.6 + 1.4 * barHeight);

    float spokes = fbm(vec2(a * 12.0, iTime * 1.5));
    float spikeMask = smoothstep(0.65, 0.95, spokes + treble * 0.4);
    float spike = smoothstep(0.08, 0.0, abs(d - (baseRadius + ringWidth + 0.2 * barHeight))) * spikeMask;
    col += spike * vec3(0.6, 0.8, 1.0) * (0.7 + 1.2 * treble);

    float barCount = 96.0;
    float sector = floor(normAngle * barCount);
    float bin = (sector + 0.5) / barCount;
    float binRaw = texture(iChannel0, vec2(bin, 0.5)).r;
    float binHeight = pow(binRaw, 0.8) * (0.6 + 0.4 * bin);
    float barEdge = abs(fract(normAngle * barCount) - 0.5);
    float barMask = smoothstep(0.48, 0.2, barEdge);
    float barRadius = baseRadius + 0.06 + binHeight * 0.6;
    float bar = smoothstep(0.015, 0.0, abs(d - barRadius)) * barMask;
    vec3 barColor = mix(vec3(0.2, 0.5, 1.0), vec3(0.9, 0.2, 0.8), binHeight);
    col += bar * barColor * (1.0 + binHeight * 2.0);
    
    float wave = fbm(vec2(a * 6.0, iTime * 0.7));
    float waveRadius = 0.12 + 0.06 * bass + 0.04 * wave;
    float waveGlow = smoothstep(0.02, 0.0, abs(d - waveRadius));
    col += waveGlow * vec3(0.8, 0.4, 1.0) * (0.6 + bass * 1.6);

    col += u_primary.rgb * (0.03 / (d + 0.02)) * (0.4 + bass);
    col += 0.2 * u_primary.rgb * exp(-d * 6.0) * (0.3 + mid);

    fragColor = vec4(col * u_fade, 1.0);
}
