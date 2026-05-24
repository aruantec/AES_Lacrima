precision highp float;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 res = max(iResolution.xy, vec2(1.0));
    vec2 uv = fragCoord / res;
    vec2 p = (fragCoord - 0.5 * res) / res.y;

    float bass = texture(iChannel0, vec2(0.05, 0.25)).r;
    float spec = texture(iChannel0, vec2(uv.x, 0.25)).r;

    vec3 base = mix(u_primary, u_secondary, 0.4 + 0.6 * spec);
    float luma = length(p) * 1.2 - spec * 0.35 - bass * 0.2;
    luma = clamp(1.0 - luma, 0.0, 1.0);

    float x = luma * 5.0;
    float band = floor(x + 0.0001) / 4.0;
    float next = min(band + 0.25, 1.0);
    float blend = smoothstep(0.0, 0.65, fract(x));
    vec3 col = base * mix(band, next, blend);

    float edge = abs(dFdx(luma)) + abs(dFdy(luma));
    col = mix(col, vec3(0.08, 0.1, 0.18), smoothstep(0.02, 0.12, edge) * 0.3);

    float vign = smoothstep(1.2, 0.4, length(p));
    col *= mix(0.7, 1.0, vign);

    fragColor = vec4(col * u_fade, 1.0);
}
