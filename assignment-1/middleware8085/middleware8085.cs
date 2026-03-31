using System;
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

public class Form1 : Form
{
    private Button button1;
    private RichTextBox richTextBox1;
    private TcpListener listener;

    public Form1()
    {
        this.Text = "Middleware8085";
        button1 = new Button();
        button1.Size = new System.Drawing.Size(100, 50);
        button1.Location = new System.Drawing.Point(35, 15);
        button1.Text = "Send Message";
        button1.Click += new EventHandler(button1_Click);

        richTextBox1 = new RichTextBox();
        richTextBox1.Location = new System.Drawing.Point(35, 70);
        richTextBox1.Width = 200;
        richTextBox1.Height = 100;
        richTextBox1.Multiline = true;
        richTextBox1.ScrollBars = RichTextBoxScrollBars.Vertical;

        Controls.Add(button1);
        Controls.Add(richTextBox1);

        listener = new TcpListener(IPAddress.Any, 8085);
        listener.Start();
        ListenForClientsAsync();
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
        richTextBox1.AppendText($"{message}\n");
    }

    private async void button1_Click(object sender, EventArgs e)
    {
        TcpClient client = new TcpClient("localhost", 8081);
        byte[] data = Encoding.UTF8.GetBytes("from 8085");
        await client.GetStream().WriteAsync(data, 0, data.Length);
    }
}

