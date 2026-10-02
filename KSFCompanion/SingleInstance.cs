using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace KsfCompanion
{
    /// <summary>
    /// One running copy per data folder: the first one holds a lock file, and starting it again asks that copy - over a
    /// Unix socket next to the lock - to bring its dashboard up.
    /// </summary>
    sealed class SingleInstance : IDisposable
    {
        readonly FileStream lockFile;
        readonly Socket listener;
        readonly string socketPath;
        bool disposed;

        SingleInstance(FileStream lockFile, Socket listener, string socketPath)
        {
            this.lockFile = lockFile;
            this.listener = listener;
            this.socketPath = socketPath;
        }

        /// <summary>Another copy asked for the dashboard (raised off the UI thread).</summary>
        public event Action ShowRequested;

        /// <summary>The first copy gets the instance; null when another copy is already running.</summary>
        public static SingleInstance TryStart(string basePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(basePath));
            FileStream lockFile;
            try
            {
                // FileShare.None takes an exclusive lock on the file, held for as long as this copy runs.
                lockFile = new FileStream(basePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return null;
            }

            var endpoint = Endpoint(basePath, out var socketPath);
            Socket listener = null;
            try
            {
                // Left over from a copy that didn't get to clean up.
                if (socketPath != null && File.Exists(socketPath)) File.Delete(socketPath);
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(endpoint);
                listener.Listen(4);
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Still the only copy; it just can't be asked to show itself.
                Program.Trace("single instance socket: " + ex.Message);
                listener?.Dispose();
                listener = null;
            }
            var instance = new SingleInstance(lockFile, listener, socketPath);
            if (listener != null) _ = instance.AcceptAsync();
            return instance;
        }

        /// <summary>
        /// The socket next to the lock - or, when that path is too long for a Unix socket (107 bytes), a name made from it
        /// in Linux's abstract socket namespace (not a file: nothing to clean up, and socketPath is null).
        /// </summary>
        static UnixDomainSocketEndPoint Endpoint(string basePath, out string socketPath)
        {
            socketPath = basePath + ".sock";
            if (Encoding.UTF8.GetByteCount(socketPath) <= 107) return new UnixDomainSocketEndPoint(socketPath);
            socketPath = null;
            return new UnixDomainSocketEndPoint("\0" + Program.AppId + "-" + Program.Hash(basePath));
        }

        async Task AcceptAsync()
        {
            while (!disposed)
            {
                try
                {
                    using var client = await listener.AcceptAsync().ConfigureAwait(false);
                    var buffer = new byte[64];
                    var read = await client.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                    if (Encoding.ASCII.GetString(buffer, 0, read).Trim() == "show") ShowRequested?.Invoke();
                }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                {
                    if (disposed) return;
                }
            }
        }

        /// <summary>Asks the copy that's running to bring its dashboard up.</summary>
        public static void AskToShow(string basePath)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(Endpoint(basePath, out _));
                socket.Send(Encoding.ASCII.GetBytes("show\n"));
            }
            catch (Exception ex) when (ex is SocketException || ex is ArgumentException) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            listener?.Dispose();
            try { if (listener != null && socketPath != null) File.Delete(socketPath); }
            catch (IOException) { }
            lockFile.Dispose();
        }
    }
}
