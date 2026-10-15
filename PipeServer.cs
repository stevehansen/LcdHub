using System.IO.Pipes;
using LcdHub.Client;

namespace LcdHub;

/// <summary>
/// Accepts producers on the per-user pipe, one thread each: a hello, then frames until the
/// producer goes. Everything it learns goes straight to <see cref="Hub"/>.
/// </summary>
internal static class PipeServer
{
    public static void Start() => new Thread(Accept) { IsBackground = true, Name = "PipeServer" }.Start();

    private static void Accept()
    {
        while (true)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(LcdHubWire.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, LcdHubWire.PipeOptions);
                pipe.WaitForConnection();
            }
            catch (IOException ex)
            {
                Diagnose.LogError("pipe accept", ex);
                Thread.Sleep(1000);
                continue;
            }
            new Thread(() => Serve(pipe)) { IsBackground = true, Name = "PipeServer client" }.Start();
        }
    }

    private static void Serve(NamedPipeServerStream pipe)
    {
        Producer? producer = null;
        try
        {
            if (LcdHubWire.Read(pipe) is not { Type: LcdHubWire.MsgHello } hello) return;
            var (version, name) = LcdHubWire.ParseHello(hello.Body);
            if (version < LcdHubWire.MinVersion)
            {
                Diagnose.Log($"pipe: {name} speaks version {version}, older than {LcdHubWire.MinVersion}");
                return;
            }
            producer = Hub.Connect(name, message => LcdHubWire.Write(pipe, message));
            while (LcdHubWire.ReadIncoming(pipe) is { } incoming)
            {
                if (incoming.Frame is { } frame) Hub.Offer(producer, frame);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or ObjectDisposedException)
        {
            Diagnose.LogError("pipe client", ex);
        }
        finally
        {
            if (producer is not null) Hub.Disconnect(producer);
            pipe.Dispose();
        }
    }
}
