namespace FluxLimiter;
internal sealed class SidebarView:Panel {
 public event Action<string>? PageSelected; private readonly Dictionary<string,Button> buttons=new(StringComparer.OrdinalIgnoreCase); private string selected="Dashboard";
 public string SelectedPage { get=>selected; set { selected=value; foreach(var b in buttons.Values){bool on=string.Equals(b.Tag?.ToString(),value,StringComparison.OrdinalIgnoreCase); b.BackColor=on?Color.FromArgb(25,58,94):Color.Transparent;b.ForeColor=on?Color.FromArgb(92,190,255):Color.FromArgb(207,216,230);} } }
 public SidebarView(){Dock=DockStyle.Left;Width=248;BackColor=Color.FromArgb(11,19,31);Padding=new Padding(16,18,16,16);
  var brand=new Label{Text="↕  FluxLimiter",Dock=DockStyle.Top,Height=64,Font=new Font("Segoe UI Variable Display",15,FontStyle.Bold),ForeColor=Color.White,TextAlign=ContentAlignment.MiddleLeft};Controls.Add(brand);
  var foot=new Label{Text="●  Service Running\n\nv1.0.0",Dock=DockStyle.Bottom,Height=86,Font=new Font("Segoe UI Variable Text",9.5f),ForeColor=Color.FromArgb(185,198,216),TextAlign=ContentAlignment.MiddleLeft};Controls.Add(foot);
  var stack=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(0,12,0,0),BackColor=Color.Transparent};Controls.Add(stack);stack.BringToFront();
  foreach(var item in new[]{("⌂","Dashboard"),("▦","Applications"),("◇","Rules"),("⚙","Settings")}){string icon=item.Item1,n=item.Item2;var b=new Button{Text="  "+icon+"    "+n,Tag=n,Width=216,Height=48,FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Segoe UI Variable Text",10.5f),ForeColor=Color.FromArgb(207,216,230),BackColor=Color.Transparent,Margin=new Padding(0,4,0,4)};b.FlatAppearance.BorderSize=0;b.FlatAppearance.MouseOverBackColor=Color.FromArgb(18,37,60);b.Click+=(_,_)=>{SelectedPage=n;PageSelected?.Invoke(n);};buttons[n]=b;stack.Controls.Add(b);} SelectedPage="Dashboard";
 }
}