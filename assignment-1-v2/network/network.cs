using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Linq;

public class Program
{
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Form1());
    }
}

public class Form1 : Form
{
    private RichTextBox richTextBox1;
    private TcpListener listener;
    private string[] delayLines;
    private int[] ports = { 8082, 8083, 8084, 8085, 8086 };
    private int currentLine = 0;
    private object lockObject = new object();

    public Form1()
    {
        this.Text = "Network";
        richTextBox1 = new RichTextBox();
        richTextBox1.Location = new System.Drawing.Point(35, 15);
        richTextBox1.Width = 200;
        richTextBox1.Height = 100;
        richTextBox1.Multiline = true;
        richTextBox1.ScrollBars = RichTextBoxScrollBars.Vertical;

        Controls.Add(richTextBox1);

        listener = new TcpListener(IPAddress.Any, 8081);
        listener.Start();
        ListenForClientsAsync();

        string currentDirectory = Directory.GetCurrentDirectory();
        string filePath = Path.Combine(currentDirectory, "network", "delays.txt");
        delayLines = File.ReadAllLines(filePath);
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
        richTextBox1.AppendText($"Receive message {message}\n");
        SendMessagesAsync(message);
    }

    private async void SendMessagesAsync(string message)
    {
        string[] delays;
        lock (lockObject)
        {
            delays = delayLines[currentLine++].Split(',');
        }
        int[] intDelays = delays.Select(x => int.Parse(x)).ToArray();
        int[] sortedPorts = new int[5];
        Array.Copy(ports, sortedPorts, 5);

        Array.Sort(intDelays, sortedPorts);
        for (int i = 0; i < 5; i++)
        {
            if (i == 0)
                await Task.Delay(intDelays[i]);
            else
                await Task.Delay(intDelays[i] - intDelays[i - 1]);
            TcpClient client = new TcpClient("localhost", sortedPorts[i]);
            byte[] data = Encoding.UTF8.GetBytes(message);
            await client.GetStream().WriteAsync(data, 0, data.Length);
            Console.WriteLine($"Sent message '{message}' to port {sortedPorts[i]} after a delay of {intDelays[i]} ms{Environment.NewLine}");
        }
    }
}
