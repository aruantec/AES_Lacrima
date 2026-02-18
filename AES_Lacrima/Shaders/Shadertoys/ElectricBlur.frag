
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
    vec2 uvScreen = fragCoord / res;

    // Audio responsiveness (sample from iChannel0 spectrum)
    float bass = texture(iChannel0, vec2(0.05, 0.5)).r;
    float mid = texture(iChannel0, vec2(0.25, 0.5)).r;
    float treble = texture(iChannel0, vec2(0.75, 0.5)).r;

    // Smooth volume based on main spectrum areas
    float vol = (bass + mid * 0.7 + treble * 0.3);

    // Beat factor to make peaks more explosive
    float beat = pow(bass, 1.5);

    vec3 col = vec3(0.0);

    // --- BACKGROUND PLANE (Distant copy with different parallax) ---
    for(int i = 0; i < 5; i++) {
        float fI = float(i);
        float layerTime = iTime * 0.7 - fI * 0.03;

        // Deep parallax: X is scaled and offset differently
        float pX = uv.x * 1.6 + (fI - 2.5) * 0.015 * sin(iTime * 0.2);
        float pUvScreenX = pX * (res.y / res.x) + 0.5;

        float yPath = 0.08 * sin(pX * 1.2 + layerTime * 0.4);
        float jitter = (fbm(vec2(pX * 6.0 + layerTime, layerTime * 1.2)) * 2.0 - 1.0) * 0.05 * (1.0 + bass);

        float currentY = yPath + jitter - 0.15; // Shifted slightly down 
        float dist = abs(uv.y - currentY);

        float colorMix = smoothstep(-0.2, 1.2, pUvScreenX);
        vec3 bgCol = mix(vec3(0.0, 0.05, 0.3), vec3(0.0, 0.3, 0.05), colorMix); 

        float glow = exp(-dist * 30.0) * 0.5;
        col += bgCol * glow * (0.3 + vol * 0.7) * (1.0 - fI * 0.1);
    }

    for(int i = 0; i < 7; i++) {
        float fI = float(i);
        float layerTime = iTime * 0.85 - fI * 0.025;

        // Intermediate parallax
        float pX = uv.x * 1.3 + (fI - 3.5) * 0.022 * sin(iTime * 0.28);
        float pUvScreenX = pX * (res.y / res.x) + 0.5;

        float yPath = 0.09 * sin(pX * 1.6 + layerTime * 0.45);
        float jitter = (fbm(vec2(pX * 8.0 + layerTime, layerTime * 1.8)) * 2.0 - 1.0) * 0.06 * (1.0 + mid);

        float currentY = yPath + jitter - 0.075; 
        float dist = abs(uv.y - currentY);

        float colorMix = smoothstep(0.0, 1.0, pUvScreenX);
        vec3 midCol = mix(vec3(0.0, 0.08, 0.5), vec3(0.0, 0.5, 0.1), colorMix); 

        float glow = exp(-dist * 35.0) * 0.7;
        col += midCol * glow * (0.4 + vol * 1.2) * (1.0 - fI * 0.12);
    }

    // Render multiple layers of electrical arcs with time offsets to simulate motion blur/persistence
    const int numLayers = 10;
    for(int i = 0; i < numLayers; i++) {
        float fI = float(i);
        // Delay each layer slightly in time to simulate persistence
        float layerTime = iTime - fI * 0.02;

        // Parallax shift: deeper layers have different horizontal offsets
        float pX = uv.x + (fI - 5.0) * 0.03 * sin(iTime * 0.35);
        float pUvScreenX = pX * (res.y / res.x) + 0.5;

        // Base horizontal movement pulsing with bass
        float yPath = 0.1 * sin(pX * 2.0 + layerTime * 0.5) * (1.0 + bass * 1.2);

        // Electric jitter (Noise based on x and time) - Increased frequency for jaggedness
        float jitter = (fbm(vec2(pX * 4.0 + layerTime * 3.0, layerTime * 2.5)) * 2.0 - 1.0) * 0.08;
        jitter += (fbm(vec2(pX * 12.0 - layerTime * 4.5, layerTime * 5.0)) * 2.0 - 1.0) * 0.03;
        jitter += (fbm(vec2(pX * 25.0 + layerTime * 8.0, layerTime * 10.0)) * 2.0 - 1.0) * 0.01; 

        // Horizontal color transition (Blue to Green)
        float colorMix = smoothstep(0.1, 0.7, pUvScreenX + jitter * 0.1);
        float greenBoost = smoothstep(0.5, 1.0, colorMix);

        // Make green arcs travel more horizontally ("crossing the line")
        // Increase the wavelength and amplitude of the jitter specifically for the green section
        float greenLongitudinalShift = (fbm(vec2(pX * 0.5 - layerTime * 0.7, layerTime * 1.5)) * 2.0 - 1.0) * 0.12 * greenBoost;
        jitter += greenLongitudinalShift; 

        // Amplify green side jaggedness with wider and wilder spikes
        jitter += (fbm(vec2(pX * 2.5 + layerTime * 3.0, layerTime * 2.0)) * 2.0 - 1.0) * 0.08 * greenBoost;
        jitter *= (1.0 + 0.3 * greenBoost); 

        // Amplify movement by volume
        jitter *= (0.15 + vol * 1.8);

        float currentY = yPath + jitter;
        float dist = abs(uv.y - currentY);

        // Color palette (Pure Blue on left, Pure Neon Green on right)
        vec3 blueBase = vec3(0.0, 0.1, 1.0);
        vec3 greenBase = vec3(0.0, 1.0, 0.0);
        vec3 layerCol = mix(blueBase, greenBase, colorMix);

        // Spark modulation: Breaks the line into "electric" segments
        float spark = fbm(vec2(pX * 15.0 + layerTime * 5.0, currentY * 10.0));
        float sparkIntensity = 0.3 + 1.4 * spark;

        // Electric Arc: Core + Inner Glow + Outer Glow (Sharpened)
        float coreWidth = (0.006 + 0.005 * beat) * (1.0 - 0.4 * greenBoost);
        float core = smoothstep(coreWidth, 0.0, dist); 
        float innerGlow = exp(-dist * (40.0 + 25.0 * greenBoost - 15.0 * beat)) * (0.9 + 0.6 * beat); 
        float outerGlow = exp(-dist * 12.0) * 0.15;

        // Fade out layers for the blur effect
        float layerAlpha = pow(1.0 - (fI / float(numLayers)), 2.0);

        // Final color assembly for the layer
        col += layerCol * (core * (4.0 + 1.5 * greenBoost) + innerGlow + outerGlow) * sparkIntensity * layerAlpha * (0.4 + vol * 2.5);
    }

    // Add secondary "ghost" arcs that flash with high frequencies
    if (treble > 0.6) {
        float ghostJitter = (fbm(vec2(uv.x * 20.0, iTime * 10.0)) * 2.0 - 1.0) * 0.05;
        float ghostDist = abs(uv.y - ghostJitter);
        col += vec3(0.5, 0.8, 1.0) * exp(-ghostDist * 50.0) * (treble - 0.6) * 2.0;
    }

    // Subtle background vignette
    col *= 1.0 - 0.3 * length(uvScreen - 0.5);

    fragColor = vec4(col * u_fade, 1.0);
}
