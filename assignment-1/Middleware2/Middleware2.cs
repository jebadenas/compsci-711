using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

public class Program
{
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MiddlewareForm());
    }
}

public class QueueEntry
{
    public string MsgId;
    public string Text;
    public int TsValue;
    public int TsSenderId;
    public bool IsFinal;
}

public class MiddlewareForm : Form
{
    // ====== Per-instance constants ======
    const int MIDDLEWARE_ID = 2;
    const int MY_PORT       = 8083;
    // ====================================

    const int NUM_MIDDLEWARE = 5;
    const int BASE_PORT      = 8081;
    const int NETWORK_PORT   = 8081;
    const string HOST        = "localhost";

    Button  btnSend;
    ListBox lstSent, lstReceived, lstReady;

    int  msgCounter = 0;
    readonly object stateLock = new object();
    List<QueueEntry>                             holdQueue = new List<QueueEntry>();
    Dictionary<string, List<(int val, int sid)>> proposals = new Dictionary<string, List<(int, int)>>();

    TcpListener listener;

    public MiddlewareForm()
    {
        InitializeUI();
        listener = new TcpListener(IPAddress.Any, MY_PORT);
        listener.Start();
        _ = AcceptLoopAsync();
    }

    void InitializeUI()
    {
        this.Text            = $"Middleware {MIDDLEWARE_ID}";
        this.Size            = new System.Drawing.Size(720, 420);
        this.FormBorderStyle = FormBorderStyle.FixedSingle;

        btnSend = new Button
        {
            Text     = "Send",
            Location = new System.Drawing.Point(20, 20),
            Size     = new System.Drawing.Size(80, 30)
        };
        btnSend.Click += OnSendClick;

        var lblSent     = new Label { Text = "Sent",     Location = new System.Drawing.Point(20,  65), AutoSize = true };
        var lblReceived = new Label { Text = "Received", Location = new System.Drawing.Point(240, 65), AutoSize = true };
        var lblReady    = new Label { Text = "Ready",    Location = new System.Drawing.Point(470, 65), AutoSize = true };

        lstSent     = new ListBox { Location = new System.Drawing.Point(20,  85), Size = new System.Drawing.Size(200, 280), HorizontalScrollbar = true };
        lstReceived = new ListBox { Location = new System.Drawing.Point(240, 85), Size = new System.Drawing.Size(210, 280), HorizontalScrollbar = true };
        lstReady    = new ListBox { Location = new System.Drawing.Point(470, 85), Size = new System.Drawing.Size(210, 280), HorizontalScrollbar = true };

        Controls.AddRange(new Control[] {
            btnSend,
            lblSent, lblReceived, lblReady,
            lstSent, lstReceived, lstReady
        });
    }

    async void OnSendClick(object sender, EventArgs e)
    {
        int counter;
        lock (stateLock) { counter = ++msgCounter; }

        string msgId = $"{MIDDLEWARE_ID}:{counter}";
        string text  = $"Msg #{counter} from Middleware {MIDDLEWARE_ID}";
        string wire  = $"MULTICAST|{msgId}|{text}";

        try
        {
            using var client = new TcpClient(HOST, NETWORK_PORT);
            byte[] data = Encoding.UTF8.GetBytes(wire);
            await client.GetStream().WriteAsync(data, 0, data.Length);
            this.Invoke((Action)(() => lstSent.Items.Add(text)));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reach network: {ex.Message}");
        }
    }

    async Task AcceptLoopAsync()
    {
        while (true)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = HandleConnectionAsync(client);
            }
            catch { }
        }
    }

    async Task HandleConnectionAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                byte[] buf = new byte[1024];
                int n = await client.GetStream().ReadAsync(buf, 0, buf.Length);
                string msg = Encoding.UTF8.GetString(buf, 0, n).Trim('\0');
                ProcessMessage(msg);
            }
        }
        catch { }
    }

    void ProcessMessage(string msg)
    {
        string[] parts = msg.Split('|');
        if (parts.Length < 2) return;

        switch (parts[0])
        {
            case "MULTICAST" when parts.Length >= 3:
                HandleMulticast(parts[1], parts[2]);
                break;
            case "PROPOSE" when parts.Length >= 4:
                HandlePropose(parts[1], int.Parse(parts[2]), int.Parse(parts[3]));
                break;
            case "FINAL" when parts.Length >= 4:
                HandleFinal(parts[1], int.Parse(parts[2]), int.Parse(parts[3]));
                break;
        }
    }

    void HandleMulticast(string msgId, string text)
    {
        int proposed;
        lock (stateLock)
        {
            int maxInQueue = holdQueue.Count > 0 ? holdQueue.Max(e => e.TsValue) : 0;
            proposed = maxInQueue + 1;
            holdQueue.Add(new QueueEntry
            {
                MsgId      = msgId,
                Text       = text,
                TsValue    = proposed,
                TsSenderId = MIDDLEWARE_ID,
                IsFinal    = false
            });
        }

        this.Invoke((Action)(() =>
            lstReceived.Items.Add($"{text} | proposed:({proposed},{MIDDLEWARE_ID})")
        ));

        int originId   = int.Parse(msgId.Split(':')[0]);
        int originPort = BASE_PORT + originId;
        _ = SendControlAsync(originPort, $"PROPOSE|{msgId}|{proposed}|{MIDDLEWARE_ID}");
    }

    void HandlePropose(string msgId, int value, int senderID)
    {
        bool allReceived = false;
        int  agreedVal   = 0;
        int  agreedSid   = 0;

        lock (stateLock)
        {
            if (!proposals.ContainsKey(msgId))
                proposals[msgId] = new List<(int, int)>();

            proposals[msgId].Add((value, senderID));

            if (proposals[msgId].Count == NUM_MIDDLEWARE)
            {
                allReceived = true;
                foreach (var (v, s) in proposals[msgId])
                {
                    if (v > agreedVal || (v == agreedVal && s > agreedSid))
                    {
                        agreedVal = v;
                        agreedSid = s;
                    }
                }
                proposals.Remove(msgId);
            }
        }

        if (allReceived)
        {
            string finalMsg = $"FINAL|{msgId}|{agreedVal}|{agreedSid}";
            for (int i = 1; i <= NUM_MIDDLEWARE; i++)
                _ = SendControlAsync(BASE_PORT + i, finalMsg);
        }
    }

    void HandleFinal(string msgId, int value, int senderID)
    {
        List<QueueEntry> toDeliver = new List<QueueEntry>();

        lock (stateLock)
        {
            var entry = holdQueue.FirstOrDefault(e => e.MsgId == msgId);
            if (entry == null) return;

            entry.TsValue    = value;
            entry.TsSenderId = senderID;
            entry.IsFinal    = true;

            holdQueue.Sort((a, b) =>
                a.TsValue != b.TsValue
                    ? a.TsValue.CompareTo(b.TsValue)
                    : a.TsSenderId.CompareTo(b.TsSenderId));

            while (holdQueue.Count > 0 && holdQueue[0].IsFinal)
            {
                toDeliver.Add(holdQueue[0]);
                holdQueue.RemoveAt(0);
            }
        }

        this.Invoke((Action)(() =>
        {
            foreach (var e in toDeliver)
                lstReady.Items.Add($"{e.Text} | final:({e.TsValue},{e.TsSenderId})");
        }));
    }

    async Task SendControlAsync(int port, string msg)
    {
        try
        {
            using var client = new TcpClient(HOST, port);
            byte[] data = Encoding.UTF8.GetBytes(msg);
            await client.GetStream().WriteAsync(data, 0, data.Length);
        }
        catch { }
    }
}
