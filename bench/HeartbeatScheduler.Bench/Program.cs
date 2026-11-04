// Quick-and-dirty harness (ns/op). Use BenchmarkDotNet for publishable numbers.
//   dotnet run -c Release --project bench/HeartbeatScheduler.Bench                     every core, workers spin then park
//   DOTNET_PROCESSOR_COUNT=6 dotnet run -c Release --project bench/HeartbeatScheduler.Bench -- --idle spin
//                                                                                     six cores for every contender, busy-wait
//   ... -- --levels [--idle spin]                                                      level-by-level DAG evaluation
//   ... [--quality high]                                                               the heartbeat workers at high thread quality
//   ... -- --clock <ns>                                                                the beat, clock slowed to <ns> (0: native)
var idle = args.Contains("spin") ? HeartbeatScheduling.HeartbeatIdlePolicy.BusySpin : HeartbeatScheduling.HeartbeatIdlePolicy.SpinThenPark;
var quality = args.Contains("high") ? HeartbeatScheduling.HeartbeatThreadQuality.High : HeartbeatScheduling.HeartbeatThreadQuality.Default;
if (Array.IndexOf(args, "--clock") is int c and >= 0) ClockBench.Run(double.Parse(args[c + 1]));
else if (args.Contains("--levels")) LevelBench.Run(idle, quality);
else HeartbeatBench.Run(idle, quality);
