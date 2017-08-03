﻿/*
* This file is subject to the terms and conditions defined in
* file 'license.txt', which is part of this source code package.
*/

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SteamKit2
{
    class TcpConnection : IConnection
    {
        const uint MAGIC = 0x31305456; // "VT01"

        private EndPoint destination;
        private Socket socket;
        private Thread netThread;
        private NetworkStream netStream;
        private BinaryReader netReader;
        private BinaryWriter netWriter;

        private CancellationTokenSource cancellationToken;
        private ManualResetEvent connectionFree;
        private object netLock, connectLock;

        public TcpConnection()
        {
            netLock = new object();
            connectLock = new object();
            connectionFree = new ManualResetEvent(true);
        }

        public event EventHandler<NetMsgEventArgs> NetMsgReceived;

        public event EventHandler Connected;

        public event EventHandler<DisconnectedEventArgs> Disconnected;

        public EndPoint CurrentEndPoint { get; private set; }

        public ProtocolTypes ProtocolTypes => ProtocolTypes.Tcp;

        private void Shutdown()
        {
            try
            {
                if (socket.Connected)
                {
                    socket.Shutdown(SocketShutdown.Both);
#if NET46
                    socket.Disconnect( true );
#endif
                }
            }
            catch
            {
                // Shutdown is throwing when the remote end closes the connection before SteamKit attempts to
                // so this should be safe as a no-op
                // see: https://bitbucket.org/VoiDeD/steamre/issue/41/socketexception-thrown-when-closing
            }
        }

        private void Release( bool userRequestedDisconnect )
        {
            lock (netLock)
            {
                if (cancellationToken != null)
                {
                    cancellationToken.Dispose();
                    cancellationToken = null;
                }

                if (netWriter != null)
                {
                    netWriter.Dispose();
                    netWriter = null;
                }

                if (netReader != null)
                {
                    netReader.Dispose();
                    netReader = null;
                }

                if (netStream != null)
                {
                    netStream.Dispose();
                    netStream = null;
                }

                if (socket != null)
                {
                    socket.Dispose();
                    socket = null;
                }
            }

            Disconnected?.Invoke( this, new DisconnectedEventArgs( userRequestedDisconnect ) );

            connectionFree.Set();
        }

        private void ConnectCompleted(bool success)
        {
            // Always discard result if our request was cancelled
            // If we have no cancellation token source, we were already Release()'ed
            if (cancellationToken?.IsCancellationRequested ?? true)
            {
                DebugLog.WriteLine("TcpConnection", "Connection request to {0} was cancelled", destination);
                if (success) Shutdown();
                Release( userRequestedDisconnect: true );
                return;
            }
            else if (!success)
            {
                DebugLog.WriteLine("TcpConnection", "Timed out while connecting to {0}", destination);
                Release( userRequestedDisconnect: false );
                return;
            }

            DebugLog.WriteLine("TcpConnection", "Connected to {0}", destination);

            try
            {
                lock (netLock)
                {
                    netStream = new NetworkStream(socket, false);
                    netReader = new BinaryReader(netStream);
                    netWriter = new BinaryWriter(netStream);

                    netThread = new Thread(NetLoop);
                    netThread.Name = "TcpConnection Thread";
                }

                netThread.Start();

                Connected?.Invoke( this, EventArgs.Empty );
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine("TcpConnection", "Exception while setting up connection to {0}: {1}", destination, ex);
                Release( userRequestedDisconnect: false );
            }
        }

        private void TryConnect(object sender)
        {
            int timeout = (int)sender;
            if (cancellationToken.IsCancellationRequested)
            {
                DebugLog.WriteLine("TcpConnection", "Connection to {0} cancelled by user", destination);
                Release( userRequestedDisconnect: true );
                return;
            }
            
            var connectEventArgs = new SocketAsyncEventArgs { RemoteEndPoint = destination };
            var asyncWaitHandle = new ManualResetEvent( initialState: false );
            EventHandler<SocketAsyncEventArgs> completionHandler = ( s, e ) =>
            {
                asyncWaitHandle.Set();

                var connected = e.ConnectSocket != null;
                ConnectCompleted( connected );
                (e as IDisposable)?.Dispose();
            };
            connectEventArgs.Completed += completionHandler;

            if ( !socket.ConnectAsync( connectEventArgs ) )
            {
                completionHandler( socket, connectEventArgs );
            }

            if ( WaitHandle.WaitAny( new WaitHandle[] { asyncWaitHandle, cancellationToken.Token.WaitHandle }, timeout) != 0 )
            {
                Socket.CancelConnectAsync( connectEventArgs );
            }
        }

        /// <summary>
        /// Connects to the specified end point.
        /// </summary>
        /// <param name="endPoint">The end point to connect to.</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        public void Connect(EndPoint endPoint, int timeout)
        {
            lock (connectLock)
            {
                Disconnect();

                connectionFree.Reset();

                lock (netLock)
                {
                    Debug.Assert(cancellationToken == null);
                    cancellationToken = new CancellationTokenSource();

                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.ReceiveTimeout = timeout;
                    socket.SendTimeout = timeout;

                    destination = endPoint;
                    DebugLog.WriteLine("TcpConnection", "Connecting to {0}...", destination);
                    TryConnect(timeout);
                }
            }
        }

        public void Disconnect()
        {
            lock (connectLock)
            {
                lock (netLock)
                {
                    if (cancellationToken != null)
                    {
                        cancellationToken.Cancel();
                    }
                    else
                    {
                        // we already appear to be disconncted, nothing to wait for
                        return;
                    }
                }

                connectionFree.WaitOne();
            }
        }

        // this is now a steamkit meme
        /// <summary>
        /// Nets the loop.
        /// </summary>
        void NetLoop()
        {
            // poll for readable data every 100ms
            const int POLL_MS = 100;

            while (!cancellationToken.IsCancellationRequested)
            {
                bool canRead = false;

                try
                {
                    canRead = socket.Poll(POLL_MS * 1000, SelectMode.SelectRead);
                }
                catch (SocketException ex)
                {
                    DebugLog.WriteLine("TcpConnection", "Socket exception while polling: {0}", ex);
                    break;
                }

                if (!canRead)
                {
                    // nothing to read yet
                    continue;
                }

                byte[] packData = null;

                try
                {
                    // read the packet off the network
                    packData = ReadPacket();
                }
                catch (IOException ex)
                {
                    DebugLog.WriteLine("TcpConnection", "Socket exception occurred while reading packet: {0}", ex);
                    break;
                }

                try
                {
                    NetMsgReceived?.Invoke( this, new NetMsgEventArgs( packData, destination ) );
                }
                catch (Exception ex)
                {
                    DebugLog.WriteLine("TcpConnection", "Unexpected exception propogated back to NetLoop: {0}", ex);
                }
            }

            // Thread is shutting down, ensure socket is shut down and disposed
            bool userShutdown = cancellationToken.IsCancellationRequested;

            if ( userShutdown )
            {
                Shutdown();
            }
            Release( userShutdown );
        }

        byte[] ReadPacket()
        {
            // the tcp packet header is considerably less complex than the udp one
            // it only consists of the packet length, followed by the "VT01" magic
            uint packetLen = 0;
            uint packetMagic = 0;

            try
            {
                packetLen = netReader.ReadUInt32();
                packetMagic = netReader.ReadUInt32();
            }
            catch (IOException ex)
            {
                throw new IOException("Connection lost while reading packet header.", ex);
            }

            if (packetMagic != TcpConnection.MAGIC)
            {
                throw new IOException("Got a packet with invalid magic!");
            }

            // rest of the packet is the physical data
            byte[] packData = netReader.ReadBytes((int)packetLen);

            if (packData.Length != packetLen)
            {
                throw new IOException("Connection lost while reading packet payload");
            }

            return packData;
        }

        public void Send( byte[] data )
        {
            lock ( netLock )
            {
                if (socket == null || netStream == null)
                {
                    DebugLog.WriteLine("TcpConnection", "Attempting to send client data when not connected.");
                    return;
                }

                try
                {
                    netWriter.Write((uint)data.Length);
                    netWriter.Write(MAGIC);
                    netWriter.Write(data);
                }
                catch (IOException ex)
                {
                    DebugLog.WriteLine("TcpConnection", "Socket exception while writing data: {0}", ex);
                }
            }
        }

        public IPAddress GetLocalIP()
        {
            lock (netLock)
            {
                if (socket == null)
                {
                    return IPAddress.None;
                }

                try
                {
                    return NetHelpers.GetLocalIP(socket);
                }
                catch (Exception ex)
                {
                    DebugLog.WriteLine("TcpConnection", "Socket exception trying to read bound IP: {0}", ex);
                    return IPAddress.None;
                }
            }
        }
    }
}
