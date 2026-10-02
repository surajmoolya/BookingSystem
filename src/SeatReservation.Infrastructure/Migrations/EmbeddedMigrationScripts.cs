using System.Reflection;
using System.Text.RegularExpressions;

namespace SeatReservation.Infrastructure.Migrations;

/// <param name="Version">The script name without extension, e.g. <c>V001__init</c>; this is what <c>schema_migrations</c> stores.</param>
public sealed record MigrationScript(string Version, string Sql);

public static partial class EmbeddedMigrationScripts
{
    private const string Marker = ".Migrations.Scripts.";

    [GeneratedRegex(@"^V\d{3}__[A-Za-z0-9_]+$")]
    private static partial Regex VersionPattern();

    /// <summary>Loads every embedded <c>Vnnn__name.sql</c> in version order. A misnamed script is an error, not silently skipped.</summary>
    public static IReadOnlyList<MigrationScript> Load(Assembly assembly)
    {
        var scripts = new List<MigrationScript>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var markerAt = resource.IndexOf(Marker, StringComparison.Ordinal);
            if (markerAt < 0 || !resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = resource[(markerAt + Marker.Length)..^".sql".Length];
            if (!VersionPattern().IsMatch(version))
            {
                throw new InvalidOperationException($"Embedded migration '{resource}' must be named Vnnn__name.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            scripts.Add(new MigrationScript(version, reader.ReadToEnd()));
        }

        return scripts.OrderBy(s => s.Version, StringComparer.Ordinal).ToList();
    }
}
