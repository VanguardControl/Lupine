using System;
using System.Threading;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Timer = Robust.Shared.Timing.Timer;

namespace Robust.Server.Console.Commands;

/// <summary>Wolfgate: starts, stops and reports the server's network traffic breakdown.</summary>
public sealed class NetStatsCommand : IConsoleCommand
{
    [Dependency] private NetTrafficStats _stats = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private ILogManager _log = default!;

    private CancellationTokenSource? _timer;

    public string Command => "netstats";
    public string Description => "Breaks down what the server sends: by message, network event, component and prototype.";

    public string Help => $"Usage: {Command} start [seconds] [sampleEvery] | {Command} stop | {Command} show [top]\n" +
                          "start: clears and starts counting; with seconds, stops and logs a report when they are up. " +
                          "Game states are broken down on one tick in sampleEvery (default 10).";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        switch (args.Length > 0 ? args[0] : "show")
        {
            case "start":
                var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? Math.Max(1, s) : 0;
                var every = args.Length > 2 && int.TryParse(args[2], out var e) ? e : 10;
                _timer?.Cancel();
                _timer = null;
                _stats.Start(every);
                shell.WriteLine($"Counting network traffic{(seconds > 0 ? $" for {seconds} s" : "")}.");
                if (seconds > 0)
                {
                    _timer = new CancellationTokenSource();
                    Timer.Spawn(TimeSpan.FromSeconds(seconds), () =>
                    {
                        _stats.Stop();
                        _log.GetSawmill("net.stats").Info(_stats.Report(25, _players.PlayerCount));
                    }, _timer.Token);
                }

                break;
            case "stop":
                _timer?.Cancel();
                _timer = null;
                _stats.Stop();
                shell.WriteLine(_stats.Report(25, _players.PlayerCount));
                break;
            case "show":
                var top = args.Length > 1 && int.TryParse(args[1], out var t) ? t : 25;
                shell.WriteLine(_stats.Report(top, _players.PlayerCount));
                break;
            default:
                shell.WriteError(Help);
                break;
        }
    }
}
