using System.Globalization;
namespace FluxLimiter;

internal readonly record struct DayBars(DateTime Day,long Rx,long Tx);
internal sealed record TopApp(string Name,Image? Icon,long Bytes);
internal enum RowAction{Edit,ToggleBlock}

internal sealed class DashboardView:Panel
{
 public DataGridView Grid{get;}=new(); public TextBox SearchBox{get;}=new(); public ComboBox FilterBox{get;}=new();
 public event EventHandler? SeeAllClicked; public event Action<int,RowAction>? RowActionClicked; public event Action<int,bool,int?,bool>? LimitChosen;
 readonly Label down=new(),up=new(),daily=new(),monthly=new(),net=new(),footer=new();
 readonly FlowLayoutPanel tops=new(); readonly SevenDayChart chart=new();

 public DashboardView()
 {
  Dock=DockStyle.Fill; BackColor=Palette.Bg; Padding=new Padding(26,20,26,16);
  var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,BackColor=Palette.Bg};
  root.RowStyles.Add(new RowStyle(SizeType.Absolute,82));root.RowStyles.Add(new RowStyle(SizeType.Absolute,128));root.RowStyles.Add(new RowStyle(SizeType.Absolute,230));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,22));Controls.Add(root);

  var head=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,240));
  head.Controls.Add(new Label{Text="Dashboard\nLive network usage and application activity",Dock=DockStyle.Fill,ForeColor=Color.White,Font=new Font("Segoe UI Variable Display",19,FontStyle.Bold)});
  net.TextAlign=ContentAlignment.MiddleCenter;net.ForeColor=Color.White;net.BackColor=Palette.Card;net.Font=new Font("Segoe UI Variable Text",9);head.Controls.Add(net);root.Controls.Add(head);

  var cards=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,Margin=Padding.Empty};for(int i=0;i<4;i++)cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
  AddCard(cards,"↓  DOWNLOAD",down,0);AddCard(cards,"↑  UPLOAD",up,1);AddCard(cards,"TODAY’S USAGE",daily,2);AddCard(cards,"MONTHLY USAGE",monthly,3);root.Controls.Add(cards);

  var mid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,42));mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,58));
  var topCard=Card("Top Applications (Today)");tops.Dock=DockStyle.Fill;tops.FlowDirection=FlowDirection.TopDown;tops.WrapContents=false;tops.AutoScroll=true;tops.Padding=new Padding(16,2,16,12);tops.BackColor=Palette.Card;topCard.Controls.Add(tops);
  var chartCard=Card("Daily Usage · Last 7 Days");chart.Dock=DockStyle.Fill;chart.BackColor=Palette.Card;chartCard.Controls.Add(chart);
  mid.Controls.Add(topCard);mid.Controls.Add(chartCard);root.Controls.Add(mid);

  var active=new Panel{Dock=DockStyle.Fill,BackColor=Palette.Card,Padding=new Padding(16),Margin=new Padding(4,6,4,4)};
  var bar=new Panel{Dock=DockStyle.Top,Height=48,BackColor=Palette.Card};
  var at=new Label{Text="Active Applications",Dock=DockStyle.Left,Width=220,ForeColor=Color.White,Font=new Font("Segoe UI Variable Text",11,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft};
  FilterBox.Dock=DockStyle.Right;FilterBox.Width=170;FilterBox.DropDownStyle=ComboBoxStyle.DropDownList;FilterBox.Items.AddRange(new object[]{"All Connections","User Processes","System Processes","Limited / Blocked"});FilterBox.SelectedIndex=0;FilterBox.FlatStyle=FlatStyle.Flat;FilterBox.BackColor=Palette.Input;FilterBox.ForeColor=Color.White;
  SearchBox.Dock=DockStyle.Right;SearchBox.Width=250;SearchBox.PlaceholderText="Search applications...";SearchBox.BackColor=Palette.Input;SearchBox.ForeColor=Color.White;SearchBox.BorderStyle=BorderStyle.FixedSingle;
  bar.Controls.Add(FilterBox);bar.Controls.Add(SearchBox);bar.Controls.Add(at);active.Controls.Add(Grid);active.Controls.Add(bar);Grid.Dock=DockStyle.Fill;Grid.BringToFront();ConfigureGrid();root.Controls.Add(active);

  footer.Dock=DockStyle.Fill;footer.ForeColor=Palette.Muted;footer.Font=new Font("Segoe UI Variable Text",8);footer.TextAlign=ContentAlignment.MiddleLeft;root.Controls.Add(footer);
 }

 static Panel Card(string title){var p=new Panel{Dock=DockStyle.Fill,BackColor=Palette.Card,Padding=new Padding(0),Margin=new Padding(4,6,4,4)};p.Controls.Add(new Label{Text=title,Dock=DockStyle.Top,Height=38,Padding=new Padding(16,10,0,0),ForeColor=Color.White,Font=new Font("Segoe UI Variable Text",10.5f,FontStyle.Bold)});return p;}
 static void AddCard(TableLayoutPanel host,string name,Label value,int col){var p=new Panel{Dock=DockStyle.Fill,BackColor=Palette.Card,Margin=new Padding(4,6,4,4),Padding=new Padding(16,12,16,10)};var l=new Label{Text=name,Dock=DockStyle.Top,Height=24,ForeColor=Palette.Muted,Font=new Font("Segoe UI Variable Text",8.5f,FontStyle.Bold)};value.Dock=DockStyle.Fill;value.ForeColor=Color.White;value.Font=new Font("Segoe UI Variable Display",17,FontStyle.Bold);value.TextAlign=ContentAlignment.MiddleLeft;p.Controls.Add(value);p.Controls.Add(l);host.Controls.Add(p,col,0);}

 void ConfigureGrid()
 {
  Grid.BackgroundColor=Palette.Card;Grid.BorderStyle=BorderStyle.None;Grid.CellBorderStyle=DataGridViewCellBorderStyle.None;Grid.RowHeadersVisible=false;Grid.AllowUserToAddRows=false;Grid.AllowUserToDeleteRows=false;Grid.AllowUserToResizeRows=false;Grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;Grid.MultiSelect=false;Grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;Grid.ColumnHeadersHeight=38;Grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;Grid.RowTemplate.Height=48;Grid.EnableHeadersVisualStyles=false;Grid.GridColor=Palette.Card;Grid.Font=new Font("Segoe UI Variable Text",9.25f);
  Grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;Grid.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle{BackColor=Palette.Card,ForeColor=Palette.Muted,SelectionBackColor=Palette.Card,SelectionForeColor=Palette.Muted,Font=new Font("Segoe UI Variable Text",8.5f,FontStyle.Bold),Padding=new Padding(4,0,4,0),Alignment=DataGridViewContentAlignment.MiddleLeft};
  Grid.DefaultCellStyle=new DataGridViewCellStyle{BackColor=Palette.Card,ForeColor=Color.FromArgb(224,231,240),SelectionBackColor=Palette.RowHover,SelectionForeColor=Color.White,Padding=new Padding(6,0,6,0),Alignment=DataGridViewContentAlignment.MiddleLeft};
  Grid.RowTemplate.DividerHeight=1;Grid.AlternatingRowsDefaultCellStyle=new DataGridViewCellStyle{BackColor=Palette.RowAlt,ForeColor=Color.FromArgb(224,231,240),SelectionBackColor=Palette.RowHover,SelectionForeColor=Color.White,Padding=new Padding(6,0,6,0)};
  Grid.Columns.Add(new DataGridViewImageColumn{Name="icon",HeaderText="",Width=42,ImageLayout=DataGridViewImageCellLayout.Zoom});
  foreach(var x in new[]{("app","Application",190),("status","Status",82),("download","Download",98),("upload","Upload",98),("total","Total",88),("down","Limit ↓",94),("up","Limit ↑",94),("rule","Rule",72),("action","",54),("pid","PID",90),("path","Path",160)}) Grid.Columns.Add(new DataGridViewTextBoxColumn{Name=x.Item1,HeaderText=x.Item2,Width=x.Item3});
  Grid.Columns["app"]!.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill; Grid.Columns["action"]!.DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;Grid.Columns["pid"]!.Visible=false;Grid.Columns["path"]!.Visible=false;
  Grid.CellFormatting+=(_,e)=>{if(e.RowIndex<0)return;string n=Grid.Columns[e.ColumnIndex].Name;if(n is "download" or "upload"){e.CellStyle.ForeColor=Color.FromArgb(210,220,234);e.CellStyle.Font=new Font("Segoe UI Variable Text",9.25f,FontStyle.Bold);}else if(n=="status"){var s=e.Value?.ToString()??"";e.CellStyle.ForeColor=s.Contains("Active")?Palette.Green:s.Contains("Blocked")?Color.FromArgb(248,113,113):Palette.Muted;}};
  Grid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0)RowActionClicked?.Invoke(e.RowIndex,RowAction.Edit);};
 }

 public void SetSpeeds(double d,double u){down.Text=Gfx.Speed(d);up.Text=Gfx.Speed(u);}
 public void SetNetwork(string a,bool ok){net.Text=(ok?"● Connected":"● Disconnected")+"\n"+a;net.ForeColor=ok?Palette.Green:Color.FromArgb(248,113,113);}
 public void SetUsage(long rx,long tx,long month,double quota,IReadOnlyList<DayBars> d,IReadOnlyList<TopApp> t)
 {
  daily.Text=Gfx.Bytes(rx+tx)+"\n↓ "+Gfx.Bytes(rx)+"   ↑ "+Gfx.Bytes(tx);monthly.Text=Gfx.Bytes(month);chart.SetData(d);
  tops.Controls.Clear();
  if(t.Count==0)tops.Controls.Add(new Label{Text="Waiting for validated per-app traffic data",AutoSize=true,ForeColor=Palette.Muted,Font=new Font("Segoe UI Variable Text",9.25f),Margin=new Padding(0,10,0,0)});
  else foreach(var x in t)tops.Controls.Add(new Label{Text=$"{x.Name}   {Gfx.Bytes(x.Bytes)}",AutoSize=true,ForeColor=Color.White});
 }
 public void SetFooter(string s)=>footer.Text=s;
}

