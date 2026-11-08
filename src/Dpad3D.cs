using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ThorCapture
{
    // A small software-rendered mesh: socket, side wall, bevel and dished pads.
    internal static class Dpad3D
    {
        private struct V
        {
            public float X,Y,Z;
            public V(float x,float y,float z) { X=x; Y=y; Z=z; }
        }
        private sealed class Surface
        {
            public Graphics G;
            public float Cx,Cy,X,Y,Press;
            public V Rotate(V p)
            {
                double ax=-Y*.12,ay=X*.12;
                float py=(float)(p.Y*Math.Cos(ax)-p.Z*Math.Sin(ax));
                float pz=(float)(p.Y*Math.Sin(ax)+p.Z*Math.Cos(ax));
                return new V((float)(p.X*Math.Cos(ay)+pz*Math.Sin(ay)),py,(float)(-p.X*Math.Sin(ay)+pz*Math.Cos(ay))-Press);
            }
            public PointF Project(V p) { return new PointF(Cx+p.X,Cy+p.Y-p.Z*.38f); }
            private Color Light(double nx,double ny,double nz,Color color)
            {
                double len=Math.Sqrt(nx*nx+ny*ny+nz*nz); nx/=len; ny/=len; nz/=len;
                double diffuse=Math.Max(0,-nx*.42-ny*.52+nz*.74);
                double spec=Math.Pow(Math.Max(0,-nx*.23-ny*.28+nz*.932),24)*.16;
                double shade=.42+.68*diffuse;
                return Color.FromArgb(Clamp(color.R*shade+255*spec),Clamp(color.G*shade+255*spec),Clamp(color.B*shade+255*spec));
            }
            public void Dish(V[] ring,V centre,Color color)
            {
                PointF[] points=new PointF[ring.Length]; Color[] colors=new Color[ring.Length];
                V zero=Rotate(new V(0,0,0));
                for(int i=0;i<ring.Length;i++)
                {
                    points[i]=Project(Rotate(ring[i]));
                    V n=Rotate(new V(-(ring[i].X-centre.X)*.005f,-(ring[i].Y-centre.Y)*.004f,1));
                    colors[i]=Light(n.X-zero.X,n.Y-zero.Y,n.Z-zero.Z,color);
                }
                V normal=Rotate(new V(0,0,1));
                using(PathGradientBrush dish=new PathGradientBrush(points))
                {
                    dish.CenterPoint=Project(Rotate(centre));
                    dish.CenterColor=Light(normal.X-zero.X,normal.Y-zero.Y,normal.Z-zero.Z,color);
                    dish.SurroundColors=colors;
                    G.FillPolygon(dish,points);
                }
            }
            public void Triangle(V a,V b,V c,Color color)
            {
                a=Rotate(a); b=Rotate(b); c=Rotate(c);
                float ux=b.X-a.X,uy=b.Y-a.Y,uz=b.Z-a.Z,vx=c.X-a.X,vy=c.Y-a.Y,vz=c.Z-a.Z;
                double nx=uy*vz-uz*vy,ny=uz*vx-ux*vz,nz=ux*vy-uy*vx;
                double len=Math.Sqrt(nx*nx+ny*ny+nz*nz);
                if(len<.00001) return;
                nx/=len; ny/=len; nz/=len;
                if(nz<0) { nx=-nx; ny=-ny; nz=-nz; }
                Color lit=Light(nx,ny,nz,color);
                PointF[] points={Project(a),Project(b),Project(c)};
                using(SolidBrush brush=new SolidBrush(lit)) G.FillPolygon(brush,points);
                using(Pen seam=new Pen(lit,.45f)) G.DrawPolygon(seam,points);
            }
            public void Quad(V a,V b,V c,V d,Color color) { Triangle(a,b,c,color); Triangle(a,c,d,color); }
            public PointF Mark(float x,float y,int direction,float z)
            {
                for(int i=0;i<direction;i++) { float t=x; x=-y; y=t; }
                return Project(Rotate(new V(x,y,z)));
            }
        }
        private static int Clamp(double value) { return Math.Max(0,Math.Min(255,(int)value)); }
        private static V Vertex(PointF p,float scale,float z) { return new V(p.X*scale,p.Y*scale,z); }
        public static void Draw(Graphics g,DeviceSkin skin,float cx,float cy,PadState input)
        {
            float x=input.Animated?input.TiltX:input.DirectionX,y=input.Animated?input.TiltY:input.DirectionY;
            float length=(float)Math.Sqrt(x*x+y*y); if(length>1) { x/=length; y/=length; }
            Surface mesh=new Surface { G=g,Cx=cx,Cy=cy,X=x,Y=y,Press=Math.Min(1,length)*1.6f };
            // The raised round plate under the cross is drawn by DeviceArt.Plate.
            using(GraphicsPath slot=DeviceArt.Cross(cx,cy+1,62,24,7))
            using(SolidBrush dark=new SolidBrush(Ui.Mix(skin.DpadDark,Color.Black,.65f))) g.FillPath(dark,slot);
            using(GraphicsPath shadow=DeviceArt.Cross(cx+1,cy+2,61,23,7))
            using(Pen pen=new Pen(Color.FromArgb(38,0,0,0),3)) g.DrawPath(pen,shadow);
            using(GraphicsPath shape=DeviceArt.Cross(0,0,60,22,7))
            {
                shape.Flatten(null,.7f);
                PointF[] ring=shape.PathPoints;
                for(int i=0;i<ring.Length;i++)
                {
                    PointF a=ring[i],b=ring[(i+1)%ring.Length];
                    mesh.Quad(Vertex(a,1,0),Vertex(b,1,0),Vertex(b,1,5),Vertex(a,1,5),skin.DpadDark);
                }
                for(int i=0;i<ring.Length;i++)
                {
                    PointF a=ring[i],b=ring[(i+1)%ring.Length];
                    mesh.Quad(Vertex(a,1,5),Vertex(b,1,5),Vertex(b,.945f,8),Vertex(a,.945f,8),skin.Dpad);
                    mesh.Triangle(new V(0,0,6),Vertex(a,.945f,8),Vertex(b,.945f,8),skin.DpadDark);
                }
            }
            for(int direction=0;direction<4;direction++)
            {
                // Inset the contact surface; its centre sits lower than its bevel.
                PointF[] outline={new PointF(-19,-24),new PointF(-19,-51),new PointF(-15,-55),new PointF(15,-55),new PointF(19,-51),new PointF(19,-24),new PointF(0,-3)};
                V[] outside=new V[outline.Length],inside=new V[outline.Length];
                V centre=Turn(new V(0,-33,6.4f),direction);
                for(int i=0;i<outline.Length;i++)
                {
                    PointF p=outline[i];
                    outside[i]=Turn(new V(p.X,p.Y,8),direction);
                    inside[i]=Turn(new V(p.X*.86f,-33+(p.Y+33)*.88f,7.1f),direction);
                }
                Color material=Ui.Mix(skin.Dpad,Color.White,.035f);
                for(int i=0;i<outline.Length;i++)
                {
                    int j=(i+1)%outline.Length;
                    mesh.Quad(outside[i],outside[j],inside[j],inside[i],material);
                }
                mesh.Dish(inside,centre,material);
                // Engraving uses a dark cut and a displaced light-catching lower lip.
                PointF[] mark={mesh.Mark(-6,-42,direction,6.8f),mesh.Mark(0,-49,direction,6.8f),mesh.Mark(6,-42,direction,6.8f),mesh.Mark(-6,-42,direction,6.8f)};
                PointF[] glint=(PointF[])mark.Clone(); for(int i=0;i<glint.Length;i++) glint[i].Y+=.7f;
                using(Pen light=new Pen(Ui.Alpha(Color.White,42),.7f)) g.DrawLines(light,glint);
                using(Pen dark=new Pen(Ui.Mix(skin.DpadDark,Color.Black,.35f),1.1f))
                {
                    g.DrawLines(dark,mark);
                    g.DrawLine(dark,mesh.Mark(-6,-38,direction,6.7f),mesh.Mark(6,-38,direction,6.7f));
                }
            }
        }
        private static V Turn(V p,int turns)
        {
            for(int i=0;i<turns;i++) { float x=p.X; p.X=-p.Y; p.Y=x; }
            return p;
        }
    }
}
