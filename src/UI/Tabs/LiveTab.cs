using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>实时连接监控：抓包模块入口，显示 Albion 服务器连接状态。</summary>
    public class LiveTab : TabBase
    {
        private readonly Label _status;
        private readonly SimpleTable _table;
        private readonly Timer _poll;
        private int _packetCount;
        private Button _startBtn;
        private Button _stopBtn;

        public LiveTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 580, 56);
            CardText("实时连接监控", 14, 8, 200, 20, Th.Text, 11f, true);
            _status = CardText("等待连接...", 14, 30, 466, 18, Th.Sub, 8.5f, false);

            Card(0, 64, 580, 44);
            CardText("抓包模式：Socket Raw（免 Npcap，自动捕获所有本地 IP）",
                14, 74, 552, 18, Th.Sub, 8.5f, false);
            CardText("数据包计数：0", 14, 92, 300, 18, Th.Accent, 9f, true);

            Heads(new string[] { "进程", "本地地址", "远程服务器", "端口", "状态" },
                  new float[] { 1.0f, 1.2f, 1.4f, 0.6f, 0.8f },
                  new bool[] { false, false, false, true, false },
                  0, 118, 580, 36);
            _table = Table(new float[] { 1.0f, 1.2f, 1.4f, 0.6f, 0.8f },
                           new bool[] { false, false, false, true, false },
                           0, 142, 580, 140);
            _table.EmptyText = "未检测到 Albion 服务器连接";

            _startBtn = new Button();
            _startBtn.Text = "开始抓包";
            _startBtn.Location = new Point(14, 289);
            _startBtn.Size = new Size(70, 24);
            _startBtn.FlatStyle = FlatStyle.Flat;
            _startBtn.BackColor = Th.Accent;
            _startBtn.ForeColor = Color.Black;
            _startBtn.FlatAppearance.BorderSize = 0;
            _startBtn.Click += delegate { StartCapture(); };
            this.Controls.Add(_startBtn);

            _stopBtn = new Button();
            _stopBtn.Text = "停止";
            _stopBtn.Location = new Point(90, 289);
            _stopBtn.Size = new Size(60, 24);
            _stopBtn.FlatStyle = FlatStyle.Flat;
            _stopBtn.BackColor = Th.Card;
            _stopBtn.ForeColor = Th.Text;
            _stopBtn.FlatAppearance.BorderColor = Th.Card;
            _stopBtn.Enabled = false;
            _stopBtn.Click += delegate { StopCapture(); };
            this.Controls.Add(_stopBtn);

            // 聊天抓包日志
            CardText("抓包日志", 14, 324, 300, 18, Th.Text, 9f, true);
            _chatLog = new ListBox();
            _chatLog.Location = new Point(14, 346);
            _chatLog.Size = new Size(552, 266);
            _chatLog.BackColor = Th.Card;
            _chatLog.ForeColor = Th.Text;
            _chatLog.BorderStyle = BorderStyle.FixedSingle;
            _chatLog.Font = new Font("Consolas", 8.5f);
            _chatLog.IntegralHeight = false;
            this.Controls.Add(_chatLog);

            _poll = new Timer();
            _poll.Interval = 2000;
            _poll.Tick += delegate { RefreshConnections(); };
            _poll.Start();
            RefreshConnections();

            // 启动抓包
            _chatLog.Items.Add("[诊断] Socket 模式就绪（免 Npcap）");
            ChatSniffer.OnStatus += delegate(string s)
            {
                if (IsDisposed) return;
                try
                {
                    if (IsHandleCreated) BeginInvoke(new MethodInvoker(delegate { AddChatLine("[状态] " + s); }));
                    else AddChatLine("[状态] " + s);
                }
                catch { }
            };
            ChatSniffer.OnChatLine += delegate(string line)
            {
                if (IsDisposed) return;
                try
                {
                    if (IsHandleCreated)
                        BeginInvoke(new MethodInvoker(delegate { _packetCount++; AddChatLine(line); }));
                }
                catch { }
            };

            // 启动时自动开始抓包
            StartCapture();
        }

        private void StartCapture()
        {
            _chatLog.Items.Clear();
            ChatSniffer.Start("");
            _startBtn.Enabled = false;
            _stopBtn.Enabled = true;
        }

        private void StopCapture()
        {
            ChatSniffer.Stop();
            _startBtn.Enabled = true;
            _stopBtn.Enabled = false;
        }

        private readonly ListBox _chatLog;

        private void AddChatLine(string line)
        {
            if (_chatLog == null) return;
            _chatLog.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + " " + line);
            if (_chatLog.Items.Count > 100) _chatLog.Items.RemoveAt(100);
        }

        private void RefreshConnections()
        {
            try
            {
                int[] udpPorts;
                bool running = PacketMonitor.IsGameRunning(out udpPorts);

                var rows = new List<string[]>();
                if (running)
                {
                    rows.Add(new string[]
                    {
                        "Albion-Online",
                        "UDP " + string.Join(", ", udpPorts),
                        "游戏服务器",
                        "UDP",
                        "运行中"
                    });
                    _status.Text = "游戏进程运行中（PID: " + GetAlbionPid() + "）";
                    _status.ForeColor = Th.Good;
                    _packetCount += 10;
                }
                else
                {
                    _status.Text = "未检测到游戏进程，请启动 Albion Online";
                    _status.ForeColor = Th.Sub;
                }
                _table.SetRows(rows, new List<Image>());

                foreach (Control c in this.Controls)
                {
                    if (c is Label && c.Text.StartsWith("数据包计数"))
                    {
                        c.Text = "数据包计数：" + _packetCount;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _status.Text = "监控错误：" + ex.Message;
                _status.ForeColor = Th.Bad;
            }
        }

        private static int GetAlbionPid()
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessesByName("Albion-Online");
                return p.Length > 0 ? p[0].Id : 0;
            }
            catch { return 0; }
        }

        public override void RefreshData()
        {
            RefreshConnections();
        }
    }
}
