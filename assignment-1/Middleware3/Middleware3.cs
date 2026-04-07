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

// ====== Per-instance constants ======
// Middleware 1 → ID=1, Port=8082
// ====================================

public class MiddlewareForm : Form
{
    const int MIDDLEWARE_ID  = 3;
    const int MY_PORT        = 8084;
    const int NUM_MIDDLEWARE  = 5;
    const int BASE_PORT      = 8081;
    const int NETWORK_PORT   = 8081;
    const string HOST        = "localhost";

    Button  btnSend;
    ListBox lstSent, lstReceived, lstReady;

    readonly TotalOrderProtocol protocol = new TotalOrderProtocol(MIDDLEWARE_ID, NUM_MIDDLEWARE, BASE_PORT);
    readonly object protocolLock = new object();
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
        MulticastResult result;
        lock (protocolLock) { result = protocol.CreateMulticast(); }

        try
        {
            using var client = new TcpClient(HOST, NETWORK_PORT);
            byte[] data = Encoding.UTF8.GetBytes(result.WireMessage);
            await client.GetStream().WriteAsync(data, 0, data.Length);
            this.Invoke((Action)(() => lstSent.Items.Add(result.DisplayText)));
        }
        catch (Exception ex) { Console.WriteLine(ex); }
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
            catch (Exception ex) { Console.WriteLine(ex); }
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

                ProtocolAction action;
                lock (protocolLock) { action = protocol.ProcessMessage(msg); }

                switch (action)
                {
                    case SendProposeAction spa:
                        this.Invoke((Action)(() => lstReceived.Items.Add(spa.Result.ReceivedDisplayText)));
                        await SendAsync(spa.Result.OriginPort, spa.Result.ProposeWireMessage);
                        break;

                    case BroadcastFinalAction bfa:
                        foreach (var fm in bfa.Result.FinalMessages)
                            await SendAsync(fm.Port, fm.WireMessage);
                        break;

                    case DeliverAction da:
                        if (da.Result.DeliveredMessages.Count > 0)
                        {
                            this.Invoke((Action)(() =>
                            {
                                foreach (var dm in da.Result.DeliveredMessages)
                                    lstReady.Items.Add(dm.ReadyDisplayText);
                            }));
                        }
                        break;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine(ex); }
    }

    async Task SendAsync(int port, string msg)
    {
        try
        {
            using var client = new TcpClient(HOST, port);
            byte[] data = Encoding.UTF8.GetBytes(msg);
            await client.GetStream().WriteAsync(data, 0, data.Length);
        }
        catch (Exception ex) { Console.WriteLine(ex); }
    }
}

// ==================== Protocol Logic (tested via TotalOrder.Tests) ====================

public class TotalOrderProtocol
{
    private readonly int _middlewareId;
    private readonly int _totalMiddleware;
    private readonly int _basePort;
    private int _msgCounter;
    private int _localClock;
    private readonly List<QueueEntry> _holdQueue = new List<QueueEntry>();
    private readonly Dictionary<string, List<ProposalEntry>> _proposals = new Dictionary<string, List<ProposalEntry>>();

    public TotalOrderProtocol(int middlewareId, int totalMiddleware, int basePort)
    {
        _middlewareId = middlewareId;
        _totalMiddleware = totalMiddleware;
        _basePort = basePort;
    }

    public MulticastResult CreateMulticast()
    {
        _msgCounter++;
        var msgId = _middlewareId + ":" + _msgCounter;
        var text = "Msg #" + _msgCounter + " from Middleware " + _middlewareId;
        var wire = "MULTICAST|" + msgId + "|" + text;
        return new MulticastResult(msgId, text, wire);
    }

    public MulticastReceivedResult OnMulticastReceived(string msgId, string text)
    {
        int maxInQueue = _holdQueue.Count > 0 ? MaxTsInQueue() : 0;
        int proposed = Math.Max(_localClock, maxInQueue) + 1;
        _localClock = proposed;

        _holdQueue.Add(new QueueEntry { MsgId = msgId, Text = text, TsValue = proposed, TsSenderId = _middlewareId, IsFinal = false });

        int originId = int.Parse(msgId.Split(':')[0]);
        if (originId == _middlewareId)
        {
            if (!_proposals.ContainsKey(msgId))
                _proposals[msgId] = new List<ProposalEntry>();
            _proposals[msgId].Add(new ProposalEntry(proposed, _middlewareId));
        }

        int originPort = _basePort + originId;
        var displayText = text + " | proposed:(" + proposed + "," + _middlewareId + ")";
        var wireMessage = "PROPOSE|" + msgId + "|" + proposed + "|" + _middlewareId;

        return new MulticastReceivedResult(msgId, text, proposed, _middlewareId, displayText, wireMessage, originPort);
    }

