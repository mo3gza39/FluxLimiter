using System.Globalization;
namespace FluxLimiter;
internal static class Palette { public static readonly Color Bg=Color.FromArgb(9,16,27), Card=Color.FromArgb(16,25,38), Line=Color.FromArgb(38,51,69), Muted=Color.FromArgb(175,188,207), Green=Color.FromArgb(47,211,128), Blue=Color.FromArgb(52,158,255), Input=Color.FromArgb(18,29,44), Pill=Color.FromArgb(18,31,51), RowAlt=Color.FromArgb(13,23,39), RowHover=Color.FromArgb(20,45,72); }
internal static class Gfx {
 public static string Speed(double kbps){ if(kbps<.05)return "0 KB/s"; if(kbps>=1024)return (kbps/1024).ToString("F2",CultureInfo.InvariantCulture)+" MB/s"; return kbps.ToString(kbps<10?"F1":"F0",CultureInfo.InvariantCulture)+" KB/s"; }
 public static string Bytes(long b){ if(b<=0)return "0 B"; double v=b; string[] u={"B","KB","MB","GB","TB"}; int i=0; while(v>=1024&&i<u.Length-1){v/=1024;i++;} return v.ToString(i<2?"F1":"F2",CultureInfo.InvariantCulture)+" "+u[i];}
}
internal class BufferedControl:Control { public BufferedControl(){DoubleBuffered=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);} }
