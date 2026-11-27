export const vertex = `#version 300 es
out vec2 uv;
void main() {
  vec2 p=vec2(float((gl_VertexID<<1)&2),float(gl_VertexID&2));
  uv=p; gl_Position=vec4(p*2.0-1.0,0.0,1.0);
}`;
const header = `#version 300 es
precision highp float;
in vec2 uv;
out vec4 result;
uniform sampler2D field;
uniform sampler2D velocity;
uniform sampler2D auxiliary;
uniform vec2 texel;
uniform float dt;
uniform vec4 emitters[6];
uniform vec2 directions[6];
uniform vec4 splats[3];
uniform float seed;
`;
const sources = `
float plume(vec2 center,float width) {
  vec2 delta=(uv-center)*vec2(texel.y/texel.x,1.0);
  return exp(-dot(delta,delta)/(width*width));
}
`;
export const shaders = {
  advect: header + sources + `
void main() {
  vec2 v=texture(velocity,uv).xy;
  vec2 back=clamp(uv-dt*v*texel,texel*0.5,vec2(1.0)-texel*0.5);
  vec3 contents=texture(auxiliary,uv).rgb;
  float drag=dot(contents,vec3(1.2,1.0,0.9))/max(dot(contents,vec3(1)),0.001);
  v=texture(field,back).xy*exp(-dt*max(0.9,drag));
  float pigment=dot(contents,vec3(0.333));
  v.y+=pigment*dt*0.5;
  for(int i=0;i<6;i++) {
    vec2 center=emitters[i].xy;
    float weight=emitters[i].z;
    float band=emitters[i].w;
    float forceWidth=band<0.5?0.09:(band<1.5?0.055:0.025);
    float push=1000.0;
    float force=plume(center,forceWidth);
    v+=directions[i]*force*weight*dt*push;
  }
  float speed=length(v);
  v*=min(1.0,75.0/max(speed,0.001));
  result=vec4(v,0,1);
}`,
  divergence: header + `
void main() {
  vec2 l=texture(field,uv-vec2(texel.x,0)).xy;
  vec2 r=texture(field,uv+vec2(texel.x,0)).xy;
  vec2 b=texture(field,uv-vec2(0,texel.y)).xy;
  vec2 t=texture(field,uv+vec2(0,texel.y)).xy;
  vec2 c=texture(field,uv).xy;
  if(uv.x<texel.x) l.x=-c.x;
  if(uv.x>1.0-texel.x) r.x=-c.x;
  if(uv.y<texel.y) b.y=-c.y;
  if(uv.y>1.0-texel.y) t.y=-c.y;
  result=vec4((r.x-l.x+t.y-b.y)*0.5,(r.y-l.y-t.x+b.x)*0.5,0,1);
}`,
  curl: header + `
void main() {
  float l=abs(texture(auxiliary,uv-vec2(texel.x,0)).g);
  float r=abs(texture(auxiliary,uv+vec2(texel.x,0)).g);
  float b=abs(texture(auxiliary,uv-vec2(0,texel.y)).g);
  float t=abs(texture(auxiliary,uv+vec2(0,texel.y)).g);
  vec2 gradient=vec2(r-l,t-b)*0.5;
  gradient/=max(length(gradient),0.0001);
  float rotation=texture(auxiliary,uv).g;
  vec2 v=texture(field,uv).xy+vec2(gradient.y,-gradient.x)*rotation*dt*7.0;
  v*=min(1.0,75.0/max(length(v),0.001));
  result=vec4(v,0,1);
}`,
  pressure: header + `
void main() {
  float l=texture(field,uv-vec2(texel.x,0)).r;
  float r=texture(field,uv+vec2(texel.x,0)).r;
  float b=texture(field,uv-vec2(0,texel.y)).r;
  float t=texture(field,uv+vec2(0,texel.y)).r;
  result=vec4((l+r+b+t-texture(auxiliary,uv).r)*0.25,0,0,1);
}`,
  project: header + `
void main() {
  float l=texture(auxiliary,uv-vec2(texel.x,0)).r;
  float r=texture(auxiliary,uv+vec2(texel.x,0)).r;
  float b=texture(auxiliary,uv-vec2(0,texel.y)).r;
  float t=texture(auxiliary,uv+vec2(0,texel.y)).r;
  vec2 v=texture(field,uv).xy-vec2(r-l,t-b)*0.5;
  if(uv.x<texel.x||uv.x>1.0-texel.x) v.x=0.0;
  if(uv.y<texel.y||uv.y>1.0-texel.y) v.y=0.0;
  result=vec4(v,0,1);
}`,
  pigment: header + sources + `
void main() {
  vec2 v=texture(velocity,uv).xy;
  vec2 back=clamp(uv-dt*v*texel,texel*0.5,vec2(1.0)-texel*0.5);
  vec3 dye=texture(field,back).rgb*exp(-dt*vec3(0.36,0.55,1.0));
  // Initial diffuse pigment is static; all subsequent sources are short musical events.
  float haze=max(0.0,sin(uv.x*5.0+uv.y*3.0)*cos(uv.y*4.0-uv.x*2.0));
  dye+=vec3(0.07,0.04,0.03)*haze*seed;
  for(int i=0;i<6;i++) {
    float band=emitters[i].w;
    float width=band<0.5?0.065:(band<1.5?0.04:0.018);
    // Preserve a visible core while allowing the moving fluid to draw out fine trails.
    float volume=7.5;
    float footprint=plume(emitters[i].xy,width);
    if(band>1.5) {
      vec2 delta=(uv-emitters[i].xy)*vec2(texel.y/texel.x,1.0);
      vec2 axis=directions[i];
      float along=dot(delta,axis)/(width*2.5);
      float across=dot(delta,vec2(-axis.y,axis.x))/width;
      footprint=exp(-along*along-across*across);
    }
    float amount=footprint*emitters[i].z*dt*volume;
    vec3 channel=band<0.5?vec3(1,0,0):(band<1.5?vec3(0,1,0):vec3(0,0,1));
    dye+=channel*amount;
  }
  for(int i=0;i<3;i++) {
    if(splats[i].z<0.001) continue;
    // Compact circular injection with a steep, smooth spatial falloff.
    float radius=i==0?0.028:(i==1?0.021:0.014);
    float footprint=plume(splats[i].xy,radius);
    vec3 channel=i==0?vec3(1,0,0):(i==1?vec3(0,1,0):vec3(0,0,1));
    dye+=channel*footprint*splats[i].z*dt*80.0;
  }
  result=vec4(min(dye,vec3(5.0)),1);
}`,
  display: header + `
uniform vec3 tint;
float hueOf(vec3 c) {
  float high=max(c.r,max(c.g,c.b));
  float low=min(c.r,min(c.g,c.b));
  float span=high-low;
  if(span<0.0001) return 0.58;
  float hue=high==c.r?(c.g-c.b)/span:(high==c.g?2.0+(c.b-c.r)/span:4.0+(c.r-c.g)/span);
  return fract(hue/6.0);
}
vec3 palette(float hue,float saturation,float brightness) {
  vec3 wave=abs(fract(hue+vec3(0.0,2.0/3.0,1.0/3.0))*6.0-3.0);
  return brightness*mix(vec3(1.0),clamp(wave-1.0,0.0,1.0),saturation);
}
void main() {
  vec3 dye=texture(field,uv).rgb;
  float density=dot(dye,vec3(1));
  // Suspended dye has no raised surface: density controls transmission,
  // without the embossed highlights that made the fluid resemble thick paint.
  float hue=hueOf(tint);
  float brightness=max(tint.r,max(tint.g,tint.b));
  // A coherent artwork-derived palette with a recognizable color per register.
  vec3 first=palette(hue,0.88,brightness*0.85);
  vec3 second=palette(hue+0.14,0.76,brightness*0.95);
  vec3 third=palette(hue+0.40,0.55,brightness);
  vec3 pigment=(first*dye.r+second*dye.g+third*dye.b)/max(density,0.001);
  float opacity=1.0-exp(-density*0.95);
  vec3 color=pigment*opacity*0.85;
  color=color/(vec3(1.0)+color*0.35);
  result=vec4(vec3(0.005,0.007,0.012)+color,1.0);
}`,
};
