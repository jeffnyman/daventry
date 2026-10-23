using Daventry.Agi;
using Daventry.Core.Hosting;
using Daventry.Sci;

// Plays a game without a window, from the published packages:
//   ConsoleSmoke <game folder> [ticks] [screenshot.png]
// Exit code 0 when the game drew something.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ConsoleSmoke <game folder> [ticks] [screenshot.png]");
    return 2;
}

int ticks = args.Length > 1 ? int.Parse(args[1]) : 600;
var registry = new EngineRegistry().Add(new AgiEngineProvider()).Add(new SciEngineProvider());
if (registry.DetectInDirectory(args[0]) is not var (container, games))
{
    Console.Error.WriteLine($"No AGI or SCI game in {args[0]}");
    return 1;
}

Console.WriteLine($"Detected {games[0]}");
var host = new HeadlessHost(new TextWriterLog(Console.Out, LogLevel.Warning));
using var engine = registry.Create(host, container, games[0]);
engine.Initialize();

int framesPerTick = (int)(host.Audio.SampleRate * host.TickInterval.TotalSeconds);
int peak = 0;
int ran = 0;
for (; ran < ticks; ran++)
{
    if (!engine.Tick(ran == 0 ? TimeSpan.Zero : host.TickInterval).Continue)
    {
        break;
    }

    foreach (short sample in host.Audio.Pull(framesPerTick))
    {
        peak = Math.Max(peak, Math.Abs((int)sample));
    }
}

var frame = host.Graphics.LastFrame;
int lit = frame is null ? 0 : frame.Pixels.Count(p => p != 0);
Console.WriteLine($"{ran} ticks, {host.Graphics.PresentCount} frames presented, {lit} of {frame?.Pixels.Length ?? 0} pixels lit, audio peak {peak}");

if (args.Length > 2 && frame is not null)
{
    File.WriteAllBytes(args[2], host.Graphics.EncodePng());
    Console.WriteLine($"Wrote {args[2]}");
}

return lit > 0 ? 0 : 1;
