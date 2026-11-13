using PyCompat;
using WinBackground;

namespace EvCharging;

/// <summary>A daily run through Windows Task Scheduler (WinBackground).
/// <para>Bookings need a day's notice, not minutes, so there's no watcher: a task runs `run --hook --scheduled` daily
/// at daily_at and at logon, and catches up a run missed while the laptop was off or asleep. A run with nothing to do
/// costs a few Graph calls. It runs as you, only while you're logged on, needs no admin rights, and runs
/// ev-charging-background.exe directly, so no window appears (the browser runs headless). The same task name as the
/// Python version, so switching replaces the Python task rather than adding a second.</para></summary>
public static class Autostart
{
    public const string TaskName = "ev-charging run";
    public const string ConsoleExe = "ev-charging.exe";
    public const string BackgroundExe = "ev-charging-background.exe";

    public static ITask Create(string name) =>
        new ScheduledTask(name, "Books EV charging when the calendar asks for it, and applies approvals (ev-charging run).");

    /// <summary>The program the task runs (the windowless one beside the running program, if it's there) and its
    /// arguments.</summary>
    public static (string Exe, List<string> Args) Command(string programDir, string configPath)
    {
        var background = Path.Combine(programDir, BackgroundExe);
        var exe = File.Exists(background) ? background : Path.Combine(programDir, ConsoleExe);
        return (exe, ["--config", PyPath.Resolve(configPath), "run", "--hook", "--scheduled"]);
    }

    /// <summary>Daily at daily_at and at logon, as a job stopped after 30 minutes, started now.</summary>
    public static TaskSchedule Schedule(string dailyAt) => new()
    {
        AtLogon = true,
        DailyAt = dailyAt,
        KeepRunning = false,
    };
}
