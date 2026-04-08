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
        Application.Run(new Form1());
    }
}

class MessageEntry
{
    public string Id;
    public string Content;
    public int Seq;
    public bool Deliverable;
    public int SenderId;
}

public class Form1 : Form
{
    private Button sendButton;
    private RichTextBox sentList;
    private RichTextBox receivedList;
    private RichTextBox readyList;
    private TcpListener listener;
    private TcpListener controlListener;

    private int msgCounter = 0;
    private int localCounter = 0;
    private List<MessageEntry> holdingQueue = new List<MessageEntry>();
    private Dictionary<string, List<int>> proposalTracker = new Dictionary<string, List<int>>();
    private object lockObj = new object();

    private const int MY_ID = 3;
    private const int MY_PORT = 8084;
    private const int MY_CONTROL_PORT = 8089;
    private const int NETWORK_PORT = 8081;
    private const int TOTAL_MIDDLEWARE = 5;
    private static readonly int[] ALL_CONTROL_PORTS = { 8087, 8088, 8089, 8090, 8091 };

    public Form1()
    {
        this.Text = "Middleware 3";
        this.Width = 900;
        this.Height = 500;

        sendButton = new Button();
        sendButton.Text = "Send";
        sendButton.Location = new System.Drawing.Point(20, 15);
        sendButton.Size = new System.Drawing.Size(80, 30);
        sendButton.Click += new EventHandler(SendButton_Click);

        Label sentLabel = new Label();
        sentLabel.Text = "Sent";
        sentLabel.Location = new System.Drawing.Point(20, 60);

        Label receivedLabel = new Label();
        receivedLabel.Text = "Received";
        receivedLabel.Location = new System.Drawing.Point(310, 60);

        Label readyLabel = new Label();
        readyLabel.Text = "Ready";
        readyLabel.Location = new System.Drawing.Point(600, 60);

        sentList = new RichTextBox();
        sentList.Location = new System.Drawing.Point(20, 80);
        sentList.Size = new System.Drawing.Size(270, 350);
        sentList.ReadOnly = true;
        sentList.ScrollBars = RichTextBoxScrollBars.Vertical;
        sentList.WordWrap = true;

        receivedList = new RichTextBox();
        receivedList.Location = new System.Drawing.Point(310, 80);
        receivedList.Size = new System.Drawing.Size(270, 350);
        receivedList.ReadOnly = true;
        receivedList.ScrollBars = RichTextBoxScrollBars.Vertical;
        receivedList.WordWrap = true;

        readyList = new RichTextBox();
        readyList.Location = new System.Drawing.Point(600, 80);
        readyList.Size = new System.Drawing.Size(270, 350);
        readyList.ReadOnly = true;
        readyList.ScrollBars = RichTextBoxScrollBars.Vertical;
        readyList.WordWrap = true;

        Controls.Add(sendButton);
        Controls.Add(sentLabel);
        Controls.Add(receivedLabel);
        Controls.Add(readyLabel);
        Controls.Add(sentList);
        Controls.Add(receivedList);
        Controls.Add(readyList);

        listener = new TcpListener(IPAddress.Any, MY_PORT);
        listener.Start();
        ListenForClientsAsync();

        controlListener = new TcpListener(IPAddress.Any, MY_CONTROL_PORT);
        controlListener.Start();
        ListenForControlAsync();
    }

    private async void ListenForClientsAsync()
    {
        while (true)
        {
            TcpClient client = await listener.AcceptTcpClientAsync();
            ReadMessageAsync(client);
        }
    }

