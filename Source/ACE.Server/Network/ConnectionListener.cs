using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

using log4net;

using ACE.Server.Network.Managers;

namespace ACE.Server.Network
{
    public class ConnectionListener
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly ILog packetLog = LogManager.GetLogger(System.Reflection.Assembly.GetEntryAssembly(), "Packets");

        public Socket Socket { get; private set; }

        private IPEndPoint listenerEndpoint;

        private readonly uint listeningPort;

        private readonly byte[] buffer = new byte[ClientPacket.MaxPacketSize];

        private readonly IPAddress listeningHost;

        public ConnectionListener(IPAddress host, uint port)
        {
            log.DebugFormat("ConnectionListener ctor, host {0} port {1}", host, port);
            listeningHost = host;
            listeningPort = port;
        }

        public void Start()
        {
            log.DebugFormat("Starting ConnectionListener, host {0} port {1}", listeningHost, listeningPort);
            try
            {
                listenerEndpoint = new IPEndPoint(listeningHost, (int)listeningPort);
                Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                Socket.Bind(listenerEndpoint);
                Listen();
            }
            catch (Exception exception)
            {
                log.FatalFormat("Network Socket has thrown: {0}", exception.Message);
            }
        }

        public void Shutdown()
        {
            log.DebugFormat("Shutting down ConnectionListener, host {0} port {1}", listeningHost, listeningPort);
            if (Socket != null && Socket.IsBound)
                Socket.Close();
        }

        private void Listen()
        {
            try
            {
                EndPoint clientEndPoint = new IPEndPoint(listeningHost, 0);
                Socket.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref clientEndPoint, OnDataReceieve, Socket);
            }
            catch (SocketException socketException)
            {
                log.DebugFormat("ConnectionListener.Listen() has thrown {0}: {1}", socketException.SocketErrorCode, socketException.Message);
                Listen();
            }
            catch (Exception exception)
            {
                log.FatalFormat("ConnectionListener.Listen() has thrown: {0}", exception.Message);
            }
        }

        private void OnDataReceieve(IAsyncResult result)
        {
            EndPoint clientEndPoint = null;
            try
            {
                clientEndPoint = new IPEndPoint(listeningHost, 0);
                int dataSize = Socket.EndReceiveFrom(result, ref clientEndPoint);

                byte[] data = new byte[dataSize];
                Buffer.BlockCopy(buffer, 0, data, 0, dataSize);

                IPEndPoint ipEndpoint = (IPEndPoint)clientEndPoint;

                // TO-DO: generate ban entries here based on packet rates of endPoint, IP Address, and IP Address Range

                if (packetLog.IsDebugEnabled)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"Received Packet (Len: {data.Length}) [{ipEndpoint.Address}:{ipEndpoint.Port}=>{listenerEndpoint.Address}:{listenerEndpoint.Port}]");
                    sb.AppendLine(data.BuildPacketString());
                    packetLog.Debug(sb.ToString());
                }

                var packet = new ClientPacket(data);
                if (packet.IsValid)
                    NetworkManager.ProcessPacket(packet, ipEndpoint, listenerEndpoint);
            }
            catch (SocketException socketException)
            {
                // If we get "Connection has been forcibly closed..." error, just eat the exception and continue on
                // This gets sent when the remote host terminates the connection (on UDP? interesting...)
                // TODO: There might be more, should keep an eye out. Logged message will help here.
                if (socketException.SocketErrorCode == SocketError.NetworkReset ||
                    socketException.SocketErrorCode == SocketError.ConnectionReset)
                {
                    log.DebugFormat("ConnectionListener.OnDataReceieve() has thrown {0}: {1} from client {2}", socketException.SocketErrorCode, socketException.Message, clientEndPoint != null ? clientEndPoint.ToString() : "Unknown");
                }
                else
                {
                    log.FatalFormat("ConnectionListener.OnDataReceieve() has thrown {0}: {1} from client {2}", socketException.SocketErrorCode, socketException.Message, clientEndPoint != null ? clientEndPoint.ToString() : "Unknown");
                    return;
                }
            }
            Listen();
        }
    }
}
