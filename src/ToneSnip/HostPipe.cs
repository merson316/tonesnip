using System.IO.Pipes;
using System.Text;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Diagnostics;

namespace ToneSnip.App;

/// <summary>Pipe "tonesnip": a second launch hands its command line to the running instance.</summary>
public static class HostPipe
{
    /// <summary>
    /// Sends a command to the running instance, exactly as given.
    /// <para>It never turns an empty command into <c>--settings</c>: only <c>Program</c> can tell a bare launch from a
    /// line that reduced to nothing, because it still has the original argv.</para>
    /// </summary>
    /// <returns>True when the running instance took the line; false when none answered in time, or when
    /// <paramref name="giveUp"/> said to stop trying.</returns>
    public static bool Forward(StartupCommand c, Func<bool>? giveUp = null)
    {
        // Retried: the running instance takes the mutex early in Main but only serves the pipe near the end of startup.
        byte[] line = Encoding.UTF8.GetBytes(string.Join(" ", CommandLine.ToArgs(c)) + "\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < ForwardPatience)
        {
            if (giveUp?.Invoke() == true) return false;
            try
            {
                using var pipe = new NamedPipeClientStream(".", Protocol.HostPipeFor(AppPaths.SessionId), PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(1000);
                pipe.Write(line); pipe.Flush();
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { Thread.Sleep(200); }
            catch { return false; }
        }
        return false;
    }

    /// <summary>How long a second launch keeps trying to reach a first instance that is still starting.</summary>
    private static readonly TimeSpan ForwardPatience = TimeSpan.FromSeconds(10);

    /// <summary>A command line is a few dozen bytes; anything past this is not one of ours.</summary>
    private const int MaxLineBytes = 4096;

    /// <summary>How long a connected client has to send its line, so a silent client cannot block later
    /// launches.</summary>
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(2);

    public static IDisposable Serve(Action<StartupCommand> handle, ILog log)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Protocol.HostPipeFor(AppPaths.SessionId), PipeDirection.In, 2, PipeTransmissionMode.Byte,
                                                                 PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cts.Token);
                    using var line = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    line.CancelAfter(LineTimeout);
                    string? text;
                    try { text = await LineReading.ReadLineAsync(server, MaxLineBytes, line.Token); }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested) { log.Warn("host pipe: a client connected and sent no command"); continue; }
                    if (text == null) { log.Warn("host pipe: a command line over the size limit was ignored"); continue; }
                    handle(CommandLine.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { log.Warn("host pipe: " + e.Message); await Task.Delay(300); }
            }
        });
        return new Stopper(cts);
    }

    /// <summary>Cancels, then disposes: disposing a CancellationTokenSource alone does not cancel the pending
    /// wait.</summary>
    private sealed class Stopper(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() { try { cts.Cancel(); } catch (ObjectDisposedException) { } cts.Dispose(); }
    }
}
