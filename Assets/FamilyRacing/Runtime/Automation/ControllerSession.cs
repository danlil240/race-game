using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace FamilyRacing.Automation
{
    // One controller connection: owns the socket, the reader thread, the
    // bounded hello handshake and bounded line reads. Parsed frames are
    // handed to the bridge's inbound queue tagged with this session; the
    // reader never touches game state and never sends sequenced records.
    // Every socket write — reader error replies and stream-writer records —
    // is serialized through SendLock so frames cannot interleave.
    internal sealed class ControllerSession : IDisposable
    {
        private const int AuthTimeoutMs = 5000;
        // Every synchronous socket write — hello/error replies and stream
        // records — is bounded so a peer that stops reading cannot stall the
        // writer forever.
        internal const int SendTimeoutMs = 2000;
        private const string Oversize = "\0";
        private const string BadUtf8 = "\x01";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly string expectedRunId;
        private readonly string expectedToken;
        private readonly string serverHello;
        private readonly Func<ControllerSession, JObject, bool> enqueue;
        private readonly TcpClient client;
        private Thread reader;

        internal volatile bool authenticated;
        internal volatile bool alive = true;
        internal NetworkStream Stream { get; private set; }
        internal readonly object SendLock = new object();

        internal ControllerSession(TcpClient client, string expectedRunId,
            string expectedToken, string serverHello,
            Func<ControllerSession, JObject, bool> enqueue)
        {
            this.client = client;
            this.expectedRunId = expectedRunId;
            this.expectedToken = expectedToken;
            this.serverHello = serverHello;
            this.enqueue = enqueue;
            Stream = client.GetStream();
            client.SendTimeout = SendTimeoutMs;
            reader = new Thread(ReaderLoop) { IsBackground = true, Name = "ValidationReader" };
            reader.Start();
        }

        private void ReaderLoop()
        {
            try
            {
                client.ReceiveTimeout = 1000;
                // Total hello deadline: a per-byte timeout alone would let a
                // drip-feed of one byte per second hold the slot forever.
                var helloDeadline = DateTime.UtcNow.AddMilliseconds(AuthTimeoutMs);
                var helloLine = ReadLineBounded(Stream, helloDeadline);
                if (!ValidateHello(helloLine)) { alive = false; return; }
                client.ReceiveTimeout = 0;
                authenticated = true;
                string line;
                while ((line = ReadLineBounded(Stream, DateTime.MaxValue)) != null)
                {
                    if (line == Oversize)
                    {
                        WriteError("frameTooLarge");
                        continue;
                    }
                    if (line == BadUtf8)
                    {
                        WriteError("invalidUtf8");
                        continue;
                    }
                    if (!ValidationJson.TryParseFrame(line, out var frame, out var error))
                    {
                        WriteError(error ?? "invalidJson");
                        continue;
                    }
                    if (!enqueue(this, frame))
                        WriteError("commandQueueFull");
                }
            }
            catch (Exception) { }
            finally { alive = false; }
        }

        private bool ValidateHello(string line)
        {
            if (line == null || line == Oversize || line == BadUtf8 ||
                !ValidationJson.TryParseFrame(line, out var hello, out _))
            {
                WriteError("badHello");
                return false;
            }
            if (ValidationJson.Text(hello, "type") != "hello" ||
                ValidationJson.Int(hello, "protocolVersion") != ValidationBridge.ProtocolVersion ||
                ValidationJson.Text(hello, "runId") != expectedRunId ||
                ValidationJson.Text(hello, "token") != expectedToken)
            {
                WriteError("unauthorized");
                return false;
            }
            WriteRaw(serverHello);
            return true;
        }

        internal void WriteError(string code)
        {
            WriteRaw("{\"type\":\"error\",\"protocolVersion\":1,\"runId\":\"" +
                expectedRunId + "\",\"code\":\"" + code + "\"}");
        }

        private void WriteRaw(string payload)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(payload + "\n");
                lock (SendLock)
                {
                    Stream.Write(bytes, 0, bytes.Length);
                    Stream.Flush();
                }
            }
            catch (Exception)
            {
                // A failed reply write means this session's sink is gone:
                // close it so nothing else pays another send timeout on it.
                alive = false;
                Dispose();
            }
        }

        // Reads one \n-terminated frame bounded to MaxFrameChars. Returns null
        // on EOF, socket error or an expired deadline — a partial line is an
        // incomplete frame and is never processed. Receive timeouts retry
        // until the absolute deadline so the 5s hello budget is total, not
        // per-byte. Returns Oversize when the limit is hit (line drained to
        // newline) and BadUtf8 on strict UTF-8 decode failure.
        private static string ReadLineBounded(NetworkStream stream, DateTime deadline)
        {
            var buffer = new MemoryStream();
            int b;
            while (true)
            {
                if (DateTime.UtcNow > deadline) return null;
                try { b = stream.ReadByte(); }
                catch (Exception e) when (IsTimeout(e))
                {
                    if (DateTime.UtcNow > deadline) return null;
                    continue;
                }
                catch (Exception) { return null; }
                if (b < 0) return null;
                if (b == '\n') break;
                buffer.WriteByte((byte)b);
                if (buffer.Length > ValidationJson.MaxFrameChars)
                {
                    while (true)
                    {
                        try { b = stream.ReadByte(); }
                        catch (Exception) { return Oversize; }
                        if (b < 0 || b == '\n') break;
                    }
                    return Oversize;
                }
            }
            try { return StrictUtf8.GetString(buffer.ToArray()); }
            catch (DecoderFallbackException) { return BadUtf8; }
        }

        private static bool IsTimeout(Exception e)
        {
            var socket = e as SocketException ?? e.InnerException as SocketException;
            return socket != null &&
                (socket.SocketErrorCode == SocketError.TimedOut ||
                 socket.SocketErrorCode == SocketError.WouldBlock);
        }

        internal static void RejectAndClose(TcpClient client, string code, string runId)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(
                    "{\"type\":\"error\",\"protocolVersion\":1,\"runId\":\"" + runId +
                    "\",\"code\":\"" + code + "\"}\n");
                client.SendTimeout = SendTimeoutMs;
                client.GetStream().Write(bytes, 0, bytes.Length);
                client.GetStream().Flush();
                // Drain unread client bytes and shut down gracefully: closing
                // with pending inbound data sends RST and can discard the
                // error frame the client is about to read.
                client.ReceiveTimeout = 50;
                try { while (client.GetStream().ReadByte() >= 0) { } }
                catch (Exception) { }
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception) { }
            try { client.Close(); } catch { }
        }

        public void Dispose()
        {
            alive = false;
            try { Stream?.Close(); } catch { }
            try { client?.Close(); } catch { }
        }
    }
}
