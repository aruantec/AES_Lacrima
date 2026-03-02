// Circular Spectrum Visualizer
// Shadertoy-compatible fragment shader
// Channels expected:
// iChannel0 - audio buffer (Shadertoy 'Audio' or a small texture with FFT data)

uniform vec3      iResolution;
uniform float     iTime;
uniform sampler2D iChannel0; // audio texture (use Shadertoy audio channel)

// simple pseudo-random noise based on coords
float rand(vec2 co){return fract(sin(dot(co.xy,vec2(12.9898,78.233))) * 43758.5453);}

// Convert HSV to RGB
vec3 hsv2rgb(vec3 c){
    vec4 K = vec4(1.0, 2.0/3.0, 1.0/3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

// Sample a simple audio spectrum from iChannel0.
// Shadertoy audio channel exposes frequency bins horizontally.
float sampleSpectrum(float x){
    // clamp coordinates; audio texture often has height 1 or small
    return texture(iChannel0, vec2(clamp(x,0.0,1.0), 0.125)).r;
}

// simplified neighbor color calculation (lighter-weight than full path)
// defined at top-level (GLSL doesn't allow nested function definitions)
vec3 sampleNeighbor(vec2 uvn, float time, float pulse, float bass, float highs) {
    float rn = length(uvn);
    float an = atan(uvn.y, uvn.x);
    float freqN = pow(rn, 0.6);
    float specN = sampleSpectrum(freqN);
    float ringFreqN = 30.0 + 50.0 * specN;
    // note: we intentionally avoid expensive trig/loops here
    float ringMaskN = smoothstep(0.45, 0.0, abs(fract(rn * ringFreqN * 0.5) - 0.5));
    float vignN = smoothstep(0.95, 0.2, rn);
    float spokesN = pow(cos(an * (6.0 + 6.0 * highs) + time * 3.0) * 0.5 + 0.5, 2.0);
    float intenN = clamp(ringMaskN * 0.45 + pulse * 0.6 + spokesN * 0.12, 0.0, 2.0);
    vec3 baseN = hsv2rgb(vec3(0.08 + specN * 0.05, 0.85, 0.75));
    vec3 c = baseN * (intenN * vignN);
    c += baseN * pow(max(0.0, 1.0 - rn*2.0), 4.0) * (0.25 + bass*0.8);
    return c;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord){
    vec2 uv = (fragCoord.xy - 0.5 * iResolution.xy) / iResolution.y;
    float time = iTime;

    // Quick safety fallback: if audio channel is empty (no spectrum provided)
    // render a simple animated gradient to avoid heavy audio-driven code
    // causing a blank/frozen frame on some hosts.
    float audioProbe = texture(iChannel0, vec2(0.5, 0.125)).r;
    if (audioProbe < 0.0001) {
        // simple safe visual: rotating warm gradient
        float ang = atan(uv.y, uv.x) + time * 0.3;
        float r = length(uv);
        vec3 col = vec3(0.9, 0.45, 0.05) * (0.6 + 0.4 * sin(ang * 3.0 + time));
        col *= smoothstep(0.9, 0.0, r);
        fragColor = vec4(pow(col, vec3(0.4545)), 1.0);
        return;
    }

    // add swirling distortion to UV
    float swirlAmt = 0.3 + 0.7 * sampleSpectrum(0.2);
    float theta = swirlAmt * sin(iTime * 0.5 + length(uv) * 3.0);
    float cs = cos(theta), sn = sin(theta);
    uv = mat2(cs,-sn,sn,cs) * uv;
    
    // polar coords
    float r = length(uv);
    float a = atan(uv.y, uv.x);

    // base audio-driven values
    // sample average energy from low-mid bins
    float bass = sampleSpectrum(0.05) * 1.5;
    float mids = sampleSpectrum(0.15) * 1.2;
    float highs = sampleSpectrum(0.6) * 2.0;

    // radial mapping: map radius to frequency bin coordinate
    float freqCoord = pow(r, 0.6);
    float spectrum = sampleSpectrum(freqCoord);

    // central pulse influenced by bass
    float pulse = smoothstep(0.0, 0.35, 0.5 + 0.5 * sin(time*2.0)) * bass;

    // waves: create several concentric ripples that react to music
    float waveFreq = 20.0 + 40.0 * highs;
    float wave = sin(r * waveFreq - time * 8.0);
    float waveMask = smoothstep(0.3, 0.0, abs(fract(r * waveFreq * 0.25) - 0.5));
    waveMask *= (0.6 + 0.4 * bass); // bass emphasises waves

    // radial streaks — sum along radial direction to emulate motion blur
    float streak = 0.0;
    const int SAMPLES = 12;
    for(int i=0;i<SAMPLES;i++){
        float t = float(i) / float(SAMPLES-1);
        // sample a little further out, offset by time and audio
        float rr = r + t * (0.6 + 0.6 * mids) * 0.15;
        float fc = pow(rr, 0.6);
        float s = sampleSpectrum(fc);
        // weight nearer samples more
        streak += smoothstep(0.02, 0.0, abs(fract(rr * (20.0 + s*80.0)) - 0.5)) * (1.0 - t);
    }
    streak *= 0.6;

    // circular distortions: create spokes / concentric arcs
    float spokes = cos(a * (8.0 + 8.0 * highs) + time * 4.0) * 0.5 + 0.5;
    spokes = pow(spokes, 2.0);

    // electric arcs: sharp radial spikes that dance with bass
    float arc = pow(max(0.0, sin(r * 120.0 - time * 25.0 + rand(vec2(a, time)) * 6.0)), 24.0);
    arc *= smoothstep(0.9,0.0,r); // fade toward edge
    arc *= bass * 2.5; // amplify by bass

    // combine layers (reduced max intensity for less brightness)
    float intensity = clamp(waveMask * (0.6 + wave * 0.4) + streak * 0.6 + pulse * 0.9 + spokes * 0.2 + arc, 0.0, 2.5);

    // color base - cycle hue with time and spectrum
    float hue = mod(0.08 + spectrum*0.05 + iTime*0.02 + rand(uv)*0.03, 1.0);
    float sat = 0.7 + 0.3 * highs;
    float val = 0.75 + 0.25 * bass;
    vec3 baseColor = hsv2rgb(vec3(hue, sat, val));

    // radial vignette and falloff so center is bright
    float vignette = smoothstep(0.9, 0.2, r);

    vec3 col = baseColor * (intensity * vignette);

    // add enhanced rim glow and center highlight
    col += baseColor * pow(max(0.0, 1.0 - r*2.0), 6.0) * (0.5 + bass*1.5);

    // Cheap 3-tap blur approximation: sample two nearby offsets and average.
    // Offsets are small and perpendicular to angle to create radial streak softness.
    vec2 ofs1 = vec2(cos(a + 1.5708), sin(a + 1.5708)) * 0.008;
    vec2 ofs2 = vec2(cos(a - 1.5708), sin(a - 1.5708)) * 0.008;

    // Sample neighbors using a helper that accepts required locals as params.
    vec3 n1 = sampleNeighbor(uv + ofs1, time, pulse, bass, highs);
    vec3 n2 = sampleNeighbor(uv + ofs2, time, pulse, bass, highs);
    col = (col + n1 + n2) / 3.0;

    // slight overall dimming to control brightness
    col *= 0.7;

    // tone mapping / saturation
    col = pow(col, vec3(0.92));
    col = mix(col * 0.65, col, 0.85);

    // final gamma
    col = pow(col, vec3(0.4545));

    fragColor = vec4(col, 1.0);
}

// Note: the host wraps this shader and provides its own `main` that
// calls `mainImage(out vec4, in vec2)`. Do not define a second `main` here.
