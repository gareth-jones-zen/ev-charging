// ev-charging-background.exe: no window, ever. What the scheduled task runs (`--config SETTINGS run --hook
// --scheduled`); anything else runs as the console program would, with nothing shown.
return EvCharging.Cli.Run(args, new EvCharging.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