internal sealed class SevenDayChart:Control
{
 IReadOnlyList<DayBars> data=Array.Empty<DayBars>();
 readonly Font labelFont=new("Segoe UI Variable Text",8.5f), legendFont=new("Segoe UI Variable Text",8.5f,FontStyle.Bold);
 public SevenDayChart(){DoubleBuffered=true;}
 public void SetData(IReadOnlyList<DayBars> value){data=value;Invalidate();}
 protected override void OnPaint(PaintEventArgs e)
 {
  base.OnPaint(e);if(data.Count==0)return;var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
  var area=new RectangleF(20,32,Math.Max(1,Width-40),Math.Max(1,Height-58));long max=Math.Max(1,data.Max(x=>Math.Max(x.Rx,x.Tx)));float slot=area.Width/data.Count;float bw=Math.Min(14,slot*.18f);
  using var gridPen=new Pen(Color.FromArgb(31,45,63));for(int i=0;i<4;i++){float y=area.Top+(area.Height*i/3);g.DrawLine(gridPen,area.Left,y,area.Right,y);}
  using var db=new SolidBrush(Palette.Blue);using var ub=new SolidBrush(Palette.Green);
  g.FillEllipse(db,area.Left,8,7,7);TextRenderer.DrawText(g,"Download",legendFont,new Point((int)area.Left+12,4),Palette.Muted);
  g.FillEllipse(ub,area.Left+92,8,7,7);TextRenderer.DrawText(g,"Upload",legendFont,new Point((int)area.Left+104,4),Palette.Muted);
  for(int i=0;i<data.Count;i++){var d=data[i];float dh=(float)(area.Height*(d.Rx/(double)max));float uh=(float)(area.Height*(d.Tx/(double)max));float center=area.Left+i*slot+slot/2;g.FillRectangle(db,center-bw-2,area.Bottom-dh,bw,dh);g.FillRectangle(ub,center+2,area.Bottom-uh,bw,uh);string day=d.Day.ToString("ddd",CultureInfo.InvariantCulture);TextRenderer.DrawText(g,day,labelFont,new Rectangle((int)(area.Left+i*slot),(int)area.Bottom+5,(int)slot,18),Palette.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);}
 }
 protected override void Dispose(bool disposing){if(disposing){labelFont.Dispose();legendFont.Dispose();}base.Dispose(disposing);}
}
