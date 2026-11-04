using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LanBridge {
 public sealed partial class BridgeForm {
  TableLayoutPanel shell; Panel pageHost,joinBanner; Panel[] pages; Button[] navigation;
  Label pageTitle,pageSubtitle,headerStatus,notice,joinLabel,deviceHint,scanStatus,peerStatus,pairCode,remoteStatus,clipboardLocal,clipboardPeer,fileStatus,fileTargetHint;
  Label diagnostics; TextBox localAddress,peerAddress,preview,activityLog; CheckBox auto,pause,approveSync,connectClipboard;
  ListView devices,files,peerFiles; ComboBox addresses,fileTargets; ListBox nearby; Button scanButton;
  PictureBox localQr,remoteQr; JoinRequest pendingJoin;
  Panel[] connectionPages,remoteSteps; Button[] connectionModes,remoteModeButtons;
  Label connectionHelp,remoteInviteHint,remoteJoinStatus; TextBox remoteAddress,remoteInvitation;
  CheckBox inviteShareClipboard,remoteConnectClipboard; Button remoteToggle,inviteCopyButton;
  CheckBox autoAttachments; Label attachmentStatus; TableLayoutPanel connectionLayout,recentButtons; Panel recentArea; ListView historyDevices;
  readonly Color ink=Color.FromArgb(31,48,65),muted=Color.FromArgb(94,111,128),accent=Color.FromArgb(24,110,98);
  Button Button(string text,Action action,bool primary=false) {
   var button=new Button {Text=text,AutoSize=true,MinimumSize=new Size(80,36),Padding=new Padding(12,5,12,5),FlatStyle=FlatStyle.Flat,BackColor=primary?accent:Color.White,ForeColor=primary?Color.White:ink,Margin=new Padding(0,4,8,4),Cursor=Cursors.Hand};
   button.FlatAppearance.BorderColor=primary?accent:Color.FromArgb(215,224,230); button.Click+=delegate { Run(action); }; return button;
  }
  Label Label(string text,float size=10,bool bold=false) { return new Label {Text=text,AutoSize=true,MaximumSize=new Size(720,0),Font=new Font(Font.FontFamily,size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=bold?ink:muted,Margin=new Padding(0,4,0,8)}; }
  FlowLayoutPanel Vertical() {
   var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(12),BackColor=Color.White};
   panel.SizeChanged+=delegate { foreach(Control control in panel.Controls) { if(control is Label) control.MaximumSize=new Size(Math.Max(200,panel.ClientSize.Width-40),0); else if(control is TextBox || control is ListBox || control is ComboBox || control is ListView || control is TableLayoutPanel) control.Width=Math.Max(200,panel.ClientSize.Width-42); } }; return panel;
  }
  FlowLayoutPanel Row() { return new FlowLayoutPanel {AutoSize=true,WrapContents=true,Dock=DockStyle.Top,Margin=new Padding(0),Padding=new Padding(0)}; }
  ListView List(params string[] columns) {
   var list=new ListView {Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,HideSelection=false,BorderStyle=BorderStyle.FixedSingle,BackColor=Color.White};
   foreach(string name in columns) list.Columns.Add(name,150);
   list.SizeChanged+=delegate { if(list.Columns.Count==0) return; int width=Math.Max(100,list.ClientSize.Width-8); for(int i=0;i<list.Columns.Count;i++) list.Columns[i].Width=i==0?(int)(width*0.34):(int)(width*0.66/(list.Columns.Count-1)); }; return list;
  }
  TextBox ReadBox(int height) { return new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Height=height,Width=650,BackColor=Color.FromArgb(248,250,252),BorderStyle=BorderStyle.FixedSingle}; }
  void Build() {
   var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)}; root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,152)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); Controls.Add(root);
   var sidebar=new Panel {Dock=DockStyle.Fill,BackColor=Color.FromArgb(28,47,62),Padding=new Padding(14,24,14,14)}; root.Controls.Add(sidebar,0,0);
   var nav=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false}; sidebar.Controls.Add(nav);
   var brand=Label("轻桥",25,true); brand.ForeColor=Color.White; brand.Margin=new Padding(8,0,0,0); nav.Controls.Add(brand);
   var version=Label("LANBRIDGE  1.5",8); version.ForeColor=Color.FromArgb(167,190,203); version.Margin=new Padding(8,6,0,30); nav.Controls.Add(version);
   string[] names={"设备","剪贴板","文件","设置"}; navigation=new Button[names.Length];
   for(int i=0;i<names.Length;i++) { int index=i; var button=new Button {Text=names[i],Width=120,Height=48,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(16,0,0,0),FlatStyle=FlatStyle.Flat,BackColor=sidebar.BackColor,ForeColor=Color.FromArgb(200,216,225),Margin=new Padding(0,0,0,8),Cursor=Cursors.Hand}; button.FlatAppearance.BorderSize=0; button.Click+=delegate { SelectPage(index); }; navigation[i]=button; nav.Controls.Add(button); }
   var bottom=Label("优先尝试直连\n不必判断网络类型",9); bottom.ForeColor=Color.FromArgb(167,190,203); bottom.Margin=new Padding(8,26,0,0); nav.Controls.Add(bottom);
   shell=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(24,12,24,10),Margin=new Padding(0)};
   shell.RowStyles.Add(new RowStyle(SizeType.Absolute,82)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute,0)); shell.RowStyles.Add(new RowStyle(SizeType.Percent,100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute,48)); root.Controls.Add(shell,1,0);
   var header=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2}; header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
   pageTitle=Label("设备",22,true); pageTitle.Dock=DockStyle.Fill; header.Controls.Add(pageTitle,0,0);
   headerStatus=Label("等待设备连接",10,true); headerStatus.TextAlign=ContentAlignment.MiddleRight; headerStatus.Dock=DockStyle.Fill; header.Controls.Add(headerStatus,1,0);
   pageSubtitle=Label("先连接设备，再顺手复制和传文件。",9); header.Controls.Add(pageSubtitle,0,1); header.SetColumnSpan(pageSubtitle,2); shell.Controls.Add(header,0,0);
   joinBanner=new Panel {Dock=DockStyle.Fill,BackColor=Color.FromArgb(231,242,238),Visible=false,Padding=new Padding(10,4,10,4)};
   var joinGrid=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2}; joinGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); joinGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
   joinLabel=Label("",11,true); joinLabel.Dock=DockStyle.Fill; joinGrid.Controls.Add(joinLabel,0,0); var decisions=Row(); decisions.Controls.Add(Button("允许连接",Approve,true)); decisions.Controls.Add(Button("拒绝",delegate { if(pendingJoin!=null) state.ApproveJoin(pendingJoin.key,false); RefreshView(); })); joinGrid.Controls.Add(decisions,1,0); joinGrid.SetRowSpan(decisions,2);
   approveSync=new CheckBox {AutoSize=true,Text="同时开启本机文字自动同步（包括敏感文字）",Checked=true}; joinGrid.Controls.Add(approveSync,0,1); joinBanner.Controls.Add(joinGrid); shell.Controls.Add(joinBanner,0,1);
   pageHost=new Panel {Dock=DockStyle.Fill}; shell.Controls.Add(pageHost,0,2);
   pages=new[]{new Panel(),new Panel(),new Panel(),new Panel()}; foreach(var page in pages) { page.Dock=DockStyle.Fill; page.Visible=false; pageHost.Controls.Add(page); }
   BuildDevicesPage(pages[0]); BuildTextPage(pages[1]); BuildFilesPage(pages[2]); BuildSettingsPage(pages[3]);
   notice=Label("等待设备连接",9); notice.Dock=DockStyle.Fill; notice.AutoEllipsis=true; notice.AutoSize=false; notice.TextAlign=ContentAlignment.MiddleLeft; shell.Controls.Add(notice,0,3); SelectPage(0);
  }
  void SelectPage(int index) {
   if(pages==null) return; for(int i=0;i<pages.Length;i++) { pages[i].Visible=i==index; navigation[i].BackColor=i==index?accent:Color.FromArgb(28,47,62); navigation[i].ForeColor=i==index?Color.White:Color.FromArgb(200,216,225); }
   string[] titles={"设备","剪贴板同步","文件互传","设置与排查"}; string[] subtitles={"选择电脑或手机；程序优先尝试直连。","文字、截图和复制的文件，都能从这里共享。","选好收件设备，复制后在本页 Ctrl+V 直接发送。","管理最近电脑、已配对设备和运行选项。"};
   pageTitle.Text=titles[index]; pageSubtitle.Text=subtitles[index]; pages[index].BringToFront();
  }
  void BuildDevicesPage(Panel page) {
   var grid=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5}; connectionLayout=grid;
   grid.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,0)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,52)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,34)); grid.RowStyles.Add(new RowStyle(SizeType.Percent,100)); page.Controls.Add(grid);
   deviceHint=Label("选择连接方式；已配对设备可到设置管理。",9); deviceHint.AutoSize=false; deviceHint.Dock=DockStyle.Fill; grid.Controls.Add(deviceHint,0,0);
   recentArea=new Panel {Dock=DockStyle.Fill,Visible=false}; recentArea.Controls.Add(Label("最近电脑 · 点击即可重连",9,true)); recentButtons=new TableLayoutPanel {Dock=DockStyle.Bottom,Height=48,ColumnCount=3,RowCount=1,Margin=new Padding(0)}; for(int i=0;i<3;i++) recentButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f)); recentArea.Controls.Add(recentButtons); grid.Controls.Add(recentArea,0,1);
   var choices=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)}; for(int i=0;i<2;i++) choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); grid.Controls.Add(choices,0,2);
   string[] names={"连接电脑","连接手机"}; connectionModes=new Button[2];
   for(int i=0;i<2;i++) { int index=i==0?0:2; var button=Button(names[i],delegate { SelectConnectionMode(index); }); button.AutoSize=false; button.MinimumSize=Size.Empty; button.Dock=DockStyle.Fill; connectionModes[i]=button; choices.Controls.Add(button,i,0); }
   connectionHelp=Label("",10); connectionHelp.Dock=DockStyle.Fill; connectionHelp.AutoSize=false; grid.Controls.Add(connectionHelp,0,3);
   var host=new Panel {Dock=DockStyle.Fill}; grid.Controls.Add(host,0,4); connectionPages=new[]{new Panel(),new Panel(),new Panel(),new Panel()};
   foreach(var pane in connectionPages) { pane.Dock=DockStyle.Fill; pane.Visible=false; host.Controls.Add(pane); }
   BuildLocalConnection(connectionPages[0]); BuildRemoteConnection(connectionPages[1]); BuildPhoneConnection(connectionPages[2]); BuildNearbyConnection(connectionPages[3]); SelectConnectionMode(0);
  }
  void SelectConnectionMode(int index) {
   for(int i=0;i<connectionPages.Length;i++) connectionPages[i].Visible=i==index;
   for(int i=0;i<connectionModes.Length;i++) { bool selected=i==(index==2?1:0); connectionModes[i].BackColor=selected?accent:Color.White; connectionModes[i].ForeColor=selected?Color.White:ink; }
   string[] help={"查找电脑或粘贴邀请，无需区分有线、Wi-Fi 或路由器层级。","一端创建邀请，另一端粘贴加入；优先直连，必要时尝试互联网入口。","手机主动打开电脑地址；首次在电脑允许一次。","可使用系统热点；Windows 电脑之间也可通过系统蓝牙传文件。"};
   connectionHelp.Text=help[index]; connectionPages[index].BringToFront();
  }
  void BuildLocalConnection(Panel page) {
   var connect=Vertical(); page.Controls.Add(connect);
   connect.Controls.Add(Label("添加或连接电脑",15,true));
   var search=new TableLayoutPanel {Width=650,Height=46,ColumnCount=3,RowCount=1,Margin=new Padding(0)}; for(int i=0;i<3;i++) search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));
   scanButton=Button("自动查找电脑",ScanComputers,true); var entryButtons=new[]{scanButton,Button("创建／加入邀请",delegate {SelectConnectionMode(1);SelectRemoteStep(0);}),Button("蓝牙传文件",OpenBluetoothTransfer)};
   for(int i=0;i<entryButtons.Length;i++) {entryButtons[i].Dock=DockStyle.Fill;entryButtons[i].AutoSize=false;entryButtons[i].MinimumSize=Size.Empty;search.Controls.Add(entryButtons[i],i,0);} connect.Controls.Add(search);
   scanStatus=Label("两台电脑都运行轻桥。只需一端发起连接。",9); connect.Controls.Add(scanStatus);
   nearby=new ListBox {Width=650,Height=68,IntegralHeight=false}; nearby.SelectedIndexChanged+=delegate { if(nearby.SelectedItem is NearbyComputer) peerAddress.Text=((NearbyComputer)nearby.SelectedItem).url; }; nearby.DoubleClick+=delegate { if(nearby.SelectedItem!=null) Run(ConnectComputer); }; connect.Controls.Add(nearby);
   connect.Controls.Add(Label("粘贴对方邀请，或使用上面选中的电脑地址",11,true)); peerAddress=new TextBox {Width=650,Height=70,Multiline=true,ScrollBars=ScrollBars.Vertical,Text=state.Prefs.peer!=null && state.Prefs.peer.url.StartsWith("http://")?state.Prefs.peer.url:"",Name="PeerAddress"}; connect.Controls.Add(peerAddress);
   connectClipboard=new CheckBox {AutoSize=true,Text="连接后自动同步新复制的文字",Checked=true,Margin=new Padding(0,8,0,4)}; connect.Controls.Add(connectClipboard);
   var actions=Row(); actions.Controls.Add(Button("连接电脑",ConnectComputer,true)); actions.Controls.Add(Button("取消连接",delegate { state.Peer.Disconnect(); Feedback("已取消本机发起的连接。已授权设备可在设置中管理。"); })); connect.Controls.Add(actions);
   peerStatus=Label("尚未连接",10,true); connect.Controls.Add(peerStatus);
   connect.Controls.Add(Label("没找到时，请使用对方创建的邀请；家里有多台路由器也不必判断网络类型。蓝牙按钮打开 Windows 系统文件传输，不属于自动剪贴板同步。",9));
  }
  void BuildRemoteConnection(Panel page) {
   var grid=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2}; grid.RowStyles.Add(new RowStyle(SizeType.Absolute,54)); grid.RowStyles.Add(new RowStyle(SizeType.Percent,100)); page.Controls.Add(grid);
   var choices=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)}; choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); grid.Controls.Add(choices,0,0);
   remoteModeButtons=new[]{Button("我来发邀请",delegate { SelectRemoteStep(0); }),Button("我有邀请，加入连接",delegate { SelectRemoteStep(1); })};
   for(int i=0;i<remoteModeButtons.Length;i++) { var button=remoteModeButtons[i]; button.AutoSize=false; button.MinimumSize=Size.Empty; button.Dock=DockStyle.Fill; choices.Controls.Add(button,i,0); }
   var host=new Panel {Dock=DockStyle.Fill}; grid.Controls.Add(host,0,1); remoteSteps=new[]{new Panel(),new Panel()}; foreach(var pane in remoteSteps) { pane.Dock=DockStyle.Fill; pane.Visible=false; host.Controls.Add(pane); }
   var send=Vertical(); remoteSteps[0].Controls.Add(send);
   send.Controls.Add(Label("复制本机邀请，另一台粘贴加入",15,true));
   send.Controls.Add(Label("邀请会提供多个可尝试的地址，程序优先直连。无需判断是否同一网段、哪台路由器或有线／Wi-Fi。",9));
   remoteInvitation=new TextBox {Width=650,Height=66,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text="点击下方复制邀请。",Name="OutgoingInvitation"}; send.Controls.Add(remoteInvitation);
   inviteShareClipboard=new CheckBox {Text="同时开启文字自动同步（包含新复制的敏感文字）",AutoSize=true,Checked=true}; send.Controls.Add(inviteShareClipboard);
   inviteCopyButton=Button("复制连接邀请",CreateRemoteInvitation,true); send.Controls.Add(inviteCopyButton);
   remoteInviteHint=Label("对方打开轻桥 → 连接电脑，粘贴邀请即可尝试。",9); send.Controls.Add(remoteInviteHint);
   send.Controls.Add(Label("直连未成功时：准备互联网入口",12,true));
   remoteToggle=Button("准备互联网入口",delegate { state.ToggleRemote(); }); send.Controls.Add(remoteToggle);
   remoteStatus=Label("尚未准备互联网入口",10,true); send.Controls.Add(remoteStatus);
   send.Controls.Add(Label("如有需要，准备后再复制更新的邀请。自动等待和重试，无需反复点击；临时服务仍不保证所有网络可达。双方无需服务器或账号。",9));
   var qrToggle=new CheckBox {AutoSize=true,Text="手机通过互联网加入：展开二维码"}; send.Controls.Add(qrToggle);
   remoteQr=new PictureBox {Width=180,Height=180,SizeMode=PictureBoxSizeMode.Zoom,Visible=false}; qrToggle.CheckedChanged+=delegate { remoteQr.Visible=qrToggle.Checked; }; send.Controls.Add(remoteQr);
   send.Controls.Add(Label("公共临时入口 30 分钟后关闭；邀请 5 分钟内一次有效。流量经 Cloudflare，非端到端加密，首次下载约 53 MB 模块，跨网上传单文件限 90 MB。",9));
   var join=Vertical(); remoteSteps[1].Controls.Add(join);
   join.Controls.Add(Label("粘贴另一台电脑发来的邀请",15,true)); join.Controls.Add(Label("可粘贴整段电脑邀请或旧版 HTTPS 链接。新邀请会自动尝试直连和其中可用的互联网入口。",10));
   remoteAddress=new TextBox {Width=650,Height=90,Multiline=true,ScrollBars=ScrollBars.Vertical,Name="RemoteInvitation"}; join.Controls.Add(remoteAddress);
   remoteConnectClipboard=new CheckBox {AutoSize=true,Text="连接后自动同步新复制的文字",Checked=true,Margin=new Padding(0,10,0,8)}; join.Controls.Add(remoteConnectClipboard);
   var actions=Row(); actions.Controls.Add(Button("粘贴邀请并连接",PasteRemoteInvitation,true)); actions.Controls.Add(Button("连接输入的邀请",JoinRemoteInvitation)); join.Controls.Add(actions);
   remoteJoinStatus=Label("等待邀请。这里直接连接电脑，不需要摄像头。",10,true); join.Controls.Add(remoteJoinStatus);
   join.Controls.Add(Label("2. 连接成功后，复制新文字即可在另一台 Ctrl+V；发送文件时到“文件”页选择对方电脑。",10));
   join.Controls.Add(Button("取消本机发起的连接",delegate { state.Peer.Disconnect(); Feedback("已取消本机发起的连接。"); }));
   SelectRemoteStep(state.Prefs.peer!=null && state.Prefs.peer.url.StartsWith("https://")?1:0);
  }
  void SelectRemoteStep(int index) {
   for(int i=0;i<remoteSteps.Length;i++) { remoteSteps[i].Visible=i==index; remoteModeButtons[i].BackColor=i==index?accent:Color.White; remoteModeButtons[i].ForeColor=i==index?Color.White:ink; }
   remoteSteps[index].BringToFront();
  }
  void BuildPhoneConnection(Panel page) {
   var flow=Vertical(); page.Controls.Add(flow);
   flow.Controls.Add(Label("1. 在 iPhone Safari 打开本机地址",14,true)); localAddress=new TextBox {Width=650,ReadOnly=true}; flow.Controls.Add(localAddress);
   flow.Controls.Add(Label("2. 手机点击连接，在电脑核对识别号并允许。首次只需输入一次地址，同网无需扫码。",10));
   var tools=Row(); tools.Controls.Add(Button("复制固定地址",delegate { CopyText(localAddress.Text,"固定地址已复制。"); })); tools.Controls.Add(Button("刷新本机地址",delegate { RefreshAddresses(); Feedback("已刷新当前网络地址。"); })); flow.Controls.Add(tools);
   var toggle=new CheckBox {AutoSize=true,Text="地址打不开？展开备用 IP、配对码和二维码"}; flow.Controls.Add(toggle);
   var fallback=new FlowLayoutPanel {AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Visible=false}; toggle.CheckedChanged+=delegate { fallback.Visible=toggle.Checked; }; flow.Controls.Add(fallback);
   addresses=new ComboBox {Width=400,DropDownStyle=ComboBoxStyle.DropDownList}; fallback.Controls.Add(addresses);
   var actions=Row(); actions.Controls.Add(Button("复制备用 IP 地址",delegate { if(addresses.SelectedItem==null) throw new ApiError(400,"未找到局域网 IPv4，请先连接 Wi-Fi 或热点。"); CopyText("http://"+addresses.SelectedItem+":"+state.Port+"/","备用地址已复制。"); })); actions.Controls.Add(Button("刷新配对码",delegate { state.RefreshPairing(); RefreshView(); })); fallback.Controls.Add(actions);
   pairCode=Label("",10,true); fallback.Controls.Add(pairCode); localQr=new PictureBox {Width=180,Height=180,SizeMode=PictureBoxSizeMode.Zoom}; fallback.Controls.Add(localQr);
   flow.Controls.Add(Label("3. 日常少操作：手机网页的“设置与帮助”可配置一个背面快捷指令，文字收发使用同一个手势。",10));
  }
  void BuildNearbyConnection(Panel page) {
   var flow=Vertical(); page.Controls.Add(flow);
   flow.Controls.Add(Label("设备就在旁边，优先用热点",15,true)); flow.Controls.Add(Label("不需要服务器。选择下列任一种热点，让两台设备处在同一网络，再返回同网直连。",10));
   flow.Controls.Add(Label("用 iPhone 热点",12,true)); flow.Controls.Add(Label("iPhone 设置 → 个人热点 → 允许其他人加入；电脑从 Wi-Fi 列表连接这个热点。按系统提示输入一次密码。",10));
   flow.Controls.Add(Label("用 Windows 热点",12,true)); flow.Controls.Add(Button("打开 Windows 移动热点设置",delegate { if(test) { Feedback("测试模式不打开或修改系统热点设置。"); return; } Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") {UseShellExecute=true}); })); flow.Controls.Add(Label("在系统中开启热点，另一台电脑或 iPhone 通过 Wi-Fi 加入。能否开启取决于网卡和系统支持。",10));
   flow.Controls.Add(Button("已连上热点，查找同网电脑",delegate { RefreshAddresses(); SelectConnectionMode(0); ScanComputers(); },true));
   flow.Controls.Add(Label("蓝牙是附近联网的备用方式：iPhone 开启个人热点后，Windows 还需配对并加入蓝牙个人区域网（PAN）。配对本身不等于已连通；本版未实现自动蓝牙组网。不在附近时，请使用“不同网络”。",9));
  }
  void BuildTextPage(Panel page) {
   var flow=Vertical(); page.Controls.Add(flow); flow.Controls.Add(Label("自动同步",17,true));
   auto=new CheckBox {AutoSize=true,Text="自动同步文字（电脑之间双向，向手机共享）",Checked=state.Prefs.autoClipboard,Margin=new Padding(0,10,0,6),Name="AutoClipboard"}; auto.CheckedChanged+=delegate { if(refreshing) return; Run(delegate { SetAuto(auto.Checked); Feedback(auto.Checked?"已开启，之后新复制的文字将自动同步。":"电脑文字自动同步已关闭；手机手动收发仍可使用。"); }); }; flow.Controls.Add(auto);
   flow.Controls.Add(Label("开启后，新复制的文字（包括敏感文字）会提供给已配对设备。关闭后停止电脑自动收发；手机主动运行快捷指令仍可收发。",9));
   clipboardLocal=Label("",10,true); clipboardPeer=Label("",10,true); flow.Controls.Add(clipboardLocal); flow.Controls.Add(clipboardPeer);
   var actions=Row(); actions.Controls.Add(Button("共享当前文字",delegate { Publish(true); },true)); actions.Controls.Add(Button("清空共享文字",delegate { state.ClearClipboard(); Feedback("共享区已清空；设备本来的剪贴板保留。"); })); flow.Controls.Add(actions);
   autoAttachments=new CheckBox {AutoSize=true,Text="自动同步图片和复制的文件（仅已连接电脑）",Checked=state.Prefs.attachmentClipboard,Margin=new Padding(0,12,0,6)}; autoAttachments.CheckedChanged+=delegate {if(!refreshing) Run(delegate {SetAttachmentAuto(autoAttachments.Checked);});}; flow.Controls.Add(autoAttachments);
   flow.Controls.Add(Label("两端各开启一次，之后截图／复制图片／在资源管理器复制文件，另一台可在支持图片或附件的应用直接粘贴。关闭时仍可在文件页 Ctrl+V 发送，接收内容先保存到 Received。",9));
   attachmentStatus=Label("等待图片或文件",10,true); flow.Controls.Add(attachmentStatus);
   flow.Controls.Add(Label("最近的共享文字",11,true)); preview=ReadBox(80); preview.Name="ClipboardPreview"; flow.Controls.Add(preview);
   flow.Controls.Add(Label("电脑 ↔ 电脑",11,true)); flow.Controls.Add(Label("连接后复制新文字，另一台直接 Ctrl+V。若未收到，先看上方同步状态，再到设备页检查连接。",10));
   flow.Controls.Add(Label("电脑 ↔ iPhone",11,true)); flow.Controls.Add(Label("两边都用同一个“轻桥同步”背面手势。手机首次在网页设置中创建快捷指令；每次复制后轻点背面，再在原 App 粘贴。网页打开不等于后台自动接收。自动接收仅处理两分钟内的新文字，超时请重新复制。",10));
  }
  void BuildFilesPage(Panel page) {
   var grid=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5}; grid.RowStyles.Add(new RowStyle(SizeType.Absolute,142)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,56)); grid.RowStyles.Add(new RowStyle(SizeType.Percent,100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,34)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,0)); page.Controls.Add(grid);
   var send=new TableLayoutPanel {Dock=DockStyle.Fill,BackColor=Color.White,ColumnCount=1,RowCount=4,Padding=new Padding(12,4,12,4)}; send.RowStyles.Add(new RowStyle(SizeType.Absolute,26)); send.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); send.RowStyles.Add(new RowStyle(SizeType.Absolute,32)); send.RowStyles.Add(new RowStyle(SizeType.Percent,100)); grid.Controls.Add(send,0,0);
   send.Controls.Add(Label("收件位置",11,true),0,0); var sending=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1}; sending.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); sending.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
   fileTargets=new ComboBox {Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,Name="FileTarget",Margin=new Padding(0,4,8,4)}; fileTargets.SelectedIndexChanged+=delegate { if(!refreshing) { fileTargetChosen=true; RefreshView(); } }; sending.Controls.Add(fileTargets,0,0); sending.Controls.Add(Button("＋ 发送文件",ChooseFiles,true),1,0); send.Controls.Add(sending,0,1);
   fileTargetHint=Label("",9); fileTargetHint.AutoSize=false; fileTargetHint.Dock=DockStyle.Fill; send.Controls.Add(fileTargetHint,0,2); fileStatus=Label("",9,true); fileStatus.AutoSize=false; fileStatus.AutoEllipsis=true; fileStatus.Dock=DockStyle.Fill; send.Controls.Add(fileStatus,0,3);
   var tools=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=new Padding(0)}; for(int i=0;i<4;i++) tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
   var fileActions=new[]{Button("粘贴并发送  Ctrl+V",PasteClipboardContent,true),Button("复制选中内容",CopySelectedAttachments),Button("打开接收文件夹",delegate {Process.Start("explorer.exe",state.Received);}),Button("移除记录",RemoveSelectedFile)};
   for(int i=0;i<fileActions.Length;i++) {fileActions[i].Dock=DockStyle.Fill;fileActions[i].AutoSize=false;fileActions[i].MinimumSize=Size.Empty;tools.Controls.Add(fileActions[i],i,0);} grid.Controls.Add(tools,0,1);
   files=List("文件","结果","收件／来源","大小","时间"); files.Name="TransferHistory"; files.DoubleClick+=delegate { Run(LocateFile); }; grid.Controls.Add(files,0,2);
   var more=new CheckBox {Text="查看对方共享区的旧文件（按需下载）",AutoSize=true}; grid.Controls.Add(more,0,3);
   var oldFiles=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Visible=false}; oldFiles.RowStyles.Add(new RowStyle(SizeType.Percent,100)); oldFiles.RowStyles.Add(new RowStyle(SizeType.Absolute,42)); grid.Controls.Add(oldFiles,0,4);
   peerFiles=List("对方共享文件","大小"); oldFiles.Controls.Add(peerFiles,0,0); oldFiles.Controls.Add(Button("下载选中文件",delegate { if(peerFiles.SelectedItems.Count==0) throw new ApiError(400,"请选中文件，或先在设备页连接电脑。"); state.Peer.Download((PeerFile)peerFiles.SelectedItems[0].Tag); Feedback("已加入下载队列，请查看传输结果。"); }),0,1);
   more.CheckedChanged+=delegate { oldFiles.Visible=more.Checked; grid.RowStyles[4].Height=more.Checked?145:0; };
  }
  void LocateFile() { if(files.SelectedItems.Count==0) throw new ApiError(400,"请先选中文件记录"); var file=(Transfer)files.SelectedItems[0].Tag; if(!System.IO.File.Exists(file.path)) throw new ApiError(404,"本机文件已被移动或删除"); Process.Start("explorer.exe","/select,\""+file.path+"\""); }
  void BuildSettingsPage(Panel page) {
   var flow=Vertical(); page.Controls.Add(flow);
   flow.Controls.Add(Label("历史电脑 · 选择后重新连接",14,true)); historyDevices=List("电脑","入口","最近连接","地址"); historyDevices.Dock=DockStyle.None; historyDevices.Width=650; historyDevices.Height=130; historyDevices.DoubleClick+=delegate {Run(ReconnectSelectedHistory);}; flow.Controls.Add(historyDevices);
   var historyActions=Row(); historyActions.Controls.Add(Button("重新连接",ReconnectSelectedHistory,true)); historyActions.Controls.Add(Button("移除历史",delegate {if(historyDevices.SelectedItems.Count>0) {state.ForgetRecentPeer(historyDevices.SelectedItems[0].Name);recentViewKey="!";RefreshView();}})); flow.Controls.Add(historyActions);
   flow.Controls.Add(Label("已配对设备",14,true));
   devices=List("设备","类型","状态","最近联系"); devices.Name="PairedDevices"; devices.Dock=DockStyle.None; devices.Width=650; devices.Height=140; flow.Controls.Add(devices);
   flow.Controls.Add(Button("断开／移除选中的设备",DisconnectSelected));
   flow.Controls.Add(Label("相同设备名可能来自不同浏览器入口。移除后，该入口和使用同一令牌的快捷指令需要重新配对。",9));
   flow.Controls.Add(Label("运行选项",14,true)); pause=new CheckBox {AutoSize=true,Text="暂停全部互传",Margin=new Padding(0,8,0,10)}; pause.CheckedChanged+=delegate { if(refreshing) return; state.Paused=pause.Checked; state.Changed(); }; flow.Controls.Add(pause);
   var startup=new CheckBox {AutoSize=true,Text="登录 Windows 时启动轻桥",Margin=new Padding(0,0,0,10)};
   if(!test) try { using(var key=Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) startup.Checked=key!=null && key.GetValue("LanBridge")!=null; } catch { startup.Enabled=false; }
   startup.CheckedChanged+=delegate { Run(delegate { if(test) { Feedback("测试模式不更改登录启动设置"); return; } using(var key=Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) { if(startup.Checked) key.SetValue("LanBridge","\""+Application.ExecutablePath+"\" --port "+state.Port+" --background"); else key.DeleteValue("LanBridge",false); } Feedback("登录启动设置已保存。"); }); }; flow.Controls.Add(startup);
   flow.Controls.Add(Button("添加文件右键“发送到轻桥”入口",delegate { if(test) { Feedback("测试模式不更改右键菜单"); return; } DesktopCommands.InstallSendTo(Application.ExecutablePath,state.Port); Feedback("已添加文件右键 → 发送到 → 轻桥入口，文件进入手机／浏览器共享区。"); }));
   flow.Controls.Add(Label("关闭窗口后仍驻留托盘。Alt+Shift+C 共享当前文字，Alt+Shift+O 打开窗口。",9));
   flow.Controls.Add(Label("连接检查",14,true)); flow.Controls.Add(Button("刷新地址并检查",CheckConnection)); diagnostics=Label("连接不上时先运行检查；不会修改防火墙或网络设置。",9); flow.Controls.Add(diagnostics);
   flow.Controls.Add(Label("最近结果",14,true)); flow.Controls.Add(Label("仅在本次运行中保留状态，不记录剪贴板正文和配对密钥。",9)); activityLog=ReadBox(190); flow.Controls.Add(activityLog);
   flow.Controls.Add(Label("局域网 HTTP 仅用于可信网络。跨网请从设备页开启临时 HTTPS 通道，局域网地址无法直接跨网访问。",9));
  }
 }
}
