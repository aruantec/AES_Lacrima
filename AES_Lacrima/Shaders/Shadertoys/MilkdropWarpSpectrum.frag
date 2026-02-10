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

float sampleSpectrum(float x) {
    float bin = fract(x);
    float prev = texture(iChannel0, vec2(fract(bin - 1.0 / 160.0), 0.5)).r;
    float cur = texture(iChannel0, vec2(bin, 0.5)).r;
    float next = texture(iChannel0, vec2(fract(bin + 1.0 / 160.0), 0.5)).r;
    return (cur + prev + next) / 3.0;
}

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec2 res = iResolution.xy;
    vec2 uv = (fragCoord - 0.5 * res) / res.y;
    vec2 uvScreen = fragCoord / res;
    float dnaAngle = sin(uv.x * 2.2 + iTime * 0.6) * 0.18;
    float dnaCos = cos(dnaAngle);
    float dnaSin = sin(dnaAngle);
    vec2 uvTwist = vec2(uv.x * dnaCos - uv.y * dnaSin, uv.x * dnaSin + uv.y * dnaCos);

    float d = length(uv);
    float a = atan(uv.y, uv.x);
    float normAngle = (a + 3.14159) / (2.0 * 3.14159);

    float bass = texture(iChannel0, vec2(0.05, 0.5)).r;
    float mid = texture(iChannel0, vec2(0.25, 0.5)).r;
    float treble = texture(iChannel0, vec2(0.75, 0.5)).r;

    vec3 col = vec3(0.0);

    float warp = fbm(vec2(a * 3.0 + iTime * 0.3, d * 3.0 - iTime * 0.7));
    float tunnel = smoothstep(1.4, 0.2, d + warp * 0.2);
    col += vec3(0.015, 0.015, 0.035) + tunnel * vec3(0.018, 0.06, 0.09);

    float rayAngle = normAngle + iTime * 0.08 + warp * 0.1;
    float rays = smoothstep(0.06, 0.0, abs(fract(rayAngle * 180.0) - 0.5));
    float rayMask = pow(1.0 - d, 2.2) * (0.5 + treble * 1.5);
    col += vec3(0.06, 0.26, 0.4) * rays * rayMask;

    float streaks = smoothstep(0.2, 0.0, abs(fract(rayAngle * 60.0 + iTime * 0.2) - 0.5));
    col += vec3(0.12, 0.45, 0.7) * streaks * rayMask * (0.25 + bass * 0.8);

    float baseThickness = 0.012 + 0.018 * bass;
    float binHeight1 = sampleSpectrum(uvScreen.x - iTime * 0.05);
    float binHeight2 = sampleSpectrum(uvScreen.x - iTime * 0.08 + 0.12);
    float binHeight3 = sampleSpectrum(uvScreen.x - iTime * 0.11 + 0.24);
    float smoothBias = 0.25 + 0.5 * smoothstep(0.0, 1.0, uvScreen.x);
    binHeight1 = mix(binHeight1, pow(binHeight1, 0.7), smoothBias);
    binHeight2 = mix(binHeight2, pow(binHeight2, 0.7), smoothBias);
    binHeight3 = mix(binHeight3, pow(binHeight3, 0.7), smoothBias);

    float jitter1 = fbm(vec2(uvTwist.x * 18.0 + iTime * 0.8, iTime * 1.8)) * 0.08;
    float surge1 = fbm(vec2(uvTwist.x * 24.0 - iTime * 1.6, iTime * 2.4)) * 0.06;
    float waveOffset1 = (binHeight1 - 0.5) * 0.38 + (jitter1 - 0.035) * (0.45 + treble);
    waveOffset1 += (surge1 - 0.03) * (0.5 + bass);

    float jitter2 = fbm(vec2(uvTwist.x * 16.0 + iTime * 1.1, iTime * 2.0)) * 0.07;
    float surge2 = fbm(vec2(uvTwist.x * 20.0 - iTime * 1.3, iTime * 2.1)) * 0.05;
    float waveOffset2 = (binHeight2 - 0.5) * 0.32 + (jitter2 - 0.03) * (0.35 + treble);
    waveOffset2 += (surge2 - 0.025) * (0.45 + bass);

    float jitter3 = fbm(vec2(uvTwist.x * 22.0 + iTime * 1.4, iTime * 2.2)) * 0.06;
    float surge3 = fbm(vec2(uvTwist.x * 26.0 - iTime * 1.1, iTime * 2.6)) * 0.045;
    float waveOffset3 = (binHeight3 - 0.5) * 0.28 + (jitter3 - 0.028) * (0.3 + treble);
    waveOffset3 += (surge3 - 0.022) * (0.35 + bass);

    float band1 = smoothstep(baseThickness * 1.3, 0.0, abs(uvTwist.y - waveOffset1));
    float bandGlow1 = smoothstep(baseThickness * 5.5, 0.0, abs(uvTwist.y - waveOffset1));
    float bandFuzz1 = smoothstep(baseThickness * 2.0, 0.0, abs(uvTwist.y - waveOffset1 - jitter1 * 0.22));

    float band2 = smoothstep(baseThickness * 1.45, 0.0, abs(uvTwist.y - waveOffset2));
    float bandGlow2 = smoothstep(baseThickness * 4.6, 0.0, abs(uvTwist.y - waveOffset2));
    float bandFuzz2 = smoothstep(baseThickness * 2.1, 0.0, abs(uvTwist.y - waveOffset2 - jitter2 * 0.2));

    float band3 = smoothstep(baseThickness * 1.6, 0.0, abs(uvTwist.y - waveOffset3));
    float bandGlow3 = smoothstep(baseThickness * 4.0, 0.0, abs(uvTwist.y - waveOffset3));
    float bandFuzz3 = smoothstep(baseThickness * 2.2, 0.0, abs(uvTwist.y - waveOffset3 - jitter3 * 0.18));

    float sparks = fbm(vec2(uvTwist.x * 30.0 + iTime * 1.6, uvTwist.y * 6.0 - iTime * 1.1));
    float filament = 0.7 + 0.8 * sparks;

    vec3 waveColor1 = vec3(0.08, 0.78, 0.7);
    vec3 waveColor2 = vec3(0.28, 0.7, 0.2);
    vec3 waveColor3 = vec3(0.75, 0.7, 0.18);
    vec3 glowColor = vec3(0.45, 0.75, 0.85);
    float electric1 = (band1 + 0.7 * bandFuzz1) * filament;
    float electric2 = (band2 + 0.6 * bandFuzz2) * (0.8 + filament * 0.5);
    float electric3 = (band3 + 0.55 * bandFuzz3) * (0.7 + filament * 0.4);
    float depth1 = 1.0;
    float depth2 = 0.75;
    float depth3 = 0.55;
    col += waveColor1 * electric1 * (0.45 + 1.5 * binHeight1) * depth1;
    col += waveColor2 * electric2 * (0.38 + 1.3 * binHeight2) * depth2;
    col += waveColor3 * electric3 * (0.3 + 1.1 * binHeight3) * depth3;
    col += glowColor * (bandGlow1 * depth1 + bandGlow2 * 0.75 * depth2 + bandGlow3 * 0.6 * depth3) * (0.22 + 1.1 * treble);

    float starNoise = hash(uvScreen.x * 240.0 + uvScreen.y * 420.0 + iTime * 0.3);
    float stars = step(0.995, starNoise) * pow(1.0 - d, 1.2);
    col += stars * vec3(0.45, 0.7, 0.8) * (0.2 + mid * 0.7);

    col += u_primary.rgb * (0.025 / (d + 0.05)) * (0.3 + bass * 0.7);

    fragColor = vec4(col * u_fade, 1.0);
}