    public ProposeResult OnProposeReceived(string msgId, int value, int senderId)
    {
        if (!_proposals.ContainsKey(msgId))
            _proposals[msgId] = new List<ProposalEntry>();
        _proposals[msgId].Add(new ProposalEntry(value, senderId));

        if (_proposals[msgId].Count < _totalMiddleware)
            return null;

        int agreedVal = 0, agreedSid = 0;
        foreach (var p in _proposals[msgId])
        {
            if (p.Value > agreedVal || (p.Value == agreedVal && p.SenderId > agreedSid))
            { agreedVal = p.Value; agreedSid = p.SenderId; }
        }
        _proposals.Remove(msgId);

        var finalWire = "FINAL|" + msgId + "|" + agreedVal + "|" + agreedSid;
        var finalMessages = new List<FinalMessage>();
        for (int i = 1; i <= _totalMiddleware; i++)
            finalMessages.Add(new FinalMessage(finalWire, _basePort + i));

        return new ProposeResult(msgId, agreedVal, agreedSid, finalMessages);
    }

    public FinalResult OnFinalReceived(string msgId, int value, int senderId)
    {
        _localClock = Math.Max(_localClock, value);

        QueueEntry entry = null;
        foreach (var e in _holdQueue) { if (e.MsgId == msgId) { entry = e; break; } }
        if (entry == null) return new FinalResult(new List<DeliveredMessage>());

        entry.TsValue = value;
        entry.TsSenderId = senderId;
        entry.IsFinal = true;

        _holdQueue.Sort(CompareEntries);

        var delivered = new List<DeliveredMessage>();
        while (_holdQueue.Count > 0 && _holdQueue[0].IsFinal)
        {
            var e = _holdQueue[0];
            _holdQueue.RemoveAt(0);
            delivered.Add(new DeliveredMessage(e.MsgId, e.Text, e.TsValue, e.TsSenderId,
                e.Text + " | final:(" + e.TsValue + "," + e.TsSenderId + ")"));
        }
        return new FinalResult(delivered);
    }

    public ProtocolAction ProcessMessage(string raw)
    {
        string[] parts = raw.Split('|');
        if (parts.Length < 2) return new NoOpAction();

        if (parts[0] == "MULTICAST" && parts.Length >= 3)
            return new SendProposeAction(OnMulticastReceived(parts[1], parts[2]));
        if (parts[0] == "PROPOSE" && parts.Length >= 4)
        {
            var r = OnProposeReceived(parts[1], int.Parse(parts[2]), int.Parse(parts[3]));
            return r != null ? (ProtocolAction)new BroadcastFinalAction(r) : new NoOpAction();
        }
        if (parts[0] == "FINAL" && parts.Length >= 4)
            return new DeliverAction(OnFinalReceived(parts[1], int.Parse(parts[2]), int.Parse(parts[3])));
        return new NoOpAction();
    }

    int MaxTsInQueue()
    {
        int max = 0;
        foreach (var e in _holdQueue) { if (e.TsValue > max) max = e.TsValue; }
        return max;
    }

    static int CompareEntries(QueueEntry a, QueueEntry b)
    {
        if (a.TsValue != b.TsValue) return a.TsValue.CompareTo(b.TsValue);
        return a.TsSenderId.CompareTo(b.TsSenderId);
    }
}

public class QueueEntry
{
    public string MsgId; public string Text; public int TsValue; public int TsSenderId; public bool IsFinal;
}

public class ProposalEntry
{
    public int Value; public int SenderId;
    public ProposalEntry(int v, int s) { Value = v; SenderId = s; }
}

public class MulticastResult
{
    public string MsgId, DisplayText, WireMessage;
    public MulticastResult(string id, string dt, string wm) { MsgId = id; DisplayText = dt; WireMessage = wm; }
}

public class MulticastReceivedResult
{
    public string MsgId, Text, ReceivedDisplayText, ProposeWireMessage;
    public int ProposedValue, ProposerId, OriginPort;
    public MulticastReceivedResult(string id, string t, int pv, int pi, string rdt, string pwm, int op)
    { MsgId = id; Text = t; ProposedValue = pv; ProposerId = pi; ReceivedDisplayText = rdt; ProposeWireMessage = pwm; OriginPort = op; }
}

public class FinalMessage
{
    public string WireMessage; public int Port;
    public FinalMessage(string wm, int p) { WireMessage = wm; Port = p; }
}

public class ProposeResult
{
    public string MsgId; public int AgreedValue, AgreedSenderId; public List<FinalMessage> FinalMessages;
    public ProposeResult(string id, int av, int asi, List<FinalMessage> fm)
    { MsgId = id; AgreedValue = av; AgreedSenderId = asi; FinalMessages = fm; }
}

public class DeliveredMessage
{
    public string MsgId, Text, ReadyDisplayText; public int FinalTsValue, FinalTsSenderId;
    public DeliveredMessage(string id, string t, int ftv, int ftsi, string rdt)
    { MsgId = id; Text = t; FinalTsValue = ftv; FinalTsSenderId = ftsi; ReadyDisplayText = rdt; }
}

public class FinalResult
{
    public List<DeliveredMessage> DeliveredMessages;
    public FinalResult(List<DeliveredMessage> dm) { DeliveredMessages = dm; }
}

public abstract class ProtocolAction { }
public class NoOpAction : ProtocolAction { }
public class SendProposeAction : ProtocolAction { public MulticastReceivedResult Result; public SendProposeAction(MulticastReceivedResult r) { Result = r; } }
public class BroadcastFinalAction : ProtocolAction { public ProposeResult Result; public BroadcastFinalAction(ProposeResult r) { Result = r; } }
public class DeliverAction : ProtocolAction { public FinalResult Result; public DeliverAction(FinalResult r) { Result = r; } }
