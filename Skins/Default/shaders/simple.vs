#version 110
attribute vec3 aPosition;
attribute vec2 aTexCoord;
attribute vec4 aColor;
uniform mat4 uMVP;
varying vec2 vTexCoord;
varying vec4 vColor;

void main()
{
    gl_Position = uMVP * vec4(aPosition, 1.0);
    vTexCoord = aTexCoord;
    vColor = aColor;
}
