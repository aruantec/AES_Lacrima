// simple HSV-to-RGB helper used for rainbow gradient
vec3 hsv2rgb(vec3 c){
    vec4 K = vec4(1.0, 2.0/3.0, 1.0/3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

// simple fallback rainbow gradient that doesn't rely on host-provided uniforms
vec3 sampleGradient(float t) {
    // use full hue cycle
    return hsv2rgb(vec3(fract(t), 1.0, 1.0));
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    // 1. Safety setup for resolution
    vec2 res = iResolution.xy + 0.1;
    vec2 uv = fragCoord / res;
    vec2 rawP = (fragCoord - 0.5 * res) / res.y;
    float verticalMargin = 0.16;
    float verticalScale = 1.0 - verticalMargin * 2.0;
    vec2 p = vec2(rawP.x, clamp(rawP.y, -verticalScale, verticalScale) / verticalScale);

    // 2. Audio Sampling
    float spectrum = texture(iChannel0, vec2(uv.x, 0.5)).r;
    float bass = texture(iChannel0, vec2(0.05, 0.5)).r;
    
    // 3. Rainbow Mapping (simple, because gradient uniforms unavailable)
    vec3 rainbow = sampleGradient(uv.x);
    
    // 4. Symmetrical pulsing bars with waveform centre
    float barWidth = 0.002;   // even thinner
    float barSpacing = 0.008;  // fill width with more bars
    // determine distance from center (0.5) and mirror
    float x = uv.x - 0.5;
    float mx = abs(x);
    float barIndex = floor(mx / barSpacing);
    float gridX = mod(mx, barSpacing);

    float barFreq = texture(iChannel0, vec2(barIndex * barSpacing, 0.5)).r;
    float barHeight = barFreq * 0.8;

    float barMask = step(gridX, barWidth);
    float barDist = abs(p.y);
    // fade to transparent near top and bottom of each bar
    float fadeRange = max(0.05, barHeight * 0.25);
    float edgeFade = smoothstep(barHeight, barHeight - fadeRange, barDist);
    float barCore = edgeFade * barMask;
    float barGlow = exp(-10.0 * (barDist / (barHeight + 0.01))) * barMask;


    // combine
    float contribution = barCore + barGlow * 0.5;
    if (contribution <= 0.005)
    {
        discard;
    }
    vec3 col = rainbow * (barCore + barGlow * 0.5);
    float mask = smoothstep(0.001, 0.01, contribution);

    // Apply global fade and output (transparent where nothing is drawn)
    fragColor = vec4(col * u_fade, mask * u_fade);
}