    private async void ReadMessageAsync(TcpClient client)
    {
        byte[] buffer = new byte[1024];
        await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
        string message = Encoding.UTF8.GetString(buffer).Trim('\0');
        client.Close();

        string[] parts = message.Split(' ');
        int senderId = int.Parse(parts[4]);
        string msgNum = parts[1].TrimStart('#');
        string msgId = $"MW{senderId}#{msgNum}";

        int proposed;
        lock (lockObj)
        {
            proposed = ++localCounter;
            holdingQueue.Add(new MessageEntry { Id = msgId, Content = message, Seq = proposed, Deliverable = false, SenderId = senderId });
            SortQueue();
        }

        receivedList.AppendText($"{message} [proposed: {proposed}]\n");

        if (senderId == MY_ID)
        {
            lock (lockObj) { proposalTracker[msgId].Add(proposed); }
            await CheckAndSendFinalAsync(msgId);
        }
        else
        {
            int senderControlPort = 8086 + senderId;
            await SendControlAsync(senderControlPort, $"PROPOSE|{msgId}|{proposed}");
        }
    }

    private async void ListenForControlAsync()
    {
        while (true)
        {
            TcpClient client = await controlListener.AcceptTcpClientAsync();
            HandleControlMessageAsync(client);
        }
    }

    private async void HandleControlMessageAsync(TcpClient client)
    {
        byte[] buffer = new byte[1024];
        await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
        string message = Encoding.UTF8.GetString(buffer).Trim('\0');
        client.Close();

        string[] parts = message.Split('|');
        string msgId = parts[1];

        if (parts[0] == "PROPOSE")
        {
            int proposal = int.Parse(parts[2]);
            lock (lockObj) { proposalTracker[msgId].Add(proposal); }
            await CheckAndSendFinalAsync(msgId);
        }
        else if (parts[0] == "FINAL")
        {
            int finalSeq = int.Parse(parts[2]);
            lock (lockObj)
            {
                localCounter = Math.Max(localCounter, finalSeq);
                var entry = holdingQueue.Find(e => e.Id == msgId);
                if (entry != null)
                {
                    entry.Seq = finalSeq;
                    entry.Deliverable = true;
                    SortQueue();
                }
            }
            TryDeliver();
        }
    }

    private async Task CheckAndSendFinalAsync(string msgId)
    {
        int finalSeq = 0;
        bool shouldSend = false;
        lock (lockObj)
        {
            if (proposalTracker.ContainsKey(msgId) && proposalTracker[msgId].Count == TOTAL_MIDDLEWARE)
            {
                finalSeq = proposalTracker[msgId].Max();
                shouldSend = true;
            }
        }
        if (shouldSend)
        {
            foreach (int port in ALL_CONTROL_PORTS)
            {
                await SendControlAsync(port, $"FINAL|{msgId}|{finalSeq}");
            }
        }
    }

    private void TryDeliver()
    {
        while (true)
        {
            MessageEntry head = null;
            lock (lockObj)
            {
                if (holdingQueue.Count == 0 || !holdingQueue[0].Deliverable) break;
                head = holdingQueue[0];
                holdingQueue.RemoveAt(0);
            }
            readyList.AppendText($"{head.Content} [final: {head.Seq}]\n");
        }
    }

    private void SortQueue()
    {
        holdingQueue.Sort((a, b) => a.Seq != b.Seq ? a.Seq.CompareTo(b.Seq) : a.SenderId.CompareTo(b.SenderId));
    }

    private async Task SendControlAsync(int port, string message)
    {
        TcpClient client = new TcpClient("localhost", port);
        byte[] data = Encoding.UTF8.GetBytes(message);
        await client.GetStream().WriteAsync(data, 0, data.Length);
        client.Close();
    }

    private async void SendButton_Click(object sender, EventArgs e)
    {
        msgCounter++;
        string msgId = $"MW{MY_ID}#{msgCounter}";
        string message = $"Msg #{msgCounter} from Middleware {MY_ID}";

        lock (lockObj) { proposalTracker[msgId] = new List<int>(); }

        sentList.AppendText(message + "\n");

        TcpClient client = new TcpClient("localhost", NETWORK_PORT);
        byte[] data = Encoding.UTF8.GetBytes(message);
        await client.GetStream().WriteAsync(data, 0, data.Length);
        client.Close();
    }
}
