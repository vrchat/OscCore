using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace OscCore
{
    sealed class OscSocket : IDisposable
    {
#if UNITY_EDITOR
        static readonly ProfilerMarker k_ProfilerMarker = new ProfilerMarker("Receive OSC");
#endif
        readonly Socket m_Socket;
        readonly Thread m_Thread;
        bool m_Disposed;
        bool m_Started;

        AutoResetEvent m_ThreadWakeup;
        bool m_CloseRequested;

        public int Port { get; }
        public OscServer Server { get; set; }
        
        public OscSocket(int port)
        {
            Port = port;
            m_Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = int.MaxValue };
            m_Thread = new Thread(Serve);
        }

        public void Start()
        {
            // make sure redundant calls don't do anything after the first
            if (m_Started) return;
            
            m_Disposed = false;
            if (!m_Socket.IsBound)
                m_Socket.Bind(new IPEndPoint(IPAddress.Any, Port));

            m_ThreadWakeup = new AutoResetEvent(false);

            m_Thread.Start();
            m_Started = true;
        }
        
        void Serve()
        {
#if UNITY_EDITOR
            Profiler.BeginThreadProfiling("OscCore", "Server");
#endif
            var buffer = Server.Parser.Buffer;
            var socket = m_Socket;
            
            while (!m_Disposed)
            {
                try
                {
                    int receivedByteCount = 0;
                    socket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, result => {
                        try
                        {
                            receivedByteCount = socket.EndReceive(result);
                        }
                        catch (Exception e)
                        {
                            if (!m_Disposed && !m_CloseRequested) Debug.LogException(e);
                        }
                        finally
                        {
                            // even if this runs sync, the wakeup will stay signalled until the next WaitOne:
                            // https://learn.microsoft.com/en-us/dotnet/api/system.threading.eventwaithandle.set?view=netframework-4.8#remarks
                            m_ThreadWakeup.Set();
                        }
                    }, null);

                    // wait for the receive to complete, OR for Dispose to be called
                    m_ThreadWakeup.WaitOne();

                    if (m_CloseRequested)
                    {
                        m_Socket.Close();
                        m_Socket.Dispose();
                        break;
                    }

                    if (receivedByteCount == 0) continue;
#if UNITY_EDITOR
                    k_ProfilerMarker.Begin();
                    Server.ParseBuffer(receivedByteCount);
                    k_ProfilerMarker.End();
#else
                    Server.ParseBuffer(receivedByteCount);
#endif
                    Profiler.EndSample();
                }
                // a read timeout can result in a socket exception, should just be ok to ignore
                catch (SocketException) { }
                catch (ThreadAbortException) {}
                catch (Exception e)
                {
                    if (!m_Disposed) Debug.LogException(e); 
                    break;
                }
            }

            m_ThreadWakeup.Dispose();

#if UNITY_EDITOR
            Profiler.EndThreadProfiling();
#endif
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            if (m_ThreadWakeup != null)
            {
                // thread running, let it dispose itself async
                m_CloseRequested = true;
                m_ThreadWakeup.Set();
            }
            else
            {
                // try close directly
                m_Socket.Close();
                m_Socket.Dispose();
            }
            m_Disposed = true;
        }
    }
}