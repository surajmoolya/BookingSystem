// Burst: concurrent load and correctness checker for the seat reservation service.
// Scenarios, the runner and the report are added in T-3.15 and M7 (lld §12).

const string Usage = """
    Usage: burst <BASE_URL> [options]

    Fires concurrent reservation traffic at a running service and checks the results.

    Options:
      --scenario <list>        hot,idem,conflict,limit,mixed,cancel (default: all)
      --hot-users <n>          distinct users hitting one seat (default: 500)
      --idem-requests <n>      concurrent same-key requests (default: 200)
      --limit-requests <n>     one user, distinct seats (default: 50)
      --mixed-requests <n>     total mixed-storm requests (default: 20000)
      --mixed-users <n>        (default: 5000)
      --mixed-seats <n>        (default: 1000)
      --max-connections <n>    MaxConnectionsPerServer (default: 1000)
      --http2                  use HTTP/2 multiplexing
      --timeout-seconds <n>    per-request timeout (default: 60)
      --key-in <mode>          header, body or mixed (default: mixed)
      --json <file>            write a machine-readable report
      -h, --help               show this help

    Exit code: 0 if every scenario passes, 1 otherwise.
    """;

if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

Console.Error.WriteLine("Scenarios are not implemented yet. Run with --help for usage.");
return 1;
