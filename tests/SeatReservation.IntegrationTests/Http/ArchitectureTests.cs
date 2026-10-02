using System.Reflection;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// Keeps the layering honest (D-081): the logic layer must not depend on HTTP, SQL, metrics or logging libraries.
/// Walks the full transitive assembly-reference closure, so an indirect dependency can't sneak in either.
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenInApplication =
    [
        "Npgsql", "Dapper", "Microsoft.AspNetCore", "prometheus-net", "Prometheus", "Serilog",
        "SeatReservation.Api", "SeatReservation.Infrastructure",
    ];

    private static Assembly Application => typeof(SeatReservation.Application.DependencyInjection).Assembly;

    private static Assembly Infrastructure => typeof(DataSources).Assembly;

    private static Assembly Api => typeof(Program).Assembly;

    [Fact]
    public void Application_does_not_depend_on_http_sql_metrics_logging_or_the_other_layers()
    {
        var closure = ReferenceClosure(Application);

        var violations = closure.Where(name => ForbiddenInApplication.Any(f => Matches(name, f))).ToList();

        Assert.True(violations.Count == 0, $"SeatReservation.Application must not reference: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Application_references_only_the_two_allowed_extension_packages()
    {
        var direct = Application.GetReferencedAssemblies().Select(a => a.Name!)
            .Where(n => n.StartsWith("Microsoft.", StringComparison.Ordinal) && n != "Microsoft.CSharp")
            .ToList();

        var unexpected = direct.Where(n => n is not ("Microsoft.Extensions.Logging.Abstractions" or "Microsoft.Extensions.Options"
            or "Microsoft.Extensions.DependencyInjection.Abstractions")).ToList();

        Assert.True(unexpected.Count == 0, $"Unexpected direct Microsoft.* references in Application: {string.Join(", ", unexpected)}");
    }

    [Fact]
    public void Infrastructure_depends_on_Application_but_not_on_the_controller_layer()
    {
        var closure = ReferenceClosure(Infrastructure);

        Assert.Contains("SeatReservation.Application", closure);
        Assert.DoesNotContain("SeatReservation.Api", closure);
    }

    [Fact]
    public void The_guard_can_actually_see_forbidden_references_it_is_looking_for()
    {
        // Negative controls: if the walker were broken it would report nothing for the layers that legitimately have these.
        Assert.Contains(ReferenceClosure(Infrastructure), n => Matches(n, "Npgsql"));
        Assert.Contains(ReferenceClosure(Api), n => Matches(n, "Microsoft.AspNetCore"));
    }

    private static bool Matches(string assemblyName, string forbidden) =>
        assemblyName.Equals(forbidden, StringComparison.OrdinalIgnoreCase)
        || assemblyName.StartsWith(forbidden + ".", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ReferenceClosure(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<Assembly>([root]);
        while (pending.Count > 0)
        {
            foreach (var reference in pending.Pop().GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                {
                    continue;
                }

                // The BCL and the shared framework can't pull in anything we forbid, except ASP.NET Core itself, which is
                // detected by name above; skip descending into System.* to keep the walk fast and the failure message short.
                if (reference.Name.StartsWith("System.", StringComparison.Ordinal) || reference.Name is "mscorlib" or "netstandard")
                {
                    continue;
                }

                try
                {
                    pending.Push(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // Reference-only assembly that isn't deployed; its name is already recorded.
                }
            }
        }

        return seen;
    }
}
