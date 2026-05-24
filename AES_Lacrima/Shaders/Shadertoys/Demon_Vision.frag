precision highp float;

float hash21(vec2 p)
{
    p = fract(p * vec2(127.1, 311.7));
    p += dot(p, p + 19.19);
    return fract(p.x * p.y);
}

float noise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 res = max(iResolution.xy, vec2(1.0));
    vec2 uv = fragCoord / res;
    vec2 p = (fragCoord - 0.5 * res) / res.y;
    float t = iTime;

    float bass = texture(iChannel0, vec2(0.05, 0.25)).r;
    float spec = texture(iChannel0, vec2(uv.x, 0.25)).r;

    float n = noise(p * 3.0 + vec2(t * 0.35, -t * 0.28));
    float heat = smoothstep(0.3, 0.95, n + spec * 0.4 + bass * 0.3);

    vec3 hell = mix(vec3(0.06, 0.0, 0.01), u_primary, 0.55);
    vec3 ember = mix(vec3(1.0, 0.1, 0.02), u_secondary, 0.35);
    vec3 core = vec3(1.0, 0.65, 0.12);

    vec3 col = mix(hell, ember, heat);
    col = mix(col, core, smoothstep(0.72, 1.0, heat + bass * 0.25));

    float dist = length(p);
    float vign = smoothstep(1.15, 0.2, dist);
    float ring = smoothstep(0.02, 0.0, abs(dist - 0.62)) * 0.35;
    col += ember * ring;

    float scan = sin((p.y + t * 0.65) * 70.0) * 0.5 + 0.5;
    float sight = smoothstep(0.5, 0.95, 1.0 - abs(p.y - sin(p.x * 5.0 + t * 1.8) * 0.07));
    col += ember * sight * (0.3 + bass * 0.5);
    col *= mix(0.45, 1.0, vign);
    col *= mix(0.88, 1.0, scan * 0.12);

    float pulse = 0.9 + 0.1 * sin(t * 2.4);
    col *= pulse;

    fragColor = vec4(col * u_fade, 1.0);
}
