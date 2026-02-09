uniform vec3 u_grad0;
uniform vec3 u_grad1;
uniform vec3 u_grad2;
uniform vec3 u_grad3;
uniform vec3 u_grad4;

vec3 sampleGradient(float t) {
    if (t < 0.25) return mix(u_grad0, u_grad1, t / 0.25);
    if (t < 0.5) return mix(u_grad1, u_grad2, (t - 0.25) / 0.25);
    if (t < 0.75) return mix(u_grad2, u_grad3, (t - 0.5) / 0.25);
    return mix(u_grad3, u_grad4, (t - 0.75) / 0.25);
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
    
    // 3. Rainbow Mapping
    vec3 rainbow = sampleGradient(uv.x);
    
    // 4. Dense Bar Logic
    float barWidth = 0.006; 
    float barSpacing = 0.009;
    float barIndex = floor(uv.x / barSpacing);
    float gridX = mod(uv.x, barSpacing);
    
    float barFreq = texture(iChannel0, vec2(barIndex * barSpacing, 0.5)).r;
    float barHeight = barFreq * 0.75;
    
    float barMask = step(gridX, barWidth);
    float barDist = abs(p.y);
    float barCore = smoothstep(barHeight, barHeight - 0.01, barDist) * barMask;
    
    // Safety added to the denominator (+ 0.01) to prevent infinite values/freeze
    float barGlow = exp(-12.0 * (barDist / (barHeight + 0.01))) * barMask;

    // 5. Composition (removed DNA waves)
    float contribution = barCore + barGlow * 0.6;
    if (contribution <= 0.002)
    {
        discard;
    }
    vec3 col = rainbow * contribution;
    float mask = smoothstep(0.01, 0.03, contribution);
    // Apply global fade and output (transparent where nothing is drawn)
    fragColor = vec4(col * u_fade, mask * u_fade);
}
