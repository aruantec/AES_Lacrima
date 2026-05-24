precision highp float;

float hash21(vec2 p)
{
    p = fract(p * vec2(127.1, 311.7));
    p += dot(p, p + 19.19);
    return fract(p.x * p.y);
}

float sdSphere(vec3 p, float r)
{
    return length(p) - r;
}

float mapScene(vec3 p, float bass)
{
    float breathe = 0.18 + bass * 0.12;
    return sdSphere(p, breathe);
}

vec3 calcNormal(vec3 p, float bass)
{
    const float e = 0.0015;
    vec2 h = vec2(e, 0.0);
    return normalize(vec3(
        mapScene(p + vec3(h.x, h.y, h.y), bass) - mapScene(p - vec3(h.x, h.y, h.y), bass),
        mapScene(p + vec3(h.y, h.x, h.y), bass) - mapScene(p - vec3(h.y, h.x, h.y), bass),
        mapScene(p + vec3(h.y, h.y, h.x), bass) - mapScene(p - vec3(h.y, h.y, h.x), bass)));
}

vec3 shadeCel(vec3 base, float ndl, float levels)
{
    float x = ndl * levels;
    float band = floor(x + 0.0001) / (levels - 1.0);
    float next = min(band + 1.0 / (levels - 1.0), 1.0);
    float blend = smoothstep(0.0, 0.45, fract(x));
    return base * mix(band, next, blend);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 res = max(iResolution.xy, vec2(1.0));
    vec2 uv = (fragCoord - 0.5 * res) / res.y;
    float t = iTime;
    float bass = texture(iChannel0, vec2(0.05, 0.25)).r;

    vec3 ro = vec3(0.0, 0.0, 2.6);
    vec3 rd = normalize(vec3(uv, -1.35));

    float d = 0.0;
    vec3 p;
    for (int i = 0; i < 64; i++)
    {
        p = ro + rd * d;
        float h = mapScene(p, bass);
        if (h < 0.001)
            break;
        d += h;
        if (d > 8.0)
            break;
    }

    vec3 col = vec3(0.04, 0.05, 0.1);
    if (d < 8.0)
    {
        vec3 n = calcNormal(p, bass);
        vec3 lightDir = normalize(vec3(0.5, 0.8, 0.6));
        float ndl = max(dot(n, lightDir), 0.0);

        vec3 base = mix(u_primary, u_secondary, 0.35 + 0.65 * (0.5 + 0.5 * sin(t * 0.4)));
        col = shadeCel(base, ndl, 6.0);

        float rim = pow(1.0 - max(dot(n, -rd), 0.0), 3.0);
        col += mix(vec3(0.1, 0.12, 0.2), base, 0.4) * smoothstep(0.55, 1.0, rim) * 0.35;

        float edge = 1.0 - smoothstep(0.0, 0.03, mapScene(p + n * 0.02, bass));
        col = mix(col, vec3(0.06, 0.06, 0.1), edge * 0.4);
    }

    float vign = smoothstep(1.35, 0.35, length(uv));
    col *= mix(0.55, 1.0, vign);

    fragColor = vec4(col * u_fade, 1.0);
}
