#version 110
uniform sampler2D uTexture;
uniform float uTime;
uniform float uOpacity;
varying vec2 vTexCoord;
varying vec4 vColor;

void main()
{
    vec4 texel = texture2D(uTexture, vTexCoord);
    float pulse = 0.9 + 0.1 * sin(uTime * 5.0);
    gl_FragColor = texel * vColor * vec4(pulse, pulse, pulse, uOpacity);
}
