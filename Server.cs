using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ticker.Server
{
    class Server
    {
        public bool Running { get; set; } = true;
        public static List<ClientHandler> Clients { get; set; } = new List<ClientHandler>();
        private TcpListener _listener;
        public Server()
        {
            _listener = new TcpListener(IPAddress.Parse("192.168.1.9"), 123);
        }

        public void Start()
        {
            _listener.Start();
            while (Running)
            {
                if (_listener.Pending())
                {
                    var client = _listener.AcceptTcpClient();
                    ClientHandler.Spawn(client);
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
        }

        public void StartThreaded()
        {
            Thread th = new Thread(new ThreadStart(Start));
            th.Start();
        }
    }
}
