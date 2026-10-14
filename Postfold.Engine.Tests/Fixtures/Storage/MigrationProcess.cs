using Postfold.Storage.Models;
using Postfold.Storage.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Fixtures.Storage;

internal static class MigrationProcess
{
    internal static string? Barrier { get; private set; }

    public static async Task<int> Main(string[] args)
    {
        if (args is not [string database, string barrier, string count] ||
            !Path.GetFullPath(database).Contains(Path.Combine("artifacts", "engine-tests"), StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        Barrier = barrier;
        bool measure = barrier == "measure-upgrade";
        string? original = measure ? Digest(database) : null;
        ProfileMigrationPlan plan = EvolutionContext.CreatePlan();
        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<string, object> stages = new Dictionary<string, object>();
        plan = plan with
        {
            Versions = plan.Versions.Take(int.Parse(count, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            Checkpoint = stage =>
            {
                if (measure)
                {
                    using Process process = Process.GetCurrentProcess();
                    stages[stage] = new
                    {
                        Milliseconds = watch.Elapsed.TotalMilliseconds,
                        PrivateBytes = process.PrivateMemorySize64
                    };
                }

                Signal(stage);
            }
        };
        using FileStream lease = await ProfileMigration.AdmitAsync(database, plan, CancellationToken.None);
        if (measure)
        {
            double milliseconds = watch.Elapsed.TotalMilliseconds;
            if (Digest(database) != original)
            {
                throw new InvalidDataException("Synthetic upgrade changed retained row values.");
            }

            string[] backups = Directory.GetDirectories(Path.GetDirectoryName(database)!, "*.before-migration-*");
            foreach (string backup in backups)
            {
                ProfileBackup.Validate(backup);
            }

            object evidence = new
            {
                MigrationMilliseconds = milliseconds,
                Stages = stages,
                MemoryScope = "Private-byte samples at exact admission checkpoints; not a continuous peak measurement",
                BackupBytes = backups.SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    .Sum(file => new FileInfo(file).Length),
                RowDigestPreserved = true,
                NetworkScope = "Storage-only fixture; no network or provider ports constructed"
            };
            File.WriteAllText(database + ".measurement.json", JsonSerializer.Serialize(evidence, new JsonSerializerOptions
            {
                WriteIndented = true
            }) + "\n");
            Console.WriteLine("Synthetic upgrade qualified in " + milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms.");
        }

        Signal("admitted");
        return 0;
    }

    internal static void Signal(string stage)
    {
        if (Barrier == stage)
        {
            Console.WriteLine(stage);
            Console.Out.Flush();
            Console.ReadLine();
        }
    }

    private static string Digest(string database)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProfileEvidence.Rows(database))));
    }
}
