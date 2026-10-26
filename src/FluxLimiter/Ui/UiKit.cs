using System.Globalization;
namespace FluxLimiter;
internal static class Palette { public static readonly Color Bg=Color.FromArgb(8,15,28), Card=Color.FromArgb(15,25,43), Line=Color.FromArgb(29,43,66), Muted=Color.FromArgb(139,155,180), Green=Color.FromArgb(0,230,118), Blue=Color.FromArgb(29,155,240), Input=Color.FromArgb(13,21,36), Pill=Color.FromArgb(18,31,51), RowAlt=Color.FromArgb(13,23,39), RowHover=Color.FromArgb(20,45,72); }
internal static class Gfx {
 public static string Speed(double kbps){ if(kbps<.05)return "0 KB/s"; if(kbps>=1024)return (kbps/1024).ToString("F2",CultureInfo.InvariantCulture)+" MB/s"; return kbps.ToString(kbps<10?"F1":"F0",CultureInfo.InvariantCulture)+" KB/s"; }
 public static string Bytes(long b){ if(b<=0)return "0 B"; double v=b; string[] u={"B","KB","MB","GB","TB"}; int i=0; while(v>=1024&&i<u.Length-1){v/=1024;i++;} return v.ToString(i<2?"F1":"F2",CultureInfo.InvariantCulture)+" "+u[i];}
}
internal class BufferedControl:Control { public BufferedControl(){DoubleBuffered=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);} }
