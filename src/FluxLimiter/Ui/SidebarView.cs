namespace FluxLimiter;
internal sealed class SidebarView:Panel {
 public event Action<string>? PageSelected; private readonly Dictionary<string,Button> buttons=new(StringComparer.OrdinalIgnoreCase); private string selected="Dashboard";
 public string SelectedPage { get=>selected; set { selected=value; foreach(var b in buttons.Values){bool on=string.Equals(b.Tag?.ToString(),value,StringComparison.OrdinalIgnoreCase); b.BackColor=on?Color.FromArgb(11,88,168):Color.Transparent;b.ForeColor=on?Color.White:Palette.Muted;} } }
 public SidebarView(){Dock=DockStyle.Left;Width=224;BackColor=Color.FromArgb(10,18,32);Padding=new Padding(12,18,12,16);
  var brand=new Label{Text="▥  FluxLimiter",Dock=DockStyle.Top,Height=52,Font=new Font("Segoe UI",14,FontStyle.Bold),ForeColor=Color.White,TextAlign=ContentAlignment.MiddleLeft};Controls.Add(brand);
  var stack=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(0,12,0,0),BackColor=Color.Transparent};Controls.Add(stack);stack.BringToFront();
  foreach(var n in new[]{"Dashboard","Applications","Rules","Monthly Usage","Statistics","Settings","About"}){var b=new Button{Text="   "+n,Tag=n,Width=196,Height=42,FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Segoe UI Semibold",10),ForeColor=Palette.Muted,BackColor=Color.Transparent,Margin=new Padding(0,3,0,3)};b.FlatAppearance.BorderSize=0;b.Click+=(_,_)=>{SelectedPage=n;PageSelected?.Invoke(n);};buttons[n]=b;stack.Controls.Add(b);} SelectedPage="Dashboard";
 }
}
