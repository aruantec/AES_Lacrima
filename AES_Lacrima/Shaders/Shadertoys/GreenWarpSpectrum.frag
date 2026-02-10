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

    float d = length(uv);
    float a = atan(uv.y, uv.x);
    float normAngle = (a + 3.14159) / (2.0 * 3.14159);

    float bass = texture(iChannel0, vec2(0.05, 0.5)).r;
    float mid = texture(iChannel0, vec2(0.25, 0.5)).r;
    float treble = texture(iChannel0, vec2(0.75, 0.5)).r;

    vec3 col = vec3(0.0);

    float beat = smoothstep(0.1, 0.9, bass + mid * 0.6);
    float glow = exp(-d * (1.9 + beat * 0.6));
    vec3 bgColor = mix(vec3(0.01, 0.04, 0.02), vec3(0.03, 0.12, 0.07), beat);
    col += bgColor + vec3(0.02, 0.08, 0.05) * glow * (0.6 + beat);

    float rotAngle = normAngle + iTime * 0.05;
    float streaks = smoothstep(0.1, 0.0, abs(fract(rotAngle * 120.0) - 0.5));
    float streakMask = pow(1.0 - d, 2.0) * (0.4 + treble * 1.4);
    col += vec3(0.05, 0.45, 0.25) * streaks * streakMask;

    float waveBin = floor(uvScreen.x * 128.0);
    float bin = (waveBin + 0.5) / 128.0;
    float binHeight = texture(iChannel0, vec2(bin, 0.5)).r;
    float waveOffset = (binHeight - 0.5) * 0.4;
    float zap = fbm(vec2(uv.x * 20.0 + iTime * 0.6, iTime * 2.4)) * 0.08;
    waveOffset += (zap - 0.04) * (0.4 + treble * 0.8);

    float baseThickness = 0.012 + 0.02 * bass;
    float jitterA = fbm(vec2(uv.x * 10.0 + iTime * 1.4, iTime * 2.2)) * 0.04;
    float jitterB = fbm(vec2(uv.x * 14.0 - iTime * 1.1, iTime * 1.8)) * 0.035;
    float band = smoothstep(baseThickness, 0.0, abs(uv.y - waveOffset));
    float ripple = fbm(vec2(uv.x * 6.0, iTime * 1.2)) * 0.03;
    float band2 = smoothstep(baseThickness * 1.4, 0.0, abs(uv.y - waveOffset - ripple));
    float band3 = smoothstep(baseThickness * 1.1, 0.0, abs(uv.y - (waveOffset + jitterA)));
    float band4 = smoothstep(baseThickness * 1.1, 0.0, abs(uv.y - (waveOffset - jitterB)));
    float blur = smoothstep(baseThickness * 3.6, 0.0, abs(uv.y - waveOffset));

    float electricNoise = fbm(vec2(uv.x * 18.0 + iTime * 1.8, uv.y * 6.0 - iTime * 0.6));
    float electricFlow = fbm(vec2(uv.x * 28.0 - iTime * 2.4, iTime * 1.5));
    float wire = 0.35 + 1.4 * electricNoise + 0.9 * electricFlow;

    vec3 waveColor = vec3(0.1, 0.9, 0.6);
    vec3 arcColor = vec3(0.45, 1.0, 0.85);
    float electric = (band + 0.6 * band2 + 0.8 * band3 + 0.7 * band4) * wire;
    col += waveColor * electric * (0.5 + 1.8 * binHeight);
    col += arcColor * blur * (0.2 + 1.4 * treble);

    float sparkle = step(0.995, hash(uvScreen.x * 160.0 + uvScreen.y * 240.0 + iTime));
    col += sparkle * vec3(0.4, 1.0, 0.8) * (0.2 + treble * 1.2);

    col += u_primary.rgb * (0.04 / (d + 0.05)) * (0.4 + bass);

    fragColor = vec4(col * u_fade, 1.0);
}
